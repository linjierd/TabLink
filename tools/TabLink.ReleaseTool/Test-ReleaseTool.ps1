#requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'TabLink.ReleaseTool.csproj'
& dotnet build $project -c Release --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "ReleaseTool build failed with exit code $LASTEXITCODE." }
$tool = Join-Path $PSScriptRoot 'bin\Release\net10.0\TabLink.ReleaseTool.dll'
if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "ReleaseTool output is missing: $tool" }

function Invoke-ReleaseTool([string[]]$Arguments, [bool]$ShouldSucceed) {
    $output = & dotnet $tool @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($ShouldSucceed -and $exitCode -ne 0) {
        throw "ReleaseTool unexpectedly failed ($exitCode): $($output -join [Environment]::NewLine)"
    }
    if (-not $ShouldSucceed -and $exitCode -eq 0) {
        throw "ReleaseTool unexpectedly succeeded: $($Arguments -join ' ')"
    }
    return @($output)
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Assert-Throws([scriptblock]$Action, [string]$ExpectedMessage) {
    $failed = $false
    try { & $Action | Out-Null }
    catch {
        $failed = $true
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Expected failure containing '$ExpectedMessage', got: $($_.Exception.Message)"
        }
    }
    if (-not $failed) { throw "Expected operation to fail with: $ExpectedMessage" }
}

function New-TestWindowsSource([string]$Path, [string]$ToolDirectory) {
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    Copy-Item -LiteralPath (Join-Path $ToolDirectory 'TabLink.ReleaseTool.exe') -Destination (Join-Path $Path 'TabLink.exe')
    Copy-Item -LiteralPath (Join-Path $ToolDirectory 'TabLink.ReleaseTool.dll') -Destination (Join-Path $Path 'TabLink.dll')
    Copy-Item -LiteralPath (Join-Path $ToolDirectory 'TabLink.ReleaseTool.exe') -Destination (Join-Path $Path 'TabLink.Updater.exe')
    Copy-Item -LiteralPath (Join-Path $ToolDirectory 'TabLink.ReleaseTool.dll') -Destination (Join-Path $Path 'TabLink.Updater.dll')
    Copy-Item -LiteralPath (Join-Path $ToolDirectory 'TabLink.ReleaseTool.deps.json') -Destination (Join-Path $Path 'TabLink.Updater.deps.json')
    Copy-Item -LiteralPath (Join-Path $ToolDirectory 'TabLink.ReleaseTool.runtimeconfig.json') -Destination (Join-Path $Path 'TabLink.Updater.runtimeconfig.json')
    Write-Utf8NoBom (Join-Path $Path 'update-channel.json') '{"enabled":true,"manifestUrl":"https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json","checkIntervalMinutes":360}'
    Write-Utf8NoBom (Join-Path $Path 'selftest-result.txt') 'stable publisher fixture passed'
    $checksums = foreach ($name in @('TabLink.exe', 'TabLink.dll', 'TabLink.Updater.exe', 'TabLink.Updater.dll')) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $Path $name) -Algorithm SHA256).Hash
        "$hash  $name"
    }
    Write-Utf8NoBom (Join-Path $Path 'SHA256SUMS.txt') (($checksums -join "`n") + "`n")
}

