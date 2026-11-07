#!/usr/bin/env bash
# Collects a self-contained UxPlay + GStreamer runtime from MSYS2 UCRT64.
# Usage (UCRT64 shell): stage_engine.sh <dest-dir> [ffmpeg-min-prefix]
#   With ffmpeg-min-prefix, the minimal FFmpeg DLLs (build_ffmpeg_min.sh) are used
#   instead of MSYS2's full FFmpeg, so none of its codec libraries get bundled.
# Layout produced (relocatable GStreamer layout):
#   <dest>/bin/uxplay.exe, gst-inspect-1.0.exe, *.dll
#   <dest>/lib/gstreamer-1.0/libgst*.dll           (only the plugins UxPlay needs)
#   <dest>/libexec/gstreamer-1.0/gst-plugin-scanner.exe
set -euo pipefail
DEST="${1:?destination dir}"
FFMIN="${2:-}"
SRC=/ucrt64
rm -rf "$DEST"
mkdir -p "$DEST/bin" "$DEST/lib/gstreamer-1.0" "$DEST/libexec/gstreamer-1.0"
if [[ -n "$FFMIN" ]]; then
  cp "$FFMIN"/bin/*.dll "$DEST/bin/"
  # make ldd resolve av*/sw* DLLs to the minimal build
  export PATH="$FFMIN/bin:$PATH"
  echo "using minimal FFmpeg from $FFMIN"
fi

# GStreamer plugins used by UxPlay's pipelines (see renderers/*.c) + Windows
# hardware decoders/sinks + audio sinks.
# (vulkan, openh264, jpeg/imagefreeze/pango for cover-art are intentionally left
#  out: D3D12/D3D11/NVDEC + avdec cover every decoder case, and the jpeg
#  pipeline is only built with -ca, which the GUI does not use.)
PLUGINS=(
  coreelements app typefindfunctions playback autodetect
  videoparsersbad audioparsers
  d3d12 d3d11 nvcodec libav
  videoconvertscale videofilter
  audioconvert audioresample volume level
  wasapi wasapi2 directsound
)

EXES=(
  "$SRC/bin/uxplay.exe"
  "$SRC/bin/gst-inspect-1.0.exe"
  "$SRC/bin/gst-launch-1.0.exe"
  "$SRC/libexec/gstreamer-1.0/gst-plugin-scanner.exe"
)

cp "${EXES[0]}" "${EXES[1]}" "${EXES[2]}" "$DEST/bin/"
cp "${EXES[3]}" "$DEST/libexec/gstreamer-1.0/"

FILES=("${EXES[@]}")
for p in "${PLUGINS[@]}"; do
  f="$SRC/lib/gstreamer-1.0/libgst$p.dll"
  if [[ -f "$f" ]]; then
    cp "$f" "$DEST/lib/gstreamer-1.0/"
    FILES+=("$f")
  else
    echo "WARN: plugin $p not found" >&2
  fi
done

# DLL closure: ldd is recursive; keep only DLLs that live in /ucrt64/bin
# (system DLLs such as d3d12.dll / nvcuda.dll come from Windows/the driver).
for f in "${FILES[@]}"; do ldd "$f"; done \
  | awk '{print $3}' | grep -i '^/ucrt64/bin/.*\.dll$' | sort -u \
  | while read -r dll; do cp -n "$dll" "$DEST/bin/"; done

echo "plugins: $(ls "$DEST/lib/gstreamer-1.0" | wc -l)"
echo "dlls:    $(ls "$DEST/bin"/*.dll | wc -l)"
du -sh "$DEST"
