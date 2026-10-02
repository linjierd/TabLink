[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)

function Read-StrictUtf8([string]$RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required localisation file is missing: $RelativePath"
    }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "Localisation files must be UTF-8 without BOM: $RelativePath"
    }
    try { return $strictUtf8.GetString($bytes) }
    catch { throw "Localisation file is not strict UTF-8: $RelativePath" }
}

function Test-OrdinalContains([string]$Text, [string]$Value) {
    return $Text.IndexOf($Value, [StringComparison]::Ordinal) -ge 0
}

function Assert-UniqueAndReturnSet([string[]]$Keys, [string]$Description) {
    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($key in $Keys) {
        if ([string]::IsNullOrWhiteSpace($key)) { throw "$Description contains an empty resource key." }
        if (-not $set.Add($key)) { throw "$Description contains a duplicate resource key: $key" }
    }
    if ($set.Count -eq 0) { throw "$Description contains no localisable resource keys." }
    return $set
}

function Assert-SameKeys($Expected, $Actual, [string]$Description) {
    $missing = @($Expected | Where-Object { -not $Actual.Contains($_) } | Sort-Object)
    $unexpected = @($Actual | Where-Object { -not $Expected.Contains($_) } | Sort-Object)
    if ($missing.Count -ne 0 -or $unexpected.Count -ne 0) {
        throw "$Description resource keys differ. Missing: $($missing -join ', '); unexpected: $($unexpected -join ', ')."
    }
}

function Get-AndroidKeys([string]$RelativePath) {
    $text = Read-StrictUtf8 $RelativePath
    try { [xml]$document = $text }
    catch { throw "Android localisation XML is invalid: $RelativePath" }
    $keys = @(
        foreach ($node in $document.resources.ChildNodes) {
            if ($node.NodeType -ne [Xml.XmlNodeType]::Element -or -not $node.HasAttribute('name')) { continue }
            if ($node.HasAttribute('translatable') -and $node.GetAttribute('translatable') -eq 'false') { continue }
            '{0}:{1}' -f $node.LocalName, $node.GetAttribute('name')
        }
    )
    return Assert-UniqueAndReturnSet $keys "Android $RelativePath"
}

function Get-AppleKeys([string]$RelativePath) {
    $text = Read-StrictUtf8 $RelativePath
    $matches = [regex]::Matches($text, '(?m)^\s*"(?<key>(?:[^"\\]|\\.)+)"\s*=')
    return Assert-UniqueAndReturnSet @($matches | ForEach-Object { $_.Groups['key'].Value }) "Apple $RelativePath"
}

function Get-HarmonyKeys([string]$RelativePath) {
    $text = Read-StrictUtf8 $RelativePath
    try { $document = $text | ConvertFrom-Json }
    catch { throw "HarmonyOS localisation JSON is invalid: $RelativePath" }
    if ($null -eq $document.string) { throw "HarmonyOS localisation JSON has no string collection: $RelativePath" }
    return Assert-UniqueAndReturnSet @($document.string | ForEach-Object { [string]$_.name }) "HarmonyOS $RelativePath"
}

$androidEnglish = Get-AndroidKeys 'android\app\src\main\res\values\strings.xml'
$androidChinese = Get-AndroidKeys 'android\app\src\main\res\values-zh-rCN\strings.xml'
Assert-SameKeys $androidEnglish $androidChinese 'Android English and Simplified Chinese'

$appleEnglish = Get-AppleKeys 'native\apple\Resources\en.lproj\Localizable.strings'
$appleChinese = Get-AppleKeys 'native\apple\Resources\zh-Hans.lproj\Localizable.strings'
Assert-SameKeys $appleEnglish $appleChinese 'Apple English and Simplified Chinese'
$appleInfoEnglish = Get-AppleKeys 'native\apple\Resources\en.lproj\InfoPlist.strings'
$appleInfoChinese = Get-AppleKeys 'native\apple\Resources\zh-Hans.lproj\InfoPlist.strings'
Assert-SameKeys $appleInfoEnglish $appleInfoChinese 'Apple Info.plist English and Simplified Chinese'

$harmonyBase = Get-HarmonyKeys 'native\harmony\entry\src\main\resources\base\element\string.json'
$harmonyEnglish = Get-HarmonyKeys 'native\harmony\entry\src\main\resources\en_US\element\string.json'
$harmonyChinese = Get-HarmonyKeys 'native\harmony\entry\src\main\resources\zh_CN\element\string.json'
Assert-SameKeys $harmonyBase $harmonyEnglish 'HarmonyOS base and English'
Assert-SameKeys $harmonyBase $harmonyChinese 'HarmonyOS base and Simplified Chinese'
$harmonyAppBase = Get-HarmonyKeys 'native\harmony\AppScope\resources\base\element\string.json'
$harmonyAppEnglish = Get-HarmonyKeys 'native\harmony\AppScope\resources\en_US\element\string.json'
$harmonyAppChinese = Get-HarmonyKeys 'native\harmony\AppScope\resources\zh_CN\element\string.json'
Assert-SameKeys $harmonyAppBase $harmonyAppEnglish 'HarmonyOS app-name base and English'
Assert-SameKeys $harmonyAppBase $harmonyAppChinese 'HarmonyOS app-name base and Simplified Chinese'

