<#
  Photographs every screen and state of a C.A.R.S. test copy, in several appearances and window sizes,
  and writes a contact sheet:  tools\ui-audit\screens\<timestamp>\index.html
  (Windows PowerShell 5.1)

  The test copy opens on the secondary monitor and never takes focus (DevWindowPlacement), so this can
  run while you use the PC. It only navigates, selects, filters, expands and opens dialogs it then
  cancels: nothing is installed, uninstalled, cleaned or recycled. settings.json is backed up first and
  restored at the end (appearance passes change it).

    -Quick      only the default appearance at the default size
    -Docs       like -Quick, then copy the screens the documentation uses into docs\images (review them before publishing:
                they show this PC's real apps, drives and folder names)
#>
param([switch]$Quick, [switch]$Docs, [switch]$ReleaseCheck,
      [ValidateSet('default','minimum','wide','system')][string]$StartPass = 'default')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class CW {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
"@
# Window bounds in real screen pixels whatever this process's DPI awareness is (GetWindowRect is virtualized for a DPI-unaware
# host, which cropped every screenshot on the 250% TV once). Its own class, so a reused PowerShell host never has to redefine one.
if (-not ('CarsShotBounds' -as [type])) {
  Add-Type @"
using System; using System.Runtime.InteropServices;
public static class CarsShotBounds {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out R r, int size);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  /// Per-monitor DPI for this thread. The process-wide switch fails when the PowerShell host's manifest already set one, and
  /// then window sizes are scaled: on the 250% TV a 960-wide window became 2400 and every size pass came out full-screen.
  public static void PerMonitor() { SetThreadDpiAwarenessContext(new IntPtr(-4)); }
  public static int[] Of(IntPtr h) {
    R r;
    if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(R))) != 0) GetWindowRect(h, out r); // 9 = DWMWA_EXTENDED_FRAME_BOUNDS
    return new[] { r.L, r.T, r.Rt, r.B };
  }
}
"@
}
[CarsShotBounds]::PerMonitor()

Add-Type -AssemblyName System.Windows.Forms
[CW]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null   # per-monitor v2: real pixel sizes on every monitor
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

