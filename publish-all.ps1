[CmdletBinding()]
param(
    [string[]]$Only = @('all'),

    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',

    [switch]$NoClean,
    [switch]$Clean,
    [switch]$Parallel,
    [switch]$Sequential,
    [ValidateRange(1,32)]
    [int]$MaxParallel = [Math]::Min(5, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount / 2))),
    [switch]$BuildSolution,
    [switch]$SkipSolutionBuild,
    [switch]$AppImage,
    [switch]$SkipAppImage,
    [switch]$LinuxPackages,
    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Wait-BeforeClose {
    if ($NoPause -or $env:CI -or [Console]::IsInputRedirected) {
        return
    }
    try {
        Write-Host ''
        [void](Read-Host 'Press Enter to close this window')
    } catch {
    }
}

function Exit-Script {
    param([int]$Code)

    if (Get-Command Stop-AndroidBuild -ErrorAction SilentlyContinue) {
        Stop-AndroidBuild
    }
    if (Get-Command Remove-PublishManifests -ErrorAction SilentlyContinue) {
        Remove-PublishManifests
    }
    Wait-BeforeClose
    exit $Code
}

trap {
    $failure = $_
    Write-Host ''
    Write-Host 'BUILD SCRIPT CRASHED' -ForegroundColor Red
    Write-Host $failure.Exception.Message -ForegroundColor Red
    if ($failure.InvocationInfo -and $failure.InvocationInfo.PositionMessage) {
        Write-Host $failure.InvocationInfo.PositionMessage -ForegroundColor DarkGray
    }
    if ($failure.ScriptStackTrace) {
        Write-Host $failure.ScriptStackTrace -ForegroundColor DarkGray
    }
    Exit-Script 1
}

$validOnly = @('windows', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'linux-musl-arm64', 'osx-x64', 'osx-arm64', 'android', 'all')
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
$invalidOnly = @($Only | Where-Object { $validOnly -notcontains $_ })
if ($Only.Count -eq 0 -or $invalidOnly.Count -gt 0) {
    throw "Unknown -Only value: $($invalidOnly -join ', '). Use one or more of: $($validOnly -join ', ')"
}

$IsWindowsHost = $env:OS -eq 'Windows_NT'
$PathComparison = if ($IsWindowsHost) {
    [System.StringComparison]::OrdinalIgnoreCase
} else {
    [System.StringComparison]::Ordinal
}

$Root      = [System.IO.Path]::GetFullPath($PSScriptRoot)
$Out       = [System.IO.Path]::GetFullPath((Join-Path $Root 'PublishedBuilds'))
$Staging   = Join-Path $Out '.staging'
$LinuxOut  = Join-Path $Out 'Linux'
$MacOut    = Join-Path $Out 'macOS'
$ArtifactDir = [System.IO.Path]::GetFullPath((Join-Path $Root 'artifacts/publish'))
$WinProj   = [System.IO.Path]::GetFullPath((Join-Path $Root 'src/Voidstrap.App/Voidstrap.csproj'))
$CrossProj = [System.IO.Path]::GetFullPath((Join-Path $Root 'src/Voidstrap.Cross/Voidstrap.Cross.csproj'))
$Sln       = [System.IO.Path]::GetFullPath((Join-Path $Root 'Voidstrap.sln'))
$IsLinuxHost = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)
$LinuxPackagingTools = @('dpkg-deb', 'rpmbuild', 'flatpak', 'flatpak-builder')

if ($Parallel -and $Sequential) {
    throw 'Parallel and Sequential cannot be selected together.'
}

if ($Clean -and $NoClean) {
    throw 'Clean and NoClean cannot be selected together.'
}

if ($BuildSolution -and $SkipSolutionBuild) {
    throw 'BuildSolution and SkipSolutionBuild cannot be selected together.'
}

if ($AppImage -and $SkipAppImage) {
    throw 'AppImage and SkipAppImage cannot be selected together.'
}

function Assert-BuildDirectory {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$ExpectedName
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $parent = [System.IO.Path]::GetDirectoryName($fullPath)
    $name = [System.IO.Path]::GetFileName($fullPath)

    if (-not [string]::Equals($parent, $Root, $PathComparison) -or
        -not [string]::Equals($name, $ExpectedName, $PathComparison)) {
        throw "Refusing to use unsafe build directory: $fullPath"
    }
}

function Stop-ProcessesUsingPath {
    param([Parameter(Mandatory)] [string[]]$Path)

    $prefixes = @($Path | ForEach-Object { $_.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar })
    $stopped = 0

    foreach ($proc in (Get-Process -ErrorAction SilentlyContinue)) {
        $exe = $null
        try { $exe = $proc.Path } catch { $exe = $null }
        if ([string]::IsNullOrWhiteSpace($exe)) { continue }
        $owner = $prefixes | Where-Object { $exe.StartsWith($_, $PathComparison) } | Select-Object -First 1
        if (-not $owner) { continue }

        Write-Host "  Stopping $($proc.ProcessName) (pid $($proc.Id)) which is running from $owner" -ForegroundColor DarkYellow
        try {
            $proc.Kill()
            $null = $proc.WaitForExit(5000)
            $stopped++
        } catch {
        }
    }

    if ($stopped -gt 0) {
        Start-Sleep -Milliseconds 500
    }
}

function Remove-BuildDirectory {
    param([Parameter(Mandatory)] [string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $safe = $false
    foreach ($buildRoot in @($Out, $ArtifactDir)) {
        $prefix = $buildRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if ([string]::Equals($fullPath, $buildRoot, $PathComparison) -or $fullPath.StartsWith($prefix, $PathComparison)) {
            $safe = $true
            break
        }
    }
    if (-not $safe) {
        throw "Refusing to remove a path outside the build directories: $fullPath"
    }
    $ancestor = [System.IO.Path]::GetDirectoryName($fullPath)
    while ($ancestor -and -not [string]::Equals($ancestor, $Root, $PathComparison)) {
        $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to remove a path through a linked directory: $fullPath"
        }
        $ancestor = [System.IO.Path]::GetDirectoryName($ancestor)
    }
    $Path = $fullPath

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $item = Get-Item -LiteralPath $Path -Force

            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                Remove-Item -LiteralPath $Path -Force
            } else {
                Remove-Item -LiteralPath $Path -Recurse -Force
            }
            return
        }
        catch {
            if ($attempt -eq 3) {
                throw
            }
            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}

function Assert-FileExists {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not found: $Path"
    }
}

function Resolve-AndroidJdk {
    $javaLeaf = if ($IsWindowsHost) { 'bin/java.exe' } else { 'bin/java' }
    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) { $candidates.Add($env:JAVA_HOME) }

    $jdksRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.jdks'
    if (Test-Path -LiteralPath $jdksRoot -PathType Container) {
        Get-ChildItem -LiteralPath $jdksRoot -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { $candidates.Add($_.FullName) }
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $java = Join-Path $candidate $javaLeaf
        if (Test-Path -LiteralPath $java -PathType Leaf) { return [System.IO.Path]::GetFullPath($candidate) }
    }
    throw 'A Java 17 JDK is required for the Android build. Set JAVA_HOME to it, or install one under ~/.jdks.'
}

function Resolve-AndroidSdk {
    $candidates = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT)
    if ($IsWindowsHost) {
        $candidates += (Join-Path $env:LOCALAPPDATA 'Android/Sdk')
    } else {
        $candidates += (Join-Path $env:HOME 'Android/Sdk')
    }
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (Test-Path -LiteralPath (Join-Path $candidate 'platform-tools') -PathType Container) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    throw 'The Android SDK is required for the Android build. Set ANDROID_HOME to it.'
}

function Resolve-ApkSigner {
    param([Parameter(Mandatory)] [string]$SdkRoot)

    $buildTools = Join-Path $SdkRoot 'build-tools'
    if (-not (Test-Path -LiteralPath $buildTools -PathType Container)) {
        throw "The Android SDK has no build-tools folder: $buildTools"
    }
    $name = if ($IsWindowsHost) { 'apksigner.bat' } else { 'apksigner' }
    $found = Get-ChildItem -LiteralPath $buildTools -Directory |
        Sort-Object Name |
        ForEach-Object { Join-Path $_.FullName $name } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -Last 1
    if (-not $found) {
        throw 'apksigner was not found in the Android SDK build-tools.'
    }
    return $found
}

