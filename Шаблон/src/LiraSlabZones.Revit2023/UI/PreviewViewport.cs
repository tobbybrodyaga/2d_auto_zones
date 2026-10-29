using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LiraSlabZones.Core;
using Newtonsoft.Json;

namespace LiraSlabZones.Revit2023.UI
{
    public enum ZoneEditMode { Select, Move, Resize, Create, Split, Merge, Delete, PerpendicularToEdge, CreateGap }
    public enum DiagnosticFilter { All, Uncovered, ZoneErrors, Conflicts, Off }

    /// <summary>
    /// Векторный превью-холст: зум без размытия, зоны и контур по сетке КЭ.
    /// </summary>
    public sealed partial class PreviewViewport : FrameworkElement
    {
        private const int MaxUndoActions = 50;
        private enum ResizeEdge { None, Left, Right, Bottom, Top }
        private AnalysisResult? _result;
        private IList<AdditionalZone>? _coverageReference;
        private readonly List<string> _undo = new();
        private string? _pendingUndo;
        private AnalysisSettings _settings = new AnalysisSettings();
        private bool _showMesh = true;
        private bool _showIso;
        private bool _showPatchBoundaries;
        private bool _showAxes;
        private DiagnosticFilter _diagnosticFilter = DiagnosticFilter.All;

        private double _zoom = 1.0;
        private double _panX;
        private double _panY;
        private bool _panning;
        private Point _panLast;

        private double _modelMinX, _modelMinY, _modelMaxX, _modelMaxY;
        private double _fitScale = 1;

        private class CachedShape
        {
            public StreamGeometry Geometry = null!;
            public double MinX;
            public double MaxX;
            public double MinY;
            public double MaxY;

            public bool Intersects(double minX, double maxX, double minY, double maxY) =>
                !(MaxX < minX || MinX > maxX || MaxY < minY || MinY > maxY);
        }

        private sealed class CachedZoneShape : CachedShape
        {
            public AdditionalZone Zone = null!;
            public Point3[] Contour = Array.Empty<Point3>();
            public double PatchLabelOffsetYpx;
        }

        private sealed class PatchElementValues
        {
            public int PlateIndex;
            public Point3 Position = null!;
            public string Text = string.Empty;
        }

        private sealed class CachedPatchFrame
        {
            public readonly List<ZonePatch> Patches = new();
            public readonly List<AdditionalZone> Zones = new();
            public double MinX;
            public double MaxX;
            public double MinY;
            public double MaxY;
            public CachedShape Shape = null!;
            public int SelectionId;
            public RebarLayer? Layer => Patches.Count > 0 && Patches.All(patch => patch.Layer == Patches[0].Layer)
                ? Patches[0].Layer
                : null;
        }

        private readonly List<CachedShape> _plateShapes = new();
        private readonly List<CachedZoneShape> _drawZones = new();
        private readonly List<(int Level, CachedShape Shape)> _drawPatchFills = new();
        private readonly List<CachedPatchFrame> _drawPatchFrames = new();
        private readonly List<CachedZoneShape> _drawPatchZones = new();
        private readonly List<AdditionalZone> _patchCandidateZones = new();
        private readonly List<PatchElementValues> _patchElementValues = new();
        private static readonly Dictionary<int, Brush> DiameterFillCache = new();
        private static readonly Dictionary<int, Brush> DiameterStrokeCache = new();
        private static readonly Dictionary<int, Brush> PatchFillCache = new();
        private static readonly Dictionary<(RebarLayer Layer, byte Alpha), Brush> LayerBrushCache = new();
        private static readonly Brush PatchValueTextBrush = CreatePatchValueTextBrush();
        private int? _selectedZoneId;
        private int? _selectedPatchFrameId;
        private ZoneEditMode _editMode;
        private AdditionalZone? _editZone;
        private AdditionalZone? _mergeZone;
        private AdditionalZone? _gapMovingZone;
        private Point3? _editStart;
        private ResizeEdge _resizeEdge;
        private (double MinX, double MaxX, double MinY, double MaxY) _resizeBounds;
        public event Action<AdditionalZone>? ZoneSelected;
        public event Action<ZonePatchFrameSelection?>? PatchSelected;
        public event Action? ZonesEdited;
        public event Action<string>? StatusChanged;

        public void SetDiagnosticFilter(DiagnosticFilter filter)
        {
            _diagnosticFilter = filter;
            InvalidateVisual();
        }

        private static readonly Brush Bg = Brushes.White;
        private static readonly Pen OutlinePen = FreezePen(Color.FromRgb(29, 78, 216), 2.0, dash: true);
        private static readonly Pen MeshPen = FreezePen(Color.FromArgb(80, 55, 65, 81), 0.4);

        public double Zoom => _zoom;
        public ZoneConflictGroupResult? LastConflictResolution { get; private set; }
        public Action<string, int, int>? LayoutProgress { get; set; }
        public int EditableZoneCount => _result?.PatchPreviewOnly == true
            ? _patchCandidateZones.Count
            : _result?.Zones.Count ?? 0;

        private List<AdditionalZone> WorkingZones => _result?.PatchPreviewOnly == true
            ? _patchCandidateZones
            : _result?.Zones ?? new List<AdditionalZone>();

        /// <summary>Снимок зон, которые сейчас показаны на холсте (с учётом ручных правок и отмены).</summary>
        public List<AdditionalZone> PlacementZones => WorkingZones.ToList();

        /// <summary>Итог последней привязки зон к осям Revit; null — осей нет.</summary>
        public GridAlignmentReport? LastAlignment { get; private set; }

        public bool UndoLastEdit()
        {
            if (_result == null || _undo.Count == 0) return false;
            var last = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            var restored = JsonConvert.DeserializeObject<List<AdditionalZone>>(last) ?? new List<AdditionalZone>();
            if (_result.PatchPreviewOnly)
            {
                _patchCandidateZones.Clear();
                _patchCandidateZones.AddRange(restored);
            }
            else _result.Zones = restored;
            _pendingUndo = null;
            _editStart = null;
            _editZone = null;
            _mergeZone = null;
            _gapMovingZone = null;
            CommitEdits();
            RaiseStatus($"Отменено действие · осталось {_undo.Count} из {MaxUndoActions}");
            return true;
        }

        private void BeginEdit() => _pendingUndo = _result == null ? null : JsonConvert.SerializeObject(WorkingZones);
        private AdditionalZone? SelectedZone => WorkingZones.FirstOrDefault(z => z.ZoneId == _selectedZoneId);

        public bool ResizeSelectedZone(double lengthMm, double widthMm)
        {
            var zone = SelectedZone;
            if (zone == null || _result == null ||
                lengthMm <= 50 || widthMm <= 50) return false;
            BeginEdit();
            if (!ZoneEditor.ResizeByDimensions(zone, lengthMm, widthMm, _result.Outline,
                    clipToSlab: !_result.PatchPreviewOnly))
            { _pendingUndo = null; return false; }
            CommitEdits(zone);
            return true;
        }

        public bool SetSelectedFamily(ZoneFamilyKind kind, string name)
        {
            var zone = SelectedZone;
            if (zone == null || string.IsNullOrWhiteSpace(name)) return false;
            BeginEdit();
            ZoneEditor.SetFamily(zone, kind, name);
            CommitEdits(zone);
            return true;
        }

        public bool SetSelectedDiameter(int diameterMm)
        {
            if (_result == null || !_selectedZoneId.HasValue) return false;
            var zone = WorkingZones.FirstOrDefault(z => z.ZoneId == _selectedZoneId.Value);
            if (zone == null) return false;
            var background = zone.Layer == RebarLayer.As1 || zone.Layer == RebarLayer.As2
                ? _settings.BgBottomDiameterMm
                : _settings.BgTopDiameterMm;
            if (diameterMm < background)
            {
                RaiseStatus($"Ø{diameterMm} меньше фонового Ø{background}");
                return false;
            }
            BeginEdit();
            ZoneEditor.SetDiameter(zone, diameterMm);
            CommitEdits(zone);
            return true;
        }

        public bool SetSelectedStep(int stepMm)
        {
            var zone = SelectedZone;
            if (zone == null || (stepMm != 100 && stepMm != 200)) return false;
            BeginEdit();
            ZoneEditor.SetStep(zone, stepMm);
            CommitEdits(zone);
            return true;
        }

        public void SetEditMode(ZoneEditMode mode)
        {
            _editMode = mode;
            _editZone = null;
            _editStart = null;
            _resizeEdge = ResizeEdge.None;
            if (mode != ZoneEditMode.Merge) _mergeZone = null;
            if (mode != ZoneEditMode.CreateGap) _gapMovingZone = null;
            Cursor = mode == ZoneEditMode.Move ? Cursors.SizeAll :
                mode == ZoneEditMode.Delete ? Cursors.No : Cursors.Cross;
            RaiseStatus($"Редактирование зон: {mode}");
        }

        public void SetData(AnalysisResult? result, AnalysisSettings settings, bool showMesh, bool showIso,
            bool showAxes = false, bool fitView = true, bool showPatchBoundaries = false,
            IList<AdditionalZone>? coverageReference = null)
        {
            if (!ReferenceEquals(_result, result))
            {
                _undo.Clear();
                _pendingUndo = null;
                _selectedPatchFrameId = null;
            }
            _result = result;
            LastConflictResolution = null;
            _coverageReference = coverageReference;
            _settings = settings;
            _showMesh = showMesh;
            _showIso = showIso;
            _showAxes = showAxes;
            _showPatchBoundaries = showPatchBoundaries;
            ComputeModelExtents();
            RebuildGeometryCache();
            if (result?.PatchPreviewOnly == true) ComputeModelExtents();
            if (fitView) FitToView();
            InvalidateVisual();
        }

        public void RefreshDisplayFlags(bool showMesh, bool showIso, bool showAxes = false,
            bool showPatchBoundaries = false)
        {
            _showMesh = showMesh;
            _showIso = showIso;
            _showAxes = showAxes;
            _showPatchBoundaries = showPatchBoundaries;
            ComputeModelExtents();
            InvalidateVisual();
        }

        /// <summary>Обновить смещение/поворот без повторной загрузки данных.</summary>
        public void RefreshTransform(AnalysisSettings settings, bool fit = true)
        {
            _settings = settings ?? _settings;
            ComputeModelExtents();
            RebuildGeometryCache();
            if (_result?.PatchPreviewOnly == true) ComputeModelExtents();
            if (fit) FitToView();
            InvalidateVisual();
            RaiseStatus();
        }

        private Point Tx(Point3 p) => Tx(p.X, p.Y);

        // То же преобразование использует ZonePlacer при вставке в Revit.
        private LiraRevitTransform Transform => new LiraRevitTransform(_settings, _pivotX, _pivotY);

        private Point Tx(double x, double y)
        {
            var (rx, ry) = Transform.ToRevit(x, y);
            return new Point(rx, ry);
        }

        private Point3 UnTx(double x, double y)
        {
            var (lx, ly) = Transform.ToLira(x, y);
            return new Point3(lx, ly, 0);
        }

        private double _pivotX, _pivotY;

        public void ZoomBy(double factor, Point? anchorScreen = null)
        {
            SetZoom(_zoom * factor, anchorScreen);
        }

        public void SetZoom(double zoom, Point? anchorScreen = null)
        {
            zoom = Math.Max(0.2, Math.Min(40.0, zoom));
            if (Math.Abs(zoom - _zoom) < 1e-6) return;

            var anchor = anchorScreen ?? new Point(ActualWidth / 2, ActualHeight / 2);
            var before = ScreenToTransformed(anchor);
            _zoom = zoom;
            var after = ScreenToTransformed(anchor);
            _panX += (after.X - before.X) * _fitScale * _zoom;
            _panY -= (after.Y - before.Y) * _fitScale * _zoom;
            InvalidateVisual();
            RaiseStatus();
        }

        public void ResetZoom() { _zoom = 1; _panX = 0; _panY = 0; FitToView(); InvalidateVisual(); RaiseStatus(); }

