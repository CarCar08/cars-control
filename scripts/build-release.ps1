<#
  Builds a release: the app, the admin helper, and the single-file installer that carries them.

    powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 [-Version 0.8.0] [-Output artifacts]

  Produces, in the output folder:
    CarsControl-Setup-<version>.exe   the installer (app + helper + libraries inside, one file)
    SHA256SUMS.txt                    its checksum, for the Releases page and for self-update
    release-notes.md                  this version's section of CHANGELOG.md

  Needs the .NET SDK (10, or 8). No admin rights, no Visual Studio. Works the same on a developer PC and
  in GitHub Actions (.github\workflows\release.yml calls this script).
#>
param(
    [string]$Version,
    [string]$Output = 'artifacts',
    [string]$Configuration = 'Release',
    [switch]$ReadyToRun
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# The version: -Version, else <Version> in Directory.Build.props.
if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
    $Version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$') { throw "Not a version: '$Version' (expected 1.2.3 or 1.2.3-rc.1)" }
# Assembly versions are four numbers; the full text goes in InformationalVersion.
$numeric = ($Version -replace '-.*$', '') + '.0'

if ($Output -notmatch '^artifacts(?:-[A-Za-z0-9_-]+)?$') { throw 'Output must be a repository-root artifacts or artifacts-<name> folder; existing contents are replaced.' }
$out = [IO.Path]::GetFullPath((Join-Path $root $Output))
if (-not $out.StartsWith([IO.Path]::GetFullPath($root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Release output escaped the repository.' }
$work = Join-Path $out 'work'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null

function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Run($file, $arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file failed ($LASTEXITCODE)" }
}

Step "Building C.A.R.S. Control $Version (app + admin helper)"
$app = Join-Path $work 'app'
$publishArgs = @('publish', 'src\CarsControl.App\CarsControl.App.csproj', '-c', $Configuration, '-o', $app, '--nologo', '-v', 'q',
    "-p:Version=$numeric", "-p:InformationalVersion=$Version")
if ($ReadyToRun) { $publishArgs += '-p:PublishReadyToRun=true' }
Run 'dotnet' $publishArgs
foreach ($f in 'CarsControl.exe', 'CarsControl.Helper.exe', 'Microsoft.Management.Deployment.winmd') {
    if (-not (Test-Path (Join-Path $app $f))) { throw "Missing $f in the publish output" }
}
# Debug symbols, XML docs and satellite resource folders for other languages don't belong in a release.
Get-ChildItem $app -Recurse -Include *.pdb, *.xml | Remove-Item -Force
Get-ChildItem $app -Directory | Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' -and $_.Name -ne 'en' } | Remove-Item -Recurse -Force
# Mono.Posix's native helpers are only used on Linux/macOS (LibreHardwareMonitor pulls them in): 1.5 MB of nothing on Windows.
Get-ChildItem $app -File | Where-Object { $_.Name -in 'libMonoPosixHelper.dll', 'MonoPosixHelper.dll' } | Remove-Item -Force
# The licences travel with the program (Settings › About & updates › Licenses opens the notices).
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $app 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $app 'THIRD-PARTY-NOTICES.md')
& (Join-Path $root 'scripts\Copy-ThirdPartyNotices.ps1') -AppFolder $app

Step "Packing the payload"
$tool = Join-Path $work 'tool'
Run 'dotnet' @('publish', 'tools\CarsControl.ReleaseTool\CarsControl.ReleaseTool.csproj', '-c', $Configuration, '-o', $tool, '--nologo', '-v', 'q')
$payload = Join-Path $work 'payload'
Run 'dotnet' @((Join-Path $tool 'CarsControl.ReleaseTool.dll'), 'payload', $app, $Version, $payload)

Step "Building the installer"
$setupBuild = Join-Path $work 'setup'
Run 'dotnet' @('build', 'src\CarsControl.Setup\CarsControl.Setup.csproj', '-c', $Configuration, '-o', $setupBuild, '--nologo', '-v', 'q',
    "-p:Version=$numeric", "-p:InformationalVersion=$Version",
    "-p:PayloadZip=$(Join-Path $payload 'payload.zip')", "-p:PayloadJson=$(Join-Path $payload 'payload.json')")
$setupExe = Join-Path $out "CarsControl-Setup-$Version.exe"
Copy-Item (Join-Path $setupBuild 'CarsControl-Setup.exe') $setupExe

# It must run on its own: nothing next to it, everything inside.
$alone = Join-Path $work 'alone'
New-Item -ItemType Directory $alone | Out-Null
Copy-Item $setupExe (Join-Path $alone 'CarsControl-Setup.exe')

Step "Checksums and release notes"
Run 'dotnet' @((Join-Path $tool 'CarsControl.ReleaseTool.dll'), 'sums', (Join-Path $out 'SHA256SUMS.txt'), $setupExe)
Run 'dotnet' @((Join-Path $tool 'CarsControl.ReleaseTool.dll'), 'notes', (Join-Path $root 'CHANGELOG.md'), $Version, (Join-Path $out 'release-notes.md'))

# Optional signing: when a certificate is configured (CARS_SIGN_PFX / CARS_SIGN_PASSWORD, or an Actions
# secret), the installer is signed here, before its checksum is taken. Left off until there is one.
if ($env:CARS_SIGN_PFX) { Write-Warning 'CARS_SIGN_PFX is set but signing is not wired yet: the installer stays unsigned.' }

Remove-Item $work -Recurse -Force
$size = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $setupExe ($size MB)" -ForegroundColor Green
Get-Content (Join-Path $out 'SHA256SUMS.txt')
