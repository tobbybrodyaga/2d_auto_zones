param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
$refs=@('Newtonsoft.Json.dll','Clipper2Lib.dll','LiraSlabZones.Core.dll')|ForEach-Object {
    $path=Join-Path $BuildDirectory $_
    Add-Type -Path $path
    $path
}
Add-Type -Path (Join-Path $PSScriptRoot 'ZoneCompactness.Regression.cs') -ReferencedAssemblies ($refs+@('System.Core.dll'))
[ZoneCompactnessRegression]::Run()
