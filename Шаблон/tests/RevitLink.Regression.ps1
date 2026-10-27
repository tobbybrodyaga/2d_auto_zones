param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BuildDirectory = '',
    # Реальная плита: JSON программы (slab_zones.json) и выгрузка перекрытий/осей уровня Revit.
    [string]$RealSlabJson = '',
    [string]$RealRevitJson = ''
)

# Автотесты связи с Revit: совмещение ЛИРА↔Revit по перекрытиям и привязка зон
# к осям Revit с кратностью 10 мм (цепочки, отступ от края, узкие места).

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

$failures = 0
function Check([bool]$condition, [string]$message) {
    if ($condition) { Write-Host "  ok   $message" }
    else { Write-Host "  FAIL $message" -ForegroundColor Red; $script:failures++ }
}

function P([double]$x, [double]$y) { [LiraSlabZones.Core.Point3]::new($x, $y, 0) }

function Points([double[][]]$xy) {
    $list = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
    foreach ($p in $xy) { $list.Add((P $p[0] $p[1])) }
    return ,$list
}

function New-Zone([double]$minX, [double]$maxX, [double]$minY, [double]$maxY, [string]$layer = 'As1') {
    $zone = [LiraSlabZones.Core.AdditionalZone]::new()
    $zone.Layer = [LiraSlabZones.Core.RebarLayer]::$layer
    $zone.Contour = Points @(@($minX, $minY), @($maxX, $minY), @($maxX, $maxY), @($minX, $maxY))
    $zone.Placement = P $minX $minY
    return $zone
}

function Axis([string]$name, [bool]$vertical, [double]$pos) {
    $a = [LiraSlabZones.Core.AlignAxis]::new()
    $a.Name = $name
    $a.Vertical = $vertical
    if ($vertical) { $a.X1 = $pos; $a.X2 = $pos; $a.Y1 = -100; $a.Y2 = 100 }
    else { $a.Y1 = $pos; $a.Y2 = $pos; $a.X1 = -100; $a.X2 = 100 }
    return $a
}

function List($items, [type]$type) {
    $list = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($type))
    foreach ($i in $items) { $list.Add($i) }
    return ,$list
}

function MinX($zone) { ($zone.Contour | Measure-Object X -Minimum).Minimum }
function MaxX($zone) { ($zone.Contour | Measure-Object X -Maximum).Maximum }
function MinY($zone) { ($zone.Contour | Measure-Object Y -Minimum).Minimum }
function MaxY($zone) { ($zone.Contour | Measure-Object Y -Maximum).Maximum }
function Mm([double]$m) { [Math]::Round($m * 1000, 3) }
function OnLattice([double]$m, [double]$axis) {
    $mm = ($m - $axis) * 1000
    return [Math]::Abs($mm - 10 * [Math]::Round($mm / 10)) -le 0.5
}

$Align = [LiraSlabZones.Core.RevitGridAlignment]
$noOpenings = [Collections.Generic.List[LiraSlabZones.Core.OpeningInfo]]::new()
$bigSlab = Points @(@(-20, -20), @(40, -20), @(40, 40), @(-20, 40))

Write-Host '1. Цепочка соседних зон сдвигается одним общим сдвигом'
$a = New-Zone 1.0 4.0 1.0 2.0
$b = New-Zone 1.0 4.0 2.2 3.2          # зазор 200 мм до зоны a
$c = New-Zone 10.0 13.0 1.0 2.0        # отдельная зона, далеко
$zones = List @($a, $b, $c) ([LiraSlabZones.Core.AdditionalZone])
$axes = List @((Axis '1' $true 0.003), (Axis 'А' $false 0.0071)) ([LiraSlabZones.Core.AlignAxis])
$report = $Align::Apply($zones, $axes, $bigSlab, $noOpenings, 50)
Check ((Mm (MinX $a)) -eq 1003) "левая грань a: 1003 мм от начала (кратно 10 от оси 1 на 3 мм)"
Check ((Mm (MinX $b)) -eq 1003) "цепочка: b сдвинута так же, как a"
Check ([Math]::Abs((Mm (MinY $b)) - (Mm (MaxY $a)) - 200) -lt 0.01) "зазор 200 мм между a и b не изменился"
Check (OnLattice (MinY $a) 0.0071) "низ a кратен 10 мм от оси А"
Check ($report.Chains -eq 2) "цепочек 2 (a+b и c), получено $($report.Chains)"
Check ($report.UntiedZones -eq 0) "непривязанных зон нет"
Check ($a.AxisNameX -eq '1' -and $a.OffsetFromAxisXMm -eq 1000) "привязка записана в зону: от 1 = $($a.OffsetFromAxisXMm)"

