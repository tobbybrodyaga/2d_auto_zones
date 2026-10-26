using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiraSlabZones.Core;
using ComboBox = System.Windows.Controls.ComboBox;
using Newtonsoft.Json;

namespace LiraSlabZones.Revit2023
{
    /// <summary>
    /// Выбор планов низа/верха и размещение зон. Семейства SUM-30…34 — элементы узлов,
    /// поэтому каждая зона ставится на конкретный вид.
    /// </summary>
    internal static class ZonePlacementService
    {
        public static void PlaceWithPrompt(UIApplication uiApp, FamilySymbol? defaultSymbol, AnalysisResult analysis)
        {
            var doc = uiApp.ActiveUIDocument?.Document
                      ?? throw new InvalidOperationException("Нет активного документа Revit.");
            if (analysis.Zones == null || analysis.Zones.Count == 0)
            {
                TaskDialog.Show("LiraSlabZones", "Нет зон для размещения.");
                return;
            }

            var plans = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => !v.IsTemplate &&
                            (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.EngineeringPlan))
                .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (plans.Count == 0)
            {
                TaskDialog.Show("LiraSlabZones", "В проекте нет планов этажей или несущих конструкций.");
                return;
            }

            var bottomCount = analysis.Zones.Count(z => z.Layer is RebarLayer.As1 or RebarLayer.As2);
            var topCount = analysis.Zones.Count - bottomCount;
            var prefs = RevitPlacementPrefs.Load();
            var activePlan = doc.ActiveView as ViewPlan;
            string Default(string? remembered) =>
                plans.Any(v => v.Name == remembered) ? remembered!
                : activePlan != null && plans.Any(v => v.Id == activePlan.Id) ? activePlan.Name
                : plans[0].Name;

            var dialog = new PlacementTargetDialog(
                plans.Select(v => v.Name).ToList(),
                Default(prefs.BottomViewName), Default(prefs.TopViewName),
                bottomCount, topCount);
            new WindowInteropHelper(dialog) { Owner = uiApp.MainWindowHandle };
            if (dialog.ShowDialog() != true) return;

            var bottomView = plans.First(v => v.Name == dialog.BottomViewName);
            var topView = plans.First(v => v.Name == dialog.TopViewName);
            prefs.BottomViewName = bottomView.Name;
            prefs.TopViewName = topView.Name;
            prefs.Save();

            var report = PlaceIntoViews(doc, defaultSymbol, analysis, bottomView, topView);
            var text =
                $"Размещено: {report.Placed} из {analysis.Zones.Count}\n" +
                $"  низ (As1/As2) → «{bottomView.Name}»: {report.PlacedBottom}\n" +
                $"  верх (As3/As4) → «{topView.Name}»: {report.PlacedTop}";
            if (report.Failures.Count > 0)
                text += $"\n\nНе размещено: {report.Failures.Count}\n" +
                        string.Join("\n", report.Failures.Take(10)) +
                        (report.Failures.Count > 10 ? "\n…" : "");
            TaskDialog.Show("LiraSlabZones", text);
        }

        public static ZonePlacementReport PlaceIntoViews(Document doc, FamilySymbol? defaultSymbol,
            AnalysisResult analysis, View bottomView, View topView)
        {
            using var tx = new Transaction(doc, "LiraSlabZones: размещение зон");
            tx.Start();
            if (defaultSymbol != null && !defaultSymbol.IsActive) defaultSymbol.Activate();
            var report = ZonePlacer.Place(doc, defaultSymbol, analysis, bottomView, topView);
            tx.Commit();
            return report;
        }
    }

    /// <summary>
    /// Выбранные планы хранятся отдельно от LiraSlabZones.cfg: окно предпросмотра
    /// перезаписывает cfg своими настройками и не знает о видах Revit.
    /// </summary>
    internal sealed class RevitPlacementPrefs
    {
        public string? BottomViewName { get; set; }
        public string? TopViewName { get; set; }

        private static string PathName => Path.Combine(AppConfig.UserConfigDirectory, "LiraSlabZones.revit.json");

        public static RevitPlacementPrefs Load()
        {
            try
            {
                if (File.Exists(PathName))
                    return JsonConvert.DeserializeObject<RevitPlacementPrefs>(File.ReadAllText(PathName))
                           ?? new RevitPlacementPrefs();
            }
            catch { /* повреждённый файл — берём значения по умолчанию */ }
            return new RevitPlacementPrefs();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppConfig.UserConfigDirectory);
                File.WriteAllText(PathName, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch { /* не критично: выбор просто не запомнится */ }
        }
    }

    internal sealed class PlacementTargetDialog : Window
    {
        private readonly ComboBox _bottom;
        private readonly ComboBox _top;

        public string BottomViewName => (string)_bottom.SelectedItem;
        public string TopViewName => (string)_top.SelectedItem;

        public PlacementTargetDialog(IList<string> viewNames, string bottom, string top,
            int bottomCount, int topCount)
        {
            Title = "LiraSlabZones — размещение в Revit";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = "Зоны — элементы узлов: они будут видны только на выбранном плане.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            _bottom = AddCombo(panel, $"План низа — As1, As2 (зон: {bottomCount})", viewNames, bottom, bottomCount > 0);
            _top = AddCombo(panel, $"План верха — As3, As4 (зон: {topCount})", viewNames, top, topCount > 0);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button { Content = "Разместить", Width = 110, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, __) =>
            {
                if (bottomCount > 0 && topCount > 0 && BottomViewName == TopViewName &&
                    MessageBox.Show(this, "Низ и верх будут на одном плане. Продолжить?", Title,
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                DialogResult = true;
            };
            var cancel = new Button { Content = "Отмена", Width = 90, IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;
        }

        private static ComboBox AddCombo(System.Windows.Controls.Panel panel, string label, IList<string> items, string selected, bool enabled)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            var combo = new ComboBox
            {
                ItemsSource = items,
                SelectedItem = selected,
                IsEnabled = enabled,
                IsTextSearchEnabled = true
            };
            panel.Children.Add(combo);
            return combo;
        }
    }

    /// <summary>
    /// Окно предпросмотра немодальное: изменения в модели допустимы только из контекста
    /// Revit API, поэтому действие передаётся через ExternalEvent.
    /// </summary>
    internal sealed class RevitActionQueue : IExternalEventHandler
    {
        private Action<UIApplication>? _pending;
        private readonly ExternalEvent _event;

        public RevitActionQueue() => _event = ExternalEvent.Create(this);

        public void Run(Action<UIApplication> action)
        {
            _pending = action;
            _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            var action = _pending;
            _pending = null;
            if (action == null) return;
            try { action(app); }
            catch (Exception ex) { TaskDialog.Show("LiraSlabZones — ошибка", ex.Message); }
        }

        public string GetName() => "LiraSlabZones: размещение зон";
    }
}
