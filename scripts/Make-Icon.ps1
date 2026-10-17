# Generates src\CarsControl.App\Assets\cars.ico (16/24/32/48/64/256 px, PNG-compressed entries) and cars.png.
# Run with Windows PowerShell 5.1 (System.Drawing):  powershell -File scripts\Make-Icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\CarsControl.App\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

function New-IconBitmap([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.Clear([System.Drawing.Color]::Transparent)
  $r = [float]($size * 0.22); $s = [float]($size - 1)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($s - $r, 0, $r, $r, 270, 90)
  $path.AddArc($s - $r, $s - $r, $r, $r, 0, 90); $path.AddArc(0, $s - $r, $r, $r, 90, 90); $path.CloseFigure()
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $size, $size), ([System.Drawing.Color]::FromArgb(255, 38, 115, 168)), ([System.Drawing.Color]::FromArgb(255, 24, 160, 140))
  $g.FillPath($brush, $path)
  # Gauge arc (the "control" dial) + needle
  $penW = [float][Math]::Max(1.5, $size * 0.075)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 255, 255, 255)), $penW
  $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
  $m = [float]($size * 0.2); $d = [float]($size - 2 * $m)
  $g.DrawArc($pen, $m, $m + $size * 0.06, $d, $d, 150, 240)
  $cx = [float]($size / 2); $cy = [float]($size / 2 + $size * 0.06)
  $needle = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float][Math]::Max(1.5, $size * 0.07))
  $needle.StartCap = 'Round'; $needle.EndCap = 'Round'
  $g.DrawLine($needle, $cx, $cy, [float]($cx + $d * 0.30), [float]($cy - $d * 0.24))
  $dot = [float]($size * 0.11)
  $g.FillEllipse([System.Drawing.Brushes]::White, $cx - $dot / 2, $cy - $dot / 2, $dot, $dot)
  $g.Dispose()
  return $bmp
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($sz in $sizes) {
  $b = New-IconBitmap $sz
  $ms = New-Object System.IO.MemoryStream
  $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  if ($sz -eq 256) { $b.Save((Join-Path $out 'cars.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
  $b.Dispose()
  , $ms.ToArray()
}

# ICO container: ICONDIR + ICONDIRENTRY[] + PNG payloads
$fs = [System.IO.File]::Create((Join-Path $out 'cars.ico'))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $sz = $sizes[$i]; $len = $pngs[$i].Length
  $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$len); $w.Write([uint32]$offset); $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"wrote $(Join-Path $out 'cars.ico')"
