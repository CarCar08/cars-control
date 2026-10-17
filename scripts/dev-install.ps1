<#
  Builds the installer from your working copy and runs it, so you can try your own changes exactly the way
  users get them: through CarsControl-Setup.exe.

    powershell -ExecutionPolicy Bypass -File scripts\dev-install.ps1 [-Version 0.8.0-dev.1]

  Run it as yourself (not elevated): Setup asks for administrator rights by itself. If the same version is
  already installed, Setup offers Repair, which puts your freshly built files in place.
  For quick UI work you don't need this: run the app straight from the build folder with
  CARS_DEV_INSTANCE=1 (see docs\building.md).
#>
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
    $Version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
& (Join-Path $PSScriptRoot 'build-release.ps1') -Version $Version -Output 'artifacts'
if ($LASTEXITCODE -ne 0) { throw 'The release build failed.' }
$setup = Join-Path $root "artifacts\CarsControl-Setup-$Version.exe"
Write-Host "Starting $setup" -ForegroundColor Cyan
Start-Process $setup
