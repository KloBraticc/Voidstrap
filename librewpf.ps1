# This is for updating the Lib
$ErrorActionPreference = 'Stop'

$index = Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/librewpf.sdk/index.json'
$latest = $index.versions[-1]
$path = Join-Path $PSScriptRoot 'global.json'
$current = Get-Content $path -Raw

if ($current -notmatch '"LibreWPF\.Sdk":\s*"([^"]+)"') {
    throw "global.json has no LibreWPF.Sdk entry under msbuild-sdks"
}

$previous = $Matches[1]
if ($previous -eq $latest) {
    Write-Host "LibreWPF.Sdk is already on $latest"
    return
}

$updated = $current -replace '(?<="LibreWPF\.Sdk":\s*")[^"]+', $latest
[IO.File]::WriteAllText($path, $updated, (New-Object Text.UTF8Encoding $false))
Write-Host "LibreWPF.Sdk $previous to $latest"