function New-TestAndroidApkWithWrongPackage([string]$Path, [string]$RepositoryRoot) {
    $fixtureRoot = Join-Path ([IO.Path]::GetDirectoryName($Path)) ('wrong-package-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $manifestPath = Join-Path $fixtureRoot 'AndroidManifest.xml'
    Write-Utf8NoBom $manifestPath @'
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
    package="com.example.not_tablink"
    android:versionCode="100"
    android:versionName="1.0.0">
    <uses-sdk android:minSdkVersion="23" android:targetSdkVersion="35" />
    <application android:label="Wrong package fixture" />
</manifest>
'@
    $aapt = Join-Path $RepositoryRoot 'android\.tools\sdk\build-tools\35.0.0\aapt.exe'
    $androidJar = Join-Path $RepositoryRoot 'android\.tools\sdk\platforms\android-35\android.jar'
    if (-not [IO.File]::Exists($aapt) -or -not [IO.File]::Exists($androidJar)) {
        throw 'Wrong-package publisher fixture requires the repository Android 35 aapt and android.jar.'
    }
    $inspectionAndroidJar = Join-Path $fixtureRoot 'android.jar'
    [IO.File]::Copy($androidJar, $inspectionAndroidJar, $false)
    & $aapt package -f -M $manifestPath -I $inspectionAndroidJar -F $Path 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not [IO.File]::Exists($Path)) {
        throw 'Could not build the isolated wrong-package Android fixture.'
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('TabLink-ReleaseTool-Test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $privateKey = Join-Path $testRoot 'test-private.pem'
    $publicKey = Join-Path $testRoot 'test-public.spki.base64'
    Invoke-ReleaseTool @('init-key', $privateKey, $publicKey) $true | Out-Null

    $payloadPath = Join-Path $testRoot 'payload.json'
    $payload = [ordered]@{
        schema = 1
        channel = 'stable'
        releaseId = 'release-tool-test-1.2.3'
        publishedAtUtc = '2026-09-29T14:35:00Z'
        rolloutPercentage = 100
        minimumProtocolVersion = 1
        artifacts = @(
            [ordered]@{
                platform = 'windows-x64'
                version = '1.2.3'
                build = 123
                url = 'https://updates.tablink.example/download/api/download?path=TabLink-1.2.3.zip'
                size = 42
                sha256 = ('a' * 64)
                notes = 'interoperability test'
            }
        )
    }
    Write-Utf8NoBom $payloadPath ($payload | ConvertTo-Json -Depth 8 -Compress)

    $manifest = Join-Path $testRoot 'manifest.json'
    Invoke-ReleaseTool @('sign', $privateKey, $payloadPath, $manifest) $true | Out-Null
    Invoke-ReleaseTool @('verify', $publicKey, $manifest) $true | Out-Null

    $pausedPayload = ($payload | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
    $pausedPayload.rolloutPercentage = 0
    $pausedPayloadPath = Join-Path $testRoot 'payload-paused.json'
    $pausedManifest = Join-Path $testRoot 'manifest-paused.json'
    Write-Utf8NoBom $pausedPayloadPath ($pausedPayload | ConvertTo-Json -Depth 8 -Compress)
    Invoke-ReleaseTool @('sign', $privateKey, $pausedPayloadPath, $pausedManifest) $true | Out-Null
    Invoke-ReleaseTool @('verify', $publicKey, $pausedManifest) $true | Out-Null

    $rawEnvelope = Get-Content -LiteralPath $manifest -Raw
    $envelope = $rawEnvelope | ConvertFrom-Json
    if ($rawEnvelope -cnotmatch '"payload"\s*:' -or $rawEnvelope -cnotmatch '"signature"\s*:' -or
        $rawEnvelope -cmatch '"Payload"\s*:' -or $rawEnvelope -cmatch '"Signature"\s*:') {
        throw 'Signed envelope property casing is not exactly payload/signature.'
    }
    $decodedPayload = [Convert]::FromBase64String($envelope.payload)
    if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($decodedPayload, [IO.File]::ReadAllBytes($payloadPath))) {
        throw 'Signed envelope payload differs from the source payload bytes.'
    }

    $tamperedPayloadText = [Text.Encoding]::UTF8.GetString($decodedPayload).Replace('"stable"', '"stablE"')
    $tamperedPayloadEnvelope = [ordered]@{
        payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($tamperedPayloadText))
        signature = $envelope.signature
    }
    $tamperedPayloadManifest = Join-Path $testRoot 'manifest-tampered-payload.json'
    Write-Utf8NoBom $tamperedPayloadManifest ($tamperedPayloadEnvelope | ConvertTo-Json -Compress)
    Invoke-ReleaseTool @('verify', $publicKey, $tamperedPayloadManifest) $false | Out-Null

    $signatureBytes = [Convert]::FromBase64String($envelope.signature)
    $signatureBytes[0] = $signatureBytes[0] -bxor 1
    $tamperedSignatureEnvelope = [ordered]@{
        payload = $envelope.payload
        signature = [Convert]::ToBase64String($signatureBytes)
    }
    $tamperedSignatureManifest = Join-Path $testRoot 'manifest-tampered-signature.json'
    Write-Utf8NoBom $tamperedSignatureManifest ($tamperedSignatureEnvelope | ConvertTo-Json -Compress)
    Invoke-ReleaseTool @('verify', $publicKey, $tamperedSignatureManifest) $false | Out-Null

    $missingPrivateManifest = Join-Path $testRoot 'manifest-missing-private.json'
    Invoke-ReleaseTool @('sign', (Join-Path $testRoot 'missing-private.pem'), $payloadPath, $missingPrivateManifest) $false | Out-Null
    if (Test-Path -LiteralPath $missingPrivateManifest) {
        throw 'A manifest was created even though the private key was missing.'
    }

    $existingTarget = Join-Path $testRoot 'manifest-existing.json'
    Write-Utf8NoBom $existingTarget 'sentinel-do-not-replace'
    Invoke-ReleaseTool @('sign', $privateKey, $payloadPath, $existingTarget) $false | Out-Null
    if ((Get-Content -LiteralPath $existingTarget -Raw) -cne 'sentinel-do-not-replace') {
        throw 'An existing manifest target was modified.'
    }

    $invalidPayload = ($payload | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
    $invalidPayload.artifacts = @(
        [ordered]@{
            platform = 'windows-x64'; version = '1.2.3'; build = 123
            url = 'http://updates.tablink.example/TabLink.zip'; size = 42; sha256 = ('a' * 64)
        }
    )
    $invalidPayloadPath = Join-Path $testRoot 'payload-http.json'
    Write-Utf8NoBom $invalidPayloadPath ($invalidPayload | ConvertTo-Json -Depth 8 -Compress)
    Invoke-ReleaseTool @('sign', $privateKey, $invalidPayloadPath, (Join-Path $testRoot 'manifest-http.json')) $false | Out-Null

    $fractionalPayload = ($payload | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
    $fractionalPayload.publishedAtUtc = '2026-09-29T14:35:00.1234567Z'
    $fractionalPayloadPath = Join-Path $testRoot 'payload-fractional-time.json'
    Write-Utf8NoBom $fractionalPayloadPath ($fractionalPayload | ConvertTo-Json -Depth 8 -Compress)
    Invoke-ReleaseTool @('sign', $privateKey, $fractionalPayloadPath, (Join-Path $testRoot 'manifest-fractional-time.json')) $false | Out-Null

    foreach ($invalidRollout in @(-1, 101)) {
        $rolloutPayload = ($payload | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
        $rolloutPayload.rolloutPercentage = $invalidRollout
        $rolloutPayloadPath = Join-Path $testRoot "payload-rollout-$invalidRollout.json"
        Write-Utf8NoBom $rolloutPayloadPath ($rolloutPayload | ConvertTo-Json -Depth 8 -Compress)
        Invoke-ReleaseTool @('sign', $privateKey, $rolloutPayloadPath, (Join-Path $testRoot "manifest-rollout-$invalidRollout.json")) $false | Out-Null
    }

    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $publisher = Join-Path $repositoryRoot 'tools\New-TabLinkStableRelease.ps1'
    $buildScript = Join-Path $repositoryRoot 'build.ps1'
    $toolDirectory = Split-Path -Parent $tool

    $nonEmptyBuildOutput = Join-Path $testRoot 'non-empty-build-output'
    [IO.Directory]::CreateDirectory($nonEmptyBuildOutput) | Out-Null
    Write-Utf8NoBom (Join-Path $nonEmptyBuildOutput 'sentinel.txt') 'must survive'
    Assert-Throws { & $buildScript -OutputDirectory $nonEmptyBuildOutput } 'OutputDirectory must be new or empty'
    if ((Get-Content -LiteralPath (Join-Path $nonEmptyBuildOutput 'sentinel.txt') -Raw) -cne 'must survive') {
        throw 'The explicit non-empty build output was modified.'
    }

    $validSource = Join-Path $testRoot 'windows-valid'
    New-TestWindowsSource $validSource $toolDirectory
    $validOutput = Join-Path $testRoot 'publisher-valid-output'
    & $publisher -Version '1.0.0' -WindowsBuild 100 -WindowsSource $validSource `
        -BaseUrl 'https://linjie.space/download/api/download?path={path}' -OutputRoot $validOutput `
        -PrivateKeyPath $privateKey -PublicKeyPath $publicKey -RolloutPercentage 0 | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $validOutput '1.0.0\manifest.json') -PathType Leaf)) {
        throw 'Valid stable publisher fixture did not produce a manifest.'
    }

    $wrongPackageApk = Join-Path $testRoot 'wrong-package.apk'
    New-TestAndroidApkWithWrongPackage $wrongPackageApk $repositoryRoot
    Assert-Throws {
        & $publisher -Version '1.0.0' -WindowsBuild 100 -WindowsSource $validSource `
            -BaseUrl 'https://linjie.space/download/api/download?path={path}' -OutputRoot (Join-Path $testRoot 'wrong-package-output') `
            -PrivateKeyPath $privateKey -PublicKeyPath $publicKey -AndroidApk $wrongPackageApk -AndroidBuild 100 `
            -AndroidSignerSha256 ('0' * 64)
    } "Android APK package name is 'com.example.not_tablink'; expected 'com.tablink.client'."

    $missingUpdaterSource = Join-Path $testRoot 'windows-missing-updater'
    New-TestWindowsSource $missingUpdaterSource $toolDirectory
    Remove-Item -LiteralPath (Join-Path $missingUpdaterSource 'TabLink.Updater.runtimeconfig.json')
    Assert-Throws {
        & $publisher -Version '1.0.0' -WindowsBuild 100 -WindowsSource $missingUpdaterSource `
            -BaseUrl 'https://linjie.space/download/api/download?path={path}' -OutputRoot (Join-Path $testRoot 'missing-updater-output') `
            -PrivateKeyPath $privateKey -PublicKeyPath $publicKey
    } "Required stable Windows package file 'TabLink.Updater.runtimeconfig.json'"

    $badChannelSource = Join-Path $testRoot 'windows-bad-channel'
    New-TestWindowsSource $badChannelSource $toolDirectory
    Write-Utf8NoBom (Join-Path $badChannelSource 'update-channel.json') '{"enabled":true,"manifestUrl":"https://wrong.example/stable.json"}'
    Assert-Throws {
        & $publisher -Version '1.0.0' -WindowsBuild 100 -WindowsSource $badChannelSource `
            -BaseUrl 'https://linjie.space/download/api/download?path={path}' -OutputRoot (Join-Path $testRoot 'bad-channel-output') `
            -PrivateKeyPath $privateKey -PublicKeyPath $publicKey
    } 'manifestUrl must exactly equal the formal stable URL'

    $tamperedChecksumSource = Join-Path $testRoot 'windows-tampered-checksum'
    New-TestWindowsSource $tamperedChecksumSource $toolDirectory
    [IO.File]::AppendAllText((Join-Path $tamperedChecksumSource 'TabLink.Updater.dll'), 'tampered')
    Assert-Throws {
        & $publisher -Version '1.0.0' -WindowsBuild 100 -WindowsSource $tamperedChecksumSource `
            -BaseUrl 'https://linjie.space/download/api/download?path={path}' -OutputRoot (Join-Path $testRoot 'tampered-checksum-output') `
            -PrivateKeyPath $privateKey -PublicKeyPath $publicKey
    } 'SHA256SUMS.txt hash mismatch'

    [pscustomobject]@{
        Build = 'PASS'
        SignVerify = 'PASS'
        LowercaseEnvelope = 'PASS'
        HttpsQueryInteroperability = 'PASS'
        TamperedPayloadRejected = 'PASS'
        TamperedSignatureRejected = 'PASS'
        MissingPrivateKeyRejected = 'PASS'
        ExistingTargetPreserved = 'PASS'
        HttpArtifactRejected = 'PASS'
        FractionalTimestampRejected = 'PASS'
        ZeroRolloutPauseAccepted = 'PASS'
        OutOfRangeRolloutRejected = 'PASS'
        NonEmptyBuildOutputRejected = 'PASS'
        CompletePublisherPreflightAccepted = 'PASS'
        WrongAndroidPackageRejected = 'PASS'
        MissingUpdaterRejected = 'PASS'
        WrongStableChannelRejected = 'PASS'
        TamperedChecksumRejected = 'PASS'
    } | Format-List
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedTestRoot.StartsWith($resolvedTemp + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($resolvedTestRoot).StartsWith('TabLink-ReleaseTool-Test-', [StringComparison]::Ordinal))) {
            throw "Refusing to clean unexpected test path: $resolvedTestRoot"
        }
        [IO.Directory]::Delete($resolvedTestRoot, $true)
    }
}
