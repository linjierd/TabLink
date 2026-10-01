$ErrorActionPreference = 'Stop'
$buildRoot = $PSScriptRoot
$downloadDirectory = Join-Path $buildRoot 'downloads'
$toolchain = Join-Path $buildRoot 'toolchain'

if (Test-Path -LiteralPath (Join-Path $toolchain 'w64devkit')) {
    throw 'A w64devkit directory already exists; use a fresh source-bundle directory.'
}
if (Test-Path -LiteralPath (Join-Path $toolchain 'cmake-3.31.6-windows-x86_64')) {
    throw 'A CMake directory already exists; use a fresh source-bundle directory.'
}
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $toolchain -Force | Out-Null

function Get-VerifiedArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][long]$Size,
        [Parameter(Mandatory = $true)][string]$Sha256
    )
    $archive = Join-Path $downloadDirectory $Name
    if (-not (Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -Uri $Uri -OutFile $archive
    }
    $file = Get-Item -LiteralPath $archive
    if ($file.Length -ne $Size -or
        -not [string]::Equals((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash,$Sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The downloaded tool archive failed its pinned size or SHA-256 check: $Name"
    }
    return $archive
}

$compilerArchive = Get-VerifiedArchive -Name 'w64devkit-x64-2.0.0.exe' `
    -Uri 'https://github.com/skeeto/w64devkit/releases/download/v2.0.0/w64devkit-x64-2.0.0.exe' `
    -Size 36581551 -Sha256 'cea23fc56a5e61457492113a8377c8ab0c42ed82303fcc454ccd1963a46f8ce1'
$compiler = Start-Process -FilePath $compilerArchive -ArgumentList '-y', ('-o"' + $toolchain + '"') `
    -WindowStyle Hidden -PassThru -Wait
if ($compiler.ExitCode -ne 0) { throw "Portable compiler extraction failed: $($compiler.ExitCode)" }

$cmakeArchive = Get-VerifiedArchive -Name 'cmake-3.31.6-windows-x86_64.zip' `
    -Uri 'https://github.com/Kitware/CMake/releases/download/v3.31.6/cmake-3.31.6-windows-x86_64.zip' `
    -Size 46473549 -Sha256 'd163cd3ab4959b0a53fa8988f2ddbd2e6c501658201e6a154386bad9dbe4f836'
Expand-Archive -LiteralPath $cmakeArchive -DestinationPath $toolchain
$cmake = Join-Path $toolchain 'cmake-3.31.6-windows-x86_64\bin\cmake.exe'
$cmakeVersion = @(& $cmake --version 2>&1 | ForEach-Object { $_.ToString() })
if ($LASTEXITCODE -ne 0 -or $cmakeVersion[0] -ne 'cmake version 3.31.6') {
    throw 'Portable CMake extraction did not produce the pinned CMake 3.31.6 executable.'
}

Write-Output 'Portable compiler and CMake were extracted locally. No system PATH or installed component was changed.'
