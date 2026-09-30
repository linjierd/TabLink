param([string]$WorkDirectory = (Join-Path $env:TEMP ('TabLink-ffmpeg-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$buildRoot = $PSScriptRoot
# GCC 14's linker cannot reliably use this project's Chinese path. Stage an
# isolated ASCII build; never change machine/user PATH or install a toolchain.
if ($WorkDirectory -match '[^\x00-\x7F]') { throw 'Choose an ASCII-only temporary build directory.' }
if (Test-Path -LiteralPath $WorkDirectory) { throw 'Choose a new, empty build directory.' }
New-Item -ItemType Directory -Path $WorkDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $buildRoot 'source'), (Join-Path $buildRoot 'toolchain') -Destination $WorkDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $buildRoot 'build.sh') -Destination $WorkDirectory
$previousPath = $env:PATH
Push-Location -LiteralPath $WorkDirectory
try {
    $env:PATH = (Join-Path $WorkDirectory 'toolchain\w64devkit\bin') + ';' + $previousPath
    & (Join-Path $WorkDirectory 'toolchain\w64devkit\bin\sh.exe') (Join-Path $WorkDirectory 'build.sh') *> (Join-Path $buildRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE); see build.log and $WorkDirectory\build\ffbuild\config.log" }
    New-Item -ItemType Directory -Path (Join-Path $buildRoot 'bin') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $WorkDirectory 'bin\ffmpeg.exe') -Destination (Join-Path $buildRoot 'bin\ffmpeg.exe') -Force
    Copy-Item -LiteralPath (Join-Path $buildRoot 'source\ffmpeg-7.0.2\COPYING.LGPLv2.1'), (Join-Path $buildRoot 'toolchain\w64devkit\COPYING.MinGW-w64-runtime.txt') -Destination (Join-Path $buildRoot 'bin') -Force
    Write-Output "Built: $buildRoot\bin\ffmpeg.exe"
    Write-Output "Retained compiler objects/configuration for review: $WorkDirectory\build"
} finally {
    $env:PATH = $previousPath
    Pop-Location
}
