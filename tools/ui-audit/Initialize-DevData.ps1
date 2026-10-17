# Imported by audit scripts. Test copies get independent preferences/history; the installed copy keeps its data.
function Initialize-DevData([string]$repoRoot, [switch]$Demo) {
  $devRoot = Join-Path $repoRoot ('artifacts-ui\data-' + [Guid]::NewGuid().ToString('N'))
  & dotnet run --no-build --project (Join-Path $repoRoot 'tools\CarsControl.Probe') -- seed-dev $devRoot @(if ($Demo) { '--demo' }) | Out-Host
  if ($LASTEXITCODE -ne 0) { throw 'Could not prepare isolated UI test data.' }
  $env:CARS_DEV_DATA_ROOT = $devRoot
  $env:CARS_DEMO = if ($Demo) { '1' } else { $null }
  $env:CARS_DEV_INSTANCE = '1'; $env:CARS_HELPER_UNELEVATED = '1'; $env:CARS_DEV_MONITOR = 'tv'
  $devRoot
}
