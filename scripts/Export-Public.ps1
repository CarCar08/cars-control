<#
.SYNOPSIS
  Copies the repository's tracked files, minus the private ones, into a folder for the public repository, and refuses to finish
  if anything personal is left in them.
.DESCRIPTION
  The private repository keeps the development history and the working notes (AGENTS.md, STATUS, the owner's setup notes). The
  public repository gets a reviewed snapshot of everything else. Run from the private repository:

    .\scripts\Export-Public.ps1 -Destination C:\path\to\public-clone

  The destination may be an empty folder or a clone of the public repository: its .git is kept, every other file is replaced by
  the snapshot (files removed here are removed there too). Nothing is committed or pushed; review `git status` there first.

  The scan looks for: this PC's name, this account's profile path, e-mail addresses other than example.com and GitHub no-reply
  addresses, GitHub tokens and private keys, and every line of the private patterns file (-Patterns, one regular expression per
  line, # for comments). That file is excluded from the snapshot, so it can name what must never be published.
#>
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$Patterns = (Join-Path $PSScriptRoot '..\docs\dev\public-scan-patterns.txt'),
    [switch]$ScanOnly
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Working notes that stay in the private repository.
$private = @('AGENTS.md', 'CLAUDE.md', 'docs/my-setup.md', 'docs/dev/STATUS.md', 'docs/dev/RELEASE-READINESS.md', 'docs/dev/public-scan-patterns.txt')

$files = @(git -C $root ls-files) | Where-Object { $_ -and $private -notcontains $_ }
if ($files.Count -lt 50) { throw "Only $($files.Count) tracked files found; is $root the repository?" }

# ---------- scan ----------
$rules = [System.Collections.Generic.List[object]]::new()
$rules.Add(@{ Name = 'this PC''s name'; Rx = '\b' + [regex]::Escape($env:COMPUTERNAME) + '\b' })
$rules.Add(@{ Name = 'this account''s profile'; Rx = [regex]::Escape($env:USERPROFILE) + '(\\|\b)' })
$rules.Add(@{ Name = 'GitHub token'; Rx = '\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}' })
$rules.Add(@{ Name = 'private key'; Rx = '-----BEGIN [A-Z ]*PRIVATE KEY-----' })
$rules.Add(@{ Name = 'e-mail address'; Rx = '\b[A-Za-z0-9._%+-]+@(?!example\.(com|org)\b|users\.noreply\.github\.com\b|anthropic\.com\b)[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b' })
# This PC's own phone-alert topic (the key to its alerts), read from its settings so it's never written into a file here.
$settingsFile = Join-Path $env:LOCALAPPDATA 'CarsControl\settings.json'
if (Test-Path -LiteralPath $settingsFile) {
    try { $topic = (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Alerts.Topic } catch { $topic = $null }
    if ($topic -and $topic.Length -ge 4) { $rules.Add(@{ Name = 'this PC''s alert topic'; Rx = [regex]::Escape($topic) }) }
}
if (Test-Path -LiteralPath $Patterns) {
    foreach ($line in Get-Content -LiteralPath $Patterns) {
        $t = $line.Trim()
        if ($t -and -not $t.StartsWith('#')) { $rules.Add(@{ Name = 'private pattern'; Rx = $t }) }
    }
} else { Write-Warning "No private patterns file at $Patterns; only the built-in checks run." }

$text = '\.(cs|xaml|ps1|psm1|md|txt|json|yml|yaml|xml|props|targets|csproj|sln|manifest|html|css|js|config|gitignore|editorconfig)$'
$hits = foreach ($f in $files) {
    if ($f -notmatch $text -and $f -notmatch '^[^.]+$') { continue }
    $path = Join-Path $root $f
    $n = 0
    foreach ($line in [IO.File]::ReadLines($path)) {
        $n++
        foreach ($r in $rules) {
            if ($line -match $r.Rx) { [pscustomobject]@{ File = $f; Line = $n; Rule = $r.Name } }
        }
    }
}
if ($hits) {
    # Only where and which rule: the matched text itself is what must not be printed into logs.
    $hits | Format-Table -AutoSize | Out-String | Write-Host
    throw "$(@($hits).Count) personal marker(s) found; fix them in the private repository and export again."
}
Write-Host "Scan clean: $($files.Count) files, $($rules.Count) rules."
if ($ScanOnly) { return }

# ---------- copy ----------
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$dest = (Resolve-Path $Destination).Path
if ($dest.TrimEnd('\') -ieq $root.TrimEnd('\')) { throw 'The destination is this repository.' }
Get-ChildItem -LiteralPath $dest -Force | Where-Object { $_.Name -ne '.git' } | Remove-Item -Recurse -Force
foreach ($f in $files) {
    $target = Join-Path $dest $f
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $f) -Destination $target
}
Write-Host "Exported $($files.Count) files to $dest (left out: $($private -join ', '))."