$root = Resolve-Path "$PSScriptRoot\..\.."
. (Join-Path $PSScriptRoot 'Initialize-DevData.ps1')
$devData = Initialize-DevData $root -Demo:$Docs   # documentation screenshots use the generic demo app list and PC name
$exe = (Get-ChildItem "$root\src\CarsControl.App\bin\Debug" -Recurse -Filter CarsControl.exe | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
$settingsFile = Join-Path $devData 'settings.json'
$stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
$outRoot = Join-Path $PSScriptRoot "screens\$stamp"
New-Item -ItemType Directory -Force $outRoot | Out-Null
$env:CARS_DEV_INSTANCE = '1'; $env:CARS_HELPER_UNELEVATED = '1'; $env:CARS_DEV_MONITOR = 'tv'
if ($Docs) { $env:CARS_DOC_SHOT = '1' }   # no "TEST COPY" in the window title of the documentation screenshots
$env:CARS_DEV_FAKE_APPROVALS = '1'   # Home's "admin items waiting" card, with sample items that are never saved
$shots = New-Object System.Collections.Generic.List[object]
$log = New-Object System.Collections.Generic.List[string]

# ---------- helpers ----------
function Pat($el, $p) {
  try { $o = $null; if ($el -and $el.TryGetCurrentPattern($p, [ref]$o)) { $o } else { $null } }
  catch { if ($_.Exception.GetBaseException() -isnot [System.Windows.Automation.ElementNotAvailableException]) { throw } }
}
function Main { Get-Process CarsControl -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
# The real main window, remembered at start: while a dialog is open Windows may report the dialog as "main".
$script:mainHandle = [IntPtr]::Zero
function MainEl { if ($script:mainHandle -ne [IntPtr]::Zero) { $AE::FromHandle($script:mainHandle) } }
function FindAll($el, $name) {
  for ($attempt=0; $attempt -lt 3; $attempt++) {
    try { return $el.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
    catch {
      if ($_.Exception.GetBaseException() -isnot [System.Windows.Automation.ElementNotAvailableException] -or $attempt -eq 2) { throw }
      Start-Sleep -Milliseconds 200; $el = MainEl
    }
  }
}
function FindFirst($el, [scriptblock]$where) {
  for ($attempt=0; $attempt -lt 3; $attempt++) {
    try {
      foreach ($e in $el.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        try { if (& $where $e) { return $e } }
        catch { if ($_.Exception.GetBaseException() -isnot [System.Windows.Automation.ElementNotAvailableException]) { throw } }
      }
      return $null
    } catch {
      if ($_.Exception.GetBaseException() -isnot [System.Windows.Automation.ElementNotAvailableException] -or $attempt -eq 2) { throw }
      Start-Sleep -Milliseconds 200; $el = MainEl
    }
  }
}

function Stop-Dev { Get-Process CarsControl -ErrorAction SilentlyContinue | Where-Object Path -eq $exe | Stop-Process -Force; Start-Sleep -Milliseconds 800 }

$backup = Join-Path $outRoot 'settings-before.json'
function Start-Dev($theme, $backdrop) {
  Stop-Dev
  $script:theme = $theme; $script:backdrop = $backdrop
  if (-not (Test-Path $settingsFile)) {
    # First run of a settings.json build: let the app create it (it migrates cars.db), then back it up.
    Start-Process -FilePath $exe -WindowStyle Hidden | Out-Null
    for ($i = 0; $i -lt 60 -and -not (Test-Path $settingsFile); $i++) { Start-Sleep -Milliseconds 500 }
    Stop-Dev
  }
  if ((Test-Path $settingsFile) -and -not (Test-Path $backup)) { Copy-Item $settingsFile $backup -Force }
  if (Test-Path $settingsFile) {
    $s = Get-Content $settingsFile -Raw | ConvertFrom-Json
    $s.Appearance.Theme = $theme; $s.Appearance.Backdrop = $backdrop
    $s | ConvertTo-Json -Depth 10 | Set-Content $settingsFile -Encoding UTF8
  }
  Start-Process -FilePath $exe -WindowStyle Hidden | Out-Null
  for ($i = 0; $i -lt 60 -and -not (Main); $i++) { Start-Sleep -Milliseconds 500 }
  $script:mainHandle = (Main).MainWindowHandle
  Start-Sleep -Seconds 6   # inventory cache, first checks
}

function Go($page) { Start-Process -FilePath $exe -WindowStyle Hidden -ArgumentList '--page', $page | Out-Null; Start-Sleep -Milliseconds 1500 }
function Tab($prefix) { $t = FindFirst (MainEl) { param($e) $e.Current.ControlType -eq $CT::TabItem -and $e.Current.Name.StartsWith($prefix) }; if ($s = Pat $t ([System.Windows.Automation.SelectionItemPattern]::Pattern)) { $s.Select(); Start-Sleep -Milliseconds 700 } else { $log.Add("no tab $prefix") } }
function Invoke1($name, $win = (MainEl)) { foreach ($e in (FindAll $win $name)) { if ($ip = Pat $e ([System.Windows.Automation.InvokePattern]::Pattern)) { $ip.Invoke(); Start-Sleep -Milliseconds 700; return $true } }; $log.Add("no button $name"); $false }
function Toggle1($name) { foreach ($e in (FindAll (MainEl) $name)) { if ($tp = Pat $e ([System.Windows.Automation.TogglePattern]::Pattern)) { $tp.Toggle(); Start-Sleep -Milliseconds 700; return } }; $log.Add("no toggle $name") }
function SetText($name, $text) { foreach ($e in (FindAll (MainEl) $name)) { if ($vp = Pat $e ([System.Windows.Automation.ValuePattern]::Pattern)) { $vp.SetValue($text); Start-Sleep -Milliseconds 900; return } }; $log.Add("no text box $name") }
# Rows on a monitor that's switched off all report "offscreen", so the first realized row is the fallback.
function SelectFirstRow { $r = FindFirst (MainEl) { param($e) $e.Current.ControlType -eq $CT::DataItem -and $e.Current.ClassName -eq 'DataGridRow' -and -not $e.Current.IsOffscreen }; if (-not $r) { $r = FindFirst (MainEl) { param($e) $e.Current.ControlType -eq $CT::DataItem -and $e.Current.ClassName -eq 'DataGridRow' } }; if ($s = Pat $r ([System.Windows.Automation.SelectionItemPattern]::Pattern)) { $s.Select(); Start-Sleep -Milliseconds 700 } else { $log.Add('no row to select') } }
function ScrollPage($percent) { $sv = FindFirst (MainEl) { param($e) ($sp = Pat $e ([System.Windows.Automation.ScrollPattern]::Pattern)) -and $sp.Current.VerticallyScrollable -and $e.Current.ClassName -match 'ScrollViewer' }; if ($sp = Pat $sv ([System.Windows.Automation.ScrollPattern]::Pattern)) { $sp.SetScrollPercent(-1, $percent); Start-Sleep -Milliseconds 500 } }
# Picks an item in a ComboBox by the box's accessible name and the item's text (changes settings live).
function Choose($comboName, $itemName) {
  $c = FindFirst (MainEl) { param($e) $e.Current.ControlType -eq $CT::ComboBox -and $e.Current.Name -eq $comboName }
  if (-not ($x = Pat $c ([System.Windows.Automation.ExpandCollapsePattern]::Pattern))) { $log.Add("no combo $comboName"); return }
  $x.Expand(); Start-Sleep -Milliseconds 500
  $item = $c.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $itemName)))
  if ($s = Pat $item ([System.Windows.Automation.SelectionItemPattern]::Pattern)) { $s.Select() } else { $log.Add("no item $itemName in $comboName") }
  Start-Sleep -Milliseconds 300; try { $x.Collapse() } catch { }; Start-Sleep -Milliseconds 900
}
function Select1($name) { foreach ($e in (FindAll (MainEl) $name)) { if ($sp = Pat $e ([System.Windows.Automation.SelectionItemPattern]::Pattern)) { $sp.Select(); Start-Sleep -Milliseconds 700; return $true } }; $log.Add("no list item $name"); $false }
function Expand1($prefix) { $x = FindFirst (MainEl) { param($e) $e.Current.Name.StartsWith($prefix) -and (Pat $e ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)) }; if ($p = Pat $x ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)) { $p.Expand(); Start-Sleep -Milliseconds 600 } }

