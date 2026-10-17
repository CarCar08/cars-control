<#
  Photographs the installer's wizard in a sandbox (Windows PowerShell 5.1). Nothing is installed for real:
  Setup runs with --sandbox, so files go into a temp folder, no admin prompt, no registry. The wizard opens on
  the TV (CARS_DEV_INSTANCE=1, like the app's test copies) and never takes focus.

    -Setup <old.exe> -NewSetup <new.exe>    two builds: the old one is installed, the new one is photographed
                                            offering the update, then repair, then the uninstall screens
  Output: tools\ui-audit\screens\setup\*.png
#>
param(
    [Parameter(Mandatory)][string]$Setup,
    [string]$NewSetup,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'screens\setup' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class SS {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
"@
[SS]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
New-Item -ItemType Directory -Force $Out | Out-Null
$env:CARS_DEV_INSTANCE = '1'
$env:CARS_DEV_MONITOR = 'tv'
$base = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'cars-setup-visual'))
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
if (-not $base.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $base -Leaf) -ne 'cars-setup-visual') { throw 'Setup sandbox escaped its temporary directory.' }
if (Test-Path $base) { Remove-Item $base -Recurse -Force }
$sandbox = Join-Path $base 'sandbox'
New-Item -ItemType Directory -Force $base | Out-Null

function Shot($proc, $name) {
  $proc.Refresh(); $h = $proc.MainWindowHandle
  $r = New-Object SS+RECT; [SS]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [SS]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
  $dpi = [SS]::GetDpiForWindow($h); if ($dpi -lt 96) { $dpi = 96 }
  $w = [int][Math]::Round($bmp.Width * 96.0 / $dpi); $hh = [int][Math]::Round($bmp.Height * 96.0 / $dpi)
  $small = New-Object System.Drawing.Bitmap $w, $hh
  $g2 = [System.Drawing.Graphics]::FromImage($small); $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic; $g2.DrawImage($bmp, 0, 0, $w, $hh); $g2.Dispose()
  $small.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $small.Dispose(); $bmp.Dispose()
  "saved $name"
}
function Wait-Window($proc) { for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 300; $proc.Refresh(); if ($proc.MainWindowHandle -ne 0) { Start-Sleep -Milliseconds 700; return } } throw 'no window' }
function Click($proc, $text) {
  $root = $AE::FromHandle($proc.MainWindowHandle)
  foreach ($e in $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $text)))) {
    $o = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$o)) { $o.Invoke(); return $true }
  }
  $false
}
$owned = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
function Open($exe, $more = @()) { $p = Start-Process -FilePath $exe -WindowStyle Hidden -ArgumentList (@('--sandbox', $sandbox) + $more) -PassThru; $owned.Add($p); Wait-Window $p; $p }
function Close($p) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }

try {
$a = Join-Path $base 'a.exe'; Copy-Item $Setup $a
# 1. Fresh install: welcome, then run it and photograph the finish.
$env:CARS_SETUP_SLOW = '1'   # inherited by the Setup started below: slow enough to photograph the progress screen
$p = Open $a; Shot $p '01-welcome-install'
if (Click $p 'Install') { Start-Sleep -Milliseconds 2500; Shot $p '01b-working'; Start-Sleep -Seconds 9; Shot $p '02-done-install' }
Remove-Item Env:\CARS_SETUP_SLOW -ErrorAction SilentlyContinue
Close $p

if ($NewSetup) {
    $b = Join-Path $base 'b.exe'; Copy-Item $NewSetup $b
    $p = Open $b; Shot $p '03-welcome-update'
    if (Click $p 'Update') { Start-Sleep -Seconds 4; Shot $p '04-done-update' }
    Close $p
    $p = Open $b; Shot $p '05-welcome-repair'
    if (Click $p ('Uninstall' + [char]0x2026)) { Start-Sleep -Milliseconds 800; Shot $p '06-confirm-uninstall' }
    if (Click $p 'Uninstall') { Start-Sleep -Seconds 3; Shot $p '07-done-uninstall' }
    Close $p
    # An older Setup over a newer install.
    $p = Open $b; if (Click $p 'Install') { Start-Sleep -Seconds 3 }; Close $p
    $p = Open $a; Shot $p '08-welcome-older'; Close $p
}
} finally {
  foreach ($testSetup in $owned) { Close $testSetup }
  Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
