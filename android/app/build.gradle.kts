import org.gradle.process.ExecOperations
import javax.inject.Inject

plugins {
    alias(libs.plugins.android.application)
}

val signingProps = listOf("voidstrap.storeFile", "voidstrap.storePassword", "voidstrap.keyAlias", "voidstrap.keyPassword")
val canSign = signingProps.all { providers.gradleProperty(it).isPresent }
val mainVersion = Regex("<VoidstrapVersion>([^<]+)</VoidstrapVersion>")
    .find(rootProject.file("../Directory.Build.props").readText())?.groupValues?.get(1)
    ?: error("VoidstrapVersion is missing")
val mainVersionCode = mainVersion.split('.').fold(0) { code, part -> code * 100 + part.toInt() }

android {
    namespace = "com.voidstrap.android"
    compileSdk = 37
    ndkVersion = "29.0.14206865"

    defaultConfig {
        applicationId = "com.voidstrap.android"
        minSdk = 24
        targetSdk = 36
        versionCode = mainVersionCode
        versionName = mainVersion
    }

    signingConfigs {
        if (canSign) {
            create("release") {
                storeFile = file(providers.gradleProperty("voidstrap.storeFile").get())
                storePassword = providers.gradleProperty("voidstrap.storePassword").get()
                keyAlias = providers.gradleProperty("voidstrap.keyAlias").get()
                keyPassword = providers.gradleProperty("voidstrap.keyPassword").get()
            }
        }
    }

    flavorDimensions += "channel"
    productFlavors {
        create("play") {
            dimension = "channel"
            buildConfigField("boolean", "DIRECT_UPDATES", "false")
        }
        create("direct") {
            dimension = "channel"
            applicationIdSuffix = ".direct"
            buildConfigField("boolean", "DIRECT_UPDATES", "true")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            if (canSign) signingConfig = signingConfigs.getByName("release")
        }
    }

    buildFeatures {
        buildConfig = true
        viewBinding = false
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
        isCoreLibraryDesugaringEnabled = true
    }

    lint {
        abortOnError = true
        warningsAsErrors = false
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }
}

abstract class CargoBuild : DefaultTask() {
    @get:InputFiles
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val sources: ConfigurableFileCollection

    @get:Input
    abstract val ndk: Property<String>

    @get:Input
    abstract val versionCode: Property<Int>

    @get:Input
    abstract val features: ListProperty<String>

    @get:Internal
    abstract val workDir: DirectoryProperty

    @get:Internal
    abstract val targetDir: DirectoryProperty

    @get:OutputDirectory
    abstract val output: DirectoryProperty

    @get:Inject
    abstract val execOps: ExecOperations

    @TaskAction
    fun build() {
        val abis = mapOf("arm64-v8a" to "aarch64-linux-android", "armeabi-v7a" to "armv7-linux-androideabi", "x86_64" to "x86_64-linux-android")
        val clangTargets = mapOf("aarch64-linux-android" to "aarch64-linux-android24", "armv7-linux-androideabi" to "armv7a-linux-androideabi24", "x86_64-linux-android" to "x86_64-linux-android24")
        val os = System.getProperty("os.name")
        val windows = os.startsWith("Windows")
        val host = if (windows) "windows-x86_64" else if (os.startsWith("Mac")) "darwin-x86_64" else "linux-x86_64"
        val exe = if (windows) ".exe" else ""
        val clang = File(ndk.get(), "toolchains/llvm/prebuilt/$host/bin/clang$exe")
        val cargo = File(System.getProperty("user.home"), ".cargo/bin/cargo$exe").takeIf { it.exists() }?.path ?: "cargo"
        val target = targetDir.get().asFile
        execOps.exec {
            workingDir = this@CargoBuild.workDir.get().asFile
            commandLine(listOf(cargo, "build", "--release", "--locked", "-p", "voidstrap_helper", "-p", "voidstrap_core") + features.get().flatMap { listOf("--features", it) } + abis.values.flatMap { listOf("--target", it) })
            environment("CARGO_TARGET_DIR", target.path)
            environment("VOIDSTRAP_VERSION_CODE", versionCode.get().toString())
            clangTargets.forEach { (triple, clangTarget) ->
                val key = triple.uppercase().replace('-', '_')
                environment("CARGO_TARGET_${key}_LINKER", clang.path)
                environment("CARGO_TARGET_${key}_RUSTFLAGS", "-C link-arg=--target=$clangTarget")
            }
        }
        val out = output.get().asFile
        out.deleteRecursively()
        abis.forEach { (abi, triple) ->
            File(target, "$triple/release/voidstrap_helper").copyTo(File(out, "$abi/libvoidstrap_helper.so"), overwrite = true)
            File(target, "$triple/release/libvoidstrap_core.so").copyTo(File(out, "$abi/libvoidstrap_core.so"), overwrite = true)
        }
    }
}

fun cargoTask(flavor: String, cargoFeatures: List<String>) = tasks.register<CargoBuild>("cargoBuild${flavor.replaceFirstChar { it.uppercase() }}") {
    val rust = rootProject.layout.projectDirectory.dir("rust")
    workDir.set(rust)
    sources.from(rust.file("Cargo.toml"), rust.file("Cargo.lock"), rust.dir("helper"), rust.file("core/Cargo.toml"), rust.dir("core/src"), rust.dir("vendor"))
    ndk.set(androidComponents.sdkComponents.sdkDirectory.map { it.dir("ndk/${android.ndkVersion}").asFile.path })
    versionCode.set(mainVersionCode)
    features.set(cargoFeatures)
    targetDir.set(layout.buildDirectory.dir("rust/target"))
    output.set(layout.buildDirectory.dir("rust/jniLibs-$flavor"))
}

val cargoBuildPlay = cargoTask("play", emptyList())
val cargoBuildDirect = cargoTask("direct", listOf("voidstrap_core/matchmaker", "voidstrap_core/updates"))
cargoBuildDirect.configure { mustRunAfter(cargoBuildPlay) }

androidComponents {
    onVariants { variant ->
        val cargo = if (variant.flavorName == "direct") cargoBuildDirect else cargoBuildPlay
        variant.sources.jniLibs?.addGeneratedSourceDirectory(cargo, CargoBuild::output)
    }
}

dependencies {
    implementation(libs.appcompat)
    implementation(libs.material)
    implementation(libs.core)
    implementation(libs.constraintlayout)
    "playImplementation"(libs.app.update)
    coreLibraryDesugaring(libs.desugar.jdk.libs.nio)
}