# Sizes are in device-independent pixels (what the window sees), so a pass looks the same on a 100% monitor
# and on the 4K TV (250%). A size bigger than the monitor's work area is clamped to it and noted.
function Size($w, $h) {
  $dpi = [CW]::GetDpiForWindow($script:mainHandle); if ($dpi -lt 96) { $dpi = 96 }
  $scale = $dpi / 96.0
  $wa = [System.Windows.Forms.Screen]::FromHandle($script:mainHandle).WorkingArea
  $pw = [int]($w * $scale); $ph = [int]($h * $scale)
  if ($pw -gt $wa.Width - 20 -or $ph -gt $wa.Height - 20) {
    $log.Add(("pass '{0}': asked for {1}x{2} DIP, the monitor fits {3}x{4}" -f $script:pass, $w, $h, [int](($wa.Width - 20) / $scale), [int](($wa.Height - 20) / $scale)))
    $pw = [Math]::Min($pw, $wa.Width - 20); $ph = [Math]::Min($ph, $wa.Height - 20)
  }
  [CW]::SetWindowPos($script:mainHandle, [IntPtr]::Zero, $wa.Left + 10, $wa.Top + 10, $pw, $ph, 0x4 -bor 0x10) | Out-Null   # NOZORDER | NOACTIVATE
  Start-Sleep -Milliseconds 900
}


