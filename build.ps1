param([switch]$SkipAndroid,[string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
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
$apkPath = Join-Path $projectRoot 'android\artifacts\TabLink-android-0.8.0-debug.apk'
$ffmpegRoot = Join-Path $projectRoot 'third_party\ffmpeg-tablink'
if (-not (Test-Path -LiteralPath (Join-Path $ffmpegRoot 'bin\ffmpeg.exe'))) { throw 'Build the verified TabLink FFmpeg component first.' }
if (-not (Test-Path -LiteralPath (Join-Path $ffmpegRoot 'README.md'))) { throw 'FFmpeg source notice missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $ffmpegRoot 'source-bundle.tar.gz'))) { throw 'FFmpeg corresponding source bundle missing.' }
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
& (Join-Path $projectRoot 'tools\Prepare-BundledAdb.ps1') -VerifyOnly
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
dotnet publish (Join-Path $projectRoot 'src\TabLink.Windows\TabLink.Windows.csproj') -c Release -r win-x64 --self-contained false -o $publishRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
dotnet publish (Join-Path $projectRoot 'src\TabLink.DriverSetup\TabLink.DriverSetup.csproj') -c Release -r win-x64 --self-contained false -o $publishRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Driver helper publish failed.' }
if (-not $SkipAndroid) {
    & (Join-Path $projectRoot 'android\build.ps1') -Offline -UpdateManifestUrl 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json'
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
}
if (-not (Test-Path -LiteralPath $apkPath)) { throw 'Android APK missing.' }
New-Item -ItemType Directory -Path (Join-Path $publishRoot 'android') -Force | Out-Null
Copy-Item -LiteralPath $apkPath -Destination (Join-Path $publishRoot 'android\TabLink.apk')
Copy-Item -LiteralPath (Join-Path $projectRoot 'android\README.md') -Destination (Join-Path $publishRoot 'android')
foreach ($androidNotice in @('THIRD_PARTY_NOTICES.md','VERIFICATION-0.7.0.md','VERIFICATION-0.7.1.md','VERIFICATION-0.8.0.md')) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot ('android\'+$androidNotice))) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ('android\'+$androidNotice)) -Destination (Join-Path $publishRoot 'android')
    }
}
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
$ffmpegOutput = Join-Path $publishRoot 'tools\ffmpeg'
New-Item -ItemType Directory -Path $ffmpegOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'bin\ffmpeg.exe') -Destination $ffmpegOutput
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
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'RELEASE-0.7.0.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'RELEASE-0.7.1.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.7.1.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'RELEASE-0.7.2.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.7.2.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'RELEASE-0.7.3.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.7.3.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'RELEASE-0.8.0.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'RELEASE-0.8.1.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'AUTO-UPDATE.md') -Destination $publishRoot
if(Test-Path -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.8.0.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.8.0.md') -Destination $publishRoot
}
if(Test-Path -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.8.1.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.8.1.md') -Destination $publishRoot
}
if(Test-Path -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.7.0.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.7.0.md') -Destination $publishRoot
}
$noticeOutput = Join-Path $publishRoot 'licenses'
New-Item -ItemType Directory -Path $noticeOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'third_party\qrcoder\LICENSE.txt') -Destination (Join-Path $noticeOutput 'QRCoder-MIT.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot 'third_party\qrcoder\README.md') -Destination (Join-Path $noticeOutput 'QRCoder-NOTICE.md')
if(Test-Path -LiteralPath (Join-Path $projectRoot 'third_party\sipsorcery')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'third_party\sipsorcery') -Destination $noticeOutput -Recurse -Force
}
foreach($clientSource in @('native','browser')) {
    $sourcePath=Join-Path $projectRoot $clientSource
    if(Test-Path -LiteralPath $sourcePath) {
        Copy-Item -LiteralPath $sourcePath -Destination $publishRoot -Recurse -Force
    }
}
if (Test-Path -LiteralPath (Join-Path $projectRoot 'VERIFICATION.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'VERIFICATION.md') -Destination $publishRoot
}
# Pure transport tests run through the managed entry point so the build does not
# open a UAC prompt. The shipped executable itself requires administrator consent.
& dotnet (Join-Path $publishRoot 'TabLink.dll') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Windows transport tests failed.' }
Get-Content -LiteralPath (Join-Path $publishRoot 'selftest-result.txt')
Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Where-Object { $_.Extension -in '.exe','.dll','.apk','.cat','.inf' } | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($publishRoot, $_.FullName)
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    "$hash  $relative"
} | Set-Content -LiteralPath (Join-Path $publishRoot 'SHA256SUMS.txt') -Encoding UTF8
Write-Output ('Ready: ' + (Join-Path $publishRoot 'TabLink.exe'))
