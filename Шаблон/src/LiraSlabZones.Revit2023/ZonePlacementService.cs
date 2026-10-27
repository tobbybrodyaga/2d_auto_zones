using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiraSlabZones.Core;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = Autodesk.Revit.DB.Grid;

namespace LiraSlabZones.Revit2023
{
    /// <summary>
    /// Связь отметки ЛИРЫ с Revit: уровень, планы несущих конструкций по рядам армирования
    /// и оси с этих планов.
    /// </summary>
    internal static class RevitLinkService
    {
        /// <summary>Выбор уровня, создание (или повторное использование) планов и чтение осей.</summary>
        /// <returns>null, если пользователь отменил выбор уровня.</returns>
        public static RevitLinkInfo? Link(UIApplication uiApp, AnalysisResult analysis,
            IEnumerable<RebarLayer>? extraLayers = null)
        {
            var doc = uiApp.ActiveUIDocument?.Document
                      ?? throw new InvalidOperationException("Нет активного документа Revit.");
            var elevation = analysis.ElevationZM;
            var settings = analysis.Settings ?? new AnalysisSettings();
            var layers = RevitLinkRules.LayersForPlans(settings)
                .Union(extraLayers ?? Enumerable.Empty<RebarLayer>())
                .OrderBy(layer => layer)
                .ToList();

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => Math.Abs(UnitConversion.FeetToMeters(level.Elevation) - elevation))
                .ToList();
            if (levels.Count == 0)
                throw new InvalidOperationException("В проекте Revit нет уровней.");

            var info = "Отметка плиты в программе: " + RevitLinkRules.FormatElevation(elevation) + "\n\n" +
                       "Будут созданы (или использованы существующие) планы — отметка в имени " +
                       "берётся из Revit, по верху перекрытия выбранного уровня:\n" +
                       string.Join("\n", layers.Select(layer =>
                           "• " + RevitLinkRules.PlanNamePrefix(layer, settings.ReverseZoneDirections) + "…")) +
                       "\n\nПо контурам перекрытий план ЛИРЫ совмещается с Revit (поворот и сдвиг), " +
                       "с планов читаются оси.";
            var templateName = AppConfig.LoadEffectiveSettings().RevitPlanTemplateName?.Trim();
            info += "\n\nШаблон вида: " + (string.IsNullOrEmpty(templateName)
                ? "не задан — останется шаблон типа вида"
                : FindPlanTemplate(doc) != null
                    ? templateName
                    : $"«{templateName}» не найден в проекте — останется шаблон типа вида");
            var items = levels.Select(level =>
            {
                var levelM = UnitConversion.FeetToMeters(level.Elevation);
                var deltaMm = Math.Round((levelM - elevation) * 1000);
                return $"{level.Name}  ({RevitLinkRules.FormatElevation(levelM)}; Δ = {deltaMm:+0;-0;0} мм)";
            }).ToList();
            var dialog = new ChoiceDialog("LiraSlabZones — связь с Revit", info,
                "Уровень Revit для планов (ближайший по отметке — первый):", items, 0, "Создать планы");
            new WindowInteropHelper(dialog) { Owner = uiApp.MainWindowHandle };
            if (dialog.ShowDialog() != true) return null;
            var level = levels[dialog.SelectedIndex];

            var link = new RevitLinkInfo
            {
                DocumentTitle = doc.Title,
                LiraElevationZM = elevation,
                LevelName = level.Name,
                LevelId = RevitIds.Value(level.Id),
                LevelElevationM = UnitConversion.FeetToMeters(level.Elevation)
            };
            link.Floors.AddRange(ReadFloors(doc, level));
            link.PlanElevationM = RevitLinkRules.PlanElevation(link.Floors, link.LevelElevationM);

            using var tx = new Transaction(doc, "LiraSlabZones: планы для отметки " +
                                                RevitLinkRules.FormatElevation(link.PlanElevationM));
            tx.Start();
            link.Plans.AddRange(EnsurePlans(doc, level, layers, settings, link.PlanElevationM));
            doc.Regenerate();
            ReadGrids(doc, link);
            tx.Commit();

            link.Registration = RevitRegistrationSolver.Solve(analysis, link);
            return link;
        }