# Windows scales the 4K TV to 250%: the window is really 2.5x bigger than the DIP size the app lays out in.
# Shots are saved at DIP size (same as on a 100% monitor) unless CARS_SHOT_FULL=1 asks for every pixel.
function Save-Shot($bmp, $file, $hwnd) {
  $dpi = [CW]::GetDpiForWindow($hwnd); if ($dpi -lt 96) { $dpi = 96 }
  if ($dpi -gt 100 -and $env:CARS_SHOT_FULL -ne '1') {
    $w = [int][Math]::Round($bmp.Width * 96.0 / $dpi); $h = [int][Math]::Round($bmp.Height * 96.0 / $dpi)
    $small = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($small)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($bmp, 0, 0, $w, $h); $g.Dispose()
    $small.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $small.Dispose()
  } else { $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png) }
}

function Grab([IntPtr]$h, $file) {
  $b = [CarsShotBounds]::Of($h); $r = [pscustomobject]@{ L = $b[0]; T = $b[1]; R = $b[2]; B = $b[3] }
  $bmp = New-Object System.Drawing.Bitmap ([Math]::Max(1, $r.R - $r.L)), ([Math]::Max(1, $r.B - $r.T))
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [CW]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
  Save-Shot $bmp $file $h; $bmp.Dispose()
}

$script:pass = ''
function Shot($name, [IntPtr]$h = [IntPtr]::Zero) {
  if ($h -eq [IntPtr]::Zero) { $h = $script:mainHandle }
  $dir = Join-Path $outRoot $script:pass; New-Item -ItemType Directory -Force $dir | Out-Null
  $file = Join-Path $dir "$name.png"; Grab $h $file
  $shots.Add([pscustomobject]@{ Pass = $script:pass; Name = $name; File = "$script:pass/$name.png" })
}

# The test copy's other windows (uninstall window, dialogs). Owned windows show up in UI Automation
# as children of their owner, not of the desktop.
function OtherWindow {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Window)
  for ($i = 0; $i -lt 10; $i++) {
    # Tooltips and popups can show up as child windows too; a dialog is at least 300 x 250 (screen pixels).
    foreach ($w in (MainEl).FindAll($TS::Children, $cond)) { $b = $w.Current.BoundingRectangle; if ($w.Current.NativeWindowHandle -ne 0 -and $b.Width -ge 300 -and $b.Height -ge 250) { return $w } }
    Start-Sleep -Milliseconds 300
  }
}
function CloseOther { if ($w = OtherWindow) { if ($wp = Pat $w ([System.Windows.Automation.WindowPattern]::Pattern)) { try { $wp.Close() } catch { } }; Start-Sleep -Milliseconds 700 } }

# The command palette is its own top-level window; it's typed into through UI Automation (no focus needed).
function PaletteShots([switch]$Full) {
  Go palette; Start-Sleep -Milliseconds 600
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, (Main).Id)
  $pal = $AE::RootElement.FindAll($TS::Children, $cond) | Where-Object { $_.Current.Name -eq 'Command palette' } | Select-Object -First 1
  if (-not $pal) { $log.Add('no palette window'); return }
  $h = [IntPtr]$pal.Current.NativeWindowHandle
  Shot '30-palette' $h
  if ($Full) {
    $box = $pal.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Search commands')))
    if ($vp = Pat $box ([System.Windows.Automation.ValuePattern]::Pattern)) {
      foreach ($q in 'upd', 'steam', 'quiet') { $vp.SetValue($q); Start-Sleep -Milliseconds 700; Shot "31-palette-$q" $h }
    }
  }
  if ($wp = Pat $pal ([System.Windows.Automation.WindowPattern]::Pattern)) { $wp.Close(); Start-Sleep -Milliseconds 500 }
}

