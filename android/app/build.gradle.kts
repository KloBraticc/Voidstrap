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
}

dependencies {
    implementation(libs.appcompat)
    implementation(libs.material)
    implementation(libs.core)
    implementation(libs.constraintlayout)
    implementation(libs.junrar)
    implementation(libs.commons.compress)
    implementation(libs.xz)
    coreLibraryDesugaring(libs.desugar.jdk.libs.nio)
}
