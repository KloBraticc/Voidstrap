 param(
	[Parameter(Mandatory = $true)]
	[string]$Version,

	[string]$Repository = 'KloBraticc/Voidstrap',

	[Parameter(Mandatory = $true)]
	[string]$OutputDirectory,

	[string]$ManifestVersion = '1.9.0'
)

$ErrorActionPreference = 'Stop'

$identifier = 'KloBraticc.Voidstrap'
$packageFolder = Join-Path $OutputDirectory ("KloBraticc\Voidstrap\" + $Version)
[System.IO.Directory]::CreateDirectory($packageFolder) | Out-Null

$assetName = 'Voidstrap.exe'
$tag = 'v' + $Version
$installerUrl = "https://github.com/$Repository/releases/download/$tag/$assetName"
$releaseUrl = "https://github.com/$Repository/releases/tag/$tag"

$downloads = Join-Path ([IO.Path]::GetTempPath()) ('VoidstrapWinget/' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($downloads) | Out-Null
$assetPath = Join-Path $downloads $assetName

Write-Host ('  Downloading ' + $installerUrl)
Invoke-WebRequest -Uri $installerUrl -OutFile $assetPath -UseBasicParsing
$hash = (Get-FileHash -Path $assetPath -Algorithm SHA256).Hash
$releaseDate = (Get-Date).ToString('yyyy-MM-dd')
[System.IO.Directory]::Delete($downloads, $true)

$versionManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.9.0.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $ManifestVersion
"@

$installerManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.9.0.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: $installerUrl
    InstallerSha256: $hash
    InstallerSwitches:
      Silent: -quiet
      SilentWithProgress: -quiet
    InstallModes:
      - interactive
      - silent
      - silentWithProgress
    UpgradeBehavior: install
    Scope: user
    AppsAndFeaturesEntries:
      - DisplayName: Voidstrap
        DisplayVersion: $Version
        Publisher: Voidstrap
        ProductCode: Voidstrap
ManifestType: installer
ManifestVersion: $ManifestVersion
"@

$localeManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.9.0.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: KloBraticc
PublisherUrl: https://github.com/KloBraticc
PublisherSupportUrl: https://github.com/KloBraticc/Voidstrap/issues
PackageName: Voidstrap
PackageUrl: https://github.com/KloBraticc/Voidstrap
License: MIT
LicenseUrl: https://github.com/KloBraticc/Voidstrap/blob/main/LICENSE.VOIDSTRAP
ShortDescription: Fast, customizable Roblox bootstrapper with FastFlags, mods and deep customization
Description: |-
  Voidstrap is a launcher for Roblox on Windows with FastFlags, custom integrations,
  multi instance support, modular UI customization and performance tuning. The app
  installs per user, keeps Windows and Linux in sync and updates itself from GitHub.
Moniker: voidstrap
Tags:
  - roblox
  - launcher
  - bootstrapper
  - fastflags
  - game-launcher
ReleaseNotesUrl: $releaseUrl
InstallationNotes: Voidstrap finishes setup on first launch and keeps itself up to date.
ManifestType: defaultLocale
ManifestVersion: $ManifestVersion
"@

[System.IO.File]::WriteAllText((Join-Path $packageFolder "$identifier.installer.yaml"), $installerManifest)
[System.IO.File]::WriteAllText((Join-Path $packageFolder "$identifier.locale.en-US.yaml"), $localeManifest)
[System.IO.File]::WriteAllText((Join-Path $packageFolder "$identifier.yaml"), $versionManifest)

Write-Host ('  SHA256 ' + $hash)
Write-Host ('  Manifests written to ' + $packageFolder)
