#!/bin/sh
set -eu

BUILD_ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
TOOL_BIN="$BUILD_ROOT/toolchain/w64devkit/bin"
HARDWARE_DEPS="$BUILD_ROOT/deps/hardware"
SOFTWARE_DEPS="$BUILD_ROOT/deps/software"
HARDWARE_BUILD="$BUILD_ROOT/build/hardware"
SOFTWARE_BUILD="$BUILD_ROOT/build/software"
TEMP_ROOT="$BUILD_ROOT/temp"

export PATH="$TOOL_BIN:$PATH"
export TEMP="$TEMP_ROOT"
export TMP="$TEMP_ROOT"
export TMPDIR="$TEMP_ROOT"
export SOURCE_DATE_EPOCH=0
# w64devkit 2.0.0 ships an old busybox awk that mis-evaluates several FFmpeg
# configure expressions. Git for Windows provides the required GNU awk.
export TABLINK_GNU_AWK="${TABLINK_GNU_AWK:-C:/Program Files/Git/usr/bin/awk.exe}"

test -f "$TABLINK_GNU_AWK"
test -f "$HARDWARE_DEPS/lib/libvpl.a"
test -f "$HARDWARE_DEPS/include/AMF/core/Version.h"
mkdir -p "$TEMP_ROOT" "$SOFTWARE_DEPS" "$HARDWARE_BUILD/pkgconfig" "$SOFTWARE_BUILD" "$BUILD_ROOT/bin"

# Build a private static x264 only for the GPL software helper. It is never put
# in the hardware helper's include or pkg-config search path.
cd "$BUILD_ROOT/source/x264-b35605ace3dd"
./configure \
    --prefix="$SOFTWARE_DEPS" \
    --host=x86_64-w64-mingw32 \
    --enable-static \
    --disable-cli \
    --disable-opencl \
    --disable-avs \
    --disable-swscale \
    --disable-lavf \
    --disable-ffms \
    --disable-gpac \
    --disable-lsmash \
    --disable-asm
make -j8
make prefix="$SOFTWARE_DEPS" install-lib-static install-lib-dev
sed -i 's|^prefix=.*|prefix=${pcfiledir}/../../|' "$SOFTWARE_DEPS/lib/pkgconfig/x264.pc"

sed "s|@@PREFIX@@|$BUILD_ROOT/source/nv-codec-headers-n12.2.72.0|" \
    "$BUILD_ROOT/source/nv-codec-headers-n12.2.72.0/ffnvcodec.pc.in" > "$HARDWARE_BUILD/pkgconfig/ffnvcodec.pc"

# LGPL hardware helper: the three GPU APIs only. libx264 is intentionally not
# visible to this configure invocation.
cd "$HARDWARE_BUILD"
export PKG_CONFIG_PATH="$HARDWARE_DEPS/lib/pkgconfig;$HARDWARE_BUILD/pkgconfig"
sh -c 'awk() { "$TABLINK_GNU_AWK" "$@"; }; . "$0" "$@"' "$BUILD_ROOT/source/ffmpeg-7.0.2/configure" \
    --prefix=/ffmpeg-tablink-084-hardware \
    --extra-version=tablink-084-hardware1 \
    --target-os=mingw32 --arch=x86_64 --cc=gcc --cxx=g++ \
    --extra-cflags="-I../../deps/hardware/include" \
    --extra-ldflags="-L../../deps/hardware/lib -Wl,--no-insert-timestamp" \
    --pkg-config-flags="--static --dont-define-prefix" \
    --disable-everything --disable-autodetect --disable-doc --disable-debug \
    --disable-network --disable-x86asm --disable-shared --enable-static --disable-postproc \
    --disable-ffplay --disable-ffprobe --enable-ffmpeg \
    --enable-w32threads --enable-d3d11va \
    --enable-ffnvcodec --enable-nvenc --enable-libvpl --enable-amf \
    --enable-indev=gdigrab,lavfi \
    --enable-filter=ddagrab,testsrc2,format,scale,hwdownload,fps,null,crop \
    --enable-encoder=h264_nvenc,h264_qsv,h264_amf,rawvideo,wrapped_avframe \
    --enable-decoder=rawvideo,bmp,wrapped_avframe \
    --enable-muxer=h264,rawvideo,null \
    --enable-protocol=file,pipe \
    --enable-parser=h264 --enable-bsf=h264_metadata
make -j8
cp ffmpeg.exe "$BUILD_ROOT/bin/ffmpeg.exe"

# GPL software helper: libx264 only. No hardware encoder headers or libraries
# are in its dependency prefix.
cd "$SOFTWARE_BUILD"
export PKG_CONFIG_PATH="$SOFTWARE_DEPS/lib/pkgconfig"
sh -c 'awk() { "$TABLINK_GNU_AWK" "$@"; }; . "$0" "$@"' "$BUILD_ROOT/source/ffmpeg-7.0.2/configure" \
    --prefix=/ffmpeg-tablink-084-software \
    --extra-version=tablink-084-libx264-1 \
    --target-os=mingw32 --arch=x86_64 --cc=gcc --cxx=g++ \
    --extra-cflags="-I../../deps/software/include" \
    --extra-ldflags="-L../../deps/software/lib -Wl,--no-insert-timestamp" \
    --pkg-config-flags="--static --dont-define-prefix" \
    --disable-everything --disable-autodetect --disable-doc --disable-debug \
    --disable-network --disable-x86asm --disable-shared --enable-static --disable-postproc \
    --disable-ffplay --disable-ffprobe --enable-ffmpeg \
    --enable-gpl --enable-libx264 \
    --enable-w32threads --enable-d3d11va \
    --enable-indev=gdigrab,lavfi \
    --enable-filter=ddagrab,testsrc2,format,scale,hwdownload,fps,null,crop \
    --enable-encoder=libx264,rawvideo,wrapped_avframe \
    --enable-decoder=rawvideo,bmp,wrapped_avframe \
    --enable-muxer=h264,rawvideo,null \
    --enable-protocol=file,pipe \
    --enable-parser=h264 --enable-bsf=h264_metadata
make -j8
cp ffmpeg.exe "$BUILD_ROOT/bin/ffmpeg-x264.exe"
