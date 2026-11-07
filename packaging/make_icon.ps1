# Draws the KeidoScreen icon (monitor + phone) and writes a multi-size .ico (PNG entries).
param([Parameter(Mandatory)] [string]$Out)
Add-Type -AssemblyName System.Drawing
$sizes = 256, 64, 48, 32, 24, 16
$pngs = @()

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0
    # background tile
    $bg = RoundRect (8*$k) (8*$k) (240*$k) (240*$k) (56*$k)
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0,0)), ([System.Drawing.PointF]::new($s,$s)),
        ([System.Drawing.Color]::FromArgb(255, 0x4c, 0x8d, 0xff)), ([System.Drawing.Color]::FromArgb(255, 0x8a, 0x5c, 0xff))
    $g.FillPath($br, $bg)
    $white = [System.Drawing.Color]::White
    # monitor
    $pen = New-Object System.Drawing.Pen $white, ([Math]::Max(1.5, 14*$k))
    $g.DrawPath($pen, (RoundRect (52*$k) (60*$k) (152*$k) (104*$k) (14*$k)))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $white), [float](116*$k), [float](168*$k), [float](24*$k), [float](20*$k))
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), (RoundRect (90*$k) (186*$k) (76*$k) (14*$k) (7*$k)))
    # phone in front (bottom-left), filled white with a blue screen
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), (RoundRect (34*$k) (104*$k) (64*$k) (116*$k) (14*$k)))
    $scr = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x5a, 0x7c, 0xff))
    $g.FillPath($scr, (RoundRect (42*$k) (114*$k) (48*$k) (96*$k) (8*$k)))
    # cast waves from phone to monitor
    if ($s -ge 32) {
        $wp = New-Object System.Drawing.Pen $white, ([Math]::Max(1.5, 10*$k)); $wp.StartCap = 'Round'; $wp.EndCap = 'Round'
        $g.DrawArc($wp, [float](112*$k), [float](92*$k), [float](40*$k), [float](40*$k), 225, 90)
        $g.DrawArc($wp, [float](100*$k), [float](80*$k), [float](64*$k), [float](64*$k), 225, 90)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $pngs += , $ms.ToArray()
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
# also a PNG preview
[System.IO.File]::WriteAllBytes([System.IO.Path]::ChangeExtension($Out, '.png'), $pngs[0])
