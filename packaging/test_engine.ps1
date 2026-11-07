# Verifies a staged engine folder works WITHOUT MSYS2: clean PATH (only the engine
# + Windows), isolated plugin path and a fresh plugin registry.
param([Parameter(Mandatory)] [string]$Engine, [Parameter(Mandatory)] [string]$Media)
$ErrorActionPreference = 'Continue'
$bin = Join-Path $Engine 'bin'
$reg = Join-Path $env:TEMP ("ks-registry-" + [guid]::NewGuid() + ".bin")
$env:PATH = "$bin;C:\Windows\System32;C:\Windows"
$env:GST_PLUGIN_SYSTEM_PATH_1_0 = Join-Path $Engine 'lib\gstreamer-1.0'
$env:GST_PLUGIN_PATH_1_0 = ''
$env:GST_PLUGIN_SCANNER_1_0 = Join-Path $Engine 'libexec\gstreamer-1.0\gst-plugin-scanner.exe'
$env:GST_REGISTRY_1_0 = $reg
Remove-Item Env:GST_DEBUG -ErrorAction SilentlyContinue
$fail = 0

"== elements"
foreach ($e in 'appsrc','queue','h264parse','h265parse','aacparse','d3d12h264dec','d3d12h265dec','d3d11h264dec','nvh264dec','nvh265dec',
               'avdec_h264','avdec_h265','avdec_aac','avdec_alac','videoconvert','videoscale','videoflip','d3d12videosink','d3d11videosink',
               'autovideosink','audioconvert','audioresample','volume','level','wasapisink','autoaudiosink','decodebin') {
    & "$bin\gst-inspect-1.0.exe" --exists $e
    if ($LASTEXITCODE -eq 0) { "  ok   $e" } else { "  MISS $e"; $fail++ }
}
$blacklist = & "$bin\gst-inspect-1.0.exe" -b 2>&1 | Out-String
if ($blacklist -match '\.dll') { "== BLACKLISTED plugins (missing DLLs?)"; $blacklist; $fail++ }

"== decode tests"
$m = $Media -replace '\\', '/'
$tests = @(
  "filesrc location=$m/t.h264 ! h264parse ! d3d12h264dec ! fakesink",
  "filesrc location=$m/t.h264 ! h264parse ! nvh264dec compliance=flexible max-display-delay=0 ! fakesink",
  "filesrc location=$m/t.h264 ! h264parse ! avdec_h264 ! fakesink",
  "filesrc location=$m/t.h265 ! h265parse ! d3d12h265dec ! fakesink",
  "filesrc location=$m/t.h265 ! h265parse ! avdec_h265 ! fakesink",
  "filesrc location=$m/t.aac ! aacparse ! avdec_aac ! audioconvert ! audioresample ! volume ! level ! fakesink"
)
foreach ($t in $tests) {
    $out = & "$bin\gst-launch-1.0.exe" -q ($t -split ' ') 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) { "  ok   $t" } else { "  FAIL $t`n$out"; $fail++ }
}

"== uxplay start"
$log = Join-Path $env:TEMP ("ks-uxplay-" + [guid]::NewGuid() + ".log")
$p = Start-Process "$bin\uxplay.exe" -ArgumentList '-n','KS-selftest','-nh','-h265','-vd','d3d12h264dec','-vs','d3d12videosink','-as','wasapisink','-vsync','no','-d' `
     -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
Start-Sleep 5
$alive = -not $p.HasExited
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
$txt = (Get-Content $log, "$log.err" -ErrorAction SilentlyContinue) -join "`n"
$inits = ([regex]::Matches($txt, 'Initialized GStreamer video renderer')).Count
"  alive=$alive renderers=$inits dnssd=" + ($txt -match 'advertised AirPlay service')
if (-not $alive -or $inits -lt 2) { $txt; $fail++ }
Remove-Item $reg, $log, "$log.err" -ErrorAction SilentlyContinue
"== RESULT: " + $(if ($fail) { "$fail problem(s)" } else { 'ALL OK' })
exit $fail
