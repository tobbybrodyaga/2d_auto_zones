using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public sealed class SlabZoneAnalyzer
    {
        public AnalysisResult Analyze(string? lirPath, AnalysisSettings settings)
        {
            settings ??= new AnalysisSettings();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var geometry = new LiraGeometryReader();
            geometry.ModelPart = LiraGeometryReader.ParseModelPart(settings.ModelPart);
            geometry.AttachToRunningOrOpen(lirPath);

            var (nodes, platesRaw) = geometry.ReadNodesAndPlates();
            var platesAll = MeshBoundary.FilterHorizontalPlates(platesRaw);
            var axes = geometry.ReadConstructionAxes();
            var marks = geometry.ReadElevationMarks();
            var levels = MeshBoundary.CollectLevels(platesAll, marks);

            using var rebar = new LiraReinforcementReader();
            var fromPath = System.IO.Path.GetFileNameWithoutExtension(geometry.DocumentPath);
            var fromTitle = geometry.DocumentName;
            var docName = !string.IsNullOrWhiteSpace(fromPath) ? fromPath : fromTitle;

            if (settings.LoadReinforcement)
            {
                var nameForApi = string.IsNullOrWhiteSpace(fromTitle) ||
                                 string.Equals(fromTitle, docName, StringComparison.OrdinalIgnoreCase)
                    ? docName
                    : fromTitle + "|" + docName;
                // As на все уровни — чтобы смена отметки не теряла армирование
                rebar.FillPlateReinforcement(nameForApi, platesAll, settings.DesignOption);
            }
            else
            {
                foreach (var p in platesAll)
                    p.Rebar = new PlateReinforcement { Ok = false };
            }

            var (plates, elevZ) = SelectLevel(platesAll, settings, levels);
            var elevLabel = LiraGeometryReader.FormatElevationLabel(elevZ, marks);
            var geoMs = sw.ElapsedMilliseconds;

            var result = BuildResult(docName, geometry.DocumentPath, nodes.Count, plates, settings, axes, elevZ, elevLabel, skipLevelFilter: true);
            result.AllPlates = platesAll;
            result.AvailableLevels = levels;
            result.UnitsNote +=
                $" | plates={platesRaw.Count}->{platesAll.Count} horiz | levels={platesAll.Count}->{plates.Count} {elevLabel} {geometry.LastAxesDiagnostics} geo={geoMs}ms total={sw.ElapsedMilliseconds}ms | " +
                (settings.LoadReinforcement ? rebar.LastDiagnostics : "As отключён");
            return result;
        }

        /// <summary>
        /// Переключить отметку без повторного чтения ЛИРА (по AllPlates).
        /// </summary>
        public static AnalysisResult RebuildForElevation(AnalysisResult source, double targetZM, AnalysisSettings? settings = null)
        {
            settings ??= source.Settings ?? new AnalysisSettings();
            settings.TargetElevationZM = targetZM;
            var all = source.AllPlates != null && source.AllPlates.Count > 0
                ? source.AllPlates
                : source.Plates;

            var (plates, elevZ) = MeshBoundary.FilterNearestLevel(all, targetZM);
            string elevLabel = source.AvailableLevels
                .FirstOrDefault(l => Math.Abs(l.ZM - elevZ) < 0.08)?.Label
                ?? $"Z = {elevZ:F3} м";
            var sameLevel = Math.Abs(source.ElevationZM - elevZ) < 0.08;
            var preserveCustomOpenings = source.UseCustomOpenings && sameLevel;

            var result = BuildResult(
                source.DocumentName,
                source.DocumentPath,
                source.NodeCount,
                plates,
                settings,
                source.Axes,
                elevZ,
                elevLabel,
                skipLevelFilter: true,
                openings: preserveCustomOpenings ? source.Openings : null,
                useCustomOpenings: preserveCustomOpenings);
            if (sameLevel)
                result.BoundaryConditions = CloneBoundaryConditions(source.BoundaryConditions);
            result.AllPlates = all;
            result.AvailableLevels = source.AvailableLevels.Count > 0
                ? source.AvailableLevels
                : MeshBoundary.CollectLevels(all, null);
            result.UnitsNote = source.UnitsNote;
            return result;
        }

        private static (List<LiraPlateElement> Plates, double ElevationZM) SelectLevel(
            List<LiraPlateElement> platesAll,
            AnalysisSettings settings,
            List<ElevationLevelInfo> levels)
        {
            if (!double.IsNaN(settings.TargetElevationZM))
                return MeshBoundary.FilterNearestLevel(platesAll, settings.TargetElevationZM);

            return MeshBoundary.FilterDominantLevelEx(platesAll);
        }

        public static AnalysisResult BuildResult(
            string documentName,
            string documentPath,
            int nodeCount,
            List<LiraPlateElement> plates,
            AnalysisSettings settings,
            List<ConstructionAxis>? axes = null,
            double? elevationZM = null,
            string? elevationLabel = null,
            bool skipLevelFilter = false,
            List<OpeningInfo>? openings = null,
            bool useCustomOpenings = false)
        {
            List<LiraPlateElement> levelPlates;
            double elev;
            if (skipLevelFilter)
            {
                levelPlates = plates;
                elev = elevationZM ?? (plates.Count > 0 ? plates.Average(p => p.Centroid.Z) : 0);
            }
            else if (!double.IsNaN(settings.TargetElevationZM))
            {
                var filtered = MeshBoundary.FilterNearestLevel(plates, settings.TargetElevationZM);
                levelPlates = filtered.Plates;
                elev = elevationZM ?? filtered.ElevationZM;
            }
            else
            {
                var filtered = MeshBoundary.FilterDominantLevelEx(plates);
                levelPlates = filtered.Plates;
                elev = elevationZM ?? filtered.ElevationZM;
            }

            var geometry = SlabGeometryCache.Get(levelPlates);
            List<Point3> outline = geometry.Outline ?? new List<Point3>();
            var detectedOpenings = useCustomOpenings && openings != null
                ? openings.Select(CloneOpening).ToList()
                : geometry.Openings ?? new List<OpeningInfo>();
            if (!useCustomOpenings && openings != null)
                detectedOpenings.AddRange(openings.Where(op => !detectedOpenings.Any(existing =>
                    Math.Abs(existing.MinXM - op.MinXM) < 0.001 && Math.Abs(existing.MinYM - op.MinYM) < 0.001)));
            var layoutOutline = settings.ApplySlabBoundaryAndOpeningRules
                ? outline
                : new List<Point3>();
            var layoutOpenings = settings.ApplySlabBoundaryAndOpeningRules
                ? detectedOpenings
                : new List<OpeningInfo>();

            if (!string.Equals(settings.PlacementMode, "LegacyZones", StringComparison.OrdinalIgnoreCase))
            {
                var patches = ZonePatchAnalyzer.Build(levelPlates, settings, elev);
                return new AnalysisResult
                {
                    DocumentName = documentName,
                    DocumentPath = documentPath,
                    UnitsNote = "Координаты: м. Мозаика As−фон: см²/м. Сейчас строятся только связные пятна и их ограничивающие рамки.",
                    Settings = settings,
                    NodeCount = nodeCount,
                    PlateCount = levelPlates.Count,
                    Plates = levelPlates,
                    Outline = outline,
                    Zones = new List<AdditionalZone>(),
                    Patches = patches,
                    PatchPreviewOnly = true,
                    Axes = axes ?? new List<ConstructionAxis>(),
                    Openings = detectedOpenings,
                    UseCustomOpenings = useCustomOpenings,
                    ElevationZM = elev,
                    ElevationLabel = elevationLabel ?? $"Z = {elev:F3} м",
                    Stats = ComputeStats(new List<AdditionalZone>(), settings, outline, levelPlates),
                    Diagnostics = new ZoneDiagnostics()
                };
            }

            // The layout uses openings for avoidance. Actual polygon splitting is applied
            // only below, after the layout has stabilized.
            var zones = ZoneLayoutEngine.Layout(levelPlates, settings,
                openings: layoutOpenings, outline: layoutOutline, axes: axes);
            if (settings.ApplySlabBoundaryAndOpeningRules && settings.ApplyHoleRules && layoutOpenings.Count > 0)
            {
                var platesById = levelPlates.ToDictionary(plate => plate.Id);
                if (settings.ReverseZoneDirections)
                    ZoneEditor.MergeLongitudinalConflicts(zones, layoutOutline);
                for (var i = zones.Count - 1; i >= 0; i--)
                {
                    var zone = zones[i];
                    var parts = ZoneEditor.SplitAtOpenings(zone, layoutOpenings, settings, levelPlates)
                        .Where(part => part.NodeIds.Count > 0).ToList();
                    if (parts.Count == 1 && ReferenceEquals(parts[0], zone)) continue;
                    if (parts.Count == 0 && ZoneEditor.IsFullyInsideOpening(zone, layoutOpenings))
                    {
                        zones.RemoveAt(i);
                        continue;
                    }
                    var otherZones = zones.Where((other, otherIndex) => otherIndex != i).ToList();
                    parts = parts.Where(part => !otherZones.Any(other =>
                    {
                        if (other.Layer != part.Layer ||
                            other.AsCoveredCm2PerM + 1e-6 < part.AsCoveredCm2PerM ||
                            !part.NodeIds.All(other.NodeIds.Contains) ||
                            part.Contour.Count < 3 || other.Contour.Count < 3)
                            return false;
                        const double toleranceM = 0.001;
                        return other.Contour.Min(point => point.X) <= part.Contour.Min(point => point.X) + toleranceM &&
                               other.Contour.Max(point => point.X) >= part.Contour.Max(point => point.X) - toleranceM &&
                               other.Contour.Min(point => point.Y) <= part.Contour.Min(point => point.Y) + toleranceM &&
                               other.Contour.Max(point => point.Y) >= part.Contour.Max(point => point.Y) - toleranceM;
                    })).ToList();
                    ZoneEditor.AbsorbNarrowSplitParts(parts, settings.EffectiveMinZoneWidthM);
                    ZoneEditor.EnforceRequiredGaps(parts, levelPlates, settings, layoutOutline, layoutOpenings);
                    var splitCoverageZones = parts.Concat(otherZones
                        .Where(other => other.Layer == zone.Layer)).ToList();
                    var uncoveredIds = FindUncoveredZoneElements(
                        zone, splitCoverageZones, platesById, settings, layoutOutline, layoutOpenings);
                    var narrowParts = parts.Where(part =>
                        part.FamilyKind == ZoneFamilyKind.Straight &&
                        settings.EffectiveMinZoneWidthM > 0 &&
                        part.WidthM + 1e-6 < settings.EffectiveMinZoneWidthM).ToList();
                    var removableSlivers = narrowParts.Where(sliver => sliver.NodeIds.All(id =>
                        parts.Any(part => !ReferenceEquals(part, sliver) &&
                            part.Layer == sliver.Layer &&
                            part.AsCoveredCm2PerM + 1e-6 >= sliver.AsCoveredCm2PerM &&
                            part.NodeIds.Contains(id)) ||
                        otherZones.Any(other => other.Layer == sliver.Layer &&
                            other.AsCoveredCm2PerM + 1e-6 >= sliver.AsCoveredCm2PerM &&
                            other.NodeIds.Contains(id)))).ToList();
                    if (removableSlivers.Count > 0)
                    {
                        parts.RemoveAll(removableSlivers.Contains);
                        narrowParts = narrowParts.Except(removableSlivers).ToList();
                        splitCoverageZones = parts.Concat(otherZones
                            .Where(other => other.Layer == zone.Layer)).ToList();
                        uncoveredIds = FindUncoveredZoneElements(
                            zone, splitCoverageZones, platesById, settings, layoutOutline, layoutOpenings);
                    }
                    if (parts.Count == 0 || uncoveredIds.Count > 0 || narrowParts.Count > 0)
                    {
                        zone.StatusColor = "warn";
                        var narrowDescription = string.Join(",", narrowParts.Select(part =>
                            $"{part.WidthMm:0}мм/{part.NodeIds.Count}КЭ"));
                        zone.Comment = $"отверстие: разделение отклонено; частей={parts.Count}; " +
                                       $"непокрытых КЭ={uncoveredIds.Count}; " +
                                       $"узких частей={narrowParts.Count} [{narrowDescription}]";
                        continue;
                    }
                    zones.RemoveAt(i);
                    zones.InsertRange(i, parts);
                }
                foreach (var normalized in zones)
                    normalized.Direction = RebarTables.DirectionForLayer(
                        normalized.Layer, settings.ReverseZoneDirections);
                if (settings.ReverseZoneDirections)
                    ZoneEditor.MergeDominatedOpeningExtensions(zones);
                ZoneEditor.EnforceRequiredGaps(zones, levelPlates, settings, layoutOutline, layoutOpenings);
                for (var i = 0; i < zones.Count; i++) zones[i].ZoneId = i + 1;
            }
            ZoneEditor.EnsureAssignedCapacity(zones, levelPlates, settings);
            ZoneEditor.EnforceMaximumDetailLength(zones, levelPlates, outline);
            ZoneEditor.NormalizeWidthsToBarStep(zones, levelPlates, layoutOutline, settings);
            ZoneEditor.EnforceRequiredGaps(zones, levelPlates, settings, layoutOutline, layoutOpenings);
            ZoneEditor.RemoveCoveredOverlapZones(zones, levelPlates, settings, layoutOutline, layoutOpenings);
            ZoneEditor.EnforceRequiredGaps(zones, levelPlates, settings, layoutOutline, layoutOpenings);
            for (var repairPass = 0; repairPass < 2; repairPass++)
            {
                ZoneEditor.CloseUncoveredStepGaps(zones, levelPlates, settings, layoutOutline, layoutOpenings);
                ZoneLayoutEngine.ResolveFinalIntersections(zones, levelPlates, settings, layoutOutline);
                ZoneEditor.RemoveCoveredOverlapZones(zones, levelPlates, settings, layoutOutline, layoutOpenings);
                ZoneEditor.EnforceRequiredGaps(zones, levelPlates, settings, layoutOutline, layoutOpenings);
            }
            // Late gap/overlap repairs can add FE IDs to zones, so audit capacity once more.
            ZoneEditor.EnsureAssignedCapacity(zones, levelPlates, settings);
            ZoneEditor.NormalizeBarArrayWidthsToStep(zones);
            for (var i = 0; i < zones.Count; i++) zones[i].ZoneId = i + 1;
            var stats = ComputeStats(zones, settings, outline, levelPlates);
            var diagnostics = ZoneLayoutDiagnostics.Evaluate(
                levelPlates, zones, settings, 0, true, outline, detectedOpenings);

            return new AnalysisResult
            {
                DocumentName = documentName,
                DocumentPath = documentPath,
                UnitsNote =
                    "Координаты: м. Ø/шаг/длины зон: мм. As/фон: см²/м. В Revit длины → футы только в ZonePlacer.",
                Settings = settings,
                NodeCount = nodeCount,
                PlateCount = levelPlates.Count,
                Plates = levelPlates,
                Outline = outline,
                Zones = zones,
                Axes = axes ?? new List<ConstructionAxis>(),
                Openings = detectedOpenings,
                UseCustomOpenings = useCustomOpenings,
                ElevationZM = elev,
                ElevationLabel = elevationLabel ?? $"Z = {elev:F3} м",
                Stats = stats,
                Diagnostics = diagnostics
            };
        }

        private static List<int> FindUncoveredZoneElements(
            AdditionalZone sourceZone, IList<AdditionalZone> coverageZones,
            IReadOnlyDictionary<int, LiraPlateElement> platesById, AnalysisSettings settings,
            IList<Point3>? slabOutline = null, IList<OpeningInfo>? openings = null)
        {
            var sameLayerZones = coverageZones
                .Where(candidate => candidate.Layer == sourceZone.Layer).ToList();
            return sourceZone.NodeIds.Distinct().Where(id =>
            {
                if (!platesById.TryGetValue(id, out var plate)) return true;
                if (!plate.Rebar.Ok)
                    return !sameLayerZones.Any(candidate => candidate.NodeIds.Contains(id));

                var requiredAs = plate.Rebar.Get(sourceZone.Layer) - BackgroundAs(settings, sourceZone.Layer);
                if (requiredAs <= MosaicBuilder.PositiveResidualToleranceCm2PerM) return false;

                return !ZoneCoverageRules.CoversOrBridgesGap(
                    sameLayerZones, plate, sourceZone.Layer, requiredAs, slabOutline, openings);
            }).ToList();
        }

        private static double BackgroundAs(AnalysisSettings settings, RebarLayer layer) => layer switch
        {
            RebarLayer.As1 => settings.AsMainAs1,
            RebarLayer.As2 => settings.AsMainAs2,
            RebarLayer.As3 => settings.AsMainAs3,
            RebarLayer.As4 => settings.AsMainAs4,
            _ => 0
        };

        public static List<Point3> BuildOutline(IList<LiraPlateElement> plates) =>
            MeshBoundary.BuildOuterContour(plates);

        private static OpeningInfo CloneOpening(OpeningInfo opening) => new OpeningInfo
        {
            OpeningId = opening.OpeningId,
            MinXM = opening.MinXM,
            MaxXM = opening.MaxXM,
            MinYM = opening.MinYM,
            MaxYM = opening.MaxYM,
            ElementIds = opening.ElementIds?.Distinct().ToList() ?? new List<int>(),
            MeshAnchors = opening.MeshAnchors?.Select(anchor => new MeshVertexAnchor
            {
                ElementId = anchor.ElementId,
                VertexIndex = anchor.VertexIndex,
                Position = anchor.Position == null
                    ? new Point3()
                    : new Point3(anchor.Position.X, anchor.Position.Y, anchor.Position.Z)
            }).ToList() ?? new List<MeshVertexAnchor>()
        };

        private static List<BoundaryConditionInfo> CloneBoundaryConditions(
            IEnumerable<BoundaryConditionInfo>? boundaries) => boundaries?.Select(boundary =>
                new BoundaryConditionInfo
                {
                    BoundaryId = boundary.BoundaryId,
                    Name = boundary.Name,
                    Edges = boundary.Edges?.Select(edge => new MeshEdgeAnchor
                    {
                        ElementId = edge.ElementId,
                        EdgeIndex = edge.EdgeIndex,
                        Start = edge.Start == null ? new Point3() :
                            new Point3(edge.Start.X, edge.Start.Y, edge.Start.Z),
                        End = edge.End == null ? new Point3() :
                            new Point3(edge.End.X, edge.End.Y, edge.End.Z)
                    }).ToList() ?? new List<MeshEdgeAnchor>()
                }).ToList() ?? new List<BoundaryConditionInfo>();

        public static AnalysisResult RebuildLayers(
            AnalysisResult source, AnalysisSettings settings, IEnumerable<RebarLayer> changedLayers)
        {
            var changed = new HashSet<RebarLayer>(changedLayers);
            if (changed.Count == 0) return source;
            var localSettings = Newtonsoft.Json.JsonConvert.DeserializeObject<AnalysisSettings>(
                Newtonsoft.Json.JsonConvert.SerializeObject(settings)) ?? settings;
            localSettings.ShowAs1 = changed.Contains(RebarLayer.As1) && settings.ShowAs1;
            localSettings.ShowAs2 = changed.Contains(RebarLayer.As2) && settings.ShowAs2;
            localSettings.ShowAs3 = changed.Contains(RebarLayer.As3) && settings.ShowAs3;
            localSettings.ShowAs4 = changed.Contains(RebarLayer.As4) && settings.ShowAs4;

            var rebuilt = BuildResult(source.DocumentName, source.DocumentPath, source.NodeCount,
                source.Plates, localSettings, source.Axes, source.ElevationZM, source.ElevationLabel,
                skipLevelFilter: true, openings: source.Openings,
                useCustomOpenings: source.UseCustomOpenings);
            if (source.PatchPreviewOnly)
            {
                rebuilt.Patches = source.Patches.Where(patch => !changed.Contains(patch.Layer))
                    .Concat(rebuilt.Patches).ToList();
                rebuilt.PatchPreviewOnly = true;
                rebuilt.Zones.Clear();
                rebuilt.Stats = ComputeStats(rebuilt.Zones, settings, rebuilt.Outline, rebuilt.Plates);
                rebuilt.Diagnostics = new ZoneDiagnostics();
            }
            else
            {
                rebuilt.Zones = source.Zones.Where(zone => !changed.Contains(zone.Layer))
                    .Concat(rebuilt.Zones).ToList();
                for (var i = 0; i < rebuilt.Zones.Count; i++) rebuilt.Zones[i].ZoneId = i + 1;
                rebuilt.Stats = ComputeStats(rebuilt.Zones, settings, rebuilt.Outline, rebuilt.Plates);
                rebuilt.Diagnostics = ZoneLayoutDiagnostics.Evaluate(
                    rebuilt.Plates, rebuilt.Zones, settings, 0, true, rebuilt.Outline, rebuilt.Openings);
            }
            rebuilt.Settings = settings;
            rebuilt.AllPlates = source.AllPlates;
            rebuilt.BoundaryConditions = CloneBoundaryConditions(source.BoundaryConditions);
            rebuilt.AvailableLevels = source.AvailableLevels;
            rebuilt.UnitsNote = source.UnitsNote;
            return rebuilt;
        }

        /// <summary>Старый режим: 1 зона = 1 КЭ (для отладки изополей).</summary>
        public static List<AdditionalZone> BuildZones(IEnumerable<LiraPlateElement> plates, AnalysisSettings settings) =>
            BuildZonesPerElement(plates, settings);

        public static List<AdditionalZone> BuildZonesPerElement(IEnumerable<LiraPlateElement> plates, AnalysisSettings settings)
        {
            var zones = new List<AdditionalZone>();
            int zoneId = 1;
            var layers = new[]
            {
                (RebarLayer.As1, settings.ShowAs1, settings.AsMainAs1),
                (RebarLayer.As2, settings.ShowAs2, settings.AsMainAs2),
                (RebarLayer.As3, settings.ShowAs3, settings.AsMainAs3),
                (RebarLayer.As4, settings.ShowAs4, settings.AsMainAs4)
            };

            foreach (var plate in plates.Where(p => p.Rebar.Ok))
            {
                foreach (var (layer, show, asMain) in layers)
                {
                    if (!show) continue;
                    double asReq = plate.Rebar.Get(layer);
                    double asAdd = asReq - asMain;
                    if (asAdd <= MosaicBuilder.PositiveResidualToleranceCm2PerM) continue;
                    if (settings.EffectiveMinZoneWidthM > 0 &&
                        plate.WidthM < settings.EffectiveMinZoneWidthM &&
                        plate.LengthM < settings.EffectiveMinZoneWidthM)
                        continue;

                    bool warnSize =
                        (settings.MaxZoneWidthM > 0 && plate.WidthM > settings.MaxZoneWidthM) ||
                        (settings.MinZoneLengthM > 0 && plate.LengthM < settings.MinZoneLengthM);

                    var dir = RebarTables.DirectionForLayer(layer, settings.ReverseZoneDirections);
                    var backgroundDiameter = layer is RebarLayer.As1 or RebarLayer.As2
                        ? settings.BgBottomDiameterMm
                        : settings.BgTopDiameterMm;
                    var option = BarCapacity.SelectDiameterAndStep(
                        asAdd,
                        settings.MaxDiameterMm > 0 ? settings.MaxDiameterMm : 36,
                        backgroundDiameter,
                        settings.AllowedAdditionalBarStepsMm?.ToArray(),
                        settings.ExcludedZoneDiametersMm?.ToArray());
                    var step = option.StepMm;
                    var d = option.DiameterMm;
                    if (d <= 0) continue;
                    var span = UnitConversion.MetersToMm(Math.Min(plate.WidthM, plate.LengthM));
                    var (barCount, widthMm) = BarCapacity.BarsForSpanAndAs(asAdd, d, step, span);

                    zones.Add(new AdditionalZone
                    {
                        ZoneId = zoneId++,
                        ElementId = plate.Id,
                        Layer = layer,
                        NodeIds = plate.NodeIds,
                        Placement = plate.Centroid,
                        Contour = plate.Contour,
                        WidthM = plate.WidthM,
                        LengthM = plate.LengthM,
                        LevelZM = plate.Centroid.Z,
                        AsRequired = asReq,
                        AsAdditional = asAdd,
                        Rebar = plate.Rebar,
                        Comment = warnSize ? "габарит вне допусков" : "",
                        IsValid = !warnSize,
                        StatusColor = warnSize ? "warn" : "ok",
                        Direction = dir,
                        DiameterMm = d,
                        BarStepMm = step,
                        BarCount = barCount,
                        WidthMm = widthMm,
                        LengthMm = UnitConversion.MetersToMm(Math.Max(plate.WidthM, plate.LengthM)),
                        FamilyKind = ZoneFamilyKind.Straight,
                        FamilyFileName = settings.GetFamilyName(ZoneFamilyKind.Straight),
                        AsCoveredCm2PerM = d > 0 ? BarCapacity.AsCm2PerM(d, step) : 0,
                        ConcreteClass = settings.ConcreteClass,
                        AlphaCoef = settings.AlphaCoef,
                        RotationDeg = dir == ZoneDirection.Y ? 90 : 0,
                        CountInSpec = true
                    });
                }
            }

            return zones;
        }

        private static PreviewStats ComputeStats(
            List<AdditionalZone> zones,
            AnalysisSettings? settings = null,
            IList<Point3>? outline = null,
            IList<LiraPlateElement>? plates = null)
        {
            double mass = 0;
            foreach (var z in zones)
            {
                if (z.DiameterMm <= 0 || z.BarCount <= 0) continue;
                mass += BarCapacity.SteelKgPerM(z.DiameterMm) * (z.LengthMm / 1000.0) * z.BarCount;
            }

            var slider = settings?.DetailSlider ?? 0;
            var thick = settings?.SlabThicknessMm > 0 ? settings.SlabThicknessMm : 200;
            var area = DetailOptimizer.SlabAreaM2(outline, plates);
            var kgPerM3 = DetailOptimizer.EstimateKgPerM3(zones, area, thick);

            return new PreviewStats
            {
                ZonesAs1 = zones.Count(z => z.Layer == RebarLayer.As1),
                ZonesAs2 = zones.Count(z => z.Layer == RebarLayer.As2),
                ZonesAs3 = zones.Count(z => z.Layer == RebarLayer.As3),
                ZonesAs4 = zones.Count(z => z.Layer == RebarLayer.As4),
                AreaAs1M2 = zones.Where(z => z.Layer == RebarLayer.As1).Sum(z => z.WidthM * z.LengthM),
                AreaAs2M2 = zones.Where(z => z.Layer == RebarLayer.As2).Sum(z => z.WidthM * z.LengthM),
                AreaAs3M2 = zones.Where(z => z.Layer == RebarLayer.As3).Sum(z => z.WidthM * z.LengthM),
                AreaAs4M2 = zones.Where(z => z.Layer == RebarLayer.As4).Sum(z => z.WidthM * z.LengthM),
                MaxAs = zones.Count == 0 ? 0 : zones.Max(z => z.AsRequired),
                WarnCount = zones.Count(z => z.StatusColor == "warn"),
                ErrorCount = zones.Count(z => z.StatusColor == "error"),
                TotalSteelMassKg = mass,
                SteelKgPerM3 = kgPerM3,
                DetailLevelLabel = DetailOptimizer.LabelWithMass(slider, kgPerM3)
            };
        }

        public static void SaveJson(AnalysisResult result, string path)
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);

            System.IO.File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented));
        }

        /// <summary>
        /// Один JSON со всеми горизонтальными плитами (все уровни), без зон.
        /// </summary>
        public static void SaveAllPlatesJson(AnalysisResult result, string path)
        {
            var plates = result.AllPlates != null && result.AllPlates.Count > 0
                ? result.AllPlates
                : result.Plates;

            var payload = new
            {
                result.DocumentName,
                result.DocumentPath,
                SavedUtc = DateTime.UtcNow,
                PlateCount = plates.Count,
                Levels = result.AvailableLevels,
                Axes = result.Axes,
                UnitsNote = result.UnitsNote,
                Plates = plates
            };

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);

            System.IO.File.WriteAllText(
                path,
                Newtonsoft.Json.JsonConvert.SerializeObject(payload, Newtonsoft.Json.Formatting.Indented));
        }

        public static AnalysisResult LoadJson(string path, AnalysisSettings? fallbackSettings = null)
        {
            var result = new AnalysisResult { Settings = fallbackSettings ?? new AnalysisSettings() };
            using (var file = System.IO.File.OpenText(path))
            using (var reader = new Newtonsoft.Json.JsonTextReader(file))
                Newtonsoft.Json.JsonSerializer.CreateDefault().Populate(reader, result);

            result.AllPlates = MeshBoundary.FilterHorizontalPlates(result.Plates);
            if (result.AvailableLevels.Count == 0)
                result.AvailableLevels = MeshBoundary.CollectLevels(result.AllPlates, null);

            if (result.AllPlates.Count > 0)
            {
                var (levelPlates, _) = SelectLevel(result.AllPlates, result.Settings, result.AvailableLevels);
                result.Settings.GridCellMm = MeshBoundary.EstimateGridCellMm(
                    levelPlates, result.Settings.GridCellMm > 0 ? result.Settings.GridCellMm : 300);
            }

            // Экспорт всех плит не содержит зон и настроек. Считаем выбранный этаж,
            // сохраняя остальные плиты для последующей смены отметки.
            if (result.Zones.Count == 0 && result.AllPlates.Count > 0)
            {
                var (_, elevation) = SelectLevel(result.AllPlates, result.Settings, result.AvailableLevels);
                return RebuildForElevation(result, elevation, result.Settings);
            }
            return result;
        }
    }
}
