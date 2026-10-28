param([string]$BuildDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$BuildDirectory) { $BuildDirectory = Join-Path $root 'src\LiraSlabZones.PreviewHost\bin\x64\Release\net48' }
Add-Type -AssemblyName PresentationFramework
@('Newtonsoft.Json.dll','Clipper2Lib.dll','LiraSlabZones.Core.dll') | ForEach-Object {
    Add-Type -Path (Join-Path $BuildDirectory $_)
}
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $BuildDirectory 'LiraSlabZones.PreviewHost.exe'))
$type = $assembly.GetType('LiraSlabZones.Revit2023.UI.ZonePreviewWindow')
$window = [Activator]::CreateInstance($type)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
try {
    $angle = 7.34567891234567
    $x = 1.234567891234
    $y = -3.456789123456
    $null = $type.GetMethod('SetTransformText',$flags).Invoke($window,@($angle,$x,$y))
    $null = $type.GetMethod('RotateBy',$flags).Invoke($window,@([double]90))
    $null = $type.GetMethod('RotateBy',$flags).Invoke($window,@([double]-90))
    $cfg = $type.GetMethod('ReadSettingsFromUi',$flags).Invoke($window,@())
    if ([Math]::Abs($cfg.RotationDeg - $angle) -gt 1e-13 -or $cfg.OffsetXM -ne $x -or $cfg.OffsetYM -ne $y) {
        throw 'UI rounding changed the transform'
    }
    $box = $type.GetField('TbRot',$flags).GetValue($window)
    $box.Text = '12,345678912345'
    $cfg = $type.GetMethod('ReadSettingsFromUi',$flags).Invoke($window,@())
    if ([Math]::Abs($cfg.RotationDeg - 12.345678912345) -gt 1e-13) { throw 'Manual angle ignored' }
    if (!$cfg.AutoAlignAfterRevitLink) { throw 'Auto alignment must default to enabled' }
    Write-Host 'PASS WPF precision / rotate +/-90 / manual angle / auto-align default'
} finally { $window.Close() }
