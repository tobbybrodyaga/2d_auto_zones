param(
    [Parameter(Mandatory = $true)]
    [string]$InputJson,
    [double]$ElevationZM = 29.45,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BuildDirectory = '',
    [switch]$SkipCompatibleMerges
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    Join-Path $root "src\LiraSlabZones.PreviewHost\bin\x64\$Configuration\net48"
} else {
    (Resolve-Path $BuildDirectory).Path
}
Add-Type -Path (Join-Path $build 'Newtonsoft.Json.dll')
Add-Type -Path (Join-Path $build 'Clipper2Lib.dll')
Add-Type -Path (Join-Path $build 'LiraSlabZones.Core.dll')

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-CrossBounds($Contour, $Direction) {
    if ($Direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
        return @{ Min = ($Contour | ForEach-Object { $_.Y } | Measure-Object -Minimum).Minimum
                  Max = ($Contour | ForEach-Object { $_.Y } | Measure-Object -Maximum).Maximum }
    }
    return @{ Min = ($Contour | ForEach-Object { $_.X } | Measure-Object -Minimum).Minimum
              Max = ($Contour | ForEach-Object { $_.X } | Measure-Object -Maximum).Maximum }
}

function Get-AxialBounds($Contour, $Direction) {
    if ($Direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
        return @{ Min = ($Contour | ForEach-Object { $_.X } | Measure-Object -Minimum).Minimum
                  Max = ($Contour | ForEach-Object { $_.X } | Measure-Object -Maximum).Maximum }
    }
    return @{ Min = ($Contour | ForEach-Object { $_.Y } | Measure-Object -Minimum).Minimum
              Max = ($Contour | ForEach-Object { $_.Y } | Measure-Object -Maximum).Maximum }
}

function Get-CoverageCounts($Zones, $Plates, $Settings, $Outline, $Openings) {
    $counts = @{ As1Active = 0; As1Uncovered = 0; As2Active = 0; As2Uncovered = 0 }
    foreach ($layer in @([LiraSlabZones.Core.RebarLayer]::As1, [LiraSlabZones.Core.RebarLayer]::As2)) {
        $enabled = if ($layer -eq [LiraSlabZones.Core.RebarLayer]::As1) { $Settings.ShowAs1 } else { $Settings.ShowAs2 }
        if (-not $enabled) { continue }
        $background = if ($layer -eq [LiraSlabZones.Core.RebarLayer]::As1) { $Settings.AsMainAs1 } else { $Settings.AsMainAs2 }
        $layerZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
        foreach ($zone in $Zones) { if ($zone.Layer -eq $layer) { $layerZones.Add($zone) } }
        $name = if ($layer -eq [LiraSlabZones.Core.RebarLayer]::As1) { 'As1' } else { 'As2' }
        foreach ($plate in $Plates) {
            if (-not $plate.Rebar.Ok) { continue }
            $required = $plate.Rebar.Get($layer) - $background
            if ($required -le [LiraSlabZones.Core.MosaicBuilder]::PositiveResidualToleranceCm2PerM) { continue }
            $counts["${name}Active"]++
            if (-not [LiraSlabZones.Core.ZoneCoverageRules]::CoversOrBridgesGap(
                    $layerZones, $plate, $layer, $required, $Outline, $Openings)) {
                $counts["${name}Uncovered"]++
            }
        }
    }
    return $counts
}

$settings = [LiraSlabZones.Core.AnalysisSettings]::new()
$settings.SlabSelected = $true
$settings.BgBottomDiameterMm = 12
$settings.BgBottomStepMm = 200
$settings.BgTopDiameterMm = 12
$settings.BgTopStepMm = 200
$settings.ShowAs1 = $true
$settings.ShowAs2 = $true
$settings.ShowAs3 = $false
$settings.ShowAs4 = $false
$settings.ConcreteClass = 'B40'
$settings.TargetElevationZM = $ElevationZM
$settings.GridCellMm = 400
$settings.DetailSlider = 0
$settings.UseBarStep100 = $true
$settings.SyncBackgroundAsFromBars()

