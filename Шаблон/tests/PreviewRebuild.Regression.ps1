param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [string]$SourceJson='',
    [ValidateSet('As1','As2','As3','As4')][string]$Layer='As1',
    [string]$OutputJson='',
    [string]$ScreenshotPath='',
    [switch]$Synchronous,
    [switch]$MergeOnly,
    [switch]$RequireClean,
    [int]$TimeoutSeconds=900
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework
@('Newtonsoft.Json.dll','Clipper2Lib.dll','LiraSlabZones.Core.dll')|ForEach-Object {Add-Type -Path (Join-Path $BuildDirectory $_)}
$asm=[Reflection.Assembly]::LoadFrom((Join-Path $BuildDirectory 'LiraSlabZones.PreviewHost.exe'))
$type=$asm.GetType('LiraSlabZones.Revit2023.UI.ZonePreviewWindow')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$oldContext=[Threading.SynchronizationContext]::Current
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new())
$window=[Activator]::CreateInstance($type)
$window.ShowInTaskbar=$false
$window.ShowActivated=$false
$window.WindowStartupLocation=[Windows.WindowStartupLocation]::Manual
$window.Left=-20000
$window.Top=-20000
$window.Width=1600
$window.Height=1000
$timer=[Windows.Threading.DispatcherTimer]::new()
$script:pulses=0
$script:lastStage=''
$timer.Interval=[TimeSpan]::FromMilliseconds(150)
$timer.Add_Tick({
    $script:pulses++
    if(!$Synchronous){
        $stage=$window.FindName('TxtRebuildStage').Text
        $prefix=($stage -split ':')[0]
        if($prefix -ne $script:lastStage){Write-Host ('STAGE '+$stage);$script:lastStage=$prefix}
    }
})
try {
    if($SourceJson){$source=[LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson((Resolve-Path -LiteralPath $SourceJson).Path,$null)}
    else {
        $cfg=[LiraSlabZones.Core.AnalysisSettings]::new()
        $cfg.ConcreteClass='B30';$cfg.BgBottomDiameterMm=8;$cfg.BgBottomStepMm=200
        $source=[LiraSlabZones.Core.DemoSlabFactory]::Create($cfg)
    }
    if($Layer -in @('As3','As4')){
        $source.Settings.BgTopDiameterMm=8;$source.Settings.BgTopStepMm=200
        $source.Settings.SyncBackgroundAsFromBars()
    }
    $window.LoadResult($source,$true,$true)
    $window.Show()
    $viewport=$window.FindName('Viewport')
    $viewport.FitToView()
    $viewport.ZoomBy(1.25)
    $zoom=$viewport.Zoom
    $geometry=[Newtonsoft.Json.JsonConvert]::SerializeObject($source.Plates)
    $type.GetField('_suppressUiEvents',$flags).SetValue($window,$true)
    foreach($name in @('As1','As2','As3','As4')){$window.FindName('Chk'+$name).IsChecked=($name -eq $Layer)}
    $type.GetField('_suppressUiEvents',$flags).SetValue($window,$false)
    $watch=[Diagnostics.Stopwatch]::StartNew()
    if($Synchronous){$null=$type.GetMethod('RebuildZonesPreservingView',$flags).Invoke($window,@($true))}
    else {
        $timer.Start()
        if($MergeOnly){$task=$type.GetMethod('UpdateZonesPreservingViewAsync',$flags).Invoke($window,@($false,$true))}
        else {$task=$type.GetMethod('RebuildZonesPreservingViewAsync',$flags).Invoke($window,@($true))}
        if($window.FindName('RebuildProgressPanel').Visibility -ne 'Visible'){throw 'No visible recalculation progress'}
        if($window.FindName('SettingsScroll').IsEnabled){throw 'Settings may race the active calculation'}
        while(!$task.IsCompleted){
            if($watch.Elapsed.TotalSeconds -gt $TimeoutSeconds){throw 'Recalculation timed out'}
            $frame=[Windows.Threading.DispatcherFrame]::new()
            $pulse=[Windows.Threading.DispatcherTimer]::new()
            $pulse.Interval=[TimeSpan]::FromMilliseconds(40)
            $pulse.Add_Tick({$frame.Continue=$false})
            $pulse.Start()
            [Windows.Threading.Dispatcher]::PushFrame($frame)
            $pulse.Stop()
        }
        $null=$task.GetAwaiter().GetResult()
        if($script:pulses -eq 0){throw 'Dispatcher did not remain responsive'}
        if($window.FindName('RebuildProgressPanel').Visibility -ne 'Collapsed'){throw 'Progress panel was not closed'}
        if(!$window.FindName('SettingsScroll').IsEnabled){throw 'Settings did not recover'}
        if([Math]::Abs($viewport.Zoom-$zoom) -gt 0.00001){throw 'Recalculation reset the viewport zoom'}
        $splitters=@([Windows.Media.VisualTreeHelper]::GetChildrenCount($window.Content))
        $grid=$window.Content
        if($grid.ColumnDefinitions.Count -ne 5){throw 'Resizable side-panel columns missing'}
        $grid.ColumnDefinitions[0].Width=[Windows.GridLength]::new(390)
        $window.UpdateLayout()
        if($grid.ColumnDefinitions[0].ActualWidth -lt 380){throw 'Side panel width cannot change'}
    }
    $result=$type.GetField('_result',$flags).GetValue($window)
    if([Object]::ReferenceEquals($source,$result)){throw 'No new result was committed (inspect the log)'}
    if($geometry -cne [Newtonsoft.Json.JsonConvert]::SerializeObject($result.Plates)){throw 'Source FE/As changed'}
    if(@($result.Zones|Where-Object {$_.Layer.ToString() -ne $Layer}).Count -gt 0){throw 'Inactive reinforcement layers leaked into the layout'}
    $diag=$result.Diagnostics
    $independent=[LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate($result.Plates,$result.Zones,$result.Settings,0,$true,$result.Outline,$result.Openings)
    $bounds=[LiraSlabZones.Core.ZoneBoundaryConstraints]::new($result.Outline,$result.Openings,$result.Settings.EdgeOffsetMm)
    $independent.Issues.AddRange($bounds.CheckZones($result.Zones))
    if($OutputJson){[LiraSlabZones.Core.SlabZoneAnalyzer]::SaveJson($result,[IO.Path]::GetFullPath($OutputJson))}
    if($RequireClean -and $diag.HasErrors){throw ('Diagnostic errors: '+($diag.Issues.Message -join '; '))}
    if($RequireClean -and $independent.HasErrors){throw ('Independent core errors: '+($independent.Issues.Message -join '; '))}
    if($ScreenshotPath){
        $viewport.FitToView();$window.UpdateLayout()
        $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(1600,1000,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window)
        $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream=[IO.File]::Create([IO.Path]::GetFullPath($ScreenshotPath))
        try{$encoder.Save($stream)}finally{$stream.Dispose()}
    }
    Write-Host ('PASS '+$Layer+'; patches='+$result.Patches.Count+'; zones='+$result.Zones.Count+'; conflicts='+$diag.ConflictCount+'; uncovered='+$diag.UncoveredCount+
        '; issues='+$diag.Issues.Count+'; stock11700='+@($result.Zones|Where-Object LengthMm -gt 11699).Count+
        '; lengthM='+[Math]::Round(($result.Zones|Measure-Object LengthMm -Sum).Sum/1000,1)+
        '; seconds='+[Math]::Round($watch.Elapsed.TotalSeconds,1)+'; dispatcher pulses='+$script:pulses)
} finally {
    $timer.Stop();$window.Close()
    [Threading.SynchronizationContext]::SetSynchronizationContext($oldContext)
}
