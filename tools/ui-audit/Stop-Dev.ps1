<#
  Closes the test copies that Shoot.ps1 / Capture-All.ps1 / Measure-Performance.ps1 leave running, so a build can replace their
  files. Touched: windows titled "TEST COPY", and any CarsControl.exe running from inside this repository (build output,
  artifacts-perf, hidden or tray copies included). Never the installed app in Program Files.
#>
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\') + '\'
Get-Process CarsControl -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle -like '*TEST COPY*' -or ($_.Path -and $_.Path.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) } |
    ForEach-Object { "closing test copy $($_.Id)"; $_ | Stop-Process -Force }
Start-Sleep -Milliseconds 800