$windowsPreference = Read-StrictUtf8 'src\TabLink.Core\LanguagePreferences.cs'
foreach ($required in @('ProductLanguageMode', 'System', 'SimplifiedChinese', 'English', '"system"', '"zh-CN"', '"en"')) {
    if (-not (Test-OrdinalContains $windowsPreference $required)) {
        throw "Windows language preference contract is missing: $required"
    }
}
if (-not (Test-OrdinalContains $windowsPreference 'StartsWith("zh"')) {
    throw 'Windows system-language mode must map every zh-* culture to Simplified Chinese.'
}
$windowsUi = (Read-StrictUtf8 'src\TabLink.Windows\MainForm.Localization.cs') + "`n" +
    (Read-StrictUtf8 'src\TabLink.Windows\WindowsUiText.cs')
foreach ($required in @('Follow system', '跟随系统', 'ProductLanguageMode.System', 'ApplyUiLanguage')) {
    if (-not (Test-OrdinalContains $windowsUi $required)) {
        throw "Windows language settings UI is missing: $required"
    }
}

$androidPreference = Read-StrictUtf8 'android\app\src\main\java\com\tablink\client\LanguagePreference.java'
foreach ($required in @('SYSTEM', 'ENGLISH', 'CHINESE_SIMPLIFIED')) {
    if (-not (Test-OrdinalContains $androidPreference $required)) {
        throw "Android language preference contract is missing: $required"
    }
}
if (-not (Test-OrdinalContains $androidPreference 'startsWith("zh-")') -or
    -not (Test-OrdinalContains $androidPreference '"zh-CN" : "en"')) {
    throw 'Android system-language mode must map every zh-* locale to Simplified Chinese and all other locales to English.'
}

$browserHtml = Read-StrictUtf8 'browser\assets\index.html'
$browserScript = Read-StrictUtf8 'browser\assets\client.js'
foreach ($required in @('value="system"', 'value="zh-CN"', 'value="en"')) {
    if (-not (Test-OrdinalContains $browserHtml $required)) {
        throw "Browser language selector is missing: $required"
    }
}
foreach ($required in @('tablink.language.v1', 'navigator.language', 'localStorage', '"zh-CN"', 'strings')) {
    if (-not (Test-OrdinalContains $browserScript $required)) {
        throw "Browser language implementation is missing: $required"
    }
}
if (-not (Test-OrdinalContains $browserScript '.startsWith("zh")?"zh-CN":"en"')) {
    throw 'Browser system-language mode must map every zh-* locale to Simplified Chinese and all other locales to English.'
}
$browserObjects = [regex]::Match($browserScript,
    'en:\{(?<english>.*?)\},\s*"zh-CN":\{(?<chinese>.*?)\}\s*\r?\n\s*\};',
    [Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $browserObjects.Success) {
    throw 'Browser English and Simplified Chinese string dictionaries could not be parsed.'
}
$browserKeyPattern = '(?:(?<=^)|(?<=,))\s*(?<key>[A-Za-z][A-Za-z0-9]*)\s*:'
$browserEnglish = Assert-UniqueAndReturnSet @(
    [regex]::Matches($browserObjects.Groups['english'].Value, $browserKeyPattern) |
        ForEach-Object { $_.Groups['key'].Value }
) 'Browser English'
$browserChinese = Assert-UniqueAndReturnSet @(
    [regex]::Matches($browserObjects.Groups['chinese'].Value, $browserKeyPattern) |
        ForEach-Object { $_.Groups['key'].Value }
) 'Browser Simplified Chinese'
Assert-SameKeys $browserEnglish $browserChinese 'Browser English and Simplified Chinese'

$appleImplementation = Read-StrictUtf8 'native\apple\Sources\App\AppLocalization.swift'
foreach ($required in @('case system', 'case simplifiedChinese', 'case english', 'UserDefaults.standard')) {
    if (-not (Test-OrdinalContains $appleImplementation $required)) {
        throw "Apple language preference contract is missing: $required"
    }
}
if (-not (Test-OrdinalContains $appleImplementation 'Locale.preferredLanguages.first') -or
    -not (Test-OrdinalContains $appleImplementation 'hasPrefix("zh") ? "zh-Hans" : "en"')) {
    throw 'Apple system-language mode must map every zh-* locale to Simplified Chinese and all other locales to English.'
}
$harmonyImplementation = Read-StrictUtf8 'native\harmony\entry\src\main\ets\localization\AppLocalization.ets'
foreach ($required in @("'system'", "'zhHans'", "'english'", 'preferences.getPreferences', 'setAppPreferredLanguage')) {
    if (-not (Test-OrdinalContains $harmonyImplementation $required)) {
        throw "HarmonyOS language preference contract is missing: $required"
    }
}
if (-not (Test-OrdinalContains $harmonyImplementation 'getSystemLanguage().toLowerCase()') -or
    -not (Test-OrdinalContains $harmonyImplementation "systemLanguage.startsWith('zh') ? 'zh-CN' : 'en'")) {
    throw 'HarmonyOS system-language mode must map every zh-* locale to Simplified Chinese and all other locales to English.'
}

$readmeEnglish = Read-StrictUtf8 'README.md'
$readmeChinese = Read-StrictUtf8 'README.zh-CN.md'
if (-not (Test-OrdinalContains $readmeEnglish '## Interface languages') -or
    -not (Test-OrdinalContains $readmeChinese '## 界面语言')) {
    throw 'Both root README languages must document the product interface-language behaviour.'
}

$successMessage = ("Localisation contract passed: Android {0}, Apple {1}, HarmonyOS {2} paired keys; " +
    'Windows, browser, Android, Apple and HarmonyOS expose System / Simplified Chinese / English.') -f `
    $androidEnglish.Count, $appleEnglish.Count, $harmonyBase.Count
Write-Host $successMessage
