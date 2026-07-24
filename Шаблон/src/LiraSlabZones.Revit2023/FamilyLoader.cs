using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using LiraSlabZones.Core;

namespace LiraSlabZones.Revit2023
{
    internal static class PathResolver
    {
        public static string FindSolutionRoot() => SolutionPaths.FindRoot();

        public static string? PickJson(string defaultPath)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Выберите JSON зон (slab_zones.json)",
                Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = File.Exists(defaultPath) ? defaultPath : "slab_zones.json",
                InitialDirectory = File.Exists(defaultPath)
                    ? Path.GetDirectoryName(defaultPath)
                    : FindSolutionRoot()
            };
            return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
        }

        public static string? FindFamilyOnODrive(string familyFileName) =>
            SolutionPaths.FindFamilyOnODrive(familyFileName);
    }

    internal static class FamilyLoader
    {
        public static FamilySymbol EnsureSymbol(Document doc, string familyPath, string familyName)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase)
                                     || s.Family.Name.Equals(Path.GetFileNameWithoutExtension(familyPath), StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (!existing.IsActive) existing.Activate();
                return existing;
            }

            if (!doc.LoadFamily(familyPath, out var family) || family == null)
                throw new InvalidOperationException("Не удалось загрузить семейство: " + familyPath);

            var symbolIds = family.GetFamilySymbolIds();
            var symbol = symbolIds.Select(id => doc.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault()
                         ?? throw new InvalidOperationException("В семействе нет типоразмеров: " + family.Name);

            if (!symbol.IsActive) symbol.Activate();
            return symbol;
        }

        /// <summary>Загрузка SUM-30…34 из каталога families (недостающие пропускаются).</summary>
        public static Dictionary<string, FamilySymbol> EnsureSumFamilies(Document doc, string familiesDir)
        {
            var map = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
            foreach (var fileName in RebarTables.AllFamilyFiles)
            {
                var path = Path.Combine(familiesDir, fileName);
                if (!File.Exists(path))
                {
                    var alt = Path.Combine(familiesDir, Path.GetFileNameWithoutExtension(fileName) + "_R22.rfa");
                    if (File.Exists(alt)) path = alt;
                    else
                    {
                        var any = Directory.Exists(familiesDir)
                            ? Directory.GetFiles(familiesDir, Path.GetFileNameWithoutExtension(fileName) + "*.rfa").FirstOrDefault()
                            : null;
                        if (any == null) continue;
                        path = any;
                    }
                }

                try
                {
                    var name = Path.GetFileNameWithoutExtension(fileName);
                    map[fileName] = EnsureSymbol(doc, path, name);
                }
                catch
                {
                    // семейство опционально
                }
            }
            return map;
        }
    }
}
