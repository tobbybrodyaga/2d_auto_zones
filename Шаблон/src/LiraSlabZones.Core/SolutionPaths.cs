using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LiraSlabZones.Core
{
    public static class SolutionPaths
    {
        /// <summary>Каталог данных установки: %LOCALAPPDATA%\LiraSlabZones</summary>
        public static string InstalledDataRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiraSlabZones");

        public static string FindRoot()
        {
            foreach (var start in new[]
                     {
                         AppDomain.CurrentDomain.BaseDirectory,
                         Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ""
                     }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (LooksLikeRoot(dir.FullName))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }

            if (LooksLikeRoot(InstalledDataRoot))
                return InstalledDataRoot;

            var fallback = @"C:\Users\Filippov_G\Pictures\Test\Шаблон";
            if (Directory.Exists(fallback) && LooksLikeRoot(fallback))
                return fallback;

            EnsureInstalledLayout(InstalledDataRoot);
            return InstalledDataRoot;
        }

        public static bool LooksLikeRoot(string path) =>
            !string.IsNullOrWhiteSpace(path)
            && (File.Exists(Path.Combine(path, AppConfig.DefaultFileName))
                || Directory.Exists(Path.Combine(path, "config"))
                || Directory.Exists(Path.Combine(path, "output")));

        public static void EnsureInstalledLayout(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Directory.CreateDirectory(Path.Combine(root, "output"));

            var defCfg = Path.Combine(root, AppConfig.DefaultFileName);
            if (!File.Exists(defCfg))
            {
                var fromRepo = AppConfig.FindDefaultSettingsPath();
                if (fromRepo != null && !string.Equals(fromRepo, defCfg, StringComparison.OrdinalIgnoreCase))
                    File.Copy(fromRepo, defCfg, overwrite: false);
                else
                    File.WriteAllText(defCfg, JsonDefaults());
            }
        }

        private static string JsonDefaults() =>
            "{\n" +
            "  \"FamilyStraight\": \"SUM-30-Зона дополнительного армирования\",\n" +
            "  \"FamilyL\": \"SUM-31-Зона дополнительного армирования Г\",\n" +
            "  \"FamilyPEqual\": \"SUM-32-Зона дополнительного армирования П-образная равнополочная\",\n" +
            "  \"FamilyPDiff\": \"SUM-33-Зона дополнительного армирования П-образная разнополочная\",\n" +
            "  \"FamilyBentStick\": \"SUM-34-Зона дополнительного армирования Гнутый стержень\",\n" +
            "  \"ConcreteClass\": \"B25\",\n" +
            "  \"AutoLayout\": true,\n" +
            "  \"GridCellMm\": 300\n" +
            "}\n";
    }
}
