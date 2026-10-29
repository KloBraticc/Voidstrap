[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('10', '11')][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DotNetPath = 'dotnet',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.StartsWith($root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $output -eq $root) {
    throw 'Windows payload output must be outside the source checkout.'
}
$buildRoot = Join-Path $output 'build'
$arguments = @(
    'publish', (Join-Path $root 'src/Voidstrap.App/Voidstrap.csproj'),
    '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false',
    "-p:VoidstrapRuntime=$Runtime", "-p:VoidstrapLinuxPackagingRoot=$($buildRoot.Replace('\', '/'))/",
    '-p:PublishProfile=FolderProfile', '-p:PublishSingleFile=true', '-p:UseAppHost=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:IncludeAllContentForSelfExtract=false',
    '-p:PublishReadyToRun=false', '-p:DebugType=none', '-p:DebugSymbols=false',
    '-p:TreatWarningsAsErrors=true', '-p:ContinuousIntegrationBuild=true',
    '-p:IncludeSourceRevisionInInformationalVersion=false', '-o', $output
)
if ($Version) { $arguments += "-p:Version=$Version" }
Push-Location -LiteralPath $(if ($Runtime -eq '11') { Join-Path $PSScriptRoot 'Runtime11' } else { $root })
try {
    & $DotNetPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "The .NET $Runtime Windows payload build failed." }
} finally {
    Pop-Location
}
if (-not (Test-Path -LiteralPath (Join-Path $output 'Voidstrap.exe') -PathType Leaf)) {
    throw 'The Windows payload executable was not produced.'
}
$runtimeConfigPath = Join-Path $buildRoot "bin/Voidstrap/$Configuration/net$Runtime.0-windows/win-x64/Voidstrap.runtimeconfig.json"
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.tfm -ne "net$Runtime.0") {
    throw 'The Windows payload targets the wrong runtime.'
}
Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'PublishOutputs.*.txt' -File -ErrorAction SilentlyContinue | Remove-Item -Force
