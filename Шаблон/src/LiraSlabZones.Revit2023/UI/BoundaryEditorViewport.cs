using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LiraSlabZones.Core;

namespace LiraSlabZones.Revit2023.UI
{
    public enum BoundaryEditMode
    {
        Select,
        AddOpening,
        MoveOpening,
        ResizeOpening,
        AddBoundary,
        AddOuterBoundary
    }

    public sealed class BoundaryEditorViewport : FrameworkElement
    {
        private static readonly (Color Fill, Color Stroke)[] OpeningHatchPalette =
        {
            (Color.FromRgb(254, 226, 226), Color.FromRgb(220, 38, 38)),
            (Color.FromRgb(219, 234, 254), Color.FromRgb(37, 99, 235)),
            (Color.FromRgb(209, 250, 229), Color.FromRgb(5, 150, 105)),
            (Color.FromRgb(254, 243, 199), Color.FromRgb(217, 119, 6)),
            (Color.FromRgb(237, 233, 254), Color.FromRgb(124, 58, 237)),
            (Color.FromRgb(207, 250, 254), Color.FromRgb(8, 145, 178))
        };

        private sealed class MeshVertex
        {
            public LiraPlateElement Plate = null!;
            public int Index;
            public Point3 Position = null!;
        }

        private sealed class MeshEdge
        {
            public MeshEdgeAnchor Anchor = null!;
            public Point3 Start => Anchor.Start;
            public Point3 End => Anchor.End;
        }

        private enum OpeningCorner { MinMin, MaxMin, MaxMax, MinMax }

        private List<LiraPlateElement> _plates = new List<LiraPlateElement>();
        private List<Point3> _outline = new List<Point3>();
        private List<OpeningInfo> _openings = new List<OpeningInfo>();
        private List<BoundaryConditionInfo> _boundaries = new List<BoundaryConditionInfo>();
        private readonly List<MeshVertex> _vertices = new List<MeshVertex>();
        private readonly List<MeshEdge> _edges = new List<MeshEdge>();
        private readonly HashSet<string> _edgeKeys = new HashSet<string>();
        private int _selectedOpeningIndex = -1;
        private int _selectedBoundaryIndex = -1;
        private BoundaryEditMode _mode;
        private double _minX, _maxX, _minY, _maxY;
        private double _zoom = 1;
        private double _panX, _panY;
        private double _pixelsPerMeter = 1;
        private Point3? _dragStart;
        private Rect? _dragPreview;
        private bool _dragging;
        private bool _panning;
        private Point _lastPanPoint;
        private OpeningCorner _resizeCorner;

        public BoundaryEditorViewport()
        {
            Focusable = true;
            SizeChanged += (_, __) => InvalidateVisual();
        }

        public event Action? SelectionChanged;
        public event Action<string>? StatusChanged;
        public event Action? GeometryChanged;

        public bool ShowMesh { get; set; } = true;
        public int PlateCount => _plates.Count;
        public OpeningInfo? SelectedOpening => _selectedOpeningIndex >= 0 && _selectedOpeningIndex < _openings.Count
            ? _openings[_selectedOpeningIndex] : null;
        public BoundaryConditionInfo? SelectedBoundary => _selectedBoundaryIndex >= 0 && _selectedBoundaryIndex < _boundaries.Count
            ? _boundaries[_selectedBoundaryIndex] : null;

        public void SetData(IList<LiraPlateElement> plates, IList<Point3> outline,
            List<OpeningInfo> openings, List<BoundaryConditionInfo> boundaries)
        {
            _plates = plates?.Where(plate => plate.Contour != null && plate.Contour.Count >= 3).ToList()
                ?? new List<LiraPlateElement>();
            _outline = outline?.Select(point => new Point3(point.X, point.Y, point.Z)).ToList()
                ?? new List<Point3>();
            _openings = openings ?? new List<OpeningInfo>();
            _boundaries = boundaries ?? new List<BoundaryConditionInfo>();
            BuildMeshAnchors();
            EnsureOpeningBindings();
            UpdateBounds();
            FitToView();
        }

        public void SetMode(BoundaryEditMode mode)
        {
            _mode = mode;
            _dragging = false;
            _dragStart = null;
            _dragPreview = null;
            var message = mode switch
            {
                BoundaryEditMode.Select => "Выбор отверстия или границы",
                BoundaryEditMode.AddOpening => "Протяните рамку отверстия: углы привяжутся к вершинам КЭ",
                BoundaryEditMode.MoveOpening => "Перетащите выбранное отверстие; начальная точка привяжется к КЭ",
                BoundaryEditMode.ResizeOpening => "Перетащите угол выбранного отверстия к вершине КЭ",
                BoundaryEditMode.AddBoundary => "Щёлкните ребро КЭ, чтобы добавить граничную линию",
                BoundaryEditMode.AddOuterBoundary => "Щёлкните грань КЭ, чтобы построить внешний контур связанной области",
                _ => ""
            };
            StatusChanged?.Invoke(message);
            Cursor = mode == BoundaryEditMode.Select ? Cursors.Arrow : Cursors.Cross;
            InvalidateVisual();
        }

        public void FitToView()
        {
            _zoom = 1;
            _panX = 0;
            _panY = 0;
            InvalidateVisual();
        }

