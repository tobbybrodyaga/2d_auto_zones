using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>Ось Revit в координатах ЛИРЫ, параллельная одной из осей X/Y плана ЛИРЫ.</summary>
    public sealed class AlignAxis
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>true — ось вдоль Y ЛИРЫ (задаёт X), false — вдоль X (задаёт Y).</summary>
        public bool Vertical { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }

        /// <summary>Координата оси (X для вертикальной, Y для горизонтальной) в точке along.</summary>
        public double PositionAt(double along)
        {
            if (Vertical)
                return Math.Abs(Y2 - Y1) < 1e-9 ? (X1 + X2) / 2 : X1 + (along - Y1) * (X2 - X1) / (Y2 - Y1);
            return Math.Abs(X2 - X1) < 1e-9 ? (Y1 + Y2) / 2 : Y1 + (along - X1) * (Y2 - Y1) / (X2 - X1);
        }
    }

    public sealed class GridAlignmentReport
    {
        public int Chains { get; set; }
        public int ShiftedChains { get; set; }
        public int CenteredZones { get; set; }
        public int UntiedZones { get; set; }
        public double MaxShiftMm { get; set; }

        public string Summary =>
            $"цепочек {Chains}, сдвинуто {ShiftedChains} (до {MaxShiftMm:0.#} мм)" +
            (CenteredZones > 0 ? $", по центру между краями {CenteredZones}" : "") +
            (UntiedZones > 0 ? $", не привязано {UntiedZones}" : "");
    }

    /// <summary>
    /// Привязка зон к осям Revit с кратностью 10 мм. Соседние зоны ряда (зазор до 250 мм)
    /// образуют цепочку и сдвигаются одним общим сдвигом (не больше 10 мм по каждому
    /// направлению), чтобы зазоры между ними не менялись. Сдвиг не приближает зону к краю
    /// плиты или отверстию ближе отступа (50 мм) — у края округление идёт внутрь.
    /// Зона, зажатая между краями с двух сторон, ставится по центру с равными зазорами.
    /// </summary>
    public static class RevitGridAlignment
    {
        public const double StepMm = 10;
        private const double TieToleranceMm = 0.5;
        private const double ParallelSin = 0.0035; // ≈ 0,2°
        private const double ChainGapM = 0.25;
        private const double MaxAxisDistanceM = 15;
        private const double GapSearchM = 1.0;

        /// <summary>Оси связи с Revit в координатах ЛИРЫ; наклонные к осям плана пропускаются.</summary>
        public static List<AlignAxis> AxesInLira(AnalysisResult result, AnalysisSettings settings)
        {
            var axes = new List<AlignAxis>();
            var grids = result.RevitLink?.Grids;
            if (grids == null || grids.Count == 0) return axes;
            var transform = LiraRevitTransform.For(result, settings);
            foreach (var grid in grids)
            {
                var (x1, y1) = transform.ToLira(grid.X1, grid.Y1);
                var (x2, y2) = transform.ToLira(grid.X2, grid.Y2);
                var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                if (length < 1e-6) continue;
                bool vertical = Math.Abs(x2 - x1) / length < ParallelSin;
                bool horizontal = Math.Abs(y2 - y1) / length < ParallelSin;
                if (!vertical && !horizontal) continue;
                axes.Add(new AlignAxis { Name = grid.Name, Vertical = vertical, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
            }
            return axes;
        }

        public static GridAlignmentReport Apply(IList<AdditionalZone> zones, IList<AlignAxis> axes,
            IList<Point3>? outline, IList<OpeningInfo>? openings, double edgeOffsetMm)
        {
            var report = new GridAlignmentReport();
            if (zones.Count == 0 || axes.Count == 0) return report;
            var obstacles = Obstacles(outline, openings);
            var edgeM = Math.Max(0, edgeOffsetMm) / 1000.0;

            foreach (var chain in Chains(zones))
            {
                report.Chains++;
                var dx = ChainShift(chain, axes, obstacles, edgeM, vertical: true, report);
                if (Math.Abs(dx) > 1e-9) Translate(chain, dx, 0);
                var dy = ChainShift(chain, axes, obstacles, edgeM, vertical: false, report);
                if (Math.Abs(dy) > 1e-9) Translate(chain, 0, dy);
                if (Math.Abs(dx) > 1e-9 || Math.Abs(dy) > 1e-9)
                {
                    report.ShiftedChains++;
                    report.MaxShiftMm = Math.Max(report.MaxShiftMm, Math.Max(Math.Abs(dx), Math.Abs(dy)) * 1000);
                }
            }

            foreach (var zone in zones) FillTie(zone, axes);
            report.UntiedZones = Check(zones, axes, outline, openings, edgeOffsetMm)
                .Select(issue => issue.ZoneId).Distinct().Count();
            return report;
        }

        /// <summary>Зоны, у которых ни одна грань не на сетке 10 мм от ближайшей оси.</summary>
        public static List<ZoneIssue> Check(IEnumerable<AdditionalZone> zones, IList<AlignAxis> axes,
            IList<Point3>? outline, IList<OpeningInfo>? openings, double edgeOffsetMm)
        {
            var issues = new List<ZoneIssue>();
            if (axes.Count == 0) return issues;
            var obstacles = Obstacles(outline, openings);
            var edgeM = Math.Max(0, edgeOffsetMm) / 1000.0;
            foreach (var zone in zones)
            {
                if (!TryBounds(zone, out var b)) continue;
                var problems = new List<string>();
                foreach (var vertical in new[] { true, false })
                {
                    var tie = BestTie(b, axes, vertical);
                    if (tie == null || Math.Abs(tie.Value.Frac) <= TieToleranceMm) continue;
                    var (gNeg, gPos) = Gaps(b, obstacles, vertical);
                    if (IsNarrow(gNeg, gPos, edgeM) && Math.Abs(gNeg - gPos) <= 0.001) continue;
                    problems.Add($"от оси {tie.Value.Axis.Name} {Mm(tie.Value.OffsetMm)} мм");
                }
                if (problems.Count == 0) continue;
                issues.Add(new ZoneIssue
                {
                    Kind = ZoneIssueKind.AxisTie,
                    Layer = zone.Layer,
                    ZoneId = zone.ZoneId,
                    Message = $"Зона {zone.ZoneId}: привязка к оси не кратна 10 мм ({string.Join("; ", problems)})"
                });
            }
            return issues;
        }

        private static double ChainShift(List<AdditionalZone> chain, IList<AlignAxis> axes,
            List<Seg> obstacles, double edgeM, bool vertical, GridAlignmentReport report)
        {
            if (!axes.Any(axis => axis.Vertical == vertical)) return 0;
            const double limit = StepMm / 1000.0;
            double lo = -limit, hi = limit;
            var bounds = new List<Bounds>();
            var narrow = new List<(double GNeg, double GPos)>();
            foreach (var zone in chain)
            {
                if (!TryBounds(zone, out var b)) continue;
                bounds.Add(b);
                var (gNeg, gPos) = Gaps(b, obstacles, vertical);
                if (IsNarrow(gNeg, gPos, edgeM))
                {
                    narrow.Add((gNeg, gPos));
                    continue;
                }
                // Не подходить к краю/отверстию ближе отступа (или ближе, чем уже есть).
                lo = Math.Max(lo, -(gNeg - Math.Min(gNeg, edgeM)));
                hi = Math.Min(hi, gPos - Math.Min(gPos, edgeM));
            }
            if (bounds.Count == 0) return 0;

            if (narrow.Count > 0)
            {
                var tightest = narrow.OrderBy(gap => gap.GNeg + gap.GPos).First();
                var center = (tightest.GPos - tightest.GNeg) / 2;
                report.CenteredZones += narrow.Count;
                return lo <= hi ? Math.Max(lo, Math.Min(hi, center)) : center;
            }
            if (lo > hi + 1e-9) return 0;

            var candidates = new List<double> { 0 };
            foreach (var b in bounds)
            {
                foreach (var frac in EdgeFracs(b, axes, vertical))
                {
                    candidates.Add(-frac / 1000);
                    candidates.Add((-frac + StepMm) / 1000);
                    candidates.Add((-frac - StepMm) / 1000);
                }
            }

            double best = 0;
            int bestTied = -1;
            foreach (var shift in candidates.Where(c => c >= lo - 1e-9 && c <= hi + 1e-9))
            {
                var tied = bounds.Count(b => EdgeFracs(b.Shift(shift, vertical), axes, vertical)
                    .Any(frac => Math.Abs(frac) <= TieToleranceMm));
                if (tied > bestTied || (tied == bestTied && Math.Abs(shift) < Math.Abs(best)))
                {
                    best = shift;
                    bestTied = tied;
                }
            }
            return best;
        }

        private static bool IsNarrow(double gNeg, double gPos, double edgeM) =>
            gNeg <= edgeM + StepMm / 1000 + 1e-6 && gPos <= edgeM + StepMm / 1000 + 1e-6;

        /// <summary>Остатки граней (мм, в пределах ±5) от ближайшей оси нужного направления.</summary>
        private static IEnumerable<double> EdgeFracs(Bounds b, IList<AlignAxis> axes, bool vertical)
        {
            var along = vertical ? (b.MinY + b.MaxY) / 2 : (b.MinX + b.MaxX) / 2;
            foreach (var edge in vertical ? new[] { b.MinX, b.MaxX } : new[] { b.MinY, b.MaxY })
            {
                var axis = NearestAxis(edge, along, axes, vertical);
                if (axis == null) continue;
                yield return Frac((edge - axis.PositionAt(along)) * 1000);
            }
        }

        private static (AlignAxis Axis, double OffsetMm, double Frac)? BestTie(Bounds b, IList<AlignAxis> axes,
            bool vertical)
        {
            var along = vertical ? (b.MinY + b.MaxY) / 2 : (b.MinX + b.MaxX) / 2;
            (AlignAxis Axis, double OffsetMm, double Frac)? best = null;
            foreach (var edge in vertical ? new[] { b.MinX, b.MaxX } : new[] { b.MinY, b.MaxY })
            {
                var axis = NearestAxis(edge, along, axes, vertical);
                if (axis == null) continue;
                var offset = (edge - axis.PositionAt(along)) * 1000;
                var frac = Frac(offset);
                if (best == null || Math.Abs(frac) < Math.Abs(best.Value.Frac)) best = (axis, offset, frac);
            }
            return best;
        }

        private static AlignAxis? NearestAxis(double value, double along, IList<AlignAxis> axes, bool vertical)
        {
            AlignAxis? best = null;
            var bestDistance = MaxAxisDistanceM;
            foreach (var axis in axes)
            {
                if (axis.Vertical != vertical) continue;
                var distance = Math.Abs(value - axis.PositionAt(along));
                if (distance <= bestDistance)
                {
                    best = axis;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static double Frac(double mm) => mm - StepMm * Math.Round(mm / StepMm, MidpointRounding.AwayFromZero);

        private static void FillTie(AdditionalZone zone, IList<AlignAxis> axes)
        {
            if (!TryBounds(zone, out var b)) return;
            var parts = new List<string>();
            var axisX = NearestAxis(b.MinX, (b.MinY + b.MaxY) / 2, axes, vertical: true);
            if (axisX != null)
            {
                zone.AxisNameX = axisX.Name;
                zone.AxisPosXM = axisX.PositionAt((b.MinY + b.MaxY) / 2);
                zone.OffsetFromAxisXMm = Math.Round((b.MinX - zone.AxisPosXM) * 1000, 1);
                parts.Add($"от {axisX.Name}: {Mm(zone.OffsetFromAxisXMm)}");
            }
            var axisY = NearestAxis(b.MinY, (b.MinX + b.MaxX) / 2, axes, vertical: false);
            if (axisY != null)
            {
                zone.AxisNameY = axisY.Name;
                zone.AxisPosYM = axisY.PositionAt((b.MinX + b.MaxX) / 2);
                zone.OffsetFromAxisYMm = Math.Round((b.MinY - zone.AxisPosYM) * 1000, 1);
                parts.Add($"от {axisY.Name}: {Mm(zone.OffsetFromAxisYMm)}");
            }
            zone.AxisTieLabel = string.Join(" · ", parts);
        }

        private static string Mm(double mm) =>
            (mm >= 0 ? "+" : "") + mm.ToString(Math.Abs(Frac(mm)) <= TieToleranceMm ? "0" : "0.#",
                CultureInfo.InvariantCulture);

        private static void Translate(IEnumerable<AdditionalZone> zones, double dx, double dy)
        {
            // Новые точки: Point3 могут быть общими у соседних зон.
            foreach (var zone in zones)
            {
                zone.Contour = zone.Contour.Select(p => new Point3(p.X + dx, p.Y + dy, p.Z)).ToList();
                if (zone.Placement != null)
                    zone.Placement = new Point3(zone.Placement.X + dx, zone.Placement.Y + dy, zone.Placement.Z);
            }
        }

        /// <summary>Связные группы зон одного ряда: зазор между рамками не больше 250 мм.</summary>
        private static List<List<AdditionalZone>> Chains(IList<AdditionalZone> zones)
        {
            var chains = new List<List<AdditionalZone>>();
            foreach (var layer in zones.GroupBy(zone => zone.Layer))
            {
                var items = layer.Where(zone => TryBounds(zone, out _)).ToList();
                var bounds = items.Select(zone => { TryBounds(zone, out var b); return b; }).ToList();
                var parent = Enumerable.Range(0, items.Count).ToArray();
                int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
                for (var i = 0; i < items.Count; i++)
                for (var j = i + 1; j < items.Count; j++)
                {
                    var gapX = Math.Max(bounds[i].MinX - bounds[j].MaxX, bounds[j].MinX - bounds[i].MaxX);
                    var gapY = Math.Max(bounds[i].MinY - bounds[j].MaxY, bounds[j].MinY - bounds[i].MaxY);
                    if (gapX <= ChainGapM && gapY <= ChainGapM) parent[Find(i)] = Find(j);
                }
                chains.AddRange(Enumerable.Range(0, items.Count)
                    .GroupBy(Find)
                    .Select(group => group.Select(i => items[i]).ToList()));
            }
            return chains;
        }

        /// <summary>
        /// Свободное расстояние от граней зоны до края плиты/отверстия в отрицательную и
        /// положительную сторону (по X для vertical, по Y иначе). Учитываются только
        /// препятствия целиком снаружи грани; дальше 1 м — бесконечность.
        /// </summary>
        private static (double Neg, double Pos) Gaps(Bounds b, List<Seg> obstacles, bool vertical)
        {
            double neg = double.PositiveInfinity, pos = double.PositiveInfinity;
            double lo = vertical ? b.MinY : b.MinX, hi = vertical ? b.MaxY : b.MaxX;
            double min = vertical ? b.MinX : b.MinY, max = vertical ? b.MaxX : b.MaxY;
            lo += 0.001;
            hi -= 0.001;
            if (hi <= lo) return (neg, pos);
            foreach (var s in obstacles)
            {
                // u — координата вдоль сдвига, v — поперёк.
                double u1 = vertical ? s.X1 : s.Y1, v1 = vertical ? s.Y1 : s.X1;
                double u2 = vertical ? s.X2 : s.Y2, v2 = vertical ? s.Y2 : s.X2;
                double t0 = 0, t1 = 1;
                if (Math.Abs(v2 - v1) < 1e-12)
                {
                    if (v1 < lo || v1 > hi) continue;
                }
                else
                {
                    var ta = (lo - v1) / (v2 - v1);
                    var tb = (hi - v1) / (v2 - v1);
                    t0 = Math.Max(0, Math.Min(ta, tb));
                    t1 = Math.Min(1, Math.Max(ta, tb));
                    if (t0 > t1) continue;
                }
                double ua = u1 + (u2 - u1) * t0, ub = u1 + (u2 - u1) * t1;
                double uMin = Math.Min(ua, ub), uMax = Math.Max(ua, ub);
                if (uMax <= min + 1e-6 && min - uMax < GapSearchM) neg = Math.Min(neg, min - uMax);
                else if (uMin >= max - 1e-6 && uMin - max < GapSearchM) pos = Math.Min(pos, uMin - max);
            }
            return (Math.Max(0, neg), Math.Max(0, pos));
        }

        private static List<Seg> Obstacles(IList<Point3>? outline, IList<OpeningInfo>? openings)
        {
            var segments = new List<Seg>();
            if (outline != null && outline.Count >= 3)
                for (var i = 0; i < outline.Count; i++)
                {
                    var a = outline[i];
                    var c = outline[(i + 1) % outline.Count];
                    segments.Add(new Seg(a.X, a.Y, c.X, c.Y));
                }
            foreach (var o in openings ?? new List<OpeningInfo>())
            {
                segments.Add(new Seg(o.MinXM, o.MinYM, o.MaxXM, o.MinYM));
                segments.Add(new Seg(o.MaxXM, o.MinYM, o.MaxXM, o.MaxYM));
                segments.Add(new Seg(o.MaxXM, o.MaxYM, o.MinXM, o.MaxYM));
                segments.Add(new Seg(o.MinXM, o.MaxYM, o.MinXM, o.MinYM));
            }
            return segments;
        }

        private static bool TryBounds(AdditionalZone zone, out Bounds bounds)
        {
            bounds = default;
            if (zone.Contour == null || zone.Contour.Count < 3) return false;
            bounds = new Bounds(zone.Contour.Min(p => p.X), zone.Contour.Max(p => p.X),
                zone.Contour.Min(p => p.Y), zone.Contour.Max(p => p.Y));
            return bounds.MaxX - bounds.MinX > 1e-6 && bounds.MaxY - bounds.MinY > 1e-6;
        }

        private readonly struct Bounds
        {
            public readonly double MinX, MaxX, MinY, MaxY;
            public Bounds(double minX, double maxX, double minY, double maxY)
            {
                MinX = minX; MaxX = maxX; MinY = minY; MaxY = maxY;
            }

            public Bounds Shift(double d, bool vertical) => vertical
                ? new Bounds(MinX + d, MaxX + d, MinY, MaxY)
                : new Bounds(MinX, MaxX, MinY + d, MaxY + d);
        }

        private readonly struct Seg
        {
            public readonly double X1, Y1, X2, Y2;
            public Seg(double x1, double y1, double x2, double y2) { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
        }
    }
}