function Assert-ApkIsReleaseSigned {
    param(
        [Parameter(Mandatory)] [string]$ApkSigner,
        [Parameter(Mandatory)] [string]$ApkPath,
        [Parameter(Mandatory)] [string]$Label
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $ApkSigner 'verify' '--verbose' '--print-certs' $ApkPath 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0 -or $output -notmatch '(?m)^Verifies\s*$') {
        throw "$Label is not correctly signed. apksigner said: $($output.Trim())"
    }
    if ($output -match 'CN=Android Debug') {
        throw "$Label is signed with the Android debug certificate and must never be published."
    }
}

function Get-AndroidGradlePropertiesPath {
    $gradleHome = if ([string]::IsNullOrWhiteSpace($env:GRADLE_USER_HOME)) {
        Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.gradle'
    } else {
        $env:GRADLE_USER_HOME
    }
    return (Join-Path $gradleHome 'gradle.properties')
}

function Get-MissingAndroidSigningProperties {
    param([Parameter(Mandatory)] [string]$GradlePropertiesPath)

    $signingProperties = @(
        'voidstrap.storeFile',
        'voidstrap.storePassword',
        'voidstrap.keyAlias',
        'voidstrap.keyPassword'
    )
    $declared = if (Test-Path -LiteralPath $GradlePropertiesPath -PathType Leaf) {
        Get-Content -LiteralPath $GradlePropertiesPath
    } else {
        @()
    }
    return @($signingProperties | Where-Object { $name = $_; -not ($declared | Where-Object { $_ -match ('^\s*' + [regex]::Escape($name) + '\s*=') }) })
}

function Test-AndroidToolchain {
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'android') -PathType Container)) { return $false }
    try {
        $null = Resolve-AndroidJdk
        $sdk = Resolve-AndroidSdk
        $null = Resolve-ApkSigner $sdk
    } catch {
        return $false
    }
    return @(Get-MissingAndroidSigningProperties (Get-AndroidGradlePropertiesPath)).Count -eq 0
}

$script:AndroidBuild = $null

function Start-AndroidBuild {
    $androidRoot = Join-Path $Root 'android'
    if (-not (Test-Path -LiteralPath $androidRoot -PathType Container)) {
        throw "The Android project folder is missing: $androidRoot"
    }

    $gradlew = Join-Path $androidRoot $(if ($IsWindowsHost) { 'gradlew.bat' } else { 'gradlew' })
    Assert-FileExists $gradlew 'The Gradle wrapper'

    $jdk = Resolve-AndroidJdk
    $sdk = Resolve-AndroidSdk
    $apkSigner = Resolve-ApkSigner $sdk

    $gradleProperties = Get-AndroidGradlePropertiesPath
    $missing = @(Get-MissingAndroidSigningProperties $gradleProperties)
    if ($missing.Count -gt 0) {
        throw @"
The Android release keystore is not configured, so the build would produce
unsigned APKs that cannot be installed.

Add these to $gradleProperties :
  $($missing -join "`n  ")
"@
    }

    [System.IO.Directory]::CreateDirectory($ArtifactDir) | Out-Null
    $log = Join-Path $ArtifactDir 'android.log'
    $errorLog = Join-Path $ArtifactDir 'android.errors.log'
    $gradleArgs = @('assemblePlayRelease', 'assembleDirectRelease', '--console=plain', '--build-cache')
    Write-Host '  Starting Android...' -ForegroundColor DarkGray
    $previousJavaHome = $env:JAVA_HOME
    $previousAndroidHome = $env:ANDROID_HOME
    try {
        $env:JAVA_HOME = $jdk
        $env:ANDROID_HOME = $sdk
        if ($IsWindowsHost) {
            $process = Start-Process -FilePath $gradlew -ArgumentList $gradleArgs -WorkingDirectory $androidRoot -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errorLog
        } else {
            $process = Start-Process -FilePath 'sh' -ArgumentList (@($gradlew) + $gradleArgs) -WorkingDirectory $androidRoot -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errorLog
        }
        $null = $process.Handle
    } finally {
        $env:JAVA_HOME = $previousJavaHome
        $env:ANDROID_HOME = $previousAndroidHome
    }

    $script:AndroidBuild = [pscustomobject]@{
        Process          = $process
        StartTime        = Get-Date
        Root             = $androidRoot
        ApkSigner        = $apkSigner
        GradleProperties = $gradleProperties
        Log              = $log
        ErrorLog         = $errorLog
    }
}

function Stop-AndroidBuild {
    $build = $script:AndroidBuild
    if (-not $build -or $build.Process.HasExited) {
        return
    }
    if ($IsWindowsHost) {
        & taskkill.exe /T /F /PID $build.Process.Id 2>$null | Out-Null
    } else {
        try { $build.Process.Kill() } catch { }
    }
}

function Complete-AndroidPublish {
    param([Parameter(Mandatory)] [string]$Version)

    $build = $script:AndroidBuild
    while (-not $build.Process.WaitForExit(1000)) {
        Write-Heartbeat
    }
    $elapsed = [int](New-TimeSpan -Start $build.StartTime).TotalSeconds
    if ($build.Process.ExitCode -ne 0) {
        Write-Host "  Android: Failed (${elapsed}s)" -ForegroundColor Red
        foreach ($logPath in @($build.ErrorLog, $build.Log)) {
            if (Test-Path -LiteralPath $logPath -PathType Leaf) {
                Get-Content -LiteralPath $logPath -Tail 25 | ForEach-Object { Write-Host "    $_" }
            }
        }
        throw "The Android build failed with exit code $($build.Process.ExitCode). Full log: $($build.Log)"
    }
    Write-Host "  Android: Done (${elapsed}s)" -ForegroundColor Green

    $androidRoot = $build.Root
    $apkSigner = $build.ApkSigner
    $gradleProperties = $build.GradleProperties
    $androidOutput = Join-Path $Out 'Android'
    Reset-OutputDirectory $androidOutput

    foreach ($flavour in @('play', 'direct')) {
        $built = Join-Path $androidRoot "app/build/outputs/apk/$flavour/release/app-$flavour-release.apk"
        $unsigned = Join-Path $androidRoot "app/build/outputs/apk/$flavour/release/app-$flavour-release-unsigned.apk"
        if ((-not (Test-Path -LiteralPath $built -PathType Leaf)) -and (Test-Path -LiteralPath $unsigned -PathType Leaf)) {
            throw "The $flavour build produced an unsigned APK. Check the keystore properties in $gradleProperties."
        }
        Assert-FileExists $built "The $flavour release APK"
        Assert-ApkIsReleaseSigned $apkSigner $built "The $flavour release APK"
        if ((Get-Item -LiteralPath $built).Length -lt 1048576) {
            throw "The $flavour release APK is unexpectedly small."
        }
        $published = Join-Path $androidOutput "Voidstrap-Android-$flavour-$Version.apk"
        Copy-Item -LiteralPath $built -Destination $published -Force
        Write-Host "  $([System.IO.Path]::GetFileName($published))" -ForegroundColor Green
    }

    Assert-AndroidFlavourSplit (Join-Path $androidOutput "Voidstrap-Android-play-$Version.apk")
}

function Assert-AndroidFlavourSplit {
    param([Parameter(Mandatory)] [string]$PlayApk)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PlayApk)
    try {
        $manifestEntry = $archive.GetEntry('AndroidManifest.xml')
        if (-not $manifestEntry) {
            throw 'The play manifest was not found.'
        }
        $reader = New-Object System.IO.StreamReader($manifestEntry.Open(), [System.Text.Encoding]::Unicode)
        try {
            $manifestText = $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }
        if ($manifestText -match 'REQUEST_INSTALL_PACKAGES') {
            throw 'The play APK asks for REQUEST_INSTALL_PACKAGES, which only the direct flavour may declare.'
        }

        $dexEntries = @($archive.Entries | Where-Object { $_.FullName -match '^classes\d*\.dex$' })
        foreach ($dex in $dexEntries) {
            $stream = $dex.Open()
            try {
                $buffer = New-Object byte[] $dex.Length
                $offset = 0
                while ($offset -lt $buffer.Length) {
                    $read = $stream.Read($buffer, $offset, $buffer.Length - $offset)
                    if ($read -le 0) { break }
                    $offset += $read
                }
            } finally {
                $stream.Dispose()
            }
            $dexText = [System.Text.Encoding]::ASCII.GetString($buffer)
            foreach ($term in @('Matchmaker', 'RobloxLogin', 'SignInActivity', 'Reroute', 'ROBLOSECURITY')) {
                if ($dexText.Contains($term)) {
                    throw "The play APK contains $term in $($dex.Name), which belongs to the direct flavour only."
                }
            }
        }
    } finally {
        $archive.Dispose()
    }
}

$script:ManifestsRemoved = $false

