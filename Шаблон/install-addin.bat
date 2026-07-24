@echo off
chcp 65001 >nul
set "SRC=C:\Users\Filippov_G\Pictures\Test\Шаблон\src\LiraSlabZones.Revit2023\bin\x64\Debug\net48"
set "ADDIN_DIR=%APPDATA%\Autodesk\Revit\Addins\2023"
set "DEPLOY=%ADDIN_DIR%\LiraSlabZones"

if not exist "%SRC%\LiraSlabZones.Revit2023.dll" (
  echo Сначала соберите решение: dotnet build LiraSlabZones.sln -c Debug -p:Platform=x64
  exit /b 1
)

mkdir "%DEPLOY%" 2>nul
xcopy /Y /I "%SRC%\*.dll" "%DEPLOY%\"
xcopy /Y /I "%SRC%\*.json" "%DEPLOY%\" 2>nul

(
echo ^<?xml version="1.0" encoding="utf-8"?^>
echo ^<RevitAddIns^>
echo   ^<AddIn Type="Application"^>
echo     ^<Name^>LiraSlabZones^</Name^>
echo     ^<Assembly^>%DEPLOY%\LiraSlabZones.Revit2023.dll^</Assembly^>
echo     ^<AddInId^>B7E6C2A1-4F3D-4A9E-9C11-8D2A6F0E5B21^</AddInId^>
echo     ^<FullClassName^>LiraSlabZones.Revit2023.App^</FullClassName^>
echo     ^<VendorId^>SUM^</VendorId^>
echo     ^<VendorDescription^>LIRA to Revit slab additional rebar zones^</VendorDescription^>
echo   ^</AddIn^>
echo ^</RevitAddIns^>
) > "%ADDIN_DIR%\LiraSlabZones.addin"

echo Installed to %ADDIN_DIR%
echo Restart Revit 2023.
