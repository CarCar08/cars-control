<#
  Keyboard audit of a test copy (Windows PowerShell 5.1): walks the Tab order of every page and reports
  controls with no accessible name, Tab stops outside the window, focus traps and pages that can't be
  reached by keyboard. The copy does the walk itself (`--page tabaudit`), so no keys are pressed and
  nothing takes focus from what you're doing.  Report: tools\ui-audit\last-keyboard-report.txt
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\..\.."
. (Join-Path $PSScriptRoot 'Initialize-DevData.ps1')
$devData = Initialize-DevData $root
$exe = (Get-ChildItem "$root\src\CarsControl.App\bin\Debug" -Recurse -Filter CarsControl.exe | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
$env:CARS_DEV_INSTANCE = '1'; $env:CARS_HELPER_UNELEVATED = '1'; $env:CARS_DEV_MONITOR = 'tv'
$settingsFile = Join-Path $devData 'settings.json'
$settingsBefore = if (Test-Path -LiteralPath $settingsFile) { [IO.File]::ReadAllBytes($settingsFile) } else { $null }
$result = Join-Path $env:TEMP 'CarsControl\tab-order.txt'
Remove-Item $result -ErrorAction SilentlyContinue

try {
Get-Process CarsControl -ErrorAction SilentlyContinue | Where-Object Path -eq $exe | Stop-Process -Force
$p = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
for ($i = 0; $i -lt 60 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
Start-Sleep -Seconds 6
Start-Process -FilePath $exe -WindowStyle Hidden -ArgumentList '--page', 'tabaudit' | Out-Null
for ($i = 0; $i -lt 900 -and -not (Test-Path $result); $i++) { Start-Sleep -Milliseconds 500 }
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $result)) { throw 'The test copy did not write a tab-order report.' }
Copy-Item $result (Join-Path $PSScriptRoot 'last-keyboard-report.txt') -Force
$text = Get-Content $result
$text | Where-Object { $_ -match '^==|Tab stops|<--|TRAP|NO STOPS|RESULT' }
} finally {
  if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  if ($null -ne $settingsBefore) { [IO.File]::WriteAllBytes($settingsFile, $settingsBefore) }
}
