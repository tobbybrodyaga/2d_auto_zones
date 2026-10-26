using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public static class ZonePatchGaplessLayout
    {
        private const double GeometryToleranceM = 1e-6;
        private const double DemandTolerance = MosaicBuilder.PositiveResidualToleranceCm2PerM;
        private const int GeometryScale = 1000000;
        private const int CoordinateUnitsPerMm = 1000;
        private const double NarrowZoneAbsorptionWidthM = 0.4;

        private sealed class ZoneEntry
        {
            public AdditionalZone Original = null!;
            public AdditionalZone Candidate = null!;
            public ZonePatchFrameBounds Support;
            public ZonePatchFrameBounds Patch;
            public (double Min, double Max) OriginalCross;
            public (double Min, double Max) SupportCross;
            public (double Min, double Max) AnchorageAxial;
        }

        private sealed class ZoneMoveUnit
        {
            public List<ZoneEntry> Members = new List<ZoneEntry>();
        }

        private sealed class SweepGroup
        {
            public List<ZoneEntry> Members = new List<ZoneEntry>();
            public (double Min, double Max) OriginalCross;
            public (double Min, double Max) Axial;
        }

        private sealed class LayoutState
        {
            public long PositionMm;
            public double Cost;
            public LayoutState? Previous;
            public long ZoneStartMm;
            public long ZoneEndMm;
        }

        public static bool TryArrange(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out string warning,
            IList<ZonePatch>? sourcePatches = null)
        {
            warning = string.Empty;
            if (zones == null || zones.Count == 0) return true;
            if (settings == null || plates == null)
            {
                warning = "Недостаточно данных для построения раскладки без зазоров.";
                AddWarning(zones, warning);
                return false;
            }

            maxPatchOverrunM = ComputeUnboundedPatchOverrun(
                zones, sourceBoundsByZone, patchBoundsByZone, minimumWidthM);

            var arrangedByLayer = new List<AdditionalZone>();
            var layerWarnings = new List<string>();
            foreach (var layer in Enum.GetValues(typeof(RebarLayer)).Cast<RebarLayer>())
            {
                var originals = zones.Where(zone => zone.Layer == layer).ToList();
                if (originals.Count == 0) continue;

                var layerZones = originals.Select(CopyZone).ToList();
                var initialZones = layerZones.Select(CopyZone).ToList();
                var layerSources = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
                var layerPatches = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
                for (var i = 0; i < originals.Count; i++)
                {
                    if (sourceBoundsByZone != null && sourceBoundsByZone.TryGetValue(originals[i], out var source))
                        layerSources[layerZones[i]] = source;
                    if (patchBoundsByZone != null && patchBoundsByZone.TryGetValue(originals[i], out var patch))
                        layerPatches[layerZones[i]] = patch;
                }
                var initialSources = CloneBoundsForZones(layerZones, initialZones, layerSources);
                var initialPatches = CloneBoundsForZones(layerZones, initialZones, layerPatches);

                var layerSettings = SettingsForLayer(settings, layer);
                var baselineCoverage = ZoneLayoutDiagnostics.Evaluate(
                    plates, layerZones, layerSettings, 0, true, slabOutline, openings);
                var baselineUncoveredIds = new HashSet<int>(baselineCoverage.Issues
                    .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                    .Select(issue => issue.ElementId));
                var arranged = TryArrangeLayerOnePass(
                    layerZones, layerSources, layerPatches, plates,
                    layerSettings, slabOutline, openings,
                    minimumWidthM, maxPatchOverrunM, out warning);
                if (!arranged || CountPositiveAreaConflicts(layerZones) > 0)
                {
                    var repairedZones = initialZones.Select(CopyZone).ToList();
                    var repairedSources = CloneBoundsForZones(initialZones, repairedZones, initialSources);
                    var repairedPatches = CloneBoundsForZones(initialZones, repairedZones, initialPatches);
                    var rebuildWarning = string.Empty;
                    var rebuiltFromPatches = sourcePatches != null &&
                        TryRebuildConflictsFromPatches(
                            repairedZones, repairedSources, repairedPatches, sourcePatches,
                            plates, layerSettings, slabOutline, openings, minimumWidthM,
                            baselineUncoveredIds, out rebuildWarning);
                    if (rebuiltFromPatches && CountPositiveAreaConflicts(repairedZones) > 0)
                    {
                        var safePartialZones = repairedZones.Select(CopyZone).ToList();
                        var safePartialSources = CloneBoundsForZones(
                            repairedZones, safePartialZones, repairedSources);
                        var safePartialPatches = CloneBoundsForZones(
                            repairedZones, safePartialZones, repairedPatches);
                        var locallyArranged = TryArrangeLayerOnePass(
                            repairedZones, repairedSources, repairedPatches, plates,
                            layerSettings, slabOutline, openings,
                            minimumWidthM, maxPatchOverrunM, out var localWarning);
                        if (!locallyArranged || CountPositiveAreaConflicts(repairedZones) > 0)
                        {
                            repairedZones = safePartialZones;
                            repairedSources = safePartialSources;
                            repairedPatches = safePartialPatches;
                            if (!string.IsNullOrWhiteSpace(localWarning))
                                rebuildWarning = string.Join(" ", new[] { rebuildWarning, localWarning }
                                    .Where(message => !string.IsNullOrWhiteSpace(message)).Distinct());
                        }
                    }
                    if (rebuiltFromPatches && CountPositiveAreaConflicts(repairedZones) == 0 &&
                        repairedZones.All(HasStepSizedWidth) &&
                        !repairedZones.Any(RebarTables.ExceedsMaxBarLength))
                    {
                        layerZones = repairedZones;
                        arranged = true;
                        warning = string.Empty;
                    }
                    else
                    {
                        var safeBeforePacking = repairedZones.Select(CopyZone).ToList();
                        var safeSourcesBeforePacking = CloneBoundsForZones(
                            repairedZones, safeBeforePacking, repairedSources);
                        var safePatchesBeforePacking = CloneBoundsForZones(
                            repairedZones, safeBeforePacking, repairedPatches);
                        if (TryForceLayerCollisionFreePacking(
                                repairedZones, repairedSources, repairedPatches, plates, layerSettings,
                                slabOutline, openings, minimumWidthM, baselineUncoveredIds,
                                out var coveragePreserved, out var packingWarning) &&
                            coveragePreserved && CountPositiveAreaConflicts(repairedZones) == 0 &&
                            repairedZones.All(HasStepSizedWidth) &&
                            !repairedZones.Any(RebarTables.ExceedsMaxBarLength))
                        {
                            layerZones = repairedZones;
                            arranged = true;
                            warning = string.Empty;
                        }
                        else
                        {
                            layerZones = rebuiltFromPatches ? safeBeforePacking : initialZones;
                            arranged = false;
                            repairedSources = safeSourcesBeforePacking;
                            repairedPatches = safePatchesBeforePacking;
                            var fallbackReason = string.Join(" ", new[]
                            {
                                rebuildWarning,
                                string.IsNullOrWhiteSpace(packingWarning)
                                    ? string.Empty
                                    : "Резервное смещение отменено: " + packingWarning
                            }
                                .Where(message => !string.IsNullOrWhiteSpace(message)).Distinct());
                            warning = string.IsNullOrWhiteSpace(fallbackReason)
                                ? warning
                                : string.IsNullOrWhiteSpace(warning)
                                    ? fallbackReason
                                    : warning + " " + fallbackReason;
                        }
                    }
                }
                if (!arranged) layerWarnings.Add(warning);

                arrangedByLayer.AddRange(layerZones);
            }

            zones.Clear();
            foreach (var zone in arrangedByLayer) zones.Add(zone);
            if (layerWarnings.Count > 0)
            {
                warning = string.Join(" ", layerWarnings.Where(message =>
                    !string.IsNullOrWhiteSpace(message)).Distinct());
                AddWarning(zones, warning);
                return false;
            }
            return true;
        }

        private static bool TryForceLayerCollisionFreePacking(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            ISet<int> originallyUncoveredIds,
            out bool coveragePreserved,
            out string warning)
        {
            coveragePreserved = false;
            warning = string.Empty;
            if (zones == null || zones.Count == 0) return true;

            var entries = zones.Select(zone =>
            {
                var support = sourceBoundsByZone != null && sourceBoundsByZone.TryGetValue(zone, out var source)
                    ? source
                    : Bounds(zone.Contour);
                var patch = patchBoundsByZone != null && patchBoundsByZone.TryGetValue(zone, out var outer)
                    ? outer
                    : support;
                return new ZoneEntry
                {
                    Original = zone,
                    Candidate = zone,
                    Support = support,
                    Patch = patch,
                    OriginalCross = CrossInterval(zone.Contour, zone.Direction),
                    SupportCross = CrossInterval(support, zone.Direction),
                    AnchorageAxial = AxialInterval(zone.Contour, zone.Direction)
                };
            }).ToList();
            if (entries.Any(entry => !IsRectangle(entry.Candidate)))
            {
                warning = "Остались зоны, для которых нельзя безопасно вычислить прямоугольную полосу переноса.";
                return false;
            }

            TryCoalesceConflictsWithoutCoverageLoss(
                entries, zones, sourceBoundsByZone, patchBoundsByZone, plates,
                settings, slabOutline, openings, minimumWidthM, originallyUncoveredIds);

            var units = OrderUnitsFromPeakThenLeftThenRight(
                BuildMoveUnits(entries), plates, settings);
            var travel = units.Sum(unit =>
            {
                var interval = UnitCrossInterval(unit, unit.Members[0].Candidate.Direction);
                var support = (
                    Min: unit.Members.Min(member => member.SupportCross.Min),
                    Max: unit.Members.Max(member => member.SupportCross.Max));
                var step = unit.Members.Max(member => Math.Max(1, member.Candidate.BarStepMm));
                return Math.Max(interval.Max - interval.Min, support.Max - support.Min) +
                       UnitConversion.MmToMeters(step);
            }) + 1.0;
            var placed = new List<ZoneMoveUnit>();

            for (var unitIndex = 0; unitIndex < units.Count; unitIndex++)
            {
                var unit = units[unitIndex];
                var direction = unit.Members[0].Candidate.Direction;
                var original = UnitCrossInterval(unit, direction);
                var widthUnits = ToCoordinateUnits(original.Max - original.Min);
                if (widthUnits <= 0 || unit.Members.Any(member =>
                        Math.Abs(ToCoordinateUnits(CrossInterval(
                            member.Candidate.Contour, direction).Max - CrossInterval(
                            member.Candidate.Contour, direction).Min) - widthUnits) > 1))
                {
                    warning = "Сегменты одной детали имеют разную ширину; сохранение их единой оси невозможно.";
                    return false;
                }

                var requiredCrossMin = unit.Members.Min(member =>
                    ToCoordinateUnits(member.SupportCross.Min));
                var requiredCrossMax = unit.Members.Max(member =>
                    ToCoordinateUnits(member.SupportCross.Max));
                var commonStepMm = unit.Members
                    .Select(member => member.Candidate.BarStepMm)
                    .Aggregate(1, LeastCommonMultiple);
                if (commonStepMm <= 0)
                {
                    warning = "Не удалось вычислить общий шаг сегментов зоны.";
                    return false;
                }
                var commonStepUnits = (long)commonStepMm * CoordinateUnitsPerMm;
                var supportWidthUnits = Math.Max(0, requiredCrossMax - requiredCrossMin);
                var requiredWidthUnits = Math.Max(widthUnits, supportWidthUnits);
                widthUnits = ((requiredWidthUnits + commonStepUnits - 1) / commonStepUnits) *
                             commonStepUnits;

                var allCross = entries.Select(entry => CrossInterval(
                    entry.Candidate.Contour, direction)).ToList();
                var searchMin = allCross.Min(interval => interval.Min) - travel;
                var searchMax = allCross.Max(interval => interval.Max) + travel;
                var minStart = ToCoordinateUnits(searchMin);
                var maxStart = ToCoordinateUnits(searchMax) - widthUnits;
                if (maxStart < minStart)
                {
                    warning = "Не удалось построить свободный поперечный диапазон для слоя.";
                    return false;
                }

                var starts = new HashSet<long>
                {
                    Math.Max(minStart, Math.Min(maxStart, ToCoordinateUnits(original.Min))),
                    minStart,
                    maxStart
                };
                foreach (var placedUnit in placed)
                foreach (var blocker in placedUnit.Members)
                foreach (var member in unit.Members)
                {
                    if (IsIntentionalLapPair(member.Candidate, blocker.Candidate)) continue;
                    var movingAxial = AxialInterval(member.Candidate.Contour, direction);
                    var blockerAxial = AxialInterval(blocker.Candidate.Contour, direction);
                    if (Math.Min(movingAxial.Max, blockerAxial.Max) -
                        Math.Max(movingAxial.Min, blockerAxial.Min) <= GeometryToleranceM) continue;
                    var blockerCross = CrossInterval(blocker.Candidate.Contour, direction);
                    starts.Add(ToCoordinateUnits(blockerCross.Min) - widthUnits);
                    starts.Add(ToCoordinateUnits(blockerCross.Max));
                }

                var candidates = starts.Where(start => start >= minStart && start <= maxStart)
                    .Where(start => IsCrossPlacementClear(
                        unit, placed, direction, start / (double)GeometryScale, widthUnits))
                    .OrderBy(start => Math.Abs(start - ToCoordinateUnits(original.Min)))
                    .ThenBy(start => start)
                    .ToList();
                long? selected = candidates.Where(start => !IntersectsOpeningAt(
                        unit, openings, direction,
                        start / (double)GeometryScale,
                        (start + widthUnits) / (double)GeometryScale))
                    .Where(start => PreservesAffectedCoverageAt(
                        unit, units, plates, settings, slabOutline, openings,
                        originallyUncoveredIds, direction,
                        start / (double)GeometryScale, widthUnits))
                    .Where(start => PreservesAssignedElementCoverage(
                        unit, placed, start, widthUnits, entries,
                        units.Where(candidate => !ReferenceEquals(candidate, unit))
                            .SelectMany(candidate => candidate.Members)
                            .Select(member => member.Candidate),
                        plates, settings, slabOutline, openings, originallyUncoveredIds))
                    .Select(start => (long?)start)
                    .FirstOrDefault();
                if (!selected.HasValue && CanRemoveUnitWithoutCoverageLoss(
                        unit, units, plates, settings, slabOutline, openings,
                        originallyUncoveredIds))
                {
                    foreach (var member in unit.Members)
                    {
                        zones.Remove(member.Candidate);
                        entries.Remove(member);
                    }
                    units.RemoveAt(unitIndex--);
                    continue;
                }
                selected ??= candidates.Where(start => !IntersectsOpeningAt(
                        unit, openings, direction,
                        start / (double)GeometryScale,
                        (start + widthUnits) / (double)GeometryScale))
                    .Select(start => (long?)start)
                    .FirstOrDefault();
                selected ??= candidates.Select(start => (long?)start).FirstOrDefault();
                if (!selected.HasValue)
                {
                    warning = "Не удалось найти свободную поперечную полосу даже за пределами пятна.";
                    return false;
                }

                var interval = (selected.Value / (double)GeometryScale,
                    (selected.Value + widthUnits) / (double)GeometryScale);
                foreach (var member in unit.Members)
                    SetCrossInterval(member.Candidate, interval);
                placed.Add(unit);
            }

            var conflicts = CountPositiveAreaConflicts(zones);
            if (conflicts > 0)
            {
                warning = $"После свободного переноса осталось фактических пересечений: {conflicts}.";
                return false;
            }

            var finalCoverage = ZoneLayoutDiagnostics.Evaluate(
                plates, zones, settings, 0, true, slabOutline, openings);
            var uncoveredIds = finalCoverage.Issues
                .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                .Select(issue => issue.ElementId).ToHashSet();
            coveragePreserved = finalCoverage.UncoveredCount <= originallyUncoveredIds.Count &&
                                uncoveredIds.All(originallyUncoveredIds.Contains);
            if (!coveragePreserved)
                warning = $"Пересечения геометрии устранены, но непокрытые КЭ изменились: " +
                          $"исходно {originallyUncoveredIds.Count}, после переноса {finalCoverage.UncoveredCount}.";
            return true;
        }

        private static void TryCoalesceConflictsWithoutCoverageLoss(
            IList<ZoneEntry> entries,
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            ISet<int> originallyUncoveredIds)
        {
            var groups = BuildConflictGroups(entries).Where(group => group.Count > 1).ToList();
            if (groups.Count == 0) return;

            var planned = new List<(List<ZoneEntry> Group, ZoneEntry Retained, AdditionalZone Merged)>();
            foreach (var group in groups)
            {
                if (!TryAbsorbConflictGroup(group, settings, openings, minimumWidthM, 0,
                        out var retained, out var merged, out _)) continue;
                planned.Add((group, retained, merged));
            }
            if (planned.Count == 0) return;

            var removed = new HashSet<AdditionalZone>(planned
                .SelectMany(item => item.Group).Select(entry => entry.Candidate));
            var trialZones = zones.Where(zone => !removed.Contains(zone)).ToList();
            foreach (var item in planned) trialZones.Add(item.Merged);
            if (CountPositiveAreaConflicts(trialZones) >= CountPositiveAreaConflicts(zones)) return;

            var trialCoverage = ZoneLayoutDiagnostics.Evaluate(
                plates, trialZones, settings, 0, true, slabOutline, openings);
            var uncoveredIds = trialCoverage.Issues
                .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                .Select(issue => issue.ElementId)
                .ToHashSet();
            if (trialCoverage.UncoveredCount > originallyUncoveredIds.Count ||
                uncoveredIds.Any(id => !originallyUncoveredIds.Contains(id))) return;

            zones.Clear();
            foreach (var zone in trialZones) zones.Add(zone);
            foreach (var item in planned)
            {
                var support = UnionBounds(item.Group, usePatchBounds: false);
                var patch = UnionBounds(item.Group, usePatchBounds: true);
                item.Retained.Candidate = item.Merged;
                item.Retained.Support = support;
                item.Retained.Patch = patch;
                item.Retained.OriginalCross = CrossInterval(item.Merged.Contour, item.Merged.Direction);
                item.Retained.SupportCross = CrossInterval(support, item.Merged.Direction);
                item.Retained.AnchorageAxial = (
                    item.Group.Min(entry => entry.AnchorageAxial.Min),
                    item.Group.Max(entry => entry.AnchorageAxial.Max));
                foreach (var entry in item.Group)
                    if (!ReferenceEquals(entry, item.Retained)) entries.Remove(entry);
                sourceBoundsByZone[item.Merged] = support;
                if (patchBoundsByZone != null) patchBoundsByZone[item.Merged] = patch;
            }
        }

        private static bool TryRebuildConflictsFromPatches(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> patchBoundsByZone,
            IList<ZonePatch> sourcePatches,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            ISet<int> originallyUncoveredIds,
            out string warning)
        {
            warning = string.Empty;
            if (sourcePatches == null || sourcePatches.Count == 0 ||
                CountPositiveAreaConflicts(zones) == 0) return false;

            var working = zones.Select(CopyZone).ToList();
            var workingSources = CloneBoundsForZones(zones, working, sourceBoundsByZone);
            var workingPatches = CloneBoundsForZones(zones, working, patchBoundsByZone);
            var maximumPasses = Math.Max(1, zones.Count);
            var changedAny = false;
            for (var pass = 0; pass < maximumPasses &&
                 CountPositiveAreaConflicts(working) > 0; pass++)
            {
                var changed = false;
                foreach (var component in BuildPositiveAreaConflictGroups(working)
                             .OrderByDescending(group => group.Count))
                {
                    var layer = component[0].Layer;
                    var elementIds = new HashSet<int>(component
                        .SelectMany(zone => zone.NodeIds ?? new List<int>()));
                    if (elementIds.Count == 0) continue;

                    var componentPatches = sourcePatches
                        .Where(patch => patch.Layer == layer && patch.ElementIds != null &&
                                        patch.ElementIds.Any(elementIds.Contains))
                        .GroupBy(patch => patch.PatchId)
                        .Select(group => group.First())
                        .ToList();
                    if (componentPatches.Count == 0) continue;

                    var patchElementIds = new HashSet<int>(componentPatches
                        .SelectMany(patch => patch.ElementIds ?? new List<int>()));
                    var frameElements = plates
                        .Where(plate => patchElementIds.Contains(plate.Id) && plate.Rebar.Ok &&
                                        plate.Contour != null && plate.Contour.Count >= 3)
                        .Select(plate =>
                        {
                            var requiredAs = plate.Rebar.Get(layer) - Background(settings, layer);
                            return new ZonePatchFrameElement
                            {
                                ElementId = plate.Id,
                                Layer = layer,
                                AsAdditionalCm2PerM = requiredAs,
                                Contour = plate.Contour
                            };
                        })
                        .Where(element => element.AsAdditionalCm2PerM > DemandTolerance)
                        .ToList();
                    if (frameElements.Count == 0) continue;

                    var selection = new ZonePatchFrameSelection
                    {
                        Patches = componentPatches,
                        Elements = frameElements,
                        SlabOutline = slabOutline?.ToList() ?? new List<Point3>(),
                        MinXM = componentPatches.Min(patch => patch.MinXM),
                        MaxXM = componentPatches.Max(patch => patch.MaxXM),
                        MinYM = componentPatches.Min(patch => patch.MinYM),
                        MaxYM = componentPatches.Max(patch => patch.MaxYM)
                    };
                    var rebuilt = ZonePatchZoneBuilder.Build(selection,
                        component[0].LevelZM, settings);
                    if (rebuilt.Count == 0 || rebuilt.Any(zone =>
                            zone.Layer != layer || !HasStepSizedWidth(zone) ||
                            RebarTables.ExceedsMaxBarLength(zone) ||
                            (openings != null && openings.Count > 0 &&
                             ZoneEditor.IntersectsOpening(zone, openings))))
                        continue;

                    var removed = new HashSet<AdditionalZone>(component);
                    var trial = working.Where(zone => !removed.Contains(zone)).ToList();
                    trial.AddRange(rebuilt);
                    if (CountPositiveAreaConflicts(trial) >=
                        CountPositiveAreaConflicts(working)) continue;

                    var coverage = ZoneLayoutDiagnostics.Evaluate(
                        plates, trial, settings, 0, true, slabOutline, openings);
                    var uncoveredIds = coverage.Issues
                        .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                        .Select(issue => issue.ElementId)
                        .ToHashSet();
                    if (coverage.UncoveredCount > originallyUncoveredIds.Count ||
                        uncoveredIds.Any(id => !originallyUncoveredIds.Contains(id)))
                        continue;

                    var patchBounds = new ZonePatchFrameBounds(
                        componentPatches.Min(patch => patch.MinXM),
                        componentPatches.Max(patch => patch.MaxXM),
                        componentPatches.Min(patch => patch.MinYM),
                        componentPatches.Max(patch => patch.MaxYM));
                    foreach (var zone in component)
                    {
                        workingSources.Remove(zone);
                        workingPatches.Remove(zone);
                    }
                    foreach (var zone in rebuilt)
                    {
                        workingSources[zone] = Bounds(zone.Contour);
                        workingPatches[zone] = patchBounds;
                    }

                    working = trial;
                    changed = true;
                    changedAny = true;
                    break;
                }

                if (!changed) break;
            }

            if (CountPositiveAreaConflicts(working) > 0)
            {
                if (!changedAny)
                {
                    warning = "Не удалось объединить конфликтные пятна без потери покрытия КЭ.";
                    return false;
                }
                warning = $"Объединение пятен сократило пересечения до " +
                          $"{CountPositiveAreaConflicts(working)}, остались группы у отверстий или длинных стыков.";
            }

            zones.Clear();
            foreach (var zone in working) zones.Add(zone);
            sourceBoundsByZone.Clear();
            foreach (var pair in workingSources) sourceBoundsByZone[pair.Key] = pair.Value;
            patchBoundsByZone.Clear();
            foreach (var pair in workingPatches) patchBoundsByZone[pair.Key] = pair.Value;
            return true;
        }

        private static List<List<AdditionalZone>> BuildPositiveAreaConflictGroups(
            IList<AdditionalZone> zones)
        {
            var result = new List<List<AdditionalZone>>();
            foreach (var layerZones in zones.GroupBy(zone => zone.Layer))
            {
                var pending = new HashSet<AdditionalZone>(layerZones);
                while (pending.Count > 0)
                {
                    var seed = pending.First();
                    pending.Remove(seed);
                    var group = new List<AdditionalZone> { seed };
                    var queue = new Queue<AdditionalZone>();
                    queue.Enqueue(seed);
                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        foreach (var candidate in pending.ToList())
                        {
                            if (!HasPositiveAreaOverlap(current, candidate)) continue;
                            pending.Remove(candidate);
                            group.Add(candidate);
                            queue.Enqueue(candidate);
                        }
                    }

                    if (group.Count > 1 && CountPositiveAreaConflicts(group) > 0)
                        result.Add(group);
                }
            }

            return result;
        }

        private static bool CanRemoveUnitWithoutCoverageLoss(
            ZoneMoveUnit unit,
            IList<ZoneMoveUnit> allUnits,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            ISet<int> originallyUncoveredIds)
        {
            var coverageZones = allUnits.Where(candidate => !ReferenceEquals(candidate, unit))
                .SelectMany(candidate => candidate.Members)
                .Select(member => member.Candidate)
                .ToList();
            var affectedBounds = unit.Members
                .SelectMany(member => new[] { Bounds(member.Candidate.Contour), member.Support })
                .ToList();
            var layer = unit.Members[0].Candidate.Layer;
            foreach (var plate in plates)
            {
                if (!plate.Rebar.Ok || originallyUncoveredIds.Contains(plate.Id)) continue;
                var requiredAs = plate.Rebar.Get(layer) - Background(settings, layer);
                if (requiredAs <= MosaicBuilder.PositiveResidualToleranceCm2PerM) continue;
                var plateBounds = plate.Contour != null && plate.Contour.Count >= 3
                    ? Bounds(plate.Contour)
                    : new ZonePatchFrameBounds(plate.Centroid.X, plate.Centroid.X,
                        plate.Centroid.Y, plate.Centroid.Y);
                if (!affectedBounds.Any(bounds =>
                        Math.Min(bounds.MaxX, plateBounds.MaxX) - Math.Max(bounds.MinX, plateBounds.MinX) > GeometryToleranceM &&
                        Math.Min(bounds.MaxY, plateBounds.MaxY) - Math.Max(bounds.MinY, plateBounds.MinY) > GeometryToleranceM))
                    continue;
                if (!ZoneCoverageRules.CoversOrBridgesGap(
                        coverageZones, plate, layer, requiredAs, slabOutline, openings))
                    return false;
            }
            return true;
        }

        private static bool PreservesAffectedCoverageAt(
            ZoneMoveUnit unit,
            IList<ZoneMoveUnit> allUnits,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            ISet<int> originallyUncoveredIds,
            ZoneDirection direction,
            double crossMin,
            long widthUnits)
        {
            var movedZones = new List<AdditionalZone>();
            foreach (var member in unit.Members)
            {
                var moved = CopyZone(member.Candidate);
                var originalCross = CrossInterval(member.Candidate.Contour, direction);
                var delta = crossMin - originalCross.Min;
                SetCrossInterval(moved, (originalCross.Min + delta, originalCross.Max + delta));
                movedZones.Add(moved);
            }

            var coverageZones = allUnits.Where(candidate => !ReferenceEquals(candidate, unit))
                .SelectMany(candidate => candidate.Members)
                .Select(member => member.Candidate)
                .Concat(movedZones)
                .ToList();
            var affectedBounds = unit.Members
                .SelectMany(member => new[] { Bounds(member.Candidate.Contour), member.Support })
                .ToList();
            var layer = unit.Members[0].Candidate.Layer;
            foreach (var plate in plates)
            {
                if (!plate.Rebar.Ok || originallyUncoveredIds.Contains(plate.Id)) continue;
                var requiredAs = plate.Rebar.Get(layer) - Background(settings, layer);
                if (requiredAs <= MosaicBuilder.PositiveResidualToleranceCm2PerM) continue;
                var plateBounds = plate.Contour != null && plate.Contour.Count >= 3
                    ? Bounds(plate.Contour)
                    : new ZonePatchFrameBounds(plate.Centroid.X, plate.Centroid.X,
                        plate.Centroid.Y, plate.Centroid.Y);
                if (!affectedBounds.Any(bounds =>
                        Math.Min(bounds.MaxX, plateBounds.MaxX) - Math.Max(bounds.MinX, plateBounds.MinX) > GeometryToleranceM &&
                        Math.Min(bounds.MaxY, plateBounds.MaxY) - Math.Max(bounds.MinY, plateBounds.MinY) > GeometryToleranceM))
                    continue;
                if (!ZoneCoverageRules.CoversOrBridgesGap(
                        coverageZones, plate, layer, requiredAs, slabOutline, openings))
                    return false;
            }
            return true;
        }

        private static bool IsCrossPlacementClear(
            ZoneMoveUnit unit,
            IList<ZoneMoveUnit> placed,
            ZoneDirection direction,
            double crossMin,
            long widthUnits)
        {
            var crossMax = crossMin + widthUnits / (double)GeometryScale;
            foreach (var member in unit.Members)
            {
                var moved = CopyZone(member.Candidate);
                var originalCross = CrossInterval(member.Candidate.Contour, direction);
                var delta = crossMin - originalCross.Min;
                SetCrossInterval(moved, (originalCross.Min + delta, originalCross.Max + delta));
                foreach (var other in placed.SelectMany(otherUnit => otherUnit.Members))
                {
                    if (IsIntentionalLapPair(member.Candidate, other.Candidate)) continue;
                    if (HasPositiveAreaOverlap(moved, other.Candidate)) return false;
                }
            }
            return true;
        }

        private static int CountPositiveAreaConflicts(IList<AdditionalZone> zones)
        {
            var count = 0;
            for (var i = 0; i < zones.Count; i++)
            for (var j = i + 1; j < zones.Count; j++)
                if (!IsIntentionalLapPair(zones[i], zones[j]) &&
                    HasPositiveAreaOverlap(zones[i], zones[j])) count++;
            return count;
        }

        private static bool HasPositiveAreaOverlap(AdditionalZone first, AdditionalZone second)
        {
            if (first.Layer != second.Layer || first.Contour.Count < 3 || second.Contour.Count < 3)
                return false;
            var a = Bounds(first.Contour);
            var b = Bounds(second.Contour);
            return Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX) > GeometryToleranceM &&
                   Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY) > GeometryToleranceM;
        }

        private static bool TryArrangeSingleLayer(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out string warning)
        {
            warning = string.Empty;
            if (zones == null || zones.Count == 0) return true;
            if (settings == null || plates == null)
            {
                warning = "Недостаточно данных для построения раскладки без зазоров.";
                AddWarning(zones, warning);
                return false;
            }

            var entries = zones.Select(zone =>
            {
                var support = sourceBoundsByZone != null && sourceBoundsByZone.TryGetValue(zone, out var source)
                    ? source
                    : Bounds(zone.Contour);
                var patch = patchBoundsByZone != null && patchBoundsByZone.TryGetValue(zone, out var outer)
                    ? outer
                    : support;
                return new ZoneEntry
                {
                    Original = zone,
                    Candidate = CopyZone(zone),
                    Support = support,
                    Patch = patch,
                    OriginalCross = CrossInterval(zone.Contour, zone.Direction),
                    SupportCross = CrossInterval(support, zone.Direction)
                };
            }).ToList();

            var originalCoverage = ZoneLayoutDiagnostics.Evaluate(
                plates, zones, settings, 0, true, slabOutline, openings);
            var originalUncoveredCount = originalCoverage.UncoveredCount;
            var originalUncoveredIds = new HashSet<int>(originalCoverage.Issues
                .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                .Select(issue => issue.ElementId));

            NormalizeCandidateWidths(entries, minimumWidthM, maxPatchOverrunM);

            var absorbedEntries = new HashSet<ZoneEntry>();
            foreach (var group in BuildConflictGroups(entries.Where(entry =>
                         IsRectangle(entry.Candidate)).ToList()))
            {
                var ordered = group.OrderBy(entry => entry.SupportCross.Min)
                    .ThenBy(entry => entry.SupportCross.Max)
                    .ThenByDescending(entry => entry.Candidate.AsAdditional)
                    .ToList();
                var currentCandidates = entries.Where(entry => !absorbedEntries.Contains(entry))
                    .Select(entry => entry.Candidate).ToList();
                var stripFailure = string.Empty;
                var arrangedSuccessfully = TryArrangeByPriorityStepTransfer(
                    ordered, currentCandidates, plates, settings, slabOutline, openings,
                    minimumWidthM, maxPatchOverrunM, out var intervals);
                if (!arrangedSuccessfully)
                    arrangedSuccessfully = TryArrangeConflictGroup(
                        ordered, plates, settings, minimumWidthM, maxPatchOverrunM,
                        out intervals, out _, out stripFailure);
                while (!arrangedSuccessfully && ordered.Count > 1 && TryDropRedundantZone(
                           ordered, currentCandidates, plates, settings, slabOutline, openings,
                           originalUncoveredCount, out var reducedCandidates,
                           out var redundantEntry, out _))
                {
                    absorbedEntries.Add(redundantEntry);
                    ordered.Remove(redundantEntry);
                    group.Remove(redundantEntry);
                    currentCandidates = reducedCandidates;
                    arrangedSuccessfully = TryArrangeByPriorityStepTransfer(
                        ordered, currentCandidates, plates, settings, slabOutline, openings,
                        minimumWidthM, maxPatchOverrunM, out intervals);
                    if (!arrangedSuccessfully)
                        arrangedSuccessfully = TryArrangeConflictGroup(
                            ordered, plates, settings, minimumWidthM, maxPatchOverrunM,
                            out intervals, out _, out stripFailure);
                }
                if (!arrangedSuccessfully)
                {
                    var absorptionFailure = string.Empty;
                    var candidateSnapshot = entries.Where(entry => !absorbedEntries.Contains(entry))
                        .Select(entry => entry.Candidate).ToList();
                    if (TryAbsorbConflictPair(
                            ordered, entries, candidateSnapshot, plates, settings, slabOutline, openings,
                            minimumWidthM, maxPatchOverrunM, originalUncoveredCount,
                            out _, out var absorbedGroup,
                            out var keeper, out var absorbed, out absorptionFailure))
                    {
                        keeper.Candidate = absorbed;
                        keeper.Support = UnionBounds(absorbedGroup, usePatchBounds: false);
                        keeper.Patch = UnionBounds(absorbedGroup, usePatchBounds: true);
                        keeper.SupportCross = CrossInterval(keeper.Support, absorbed.Direction);
                        keeper.OriginalCross = CrossInterval(absorbed.Contour, absorbed.Direction);
                        foreach (var entry in absorbedGroup)
                            if (!ReferenceEquals(entry, keeper)) absorbedEntries.Add(entry);
                        continue;
                    }

                    var fallbackWarning = string.Empty;
                    if (TryBuildConflictFallback(
                            entries, plates, settings, slabOutline, openings,
                            minimumWidthM, maxPatchOverrunM, originalUncoveredCount,
                            out var fallbackZones, out fallbackWarning))
                    {
                        zones.Clear();
                        foreach (var fallbackZone in fallbackZones) zones.Add(fallbackZone);
                        return true;
                    }

                    warning = "Не удалось одновременно состыковать зоны с шириной, кратной шагу, " +
                              "и сохранить покрытие КЭ.";
                    warning += $" Группа: {ordered.Count} зон; {stripFailure}. {absorptionFailure} Исходная геометрия оставлена без изменений.";
                    if (!string.IsNullOrWhiteSpace(fallbackWarning)) warning += " " + fallbackWarning;
                    AddWarning(zones, warning);
                    return false;
                }

                var preArrangementZones = ordered.Select(entry => CopyZone(entry.Candidate)).ToList();
                for (var i = 0; i < ordered.Count; i++)
                    SetCrossInterval(ordered[i].Candidate, intervals[i]);

                var arrangedCoverage = ZoneLayoutDiagnostics.Evaluate(
                    plates, entries.Where(entry => !absorbedEntries.Contains(entry))
                        .Select(entry => entry.Candidate).ToList(),
                    settings, 0, true, slabOutline, openings);
                if (arrangedCoverage.UncoveredCount > originalUncoveredCount ||
                    arrangedCoverage.Issues.Any(issue =>
                        issue.Kind == ZoneIssueKind.UncoveredElement &&
                        !originalUncoveredIds.Contains(issue.ElementId)))
                {
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        ordered[i].Candidate = preArrangementZones[i];
                        ordered[i].OriginalCross = CrossInterval(
                            preArrangementZones[i].Contour, preArrangementZones[i].Direction);
                    }
                }
            }

            var candidateZones = entries.Where(entry => !absorbedEntries.Contains(entry))
                .Select(entry => entry.Candidate).ToList();
            if (CountArrangementConflicts(candidateZones) > 0)
            {
                var candidateSources = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
                var candidatePatches = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
                foreach (var entry in entries.Where(entry => !absorbedEntries.Contains(entry)))
                {
                    candidateSources[entry.Candidate] = entry.Support;
                    candidatePatches[entry.Candidate] = entry.Patch;
                }
                if (!TryResolveExistingConflicts(
                        candidateZones, candidateSources, candidatePatches, plates,
                        settings, slabOutline, openings, minimumWidthM, maxPatchOverrunM,
                        out var conflictWarning) || CountArrangementConflicts(candidateZones) > 0)
                {
                    warning = string.IsNullOrWhiteSpace(conflictWarning)
                        ? "После согласования независимых групп остались пересечения зон."
                        : conflictWarning;
                    warning += " Исходная геометрия оставлена без изменений.";
                    AddWarning(zones, warning);
                    return false;
                }
            }

            var zonesBeforeMerge = candidateZones.ToList();
            var conflictsBeforeMerge = CountArrangementConflicts(candidateZones);
            var uncoveredBeforeMerge = ZoneLayoutDiagnostics.Evaluate(
                plates, candidateZones, settings, 0, true, slabOutline, openings).UncoveredCount;
            var mergedCount = ZonePatchZoneBuilder.MergeAdjacentCompatibleZones(candidateZones);
            var remainingConflicts = FindArrangementConflictPairs(candidateZones);
            var mergedCoverage = ZoneLayoutDiagnostics.Evaluate(
                plates, candidateZones, settings, 0, true, slabOutline, openings);
            if (remainingConflicts.Count > 0 ||
                candidateZones.Any(zone => !HasStepSizedWidth(zone)) ||
                mergedCoverage.UncoveredCount > uncoveredBeforeMerge)
            {
                candidateZones = zonesBeforeMerge;
                mergedCount = 0;
                remainingConflicts = FindArrangementConflictPairs(candidateZones);
            }

            if (remainingConflicts.Count > 0)
            {
                var examples = string.Join(", ", remainingConflicts.Take(4).Select(pair =>
                    $"Ø{pair.First.DiameterMm}/{pair.First.BarStepMm} №{pair.First.ZoneId} vs " +
                    $"Ø{pair.Second.DiameterMm}/{pair.Second.BarStepMm} №{pair.Second.ZoneId}"));
                warning = $"После согласования осталось пересечений: {remainingConflicts.Count} " +
                          $"(до объединения {conflictsBeforeMerge}, объединено {mergedCount}; {examples}). " +
                          "Исходная геометрия оставлена без изменений.";
                AddWarning(zones, warning);
                return false;
            }

            if (candidateZones.Any(zone => !HasStepSizedWidth(zone)))
            {
                warning = "После объединения соседних зон ширина перестала соответствовать шагу. " +
                          "Исходная геометрия оставлена без изменений.";
                AddWarning(zones, warning);
                return false;
            }

            var diagnostics = ZoneLayoutDiagnostics.Evaluate(
                plates, candidateZones, settings, 0, true, slabOutline, openings);
            if (diagnostics.UncoveredCount > originalUncoveredCount)
            {
                if (TryBuildConflictFallback(
                        entries, plates, settings, slabOutline, openings,
                        minimumWidthM, maxPatchOverrunM, originalUncoveredCount,
                        out var fallbackZones, out var fallbackWarning))
                {
                    candidateZones = fallbackZones;
                    diagnostics = ZoneLayoutDiagnostics.Evaluate(
                        plates, candidateZones, settings, 0, true, slabOutline, openings);
                }

                if (diagnostics.UncoveredCount > originalUncoveredCount)
                {
                    warning = $"Согласование зон увеличило число непокрытых КЭ: " +
                              $"{originalUncoveredCount} → {diagnostics.UncoveredCount}; " +
                              "результат отклонён. " + fallbackWarning;
                    AddWarning(zones, warning);
                    return false;
                }
            }

            zones.Clear();
            foreach (var candidate in candidateZones) zones.Add(candidate);
            return true;
        }

        private static bool TryArrangeLayerOnePass(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out string warning)
        {
            warning = string.Empty;
            if (zones == null || zones.Count == 0) return true;
            if (settings == null || plates == null)
            {
                warning = "Недостаточно данных для размещения зон одного слоя.";
                return false;
            }

            var entries = zones.Select(zone =>
            {
                var support = sourceBoundsByZone != null && sourceBoundsByZone.TryGetValue(zone, out var source)
                    ? source
                    : Bounds(zone.Contour);
                var patch = patchBoundsByZone != null && patchBoundsByZone.TryGetValue(zone, out var outer)
                    ? outer
                    : support;
                return new ZoneEntry
                {
                    Original = zone,
                    Candidate = CopyZone(zone),
                    Support = support,
                    Patch = patch,
                    OriginalCross = CrossInterval(zone.Contour, zone.Direction),
                    SupportCross = CrossInterval(support, zone.Direction),
                    AnchorageAxial = AxialInterval(zone.Contour, zone.Direction)
                };
            }).ToList();

            var originalCoverage = ZoneLayoutDiagnostics.Evaluate(
                plates, zones, settings, 0, true, slabOutline, openings);
            var originalUncoveredIds = new HashSet<int>(originalCoverage.Issues
                .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                .Select(issue => issue.ElementId));

            NormalizeLayerWidths(entries, minimumWidthM, maxPatchOverrunM);
            var units = OrderUnitsFromPeakThenLeftThenRight(
                BuildMoveUnits(entries), plates, settings);
            var layerCrossMin = entries.Min(entry => Math.Min(
                entry.SupportCross.Min, entry.OriginalCross.Min));
            var layerCrossMax = entries.Max(entry => Math.Max(
                entry.SupportCross.Max, entry.OriginalCross.Max));
            var relocationPadding = units.Sum(unit => unit.Members.Max(member =>
                CrossInterval(member.Candidate.Contour, member.Candidate.Direction).Max -
                CrossInterval(member.Candidate.Contour, member.Candidate.Direction).Min +
                UnitConversion.MmToMeters(Math.Max(1, member.Candidate.BarStepMm)))) + 1.0;
            var searchMin = layerCrossMin - relocationPadding;
            var searchMax = layerCrossMax + relocationPadding;
            var placedUnits = new List<ZoneMoveUnit>();
            var reducedWidthEntries = new HashSet<ZoneEntry>();

            for (var unitIndex = 0; unitIndex < units.Count; unitIndex++)
            {
                var unit = units[unitIndex];
                var direction = unit.Members[0].Candidate.Direction;
                var original = CrossInterval(unit.Members[0].Candidate.Contour, direction);
                var widthUnits = ToCoordinateUnits(original.Max - original.Min);
                if (unit.Members.Any(entry => Math.Abs(ToCoordinateUnits(
                        CrossInterval(entry.Candidate.Contour, direction).Max -
                        CrossInterval(entry.Candidate.Contour, direction).Min) - widthUnits) > 1))
                {
                    warning = "Не удалось переместить совместно сегменты стержня: ширина сегментов различается.";
                    return false;
                }

                var allowedMin = unit.Members.Min(entry =>
                    CrossInterval(entry.Patch, direction).Min - Math.Max(0, maxPatchOverrunM));
                var allowedMax = unit.Members.Max(entry =>
                    CrossInterval(entry.Patch, direction).Max + Math.Max(0, maxPatchOverrunM));
                allowedMin = Math.Min(allowedMin, original.Min);
                allowedMax = Math.Max(allowedMax, original.Max);
                var allowedStartUnits = ToCoordinateUnits(allowedMin);
                var allowedEndUnits = ToCoordinateUnits(allowedMax);
                var maxStartUnits = allowedEndUnits - widthUnits;
                if (maxStartUnits < allowedStartUnits)
                {
                    warning = $"Не хватает допустимой ширины для зоны слоя {unit.Members[0].Candidate.Layer}.";
                    return false;
                }

                var possibleStarts = new HashSet<long>
                {
                    Math.Max(allowedStartUnits, Math.Min(maxStartUnits, ToCoordinateUnits(original.Min))),
                    allowedStartUnits,
                    maxStartUnits
                };
                foreach (var blockerUnit in placedUnits)
                foreach (var blocker in blockerUnit.Members)
                {
                    if (!unit.Members.Any(member => AxiallyOverlap(member, blocker))) continue;
                    var blockedCross = CrossInterval(blocker.Candidate.Contour, direction);
                    possibleStarts.Add(ToCoordinateUnits(blockedCross.Min) - widthUnits);
                    possibleStarts.Add(ToCoordinateUnits(blockedCross.Max));
                }

                var placementChoices = possibleStarts
                    .Where(start => start >= allowedStartUnits && start <= maxStartUnits)
                    .Select(start => (Start: start, Width: widthUnits, Touch: false))
                    .ToList();
                var requiredCrossMin = unit.Members.Min(member =>
                    ToCoordinateUnits(member.SupportCross.Min));
                var requiredCrossMax = unit.Members.Max(member =>
                    ToCoordinateUnits(member.SupportCross.Max));
                var unitStepUnits = (long)unit.Members.Max(member =>
                    member.Candidate.BarStepMm) * CoordinateUnitsPerMm;
                if (unitStepUnits > 0)
                {
                    foreach (var blockerUnit in placedUnits)
                    foreach (var blocker in blockerUnit.Members)
                    {
                        if (!unit.Members.Any(member => AxiallyOverlap(member, blocker)) ||
                            unit.Members.Any(member => IsIntentionalLapPair(
                                member.Candidate, blocker.Candidate))) continue;
                        var blockedCross = CrossInterval(blocker.Candidate.Contour, direction);
                        var blockedMin = ToCoordinateUnits(blockedCross.Min);
                        var blockedMax = ToCoordinateUnits(blockedCross.Max);

                        if (blockedMax <= requiredCrossMin)
                        {
                            var requiredWidth = Math.Max(widthUnits, requiredCrossMax - blockedMax);
                            var touchingWidth = ((requiredWidth + unitStepUnits - 1) / unitStepUnits) * unitStepUnits;
                            if (blockedMax >= allowedStartUnits &&
                                blockedMax + touchingWidth <= allowedEndUnits)
                                placementChoices.Add((blockedMax, touchingWidth, true));
                        }
                        if (blockedMin >= requiredCrossMax)
                        {
                            var requiredWidth = Math.Max(widthUnits, blockedMin - requiredCrossMin);
                            var touchingWidth = ((requiredWidth + unitStepUnits - 1) / unitStepUnits) * unitStepUnits;
                            var touchingStart = blockedMin - touchingWidth;
                            if (touchingStart >= allowedStartUnits &&
                                blockedMin <= allowedEndUnits)
                                placementChoices.Add((touchingStart, touchingWidth, true));
                        }
                    }
                }

                var availablePlacement = placementChoices
                    .Where(choice => choice.Start + choice.Width <= allowedEndUnits)
                    .Where(choice => choice.Width % unitStepUnits == 0)
                    .Where(choice => !OverlapsPlacedZones(unit, placedUnits, direction,
                        choice.Start / (double)GeometryScale,
                        (choice.Start + choice.Width) / (double)GeometryScale))
                    .Where(choice => !IntersectsOpeningAt(unit, openings, direction,
                        choice.Start / (double)GeometryScale,
                        (choice.Start + choice.Width) / (double)GeometryScale))
                    .Where(choice => PreservesAssignedElementCoverage(
                        unit, placedUnits, choice.Start, choice.Width, unit.Members,
                        units.Where(candidate => !ReferenceEquals(candidate, unit))
                            .SelectMany(candidate => candidate.Members)
                            .Select(member => member.Candidate),
                        plates, settings, slabOutline, openings, originalUncoveredIds))
                    .OrderByDescending(choice => choice.Touch)
                    .ThenBy(choice => Math.Abs(choice.Start - ToCoordinateUnits(original.Min)))
                    .ThenBy(choice => choice.Start)
                    .FirstOrDefault();
                var hasAvailablePlacement = placementChoices.Any(choice =>
                    choice.Start == availablePlacement.Start && choice.Width == availablePlacement.Width &&
                    choice.Touch == availablePlacement.Touch) &&
                    !OverlapsPlacedZones(unit, placedUnits, direction,
                        availablePlacement.Start / (double)GeometryScale,
                        (availablePlacement.Start + availablePlacement.Width) / (double)GeometryScale) &&
                    !IntersectsOpeningAt(unit, openings, direction,
                        availablePlacement.Start / (double)GeometryScale,
                        (availablePlacement.Start + availablePlacement.Width) / (double)GeometryScale);
                if (!hasAvailablePlacement && unitStepUnits > 0 &&
                    HasSufficientCapacityForAssignedElements(unit, plates, settings))
                {
                    var minimumWidthUnits = unit.Members.Max(member =>
                        MinimumWidthUnits(member.Candidate, minimumWidthM));
                    var reducedChoices = new List<(long Start, long Width, bool Touch)>();
                    for (var reducedWidth = widthUnits - unitStepUnits;
                         reducedWidth >= minimumWidthUnits;
                         reducedWidth -= unitStepUnits)
                    {
                        if (unit.Members.Any(member => reducedWidth %
                                ((long)Math.Max(1, member.Candidate.BarStepMm) * CoordinateUnitsPerMm) != 0))
                            continue;

                        var reducedMaxStart = allowedEndUnits - reducedWidth;
                        if (reducedMaxStart < allowedStartUnits) continue;
                        var reducedStarts = new HashSet<long>
                        {
                            Math.Max(allowedStartUnits, Math.Min(reducedMaxStart,
                                ToCoordinateUnits(original.Min))),
                            allowedStartUnits,
                            reducedMaxStart,
                            requiredCrossMin,
                            requiredCrossMax - reducedWidth
                        };
                        foreach (var blockerUnit in placedUnits)
                        foreach (var blocker in blockerUnit.Members)
                        {
                            if (!unit.Members.Any(member => AxiallyOverlap(member, blocker)) ||
                                unit.Members.Any(member => IsIntentionalLapPair(
                                    member.Candidate, blocker.Candidate))) continue;
                            var blockedCross = CrossInterval(blocker.Candidate.Contour, direction);
                            reducedStarts.Add(ToCoordinateUnits(blockedCross.Min) - reducedWidth);
                            reducedStarts.Add(ToCoordinateUnits(blockedCross.Max));
                        }

                        foreach (var start in reducedStarts.Where(start =>
                                     start >= allowedStartUnits && start <= reducedMaxStart))
                        {
                            var touchesPlaced = placedUnits.SelectMany(candidate => candidate.Members)
                                .Where(blocker => unit.Members.Any(member => AxiallyOverlap(member, blocker)) &&
                                    !unit.Members.Any(member => IsIntentionalLapPair(
                                        member.Candidate, blocker.Candidate)))
                                .Select(blocker => CrossInterval(blocker.Candidate.Contour, direction))
                                .Any(blocked =>
                                    Math.Abs(start + reducedWidth - ToCoordinateUnits(blocked.Min)) <= 1 ||
                                    Math.Abs(start - ToCoordinateUnits(blocked.Max)) <= 1);
                            reducedChoices.Add((start, reducedWidth, touchesPlaced));
                        }
                    }

                    var reducedPlacement = reducedChoices
                        .Where(choice => !OverlapsPlacedZones(unit, placedUnits, direction,
                            choice.Start / (double)GeometryScale,
                            (choice.Start + choice.Width) / (double)GeometryScale))
                        .Where(choice => !IntersectsOpeningAt(unit, openings, direction,
                            choice.Start / (double)GeometryScale,
                            (choice.Start + choice.Width) / (double)GeometryScale))
                        .Where(choice => PreservesAssignedElementCoverage(
                            unit, placedUnits, choice.Start, choice.Width, unit.Members,
                            units.Where(candidate => !ReferenceEquals(candidate, unit))
                                .SelectMany(candidate => candidate.Members)
                                .Select(member => member.Candidate),
                            plates, settings, slabOutline, openings, originalUncoveredIds))
                        .OrderByDescending(choice => choice.Width)
                        .ThenByDescending(choice => choice.Touch)
                        .ThenBy(choice => Math.Abs(choice.Start - ToCoordinateUnits(original.Min)))
                        .ThenBy(choice => choice.Start)
                        .FirstOrDefault();
                    if (reducedChoices.Any(choice => choice.Start == reducedPlacement.Start &&
                            choice.Width == reducedPlacement.Width && choice.Touch == reducedPlacement.Touch) &&
                        !OverlapsPlacedZones(unit, placedUnits, direction,
                            reducedPlacement.Start / (double)GeometryScale,
                            (reducedPlacement.Start + reducedPlacement.Width) / (double)GeometryScale) &&
                        !IntersectsOpeningAt(unit, openings, direction,
                            reducedPlacement.Start / (double)GeometryScale,
                            (reducedPlacement.Start + reducedPlacement.Width) / (double)GeometryScale))
                    {
                        availablePlacement = reducedPlacement;
                        hasAvailablePlacement = true;
                        foreach (var member in unit.Members) reducedWidthEntries.Add(member);
                    }
                }
                if (!hasAvailablePlacement && TryReducePlacedNeighborForUnit(
                        unit, placedUnits, direction, widthUnits,
                        allowedStartUnits, allowedEndUnits, original.Min,
                        requiredCrossMin, requiredCrossMax, openings, plates, settings,
                        minimumWidthM, slabOutline, originalUncoveredIds,
                        units, reducedWidthEntries, out var neighborPlacement))
                {
                    availablePlacement = neighborPlacement;
                    hasAvailablePlacement = true;
                }
                if (!hasAvailablePlacement)
                {
                    var merged = false;
                    var absorptionReasons = new List<string>();
                    var blockingUnits = new HashSet<ZoneMoveUnit>(placedUnits.Where(blocker =>
                        UnitsOverlap(unit, blocker)));
                    var expandedComponent = true;
                    while (expandedComponent)
                    {
                        expandedComponent = false;
                        foreach (var candidate in placedUnits)
                        {
                            if (blockingUnits.Contains(candidate) ||
                                !blockingUnits.Any(blocker => UnitsOverlap(blocker, candidate)))
                                continue;
                            blockingUnits.Add(candidate);
                            expandedComponent = true;
                        }
                    }

                    if (blockingUnits.Count > 1)
                    {
                        var mergeEntries = unit.Members.Concat(
                            blockingUnits.SelectMany(blocker => blocker.Members)).ToList();
                        if (!TryAbsorbConflictGroup(mergeEntries, settings, openings,
                                minimumWidthM, maxPatchOverrunM,
                                out var retained, out var mergedZone, out var groupFailure))
                            absorptionReasons.Add(groupFailure);
                        else if (!EnclosesAxialExtents(mergedZone, mergeEntries))
                            absorptionReasons.Add("объединение сокращает анкеровочный габарит");
                        else if (mergedZone.AsCoveredCm2PerM + DemandTolerance <
                                 mergeEntries.Max(entry => entry.Candidate.AsAdditional))
                            absorptionReasons.Add("объединённая зона не обеспечивает максимальную расчётную As");
                        else
                        {
                            var mergedEntry = new ZoneEntry
                            {
                                Original = retained.Original,
                                Candidate = mergedZone,
                                Support = UnionBounds(mergeEntries, usePatchBounds: false),
                                Patch = UnionBounds(mergeEntries, usePatchBounds: true),
                                OriginalCross = CrossInterval(mergedZone.Contour, direction),
                                SupportCross = CrossInterval(
                                    UnionBounds(mergeEntries, usePatchBounds: false), direction),
                                AnchorageAxial = (
                                    mergeEntries.Min(entry => entry.AnchorageAxial.Min),
                                    mergeEntries.Max(entry => entry.AnchorageAxial.Max))
                            };
                            var mergedUnit = new ZoneMoveUnit();
                            mergedUnit.Members.Add(mergedEntry);
                            var otherPlaced = placedUnits.Where(candidate =>
                                !blockingUnits.Contains(candidate)).ToList();
                            var mergedCross = CrossInterval(mergedZone.Contour, direction);
                            if (PreservesAnchorageExtent(mergedEntry) &&
                                !OverlapsPlacedZones(mergedUnit, otherPlaced, direction,
                                    mergedCross.Min, mergedCross.Max))
                            {
                                var keeperUnit = blockingUnits.First();
                                keeperUnit.Members.Clear();
                                keeperUnit.Members.Add(mergedEntry);
                                foreach (var absorbedUnit in blockingUnits.Where(candidate =>
                                             !ReferenceEquals(candidate, keeperUnit)))
                                {
                                    absorbedUnit.Members.Clear();
                                    placedUnits.Remove(absorbedUnit);
                                }
                                unit.Members.Clear();
                                merged = true;
                            }
                            else
                                absorptionReasons.Add("после объединения остаётся пересечение с размещённой зоной");
                        }
                    }

                    foreach (var blockerUnit in placedUnits.Where(blocker =>
                                 UnitsOverlap(unit, blocker)).ToList())
                    {
                        if (merged) break;
                        var mergeEntries = unit.Members.Concat(blockerUnit.Members).ToList();
                        if (!TryAbsorbConflictGroup(mergeEntries, settings, openings,
                                minimumWidthM, maxPatchOverrunM,
                                out var retained, out var mergedZone, out var pairFailure))
                        {
                            if (!string.IsNullOrWhiteSpace(pairFailure)) absorptionReasons.Add(pairFailure);
                            continue;
                        }
                        if (!EnclosesAxialExtents(mergedZone, mergeEntries) ||
                            mergedZone.AsCoveredCm2PerM + DemandTolerance <
                            mergeEntries.Max(entry => entry.Candidate.AsAdditional))
                            continue;

                        var mergedEntry = new ZoneEntry
                        {
                            Original = retained.Original,
                            Candidate = mergedZone,
                            Support = UnionBounds(mergeEntries, usePatchBounds: false),
                            Patch = UnionBounds(mergeEntries, usePatchBounds: true),
                            OriginalCross = CrossInterval(mergedZone.Contour, direction),
                            SupportCross = CrossInterval(
                                UnionBounds(mergeEntries, usePatchBounds: false), direction),
                            AnchorageAxial = (
                                mergeEntries.Min(entry => entry.AnchorageAxial.Min),
                                mergeEntries.Max(entry => entry.AnchorageAxial.Max))
                        };
                        if (!PreservesAnchorageExtent(mergedEntry)) continue;
                        var mergedUnit = new ZoneMoveUnit();
                        mergedUnit.Members.Add(mergedEntry);
                        var otherPlaced = placedUnits.Where(candidate =>
                            !ReferenceEquals(candidate, blockerUnit)).ToList();
                        var mergedCross = CrossInterval(mergedZone.Contour, direction);
                        if (OverlapsPlacedZones(mergedUnit, otherPlaced, direction,
                                mergedCross.Min, mergedCross.Max))
                            continue;

                        blockerUnit.Members.Clear();
                        blockerUnit.Members.Add(mergedEntry);
                        units.RemoveAt(unitIndex--);
                        merged = true;
                        break;
                    }
                    if (merged) continue;

                    if (TryFindUnboundedPlacement(
                            unit, placedUnits, units, plates, settings, slabOutline, openings,
                            originalUncoveredIds, direction, widthUnits,
                            requiredCrossMin, requiredCrossMax, searchMin, searchMax,
                            out var unboundedPlacement))
                    {
                        var relocatedInterval = (
                            unboundedPlacement.Start / (double)GeometryScale,
                            (unboundedPlacement.Start + unboundedPlacement.Width) / (double)GeometryScale);
                        foreach (var member in unit.Members)
                            SetCrossInterval(member.Candidate, relocatedInterval);
                        placedUnits.Add(unit);
                        continue;
                    }

                    var zone = unit.Members[0].Candidate;
                    var supportCross = (
                        Min: unit.Members.Min(member => member.SupportCross.Min),
                        Max: unit.Members.Max(member => member.SupportCross.Max));
                    var blockerIds = string.Join(",", blockingUnits.SelectMany(blocker => blocker.Members)
                        .Select(member => member.Candidate.ZoneId).Distinct().OrderBy(id => id));
                    warning = $"В слое {unit.Members[0].Candidate.Layer} для зоны " +
                              $"№{zone.ZoneId} Ø{zone.DiameterMm}/{zone.BarStepMm} " +
                              $"не найдено положение (опора {supportCross.Min:0.###}–{supportCross.Max:0.###} м; " +
                              $"конфликтующие зоны: {(string.IsNullOrEmpty(blockerIds) ? "нет" : blockerIds)}). " +
                              string.Join("; ", absorptionReasons.Distinct());
                    return false;
                }

                var interval = (availablePlacement.Start / (double)GeometryScale,
                    (availablePlacement.Start + availablePlacement.Width) / (double)GeometryScale);
                foreach (var member in unit.Members)
                    SetCrossInterval(member.Candidate, interval);
                placedUnits.Add(unit);
            }

            var originalOrder = entries.Select((entry, index) => new { entry.Original, index })
                .ToDictionary(item => item.Original, item => item.index);
            var arrangedZones = units.SelectMany(unit => unit.Members)
                .OrderBy(entry => originalOrder.TryGetValue(entry.Original, out var index)
                    ? index : int.MaxValue)
                .Select(entry => entry.Candidate).ToList();
            var remainingConflicts = CountArrangementConflicts(arrangedZones);
            var badWidths = arrangedZones.Any(zone => !HasStepSizedWidth(zone));
            var overlong = arrangedZones.Any(RebarTables.ExceedsMaxBarLength);
            var finalCoverage = ZoneLayoutDiagnostics.Evaluate(
                plates, arrangedZones, settings, 0, true, slabOutline, openings);
            var newlyUncovered = finalCoverage.Issues.Any(issue =>
                issue.Kind == ZoneIssueKind.UncoveredElement &&
                !originalUncoveredIds.Contains(issue.ElementId));
            var newlyUncoveredIds = finalCoverage.Issues
                .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement &&
                                !originalUncoveredIds.Contains(issue.ElementId))
                .Select(issue => issue.ElementId).Distinct().OrderBy(id => id).ToList();
            var anchorageReduced = units.SelectMany(unit => unit.Members)
                .Any(entry => !PreservesAnchorageExtent(entry));
            if (remainingConflicts > 0 || badWidths || overlong || newlyUncovered || anchorageReduced ||
                finalCoverage.UncoveredCount > originalCoverage.UncoveredCount)
            {
                warning = $"Послойная однопроходная раскладка отклонена: пересечений={remainingConflicts}, " +
                          $"непокрытых КЭ было/стало={originalCoverage.UncoveredCount}/{finalCoverage.UncoveredCount}, " +
                          $"ширина_не_кратна={badWidths}, длина_свыше_11700={overlong}, " +
                          $"уменьшена_анкеровка={anchorageReduced}. Исходные зоны слоя сохранены.";
                if (newlyUncoveredIds.Count > 0)
                    warning += " Новые непокрытые КЭ: " + string.Join(",", newlyUncoveredIds.Take(12)) + ".";
                if (reducedWidthEntries.Count > 0)
                    warning += " Сужались зоны: " + string.Join(",", reducedWidthEntries
                        .Select(entry => entry.Candidate.ZoneId).Distinct().OrderBy(id => id).Take(12)) + ".";
                return false;
            }

            zones.Clear();
            foreach (var entry in reducedWidthEntries)
            {
                const string note = "ширина уменьшена на шаг для устранения пересечения; покрытие и несущая способность КЭ проверены";
                entry.Candidate.Comment = string.IsNullOrWhiteSpace(entry.Candidate.Comment)
                    ? note
                    : entry.Candidate.Comment + "; " + note;
            }
            foreach (var zone in arrangedZones) zones.Add(zone);
            return true;
        }

        private static bool TryFindUnboundedPlacement(
            ZoneMoveUnit unit,
            IList<ZoneMoveUnit> placedUnits,
            IList<ZoneMoveUnit> allUnits,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            ISet<int> originallyUncoveredIds,
            ZoneDirection direction,
            long widthUnits,
            long requiredCrossMin,
            long requiredCrossMax,
            double searchMin,
            double searchMax,
            out (long Start, long Width, bool Touch) placement)
        {
            placement = default;
            var startMin = ToCoordinateUnits(searchMin);
            var endMax = ToCoordinateUnits(searchMax);
            var maxStart = endMax - widthUnits;
            if (maxStart < startMin) return false;

            var original = UnitCrossInterval(unit, direction);
            var originalStart = ToCoordinateUnits(original.Min);
            var starts = new HashSet<long>
            {
                startMin,
                maxStart,
                Math.Max(startMin, Math.Min(maxStart, originalStart)),
                Math.Max(startMin, Math.Min(maxStart, requiredCrossMin)),
                Math.Max(startMin, Math.Min(maxStart, requiredCrossMax - widthUnits))
            };
            foreach (var blockerUnit in placedUnits)
            foreach (var blocker in blockerUnit.Members)
            {
                if (!unit.Members.Any(member => AxiallyOverlap(member, blocker)) ||
                    unit.Members.Any(member => IsIntentionalLapPair(
                        member.Candidate, blocker.Candidate))) continue;
                var blocked = CrossInterval(blocker.Candidate.Contour, direction);
                starts.Add(ToCoordinateUnits(blocked.Min) - widthUnits);
                starts.Add(ToCoordinateUnits(blocked.Max));
            }

            var fallbackZones = allUnits.Where(candidate => !ReferenceEquals(candidate, unit))
                .SelectMany(candidate => candidate.Members)
                .Select(member => member.Candidate)
                .ToList();
            var choices = starts.Where(start => start >= startMin && start <= maxStart)
                .Where(start => !OverlapsPlacedZones(unit, placedUnits, direction,
                    start / (double)GeometryScale,
                    (start + widthUnits) / (double)GeometryScale))
                .Where(start => !IntersectsOpeningAt(unit, openings, direction,
                    start / (double)GeometryScale,
                    (start + widthUnits) / (double)GeometryScale))
                .Where(start => PreservesAssignedElementCoverage(
                    unit, placedUnits, start, widthUnits, unit.Members, fallbackZones,
                    plates, settings, slabOutline, openings, originallyUncoveredIds))
                .OrderBy(start => Math.Abs(start - originalStart))
                .ThenBy(start => start)
                .Cast<long?>()
                .FirstOrDefault();
            if (!choices.HasValue) return false;
            placement = (choices.Value, widthUnits, false);
            return true;
        }

        private static bool HasSufficientCapacityForAssignedElements(
            ZoneMoveUnit unit, IList<LiraPlateElement> plates, AnalysisSettings settings)
        {
            foreach (var member in unit.Members)
                if (!HasSufficientCapacityForAssignedElements(member, plates, settings)) return false;
            return true;
        }

        private static bool TryReducePlacedNeighborForUnit(
            ZoneMoveUnit unit,
            IList<ZoneMoveUnit> placedUnits,
            ZoneDirection direction,
            long widthUnits,
            long allowedStartUnits,
            long allowedEndUnits,
            double preferredStart,
            long requiredCrossMin,
            long requiredCrossMax,
            IList<OpeningInfo>? openings,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            double minimumWidthM,
            IList<Point3>? slabOutline,
            ISet<int> originallyUncoveredIds,
            IList<ZoneMoveUnit> allUnits,
            ISet<ZoneEntry> reducedWidthEntries,
            out (long Start, long Width, bool Touch) placement)
        {
            placement = default;
            var unitCross = UnitCrossInterval(unit, direction);
            var unitCenter = (unitCross.Min + unitCross.Max) * 0.5;
            var currentStartUnits = ToCoordinateUnits(preferredStart);
            var maxCurrentStart = allowedEndUnits - widthUnits;
            if (maxCurrentStart < allowedStartUnits) return false;

            foreach (var blocker in placedUnits.Where(candidate => UnitsOverlap(unit, candidate)))
            {
                if (blocker.Members.Any(member => member.Candidate.Direction != direction) ||
                    !HasSufficientCapacityForAssignedElements(blocker, plates, settings))
                    continue;

                var blockerCross = UnitCrossInterval(blocker, direction);
                var blockerWidth = ToCoordinateUnits(blockerCross.Max - blockerCross.Min);
                var stepMm = blocker.Members.Select(member => member.Candidate.BarStepMm)
                    .Aggregate(1, LeastCommonMultiple);
                if (stepMm <= 0) continue;
                var stepUnits = (long)stepMm * CoordinateUnitsPerMm;
                var minimumWidth = blocker.Members.Max(member =>
                    MinimumWidthUnits(member.Candidate, minimumWidthM));
                var shrinkFromMinFirst = unitCenter >= (blockerCross.Min + blockerCross.Max) * 0.5;

                for (var reducedWidth = blockerWidth - stepUnits;
                     reducedWidth >= minimumWidth;
                     reducedWidth -= stepUnits)
                {
                    var intervals = shrinkFromMinFirst
                        ? new[]
                        {
                            (Min: ToCoordinateUnits(blockerCross.Min),
                             Max: ToCoordinateUnits(blockerCross.Min) + reducedWidth),
                            (Min: ToCoordinateUnits(blockerCross.Max) - reducedWidth,
                             Max: ToCoordinateUnits(blockerCross.Max))
                        }
                        : new[]
                        {
                            (Min: ToCoordinateUnits(blockerCross.Max) - reducedWidth,
                             Max: ToCoordinateUnits(blockerCross.Max)),
                            (Min: ToCoordinateUnits(blockerCross.Min),
                             Max: ToCoordinateUnits(blockerCross.Min) + reducedWidth)
                        };

                    foreach (var blockerInterval in intervals)
                    {
                        var trialMembers = blocker.Members.Select(member => new ZoneEntry
                        {
                            Original = member.Original,
                            Candidate = CopyZone(member.Candidate),
                            Support = member.Support,
                            Patch = member.Patch,
                            OriginalCross = member.OriginalCross,
                            SupportCross = member.SupportCross,
                            AnchorageAxial = member.AnchorageAxial
                        }).ToList();
                        foreach (var member in trialMembers)
                            SetCrossInterval(member.Candidate,
                                (blockerInterval.Min / (double)GeometryScale,
                                 blockerInterval.Max / (double)GeometryScale));
                        var trialUnit = new ZoneMoveUnit();
                        trialUnit.Members.AddRange(trialMembers);

                        var otherPlaced = placedUnits.Where(candidate =>
                            !ReferenceEquals(candidate, blocker)).ToList();
                        if (OverlapsPlacedZones(trialUnit, otherPlaced, direction,
                                blockerInterval.Min / (double)GeometryScale,
                                blockerInterval.Max / (double)GeometryScale))
                            continue;

                        var testPlaced = otherPlaced.Concat(new[] { trialUnit }).ToList();
                        var starts = new HashSet<long>
                        {
                            Math.Max(allowedStartUnits, Math.Min(maxCurrentStart, currentStartUnits)),
                            allowedStartUnits,
                            maxCurrentStart,
                            requiredCrossMin,
                            requiredCrossMax - widthUnits
                        };
                        foreach (var placed in testPlaced)
                        foreach (var other in placed.Members)
                        {
                            if (!unit.Members.Any(member => AxiallyOverlap(member, other))) continue;
                            var cross = CrossInterval(other.Candidate.Contour, direction);
                            starts.Add(ToCoordinateUnits(cross.Min) - widthUnits);
                            starts.Add(ToCoordinateUnits(cross.Max));
                        }

                        var candidateStart = starts
                            .Where(start => start >= allowedStartUnits && start <= maxCurrentStart)
                            .Where(start => !OverlapsPlacedZones(unit, testPlaced, direction,
                                start / (double)GeometryScale,
                                (start + widthUnits) / (double)GeometryScale))
                            .Where(start => !IntersectsOpeningAt(unit, openings, direction,
                                start / (double)GeometryScale,
                                (start + widthUnits) / (double)GeometryScale))
                            .Where(start => PreservesAssignedElementCoverage(
                                unit, testPlaced, start, widthUnits,
                                blocker.Members.Concat(unit.Members),
                                allUnits.Where(candidate => !ReferenceEquals(candidate, unit) &&
                                        !ReferenceEquals(candidate, blocker))
                                    .SelectMany(candidate => candidate.Members)
                                    .Select(member => member.Candidate),
                                plates, settings,
                                slabOutline, openings, originallyUncoveredIds))
                            .OrderByDescending(start =>
                                Math.Abs(start + widthUnits - blockerInterval.Min) <= 1 ||
                                Math.Abs(start - blockerInterval.Max) <= 1)
                            .ThenBy(start => Math.Abs(start - currentStartUnits))
                            .ThenBy(start => start)
                            .Cast<long?>()
                            .FirstOrDefault();
                        if (!candidateStart.HasValue) continue;

                        for (var i = 0; i < blocker.Members.Count; i++)
                        {
                            SetCrossInterval(blocker.Members[i].Candidate,
                                (blockerInterval.Min / (double)GeometryScale,
                                 blockerInterval.Max / (double)GeometryScale));
                            reducedWidthEntries.Add(blocker.Members[i]);
                        }

                        var chosenStart = candidateStart.Value;
                        placement = (chosenStart, widthUnits,
                            Math.Abs(chosenStart + widthUnits - blockerInterval.Min) <= 1 ||
                            Math.Abs(chosenStart - blockerInterval.Max) <= 1);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasSufficientCapacityForAssignedElements(
            ZoneEntry entry, IList<LiraPlateElement> plates, AnalysisSettings settings)
        {
            var zone = entry.Candidate;
            var assignedIds = new HashSet<int>(zone.NodeIds ?? new List<int>());
            var maximumDemand = plates
                .Where(plate => plate.Rebar.Ok && (assignedIds.Count > 0
                    ? assignedIds.Contains(plate.Id)
                    : plate.Centroid.X >= entry.Support.MinX - GeometryToleranceM &&
                      plate.Centroid.X <= entry.Support.MaxX + GeometryToleranceM &&
                      plate.Centroid.Y >= entry.Support.MinY - GeometryToleranceM &&
                      plate.Centroid.Y <= entry.Support.MaxY + GeometryToleranceM))
                .Select(plate => plate.Rebar.Get(zone.Layer) - Background(settings, zone.Layer))
                .Where(demand => demand > DemandTolerance)
                .DefaultIfEmpty(0)
                .Max();
            return zone.AsCoveredCm2PerM + DemandTolerance >= maximumDemand;
        }

        private static bool PreservesAssignedElementCoverage(
            ZoneMoveUnit unit,
            IList<ZoneMoveUnit> placedUnits,
            long crossStartUnits,
            long widthUnits,
            IEnumerable<ZoneEntry> requiredEntries,
            IEnumerable<AdditionalZone> fallbackZones,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            ISet<int> originallyUncoveredIds)
        {
            var candidateUnit = new ZoneMoveUnit();
            foreach (var member in unit.Members)
            {
                var candidate = CopyZone(member.Candidate);
                SetCrossInterval(candidate, (
                    crossStartUnits / (double)GeometryScale,
                    (crossStartUnits + widthUnits) / (double)GeometryScale));
                var entry = new ZoneEntry { Candidate = candidate };
                candidateUnit.Members.Add(entry);
            }

            var requiredIds = new HashSet<int>(requiredEntries
                .SelectMany(entry => entry.Candidate.NodeIds ?? new List<int>()));
            if (requiredIds.Count == 0) return true;

            var coverageZones = placedUnits.SelectMany(placed => placed.Members)
                .Select(member => member.Candidate)
                .Concat(fallbackZones)
                .Concat(candidateUnit.Members.Select(member => member.Candidate))
                .ToList();
            var layer = unit.Members[0].Candidate.Layer;
            foreach (var plate in plates)
            {
                if (!requiredIds.Contains(plate.Id) || originallyUncoveredIds.Contains(plate.Id) ||
                    !plate.Rebar.Ok)
                    continue;
                var requiredAs = plate.Rebar.Get(layer) - Background(settings, layer);
                if (requiredAs <= DemandTolerance) continue;
                if (!ZoneCoverageRules.CoversOrBridgesGap(
                        coverageZones, plate, layer, requiredAs, slabOutline, openings))
                    return false;
            }

            return true;
        }

        private static List<ZoneMoveUnit> BuildMoveUnits(IList<ZoneEntry> entries)
        {
            var pending = new HashSet<ZoneEntry>(entries);
            var units = new List<ZoneMoveUnit>();
            while (pending.Count > 0)
            {
                var seed = pending.First();
                pending.Remove(seed);
                var unit = new ZoneMoveUnit();
                unit.Members.Add(seed);
                var frontier = new Queue<ZoneEntry>();
                frontier.Enqueue(seed);
                while (frontier.Count > 0)
                {
                    var current = frontier.Dequeue();
                    foreach (var neighbor in pending.ToList())
                    {
                        if (!IsIntentionalLapPair(current.Candidate, neighbor.Candidate)) continue;
                        pending.Remove(neighbor);
                        unit.Members.Add(neighbor);
                        frontier.Enqueue(neighbor);
                    }
                }
                units.Add(unit);
            }
            return units;
        }

        private static List<ZoneMoveUnit> OrderUnitsFromPeakThenLeftThenRight(
            IList<ZoneMoveUnit> units, IList<LiraPlateElement> plates, AnalysisSettings settings)
        {
            if (units.Count <= 1) return units.ToList();

            var layer = units[0].Members[0].Candidate.Layer;
            var plateById = plates.GroupBy(plate => plate.Id)
                .ToDictionary(group => group.Key, group => group.First());
            var peakByUnit = units.ToDictionary(unit => unit, unit =>
            {
                var nodePeak = unit.Members
                    .SelectMany(entry => entry.Candidate.NodeIds ?? new List<int>())
                    .Distinct()
                    .Where(plateById.ContainsKey)
                    .Select(id => plateById[id])
                    .Where(plate => plate.Rebar.Ok)
                    .Select(plate => plate.Rebar.Get(layer) - Background(settings, layer))
                    .DefaultIfEmpty(0)
                    .Max();
                if (nodePeak > DemandTolerance) return nodePeak;

                var supportPeak = unit.Members
                    .SelectMany(entry => plates.Where(plate => plate.Rebar.Ok &&
                        plate.Centroid.X >= entry.Support.MinX - GeometryToleranceM &&
                        plate.Centroid.X <= entry.Support.MaxX + GeometryToleranceM &&
                        plate.Centroid.Y >= entry.Support.MinY - GeometryToleranceM &&
                        plate.Centroid.Y <= entry.Support.MaxY + GeometryToleranceM))
                    .Select(plate => plate.Rebar.Get(layer) - Background(settings, layer))
                    .DefaultIfEmpty(0)
                    .Max();
                return supportPeak > DemandTolerance
                    ? supportPeak
                    : unit.Members.Max(entry => entry.Candidate.AsAdditional);
            });

            var seed = units
                .OrderByDescending(unit => peakByUnit[unit])
                .ThenByDescending(unit => unit.Members.Max(entry =>
                    entry.Candidate.WidthMm * entry.Candidate.LengthMm))
                .ThenBy(unit => unit.Members.Min(entry => entry.Candidate.ZoneId))
                .First();
            var direction = seed.Members[0].Candidate.Direction;
            var seedCross = CrossInterval(seed.Members[0].Candidate.Contour, direction);
            var seedCenter = (seedCross.Min + seedCross.Max) * 0.5;
            var remaining = units.Where(unit => !ReferenceEquals(unit, seed)).ToList();
            var left = remaining
                .Where(unit => UnitCrossCenter(unit, direction) < seedCenter - GeometryToleranceM)
                .OrderByDescending(unit => UnitCrossInterval(unit, direction).Max)
                .ThenByDescending(unit => peakByUnit[unit])
                .ThenBy(unit => unit.Members.Min(entry => entry.Candidate.ZoneId));
            var right = remaining
                .Where(unit => UnitCrossCenter(unit, direction) >= seedCenter - GeometryToleranceM)
                .OrderBy(unit => UnitCrossInterval(unit, direction).Min)
                .ThenByDescending(unit => peakByUnit[unit])
                .ThenBy(unit => unit.Members.Min(entry => entry.Candidate.ZoneId));

            return new[] { seed }.Concat(left).Concat(right).ToList();
        }

        private static (double Min, double Max) UnitCrossInterval(
            ZoneMoveUnit unit, ZoneDirection direction)
        {
            var intervals = unit.Members.Select(entry =>
                CrossInterval(entry.Candidate.Contour, direction)).ToList();
            return (intervals.Min(interval => interval.Min), intervals.Max(interval => interval.Max));
        }

        private static double UnitCrossCenter(ZoneMoveUnit unit, ZoneDirection direction)
        {
            var interval = UnitCrossInterval(unit, direction);
            return (interval.Min + interval.Max) * 0.5;
        }

        private static void NormalizeLayerWidths(
            IList<ZoneEntry> entries, double minimumWidthM, double maxPatchOverrunM)
        {
            foreach (var entry in entries)
            {
                var zone = entry.Candidate;
                if (!IsRectangle(zone) || zone.BarStepMm <= 0) continue;

                var support = entry.SupportCross;
                var stepUnits = (long)zone.BarStepMm * CoordinateUnitsPerMm;
                var requiredWidth = Math.Max(
                    ToCoordinateUnits(support.Max - support.Min),
                    MinimumWidthUnits(zone, minimumWidthM));
                var normalizedWidth = ((requiredWidth + stepUnits - 1) / stepUnits) * stepUnits;
                var patch = CrossInterval(entry.Patch, zone.Direction);
                var overrunUnits = ToCoordinateUnits(Math.Max(0, maxPatchOverrunM));
                var allowedMin = ToCoordinateUnits(patch.Min) - overrunUnits;
                var allowedMax = ToCoordinateUnits(patch.Max) + overrunUnits;
                var preferredStart = (ToCoordinateUnits(support.Min) +
                                      ToCoordinateUnits(support.Max) - normalizedWidth) / 2;
                var normalized = PlaceContainingSupportUnits(
                    ToCoordinateUnits(support.Min), ToCoordinateUnits(support.Max),
                    normalizedWidth, preferredStart, allowedMin, allowedMax);
                SetCrossInterval(zone, (normalized.Min / (double)GeometryScale,
                    normalized.Max / (double)GeometryScale));
                entry.OriginalCross = CrossInterval(zone.Contour, zone.Direction);
            }
        }

        private static bool OverlapsPlacedZones(
            ZoneMoveUnit unit, IList<ZoneMoveUnit> placed, ZoneDirection direction,
            double crossMin, double crossMax)
        {
            foreach (var member in unit.Members)
            foreach (var blocker in placed.SelectMany(candidate => candidate.Members))
            {
                if (!AxiallyOverlap(member, blocker) ||
                    IsIntentionalLapPair(member.Candidate, blocker.Candidate)) continue;
                if (CrossBandsOverlap((crossMin, crossMax),
                    CrossInterval(blocker.Candidate.Contour, direction))) return true;
            }
            return false;
        }

        private static bool UnitsOverlap(ZoneMoveUnit first, ZoneMoveUnit second)
        {
            foreach (var firstMember in first.Members)
            foreach (var secondMember in second.Members)
            {
                if (IsIntentionalLapPair(firstMember.Candidate, secondMember.Candidate) ||
                    !AxiallyOverlap(firstMember, secondMember)) continue;
                if (CrossBandsOverlap(
                    CrossInterval(firstMember.Candidate.Contour, firstMember.Candidate.Direction),
                    CrossInterval(secondMember.Candidate.Contour, secondMember.Candidate.Direction)))
                    return true;
            }
            return false;
        }

        private static bool EnclosesAxialExtents(
            AdditionalZone zone, IList<ZoneEntry> sourceEntries)
        {
            var axial = AxialInterval(zone.Contour, zone.Direction);
            return sourceEntries.All(entry =>
            {
                var source = AxialInterval(entry.Candidate.Contour, zone.Direction);
                return axial.Min <= source.Min + GeometryToleranceM &&
                       axial.Max >= source.Max - GeometryToleranceM;
            });
        }

        private static bool PreservesAnchorageExtent(ZoneEntry entry)
        {
            var axial = AxialInterval(entry.Candidate.Contour, entry.Candidate.Direction);
            return axial.Min <= entry.AnchorageAxial.Min + GeometryToleranceM &&
                   axial.Max >= entry.AnchorageAxial.Max - GeometryToleranceM;
        }

        private static bool IntersectsOpeningAt(
            ZoneMoveUnit unit, IList<OpeningInfo>? openings, ZoneDirection direction,
            double crossMin, double crossMax)
        {
            if (openings == null || openings.Count == 0) return false;
            foreach (var member in unit.Members)
            {
                var probe = CopyZone(member.Candidate);
                SetCrossInterval(probe, (crossMin, crossMax));
                if (ZoneEditor.IntersectsOpening(probe, openings)) return true;
            }
            return false;
        }

        private static void NormalizeCandidateWidths(
            IList<ZoneEntry> entries, double minimumWidthM, double maxPatchOverrunM)
        {
            foreach (var entry in entries)
            {
                var zone = entry.Candidate;
                if (!IsRectangle(zone) || zone.BarStepMm <= 0) continue;

                var current = CrossInterval(zone.Contour, zone.Direction);
                var support = entry.SupportCross;
                var currentWidth = ToCoordinateUnits(current.Max - current.Min);
                var supportWidth = ToCoordinateUnits(support.Max - support.Min);
                var stepUnits = (long)zone.BarStepMm * CoordinateUnitsPerMm;
                var requiredWidth = Math.Max(Math.Max(currentWidth, supportWidth),
                    MinimumWidthUnits(zone, minimumWidthM));
                var normalizedWidth = ((requiredWidth + stepUnits - 1) / stepUnits) * stepUnits;

                var patch = CrossInterval(entry.Patch, zone.Direction);
                var overrunUnits = ToCoordinateUnits(Math.Max(0, maxPatchOverrunM));
                var allowedMin = ToCoordinateUnits(patch.Min) - overrunUnits;
                var allowedMax = ToCoordinateUnits(patch.Max) + overrunUnits;
                var preferredStart = ToCoordinateUnits(current.Min) -
                                     (normalizedWidth - currentWidth) / 2;
                var normalized = PlaceContainingSupportUnits(
                    ToCoordinateUnits(support.Min), ToCoordinateUnits(support.Max),
                    normalizedWidth, preferredStart, allowedMin, allowedMax);

                SetCrossInterval(zone, (normalized.Min / (double)GeometryScale,
                    normalized.Max / (double)GeometryScale));
                entry.OriginalCross = CrossInterval(zone.Contour, zone.Direction);
            }
        }

        private static (long Min, long Max) PlaceContainingSupportUnits(
            long supportMin, long supportMax, long width,
            long preferredStart, long allowedMin, long allowedMax)
        {
            var minimumStart = Math.Max(supportMax - width, allowedMin);
            var maximumStart = Math.Min(supportMin, allowedMax - width);
            if (minimumStart > maximumStart)
            {
                minimumStart = supportMax - width;
                maximumStart = supportMin;
            }

            var start = Math.Max(minimumStart, Math.Min(maximumStart, preferredStart));
            return (start, start + width);
        }

        private static bool TryBuildConflictFallback(
            IList<ZoneEntry> entries,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            int maximumUncoveredCount,
            out List<AdditionalZone> fallbackZones,
            out string warning)
        {
            fallbackZones = new List<AdditionalZone>(entries.Count);
            var sources = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
            var patches = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
            foreach (var entry in entries)
            {
                var zone = CopyZone(entry.Original);
                fallbackZones.Add(zone);
                sources[zone] = entry.Support;
                patches[zone] = entry.Patch;
            }

            if (CountArrangementConflicts(fallbackZones) == 0)
            {
                warning = "Исходные зоны не пересекаются; резервное поглощение неприменимо.";
                return false;
            }

            TryResolveExistingConflicts(
                fallbackZones, sources, patches, plates, settings, slabOutline, openings,
                minimumWidthM, maxPatchOverrunM, out warning);
            if (CountArrangementConflicts(fallbackZones) > 0 ||
                fallbackZones.Any(zone => !HasStepSizedWidth(zone)) ||
                ZoneLayoutDiagnostics.Evaluate(
                    plates, fallbackZones, settings, 0, true, slabOutline, openings).UncoveredCount > maximumUncoveredCount)
            {
                if (string.IsNullOrWhiteSpace(warning))
                    warning = "Резервная раскладка не смогла одновременно убрать пересечения и сохранить покрытие КЭ.";
                return false;
            }

            ZonePatchZoneBuilder.MergeAdjacentCompatibleZones(fallbackZones);
            if (CountArrangementConflicts(fallbackZones) > 0 ||
                fallbackZones.Any(zone => !HasStepSizedWidth(zone)) ||
                ZoneLayoutDiagnostics.Evaluate(
                    plates, fallbackZones, settings, 0, true, slabOutline, openings).UncoveredCount > maximumUncoveredCount)
            {
                warning = string.Join(" ", new[]
                {
                    warning,
                    "Объединение совместимых зон создало конфликт или ухудшило покрытие КЭ."
                }.Where(message => !string.IsNullOrWhiteSpace(message)));
                return false;
            }

            return true;
        }

        public static bool TryResolveExistingConflicts(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out string warning)
        {
            warning = string.Empty;
            if (zones == null || zones.Count == 0) return true;
            if (settings == null || plates == null)
            {
                warning = "Недостаточно данных для устранения пересечений зон.";
                AddWarning(zones, warning);
                return false;
            }

            maxPatchOverrunM = ComputeUnboundedPatchOverrun(
                zones, sourceBoundsByZone, patchBoundsByZone, minimumWidthM);

            var arrangedByLayer = new List<AdditionalZone>();
            var layerWarnings = new List<string>();
            foreach (var layer in Enum.GetValues(typeof(RebarLayer)).Cast<RebarLayer>())
            {
                var originals = zones.Where(zone => zone.Layer == layer).ToList();
                if (originals.Count == 0) continue;

                var layerZones = originals.Select(CopyZone).ToList();
                var layerSources = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
                var layerPatches = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
                for (var i = 0; i < originals.Count; i++)
                {
                    if (sourceBoundsByZone != null && sourceBoundsByZone.TryGetValue(originals[i], out var source))
                        layerSources[layerZones[i]] = source;
                    if (patchBoundsByZone != null && patchBoundsByZone.TryGetValue(originals[i], out var patch))
                        layerPatches[layerZones[i]] = patch;
                }

                if (!TryResolveExistingConflictsSingleLayer(
                        layerZones, layerSources, layerPatches, plates,
                        SettingsForLayer(settings, layer), slabOutline, openings,
                        minimumWidthM, maxPatchOverrunM, out warning))
                    layerWarnings.Add(warning);

                arrangedByLayer.AddRange(layerZones);
            }

            zones.Clear();
            foreach (var zone in arrangedByLayer) zones.Add(zone);
            if (layerWarnings.Count > 0)
            {
                warning = string.Join(" ", layerWarnings.Where(message =>
                    !string.IsNullOrWhiteSpace(message)).Distinct());
                AddWarning(zones, warning);
                return false;
            }
            return true;
        }

        private static bool TryResolveExistingConflictsSingleLayer(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out string warning)
        {
            warning = string.Empty;
            if (zones == null || zones.Count == 0) return true;

            var originals = zones.ToList();
            var stagedZones = originals.Select(CopyZone).ToList();
            var stagedSources = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
            var stagedPatches = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
            for (var i = 0; i < originals.Count; i++)
            {
                if (sourceBoundsByZone != null &&
                    sourceBoundsByZone.TryGetValue(originals[i], out var source))
                    stagedSources[stagedZones[i]] = source;
                if (patchBoundsByZone != null &&
                    patchBoundsByZone.TryGetValue(originals[i], out var patch))
                    stagedPatches[stagedZones[i]] = patch;
            }

            var resolved = TryResolveExistingConflictsCore(
                stagedZones, stagedSources, stagedPatches, plates, settings,
                slabOutline, openings, minimumWidthM, maxPatchOverrunM, out warning, 0);
            if (!resolved || FindArrangementConflictPairs(stagedZones).Count > 0)
            {
                const string rollbackNote = "Исходное расположение зон сохранено без частичных изменений.";
                warning = string.Join(" ", new[] { warning, rollbackNote }
                    .Where(message => !string.IsNullOrWhiteSpace(message)));
                AddWarning(originals, warning);
                return false;
            }

            zones.Clear();
            foreach (var candidate in stagedZones) zones.Add(candidate);
            return true;
        }

        private static bool TryResolveExistingConflictsCore(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out string warning,
            int pass)
        {
            warning = string.Empty;
            if (zones == null || zones.Count == 0) return true;
            if (settings == null || plates == null)
            {
                warning = "Недостаточно данных для устранения пересечений зон.";
                AddWarning(zones, warning);
                return false;
            }

            var entries = zones.Select(zone =>
            {
                var support = sourceBoundsByZone != null && sourceBoundsByZone.TryGetValue(zone, out var source)
                    ? source
                    : Bounds(zone.Contour);
                var patch = patchBoundsByZone != null && patchBoundsByZone.TryGetValue(zone, out var outer)
                    ? outer
                    : support;
                return new ZoneEntry
                {
                    Original = zone,
                    Candidate = CopyZone(zone),
                    Support = support,
                    Patch = patch,
                    OriginalCross = CrossInterval(zone.Contour, zone.Direction),
                    SupportCross = CrossInterval(support, zone.Direction)
                };
            }).ToList();
            var candidates = entries.Select(entry => entry.Candidate).ToList();
            var currentCoverage = ZoneLayoutDiagnostics.Evaluate(
                plates, candidates, settings, 0, true, slabOutline, openings);
            var currentUncovered = currentCoverage.UncoveredCount;
            var currentUncoveredIds = new HashSet<int>(currentCoverage.Issues
                .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                .Select(issue => issue.ElementId));
            var anyFailure = false;
            var warnings = new List<string>();
            var coalescedAnyBand = false;

            foreach (var duplicateBand in BuildConflictGroups(entries))
            {
                if (!TryCoalesceAxialBands(
                        duplicateBand, candidates, plates, settings, slabOutline, openings,
                        currentUncoveredIds, out var coalescedCandidates,
                        out var replacements))
                    continue;

                candidates = coalescedCandidates;
                coalescedAnyBand = true;
                foreach (var replacement in replacements)
                {
                    if (replacement.Value == null)
                        entries.Remove(replacement.Key);
                    else
                        replacement.Key.Candidate = replacement.Value;
                }
                currentCoverage = ZoneLayoutDiagnostics.Evaluate(
                    plates, candidates, settings, 0, true, slabOutline, openings);
                currentUncovered = currentCoverage.UncoveredCount;
                currentUncoveredIds = new HashSet<int>(currentCoverage.Issues
                    .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                    .Select(issue => issue.ElementId));
            }

            var conflictGroups = BuildConflictGroups(entries);
            if (conflictGroups.Count == 0)
            {
                if (coalescedAnyBand)
                {
                    zones.Clear();
                    foreach (var candidate in candidates) zones.Add(candidate);
                }
                return true;
            }

            foreach (var group in conflictGroups)
            {
                var ordered = group.Where(entry => candidates.Contains(entry.Candidate))
                    .OrderBy(entry => entry.SupportCross.Min)
                    .ThenBy(entry => entry.SupportCross.Max)
                    .ThenByDescending(entry => entry.Candidate.AsAdditional)
                    .ToList();
                var arranged = ordered.Select(entry => new ZoneEntry
                {
                    Original = entry.Original,
                    Candidate = CopyZone(entry.Candidate),
                    Support = entry.Support,
                    Patch = entry.Patch,
                    OriginalCross = entry.OriginalCross,
                    SupportCross = entry.SupportCross
                }).ToList();
                var stripFailure = string.Empty;
                var arrangedSuccessfully = TryArrangeConflictGroup(
                    arranged, plates, settings, minimumWidthM, maxPatchOverrunM,
                    out var intervals, out _, out stripFailure);
                var priorityTransferApplied = false;
                if (!arrangedSuccessfully)
                {
                    priorityTransferApplied = TryArrangeByPriorityStepTransfer(
                        ordered, candidates, plates, settings, slabOutline, openings,
                        minimumWidthM, maxPatchOverrunM, out intervals);
                    arrangedSuccessfully = priorityTransferApplied;
                }
                while (!arrangedSuccessfully && ordered.Count > 1 && TryDropRedundantZone(
                           ordered, candidates, plates, settings, slabOutline, openings,
                           currentUncovered, out var reducedCandidates,
                           out var redundantEntry, out var reducedUncovered))
                {
                    candidates = reducedCandidates;
                    currentUncovered = reducedUncovered;
                    currentUncoveredIds = new HashSet<int>(ZoneLayoutDiagnostics.Evaluate(
                        plates, candidates, settings, 0, true, slabOutline, openings).Issues
                        .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                        .Select(issue => issue.ElementId));
                    ordered.Remove(redundantEntry);
                    group.Remove(redundantEntry);
                    arranged = ordered.Select(entry => new ZoneEntry
                    {
                        Original = entry.Original,
                        Candidate = CopyZone(entry.Candidate),
                        Support = entry.Support,
                        Patch = entry.Patch,
                        OriginalCross = entry.OriginalCross,
                        SupportCross = entry.SupportCross
                    }).ToList();
                    arrangedSuccessfully = TryArrangeConflictGroup(
                        arranged, plates, settings, minimumWidthM, maxPatchOverrunM,
                        out intervals, out _, out stripFailure);
                    priorityTransferApplied = false;
                    if (!arrangedSuccessfully)
                    {
                        priorityTransferApplied = TryArrangeByPriorityStepTransfer(
                            ordered, candidates, plates, settings, slabOutline, openings,
                            minimumWidthM, maxPatchOverrunM, out intervals);
                        arrangedSuccessfully = priorityTransferApplied;
                    }
                }
                if (ordered.Count < 2) continue;
                if (CountArrangementConflicts(ordered.Select(entry => entry.Candidate).ToList()) == 0)
                    continue;
                if (!arrangedSuccessfully)
                {
                    if (TryAbsorbConflictPair(
                            ordered, entries, candidates, plates, settings, slabOutline, openings,
                            minimumWidthM, maxPatchOverrunM, currentUncovered,
                            out var absorbedTrial, out var absorbedGroup,
                            out var keeper, out var absorbed,
                            out var absorptionFailure))
                    {
                        var absorbedConflictCount = CountArrangementConflicts(absorbedTrial);
                        var absorbedUncovered = ZoneLayoutDiagnostics.Evaluate(
                            plates, absorbedTrial, settings, 0, true, slabOutline, openings).UncoveredCount;
                        if (absorbedConflictCount < CountArrangementConflicts(candidates) &&
                            absorbedUncovered <= currentUncovered)
                        {
                            candidates = absorbedTrial;
                            currentUncovered = absorbedUncovered;
                            currentUncoveredIds = new HashSet<int>(ZoneLayoutDiagnostics.Evaluate(
                                plates, candidates, settings, 0, true, slabOutline, openings).Issues
                                .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                                .Select(issue => issue.ElementId));
                            keeper.Candidate = absorbed;
                            keeper.Support = UnionBounds(absorbedGroup, usePatchBounds: false);
                            keeper.Patch = UnionBounds(absorbedGroup, usePatchBounds: true);
                            keeper.SupportCross = CrossInterval(keeper.Support, absorbed.Direction);
                            keeper.OriginalCross = CrossInterval(absorbed.Contour, absorbed.Direction);
                            continue;
                        }

                        absorptionFailure = absorbedUncovered > currentUncovered
                            ? $"объединение оставляет непокрытыми КЭ: {currentUncovered} → {absorbedUncovered}"
                            : "объединение не уменьшает число пересечений";
                    }

                    AddConflictWarning(group, warnings,
                        $"Пересечение не устранено для группы из {group.Count} зон: {stripFailure}. " +
                        absorptionFailure);
                    anyFailure = true;
                    continue;
                }

                for (var i = 0; i < arranged.Count; i++)
                    SetCrossInterval(arranged[i].Candidate, intervals[i]);

                var arrangedZones = arranged.Select(entry => entry.Candidate).ToList();
                var arrangedByOriginal = arranged.ToDictionary(
                    entry => entry.Original, entry => entry.Candidate);
                var arrangedByCandidate = ordered.ToDictionary(
                    entry => entry.Candidate,
                    entry => arrangedByOriginal[entry.Original]);
                var trial = new List<AdditionalZone>(candidates.Count);
                foreach (var candidate in candidates)
                {
                    trial.Add(arrangedByCandidate.TryGetValue(candidate, out var replacement)
                        ? replacement
                        : candidate);
                }

                var groupWidthsAreStepSized = arrangedZones.All(HasStepSizedWidth);
                var currentGroupConflictCount = CountArrangementConflicts(
                    group.Select(entry => entry.Candidate).ToList());
                var arrangedGroupConflictCount = CountArrangementConflicts(arrangedZones);
                if (!groupWidthsAreStepSized || arrangedGroupConflictCount > 0)
                {
                    if (priorityTransferApplied && groupWidthsAreStepSized &&
                        arrangedGroupConflictCount < currentGroupConflictCount)
                    {
                        var partialCoverage = ZoneLayoutDiagnostics.Evaluate(
                            plates, trial, settings, 0, true, slabOutline, openings);
                        var newlyUncovered = partialCoverage.Issues.Any(issue =>
                            issue.Kind == ZoneIssueKind.UncoveredElement &&
                            !currentUncoveredIds.Contains(issue.ElementId));
                        if (partialCoverage.UncoveredCount <= currentUncovered && !newlyUncovered)
                        {
                            candidates = trial;
                            foreach (var entry in group)
                            {
                                entry.Candidate = arrangedByCandidate[entry.Candidate];
                                entry.OriginalCross = CrossInterval(
                                    entry.Candidate.Contour, entry.Candidate.Direction);
                            }
                            currentUncovered = partialCoverage.UncoveredCount;
                            currentUncoveredIds = new HashSet<int>(partialCoverage.Issues
                                .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                                .Select(issue => issue.ElementId));
                            continue;
                        }
                    }

                    AddConflictWarning(group, warnings);
                    anyFailure = true;
                    continue;
                }

                var trialCoverage = ZoneLayoutDiagnostics.Evaluate(
                    plates, trial, settings, 0, true, slabOutline, openings);
                var trialUncovered = trialCoverage.UncoveredCount;
                var alternateSweepFailure = string.Empty;
                if (trialUncovered > currentUncovered)
                {
                    var sweepArranged = ordered.Select(entry => new ZoneEntry
                    {
                        Original = entry.Original,
                        Candidate = CopyZone(entry.Candidate),
                        Support = entry.Support,
                        Patch = entry.Patch,
                        OriginalCross = entry.OriginalCross,
                        SupportCross = entry.SupportCross
                    }).ToList();
                    var direction = ordered[0].Candidate.Direction;
                    var layer = ordered[0].Candidate.Layer;
                    var axialMin = ordered.Min(entry => AxialInterval(
                        entry.Candidate.Contour, direction).Min);
                    var axialMax = ordered.Max(entry => AxialInterval(
                        entry.Candidate.Contour, direction).Max);
                    var patchCrossMin = ordered.Min(entry => CrossInterval(entry.Patch, direction).Min);
                    var patchCrossMax = ordered.Max(entry => CrossInterval(entry.Patch, direction).Max);
                    var allowedCrossMin = Math.Min(ordered.Min(entry => entry.SupportCross.Min),
                        patchCrossMin - maxPatchOverrunM);
                    var allowedCrossMax = Math.Max(ordered.Max(entry => entry.SupportCross.Max),
                        patchCrossMax + maxPatchOverrunM);
                    var coveragePadding = Math.Max(0.05,
                        ordered.Max(entry => entry.Candidate.BarStepMm) / 1000.0);
                    var protectedPlates = plates.Where(plate =>
                    {
                        if (!plate.Rebar.Ok ||
                            plate.Rebar.Get(layer) - Background(settings, layer) <= DemandTolerance)
                            return false;
                        (double Min, double Max) plateAxial = plate.Contour != null && plate.Contour.Count >= 3
                            ? AxialInterval(plate.Contour, direction)
                            : (Min: direction == ZoneDirection.X
                                    ? plate.Centroid.X : plate.Centroid.Y,
                                Max: direction == ZoneDirection.X
                                    ? plate.Centroid.X : plate.Centroid.Y);
                        var plateCrossMin = plate.Contour != null && plate.Contour.Count >= 3
                            ? CrossInterval(plate.Contour, direction).Min
                            : CrossCoordinate(plate.Centroid, direction);
                        var plateCrossMax = plate.Contour != null && plate.Contour.Count >= 3
                            ? CrossInterval(plate.Contour, direction).Max
                            : CrossCoordinate(plate.Centroid, direction);
                        return plateAxial.Max >= axialMin - coveragePadding &&
                               plateAxial.Min <= axialMax + coveragePadding &&
                               plateCrossMax >= allowedCrossMin - coveragePadding &&
                               plateCrossMin <= allowedCrossMax + coveragePadding;
                    }).ToList();

                    bool PreservesCoverage(IList<(double Min, double Max)> proposedIntervals)
                    {
                        var originalIntervals = ordered.Select(entry => CrossInterval(
                            entry.Candidate.Contour, direction)).ToList();
                        try
                        {
                            for (var i = 0; i < ordered.Count; i++)
                                SetCrossInterval(ordered[i].Candidate, proposedIntervals[i]);

                            foreach (var plate in protectedPlates)
                            {
                                if (currentUncoveredIds.Contains(plate.Id)) continue;
                                var requiredAs = plate.Rebar.Get(layer) - Background(settings, layer);
                                if (!ZoneCoverageRules.CoversOrBridgesGap(
                                        candidates, plate, layer, requiredAs, slabOutline, openings))
                                    return false;
                            }
                            return true;
                        }
                        finally
                        {
                            for (var i = 0; i < ordered.Count; i++)
                                SetCrossInterval(ordered[i].Candidate, originalIntervals[i]);
                        }
                    }

                    if (TryArrangeAxialSweep(sweepArranged, minimumWidthM, maxPatchOverrunM,
                            out var sweepIntervals, out _, out alternateSweepFailure,
                            PreservesCoverage))
                    {
                        for (var i = 0; i < sweepArranged.Count; i++)
                            SetCrossInterval(sweepArranged[i].Candidate, sweepIntervals[i]);

                        var sweepZones = sweepArranged.Select(entry => entry.Candidate).ToList();
                        var sweepConflictCount = CountArrangementConflicts(sweepZones);
                        var sweepByOriginal = sweepArranged.ToDictionary(
                            entry => entry.Original, entry => entry.Candidate);
                        var sweepByCandidate = ordered.ToDictionary(
                            entry => entry.Candidate,
                            entry => sweepByOriginal[entry.Original]);
                        var sweepTrial = candidates.Select(candidate =>
                            sweepByCandidate.TryGetValue(candidate, out var replacement)
                                ? replacement
                                : candidate).ToList();
                        var sweepCoverage = ZoneLayoutDiagnostics.Evaluate(
                            plates, sweepTrial, settings, 0, true, slabOutline, openings);
                        if (sweepConflictCount < currentGroupConflictCount &&
                            sweepCoverage.UncoveredCount <= currentUncovered)
                        {
                            arranged = sweepArranged;
                            arrangedZones = sweepZones;
                            arrangedGroupConflictCount = sweepConflictCount;
                            arrangedByCandidate = sweepByCandidate;
                            trial = sweepTrial;
                            trialCoverage = sweepCoverage;
                            trialUncovered = sweepCoverage.UncoveredCount;
                        }
                    }
                }
                var newlyUncoveredIds = trialCoverage.Issues
                    .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement &&
                                    !currentUncoveredIds.Contains(issue.ElementId))
                    .Select(issue => issue.ElementId)
                    .Distinct()
                    .Take(8)
                    .ToList();
                if (arrangedGroupConflictCount >= currentGroupConflictCount ||
                    trialUncovered > currentUncovered)
                {
                    var layoutFailure = arrangedGroupConflictCount >= currentGroupConflictCount
                        ? "раздвижка не устранила пересечение"
                        : $"раздвижка увеличила число непокрытых КЭ: {currentUncovered} → {trialUncovered}" +
                          (newlyUncoveredIds.Count == 0
                              ? string.Empty
                              : $" (новые КЭ: {string.Join(", ", newlyUncoveredIds)})");
                    if (!string.IsNullOrWhiteSpace(alternateSweepFailure))
                        layoutFailure += $"; резервная раздвижка: {alternateSweepFailure}";
                    var absorptionFailure = string.Empty;
                    if (TryAbsorbConflictPair(
                            group, entries, candidates, plates, settings, slabOutline, openings,
                            minimumWidthM, maxPatchOverrunM, currentUncovered,
                            out var absorbedTrial, out var absorbedGroup,
                            out var keeper, out var absorbed,
                            out absorptionFailure))
                    {
                        var absorbedConflictCount = CountArrangementConflicts(absorbedTrial);
                        var absorbedUncovered = ZoneLayoutDiagnostics.Evaluate(
                            plates, absorbedTrial, settings, 0, true, slabOutline, openings).UncoveredCount;
                        if (absorbedConflictCount < CountArrangementConflicts(candidates) &&
                            absorbedUncovered <= currentUncovered)
                        {
                            candidates = absorbedTrial;
                            currentUncovered = absorbedUncovered;
                            currentUncoveredIds = new HashSet<int>(ZoneLayoutDiagnostics.Evaluate(
                                plates, candidates, settings, 0, true, slabOutline, openings).Issues
                                .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                                .Select(issue => issue.ElementId));
                            keeper.Candidate = absorbed;
                            keeper.Support = UnionBounds(absorbedGroup, usePatchBounds: false);
                            keeper.Patch = UnionBounds(absorbedGroup, usePatchBounds: true);
                            keeper.SupportCross = CrossInterval(keeper.Support, absorbed.Direction);
                            keeper.OriginalCross = CrossInterval(absorbed.Contour, absorbed.Direction);
                            continue;
                        }

                        absorptionFailure = absorbedUncovered > currentUncovered
                            ? $"объединение оставляет непокрытыми КЭ: {currentUncovered} → {absorbedUncovered}"
                            : "объединение не уменьшает число пересечений";
                    }

                    if (TryCollapseConflictGroup(
                            group, candidates, plates, settings, slabOutline, openings,
                            maxPatchOverrunM, minimumWidthM, currentUncoveredIds,
                            out var collapsedCandidates, out var replacements))
                    {
                        candidates = collapsedCandidates;
                        var collapsedSupport = UnionBounds(group, usePatchBounds: false);
                        var collapsedPatch = UnionBounds(group, usePatchBounds: true);
                        foreach (var replacement in replacements)
                        {
                            if (replacement.Value == null)
                            {
                                entries.Remove(replacement.Key);
                                group.Remove(replacement.Key);
                                continue;
                            }

                            replacement.Key.Candidate = replacement.Value;
                            replacement.Key.Support = collapsedSupport;
                            replacement.Key.Patch = collapsedPatch;
                            replacement.Key.SupportCross = CrossInterval(
                                collapsedSupport, replacement.Value.Direction);
                            replacement.Key.OriginalCross = CrossInterval(
                                replacement.Value.Contour, replacement.Value.Direction);
                        }
                        currentCoverage = ZoneLayoutDiagnostics.Evaluate(
                            plates, candidates, settings, 0, true, slabOutline, openings);
                        currentUncovered = currentCoverage.UncoveredCount;
                        currentUncoveredIds = new HashSet<int>(currentCoverage.Issues
                            .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                            .Select(issue => issue.ElementId));
                        continue;
                    }

                    AddConflictWarning(group, warnings,
                        $"Локальная раздвижка: {layoutFailure}; " +
                        "попытка поглощения зоны не помогла. " + absorptionFailure);
                    anyFailure = true;
                    continue;
                }

                candidates = trial;
                foreach (var entry in ordered)
                    entry.Candidate = arrangedByOriginal[entry.Original];
                currentUncovered = trialUncovered;
                currentUncoveredIds = new HashSet<int>(trialCoverage.Issues
                    .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                    .Select(issue => issue.ElementId));
            }

            var remainingPairs = FindArrangementConflictPairs(candidates);
            foreach (var pair in remainingPairs)
            {
                var firstAxial = AxialInterval(pair.First.Contour, pair.First.Direction);
                var secondAxial = AxialInterval(pair.Second.Contour, pair.Second.Direction);
                var firstCross = CrossInterval(pair.First.Contour, pair.First.Direction);
                var secondCross = CrossInterval(pair.Second.Contour, pair.Second.Direction);
                var warningText = $"Пересечение зон #{pair.First.ZoneId} и #{pair.Second.ZoneId} " +
                    $"({pair.First.Layer}/{pair.First.Direction}, шаг {pair.First.BarStepMm}/{pair.Second.BarStepMm} мм): " +
                    $"по длине [{firstAxial.Min:0.###};{firstAxial.Max:0.###}] / [{secondAxial.Min:0.###};{secondAxial.Max:0.###}], " +
                    $"по ширине [{firstCross.Min:0.###};{firstCross.Max:0.###}] / [{secondCross.Min:0.###};{secondCross.Max:0.###}].";
                AddWarning(new[] { pair.First, pair.Second }, warningText);
                if (!warnings.Contains(warningText)) warnings.Add(warningText);
                anyFailure = true;
            }

            zones.Clear();
            foreach (var candidate in candidates) zones.Add(candidate);
            warning = string.Join(" ", warnings.Distinct());
            if (remainingPairs.Count > 0 && pass == 0)
            {
                var retrySources = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
                var retryPatches = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
                foreach (var candidate in candidates)
                {
                    var entry = entries.FirstOrDefault(item => ReferenceEquals(item.Candidate, candidate));
                    var source = entry?.Support ?? Bounds(candidate.Contour);
                    retrySources[candidate] = source;
                    retryPatches[candidate] = entry?.Patch ?? source;
                }

                var firstWarning = warning;
                var retryOk = TryResolveExistingConflictsCore(
                    zones, retrySources, retryPatches, plates, settings, slabOutline, openings,
                    minimumWidthM, maxPatchOverrunM, out var retryWarning, pass + 1);
                warning = string.Join(" ", new[] { firstWarning, retryWarning }
                    .Where(message => !string.IsNullOrWhiteSpace(message)).Distinct());
                return retryOk && FindArrangementConflictPairs(zones).Count == 0;
            }

            return !anyFailure;
        }

        private static ZonePatchFrameBounds UnionBounds(IList<ZoneEntry> group, bool usePatchBounds)
        {
            var bounds = group.Select(entry => usePatchBounds ? entry.Patch : entry.Support).ToList();
            return new ZonePatchFrameBounds(
                bounds.Min(bound => bound.MinX), bounds.Max(bound => bound.MaxX),
                bounds.Min(bound => bound.MinY), bounds.Max(bound => bound.MaxY));
        }

        private static bool TryAbsorbConflictClosure(
            IList<ZoneEntry> initialGroup,
            IList<ZoneEntry> allEntries,
            IList<AdditionalZone> candidates,
            AnalysisSettings settings,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out List<AdditionalZone> resolvedCandidates,
            out List<ZoneEntry> absorbedGroup,
            out ZoneEntry retainedEntry,
            out AdditionalZone absorbedZone,
            out string failureReason)
        {
            resolvedCandidates = new List<AdditionalZone>();
            absorbedGroup = new List<ZoneEntry>();
            retainedEntry = null!;
            absorbedZone = null!;
            failureReason = string.Empty;
            var group = initialGroup.ToList();
            absorbedGroup = group;
            var groupSet = new HashSet<ZoneEntry>(group);
            for (var expansion = 0; expansion <= allEntries.Count; expansion++)
            {
                if (!TryAbsorbConflictGroup(group, settings, openings, minimumWidthM,
                        maxPatchOverrunM, out retainedEntry, out absorbedZone, out failureReason))
                    return false;

                var groupCandidates = new HashSet<AdditionalZone>(group.Select(entry => entry.Candidate));
                resolvedCandidates = new List<AdditionalZone>(candidates.Count - group.Count + 1);
                var inserted = false;
                foreach (var candidate in candidates)
                {
                    if (!groupCandidates.Contains(candidate))
                        resolvedCandidates.Add(candidate);
                    else if (!inserted)
                    {
                        resolvedCandidates.Add(absorbedZone);
                        inserted = true;
                    }
                }

                var expanded = false;
                foreach (var pair in FindArrangementConflictPairs(resolvedCandidates))
                {
                    var outside = ReferenceEquals(pair.First, absorbedZone) ? pair.Second :
                        ReferenceEquals(pair.Second, absorbedZone) ? pair.First : null;
                    if (outside == null) continue;
                    var entry = allEntries.FirstOrDefault(item =>
                        ReferenceEquals(item.Candidate, outside));
                    if (entry == null || !groupSet.Add(entry)) continue;
                    group.Add(entry);
                    expanded = true;
                }

                if (!expanded) return true;
            }

            failureReason = "Нельзя объединить: группа конфликтов продолжает расширяться.";
            return false;
        }

        private static bool TryAbsorbConflictPair(
            IList<ZoneEntry> conflictGroup,
            IList<ZoneEntry> allEntries,
            IList<AdditionalZone> candidates,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            int maximumUncoveredCount,
            out List<AdditionalZone> resolvedCandidates,
            out List<ZoneEntry> absorbedGroup,
            out ZoneEntry retainedEntry,
            out AdditionalZone absorbedZone,
            out string failureReason)
        {
            resolvedCandidates = new List<AdditionalZone>();
            absorbedGroup = new List<ZoneEntry>();
            retainedEntry = null!;
            absorbedZone = null!;
            failureReason = string.Empty;
            var currentConflictCount = CountArrangementConflicts(candidates);
            var conflictPairs = FindArrangementConflictPairs(
                conflictGroup.Select(entry => entry.Candidate).ToList());
            foreach (var pair in conflictPairs)
            {
                var pairEntries = conflictGroup.Where(entry =>
                    ReferenceEquals(entry.Candidate, pair.First) ||
                    ReferenceEquals(entry.Candidate, pair.Second)).ToList();
                if (pairEntries.Count != 2) continue;
                if (!TryAbsorbConflictClosure(
                        pairEntries, allEntries, candidates, settings, openings,
                        minimumWidthM, maxPatchOverrunM,
                        out var trial, out var trialGroup, out var keeper,
                        out var absorbed, out var trialFailure))
                {
                    failureReason = trialFailure;
                    continue;
                }

                var conflictCount = CountArrangementConflicts(trial);
                var uncoveredCount = ZoneLayoutDiagnostics.Evaluate(
                    plates, trial, settings, 0, true, slabOutline, openings).UncoveredCount;
                if (conflictCount >= currentConflictCount || uncoveredCount > maximumUncoveredCount)
                {
                    failureReason = uncoveredCount > maximumUncoveredCount
                        ? $"объединение оставляет непокрытыми КЭ: {maximumUncoveredCount} → {uncoveredCount}"
                        : "объединение не уменьшает число пересечений";
                    continue;
                }

                resolvedCandidates = trial;
                absorbedGroup = trialGroup;
                retainedEntry = keeper;
                absorbedZone = absorbed;
                return true;
            }

            if (string.IsNullOrWhiteSpace(failureReason))
                failureReason = "В группе не найдено исправимое фактическое пересечение.";
            return false;
        }

        private static void AddConflictWarning(
            IEnumerable<ZoneEntry> group, ICollection<string> warnings, string? message = null)
        {
            var warning = message ??
                "Пересечение зоны оставлено без изменения: исправление нарушит покрытие КЭ или кратность шага.";
            AddWarning(group.Select(entry => entry.Candidate), warning);
            if (!warnings.Contains(warning)) warnings.Add(warning);
        }

        private static bool TryDropRedundantZone(
            IList<ZoneEntry> conflictGroup,
            IList<AdditionalZone> candidates,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            int maximumUncoveredCount,
            out List<AdditionalZone> reducedCandidates,
            out ZoneEntry removedEntry,
            out int uncoveredCount)
        {
            reducedCandidates = new List<AdditionalZone>();
            removedEntry = null!;
            uncoveredCount = maximumUncoveredCount;
            var groupZones = conflictGroup.Select(entry => entry.Candidate).ToList();
            var conflictPairs = FindArrangementConflictPairs(groupZones);
            if (conflictPairs.Count == 0) return false;

            var conflictingZones = new HashSet<AdditionalZone>(conflictPairs
                .SelectMany(pair => new[] { pair.First, pair.Second }));
            var eligible = conflictGroup
                .Where(entry => conflictingZones.Contains(entry.Candidate) &&
                                entry.Candidate.NodeIds != null && entry.Candidate.NodeIds.Count > 0 &&
                                !TryGetSegmentRange(entry.Candidate, out _, out _))
                .OrderBy(entry => entry.Candidate.AsAdditional)
                .ThenBy(entry => entry.Candidate.WidthMm * entry.Candidate.LengthMm)
                .ToList();
            if (eligible.Count == 0) return false;

            var currentConflictCount = CountArrangementConflicts(candidates);
            foreach (var entry in eligible)
            {
                var trial = candidates.Where(candidate =>
                    !ReferenceEquals(candidate, entry.Candidate)).ToList();
                if (CountArrangementConflicts(trial) >= currentConflictCount) continue;

                var trialUncovered = ZoneLayoutDiagnostics.Evaluate(
                    plates, trial, settings, 0, true, slabOutline, openings).UncoveredCount;
                if (trialUncovered > maximumUncoveredCount) continue;

                reducedCandidates = trial;
                removedEntry = entry;
                uncoveredCount = trialUncovered;
                return true;
            }

            return false;
        }

        private static bool TryArrangeByPriorityStepTransfer(
            IList<ZoneEntry> conflictGroup,
            IList<AdditionalZone> candidates,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out List<(double Min, double Max)> intervals)
        {
            intervals = new List<(double Min, double Max)>();
            if (conflictGroup == null || conflictGroup.Count < 2 || candidates == null ||
                candidates.Count < 2 || plates == null || settings == null)
                return false;

            var candidateIndices = new Dictionary<AdditionalZone, int>();
            for (var i = 0; i < candidates.Count; i++)
                candidateIndices[candidates[i]] = i;
            if (conflictGroup.Any(entry => !candidateIndices.ContainsKey(entry.Candidate)))
                return false;

            var groupPairs = FindArrangementConflictPairs(
                conflictGroup.Select(entry => entry.Candidate).ToList());
            if (groupPairs.Count == 0) return false;

            var trial = candidates.Select(CopyZone).ToList();
            var trialIndices = trial.Select((zone, index) => (zone, index))
                .ToDictionary(item => item.zone, item => item.index);

            var originalConflictKeys = new HashSet<(int First, int Second)>(
                FindArrangementConflictPairs(candidates).Select(pair =>
                    OrderedPair(candidateIndices[pair.First], candidateIndices[pair.Second])));
            var groupIndices = new HashSet<int>(conflictGroup.Select(entry =>
                candidateIndices[entry.Candidate]));

            var direction = conflictGroup[0].Candidate.Direction;
            var allowedMin = conflictGroup.Min(entry => Math.Min(
                entry.SupportCross.Min,
                CrossInterval(entry.Patch, direction).Min - maxPatchOverrunM));
            var allowedMax = conflictGroup.Max(entry => Math.Max(
                entry.SupportCross.Max,
                CrossInterval(entry.Patch, direction).Max + maxPatchOverrunM));

            while (true)
            {
                var currentGroupPairs = FindArrangementConflictPairs(trial)
                    .Select(conflict => OrderedPair(
                        trialIndices[conflict.First], trialIndices[conflict.Second]))
                    .Where(pair => groupIndices.Contains(pair.First) && groupIndices.Contains(pair.Second))
                    .Distinct()
                    .OrderByDescending(pair => Math.Max(
                        trial[pair.First].AsAdditional, trial[pair.Second].AsAdditional))
                    .ThenByDescending(pair => CrossBandsOverlapLength(
                        CrossInterval(trial[pair.First].Contour, trial[pair.First].Direction),
                        CrossInterval(trial[pair.Second].Contour, trial[pair.Second].Direction)))
                    .ToList();
                if (currentGroupPairs.Count == 0) break;

                var changed = false;
                foreach (var pair in currentGroupPairs)
                {
                    var firstOriginal = candidates[pair.First];
                    var secondOriginal = candidates[pair.Second];
                    if (candidates.Where((_, index) => index != pair.First && index != pair.Second)
                        .Any(other => IsIntentionalLapPair(firstOriginal, other) ||
                                      IsIntentionalLapPair(secondOriginal, other)))
                        continue;

                    var beforePairs = FindArrangementConflictPairs(trial);
                    if (!beforePairs.Any(conflict =>
                            OrderedPair(trialIndices[conflict.First], trialIndices[conflict.Second]) == pair))
                        continue;

                    var pairTrial = trial.Select(CopyZone).ToList();
                    var pairTrialIndices = pairTrial.Select((zone, index) => (zone, index))
                        .ToDictionary(item => item.zone, item => item.index);
                    if (!TryTransferOneBarStep(
                            pairTrial[pair.First], pairTrial[pair.Second], minimumWidthM,
                            allowedMin, allowedMax) ||
                        conflictGroup.Any(entry => !HasStepSizedWidth(
                            pairTrial[candidateIndices[entry.Candidate]])))
                        continue;

                    var afterPairs = FindArrangementConflictPairs(pairTrial);
                    if (afterPairs.Count >= beforePairs.Count ||
                        afterPairs.Any(conflict => !originalConflictKeys.Contains(
                            OrderedPair(pairTrialIndices[conflict.First], pairTrialIndices[conflict.Second]))))
                        continue;

                    var beforeCoverage = ZoneLayoutDiagnostics.Evaluate(
                        plates, trial, settings, 0, true, slabOutline, openings);
                    var afterCoverage = ZoneLayoutDiagnostics.Evaluate(
                        plates, pairTrial, settings, 0, true, slabOutline, openings);
                    var previouslyUncoveredIds = new HashSet<int>(beforeCoverage.Issues
                        .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                        .Select(issue => issue.ElementId));
                    if (afterCoverage.UncoveredCount > beforeCoverage.UncoveredCount ||
                        afterCoverage.Issues.Any(issue => issue.Kind == ZoneIssueKind.UncoveredElement &&
                                                          !previouslyUncoveredIds.Contains(issue.ElementId)))
                        continue;

                    trial = pairTrial;
                    trialIndices = pairTrialIndices;
                    changed = true;
                }

                if (!changed) break;
            }

            var trialConflictPairs = FindArrangementConflictPairs(trial);
            if (trialConflictPairs.Count >= originalConflictKeys.Count ||
                conflictGroup.Any(entry => !HasStepSizedWidth(
                    trial[candidateIndices[entry.Candidate]])))
                return false;

            intervals = conflictGroup.Select(entry =>
                CrossInterval(trial[candidateIndices[entry.Candidate]].Contour, direction)).ToList();
            return true;
        }

        private static bool TryTransferOneBarStep(
            AdditionalZone first,
            AdditionalZone second,
            double minimumWidthM,
            double allowedMin,
            double allowedMax)
        {
            const double toleranceM = 1e-6;
            if (first.Layer != second.Layer || first.Direction != second.Direction ||
                first.BarStepMm <= 0 || second.BarStepMm <= 0)
                return false;

            var firstCross = CrossInterval(first.Contour, first.Direction);
            var secondCross = CrossInterval(second.Contour, second.Direction);
            var firstIsLeft = firstCross.Min < secondCross.Min - toleranceM ||
                              (Math.Abs(firstCross.Min - secondCross.Min) <= toleranceM &&
                               firstCross.Max < secondCross.Max - toleranceM);
            if (Math.Abs(firstCross.Min - secondCross.Min) <= toleranceM &&
                Math.Abs(firstCross.Max - secondCross.Max) <= toleranceM)
                return false;

            var left = firstIsLeft ? first : second;
            var right = firstIsLeft ? second : first;
            var leftCross = firstIsLeft ? firstCross : secondCross;
            var rightCross = firstIsLeft ? secondCross : firstCross;
            var winner = CompareCalculatedAs(left, right) >= 0 ? left : right;
            var loser = ReferenceEquals(winner, left) ? right : left;
            var transferStepMm = LeastCommonMultiple(first.BarStepMm, second.BarStepMm);
            if (transferStepMm <= 0) return false;
            var transferStepM = transferStepMm / 1000.0;
            var leftWidth = leftCross.Max - leftCross.Min;
            var rightWidth = rightCross.Max - rightCross.Min;
            (double Min, double Max) winnerNext;
            (double Min, double Max) loserNext;

            if (ReferenceEquals(winner, left))
            {
                var boundary = leftCross.Min + leftWidth + transferStepM;
                winnerNext = (leftCross.Min, boundary);
                loserNext = (boundary, boundary + rightWidth - transferStepM);
            }
            else
            {
                var boundary = rightCross.Max - rightWidth - transferStepM;
                winnerNext = (boundary, rightCross.Max);
                loserNext = (boundary - leftWidth + transferStepM, boundary);
            }

            var winnerWidth = winnerNext.Max - winnerNext.Min;
            var loserWidth = loserNext.Max - loserNext.Min;
            if (winnerWidth <= 0 || loserWidth <= 0 ||
                winnerNext.Min < allowedMin - toleranceM || winnerNext.Max > allowedMax + toleranceM ||
                loserNext.Min < allowedMin - toleranceM || loserNext.Max > allowedMax + toleranceM ||
                loserWidth + toleranceM < MinimumWidthUnits(loser, minimumWidthM) / (double)GeometryScale ||
                !IsWidthMultipleOfStep(winnerWidth, winner.BarStepMm) ||
                !IsWidthMultipleOfStep(loserWidth, loser.BarStepMm))
                return false;

            SetCrossInterval(winner, winnerNext);
            SetCrossInterval(loser, loserNext);
            return HasStepSizedWidth(winner) && HasStepSizedWidth(loser);
        }

        private static int LeastCommonMultiple(int first, int second)
        {
            if (first <= 0 || second <= 0) return 0;
            var a = first;
            var b = second;
            while (b != 0)
            {
                var remainder = a % b;
                a = b;
                b = remainder;
            }
            var multiple = (long)first / a * second;
            return multiple <= int.MaxValue ? (int)multiple : 0;
        }

        private static int CompareCalculatedAs(AdditionalZone first, AdditionalZone second)
        {
            var delta = first.AsAdditional - second.AsAdditional;
            if (Math.Abs(delta) > 1e-6) return delta > 0 ? 1 : -1;
            delta = first.AsRequired - second.AsRequired;
            if (Math.Abs(delta) > 1e-6) return delta > 0 ? 1 : -1;
            delta = first.AsCoveredCm2PerM - second.AsCoveredCm2PerM;
            if (Math.Abs(delta) > 1e-6) return delta > 0 ? 1 : -1;
            return 0;
        }

        private static bool IsWidthMultipleOfStep(double widthM, int stepMm)
        {
            if (stepMm <= 0) return false;
            var stepCount = widthM * 1000.0 / stepMm;
            return Math.Abs(stepCount - Math.Round(stepCount)) <= 1e-5;
        }

        private static (int First, int Second) OrderedPair(int first, int second) =>
            first <= second ? (first, second) : (second, first);

        private static double CrossBandsOverlapLength(
            (double Min, double Max) first, (double Min, double Max) second) =>
            Math.Max(0, Math.Min(first.Max, second.Max) - Math.Max(first.Min, second.Min));

        private static bool TryArrangeConflictGroup(
            IList<ZoneEntry> zones,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out List<(double Min, double Max)> intervals,
            out bool minimumWidthInfeasible,
            out string failureReason)
        {
            if (HasIntentionalLapPair(zones))
                return TryArrangeAxialSweep(zones, minimumWidthM, maxPatchOverrunM,
                    out intervals, out minimumWidthInfeasible, out failureReason);

            if (TryArrangeStrip(zones, plates, settings, minimumWidthM,
                    maxPatchOverrunM, out intervals, out minimumWidthInfeasible,
                    out failureReason))
                return true;

            return TryArrangeAxialSweep(zones, minimumWidthM, maxPatchOverrunM,
                out intervals, out minimumWidthInfeasible, out failureReason);
        }

        private static bool TryCoalesceAxialBands(
            IList<ZoneEntry> conflictGroup,
            IList<AdditionalZone> candidates,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            ISet<int> previouslyUncoveredIds,
            out List<AdditionalZone> coalescedCandidates,
            out Dictionary<ZoneEntry, AdditionalZone?> replacements)
        {
            coalescedCandidates = new List<AdditionalZone>();
            replacements = new Dictionary<ZoneEntry, AdditionalZone?>();
            if (conflictGroup.Count < 2) return false;

            var stagedCandidates = candidates.ToList();

            var pending = new HashSet<ZoneEntry>(conflictGroup.Where(entry =>
                IsRectangle(entry.Candidate) &&
                entry.Candidate.FamilyKind == ZoneFamilyKind.Straight &&
                entry.Candidate.BarStepMm > 0 &&
                HasStepSizedWidth(entry.Candidate)));
            while (pending.Count > 0)
            {
                var seed = pending.First();
                pending.Remove(seed);
                var band = new List<ZoneEntry> { seed };
                var frontier = new Queue<ZoneEntry>();
                frontier.Enqueue(seed);
                while (frontier.Count > 0)
                {
                    var current = frontier.Dequeue();
                    foreach (var neighbor in pending.ToList())
                    {
                        if (!SameAxialBand(current.Candidate, neighbor.Candidate) ||
                            !AxiallyOverlap(current, neighbor))
                            continue;
                        pending.Remove(neighbor);
                        band.Add(neighbor);
                        frontier.Enqueue(neighbor);
                    }
                }

                if (band.Count < 2 || band.All(entry =>
                        TryGetSegmentRange(entry.Candidate, out _, out _)))
                    continue;
                var orderedBand = band.OrderBy(entry => AxialInterval(
                    entry.Candidate.Contour, entry.Candidate.Direction).Min)
                    .ThenBy(entry => entry.Candidate.ZoneId).ToList();
                var template = orderedBand
                    .Where(entry => entry.Candidate.AsCoveredCm2PerM + DemandTolerance >=
                                    orderedBand.Max(member => member.Candidate.AsAdditional))
                    .OrderBy(entry => entry.Candidate.AsCoveredCm2PerM)
                    .FirstOrDefault()?.Candidate;
                if (template == null) continue;

                var direction = template.Direction;
                var axialStartMm = orderedBand.Min(entry => AxialInterval(
                    entry.Candidate.Contour, direction).Min) * 1000.0;
                var axialEndMm = orderedBand.Max(entry => AxialInterval(
                    entry.Candidate.Contour, direction).Max) * 1000.0;
                if (axialEndMm - axialStartMm <= 11700 + 1e-6 ||
                    !TryBuildLapSegments(axialStartMm, axialEndMm, template,
                        out var segments) || segments.Count != orderedBand.Count)
                    continue;

                var requiredAs = orderedBand.Max(entry => entry.Candidate.AsAdditional);
                var nodeIds = orderedBand.SelectMany(entry => entry.Candidate.NodeIds ?? new List<int>())
                    .Distinct().ToList();
                var cross = CrossInterval(template.Contour, direction);
                var lapOverlapMm = 2 * RebarTables.LapLenMm(
                    template.ConcreteClass, template.DiameterMm);
                var proposed = new Dictionary<ZoneEntry, AdditionalZone>();
                for (var i = 0; i < orderedBand.Count; i++)
                {
                    var segment = segments[i];
                    var replacement = CopyZone(template);
                    SetRectangleBounds(replacement, segment.StartMm / 1000.0,
                        segment.EndMm / 1000.0, cross.Min, cross.Max);
                    replacement.ZoneId = orderedBand[i].Candidate.ZoneId;
                    replacement.LengthMm = segment.LengthMm;
                    replacement.LengthM = segment.LengthMm / 1000.0;
                    replacement.WidthMm = template.WidthMm;
                    replacement.WidthM = template.WidthMm / 1000.0;
                    replacement.BarCount = template.BarCount;
                    replacement.AsAdditional = requiredAs;
                    replacement.AsRequired = requiredAs + Background(settings, template.Layer);
                    replacement.NodeIds = nodeIds.ToList();
                    replacement.ElementId = replacement.NodeIds.FirstOrDefault();
                    var retainedComment = StripSegmentMetadata(template.Comment);
                    var segmentComment = $"часть {i + 1}/{segments.Count}; нахлёст {lapOverlapMm} мм";
                    replacement.Comment = string.IsNullOrWhiteSpace(retainedComment)
                        ? segmentComment
                        : segmentComment + "; " + retainedComment;
                    SetLayerAs(replacement, replacement.AsRequired);
                    if (replacement.LengthMm > 11700 + 1e-6 || !HasStepSizedWidth(replacement))
                    {
                        proposed.Clear();
                        break;
                    }
                    proposed.Add(orderedBand[i], replacement);
                }
                if (proposed.Count != orderedBand.Count) continue;

                var proposedByZone = proposed.ToDictionary(
                    item => item.Key.Candidate, item => item.Value);
                var bandTrial = stagedCandidates.Select(candidate =>
                    proposedByZone.TryGetValue(candidate, out var replacement)
                        ? replacement
                        : candidate).ToList();
                if (CountArrangementConflicts(bandTrial) >=
                    CountArrangementConflicts(stagedCandidates))
                    continue;

                var bandCoverage = ZoneLayoutDiagnostics.Evaluate(
                    plates, bandTrial, settings, 0, true, slabOutline, openings);
                var bandUncoveredIds = new HashSet<int>(bandCoverage.Issues
                    .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                    .Select(issue => issue.ElementId));
                if (bandTrial.Any(zone => zone.LengthMm > 11700 + 1e-6 ||
                                          !HasStepSizedWidth(zone)) ||
                    bandUncoveredIds.Any(id => !previouslyUncoveredIds.Contains(id)))
                    continue;

                stagedCandidates = bandTrial;
                foreach (var replacement in proposed)
                    replacements[replacement.Key] = replacement.Value;
            }

            if (replacements.Count == 0) return false;
            coalescedCandidates = stagedCandidates;
            return true;
        }

        private static bool TryCollapseConflictGroup(
            IList<ZoneEntry> conflictGroup,
            IList<AdditionalZone> candidates,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double maxPatchOverrunM,
            double minimumWidthM,
            ISet<int> previouslyUncoveredIds,
            out List<AdditionalZone> collapsedCandidates,
            out Dictionary<ZoneEntry, AdditionalZone?> replacements)
        {
            collapsedCandidates = new List<AdditionalZone>();
            replacements = new Dictionary<ZoneEntry, AdditionalZone?>();
            if (plates.Count == 0 || conflictGroup.Count < 3 ||
                conflictGroup.Any(entry =>
                    !IsRectangle(entry.Candidate) ||
                    entry.Candidate.Layer != conflictGroup[0].Candidate.Layer ||
                    entry.Candidate.Direction != conflictGroup[0].Candidate.Direction ||
                    entry.Candidate.FamilyKind != ZoneFamilyKind.Straight ||
                    entry.Candidate.BarStepMm <= 0 ||
                    entry.Candidate.Contour == null || entry.Candidate.Contour.Count < 4))
                return false;

            var requiredAs = conflictGroup.Max(entry => entry.Candidate.AsAdditional);
            var template = conflictGroup
                .Where(entry => entry.Candidate.AsCoveredCm2PerM + DemandTolerance >= requiredAs &&
                                entry.Candidate.FamilyKind == ZoneFamilyKind.Straight &&
                                entry.Candidate.BarStepMm > 0)
                .OrderBy(entry => entry.Candidate.AsCoveredCm2PerM)
                .FirstOrDefault()?.Candidate;
            if (template == null || conflictGroup.Any(entry =>
                    entry.Candidate.BarStepMm != template.BarStepMm ||
                    !string.Equals(entry.Candidate.FamilyFileName, template.FamilyFileName,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(entry.Candidate.ConcreteClass, template.ConcreteClass,
                        StringComparison.OrdinalIgnoreCase)))
                return false;
            if (conflictGroup.Any(entry => !ReferenceEquals(entry.Candidate, template) &&
                                           !HasNarrowRemainderOutside(entry.Candidate, template)))
                return false;

            var direction = template.Direction;
            var supportMin = conflictGroup.Min(entry => entry.SupportCross.Min);
            var supportMax = conflictGroup.Max(entry => entry.SupportCross.Max);
            var patchMin = conflictGroup.Min(entry => CrossInterval(entry.Patch, direction).Min);
            var patchMax = conflictGroup.Max(entry => CrossInterval(entry.Patch, direction).Max);
            var allowedMin = Math.Min(supportMin, patchMin - Math.Max(0, maxPatchOverrunM));
            var allowedMax = Math.Max(supportMax, patchMax + Math.Max(0, maxPatchOverrunM));
            var stepUnits = (long)template.BarStepMm * CoordinateUnitsPerMm;
            var minWidthUnits = Math.Max(ToCoordinateUnits(supportMax - supportMin),
                MinimumWidthUnits(template, minimumWidthM));
            var widthUnits = ((minWidthUnits + stepUnits - 1) / stepUnits) * stepUnits;
            var allowedMinUnits = ToCoordinateUnits(allowedMin);
            var allowedMaxUnits = ToCoordinateUnits(allowedMax);
            var supportMinUnits = ToCoordinateUnits(supportMin);
            var supportMaxUnits = ToCoordinateUnits(supportMax);
            if (widthUnits > allowedMaxUnits - allowedMinUnits) return false;

            var lowestStart = Math.Max(allowedMinUnits, supportMaxUnits - widthUnits);
            var highestStart = Math.Min(supportMinUnits, allowedMaxUnits - widthUnits);
            if (lowestStart > highestStart) return false;
            var centeredStart = (supportMinUnits + supportMaxUnits - widthUnits) / 2;
            var crossStartUnits = Math.Max(lowestStart, Math.Min(highestStart, centeredStart));
            var crossMin = crossStartUnits / (double)GeometryScale;
            var crossMax = (crossStartUnits + widthUnits) / (double)GeometryScale;

            var axialStartMm = conflictGroup.Min(entry => AxialInterval(
                entry.Candidate.Contour, direction).Min) * 1000.0;
            var axialEndMm = conflictGroup.Max(entry => AxialInterval(
                entry.Candidate.Contour, direction).Max) * 1000.0;
            if (!TryBuildLapSegments(axialStartMm, axialEndMm, template, out var segments) ||
                segments.Count == 0 || segments.Count > conflictGroup.Count)
                return false;

            var orderedEntries = conflictGroup.OrderBy(entry => AxialInterval(
                    entry.Candidate.Contour, direction).Min)
                .ThenBy(entry => entry.Candidate.ZoneId).ToList();
            var nodeIds = conflictGroup.SelectMany(entry => entry.Candidate.NodeIds ?? new List<int>())
                .Distinct().ToList();
            var widthMm = widthUnits / (double)CoordinateUnitsPerMm;
            var lapOverlapMm = 2 * RebarTables.LapLenMm(
                template.ConcreteClass, template.DiameterMm);
            var proposed = new List<AdditionalZone>(segments.Count);
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var zone = CopyZone(template);
                SetRectangleBounds(zone, segment.StartMm / 1000.0,
                    segment.EndMm / 1000.0, crossMin, crossMax);
                zone.ZoneId = orderedEntries[i].Candidate.ZoneId;
                zone.LengthMm = segment.LengthMm;
                zone.LengthM = segment.LengthMm / 1000.0;
                zone.WidthMm = widthMm;
                zone.WidthM = widthMm / 1000.0;
                zone.BarCount = Math.Max(1,
                    (int)Math.Floor(widthMm / template.BarStepMm + 1e-9) + 1);
                zone.AsAdditional = requiredAs;
                zone.AsRequired = requiredAs + Background(settings, template.Layer);
                zone.NodeIds = nodeIds.ToList();
                zone.ElementId = zone.NodeIds.FirstOrDefault();
                var note = StripSegmentMetadata(template.Comment);
                var segmentNote = $"часть {i + 1}/{segments.Count}; нахлёст {lapOverlapMm} мм";
                zone.Comment = string.IsNullOrWhiteSpace(note) ? segmentNote : segmentNote + "; " + note;
                SetLayerAs(zone, zone.AsRequired);
                if (zone.LengthMm > 11700 + 1e-6 || !HasStepSizedWidth(zone) ||
                    openings != null && openings.Count > 0 &&
                    ZoneEditor.IntersectsOpening(zone, openings))
                    return false;
                proposed.Add(zone);
            }

            var groupCandidates = new HashSet<AdditionalZone>(
                conflictGroup.Select(entry => entry.Candidate));
            var trial = new List<AdditionalZone>(candidates.Count - conflictGroup.Count + proposed.Count);
            var inserted = false;
            foreach (var candidate in candidates)
            {
                if (!groupCandidates.Contains(candidate))
                    trial.Add(candidate);
                else if (!inserted)
                {
                    trial.AddRange(proposed);
                    inserted = true;
                }
            }
            if (!inserted || CountArrangementConflicts(trial) >= CountArrangementConflicts(candidates))
                return false;

            var coverage = ZoneLayoutDiagnostics.Evaluate(
                plates, trial, settings, 0, true, slabOutline, openings);
            var uncoveredIds = new HashSet<int>(coverage.Issues
                .Where(issue => issue.Kind == ZoneIssueKind.UncoveredElement)
                .Select(issue => issue.ElementId));
            if (uncoveredIds.Any(id => !previouslyUncoveredIds.Contains(id))) return false;

            foreach (var entry in conflictGroup) replacements[entry] = null;
            for (var i = 0; i < proposed.Count; i++)
                replacements[orderedEntries[i]] = proposed[i];
            collapsedCandidates = trial;
            return true;
        }

        private static bool SameAxialBand(AdditionalZone first, AdditionalZone second)
        {
            if (first.Layer != second.Layer || first.Direction != second.Direction ||
                first.FamilyKind != ZoneFamilyKind.Straight ||
                second.FamilyKind != ZoneFamilyKind.Straight ||
                first.BarStepMm != second.BarStepMm ||
                !string.Equals(first.FamilyFileName, second.FamilyFileName,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(first.ConcreteClass, second.ConcreteClass,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            var firstCross = CrossInterval(first.Contour, first.Direction);
            var secondCross = CrossInterval(second.Contour, second.Direction);
            return Math.Abs(firstCross.Min - secondCross.Min) <= 0.001 &&
                   Math.Abs(firstCross.Max - secondCross.Max) <= 0.001;
        }

        private static string StripSegmentMetadata(string? comment)
        {
            if (string.IsNullOrWhiteSpace(comment)) return string.Empty;
            return string.Join("; ", comment!.Split(';')
                .Select(part => part?.Trim() ?? string.Empty)
                .Where(part => !part.StartsWith("часть ", StringComparison.OrdinalIgnoreCase) &&
                               !part.StartsWith("нахлёст ", StringComparison.OrdinalIgnoreCase)));
        }

        private static bool TryBuildLapSegments(
            double startMm,
            double endMm,
            AdditionalZone template,
            out List<(double StartMm, double EndMm, double LengthMm)> segments)
        {
            segments = new List<(double StartMm, double EndMm, double LengthMm)>();
            const double maximumLengthMm = 11700;
            var lapMm = 2.0 * RebarTables.LapLenMm(template.ConcreteClass, template.DiameterMm);
            var advanceMm = maximumLengthMm - lapMm;
            if (endMm <= startMm || advanceMm <= 0) return false;

            var cursorMm = startMm;
            while (endMm - cursorMm > maximumLengthMm + 1e-6)
            {
                segments.Add((cursorMm, cursorMm + maximumLengthMm, maximumLengthMm));
                cursorMm += advanceMm;
            }
            segments.Add((cursorMm, endMm, 0));

            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var requiredLengthMm = segment.EndMm - segment.StartMm;
                var familyLengthMm = RebarTables.PickFamilyLength(requiredLengthMm);
                if (familyLengthMm <= 0 || familyLengthMm > maximumLengthMm) return false;
                var centerMm = (segment.StartMm + segment.EndMm) * 0.5;
                segments[i] = (centerMm - familyLengthMm * 0.5,
                    centerMm + familyLengthMm * 0.5, familyLengthMm);
            }

            for (var i = 1; i < segments.Count; i++)
                if (segments[i - 1].EndMm - segments[i].StartMm + 1e-6 < lapMm)
                    return false;
            return true;
        }

        private static void SetLayerAs(AdditionalZone zone, double value)
        {
            switch (zone.Layer)
            {
                case RebarLayer.As1: zone.Rebar.As1 = value; break;
                case RebarLayer.As2: zone.Rebar.As2 = value; break;
                case RebarLayer.As3: zone.Rebar.As3 = value; break;
                case RebarLayer.As4: zone.Rebar.As4 = value; break;
            }
        }

        private static List<List<ZoneEntry>> BuildConflictGroups(IList<ZoneEntry> entries)
        {
            var groups = new List<List<ZoneEntry>>();
            foreach (var bucket in entries.Where(entry => IsRectangle(entry.Candidate))
                         .GroupBy(entry => new
                         {
                             entry.Candidate.Layer,
                             entry.Candidate.Direction
                         }))
            {
                var pending = new HashSet<ZoneEntry>(bucket);
                while (pending.Count > 0)
                {
                    var seed = pending.First();
                    pending.Remove(seed);
                    var component = new List<ZoneEntry> { seed };
                    var frontier = new Queue<ZoneEntry>();
                    frontier.Enqueue(seed);
                    while (frontier.Count > 0)
                    {
                        var current = frontier.Dequeue();
                        foreach (var neighbor in pending.ToList())
                        {
                            if (!IsIntentionalLapPair(current.Candidate, neighbor.Candidate) &&
                                (!AxiallyOverlap(current, neighbor) ||
                                 !CrossBandsOverlap(current.OriginalCross, neighbor.OriginalCross)))
                                continue;
                            pending.Remove(neighbor);
                            component.Add(neighbor);
                            frontier.Enqueue(neighbor);
                        }
                    }

                    if (HasConflictPair(component)) groups.Add(component);
                }
            }
            return groups;
        }

        private static bool HasConflictPair(IList<ZoneEntry> group)
        {
            for (var i = 0; i < group.Count; i++)
            for (var j = i + 1; j < group.Count; j++)
                if (!IsIntentionalLapPair(group[i].Candidate, group[j].Candidate) &&
                    AxiallyOverlap(group[i], group[j]) &&
                    CrossBandsOverlap(group[i].OriginalCross, group[j].OriginalCross))
                    return true;
            return false;
        }

        private static double CrossIntervalGap((double Min, double Max) first, (double Min, double Max) second) =>
            Math.Max(0, Math.Max(first.Min, second.Min) - Math.Min(first.Max, second.Max));

        private static int CountArrangementConflicts(IList<AdditionalZone> zones) =>
            FindArrangementConflictPairs(zones).Count;

        private static List<(AdditionalZone First, AdditionalZone Second)> FindArrangementConflictPairs(
            IList<AdditionalZone> zones)
        {
            var pairs = new List<(AdditionalZone First, AdditionalZone Second)>();
            var rectangles = zones.Where(IsRectangle).ToList();
            for (var i = 0; i < rectangles.Count; i++)
            for (var j = i + 1; j < rectangles.Count; j++)
            {
                var first = rectangles[i];
                var second = rectangles[j];
                if (first.Layer != second.Layer || first.Direction != second.Direction ||
                    IsIntentionalLapPair(first, second) ||
                    !AxiallyOverlap(first, second) ||
                    !CrossBandsOverlap(CrossInterval(first.Contour, first.Direction),
                        CrossInterval(second.Contour, second.Direction)))
                    continue;
                pairs.Add((first, second));
            }
            return pairs;
        }

        private static List<List<ZoneEntry>> BuildArrangementGroups(IList<ZoneEntry> entries)
        {
            var buckets = entries.GroupBy(entry =>
            {
                var zone = entry.Candidate;
                return new
                {
                    zone.Layer,
                    zone.Direction
                };
            });
            var result = new List<List<ZoneEntry>>();
            foreach (var bucket in buckets)
            {
                var pending = new HashSet<ZoneEntry>(bucket);
                while (pending.Count > 0)
                {
                    var seed = pending.First();
                    pending.Remove(seed);
                    var component = new List<ZoneEntry> { seed };
                    var frontier = new Queue<ZoneEntry>();
                    frontier.Enqueue(seed);
                    while (frontier.Count > 0)
                    {
                        var current = frontier.Dequeue();
                        foreach (var neighbor in pending.ToList())
                        {
                            if (!BelongsToSameArrangementStrip(current, neighbor))
                                continue;
                            pending.Remove(neighbor);
                            component.Add(neighbor);
                            frontier.Enqueue(neighbor);
                        }
                    }
                    result.Add(component);
                }
            }
            return result;
        }

        private static bool BelongsToSameArrangementStrip(ZoneEntry first, ZoneEntry second)
        {
            if (IsIntentionalLapPair(first.Candidate, second.Candidate) ||
                !AxiallyOverlap(first, second)) return false;
            return CrossBandsTouchOrOverlap(first.SupportCross, second.SupportCross) ||
                   CrossBandsOverlap(first.OriginalCross, second.OriginalCross);
        }

        private static bool CrossBandsTouchOrOverlap(
            (double Min, double Max) first, (double Min, double Max) second)
        {
            const double toleranceM = 0.001;
            return first.Min <= second.Max + toleranceM &&
                   second.Min <= first.Max + toleranceM;
        }

        private static bool CrossBandsOverlap(
            (double Min, double Max) first, (double Min, double Max) second)
        {
            return Math.Min(first.Max, second.Max) - Math.Max(first.Min, second.Min) > GeometryToleranceM;
        }

        private static bool AxiallyOverlap(ZoneEntry first, ZoneEntry second)
        {
            return AxiallyOverlap(first.Candidate, second.Candidate);
        }

        private static bool AxiallyOverlap(AdditionalZone first, AdditionalZone second)
        {
            var firstAxial = AxialInterval(first.Contour, first.Direction);
            var secondAxial = AxialInterval(second.Contour, second.Direction);
            return Math.Min(firstAxial.Max, secondAxial.Max) -
                   Math.Max(firstAxial.Min, secondAxial.Min) > GeometryToleranceM;
        }

        private static bool HasArrangementConflicts(IList<AdditionalZone> zones)
        {
            var rectangles = zones.Where(IsRectangle).ToList();
            for (var i = 0; i < rectangles.Count; i++)
            for (var j = i + 1; j < rectangles.Count; j++)
            {
                var first = rectangles[i];
                var second = rectangles[j];
                if (first.Layer != second.Layer || first.Direction != second.Direction ||
                    IsIntentionalLapPair(first, second) || !AxiallyOverlap(first, second))
                    continue;

                if (CrossBandsOverlap(
                        CrossInterval(first.Contour, first.Direction),
                        CrossInterval(second.Contour, second.Direction)))
                    return true;
            }

            return false;
        }

        private static bool HasIntentionalLapPair(IList<ZoneEntry> entries)
        {
            for (var i = 0; i < entries.Count; i++)
            for (var j = i + 1; j < entries.Count; j++)
                if (IsIntentionalLapPair(entries[i].Candidate, entries[j].Candidate))
                    return true;
            return false;
        }

        private static bool IsIntentionalLapPair(AdditionalZone first, AdditionalZone second)
        {
            if (first.Layer != second.Layer || first.Direction != second.Direction ||
                first.DiameterMm != second.DiameterMm || first.BarStepMm != second.BarStepMm ||
                first.BarCount != second.BarCount || first.FamilyKind != second.FamilyKind ||
                !string.Equals(first.FamilyFileName, second.FamilyFileName, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(first.AsAdditional - second.AsAdditional) > 1e-6 ||
                first.NodeIds == null || second.NodeIds == null || first.NodeIds.Count == 0 ||
                first.NodeIds.Count != second.NodeIds.Count ||
                !new HashSet<int>(first.NodeIds).SetEquals(second.NodeIds) ||
                !TryGetSegmentRange(first, out var firstPart, out var firstTotal) ||
                !TryGetSegmentRange(second, out var secondPart, out var secondTotal) ||
                firstTotal != secondTotal || Math.Abs(firstPart - secondPart) != 1)
                return false;

            var firstCross = CrossInterval(first.Contour, first.Direction);
            var secondCross = CrossInterval(second.Contour, second.Direction);
            if (Math.Abs(firstCross.Min - secondCross.Min) > 0.001 ||
                Math.Abs(firstCross.Max - secondCross.Max) > 0.001)
                return false;

            var firstAxial = AxialInterval(first.Contour, first.Direction);
            var secondAxial = AxialInterval(second.Contour, second.Direction);
            return Math.Min(firstAxial.Max, secondAxial.Max) -
                   Math.Max(firstAxial.Min, secondAxial.Min) > GeometryToleranceM;
        }

        private static bool TryGetSegmentRange(AdditionalZone zone, out int part, out int total)
        {
            const string marker = "часть ";
            var comment = zone.Comment ?? string.Empty;
            var start = comment.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            part = 0;
            total = 0;
            if (start < 0) return false;
            start += marker.Length;
            var end = comment.IndexOf('/', start);
            if (end <= start || !int.TryParse(comment.Substring(start, end - start),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out part)) return false;
            var totalStart = end + 1;
            var totalEnd = totalStart;
            while (totalEnd < comment.Length && char.IsDigit(comment[totalEnd])) totalEnd++;
            return totalEnd > totalStart &&
                   int.TryParse(comment.Substring(totalStart, totalEnd - totalStart),
                       System.Globalization.NumberStyles.Integer,
                       System.Globalization.CultureInfo.InvariantCulture, out total) &&
                    part > 0 && total > 1 && total >= part;
        }

        private static bool TryArrangeAxialSweep(
            IList<ZoneEntry> zones,
            double minimumWidthM,
            double maxPatchOverrunM,
            out List<(double Min, double Max)> intervals,
            out bool minimumWidthInfeasible,
            out string failureReason,
            Func<IList<(double Min, double Max)>, bool>? validateArrangement = null)
        {
            intervals = new List<(double Min, double Max)>(zones.Count);
            minimumWidthInfeasible = false;
            failureReason = string.Empty;
            if (zones.Count == 0) return true;

            var direction = zones[0].Candidate.Direction;
            var sweepGroups = BuildSweepGroups(zones);
            var groupByEntry = new Dictionary<ZoneEntry, SweepGroup>();
            foreach (var group in sweepGroups)
                foreach (var member in group.Members)
                    groupByEntry.Add(member, group);

            var supportMin = zones.Min(entry => entry.SupportCross.Min);
            var supportMax = zones.Max(entry => entry.SupportCross.Max);
            var patchMin = zones.Min(entry => CrossInterval(entry.Patch, direction).Min);
            var patchMax = zones.Max(entry => CrossInterval(entry.Patch, direction).Max);
            var allowedMin = ToCoordinateUnits(Math.Min(supportMin, patchMin - maxPatchOverrunM));
            var allowedMax = ToCoordinateUnits(Math.Max(supportMax, patchMax + maxPatchOverrunM));
            var slots = new Dictionary<SweepGroup, (long Min, long Max, long MinWidth, long MaxWidth, long Step)>();
            foreach (var group in sweepGroups)
            {
                var zone = group.Members[0].Candidate;
                var step = (long)Math.Max(1, zone.BarStepMm) * CoordinateUnitsPerMm;
                var groupSupportMin = group.Members.Min(member => member.SupportCross.Min);
                var groupSupportMax = group.Members.Max(member => member.SupportCross.Max);
                var groupPatchMin = group.Members.Min(member =>
                    CrossInterval(member.Patch, direction).Min);
                var groupPatchMax = group.Members.Max(member =>
                    CrossInterval(member.Patch, direction).Max);
                var groupAllowedMin = ToCoordinateUnits(
                    Math.Min(groupSupportMin, groupPatchMin - maxPatchOverrunM));
                var groupAllowedMax = ToCoordinateUnits(
                    Math.Max(groupSupportMax, groupPatchMax + maxPatchOverrunM));
                var currentWidth = group.Members.Max(member =>
                {
                    var current = CrossInterval(member.Candidate.Contour, direction);
                    return ToCoordinateUnits(current.Max - current.Min);
                });
                var minWidth = group.Members.Max(member =>
                    MinimumWidthUnits(member.Candidate, minimumWidthM));
                var maxWidth = ((Math.Max(currentWidth, minWidth) + step - 1) / step) * step;
                if (groupAllowedMax - groupAllowedMin < minWidth)
                {
                    minimumWidthInfeasible = minWidth > groupAllowedMax - groupAllowedMin;
                    failureReason = minimumWidthInfeasible
                        ? $"зона Ø{zone.DiameterMm}/шаг {zone.BarStepMm} мм не помещается в разрешённую ширину"
                        : $"зона Ø{zone.DiameterMm}/шаг {zone.BarStepMm} мм шире общего разрешённого диапазона";
                    return false;
                }
                slots.Add(group, (groupAllowedMin, groupAllowedMax, minWidth, maxWidth, step));
            }

            var ordered = sweepGroups
                .Select(entry => new
                {
                    Entry = entry,
                    Degree = sweepGroups.Count(other => !ReferenceEquals(entry, other) &&
                        SweepGroupsOverlapAxially(entry, other) &&
                        CrossBandsOverlap(entry.OriginalCross, other.OriginalCross)),
                    entry.Axial
                })
                .OrderByDescending(item => item.Degree)
                .ThenByDescending(item => item.Entry.Members.Max(member => member.Candidate.AsAdditional))
                .ThenByDescending(item => slots[item.Entry].MaxWidth)
                .ThenBy(item => item.Axial.Min)
                .Select(item => item.Entry)
                .ToList();
            var assigned = new Dictionary<SweepGroup, (long Min, long Max)>();
            var maximumSearchNodes = Math.Max(5000, sweepGroups.Count * sweepGroups.Count * 1000);
            var visitedSearchNodes = 0;
            var rejectedCoverageCandidate = false;
            var maxAs = Math.Max(0.001, zones.Max(entry => entry.Candidate.AsAdditional));

            if (validateArrangement != null)
            {
                var crossOrdered = sweepGroups
                    .OrderBy(group => group.OriginalCross.Min)
                    .ThenBy(group => group.OriginalCross.Max)
                    .ThenBy(group => group.Axial.Min)
                    .ToList();

                bool TryPackWidths(
                    IDictionary<SweepGroup, long> widths,
                    out List<(double Min, double Max)> packedIntervals)
                {
                    packedIntervals = new List<(double Min, double Max)>();
                    var packed = new Dictionary<SweepGroup, (long Min, long Max)>();
                    foreach (var group in crossOrdered)
                    {
                        var slot = slots[group];
                        var width = widths[group];
                        var maxStart = slot.Max - width;
                        if (maxStart < slot.Min) return false;

                        var originalMin = ToCoordinateUnits(group.OriginalCross.Min);
                        var start = Math.Max(slot.Min, Math.Min(maxStart, originalMin));
                        foreach (var previous in packed)
                        {
                            if (!SweepGroupsOverlapAxially(group, previous.Key)) continue;
                            start = Math.Max(start, previous.Value.Max);
                        }
                        if (start > maxStart) return false;
                        packed[group] = (start, start + width);
                    }

                    var requiredShiftLeft = packed.Max(pair => Math.Max(
                        0, pair.Value.Max - slots[pair.Key].Max));
                    if (requiredShiftLeft > 0)
                    {
                        if (packed.Any(pair =>
                                pair.Value.Min - requiredShiftLeft < slots[pair.Key].Min))
                            return false;
                        foreach (var group in packed.Keys.ToList())
                            packed[group] = (packed[group].Min - requiredShiftLeft,
                                packed[group].Max - requiredShiftLeft);
                    }

                    packedIntervals = zones.Select(entry =>
                    {
                        var interval = packed[groupByEntry[entry]];
                        return (interval.Min / (double)GeometryScale,
                            interval.Max / (double)GeometryScale);
                    }).ToList();
                    return true;
                }

                var packedWidths = sweepGroups.ToDictionary(group => group,
                    group => slots[group].MaxWidth);
                while (true)
                {
                    var hasPackedLayout = TryPackWidths(packedWidths, out var packedIntervals);
                    if (hasPackedLayout &&
                        validateArrangement(packedIntervals))
                    {
                        intervals = packedIntervals;
                        return true;
                    }
                    if (hasPackedLayout) rejectedCoverageCandidate = true;

                    var shrinkGroup = crossOrdered
                        .Where(group => packedWidths[group] > slots[group].MinWidth)
                        .OrderBy(group => group.Members.Min(member =>
                            member.Candidate.AsAdditional))
                        .ThenByDescending(group => packedWidths[group] - slots[group].MinWidth)
                        .FirstOrDefault();
                    if (shrinkGroup == null) break;
                    packedWidths[shrinkGroup] -= slots[shrinkGroup].Step;
                }
            }

            bool Search(int index)
            {
                if (index == ordered.Count)
                {
                    var proposedIntervals = zones.Select(entry =>
                    {
                        var interval = assigned[groupByEntry[entry]];
                        return (interval.Min / (double)GeometryScale,
                            interval.Max / (double)GeometryScale);
                    }).ToList();
                    if (validateArrangement != null && !validateArrangement(proposedIntervals))
                    {
                        rejectedCoverageCandidate = true;
                        return false;
                    }
                    return true;
                }
                if (++visitedSearchNodes > maximumSearchNodes) return false;

                var entry = ordered[index];
                var slot = slots[entry];
                var original = entry.OriginalCross;
                var originalMin = ToCoordinateUnits(original.Min);
                var blockers = assigned.Where(pair =>
                    SweepGroupsOverlapAxially(entry, pair.Key)).ToList();
                var candidateOptions = new List<(long Start, long Width, long ConflictGap, double Movement)>();
                for (var width = slot.MaxWidth; width >= slot.MinWidth; width -= slot.Step)
                {
                    var maxStart = slot.Max - width;
                    if (maxStart < slot.Min) continue;
                    var priorGroupsWidth = ordered
                        .Where(other => !ReferenceEquals(other, entry) &&
                            CompareSweepGroupsCrossOrder(other, entry) < 0 &&
                            SweepGroupsOverlapAxially(entry, other))
                        .Sum(other => slots[other].MaxWidth);
                    var followingGroupsWidth = ordered
                        .Where(other => !ReferenceEquals(other, entry) &&
                            CompareSweepGroupsCrossOrder(other, entry) > 0 &&
                            SweepGroupsOverlapAxially(entry, other))
                        .Sum(other => slots[other].MaxWidth);
                    var candidateStarts = new HashSet<long>
                    {
                        slot.Min,
                        maxStart,
                        Math.Max(slot.Min, Math.Min(maxStart, originalMin)),
                        Math.Max(slot.Min, Math.Min(maxStart,
                            ToCoordinateUnits(original.Max) - width)),
                        Math.Max(slot.Min, Math.Min(maxStart,
                            (originalMin + ToCoordinateUnits(original.Max) - width) / 2)),
                        slot.Min + priorGroupsWidth,
                        slot.Max - width - followingGroupsWidth
                    };
                    foreach (var pair in assigned)
                    {
                        if (!SweepGroupsOverlapAxially(entry, pair.Key)) continue;
                        candidateStarts.Add(pair.Value.Min - width);
                        candidateStarts.Add(pair.Value.Max);
                    }

                    foreach (var start in candidateStarts
                        .Where(value => value >= slot.Min && value <= maxStart)
                        .Where(start => blockers.All(blocker =>
                        {
                            var order = CompareSweepGroupsCrossOrder(entry, blocker.Key);
                            if (order < 0) return start + width <= blocker.Value.Min;
                            if (order > 0) return start >= blocker.Value.Max;
                            return start + width <= blocker.Value.Min || start >= blocker.Value.Max;
                        })))
                    {
                        var peakAs = entry.Members.Max(member => member.Candidate.AsAdditional);
                        var peakWeight = 1.0 + 3.0 * Math.Max(0, peakAs) / maxAs;
                        var movement = (Math.Abs(start - originalMin) +
                            Math.Abs(start + width - ToCoordinateUnits(original.Max))) * peakWeight;
                        var conflictGap = blockers
                            .Where(blocker => CrossBandsOverlap(
                                entry.OriginalCross, blocker.Key.OriginalCross))
                            .Sum(blocker => Math.Max(0, Math.Max(
                                start - blocker.Value.Max,
                                blocker.Value.Min - (start + width))));
                        candidateOptions.Add((start, width, conflictGap, movement));
                    }
                    if (width - slot.Step < slot.MinWidth) break;
                }

                foreach (var option in candidateOptions
                    .OrderBy(candidate => -candidate.Width)
                    .ThenBy(candidate => candidate.ConflictGap)
                    .ThenBy(candidate => candidate.Movement))
                {
                    assigned[entry] = (option.Start, option.Start + option.Width);
                    if (Search(index + 1)) return true;
                    assigned.Remove(entry);
                    if (visitedSearchNodes > maximumSearchNodes) return false;
                }
                return false;
            }

            if (!Search(0))
            {
                if (visitedSearchNodes > maximumSearchNodes)
                {
                    if (TryArrangeEqualStepSweep(
                            zones, sweepGroups, slots, allowedMin, allowedMax, out intervals,
                            out var fallbackFailure) &&
                        (validateArrangement == null || validateArrangement(intervals)))
                        return true;
                    else if (!string.IsNullOrWhiteSpace(fallbackFailure))
                        failureReason = "Резервное размещение: " + fallbackFailure;
                }

                if (string.IsNullOrWhiteSpace(failureReason))
                    failureReason = rejectedCoverageCandidate
                    ? "все проверенные непересекающиеся положения ухудшают покрытие КЭ"
                    : visitedSearchNodes > maximumSearchNodes
                    ? "не найдено решение в пределах лимита поиска поперечных полос"
                    : "не удалось разместить зоны по конфликтующим продольным участкам";
                return false;
            }

            intervals = zones.Select(entry =>
            {
                var interval = assigned[groupByEntry[entry]];
                return (interval.Min / (double)GeometryScale, interval.Max / (double)GeometryScale);
            }).ToList();
            return true;
        }

        private static List<SweepGroup> BuildSweepGroups(IList<ZoneEntry> zones)
        {
            var groups = new List<SweepGroup>();
            var pending = new HashSet<ZoneEntry>(zones);
            while (pending.Count > 0)
            {
                var seed = pending.First();
                pending.Remove(seed);
                var members = new List<ZoneEntry> { seed };
                var frontier = new Queue<ZoneEntry>();
                frontier.Enqueue(seed);
                while (frontier.Count > 0)
                {
                    var current = frontier.Dequeue();
                    foreach (var neighbor in pending.ToList())
                    {
                        if (!IsIntentionalLapPair(current.Candidate, neighbor.Candidate)) continue;
                        pending.Remove(neighbor);
                        members.Add(neighbor);
                        frontier.Enqueue(neighbor);
                    }
                }

                var representative = members[0];
                groups.Add(new SweepGroup
                {
                    Members = members,
                    OriginalCross = representative.OriginalCross,
                    Axial = (
                        members.Min(member => AxialInterval(member.Candidate.Contour,
                            member.Candidate.Direction).Min),
                        members.Max(member => AxialInterval(member.Candidate.Contour,
                            member.Candidate.Direction).Max))
                });
            }

            return groups;
        }

        private static bool SweepGroupsOverlapAxially(SweepGroup first, SweepGroup second) =>
            Math.Min(first.Axial.Max, second.Axial.Max) -
            Math.Max(first.Axial.Min, second.Axial.Min) > GeometryToleranceM;

        private static int CompareSweepGroupsCrossOrder(SweepGroup first, SweepGroup second)
        {
            var minDelta = first.OriginalCross.Min - second.OriginalCross.Min;
            if (Math.Abs(minDelta) > GeometryToleranceM) return minDelta < 0 ? -1 : 1;
            var maxDelta = first.OriginalCross.Max - second.OriginalCross.Max;
            return Math.Abs(maxDelta) <= GeometryToleranceM ? 0 : maxDelta < 0 ? -1 : 1;
        }

        private static bool TryArrangeEqualStepSweep(
            IList<ZoneEntry> zones,
            IList<SweepGroup> groups,
            IDictionary<SweepGroup, (long Min, long Max, long MinWidth, long MaxWidth, long Step)> slots,
            long allowedMin,
            long allowedMax,
            out List<(double Min, double Max)> intervals,
            out string failureReason)
        {
            intervals = new List<(double Min, double Max)>();
            failureReason = string.Empty;
            if (groups.Count == 0) return true;

            var firstSlot = slots[groups[0]];
            if (groups.Any(group => slots[group].Step != firstSlot.Step ||
                                    slots[group].MinWidth != firstSlot.MinWidth))
            {
                failureReason = "у зон группы различаются шаг или минимальная ширина";
                return false;
            }

            var laneWidth = firstSlot.MinWidth;
            var laneCount = (int)((allowedMax - allowedMin) / laneWidth);
            if (laneCount <= 0)
            {
                failureReason = "коридор уже одной минимальной полосы";
                return false;
            }
            var assigned = new Dictionary<SweepGroup, (long Min, long Max)>();
            var unassigned = new HashSet<SweepGroup>(groups);
            var visitedLaneStates = 0;
            var maximumLaneStates = Math.Max(20000, groups.Count * groups.Count * 1000);
            var searchFailure = string.Empty;
            var maxAs = Math.Max(0.001,
                groups.Max(group => group.Members.Max(member => member.Candidate.AsAdditional)));

            List<(int Lane, long Min, long Max, long Span, double Movement, double CenterDelta)> GetLaneOptions(
                SweepGroup group)
            {
                var slot = slots[group];
                var active = assigned.Where(pair => SweepGroupsOverlapAxially(group, pair.Key))
                    .ToList();
                var originalMin = ToCoordinateUnits(group.OriginalCross.Min);
                var originalMax = ToCoordinateUnits(group.OriginalCross.Max);
                var options = new List<(int Lane, long Min, long Max, long Span, double Movement, double CenterDelta)>();
                for (var lane = 0; lane < laneCount; lane++)
                {
                    var start = allowedMin + lane * laneWidth;
                    var end = start + laneWidth;
                    if (start < slot.Min || end > slot.Max || active.Any(interval =>
                    {
                        var order = CompareSweepGroupsCrossOrder(group, interval.Key);
                        if (order < 0) return end > interval.Value.Min;
                        if (order > 0) return start < interval.Value.Max;
                        return start < interval.Value.Max && end > interval.Value.Min;
                    }))
                        continue;

                    var envelopeMin = active.Count == 0
                        ? start
                        : Math.Min(start, active.Min(interval => interval.Value.Min));
                    var envelopeMax = active.Count == 0
                        ? end
                        : Math.Max(end, active.Max(interval => interval.Value.Max));
                    var peakAs = group.Members.Max(member => member.Candidate.AsAdditional);
                    var movement = (Math.Abs(start - originalMin) + Math.Abs(end - originalMax)) *
                                   (1.0 + 3.0 * Math.Max(0, peakAs) / maxAs);
                    var centerDelta = Math.Abs((start + end) - (originalMin + originalMax));
                    options.Add((lane, start, end, envelopeMax - envelopeMin, movement, centerDelta));
                }

                return options.OrderBy(option => option.Span)
                    .ThenBy(option => option.CenterDelta)
                    .ThenBy(option => option.Movement)
                    .ThenBy(option => option.Lane)
                    .ToList();
            }

            bool AssignLanes()
            {
                if (unassigned.Count == 0) return true;
                if (++visitedLaneStates > maximumLaneStates)
                {
                    searchFailure = $"превышен лимит поиска полос ({maximumLaneStates} состояний)";
                    return false;
                }

                SweepGroup? next = null;
                List<(int Lane, long Min, long Max, long Span, double Movement, double CenterDelta)>? nextOptions = null;
                var nextDegree = -1;
                foreach (var group in unassigned)
                {
                    var options = GetLaneOptions(group);
                    if (options.Count == 0)
                    {
                        searchFailure = $"для участка [{group.Axial.Min:0.###};{group.Axial.Max:0.###}] м " +
                            $"нет свободной полосы шириной {laneWidth / (double)GeometryScale:0.###} м " +
                            $"из {laneCount} доступных";
                        return false;
                    }
                    var degree = groups.Count(other => !ReferenceEquals(group, other) &&
                        SweepGroupsOverlapAxially(group, other));
                    if (nextOptions != null && (options.Count > nextOptions.Count ||
                        options.Count == nextOptions.Count && degree <= nextDegree))
                        continue;
                    next = group;
                    nextOptions = options;
                    nextDegree = degree;
                }

                if (next == null || nextOptions == null) return false;
                foreach (var option in nextOptions)
                {
                    assigned[next] = (option.Min, option.Max);
                    unassigned.Remove(next);
                    if (AssignLanes()) return true;
                    unassigned.Add(next);
                    assigned.Remove(next);
                }
                return false;
            }

            if (!AssignLanes())
            {
                failureReason = string.IsNullOrWhiteSpace(searchFailure)
                    ? "не удалось согласовать набор полос без наложений"
                    : searchFailure;
                return false;
            }

            foreach (var group in groups
                .OrderByDescending(item => item.Members.Max(member => member.Candidate.AsAdditional))
                .ThenByDescending(item => slots[item].MaxWidth))
            {
                var slot = slots[group];
                var step = slot.Step;
                while (assigned[group].Max - assigned[group].Min + step <= slot.MaxWidth)
                {
                    var current = assigned[group];
                    var options = new List<(long Min, long Max, double Movement)>();
                    var left = (Min: current.Min - step, Max: current.Max);
                    var right = (Min: current.Min, Max: current.Max + step);
                    foreach (var option in new[] { left, right })
                    {
                        if (option.Min < allowedMin || option.Max > allowedMax) continue;
                        if (option.Min < slot.Min || option.Max > slot.Max) continue;
                        if (assigned.Any(pair => !ReferenceEquals(pair.Key, group) &&
                            SweepGroupsOverlapAxially(group, pair.Key) &&
                            option.Max > pair.Value.Min && option.Min < pair.Value.Max))
                            continue;

                        var peakAs = group.Members.Max(member => member.Candidate.AsAdditional);
                        var peakWeight = 1.0 + 3.0 * Math.Max(0, peakAs) / maxAs;
                        var original = group.OriginalCross;
                        var movement = (Math.Abs(option.Min - ToCoordinateUnits(original.Min)) +
                            Math.Abs(option.Max - ToCoordinateUnits(original.Max))) * peakWeight;
                        options.Add((option.Min, option.Max, movement));
                    }

                    var best = options.OrderBy(option => option.Movement).FirstOrDefault();
                    if (options.Count == 0) break;
                    assigned[group] = (best.Min, best.Max);
                }
            }

            intervals = zones.Select(entry =>
            {
                var interval = assigned[groups.First(group => group.Members.Contains(entry))];
                return (interval.Min / (double)GeometryScale, interval.Max / (double)GeometryScale);
            }).ToList();
            return true;
        }

        private static bool TryArrangeStrip(
            IList<ZoneEntry> zones,
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out List<(double Min, double Max)> intervals,
            out bool minimumWidthInfeasible,
            out string failureReason)
        {
            intervals = new List<(double Min, double Max)>();
            minimumWidthInfeasible = false;
            failureReason = string.Empty;
            if (zones.Count == 0) return true;
            var direction = zones[0].Candidate.Direction;
            var layer = zones[0].Candidate.Layer;
            var supportMin = zones.Min(entry => entry.SupportCross.Min);
            var supportMax = zones.Max(entry => entry.SupportCross.Max);
            var patchMin = zones.Min(entry => CrossInterval(entry.Patch, direction).Min);
            var patchMax = zones.Max(entry => CrossInterval(entry.Patch, direction).Max);
            var allowedStart = Math.Min(supportMin, patchMin - maxPatchOverrunM);
            var allowedEnd = Math.Max(supportMax, patchMax + maxPatchOverrunM);
            var supportMinMm = ToCoordinateUnits(supportMin);
            var supportMaxMm = ToCoordinateUnits(supportMax);
            var allowedStartMm = ToCoordinateUnits(allowedStart);
            var allowedEndMm = ToCoordinateUnits(allowedEnd);
            if (allowedEndMm <= allowedStartMm || supportMaxMm <= supportMinMm)
            {
                failureReason = "пустой диапазон пятна или опоры";
                return false;
            }

            var axialMin = zones.Min(entry => AxialInterval(entry.Candidate.Contour, direction).Min);
            var axialMax = zones.Max(entry => AxialInterval(entry.Candidate.Contour, direction).Max);
            var active = plates.Where(plate => plate.Rebar.Ok &&
                    plate.Rebar.Get(layer) - Background(settings, layer) > DemandTolerance &&
                    plate.Contour != null && plate.Contour.Count >= 3)
                .Select(plate => new ElementBounds(plate, direction,
                    plate.Rebar.Get(layer) - Background(settings, layer)))
                .Where(element => element.AxialMax >= axialMin - GeometryToleranceM &&
                                  element.AxialMin <= axialMax + GeometryToleranceM &&
                                  element.CrossMax >= allowedStart - GeometryToleranceM &&
                                  element.CrossMin <= allowedEnd + GeometryToleranceM)
                .ToList();

            var cutPositions = new HashSet<long>();
            foreach (var element in active)
            foreach (var point in element.Plate.Contour)
            {
                var vertex = ToCoordinateUnits(CrossCoordinate(point, direction));
                cutPositions.Add(vertex);
            }
            foreach (var entry in zones)
            {
                cutPositions.Add(ToCoordinateUnits(entry.SupportCross.Min));
                cutPositions.Add(ToCoordinateUnits(entry.SupportCross.Max));
                cutPositions.Add(ToCoordinateUnits(entry.OriginalCross.Min));
                cutPositions.Add(ToCoordinateUnits(entry.OriginalCross.Max));
            }
            if (active.Count > 0)
            {
                var activeMin = Math.Max(allowedStart, active.Min(element => element.CrossMin));
                var activeMax = Math.Min(allowedEnd, active.Max(element => element.CrossMax));
                var cutStep = CoordinateUnitsPerMm;
                var cutOffset = (supportMinMm - ToCoordinateUnits(activeMin)) % cutStep;
                if (cutOffset < 0) cutOffset += cutStep;
                for (var position = ToCoordinateUnits(activeMin) + cutOffset;
                     position < ToCoordinateUnits(activeMax);
                     position += cutStep)
                    cutPositions.Add(position);
            }
            cutPositions.RemoveWhere(position => position <= allowedStartMm || position >= allowedEndMm);

            var maxStepWidth = allowedEndMm - allowedStartMm;
            var states = new Dictionary<long, LayoutState>();
            var startMinimum = allowedStartMm;
            var maxAs = Math.Max(0.001, zones.Max(entry => entry.Candidate.AsAdditional));
            var starts = new HashSet<long> { supportMinMm, startMinimum };
            var firstZone = zones[0].Candidate;
            var firstStep = (long)Math.Max(1, firstZone.BarStepMm) * CoordinateUnitsPerMm;
            var firstMinimumWidth = MinimumWidthUnits(firstZone, minimumWidthM);
            foreach (var cut in cutPositions)
            {
                var minimumStart = cut - firstMinimumWidth;
                var extraSteps = minimumStart > supportMinMm
                    ? (minimumStart - supportMinMm + firstStep - 1) / firstStep
                    : 0;
                var width = firstMinimumWidth + extraSteps * firstStep;
                var start = cut - width;
                if (width <= maxStepWidth && start >= startMinimum && start <= supportMinMm)
                    starts.Add(start);
            }
            var originalFirstStart = ToCoordinateUnits(zones[0].OriginalCross.Min);
            if (originalFirstStart >= startMinimum && originalFirstStart <= supportMinMm)
                starts.Add(originalFirstStart);
            foreach (var start in starts.Where(value => value >= startMinimum && value <= supportMinMm))
            {
                var state = new LayoutState
                {
                    PositionMm = start,
                    Cost = (supportMinMm - start) / (double)CoordinateUnitsPerMm * 4.0
                };
                states[start] = state;
            }

            for (var zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
            {
                var zone = zones[zoneIndex].Candidate;
                var step = (long)Math.Max(1, zone.BarStepMm) * CoordinateUnitsPerMm;
                var minimum = MinimumWidthUnits(zone, minimumWidthM);
                var next = new Dictionary<long, LayoutState>();
                var last = zoneIndex == zones.Count - 1;
                foreach (var state in states.Values)
                {
                    var maximumWidth = Math.Min(maxStepWidth, allowedEndMm - state.PositionMm);
                    for (var width = minimum; width <= maximumWidth; width += step)
                    {
                        var end = state.PositionMm + width;
                        if (last)
                        {
                            if (end < supportMaxMm || end > allowedEndMm) continue;
                        }
                        else if (!cutPositions.Contains(end))
                        {
                            continue;
                        }

                        var old = zones[zoneIndex].OriginalCross;
                        var oldMin = ToCoordinateUnits(old.Min);
                        var oldMax = ToCoordinateUnits(old.Max);
                        var peakWeight = 1.0 + 3.0 * Math.Max(0, zone.AsAdditional) / maxAs;
                        var movement = (Math.Abs(state.PositionMm - oldMin) + Math.Abs(end - oldMax)) /
                                       (double)CoordinateUnitsPerMm * peakWeight;
                        var boundaryPenalty = last
                            ? Math.Max(0, end - supportMaxMm) / (double)CoordinateUnitsPerMm * 0.25
                            : 0;
                        var candidate = new LayoutState
                        {
                            PositionMm = end,
                            Cost = state.Cost + movement + boundaryPenalty,
                            Previous = state,
                            ZoneStartMm = state.PositionMm,
                            ZoneEndMm = end
                        };
                        if (!next.TryGetValue(end, out var existing) || candidate.Cost < existing.Cost)
                            next[end] = candidate;
                    }
                }

                if (next.Count == 0)
                {
                    if (TryArrangeStripSequential(
                            zones, active, minimumWidthM,
                            allowedStartMm, allowedEndMm, supportMinMm, supportMaxMm,
                            out intervals))
                        return true;

                    var failedZone = zones[zoneIndex].Candidate;
                    var reachableCuts = cutPositions.Where(position =>
                    {
                        return states.Values.Any(state =>
                        {
                            var width = position - state.PositionMm;
                            var maxWidth = Math.Min(maxStepWidth, allowedEndMm - state.PositionMm);
                            return width >= minimum && width <= maxWidth && width % step == 0;
                        });
                    }).ToList();
                    failureReason = $"на зоне {zoneIndex + 1}/{zones.Count} (Ø{failedZone.DiameterMm}, шаг {failedZone.BarStepMm} мм) " +
                        $"нет границы кратной шагу: допустимых линий={reachableCuts.Count}";
                    return false;
                }
                states = next;
            }

            var best = states.Values.Where(state => state.PositionMm >= supportMaxMm &&
                                                     state.PositionMm <= allowedEndMm)
                .OrderBy(state => state.Cost).FirstOrDefault();
            if (best == null)
            {
                if (TryArrangeStripSequential(
                        zones, active, minimumWidthM,
                        allowedStartMm, allowedEndMm, supportMinMm, supportMaxMm,
                        out intervals))
                    return true;

                failureReason = "не найдено конечное положение, покрывающее опору последней зоны";
                return false;
            }
            var reversed = new List<(double Min, double Max)>();
            var cursor = best;
            while (cursor.Previous != null)
            {
                reversed.Add((cursor.ZoneStartMm / (double)GeometryScale,
                    cursor.ZoneEndMm / (double)GeometryScale));
                cursor = cursor.Previous;
            }
            reversed.Reverse();
            if (reversed.Count != zones.Count)
            {
                failureReason = "не удалось восстановить полный набор границ зон";
                return false;
            }
            intervals = reversed;
            return true;
        }

        private static bool TryArrangeStripSequential(
            IList<ZoneEntry> zones,
            IList<ElementBounds> elements,
            double minimumWidthM,
            long allowedStartUnits,
            long allowedEndUnits,
            long supportMinUnits,
            long supportMaxUnits,
            out List<(double Min, double Max)> intervals)
        {
            intervals = new List<(double Min, double Max)>(zones.Count);
            var widths = new List<long>(zones.Count);
            foreach (var entry in zones)
            {
                var zone = entry.Candidate;
                var stepUnits = (long)Math.Max(1, zone.BarStepMm) * CoordinateUnitsPerMm;
                var current = CrossInterval(zone.Contour, zone.Direction);
                var currentWidth = ToCoordinateUnits(current.Max - current.Min);
                var required = Math.Max(currentWidth, MinimumWidthUnits(zone, minimumWidthM));
                var width = ((required + stepUnits - 1) / stepUnits) * stepUnits;
                widths.Add(width);
            }

            var totalWidth = widths.Sum();
            var start = Math.Min(supportMinUnits, allowedEndUnits - totalWidth);
            if (start < allowedStartUnits || start > supportMinUnits ||
                start + totalWidth < supportMaxUnits || start + totalWidth > allowedEndUnits)
                return false;

            var position = start;
            for (var i = 0; i < zones.Count; i++)
            {
                var end = position + widths[i];
                intervals.Add((position / (double)GeometryScale, end / (double)GeometryScale));
                position = end;
            }

            return position >= supportMaxUnits && position <= allowedEndUnits;
        }

        private static long MinimumWidthUnits(AdditionalZone zone, double minimumWidthM)
        {
            var step = (long)Math.Max(1, zone.BarStepMm) * CoordinateUnitsPerMm;
            var steps = (long)Math.Ceiling(
                Math.Max(0, minimumWidthM) * GeometryScale / step - 1e-10);
            return Math.Max(1, steps) * step;
        }

        private sealed class ElementBounds
        {
            public LiraPlateElement Plate { get; }
            public double AdditionalAsCm2PerM { get; }
            public double CrossMin { get; }
            public double CrossMax { get; }
            public double AxialMin { get; }
            public double AxialMax { get; }

            public ElementBounds(LiraPlateElement plate, ZoneDirection direction, double additionalAsCm2PerM)
            {
                Plate = plate;
                AdditionalAsCm2PerM = additionalAsCm2PerM;
                CrossMin = plate.Contour.Min(point => CrossCoordinate(point, direction));
                CrossMax = plate.Contour.Max(point => CrossCoordinate(point, direction));
                var axial = AxialInterval(plate.Contour, direction);
                AxialMin = axial.Min;
                AxialMax = axial.Max;
            }
        }

        private static bool TryAbsorbConflictGroup(
            IList<ZoneEntry> group,
            AnalysisSettings settings,
            IList<OpeningInfo>? openings,
            double minimumWidthM,
            double maxPatchOverrunM,
            out ZoneEntry retainedEntry,
            out AdditionalZone absorbedZone,
            out string failureReason)
        {
            retainedEntry = null!;
            absorbedZone = null!;
            failureReason = string.Empty;
            if (group.Count < 2 || group.Any(entry =>
                    entry.Candidate.Layer != group[0].Candidate.Layer ||
                    entry.Candidate.Direction != group[0].Candidate.Direction ||
                    entry.Candidate.FamilyKind != ZoneFamilyKind.Straight ||
                    entry.Candidate.BarStepMm <= 0))
            {
                failureReason = "Нельзя объединить: различаются слой, направление или семейство зон.";
                return false;
            }

            var requiredAs = group.Max(entry => entry.Candidate.AsAdditional);
            var retained = group
                .Where(entry => entry.Candidate.AsCoveredCm2PerM + DemandTolerance >= requiredAs)
                .OrderByDescending(entry => entry.Candidate.AsAdditional)
                .ThenByDescending(entry => entry.Candidate.BarStepMm)
                .FirstOrDefault();
            if (retained == null)
            {
                failureReason = "Нельзя объединить: ни одна зона не обеспечивает максимальный As группы.";
                return false;
            }
            retainedEntry = retained;

            if (group.Any(entry => !ReferenceEquals(entry, retained) &&
                                   !HasNarrowRemainderOutside(entry.Candidate,
                                       retained.Candidate)))
            {
                failureReason = $"Нельзя объединить: после отделения слабой зоны её остаточная ширина " +
                                $"не меньше {NarrowZoneAbsorptionWidthM * 1000:0} мм.";
                return false;
            }

            var template = retainedEntry.Candidate;
            var direction = template.Direction;
            var supportMin = group.Min(entry => entry.SupportCross.Min);
            var supportMax = group.Max(entry => entry.SupportCross.Max);
            var stepUnits = (long)template.BarStepMm * CoordinateUnitsPerMm;
            var supportMinUnits = ToCoordinateUnits(supportMin);
            var supportMaxUnits = ToCoordinateUnits(supportMax);
            var supportWidthUnits = supportMaxUnits - supportMinUnits;
            var requiredWidthUnits = Math.Max(supportWidthUnits,
                MinimumWidthUnits(template, minimumWidthM));
            var widthUnits = ((requiredWidthUnits + stepUnits - 1) / stepUnits) * stepUnits;

            var firstAxial = group.Min(entry => AxialInterval(entry.Candidate.Contour, direction).Min);
            var lastAxial = group.Max(entry => AxialInterval(entry.Candidate.Contour, direction).Max);
            var requiredLengthMm = (lastAxial - firstAxial) * 1000.0;
            if (requiredLengthMm > 11700 + 1)
            {
                failureReason = "Нельзя объединить: продольная длина группы превышает 11700 мм.";
                return false;
            }
            var familyLengthMm = RebarTables.PickFamilyLength(requiredLengthMm);
            if (familyLengthMm <= 0 || familyLengthMm > 11700)
            {
                failureReason = "Нельзя объединить: не найдена эталонная длина стержня до 11700 мм.";
                return false;
            }

            var lowestStart = supportMaxUnits - widthUnits;
            var highestStart = supportMinUnits;
            var centeredStart = (supportMinUnits + supportMaxUnits - widthUnits) / 2;
            var crossStartUnits = Math.Max(lowestStart, Math.Min(highestStart, centeredStart));
            var crossMin = crossStartUnits / (double)GeometryScale;
            var crossMax = (crossStartUnits + widthUnits) / (double)GeometryScale;
            var axialCenter = (firstAxial + lastAxial) * 0.5;
            var axialHalfLength = familyLengthMm / 2000.0;
            var axialMin = axialCenter - axialHalfLength;
            var axialMax = axialCenter + axialHalfLength;

            absorbedZone = CopyZone(template);
            SetRectangleBounds(absorbedZone, axialMin, axialMax, crossMin, crossMax);
            absorbedZone.WidthMm = widthUnits / (double)CoordinateUnitsPerMm;
            absorbedZone.WidthM = absorbedZone.WidthMm / 1000.0;
            absorbedZone.LengthMm = familyLengthMm;
            absorbedZone.LengthM = familyLengthMm / 1000.0;
            absorbedZone.BarCount = Math.Max(1,
                (int)Math.Floor(absorbedZone.WidthMm / template.BarStepMm + 1e-9) + 1);
            absorbedZone.AsAdditional = requiredAs;
            absorbedZone.AsRequired = requiredAs + Background(settings, template.Layer);
            absorbedZone.NodeIds = group.SelectMany(entry => entry.Candidate.NodeIds ?? new List<int>())
                .Distinct().ToList();
            absorbedZone.ElementId = absorbedZone.NodeIds.FirstOrDefault();
            switch (absorbedZone.Layer)
            {
                case RebarLayer.As1: absorbedZone.Rebar.As1 = absorbedZone.AsRequired; break;
                case RebarLayer.As2: absorbedZone.Rebar.As2 = absorbedZone.AsRequired; break;
                case RebarLayer.As3: absorbedZone.Rebar.As3 = absorbedZone.AsRequired; break;
                case RebarLayer.As4: absorbedZone.Rebar.As4 = absorbedZone.AsRequired; break;
            }
            var note = $"объединено из {group.Count} зон: на сетке КЭ нет разреза с шириной, кратной шагу; пиковый КЭ оставлен целиком";
            absorbedZone.Comment = string.IsNullOrWhiteSpace(absorbedZone.Comment)
                ? note
                : absorbedZone.Comment + "; " + note;
            absorbedZone.StatusColor = "warn";
            if (openings != null && openings.Count > 0 &&
                ZoneEditor.IntersectsOpening(absorbedZone, openings))
            {
                failureReason = "Нельзя объединить: итоговая зона пересекает отверстие.";
                return false;
            }
            return true;
        }

        private static bool HasNarrowRemainderOutside(AdditionalZone donor, AdditionalZone absorber)
        {
            if (donor.Layer != absorber.Layer || donor.Direction != absorber.Direction ||
                !IsRectangle(donor) || !IsRectangle(absorber))
                return false;

            var donorCross = CrossInterval(donor.Contour, donor.Direction);
            var absorberCross = CrossInterval(absorber.Contour, absorber.Direction);
            var leftRemainder = Math.Max(0, Math.Min(donorCross.Max, absorberCross.Min) - donorCross.Min);
            var rightRemainder = Math.Max(0, donorCross.Max - Math.Max(donorCross.Min, absorberCross.Max));
            return leftRemainder + rightRemainder < NarrowZoneAbsorptionWidthM - GeometryToleranceM;
        }

        private static double ComputeUnboundedPatchOverrun(
            IEnumerable<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            double minimumWidthM)
        {
            var entries = zones.Where(zone => zone?.Contour != null && zone.Contour.Count >= 3).ToList();
            if (entries.Count == 0) return 1.0;

            var totalWidth = 0.0;
            var minCoordinate = double.PositiveInfinity;
            var maxCoordinate = double.NegativeInfinity;
            foreach (var zone in entries)
            {
                var current = CrossInterval(zone.Contour, zone.Direction);
                var support = sourceBoundsByZone != null &&
                              sourceBoundsByZone.TryGetValue(zone, out var supportBounds)
                    ? CrossInterval(supportBounds, zone.Direction)
                    : current;
                var patch = patchBoundsByZone != null &&
                            patchBoundsByZone.TryGetValue(zone, out var patchBounds)
                    ? CrossInterval(patchBounds, zone.Direction)
                    : support;
                var stepM = UnitConversion.MmToMeters(Math.Max(1, zone.BarStepMm));
                totalWidth += Math.Max(Math.Max(current.Max - current.Min,
                        support.Max - support.Min), Math.Max(Math.Max(0, minimumWidthM), stepM)) + stepM;
                minCoordinate = Math.Min(minCoordinate,
                    Math.Min(current.Min, Math.Min(support.Min, patch.Min)));
                maxCoordinate = Math.Max(maxCoordinate,
                    Math.Max(current.Max, Math.Max(support.Max, patch.Max)));
            }

            var envelope = maxCoordinate - minCoordinate + totalWidth + 1.0;
            return double.IsNaN(envelope) || double.IsInfinity(envelope) ? totalWidth + 1.0 :
                Math.Max(1.0, envelope);
        }

        private static void SetRectangleBounds(
            AdditionalZone zone, double axialMin, double axialMax, double crossMin, double crossMax)
        {
            var minX = zone.Direction == ZoneDirection.X ? axialMin : crossMin;
            var maxX = zone.Direction == ZoneDirection.X ? axialMax : crossMax;
            var minY = zone.Direction == ZoneDirection.X ? crossMin : axialMin;
            var maxY = zone.Direction == ZoneDirection.X ? crossMax : axialMax;
            var z = zone.LevelZM;
            zone.Contour = new List<Point3>
            {
                new Point3(minX, minY, z), new Point3(maxX, minY, z),
                new Point3(maxX, maxY, z), new Point3(minX, maxY, z)
            };
            zone.Placement = new Point3((minX + maxX) * 0.5, (minY + maxY) * 0.5, z);
        }

        private static void SetCrossInterval(AdditionalZone zone, (double Min, double Max) cross)
        {
            var bounds = Bounds(zone.Contour);
            var minX = bounds.MinX;
            var maxX = bounds.MaxX;
            var minY = bounds.MinY;
            var maxY = bounds.MaxY;
            if (zone.Direction == ZoneDirection.X)
            {
                minY = cross.Min;
                maxY = cross.Max;
            }
            else
            {
                minX = cross.Min;
                maxX = cross.Max;
            }

            var z = zone.Contour[0].Z;
            zone.Contour = new List<Point3>
            {
                new Point3(minX, minY, z), new Point3(maxX, minY, z),
                new Point3(maxX, maxY, z), new Point3(minX, maxY, z)
            };
            zone.Placement = new Point3((minX + maxX) * 0.5, (minY + maxY) * 0.5, zone.LevelZM);
            zone.WidthM = cross.Max - cross.Min;
            zone.WidthMm = zone.WidthM * 1000.0;
            zone.BarCount = Math.Max(1, (int)Math.Floor(zone.WidthMm / Math.Max(1, zone.BarStepMm) + 1e-9) + 1);
        }

        private static bool HasStepSizedWidth(AdditionalZone zone)
        {
            if (zone.BarStepMm <= 0) return false;
            var bars = zone.WidthMm / zone.BarStepMm;
            return Math.Abs(bars - Math.Round(bars)) <= 1e-6;
        }

        private static Dictionary<AdditionalZone, ZonePatchFrameBounds> CloneBoundsForZones(
            IList<AdditionalZone> sourceZones,
            IList<AdditionalZone> destinationZones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> boundsByZone)
        {
            var result = new Dictionary<AdditionalZone, ZonePatchFrameBounds>();
            for (var i = 0; i < Math.Min(sourceZones.Count, destinationZones.Count); i++)
                if (boundsByZone != null && boundsByZone.TryGetValue(sourceZones[i], out var bounds))
                    result[destinationZones[i]] = bounds;
            return result;
        }

        private static AdditionalZone CopyZone(AdditionalZone source) => new AdditionalZone
        {
            ZoneId = source.ZoneId,
            ElementId = source.ElementId,
            Layer = source.Layer,
            NodeIds = source.NodeIds?.ToList() ?? new List<int>(),
            Placement = new Point3(source.Placement.X, source.Placement.Y, source.Placement.Z),
            Contour = source.Contour?.Select(point => new Point3(point.X, point.Y, point.Z)).ToList() ?? new List<Point3>(),
            WidthM = source.WidthM,
            LengthM = source.LengthM,
            LevelZM = source.LevelZM,
            AsRequired = source.AsRequired,
            AsAdditional = source.AsAdditional,
            Rebar = new PlateReinforcement
            {
                As1 = source.Rebar.As1, As2 = source.Rebar.As2,
                As3 = source.Rebar.As3, As4 = source.Rebar.As4, Ok = source.Rebar.Ok
            },
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

        private static void AddWarning(IEnumerable<AdditionalZone> zones, string warning)
        {
            foreach (var zone in zones)
            {
                if (string.IsNullOrWhiteSpace(zone.Comment)) zone.Comment = warning;
                else if (!zone.Comment.Contains(warning)) zone.Comment += "; " + warning;
                zone.StatusColor = "warn";
            }
        }

        private static bool IsRectangle(AdditionalZone zone) => zone.Contour != null && zone.Contour.Count >= 4;

        private static long ToCoordinateUnits(double meters) => (long)Math.Round(
            meters * GeometryScale, MidpointRounding.AwayFromZero);

        private static double CrossCoordinate(Point3 point, ZoneDirection direction) =>
            direction == ZoneDirection.X ? point.Y : point.X;

        private static (double Min, double Max) CrossInterval(
            IList<Point3> contour, ZoneDirection direction) => direction == ZoneDirection.X
            ? (contour.Min(point => point.Y), contour.Max(point => point.Y))
            : (contour.Min(point => point.X), contour.Max(point => point.X));

        private static (double Min, double Max) CrossInterval(
            ZonePatchFrameBounds bounds, ZoneDirection direction) => direction == ZoneDirection.X
            ? (bounds.MinY, bounds.MaxY)
            : (bounds.MinX, bounds.MaxX);

        private static (double Min, double Max) AxialInterval(
            IList<Point3> contour, ZoneDirection direction) => direction == ZoneDirection.X
            ? (contour.Min(point => point.X), contour.Max(point => point.X))
            : (contour.Min(point => point.Y), contour.Max(point => point.Y));

        private static ZonePatchFrameBounds Bounds(IList<Point3> contour) => new ZonePatchFrameBounds(
            contour.Min(point => point.X), contour.Max(point => point.X),
            contour.Min(point => point.Y), contour.Max(point => point.Y));

        private static AnalysisSettings SettingsForLayer(AnalysisSettings settings, RebarLayer layer) =>
            new AnalysisSettings
            {
                ExcludedZoneDiametersMm = settings.ExcludedZoneDiametersMm?.ToList() ?? new List<int>(),
                AsMainCm2PerM = settings.AsMainCm2PerM,
                ShowAs1 = layer == RebarLayer.As1,
                ShowAs2 = layer == RebarLayer.As2,
                ShowAs3 = layer == RebarLayer.As3,
                ShowAs4 = layer == RebarLayer.As4,
                AsMainAs1 = settings.AsMainAs1,
                AsMainAs2 = settings.AsMainAs2,
                AsMainAs3 = settings.AsMainAs3,
                AsMainAs4 = settings.AsMainAs4,
                BgBottomDiameterMm = settings.BgBottomDiameterMm,
                BgBottomStepMm = settings.BgBottomStepMm,
                BgTopDiameterMm = settings.BgTopDiameterMm,
                BgTopStepMm = settings.BgTopStepMm,
                SlabSelected = settings.SlabSelected,
                PlacementMode = settings.PlacementMode,
                DesignOption = settings.DesignOption,
                FamilyStraight = settings.FamilyStraight,
                FamilyL = settings.FamilyL,
                FamilyPEqual = settings.FamilyPEqual,
                FamilyPDiff = settings.FamilyPDiff,
                FamilyBentStick = settings.FamilyBentStick,
                MinZoneWidthM = settings.EffectiveMinZoneWidthM,
                MaxZoneWidthM = settings.MaxZoneWidthM,
                MinZoneLengthM = settings.MinZoneLengthM,
                MinActiveElements = settings.MinActiveElements,
                VisualizationScale = settings.VisualizationScale,
                OffsetXM = settings.OffsetXM,
                OffsetYM = settings.OffsetYM,
                RotationDeg = settings.RotationDeg,
                TargetElevationZM = settings.TargetElevationZM,
                ModelPart = settings.ModelPart,
                LoadReinforcement = settings.LoadReinforcement,
                AutoLayout = settings.AutoLayout,
                DetailLevel = settings.DetailLevel,
                DetailSlider = settings.DetailSlider,
                BarStepMm = settings.BarStepMm,
                ConcreteClass = settings.ConcreteClass,
                AlphaCoef = settings.AlphaCoef,
                GridCellMm = settings.GridCellMm,
                SlabThicknessMm = settings.SlabThicknessMm,
                CoverTopMm = settings.CoverTopMm,
                CoverBottomMm = settings.CoverBottomMm,
                MaxDiameterMm = settings.MaxDiameterMm,
                ApplySlabBoundaryAndOpeningRules = settings.ApplySlabBoundaryAndOpeningRules,
                ApplyHoleRules = settings.ApplyHoleRules,
                ApplyBentRules = settings.ApplyBentRules,
                HoleIgnorePerpMm = settings.HoleIgnorePerpMm,
                EdgeOffsetMm = settings.EdgeOffsetMm,
                SlabEdgeInsetMm = settings.SlabEdgeInsetMm,
                AllowedAdditionalBarStepsMm = settings.AllowedAdditionalBarStepsMm?.ToList() ?? new List<int> { 100, 200 },
                UseBarStep100 = settings.UseBarStep100,
                ReverseZoneDirections = settings.ReverseZoneDirections
            };

        private static double Background(AnalysisSettings settings, RebarLayer layer) => layer switch
        {
            RebarLayer.As1 => settings.AsMainAs1,
            RebarLayer.As2 => settings.AsMainAs2,
            RebarLayer.As3 => settings.AsMainAs3,
            RebarLayer.As4 => settings.AsMainAs4,
            _ => 0
        };
    }
}
