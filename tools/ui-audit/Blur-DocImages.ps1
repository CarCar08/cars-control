<#
.SYNOPSIS
  Pixelates the parts of the documentation screenshots that show the capturing PC's own lists (game names, startup apps,
  right-click entries, running programs, crash times), so docs\images can be public.
.DESCRIPTION
  Regions come from tools\ui-audit\doc-blur-regions.json: { "image.png": [[x, y, width, height], ...] } in the image's pixels
  (Capture-All saves at window size in DIPs, 1320 x 860). Capture-All -Docs runs this after copying the images; run it yourself
  after replacing an image by hand. Pixelation is in blocks of 12 pixels: text can't be read back, the layout stays visible.
#>
param([string]$Images = (Join-Path $PSScriptRoot '..\..\docs\images'), [string]$Map = (Join-Path $PSScriptRoot 'doc-blur-regions.json'), [int]$Block = 12)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$regions = Get-Content -LiteralPath $Map -Raw | ConvertFrom-Json
foreach ($entry in $regions.PSObject.Properties) {
    $file = Join-Path $Images $entry.Name
    if (-not (Test-Path -LiteralPath $file)) { Write-Host "skipped (missing): $($entry.Name)"; continue }
    $bytes = [IO.File]::ReadAllBytes($file)
    $stream = New-Object IO.MemoryStream(, $bytes)
    $source = [Drawing.Bitmap]::FromStream($stream)
    $bitmap = New-Object Drawing.Bitmap($source)
    $source.Dispose(); $stream.Dispose()
    foreach ($r in $entry.Value) {
        $x0 = [int]$r[0]; $y0 = [int]$r[1]; $x1 = [Math]::Min($bitmap.Width, $x0 + [int]$r[2]); $y1 = [Math]::Min($bitmap.Height, $y0 + [int]$r[3])
        for ($by = $y0; $by -lt $y1; $by += $Block) {
            for ($bx = $x0; $bx -lt $x1; $bx += $Block) {
                $w = [Math]::Min($Block, $x1 - $bx); $h = [Math]::Min($Block, $y1 - $by)
                [long]$sr = 0; [long]$sg = 0; [long]$sb = 0; $n = 0
                for ($py = $by; $py -lt $by + $h; $py += 2) { for ($px = $bx; $px -lt $bx + $w; $px += 2) { $c = $bitmap.GetPixel($px, $py); $sr += $c.R; $sg += $c.G; $sb += $c.B; $n++ } }
                $avg = [Drawing.Color]::FromArgb([int]($sr / $n), [int]($sg / $n), [int]($sb / $n))
                for ($py = $by; $py -lt $by + $h; $py++) { for ($px = $bx; $px -lt $bx + $w; $px++) { $bitmap.SetPixel($px, $py, $avg) } }
            }
        }
    }
    $bitmap.Save($file, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "blurred $($entry.Value.Count) region(s): $($entry.Name)"
}
