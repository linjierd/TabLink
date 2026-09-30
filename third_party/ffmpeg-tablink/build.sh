#!/bin/sh
set -eu
BUILD_ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
export PATH="$BUILD_ROOT/toolchain/w64devkit/bin:$PATH"
export PKG_CONFIG_PATH="$BUILD_ROOT/build/pkgconfig"
# busybox awk bundled by w64devkit 2.0.0 mis-evaluates configure's ternary
# expressions. Use the existing Git-for-Windows GNU awk only in configure.
export TABLINK_GNU_AWK="${TABLINK_GNU_AWK:-C:/Program Files/Git/usr/bin/awk.exe}"
test -f "$TABLINK_GNU_AWK"
mkdir -p "$BUILD_ROOT/build/pkgconfig"
sed "s|@@PREFIX@@|$BUILD_ROOT/source/nv-codec-headers-n12.2.72.0|" \
    "$BUILD_ROOT/source/nv-codec-headers-n12.2.72.0/ffnvcodec.pc.in" > "$BUILD_ROOT/build/pkgconfig/ffnvcodec.pc"
cd "$BUILD_ROOT/build"
sh -c 'awk() { "$TABLINK_GNU_AWK" "$@"; }; . "$0" "$@"' "$BUILD_ROOT/source/ffmpeg-7.0.2/configure" \
    --prefix="$BUILD_ROOT/install" \
    --extra-version=tablink-hires1 \
    --target-os=mingw32 --arch=x86_64 --cc=gcc --cxx=g++ \
    --pkg-config-flags=--dont-define-prefix \
    --disable-everything --disable-autodetect --disable-doc --disable-debug \
    --disable-network --disable-x86asm --disable-shared --enable-static \
    --disable-ffplay --disable-ffprobe --enable-ffmpeg \
    --enable-w32threads --enable-d3d11va --enable-ffnvcodec --enable-nvenc \
    --enable-indev=gdigrab,lavfi \
    --enable-filter=ddagrab,testsrc2,format,scale,hwdownload,fps,null,crop \
    --enable-encoder=h264_nvenc,rawvideo,wrapped_avframe \
    --enable-decoder=rawvideo,bmp,wrapped_avframe \
    --enable-muxer=h264,rawvideo,null \
    --enable-protocol=file,pipe \
    --enable-parser=h264 --enable-bsf=h264_metadata
make -j8
mkdir -p "$BUILD_ROOT/bin"
cp ffmpeg.exe "$BUILD_ROOT/bin/ffmpeg.exe"
