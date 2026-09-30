param([string]$ToolchainDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $ToolchainDirectory) { $ToolchainDirectory = Join-Path $projectRoot 'third_party\ffmpeg-tablink\toolchain\w64devkit' }
if (-not (Test-Path -LiteralPath (Join-Path $ToolchainDirectory 'bin\g++.exe'))) { throw 'Portable w64devkit compiler is missing.' }
$stage = Join-Path $env:TEMP ('TabLink-native-motion-' + [Guid]::NewGuid().ToString('N'))
if ($stage -match '[^\x00-\x7F]') { throw 'Use an ASCII-only TEMP directory for the native compiler.' }
New-Item -ItemType Directory -Path $stage | Out-Null
if ($ToolchainDirectory -match '[^\x00-\x7F]') {
    Copy-Item -LiteralPath $ToolchainDirectory -Destination (Join-Path $stage 'w64devkit') -Recurse
    $ToolchainDirectory = Join-Path $stage 'w64devkit'
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'd3d-motion-probe.cpp') -Destination $stage
$previousPath = $env:PATH
try {
    $env:PATH = (Join-Path $ToolchainDirectory 'bin') + ';' + $previousPath
    & (Join-Path $ToolchainDirectory 'bin\g++.exe') -std=c++17 -O2 -municode (Join-Path $stage 'd3d-motion-probe.cpp') -o (Join-Path $stage 'd3d-motion-probe.exe') -ld3d11 -ldxgi -luser32 -lgdi32 -luuid
    if ($LASTEXITCODE -ne 0) { throw 'Native motion helper build failed.' }
    & dotnet build (Join-Path $projectRoot 'tests\TabLink.Video.Tests\TabLink.Video.Tests.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Managed motion guard build failed.' }
    $output = Join-Path $projectRoot 'tests\TabLink.Video.Tests\bin\Release\net10.0-windows\d3d-motion-probe.exe'
    Copy-Item -LiteralPath (Join-Path $stage 'd3d-motion-probe.exe') -Destination $output -Force
    Write-Output $output
} finally { $env:PATH = $previousPath }
