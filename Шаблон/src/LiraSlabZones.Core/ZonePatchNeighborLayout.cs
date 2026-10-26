using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public sealed class ZonePatchNeighborLayoutResult
    {
        public int NormalizedWidths { get; set; }
        public int ShiftedZones { get; set; }
        public int ShrunkWeakZones { get; set; }
        public int ShrunkConflictZones { get; set; }
        public int TrimmedOverhangZones { get; set; }
        public int TransferredCoverageElements { get; set; }
        public int ExtendedZones { get; set; }
        public int AbsorbedContainedZones { get; set; }
        public int SplitContainedZones { get; set; }
        public int UnresolvedWidths { get; set; }
        public int UnresolvedPairs { get; set; }
        public int ResidualIntersections { get; set; }
        public int ResidualSpacingConflicts { get; set; }
        public int ResidualExcessiveGaps
        {
            get => ResidualSpacingConflicts;
            set => ResidualSpacingConflicts = value;
        }
        public bool RolledBackForCoverage { get; set; }
        public IList<string> UnresolvedDetails { get; } = new List<string>();
        public IList<string> PriorityDetails { get; } = new List<string>();
        public IList<string> ResidualDetails { get; } = new List<string>();
        public IList<string> CoverageLossDetails { get; } = new List<string>();
        public string Warning { get; set; } = string.Empty;
    }

    public static class ZonePatchNeighborLayout
    {
        private const double GeometryToleranceM = 1e-5;
        private const double CoverageTolerance = 1e-6;

        private sealed class Entry
        {
            public AdditionalZone Zone = null!;
            public ZonePatchFrameBounds Support;
            public ZonePatchFrameBounds Patch;
            public double CrossMin;
            public double CrossMax;
            public double AxialMin;
            public double AxialMax;
            public double AllowedCrossMin;
            public double AllowedCrossMax;
            public double AllowedAxialStartMin;
            public double AllowedAxialStartMax;
            public double Capacity;
            public int StepMm;
            public double SupportCrossCenter => Zone.Direction == ZoneDirection.X
                ? (Support.MinY + Support.MaxY) * 0.5
                : (Support.MinX + Support.MaxX) * 0.5;
        }

        private sealed class Lane
        {
            public List<Entry> Entries = new List<Entry>();
            public double SupportCrossMin;
            public double SupportCrossMax;
            public double AllowedCrossMin;
            public double AllowedCrossMax;
            public double CrossMin;
            public double CrossMax;
            public double PreferredStart;
            public double CoverageCrossMin = double.PositiveInfinity;
            public double CoverageCrossMax = double.NegativeInfinity;
            public double Capacity;
            public double ReinforcementAreaPriority;
            public int StepMm;
            public double MinimumWidthM;
            public bool WidthNormalizationFailed;
            public double SupportCrossCenter => (SupportCrossMin + SupportCrossMax) * 0.5;
        }

        private sealed class LaneEdge
        {
            public int Left;
            public int Right;
            public double MinimumGapM;
            public double MaximumGapM;
        }

        private sealed class DifferenceEdge
        {
            public int From;
            public int To;
            public double Weight;
        }

        private sealed class Snapshot
        {
            public List<Point3> Contour = new List<Point3>();
            public Point3 Placement = new Point3();
            public double WidthM;
            public double WidthMm;
            public int BarCount;
        }

        private sealed class CoveredElement
        {
            public LiraPlateElement Plate = null!;
            public RebarLayer Layer;
            public double RequiredAs;
            public AdditionalZone? DirectWitness;
            public Lane? DirectLaneWitness;
            public bool CoverageTransferred;
            public List<AdditionalZone> CoverageCandidates = new List<AdditionalZone>();
            public AdditionalZone? BridgeFirst;
            public AdditionalZone? BridgeSecond;
        }

        public static ZonePatchNeighborLayoutResult Apply(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> supportBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            var result = new ZonePatchNeighborLayoutResult();
            if (zones == null || zones.Count < 2 || plates == null || settings == null)
                return result;

            ResolveContainedZoneOverlaps(zones, supportBoundsByZone, patchBoundsByZone,
                plates, settings, slabOutline, openings, result);

            var adjustable = zones.Where(IsAdjustable).ToList();
            if (adjustable.Count < 2) return result;

            var snapshots = adjustable.ToDictionary(zone => zone, Capture);
            var zonesByLayer = adjustable.GroupBy(zone => zone.Layer)
                .ToDictionary(group => group.Key, group => (IList<AdditionalZone>)group.ToList());
            var alreadyCovered = CaptureCoveredElements(plates, zonesByLayer, settings, slabOutline, openings);
            var entries = adjustable.Select(zone => CreateEntry(
                    zone, supportBoundsByZone, patchBoundsByZone, settings))
                .Where(entry => entry != null)
                .Cast<Entry>()
                .ToList();
            var entryByZone = entries.ToDictionary(entry => entry.Zone);
            foreach (var group in entries.GroupBy(entry => entry.Zone.Layer)
                         .SelectMany(layerGroup => layerGroup.GroupBy(entry => entry.Zone.Direction)))
            {
                var lanes = BuildLanes(group, settings.EffectiveMinZoneWidthM)
                    .OrderBy(lane => lane.SupportCrossCenter).ToList();
                RestrictOverrunToPatchEdges(lanes, Math.Max(0, settings.GridCellMm) / 1000.0);
                var laneIndexByZone = lanes.SelectMany((lane, index) => lane.Entries
                    .Select(entry => (entry.Zone, Index: index)))
                    .ToDictionary(item => item.Zone, item => item.Index);
                foreach (var lane in lanes.OrderByDescending(item => item.Capacity))
                {
                    var originalWidth = lane.CrossMax - lane.CrossMin;
                    if (!TryNormalizeLaneWidth(lane))
                    {
                        lane.WidthNormalizationFailed = true;
                        result.UnresolvedWidths += lane.Entries.Count;
                        continue;
                    }
                    if (lane.CrossMax - lane.CrossMin > originalWidth + GeometryToleranceM)
                        result.NormalizedWidths += lane.Entries.Count;
                }

                AssignDirectCoverageWitnesses(lanes, laneIndexByZone, entryByZone,
                    alreadyCovered, group.First().Zone.Layer, group.Key);
                TrimNonPeakOverhangs(lanes, result);
                ShrinkWeakInteriorLanes(lanes, alreadyCovered, result);
                var edges = BuildLaneEdges(lanes);
                AddCoverageBridgeEdges(lanes, laneIndexByZone, alreadyCovered,
                    group.First().Zone.Layer, group.Key, edges);
                TryArrangeLanes(lanes, edges, out var starts, out var unresolvedPairs,
                    result.UnresolvedDetails, result);
                result.UnresolvedPairs += unresolvedPairs;
                ApplyLaneStarts(lanes, starts, result);
            }

            foreach (var entry in entries) ApplyInterval(entry);

            var absorbedBeforeNormalization = result.AbsorbedContainedZones;
            var splitBeforeNormalization = result.SplitContainedZones;
            ResolveContainedZoneOverlaps(zones, supportBoundsByZone, patchBoundsByZone,
                plates, settings, slabOutline, openings, result);
            if (result.AbsorbedContainedZones != absorbedBeforeNormalization ||
                result.SplitContainedZones != splitBeforeNormalization)
            {
                var previousSnapshots = snapshots;
                var previousSnapshotsById = snapshots
                    .GroupBy(pair => pair.Key.ZoneId)
                    .ToDictionary(group => group.Key, group => group.First().Value);
                adjustable = zones.Where(IsAdjustable).ToList();
                snapshots = new Dictionary<AdditionalZone, Snapshot>();
                foreach (var zone in adjustable)
                {
                    if (previousSnapshots.TryGetValue(zone, out var priorSnapshot))
                        snapshots[zone] = priorSnapshot;
                    else if (previousSnapshotsById.TryGetValue(zone.ZoneId, out priorSnapshot))
                        snapshots[zone] = priorSnapshot;
                    else
                        snapshots[zone] = Capture(zone);
                }
                zonesByLayer = adjustable.GroupBy(zone => zone.Layer)
                    .ToDictionary(group => group.Key, group => (IList<AdditionalZone>)group.ToList());
                entries = adjustable.Select(zone => CreateEntry(
                        zone, supportBoundsByZone, patchBoundsByZone, settings))
                    .Where(entry => entry != null)
                    .Cast<Entry>()
                    .ToList();
                entryByZone = entries.ToDictionary(entry => entry.Zone);
            }

            ResolveResidualGeometryConflicts(entries, alreadyCovered, zonesByLayer,
                slabOutline, openings, result);
            var lostCoverage = alreadyCovered.Where(item =>
                !IsCovered(item, zonesByLayer, slabOutline, openings)).ToList();
            if (lostCoverage.Count > 0)
            {
                var toRestore = new HashSet<AdditionalZone>();
                foreach (var lost in lostCoverage)
                {
                    var relatedZones = adjustable.Where(zone => zone.Layer == lost.Layer &&
                        (zone.NodeIds.Contains(lost.Plate.Id) || IntersectsFootprint(zone, lost.Plate)))
                        .ToHashSet();
                    var zonesToRestore = new HashSet<AdditionalZone>();
                    if (result.CoverageLossDetails.Count < 30)
                    {
                        var members = relatedZones.Select(zone =>
                        {
                            var bounds = Bounds(zone.Contour);
                            var cross = Cross(bounds, zone.Direction);
                            return $"{zone.Layer}/{zone.Direction} Ø{zone.DiameterMm}/{zone.BarStepMm} " +
                                   $"As{zone.AsCoveredCm2PerM:0.##} {cross.Min:0.###}..{cross.Max:0.###}";
                        });
                        result.CoverageLossDetails.Add(
                            $"КЭ {lost.Plate.Id} {lost.Layer}, треб. As={lost.RequiredAs:0.##}; " +
                            $"связанные зоны: {string.Join(" | ", members)}");
                    }
                    if (lost.BridgeFirst != null && lost.BridgeSecond != null)
                    {
                        zonesToRestore.Add(lost.BridgeFirst);
                        zonesToRestore.Add(lost.BridgeSecond);
                    }
                    else if (lost.DirectWitness != null)
                        zonesToRestore.Add(lost.DirectWitness);
                    else
                        zonesToRestore.UnionWith(lost.CoverageCandidates.Where(snapshots.ContainsKey));
                    if (zonesToRestore.Count == 0)
                        zonesToRestore.UnionWith(relatedZones);
                    if (zonesToRestore.Count == 0)
                        zonesToRestore.UnionWith(adjustable.Where(zone => zone.Layer == lost.Layer));
                    toRestore.UnionWith(zonesToRestore);
                }
                foreach (var zone in toRestore)
                {
                    if (!snapshots.TryGetValue(zone, out var snapshot)) continue;
                    Restore(zone, snapshot);
                    if (NormalizeRestoredZoneWidth(zone)) result.NormalizedWidths++;
                    if (entryByZone.TryGetValue(zone, out var entry)) UpdateEntryFromZone(entry);
                }

                ResolveResidualGeometryConflicts(entries, alreadyCovered, zonesByLayer,
                    slabOutline, openings, result);
                TryRearrangeCurrentConflictComponents(entries, alreadyCovered, zonesByLayer,
                    settings, slabOutline, openings, result);
                result.RolledBackForCoverage = true;
                result.Warning = "Часть компонентов возвращена к исходному положению, чтобы сохранить покрытие ранее закрытых КЭ.";
            }

            UpdateResidualConflictCounts(entries, result);
            if (result.ResidualIntersections > 0 || result.ResidualSpacingConflicts > 0 ||
                result.UnresolvedWidths > 0)
                result.Warning = "Не все соседние зоны удалось нормировать по ширине и точному зазору в пределах допуска одного КЭ без потери покрытия.";
            return result;
        }

        public static ZonePatchNeighborLayoutResult ResolveContainedOverlaps(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> supportBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            var result = new ZonePatchNeighborLayoutResult();
            if (zones == null || zones.Count < 2 || plates == null || settings == null)
                return result;

            ResolveContainedZoneOverlaps(zones, supportBoundsByZone, patchBoundsByZone,
                plates, settings, slabOutline, openings, result);
            return result;
        }

        private static bool TryRearrangeCurrentConflictComponents(
            IList<Entry> entries,
            IList<CoveredElement> coveredElements,
            IDictionary<RebarLayer, IList<AdditionalZone>> zonesByLayer,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            ZonePatchNeighborLayoutResult result)
        {
            var rearrangedAny = false;
            foreach (var group in entries.GroupBy(entry => (entry.Zone.Layer, entry.Zone.Direction)))
            {
                var lanes = BuildLanes(group, settings.EffectiveMinZoneWidthM)
                    .OrderBy(lane => lane.SupportCrossCenter).ToList();
                RestrictOverrunToPatchEdges(lanes, Math.Max(0, settings.GridCellMm) / 1000.0);
                var laneIndexByZone = lanes.SelectMany((lane, index) => lane.Entries
                        .Select(entry => (entry.Zone, Index: index)))
                    .ToDictionary(item => item.Zone, item => item.Index);
                AssignDirectCoverageWitnesses(lanes, laneIndexByZone,
                    entries.ToDictionary(entry => entry.Zone), coveredElements,
                    group.Key.Layer, group.Key.Direction);
                var edges = BuildLaneEdges(lanes);
                AddCoverageBridgeEdges(lanes, laneIndexByZone, coveredElements,
                    group.Key.Layer, group.Key.Direction, edges);

                foreach (var component in LaneComponents(lanes.Count, edges))
                {
                    if (!component.Any(index => edges.Any(edge =>
                            edge.Left == index || edge.Right == index))) continue;
                    var orderedComponent = component
                        .OrderBy(index => lanes[index].SupportCrossCenter)
                        .ThenBy(index => index)
                        .ToList();
                    var localIndex = orderedComponent.Select((global, local) => (global, local))
                        .ToDictionary(pair => pair.global, pair => pair.local);
                    var componentLanes = orderedComponent.Select(index => lanes[index]).ToList();
                    var componentEdges = edges
                        .Where(edge => localIndex.ContainsKey(edge.Left) && localIndex.ContainsKey(edge.Right))
                        .Select(edge => new LaneEdge
                        {
                            Left = localIndex[edge.Left],
                            Right = localIndex[edge.Right],
                            MinimumGapM = edge.MinimumGapM,
                            MaximumGapM = edge.MaximumGapM
                        }).ToList();
                    var componentZones = componentLanes.SelectMany(lane => lane.Entries)
                        .Select(entry => entry.Zone).ToHashSet();
                    var affectedCoverage = coveredElements.Where(item =>
                        item.CoverageCandidates.Any(componentZones.Contains) ||
                        (item.DirectWitness != null && componentZones.Contains(item.DirectWitness)) ||
                        (item.BridgeFirst != null && componentZones.Contains(item.BridgeFirst)) ||
                        (item.BridgeSecond != null && componentZones.Contains(item.BridgeSecond))).ToList();
                    var zoneSnapshots = componentZones.ToDictionary(zone => zone, Capture);
                    var laneSnapshots = componentLanes.Select(lane =>
                        (lane.CrossMin, lane.CrossMax, lane.PreferredStart)).ToList();
                    var allowedBounds = componentLanes
                        .Select(lane => (Min: lane.AllowedCrossMin, Max: lane.AllowedCrossMax)).ToList();
                    var shrinkCount = result.ShrunkConflictZones;

                    RestrictComponentToFixedNeighbors(lanes, orderedComponent, componentLanes);
                    var arranged = TryArrangeComponent(componentLanes, componentEdges,
                        out var starts, out _);
                    if (!arranged)
                        arranged = TryShrinkAndArrangeComponent(componentLanes, componentEdges,
                            out starts, out _, result);
                    RestoreAllowedBounds(componentLanes, allowedBounds);
                    if (arranged)
                    {
                        ApplyLaneStarts(componentLanes, starts, result);
                        foreach (var entry in componentLanes.SelectMany(lane => lane.Entries))
                            ApplyInterval(entry);
                        arranged = affectedCoverage.All(item =>
                            IsCovered(item, zonesByLayer, slabOutline, openings));
                    }

                    if (!arranged)
                    {
                        result.ShrunkConflictZones = shrinkCount;
                        for (var i = 0; i < componentLanes.Count; i++)
                        {
                            componentLanes[i].CrossMin = laneSnapshots[i].CrossMin;
                            componentLanes[i].CrossMax = laneSnapshots[i].CrossMax;
                            componentLanes[i].PreferredStart = laneSnapshots[i].PreferredStart;
                        }
                        foreach (var entry in componentLanes.SelectMany(lane => lane.Entries))
                        {
                            Restore(entry.Zone, zoneSnapshots[entry.Zone]);
                            UpdateEntryFromZone(entry);
                        }
                        continue;
                    }

                    rearrangedAny = true;
                }
            }

            if (rearrangedAny)
                ResolveResidualGeometryConflicts(entries, coveredElements, zonesByLayer,
                    slabOutline, openings, result);
            return rearrangedAny;
        }

        private static Entry? CreateEntry(
            AdditionalZone zone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> supportBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> patchBoundsByZone,
            AnalysisSettings settings)
        {
            if (zone.Contour == null || zone.Contour.Count < 3 || zone.BarStepMm <= 0) return null;
            var bounds = Bounds(zone.Contour);
            var support = supportBoundsByZone != null && supportBoundsByZone.TryGetValue(zone, out var foundSupport)
                ? foundSupport
                : bounds;
            var patch = patchBoundsByZone != null && patchBoundsByZone.TryGetValue(zone, out var foundPatch)
                ? foundPatch
                : support;
            var direction = zone.Direction;
            var patchCross = Cross(patch, direction);
            var cross = Cross(bounds, direction);
            var axial = Axial(bounds, direction);
            var supportAxial = Axial(support, direction);
            var barLengthM = axial.Max - axial.Min;
            var anchorageM = RebarTables.AnchorageLenMm(
                settings.ConcreteClass, zone.DiameterMm) / 1000.0;
            var allowedAxialStartMin = supportAxial.Max + anchorageM - barLengthM;
            var allowedAxialStartMax = supportAxial.Min - anchorageM;
            if (allowedAxialStartMin > allowedAxialStartMax + GeometryToleranceM)
            {
                allowedAxialStartMin = axial.Min;
                allowedAxialStartMax = axial.Min;
            }
            return new Entry
            {
                Zone = zone,
                Support = support,
                Patch = patch,
                CrossMin = cross.Min,
                CrossMax = cross.Max,
                AxialMin = axial.Min,
                AxialMax = axial.Max,
                AllowedCrossMin = patchCross.Min,
                AllowedCrossMax = patchCross.Max,
                AllowedAxialStartMin = allowedAxialStartMin,
                AllowedAxialStartMax = allowedAxialStartMax,
                Capacity = zone.AsCoveredCm2PerM > 0
                    ? zone.AsCoveredCm2PerM
                    : BarCapacity.AsCm2PerM(zone.DiameterMm, zone.BarStepMm),
                StepMm = zone.BarStepMm
            };
        }

        private static List<Lane> BuildLanes(IEnumerable<Entry> entries, double minimumWidthM)
        {
            return entries.GroupBy(entry =>
            {
                var support = Cross(entry.Support, entry.Zone.Direction);
                return (entry.Zone.Layer, entry.Zone.Direction, entry.Zone.DiameterMm, entry.StepMm,
                    Min: Math.Round(support.Min, 6), Max: Math.Round(support.Max, 6));
            }).Select(group =>
            {
                var members = group.ToList();
                var supports = members.Select(entry => Cross(entry.Support, entry.Zone.Direction)).ToList();
                var currentMin = members.Min(entry => entry.CrossMin);
                var currentMax = members.Max(entry => entry.CrossMax);
                return new Lane
                {
                    Entries = members,
                    SupportCrossMin = supports.Min(interval => interval.Min),
                    SupportCrossMax = supports.Max(interval => interval.Max),
                    AllowedCrossMin = members.Max(entry => entry.AllowedCrossMin),
                    AllowedCrossMax = members.Min(entry => entry.AllowedCrossMax),
                    CrossMin = currentMin,
                    CrossMax = currentMax,
                    PreferredStart = members.Average(entry => entry.CrossMin),
                    Capacity = members.Min(entry => entry.Capacity),
                    ReinforcementAreaPriority = members.Min(entry => entry.Capacity) *
                        (supports.Max(interval => interval.Max) - supports.Min(interval => interval.Min)),
                    StepMm = members[0].StepMm,
                    MinimumWidthM = Math.Max(0, minimumWidthM)
                };
            }).OrderBy(lane => lane.SupportCrossCenter).ToList();
        }

        private static void RestrictOverrunToPatchEdges(IList<Lane> lanes, double maxOverrunM)
        {
            var remaining = new HashSet<int>(Enumerable.Range(0, lanes.Count));
            while (remaining.Count > 0)
            {
                var component = new List<int>();
                var queue = new Queue<int>();
                var start = remaining.First();
                remaining.Remove(start);
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    component.Add(current);
                    foreach (var candidate in remaining.ToList())
                    {
                        if (!HasAxialOverlap(lanes[current], lanes[candidate]) ||
                            !AreAdjacentSupportLanes(lanes[current], lanes[candidate]))
                            continue;
                        remaining.Remove(candidate);
                        queue.Enqueue(candidate);
                    }
                }

                var componentLanes = component.Select(index => lanes[index]).ToList();
                var componentEntries = componentLanes.SelectMany(lane => lane.Entries).ToList();
                var patchIntervals = componentEntries.Select(entry => Cross(entry.Patch, entry.Zone.Direction)).ToList();
                var patchMin = patchIntervals.Min(interval => interval.Min);
                var patchMax = patchIntervals.Max(interval => interval.Max);
                var supportMin = componentLanes.Min(lane => lane.SupportCrossMin);
                var supportMax = componentLanes.Max(lane => lane.SupportCrossMax);

                foreach (var lane in componentLanes)
                {
                    var allowedMin = patchMin;
                    var allowedMax = patchMax;
                    if (lane.SupportCrossMin <= supportMin + GeometryToleranceM)
                        allowedMin -= maxOverrunM;
                    if (lane.SupportCrossMax >= supportMax - GeometryToleranceM)
                        allowedMax += maxOverrunM;
                    lane.AllowedCrossMin = allowedMin;
                    lane.AllowedCrossMax = allowedMax;
                    foreach (var entry in lane.Entries)
                    {
                        entry.AllowedCrossMin = allowedMin;
                        entry.AllowedCrossMax = allowedMax;
                    }
                }
            }
        }

        private static bool TryNormalizeLaneWidth(Lane lane)
        {
            var stepM = lane.StepMm / 1000.0;
            var currentWidth = lane.CrossMax - lane.CrossMin;
            var supportWidth = lane.SupportCrossMax - lane.SupportCrossMin;
            var targetWidth = RoundUpToStep(Math.Max(currentWidth, supportWidth), stepM);
            if (targetWidth > lane.AllowedCrossMax - lane.AllowedCrossMin + GeometryToleranceM)
                return false;

            var minimumStart = Math.Max(lane.AllowedCrossMin, lane.SupportCrossMax - targetWidth);
            var maximumStart = Math.Min(lane.AllowedCrossMax - targetWidth, lane.SupportCrossMin);
            if (minimumStart > maximumStart + GeometryToleranceM) return false;

            var preferred = lane.PreferredStart;
            lane.CrossMin = Clamp(preferred, minimumStart, maximumStart);
            lane.CrossMax = lane.CrossMin + targetWidth;
            return true;
        }

        private static void AssignDirectCoverageWitnesses(
            IList<Lane> lanes,
            IDictionary<AdditionalZone, int> laneIndexByZone,
            IDictionary<AdditionalZone, Entry> entriesByZone,
            IList<CoveredElement> coveredElements,
            RebarLayer layer,
            ZoneDirection direction)
        {
            foreach (var item in coveredElements.Where(item => item.Layer == layer &&
                         item.DirectWitness != null && item.Plate.Contour != null &&
                         item.Plate.Contour.Count >= 3))
            {
                var plateBounds = Bounds(item.Plate.Contour);
                var plateCross = Cross(plateBounds, direction);
                var plateAxial = Axial(plateBounds, direction);
                var candidates = item.CoverageCandidates
                    .Where(zone => zone.Direction == direction &&
                                   laneIndexByZone.ContainsKey(zone) && entriesByZone.ContainsKey(zone))
                    .Select(zone => (Zone: zone, Lane: lanes[laneIndexByZone[zone]],
                        Entry: entriesByZone[zone]))
                    .Where(candidate => CapacityOf(candidate.Zone) + CoverageTolerance >= item.RequiredAs &&
                        candidate.Entry.AxialMin <= plateAxial.Min + GeometryToleranceM &&
                        candidate.Entry.AxialMax >= plateAxial.Max - GeometryToleranceM &&
                        candidate.Lane.CrossMin <= plateCross.Min + GeometryToleranceM &&
                        candidate.Lane.CrossMax >= plateCross.Max - GeometryToleranceM)
                    .OrderByDescending(candidate => candidate.Lane.Capacity)
                    .ThenByDescending(candidate => Math.Min(
                        plateCross.Min - candidate.Lane.CrossMin,
                        candidate.Lane.CrossMax - plateCross.Max))
                    .ThenBy(candidate => candidate.Lane.SupportCrossCenter)
                    .ToList();

                if (candidates.Count > 1 ||
                    (candidates.Count == 0 && item.BridgeFirst != null && item.BridgeSecond != null))
                    continue;
                var witness = candidates.FirstOrDefault();
                if (witness.Zone == null && item.DirectWitness != null &&
                    laneIndexByZone.TryGetValue(item.DirectWitness, out var directIndex) &&
                    entriesByZone.TryGetValue(item.DirectWitness, out var directEntry) &&
                    directEntry.AxialMin <= plateAxial.Min + GeometryToleranceM &&
                    directEntry.AxialMax >= plateAxial.Max - GeometryToleranceM &&
                    TryExpandLaneToContain(lanes[directIndex], plateCross))
                    witness = (item.DirectWitness, lanes[directIndex], directEntry);

                if (witness.Zone == null) continue;
                item.DirectLaneWitness = witness.Lane;
                witness.Lane.CoverageCrossMin = Math.Min(witness.Lane.CoverageCrossMin, plateCross.Min);
                witness.Lane.CoverageCrossMax = Math.Max(witness.Lane.CoverageCrossMax, plateCross.Max);
            }
        }

        private static bool TryExpandLaneToContain(Lane lane, (double Min, double Max) cross)
        {
            var stepM = lane.StepMm / 1000.0;
            var width = lane.CrossMax - lane.CrossMin;
            var protectedMin = Math.Min(lane.CoverageCrossMin, cross.Min);
            var protectedMax = Math.Max(lane.CoverageCrossMax, cross.Max);
            var protectedWidth = double.IsInfinity(protectedMin) ? 0 : protectedMax - protectedMin;
            var targetWidth = RoundUpToStep(Math.Max(
                Math.Max(width, lane.SupportCrossMax - lane.SupportCrossMin), protectedWidth), stepM);
            if (targetWidth > lane.AllowedCrossMax - lane.AllowedCrossMin + GeometryToleranceM)
                return false;

            var minimumStart = Math.Max(lane.AllowedCrossMin,
                Math.Max(lane.SupportCrossMax - targetWidth, protectedMax - targetWidth));
            var maximumStart = Math.Min(lane.AllowedCrossMax - targetWidth,
                Math.Min(lane.SupportCrossMin, protectedMin));
            if (minimumStart > maximumStart + GeometryToleranceM) return false;
            lane.CrossMin = Clamp(lane.CrossMin, minimumStart, maximumStart);
            lane.CrossMax = lane.CrossMin + targetWidth;
            return true;
        }

        private static void TrimNonPeakOverhangs(IList<Lane> lanes, ZonePatchNeighborLayoutResult result)
        {
            for (var i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                var left = lanes.Take(i).Reverse().FirstOrDefault(candidate =>
                    AreAdjacentSupportLanes(candidate, lane) && HasAxialOverlap(candidate, lane));
                var right = lanes.Skip(i + 1).FirstOrDefault(candidate =>
                    AreAdjacentSupportLanes(lane, candidate) && HasAxialOverlap(candidate, lane));
                if (left == null || right == null ||
                    !HasLowerReinforcementPriority(lane, left) ||
                    !HasLowerReinforcementPriority(lane, right))
                    continue;

                var stepM = lane.StepMm / 1000.0;
                var protectedWidth = lane.CoverageCrossMin <= lane.CoverageCrossMax
                    ? lane.CoverageCrossMax - lane.CoverageCrossMin
                    : 0;
                var targetWidth = RoundUpToStep(Math.Max(
                    Math.Max(lane.SupportCrossMax - lane.SupportCrossMin, protectedWidth),
                    Math.Max(stepM, lane.MinimumWidthM)), stepM);
                if (targetWidth >= lane.CrossMax - lane.CrossMin - GeometryToleranceM) continue;
                if (!TrySetLaneWidth(lane, targetWidth)) continue;
                result.ShrunkWeakZones += lane.Entries.Count;
            }
        }

        private static bool TrySetLaneWidth(Lane lane, double targetWidth)
        {
            var stepM = lane.StepMm / 1000.0;
            var protectedWidth = lane.CoverageCrossMin <= lane.CoverageCrossMax
                ? lane.CoverageCrossMax - lane.CoverageCrossMin
                : 0;
            var minimumWidth = RoundUpToStep(Math.Max(
                Math.Max(stepM, lane.MinimumWidthM), protectedWidth), stepM);
            if (targetWidth < minimumWidth - GeometryToleranceM) return false;
            if (targetWidth > lane.AllowedCrossMax - lane.AllowedCrossMin + GeometryToleranceM)
                return false;
            var minimumStart = Math.Max(lane.AllowedCrossMin, lane.SupportCrossMax - targetWidth);
            var maximumStart = Math.Min(lane.AllowedCrossMax - targetWidth, lane.SupportCrossMin);
            if (lane.CoverageCrossMin <= lane.CoverageCrossMax)
            {
                minimumStart = Math.Max(minimumStart, lane.CoverageCrossMax - targetWidth);
                maximumStart = Math.Min(maximumStart, lane.CoverageCrossMin);
            }
            if (minimumStart > maximumStart + GeometryToleranceM) return false;
            lane.CrossMin = Clamp(lane.CrossMin, minimumStart, maximumStart);
            lane.CrossMax = lane.CrossMin + targetWidth;
            return true;
        }

        private static bool TryShrinkAndArrangeComponent(
            IList<Lane> lanes, IList<LaneEdge> edges,
            out double[] starts, out int unresolvedEdges,
            ZonePatchNeighborLayoutResult? result)
        {
            TryArrangeComponentByPriority(lanes, edges, out var originalStarts);
            var originalUnresolved = CountUnsatisfiedEdges(lanes, edges, originalStarts);
            var candidates = new Dictionary<int, double>();
            foreach (var edge in edges)
            {
                var shortage = edge.MinimumGapM -
                               (originalStarts[edge.Right] - (originalStarts[edge.Left] +
                                   (lanes[edge.Left].CrossMax - lanes[edge.Left].CrossMin)));
                if (shortage <= GeometryToleranceM) continue;
                var candidateIndex = SelectConflictShrinkCandidate(
                    lanes[edge.Left], lanes[edge.Right], edge.Left, edge.Right);
                if (candidateIndex < 0) continue;
                candidates[candidateIndex] = Math.Max(
                    candidates.TryGetValue(candidateIndex, out var existing) ? existing : 0,
                    shortage);
            }

            if (candidates.Count == 0)
            {
                starts = originalStarts;
                unresolvedEdges = originalUnresolved;
                return false;
            }

            var snapshots = lanes.Select(lane =>
                (lane.CrossMin, lane.CrossMax, lane.PreferredStart)).ToArray();
            var reduced = new HashSet<int>();
            foreach (var candidate in candidates.OrderByDescending(item => item.Value)
                         .ThenBy(item => lanes[item.Key].ReinforcementAreaPriority))
            {
                var lane = lanes[candidate.Key];
                var stepM = lane.StepMm / 1000.0;
                var protectedWidth = lane.CoverageCrossMin <= lane.CoverageCrossMax
                    ? lane.CoverageCrossMax - lane.CoverageCrossMin
                    : 0;
                var minimumWidth = RoundUpToStep(Math.Max(
                    Math.Max(stepM, lane.MinimumWidthM), protectedWidth), stepM);
                while (lane.CrossMax - lane.CrossMin - stepM >=
                       minimumWidth - GeometryToleranceM)
                {
                    var targetWidth = lane.CrossMax - lane.CrossMin - stepM;
                    if (!TrySetLaneWidthForConflict(lane, targetWidth)) break;
                    reduced.Add(candidate.Key);
                    if (TryArrangeComponent(lanes, edges, out starts, out _))
                    {
                        unresolvedEdges = 0;
                        if (result != null)
                            result.ShrunkConflictZones += reduced.Sum(index =>
                                lanes[index].Entries.Count);
                        return true;
                    }
                }
            }

            if (reduced.Count == 0)
            {
                starts = originalStarts;
                unresolvedEdges = originalUnresolved;
                return false;
            }

            if (TryArrangeComponent(lanes, edges, out starts, out _))
            {
                unresolvedEdges = 0;
                if (result != null)
                    result.ShrunkConflictZones += reduced.Sum(index => lanes[index].Entries.Count);
                return true;
            }

            TryArrangeComponentByPriority(lanes, edges, out var reducedStarts);
            var reducedUnresolved = CountUnsatisfiedEdges(lanes, edges, reducedStarts);
            if (reducedUnresolved < originalUnresolved)
            {
                starts = reducedStarts;
                unresolvedEdges = reducedUnresolved;
                if (result != null)
                    result.ShrunkConflictZones += reduced.Sum(index => lanes[index].Entries.Count);
                return false;
            }

            for (var i = 0; i < lanes.Count; i++)
            {
                lanes[i].CrossMin = snapshots[i].CrossMin;
                lanes[i].CrossMax = snapshots[i].CrossMax;
                lanes[i].PreferredStart = snapshots[i].PreferredStart;
            }
            starts = originalStarts;
            unresolvedEdges = originalUnresolved;
            return false;
        }

        private static int SelectConflictShrinkCandidate(
            Lane first, Lane second, int firstIndex, int secondIndex)
        {
            if (first.StepMm != second.StepMm)
                return first.StepMm < second.StepMm ? firstIndex : secondIndex;

            if (first.Entries[0].Zone.DiameterMm == second.Entries[0].Zone.DiameterMm)
            {
                var firstLengths = first.Entries.Select(entry => entry.Zone.LengthMm).ToList();
                var secondLengths = second.Entries.Select(entry => entry.Zone.LengthMm).ToList();
                if (firstLengths.Max() < secondLengths.Min() - 1)
                    return firstIndex;
                if (secondLengths.Max() < firstLengths.Min() - 1)
                    return secondIndex;
            }

            if (first.ReinforcementAreaPriority <
                second.ReinforcementAreaPriority - CoverageTolerance)
                return firstIndex;
            if (second.ReinforcementAreaPriority <
                first.ReinforcementAreaPriority - CoverageTolerance)
                return secondIndex;

            var firstOverrun = PatchOverrunBeyondStep(first);
            var secondOverrun = PatchOverrunBeyondStep(second);
            if (firstOverrun > GeometryToleranceM || secondOverrun > GeometryToleranceM)
                return firstOverrun >= secondOverrun ? firstIndex : secondIndex;

            return -1;
        }

        private static double PatchOverrunBeyondStep(Lane lane)
        {
            var stepM = lane.StepMm / 1000.0;
            var largestOverrunM = 0.0;
            foreach (var entry in lane.Entries)
            {
                var patch = Cross(entry.Patch, entry.Zone.Direction);
                var overrunM = Math.Max(patch.Min - lane.CrossMin, lane.CrossMax - patch.Max);
                largestOverrunM = Math.Max(largestOverrunM, overrunM - stepM);
            }
            return largestOverrunM;
        }

        private static bool TrySetLaneWidthForConflict(Lane lane, double targetWidth)
        {
            if (targetWidth <= 0 ||
                targetWidth > lane.AllowedCrossMax - lane.AllowedCrossMin + GeometryToleranceM)
                return false;

            var minimumStart = lane.AllowedCrossMin;
            var maximumStart = lane.AllowedCrossMax - targetWidth;
            if (lane.CoverageCrossMin <= lane.CoverageCrossMax)
            {
                minimumStart = Math.Max(minimumStart, lane.CoverageCrossMax - targetWidth);
                maximumStart = Math.Min(maximumStart, lane.CoverageCrossMin);
            }
            if (minimumStart > maximumStart + GeometryToleranceM) return false;
            var centeredStart = (lane.CrossMin + lane.CrossMax - targetWidth) * 0.5;
            lane.CrossMin = Clamp(centeredStart, minimumStart, maximumStart);
            lane.CrossMax = lane.CrossMin + targetWidth;
            lane.PreferredStart = lane.CrossMin;
            return true;
        }

        private static void ShrinkWeakInteriorLanes(
            IList<Lane> lanes,
            IList<CoveredElement> coveredElements,
            ZonePatchNeighborLayoutResult result)
        {
            for (var i = 0; i < lanes.Count; i++)
            {
                var middle = lanes[i];
                var left = lanes.Take(i).Reverse().FirstOrDefault(candidate =>
                    AreAdjacentSupportLanes(candidate, middle) && HasAxialOverlap(candidate, middle));
                var right = lanes.Skip(i + 1).FirstOrDefault(candidate =>
                    AreAdjacentSupportLanes(middle, candidate) && HasAxialOverlap(candidate, middle));
                if (result.PriorityDetails.Count < 20 && left != null && right != null)
                    result.PriorityDetails.Add(
                        $"{middle.SupportCrossMin:0.###}..{middle.SupportCrossMax:0.###}: " +
                        $"area {middle.ReinforcementAreaPriority:0.##} vs " +
                        $"{left.ReinforcementAreaPriority:0.##}/{right.ReinforcementAreaPriority:0.##}, " +
                        $"width {middle.CrossMax - middle.CrossMin:0.###}, " +
                        $"step {middle.StepMm}, slot {right.SupportCrossMin - left.SupportCrossMax:0.###}");
                if (left == null || right == null ||
                    !HasLowerReinforcementPriority(middle, left) ||
                    !HasLowerReinforcementPriority(middle, right))
                    continue;

                foreach (var item in coveredElements.Where(item =>
                             item.DirectLaneWitness == middle && !item.CoverageTransferred))
                {
                    if (!CanTransferCoverageToFlanks(item, left, middle, right)) continue;
                    item.CoverageTransferred = true;
                    result.TransferredCoverageElements++;
                }
                RecalculateLaneCoverage(middle, coveredElements);

                var currentWidth = middle.CrossMax - middle.CrossMin;
                var stepM = middle.StepMm / 1000.0;
                var protectedWidth = middle.CoverageCrossMin <= middle.CoverageCrossMax
                    ? middle.CoverageCrossMax - middle.CoverageCrossMin
                    : 0;
                var minimumWidth = RoundUpToStep(Math.Max(
                    Math.Max(stepM, middle.MinimumWidthM), protectedWidth), stepM);
                var leftGap = Math.Min(left.StepMm, middle.StepMm) / 1000.0;
                var rightGap = Math.Min(middle.StepMm, right.StepMm) / 1000.0;
                var supportSlot = right.SupportCrossMin - left.SupportCrossMax - leftGap - rightGap;
                var targetWidth = RoundDownToStep(Math.Max(0, supportSlot), stepM);
                if (targetWidth < minimumWidth - GeometryToleranceM)
                    targetWidth = minimumWidth;
                if (result.PriorityDetails.Count < 20)
                    result.PriorityDetails.Add(
                        $"target {targetWidth:0.###}, minimum {minimumWidth:0.###}, " +
                        $"width {currentWidth:0.###}, weak={HasLowerReinforcementPriority(middle, left)}/" +
                        $"{HasLowerReinforcementPriority(middle, right)}");
                if (targetWidth >= currentWidth - GeometryToleranceM) continue;

                var minimumStart = Math.Max(middle.AllowedCrossMin, left.CrossMax);
                var maximumStart = Math.Min(middle.AllowedCrossMax - targetWidth,
                    right.CrossMin - targetWidth);
                if (middle.CoverageCrossMin <= middle.CoverageCrossMax)
                {
                    minimumStart = Math.Max(minimumStart, middle.CoverageCrossMax - targetWidth);
                    maximumStart = Math.Min(maximumStart, middle.CoverageCrossMin);
                }
                if (minimumStart > maximumStart + GeometryToleranceM) continue;
                middle.CrossMin = Clamp(middle.CrossMin, minimumStart, maximumStart);
                middle.CrossMax = middle.CrossMin + targetWidth;
                result.ShrunkWeakZones += middle.Entries.Count;
            }
        }

        private static bool CanTransferCoverageToFlanks(
            CoveredElement item, Lane left, Lane middle, Lane right)
        {
            if (item.Plate.Contour == null || item.Plate.Contour.Count < 3 ||
                Math.Min(left.Capacity, right.Capacity) + CoverageTolerance < item.RequiredAs)
                return false;
            var plateBounds = Bounds(item.Plate.Contour);
            var direction = middle.Entries[0].Zone.Direction;
            var plateCross = Cross(plateBounds, direction);
            var plateAxial = Axial(plateBounds, direction);
            if (plateCross.Min < left.SupportCrossMin - GeometryToleranceM ||
                plateCross.Max > right.SupportCrossMax + GeometryToleranceM)
                return false;
            var leftCoversAxially = left.Entries.Any(entry =>
                entry.AxialMin <= plateAxial.Min + GeometryToleranceM &&
                entry.AxialMax >= plateAxial.Max - GeometryToleranceM);
            var rightCoversAxially = right.Entries.Any(entry =>
                entry.AxialMin <= plateAxial.Min + GeometryToleranceM &&
                entry.AxialMax >= plateAxial.Max - GeometryToleranceM);
            return leftCoversAxially && rightCoversAxially &&
                   AreAdjacentSupportLanes(left, middle) && AreAdjacentSupportLanes(middle, right);
        }

        private static void RecalculateLaneCoverage(Lane lane, IEnumerable<CoveredElement> coveredElements)
        {
            var protectedElements = coveredElements.Where(item =>
                item.DirectLaneWitness == lane && !item.CoverageTransferred &&
                item.Plate.Contour != null && item.Plate.Contour.Count >= 3).ToList();
            lane.CoverageCrossMin = double.PositiveInfinity;
            lane.CoverageCrossMax = double.NegativeInfinity;
            foreach (var item in protectedElements)
            {
                var plateCross = Cross(Bounds(item.Plate.Contour!), lane.Entries[0].Zone.Direction);
                lane.CoverageCrossMin = Math.Min(lane.CoverageCrossMin, plateCross.Min);
                lane.CoverageCrossMax = Math.Max(lane.CoverageCrossMax, plateCross.Max);
            }
        }

        private static List<LaneEdge> BuildLaneEdges(IList<Lane> lanes)
        {
            var result = new Dictionary<(int Left, int Right), LaneEdge>();

            for (var left = 0; left < lanes.Count; left++)
            for (var right = left + 1; right < lanes.Count; right++)
            {
                if (!HasAxialOverlap(lanes[left], lanes[right])) continue;
                var crossOverlap = Math.Min(lanes[left].CrossMax, lanes[right].CrossMax) -
                                   Math.Max(lanes[left].CrossMin, lanes[right].CrossMin);
                if (crossOverlap <= GeometryToleranceM) continue;
                result[(left, right)] = new LaneEdge
                {
                    Left = left,
                    Right = right,
                    MinimumGapM = 0,
                    MaximumGapM = double.PositiveInfinity
                };
            }

            var axialCuts = lanes.SelectMany(lane => lane.Entries)
                .SelectMany(entry => new[] { entry.AxialMin, entry.AxialMax })
                .OrderBy(value => value)
                .Aggregate(new List<double>(), (cuts, value) =>
                {
                    if (cuts.Count == 0 || Math.Abs(cuts[cuts.Count - 1] - value) > GeometryToleranceM)
                        cuts.Add(value);
                    return cuts;
                });

            for (var band = 0; band + 1 < axialCuts.Count; band++)
            {
                if (axialCuts[band + 1] - axialCuts[band] <= GeometryToleranceM) continue;
                var midpoint = (axialCuts[band] + axialCuts[band + 1]) * 0.5;
                var active = Enumerable.Range(0, lanes.Count)
                    .Where(index => lanes[index].Entries.Any(entry =>
                        midpoint > entry.AxialMin - GeometryToleranceM &&
                        midpoint < entry.AxialMax + GeometryToleranceM))
                    .ToList();
                for (var i = 0; i + 1 < active.Count; i++)
                {
                    var left = active[i];
                    var right = active[i + 1];
                    if (!AreAdjacentSupportLanes(lanes[left], lanes[right])) continue;
                    var key = (left, right);
                    var maximumGap = Math.Min(lanes[left].StepMm, lanes[right].StepMm) / 1000.0;
                    if (!result.TryGetValue(key, out var edge))
                    {
                        edge = new LaneEdge
                        {
                            Left = left,
                            Right = right,
                            MaximumGapM = double.PositiveInfinity
                        };
                        result[key] = edge;
                    }
                    edge.MinimumGapM = Math.Max(edge.MinimumGapM, maximumGap);
                    edge.MaximumGapM = Math.Min(edge.MaximumGapM, maximumGap);
                }
            }
            return result.Values.ToList();
        }

        private static void RestrictComponentToFixedNeighbors(
            IList<Lane> allLanes, IList<int> componentIndices, IList<Lane> componentLanes)
        {
            var componentSet = new HashSet<int>(componentIndices);
            for (var local = 0; local < componentIndices.Count; local++)
            {
                var global = componentIndices[local];
                var lane = componentLanes[local];
                for (var blockerIndex = 0; blockerIndex < allLanes.Count; blockerIndex++)
                {
                    if (componentSet.Contains(blockerIndex)) continue;
                    var blocker = allLanes[blockerIndex];
                    if (!HasAxialOverlap(lane, blocker)) continue;
                    if (blockerIndex < global)
                        lane.AllowedCrossMin = Math.Max(lane.AllowedCrossMin, blocker.CrossMax);
                    else
                        lane.AllowedCrossMax = Math.Min(lane.AllowedCrossMax, blocker.CrossMin);
                }
            }
        }

        private static void RestoreAllowedBounds(
            IList<Lane> lanes, IList<(double Min, double Max)> bounds)
        {
            for (var i = 0; i < lanes.Count; i++)
            {
                lanes[i].AllowedCrossMin = bounds[i].Min;
                lanes[i].AllowedCrossMax = bounds[i].Max;
            }
        }

        private static void AddCoverageBridgeEdges(
            IList<Lane> lanes,
            IDictionary<AdditionalZone, int> laneIndexByZone,
            IList<CoveredElement> coveredElements,
            RebarLayer layer,
            ZoneDirection direction,
            IList<LaneEdge> edges)
        {
            foreach (var item in coveredElements.Where(item => item.Layer == layer &&
                         item.BridgeFirst != null && item.BridgeSecond != null))
            {
                var firstZone = item.BridgeFirst!;
                var secondZone = item.BridgeSecond!;
                if (firstZone.Direction != direction || secondZone.Direction != direction ||
                    !laneIndexByZone.TryGetValue(firstZone, out var firstIndex) ||
                    !laneIndexByZone.TryGetValue(secondZone, out var secondIndex) ||
                    Math.Abs(firstIndex - secondIndex) != 1)
                    continue;

                var leftIndex = Math.Min(firstIndex, secondIndex);
                var rightIndex = Math.Max(firstIndex, secondIndex);
                var maximumGap = Math.Min(firstZone.BarStepMm, secondZone.BarStepMm) / 1000.0;
                var existing = edges.FirstOrDefault(edge => edge.Left == leftIndex && edge.Right == rightIndex);
                if (existing == null)
                    edges.Add(new LaneEdge
                    {
                        Left = leftIndex,
                        Right = rightIndex,
                        MinimumGapM = maximumGap,
                        MaximumGapM = maximumGap
                    });
                else
                {
                    existing.MinimumGapM = Math.Max(existing.MinimumGapM, maximumGap);
                    existing.MaximumGapM = Math.Min(existing.MaximumGapM, maximumGap);
                }
            }
        }

        private static bool TryArrangeLanes(
            IList<Lane> lanes, IList<LaneEdge> laneEdges,
            out double[] starts, out int unresolvedPairs,
            IList<string>? unresolvedDetails = null,
            ZonePatchNeighborLayoutResult? result = null)
        {
            starts = lanes.Select(lane => lane.CrossMin).ToArray();
            unresolvedPairs = 0;
            foreach (var component in LaneComponents(lanes.Count, laneEdges))
            {
                var orderedComponent = component
                    .OrderBy(index => lanes[index].SupportCrossCenter)
                    .ThenBy(index => index)
                    .ToList();
                var localIndex = orderedComponent.Select((global, local) => (global, local))
                    .ToDictionary(pair => pair.global, pair => pair.local);
                var componentLanes = orderedComponent.Select(index => lanes[index]).ToList();
                var componentEdges = laneEdges
                    .Where(edge => localIndex.ContainsKey(edge.Left) && localIndex.ContainsKey(edge.Right))
                    .Select(edge => new LaneEdge
                    {
                        Left = localIndex[edge.Left],
                        Right = localIndex[edge.Right],
                        MinimumGapM = edge.MinimumGapM,
                        MaximumGapM = edge.MaximumGapM
                    }).ToList();
                var originalAllowedBounds = componentLanes
                    .Select(lane => (Min: lane.AllowedCrossMin, Max: lane.AllowedCrossMax)).ToList();
                RestrictComponentToFixedNeighbors(lanes, orderedComponent, componentLanes);
                if (!TryArrangeComponent(componentLanes, componentEdges, out var componentStarts,
                        out var failureReason))
                {
                    var arrangedAfterShrink = TryShrinkAndArrangeComponent(
                        componentLanes, componentEdges, out componentStarts,
                        out var unsatisfiedEdges, result);
                    if (!arrangedAfterShrink)
                        unresolvedPairs += Math.Max(1, unsatisfiedEdges);
                    if (unresolvedDetails != null && unresolvedDetails.Count < 30)
                    {
                        var lanesText = componentLanes.Select((lane, index) =>
                            $"{index}: [{lane.CrossMin:0.###}..{lane.CrossMax:0.###}] " +
                            $"support [{lane.SupportCrossMin:0.###}..{lane.SupportCrossMax:0.###}] " +
                            $"allowed [{lane.AllowedCrossMin:0.###}..{lane.AllowedCrossMax:0.###}] " +
                            $"cover [{lane.CoverageCrossMin:0.###}..{lane.CoverageCrossMax:0.###}] " +
                            $"As{lane.Capacity:0.##}/AΣ{lane.ReinforcementAreaPriority:0.##}/step{lane.StepMm}");
                        var edgesText = string.Join(", ", componentEdges.Select(edge =>
                            $"{edge.Left}->{edge.Right} gap={edge.MinimumGapM:0.###}..{edge.MaximumGapM:0.###}"));
                        if (!arrangedAfterShrink)
                            unresolvedDetails.Add("Группа неразрешима (" + failureReason + ") [" + edgesText + "]: " +
                                string.Join("; ", lanesText));
                    }
                    if (!arrangedAfterShrink)
                    {
                        for (var i = 0; i < orderedComponent.Count; i++)
                            starts[orderedComponent[i]] = componentStarts[i];
                        RestoreAllowedBounds(componentLanes, originalAllowedBounds);
                        continue;
                    }
                }
                RestoreAllowedBounds(componentLanes, originalAllowedBounds);
                for (var i = 0; i < orderedComponent.Count; i++)
                    starts[orderedComponent[i]] = componentStarts[i];
            }
            return unresolvedPairs == 0;
        }

        private static bool TryArrangeComponent(
            IList<Lane> lanes, IList<LaneEdge> laneEdges, out double[] starts, out string failureReason)
        {
            starts = new double[lanes.Count];
            failureReason = string.Empty;
            var root = lanes.Count;
            var lowerBounds = new double[lanes.Count];
            var upperBounds = new double[lanes.Count];
            var constraints = new List<DifferenceEdge>(lanes.Count * 2 + laneEdges.Count * 2);
            for (var i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                var width = lane.CrossMax - lane.CrossMin;
                var lower = lane.AllowedCrossMin;
                var upper = lane.AllowedCrossMax - width;
                if (lane.CoverageCrossMin <= lane.CoverageCrossMax)
                {
                    lower = Math.Max(lower, lane.CoverageCrossMax - width);
                    upper = Math.Min(upper, lane.CoverageCrossMin);
                }
                if (lower > upper + GeometryToleranceM)
                {
                    failureReason = "ширина/защита КЭ не помещается в допуск";
                    return false;
                }
                lowerBounds[i] = lower;
                upperBounds[i] = upper;
                constraints.Add(new DifferenceEdge { From = root, To = i, Weight = upper });
                constraints.Add(new DifferenceEdge { From = i, To = root, Weight = -lower });
            }

            if (IsArrangementValid(lanes, laneEdges, lowerBounds, upperBounds))
            {
                for (var i = 0; i < lanes.Count; i++) starts[i] = lanes[i].CrossMin;
                return true;
            }

            if (TryArrangeChain(lanes, laneEdges, lowerBounds, upperBounds,
                    out starts, out var chainFailure) &&
                IsStartsArrangementValid(lanes, laneEdges, lowerBounds, upperBounds, starts))
                return true;

            foreach (var edge in laneEdges)
            {
                var leftWidth = lanes[edge.Left].CrossMax - lanes[edge.Left].CrossMin;
                constraints.Add(new DifferenceEdge
                {
                    From = edge.Right,
                    To = edge.Left,
                    Weight = -leftWidth - edge.MinimumGapM
                });
                if (!double.IsPositiveInfinity(edge.MaximumGapM))
                {
                    constraints.Add(new DifferenceEdge
                    {
                        From = edge.Left,
                        To = edge.Right,
                        Weight = leftWidth + edge.MaximumGapM
                    });
                }
            }

            if (!TryShortestPotentials(lanes.Count + 1, root, constraints, out var maximumPotential))
            {
                failureReason = "конфликт ограничений положения; цепочка: " + chainFailure;
                return false;
            }
            var reversed = constraints.Select(edge => new DifferenceEdge
            {
                From = edge.To,
                To = edge.From,
                Weight = edge.Weight
            }).ToList();
            if (!TryShortestPotentials(lanes.Count + 1, root, reversed, out var negativeMinimumPotential))
            {
                failureReason = "конфликт обратных ограничений положения";
                return false;
            }

            var rootMaximum = maximumPotential[root];
            var rootNegativeMinimum = negativeMinimumPotential[root];
            var numerator = 0.0;
            var denominator = 0.0;
            var feasibleMinimum = new double[lanes.Count];
            var feasibleMaximum = new double[lanes.Count];
            for (var i = 0; i < lanes.Count; i++)
            {
                feasibleMaximum[i] = maximumPotential[i] - rootMaximum;
                feasibleMinimum[i] = -(negativeMinimumPotential[i] - rootNegativeMinimum);
                var range = feasibleMaximum[i] - feasibleMinimum[i];
                numerator += (lanes[i].PreferredStart - feasibleMinimum[i]) * range;
                denominator += range * range;
            }

            // A convex blend of two feasible layouts stays feasible; independently
            // averaging each lane's extrema can create new overlaps in branched groups.
            var blend = denominator <= GeometryToleranceM * GeometryToleranceM
                ? 0.5
                : Clamp(numerator / denominator, 0, 1);
            for (var i = 0; i < lanes.Count; i++)
                starts[i] = feasibleMinimum[i] + blend * (feasibleMaximum[i] - feasibleMinimum[i]);

            if (IsStartsArrangementValid(lanes, laneEdges, lowerBounds, upperBounds, starts)) return true;
            failureReason = "решение потенциалов не прошло итоговую проверку";
            return false;
        }

        private static void TryArrangeComponentByPriority(
            IList<Lane> lanes, IList<LaneEdge> laneEdges, out double[] starts)
        {
            starts = lanes.Select(lane => lane.CrossMin).ToArray();
            var assigned = new bool[lanes.Count];
            var lowerBounds = new double[lanes.Count];
            var upperBounds = new double[lanes.Count];
            for (var i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                var width = lane.CrossMax - lane.CrossMin;
                lowerBounds[i] = lane.AllowedCrossMin;
                upperBounds[i] = lane.AllowedCrossMax - width;
                if (lane.CoverageCrossMin <= lane.CoverageCrossMax)
                {
                    lowerBounds[i] = Math.Max(lowerBounds[i], lane.CoverageCrossMax - width);
                    upperBounds[i] = Math.Min(upperBounds[i], lane.CoverageCrossMin);
                }
            }

            var order = Enumerable.Range(0, lanes.Count)
                .OrderByDescending(index => lanes[index].ReinforcementAreaPriority)
                .ThenByDescending(index => lanes[index].Capacity)
                .ThenBy(index => lanes[index].SupportCrossCenter)
                .ToList();
            foreach (var index in order)
            {
                var lane = lanes[index];
                var width = lane.CrossMax - lane.CrossMin;
                var minimum = lowerBounds[index];
                var maximum = upperBounds[index];
                if (minimum > maximum + GeometryToleranceM) continue;

                foreach (var edge in laneEdges)
                {
                    if (edge.Left == index && assigned[edge.Right])
                    {
                        var otherWidth = lanes[index].CrossMax - lanes[index].CrossMin;
                        if (!double.IsPositiveInfinity(edge.MaximumGapM))
                            minimum = Math.Max(minimum,
                                starts[edge.Right] - otherWidth - edge.MaximumGapM);
                        maximum = Math.Min(maximum,
                            starts[edge.Right] - otherWidth - edge.MinimumGapM);
                    }
                    else if (edge.Right == index && assigned[edge.Left])
                    {
                        var leftWidth = lanes[edge.Left].CrossMax - lanes[edge.Left].CrossMin;
                        minimum = Math.Max(minimum,
                            starts[edge.Left] + leftWidth + edge.MinimumGapM);
                        if (!double.IsPositiveInfinity(edge.MaximumGapM))
                            maximum = Math.Min(maximum,
                                starts[edge.Left] + leftWidth + edge.MaximumGapM);
                    }
                }

                if (minimum > maximum + GeometryToleranceM) continue;
                starts[index] = Clamp(lane.PreferredStart, minimum, maximum);
                assigned[index] = true;
            }
        }

        private static bool TryArrangeChain(
            IList<Lane> lanes, IList<LaneEdge> edges,
            IList<double> lowerBounds, IList<double> upperBounds, out double[] starts,
            out string failureReason)
        {
            starts = new double[lanes.Count];
            failureReason = string.Empty;
            var edgeByPair = edges.Where(edge => edge.Right == edge.Left + 1)
                .ToDictionary(edge => (edge.Left, edge.Right));
            for (var i = 0; i + 1 < lanes.Count; i++)
                if (!edgeByPair.ContainsKey((i, i + 1)))
                {
                    failureReason = $"нет ребра {i}->{i + 1}";
                    return false;
                }

            var feasibleMin = lowerBounds.ToArray();
            var feasibleMax = upperBounds.ToArray();
            for (var i = lanes.Count - 2; i >= 0; i--)
            {
                var edge = edgeByPair[(i, i + 1)];
                var width = lanes[i].CrossMax - lanes[i].CrossMin;
                feasibleMin[i] = Math.Max(feasibleMin[i], feasibleMin[i + 1] - width - edge.MaximumGapM);
                feasibleMax[i] = Math.Min(feasibleMax[i], feasibleMax[i + 1] - width - edge.MinimumGapM);
                if (feasibleMin[i] > feasibleMax[i] + GeometryToleranceM)
                {
                    failureReason = $"обратный проход, полоса {i}: " +
                        $"[{feasibleMin[i]:0.000000}..{feasibleMax[i]:0.000000}]";
                    return false;
                }
            }

            starts[0] = Clamp(lanes[0].PreferredStart, feasibleMin[0], feasibleMax[0]);
            for (var i = 0; i + 1 < lanes.Count; i++)
            {
                var edge = edgeByPair[(i, i + 1)];
                var width = lanes[i].CrossMax - lanes[i].CrossMin;
                var minimum = Math.Max(feasibleMin[i + 1], starts[i] + width + edge.MinimumGapM);
                var maximum = Math.Min(feasibleMax[i + 1], starts[i] + width + edge.MaximumGapM);
                if (minimum > maximum + GeometryToleranceM)
                {
                    failureReason = $"прямой проход {i}->{i + 1}: " +
                        $"[{minimum:0.000000}..{maximum:0.000000}], " +
                        $"ширины {width:0.000000}/{lanes[i + 1].CrossMax - lanes[i + 1].CrossMin:0.000000}, " +
                        $"допуск {edge.MinimumGapM:0.000000}..{edge.MaximumGapM:0.000000}";
                    return false;
                }
                starts[i + 1] = Clamp(lanes[i + 1].PreferredStart, minimum, maximum);
            }
            return true;
        }

        private static bool IsArrangementValid(
            IList<Lane> lanes, IList<LaneEdge> edges,
            IList<double> lowerBounds, IList<double> upperBounds)
        {
            var starts = lanes.Select(lane => lane.CrossMin).ToArray();
            return IsStartsArrangementValid(lanes, edges, lowerBounds, upperBounds, starts);
        }

        private static bool IsStartsArrangementValid(
            IList<Lane> lanes, IList<LaneEdge> edges,
            IList<double> lowerBounds, IList<double> upperBounds, IList<double> starts)
        {
            for (var i = 0; i < lanes.Count; i++)
                if (starts[i] < lowerBounds[i] - GeometryToleranceM ||
                    starts[i] > upperBounds[i] + GeometryToleranceM)
                    return false;
            foreach (var edge in edges)
            {
                var leftWidth = lanes[edge.Left].CrossMax - lanes[edge.Left].CrossMin;
                var gap = starts[edge.Right] - (starts[edge.Left] + leftWidth);
                if (gap < edge.MinimumGapM - GeometryToleranceM ||
                    gap > edge.MaximumGapM + GeometryToleranceM)
                    return false;
            }
            return true;
        }

        private static List<List<int>> LaneComponents(int laneCount, IList<LaneEdge> edges)
        {
            var neighbors = Enumerable.Range(0, laneCount).Select(_ => new List<int>()).ToList();
            foreach (var edge in edges)
            {
                neighbors[edge.Left].Add(edge.Right);
                neighbors[edge.Right].Add(edge.Left);
            }

            var seen = new bool[laneCount];
            var components = new List<List<int>>();
            for (var start = 0; start < laneCount; start++)
            {
                if (seen[start]) continue;
                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                seen[start] = true;
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    component.Add(current);
                    foreach (var next in neighbors[current])
                    {
                        if (seen[next]) continue;
                        seen[next] = true;
                        queue.Enqueue(next);
                    }
                }
                components.Add(component);
            }
            return components;
        }

        private static bool TryShortestPotentials(
            int vertexCount, int root, IList<DifferenceEdge> edges, out double[] potentials)
        {
            const double infinity = double.MaxValue / 4;
            potentials = Enumerable.Repeat(infinity, vertexCount).ToArray();
            potentials[root] = 0;
            for (var pass = 0; pass < vertexCount - 1; pass++)
            {
                var changed = false;
                foreach (var edge in edges)
                {
                    if (potentials[edge.From] >= infinity / 2) continue;
                    var candidate = potentials[edge.From] + edge.Weight;
                    if (candidate >= potentials[edge.To] - GeometryToleranceM) continue;
                    potentials[edge.To] = candidate;
                    changed = true;
                }
                if (!changed) break;
            }

            foreach (var edge in edges)
                if (potentials[edge.From] < infinity / 2 &&
                    potentials[edge.To] > potentials[edge.From] + edge.Weight + GeometryToleranceM)
                    return false;
            return true;
        }

        private static int CountUnsatisfiedEdges(
            IList<Lane> lanes, IList<LaneEdge> edges, IList<double> starts)
        {
            var count = 0;
            foreach (var edge in edges)
            {
                var leftWidth = lanes[edge.Left].CrossMax - lanes[edge.Left].CrossMin;
                var gap = starts[edge.Right] - (starts[edge.Left] + leftWidth);
                if (gap < edge.MinimumGapM - GeometryToleranceM ||
                    gap > edge.MaximumGapM + GeometryToleranceM) count++;
            }
            return count;
        }

        private static List<CoveredElement> CaptureCoveredElements(
            IList<LiraPlateElement> plates,
            IDictionary<RebarLayer, IList<AdditionalZone>> zonesByLayer,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            var covered = new List<CoveredElement>();
            foreach (RebarLayer layer in Enum.GetValues(typeof(RebarLayer)))
            {
                if (!LayerEnabled(layer, settings) || !zonesByLayer.TryGetValue(layer, out var layerZones)) continue;
                var background = BackgroundAs(layer, settings);
                foreach (var plate in plates)
                {
                    if (!plate.Rebar.Ok) continue;
                    var required = plate.Rebar.Get(layer) - background;
                    if (required <= MosaicBuilder.PositiveResidualToleranceCm2PerM ||
                        !ZoneCoverageRules.CoversOrBridgesGap(
                            layerZones, plate, layer, required, slabOutline, openings))
                        continue;

                    var candidates = layerZones.Where(zone =>
                        CapacityOf(zone) + CoverageTolerance >= required &&
                        (zone.NodeIds.Contains(plate.Id) || IntersectsFootprint(zone, plate))).ToList();
                    var directWitness = candidates
                        .Where(zone => ZoneCoverageRules.CoversOrBridgesGap(
                            new List<AdditionalZone> { zone }, plate, layer, required, slabOutline, openings))
                        .OrderBy(zone => CapacityOf(zone) - required)
                        .ThenByDescending(zone => CoverageMargin(zone, plate))
                        .FirstOrDefault();
                    var item = new CoveredElement
                    {
                        Plate = plate,
                        Layer = layer,
                        RequiredAs = required,
                        DirectWitness = directWitness,
                        CoverageCandidates = candidates
                    };
                    if (directWitness == null || candidates.Count > 1)
                        FindCrossGapWitness(item, candidates, slabOutline, openings);
                    covered.Add(item);
                }
            }
            return covered;
        }

        private static void FindCrossGapWitness(
            CoveredElement item,
            IList<AdditionalZone> candidates,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            var options = new List<(AdditionalZone First, AdditionalZone Second, double Gap, double Capacity)>(
                Math.Min(64, candidates.Count * candidates.Count / 2));
            for (var i = 0; i < candidates.Count; i++)
            for (var j = i + 1; j < candidates.Count; j++)
            {
                var first = candidates[i];
                var second = candidates[j];
                if (first.Direction != second.Direction) continue;
                var firstBounds = Bounds(first.Contour);
                var secondBounds = Bounds(second.Contour);
                var firstCross = Cross(firstBounds, first.Direction);
                var secondCross = Cross(secondBounds, second.Direction);
                var crossGap = firstCross.Max <= secondCross.Min
                    ? secondCross.Min - firstCross.Max
                    : secondCross.Max <= firstCross.Min
                        ? firstCross.Min - secondCross.Max
                        : 0;
                var firstAxial = Axial(firstBounds, first.Direction);
                var secondAxial = Axial(secondBounds, second.Direction);
                var axialOverlap = Math.Min(firstAxial.Max, secondAxial.Max) -
                                   Math.Max(firstAxial.Min, secondAxial.Min);
                var maxGap = Math.Min(first.BarStepMm, second.BarStepMm) / 1000.0;
                if (crossGap > maxGap + GeometryToleranceM || axialOverlap <= GeometryToleranceM)
                    continue;

                if (!ZoneCoverageRules.CoversOrBridgesGap(
                        new List<AdditionalZone> { first, second }, item.Plate, item.Layer,
                        item.RequiredAs, slabOutline, openings))
                    continue;
                options.Add((first, second, crossGap,
                    Math.Min(CapacityOf(first), CapacityOf(second))));
            }

            var witness = options.OrderBy(option => option.Gap)
                .ThenBy(option => option.Capacity - item.RequiredAs)
                .FirstOrDefault();
            if (witness.First == null || witness.Second == null) return;
            item.BridgeFirst = witness.First;
            item.BridgeSecond = witness.Second;
        }

        private static double CoverageMargin(AdditionalZone zone, LiraPlateElement plate)
        {
            if (plate.Contour == null || plate.Contour.Count < 3) return 0;
            var zoneCross = Cross(Bounds(zone.Contour), zone.Direction);
            var plateCross = Cross(Bounds(plate.Contour), zone.Direction);
            return Math.Min(plateCross.Min - zoneCross.Min, zoneCross.Max - plateCross.Max);
        }

        private static void ResolveContainedZoneOverlaps(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> supportBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            ZonePatchNeighborLayoutResult result)
        {
            var pairs = zones.Where(IsAdjustable)
                .SelectMany(inner => zones.Where(outer => !ReferenceEquals(inner, outer) &&
                    IsAdjustable(outer) && inner.Layer == outer.Layer &&
                    inner.Direction == outer.Direction &&
                    StrictlyContains(Bounds(outer.Contour), Bounds(inner.Contour)))
                    .Select(outer => (Inner: inner, Outer: outer,
                        InnerArea: RectangleArea(Bounds(inner.Contour)),
                        OuterArea: RectangleArea(Bounds(outer.Contour)))))
                .OrderBy(item => item.InnerArea)
                .ThenBy(item => item.OuterArea)
                .ToList();

            foreach (var pair in pairs)
            {
                if (!zones.Contains(pair.Inner) || !zones.Contains(pair.Outer)) continue;
                var affectedBounds = Bounds(pair.Outer.Contour);
                affectedBounds = UnionBounds(affectedBounds, Bounds(pair.Inner.Contour));
                if (supportBoundsByZone.TryGetValue(pair.Outer, out var outerSupport))
                    affectedBounds = UnionBounds(affectedBounds, outerSupport);
                if (supportBoundsByZone.TryGetValue(pair.Inner, out var innerSupport))
                    affectedBounds = UnionBounds(affectedBounds, innerSupport);
                var affectedPlates = plates.Where(plate => plate.Rebar.Ok &&
                    plate.Rebar.Get(pair.Inner.Layer) - BackgroundAs(pair.Inner.Layer, settings) >
                    MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                    IntersectsBounds(plate, affectedBounds)).ToList();
                var preservedCoverage = CaptureCoverageExpectations(
                    zones, affectedPlates, pair.Inner.Layer, settings, slabOutline, openings);
                if (TryAbsorbContainedZone(pair.Outer, pair.Inner, zones,
                        supportBoundsByZone, patchBoundsByZone, preservedCoverage,
                        settings, slabOutline, openings))
                {
                    result.AbsorbedContainedZones++;
                    continue;
                }

                if (TrySplitContainingZone(pair.Outer, pair.Inner, zones,
                        supportBoundsByZone, patchBoundsByZone, preservedCoverage,
                        plates, settings, slabOutline, openings))
                    result.SplitContainedZones++;
            }
        }

        private static List<(LiraPlateElement Plate, RebarLayer Layer, double RequiredAs)>
            CaptureCoverageExpectations(
                IList<AdditionalZone> zones, IList<LiraPlateElement> plates, RebarLayer layer,
                AnalysisSettings settings, IList<Point3>? slabOutline, IList<OpeningInfo>? openings)
        {
            var expected = new List<(LiraPlateElement, RebarLayer, double)>();
            if (!LayerEnabled(layer, settings)) return expected;
            var layerZones = zones.Where(zone => zone.Layer == layer).ToList();
            var background = BackgroundAs(layer, settings);
            foreach (var plate in plates)
            {
                var required = plate.Rebar.Get(layer) - background;
                if (required <= MosaicBuilder.PositiveResidualToleranceCm2PerM ||
                    !ZoneCoverageRules.CoversOrBridgesGap(
                        layerZones, plate, layer, required, slabOutline, openings))
                    continue;
                expected.Add((plate, layer, required));
            }
            return expected;
        }

        private static bool PreservesCoverageExpectations(
            IList<AdditionalZone> zones,
            IList<(LiraPlateElement Plate, RebarLayer Layer, double RequiredAs)> expected,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            var zonesByLayer = zones.GroupBy(zone => zone.Layer)
                .ToDictionary(group => group.Key, group => (IList<AdditionalZone>)group.ToList());
            return expected.All(item => zonesByLayer.TryGetValue(item.Layer, out var layerZones) &&
                ZoneCoverageRules.CoversOrBridgesGap(
                    layerZones, item.Plate, item.Layer, item.RequiredAs, slabOutline, openings));
        }

        private static bool TryAbsorbContainedZone(
            AdditionalZone container,
            AdditionalZone contained,
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> supportBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> patchBoundsByZone,
            IList<(LiraPlateElement Plate, RebarLayer Layer, double RequiredAs)> preservedCoverage,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            var requiredAs = Math.Max(container.AsAdditional, contained.AsAdditional);
            var promoted = CapacityOf(container) + CoverageTolerance >= requiredAs
                ? container
                : CreatePromotedContainer(container, contained, requiredAs,
                    supportBoundsByZone, patchBoundsByZone, settings);
            if (promoted == null) return false;

            promoted.AsAdditional = requiredAs;
            promoted.AsRequired = Math.Max(container.AsRequired, contained.AsRequired);
            promoted.NodeIds = container.NodeIds.Concat(contained.NodeIds).Distinct().ToList();
            promoted.ElementId = promoted.NodeIds.FirstOrDefault();
            promoted.Rebar = CloneReinforcement(container.Rebar);
            SetLayerAs(promoted.Rebar, promoted.Layer,
                requiredAs + BackgroundAs(promoted.Layer, settings));
            var absorptionNote = "вложенная зона поглощена";
            promoted.Comment = string.IsNullOrWhiteSpace(promoted.Comment)
                ? absorptionNote
                : promoted.Comment + "; " + absorptionNote;

            var proposed = zones.Where(zone => !ReferenceEquals(zone, contained))
                .Select(zone => ReferenceEquals(zone, container) ? promoted : zone).ToList();
            if (!PreservesCoverageExpectations(
                    proposed, preservedCoverage, slabOutline, openings)) return false;

            var containerIndex = zones.IndexOf(container);
            zones[containerIndex] = promoted;
            zones.Remove(contained);
            ReplaceBoundsKey(supportBoundsByZone, container, promoted);
            ReplaceBoundsKey(patchBoundsByZone, container, promoted);
            supportBoundsByZone.Remove(contained);
            patchBoundsByZone.Remove(contained);
            return true;
        }

        private static AdditionalZone? CreatePromotedContainer(
            AdditionalZone container,
            AdditionalZone contained,
            double requiredAs,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> supportBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> patchBoundsByZone,
            AnalysisSettings settings)
        {
            var support = supportBoundsByZone.TryGetValue(container, out var foundSupport)
                ? foundSupport
                : Bounds(container.Contour);
            var patch = patchBoundsByZone.TryGetValue(container, out var foundPatch)
                ? foundPatch
                : support;
            var direction = container.Direction;
            var oldCross = Cross(Bounds(container.Contour), direction);
            var supportCross = Cross(support, direction);
            var containedCross = Cross(Bounds(contained.Contour), direction);
            var requiredCross = (Min: Math.Min(oldCross.Min, Math.Min(supportCross.Min, containedCross.Min)),
                Max: Math.Max(oldCross.Max, Math.Max(supportCross.Max, containedCross.Max)));
            var patchCross = Cross(patch, direction);
            var allowedOverrun = Math.Max(1, settings.GridCellMm) / 1000.0;
            var allowed = (Min: patchCross.Min - allowedOverrun,
                Max: patchCross.Max + allowedOverrun);
            var minDiameter = BackgroundDiameter(settings, container.Layer);
            if (minDiameter <= 0) minDiameter = 8;
            var excluded = settings.ExcludedZoneDiametersMm?.ToArray();
            var steps = (settings.AllowedAdditionalBarStepsMm ?? new List<int>())
                .Where(step => step == 100 || step == 200)
                .Concat(new[] { container.BarStepMm, contained.BarStepMm })
                .Where(step => step == 100 || step == 200)
                .Distinct()
                .ToList();
            if (steps.Count == 0) steps.Add(200);
            var option = steps.Select(step =>
                {
                    var diameter = BarCapacity.MinDiameterForAs(requiredAs, step,
                        settings.MaxDiameterMm > 0 ? settings.MaxDiameterMm : 36,
                        minDiameter, excluded);
                    return new
                    {
                        Diameter = diameter,
                        Step = step,
                        Capacity = diameter > 0 ? BarCapacity.AsCm2PerM(diameter, step) : 0
                    };
                })
                .Where(item => item.Diameter > 0 && item.Capacity + CoverageTolerance >= requiredAs)
                .OrderBy(item => item.Capacity)
                .ThenByDescending(item => item.Step)
                .ThenBy(item => item.Diameter)
                .FirstOrDefault();
            if (option == null) return null;

            var anchorageMm = RebarTables.AnchorageLenMm(settings.ConcreteClass, option.Diameter);
            var supportAxial = Axial(support, direction);
            var requiredLengthMm = UnitConversion.MetersToMm(supportAxial.Max - supportAxial.Min) +
                                   2.0 * anchorageMm;
            if (container.LengthMm + 1e-6 < requiredLengthMm ||
                container.LengthMm > 11700 + 1e-6) return null;

            var stepM = option.Step / 1000.0;
            var widthM = RoundUpToStep(
                Math.Max(requiredCross.Max - requiredCross.Min, settings.EffectiveMinZoneWidthM), stepM);
            var minStart = Math.Max(allowed.Min, requiredCross.Max - widthM);
            var maxStart = Math.Min(requiredCross.Min, allowed.Max - widthM);
            if (minStart > maxStart + GeometryToleranceM) return null;
            var start = Clamp(oldCross.Min, minStart, maxStart);
            var promoted = CloneZone(container);
            SetZoneCrossInterval(promoted, (start, start + widthM));
            promoted.DiameterMm = option.Diameter;
            promoted.BarStepMm = option.Step;
            promoted.AsCoveredCm2PerM = option.Capacity;
            promoted.BarCount = Math.Max(1,
                (int)Math.Floor(promoted.WidthMm / option.Step + 1e-9) + 1);
            return promoted;
        }

        private static bool TrySplitContainingZone(
            AdditionalZone container,
            AdditionalZone contained,
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> supportBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> patchBoundsByZone,
            IList<(LiraPlateElement Plate, RebarLayer Layer, double RequiredAs)> preservedCoverage,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            var containerBounds = Bounds(container.Contour);
            var containedBounds = Bounds(contained.Contour);
            var containerAxial = Axial(containerBounds, container.Direction);
            var containedAxial = Axial(containedBounds, contained.Direction);
            if (Math.Abs(containerAxial.Min - containedAxial.Min) > GeometryToleranceM ||
                Math.Abs(containerAxial.Max - containedAxial.Max) > GeometryToleranceM ||
                container.BarStepMm <= 0 || contained.BarStepMm <= 0)
                return false;

            var containerCross = Cross(containerBounds, container.Direction);
            var containedCross = Cross(containedBounds, container.Direction);
            var gapM = Math.Min(container.BarStepMm, contained.BarStepMm) / 1000.0;
            var stepM = container.BarStepMm / 1000.0;
            var minimumWidthM = RoundUpToStep(
                Math.Max(settings.EffectiveMinZoneWidthM, stepM), stepM);
            var possible = new List<(AdditionalZone Zone, ZonePatchFrameBounds Support,
                ZonePatchFrameBounds Patch)>();

            AddSide(containerCross.Min, containedCross.Min - gapM, true);
            AddSide(containedCross.Max + gapM, containerCross.Max, false);
            if (possible.Count == 0) return false;

            var proposed = zones.Where(zone => !ReferenceEquals(zone, container)).ToList();
            proposed.AddRange(possible.Select(item => item.Zone));
            if (!PreservesCoverageExpectations(
                    proposed, preservedCoverage, slabOutline, openings)) return false;

            var originalIndex = zones.IndexOf(container);
            zones.RemoveAt(originalIndex);
            foreach (var item in possible)
            {
                zones.Insert(originalIndex++, item.Zone);
                supportBoundsByZone[item.Zone] = item.Support;
                patchBoundsByZone[item.Zone] = item.Patch;
            }
            supportBoundsByZone.Remove(container);
            patchBoundsByZone.Remove(container);
            return true;

            void AddSide(double availableMin, double availableMax, bool keepLowEdge)
            {
                var sideWidth = RoundDownToStep(availableMax - availableMin, stepM);
                if (sideWidth < minimumWidthM - GeometryToleranceM) return;
                var interval = keepLowEdge
                    ? (Min: availableMin, Max: availableMin + sideWidth)
                    : (Min: availableMax - sideWidth, Max: availableMax);
                var source = supportBoundsByZone.TryGetValue(container, out var foundSupport)
                    ? foundSupport
                    : containerBounds;
                var sideSupport = ClipToCross(source, container.Direction, interval);
                if (sideSupport == null) return;
                var originalPatch = patchBoundsByZone.TryGetValue(container, out var foundPatch)
                    ? foundPatch
                    : source;
                var sidePatch = ClipToCross(originalPatch, container.Direction, interval) ?? sideSupport.Value;
                var piece = CloneZone(container);
                SetZoneCrossInterval(piece, interval);
                piece.ZoneId = zones.Max(zone => zone.ZoneId) + possible.Count + 1;
                piece.NodeIds = plates.Where(plate => plate.Rebar.Ok &&
                        plate.Rebar.Get(container.Layer) > BackgroundAs(container.Layer, settings) +
                        MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                        PointInsideBounds(plate.Centroid, sideSupport.Value))
                    .Select(plate => plate.Id).Distinct().ToList();
                piece.ElementId = piece.NodeIds.FirstOrDefault();
                piece.Comment = string.IsNullOrWhiteSpace(piece.Comment)
                    ? "зона разделена вокруг более мощной вложенной зоны"
                    : piece.Comment + "; разделена вокруг вложенной зоны";
                possible.Add((piece, sideSupport.Value, sidePatch));
            }
        }

        private static bool StrictlyContains(ZonePatchFrameBounds outer, ZonePatchFrameBounds inner) =>
            outer.MinX <= inner.MinX + GeometryToleranceM &&
            outer.MaxX >= inner.MaxX - GeometryToleranceM &&
            outer.MinY <= inner.MinY + GeometryToleranceM &&
            outer.MaxY >= inner.MaxY - GeometryToleranceM &&
            (outer.MinX < inner.MinX - GeometryToleranceM ||
             outer.MaxX > inner.MaxX + GeometryToleranceM ||
             outer.MinY < inner.MinY - GeometryToleranceM ||
             outer.MaxY > inner.MaxY + GeometryToleranceM);

        private static double RectangleArea(ZonePatchFrameBounds bounds) =>
            Math.Max(0, bounds.MaxX - bounds.MinX) * Math.Max(0, bounds.MaxY - bounds.MinY);

        private static ZonePatchFrameBounds UnionBounds(
            ZonePatchFrameBounds first, ZonePatchFrameBounds second) =>
            new ZonePatchFrameBounds(
                Math.Min(first.MinX, second.MinX), Math.Max(first.MaxX, second.MaxX),
                Math.Min(first.MinY, second.MinY), Math.Max(first.MaxY, second.MaxY));

        private static ZonePatchFrameBounds? ClipToCross(
            ZonePatchFrameBounds bounds, ZoneDirection direction, (double Min, double Max) cross)
        {
            if (direction == ZoneDirection.X)
            {
                var minY = Math.Max(bounds.MinY, cross.Min);
                var maxY = Math.Min(bounds.MaxY, cross.Max);
                return maxY - minY > GeometryToleranceM
                    ? new ZonePatchFrameBounds(bounds.MinX, bounds.MaxX, minY, maxY)
                    : null;
            }

            var minX = Math.Max(bounds.MinX, cross.Min);
            var maxX = Math.Min(bounds.MaxX, cross.Max);
            return maxX - minX > GeometryToleranceM
                ? new ZonePatchFrameBounds(minX, maxX, bounds.MinY, bounds.MaxY)
                : null;
        }

        private static bool PointInsideBounds(Point3 point, ZonePatchFrameBounds bounds) =>
            point.X >= bounds.MinX - GeometryToleranceM && point.X <= bounds.MaxX + GeometryToleranceM &&
            point.Y >= bounds.MinY - GeometryToleranceM && point.Y <= bounds.MaxY + GeometryToleranceM;

        private static bool IntersectsBounds(LiraPlateElement plate, ZonePatchFrameBounds bounds)
        {
            if (plate.Contour == null || plate.Contour.Count < 3)
                return PointInsideBounds(plate.Centroid, bounds);
            var plateBounds = Bounds(plate.Contour);
            return plateBounds.MaxX >= bounds.MinX - GeometryToleranceM &&
                   plateBounds.MinX <= bounds.MaxX + GeometryToleranceM &&
                   plateBounds.MaxY >= bounds.MinY - GeometryToleranceM &&
                   plateBounds.MinY <= bounds.MaxY + GeometryToleranceM;
        }

        private static AdditionalZone CloneZone(AdditionalZone source) => new AdditionalZone
        {
            ZoneId = source.ZoneId,
            ElementId = source.ElementId,
            Layer = source.Layer,
            NodeIds = source.NodeIds.ToList(),
            Placement = source.Placement,
            Contour = source.Contour.ToList(),
            WidthM = source.WidthM,
            LengthM = source.LengthM,
            LevelZM = source.LevelZM,
            AsRequired = source.AsRequired,
            AsAdditional = source.AsAdditional,
            Rebar = CloneReinforcement(source.Rebar),
            Comment = source.Comment,
            IsValid = source.IsValid,
            StatusColor = source.StatusColor,
            Direction = source.Direction,
            DiameterMm = source.DiameterMm,
            BarStepMm = source.BarStepMm,
            BarCount = source.BarCount,
            WidthMm = source.WidthMm,
            LengthMm = source.LengthMm,
            FamilyKind = source.FamilyKind,
            FamilyFileName = source.FamilyFileName,
            AsCoveredCm2PerM = source.AsCoveredCm2PerM,
            ConcreteClass = source.ConcreteClass,
            AlphaCoef = source.AlphaCoef,
            RotationDeg = source.RotationDeg,
            RnfSection = source.RnfSection,
            RnfMarkConstruction = source.RnfMarkConstruction,
            RnfMarkAssembly = source.RnfMarkAssembly,
            RnfMarkElement = source.RnfMarkElement,
            CountInSpec = source.CountInSpec,
            CountBars = source.CountBars,
            VerticalLegMm = source.VerticalLegMm,
            AxisNameX = source.AxisNameX,
            AxisPosXM = source.AxisPosXM,
            OffsetFromAxisXMm = source.OffsetFromAxisXMm,
            AxisNameY = source.AxisNameY,
            AxisPosYM = source.AxisPosYM,
            OffsetFromAxisYMm = source.OffsetFromAxisYMm,
            AxisTieLabel = source.AxisTieLabel
        };

        private static PlateReinforcement CloneReinforcement(PlateReinforcement source) => new PlateReinforcement
        {
            As1 = source.As1,
            As2 = source.As2,
            As3 = source.As3,
            As4 = source.As4,
            Ok = source.Ok
        };

        private static void SetLayerAs(PlateReinforcement rebar, RebarLayer layer, double value)
        {
            switch (layer)
            {
                case RebarLayer.As1: rebar.As1 = value; break;
                case RebarLayer.As2: rebar.As2 = value; break;
                case RebarLayer.As3: rebar.As3 = value; break;
                case RebarLayer.As4: rebar.As4 = value; break;
            }
        }

        private static void SetZoneCrossInterval(
            AdditionalZone zone, (double Min, double Max) cross)
        {
            var bounds = Bounds(zone.Contour);
            var minX = zone.Direction == ZoneDirection.X ? bounds.MinX : cross.Min;
            var maxX = zone.Direction == ZoneDirection.X ? bounds.MaxX : cross.Max;
            var minY = zone.Direction == ZoneDirection.X ? cross.Min : bounds.MinY;
            var maxY = zone.Direction == ZoneDirection.X ? cross.Max : bounds.MaxY;
            zone.Contour = new List<Point3>
            {
                new Point3(minX, minY, zone.LevelZM), new Point3(maxX, minY, zone.LevelZM),
                new Point3(maxX, maxY, zone.LevelZM), new Point3(minX, maxY, zone.LevelZM)
            };
            zone.Placement = new Point3((minX + maxX) * 0.5, (minY + maxY) * 0.5, zone.LevelZM);
            zone.WidthM = cross.Max - cross.Min;
            zone.WidthMm = UnitConversion.MetersToMm(zone.WidthM);
            zone.BarCount = Math.Max(1,
                (int)Math.Floor(zone.WidthMm / Math.Max(1, zone.BarStepMm) + 1e-9) + 1);
        }

        private static int BackgroundDiameter(AnalysisSettings settings, RebarLayer layer) => layer switch
        {
            RebarLayer.As1 or RebarLayer.As2 => settings.BgBottomDiameterMm,
            RebarLayer.As3 or RebarLayer.As4 => settings.BgTopDiameterMm,
            _ => 0
        };

        private static void ReplaceBoundsKey(
            IDictionary<AdditionalZone, ZonePatchFrameBounds> boundsByZone,
            AdditionalZone oldZone, AdditionalZone newZone)
        {
            if (!boundsByZone.TryGetValue(oldZone, out var bounds)) return;
            boundsByZone.Remove(oldZone);
            boundsByZone[newZone] = bounds;
        }

        private static void ResolveResidualGeometryConflicts(
            IList<Entry> entries,
            IList<CoveredElement> coveredElements,
            IDictionary<RebarLayer, IList<AdditionalZone>> zonesByLayer,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            ZonePatchNeighborLayoutResult result)
        {
            var entryIndex = entries.Select((entry, index) => (entry.Zone, Index: index))
                .ToDictionary(item => item.Zone, item => item.Index);
            var initialIntersections = FindIntersectionPairs(entries);
            var initialGaps = FindGapSpacingConflicts(entries);
            var work = initialIntersections.Select(pair => (Pair: pair, IsGap: false))
                .Concat(initialGaps.Select(pair => (Pair: pair, IsGap: true)))
                .OrderBy(item => Math.Min(
                    ReinforcementAreaPriority(entries[item.Pair.First]),
                    ReinforcementAreaPriority(entries[item.Pair.Second])))
                .ToList();

            foreach (var item in work)
            {
                var pair = item.Pair;
                var intersections = FindIntersectionPairs(entries);
                var gaps = FindGapSpacingConflicts(entries);
                if (!intersections.Contains(pair) && !gaps.Contains(pair)) continue;

                var first = entries[pair.First];
                var second = entries[pair.Second];
                var rejectedAllowance = 0;
                var rejectedNewConflicts = 0;
                var rejectedCoverage = 0;
                var movingOptions = new[] { first, second }
                    .OrderBy(ReinforcementAreaPriority)
                    .ThenBy(entry => entry.Zone.ZoneId)
                    .ToList();
                foreach (var moving in movingOptions)
                {
                    var other = ReferenceEquals(moving, first) ? second : first;
                    var isIntersection = intersections.Contains(pair);
                    var isGap = gaps.Contains(pair);
                    var currentConflicts = new HashSet<(int First, int Second)>(intersections);
                    currentConflicts.UnionWith(gaps);
                    foreach (var shift in ConflictResolvingShifts(moving, other, isIntersection, isGap)
                                 .OrderBy(shift => Math.Abs(shift.Dx) + Math.Abs(shift.Dy)))
                    {
                        if (!TryTranslateAndReflow(entries, moving, pair,
                                shift.Dx, shift.Dy, currentConflicts, coveredElements,
                                zonesByLayer, slabOutline, openings, out var movedCount))
                        {
                            if (!TryTranslateSingle(entries, moving, pair,
                                    shift.Dx, shift.Dy, currentConflicts, coveredElements,
                                    zonesByLayer, slabOutline, openings))
                            {
                                rejectedNewConflicts++;
                                continue;
                            }
                            movedCount = 1;
                        }

                        result.ShiftedZones += movedCount;
                        break;
                    }
                    if (!intersections.Contains(pair) && !gaps.Contains(pair)) break;
                    intersections = FindIntersectionPairs(entries);
                    gaps = FindGapSpacingConflicts(entries);
                }
                if (result.ResidualDetails.Count < 30 &&
                    (FindIntersectionPairs(entries).Contains(pair) || FindGapSpacingConflicts(entries).Contains(pair)))
                    result.ResidualDetails.Add(
                        $"Зоны {first.Zone.ZoneId}/{second.Zone.ZoneId}: " +
                        $"выход за пятно={rejectedAllowance}, новые конфликты={rejectedNewConflicts}, " +
                        $"потеря покрытия={rejectedCoverage}; " +
                        $"ширины {first.Zone.WidthMm:0}/{second.Zone.WidthMm:0} мм, " +
                        $"шаги {first.StepMm}/{second.StepMm} мм");
            }

            UpdateResidualConflictCounts(entries, result);
        }

        private static IEnumerable<(double Dx, double Dy)> ConflictResolvingShifts(
            Entry moving, Entry other, bool intersects, bool hasSpacingConflict)
        {
            var movingBounds = Bounds(moving.Zone.Contour);
            var otherBounds = Bounds(other.Zone.Contour);
            if (intersects)
            {
                var intersectionMovingCross = Cross(movingBounds, moving.Zone.Direction);
                var intersectionOtherCross = Cross(otherBounds, moving.Zone.Direction);
                var supportA = Cross(moving.Support, moving.Zone.Direction);
                var supportB = Cross(other.Support, moving.Zone.Direction);
                var intersectionRequiredGap = Math.Min(moving.StepMm, other.StepMm) / 1000.0;
                var supportGap = Math.Max(0, Math.Max(supportA.Min, supportB.Min) -
                                             Math.Min(supportA.Max, supportB.Max));
                if (supportGap <= intersectionRequiredGap + GeometryToleranceM)
                {
                    var beforeDelta = intersectionOtherCross.Min - intersectionRequiredGap - intersectionMovingCross.Max;
                    var afterDelta = intersectionOtherCross.Max + intersectionRequiredGap - intersectionMovingCross.Min;
                    yield return moving.Zone.Direction == ZoneDirection.X
                        ? (0, beforeDelta) : (beforeDelta, 0);
                    yield return moving.Zone.Direction == ZoneDirection.X
                        ? (0, afterDelta) : (afterDelta, 0);
                    var movingAxial = Axial(movingBounds, moving.Zone.Direction);
                    var otherAxial = Axial(otherBounds, moving.Zone.Direction);
                    var beforeAxialDelta = otherAxial.Min - movingAxial.Max - GeometryToleranceM;
                    var afterAxialDelta = otherAxial.Max - movingAxial.Min + GeometryToleranceM;
                    yield return moving.Zone.Direction == ZoneDirection.X
                        ? (beforeAxialDelta, 0) : (0, beforeAxialDelta);
                    yield return moving.Zone.Direction == ZoneDirection.X
                        ? (afterAxialDelta, 0) : (0, afterAxialDelta);
                    yield break;
                }
                yield return (otherBounds.MinX - movingBounds.MaxX - GeometryToleranceM, 0);
                yield return (otherBounds.MaxX - movingBounds.MinX + GeometryToleranceM, 0);
                yield return (0, otherBounds.MinY - movingBounds.MaxY - GeometryToleranceM);
                yield return (0, otherBounds.MaxY - movingBounds.MinY + GeometryToleranceM);
                yield break;
            }

            if (!hasSpacingConflict) yield break;
            var movingCross = Cross(movingBounds, moving.Zone.Direction);
            var otherCross = Cross(otherBounds, moving.Zone.Direction);
            var movingAxialForGap = Axial(movingBounds, moving.Zone.Direction);
            var otherAxialForGap = Axial(otherBounds, moving.Zone.Direction);
            var requiredGap = Math.Min(moving.StepMm, other.StepMm) / 1000.0;
            if (movingCross.Max <= otherCross.Min)
            {
                var delta = otherCross.Min - requiredGap - movingCross.Max;
                yield return moving.Zone.Direction == ZoneDirection.X ? (0, delta) : (delta, 0);
            }
            else if (otherCross.Max <= movingCross.Min)
            {
                var delta = otherCross.Max + requiredGap - movingCross.Min;
                yield return moving.Zone.Direction == ZoneDirection.X ? (0, delta) : (delta, 0);
            }

            if (Math.Min(movingAxialForGap.Max, otherAxialForGap.Max) -
                Math.Max(movingAxialForGap.Min, otherAxialForGap.Min) > GeometryToleranceM)
            {
                var beforeAxialDelta = otherAxialForGap.Min - movingAxialForGap.Max - GeometryToleranceM;
                var afterAxialDelta = otherAxialForGap.Max - movingAxialForGap.Min + GeometryToleranceM;
                yield return moving.Zone.Direction == ZoneDirection.X
                    ? (beforeAxialDelta, 0) : (0, beforeAxialDelta);
                yield return moving.Zone.Direction == ZoneDirection.X
                    ? (afterAxialDelta, 0) : (0, afterAxialDelta);
            }
        }

        private static bool IsWithinOneElementAllowance(Entry entry)
        {
            var bounds = Bounds(entry.Zone.Contour);
            var cross = Cross(bounds, entry.Zone.Direction);
            var axial = Axial(bounds, entry.Zone.Direction);
            return cross.Min >= entry.AllowedCrossMin - GeometryToleranceM &&
                   cross.Max <= entry.AllowedCrossMax + GeometryToleranceM &&
                   axial.Min >= entry.AllowedAxialStartMin - GeometryToleranceM &&
                   axial.Min <= entry.AllowedAxialStartMax + GeometryToleranceM;
        }

        private static bool TryTranslateAndReflow(
            IList<Entry> entries,
            Entry moving,
            (int First, int Second) targetPair,
            double dx,
            double dy,
            ISet<(int First, int Second)> existingConflicts,
            IList<CoveredElement> coveredElements,
            IDictionary<RebarLayer, IList<AdditionalZone>> zonesByLayer,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            out int movedCount)
        {
            movedCount = 0;
            var states = entries.ToDictionary(entry => entry, entry => new
            {
                Contour = entry.Zone.Contour,
                entry.Zone.Placement,
                entry.CrossMin,
                entry.CrossMax,
                entry.AxialMin,
                entry.AxialMax
            });
            var moved = new HashSet<Entry> { moving };
            var shiftX = Math.Abs(dx) > GeometryToleranceM;
            var direction = shiftX ? Math.Sign(dx) : Math.Sign(dy);
            if (direction == 0) return false;

            TranslateZone(moving.Zone, dx, dy);
            UpdateEntryFromZone(moving);
            var maximumPushes = Math.Max(entries.Count * 2, 1);
            var complete = IsWithinOneElementAllowance(moving);

            for (var push = 0; complete && push < maximumPushes; push++)
            {
                if (!TryFindMovedIntersection(entries, moved, out var collision)) break;
                if (collision.First == targetPair.First && collision.Second == targetPair.Second)
                {
                    complete = false;
                    break;
                }

                var first = entries[collision.First];
                var second = entries[collision.Second];
                var firstBounds = Bounds(first.Zone.Contour);
                var secondBounds = Bounds(second.Zone.Contour);
                var firstCenter = shiftX
                    ? (firstBounds.MinX + firstBounds.MaxX) * 0.5
                    : (firstBounds.MinY + firstBounds.MaxY) * 0.5;
                var secondCenter = shiftX
                    ? (secondBounds.MinX + secondBounds.MaxX) * 0.5
                    : (secondBounds.MinY + secondBounds.MaxY) * 0.5;
                var leading = direction > 0
                    ? (firstCenter >= secondCenter ? first : second)
                    : (firstCenter <= secondCenter ? first : second);
                var trailing = ReferenceEquals(leading, first) ? second : first;
                var leadingBounds = ReferenceEquals(leading, first) ? firstBounds : secondBounds;
                var trailingBounds = ReferenceEquals(trailing, first) ? firstBounds : secondBounds;
                var leadingMin = shiftX ? leadingBounds.MinX : leadingBounds.MinY;
                var leadingMax = shiftX ? leadingBounds.MaxX : leadingBounds.MaxY;
                var trailingMin = shiftX ? trailingBounds.MinX : trailingBounds.MinY;
                var trailingMax = shiftX ? trailingBounds.MaxX : trailingBounds.MaxY;
                var delta = direction > 0
                    ? trailingMax - leadingMin + GeometryToleranceM
                    : trailingMin - leadingMax - GeometryToleranceM;
                if (Math.Abs(delta) <= GeometryToleranceM)
                {
                    complete = false;
                    break;
                }

                TranslateZone(leading.Zone, shiftX ? delta : 0, shiftX ? 0 : delta);
                UpdateEntryFromZone(leading);
                moved.Add(leading);
                complete = IsWithinOneElementAllowance(leading);
            }

            if (complete && TryFindMovedIntersection(entries, moved, out _)) complete = false;
            if (complete && (FindIntersectionPairs(entries).Contains(targetPair) ||
                             FindGapSpacingConflicts(entries).Contains(targetPair) ||
                             HasNewConflicts(entries, existingConflicts)))
                complete = false;
            if (complete)
                foreach (var entry in moved)
                    if (!PreservesCapturedCoverage(entry.Zone, coveredElements,
                            zonesByLayer, slabOutline, openings))
                    {
                        complete = false;
                        break;
                    }

            if (!complete)
            {
                foreach (var state in states)
                {
                    state.Key.Zone.Contour = state.Value.Contour;
                    state.Key.Zone.Placement = state.Value.Placement;
                    state.Key.CrossMin = state.Value.CrossMin;
                    state.Key.CrossMax = state.Value.CrossMax;
                    state.Key.AxialMin = state.Value.AxialMin;
                    state.Key.AxialMax = state.Value.AxialMax;
                }
                return false;
            }

            movedCount = moved.Count;
            return true;
        }

        private static bool TryTranslateSingle(
            IList<Entry> entries,
            Entry moving,
            (int First, int Second) targetPair,
            double dx,
            double dy,
            ISet<(int First, int Second)> existingConflicts,
            IList<CoveredElement> coveredElements,
            IDictionary<RebarLayer, IList<AdditionalZone>> zonesByLayer,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            var originalContour = moving.Zone.Contour;
            var originalPlacement = moving.Zone.Placement;
            TranslateZone(moving.Zone, dx, dy);
            var allowed = IsWithinOneElementAllowance(moving);
            var newConflict = allowed && HasNewConflicts(entries, existingConflicts);
            var pairStillConflicts = allowed && !newConflict &&
                (FindIntersectionPairs(entries).Contains(targetPair) ||
                 FindGapSpacingConflicts(entries).Contains(targetPair));
            var coveragePreserved = allowed && !newConflict && !pairStillConflicts &&
                PreservesCapturedCoverage(moving.Zone, coveredElements,
                    zonesByLayer, slabOutline, openings);
            if (!allowed || newConflict || pairStillConflicts || !coveragePreserved)
            {
                moving.Zone.Contour = originalContour;
                moving.Zone.Placement = originalPlacement;
                return false;
            }

            UpdateEntryFromZone(moving);
            return true;
        }

        private static bool TryFindMovedIntersection(
            IList<Entry> entries,
            ISet<Entry> moved,
            out (int First, int Second) collision)
        {
            for (var first = 0; first < entries.Count; first++)
            for (var second = first + 1; second < entries.Count; second++)
            {
                var a = entries[first];
                var b = entries[second];
                if ((!moved.Contains(a) && !moved.Contains(b)) ||
                    a.Zone.Layer != b.Zone.Layer || a.Zone.Direction != b.Zone.Direction)
                    continue;
                var firstBounds = Bounds(a.Zone.Contour);
                var secondBounds = Bounds(b.Zone.Contour);
                if (Math.Abs(Cross(firstBounds, a.Zone.Direction).Min -
                             Cross(secondBounds, b.Zone.Direction).Min) <= GeometryToleranceM &&
                    Math.Abs(Cross(firstBounds, a.Zone.Direction).Max -
                             Cross(secondBounds, b.Zone.Direction).Max) <= GeometryToleranceM)
                    continue;
                if (Math.Min(firstBounds.MaxX, secondBounds.MaxX) -
                        Math.Max(firstBounds.MinX, secondBounds.MinX) > GeometryToleranceM &&
                    Math.Min(firstBounds.MaxY, secondBounds.MaxY) -
                        Math.Max(firstBounds.MinY, secondBounds.MinY) > GeometryToleranceM)
                {
                    collision = (first, second);
                    return true;
                }
            }

            collision = default;
            return false;
        }

        private static void UpdateEntryFromZone(Entry entry)
        {
            var bounds = Bounds(entry.Zone.Contour);
            var cross = Cross(bounds, entry.Zone.Direction);
            var axial = Axial(bounds, entry.Zone.Direction);
            entry.CrossMin = cross.Min;
            entry.CrossMax = cross.Max;
            entry.AxialMin = axial.Min;
            entry.AxialMax = axial.Max;
        }

        private static void ApplyLaneStarts(
            IList<Lane> lanes, IList<double> starts, ZonePatchNeighborLayoutResult result)
        {
            for (var i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                var width = lane.CrossMax - lane.CrossMin;
                lane.CrossMin = starts[i];
                lane.CrossMax = starts[i] + width;
                foreach (var entry in lane.Entries)
                {
                    if (Math.Abs(entry.CrossMin - lane.CrossMin) > GeometryToleranceM)
                        result.ShiftedZones++;
                    var support = Cross(entry.Support, entry.Zone.Direction);
                    if (lane.CrossMin < support.Min - GeometryToleranceM ||
                        lane.CrossMax > support.Max + GeometryToleranceM)
                        result.ExtendedZones++;
                    entry.CrossMin = lane.CrossMin;
                    entry.CrossMax = lane.CrossMax;
                }
            }
        }

        private static bool PreservesCapturedCoverage(
            AdditionalZone zone,
            IList<CoveredElement> coveredElements,
            IDictionary<RebarLayer, IList<AdditionalZone>> zonesByLayer,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings)
        {
            if (!zonesByLayer.TryGetValue(zone.Layer, out var layerZones)) return false;
            foreach (var item in coveredElements.Where(item => item.Layer == zone.Layer &&
                         (item.CoverageCandidates.Contains(zone) || zone.NodeIds.Contains(item.Plate.Id))))
                if (!IsCovered(item, zonesByLayer, slabOutline, openings)) return false;
            return true;
        }

        private static HashSet<(int First, int Second)> FindIntersectionPairs(IList<Entry> entries)
        {
            var result = new HashSet<(int First, int Second)>();
            for (var first = 0; first < entries.Count; first++)
            for (var second = first + 1; second < entries.Count; second++)
            {
                var a = entries[first];
                var b = entries[second];
                if (a.Zone.Layer != b.Zone.Layer || a.Zone.Direction != b.Zone.Direction) continue;
                var firstBounds = Bounds(a.Zone.Contour);
                var secondBounds = Bounds(b.Zone.Contour);
                var firstCross = Cross(firstBounds, a.Zone.Direction);
                var secondCross = Cross(secondBounds, b.Zone.Direction);
                if (Math.Abs(firstCross.Min - secondCross.Min) <= GeometryToleranceM &&
                    Math.Abs(firstCross.Max - secondCross.Max) <= GeometryToleranceM)
                    continue;
                if (Math.Min(firstBounds.MaxX, secondBounds.MaxX) -
                        Math.Max(firstBounds.MinX, secondBounds.MinX) > GeometryToleranceM &&
                    Math.Min(firstBounds.MaxY, secondBounds.MaxY) -
                        Math.Max(firstBounds.MinY, secondBounds.MinY) > GeometryToleranceM)
                    result.Add((first, second));
            }
            return result;
        }

        private static HashSet<(int First, int Second)> FindGapSpacingConflicts(IList<Entry> entries)
        {
            var result = new HashSet<(int First, int Second)>();
            for (var first = 0; first < entries.Count; first++)
            for (var second = first + 1; second < entries.Count; second++)
            {
                var a = entries[first];
                var b = entries[second];
                if (a.Zone.Layer != b.Zone.Layer || a.Zone.Direction != b.Zone.Direction) continue;
                var firstBounds = Bounds(a.Zone.Contour);
                var secondBounds = Bounds(b.Zone.Contour);
                var axialA = Axial(firstBounds, a.Zone.Direction);
                var axialB = Axial(secondBounds, b.Zone.Direction);
                if (Math.Min(axialA.Max, axialB.Max) - Math.Max(axialA.Min, axialB.Min) <=
                    GeometryToleranceM) continue;
                var supportA = Cross(a.Support, a.Zone.Direction);
                var supportB = Cross(b.Support, b.Zone.Direction);
                var supportGap = Math.Max(0, Math.Max(supportA.Min, supportB.Min) -
                                             Math.Min(supportA.Max, supportB.Max));
                var requiredGap = Math.Min(a.StepMm, b.StepMm) / 1000.0;
                if (supportGap > requiredGap + GeometryToleranceM) continue;
                var actualA = Cross(firstBounds, a.Zone.Direction);
                var actualB = Cross(secondBounds, b.Zone.Direction);
                var actualGap = actualA.Max <= actualB.Min
                    ? actualB.Min - actualA.Max
                    : actualB.Max <= actualA.Min
                        ? actualA.Min - actualB.Max
                        : double.NegativeInfinity;
                if (actualGap >= 0 && Math.Abs(actualGap - requiredGap) > GeometryToleranceM)
                    result.Add((first, second));
            }
            return result;
        }

        private static bool HasNewConflicts(
            IList<Entry> entries, ISet<(int First, int Second)> originalConflicts) =>
            FindIntersectionPairs(entries).Any(pair => !originalConflicts.Contains(pair)) ||
            FindGapSpacingConflicts(entries).Any(pair => !originalConflicts.Contains(pair));

        private static void UpdateResidualConflictCounts(
            IList<Entry> entries, ZonePatchNeighborLayoutResult result)
        {
            result.ResidualIntersections = FindIntersectionPairs(entries).Count;
            result.ResidualSpacingConflicts = FindGapSpacingConflicts(entries).Count;
            result.UnresolvedPairs = result.ResidualIntersections + result.ResidualSpacingConflicts;
        }

        private static double ReinforcementAreaPriority(Entry entry) =>
            entry.Capacity * (Cross(entry.Support, entry.Zone.Direction).Max -
                              Cross(entry.Support, entry.Zone.Direction).Min);

        private static void TranslateZone(AdditionalZone zone, double dx, double dy)
        {
            zone.Contour = zone.Contour.Select(point =>
                new Point3(point.X + dx, point.Y + dy, point.Z)).ToList();
            zone.Placement = new Point3(zone.Placement.X + dx, zone.Placement.Y + dy,
                zone.Placement.Z);
        }

        private static double CapacityOf(AdditionalZone zone) =>
            zone.AsCoveredCm2PerM > 0
                ? zone.AsCoveredCm2PerM
                : BarCapacity.AsCm2PerM(zone.DiameterMm, zone.BarStepMm);

        private static bool IsCovered(
            CoveredElement item,
            IDictionary<RebarLayer, IList<AdditionalZone>> zonesByLayer,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings) =>
            zonesByLayer.TryGetValue(item.Layer, out var layerZones) &&
            ZoneCoverageRules.CoversOrBridgesGap(
                layerZones, item.Plate, item.Layer, item.RequiredAs, slabOutline, openings);

        private static bool IntersectsFootprint(AdditionalZone zone, LiraPlateElement plate)
        {
            if (zone.Contour == null || zone.Contour.Count < 3 ||
                plate.Contour == null || plate.Contour.Count < 3)
                return false;
            var zoneBounds = Bounds(zone.Contour);
            var plateBounds = Bounds(plate.Contour);
            var padding = Math.Max(0, zone.BarStepMm) / 1000.0;
            return zoneBounds.MaxX >= plateBounds.MinX - padding &&
                   zoneBounds.MinX <= plateBounds.MaxX + padding &&
                   zoneBounds.MaxY >= plateBounds.MinY - padding &&
                   zoneBounds.MinY <= plateBounds.MaxY + padding;
        }

        private static bool HasAxialOverlap(Lane first, Lane second) =>
            first.Entries.Any(a => second.Entries.Any(b =>
                Math.Min(a.AxialMax, b.AxialMax) - Math.Max(a.AxialMin, b.AxialMin) >
                GeometryToleranceM));

        private static bool AreAdjacentSupportLanes(Lane first, Lane second)
        {
            var gap = Math.Max(0, Math.Max(first.SupportCrossMin, second.SupportCrossMin) -
                                  Math.Min(first.SupportCrossMax, second.SupportCrossMax));
            var maximumGap = Math.Min(first.StepMm, second.StepMm) / 1000.0;
            return gap <= maximumGap + GeometryToleranceM;
        }

        private static bool HasLowerReinforcementPriority(Lane first, Lane second)
        {
            if (first.Capacity < second.Capacity - CoverageTolerance) return true;
            return Math.Abs(first.Capacity - second.Capacity) <= CoverageTolerance &&
                   first.ReinforcementAreaPriority <
                   second.ReinforcementAreaPriority - CoverageTolerance;
        }

        private static bool IsAdjustable(AdditionalZone zone) =>
            zone != null && zone.FamilyKind == ZoneFamilyKind.Straight && zone.BarStepMm > 0 &&
            zone.Contour != null && zone.Contour.Count >= 3;

        private static void ApplyInterval(Entry entry)
        {
            var zone = entry.Zone;
            var bounds = Bounds(zone.Contour);
            var minX = zone.Direction == ZoneDirection.X ? bounds.MinX : entry.CrossMin;
            var maxX = zone.Direction == ZoneDirection.X ? bounds.MaxX : entry.CrossMax;
            var minY = zone.Direction == ZoneDirection.X ? entry.CrossMin : bounds.MinY;
            var maxY = zone.Direction == ZoneDirection.X ? entry.CrossMax : bounds.MaxY;
            zone.Contour = new List<Point3>
            {
                new Point3(minX, minY, zone.LevelZM), new Point3(maxX, minY, zone.LevelZM),
                new Point3(maxX, maxY, zone.LevelZM), new Point3(minX, maxY, zone.LevelZM)
            };
            zone.Placement = new Point3((minX + maxX) * 0.5, (minY + maxY) * 0.5, zone.LevelZM);
            zone.WidthM = entry.CrossMax - entry.CrossMin;
            zone.WidthMm = UnitConversion.MetersToMm(zone.WidthM);
            zone.BarCount = Math.Max(1, (int)Math.Floor(zone.WidthMm / zone.BarStepMm + 1e-9) + 1);
        }

        private static Snapshot Capture(AdditionalZone zone) => new Snapshot
        {
            Contour = zone.Contour.ToList(),
            Placement = zone.Placement,
            WidthM = zone.WidthM,
            WidthMm = zone.WidthMm,
            BarCount = zone.BarCount
        };

        private static void Restore(AdditionalZone zone, Snapshot snapshot)
        {
            zone.Contour = snapshot.Contour;
            zone.Placement = snapshot.Placement;
            zone.WidthM = snapshot.WidthM;
            zone.WidthMm = snapshot.WidthMm;
            zone.BarCount = snapshot.BarCount;
        }

        private static bool NormalizeRestoredZoneWidth(AdditionalZone zone)
        {
            if (zone.BarStepMm <= 0 || zone.Contour == null || zone.Contour.Count < 3) return false;
            var bounds = Bounds(zone.Contour);
            var cross = Cross(bounds, zone.Direction);
            var stepM = zone.BarStepMm / 1000.0;
            var currentWidth = cross.Max - cross.Min;
            var targetWidth = Math.Max(stepM, RoundUpToStep(currentWidth, stepM));
            var changed = targetWidth > currentWidth + GeometryToleranceM;
            var center = (cross.Min + cross.Max) * 0.5;
            var start = center - targetWidth * 0.5;
            var minX = zone.Direction == ZoneDirection.X ? bounds.MinX : start;
            var maxX = zone.Direction == ZoneDirection.X ? bounds.MaxX : start + targetWidth;
            var minY = zone.Direction == ZoneDirection.X ? start : bounds.MinY;
            var maxY = zone.Direction == ZoneDirection.X ? start + targetWidth : bounds.MaxY;
            zone.Contour = new List<Point3>
            {
                new Point3(minX, minY, zone.LevelZM), new Point3(maxX, minY, zone.LevelZM),
                new Point3(maxX, maxY, zone.LevelZM), new Point3(minX, maxY, zone.LevelZM)
            };
            zone.Placement = new Point3((minX + maxX) * 0.5, (minY + maxY) * 0.5, zone.LevelZM);
            zone.WidthM = targetWidth;
            zone.WidthMm = UnitConversion.MetersToMm(targetWidth);
            zone.BarCount = Math.Max(1, (int)Math.Floor(zone.WidthMm / zone.BarStepMm + 1e-9) + 1);
            return changed;
        }

        private static (double Min, double Max) Cross(ZonePatchFrameBounds bounds, ZoneDirection direction) =>
            direction == ZoneDirection.X ? (bounds.MinY, bounds.MaxY) : (bounds.MinX, bounds.MaxX);

        private static (double Min, double Max) Axial(ZonePatchFrameBounds bounds, ZoneDirection direction) =>
            direction == ZoneDirection.X ? (bounds.MinX, bounds.MaxX) : (bounds.MinY, bounds.MaxY);

        private static ZonePatchFrameBounds Bounds(IList<Point3> contour) => new ZonePatchFrameBounds(
            contour.Min(point => point.X), contour.Max(point => point.X),
            contour.Min(point => point.Y), contour.Max(point => point.Y));

        private static double RoundUpToStep(double value, double step) =>
            step <= 0 ? value : Math.Ceiling((value - 1e-9) / step) * step;

        private static double RoundDownToStep(double value, double step) =>
            step <= 0 ? value : Math.Floor((value + 1e-9) / step) * step;

        private static double Clamp(double value, double min, double max) =>
            Math.Max(min, Math.Min(max, value));

        private static bool LayerEnabled(RebarLayer layer, AnalysisSettings settings) => layer switch
        {
            RebarLayer.As1 => settings.ShowAs1,
            RebarLayer.As2 => settings.ShowAs2,
            RebarLayer.As3 => settings.ShowAs3,
            RebarLayer.As4 => settings.ShowAs4,
            _ => false
        };

        private static double BackgroundAs(RebarLayer layer, AnalysisSettings settings) => layer switch
        {
            RebarLayer.As1 => settings.AsMainAs1,
            RebarLayer.As2 => settings.AsMainAs2,
            RebarLayer.As3 => settings.AsMainAs3,
            RebarLayer.As4 => settings.AsMainAs4,
            _ => 0
        };
    }

}
