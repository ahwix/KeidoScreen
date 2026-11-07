# Builds the UxPlay AirPlay engine for KeidoScreen (run once per build machine).
# 1) installs MSYS2 into C:\msys64 (if missing)
# 2) installs compiler + GStreamer (with D3D11/D3D12/NVDEC decoders) via pacman
# 3) clones UxPlay master (built-in mDNS, no Bonjour needed) and builds it
# Result: C:\msys64\ucrt64\bin\uxplay.exe

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$msys = 'C:\msys64'
$bash = "$msys\usr\bin\bash.exe"
# short path on purpose: deep paths exceed Windows MAX_PATH during the build
$src  = "$msys\src\UxPlay"

function Sh([string]$cmd) {
    $env:MSYSTEM = 'UCRT64'
    $env:CHERE_INVOKING = '1'
    & $bash -lc $cmd
    if ($LASTEXITCODE -ne 0) { throw "MSYS2 command failed ($LASTEXITCODE): $cmd" }
}

if (-not (Test-Path $bash)) {
    Write-Host '== Installing MSYS2 ==' -ForegroundColor Cyan
    $sfx = Join-Path $env:TEMP 'msys2-base-x86_64-latest.sfx.exe'
    Invoke-WebRequest 'https://github.com/msys2/msys2-installer/releases/download/nightly-x86_64/msys2-base-x86_64-latest.sfx.exe' -OutFile $sfx
    if ((Get-AuthenticodeSignature $sfx).Status -ne 'Valid') { throw 'MSYS2 archive signature is not valid' }
    & $sfx -y -oC:\ | Out-Null
    Sh 'true'   # first-run initialisation (keyring etc.)
}

Write-Host '== Updating MSYS2 ==' -ForegroundColor Cyan
Sh 'pacman -Syuu --noconfirm'
Sh 'pacman -Syuu --noconfirm'

Write-Host '== Installing build tools and GStreamer ==' -ForegroundColor Cyan
$p = 'mingw-w64-ucrt-x86_64'
Sh ("pacman -S --needed --noconfirm git " +
    "$p-cmake $p-ninja $p-gcc $p-pkgconf $p-openssl $p-libplist " +
    "$p-gstreamer $p-gst-plugins-base $p-gst-plugins-good $p-gst-plugins-bad $p-gst-libav " +
    "$p-nsis $p-github-cli")

Write-Host '== Fetching UxPlay ==' -ForegroundColor Cyan
New-Item -ItemType Directory -Force (Split-Path $src) | Out-Null
$srcU = Sh "cygpath -u '$src'" | Select-Object -Last 1
if (Test-Path "$src\.git") { Sh "cd '$srcU' && git pull --ff-only" }
else { Sh "git clone --depth 1 https://github.com/FDH2/UxPlay '$srcU'" }

Write-Host '== Building UxPlay ==' -ForegroundColor Cyan
# NO_MARCH_NATIVE: generic x86-64 code, otherwise the binary only runs on CPUs like the build machine's
Sh ("cd '$srcU' && mkdir -p build && cd build && " +
    "cmake -G Ninja -DCMAKE_BUILD_TYPE=Release -DNO_MARCH_NATIVE=ON .. && ninja && cmake --install . --prefix /ucrt64")

Write-Host '== Available H.264/H.265 decoders ==' -ForegroundColor Cyan
Sh "gst-inspect-1.0 | grep -Ei 'h26[45]dec' || true"

Write-Host "`nDone: $msys\ucrt64\bin\uxplay.exe" -ForegroundColor Green
