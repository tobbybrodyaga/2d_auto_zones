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
                var root = FindRoot();
                var configPath = Path.Combine(root, "config", "settings.json");
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

                if (!File.Exists(configPath))
                {
                    AnalysisSettingsStore.Save(configPath, new AnalysisSettings());
                    Console.WriteLine("Создан конфиг по умолчанию: " + configPath);
                }

                var settings = AnalysisSettingsStore.LoadOrDefault(configPath);
                Console.WriteLine("AsMain = {0} см2/м, режим = {1}", settings.AsMainCm2PerM, settings.PlacementMode);
                Console.WriteLine(lirPath == null
                    ? "Чтение активной схемы из запущенной ЛИРА-САПР..."
                    : "Открытие: " + lirPath);

                var analyzer = new SlabZoneAnalyzer();
                var result = analyzer.Analyze(lirPath, settings);
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
            Console.WriteLine("  --config <json>    настройки порога AsMain и режима размещения");
        }

        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "config")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "families")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        }
    }
}