# ---------- one full tour ----------
function Tour([switch]$Full) {
  # A control that vanished between "find" and "click" (a list refreshing) must not end the whole tour: the error is noted and the tour goes on.
  $ErrorActionPreference = 'Continue'
  Go home;     Shot '01-home'
  Go apps;     Shot '02-apps'
  SelectFirstRow; Toggle1 'Details'; Shot '03-apps-details'; Toggle1 'Details'
  Toggle1 'Has update'; Shot '04-apps-has-update'; Toggle1 'Has update'
  # The update check started at launch may still be running; the shot waits (up to a minute) for it to finish.
  Go refresh;  for ($i = 0; $i -lt 60 -and (FindAll (MainEl) ('Checking' + [char]0x2026)).Count -gt 0; $i++) { Start-Sleep -Seconds 1 }; Shot '05-refresh'
  if ($Full) { ScrollPage 100; Expand1 'Ignored'; Shot '06-refresh-bottom'; ScrollPage 0 }
  Go storage;  Tab 'Explore'; Shot '07-storage-explore'
  Tab 'Big';   Shot '08-storage-files'
  if ($Full) { SelectFirstRow; Shot '09-storage-files-selected' }
  Tab 'Games'; Shot '10-storage-games'
  Tab 'Old Windows'; Shot '11-storage-oldwindows'
  Tab 'Explore'
  Go cleanup;  Shot '12-cleanup'
  if ($Full) { ScrollPage 100; Shot '13-cleanup-bottom'; ScrollPage 0 }
  Go privacy; Shot '14a-privacy'
  ScrollPage 50; Shot '14b-privacy-middle'; ScrollPage 100; Shot '14c-privacy-bottom'; ScrollPage 0
  Go optimize; Shot '14d-optimize'
  ScrollPage 50; Shot '14e-optimize-middle'; ScrollPage 100; Shot '14f-optimize-bottom'; ScrollPage 0
  Go history;  Shot '14-history'
  if ($Full) { SelectFirstRow; Shot '15-history-selected' }
  Go settings; Select1 'Appearance'; Shot '16-settings'
  if ($Full -and -not $Docs) {   # docs skip it: a switch back that fails would wash out every later screenshot
    # Switch appearance live (the path that threw in round 4's first capture), then switch back.
    Choose 'Background' 'Solid'; Shot '16b-settings-solid-live'
    Choose 'Theme' 'Light'; Shot '16c-settings-light-live'
    # Back to this pass's own look (it used to always go back to dark translucent, so the light passes'
    # later screens came out dark).
    Choose 'Theme' $(if ($script:theme -eq 'System') { 'Same as Windows' } else { $script:theme })
    Choose 'Background' $(if ($script:backdrop -eq 'Solid') { 'Solid' } else { 'Translucent (wallpaper shows through)' })
  }
  # Settings has a section list since round 6: photograph every section (and the bottom of long ones).
  $n = 0
  foreach ($section in 'Scheduled maintenance', 'Gaming mode', 'Phone alerts', 'Health', 'Command palette', 'Background work', 'Admin helper', 'This PC', 'Protected apps', 'Ignored updates', 'About and updates', 'Data and files') {
    $n++
    if (Select1 $section) {
      $slug = '17-settings-{0:00}-{1}' -f $n, ($section -replace ' ', '-').ToLower()
      Shot $slug
      if ($Full -and $section -in 'Gaming mode', 'Phone alerts') { ScrollPage 100; Shot "$slug-bottom"; ScrollPage 0 }
    }
  }
  Select1 'Appearance' | Out-Null

  # The tabs added in rounds 6 to 8 (Health, Apps, Windows Update, Storage and Backup & sync).
  Go apps; Tab 'Get apps'; Shot '21-apps-getapps'; Tab 'Startup'; Start-Sleep -Milliseconds 1500; Shot '22-apps-startup'
  Tab 'Right-click'; Start-Sleep -Milliseconds 1500; Shot '23-apps-menu'; Tab 'Installed'
  Go refresh; Tab 'Windows Update'; Start-Sleep -Milliseconds 1500; Shot '24-refresh-windowsupdate'; Tab 'Updates'
  Go storage; Tab 'Duplicates'; Shot '25-storage-duplicates'; Tab 'Junk'; Shot '26-storage-junk'; Tab 'Move folders'; Shot '27-storage-move'; Tab 'Explore'
  Go health
  foreach ($t in @(@('Overview', 'overview'), @('Sensors', 'sensors'), @('Drives', 'drives'), @('Crashes', 'crashes'), @('Sleep', 'sleep'), @('Drivers', 'drivers'), @('Activity', 'activity'), @('Network', 'network'))) {
    Tab $t[0]; Start-Sleep -Milliseconds 2500; Shot ('28-health-' + $t[1])
  }
  Tab 'Overview'
  Go backup
  foreach ($t in @(@('Backups', 'backups'), @('Your setup', 'setup'), @('Your PCs', 'pcs'))) { Tab $t[0]; Start-Sleep -Milliseconds 1200; Shot ('29-backup-' + $t[1]) }
  Tab 'Backups'

  # Gaming mode switched on by hand (no admin session in a test copy, so nothing is paused), then off.
  Go home; Toggle1 'Gaming mode'; Shot '01b-home-gaming'; Toggle1 'Gaming mode'
  PaletteShots -Full:$Full

  if ($Full) {
    # The uninstall window, review step only (it is closed without starting anything).
    # The first rows are protected (their Uninstall is disabled), so an ordinary app is searched for first.
    Go apps; SetText 'Search apps' 'Chrome'; SelectFirstRow; Toggle1 'Details'
    if (Invoke1 ('Uninstall' + [char]0x2026)) { Start-Sleep -Milliseconds 2500; if ($w = OtherWindow) { Shot '18-uninstall-review' ([IntPtr]$w.Current.NativeWindowHandle); CloseOther } }
    # A themed confirmation dialog: unprotecting a broadly protected app asks first; it's cancelled.
    SetText 'Search apps' 'Visual C++'; SelectFirstRow
    if (Invoke1 'Unprotect') { Start-Sleep -Milliseconds 800; if ($w = OtherWindow) { Shot '19-dialog-confirm' ([IntPtr]$w.Current.NativeWindowHandle); CloseOther } }
    SetText 'Search apps' ''; Toggle1 'Details'

    # Every first-run step; close the window without Finish or Skip so no completion marker is written.
    Go firstrun; Start-Sleep -Milliseconds 800
    if ($w = OtherWindow) {
      $wh = [IntPtr]$w.Current.NativeWindowHandle
      Shot '20-firstrun-1' $wh
      # Searched from the main window: the setup window is owned by it and sits in its UI Automation tree.
      foreach ($n in 2..7) { if (Invoke1 'Next') { Shot "20-firstrun-$n" $wh } }
      CloseOther
    }
  }
}