$result = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
Assert ($result.PatchPreviewOnly) 'The input did not load in patch preview mode.'
$settings = $result.Settings
$platesById = @{}
foreach ($plate in $result.Plates) {
    if (-not $platesById.ContainsKey($plate.Id)) { $platesById[$plate.Id] = $plate }
}

$frames = [Collections.Generic.List[object]]::new()
foreach ($patch in $result.Patches) {
    $frames.Add([pscustomobject]@{
        MinX = $patch.MinXM; MaxX = $patch.MaxXM; MinY = $patch.MinYM; MaxY = $patch.MaxYM
        Patches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
    })
    $frames[$frames.Count - 1].Patches.Add($patch)
}
$merged = $true
while ($merged) {
    $merged = $false
    for ($i = 0; $i -lt $frames.Count -and -not $merged; $i++) {
        for ($j = $i + 1; $j -lt $frames.Count; $j++) {
            $a = $frames[$i]
            $b = $frames[$j]
            if ($a.MinX -ge $b.MaxX -or $a.MaxX -le $b.MinX -or
                $a.MinY -ge $b.MaxY -or $a.MaxY -le $b.MinY) { continue }
            $a.Patches.AddRange($b.Patches)
            $a.MinX = [Math]::Min($a.MinX, $b.MinX)
            $a.MaxX = [Math]::Max($a.MaxX, $b.MaxX)
            $a.MinY = [Math]::Min($a.MinY, $b.MinY)
            $a.MaxY = [Math]::Max($a.MaxY, $b.MaxY)
            $frames.RemoveAt($j)
            $merged = $true
            break
        }
    }
}

