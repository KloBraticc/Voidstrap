[CmdletBinding()]
param([Parameter(Mandatory)][string]$PayloadDirectory)

$ErrorActionPreference = 'Stop'
$payloads = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$metadata = Get-Content -LiteralPath (Join-Path $payloads 'mirror.json') -Raw | ConvertFrom-Json
$names = @('Voidstrap.net10.bin', 'Voidstrap.net11.bin')
$hashes = @()
foreach ($name in $names) {
    $file = Get-Item -LiteralPath (Join-Path $payloads $name)
    if ($file.Length -eq 0 -or $file.Length -gt 25 * 1024 * 1024) {
        throw 'A runtime payload exceeds the download mirror file limit.'
    }
    $stream = [IO.File]::OpenRead($file.FullName)
    $algorithm = [Security.Cryptography.SHA512]::Create()
    try { $hashes += ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$branch = 'p' + $hashes[0].Substring(0, 12) + $hashes[1].Substring(0, 12)
$mirrorHost = $branch + '.voidstrap-downloads.pages.dev'
if ($metadata.branch -cne $branch -or $metadata.host -cne $mirrorHost) {
    throw 'The mirror metadata does not match the runtime payloads.'
}
function Assert-PayloadHost {
    param([string]$BaseUrl)
    for ($index = 0; $index -lt $names.Count; $index++) {
        $request = [Net.HttpWebRequest]::Create($BaseUrl + '/' + $names[$index])
        $request.Timeout = 30000
        $request.ReadWriteTimeout = 30000
        $response = $request.GetResponse()
        $stream = $response.GetResponseStream()
        $algorithm = [Security.Cryptography.SHA512]::Create()
        try { $hash = ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $algorithm.Dispose(); $stream.Dispose(); $response.Dispose() }
        if ($hash -cne $hashes[$index]) { throw 'A published runtime payload failed verification.' }
    }
}
$npx = (Get-Command npx.cmd -ErrorAction Stop).Source
$gh = (Get-Command gh.exe -ErrorAction Stop).Source
& $gh auth status --hostname github.com
if ($LASTEXITCODE -ne 0) { throw 'GitHub access is required to publish runtime payloads.' }
$stagingRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'VoidstrapMirror'))
$staging = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $payloads $name) -Destination (Join-Path $staging $name) }
    "/*`n  Content-Type: application/octet-stream`n  Cache-Control: public, max-age=31536000, immutable`n  X-Content-Type-Options: nosniff`n" | Set-Content -LiteralPath (Join-Path $staging '_headers') -Encoding ascii
    Push-Location -LiteralPath $staging
    try {
        & $npx --yes wrangler@4.155.0 pages deploy . --project-name voidstrap-downloads --branch $branch --commit-dirty=true --commit-message 'Publish verified Windows runtime payloads'
        if ($LASTEXITCODE -ne 0) { throw 'The runtime download mirror could not be published.' }
    } finally { Pop-Location }
    Assert-PayloadHost ('https://' + $mirrorHost)
    Write-Host ('Verified runtime download mirror: https://' + $mirrorHost)

    $exists = $false
    try {
        & $gh release view $branch --repo KloBraticc/VoidstrapInstaller --json tagName 2>$null | Out-Null
        $exists = $LASTEXITCODE -eq 0
    } catch { $exists = $false }
    if (-not $exists) {
        $notes = Join-Path $staging 'release.txt'
        'App payloads for native Windows setup. Users download Voidstrap.exe separately.' | Set-Content -LiteralPath $notes -Encoding ascii
        & $gh release create $branch (Join-Path $payloads $names[0]) (Join-Path $payloads $names[1]) --repo KloBraticc/VoidstrapInstaller --target main --title 'Voidstrap runtime payloads' --notes-file $notes --prerelease --latest=false
        if ($LASTEXITCODE -ne 0) { throw 'The GitHub runtime payloads could not be published.' }
    }
    Assert-PayloadHost ('https://github.com/KloBraticc/VoidstrapInstaller/releases/download/' + $branch)
    Write-Host ('Verified GitHub runtime payloads: ' + $branch)
} finally {
    $resolved = [IO.Path]::GetFullPath($staging)
    if (-not $resolved.StartsWith($stagingRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid mirror staging directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
