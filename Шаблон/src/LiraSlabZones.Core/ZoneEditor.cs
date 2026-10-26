using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace LiraSlabZones.Core
{
    public static class ZoneEditor
    {
        private const double Scale = 1000000.0;

        public static bool Move(AdditionalZone zone, double dxM, double dyM, IList<Point3> slab,
            bool clipToSlab = true)
        {
            var moved = zone.Contour
                .Select(p => new Point3(p.X + dxM, p.Y + dyM, p.Z))
                .ToList();
            return ApplyEditedContour(zone, moved, slab, clipToSlab);
        }

        public static bool Resize(
            AdditionalZone zone, double minX, double maxX, double minY, double maxY,
            IList<Point3> slab, bool clipToSlab = true)
        {
            return ApplyEditedContour(zone, Rectangle(minX, maxX, minY, maxY, zone.LevelZM), slab, clipToSlab);
        }

        public static AdditionalZone? TrimToBounds(
            AdditionalZone source, double minX, double maxX, double minY, double maxY,
            IList<Point3> slab)
        {
            if (source.Contour.Count < 3 || maxX - minX <= 0.05 || maxY - minY <= 0.05)
                return null;
            var copy = Copy(source);
            var clipped = Clipper.Intersect(new Paths64 { ToPath(source.Contour) },
                new Paths64 { ToPath(Rectangle(minX, maxX, minY, maxY, source.LevelZM)) },
                FillRule.NonZero);
            clipped = IntersectWithSlab(clipped, slab ?? new List<Point3>());
            var path = Largest(clipped);
            if (path == null) return null;
            SetContour(copy, FromPath(path, copy.LevelZM));
            return copy.LengthM > 0.05 && copy.WidthM > 0.05 ? copy : null;
        }

        public static void SetDiameter(AdditionalZone zone, int diameterMm)
        {
            if (diameterMm <= 0) throw new ArgumentOutOfRangeException(nameof(diameterMm));
            zone.DiameterMm = diameterMm;
            zone.AsCoveredCm2PerM = BarCapacity.AsCm2PerM(diameterMm, Math.Max(1, zone.BarStepMm));
            zone.Comment = "диаметр изменён в предпросмотре";
        }

        public static void SetStep(AdditionalZone zone, int stepMm)
        {
            if (stepMm != 100 && stepMm != 200) throw new ArgumentOutOfRangeException(nameof(stepMm));
            zone.BarStepMm = stepMm;
            zone.BarCount = Math.Max(2, (int)Math.Ceiling(zone.WidthMm / stepMm) + 1);
            zone.AsCoveredCm2PerM = BarCapacity.AsCm2PerM(zone.DiameterMm, stepMm);
            zone.Comment = "шаг изменён в предпросмотре";
        }

        public static int RemoveZonesWithoutRequiredElements(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            AnalysisSettings? settings, IList<Point3>? slabOutline = null,
            IList<OpeningInfo>? openings = null)
        {
            if (zones == null || plates == null || plates.Count == 0) return 0;

            var requiredByLayer = zones.Select(zone => zone.Layer).Distinct().ToDictionary(layer => layer,
                layer =>
                {
                    var background = BackgroundAs(settings, layer);
                    return plates.Where(plate => plate.Rebar.Ok &&
                        plate.Rebar.Get(layer) - background >
                        MosaicBuilder.PositiveResidualToleranceCm2PerM).ToList();
                });
            var maximumGapByLayer = zones.GroupBy(zone => zone.Layer).ToDictionary(group => group.Key,
                group => Math.Max(0.05, group.Max(zone => Math.Max(0, zone.BarStepMm) / 1000.0)));
            var removed = 0;
            for (var i = zones.Count - 1; i >= 0; i--)
            {
                var zone = zones[i];
                if (zone.Contour == null || zone.Contour.Count < 3) continue;

                var background = BackgroundAs(settings, zone.Layer);
                var requiredPlates = requiredByLayer[zone.Layer];
                var supportingIds = new HashSet<int>();
                var maximumGapM = maximumGapByLayer[zone.Layer];
                var zoneBounds = Bounds(zone.Contour);
                var otherZones = zones.Where(candidate =>
                    !ReferenceEquals(candidate, zone)).ToList();
                foreach (var plate in requiredPlates)
                {
                    if (ZoneCoverageRules.IntersectsElementFootprint(zone, plate, slabOutline))
                    {
                        supportingIds.Add(plate.Id);
                        continue;
                    }

                    var plateBounds = PlateBounds(plate);
                    if (plateBounds.MaxX < zoneBounds.MinX - maximumGapM ||
                        plateBounds.MinX > zoneBounds.MaxX + maximumGapM ||
                        plateBounds.MaxY < zoneBounds.MinY - maximumGapM ||
                        plateBounds.MinY > zoneBounds.MaxY + maximumGapM)
                        continue;

                    var requiredAs = plate.Rebar.Get(zone.Layer) - background;
                    if (ZoneCoverageRules.CoversOrBridgesGap(
                            zones, plate, zone.Layer, requiredAs, slabOutline, openings,
                            allowOverlappingZones: true) &&
                        !ZoneCoverageRules.CoversOrBridgesGap(
                            otherZones, plate, zone.Layer, requiredAs, slabOutline, openings,
                            allowOverlappingZones: true))
                        supportingIds.Add(plate.Id);
                }

                if (supportingIds.Count == 0)
                {
                    zones.RemoveAt(i);
                    removed++;
                    continue;
                }

                zone.NodeIds = supportingIds.OrderBy(id => id).ToList();
                zone.ElementId = zone.NodeIds[0];
            }
            return removed;
        }

        public static int ApplyOuterBoundaryCuts(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            IList<Point3> outline, double jointGapMm)
        {
            if (zones == null || zones.Count == 0 || outline == null || outline.Count < 3)
                return 0;

            var changed = AlignLongitudinalEndsToBoundary(zones, plates, outline);
            changed += SeparateOuterBoundaryJoints(zones, plates, outline, jointGapMm);
            return changed;
        }

        private static int AlignLongitudinalEndsToBoundary(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates, IList<Point3> outline)
        {
            var platesById = (plates ?? new List<LiraPlateElement>())
                .GroupBy(plate => plate.Id).ToDictionary(group => group.Key, group => group.First());
            var changed = 0;
            // Resolve longer details first; at each rounded edge keep the outermost
            // corner contact that still covers the assigned element centers.
            foreach (var zone in zones.Where(zone => zone.Contour != null && zone.Contour.Count >= 3)
                         .OrderByDescending(zone => zone.LengthMm))
            {
                var bounds = Bounds(zone.Contour);
                var axialMin = zone.Direction == ZoneDirection.X ? bounds.MinX : bounds.MinY;
                var axialMax = zone.Direction == ZoneDirection.X ? bounds.MaxX : bounds.MaxY;
                var crossMin = zone.Direction == ZoneDirection.X ? bounds.MinY : bounds.MinX;
                var crossMax = zone.Direction == ZoneDirection.X ? bounds.MaxY : bounds.MaxX;
                var assigned = zone.NodeIds.Where(platesById.ContainsKey)
                    .Select(id => platesById[id].Centroid).ToList();
                var supportMin = assigned.Count == 0
                    ? axialMin
                    : assigned.Min(point => zone.Direction == ZoneDirection.X ? point.X : point.Y);
                var supportMax = assigned.Count == 0
                    ? axialMax
                    : assigned.Max(point => zone.Direction == ZoneDirection.X ? point.X : point.Y);
                var nextMin = axialMin;
                var nextMax = axialMax;

                if (EndHasOutsideCorner(zone.Direction, axialMin, crossMin, crossMax, outline))
                {
                    var candidates = new[] { crossMin, crossMax }
                        .SelectMany(cross => AxisBoundaryIntersections(zone.Direction, cross, outline)
                            .Select(value => (Value: value, Cross: cross)))
                        .Where(candidate => candidate.Value >= axialMin - 1e-8 &&
                                            candidate.Value <= supportMin + 1e-8)
                        .Where(candidate => IsInsideAfterBoundary(zone.Direction,
                            candidate.Value, candidate.Cross, outline, fromStart: true))
                        .ToList();
                    if (candidates.Count > 0) nextMin = candidates.Min(candidate => candidate.Value);
                }

                if (EndHasOutsideCorner(zone.Direction, axialMax, crossMin, crossMax, outline))
                {
                    var candidates = new[] { crossMin, crossMax }
                        .SelectMany(cross => AxisBoundaryIntersections(zone.Direction, cross, outline)
                            .Select(value => (Value: value, Cross: cross)))
                        .Where(candidate => candidate.Value <= axialMax + 1e-8 &&
                                            candidate.Value >= supportMax - 1e-8)
                        .Where(candidate => IsInsideAfterBoundary(zone.Direction,
                            candidate.Value, candidate.Cross, outline, fromStart: false))
                        .ToList();
                    if (candidates.Count > 0) nextMax = candidates.Max(candidate => candidate.Value);
                }

                if (nextMax - nextMin <= 0.05 ||
                    (Math.Abs(nextMin - axialMin) <= 1e-8 && Math.Abs(nextMax - axialMax) <= 1e-8))
                    continue;

                SetContour(zone, Rectangle(
                    zone.Direction == ZoneDirection.X ? nextMin : bounds.MinX,
                    zone.Direction == ZoneDirection.X ? nextMax : bounds.MaxX,
                    zone.Direction == ZoneDirection.X ? bounds.MinY : nextMin,
                    zone.Direction == ZoneDirection.X ? bounds.MaxY : nextMax,
                    zone.LevelZM));
                zone.Comment = AppendBoundaryComment(zone.Comment, "торец привязан к внешнему контуру");
                changed++;
            }
            return changed;
        }

        private static bool EndHasOutsideCorner(
            ZoneDirection direction, double axial, double crossMin, double crossMax,
            IList<Point3> outline)
        {
            return new[] { crossMin, crossMax }.Any(cross =>
            {
                var point = AxisPoint(direction, axial, cross);
                return !PointInPolygonOrOnBoundary(point.X, point.Y, outline);
            });
        }

        private static List<double> AxisBoundaryIntersections(
            ZoneDirection direction, double cross, IList<Point3> outline)
        {
            const double tolerance = 1e-9;
            var values = new List<double>();
            for (var i = 0; i < outline.Count; i++)
            {
                var first = outline[i];
                var second = outline[(i + 1) % outline.Count];
                var firstCross = direction == ZoneDirection.X ? first.Y : first.X;
                var secondCross = direction == ZoneDirection.X ? second.Y : second.X;
                var firstAxis = direction == ZoneDirection.X ? first.X : first.Y;
                var secondAxis = direction == ZoneDirection.X ? second.X : second.Y;
                if (Math.Abs(firstCross - cross) <= tolerance &&
                    Math.Abs(secondCross - cross) <= tolerance)
                {
                    values.Add(firstAxis);
                    values.Add(secondAxis);
                    continue;
                }
                if (cross < Math.Min(firstCross, secondCross) - tolerance ||
                    cross > Math.Max(firstCross, secondCross) + tolerance ||
                    Math.Abs(secondCross - firstCross) <= tolerance)
                    continue;
                var ratio = (cross - firstCross) / (secondCross - firstCross);
                values.Add(firstAxis + ratio * (secondAxis - firstAxis));
            }
            var distinct = new List<double>();
            foreach (var value in values.OrderBy(value => value))
                if (distinct.Count == 0 || Math.Abs(value - distinct[distinct.Count - 1]) > 1e-8)
                    distinct.Add(value);
            return distinct;
        }

        private static bool IsInsideAfterBoundary(
            ZoneDirection direction, double axial, double cross,
            IList<Point3> outline, bool fromStart)
        {
            var inward = fromStart ? 1e-5 : -1e-5;
            var probe = AxisPoint(direction, axial + inward, cross);
            return MeshBoundary.PointInPolygon(probe.X, probe.Y, outline);
        }

        private static Point3 AxisPoint(ZoneDirection direction, double axial, double cross) =>
            direction == ZoneDirection.X
                ? new Point3(axial, cross, 0)
                : new Point3(cross, axial, 0);

        private static bool PointInPolygonOrOnBoundary(double x, double y, IList<Point3> outline)
        {
            const double tolerance = 1e-8;
            for (var i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var lengthSquared = dx * dx + dy * dy;
                var t = lengthSquared <= tolerance ? 0 :
                    Math.Max(0, Math.Min(1, ((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared));
                var px = a.X + t * dx;
                var py = a.Y + t * dy;
                if ((x - px) * (x - px) + (y - py) * (y - py) <= tolerance * tolerance)
                    return true;
            }
            return MeshBoundary.PointInPolygon(x, y, outline);
        }

        private static int SeparateOuterBoundaryJoints(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            IList<Point3> outline, double gapMm)
        {
            var gap = UnitConversion.MmToMeters(Math.Max(0, gapMm));
            if (gap <= 1e-8 || zones.Count < 2) return 0;
            var changed = 0;
            var platesById = (plates ?? new List<LiraPlateElement>())
                .GroupBy(plate => plate.Id).ToDictionary(group => group.Key, group => group.First());
            var ordered = zones.Where(zone => zone.Contour != null && zone.Contour.Count >= 3)
                .OrderByDescending(zone => zone.LengthMm).ToList();
            for (var i = 0; i < ordered.Count; i++)
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var longer = ordered[i];
                var shorter = ordered[j];
                if (longer.Layer != shorter.Layer || longer.Direction != shorter.Direction ||
                    longer.NodeIds.Intersect(shorter.NodeIds).Any())
                    continue;
                var a = Bounds(longer.Contour);
                var b = Bounds(shorter.Contour);
                var longCrossMin = longer.Direction == ZoneDirection.X ? a.MinY : a.MinX;
                var longCrossMax = longer.Direction == ZoneDirection.X ? a.MaxY : a.MaxX;
                var shortCrossMin = shorter.Direction == ZoneDirection.X ? b.MinY : b.MinX;
                var shortCrossMax = shorter.Direction == ZoneDirection.X ? b.MaxY : b.MaxX;
                var crossMin = Math.Max(longCrossMin, shortCrossMin);
                var crossMax = Math.Min(longCrossMax, shortCrossMax);
                if (crossMax - crossMin <= 0.05) continue;
                var longMin = longer.Direction == ZoneDirection.X ? a.MinX : a.MinY;
                var longMax = longer.Direction == ZoneDirection.X ? a.MaxX : a.MaxY;
                var shortMin = shorter.Direction == ZoneDirection.X ? b.MinX : b.MinY;
                var shortMax = shorter.Direction == ZoneDirection.X ? b.MaxX : b.MaxY;
                if (Math.Min(longMax, shortMax) - Math.Max(longMin, shortMin) <= 1e-8)
                    continue;

                var candidates = new List<(bool TrimStart, double Coordinate)>();
                if (longMin < shortMin - 1e-8 && longMax > shortMin)
                    candidates.Add((false, shortMin - gap));
                if (longMax > shortMax + 1e-8 && longMin < shortMax)
                    candidates.Add((true, shortMax + gap));
                foreach (var candidate in candidates)
                {
                    var newMin = candidate.TrimStart ? candidate.Coordinate : longMin;
                    var newMax = candidate.TrimStart ? longMax : candidate.Coordinate;
                    if (newMax - newMin <= 0.05 ||
                        (candidate.TrimStart && newMin <= longMin) ||
                        (!candidate.TrimStart && newMax >= longMax))
                        continue;
                    var removedMin = candidate.TrimStart ? longMin : newMax;
                    var removedMax = candidate.TrimStart ? newMin : longMax;
                    var strip = longer.Direction == ZoneDirection.X
                        ? (MinX: removedMin, MaxX: removedMax, MinY: longCrossMin, MaxY: longCrossMax)
                        : (MinX: longCrossMin, MaxX: longCrossMax, MinY: removedMin, MaxY: removedMax);
                    if (!RectangleOutsideOutline(strip.MinX, strip.MaxX,
                            strip.MinY, strip.MaxY, outline) ||
                        !CanDropAssignedElements(longer, shorter, candidate.TrimStart,
                            candidate.Coordinate, platesById))
                        continue;

                    SetContour(longer, Rectangle(
                        longer.Direction == ZoneDirection.X ? newMin : a.MinX,
                        longer.Direction == ZoneDirection.X ? newMax : a.MaxX,
                        longer.Direction == ZoneDirection.X ? a.MinY : newMin,
                        longer.Direction == ZoneDirection.X ? a.MaxY : newMax,
                        longer.LevelZM));
                    TransferDroppedAssignedElements(longer, shorter, platesById);
                    longer.Comment = AppendBoundaryComment(longer.Comment,
                        $"стык у края: зазор {gapMm:0} мм");
                    changed++;
                    break;
                }
            }
            return changed;
        }

        private static bool CanDropAssignedElements(
            AdditionalZone source, AdditionalZone covering, bool trimStart, double coordinate,
            IDictionary<int, LiraPlateElement> platesById)
        {
            var sourceBounds = Bounds(source.Contour);
            var retainedMin = source.Direction == ZoneDirection.X ? sourceBounds.MinX : sourceBounds.MinY;
            var retainedMax = source.Direction == ZoneDirection.X ? sourceBounds.MaxX : sourceBounds.MaxY;
            if (trimStart) retainedMin = coordinate; else retainedMax = coordinate;
            var coverBounds = Bounds(covering.Contour);
            var required = Math.Max(source.AsRequired, source.AsAdditional);
            foreach (var id in source.NodeIds)
            {
                if (!platesById.TryGetValue(id, out var plate)) continue;
                var axial = source.Direction == ZoneDirection.X ? plate.Centroid.X : plate.Centroid.Y;
                if (axial >= retainedMin - 1e-8 && axial <= retainedMax + 1e-8) continue;
                var coveredAxially = source.Direction == ZoneDirection.X
                    ? plate.Centroid.X >= coverBounds.MinX - 1e-8 && plate.Centroid.X <= coverBounds.MaxX + 1e-8
                    : plate.Centroid.Y >= coverBounds.MinY - 1e-8 && plate.Centroid.Y <= coverBounds.MaxY + 1e-8;
                var coveredTransversely = source.Direction == ZoneDirection.X
                    ? plate.Centroid.Y >= coverBounds.MinY - 1e-8 && plate.Centroid.Y <= coverBounds.MaxY + 1e-8
                    : plate.Centroid.X >= coverBounds.MinX - 1e-8 && plate.Centroid.X <= coverBounds.MaxX + 1e-8;
                if (!coveredAxially || !coveredTransversely ||
                    covering.AsCoveredCm2PerM + 1e-6 < required)
                    return false;
            }
            return true;
        }

        private static void TransferDroppedAssignedElements(
            AdditionalZone source, AdditionalZone target,
            IDictionary<int, LiraPlateElement> platesById)
        {
            if (source.NodeIds.Count == 0) return;
            var sourceBounds = Bounds(source.Contour);
            var retained = new List<int>();
            var transferred = new List<int>();
            foreach (var id in source.NodeIds.Distinct())
            {
                if (!platesById.TryGetValue(id, out var plate))
                {
                    retained.Add(id);
                    continue;
                }
                var inside = plate.Centroid.X >= sourceBounds.MinX - 1e-8 &&
                             plate.Centroid.X <= sourceBounds.MaxX + 1e-8 &&
                             plate.Centroid.Y >= sourceBounds.MinY - 1e-8 &&
                             plate.Centroid.Y <= sourceBounds.MaxY + 1e-8;
                (inside ? retained : transferred).Add(id);
            }
            source.NodeIds = retained;
            source.ElementId = retained.FirstOrDefault();
            target.NodeIds = target.NodeIds.Concat(transferred).Distinct().ToList();
            target.ElementId = target.NodeIds.FirstOrDefault();
        }

        private static bool RectangleOutsideOutline(
            double minX, double maxX, double minY, double maxY, IList<Point3> outline)
        {
            if (maxX - minX <= 1e-8 || maxY - minY <= 1e-8) return true;
            var overlap = Clipper.Intersect(new Paths64 { ToPath(Rectangle(minX, maxX, minY, maxY, 0)) },
                new Paths64 { ToPath(outline) }, FillRule.NonZero);
            return overlap.Sum(path => Math.Abs(Clipper.Area(path))) <= 1;
        }

        private static string AppendBoundaryComment(string current, string addition) =>
            string.IsNullOrWhiteSpace(current) ? addition : current.Contains(addition)
                ? current : current + "; " + addition;

        public static int EnsureAssignedCapacity(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates, AnalysisSettings settings)
        {
            var platesById = plates.ToDictionary(plate => plate.Id);
            var maxDiameter = settings.MaxDiameterMm > 0 ? settings.MaxDiameterMm : 36;
            var excludedDiameters = settings.ExcludedZoneDiametersMm?.ToArray();
            var changedCount = 0;

            foreach (var zone in zones)
            {
                if (zone.NodeIds == null || zone.NodeIds.Count == 0) continue;
                var requiredAs = zone.NodeIds
                    .Distinct()
                    .Where(platesById.ContainsKey)
                    .Select(id => platesById[id])
                    .Where(plate => plate.Rebar.Ok)
                    .Select(plate => plate.Rebar.Get(zone.Layer) - BackgroundAs(settings, zone.Layer))
                    .DefaultIfEmpty(0)
                    .Max();
                if (requiredAs <= MosaicBuilder.PositiveResidualToleranceCm2PerM) continue;

                var currentCapacity = zone.DiameterMm > 0 && zone.BarStepMm > 0
                    ? BarCapacity.AsCm2PerM(zone.DiameterMm, zone.BarStepMm)
                    : 0;
                if (currentCapacity + 1e-6 >= requiredAs)
                {
                    zone.AsCoveredCm2PerM = currentCapacity;
                    continue;
                }

                var step = zone.BarStepMm == 100 ? 100 : 200;
                var minDiameter = zone.Layer is RebarLayer.As1 or RebarLayer.As2
                    ? settings.BgBottomDiameterMm
                    : settings.BgTopDiameterMm;
                var diameter = BarCapacity.MinDiameterForAs(
                    requiredAs, step, maxDiameter, minDiameter, excludedDiameters);
                var capacity = diameter > 0 ? BarCapacity.AsCm2PerM(diameter, step) : 0;

                if (capacity + 1e-6 < requiredAs && step == 200 && settings.UseBarStep100)
                {
                    step = 100;
                    diameter = BarCapacity.MinDiameterForAs(
                        requiredAs, step, maxDiameter, minDiameter, excludedDiameters);
                    capacity = diameter > 0 ? BarCapacity.AsCm2PerM(diameter, step) : 0;
                }

                if (capacity + 1e-6 < requiredAs)
                {
                    zone.StatusColor = "warn";
                    zone.Comment = AppendComment(zone.Comment,
                        $"недостаточная вместимость: требуется As={requiredAs:0.##} см²/м");
                    changedCount++;
                    continue;
                }

                zone.AsCoveredCm2PerM = capacity;
                zone.DiameterMm = diameter;
                zone.BarStepMm = step;
                zone.AsAdditional = Math.Max(zone.AsAdditional, requiredAs);
                zone.AsRequired = Math.Max(zone.AsRequired,
                    requiredAs + BackgroundAs(settings, zone.Layer));
                zone.Comment = AppendComment(zone.Comment,
                    $"вместимость скорректирована по пику КЭ: Ø{diameter}/{step}");
                changedCount++;
            }

            return changedCount;
        }

        private static string AppendComment(string comment, string addition) =>
            string.IsNullOrWhiteSpace(comment) ? addition : comment + "; " + addition;

        public static List<AdditionalZone> SplitPerpendicularToEdge(
            AdditionalZone zone, double xM, double yM, bool verticalEdge, IList<Point3> slab,
            bool clipToSlab = true)
        {
            // A vertical edge determines a horizontal cut, and vice versa.
            return Split(zone, verticalEdge ? yM : xM, !verticalEdge, slab, clipToSlab);
        }

        public static bool ResizeByDimensions(AdditionalZone zone, double lengthMm, double widthMm,
            IList<Point3> slab, bool clipToSlab = true)
        {
            if (lengthMm <= 50 || widthMm <= 50) return false;
            var halfX = (zone.Direction == ZoneDirection.X ? lengthMm : widthMm) / 2000.0;
            var halfY = (zone.Direction == ZoneDirection.Y ? lengthMm : widthMm) / 2000.0;
            var candidate = Copy(zone);
            if (!Resize(candidate, zone.Placement.X - halfX, zone.Placement.X + halfX,
                    zone.Placement.Y - halfY, zone.Placement.Y + halfY, slab, clipToSlab)) return false;
            SetContour(zone, candidate.Contour.ToList());
            zone.Comment = "габариты изменены в предпросмотре";
            return true;
        }

        public static void SetFamily(AdditionalZone zone, ZoneFamilyKind kind, string familyName)
        {
            var name = AppConfig.StripRfa(familyName);
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Имя семейства не задано.", nameof(familyName));
            zone.FamilyKind = kind;
            zone.FamilyFileName = name;
            SetContour(zone, zone.Contour.ToList());
            zone.Comment = "семейство изменено в предпросмотре";
        }

        public static bool CreateGap(AdditionalZone moving, AdditionalZone reference, IList<Point3> slab,
            bool clipToSlab = true)
        {
            if (ReferenceEquals(moving, reference)) return false;
            var gap = Math.Min(moving.BarStepMm, reference.BarStepMm) / 1000.0;
            var a = Bounds(moving.Contour);
            var b = Bounds(reference.Contour);

            var candidates = new[]
            {
                (Dx: b.MinX - gap - a.MaxX, Dy: 0.0),
                (Dx: b.MaxX + gap - a.MinX, Dy: 0.0),
                (Dx: 0.0, Dy: b.MinY - gap - a.MaxY),
                (Dx: 0.0, Dy: b.MaxY + gap - a.MinY)
            }.OrderBy(v => Math.Abs(v.Dx) + Math.Abs(v.Dy));

            foreach (var candidate in candidates)
            {
                var copy = Copy(moving);
                var shifted = moving.Contour
                    .Select(p => new Point3(p.X + candidate.Dx, p.Y + candidate.Dy, p.Z))
                    .ToList();
                if (!ApplyEditedContour(copy, shifted, slab, clipToSlab)) continue;
                if (!HasRequiredGap(copy.Contour, reference.Contour, gap)) continue;
                SetContour(moving, copy.Contour.ToList());
                moving.Comment = $"зазор {gap * 1000:0} мм создан в предпросмотре";
                return true;
            }
            return false;
        }

        private static bool HasRequiredGap(IList<Point3> first, IList<Point3> second, double required)
        {
            var a = Bounds(first);
            var b = Bounds(second);
            var overlapX = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            var overlapY = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            var gapX = Math.Max(0, Math.Max(a.MinX, b.MinX) - Math.Min(a.MaxX, b.MaxX));
            var gapY = Math.Max(0, Math.Max(a.MinY, b.MinY) - Math.Min(a.MaxY, b.MaxY));
            return (overlapY > 1e-6 && gapX + 1e-6 >= required) ||
                   (overlapX > 1e-6 && gapY + 1e-6 >= required);
        }

        public static bool HasPlacementConflict(AdditionalZone first, AdditionalZone second)
        {
            if (first.Layer != second.Layer || first.Contour.Count < 3 || second.Contour.Count < 3)
                return false;
            var a = Bounds(first.Contour);
            var b = Bounds(second.Contour);
            var overlapX = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            var overlapY = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            if (overlapX > 1e-6 && overlapY > 1e-6)
                return !IsAllowedLongitudinalLapOverlap(first, second);
            var requiredGap = Math.Min(first.BarStepMm, second.BarStepMm) / 1000.0;
            var gapX = Math.Max(0, Math.Max(a.MinX, b.MinX) - Math.Min(a.MaxX, b.MaxX));
            var gapY = Math.Max(0, Math.Max(a.MinY, b.MinY) - Math.Min(a.MaxY, b.MaxY));
            if (first.Direction == second.Direction)
                return first.Direction == ZoneDirection.X
                    ? overlapX > 1e-6 && gapY < requiredGap - 1e-6
                    : overlapY > 1e-6 && gapX < requiredGap - 1e-6;
            return (overlapY > 1e-6 && gapX < requiredGap - 1e-6) ||
                   (overlapX > 1e-6 && gapY < requiredGap - 1e-6);
        }

        internal static bool IsAllowedLongitudinalLapOverlap(
            AdditionalZone first, AdditionalZone second)
        {
            if (first.Layer != second.Layer || first.Direction != second.Direction ||
                first.Contour.Count < 3 || second.Contour.Count < 3)
                return false;

            var allowedMm = RebarTables.AllowedZoneOverlapMm(first, second);
            if (allowedMm <= 0) return false;

            var a = Bounds(first.Contour);
            var b = Bounds(second.Contour);
            var longitudinalOverlap = first.Direction == ZoneDirection.X
                ? Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX)
                : Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            var firstCrossMin = first.Direction == ZoneDirection.X ? a.MinY : a.MinX;
            var firstCrossMax = first.Direction == ZoneDirection.X ? a.MaxY : a.MaxX;
            var secondCrossMin = first.Direction == ZoneDirection.X ? b.MinY : b.MinX;
            var secondCrossMax = first.Direction == ZoneDirection.X ? b.MaxY : b.MaxX;

            const double crossAlignmentToleranceM = 0.001;
            var sameBarBand = Math.Abs(firstCrossMin - secondCrossMin) <= crossAlignmentToleranceM &&
                              Math.Abs(firstCrossMax - secondCrossMax) <= crossAlignmentToleranceM;
            return sameBarBand &&
                   UnitConversion.MetersToMm(longitudinalOverlap) + 1 >= allowedMm;
        }

        public static void EnforceRequiredGaps(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            AnalysisSettings? settings = null, IList<Point3>? slab = null,
            IList<OpeningInfo>? openings = null)
        {
            var platesById = plates.ToDictionary(plate => plate.Id);
            for (var pass = 0; pass < Math.Min(512, Math.Max(1, zones.Count * 2)); pass++)
            {
                var adjacentSpacingPairs = ZoneLayoutDiagnostics.AddInterZoneStepIssues(
                    zones, new ZoneDiagnostics(),
                    UnitConversion.MmToMeters(Math.Max(0, settings?.GridCellMm ?? 0)));
                var changed = false;
                for (var i = 0; i < zones.Count && !changed; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var first = zones[i];
                    var second = zones[j];
                    var placementConflict = HasPlacementConflict(first, second);
                    var excessiveAdjacentGap = !placementConflict &&
                        adjacentSpacingPairs.Contains((i, j));
                    if (!placementConflict && !excessiveAdjacentGap) continue;
                    if (excessiveAdjacentGap &&
                        TryExpandWeakerZoneIntoGap(
                            zones, first, second, platesById, settings, slab, openings))
                    {
                        changed = true;
                        break;
                    }
                    if (!TryResolvePlacementConflict(
                            zones, first, second, platesById, settings, slab, openings,
                            requireExactGap: excessiveAdjacentGap))
                        continue;
                    changed = true;
                    break;
                }
                if (!changed) break;
            }
        }

        public static void CloseUncoveredStepGaps(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            AnalysisSettings settings, IList<Point3> slab, IList<OpeningInfo> openings)
        {
            var platesById = plates.ToDictionary(plate => plate.Id);
            var allPlates = plates.ToList();
            var orderedPlates = allPlates.Where(plate => plate.Rebar.Ok)
                .OrderBy(plate => plate.Id).ToList();

            foreach (var plate in orderedPlates)
            foreach (RebarLayer layer in System.Enum.GetValues(typeof(RebarLayer)))
            {
                if (!LayerEnabled(layer, settings)) continue;
                var requiredAs = plate.Rebar.Get(layer) - BackgroundAs(settings, layer);
                if (requiredAs <= MosaicBuilder.PositiveResidualToleranceCm2PerM ||
                    HasCoverage(zones, layer, plate, requiredAs, slab, openings))
                    continue;

                var candidates = new List<(AdditionalZone Moving, double Dx, double Dy, int ExpandEdge)>();
                var sameLayer = zones.Where(zone => zone.Layer == layer &&
                    zone.Contour.Count >= 3 && zone.AsCoveredCm2PerM + 1e-6 >= requiredAs).ToList();
                for (var i = 0; i < sameLayer.Count; i++)
                for (var j = i + 1; j < sameLayer.Count; j++)
                {
                    var first = sameLayer[i];
                    var second = sameLayer[j];
                    if (first.Direction != second.Direction) continue;
                    var a = Bounds(first.Contour);
                    var b = Bounds(second.Contour);
                    var transverseMinA = first.Direction == ZoneDirection.X ? a.MinY : a.MinX;
                    var transverseMaxA = first.Direction == ZoneDirection.X ? a.MaxY : a.MaxX;
                    var transverseMinB = first.Direction == ZoneDirection.X ? b.MinY : b.MinX;
                    var transverseMaxB = first.Direction == ZoneDirection.X ? b.MaxY : b.MaxX;
                    var longitudinalMin = first.Direction == ZoneDirection.X
                        ? Math.Max(a.MinX, b.MinX) : Math.Max(a.MinY, b.MinY);
                    var longitudinalMax = first.Direction == ZoneDirection.X
                        ? Math.Min(a.MaxX, b.MaxX) : Math.Min(a.MaxY, b.MaxY);
                    var longitudinalPoint = first.Direction == ZoneDirection.X
                        ? plate.Centroid.X : plate.Centroid.Y;
                    if (longitudinalMax - longitudinalMin <= 1e-6 ||
                        longitudinalPoint < longitudinalMin - 1e-6 ||
                        longitudinalPoint > longitudinalMax + 1e-6)
                        continue;

                    var lower = transverseMinA <= transverseMinB ? first : second;
                    var upper = ReferenceEquals(lower, first) ? second : first;
                    var lowerBounds = ReferenceEquals(lower, first) ? a : b;
                    var upperBounds = ReferenceEquals(upper, first) ? a : b;
                    var lowerMax = lower.Direction == ZoneDirection.X
                        ? lowerBounds.MaxY : lowerBounds.MaxX;
                    var upperMin = upper.Direction == ZoneDirection.X
                        ? upperBounds.MinY : upperBounds.MinX;
                    var point = lower.Direction == ZoneDirection.X
                        ? plate.Centroid.Y : plate.Centroid.X;
                    var gapM = upperMin - lowerMax;
                    var allowedGapM = UnitConversion.MmToMeters(Math.Min(lower.BarStepMm, upper.BarStepMm));
                    if (gapM <= allowedGapM + 1e-6 || point < lowerMax - 1e-6 || point > upperMin + 1e-6)
                        continue;

                    var shiftM = gapM - allowedGapM;
                    if (lower.Direction == ZoneDirection.X)
                    {
                        candidates.Add((lower, 0, shiftM, 0));
                        candidates.Add((upper, 0, -shiftM, 0));
                        candidates.Add((lower, 0, shiftM, 2));
                        candidates.Add((upper, 0, shiftM, 1));
                    }
                    else
                    {
                        candidates.Add((lower, shiftM, 0, 0));
                        candidates.Add((upper, -shiftM, 0, 0));
                        candidates.Add((lower, shiftM, 0, 2));
                        candidates.Add((upper, shiftM, 0, 1));
                    }
                }

                foreach (var zone in sameLayer)
                {
                    var bounds = Bounds(zone.Contour);
                    var plateBounds = PlateBounds(plate);
                    var longitudinalContains = zone.Direction == ZoneDirection.X
                        ? plate.Centroid.X >= bounds.MinX - 1e-6 && plate.Centroid.X <= bounds.MaxX + 1e-6
                        : plate.Centroid.Y >= bounds.MinY - 1e-6 && plate.Centroid.Y <= bounds.MaxY + 1e-6;
                    if (!longitudinalContains)
                    {
                        var longitudinalMin = zone.Direction == ZoneDirection.X ? bounds.MinX : bounds.MinY;
                        var longitudinalMax = zone.Direction == ZoneDirection.X ? bounds.MaxX : bounds.MaxY;
                        var elementMin = zone.Direction == ZoneDirection.X ? plateBounds.MinX : plateBounds.MinY;
                        var elementMax = zone.Direction == ZoneDirection.X ? plateBounds.MaxX : plateBounds.MaxY;
                        if (elementMax <= longitudinalMin + 1e-6 || elementMin >= longitudinalMax - 1e-6)
                            continue;
                    }

                    var min = zone.Direction == ZoneDirection.X ? bounds.MinY : bounds.MinX;
                    var max = zone.Direction == ZoneDirection.X ? bounds.MaxY : bounds.MaxX;
                    var coordinate = zone.Direction == ZoneDirection.X ? plate.Centroid.Y : plate.Centroid.X;
                    var delta = coordinate < min ? coordinate - min : coordinate > max ? coordinate - max : 0;
                    if (Math.Abs(delta) > 1e-6 &&
                        Math.Abs(delta) <= UnitConversion.MmToMeters(zone.BarStepMm) + 1e-6)
                        candidates.Add(zone.Direction == ZoneDirection.X
                            ? (zone, 0, delta, 0)
                            : (zone, delta, 0, 0));

                    var crossMin = zone.Direction == ZoneDirection.X ? plateBounds.MinY : plateBounds.MinX;
                    var crossMax = zone.Direction == ZoneDirection.X ? plateBounds.MaxY : plateBounds.MaxX;
                    var alongMin = zone.Direction == ZoneDirection.X ? plateBounds.MinX : plateBounds.MinY;
                    var alongMax = zone.Direction == ZoneDirection.X ? plateBounds.MaxX : plateBounds.MaxY;
                    var zoneAlongMin = zone.Direction == ZoneDirection.X ? bounds.MinX : bounds.MinY;
                    var zoneAlongMax = zone.Direction == ZoneDirection.X ? bounds.MaxX : bounds.MaxY;
                    var crossMinExtension = crossMin < min - 1e-6
                        ? RoundUpToBarStep(min - crossMin, zone.BarStepMm) : 0;
                    var crossMaxExtension = crossMax > max + 1e-6
                        ? RoundUpToBarStep(crossMax - max, zone.BarStepMm) : 0;
                    var alongMinExtension = alongMin < zoneAlongMin - 1e-6
                        ? RoundUpToBarStep(zoneAlongMin - alongMin, zone.BarStepMm) : 0;
                    var alongMaxExtension = alongMax > zoneAlongMax + 1e-6
                        ? RoundUpToBarStep(alongMax - zoneAlongMax, zone.BarStepMm) : 0;

                    void AddExpansion(int edge, double crossExtension, double alongExtension)
                    {
                        var dx = zone.Direction == ZoneDirection.X ? alongExtension : crossExtension;
                        var dy = zone.Direction == ZoneDirection.X ? crossExtension : alongExtension;
                        candidates.Add((zone, dx, dy, edge));
                    }

                    if (crossMinExtension > 0) AddExpansion(1, crossMinExtension, 0);
                    if (crossMaxExtension > 0) AddExpansion(2, crossMaxExtension, 0);
                    if (alongMinExtension > 0) AddExpansion(4, 0, alongMinExtension);
                    if (alongMaxExtension > 0) AddExpansion(8, 0, alongMaxExtension);
                    if (crossMinExtension > 0 && alongMinExtension > 0)
                        AddExpansion(5, crossMinExtension, alongMinExtension);
                    if (crossMinExtension > 0 && alongMaxExtension > 0)
                        AddExpansion(9, crossMinExtension, alongMaxExtension);
                    if (crossMaxExtension > 0 && alongMinExtension > 0)
                        AddExpansion(6, crossMaxExtension, alongMinExtension);
                    if (crossMaxExtension > 0 && alongMaxExtension > 0)
                        AddExpansion(10, crossMaxExtension, alongMaxExtension);
                }

                var accepted = candidates
                    .Distinct()
                    .OrderBy(candidate => Math.Abs(candidate.Dx) + Math.Abs(candidate.Dy))
                    .ThenBy(candidate => candidate.ExpandEdge == 0 ? 0 : 1)
                    .ThenBy(candidate => candidate.Moving.NodeIds.Count)
                    .Select(candidate => TryShiftForCoverageGap(
                        candidate.Moving, candidate.Dx, candidate.Dy, candidate.ExpandEdge, zones,
                        allPlates, platesById, settings, slab, openings,
                        plate, layer, requiredAs))
                    .FirstOrDefault(candidate => candidate != null);
                if (accepted != null)
                {
                    var replacement = accepted.Value;
                    var index = zones.IndexOf(replacement.Original);
                    zones[index] = replacement.Replacement;
                }
                else
                {
                    TryShiftNeighborBoundaryForCoverage(
                        zones, allPlates, platesById, settings, slab, openings,
                        plate, layer, requiredAs);
                }
            }
        }

        private static (AdditionalZone Original, AdditionalZone Replacement)? TryShiftForCoverageGap(
            AdditionalZone moving, double dx, double dy, int expandEdge, IList<AdditionalZone> zones,
            IList<LiraPlateElement> plates, IReadOnlyDictionary<int, LiraPlateElement> platesById,
            AnalysisSettings settings,
            IList<Point3> slab, IList<OpeningInfo> openings,
            LiraPlateElement target, RebarLayer layer, double requiredAs)
        {
            var copy = Copy(moving);
            var shifted = moving.Contour.Select(point =>
                new Point3(point.X + dx, point.Y + dy, point.Z)).ToList();
            if (expandEdge != 0)
            {
                var bounds = Bounds(moving.Contour);
                var crossExtension = RoundUpToBarStep(
                    moving.Direction == ZoneDirection.X ? Math.Abs(dy) : Math.Abs(dx), moving.BarStepMm);
                var alongExtension = RoundUpToBarStep(
                    moving.Direction == ZoneDirection.X ? Math.Abs(dx) : Math.Abs(dy), moving.BarStepMm);
                var expandCrossMin = (expandEdge & 1) != 0;
                var expandCrossMax = (expandEdge & 2) != 0;
                var expandAlongMin = (expandEdge & 4) != 0;
                var expandAlongMax = (expandEdge & 8) != 0;
                if (moving.Direction == ZoneDirection.X)
                {
                    if (expandCrossMin) bounds.MinY -= crossExtension;
                    if (expandCrossMax) bounds.MaxY += crossExtension;
                    if (expandAlongMin) bounds.MinX -= alongExtension;
                    if (expandAlongMax) bounds.MaxX += alongExtension;
                }
                else
                {
                    if (expandCrossMin) bounds.MinX -= crossExtension;
                    if (expandCrossMax) bounds.MaxX += crossExtension;
                    if (expandAlongMin) bounds.MinY -= alongExtension;
                    if (expandAlongMax) bounds.MaxY += alongExtension;
                }
                shifted = Rectangle(bounds.MinX, bounds.MaxX, bounds.MinY, bounds.MaxY, moving.LevelZM);
            }
            if (!ApplyClipped(copy, shifted, slab) || copy.LengthMm > 11701 ||
                (settings.MaxZoneWidthM > 0 && copy.WidthM > settings.MaxZoneWidthM + 1e-6) ||
                IntersectsOpening(copy, openings) ||
                (!ContainsContour(copy, moving) &&
                 !PreservesRequiredCoverage(copy, moving, zones, platesById, settings, slab, openings)))
                return null;

            var proposedZones = zones.Where(zone => !ReferenceEquals(zone, moving))
                .Concat(new[] { copy }).ToList();
            if (!HasCoverage(proposedZones, layer, target, requiredAs, slab, openings)) return null;
            var originalBounds = Bounds(moving.Contour);
            var replacementBounds = Bounds(copy.Contour);
            var paddingM = UnitConversion.MmToMeters(moving.BarStepMm);
            foreach (var coveredPlate in plates)
            {
                if (!coveredPlate.Rebar.Ok) continue;
                var plateRequiredAs = coveredPlate.Rebar.Get(layer) - BackgroundAs(settings, layer);
                var elementBounds = PlateBounds(coveredPlate);
                if (plateRequiredAs <= 0.01 ||
                    elementBounds.MaxX < Math.Min(originalBounds.MinX, replacementBounds.MinX) - paddingM ||
                    elementBounds.MinX > Math.Max(originalBounds.MaxX, replacementBounds.MaxX) + paddingM ||
                    elementBounds.MaxY < Math.Min(originalBounds.MinY, replacementBounds.MinY) - paddingM ||
                    elementBounds.MinY > Math.Max(originalBounds.MaxY, replacementBounds.MaxY) + paddingM ||
                    !HasCoverage(zones, layer, coveredPlate, plateRequiredAs, slab, openings))
                    continue;
                if (!HasCoverage(proposedZones, layer, coveredPlate, plateRequiredAs, slab, openings)) return null;
            }

            foreach (var plate in plates)
            {
                if (!IntersectsBounds(copy, plate) || IntersectsBounds(moving, plate)) continue;
                // A rod still runs through low-As mesh along its unchanged longitudinal span.
                if (!plate.Rebar.Ok) return null;
                var plateRequiredAs = plate.Rebar.Get(layer) - BackgroundAs(settings, layer);
                if (plateRequiredAs <= 0.01) continue;
                if (copy.AsCoveredCm2PerM + 1e-6 < plateRequiredAs &&
                    !HasCoverage(proposedZones, layer, plate, plateRequiredAs, slab, openings))
                    return null;
            }

            foreach (var other in zones.Where(zone => !ReferenceEquals(zone, moving) && zone.Layer == layer))
                if (!HasPlacementConflict(moving, other) && HasPlacementConflict(copy, other))
                    return null;

            copy.NodeIds = copy.NodeIds.Where(id => platesById.TryGetValue(id, out var existingPlate) &&
                    IntersectsBounds(copy, existingPlate)).Concat(plates.Where(plate => plate.Rebar.Ok &&
                    plate.Rebar.Get(layer) - BackgroundAs(settings, layer) >
                    MosaicBuilder.PositiveResidualToleranceCm2PerM && IntersectsBounds(copy, plate))
                .Select(plate => plate.Id)).Distinct().ToList();
            copy.ElementId = copy.NodeIds.FirstOrDefault();
            copy.Comment = "сдвинуто для покрытия КЭ в зазоре шага";
            return (moving, copy);
        }

        private static bool TryShiftNeighborBoundaryForCoverage(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            IReadOnlyDictionary<int, LiraPlateElement> platesById,
            AnalysisSettings settings, IList<Point3> slab, IList<OpeningInfo> openings,
            LiraPlateElement target, RebarLayer layer, double requiredAs)
        {
            var targetBounds = PlateBounds(target);
            var targetLongMin = 0.0;
            var targetLongMax = 0.0;
            var targetCrossMin = 0.0;
            var targetCrossMax = 0.0;
            var candidates = zones.Where(zone => zone.Layer == layer &&
                    zone.Direction == RebarTables.DirectionForLayer(layer, settings.ReverseZoneDirections) &&
                    zone.Contour.Count >= 3 &&
                    zone.AsCoveredCm2PerM + 1e-6 >= requiredAs)
                .OrderBy(zone => ZoneDistanceToBounds(zone, targetBounds)).ToList();
            if (candidates.Count == 0) return false;

            foreach (var strong in candidates)
            foreach (var weak in zones.Where(zone => !ReferenceEquals(zone, strong) &&
                         zone.Layer == layer && zone.Direction == strong.Direction &&
                         zone.Contour.Count >= 3))
            {
                var strongBounds = Bounds(strong.Contour);
                var weakBounds = Bounds(weak.Contour);
                if (strong.Direction == ZoneDirection.X)
                {
                    targetLongMin = targetBounds.MinX;
                    targetLongMax = targetBounds.MaxX;
                    targetCrossMin = targetBounds.MinY;
                    targetCrossMax = targetBounds.MaxY;
                }
                else
                {
                    targetLongMin = targetBounds.MinY;
                    targetLongMax = targetBounds.MaxY;
                    targetCrossMin = targetBounds.MinX;
                    targetCrossMax = targetBounds.MaxX;
                }

                var strongLongMin = strong.Direction == ZoneDirection.X ? strongBounds.MinX : strongBounds.MinY;
                var strongLongMax = strong.Direction == ZoneDirection.X ? strongBounds.MaxX : strongBounds.MaxY;
                var weakLongMin = weak.Direction == ZoneDirection.X ? weakBounds.MinX : weakBounds.MinY;
                var weakLongMax = weak.Direction == ZoneDirection.X ? weakBounds.MaxX : weakBounds.MaxY;
                if (targetLongMin < Math.Max(strongLongMin, weakLongMin) - 1e-6 ||
                    targetLongMax > Math.Min(strongLongMax, weakLongMax) + 1e-6)
                    continue;

                var strongCrossMin = strong.Direction == ZoneDirection.X ? strongBounds.MinY : strongBounds.MinX;
                var strongCrossMax = strong.Direction == ZoneDirection.X ? strongBounds.MaxY : strongBounds.MaxX;
                var weakCrossMin = weak.Direction == ZoneDirection.X ? weakBounds.MinY : weakBounds.MinX;
                var weakCrossMax = weak.Direction == ZoneDirection.X ? weakBounds.MaxY : weakBounds.MaxX;
                var strongIsLower = strongCrossMax <= weakCrossMin + 1e-6;
                var strongIsUpper = weakCrossMax <= strongCrossMin + 1e-6;
                if ((!strongIsLower && !strongIsUpper) || HasPlacementConflict(strong, weak)) continue;

                var gapM = strongIsLower ? weakCrossMin - strongCrossMax : strongCrossMin - weakCrossMax;
                var allowedGapM = UnitConversion.MmToMeters(Math.Min(strong.BarStepMm, weak.BarStepMm));
                if (gapM < allowedGapM - 1e-6 || gapM > allowedGapM + 1e-6) continue;

                var extensionMm = strongIsLower
                    ? UnitConversion.MetersToMm(targetCrossMax - strongCrossMax)
                    : UnitConversion.MetersToMm(strongCrossMin - targetCrossMin);
                if (extensionMm <= 1e-3) continue;
                var commonStepMm = LeastCommonMultiple(strong.BarStepMm, weak.BarStepMm);
                var shiftM = RoundUpToBarStep(UnitConversion.MmToMeters(extensionMm), commonStepMm);
                if (shiftM <= 1e-6) continue;

                var nextStrong = Copy(strong);
                var nextWeak = Copy(weak);
                var nextStrongMinX = strongBounds.MinX;
                var nextStrongMaxX = strongBounds.MaxX;
                var nextStrongMinY = strongBounds.MinY;
                var nextStrongMaxY = strongBounds.MaxY;
                var nextWeakMinX = weakBounds.MinX;
                var nextWeakMaxX = weakBounds.MaxX;
                var nextWeakMinY = weakBounds.MinY;
                var nextWeakMaxY = weakBounds.MaxY;
                if (strong.Direction == ZoneDirection.X)
                {
                    if (strongIsLower)
                    {
                        nextStrongMaxY += shiftM;
                        nextWeakMinY += shiftM;
                    }
                    else
                    {
                        nextStrongMinY -= shiftM;
                        nextWeakMaxY -= shiftM;
                    }
                }
                else if (strongIsLower)
                {
                    nextStrongMaxX += shiftM;
                    nextWeakMinX += shiftM;
                }
                else
                {
                    nextStrongMinX -= shiftM;
                    nextWeakMaxX -= shiftM;
                }

                if (!ApplyClipped(nextStrong,
                        Rectangle(nextStrongMinX, nextStrongMaxX, nextStrongMinY, nextStrongMaxY, strong.LevelZM), slab) ||
                    !ApplyClipped(nextWeak,
                        Rectangle(nextWeakMinX, nextWeakMaxX, nextWeakMinY, nextWeakMaxY, weak.LevelZM), slab) ||
                    nextStrong.LengthMm > 11701 || nextWeak.LengthMm > 11701 ||
                    (settings.MaxZoneWidthM > 0 &&
                     (nextStrong.WidthM > settings.MaxZoneWidthM + 1e-6 ||
                      nextWeak.WidthM > settings.MaxZoneWidthM + 1e-6)) ||
                    IntersectsOpening(nextStrong, openings) || IntersectsOpening(nextWeak, openings) ||
                    HasPlacementConflict(nextStrong, nextWeak))
                    continue;

                var strongIndex = zones.IndexOf(strong);
                var weakIndex = zones.IndexOf(weak);
                if (strongIndex < 0 || weakIndex < 0) continue;
                var proposed = zones.ToList();
                proposed[strongIndex] = nextStrong;
                proposed[weakIndex] = nextWeak;
                if (!HasCoverage(proposed, layer, target, requiredAs, slab, openings)) continue;

                var nextStrongBounds = Bounds(nextStrong.Contour);
                var nextWeakBounds = Bounds(nextWeak.Contour);
                var strongNearBefore = strongIsLower ? strongCrossMax : strongCrossMin;
                var weakNearBefore = strongIsLower ? weakCrossMin : weakCrossMax;
                var strongNearAfter = strong.Direction == ZoneDirection.X
                    ? (strongIsLower ? nextStrongBounds.MaxY : nextStrongBounds.MinY)
                    : (strongIsLower ? nextStrongBounds.MaxX : nextStrongBounds.MinX);
                var weakNearAfter = weak.Direction == ZoneDirection.X
                    ? (strongIsLower ? nextWeakBounds.MinY : nextWeakBounds.MaxY)
                    : (strongIsLower ? nextWeakBounds.MinX : nextWeakBounds.MaxX);
                var changedCrossMin = Math.Min(Math.Min(strongNearBefore, weakNearBefore),
                    Math.Min(strongNearAfter, weakNearAfter));
                var changedCrossMax = Math.Max(Math.Max(strongNearBefore, weakNearBefore),
                    Math.Max(strongNearAfter, weakNearAfter));
                var changedLongMin = Math.Max(strongLongMin, weakLongMin);
                var changedLongMax = Math.Min(strongLongMax, weakLongMax);
                var changedBand = strong.Direction == ZoneDirection.X
                    ? (MinX: changedLongMin, MaxX: changedLongMax,
                       MinY: changedCrossMin, MaxY: changedCrossMax)
                    : (MinX: changedCrossMin, MaxX: changedCrossMax,
                       MinY: changedLongMin, MaxY: changedLongMax);
                var relevantPlates = plates.Where(candidate => candidate.Rebar.Ok &&
                    candidate.Rebar.Get(layer) - BackgroundAs(settings, layer) >
                    MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                    BoundsIntersect(PlateBounds(candidate), changedBand)).ToList();
                var losesCoverage = relevantPlates.Any(candidate =>
                {
                    var candidateRequiredAs = candidate.Rebar.Get(layer) - BackgroundAs(settings, layer);
                    return HasCoverage(zones, layer, candidate, candidateRequiredAs, slab, openings) &&
                           !HasCoverage(proposed, layer, candidate, candidateRequiredAs, slab, openings);
                });
                if (losesCoverage) continue;

                var createsConflict = new[] { (Original: strong, Replacement: nextStrong),
                    (Original: weak, Replacement: nextWeak) }.Any(pair => zones.Any(other =>
                    !ReferenceEquals(other, strong) && !ReferenceEquals(other, weak) &&
                    !HasPlacementConflict(pair.Original, other) &&
                    HasPlacementConflict(pair.Replacement, other)));
                if (createsConflict) continue;

                foreach (var copy in new[] { nextStrong, nextWeak })
                {
                    copy.NodeIds = copy.NodeIds.Where(id => platesById.TryGetValue(id, out var existing) &&
                            IntersectsBounds(copy, existing))
                        .Concat(relevantPlates.Where(candidate => IntersectsBounds(copy, candidate))
                            .Select(candidate => candidate.Id)).Distinct().ToList();
                    copy.ElementId = copy.NodeIds.FirstOrDefault();
                    copy.Comment = "граница пары зон смещена для полного покрытия КЭ";
                }
                zones[strongIndex] = nextStrong;
                zones[weakIndex] = nextWeak;
                return true;
            }
            return false;
        }

        private static double ZoneDistanceToBounds(
            AdditionalZone zone, (double MinX, double MaxX, double MinY, double MaxY) target)
        {
            var bounds = Bounds(zone.Contour);
            var dx = Math.Max(0, Math.Max(bounds.MinX - target.MaxX, target.MinX - bounds.MaxX));
            var dy = Math.Max(0, Math.Max(bounds.MinY - target.MaxY, target.MinY - bounds.MaxY));
            return dx * dx + dy * dy;
        }

        private static bool BoundsIntersect(
            (double MinX, double MaxX, double MinY, double MaxY) first,
            (double MinX, double MaxX, double MinY, double MaxY) second) =>
            first.MaxX >= second.MinX - 1e-6 && first.MinX <= second.MaxX + 1e-6 &&
            first.MaxY >= second.MinY - 1e-6 && first.MinY <= second.MaxY + 1e-6;

        private static int LeastCommonMultiple(int first, int second)
        {
            var a = Math.Max(1, first);
            var b = Math.Max(1, second);
            var x = a;
            var y = b;
            while (y != 0)
            {
                var remainder = x % y;
                x = y;
                y = remainder;
            }
            return Math.Max(1, a / x * b);
        }

        private static bool HasCoverage(
            IList<AdditionalZone> zones, RebarLayer layer, Point3 point, double requiredAs)
        {
            var adequate = zones.Where(zone => zone.Layer == layer &&
                zone.AsCoveredCm2PerM + 1e-6 >= requiredAs).ToList();
            return ZoneCoverageRules.CoversOrBridgesGap(adequate, point, requiredAs);
        }

        private static bool HasCoverage(
            IList<AdditionalZone> zones, RebarLayer layer, LiraPlateElement plate, double requiredAs,
            IList<Point3>? slabOutline = null, IList<OpeningInfo>? openings = null) =>
            ZoneCoverageRules.CoversOrBridgesGap(zones, plate, layer, requiredAs, slabOutline, openings);

        private static double RoundUpToBarStep(double extentM, int stepMm) =>
            UnitConversion.MmToMeters(Math.Ceiling(
                (UnitConversion.MetersToMm(Math.Max(0, extentM)) - 1e-6) / Math.Max(1, stepMm)) *
                Math.Max(1, stepMm));

        private static bool LayerEnabled(RebarLayer layer, AnalysisSettings settings) => layer switch
        {
            RebarLayer.As1 => settings.ShowAs1,
            RebarLayer.As2 => settings.ShowAs2,
            RebarLayer.As3 => settings.ShowAs3,
            RebarLayer.As4 => settings.ShowAs4,
            _ => false
        };

        public static void RemoveCoveredOverlapZones(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            AnalysisSettings? settings = null) =>
            RemoveCoveredOverlapZones(zones, plates, settings, null, null);

        public static void RemoveCoveredOverlapZones(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            AnalysisSettings? settings, IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            var platesById = plates.ToDictionary(plate => plate.Id);

            bool IsCoveredWithout(AdditionalZone candidate) => candidate.NodeIds.Count > 0 &&
                candidate.NodeIds.All(id =>
                {
                    if (!platesById.TryGetValue(id, out var plate)) return false;
                    var requiredAs = plate.Rebar.Get(candidate.Layer) - BackgroundAs(settings, candidate.Layer);
                    if (!plate.Rebar.Ok || requiredAs <= MosaicBuilder.PositiveResidualToleranceCm2PerM) return true;
                    var otherZones = zones.Where(other => !ReferenceEquals(other, candidate) &&
                        other.Layer == candidate.Layer &&
                        other.AsCoveredCm2PerM + 1e-6 >= requiredAs).ToList();
                    return ZoneCoverageRules.CoversOrBridgesGap(
                        otherZones, plate, candidate.Layer, requiredAs, slabOutline, openings);
                });

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < zones.Count && !changed; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var first = zones[i];
                    var second = zones[j];
                    if (first.Layer != second.Layer || first.Contour.Count < 3 || second.Contour.Count < 3 ||
                        !HasPlacementConflict(first, second))
                        continue;

                    var a = Bounds(first.Contour);
                    var b = Bounds(second.Contour);
                    var overlapX = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
                    var overlapY = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
                    if (overlapX <= 1e-6 || overlapY <= 1e-6) continue;

                    var removable = first.NodeIds.Count <= second.NodeIds.Count ? first : second;
                    var other = ReferenceEquals(removable, first) ? second : first;
                    if (!IsCoveredWithout(removable))
                    {
                        removable = other;
                        if (!IsCoveredWithout(removable)) continue;
                    }

                    zones.Remove(removable);
                    changed = true;
                    break;
                }
            }
        }

        public static void RemoveCoveredOverlapZones(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            AnalysisSettings? settings, IList<Point3>? slabOutline) =>
            RemoveCoveredOverlapZones(zones, plates, settings, slabOutline, null);

        private static bool TryResolvePlacementConflict(
            IList<AdditionalZone> zones, AdditionalZone first, AdditionalZone second,
            IReadOnlyDictionary<int, LiraPlateElement> platesById,
            AnalysisSettings? settings, IList<Point3>? slab, IList<OpeningInfo>? openings,
            bool requireExactGap = false)
        {
            var gap = Math.Min(first.BarStepMm, second.BarStepMm) / 1000.0;
            var a = Bounds(first.Contour);
            var b = Bounds(second.Contour);
            var candidates = new List<(AdditionalZone Moving, AdditionalZone Reference, double Dx, double Dy)>();

            void AddCandidates(
                AdditionalZone moving, AdditionalZone reference,
                (double MinX, double MaxX, double MinY, double MaxY) source,
                (double MinX, double MaxX, double MinY, double MaxY) target)
            {
                candidates.Add((moving, reference, target.MinX - gap - source.MaxX, 0));
                candidates.Add((moving, reference, target.MaxX + gap - source.MinX, 0));
                candidates.Add((moving, reference, 0, target.MinY - gap - source.MaxY));
                candidates.Add((moving, reference, 0, target.MaxY + gap - source.MinY));
            }

            AddCandidates(first, second, a, b);
            AddCandidates(second, first, b, a);

            bool IsSafe(AdditionalZone copy, AdditionalZone moving, AdditionalZone reference) =>
                !HasPlacementConflict(copy, reference) &&
                (!requireExactGap || HasExactAdjacentGap(copy, reference, gap)) &&
                (openings == null || openings.Count == 0 || !IntersectsOpening(copy, openings)) &&
                PreservesRequiredCoverage(copy, moving, zones, platesById, settings, slab, openings) &&
                !zones.Any(other =>
                    !ReferenceEquals(other, moving) && !ReferenceEquals(other, reference) &&
                    other.Layer == moving.Layer && !HasPlacementConflict(moving, other) &&
                    HasPlacementConflict(copy, other));

            foreach (var candidate in candidates
                         .OrderBy(item => Math.Abs(item.Dx) + Math.Abs(item.Dy))
                         .ThenBy(item => item.Moving.NodeIds.Count))
            {
                var moving = candidate.Moving;
                var reference = candidate.Reference;
                var copy = Copy(moving);
                var shifted = moving.Contour.Select(point =>
                    new Point3(point.X + candidate.Dx, point.Y + candidate.Dy, point.Z)).ToList();
                if (!ApplyClipped(copy, shifted, slab ?? new List<Point3>()) ||
                    !IsSafe(copy, moving, reference)) continue;

                SetContour(moving, copy.Contour.ToList());
                moving.NodeIds = moving.NodeIds.Where(id => platesById.TryGetValue(id, out var plate) &&
                    Covers(moving, plate.Centroid)).ToList();
                if (moving.NodeIds.Count == 0)
                    moving.NodeIds = platesById.Values.Where(plate => plate.Rebar.Ok &&
                        plate.Rebar.Get(moving.Layer) - BackgroundAs(settings, moving.Layer) >
                        MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                        Covers(moving, plate.Centroid)).Select(plate => plate.Id).ToList();
                moving.ElementId = moving.NodeIds.FirstOrDefault();
                moving.Comment = $"зазор {Math.Min(moving.BarStepMm, reference.BarStepMm)} мм создан автоматически";
                return true;
            }

            return TryShrinkAndShiftForConflict(
                zones, first, second, platesById, settings, slab, openings);
        }

        private static bool TryShrinkAndShiftForConflict(
            IList<AdditionalZone> zones,
            AdditionalZone first,
            AdditionalZone second,
            IReadOnlyDictionary<int, LiraPlateElement> platesById,
            AnalysisSettings? settings,
            IList<Point3>? slab,
            IList<OpeningInfo>? openings)
        {
            if (first.Direction != second.Direction || first.BarStepMm <= 0 ||
                second.BarStepMm <= 0) return false;

            var eligible = new List<(AdditionalZone Moving, AdditionalZone Reference)>();
            if (CanShrinkForConflict(first, second)) eligible.Add((first, second));
            if (CanShrinkForConflict(second, first)) eligible.Add((second, first));
            foreach (var pair in eligible.OrderBy(item => item.Moving.AsAdditional)
                         .ThenBy(item => item.Moving.LengthMm))
            {
                var moving = pair.Moving;
                var reference = pair.Reference;
                var stepMm = moving.BarStepMm;
                var originalBounds = Bounds(moving.Contour);
                var referenceBounds = Bounds(reference.Contour);
                var originalCross = moving.Direction == ZoneDirection.X
                    ? (Min: originalBounds.MinY, Max: originalBounds.MaxY)
                    : (Min: originalBounds.MinX, Max: originalBounds.MaxX);
                var referenceCross = moving.Direction == ZoneDirection.X
                    ? (Min: referenceBounds.MinY, Max: referenceBounds.MaxY)
                    : (Min: referenceBounds.MinX, Max: referenceBounds.MaxX);
                var axialOverlap = moving.Direction == ZoneDirection.X
                    ? Math.Min(originalBounds.MaxX, referenceBounds.MaxX) -
                      Math.Max(originalBounds.MinX, referenceBounds.MinX)
                    : Math.Min(originalBounds.MaxY, referenceBounds.MaxY) -
                      Math.Max(originalBounds.MinY, referenceBounds.MinY);
                if (axialOverlap <= 1e-6) continue;

                var currentModules = (int)Math.Floor(
                    UnitConversion.MetersToMm(originalCross.Max - originalCross.Min) / stepMm + 1e-8);
                var configuredMinimumMm = settings == null
                    ? stepMm
                    : settings.EffectiveMinZoneWidthM * 1000.0;
                var minimumModules = Math.Max(1, (int)Math.Ceiling(
                    Math.Max(configuredMinimumMm, stepMm) / stepMm - 1e-8));
                var requiredGapM = Math.Min(moving.BarStepMm, reference.BarStepMm) / 1000.0;
                if (currentModules <= minimumModules) continue;

                for (var modules = currentModules - 1; modules >= minimumModules; modules--)
                {
                    var widthM = UnitConversion.MmToMeters(modules * stepMm);
                    var placements = new[]
                    {
                        (Min: referenceCross.Min - requiredGapM - widthM,
                         Max: referenceCross.Min - requiredGapM),
                        (Min: referenceCross.Max + requiredGapM,
                         Max: referenceCross.Max + requiredGapM + widthM)
                    }.OrderBy(interval => Math.Abs(
                        (interval.Min + interval.Max - originalCross.Min - originalCross.Max) * 0.5))
                     .ToList();

                    foreach (var cross in placements)
                    {
                        var copy = Copy(moving);
                        var candidateBounds = originalBounds;
                        if (moving.Direction == ZoneDirection.X)
                        {
                            candidateBounds.MinY = cross.Min;
                            candidateBounds.MaxY = cross.Max;
                        }
                        else
                        {
                            candidateBounds.MinX = cross.Min;
                            candidateBounds.MaxX = cross.Max;
                        }

                        if (!ApplyClipped(copy, Rectangle(candidateBounds.MinX,
                                candidateBounds.MaxX, candidateBounds.MinY,
                                candidateBounds.MaxY, moving.LevelZM), slab ?? new List<Point3>()) ||
                            Math.Abs(copy.WidthMm / stepMm -
                                     Math.Round(copy.WidthMm / stepMm)) > 0.001 ||
                            copy.WidthMm + 1e-6 < minimumModules * stepMm ||
                            HasPlacementConflict(copy, reference) ||
                            !HasExactAdjacentGap(copy, reference, requiredGapM) ||
                            (openings != null && openings.Count > 0 && IntersectsOpening(copy, openings)) ||
                            !PreservesRequiredCoverage(copy, moving, zones, platesById,
                                settings, slab, openings))
                            continue;

                        var proposed = zones.Where(zone => !ReferenceEquals(zone, moving))
                            .Concat(new[] { copy }).ToList();
                        var createsConflict = zones.Any(other =>
                            !ReferenceEquals(other, moving) && !ReferenceEquals(other, reference) &&
                            other.Layer == moving.Layer && !HasPlacementConflict(moving, other) &&
                            HasPlacementConflict(copy, other));
                        if (createsConflict) continue;

                        var oldBounds = Bounds(moving.Contour);
                        var replacementBounds = Bounds(copy.Contour);
                        var padding = UnitConversion.MmToMeters(Math.Max(
                            moving.BarStepMm, reference.BarStepMm));
                        var losesNearbyCoverage = platesById.Values.Any(plate =>
                        {
                            if (!plate.Rebar.Ok ||
                                plate.Rebar.Get(moving.Layer) - BackgroundAs(settings, moving.Layer) <=
                                    MosaicBuilder.PositiveResidualToleranceCm2PerM)
                                return false;
                            var plateBounds = PlateBounds(plate);
                            if (plateBounds.MaxX < Math.Min(oldBounds.MinX, replacementBounds.MinX) - padding ||
                                plateBounds.MinX > Math.Max(oldBounds.MaxX, replacementBounds.MaxX) + padding ||
                                plateBounds.MaxY < Math.Min(oldBounds.MinY, replacementBounds.MinY) - padding ||
                                plateBounds.MinY > Math.Max(oldBounds.MaxY, replacementBounds.MaxY) + padding)
                                return false;
                            var requiredAs = plate.Rebar.Get(moving.Layer) -
                                             BackgroundAs(settings, moving.Layer);
                            return HasCoverage(zones, moving.Layer, plate, requiredAs, slab, openings) &&
                                   !HasCoverage(proposed, moving.Layer, plate, requiredAs, slab, openings);
                        });
                        if (losesNearbyCoverage) continue;

                        SetContour(moving, copy.Contour.ToList());
                        moving.NodeIds = moving.NodeIds.Where(id =>
                                platesById.TryGetValue(id, out var plate) && IntersectsBounds(moving, plate))
                            .Concat(platesById.Values.Where(plate => plate.Rebar.Ok &&
                                    plate.Rebar.Get(moving.Layer) - BackgroundAs(settings, moving.Layer) >
                                        MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                                    moving.AsCoveredCm2PerM + 1e-6 >=
                                        plate.Rebar.Get(moving.Layer) - BackgroundAs(settings, moving.Layer) &&
                                    IntersectsBounds(moving, plate))
                                .Select(plate => plate.Id)).Distinct().ToList();
                        moving.ElementId = moving.NodeIds.FirstOrDefault();
                        moving.Comment = $"ширина уменьшена на {currentModules * stepMm - modules * stepMm} мм; " +
                                         $"создан зазор {requiredGapM * 1000:0} мм с сохранением покрытия КЭ";
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool CanShrinkForConflict(AdditionalZone moving, AdditionalZone reference)
        {
            if (moving.AsAdditional < reference.AsAdditional - 1e-6) return true;
            return Math.Abs(moving.AsAdditional - reference.AsAdditional) <= 1e-6 &&
                   moving.DiameterMm == reference.DiameterMm &&
                   moving.BarStepMm == reference.BarStepMm &&
                   moving.LengthMm < reference.LengthMm - 1;
        }

        private static bool HasExactAdjacentGap(
            AdditionalZone first, AdditionalZone second, double requiredGapM)
        {
            if (first.Layer != second.Layer || first.Direction != second.Direction)
                return false;
            var a = Bounds(first.Contour);
            var b = Bounds(second.Contour);
            var longitudinalOverlap = first.Direction == ZoneDirection.X
                ? Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX)
                : Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            if (longitudinalOverlap <= 1e-6) return false;
            var gap = first.Direction == ZoneDirection.X
                ? Math.Max(0, Math.Max(a.MinY, b.MinY) - Math.Min(a.MaxY, b.MaxY))
                : Math.Max(0, Math.Max(a.MinX, b.MinX) - Math.Min(a.MaxX, b.MaxX));
            return Math.Abs(gap - requiredGapM) <= 0.001;
        }

        private static bool TryExpandWeakerZoneIntoGap(
            IList<AdditionalZone> zones, AdditionalZone first, AdditionalZone second,
            IReadOnlyDictionary<int, LiraPlateElement> platesById,
            AnalysisSettings? settings, IList<Point3>? slab, IList<OpeningInfo>? openings)
        {
            if (first.Layer != second.Layer || first.Direction != second.Direction ||
                first.FamilyKind != ZoneFamilyKind.Straight ||
                second.FamilyKind != ZoneFamilyKind.Straight ||
                first.BarStepMm <= 0 || second.BarStepMm <= 0)
                return false;

            var a = Bounds(first.Contour);
            var b = Bounds(second.Contour);
            var axialOverlap = first.Direction == ZoneDirection.X
                ? Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX)
                : Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            if (axialOverlap <= 1e-6) return false;

            var firstCrossMin = first.Direction == ZoneDirection.X ? a.MinY : a.MinX;
            var firstCrossMax = first.Direction == ZoneDirection.X ? a.MaxY : a.MaxX;
            var secondCrossMin = first.Direction == ZoneDirection.X ? b.MinY : b.MinX;
            var secondCrossMax = first.Direction == ZoneDirection.X ? b.MaxY : b.MaxX;
            AdditionalZone left;
            AdditionalZone right;
            if (firstCrossMax < secondCrossMin)
            {
                left = first;
                right = second;
            }
            else if (secondCrossMax < firstCrossMin)
            {
                left = second;
                right = first;
            }
            else
            {
                return false;
            }

            var gap = (right.Direction == ZoneDirection.X
                ? Bounds(right.Contour).MinY - Bounds(left.Contour).MaxY
                : Bounds(right.Contour).MinX - Bounds(left.Contour).MaxX);
            var requiredGap = UnitConversion.MmToMeters(Math.Min(first.BarStepMm, second.BarStepMm));
            var expansionNeededMm = UnitConversion.MetersToMm(gap - requiredGap);
            if (expansionNeededMm <= 1) return false;

            foreach (var moving in new[] { left, right }
                         .OrderBy(zone => zone.AsCoveredCm2PerM)
                         .ThenBy(zone => zone.NodeIds.Count))
            {
                var step = moving.BarStepMm;
                var bounds = Bounds(moving.Contour);
                var currentWidthMm = UnitConversion.MetersToMm(moving.Direction == ZoneDirection.X
                    ? bounds.MaxY - bounds.MinY : bounds.MaxX - bounds.MinX);
                var roundedWidthMm = RebarTables.CeilToStep(currentWidthMm, step);
                var addedModules = Math.Max(1, (int)Math.Ceiling((expansionNeededMm - 1e-6) / step));
                var targetWidthMm = roundedWidthMm + addedModules * step;
                var targetWidthM = UnitConversion.MmToMeters(targetWidthMm);
                var minX = bounds.MinX;
                var maxX = bounds.MaxX;
                var minY = bounds.MinY;
                var maxY = bounds.MaxY;

                if (ReferenceEquals(moving, left))
                {
                    if (moving.Direction == ZoneDirection.X)
                        maxY = Bounds(right.Contour).MinY - requiredGap;
                    else
                        maxX = Bounds(right.Contour).MinX - requiredGap;
                }
                else
                {
                    if (moving.Direction == ZoneDirection.X)
                        minY = Bounds(left.Contour).MaxY + requiredGap;
                    else
                        minX = Bounds(left.Contour).MaxX + requiredGap;
                }

                if (moving.Direction == ZoneDirection.X)
                {
                    if (ReferenceEquals(moving, left)) minY = maxY - targetWidthM;
                    else maxY = minY + targetWidthM;
                }
                else
                {
                    if (ReferenceEquals(moving, left)) minX = maxX - targetWidthM;
                    else maxX = minX + targetWidthM;
                }

                var copy = Copy(moving);
                SetContour(copy, Rectangle(minX, maxX, minY, maxY, copy.LevelZM));
                if (!ApplyClipped(copy, copy.Contour.ToList(), slab ?? new List<Point3>())) continue;
                var actualWidthMm = UnitConversion.MetersToMm(copy.Direction == ZoneDirection.X
                    ? copy.Contour.Max(point => point.Y) - copy.Contour.Min(point => point.Y)
                    : copy.Contour.Max(point => point.X) - copy.Contour.Min(point => point.X));
                if (Math.Abs(actualWidthMm - targetWidthMm) > 1 ||
                    !HasExactAdjacentGap(copy, ReferenceEquals(moving, left) ? right : left, requiredGap))
                    continue;

                if ((openings?.Count ?? 0) > 0 && IntersectsOpening(copy, openings!)) continue;
                if (!PreservesRequiredCoverage(copy, moving, zones, platesById,
                        settings, slab, openings)) continue;

                var proposedZones = zones.Where(zone => zone.Layer == moving.Layer &&
                    !ReferenceEquals(zone, moving)).Concat(new[] { copy }).ToList();
                var coverageValid = platesById.Values
                    .Where(plate => plate.Rebar.Ok &&
                        plate.Rebar.Get(moving.Layer) - BackgroundAs(settings, moving.Layer) >
                            MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                        IntersectsBounds(copy, plate))
                    .All(plate => ZoneCoverageRules.CoversOrBridgesGap(
                        proposedZones, plate, moving.Layer,
                        plate.Rebar.Get(moving.Layer) - BackgroundAs(settings, moving.Layer),
                        slab, openings));
                if (!coverageValid || zones.Any(other =>
                        !ReferenceEquals(other, moving) && other.Layer == moving.Layer &&
                        HasPlacementConflict(copy, other)))
                    continue;

                copy.NodeIds = platesById.Values.Where(plate => plate.Rebar.Ok &&
                        plate.Rebar.Get(moving.Layer) - BackgroundAs(settings, moving.Layer) >
                            MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                        copy.AsCoveredCm2PerM + 1e-6 >=
                            plate.Rebar.Get(moving.Layer) - BackgroundAs(settings, moving.Layer) &&
                        Covers(copy, plate.Centroid))
                    .Select(plate => plate.Id).Distinct().ToList();
                copy.ElementId = copy.NodeIds.FirstOrDefault();
                copy.Comment = AppendComment(copy.Comment,
                    $"ширина увеличена на {addedModules * step} мм для точного зазора {Math.Min(first.BarStepMm, second.BarStepMm)} мм");
                SetContour(moving, copy.Contour.ToList());
                moving.NodeIds = copy.NodeIds;
                moving.ElementId = copy.ElementId;
                moving.Comment = copy.Comment;
                return true;
            }

            return false;
        }

        private static bool PreservesRequiredCoverage(
            AdditionalZone candidate, AdditionalZone original, IList<AdditionalZone> zones,
            IReadOnlyDictionary<int, LiraPlateElement> platesById, AnalysisSettings? settings,
            IList<Point3>? slabOutline = null, IList<OpeningInfo>? openings = null)
        {
            var background = BackgroundAs(settings, original.Layer);
            var nearbyPlates = platesById.Values.Where(plate => plate.Rebar.Ok &&
                plate.Rebar.Get(original.Layer) - background > 0.01 &&
                (original.NodeIds.Contains(plate.Id) || IntersectsBounds(original, plate))).ToList();
            var targets = nearbyPlates.Where(plate => ZoneCoverageRules.CoversOrBridgesGap(
                zones, plate, original.Layer,
                plate.Rebar.Get(original.Layer) - background, slabOutline, openings)).ToList();

            var otherZones = zones.Where(zone => !ReferenceEquals(zone, original) &&
                zone.Layer == original.Layer && zone.Contour.Count >= 3).ToList();
            var proposedZones = otherZones.Concat(new[] { candidate }).ToList();
            return targets.All(plate =>
            {
                if (ZoneCoverageRules.CoversOrBridgesGap(
                        new[] { candidate }, plate, original.Layer,
                        plate.Rebar.Get(original.Layer) - background, slabOutline, openings)) return true;
                var requiredAs = plate.Rebar.Get(original.Layer) - background;
                var nearbyZones = proposedZones.Where(zone =>
                {
                    if (zone.AsCoveredCm2PerM + 1e-6 < requiredAs) return false;
                    var bounds = Bounds(zone.Contour);
                    var padding = Math.Max(0.1, zone.BarStepMm / 1000.0);
                    var elementBounds = PlateBounds(plate);
                    return elementBounds.MaxX >= bounds.MinX - padding &&
                           elementBounds.MinX <= bounds.MaxX + padding &&
                           elementBounds.MaxY >= bounds.MinY - padding &&
                           elementBounds.MinY <= bounds.MaxY + padding;
                }).ToList();
                return ZoneCoverageRules.CoversOrBridgesGap(
                    nearbyZones, plate, original.Layer, requiredAs, slabOutline, openings);
            });
        }

        private static double BackgroundAs(AnalysisSettings? settings, RebarLayer layer) => settings == null ? 0 : layer switch
        {
            RebarLayer.As1 => settings.AsMainAs1,
            RebarLayer.As2 => settings.AsMainAs2,
            RebarLayer.As3 => settings.AsMainAs3,
            RebarLayer.As4 => settings.AsMainAs4,
            _ => 0
        };

        public static void MergeLongitudinalConflicts(
            IList<AdditionalZone> zones, IList<Point3> slab)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < zones.Count && !changed; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var first = zones[i];
                    var second = zones[j];
                    if (first.Layer != second.Layer || first.Direction != second.Direction ||
                        first.DiameterMm != second.DiameterMm || first.BarStepMm != second.BarStepMm ||
                        first.FamilyKind != ZoneFamilyKind.Straight ||
                        second.FamilyKind != ZoneFamilyKind.Straight)
                        continue;
                    var a = Bounds(first.Contour);
                    var b = Bounds(second.Contour);
                    var longitudinalOverlap = first.Direction == ZoneDirection.X
                        ? Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX)
                        : Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
                    var transverseOverlap = first.Direction == ZoneDirection.X
                        ? Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY)
                        : Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
                    var minTransverse = first.Direction == ZoneDirection.X
                        ? Math.Min(a.MaxY - a.MinY, b.MaxY - b.MinY)
                        : Math.Min(a.MaxX - a.MinX, b.MaxX - b.MinX);
                    if (longitudinalOverlap <= 1e-6 ||
                        transverseOverlap < minTransverse * 0.8)
                        continue;
                    var merged = Merge(first, second, slab);
                    if (merged == null) continue;
                    SetContour(merged, Rectangle(
                        Math.Min(a.MinX, b.MinX), Math.Max(a.MaxX, b.MaxX),
                        Math.Min(a.MinY, b.MinY), Math.Max(a.MaxY, b.MaxY),
                        merged.LevelZM));
                    merged.Comment = "объединено перед обходом отверстия";
                    zones[i] = merged;
                    zones.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        public static void AbsorbNarrowSplitParts(
            IList<AdditionalZone> parts, double minimumWidthM)
        {
            if (minimumWidthM <= 0) return;
            var narrow = parts.Where(part => part.Contour.Count >= 3 &&
                part.FamilyKind == ZoneFamilyKind.Straight &&
                part.WidthM + 1e-6 < minimumWidthM).ToList();
            foreach (var sliver in narrow)
            {
                if (!parts.Contains(sliver)) continue;
                var a = Bounds(sliver.Contour);
                var target = parts.Where(candidate => !ReferenceEquals(candidate, sliver) &&
                        candidate.Layer == sliver.Layer &&
                        candidate.Direction == sliver.Direction &&
                        candidate.DiameterMm == sliver.DiameterMm &&
                        candidate.BarStepMm == sliver.BarStepMm &&
                        candidate.FamilyKind == ZoneFamilyKind.Straight)
                    .Select(candidate =>
                    {
                        var b = Bounds(candidate.Contour);
                        var longitudinalOverlap = sliver.Direction == ZoneDirection.X
                            ? Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX)
                            : Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
                        var transverseGap = sliver.Direction == ZoneDirection.X
                            ? Math.Max(0, Math.Max(a.MinY, b.MinY) - Math.Min(a.MaxY, b.MaxY))
                            : Math.Max(0, Math.Max(a.MinX, b.MinX) - Math.Min(a.MaxX, b.MaxX));
                        return new { Zone = candidate, Bounds = b, longitudinalOverlap, transverseGap };
                    })
                    .Where(item => item.longitudinalOverlap > 1e-6 &&
                        item.transverseGap <= UnitConversion.MmToMeters(sliver.BarStepMm) + 1e-6)
                    .OrderBy(item => item.transverseGap)
                    .FirstOrDefault();
                if (target == null) continue;
                SetContour(target.Zone, Rectangle(
                    Math.Min(a.MinX, target.Bounds.MinX), Math.Max(a.MaxX, target.Bounds.MaxX),
                    Math.Min(a.MinY, target.Bounds.MinY), Math.Max(a.MaxY, target.Bounds.MaxY),
                    target.Zone.LevelZM));
                target.Zone.NodeIds = target.Zone.NodeIds.Concat(sliver.NodeIds).Distinct().ToList();
                target.Zone.ElementId = target.Zone.NodeIds.FirstOrDefault();
                target.Zone.AsRequired = Math.Max(target.Zone.AsRequired, sliver.AsRequired);
                target.Zone.AsAdditional = Math.Max(target.Zone.AsAdditional, sliver.AsAdditional);
                parts.Remove(sliver);
            }
        }

        public static void EnforceMaximumDetailLength(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            IList<Point3> outline,
            int maximumLengthMm = 11700,
            bool allowBoundaryCornerOverrun = false)
        {
            var centroids = plates.ToDictionary(plate => plate.Id, plate => plate.Centroid);
            for (var index = zones.Count - 1; index >= 0; index--)
            {
                var source = zones[index];
                if (source.Contour.Count < 3 || source.LengthMm <= maximumLengthMm + 1)
                    continue;
                var bounds = Bounds(source.Contour);
                var bent = source.FamilyKind != ZoneFamilyKind.Straight;
                var legCount = source.FamilyKind is ZoneFamilyKind.PEqual or ZoneFamilyKind.PDiff ? 2 : 1;
                var maxPlanMm = maximumLengthMm -
                    (bent ? legCount * Math.Max(0, source.VerticalLegMm) : 0);
                if (maxPlanMm <= 50)
                {
                    source.IsValid = false;
                    source.StatusColor = "error";
                    source.Comment = "деталь превышает 11700 мм: вертикальные участки слишком велики";
                    continue;
                }

                var startMm = UnitConversion.MetersToMm(source.Direction == ZoneDirection.X
                    ? bounds.MinX : bounds.MinY);
                var endMm = UnitConversion.MetersToMm(source.Direction == ZoneDirection.X
                    ? bounds.MaxX : bounds.MaxY);
                var lapMm = Math.Min(maxPlanMm - 1,
                    2 * RebarTables.LapLenMm(source.ConcreteClass, source.DiameterMm));
                var piecePlanLimitMm = bent ? maxPlanMm - 1 : maxPlanMm;
                var ranges = new List<(double Start, double End)>();
                if (source.LengthMm - maximumLengthMm <= 50)
                {
                    var centerMm = (startMm + endMm) / 2.0;
                    ranges.Add((centerMm - piecePlanLimitMm / 2.0,
                        centerMm + piecePlanLimitMm / 2.0));
                }
                else
                {
                    var cursor = startMm;
                    while (endMm - cursor > piecePlanLimitMm + 1)
                    {
                        ranges.Add((cursor, cursor + piecePlanLimitMm));
                        cursor += piecePlanLimitMm - lapMm;
                    }
                    var remainingMm = endMm - cursor;
                    var lastPlanMm = bent
                        ? remainingMm
                        : Math.Min(piecePlanLimitMm, RebarTables.PickFamilyLength(remainingMm));
                    ranges.Add((endMm - lastPlanMm, endMm));
                }

                var replacements = new List<AdditionalZone>();
                foreach (var range in ranges)
                {
                    var copy = Copy(source);
                    var a = UnitConversion.MmToMeters(range.Start);
                    var b = UnitConversion.MmToMeters(range.End);
                    var minX = source.Direction == ZoneDirection.X ? a : bounds.MinX;
                    var maxX = source.Direction == ZoneDirection.X ? b : bounds.MaxX;
                    var minY = source.Direction == ZoneDirection.Y ? a : bounds.MinY;
                    var maxY = source.Direction == ZoneDirection.Y ? b : bounds.MaxY;
                    if (!allowBoundaryCornerOverrun &&
                        !RectangleInsideOutline(minX, maxX, minY, maxY, outline))
                    {
                        var sourcePoints = source.NodeIds
                            .Where(centroids.ContainsKey)
                            .Select(id => centroids[id]).ToList();
                        TryShiftRectangleInsideOutline(ref minX, ref maxX, ref minY, ref maxY,
                            source.Direction, outline, sourcePoints);
                    }
                    SetContour(copy, Rectangle(minX, maxX, minY, maxY, source.LevelZM));
                    if (copy.LengthMm > maximumLengthMm + 1)
                        continue;
                    copy.NodeIds = source.NodeIds.Where(id => centroids.TryGetValue(id, out var point) &&
                        point.X >= copy.Contour.Min(p => p.X) - 1e-6 &&
                        point.X <= copy.Contour.Max(p => p.X) + 1e-6 &&
                        point.Y >= copy.Contour.Min(p => p.Y) - 1e-6 &&
                        point.Y <= copy.Contour.Max(p => p.Y) + 1e-6).ToList();
                    copy.ElementId = copy.NodeIds.FirstOrDefault();
                    copy.Comment = "разделено по максимальной длине детали 11700 мм";
                    replacements.Add(copy);
                }
                if (replacements.Count == 0)
                {
                    source.IsValid = false;
                    source.StatusColor = "warn";
                    source.Comment = "превышение 11700 мм: не удалось построить части внутри плиты";
                    continue;
                }
                zones.RemoveAt(index);
                foreach (var replacement in replacements)
                    zones.Insert(index++, replacement);
                index -= replacements.Count;
            }
        }

        public static void MergeDominatedOpeningExtensions(IList<AdditionalZone> zones)
        {
            for (var i = zones.Count - 1; i >= 0; i--)
            {
                var weaker = zones[i];
                if (weaker.FamilyKind != ZoneFamilyKind.Straight || weaker.NodeIds.Count > 3)
                    continue;
                var weakBounds = Bounds(weaker.Contour);
                var candidate = zones.Where((stronger, index) => index != i &&
                        stronger.Layer == weaker.Layer && stronger.Direction == weaker.Direction &&
                        stronger.FamilyKind == ZoneFamilyKind.Straight &&
                        stronger.DiameterMm > weaker.DiameterMm &&
                        stronger.Comment?.IndexOf("обход отверстия", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(stronger =>
                    {
                        var bounds = Bounds(stronger.Contour);
                        var transverseOverlap = stronger.Direction == ZoneDirection.X
                            ? Math.Min(bounds.MaxY, weakBounds.MaxY) - Math.Max(bounds.MinY, weakBounds.MinY)
                            : Math.Min(bounds.MaxX, weakBounds.MaxX) - Math.Max(bounds.MinX, weakBounds.MinX);
                        var weakWidth = weaker.Direction == ZoneDirection.X
                            ? weakBounds.MaxY - weakBounds.MinY : weakBounds.MaxX - weakBounds.MinX;
                        var longitudinalGap = stronger.Direction == ZoneDirection.X
                            ? Math.Max(0, Math.Max(bounds.MinX, weakBounds.MinX) - Math.Min(bounds.MaxX, weakBounds.MaxX))
                            : Math.Max(0, Math.Max(bounds.MinY, weakBounds.MinY) - Math.Min(bounds.MaxY, weakBounds.MaxY));
                        return new { Zone = stronger, Bounds = bounds, transverseOverlap, weakWidth, longitudinalGap };
                    })
                    .Where(item => item.transverseOverlap >= item.weakWidth * 0.8 &&
                        item.longitudinalGap <= UnitConversion.MmToMeters(weaker.BarStepMm) + 1e-6)
                    .OrderBy(item => item.longitudinalGap)
                    .FirstOrDefault();
                if (candidate == null) continue;

                var strong = candidate.Zone;
                var longitudinalMin = strong.Direction == ZoneDirection.X
                    ? Math.Min(candidate.Bounds.MinX, weakBounds.MinX)
                    : Math.Min(candidate.Bounds.MinY, weakBounds.MinY);
                var longitudinalMax = strong.Direction == ZoneDirection.X
                    ? Math.Max(candidate.Bounds.MaxX, weakBounds.MaxX)
                    : Math.Max(candidate.Bounds.MaxY, weakBounds.MaxY);
                var familyMm = RebarTables.PickFamilyLength(
                    UnitConversion.MetersToMm(longitudinalMax - longitudinalMin));
                if (familyMm > 11700) continue;
                var center = (longitudinalMin + longitudinalMax) / 2.0;
                var half = UnitConversion.MmToMeters(familyMm) / 2.0;
                SetContour(strong, strong.Direction == ZoneDirection.X
                    ? Rectangle(center - half, center + half,
                        candidate.Bounds.MinY, candidate.Bounds.MaxY, strong.LevelZM)
                    : Rectangle(candidate.Bounds.MinX, candidate.Bounds.MaxX,
                        center - half, center + half, strong.LevelZM));
                strong.NodeIds = strong.NodeIds.Concat(weaker.NodeIds).Distinct().ToList();
                strong.ElementId = strong.NodeIds.FirstOrDefault();
                strong.Comment = "переходная зона объединена у отверстия";
                zones.RemoveAt(i);
            }
        }

        public static void NormalizeWidthsToBarStep(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            IList<Point3> outline, AnalysisSettings settings)
        {
            foreach (var zone in zones.Where(zone => zone.Contour.Count >= 3 &&
                zone.BarStepMm > 0 && zone.FamilyKind == ZoneFamilyKind.Straight))
            {
                var bounds = Bounds(zone.Contour);

                var background = BackgroundAs(settings, zone.Layer);
                var relevantPlates = plates
                    .Where(plate => plate.Rebar.Ok && plate.Rebar.Get(zone.Layer) - background > 0.01 &&
                        (zone.NodeIds.Contains(plate.Id) || IntersectsBounds(zone, plate)))
                    .ToList();
                var relevantPoints = relevantPlates.SelectMany(plate =>
                    plate.Contour != null && plate.Contour.Count >= 3
                        ? (IEnumerable<Point3>)plate.Contour
                        : new[] { plate.Centroid }).ToList();
                var perpendicularValues = relevantPoints.Select(point =>
                    zone.Direction == ZoneDirection.X ? point.Y : point.X).ToList();
                var coreMin = perpendicularValues.Count == 0
                    ? zone.Direction == ZoneDirection.X ? bounds.MinY : bounds.MinX
                    : perpendicularValues.Min();
                var coreMax = perpendicularValues.Count == 0
                    ? zone.Direction == ZoneDirection.X ? bounds.MaxY : bounds.MaxX
                    : perpendicularValues.Max();
                var minimumCoverWidthMm = UnitConversion.MetersToMm(coreMax - coreMin);
                var minimumConfiguredWidthMm = settings.EffectiveMinZoneWidthM * 1000.0;
                var modules = Math.Max(1, (int)Math.Ceiling(
                    (Math.Max(minimumConfiguredWidthMm, minimumCoverWidthMm) - 1e-6) / zone.BarStepMm));
                var targetWidthM = UnitConversion.MmToMeters(modules * zone.BarStepMm);
                var minX = bounds.MinX;
                var maxX = bounds.MaxX;
                var minY = bounds.MinY;
                var maxY = bounds.MaxY;
                if (zone.Direction == ZoneDirection.X)
                {
                    var center = (minY + maxY) / 2.0;
                    if (perpendicularValues.Count > 0)
                    {
                        var minCenter = coreMax - targetWidthM / 2.0;
                        var maxCenter = coreMin + targetWidthM / 2.0;
                        center = Math.Max(minCenter, Math.Min(maxCenter, center));
                    }
                    minY = center - targetWidthM / 2.0;
                    maxY = center + targetWidthM / 2.0;
                }
                else
                {
                    var center = (minX + maxX) / 2.0;
                    if (perpendicularValues.Count > 0)
                    {
                        var minCenter = coreMax - targetWidthM / 2.0;
                        var maxCenter = coreMin + targetWidthM / 2.0;
                        center = Math.Max(minCenter, Math.Min(maxCenter, center));
                    }
                    minX = center - targetWidthM / 2.0;
                    maxX = center + targetWidthM / 2.0;
                }
                if (!RectangleInsideOutline(minX, maxX, minY, maxY, outline))
                {
                    if (!TryShiftRectangleInsideOutline(ref minX, ref maxX, ref minY, ref maxY,
                            zone.Direction, outline, relevantPoints) ||
                        !RectangleInsideOutline(minX, maxX, minY, maxY, outline) ||
                        relevantPoints.Any(point => point.X < minX - 1e-6 || point.X > maxX + 1e-6 ||
                                                    point.Y < minY - 1e-6 || point.Y > maxY + 1e-6))
                    {
                        minX = bounds.MinX;
                        maxX = bounds.MaxX;
                        minY = bounds.MinY;
                        maxY = bounds.MaxY;
                    }
                }
                SetContour(zone, Rectangle(minX, maxX, minY, maxY, zone.LevelZM));
            }
        }

        public static void NormalizeBarArrayWidthsToStep(IList<AdditionalZone> zones)
        {
            foreach (var zone in zones.Where(zone => zone.Contour.Count >= 3 && zone.BarStepMm > 0))
            {
                var minX = zone.Contour.Min(point => point.X);
                var maxX = zone.Contour.Max(point => point.X);
                var minY = zone.Contour.Min(point => point.Y);
                var maxY = zone.Contour.Max(point => point.Y);
                var contourWidthMm = UnitConversion.MetersToMm(zone.Direction == ZoneDirection.X
                    ? maxY - minY
                    : maxX - minX);
                zone.BarCount = Math.Max(2,
                    (int)Math.Ceiling((contourWidthMm - 1e-6) / zone.BarStepMm) + 1);
                zone.WidthMm = (zone.BarCount - 1) * (double)zone.BarStepMm;
            }
        }

        private static bool RectangleInsideOutline(
            double minX, double maxX, double minY, double maxY,
            IList<Point3> outline)
        {
            if (outline == null || outline.Count < 3) return true;
            var centerX = (minX + maxX) / 2.0;
            var centerY = (minY + maxY) / 2.0;
            return Rectangle(minX, maxX, minY, maxY, 0).All(point =>
            {
                var x = point.X + (centerX - point.X) * 0.001;
                var y = point.Y + (centerY - point.Y) * 0.001;
                return MeshBoundary.PointInPolygon(x, y, outline);
            });
        }

        private static bool TryShiftRectangleInsideOutline(
            ref double minX, ref double maxX, ref double minY, ref double maxY,
            ZoneDirection direction, IList<Point3> outline, IList<Point3> assigned)
        {
            var bestShift = double.NaN;
            var bestCovered = -1;
            const double stepM = 0.05;
            const int maxSteps = 40;
            for (var step = 0; step <= maxSteps; step++)
            foreach (var sign in step == 0 ? new[] { 0 } : new[] { -1, 1 })
            {
                var shift = step * stepM * sign;
                var x1 = minX + (direction == ZoneDirection.Y ? shift : 0);
                var x2 = maxX + (direction == ZoneDirection.Y ? shift : 0);
                var y1 = minY + (direction == ZoneDirection.X ? shift : 0);
                var y2 = maxY + (direction == ZoneDirection.X ? shift : 0);
                if (!RectangleInsideOutline(x1, x2, y1, y2, outline)) continue;
                var covered = assigned.Count(point =>
                    point.X >= x1 - 1e-6 && point.X <= x2 + 1e-6 &&
                    point.Y >= y1 - 1e-6 && point.Y <= y2 + 1e-6);
                if (covered < bestCovered) continue;
                if (covered == bestCovered && !double.IsNaN(bestShift) &&
                    Math.Abs(shift) >= Math.Abs(bestShift)) continue;
                bestCovered = covered;
                bestShift = shift;
            }
            if (double.IsNaN(bestShift)) return false;
            if (direction == ZoneDirection.Y) { minX += bestShift; maxX += bestShift; }
            else { minY += bestShift; maxY += bestShift; }
            return true;
        }

        private static (double MinX, double MaxX, double MinY, double MaxY) Bounds(IList<Point3> contour) =>
            (contour.Min(p => p.X), contour.Max(p => p.X),
             contour.Min(p => p.Y), contour.Max(p => p.Y));

        private static (double MinX, double MaxX, double MinY, double MaxY) PlateBounds(
            LiraPlateElement plate) => plate.Contour != null && plate.Contour.Count >= 3
            ? Bounds(plate.Contour)
            : (plate.Centroid.X, plate.Centroid.X, plate.Centroid.Y, plate.Centroid.Y);

        private static bool IntersectsBounds(AdditionalZone zone, LiraPlateElement plate)
        {
            if (zone.Contour == null || zone.Contour.Count < 3) return false;
            var zoneBounds = Bounds(zone.Contour);
            var plateBounds = PlateBounds(plate);
            return plateBounds.MaxX >= zoneBounds.MinX - 1e-6 &&
                   plateBounds.MinX <= zoneBounds.MaxX + 1e-6 &&
                   plateBounds.MaxY >= zoneBounds.MinY - 1e-6 &&
                   plateBounds.MinY <= zoneBounds.MaxY + 1e-6;
        }

        private static bool ContainsContour(AdditionalZone container, AdditionalZone subject)
        {
            if (container.Contour.Count < 3 || subject.Contour.Count < 3) return false;
            var missing = Clipper.Difference(new Paths64 { ToPath(subject.Contour) },
                new Paths64 { ToPath(container.Contour) }, FillRule.NonZero);
            var missingArea = missing.Sum(path => Math.Abs(Clipper.Area(path)));
            var subjectArea = Math.Abs(Clipper.Area(ToPath(subject.Contour)));
            return missingArea <= Math.Max(10.0, subjectArea * 1e-8);
        }

        private static bool Covers(AdditionalZone zone, Point3 point)
        {
            if (zone.Contour.Count < 3) return false;
            var bounds = Bounds(zone.Contour);
            const double boundaryToleranceM = 0.01;
            return point.X >= bounds.MinX - boundaryToleranceM &&
                   point.X <= bounds.MaxX + boundaryToleranceM &&
                   point.Y >= bounds.MinY - boundaryToleranceM &&
                   point.Y <= bounds.MaxY + boundaryToleranceM;
        }

        public static bool IntersectsOpening(AdditionalZone zone, IList<OpeningInfo> openings)
        {
            if (zone.Contour.Count < 3) return false;
            foreach (var opening in openings)
            {
                var overlap = Clipper.Intersect(new Paths64 { ToPath(zone.Contour) },
                    new Paths64 { ToPath(Rectangle(opening.MinXM, opening.MaxXM,
                        opening.MinYM, opening.MaxYM, zone.LevelZM)) }, FillRule.NonZero);
                if (overlap.Any(path => Math.Abs(Clipper.Area(path)) > 1)) return true;
            }
            return false;
        }

        public static bool IsFullyInsideOpening(AdditionalZone zone, IList<OpeningInfo> openings)
        {
            if (zone.Contour.Count < 3 || openings == null || openings.Count == 0)
                return false;
            var zonePath = ToPath(zone.Contour);
            var zoneArea = Math.Abs(Clipper.Area(zonePath));
            if (zoneArea <= 0.5) return false;

            var openingPaths = openings.Select(opening => ToPath(Rectangle(
                opening.MinXM, opening.MaxXM, opening.MinYM, opening.MaxYM, zone.LevelZM)))
                .Where(path => path.Count >= 3 && Math.Abs(Clipper.Area(path)) > 0.5)
                .ToList();
            if (openingPaths.Count == 0) return false;

            var openingUnion = Clipper.Union(new Paths64(openingPaths), FillRule.NonZero);
            var remaining = Clipper.Difference(new Paths64 { zonePath }, openingUnion, FillRule.NonZero);
            var remainingArea = remaining.Sum(path => Math.Abs(Clipper.Area(path)));
            return remainingArea <= Math.Max(1.0, zoneArea * 1e-8);
        }

        public static List<AdditionalZone> ExcludeOpenings(AdditionalZone zone, IList<OpeningInfo> openings)
        {
            var parts = new List<AdditionalZone> { zone };
            foreach (var opening in openings)
            {
                var next = new List<AdditionalZone>();
                foreach (var part in parts)
                {
                    var bounds = Bounds(part.Contour);
                    var left = Math.Max(bounds.MinX, opening.MinXM);
                    var right = Math.Min(bounds.MaxX, opening.MaxXM);
                    var bottom = Math.Max(bounds.MinY, opening.MinYM);
                    var top = Math.Min(bounds.MaxY, opening.MaxYM);
                    if (right - left <= 1e-6 || top - bottom <= 1e-6)
                    {
                        next.Add(part);
                        continue;
                    }
                    void AddPiece(double x0, double x1, double y0, double y1)
                    {
                        if (x1 - x0 <= 0.05 || y1 - y0 <= 0.05) return;
                        var clipped = Clipper.Intersect(new Paths64 { ToPath(part.Contour) },
                            new Paths64 { ToPath(Rectangle(x0, x1, y0, y1, part.LevelZM)) }, FillRule.NonZero);
                        foreach (var path in clipped.Where(p => p.Count >= 3))
                        {
                            var copy = Copy(part);
                            SetContour(copy, FromPath(path, copy.LevelZM));
                            if (copy.LengthM <= 0.05 || copy.WidthM <= 0.05) continue;
                            copy.Comment = "подрезано по отверстию";
                            next.Add(copy);
                        }
                    }
                    AddPiece(bounds.MinX, left, bounds.MinY, bounds.MaxY);
                    AddPiece(right, bounds.MaxX, bounds.MinY, bounds.MaxY);
                    AddPiece(left, right, bounds.MinY, bottom);
                    AddPiece(left, right, top, bounds.MaxY);
                }
                parts = next;
            }
            return parts;
        }

        public static List<AdditionalZone> SplitAtOpenings(
            AdditionalZone zone, IList<OpeningInfo> openings, AnalysisSettings settings,
            IList<LiraPlateElement>? plates = null)
        {
            // Direction is a layer setting, not persistent geometry. Re-resolve it here
            // because imported/edited zones may still carry the pre-reverse direction.
            var effectiveDirection = RebarTables.DirectionForLayer(
                zone.Layer, settings.ReverseZoneDirections);
            zone.Direction = effectiveDirection;
            var zoneBounds = Bounds(zone.Contour);
            // A straight family is centred and rounded up only after opening
            // processing. Include a nearby opening that the final anchored bar can
            // reach; checking only the preliminary rectangle misses openings beside
            // a reversed X/Y layer.
            var influenceMm = Math.Max(0, settings.EdgeOffsetMm);
            if (settings.ReverseZoneDirections && effectiveDirection == ZoneDirection.X)
                influenceMm += RebarTables.AnchorageLenMm(
                    settings.ConcreteClass, zone.DiameterMm) + Math.Max(0, settings.GridCellMm);
            var influenceGap = UnitConversion.MmToMeters(influenceMm) + 1e-5;
            var relevant = openings.Where(op => !HoleBentRules.ShouldIgnoreOpening(
                    op, effectiveDirection, settings.HoleIgnorePerpMm))
                .Where(op => HoleBentRules.RectIntersects(op,
                        zoneBounds.MinX, zoneBounds.MaxX, zoneBounds.MinY, zoneBounds.MaxY) ||
                    (effectiveDirection == ZoneDirection.X
                        ? Math.Min(zoneBounds.MaxY, op.MaxYM) - Math.Max(zoneBounds.MinY, op.MinYM) > 1e-6 &&
                          (Math.Abs(zoneBounds.MinX - op.MaxXM) <= influenceGap ||
                           Math.Abs(zoneBounds.MaxX - op.MinXM) <= influenceGap)
                        : Math.Min(zoneBounds.MaxX, op.MaxXM) - Math.Max(zoneBounds.MinX, op.MinXM) > 1e-6 &&
                          (Math.Abs(zoneBounds.MinY - op.MaxYM) <= influenceGap ||
                           Math.Abs(zoneBounds.MaxY - op.MinYM) <= influenceGap)))
                .ToList();
            if (relevant.Count == 0)
                return new List<AdditionalZone> { zone };

            var parts = IntersectsOpening(zone, relevant)
                ? ExcludeOpenings(zone, relevant)
                : new List<AdditionalZone> { zone };
            if (plates != null && plates.Count > 0)
            {
                var transitionCellMm = settings.ReverseZoneDirections &&
                    zone.Layer == RebarLayer.As2 && effectiveDirection == ZoneDirection.X
                        ? settings.GridCellMm
                        : 0;
                parts = SplitAtTransverseOpeningEdges(
                    parts, relevant, effectiveDirection, transitionCellMm);
                parts = MergeSameTransverseBands(parts, effectiveDirection);
            }
            foreach (var part in parts)
            {
                part.Direction = effectiveDirection;
                var bounds = Bounds(part.Contour);
                var endsAtOpening = relevant.Any(op => effectiveDirection == ZoneDirection.X
                    ? Math.Min(bounds.MaxY, op.MaxYM) - Math.Max(bounds.MinY, op.MinYM) > 1e-6 &&
                      (Math.Abs(bounds.MaxX - op.MinXM) < 1e-5 ||
                       Math.Abs(bounds.MinX - op.MaxXM) < 1e-5)
                    : Math.Min(bounds.MaxX, op.MaxXM) - Math.Max(bounds.MinX, op.MinXM) > 1e-6 &&
                      (Math.Abs(bounds.MaxY - op.MinYM) < 1e-5 ||
                       Math.Abs(bounds.MinY - op.MaxYM) < 1e-5));
                if (endsAtOpening && settings.ApplyBentRules)
                {
                    var gapM = UnitConversion.MmToMeters(Math.Max(0, settings.EdgeOffsetMm));
                    var minX = bounds.MinX;
                    var maxX = bounds.MaxX;
                    var minY = bounds.MinY;
                    var maxY = bounds.MaxY;
                    foreach (var op in relevant)
                    {
                        if (effectiveDirection == ZoneDirection.X)
                        {
                            if (Math.Abs(maxX - op.MinXM) < 1e-5) maxX -= gapM;
                            if (Math.Abs(minX - op.MaxXM) < 1e-5) minX += gapM;
                        }
                        else
                        {
                            if (Math.Abs(maxY - op.MinYM) < 1e-5) maxY -= gapM;
                            if (Math.Abs(minY - op.MaxYM) < 1e-5) minY += gapM;
                        }
                    }
                    if (maxX - minX > 0.05 && maxY - minY > 0.05)
                    {
                        SetContour(part, Rectangle(minX, maxX, minY, maxY, part.LevelZM));
                        bounds = Bounds(part.Contour);
                    }
                    part.VerticalLegMm = HoleBentRules.VerticalLegAvailableMm(
                        settings, part.Layer, part.DiameterMm);
                    part.FamilyKind = HoleBentRules.ChooseBentFamily(part.VerticalLegMm, part.DiameterMm);
                    part.FamilyFileName = settings.GetFamilyName(part.FamilyKind);
                    part.CountBars = true;
                    part.CountInSpec = false;
                    part.Comment = "отверстие: гнутая деталь";
                }
                else if (zone.FamilyKind == ZoneFamilyKind.Straight)
                {
                    part.FamilyKind = ZoneFamilyKind.Straight;
                    part.FamilyFileName = settings.GetFamilyName(ZoneFamilyKind.Straight);
                    part.VerticalLegMm = 0;
                    part.Comment = "обход отверстия: прямой стержень";
                }
                SetContour(part, part.Contour.ToList());
                if (plates != null)
                {
                    part.NodeIds = plates.Where(plate => zone.NodeIds.Contains(plate.Id) &&
                        plate.Centroid.X >= bounds.MinX - 1e-6 && plate.Centroid.X <= bounds.MaxX + 1e-6 &&
                        plate.Centroid.Y >= bounds.MinY - 1e-6 && plate.Centroid.Y <= bounds.MaxY + 1e-6)
                        .Select(plate => plate.Id).Distinct().ToList();
                    part.ElementId = part.NodeIds.FirstOrDefault();
                }
            }
            if (plates != null && plates.Count > 0)
            {
                foreach (var part in parts.Where(part => part.NodeIds.Count > 0))
                    RestoreLongitudinalPlacement(part, relevant, settings, plates);
            }
            return parts;
        }

        private static List<AdditionalZone> MergeSameTransverseBands(
            List<AdditionalZone> source, ZoneDirection direction)
        {
            const double toleranceM = 0.001;
            var result = new List<AdditionalZone>();
            foreach (var part in source)
            {
                var bounds = Bounds(part.Contour);
                var existing = result.FirstOrDefault(candidate =>
                {
                    var other = Bounds(candidate.Contour);
                    return direction == ZoneDirection.X
                        ? Math.Abs(bounds.MinY - other.MinY) <= toleranceM &&
                          Math.Abs(bounds.MaxY - other.MaxY) <= toleranceM
                        : Math.Abs(bounds.MinX - other.MinX) <= toleranceM &&
                          Math.Abs(bounds.MaxX - other.MaxX) <= toleranceM;
                });
                if (existing == null)
                {
                    result.Add(part);
                    continue;
                }
                var otherBounds = Bounds(existing.Contour);
                SetContour(existing, Rectangle(
                    Math.Min(bounds.MinX, otherBounds.MinX), Math.Max(bounds.MaxX, otherBounds.MaxX),
                    Math.Min(bounds.MinY, otherBounds.MinY), Math.Max(bounds.MaxY, otherBounds.MaxY),
                    existing.LevelZM));
                existing.NodeIds = existing.NodeIds.Concat(part.NodeIds).Distinct().ToList();
                existing.ElementId = existing.NodeIds.FirstOrDefault();
            }
            return result;
        }

        private static void RestoreLongitudinalPlacement(
            AdditionalZone zone, IList<OpeningInfo> openings, AnalysisSettings settings,
            IList<LiraPlateElement> plates)
        {
            var assigned = plates.Where(plate => zone.NodeIds.Contains(plate.Id)).ToList();
            if (assigned.Count == 0 || zone.Contour.Count < 3) return;
            var bounds = Bounds(zone.Contour);
            var coreMin = zone.Direction == ZoneDirection.X
                ? assigned.Min(plate => plate.Contour.Min(point => point.X))
                : assigned.Min(plate => plate.Contour.Min(point => point.Y));
            var coreMax = zone.Direction == ZoneDirection.X
                ? assigned.Max(plate => plate.Contour.Max(point => point.X))
                : assigned.Max(plate => plate.Contour.Max(point => point.Y));
            var anchorageM = UnitConversion.MmToMeters(
                RebarTables.AnchorageLenMm(settings.ConcreteClass, zone.DiameterMm));
            // Near an opening the preliminary FE envelope is narrower than the
            // final bar. Keep two mesh modules in addition to concrete anchorage;
            // this matches the two-FE opening rule and prevents undersized 1950 /
            // 2900 families after a reversed-direction split.
            var placementReserveM = settings.ReverseZoneDirections &&
                                    zone.FamilyKind == ZoneFamilyKind.Straight &&
                                    zone.Direction == ZoneDirection.X
                ? UnitConversion.MmToMeters(Math.Max(0, settings.GridCellMm))
                : 0;
            var start = coreMin - anchorageM - placementReserveM;
            var end = coreMax + anchorageM + placementReserveM;
            if (end - start <= 0.05) return;

            if (zone.FamilyKind == ZoneFamilyKind.Straight)
            {
                var requiredMm = UnitConversion.MetersToMm(end - start);
                var familyM = UnitConversion.MmToMeters(RebarTables.PickFamilyLength(requiredMm));
                var center = (coreMin + coreMax) / 2.0;
                var familyStart = center - familyM / 2.0;
                var familyEnd = center + familyM / 2.0;
                start = familyStart;
                end = familyEnd;
            }

            if (zone.Direction == ZoneDirection.X)
                SetContour(zone, Rectangle(start, end, bounds.MinY, bounds.MaxY, zone.LevelZM));
            else
                SetContour(zone, Rectangle(bounds.MinX, bounds.MaxX, start, end, zone.LevelZM));
        }

        private static List<AdditionalZone> SplitAtTransverseOpeningEdges(
            List<AdditionalZone> source, IList<OpeningInfo> openings,
            ZoneDirection direction, int transitionCellMm)
        {
            var result = new List<AdditionalZone>();
            foreach (var part in source)
            {
                var bounds = Bounds(part.Contour);
                var cuts = openings
                    .SelectMany(op => direction == ZoneDirection.X
                        ? transitionCellMm > 0
                            ? new[]
                            {
                                op.MinYM - 2 * UnitConversion.MmToMeters(transitionCellMm),
                                op.MinYM,
                                op.MaxYM
                            }
                            : new[] { op.MinYM, op.MaxYM }
                        : new[] { op.MinXM, op.MaxXM })
                    .Where(value => value > (direction == ZoneDirection.X ? bounds.MinY : bounds.MinX) + 1e-5 &&
                                    value < (direction == ZoneDirection.X ? bounds.MaxY : bounds.MaxX) - 1e-5)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList();
                if (cuts.Count == 0)
                {
                    result.Add(part);
                    continue;
                }

                var edges = new List<double>
                {
                    direction == ZoneDirection.X ? bounds.MinY : bounds.MinX
                };
                edges.AddRange(cuts);
                edges.Add(direction == ZoneDirection.X ? bounds.MaxY : bounds.MaxX);
                for (var i = 0; i < edges.Count - 1; i++)
                {
                    if (edges[i + 1] - edges[i] <= 0.05) continue;
                    var copy = Copy(part);
                    var rectangle = direction == ZoneDirection.X
                        ? Rectangle(bounds.MinX, bounds.MaxX, edges[i], edges[i + 1], part.LevelZM)
                        : Rectangle(edges[i], edges[i + 1], bounds.MinY, bounds.MaxY, part.LevelZM);
                    var clipped = Clipper.Intersect(new Paths64 { ToPath(part.Contour) },
                        new Paths64 { ToPath(rectangle) }, FillRule.NonZero);
                    foreach (var path in clipped.Where(path => path.Count >= 3))
                    {
                        var band = Copy(copy);
                        SetContour(band, FromPath(path, band.LevelZM));
                        result.Add(band);
                    }
                }
            }
            return result;
        }

        public static AdditionalZone? Create(
            AdditionalZone template, double minX, double maxX, double minY, double maxY,
            IList<Point3> slab, bool clipToSlab = true)
        {
            var zone = Copy(template);
            zone.NodeIds.Clear();
            zone.ElementId = 0;
            zone.Comment = "создано в предпросмотре";
            return ApplyEditedContour(zone, Rectangle(minX, maxX, minY, maxY, template.LevelZM), slab, clipToSlab)
                ? zone
                : null;
        }

        public static AdditionalZone? Merge(AdditionalZone first, AdditionalZone second, IList<Point3> slab,
            bool clipToSlab = true)
        {
            if (first.Layer != second.Layer || first.Direction != second.Direction)
                return null;
            var paths = new Paths64 { ToPath(first.Contour), ToPath(second.Contour) };
            var union = Clipper.Union(paths, FillRule.NonZero);
            var mergedPaths = clipToSlab ? IntersectWithSlab(union, slab) : union;
            if (mergedPaths.Count != 1) return null;
            var path = Largest(mergedPaths);
            if (path == null) return null;

            var governing = first.AsCoveredCm2PerM >= second.AsCoveredCm2PerM ? first : second;
            var merged = Copy(governing);
            merged.NodeIds = first.NodeIds.Concat(second.NodeIds).Distinct().ToList();
            merged.ElementId = merged.NodeIds.FirstOrDefault();
            merged.AsRequired = Math.Max(first.AsRequired, second.AsRequired);
            merged.AsAdditional = Math.Max(first.AsAdditional, second.AsAdditional);
            merged.Comment = "объединено в предпросмотре";
            SetContour(merged, FromPath(path, merged.LevelZM));
            return merged;
        }

        public static List<AdditionalZone> Split(
            AdditionalZone zone, double coordinateM, bool verticalCut, IList<Point3> slab,
            bool clipToSlab = true)
        {
            var minX = zone.Contour.Min(p => p.X);
            var maxX = zone.Contour.Max(p => p.X);
            var minY = zone.Contour.Min(p => p.Y);
            var maxY = zone.Contour.Max(p => p.Y);
            var margin = Math.Max(maxX - minX, maxY - minY) + 1.0;
            var cutters = verticalCut
                ? new[]
                {
                    Rectangle(minX - margin, coordinateM, minY - margin, maxY + margin, zone.LevelZM),
                    Rectangle(coordinateM, maxX + margin, minY - margin, maxY + margin, zone.LevelZM)
                }
                : new[]
                {
                    Rectangle(minX - margin, maxX + margin, minY - margin, coordinateM, zone.LevelZM),
                    Rectangle(minX - margin, maxX + margin, coordinateM, maxY + margin, zone.LevelZM)
                };

            var result = new List<AdditionalZone>();
            foreach (var cutter in cutters)
            {
                var pieces = Clipper.Intersect(
                    new Paths64 { ToPath(zone.Contour) },
                    new Paths64 { ToPath(cutter) }, FillRule.NonZero);
                var splitPaths = clipToSlab ? IntersectWithSlab(pieces, slab) : pieces;
                var path = Largest(splitPaths);
                if (path == null) continue;
                var copy = Copy(zone);
                copy.Comment = "разделено в предпросмотре";
                SetContour(copy, FromPath(path, copy.LevelZM));
                if (copy.LengthM > 0.05 && copy.WidthM > 0.05)
                    result.Add(copy);
            }
            return result;
        }

        private static bool ApplyClipped(
            AdditionalZone zone, IList<Point3> contour, IList<Point3> slab)
        {
            var clipped = IntersectWithSlab(new Paths64 { ToPath(contour) }, slab);
            var path = Largest(clipped);
            if (path == null) return false;
            SetContour(zone, FromPath(path, zone.LevelZM));
            return zone.LengthM > 0.05 && zone.WidthM > 0.05;
        }

        private static bool ApplyEditedContour(
            AdditionalZone zone, IList<Point3> contour, IList<Point3> slab, bool clipToSlab)
        {
            if (clipToSlab) return ApplyClipped(zone, contour, slab);
            if (contour == null || contour.Count < 3) return false;
            SetContour(zone, contour.ToList());
            return zone.LengthM > 0.05 && zone.WidthM > 0.05;
        }

        private static Paths64 IntersectWithSlab(Paths64 subject, IList<Point3> slab)
        {
            if (slab == null || slab.Count < 3) return subject;
            return Clipper.Intersect(subject, new Paths64 { ToPath(slab) }, FillRule.NonZero);
        }

        private static Path64? Largest(Paths64 paths) => paths
            .Where(p => p.Count >= 3)
            .OrderByDescending(p => Math.Abs(Clipper.Area(p)))
            .FirstOrDefault();

        private static Path64 ToPath(IEnumerable<Point3> points) =>
            new Path64(points.Select(p => new Point64(
                (long)Math.Round(p.X * Scale),
                (long)Math.Round(p.Y * Scale))));

        private static List<Point3> FromPath(Path64 path, double z) => path
            .Select(p => new Point3(p.X / Scale, p.Y / Scale, z))
            .ToList();

        private static List<Point3> Rectangle(
            double minX, double maxX, double minY, double maxY, double z) =>
            new List<Point3>
            {
                new Point3(Math.Min(minX, maxX), Math.Min(minY, maxY), z),
                new Point3(Math.Max(minX, maxX), Math.Min(minY, maxY), z),
                new Point3(Math.Max(minX, maxX), Math.Max(minY, maxY), z),
                new Point3(Math.Min(minX, maxX), Math.Max(minY, maxY), z)
            };

        private static AdditionalZone Copy(AdditionalZone source) => new AdditionalZone
        {
            ZoneId = source.ZoneId,
            Layer = source.Layer,
            NodeIds = source.NodeIds.ToList(),
            ElementId = source.ElementId,
            LevelZM = source.LevelZM,
            Direction = source.Direction,
            DiameterMm = source.DiameterMm,
            BarStepMm = source.BarStepMm,
            BarCount = source.BarCount,
            WidthMm = source.WidthMm,
            WidthM = source.WidthM,
            LengthMm = source.LengthMm,
            LengthM = source.LengthM,
            AsAdditional = source.AsAdditional,
            AsRequired = source.AsRequired,
            AsCoveredCm2PerM = source.AsCoveredCm2PerM,
            ConcreteClass = source.ConcreteClass,
            FamilyKind = source.FamilyKind,
            FamilyFileName = source.FamilyFileName,
            CountInSpec = source.CountInSpec,
            CountBars = source.CountBars,
            VerticalLegMm = source.VerticalLegMm,
            RotationDeg = source.RotationDeg,
            AlphaCoef = source.AlphaCoef,
            Rebar = source.Rebar,
            AxisNameX = source.AxisNameX,
            AxisNameY = source.AxisNameY,
            AxisPosXM = source.AxisPosXM,
            AxisPosYM = source.AxisPosYM,
            OffsetFromAxisXMm = source.OffsetFromAxisXMm,
            OffsetFromAxisYMm = source.OffsetFromAxisYMm,
            AxisTieLabel = source.AxisTieLabel,
            Comment = source.Comment,
            IsValid = source.IsValid,
            StatusColor = source.StatusColor
        };

        private static void SetContour(AdditionalZone zone, List<Point3> contour)
        {
            zone.Contour = contour;
            var minX = contour.Min(p => p.X);
            var maxX = contour.Max(p => p.X);
            var minY = contour.Min(p => p.Y);
            var maxY = contour.Max(p => p.Y);
            zone.Placement = new Point3((minX + maxX) / 2, (minY + maxY) / 2, zone.LevelZM);
            zone.LengthM = zone.Direction == ZoneDirection.X ? maxX - minX : maxY - minY;
            zone.WidthM = zone.Direction == ZoneDirection.X ? maxY - minY : maxX - minX;
            var planLengthMm = UnitConversion.MetersToMm(zone.LengthM);
            zone.LengthMm = zone.FamilyKind == ZoneFamilyKind.Straight
                ? planLengthMm
                : RebarTables.BentBarTotalLengthMm(planLengthMm, zone.VerticalLegMm, zone.FamilyKind);
            zone.WidthMm = UnitConversion.MetersToMm(zone.WidthM);
            zone.BarCount = Math.Max(2, (int)Math.Round(zone.WidthMm / Math.Max(1, zone.BarStepMm)) + 1);
        }
    }
}
