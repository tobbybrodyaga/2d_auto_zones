using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Clipper2Lib;

namespace LiraSlabZones.Core
{
    public enum ZoneIssueKind
    {
        UncoveredElement,
        EmptyZone,
        InvalidZone,
        OverlongBar,
        PlacementConflict,
        InterZoneStep,
        OpeningIntersection,
        OutsideSlab
    }

    public sealed class ZoneIssue
    {
        public ZoneIssueKind Kind { get; set; }
        public RebarLayer Layer { get; set; }
        public int ZoneId { get; set; }
        public int RelatedZoneId { get; set; }
        public int ElementId { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public sealed class ZoneDiagnostics
    {
        public List<ZoneIssue> Issues { get; set; } = new List<ZoneIssue>();
        public int LayoutIterations { get; set; }
        public bool LayoutConverged { get; set; }
        public int UncoveredCount => Issues.Count(i => i.Kind == ZoneIssueKind.UncoveredElement);
        public int EmptyZoneCount => Issues.Count(i => i.Kind == ZoneIssueKind.EmptyZone);
        public int ConflictCount => Issues.Count(i => i.Kind == ZoneIssueKind.PlacementConflict ||
            i.Kind == ZoneIssueKind.InterZoneStep);
        public bool HasErrors => Issues.Count > 0;
    }

    public sealed class ZoneSpatialIndex
    {
        private readonly double _cellM;
        private readonly Dictionary<(int X, int Y), List<int>> _cells =
            new Dictionary<(int X, int Y), List<int>>();

        public ZoneSpatialIndex(double cellM = 2.0) => _cellM = Math.Max(0.1, cellM);

        public void Add(int index, double minX, double maxX, double minY, double maxY)
        {
            foreach (var key in Keys(minX, maxX, minY, maxY))
            {
                if (!_cells.TryGetValue(key, out var values))
                    _cells[key] = values = new List<int>();
                values.Add(index);
            }
        }

        public IEnumerable<int> Query(double minX, double maxX, double minY, double maxY) =>
            Keys(minX, maxX, minY, maxY)
                .Where(_cells.ContainsKey).SelectMany(key => _cells[key]).Distinct();

        private IEnumerable<(int X, int Y)> Keys(double minX, double maxX, double minY, double maxY)
        {
            var x0 = (int)Math.Floor(minX / _cellM);
            var x1 = (int)Math.Floor(maxX / _cellM);
            var y0 = (int)Math.Floor(minY / _cellM);
            var y1 = (int)Math.Floor(maxY / _cellM);
            for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                yield return (x, y);
        }
    }

    public static class ZoneLayoutDiagnostics
    {
        private const double DiagnosticGeometryScale = 1000000.0;
        private const double StepMatchToleranceM = 0.001;
        private const double OpeningOverlapToleranceM = 0.000001;

        public static ZoneDiagnostics Evaluate(
            IList<LiraPlateElement> plates, IList<AdditionalZone> zones, AnalysisSettings settings,
            int iterations = 0, bool converged = true) =>
            Evaluate(plates, zones, settings, iterations, converged, null, null);

        public static ZoneDiagnostics Evaluate(
            IList<LiraPlateElement> plates, IList<AdditionalZone> zones, AnalysisSettings settings,
            int iterations, bool converged, IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            var result = new ZoneDiagnostics { LayoutIterations = iterations, LayoutConverged = converged };
            var byLayer = zones.GroupBy(z => z.Layer).ToDictionary(g => g.Key, g => g.ToList());
            foreach (RebarLayer layer in Enum.GetValues(typeof(RebarLayer)))
            {
                if (!LayerEnabled(layer, settings)) continue;
                var background = Background(layer, settings);
                var layerZones = byLayer.TryGetValue(layer, out var found) ? found : new List<AdditionalZone>();
                foreach (var plate in plates.Where(p => p.Rebar.Ok &&
                    p.Rebar.Get(layer) - background > MosaicBuilder.PositiveResidualToleranceCm2PerM))
                {
                    var requiredAdditional = plate.Rebar.Get(layer) - background;
                    if (ZoneCoverageRules.CoversOrBridgesGap(
                            layerZones, plate, layer, requiredAdditional, slabOutline, openings,
                            allowOverlappingZones: true)) continue;
                    result.Issues.Add(new ZoneIssue { Kind = ZoneIssueKind.UncoveredElement,
                        Layer = layer, ElementId = plate.Id, Message = "КЭ не покрыт зоной" });
                }
            }

            var plateById = plates.ToDictionary(p => p.Id);
            for (var i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];
                if (!zone.IsValid)
                    result.Issues.Add(Issue(ZoneIssueKind.InvalidZone, zone, zone.Comment));
                if (RebarTables.ExceedsMaxBarLength(zone))
                    result.Issues.Add(Issue(ZoneIssueKind.OverlongBar, zone, "Полная длина больше 11700 мм"));
                var useful = zone.NodeIds.Any(id => plateById.TryGetValue(id, out var plate) &&
                    plate.Rebar.Ok && plate.Rebar.Get(zone.Layer) - Background(zone.Layer, settings) >
                    MosaicBuilder.PositiveResidualToleranceCm2PerM);
                if (!useful && zone.NodeIds.Count == 0)
                    useful = plates.Any(plate => plate.Rebar.Ok &&
                        plate.Rebar.Get(zone.Layer) - Background(zone.Layer, settings) >
                        MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                        ZoneCoverageRules.CoversOrBridgesGap(
                            new[] { zone }, plate, zone.Layer,
                            plate.Rebar.Get(zone.Layer) - Background(zone.Layer, settings),
                            slabOutline, openings, allowOverlappingZones: true));
                if (!useful)
                    result.Issues.Add(Issue(ZoneIssueKind.EmptyZone, zone, "Зона не покрывает расчётный КЭ"));
                if (openings != null && openings.Count > 0)
                {
                    var overlapWidthM = MaxOpeningOverlapWidthM(zone, openings);
                    if (overlapWidthM > 0.05 + OpeningOverlapToleranceM)
                        result.Issues.Add(Issue(ZoneIssueKind.OpeningIntersection, zone,
                            $"Заход в отверстие по ширине {overlapWidthM * 1000:0.#} мм; " +
                            "допустимо не более 50 мм"));
                }
                if (slabOutline != null && slabOutline.Count >= 3 &&
                    ExtendsOutsideSlab(zone, slabOutline))
                    result.Issues.Add(Issue(ZoneIssueKind.OutsideSlab, zone,
                        "Контур зоны выходит за границу плиты"));
            }

            var stepPairs = AddInterZoneStepIssues(zones, result,
                Math.Max(0, settings.GridCellMm) / 1000.0);
            var index = BuildIndex(zones);
            for (var i = 0; i < zones.Count; i++)
            {
                var bounds = Bounds(zones[i]);
                var padding = Math.Max(0.1, zones[i].BarStepMm / 1000.0);
                foreach (var j in index.Query(bounds.MinX - padding, bounds.MaxX + padding,
                             bounds.MinY - padding, bounds.MaxY + padding).Where(j => j > i))
                {
                    if (stepPairs.Contains((i, j))) continue;
                    if (!ZoneEditor.HasPlacementConflict(zones[i], zones[j])) continue;
                    result.Issues.Add(Issue(ZoneIssueKind.PlacementConflict, zones[i],
                        $"Конфликт с зоной №{zones[j].ZoneId}"));
                    result.Issues.Add(Issue(ZoneIssueKind.PlacementConflict, zones[j],
                        $"Конфликт с зоной №{zones[i].ZoneId}"));
                }
            }
            return result;
        }

        internal static HashSet<(int First, int Second)> AddInterZoneStepIssues(
            IList<AdditionalZone> zones, ZoneDiagnostics result, double mosaicCellM)
        {
            var pairs = new HashSet<(int First, int Second)>();
            var groups = zones.Select((zone, index) => (Zone: zone, Index: index))
                .Where(item => item.Zone.Contour != null && item.Zone.Contour.Count >= 3)
                .GroupBy(item => (item.Zone.Layer, item.Zone.Direction));

            foreach (var group in groups)
            {
                var zonesInGroup = group.ToList();
                var axialCoordinates = zonesInGroup
                    .SelectMany(item => item.Zone.Contour.Select(point =>
                        group.Key.Direction == ZoneDirection.X ? point.X : point.Y))
                    .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
                    .OrderBy(value => value)
                    .ToList();
                var breakpoints = new List<double>();
                foreach (var coordinate in axialCoordinates)
                {
                    if (breakpoints.Count == 0 ||
                        coordinate - breakpoints[breakpoints.Count - 1] > 1e-9)
                        breakpoints.Add(coordinate);
                }

                for (var slabIndex = 0; slabIndex + 1 < breakpoints.Count; slabIndex++)
                {
                    var slabMin = breakpoints[slabIndex];
                    var slabMax = breakpoints[slabIndex + 1];
                    var slabWidth = slabMax - slabMin;
                    if (slabWidth <= 1e-9) continue;

                    foreach (var fraction in new[] { 0.001, 0.5, 0.999 })
                    {
                        var axial = slabMin + slabWidth * fraction;
                        var intervals = new List<(int ZoneIndex, double Min, double Max)>();
                        foreach (var item in zonesInGroup)
                        {
                            foreach (var interval in CrossSectionIntervals(
                                         item.Zone, group.Key.Direction, axial))
                                intervals.Add((item.Index, interval.Min, interval.Max));
                        }
                        intervals = intervals.OrderBy(interval => interval.Min)
                            .ThenBy(interval => interval.Max)
                            .ThenBy(interval => interval.ZoneIndex)
                            .ToList();

                        for (var intervalIndex = 0; intervalIndex + 1 < intervals.Count;
                             intervalIndex++)
                        {
                            var firstInterval = intervals[intervalIndex];
                            var secondInterval = intervals[intervalIndex + 1];
                            if (firstInterval.ZoneIndex == secondInterval.ZoneIndex) continue;

                            var actualGapM = secondInterval.Min - firstInterval.Max;
                            // Overlap is reported by placement-conflict diagnostics. Only
                            // compare geometrically adjacent zones within one mesh cell.
                            if (actualGapM < -1e-6) continue;

                            var firstZone = zones[firstInterval.ZoneIndex];
                            var secondZone = zones[secondInterval.ZoneIndex];
                            var requiredGapM = Math.Min(firstZone.BarStepMm,
                                secondZone.BarStepMm) / 1000.0;
                            if (requiredGapM <= 0 ||
                                actualGapM > requiredGapM + mosaicCellM + StepMatchToleranceM ||
                                Math.Abs(actualGapM - requiredGapM) <= StepMatchToleranceM)
                                continue;

                            var pair = (Math.Min(firstInterval.ZoneIndex, secondInterval.ZoneIndex),
                                Math.Max(firstInterval.ZoneIndex, secondInterval.ZoneIndex));
                            if (!pairs.Add(pair)) continue;

                            var message = $"Зазор {actualGapM * 1000:0.#} мм; требуется " +
                                          $"{requiredGapM * 1000:0.#} мм до зоны №";
                            result.Issues.Add(Issue(ZoneIssueKind.InterZoneStep, firstZone,
                                message + secondZone.ZoneId, secondZone.ZoneId));
                            result.Issues.Add(Issue(ZoneIssueKind.InterZoneStep, secondZone,
                                message + firstZone.ZoneId, firstZone.ZoneId));
                        }
                    }
                }
            }

            return pairs;
        }

        private static List<(double Min, double Max)> CrossSectionIntervals(
            AdditionalZone zone, ZoneDirection direction, double axial)
        {
            var crossings = new List<double>();
            for (var i = 0; i < zone.Contour.Count; i++)
            {
                var first = zone.Contour[i];
                var second = zone.Contour[(i + 1) % zone.Contour.Count];
                var firstAxial = direction == ZoneDirection.X ? first.X : first.Y;
                var secondAxial = direction == ZoneDirection.X ? second.X : second.Y;
                if (!((firstAxial <= axial && axial < secondAxial) ||
                      (secondAxial <= axial && axial < firstAxial))) continue;

                var firstCross = direction == ZoneDirection.X ? first.Y : first.X;
                var secondCross = direction == ZoneDirection.X ? second.Y : second.X;
                var ratio = (axial - firstAxial) / (secondAxial - firstAxial);
                crossings.Add(firstCross + (secondCross - firstCross) * ratio);
            }

            crossings.Sort();
            var intervals = new List<(double Min, double Max)>();
            for (var i = 0; i + 1 < crossings.Count; i += 2)
                if (crossings[i + 1] - crossings[i] > 1e-9)
                    intervals.Add((crossings[i], crossings[i + 1]));
            return intervals;
        }

        private static bool ExtendsOutsideSlab(AdditionalZone zone, IList<Point3> slabOutline)
        {
            if (zone.Contour == null || zone.Contour.Count < 3) return false;
            var subject = new Paths64 { ToDiagnosticPath(zone.Contour) };
            var outline = new Paths64 { ToDiagnosticPath(slabOutline) };
            var outside = Clipper.Difference(subject, outline, FillRule.NonZero);
            var outsideArea = outside.Sum(path => Math.Abs(Clipper.Area(path)));
            var zoneArea = Math.Abs(Clipper.Area(subject[0]));
            return outsideArea > Math.Max(10.0, zoneArea * 1e-8);
        }

        private static Path64 ToDiagnosticPath(IEnumerable<Point3> points) => new Path64(
            points.Select(point => new Point64(
                (long)Math.Round(point.X * DiagnosticGeometryScale),
                (long)Math.Round(point.Y * DiagnosticGeometryScale))));

        private static double MaxOpeningOverlapWidthM(
            AdditionalZone zone, IList<OpeningInfo> openings)
        {
            if (zone.Contour == null || zone.Contour.Count < 3) return 0;

            var maxOverlapWidthM = 0.0;
            var zoneBounds = Bounds(zone);
            var zonePath = ToDiagnosticPath(zone.Contour);
            foreach (var opening in openings)
            {
                var minX = Math.Min(opening.MinXM, opening.MaxXM);
                var maxX = Math.Max(opening.MinXM, opening.MaxXM);
                var minY = Math.Min(opening.MinYM, opening.MaxYM);
                var maxY = Math.Max(opening.MinYM, opening.MaxYM);
                if (maxX - minX <= 1e-9 || maxY - minY <= 1e-9) continue;
                if (zoneBounds.MaxX < minX - 1e-9 || zoneBounds.MinX > maxX + 1e-9 ||
                    zoneBounds.MaxY < minY - 1e-9 || zoneBounds.MinY > maxY + 1e-9)
                    continue;

                var openingPath = new Path64
                {
                    new Point64((long)Math.Round(minX * DiagnosticGeometryScale),
                        (long)Math.Round(minY * DiagnosticGeometryScale)),
                    new Point64((long)Math.Round(maxX * DiagnosticGeometryScale),
                        (long)Math.Round(minY * DiagnosticGeometryScale)),
                    new Point64((long)Math.Round(maxX * DiagnosticGeometryScale),
                        (long)Math.Round(maxY * DiagnosticGeometryScale)),
                    new Point64((long)Math.Round(minX * DiagnosticGeometryScale),
                        (long)Math.Round(maxY * DiagnosticGeometryScale))
                };
                var intersection = Clipper.Intersect(
                    new Paths64 { new Path64(zonePath) },
                    new Paths64 { openingPath }, FillRule.NonZero);
                foreach (var path in intersection)
                {
                    if (path.Count < 3 || Math.Abs(Clipper.Area(path)) <= 0.5) continue;
                    var overlapWidth = zone.Direction == ZoneDirection.X
                        ? path.Max(point => point.Y) - path.Min(point => point.Y)
                        : path.Max(point => point.X) - path.Min(point => point.X);
                    maxOverlapWidthM = Math.Max(maxOverlapWidthM,
                        overlapWidth / DiagnosticGeometryScale);
                }
            }
            return maxOverlapWidthM;
        }

        public static ZoneDiagnostics Evaluate(
            IList<LiraPlateElement> plates, IList<AdditionalZone> zones, AnalysisSettings settings,
            int iterations, bool converged, IList<Point3>? slabOutline) =>
            Evaluate(plates, zones, settings, iterations, converged, slabOutline, null);

        public static ZoneSpatialIndex BuildIndex(IList<AdditionalZone> zones)
        {
            var index = new ZoneSpatialIndex();
            for (var i = 0; i < zones.Count; i++)
            {
                var b = Bounds(zones[i]);
                index.Add(i, b.MinX, b.MaxX, b.MinY, b.MaxY);
            }
            return index;
        }

        private static ZoneIssue Issue(ZoneIssueKind kind, AdditionalZone zone, string message,
            int relatedZoneId = 0) => new ZoneIssue
        {
            Kind = kind,
            Layer = zone.Layer,
            ZoneId = zone.ZoneId,
            RelatedZoneId = relatedZoneId,
            Message = message ?? string.Empty
        };

        private static bool Covers(AdditionalZone zone, Point3 point) => zone.Contour.Count >= 3 &&
            point.X >= zone.Contour.Min(p => p.X) - 1e-6 && point.X <= zone.Contour.Max(p => p.X) + 1e-6 &&
            point.Y >= zone.Contour.Min(p => p.Y) - 1e-6 && point.Y <= zone.Contour.Max(p => p.Y) + 1e-6;

        private static (double MinX, double MaxX, double MinY, double MaxY) Bounds(AdditionalZone zone) =>
            (zone.Contour.Min(p => p.X), zone.Contour.Max(p => p.X),
             zone.Contour.Min(p => p.Y), zone.Contour.Max(p => p.Y));

        private static bool LayerEnabled(RebarLayer layer, AnalysisSettings s) => layer switch
        { RebarLayer.As1 => s.ShowAs1, RebarLayer.As2 => s.ShowAs2,
          RebarLayer.As3 => s.ShowAs3, RebarLayer.As4 => s.ShowAs4, _ => false };

        private static double Background(RebarLayer layer, AnalysisSettings s) => layer switch
        { RebarLayer.As1 => s.AsMainAs1, RebarLayer.As2 => s.AsMainAs2,
          RebarLayer.As3 => s.AsMainAs3, RebarLayer.As4 => s.AsMainAs4, _ => 0 };
    }