# ---------- passes ----------
$errorsLog = Join-Path $devData 'logs\errors.log'
$errorsBefore = if (Test-Path $errorsLog) { (Get-Item $errorsLog).Length } else { 0 }
try {
  Start-Dev 'Dark' 'Translucent'
  if ($StartPass -eq 'default') { $script:pass = 'dark-translucent-default'; Size 1320 860; Tour -Full }
  if (-not $Quick -and -not $Docs) {
    if ($StartPass -in 'default','minimum') { $script:pass = 'dark-translucent-minimum'; Size 960 620; Tour }
    if ($StartPass -in 'default','minimum','wide') { $script:pass = 'dark-translucent-wide'; Size 2520 1380; Tour }
    if (-not $ReleaseCheck) {
      Start-Dev 'Dark' 'Solid';         $script:pass = 'dark-solid';         Size 1320 860; Tour
      Start-Dev 'Light' 'Solid';        $script:pass = 'light-solid';        Size 1320 860; Tour -Full
      Start-Dev 'Light' 'Translucent';  $script:pass = 'light-translucent';  Size 1320 860; Tour
    }
    Start-Dev 'System' 'Translucent'; $script:pass = 'system-translucent'; Size 1320 860; Tour -Full
  }
}
finally {
  Stop-Dev
  if (Test-Path $backup) { Move-Item $backup $settingsFile -Force }
}

