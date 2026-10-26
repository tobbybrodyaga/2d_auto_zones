param(
    [string]$InputJson,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$build = Join-Path $root "src\LiraSlabZones.PreviewHost\bin\x64\$Configuration\net48"
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
Add-Type -Path (Join-Path $build 'Newtonsoft.Json.dll')
Add-Type -Path (Join-Path $build 'Clipper2Lib.dll')
Add-Type -Path (Join-Path $build 'LiraSlabZones.Core.dll')
[void][Reflection.Assembly]::LoadFrom((Join-Path $build 'LiraSlabZones.PreviewHost.exe'))

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function New-GapZone([double]$minY, [double]$maxY, [int]$step, [double]$capacity) {
    $zone = [LiraSlabZones.Core.AdditionalZone]::new()
    $zone.Layer = [LiraSlabZones.Core.RebarLayer]::As2
    $zone.Direction = [LiraSlabZones.Core.ZoneDirection]::X
    $zone.BarStepMm = $step
    $zone.AsCoveredCm2PerM = $capacity
    $zone.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new(0, $minY, 0))
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new(4, $minY, 0))
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new(4, $maxY, 0))
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new(0, $maxY, 0))
    return $zone
}

function New-RectZone([double]$minX, [double]$maxX, [double]$minY, [double]$maxY,
    [int]$step, [double]$capacity) {
    $zone = [LiraSlabZones.Core.AdditionalZone]::new()
    $zone.Layer = [LiraSlabZones.Core.RebarLayer]::As2
    $zone.Direction = [LiraSlabZones.Core.ZoneDirection]::X
    $zone.BarStepMm = $step
    $zone.AsCoveredCm2PerM = $capacity
    $zone.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new($minX, $minY, 0))
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new($maxX, $minY, 0))
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new($maxX, $maxY, 0))
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new($minX, $maxY, 0))
    return $zone
}

$gapZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$gapZones.Add((New-GapZone 0 1 200 5))
$gapZones.Add((New-GapZone 1.2 2.2 200 3))
$gapPoint = [LiraSlabZones.Core.Point3]::new(2, 1.1, 0)
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap($gapZones, $gapPoint, 3)) `
    'A 200 mm gap between zones with 200 mm spacing was reported uncovered.'
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap($gapZones, $gapPoint, 3.01)) `
    'Gap capacity must equal the weaker adjacent zone.'
for ($i = 0; $i -lt $gapZones[1].Contour.Count; $i++) {
    $point = $gapZones[1].Contour[$i]
    $gapZones[1].Contour[$i] = [LiraSlabZones.Core.Point3]::new($point.X, $point.Y + 0.001, $point.Z)
}
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap($gapZones, $gapPoint, 0)) `
    'A gap larger than the smaller zone spacing was reported covered.'
for ($i = 0; $i -lt $gapZones[1].Contour.Count; $i++) {
    $point = $gapZones[1].Contour[$i]
    $gapZones[1].Contour[$i] = [LiraSlabZones.Core.Point3]::new($point.X, $point.Y - 0.101, $point.Z)
}
$gapZones[0].BarStepMm = 100
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap($gapZones, $gapPoint, 3)) `
    'A 100 mm gap between 100/200 mm zones was reported uncovered.'
Write-Host 'PASS gaps up to the smaller zone spacing use the weaker adjacent capacity'

function New-ContourPlate([int]$id, [double]$minX, [double]$maxX, [double]$minY, [double]$maxY) {
    $plate = [LiraSlabZones.Core.LiraPlateElement]::new()
    $plate.Id = $id
    $plate.Centroid = [LiraSlabZones.Core.Point3]::new(($minX + $maxX) / 2, ($minY + $maxY) / 2, 0)
    $plate.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($minX, $minY, 0))
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($maxX, $minY, 0))
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($maxX, $maxY, 0))
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($minX, $maxY, 0))
    return $plate
}

$strictGapZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$strictGapZones.Add((New-GapZone 0 1 200 5))
$strictGapZones.Add((New-GapZone 1.2 2.2 200 3))
$bridgePlate = New-ContourPlate 590 1 3 1.02 1.18
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $strictGapZones, $bridgePlate, [LiraSlabZones.Core.RebarLayer]::As2, 3)) `
    'An FE fully contained in the single allowed gap between two zones was reported uncovered.'
$edgePlate = New-ContourPlate 591 3.95 4.05 0.2 0.8
$singleEdgeZone = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$singleEdgeZone.Add((New-GapZone 0 1 200 5))
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $singleEdgeZone, $edgePlate, [LiraSlabZones.Core.RebarLayer]::As2, 3)) `
    'A partial FE crossing a single zone edge was incorrectly reported fully covered.'
$outsidePairPlate = New-ContourPlate 592 3.95 4.05 1.02 1.18
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $strictGapZones, $outsidePairPlate, [LiraSlabZones.Core.RebarLayer]::As2, 3)) `
    'The step-gap exception covered an FE portion beyond the combined extent of its two zones.'
$overlapZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$overlapZones.Add((New-RectZone 0 2 0 2 200 5))
$overlapZones.Add((New-RectZone 1.9 4 0 2 200 5))
$overlapPlate = New-ContourPlate 594 1.5 2.5 0.5 1.5
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $overlapZones, $overlapPlate, [LiraSlabZones.Core.RebarLayer]::As2, 3)) `
    'Overlapping zones were incorrectly treated as the permitted step-gap exception.'
 $boundaryZone = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
 $boundaryZone.Add((New-GapZone 0 1 200 5))
 $boundaryPlate = New-ContourPlate 593 1 3 -0.05 0.5
 $slabOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
 foreach ($point in @(@(0,0),@(4,0),@(4,2),@(0,2))) {
    $slabOutline.Add([LiraSlabZones.Core.Point3]::new($point[0],$point[1],0))
 }
 Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $boundaryZone, $boundaryPlate, [LiraSlabZones.Core.RebarLayer]::As2, 3, $slabOutline)) `
    'A zone covering the full in-slab part of a boundary FE was reported uncovered.'
Write-Host 'PASS full FE geometry is required for direct and two-zone gap coverage'

$holePlate = New-ContourPlate 595 1.0 1.4 0.3 0.7
$holePlate.Rebar.Ok = $true
$holePlate.Rebar.As2 = 5
$holeZone = New-RectZone 1.05 1.4 0.3 0.7 200 5
$holeZoneList = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$holeZoneList.Add($holeZone)
$hole = [LiraSlabZones.Core.OpeningInfo]::new()
$hole.MinXM = 0.5
$hole.MaxXM = 1.0
$hole.MinYM = 0.3
$hole.MaxYM = 0.7
$holes = [Collections.Generic.List[LiraSlabZones.Core.OpeningInfo]]::new()
$holes.Add($hole)
$holeOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
foreach ($xy in @(@(0,0), @(2,0), @(2,1), @(0,1))) {
    $holeOutline.Add([LiraSlabZones.Core.Point3]::new($xy[0], $xy[1], 0))
}
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $holeZoneList, $holePlate, [LiraSlabZones.Core.RebarLayer]::As2, 4, $holeOutline)) `
    'A 50 mm edge allowance was applied without passing detected openings.'
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $holeZoneList, $holePlate, [LiraSlabZones.Core.RebarLayer]::As2, 4, $holeOutline, $holes)) `
    'A plate edge 50 mm from an opening was reported uncovered.'
$over100Zone = New-RectZone 1.1002 1.4 0.3 0.7 200 5
$over100Zones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$over100Zones.Add($over100Zone)
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $over100Zones, $holePlate, [LiraSlabZones.Core.RebarLayer]::As2, 4, $holeOutline, $holes)) `
    'The opening allowance accepted a zone more than 100 mm away.'
$shortHoleZone = New-RectZone 1.05 1.35 0.3 0.7 200 5
$shortHoleZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$shortHoleZones.Add($shortHoleZone)
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $shortHoleZones, $holePlate, [LiraSlabZones.Core.RebarLayer]::As2, 4, $holeOutline, $holes)) `
    'The opening allowance hid a separate uncovered strip away from the opening.'
$emptyNearHoleZone = New-RectZone 0.55 0.9 0.3 0.7 200 5
$emptyNearHoleZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$emptyNearHoleZones.Add($emptyNearHoleZone)
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $emptyNearHoleZones, $holePlate, [LiraSlabZones.Core.RebarLayer]::As2, 4, $holeOutline, $holes)) `
    'A nearby zone with no actual overlap was allowed to cover an FE.'
Write-Host 'PASS 100 mm opening allowance covers only the FE portion beside a real opening'

$splitSource = [LiraSlabZones.Core.AdditionalZone]::new()
$splitSource.Layer = [LiraSlabZones.Core.RebarLayer]::As2
$splitSource.NodeIds.Add(501)
$splitPlate = [LiraSlabZones.Core.LiraPlateElement]::new()
$splitPlate.Id = 501
$splitPlate.Centroid = [LiraSlabZones.Core.Point3]::new(2, 1.1, 0)
$splitPlate.Rebar.Ok = $true
$splitPlate.Rebar.As2 = 3
$splitPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$splitPlates.Add($splitPlate)
$splitZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$splitZones.Add((New-GapZone 0 1 200 5))
$splitZones.Add((New-GapZone 1.2 2.2 200 3))
$splitPlateMap = [Collections.Generic.Dictionary[int,LiraSlabZones.Core.LiraPlateElement]]::new()
$splitPlateMap.Add($splitPlate.Id, $splitPlate)
$findUncoveredSplitElements = [LiraSlabZones.Core.SlabZoneAnalyzer].GetMethod(
    'FindUncoveredZoneElements', [Reflection.BindingFlags]'NonPublic,Static')
$splitMissing = $findUncoveredSplitElements.Invoke(
    $null, [object[]]@($splitSource, $splitZones, $splitPlateMap,
        [LiraSlabZones.Core.AnalysisSettings]::new(), [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new(),
        [Collections.Generic.List[LiraSlabZones.Core.OpeningInfo]]::new()))
Assert ($splitMissing.Count -eq 0) 'Opening split rejected an FE covered by the permitted 200 mm gap.'
for ($i = 0; $i -lt $splitZones[1].Contour.Count; $i++) {
    $point = $splitZones[1].Contour[$i]
    $splitZones[1].Contour[$i] = [LiraSlabZones.Core.Point3]::new($point.X, $point.Y + 0.001, $point.Z)
}
$splitMissing = $findUncoveredSplitElements.Invoke(
    $null, [object[]]@($splitSource, $splitZones, $splitPlateMap,
        [LiraSlabZones.Core.AnalysisSettings]::new(), [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new(),
        [Collections.Generic.List[LiraSlabZones.Core.OpeningInfo]]::new()))
Assert ($splitMissing.Count -eq 1 -and $splitMissing[0] -eq 501) `
    'Opening split accepted an FE in a gap larger than the configured spacing.'
Write-Host 'PASS opening split preserves FE coverage across allowed gaps only'

function New-TestPlate([int]$id, [double]$y, [double]$as2) {
    $plate = [LiraSlabZones.Core.LiraPlateElement]::new()
    $plate.Id = $id
    $plate.Centroid = [LiraSlabZones.Core.Point3]::new(2, $y, 0)
    $plate.Rebar.Ok = $true
    $plate.Rebar.As2 = $as2
    return $plate
}

$gapRepairSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$gapRepairSettings.AsMainAs2 = 0
$edgeRepairZone = New-GapZone 0 1 200 5
$edgeRepairZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$edgeRepairZones.Add($edgeRepairZone)
$cornerEdgePlate = New-ContourPlate 598 -0.05 0.05 -0.05 0.05
$cornerEdgePlate.Rebar.Ok = $true
$cornerEdgePlate.Rebar.As2 = 3
$oppositeEdgePlate = New-ContourPlate 599 -0.05 0.05 0.2 0.8
$oppositeEdgePlate.Rebar.Ok = $true
$oppositeEdgePlate.Rebar.As2 = 3
$edgeRepairPlate = New-ContourPlate 600 3.95 4.05 0.2 0.8
$edgeRepairPlate.Rebar.Ok = $true
$edgeRepairPlate.Rebar.As2 = 3
$edgeRepairPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$edgeRepairPlates.Add($cornerEdgePlate)
$edgeRepairPlates.Add($oppositeEdgePlate)
$edgeRepairPlates.Add($edgeRepairPlate)
[LiraSlabZones.Core.ZoneEditor]::CloseUncoveredStepGaps(
    $edgeRepairZones, $edgeRepairPlates, $gapRepairSettings,
    [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new(),
    [Collections.Generic.List[LiraSlabZones.Core.OpeningInfo]]::new())
$edgeRepairFailures = @($edgeRepairPlates | Where-Object {
    -not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $edgeRepairZones, $_, [LiraSlabZones.Core.RebarLayer]::As2, 3)
})
Assert ($edgeRepairFailures.Count -eq 0) `
    'The repair pass did not extend the zone to cover the full footprints of its partially covered edge FEs.'
Write-Host 'PASS partial edge FE is repaired by extending the zone in whole spacing modules'

$boundarySettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$boundarySettings.AsMainAs2 = 0
$boundarySettings.ReverseZoneDirections = $true
$weakBoundaryZone = New-RectZone 0 4 0 1 100 2
$strongBoundaryZone = New-RectZone 0 4 1.1 2.1 200 5
$weakBoundaryZone.FamilyKind = [LiraSlabZones.Core.ZoneFamilyKind]::L
$weakBoundaryZone.VerticalLegMm = 200
$strongBoundaryZone.FamilyKind = [LiraSlabZones.Core.ZoneFamilyKind]::BentStick
$strongBoundaryZone.VerticalLegMm = 300
$boundaryZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$boundaryZones.Add($weakBoundaryZone)
$boundaryZones.Add($strongBoundaryZone)
$boundaryTarget = New-ContourPlate 604 1.5 2.5 0.8 1.2
$boundaryTarget.Rebar.Ok = $true
$boundaryTarget.Rebar.As2 = 3
$weakPreservePlate = New-ContourPlate 605 1.5 2.5 0.1 0.3
$weakPreservePlate.Rebar.Ok = $true
$weakPreservePlate.Rebar.As2 = 2
$strongPreservePlate = New-ContourPlate 606 1.5 2.5 1.5 1.7
$strongPreservePlate.Rebar.Ok = $true
$strongPreservePlate.Rebar.As2 = 4
$weakBoundaryZone.NodeIds.Add($weakPreservePlate.Id)
$strongBoundaryZone.NodeIds.Add($strongPreservePlate.Id)
$boundaryPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$boundaryPlates.Add($boundaryTarget)
$boundaryPlates.Add($weakPreservePlate)
$boundaryPlates.Add($strongPreservePlate)
[LiraSlabZones.Core.ZoneEditor]::CloseUncoveredStepGaps(
    $boundaryZones, $boundaryPlates, $boundarySettings,
    [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new(),
    [Collections.Generic.List[LiraSlabZones.Core.OpeningInfo]]::new())
$boundaryGap = ($strongBoundaryZone.Contour.Y | Measure-Object -Minimum).Minimum -
    ($weakBoundaryZone.Contour.Y | Measure-Object -Maximum).Maximum
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $boundaryZones, $boundaryTarget, [LiraSlabZones.Core.RebarLayer]::As2, 3)) `
    'The stronger neighboring zone did not take full coverage of a FE crossing the capacity boundary.'
Assert ([Math]::Abs($boundaryGap - 0.1) -le 0.001) `
    'Moving the shared zone boundary did not preserve the 100 mm separation.'
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $boundaryZones, $weakPreservePlate, [LiraSlabZones.Core.RebarLayer]::As2, 2) -and
    [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $boundaryZones, $strongPreservePlate, [LiraSlabZones.Core.RebarLayer]::As2, 4)) `
    'Moving the shared boundary lost coverage of a previously covered FE.'
Assert ([Math]::Abs($weakBoundaryZone.WidthMm / $weakBoundaryZone.BarStepMm -
        [Math]::Round($weakBoundaryZone.WidthMm / $weakBoundaryZone.BarStepMm)) -lt 0.001 -and
    [Math]::Abs($strongBoundaryZone.WidthMm / $strongBoundaryZone.BarStepMm -
        [Math]::Round($strongBoundaryZone.WidthMm / $strongBoundaryZone.BarStepMm)) -lt 0.001) `
    'Moving the shared boundary made a zone width non-integral in bar spacing.'
