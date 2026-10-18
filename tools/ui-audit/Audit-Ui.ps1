<#
  C.A.R.S. Control UI audit (Windows PowerShell 5.1). Drives a *dev* instance through UI Automation:
    1. Scroll check: every scrollable list on every page must have a finite viewport and be scrollable
       when its content overflows (the round-1 bug: WPF-UI wrapped pages in an unbounded scroll viewer,
       so inner lists reported "not scrollable" and swallowed the mouse wheel).
    2. Click audit: invokes every safe button/toggle/tab/card on each page and records whether anything
       visibly responded (UI tree changed, a window opened, or navigation happened).
  Safety: the dev instance runs with CARS_HELPER_UNELEVATED=1 (the helper can't elevate, no UAC prompt
  can appear) and a deny-list skips anything destructive or that opens Explorer/changes settings.
  Usage:  powershell -ExecutionPolicy Bypass -File tools\ui-audit\Audit-Ui.ps1
#>
param([string]$Exe = (Get-ChildItem "$PSScriptRoot\..\..\src\CarsControl.App\bin\Debug" -Recurse -Filter CarsControl.exe | Sort-Object LastWriteTime | Select-Object -Last 1).FullName,
      # Never 'privacy' or 'optimize': their switches change your own account for real even in a test copy.
      [string[]]$Pages = @('home','apps','refresh','storage','cleanup','history','settings'),
      [switch]$ScrollOnly)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$P = @{ Invoke = [System.Windows.Automation.InvokePattern]::Pattern; Toggle = [System.Windows.Automation.TogglePattern]::Pattern
        Select = [System.Windows.Automation.SelectionItemPattern]::Pattern; Expand = [System.Windows.Automation.ExpandCollapsePattern]::Pattern
        Scroll = [System.Windows.Automation.ScrollPattern]::Pattern }
. (Join-Path $PSScriptRoot 'Initialize-DevData.ps1')
$devData = Initialize-DevData ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')))

# Never invoked: destructive, settings-changing, UAC-triggering, clipboard or Explorer-opening.
$deny = 'Free up|Find leftovers|Update everything|Update selected|Update now|Uninstall|Clean selected|Quarantine|Recycle|Restore|Empty|^Import|No thanks|Test admin helper|Measure admin|Exit|^Close$|Stop ignoring|^Save$|Reset to defaults|Start with Windows|Keep running|Protect|^Open|^Show$|^Folder$|Copy|Drive [A-Z]:|Rescan|^Minimize|^Maximize|^Restore|Leave them|Uninstall selected|^Select |Caret|Send test|Install|Apply|Undo|Finish|Pause|Restart|Delete|Remove|Move it|Run now|Sync now|Back up|Download|Approve'
$safeInvoke = '^(What is |Help$|Refresh$|Re-check$|Check (now|for updates)$|Clear (search|filters)$|Show (details|all|ignored)|Details$|Reset filters$|Find duplicates$|Search$|Updates ready$|Reclaimable space$|Installed apps$|Games$|All history$|Schedule$)'

$env:CARS_DEV_INSTANCE = '1'; $env:CARS_HELPER_UNELEVATED = '1'; $env:CARS_DEV_MONITOR = 'tv'
function Get-Main {
  $p = Get-Process CarsControl -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if (-not $p) {
    Start-Process -FilePath $Exe -WindowStyle Hidden | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    do { Start-Sleep -Milliseconds 400; $p = Get-Process CarsControl -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1 } until ($p -or (Get-Date) -gt $deadline)
    Start-Sleep -Seconds 4
  }
  $AE::FromHandle($p.MainWindowHandle)
}
function Go($page) { Start-Process -FilePath $Exe -WindowStyle Hidden -ArgumentList '--page', $page | Out-Null; Start-Sleep -Milliseconds 1800 }
function Signature($root) {
  $all = $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $names = ($all | ForEach-Object { $_.Current.Name }) -join '|'
  "{0}:{1}:{2}" -f $all.Count, $names.GetHashCode(), (Get-Process CarsControl | Measure-Object).Count
}
function Pat($el, $p) { $o = $null; if ($el.TryGetCurrentPattern($p, [ref]$o)) { $o } else { $null } }

