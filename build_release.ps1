# Builds a KeidoScreen release: installer + portable zip.
# Needs: MSYS2 with UxPlay built (engine\build_engine.ps1), NSIS (pacman), .NET Framework 4 (Windows).
#   powershell -ExecutionPolicy Bypass -File build_release.ps1 [-Version 1.0.0] [-Repo owner/KeidoScreen] [-SkipEngine]
#   -Repo: GitHub repository used by the auto-updater (empty = updates disabled)
param([string]$Version = '1.0.0', [string]$Repo = '', [switch]$SkipEngine)
if (-not $Repo -and (Test-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'repo.txt'))) {
    $Repo = (Get-Content (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'repo.txt') -Raw).Trim()
}
$ErrorActionPreference = 'Stop'
$here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$stage = 'C:\msys64\src\pkg\KeidoScreen'   # short path: deep paths break MSYS2 tools (MAX_PATH)
$out   = Join-Path $here 'release'
$bash  = 'C:\msys64\usr\bin\bash.exe'
$csc   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

function Sh([string]$cmd) {
    $env:MSYSTEM = 'UCRT64'; $env:CHERE_INVOKING = '1'
    & $bash -lc $cmd
    if ($LASTEXITCODE -ne 0) { throw "MSYS2 command failed: $cmd" }
}
function U([string]$p) { (& $bash -lc "cygpath -u '$p'") | Select-Object -Last 1 }
function Step([string]$t) { Write-Host "`n== $t" -ForegroundColor Cyan }

New-Item -ItemType Directory -Force $stage, $out | Out-Null

if (-not $SkipEngine) {
    # minimal LGPL FFmpeg (packaging/build_ffmpeg_min.sh) is used when it has been built
    $ffmin = ''
    if (Test-Path 'C:\msys64\src\pkg\ffmpeg-min\bin\avcodec-*.dll') { $ffmin = '/c/msys64/src/pkg/ffmpeg-min' }
    Step 'Engine (UxPlay + minimal GStreamer runtime)'
    Sh ("bash '" + (U "$here\packaging\stage_engine.sh") + "' '" + (U "$stage\engine") + "' '$ffmin'")
    Step 'Licenses'
    Sh ("bash '" + (U "$here\packaging\collect_licenses.sh") + "' '" + (U "$stage\engine") + "' '" + (U "$stage\licenses") + "' /c/msys64/src/UxPlay '$ffmin'")
}

Step 'Probe clips for hardware auto-tuning'
New-Item -ItemType Directory -Force "$stage\engine\share\keidoscreen" | Out-Null
Copy-Item "$here\packaging\probe\probe.h264", "$here\packaging\probe\probe.h265" "$stage\engine\share\keidoscreen" -Force

Step "App (C# / WPF)  version $Version  update repo: $(if ($Repo) { $Repo } else { 'none' })"
& "$here\packaging\make_icon.ps1" -Out "$here\src\KeidoScreen.ico"
Copy-Item "$here\src\KeidoScreen.ico" $stage -Force
$tmpSrc = Join-Path $env:TEMP 'keido-src'
New-Item -ItemType Directory -Force $tmpSrc | Out-Null
foreach ($f in Get-ChildItem "$here\src\*.cs") {
    $c = Get-Content $f.FullName -Raw -Encoding UTF8
    $c = $c -replace 'Assembly(File)?Version\("[\d.]+"\)', ('Assembly$1Version("' + $Version + '.0")')
    $c = $c.Replace('"__REPO__"', '"' + $Repo + '"')
    [IO.File]::WriteAllText((Join-Path $tmpSrc $f.Name), $c, (New-Object Text.UTF8Encoding $true))
}
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
& $csc /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 /nowarn:649 "/out:$stage\KeidoScreen.exe" "/win32icon:$here\src\KeidoScreen.ico" `
    "/win32manifest:$here\src\app.manifest" "/lib:$fw\WPF" `
    /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll `
    "/resource:$here\src\MainWindow.xaml,KeidoScreen.MainWindow.xaml" (Get-ChildItem "$tmpSrc\*.cs" | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw 'C# build failed' }
Remove-Item $tmpSrc -Recurse -Force
# license shown by the installer (UTF-8 with BOM so NSIS reads Cyrillic correctly)
$lic = Get-Content "$here\packaging\LICENSE.txt" -Raw -Encoding UTF8
[IO.File]::WriteAllText("$stage\licenses\LICENSE.txt", $lic, (New-Object Text.UTF8Encoding $true))

Step 'Self-test of the bundled engine (no MSYS2 on PATH)'
$media = Join-Path $env:TEMP 'keido-media'
New-Item -ItemType Directory -Force $media | Out-Null
$m = $media -replace '\\', '/'
$env:PATH = "C:\msys64\ucrt64\bin;$env:PATH"
gst-launch-1.0.exe -q videotestsrc num-buffers=60 ! 'video/x-raw,width=1280,height=720,framerate=60/1' ! nvh264enc bframes=0 ! h264parse ! 'video/x-h264,stream-format=byte-stream' ! filesink location="$m/t.h264" 2>$null
if ($LASTEXITCODE -ne 0) { gst-launch-1.0.exe -q videotestsrc num-buffers=60 ! 'video/x-raw,width=1280,height=720' ! openh264enc ! h264parse ! filesink location="$m/t.h264" }
gst-launch-1.0.exe -q videotestsrc num-buffers=60 ! 'video/x-raw,width=1280,height=720,framerate=60/1' ! nvh265enc bframes=0 ! h265parse ! 'video/x-h265,stream-format=byte-stream' ! filesink location="$m/t.h265" 2>$null
gst-launch-1.0.exe -q audiotestsrc num-buffers=100 ! avenc_aac ! aacparse ! 'audio/mpeg,stream-format=adts' ! filesink location="$m/t.aac"
powershell -NoProfile -ExecutionPolicy Bypass -File "$here\packaging\test_engine.ps1" -Engine "$stage\engine" -Media $media
if ($LASTEXITCODE -ne 0) { throw 'Engine self-test failed' }

Step 'Installer (NSIS)'
$makensis = 'C:\msys64\ucrt64\bin\makensis.exe'
& $makensis /V2 /INPUTCHARSET UTF8 "/DVERSION=$Version" "/DDIST=$stage" "/DOUTDIR=$out" "$here\packaging\installer.nsi"
if ($LASTEXITCODE -ne 0) { throw 'NSIS failed' }

Step 'Portable zip'
$zip = Join-Path $out "KeidoScreen-$Version-portable.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage\KeidoScreen.exe", "$stage\KeidoScreen.ico", "$stage\engine", "$stage\licenses" -DestinationPath $zip -CompressionLevel Optimal

Step 'Checksums (used by the auto-updater)'
$files = @("KeidoScreen-Setup-$Version.exe", "KeidoScreen-$Version-portable.zip")
($files | ForEach-Object { (Get-FileHash (Join-Path $out $_) -Algorithm SHA256).Hash.ToLower() + "  " + $_ }) -join "`n" |
    Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii -NoNewline

Step 'Done'
Get-ChildItem $out | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