# ---------- the screens the documentation uses ----------
if ($Docs) {
  $docImages = Join-Path $root 'docs\images'
  New-Item -ItemType Directory -Force $docImages | Out-Null
  $map = [ordered]@{
    '01-home' = 'home'; '02-apps' = 'apps-installed'; '21-apps-getapps' = 'apps-getapps'; '22-apps-startup' = 'apps-startup'; '23-apps-menu' = 'apps-menu'
    '05-refresh' = 'refresh-updates'; '24-refresh-windowsupdate' = 'refresh-windowsupdate'
    '07-storage-explore' = 'storage-explore'; '25-storage-duplicates' = 'storage-duplicates'; '26-storage-junk' = 'storage-junk'
    '10-storage-games' = 'storage-games'; '27-storage-move' = 'storage-move'; '11-storage-oldwindows' = 'storage-oldwindows'
    '12-cleanup' = 'cleanup'; '14a-privacy' = 'privacy'; '14d-optimize' = 'optimize'; '14e-optimize-middle' = 'optimize-gaming'; '14-history' = 'history'
    '28-health-overview' = 'health-overview'; '28-health-sensors' = 'health-sensors'; '28-health-drives' = 'health-drives'; '28-health-crashes' = 'health-crashes'
    '28-health-sleep' = 'health-sleep'; '28-health-drivers' = 'health-drivers'; '28-health-activity' = 'health-activity';
    '29-backup-backups' = 'backup-backups'; '29-backup-setup' = 'backup-setup'; '29-backup-pcs' = 'backup-pcs'
    '16-settings' = 'settings-appearance'; '17-settings-01-scheduled-maintenance' = 'settings-maintenance'; '17-settings-02-gaming-mode' = 'settings-gaming'
    '18-uninstall-review' = 'uninstall-review'; '20-firstrun-1' = 'firstrun-1'; '20-firstrun-2' = 'firstrun-2'; '20-firstrun-3' = 'firstrun-3'; '20-firstrun-4' = 'firstrun-4'
    '30-palette' = 'palette'
  }
  $copied = 0
  foreach ($k in $map.Keys) {
    $src = Join-Path $outRoot "dark-translucent-default\$k.png"
    if (Test-Path $src) { Copy-Item $src (Join-Path $docImages "$($map[$k]).png") -Force; $copied++ } else { $log.Add("docs image missing: $k") }
  }
  $log.Add("$copied images copied to $docImages")
  # Pixelate this PC's own lists (games, startup apps, running programs…) before anything can be published.
  & (Join-Path $PSScriptRoot 'Blur-DocImages.ps1') -Images $docImages | ForEach-Object { $log.Add("$_") }
}

# ---------- contact sheet ----------
$html = New-Object System.Text.StringBuilder
[void]$html.Append("<!doctype html><meta charset=utf-8><title>C.A.R.S. screens $stamp</title><style>body{font:14px Segoe UI,sans-serif;background:#1b1b1f;color:#eee;margin:24px}h2{margin:28px 0 8px}div.g{display:flex;flex-wrap:wrap;gap:12px}figure{margin:0;width:420px}img{width:420px;border:1px solid #444;border-radius:6px}figcaption{font-size:12px;color:#aaa;margin-top:4px}</style>")
[void]$html.Append("<h1>C.A.R.S. Control - every screen ($stamp)</h1>")
foreach ($g in ($shots | Group-Object Pass)) {
  [void]$html.Append("<h2>$($g.Name)</h2><div class=g>")
  foreach ($s in $g.Group) { [void]$html.Append("<figure><a href='$($s.File)'><img src='$($s.File)' loading=lazy></a><figcaption>$($s.Name)</figcaption></figure>") }
  [void]$html.Append("</div>")
}
[System.IO.File]::WriteAllText((Join-Path $outRoot 'index.html'), $html.ToString())
# Anything the app logged as unexpected during the run is a bug to fix.
if ((Test-Path $errorsLog) -and (Get-Item $errorsLog).Length -gt $errorsBefore) {
  $fs = [System.IO.File]::Open($errorsLog, 'Open', 'Read', 'ReadWrite'); $fs.Seek($errorsBefore, 'Begin') | Out-Null
  $new = (New-Object System.IO.StreamReader $fs).ReadToEnd(); $fs.Dispose()
  $log.Add("APP ERRORS DURING CAPTURE:"); $log.Add($new)
}
foreach ($e in ($Error | Select-Object -First 12)) { $log.Add("script note: " + $e.Exception.Message + " (line " + $e.InvocationInfo.ScriptLineNumber + ")") }
$log | Set-Content (Join-Path $outRoot 'log.txt')
"{0} screenshots -> {1}" -f $shots.Count, (Join-Path $outRoot 'index.html')
if ($log.Count) { "notes:"; $log }