    public static class ZoneCoverageRules
    {
        private const double GeometryScale = 1000000.0;
        private const double OpeningCoverageAllowanceMm = 100;
        private sealed class CachedFootprint
        {
            public IList<Point3>? Contour;
            public IList<Point3>? SlabOutline;
            public Paths64 Paths = new Paths64();
            public bool IsValid;
            public bool UsePointFallback = true;
            public (double MinX, double MaxX, double MinY, double MaxY) Bounds;
        }

        private sealed class CachedOutlinePath
        {
            public CachedOutlinePath(Path64 path) => Path = path;
            public Path64 Path { get; }
        }

        private sealed class CachedOpeningBuffer
        {
            public double MinX;
            public double MaxX;
            public double MinY;
            public double MaxY;
            public Paths64 Paths = new Paths64();
        }

        private sealed class CachedZoneOpeningBuffers
        {
            public IList<Point3>? Contour;
            public IList<OpeningInfo>? Openings;
            public double[] ContourCoordinates = Array.Empty<double>();
            public double[] OpeningCoordinates = Array.Empty<double>();
            public Paths64 Paths = new Paths64();
        }

        private static readonly ConditionalWeakTable<LiraPlateElement, CachedFootprint> FootprintCache =
            new ConditionalWeakTable<LiraPlateElement, CachedFootprint>();
        private static readonly ConditionalWeakTable<IList<Point3>, CachedOutlinePath> OutlinePathCache =
            new ConditionalWeakTable<IList<Point3>, CachedOutlinePath>();
        private static readonly ConditionalWeakTable<OpeningInfo, CachedOpeningBuffer> OpeningBufferCache =
            new ConditionalWeakTable<OpeningInfo, CachedOpeningBuffer>();
        private static readonly ConditionalWeakTable<AdditionalZone, CachedZoneOpeningBuffers> ZoneOpeningBufferCache =
            new ConditionalWeakTable<AdditionalZone, CachedZoneOpeningBuffers>();

