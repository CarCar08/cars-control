<# Measures only a TV test copy, using its no-focus navigation hooks. No settings are changed. #>
param([Parameter(Mandatory=$true)][string]$Exe, [string]$Label = 'Release', [int]$Samples = 3,
      [int]$IdleSeconds = 12, [string]$Output = 'artifacts-perf')
$ErrorActionPreference = 'Stop'
if ($Label -notmatch '^[A-Za-z0-9_-]{1,40}$') { throw 'Use a short alphanumeric label.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$exePath = [IO.Path]::GetFullPath($Exe)
if (-not $exePath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $exePath)) { throw 'Only a test executable inside this repository may be measured.' }
$outPath = [IO.Path]::GetFullPath((Join-Path $root $Output))
if (-not $outPath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be inside this repository.' }
New-Item -ItemType Directory -Force -Path $outPath | Out-Null
$env:CARS_DEV_INSTANCE = '1'; $env:CARS_HELPER_UNELEVATED = '1'; $env:CARS_DEV_MONITOR = 'tv'
. (Join-Path $PSScriptRoot 'Initialize-DevData.ps1')
$devData = Initialize-DevData $root
$results = @()
function Navigate([string]$page) {
  $marker = 'activation handled: ' + $page + '$'
  $before = if ($log -and (Test-Path -LiteralPath $log)) { @(Select-String -LiteralPath $log -Pattern $marker).Count } else { 0 }
  $signal = Start-Process -FilePath $exePath -ArgumentList '--page', $page -WindowStyle Hidden -PassThru
  if (-not $signal.WaitForExit(10000)) { throw "Navigation signal did not finish: $page" }
  if ($page -eq 'quit') { return }
  $deadline = [DateTime]::UtcNow.AddSeconds(30)
  do {
    Start-Sleep -Milliseconds 100
    $handled = @(Select-String -LiteralPath $log -Pattern $marker).Count -gt $before
  } until ($handled -or [DateTime]::UtcNow -gt $deadline)
  if (-not $handled) {
    $stackTool = Join-Path $root 'artifacts-tools\dotnet-stack.exe'
    if (Test-Path -LiteralPath $stackTool) { & $stackTool report --process-id $process.Id | Set-Content (Join-Path $outPath ($Label + '-timeout-stacks.txt')) }
    throw "The test copy did not acknowledge $page; no measurement is valid for this sample."
  }
}
function CpuSample($process) {
  $process.Refresh(); $before = $process.TotalProcessorTime.TotalMilliseconds; $watch = [Diagnostics.Stopwatch]::StartNew()
  Start-Sleep -Seconds $IdleSeconds
  $process.Refresh()
  [math]::Round(100 * ($process.TotalProcessorTime.TotalMilliseconds - $before) / $watch.Elapsed.TotalMilliseconds / [Environment]::ProcessorCount, 4)
}
for ($sample = 1; $sample -le $Samples; $sample++) {
  if (Get-Process CarsControl -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) }) { throw 'Close existing repository test copies before measuring.' }
  $log = Join-Path $outPath ("{0}-{1}-perf.log" -f $Label, $sample)
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
  $env:CARS_DEV_PERF_LOG = $log
  $process = Start-Process -FilePath $exePath -WindowStyle Hidden -PassThru
  try {
    $deadline = [DateTime]::UtcNow.AddSeconds(40); $paint = $null
    do {
      Start-Sleep -Milliseconds 200
      if (Test-Path -LiteralPath $log) { $paint = Select-String -LiteralPath $log -Pattern '(\d+) ms since start  first paint' | Select-Object -Last 1 }
      if ($process.HasExited) { throw 'Test copy exited before its first frame.' }
    } until ($paint -or [DateTime]::UtcNow -gt $deadline)
    if (-not $paint) { throw 'No first-paint timing was recorded.' }
    $startup = [int]$paint.Matches[0].Groups[1].Value
    Start-Sleep -Seconds 8
    $peak = 0.0; $pages = @()
    foreach ($page in @('home','apps','refresh','storage','cleanup','health','privacy','backup','history','settings')) {
      Navigate $page; Start-Sleep -Milliseconds 1400; Navigate 'perf'; $process.Refresh()
      $working = [math]::Round($process.WorkingSet64 / 1MB, 1); $peak = [math]::Max($peak, $working)
      $pages += [pscustomobject]@{ Page=$page; WorkingSetMiB=$working }
    }
    Navigate 'home'; Start-Sleep -Seconds 4
    $visibleCpu = CpuSample $process
    Navigate 'tray'; Start-Sleep -Seconds 2
    $trayCpu = CpuSample $process
    $process.Refresh()
    $trayBefore = [math]::Round($process.WorkingSet64/1MB,1)
    # What the app does after three minutes in the tray (PageCache.Release), triggered now instead of waiting.
    Navigate 'release'; Start-Sleep -Seconds 3; $process.Refresh()
    $trayPrivate = [math]::Round($process.PrivateMemorySize64/1MB,1)
    $trayReleased = [math]::Round($process.WorkingSet64/1MB,1)
    $released = Select-String -LiteralPath $log -Pattern 'pages released: (\d+); still alive after release \(should be 0\): (\d+)' | Select-Object -Last 1
    $results += [pscustomobject]@{ Label=$Label; Sample=$sample; FirstPaintMs=$startup; BrowsingPeakMiB=$peak; TrayWorkingSetMiB=$trayBefore; TrayAfterReleaseMiB=$trayReleased; TrayPrivateMiB=$trayPrivate; PagesReleased=$(if($released){$released.Matches[0].Groups[1].Value}); PagesStillAlive=$(if($released){$released.Matches[0].Groups[2].Value}); VisibleCpuPercent=$visibleCpu; TrayCpuPercent=$trayCpu; Pages=$pages }
    $results[-1] | Select-Object Label,Sample,FirstPaintMs,BrowsingPeakMiB,TrayWorkingSetMiB,TrayAfterReleaseMiB,TrayPrivateMiB,PagesStillAlive,TrayCpuPercent | Format-Table -AutoSize
  } finally {
    if (-not $process.HasExited) { Navigate 'quit'; if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force } }
  }
}
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outPath ($Label + '-results.json')) -Encoding UTF8
