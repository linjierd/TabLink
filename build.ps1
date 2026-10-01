param([switch]$SkipAndroid,[string]$OutputDirectory,[switch]$PublicRelease)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if ($PublicRelease) {
    $insideWorkTree = & git -C $projectRoot rev-parse --is-inside-work-tree 2>$null
    if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne 'true') {
        throw 'PublicRelease must run from a Git working tree so the binaries can be traced to one source commit.'
    }
    $releaseChanges = @(& git -C $projectRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to verify the Git working tree before PublicRelease.' }
    if ($releaseChanges.Count -ne 0) {
        throw 'PublicRelease requires a clean Git working tree. Commit or remove every tracked and untracked source change first.'
    }
}
if ($PublicRelease -and -not $PSBoundParameters.ContainsKey('OutputDirectory')) {
    throw 'PublicRelease requires an explicit new or empty OutputDirectory.'
}
if ($PSBoundParameters.ContainsKey('OutputDirectory')) {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { throw 'OutputDirectory cannot be empty when explicitly supplied.' }
    $publishRoot = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $publishRoot) {
        if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
            throw "OutputDirectory already exists and is not a directory: $publishRoot"
        }
        if ($null -ne (Get-ChildItem -LiteralPath $publishRoot -Force | Select-Object -First 1)) {
            throw "OutputDirectory must be new or empty for a reproducible release build: $publishRoot"
        }
    }
}
else {
    $publishRoot = Join-Path $projectRoot 'dist\TabLink'
}
$apkPath = Join-Path $projectRoot $(if ($PublicRelease) { 'android\artifacts\TabLink-android-0.8.2-preview.apk' } else { 'android\artifacts\TabLink-android-0.8.2-debug.apk' })
$ffmpegRoot = Join-Path $projectRoot 'third_party\ffmpeg-tablink'
$ffmpegBinary = Join-Path $ffmpegRoot 'bin\ffmpeg.exe'
if (-not (Test-Path -LiteralPath $ffmpegBinary)) { throw 'Build the verified TabLink FFmpeg component first.' }
if (-not (Test-Path -LiteralPath (Join-Path $ffmpegRoot 'README.md'))) { throw 'FFmpeg source notice missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $ffmpegRoot 'source-bundle.tar.gz'))) { throw 'FFmpeg corresponding source bundle missing.' }
if ($PublicRelease) {
    $ffmpegBytes = [IO.File]::ReadAllBytes($ffmpegBinary)
    $ffmpegAscii = [Text.Encoding]::ASCII.GetString($ffmpegBytes)
    $ffmpegUtf16 = [Text.Encoding]::Unicode.GetString($ffmpegBytes)
    $ffmpegVersion = @(& $ffmpegBinary -version 2>&1 | ForEach-Object { $_.ToString() }) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Bundled FFmpeg failed its version probe.' }
    $userPathPattern = '(?i)[A-Z]:[\\/]+Users[\\/]'
    if ($ffmpegAscii -match $userPathPattern -or $ffmpegUtf16 -match $userPathPattern -or $ffmpegVersion -match $userPathPattern) {
        throw 'Bundled FFmpeg exposes a builder-specific Users path; rebuild it with the neutral prefix before publishing.'
    }
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
if (-not $PublicRelease) {
    & (Join-Path $projectRoot 'tools\Prepare-BundledAdb.ps1') -VerifyOnly
}
dotnet run --project (Join-Path $projectRoot 'tests\TabLink.Core.Tests\TabLink.Core.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
foreach ($windowsTest in @(
    'TabLink.AdbLocator.Tests',
    'TabLink.Browser.Tests',
    'TabLink.DriverConfiguration.Tests',
    'TabLink.DisplayAllocation.Tests',
    'TabLink.DisplayCleanup.Tests',
    'TabLink.DisplayIdentity.Tests',
    'TabLink.DisplayLifecycle.Tests',
    'TabLink.ConnectionHealth.Tests',
    'TabLink.Diagnostics.Tests',
    'TabLink.Transport.Tests',
    'TabLink.UsbLease.Tests',
    'TabLink.Video.Tests'
)) {
    dotnet run --project (Join-Path $projectRoot ('tests\'+$windowsTest+'\'+$windowsTest+'.csproj')) -c Release
    if ($LASTEXITCODE -ne 0) { throw ($windowsTest+' failed.') }
}
dotnet run --project (Join-Path $projectRoot 'tests\TabLink.Update.Tests\TabLink.Update.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Stable update tests failed.' }
$selfContained = if ($PublicRelease) { 'true' } else { 'false' }
$publicPublishProperties = @()
if ($PublicRelease) {
    # Public artifacts must not disclose local source/PDB paths. PathMap is a
    # second line of defence for compiler-produced metadata; PDB files are also
    # removed from the final staging directory below.
    $publicPublishProperties = @(
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        ("-p:PathMap=$projectRoot=/_/src")
    )
}
$windowsPublishArguments = @(
    'publish', (Join-Path $projectRoot 'src\TabLink.Windows\TabLink.Windows.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', $selfContained,
    '-o', $publishRoot, '--nologo'
)
if ($PublicRelease) {
    $windowsPublishArguments += '-p:EnableBrowserReceiver=false'
    $windowsPublishArguments += $publicPublishProperties
}
dotnet @windowsPublishArguments
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
if ($PublicRelease) {
    $updaterPublishArguments = @(
        'publish', (Join-Path $projectRoot 'src\TabLink.Updater\TabLink.Updater.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-o', $publishRoot, '--nologo'
    ) + $publicPublishProperties
    dotnet @updaterPublishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained updater publish failed.' }
}
$driverPublishArguments = @(
    'publish', (Join-Path $projectRoot 'src\TabLink.DriverSetup\TabLink.DriverSetup.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', $selfContained,
    '-o', $publishRoot, '--nologo'
) + $publicPublishProperties
dotnet @driverPublishArguments
if ($LASTEXITCODE -ne 0) { throw 'Driver helper publish failed.' }
if (-not $SkipAndroid) {
    $androidArguments = @{
        Offline = $true
        UpdateManifestUrl = 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json'
    }
    if ($PublicRelease) { $androidArguments['ReleasePreview'] = $true }
    & (Join-Path $projectRoot 'android\build.ps1') @androidArguments
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
}
if (-not (Test-Path -LiteralPath $apkPath)) { throw 'Android APK missing.' }
New-Item -ItemType Directory -Path (Join-Path $publishRoot 'android') -Force | Out-Null
Copy-Item -LiteralPath $apkPath -Destination (Join-Path $publishRoot 'android\TabLink.apk')
Copy-Item -LiteralPath (Join-Path $projectRoot 'android\README.md') -Destination (Join-Path $publishRoot 'android')
$androidNotices = if ($PublicRelease) { @('THIRD_PARTY_NOTICES.md') } else { @('THIRD_PARTY_NOTICES.md','VERIFICATION-0.7.0.md','VERIFICATION-0.7.1.md','VERIFICATION-0.8.0.md') }
foreach ($androidNotice in $androidNotices) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot ('android\'+$androidNotice))) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ('android\'+$androidNotice)) -Destination (Join-Path $publishRoot 'android')
    }
}
if (-not $PublicRelease) {
    $adbOutput = Join-Path $publishRoot 'tools\platform-tools'
    New-Item -ItemType Directory -Path $adbOutput -Force | Out-Null
    foreach ($name in @('adb.exe','AdbWinApi.dll','AdbWinUsbApi.dll')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ('third_party\adb\bin\'+$name)) -Destination $adbOutput
    }
    foreach ($name in @('README.md','LICENSE.txt','NOTICE.txt','source.properties','provenance.json')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ('third_party\adb\'+$name)) -Destination $adbOutput
    }
    Get-ChildItem -LiteralPath $adbOutput -File | Where-Object {$_.Extension -in '.exe','.dll'} | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash+'  '+$_.Name
    } | Set-Content -LiteralPath (Join-Path $adbOutput 'SHA256SUMS.txt') -Encoding UTF8
}
$ffmpegOutput = Join-Path $publishRoot 'tools\ffmpeg'
New-Item -ItemType Directory -Path $ffmpegOutput -Force | Out-Null
Copy-Item -LiteralPath $ffmpegBinary -Destination $ffmpegOutput
Get-ChildItem -LiteralPath (Join-Path $ffmpegRoot 'bin') -Filter 'COPYING*' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $ffmpegOutput }
foreach ($name in @('README.md','source-bundle.tar.gz','0001-windows-private-high-resolution-usleep.patch','downloads-manifest.json')) {
    Copy-Item -LiteralPath (Join-Path $ffmpegRoot $name) -Destination $ffmpegOutput
}
if (Test-Path -LiteralPath (Join-Path $ffmpegRoot 'NATIVE-MOTION-VALIDATION.md')) {
    Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'NATIVE-MOTION-VALIDATION.md') -Destination $ffmpegOutput
}
# Remove only provenance files copied by the previous Gyan package step.
foreach ($legacy in @('LICENSE','README.txt','TABLINK-NOTICE.md')) {
    $legacyPath = Join-Path $ffmpegOutput $legacy
    if (Test-Path -LiteralPath $legacyPath) { Remove-Item -LiteralPath $legacyPath }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'AUTHORS.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'RELEASE-0.8.2.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.8.2.md') -Destination $publishRoot
if ($PublicRelease) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'PUBLIC-RELEASE.md') -Destination $publishRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot 'ADB-SETUP.md') -Destination $publishRoot
    foreach ($publicDocument in @('RELEASE-0.8.0.md','VERIFICATION-0.8.0.md','AUTO-UPDATE.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $publicDocument) -Destination $publishRoot
    }
}
else {
    foreach ($document in @(
        'RELEASE-0.7.0.md','VERIFICATION-0.7.0.md',
        'RELEASE-0.7.1.md','VERIFICATION-0.7.1.md',
        'RELEASE-0.7.2.md','VERIFICATION-0.7.2.md',
        'RELEASE-0.7.3.md','VERIFICATION-0.7.3.md',
        'RELEASE-0.8.0.md','VERIFICATION-0.8.0.md',
        'VERIFICATION-0.8.1.md','AUTO-UPDATE.md'
    )) {
        $documentPath = Join-Path $projectRoot $document
        if (Test-Path -LiteralPath $documentPath) { Copy-Item -LiteralPath $documentPath -Destination $publishRoot }
    }
}
$noticeOutput = Join-Path $publishRoot 'licenses'
New-Item -ItemType Directory -Path $noticeOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'third_party\qrcoder\LICENSE.txt') -Destination (Join-Path $noticeOutput 'QRCoder-MIT.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot 'third_party\qrcoder\README.md') -Destination (Join-Path $noticeOutput 'QRCoder-NOTICE.md')
if(-not $PublicRelease -and (Test-Path -LiteralPath (Join-Path $projectRoot 'third_party\sipsorcery'))) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'third_party\sipsorcery') -Destination $noticeOutput -Recurse -Force
}
foreach($clientSource in @('native','browser')) {
    if($PublicRelease) { continue }
    $sourcePath=Join-Path $projectRoot $clientSource
    if(Test-Path -LiteralPath $sourcePath) {
        Copy-Item -LiteralPath $sourcePath -Destination $publishRoot -Recurse -Force
    }
}
if (-not $PublicRelease -and (Test-Path -LiteralPath (Join-Path $projectRoot 'VERIFICATION.md'))) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'VERIFICATION.md') -Destination $publishRoot
}
if ($PublicRelease) {
    Get-ChildItem -LiteralPath $publishRoot -Filter '*.pdb' -File -Recurse | Remove-Item -Force
}
# Pure transport tests run through the managed entry point so the build does not
# open a UAC prompt. The shipped executable itself requires administrator consent.
& dotnet (Join-Path $publishRoot 'TabLink.dll') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Windows transport tests failed.' }
Get-Content -LiteralPath (Join-Path $publishRoot 'selftest-result.txt')
Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($publishRoot, $_.FullName)
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    "$hash  $relative"
} | Set-Content -LiteralPath (Join-Path $publishRoot 'SHA256SUMS.txt') -Encoding UTF8
Write-Output ('Ready: ' + (Join-Path $publishRoot 'TabLink.exe'))