Assert ($weakBoundaryZone.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::L -and
    $weakBoundaryZone.VerticalLegMm -eq 200 -and
    $strongBoundaryZone.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::BentStick -and
    $strongBoundaryZone.VerticalLegMm -eq 300) `
    'Moving the shared boundary changed the zones bent families or leg lengths.'
Write-Host 'PASS adjacent capacity boundary shifts by whole bar modules and keeps both zones covered'

$repairZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$repairZones.Add((New-GapZone 0 1 200 5))
$repairZones.Add((New-GapZone 1.4 2.4 200 3))
$repairPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$repairPlates.Add((New-TestPlate 601 0.5 3))
$repairPlates.Add((New-TestPlate 602 1.2 3))
$repairPlates.Add((New-TestPlate 603 1.9 3))
[LiraSlabZones.Core.ZoneEditor]::CloseUncoveredStepGaps(
    $repairZones, $repairPlates, $gapRepairSettings,
    [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new(),
    [Collections.Generic.List[LiraSlabZones.Core.OpeningInfo]]::new())
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap($repairZones, $repairPlates[1].Centroid, 3)) `
    'The automatic layout did not close a short uncovered FE gap.'
$repairedGap = ($repairZones[1].Contour.Y | Measure-Object -Minimum).Minimum -
    ($repairZones[0].Contour.Y | Measure-Object -Maximum).Maximum
Assert ([Math]::Abs($repairedGap - 0.2) -le 0.001) 'The repaired zone gap is not equal to the 200 mm spacing.'
Assert (-not [LiraSlabZones.Core.ZoneEditor]::HasPlacementConflict($repairZones[0], $repairZones[1])) `
    'The repaired zones still violate the minimum spacing.'
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap($repairZones, $repairPlates[0].Centroid, 3) -and
    [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap($repairZones, $repairPlates[2].Centroid, 3)) `
    'Closing the gap lost coverage of an existing FE.'
Write-Host 'PASS automatic layout closes uncovered gaps without losing existing FE coverage'

$endGapZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$endGapLower = New-GapZone 0 1 100 5
$endGapUpper = New-GapZone 0 1 200 3
$endGapLower.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$endGapLower.Contour.Add([LiraSlabZones.Core.Point3]::new(0, 0, 0))
$endGapLower.Contour.Add([LiraSlabZones.Core.Point3]::new(1, 0, 0))
$endGapLower.Contour.Add([LiraSlabZones.Core.Point3]::new(1, 4, 0))
$endGapLower.Contour.Add([LiraSlabZones.Core.Point3]::new(0, 4, 0))
$endGapUpper.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$endGapUpper.Contour.Add([LiraSlabZones.Core.Point3]::new(1.1, 0, 0))
$endGapUpper.Contour.Add([LiraSlabZones.Core.Point3]::new(2.1, 0, 0))
$endGapUpper.Contour.Add([LiraSlabZones.Core.Point3]::new(2.1, 4, 0))
$endGapUpper.Contour.Add([LiraSlabZones.Core.Point3]::new(1.1, 4, 0))
$endGapZones.Add($endGapLower)
$endGapZones.Add($endGapUpper)
Assert (-not [LiraSlabZones.Core.ZoneEditor]::HasPlacementConflict($endGapLower, $endGapUpper)) `
    'A longitudinal butt joint was incorrectly checked as transverse bar-array spacing.'
$endGapPoint = [LiraSlabZones.Core.Point3]::new(1.05, 2, 0)
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap($endGapZones, $endGapPoint, 3)) `
    'A 100 mm end-to-end gap was not covered at the smaller adjacent capacity.'
$endGapUpper.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$endGapUpper.Contour.Add([LiraSlabZones.Core.Point3]::new(1.101, 0, 0))
$endGapUpper.Contour.Add([LiraSlabZones.Core.Point3]::new(2.101, 0, 0))
$endGapUpper.Contour.Add([LiraSlabZones.Core.Point3]::new(2.101, 4, 0))
$endGapUpper.Contour.Add([LiraSlabZones.Core.Point3]::new(1.101, 4, 0))
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap($endGapZones, $endGapPoint, 0)) `
    'An end-to-end gap larger than the smaller spacing was reported covered.'
Write-Host 'PASS end-to-end gaps use the smaller adjacent bar capacity only within one step'

$spacingLower = New-GapZone 0 1 100 5
$spacingUpper = New-GapZone 1.1 2.1 200 3
Assert (-not [LiraSlabZones.Core.ZoneEditor]::HasPlacementConflict($spacingLower, $spacingUpper)) `
    'Zones separated by the smaller adjacent step were reported in conflict.'
for ($i = 0; $i -lt $spacingUpper.Contour.Count; $i++) {
    $point = $spacingUpper.Contour[$i]
    $spacingUpper.Contour[$i] = [LiraSlabZones.Core.Point3]::new($point.X, $point.Y - 0.001, $point.Z)
}
Assert ([LiraSlabZones.Core.ZoneEditor]::HasPlacementConflict($spacingLower, $spacingUpper)) `
    'Zones separated by less than the smaller adjacent step were not reported in conflict.'
Write-Host 'PASS transverse zone separation equals the smaller adjacent spacing'

$wideGapSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$wideGapSettings.GridCellMm = 400
$wideGapA = New-RectZone 0 4 0 1 100 2
$wideGapB = New-RectZone 0 4 1.4 2.4 200 4
$wideGapZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$wideGapZones.Add($wideGapA)
$wideGapZones.Add($wideGapB)
$emptyGapPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
[LiraSlabZones.Core.ZoneEditor]::EnforceRequiredGaps(
    $wideGapZones, $emptyGapPlates, $wideGapSettings, $null, $null)
$actualWideGap = ($wideGapB.Contour | ForEach-Object { $_.Y } | Measure-Object -Minimum).Minimum -
    ($wideGapA.Contour | ForEach-Object { $_.Y } | Measure-Object -Maximum).Maximum
Assert ([Math]::Abs($actualWideGap - 0.1) -lt 1e-6) `
    "Excessive 400 mm gap was not closed to the smaller 100 mm spacing; got $($actualWideGap * 1000) mm."
Write-Host 'PASS nearby excessive gaps are closed to the exact smaller bar spacing'

function New-StepGapPlate([int]$id, [double]$minY, [double]$maxY, [double]$as2) {
    $plate = [LiraSlabZones.Core.LiraPlateElement]::new()
    $plate.Id = $id
    $plate.Centroid = [LiraSlabZones.Core.Point3]::new(2, ($minY + $maxY) / 2, 0)
    $plate.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new(0, $minY, 0))
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new(4, $minY, 0))
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new(4, $maxY, 0))
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new(0, $maxY, 0))
    $plate.Rebar.Ok = $true
    $plate.Rebar.As1 = 0
    $plate.Rebar.As2 = $as2
    return $plate
}

$anchoredGapA = New-RectZone 0 4 0 1 100 5
$anchoredGapB = New-RectZone 0 4 1.4 2.4 100 10
$anchoredGapA.NodeIds.Add(801)
$anchoredGapB.NodeIds.Add(802)
$anchorA = New-StepGapPlate 801 0 0.4 4
$anchorB = New-StepGapPlate 802 2 2.4 4
$gapElement = New-StepGapPlate 803 1 1.4 3
$anchoredGapPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$anchoredGapPlates.Add($anchorA)
$anchoredGapPlates.Add($anchorB)
$anchoredGapPlates.Add($gapElement)
$anchoredGapSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$anchoredGapSettings.GridCellMm = 400
$anchoredGapZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$anchoredGapZones.Add($anchoredGapA)
$anchoredGapZones.Add($anchoredGapB)
[LiraSlabZones.Core.ZoneEditor]::EnforceRequiredGaps(
    $anchoredGapZones, $anchoredGapPlates, $anchoredGapSettings, $null, $null)
$anchoredGap = ($anchoredGapB.Contour | ForEach-Object { $_.Y } | Measure-Object -Minimum).Minimum -
    ($anchoredGapA.Contour | ForEach-Object { $_.Y } | Measure-Object -Maximum).Maximum
Assert ([Math]::Abs($anchoredGap - 0.1) -lt 1e-6 -and
        [Math]::Abs($anchoredGapA.WidthMm / $anchoredGapA.BarStepMm -
                    [Math]::Round($anchoredGapA.WidthMm / $anchoredGapA.BarStepMm)) -lt 1e-9 -and
        [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
            $anchoredGapZones, $gapElement, [LiraSlabZones.Core.RebarLayer]::As2, 3)) `
    'An exact gap could not be achieved by step-rounded widening while preserving anchored FE and weak-capacity bridge coverage.'
Write-Host 'PASS anchored adjacent zones widen by whole steps and bridge gap FE at weaker capacity'

$partitionMethod = [LiraSlabZones.Core.ZoneLayoutEngine].GetMethod(
    'PartitionInvalidOverlaps', [Reflection.BindingFlags]'NonPublic,Static')
$partitionMosaic = [LiraSlabZones.Core.MosaicGrid]::new()
$partitionMosaic.PlateCentroids.Add(701, [LiraSlabZones.Core.Point3]::new(2, 1, 0))
$partitionZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$partitionLower = New-GapZone 0 1.4 200 5
$partitionUpper = New-GapZone 0.6 2 100 3
$partitionLower.NodeIds.Add(701)
$partitionUpper.NodeIds.Add(701)
$partitionZones.Add($partitionLower)
$partitionZones.Add($partitionUpper)
[void]$partitionMethod.Invoke($null, [object[]]@($partitionZones, $partitionMosaic, $null, $true))
$partitionLowerMax = ($partitionLower.Contour.Y | Measure-Object -Maximum).Maximum
$partitionUpperMin = ($partitionUpper.Contour.Y | Measure-Object -Minimum).Minimum
Assert (-not [LiraSlabZones.Core.ZoneEditor]::HasPlacementConflict($partitionLower, $partitionUpper)) `
    'Final overlap partition left two transverse zones in conflict.'
Assert ([Math]::Abs(($partitionUpperMin - $partitionLowerMax) - 0.1) -le 0.001) `
    'Final overlap partition did not create the smaller adjacent step as a transverse gap.'
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $partitionZones, [LiraSlabZones.Core.Point3]::new(2, 1, 0), 3)) `
    'Final overlap partition lost the common FE in its permitted weaker-capacity gap.'

$partitionEndZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$partitionEndLeft = New-GapZone 0 4 100 5
$partitionEndRight = New-GapZone 0 4 200 3
$partitionEndLeft.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$partitionEndLeft.Contour.Add([LiraSlabZones.Core.Point3]::new(0, 0, 0))
$partitionEndLeft.Contour.Add([LiraSlabZones.Core.Point3]::new(1, 0, 0))
$partitionEndLeft.Contour.Add([LiraSlabZones.Core.Point3]::new(1, 4, 0))
$partitionEndLeft.Contour.Add([LiraSlabZones.Core.Point3]::new(0, 4, 0))
$partitionEndRight.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$partitionEndRight.Contour.Add([LiraSlabZones.Core.Point3]::new(1, 0, 0))
$partitionEndRight.Contour.Add([LiraSlabZones.Core.Point3]::new(2, 0, 0))
$partitionEndRight.Contour.Add([LiraSlabZones.Core.Point3]::new(2, 4, 0))
$partitionEndRight.Contour.Add([LiraSlabZones.Core.Point3]::new(1, 4, 0))
$partitionEndLeft.NodeIds.Add(702)
$partitionEndRight.NodeIds.Add(702)
$partitionEndZones.Add($partitionEndLeft)
$partitionEndZones.Add($partitionEndRight)
$endPartitionMosaic = [LiraSlabZones.Core.MosaicGrid]::new()
$endPartitionMosaic.PlateCentroids.Add(702, [LiraSlabZones.Core.Point3]::new(1, 2, 0))
[void]$partitionMethod.Invoke($null, [object[]]@($partitionEndZones, $endPartitionMosaic, $null, $true))
$partitionEndGap = ($partitionEndRight.Contour.X | Measure-Object -Minimum).Minimum -
    ($partitionEndLeft.Contour.X | Measure-Object -Maximum).Maximum
