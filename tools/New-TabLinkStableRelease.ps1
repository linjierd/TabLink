#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [long]$WindowsBuild,

    [Parameter(Mandatory)]
    [string]$BaseUrl,

    [string]$WindowsSource,
    [string]$AndroidApk,
    [long]$AndroidBuild,
    [string]$AndroidSignerSha256,
    [string]$JavaHome,
    [string]$OutputRoot,
    [string]$PrivateKeyPath,
    [string]$PublicKeyPath,

    [ValidateRange(0, 100)]
    [int]$RolloutPercentage = 100,

    [ValidateRange(1, [int]::MaxValue)]
    [int]$MinimumProtocolVersion = 1,

    [string]$ReleaseId,
    [ValidateLength(0, 4096)]
    [string]$Notes = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stableManifestUrl = 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json'
$expectedAndroidPackageName = 'com.tablink.client'
if ([string]::IsNullOrWhiteSpace($WindowsSource)) {
    $WindowsSource = Join-Path $repositoryRoot 'dist\TabLink'
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\stable'
}
if ([string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    $PrivateKeyPath = Join-Path $env:LOCALAPPDATA 'TabLink\release-signing\stable-private.pem'
}
if ([string]::IsNullOrWhiteSpace($PublicKeyPath)) {
    $PublicKeyPath = Join-Path $repositoryRoot 'updates\stable-public-key.spki.base64'
}
if ([string]::IsNullOrWhiteSpace($ReleaseId)) {
    $ReleaseId = "tablink-$Version"
}

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Assert-ExistingFile([string]$Path, [string]$Description) {
    $fullPath = Get-FullPath $Path
    if (-not [IO.File]::Exists($fullPath)) {
        throw "$Description does not exist: $fullPath"
    }
    return $fullPath
}

function Assert-ExistingDirectory([string]$Path, [string]$Description) {
    $fullPath = Get-FullPath $Path
    if (-not [IO.Directory]::Exists($fullPath)) {
        throw "$Description does not exist: $fullPath"
    }
    return $fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Test-PathInside([string]$Candidate, [string]$Parent) {
    $candidatePath = (Get-FullPath $Candidate).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $parentPath = (Get-FullPath $Parent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return $candidatePath.Equals($parentPath, [StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith($parentPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function New-ReleaseUrl([string]$RelativePath, [bool]$UsePathTemplate, [string]$PathTemplate, [Uri]$RootUri) {
    if ($UsePathTemplate) {
        $encodedPath = [Uri]::EscapeDataString($RelativePath)
        return $PathTemplate.Replace('{path}', $encodedPath)
    }
    $builder = [UriBuilder]::new($RootUri)
    $basePath = $builder.Path.TrimEnd('/')
    $encodedSegments = $RelativePath.Split('/', [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { [Uri]::EscapeDataString($_) }
    $builder.Path = $basePath + '/' + ($encodedSegments -join '/')
    $builder.Query = ''
    $builder.Fragment = ''
    return $builder.Uri.AbsoluteUri
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Get-FullPath $Path), $Content, $encoding)
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-StableWindowsSource([string]$SourceDirectory, [string]$ExpectedManifestUrl) {
    $source = Assert-ExistingDirectory $SourceDirectory 'Windows source directory'
    $requiredFiles = @(
        'TabLink.exe',
        'TabLink.dll',
        'TabLink.Updater.exe',
        'TabLink.Updater.dll',
        'TabLink.Updater.deps.json',
        'TabLink.Updater.runtimeconfig.json',
        'update-channel.json',
        'selftest-result.txt',
        'SHA256SUMS.txt'
    )
    foreach ($relative in $requiredFiles) {
        $required = Assert-ExistingFile (Join-Path $source $relative) "Required stable Windows package file '$relative'"
        if ((Get-Item -LiteralPath $required).Length -eq 0) {
            throw "Required stable Windows package file is empty: $relative"
        }
    }

    $channelPath = Join-Path $source 'update-channel.json'
    try {
        $channel = Get-Content -LiteralPath $channelPath -Raw | ConvertFrom-Json -AsHashtable
    }
    catch {
        throw "update-channel.json is not valid JSON: $($_.Exception.Message)"
    }
    if ($channel -isnot [Collections.IDictionary] -or
        -not $channel.Contains('enabled') -or $channel.enabled -isnot [bool] -or -not $channel.enabled) {
        throw 'update-channel.json must contain enabled=true as a JSON boolean.'
    }
    if (-not $channel.Contains('manifestUrl') -or $channel.manifestUrl -isnot [string] -or
        $channel.manifestUrl -cne $ExpectedManifestUrl) {
        throw "update-channel.json manifestUrl must exactly equal the formal stable URL: $ExpectedManifestUrl"
    }

    $checksumPath = Join-Path $source 'SHA256SUMS.txt'
    $sourcePrefix = $source.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadAllLines($checksumPath)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -cnotmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<path>.+)$') {
            throw "SHA256SUMS.txt line $lineNumber must be '<64 hex><two spaces><relative path>'."
        }
        $expectedHash = $Matches.hash.ToLowerInvariant()
        $relativeText = $Matches.path
        if ($relativeText -cne $relativeText.Trim()) {
            throw "SHA256SUMS.txt line $lineNumber contains leading or trailing path whitespace."
        }
        $segments = @($relativeText -split '[\\/]')
        $unsafeSegment = $null -ne ($segments | Where-Object { [string]::IsNullOrEmpty([string]$_) -or $_ -in @('.', '..') } | Select-Object -First 1)
        if ([IO.Path]::IsPathRooted($relativeText) -or $relativeText.Contains(':') -or
            $segments.Count -eq 0 -or $unsafeSegment) {
            throw "SHA256SUMS.txt line $lineNumber contains an unsafe relative path: $relativeText"
        }
        $normalizedRelative = $segments -join [IO.Path]::DirectorySeparatorChar
        if (-not $listed.Add($normalizedRelative)) {
            throw "SHA256SUMS.txt contains a duplicate path: $relativeText"
        }
        $fullPath = [IO.Path]::GetFullPath((Join-Path $source $normalizedRelative))
        if (-not $fullPath.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.File]::Exists($fullPath)) {
            throw "SHA256SUMS.txt references a missing or out-of-package file: $relativeText"
        }
        if (([IO.File]::GetAttributes($fullPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "SHA256SUMS.txt references a reparse point: $relativeText"
        }
        $actualHash = Get-Sha256 $fullPath
        if ($actualHash -cne $expectedHash) {
            throw "SHA256SUMS.txt hash mismatch for '$relativeText': expected $expectedHash, actual $actualHash"
        }
    }
    if ($listed.Count -eq 0) { throw 'SHA256SUMS.txt does not contain any file hashes.' }
    foreach ($binary in @('TabLink.exe', 'TabLink.dll', 'TabLink.Updater.exe', 'TabLink.Updater.dll')) {
        if (-not $listed.Contains($binary)) {
            throw "SHA256SUMS.txt must include the release binary: $binary"
        }
    }
}

function New-DeterministicZip([string]$SourceDirectory, [string]$DestinationFile) {
    Add-Type -AssemblyName System.IO.Compression
    $source = Assert-ExistingDirectory $SourceDirectory 'Windows source directory'
    $destination = Get-FullPath $DestinationFile
    $files = @(Get-ChildItem -LiteralPath $source -Force -Recurse -File | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw 'Windows source directory is empty.'
    }

    $stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in $files) {
                $relativePath = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
                if ($relativePath.StartsWith('../', [StringComparison]::Ordinal) -or $relativePath.Contains('/../', [StringComparison]::Ordinal)) {
                    throw "Unsafe ZIP entry path: $relativePath"
                }
                $entry = $archive.CreateEntry($relativePath, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try {
                    $output = $entry.Open()
                    try {
                        $input.CopyTo($output)
                    }
                    finally {
                        $output.Dispose()
                    }
                }
                finally {
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Find-AndroidBuildTools {
    $candidateRoots = [Collections.Generic.List[string]]::new()
    $candidateRoots.Add((Join-Path $repositoryRoot 'android\.tools\sdk\build-tools'))
    $candidateRoots.Add((Join-Path $repositoryRoot 'android\.tools\build-tools-unpacked'))
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) {
        $candidateRoots.Add((Join-Path $env:ANDROID_SDK_ROOT 'build-tools'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidateRoots.Add((Join-Path $env:LOCALAPPDATA 'Android\Sdk\build-tools'))
    }

    $candidates = [Collections.Generic.List[IO.DirectoryInfo]]::new()
    foreach ($root in $candidateRoots | Select-Object -Unique) {
        if (-not [IO.Directory]::Exists($root)) {
            continue
        }
        foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -Force) {
            if ([IO.File]::Exists((Join-Path $directory.FullName 'aapt.exe')) -and
                [IO.File]::Exists((Join-Path $directory.FullName 'apksigner.bat'))) {
                $candidates.Add($directory)
            }
        }
    }
    $selected = $candidates | Sort-Object LastWriteTimeUtc, FullName -Descending | Select-Object -First 1
    if ($null -eq $selected) {
        throw 'Android APK staging requires Android build-tools containing aapt.exe and apksigner.bat.'
    }
    return $selected.FullName
}

function Find-JavaHome([string]$RequestedJavaHome) {
    $candidates = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($RequestedJavaHome)) { $candidates.Add($RequestedJavaHome) }
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) { $candidates.Add($env:JAVA_HOME) }
    foreach ($pattern in @(
        'C:\Program Files\Android\openjdk\*',
        'C:\Program Files\Android\Android Studio*\jbr',
        'C:\Program Files\Java\*'
    )) {
        foreach ($directory in Get-Item -Path $pattern -ErrorAction SilentlyContinue) {
            if ($directory.PSIsContainer) { $candidates.Add($directory.FullName) }
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $candidateHome = Get-FullPath $candidate
        $java = Join-Path $candidateHome 'bin\java.exe'
        if (-not [IO.File]::Exists($java)) { continue }
        $versionOutput = & $java -version 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch 'version\s+"(?<major>[0-9]+)(?:\.(?<minor>[0-9]+))?') { continue }
        $major = [int]$Matches.major
        if ($major -eq 1 -and $Matches.minor) { $major = [int]$Matches.minor }
        if ($major -ge 11) { return $candidateHome }
    }
    throw 'Android APK verification requires Java 11 or newer.'
}

function Test-AndroidArtifact(
    [string]$ApkPath,
    [string]$ExpectedVersion,
    [long]$ExpectedBuild,
    [string]$ExpectedSignerSha256,
    [string]$RequestedJavaHome
) {
    if ($ExpectedBuild -lt 1) {
        throw '-AndroidBuild must be positive when -AndroidApk is supplied.'
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedSignerSha256)) {
        throw '-AndroidSignerSha256 is required when -AndroidApk is supplied.'
    }
    $normalizedSigner = ($ExpectedSignerSha256 -replace ':', '').ToLowerInvariant()
    if ($normalizedSigner -notmatch '^[0-9a-f]{64}$') {
        throw '-AndroidSignerSha256 must be the expected 64-hex signing-certificate SHA-256 digest.'
    }

    # Older Android build-tools cannot open Unicode paths, so inspect a private ASCII-named copy.
    $inspectionRoot = Join-Path ([IO.Path]::GetTempPath()) ('TabLink-Apk-Inspect-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($inspectionRoot) | Out-Null
    $inspectionApk = Join-Path $inspectionRoot 'artifact.apk'
    try {
        [IO.File]::Copy($ApkPath, $inspectionApk, $false)
        $buildTools = Find-AndroidBuildTools
        $aapt = Join-Path $buildTools 'aapt.exe'
        $badging = & $aapt dump badging $inspectionApk 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "aapt could not inspect the Android APK: $($badging -join [Environment]::NewLine)"
        }
        $packageLine = $badging | Where-Object { $_ -like 'package:*' } | Select-Object -First 1
        $packageNameMatch = [regex]::Match([string]$packageLine, "(?:^|\s)name='(?<value>[^']+)'")
        $versionMatch = [regex]::Match([string]$packageLine, "(?:^|\s)versionCode='(?<build>[0-9]+)'\s+versionName='(?<version>[^']+)'")
        if (-not $packageNameMatch.Success -or -not $versionMatch.Success) {
            throw 'aapt did not return an Android package name, versionName, and versionCode.'
        }
        $actualPackageName = $packageNameMatch.Groups['value'].Value
        if ($actualPackageName -cne $expectedAndroidPackageName) {
            throw "Android APK package name is '$actualPackageName'; expected '$expectedAndroidPackageName'."
        }
        $actualVersion = $versionMatch.Groups['version'].Value
        $actualBuild = [long]$versionMatch.Groups['build'].Value
        if ($actualVersion -cne $ExpectedVersion -or $actualBuild -ne $ExpectedBuild) {
            throw "Android APK metadata is $actualVersion build $actualBuild; expected $ExpectedVersion build $ExpectedBuild."
        }

        $verifiedJavaHome = Find-JavaHome $RequestedJavaHome
        $apksigner = Join-Path $buildTools 'apksigner.bat'
        $savedJavaHome = $env:JAVA_HOME
        try {
            $env:JAVA_HOME = $verifiedJavaHome
            $signatureOutput = & $apksigner verify --verbose --print-certs $inspectionApk 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "apksigner rejected the Android APK: $($signatureOutput -join [Environment]::NewLine)"
            }
        }
        finally {
            $env:JAVA_HOME = $savedJavaHome
        }

        $signatureText = $signatureOutput -join [Environment]::NewLine
        $match = [regex]::Match($signatureText, 'Signer #1 certificate SHA-256 digest:\s*(?<digest>[0-9a-fA-F]{64})')
        if (-not $match.Success) {
            throw 'apksigner did not report a signing-certificate SHA-256 digest.'
        }
        $actualSigner = $match.Groups['digest'].Value.ToLowerInvariant()
        if ($actualSigner -cne $normalizedSigner) {
            throw "Android APK signer digest does not match the explicitly pinned release signer."
        }
        return $actualSigner
    }
    finally {
        if ([IO.Directory]::Exists($inspectionRoot)) {
            $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
            $resolvedInspectionRoot = [IO.Path]::GetFullPath($inspectionRoot)
            if (-not $resolvedInspectionRoot.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                -not ([IO.Path]::GetFileName($resolvedInspectionRoot).StartsWith('TabLink-Apk-Inspect-', [StringComparison]::Ordinal))) {
                throw "Refusing to clean unexpected APK inspection path: $resolvedInspectionRoot"
            }
            [IO.Directory]::Delete($resolvedInspectionRoot, $true)
        }
    }
}

if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw '-Version must be a stable SemVer in major.minor.patch form; prerelease and build metadata are not accepted.'
}
if ($ReleaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
    throw '-ReleaseId contains unsupported characters or is too long.'
}

$pathTemplateMode = $BaseUrl.Contains('{path}', [StringComparison]::Ordinal)
$baseUri = $null
if ($pathTemplateMode) {
    if ([regex]::Matches($BaseUrl, '\{path\}', [Text.RegularExpressions.RegexOptions]::CultureInvariant).Count -ne 1) {
        throw '-BaseUrl path template must contain {path} exactly once.'
    }
    $probeUrl = $BaseUrl.Replace('{path}', 'tablink-template-probe')
    if ($probeUrl.Contains('{', [StringComparison]::Ordinal) -or $probeUrl.Contains('}', [StringComparison]::Ordinal) -or
        -not [Uri]::TryCreate($probeUrl, [UriKind]::Absolute, [ref]$baseUri) -or
        $baseUri.Scheme -cne 'https' -or [string]::IsNullOrWhiteSpace($baseUri.Host) -or
        -not [string]::IsNullOrEmpty($baseUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($baseUri.Fragment) -or
        $baseUri.Query -cne '?path=tablink-template-probe') {
        throw '-BaseUrl template must be an absolute HTTPS URL whose only query is path={path}, without credentials or fragment.'
    }
    $normalizedBaseUrl = $BaseUrl
}
else {
    if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$baseUri) -or
        $baseUri.Scheme -cne 'https' -or [string]::IsNullOrWhiteSpace($baseUri.Host) -or
        -not [string]::IsNullOrEmpty($baseUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($baseUri.Query) -or
        -not [string]::IsNullOrEmpty($baseUri.Fragment)) {
        throw '-BaseUrl must be an absolute HTTPS URL without credentials, query, or fragment, or the strict ?path={path} template.'
    }
    $normalizedBaseUrl = $baseUri.AbsoluteUri.TrimEnd('/')
}

$WindowsSource = Assert-ExistingDirectory $WindowsSource 'Windows source directory'
$null = Assert-StableWindowsSource $WindowsSource $stableManifestUrl
$PrivateKeyPath = Assert-ExistingFile $PrivateKeyPath 'Stable release private key'
$PublicKeyPath = Assert-ExistingFile $PublicKeyPath 'Pinned stable release public key'
$OutputRoot = Get-FullPath $OutputRoot
if (Test-PathInside $OutputRoot $WindowsSource) {
    throw '-OutputRoot cannot be inside -WindowsSource.'
}
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

$finalDirectory = Join-Path $OutputRoot $Version
if ([IO.Directory]::Exists($finalDirectory) -or [IO.File]::Exists($finalDirectory)) {
    throw "Refusing to replace an existing stable release directory: $finalDirectory"
}

$windowsExe = Assert-ExistingFile (Join-Path $WindowsSource 'TabLink.exe') 'TabLink Windows executable'
$windowsDll = Assert-ExistingFile (Join-Path $WindowsSource 'TabLink.dll') 'TabLink Windows assembly'
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($windowsDll).Version
$sourceVersion = "$($assemblyVersion.Major).$($assemblyVersion.Minor).$($assemblyVersion.Build)"
if ($sourceVersion -cne $Version) {
    throw "Windows source assembly version is $sourceVersion; requested stable version is $Version."
}

$privateKeyFullPath = Get-FullPath $PrivateKeyPath
$sensitiveExtensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @('.pem', '.key', '.pfx', '.p12', '.jks', '.keystore', '.snk')) { $sensitiveExtensions.Add($extension) | Out-Null }
$packageItems = @(Get-ChildItem -LiteralPath $WindowsSource -Force -Recurse)
foreach ($item in $packageItems) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Windows source contains a reparse point, which is not allowed in a stable package: $($item.FullName)"
    }
    if (-not $item.PSIsContainer) {
        if ((Get-FullPath $item.FullName).Equals($privateKeyFullPath, [StringComparison]::OrdinalIgnoreCase) -or $sensitiveExtensions.Contains($item.Extension) -or
            $item.Name -in @('.env', 'id_rsa', 'id_ed25519')) {
            throw "Windows source contains a possible private key or secret file: $($item.FullName)"
        }
    }
}

$resolvedAndroidApk = $null
$actualAndroidSigner = $null
if (-not [string]::IsNullOrWhiteSpace($AndroidApk)) {
    $resolvedAndroidApk = Assert-ExistingFile $AndroidApk 'Android APK'
    $actualAndroidSigner = Test-AndroidArtifact $resolvedAndroidApk $Version $AndroidBuild $AndroidSignerSha256 $JavaHome
}
elseif ($AndroidBuild -ne 0 -or -not [string]::IsNullOrWhiteSpace($AndroidSignerSha256)) {
    throw '-AndroidBuild and -AndroidSignerSha256 are only valid together with -AndroidApk.'
}

$releaseToolProject = Assert-ExistingFile (Join-Path $repositoryRoot 'tools\TabLink.ReleaseTool\TabLink.ReleaseTool.csproj') 'Release tool project'
& dotnet build $releaseToolProject -c Release --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "ReleaseTool build failed with exit code $LASTEXITCODE." }
$releaseToolDll = Assert-ExistingFile (Join-Path $repositoryRoot 'tools\TabLink.ReleaseTool\bin\Release\net10.0\TabLink.ReleaseTool.dll') 'Release tool assembly'

