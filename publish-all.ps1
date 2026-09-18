[CmdletBinding()]
param(
    [ValidateSet('windows','linux-x64','linux-arm64','linux-musl-x64','linux-musl-arm64','osx-x64','osx-arm64','all')]
    [string[]]$Only = @('all'),

    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',

    [switch]$NoClean,
    [switch]$Parallel,
    [switch]$Sequential,
    [switch]$SkipSolutionBuild,
    [switch]$AppImage,
    [switch]$SkipAppImage,
    [switch]$LinuxPackages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$IsWindowsHost = $env:OS -eq 'Windows_NT'
$PathComparison = if ($IsWindowsHost) {
    [System.StringComparison]::OrdinalIgnoreCase
} else {
    [System.StringComparison]::Ordinal
}

$Root      = [System.IO.Path]::GetFullPath($PSScriptRoot)
$Out       = [System.IO.Path]::GetFullPath((Join-Path $Root 'PublishedBuilds'))
$LogDir    = [System.IO.Path]::GetFullPath((Join-Path $Root '.buildlogs'))
$ArtifactDir = [System.IO.Path]::GetFullPath((Join-Path $Root '.publish-artifacts'))
$WinProj   = [System.IO.Path]::GetFullPath((Join-Path $Root 'src/Voidstrap.App/Voidstrap.csproj'))
$CrossProj = [System.IO.Path]::GetFullPath((Join-Path $Root 'src/Voidstrap.Cross/Voidstrap.Cross.csproj'))
$Sln       = [System.IO.Path]::GetFullPath((Join-Path $Root 'Voidstrap.sln'))
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$IsLinuxHost = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)

if ($Parallel -and $Sequential) {
    throw 'Parallel and Sequential cannot be selected together.'
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
    param([Parameter(Mandatory)] [string]$Path)

    $normalized = $Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $stopped = 0

    foreach ($proc in (Get-Process -ErrorAction SilentlyContinue)) {
        $exe = $null
        try { $exe = $proc.Path } catch { $exe = $null }
        if ([string]::IsNullOrWhiteSpace($exe)) { continue }
        if (-not $exe.StartsWith($normalized, $PathComparison)) { continue }

        Write-Host "  Stopping $($proc.ProcessName) (pid $($proc.Id)) which is running from the output folder" -ForegroundColor DarkYellow
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

function Remove-PublishManifests {
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
            Invoke-WebRequest -Uri $downloadUrl -OutFile $downloadPath -UseBasicParsing
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
    & chmod 0755 $toolPath
    if ($LASTEXITCODE -ne 0) {
        throw 'The AppImage packaging tool could not be made executable.'
    }
    return $toolPath
}

function Test-TransientPublishFailure {
    param([Parameter(Mandatory)] $Job)

    $text = [string]$Job.CapturedOutput
    return $text -match '(?i)NU1301|NU1302|timed out|temporar(?:y|ily)|connection (?:reset|closed)|resource (?:busy|unavailable)|text file busy|being used by another process|MSB3021|MSB3026|MSB3027|IOException'
}

# Used only on Windows PowerShell/.NET Framework, where
# ProcessStartInfo.ArgumentList is unavailable.
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

    $Job.Process.WaitForExit()

    $stdout = $Job.StdOutTask.GetAwaiter().GetResult()
    $stderr = $Job.StdErrTask.GetAwaiter().GetResult()
    $Job.CapturedOutput = [string]$stdout + [Environment]::NewLine + [string]$stderr

    [System.IO.File]::WriteAllText($Job.Log, [string]$stdout, $Utf8NoBom)
    [System.IO.File]::WriteAllText($Job.ErrLog, [string]$stderr, $Utf8NoBom)

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

    if (Test-Path -LiteralPath $Job.Log -PathType Leaf) {
        Move-Item -LiteralPath $Job.Log -Destination ($Job.Log + '.attempt1') -Force
    }
    if (Test-Path -LiteralPath $Job.ErrLog -PathType Leaf) {
        Move-Item -LiteralPath $Job.ErrLog -Destination ($Job.ErrLog + '.attempt1') -Force
    }
    try { $Job.Process.Dispose() } catch { }
    if (Test-Path -LiteralPath $Job.Target.OutDir) {
        Remove-BuildDirectory $Job.Target.OutDir
    }
    New-Item -ItemType Directory -Path $Job.Target.OutDir -Force | Out-Null
    $started = Start-DotNetProcess -Arguments $Job.Arguments -WorkingDirectory $Root
    $Job.Retried = $true
    $Job.Process = $started.Process
    $Job.StdOutTask = $started.StdOutTask
    $Job.StdErrTask = $started.StdErrTask
    $Job.StartTime = Get-Date
    $Job.EndTime = $null
    $Job.Failure = $null
    $Job.CapturedOutput = ''
    $Job.Status = 'Building'
}

function Show-PublishFailure {
    param([Parameter(Mandatory)] $Job)

    Write-Host "$($Job.Target.Name) errors:" -ForegroundColor Red

    if ($Job.Failure) {
        Write-Host $Job.Failure
    }

    $errorLines = @()

    if (Test-Path -LiteralPath $Job.ErrLog -PathType Leaf) {
        $errorLines += @(Get-Content -LiteralPath $Job.ErrLog -Tail 30 -ErrorAction SilentlyContinue)
    }

    if (Test-Path -LiteralPath $Job.Log -PathType Leaf) {
        $errorLines += @(Get-Content -LiteralPath $Job.Log -Tail 80 -ErrorAction SilentlyContinue |
            Where-Object { $_ -match '(?i)\berror\b|NETSDK\d+|MSB\d+' })
    }

    if ($errorLines.Count -gt 0) {
        $errorLines | Select-Object -Unique | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host 'No additional error text was written by dotnet.' -ForegroundColor DarkGray
    }

    Write-Host "Full logs: $($Job.Log) and $($Job.ErrLog)" -ForegroundColor DarkGray
    Write-Host ''
}

Assert-BuildDirectory $Out 'PublishedBuilds'
Assert-BuildDirectory $LogDir '.buildlogs'
Assert-BuildDirectory $ArtifactDir '.publish-artifacts'

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
        [Parameter(Mandatory)][string]$Candidate,
        [string]$Required
    )
    if ([string]::IsNullOrWhiteSpace($Required)) { return $true }
    $parsedRequired = $null
    if (-not [version]::TryParse((($Required -split '-')[0]), [ref]$parsedRequired)) { return $true }
    $listed = & $Candidate --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $listed) { return $false }
    foreach ($line in $listed) {
        $token = ($line -split '\s+')[0]
        $parsed = $null
        if ([version]::TryParse((($token -split '-')[0]), [ref]$parsed)) {
            if ($parsed.Major -eq $parsedRequired.Major -and $parsed.Minor -eq $parsedRequired.Minor -and $parsed -ge $parsedRequired) {
                return $true
            }
        }
    }
    return $false
}