Assert (-not [LiraSlabZones.Core.ZoneEditor]::HasPlacementConflict($partitionEndLeft, $partitionEndRight)) `
    'Final overlap partition left two end-to-end zones in conflict.'
Assert ([Math]::Abs($partitionEndGap - 0.1) -le 0.001) `
    'Final overlap partition did not create the smaller adjacent step as an end gap.'
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $partitionEndZones, [LiraSlabZones.Core.Point3]::new(1, 2, 0), 3)) `
    'Final end-gap partition lost the common FE in its permitted weaker-capacity gap.'
Write-Host 'PASS final overlap partition applies exact minimum spacing and preserves bridged FE'

$arrayWidthZone = New-GapZone 0 0.95 200 5
$arrayWidthZone.BarCount = 4
$arrayWidthZone.WidthMm = 650
$arrayWidthZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$arrayWidthZones.Add($arrayWidthZone)
[LiraSlabZones.Core.ZoneEditor]::NormalizeBarArrayWidthsToStep($arrayWidthZones)
Assert ($arrayWidthZone.BarCount -eq 6 -and $arrayWidthZone.WidthMm -eq 1000) `
    'The bar array width did not round up to a step multiple that covers the full zone.'
Write-Host 'PASS family width equals (bar count - 1) x spacing'

$blockedRepairZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$blockedRepairZones.Add((New-GapZone 0 1 200 5))
$blockedRepairZones.Add((New-GapZone 1.4 2.4 200 3))
$opening = [LiraSlabZones.Core.OpeningInfo]::new()
$opening.MinXM = 1; $opening.MaxXM = 3; $opening.MinYM = 1.1; $opening.MaxYM = 1.3
$blockedOpenings = [Collections.Generic.List[LiraSlabZones.Core.OpeningInfo]]::new()
$blockedOpenings.Add($opening)
[LiraSlabZones.Core.ZoneEditor]::CloseUncoveredStepGaps(
    $blockedRepairZones, $repairPlates, $gapRepairSettings,
    [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new(), $blockedOpenings)
Assert ([Math]::Abs((($blockedRepairZones[1].Contour.Y | Measure-Object -Minimum).Minimum) - 1.4) -le 1e-6) `
    'A gap repair moved a zone into an opening.'
Write-Host 'PASS automatic gap repair does not cross slab openings'

function Assert-ZoneRules($zones, [int]$backgroundDiameter) {
    foreach ($zone in $zones) {
        Assert ($zone.DiameterMm -ge $backgroundDiameter) "Zone $($zone.ZoneId) diameter is below background."
        Assert ($zone.BarStepMm -in @(100, 200)) "Zone $($zone.ZoneId) has unsupported spacing."
        $ruleX = $zone.Contour.X | Measure-Object -Minimum -Maximum
        $ruleY = $zone.Contour.Y | Measure-Object -Minimum -Maximum
        Assert ($zone.LengthMm -le 11701) `
            "Zone $($zone.ZoneId) ($($zone.Layer)/$($zone.Direction)/$($zone.FamilyKind)) detail length exceeds 11700 mm: $($zone.LengthMm); X=$($ruleX.Minimum)..$($ruleX.Maximum); Y=$($ruleY.Minimum)..$($ruleY.Maximum); $($zone.Comment)"
        $actualLengthMm = if ($zone.Direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
            (($zone.Contour.X | Measure-Object -Maximum).Maximum - ($zone.Contour.X | Measure-Object -Minimum).Minimum) * 1000
        } else {
            (($zone.Contour.Y | Measure-Object -Maximum).Maximum - ($zone.Contour.Y | Measure-Object -Minimum).Minimum) * 1000
        }
        if ($zone.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::Straight) {
            Assert ([Math]::Abs($actualLengthMm - $zone.LengthMm) -le 1.1) "Straight zone $($zone.ZoneId) label length differs from its contour."
        } else {
            $expectedBent = [LiraSlabZones.Core.RebarTables]::BentBarTotalLengthMm(
                $actualLengthMm, $zone.VerticalLegMm, $zone.FamilyKind)
            Assert ([Math]::Abs($expectedBent - $zone.LengthMm) -le 1.1) "Bent zone $($zone.ZoneId) total length is wrong."
        }
    }
    for ($i = 0; $i -lt $zones.Count; $i++) {
        for ($j = $i + 1; $j -lt $zones.Count; $j++) {
            if ($zones[$i].Layer -ne $zones[$j].Layer) { continue }
            $a = $zones[$i].Contour
            $b = $zones[$j].Contour
            $overlapX = [Math]::Min(($a.X | Measure-Object -Maximum).Maximum, ($b.X | Measure-Object -Maximum).Maximum) -
                [Math]::Max(($a.X | Measure-Object -Minimum).Minimum, ($b.X | Measure-Object -Minimum).Minimum)
            $overlapY = [Math]::Min(($a.Y | Measure-Object -Maximum).Maximum, ($b.Y | Measure-Object -Maximum).Maximum) -
                [Math]::Max(($a.Y | Measure-Object -Minimum).Minimum, ($b.Y | Measure-Object -Minimum).Minimum)
            if ($overlapX -gt 0.000001 -and $overlapY -gt 0.000001) {
                $allowedMm = [LiraSlabZones.Core.RebarTables]::AllowedZoneOverlapMm($zones[$i], $zones[$j])
                $longOverlapMm = if ($zones[$i].Direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
                    $overlapX * 1000
                } else {
                    $overlapY * 1000
                }
                Assert ($allowedMm -gt 0 -and ($longOverlapMm + 1) -ge $allowedMm) "Zones $($zones[$i].ZoneId) and $($zones[$j].ZoneId) have insufficient lap overlap: $([Math]::Round($longOverlapMm))/$allowedMm mm, lengths $($zones[$i].LengthMm)/$($zones[$j].LengthMm), placements $($zones[$i].Placement.X),$($zones[$i].Placement.Y) / $($zones[$j].Placement.X),$($zones[$j].Placement.Y), FE $($zones[$i].NodeIds -join ',') / $($zones[$j].NodeIds -join ',')."
                continue
            }
            $gapX = if ($overlapX -lt 0) { -1.0 * $overlapX } else { 0.0 }
            $gapY = if ($overlapY -lt 0) { -1.0 * $overlapY } else { 0.0 }
            $requiredGap = [Math]::Min($zones[$i].BarStepMm, $zones[$j].BarStepMm) / 1000.0
            $tooClose = if ($zones[$i].Direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
                $overlapX -gt 0.000001 -and $gapY -lt ($requiredGap - 0.000001)
            } else {
                $overlapY -gt 0.000001 -and $gapX -lt ($requiredGap - 0.000001)
            }
            Assert (-not $tooClose) "Zones $($zones[$i].ZoneId) and $($zones[$j].ZoneId) violate the $($requiredGap * 1000) mm gap: overlap=($overlapX,$overlapY), gap=($gapX,$gapY), steps=$($zones[$i].BarStepMm)/$($zones[$j].BarStepMm)."
        }
    }
}

function Assert-AutoZoneArrayWidths($zones) {
    foreach ($zone in $zones) {
        $expectedWidth = ($zone.BarCount - 1) * $zone.BarStepMm
        Assert ([Math]::Abs($zone.WidthMm - $expectedWidth) -le 0.01) `
            "Zone $($zone.ZoneId) family width $($zone.WidthMm) is not its $($zone.BarCount) bars at $($zone.BarStepMm) mm spacing."
        $crossWidthM = if ($zone.Direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
            ($zone.Contour.Y | Measure-Object -Maximum).Maximum -
                ($zone.Contour.Y | Measure-Object -Minimum).Minimum
        } else {
            ($zone.Contour.X | Measure-Object -Maximum).Maximum -
                ($zone.Contour.X | Measure-Object -Minimum).Minimum
        }
        Assert ($zone.WidthMm + 1 -ge $crossWidthM * 1000) `
            "Zone $($zone.ZoneId) bar array is narrower than its contour."
    }
}

$step200 = [LiraSlabZones.Core.BarCapacity]::SelectDiameterAndStep(11.0, 36, 12, $false)
$excluded = [LiraSlabZones.Core.BarCapacity]::SelectDiameterAndStep(11.0, 36, 12, $false, [int[]]@(16))
Assert ($excluded.DiameterMm -ne 16) 'Excluded diameter was selected.'
$mixed = [LiraSlabZones.Core.BarCapacity]::SelectDiameterAndStep(11.0, 36, 12, $true)
$mixed200 = [LiraSlabZones.Core.BarCapacity]::SelectDiameterAndStep(6.0, 36, 12, $true)
Assert ($step200.Item1 -ge 12 -and $step200.Item2 -eq 200) 'Single-spacing mode must use 200 mm.'
Assert ($mixed.Item1 -ge 12 -and $mixed.Item2 -eq 100) 'Mixed-spacing mode did not select the economical 100 mm option.'
Assert ($mixed200.Item1 -ge 12 -and $mixed200.Item2 -eq 200) 'Mixed-spacing mode did not retain the economical 200 mm option.'
Write-Host 'PASS diameter floor and mixed 100/200 spacing selection'
$xDirection = [LiraSlabZones.Core.ZoneDirection]::X
$yDirection = [LiraSlabZones.Core.ZoneDirection]::Y
Assert ([LiraSlabZones.Core.RebarTables]::DirectionForLayer([LiraSlabZones.Core.RebarLayer]::As1, $false) -eq $xDirection) 'Normal As1 direction must be X.'
Assert ([LiraSlabZones.Core.RebarTables]::DirectionForLayer([LiraSlabZones.Core.RebarLayer]::As2, $false) -eq $yDirection) 'Normal As2 direction must be Y.'
Assert ([LiraSlabZones.Core.RebarTables]::DirectionForLayer([LiraSlabZones.Core.RebarLayer]::As3, $true) -eq $yDirection) 'Reversed As3 direction must be Y.'
Assert ([LiraSlabZones.Core.RebarTables]::DirectionForLayer([LiraSlabZones.Core.RebarLayer]::As4, $true) -eq $xDirection) 'Reversed As4 direction must be X.'
Write-Host 'PASS normal and reversed layer directions'

$editOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$editOutline.Add([LiraSlabZones.Core.Point3]::new(0, 0, 0))
$editOutline.Add([LiraSlabZones.Core.Point3]::new(10, 0, 0))
$editOutline.Add([LiraSlabZones.Core.Point3]::new(10, 10, 0))
$editOutline.Add([LiraSlabZones.Core.Point3]::new(0, 10, 0))
$editTemplate = [LiraSlabZones.Core.AdditionalZone]::new()
$editTemplate.Layer = [LiraSlabZones.Core.RebarLayer]::As1
$editTemplate.Direction = [LiraSlabZones.Core.ZoneDirection]::X
$editTemplate.DiameterMm = 16
$editTemplate.BarStepMm = 200
$edited = [LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, -1, 4, 2, 5, $editOutline)
Assert ($null -ne $edited -and (($edited.Contour.X | Measure-Object -Minimum).Minimum -ge -0.000001)) 'Clipper create did not trim the zone to the slab.'
Assert ([LiraSlabZones.Core.ZoneEditor]::Move($edited, 2, 1, $editOutline)) 'Clipper move failed.'
Assert ([LiraSlabZones.Core.ZoneEditor]::Resize($edited, 1, 7, 1, 7, $editOutline)) 'Clipper resize failed.'
$parts = [LiraSlabZones.Core.ZoneEditor]::Split($edited, 4, $true, $editOutline)
Assert ($parts.Count -eq 2) 'Clipper split did not produce two zones.'
$joined = [LiraSlabZones.Core.ZoneEditor]::Merge($parts[0], $parts[1], $editOutline)
Assert ($null -ne $joined -and $joined.Contour.Count -ge 4) 'Clipper merge failed.'
Write-Host 'PASS Clipper2 zone create, move, resize, split and merge'
$manual = [LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, 1, 7, 2, 5, $editOutline)
[LiraSlabZones.Core.ZoneEditor]::SetDiameter($manual, 20)
Assert ($manual.DiameterMm -eq 20 -and $manual.AsCoveredCm2PerM -gt 0) 'Manual diameter was not applied.'
$edgeParts = [LiraSlabZones.Core.ZoneEditor]::SplitPerpendicularToEdge($manual, 4, 3.5, $false, $editOutline)
Assert ($edgeParts.Count -eq 2) 'Horizontal edge must create two zones by a vertical cut.'
Assert ([Math]::Abs($edgeParts[0].LengthM - 3) -lt 0.001) 'Vertical cut is not at the clicked X coordinate.'
$verticalParts = [LiraSlabZones.Core.ZoneEditor]::SplitPerpendicularToEdge($manual, 4, 3.5, $true, $editOutline)
Assert ($verticalParts.Count -eq 2) 'Vertical edge must create two zones by a horizontal cut.'
Assert ([LiraSlabZones.Core.ZoneEditor]::ResizeByDimensions($manual, 4000, 2000, $editOutline)) 'Manual dimensions were rejected.'
Assert ([Math]::Abs($manual.LengthMm - 4000) -lt 1 -and [Math]::Abs($manual.WidthMm - 2000) -lt 1) 'Manual dimensions were not applied.'
[LiraSlabZones.Core.ZoneEditor]::SetFamily($manual, [LiraSlabZones.Core.ZoneFamilyKind]::L, 'Custom SUM-31')
Assert ($manual.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::L -and $manual.FamilyFileName -eq 'Custom SUM-31') 'Manual family was not applied.'
Write-Host 'PASS manual diameter, edge split, dimensions and family'
$gapMoving = [LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, 4, 6, 2, 4, $editOutline)
$gapFixed = [LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, 6, 8, 2, 4, $editOutline)
$gapMoving.BarStepMm = 100
$gapFixed.BarStepMm = 200
$fixedMinBefore = ($gapFixed.Contour.X | Measure-Object -Minimum).Minimum
Assert ([LiraSlabZones.Core.ZoneEditor]::CreateGap($gapMoving, $gapFixed, $editOutline)) 'Manual gap creation failed.'
$movingMax = ($gapMoving.Contour.X | Measure-Object -Maximum).Maximum
$fixedMin = ($gapFixed.Contour.X | Measure-Object -Minimum).Minimum
Assert ([Math]::Abs(($fixedMin - $movingMax) - 0.1) -lt 0.001) 'Manual gap is not equal to the smaller 100 mm spacing.'
Assert ([Math]::Abs($fixedMin - $fixedMinBefore) -lt 0.000001) 'Reference zone moved while creating a gap.'
Write-Host 'PASS two-click gap uses the smaller zone spacing'
$holePlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
for ($hy = 0; $hy -lt 6; $hy++) {
    for ($hx = 0; $hx -lt 6; $hx++) {
        if (($hx -in @(2,3) -and $hy -in @(2,3)) -or ($hx -eq 4 -and $hy -eq 4)) { continue }
        $plate = [LiraSlabZones.Core.LiraPlateElement]::new()
        $plate.Id = $hy * 6 + $hx + 1
        foreach ($point in @(@($hx,$hy),@(($hx+1),$hy),@(($hx+1),($hy+1)),@($hx,($hy+1)))) {
            $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($point[0], $point[1], 0))
        }
        $holePlates.Add($plate)
    }
}
$holeOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
foreach ($point in @(@(0,0),@(6,0),@(6,6),@(0,6))) {
    $holeOutline.Add([LiraSlabZones.Core.Point3]::new($point[0], $point[1], 0))
}
$detectedHoles = [LiraSlabZones.Core.SlabOpenings]::Detect($holePlates, $holeOutline)
Assert ($detectedHoles.Count -eq 1) "Expected only the 2x2 FE opening, got $($detectedHoles.Count)."
Assert ([Math]::Abs($detectedHoles[0].WidthM - 2) -lt 0.001 -and [Math]::Abs($detectedHoles[0].HeightM - 2) -lt 0.001) 'Opening dimensions are incorrect.'
Write-Host 'PASS opening detection ignores 1x1 FE gaps and keeps 2x2 FE gaps'
$openingSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$openingSettings.SlabThicknessMm = 200
$openingSettings.CoverTopMm = 25
$openingSettings.CoverBottomMm = 25
$crossing = [LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, 0.5, 5.5, 0.5, 5.5, $holeOutline)
$holePieces = [LiraSlabZones.Core.ZoneEditor]::SplitAtOpenings(
    $crossing, $detectedHoles, $openingSettings)
