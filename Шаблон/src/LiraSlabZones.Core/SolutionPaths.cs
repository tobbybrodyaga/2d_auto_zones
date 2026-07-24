using System;
using System.IO;
using System.Linq;

namespace LiraSlabZones.Core
{
    public static class SolutionPaths
    {
        public static string FindRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "families")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "config")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            var fallback = @"C:\Users\Filippov_G\Pictures\Test\Шаблон";
            if (Directory.Exists(fallback))
                return fallback;

            throw new DirectoryNotFoundException("Не найден корень LiraSlabZones (families/config).");
        }

        public static string? FindFamilyOnODrive(string familyFileName)
        {
            try
            {
                var filippov = Directory.GetDirectories(@"O:\")
                    .FirstOrDefault(d => d.IndexOf("Филиппов", StringComparison.OrdinalIgnoreCase) >= 0);
                if (filippov == null) return null;

                var detach = Directory.GetDirectories(filippov)
                    .FirstOrDefault(d => d.IndexOf("отсоедин", StringComparison.OrdinalIgnoreCase) >= 0);
                if (detach == null) return null;

                var path = Path.Combine(detach, familyFileName);
                if (File.Exists(path)) return path;

                return Directory.GetFiles(detach, "*.rfa")
                    .FirstOrDefault(f => Path.GetFileName(f).IndexOf("SUM-30", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch
            {
                return null;
            }
        }
    }
}
