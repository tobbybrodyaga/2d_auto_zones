param([string]$BuildDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$BuildDirectory) { $BuildDirectory = Join-Path $root 'src\LiraSlabZones.PreviewHost\bin\x64\Release\net48' }
$refs = @('Newtonsoft.Json.dll','Clipper2Lib.dll','LiraSlabZones.Core.dll') | ForEach-Object {
    $path = Join-Path $BuildDirectory $_
    Add-Type -Path $path
    $path
}
Add-Type -Path (Join-Path $PSScriptRoot 'ZoneBoundary.Regression.cs') -ReferencedAssemblies ($refs + @('System.Core.dll'))
[ZoneBoundaryRegression]::Run()
Write-Host 'PASS ZoneBoundary regression'
