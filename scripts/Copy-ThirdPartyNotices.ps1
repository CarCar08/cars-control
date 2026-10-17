param([Parameter(Mandatory)][string]$AppFolder)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd('\') + '\'
$app = [IO.Path]::GetFullPath($AppFolder)
if (-not $app.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Notices output must stay in the repository.' }
$deps = Get-Content (Join-Path $app 'CarsControl.deps.json') -Raw | ConvertFrom-Json
$assets = Get-Content (Join-Path $root 'src\CarsControl.App\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$out = Join-Path $app 'licenses'
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $root 'docs\licenses\*.txt') $out
$index = New-Object System.Collections.Generic.List[string]
$index.Add('Package notices from the exact restored runtime dependencies. Source, copyright and licence declarations below are package metadata; bundled upstream notice files are preserved alongside this index.')
foreach ($lib in ($deps.libraries.PSObject.Properties | Sort-Object Name)) {
    if ($lib.Value.type -ne 'package') { continue }
    $package = $null
    foreach ($cache in $packageRoots) {
        $candidate = Join-Path $cache $lib.Value.path
        if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
    }
    if (-not $package) { throw "Missing restored package: $($lib.Name)" }
    $nuspec = Get-ChildItem $package -Filter *.nuspec -File | Select-Object -First 1
    if (-not $nuspec) { throw "Missing package metadata: $($lib.Name)" }
    $metadata = ([xml](Get-Content $nuspec.FullName -Raw)).package.metadata
    $index.Add('')
    $index.Add($lib.Name)
    foreach ($field in @('authors','copyright','license','licenseUrl','projectUrl')) {
        $value = $metadata.$field
        if ($value) { $index.Add(('{0}: {1}' -f $field, $(if ($value -is [System.Xml.XmlElement]) { $value.InnerText } else { [string]$value }))) }
    }
    if ($metadata.repository) { $index.Add(('source: {0} @ {1}' -f $metadata.repository.url, $metadata.repository.commit)) }
    $dest = Join-Path $out ($lib.Name.Replace('/', '-'))
    $files = @(Get-ChildItem $package -File | Where-Object { $_.Name -match 'licen[cs]e|notice|copying|copyright' })
    if ($metadata.license.type -eq 'file') {
        $specified = [IO.Path]::GetFullPath((Join-Path $package $metadata.license.InnerText))
        if (-not $specified.StartsWith($package.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package licence path.' }
        if (Test-Path -LiteralPath $specified) { $files += Get-Item -LiteralPath $specified }
    }
    if ($files.Count) {
        New-Item -ItemType Directory -Force $dest | Out-Null
        $files | Sort-Object FullName -Unique | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $dest }
    }
}
[IO.File]::WriteAllLines((Join-Path $out 'PACKAGE-NOTICES.txt'), $index)
Write-Host "Preserved notices for $(@($deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }).Count) runtime packages."
