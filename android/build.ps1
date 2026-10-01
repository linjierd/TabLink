param(
    [string]$JavaHome,
    [string]$AndroidSdk,
    [string]$Gradle,
    [string]$UpdateManifestUrl,
    [switch]$Offline,
    [switch]$ReleasePreview
)
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
if (-not $JavaHome) {
    $javaCandidates = @(
        'C:\Program Files\Android\openjdk\jdk-21.0.8',
        $env:JAVA_HOME,
        'C:\Program Files\Android\Android Studio1\jbr',
        'C:\Program Files\Android\Android Studio\jbr'
    )
    $JavaHome = $javaCandidates | Where-Object {
        $_ -and (Test-Path -LiteralPath (Join-Path $_ 'bin\javac.exe')) -and
        (Get-Content -LiteralPath (Join-Path $_ 'release') -Raw) -match 'JAVA_VERSION="(17|21)\.'
    } | Select-Object -First 1
}
if (-not $JavaHome) { throw 'Specify -JavaHome with a JDK 17 or JDK 21 installation.' }
if (-not $AndroidSdk -and (Test-Path -LiteralPath (Join-Path $projectDirectory '.tools\sdk\build-tools\35.0.0\aapt2.exe'))) {
    $AndroidSdk = Join-Path $projectDirectory '.tools\sdk'
}
if (-not $AndroidSdk) { $AndroidSdk = $env:ANDROID_HOME }
if (-not $AndroidSdk) { $AndroidSdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
if (-not (Test-Path -LiteralPath (Join-Path $AndroidSdk 'platforms\android-35\android.jar'))) {
    throw "Android SDK platform 35 was not found in: $AndroidSdk"
}
if (-not (Test-Path -LiteralPath (Join-Path $AndroidSdk 'build-tools\35.0.0\aapt2.exe'))) {
    throw "Android SDK build-tools 35.0.0 is incomplete in: $AndroidSdk"
}
if (-not $Gradle) {
    $localGradle = Join-Path $projectDirectory '.tools\gradle-8.13\gradle-8.13\bin\gradle.bat'
    if (Test-Path -LiteralPath $localGradle) { $Gradle = $localGradle }
}
if (-not $Gradle) {
    $gradleRoot = Join-Path $env:USERPROFILE '.gradle\wrapper\dists\gradle-8.13-bin'
    if (Test-Path -LiteralPath $gradleRoot) {
        $Gradle = Get-ChildItem -LiteralPath $gradleRoot -Filter gradle.bat -File -Recurse | Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $Gradle) { throw 'Specify -Gradle with the path to Gradle 8.13 bin\gradle.bat.' }
$savedJavaHome = $env:JAVA_HOME
$savedAndroidHome = $env:ANDROID_HOME
$savedAndroidSdkRoot = $env:ANDROID_SDK_ROOT
$savedAndroidUserHome = $env:ANDROID_USER_HOME
$savedGradleUserHome = $env:GRADLE_USER_HOME
$savedTemp = $env:TEMP
$savedTmp = $env:TMP
try {
    $toolStateRoot = Join-Path $projectDirectory '.tools'
    $androidUserHome = Join-Path $toolStateRoot 'android-user-home'
    $gradleUserHome = Join-Path $toolStateRoot 'gradle-user-home'
    $temporaryDirectory = Join-Path $toolStateRoot 'tmp'
    New-Item -ItemType Directory -Force -Path $androidUserHome,$gradleUserHome,$temporaryDirectory | Out-Null
    $env:JAVA_HOME = $JavaHome
    $env:ANDROID_HOME = $AndroidSdk
    $env:ANDROID_SDK_ROOT = $AndroidSdk
    $env:ANDROID_USER_HOME = $androidUserHome
    $env:GRADLE_USER_HOME = $gradleUserHome
    $env:TEMP = $temporaryDirectory
    $env:TMP = $temporaryDirectory
    $signingDirectory = Join-Path $projectDirectory 'build\signing'
    New-Item -ItemType Directory -Force -Path $signingDirectory | Out-Null
    $keyStore = Join-Path $signingDirectory 'debug.keystore'
    if (-not (Test-Path -LiteralPath $keyStore)) {
        & (Join-Path $JavaHome 'bin\keytool.exe') -genkeypair -keystore $keyStore -storepass android -alias androiddebugkey -keypass android -keyalg RSA -keysize 2048 -validity 10000 -dname 'CN=TabLink Development, O=TabLink, C=CN'
        if ($LASTEXITCODE -ne 0) { throw 'Could not generate development signing key.' }
    }
    $testDirectory = Join-Path $projectDirectory 'build\protocol-tests'
    New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
    $sources = @(
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\WireProtocol.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\FrameGeometry.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\PresentationProgress.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\SubmissionProgress.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\VideoAccessUnit.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\VideoFrameQueue.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\FrameRateMeter.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\RenderClock.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\HudStyle.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\CapturePauseState.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\StreamingBrightnessPolicy.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\DecoderCandidateSelector.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\DecoderRefreshRequest.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\KeyFrameRequestController.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\PendingDecoderRefresh.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\ReceiverFeedbackProgress.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\PairingLink.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\PinnedTls.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\ReleaseManifest.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\ArtifactIntegrity.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\UpdateStateMachine.java'),
        (Join-Path $projectDirectory 'app\src\main\java\com\tablink\client\QrCodeDecoder.java'),
        (Join-Path $projectDirectory 'tests\ProtocolSmokeTest.java'),
        (Join-Path $projectDirectory 'tests\VideoFrameQueueTest.java'),
        (Join-Path $projectDirectory 'tests\KeyFrameRequestControllerTest.java'),
        (Join-Path $projectDirectory 'tests\PendingDecoderRefreshTest.java'),
        (Join-Path $projectDirectory 'tests\ReceiverFeedbackProgressTest.java'),
        (Join-Path $projectDirectory 'tests\RenderClockTest.java'),
        (Join-Path $projectDirectory 'tests\DecoderCandidateSelectorTest.java'),
        (Join-Path $projectDirectory 'tests\DisplayUiPolicyTest.java'),
        (Join-Path $projectDirectory 'tests\PairingSecurityTest.java'),
        (Join-Path $projectDirectory 'tests\StableUpdateSecurityTest.java')
    )
    $zxingDirectory = Join-Path $projectDirectory '.tools\dependencies'
    $zxingJar = Join-Path $zxingDirectory 'zxing-core-3.5.3.jar'
    $zxingHash = '8D8064C1636FDAEF7189DD9055C7D59950A8940A12F2293956446EC3C109FD82'
    if (-not (Test-Path -LiteralPath $zxingJar)) {
        if ($Offline) { throw 'ZXing core 3.5.3 is not cached. Run once without -Offline.' }
        New-Item -ItemType Directory -Force -Path $zxingDirectory | Out-Null
        $download = $zxingJar + '.download'
        try {
            Invoke-WebRequest -Uri 'https://repo.maven.apache.org/maven2/com/google/zxing/core/3.5.3/core-3.5.3.jar' -OutFile $download
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $zxingHash) {
                throw 'Downloaded ZXing dependency checksum mismatch.'
            }
            Move-Item -LiteralPath $download -Destination $zxingJar
        }
        finally {
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
        }
    }
    if ((Get-FileHash -LiteralPath $zxingJar -Algorithm SHA256).Hash -ne $zxingHash) {
        throw 'Cached ZXing dependency checksum mismatch.'
    }
    & (Join-Path $JavaHome 'bin\javac.exe') -encoding UTF-8 -cp $zxingJar -d $testDirectory @sources
    if ($LASTEXITCODE -ne 0) { throw 'Protocol test compilation failed.' }
    & (Join-Path $JavaHome 'bin\java.exe') -cp $testDirectory com.tablink.client.ProtocolSmokeTest
    if ($LASTEXITCODE -ne 0) { throw 'Protocol or coordinate test failed.' }
    & (Join-Path $JavaHome 'bin\java.exe') -cp $testDirectory com.tablink.client.VideoFrameQueueTest
    if ($LASTEXITCODE -ne 0) { throw 'Bounded video input queue test failed.' }
    & (Join-Path $JavaHome 'bin\java.exe') -cp $testDirectory com.tablink.client.KeyFrameRequestControllerTest
    if ($LASTEXITCODE -ne 0) { throw 'Key-frame request limiter test failed.' }
    & (Join-Path $JavaHome 'bin\java.exe') -cp $testDirectory com.tablink.client.PendingDecoderRefreshTest
    if ($LASTEXITCODE -ne 0) { throw 'Pending decoder refresh race test failed.' }
    & (Join-Path $JavaHome 'bin\java.exe') -cp $testDirectory com.tablink.client.ReceiverFeedbackProgressTest
    if ($LASTEXITCODE -ne 0) { throw 'Receiver feedback test failed.' }
    & (Join-Path $JavaHome 'bin\java.exe') -cp $testDirectory com.tablink.client.RenderClockTest
    if ($LASTEXITCODE -ne 0) { throw 'Bounded render clock test failed.' }
    & (Join-Path $JavaHome 'bin\java.exe') -cp $testDirectory com.tablink.client.DecoderCandidateSelectorTest
    if ($LASTEXITCODE -ne 0) { throw 'Decoder candidate scoring test failed.' }
    & (Join-Path $JavaHome 'bin\java.exe') -cp $testDirectory com.tablink.client.DisplayUiPolicyTest
    if ($LASTEXITCODE -ne 0) { throw 'HUD or capture pause policy test failed.' }
    & (Join-Path $JavaHome 'bin\java.exe') -cp "$testDirectory;$zxingJar" com.tablink.client.PairingSecurityTest $keyStore
    if ($LASTEXITCODE -ne 0) { throw 'Pairing, QR or certificate pinning test failed.' }
    $updatePayloadFixture = Join-Path $projectDirectory 'tests\fixtures\stable-manifest-payload.json'
    $updateEnvelopeFixture = Join-Path $projectDirectory 'tests\fixtures\stable-manifest-valid.json'
    if (-not (Test-Path -LiteralPath $updatePayloadFixture) -or -not (Test-Path -LiteralPath $updateEnvelopeFixture)) {
        throw 'Stable update signature fixtures are missing.'
    }
    & (Join-Path $JavaHome 'bin\java.exe') -cp $testDirectory com.tablink.client.StableUpdateSecurityTest $updatePayloadFixture $updateEnvelopeFixture
    if ($LASTEXITCODE -ne 0) { throw 'Stable update security or state test failed.' }
    $gradleTasks = if ($ReleasePreview) { @('assembleRelease','lintRelease') } else { @('assembleDebug','lintDebug') }
    $gradleArguments = @('--project-dir', $projectDirectory, '--console=plain', '--no-daemon') + $gradleTasks
    if ($ReleasePreview) { $gradleArguments += '-PtablinkPreviewSigning=true' }
    if ($UpdateManifestUrl) {
        if (-not $UpdateManifestUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'UpdateManifestUrl must use HTTPS.'
        }
        $gradleArguments += "-PtablinkUpdateManifestUrl=$UpdateManifestUrl"
    }
    if ($Offline) { $gradleArguments += '--offline' }
    & $Gradle @gradleArguments
    if ($LASTEXITCODE -ne 0) { throw 'Android build or lint failed.' }
    $artifactDirectory = Join-Path $projectDirectory 'artifacts'
    New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
    $apk = Join-Path $artifactDirectory $(if ($ReleasePreview) { 'TabLink-android-0.8.3-preview.apk' } else { 'TabLink-android-0.8.3-debug.apk' })
    $builtApk = if ($ReleasePreview) { 'app\build\outputs\apk\release\app-release.apk' } else { 'app\build\outputs\apk\debug\app-debug.apk' }
    Copy-Item -LiteralPath (Join-Path $projectDirectory $builtApk) -Destination $apk -Force
    & (Join-Path $JavaHome 'bin\java.exe') -jar (Join-Path $AndroidSdk 'build-tools\35.0.0\lib\apksigner.jar') verify --verbose --min-sdk-version 23 $apk
    if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
    Get-FileHash -Algorithm SHA256 -LiteralPath $apk | Format-List Algorithm, Hash, Path
} finally {
    $env:JAVA_HOME = $savedJavaHome
    $env:ANDROID_HOME = $savedAndroidHome
    $env:ANDROID_SDK_ROOT = $savedAndroidSdkRoot
    $env:ANDROID_USER_HOME = $savedAndroidUserHome
    $env:GRADLE_USER_HOME = $savedGradleUserHome
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
}
