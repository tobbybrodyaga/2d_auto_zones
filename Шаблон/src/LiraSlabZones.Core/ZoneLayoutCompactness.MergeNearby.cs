using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public static partial class ZoneLayoutCompactness
    {
        public const double NearbyRowAlignmentMm = 50;

        /// <summary>Выравнивает близкие торцы одинаковых соседних рядов без изменения длины и шага.</summary>
        public static int MergeNearbyRows(IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            AnalysisSettings settings, IList<Point3>? outline, IList<OpeningInfo>? openings,
            ZoneBoundaryConstraints constraints, Action<string, int, int>? progress = null,
            IList<AlignAxis>? axes = null)
        {
            if (zones.Count < 2 || plates.Count == 0 || !constraints.HasOutline ||
                string.IsNullOrWhiteSpace(settings.ConcreteClass)) return 0;
            // Входные зоны не изменяем: откат сохраняет также их внешние ссылки и ручные свойства.
            var baseline = zones.ToList();
            var bounds = zones.Where(z => z.Contour.Count >= 3)
                .ToDictionary(z => z, z => ZoneBoundaryConstraints.Bounds(z.Contour));
            var origins = bounds.ToDictionary(p => p.Key, p => NearbyOrigins(p.Key, p.Value));
            var plateBounds = plates.Select(p => p.Contour.Count >= 3
                ? ZoneBoundaryConstraints.Bounds(p.Contour)
                : new ZonePatchFrameBounds(p.Centroid.X, p.Centroid.X, p.Centroid.Y, p.Centroid.Y)).ToArray();
            var plateIndex = new ZoneSpatialIndex();
            for (var i = 0; i < plates.Count; i++)
                if (plates[i].Rebar.Ok)
                {
                    var b = plateBounds[i];
                    plateIndex.Add(i, b.MinX, b.MaxX, b.MinY, b.MaxY);
                }
            var margin = Math.Max(settings.GridCellMm, zones.Max(z => z.BarStepMm)) / 1000.0 + Eps;
            int count = 0;
            int? originalConflicts = null;
            for (var again = true; again;)
            {
                again = false;
                progress?.Invoke("Объединение близких рядов", count, baseline.Count - 1);
                for (var i = 0; i < zones.Count && !again; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var a = zones[i]; var b = zones[j];
                    if (!CompatibleNearbyRows(a, b) || !bounds.ContainsKey(a) || !bounds.ContainsKey(b)) continue;
                    if ((axes == null || axes.Count == 0) &&
                        (!string.IsNullOrEmpty(a.AxisTieLabel) || !string.IsNullOrEmpty(b.AxisTieLabel))) continue;
                    var x = bounds[a]; var y = bounds[b];
                    if (!IsRectangle(a, x) || !IsRectangle(b, y)) continue;
                    var xLength = AxialMax(x, a.Direction) - AxialMin(x, a.Direction);
                    var yLength = AxialMax(y, a.Direction) - AxialMin(y, a.Direction);
                    if (Math.Abs(xLength - yLength) > Eps) continue;
                    var gap = Math.Max(CrossMin(x, a.Direction), CrossMin(y, a.Direction)) -
                        Math.Min(CrossMax(x, a.Direction), CrossMax(y, a.Direction));
                    if (Math.Abs(gap - a.BarStepMm / 1000.0) > Eps) continue;
                    if (Math.Abs(CrossMax(x, a.Direction) - CrossMin(x, a.Direction) -
                            (a.BarCount - 1) * a.BarStepMm / 1000.0) > Eps ||
                        Math.Abs(CrossMax(y, a.Direction) - CrossMin(y, a.Direction) -
                            (b.BarCount - 1) * b.BarStepMm / 1000.0) > Eps) continue;
                    var from = Math.Min(origins[a].Min, origins[b].Min);
                    var to = Math.Max(origins[a].Max, origins[b].Max);
                    // Не допускаем накопленного смещения при последовательном объединении длинной цепочки.
                    if (to - from > NearbyRowAlignmentMm / 1000.0 + Eps) continue;
                    var envelope = Union(x, y);
                    var background = Background(settings, a.Layer);
                    var relevant = plateIndex.Query(envelope.MinX - margin, envelope.MaxX + margin,
                            envelope.MinY - margin, envelope.MaxY + margin)
                        .Where(k => Touches(plateBounds[k], envelope, margin) &&
                            plates[k].Rebar.Get(a.Layer) - background > MosaicBuilder.PositiveResidualToleranceCm2PerM)
                        .OrderBy(k => k).ToList();
                    if (relevant.Count == 0) continue;
                    var affected = envelope;
                    foreach (var k in relevant) affected = Union(affected, plateBounds[k]);
                    // Все участники покрытия целых соседних КЭ, включая стержни за рамкой и в зазоре.
                    var local = zones.Where(z => z.Layer == a.Layer && bounds.ContainsKey(z) &&
                        Touches(bounds[z], affected, margin)).ToList();
                    var localPlates = relevant.Select(k => plates[k]).ToList();
                    var oldConflicts = ZoneBoundaryLayoutSolver.ConflictCount(local, settings);
                    var weightedStart = (AxialMin(x, a.Direction) * a.BarCount +
                        AxialMin(y, a.Direction) * b.BarCount) / (a.BarCount + b.BarCount);
                    var starts = new[] { weightedStart, (from + to) / 2, AxialMin(x, a.Direction),
                        AxialMin(y, a.Direction), from, to }.ToList();
                    if (axes?.Count > 0)
                        starts.InsertRange(0, starts.SelectMany(s => new[] { Math.Floor(s * 100) / 100,
                            Math.Ceiling(s * 100) / 100 }).ToArray());
                    foreach (var start in starts.Distinct())
                    {
                        if (start < from - Eps || start > to + Eps) continue;
                        var merged = Clone(CrossMin(x, a.Direction) <= CrossMin(y, a.Direction) ? a : b);
                        SetBounds(merged, a.Direction == ZoneDirection.X
                            ? new ZonePatchFrameBounds(start, start + xLength, envelope.MinY, envelope.MaxY)
                            : new ZonePatchFrameBounds(envelope.MinX, envelope.MaxX, start, start + xLength));
                        merged.BoundaryConstraints = constraints;
                        merged.BoundaryCheck = constraints.Check(merged.Contour, merged.Direction);
                        if (merged.BarCount != a.BarCount + b.BarCount || !merged.BoundaryCheck.Allowed ||
                            RebarTables.ExceedsMaxBarLength(merged) || Math.Abs(merged.LengthMm - a.LengthMm) >= 0.01) continue;
                        if (axes?.Count > 0 && RevitGridAlignment.Check(new[] { merged }, axes,
                            outline, openings, constraints.EdgeOffsetMm).Count > 0) continue;
                        if (zones.Any(z => !ReferenceEquals(z, a) && !ReferenceEquals(z, b) &&
                            ZoneEditor.HasPlacementConflict(merged, z))) continue;
                        var proposal = local.Where(z => !ReferenceEquals(z, a) && !ReferenceEquals(z, b))
                            .Concat(new[] { merged }).ToList();
                        if (ZoneBoundaryLayoutSolver.ConflictCount(proposal, settings) > oldConflicts ||
                            !ZoneBoundaryLayoutSolver.PreservesRequiredLayout(local, proposal, localPlates,
                                settings, outline, openings, constraints, out _)) continue;
                        originalConflicts ??= ZoneBoundaryLayoutSolver.ConflictCount(baseline, settings);
                        merged.AsAdditional = Math.Max(a.AsAdditional, b.AsAdditional);
                        merged.AsRequired = Math.Max(a.AsRequired, b.AsRequired);
                        merged.AsCoveredCm2PerM = Math.Min(a.AsCoveredCm2PerM, b.AsCoveredCm2PerM);
                        merged.DesignEdgeCoverMm = Math.Max(a.DesignEdgeCoverMm, b.DesignEdgeCoverMm);
                        merged.NodeIds = a.NodeIds.Concat(b.NodeIds).Distinct().ToList();
                        merged.ElementId = merged.NodeIds.FirstOrDefault();
                        merged.RowMergeCenterMinM = from + xLength / 2;
                        merged.RowMergeCenterMaxM = to + xLength / 2;
                        merged.RowMergeCenterM = start + xLength / 2;
                        UpdateReinforcement(merged);
                        if (axes?.Count > 0) RevitGridAlignment.FillTie(merged, axes);
                        merged.Comment += "; близкие одинаковые ряды выровнены и объединены, длина и число стержней сохранены";
                        zones[i] = merged; zones.RemoveAt(j);
                        bounds[merged] = ZoneBoundaryConstraints.Bounds(merged.Contour);
                        origins[merged] = (from, to);
                        count++; again = true; break;
                    }
                    if (again) break;
                }
            }
            if (count == 0) return 0;
            progress?.Invoke("Проверка объединённых рядов", 0, 0);
            // Локальные проверки ускоряют перебор; общий контроль всегда остаётся последним барьером.
            if (ZoneBoundaryLayoutSolver.ConflictCount(zones, settings) > originalConflicts!.Value ||
                !ZoneBoundaryLayoutSolver.PreservesRequiredLayout(baseline, zones, plates, settings,
                    outline, openings, constraints, out _))
            {
                zones.Clear(); foreach (var zone in baseline) zones.Add(zone);
                return 0;
            }
            constraints.CheckZones(zones);
            return count;
        }

        private static bool CompatibleNearbyRows(AdditionalZone a, AdditionalZone b) =>
            a.IsValid && b.IsValid && a.SplicePartCount <= 1 && b.SplicePartCount <= 1 &&
            a.Layer == b.Layer && a.Direction == b.Direction && a.DiameterMm > 0 && a.DiameterMm == b.DiameterMm &&
            a.BarStepMm > 0 && a.BarStepMm == b.BarStepMm && a.BarCount > 0 && b.BarCount > 0 &&
            a.FamilyKind == b.FamilyKind && a.FamilyFileName == b.FamilyFileName &&
            a.BendAtStart == b.BendAtStart && a.BendAtEnd == b.BendAtEnd &&
            Math.Abs(a.LengthMm - b.LengthMm) < 0.01 && Math.Abs(a.VerticalLegMm - b.VerticalLegMm) < 0.01 &&
            Math.Abs(a.LevelZM - b.LevelZM) < Eps && Math.Abs(a.RotationDeg - b.RotationDeg) < 1e-6 &&
            a.ConcreteClass == b.ConcreteClass && Math.Abs(a.AlphaCoef - b.AlphaCoef) < 1e-6 &&
            Math.Abs(a.AsCoveredCm2PerM - b.AsCoveredCm2PerM) < 1e-6 &&
            Math.Abs(a.DesignEdgeCoverMm - b.DesignEdgeCoverMm) < 0.01 &&
            a.CountInSpec == b.CountInSpec && a.CountBars == b.CountBars && a.RnfSection == b.RnfSection &&
            a.RnfMarkConstruction == b.RnfMarkConstruction && a.RnfMarkAssembly == b.RnfMarkAssembly &&
            a.RnfMarkElement == b.RnfMarkElement;

        private static bool IsRectangle(AdditionalZone zone, ZonePatchFrameBounds b) =>
            zone.Contour.Count == 4 && zone.Contour.All(p =>
                (Math.Abs(p.X - b.MinX) < Eps || Math.Abs(p.X - b.MaxX) < Eps) &&
                (Math.Abs(p.Y - b.MinY) < Eps || Math.Abs(p.Y - b.MaxY) < Eps));
        private static (double Min, double Max) NearbyOrigins(AdditionalZone zone, ZonePatchFrameBounds b)
        {
            var start = AxialMin(b, zone.Direction);
            var half = (AxialMax(b, zone.Direction) - start) / 2;
            var center = start + half;
            // История сохраняется в JSON; после перемещения зоны её положение становится новым исходным.
            if (zone.RowMergeCenterM.HasValue && zone.RowMergeCenterMinM.HasValue && zone.RowMergeCenterMaxM.HasValue &&
                Math.Abs(zone.RowMergeCenterM.Value - center) < Eps &&
                zone.RowMergeCenterMinM.Value <= center + Eps && zone.RowMergeCenterMaxM.Value >= center - Eps &&
                zone.RowMergeCenterMaxM.Value - zone.RowMergeCenterMinM.Value <= NearbyRowAlignmentMm / 1000.0 + Eps)
                return (zone.RowMergeCenterMinM.Value - half, zone.RowMergeCenterMaxM.Value - half);
            return (start, start);
        }
        private static double AxialMin(ZonePatchFrameBounds b, ZoneDirection d) => d == ZoneDirection.X ? b.MinX : b.MinY;
        private static double AxialMax(ZonePatchFrameBounds b, ZoneDirection d) => d == ZoneDirection.X ? b.MaxX : b.MaxY;
        private static double CrossMin(ZonePatchFrameBounds b, ZoneDirection d) => d == ZoneDirection.X ? b.MinY : b.MinX;
        private static double CrossMax(ZonePatchFrameBounds b, ZoneDirection d) => d == ZoneDirection.X ? b.MaxY : b.MaxX;
        private static ZonePatchFrameBounds Union(ZonePatchFrameBounds a, ZonePatchFrameBounds b) =>
            new ZonePatchFrameBounds(Math.Min(a.MinX, b.MinX), Math.Max(a.MaxX, b.MaxX), Math.Min(a.MinY, b.MinY), Math.Max(a.MaxY, b.MaxY));
        private static bool Touches(ZonePatchFrameBounds a, ZonePatchFrameBounds b, double padding) =>
            a.MaxX >= b.MinX - padding && a.MinX <= b.MaxX + padding &&
            a.MaxY >= b.MinY - padding && a.MinY <= b.MaxY + padding;
    }
}