        public void DeleteSelected()
        {
            if (_selectedOpeningIndex >= 0 && _selectedOpeningIndex < _openings.Count)
            {
                var removed = _openings[_selectedOpeningIndex];
                _openings.RemoveAt(_selectedOpeningIndex);
                _selectedOpeningIndex = -1;
                StatusChanged?.Invoke($"Отверстие #{removed.OpeningId} удалено");
                GeometryChanged?.Invoke();
                SelectionChanged?.Invoke();
                InvalidateVisual();
                return;
            }

            if (_selectedBoundaryIndex >= 0 && _selectedBoundaryIndex < _boundaries.Count)
            {
                var removed = _boundaries[_selectedBoundaryIndex];
                _boundaries.RemoveAt(_selectedBoundaryIndex);
                _selectedBoundaryIndex = -1;
                StatusChanged?.Invoke($"Граница #{removed.BoundaryId} удалена");
                GeometryChanged?.Invoke();
                SelectionChanged?.Invoke();
                InvalidateVisual();
                return;
            }

            StatusChanged?.Invoke("Сначала выберите отверстие или границу");
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(250, 251, 252)), null,
                new Rect(0, 0, ActualWidth, ActualHeight));
            if (_plates.Count == 0 || ActualWidth < 10 || ActualHeight < 10) return;