        /// <summary>Перекрытия уровня: горизонтальные верхние грани, их контуры, отметка и толщина.</summary>
        private static IEnumerable<RevitFloorOutline> ReadFloors(Document doc, Level level)
        {
            const double squareFeetToM2 = 0.09290304;
            foreach (var floor in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
            {
                if (floor.LevelId != level.Id) continue;
                IList<Reference> tops;
                try { tops = HostObjectUtils.GetTopFaces(floor); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }

                var outline = new RevitFloorOutline
                {
                    ElementId = RevitIds.Value(floor.Id),
                    TypeName = floor.FloorType?.Name ?? string.Empty,
                    ThicknessMm = Math.Round(UnitConversion.FeetToMeters(
                        floor.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)?.AsDouble() ?? 0) * 1000)
                };
                double topArea = 0;
                foreach (var reference in tops)
                {
                    if (floor.GetGeometryObjectFromReference(reference) is not PlanarFace face ||
                        face.FaceNormal.Z < 0.99)
                        continue;
                    outline.AreaM2 += face.Area * squareFeetToM2;
                    if (face.Area > topArea)
                    {
                        topArea = face.Area;
                        outline.TopM = UnitConversion.FeetToMeters(face.Origin.Z);
                    }
                    foreach (var loop in face.GetEdgesAsCurveLoops())
                    {
                        var points = new List<Point3>();
                        foreach (var curve in loop)
                        {
                            var tessellated = curve.Tessellate();
                            for (var i = 0; i < tessellated.Count - 1; i++)
                                points.Add(new Point3(UnitConversion.FeetToMeters(tessellated[i].X),
                                    UnitConversion.FeetToMeters(tessellated[i].Y), 0));
                        }
                        if (points.Count >= 3) outline.Loops.Add(points);
                    }
                }
                if (outline.Loops.Count > 0) yield return outline;
            }
        }

        /// <summary>Связь годится для размещения: тот же документ и отметка, все нужные планы на месте.</summary>
        public static bool IsUsable(Document doc, RevitLinkInfo? link, double elevationZM,
            IEnumerable<RebarLayer> layers)
        {
            if (link == null || !link.Matches(elevationZM) || link.DocumentTitle != doc.Title) return false;
            return layers.All(layer => link.Plans.Any(plan => plan.Layer == layer &&
                doc.GetElement(RevitIds.From(plan.ViewId)) is ViewPlan));
        }

        private static IEnumerable<RevitLayerPlan> EnsurePlans(Document doc, Level level,
            IEnumerable<RebarLayer> layers, AnalysisSettings settings, double elevation)
        {
            var planType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(type => type.ViewFamily == ViewFamily.StructuralPlan)
                ?? throw new InvalidOperationException(
                    "В проекте нет типа вида «План несущих конструкций».");
            var plans = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(view => !view.IsTemplate)
                .ToList();
            var existing = plans
                .GroupBy(view => view.Name)
                .ToDictionary(group => group.Key, group => group.First());
            var template = FindPlanTemplate(doc);

            foreach (var layer in layers)
            {
                var name = RevitLinkRules.PlanName(layer, settings.ReverseZoneDirections, elevation);
                if (!existing.TryGetValue(name, out var plan))
                {
                    // План этого уровня с прежней отметкой в имени (например, отметкой уровня) —
                    // переименовать, а не плодить второй.
                    var prefix = RevitLinkRules.PlanNamePrefix(layer, settings.ReverseZoneDirections);
                    plan = plans.FirstOrDefault(view => view.GenLevel?.Id == level.Id &&
                                                        view.Name.StartsWith(prefix, StringComparison.Ordinal));
                    if (plan == null) plan = ViewPlan.Create(doc, planType.Id, level.Id);
                    plan.Name = name;
                }
                if (template != null && plan.ViewTemplateId != template.Id && plan.IsValidViewTemplate(template.Id))
                    plan.ViewTemplateId = template.Id;
                yield return new RevitLayerPlan { Layer = layer, ViewName = name, ViewId = RevitIds.Value(plan.Id) };
            }
        }

        /// <summary>
        /// Шаблон берётся из LiraSlabZones.cfg (RevitPlanTemplateName), а не из настроек окна:
        /// окно его не редактирует. Нет такого шаблона — остаётся шаблон типа вида.
        /// </summary>
        public static View? FindPlanTemplate(Document doc)
        {
            var name = AppConfig.LoadEffectiveSettings().RevitPlanTemplateName?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(view => view.IsTemplate && view.Name == name);
        }

