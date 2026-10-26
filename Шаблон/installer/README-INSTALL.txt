LiraSlabZones — установка

Что будет установлено:
1. Add-in Revit 2022/2023/2025/2026 + DefaultSettings.cfg:
   %APPDATA%\Autodesk\Revit\Addins\<версия>\LiraSlabZones\
2. Пользовательские настройки (создаются при сохранении):
   %APPDATA%\Autodesk\Revit\Addins\<версия>\LiraSlabZones.cfg
3. Данные (output):
   %LOCALAPPDATA%\LiraSlabZones\
4. PreviewHost:
   %LOCALAPPDATA%\LiraSlabZones\tools\

Семейства .rfa плагин НЕ подгружает.
Имена семейств берутся из проекта Revit по названию (DefaultSettings.cfg / LiraSlabZones.cfg).

Требования:
- Autodesk Revit 2022, 2023, 2025 или 2026 (x64)
- .NET Framework 4.8
- Семейства зон уже загружены в шаблон/проект

После установки перезапустите используемую версию Revit.