Assert ($holePieces.Count -eq 4) 'Opening must split a crossing zone into four non-overlapping pieces.'
Assert (($holePieces | Where-Object FamilyKind -eq ([LiraSlabZones.Core.ZoneFamilyKind]::Straight)).Count -eq 2) 'Unaffected bar lanes must remain straight.'
Assert (($holePieces | Where-Object FamilyKind -ne ([LiraSlabZones.Core.ZoneFamilyKind]::Straight)).Count -eq 2) 'Bars ending at the opening must use bent families.'
Assert (($holePieces | Where-Object { [LiraSlabZones.Core.ZoneEditor]::IntersectsOpening($_, $detectedHoles) }).Count -eq 0) 'A split piece still crosses the opening.'
[LiraSlabZones.Core.ZoneEditor]::NormalizeBarArrayWidthsToStep($holePieces)
Assert (($holePieces | Where-Object { [Math]::Abs($_.WidthMm / $_.BarStepMm - [Math]::Round($_.WidthMm / $_.BarStepMm)) -gt 0.000001 }).Count -eq 0) `
    'A cut zone width was not normalized to a whole spacing module.'
Write-Host 'PASS opening splits bent end pieces and straight bypass pieces'

$insideOpening = [LiraSlabZones.Core.ZoneEditor]::Create(
    $editTemplate, 2.25, 3.75, 2.25, 3.75, $holeOutline)
Assert ([LiraSlabZones.Core.ZoneEditor]::IsFullyInsideOpening($insideOpening, $detectedHoles)) `
    'A zone strictly inside an opening was not recognized.'
Assert (([LiraSlabZones.Core.ZoneEditor]::SplitAtOpenings(
    $insideOpening, $detectedHoles, $openingSettings)).Count -eq 0) `
    'A zone strictly inside an opening must be removed by the cut.'
Write-Host 'PASS zones fully inside openings are identified and clipped away'

$oppositeRowSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$oppositeRowSettings.SlabThicknessMm = 222
$oppositeRowSettings.CoverTopMm = 25
$oppositeRowSettings.CoverBottomMm = 25
$oppositeRowSettings.BgTopDiameterMm = 12
$oppositeRowSettings.BgBottomDiameterMm = 20
$as1Vertical = [LiraSlabZones.Core.HoleBentRules]::VerticalLegAvailableMm(
    $oppositeRowSettings, [LiraSlabZones.Core.RebarLayer]::As1, 36)
$as3Vertical = [LiraSlabZones.Core.HoleBentRules]::VerticalLegAvailableMm(
    $oppositeRowSettings, [LiraSlabZones.Core.RebarLayer]::As3, 36)
Assert ([Math]::Abs($as1Vertical - 160) -lt 0.001) 'As1 must use the opposite top-row diameter.'
Assert ([Math]::Abs($as3Vertical - 152) -lt 0.001) 'As3 must use the opposite bottom-row diameter.'
Assert (([LiraSlabZones.Core.HoleBentRules]::ChooseBentFamily($as1Vertical, 16)) -eq
    [LiraSlabZones.Core.ZoneFamilyKind]::PEqual) 'The Legacy family threshold for As1 was not applied.'
Assert (([LiraSlabZones.Core.HoleBentRules]::ChooseBentFamily($as3Vertical, 16)) -eq
    [LiraSlabZones.Core.ZoneFamilyKind]::L) 'The Legacy family threshold for As3 was not applied.'
Write-Host 'PASS bent family clearance uses the opposite background row'

$allowanceOpening = [LiraSlabZones.Core.OpeningInfo]::new()
$allowanceOpening.MinXM = 1.1
$allowanceOpening.MaxXM = 1.8
$allowanceOpening.MinYM = 0.2
$allowanceOpening.MaxYM = 0.8
$allowanceOpenings = [Collections.Generic.List[LiraSlabZones.Core.OpeningInfo]]::new()
$allowanceOpenings.Add($allowanceOpening)
$nearOpeningZone = New-RectZone 0.5 1.0 0.2 0.8 100 5
$nearOpeningPlate = New-ContourPlate 800 0.5 1.08 0.2 0.8
$nearOpeningZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$nearOpeningZones.Add($nearOpeningZone)
Assert ([LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
    $nearOpeningZones, $nearOpeningPlate, [LiraSlabZones.Core.RebarLayer]::As2,
    5, $null, $allowanceOpenings)) 'A required FE within 100 mm of an opening was not treated as covered.'
$allowanceOpening.MinXM = 1.3
$farOpeningZone = New-RectZone 0.5 1.0 0.2 0.8 100 5
$farOpeningZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$farOpeningZones.Add($farOpeningZone)
Assert (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
    $farOpeningZones, $nearOpeningPlate, [LiraSlabZones.Core.RebarLayer]::As2,
    5, $null, $allowanceOpenings)) 'An FE more than 100 mm from an opening was incorrectly treated as covered.'
Write-Host 'PASS opening coverage allowance is limited to 100 mm'

# При реверсе направление для правил отверстия определяется по слою, даже если
# зона была загружена со старым (нереверсивным) значением Direction.
$reverseHoleSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$reverseHoleSettings.ReverseZoneDirections = $true
$reverseHoleSettings.SlabThicknessMm = 200
$reverseHoleSettings.CoverTopMm = 25
$reverseHoleSettings.CoverBottomMm = 25
$reverseHoleTemplate = [LiraSlabZones.Core.AdditionalZone]::new()
$reverseHoleTemplate.Layer = [LiraSlabZones.Core.RebarLayer]::As2
$reverseHoleTemplate.Direction = [LiraSlabZones.Core.ZoneDirection]::Y
$reverseHoleTemplate.DiameterMm = 16
$reverseHoleTemplate.BarStepMm = 200
$reverseHoleTemplate.FamilyKind = [LiraSlabZones.Core.ZoneFamilyKind]::Straight
$reverseCrossing = [LiraSlabZones.Core.ZoneEditor]::Create(
    $reverseHoleTemplate, 0.5, 5.5, 0.5, 5.5, $holeOutline)
$reverseHolePieces = [LiraSlabZones.Core.ZoneEditor]::SplitAtOpenings(
    $reverseCrossing, $detectedHoles, $reverseHoleSettings)
Assert ($reverseHolePieces.Count -eq 4) 'Reversed As2 opening split must produce four pieces.'
Assert (($reverseHolePieces | Where-Object Direction -ne $xDirection).Count -eq 0) 'Reversed As2 opening rules did not switch to X.'
Assert (($reverseHolePieces | Where-Object FamilyKind -ne ([LiraSlabZones.Core.ZoneFamilyKind]::Straight)).Count -eq 2) 'Reversed As2 must bend the two X-directed ends at the opening.'
Assert (($reverseHolePieces | Where-Object FamilyKind -eq ([LiraSlabZones.Core.ZoneFamilyKind]::Straight)).Count -eq 2) 'Reversed As2 must keep the two bypass lanes straight.'
$reverseBentPieces = @($reverseHolePieces | Where-Object FamilyKind -ne ([LiraSlabZones.Core.ZoneFamilyKind]::Straight))
$leftBentMaxX = (($reverseBentPieces | Where-Object { ($_.Contour.X | Measure-Object -Maximum).Maximum -lt $detectedHoles[0].MinXM }).Contour.X | Measure-Object -Maximum).Maximum
$rightBentMinX = (($reverseBentPieces | Where-Object { ($_.Contour.X | Measure-Object -Minimum).Minimum -gt $detectedHoles[0].MaxXM }).Contour.X | Measure-Object -Minimum).Minimum
Assert ([Math]::Abs(($detectedHoles[0].MinXM - $leftBentMaxX) * 1000 - 50) -lt 1) 'Left bent end has no 50 mm opening gap.'
Assert ([Math]::Abs(($rightBentMinX - $detectedHoles[0].MaxXM) * 1000 - 50) -lt 1) 'Right bent end has no 50 mm opening gap.'
Write-Host 'PASS reversed As2 opening split uses As1/As3 X-direction rules'
$holeResult = [LiraSlabZones.Core.AnalysisResult]::new()
$holeResult.Settings = $openingSettings
$holeResult.Outline = $holeOutline
$holeResult.Openings = $detectedHoles
$holeViewport = [LiraSlabZones.Revit2023.UI.PreviewViewport]::new()
$holeViewport.SetData($holeResult, $openingSettings, $true, $false)
$privateInstance = [Reflection.BindingFlags]'NonPublic,Instance'
$holeViewport.GetType().GetMethod('BeginEdit', $privateInstance).Invoke($holeViewport, @()) | Out-Null
$holeResult.Zones.Add([LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, 0.5, 5.5, 0.5, 5.5, $holeOutline))
$holeViewport.GetType().GetMethod('CommitEdits', $privateInstance).Invoke($holeViewport, @($null)) | Out-Null
Assert ($holeResult.Zones.Count -eq 4) 'Preview rejected a zone crossing an opening instead of splitting it.'
Assert ($holeViewport.UndoLastEdit() -and $holeResult.Zones.Count -eq 0) 'Undo did not restore the state before opening split.'
Write-Host 'PASS preview splits a crossing zone and supports undo'
$longZone = [LiraSlabZones.Core.AdditionalZone]::new()
$longZone.Direction = [LiraSlabZones.Core.ZoneDirection]::Y
$longZone.DiameterMm = 25
$longZone.BarStepMm = 200
$longZone.ConcreteClass = 'B40'
$longZone.FamilyKind = [LiraSlabZones.Core.ZoneFamilyKind]::PEqual
$longZone.VerticalLegMm = 150
$longZone.LengthMm = 12960
foreach ($point in @(@(0.5,0.5),@(1.5,0.5),@(1.5,13.16),@(0.5,13.16))) {
    $longZone.Contour.Add([LiraSlabZones.Core.Point3]::new($point[0], $point[1], 0))
}
$longZone.NodeIds.Add(1); $longZone.NodeIds.Add(2); $longZone.NodeIds.Add(3)
$longMosaic = [LiraSlabZones.Core.MosaicGrid]::new()
$longMosaic.PlateCentroids[1] = [LiraSlabZones.Core.Point3]::new(1, 1, 0)
$longMosaic.PlateCentroids[2] = [LiraSlabZones.Core.Point3]::new(1, 6, 0)
$longMosaic.PlateCentroids[3] = [LiraSlabZones.Core.Point3]::new(1, 12, 0)
$longOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
foreach ($point in @(@(0,0),@(4,0),@(4,14),@(0,14))) {
    $longOutline.Add([LiraSlabZones.Core.Point3]::new($point[0], $point[1], 0))
}
$longZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$longZones.Add($longZone)
Assert ([LiraSlabZones.Core.RebarTables]::ExceedsMaxBarLength($longZone)) 'Overlong bent bar was not detected before splitting.'
$splitLong = [LiraSlabZones.Core.ZoneLayoutEngine].GetMethod('SplitOverlongZones', [Reflection.BindingFlags]'NonPublic,Static')
$splitLong.Invoke($null, @($longZones, $longMosaic, $longOutline, 0.0)) | Out-Null
Assert ($longZones.Count -eq 2) 'A 12660 mm bent zone was not divided.'
Assert (($longZones | Where-Object LengthMm -gt 11700).Count -eq 0) 'A divided bent zone still exceeds 11700 mm.'
Assert (($longZones | Where-Object { [LiraSlabZones.Core.RebarTables]::ExceedsMaxBarLength($_) }).Count -eq 0) 'Bent bar geometry still exceeds 11700 mm.'
$overlap = ([Math]::Min(($longZones[0].Contour.Y | Measure-Object -Maximum).Maximum, ($longZones[1].Contour.Y | Measure-Object -Maximum).Maximum) -
    [Math]::Max(($longZones[0].Contour.Y | Measure-Object -Minimum).Minimum, ($longZones[1].Contour.Y | Measure-Object -Minimum).Minimum)) * 1000
Assert ($overlap + 1 -ge (2 * [LiraSlabZones.Core.RebarTables]::LapLenMm('B40', 25))) 'Bent splice has insufficient overlap.'
Write-Host 'PASS 12660 mm bent zone splits into bars at most 11700 mm with tabular overlap'
Assert ([LiraSlabZones.Core.RebarTables]::PickFamilyLength(3460) -eq 3900) '3460 mm was not rounded up to the 3900 mm family length.'
Write-Host 'PASS family length rounds 3460 mm up to 3900 mm'
Assert ([LiraSlabZones.Core.RebarTables]::BentBarTotalLengthMm(3460, 150, [LiraSlabZones.Core.ZoneFamilyKind]::L) -eq 3610) 'SUM-31 total length is wrong.'
Assert ([LiraSlabZones.Core.RebarTables]::BentBarTotalLengthMm(3460, 150, [LiraSlabZones.Core.ZoneFamilyKind]::PEqual) -eq 3760) 'SUM-32 total length is wrong.'
Write-Host 'PASS bent length = plan part + vertical legs'
$lapA = [LiraSlabZones.Core.AdditionalZone]::new()
$lapB = [LiraSlabZones.Core.AdditionalZone]::new()
$lapA.LengthMm = 11700; $lapA.DiameterMm = 12; $lapA.ConcreteClass = 'B40'
$lapB.LengthMm = 3900;  $lapB.DiameterMm = 16; $lapB.ConcreteClass = 'B40'
Assert ([LiraSlabZones.Core.RebarTables]::AllowedZoneOverlapMm($lapA, $lapB) -eq 2000) 'Allowed overlap must be two laps of the larger diameter.'
$lapA.LengthMm = 7800
Assert ([LiraSlabZones.Core.RebarTables]::AllowedZoneOverlapMm($lapA, $lapB) -eq 0) 'Overlap without a 11700 mm zone must be forbidden.'
$localOverlapA = New-GapZone 0 2 200 5
$localOverlapB = New-GapZone 0 2 200 5
$localOverlapA.FamilyKind = [LiraSlabZones.Core.ZoneFamilyKind]::BentStick
$localOverlapB.FamilyKind = [LiraSlabZones.Core.ZoneFamilyKind]::BentStick
$localOverlapA.Comment = 'локальная гнутая деталь'
$localOverlapB.Comment = 'локальная гнутая деталь'
for ($i = 0; $i -lt $localOverlapB.Contour.Count; $i++) {
    $point = $localOverlapB.Contour[$i]
    $localOverlapB.Contour[$i] = [LiraSlabZones.Core.Point3]::new($point.X + 3.5, $point.Y, $point.Z)
}
Assert ([LiraSlabZones.Core.ZoneEditor]::HasPlacementConflict($localOverlapA, $localOverlapB)) `
    'Local bent details bypassed the required non-overlap rule.'
