param(
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetPathRoot($PSScriptRoot)) ('TabLink-ffmpeg-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$buildRoot = $PSScriptRoot
$sourceDrive = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($buildRoot))
$workDrive = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($WorkDirectory))

# GCC cannot reliably link this project from its Chinese checkout path. Stage a
# new ASCII-only directory on the same drive and keep every temporary file there.
if ($WorkDirectory -match '[^\x00-\x7F]') { throw 'Choose an ASCII-only temporary build directory.' }
if (-not [string]::Equals($sourceDrive, $workDrive, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The FFmpeg staging directory must stay on the same drive as this source bundle.'
}
if (Test-Path -LiteralPath $WorkDirectory) { throw 'Choose a new, empty build directory.' }
foreach ($required in @('source\ffmpeg-7.0.2','source\nv-codec-headers-n12.2.72.0','source\oneVPL-2.11.0',
    'source\AMF-1.4.35','source\x264-b35605ace3dd','toolchain\w64devkit',
    'toolchain\cmake-3.31.6-windows-x86_64','build.sh')) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildRoot $required))) { throw "Missing build input: $required" }
}

New-Item -ItemType Directory -Path $WorkDirectory | Out-Null
New-Item -ItemType Directory -Path (Join-Path $WorkDirectory 'source') | Out-Null
foreach ($sourceName in @('ffmpeg-7.0.2','nv-codec-headers-n12.2.72.0','oneVPL-2.11.0','x264-b35605ace3dd')) {
    Copy-Item -LiteralPath (Join-Path $buildRoot ('source\' + $sourceName)) -Destination (Join-Path $WorkDirectory 'source') -Recurse
}
$stagedAmf = Join-Path $WorkDirectory 'source\AMF-1.4.35'
New-Item -ItemType Directory -Path (Join-Path $stagedAmf 'amf\public\include') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $buildRoot 'source\AMF-1.4.35\LICENSE.txt') -Destination $stagedAmf
Copy-Item -LiteralPath (Join-Path $buildRoot 'source\AMF-1.4.35\amf\public\include\core') -Destination (Join-Path $stagedAmf 'amf\public\include') -Recurse
Copy-Item -LiteralPath (Join-Path $buildRoot 'source\AMF-1.4.35\amf\public\include\components') -Destination (Join-Path $stagedAmf 'amf\public\include') -Recurse
Copy-Item -LiteralPath (Join-Path $buildRoot 'toolchain') -Destination $WorkDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $buildRoot 'build.sh') -Destination $WorkDirectory
$previousPath = $env:PATH
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousTmpDir = $env:TMPDIR
$previousEpoch = $env:SOURCE_DATE_EPOCH
Push-Location -LiteralPath $WorkDirectory
try {
    $toolBin = Join-Path $WorkDirectory 'toolchain\w64devkit\bin'
    $cmake = Join-Path $WorkDirectory 'toolchain\cmake-3.31.6-windows-x86_64\bin\cmake.exe'
    $env:PATH = $toolBin + ';' + $previousPath
    $childTemp = Join-Path $WorkDirectory 'temp'
    New-Item -ItemType Directory -Path $childTemp | Out-Null
    $env:TEMP = $childTemp
    $env:TMP = $childTemp
    $env:TMPDIR = $childTemp.Replace('\', '/')
    $env:SOURCE_DATE_EPOCH = '0'
    $cmakeVersion = @(& $cmake --version 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0 -or $cmakeVersion[0] -ne 'cmake version 3.31.6') {
        throw 'The pinned portable CMake 3.31.6 toolchain is missing or changed.'
    }

    $hardwareDeps = Join-Path $WorkDirectory 'deps\hardware'
    $vplBuild = Join-Path $WorkDirectory 'build\onevpl'
    New-Item -ItemType Directory -Path $hardwareDeps -Force | Out-Null
    & $cmake `
        -S (Join-Path $WorkDirectory 'source\oneVPL-2.11.0') `
        -B $vplBuild `
        -G 'MinGW Makefiles' `
        '-DCMAKE_BUILD_TYPE=Release' `
        '-DBUILD_SHARED_LIBS=OFF' `
        '-DBUILD_TESTS=OFF' `
        '-DBUILD_EXAMPLES=OFF' `
        "-DCMAKE_MAKE_PROGRAM=$((Join-Path $toolBin 'make.exe') -replace '\\','/')" `
        "-DCMAKE_INSTALL_PREFIX=$($hardwareDeps -replace '\\','/')" `
        "-DCMAKE_C_COMPILER=$((Join-Path $toolBin 'gcc.exe') -replace '\\','/')" `
        "-DCMAKE_CXX_COMPILER=$((Join-Path $toolBin 'g++.exe') -replace '\\','/')"
    if ($LASTEXITCODE -ne 0) { throw "oneVPL configure failed: $LASTEXITCODE" }
    & $cmake --build $vplBuild --parallel 8
    if ($LASTEXITCODE -ne 0) { throw "oneVPL build failed: $LASTEXITCODE" }
    & $cmake --install $vplBuild
    if ($LASTEXITCODE -ne 0) { throw "oneVPL install failed: $LASTEXITCODE" }

    $amfTarget = Join-Path $hardwareDeps 'include\AMF'
    New-Item -ItemType Directory -Path $amfTarget -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $WorkDirectory 'source\AMF-1.4.35\amf\public\include\core') -Destination $amfTarget -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $WorkDirectory 'source\AMF-1.4.35\amf\public\include\components') -Destination $amfTarget -Recurse -Force

    $stdoutLog = Join-Path $WorkDirectory 'build.stdout.log'
    $stderrLog = Join-Path $WorkDirectory 'build.stderr.log'
    $buildProcess = Start-Process -FilePath (Join-Path $toolBin 'sh.exe') `
        -ArgumentList (Join-Path $WorkDirectory 'build.sh') -WorkingDirectory $WorkDirectory `
        -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -NoNewWindow -Wait -PassThru
    $utf8NoBom = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $buildRoot 'build.log'),
        ([IO.File]::ReadAllText($stdoutLog) + [IO.File]::ReadAllText($stderrLog)), $utf8NoBom)
    if ($buildProcess.ExitCode -ne 0) {
        throw "Build failed ($($buildProcess.ExitCode)); see build.log and the staged build directories under $WorkDirectory"
    }

    $builtHardware = Join-Path $WorkDirectory 'bin\ffmpeg.exe'
    $builtSoftware = Join-Path $WorkDirectory 'bin\ffmpeg-x264.exe'
    $personalPathPattern = '(?i)(?:[A-Z]:[\\/]+Users[\\/]|/[A-Z]/Users/)'
    foreach ($binary in @($builtHardware, $builtSoftware)) {
        if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing built FFmpeg helper: $binary" }
        $bytes = [IO.File]::ReadAllBytes($binary)
        $views = @([Text.Encoding]::ASCII.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes),
            [Text.Encoding]::BigEndianUnicode.GetString($bytes))
        $version = @(& $binary -version 2>&1 | ForEach-Object { $_.ToString() }) -join "`n"
        $buildConfiguration = @(& $binary -buildconf 2>&1 | ForEach-Object { $_.ToString() }) -join "`n"
        if ($LASTEXITCODE -ne 0 -or $null -ne ($views | Where-Object { $_ -match $personalPathPattern } | Select-Object -First 1) -or
            $version -match $personalPathPattern -or $buildConfiguration -match $personalPathPattern) {
            throw "Built FFmpeg helper exposes a builder-specific Users path or failed its audit: $binary"
        }
    }

    $binaryOutput = Join-Path $buildRoot 'bin'
    New-Item -ItemType Directory -Path $binaryOutput -Force | Out-Null
    Copy-Item -LiteralPath $builtHardware -Destination (Join-Path $binaryOutput 'ffmpeg.exe') -Force
    Copy-Item -LiteralPath $builtSoftware -Destination (Join-Path $binaryOutput 'ffmpeg-x264.exe') -Force
    Copy-Item -LiteralPath (Join-Path $buildRoot 'source\ffmpeg-7.0.2\COPYING.LGPLv2.1') -Destination (Join-Path $binaryOutput 'COPYING.LGPLv2.1') -Force
    Copy-Item -LiteralPath (Join-Path $buildRoot 'source\ffmpeg-7.0.2\COPYING.GPLv2') -Destination (Join-Path $binaryOutput 'COPYING.GPLv2') -Force
    Copy-Item -LiteralPath (Join-Path $buildRoot 'toolchain\w64devkit\COPYING.MinGW-w64-runtime.txt') -Destination $binaryOutput -Force
    Copy-Item -LiteralPath (Join-Path $buildRoot 'source\oneVPL-2.11.0\LICENSE') -Destination (Join-Path $binaryOutput 'COPYING.oneVPL.txt') -Force
    Copy-Item -LiteralPath (Join-Path $buildRoot 'source\oneVPL-2.11.0\third-party-programs.txt') -Destination (Join-Path $binaryOutput 'COPYING.oneVPL-third-party-programs.txt') -Force
    Copy-Item -LiteralPath (Join-Path $buildRoot 'source\AMF-1.4.35\LICENSE.txt') -Destination (Join-Path $binaryOutput 'COPYING.AMF.txt') -Force
    Copy-Item -LiteralPath (Join-Path $buildRoot 'source\x264-b35605ace3dd\COPYING') -Destination (Join-Path $binaryOutput 'COPYING.x264.txt') -Force
    $nvidiaHeader = [IO.File]::ReadAllText((Join-Path $buildRoot 'source\nv-codec-headers-n12.2.72.0\include\ffnvcodec\nvEncodeAPI.h'))
    $nvidiaNotice = [regex]::Match($nvidiaHeader, '\A/\*.*?\*/', [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $nvidiaNotice.Success) { throw 'Unable to extract the NVIDIA header notice.' }
    [IO.File]::WriteAllText((Join-Path $binaryOutput 'COPYING.NVIDIA.txt'), $nvidiaNotice.Value + "`n", $utf8NoBom)

    foreach ($binary in @('ffmpeg.exe','ffmpeg-x264.exe')) {
        $path = Join-Path $binaryOutput $binary
        Write-Output ("Built {0}: {1} bytes; SHA-256 {2}" -f $path,(Get-Item -LiteralPath $path).Length,
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
    }
    Write-Output "Retained compiler objects/configuration for review: $WorkDirectory\build"
}
finally {
    $env:PATH = $previousPath
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    $env:TMPDIR = $previousTmpDir
    $env:SOURCE_DATE_EPOCH = $previousEpoch
    Pop-Location
}
