param([Parameter(Mandatory=$true)][string]$ProjectDirectory)
$ErrorActionPreference = 'Stop'

$defaultPath = Join-Path $ProjectDirectory 'app\src\main\res\values\strings.xml'
$chinesePath = Join-Path $ProjectDirectory 'app\src\main\res\values-zh-rCN\strings.xml'
[xml]$default = Get-Content -LiteralPath $defaultPath -Raw -Encoding UTF8
[xml]$chinese = Get-Content -LiteralPath $chinesePath -Raw -Encoding UTF8

function Nodes($document, $kind) { @($document.resources.ChildNodes | Where-Object { $_.LocalName -eq $kind }) }
function NamedMap($nodes, $label) {
    $map = @{}
    foreach ($node in $nodes) {
        $name = [string]$node.GetAttribute('name')
        if (-not $name) { throw "$label contains an unnamed resource." }
        if ($map.ContainsKey($name)) { throw "$label contains duplicate resource '$name'." }
        $map[$name] = $node
    }
    $map
}
function Placeholders($value) {
    @( [regex]::Matches([string]$value, '%(?:[0-9]+\$)?[a-zA-Z]') | ForEach-Object Value | Sort-Object ) -join ','
}

$defaultStrings = NamedMap (Nodes $default 'string') 'Default strings'
$chineseStrings = NamedMap (Nodes $chinese 'string') 'Chinese strings'
$requiredStringNames = @($defaultStrings.Keys | Where-Object {
    [string]$defaultStrings[$_].GetAttribute('translatable') -ne 'false'
} | Sort-Object)
$translatedStringNames = @($chineseStrings.Keys | Sort-Object)
if (($requiredStringNames -join "`n") -cne ($translatedStringNames -join "`n")) {
    $missing = @($requiredStringNames | Where-Object { -not $chineseStrings.ContainsKey($_) })
    $extra = @($translatedStringNames | Where-Object { $_ -notin $requiredStringNames })
    throw "Chinese string keys differ. Missing=[$($missing -join ', ')] Extra=[$($extra -join ', ')]"
}
foreach ($name in $requiredStringNames) {
    $expected = Placeholders $defaultStrings[$name].InnerText
    $actual = Placeholders $chineseStrings[$name].InnerText
    if ($expected -cne $actual) { throw "Placeholder mismatch for '$name': '$expected' versus '$actual'." }
}

$defaultArrays = NamedMap (Nodes $default 'string-array') 'Default string arrays'
$chineseArrays = NamedMap (Nodes $chinese 'string-array') 'Chinese string arrays'
$defaultArrayNames = @($defaultArrays.Keys | Sort-Object)
$chineseArrayNames = @($chineseArrays.Keys | Sort-Object)
if (($defaultArrayNames -join "`n") -cne ($chineseArrayNames -join "`n")) {
    throw 'Default and Chinese string-array keys differ.'
}
foreach ($name in $defaultArrayNames) {
    $defaultItems = @($defaultArrays[$name].item)
    $chineseItems = @($chineseArrays[$name].item)
    if ($defaultItems.Count -ne $chineseItems.Count) {
        throw "String-array '$name' item count differs."
    }
}
$defaultLanguageOrder = @($defaultArrays['language_modes'].item | ForEach-Object { [string]$_ })
$chineseLanguageOrder = @($chineseArrays['language_modes'].item | ForEach-Object { [string]$_ })
if (($defaultLanguageOrder -join '|') -cne 'Follow system|Chinese (Simplified)|English' -or
    ($chineseLanguageOrder -join '|') -cne '跟随系统|简体中文|English') {
    throw 'Language arrays must be ordered as system, simplified Chinese, English.'
}

if ((Get-Content -LiteralPath $defaultPath -Raw -Encoding UTF8) -match '[\p{IsCJKUnifiedIdeographs}]') {
    throw 'The default (English) resource file contains Han characters.'
}

$sourceRoot = Join-Path $ProjectDirectory 'app\src\main\java\com\tablink\client'
$allJava = Get-ChildItem -LiteralPath $sourceRoot -Filter '*.java' -File
$definedStrings = @($defaultStrings.Keys)
$definedArrays = @($defaultArrays.Keys)
foreach ($file in $allJava) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    foreach ($match in [regex]::Matches($text, 'R\.string\.([A-Za-z0-9_]+)')) {
        if ($match.Groups[1].Value -notin $definedStrings) {
            throw "$($file.Name) references missing string '$($match.Groups[1].Value)'."
        }
    }
    foreach ($match in [regex]::Matches($text, 'R\.array\.([A-Za-z0-9_]+)')) {
        if ($match.Groups[1].Value -notin $definedArrays) {
            throw "$($file.Name) references missing string-array '$($match.Groups[1].Value)'."
        }
    }
}

$userFacingFiles = @('MainActivity.java','QrScannerActivity.java','AndroidUpdateController.java',
    'UpdateInstallReceiver.java','VideoDecoder.java')
foreach ($name in $userFacingFiles) {
    $path = Join-Path $sourceRoot $name
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    if ($text -match '[\p{IsCJKUnifiedIdeographs}]') {
        throw "$name contains hard-coded Han user-facing text."
    }
    $directUiLiteral = '(?m)(?:setText|setTitle|setMessage|setPositiveButton|setNegativeButton|setNeutralButton|setContentDescription|showMessage|publish|button|text)\(\s*"[^"\r\n]*[A-Za-z]'
    if ($text -match $directUiLiteral) {
        throw "$name contains a direct hard-coded user-facing string: $($Matches[0])"
    }
    $toastLiteral = '(?m)Toast\.makeText\([^,\r\n]+,\s*"[^"\r\n]*[A-Za-z]'
    if ($text -match $toastLiteral) {
        throw "$name contains a hard-coded Toast string: $($Matches[0])"
    }
}

$mainActivityText = Get-Content -LiteralPath (Join-Path $sourceRoot 'MainActivity.java') -Raw -Encoding UTF8
if ($mainActivityText -notmatch 'boolean\s+activeDisplay\s*=\s*current\s*!=\s*null\s*&&\s*current\.running\s*;') {
    throw 'Language switching must refresh every running session in place, including authentication and recovery.'
}
if ($mainActivityText -notmatch 'LanguageSwitchPolicy\.sessionState\(current\.running,\s*current\.connected,\s*current\.reconnecting\)') {
    throw 'In-place language refresh must preserve connecting and reconnecting session semantics.'
}
$appLanguageText = Get-Content -LiteralPath (Join-Path $sourceRoot 'AppLanguage.java') -Raw -Encoding UTF8
if ($appLanguageText -notmatch 'Resources\.getSystem\(\)\.getConfiguration\(\)') {
    throw 'Follow-system language resolution must read the unmodified system configuration.'
}
$installReceiverText = Get-Content -LiteralPath (Join-Path $sourceRoot 'UpdateInstallReceiver.java') -Raw -Encoding UTF8
if ($installReceiverText -notmatch 'AppLanguage\.isChinese\(context\)\s*!=\s*containsHan\(detail\)') {
    throw 'Installer detail language filtering must be symmetric for Chinese and English modes.'
}

Write-Output "Android localization: $($requiredStringNames.Count) translated strings, $($defaultArrayNames.Count) translated arrays, resource references and user-facing hard-coded text audit passed."
