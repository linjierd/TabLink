[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$versionIdentityPath = Join-Path $resolvedRepositoryRoot 'eng\version.json'
$windowsProjectPath = Join-Path $resolvedRepositoryRoot 'src\TabLink.Windows\TabLink.Windows.csproj'
$androidBuildPath = Join-Path $resolvedRepositoryRoot 'android\app\build.gradle'

foreach ($requiredPath in @($versionIdentityPath, $windowsProjectPath, $androidBuildPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required version source was not found: $requiredPath"
    }
}

try {
    $versionIdentity = Get-Content -LiteralPath $versionIdentityPath -Raw | ConvertFrom-Json
}
catch {
    throw "eng\version.json is not valid JSON: $($_.Exception.Message)"
}
if ($null -eq $versionIdentity -or $versionIdentity -isnot [PSCustomObject]) {
    throw 'eng\version.json must contain one JSON object.'
}
$expectedIdentityProperties = @('schemaVersion', 'version', 'channel', 'previewNumber', 'androidVersionCode')
$actualIdentityProperties = @($versionIdentity.PSObject.Properties.Name)
$missingIdentityProperties = @($expectedIdentityProperties | Where-Object { $_ -notin $actualIdentityProperties })
$unknownIdentityProperties = @($actualIdentityProperties | Where-Object { $_ -notin $expectedIdentityProperties })
if ($missingIdentityProperties.Count -ne 0 -or $unknownIdentityProperties.Count -ne 0) {
    throw ('eng\version.json must contain exactly: ' + ($expectedIdentityProperties -join ', ') +
        '. Missing: ' + ($missingIdentityProperties -join ', ') +
        '. Unknown: ' + ($unknownIdentityProperties -join ', ') + '.')
}
if (($versionIdentity.schemaVersion -isnot [int] -and $versionIdentity.schemaVersion -isnot [long]) -or
    $versionIdentity.schemaVersion -ne 1) {
    throw 'eng\version.json schemaVersion must be the integer 1.'
}
$releaseVersion = [string]$versionIdentity.version
if ($releaseVersion -cnotmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$') {
    throw "eng\version.json version must be a canonical three-part numeric version; found '$releaseVersion'."
}
if ([string]$versionIdentity.channel -cne 'preview') {
    throw "eng\version.json channel must be 'preview' on this release line."
}
if (($versionIdentity.previewNumber -isnot [int] -and $versionIdentity.previewNumber -isnot [long]) -or
    $versionIdentity.previewNumber -le 0) {
    throw 'eng\version.json previewNumber must be a positive integer.'
}
if (($versionIdentity.androidVersionCode -isnot [int] -and $versionIdentity.androidVersionCode -isnot [long]) -or
    $versionIdentity.androidVersionCode -le 0) {
    throw 'eng\version.json androidVersionCode must be a positive integer.'
}

[xml]$windowsProject = Get-Content -LiteralPath $windowsProjectPath -Raw
$windowsVersionNodes = @($windowsProject.SelectNodes('/Project/PropertyGroup/Version'))
if ($windowsVersionNodes.Count -ne 1) {
    throw "Expected exactly one Windows <Version> value, found $($windowsVersionNodes.Count)."
}
$windowsVersion = $windowsVersionNodes[0].InnerText.Trim()
if ([string]::IsNullOrWhiteSpace($windowsVersion)) {
    throw 'The Windows <Version> value is empty.'
}

$androidBuild = Get-Content -LiteralPath $androidBuildPath -Raw
$androidVersionMatches = [regex]::Matches(
    $androidBuild,
    '(?m)^\s*versionName\s+["''](?<value>[^"'']+)["'']\s*(?://.*)?$'
)
if ($androidVersionMatches.Count -ne 1) {
    throw "Expected exactly one Android versionName value, found $($androidVersionMatches.Count)."
}
$androidVersion = $androidVersionMatches[0].Groups['value'].Value.Trim()

$androidVersionCodeMatches = [regex]::Matches(
    $androidBuild,
    '(?m)^\s*versionCode\s+(?<value>\d+)\s*(?://.*)?$'
)
if ($androidVersionCodeMatches.Count -ne 1) {
    throw "Expected exactly one Android versionCode value, found $($androidVersionCodeMatches.Count)."
}
$androidVersionCode = [int64]$androidVersionCodeMatches[0].Groups['value'].Value
if ($androidVersionCode -le 0) {
    throw "Android versionCode must be positive; found $androidVersionCode."
}

if ($windowsVersion -cne $androidVersion) {
    throw "Cross-platform version mismatch: Windows=$windowsVersion; Android=$androidVersion."
}
if ($windowsVersion -cne $releaseVersion) {
    throw "Release identity mismatch: eng/version.json=$releaseVersion; Windows=$windowsVersion; Android=$androidVersion."
}
if ($androidVersionCode -ne [int64]$versionIdentity.androidVersionCode) {
    throw "Android versionCode mismatch: eng/version.json=$($versionIdentity.androidVersionCode); Gradle=$androidVersionCode."
}

$releaseDocumentPath = Join-Path $resolvedRepositoryRoot ("RELEASE-$releaseVersion.md")
$verificationDocumentPath = Join-Path $resolvedRepositoryRoot ("VERIFICATION-$releaseVersion.md")
$rootReadmePath = Join-Path $resolvedRepositoryRoot 'README.md'
$androidReadmePath = Join-Path $resolvedRepositoryRoot 'android\README.md'
$publicReleasePath = Join-Path $resolvedRepositoryRoot 'PUBLIC-RELEASE.md'
$currentDocumentationPaths = @(
    $releaseDocumentPath,
    $verificationDocumentPath,
    $rootReadmePath,
    $androidReadmePath,
    $publicReleasePath
)
foreach ($documentPath in $currentDocumentationPaths) {
    if (-not (Test-Path -LiteralPath $documentPath -PathType Leaf)) {
        throw "Current release document was not found: $documentPath"
    }
}

$releaseTag = "v$releaseVersion-preview.$($versionIdentity.previewNumber)"
$identityMarker = '<!-- tablink-version-contract: version=' + $releaseVersion +
    '; channel=preview; preview=' + $versionIdentity.previewNumber +
    '; androidVersionCode=' + $androidVersionCode + ' -->'
$documentation = @{}
foreach ($documentPath in $currentDocumentationPaths) {
    $documentText = Get-Content -LiteralPath $documentPath -Raw
    $documentation[$documentPath] = $documentText
    $markerCount = [regex]::Matches($documentText, [regex]::Escape($identityMarker)).Count
    if ($markerCount -ne 1) {
        throw "Current release document must contain the exact version-contract marker once: $documentPath"
    }
}

$previewLabel = "Preview $($versionIdentity.previewNumber)"
if ($documentation[$releaseDocumentPath] -cnotmatch ('(?m)^# TabLink ' + [regex]::Escape($releaseVersion) +
        ' ' + [regex]::Escape($previewLabel) + '[：:].+$')) {
    throw 'Current release document heading does not match eng\version.json.'
}
if ($documentation[$verificationDocumentPath] -cnotmatch ('(?m)^# TabLink ' + [regex]::Escape($releaseVersion) +
        ' ' + [regex]::Escape($previewLabel) + ' 验证记录$')) {
    throw 'Current verification document heading does not match eng\version.json.'
}
$candidateIdentityPattern = '当前是 \*\*' + [regex]::Escape($releaseVersion) +
    ' ' + [regex]::Escape($previewLabel) + ' 源码候选\*\*'
$publishedIdentityPattern = 'GitHub 已发布 \*\*' + [regex]::Escape($releaseVersion) +
    ' ' + [regex]::Escape($previewLabel) + '\*\*'
$hasCurrentReleaseIdentity =
    $documentation[$rootReadmePath] -cmatch $candidateIdentityPattern -or
    $documentation[$rootReadmePath] -cmatch $publishedIdentityPattern
if (-not $hasCurrentReleaseIdentity -or
    $documentation[$rootReadmePath] -cnotmatch ('Android 身份为 \*\*' + [regex]::Escape($releaseVersion) +
        ' / build ' + $androidVersionCode + '\*\*')) {
    throw 'Root README current source identity does not match eng\version.json.'
}
if ($documentation[$androidReadmePath] -cnotmatch ('(?m)^# TabLink Android 客户端 ' +
        [regex]::Escape($releaseVersion) + ' ' + [regex]::Escape($previewLabel) + ' 源码候选$')) {
    throw 'Android README heading does not match eng\version.json.'
}
if ($documentation[$publicReleasePath] -cnotmatch ('Windows x64 ' + [regex]::Escape($releaseVersion) +
        ' ' + [regex]::Escape($previewLabel)) -or
    $documentation[$publicReleasePath] -cnotmatch ('Android ' + [regex]::Escape($releaseVersion) +
        ' / versionCode ' + $androidVersionCode)) {
    throw 'Public release scope does not match eng\version.json.'
}
Write-Host "Version contract passed: $releaseTag; Windows=$windowsVersion; Android=$androidVersion; Android versionCode=$androidVersionCode."