$zones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$sourceBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$outerBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$partitionCount = 0
foreach ($frame in $frames) {
    $frameElements = [Collections.Generic.List[LiraSlabZones.Core.ZonePatchFrameElement]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new()
    foreach ($patch in $frame.Patches) {
        foreach ($id in $patch.ElementIds) {
            if (-not $platesById.ContainsKey($id)) { continue }
            $plate = $platesById[$id]
            if (-not $plate.Rebar.Ok -or $plate.Contour.Count -lt 3) { continue }
            $background = if ($patch.Layer -eq [LiraSlabZones.Core.RebarLayer]::As1) { $settings.AsMainAs1 } else { $settings.AsMainAs2 }
            $additional = $plate.Rebar.Get($patch.Layer) - $background
            $key = "$($patch.Layer):$id"
            if ($additional -le [LiraSlabZones.Core.MosaicBuilder]::PositiveResidualToleranceCm2PerM -or
                -not $seen.Add($key)) { continue }
            $element = [LiraSlabZones.Core.ZonePatchFrameElement]::new()
            $element.ElementId = $id
            $element.Layer = $patch.Layer
            $element.AsAdditionalCm2PerM = $additional
            $element.Contour = $plate.Contour
            $frameElements.Add($element)
        }
    }
    $partitions = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
        $frame.Patches, $frame.MinX, $frame.MaxX, $frame.MinY, $frame.MaxY,
        $settings.EffectiveMinZoneWidthM, $frameElements)
    foreach ($partition in $partitions) {
        $selection = [LiraSlabZones.Core.ZonePatchFrameSelection]::new()
        $selection.Patches = $frame.Patches
        $selection.Elements = $frameElements
        $selection.SlabOutline = $result.Outline
        $selection.MinXM = $partition.MinX
        $selection.MaxXM = $partition.MaxX
        $selection.MinYM = $partition.MinY
        $selection.MaxYM = $partition.MaxY
        foreach ($zone in [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build(
                     $selection, $result.ElevationZM, $settings, $sourceBounds, $outerBounds)) {
            $zones.Add($zone)
        }
        $partitionCount++
    }
}

if (-not $SkipCompatibleMerges) {
    [void][LiraSlabZones.Core.ZonePatchZoneBuilder]::MergeAdjacentCompatibleZones($zones)
    [void][LiraSlabZones.Core.ZonePatchZoneBuilder]::MergeShiftableAdjacentZonesAlongBars(
        $zones, $sourceBounds, $settings)
    [void][LiraSlabZones.Core.ZonePatchZoneBuilder]::MergeAdjacentCompatibleZones($zones)
}
$coverageBefore = Get-CoverageCounts $zones $result.Plates $settings $result.Outline $result.Openings
$layout = [LiraSlabZones.Core.ZonePatchNeighborLayout]::Apply(
    $zones, $sourceBounds, $outerBounds, $result.Plates, $settings, $result.Outline, $result.Openings)
$coverageAfter = Get-CoverageCounts $zones $result.Plates $settings $result.Outline $result.Openings

$nonMultipleWidths = @($zones | Where-Object {
    $_.BarStepMm -le 0 -or [Math]::Abs($_.WidthMm - [Math]::Round($_.WidthMm / $_.BarStepMm) * $_.BarStepMm) -gt 0.01
})
$longBars = @($zones | Where-Object { $_.LengthMm -gt 11700.01 })
$crossOverlaps = 0
$spacingConflicts = 0
$overlapExamples = [Collections.Generic.List[string]]::new()
foreach ($i in 0..([Math]::Max(0, $zones.Count - 1))) {
    if ($i -ge $zones.Count) { continue }
    $a = $zones[$i]
    $aCross = Get-CrossBounds $a.Contour $a.Direction
    $aAxial = Get-AxialBounds $a.Contour $a.Direction
    for ($j = $i + 1; $j -lt $zones.Count; $j++) {
        $b = $zones[$j]
        if ($a.Layer -ne $b.Layer -or $a.Direction -ne $b.Direction) { continue }
        $bCross = Get-CrossBounds $b.Contour $b.Direction
        $bAxial = Get-AxialBounds $b.Contour $b.Direction
        if ([Math]::Min($aAxial.Max, $bAxial.Max) -le [Math]::Max($aAxial.Min, $bAxial.Min) + 1e-6) { continue }
        if ([Math]::Abs((($aCross.Min + $aCross.Max) - ($bCross.Min + $bCross.Max)) / 2) -lt 1e-6) { continue }
        $gap = if ($aCross.Max -le $bCross.Min) { $bCross.Min - $aCross.Max } elseif ($bCross.Max -le $aCross.Min) { $aCross.Min - $bCross.Max } else { -[Math]::Min($aCross.Max, $bCross.Max) + [Math]::Max($aCross.Min, $bCross.Min) }
        if ($gap -lt -1e-5) {
            $crossOverlaps++
            if ($overlapExamples.Count -lt 20) {
                $aSupport = if ($sourceBounds.ContainsKey($a)) { Get-CrossBounds @(
                    [LiraSlabZones.Core.Point3]::new($sourceBounds[$a].MinX, $sourceBounds[$a].MinY, 0),
                    [LiraSlabZones.Core.Point3]::new($sourceBounds[$a].MaxX, $sourceBounds[$a].MaxY, 0)) $a.Direction } else { $null }
                $bSupport = if ($sourceBounds.ContainsKey($b)) { Get-CrossBounds @(
                    [LiraSlabZones.Core.Point3]::new($sourceBounds[$b].MinX, $sourceBounds[$b].MinY, 0),
                    [LiraSlabZones.Core.Point3]::new($sourceBounds[$b].MaxX, $sourceBounds[$b].MaxY, 0)) $b.Direction } else { $null }
                $overlapExamples.Add(("{0}:{1} Ø{2}/{3} As{4:0.##} / {5}:{6} Ø{7}/{8} As{9:0.##}; layer={10} dir={11}; cross={12:0.###}..{13:0.###} vs {14:0.###}..{15:0.###}; supports={16:0.###}..{17:0.###} vs {18:0.###}..{19:0.###}; axial={20:0.###}..{21:0.###} vs {22:0.###}..{23:0.###}" -f `
                    $i, $a.ZoneId, $a.DiameterMm, $a.BarStepMm, $a.AsCoveredCm2PerM,
                    $j, $b.ZoneId, $b.DiameterMm, $b.BarStepMm, $b.AsCoveredCm2PerM,
                    $a.Layer, $a.Direction,
                    $aCross.Min, $aCross.Max, $bCross.Min, $bCross.Max,
                    $aSupport.Min, $aSupport.Max, $bSupport.Min, $bSupport.Max,
                    $aAxial.Min, $aAxial.Max, $bAxial.Min, $bAxial.Max))
            }
        }
        elseif ($sourceBounds.ContainsKey($a) -and $sourceBounds.ContainsKey($b)) {
            $aSupport = Get-CrossBounds @(
                [LiraSlabZones.Core.Point3]::new($sourceBounds[$a].MinX, $sourceBounds[$a].MinY, 0),
                [LiraSlabZones.Core.Point3]::new($sourceBounds[$a].MaxX, $sourceBounds[$a].MaxY, 0)) $a.Direction
            $bSupport = Get-CrossBounds @(
                [LiraSlabZones.Core.Point3]::new($sourceBounds[$b].MinX, $sourceBounds[$b].MinY, 0),
                [LiraSlabZones.Core.Point3]::new($sourceBounds[$b].MaxX, $sourceBounds[$b].MaxY, 0)) $b.Direction
            $supportGap = [Math]::Max(0, [Math]::Max($aSupport.Min, $bSupport.Min) -
                [Math]::Min($aSupport.Max, $bSupport.Max))
            $requiredGap = [Math]::Min($a.BarStepMm, $b.BarStepMm) / 1000.0
            if ($supportGap -le $requiredGap + 1e-6 -and
                [Math]::Abs($gap - $requiredGap) -gt 1e-5) { $spacingConflicts++ }
        }
    }
}

$report = [pscustomobject]@{
    Input = $InputJson
    ElevationZM = $result.ElevationZM
    GridCellMm = $settings.GridCellMm
    Plates = $result.Plates.Count
    Patches = $result.Patches.Count
    Frames = $frames.Count
    Partitions = $partitionCount
    ZonesAfterMerges = $zones.Count
    NormalizedWidths = $layout.NormalizedWidths
    ShiftedZones = $layout.ShiftedZones
    ShrunkWeakZones = $layout.ShrunkWeakZones
    ShrunkConflictZones = $layout.ShrunkConflictZones
    TrimmedOverhangZones = $layout.TrimmedOverhangZones
    TransferredCoverageElements = $layout.TransferredCoverageElements
    ExtendedZones = $layout.ExtendedZones
    UnresolvedWidths = $layout.UnresolvedWidths
    UnresolvedPairs = $layout.UnresolvedPairs
    ResidualIntersections = $layout.ResidualIntersections
    ResidualSpacingConflicts = $layout.ResidualSpacingConflicts
    RolledBackForCoverage = $layout.RolledBackForCoverage
    CoverageBefore = $coverageBefore
    CoverageAfter = $coverageAfter
    WidthsNotMultiple = $nonMultipleWidths.Count
    CrossWidthOverlaps = $crossOverlaps
    ExactSpacingViolations = $spacingConflicts
    BarsOver11700 = $longBars.Count
    Warning = $layout.Warning
}
$report | Format-List
if ($layout.PriorityDetails.Count -gt 0) {
    Write-Host 'Priority comparisons:'
    $layout.PriorityDetails | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
}
if ($layout.ResidualDetails.Count -gt 0) {
    Write-Host 'Residual conflict move checks:'
    $layout.ResidualDetails | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
}
if ($layout.UnresolvedDetails.Count -gt 0) {
    Write-Host 'First unsolved groups:'
    $layout.UnresolvedDetails | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
}
if ($layout.CoverageLossDetails.Count -gt 0) {
    Write-Host 'First coverage losses before rollback:'
    $layout.CoverageLossDetails | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
}
if ($overlapExamples.Count -gt 0) {
    Write-Host 'First final cross-width overlaps:'
    $overlapExamples | ForEach-Object { Write-Host "  $_" }
}

Assert ($coverageAfter.As1Uncovered -le $coverageBefore.As1Uncovered -and
        $coverageAfter.As2Uncovered -le $coverageBefore.As2Uncovered) `
    'The neighbor layout reduced finite element coverage compared with the unmodified candidate zones.'
Assert ($nonMultipleWidths.Count -eq 0) 'Some final zone widths are not integer multiples of their bar step.'
Assert ($crossOverlaps -eq 0) 'Cross-width reinforcement zone overlaps remain after layout and compatible merges.'
Assert ($longBars.Count -eq 0) 'A bar exceeds the preserved 11700 mm maximum length.'