        public static bool IntersectsElementFootprint(
            AdditionalZone zone, LiraPlateElement plate, IList<Point3>? slabOutline = null)
        {
            if (zone?.Contour == null || zone.Contour.Count < 3 || plate == null) return false;
            var footprint = GetFootprint(plate, slabOutline);
            if (footprint.UsePointFallback) return DirectlyCovers(zone, plate.Centroid);
            if (!footprint.IsValid) return false;

            var zoneBounds = Bounds(zone);
            if (zoneBounds.MaxX <= footprint.Bounds.MinX ||
                zoneBounds.MinX >= footprint.Bounds.MaxX ||
                zoneBounds.MaxY <= footprint.Bounds.MinY ||
                zoneBounds.MinY >= footprint.Bounds.MaxY)
                return false;

            var overlap = Clipper.Intersect(
                new Paths64 { ToPath(zone.Contour) }, ClonePaths(footprint.Paths), FillRule.NonZero);
            return overlap.Sum(path => Math.Abs(Clipper.Area(path))) > 0.5;
        }

        public static bool CoversOrBridgesGap(
            IList<AdditionalZone> zones, Point3 point, double requiredAs = 0)
        {
            if (zones.Any(zone => zone.AsCoveredCm2PerM + 1e-6 >= requiredAs &&
                                  DirectlyCovers(zone, point))) return true;
            for (var i = 0; i < zones.Count; i++)
            for (var j = i + 1; j < zones.Count; j++)
            {
                var first = zones[i];
                var second = zones[j];
                if (first.Layer != second.Layer || first.Direction != second.Direction ||
                    first.Contour.Count < 3 || second.Contour.Count < 3)
                    continue;
                var weakerCapacity = Math.Min(first.AsCoveredCm2PerM, second.AsCoveredCm2PerM);
                if (weakerCapacity + 1e-6 < requiredAs) continue;
                var a = Bounds(first);
                var b = Bounds(second);
                var maximumGapM = Math.Min(first.BarStepMm, second.BarStepMm) / 1000.0;
                const double boundaryToleranceM = 0.01;
                if (first.Direction == ZoneDirection.X)
                {
                    var overlapMin = Math.Max(a.MinX, b.MinX);
                    var overlapMax = Math.Min(a.MaxX, b.MaxX);
                    var lower = a.MinY <= b.MinY ? a : b;
                    var upper = a.MinY <= b.MinY ? b : a;
                    var gap = upper.MinY - lower.MaxY;
                    if (gap >= -1e-6 && gap <= maximumGapM + 1e-6 &&
                        point.X >= overlapMin - boundaryToleranceM &&
                        point.X <= overlapMax + boundaryToleranceM &&
                        point.Y >= lower.MaxY - boundaryToleranceM &&
                        point.Y <= upper.MinY + boundaryToleranceM)
                        return true;

                    var longitudinalLower = a.MinX <= b.MinX ? a : b;
                    var longitudinalUpper = a.MinX <= b.MinX ? b : a;
                    var longitudinalGap = longitudinalUpper.MinX - longitudinalLower.MaxX;
                    var transverseOverlapMin = Math.Max(a.MinY, b.MinY);
                    var transverseOverlapMax = Math.Min(a.MaxY, b.MaxY);
                    if (longitudinalGap >= -1e-6 && longitudinalGap <= maximumGapM + 1e-6 &&
                        point.X >= longitudinalLower.MaxX - boundaryToleranceM &&
                        point.X <= longitudinalUpper.MinX + boundaryToleranceM &&
                        point.Y >= transverseOverlapMin - boundaryToleranceM &&
                        point.Y <= transverseOverlapMax + boundaryToleranceM)
                        return true;
                }
                else
                {
                    var overlapMin = Math.Max(a.MinY, b.MinY);
                    var overlapMax = Math.Min(a.MaxY, b.MaxY);
                    var left = a.MinX <= b.MinX ? a : b;
                    var right = a.MinX <= b.MinX ? b : a;
                    var gap = right.MinX - left.MaxX;
                    if (gap >= -1e-6 && gap <= maximumGapM + 1e-6 &&
                        point.Y >= overlapMin - boundaryToleranceM &&
                        point.Y <= overlapMax + boundaryToleranceM &&
                        point.X >= left.MaxX - boundaryToleranceM &&
                        point.X <= right.MinX + boundaryToleranceM)
                        return true;

                    var longitudinalLower = a.MinY <= b.MinY ? a : b;
                    var longitudinalUpper = a.MinY <= b.MinY ? b : a;
                    var longitudinalGap = longitudinalUpper.MinY - longitudinalLower.MaxY;
                    var transverseOverlapMin = Math.Max(a.MinX, b.MinX);
                    var transverseOverlapMax = Math.Min(a.MaxX, b.MaxX);
                    if (longitudinalGap >= -1e-6 && longitudinalGap <= maximumGapM + 1e-6 &&
                        point.Y >= longitudinalLower.MaxY - boundaryToleranceM &&
                        point.Y <= longitudinalUpper.MinY + boundaryToleranceM &&
                        point.X >= transverseOverlapMin - boundaryToleranceM &&
                        point.X <= transverseOverlapMax + boundaryToleranceM)
                        return true;
                }
            }
            return false;
        }

