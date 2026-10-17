<#
.SYNOPSIS
  Checks that one Setup can update an install made by another, the way the app's self-update does it.
.DESCRIPTION
  Everything happens in a sandbox (Setup's --sandbox: a temp folder instead of Program Files, no admin, no registry, no Start menu).
  1. The old Setup installs silently.
  2. The new Setup updates it silently, started with its working folder inside the install folder (as the app starts it) while
     another process briefly holds a file open in that folder (antivirus does this).
  3. Checks: exit code 0, the installed version is the new one, no leftover .new/.old/.failed folders, no Setup process left running.
  Every 0.9.0 release candidate failure (Setup lingering, standing in the folder it swaps, a busy file, a process exiting mid-check)
  would have failed this test.
.EXAMPLE
  scripts\Test-Update.ps1 -OldSetup artifacts-release-check\CarsControl-Setup-0.9.0.exe -NewSetup artifacts\CarsControl-Setup-1.0.0.exe
#>
param(
    [Parameter(Mandatory)][string]$OldSetup,
    [Parameter(Mandatory)][string]$NewSetup,
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'

function Version-Of([string]$exe) { (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -replace '\+.*$', '' }
function Fail([string]$why) { Write-Host "UPDATE TEST FAILED: $why" -ForegroundColor Red; exit 1 }

$work = Join-Path ([IO.Path]::GetTempPath()) ("cars-update-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$old = Join-Path $work 'old-setup.exe'; Copy-Item -LiteralPath $OldSetup -Destination $old
$new = Join-Path $work 'new-setup.exe'; Copy-Item -LiteralPath $NewSetup -Destination $new
$sandbox = Join-Path $work 'sandbox'
$installDir = Join-Path $sandbox 'ProgramFiles\CarsControl'
$oldVersion = Version-Of $old; $newVersion = Version-Of $new
Write-Host "Update test: $oldVersion -> $newVersion in $sandbox"

function Run-Setup([string]$exe, [string[]]$arguments, [string]$cwd) {
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $cwd -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $p.Id -Force; Fail "$([IO.Path]::GetFileName($exe)) $($arguments -join ' ') didn't finish in $TimeoutSeconds s" }
    return $p.ExitCode
}

# 1. The old version, installed silently.
$code = Run-Setup $old @('--sandbox', $sandbox, '--install', '--silent') $work
if ($code -ne 0) { Fail "the old Setup's install exited with $code" }
$exe = Join-Path $installDir 'CarsControl.exe'
if (-not (Test-Path -LiteralPath $exe)) { Fail 'the old install has no CarsControl.exe' }
if ((Version-Of $exe) -ne $oldVersion) { Fail "the old install reports $(Version-Of $exe), expected $oldVersion" }

# 2. A file held open for a few seconds while the update starts (like antivirus scanning it).
$held = Get-ChildItem -LiteralPath $installDir -Filter '*.dll' | Select-Object -First 1
$holder = Start-Job -ScriptBlock {
    param($path)
    $stream = [IO.File]::Open($path, 'Open', 'Read', 'Read')
    Start-Sleep -Seconds 6
    $stream.Dispose()
} -ArgumentList $held.FullName
Start-Sleep -Milliseconds 500

# 3. The update, started from inside the install folder the way the app starts it.
$code = Run-Setup $new @('--sandbox', $sandbox, '--update', '--silent') $installDir
Wait-Job $holder -Timeout 30 | Out-Null; Remove-Job $holder -Force
if ($code -ne 0) {
    $log = Join-Path ([IO.Path]::GetTempPath()) 'CarsControl-Setup.log'
    if (Test-Path $log) { Get-Content $log -Tail 15 -Encoding UTF8 | ForEach-Object { Write-Host "  $_" } }
    Fail "the new Setup's update exited with $code"
}
if ((Version-Of $exe) -ne $newVersion) { Fail "after the update the install reports $(Version-Of $exe), expected $newVersion" }
foreach ($leftover in @("$installDir.new", "$installDir.old", "$installDir.new.failed")) {
    if (Test-Path -LiteralPath $leftover) { Fail "leftover folder $leftover" }
}
$lingering = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($work, [StringComparison]::OrdinalIgnoreCase) }
if ($lingering) { $lingering | Stop-Process -Force; Fail "a Setup process was still running afterwards ($($lingering.ProcessName -join ', '))" }

Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "UPDATE TEST PASSED: $oldVersion -> $newVersion" -ForegroundColor Green
exit 0