        /// <summary>Оси, видимые на первом плане связи; дуговые и составные оси пропускаются.</summary>
        private static void ReadGrids(Document doc, RevitLinkInfo link)
        {
            var plan = link.Plans.Select(p => doc.GetElement(RevitIds.From(p.ViewId))).OfType<View>().FirstOrDefault();
            if (plan == null) return;
            foreach (var grid in new FilteredElementCollector(doc, plan.Id).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (grid.Curve is not Line line)
                {
                    link.SkippedGrids++;
                    continue;
                }
                var a = line.GetEndPoint(0);
                var b = line.GetEndPoint(1);
                link.Grids.Add(new RevitGridLine
                {
                    Name = grid.Name,
                    ElementId = RevitIds.Value(grid.Id),
                    X1 = UnitConversion.FeetToMeters(a.X),
                    Y1 = UnitConversion.FeetToMeters(a.Y),
                    X2 = UnitConversion.FeetToMeters(b.X),
                    Y2 = UnitConversion.FeetToMeters(b.Y)
                });
            }
        }
    }

    /// <summary>Размещение зон: каждый ряд — на свой план несущих конструкций.</summary>
    internal static class ZonePlacementService
    {
        /// <returns>Связь, по которой выполнено размещение (новая, если пришлось связать заново).</returns>
        public static RevitLinkInfo? PlaceWithPrompt(UIApplication uiApp, FamilySymbol? defaultSymbol,
            AnalysisResult analysis)
        {
            var doc = uiApp.ActiveUIDocument?.Document
                      ?? throw new InvalidOperationException("Нет активного документа Revit.");
            if (analysis.Zones == null || analysis.Zones.Count == 0)
            {
                TaskDialog.Show("LiraSlabZones", "Нет зон для размещения.");
                return analysis.RevitLink;
            }

            var zoneLayers = analysis.Zones.Select(zone => zone.Layer).Distinct().ToList();
            var link = analysis.RevitLink;
            if (!RevitLinkService.IsUsable(doc, link, analysis.ElevationZM, zoneLayers))
            {
                link = RevitLinkService.Link(uiApp, analysis, zoneLayers);
                if (link == null) return analysis.RevitLink;
                analysis.RevitLink = link;
            }

            var views = link!.Plans.ToDictionary(plan => plan.Layer,
                plan => doc.GetElement(RevitIds.From(plan.ViewId)) as View);
            ZonePlacementReport report;
            using (var tx = new Transaction(doc, "LiraSlabZones: размещение зон " +
                                                 RevitLinkRules.FormatElevation(analysis.ElevationZM)))
            {
                tx.Start();
                if (defaultSymbol != null && !defaultSymbol.IsActive) defaultSymbol.Activate();
                report = ZonePlacer.Place(doc, defaultSymbol, analysis,
                    layer => views.TryGetValue(layer, out var view) ? view : null);
                tx.Commit();
            }

            var text = $"Размещено: {report.Placed} из {analysis.Zones.Count}\n" +
                       string.Join("\n", link.Plans
                           .Where(plan => report.PlacedByLayer.ContainsKey(plan.Layer))
                           .Select(plan => $"  {plan.Layer} → «{plan.ViewName}»: {report.PlacedByLayer[plan.Layer]}"));
            if (report.Failures.Count > 0)
                text += $"\n\nНе размещено: {report.Failures.Count}\n" +
                        string.Join("\n", report.Failures.Take(10)) +
                        (report.Failures.Count > 10 ? "\n…" : "");
            TaskDialog.Show("LiraSlabZones", text);
            return link;
        }
    }

    internal static class RevitIds
    {
#if NET8_0_OR_GREATER
        public static long Value(ElementId id) => id.Value;
        public static ElementId From(long value) => new ElementId(value);
#else
        public static long Value(ElementId id) => id.IntegerValue;
        public static ElementId From(long value) => new ElementId((int)value);
#endif
    }

    /// <summary>Простое окно выбора одного значения из списка.</summary>
    internal sealed class ChoiceDialog : Window
    {
        private readonly ComboBox _combo;

        public int SelectedIndex => _combo.SelectedIndex;

        public ChoiceDialog(string title, string info, string label, IList<string> items, int selected,
            string okText)
        {
            Title = title;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = info, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) });
            _combo = new ComboBox { ItemsSource = items, SelectedIndex = selected, IsTextSearchEnabled = true };
            panel.Children.Add(_combo);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button { Content = okText, MinWidth = 120, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, __) => { if (_combo.SelectedIndex >= 0) DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button { Content = "Отмена", Width = 90, IsCancel = true });
            panel.Children.Add(buttons);
            Content = panel;
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

        public string GetName() => "LiraSlabZones: связь с Revit";
    }
}
