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
                openings: source.Openings);
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
            List<OpeningInfo>? openings = null)
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

            var outline = MeshBoundary.BuildOuterContour(levelPlates);
            var zones = ZoneLayoutEngine.Layout(levelPlates, settings, openings: openings, outline: outline, axes: axes);
            var stats = ComputeStats(zones, settings, outline, levelPlates);

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
                Openings = openings ?? new List<OpeningInfo>(),
                ElevationZM = elev,
                ElevationLabel = elevationLabel ?? $"Z = {elev:F3} м",
                Stats = stats
            };
        }

        public static List<Point3> BuildOutline(IList<LiraPlateElement> plates) =>
            MeshBoundary.BuildOuterContour(plates);

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
                    if (asAdd <= 0.01) continue;
                    if (settings.MinZoneWidthM > 0 &&
                        plate.WidthM < settings.MinZoneWidthM && plate.LengthM < settings.MinZoneWidthM)
                        continue;

                    bool warnSize =
                        (settings.MaxZoneWidthM > 0 && plate.WidthM > settings.MaxZoneWidthM) ||
                        (settings.MinZoneLengthM > 0 && plate.LengthM < settings.MinZoneLengthM);

                    var dir = RebarTables.DirectionForLayer(layer);
                    var step = settings.BarStepMm is 100 or 200 ? settings.BarStepMm : 200;
                    var d = BarCapacity.MinDiameterForAs(asAdd, step, settings.MaxDiameterMm > 0 ? settings.MaxDiameterMm : 36);
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
                        FamilyFileName = RebarTables.StraightFamily,
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

        public static AnalysisResult LoadJson(string path) =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<AnalysisResult>(System.IO.File.ReadAllText(path))
            ?? throw new System.IO.InvalidDataException("Пустой JSON анализа: " + path);
    }
}