$report = New-Object System.Collections.Generic.List[string]
$main = Get-Main
foreach ($page in $Pages) {
  Go $page
  $main = Get-Main
  $report.Add("== $page")

  # ---- scroll check ----
  foreach ($s in $main.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::IsScrollPatternAvailableProperty, $true)))) {
    $sp = Pat $s $P.Scroll
    if (-not $sp -or $s.Current.IsOffscreen) { continue }
    $cls = $s.Current.ClassName; $view = [math]::Round($sp.Current.VerticalViewSize, 1)
    $status = if ($sp.Current.VerticallyScrollable) { "OK scrollable (view $view%)" } elseif ($view -ge 100) { "fits (nothing to scroll)" } else { "PROBLEM" }
    $report.Add(("   scroll  {0,-14} {1}" -f $cls, $status))
  }
  if ($ScrollOnly) { continue }

  # ---- click audit ----
  $cands = $main.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
    $n = $_.Current.Name; -not $_.Current.IsOffscreen -and $_.Current.IsEnabled -and $n -and $n -notmatch $deny -and
    ((Pat $_ $P.Invoke) -or (Pat $_ $P.Toggle) -or (Pat $_ $P.Expand) -or ($_.Current.ControlType.ProgrammaticName -eq 'ControlType.TabItem'))
  } | Select-Object -First 40
  $targets = @($cands | ForEach-Object { try { [pscustomobject]@{Name=$_.Current.Name;Type=$_.Current.ControlType} } catch {} })
  foreach ($target in $targets) {
    $name = $target.Name
    try {
      $main = Get-Main
      $el = $main.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.IsEnabled -and $_.Current.ControlType -eq $target.Type } | Select-Object -First 1
      if (-not $el) { $report.Add("   skipped $name (navigation refreshed the control)"); continue }
      if ((Pat $el $P.Invoke) -and $name -notmatch $safeInvoke) { continue }
      if ((Pat $el $P.Toggle) -and $name -ne 'Details') { continue }
      $before = Signature $main
      if ($inv = Pat $el $P.Invoke) { $inv.Invoke(); $how = 'invoke' }
      elseif ($tog = Pat $el $P.Toggle) { $tog.Toggle(); $how = 'toggle' }
      elseif ($exp = Pat $el $P.Expand) { if ($exp.Current.ExpandCollapseState -eq 'Collapsed') { $exp.Expand() } else { $exp.Collapse() }; $how = 'expand' }
      elseif ($sel = Pat $el $P.Select) { $sel.Select(); $how = 'select' }
      Start-Sleep -Milliseconds 700
      $after = Signature $main
      # Close anything modal it opened (uninstall review etc.) so the next control is reachable.
      foreach ($w in $main.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))) {
        if ($w.Current.Name -ne $main.Current.Name) { (Pat $w ([System.Windows.Automation.WindowPattern]::Pattern)).Close() }
      }
      if ($how -eq 'toggle') { $tog.Toggle() } # put toggles back
      $res = if ($after -ne $before) { 'responded' } else { 'NO VISIBLE CHANGE' }
      $report.Add(("   click   {0,-6} {1,-40} {2}" -f $how, $name, $res))
    } catch {
      if ($_.Exception.GetBaseException() -is [System.Windows.Automation.ElementNotAvailableException]) { $report.Add("   skipped $name (control was refreshed)") }
      else { $report.Add(("   click   {0,-40} ERROR {1}" -f $name, $_.Exception.Message)) }
    }
    if ((Get-Process CarsControl -ErrorAction SilentlyContinue | Where-Object Path -eq $Exe) -eq $null) { $report.Add('   !! app exited'); break }
    $main = Get-Main
  }
}
$out = Join-Path $PSScriptRoot 'last-report.txt'
$report | Set-Content $out
$report
"report: $out"