function Remove-PublishManifests {
    if ($script:ManifestsRemoved) {
        return
    }
    $script:ManifestsRemoved = $true
    Get-ChildItem -LiteralPath $Root -Recurse -File -Force -Filter 'PublishOutputs.*.txt' -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Resolve-AppImageTool {
    $provided = $env:APPIMAGETOOL
    if (-not [string]::IsNullOrWhiteSpace($provided) -and (Test-Path -LiteralPath $provided -PathType Leaf)) {
        return [System.IO.Path]::GetFullPath($provided)
    }

    $command = Get-Command appimagetool -ErrorAction SilentlyContinue
    if ($command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return [System.IO.Path]::GetFullPath($command.Source)
    }

    $toolVersion = '1.9.1'
    $toolArchitecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        ([System.Runtime.InteropServices.Architecture]::X64) { 'x86_64' }
        ([System.Runtime.InteropServices.Architecture]::Arm64) { 'aarch64' }
        default { throw 'Automatic AppImage tooling supports x64 and arm64 Linux hosts.' }
    }
    $expectedHash = switch ($toolArchitecture) {
        'x86_64' { 'ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0' }
        'aarch64' { 'f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158' }
    }
    $cacheBase = if (-not [string]::IsNullOrWhiteSpace($env:XDG_CACHE_HOME)) {
        $env:XDG_CACHE_HOME
    } else {
        Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.cache'
    }
    $toolDirectory = Join-Path $cacheBase "voidstrap/build-tools/appimagetool/$toolVersion"
    $toolPath = Join-Path $toolDirectory "appimagetool-$toolArchitecture.AppImage"
    $toolValid = (Test-Path -LiteralPath $toolPath -PathType Leaf) -and
        [string]::Equals((Get-FileHash -LiteralPath $toolPath -Algorithm SHA256).Hash, $expectedHash, [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $toolValid) {
        [System.IO.Directory]::CreateDirectory($toolDirectory) | Out-Null
        $downloadPath = "$toolPath.download.$PID"
        $downloadUrl = "https://github.com/AppImage/appimagetool/releases/download/$toolVersion/appimagetool-$toolArchitecture.AppImage"
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
            Invoke-WebRequest -Uri $downloadUrl -OutFile $downloadPath -UseBasicParsing -TimeoutSec 120
            $actualHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
            if (-not [string]::Equals($actualHash, $expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'The downloaded AppImage packaging tool failed verification.'
            }
            Move-Item -LiteralPath $downloadPath -Destination $toolPath -Force
        }
        finally {
            if (Test-Path -LiteralPath $downloadPath) {
                Remove-Item -LiteralPath $downloadPath -Force
            }
        }
    }
    if (-not $IsWindowsHost) {
        & chmod 0755 $toolPath
        if ($LASTEXITCODE -ne 0) {
            throw 'The AppImage packaging tool could not be made executable.'
        }
    }
    return $toolPath
}

function Resolve-GitBashPath {
    $candidates = [System.Collections.Generic.List[string]]::new()
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git -and -not [string]::IsNullOrWhiteSpace($git.Source)) {
        $gitRoot = Split-Path -Parent (Split-Path -Parent $git.Source)
        if (-not [string]::IsNullOrWhiteSpace($gitRoot)) {
            $candidates.Add((Join-Path $gitRoot 'bin/bash.exe'))
            $candidates.Add((Join-Path $gitRoot 'usr/bin/bash.exe'))
        }
    }
    foreach ($base in @($env:ProgramFiles, $env:ProgramW6432, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if ([string]::IsNullOrWhiteSpace($base)) { continue }
        $candidates.Add((Join-Path $base 'Git/bin/bash.exe'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs/Git/bin/bash.exe'))
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [System.IO.Path]::GetFullPath($candidate) }
    }

    $system32 = if ([string]::IsNullOrWhiteSpace($env:WINDIR)) { '' } else { [System.IO.Path]::GetFullPath((Join-Path $env:WINDIR 'System32')) }
    foreach ($cmd in @(Get-Command bash -All -ErrorAction SilentlyContinue)) {
        $src = $cmd.Source
        if ([string]::IsNullOrWhiteSpace($src)) { continue }
        if ($system32 -and $src.StartsWith($system32, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($src -match '[\\/]WindowsApps[\\/]') { continue }
        if (Test-Path -LiteralPath $src -PathType Leaf) { return [System.IO.Path]::GetFullPath($src) }
    }
    return $null
}

function Invoke-NativeWithTimeout {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)] [int]$TimeoutSeconds
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (($Arguments | ForEach-Object { ConvertTo-CommandLineArgument ([string]$_) }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $null = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            return $null
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = [string]$output.GetAwaiter().GetResult() }
    } finally {
        $process.Dispose()
    }
}

function New-PackagingShell {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Command,
        [string[]]$Prefix = @(),
        [switch]$Linux,
        [switch]$Wsl
    )

    $tools = @()
    if ($Linux) {
        $probe = 'for t in ' + ($LinuxPackagingTools -join ' ') + '; do command -v $t >/dev/null 2>&1 && echo $t; done; true'
        $result = Invoke-NativeWithTimeout -FilePath $Command -Arguments (@($Prefix) + @('-c', $probe)) -TimeoutSeconds 30
        if (-not $result) {
            Write-Host "  $Name did not answer within 30 seconds, skipping it." -ForegroundColor DarkYellow
            return $null
        }
        if ($result.ExitCode -ne 0) { return $null }
        $tools = @(($result.Output -split '\r?\n') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }

    return [pscustomobject]@{
        Name    = $Name
        Command = $Command
        Prefix  = @($Prefix)
        Linux   = [bool]$Linux
        Wsl     = [bool]$Wsl
        Tools   = $tools
    }
}

function Resolve-PackagingShell {
    if (-not $IsWindowsHost) {
        $bash = Get-Command bash -ErrorAction SilentlyContinue
        if (-not $bash) { return $null }
        return New-PackagingShell -Name 'bash' -Command $bash.Source -Linux:$IsLinuxHost
    }

    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if ($wsl) {
        Write-Host 'Checking WSL for Linux packaging...' -ForegroundColor DarkGray
        $listed = Invoke-NativeWithTimeout -FilePath $wsl.Source -Arguments @('--list', '--quiet') -TimeoutSeconds 15
        if (-not $listed) {
            Write-Host '  WSL did not answer within 15 seconds. Run "wsl --shutdown" and try again to get Linux packages.' -ForegroundColor DarkYellow
            $PackageNotes.Add('WSL did not answer. Run "wsl --shutdown", then publish again to build the Linux packages.')
        }
        elseif ($listed.ExitCode -eq 0) {
            $distros = @(($listed.Output.Replace([string][char]0, '') -split '\r?\n') |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_ -and $_ -notmatch '^docker-desktop' })
            $fallback = $null
            foreach ($distro in $distros) {
                $shell = New-PackagingShell -Name "WSL $distro" -Command $wsl.Source -Prefix @('-d', $distro, '-e', 'bash') -Linux -Wsl
                if (-not $shell) { continue }
                if (@($LinuxPackagingTools | Where-Object { $shell.Tools -notcontains $_ }).Count -eq 0) { return $shell }
                if (-not $fallback) { $fallback = $shell }
            }
            if ($fallback) { return $fallback }
        }
    }

    $gitBash = Resolve-GitBashPath
    if ($gitBash) {
        return New-PackagingShell -Name 'Git Bash' -Command $gitBash
    }
    return $null
}

function Get-RootRelativePath {
    param([Parameter(Mandatory)] [string]$Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    $prefix = $Root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, $PathComparison)) {
        throw "Refusing to package a path outside the repository: $full"
    }
    return $full.Substring($prefix.Length).Replace('\', '/')
}

function Reset-OutputDirectory {
    param([Parameter(Mandatory)] [string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-BuildDirectory $Path
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Repair-FutureTimestamps {
    $now = Get-Date
    $limit = $now.AddMinutes(1)
    $past = $now.AddHours(-2)
    $outputs = 0
    $sources = 0
    foreach ($name in @('src', 'external', 'artifacts')) {
        $dir = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $files = $null
        try {
            $files = [System.IO.Directory]::EnumerateFiles($dir, '*', [System.IO.SearchOption]::AllDirectories)
            foreach ($file in $files) {
                if ([System.IO.File]::GetLastWriteTime($file) -le $limit) { continue }
                $isOutput = $name -eq 'artifacts' -or $file -match '[\\/](bin|obj)([-_][^\\/]*)?[\\/]'
                try {
                    if ($isOutput) {
                        [System.IO.File]::SetLastWriteTime($file, $past)
                        $outputs++
                    } else {
                        [System.IO.File]::SetLastWriteTime($file, $now)
                        $sources++
                    }
                } catch {
                }
            }
        } catch {
            Write-Host "  Could not check file dates under ${name}: $($_.Exception.Message)" -ForegroundColor DarkYellow
        }
    }
    if ($outputs -gt 0 -or $sources -gt 0) {
        Write-Host "  The system clock ran ahead (often after booting Linux): reset $outputs build files and $sources source files dated in the future so every page and resource rebuilds." -ForegroundColor DarkYellow
    }
}

function Get-PackageFiles {
    param([Parameter(Mandatory)] $Job)

    $rid = $Job.Target.Rid
    $directory = if ($rid.StartsWith('osx-', [System.StringComparison]::Ordinal)) { $MacOut } else { $LinuxOut }
    $tokens = switch ($rid) {
        'linux-x64' { @('linux-x64', '_amd64.deb', '_x86_64.') }
        'linux-arm64' { @('linux-arm64', '_arm64.deb', '_aarch64.') }
        default { @($rid) }
    }
    return @(Get-ChildItem -LiteralPath $directory -File -ErrorAction SilentlyContinue | Where-Object {
        $name = $_.Name
        @($tokens | Where-Object { $name.Contains($_) }).Count -gt 0
    })
}

function Test-PackageCache {
    param([Parameter(Mandatory)] $Job)

    if ($Clean -or $env:MACOS_SIGN_IDENTITY -or $env:MACOS_NOTARY_PROFILE -or $env:VOIDSTRAP_APPIMAGE_SYSROOT -or $env:VOIDSTRAP_APPIMAGE_SYSROOT_DIR) {
        return $false
    }
    $stampPath = Join-Path (Join-Path $ArtifactDir $Job.Target.Key) 'packages.json'
    if (-not (Test-Path -LiteralPath $stampPath -PathType Leaf)) { return $false }
    try {
        $stamp = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json
        if ($stamp.InputHash -ne $Job.PackageInputHash -or @($stamp.Files).Count -eq 0) { return $false }
        $files = @(Get-PackageFiles $Job)
        foreach ($expected in $stamp.Files) {
            $file = $files | Where-Object { $_.Name -eq $expected.Name } | Select-Object -First 1
            if (-not $file -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $expected.Hash) { return $false }
        }
        return $true
    } catch {
        return $false
    }
}

function Save-PackageCache {
    param([Parameter(Mandatory)] $Job)

    $files = @(Get-PackageFiles $Job | ForEach-Object {
        [pscustomobject]@{ Name = $_.Name; Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    if ($files.Count -eq 0) { return }
    $stamp = [pscustomobject]@{ InputHash = $Job.PackageInputHash; Files = $files }
    $stamp | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path (Join-Path $ArtifactDir $Job.Target.Key) 'packages.json') -Encoding UTF8
}

function Invoke-PackagingScript {
    param(
        [Parameter(Mandatory)] $Shell,
        [Parameter(Mandatory)] [string]$Label,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [switch]$BestEffort
    )

    Write-Host "Packaging $Label..." -ForegroundColor Cyan
    $allArgs = @($Shell.Prefix) + $Arguments
    Push-Location -LiteralPath $Root
    try {
        & $Shell.Command @allArgs | Out-Host
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    if ($exitCode -eq 0) {
        return
    }
    $message = "$Label packaging failed with exit code $exitCode."
    if (-not $BestEffort) {
        throw $message
    }
    Write-Host "  $message" -ForegroundColor DarkYellow
    $PackageNotes.Add($message)
}

function Test-TransientPublishFailure {
    param([Parameter(Mandatory)] $Job)

    $text = [string]$Job.CapturedOutput
    return $text -match '(?i)NU1301|NU1302|timed out|temporar(?:y|ily)|connection (?:reset|closed)|resource (?:busy|unavailable)|text file busy|being used by another process|MSB3021|MSB3026|MSB3027|IOException'
}

function ConvertTo-CommandLineArgument {
    param([AllowEmptyString()] [string]$Argument)

    if ($null -eq $Argument -or $Argument.Length -eq 0) {
        return '""'
    }

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $slashes = 0

    foreach ($ch in $Argument.ToCharArray()) {
        if ($ch -eq '\') {
            $slashes++
            continue
        }

        if ($ch -eq '"') {
            if ($slashes -gt 0) {
                [void]$builder.Append(('\' * ($slashes * 2)))
            }
            [void]$builder.Append('\"')
            $slashes = 0
            continue
        }

        if ($slashes -gt 0) {
            [void]$builder.Append(('\' * $slashes))
            $slashes = 0
        }

        [void]$builder.Append($ch)
    }

    if ($slashes -gt 0) {
        [void]$builder.Append(('\' * ($slashes * 2)))
    }

    [void]$builder.Append('"')
    return $builder.ToString()
}

function Start-DotNetProcess {
    param(
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$WorkingDirectory
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:DotNetPath
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    if ($psi.PSObject.Properties.Name -contains 'ArgumentList') {
        foreach ($arg in $Arguments) {
            [void]$psi.ArgumentList.Add([string]$arg)
        }
    } else {
        $psi.Arguments = (($Arguments | ForEach-Object {
            ConvertTo-CommandLineArgument ([string]$_)
        }) -join ' ')
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    if (-not $process.Start()) {
        $process.Dispose()
        throw 'Failed to start dotnet.'
    }

    return [pscustomobject]@{
        Process    = $process
        StdOutTask = $process.StandardOutput.ReadToEndAsync()
        StdErrTask = $process.StandardError.ReadToEndAsync()
    }
}

function Finish-PublishJob {
    param([Parameter(Mandatory)] $Job)

    if ($Job.Status -ne 'Building') {
        return
    }

    while (-not $Job.Process.WaitForExit(1000)) {
        Write-Heartbeat @($Job)
    }
    $Job.Process.WaitForExit()

    $Job.StdOut = [string]$Job.StdOutTask.GetAwaiter().GetResult()
    $Job.StdErr = [string]$Job.StdErrTask.GetAwaiter().GetResult()
    $Job.CapturedOutput = $Job.StdOut + [Environment]::NewLine + $Job.StdErr

    $Job.EndTime = Get-Date
    $exitCode = $Job.Process.ExitCode

    if ($exitCode -ne 0) {
        $Job.Failure = "dotnet publish exited with code $exitCode"
        $Job.Status = 'Failed'
    }
    elseif (-not (Test-Path -LiteralPath $Job.Expected -PathType Leaf)) {
        $Job.Failure = "Publish completed, but the expected executable was not created: $($Job.Expected)"
        $Job.Status = 'Failed'
    }
    else {
        $Job.Status = 'Done'
    }

    $elapsed = [int](New-TimeSpan -Start $Job.StartTime -End $Job.EndTime).TotalSeconds
    $color = if ($Job.Status -eq 'Done') { 'Green' } else { 'Red' }
    Write-Host "  $($Job.Target.Name): $($Job.Status) (${elapsed}s)" -ForegroundColor $color
}

function Restart-PublishJob {
    param([Parameter(Mandatory)] $Job)

    try { $Job.Process.Dispose() } catch { }
    Reset-OutputDirectory $Job.Target.OutDir
    $started = Start-DotNetProcess -Arguments $Job.Arguments -WorkingDirectory $Root
    $Job.Retried = $true
    $Job.Process = $started.Process
    $Job.StdOutTask = $started.StdOutTask
    $Job.StdErrTask = $started.StdErrTask
    $Job.StartTime = Get-Date
    $Job.EndTime = $null
    $Job.Failure = $null
    $Job.CapturedOutput = ''
    $Job.StdOut = ''
    $Job.StdErr = ''
    $Job.Status = 'Building'
}

function Show-PublishFailure {
    param([Parameter(Mandatory)] $Job)

    Write-Host "$($Job.Target.Name) errors:" -ForegroundColor Red

    if ($Job.Failure) {
        Write-Host $Job.Failure
    }

    $errorLines = @()

    if (-not [string]::IsNullOrWhiteSpace($Job.StdErr)) {
        $errorLines += @(($Job.StdErr -split '\r?\n') | Select-Object -Last 30)
    }

    if (-not [string]::IsNullOrWhiteSpace($Job.StdOut)) {
        $errorLines += @(($Job.StdOut -split '\r?\n' | Select-Object -Last 80) |
            Where-Object { $_ -match '(?i)\berror\b|NETSDK\d+|MSB\d+' })
    }

    if ($errorLines.Count -gt 0) {
        $errorLines | Select-Object -Unique | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host 'No additional error text was written by dotnet.' -ForegroundColor DarkGray
    }

    Write-Host ''
}

function Get-VoidstrapVersion {
    $props = Join-Path $Root 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $props -PathType Leaf)) {
        throw "Directory.Build.props was not found: $props"
    }
    $match = [regex]::Match((Get-Content -Raw -LiteralPath $props), '<VoidstrapVersion>([^<]+)</VoidstrapVersion>')
    if (-not $match.Success) {
        throw 'The Voidstrap package version is unavailable.'
    }
    return $match.Groups[1].Value
}

$script:HeartbeatClock = [System.Diagnostics.Stopwatch]::StartNew()

function Write-Heartbeat {
    param([object[]]$Jobs = @())

    if ($script:HeartbeatClock.Elapsed.TotalSeconds -lt 15) {
        return
    }
    $script:HeartbeatClock.Restart()
    $running = @($Jobs | Where-Object { $_.Status -eq 'Building' } | ForEach-Object {
        "$($_.Target.Name) $([int](New-TimeSpan -Start $_.StartTime).TotalSeconds)s"
    })
    $android = $script:AndroidBuild
    if ($android -and -not $android.Process.HasExited) {
        $running += "Android $([int](New-TimeSpan -Start $android.StartTime).TotalSeconds)s"
    }
    if ($running.Count -gt 0) {
        Write-Host "  Still building: $($running -join ', ')" -ForegroundColor DarkGray
    }
}

function Wait-ForPublishJobs {
    param(
        [Parameter(Mandatory)] [object[]]$Jobs,
        [int]$PollMilliseconds = 250,
        [switch]$WaitForSlot
    )

    while (@($Jobs | Where-Object { $_.Status -eq 'Building' }).Count -gt 0) {
        foreach ($job in $Jobs) {
            if ($job.Status -eq 'Building' -and $job.Process.HasExited) {
                Finish-PublishJob $job
            }
        }
        if ($WaitForSlot -and @($Jobs | Where-Object { $_.Status -eq 'Building' }).Count -lt $MaxParallel) {
            return
        }
        if (@($Jobs | Where-Object { $_.Status -eq 'Building' }).Count -gt 0) {
            Write-Heartbeat $Jobs
            Start-Sleep -Milliseconds $PollMilliseconds
        }
    }
}

function Write-BuildOutcome {
    param(
        [Parameter(Mandatory)] [string]$Title,
        [Parameter(Mandatory)] [string]$Color,
        [string]$Detail,
        [switch]$IncludeArtifacts
    )

    Write-Host $Title -ForegroundColor $Color
    if ($Detail) {
        Write-Host $Detail -ForegroundColor $Color
    }
    Write-Host "Output: $Out"
    if ($IncludeArtifacts) {
        Write-Host "Artifacts: $ArtifactDir"
    }
    Write-Host "Time:   $($sw.Elapsed.ToString('mm\:ss'))"
}

Assert-BuildDirectory $Out 'PublishedBuilds'

[System.IO.Directory]::CreateDirectory((Split-Path -Parent $ArtifactDir)) | Out-Null
$publishLock = $null
try {
    $publishLock = [System.IO.File]::Open((Join-Path (Split-Path -Parent $ArtifactDir) 'publish.lock'), [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
} catch {
    throw 'Another publish is already running for this repository, or its build directory is inaccessible.'
}
$originalDotnetRoot = $env:DOTNET_ROOT
$originalPath = $env:PATH
$originalAppImageTool = $env:APPIMAGETOOL
$originalWslEnv = $env:WSLENV
try {

$requiredSdkVersion = $null
$globalJsonPath = Join-Path $Root 'global.json'
if (Test-Path -LiteralPath $globalJsonPath -PathType Leaf) {
    try {
        $requiredSdkVersion = (Get-Content -Raw -LiteralPath $globalJsonPath | ConvertFrom-Json).sdk.version
    } catch {
        $requiredSdkVersion = $null
    }
}

function Test-DotNetSatisfiesSdk {
    param(
        [Parameter(Mandatory)][string]$Candidate
    )
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    Push-Location -LiteralPath $Root
    try {
        $selected = & $Candidate --version 2>$null
        $exitCode = $LASTEXITCODE
    } catch {
        return $false
    } finally {
        Pop-Location
        $ErrorActionPreference = $previousPreference
    }
    $script:DotNetVersion = ([string]$selected).Trim()
    return $exitCode -eq 0 -and $script:DotNetVersion -match '^\d+\.\d+\.\d+(?:-.+)?$'
}

$dotnetCandidates = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
    $dotnetCandidates.Add((Join-Path $env:DOTNET_ROOT $(if ($IsWindowsHost) { 'dotnet.exe' } else { 'dotnet' })))
}
$homeDirectory = if ($IsWindowsHost) { $env:USERPROFILE } else { $env:HOME }
if (-not [string]::IsNullOrWhiteSpace($homeDirectory)) {
    $dotnetCandidates.Add((Join-Path (Join-Path $homeDirectory '.dotnet') $(if ($IsWindowsHost) { 'dotnet.exe' } else { 'dotnet' })))
}
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCommand) {
    $dotnetCandidates.Add($dotnetCommand.Source)
}
foreach ($fallback in @('/usr/lib/dotnet/dotnet', '/usr/share/dotnet/dotnet', '/usr/local/share/dotnet/dotnet')) {
    $dotnetCandidates.Add($fallback)
}

$dotnetPath = $null
$dotnetComparer = if ($IsWindowsHost) { [System.StringComparer]::OrdinalIgnoreCase } else { [System.StringComparer]::Ordinal }
$seenDotNet = [System.Collections.Generic.HashSet[string]]::new($dotnetComparer)
foreach ($candidate in $dotnetCandidates) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $resolved = [System.IO.Path]::GetFullPath($candidate)
    if (-not $seenDotNet.Add($resolved)) { continue }
    if (Test-DotNetSatisfiesSdk -Candidate $resolved) {
        $dotnetPath = $resolved
        break
    }
}
if (-not $dotnetPath) {
    if ([string]::IsNullOrWhiteSpace($requiredSdkVersion)) {
        throw 'The .NET SDK was not found. Install the .NET SDK and make sure dotnet is in PATH.'
    }
    throw "The required .NET SDK $requiredSdkVersion was not found."
}

$dotnetDirectory = Split-Path -Parent $dotnetPath
$env:DOTNET_ROOT = $dotnetDirectory
$pathSeparator = [System.IO.Path]::PathSeparator
if (($env:PATH -split [regex]::Escape($pathSeparator)) -notcontains $dotnetDirectory) {
    $env:PATH = $dotnetDirectory + $pathSeparator + $env:PATH
}
$script:DotNetPath = $dotnetPath

Write-Host 'Voidstrap Build' -ForegroundColor Cyan
Write-Host "Root:   $Root"
Write-Host "Output: $Out"
Write-Host "SDK:    $script:DotNetVersion"
Write-Host ''

Assert-FileExists $Sln 'Solution'
Assert-FileExists $WinProj 'Windows project'
Assert-FileExists $CrossProj 'Cross platform project'

$PubOpts = @(
    '-p:PublishSingleFile=true',
    '-p:UseAppHost=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-p:TreatWarningsAsErrors=true',
    '-p:ContinuousIntegrationBuild=true',
    '-p:RestoreUseStaticGraphEvaluation=true',
    '-nr:false',
    '-clp:ErrorsOnly'
)

$AllTargets = @(
    [pscustomobject]@{ Name='Windows-x64';       Key='windows';          Kind='windows'; Rid='win-x64';          OutDir=(Join-Path $Out 'Windows');          Executable='Voidstrap.exe' }
    [pscustomobject]@{ Name='Linux-x64';         Key='linux-x64';        Kind='cross';   Rid='linux-x64';        OutDir=(Join-Path $Staging 'Linux-x64');    Executable='Voidstrap' }
    [pscustomobject]@{ Name='Linux-arm64';       Key='linux-arm64';      Kind='cross';   Rid='linux-arm64';      OutDir=(Join-Path $Staging 'Linux-arm64');  Executable='Voidstrap' }
    [pscustomobject]@{ Name='Linux-musl-x64';    Key='linux-musl-x64';   Kind='cross';   Rid='linux-musl-x64';   OutDir=(Join-Path $Staging 'Linux-musl-x64'); Executable='Voidstrap' }
    [pscustomobject]@{ Name='Linux-musl-arm64';  Key='linux-musl-arm64'; Kind='cross';   Rid='linux-musl-arm64'; OutDir=(Join-Path $Staging 'Linux-musl-arm64'); Executable='Voidstrap' }
    [pscustomobject]@{ Name='macOS-x64';         Key='osx-x64';          Kind='cross';   Rid='osx-x64';          OutDir=(Join-Path $Staging 'macOS-x64');    Executable='Voidstrap' }
    [pscustomobject]@{ Name='macOS-arm64';       Key='osx-arm64';        Kind='cross';   Rid='osx-arm64';        OutDir=(Join-Path $Staging 'macOS-arm64');  Executable='Voidstrap' }
)

$RequestedAll = $Only -contains 'all'

if ($RequestedAll) {
    $Targets = @($AllTargets)
} else {
    $Targets = @($AllTargets | Where-Object { $Only -contains $_.Key })
}

$AndroidExplicit = $Only -contains 'android'
$BuildAndroid = $AndroidExplicit -or $RequestedAll
$PackageNotes = [System.Collections.Generic.List[string]]::new()

if ($Targets.Count -eq 0 -and -not $BuildAndroid) {
    throw "Nothing matches -Only $($Only -join ', ')"
}

$SkippedHostTargets = @()
if (-not $IsWindowsHost -and @($Targets | Where-Object { $_.Key -eq 'windows' }).Count -gt 0) {
    $SkippedHostTargets = @($Targets | Where-Object { $_.Key -eq 'windows' })
    $Targets = @($Targets | Where-Object { $_.Key -ne 'windows' })

    if ($Targets.Count -eq 0 -and -not $BuildAndroid) {
        throw @"
The Windows Voidstrap project cannot be built on this host.
Microsoft.Windows.CsWinRT invokes cswinrt.exe during the WPF build, and that
Windows executable cannot run natively on Linux/macOS (exit code 126).

Build the Windows target from Windows, or run this script for a Linux/macOS target.
"@
    }
}

$appImageTargets = @($Targets | Where-Object { $_.Rid -in @('linux-x64', 'linux-arm64') })
if ($AppImage -and -not $IsLinuxHost -and -not $IsWindowsHost) {
    throw 'AppImage packaging requires a Linux host, or Windows with a WSL distro.'
}
if ($AppImage -and $appImageTargets.Count -eq 0) {
    throw 'AppImage packaging requires a linux x64 or linux arm64 target.'
}
$ShouldBuildAppImage = ($IsLinuxHost -or $AppImage) -and -not $SkipAppImage -and $appImageTargets.Count -gt 0
$selectedLinuxTargets = @($Targets | Where-Object { $_.Rid.StartsWith('linux-', [System.StringComparison]::Ordinal) })
$glibcLinuxTargets = @($selectedLinuxTargets | Where-Object { -not $_.Rid.StartsWith('linux-musl-', [System.StringComparison]::Ordinal) })
$wantLinuxPackages = $LinuxPackages -or ($selectedLinuxTargets.Count -gt 0 -and -not $AppImage)
if ($LinuxPackages -and $selectedLinuxTargets.Count -eq 0) {
    throw 'Select at least one Linux target when requesting Linux packages.'
}
$packagingShell = $null
if ($wantLinuxPackages -or $ShouldBuildAppImage -or @($Targets | Where-Object { $_.Rid.StartsWith('osx-', [System.StringComparison]::Ordinal) }).Count -gt 0) {
    $packagingShell = Resolve-PackagingShell
}
if ($wantLinuxPackages) {
    $packageRequirement = $null
    if (-not $packagingShell) {
        $packageRequirement = 'Linux packages require bash on this host.'
    } elseif ($glibcLinuxTargets.Count -gt 0 -and -not $packagingShell.Linux) {
        $packageRequirement = 'Linux packages require a Linux host, or Windows with a WSL distro.'
    } elseif ($glibcLinuxTargets.Count -gt 0) {
        $missingTools = @($LinuxPackagingTools | Where-Object { $packagingShell.Tools -notcontains $_ })
        if ($missingTools.Count -gt 0) {
            $packageRequirement = "Linux packaging needs these tools in $($packagingShell.Name): $($missingTools -join ', ')."
        }
    }
    if ($packageRequirement) {
        if ($LinuxPackages) { throw $packageRequirement }
        $PackageNotes.Add($packageRequirement + ' Plain Linux executables will be published instead.')
        $wantLinuxPackages = $false
    }
}
if ($ShouldBuildAppImage -and -not ($packagingShell -and $packagingShell.Linux)) {
    throw 'AppImage packaging requires a Linux host, or Windows with a WSL distro.'
}
$appImageTool = $null
if ($packagingShell -and $packagingShell.Linux -and -not $SkipAppImage -and $appImageTargets.Count -gt 0 -and ($wantLinuxPackages -or $ShouldBuildAppImage)) {
    $appImageTool = Resolve-AppImageTool
}
$UseParallel = -not $Sequential -and ($Parallel -or $Targets.Count -gt 1)
if ($UseParallel) {
    $PubOpts += '-m:1'
} else {
    $PubOpts += '-m'
}

if ($SkippedHostTargets.Count -gt 0) {
    foreach ($skipped in $SkippedHostTargets) {
        Write-Host "Skipping $($skipped.Name): Windows WPF/CsWinRT publishing requires a Windows host." -ForegroundColor DarkYellow
    }
    Write-Host ''
}

if ($Clean -and $RequestedAll -and (Test-Path -LiteralPath $Out)) {
    Write-Host 'Cleaning previous published output...' -ForegroundColor DarkGray
    Stop-ProcessesUsingPath $Out
    Remove-BuildDirectory $Out
}
New-Item -ItemType Directory -Path $Out -Force | Out-Null

if ($Targets.Count -gt 0) {
    if ($Clean) {
        foreach ($target in $Targets) {
            Remove-BuildDirectory (Join-Path $ArtifactDir $target.Key)
        }
    }
    New-Item -ItemType Directory -Path $ArtifactDir -Force | Out-Null
}

if ($BuildSolution -and $Targets.Count -gt 0) {
    if ($IsWindowsHost) {
        Write-Host "[1/2] Building solution ($Configuration)..." -ForegroundColor Cyan

        $buildArgs = @(
            'build', $Sln,
            '-c', $Configuration,
            '-clp:ErrorsOnly'
        )

        & $script:DotNetPath @buildArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Solution build failed with exit code $LASTEXITCODE."
        }

        Write-Host '    done' -ForegroundColor Green
        Write-Host ''
    } else {
        Write-Host '[1/2] Full solution prebuild skipped: this host is not Windows.' -ForegroundColor DarkYellow
        Write-Host '      The solution contains a WPF/CsWinRT project that requires Windows.' -ForegroundColor DarkGray
        Write-Host '      Each selected cross platform publish will restore and build what it needs.' -ForegroundColor DarkGray
        Write-Host ''
    }
}

$buildNames = @($Targets | ForEach-Object { $_.Name })
if ($BuildAndroid) { $buildNames += 'Android' }
if ($UseParallel) {
    Write-Host "Building $($buildNames -join ', '), up to $MaxParallel at once" -ForegroundColor Cyan
} else {
    Write-Host "Building $($buildNames -join ', ')" -ForegroundColor Cyan
}
$plannedPackages = [System.Collections.Generic.List[string]]::new()
if (@($Targets | Where-Object { $_.Kind -eq 'windows' }).Count -gt 0) { $plannedPackages.Add('Windows: Voidstrap.exe') }
if ($selectedLinuxTargets.Count -gt 0) {
    if ($wantLinuxPackages) {
        $plannedFormats = @('tar.gz')
        if ($glibcLinuxTargets.Count -gt 0) {
            $plannedFormats += 'deb', 'rpm'
            if ($appImageTool) { $plannedFormats += 'AppImage' }
            $plannedFormats += 'Flatpak'
        }
        if ($glibcLinuxTargets.Count -eq 2) { $plannedFormats += 'AUR metadata' }
        $plannedPackages.Add("Linux: $($plannedFormats -join ', ')")
    } elseif ($ShouldBuildAppImage) {
        $plannedPackages.Add('Linux: AppImage')
    } else {
        $plannedPackages.Add('Linux: plain executables')
    }
}
if (@($Targets | Where-Object { $_.Rid.StartsWith('osx-', [System.StringComparison]::Ordinal) }).Count -gt 0) {
    $plannedPackages.Add($(if ($packagingShell) { 'macOS: Voidstrap.app archives' } else { 'macOS: plain executables' }))
}
if ($BuildAndroid) { $plannedPackages.Add('Android: play and direct APKs') }
Write-Host 'Packages:' -ForegroundColor Cyan
foreach ($planned in $plannedPackages) {
    Write-Host "  $planned" -ForegroundColor Cyan
}
foreach ($note in $PackageNotes) {
    Write-Host "  Skipping: $note" -ForegroundColor DarkYellow
}
Write-Host ''

Repair-FutureTimestamps

$jobs = @()
$unexpectedFailure = $null

try {
    if ($BuildAndroid) {
        if ($AndroidExplicit -or (Test-AndroidToolchain)) {
            Start-AndroidBuild
        } else {
            Write-Host '  Skipping Android APKs: no Android JDK, SDK, or signing keystore is configured on this host.' -ForegroundColor DarkYellow
            $PackageNotes.Add('Android APKs: configure the JDK, SDK and signing keystore, or run with -Only android to see the exact error.')
        }
    }
    if ($Targets.Count -gt 0) {
        Stop-ProcessesUsingPath @($Targets | ForEach-Object { $_.OutDir })
    }
    $script:HeartbeatClock.Restart()
    foreach ($t in $Targets) {
        if ($UseParallel -and @($jobs | Where-Object { $_.Status -eq 'Building' }).Count -ge $MaxParallel) {
            Wait-ForPublishJobs -Jobs $jobs -WaitForSlot
        }
        Reset-OutputDirectory $t.OutDir

        $targetArtifacts = Join-Path $ArtifactDir $t.Key

        if ($t.Kind -eq 'windows') {
            $argList = @(
                'publish', $WinProj,
                '-c', $Configuration,
                '-r', $t.Rid,
                '-o', $t.OutDir,
                '--artifacts-path', $targetArtifacts,
                '--self-contained', 'false',
                '-p:PublishProfile=FolderProfile',
                '-p:PublishReadyToRun=false',
                '-p:IncludeAllContentForSelfExtract=false',
                '-p:EnableWindowsTargeting=true'
            ) + $PubOpts
        } else {
            $argList = @(
                'publish', $CrossProj,
                '-c', $Configuration,
                '-r', $t.Rid,
                '-o', $t.OutDir,
                '--artifacts-path', $targetArtifacts,
                "-p:VoidstrapLinuxPackagingRoot=$targetArtifacts/",
                '--self-contained', 'true',
                '-p:IncludeAllContentForSelfExtract=true',
                '-p:EnableCompressionInSingleFile=true'
            ) + $PubOpts
        }

        Write-Host "  Starting $($t.Name)..." -ForegroundColor DarkGray
        $started = Start-DotNetProcess -Arguments $argList -WorkingDirectory $Root

        $job = [pscustomobject]@{
            Target     = $t
            Process    = $started.Process
            StdOutTask = $started.StdOutTask
            StdErrTask = $started.StdErrTask
            Expected   = Join-Path $t.OutDir $t.Executable
            Arguments  = $argList
            StartTime  = Get-Date
            EndTime    = $null
            Failure    = $null
            CapturedOutput = ''
            StdOut     = ''
            StdErr     = ''
            Status     = 'Building'
            Retried    = $false
        }

        $jobs += $job

        if (-not $UseParallel) {
            Finish-PublishJob $job

            if ($job.Status -eq 'Failed' -and -not $job.Retried -and (Test-TransientPublishFailure $job)) {
                Write-Host "  $($t.Name) failed, retrying once..." -ForegroundColor DarkYellow
                Restart-PublishJob $job
                Finish-PublishJob $job
            }
        }
    }

    if ($UseParallel) {
        Wait-ForPublishJobs -Jobs $jobs
        $retryJobs = @($jobs | Where-Object { $_.Status -eq 'Failed' -and -not $_.Retried -and (Test-TransientPublishFailure $_) })
        foreach ($job in $retryJobs) {
            if (@($jobs | Where-Object { $_.Status -eq 'Building' }).Count -ge $MaxParallel) {
                Wait-ForPublishJobs -Jobs $jobs -WaitForSlot
            }
            Write-Host "  $($job.Target.Name) failed, retrying once..." -ForegroundColor DarkYellow
            Restart-PublishJob $job
        }
        Wait-ForPublishJobs -Jobs $jobs
    }
}
catch {
    $unexpectedFailure = $_
}
finally {
    foreach ($job in $jobs) {
        if ($job.Status -eq 'Building' -and -not $job.Process.HasExited) {
            try {
                if ($job.Process.PSObject.Methods.Name -contains 'Kill') {
                    try {
                        $job.Process.Kill($true)
                    } catch {
                        $job.Process.Kill()
                    }
                }
            } catch {
            }
        }
    }
    foreach ($job in $jobs) {
        try {
            if ($job.Status -eq 'Building') {
                $job.Process.WaitForExit()
                $null = $job.StdOutTask.GetAwaiter().GetResult()
                $null = $job.StdErrTask.GetAwaiter().GetResult()
            }
        } catch { }
        try { $job.Process.Dispose() } catch { }
    }
}

if ($unexpectedFailure) {
    $sw.Stop()
    Write-Host ''
    Write-BuildOutcome -Title 'BUILD SCRIPT FAILED' -Color Red -Detail $unexpectedFailure.Exception.Message
    Exit-Script 1
}

Write-Host ''
$Failed = @($jobs | Where-Object { $_.Status -eq 'Failed' })

foreach ($job in $Failed) {
    Show-PublishFailure $job
}

Get-ChildItem -LiteralPath $Out -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

if ($Failed.Count -gt 0) {
    $sw.Stop()
    Write-Host ''
    Write-BuildOutcome -Title "FAILED: $(($Failed | ForEach-Object { $_.Target.Name }) -join ', ')" -Color Red -IncludeArtifacts
    Exit-Script 1
}

$packageFailure = $null
try {
    if ($script:AndroidBuild) {
        Complete-AndroidPublish (Get-VoidstrapVersion)
    }

    $linuxJobs = @($jobs | Where-Object { $_.Status -eq 'Done' -and $_.Target.Rid.StartsWith('linux-', [System.StringComparison]::Ordinal) })
    $macJobs = @($jobs | Where-Object { $_.Status -eq 'Done' -and $_.Target.Rid.StartsWith('osx-', [System.StringComparison]::Ordinal) })
    $wantAppImageOnly = $ShouldBuildAppImage -and -not $wantLinuxPackages
    $shell = $packagingShell
    if ($shell) {
        Write-Host "Packaging with $($shell.Name)" -ForegroundColor DarkGray
    }

    $linuxScript = 'build/Packaging/Linux/package.sh'
    $flatpakScript = 'build/Packaging/Flatpak/package.sh'
    $aurScript = 'build/Packaging/Arch/pkgbuild.sh'
    $macScript = 'build/Packaging/MacOS/package.sh'
    $packageVersion = if ($shell) { Get-VoidstrapVersion } else { $null }
    $initialPackageNoteCount = $PackageNotes.Count

    $packageSources = @(Get-ChildItem -LiteralPath (Join-Path $Root 'build/Packaging') -Recurse -File |
        Sort-Object FullName | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
    foreach ($sourcePath in @('publish-all.ps1', 'Directory.Build.props', 'LICENSE.VOIDSTRAP', 'src/Voidstrap.App/Voidstrap.png')) {
        $packageSources += (Get-FileHash -LiteralPath (Join-Path $Root $sourcePath) -Algorithm SHA256).Hash
    }
    if ($appImageTool) { $packageSources += (Get-FileHash -LiteralPath $appImageTool -Algorithm SHA256).Hash }
    $shellName = if ($shell) { $shell.Name } else { '' }
    $packageSources += "$wantLinuxPackages,$wantAppImageOnly,$shellName,$IsWindowsHost,$($env:VOIDSTRAP_APPIMAGE_GLIBC_CEILING),$($env:SOURCE_DATE_EPOCH),$($env:MACOS_DEPLOYMENT_TARGET)"
    foreach ($job in @($linuxJobs) + @($macJobs)) {
        $inputText = ($packageSources + (Get-FileHash -LiteralPath $job.Expected -Algorithm SHA256).Hash) -join "`n"
        $hasher = [System.Security.Cryptography.SHA256]::Create()
        try {
            $inputHash = [BitConverter]::ToString($hasher.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($inputText))).Replace('-', '')
        } finally {
            $hasher.Dispose()
        }
        $job | Add-Member -NotePropertyName PackageInputHash -NotePropertyValue $inputHash
        $cached = Test-PackageCache $job
        $job | Add-Member -NotePropertyName PackagesCached -NotePropertyValue $cached
        if ($cached) {
            Write-Host "  $($job.Target.Name): verified existing packages." -ForegroundColor Green
        } else {
            Get-PackageFiles $job | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        }
    }
    $linuxPackageJobs = @($linuxJobs | Where-Object { -not $_.PackagesCached })
    $macPackageJobs = @($macJobs | Where-Object { -not $_.PackagesCached })

    if ($appImageTool) {
        $env:APPIMAGETOOL = $appImageTool
        if ($shell.Wsl -and $env:WSLENV -notmatch '(^|:)APPIMAGETOOL/') {
            $env:WSLENV = (@($env:WSLENV, 'APPIMAGETOOL/p') | Where-Object { $_ }) -join ':'
        }
    }

    if ($wantLinuxPackages) {
        $packageOutput = $LinuxOut
        New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
        $packageOutputPath = Get-RootRelativePath $packageOutput
        foreach ($job in $linuxPackageJobs) {
            $rid = $job.Target.Rid
            $executablePath = Get-RootRelativePath $job.Expected
            $glibc = -not $rid.StartsWith('linux-musl-', [System.StringComparison]::Ordinal)
            $formats = @('tar')
            if ($glibc) {
                $formats += 'deb', 'rpm'
                if ($appImageTool) { $formats += 'appimage' }
            }
            foreach ($format in $formats) {
                Invoke-PackagingScript $shell "$($job.Target.Name) $format" @($linuxScript, $rid, $packageVersion, $format, $packageOutputPath, $executablePath)
            }
            if ($glibc) {
                Invoke-PackagingScript $shell "$($job.Target.Name) Flatpak" @($flatpakScript, $rid, $packageVersion, $packageOutputPath, $executablePath)
            }
        }

        $x64Archive = Join-Path $packageOutput "Voidstrap_${packageVersion}_linux-x64.tar.gz"
        $arm64Archive = Join-Path $packageOutput "Voidstrap_${packageVersion}_linux-arm64.tar.gz"
        $aurMetadata = Join-Path $packageOutput 'Voidstrap_AUR_metadata.tar.gz'
        if ((Test-Path -LiteralPath $x64Archive -PathType Leaf) -and (Test-Path -LiteralPath $arm64Archive -PathType Leaf) -and ($linuxPackageJobs.Count -gt 0 -or -not (Test-Path -LiteralPath $aurMetadata -PathType Leaf))) {
            if (Test-Path -LiteralPath $aurMetadata -PathType Leaf) { Remove-Item -LiteralPath $aurMetadata -Force }
            $aurStage = Join-Path $Staging 'AUR'
            Reset-OutputDirectory $aurStage
            $aurArgs = @($aurScript, $packageVersion, (Get-RootRelativePath $aurStage), (Get-RootRelativePath $x64Archive), (Get-RootRelativePath $arm64Archive), (Get-RootRelativePath (Join-Path $packageOutput 'Voidstrap_AUR_metadata.tar.gz')))
            Invoke-PackagingScript $shell 'AUR metadata' $aurArgs
        }
    }
    elseif ($wantAppImageOnly) {
        $appImageOutput = $LinuxOut
        New-Item -ItemType Directory -Path $appImageOutput -Force | Out-Null
        foreach ($job in @($linuxPackageJobs | Where-Object { $_.Target.Rid -in @('linux-x64', 'linux-arm64') })) {
            Invoke-PackagingScript $shell "$($job.Target.Name) AppImage" @($linuxScript, $job.Target.Rid, $packageVersion, 'appimage', (Get-RootRelativePath $appImageOutput), (Get-RootRelativePath $job.Expected))
            $packageArchitecture = if ($job.Target.Rid -eq 'linux-x64') { 'x86_64' } else { 'aarch64' }
            $appImagePath = Join-Path $appImageOutput "Voidstrap_${packageVersion}_${packageArchitecture}.AppImage"
            Assert-FileExists $appImagePath 'AppImage output'
            Assert-FileExists ($appImagePath + '.zsync') 'AppImage update information'
            if ((Get-Item -LiteralPath $appImagePath).Length -lt 1048576) {
                throw 'The AppImage output is unexpectedly small.'
            }
        }
    }

    if ($macPackageJobs.Count -gt 0) {
        if (-not $shell) {
            $PackageNotes.Add('macOS app bundles were skipped: they need bash, which Git for Windows provides.')
        } else {
            $macOutput = $MacOut
            New-Item -ItemType Directory -Path $macOutput -Force | Out-Null
            foreach ($job in $macPackageJobs) {
                $macArgs = @($macScript, $job.Target.Rid, $packageVersion, (Get-RootRelativePath $macOutput), (Get-RootRelativePath $job.Expected))
                Invoke-PackagingScript $shell "$($job.Target.Name) app" $macArgs -BestEffort
            }
        }
    }

    foreach ($job in @($linuxPackageJobs) + @($macPackageJobs)) {
        $platformOutput = if ($job.Target.Rid.StartsWith('osx-', [System.StringComparison]::Ordinal)) { $MacOut } else { $LinuxOut }
        New-Item -ItemType Directory -Path $platformOutput -Force | Out-Null
        $tokens = switch ($job.Target.Rid) {
            'linux-x64' { @('linux-x64', '_x86_64.') }
            'linux-arm64' { @('linux-arm64', '_aarch64.') }
            default { @($job.Target.Rid) }
        }
        $packaged = @(Get-ChildItem -LiteralPath $platformOutput -File | Where-Object { $name = $_.Name; @($tokens | Where-Object { $name.Contains($_) }).Count -gt 0 })
        if ($packaged.Count -eq 0) {
            Copy-Item -LiteralPath $job.Expected -Destination (Join-Path $platformOutput "Voidstrap_$(Get-VoidstrapVersion)_$($job.Target.Rid)") -Force
            $PackageNotes.Add("$($job.Target.Name): no package could be built, so the plain executable was copied instead.")
        }
        if ($packaged.Count -gt 0 -and $PackageNotes.Count -eq $initialPackageNoteCount) {
            Save-PackageCache $job
        }
    }
}
catch {
    $packageFailure = $_
}
finally {
    if (-not $packageFailure -and -not $NoClean -and (Test-Path -LiteralPath $Staging)) {
        try {
            Remove-BuildDirectory $Staging
        } catch {
            Write-Host "Warning: could not remove the staging folder: $($_.Exception.Message)" -ForegroundColor DarkYellow
        }
    }
}

$sw.Stop()
Write-Host ''
if ($PackageNotes.Count -gt 0) {
    Write-Host 'Not published on this host:' -ForegroundColor DarkYellow
    foreach ($note in $PackageNotes) {
        Write-Host "  $note" -ForegroundColor DarkYellow
    }
    Write-Host ''
}
if ($packageFailure) {
    Write-BuildOutcome -Title 'PACKAGE CREATION FAILED' -Color Red -Detail $packageFailure.Exception.Message -IncludeArtifacts
    Exit-Script 1
}

if ($PackageNotes.Count -gt 0) {
    Write-Host 'Published everything this host can build.' -ForegroundColor Green
} else {
    Write-Host 'All builds published successfully.' -ForegroundColor Green
}
Write-Host "Output: $Out"
Write-Host "Time:   $($sw.Elapsed.ToString('mm\:ss'))"
Write-Host ''
$runStarted = (Get-Date).AddSeconds(-$sw.Elapsed.TotalSeconds - 5)
foreach ($folder in @(Get-ChildItem -LiteralPath $Out -Directory -ErrorAction SilentlyContinue | Where-Object { -not $_.Name.StartsWith('.') } | Sort-Object Name)) {
    $files = @(Get-ChildItem -LiteralPath $folder.FullName -File -ErrorAction SilentlyContinue | Where-Object { -not $_.Name.StartsWith('.') } | Sort-Object Name)
    if ($files.Count -eq 0) { continue }
    Write-Host "  $($folder.Name)" -ForegroundColor Cyan
    foreach ($file in $files) {
        $size = if ($file.Length -ge 1MB) { '{0:0.0} MB' -f ($file.Length / 1MB) } else { '{0:0} KB' -f [Math]::Max(1, $file.Length / 1KB) }
        $line = '    {0,-48} {1,9}' -f $file.Name, $size
        if ($file.LastWriteTime -ge $runStarted) {
            Write-Host $line
        } else {
            Write-Host "$line  (from an earlier run)" -ForegroundColor DarkGray
        }
    }
}

Exit-Script 0
} finally {
    Remove-PublishManifests
    $env:DOTNET_ROOT = $originalDotnetRoot
    $env:PATH = $originalPath
    $env:APPIMAGETOOL = $originalAppImageTool
    $env:WSLENV = $originalWslEnv
    $publishLock.Dispose()
}