$lapA = New-RectZone 0 11.7 0 2 200 5
$lapA.LengthMm = 11700; $lapA.DiameterMm = 25; $lapA.ConcreteClass = 'B40'
$lapB = New-RectZone 8.5 12.4 0 2 200 5
$lapB.LengthMm = 3900; $lapB.DiameterMm = 25; $lapB.ConcreteClass = 'B40'
Assert (-not [LiraSlabZones.Core.ZoneEditor]::HasPlacementConflict($lapA, $lapB)) `
    'A valid 11700 mm longitudinal splice with aligned bar bands was rejected.'
$lapB.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$lapB.Contour.Add([LiraSlabZones.Core.Point3]::new(8.5, 1, 0))
$lapB.Contour.Add([LiraSlabZones.Core.Point3]::new(12.4, 1, 0))
$lapB.Contour.Add([LiraSlabZones.Core.Point3]::new(12.4, 3, 0))
$lapB.Contour.Add([LiraSlabZones.Core.Point3]::new(8.5, 3, 0))
Assert ([LiraSlabZones.Core.ZoneEditor]::HasPlacementConflict($lapA, $lapB)) `
    'A transverse overlap was incorrectly accepted as a longitudinal splice.'
$lapB.Direction = [LiraSlabZones.Core.ZoneDirection]::Y
$lapB.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$lapB.Contour.Add([LiraSlabZones.Core.Point3]::new(8.5, 0, 0))
$lapB.Contour.Add([LiraSlabZones.Core.Point3]::new(12.4, 0, 0))
$lapB.Contour.Add([LiraSlabZones.Core.Point3]::new(12.4, 2, 0))
$lapB.Contour.Add([LiraSlabZones.Core.Point3]::new(8.5, 2, 0))
Assert ([LiraSlabZones.Core.ZoneEditor]::HasPlacementConflict($lapA, $lapB)) `
    'A cross-direction overlap was incorrectly accepted as a longitudinal splice.'
Write-Host 'PASS 11700 zone overlap uses two laps of the larger diameter'

function Test-ZoneCoverage($plate, $zones) {
    foreach ($zone in $zones) {
        $minX = ($zone.Contour.X | Measure-Object -Minimum).Minimum
        $maxX = ($zone.Contour.X | Measure-Object -Maximum).Maximum
        $minY = ($zone.Contour.Y | Measure-Object -Minimum).Minimum
        $maxY = ($zone.Contour.Y | Measure-Object -Maximum).Maximum
        $dx = [Math]::Max(0, [Math]::Max($minX - $plate.Centroid.X, $plate.Centroid.X - $maxX))
        $dy = [Math]::Max(0, [Math]::Max($minY - $plate.Centroid.Y, $plate.Centroid.Y - $maxY))
        if ([Math]::Sqrt($dx * $dx + $dy * $dy) -le 0.01) { return $true }
    }
    $typedZones = [LiraSlabZones.Core.AdditionalZone[]]@($zones)
    return [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
        $typedZones, $plate.Centroid, 0)
}

function Test-PointCoverage($point, $zones) {
    foreach ($zone in $zones) {
        $minX = ($zone.Contour.X | Measure-Object -Minimum).Minimum
        $maxX = ($zone.Contour.X | Measure-Object -Maximum).Maximum
        $minY = ($zone.Contour.Y | Measure-Object -Minimum).Minimum
        $maxY = ($zone.Contour.Y | Measure-Object -Maximum).Maximum
        if ($point.X -ge ($minX - 0.01) -and $point.X -le ($maxX + 0.01) -and
            $point.Y -ge ($minY - 0.01) -and $point.Y -le ($maxY + 0.01)) {
            return $true
        }
    }
    return $false
}

function Assert-ZonesInsideOutline($zones, $outline) {
    foreach ($zone in $zones) {
        foreach ($point in $zone.Contour) {
            $x = $point.X + ($zone.Placement.X - $point.X) * 0.001
            $y = $point.Y + ($zone.Placement.Y - $point.Y) * 0.001
            Assert ([LiraSlabZones.Core.MeshBoundary]::PointInPolygon($x, $y, $outline)) `
                "Zone $($zone.ZoneId) ($($zone.Layer)/$($zone.Direction)/$($zone.FamilyKind), L=$($zone.LengthMm)) extends outside the slab outline at ($($point.X),$($point.Y))."
        }
    }
}

function New-PolygonPlate([int]$id, [double[][]]$xy, [double]$as3) {
    $plate = [LiraSlabZones.Core.LiraPlateElement]::new()
    $plate.Id = $id
    $plate.TypeCode = if ($xy.Count -eq 3) { 44 } else { 42 }
    foreach ($point in $xy) {
        $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($point[0], $point[1], 3.0))
    }
    $plate.Centroid = [LiraSlabZones.Core.Point3]::new(
        ($plate.Contour.X | Measure-Object -Average).Average,
        ($plate.Contour.Y | Measure-Object -Average).Average,
        3.0)
    $width = 0.0
    $length = 0.0
    [LiraSlabZones.Core.ContourFix]::EdgeAlignedSize($plate.Contour, [ref]$width, [ref]$length)
    $plate.WidthM = $width
    $plate.LengthM = $length
    $plate.Rebar.Ok = $true
    $plate.Rebar.As3 = $as3
    return $plate
}

function Assert-PolygonApproximation([string]$name, $plates, $settings, $outline) {
    $zones = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout($plates, $settings, $null, $outline, $null)
    Assert ($zones.Count -eq 1) "${name}: expected one approximating zone, got $($zones.Count)."
    $zone = $zones[0]
    Assert ($zone.Contour.Count -eq 4) "${name}: approximating zone is not rectangular."
    foreach ($plate in $plates) {
        Assert ($zone.NodeIds -contains $plate.Id) "${name}: FE $($plate.Id) is absent from the zone."
        foreach ($point in $plate.Contour) {
            Assert (Test-PointCoverage $point @($zone)) "${name}: vertex of FE $($plate.Id) is outside the zone."
        }
    }
    Write-Host "PASS $name`: $($plates.Count) polygon FE -> 1 rectangle, $($zone.WidthMm)x$($zone.LengthMm) mm"
}

$legacyDemoSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$legacyDemoSettings.PlacementMode = 'LegacyZones'
$demo = [LiraSlabZones.Core.DemoSlabFactory]::Create($legacyDemoSettings)
$expected = $demo.Zones.Count
Assert ($expected -gt 0) 'Demo has no zones.'

# Reproduce old LIRA exports: contour sorted, node IDs still in FE order.
foreach ($plate in $demo.Plates) {
    $swap = $plate.NodeIds[2]
    $plate.NodeIds[2] = $plate.NodeIds[3]
    $plate.NodeIds[3] = $swap
}
$rebuilt = [LiraSlabZones.Core.SlabZoneAnalyzer]::RebuildForElevation($demo, 3, $demo.Settings)
Assert ($rebuilt.Zones.Count -eq $expected) 'Legacy node order changed the zone count.'
Write-Host "PASS legacy contour: $expected zones"

# Аппроксимация пятна: три активных КЭ 400x400 мм образуют букву "Г".
# Ожидается один охватывающий прямоугольник, а не три зоны по одному КЭ.
$approxSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$approxSettings.PlacementMode = 'LegacyZones'
$approxSettings.ShowAs1 = $false
$approxSettings.ShowAs2 = $false
$approxSettings.ShowAs3 = $true
$approxSettings.ShowAs4 = $false
$approxSettings.SlabSelected = $true
$approxSettings.BgBottomDiameterMm = 12
$approxSettings.BgBottomStepMm = 200
$approxSettings.BgTopDiameterMm = 12
$approxSettings.BgTopStepMm = 200
$approxSettings.ConcreteClass = 'B40'
$approxSettings.GridCellMm = 400
$approxSettings.DetailSlider = 1.0
$approxSettings.UseBarStep100 = $true
$approxSettings.MinZoneWidthM = 1.2
$approxSettings.SyncBackgroundAsFromBars()

$approxPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$activeIds = @(15, 16, 21) # (2,2), (3,2), (2,3) в сетке 6x6
$elementId = 1
for ($iy = 0; $iy -lt 6; $iy++) {
    for ($ix = 0; $ix -lt 6; $ix++) {
        $x0 = $ix * 0.4
        $y0 = $iy * 0.4
        $plate = [LiraSlabZones.Core.LiraPlateElement]::new()
        $plate.Id = $elementId
        $plate.TypeCode = 42
        $plate.Centroid = [LiraSlabZones.Core.Point3]::new($x0 + 0.2, $y0 + 0.2, 3.0)
        $plate.WidthM = 0.4
        $plate.LengthM = 0.4
        $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($x0,       $y0,       3.0))
        $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($x0 + 0.4, $y0,       3.0))
        $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($x0 + 0.4, $y0 + 0.4, 3.0))
        $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($x0,       $y0 + 0.4, 3.0))
        $plate.Rebar.Ok = $true
        $plate.Rebar.As3 = if ($activeIds -contains $elementId) {
            $approxSettings.AsMainAs3 + 4.0
        } else {
            $approxSettings.AsMainAs3
        }
        $approxPlates.Add($plate)
        $elementId++
    }
}

$approx = [LiraSlabZones.Core.SlabZoneAnalyzer]::BuildResult(
    'APPROX_L_SHAPE', '(synthetic)', 49, $approxPlates, $approxSettings,
    $null, 3.0, 'Z = 3.000 m', $true, $null)
Assert ($approx.Zones.Count -eq 1) 'L-shaped spot was not approximated by one zone.'
Assert-AutoZoneArrayWidths $approx.Zones
$approxZone = $approx.Zones[0]
Assert ($approxZone.Contour.Count -eq 4) 'Approximated zone is not rectangular.'
Assert ($approxZone.NodeIds.Count -eq 3) 'Approximated zone lost or added active FE.'
Assert (($activeIds | Where-Object { $_ -notin $approxZone.NodeIds }).Count -eq 0) 'Approximated zone does not contain every active FE.'
Assert ($approxZone.WidthMm -ge 1199) 'Approximated zone does not satisfy the 1200 mm minimum width.'
foreach ($id in $activeIds) {
    $plate = $approxPlates | Where-Object { $_.Id -eq $id }
    Assert (Test-ZoneCoverage $plate @($approxZone)) "Approximated zone does not cover FE $id."
}
Write-Host "PASS L-shape approximation: 3 FE -> 1 rectangle, $($approxZone.WidthMm)x$($approxZone.LengthMm) mm"

$approxSettings.ReverseZoneDirections = $true
$reversedApprox = [LiraSlabZones.Core.SlabZoneAnalyzer]::BuildResult(
    'APPROX_L_SHAPE_REVERSED', '(synthetic)', 49, $approxPlates, $approxSettings,
    $null, 3.0, 'Z = 3.000 m', $true, $null)
