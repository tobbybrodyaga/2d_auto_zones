using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LiraSlabZones.Core;

namespace LiraSlabZones.Revit2023.UI
{
    /// <summary>
    /// Векторный превью-холст: зум без размытия, зоны и контур по сетке КЭ.
    /// </summary>
    public sealed class PreviewViewport : FrameworkElement
    {
        private AnalysisResult? _result;
        private AnalysisSettings _settings = new AnalysisSettings();
        private bool _showMesh = true;
        private bool _showIso;
        private bool _showAxes;

        private double _zoom = 1.0;
        private double _panX;
        private double _panY;
        private bool _panning;
        private Point _panLast;

        private double _modelMinX, _modelMinY, _modelMaxX, _modelMaxY;
        private double _fitScale = 1;

        private readonly List<(AdditionalZone Zone, Point3[] Contour)> _drawZones = new();
        private int? _selectedZoneId;
        public event Action<AdditionalZone>? ZoneSelected;
        public event Action<string>? StatusChanged;

        private static readonly Brush Bg = Brushes.White;
        private static readonly Pen OutlinePen = FreezePen(Color.FromRgb(29, 78, 216), 2.0, dash: true);
        private static readonly Pen MeshPen = FreezePen(Color.FromArgb(80, 55, 65, 81), 0.4);

        public double Zoom => _zoom;

        public void SetData(AnalysisResult? result, AnalysisSettings settings, bool showMesh, bool showIso, bool showAxes = false, bool fitView = true)
        {
            _result = result;
            _settings = settings;
            _showMesh = showMesh;
            _showIso = showIso;
            _showAxes = showAxes;
            RebuildDrawList();
            ComputeModelExtents();
            if (fitView) FitToView();
            InvalidateVisual();
        }

        public void RefreshDisplayFlags(bool showMesh, bool showIso, bool showAxes = false)
        {
            _showMesh = showMesh;
            _showIso = showIso;
            _showAxes = showAxes;
            ComputeModelExtents();
            InvalidateVisual();
        }

        /// <summary>Обновить смещение/поворот без повторной загрузки данных.</summary>
        public void RefreshTransform(AnalysisSettings settings, bool fit = true)
        {
            _settings = settings ?? _settings;
            ComputeModelExtents();
            if (fit) FitToView();
            InvalidateVisual();
            RaiseStatus();
        }

        private Point Tx(Point3 p) => Tx(p.X, p.Y);

        private Point Tx(double x, double y)
        {
            double ox = _settings?.OffsetXM ?? 0;
            double oy = _settings?.OffsetYM ?? 0;
            double deg = _settings?.RotationDeg ?? 0;
            if (Math.Abs(deg) < 1e-9 && Math.Abs(ox) < 1e-12 && Math.Abs(oy) < 1e-12)
                return new Point(x, y);

            double rad = deg * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            double dx = x - _pivotX, dy = y - _pivotY;
            return new Point(c * dx - s * dy + _pivotX + ox, s * dx + c * dy + _pivotY + oy);
        }

        private Point3 UnTx(double x, double y)
        {
            double ox = _settings?.OffsetXM ?? 0;
            double oy = _settings?.OffsetYM ?? 0;
            double deg = _settings?.RotationDeg ?? 0;
            double xr = x - ox, yr = y - oy;
            if (Math.Abs(deg) < 1e-9)
                return new Point3(xr, yr, 0);

            double rad = -deg * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            double dx = xr - _pivotX, dy = yr - _pivotY;
            return new Point3(c * dx - s * dy + _pivotX, s * dx + c * dy + _pivotY, 0);
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
            var before = ScreenToModel(anchor);
            _zoom = zoom;
            var after = ScreenToModel(anchor);
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
                    if (!BBoxHitTx(plate, vMinX, vMaxX, vMinY, vMaxY)) continue;
                    if (m++ > 18000) break;
                    var g = Geom(plate.Contour);
                    if (g == null) continue;
                    dc.DrawGeometry(plateFill, _showMesh ? meshPen : null, g);
                }
            }

            int drawn = 0;
            const int maxDraw = 12000;

