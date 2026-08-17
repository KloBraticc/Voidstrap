[CmdletBinding()]
param(
    [ValidateSet('windows','linux-x64','linux-arm64','linux-musl-x64','linux-musl-arm64','osx-x64','osx-arm64','all')]
    [string[]]$Only = @('all'),

    [string]$Configuration = 'Release',

    [switch]$NoClean,
    [switch]$Parallel,
    [switch]$SkipSolutionBuild
)

$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$Root      = [System.IO.Path]::GetFullPath($PSScriptRoot)
$Out       = [System.IO.Path]::GetFullPath((Join-Path $Root 'PublishedBuilds'))
$LogDir    = [System.IO.Path]::GetFullPath((Join-Path $Root '.buildlogs'))
$WinProj   = Join-Path $Root 'src\Voidstrap.App\Voidstrap.csproj'
$CrossProj = Join-Path $Root 'src\Voidstrap.Cross\Voidstrap.Cross.csproj'
$Sln       = Join-Path $Root 'Voidstrap.sln'

function Assert-BuildDirectory([string]$Path, [string]$ExpectedName) {
    $parent = [System.IO.Path]::GetDirectoryName($Path)
    $name = [System.IO.Path]::GetFileName($Path)
    if (-not [string]::Equals($parent, $Root, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($name, $ExpectedName, [System.StringComparison]::Ordinal)) {
        throw "The build directory is outside the repository root"
    }
}

function Remove-BuildDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Remove-Item -LiteralPath $Path -Force
        return
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

Assert-BuildDirectory $Out 'PublishedBuilds'
Assert-BuildDirectory $LogDir '.buildlogs'

$PubOpts = @(
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:IncludeAllContentForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-clp:ErrorsOnly'
)

$AllTargets = @(
    [pscustomobject]@{ Name='Windows';     Key='windows';     Kind='profile'; Rid=$null;        OutDir=(Join-Path $Out 'Windows');     Executable='Voidstrap.exe' }
    [pscustomobject]@{ Name='Linux-x64';   Key='linux-x64';   Kind='cross';   Rid='linux-x64';   OutDir=(Join-Path $Out 'Linux-x64');   Executable='Voidstrap' }
    [pscustomobject]@{ Name='Linux-arm64'; Key='linux-arm64'; Kind='cross';   Rid='linux-arm64'; OutDir=(Join-Path $Out 'Linux-arm64'); Executable='Voidstrap' }
    [pscustomobject]@{ Name='Linux-musl-x64';   Key='linux-musl-x64';   Kind='cross';   Rid='linux-musl-x64';   OutDir=(Join-Path $Out 'Linux-musl-x64');   Executable='Voidstrap' }
    [pscustomobject]@{ Name='Linux-musl-arm64'; Key='linux-musl-arm64'; Kind='cross';   Rid='linux-musl-arm64'; OutDir=(Join-Path $Out 'Linux-musl-arm64'); Executable='Voidstrap' }
    [pscustomobject]@{ Name='macOS-x64';   Key='osx-x64';     Kind='cross';   Rid='osx-x64';     OutDir=(Join-Path $Out 'macOS-x64');   Executable='Voidstrap' }
    [pscustomobject]@{ Name='macOS-arm64'; Key='osx-arm64';   Kind='cross';   Rid='osx-arm64';   OutDir=(Join-Path $Out 'macOS-arm64'); Executable='Voidstrap' }
)

if ($Only -contains 'all') {
    $Targets = @($AllTargets)
} else {
    $Targets = @($AllTargets | Where-Object { $Only -contains $_.Key })
}

if (-not $Targets) {
    Write-Host "Nothing matches -Only $($Only -join ',')" -ForegroundColor Red
    exit 1
}

function Quote-Arg([string]$Arg) {
    if ($Arg -match '\s') {
        if ($Arg -match '\\$' -and $Arg -notmatch '\\\\$') { $Arg += '\' }
        return '"' + $Arg + '"'
    }
    return $Arg
}

Write-Host 'Voidstrap Build' -ForegroundColor Cyan
Write-Host "Output: $Out"
Write-Host ''

if (Get-Process -Name 'Voidstrap' -ErrorAction SilentlyContinue) {
    Write-Host 'Voidstrap.exe is running and locks the output file' -ForegroundColor Red
    Write-Host 'Close Voidstrap, then run this again'
    exit 1
}

if (-not $NoClean -and (Test-Path $Out)) {
    Write-Host 'Cleaning previous output...' -ForegroundColor DarkGray
    Remove-BuildDirectory $Out
}
New-Item -ItemType Directory -Path $Out -Force | Out-Null

if (Test-Path $LogDir) { Remove-BuildDirectory $LogDir }
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

if (-not $SkipSolutionBuild) {
    Write-Host "[1/2] Building solution ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $Sln -c $Configuration -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host 'Solution build failed, stopping' -ForegroundColor Red
        exit 1
    }
    Write-Host '    done'
    Write-Host ''
}

if ($Parallel) {
    Write-Host "[2/2] Publishing $($Targets.Count) target(s) in parallel..." -ForegroundColor Cyan
    Write-Host '      Targets share the referenced project output, so this can fail intermittently' -ForegroundColor DarkYellow
} else {
    Write-Host "[2/2] Publishing $($Targets.Count) target(s)..." -ForegroundColor Cyan
}
Write-Host ''

$jobs = @()

foreach ($t in $Targets) {
    if ($t.Kind -eq 'profile') {
        $argList = @('publish', $WinProj, '-p:PublishProfile=FolderProfile', "-p:PublishDir=$($t.OutDir)\", '-clp:ErrorsOnly')
    } else {
        $argList = @('publish', $CrossProj, '-c', $Configuration, '-r', $t.Rid, '-o', $t.OutDir,
					  "-p:BaseIntermediateOutputPath=obj-$($t.Rid)\") + $PubOpts
    }
    $argString = ($argList | ForEach-Object { Quote-Arg $_ }) -join ' '
    $log = Join-Path $LogDir "$($t.Key).log"

    $proc = Start-Process -FilePath 'dotnet' -ArgumentList $argString -WorkingDirectory $Root `
        -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"

    $j = [pscustomobject]@{
        Target    = $t
        Process   = $proc
        Log       = $log
        ErrLog    = "$log.err"
        Expected  = Join-Path $t.OutDir $t.Executable
        StartTime = Get-Date
        EndTime   = $null
        Failure   = $null
        Status    = 'Building'
    }

    if (-not $Parallel) { $proc.WaitForExit() }

    $jobs += $j
}

while ($jobs | Where-Object { $_.Status -eq 'Building' }) {
    Start-Sleep -Milliseconds 500
    foreach ($j in $jobs) {
        if ($j.Status -eq 'Building' -and $j.Process.HasExited) {
            $j.Process.WaitForExit()
            $j.EndTime = Get-Date
            $exitCode = $j.Process.ExitCode
            if ($null -ne $exitCode -and $exitCode -ne 0) {
                $j.Failure = "dotnet publish exited with code $exitCode"
                $j.Status = 'Failed'
            } elseif (-not (Test-Path -LiteralPath $j.Expected -PathType Leaf)) {
                $j.Failure = "Expected executable was not created: $($j.Expected)"
                $j.Status = 'Failed'
            } else {
                $j.Status = 'Done'
            }
            $elapsed = [int](New-TimeSpan -Start $j.StartTime -End $j.EndTime).TotalSeconds
            $color = if ($j.Status -eq 'Done') { 'Green' } else { 'Red' }
            Write-Host "  $($j.Target.Name): $($j.Status) (${elapsed}s)" -ForegroundColor $color
        }
    }
}

Write-Host ''

$Failed = $jobs | Where-Object { $_.Status -eq 'Failed' }

foreach ($j in $Failed) {
    Write-Host "$($j.Target.Name) errors:" -ForegroundColor Red
    if ($j.Failure) { Write-Host $j.Failure }
    Get-Content $j.ErrLog -Tail 20 -ErrorAction SilentlyContinue
    Get-Content $j.Log -Tail 20 -ErrorAction SilentlyContinue | Where-Object { $_ -match 'error' }
    Write-Host "(full log: $($j.Log))" -ForegroundColor DarkGray
    Write-Host ''
}

$pdbDirs = Get-ChildItem $Out -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Linux-*' -or $_.Name -like 'macOS-*' }
foreach ($dir in $pdbDirs) {
    Get-ChildItem $dir.FullName -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

foreach ($j in $jobs) {
    $j.Process.Dispose()
}

$sw.Stop()
Write-Host ''
if ($Failed) {
    Write-Host "FAILED: $($Failed.Target.Name -join ', ')" -ForegroundColor Red
    Write-Host "Logs:   $LogDir"
    Write-Host "Output: $Out"
    Write-Host "Time:   $($sw.Elapsed.ToString('mm\:ss'))"
    exit 1
}

Write-Host 'All builds published' -ForegroundColor Green
Write-Host "Output: $Out"
Write-Host "Time:   $($sw.Elapsed.ToString('mm\:ss'))"
Get-ChildItem $Out -Directory | ForEach-Object { Write-Host "  $($_.Name)" }
exit 0
