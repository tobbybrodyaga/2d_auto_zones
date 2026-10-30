param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [string]$OutputDirectory=''
)
$ErrorActionPreference='Stop'
$template=Split-Path -Parent $PSScriptRoot
$repo=Split-Path -Parent $template
if(!$OutputDirectory) {$OutputDirectory=Join-Path $repo 'releases'}
$build=(Resolve-Path -LiteralPath $BuildDirectory).Path
$files=@('LiraSlabZones.PreviewHost.exe','LiraSlabZones.Core.dll',
    'Newtonsoft.Json.dll','Clipper2Lib.dll','LiraSapr.Interop.dll','LiraResAPI.Interop.dll','DefaultSettings.cfg')
foreach($name in $files) {
    if(!(Test-Path -LiteralPath (Join-Path $build $name))) {throw "Missing runtime file: $name"}
}
foreach($name in @('LiraSlabZones.PreviewHost.exe','LiraSlabZones.Core.dll')) {
    if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $build $name)).FileVersion -ne '1.3.0.0') {
        throw "Not a 1.3.0 build: $name"
    }
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$paths=@($files|ForEach-Object {Join-Path $build $_})
$config=Join-Path $build 'LiraSlabZones.PreviewHost.exe.config'
if(Test-Path -LiteralPath $config) {$paths+=$config}
$paths+=Join-Path $template 'installer\README-PREVIEW-1.3.txt'
$archive=Join-Path $OutputDirectory 'LiraSlabZones-1.3.0-Preview.zip'
Compress-Archive -LiteralPath $paths -DestinationPath $archive -Force
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256.txt'),
    ($hash+'  '+[IO.Path]::GetFileName($archive)+[Environment]::NewLine),[Text.Encoding]::ASCII)
Write-Host "Packaged: $archive"
Write-Host "SHA256: $hash"
