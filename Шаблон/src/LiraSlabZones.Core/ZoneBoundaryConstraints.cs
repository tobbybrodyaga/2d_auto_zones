using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace LiraSlabZones.Core
{
    public sealed class ZoneBoundaryCheck
    {
        public bool Checked { get; set; }
        public bool Allowed { get; set; } = true;
        public bool CenteredException { get; set; }
        public double RequiredMm { get; set; }
        public double ActualMm { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Неизменяемая допустимая область для раскладки. Создаётся до разбиения рамок;
    /// цветные пятна и геометрия КЭ при проверках не меняются.
    /// </summary>
    public sealed class ZoneBoundaryConstraints
    {
        private const double Scale = 1000000;
        private const double ToleranceM = 0.000002;
        private readonly Paths64 _physical = new Paths64();
        private readonly Paths64 _allowed = new Paths64();
        private readonly List<(double Ax, double Ay, double Bx, double By)> _edges = new();
        private readonly double _edgeM;
        public bool HasOutline { get; }
        public double EdgeOffsetMm => _edgeM * 1000;

        public List<List<Point3>> ClipDesignContour(IList<Point3> contour)
        {
            if(!HasOutline) return new List<List<Point3>> {contour.ToList()};
            var z=contour.Count>0?contour[0].Z:0;
            return Clipper.Intersect(new Paths64 {Path(contour)},_allowed,FillRule.NonZero)
                .Where(p=>Math.Abs(Clipper.Area(p))>0.5)
                .Select(p=>p.Select(v=>new Point3(v.X/Scale,v.Y/Scale,z)).ToList()).ToList();
        }

        /// <summary>Предельные торцы прямоугольной полосы по реальному внутреннему контуру.</summary>
        public bool TryAxialLimits(ZonePatchFrameBounds core,ZoneDirection direction,out double min,out double max)
        {
            min=direction==ZoneDirection.X?core.MinX:core.MinY;
            max=direction==ZoneDirection.X?core.MaxX:core.MaxY;
            if(!HasOutline || !Contains(_allowed,core)) return false;
            ZonePatchFrameBounds Band(double a,double b)=>direction==ZoneDirection.X
                ?new ZonePatchFrameBounds(a,b,core.MinY,core.MaxY):new ZonePatchFrameBounds(core.MinX,core.MaxX,a,b);
            var vertices=_allowed.SelectMany(p=>p).ToList();
            var lo=(direction==ZoneDirection.X?vertices.Min(p=>p.X):vertices.Min(p=>p.Y))/Scale;
            var hi=min;
            for(var i=0;i<30;i++){var mid=(lo+hi)/2;if(Contains(_allowed,Band(mid,max))) hi=mid;else lo=mid;}
            min=hi;
            lo=max;hi=(direction==ZoneDirection.X?vertices.Max(p=>p.X):vertices.Max(p=>p.Y))/Scale;
            for(var i=0;i<30;i++){var mid=(lo+hi)/2;if(Contains(_allowed,Band(min,mid))) lo=mid;else hi=mid;}
            max=lo;
            return true;
        }

        public ZoneBoundaryConstraints(IList<Point3>? outline, IList<OpeningInfo>? openings, double edgeOffsetMm)
        {
            _edgeM = Math.Max(0, edgeOffsetMm) / 1000;
            HasOutline = outline != null && outline.Count >= 3;
            if (!HasOutline) return;
            var shell = Clipper.Union(new Paths64 { Path(outline!) }, FillRule.NonZero);
            var holes = new Paths64();
            foreach (var hole in openings ?? Array.Empty<OpeningInfo>())
                holes.Add(Rect(new ZonePatchFrameBounds(hole.MinXM, hole.MaxXM, hole.MinYM, hole.MaxYM)));
            _physical = Clipper.Difference(shell, holes, FillRule.NonZero);
            _allowed = _edgeM <= 0 ? _physical : Clipper.InflatePaths(
                _physical, -_edgeM * Scale, JoinType.Miter, EndType.Polygon);
            foreach (var loop in _physical)
                for (var i = 0; i < loop.Count; i++)
                {
                    var a = loop[i]; var b = loop[(i + 1) % loop.Count];
                    _edges.Add((a.X / Scale, a.Y / Scale, b.X / Scale, b.Y / Scale));
                }
        }

        public ZoneBoundaryCheck Check(IList<Point3> contour, ZoneDirection direction)
        {
            if (!HasOutline) return new ZoneBoundaryCheck();
            if (contour == null || contour.Count < 3)
                return new ZoneBoundaryCheck { Checked = true, Allowed = false,
                    RequiredMm = EdgeOffsetMm, Message = "Нет геометрии для проверки отступа" };
            var b = Bounds(contour);
            var path = Path(contour);
            if (Math.Abs(Clipper.Area(path)) < 0.5)
                return new ZoneBoundaryCheck { Checked = true, Allowed = false, RequiredMm = EdgeOffsetMm,
                    Message = "Вырожденный контур зоны: проверка отступа невозможна" };
            if (contour.Count == 4 && Math.Abs(Math.Abs(Clipper.Area(path)) / (Scale * Scale) -
                (b.MaxX - b.MinX) * (b.MaxY - b.MinY)) <= 1e-9)
                return Check(b, direction);
            // Подрезанная/гнутая зона проверяется по своему полигону, а не по AABB.
            var inside = Contains(_physical, path);
            var result = new ZoneBoundaryCheck { Checked = true, RequiredMm = EdgeOffsetMm,
                Allowed = inside && Contains(_allowed, path) };
            result.ActualMm = inside && _edges.Count > 0 ? _edges.Min(e =>
                Enumerable.Range(0, contour.Count).Min(i =>
                {
                    var a = contour[i]; var next = contour[(i + 1) % contour.Count];
                    return Math.Min(PointSegment(a.X, a.Y, e.Ax, e.Ay, e.Bx, e.By),
                        Math.Min(PointSegment(e.Ax, e.Ay, a.X, a.Y, next.X, next.Y),
                            PointSegment(e.Bx, e.By, a.X, a.Y, next.X, next.Y)));
                })) * 1000 : 0;
            if (!result.Allowed) result.Message = inside
                ? $"Отступ от грани плиты/отверстия {result.ActualMm:0.#} мм, требуется {EdgeOffsetMm:0.#} мм"
                : "Зона выходит за контур плиты или пересекает отверстие";
            return result;
        }

        public ZoneBoundaryCheck Check(ZonePatchFrameBounds b, ZoneDirection direction)
        {
            var result = new ZoneBoundaryCheck { Checked = HasOutline, RequiredMm = EdgeOffsetMm };
            if (!HasOutline) return result;
            var inside = Contains(_physical, b);
            result.ActualMm = inside && _edges.Count > 0 ? _edges.Min(e => Distance(b, e)) * 1000 : 0;
            if (inside && Contains(_allowed, b)) return result;
            if (inside && result.ActualMm > ToleranceM * 1000 &&
                TryNarrowCenter(b, direction, out var shift, out var lower, out var upper) &&
                Math.Abs(shift) <= 0.001)
            {
                // Исключение касается только двух противоположных параллельных граней.
                // Отверстие/излом в любом другом месте не может быть скрыт центрированием.
                var otherEdges = _edges.Where((_, i) => i != lower && i != upper).ToList();
                if (otherEdges.All(e => Distance(b, e) >= _edgeM - ToleranceM))
                {
                    result.CenteredException = true;
                    result.Message = $"Узкое место: зона по центру, равные зазоры {result.ActualMm:0.#} мм " +
                        $"вместо {EdgeOffsetMm:0.#} мм";
                    return result;
                }
            }
            result.Allowed = false;
            result.Message = inside
                ? $"Отступ от грани плиты/отверстия {result.ActualMm:0.#} мм, требуется {EdgeOffsetMm:0.#} мм"
                : "Зона выходит за контур плиты или пересекает отверстие";
            return result;
        }

        /// <summary>Сдвигает только запас габарита; required включает расчётное пятно и анкеровку.</summary>
        public bool TryFit(ZonePatchFrameBounds original, ZonePatchFrameBounds required, ZoneDirection direction,
            out ZonePatchFrameBounds fitted, out ZoneBoundaryCheck check)
        {
            fitted = original;
            check = Check(original, direction);
            if (check.Allowed) return true;
            // Если даже обязательная часть не помещается, сдвиг прямоугольника её не исправит.
            if (!Check(required, direction).Allowed)
            {
                check.Message += "; сдвиг невозможен без потери покрытия пятна или анкеровки";
                return false;
            }
            var shifts = new List<(double X, double Y)> { (0, 0) };
            if (TryNarrowCenter(original, direction, out var centered, out _, out _))
                shifts.Add(direction == ZoneDirection.X ? (0, centered) : (centered, 0));
            var corners = new[] { (original.MinX, original.MinY), (original.MaxX, original.MinY),
                (original.MaxX, original.MaxY), (original.MinX, original.MaxY) };
            foreach (var loop in _allowed)
            foreach (var p in loop)
            foreach (var corner in corners)
            {
                var dx = p.X / Scale - corner.Item1;
                var dy = p.Y / Scale - corner.Item2;
                shifts.Add((dx, 0)); shifts.Add((0, dy)); shifts.Add((dx, dy));
            }
            // Для наклонной грани ближайшая допустимая точка находится на её отрезке.
            foreach (var loop in _allowed)
            for (var i = 0; i < loop.Count; i++)
            foreach (var corner in corners)
            {
                var a = loop[i]; var b = loop[(i + 1) % loop.Count];
                var ax = a.X / Scale; var ay = a.Y / Scale;
                var dx = (b.X - a.X) / Scale; var dy = (b.Y - a.Y) / Scale;
                var len = dx * dx + dy * dy;
                var t = len < 1e-18 ? 0 : Math.Max(0, Math.Min(1,
                    ((corner.Item1 - ax) * dx + (corner.Item2 - ay) * dy) / len));
                shifts.Add((ax + t * dx - corner.Item1, ay + t * dy - corner.Item2));
            }
            foreach (var shift in shifts.Distinct().OrderBy(s => Math.Abs(s.X) + Math.Abs(s.Y)))
            {
                var candidate = Shift(original, shift.X, shift.Y);
                if (!ContainsRequired(candidate, required)) continue;
                var tested = Check(candidate, direction);
                if (!tested.Allowed) continue;
                fitted = candidate; check = tested;
                if (!check.CenteredException)
                    check.Message = $"До разбиения: зона смещена внутрь допустимого контура " +
                        $"X={shift.X * 1000:0.#}, Y={shift.Y * 1000:0.#} мм";
                return true;
            }
            check.Message += "; требуется переразбивка, покрытие и анкеровка сохранены без сдвига";
            return false;
        }

        public bool Allows(IList<Point3> contour, ZoneDirection direction)
        {
            if (!HasOutline) return true;
            if (contour == null || contour.Count < 3) return false;
            var path = Path(contour);
            return Math.Abs(Clipper.Area(path)) >= 0.5 &&
                (Contains(_allowed, path) || Check(contour, direction).Allowed);
        }

        public List<AdditionalZone> Snapshot(IEnumerable<AdditionalZone> zones)
        {
            var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<List<AdditionalZone>>(
                Newtonsoft.Json.JsonConvert.SerializeObject(zones)) ?? new List<AdditionalZone>();
            foreach (var zone in copy) zone.BoundaryConstraints = this;
            return copy;
        }

        /// <summary>После всех перемещений не допускаем потери уже покрытого КЭ.
        /// При конфликте возвращаем исходную раскладку ряда, сохраняя диагностику границ.</summary>
        public int PreserveCoverage(IList<AdditionalZone> zones, IList<AdditionalZone> baseline,
            IList<LiraPlateElement> plates, AnalysisSettings settings,
            IList<Point3>? outline, IList<OpeningInfo>? openings)
        {
            var restored = 0;
            foreach (var group in baseline.GroupBy(z => z.Layer))
            {
                var before = ZoneCoverageRules.Prepare(group.ToList());
                var after = ZoneCoverageRules.Prepare(zones.Where(z => z.Layer == group.Key).ToList());
                var background = group.Key switch
                {
                    RebarLayer.As1 => settings.AsMainAs1, RebarLayer.As2 => settings.AsMainAs2,
                    RebarLayer.As3 => settings.AsMainAs3, _ => settings.AsMainAs4
                };
                var losesCoverage = plates.Any(plate => plate.Rebar.Ok &&
                    plate.Rebar.Get(group.Key) - background > MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                    !ZoneCoverageRules.CoversOrBridgesGap(after, plate, group.Key,
                        plate.Rebar.Get(group.Key) - background, outline, openings) &&
                    ZoneCoverageRules.CoversOrBridgesGap(before, plate, group.Key,
                        plate.Rebar.Get(group.Key) - background, outline, openings));
                if (!losesCoverage) continue;
                for (var i = zones.Count - 1; i >= 0; i--)
                    if (zones[i].Layer == group.Key) zones.RemoveAt(i);
                foreach (var zone in Snapshot(before))
                {
                    zone.Comment += "; увязка ряда отменена для сохранения покрытия КЭ; требуется переразбивка";
                    zone.StatusColor = "warn";
                    zones.Add(zone);
                }
                restored++;
            }
            return restored;
        }

        public List<ZoneIssue> CheckZones(IEnumerable<AdditionalZone> zones)
        {
            var issues = new List<ZoneIssue>();
            foreach (var zone in zones)
            {
                zone.BoundaryConstraints = this;
                zone.BoundaryCheck = Check(zone.Contour, zone.Direction);
                if (!zone.BoundaryCheck.Checked) continue;
                if (!zone.BoundaryCheck.Allowed)
                    issues.Add(new ZoneIssue { Kind = ZoneIssueKind.BoundaryClearance, ZoneId = zone.ZoneId,
                        Layer = zone.Layer, Message = $"Зона {zone.ZoneId}: {zone.BoundaryCheck.Message}" });
                else if (zone.BoundaryCheck.CenteredException && zone.StatusColor != "error")
                    zone.StatusColor = "warn";
            }
            return issues;
        }

        private bool TryNarrowCenter(ZonePatchFrameBounds b, ZoneDirection direction,
            out double shift, out int lower, out int upper)
        {
            var xBars = direction == ZoneDirection.X;
            double neg = double.NegativeInfinity, pos = double.PositiveInfinity;
            lower = upper = -1; shift = 0;
            var cmin = xBars ? b.MinY : b.MinX; var cmax = xBars ? b.MaxY : b.MaxX;
            var amin = xBars ? b.MinX : b.MinY; var amax = xBars ? b.MaxX : b.MaxY;
            for (var i = 0; i < _edges.Count; i++)
            {
                var e = _edges[i];
                var c1 = xBars ? e.Ay : e.Ax; var c2 = xBars ? e.By : e.Bx;
                var a1 = xBars ? e.Ax : e.Ay; var a2 = xBars ? e.Bx : e.By;
                if (Math.Abs(c1 - c2) > ToleranceM || Math.Min(a1, a2) > amin + ToleranceM ||
                    Math.Max(a1, a2) < amax - ToleranceM) continue;
                if (c1 <= cmin + ToleranceM && c1 > neg) { neg = c1; lower = i; }
                if (c1 >= cmax - ToleranceM && c1 < pos) { pos = c1; upper = i; }
            }
            if (lower < 0 || upper < 0 || cmin - neg < -ToleranceM || pos - cmax < -ToleranceM ||
                cmin - neg + pos - cmax >= 2 * _edgeM - ToleranceM) return false;
            shift = ((pos - cmax) - (cmin - neg)) / 2;
            return true;
        }

        private static bool Contains(Paths64 paths, ZonePatchFrameBounds b) =>
            b.MaxX > b.MinX && b.MaxY > b.MinY &&
            Contains(paths, Rect(b));

        private static bool Contains(Paths64 paths, Path64 path) =>
            Clipper.Difference(new Paths64 { path }, paths, FillRule.NonZero)
                .Sum(part => Math.Abs(Clipper.Area(part))) / (Scale * Scale) <= 1e-9;

        private static bool ContainsRequired(ZonePatchFrameBounds b, ZonePatchFrameBounds r) =>
            b.MinX <= r.MinX + ToleranceM && b.MaxX >= r.MaxX - ToleranceM &&
            b.MinY <= r.MinY + ToleranceM && b.MaxY >= r.MaxY - ToleranceM;

        public static ZonePatchFrameBounds Bounds(IList<Point3> points) => new ZonePatchFrameBounds(
            points.Min(p => p.X), points.Max(p => p.X), points.Min(p => p.Y), points.Max(p => p.Y));

        private static ZonePatchFrameBounds Shift(ZonePatchFrameBounds b, double dx, double dy) =>
            new ZonePatchFrameBounds(b.MinX + dx, b.MaxX + dx, b.MinY + dy, b.MaxY + dy);

        private static Path64 Path(IEnumerable<Point3> points) => new Path64(points.Select(p =>
            new Point64((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale))));

        private static Path64 Rect(ZonePatchFrameBounds b) => Path(new[] { new Point3(b.MinX, b.MinY, 0),
            new Point3(b.MaxX, b.MinY, 0), new Point3(b.MaxX, b.MaxY, 0), new Point3(b.MinX, b.MaxY, 0) });

        private static double Distance(ZonePatchFrameBounds b, (double Ax, double Ay, double Bx, double By) e)
        {
            var corners = new[] { (b.MinX, b.MinY), (b.MaxX, b.MinY), (b.MaxX, b.MaxY), (b.MinX, b.MaxY) };
            var result = corners.Min(p => PointSegment(p.Item1, p.Item2, e.Ax, e.Ay, e.Bx, e.By));
            foreach (var p in new[] { (e.Ax, e.Ay), (e.Bx, e.By) })
            {
                var dx = Math.Max(b.MinX - p.Item1, Math.Max(0, p.Item1 - b.MaxX));
                var dy = Math.Max(b.MinY - p.Item2, Math.Max(0, p.Item2 - b.MaxY));
                result = Math.Min(result, Math.Sqrt(dx * dx + dy * dy));
            }
            return result;
        }

        private static double PointSegment(double x, double y, double ax, double ay, double bx, double by)
        {
            var dx = bx - ax; var dy = by - ay; var len = dx * dx + dy * dy;
            var t = len < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len));
            dx = x - ax - t * dx; dy = y - ay - t * dy;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