$dotnetCandidates = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
    $dotnetCandidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet'))
}
$homeDirectory = if ($IsWindowsHost) { $env:USERPROFILE } else { $env:HOME }
if (-not [string]::IsNullOrWhiteSpace($homeDirectory)) {
    $dotnetCandidates.Add((Join-Path (Join-Path $homeDirectory '.dotnet') 'dotnet'))
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
    if (Test-DotNetSatisfiesSdk -Candidate $resolved -Required $requiredSdkVersion) {
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

Assert-FileExists $Sln 'Solution'
Assert-FileExists $WinProj 'Windows project'
Assert-FileExists $CrossProj 'Cross-platform project'

$PubOpts = @(
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:UseAppHost=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:IncludeAllContentForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-p:TreatWarningsAsErrors=true',
    '-p:ContinuousIntegrationBuild=true',
    '-p:RestoreUseStaticGraphEvaluation=true',
    '-clp:ErrorsOnly'
)

$AllTargets = @(
    [pscustomobject]@{ Name='Windows-x64';       Key='windows';          Kind='windows'; Rid='win-x64';          OutDir=(Join-Path $Out 'Windows');          Executable='Voidstrap.exe' }
    [pscustomobject]@{ Name='Linux-x64';         Key='linux-x64';        Kind='cross';   Rid='linux-x64';        OutDir=(Join-Path $Out 'Linux-x64');        Executable='Voidstrap' }
    [pscustomobject]@{ Name='Linux-arm64';       Key='linux-arm64';      Kind='cross';   Rid='linux-arm64';      OutDir=(Join-Path $Out 'Linux-arm64');      Executable='Voidstrap' }
    [pscustomobject]@{ Name='Linux-musl-x64';    Key='linux-musl-x64';   Kind='cross';   Rid='linux-musl-x64';   OutDir=(Join-Path $Out 'Linux-musl-x64');   Executable='Voidstrap' }
    [pscustomobject]@{ Name='Linux-musl-arm64';  Key='linux-musl-arm64'; Kind='cross';   Rid='linux-musl-arm64'; OutDir=(Join-Path $Out 'Linux-musl-arm64'); Executable='Voidstrap' }
    [pscustomobject]@{ Name='macOS-x64';         Key='osx-x64';          Kind='cross';   Rid='osx-x64';          OutDir=(Join-Path $Out 'macOS-x64');        Executable='Voidstrap' }
    [pscustomobject]@{ Name='macOS-arm64';       Key='osx-arm64';        Kind='cross';   Rid='osx-arm64';        OutDir=(Join-Path $Out 'macOS-arm64');      Executable='Voidstrap' }
)

$RequestedAll = $Only -contains 'all'

if ($RequestedAll) {
    $Targets = @($AllTargets)
} else {
    $Targets = @($AllTargets | Where-Object { $Only -contains $_.Key })
}

if ($Targets.Count -eq 0) {
    throw "Nothing matches -Only $($Only -join ', ')"
}

# The Windows WPF project uses Microsoft.Windows.CsWinRT, whose build targets
# launch cswinrt.exe. That tool is a Windows executable and cannot run natively
# on Linux/macOS (it exits with code 126 there). Cross-targeting alone does not
# make that build tool cross-platform.
#
# Therefore, when this script runs on a non-Windows host:
#   * `-Only all` builds every target that can be produced from this host and
#     skips the Windows WPF target with an explicit warning.
#   * If Windows was explicitly requested alongside other targets, it is skipped.
#   * If Windows is the only requested target, fail immediately with a clear error.
$SkippedHostTargets = @()
if (-not $IsWindowsHost -and ($Targets.Key -contains 'windows')) {
    $SkippedHostTargets = @($Targets | Where-Object { $_.Key -eq 'windows' })
    $Targets = @($Targets | Where-Object { $_.Key -ne 'windows' })

    if ($Targets.Count -eq 0) {
        throw @"
The Windows Voidstrap project cannot be built on this host.
Microsoft.Windows.CsWinRT invokes cswinrt.exe during the WPF build, and that
Windows executable cannot run natively on Linux/macOS (exit code 126).

Build the Windows target from Windows, or run this script for a Linux/macOS target.
"@
    }
}

$appImageTargets = @($Targets | Where-Object { $_.Rid -in @('linux-x64', 'linux-arm64') })
if ($AppImage -and -not $IsLinuxHost) {
    throw 'AppImage packaging requires a Linux host.'
}
if ($AppImage -and $appImageTargets.Count -eq 0) {
    throw 'AppImage packaging requires a linux x64 or linux arm64 target.'
}
$ShouldBuildAppImage = $IsLinuxHost -and -not $SkipAppImage -and $appImageTargets.Count -gt 0
$UseParallel = -not $Sequential -and ($Parallel -or $Targets.Count -gt 1)
if ($UseParallel) {
    $PubOpts += '-m:1'
} else {
    $PubOpts += '-m'
}

Write-Host 'Voidstrap Build' -ForegroundColor Cyan
Write-Host "Root:   $Root"
Write-Host "Output: $Out"
Write-Host "SDK:    $(& $script:DotNetPath --version)"
Write-Host ''

if ($SkippedHostTargets.Count -gt 0) {
    foreach ($skipped in $SkippedHostTargets) {
        Write-Host "Skipping $($skipped.Name): Windows WPF/CsWinRT publishing requires a Windows host." -ForegroundColor DarkYellow
    }
    Write-Host ''
}

if ($IsWindowsHost -and ($Targets.Key -contains 'windows')) {
    $runningVoidstrap = @(Get-Process -Name 'Voidstrap' -ErrorAction SilentlyContinue)
    if ($runningVoidstrap.Count -gt 0) {
        Write-Host 'Voidstrap.exe is running and may lock the Windows publish output.' -ForegroundColor Red
        Write-Host 'Close Voidstrap, then run this build again.'
        exit 1
    }
}

if (-not $NoClean -and (Test-Path -LiteralPath $Out)) {
    Write-Host 'Cleaning previous published output...' -ForegroundColor DarkGray
    Stop-ProcessesUsingPath $Out
    Remove-BuildDirectory $Out
}
New-Item -ItemType Directory -Path $Out -Force | Out-Null

if (Test-Path -LiteralPath $LogDir) {
    Remove-BuildDirectory $LogDir
}
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

if (-not $NoClean -and (Test-Path -LiteralPath $ArtifactDir)) {
    Remove-BuildDirectory $ArtifactDir
}
New-Item -ItemType Directory -Path $ArtifactDir -Force | Out-Null

if (-not $SkipSolutionBuild) {
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
        Write-Host '[1/2] Full solution pre-build skipped on non-Windows host.' -ForegroundColor DarkYellow
        Write-Host '      The solution contains a WPF/CsWinRT project that requires Windows.' -ForegroundColor DarkGray
        Write-Host '      Each selected cross-platform publish will restore/build what it needs.' -ForegroundColor DarkGray
        Write-Host ''
    }
} else {
    Write-Host '[1/2] Solution build skipped.' -ForegroundColor DarkGray
    Write-Host ''
}

if ($UseParallel) {
    Write-Host "[2/2] Publishing $($Targets.Count) target(s) in parallel..." -ForegroundColor Cyan
    Write-Host '      Each target uses an isolated .NET artifacts tree to avoid parallel build collisions.' -ForegroundColor DarkGray
} else {
    Write-Host "[2/2] Publishing $($Targets.Count) target(s)..." -ForegroundColor Cyan
}
Write-Host ''

$jobs = @()
$unexpectedFailure = $null

try {
    foreach ($t in $Targets) {
        New-Item -ItemType Directory -Path $t.OutDir -Force | Out-Null

        # IMPORTANT: do not override BaseOutputPath/BaseIntermediateOutputPath here.
        # Doing that changes the SDK's default item exclusions and can cause old
        # bin/obj .cs files to be globbed back into the project as source code.
        #
        # --artifacts-path is the supported .NET 8+ mechanism. It isolates bin,
        # obj and other build artifacts by project, and the SDK explicitly keeps
        # legacy bin/** and obj/** excluded from default source globs. Giving each
        # RID its own artifacts root also makes -Parallel safe.
        $targetArtifacts = Join-Path $ArtifactDir $t.Key

        if ($t.Kind -eq 'windows') {
            $argList = @(
                'publish', $WinProj,
                '-c', $Configuration,
                '-r', $t.Rid,
                '-o', $t.OutDir,
                '--artifacts-path', $targetArtifacts,
                '-p:PublishProfile=FolderProfile',
                '-p:PublishReadyToRun=true',
                '-p:EnableWindowsTargeting=true'
            ) + $PubOpts
        } else {
            $argList = @(
                'publish', $CrossProj,
                '-c', $Configuration,
                '-r', $t.Rid,
                '-o', $t.OutDir,
                '--artifacts-path', $targetArtifacts
            ) + $PubOpts
        }

        $log = Join-Path $LogDir "$($t.Key).log"
        $errLog = Join-Path $LogDir "$($t.Key).err.log"

        Write-Host "  Starting $($t.Name)..." -ForegroundColor DarkGray
        $started = Start-DotNetProcess -Arguments $argList -WorkingDirectory $Root

        $job = [pscustomobject]@{
            Target     = $t
            Process    = $started.Process
            StdOutTask = $started.StdOutTask
            StdErrTask = $started.StdErrTask
            Log        = $log
            ErrLog     = $errLog
            Expected   = Join-Path $t.OutDir $t.Executable
            Arguments  = $argList
            StartTime  = Get-Date
            EndTime    = $null
            Failure    = $null
            CapturedOutput = ''
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
        while (@($jobs | Where-Object { $_.Status -eq 'Building' }).Count -gt 0) {
            Start-Sleep -Milliseconds 250

            foreach ($job in $jobs) {
                if ($job.Status -eq 'Building' -and $job.Process.HasExited) {
                    Finish-PublishJob $job
                }
            }
        }
        $retryJobs = @($jobs | Where-Object { $_.Status -eq 'Failed' -and -not $_.Retried -and (Test-TransientPublishFailure $_) })
        foreach ($job in $retryJobs) {
            Write-Host "  $($job.Target.Name) failed, retrying once..." -ForegroundColor DarkYellow
            Restart-PublishJob $job
        }
        while (@($jobs | Where-Object { $_.Status -eq 'Building' }).Count -gt 0) {
            Start-Sleep -Milliseconds 100
            foreach ($job in $jobs) {
                if ($job.Status -eq 'Building' -and $job.Process.HasExited) {
                    Finish-PublishJob $job
                }
            }
        }
    }
}
catch {
    $unexpectedFailure = $_
}
finally {
    # If the script itself failed while parallel builds were still active,
    # stop those child processes rather than leaving dotnet running behind.
    if ($unexpectedFailure) {
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
                    # Best effort only.
                }
            }
        }
    }

    foreach ($job in $jobs) {
        try { $job.Process.Dispose() } catch { }
    }
}