        public void FitToView()
        {
            if (ActualWidth < 10 || ActualHeight < 10) return;
            double mw = Math.Max(0.1, _modelMaxX - _modelMinX);
            double mh = Math.Max(0.1, _modelMaxY - _modelMinY);
            _fitScale = Math.Min((ActualWidth - 24) / mw, (ActualHeight - 24) / mh);
            _panX = 0;
            _panY = 0;
            _zoom = 1;
            RaiseStatus();
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (_result == null || _result.Plates.Count == 0) return;

            double s = _fitScale * _zoom;
            if (s < 1e-9) return;

            // толщина пера в единицах модели → ~N пикселей на экране
            double penW = Math.Max(1e-4, 1.6 / s);

            var world = new Matrix();
            world.Translate(-_modelMinX, -_modelMinY);
            world.Scale(s, -s); // Y вверх → экран вниз
            world.Translate(12 + _panX + (ActualWidth - 24 - (_modelMaxX - _modelMinX) * s) * 0.5,
                            12 + _panY + (ActualHeight - 24 - (_modelMaxY - _modelMinY) * s) * 0.5 + (_modelMaxY - _modelMinY) * s);

            dc.PushTransform(new MatrixTransform(world));

            var tl = ScreenToTransformed(new Point(0, 0));
            var br = ScreenToTransformed(new Point(ActualWidth, ActualHeight));
            double vMinX = Math.Min(tl.X, br.X) - 1;
            double vMaxX = Math.Max(tl.X, br.X) + 1;
            double vMinY = Math.Min(tl.Y, br.Y) - 1;
            double vMaxY = Math.Max(tl.Y, br.Y) + 1;

            // --- Стиль активного вида ЛИРА: светлая заливка КЭ + тонкая сетка ---
            var plateFill = new SolidColorBrush(Color.FromRgb(248, 248, 250));
            plateFill.Freeze();
            var meshPen = new Pen(new SolidColorBrush(Color.FromRgb(120, 130, 145)), Math.Max(1e-4, 0.9 / s));
            meshPen.Freeze();

            // сетка КЭ как в ВИЗОРе
            {
                int m = 0;
                int step = (!_showMesh && _zoom < 1.2) ? 2 : (_zoom < 1.0 ? 2 : 1);
                if (!_showMesh) step = Math.Max(step, _result.Plates.Count > 8000 ? 3 : 1);

                for (int i = 0; i < _result.Plates.Count; i += step)
                {
                    var plate = _result.Plates[i];
                    var shape = _plateShapes[i];
                    if (!shape.Intersects(vMinX, vMaxX, vMinY, vMaxY)) continue;
                    if (m++ > 18000) break;
                    dc.DrawGeometry(plateFill, _showMesh ? meshPen : null, shape.Geometry);
                }
            }

            int drawn = 0;
            const int maxDraw = 12000;

            // изополя As: цвет закреплён за интервалом шкалы (как Ogibayushchaya)
            if (_showIso && !_result.PatchPreviewOnly)
            {
                double step = _settings.VisualizationScale;
                if (step <= 0) step = 1.0;
                drawn = 0;
                bool isoLabels = _zoom >= 1.6;
                var isoTypeface = new Typeface("Segoe UI");
                var isoBrushes = new Brush?[26];
                var isoLabelBrush = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20));
                isoLabelBrush.Freeze();
                for (var i = 0; i < _result.Plates.Count; i++)
                {
                    var plate = _result.Plates[i];
                    var shape = _plateShapes[i];
                    if (!shape.Intersects(vMinX, vMaxX, vMinY, vMaxY)) continue;
                    if (drawn++ > maxDraw) break;
                    if (!plate.Rebar.Ok) continue;

                    var asAdd = IsoAdditionalAs(plate.Rebar, _settings);
                    if (asAdd <= 0.01) continue;

                    var level = IsoColorScale.LevelForValue(asAdd, step);
                    var brush = isoBrushes[level];
                    if (brush == null)
                    {
                        var rgb = IsoColorScale.ColorForValue(asAdd, step);
                        brush = new SolidColorBrush(Color.FromArgb(160, rgb.R, rgb.G, rgb.B));
                        brush.Freeze();
                        isoBrushes[level] = brush;
                    }
                    dc.DrawGeometry(brush, null, shape.Geometry);

                    if (isoLabels && drawn <= 4000)
                    {
                        var tc = Tx(plate.Centroid.X, plate.Centroid.Y);
                        double fontModel = Math.Max(0.055, 8.0 / s);
                        var ft = new FormattedText(
                            asAdd.ToString("0.#"),
                            System.Globalization.CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            isoTypeface,
                            fontModel,
                            isoLabelBrush,
                            1.0);
                        DrawUprightText(dc, ft, tc);
                    }
                }
            }

            if (_result.PatchPreviewOnly)
            {
                foreach (var fill in _drawPatchFills)
                {
                    if (fill.Shape.Intersects(vMinX, vMaxX, vMinY, vMaxY))
                        dc.DrawGeometry(PatchFill(fill.Level), null, fill.Shape.Geometry);
                }

                var patchZoneDiagnostics = GetDiagnosticZoneBrushes();
                DrawPatchZoneOutlines(dc, s, vMinX, vMaxX, vMinY, vMaxY,
                    patchZoneDiagnostics);

                if (_showPatchBoundaries)
                {
                    foreach (var frame in _drawPatchFrames)
                    {
                        if (!frame.Shape.Intersects(vMinX, vMaxX, vMinY, vMaxY)) continue;
                        var selected = frame.SelectionId == _selectedPatchFrameId;
                        if (selected)
                        {
                            var selectionOutline = new Pen(Brushes.White, Math.Max(1e-4, 5.0 / s))
                            {
                                DashStyle = DashStyles.Dash
                            };
                            selectionOutline.Freeze();
                            dc.DrawGeometry(null, selectionOutline, frame.Shape.Geometry);
                        }
                        var frameBrush = frame.Layer.HasValue
                            ? LayerBrush(frame.Layer.Value, 255)
                            : Brushes.DarkSlateGray;
                        var outline = new Pen(frameBrush,
                            Math.Max(1e-4, (selected ? 3.0 : 2.0) / s))
                        {
                            DashStyle = DashStyles.Dash
                        };
                        outline.Freeze();
                        dc.DrawGeometry(null, outline, frame.Shape.Geometry);
                    }
                }

                DrawPatchElementValues(dc, s, vMinX, vMaxX, vMinY, vMaxY);
                DrawPatchZoneLabels(dc, s, vMinX, vMaxX, vMinY, vMaxY);
                DrawAxes(dc, s, penW);
                DrawRevitGrids(dc, s);
                dc.Pop();
                DrawElevationBadge(dc);
                DrawDiameterLegend(dc);
                DrawDirectionAxes(dc);
                return;
            }

            // зоны доп.армирования поверх сетки
            var diagnostics = _result.Diagnostics ?? new ZoneDiagnostics();
            bool showUncovered = _diagnosticFilter == DiagnosticFilter.All ||
                _diagnosticFilter == DiagnosticFilter.Uncovered;
            var uncoveredIds = new HashSet<int>(diagnostics.Issues
                .Where(issue => showUncovered && issue.Kind == ZoneIssueKind.UncoveredElement)
                .Select(issue => issue.ElementId));
            if (uncoveredIds.Count > 0)
            {
                var issueFill = new SolidColorBrush(Color.FromArgb(105, 219, 39, 119));
                var issuePen = new Pen(Brushes.DeepPink, Math.Max(1e-4, 2.0 / s));
                issueFill.Freeze(); issuePen.Freeze();
                for (var i = 0; i < _result.Plates.Count; i++)
                    if (uncoveredIds.Contains(_result.Plates[i].Id) &&
                        _plateShapes[i].Intersects(vMinX, vMaxX, vMinY, vMaxY))
                        dc.DrawGeometry(issueFill, issuePen, _plateShapes[i].Geometry);
            }

