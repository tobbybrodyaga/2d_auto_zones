# Build LiraSlabZones-Setup.exe (≤100 MB)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not (Test-Path (Join-Path $root 'LiraSlabZones.sln'))) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Set-Location -LiteralPath $root

Write-Host "== Build Release x64 =="
dotnet build ".\LiraSlabZones.sln" -c Release -p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$stage = Join-Path $root "dist\stage"
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
@(
    "addin\2022",
    "addin\2023",
    "addin\2025",
    "addin\2026",
    "tools",
    "data\config",
    "data\output"
) | ForEach-Object { New-Item -ItemType Directory -Force -Path (Join-Path $stage $_) | Out-Null }

$previewBin = Join-Path $root "src\LiraSlabZones.PreviewHost\bin\x64\Release\net48"

foreach ($version in @(2022, 2023, 2025, 2026)) {
    $framework = if ($version -ge 2025) { "net8.0-windows" } else { "net48" }
    $bin = Join-Path $root "src\LiraSlabZones.Revit$version\bin\x64\Release\$framework"
    $target = Join-Path $stage "addin\$version"
    Copy-Item (Join-Path $bin "*.dll") $target -Force
    Copy-Item (Join-Path $root "interop\*.dll") $target -Force
    Copy-Item (Join-Path $root "DefaultSettings.cfg") $target -Force
}

& (Join-Path $root "tools\publish-preview.ps1") -Configuration Release
$def = Join-Path $root "DefaultSettings.cfg"
if (Test-Path -LiteralPath $def) {
    Copy-Item $def (Join-Path $stage "data\DefaultSettings.cfg") -Force
}

$stageMb = [math]::Round(((Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Host "Stage size: $stageMb MB"

$iscc = @(
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install JRSoftware.InnoSetup." }

$iss = Join-Path $root "installer\LiraSlabZones.iss"
Write-Host "== Compile $iss =="
& $iscc $iss
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setup = Join-Path $root "dist\LiraSlabZones-Setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "Setup.exe not produced" }
$mb = [math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 2)
Write-Host "OK: $setup ($mb MB)"
if ($mb -gt 100) { throw "Setup exceeds 100 MB ($mb MB)" }