if ($unexpectedFailure) {
    $sw.Stop()
    Remove-PublishManifests
    Write-Host ''
    Write-Host 'BUILD SCRIPT FAILED' -ForegroundColor Red
    Write-Host $unexpectedFailure.Exception.Message -ForegroundColor Red
    Write-Host "Logs:   $LogDir"
    Write-Host "Output: $Out"
    Write-Host "Time:   $($sw.Elapsed.ToString('mm\:ss'))"
    exit 1
}

Write-Host ''
$Failed = @($jobs | Where-Object { $_.Status -eq 'Failed' })

foreach ($job in $Failed) {
    Show-PublishFailure $job
}

# Remove debug symbols from all final publish folders, including stale PDBs
# left behind when -NoClean is used.
Get-ChildItem -LiteralPath $Out -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

if ($Failed.Count -gt 0) {
    $sw.Stop()
    Remove-PublishManifests
    Write-Host ''
    Write-Host "FAILED: $(($Failed | ForEach-Object { $_.Target.Name }) -join ', ')" -ForegroundColor Red
    Write-Host "Logs:   $LogDir"
    Write-Host "Output: $Out"
    Write-Host "Artifacts: $ArtifactDir"
    Write-Host "Time:   $($sw.Elapsed.ToString('mm\:ss'))"
    exit 1
}

