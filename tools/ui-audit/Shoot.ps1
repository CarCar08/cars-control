<#
  Screenshot helper for the dev instance (Windows PowerShell 5.1):
    -Page <name>       navigate first (via `CarsControl.exe --page`, handled by the running instance)
    -Select <name>     select the list row with this accessible name (rows are named after the app/file)
    -Toggle <name>     toggle a switch/checkbox by name
    -Tab <name>        select a tab
    -Invoke <name>     click a button by name (deny-listed destructive names are refused)
    -ScrollPercent <n> scroll the page to n% (0-100) before the shot
  Output: PNG via PrintWindow (works while the window is covered).
#>
param([string]$Out, [string]$Page, [string]$Select, [string[]]$Toggle = @(), [string]$Tab, [string[]]$Invoke = @(), [int]$WaitMs = 1200, [double]$ScrollPercent = -1,
      [string]$Exe = (Get-ChildItem "$PSScriptRoot\..\..\src\CarsControl.App\bin\Debug" -Recurse -Filter CarsControl.exe | Sort-Object LastWriteTime | Select-Object -Last 1).FullName)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WS {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
"@
[WS]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null   # per-monitor v2: real pixel sizes on every monitor

# Windows scales the 4K TV to 250%: the window is really 2.5x bigger than the DIP size the app lays out in.
# Shots are saved at DIP size (same as on a 100% monitor) unless CARS_SHOT_FULL=1 asks for every pixel.
function Save-Shot($bmp, $file, $hwnd) {
  $dpi = [WS]::GetDpiForWindow($hwnd); if ($dpi -lt 96) { $dpi = 96 }
  if ($dpi -gt 100 -and $env:CARS_SHOT_FULL -ne '1') {
    $w = [int][Math]::Round($bmp.Width * 96.0 / $dpi); $h = [int][Math]::Round($bmp.Height * 96.0 / $dpi)
    $small = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($small)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($bmp, 0, 0, $w, $h); $g.Dispose()
    $small.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $small.Dispose()
  } else { $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png) }
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$deny = 'Free up|Find leftovers|Update everything|Update selected|Update now|Uninstall|Clean selected|Quarantine|Recycle|Restore|Empty|Test admin|Measure admin|Exit|Start with Windows'
$env:CARS_DEV_INSTANCE = '1'; $env:CARS_HELPER_UNELEVATED = '1'
function Main { $p = Get-Process CarsControl -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if (-not $p) { Start-Process -FilePath $Exe -WindowStyle Hidden | Out-Null; Start-Sleep -Seconds 6; $p = Get-Process CarsControl | Where-Object { $_.Path -eq $Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
  $p }
function Find($root, $name) { $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
function Pat($el, $p) { $o = $null; if ($el.TryGetCurrentPattern($p, [ref]$o)) { $o } else { $null } }

$proc = Main
if ($Page) { Start-Process -FilePath $Exe -WindowStyle Hidden -ArgumentList '--page', $Page | Out-Null; Start-Sleep -Milliseconds 1500 }
$root = $AE::FromHandle($proc.MainWindowHandle)
if ($Tab) { foreach ($el in (Find $root $Tab)) { if ($s = Pat $el ([System.Windows.Automation.SelectionItemPattern]::Pattern)) { $s.Select(); break } } ; Start-Sleep -Milliseconds 600 }
foreach ($t in $Toggle) { foreach ($el in (Find $root $t)) { if ($tp = Pat $el ([System.Windows.Automation.TogglePattern]::Pattern)) { $tp.Toggle(); break } } }
if ($Select) { foreach ($el in (Find $root $Select)) { if ($s = Pat $el ([System.Windows.Automation.SelectionItemPattern]::Pattern)) { $s.Select(); break } } }
foreach ($i in $Invoke) {
  if ($i -match $deny) { "refused: $i"; continue }
  foreach ($el in (Find $root $i)) { if ($ip = Pat $el ([System.Windows.Automation.InvokePattern]::Pattern)) { $ip.Invoke(); break } }
}
Start-Sleep -Milliseconds $WaitMs
# -ScrollPercent 0..100: scroll the page's main scroll viewer (the largest scrollable element) first.
if ($ScrollPercent -ge 0) {
  $scrollers = $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $sp = Pat $_ ([System.Windows.Automation.ScrollPattern]::Pattern); $sp -and $sp.Current.VerticallyScrollable }
  $big = $scrollers | Sort-Object { $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
  if ($big) { (Pat $big ([System.Windows.Automation.ScrollPattern]::Pattern)).SetScrollPercent(-1, $ScrollPercent); Start-Sleep -Milliseconds 500 }
}
$h = $proc.MainWindowHandle
$r = New-Object WS+RECT; [WS]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [WS]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
Save-Shot $bmp $Out $h; $bmp.Dispose()
"saved $Out"
