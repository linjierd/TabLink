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
$apkPath = Join-Path $projectRoot $(if ($PublicRelease) { 'android\artifacts\TabLink-android-0.8.7-preview.apk' } else { 'android\artifacts\TabLink-android-0.8.7-debug.apk' })
$ffmpegRoot = Join-Path $projectRoot 'third_party\ffmpeg-tablink'
$ffmpegHardwareBinary = Join-Path $ffmpegRoot 'bin\ffmpeg.exe'
$ffmpegSoftwareBinary = Join-Path $ffmpegRoot 'bin\ffmpeg-x264.exe'
$ffmpegSourceBundle = Join-Path $ffmpegRoot 'source-bundle.tar.gz'
$ffmpegChecksums = Join-Path $ffmpegRoot 'SHA256SUMS'
if (-not (Test-Path -LiteralPath $ffmpegHardwareBinary -PathType Leaf)) { throw 'Build the verified TabLink hardware FFmpeg component first.' }
if (-not (Test-Path -LiteralPath $ffmpegSoftwareBinary -PathType Leaf)) { throw 'Build the verified TabLink libx264 FFmpeg component first.' }
if (-not (Test-Path -LiteralPath (Join-Path $ffmpegRoot 'README.md'))) { throw 'FFmpeg source notice missing.' }
if (-not (Test-Path -LiteralPath $ffmpegSourceBundle)) { throw 'FFmpeg corresponding source bundle missing.' }
$ffmpegSourceManifest = Join-Path $ffmpegRoot 'SOURCE-BUNDLE-MANIFEST.json'
if (-not (Test-Path -LiteralPath $ffmpegSourceManifest -PathType Leaf)) { throw 'FFmpeg corresponding-source manifest missing.' }
foreach ($licenseName in @(
    'COPYING.LGPLv2.1',
    'COPYING.GPLv2',
    'COPYING.MinGW-w64-runtime.txt',
    'COPYING.NVIDIA.txt',
    'COPYING.oneVPL.txt',
    'COPYING.oneVPL-third-party-programs.txt',
    'COPYING.AMF.txt',
    'COPYING.x264.txt'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $ffmpegRoot ('bin\' + $licenseName)) -PathType Leaf)) {
        throw "FFmpeg distribution license or notice missing: bin\$licenseName"
    }
}

function Assert-FfmpegArtifactsMatchChecksums {
    param(
        [Parameter(Mandatory = $true)][string]$ChecksumPath,
        [Parameter(Mandatory = $true)][string]$HardwareBinaryPath,
        [Parameter(Mandatory = $true)][string]$SoftwareBinaryPath,
        [Parameter(Mandatory = $true)][string]$SourceBundlePath
    )

    if (-not (Test-Path -LiteralPath $ChecksumPath -PathType Leaf)) {
        throw 'FFmpeg SHA256SUMS is missing.'
    }

    $artifactPaths = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $artifactPaths.Add('bin\ffmpeg.exe', $HardwareBinaryPath)
    $artifactPaths.Add('bin\ffmpeg-x264.exe', $SoftwareBinaryPath)
    $artifactPaths.Add('source-bundle.tar.gz', $SourceBundlePath)
    $declaredHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $checksumLines = @(Get-Content -LiteralPath $ChecksumPath)
    if ($checksumLines.Count -ne $artifactPaths.Count) {
        throw 'FFmpeg SHA256SUMS must contain exactly the two binaries and corresponding source bundle entries.'
    }

    foreach ($line in $checksumLines) {
        $checksumMatch = [regex]::Match($line, '^(?<Hash>[0-9A-Fa-f]{64})  (?<Path>[^\r\n]+)$')
        if (-not $checksumMatch.Success) {
            throw "FFmpeg SHA256SUMS contains a malformed line: $line"
        }
        $entryPath = $checksumMatch.Groups['Path'].Value
        if (-not $artifactPaths.ContainsKey($entryPath)) {
            throw "FFmpeg SHA256SUMS contains an unexpected entry: $entryPath"
        }
        if ($declaredHashes.ContainsKey($entryPath)) {
            throw "FFmpeg SHA256SUMS contains a duplicate entry: $entryPath"
        }
        $declaredHashes.Add($entryPath, $checksumMatch.Groups['Hash'].Value)
    }

    foreach ($entryPath in $artifactPaths.Keys) {
        if (-not $declaredHashes.ContainsKey($entryPath)) {
            throw "FFmpeg SHA256SUMS is missing the required entry: $entryPath"
        }
        $actualHash = (Get-FileHash -LiteralPath $artifactPaths[$entryPath] -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $declaredHashes[$entryPath], [StringComparison]::OrdinalIgnoreCase)) {
            throw "FFmpeg artifact does not match SHA256SUMS: $entryPath"
        }
    }
}

function Assert-FfmpegHelpersMatchSourceManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$HardwareBinaryPath,
        [Parameter(Mandatory = $true)][string]$SoftwareBinaryPath
    )

    try {
        $manifestDocument = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw 'FFmpeg SOURCE-BUNDLE-MANIFEST.json is not valid JSON; helper hashes cannot be verified.'
    }
    if ($null -eq $manifestDocument -or $manifestDocument -isnot [PSCustomObject]) {
        throw 'FFmpeg SOURCE-BUNDLE-MANIFEST.json must contain a JSON object; helper hashes cannot be verified.'
    }

    $expectedHelpers = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $expectedHelpers.Add('ffmpeg.exe', $HardwareBinaryPath)
    $expectedHelpers.Add('ffmpeg-x264.exe', $SoftwareBinaryPath)
    $declaredHelpers = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $helpers = @($manifestDocument.helpers)
    if ($helpers.Count -ne $expectedHelpers.Count) {
        throw 'FFmpeg SOURCE-BUNDLE-MANIFEST.json helpers must contain exactly ffmpeg.exe and ffmpeg-x264.exe.'
    }

    foreach ($helper in $helpers) {
        if ($null -eq $helper -or $helper -isnot [PSCustomObject]) {
            throw 'FFmpeg SOURCE-BUNDLE-MANIFEST.json contains an invalid helpers entry.'
        }
        $helperFile = [string]$helper.file
        if ([string]::IsNullOrWhiteSpace($helperFile) -or -not $expectedHelpers.ContainsKey($helperFile)) {
            throw "FFmpeg SOURCE-BUNDLE-MANIFEST.json contains an unexpected helper file: $helperFile"
        }
        if (-not $declaredHelpers.Add($helperFile)) {
            throw "FFmpeg SOURCE-BUNDLE-MANIFEST.json contains a duplicate helper entry: $helperFile"
        }

        $declaredHash = [string]$helper.sha256
        if ($declaredHash -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "FFmpeg SOURCE-BUNDLE-MANIFEST.json helper $helperFile has a missing or malformed SHA-256 value."
        }
        $actualHash = (Get-FileHash -LiteralPath $expectedHelpers[$helperFile] -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $declaredHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "FFmpeg helper does not match SOURCE-BUNDLE-MANIFEST.json: $helperFile (declared $declaredHash; actual $actualHash)."
        }
    }

    foreach ($helperFile in $expectedHelpers.Keys) {
        if (-not $declaredHelpers.Contains($helperFile)) {
            throw "FFmpeg SOURCE-BUNDLE-MANIFEST.json is missing the required helper entry: $helperFile"
        }
    }
}