        public static bool CoversOrBridgesGap(
            IList<AdditionalZone> zones, LiraPlateElement plate, RebarLayer layer,
            double requiredAs = 0) =>
            CoversOrBridgesGap(zones, plate, layer, requiredAs, null, null);

        public static bool CoversOrBridgesGap(
            IList<AdditionalZone> zones, LiraPlateElement plate, RebarLayer layer,
            double requiredAs, IList<Point3>? slabOutline) =>
            CoversOrBridgesGap(zones, plate, layer, requiredAs, slabOutline, null);

        public static bool CoversOrBridgesGap(
            IList<AdditionalZone> zones, LiraPlateElement plate, RebarLayer layer,
            double requiredAs, IList<Point3>? slabOutline, IList<OpeningInfo>? openings)
            => CoversOrBridgesGap(zones, plate, layer, requiredAs, slabOutline, openings,
                allowOverlappingZones: false);

        public static bool CoversOrBridgesGap(
            IList<AdditionalZone> zones, LiraPlateElement plate, RebarLayer layer,
            double requiredAs, IList<Point3>? slabOutline, IList<OpeningInfo>? openings,
            bool allowOverlappingZones)
        {
            var footprint = GetFootprint(plate, slabOutline);
            if (footprint.UsePointFallback)
                return CoversOrBridgesGap(zones.Where(zone => zone.Layer == layer).ToList(),
                    plate.Centroid, requiredAs);
            if (!footprint.IsValid) return false;

            var eligible = zones.Where(zone => zone.Layer == layer &&
                zone.AsCoveredCm2PerM + 1e-6 >= requiredAs && zone.Contour.Count >= 3).ToList();
            var searchPadding = eligible.Count == 0
                ? 0.05
                : Math.Max(0.05, eligible.Max(zone => zone.BarStepMm) / 1000.0);
            var nearby = eligible.Where(zone =>
            {
                var bounds = Bounds(zone);
                return bounds.MaxX >= footprint.Bounds.MinX - searchPadding &&
                       bounds.MinX <= footprint.Bounds.MaxX + searchPadding &&
                       bounds.MaxY >= footprint.Bounds.MinY - searchPadding &&
                       bounds.MinY <= footprint.Bounds.MaxY + searchPadding;
            }).ToList();
            foreach (var zone in nearby)
            {
                var bounds = Bounds(zone);
                var zonePaths = new Paths64 { ToPath(zone.Contour) };
                if (ContainsBounds(bounds, footprint.Bounds) &&
                    (IsAxisAlignedRectangle(zone.Contour) ||
                     ContainsFootprint(footprint.Paths, zonePaths)))
                    return true;
                if (ContainsFootprintNearOpening(
                        footprint, zonePaths, GetZoneOpeningBuffers(zone, openings)))
                    return true;
            }

            Paths64? combinedZonePaths = null;
            Paths64? combinedOpeningBuffers = null;
            if (nearby.Count > 1)
            {
                var zonePaths = new Paths64();
                var openingBuffers = new Paths64();
                foreach (var zone in nearby)
                {
                    zonePaths.Add(ToPath(zone.Contour));
                    if (openings != null && openings.Count > 0)
                        openingBuffers.AddRange(ClonePaths(GetZoneOpeningBuffers(zone, openings)));
                }

                var hasPositiveAreaOverlap = HasPositiveAreaOverlap(nearby);
                var canUseCombinedCoverage = allowOverlappingZones || !hasPositiveAreaOverlap;
                combinedZonePaths = canUseCombinedCoverage ? zonePaths : null;
                combinedOpeningBuffers = canUseCombinedCoverage ? openingBuffers : null;
                var combinedCoverage = Clipper.Union(zonePaths, FillRule.NonZero);
                if (canUseCombinedCoverage &&
                    (ContainsFootprint(footprint.Paths, combinedCoverage) ||
                     ContainsFootprintNearOpening(footprint, combinedCoverage, openingBuffers)))
                    return true;
            }

            for (var i = 0; i < nearby.Count; i++)
            for (var j = i + 1; j < nearby.Count; j++)
            {
                var first = nearby[i];
                var second = nearby[j];
                if (first.Direction != second.Direction ||
                    Math.Min(first.AsCoveredCm2PerM, second.AsCoveredCm2PerM) + 1e-6 < requiredAs)
                    continue;

                var a = Bounds(first);
                var b = Bounds(second);
                if (Math.Max(a.MaxX, b.MaxX) < footprint.Bounds.MinX - 1e-6 ||
                    Math.Min(a.MinX, b.MinX) > footprint.Bounds.MaxX + 1e-6 ||
                    Math.Max(a.MaxY, b.MaxY) < footprint.Bounds.MinY - 1e-6 ||
                    Math.Min(a.MinY, b.MinY) > footprint.Bounds.MaxY + 1e-6)
                    continue;

                if (ZonesTouchOrOverlap(first, second) &&
                    (allowOverlappingZones || !ZonesHavePositiveAreaOverlap(first, second)))
                {
                    var pairCoverage = Clipper.Union(
                        new Paths64 { ToPath(first.Contour), ToPath(second.Contour) }, FillRule.NonZero);
                    if (ContainsFootprint(footprint.Paths, pairCoverage) ||
                        ContainsFootprintNearOpening(footprint, pairCoverage,
                            MergeZoneOpeningBuffers(first, second, openings))) return true;
                }

                foreach (var bridge in GapBridges(first, second))
                {
                    combinedZonePaths?.Add(new Path64(bridge));
                    var coverage = Clipper.Union(
                        new Paths64 { ToPath(first.Contour), ToPath(second.Contour), bridge },
                        FillRule.NonZero);
                    if (ContainsFootprint(footprint.Paths, coverage) ||
                        ContainsFootprintNearOpening(footprint, coverage,
                            MergeZoneOpeningBuffers(first, second, openings))) return true;
                }
            }

            if (combinedZonePaths != null)
            {
                var bridgedCoverage = Clipper.Union(combinedZonePaths, FillRule.NonZero);
                if (ContainsFootprint(footprint.Paths, bridgedCoverage) ||
                    ContainsFootprintNearOpening(footprint, bridgedCoverage, combinedOpeningBuffers!))
                    return true;
            }
            return false;
        }

