param(
    [ValidateSet('Auto', 'InstalledSdk', 'OfficialDownload')]
    [string]$Source = 'Auto',
    [string]$SdkPlatformToolsPath,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$repositoryDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bundleDirectory = Join-Path $repositoryDirectory 'third_party\adb'
$binaryDirectory = Join-Path $bundleDirectory 'bin'
$archiveUrl = 'https://dl.google.com/android/repository/platform-tools_r37.0.0-win.zip'
$archiveSha256 = '4FE305812DB074CEA32903A489D061EB4454CBC90A49E8FEA677F4B7AF764918'
$expectedFiles = [ordered]@{
    'adb.exe' = '957E46B8615F7AF5B7292A2DDABE98D2E61940C3FB2B0545756507F080613E71'
    'AdbWinApi.dll' = '120BEF587119C6CB926B86B9BE90FDFBCE38937588EAE28CD91A94CE63C7B965'
    'AdbWinUsbApi.dll' = '6CA69A2CA0E31309C087D288F058977D421AD03500E4C3E1DBD981241A069C60'
    'NOTICE.txt' = '628F43E9C88E2BF5AEE9FC4C1CB672BF3931BA12DD5EF75C8170678395F3CA23'
    'source.properties' = 'FBD87C8567AFBC6DC78E140097FCDE234F4A61FA7065E85081D43E442CCD3D24'
}
$runtimeNames = @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll')

function Test-PinnedFiles([string]$directory, [bool]$splitLayout) {
    if (-not $directory) { return $false }
    foreach ($entry in $expectedFiles.GetEnumerator()) {
        $relative = if ($splitLayout -and $runtimeNames -contains $entry.Key) { 'bin\' + $entry.Key } else { $entry.Key }
        $file = Join-Path $directory $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $false }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value) { return $false }
    }
    return $true
}

function Assert-CompleteBundle {
    if (-not (Test-PinnedFiles $bundleDirectory $true)) { throw 'Bundled ADB files are missing or differ from the pinned official Google binaries.' }
    foreach ($name in @('LICENSE.txt', 'README.md', 'provenance.json', 'SHA256SUMS.txt')) {
        $file = Join-Path $bundleDirectory $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
            throw "Required ADB license/provenance file is missing: $name"
        }
    }
    $verifiedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $requiredManifestNames = @('bin/adb.exe', 'bin/AdbWinApi.dll', 'bin/AdbWinUsbApi.dll', 'NOTICE.txt', 'source.properties', 'LICENSE.txt', 'README.md', 'provenance.json')
    foreach ($line in (Get-Content -LiteralPath (Join-Path $bundleDirectory 'SHA256SUMS.txt'))) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { throw 'Invalid ADB checksum manifest entry.' }
        $expectedHash = $Matches[1]
        $relative = $Matches[2]
        if ($relative -notin $requiredManifestNames -or -not $verifiedNames.Add($relative)) {
            throw 'ADB checksum manifest contains an unexpected or duplicate path.'
        }
        $target = [System.IO.Path]::GetFullPath((Join-Path $bundleDirectory $relative))
        if (-not $target.StartsWith($bundleDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'ADB manifest path is outside its bundle.'
        }
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expectedHash) {
            throw "ADB checksum validation failed: $relative"
        }
    }
    if ($verifiedNames.Count -ne $requiredManifestNames.Count) { throw 'ADB checksum manifest does not describe the complete eight-file payload.' }
    Write-Output 'Verified Google Platform-Tools 37.0.0 bundled ADB: three runtime files and five accompanying files. Hash verification did not execute ADB.'
}

if ($VerifyOnly) { Assert-CompleteBundle; return }
if ($Source -eq 'Auto' -and (Test-PinnedFiles $bundleDirectory $true) -and
        (Test-Path -LiteralPath (Join-Path $bundleDirectory 'SHA256SUMS.txt'))) {
    Assert-CompleteBundle
    return
}

$sourceDirectory = $null
if ($Source -ne 'OfficialDownload') {
    $sdkCandidates = @()
    if ($SdkPlatformToolsPath) { $sdkCandidates += $SdkPlatformToolsPath }
    if ($env:ANDROID_SDK_ROOT) { $sdkCandidates += Join-Path $env:ANDROID_SDK_ROOT 'platform-tools' }
    if ($env:ANDROID_HOME) { $sdkCandidates += Join-Path $env:ANDROID_HOME 'platform-tools' }
    if ($env:LOCALAPPDATA) { $sdkCandidates += Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools' }
    foreach ($candidate in $sdkCandidates) {
        if (Test-PinnedFiles $candidate $false) { $sourceDirectory = $candidate; break }
    }
    if (-not $sourceDirectory -and $Source -eq 'InstalledSdk') {
        throw 'No installed SDK matches the pinned official Platform-Tools 37.0.0 files. Use -Source OfficialDownload.'
    }
}

if (-not $sourceDirectory) {
    $cacheDirectory = Join-Path $bundleDirectory '.cache'
    New-Item -ItemType Directory -Force -Path $cacheDirectory | Out-Null
    $archive = Join-Path $cacheDirectory 'platform-tools_r37.0.0-win.zip'
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        Invoke-WebRequest -Uri $archiveUrl -OutFile $archive -TimeoutSec 120
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveSha256) {
        throw 'Official ADB archive checksum mismatch. The existing cache was not executed or deleted.'
    }
    $extractDirectory = Join-Path $cacheDirectory 'official-37.0.0'
    Expand-Archive -LiteralPath $archive -DestinationPath $extractDirectory -Force
    $sourceDirectory = Join-Path $extractDirectory 'platform-tools'
    if (-not (Test-PinnedFiles $sourceDirectory $false)) { throw 'Official archive contents do not match the pinned component hashes.' }
}

