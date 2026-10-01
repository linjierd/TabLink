param(
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetPathRoot($PSScriptRoot)) ('TabLink-ffmpeg-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$buildRoot = $PSScriptRoot
# GCC 14's linker cannot reliably use this project's Chinese path. Stage an
# isolated ASCII build on the source drive; never write to the user's TEMP,
# change machine/user PATH, or install a toolchain.
if ($WorkDirectory -match '[^\x00-\x7F]') { throw 'Choose an ASCII-only temporary build directory.' }
if (Test-Path -LiteralPath $WorkDirectory) { throw 'Choose a new, empty build directory.' }
New-Item -ItemType Directory -Path $WorkDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $buildRoot 'source'), (Join-Path $buildRoot 'toolchain') -Destination $WorkDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $buildRoot 'build.sh') -Destination $WorkDirectory
$previousPath = $env:PATH
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousTmpDir = $env:TMPDIR
Push-Location -LiteralPath $WorkDirectory
try {
    $env:PATH = (Join-Path $WorkDirectory 'toolchain\w64devkit\bin') + ';' + $previousPath
    $childTemp = Join-Path $WorkDirectory 'temp'
    New-Item -ItemType Directory -Path $childTemp | Out-Null
    $env:TEMP = $childTemp
    $env:TMP = $childTemp
    $env:TMPDIR = $childTemp.Replace('\', '/')
    $stdoutLog = Join-Path $WorkDirectory 'build.stdout.log'
    $stderrLog = Join-Path $WorkDirectory 'build.stderr.log'
    $buildProcess = Start-Process -FilePath (Join-Path $WorkDirectory 'toolchain\w64devkit\bin\sh.exe') `
        -ArgumentList (Join-Path $WorkDirectory 'build.sh') -WorkingDirectory $WorkDirectory `
        -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -NoNewWindow -Wait -PassThru
    $utf8NoBom = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $buildRoot 'build.log'),
        ([IO.File]::ReadAllText($stdoutLog) + [IO.File]::ReadAllText($stderrLog)), $utf8NoBom)
    if ($buildProcess.ExitCode -ne 0) { throw "Build failed ($($buildProcess.ExitCode)); see build.log and $WorkDirectory\build\ffbuild\config.log" }
    New-Item -ItemType Directory -Path (Join-Path $buildRoot 'bin') -Force | Out-Null
    $builtBinary = Join-Path $WorkDirectory 'bin\ffmpeg.exe'
    $binaryBytes = [IO.File]::ReadAllBytes($builtBinary)
    $userPathPattern = '(?i)[A-Z]:[\\/]+Users[\\/]'
    if ([Text.Encoding]::ASCII.GetString($binaryBytes) -match $userPathPattern -or
        [Text.Encoding]::Unicode.GetString($binaryBytes) -match $userPathPattern) {
        throw 'Built FFmpeg exposes a builder-specific Users path; the binary was not published.'
    }
    Copy-Item -LiteralPath $builtBinary -Destination (Join-Path $buildRoot 'bin\ffmpeg.exe') -Force
    Copy-Item -LiteralPath (Join-Path $buildRoot 'source\ffmpeg-7.0.2\COPYING.LGPLv2.1'), (Join-Path $buildRoot 'toolchain\w64devkit\COPYING.MinGW-w64-runtime.txt') -Destination (Join-Path $buildRoot 'bin') -Force
    Write-Output "Built: $buildRoot\bin\ffmpeg.exe"
    Write-Output "Retained compiler objects/configuration for review: $WorkDirectory\build"
} finally {
    $env:PATH = $previousPath
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    $env:TMPDIR = $previousTmpDir
    Pop-Location
}