        private static CachedFootprint GetFootprint(LiraPlateElement plate, IList<Point3>? slabOutline)
        {
            var cached = FootprintCache.GetValue(plate, _ => new CachedFootprint());
            lock (cached)
            {
                if (ReferenceEquals(cached.Contour, plate.Contour) &&
                    ReferenceEquals(cached.SlabOutline, slabOutline)) return cached;

                cached.Contour = plate.Contour;
                cached.SlabOutline = slabOutline;
                cached.Paths = new Paths64();
                cached.IsValid = false;
                cached.UsePointFallback = true;
                if (plate.Contour == null || plate.Contour.Count < 3) return cached;

                var platePath = ToPath(plate.Contour);
                if (platePath.Count < 3 || Math.Abs(Clipper.Area(platePath)) < 0.5) return cached;
                cached.UsePointFallback = false;
                if (slabOutline != null && slabOutline.Count >= 3)
                {
                    var outlinePath = OutlinePathCache.GetValue(
                        slabOutline, points => new CachedOutlinePath(ToPath(points))).Path;
                    cached.Paths = Clipper.Intersect(
                        new Paths64 { platePath }, new Paths64 { outlinePath }, FillRule.NonZero);
                }
                else
                {
                    cached.Paths.Add(platePath);
                }

                var clippedPaths = cached.Paths;
                cached.Paths = new Paths64();
                long minX = long.MaxValue, maxX = long.MinValue;
                long minY = long.MaxValue, maxY = long.MinValue;
                foreach (var path in clippedPaths)
                {
                    if (path.Count < 3 || Math.Abs(Clipper.Area(path)) < 0.5) continue;
                    cached.Paths.Add(path);
                    cached.IsValid = true;
                    foreach (var point in path)
                    {
                        if (point.X < minX) minX = point.X;
                        if (point.X > maxX) maxX = point.X;
                        if (point.Y < minY) minY = point.Y;
                        if (point.Y > maxY) maxY = point.Y;
                    }
                }
                if (cached.IsValid)
                    cached.Bounds = (minX / GeometryScale, maxX / GeometryScale,
                        minY / GeometryScale, maxY / GeometryScale);
                return cached;
            }
        }

