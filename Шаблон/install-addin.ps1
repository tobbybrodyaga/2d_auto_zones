param([int[]]$Versions = @(2022, 2023, 2025, 2026))

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

foreach ($version in $Versions) {
    if ($version -notin @(2022, 2023, 2025, 2026)) {
        throw "Unsupported Revit version: $version"
    }

    $framework = if ($version -ge 2025) { 'net8.0-windows' } else { 'net48' }
    $project = "LiraSlabZones.Revit$version"
    $src = Join-Path $root "src\$project\bin\x64\Release\$framework"
    $sourceDll = Join-Path $src "$project.dll"
    if (-not (Test-Path -LiteralPath $sourceDll)) {
        throw "Build is missing: $sourceDll. Run dotnet build LiraSlabZones.sln -c Release -p:Platform=x64"
    }

    $addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$version"
    $deploy = Join-Path $addinDir 'LiraSlabZones'
    New-Item -ItemType Directory -Force -Path $deploy | Out-Null
    Copy-Item -Path (Join-Path $src '*.dll') -Destination $deploy -Force
    Copy-Item -Path (Join-Path $root 'interop\*.dll') -Destination $deploy -Force

    $defCfg = Join-Path $root 'DefaultSettings.cfg'
    if (Test-Path -LiteralPath $defCfg) {
        Copy-Item -LiteralPath $defCfg -Destination (Join-Path $deploy 'DefaultSettings.cfg') -Force
    }

    $manifest = Join-Path $root "addins\$version\LiraSlabZones.addin"
    if (-not (Test-Path -LiteralPath $manifest)) {
        throw "Revit add-in manifest is missing: $manifest"
    }

    Copy-Item -LiteralPath $manifest -Destination (Join-Path $addinDir 'LiraSlabZones.addin') -Force
    Write-Host "OK Revit ${version}: $deploy"
}

