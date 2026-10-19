#!/usr/bin/env bash
set -euo pipefail

# Run on Linux/WSL with make and gcc-mingw-w64-x86-64 installed.
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
archive="$project_root/vendor/source/ffmpeg-7.1.3.tar.xz"
expected_sha256=f0bf043299db9e3caacb435a712fc541fbb07df613c4b893e8b77e67baf3adbe
printf '%s  %s\n' "$expected_sha256" "$archive" | sha256sum --check --status
build_root="$project_root/build/ffprobe-source"
if [[ -e "$build_root" ]]; then
    echo 'build/ffprobe-source already exists; move the previous build aside first.' >&2
    exit 1
fi
mkdir -p "$build_root"
tar -xJf "$archive" -C "$build_root" --strip-components=1
cd "$build_root"
./configure \
    --target-os=mingw32 --arch=x86_64 --cross-prefix=x86_64-w64-mingw32- \
    --prefix=/ffprobe --disable-autodetect --disable-network --disable-debug \
    --disable-doc --disable-x86asm --disable-gpl --disable-nonfree \
    --disable-ffmpeg --disable-ffplay --disable-avdevice --disable-avfilter \
    --disable-postproc --disable-encoders --disable-muxers --disable-hwaccels \
    --disable-devices --disable-protocols --enable-protocol=file \
    --disable-shared --enable-static --extra-ldflags=-static
make -j"${JOBS:-4}" ffprobe.exe
x86_64-w64-mingw32-strip ffprobe.exe
cp ffprobe.exe "$project_root/vendor/ffprobe.exe"
echo 'Built vendor/ffprobe.exe from the bundled, unmodified FFmpeg source.'
