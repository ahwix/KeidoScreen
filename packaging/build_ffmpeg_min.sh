#!/usr/bin/env bash
# Builds a minimal, LGPL-only FFmpeg whose DLLs are drop-in replacements for the
# MSYS2 ones used by gst-libav (same version => same ABI and DLL names).
# Only what AirPlay needs: AAC (incl. AAC-ELD) + ALAC audio, H.264/H.265 as a
# software fallback when no GPU decoder is available.
# Usage (UCRT64 shell): build_ffmpeg_min.sh <prefix>
set -euo pipefail
PREFIX="${1:?prefix}"
VER="$(pacman -Q mingw-w64-ucrt-x86_64-ffmpeg | awk '{print $2}' | cut -d- -f1)"
WORK=/c/msys64/src/ffmpeg-min
mkdir -p "$WORK"
cd "$WORK"
if [[ ! -d "ffmpeg-$VER" ]]; then
  if curl -fL --retry 3 -o "ffmpeg-$VER.tar.xz" "https://ffmpeg.org/releases/ffmpeg-$VER.tar.xz"; then
    tar xf "ffmpeg-$VER.tar.xz"
  else
    # official FFmpeg mirror on GitHub
    curl -fL --retry 3 -o "ffmpeg-$VER.tar.gz" "https://github.com/FFmpeg/FFmpeg/archive/refs/tags/n$VER.tar.gz"
    tar xf "ffmpeg-$VER.tar.gz"
    mv "FFmpeg-n$VER" "ffmpeg-$VER"
  fi
fi
cd "ffmpeg-$VER"
make distclean >/dev/null 2>&1 || true
./configure --prefix="$PREFIX" \
  --enable-shared --disable-static \
  --disable-programs --disable-doc --disable-debug \
  --disable-autodetect --enable-w32threads \
  --disable-network --disable-avdevice \
  --disable-everything \
  --enable-decoder=aac,aac_latm,alac,h264,hevc \
  --enable-parser=aac,aac_latm,h264,hevc \
  --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb \
  --enable-protocol=file
make -j"$(nproc)"
make install
echo "ffmpeg $VER minimal installed to $PREFIX"
ls -la "$PREFIX/bin"/*.dll
