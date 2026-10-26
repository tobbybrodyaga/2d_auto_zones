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
    }

    /// <summary>
    /// Только поиск семейств, уже загруженных в документ.
    /// Подгрузка .rfa в проект запрещена.
    /// </summary>
    internal static class FamilyLoader
    {
        /// <summary>Типоразмер по точному имени семейства из проекта.</summary>
        public static FamilySymbol? FindSymbol(Document doc, string familyName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(familyName))
                return null;

            var name = AppConfig.StripRfa(familyName);
            var match = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s =>
                    s.Family != null
                    && (s.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || s.Family.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

            if (match == null) return null;
            if (!match.IsActive) match.Activate();
            return match;
        }

        public static FamilySymbol RequireFromProject(Document doc, string familyName)
        {
            var symbol = FindSymbol(doc, familyName);
            if (symbol != null) return symbol;

            throw new InvalidOperationException(
                "Семейство «" + AppConfig.StripRfa(familyName) + "» не найдено в проекте Revit.\n" +
                "Загрузите его в проект вручную (плагин семейства не подгружает).\n" +
                "Имя можно изменить в настройках: " + AppConfig.UserConfigPath);
        }

        public static List<string> ListFamilyNames(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null && !string.IsNullOrWhiteSpace(s.FamilyName))
                .Select(s => s.FamilyName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Найти по имени; при отсутствии — диалог выбора из уже загруженных.</summary>
        public static FamilySymbol ResolveFromProject(Document doc, string? preferredFamilyName)
        {
            var preferred = AppConfig.StripRfa(preferredFamilyName ?? "");
            if (!string.IsNullOrEmpty(preferred))
            {
                var found = FindSymbol(doc, preferred);
                if (found != null) return found;
            }

            var names = ListFamilyNames(doc);
            if (names.Count == 0)
                throw new InvalidOperationException(
                    "В проекте нет загруженных семейств.\n" +
                    "Загрузите семейство зоны доп. армирования в проект вручную.");

            var picked = PickFamilyName(names, preferred);
            if (string.IsNullOrEmpty(picked))
                throw new OperationCanceledException("Семейство не выбрано.");

            return RequireFromProject(doc, picked!);
        }

        public static string? PickFamilyName(IReadOnlyList<string> familyNames, string? preferred = null)
        {
            if (familyNames == null || familyNames.Count == 0)
                return null;

            using var form = new System.Windows.Forms.Form
            {
                Text = "Семейство зоны доп. армирования",
                Width = 560,
                Height = 170,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var label = new System.Windows.Forms.Label
            {
                Text = "Семейство не найдено по имени из настроек. Выберите из проекта:",
                Left = 12,
                Top = 12,
                Width = 520,
                AutoSize = false
            };

            var combo = new System.Windows.Forms.ComboBox
            {
                Left = 12,
                Top = 40,
                Width = 520,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var n in familyNames)
                combo.Items.Add(n);

            var prefer = AppConfig.StripRfa(preferred ?? "");
            var idx = IndexOfPrefer(combo, prefer);
            combo.SelectedIndex = idx >= 0 ? idx : 0;

            var ok = new System.Windows.Forms.Button { Text = "OK", DialogResult = DialogResult.OK, Left = 356, Top = 90, Width = 85 };
            var cancel = new System.Windows.Forms.Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 447, Top = 90, Width = 85 };
            form.Controls.Add(label);
            form.Controls.Add(combo);
            form.Controls.Add(ok);
            form.Controls.Add(cancel);
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            return form.ShowDialog() == DialogResult.OK
                ? combo.SelectedItem?.ToString()
                : null;
        }

        private static int IndexOfPrefer(System.Windows.Forms.ComboBox combo, string prefer)
        {
            if (!string.IsNullOrEmpty(prefer))
            {
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    var item = combo.Items[i]?.ToString() ?? "";
                    if (item.Equals(prefer, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }

            for (int i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i]?.ToString() ?? "";
                if (item.IndexOf("SUM-30", StringComparison.OrdinalIgnoreCase) >= 0
                    || item.IndexOf("дополнительного армирования", StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }

            return -1;
        }
    }
}
