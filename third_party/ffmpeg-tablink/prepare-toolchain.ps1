$ErrorActionPreference = 'Stop'
$buildRoot = $PSScriptRoot
$downloadDirectory = Join-Path $buildRoot 'downloads'
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$archive = Join-Path $downloadDirectory 'w64devkit-x64-2.0.0.exe'
$expectedSha256 = 'cea23fc56a5e61457492113a8377c8ab0c42ed82303fcc454ccd1963a46f8ce1'
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri 'https://github.com/skeeto/w64devkit/releases/download/v2.0.0/w64devkit-x64-2.0.0.exe' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedSha256) {
    throw 'The portable compiler archive does not match the recorded release hash. It was not executed.'
}
$toolchain = Join-Path $buildRoot 'toolchain'
if (Test-Path -LiteralPath (Join-Path $toolchain 'w64devkit')) {
    throw 'A toolchain directory already exists; leave it intact or choose a fresh source-bundle directory.'
}
New-Item -ItemType Directory -Path $toolchain -Force | Out-Null
$extract = Start-Process -FilePath $archive -ArgumentList '-y', ('-o"' + $toolchain + '"') -WindowStyle Hidden -PassThru
$extract.WaitForExit()
if ($extract.ExitCode -ne 0) { throw "Portable toolchain extraction failed: $($extract.ExitCode)" }
Write-Output 'Portable compiler extracted locally. No system PATH or installed components were changed.'