$signatures = @()
foreach ($name in $runtimeNames) {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $sourceDirectory $name)
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Google LLC(?:,|$)') {
        throw "Expected valid Google LLC Authenticode signature: $name"
    }
    $signatures += [ordered]@{
        file = $name
        status = $signature.Status.ToString()
        signer = $signature.SignerCertificate.Subject
        certificateThumbprint = $signature.SignerCertificate.Thumbprint
    }
}
New-Item -ItemType Directory -Force -Path $binaryDirectory | Out-Null
foreach ($name in $runtimeNames) { Copy-Item -LiteralPath (Join-Path $sourceDirectory $name) -Destination (Join-Path $binaryDirectory $name) -Force }
foreach ($name in @('NOTICE.txt', 'source.properties')) {
    Copy-Item -LiteralPath (Join-Path $sourceDirectory $name) -Destination (Join-Path $bundleDirectory $name) -Force
}

# Retain the complete upstream NOTICE verbatim; also provide its first Apache-2.0 section as LICENSE.txt.
$notice = Get-Content -LiteralPath (Join-Path $bundleDirectory 'NOTICE.txt') -Raw
$sectionStart = $notice.IndexOf('                                 Apache License', [StringComparison]::Ordinal)
$sectionEnd = $notice.IndexOf('==============================================================================', $sectionStart + 1, [StringComparison]::Ordinal)
if ($sectionStart -lt 0 -or $sectionEnd -le $sectionStart) { throw 'Official Apache license section was not found in NOTICE.txt.' }
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $bundleDirectory 'LICENSE.txt'), $notice.Substring($sectionStart, $sectionEnd - $sectionStart), $utf8)

# `version` is a local client-only command: it neither starts a server nor contacts a device.
$versionLines = @(& (Join-Path $binaryDirectory 'adb.exe') version)
if ($LASTEXITCODE -ne 0 -or $versionLines[0] -ne 'Android Debug Bridge version 1.0.41' -or
        $versionLines[1] -ne 'Version 37.0.0-14910828') { throw 'Bundled ADB version check failed.' }
$files = @()
foreach ($entry in $expectedFiles.GetEnumerator()) {
    $relative = if ($runtimeNames -contains $entry.Key) { 'bin/' + $entry.Key } else { $entry.Key }
    $files += [ordered]@{ path = $relative; sha256 = $entry.Value; length = (Get-Item -LiteralPath (Join-Path $bundleDirectory $relative)).Length }
}
$provenance = [ordered]@{
    schemaVersion = 1
    sdkRevision = '37.0.0'
    adbVersion = '1.0.41'
    adbBuild = '37.0.0-14910828'
    archive = [ordered]@{ url = $archiveUrl; sha256 = $archiveSha256; length = 8092164 }
    officialReleaseNotes = 'https://developer.android.com/tools/releases/platform-tools'
    sdkLicenseTerms = 'https://developer.android.com/studio/terms'
    binaryModification = 'none'
    validation = 'Pinned official archive component hashes; valid Google LLC Authenticode signatures; adb version only.'
    licensingScope = 'Preserve complete upstream NOTICE and review applicable component obligations before public redistribution; not blanket SDK redistribution permission.'
    signatures = $signatures
    files = $files
}
[IO.File]::WriteAllText((Join-Path $bundleDirectory 'provenance.json'), ($provenance | ConvertTo-Json -Depth 8) + "`n", $utf8)
$manifestNames = @('bin/adb.exe', 'bin/AdbWinApi.dll', 'bin/AdbWinUsbApi.dll', 'NOTICE.txt', 'source.properties', 'LICENSE.txt', 'README.md', 'provenance.json')
$manifest = $manifestNames | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $bundleDirectory $_) -Algorithm SHA256).Hash + '  ' + $_ }
[IO.File]::WriteAllText((Join-Path $bundleDirectory 'SHA256SUMS.txt'), ($manifest -join "`n") + "`n", $utf8)
Assert-CompleteBundle
