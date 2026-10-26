param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$source = Join-Path $root "src\LiraSlabZones.PreviewHost\bin\x64\$Configuration\net48"
$target = Join-Path $root 'dist\stage\tools'
$backup = Join-Path $root 'dist\backup'
$exe = Join-Path $source 'LiraSlabZones.PreviewHost.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "PreviewHost build not found: $exe"
}

New-Item -ItemType Directory -Force -Path $target, $backup | Out-Null
if (Test-Path -LiteralPath (Join-Path $target 'LiraSlabZones.PreviewHost.exe')) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $archive = Join-Path $backup "LiraSlabZones.PreviewHost-$stamp.zip"
    Compress-Archive -Path (Join-Path $target '*') -DestinationPath $archive -CompressionLevel Optimal
    Write-Host "Backup: $archive"
}

Get-ChildItem -LiteralPath $target -File | Remove-Item -Force
Copy-Item -LiteralPath $exe -Destination $target -Force
Copy-Item -Path (Join-Path $source '*.dll') -Destination $target -Force
$settings = Join-Path $root 'DefaultSettings.cfg'
if (Test-Path -LiteralPath $settings) {
    Copy-Item -LiteralPath $settings -Destination $target -Force
}

$legacyExe = Join-Path $root 'dist\stage\LiraSlabZones.PreviewHost.exe'
if (Test-Path -LiteralPath $legacyExe) {
    Remove-Item -LiteralPath $legacyExe -Force
}

$sourceHash = (Get-FileHash -LiteralPath $exe).Hash
$targetHash = (Get-FileHash -LiteralPath (Join-Path $target 'LiraSlabZones.PreviewHost.exe')).Hash
if ($sourceHash -ne $targetHash) { throw 'Published PreviewHost hash mismatch.' }

Write-Host "Published: $(Join-Path $target 'LiraSlabZones.PreviewHost.exe')"