$temporaryDirectory = Join-Path $OutputRoot ('.staging-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
try {
    $releaseDirectory = Join-Path $temporaryDirectory 'release'
    [IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null

    $windowsFileName = "TabLink-windows-x64-$Version.zip"
    $windowsArchive = Join-Path $releaseDirectory $windowsFileName
    New-DeterministicZip $WindowsSource $windowsArchive

    $artifacts = [Collections.Generic.List[object]]::new()
    $windowsRelativePath = if ($pathTemplateMode) { "TabLink/stable/$Version/$windowsFileName" } else { "releases/$Version/$windowsFileName" }
    $windowsArtifact = [ordered]@{
        platform = 'windows-x64'
        version  = $Version
        build    = $WindowsBuild
        url      = New-ReleaseUrl $windowsRelativePath $pathTemplateMode $BaseUrl $baseUri
        size     = (Get-Item -LiteralPath $windowsArchive).Length
        sha256   = Get-Sha256 $windowsArchive
    }
    if (-not [string]::IsNullOrEmpty($Notes)) { $windowsArtifact.notes = $Notes }
    $artifacts.Add($windowsArtifact)

    if ($null -ne $resolvedAndroidApk) {
        $androidFileName = "TabLink-android-$Version.apk"
        $androidDestination = Join-Path $releaseDirectory $androidFileName
        [IO.File]::Copy($resolvedAndroidApk, $androidDestination, $false)
        $androidRelativePath = if ($pathTemplateMode) { "TabLink/stable/$Version/$androidFileName" } else { "releases/$Version/$androidFileName" }
        $androidArtifact = [ordered]@{
            platform     = 'android'
            version      = $Version
            build        = $AndroidBuild
            url          = New-ReleaseUrl $androidRelativePath $pathTemplateMode $BaseUrl $baseUri
            size         = (Get-Item -LiteralPath $androidDestination).Length
            sha256       = Get-Sha256 $androidDestination
            installerUrl = New-ReleaseUrl $androidRelativePath $pathTemplateMode $BaseUrl $baseUri
        }
        if (-not [string]::IsNullOrEmpty($Notes)) { $androidArtifact.notes = $Notes }
        $artifacts.Add($androidArtifact)
    }

    $publishedAtUtc = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    $payload = [ordered]@{
        schema                 = 1
        channel                = 'stable'
        releaseId              = $ReleaseId
        publishedAtUtc         = $publishedAtUtc
        rolloutPercentage      = $RolloutPercentage
        minimumProtocolVersion = $MinimumProtocolVersion
        artifacts              = $artifacts.ToArray()
    }
    $payloadPath = Join-Path $temporaryDirectory 'payload.json'
    Write-Utf8NoBom $payloadPath ($payload | ConvertTo-Json -Depth 8 -Compress)

    $manifestPath = Join-Path $temporaryDirectory 'manifest.json'
    & dotnet $releaseToolDll sign $PrivateKeyPath $payloadPath $manifestPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "ReleaseTool signing failed with exit code $LASTEXITCODE." }
    & dotnet $releaseToolDll verify $PublicKeyPath $manifestPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ReleaseTool verification failed with exit code $LASTEXITCODE." }

    $envelope = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $embeddedPayload = [Convert]::FromBase64String($envelope.payload)
    $payloadBytes = [IO.File]::ReadAllBytes($payloadPath)
    if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($embeddedPayload, $payloadBytes)) {
        throw 'Signed manifest does not contain the exact payload.json bytes.'
    }

    $checksumEntries = [Collections.Generic.List[object]]::new()
    $checksumFiles = @(Get-ChildItem -LiteralPath $releaseDirectory -File | Sort-Object Name)
    $checksumFiles += Get-Item -LiteralPath $payloadPath
    $checksumFiles += Get-Item -LiteralPath $manifestPath
    foreach ($file in $checksumFiles) {
        $relative = [IO.Path]::GetRelativePath($temporaryDirectory, $file.FullName).Replace('\', '/')
        $checksumEntries.Add([pscustomobject]@{ Path = $relative; Sha256 = Get-Sha256 $file.FullName; Size = $file.Length })
    }
    $checksumsPath = Join-Path $temporaryDirectory 'SHA256SUMS.txt'
    $checksumLines = ($checksumEntries | ForEach-Object { "$($_.Sha256)  $($_.Path)" }) -join "`n"
    Write-Utf8NoBom $checksumsPath ($checksumLines + "`n")

    $publicKeyHash = Get-Sha256 $PublicKeyPath
    $manifestRelativePath = if ($pathTemplateMode) { 'TabLink/stable/manifest.json' } else { 'stable/manifest.json' }
    $manifestPublishUrl = New-ReleaseUrl $manifestRelativePath $pathTemplateMode $BaseUrl $baseUri
    $summary = [ordered]@{
        schema             = 1
        channel            = 'stable'
        version            = $Version
        releaseId          = $ReleaseId
        publishedAtUtc     = $publishedAtUtc
        baseUrl            = $normalizedBaseUrl
        manifestPublishUrl = $manifestPublishUrl
        immutableArtifacts = $artifacts.ToArray()
        manifest           = [ordered]@{
            path   = 'manifest.json'
            size   = (Get-Item -LiteralPath $manifestPath).Length
            sha256 = Get-Sha256 $manifestPath
        }
        publicKey          = [ordered]@{
            algorithm = 'ECDSA-P256-SHA256-DER'
            sha256    = $publicKeyHash
        }
        androidSignerSha256 = $actualAndroidSigner
        uploaded            = $false
        uploadPerformedByTool = $false
    }
    $summaryPath = Join-Path $temporaryDirectory 'release-summary.json'
    Write-Utf8NoBom $summaryPath ($summary | ConvertTo-Json -Depth 8)

    # Directory.Move is atomic on the same volume and will fail if another publisher won the version name.
    [IO.Directory]::Move($temporaryDirectory, $finalDirectory)
    $temporaryDirectory = $null

    Write-Host "Stable release staged without uploading: $finalDirectory"
    Write-Host "Immutable artifact URLs are listed in release-summary.json."
    Write-Host "Publish manifest.json last to: $manifestPublishUrl"
    [pscustomobject]@{
        Version             = $Version
        Directory           = $finalDirectory
        ArtifactCount       = $artifacts.Count
        ManifestVerified    = $true
        ExternalUpload      = $false
        ManifestPublishUrl  = $manifestPublishUrl
    }
}
finally {
    if ($null -ne $temporaryDirectory -and [IO.Directory]::Exists($temporaryDirectory)) {
        if (-not (Test-PathInside $temporaryDirectory $OutputRoot)) {
            throw "Refusing to clean a temporary path outside OutputRoot: $temporaryDirectory"
        }
        [IO.Directory]::Delete($temporaryDirectory, $true)
    }
}
