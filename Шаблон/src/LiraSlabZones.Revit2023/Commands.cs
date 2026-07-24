using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiraSlabZones.Core;

namespace LiraSlabZones.Revit2023
{
    [Transaction(TransactionMode.Manual)]
    public sealed class PlaceZonesFromJsonCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = commandData.Application.ActiveUIDocument
                            ?? throw new InvalidOperationException("Нет активного документа Revit.");
                var doc = uiDoc.Document;

                var root = PathResolver.FindSolutionRoot();
                var defaultJson = Path.Combine(root, "output", "slab_zones.json");
                var jsonPath = PathResolver.PickJson(defaultJson);
                if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
                {
                    message = "JSON с зонами не выбран.";
                    return Result.Cancelled;
                }

                var analysis = SlabZoneAnalyzer.LoadJson(jsonPath!);
                var familyPath = Path.Combine(root, "families", analysis.Settings.FamilyFileName);
                if (!File.Exists(familyPath))
                {
                    // запасной путь — исходная отсоединёнка
                    var alt = PathResolver.FindFamilyOnODrive(analysis.Settings.FamilyFileName);
                    if (alt != null) familyPath = alt;
                }

                if (!File.Exists(familyPath))
                    throw new FileNotFoundException("Не найдено семейство: " + analysis.Settings.FamilyFileName, familyPath);

                int placed;
                using (var tx = new Transaction(doc, "Раскладка зон доп.армирования (ЛИРА)"))
                {
                    tx.Start();
                    var symbol = FamilyLoader.EnsureSymbol(doc, familyPath, analysis.Settings.FamilyName);
                    placed = ZonePlacer.Place(doc, symbol, analysis);
                    tx.Commit();
                }

                TaskDialog.Show("LiraSlabZones",
                    $"Размещено экземпляров: {placed}\n" +
                    $"Зон в JSON: {analysis.Zones.Count}\n" +
                    $"Семейство: {analysis.Settings.FamilyName}\n\n" +
                    "Сопоставление контура с моделью выполните вручную в Revit (без привязки к осям).");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("LiraSlabZones — ошибка", ex.ToString());
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class AnalyzeLiraAndPlaceCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = commandData.Application.ActiveUIDocument
                            ?? throw new InvalidOperationException("Нет активного документа Revit.");
                var doc = uiDoc.Document;
                var root = PathResolver.FindSolutionRoot();
                var configPath = Path.Combine(root, "config", "settings.json");
                var outputPath = Path.Combine(root, "output", "slab_zones.json");

                if (!File.Exists(configPath))
                    AnalysisSettingsStore.Save(configPath, new AnalysisSettings());

                var settings = AnalysisSettingsStore.LoadOrDefault(configPath);
                var analyzer = new SlabZoneAnalyzer();
                var analysis = analyzer.Analyze(null, settings);
                SlabZoneAnalyzer.SaveJson(analysis, outputPath);

                var familyPath = Path.Combine(root, "families", settings.FamilyFileName);
                if (!File.Exists(familyPath))
                {
                    var alt = PathResolver.FindFamilyOnODrive(settings.FamilyFileName);
                    if (alt != null) familyPath = alt;
                }

                if (!File.Exists(familyPath))
                    throw new FileNotFoundException("Не найдено семейство: " + settings.FamilyFileName);

                int placed;
                using (var tx = new Transaction(doc, "Анализ ЛИРА + раскладка зон"))
                {
                    tx.Start();
                    var symbol = FamilyLoader.EnsureSymbol(doc, familyPath, settings.FamilyName);
                    placed = ZonePlacer.Place(doc, symbol, analysis);
                    tx.Commit();
                }

                TaskDialog.Show("LiraSlabZones",
                    $"Документ ЛИРА: {analysis.DocumentName}\n" +
                    $"Пластин: {analysis.PlateCount}, зон: {analysis.Zones.Count}\n" +
                    $"Размещено в Revit: {placed}\n" +
                    $"JSON: {outputPath}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("LiraSlabZones — ошибка", ex.ToString());
                return Result.Failed;
            }
        }
    }

    public sealed class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            const string tab = "LiraSlabZones";
            try { application.CreateRibbonTab(tab); } catch { /* tab may exist */ }

            var panel = application.CreateRibbonPanel(tab, "Плиты");
            var asm = Assembly.GetExecutingAssembly().Location;

            panel.AddItem(new PushButtonData(
                "ZonePreview",
                "Превью\nзон",
                asm,
                typeof(OpenZonePreviewCommand).FullName)
            {
                ToolTip = "Предварительный просмотр зон доп.армирования (SmartRebar/SmartKR-стиль)"
            });

            panel.AddItem(new PushButtonData(
                "AnalyzeAndPlace",
                "Анализ ЛИРА\nи раскладка",
                asm,
                typeof(AnalyzeLiraAndPlaceCommand).FullName));

            panel.AddItem(new PushButtonData(
                "PlaceFromJson",
                "Раскладка\nиз JSON",
                asm,
                typeof(PlaceZonesFromJsonCommand).FullName));

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }
}