        private static bool IsAxisAlignedRectangle(IList<Point3> contour)
        {
            if (contour.Count != 4) return false;
            var bounds = Bounds(contour);
            if (bounds.MaxX - bounds.MinX <= 1e-9 || bounds.MaxY - bounds.MinY <= 1e-9)
                return false;

            var corners = 0;
            for (var i = 0; i < contour.Count; i++)
            {
                var point = contour[i];
                var next = contour[(i + 1) % contour.Count];
                if (Math.Abs(point.X - next.X) > 1e-9 && Math.Abs(point.Y - next.Y) > 1e-9)
                    return false;
                if (Math.Abs(point.X - bounds.MinX) <= 1e-9 && Math.Abs(point.Y - bounds.MinY) <= 1e-9)
                    corners |= 1;
                else if (Math.Abs(point.X - bounds.MaxX) <= 1e-9 && Math.Abs(point.Y - bounds.MinY) <= 1e-9)
                    corners |= 2;
                else if (Math.Abs(point.X - bounds.MaxX) <= 1e-9 && Math.Abs(point.Y - bounds.MaxY) <= 1e-9)
                    corners |= 4;
                else if (Math.Abs(point.X - bounds.MinX) <= 1e-9 && Math.Abs(point.Y - bounds.MaxY) <= 1e-9)
                    corners |= 8;
                else return false;
            }
            return corners == 15;
        }

