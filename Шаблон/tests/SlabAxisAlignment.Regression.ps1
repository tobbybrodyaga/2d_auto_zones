param([string]$BuildDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$BuildDirectory) { $BuildDirectory = Join-Path $root 'src\LiraSlabZones.PreviewHost\bin\x64\Release\net48' }
$refs = @('Newtonsoft.Json.dll', 'Clipper2Lib.dll', 'LiraSlabZones.Core.dll') | ForEach-Object {
    $path = Join-Path $BuildDirectory $_
    Add-Type -Path $path
    $path
}
Add-Type -Path (Join-Path $PSScriptRoot 'SlabAxisAlignment.Regression.cs') -ReferencedAssemblies ($refs + @('System.Core.dll'))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('slab-alignment-' + [Guid]::NewGuid().ToString('N'))
$scratch = [IO.Path]::GetFullPath($scratch)
if (!$scratch.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Scratch path must stay inside the temporary directory'
}
[IO.Directory]::CreateDirectory($scratch) | Out-Null
try { [SlabAlignmentRegression]::Run($scratch) }
finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
Write-Host 'PASS SlabAxisAlignment regression'
