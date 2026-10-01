[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$windowsProjectPath = Join-Path $resolvedRepositoryRoot 'src\TabLink.Windows\TabLink.Windows.csproj'
$androidBuildPath = Join-Path $resolvedRepositoryRoot 'android\app\build.gradle'

foreach ($requiredPath in @($windowsProjectPath, $androidBuildPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required version source was not found: $requiredPath"
    }
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

Write-Host "Version contract passed: Windows=$windowsVersion; Android=$androidVersion; Android versionCode=$androidVersionCode."