            // изополя As: цвет закреплён за интервалом шкалы (как Ogibayushchaya)
            if (_showIso)
            {
                double step = _settings.VisualizationScale;
                if (step <= 0) step = 1.0;
                drawn = 0;
                bool isoLabels = _zoom >= 1.6;
                var isoTypeface = new Typeface("Segoe UI");
                foreach (var plate in _result.Plates)
                {
                    if (!BBoxHitTx(plate, vMinX, vMaxX, vMinY, vMaxY)) continue;
                    if (drawn++ > maxDraw) break;
                    if (!plate.Rebar.Ok) continue;

                    var asAdd = IsoAdditionalAs(plate.Rebar, _settings);
                    if (asAdd <= 0.01) continue;

                    var rgb = IsoColorScale.ColorForValue(asAdd, step);
                    var brush = new SolidColorBrush(Color.FromArgb(160, rgb.R, rgb.G, rgb.B));
                    brush.Freeze();
                    var g = Geom(plate.Contour);
                    if (g != null) dc.DrawGeometry(brush, null, g);

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
                            new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)),
                            1.0);
                        DrawUprightText(dc, ft, tc);
                    }
                }
            }

            // зоны доп.армирования поверх сетки
            drawn = 0;
            bool labels = _zoom >= 1.6;
            bool dims = _zoom >= 1.5;
            var typeface = new Typeface("Segoe UI");
            var dimPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)), Math.Max(1e-4, 1.0 / s));
            dimPen.Freeze();

            var zoneBoxes = new List<(AdditionalZone Zone, double MinX, double MaxX, double MinY, double MaxY)>();
            foreach (var (zone, contour) in _drawZones)
            {
                if (!ContourHitTx(contour, vMinX, vMaxX, vMinY, vMaxY)) continue;
                if (drawn++ > maxDraw) break;

                var g = Geom(contour);
                if (g == null) continue;
                var fill = DiameterFill(zone.DiameterMm, 70);
                bool selected = _selectedZoneId == zone.ZoneId;
                var zoneOutline = new Pen(
                    selected ? Brushes.Black : DiameterStroke(zone.DiameterMm),
                    Math.Max(1e-4, (selected ? 2.2 : 1.35) / s))
                {
                    DashStyle = DashStyles.Dash
                };
                zoneOutline.Freeze();
                dc.DrawGeometry(fill, zoneOutline, g);

                double minX = contour.Min(p => p.X), maxX = contour.Max(p => p.X);
                double minY = contour.Min(p => p.Y), maxY = contour.Max(p => p.Y);
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

            dc.Pop();

            // подпись отметки в экранных координатах (не масштабируется с моделью)
            DrawElevationBadge(dc);
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
                RaiseStatus($"X={m.X:F2} Y={m.Y:F2} м | зум {_zoom * 100:0}% | колесо зум, ПКМ пан");
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var m = ScreenToModel(e.GetPosition(this));
            for (int i = _drawZones.Count - 1; i >= 0; i--)
            {
                var (zone, c) = _drawZones[i];
                if (PointInPoly(m, c))
                {
                    _selectedZoneId = zone.ZoneId;
                    ZoneSelected?.Invoke(zone);
                    InvalidateVisual();
                    e.Handled = true;
                    break;
                }
            }
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (_result != null && _zoom <= 1.01)
                FitToView();
            InvalidateVisual();
        }

        private void RebuildDrawList()
        {
            _drawZones.Clear();
            if (_result == null) return;
            foreach (var z in _result.Zones)
            {
                if (!z.IsValid && z.StatusColor == "error") continue;
                if (z.Contour == null || z.Contour.Count < 3) continue;
                var arr = new Point3[z.Contour.Count];
                for (int i = 0; i < z.Contour.Count; i++) arr[i] = z.Contour[i];
                _drawZones.Add((z, arr));
            }
        }

        private void ComputeModelExtents()
        {
            _modelMinX = _modelMinY = double.MaxValue;
            _modelMaxX = _modelMaxY = double.MinValue;
            _pivotX = _pivotY = 0;
            if (_result == null) return;

            // pivot = центр исходной геометрии (до поворота)
            double sx = 0, sy = 0;
            int n = 0;
            void AccRaw(Point3 p) { sx += p.X; sy += p.Y; n++; }
            if (_result.Outline != null)
                foreach (var p in _result.Outline) AccRaw(p);
            foreach (var plate in _result.Plates)
            foreach (var p in plate.Contour)
                AccRaw(p);
            if (n > 0) { _pivotX = sx / n; _pivotY = sy / n; }

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

        private bool ContourHitTx(Point3[] c, double minX, double maxX, double minY, double maxY)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in c)
            {
                var t = Tx(p);
                if (t.X < x0) x0 = t.X; if (t.Y < y0) y0 = t.Y;
                if (t.X > x1) x1 = t.X; if (t.Y > y1) y1 = t.Y;
            }
            return !(x1 < minX || x0 > maxX || y1 < minY || y0 > maxY);
        }

        private static bool BBoxHit(LiraPlateElement plate, double minX, double maxX, double minY, double maxY)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in plate.Contour)
            {
                if (p.X < x0) x0 = p.X; if (p.Y < y0) y0 = p.Y;
                if (p.X > x1) x1 = p.X; if (p.Y > y1) y1 = p.Y;
            }
            return !(x1 < minX || x0 > maxX || y1 < minY || y0 > maxY);
        }

        private bool BBoxHitTx(LiraPlateElement plate, double minX, double maxX, double minY, double maxY)
        {
            // при повороте AABB исходного контура ненадёжен — проверяем трансформированные углы
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in plate.Contour)
            {
                var t = Tx(p);
                if (t.X < x0) x0 = t.X; if (t.Y < y0) y0 = t.Y;
                if (t.X > x1) x1 = t.X; if (t.Y > y1) y1 = t.Y;
            }
            return !(x1 < minX || x0 > maxX || y1 < minY || y0 > maxY);
        }

        private static bool ContourHit(Point3[] c, double minX, double maxX, double minY, double maxY)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in c)
            {
                if (p.X < x0) x0 = p.X; if (p.Y < y0) y0 = p.Y;
                if (p.X > x1) x1 = p.X; if (p.Y > y1) y1 = p.Y;
            }
            return !(x1 < minX || x0 > maxX || y1 < minY || y0 > maxY);
        }

        private StreamGeometry? Geom(IList<Point3> contour)
        {
            if (contour == null || contour.Count < 2) return null;
            var g = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var ctx = g.Open())
            {
                var p0 = Tx(contour[0]);
                ctx.BeginFigure(p0, true, true);
                for (int i = 1; i < contour.Count; i++)
                    ctx.LineTo(Tx(contour[i]), true, false);
            }
            g.Freeze();
            return g;
        }

        private StreamGeometry? Geom(Point3[] contour)
        {
            if (contour == null || contour.Length < 2) return null;
            var g = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var ctx = g.Open())
            {
                var p0 = Tx(contour[0]);
                ctx.BeginFigure(p0, true, true);
                for (int i = 1; i < contour.Length; i++)
                    ctx.LineTo(Tx(contour[i]), true, false);
            }
            g.Freeze();
            return g;
        }

        private static Brush DiameterFill(int diameterMm, byte alpha)
        {
            var c = DiameterColor(diameterMm);
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        private static Brush DiameterStroke(int diameterMm)
        {
            var c = DiameterColor(diameterMm);
            var b = new SolidColorBrush(Color.FromArgb(230, c.R, c.G, c.B));
            b.Freeze();
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

        /// <summary>Формат как в Revit: «12-3900» / «шаг 200».</summary>
        private static (string Line1, string Line2) BuildZoneLabelLines(AdditionalZone zone)
        {
            if (zone.DiameterMm <= 0) return ("", "");
            var lenMm = zone.LengthMm > 0
                ? (int)Math.Round(zone.LengthMm)
                : (int)Math.Round(UnitConversion.MetersToMm(zone.LengthM));
            if (lenMm < 1) lenMm = zone.BarCount > 0 ? zone.BarCount : 0;
            var step = zone.BarStepMm > 0 ? zone.BarStepMm : 200;
            return ($"{zone.DiameterMm}-{lenMm}", $"шаг {step}");
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
