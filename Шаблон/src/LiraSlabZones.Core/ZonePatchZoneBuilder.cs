using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public static class ZonePatchZoneBuilder
    {
        private sealed class DiameterStepOption
        {
            public int DiameterMm { get; set; }
            public int StepMm { get; set; }
            public double CapacityAs { get; set; }
            public bool MeetsDemand { get; set; }
        }

        public static List<AdditionalZone> Build(
            ZonePatchFrameSelection frame, double elevationZM, AnalysisSettings settings,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? sourceBoundsByZone = null,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone = null)
        {
            var zones = new List<AdditionalZone>();
            if (frame == null || settings == null || frame.Patches == null || frame.Patches.Count == 0)
                return zones;
            if (string.IsNullOrWhiteSpace(settings.ConcreteClass)) return zones;

            settings.SyncBackgroundAsFromBars();
            var concrete = RebarTables.NormalizeConcrete(settings.ConcreteClass);

            var frameBounds = new ZonePatchFrameBounds(frame.MinXM, frame.MaxXM, frame.MinYM, frame.MaxYM);
            if (frameBounds.MaxX <= frameBounds.MinX || frameBounds.MaxY <= frameBounds.MinY) return zones;

            foreach (var layerGroup in frame.Patches.GroupBy(patch => patch.Layer).OrderBy(g => g.Key))
            {
                var patches = layerGroup.ToList();
                if (!ZonePatchFramePartitioner.TryTrimToActiveCells(
                        frameBounds, patches, frame.Elements, out var layerBounds))
                    continue;
                var widthX = layerBounds.MaxX - layerBounds.MinX;
                var widthY = layerBounds.MaxY - layerBounds.MinY;
                if (widthX <= 0 || widthY <= 0) continue;

                var hasCellData = patches.Any(patch => patch.Cells != null && patch.Cells.Count > 0);
                var intersectingPatches = hasCellData
                    ? patches.Where(patch => patch.Cells != null &&
                                             patch.Cells.Any(cell => IntersectsFrame(cell, layerBounds))).ToList()
                    : patches.Where(patch => IntersectsFrame(patch, layerBounds)).ToList();
                if (intersectingPatches.Count == 0) continue;
                var patchBounds = new ZonePatchFrameBounds(
                    intersectingPatches.Min(patch => patch.MinXM),
                    intersectingPatches.Max(patch => patch.MaxXM),
                    intersectingPatches.Min(patch => patch.MinYM),
                    intersectingPatches.Max(patch => patch.MaxYM));

                var requiredAs = hasCellData
                    ? PeakDemand(intersectingPatches, layerBounds, settings.AveragePatchPeaks)
                    : intersectingPatches.Max(patch => patch.PeakAsAdditionalCm2PerM);
                if (requiredAs <= MosaicBuilder.PositiveResidualToleranceCm2PerM) continue;

                var minDiameter = BackgroundDiameter(settings, layerGroup.Key);
                var referenceStep = BackgroundStep(settings, layerGroup.Key);
                if (minDiameter <= 0 || referenceStep <= 0) continue;

                var option = SelectSmallestDiameter(requiredAs, settings, minDiameter);
                if (option == null || option.DiameterMm <= 0) continue;

                var direction = RebarTables.DirectionForLayer(layerGroup.Key, settings.ReverseZoneDirections);
                var zoneBounds = ExpandWidthToMinimum(
                    layerBounds, direction, settings.EffectiveMinZoneWidthM);
                var widthM = direction == ZoneDirection.X
                    ? zoneBounds.MaxY - zoneBounds.MinY
                    : zoneBounds.MaxX - zoneBounds.MinX;
                var widthMm = UnitConversion.MetersToMm(widthM);
                var barCount = Math.Max(1, (int)Math.Floor(widthMm / option.StepMm + 1e-9) + 1);
                var backgroundAs = BackgroundAs(settings, layerGroup.Key);
                var nodeIds = intersectingPatches.SelectMany(patch => patch.ElementIds).Distinct().ToList();
                var anchorageMm = RebarTables.AnchorageLenMm(concrete, option.DiameterMm);
                var coreStartMm = UnitConversion.MetersToMm(
                    direction == ZoneDirection.X ? zoneBounds.MinX : zoneBounds.MinY);
                var coreEndMm = UnitConversion.MetersToMm(
                    direction == ZoneDirection.X ? zoneBounds.MaxX : zoneBounds.MaxY);
                var startMm = coreStartMm - anchorageMm;
                var endMm = coreEndMm + anchorageMm;
                const double maxStraightLengthMm = 11700;
                var lapOverlapMm = 2.0 * RebarTables.LapLenMm(concrete, option.DiameterMm);
                var segments = RoundSegmentsToFamilyLengths(
                    SplitStraightLength(startMm, endMm, maxStraightLengthMm, lapOverlapMm));

                for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
                {
                    var segment = segments[segmentIndex];
                    var minX = direction == ZoneDirection.X
                        ? UnitConversion.MmToMeters(segment.StartMm) : zoneBounds.MinX;
                    var maxX = direction == ZoneDirection.X
                        ? UnitConversion.MmToMeters(segment.EndMm) : zoneBounds.MaxX;
                    var minY = direction == ZoneDirection.Y
                        ? UnitConversion.MmToMeters(segment.StartMm) : zoneBounds.MinY;
                    var maxY = direction == ZoneDirection.Y
                        ? UnitConversion.MmToMeters(segment.EndMm) : zoneBounds.MaxY;
                    var segmentLengthMm = segment.EndMm - segment.StartMm;
                    var anchorOutside =
                        (segmentIndex == 0 && AnchorEndOutsideOutline(
                            frame.SlabOutline, direction, startMm, zoneBounds, barCount)) ||
                        (segmentIndex == segments.Count - 1 && AnchorEndOutsideOutline(
                            frame.SlabOutline, direction, endMm, zoneBounds, barCount));
                    var standardizedEndOutside =
                        (segmentIndex == 0 && AnchorEndOutsideOutline(
                            frame.SlabOutline, direction, segment.StartMm, zoneBounds, barCount)) ||
                        (segmentIndex == segments.Count - 1 && AnchorEndOutsideOutline(
                            frame.SlabOutline, direction, segment.EndMm, zoneBounds, barCount));
                    var commentParts = new List<string>();
                    if (!option.MeetsDemand)
                        commentParts.Add("пик ΔAs превышает вместимость доступного Ø");
                    if (segments.Count > 1)
                        commentParts.Add($"часть {segmentIndex + 1}/{segments.Count}; нахлёст {lapOverlapMm:0} мм");
                    if (anchorOutside)
                        commentParts.Add("анкеровка выходит за контур плиты");
                    else if (standardizedEndOutside)
                        commentParts.Add("эталонная длина стержня выходит за контур плиты");

                    var rebar = new PlateReinforcement { Ok = true };
                    SetLayerAs(rebar, layerGroup.Key, requiredAs + backgroundAs);
                    zones.Add(new AdditionalZone
                    {
                        ZoneId = zones.Count + 1,
                        ElementId = nodeIds.FirstOrDefault(),
                        Layer = layerGroup.Key,
                        NodeIds = nodeIds,
                        Placement = new Point3((minX + maxX) / 2, (minY + maxY) / 2, elevationZM),
                        Contour = new List<Point3>
                        {
                            new Point3(minX, minY, elevationZM),
                            new Point3(maxX, minY, elevationZM),
                            new Point3(maxX, maxY, elevationZM),
                            new Point3(minX, maxY, elevationZM)
                        },
                        WidthM = widthM,
                        LengthM = UnitConversion.MmToMeters(segmentLengthMm),
                        LevelZM = elevationZM,
                        AsRequired = requiredAs + backgroundAs,
                        AsAdditional = requiredAs,
                        Rebar = rebar,
                        Comment = string.Join("; ", commentParts),
                        IsValid = option.MeetsDemand,
                        StatusColor = option.MeetsDemand && !anchorOutside && !standardizedEndOutside
                            ? "ok"
                            : "warn",
                        Direction = direction,
                        DiameterMm = option.DiameterMm,
                        BarStepMm = option.StepMm,
                        BarCount = barCount,
                        WidthMm = widthMm,
                        LengthMm = segmentLengthMm,
                        FamilyKind = ZoneFamilyKind.Straight,
                        FamilyFileName = settings.GetFamilyName(ZoneFamilyKind.Straight),
                        AsCoveredCm2PerM = option.CapacityAs,
                        ConcreteClass = concrete,
                        AlphaCoef = settings.AlphaCoef,
                        RotationDeg = direction == ZoneDirection.Y ? 90 : 0
                    });
                    sourceBoundsByZone?.Add(zones[zones.Count - 1], layerBounds);
                    patchBoundsByZone?.Add(zones[zones.Count - 1], patchBounds);
                }
            }

            return zones;
        }

        public static int NormalizeWidthsAndResolveOverlaps(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            double minimumWidthM,
            double maxPatchOverrunM = 0.2,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone = null)
        {
            if (zones == null || zones.Count == 0 || sourceBoundsByZone == null) return 0;

            const double toleranceM = 1e-8;
            var removed = new HashSet<AdditionalZone>();
            var resolved = new List<AdditionalZone>();
            var ordered = zones.Select((zone, index) => (Zone: zone, Index: index))
                .Where(item => IsAdjustable(item.Zone))
                .OrderByDescending(item => item.Zone.AsAdditional)
                .ThenBy(item => item.Index)
                .Select(item => item.Zone)
                .ToList();

            foreach (var zone in ordered)
            {
                if (!sourceBoundsByZone.TryGetValue(zone, out var supportBounds))
                    supportBounds = Bounds(zone.Contour);
                var patchBounds = patchBoundsByZone != null && patchBoundsByZone.TryGetValue(zone, out var outerBounds)
                    ? outerBounds
                    : supportBounds;

                var support = CrossInterval(supportBounds, zone.Direction);
                var patchCross = CrossInterval(patchBounds, zone.Direction);
                var stepM = UnitConversion.MmToMeters(zone.BarStepMm);
                var minimumM = Math.Max(stepM, RoundUpToStep(minimumWidthM, stepM));
                NormalizeZoneWidth(zone, support, patchCross, stepM, minimumM, maxPatchOverrunM);

                var blockers = resolved.Where(other => BlocksAcrossWidth(zone, other, toleranceM))
                    .ToList();
                if (blockers.Count == 0)
                {
                    resolved.Add(zone);
                    continue;
                }

                var blockedIntervals = blockers.Select(other => CrossInterval(other.Contour, zone.Direction))
                    .ToList();
                var uncovered = SubtractIntervals(support, blockedIntervals, toleranceM);
                if (uncovered.Count == 0)
                {
                    if (IsSupportCoveredByZones(supportBounds, zone, resolved, toleranceM))
                        removed.Add(zone);
                    else
                    {
                        AddCoverageWarning(zone);
                        resolved.Add(zone);
                    }
                    continue;
                }

                var target = uncovered
                    .OrderByDescending(interval => interval.Max - interval.Min)
                    .ThenBy(interval => Math.Abs((interval.Min + interval.Max) * 0.5 -
                                                 (support.Min + support.Max) * 0.5))
                    .First();
                var requiredWidth = RoundUpToStep(
                    Math.Max(target.Max - target.Min, minimumM), stepM);
                var allowed = (Min: patchCross.Min - maxPatchOverrunM,
                    Max: patchCross.Max + maxPatchOverrunM);
                var freeIntervals = SubtractIntervals(allowed, blockedIntervals, toleranceM);
                var placement = FindPlacement(freeIntervals, target, requiredWidth, zone, toleranceM);
                if (placement.HasValue)
                {
                    var previous = CrossInterval(zone.Contour, zone.Direction);
                    SetCrossInterval(zone, placement.Value);
                    if (!IsSupportCoveredByZones(supportBounds, zone, resolved.Append(zone), toleranceM))
                    {
                        SetCrossInterval(zone, previous);
                        AddCoverageWarning(zone);
                    }
                    resolved.Add(zone);
                    continue;
                }

                if (TryPlaceStepRoundedZone(zone, support, target, blockers, blockedIntervals,
                        freeIntervals, minimumM, minimumWidthM, stepM, sourceBoundsByZone,
                        patchBoundsByZone, supportBounds, resolved, maxPatchOverrunM, toleranceM))
                    continue;

                var absorbed = false;
                foreach (var absorber in blockers
                    .OrderBy(other => DistanceToInterval(CrossInterval(other.Contour, zone.Direction), target))
                    .ThenByDescending(other => other.AsAdditional))
                {
                    if (ExpandNeighborIntoRemovedZone(absorber, zone, target, sourceBoundsByZone,
                            patchBoundsByZone, resolved, minimumWidthM, maxPatchOverrunM, toleranceM))
                    {
                        absorbed = true;
                        break;
                    }
                }
                if (absorbed && IsSupportCoveredByZones(supportBounds, zone, resolved, toleranceM))
                    removed.Add(zone);
                else
                {
                    AddCoverageWarning(zone);
                    resolved.Add(zone);
                }
            }

            for (var i = zones.Count - 1; i >= 0; i--)
                if (removed.Contains(zones[i])) zones.RemoveAt(i);
            return removed.Count;
        }

        public static int MergeAdjacentCompatibleZones(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? supportBoundsByZone = null,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone = null)
        {
            if (zones == null || zones.Count < 2) return 0;

            const double coordinateToleranceM = 1e-6;
            const double lengthToleranceMm = 1.0;
            var mergeCount = 0;
            var merged = true;
            while (merged)
            {
                merged = false;
                for (var i = 0; i < zones.Count && !merged; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var first = zones[i];
                    var second = zones[j];
                    if (!SameBarParameters(first, second, lengthToleranceMm) ||
                        !ShareFullLengthSide(first, second, coordinateToleranceM))
                        continue;

                    var combined = ZoneEditor.Merge(first, second, Array.Empty<Point3>(), clipToSlab: false);
                    if (combined == null || combined.LengthMm > 11700 + lengthToleranceMm ||
                        Math.Abs(combined.LengthMm - first.LengthMm) > lengthToleranceMm ||
                        Math.Abs(combined.WidthMm / first.BarStepMm -
                                 Math.Round(combined.WidthMm / first.BarStepMm)) > 1e-6)
                        continue;

                    var supportUnion = TryGetUnionBounds(
                        supportBoundsByZone, first, second, out var supports)
                        ? supports
                        : (ZonePatchFrameBounds?)null;
                    var patchUnion = TryGetUnionBounds(
                        patchBoundsByZone, first, second, out var patches)
                        ? patches
                        : (ZonePatchFrameBounds?)null;
                    zones[i] = combined;
                    zones.RemoveAt(j);
                    TransferMergedBounds(supportBoundsByZone, first, second, combined, supportUnion);
                    TransferMergedBounds(patchBoundsByZone, first, second, combined, patchUnion);
                    mergeCount++;
                    merged = true;
                    break;
                }
            }

            return mergeCount;
        }

        public static int MergeShiftableAdjacentZonesAlongBars(
            IList<AdditionalZone> zones,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> supportBoundsByZone,
            AnalysisSettings settings,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone = null,
            IList<LiraPlateElement>? plates = null,
            IList<Point3>? slabOutline = null,
            IList<OpeningInfo>? openings = null)
        {
            if (zones == null || zones.Count < 2 || supportBoundsByZone == null || settings == null)
                return 0;

            const double toleranceM = 1e-6;
            const double toleranceMm = 1.0;
            var maximumSupportGapM = Math.Max(0, settings.GridCellMm) / 1000.0 + toleranceM;
            var mergeCount = 0;
            var merged = true;
            while (merged)
            {
                merged = false;
                for (var i = 0; i < zones.Count && !merged; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var first = zones[i];
                    var second = zones[j];
                    if (!IsAdjustable(first) || !IsAdjustable(second) ||
                        !SameBarParameters(first, second, toleranceMm) ||
                        !supportBoundsByZone.TryGetValue(first, out var firstSupport) ||
                        !supportBoundsByZone.TryGetValue(second, out var secondSupport))
                        continue;
                    var anchorageMm = RebarTables.AnchorageLenMm(
                        settings.ConcreteClass, first.DiameterMm);

                    var firstZoneBounds = Bounds(first.Contour);
                    var secondZoneBounds = Bounds(second.Contour);
                    var firstCross = CrossInterval(firstZoneBounds, first.Direction);
                    var secondCross = CrossInterval(secondZoneBounds, second.Direction);
                    if (Math.Abs(firstCross.Min - secondCross.Min) > toleranceM ||
                        Math.Abs(firstCross.Max - secondCross.Max) > toleranceM)
                    {
                        if (!TryMergeShiftedCrossAdjacentZones(
                                zones, first, second, supportBoundsByZone, patchBoundsByZone,
                                settings, plates, slabOutline, openings, maximumSupportGapM,
                                toleranceM, toleranceMm))
                            continue;
                        if (patchBoundsByZone != null &&
                            patchBoundsByZone.TryGetValue(first, out var mergedPatch))
                            patchBoundsByZone[first] = mergedPatch;
                        supportBoundsByZone.Remove(second);
                        patchBoundsByZone?.Remove(second);
                        zones.RemoveAt(j);
                        mergeCount++;
                        merged = true;
                        break;
                    }

                    var firstSupportCross = CrossInterval(firstSupport, first.Direction);
                    var secondSupportCross = CrossInterval(secondSupport, second.Direction);
                    if (firstSupportCross.Min < firstCross.Min - toleranceM ||
                        firstSupportCross.Max > firstCross.Max + toleranceM ||
                        secondSupportCross.Min < firstCross.Min - toleranceM ||
                        secondSupportCross.Max > firstCross.Max + toleranceM ||
                        IntervalGap(firstSupportCross, secondSupportCross) > maximumSupportGapM)
                        continue;

                    var firstAxial = AxialInterval(firstSupport, first.Direction);
                    var secondAxial = AxialInterval(secondSupport, second.Direction);
                    if (IntervalGap(firstAxial, secondAxial) > maximumSupportGapM)
                        continue;

                    var requiredStartMm = UnitConversion.MetersToMm(
                        Math.Min(firstAxial.Min, secondAxial.Min)) - anchorageMm;
                    var requiredEndMm = UnitConversion.MetersToMm(
                        Math.Max(firstAxial.Max, secondAxial.Max)) + anchorageMm;
                    var requiredLengthMm = requiredEndMm - requiredStartMm;
                    var familyLengthMm = Math.Min(first.LengthMm, second.LengthMm);
                    if (requiredLengthMm >= familyLengthMm - toleranceMm ||
                        BarCapacity.AsCm2PerM(first.DiameterMm, first.BarStepMm) + 1e-6 <
                        Math.Max(first.AsAdditional, second.AsAdditional))
                        continue;

                    var allowedStartMm = requiredEndMm - familyLengthMm;
                    var preferredStartMm = (
                        UnitConversion.MetersToMm(AxialInterval(firstZoneBounds, first.Direction).Min) +
                        UnitConversion.MetersToMm(AxialInterval(secondZoneBounds, second.Direction).Min)) * 0.5;
                    var startMm = Math.Max(allowedStartMm,
                        Math.Min(requiredStartMm, preferredStartMm));
                    var endMm = startMm + familyLengthMm;
                    var minX = first.Direction == ZoneDirection.X
                        ? UnitConversion.MmToMeters(startMm) : firstCross.Min;
                    var maxX = first.Direction == ZoneDirection.X
                        ? UnitConversion.MmToMeters(endMm) : firstCross.Max;
                    var minY = first.Direction == ZoneDirection.Y
                        ? UnitConversion.MmToMeters(startMm) : firstCross.Min;
                    var maxY = first.Direction == ZoneDirection.Y
                        ? UnitConversion.MmToMeters(endMm) : firstCross.Max;

                    first.Contour = new List<Point3>
                    {
                        new Point3(minX, minY, first.LevelZM),
                        new Point3(maxX, minY, first.LevelZM),
                        new Point3(maxX, maxY, first.LevelZM),
                        new Point3(minX, maxY, first.LevelZM)
                    };
                    first.Placement = new Point3(
                        (minX + maxX) * 0.5, (minY + maxY) * 0.5, first.LevelZM);
                    first.NodeIds = (first.NodeIds ?? new List<int>())
                        .Concat(second.NodeIds ?? new List<int>()).Distinct().ToList();
                    first.ElementId = first.NodeIds.FirstOrDefault();
                    first.AsAdditional = Math.Max(first.AsAdditional, second.AsAdditional);
                    first.AsRequired = Math.Max(first.AsRequired, second.AsRequired);
                    first.IsValid = first.IsValid && second.IsValid;
                    if (first.StatusColor != "ok" || second.StatusColor != "ok")
                        first.StatusColor = "warn";
                    first.Comment = string.IsNullOrWhiteSpace(first.Comment)
                        ? "объединено со сдвигом; анкеровка сохранена"
                        : first.Comment + "; объединено со сдвигом, анкеровка сохранена";
                    first.LengthM = UnitConversion.MmToMeters(familyLengthMm);
                    first.LengthMm = familyLengthMm;
                    var combinedRebar = new PlateReinforcement { Ok = true };
                    SetLayerAs(combinedRebar, first.Layer, first.AsRequired);
                    first.Rebar = combinedRebar;

                    supportBoundsByZone[first] = new ZonePatchFrameBounds(
                        Math.Min(firstSupport.MinX, secondSupport.MinX),
                        Math.Max(firstSupport.MaxX, secondSupport.MaxX),
                        Math.Min(firstSupport.MinY, secondSupport.MinY),
                        Math.Max(firstSupport.MaxY, secondSupport.MaxY));
                    supportBoundsByZone.Remove(second);
                    zones.RemoveAt(j);
                    mergeCount++;
                    merged = true;
                    break;
                }
            }

            return mergeCount;
        }

        private static bool TryMergeShiftedCrossAdjacentZones(
            IList<AdditionalZone> zones,
            AdditionalZone first,
            AdditionalZone second,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> supportBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            AnalysisSettings settings,
            IList<LiraPlateElement>? plates,
            IList<Point3>? slabOutline,
            IList<OpeningInfo>? openings,
            double maximumGapM,
            double toleranceM,
            double toleranceMm)
        {
            if (!supportBoundsByZone.TryGetValue(first, out var firstSupport) ||
                !supportBoundsByZone.TryGetValue(second, out var secondSupport))
                return false;

            var firstBounds = Bounds(first.Contour);
            var secondBounds = Bounds(second.Contour);
            var firstCross = CrossInterval(firstBounds, first.Direction);
            var secondCross = CrossInterval(secondBounds, second.Direction);
            if (IntervalGap(firstCross, secondCross) > maximumGapM + toleranceM)
                return false;

            var firstAxial = AxialInterval(firstSupport, first.Direction);
            var secondAxial = AxialInterval(secondSupport, second.Direction);
            if (IntervalGap(firstAxial, secondAxial) > maximumGapM + toleranceM)
                return false;

            var familyLengthMm = Math.Min(first.LengthMm, second.LengthMm);
            if (familyLengthMm > 11700 + toleranceMm ||
                Math.Abs(first.LengthMm - second.LengthMm) > toleranceMm)
                return false;

            var anchorageMm = RebarTables.AnchorageLenMm(settings.ConcreteClass, first.DiameterMm);
            var requiredAxialMinMm = UnitConversion.MetersToMm(
                Math.Min(firstAxial.Min, secondAxial.Min)) - anchorageMm;
            var requiredAxialMaxMm = UnitConversion.MetersToMm(
                Math.Max(firstAxial.Max, secondAxial.Max)) + anchorageMm;
            if (requiredAxialMaxMm - requiredAxialMinMm > familyLengthMm + toleranceMm)
                return false;

            var allowedStartMinMm = requiredAxialMaxMm - familyLengthMm;
            var allowedStartMaxMm = requiredAxialMinMm;
            var preferredStartMm = (
                UnitConversion.MetersToMm(AxialInterval(firstBounds, first.Direction).Min) +
                UnitConversion.MetersToMm(AxialInterval(secondBounds, second.Direction).Min)) * 0.5;
            var startMm = Math.Max(allowedStartMinMm, Math.Min(allowedStartMaxMm, preferredStartMm));
            var endMm = startMm + familyLengthMm;

            var supportUnion = UnionBounds(firstSupport, secondSupport);
            var requiredCross = (
                Min: Math.Min(Math.Min(firstCross.Min, secondCross.Min),
                    CrossInterval(supportUnion, first.Direction).Min),
                Max: Math.Max(Math.Max(firstCross.Max, secondCross.Max),
                    CrossInterval(supportUnion, first.Direction).Max));
            var stepM = first.BarStepMm / 1000.0;
            var targetWidthM = Math.Ceiling(
                (requiredCross.Max - requiredCross.Min - 1e-9) / stepM) * stepM;
            var patchUnion = patchBoundsByZone != null &&
                patchBoundsByZone.TryGetValue(first, out var firstPatch) &&
                patchBoundsByZone.TryGetValue(second, out var secondPatch)
                    ? UnionBounds(firstPatch, secondPatch)
                    : supportUnion;
            var patchCross = CrossInterval(patchUnion, first.Direction);
            var allowedCrossMin = patchCross.Min - maximumGapM;
            var allowedCrossMax = patchCross.Max + maximumGapM;
            var minStart = Math.Max(allowedCrossMin, requiredCross.Max - targetWidthM);
            var maxStart = Math.Min(allowedCrossMax - targetWidthM, requiredCross.Min);
            if (targetWidthM < stepM - toleranceM || minStart > maxStart + toleranceM)
                return false;

            var preferredCrossStart = Math.Min(firstCross.Min, secondCross.Min);
            var crossStart = Math.Max(minStart, Math.Min(maxStart, preferredCrossStart));
            var cross = (Min: crossStart, Max: crossStart + targetWidthM);
            var z = first.LevelZM;
            var minX = first.Direction == ZoneDirection.X
                ? UnitConversion.MmToMeters(startMm) : cross.Min;
            var maxX = first.Direction == ZoneDirection.X
                ? UnitConversion.MmToMeters(endMm) : cross.Max;
            var minY = first.Direction == ZoneDirection.Y
                ? UnitConversion.MmToMeters(startMm) : cross.Min;
            var maxY = first.Direction == ZoneDirection.Y
                ? UnitConversion.MmToMeters(endMm) : cross.Max;
            var candidate = new AdditionalZone
            {
                ZoneId = first.ZoneId,
                ElementId = first.ElementId,
                Layer = first.Layer,
                Direction = first.Direction,
                DiameterMm = first.DiameterMm,
                BarStepMm = first.BarStepMm,
                BarCount = Math.Max(1, (int)Math.Floor(targetWidthM * 1000 / first.BarStepMm + 1e-9) + 1),
                WidthM = targetWidthM,
                WidthMm = UnitConversion.MetersToMm(targetWidthM),
                LengthM = UnitConversion.MmToMeters(familyLengthMm),
                LengthMm = familyLengthMm,
                LevelZM = z,
                Placement = new Point3((minX + maxX) * 0.5, (minY + maxY) * 0.5, z),
                Contour = new List<Point3>
                {
                    new Point3(minX, minY, z), new Point3(maxX, minY, z),
                    new Point3(maxX, maxY, z), new Point3(minX, maxY, z)
                },
                NodeIds = first.NodeIds.Concat(second.NodeIds).Distinct().ToList(),
                AsAdditional = Math.Max(first.AsAdditional, second.AsAdditional),
                AsRequired = Math.Max(first.AsRequired, second.AsRequired),
                AsCoveredCm2PerM = Math.Min(first.AsCoveredCm2PerM, second.AsCoveredCm2PerM),
                Rebar = new PlateReinforcement { Ok = true },
                IsValid = first.IsValid && second.IsValid,
                StatusColor = first.StatusColor == "ok" && second.StatusColor == "ok" ? "ok" : "warn",
                Comment = string.IsNullOrWhiteSpace(first.Comment)
                    ? "смежные зоны выровнены с сохранением анкеровки и объединены"
                    : first.Comment + "; смежные зоны выровнены с сохранением анкеровки и объединены",
                FamilyKind = first.FamilyKind,
                FamilyFileName = first.FamilyFileName,
                ConcreteClass = first.ConcreteClass,
                AlphaCoef = first.AlphaCoef,
                RotationDeg = first.RotationDeg
            };
            SetLayerAs(candidate.Rebar, candidate.Layer,
                candidate.AsRequired > 0 ? candidate.AsRequired : candidate.AsAdditional);

            var layerZones = zones.Where(zone => zone.Layer == first.Layer &&
                !ReferenceEquals(zone, first) && !ReferenceEquals(zone, second)).ToList();
            if (layerZones.Any(other => ZoneEditor.HasPlacementConflict(candidate, other) &&
                !ZoneEditor.HasPlacementConflict(first, other) &&
                !ZoneEditor.HasPlacementConflict(second, other)))
                return false;

            if (plates != null)
            {
                var previousPair = new List<AdditionalZone> { first, second };
                var proposed = layerZones.Concat(new[] { candidate }).ToList();
                foreach (var plate in plates.Where(plate => plate.Rebar.Ok &&
                             plate.Rebar.Get(first.Layer) - BackgroundAs(settings, first.Layer) >
                             MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                             ZoneCoverageRules.CoversOrBridgesGap(previousPair, plate, first.Layer,
                                 plate.Rebar.Get(first.Layer) - BackgroundAs(settings, first.Layer),
                                 slabOutline, openings)))
                {
                    var requiredAs = plate.Rebar.Get(first.Layer) - BackgroundAs(settings, first.Layer);
                    if (!ZoneCoverageRules.CoversOrBridgesGap(proposed, plate, first.Layer,
                            requiredAs, slabOutline, openings))
                        return false;
                }
            }

            first.Contour = candidate.Contour;
            first.Placement = candidate.Placement;
            first.WidthM = candidate.WidthM;
            first.WidthMm = candidate.WidthMm;
            first.LengthM = candidate.LengthM;
            first.LengthMm = candidate.LengthMm;
            first.BarCount = candidate.BarCount;
            first.NodeIds = candidate.NodeIds;
            first.ElementId = first.NodeIds.FirstOrDefault();
            first.AsAdditional = candidate.AsAdditional;
            first.AsRequired = candidate.AsRequired;
            first.AsCoveredCm2PerM = candidate.AsCoveredCm2PerM;
            first.Rebar = candidate.Rebar;
            first.IsValid = candidate.IsValid;
            first.StatusColor = candidate.StatusColor;
            first.Comment = candidate.Comment;

            supportBoundsByZone[first] = supportUnion;
            if (patchBoundsByZone != null)
                patchBoundsByZone[first] = patchUnion;
            return true;
        }

        private static bool TryGetUnionBounds(
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? boundsByZone,
            AdditionalZone first,
            AdditionalZone second,
            out ZonePatchFrameBounds union)
        {
            if (boundsByZone != null && boundsByZone.TryGetValue(first, out var firstBounds) &&
                boundsByZone.TryGetValue(second, out var secondBounds))
            {
                union = UnionBounds(firstBounds, secondBounds);
                return true;
            }

            union = default;
            return false;
        }

        private static void TransferMergedBounds(
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? boundsByZone,
            AdditionalZone first,
            AdditionalZone second,
            AdditionalZone merged,
            ZonePatchFrameBounds? union)
        {
            if (boundsByZone == null) return;
            boundsByZone.Remove(first);
            boundsByZone.Remove(second);
            if (union.HasValue) boundsByZone[merged] = union.Value;
        }

        private static double PeakDemand(
            IEnumerable<ZonePatch> patches, ZonePatchFrameBounds bounds, bool averagePeaks)
        {
            var cellsByIndex = new Dictionary<(int X, int Y), double>();
            foreach (var cell in patches.SelectMany(patch => patch.Cells ?? new List<ZonePatchCell>())
                         .Where(cell => IntersectsFrame(cell, bounds)))
            {
                var value = cell.RawAsAdditionalCm2PerM ?? cell.AsAdditionalCm2PerM;
                var key = (cell.Ix, cell.Iy);
                if (!cellsByIndex.TryGetValue(key, out var current) || value > current)
                    cellsByIndex[key] = value;
            }

            if (cellsByIndex.Count == 0) return 0;
            if (!averagePeaks) return cellsByIndex.Values.Max();

            var peak = 0.0;
            foreach (var cell in cellsByIndex)
            {
                var sum = 0.0;
                var count = 0;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!cellsByIndex.TryGetValue((cell.Key.X + dx, cell.Key.Y + dy), out var value))
                        continue;
                    sum += value;
                    count++;
                }
                if (count > 0) peak = Math.Max(peak, sum / count);
            }
            return peak;
        }

        private static bool SameBarParameters(
            AdditionalZone first, AdditionalZone second, double lengthToleranceMm) =>
            first.Layer == second.Layer &&
            first.Direction == second.Direction &&
            first.DiameterMm == second.DiameterMm &&
            first.BarStepMm == second.BarStepMm &&
            first.FamilyKind == second.FamilyKind &&
            string.Equals(first.FamilyFileName, second.FamilyFileName, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(first.LengthMm - second.LengthMm) <= lengthToleranceMm;

        private static bool ShareFullLengthSide(
            AdditionalZone first, AdditionalZone second, double toleranceM)
        {
            if (first.Contour == null || first.Contour.Count < 3 ||
                second.Contour == null || second.Contour.Count < 3)
                return false;

            var firstMinX = first.Contour.Min(point => point.X);
            var firstMaxX = first.Contour.Max(point => point.X);
            var firstMinY = first.Contour.Min(point => point.Y);
            var firstMaxY = first.Contour.Max(point => point.Y);
            var secondMinX = second.Contour.Min(point => point.X);
            var secondMaxX = second.Contour.Max(point => point.X);
            var secondMinY = second.Contour.Min(point => point.Y);
            var secondMaxY = second.Contour.Max(point => point.Y);

            if (first.Direction == ZoneDirection.X)
            {
                var sameRun = Math.Abs(firstMinX - secondMinX) <= toleranceM &&
                              Math.Abs(firstMaxX - secondMaxX) <= toleranceM;
                var touchOrOverlapAcrossWidth =
                    Math.Min(firstMaxY, secondMaxY) >= Math.Max(firstMinY, secondMinY) - toleranceM;
                return sameRun && touchOrOverlapAcrossWidth;
            }

            var sameRunY = Math.Abs(firstMinY - secondMinY) <= toleranceM &&
                           Math.Abs(firstMaxY - secondMaxY) <= toleranceM;
            var touchOrOverlapAcrossWidthX =
                Math.Min(firstMaxX, secondMaxX) >= Math.Max(firstMinX, secondMinX) - toleranceM;
            return sameRunY && touchOrOverlapAcrossWidthX;
        }

        private static DiameterStepOption? SelectSmallestDiameter(
            double requiredAs, AnalysisSettings settings, int minDiameterMm)
        {
            var maxDiameter = settings.MaxDiameterMm > 0 ? settings.MaxDiameterMm : 36;
            var excluded = settings.ExcludedZoneDiametersMm?.ToArray();
            var steps = (settings.AllowedAdditionalBarStepsMm ?? new List<int> { 100, 200 })
                .Where(step => step == 100 || step == 200)
                .Distinct()
                .OrderByDescending(step => step)
                .ToArray();
            if (steps.Length == 0) steps = new[] { 200 };
            var options = steps
                .Select(step =>
                {
                    var diameter = BarCapacity.MinDiameterForAs(
                        requiredAs, step, maxDiameter, minDiameterMm, excluded);
                    var capacity = diameter > 0 ? BarCapacity.AsCm2PerM(diameter, step) : 0;
                    return new DiameterStepOption
                    {
                        DiameterMm = diameter,
                        StepMm = step,
                        CapacityAs = capacity,
                        MeetsDemand = diameter > 0 && capacity + 1e-9 >= requiredAs
                    };
                })
                .Where(option => option.DiameterMm > 0)
                .ToList();

            return options.Where(option => option.MeetsDemand)
                       .OrderBy(option => option.CapacityAs)
                       .ThenByDescending(option => option.StepMm)
                       .ThenBy(option => option.DiameterMm)
                       .FirstOrDefault()
                   ?? options.OrderByDescending(option => option.CapacityAs)
                       .ThenBy(option => option.DiameterMm)
                       .ThenBy(option => option.StepMm)
                       .FirstOrDefault();
        }

        private static int BackgroundDiameter(AnalysisSettings settings, RebarLayer layer) =>
            layer is RebarLayer.As1 or RebarLayer.As2
                ? settings.BgBottomDiameterMm
                : settings.BgTopDiameterMm;

        private static int BackgroundStep(AnalysisSettings settings, RebarLayer layer) =>
            layer is RebarLayer.As1 or RebarLayer.As2
                ? settings.BgBottomStepMm
                : settings.BgTopStepMm;

        private static ZonePatchFrameBounds ExpandWidthToMinimum(
            ZonePatchFrameBounds bounds, ZoneDirection direction,
            double minimumWidthM)
        {
            var interval = CrossInterval(bounds, direction);
            var targetWidth = Math.Max(interval.Max - interval.Min, minimumWidthM);
            if (targetWidth <= interval.Max - interval.Min) return bounds;
            var centeredStart = (interval.Min + interval.Max - targetWidth) * 0.5;
            return WithCrossInterval(bounds, direction,
                (centeredStart, centeredStart + targetWidth));
        }

        private static bool IsAdjustable(AdditionalZone zone) =>
            zone != null && zone.FamilyKind == ZoneFamilyKind.Straight &&
            zone.BarStepMm > 0 && zone.Contour != null && zone.Contour.Count >= 3;

        private static bool BlocksAcrossWidth(
            AdditionalZone zone, AdditionalZone other, double toleranceM)
        {
            if (zone.Layer != other.Layer || zone.Direction != other.Direction ||
                !IsAdjustable(other)) return false;

            var zoneBounds = Bounds(zone.Contour);
            var otherBounds = Bounds(other.Contour);
            var alongZoneMin = zone.Direction == ZoneDirection.X ? zoneBounds.MinX : zoneBounds.MinY;
            var alongZoneMax = zone.Direction == ZoneDirection.X ? zoneBounds.MaxX : zoneBounds.MaxY;
            var alongOtherMin = other.Direction == ZoneDirection.X ? otherBounds.MinX : otherBounds.MinY;
            var alongOtherMax = other.Direction == ZoneDirection.X ? otherBounds.MaxX : otherBounds.MaxY;
            if (Math.Min(alongZoneMax, alongOtherMax) - Math.Max(alongZoneMin, alongOtherMin) <= toleranceM)
                return false;

            var first = CrossInterval(zone.Contour, zone.Direction);
            var second = CrossInterval(other.Contour, other.Direction);
            return first.Min < second.Max - toleranceM && second.Min < first.Max - toleranceM;
        }

        private static (double Min, double Max) CrossInterval(
            ZonePatchFrameBounds bounds, ZoneDirection direction) => direction == ZoneDirection.X
            ? (bounds.MinY, bounds.MaxY)
            : (bounds.MinX, bounds.MaxX);

        private static (double Min, double Max) AxialInterval(
            ZonePatchFrameBounds bounds, ZoneDirection direction) => direction == ZoneDirection.X
            ? (bounds.MinX, bounds.MaxX)
            : (bounds.MinY, bounds.MaxY);

        private static double IntervalGap((double Min, double Max) first, (double Min, double Max) second) =>
            first.Max < second.Min ? second.Min - first.Max :
            second.Max < first.Min ? first.Min - second.Max : 0;

        private static (double Min, double Max) CrossInterval(
            IList<Point3> contour, ZoneDirection direction) => direction == ZoneDirection.X
            ? (contour.Min(point => point.Y), contour.Max(point => point.Y))
            : (contour.Min(point => point.X), contour.Max(point => point.X));

        private static ZonePatchFrameBounds Bounds(IList<Point3> contour) => new ZonePatchFrameBounds(
            contour.Min(point => point.X), contour.Max(point => point.X),
            contour.Min(point => point.Y), contour.Max(point => point.Y));

        private static double RoundUpToStep(double value, double step) =>
            step <= 0 ? value : Math.Ceiling((value - 1e-9) / step) * step;

        private static double RoundDownToStep(double value, double step) =>
            step <= 0 ? value : Math.Floor((value + 1e-9) / step) * step;

        private static (double Min, double Max) PlaceContainingSupport(
            (double Min, double Max) support, double width,
            (double Min, double Max) preferred, (double Min, double Max) allowed)
        {
            var minimumStart = Math.Max(support.Max - width, allowed.Min);
            var maximumStart = Math.Min(support.Min, allowed.Max - width);
            if (minimumStart > maximumStart)
            {
                minimumStart = support.Min;
                maximumStart = support.Max - width;
            }

            var start = Math.Max(minimumStart, Math.Min(maximumStart, preferred.Min));
            return (start, start + width);
        }

        private static void NormalizeZoneWidth(
            AdditionalZone zone, (double Min, double Max) support,
            (double Min, double Max) patchCross,
            double stepM, double minimumWidthM, double maxOverrunM)
        {
            var current = CrossInterval(zone.Contour, zone.Direction);
            var target = RoundUpToStep(
                Math.Max(support.Max - support.Min, minimumWidthM), stepM);
            var maxTarget = RoundDownToStep(
                patchCross.Max - patchCross.Min + 2 * maxOverrunM, stepM);
            if (maxTarget >= support.Max - support.Min - 1e-9)
                target = Math.Min(target, maxTarget);
            var allowed = (Min: patchCross.Min - maxOverrunM,
                Max: patchCross.Max + maxOverrunM);
            var interval = PlaceContainingSupport(support, target, current, allowed);
            SetCrossInterval(zone, interval);
        }

        private static bool IsSupportCoveredByZones(
            ZonePatchFrameBounds support, AdditionalZone requiredZone,
            IEnumerable<AdditionalZone> zones, double toleranceM)
        {
            var rectangles = zones
                .Where(zone => zone.Layer == requiredZone.Layer &&
                               zone.Direction == requiredZone.Direction &&
                               zone.AsCoveredCm2PerM + 1e-9 >= requiredZone.AsAdditional &&
                               zone.Contour != null && zone.Contour.Count >= 3)
                .Select(zone => Bounds(zone.Contour))
                .Select(bounds => new ZonePatchFrameBounds(
                    Math.Max(support.MinX, bounds.MinX), Math.Min(support.MaxX, bounds.MaxX),
                    Math.Max(support.MinY, bounds.MinY), Math.Min(support.MaxY, bounds.MaxY)))
                .Where(bounds => bounds.MaxX > bounds.MinX + toleranceM &&
                                 bounds.MaxY > bounds.MinY + toleranceM)
                .ToList();

            var xCuts = new List<double> { support.MinX, support.MaxX };
            var yCuts = new List<double> { support.MinY, support.MaxY };
            foreach (var bounds in rectangles)
            {
                xCuts.Add(bounds.MinX);
                xCuts.Add(bounds.MaxX);
                yCuts.Add(bounds.MinY);
                yCuts.Add(bounds.MaxY);
            }
            xCuts = xCuts.Distinct().OrderBy(value => value).ToList();
            yCuts = yCuts.Distinct().OrderBy(value => value).ToList();

            for (var x = 0; x + 1 < xCuts.Count; x++)
            for (var y = 0; y + 1 < yCuts.Count; y++)
            {
                if (xCuts[x + 1] - xCuts[x] <= toleranceM ||
                    yCuts[y + 1] - yCuts[y] <= toleranceM)
                    continue;

                var midX = (xCuts[x] + xCuts[x + 1]) * 0.5;
                var midY = (yCuts[y] + yCuts[y + 1]) * 0.5;
                if (!rectangles.Any(bounds => midX >= bounds.MinX - toleranceM &&
                                              midX <= bounds.MaxX + toleranceM &&
                                              midY >= bounds.MinY - toleranceM &&
                                              midY <= bounds.MaxY + toleranceM))
                    return false;
            }

            return rectangles.Count > 0;
        }

        private static void AddCoverageWarning(AdditionalZone zone)
        {
            const string warning = "пересечение сохранено, чтобы не потерять покрытие КЭ";
            if (zone.Comment.IndexOf(warning, StringComparison.OrdinalIgnoreCase) < 0)
                zone.Comment = string.IsNullOrWhiteSpace(zone.Comment)
                    ? warning
                    : zone.Comment + "; " + warning;
            zone.StatusColor = "warn";
        }

        private static ZonePatchFrameBounds UnionBounds(
            ZonePatchFrameBounds first, ZonePatchFrameBounds second) => new ZonePatchFrameBounds(
            Math.Min(first.MinX, second.MinX), Math.Max(first.MaxX, second.MaxX),
            Math.Min(first.MinY, second.MinY), Math.Max(first.MaxY, second.MaxY));

        private static List<(double Min, double Max)> SubtractIntervals(
            (double Min, double Max) source,
            IEnumerable<(double Min, double Max)> blocked,
            double toleranceM)
        {
            var result = new List<(double Min, double Max)>();
            var cursor = source.Min;
            foreach (var interval in blocked
                         .Select(item => (Min: Math.Max(source.Min, item.Min), Max: Math.Min(source.Max, item.Max)))
                         .Where(item => item.Max > item.Min + toleranceM)
                         .OrderBy(item => item.Min))
            {
                if (interval.Min > cursor + toleranceM)
                    result.Add((cursor, interval.Min));
                cursor = Math.Max(cursor, interval.Max);
                if (cursor >= source.Max - toleranceM) break;
            }
            if (cursor < source.Max - toleranceM) result.Add((cursor, source.Max));
            return result;
        }

        private static (double Min, double Max)? FindPlacement(
            IList<(double Min, double Max)> freeIntervals,
            (double Min, double Max) required,
            double width,
            AdditionalZone zone,
            double toleranceM)
        {
            var current = CrossInterval(zone.Contour, zone.Direction);
            foreach (var free in freeIntervals
                         .Where(interval => interval.Max - interval.Min >= width - toleranceM)
                         .OrderByDescending(interval => OverlapLength(interval, required))
                         .ThenBy(interval => DistanceToInterval(interval, current)))
            {
                var minStart = Math.Max(free.Min, required.Max - width);
                var maxStart = Math.Min(free.Max - width, required.Min);
                var start = minStart <= maxStart
                    ? Math.Max(minStart, Math.Min(maxStart, current.Min))
                    : Math.Max(free.Min, Math.Min(free.Max - width,
                        (required.Min + required.Max - width) * 0.5));
                return (start, start + width);
            }
            return null;
        }

        private static double OverlapLength(
            (double Min, double Max) first, (double Min, double Max) second) =>
            Math.Max(0, Math.Min(first.Max, second.Max) - Math.Max(first.Min, second.Min));

        private static double DistanceToInterval(
            (double Min, double Max) interval, (double Min, double Max) target) =>
            interval.Max < target.Min ? target.Min - interval.Max :
            target.Max < interval.Min ? interval.Min - target.Max : 0;

        private static bool ExpandNeighborIntoRemovedZone(
            AdditionalZone neighbor, AdditionalZone removedZone,
            (double Min, double Max) uncovered,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            IList<AdditionalZone> resolved,
            double minimumWidthM, double maxOverrunM, double toleranceM)
        {
            var current = CrossInterval(neighbor.Contour, neighbor.Direction);
            var hasNeighborBounds = sourceBoundsByZone.TryGetValue(neighbor, out var neighborBounds);
            var hasRemovedBounds = sourceBoundsByZone.TryGetValue(removedZone, out var removedBounds);
            var source = hasNeighborBounds
                ? CrossInterval(neighborBounds, neighbor.Direction)
                : current;
            var removedSource = hasRemovedBounds
                ? CrossInterval(removedBounds, neighbor.Direction)
                : uncovered;
            var combined = (Min: Math.Min(source.Min, removedSource.Min),
                Max: Math.Max(source.Max, removedSource.Max));
            var required = (Min: Math.Min(current.Min, uncovered.Min),
                Max: Math.Max(current.Max, uncovered.Max));
            var stepM = UnitConversion.MmToMeters(neighbor.BarStepMm);
            if (stepM <= 0) return false;
            var needed = RoundUpToStep(Math.Max(
                required.Max - required.Min,
                Math.Max(current.Max - current.Min, minimumWidthM)), stepM);
            var neighborPatch = patchBoundsByZone != null && patchBoundsByZone.TryGetValue(neighbor, out var neighborOuter)
                ? neighborOuter
                : hasNeighborBounds ? neighborBounds : Bounds(neighbor.Contour);
            var removedPatch = patchBoundsByZone != null && patchBoundsByZone.TryGetValue(removedZone, out var removedOuter)
                ? removedOuter
                : hasRemovedBounds ? removedBounds : Bounds(removedZone.Contour);
            var allowedBounds = UnionBounds(neighborPatch, removedPatch);
            var allowedCross = CrossInterval(allowedBounds, neighbor.Direction);
            var allowed = (Min: allowedCross.Min - maxOverrunM,
                Max: allowedCross.Max + maxOverrunM);
            if (needed <= 0 || needed > allowed.Max - allowed.Min + toleranceM) return false;

            var minStart = Math.Max(allowed.Min, required.Max - needed);
            var maxStart = Math.Min(required.Min, allowed.Max - needed);
            if (minStart > maxStart + toleranceM) return false;
            var start = Math.Max(minStart, Math.Min(maxStart, current.Min));
            var expanded = (Min: start, Max: start + needed);
            if (expanded.Min > uncovered.Min + toleranceM ||
                expanded.Max < uncovered.Max - toleranceM)
                return false;

            if (resolved.Any(other => !ReferenceEquals(other, neighbor) &&
                    neighbor.Layer == other.Layer && neighbor.Direction == other.Direction &&
                    AxialOverlap(neighbor, other, toleranceM) &&
                    expanded.Min < CrossInterval(other.Contour, neighbor.Direction).Max - toleranceM &&
                    CrossInterval(other.Contour, neighbor.Direction).Min < expanded.Max - toleranceM))
                return false;

            SetCrossInterval(neighbor, expanded);
            if (hasNeighborBounds)
                sourceBoundsByZone[neighbor] = WithCrossInterval(neighborBounds, neighbor.Direction, combined);
            var oldNeighborPatchBounds = default(ZonePatchFrameBounds);
            var hadNeighborPatchBounds = patchBoundsByZone != null &&
                patchBoundsByZone.TryGetValue(neighbor, out oldNeighborPatchBounds);
            if (patchBoundsByZone != null && hadNeighborPatchBounds)
                patchBoundsByZone[neighbor] = allowedBounds;

            var supportBounds = hasRemovedBounds ? removedBounds : Bounds(removedZone.Contour);
            if (IsSupportCoveredByZones(supportBounds, removedZone, resolved, toleranceM))
                return true;

            SetCrossInterval(neighbor, current);
            if (hasNeighborBounds) sourceBoundsByZone[neighbor] = neighborBounds;
            if (patchBoundsByZone != null && hadNeighborPatchBounds)
                patchBoundsByZone[neighbor] = oldNeighborPatchBounds;
            return false;
        }

        // Keep the clipped band on its step grid; transfer a sub-step remainder to a neighbor.
        private static bool TryPlaceStepRoundedZone(
            AdditionalZone zone,
            (double Min, double Max) support,
            (double Min, double Max) required,
            IList<AdditionalZone> blockers,
            IList<(double Min, double Max)> blockedIntervals,
            IList<(double Min, double Max)> freeIntervals,
            double minimumZoneWidthM,
            double configuredMinimumWidthM,
            double stepM,
            IDictionary<AdditionalZone, ZonePatchFrameBounds> sourceBoundsByZone,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patchBoundsByZone,
            ZonePatchFrameBounds supportBounds,
            IList<AdditionalZone> resolved,
            double maxOverrunM,
            double toleranceM)
        {
            var current = CrossInterval(zone.Contour, zone.Direction);
            foreach (var free in freeIntervals
                         .Where(interval => OverlapLength(interval, required) > toleranceM)
                         .OrderByDescending(interval => OverlapLength(interval, required))
                         .ThenBy(interval => DistanceToInterval(interval, current)))
            {
                var width = RoundDownToStep(free.Max - free.Min, stepM);
                if (width < minimumZoneWidthM - toleranceM) continue;

                var placements = new[]
                    {
                        (Min: free.Min, Max: free.Min + width),
                        (Min: free.Max - width, Max: free.Max)
                    }
                    .Distinct()
                    .Where(interval => OverlapLength(interval, required) > toleranceM)
                    .OrderBy(interval => DistanceToInterval(interval, current))
                    .ToList();

                foreach (var placement in placements)
                {
                    var remaining = SubtractIntervals(support,
                        blockedIntervals.Append(placement), toleranceM);
                    if (remaining.Count == 0)
                    {
                        SetCrossInterval(zone, placement);
                        if (IsSupportCoveredByZones(supportBounds, zone, resolved.Append(zone), toleranceM))
                        {
                            resolved.Add(zone);
                            return true;
                        }
                        SetCrossInterval(zone, current);
                        continue;
                    }

                    if (remaining.Count != 1 ||
                        remaining[0].Max - remaining[0].Min >= minimumZoneWidthM - toleranceM)
                        continue;

                    var gap = remaining[0];
                    var adjacent = blockers
                        .Select(blocker => (Zone: blocker,
                            Interval: CrossInterval(blocker.Contour, zone.Direction)))
                        .Where(item => Math.Abs(item.Interval.Max - gap.Min) <= toleranceM ||
                                       Math.Abs(item.Interval.Min - gap.Max) <= toleranceM)
                        .OrderBy(item => DistanceToInterval(item.Interval, gap))
                        .ThenByDescending(item => item.Zone.AsAdditional)
                        .ToList();
                    if (adjacent.Count == 0) continue;

                    SetCrossInterval(zone, placement);
                    resolved.Add(zone);
                    foreach (var neighbor in adjacent)
                    {
                        if (ExpandNeighborIntoRemovedZone(neighbor.Zone, zone, gap,
                                sourceBoundsByZone, patchBoundsByZone, resolved, configuredMinimumWidthM,
                                maxOverrunM, toleranceM))
                            return true;
                    }
                    resolved.Remove(zone);
                    SetCrossInterval(zone, current);
                }
            }

            return false;
        }

        private static bool AxialOverlap(AdditionalZone first, AdditionalZone second, double toleranceM)
        {
            var firstBounds = Bounds(first.Contour);
            var secondBounds = Bounds(second.Contour);
            var firstMin = first.Direction == ZoneDirection.X ? firstBounds.MinX : firstBounds.MinY;
            var firstMax = first.Direction == ZoneDirection.X ? firstBounds.MaxX : firstBounds.MaxY;
            var secondMin = second.Direction == ZoneDirection.X ? secondBounds.MinX : secondBounds.MinY;
            var secondMax = second.Direction == ZoneDirection.X ? secondBounds.MaxX : secondBounds.MaxY;
            return Math.Min(firstMax, secondMax) - Math.Max(firstMin, secondMin) > toleranceM;
        }

        private static void SetCrossInterval(
            AdditionalZone zone, (double Min, double Max) cross)
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
            zone.WidthMm = UnitConversion.MetersToMm(zone.WidthM);
            zone.BarCount = Math.Max(1, (int)Math.Floor(zone.WidthMm / zone.BarStepMm + 1e-9) + 1);
        }

        private static ZonePatchFrameBounds WithCrossInterval(
            ZonePatchFrameBounds bounds, ZoneDirection direction,
            (double Min, double Max) cross) => direction == ZoneDirection.X
            ? new ZonePatchFrameBounds(bounds.MinX, bounds.MaxX, cross.Min, cross.Max)
            : new ZonePatchFrameBounds(cross.Min, cross.Max, bounds.MinY, bounds.MaxY);

        private static (double Min, double Max) CrossInterval(
            AdditionalZone zone) => CrossInterval(zone.Contour, zone.Direction);

        private static List<(double StartMm, double EndMm)> SplitStraightLength(
            double startMm, double endMm, double maxLengthMm, double lapOverlapMm)
        {
            var result = new List<(double StartMm, double EndMm)>();
            if (endMm <= startMm) return result;
            if (endMm - startMm <= maxLengthMm + 1e-6)
            {
                result.Add((startMm, endMm));
                return result;
            }

            var advanceMm = maxLengthMm - lapOverlapMm;
            if (advanceMm <= 0) return result;
            var segmentStartMm = startMm;
            while (endMm - segmentStartMm > maxLengthMm + 1e-6)
            {
                var segmentEndMm = segmentStartMm + maxLengthMm;
                result.Add((segmentStartMm, segmentEndMm));
                segmentStartMm += advanceMm;
            }
            result.Add((segmentStartMm, endMm));
            return result;
        }

        private static List<(double StartMm, double EndMm)> RoundSegmentsToFamilyLengths(
            IEnumerable<(double StartMm, double EndMm)> segments)
        {
            var result = new List<(double StartMm, double EndMm)>();
            foreach (var segment in segments)
            {
                var requiredLengthMm = segment.EndMm - segment.StartMm;
                var familyLengthMm = RebarTables.PickFamilyLength(requiredLengthMm);
                var centerMm = (segment.StartMm + segment.EndMm) * 0.5;
                result.Add((centerMm - familyLengthMm * 0.5, centerMm + familyLengthMm * 0.5));
            }
            return result;
        }

        private static bool AnchorEndOutsideOutline(
            IList<Point3>? outline, ZoneDirection direction, double endMm,
            ZonePatchFrameBounds coreBounds, int barCount)
        {
            if (outline == null || outline.Count < 3) return false;
            var coordinateCount = Math.Max(1, barCount);
            for (var i = 0; i < coordinateCount; i++)
            {
                var fraction = coordinateCount == 1 ? 0.5 : (double)i / (coordinateCount - 1);
                var cross = direction == ZoneDirection.X
                    ? coreBounds.MinY + (coreBounds.MaxY - coreBounds.MinY) * fraction
                    : coreBounds.MinX + (coreBounds.MaxX - coreBounds.MinX) * fraction;
                var x = direction == ZoneDirection.X ? UnitConversion.MmToMeters(endMm) : cross;
                var y = direction == ZoneDirection.Y ? UnitConversion.MmToMeters(endMm) : cross;
                if (!PointInsideOrOnOutline(x, y, outline)) return true;
            }
            return false;
        }

        private static bool PointInsideOrOnOutline(double x, double y, IList<Point3> outline)
        {
            if (MeshBoundary.PointInPolygon(x, y, outline)) return true;
            const double tolerance = 1e-8;
            for (var i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var lengthSquared = dx * dx + dy * dy;
                var t = lengthSquared <= 1e-18 ? 0 : Math.Max(0, Math.Min(1,
                    ((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared));
                var px = a.X + t * dx;
                var py = a.Y + t * dy;
                if ((x - px) * (x - px) + (y - py) * (y - py) <= tolerance * tolerance)
                    return true;
            }
            return false;
        }

        private static double BackgroundAs(AnalysisSettings settings, RebarLayer layer) => layer switch
        {
            RebarLayer.As1 => settings.AsMainAs1,
            RebarLayer.As2 => settings.AsMainAs2,
            RebarLayer.As3 => settings.AsMainAs3,
            RebarLayer.As4 => settings.AsMainAs4,
            _ => 0
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

        private static bool IntersectsFrame(ZonePatch patch, ZonePatchFrameBounds frame) =>
            patch.MinXM < frame.MaxX && patch.MaxXM > frame.MinX &&
            patch.MinYM < frame.MaxY && patch.MaxYM > frame.MinY;

        private static bool IntersectsFrame(ZonePatchCell cell, ZonePatchFrameBounds frame) =>
            cell.MinXM < frame.MaxX && cell.MaxXM > frame.MinX &&
            cell.MinYM < frame.MaxY && cell.MaxYM > frame.MinY;
    }
}
