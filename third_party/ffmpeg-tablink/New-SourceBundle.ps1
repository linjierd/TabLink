param(
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetPathRoot($PSScriptRoot)) `
        ('TabLink-source-bundle-' + [Guid]::NewGuid().ToString('N'))),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'source-bundle.tar.gz'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$bundleSourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$workRoot = [IO.Path]::GetFullPath($WorkDirectory)
$outputFile = [IO.Path]::GetFullPath($OutputPath)
$sourceDrive = [IO.Path]::GetPathRoot($bundleSourceRoot)
$workDrive = [IO.Path]::GetPathRoot($workRoot)
$outputDrive = [IO.Path]::GetPathRoot($outputFile)

function Assert-SameDrive {
    param(
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Actual,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not [string]::Equals($Expected, $Actual, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must stay on the same drive as the FFmpeg source bundle."
    }
}

function Test-ContainedPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Candidate
    )

    $parentWithSeparator = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidateFullPath = [IO.Path]::GetFullPath($Candidate)
    return $candidateFullPath.StartsWith($parentWithSeparator, [StringComparison]::OrdinalIgnoreCase)
}

function Copy-FilteredSourceTree {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Missing source directory: $Source"
    }

    $excludedDirectories = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    @(
        '.git', '.hg', '.svn', '.vs', '.idea', '.cache', '.deps', '.libs',
        '__pycache__', '_build', 'build', 'CMakeFiles', 'Testing', 'out', 'dist',
        'artifacts'
    ) | ForEach-Object { [void]$excludedDirectories.Add($_) }

    $excludedExtensions = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    @(
        '.o', '.obj', '.a', '.lib', '.dll', '.exe', '.pdb', '.ilk', '.exp',
        '.so', '.dylib', '.pyc', '.class', '.jar'
    ) | ForEach-Object { [void]$excludedExtensions.Add($_) }

    $excludedFileNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    @(
        'CMakeCache.txt', 'cmake_install.cmake', 'compile_commands.json',
        'build.ninja', '.ninja_deps', '.ninja_log', 'config.log', 'config.mak',
        'config.asm', 'config.fate', 'x264_config.h', 'x264.pc', '.depend'
    ) | ForEach-Object { [void]$excludedFileNames.Add($_) }

    New-Item -ItemType Directory -Path $Destination | Out-Null
    $pending = [Collections.Generic.Stack[object]]::new()
    $pending.Push([pscustomobject]@{ Source = $Source; Destination = $Destination })

    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory.Source -Force -ErrorAction Stop) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to follow a source reparse point: $($item.FullName)"
            }

            $target = Join-Path $directory.Destination $item.Name
            if ($item.PSIsContainer) {
                if ($excludedDirectories.Contains($item.Name)) { continue }
                New-Item -ItemType Directory -Path $target | Out-Null
                $pending.Push([pscustomobject]@{ Source = $item.FullName; Destination = $target })
                continue
            }

            if ($excludedExtensions.Contains($item.Extension) -or
                $excludedFileNames.Contains($item.Name)) {
                continue
            }
            [IO.File]::Copy($item.FullName, $target, $false)
        }
    }
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    $source = Join-Path $bundleSourceRoot $RelativePath
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing source-bundle input: $RelativePath"
    }
    $destination = Join-Path $DestinationRoot $RelativePath
    $destinationDirectory = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }
    [IO.File]::Copy($source, $destination, $false)
}

function Assert-ArchiveLayout {
    param([Parameter(Mandatory = $true)][string]$ArchivePath)

    $requiredEntries = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    @(
        'ffmpeg-tablink/source/ffmpeg-7.0.2/configure',
        'ffmpeg-tablink/source/nv-codec-headers-n12.2.72.0/Makefile',
        'ffmpeg-tablink/source/oneVPL-2.11.0/CMakeLists.txt',
        'ffmpeg-tablink/source/oneVPL-2.11.0/third-party-programs.txt',
        'ffmpeg-tablink/source/x264-b35605ace3dd/configure',
        'ffmpeg-tablink/source/AMF-1.4.35/LICENSE.txt',
        'ffmpeg-tablink/source/AMF-1.4.35/amf/public/include/core/Context.h',
        'ffmpeg-tablink/source/AMF-1.4.35/amf/public/include/components/VideoEncoderVCE.h',
        'ffmpeg-tablink/0001-windows-private-high-resolution-usleep.patch',
        'ffmpeg-tablink/build.ps1',
        'ffmpeg-tablink/build.sh',
        'ffmpeg-tablink/New-SourceBundle.ps1',
        'ffmpeg-tablink/prepare-toolchain.ps1',
        'ffmpeg-tablink/downloads-manifest.json',
        'ffmpeg-tablink/SOURCE-BUNDLE-MANIFEST.json',
        'ffmpeg-tablink/README.md',
        'ffmpeg-tablink/VALIDATION.md',
        'ffmpeg-tablink/NATIVE-MOTION-VALIDATION.md',
        'ffmpeg-tablink/bin/COPYING.oneVPL-third-party-programs.txt'
    ) | ForEach-Object { [void]$requiredEntries.Add($_) }

    $fileStream = [IO.File]::OpenRead($ArchivePath)
    $gzip = $null
    $reader = $null
    try {
        $gzip = [IO.Compression.GZipStream]::new(
            $fileStream, [IO.Compression.CompressionMode]::Decompress, $false)
        $reader = [System.Formats.Tar.TarReader]::new($gzip, $false)
        while ($null -ne ($entry = $reader.GetNextEntry())) {
            $name = $entry.Name.Replace('\', '/')
            if ($name -ne 'ffmpeg-tablink/' -and
                -not $name.StartsWith('ffmpeg-tablink/', [StringComparison]::Ordinal)) {
                throw "Archive entry is outside ffmpeg-tablink/: $name"
            }
            if ($name -match '(^|/)\.\.?(/|$)' -or $name -match '(^|/)\.git(/|$)') {
                throw "Archive contains an unsafe or excluded path: $name"
            }
            [void]$requiredEntries.Remove($name.TrimEnd('/'))
        }
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        elseif ($null -ne $gzip) { $gzip.Dispose() }
        else { $fileStream.Dispose() }
    }

    if ($requiredEntries.Count -ne 0) {
        throw ('Source archive is missing required entries: ' +
            (($requiredEntries | Sort-Object) -join ', '))
    }
}

function Remove-VerifiedStaging {
    param(
        [Parameter(Mandatory = $true)][string]$VerifiedWorkRoot,
        [Parameter(Mandatory = $true)][string]$StagingPath
    )

    $stagingFullPath = [IO.Path]::GetFullPath($StagingPath)
    if ((Split-Path -Leaf $stagingFullPath) -ne 'staging' -or
        -not (Test-ContainedPath -Parent $VerifiedWorkRoot -Candidate $stagingFullPath)) {
        throw "Refusing to clean an unverified staging path: $stagingFullPath"
    }
    if (Test-Path -LiteralPath $stagingFullPath) {
        Remove-Item -LiteralPath $stagingFullPath -Recurse -Force
    }
}

Assert-SameDrive -Expected $sourceDrive -Actual $workDrive -Description 'The ASCII work directory'
Assert-SameDrive -Expected $sourceDrive -Actual $outputDrive -Description 'The output archive'
if ($workRoot -match '[^\x00-\x7F]') {
    throw 'Choose a new ASCII-only work directory.'
}
if (Test-Path -LiteralPath $workRoot) {
    throw 'Choose a new work directory; the path must not already exist.'
}
if (Test-Path -LiteralPath $outputFile) {
    if ((Get-Item -LiteralPath $outputFile).PSIsContainer) {
        throw "The output path names a directory: $outputFile"
    }
    if (-not $Force) {
        throw "The output archive already exists. Pass -Force to replace it: $outputFile"
    }
}

$stagingContainer = Join-Path $workRoot 'staging'
$archiveRoot = Join-Path $stagingContainer 'ffmpeg-tablink'
$temporaryTar = Join-Path $stagingContainer 'source-bundle.tar'
$temporaryGzip = Join-Path $stagingContainer 'source-bundle.tar.gz'
$stagingCreated = $false

New-Item -ItemType Directory -Path $workRoot | Out-Null
try {
    New-Item -ItemType Directory -Path $stagingContainer | Out-Null
    $stagingCreated = $true
    New-Item -ItemType Directory -Path (Join-Path $archiveRoot 'source') -Force | Out-Null

    foreach ($sourceName in @(
        'ffmpeg-7.0.2',
        'nv-codec-headers-n12.2.72.0',
        'oneVPL-2.11.0',
        'x264-b35605ace3dd'
    )) {
        Copy-FilteredSourceTree `
            -Source (Join-Path $bundleSourceRoot ('source\' + $sourceName)) `
            -Destination (Join-Path $archiveRoot ('source\' + $sourceName))
    }

    $amfSource = Join-Path $bundleSourceRoot 'source\AMF-1.4.35'
    $amfDestination = Join-Path $archiveRoot 'source\AMF-1.4.35'
    foreach ($amfInput in @(
        'LICENSE.txt',
        'amf\public\include\core',
        'amf\public\include\components'
    )) {
        $source = Join-Path $amfSource $amfInput
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Missing AMF source-bundle input: $amfInput"
        }
        $destination = Join-Path $amfDestination $amfInput
        if ((Get-Item -LiteralPath $source).PSIsContainer) {
            Copy-FilteredSourceTree -Source $source -Destination $destination
        }
        else {
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            [IO.File]::Copy($source, $destination, $false)
        }
    }

    foreach ($relativePath in @(
        '0001-windows-private-high-resolution-usleep.patch',
        'build.ps1',
        'build.sh',
        'New-SourceBundle.ps1',
        'prepare-toolchain.ps1',
        'downloads-manifest.json',
        'SOURCE-BUNDLE-MANIFEST.json',
        'README.md',
        'VALIDATION.md',
        'NATIVE-MOTION-VALIDATION.md'
    )) {
        Copy-RequiredFile -RelativePath $relativePath -DestinationRoot $archiveRoot
    }

    $licenseFiles = @(Get-ChildItem -LiteralPath (Join-Path $bundleSourceRoot 'bin') `
        -Filter 'COPYING*' -File -Force -ErrorAction Stop)
    if ($licenseFiles.Count -eq 0) { throw 'No bin/COPYING* license files were found.' }
    $licenseDestination = Join-Path $archiveRoot 'bin'
    New-Item -ItemType Directory -Path $licenseDestination -Force | Out-Null
    foreach ($licenseFile in $licenseFiles) {
        [IO.File]::Copy($licenseFile.FullName,
            (Join-Path $licenseDestination $licenseFile.Name), $false)
    }

    foreach ($optionalLog in @('signature-verification.log', 'production-parser-test.log')) {
        $logPath = Join-Path $bundleSourceRoot $optionalLog
        if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            [IO.File]::Copy($logPath, (Join-Path $archiveRoot $optionalLog), $false)
        }
    }

    [System.Formats.Tar.TarFile]::CreateFromDirectory(
        $archiveRoot, $temporaryTar, $true)
    $tarInput = [IO.File]::OpenRead($temporaryTar)
    $gzipOutput = [IO.File]::Create($temporaryGzip)
    try {
        $gzipStream = [IO.Compression.GZipStream]::new(
            $gzipOutput, [IO.Compression.CompressionLevel]::Optimal, $true)
        try { $tarInput.CopyTo($gzipStream) }
        finally { $gzipStream.Dispose() }
    }
    finally {
        $gzipOutput.Dispose()
        $tarInput.Dispose()
    }

    Assert-ArchiveLayout -ArchivePath $temporaryGzip
    $outputDirectory = Split-Path -Parent $outputFile
    if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    [IO.File]::Copy($temporaryGzip, $outputFile, [bool]$Force)

    $stagedHash = (Get-FileHash -LiteralPath $temporaryGzip -Algorithm SHA256).Hash
    $outputHash = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash
    if (-not [string]::Equals($stagedHash, $outputHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The copied source archive failed its SHA-256 verification.'
    }
    Write-Output ("Created {0}: {1} bytes; SHA-256 {2}" -f $outputFile,
        (Get-Item -LiteralPath $outputFile).Length, $outputHash)
}
finally {
    if ($stagingCreated) {
        Remove-VerifiedStaging -VerifiedWorkRoot $workRoot -StagingPath $stagingContainer
    }
}