            var transform = CreateTransform();
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            dc.PushTransform(new MatrixTransform(transform));
            var meshFill = new SolidColorBrush(Color.FromRgb(250, 251, 252));
            var meshPen = new Pen(new SolidColorBrush(Color.FromRgb(148, 163, 184)), 0.8 / _pixelsPerMeter);
            var hiddenMeshPen = new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 0.8 / _pixelsPerMeter);
            meshFill.Freeze(); meshPen.Freeze(); hiddenMeshPen.Freeze();

            var view = VisibleModelBounds(transform);
            int count = 0;
            foreach (var plate in _plates)
            {
                if (++count > 40000) break;
                if (!BoundsOverlap(plate.Contour, view)) continue;
                dc.DrawGeometry(meshFill, ShowMesh ? meshPen : hiddenMeshPen, BuildPath(plate.Contour));
            }

            DrawOpenings(dc);
            DrawBoundaries(dc);
            if (_outline.Count >= 3)
            {
                var outlinePen = new Pen(new SolidColorBrush(Color.FromRgb(15, 23, 42)), 2.5 / _pixelsPerMeter);
                outlinePen.Freeze();
                dc.DrawGeometry(null, outlinePen, BuildPath(_outline));
            }
            DrawOpeningHandles(dc);
            DrawDragPreview(dc);
            dc.Pop();
            dc.Pop();
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.ChangedButton == MouseButton.Middle)
            {
                _panning = true;
                _lastPanPoint = e.GetPosition(this);
                CaptureMouse();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton != MouseButton.Left) return;
            var world = ScreenToWorld(e.GetPosition(this));
            if (world == null) return;

            switch (_mode)
            {
                case BoundaryEditMode.AddBoundary:
                    AddBoundaryAt(world);
                    e.Handled = true;
                    return;
                case BoundaryEditMode.AddOuterBoundary:
                    AddOuterBoundaryAt(world);
                    e.Handled = true;
                    return;
                case BoundaryEditMode.AddOpening:
                    _dragStart = world;
                    _dragging = true;
                    CaptureMouse();
                    e.Handled = true;
                    return;
                case BoundaryEditMode.MoveOpening:
                    _selectedOpeningIndex = FindOpeningAt(world);
                    _selectedBoundaryIndex = -1;
                    if (_selectedOpeningIndex >= 0)
                    {
                        _dragStart = world;
                        _dragPreview = OpeningBounds(_openings[_selectedOpeningIndex]);
                        _dragging = true;
                        CaptureMouse();
                    }
                    else StatusChanged?.Invoke("Выберите отверстие и перетащите его");
                    SelectionChanged?.Invoke();
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                case BoundaryEditMode.ResizeOpening:
                    if (_selectedOpeningIndex < 0 || _selectedOpeningIndex >= _openings.Count)
                        _selectedOpeningIndex = FindOpeningAt(world);
                    _selectedBoundaryIndex = -1;
                    if (_selectedOpeningIndex >= 0 && TryGetCorner(_openings[_selectedOpeningIndex], world, out _resizeCorner))
                    {
                        _dragStart = world;
                        _dragging = true;
                        CaptureMouse();
                    }
                    else StatusChanged?.Invoke("Выберите угол отверстия и перетащите его к вершине КЭ");
                    SelectionChanged?.Invoke();
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                default:
                    SelectAt(world);
                    e.Handled = true;
                    return;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var screen = e.GetPosition(this);
            if (_panning && e.MiddleButton == MouseButtonState.Pressed)
            {
                _panX += screen.X - _lastPanPoint.X;
                _panY += screen.Y - _lastPanPoint.Y;
                _lastPanPoint = screen;
                InvalidateVisual();
                return;
            }
            if (!_dragging || _dragStart == null) return;
            var world = ScreenToWorld(screen);
            if (world == null) return;
            _dragPreview = _mode switch
            {
                BoundaryEditMode.AddOpening => RectFrom(_dragStart, world),
                BoundaryEditMode.MoveOpening => MoveBounds(_openings[_selectedOpeningIndex], world),
                BoundaryEditMode.ResizeOpening => ResizeBounds(_openings[_selectedOpeningIndex], world),
                _ => _dragPreview
            };
            InvalidateVisual();
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (_panning && e.ChangedButton == MouseButton.Middle)
            {
                _panning = false;
                ReleaseMouseCapture();
                return;
            }
            if (e.ChangedButton != MouseButton.Left || !_dragging || _dragStart == null) return;
            var end = ScreenToWorld(e.GetPosition(this));
            if (end != null)
            {
                if (_mode == BoundaryEditMode.AddOpening)
                    CreateOpening(_dragStart, end);
                else if (_mode == BoundaryEditMode.MoveOpening)
                    MoveOpening(end);
                else if (_mode == BoundaryEditMode.ResizeOpening)
                    ResizeOpening(end);
            }
            _dragging = false;
            _dragStart = null;
            _dragPreview = null;
            ReleaseMouseCapture();
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            var anchor = e.GetPosition(this);
            var before = ScreenToWorld(anchor);
            var zoom = Math.Max(0.15, Math.Min(24, _zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15)));
            if (Math.Abs(zoom - _zoom) > 1e-9)
            {
                _zoom = zoom;
                var after = ScreenToWorld(anchor);
                if (before != null && after != null)
                {
                    _panX += (after.X - before.X) * _pixelsPerMeter;
                    _panY -= (after.Y - before.Y) * _pixelsPerMeter;
                }
            }
            InvalidateVisual();
            e.Handled = true;
        }

        private void BuildMeshAnchors()
        {
            _vertices.Clear();
            _edges.Clear();
            _edgeKeys.Clear();
            foreach (var plate in _plates)
            {
                for (var i = 0; i < plate.Contour.Count; i++)
                {
                    var start = plate.Contour[i];
                    var end = plate.Contour[(i + 1) % plate.Contour.Count];
                    _vertices.Add(new MeshVertex { Plate = plate, Index = i, Position = start });
                    var key = EdgeKey(start, end);
                    if (!_edgeKeys.Add(key)) continue;
                    _edges.Add(new MeshEdge
                    {
                        Anchor = new MeshEdgeAnchor
                        {
                            ElementId = plate.Id,
                            EdgeIndex = i,
                            Start = Copy(start),
                            End = Copy(end)
                        }
                    });
                }
            }
        }

        private void EnsureOpeningBindings()
        {
            var nextId = 1;
            foreach (var opening in _openings)
            {
                if (opening.OpeningId <= 0 || _openings.Count(item => item.OpeningId == opening.OpeningId) > 1)
                    opening.OpeningId = nextId;
                nextId = Math.Max(nextId, opening.OpeningId + 1);
                if (opening.MeshAnchors == null || opening.MeshAnchors.Count != 4)
                    opening.MeshAnchors = new List<MeshVertexAnchor>();
                if (opening.MeshAnchors.Count == 0)
                {
                    opening.MeshAnchors.Add(NearestVertex(new Point3(opening.MinXM, opening.MinYM, 0)));
                    opening.MeshAnchors.Add(NearestVertex(new Point3(opening.MaxXM, opening.MinYM, 0)));
                    opening.MeshAnchors.Add(NearestVertex(new Point3(opening.MaxXM, opening.MaxYM, 0)));
                    opening.MeshAnchors.Add(NearestVertex(new Point3(opening.MinXM, opening.MaxYM, 0)));
                }
                if (opening.ElementIds == null || opening.ElementIds.Count == 0)
                    opening.ElementIds = FindElementsForRect(OpeningBounds(opening));
            }
        }

        private void UpdateBounds()
        {
            var points = _outline.Count >= 3 ? _outline : _plates.SelectMany(plate => plate.Contour).ToList();
            if (points.Count == 0) { _minX = _minY = 0; _maxX = _maxY = 1; return; }
            _minX = points.Min(point => point.X);
            _maxX = points.Max(point => point.X);
            _minY = points.Min(point => point.Y);
            _maxY = points.Max(point => point.Y);
            var pad = Math.Max(0.15, Math.Max(_maxX - _minX, _maxY - _minY) * 0.035);
            _minX -= pad; _maxX += pad; _minY -= pad; _maxY += pad;
        }

        private Matrix CreateTransform()
        {
            var spanX = Math.Max(0.01, _maxX - _minX);
            var spanY = Math.Max(0.01, _maxY - _minY);
            _pixelsPerMeter = Math.Max(1, Math.Min((ActualWidth - 24) / spanX, (ActualHeight - 24) / spanY) * _zoom);
            var drawnWidth = spanX * _pixelsPerMeter;
            var drawnHeight = spanY * _pixelsPerMeter;
            var transform = new Matrix();
            transform.Translate(-_minX, -_minY);
            transform.Scale(_pixelsPerMeter, -_pixelsPerMeter);
            transform.Translate((ActualWidth - drawnWidth) * 0.5 + _panX,
                (ActualHeight - drawnHeight) * 0.5 + drawnHeight + _panY);
            return transform;
        }

        private Rect VisibleModelBounds(Matrix transform)
        {
            transform.Invert();
            var first = transform.Transform(new Point(0, 0));
            var last = transform.Transform(new Point(ActualWidth, ActualHeight));
            return new Rect(new Point(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y)),
                new Point(Math.Max(first.X, last.X), Math.Max(first.Y, last.Y)));
        }

        private Point3? ScreenToWorld(Point point)
        {
            if (ActualWidth < 1 || ActualHeight < 1) return null;
            var transform = CreateTransform();
            transform.Invert();
            var result = transform.Transform(point);
            return new Point3(result.X, result.Y, 0);
        }

        private void SelectAt(Point3 point)
        {
            _selectedOpeningIndex = FindOpeningAt(point);
            _selectedBoundaryIndex = _selectedOpeningIndex < 0 ? FindBoundaryAt(point) : -1;
            if (_selectedOpeningIndex >= 0)
                StatusChanged?.Invoke($"Выбрано отверстие #{_openings[_selectedOpeningIndex].OpeningId}");
            else if (_selectedBoundaryIndex >= 0)
                StatusChanged?.Invoke($"Выбрана граница #{_boundaries[_selectedBoundaryIndex].BoundaryId}");
            else
                StatusChanged?.Invoke("Выберите отверстие или границу; колесо мыши — масштаб, средняя кнопка — панорама");
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        private int FindOpeningAt(Point3 point)
        {
            for (var i = _openings.Count - 1; i >= 0; i--)
            {
                var bounds = OpeningBounds(_openings[i]);
                if (point.X >= bounds.Left && point.X <= bounds.Right &&
                    point.Y >= bounds.Top && point.Y <= bounds.Bottom) return i;
            }
            return -1;
        }

        private int FindBoundaryAt(Point3 point)
        {
            var tolerance = 8 / Math.Max(1, _pixelsPerMeter);
            for (var i = _boundaries.Count - 1; i >= 0; i--)
                foreach (var anchor in _boundaries[i].Edges)
                {
                    var edge = ResolveEdge(anchor);
                    if (edge != null && DistanceToSegment(point, edge.Value.Start, edge.Value.End) <= tolerance)
                        return i;
                }
            return -1;
        }

        private void AddBoundaryAt(Point3 point)
        {
            var tolerance = 8 / Math.Max(1, _pixelsPerMeter);
            var nearest = _edges.Select(edge => (Edge: edge, Distance: DistanceToSegment(point, edge.Start, edge.End)))
                .OrderBy(item => item.Distance).FirstOrDefault();
            if (nearest.Edge == null || nearest.Distance > tolerance)
            {
                StatusChanged?.Invoke("Ребро КЭ не найдено рядом с курсором");
                return;
            }
            var duplicate = _boundaries.FindIndex(boundary => boundary.Edges.Any(edge =>
                EdgeKey(edge.Start, edge.End) == EdgeKey(nearest.Edge.Start, nearest.Edge.End)));
            if (duplicate >= 0)
            {
                _selectedBoundaryIndex = duplicate;
                _selectedOpeningIndex = -1;
                StatusChanged?.Invoke("Это ребро уже добавлено как граница");
            }
            else
            {
                var id = _boundaries.Count == 0 ? 1 : _boundaries.Max(boundary => boundary.BoundaryId) + 1;
                _boundaries.Add(new BoundaryConditionInfo
                {
                    BoundaryId = id,
                    Name = $"Граница {id}",
                    Edges = new List<MeshEdgeAnchor> { Clone(nearest.Edge.Anchor) }
                });
                _selectedBoundaryIndex = _boundaries.Count - 1;
                _selectedOpeningIndex = -1;
                StatusChanged?.Invoke($"Добавлено ребро КЭ {nearest.Edge.Anchor.ElementId}, ребро {nearest.Edge.Anchor.EdgeIndex + 1}");
                GeometryChanged?.Invoke();
            }
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        private void AddOuterBoundaryAt(Point3 point)
        {
            var seed = _plates.AsEnumerable().Reverse().FirstOrDefault(plate =>
                PointInPolygon(new Point(point.X, point.Y), plate.Contour));
            if (seed == null)
            {
                StatusChanged?.Invoke("Щёлкните внутри грани конечного элемента");
                return;
            }

            var component = FindConnectedPlates(seed);
            var outline = MeshBoundary.BuildOuterContour(component);
            if (outline.Count < 3)
            {
                StatusChanged?.Invoke($"Не удалось определить внешний контур для КЭ {seed.Id}");
                return;
            }

            var componentIds = new HashSet<int>(component.Select(plate => plate.Id));
            const double tolerance = 1e-5;
            var outerEdges = _edges
                .Where(edge => componentIds.Contains(edge.Anchor.ElementId) &&
                    IsOnOutline(edge.Start, edge.End, outline, tolerance))
                .ToList();
            if (outerEdges.Count == 0)
            {
                StatusChanged?.Invoke($"На внешнем контуре области КЭ {seed.Id} не найдены рёбра сетки");
                return;
            }

            var boundaryName = $"Внешний контур (КЭ {component.Min(plate => plate.Id)})";
            var expectedKeys = new HashSet<string>(outerEdges.Select(edge => EdgeKey(edge.Start, edge.End)));
            var outerBoundaryIndex = _boundaries.FindIndex(boundary =>
                string.Equals(boundary.Name, boundaryName, StringComparison.Ordinal));
            if (outerBoundaryIndex < 0)
                outerBoundaryIndex = _boundaries.FindIndex(boundary =>
                    string.Equals(boundary.Name, "Внешний контур", StringComparison.Ordinal) &&
                    boundary.Edges != null && boundary.Edges.All(edge => edge.Start != null && edge.End != null &&
                        expectedKeys.Contains(EdgeKey(edge.Start, edge.End))));

            var anchors = outerEdges.Select(edge => Clone(edge.Anchor)).ToList();
            BoundaryConditionInfo boundary;
            if (outerBoundaryIndex >= 0)
            {
                boundary = _boundaries[outerBoundaryIndex];
                boundary.Name = boundaryName;
                boundary.Edges = anchors;
            }
            else
            {
                var id = _boundaries.Count == 0 ? 1 : _boundaries.Max(item => item.BoundaryId) + 1;
                boundary = new BoundaryConditionInfo
                {
                    BoundaryId = id,
                    Name = boundaryName,
                    Edges = anchors
                };
                _boundaries.Add(boundary);
                outerBoundaryIndex = _boundaries.Count - 1;
            }

            _selectedBoundaryIndex = outerBoundaryIndex;
            _selectedOpeningIndex = -1;
            _mode = BoundaryEditMode.Select;
            Cursor = Cursors.Arrow;
            StatusChanged?.Invoke($"Построен внешний контур по грани КЭ {seed.Id}: " +
                $"рёбер {boundary.Edges.Count}, КЭ в связанной области {component.Count}");
            GeometryChanged?.Invoke();
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        private List<LiraPlateElement> FindConnectedPlates(LiraPlateElement seed)
        {
            var edgeOwners = new Dictionary<string, List<int>>();
            foreach (var plate in _plates)
            {
                for (var i = 0; i < plate.Contour.Count; i++)
                {
                    var key = EdgeKey(plate.Contour[i], plate.Contour[(i + 1) % plate.Contour.Count]);
                    if (!edgeOwners.TryGetValue(key, out var owners))
                    {
                        owners = new List<int>();
                        edgeOwners.Add(key, owners);
                    }
                    if (!owners.Contains(plate.Id)) owners.Add(plate.Id);
                }
            }

            var platesById = _plates.GroupBy(plate => plate.Id).ToDictionary(group => group.Key, group => group.First());
            var connectedIds = new HashSet<int> { seed.Id };
            var pending = new Queue<int>();
            pending.Enqueue(seed.Id);
            while (pending.Count > 0)
            {
                var plateId = pending.Dequeue();
                if (!platesById.TryGetValue(plateId, out var plate)) continue;
                for (var i = 0; i < plate.Contour.Count; i++)
                {
                    var key = EdgeKey(plate.Contour[i], plate.Contour[(i + 1) % plate.Contour.Count]);
                    if (!edgeOwners.TryGetValue(key, out var owners)) continue;
                    foreach (var neighborId in owners)
                        if (connectedIds.Add(neighborId)) pending.Enqueue(neighborId);
                }
            }

            return _plates.Where(plate => connectedIds.Contains(plate.Id)).ToList();
        }

        private static bool IsOnOutline(Point3 start, Point3 end, IList<Point3> outline, double tolerance)
        {
            var midpoint = new Point3((start.X + end.X) * 0.5, (start.Y + end.Y) * 0.5,
                (start.Z + end.Z) * 0.5);
            return DistanceToOutline(start, outline) <= tolerance &&
                   DistanceToOutline(midpoint, outline) <= tolerance &&
                   DistanceToOutline(end, outline) <= tolerance;
        }

        private static double DistanceToOutline(Point3 point, IList<Point3> outline)
        {
            var distance = double.PositiveInfinity;
            for (var i = 0; i < outline.Count; i++)
                distance = Math.Min(distance, DistanceToSegment(point, outline[i], outline[(i + 1) % outline.Count]));
            return distance;
        }

        private void CreateOpening(Point3 rawStart, Point3 rawEnd)
        {
            if (!TrySnapToVertex(rawStart, out var start) || !TrySnapToVertex(rawEnd, out var end))
            {
                StatusChanged?.Invoke("Углы отверстия должны находиться рядом с вершинами КЭ");
                return;
            }
            var bounds = RectFrom(start, end);
            if (bounds.Width < 0.05 || bounds.Height < 0.05)
            {
                StatusChanged?.Invoke("Отверстие слишком мало после привязки к сетке КЭ");
                return;
            }
            if (_outline.Count >= 3 && new[]
                {
                    new Point(bounds.Left, bounds.Top), new Point(bounds.Right, bounds.Top),
                    new Point(bounds.Right, bounds.Bottom), new Point(bounds.Left, bounds.Bottom)
                }.Any(corner => !PointInPolygon(corner, _outline)))
            {
                StatusChanged?.Invoke("Отверстие должно целиком находиться внутри внешнего контура");
                return;
            }

            var id = _openings.Count == 0 ? 1 : _openings.Max(opening => opening.OpeningId) + 1;
            var opening = new OpeningInfo
            {
                OpeningId = id,
                MinXM = bounds.Left,
                MaxXM = bounds.Right,
                MinYM = bounds.Top,
                MaxYM = bounds.Bottom
            };
            BindOpening(opening);
            _openings.Add(opening);
            _selectedOpeningIndex = _openings.Count - 1;
            _selectedBoundaryIndex = -1;
            StatusChanged?.Invoke($"Создано отверстие #{id}, привязано КЭ: {opening.ElementIds.Count}");
            GeometryChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        private void MoveOpening(Point3 cursor)
        {
            if (_selectedOpeningIndex < 0 || _dragStart == null) return;
            var opening = _openings[_selectedOpeningIndex];
            var dx = cursor.X - _dragStart.X;
            var dy = cursor.Y - _dragStart.Y;
            var old = OpeningBounds(opening);
            if (!TrySnapToVertex(new Point3(old.Left + dx, old.Top + dy, 0), out var snappedOrigin) ||
                !TrySnapToVertex(new Point3(old.Right + dx, old.Bottom + dy, 0), out var snappedOpposite))
            {
                StatusChanged?.Invoke("Перемещение отменено: углы должны оставаться рядом с вершинами КЭ");
                return;
            }
            var newBounds = RectFrom(snappedOrigin, snappedOpposite);
            if (newBounds.Width < 0.05 || newBounds.Height < 0.05)
            {
                StatusChanged?.Invoke("Перемещение отменено: отверстие слишком мало после привязки");
                return;
            }
            var newMinX = newBounds.Left;
            var newMinY = newBounds.Top;
            if (_outline.Count >= 3 && new[]
            {
                new Point(newBounds.Left, newBounds.Top), new Point(newBounds.Right, newBounds.Top),
                new Point(newBounds.Right, newBounds.Bottom), new Point(newBounds.Left, newBounds.Bottom)
            }.Any(corner => !PointInPolygon(corner, _outline)))
            {
                StatusChanged?.Invoke("Отверстие останется без сдвига: оно выходит за внешний контур");
                return;
            }
            opening.MinXM = newMinX; opening.MaxXM = newBounds.Right;
            opening.MinYM = newMinY; opening.MaxYM = newBounds.Bottom;
            BindOpening(opening);
            StatusChanged?.Invoke($"Отверстие #{opening.OpeningId} привязано к КЭ");
            GeometryChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        private void ResizeOpening(Point3 cursor)
        {
            if (_selectedOpeningIndex < 0) return;
            if (!TrySnapToVertex(cursor, out var point))
            {
                StatusChanged?.Invoke("Изменение отменено: угол должен находиться рядом с вершиной КЭ");
                return;
            }
            var opening = _openings[_selectedOpeningIndex];
            var bounds = OpeningBounds(opening);
            var minX = bounds.Left;
            var maxX = bounds.Right;
            var minY = bounds.Top;
            var maxY = bounds.Bottom;
            switch (_resizeCorner)
            {
                case OpeningCorner.MinMin:
                    minX = Math.Min(point.X, bounds.Right - 0.05);
                    minY = Math.Min(point.Y, bounds.Bottom - 0.05);
                    break;
                case OpeningCorner.MaxMin:
                    maxX = Math.Max(point.X, bounds.Left + 0.05);
                    minY = Math.Min(point.Y, bounds.Bottom - 0.05);
                    break;
                case OpeningCorner.MaxMax:
                    maxX = Math.Max(point.X, bounds.Left + 0.05);
                    maxY = Math.Max(point.Y, bounds.Top + 0.05);
                    break;
                case OpeningCorner.MinMax:
                    minX = Math.Min(point.X, bounds.Right - 0.05);
                    maxY = Math.Max(point.Y, bounds.Top + 0.05);
                    break;
            }
            if (maxX - minX < 0.05 || maxY - minY < 0.05)
            {
                StatusChanged?.Invoke("Размер не изменён: отверстие слишком мало");
                return;
            }
            if (_outline.Count >= 3 && new[]
            {
                new Point(minX, minY), new Point(maxX, minY),
                new Point(maxX, maxY), new Point(minX, maxY)
            }.Any(corner => !PointInPolygon(corner, _outline)))
            {
                StatusChanged?.Invoke("Размер не изменён: отверстие выходит за внешний контур");
                return;
            }
            opening.MinXM = minX; opening.MaxXM = maxX;
            opening.MinYM = minY; opening.MaxYM = maxY;
            BindOpening(opening);
            StatusChanged?.Invoke($"Размер отверстия #{opening.OpeningId} изменён, привязано КЭ: {opening.ElementIds.Count}");
            GeometryChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        private Rect MoveBounds(OpeningInfo opening, Point3 cursor)
        {
            var bounds = OpeningBounds(opening);
            var dx = cursor.X - (_dragStart?.X ?? cursor.X);
            var dy = cursor.Y - (_dragStart?.Y ?? cursor.Y);
            return new Rect(bounds.X + dx, bounds.Y + dy, bounds.Width, bounds.Height);
        }

        private Rect ResizeBounds(OpeningInfo opening, Point3 cursor)
        {
            var point = SnapToVertex(cursor);
            var bounds = OpeningBounds(opening);
            switch (_resizeCorner)
            {
                case OpeningCorner.MinMin: bounds = new Rect(Math.Min(point.X, bounds.Right - 0.05), Math.Min(point.Y, bounds.Bottom - 0.05),
                    Math.Max(0.05, bounds.Right - point.X), Math.Max(0.05, bounds.Bottom - point.Y)); break;
                case OpeningCorner.MaxMin: bounds = new Rect(bounds.Left, Math.Min(point.Y, bounds.Bottom - 0.05),
                    Math.Max(0.05, point.X - bounds.Left), Math.Max(0.05, bounds.Bottom - point.Y)); break;
                case OpeningCorner.MaxMax: bounds = new Rect(bounds.Left, bounds.Top,
                    Math.Max(0.05, point.X - bounds.Left), Math.Max(0.05, point.Y - bounds.Top)); break;
                case OpeningCorner.MinMax: bounds = new Rect(Math.Min(point.X, bounds.Right - 0.05), bounds.Top,
                    Math.Max(0.05, bounds.Right - point.X), Math.Max(0.05, point.Y - bounds.Top)); break;
            }
            return bounds;
        }

        private bool TryGetCorner(OpeningInfo opening, Point3 point, out OpeningCorner corner)
        {
            var bounds = OpeningBounds(opening);
            var points = new[]
            {
                new Point(bounds.Left, bounds.Top), new Point(bounds.Right, bounds.Top),
                new Point(bounds.Right, bounds.Bottom), new Point(bounds.Left, bounds.Bottom)
            };
            var nearest = Enumerable.Range(0, points.Length)
                .OrderBy(index => Distance(point, points[index])).First();
            corner = (OpeningCorner)nearest;
            return Distance(point, points[nearest]) <= 10 / Math.Max(1, _pixelsPerMeter);
        }

        private void BindOpening(OpeningInfo opening)
        {
            var corners = new[]
            {
                new Point3(opening.MinXM, opening.MinYM, 0), new Point3(opening.MaxXM, opening.MinYM, 0),
                new Point3(opening.MaxXM, opening.MaxYM, 0), new Point3(opening.MinXM, opening.MaxYM, 0)
            };
            opening.MeshAnchors = corners.Select(NearestVertex).ToList();
            opening.ElementIds = FindElementsForRect(OpeningBounds(opening));
        }

        private List<int> FindElementsForRect(Rect rect) => _plates
            .Where(plate => PlateIntersectsRect(plate, rect))
            .Select(plate => plate.Id).Distinct().ToList();

        private static bool PlateIntersectsRect(LiraPlateElement plate, Rect rect)
        {
            var contour = plate.Contour;
            if (contour == null || contour.Count < 3) return false;
            if (contour.Max(point => point.X) < rect.Left || contour.Min(point => point.X) > rect.Right ||
                contour.Max(point => point.Y) < rect.Top || contour.Min(point => point.Y) > rect.Bottom) return false;
            if (contour.Any(point => rect.Contains(point.X, point.Y))) return true;
            if (PointInPolygon(new Point(rect.Left, rect.Top), contour) ||
                PointInPolygon(new Point(rect.Right, rect.Top), contour) ||
                PointInPolygon(new Point(rect.Right, rect.Bottom), contour) ||
                PointInPolygon(new Point(rect.Left, rect.Bottom), contour)) return true;
            for (var i = 0; i < contour.Count; i++)
            {
                var a = contour[i]; var b = contour[(i + 1) % contour.Count];
                if (SegmentIntersectsRect(a, b, rect)) return true;
            }
            return false;
        }

        private static bool SegmentIntersectsRect(Point3 a, Point3 b, Rect rect)
        {
            var c1 = new Point(rect.Left, rect.Top); var c2 = new Point(rect.Right, rect.Top);
            var c3 = new Point(rect.Right, rect.Bottom); var c4 = new Point(rect.Left, rect.Bottom);
            return SegmentsIntersect(a, b, c1, c2) || SegmentsIntersect(a, b, c2, c3) ||
                   SegmentsIntersect(a, b, c3, c4) || SegmentsIntersect(a, b, c4, c1);
        }

        private static bool SegmentsIntersect(Point3 a, Point3 b, Point c, Point d)
        {
            double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
            var rX = b.X - a.X; var rY = b.Y - a.Y;
            var sX = d.X - c.X; var sY = d.Y - c.Y;
            var denom = Cross(rX, rY, sX, sY);
            if (Math.Abs(denom) < 1e-12) return false;
            var qX = c.X - a.X; var qY = c.Y - a.Y;
            var t = Cross(qX, qY, sX, sY) / denom;
            var u = Cross(qX, qY, rX, rY) / denom;
            return t >= -1e-9 && t <= 1 + 1e-9 && u >= -1e-9 && u <= 1 + 1e-9;
        }

        private static bool PointInPolygon(Point point, IList<Point3> polygon)
        {
            var inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i]; var b = polygon[j];
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y + 1e-30) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        private void DrawOpenings(DrawingContext dc)
        {
            for (var i = 0; i < _openings.Count; i++)
            {
                var opening = _openings[i];
                var rect = OpeningBounds(opening);
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                var paletteIndex = Math.Abs(opening.OpeningId - 1) % OpeningHatchPalette.Length;
                var palette = OpeningHatchPalette[paletteIndex];
                var fill = new SolidColorBrush(Color.FromArgb(220,
                    palette.Fill.R, palette.Fill.G, palette.Fill.B));
                var hatchPen = new Pen(new SolidColorBrush(Color.FromArgb(220,
                    palette.Stroke.R, palette.Stroke.G, palette.Stroke.B)),
                    1 / Math.Max(1, _pixelsPerMeter));
                fill.Freeze(); hatchPen.Freeze();
                var geometry = new RectangleGeometry(rect);
                dc.DrawGeometry(fill, null, geometry);
                dc.PushClip(geometry);
                var spacing = Math.Max(0.05, 14 / _pixelsPerMeter);
                for (var x = rect.Left - rect.Height; x < rect.Right; x += spacing)
                    dc.DrawLine(hatchPen, new Point(x, rect.Top), new Point(x + rect.Height, rect.Bottom));
                dc.Pop();
                var outline = new Pen(i == _selectedOpeningIndex ? Brushes.DodgerBlue : Brushes.SlateGray,
                    (i == _selectedOpeningIndex ? 2.2 : 1.3) / _pixelsPerMeter);
                outline.Freeze();
                dc.DrawGeometry(null, outline, geometry);
            }
        }

        private void DrawBoundaries(DrawingContext dc)
        {
            for (var i = 0; i < _boundaries.Count; i++)
            {
                var pen = new Pen(i == _selectedBoundaryIndex ? Brushes.Crimson : Brushes.DarkOrange,
                    (i == _selectedBoundaryIndex ? 3.3 : 2.4) / _pixelsPerMeter);
                pen.Freeze();
                foreach (var anchor in _boundaries[i].Edges)
                {
                    var edge = ResolveEdge(anchor);
                    if (edge.HasValue)
                        dc.DrawLine(pen, new Point(edge.Value.Start.X, edge.Value.Start.Y),
                            new Point(edge.Value.End.X, edge.Value.End.Y));
                }
            }
        }

        private void DrawOpeningHandles(DrawingContext dc)
        {
            if (_selectedOpeningIndex < 0 || _selectedOpeningIndex >= _openings.Count) return;
            var bounds = OpeningBounds(_openings[_selectedOpeningIndex]);
            var brush = Brushes.White;
            var pen = new Pen(Brushes.DodgerBlue, 1.5 / _pixelsPerMeter);
            pen.Freeze();
            foreach (var point in new[]
            {
                new Point(bounds.Left, bounds.Top), new Point(bounds.Right, bounds.Top),
                new Point(bounds.Right, bounds.Bottom), new Point(bounds.Left, bounds.Bottom)
            })
                dc.DrawEllipse(brush, pen, point, 7 / _pixelsPerMeter, 7 / _pixelsPerMeter);
        }

        private void DrawDragPreview(DrawingContext dc)
        {
            if (!_dragPreview.HasValue) return;
            var pen = new Pen(Brushes.DodgerBlue, 2 / _pixelsPerMeter) { DashStyle = DashStyles.Dash };
            pen.Freeze();
            dc.DrawRectangle(null, pen, _dragPreview.Value);
        }

        private Point3 SnapToVertex(Point3 point)
        {
            var closest = _vertices.OrderBy(vertex => Distance(point, vertex.Position)).FirstOrDefault();
            return closest == null ? Copy(point) : Copy(closest.Position);
        }

        private bool TrySnapToVertex(Point3 point, out Point3 snapped)
        {
            var closest = _vertices.OrderBy(vertex => Distance(point, vertex.Position)).FirstOrDefault();
            if (closest == null || Distance(point, closest.Position) > 12 / Math.Max(1, _pixelsPerMeter))
            {
                snapped = Copy(point);
                return false;
            }
            snapped = Copy(closest.Position);
            return true;
        }

        private MeshVertexAnchor NearestVertex(Point3 point)
        {
            var closest = _vertices.OrderBy(vertex => Distance(point, vertex.Position)).FirstOrDefault();
            return closest == null ? new MeshVertexAnchor { Position = Copy(point) } : new MeshVertexAnchor
            {
                ElementId = closest.Plate.Id,
                VertexIndex = closest.Index,
                Position = Copy(closest.Position)
            };
        }

        private (Point3 Start, Point3 End)? ResolveEdge(MeshEdgeAnchor anchor)
        {
            var plate = _plates.FirstOrDefault(item => item.Id == anchor.ElementId);
            if (plate != null && plate.Contour.Count >= 2)
            {
                var index = anchor.EdgeIndex % plate.Contour.Count;
                if (index < 0) index += plate.Contour.Count;
                return (plate.Contour[index], plate.Contour[(index + 1) % plate.Contour.Count]);
            }
            if (anchor.Start != null && anchor.End != null) return (anchor.Start, anchor.End);
            return null;
        }

        private static Rect OpeningBounds(OpeningInfo opening) => new Rect(
            Math.Min(opening.MinXM, opening.MaxXM), Math.Min(opening.MinYM, opening.MaxYM),
            Math.Abs(opening.MaxXM - opening.MinXM), Math.Abs(opening.MaxYM - opening.MinYM));

        private static Rect RectFrom(Point3 first, Point3 second) => new Rect(
            Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
            Math.Abs(second.X - first.X), Math.Abs(second.Y - first.Y));

        private static Geometry BuildPath(IList<Point3> points)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(points[0].X, points[0].Y), true, true);
                for (var i = 1; i < points.Count; i++) context.LineTo(new Point(points[i].X, points[i].Y), true, false);
            }
            geometry.Freeze();
            return geometry;
        }

        private static bool BoundsOverlap(IList<Point3> points, Rect rect) =>
            points.Max(point => point.X) >= rect.Left && points.Min(point => point.X) <= rect.Right &&
            points.Max(point => point.Y) >= rect.Top && points.Min(point => point.Y) <= rect.Bottom;

        private static double Distance(Point3 point, Point other) =>
            Math.Sqrt((point.X - other.X) * (point.X - other.X) + (point.Y - other.Y) * (point.Y - other.Y));

        private static double Distance(Point3 first, Point3 second) =>
            Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Y - second.Y) * (first.Y - second.Y));

        private static double DistanceToSegment(Point3 point, Point3 start, Point3 end)
        {
            var dx = end.X - start.X; var dy = end.Y - start.Y;
            var length2 = dx * dx + dy * dy;
            if (length2 < 1e-18) return Distance(point, new Point(start.X, start.Y));
            var t = Math.Max(0, Math.Min(1, ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / length2));
            return Distance(point, new Point(start.X + t * dx, start.Y + t * dy));
        }

        private static string EdgeKey(Point3 a, Point3 b)
        {
            var first = PointKey(a); var second = PointKey(b);
            return string.CompareOrdinal(first, second) <= 0 ? first + "|" + second : second + "|" + first;
        }

        private static string PointKey(Point3 point) =>
            $"{Math.Round(point.X * 1000000):0},{Math.Round(point.Y * 1000000):0},{Math.Round(point.Z * 1000000):0}";

        private static MeshEdgeAnchor Clone(MeshEdgeAnchor edge) => new MeshEdgeAnchor
        {
            ElementId = edge.ElementId,
            EdgeIndex = edge.EdgeIndex,
            Start = Copy(edge.Start),
            End = Copy(edge.End)
        };

        private static Point3 Copy(Point3 point) => new Point3(point.X, point.Y, point.Z);
    }
}