Assert ($reversedApprox.Zones.Count -eq 1) 'Reversed L-shaped spot changed the zone count.'
Assert-AutoZoneArrayWidths $reversedApprox.Zones
Assert ($reversedApprox.Zones[0].Direction -eq $yDirection) 'Reversed As3 zone was not laid along Y.'
foreach ($id in $activeIds) {
    $plate = $approxPlates | Where-Object { $_.Id -eq $id }
    Assert (Test-ZoneCoverage $plate @($reversedApprox.Zones[0])) "Reversed zone does not cover FE $id."
}
$approxSettings.ReverseZoneDirections = $false
Write-Host 'PASS reversed As3 layout uses Y and retains FE coverage'

# При реверсе As2 стержень идёт по X и остаётся центрированным относительно пятна,
# сохраняя анкеровку по бетону с обеих сторон.
$reverseAs2Settings = [Newtonsoft.Json.JsonConvert]::DeserializeObject(
    [Newtonsoft.Json.JsonConvert]::SerializeObject($approxSettings),
    [LiraSlabZones.Core.AnalysisSettings])
$reverseAs2Settings.ShowAs2 = $true
$reverseAs2Settings.ShowAs3 = $false
$reverseAs2Settings.ReverseZoneDirections = $true
$reverseAs2Plate = New-PolygonPlate 91 @(@(2.6,1.6), @(3.4,1.6), @(3.4,2.4), @(2.6,2.4)) 0
$reverseAs2Plate.Rebar.As2 = $reverseAs2Settings.AsMainAs2 + 4.0
$reverseAs2Plates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$reverseAs2Plates.Add($reverseAs2Plate)
$reverseOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
foreach ($xy in @(@(0,0), @(6,0), @(6,4), @(0,4))) {
    $reverseOutline.Add([LiraSlabZones.Core.Point3]::new($xy[0], $xy[1], 3))
}
$reverseAs2Zones = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout(
    $reverseAs2Plates, $reverseAs2Settings, $null, $reverseOutline, $null)
Assert ($reverseAs2Zones.Count -eq 1) 'Reversed As2 did not create one centred zone.'
$reverseAs2Zone = $reverseAs2Zones[0]
Assert ($reverseAs2Zone.Direction -eq $xDirection) 'Reversed As2 bar direction is not X.'
$zoneMinX = ($reverseAs2Zone.Contour.X | Measure-Object -Minimum).Minimum
$zoneMaxX = ($reverseAs2Zone.Contour.X | Measure-Object -Maximum).Maximum
$leftAnchorMm = (2.6 - $zoneMinX) * 1000
$rightAnchorMm = ($zoneMaxX - 3.4) * 1000
$requiredAnchorMm = [LiraSlabZones.Core.RebarTables]::AnchorageLenMm(
    $reverseAs2Settings.ConcreteClass, $reverseAs2Zone.DiameterMm)
Assert ([Math]::Abs($leftAnchorMm - $rightAnchorMm) -le 1.1) 'Reversed As2 zone is not centred along X.'
Assert ($leftAnchorMm + 1 -ge $requiredAnchorMm -and $rightAnchorMm + 1 -ge $requiredAnchorMm) `
    'Reversed As2 zone lost concrete anchorage.'
Write-Host 'PASS reversed As2 is centred along X and preserves anchorage'

$testOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$testOutline.Add([LiraSlabZones.Core.Point3]::new(0, 0, 3))
$testOutline.Add([LiraSlabZones.Core.Point3]::new(4, 0, 3))
$testOutline.Add([LiraSlabZones.Core.Point3]::new(4, 4, 3))
$testOutline.Add([LiraSlabZones.Core.Point3]::new(0, 4, 3))
$activeAs3 = $approxSettings.AsMainAs3 + 4.0

$peakSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$peakSettings.ShowAs1 = $false
$peakSettings.ShowAs2 = $true
$peakSettings.ShowAs3 = $false
$peakSettings.ShowAs4 = $false
$peakSettings.SlabSelected = $true
$peakSettings.BgBottomDiameterMm = 12
$peakSettings.BgBottomStepMm = 200
$peakSettings.BgTopDiameterMm = 12
$peakSettings.BgTopStepMm = 200
$peakSettings.ConcreteClass = 'B40'
$peakSettings.GridCellMm = 400
$peakSettings.DetailSlider = 0.25
$peakSettings.UseBarStep100 = $true
$peakSettings.SyncBackgroundAsFromBars()
$peakPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$peakId = 710
for ($iy = 0; $iy -lt 3; $iy++) {
    for ($ix = 0; $ix -lt 3; $ix++) {
        $value = $peakSettings.AsMainAs2 + $(if ($ix -eq 1 -and $iy -eq 1) { 25.0 } else { 2.0 })
        $x0 = 1.0 + 0.4 * $ix
        $y0 = 1.0 + 0.4 * $iy
        $x1 = $x0 + 0.4
        $y1 = $y0 + 0.4
        $points = [double[][]]::new(4)
        $points[0] = [double[]]@($x0, $y0)
        $points[1] = [double[]]@($x1, $y0)
        $points[2] = [double[]]@($x1, $y1)
        $points[3] = [double[]]@($x0, $y1)
        $plate = New-PolygonPlate $peakId $points 0
        $plate.Rebar.As2 = $value
        $peakPlates.Add($plate)
        if ($ix -eq 1 -and $iy -eq 1) { $peakId = $plate.Id }
        $peakId++
    }
}
$peakZones = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout(
    $peakPlates, $peakSettings, $null, $testOutline, $null)
$peakCoveringZones = @($peakZones | Where-Object { $_.NodeIds -contains 714 })
Assert ($peakCoveringZones.Count -gt 0) 'The smoothed 3x3 test lost its central high-demand FE.'
Assert (($peakCoveringZones | Where-Object { $_.AsCoveredCm2PerM + 1e-6 -ge 25 }).Count -gt 0) `
    'Smoothing a local peak selected a diameter/step with insufficient reinforcement capacity.'
Write-Host 'PASS zone bar capacity is selected from the original unsmoothed peak'

$assignedPeakPlate = New-PolygonPlate 780 @(@(1,1), @(1.4,1), @(1.4,1.4), @(1,1.4)) 0
$assignedPeakPlate.Rebar.Ok = $true
$assignedPeakPlate.Rebar.As2 = $peakSettings.AsMainAs2 + 20.5
$assignedPeakPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$assignedPeakPlates.Add($assignedPeakPlate)
$assignedPeakZone = New-RectZone 0.8 1.6 0.8 1.6 200 5.65
$assignedPeakZone.Layer = [LiraSlabZones.Core.RebarLayer]::As2
$assignedPeakZone.DiameterMm = 12
$assignedPeakZone.NodeIds.Add($assignedPeakPlate.Id)
$assignedPeakZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$assignedPeakZones.Add($assignedPeakZone)
Assert ([LiraSlabZones.Core.ZoneEditor]::EnsureAssignedCapacity(
        $assignedPeakZones, $assignedPeakPlates, $peakSettings) -eq 1) `
    'Post-layout capacity audit did not update a zone assigned to a high-demand FE.'
Assert ($assignedPeakZone.DiameterMm -eq 25 -and $assignedPeakZone.BarStepMm -eq 200) `
    'Post-layout audit did not preserve spacing while increasing the zone diameter.'
Assert ($assignedPeakZone.AsCoveredCm2PerM + 1e-6 -ge 20.5) `
    'Post-layout audit left an assigned peak with insufficient zone capacity.'

$stepFallbackSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$stepFallbackSettings.ShowAs2 = $true
$stepFallbackSettings.BgBottomDiameterMm = 12
$stepFallbackSettings.BgBottomStepMm = 200
$stepFallbackSettings.MaxDiameterMm = 22
$stepFallbackSettings.UseBarStep100 = $true
$stepFallbackSettings.SyncBackgroundAsFromBars()
$stepFallbackZone = New-RectZone 0.8 1.6 0.8 1.6 200 5.65
$stepFallbackZone.Layer = [LiraSlabZones.Core.RebarLayer]::As2
$stepFallbackZone.DiameterMm = 12
$stepFallbackZone.NodeIds.Add($assignedPeakPlate.Id)
$stepFallbackZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$stepFallbackZones.Add($stepFallbackZone)
[void][LiraSlabZones.Core.ZoneEditor]::EnsureAssignedCapacity(
    $stepFallbackZones, $assignedPeakPlates, $stepFallbackSettings)
Assert ($stepFallbackZone.DiameterMm -eq 20 -and $stepFallbackZone.BarStepMm -eq 100) `
    "Post-layout audit did not fall back to 100 mm spacing when the 200 mm option was insufficient (got Ø$($stepFallbackZone.DiameterMm)/$($stepFallbackZone.BarStepMm))."
Assert ($stepFallbackZone.AsCoveredCm2PerM + 1e-6 -ge 20.5) `
    'The 100 mm fallback still does not cover the assigned peak.'
Write-Host 'PASS post-layout capacity audit upgrades diameter and, only if necessary, bar spacing'

# Два треугольных КЭ составляют один квадрат и должны дать одну зону.
$trianglePair = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$trianglePair.Add((New-PolygonPlate 101 @(@(1.2,1.2), @(2.0,1.2), @(2.0,2.0)) $activeAs3))
$trianglePair.Add((New-PolygonPlate 102 @(@(1.2,1.2), @(2.0,2.0), @(1.2,2.0)) $activeAs3))
Assert-PolygonApproximation 'triangle pair' $trianglePair $approxSettings $testOutline

# Поворотный четырёхугольник пересекает несколько ячеек мозаики.
$rotatedQuad = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$rotatedQuad.Add((New-PolygonPlate 201 @(@(0.8,2.0), @(2.0,1.1), @(3.2,2.0), @(2.0,2.9)) $activeAs3))
Assert-PolygonApproximation 'rotated quadrilateral' $rotatedQuad $approxSettings $testOutline

# Треугольник возле края: стандартная длина зоны должна сдвинуться внутрь плиты.
$edgeTriangle = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$edgeTriangle.Add((New-PolygonPlate 301 @(@(0.05,0.8), @(1.1,1.4), @(0.05,2.0)) $activeAs3))
Assert-PolygonApproximation 'edge triangle' $edgeTriangle $approxSettings $testOutline

# Локальный скруглённый/ломаный край не совпадает с AABB плиты, но всё равно
# должен назначить гнутое семейство вместо набора прямых зон у дуги.
$roundedOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
foreach ($xy in @(@(0,0), @(4,0), @(4,2), @(3.8,2.8), @(3.3,3.5), @(2.5,4), @(0,4))) {
    $roundedOutline.Add([LiraSlabZones.Core.Point3]::new($xy[0], $xy[1], 3))
}
$roundedPlate = New-PolygonPlate 302 @(@(3.05,2.75), @(3.45,2.75), @(3.45,3.15), @(3.05,3.15)) $activeAs3
$roundedPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$roundedPlates.Add($roundedPlate)
$roundedSettings = [Newtonsoft.Json.JsonConvert]::DeserializeObject(
    [Newtonsoft.Json.JsonConvert]::SerializeObject($approxSettings),
    [LiraSlabZones.Core.AnalysisSettings])
$roundedSettings.ApplySlabBoundaryAndOpeningRules = $true
$roundedSettings.ApplyBentRules = $true
$roundedZones = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout(
    $roundedPlates, $roundedSettings, $null, $roundedOutline, $null)
Assert ($roundedZones.Count -gt 0) 'Rounded edge removed the required zone.'
Assert (($roundedZones | Where-Object { $_.FamilyKind -ne [LiraSlabZones.Core.ZoneFamilyKind]::Straight }).Count -gt 0) `
    'Rounded local edge was not assigned a bent family.'
Write-Host 'PASS rounded local edge uses a bent family'

# MinActiveElements считает окрашенные КЭ, а не число занятых ими ячеек мозаики.
$approxSettings.MinActiveElements = 2
$singleLarge = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout($rotatedQuad, $approxSettings, $null, $testOutline, $null)
$twoElements = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout($trianglePair, $approxSettings, $null, $testOutline, $null)
Assert ($singleLarge.Count -eq 0) 'One colored FE was counted as several raster cells.'
Assert ($twoElements.Count -gt 0) 'Two colored FE did not satisfy MinActiveElements=2.'
$approxSettings.MinActiveElements = 0
Write-Host 'PASS minimum FE counts unique colored finite elements'

$mosaicInput = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$mosaicActivePlate = New-ContourPlate 2001 1.0 2.2 1.0 2.2
$mosaicInactivePlate = New-ContourPlate 2002 4.0 4.4 1.0 1.4
$mosaicOtherLevelPlate = New-ContourPlate 2003 10.0 11.0 10.0 11.0
foreach ($entry in @(
    @{ Plate = $mosaicActivePlate; Z = 3.0 },
    @{ Plate = $mosaicInactivePlate; Z = 3.0 },
    @{ Plate = $mosaicOtherLevelPlate; Z = 6.0 }
)) {
    $entry.Plate.Centroid = [LiraSlabZones.Core.Point3]::new(
        $entry.Plate.Centroid.X, $entry.Plate.Centroid.Y, $entry.Z)
    for ($i = 0; $i -lt $entry.Plate.Contour.Count; $i++) {
        $point = $entry.Plate.Contour[$i]
        $entry.Plate.Contour[$i] = [LiraSlabZones.Core.Point3]::new($point.X, $point.Y, $entry.Z)
    }
}
$mosaicActivePlate.Rebar.Ok = $true
$mosaicActivePlate.Rebar.As2 = 0.005
$mosaicOtherLevelPlate.Rebar.Ok = $true
$mosaicOtherLevelPlate.Rebar.As2 = 20
$mosaicInput.Add($mosaicActivePlate)
$mosaicInput.Add($mosaicInactivePlate)
$mosaicInput.Add($mosaicOtherLevelPlate)
$mosaicGrid = [LiraSlabZones.Core.MosaicBuilder]::Build(
    $mosaicInput, [LiraSlabZones.Core.RebarLayer]::As2, 0, 400, 3.0)
