[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)

function Read-StrictUtf8([string]$RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required GitHub language document is missing: $RelativePath"
    }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "GitHub language document must be UTF-8 without BOM: $RelativePath"
    }
    try {
        return $utf8.GetString($bytes)
    }
    catch {
        throw "GitHub language document is not strict UTF-8: $RelativePath"
    }
}

$versionIdentity = Get-Content -LiteralPath (Join-Path $root 'eng\version.json') -Raw | ConvertFrom-Json
$version = [string]$versionIdentity.version
$pairs = @(
    @('README.md', 'README.zh-CN.md'),
    @('SECURITY.md', 'SECURITY.zh-CN.md'),
    @('CONTRIBUTING.md', 'CONTRIBUTING.zh-CN.md'),
    @('AUTHORS.md', 'AUTHORS.zh-CN.md'),
    @("RELEASE-$version.md", "RELEASE-$version.zh-CN.md"),
    @("VERIFICATION-$version.md", "VERIFICATION-$version.zh-CN.md"),
    @('compatibility\README.md', 'compatibility\README.zh-CN.md')
)

foreach ($pair in $pairs) {
    $englishName, $chineseName = $pair
    $english = Read-StrictUtf8 $englishName
    $chinese = Read-StrictUtf8 $chineseName
    $englishLeaf = Split-Path -Leaf $englishName
    $chineseLeaf = Split-Path -Leaf $chineseName
    $englishPrefix = $english.Substring(0, [Math]::Min(800, $english.Length))
    $chinesePrefix = $chinese.Substring(0, [Math]::Min(800, $chinese.Length))
    if ($englishPrefix -cnotmatch ('\*\*English \(Singapore\)\*\*.*\[简体中文\]\(' +
            [regex]::Escape($chineseLeaf) + '\)')) {
        throw "The canonical en-SG document must show English first and link its Chinese mirror: $englishName"
    }
    if ($chinesePrefix -cnotmatch ('\[English \(Singapore\)\]\(' + [regex]::Escape($englishLeaf) +
            '\).*\*\*简体中文\*\*')) {
        throw "The Chinese mirror must link the en-SG default first and mark Chinese as current: $chineseName"
    }
    $englishHeading = ($english -split "`n", 2)[0].TrimEnd("`r")
    $chineseHeading = ($chinese -split "`n", 2)[0].TrimEnd("`r")
    if ($englishHeading -cmatch '[\u4e00-\u9fff]') {
        throw "The canonical document heading must be English (Singapore): $englishName"
    }
    if ($chineseHeading -cnotmatch '[\u4e00-\u9fff]') {
        throw "The Chinese mirror heading must contain Chinese text: $chineseName"
    }
}

$chineseRootReadme = Read-StrictUtf8 'README.zh-CN.md'
foreach ($target in @(
        'README.md',
        'AUTHORS.zh-CN.md',
        'compatibility/README.zh-CN.md',
        "RELEASE-$version.zh-CN.md",
        "VERIFICATION-$version.zh-CN.md")) {
    if ($chineseRootReadme -cnotmatch ('\]\(' + [regex]::Escape($target) + '(?:#[^)]+)?\)')) {
        throw "Chinese root README must keep readers in the Chinese documentation path: $target"
    }
}
$englishAuthors = Read-StrictUtf8 'AUTHORS.md'
if (-not $englishAuthors.Contains('[Linjie / Development Notes](https://linjie.space/)',
        [StringComparison]::Ordinal)) {
    throw 'English author page must use the English blog label.'
}
$englishContributing = Read-StrictUtf8 'CONTRIBUTING.md'
if (-not $englishContributing.Contains('After editing the catalogue,', [StringComparison]::Ordinal)) {
    throw 'English contribution guidance must use the en-SG catalogue spelling in prose.'
}

$issueTemplateDirectory = Join-Path $root '.github\ISSUE_TEMPLATE'
$issueTemplates = @(Get-ChildItem -LiteralPath $issueTemplateDirectory -Filter '*.yml' -File |
    Where-Object Name -ne 'config.yml' | Sort-Object Name)
if ($issueTemplates.Count -ne 4) {
    throw "Expected four bilingual GitHub Issue forms; found $($issueTemplates.Count)."
}
foreach ($template in $issueTemplates) {
    $text = Read-StrictUtf8 ('.github\ISSUE_TEMPLATE\' + $template.Name)
    if ($text -cnotmatch '(?m)^name: "[^"\r\n]+ / [^"\r\n]*[\u4e00-\u9fff][^"\r\n]*"\r?$' -or
        $text -cnotmatch '(?m)^description: "[^"\r\n]+ / [^"\r\n]*[\u4e00-\u9fff][^"\r\n]*"\r?$') {
        throw "Issue form name and description must put English before Chinese: $($template.Name)"
    }
    $fieldLabels = [regex]::Matches($text, '(?m)^\s+label: "(?<label>[^"\r\n]+)"\r?$')
    if ($fieldLabels.Count -eq 0) {
        throw "Issue form has no labelled input fields: $($template.Name)"
    }
    foreach ($match in $fieldLabels) {
        $label = $match.Groups['label'].Value
        if ($label -cnotmatch '^[^/]+ / .*[\u4e00-\u9fff]') {
            throw "Issue form labels must put English before Chinese: $($template.Name): $label"
        }
    }
    if ($text -cnotmatch 'This page is public' -or $text -cnotmatch '这里是公开页面' -or
        $text -cnotmatch '(?i)serial' -or $text -cnotmatch '序列') {
        throw "Issue form must retain the bilingual public-page privacy boundary: $($template.Name)"
    }
}

$issueConfig = Read-StrictUtf8 '.github\ISSUE_TEMPLATE\config.yml'
if ($issueConfig -cnotmatch '(?m)^blank_issues_enabled: false\r?$' -or
    $issueConfig -cnotmatch '(?m)^\s+- name: "[^"\r\n]+ / [^"\r\n]*[\u4e00-\u9fff][^"\r\n]*"\r?$' -or
    $issueConfig -cnotmatch '(?i)security/advisories/new') {
    throw 'Issue form configuration must stay closed to blank public issues and bilingual.'
}

$pullRequestTemplate = Read-StrictUtf8 '.github\pull_request_template.md'
foreach ($requiredHeading in @(
        '# English',
        '## What changed',
        '## Validation',
        '## Security and privacy',
        '# 简体中文',
        '## 变更内容',
        '## 验证',
        '## 安全与隐私')) {
    if (-not $pullRequestTemplate.Contains($requiredHeading, [StringComparison]::Ordinal)) {
        throw "Pull request template is missing bilingual heading: $requiredHeading"
    }
}

Write-Host "GitHub language contract passed: English (Singapore) is canonical; Chinese mirrors and bilingual forms are present."