function Invoke-FfmpegAuditCommand {
    param(
        [Parameter(Mandatory = $true)][string]$BinaryPath,
        [Parameter(Mandatory = $true)][string]$Argument,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $output = @(& $BinaryPath $Argument 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) {
        throw "$Description FFmpeg failed its $Argument audit."
    }
    return ($output -join "`n")
}

function Assert-FfmpegBinarySafe {
    param(
        [Parameter(Mandatory = $true)][string]$BinaryPath,
        [Parameter(Mandatory = $true)][ValidateSet('Hardware','Software')][string]$Flavor
    )

    $description = if ($Flavor -eq 'Hardware') { 'Hardware' } else { 'libx264' }
    $version = Invoke-FfmpegAuditCommand -BinaryPath $BinaryPath -Argument '-version' -Description $description
    $buildConfiguration = Invoke-FfmpegAuditCommand -BinaryPath $BinaryPath -Argument '-buildconf' -Description $description
    $encoders = Invoke-FfmpegAuditCommand -BinaryPath $BinaryPath -Argument '-encoders' -Description $description
    $protocols = Invoke-FfmpegAuditCommand -BinaryPath $BinaryPath -Argument '-protocols' -Description $description
    $license = Invoke-FfmpegAuditCommand -BinaryPath $BinaryPath -Argument '-L' -Description $description

    $personalPathPattern = '(?i)(?:[A-Z]:[\\/]+Users[\\/]|/[A-Z]/Users/)'
    $binaryBytes = [IO.File]::ReadAllBytes($BinaryPath)
    $binaryViews = @(
        [Text.Encoding]::ASCII.GetString($binaryBytes),
        [Text.Encoding]::Unicode.GetString($binaryBytes),
        [Text.Encoding]::BigEndianUnicode.GetString($binaryBytes)
    )
    if ($null -ne ($binaryViews | Where-Object { $_ -match $personalPathPattern } | Select-Object -First 1)) {
        throw "$description FFmpeg exposes a builder-specific personal Users path."
    }
    foreach ($auditOutput in @($version, $buildConfiguration, $encoders, $protocols, $license)) {
        if ($auditOutput -match $personalPathPattern) {
            throw "$description FFmpeg audit output exposes a builder-specific personal Users path."
        }
    }

    $versionFirstLine = @($version -split "`r?`n")[0]
    $expectedVersionPrefix = if ($Flavor -eq 'Hardware') {
        'ffmpeg version 7.0.2-tablink-085-hardware2 '
    }
    else {
        'ffmpeg version 7.0.2-tablink-085-libx264-2 '
    }
    if (-not $versionFirstLine.StartsWith($expectedVersionPrefix, [StringComparison]::Ordinal)) {
        throw "$description FFmpeg does not identify as the pinned TabLink FFmpeg 7.0.2 build ($expectedVersionPrefix)."
    }

    $configurationOptions = @(
        foreach ($line in ($buildConfiguration -split "`r?`n")) {
            $option = $line.Trim().Replace("'", '').Replace('"', '')
            if ($option.StartsWith('--', [StringComparison]::Ordinal)) { $option }
        }
    )
    $configurationOptionSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($option in $configurationOptions) { [void]$configurationOptionSet.Add($option) }
    $expectedPrefix = if ($Flavor -eq 'Hardware') { '--prefix=/ffmpeg-tablink-085-hardware' } else { '--prefix=/ffmpeg-tablink-085-software' }
    foreach ($requiredOption in @(
        $expectedPrefix,
        '--disable-everything',
        '--disable-autodetect',
        '--disable-network',
        '--disable-shared',
        '--enable-static',
        '--enable-ffmpeg',
        '--enable-protocol=file,pipe',
        '--enable-parser=h264',
        '--enable-bsf=h264_metadata'
    )) {
        if (-not $configurationOptionSet.Contains($requiredOption)) {
            throw "$description FFmpeg build configuration is missing the required option: $requiredOption"
        }
    }

    $encoderNames = @(
        foreach ($line in ($encoders -split "`r?`n")) {
            # FFmpeg uses the same six-column flag shape for legend rows such
            # as `V..... = Video`. Requiring an identifier here prevents the
            # legend's `=` token from being audited as an encoder name.
            $encoderMatch = [regex]::Match($line, '^\s*[VAS\.FSCXBD]{6}\s+(?<Name>[A-Za-z0-9][A-Za-z0-9_.-]*)')
            if ($encoderMatch.Success) { $encoderMatch.Groups['Name'].Value }
        }
    )
    $encoderSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($encoderName in $encoderNames) { [void]$encoderSet.Add($encoderName) }

    if ($Flavor -eq 'Hardware') {
        foreach ($requiredOption in @('--enable-ffnvcodec','--enable-nvenc','--enable-libvpl','--enable-amf')) {
            if (-not $configurationOptionSet.Contains($requiredOption)) {
                throw "Hardware FFmpeg build configuration is missing the required option: $requiredOption"
            }
        }
        foreach ($forbiddenOption in @('--enable-gpl','--enable-nonfree','--enable-libx264')) {
            if ($configurationOptionSet.Contains($forbiddenOption)) {
                throw "Hardware FFmpeg build configuration contains a forbidden option: $forbiddenOption"
            }
        }
        foreach ($requiredEncoder in @('h264_nvenc','h264_qsv','h264_amf')) {
            if (-not $encoderSet.Contains($requiredEncoder)) {
                throw "Hardware FFmpeg is missing the required encoder: $requiredEncoder"
            }
        }
        if ($encoderSet.Contains('libx264') -or $encoderSet.Contains('libx264rgb')) {
            throw 'Hardware FFmpeg unexpectedly contains a libx264 encoder.'
        }
        if ($license -notmatch 'GNU Lesser General Public\s+License' -or $license -match 'under the terms of the GNU General Public\s+License') {
            throw 'Hardware FFmpeg did not report the required LGPL-only license.'
        }
    }
    else {
        foreach ($requiredOption in @('--enable-gpl','--enable-libx264')) {
            if (-not $configurationOptionSet.Contains($requiredOption)) {
                throw "libx264 FFmpeg build configuration is missing the required option: $requiredOption"
            }
        }
        if ($configurationOptionSet.Contains('--enable-nonfree')) {
            throw 'libx264 FFmpeg build configuration contains the forbidden option: --enable-nonfree'
        }
        if (-not $encoderSet.Contains('libx264')) {
            throw 'libx264 FFmpeg is missing the required libx264 encoder.'
        }
        foreach ($forbiddenEncoder in @('h264_nvenc','h264_qsv','h264_amf')) {
            if ($encoderSet.Contains($forbiddenEncoder)) {
                throw "libx264 FFmpeg unexpectedly contains a hardware encoder: $forbiddenEncoder"
            }
        }
        if ($license -notmatch 'under the terms of the GNU General Public\s+License' -or $license -match 'GNU Lesser General Public\s+License') {
            throw 'libx264 FFmpeg did not report the required GPL license.'
        }
    }

    $expectedEncoders = if ($Flavor -eq 'Hardware') {
        @('h264_amf','h264_nvenc','h264_qsv','rawvideo','wrapped_avframe')
    }
    else {
        @('libx264','rawvideo','wrapped_avframe')
    }
    $unexpectedEncoders = @($encoderNames | Where-Object { $_ -notin $expectedEncoders } | Sort-Object -Unique)
    $missingEncoders = @($expectedEncoders | Where-Object { -not $encoderSet.Contains($_) })
    if ($unexpectedEncoders.Count -ne 0 -or $missingEncoders.Count -ne 0) {
        throw "$description FFmpeg encoder surface does not match its release contract. Missing: $($missingEncoders -join ', '); unexpected: $($unexpectedEncoders -join ', ')."
    }

    $protocolNames = @(
        foreach ($line in ($protocols -split "`r?`n")) {
            if ($line -match '^\s{2}(?<Name>[A-Za-z0-9+._-]+)\s*$') { $Matches['Name'] }
        }
    )
    $unexpectedProtocols = @($protocolNames | Where-Object { $_ -notin @('file','pipe') } | Sort-Object -Unique)
    $missingProtocols = @('file','pipe') | Where-Object { $_ -notin $protocolNames }
    if ($unexpectedProtocols.Count -ne 0 -or $missingProtocols.Count -ne 0) {
        throw "$description FFmpeg protocol surface must be exactly file and pipe. Missing: $($missingProtocols -join ', '); unexpected: $($unexpectedProtocols -join ', ')."
    }
}

function Assert-FfmpegSourceBundleSafe {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$ManifestPath
    )

    $auditRoot = Join-Path $projectRoot ('.ffmpeg-source-audit-' + [Guid]::NewGuid().ToString('N'))
    try {
        $entries = @(& tar -tzf $BundlePath 2>&1 | ForEach-Object { $_.ToString() })
        if ($LASTEXITCODE -ne 0) { throw 'Unable to list the FFmpeg source bundle.' }
        if ($entries.Count -eq 0) { throw 'FFmpeg source bundle is empty.' }

        $normalizedEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $entries) {
            $normalizedEntry = $entry -replace '\\', '/'
            $entryParts = @($normalizedEntry -split '/')
            if ($normalizedEntry -match '^(?:/|[A-Za-z]:/)' -or $entryParts -contains '..') {
                throw "FFmpeg source bundle contains an unsafe path: $entry"
            }
            $candidatePath = [IO.Path]::GetFullPath((Join-Path $auditRoot $normalizedEntry))
            $auditPrefix = $auditRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            if ($candidatePath -ne $auditRoot -and -not $candidatePath.StartsWith($auditPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "FFmpeg source bundle path escapes the audit directory: $entry"
            }
            if (-not $normalizedEntries.Add($normalizedEntry)) {
                throw "FFmpeg source bundle contains a duplicate entry: $entry"
            }
            if ($normalizedEntry -match '(?i)(?:^|/)\.git(?:/|$)') {
                throw "FFmpeg source bundle contains forbidden Git metadata: $entry"
            }
            if ($normalizedEntry -match '(?i)^ffmpeg-tablink/(?:build|toolchain|downloads)(?:/|$)' -or
                $normalizedEntry -match '(?i)\.(?:exe|dll|pdb|obj|o|a|lib|exp|ilk|pyc)(?:/)?$' -or
                $normalizedEntry -match '(?i)(?:^|/)__pycache__(?:/|$)') {
                throw "FFmpeg source bundle contains a build output: $entry"
            }
            if ($normalizedEntry -match '(?i)^ffmpeg-tablink/bin/' -and
                $normalizedEntry -notmatch '(?i)^ffmpeg-tablink/bin/?$' -and
                $normalizedEntry -notmatch '(?i)^ffmpeg-tablink/bin/COPYING[^/]*$') {
                throw "FFmpeg source bundle bin directory contains a non-license artifact: $entry"
            }
        }

        $requiredEntries = @(
            'ffmpeg-tablink/source/ffmpeg-7.0.2/',
            'ffmpeg-tablink/source/ffmpeg-7.0.2/configure',
            'ffmpeg-tablink/source/ffmpeg-7.0.2/COPYING.LGPLv2.1',
            'ffmpeg-tablink/source/ffmpeg-7.0.2/COPYING.GPLv2',
            'ffmpeg-tablink/source/nv-codec-headers-n12.2.72.0/',
            'ffmpeg-tablink/source/nv-codec-headers-n12.2.72.0/include/ffnvcodec/nvEncodeAPI.h',
            'ffmpeg-tablink/source/oneVPL-2.11.0/',
            'ffmpeg-tablink/source/oneVPL-2.11.0/api/vpl/mfxvideo.h',
            'ffmpeg-tablink/source/oneVPL-2.11.0/LICENSE',
            'ffmpeg-tablink/source/oneVPL-2.11.0/third-party-programs.txt',
            'ffmpeg-tablink/source/AMF-1.4.35/',
            'ffmpeg-tablink/source/AMF-1.4.35/amf/public/include/core/Factory.h',
            'ffmpeg-tablink/source/AMF-1.4.35/LICENSE.txt',
            'ffmpeg-tablink/source/x264-b35605ace3dd/',
            'ffmpeg-tablink/source/x264-b35605ace3dd/x264.h',
            'ffmpeg-tablink/source/x264-b35605ace3dd/COPYING',
            'ffmpeg-tablink/0001-windows-private-high-resolution-usleep.patch',
            'ffmpeg-tablink/0002-ddagrab-nonblocking-duplicate.patch',
            'ffmpeg-tablink/README.md',
            'ffmpeg-tablink/build.ps1',
            'ffmpeg-tablink/build.sh',
            'ffmpeg-tablink/New-SourceBundle.ps1',
            'ffmpeg-tablink/prepare-toolchain.ps1',
            'ffmpeg-tablink/downloads-manifest.json',
            'ffmpeg-tablink/SOURCE-BUNDLE-MANIFEST.json',
            'ffmpeg-tablink/bin/COPYING.LGPLv2.1',
            'ffmpeg-tablink/bin/COPYING.GPLv2',
            'ffmpeg-tablink/bin/COPYING.MinGW-w64-runtime.txt',
            'ffmpeg-tablink/bin/COPYING.NVIDIA.txt',
            'ffmpeg-tablink/bin/COPYING.oneVPL.txt',
            'ffmpeg-tablink/bin/COPYING.oneVPL-third-party-programs.txt',
            'ffmpeg-tablink/bin/COPYING.AMF.txt',
            'ffmpeg-tablink/bin/COPYING.x264.txt',
            'ffmpeg-tablink/signature-verification.log',
            'ffmpeg-tablink/production-parser-test.log',
            'ffmpeg-tablink/NATIVE-MOTION-VALIDATION.md',
            'ffmpeg-tablink/motion-085-final-result.json',
            'ffmpeg-tablink/VALIDATION.md'
        )
        foreach ($requiredEntry in $requiredEntries) {
            if (-not $normalizedEntries.Contains($requiredEntry)) {
                throw "FFmpeg source bundle is missing a required corresponding-source entry: $requiredEntry"
            }
        }

        $entryDetails = @(& tar -tvzf $BundlePath 2>&1 | ForEach-Object { $_.ToString() })
        if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect FFmpeg source bundle entry types.' }
        if ($null -ne ($entryDetails | Where-Object { $_ -notmatch '^[-d]' } | Select-Object -First 1)) {
            throw 'FFmpeg source bundle contains a link or unsupported entry type.'
        }

        New-Item -ItemType Directory -Path $auditRoot | Out-Null
        & tar -xzf $BundlePath -C $auditRoot
        if ($LASTEXITCODE -ne 0) { throw 'Unable to extract the FFmpeg source bundle for privacy audit.' }

        $bundledManifestPath = Join-Path $auditRoot 'ffmpeg-tablink\SOURCE-BUNDLE-MANIFEST.json'
        $externalManifestHash = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash
        $bundledManifestHash = (Get-FileHash -LiteralPath $bundledManifestPath -Algorithm SHA256).Hash
        if (-not [string]::Equals($externalManifestHash, $bundledManifestHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The published FFmpeg source manifest does not match the manifest inside the source bundle.'
        }
        try {
            $manifestDocument = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        }
        catch {
            throw 'FFmpeg SOURCE-BUNDLE-MANIFEST.json is not valid JSON.'
        }
        if ($null -eq $manifestDocument -or $manifestDocument -isnot [PSCustomObject]) {
            throw 'FFmpeg SOURCE-BUNDLE-MANIFEST.json must contain a JSON object.'
        }

        $personalPathPattern = '(?i)(?:[A-Z]:[\\/]+Users[\\/]|/[A-Z]/Users/)'
        foreach ($file in Get-ChildItem -LiteralPath $auditRoot -File -Recurse -Force) {
            $relativePath = [IO.Path]::GetRelativePath($auditRoot, $file.FullName)
            if ($relativePath -match $personalPathPattern) {
                throw "FFmpeg source bundle contains a personal path in an entry name: $relativePath"
            }
            $contentBytes = [IO.File]::ReadAllBytes($file.FullName)
            $contentViews = @(
                [Text.Encoding]::ASCII.GetString($contentBytes),
                [Text.Encoding]::Unicode.GetString($contentBytes),
                [Text.Encoding]::BigEndianUnicode.GetString($contentBytes)
            )
            if ($null -ne ($contentViews | Where-Object { $_ -match $personalPathPattern } | Select-Object -First 1)) {
                throw "FFmpeg source bundle contains a personal Users path in: $relativePath"
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $auditRoot) {
            Remove-Item -LiteralPath $auditRoot -Recurse -Force
        }
    }
}

if ($PublicRelease) {
    Assert-FfmpegArtifactsMatchChecksums -ChecksumPath $ffmpegChecksums `
        -HardwareBinaryPath $ffmpegHardwareBinary -SoftwareBinaryPath $ffmpegSoftwareBinary `
        -SourceBundlePath $ffmpegSourceBundle
    Assert-FfmpegHelpersMatchSourceManifest -ManifestPath $ffmpegSourceManifest `
        -HardwareBinaryPath $ffmpegHardwareBinary -SoftwareBinaryPath $ffmpegSoftwareBinary
    Assert-FfmpegBinarySafe -BinaryPath $ffmpegHardwareBinary -Flavor Hardware
    Assert-FfmpegBinarySafe -BinaryPath $ffmpegSoftwareBinary -Flavor Software
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
    'TabLink.UsbRecovery.Tests',
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
Copy-Item -LiteralPath $ffmpegHardwareBinary -Destination $ffmpegOutput
Copy-Item -LiteralPath $ffmpegSoftwareBinary -Destination $ffmpegOutput
Get-ChildItem -LiteralPath (Join-Path $ffmpegRoot 'bin') -Filter 'COPYING*' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $ffmpegOutput }
if ($PublicRelease) {
    Assert-FfmpegSourceBundleSafe -BundlePath $ffmpegSourceBundle -ManifestPath $ffmpegSourceManifest
}
foreach ($name in @('README.md','SHA256SUMS','source-bundle.tar.gz','SOURCE-BUNDLE-MANIFEST.json','0001-windows-private-high-resolution-usleep.patch','0002-ddagrab-nonblocking-duplicate.patch','downloads-manifest.json')) {
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
Copy-Item -LiteralPath (Join-Path $projectRoot 'RELEASE-0.8.7.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'VERIFICATION-0.8.7.md') -Destination $publishRoot
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