        private static IEnumerable<Path64> GapBridges(AdditionalZone first, AdditionalZone second)
        {
            var a = Bounds(first);
            var b = Bounds(second);
            var maxGap = Math.Min(first.BarStepMm, second.BarStepMm) / 1000.0;

            void AddBridge(double minX, double maxX, double minY, double maxY, List<Path64> result)
            {
                if (maxX - minX <= 1e-6 || maxY - minY <= 1e-6) return;
                result.Add(RectanglePath(minX, maxX, minY, maxY));
            }

            var bridges = new List<Path64>();
            if (first.Direction == ZoneDirection.X)
            {
                var commonXMin = Math.Max(a.MinX, b.MinX);
                var commonXMax = Math.Min(a.MaxX, b.MaxX);
                var lower = a.MinY <= b.MinY ? a : b;
                var upper = a.MinY <= b.MinY ? b : a;
                var crossGap = upper.MinY - lower.MaxY;
                if (commonXMax - commonXMin > 1e-6 && crossGap > 1e-6 && crossGap <= maxGap + 1e-6)
                    AddBridge(commonXMin, commonXMax, lower.MaxY, upper.MinY, bridges);

                var commonYMin = Math.Max(a.MinY, b.MinY);
                var commonYMax = Math.Min(a.MaxY, b.MaxY);
                var left = a.MinX <= b.MinX ? a : b;
                var right = a.MinX <= b.MinX ? b : a;
                var endGap = right.MinX - left.MaxX;
                if (commonYMax - commonYMin > 1e-6 && endGap > 1e-6 && endGap <= maxGap + 1e-6)
                    AddBridge(left.MaxX, right.MinX, commonYMin, commonYMax, bridges);
            }
            else
            {
                var commonYMin = Math.Max(a.MinY, b.MinY);
                var commonYMax = Math.Min(a.MaxY, b.MaxY);
                var left = a.MinX <= b.MinX ? a : b;
                var right = a.MinX <= b.MinX ? b : a;
                var crossGap = right.MinX - left.MaxX;
                if (commonYMax - commonYMin > 1e-6 && crossGap > 1e-6 && crossGap <= maxGap + 1e-6)
                    AddBridge(left.MaxX, right.MinX, commonYMin, commonYMax, bridges);

                var commonXMin = Math.Max(a.MinX, b.MinX);
                var commonXMax = Math.Min(a.MaxX, b.MaxX);
                var lower = a.MinY <= b.MinY ? a : b;
                var upper = a.MinY <= b.MinY ? b : a;
                var endGap = upper.MinY - lower.MaxY;
                if (commonXMax - commonXMin > 1e-6 && endGap > 1e-6 && endGap <= maxGap + 1e-6)
                    AddBridge(commonXMin, commonXMax, lower.MaxY, upper.MinY, bridges);
            }
            return bridges;
        }

        private static bool ContainsPolygon(Path64 subject, Paths64 coverage)
        {
            if (coverage.Count == 0) return false;
            var missing = Clipper.Difference(new Paths64 { new Path64(subject) }, coverage, FillRule.NonZero);
            var missingArea = missing.Sum(path => Math.Abs(Clipper.Area(path)));
            var subjectArea = Math.Abs(Clipper.Area(subject));
            return missingArea <= Math.Max(10.0, subjectArea * 1e-8);
        }

        private static bool ContainsFootprint(Paths64 footprint, Paths64 coverage) =>
            footprint.Count > 0 && footprint.All(path => ContainsPolygon(path, coverage));

        private static bool ZonesTouchOrOverlap(AdditionalZone first, AdditionalZone second)
        {
            var a = Bounds(first);
            var b = Bounds(second);
            var overlapX = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            var overlapY = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            return overlapX >= -1e-6 && overlapY >= -1e-6 &&
                   (overlapX > 1e-6 || overlapY > 1e-6);
        }

        private static bool HasPositiveAreaOverlap(IList<AdditionalZone> zones)
        {
            for (var i = 0; i < zones.Count; i++)
            for (var j = i + 1; j < zones.Count; j++)
                if (ZonesHavePositiveAreaOverlap(zones[i], zones[j])) return true;
            return false;
        }

        private static bool ZonesHavePositiveAreaOverlap(AdditionalZone first, AdditionalZone second)
        {
            var overlap = Clipper.Intersect(
                new Paths64 { ToPath(first.Contour) },
                new Paths64 { ToPath(second.Contour) }, FillRule.NonZero);
            return overlap.Sum(path => Math.Abs(Clipper.Area(path))) > 0.5;
        }

        private static bool ContainsFootprintNearOpening(
            CachedFootprint footprint, Paths64 coverage, Paths64 qualifyingBuffers)
        {
            if (footprint.Paths.Count == 0 || coverage.Count == 0 || qualifyingBuffers.Count == 0)
                return false;

            var actualCoverage = Clipper.Intersect(
                ClonePaths(footprint.Paths), ClonePaths(coverage), FillRule.NonZero);
            if (actualCoverage.Sum(path => Math.Abs(Clipper.Area(path))) <= 0.5) return false;

            var missing = Clipper.Difference(
                ClonePaths(footprint.Paths), ClonePaths(coverage), FillRule.NonZero);
            if (missing.Count == 0) return true;
            var outsideOpeningAllowance = Clipper.Difference(
                missing, qualifyingBuffers, FillRule.NonZero);
            var outsideArea = outsideOpeningAllowance.Sum(path => Math.Abs(Clipper.Area(path)));
            var footprintArea = footprint.Paths.Sum(path => Math.Abs(Clipper.Area(path)));
            return outsideArea <= Math.Max(10.0, footprintArea * 1e-8);
        }

        private static Paths64 GetZoneOpeningBuffers(
            AdditionalZone zone, IList<OpeningInfo>? openings)
        {
            var cached = ZoneOpeningBufferCache.GetValue(zone, _ => new CachedZoneOpeningBuffers());
            lock (cached)
            {
                if (SameZoneOpeningInputs(cached, zone, openings)) return cached.Paths;

                cached.Contour = zone.Contour;
                cached.Openings = openings;
                cached.ContourCoordinates = zone.Contour.SelectMany(point =>
                    new[] { point.X, point.Y, point.Z }).ToArray();
                cached.OpeningCoordinates = openings == null
                    ? Array.Empty<double>()
                    : openings.SelectMany(opening => new[]
                        { opening.MinXM, opening.MaxXM, opening.MinYM, opening.MaxYM }).ToArray();
                cached.Paths = new Paths64();
                if (openings == null || openings.Count == 0 || zone.Contour.Count < 3)
                    return cached.Paths;

                var zoneBounds = Bounds(zone);
                var zonePath = ToPath(zone.Contour);
                foreach (var opening in openings)
                {
                    var allowanceM = OpeningCoverageAllowanceMm / 1000.0 + 0.00001;
                    var openingMinX = Math.Min(opening.MinXM, opening.MaxXM) - allowanceM;
                    var openingMaxX = Math.Max(opening.MinXM, opening.MaxXM) + allowanceM;
                    var openingMinY = Math.Min(opening.MinYM, opening.MaxYM) - allowanceM;
                    var openingMaxY = Math.Max(opening.MinYM, opening.MaxYM) + allowanceM;
                    if (zoneBounds.MaxX < openingMinX || zoneBounds.MinX > openingMaxX ||
                        zoneBounds.MaxY < openingMinY || zoneBounds.MinY > openingMaxY)
                        continue;

                    var buffer = GetOpeningBuffer(opening);
                    if (buffer.Count == 0) continue;
                    var nearZone = Clipper.Intersect(new Paths64 { new Path64(zonePath) },
                        ClonePaths(buffer), FillRule.NonZero)
                        .Sum(path => Math.Abs(Clipper.Area(path))) > 0.5;
                    if (nearZone) cached.Paths.AddRange(ClonePaths(buffer));
                }
                return cached.Paths;
            }
        }

