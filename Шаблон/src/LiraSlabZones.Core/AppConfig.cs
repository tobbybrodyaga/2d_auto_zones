using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Дефолты: DefaultSettings.cfg (репозиторий / рядом с DLL).
    /// Пользователь: %APPDATA%\Roaming\Autodesk\Revit\Addins\2023\LiraSlabZones.cfg
    /// </summary>
    public static class AppConfig
    {
        public const string DefaultFileName = "DefaultSettings.cfg";
        public const string UserFileName = "LiraSlabZones.cfg";

        public static string UserConfigDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", DetectRevitVersion());

        public static string UserConfigPath => Path.Combine(UserConfigDirectory, UserFileName);

        private static string DetectRevitVersion()
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
                while (dir != null)
                {
                    if (dir.Name.Length == 4 && int.TryParse(dir.Name, out var year) && year >= 2020 && year <= 2100)
                        return dir.Name;
                    dir = dir.Parent;
                }
            }
            catch { /* standalone preview and tests use the compatibility default */ }

            return Environment.GetEnvironmentVariable("LIRASLABZONES_REVIT_VERSION") ?? "2023";
        }

        public static string? FindDefaultSettingsPath()
        {
            var env = Environment.GetEnvironmentVariable("LIRASLABZONES_DEFAULT_CFG");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                return env;

            foreach (var start in CandidateStarts())
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, DefaultFileName);
                    if (File.Exists(candidate))
                        return candidate;
                    dir = dir.Parent;
                }
            }

            return null;
        }

        private static string[] CandidateStarts()
        {
            var list = new System.Collections.Generic.List<string>();
            void Add(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                if (Directory.Exists(p))
                    list.Add(p!);
            }

            Add(AppDomain.CurrentDomain.BaseDirectory);
            try { Add(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)); } catch { /* ignore */ }
            Add(SolutionPaths.InstalledDataRoot);
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>Дефолты + поверх пользовательский cfg.</summary>
        public static AnalysisSettings LoadEffectiveSettings()
        {
            var settings = BuiltInDefaults();
            var defPath = FindDefaultSettingsPath();
            if (defPath != null)
                MergeFile(settings, defPath);

            if (File.Exists(UserConfigPath))
                MergeFile(settings, UserConfigPath);

            settings.SyncBackgroundAsFromBars();
            settings.FamilyName = settings.FamilyStraight;
            settings.FamilyFileName = settings.FamilyStraight + ".rfa";
            return settings;
        }

        public static void SaveUserSettings(AnalysisSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.FamilyStraight = string.IsNullOrWhiteSpace(settings.FamilyStraight)
                ? settings.FamilyName
                : settings.FamilyStraight;
            settings.FamilyName = settings.FamilyStraight;
            settings.FamilyFileName = settings.FamilyStraight + ".rfa";

            Directory.CreateDirectory(UserConfigDirectory);
            var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(UserConfigPath, json);
        }

        public static AnalysisSettings BuiltInDefaults()
        {
            return new AnalysisSettings
            {
                FamilyStraight = "SUM-30-Зона дополнительного армирования",
                FamilyL = "SUM-31-Зона дополнительного армирования Г",
                FamilyPEqual = "SUM-32-Зона дополнительного армирования П-образная равнополочная",
                FamilyPDiff = "SUM-33-Зона дополнительного армирования П-образная разнополочная",
                FamilyBentStick = "SUM-34-Зона дополнительного армирования Гнутый стержень",
                FamilyName = "SUM-30-Зона дополнительного армирования",
                FamilyFileName = "SUM-30-Зона дополнительного армирования.rfa",
                ConcreteClass = "B25",
                AutoLayout = true,
                GridCellMm = 300,
                MinZoneWidthM = 0.3,
                BarStepMm = 200,
                PlacementMode = "AutoLayout"
            };
        }

        private static void MergeFile(AnalysisSettings target, string path)
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return;

            var patch = JsonConvert.DeserializeObject<AnalysisSettings>(json);
            if (patch == null) return;

            // Частичный merge: берём непустые/заданные поля из JSON через JObject
            var jo = JObject.Parse(json);
            JsonConvert.PopulateObject(jo.ToString(), target);

            if (jo["FamilyName"] != null && jo["FamilyStraight"] == null
                && !string.IsNullOrWhiteSpace(patch.FamilyName))
                target.FamilyStraight = patch.FamilyName;

            if (string.IsNullOrWhiteSpace(target.FamilyStraight) && !string.IsNullOrWhiteSpace(target.FamilyName))
                target.FamilyStraight = StripRfa(target.FamilyName);
            if (string.IsNullOrWhiteSpace(target.FamilyName))
                target.FamilyName = target.FamilyStraight;
        }

        public static string StripRfa(string name)
        {
            var s = (name ?? "").Trim();
            if (s.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                s = Path.GetFileNameWithoutExtension(s);
            return s;
        }
    }
}
