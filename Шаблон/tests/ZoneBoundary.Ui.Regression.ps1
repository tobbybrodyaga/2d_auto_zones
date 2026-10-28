param([string]$BuildDirectory = '')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if (!$BuildDirectory) {$BuildDirectory=Join-Path $root 'src\LiraSlabZones.PreviewHost\bin\x64\Release\net48'}
Add-Type -AssemblyName PresentationFramework
@('Newtonsoft.Json.dll','Clipper2Lib.dll','LiraSlabZones.Core.dll') | ForEach-Object {Add-Type -Path (Join-Path $BuildDirectory $_)}
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $BuildDirectory 'LiraSlabZones.PreviewHost.exe'))
$type=$assembly.GetType('LiraSlabZones.Revit2023.UI.ZonePreviewWindow')
$window=[Activator]::CreateInstance($type)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
function Rect([double]$x1,[double]$x2,[double]$y1,[double]$y2) {
    $points=[Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
    foreach($xy in @(@($x1,$y1),@($x2,$y1),@($x2,$y2),@($x1,$y2))) {
        $points.Add([LiraSlabZones.Core.Point3]::new($xy[0],$xy[1],0))
    }
    return ,$points
}
try {
    $result=[LiraSlabZones.Core.AnalysisResult]::new()
    $result.DocumentName='Boundary test'
    $result.PatchPreviewOnly=$true
    $result.Outline=Rect 0 20 0 10
    $result.Settings.ShowAs2=$false
    $result.Settings.BgBottomDiameterMm=8
    $result.Settings.BgBottomStepMm=200
    $result.Settings.BgTopDiameterMm=8
    $result.Settings.BgTopStepMm=200
    $result.Settings.SlabSelected=$true
    $result.Settings.ConcreteClass='B30'
    # Граница/загибы выключены, связь с Revit отсутствует: preflight всё равно обязателен.
    $result.Settings.ApplySlabBoundaryAndOpeningRules=$false
    $plate=[LiraSlabZones.Core.LiraPlateElement]::new()
    $plate.Id=1
    $plate.Contour=Rect 2 3 9.92 9.99
    $plate.Centroid=[LiraSlabZones.Core.Point3]::new(2.5,9.955,0)
    $plate.Rebar.Ok=$true
    $plate.Rebar.As1=6
    $result.Plates.Add($plate)
    $result.PlateCount=1
    $zone=[LiraSlabZones.Core.AdditionalZone]::new()
    $zone.ZoneId=1
    $zone.NodeIds.Add(1)
    $zone.Contour=Rect 1.5 3.5 9.9 10.1
    $zone.Direction=[LiraSlabZones.Core.ZoneDirection]::X
    $zone.Layer=[LiraSlabZones.Core.RebarLayer]::As1
    $zone.DiameterMm=12
    $zone.BarStepMm=200
    $zone.BarCount=2
    $zone.LengthMm=2000
    $zone.WidthMm=200
    $zone.AsCoveredCm2PerM=6
    $result.Zones.Add($zone)
    $script:called=$false
    $callback=[Action[LiraSlabZones.Core.AnalysisResult]]{param($value) $script:called=$true}
    $window.SetPlaceCallback($callback)
    $window.LoadResult($result,$true,$true)
    if (@($result.Diagnostics.Issues | Where-Object Kind -eq BoundaryClearance).Count -ne 1) {
        throw 'Saved zone boundary error not shown without Revit/boundary toggle'
    }
    $null=$type.GetMethod('BtnPlace_Click',$flags).Invoke($window,@($null,$null))
    if ($script:called) {throw 'Invalid zone reached Revit callback'}
    $zone.Contour=Rect 1.5 3.5 9.7 9.9
    $null=$type.GetMethod('BtnPlace_Click',$flags).Invoke($window,@($null,$null))
    if (!$script:called) {throw 'Valid corrected zone was blocked'}
    $script:called=$false
    $zone.IsValid=$false
    $null=$type.GetMethod('BtnPlace_Click',$flags).Invoke($window,@($null,$null))
    if ($script:called) {throw 'Invalid bend/capacity reached Revit callback'}
    $zone.IsValid=$true
    $duplicate=[Newtonsoft.Json.JsonConvert]::DeserializeObject(
        [Newtonsoft.Json.JsonConvert]::SerializeObject($zone),[LiraSlabZones.Core.AdditionalZone])
    $duplicate.ZoneId=2
    $result.Zones.Add($duplicate)
    $window.LoadResult($result,$true,$true)
    $null=$type.GetMethod('BtnPlace_Click',$flags).Invoke($window,@($null,$null))
    if ($script:called) {throw 'Collision reached Revit callback'}
    $before=[Newtonsoft.Json.Linq.JObject]::FromObject($result.Settings)
    $after=[Newtonsoft.Json.Linq.JObject]::FromObject($result.Settings)
    $same=$type.GetMethod('SameCoverageInputs',[Reflection.BindingFlags]'Static,NonPublic')
    if (!$same.Invoke($null,@($before,$after))) {throw 'Equal layout settings rejected'}
    $after['BgBottomDiameterMm']=[Newtonsoft.Json.Linq.JValue]::new(16)
    if ($same.Invoke($null,@($before,$after))) {throw 'Old coverage reused after changing reinforcement settings'}
    Write-Host 'PASS saved zones / independent of boundary toggle and Revit / placement guard'
} finally {$window.Close()}