$packageFailure = $null
try {
    if ($LinuxPackages -and -not $IsLinuxHost) {
        throw 'Linux package creation requires a Linux host.'
    }

    if ($LinuxPackages -or $ShouldBuildAppImage) {
        $packageScript = Join-Path $Root 'build/Packaging/Linux/package.sh'
        $flatpakScript = Join-Path $Root 'build/Packaging/Flatpak/package.sh'
        $aurScript = Join-Path $Root 'build/Packaging/Arch/pkgbuild.sh'
        $bashCommand = Get-Command bash -ErrorAction SilentlyContinue
        if (-not $bashCommand) {
            throw 'Linux packaging requires bash.'
        }
        $versionMatch = [regex]::Match((Get-Content -Raw -LiteralPath (Join-Path $Root 'Directory.Build.props')), '<VoidstrapVersion>([^<]+)</VoidstrapVersion>')
        if (-not $versionMatch.Success) {
            throw 'The Voidstrap package version is unavailable.'
        }
        $packageVersion = $versionMatch.Groups[1].Value
        if ($ShouldBuildAppImage) {
            $env:APPIMAGETOOL = Resolve-AppImageTool
        }
    }

    if ($LinuxPackages) {
        $linuxJobs = @($jobs | Where-Object { $_.Status -eq 'Done' -and $_.Target.Rid.StartsWith('linux-', [System.StringComparison]::Ordinal) })
        if ($linuxJobs.Count -eq 0) {
            throw 'Select at least one Linux target when requesting Linux packages.'
        }
        $packageOutput = Join-Path $Out 'LinuxPackages'
        if (Test-Path -LiteralPath $packageOutput) {
            Remove-BuildDirectory $packageOutput
        }
        New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
        foreach ($job in $linuxJobs) {
            $formats = if ($job.Target.Rid.StartsWith('linux-musl-', [System.StringComparison]::Ordinal)) {
                @('tar')
            } elseif ($SkipAppImage) {
                @('tar', 'deb', 'rpm')
            } else {
                @('tar', 'deb', 'rpm', 'appimage')
            }
            foreach ($format in $formats) {
                Write-Host "Packaging $($job.Target.Name) $format..." -ForegroundColor Cyan
                & $bashCommand.Source $packageScript $job.Target.Rid $packageVersion $format $packageOutput $job.Expected
                if ($LASTEXITCODE -ne 0) {
                    throw "$format packaging failed for $($job.Target.Name) with exit code $LASTEXITCODE."
                }
            }
            if (-not $job.Target.Rid.StartsWith('linux-musl-', [System.StringComparison]::Ordinal)) {
                Write-Host "Packaging $($job.Target.Name) Flatpak..." -ForegroundColor Cyan
                & $bashCommand.Source $flatpakScript $job.Target.Rid $packageVersion $packageOutput $job.Expected
                if ($LASTEXITCODE -ne 0) {
                    throw "Flatpak packaging failed for $($job.Target.Name) with exit code $LASTEXITCODE."
                }
            }
        }
        $x64Archive = Join-Path $packageOutput "Voidstrap_${packageVersion}_linux-x64.tar.gz"
        $arm64Archive = Join-Path $packageOutput "Voidstrap_${packageVersion}_linux-arm64.tar.gz"
        if ((Test-Path -LiteralPath $x64Archive -PathType Leaf) -and (Test-Path -LiteralPath $arm64Archive -PathType Leaf)) {
            $aurOutput = Join-Path $packageOutput 'AUR'
            New-Item -ItemType Directory -Path $aurOutput -Force | Out-Null
            & $bashCommand.Source $aurScript $packageVersion $aurOutput $x64Archive $arm64Archive
            if ($LASTEXITCODE -ne 0) {
                throw "AUR metadata creation failed with exit code $LASTEXITCODE."
            }
        }
    }
    elseif ($ShouldBuildAppImage) {
        $appImageJobs = @($jobs | Where-Object { $_.Status -eq 'Done' -and $_.Target.Rid -in @('linux-x64', 'linux-arm64') })
        $appImageOutput = Join-Path $Out 'AppImage'
        if (Test-Path -LiteralPath $appImageOutput) {
            Remove-BuildDirectory $appImageOutput
        }
        New-Item -ItemType Directory -Path $appImageOutput -Force | Out-Null
        foreach ($job in $appImageJobs) {
            Write-Host "Packaging $($job.Target.Name) AppImage..." -ForegroundColor Cyan
            & $bashCommand.Source $packageScript $job.Target.Rid $packageVersion 'appimage' $appImageOutput $job.Expected
            if ($LASTEXITCODE -ne 0) {
                throw "AppImage packaging failed for $($job.Target.Name) with exit code $LASTEXITCODE."
            }
            $packageArchitecture = if ($job.Target.Rid -eq 'linux-x64') { 'x86_64' } else { 'aarch64' }
            $appImagePath = Join-Path $appImageOutput "Voidstrap_${packageVersion}_${packageArchitecture}.AppImage"
            Assert-FileExists $appImagePath 'AppImage output'
            Assert-FileExists ($appImagePath + '.zsync') 'AppImage update information'
            if ((Get-Item -LiteralPath $appImagePath).Length -lt 1048576) {
                throw 'The AppImage output is unexpectedly small.'
            }
        }
    }
}
catch {
    $packageFailure = $_
}
finally {
    Remove-PublishManifests
    if (-not $packageFailure -and -not $NoClean -and (Test-Path -LiteralPath $ArtifactDir)) {
        try {
            Remove-BuildDirectory $ArtifactDir
        } catch {
            Write-Host "Warning: could not remove temporary artifacts: $($_.Exception.Message)" -ForegroundColor DarkYellow
        }
    }
}

$sw.Stop()
Write-Host ''
if ($packageFailure) {
    Write-Host 'PACKAGE CREATION FAILED' -ForegroundColor Red
    Write-Host $packageFailure.Exception.Message -ForegroundColor Red
    Write-Host "Logs:   $LogDir"
    Write-Host "Output: $Out"
    Write-Host "Artifacts: $ArtifactDir"
    Write-Host "Time:   $($sw.Elapsed.ToString('mm\:ss'))"
    exit 1
}

Write-Host 'All builds published successfully.' -ForegroundColor Green
Write-Host "Output: $Out"
Write-Host "Logs:   $LogDir"
Write-Host "Time:   $($sw.Elapsed.ToString('mm\:ss'))"
Get-ChildItem -LiteralPath $Out -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name |
    ForEach-Object { Write-Host "  $($_.Name)" }

exit 0
