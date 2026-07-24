# Install LiraSlabZones Revit 2023 add-in
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$dll = Get-ChildItem -LiteralPath (Join-Path $root 'src\LiraSlabZones.Revit2023\bin') -Recurse -Filter 'LiraSlabZones.Revit2023.dll' -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $dll) {
    Write-Host 'Build first: dotnet build LiraSlabZones.sln -c Debug -p:Platform=x64'
    exit 1
}

$src = $dll.DirectoryName
$addinDir = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2023'
$deploy = Join-Path $addinDir 'LiraSlabZones'
New-Item -ItemType Directory -Force -Path $deploy | Out-Null
Copy-Item -Path (Join-Path $src '*.dll') -Destination $deploy -Force

$targetDll = Join-Path $deploy 'LiraSlabZones.Revit2023.dll'
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>LiraSlabZones</Name>
    <Assembly>$targetDll</Assembly>
    <AddInId>B7E6C2A1-4F3D-4A9E-9C11-8D2A6F0E5B21</AddInId>
    <FullClassName>LiraSlabZones.Revit2023.App</FullClassName>
    <VendorId>SUM</VendorId>
    <VendorDescription>LIRA to Revit slab additional rebar zones</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
[System.IO.File]::WriteAllText((Join-Path $addinDir 'LiraSlabZones.addin'), $xml, [Text.UTF8Encoding]::new($false))
Write-Host "OK: $deploy"
Write-Host 'Restart Revit 2023.'
