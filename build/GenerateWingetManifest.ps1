param(
	[Parameter(Mandatory = $true)]
	[ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z')]
	[string]$Version,

	[ValidatePattern('\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z')]
	[string]$Repository = 'KloBraticc/Voidstrap',

	[Parameter(Mandatory = $true)]
	[string]$OutputDirectory,

	[ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+\z')]
	[string]$ManifestVersion = '1.12.0'
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
try {
	Invoke-WebRequest -Uri $installerUrl -OutFile $assetPath -UseBasicParsing
	$hash = (Get-FileHash -Path $assetPath -Algorithm SHA256).Hash
} finally {
	if ([System.IO.File]::Exists($assetPath)) {
		[System.IO.File]::Delete($assetPath)
	}
	[System.IO.Directory]::Delete($downloads)
}

$versionManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$ManifestVersion.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $ManifestVersion
"@

$installerManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$ManifestVersion.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
Dependencies:
  PackageDependencies:
    - PackageIdentifier: Microsoft.DotNet.DesktopRuntime.10
      MinimumVersion: 10.0.0
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
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$ManifestVersion.schema.json
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
InstallationNotes: Requires .NET Desktop Runtime 10. WinGet installs this dependency automatically.
ManifestType: defaultLocale
ManifestVersion: $ManifestVersion
"@

[System.IO.File]::WriteAllText((Join-Path $packageFolder "$identifier.installer.yaml"), $installerManifest + "`r`n")
[System.IO.File]::WriteAllText((Join-Path $packageFolder "$identifier.locale.en-US.yaml"), $localeManifest + "`r`n")
[System.IO.File]::WriteAllText((Join-Path $packageFolder "$identifier.yaml"), $versionManifest + "`r`n")

Write-Host ('  SHA256 ' + $hash)
Write-Host ('  Manifests written to ' + $packageFolder)