$mosaicIds = [Collections.Generic.HashSet[int]]::new()
$mosaicActiveCellCount = 0
for ($iy = 0; $iy -lt $mosaicGrid.Ny; $iy++) {
    for ($ix = 0; $ix -lt $mosaicGrid.Nx; $ix++) {
        if ($mosaicGrid.Values[$iy][$ix] -le
            [LiraSlabZones.Core.MosaicBuilder]::PositiveResidualToleranceCm2PerM) { continue }
        $mosaicActiveCellCount++
        foreach ($id in $mosaicGrid.PlateIds[$iy][$ix]) { [void]$mosaicIds.Add($id) }
        Assert ([Math]::Abs($mosaicGrid.Values[$iy][$ix] - 0.005) -lt 1e-9) `
            'Mosaic cell value was not the positive residual over background As.'
    }
}
Assert ($mosaicActiveCellCount -gt 1) 'A large FE was not rasterized into all intersected mosaic cells.'
Assert ($mosaicIds.Count -eq 1 -and $mosaicIds.Contains(2001)) `
    'Mosaic IDs counted one FE more than once or included a non-positive / other-level FE.'
Assert ($mosaicGrid.Nx -lt 20 -and $mosaicGrid.Ny -lt 20) `
    'Mosaic bounds included finite elements from a different elevation.'
Assert ($mosaicGrid.PlateCentroids.ContainsKey(2001) -and
    -not $mosaicGrid.PlateCentroids.ContainsKey(2003)) `
    'Mosaic finite-element lookup contains an element from another elevation.'
$neighborFlags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$neighborMethod = [LiraSlabZones.Core.ZoneLayoutEngine].GetMethod('OrderedNeighbors', $neighborFlags)
Assert ($null -ne $neighborMethod) 'Directional mosaic traversal helper was not found.'
$xNeighbors = @($neighborMethod.Invoke($null, [object[]]@(1, 1, [LiraSlabZones.Core.ZoneDirection]::X)))
$yNeighbors = @($neighborMethod.Invoke($null, [object[]]@(1, 1, [LiraSlabZones.Core.ZoneDirection]::Y)))
Assert ($xNeighbors.Count -eq 4 -and $xNeighbors[0].Item1 -eq 0 -and
    $xNeighbors[1].Item1 -eq 2 -and $xNeighbors[2].Item2 -eq 2 -and
    $xNeighbors[3].Item2 -eq 0) 'X layout did not visit horizontal neighbors first.'
Assert ($yNeighbors.Count -eq 4 -and $yNeighbors[0].Item2 -eq 2 -and
    $yNeighbors[1].Item2 -eq 0 -and $yNeighbors[2].Item1 -eq 0 -and
    $yNeighbors[3].Item1 -eq 2) 'Y layout did not visit vertical neighbors top-to-bottom first.'
Write-Host 'PASS level-scoped mosaic preserves positive residuals and unique FE identities'

# Geometry cache returns defensive copies and spatial index limits candidate lookup.
$geometryA = [LiraSlabZones.Core.SlabGeometryCache]::Get($trianglePair)
$geometryB = [LiraSlabZones.Core.SlabGeometryCache]::Get($trianglePair)
Assert (-not [object]::ReferenceEquals($geometryA.Item1, $geometryB.Item1)) 'Geometry cache reused the mutable outline list.'
Assert (-not [object]::ReferenceEquals($geometryA.Item1[0], $geometryB.Item1[0])) 'Geometry cache reused mutable outline points.'
$spatial = [LiraSlabZones.Core.ZoneSpatialIndex]::new(1.0)
$spatial.Add(10, 0, 1, 0, 1)
$spatial.Add(20, 5, 6, 5, 6)
Assert (@($spatial.Query(-0.1, 1.1, -0.1, 1.1)).Count -eq 1) 'Spatial index returned a distant candidate.'
Write-Host 'PASS immutable slab geometry cache and spatial candidate lookup'

$noZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$diagnostics = [LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate($trianglePair, $noZones, $approxSettings)
Assert ($diagnostics.UncoveredCount -eq 2) 'Diagnostics did not report both uncovered colored FE.'
Write-Host 'PASS diagnostics reports uncovered finite elements'

# Три одинаковые соосные зоны с промежутком в один шаг объединяются в одну.
$aligned = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$aligned.Add((New-PolygonPlate 401 @(@(1.0,0.4), @(2.0,0.4), @(2.0,0.8), @(1.0,0.8)) $activeAs3))
$aligned.Add((New-PolygonPlate 402 @(@(1.0,1.0), @(2.0,1.0), @(2.0,1.4), @(1.0,1.4)) $activeAs3))
$aligned.Add((New-PolygonPlate 403 @(@(1.0,1.6), @(2.0,1.6), @(2.0,2.0), @(1.0,2.0)) $activeAs3))
$savedMinWidth = $approxSettings.MinZoneWidthM
$approxSettings.MinZoneWidthM = 0
$alignedZones = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout($aligned, $approxSettings, $null, $testOutline, $null)
$approxSettings.MinZoneWidthM = $savedMinWidth
Assert ($alignedZones.Count -eq 1) "Compatible aligned zones were not merged: $($alignedZones.Count)."
Assert ($alignedZones[0].NodeIds.Count -eq 3) 'Merged aligned zone lost colored FE.'
Write-Host 'PASS compatible aligned zones: 3 zones -> 1'

$tempJson = Join-Path ([IO.Path]::GetTempPath()) ('slab-regression-' + [guid]::NewGuid() + '.json')
try {
    $upper = [LiraSlabZones.Core.DemoSlabFactory]::Create($legacyDemoSettings)
    foreach ($plate in $upper.Plates) {
        $plate.Centroid.Z += 3
        foreach ($point in $plate.Contour) { $point.Z = 6 }
    }
    $rebuilt.AllPlates.AddRange($upper.Plates)
    $rebuilt.AvailableLevels = [LiraSlabZones.Core.MeshBoundary]::CollectLevels($rebuilt.AllPlates, $null)
    [LiraSlabZones.Core.SlabZoneAnalyzer]::SaveAllPlatesJson($rebuilt, $tempJson)
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($tempJson, $demo.Settings)
    Assert ($loaded.Settings.GridCellMm -eq 1000) 'All-plates import did not detect the FE cell size.'
    Assert ($loaded.Zones.Count -gt 0) 'All-plates import did not calculate zones.'
    Assert ($loaded.AllPlates.Count -eq 192) 'All-plates import lost another level.'
    Assert ($loaded.AvailableLevels.Count -eq 2) 'All-plates import lost level metadata.'
    Assert ($loaded.Settings.BgBottomDiameterMm -eq 12) 'Import reset the background.'
    $other = [LiraSlabZones.Core.SlabZoneAnalyzer]::RebuildForElevation($loaded, 6, $loaded.Settings)
    Assert ($other.Zones.Count -gt 0) 'Changing level lost zones.'
    $blocked = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($tempJson)
    Assert ($blocked.Zones.Count -eq 0) 'Import bypassed required layout settings.'
    $importedZoneCount = $loaded.Zones.Count
    [LiraSlabZones.Core.SlabZoneAnalyzer]::SaveJson($loaded, $tempJson)
    $roundTrip = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($tempJson)
    Assert ($roundTrip.Zones.Count -eq $importedZoneCount) 'Zones JSON round trip lost zones.'
    Write-Host 'PASS JSON imports, settings, level switch and layout gate'
}
finally {
    Remove-Item -LiteralPath $tempJson -ErrorAction SilentlyContinue
}

if ($InputJson) {
    $settings = [LiraSlabZones.Core.AnalysisSettings]::new()
    $settings.SlabSelected = $true
    $settings.BgBottomDiameterMm = 12
    $settings.BgBottomStepMm = 200
    $settings.BgTopDiameterMm = 12
    $settings.BgTopStepMm = 200
    $settings.ConcreteClass = 'B25'
    $settings.TargetElevationZM = 55.7
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    Assert ($loaded.Zones.Count -gt 0) 'Provided JSON still has no zones.'
    Assert ([Math]::Abs($loaded.ElevationZM - 55.7) -lt 0.005) 'Wrong level selected.'
    Assert ($loaded.Settings.GridCellMm -eq 400) 'Provided JSON cell size was not detected as 400 mm.'
    $loaded.Settings.MinZoneWidthM = 0.8
    $wide = [LiraSlabZones.Core.SlabZoneAnalyzer]::RebuildForElevation($loaded, 55.7, $loaded.Settings)
    Assert ($wide.Zones.Count -gt 0) 'Minimum width removed all zones.'
    $narrowInterior = @($wide.Zones | Where-Object {
        $_.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::Straight -and $_.WidthMm -lt 799
    })
    Assert ($narrowInterior.Count -eq 0) "Interior straight zone $($narrowInterior[0].ZoneId) is narrower than the requested minimum: $($narrowInterior[0].WidthMm) mm."
    Assert (($wide.Zones | ForEach-Object { $_.NodeIds.Count } | Measure-Object -Maximum).Maximum -gt 1) 'Connected cells were not merged.'
    $as2Before = @($wide.Zones | Where-Object { $_.Layer -eq [LiraSlabZones.Core.RebarLayer]::As2 } |
        ForEach-Object { "$($_.DiameterMm):$($_.LengthMm):$($_.Placement.X):$($_.Placement.Y)" }) -join '|'
    $localSettings = [Newtonsoft.Json.JsonConvert]::DeserializeObject(
        [Newtonsoft.Json.JsonConvert]::SerializeObject($wide.Settings),
        [LiraSlabZones.Core.AnalysisSettings])
    $localSettings.ShowAs1 = -not $localSettings.ShowAs1
    $local = [LiraSlabZones.Core.SlabZoneAnalyzer]::RebuildLayers($wide, $localSettings,
        [LiraSlabZones.Core.RebarLayer[]]@([LiraSlabZones.Core.RebarLayer]::As1))
    $as2After = @($local.Zones | Where-Object { $_.Layer -eq [LiraSlabZones.Core.RebarLayer]::As2 } |
        ForEach-Object { "$($_.DiameterMm):$($_.LengthMm):$($_.Placement.X):$($_.Placement.Y)" }) -join '|'
    Assert ($as2After -eq $as2Before) 'Local As1 rebuild changed As2 zones.'
    Write-Host 'PASS local layer rebuild preserves unchanged As2 geometry'
    $loaded = $wide
    Write-Host "PASS provided JSON: $($loaded.Plates.Count) plates, $($loaded.Zones.Count) merged zones with min width 800 mm"

    $settings.ShowAs1 = $false
    $settings.ShowAs2 = $false
    $settings.ShowAs3 = $true
    $settings.ShowAs4 = $false
    $settings.ConcreteClass = 'B40'
    $settings.UseBarStep100 = $true
    $settings.DetailSlider = 1.0
    $settings.MinZoneWidthM = 1.2
    $settings.TargetElevationZM = 63.2
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    $active = @($loaded.Plates | Where-Object {
        $_.Rebar.Ok -and $_.Rebar.As3 - $loaded.Settings.AsMainAs3 -gt 0.01
    })
    $uncovered = @($active | Where-Object { -not (Test-ZoneCoverage $_ $loaded.Zones) })
    Assert ($loaded.Zones.Count -gt 1) 'Z=63.200 As3 collapsed to one zone.'
    Assert (($loaded.Zones | Where-Object {
        $_.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::Straight -and $_.WidthMm -lt 1199
    }).Count -eq 0) 'Z=63.200 has an interior straight zone narrower than 1200 mm.'
    if ($uncovered.Count -gt 0) {
        Write-Host ('Uncovered FE: ' + (($uncovered | ForEach-Object {
            $plate = $_
            $distance = ($loaded.Zones | ForEach-Object {
                $minX = ($_.Contour.X | Measure-Object -Minimum).Minimum
                $maxX = ($_.Contour.X | Measure-Object -Maximum).Maximum
                $minY = ($_.Contour.Y | Measure-Object -Minimum).Minimum
                $maxY = ($_.Contour.Y | Measure-Object -Maximum).Maximum
                $dx = [Math]::Max(0, [Math]::Max($minX - $plate.Centroid.X, $plate.Centroid.X - $maxX))
                $dy = [Math]::Max(0, [Math]::Max($minY - $plate.Centroid.Y, $plate.Centroid.Y - $maxY))
                [Math]::Sqrt($dx * $dx + $dy * $dy)
            } | Measure-Object -Minimum).Minimum
            "$($_.Id) ($($distance.ToString('0.000000')))"
        }) -join ', '))
    }
    Assert ($uncovered.Count -eq 0) 'Z=63.200 has positive As3 values outside all zones.'
    Write-Host "PASS Z=63.200 As3: $($loaded.Zones.Count) zones cover all $($active.Count) positive FE"

    $settings.ShowAs3 = $false
    $settings.ShowAs4 = $true
    $settings.DetailSlider = 0.0
    $settings.MinZoneWidthM = 0
    $settings.TargetElevationZM = 66.95
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    $active = @($loaded.Plates | Where-Object {
        $_.Rebar.Ok -and $_.Rebar.As4 - $loaded.Settings.AsMainAs4 -gt 0.01
    })
    $uncovered = @($active | Where-Object { -not (Test-ZoneCoverage $_ $loaded.Zones) })
    Assert ($active.Count -eq 68) 'Z=66.950 As4 active FE count changed.'
    Assert ($loaded.Zones.Count -gt 0) 'Z=66.950 As4 produced zero zones.'
    if ($uncovered.Count -gt 0) {
        Write-Host ('Z=66.950 uncovered: ' + (($uncovered | ForEach-Object {
            "$($_.Id) ($($_.Centroid.X.ToString('0.000')),$($_.Centroid.Y.ToString('0.000')))"
        }) -join ', '))
    }
    Assert ($uncovered.Count -eq 0) 'Z=66.950 has positive As4 values outside all non-overlapping zones.'
    Assert-ZonesInsideOutline $loaded.Zones $loaded.Outline
    Assert-ZoneRules $loaded.Zones 12
    Write-Host "PASS Z=66.950 As4 Max: $($loaded.Zones.Count) non-overlapping zones for $($active.Count) positive FE"

    $settings.ShowAs1 = $false
    $settings.ShowAs2 = $false
    $settings.ShowAs3 = $true
    $settings.ShowAs4 = $false
    $settings.DetailSlider = 0.25
    $settings.UseBarStep100 = $true
    $settings.TargetElevationZM = 48.2
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    $active = @($loaded.Plates | Where-Object {
        $_.Rebar.Ok -and $_.Rebar.As3 - $loaded.Settings.AsMainAs3 -gt 0.01
    })
    $layerZones = @($loaded.Zones | Where-Object { $_.Layer -eq [LiraSlabZones.Core.RebarLayer]::As3 })
    $uncovered = @($active | Where-Object { -not (Test-ZoneCoverage $_ $layerZones) })
    Assert ($layerZones.Count -gt 0) 'Z=48.200 As3 detail 4 produced zero zones.'
    Assert ($layerZones.Count -gt 1) 'Z=48.200 detail 4 collapsed into one oversized zone.'
    Assert-ZoneRules $layerZones 12
    Write-Host "PASS Z=48.200 As3 detail 4: $($layerZones.Count) separated zones; $($uncovered.Count) FE centroids lie in mandatory gaps"

    $settings.ShowAs1 = $false
    $settings.ShowAs2 = $true
    $settings.ShowAs3 = $false
    $settings.ShowAs4 = $false
    $settings.DetailSlider = 0
    $settings.UseBarStep100 = $false
    $settings.MinZoneWidthM = 0.4
    $settings.TargetElevationZM = 29.45
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    $active = @($loaded.Plates | Where-Object {
        $_.Rebar.Ok -and $_.Rebar.As2 - $loaded.Settings.AsMainAs2 -gt 0.01
    })
    $layerZones = @($loaded.Zones | Where-Object { $_.Layer -eq [LiraSlabZones.Core.RebarLayer]::As2 })
    $uncovered = @($active | Where-Object { -not (Test-ZoneCoverage $_ $layerZones) })
    if ($uncovered.Count -gt 0) {
        $ux = $uncovered | ForEach-Object { $_.Centroid.X } | Measure-Object -Minimum -Maximum
        $uy = $uncovered | ForEach-Object { $_.Centroid.Y } | Measure-Object -Minimum -Maximum
        Write-Host ("Z=29.450 uncovered bounds: X={0:0.000}..{1:0.000}, Y={2:0.000}..{3:0.000}; sample IDs: {4}" -f `
            $ux.Minimum, $ux.Maximum, $uy.Minimum, $uy.Maximum,
            (($uncovered | Select-Object -First 12 | ForEach-Object { $_.Id }) -join ', '))
        $knownIds = @($layerZones | ForEach-Object { $_.NodeIds } | Select-Object -Unique)
        $uncoveredKnown = @($uncovered | Where-Object { $_.Id -in $knownIds })
        Write-Host "Z=29.450 uncovered IDs referenced by zones: $($uncoveredKnown.Count)/$($uncovered.Count); zones: $($layerZones.Count)"
        $sample = $uncovered | Select-Object -First 1
        $nearby = @($layerZones | Where-Object {
            $zx = $_.Contour.X | Measure-Object -Minimum -Maximum
            $zy = $_.Contour.Y | Measure-Object -Minimum -Maximum
            $sample.Centroid.X -ge $zx.Minimum - 1 -and $sample.Centroid.X -le $zx.Maximum + 1
        } | ForEach-Object {
            $zx = $_.Contour.X | Measure-Object -Minimum -Maximum
            $zy = $_.Contour.Y | Measure-Object -Minimum -Maximum
            "#$($_.ZoneId) $($_.FamilyKind) L=$($_.LengthMm) X=$($zx.Minimum)..$($zx.Maximum) Y=$($zy.Minimum)..$($zy.Maximum)"
        })
        Write-Host ('Nearby zones: ' + ($nearby -join '; '))
    }
    Assert ($uncovered.Count -eq 0) "Z=29.450 As2 has $($uncovered.Count) colored FE without zones."
    $bentEdgeAs2 = @($layerZones | Where-Object {
        $_.FamilyKind -ne [LiraSlabZones.Core.ZoneFamilyKind]::Straight -and
        $_.CountBars -and -not $_.CountInSpec
    })
    Assert ($bentEdgeAs2.Count -gt 0) 'As2 has no SUM-31...SUM-34 zones at the slab edge.'
    Assert-ZonesInsideOutline $layerZones $loaded.Outline
    Assert-ZoneRules $layerZones 12
    Write-Host "PASS Z=29.450 As2 Max: $($layerZones.Count) zones cover all $($active.Count) colored FE; $($bentEdgeAs2.Count) bent edge zones"

    $settings.ReverseZoneDirections = $true
    $reversedAs2 = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    $reversedZones = @($reversedAs2.Zones | Where-Object { $_.Layer -eq [LiraSlabZones.Core.RebarLayer]::As2 })
    $reversedActive = @($reversedAs2.Plates | Where-Object {
        $_.Rebar.Ok -and $_.Rebar.As2 - $reversedAs2.Settings.AsMainAs2 -gt 0.01
    })
    $reversedUncovered = @($reversedActive | Where-Object { -not (Test-ZoneCoverage $_ $reversedZones) })
    if ($reversedUncovered.Count -gt 0) {
        Write-Host ('Reversed uncovered: ' + (($reversedUncovered | ForEach-Object {
            "$($_.Id) ($($_.Centroid.X.ToString('0.000')),$($_.Centroid.Y.ToString('0.000')))"
        }) -join ', '))
    }
    Assert ($reversedZones.Count -gt 0) 'Reversed Z=29.450 As2 produced no zones.'
    Assert (($reversedZones | Where-Object { $_.Direction -ne $xDirection }).Count -eq 0) `
        'Reversed Z=29.450 As2 contains a zone not directed along X.'
    Assert ($reversedUncovered.Count -eq 0) `
        "Reversed Z=29.450 As2 has $($reversedUncovered.Count) uncovered FE."
    Assert ($reversedAs2.Diagnostics.EmptyZoneCount -eq 0) `
        "Reversed Z=29.450 As2 has $($reversedAs2.Diagnostics.EmptyZoneCount) empty zones."
    $closeEndPairs = 0
    for ($i = 0; $i -lt $reversedZones.Count; $i++) {
        for ($j = $i + 1; $j -lt $reversedZones.Count; $j++) {
            $a = $reversedZones[$i]; $b = $reversedZones[$j]
            $ax = $a.Contour.X | Measure-Object -Minimum -Maximum
            $ay = $a.Contour.Y | Measure-Object -Minimum -Maximum
            $bx = $b.Contour.X | Measure-Object -Minimum -Maximum
            $by = $b.Contour.Y | Measure-Object -Minimum -Maximum
            $overlapY = [Math]::Min($ay.Maximum,$by.Maximum)-[Math]::Max($ay.Minimum,$by.Minimum)
            $gapX = [Math]::Max(0,[Math]::Max($ax.Minimum,$bx.Minimum)-[Math]::Min($ax.Maximum,$bx.Maximum))
            $required = [Math]::Min($a.BarStepMm,$b.BarStepMm)/1000.0
            if ($overlapY -gt 0.000001 -and $gapX -gt 0 -and $gapX -lt $required - 0.000001) { $closeEndPairs++ }
        }
    }
    Assert ($closeEndPairs -eq 0) "Reversed As2 has $closeEndPairs end-to-end pairs closer than their bar spacing."
    $manualReferenceBands = @($reversedZones | Where-Object {
        $zx = $_.Contour.X | Measure-Object -Minimum -Maximum
        $zy = $_.Contour.Y | Measure-Object -Minimum -Maximum
        $_.DiameterMm -eq 20 -and $_.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::Straight -and
        $zx.Maximum -gt 20 -and $zx.Minimum -lt 26 -and
        $zy.Maximum -gt 29 -and $zy.Minimum -lt 37
    } | Sort-Object { ($_.Contour.Y | Measure-Object -Minimum).Minimum })
    Assert ($manualReferenceBands.Count -ge 4) `
        "Manual-reference opening region collapsed to $($manualReferenceBands.Count) bands instead of at least four."
    foreach ($band in $manualReferenceBands) {
        $roundedLength = [int][Math]::Round($band.LengthMm)
        Assert ($roundedLength -in [LiraSlabZones.Core.RebarTables]::Sum3FamilyLengthsMm) `
            "Manual-reference zone $($band.ZoneId) uses non-standard SUM-30 length $roundedLength mm."
        $widthRemainder = [Math]::Abs($band.WidthMm % $band.BarStepMm)
        Assert ($widthRemainder -le 1 -or [Math]::Abs($band.BarStepMm - $widthRemainder) -le 1) `
            "Manual-reference zone $($band.ZoneId) width $($band.WidthMm) is not a multiple of step $($band.BarStepMm)."
    }
    Write-Host "PASS manual-reference topology: $($manualReferenceBands.Count) bands with calculated standard lengths and step-multiple widths"
    Write-Host "PASS reversed Z=29.450 As2: $($reversedZones.Count) X-directed zones cover all FE without empty zones"

}

function Render-Preview($result) {
    $viewport = [LiraSlabZones.Revit2023.UI.PreviewViewport]::new()
    $viewport.Width = 1000
    $viewport.Height = 700
    $viewport.Measure([Windows.Size]::new(1000, 700))
    $viewport.Arrange([Windows.Rect]::new(0, 0, 1000, 700))
    $viewport.SetData($result, $result.Settings, $true, $false, $false, $true)
    $viewport.UpdateLayout()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(1000, 700, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($viewport)
    return $bitmap
}

$withZones = Render-Preview $loaded
$undoViewport = [LiraSlabZones.Revit2023.UI.PreviewViewport]::new()
$undoViewport.SetData($loaded, $loaded.Settings, $true, $false)
$selectedField = $undoViewport.GetType().GetField('_selectedZoneId', [Reflection.BindingFlags]'NonPublic,Instance')
$selectedField.SetValue($undoViewport, $loaded.Zones[0].ZoneId)
$originalFamily = $loaded.Zones[0].FamilyFileName
for ($editIndex = 1; $editIndex -le 51; $editIndex++) {
    Assert ($undoViewport.SetSelectedFamily([LiraSlabZones.Core.ZoneFamilyKind]::Straight, "UndoTest-$editIndex")) 'Undo setup edit failed.'
}
for ($editIndex = 0; $editIndex -lt 50; $editIndex++) {
    Assert ($undoViewport.UndoLastEdit()) "Undo action $editIndex failed."
}
Assert ($loaded.Zones[0].FamilyFileName -eq 'UndoTest-1') 'Fifty-level undo did not retain the oldest edit.'
Assert (-not $undoViewport.UndoLastEdit()) 'Undo exceeded its fifty-action limit.'
$loaded.Zones[0].FamilyFileName = $originalFamily
Write-Host 'PASS undo restores at most fifty manual edits'
$edgeZone = $loaded.Zones[0]
$originalZones = $loaded.Zones
$loaded.Zones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$loaded.Zones.Add($edgeZone)
$edgeViewport = [LiraSlabZones.Revit2023.UI.PreviewViewport]::new()
$edgeViewport.Width = 1000; $edgeViewport.Height = 700
$edgeViewport.Measure([Windows.Size]::new(1000, 700))
$edgeViewport.Arrange([Windows.Rect]::new(0, 0, 1000, 700))
$edgeViewport.SetData($loaded, $loaded.Settings, $true, $false)
$flags = [Reflection.BindingFlags]'NonPublic,Instance'
$bounds = $edgeZone.Contour
$minX = ($bounds.X | Measure-Object -Minimum).Minimum
$maxX = ($bounds.X | Measure-Object -Maximum).Maximum
$midY = (($bounds.Y | Measure-Object -Minimum).Minimum + ($bounds.Y | Measure-Object -Maximum).Maximum) / 2
$hitMethod = $edgeViewport.GetType().GetMethod('HitResizeEdge', $flags)
$hit = $hitMethod.Invoke($edgeViewport, @([LiraSlabZones.Core.Point3]::new($maxX, $midY, $edgeZone.LevelZM)))
Assert ($hit.Item1 -eq $edgeZone -and $hit.Item2.ToString() -eq 'Right') 'Right zone edge was not detected for dragging.'
$edgeViewport.GetType().GetMethod('BeginEdit', $flags).Invoke($edgeViewport, @())
$edgeField = $edgeViewport.GetType().GetField('_resizeEdge', $flags)
$edgeField.SetValue($edgeViewport, [Enum]::Parse($edgeField.FieldType, 'Right'))
$boundsField = $edgeViewport.GetType().GetField('_resizeBounds', $flags)
$minY = ($bounds.Y | Measure-Object -Minimum).Minimum
$maxY = ($bounds.Y | Measure-Object -Maximum).Maximum
$boundsField.SetValue($edgeViewport, [ValueTuple[double,double,double,double]]::new($minX,$maxX,$minY,$maxY))
$dragMethod = $edgeViewport.GetType().GetMethod('ResizeDraggedEdge', $flags)
Assert ($dragMethod.Invoke($edgeViewport, @($edgeZone, [LiraSlabZones.Core.Point3]::new($maxX - 0.1, $midY, $edgeZone.LevelZM)))) 'Dragging right edge failed.'
Assert ([Math]::Abs((($edgeZone.Contour.X | Measure-Object -Minimum).Minimum) - $minX) -lt 0.001) 'Dragging right edge moved the opposite edge.'
$loaded.Zones = $originalZones
Write-Host 'PASS dragging a zone edge keeps the opposite edge fixed'
$savedZones = $loaded.Zones
$loaded.Zones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$withoutZones = Render-Preview $loaded
$loaded.Zones = $savedZones
$a = New-Object byte[] (1000 * 700 * 4)
$b = New-Object byte[] $a.Length
$withZones.CopyPixels($a, 4000, 0)
$withoutZones.CopyPixels($b, 4000, 0)
$changed = 0
for ($i = 0; $i -lt $a.Length; $i += 4) {
    if ($a[$i] -ne $b[$i] -or $a[$i+1] -ne $b[$i+1] -or $a[$i+2] -ne $b[$i+2]) { $changed++ }
}
Assert ($changed -gt 100) 'Zones produce no visible pixels in the WPF viewport.'
$imagePath = Join-Path ([IO.Path]::GetTempPath()) 'LiraSlabZones-preview-regression.png'
$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($withZones))
$stream = [IO.File]::Create($imagePath)
try { $encoder.Save($stream) } finally { $stream.Dispose() }
Write-Host "PASS WPF rendering: $changed changed pixels; $imagePath"
