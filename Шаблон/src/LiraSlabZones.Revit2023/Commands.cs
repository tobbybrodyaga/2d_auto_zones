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
                var settings = AppConfig.LoadEffectiveSettings();
                // имена семейств — из пользовательского/дефолтного cfg
                analysis.Settings.FamilyStraight = settings.FamilyStraight;
                analysis.Settings.FamilyL = settings.FamilyL;
                analysis.Settings.FamilyPEqual = settings.FamilyPEqual;
                analysis.Settings.FamilyPDiff = settings.FamilyPDiff;
                analysis.Settings.FamilyBentStick = settings.FamilyBentStick;

                FamilySymbol symbol;
                try
                {
                    symbol = FamilyLoader.ResolveFromProject(doc, analysis.Settings.FamilyStraight);
                }
                catch (OperationCanceledException)
                {
                    message = "Семейство не выбрано.";
                    return Result.Cancelled;
                }

                analysis.Settings.FamilyStraight = symbol.FamilyName;
                AppConfig.SaveUserSettings(analysis.Settings);

                ZonePlacementService.PlaceWithPrompt(commandData.Application, symbol, analysis);
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
                var outputPath = Path.Combine(root, "output", "slab_zones.json");

                var settings = AppConfig.LoadEffectiveSettings();

                FamilySymbol symbol;
                try
                {
                    symbol = FamilyLoader.ResolveFromProject(doc, settings.FamilyStraight);
                }
                catch (OperationCanceledException)
                {
                    message = "Семейство не выбрано.";
                    return Result.Cancelled;
                }

                settings.FamilyStraight = symbol.FamilyName;
                AppConfig.SaveUserSettings(settings);

                var analyzer = new SlabZoneAnalyzer();
                var analysis = analyzer.Analyze(null, settings);
                SlabZoneAnalyzer.SaveJson(analysis, outputPath);

                ZonePlacementService.PlaceWithPrompt(commandData.Application, symbol, analysis);
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
