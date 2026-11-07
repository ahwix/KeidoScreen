#!/usr/bin/env bash
# Writes license texts + a third-party notice (package, version, licence, source)
# for every MSYS2 package that a file in the staged engine came from.
# Usage (UCRT64 shell): collect_licenses.sh <engine-dir> <out-licenses-dir> <uxplay-src-dir> [ffmpeg-min-prefix]
set -euo pipefail
ENGINE="${1:?}"; OUT="${2:?}"; UXSRC="${3:?}"; FFMIN="${4:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"
rm -rf "$OUT"; mkdir -p "$OUT/packages"

# FFmpeg DLLs of the minimal build are documented separately (not the MSYS2 package)
FFSKIP='^$'
[[ -n "$FFMIN" ]] && FFSKIP='^(bin/)?(avcodec|avformat|avfilter|avutil|swresample|swscale|avdevice)-[0-9]+\.dll$'

# map staged files back to their owning pacman packages
mapfile -t files < <(find "$ENGINE" -type f \( -name '*.dll' -o -name '*.exe' \) ! -name 'uxplay.exe' -printf '%P\n' \
  | grep -Ev "$FFSKIP" \
  | while read -r rel; do
      f="/ucrt64/$rel"; [[ -f "$f" ]] || f="/ucrt64/bin/$(basename "$rel")"
      [[ -f "$f" ]] && echo "$f"
    done)
# one pacman call for all files (per-file calls take minutes)
pkgs=$(pacman -Qqo "${files[@]}" 2>/dev/null | sort -u)

UX_COMMIT=$(git -C "$UXSRC" rev-parse HEAD 2>/dev/null || echo unknown)
cp "$UXSRC/LICENSE" "$OUT/UxPlay-LICENSE.txt"

N="$OUT/THIRD_PARTY_NOTICES.txt"
{
  echo "KeidoScreen — third-party components"
  echo "===================================="
  echo
  echo "KeidoScreen bundles the following software. Each component is distributed"
  echo "under its own license; full texts are in the 'packages' folder."
  echo
  echo "UxPlay (AirPlay mirroring engine) — GPL-3.0"
  echo "  source: https://github.com/FDH2/UxPlay  commit $UX_COMMIT"
  echo
  if [[ -n "$FFMIN" ]]; then
    FFVER=$(pacman -Q mingw-w64-ucrt-x86_64-ffmpeg | awk '{print $2}' | cut -d- -f1)
    echo "FFmpeg $FFVER (avcodec, avformat, avfilter, avutil, swresample, swscale) — LGPL-2.1-or-later"
    echo "  minimal build: decoders aac, aac_latm, alac, h264, hevc only; no GPL parts"
    echo "  source: https://ffmpeg.org/releases/ffmpeg-$FFVER.tar.xz"
    echo "  build script with the exact configure options: ffmpeg/build_ffmpeg_min.sh"
    echo
    mkdir -p "$OUT/ffmpeg"
    cp "$HERE/build_ffmpeg_min.sh" "$OUT/ffmpeg/"
    src=$(ls -d /c/msys64/src/ffmpeg-min/ffmpeg-"$FFVER" 2>/dev/null | head -1)
    [[ -n "$src" ]] && cp "$src/COPYING.LGPLv2.1" "$src/LICENSE.md" "$OUT/ffmpeg/" 2>/dev/null || true
  fi
  echo "Libraries (built by the MSYS2 project, UCRT64 environment):"
  echo "  build recipes: https://github.com/msys2/MINGW-packages"
  echo "  exact source archives: https://repo.msys2.org/mingw/sources/"
  echo
  for p in $pkgs; do
    ver=$(pacman -Q "$p" | awk '{print $2}')
    lic=$(pacman -Qi "$p" | sed -n 's/^\(Licenses\|Лицензии\) *: //p' | head -1)
    url=$(pacman -Qi "$p" | sed -n 's/^\(URL\) *: //p' | head -1)
    short=${p#mingw-w64-ucrt-x86_64-}
    printf '%-28s %-14s %-40s %s\n' "$short" "$ver" "${lic:-see packages/$short}" "$url"
    if [[ -d "/ucrt64/share/licenses/$short" ]]; then
      cp -r "/ucrt64/share/licenses/$short" "$OUT/packages/$short"
    fi
  done
  echo
  echo "On request we provide the complete corresponding source code of the GPL/LGPL"
  echo "components above for at least three years from the date of distribution."
  echo
  echo "iPhone, iPad and AirPlay are trademarks of Apple Inc., registered in the U.S."
  echo "and other countries. KeidoScreen is not affiliated with or endorsed by Apple."
} > "$N"
echo "packages: $(echo "$pkgs" | wc -w), license dirs: $(ls "$OUT/packages" | wc -l)"
