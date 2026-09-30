param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [string]$DecoderPath = (Get-Command ffmpeg -ErrorAction Stop).Source
)
$ErrorActionPreference='Stop'
$taskDirectory=(Resolve-Path -LiteralPath $Directory).Path
if(-not [IO.Path]::IsPathFullyQualified($DecoderPath) -or -not (Test-Path -LiteralPath $DecoderPath -PathType Leaf)) {
    throw 'Provide the absolute path of a full FFmpeg build with H.264 demuxing/decoding. The custom capture-only binary is insufficient.'
}
Add-Type -AssemblyName System.Drawing
$records=[Collections.Generic.List[object]]::new()
$passed=$true
foreach($index in @(0,1,2,4)) {
    $source=Join-Path $taskDirectory "screen-$index.h264"
    $sourceHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $expected=switch($index){0{'red'}1{'green'}default{'blue'}}
    foreach($frame in @(0,60)) {
        $png=Join-Path $taskDirectory "screen-$index-frame$frame.png"
        & $DecoderPath -hide_banner -loglevel error -f h264 -i $source -vf "select=eq(n\,$frame)" -frames:v 1 -y $png
        if($LASTEXITCODE -ne 0){throw "Full decoder failed for screen $index frame $frame."}
        $bitmap=[Drawing.Bitmap]::new($png)
        try {
            $red=0;$green=0;$blue=0;$black=0;$count=0
            for($y=4;$y -lt $bitmap.Height;$y+=8) {
                for($x=4;$x -lt $bitmap.Width;$x+=8) {
                    $pixel=$bitmap.GetPixel($x,$y);$r=[int]$pixel.R;$g=[int]$pixel.G;$b=[int]$pixel.B;$count++
                    if($r -gt 65 -and $r -gt 2*$g -and $r -gt 2*$b){$red++}
                    if($g -gt 65 -and $g -gt 2*$r -and $g -gt 2*$b){$green++}
                    if($b -gt 65 -and $b -gt 2*$r -and $b -gt 2*$g){$blue++}
                    if($r+$g+$b -lt 45){$black++}
                }
            }
            $matched=switch($expected){'red'{$red}'green'{$green}'blue'{$blue}}
            $contentPassed=$matched/$count -ge 0.80 -and $black/$count -le 0.05
            # Frame zero is recorded as startup evidence. Frame sixty must show
            # the correct owned pattern; startup black frames cannot hide here.
            if($frame -eq 60 -and -not $contentPassed){$passed=$false}
            $records.Add([pscustomobject]@{
                index=$index;frame=$frame;expected=$expected;width=$bitmap.Width;height=$bitmap.Height
                sourceSha256=$sourceHash;sourceLastWriteUtc=(Get-Item -LiteralPath $source).LastWriteTimeUtc
                png=[IO.Path]::GetFileName($png);pngSha256=(Get-FileHash -LiteralPath $png -Algorithm SHA256).Hash
                sampleCount=$count;redFraction=[Math]::Round($red/$count,4);greenFraction=[Math]::Round($green/$count,4)
                blueFraction=[Math]::Round($blue/$count,4);blackFraction=[Math]::Round($black/$count,4);contentPassed=$contentPassed
            })
        } finally {$bitmap.Dispose()}
    }
}
$report=[pscustomobject]@{
    timestamp=[DateTimeOffset]::Now;success=$passed
    method='Offline full FFmpeg H.264 decode; RGB samples every 8 pixels; expected colour >=80%, black <=5%. Frame 60 is mandatory, frame 0 is startup evidence.'
    limitations='This proves distinct non-black source content, including blue after releasing screen 0. It does not measure unique presentation cadence or physical-device rendering.'
    decoderPath=$DecoderPath;decoderSha256=(Get-FileHash -LiteralPath $DecoderPath -Algorithm SHA256).Hash
    records=$records
}
$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $taskDirectory 'decoded-content-result.json') -Encoding utf8
$records|Select-Object index,frame,expected,redFraction,greenFraction,blueFraction,blackFraction,contentPassed|Format-Table
if(-not $passed){throw 'A mandatory decoded frame did not contain the expected owned display pattern.'}
Write-Output 'PASS all three independent colours and the remaining blue display after releasing one target.'
