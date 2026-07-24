using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Автораскладка зон доп. армирования по мозаике As−фон (см²/м).
    /// Длина = пятно + 2×анкеровка → SUM-3 вверх; ширина кратна шагу и покрывает пятно;
    /// соседние зоны с зазором = шаг стержней перпендикулярно длине.
    /// </summary>
    public static class ZoneLayoutEngine
    {
        public static List<AdditionalZone> Layout(
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<OpeningInfo>? openings = null,
            IList<Point3>? outline = null,
            IList<ConstructionAxis>? axes = null)
        {
            if (!settings.AutoLayout ||
                string.Equals(settings.PlacementMode, "ElementCenter", StringComparison.OrdinalIgnoreCase))
            {
                return SlabZoneAnalyzer.BuildZonesPerElement(plates, settings);
            }

            settings.SyncBackgroundAsFromBars();
            if (!settings.CanLayoutAdditionalZones(out _))
                return new List<AdditionalZone>();

            settings.DetailLevel = DetailOptimizer.FromSlider(settings.DetailSlider);
            settings.BarStepMm = settings.UseBarStep100 ? 100 : 200;

            var zones = new List<AdditionalZone>();
            var zoneId = 1;
            var levelZM = plates.Count > 0 ? plates.Average(p => p.Centroid.Z) : 0;
            openings ??= Array.Empty<OpeningInfo>();

            var layers = new (RebarLayer Layer, bool Show, double AsMain)[]
            {
                (RebarLayer.As1, settings.ShowAs1, settings.AsMainAs1),
                (RebarLayer.As2, settings.ShowAs2, settings.AsMainAs2),
                (RebarLayer.As3, settings.ShowAs3, settings.AsMainAs3),
                (RebarLayer.As4, settings.ShowAs4, settings.AsMainAs4)
            };

            foreach (var (layer, show, asMain) in layers)
            {
                if (!show) continue;
                var mosaic = MosaicBuilder.Build(plates, layer, asMain, settings.GridCellMm, levelZM);
                if (mosaic.Nx == 0 || mosaic.Ny == 0) continue;

                zones.AddRange(LayoutLayer(mosaic, layer, settings, openings, outline, axes, ref zoneId));
            }

            return zones;
        }

        private static List<AdditionalZone> LayoutLayer(
            MosaicGrid mosaic,
            RebarLayer layer,
            AnalysisSettings settings,
            IList<OpeningInfo> openings,
            IList<Point3>? outline,
            IList<ConstructionAxis>? axes,
            ref int zoneId)
        {
            var direction = RebarTables.DirectionForLayer(layer);
            var values = MosaicBuilder.SmoothSingleSpike(mosaic.Values);
            var ny = mosaic.Ny;
            var nx = mosaic.Nx;
            var cellMm = mosaic.CellMm;
            var thresholdRatio = DetailOptimizer.ThresholdRatioForSlider(settings.DetailSlider);
            var concrete = RebarTables.NormalizeConcrete(settings.ConcreteClass);
            var step = settings.UseBarStep100 ? 100 : 200;
            settings.BarStepMm = step;
            var maxD = settings.MaxDiameterMm > 0 ? settings.MaxDiameterMm : 36;
            var minFamilyLen = RebarTables.Sum3FamilyLengthsMm[0];
            var gapPerpM = UnitConversion.MmToMeters(step); // зазор между зонами = шаг стержней

            // Лимиты из UI (мм). 0 = без ограничения. Поддержка «старых» значений ≥50, сохранённых как «м».
            var minWidthMm = SettingToMm(settings.MinZoneWidthM);
            var maxWidthMm = SettingToMm(settings.MaxZoneWidthM);
            var minLengthMm = SettingToMm(settings.MinZoneLengthM);
            if (minLengthMm > minFamilyLen) minFamilyLen = (int)Math.Round(minLengthMm);
            var minFe = settings.MinActiveElements;
            var maxima = LocalMaxima(values);
            var peaks = new List<(double V, int Ix, int Iy)>();
            for (var iy = 0; iy < ny; iy++)
            for (var ix = 0; ix < nx; ix++)
                if (maxima[iy][ix] && values[iy][ix] > 0.01)
                    peaks.Add((values[iy][ix], ix, iy));
            peaks.Sort((a, b) => b.V.CompareTo(a.V));

            var assigned = new bool[ny, nx];
            var result = new List<AdditionalZone>();
            // Уже размещённые AABB (м) для контроля зазора/перекрытий
            var placed = new List<(double MinX, double MaxX, double MinY, double MaxY)>();

            foreach (var (vPeak, ix0, iy0) in peaks)
            {
                if (assigned[iy0, ix0]) continue;

                var dZone = BarCapacity.MinDiameterForAs(vPeak, step, maxD);
                if (dZone <= 0) continue;

                var aThr = thresholdRatio * vPeak;
                GrowSolidRect(values, ix0, iy0, aThr, out var iLeft, out var iRight, out var jDown, out var jUp);

                // Мин. число активных КЭ (ячеек мозаики с As ≥ порога)
                var activeCells = 0;
                for (var iy = jDown; iy <= jUp; iy++)
                for (var ix = iLeft; ix <= iRight; ix++)
                    if (values[iy][ix] >= aThr) activeCells++;
                if (minFe > 0 && activeCells < minFe) continue;

                var x0 = mosaic.OriginXM + iLeft * (cellMm / 1000.0);
                var x1 = mosaic.OriginXM + (iRight + 1) * (cellMm / 1000.0);
                var y0 = mosaic.OriginYM + jDown * (cellMm / 1000.0);
                var y1 = mosaic.OriginYM + (jUp + 1) * (cellMm / 1000.0);
                // Центр пика (ячейка) — зона обязана его покрывать после всех сдвигов
                var peakXM = mosaic.OriginXM + (ix0 + 0.5) * (cellMm / 1000.0);
                var peakYM = mosaic.OriginYM + (iy0 + 0.5) * (cellMm / 1000.0);

                double longStartM, longEndM, spanPerpMm, corePerp0, corePerp1;
                if (direction == ZoneDirection.X)
                {
                    longStartM = x0;
                    longEndM = x1;
                    spanPerpMm = (jUp - jDown + 1) * cellMm;
                    corePerp0 = y0;
                    corePerp1 = y1;
                }
                else
                {
                    longStartM = y0;
                    longEndM = y1;
                    spanPerpMm = (iRight - iLeft + 1) * cellMm;
                    corePerp0 = x0;
                    corePerp1 = x1;
                }

                var ancMm = RebarTables.AnchorageLenMm(concrete, dZone);
                // Пятно + анкеровка; у края плиты длина укоротится клипом (как SmartRebar)
                var longStartMm = UnitConversion.MetersToMm(longStartM) - ancMm;
                var longEndMm = UnitConversion.MetersToMm(longEndM) + ancMm;

                var (barCount, widthMm) = BarCapacity.BarsForSpanAndAs(vPeak, dZone, step, spanPerpMm);
                if (widthMm + 1e-6 < spanPerpMm)
                {
                    barCount = Math.Max(2, (int)Math.Ceiling(spanPerpMm / step) + 1);
                    widthMm = (barCount - 1) * step;
                }
                // Ограничение макс. ширины (как SmartRebar)
                if (maxWidthMm > 0 && widthMm > maxWidthMm)
                {
                    barCount = Math.Max(2, (int)Math.Floor(maxWidthMm / step) + 1);
                    widthMm = (barCount - 1) * step;
                }
                // Мин. ширина: расширяем до порога или пропускаем пятно
                if (minWidthMm > 0 && widthMm + 1 < minWidthMm)
                {
                    barCount = Math.Max(2, (int)Math.Ceiling(minWidthMm / step) + 1);
                    widthMm = (barCount - 1) * step;
                    if (maxWidthMm > 0 && widthMm > maxWidthMm)
                        continue; // нельзя удовлетворить min и max
                }
                var asCovered = BarCapacity.AsCm2PerM(dZone, step);

                var segments = SplitByMaxLength(longStartMm, longEndMm, dZone, concrete, settings.AlphaCoef);

                var elementIds = new List<int>();
                for (var iy = jDown; iy <= jUp; iy++)
                for (var ix = iLeft; ix <= iRight; ix++)
                    if (values[iy][ix] >= aThr)
                        elementIds.AddRange(mosaic.PlateIds[iy][ix]);

                var coreCx = (x0 + x1) / 2.0;
                var coreCy = (y0 + y1) / 2.0;
                var placedAnyForPeak = false;

                foreach (var (segStartMm, segEndMm) in segments)
                {
                    var segLen = segEndMm - segStartMm;
                    var familyLen = RebarTables.PickFamilyLength(segLen);
                    if (familyLen < minFamilyLen) continue;

                    var mid = (segStartMm + segEndMm) / 2.0;
                    var sAdj = mid - familyLen / 2.0;
                    var eAdj = mid + familyLen / 2.0;

                    var perpMid = (corePerp0 + corePerp1) / 2.0;
                    var halfW = UnitConversion.MmToMeters(widthMm) / 2.0;

                    double minXM, maxXM, minYM, maxYM;
                    if (direction == ZoneDirection.X)
                    {
                        minXM = UnitConversion.MmToMeters(sAdj);
                        maxXM = UnitConversion.MmToMeters(eAdj);
                        minYM = perpMid - halfW;
                        maxYM = perpMid + halfW;
                    }
                    else
                    {
                        minYM = UnitConversion.MmToMeters(sAdj);
                        maxYM = UnitConversion.MmToMeters(eAdj);
                        minXM = perpMid - halfW;
                        maxXM = perpMid + halfW;
                    }

                    // 1) Подрезка контуром плиты (пересечение, не «сдвиг наружу»)
                    if (!MeshBoundary.ClipRectToSlab(ref minXM, ref maxXM, ref minYM, ref maxYM, outline, settings.SlabEdgeInsetMm))
                        continue;

                    // Длина после клипа → SUM-3, которая реально влезает
                    double availLenMm = UnitConversion.MetersToMm(
                        direction == ZoneDirection.X ? (maxXM - minXM) : (maxYM - minYM));
                    familyLen = RebarTables.PickFamilyLengthFit(availLenMm);
                    if (familyLen < minFamilyLen) continue;
                    CenterAlongLength(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, familyLen);

                    // Ширина кратна шагу, не шире клипа и в пределах min/max
                    double availWidMm = UnitConversion.MetersToMm(
                        direction == ZoneDirection.X ? (maxYM - minYM) : (maxXM - minXM));
                    if (maxWidthMm > 0) availWidMm = Math.Min(availWidMm, maxWidthMm);
                    var barCountFinal = Math.Max(2, (int)Math.Floor(availWidMm / step) + 1);
                    var widthMmFinal = (barCountFinal - 1) * step;
                    if (widthMmFinal < step) continue;
                    if (minWidthMm > 0 && widthMmFinal + 1 < minWidthMm) continue;
                    CenterPerpWidth(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, widthMmFinal);

                    // 2) Привязка к осям (ограниченный сдвиг) + повторный клип
                    var tie = AxisSnapper.SnapRect(ref minXM, ref maxXM, ref minYM, ref maxYM, axes);
                    if (!MeshBoundary.ClipRectToSlab(ref minXM, ref maxXM, ref minYM, ref maxYM, outline, settings.SlabEdgeInsetMm))
                        continue;
                    // пересчёт длины/ширины после snap+clip
                    availLenMm = UnitConversion.MetersToMm(
                        direction == ZoneDirection.X ? (maxXM - minXM) : (maxYM - minYM));
                    familyLen = RebarTables.PickFamilyLengthFit(availLenMm);
                    if (familyLen < minFamilyLen) continue;
                    CenterAlongLength(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, familyLen);
                    availWidMm = UnitConversion.MetersToMm(
                        direction == ZoneDirection.X ? (maxYM - minYM) : (maxXM - minXM));
                    if (maxWidthMm > 0) availWidMm = Math.Min(availWidMm, maxWidthMm);
                    barCountFinal = Math.Max(2, (int)Math.Floor(availWidMm / step) + 1);
                    widthMmFinal = (barCountFinal - 1) * step;
                    if (widthMmFinal < step) continue;
                    if (minWidthMm > 0 && widthMmFinal + 1 < minWidthMm) continue;
                    CenterPerpWidth(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, widthMmFinal);

                    // 3) Зазор без увода с пятна
                    if (!ResolvePerpGap(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, gapPerpM, placed))
                        continue;
                    if (!CoversPoint(minXM, maxXM, minYM, maxYM, peakXM, peakYM) &&
                        !OverlapsCore(minXM, maxXM, minYM, maxYM, x0, x1, y0, y1))
                        continue;
                    if (!MeshBoundary.ClipRectToSlab(ref minXM, ref maxXM, ref minYM, ref maxYM, outline, settings.SlabEdgeInsetMm))
                        continue;
                    if (!CoversPoint(minXM, maxXM, minYM, maxYM, peakXM, peakYM) &&
                        !OverlapsCore(minXM, maxXM, minYM, maxYM, x0, x1, y0, y1))
                        continue;

                    var familyKind = ZoneFamilyKind.Straight;
                    var verticalLeg = 0.0;
                    var comment = "";
                    var countInSpec = true;
                    var countBars = false;
                    ApplyHoleBent(
                        settings, openings, outline, layer, direction, dZone, concrete,
                        ref minXM, ref maxXM, ref minYM, ref maxYM, coreCx, coreCy,
                        ref familyKind, ref verticalLeg, ref comment, ref countInSpec, ref countBars);

                    if (maxXM - minXM < 0.05 || maxYM - minYM < 0.05) continue;
                    if (!MeshBoundary.ClipRectToSlab(ref minXM, ref maxXM, ref minYM, ref maxYM, outline, settings.SlabEdgeInsetMm))
                        continue;

                    var cx = (minXM + maxXM) / 2.0;
                    var cy = (minYM + maxYM) / 2.0;
                    double lengthM = direction == ZoneDirection.X ? (maxXM - minXM) : (maxYM - minYM);
                    double widthM = direction == ZoneDirection.X ? (maxYM - minYM) : (maxXM - minXM);
                    var lenMmCheck = UnitConversion.MetersToMm(lengthM);
                    familyLen = RebarTables.PickFamilyLengthFit(lenMmCheck);
                    if (familyLen < minFamilyLen) continue;

                    // Финальная ширина по фактическому AABB
                    barCountFinal = Math.Max(2, (int)Math.Round(UnitConversion.MetersToMm(widthM) / step) + 1);
                    widthMmFinal = (barCountFinal - 1) * step;
                    CenterPerpWidth(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, widthMmFinal);
                    if (!MeshBoundary.ClipRectToSlab(ref minXM, ref maxXM, ref minYM, ref maxYM, outline, settings.SlabEdgeInsetMm))
                        continue;
                    if (!ResolvePerpGap(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, gapPerpM, placed))
                        continue;
                    if (!CoversPoint(minXM, maxXM, minYM, maxYM, peakXM, peakYM) &&
                        !OverlapsCore(minXM, maxXM, minYM, maxYM, x0, x1, y0, y1))
                        continue;

                    cx = (minXM + maxXM) / 2.0;
                    cy = (minYM + maxYM) / 2.0;
                    tie = ComputeTie(minXM, minYM, axes);

                    placed.Add((minXM, maxXM, minYM, maxYM));
                    placedAnyForPeak = true;

                    result.Add(new AdditionalZone
                    {
                        ZoneId = zoneId++,
                        ElementId = elementIds.FirstOrDefault(),
                        Layer = layer,
                        NodeIds = elementIds.Distinct().ToList(),
                        Placement = new Point3(cx, cy, mosaic.LevelZM),
                        Contour = new List<Point3>
                        {
                            new Point3(minXM, minYM, mosaic.LevelZM),
                            new Point3(maxXM, minYM, mosaic.LevelZM),
                            new Point3(maxXM, maxYM, mosaic.LevelZM),
                            new Point3(minXM, maxYM, mosaic.LevelZM),
                        },
                        WidthM = direction == ZoneDirection.X ? (maxYM - minYM) : (maxXM - minXM),
                        LengthM = direction == ZoneDirection.X ? (maxXM - minXM) : (maxYM - minYM),
                        LevelZM = mosaic.LevelZM,
                        AsRequired = vPeak + GetAsMain(settings, layer),
                        AsAdditional = vPeak,
                        Comment = comment,
                        IsValid = true,
                        StatusColor = familyKind == ZoneFamilyKind.Straight ? "ok" : "warn",
                        Direction = direction,
                        DiameterMm = dZone,
                        BarStepMm = step,
                        BarCount = barCountFinal,
                        WidthMm = widthMmFinal,
                        LengthMm = familyLen,
                        FamilyKind = familyKind,
                        FamilyFileName = RebarTables.FamilyFileName(familyKind),
                        AsCoveredCm2PerM = asCovered,
                        ConcreteClass = concrete,
                        AlphaCoef = settings.AlphaCoef,
                        RotationDeg = direction == ZoneDirection.Y ? 90.0 : 0.0,
                        CountInSpec = countInSpec,
                        CountBars = countBars,
                        VerticalLegMm = verticalLeg,
                        AxisNameX = tie.AxisNameX,
                        AxisPosXM = tie.AxisPosXM,
                        OffsetFromAxisXMm = tie.OffsetFromAxisXMm,
                        AxisNameY = tie.AxisNameY,
                        AxisPosYM = tie.AxisPosYM,
                        OffsetFromAxisYMm = tie.OffsetFromAxisYMm,
                        AxisTieLabel = tie.Label
                    });
                }

                // Помечаем ячейки только после успешной укладки — иначе пятно «съедается» без зоны
                if (placedAnyForPeak)
                {
                    for (var iy = jDown; iy <= jUp; iy++)
                    for (var ix = iLeft; ix <= iRight; ix++)
                        if (values[iy][ix] >= aThr)
                            assigned[iy, ix] = true;
                }
            }

            return result;
        }

        /// <summary>
        /// Настройка в UI — мм; в DTO исторически «метры».
        /// Значения ≥ 50 считаем уже мм (пользователь вводил 800/1950 в поле «м»).
        /// </summary>
        private static double SettingToMm(double stored)
        {
            if (stored <= 0) return 0;
            if (stored >= 50) return stored;
            return stored * 1000.0;
        }

        private static void CenterAlongLength(
            ref double minXM, ref double maxXM, ref double minYM, ref double maxYM,
            ZoneDirection direction, double familyLenMm)
        {
            var lenM = UnitConversion.MmToMeters(familyLenMm);
            if (direction == ZoneDirection.X)
            {
                var cx = (minXM + maxXM) / 2.0;
                minXM = cx - lenM / 2.0;
                maxXM = cx + lenM / 2.0;
            }
            else
            {
                var cy = (minYM + maxYM) / 2.0;
                minYM = cy - lenM / 2.0;
                maxYM = cy + lenM / 2.0;
            }
        }

        private static void CenterPerpWidth(
            ref double minXM, ref double maxXM, ref double minYM, ref double maxYM,
            ZoneDirection direction, double widthMm)
        {
            var wM = UnitConversion.MmToMeters(widthMm);
            if (direction == ZoneDirection.X)
            {
                var cy = (minYM + maxYM) / 2.0;
                minYM = cy - wM / 2.0;
                maxYM = cy + wM / 2.0;
            }
            else
            {
                var cx = (minXM + maxXM) / 2.0;
                minXM = cx - wM / 2.0;
                maxXM = cx + wM / 2.0;
            }
        }

        private static bool CoversPoint(double minX, double maxX, double minY, double maxY, double px, double py)
            => px >= minX - 1e-6 && px <= maxX + 1e-6 && py >= minY - 1e-6 && py <= maxY + 1e-6;

        private static bool OverlapsCore(
            double minX, double maxX, double minY, double maxY,
            double cMinX, double cMaxX, double cMinY, double cMaxY)
        {
            var ox0 = Math.Max(minX, cMinX);
            var ox1 = Math.Min(maxX, cMaxX);
            var oy0 = Math.Max(minY, cMinY);
            var oy1 = Math.Min(maxY, cMaxY);
            if (ox1 <= ox0 || oy1 <= oy0) return false;
            var coreA = Math.Max(1e-9, (cMaxX - cMinX) * (cMaxY - cMinY));
            return (ox1 - ox0) * (oy1 - oy0) / coreA >= 0.35;
        }

        private static AxisSnapper.TieInfo ComputeTie(double minXM, double minYM, IList<ConstructionAxis>? axes)
        {
            var info = new AxisSnapper.TieInfo();
            if (axes == null || axes.Count == 0) return info;

            string? bestVX = null;
            double bestVPos = 0, bestVDist = double.MaxValue;
            string? bestHY = null;
            double bestHPos = 0, bestHDist = double.MaxValue;

            foreach (var ax in axes)
            {
                var name = string.IsNullOrWhiteSpace(ax.Name) ? "?" : ax.Name;
                bool vertical = ax.Vertical;
                double pos = ax.Position;
                if (ax.IsSegment)
                {
                    var dx = Math.Abs(ax.X2 - ax.X1);
                    var dy = Math.Abs(ax.Y2 - ax.Y1);
                    vertical = dx <= dy;
                    pos = vertical ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
                }
                if (vertical)
                {
                    var d = Math.Abs(pos - minXM);
                    if (d < bestVDist) { bestVDist = d; bestVX = name; bestVPos = pos; }
                }
                else
                {
                    var d = Math.Abs(pos - minYM);
                    if (d < bestHDist) { bestHDist = d; bestHY = name; bestHPos = pos; }
                }
            }

            if (bestVX != null && bestVDist <= 12.0)
            {
                info.AxisNameX = bestVX;
                info.AxisPosXM = bestVPos;
                info.OffsetFromAxisXMm = Math.Round((minXM - bestVPos) * 1000.0 / 10.0) * 10.0;
            }
            if (bestHY != null && bestHDist <= 12.0)
            {
                info.AxisNameY = bestHY;
                info.AxisPosYM = bestHPos;
                info.OffsetFromAxisYMm = Math.Round((minYM - bestHPos) * 1000.0 / 10.0) * 10.0;
            }
            return info;
        }

        /// <summary>Рост прямоугольника: расширяем ребро, только если ВСЯ колонка/строка ≥ порога.</summary>
        private static void GrowSolidRect(
            double[][] area, int ix0, int iy0, double aThr,
            out int iLeft, out int iRight, out int jDown, out int jUp)
        {
            var ny = area.Length;
            var nx = area[0].Length;
            iLeft = iRight = ix0;
            jDown = jUp = iy0;

            bool expanded;
            do
            {
                expanded = false;
                if (iLeft > 0)
                {
                    var ok = true;
                    for (var iy = jDown; iy <= jUp; iy++)
                        if (area[iy][iLeft - 1] < aThr) { ok = false; break; }
                    if (ok) { iLeft--; expanded = true; }
                }
                if (iRight + 1 < nx)
                {
                    var ok = true;
                    for (var iy = jDown; iy <= jUp; iy++)
                        if (area[iy][iRight + 1] < aThr) { ok = false; break; }
                    if (ok) { iRight++; expanded = true; }
                }
                if (jDown > 0)
                {
                    var ok = true;
                    for (var ix = iLeft; ix <= iRight; ix++)
                        if (area[jDown - 1][ix] < aThr) { ok = false; break; }
                    if (ok) { jDown--; expanded = true; }
                }
                if (jUp + 1 < ny)
                {
                    var ok = true;
                    for (var ix = iLeft; ix <= iRight; ix++)
                        if (area[jUp + 1][ix] < aThr) { ok = false; break; }
                    if (ok) { jUp++; expanded = true; }
                }
            } while (expanded);
        }

        /// <summary>
        /// Зазор = шаг стержней перпендикулярно длине; сдвиг ограничен — иначе зона уезжает с пятна.
        /// </summary>
        private static bool ResolvePerpGap(
            ref double minXM, ref double maxXM, ref double minYM, ref double maxYM,
            ZoneDirection direction, double gapM,
            List<(double MinX, double MaxX, double MinY, double MaxY)> placed)
        {
            const int maxIter = 16;
            var origMinX = minXM;
            var origMaxX = maxXM;
            var origMinY = minYM;
            var origMaxY = maxYM;
            var maxShift = Math.Max(0.6, (direction == ZoneDirection.X ? (maxYM - minYM) : (maxXM - minXM)) * 2.5);

            for (var iter = 0; iter < maxIter; iter++)
            {
                var moved = false;
                foreach (var p in placed)
                {
                    var aMinX = minXM - (direction == ZoneDirection.Y ? gapM : 0);
                    var aMaxX = maxXM + (direction == ZoneDirection.Y ? gapM : 0);
                    var aMinY = minYM - (direction == ZoneDirection.X ? gapM : 0);
                    var aMaxY = maxYM + (direction == ZoneDirection.X ? gapM : 0);

                    if (aMaxX <= p.MinX || aMinX >= p.MaxX || aMaxY <= p.MinY || aMinY >= p.MaxY)
                        continue;

                    if (direction == ZoneDirection.X)
                    {
                        var cy = (minYM + maxYM) / 2.0;
                        var py = (p.MinY + p.MaxY) / 2.0;
                        var half = (maxYM - minYM) / 2.0;
                        var target = cy >= py ? p.MaxY + gapM + half : p.MinY - gapM - half;
                        var dy = target - cy;
                        minYM += dy;
                        maxYM += dy;
                        moved = true;
                    }
                    else
                    {
                        var cx = (minXM + maxXM) / 2.0;
                        var px = (p.MinX + p.MaxX) / 2.0;
                        var half = (maxXM - minXM) / 2.0;
                        var target = cx >= px ? p.MaxX + gapM + half : p.MinX - gapM - half;
                        var dx = target - cx;
                        minXM += dx;
                        maxXM += dx;
                        moved = true;
                    }
                }
                if (!moved) break;
            }

            var shift = direction == ZoneDirection.X
                ? Math.Abs(((minYM + maxYM) / 2.0) - ((origMinY + origMaxY) / 2.0))
                : Math.Abs(((minXM + maxXM) / 2.0) - ((origMinX + origMaxX) / 2.0));
            if (shift > maxShift)
            {
                minXM = origMinX; maxXM = origMaxX; minYM = origMinY; maxYM = origMaxY;
                return false;
            }

            foreach (var p in placed)
            {
                var aMinX = minXM - (direction == ZoneDirection.Y ? gapM : 0);
                var aMaxX = maxXM + (direction == ZoneDirection.Y ? gapM : 0);
                var aMinY = minYM - (direction == ZoneDirection.X ? gapM : 0);
                var aMaxY = maxYM + (direction == ZoneDirection.X ? gapM : 0);
                if (!(aMaxX <= p.MinX || aMinX >= p.MaxX || aMaxY <= p.MinY || aMinY >= p.MaxY))
                    return false;
            }
            return true;
        }

        private static void ApplyHoleBent(
            AnalysisSettings settings,
            IList<OpeningInfo> openings,
            IList<Point3>? outline,
            RebarLayer layer,
            ZoneDirection direction,
            int dZone,
            string concrete,
            ref double minXM, ref double maxXM, ref double minYM, ref double maxYM,
            double coreCx, double coreCy,
            ref ZoneFamilyKind familyKind,
            ref double verticalLeg,
            ref string comment,
            ref bool countInSpec,
            ref bool countBars)
        {
            if (settings.ApplyHoleRules && openings.Count > 0)
            {
                foreach (var op in openings)
                {
                    if (HoleBentRules.ShouldIgnoreOpening(op, direction, settings.HoleIgnorePerpMm))
                        continue;
                    if (!HoleBentRules.RectIntersects(op, minXM, maxXM, minYM, maxYM))
                        continue;
                    var off = UnitConversion.MmToMeters(settings.EdgeOffsetMm);
                    if (direction == ZoneDirection.X)
                    {
                        if (coreCy < (op.MinYM + op.MaxYM) / 2) maxYM = Math.Min(maxYM, op.MinYM - off);
                        else minYM = Math.Max(minYM, op.MaxYM + off);
                    }
                    else
                    {
                        if (coreCx < (op.MinXM + op.MaxXM) / 2) maxXM = Math.Min(maxXM, op.MinXM - off);
                        else minXM = Math.Max(minXM, op.MaxXM + off);
                    }
                    if (settings.ApplyBentRules)
                    {
                        verticalLeg = HoleBentRules.VerticalLegAvailableMm(
                            settings.SlabThicknessMm, settings.CoverTopMm, settings.CoverBottomMm, dZone);
                        familyKind = HoleBentRules.ChooseBentFamily(verticalLeg, dZone);
                        countInSpec = false;
                        countBars = true;
                        comment = "отверстие: гнутая деталь";
                    }
                }
            }

            if (settings.ApplyBentRules && outline != null && outline.Count >= 3)
            {
                var oMinX = outline.Min(p => p.X);
                var oMaxX = outline.Max(p => p.X);
                var oMinY = outline.Min(p => p.Y);
                var oMaxY = outline.Max(p => p.Y);
                var off = UnitConversion.MmToMeters(settings.EdgeOffsetMm);
                var nearEdge = direction == ZoneDirection.X
                    ? (minXM < oMinX + off || maxXM > oMaxX - off)
                    : (minYM < oMinY + off || maxYM > oMaxY - off);
                if (nearEdge && familyKind == ZoneFamilyKind.Straight)
                {
                    verticalLeg = HoleBentRules.VerticalLegAvailableMm(
                        settings.SlabThicknessMm, settings.CoverTopMm, settings.CoverBottomMm, dZone);
                    familyKind = HoleBentRules.ChooseBentFamily(verticalLeg, dZone);
                    countInSpec = false;
                    countBars = true;
                    if (string.IsNullOrEmpty(comment)) comment = "торец: гнутая деталь";
                }
            }
        }

        private static double GetAsMain(AnalysisSettings s, RebarLayer layer) => layer switch
        {
            RebarLayer.As1 => s.AsMainAs1,
            RebarLayer.As2 => s.AsMainAs2,
            RebarLayer.As3 => s.AsMainAs3,
            _ => s.AsMainAs4
        };

        private static bool[][] LocalMaxima(double[][] area)
        {
            var ny = area.Length;
            var nx = area[0].Length;
            var mask = new bool[ny][];
            for (var iy = 0; iy < ny; iy++)
                mask[iy] = new bool[nx];

            for (var iy = 0; iy < ny; iy++)
            for (var ix = 0; ix < nx; ix++)
            {
                var v = area[iy][ix];
                if (v <= 0) continue;
                var ok = true;
                if (iy > 0 && v < area[iy - 1][ix]) ok = false;
                if (iy + 1 < ny && v < area[iy + 1][ix]) ok = false;
                if (ix > 0 && v < area[iy][ix - 1]) ok = false;
                if (ix + 1 < nx && v < area[iy][ix + 1]) ok = false;
                mask[iy][ix] = ok;
            }
            return mask;
        }

        private static List<(double Start, double End)> SplitByMaxLength(
            double longStartMm,
            double longEndMm,
            int diameterMm,
            string concreteClass,
            double alpha)
        {
            var lengths = RebarTables.Sum3FamilyLengthsMm;
            var maxL = lengths[lengths.Length - 1];
            if (longEndMm - longStartMm <= maxL)
                return new List<(double, double)> { (longStartMm, longEndMm) };

            var lap = RebarTables.LapLenMm(concreteClass, diameterMm);
            var overlap = 2.0 * alpha * lap;
            if (overlap >= maxL) overlap = maxL * 0.5;
            var advance = Math.Max(maxL - overlap, maxL * 0.5);

            var segs = new List<(double, double)>();
            var cur = longStartMm;
            while (cur < longEndMm - 1e-6)
            {
                var end = Math.Min(cur + maxL, longEndMm);
                segs.Add((cur, end));
                if (end >= longEndMm - 1e-6) break;
                cur += advance;
            }
            return segs;
        }
    }
}