Write-Host '2. У края плиты округление внутрь: от края 50–59 мм'
$slab = Points @(@(0, 0), @(20, 0), @(20, 10), @(0, 10))
$z = New-Zone 0.050 3.050 2.0 3.0
$zones = List @($z) ([LiraSlabZones.Core.AdditionalZone])
$axes = List @((Axis '1' $true 0.006)) ([LiraSlabZones.Core.AlignAxis])
$null = $Align::Apply($zones, $axes, $slab, $noOpenings, 50)
$edge = Mm (MinX $z)
Check ($edge -ge 50 -and $edge -lt 60) "от края $edge мм (не меньше 50)"
Check (OnLattice (MinX $z) 0.006) "от оси кратно 10 мм"

Write-Host '3. Узкое место от края до края — по центру с равными зазорами'
$strip = Points @(@(0, 0), @(20, 0), @(20, 1.0), @(0, 1.0))
$n = New-Zone 2.0 5.0 0.040 0.940      # зазоры 40 и 60 мм
$zones = List @($n) ([LiraSlabZones.Core.AdditionalZone])
$axes = List @((Axis 'А' $false 0.003)) ([LiraSlabZones.Core.AlignAxis])
$report = $Align::Apply($zones, $axes, $strip, $noOpenings, 50)
Check ([Math]::Abs((Mm (MinY $n)) - 50) -lt 0.01 -and [Math]::Abs(1000 - (Mm (MaxY $n)) - 50) -lt 0.01) `
    "зазоры $(Mm (MinY $n)) и $(1000 - (Mm (MaxY $n))) мм"
Check ($report.CenteredZones -eq 1) "зона отмечена как центрированная"
Check ($Align::Check($zones, $axes, $strip, $noOpenings, 50).Count -eq 0) "центрированная зона не попадает в диагностику"

Write-Host '4. Диагностика «привязка к оси не кратна 10 мм»'
$bad = New-Zone 1.003 4.003 1.0 2.0
$zones = List @($bad) ([LiraSlabZones.Core.AdditionalZone])
$axes = List @((Axis '1' $true 0.0)) ([LiraSlabZones.Core.AlignAxis])
$issues = $Align::Check($zones, $axes, $bigSlab, $noOpenings, 50)
Check ($issues.Count -eq 1 -and $issues[0].Kind -eq [LiraSlabZones.Core.ZoneIssueKind]::AxisTie) "найдена 1 зона: $($issues[0].Message)"
$null = $Align::Apply($zones, $axes, $bigSlab, $noOpenings, 50)
Check ($Align::Check($zones, $axes, $bigSlab, $noOpenings, 50).Count -eq 0) "после привязки ошибок нет"

Write-Host '5. Совмещение ЛИРА↔Revit по контурам перекрытий (синтетика)'
$liraOutline = Points @(@(0, 0), @(30, 0), @(30, 12), @(18, 12), @(18, 20), @(0, 20))
$result = [LiraSlabZones.Core.AnalysisResult]::new()
$result.Outline = $liraOutline
$opening = [LiraSlabZones.Core.OpeningInfo]::new()
$opening.MinXM = 5; $opening.MaxXM = 7.4; $opening.MinYM = 4; $opening.MaxYM = 5.5
$result.Openings.Add($opening)
$theta = 17.3 * [Math]::PI / 180; $tx = 1234.567; $ty = -876.543
function ToRevit([double]$x, [double]$y) {
    @(([Math]::Cos($theta) * $x - [Math]::Sin($theta) * $y + $tx), ([Math]::Sin($theta) * $x + [Math]::Cos($theta) * $y + $ty))
}
$link = [LiraSlabZones.Core.RevitLinkInfo]::new()
$floor = [LiraSlabZones.Core.RevitFloorOutline]::new()
$floor.Loops.Add((Points ($liraOutline | ForEach-Object { ,(ToRevit $_.X $_.Y) })))
$floor.Loops.Add((Points (@(@(5, 4), @(7.4, 4), @(7.4, 5.5), @(5, 5.5)) | ForEach-Object { ,(ToRevit $_[0] $_[1]) })))
$link.Floors.Add($floor)
foreach ($g in @(@('1', 0, -5, 0, 25), @('2', 6, -5, 6, 25), @('А', -5, 0, 35, 0))) {
    $gl = [LiraSlabZones.Core.RevitGridLine]::new()
    $gl.Name = $g[0]
    $p1 = ToRevit $g[1] $g[2]; $p2 = ToRevit $g[3] $g[4]
    $gl.X1 = $p1[0]; $gl.Y1 = $p1[1]; $gl.X2 = $p2[0]; $gl.Y2 = $p2[1]
    $link.Grids.Add($gl)
}
$reg = [LiraSlabZones.Core.RevitRegistrationSolver]::Solve($result, $link)
Check ($null -ne $reg -and $reg.Reliable) "совмещение найдено: $($reg.Summary)"
$result.Settings.RotationDeg = $reg.RotationDeg
$result.Settings.OffsetXM = $reg.OffsetXM
$result.Settings.OffsetYM = $reg.OffsetYM
$tr = [LiraSlabZones.Core.LiraRevitTransform]::For($result, $result.Settings)
$worst = 0
foreach ($p in $liraOutline) {
    $got = $tr.ToRevit($p.X, $p.Y); $want = ToRevit $p.X $p.Y
    $worst = [Math]::Max($worst, [Math]::Sqrt([Math]::Pow($got.Item1 - $want[0], 2) + [Math]::Pow($got.Item2 - $want[1], 2)))
}
Check ($worst -lt 0.001) ("вершины контура ложатся в Revit с ошибкой {0:0.###} мм" -f ($worst * 1000))
$result.RevitLink = $link
$liraAxes = $Align::AxesInLira($result, $result.Settings)
Check ($liraAxes.Count -eq 3) "все 3 оси Revit параллельны осям плана ЛИРЫ"
$ax2 = $liraAxes | Where-Object Name -eq '2'
Check ([Math]::Abs($ax2.PositionAt(10) - 6) -lt 0.0005) ("ось 2 в координатах ЛИРЫ: X = {0:0.####}" -f $ax2.PositionAt(10))

Write-Host '5б. Плита ЛИРЫ — часть общего перекрытия Revit (блок здания)'
$wide = [LiraSlabZones.Core.RevitLinkInfo]::new()
$wideFloor = [LiraSlabZones.Core.RevitFloorOutline]::new()
# Перекрытие продолжается на 40 м вправо: правой кромки плиты ЛИРЫ в Revit нет.
$wideFloor.Loops.Add((Points (@(@(-40, 0), @(70, 0), @(70, 12), @(18, 12), @(18, 20), @(0, 20), @(0, 30), @(-40, 30)) |
    ForEach-Object { ,(ToRevit $_[0] $_[1]) })))
$wide.Floors.Add($wideFloor)
foreach ($g in $link.Grids) { $wide.Grids.Add($g) }
$block = [LiraSlabZones.Core.AnalysisResult]::new()
$block.Outline = $liraOutline
$reg = [LiraSlabZones.Core.RevitRegistrationSolver]::Solve($block, $wide)
Check ($null -ne $reg -and $reg.Reliable) "совмещение найдено: $($reg.Summary)"
if ($reg) {
    $block.Settings.RotationDeg = $reg.RotationDeg
    $block.Settings.OffsetXM = $reg.OffsetXM
    $block.Settings.OffsetYM = $reg.OffsetYM
    $tr = [LiraSlabZones.Core.LiraRevitTransform]::For($block, $block.Settings)
    $got = $tr.ToRevit(18, 20); $want = ToRevit 18 20
    $err = [Math]::Sqrt([Math]::Pow($got.Item1 - $want[0], 2) + [Math]::Pow($got.Item2 - $want[1], 2))
    Check ($err -lt 0.001) ("угол (18; 20) ложится в Revit с ошибкой {0:0.###} мм" -f ($err * 1000))
}

if ($RealSlabJson -and $RealRevitJson) {
    Write-Host '6. Реальная плита: совмещение с перекрытиями Revit и привязка к осям'
    $real = [Newtonsoft.Json.JsonConvert]::DeserializeObject((Get-Content $RealSlabJson -Raw -Encoding UTF8),
        [LiraSlabZones.Core.AnalysisResult])
    $real.Settings.RotationDeg = 0; $real.Settings.OffsetXM = 0; $real.Settings.OffsetYM = 0
    $real.RevitLink = [Newtonsoft.Json.JsonConvert]::DeserializeObject((Get-Content $RealRevitJson -Raw -Encoding UTF8),
        [LiraSlabZones.Core.RevitLinkInfo])
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $reg = [LiraSlabZones.Core.RevitRegistrationSolver]::Solve($real, $real.RevitLink)
    Check ($null -ne $reg -and $reg.Reliable) ("совмещение за {0:0.0} с: {1}" -f $sw.Elapsed.TotalSeconds, $reg.Summary)
    if ($reg) {
        $real.Settings.RotationDeg = $reg.RotationDeg
        $real.Settings.OffsetXM = $reg.OffsetXM
        $real.Settings.OffsetYM = $reg.OffsetYM
        $realAxes = $Align::AxesInLira($real, $real.Settings)
        Check ($realAxes.Count -gt 0) "осей, параллельных плану ЛИРЫ: $($realAxes.Count) из $($real.RevitLink.Grids.Count)"
        if ($real.Zones.Count -gt 0) {
            $report = $Align::Apply($real.Zones, $realAxes, $real.Outline, $real.Openings, 50)
            Check ($report.UntiedZones -eq 0) "зоны: $($report.Summary)"
        }
    }
}

if ($failures -gt 0) { Write-Host "FAIL: $failures" -ForegroundColor Red; exit 1 }
Write-Host 'PASS' -ForegroundColor Green