        private static Paths64 MergeZoneOpeningBuffers(
            AdditionalZone first, AdditionalZone second, IList<OpeningInfo>? openings)
        {
            var result = ClonePaths(GetZoneOpeningBuffers(first, openings));
            result.AddRange(ClonePaths(GetZoneOpeningBuffers(second, openings)));
            return result;
        }

        private static bool SameZoneOpeningInputs(
            CachedZoneOpeningBuffers cached, AdditionalZone zone, IList<OpeningInfo>? openings)
        {
            if (!ReferenceEquals(cached.Contour, zone.Contour) ||
                !ReferenceEquals(cached.Openings, openings) ||
                cached.ContourCoordinates.Length != zone.Contour.Count * 3 ||
                cached.OpeningCoordinates.Length != (openings?.Count ?? 0) * 4)
                return false;

            var coordinateIndex = 0;
            foreach (var point in zone.Contour)
            {
                if (cached.ContourCoordinates[coordinateIndex++] != point.X ||
                    cached.ContourCoordinates[coordinateIndex++] != point.Y ||
                    cached.ContourCoordinates[coordinateIndex++] != point.Z)
                    return false;
            }

            if (openings == null) return true;
            coordinateIndex = 0;
            foreach (var opening in openings)
            {
                if (cached.OpeningCoordinates[coordinateIndex++] != opening.MinXM ||
                    cached.OpeningCoordinates[coordinateIndex++] != opening.MaxXM ||
                    cached.OpeningCoordinates[coordinateIndex++] != opening.MinYM ||
                    cached.OpeningCoordinates[coordinateIndex++] != opening.MaxYM)
                    return false;
            }
            return true;
        }

        private static Paths64 GetOpeningBuffer(OpeningInfo opening)
        {
            var cached = OpeningBufferCache.GetValue(opening, _ => new CachedOpeningBuffer());
            lock (cached)
            {
                var minX = Math.Min(opening.MinXM, opening.MaxXM);
                var maxX = Math.Max(opening.MinXM, opening.MaxXM);
                var minY = Math.Min(opening.MinYM, opening.MaxYM);
                var maxY = Math.Max(opening.MinYM, opening.MaxYM);
                if (cached.Paths.Count > 0 && cached.MinX == minX && cached.MaxX == maxX &&
                    cached.MinY == minY && cached.MaxY == maxY)
                    return cached.Paths;

                cached.MinX = minX;
                cached.MaxX = maxX;
                cached.MinY = minY;
                cached.MaxY = maxY;
                if (maxX - minX <= 1e-9 || maxY - minY <= 1e-9)
                {
                    cached.Paths = new Paths64();
                    return cached.Paths;
                }
                cached.Paths = Clipper.InflatePaths(
                    new Paths64 { RectanglePath(minX, maxX, minY, maxY) },
                    (long)Math.Round(OpeningCoverageAllowanceMm * GeometryScale / 1000.0) + 10,
                    JoinType.Round, EndType.Polygon, 2.0, 0.0);
                return cached.Paths;
            }
        }

        private static Paths64 ClonePaths(Paths64 paths) =>
            new Paths64(paths.Select(path => new Path64(path)));

        private static Path64 ToPath(IEnumerable<Point3> points) => new Path64(points.Select(point =>
            new Point64((long)Math.Round(point.X * GeometryScale),
                (long)Math.Round(point.Y * GeometryScale))));

        private static Path64 RectanglePath(double minX, double maxX, double minY, double maxY) =>
            new Path64
            {
                new Point64((long)Math.Round(minX * GeometryScale), (long)Math.Round(minY * GeometryScale)),
                new Point64((long)Math.Round(maxX * GeometryScale), (long)Math.Round(minY * GeometryScale)),
                new Point64((long)Math.Round(maxX * GeometryScale), (long)Math.Round(maxY * GeometryScale)),
                new Point64((long)Math.Round(minX * GeometryScale), (long)Math.Round(maxY * GeometryScale))
            };

        private static bool ContainsBounds(
            (double MinX, double MaxX, double MinY, double MaxY) outer,
            (double MinX, double MaxX, double MinY, double MaxY) inner) =>
            inner.MinX >= outer.MinX - 1e-6 && inner.MaxX <= outer.MaxX + 1e-6 &&
            inner.MinY >= outer.MinY - 1e-6 && inner.MaxY <= outer.MaxY + 1e-6;

        private static (double MinX, double MaxX, double MinY, double MaxY) Bounds(
            IList<Point3> points) =>
            (points.Min(point => point.X), points.Max(point => point.X),
             points.Min(point => point.Y), points.Max(point => point.Y));

        private static bool DirectlyCovers(AdditionalZone zone, Point3 point)
        {
            if (zone.Contour.Count < 3) return false;
            var bounds = Bounds(zone);
            const double boundaryToleranceM = 0.01;
            return point.X >= bounds.MinX - boundaryToleranceM &&
                   point.X <= bounds.MaxX + boundaryToleranceM &&
                   point.Y >= bounds.MinY - boundaryToleranceM &&
                   point.Y <= bounds.MaxY + boundaryToleranceM;
        }

        private static (double MinX, double MaxX, double MinY, double MaxY) Bounds(AdditionalZone zone) =>
            (zone.Contour.Min(point => point.X), zone.Contour.Max(point => point.X),
             zone.Contour.Min(point => point.Y), zone.Contour.Max(point => point.Y));
    }
}
