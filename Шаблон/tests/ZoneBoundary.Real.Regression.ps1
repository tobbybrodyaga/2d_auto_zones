param(
    [Parameter(Mandatory=$true)][string]$SourceJson,
    [string]$OutputJson = '',
    [string]$BuildDirectory = '',
    [switch]$RequireBoundaryImprovement,
    [switch]$RequireConflictImprovement,
    [switch]$RequireCollisionFree
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if (!$BuildDirectory) {$BuildDirectory=Join-Path $root 'src\LiraSlabZones.PreviewHost\bin\x64\Release\net48'}
Add-Type -AssemblyName PresentationFramework
@('Newtonsoft.Json.dll','Clipper2Lib.dll','LiraSlabZones.Core.dll') | ForEach-Object {Add-Type -Path (Join-Path $BuildDirectory $_)}
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $BuildDirectory 'LiraSlabZones.PreviewHost.exe'))
$type=$assembly.GetType('LiraSlabZones.Revit2023.UI.ZonePreviewWindow')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$source=[LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson((Resolve-Path -LiteralPath $SourceJson).Path,$null)
if (!$source.PatchPreviewOnly -or $source.Zones.Count -eq 0) {throw 'Input must contain a saved patch layout to compare'}
$constraints=[LiraSlabZones.Core.ZoneBoundaryConstraints]::new($source.Outline,$source.Openings,$source.Settings.EdgeOffsetMm)
$baseline=$constraints.Snapshot($source.Zones)
$geometry=[Newtonsoft.Json.JsonConvert]::SerializeObject($source.Plates)
$before=[LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate($source.Plates,$baseline,$source.Settings,0,$true,$source.Outline,$source.Openings)
$oldMissing=@($before.Issues|Where-Object Kind -eq UncoveredElement|ForEach-Object {$_.Layer.ToString()+':'+$_.ElementId})
$oldBoundary=@($baseline|Where-Object {!$constraints.Check($_.Contour,$_.Direction).Allowed}).Count
$window=[Activator]::CreateInstance($type)
try {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $window.LoadResult($source,$true,$true)
    $null=$type.GetMethod('RebuildZonesPreservingView',$flags).Invoke($window,@($false))
    $result=$type.GetField('_result',$flags).GetValue($window)
    if ($geometry -cne [Newtonsoft.Json.JsonConvert]::SerializeObject($result.Plates)) {
        throw 'Recalculation changed FE geometry, reinforcement demand or element order'
    }
    $after=[LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate($result.Plates,$result.Zones,$result.Settings,0,$true,$result.Outline,$result.Openings)
    $newMissing=@($after.Issues|Where-Object Kind -eq UncoveredElement|ForEach-Object {$_.Layer.ToString()+':'+$_.ElementId})
    $lost=@($newMissing|Where-Object {$_ -notin $oldMissing})
    if ($lost.Count -gt 0) {throw ('Previously covered finite elements lost: '+($lost -join ', '))}
    if ($after.ConflictCount -gt $before.ConflictCount) {throw 'Automatic repair increased placement/spacing conflicts'}
    if ($RequireConflictImprovement -and $after.ConflictCount -ge $before.ConflictCount) {throw 'Expected fewer collision pairs on this fixture'}
    $newBoundary=@($result.Zones|Where-Object {!$constraints.Check($_.Contour,$_.Direction).Allowed}).Count
    if ($newBoundary -gt $oldBoundary) {throw 'Automatic repair increased boundary conflicts'}
    if ($RequireBoundaryImprovement -and $newBoundary -ge $oldBoundary) {throw 'Expected a strict improvement on this fixture'}
    if ($RequireCollisionFree -and ($after.ConflictCount -gt 0 -or $newBoundary -gt 0)) {throw 'Layout is not collision-free'}
    if ($RequireCollisionFree -and $after.HasErrors) {throw ('Strict fixture still has diagnostic issues: '+($after.Issues.Message -join '; '))}
    if (@($result.Zones|Where-Object {!$_.BoundaryCheck.Checked}).Count -gt 0) {throw 'A zone escaped boundary diagnostics'}
    if ($OutputJson) {[LiraSlabZones.Core.SlabZoneAnalyzer]::SaveJson($result,[IO.Path]::GetFullPath($OutputJson))}
    Write-Host ('PASS real slab: FE='+$result.Plates.Count+'; boundary='+$oldBoundary+' -> '+$newBoundary+
        '; uncovered='+$oldMissing.Count+' -> '+$newMissing.Count+'; newly uncovered='+$lost.Count+
        '; conflict issues='+$before.ConflictCount+' -> '+$after.ConflictCount+
        '; seconds='+[Math]::Round($watch.Elapsed.TotalSeconds,1))
} finally {$window.Close()}
