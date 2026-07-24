$ErrorActionPreference = "Stop"
$dll = Join-Path $PSScriptRoot "..\src\LiraSlabZones.Core\bin\x64\Debug\net48\LiraSlabZones.Core.dll"
Add-Type -Path (Resolve-Path $dll)

if (-not [LiraSlabZones.Core.UnitConversion]::RoundTripMmOk(11700.0)) {
    throw "Unit round-trip failed"
}

$s = New-Object LiraSlabZones.Core.AnalysisSettings
$s.AutoLayout = $true
$s.ShowAs1 = $true
$s.ShowAs2 = $true
$s.BarStepMm = 200
$s.ConcreteClass = "B25"
$s.GridCellMm = 300

$demo = [LiraSlabZones.Core.DemoSlabFactory]::Create($s)
$zones = $demo.Zones
if ($zones.Count -lt 1) { throw "No zones" }

$badW = @($zones | Where-Object { $_.WidthMm % $_.BarStepMm -ne 0 }).Count
$badDir = @(
    $zones | Where-Object {
        (($_.Layer -eq "As1" -or $_.Layer -eq "As3") -and $_.Direction -ne "X") -or
        (($_.Layer -eq "As2" -or $_.Layer -eq "As4") -and $_.Direction -ne "Y")
    }
).Count

$sum3 = [LiraSlabZones.Core.RebarTables]::Sum3FamilyLengthsMm
$badLen = @($zones | Where-Object { $sum3 -notcontains [int]$_.LengthMm }).Count

Write-Host "Zones=$($zones.Count) NonMultW=$badW BadDir=$badDir BadLen=$badLen"
Write-Host "OppRow1=$([LiraSlabZones.Core.HoleBentRules]::OppositeEdgeRow(1)) OppRow2=$([LiraSlabZones.Core.HoleBentRules]::OppositeEdgeRow(2))"
$zones | Group-Object FamilyKind | ForEach-Object { Write-Host ("  {0}={1}" -f $_.Name, $_.Count) }

$tied = @($zones | Where-Object { -not [string]::IsNullOrWhiteSpace($_.AxisTieLabel) }).Count
$badSnap = @($zones | Where-Object {
    ($_.OffsetFromAxisXMm % 10) -ne 0 -or ($_.OffsetFromAxisYMm % 10) -ne 0
}).Count
Write-Host "AxisTied=$tied BadSnap10=$badSnap Sample=$($zones[0].AxisTieLabel)"

if ($badW -ne 0 -or $badDir -ne 0 -or $badLen -ne 0 -or $badSnap -ne 0) {
    throw "Smoke checks failed"
}
Write-Host "OK"
