using System;
using System.IO;
using LiraSlabZones.Core;

namespace LiraSlabZones.Exporter
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var root = SolutionPaths.FindRoot();
                var configPath = AppConfig.FindDefaultSettingsPath()
                                 ?? Path.Combine(root, AppConfig.DefaultFileName);
                var outputPath = Path.Combine(root, "output", "slab_zones.json");

                string? lirPath = null;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--lir" && i + 1 < args.Length)
                        lirPath = args[++i];
                    else if (args[i] == "--out" && i + 1 < args.Length)
                        outputPath = args[++i];
                    else if (args[i] == "--config" && i + 1 < args.Length)
                        configPath = args[++i];
                    else if (args[i] == "--help" || args[i] == "-h")
                    {
                        PrintHelp();
                        return 0;
                    }
                }

                AnalysisSettings settings;
                if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
                    settings = AnalysisSettingsStore.LoadOrDefault(configPath);
                else
                    settings = AppConfig.LoadEffectiveSettings();

                Console.WriteLine("AsMain = {0} см2/м, режим = {1}", settings.AsMainCm2PerM, settings.PlacementMode);
                Console.WriteLine("FamilyStraight = {0}", settings.FamilyStraight);
                Console.WriteLine(lirPath == null
                    ? "Чтение активной схемы из запущенной ЛИРА-САПР..."
                    : "Открытие: " + lirPath);

                var analyzer = new SlabZoneAnalyzer();
                var result = analyzer.Analyze(lirPath, settings);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                SlabZoneAnalyzer.SaveJson(result, outputPath);

                Console.WriteLine("Документ: {0}", result.DocumentName);
                Console.WriteLine("Узлов: {0}, пластин: {1}, зон доп.арм.: {2}",
                    result.NodeCount, result.PlateCount, result.Zones.Count);
                if (!string.IsNullOrWhiteSpace(result.UnitsNote))
                    Console.WriteLine(result.UnitsNote);
                var withAs = 0;
                foreach (var p in result.Plates)
                    if (p.Rebar.Ok) withAs++;
                Console.WriteLine("Пластин с прочитанным As: {0} из {1}", withAs, result.PlateCount);
                Console.WriteLine("JSON: " + outputPath);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Ошибка: " + ex.Message);
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("LiraSlabZones.Exporter");
            Console.WriteLine("  --lir <path.lir>   открыть схему (иначе — активный документ ЛИРА)");
            Console.WriteLine("  --out <json>       путь выгрузки зон");
            Console.WriteLine("  --config <cfg>     DefaultSettings.cfg / LiraSlabZones.cfg");
        }
    }
}