            var diagnosticZoneBrushes = GetDiagnosticZoneBrushes();
            drawn = 0;
            bool labels = _zoom >= 1.6;
            bool dims = _zoom >= 1.5;
            var typeface = new Typeface("Segoe UI");
            var dimPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)), Math.Max(1e-4, 1.0 / s));
            dimPen.Freeze();

            var zoneBoxes = new List<(AdditionalZone Zone, double MinX, double MaxX, double MinY, double MaxY)>();
            foreach (var shape in _drawZones)
            {
                var zone = shape.Zone;
                if (!shape.Intersects(vMinX, vMaxX, vMinY, vMaxY)) continue;
                if (drawn++ > maxDraw) break;

                var fill = DiameterFill(zone.DiameterMm, 70);
                bool selected = _selectedZoneId == zone.ZoneId;
                var statusStroke = ZoneAttentionBrush(zone);
                var hasDiagnostic = diagnosticZoneBrushes.TryGetValue(zone.ZoneId, out var diagnosticStroke);
                diagnosticStroke ??= statusStroke ?? DiameterStroke(zone.DiameterMm);
                bool hasAttention = statusStroke != null || hasDiagnostic;
                if (selected && hasAttention)
                {
                    var selectionHalo = new Pen(Brushes.White, Math.Max(1e-4, 4.2 / s));
                    selectionHalo.Freeze();
                    dc.DrawGeometry(null, selectionHalo, shape.Geometry);
                }
                var zoneOutline = new Pen(
                    selected && !hasAttention ? Brushes.Black : diagnosticStroke,
                    Math.Max(1e-4, (selected || hasAttention ? 2.4 : 1.35) / s))
                {
                    DashStyle = DashStyles.Dash
                };
                zoneOutline.Freeze();
                dc.DrawGeometry(fill, zoneOutline, shape.Geometry);

                double minX = shape.MinX, maxX = shape.MaxX;
                double minY = shape.MinY, maxY = shape.MaxY;
                double cx = (minX + maxX) * 0.5, cy = (minY + maxY) * 0.5;
                zoneBoxes.Add((zone, minX, maxX, minY, maxY));

                if (labels && zone.DiameterMm > 0)
                {
                    var tc = Tx(cx, cy);
                    double fontModel = Math.Max(0.065, 9.0 / s);
                    var (line1, line2) = BuildZoneLabelLines(zone);
                    var ft1 = new FormattedText(
                        line1,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        fontModel,
                        Brushes.Black,
                        1.0);
                    FormattedText? ft2 = null;
                    if (!string.IsNullOrEmpty(line2))
                    {
                        ft2 = new FormattedText(
                            line2,
                            System.Globalization.CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            typeface,
                            fontModel * 0.92,
                            Brushes.Black,
                            1.0);
                    }
                    double tw = Math.Max(ft1.Width, ft2?.Width ?? 0);
                    double th = ft1.Height + (ft2?.Height ?? 0) + fontModel * 0.12;
                    var pad = fontModel * 0.28;
                    var bg = new Rect(tc.X - tw / 2 - pad, tc.Y - th / 2 - pad,
                        tw + pad * 2, th + pad * 2);
                    dc.PushTransform(new ScaleTransform(1, -1, tc.X, tc.Y));
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), null, bg);
                    double y0 = tc.Y - th / 2;
                    dc.DrawText(ft1, new Point(tc.X - ft1.Width / 2, y0));
                    if (ft2 != null)
                        dc.DrawText(ft2, new Point(tc.X - ft2.Width / 2, y0 + ft1.Height + fontModel * 0.12));
                    dc.Pop();
                }
            }

            // Одна размерная цепочка по всем зонам + привязка к осям (без наложений)
            if (dims && zoneBoxes.Count > 0)
                DrawGlobalDimensionChains(dc, zoneBoxes, dimPen, typeface, s);

            // оси из ЛИРА
            DrawAxes(dc, s, penW);
                DrawRevitGrids(dc, s);

            dc.Pop();

            // подпись отметки в экранных координатах (не масштабируется с моделью)
            DrawElevationBadge(dc);
            DrawDiameterLegend(dc);
            DrawDirectionAxes(dc);
        }

        private Brush PatchFill(int level)
        {
            if (PatchFillCache.TryGetValue(level, out var cached)) return cached;
            var rgb = IsoColorScale.ColorForValue(level - 0.5, 1);
            var brush = new SolidColorBrush(Color.FromArgb(175, rgb.R, rgb.G, rgb.B));
            brush.Freeze();
            PatchFillCache[level] = brush;
            return brush;
        }

        private static Brush CreatePatchValueTextBrush()
        {
            var brush = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20));
            brush.Freeze();
            return brush;
        }

        private void DrawDirectionAxes(DrawingContext dc)
        {
            var origin = new Point(35, ActualHeight - 35);
            var pen = new Pen(Brushes.DarkSlateGray, 2);
            dc.DrawLine(pen, origin, new Point(origin.X + 32, origin.Y));
            dc.DrawLine(pen, origin, new Point(origin.X, origin.Y - 32));
            var face = new Typeface("Segoe UI");
            dc.DrawText(new FormattedText("X", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, face, 13, Brushes.DarkSlateGray, 1),
                new Point(origin.X + 35, origin.Y - 10));
            dc.DrawText(new FormattedText("Y", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, face, 13, Brushes.DarkSlateGray, 1),
                new Point(origin.X - 5, origin.Y - 51));

            var firstDirection = RebarTables.DirectionForLayer(
                RebarLayer.As1, _settings.ReverseZoneDirections);
            var secondDirection = RebarTables.DirectionForLayer(
                RebarLayer.As2, _settings.ReverseZoneDirections);
            var layoutText = $"Пятна: As1/As3 вдоль {firstDirection}  ·  As2/As4 вдоль {secondDirection}";
            dc.DrawText(new FormattedText(layoutText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, face, 12, Brushes.DarkSlateGray, 1),
                new Point(origin.X + 50, origin.Y - 18));
        }

        private void DrawDiameterLegend(DrawingContext dc)
        {
            var activeZones = _result?.PatchPreviewOnly == true
                ? _drawPatchZones.Select(item => item.Zone)
                : _drawZones.Select(item => item.Zone);
            var diameters = activeZones
                .Where(zone => zone.DiameterMm > 0)
                .Select(zone => zone.DiameterMm)
                .Distinct()
                .OrderBy(d => d)
                .ToList();
            if (diameters.Count == 0) return;

            var typeface = new Typeface("Segoe UI");
            const double fontSize = 12;
            const double rowHeight = 22;
            const double width = 132;
            var height = 34 + diameters.Count * rowHeight;
            var x = Math.Max(8, ActualWidth - width - 12);
            const double y = 12;
            var panel = new Rect(x, y, width, height);
            var panelFill = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
            panelFill.Freeze();
            var panelPen = new Pen(new SolidColorBrush(Color.FromRgb(203, 213, 225)), 1);
            panelPen.Freeze();
            dc.DrawRoundedRectangle(panelFill, panelPen, panel, 4, 4);

            var title = new FormattedText(
                "Диаметр зон",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.Black,
                1.0);
            dc.DrawText(title, new Point(x + 10, y + 8));

            for (var i = 0; i < diameters.Count; i++)
            {
                var diameter = diameters[i];
                var rowY = y + 32 + i * rowHeight;
                dc.DrawRectangle(
                    DiameterFill(diameter, 170),
                    new Pen(DiameterStroke(diameter), 1),
                    new Rect(x + 10, rowY + 3, 16, 16));
                var label = new FormattedText(
                    $"Ø{diameter} мм",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.Black,
                    1.0);
                dc.DrawText(label, new Point(x + 34, rowY + 2));
            }
        }

        /// <summary>
        /// Оси из связанного плана Revit. Они уже в координатах Revit, как и холст после Tx,
        /// поэтому рисуются без преобразования — так видно, совпадает ли план ЛИРЫ с осями.
        /// </summary>
        private void DrawRevitGrids(DrawingContext dc, double s)
        {
            var grids = _result?.RevitLink?.Grids;
            if (grids == null || grids.Count == 0 || _modelMinX > _modelMaxX) return;

            var span = Math.Max(_modelMaxX - _modelMinX, _modelMaxY - _modelMinY);
            var margin = span * 0.06 + 0.6;
            double x0 = _modelMinX - margin, x1 = _modelMaxX + margin;
            double y0 = _modelMinY - margin, y1 = _modelMaxY + margin;

            var color = Color.FromRgb(185, 28, 28);
            var pen = new Pen(new SolidColorBrush(color), Math.Max(1e-4, 1.1 / s))
            {
                DashStyle = DashStyles.DashDot
            };
            pen.Freeze();
            var bubblePen = new Pen(new SolidColorBrush(color), Math.Max(1e-4, 1.1 / s));
            bubblePen.Freeze();
            var typeface = new Typeface("Segoe UI");
            var r = Math.Max(0.18, Math.Min(0.55, 13.0 / s));

            foreach (var grid in grids)
            {
                if (!ClipSegment(grid.X1, grid.Y1, grid.X2, grid.Y2, x0, x1, y0, y1, out var a, out var b))
                    continue;
                dc.DrawLine(pen, a, b);
                // Кружок — на верхнем конце почти вертикальной оси и на левом конце горизонтальной.
                var vertical = Math.Abs(b.Y - a.Y) >= Math.Abs(b.X - a.X);
                var bubble = vertical ? (a.Y > b.Y ? a : b) : (a.X < b.X ? a : b);
                DrawAxisBubble(dc, bubble, grid.Name, r, Brushes.White, bubblePen, typeface, s);
            }
        }

        /// <summary>Отсечение отрезка прямоугольником (Лианг — Барски).</summary>
        private static bool ClipSegment(double ax, double ay, double bx, double by,
            double minX, double maxX, double minY, double maxY, out Point a, out Point b)
        {
            double t0 = 0, t1 = 1, dx = bx - ax, dy = by - ay;
            bool Clip(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0;
                var t = q / p;
                if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
                return true;
            }
            a = b = default;
            if (!Clip(-dx, ax - minX) || !Clip(dx, maxX - ax) ||
                !Clip(-dy, ay - minY) || !Clip(dy, maxY - ay))
                return false;
            a = new Point(ax + t0 * dx, ay + t0 * dy);
            b = new Point(ax + t1 * dx, ay + t1 * dy);
            return true;
        }

        private void DrawAxes(DrawingContext dc, double s, double penW)
        {
            if (!_showAxes || _result?.Axes == null || _result.Axes.Count == 0) return;
            if (!TryGetSlabBounds(out double rawMinX, out double rawMaxX, out double rawMinY, out double rawMaxY))
                return;

            var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), Math.Max(1e-4, 1.15 / s))
            {
                DashStyle = DashStyles.Dash
            };
            axisPen.Freeze();
            var bubbleFill = Brushes.White;
            var bubblePen = new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), Math.Max(1e-4, 1.1 / s));
            bubblePen.Freeze();
            var typeface = new Typeface("Segoe UI");

            double span = Math.Max(rawMaxX - rawMinX, rawMaxY - rawMinY);
            double r = Math.Max(0.18, Math.Min(0.55, 13.0 / s));
            // вынос маркеров: одна линия сверху (вертикальные оси) и одна слева (горизонтальные)
            double gap = Math.Max(r * 2.8, span * 0.04 + 0.4);
            double bubbleY = rawMaxY + gap;   // ряд кружков сверху
            double bubbleX = rawMinX - gap;   // ряд кружков слева
            // линии только снаружи контура: от маркера до грани плиты
            double edgePad = Math.Max(0.02, r * 0.15);

            foreach (var ax in _result.Axes)
            {
                bool vertical = ax.Vertical;
                double pos = ax.Position;
                if (ax.IsSegment)
                {
                    double dx = Math.Abs(ax.X2 - ax.X1);
                    double dy = Math.Abs(ax.Y2 - ax.Y1);
                    vertical = dx <= dy;
                    pos = vertical ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
                }

                if (vertical)
                {
                    // ось X=const: кружок сверху, штрих вниз до верхнего края плиты
                    if (pos < rawMinX - 1 || pos > rawMaxX + 1) continue;
                    var bubbleAt = Tx(pos, bubbleY);
                    var edge = Tx(pos, rawMaxY + edgePad);
                    dc.DrawLine(axisPen, bubbleAt, edge);
                    DrawAxisBubble(dc, bubbleAt, ax.Name, r, bubbleFill, bubblePen, typeface, s);
                }
                else
                {
                    // ось Y=const: кружок слева, штрих вправо до левого края плиты
                    if (pos < rawMinY - 1 || pos > rawMaxY + 1) continue;
                    var bubbleAt = Tx(bubbleX, pos);
                    var edge = Tx(rawMinX - edgePad, pos);
                    dc.DrawLine(axisPen, bubbleAt, edge);
                    DrawAxisBubble(dc, bubbleAt, ax.Name, r, bubbleFill, bubblePen, typeface, s);
                }
            }
        }

        private bool TryGetSlabBounds(out double minX, out double maxX, out double minY, out double maxY)
        {
            minX = double.MaxValue; maxX = double.MinValue;
            minY = double.MaxValue; maxY = double.MinValue;
            if (_result == null) return false;

            double x0 = double.MaxValue, x1 = double.MinValue;
            double y0 = double.MaxValue, y1 = double.MinValue;

            void Acc(Point3 p)
            {
                if (p.X < x0) x0 = p.X;
                if (p.X > x1) x1 = p.X;
                if (p.Y < y0) y0 = p.Y;
                if (p.Y > y1) y1 = p.Y;
            }

            if (_result.Outline != null && _result.Outline.Count >= 3)
            {
                foreach (var p in _result.Outline) Acc(p);
            }
            else
            {
                foreach (var plate in _result.Plates)
                foreach (var p in plate.Contour)
                    Acc(p);
            }

            if (x0 >= x1 || y0 >= y1) return false;
            minX = x0; maxX = x1; minY = y0; maxY = y1;
            return true;
        }

        private static void DrawAxisBubble(
            DrawingContext dc, Point center, string name, double r,
            Brush fill, Pen pen, Typeface typeface, double s)
        {
            dc.DrawEllipse(fill, pen, center, r, r);
            double fontModel = Math.Max(0.08, 10.0 / s);
            var ft = new FormattedText(
                name ?? "",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontModel,
                new SolidColorBrush(Color.FromRgb(30, 64, 175)),
                1.0);
            DrawUprightText(dc, ft, center);
        }

        /// <summary>
        /// Мир рисуется с Scale(s,-s), поэтому обычный DrawText получается вверх ногами.
        /// Локальный Scale(1,-1) вокруг точки возвращает текст «лицом вверх».
        /// </summary>
        private static void DrawUprightText(DrawingContext dc, FormattedText ft, Point center)
        {
            dc.PushTransform(new ScaleTransform(1, -1, center.X, center.Y));
            dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
            dc.Pop();
        }

        private void DrawElevationBadge(DrawingContext dc)
        {
            if (_result == null || string.IsNullOrWhiteSpace(_result.ElevationLabel)) return;
            var text = "Отметка КЭ: " + _result.ElevationLabel;
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                14,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double pad = 8;
            var rect = new Rect(12, 12, ft.Width + pad * 2, ft.Height + pad);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 17, 24, 39)), null, rect, 4, 4);
            dc.DrawText(ft, new Point(rect.X + pad, rect.Y + pad * 0.5));
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            ZoomBy(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(this));
            e.Handled = true;
            base.OnMouseWheel(e);
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            _panning = true;
            _panLast = e.GetPosition(this);
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            base.OnMouseRightButtonDown(e);
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
            base.OnMouseRightButtonUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_panning)
            {
                var p = e.GetPosition(this);
                _panX += p.X - _panLast.X;
                _panY += p.Y - _panLast.Y;
                _panLast = p;
                InvalidateVisual();
            }
            else
            {
                var m = ScreenToModel(e.GetPosition(this));
                if (_resizeEdge != ResizeEdge.None && _editZone != null && _result != null)
                {
                    if (ResizeDraggedEdge(_editZone, m))
                    {
                        RebuildZoneGeometryCache();
                        InvalidateVisual();
                    }
                }
                else if (_editMode == ZoneEditMode.Select || _editMode == ZoneEditMode.Resize)
                {
                    var edge = HitResizeEdge(m);
                    Cursor = edge.Edge == ResizeEdge.Left || edge.Edge == ResizeEdge.Right
                        ? Cursors.SizeWE : edge.Edge == ResizeEdge.Top || edge.Edge == ResizeEdge.Bottom
                            ? Cursors.SizeNS : Cursors.Arrow;
                }
                var edit = _editStart != null ? $" | {_editMode}: отпустите ЛКМ" : "";
                RaiseStatus($"X={m.X:F2} Y={m.Y:F2} м | зум {_zoom * 100:0}%{edit} | колесо зум, ПКМ пан");
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var m = ScreenToModel(e.GetPosition(this));
            if (_result != null && (_editMode == ZoneEditMode.Select || _editMode == ZoneEditMode.Resize))
            {
                var edge = HitResizeEdge(m);
                if (edge.Zone != null && edge.Edge != ResizeEdge.None)
                {
                    _editZone = edge.Zone;
                    _resizeEdge = edge.Edge;
                    _resizeBounds = (edge.Zone.Contour.Min(p => p.X), edge.Zone.Contour.Max(p => p.X),
                        edge.Zone.Contour.Min(p => p.Y), edge.Zone.Contour.Max(p => p.Y));
                    _editStart = m;
                    _selectedZoneId = edge.Zone.ZoneId;
                    BeginEdit();
                    CaptureMouse();
                    ZoneSelected?.Invoke(edge.Zone);
                    e.Handled = true;
                    base.OnMouseLeftButtonDown(e);
                    return;
                }
            }
            var hit = HitZone(m);
            if (_result?.PatchPreviewOnly == true && _editMode == ZoneEditMode.Select)
            {
                SelectPatchFrameAt(m);
                if (hit != null)
                {
                    _selectedZoneId = hit.ZoneId;
                    ZoneSelected?.Invoke(hit);
                }
                else _selectedZoneId = null;
                InvalidateVisual();
                e.Handled = hit != null || _selectedPatchFrameId.HasValue;
                base.OnMouseLeftButtonDown(e);
                return;
            }
            if (_result != null && _editMode != ZoneEditMode.Select)
            {
                if (_editMode == ZoneEditMode.Delete && hit != null)
                {
                    BeginEdit();
                    WorkingZones.Remove(hit);
                    CommitEdits();
                }
                else if (_editMode == ZoneEditMode.Split && hit != null)
                {
                    var pieces = ZoneEditor.Split(hit,
                        hit.Direction == ZoneDirection.X ? m.X : m.Y,
                        hit.Direction == ZoneDirection.X, _result.Outline,
                        clipToSlab: !_result.PatchPreviewOnly);
                    if (pieces.Count == 2)
                    {
                        BeginEdit();
                        WorkingZones.Remove(hit);
                        WorkingZones.AddRange(pieces);
                        CommitEdits();
                    }
                }
                else if (_editMode == ZoneEditMode.Merge && hit != null)
                {
                    if (_mergeZone == null)
                    {
                        _mergeZone = hit;
                        _selectedZoneId = hit.ZoneId;
                        ZoneSelected?.Invoke(hit);
                        RaiseStatus("Объединение: выберите вторую зону того же слоя");
                        InvalidateVisual();
                    }
                    else if (!ReferenceEquals(_mergeZone, hit))
                    {
                        var merged = ZoneEditor.Merge(_mergeZone, hit, _result.Outline,
                            clipToSlab: !_result.PatchPreviewOnly);
                        if (merged != null)
                        {
                            BeginEdit();
                            WorkingZones.Remove(_mergeZone);
                            WorkingZones.Remove(hit);
                            WorkingZones.Add(merged);
                            _mergeZone = null;
                            CommitEdits();
                        }
                        else RaiseStatus("Объединять можно зоны одного слоя и направления");
                    }
                }
                else if (_editMode == ZoneEditMode.PerpendicularToEdge && hit != null)
                {
                    var minX = hit.Contour.Min(p => p.X);
                    var maxX = hit.Contour.Max(p => p.X);
                    var minY = hit.Contour.Min(p => p.Y);
                    var maxY = hit.Contour.Max(p => p.Y);
                    var distanceToVertical = Math.Min(Math.Abs(m.X - minX), Math.Abs(m.X - maxX));
                    var distanceToHorizontal = Math.Min(Math.Abs(m.Y - minY), Math.Abs(m.Y - maxY));
                    var pieces = ZoneEditor.SplitPerpendicularToEdge(
                        hit, m.X, m.Y, distanceToVertical <= distanceToHorizontal, _result.Outline,
                        clipToSlab: !_result.PatchPreviewOnly);
                    if (pieces.Count == 2)
                    {
                        BeginEdit();
                        var index = WorkingZones.IndexOf(hit);
                        WorkingZones.RemoveAt(index);
                        WorkingZones.InsertRange(index, pieces);
                        CommitEdits(pieces[0]);
                        SetEditMode(ZoneEditMode.Select);
                        RaiseStatus("Зона разделена перпендикулярно грани; режим выбора восстановлен");
                    }
                    else
                    {
                        SetEditMode(ZoneEditMode.Select);
                        RaiseStatus("Разрез не выполнен; режим выбора восстановлен");
                    }
                }
                else if (_editMode == ZoneEditMode.CreateGap && hit != null)
                {
                    if (_gapMovingZone == null)
                    {
                        _gapMovingZone = hit;
                        _selectedZoneId = hit.ZoneId;
                        ZoneSelected?.Invoke(hit);
                        RaiseStatus("Зазор: выберите зону, от которой нужно отодвинуть");
                        InvalidateVisual();
                    }
                    else if (!ReferenceEquals(_gapMovingZone, hit))
                    {
                        var moving = _gapMovingZone;
                        _gapMovingZone = null;
                        BeginEdit();
                        if (ZoneEditor.CreateGap(moving, hit, _result.Outline,
                                clipToSlab: !_result.PatchPreviewOnly))
                            CommitEdits(moving);
                        else
                        {
                            _pendingUndo = null;
                            RaiseStatus("Не удалось создать зазор внутри контура плиты");
                        }
                    }
                }
                else if (_editMode == ZoneEditMode.Create || (_editMode == ZoneEditMode.Move && hit != null))
                {
                    var template = hit ?? WorkingZones.FirstOrDefault();
                    if (template == null && _result.PatchPreviewOnly)
                    {
                        var frame = _selectedPatchFrameId.HasValue
                            ? _drawPatchFrames.FirstOrDefault(item =>
                                item.SelectionId == _selectedPatchFrameId.Value)
                            : HitPatchFrame(m);
                        if (frame != null)
                        {
                            _selectedPatchFrameId = frame.SelectionId;
                            template = CreatePatchZoneTemplate(frame);
                        }
                    }

                    _editZone = template;
                    if (hit != null)
                    {
                        _selectedZoneId = hit.ZoneId;
                        ZoneSelected?.Invoke(hit);
                    }
                    if (_editZone != null)
                    {
                        _editStart = m;
                        BeginEdit();
                        CaptureMouse();
                    }
                    else RaiseStatus("Для создания зоны выберите пунктирную рамку пятна");
                }
                e.Handled = true;
                base.OnMouseLeftButtonDown(e);
                return;
            }
            if (hit != null)
            {
                _selectedZoneId = hit.ZoneId;
                ZoneSelected?.Invoke(hit);
                InvalidateVisual();
                e.Handled = true;
            }
            base.OnMouseLeftButtonDown(e);
        }

        private void SelectPatchFrameAt(Point3 point)
        {
            var frame = HitPatchFrame(point);
            _selectedPatchFrameId = frame?.SelectionId;
            PatchSelected?.Invoke(frame == null ? null : new ZonePatchFrameSelection
            {
                Patches = frame.Patches.ToList(),
                Zones = frame.Zones.ToList(),
                SlabOutline = _result?.Outline?.ToList() ?? new List<Point3>(),
                MinXM = frame.MinX,
                MaxXM = frame.MaxX,
                MinYM = frame.MinY,
                MaxYM = frame.MaxY
            });
            if (frame != null)
                RaiseStatus($"Выбрана рамка · пятен {frame.Patches.Count} · КЭ {frame.Patches.SelectMany(patch => patch.ElementIds).Distinct().Count()}");
        }

        private AdditionalZone CreatePatchZoneTemplate(CachedPatchFrame frame)
        {
            var layer = frame.Patches[0].Layer;
            var isBottom = layer is RebarLayer.As1 or RebarLayer.As2;
            var diameter = isBottom ? _settings.BgBottomDiameterMm : _settings.BgTopDiameterMm;
            var step = isBottom ? _settings.BgBottomStepMm : _settings.BgTopStepMm;
            if (diameter <= 0) diameter = 12;
            if (step != 100 && step != 200) step = 200;

            return new AdditionalZone
            {
                Layer = layer,
                Direction = RebarTables.DirectionForLayer(layer, _settings.ReverseZoneDirections),
                NodeIds = frame.Patches.SelectMany(patch => patch.ElementIds).Distinct().ToList(),
                LevelZM = _result?.ElevationZM ?? 0,
                DiameterMm = diameter,
                BarStepMm = step,
                FamilyKind = ZoneFamilyKind.Straight,
                FamilyFileName = _settings.GetFamilyName(ZoneFamilyKind.Straight),
                ConcreteClass = _settings.ConcreteClass,
                AsCoveredCm2PerM = BarCapacity.AsCm2PerM(diameter, step),
                Comment = "создано в предпросмотре пятен"
            };
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_result != null && _editStart != null)
            {
                var end = ScreenToModel(e.GetPosition(this));
                var start = _editStart;
                var resizedZone = _resizeEdge != ResizeEdge.None ? _editZone : null;
                var editedZone = resizedZone ?? _editZone;
                if (resizedZone != null)
                    ResizeDraggedEdge(resizedZone, end);
                else if (_editMode == ZoneEditMode.Move && _editZone != null)
                    ZoneEditor.Move(_editZone, end.X - start.X, end.Y - start.Y, _result.Outline,
                        clipToSlab: !_result.PatchPreviewOnly);
                else if (_editMode == ZoneEditMode.Create)
                {
                    var template = _editZone ?? WorkingZones.FirstOrDefault();
                    if (template != null)
                    {
                        var created = ZoneEditor.Create(template,
                            Math.Min(start.X, end.X), Math.Max(start.X, end.X),
                            Math.Min(start.Y, end.Y), Math.Max(start.Y, end.Y), _result.Outline,
                            clipToSlab: !_result.PatchPreviewOnly);
                        if (created != null)
                        {
                            if (_result.PatchPreviewOnly && template.NodeIds.Count > 0)
                            {
                                created.NodeIds = template.NodeIds.ToList();
                                created.ElementId = created.NodeIds[0];
                            }
                            WorkingZones.Add(created);
                            editedZone = created;
                        }
                    }
                }
                _editStart = null;
                _editZone = null;
                _resizeEdge = ResizeEdge.None;
                ReleaseMouseCapture();
                CommitEdits(editedZone);
                e.Handled = true;
            }
            base.OnMouseLeftButtonUp(e);
        }

        private AdditionalZone? HitZone(Point3 point)
        {
            var shapes = _result?.PatchPreviewOnly == true ? _drawPatchZones : _drawZones;
            for (var i = shapes.Count - 1; i >= 0; i--)
                if (PointInPoly(point, shapes[i].Contour)) return shapes[i].Zone;
            return null;
        }

        private (AdditionalZone? Zone, ResizeEdge Edge) HitResizeEdge(Point3 point)
        {
            var tolerance = 8.0 / Math.Max(1e-6, _fitScale * _zoom);
            var transformed = Tx(point);
            var shapes = _result?.PatchPreviewOnly == true ? _drawPatchZones : _drawZones;
            for (var i = shapes.Count - 1; i >= 0; i--)
            {
                var shape = shapes[i];
                var z = shape.Zone;
                if (z.Contour.Count < 3 || transformed.X < shape.MinX - tolerance ||
                    transformed.X > shape.MaxX + tolerance || transformed.Y < shape.MinY - tolerance ||
                    transformed.Y > shape.MaxY + tolerance) continue;
                var minX = z.Contour.Min(p => p.X);
                var maxX = z.Contour.Max(p => p.X);
                var minY = z.Contour.Min(p => p.Y);
                var maxY = z.Contour.Max(p => p.Y);
                var nearest = new[]
                {
                    (Distance: Math.Abs(point.X - minX), Edge: ResizeEdge.Left),
                    (Distance: Math.Abs(point.X - maxX), Edge: ResizeEdge.Right),
                    (Distance: Math.Abs(point.Y - minY), Edge: ResizeEdge.Bottom),
                    (Distance: Math.Abs(point.Y - maxY), Edge: ResizeEdge.Top)
                }.Where(item => item.Edge == ResizeEdge.Left || item.Edge == ResizeEdge.Right
                    ? point.Y >= minY - tolerance && point.Y <= maxY + tolerance
                    : point.X >= minX - tolerance && point.X <= maxX + tolerance)
                 .OrderBy(item => item.Distance).FirstOrDefault();
                if (nearest.Distance <= tolerance && nearest.Edge != ResizeEdge.None)
                    return (z, nearest.Edge);
            }
            return (null, ResizeEdge.None);
        }

        private bool ResizeDraggedEdge(AdditionalZone zone, Point3 point)
        {
            if (_result == null) return false;
            var (minX, maxX, minY, maxY) = _resizeBounds;
            switch (_resizeEdge)
            {
                case ResizeEdge.Left: minX = point.X; break;
                case ResizeEdge.Right: maxX = point.X; break;
                case ResizeEdge.Bottom: minY = point.Y; break;
                case ResizeEdge.Top: maxY = point.Y; break;
                default: return false;
            }
            if (maxX - minX <= 0.05 || maxY - minY <= 0.05) return false;
            return ZoneEditor.Resize(zone, minX, maxX, minY, maxY, _result.Outline,
                clipToSlab: !_result.PatchPreviewOnly);
        }

        private void CommitEdits(AdditionalZone? keepSelected = null)
        {
            if (_result == null) return;
            LastConflictResolution = null;
            var zones = WorkingZones;
            if (_pendingUndo != null)
            {
                var before = JsonConvert.DeserializeObject<List<AdditionalZone>>(_pendingUndo) ?? new List<AdditionalZone>();
                var unchanged = new HashSet<string>(before.Select(JsonConvert.SerializeObject));
                for (var i = zones.Count - 1; !_result.PatchPreviewOnly && i >= 0; i--)
                {
                    var zone = zones[i];
                    if (unchanged.Contains(JsonConvert.SerializeObject(zone)) ||
                        !ZoneEditor.IntersectsOpening(zone, _result.Openings)) continue;
                    var parts = ZoneEditor.SplitAtOpenings(
                        zone, _result.Openings, _settings, _result.Plates);
                    zones.RemoveAt(i);
                    zones.InsertRange(i, parts);
                    if (ReferenceEquals(keepSelected, zone)) keepSelected = parts.FirstOrDefault();
                }
                if (_pendingUndo != JsonConvert.SerializeObject(zones))
                {
                    _undo.Add(_pendingUndo);
                    if (_undo.Count > MaxUndoActions) _undo.RemoveAt(0);
                }
                _pendingUndo = null;
            }
            ZoneEditor.RemoveZonesWithoutRequiredElements(
                zones, _result.Plates, _settings, _result.Outline, _result.Openings);
            if (keepSelected != null && !zones.Contains(keepSelected)) keepSelected = null;
            AssignZoneIds(zones);
            if (_result.PatchPreviewOnly) RefreshPatchFrameZones();
            if (_result.PatchPreviewOnly) _result.Zones = zones.ToList();
            RefreshDiagnostics();
            RebuildZoneGeometryCache();
            _selectedZoneId = keepSelected?.ZoneId;
            if (keepSelected != null) ZoneSelected?.Invoke(keepSelected);
            ZonesEdited?.Invoke();
            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (_result != null && _zoom <= 1.01)
                FitToView();
            InvalidateVisual();
        }

        private void RebuildGeometryCache()
        {
            _plateShapes.Clear();
            _drawPatchFills.Clear();
            _drawPatchFrames.Clear();
            _drawPatchZones.Clear();
            var savedPatchZones = _result?.PatchPreviewOnly == true
                ? _result.Zones.ToList()
                : new List<AdditionalZone>();
            _patchCandidateZones.Clear();
            if (savedPatchZones.Count > 0) _patchCandidateZones.AddRange(savedPatchZones);
            _patchElementValues.Clear();
            if (_result != null)
            {
                foreach (var plate in _result.Plates)
                    _plateShapes.Add(BuildShape(plate.Contour));
                if (_result.PatchPreviewOnly)
                    RebuildPatchGeometryCache();
                else
                {
                    ZoneEditor.RemoveZonesWithoutRequiredElements(
                        _result.Zones, _result.Plates, _settings, _result.Outline, _result.Openings);
                    AssignZoneIds(_result.Zones);
                }
            }
            RebuildZoneGeometryCache();
            RefreshDiagnostics();
        }

        private void RebuildPatchGeometryCache()
        {
            if (_result == null) return;
            LayoutProgress?.Invoke("Мозаика и расчётные пятна", 0, 0);
            // Отступ проверяется независимо от включения подрезки/загибов и связи с Revit.
            var boundaryConstraints = new ZoneBoundaryConstraints(_result.Outline, _result.Openings, _settings.EdgeOffsetMm);
            foreach (var zone in _patchCandidateZones) zone.BoundaryConstraints = boundaryConstraints;
            var useSavedZones = _patchCandidateZones.Count > 0;
            var coverageBaseline = useSavedZones ? boundaryConstraints.Snapshot(_patchCandidateZones) : null;
            _drawPatchFrames.Clear();
            var activeElementIds = new HashSet<int>(_result.Patches.SelectMany(patch => patch.ElementIds));
            var contoursByLevel = new Dictionary<int, List<IList<Point3>>>();
            foreach (var plate in _result.Plates)
            {
                if (!activeElementIds.Contains(plate.Id) || !plate.Rebar.Ok ||
                    plate.Contour == null || plate.Contour.Count < 3)
                    continue;

                var additional = IsoAdditionalAs(plate.Rebar, _settings);
                if (additional <= MosaicBuilder.PositiveResidualToleranceCm2PerM) continue;
                var level = Math.Max(1, IsoColorScale.LevelForValue(
                    Math.Max(0.011, additional), _settings.VisualizationScale));
                if (!contoursByLevel.TryGetValue(level, out var contours))
                    contoursByLevel[level] = contours = new List<IList<Point3>>();
                contours.Add(plate.Contour);
            }

            foreach (var group in contoursByLevel)
                _drawPatchFills.Add((group.Key, BuildContours(group.Value)));

            var frames = _result.Patches.Select(patch => new CachedPatchFrame
            {
                MinX = patch.MinXM,
                MaxX = patch.MaxXM,
                MinY = patch.MinYM,
                MaxY = patch.MaxYM
            }).ToList();
            for (var i = 0; i < frames.Count; i++)
                frames[i].Patches.Add(_result.Patches[i]);

            var merged = true;
            while (merged)
            {
                merged = false;
                for (var i = 0; i < frames.Count && !merged; i++)
                for (var j = i + 1; j < frames.Count; j++)
                {
                    if (!RectanglesOverlap(frames[i], frames[j])) continue;
                    frames[i].Patches.AddRange(frames[j].Patches);
                    frames[i].MinX = Math.Min(frames[i].MinX, frames[j].MinX);
                    frames[i].MaxX = Math.Max(frames[i].MaxX, frames[j].MaxX);
                    frames[i].MinY = Math.Min(frames[i].MinY, frames[j].MinY);
                    frames[i].MaxY = Math.Max(frames[i].MaxY, frames[j].MaxY);
                    frames.RemoveAt(j);
                    merged = true;
                    break;
                }
            }

            var platesById = _result.Plates.GroupBy(plate => plate.Id)
                .ToDictionary(group => group.Key, group => group.First());
            var patchSourceBounds = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
            var patchOuterBounds = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
            if (useSavedZones)
            {
                foreach (var zone in _patchCandidateZones.Where(zone => zone.Contour != null && zone.Contour.Count >= 4))
                {
                    var zoneBounds = new ZonePatchFrameBounds(
                        zone.Contour.Min(point => point.X), zone.Contour.Max(point => point.X),
                        zone.Contour.Min(point => point.Y), zone.Contour.Max(point => point.Y));
                    patchSourceBounds[zone] = zoneBounds;

                    var nodeIds = new HashSet<int>(zone.NodeIds ?? new List<int>());
                    var relatedPatches = _result.Patches.Where(patch => patch.Layer == zone.Layer &&
                        patch.ElementIds.Any(nodeIds.Contains)).ToList();
                    patchOuterBounds[zone] = relatedPatches.Count == 0
                        ? zoneBounds
                        : new ZonePatchFrameBounds(
                            relatedPatches.Min(patch => patch.MinXM), relatedPatches.Max(patch => patch.MaxXM),
                            relatedPatches.Min(patch => patch.MinYM), relatedPatches.Max(patch => patch.MaxYM));
                }

                ZonePatchNeighborLayout.ResolveContainedOverlaps(
                    _patchCandidateZones, patchSourceBounds, patchOuterBounds,
                    _result.Plates, _settings, _result.Outline, _result.Openings);
            }
            foreach (var frame in frames)
            {
                LayoutProgress?.Invoke("Подбор зон по пятнам", frames.IndexOf(frame) + 1, frames.Count);
                var frameElements = new Dictionary<(RebarLayer Layer, int ElementId), ZonePatchFrameElement>();
                foreach (var patch in frame.Patches)
                foreach (var id in patch.ElementIds)
                {
                    if (!platesById.TryGetValue(id, out var plate) ||
                        !plate.Rebar.Ok || plate.Contour == null || plate.Contour.Count < 3)
                        continue;
                    var backgroundAs = patch.Layer switch
                    {
                        RebarLayer.As1 => _settings.AsMainAs1,
                        RebarLayer.As2 => _settings.AsMainAs2,
                        RebarLayer.As3 => _settings.AsMainAs3,
                        RebarLayer.As4 => _settings.AsMainAs4,
                        _ => 0
                    };
                    var additionalAs = plate.Rebar.Get(patch.Layer) - backgroundAs;
                    var key = (patch.Layer, id);
                    if (additionalAs <= MosaicBuilder.PositiveResidualToleranceCm2PerM ||
                        frameElements.ContainsKey(key))
                        continue;
                    frameElements.Add(key, new ZonePatchFrameElement
                    {
                        ElementId = id,
                        Layer = patch.Layer,
                        AsAdditionalCm2PerM = additionalAs,
                        Contour = plate.Contour
                    });
                }
                var partitions = ZonePatchFramePartitioner.Split(
                    frame.Patches, frame.MinX, frame.MaxX, frame.MinY, frame.MaxY,
                    _settings.EffectiveMinZoneWidthM, frameElements.Values.ToList());
                foreach (var partition in partitions)
                {
                    var splitFrame = new CachedPatchFrame
                    {
                        SelectionId = _drawPatchFrames.Count + 1,
                        MinX = partition.MinX,
                        MaxX = partition.MaxX,
                        MinY = partition.MinY,
                        MaxY = partition.MaxY,
                        Shape = BuildRectangles(new[]
                        {
                            (partition.MinX, partition.MaxX, partition.MinY, partition.MaxY)
                        })
                    };
                    splitFrame.Patches.AddRange(frame.Patches);
                    var selection = new ZonePatchFrameSelection
                    {
                        BoundaryConstraints = boundaryConstraints,
                        Patches = splitFrame.Patches,
                        Elements = frameElements.Values.ToList(),
                        SlabOutline = _result.Outline?.ToList() ?? new List<Point3>(),
                        MinXM = partition.MinX,
                        MaxXM = partition.MaxX,
                        MinYM = partition.MinY,
                        MaxYM = partition.MaxY
                    };
                    if (!useSavedZones)
                    {
                        splitFrame.Zones.AddRange(ZonePatchZoneBuilder.Build(
                            selection, _result.ElevationZM, _settings, patchSourceBounds, patchOuterBounds));
                        _patchCandidateZones.AddRange(splitFrame.Zones);
                    }
                    _drawPatchFrames.Add(splitFrame);
                }
            }
            coverageBaseline ??= boundaryConstraints.Snapshot(_patchCandidateZones);
            if (!useSavedZones)
            {
                LayoutProgress?.Invoke("Увязка соседних рядов", 0, 0);
                ZonePatchZoneBuilder.MergeAdjacentCompatibleZones(
                    _patchCandidateZones, patchSourceBounds, patchOuterBounds);
                ZonePatchZoneBuilder.MergeShiftableAdjacentZonesAlongBars(
                    _patchCandidateZones, patchSourceBounds, _settings,
                    patchOuterBounds, _result.Plates, _result.Outline, _result.Openings);
                ZonePatchZoneBuilder.MergeAdjacentCompatibleZones(
                    _patchCandidateZones, patchSourceBounds, patchOuterBounds);
                var neighborLayout = ZonePatchNeighborLayout.Apply(
                    _patchCandidateZones, patchSourceBounds, patchOuterBounds,
                    _result.Plates, _settings, _result.Outline, _result.Openings);
                if (ZonePatchZoneBuilder.MergeAdjacentCompatibleZones(
                        _patchCandidateZones, patchSourceBounds, patchOuterBounds) > 0)
                {
                    ZonePatchZoneBuilder.MergeShiftableAdjacentZonesAlongBars(
                        _patchCandidateZones, patchSourceBounds, _settings,
                        patchOuterBounds, _result.Plates, _result.Outline, _result.Openings);
                    neighborLayout = ZonePatchNeighborLayout.Apply(
                        _patchCandidateZones, patchSourceBounds, patchOuterBounds,
                        _result.Plates, _settings, _result.Outline, _result.Openings);
                }
                ZoneEditor.EnforceRequiredGaps(
                    _patchCandidateZones, _result.Plates, _settings,
                    _result.Outline, _result.Openings);
                neighborLayout = ZonePatchNeighborLayout.Apply(
                    _patchCandidateZones, patchSourceBounds, patchOuterBounds,
                    _result.Plates, _settings, _result.Outline, _result.Openings);
                ZoneEditor.EnforceRequiredGaps(
                    _patchCandidateZones, _result.Plates, _settings,
                    _result.Outline, _result.Openings);
                if (neighborLayout.UnresolvedWidths > 0)
                {
                    foreach (var zone in _patchCandidateZones)
                    {
                        zone.StatusColor = "warn";
                        zone.Comment = string.IsNullOrWhiteSpace(zone.Comment)
                            ? "ширину не удалось нормировать к шагу зоны"
                            : zone.Comment + "; ширину не удалось нормировать к шагу зоны";
                    }
                }
                if (_settings.ApplySlabBoundaryAndOpeningRules)
                {
                    for (var i = _patchCandidateZones.Count - 1; i >= 0; i--)
                    {
                        var zone = _patchCandidateZones[i];
                        var parts = ZoneEditor.SplitAtOpenings(
                            zone, _result.Openings, _settings, _result.Plates);
                        if (parts.Count == 1 && ReferenceEquals(parts[0], zone)) continue;
                        if (parts.Count == 0)
                        {
                            _patchCandidateZones.RemoveAt(i);
                            continue;
                        }
                        _patchCandidateZones.RemoveAt(i);
                        _patchCandidateZones.InsertRange(i, parts);
                    }
                    ZoneLayoutEngine.ApplyBoundaryFamilies(
                        _patchCandidateZones, _settings, _result.Outline);
                    ZoneEditor.EnforceMaximumDetailLength(
                        _patchCandidateZones, _result.Plates,
                        _result.Outline ?? new List<Point3>(), 11700,
                        allowBoundaryCornerOverrun: true);
                    if (_result.Outline != null && _result.Outline.Count >= 3)
                        ZoneEditor.ApplyOuterBoundaryCuts(_patchCandidateZones,
                            _result.Plates, _result.Outline, _settings.EdgeOffsetMm);
                    ZoneLayoutEngine.ApplyBoundaryFamilies(
                        _patchCandidateZones, _settings, _result.Outline);
                }
            }
            // Привязка к осям Revit — и после новой раскладки, и для сохранённых зон
            // (связь могла появиться позже); для уже привязанных зон сдвиг нулевой.
            var revitAxes = RevitGridAlignment.AxesInLira(_result, _settings);
            LastAlignment = revitAxes.Count > 0
                ? RevitGridAlignment.Apply(_patchCandidateZones, revitAxes,
                    _result.Outline, _result.Openings, _settings.EdgeOffsetMm)
                : null;
            ZoneEditor.RemoveZonesWithoutRequiredElements(
                _patchCandidateZones, _result.Plates, _settings, _result.Outline, _result.Openings);
            boundaryConstraints.PreserveCoverage(_patchCandidateZones, coverageBaseline,
                _result.Plates, _settings, _result.Outline, _result.Openings);
            if (_coverageReference != null)
                boundaryConstraints.PreserveCoverage(_patchCandidateZones, _coverageReference,
                    _result.Plates, _settings, _result.Outline, _result.Openings);
            _coverageReference = null;
            if (!useSavedZones)
            {
                LayoutProgress?.Invoke("Границы и отступы", 0, 0);
                ZoneBoundaryLayoutSolver.Repair(_patchCandidateZones, _result.Plates, _settings,
                    _result.Outline, _result.Openings, boundaryConstraints, revitAxes);
                LastConflictResolution = ZoneConflictGroupLayout.Resolve(_patchCandidateZones, _result.Plates,
                    _settings, _result.Outline, _result.Openings, boundaryConstraints, revitAxes, LayoutProgress);
                ZoneLayoutCompactness.Refine(_patchCandidateZones, _result.Plates, _settings,
                    _result.Outline, _result.Openings, boundaryConstraints, LayoutProgress);
            }
            LayoutProgress?.Invoke("Подготовка изображения и диагностики", 0, 0);
            AssignZoneIds(_patchCandidateZones);
            RefreshPatchFrameZones();
            _result.Zones = _patchCandidateZones.ToList();
            if (_selectedPatchFrameId.HasValue &&
                !_drawPatchFrames.Any(frame => frame.SelectionId == _selectedPatchFrameId.Value))
                _selectedPatchFrameId = null;

            for (var i = 0; i < _result.Plates.Count; i++)
            {
                var plate = _result.Plates[i];
                if (!plate.Rebar.Ok) continue;
                var additional = IsoAdditionalAs(plate.Rebar, _settings);
                if (additional <= 0.01) continue;
                _patchElementValues.Add(new PatchElementValues
                {
                    PlateIndex = i,
                    Position = plate.Centroid,
                    Text = additional.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)
                });
            }
        }

        private CachedShape BuildContours(IEnumerable<IList<Point3>> contours)
        {
            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            using (var ctx = geometry.Open())
            {
                foreach (var contour in contours)
                {
                    if (contour == null || contour.Count < 3) continue;
                    for (var i = 0; i < contour.Count; i++)
                    {
                        var point = Tx(contour[i]);
                        minX = Math.Min(minX, point.X);
                        maxX = Math.Max(maxX, point.X);
                        minY = Math.Min(minY, point.Y);
                        maxY = Math.Max(maxY, point.Y);
                        if (i == 0) ctx.BeginFigure(point, true, true);
                        else ctx.LineTo(point, true, false);
                    }
                }
            }
            geometry.Freeze();
            return new CachedShape
            {
                Geometry = geometry,
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY
            };
        }

        private static bool RectanglesOverlap(CachedPatchFrame a, CachedPatchFrame b) =>
            a.MinX < b.MaxX && a.MaxX > b.MinX &&
            a.MinY < b.MaxY && a.MaxY > b.MinY;

        private CachedPatchFrame? HitPatchFrame(Point3 point)
        {
            for (var i = _drawPatchFrames.Count - 1; i >= 0; i--)
            {
                var frame = _drawPatchFrames[i];
                if (point.X >= frame.MinX && point.X <= frame.MaxX &&
                    point.Y >= frame.MinY && point.Y <= frame.MaxY)
                    return frame;
            }
            return null;
        }

        private void DrawPatchElementValues(
            DrawingContext dc, double scale, double minX, double maxX, double minY, double maxY)
        {
            if (_zoom < 1.6 || _patchElementValues.Count == 0) return;

            var typeface = new Typeface("Segoe UI");
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var fontModel = Math.Max(0.055, 8.0 / scale);
            const int maxLabels = 4000;
            var drawn = 0;

            foreach (var label in _patchElementValues)
            {
                if (drawn >= maxLabels) break;
                var shape = _plateShapes[label.PlateIndex];
                if (!shape.Intersects(minX, maxX, minY, maxY)) continue;

                var center = Tx(label.Position.X, label.Position.Y);
                var text = new FormattedText(label.Text, culture, FlowDirection.LeftToRight,
                    typeface, fontModel, PatchValueTextBrush, pixelsPerDip);
                DrawUprightText(dc, text, center);
                drawn++;
            }
        }

        private void DrawPatchZoneOutlines(
            DrawingContext dc, double scale, double minX, double maxX, double minY, double maxY,
            IReadOnlyDictionary<int, Brush> diagnosticBrushes)
        {
            foreach (var shape in _drawPatchZones)
            {
                if (!shape.Intersects(minX, maxX, minY, maxY)) continue;
                diagnosticBrushes.TryGetValue(shape.Zone.ZoneId, out var attention);
                attention ??= ZoneAttentionBrush(shape.Zone);
                if (shape.Zone.ZoneId == _selectedZoneId)
                {
                    var selectionPen = new Pen(Brushes.White, Math.Max(1e-4, 5.0 / scale))
                    {
                        DashStyle = DashStyles.Dash
                    };
                    selectionPen.Freeze();
                    dc.DrawGeometry(null, selectionPen, shape.Geometry);
                }
                var outline = new Pen(attention ?? DiameterStroke(shape.Zone.DiameterMm),
                    Math.Max(1e-4, attention != null ? 2.8 / scale : 2.0 / scale));
                outline.Freeze();
                dc.DrawGeometry(DiameterFill(shape.Zone.DiameterMm, 32), outline, shape.Geometry);
            }
        }

        private Dictionary<int, Brush> GetDiagnosticZoneBrushes()
        {
            var diagnostics = _result?.Diagnostics ?? new ZoneDiagnostics();
            var showZoneErrors = _diagnosticFilter == DiagnosticFilter.All ||
                                 _diagnosticFilter == DiagnosticFilter.ZoneErrors;
            var showConflicts = _diagnosticFilter == DiagnosticFilter.All ||
                                _diagnosticFilter == DiagnosticFilter.Conflicts;
            var brushes = new Dictionary<int, Brush>();
            var priorities = new Dictionary<int, int>();
            foreach (var issue in diagnostics.Issues)
            {
                var isConflict = issue.Kind == ZoneIssueKind.PlacementConflict;
                var isStep = issue.Kind == ZoneIssueKind.InterZoneStep;
                if ((isConflict && !showConflicts) ||
                    (!isConflict && !isStep && !showZoneErrors) ||
                    (isStep && !showZoneErrors && !showConflicts))
                    continue;
                if (isStep && issue.RelatedZoneId > 0 && IsSamePatchFrame(
                        issue.ZoneId, issue.RelatedZoneId))
                    continue;

                var brush = DiagnosticBrush(issue.Kind);
                if (brush == null) continue;
                var priority = DiagnosticPriority(issue.Kind);
                if (priorities.TryGetValue(issue.ZoneId, out var current) && current <= priority)
                    continue;
                priorities[issue.ZoneId] = priority;
                brushes[issue.ZoneId] = brush;
            }
            return brushes;
        }

        private bool IsSamePatchFrame(int firstZoneId, int secondZoneId) =>
            _result?.PatchPreviewOnly == true && firstZoneId != secondZoneId &&
            _drawPatchFrames.Any(frame =>
                frame.Zones.Any(zone => zone.ZoneId == firstZoneId) &&
                frame.Zones.Any(zone => zone.ZoneId == secondZoneId));

        private void RefreshDiagnostics()
        {
            if (_result == null) return;
            var diagnostics = ZoneLayoutDiagnostics.Evaluate(
                _result.Plates, WorkingZones, _settings, 0, true,
                _result.Outline, _result.Openings);
            diagnostics.Issues.AddRange(new ZoneBoundaryConstraints(_result.Outline, _result.Openings,
                _settings.EdgeOffsetMm).CheckZones(WorkingZones));
            if (_result.PatchPreviewOnly)
                diagnostics.Issues.RemoveAll(issue => issue.Kind == ZoneIssueKind.InterZoneStep &&
                    issue.RelatedZoneId > 0 && IsSamePatchFrame(issue.ZoneId, issue.RelatedZoneId));
            var revitAxes = RevitGridAlignment.AxesInLira(_result, _settings);
            if (revitAxes.Count > 0)
                diagnostics.Issues.AddRange(RevitGridAlignment.Check(
                    WorkingZones, revitAxes, _result.Outline, _result.Openings, _settings.EdgeOffsetMm));
            _result.Diagnostics = diagnostics;
            if (LastConflictResolution != null)
            {
                LastConflictResolution.PairsAfter = diagnostics.Issues
                    .Where(i => i.Kind == ZoneIssueKind.PlacementConflict || i.Kind == ZoneIssueKind.InterZoneStep)
                    .Select(i => (Math.Min(i.ZoneId,i.RelatedZoneId),Math.Max(i.ZoneId,i.RelatedZoneId))).Distinct().Count();
                LastConflictResolution.BoundaryAfter = diagnostics.Issues.Count(i => i.Kind == ZoneIssueKind.BoundaryClearance);
                LastConflictResolution.InvalidZoneIds = WorkingZones.Where(z => !z.IsValid || RebarTables.ExceedsMaxBarLength(z)).Select(z=>z.ZoneId).ToList();
            }
        }

        private static int DiagnosticPriority(ZoneIssueKind kind) => kind switch
        {
            ZoneIssueKind.BoundaryClearance => 3,
            ZoneIssueKind.InvalidZone => 0,
            ZoneIssueKind.EmptyZone => 1,
            ZoneIssueKind.OverlongBar => 2,
            ZoneIssueKind.OpeningIntersection => 3,
            ZoneIssueKind.OutsideSlab => 4,
            ZoneIssueKind.AxisTie => 5,
            ZoneIssueKind.InterZoneStep => 6,
            ZoneIssueKind.PlacementConflict => 7,
            _ => int.MaxValue
        };

        private static Brush? DiagnosticBrush(ZoneIssueKind kind) => kind switch
        {
            ZoneIssueKind.BoundaryClearance => Brushes.Red,
            ZoneIssueKind.InvalidZone => Brushes.Crimson,
            ZoneIssueKind.EmptyZone => Brushes.MediumVioletRed,
            ZoneIssueKind.OverlongBar => Brushes.MediumPurple,
            ZoneIssueKind.InterZoneStep => Brushes.DarkCyan,
            ZoneIssueKind.OpeningIntersection => Brushes.OrangeRed,
            ZoneIssueKind.OutsideSlab => Brushes.RoyalBlue,
            ZoneIssueKind.AxisTie => Brushes.Red,
            ZoneIssueKind.PlacementConflict => Brushes.DarkOrange,
            _ => null
        };

        private static Brush? ZoneAttentionBrush(AdditionalZone zone)
        {
            if (!zone.IsValid || zone.StatusColor == "error") return Brushes.Red;
            if (zone.StatusColor == "warn") return Brushes.Goldenrod;
            return null;
        }

        private void DrawPatchZoneLabels(
            DrawingContext dc, double scale, double minX, double maxX, double minY, double maxY)
        {
            if (_zoom < 1.6) return;
            var typeface = new Typeface("Segoe UI");
            var occupied = new List<Rect>();

            foreach (var shape in _drawPatchZones.OrderByDescending(s => s.Zone.ZoneId == _selectedZoneId))
            {
                var zone = shape.Zone;
                if (zone.DiameterMm <= 0 || !shape.Intersects(minX, maxX, minY, maxY)) continue;

                var center = new Point((shape.MinX + shape.MaxX) * 0.5,
                    (shape.MinY + shape.MaxY) * 0.5);
                center.Y += shape.PatchLabelOffsetYpx / scale;
                var (line1, line2) = BuildZoneLabelLines(zone);
                var fontModel = Math.Max(0.065, 9.0 / scale);
                var first = new FormattedText(line1,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, fontModel, Brushes.Black, 1.0);
                FormattedText? second = string.IsNullOrEmpty(line2) ? null : new FormattedText(line2,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, fontModel * 0.92, Brushes.Black, 1.0);
                var textWidth = Math.Max(first.Width, second?.Width ?? 0);
                var textHeight = first.Height + (second?.Height ?? 0) + fontModel * 0.12;
                var padding = fontModel * 0.28;
                var background = new Rect(center.X - textWidth / 2 - padding,
                    center.Y - textHeight / 2 - padding,
                    textWidth + padding * 2, textHeight + padding * 2);
                var reserved = background;
                reserved.Inflate(3 / scale, 3 / scale);
                if (occupied.Any(r => r.IntersectsWith(reserved))) continue;
                occupied.Add(reserved);

                dc.PushTransform(new ScaleTransform(1, -1, center.X, center.Y));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), null, background);
                var textY = center.Y - textHeight / 2;
                dc.DrawText(first, new Point(center.X - first.Width / 2, textY));
                if (second != null)
                    dc.DrawText(second, new Point(center.X - second.Width / 2,
                        textY + first.Height + fontModel * 0.12));
                dc.Pop();
            }
        }

        private CachedShape BuildRectangles(IEnumerable<(double MinX, double MaxX, double MinY, double MaxY)> rectangles)
        {
            var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            void AddBounds(Point point)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
            using (var ctx = geometry.Open())
            {
                foreach (var rect in rectangles)
                {
                    var a = Tx(rect.MinX, rect.MinY);
                    var b = Tx(rect.MaxX, rect.MinY);
                    var c = Tx(rect.MaxX, rect.MaxY);
                    var d = Tx(rect.MinX, rect.MaxY);
                    AddBounds(a);
                    AddBounds(b);
                    AddBounds(c);
                    AddBounds(d);
                    ctx.BeginFigure(a, true, true);
                    ctx.LineTo(b, true, false);
                    ctx.LineTo(c, true, false);
                    ctx.LineTo(d, true, false);
                }
            }
            geometry.Freeze();
            return new CachedShape
            {
                Geometry = geometry,
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY
            };
        }

        private void RebuildZoneGeometryCache()
        {
            _drawZones.Clear();
            if (_result == null) return;
            if (_result.PatchPreviewOnly)
            {
                RebuildPatchZoneGeometryCache();
                return;
            }
            foreach (var z in _result.Zones)
            {
                if (z.Contour == null || z.Contour.Count < 3) continue;
                var arr = new Point3[z.Contour.Count];
                for (int i = 0; i < z.Contour.Count; i++) arr[i] = z.Contour[i];
                var cached = BuildShape(arr);
                _drawZones.Add(new CachedZoneShape
                {
                    Zone = z,
                    Contour = arr,
                    Geometry = cached.Geometry,
                    MinX = cached.MinX,
                    MaxX = cached.MaxX,
                    MinY = cached.MinY,
                    MaxY = cached.MaxY
                });
            }
        }

        private void RebuildPatchZoneGeometryCache()
        {
            _drawPatchZones.Clear();
            var zones = _patchCandidateZones;
            var labelOffsets = new Dictionary<int, double>();
            foreach (var group in zones.GroupBy(zone =>
                         $"{zone.Layer}:{string.Join(",", (zone.NodeIds ?? new List<int>()).OrderBy(id => id))}"))
            {
                var ordered = group.OrderBy(zone => zone.ZoneId).ToList();
                for (var i = 0; i < ordered.Count; i++)
                    labelOffsets[ordered[i].ZoneId] = (i - (ordered.Count - 1) / 2.0) * 18.0;
            }

            foreach (var zone in zones)
            {
                if (zone.Contour == null || zone.Contour.Count < 3) continue;
                var contour = zone.Contour.ToArray();
                var shape = BuildShape(contour);
                _drawPatchZones.Add(new CachedZoneShape
                {
                    Zone = zone,
                    Contour = contour,
                    Geometry = shape.Geometry,
                    MinX = shape.MinX,
                    MaxX = shape.MaxX,
                    MinY = shape.MinY,
                    MaxY = shape.MaxY,
                    PatchLabelOffsetYpx = labelOffsets.TryGetValue(zone.ZoneId, out var offset) ? offset : 0
                });
            }
        }

        private static void AssignZoneIds(IList<AdditionalZone> zones)
        {
            for (var i = 0; i < zones.Count; i++) zones[i].ZoneId = i + 1;
        }

        private void RefreshPatchFrameZones()
        {
            if (_result == null) return;
            foreach (var frame in _drawPatchFrames)
            {
                var elementIds = new HashSet<int>(frame.Patches.SelectMany(patch => patch.ElementIds));
                frame.Zones.Clear();
                frame.Zones.AddRange(_patchCandidateZones.Where(zone =>
                    frame.Patches.Any(patch => patch.Layer == zone.Layer) &&
                    zone.NodeIds != null && zone.NodeIds.Any(elementIds.Contains)));
            }
        }

        private CachedShape BuildShape(IList<Point3> contour)
        {
            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            using (var ctx = geometry.Open())
            {
                for (var i = 0; i < contour.Count; i++)
                {
                    var p = Tx(contour[i]);
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                    if (i == 0) ctx.BeginFigure(p, true, true);
                    else ctx.LineTo(p, true, false);
                }
            }
            geometry.Freeze();
            return new CachedShape
            {
                Geometry = geometry,
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY
            };
        }

        private void ComputeModelExtents()
        {
            _modelMinX = _modelMinY = double.MaxValue;
            _modelMaxX = _modelMaxY = double.MinValue;
            _pivotX = _pivotY = 0;
            if (_result == null) return;

            // pivot = центр исходной геометрии (до поворота), как при вставке в Revit
            (_pivotX, _pivotY) = LiraRevitTransform.ComputePivot(_result);

            void Acc(Point3 p)
            {
                var t = Tx(p);
                if (t.X < _modelMinX) _modelMinX = t.X;
                if (t.Y < _modelMinY) _modelMinY = t.Y;
                if (t.X > _modelMaxX) _modelMaxX = t.X;
                if (t.Y > _modelMaxY) _modelMaxY = t.Y;
            }

            if (_result.Outline != null)
                foreach (var p in _result.Outline) Acc(p);

            foreach (var plate in _result.Plates)
            foreach (var p in plate.Contour)
                Acc(p);

            if (_result.PatchPreviewOnly)
            foreach (var patch in _result.Patches)
            {
                Acc(new Point3(patch.MinXM, patch.MinYM, 0));
                Acc(new Point3(patch.MaxXM, patch.MinYM, 0));
                Acc(new Point3(patch.MaxXM, patch.MaxYM, 0));
                Acc(new Point3(patch.MinXM, patch.MaxYM, 0));
            }

            if (_result.PatchPreviewOnly)
            foreach (var zone in _drawPatchZones.Select(shape => shape.Zone))
                foreach (var point in zone.Contour)
                    Acc(point);

            if (_showAxes && _result.Axes != null && _result.Axes.Count > 0 &&
                TryGetSlabBounds(out var bx0, out var bx1, out var by0, out var by1))
            {
                double span = Math.Max(bx1 - bx0, by1 - by0);
                double gap = Math.Max(0.8, span * 0.04 + 0.5);
                double bubbleY = by1 + gap;
                double bubbleX = bx0 - gap;
                foreach (var ax in _result.Axes)
                {
                    bool vertical = ax.Vertical;
                    double pos = ax.Position;
                    if (ax.IsSegment)
                    {
                        double dx = Math.Abs(ax.X2 - ax.X1);
                        double dy = Math.Abs(ax.Y2 - ax.Y1);
                        vertical = dx <= dy;
                        pos = vertical ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
                    }
                    if (vertical)
                        Acc(new Point3(pos, bubbleY, 0));
                    else
                        Acc(new Point3(bubbleX, pos, 0));
                }
            }

            if (_modelMinX > _modelMaxX)
            {
                _modelMinX = 0; _modelMinY = 0; _modelMaxX = 10; _modelMaxY = 10;
            }
        }

        private Point ScreenToTransformed(Point screen)
        {
            double s = _fitScale * _zoom;
            if (s < 1e-12) return new Point();
            double ox = 12 + _panX + (ActualWidth - 24 - (_modelMaxX - _modelMinX) * s) * 0.5;
            double oy = 12 + _panY + (ActualHeight - 24 - (_modelMaxY - _modelMinY) * s) * 0.5 + (_modelMaxY - _modelMinY) * s;
            double mx = (screen.X - ox) / s + _modelMinX;
            double my = _modelMinY - (screen.Y - oy) / s;
            return new Point(mx, my);
        }

        private Point3 ScreenToModel(Point screen)
        {
            var t = ScreenToTransformed(screen);
            return UnTx(t.X, t.Y);
        }

        private static Brush DiameterFill(int diameterMm, byte alpha)
        {
            var key = (diameterMm << 8) | alpha;
            if (DiameterFillCache.TryGetValue(key, out var cached)) return cached;
            var c = DiameterColor(diameterMm);
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            DiameterFillCache[key] = b;
            return b;
        }

        private static Brush DiameterStroke(int diameterMm)
        {
            if (DiameterStrokeCache.TryGetValue(diameterMm, out var cached)) return cached;
            var c = DiameterColor(diameterMm);
            var b = new SolidColorBrush(Color.FromArgb(230, c.R, c.G, c.B));
            b.Freeze();
            DiameterStrokeCache[diameterMm] = b;
            return b;
        }

        private static Color DiameterColor(int d) => d switch
        {
            8 => Color.FromRgb(148, 163, 184),
            10 => Color.FromRgb(56, 189, 248),
            12 => Color.FromRgb(34, 197, 94),
            16 => Color.FromRgb(234, 179, 8),
            20 => Color.FromRgb(249, 115, 22),
            22 => Color.FromRgb(239, 68, 68),
            25 => Color.FromRgb(168, 85, 247),
            28 => Color.FromRgb(236, 72, 153),
            32 => Color.FromRgb(99, 102, 241),
            36 => Color.FromRgb(20, 184, 166),
            _ => Color.FromRgb(100, 116, 139)
        };

        private static Brush ZoneFill(AdditionalZone z, double step)
        {
            return DiameterFill(z.DiameterMm, 75);
        }

        private static double IsoAdditionalAs(PlateReinforcement r, AnalysisSettings s)
        {
            double best = 0;
            void Acc(double asVal, double main, bool show)
            {
                if (!show) return;
                best = Math.Max(best, asVal - main);
            }
            Acc(r.As1, s.AsMainAs1, s.ShowAs1);
            Acc(r.As2, s.AsMainAs2, s.ShowAs2);
            Acc(r.As3, s.AsMainAs3, s.ShowAs3);
            Acc(r.As4, s.AsMainAs4, s.ShowAs4);
            return best;
        }

        private static string BuildZoneLabel(AdditionalZone zone)
        {
            var (a, b) = BuildZoneLabelLines(zone);
            return string.IsNullOrEmpty(b) ? a : a + "\n" + b;
        }

        /// <summary>Ø-длина и поперечная раскладка: число стержней, шаг, ширина зоны.</summary>
        private static (string Line1, string Line2) BuildZoneLabelLines(AdditionalZone zone)
        {
            if (zone.DiameterMm <= 0) return ("", "");
            var lenMm = zone.LengthMm > 0
                ? (int)Math.Round(zone.LengthMm)
                : (int)Math.Round(UnitConversion.MetersToMm(zone.LengthM));
            if (lenMm < 1) lenMm = zone.BarCount > 0 ? zone.BarCount : 0;
            var step = zone.BarStepMm > 0 ? zone.BarStepMm : 200;
            var count = Math.Max(1, zone.BarCount);
            var warning = zone.Comment?.Contains("анкеровка выходит за контур плиты") == true;
            return ($"{zone.DiameterMm}-{lenMm} ×{count}",
                warning ? $"шаг {step} · анкеровка вне плиты" : (zone.BendAtStart || zone.BendAtEnd)
                    ? $"шаг {step} · {(zone.BendAtStart && zone.BendAtEnd ? "П" : "Г")} H{zone.VerticalLegMm:0}" : $"шаг {step}");
        }

        /// <summary>
        /// Глобальные размерные цепочки по всем зонам:
        /// сверху — все X-грани + ближайшие вертикальные оси;
        /// слева — все Y-грани + ближайшие горизонтальные оси.
        /// Одна линия на направление → без наложений отдельных выносок.
        /// </summary>
        private void DrawGlobalDimensionChains(
            DrawingContext dc,
            List<(AdditionalZone Zone, double MinX, double MaxX, double MinY, double MaxY)> boxes,
            Pen pen,
            Typeface typeface,
            double s)
        {
            if (boxes.Count == 0) return;
            double gap = Math.Max(0.18, 22.0 / s);
            double tick = Math.Max(0.05, 7.0 / s);
            double fontModel = Math.Max(0.06, 8.0 / s);
            var brush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            brush.Freeze();

            double slabMaxY = boxes.Max(b => b.MaxY);
            double slabMinX = boxes.Min(b => b.MinX);
            double slabMinY = boxes.Min(b => b.MinY);
            double slabMaxX = boxes.Max(b => b.MaxX);

            // --- Горизонтальная цепочка сверху ---
            var xs = new SortedSet<double>();
            foreach (var b in boxes)
            {
                xs.Add(RoundCoord(b.MinX));
                xs.Add(RoundCoord(b.MaxX));
                if (!string.IsNullOrEmpty(b.Zone.AxisNameX) && b.Zone.AxisPosXM >= slabMinX - 12 && b.Zone.AxisPosXM <= slabMaxX + 12)
                    xs.Add(RoundCoord(b.Zone.AxisPosXM));
            }
            // Ближайшие вертикальные оси из результата
            if (_result?.Axes != null)
            {
                foreach (var ax in _result.Axes)
                {
                    bool vert = ax.Vertical;
                    double pos = ax.Position;
                    if (ax.IsSegment)
                    {
                        vert = Math.Abs(ax.X2 - ax.X1) <= Math.Abs(ax.Y2 - ax.Y1);
                        pos = vert ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
                    }
                    if (!vert) continue;
                    if (pos < slabMinX - 0.5 || pos > slabMaxX + 0.5) continue;
                    // только оси, близкие к какой-то грани зоны
                    if (boxes.Any(b => Math.Abs(b.MinX - pos) < 8 || Math.Abs(b.MaxX - pos) < 8))
                        xs.Add(RoundCoord(pos));
                }
            }

            var xList = xs.ToList();
            if (xList.Count >= 2)
            {
                double yDim = slabMaxY + gap;
                foreach (var x in xList)
                    dc.DrawLine(pen, Tx(x, slabMaxY), Tx(x, yDim + tick * 0.35));
                dc.DrawLine(pen, Tx(xList[0], yDim), Tx(xList[xList.Count - 1], yDim));
                for (int i = 0; i < xList.Count; i++)
                    DrawDimTick(dc, pen, xList[i], yDim, tick, horizontal: true);
                for (int i = 0; i < xList.Count - 1; i++)
                {
                    var a = xList[i];
                    var b = xList[i + 1];
                    var segMm = Math.Round(Math.Abs(UnitConversion.MetersToMm(b - a)) / 10.0) * 10.0;
                    if (segMm < 1) continue;
                    DrawDimText(dc, typeface, brush, fontModel,
                        Tx((a + b) * 0.5, yDim + tick * 0.9), FormatMm(segMm));
                }
            }

            // --- Вертикальная цепочка слева ---
            var ys = new SortedSet<double>();
            foreach (var b in boxes)
            {
                ys.Add(RoundCoord(b.MinY));
                ys.Add(RoundCoord(b.MaxY));
                if (!string.IsNullOrEmpty(b.Zone.AxisNameY) && b.Zone.AxisPosYM >= slabMinY - 12 && b.Zone.AxisPosYM <= slabMaxY + 12)
                    ys.Add(RoundCoord(b.Zone.AxisPosYM));
            }
            if (_result?.Axes != null)
            {
                foreach (var ax in _result.Axes)
                {
                    bool vert = ax.Vertical;
                    double pos = ax.Position;
                    if (ax.IsSegment)
                    {
                        vert = Math.Abs(ax.X2 - ax.X1) <= Math.Abs(ax.Y2 - ax.Y1);
                        pos = vert ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
                    }
                    if (vert) continue;
                    if (pos < slabMinY - 0.5 || pos > slabMaxY + 0.5) continue;
                    if (boxes.Any(b => Math.Abs(b.MinY - pos) < 8 || Math.Abs(b.MaxY - pos) < 8))
                        ys.Add(RoundCoord(pos));
                }
            }

            var yList = ys.ToList();
            if (yList.Count >= 2)
            {
                double xDim = slabMinX - gap;
                foreach (var y in yList)
                    dc.DrawLine(pen, Tx(slabMinX, y), Tx(xDim - tick * 0.35, y));
                dc.DrawLine(pen, Tx(xDim, yList[0]), Tx(xDim, yList[yList.Count - 1]));
                for (int i = 0; i < yList.Count; i++)
                    DrawDimTick(dc, pen, xDim, yList[i], tick, horizontal: false);
                for (int i = 0; i < yList.Count - 1; i++)
                {
                    var a = yList[i];
                    var b = yList[i + 1];
                    var segMm = Math.Round(Math.Abs(UnitConversion.MetersToMm(b - a)) / 10.0) * 10.0;
                    if (segMm < 1) continue;
                    DrawDimText(dc, typeface, brush, fontModel,
                        Tx(xDim - tick * 0.9, (a + b) * 0.5), FormatMm(segMm));
                }
            }
        }

        private static double RoundCoord(double m)
            => Math.Round(m * 1000.0 / 10.0) * 10.0 / 1000.0;

        /// <summary>
        /// Цепочка размеров одной зоны (fallback / выбранная).
        /// </summary>
        private void DrawZoneDimensions(
            DrawingContext dc,
            double minX, double maxX, double minY, double maxY,
            AdditionalZone zone,
            Pen pen,
            Typeface typeface,
            double s)
        {
            var boxes = new List<(AdditionalZone, double, double, double, double)>
            {
                (zone, minX, maxX, minY, maxY)
            };
            DrawGlobalDimensionChains(dc, boxes, pen, typeface, s);
        }

        private static bool NearMm(double a, double b, double tol = 15)
            => Math.Abs(a - b) <= tol;

        private static string FormatMm(double mm)
            => Math.Round(mm).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        private void DrawDimTick(DrawingContext dc, Pen pen, double x, double y, double tick, bool horizontal)
        {
            double d = tick * 0.55;
            if (horizontal)
                dc.DrawLine(pen, Tx(x - d * 0.35, y - d), Tx(x + d * 0.35, y + d));
            else
                dc.DrawLine(pen, Tx(x - d, y - d * 0.35), Tx(x + d, y + d * 0.35));
        }

        private void DrawDimText(
            DrawingContext dc,
            Typeface typeface,
            Brush brush,
            double fontModel,
            Point center,
            string text)
        {
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontModel,
                brush,
                1.0);
            DrawUprightText(dc, ft, center);
        }

        private static RebarLayer DominantLayer(PlateReinforcement r)
        {
            double m = r.As1;
            var layer = RebarLayer.As1;
            if (r.As2 > m) { m = r.As2; layer = RebarLayer.As2; }
            if (r.As3 > m) { m = r.As3; layer = RebarLayer.As3; }
            if (r.As4 > m) { layer = RebarLayer.As4; }
            return layer;
        }

        private static Brush LayerBrush(RebarLayer layer, byte alpha)
        {
            var key = (layer, alpha);
            if (LayerBrushCache.TryGetValue(key, out var cached)) return cached;
            Color c = layer switch
            {
                RebarLayer.As1 => Color.FromRgb(37, 99, 235),
                RebarLayer.As2 => Color.FromRgb(5, 150, 105),
                RebarLayer.As3 => Color.FromRgb(217, 119, 6),
                RebarLayer.As4 => Color.FromRgb(220, 38, 38),
                _ => Colors.Gray
            };
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            LayerBrushCache[key] = b;
            return b;
        }

        /// <summary>
        /// Уникальные отличимые цвета ступеней (без повторов). До 26 ступеней + запас.
        /// </summary>
        private static readonly Color[] DistinctPalette = BuildDistinctPalette(26);

        private static Color[] BuildDistinctPalette(int count)
        {
            // Явно заданные хорошо различимые цвета + равномерный HSL для остатка
            var baseColors = new[]
            {
                Color.FromRgb(37, 99, 235),   // синий
                Color.FromRgb(5, 150, 105),   // зелёный
                Color.FromRgb(217, 119, 6),   // оранжевый
                Color.FromRgb(220, 38, 38),   // красный
                Color.FromRgb(124, 58, 237),  // фиолетовый
                Color.FromRgb(8, 145, 178),   // циан
                Color.FromRgb(202, 138, 4),   // жёлто-янтарный
                Color.FromRgb(219, 39, 119),  // розовый
                Color.FromRgb(22, 163, 74),   // ярко-зелёный
                Color.FromRgb(79, 70, 229),   // индиго
                Color.FromRgb(234, 88, 12),   // глубокий оранж
                Color.FromRgb(13, 148, 136),  // teal
                Color.FromRgb(185, 28, 28),   // тёмно-красный
                Color.FromRgb(67, 56, 202),   // сине-фиолет
                Color.FromRgb(161, 98, 7),    // коричнево-жёлтый
                Color.FromRgb(190, 24, 93),   // маджента
                Color.FromRgb(21, 128, 61),   // лесной
                Color.FromRgb(29, 78, 216),   // ярко-синий
                Color.FromRgb(180, 83, 9),    // охра
                Color.FromRgb(126, 34, 206),  // пурпур
                Color.FromRgb(15, 118, 110),  // тёмный teal
                Color.FromRgb(153, 27, 27),   // бордо
                Color.FromRgb(30, 64, 175),   // navy
                Color.FromRgb(146, 64, 14),   // коричневый
                Color.FromRgb(157, 23, 77),   // вишня
                Color.FromRgb(20, 83, 45),    // тёмно-зелёный
            };

            var list = new List<Color>(count);
            for (int i = 0; i < count; i++)
            {
                if (i < baseColors.Length)
                    list.Add(baseColors[i]);
                else
                {
                    // дополнительные через HSL — сдвиг по оттенку, высокая насыщенность
                    double h = (i * 137.508) % 360.0; // золотой угол → без близких соседей
                    list.Add(HslToRgb(h, 0.72, 0.42));
                }
            }
            return list.ToArray();
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            h = (h % 360 + 360) % 360;
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = l - c / 2;
            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }
            return Color.FromRgb(
                (byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255));
        }

        private static Brush DistinctBandBrush(int band, byte alpha)
        {
            if (band < 0) band = 0;
            if (band >= DistinctPalette.Length)
                band = DistinctPalette.Length - 1; // без зацикливания — последний цвет
            var c = DistinctPalette[band];
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        private static Brush ZoneStroke(AdditionalZone z)
        {
            return LayerBrush(z.Layer, 255);
        }

        private static Pen FreezePen(Color c, double t, bool dash = false)
        {
            var p = new Pen(new SolidColorBrush(c), t);
            if (dash) p.DashStyle = DashStyles.Dash;
            p.Freeze();
            return p;
        }

        private static bool PointInPoly(Point3 p, Point3[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if (((poly[i].Y > p.Y) != (poly[j].Y > p.Y)) &&
                    (p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y + 1e-12) + poly[i].X))
                    inside = !inside;
            }
            return inside;
        }

        private void RaiseStatus(string? msg = null)
        {
            StatusChanged?.Invoke(msg ?? $"зум {_zoom * 100:0}%");
        }
    }
}
