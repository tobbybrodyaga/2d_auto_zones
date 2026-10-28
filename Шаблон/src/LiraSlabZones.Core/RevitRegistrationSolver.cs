using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Совмещение плана ЛИРЫ с Revit: контур плиты и отверстия ЛИРЫ подгоняются жёстким
    /// преобразованием (поворот + сдвиг) к кромкам перекрытий уровня Revit (ICP «точка — отрезок»).
    /// Начальные приближения — по преобладающему направлению кромок (с точностью до 90°)
    /// и по центрам; выбирается вариант с наибольшей долей совпавшего контура.
    /// </summary>
    public static class RevitRegistrationSolver
    {
        private const double SourceStepM = 0.2;
        private const double MatchToleranceM = 0.02;
        private const double RmsToleranceM = 0.05;
        private const double GridSnapDeg = 0.3;

        public static RevitRegistration? Solve(AnalysisResult result, RevitLinkInfo link, List<string>? trace = null)
        {
            var sourceEdges = SourceEdges(result);
            var source = Sample(sourceEdges, SourceStepM);
            var target = link.Floors
                .SelectMany(floor => floor.Loops)
                .SelectMany(LoopEdges)
                .Where(edge => edge.Length > 1e-6)
                .ToList();
            if (source.Count < 10 || target.Count < 3) return null;
            var index = new SegmentIndex(target, 0.5);

            Fit? best = null;
            var guesses = InitialGuesses(result, sourceEdges, source, target)
                .Select(guess => (Guess: guess, StartDist: 10.0)).ToList();
            // Плита ЛИРЫ может быть частью перекрытия Revit (блок здания) — центры тогда
            // не совпадают, поэтому дополнительно перебираются сдвиги по всему перекрытию.
            guesses.AddRange(CoarseSearch(source, target, index,
                    guesses.Select(g => g.Guess.Theta).Distinct().ToList())
                .Select(guess => (Guess: guess, StartDist: 1.0)));
            foreach (var (guess, startDist) in guesses)
            {
                var fit = Icp(source, index, guess.Theta, guess.Tx, guess.Ty, fixedRotation: false, startDist);
                trace?.Add(FormattableString.Invariant(
                    $"старт {guess.Theta * 180 / Math.PI:0.###}° ({guess.Tx:0.##}; {guess.Ty:0.##}) r={startDist} → ") +
                    (fit == null ? "нет" : FormattableString.Invariant(
                        $"{fit.Theta * 180 / Math.PI:0.###}° ({fit.Tx:0.###}; {fit.Ty:0.###}) доля {fit.Share:0.###} СКО {fit.RmsM * 1000:0.#} мм")));
                if (fit != null && (best == null || fit.Better(best))) best = fit;
            }
            if (best == null) return null;

            var snapped = false;
            var gridAngle = DominantAngle(link.Grids.Select(grid =>
                new Edge(grid.X1, grid.Y1, grid.X2, grid.Y2)).ToList());
            if (!double.IsNaN(gridAngle))
            {
                var sourceAngle = SlabAxisAlignment.OutlineAngle(result) * Math.PI / 180;
                var delta = WrapQuarter(gridAngle - sourceAngle - best.Theta);
                if (Math.Abs(delta) > 1e-9 && Math.Abs(delta) < GridSnapDeg * Math.PI / 180)
                {
                    var fixedFit = Icp(source, index, best.Theta + delta, best.Tx, best.Ty, fixedRotation: true, 0.2);
                    if (fixedFit != null && fixedFit.RmsM <= best.RmsM + 0.003 &&
                        fixedFit.Share >= best.Share - 0.05)
                    {
                        best = fixedFit;
                        snapped = true;
                    }
                }
                else if (Math.Abs(delta) <= 1e-9) snapped = true;
            }

            // Revit = R·ЛИРА + t, а LiraRevitTransform: R·(x − p) + p + o  ⇒  o = t − p + R·p.
            var (px, py) = LiraRevitTransform.ComputePivot(result);
            double cos = Math.Cos(best.Theta), sin = Math.Sin(best.Theta);
            return new RevitRegistration
            {
                RotationDeg = best.Theta * 180 / Math.PI,
                OffsetXM = best.Tx - px + cos * px - sin * py,
                OffsetYM = best.Ty - py + sin * px + cos * py,
                RmsMm = Math.Round(best.RmsM * 1000, 1),
                MatchedShare = best.Share,
                Samples = source.Count,
                GridSnapped = snapped
            };
        }

        private sealed class Fit
        {
            public double Theta, Tx, Ty, Share, RmsM;

            public bool Better(Fit other) => Share > other.Share + 0.02 ||
                                             (Share > other.Share - 0.02 && RmsM < other.RmsM);
        }

        private readonly struct Guess
        {
            public readonly double Theta, Tx, Ty;
            public Guess(double theta, double tx, double ty) { Theta = theta; Tx = tx; Ty = ty; }
        }

        private static IEnumerable<Guess> InitialGuesses(AnalysisResult result, List<Edge> sourceEdges,
            List<Point3> source, List<Edge> target)
        {
            // Текущие настройки пользователя — тоже приближение.
            var settings = result.Settings ?? new AnalysisSettings();
            var current = LiraRevitTransform.For(result, settings);
            var (cx, cy) = current.ToRevit(0, 0);
            yield return new Guess(settings.RotationDeg * Math.PI / 180, cx, cy);

            var angleSource = DominantAngle(sourceEdges);
            var angleTarget = DominantAngle(target);
            var baseTheta = double.IsNaN(angleSource) || double.IsNaN(angleTarget) ? 0 : angleTarget - angleSource;

            var targetPoints = Sample(target, 0.5);
            var (tcx, tcy) = Centroid(targetPoints);
            double tMinX = target.Min(e => Math.Min(e.Ax, e.Bx)), tMaxX = target.Max(e => Math.Max(e.Ax, e.Bx));
            double tMinY = target.Min(e => Math.Min(e.Ay, e.By)), tMaxY = target.Max(e => Math.Max(e.Ay, e.By));
            var (scx, scy) = Centroid(source);
            for (var k = 0; k < 4; k++)
            {
                var theta = baseTheta + k * Math.PI / 2;
                double cos = Math.Cos(theta), sin = Math.Sin(theta);
                yield return new Guess(theta, tcx - (cos * scx - sin * scy), tcy - (sin * scx + cos * scy));

                var rotated = source.Select(p => (X: cos * p.X - sin * p.Y, Y: sin * p.X + cos * p.Y)).ToList();
                double rMinX = rotated.Min(p => p.X), rMaxX = rotated.Max(p => p.X);
                double rMinY = rotated.Min(p => p.Y), rMaxY = rotated.Max(p => p.Y);
                yield return new Guess(theta, (tMinX + tMaxX - rMinX - rMaxX) / 2, (tMinY + tMaxY - rMinY - rMaxY) / 2);
                yield return new Guess(theta, tMinX - rMinX, tMinY - rMinY);
                yield return new Guess(theta, tMaxX - rMaxX, tMaxY - rMaxY);
            }
        }

        /// <summary>
        /// Перебор сдвигов с шагом 0,5 м при каждом кандидате поворота: оценка — сколько
        /// из ~200 точек контура ЛИРЫ лежит не дальше 0,35 м от кромок. Лучшие — в ICP.
        /// </summary>
        private static IEnumerable<Guess> CoarseSearch(List<Point3> source, List<Edge> target,
            SegmentIndex index, List<double> thetas)
        {
            const double step = 0.5, near = 0.35;
            var stride = Math.Max(1, source.Count / 200);
            var sparse = source.Where((_, i) => i % stride == 0).ToList();
            double tMinX = target.Min(e => Math.Min(e.Ax, e.Bx)), tMaxX = target.Max(e => Math.Max(e.Ax, e.Bx));
            double tMinY = target.Min(e => Math.Min(e.Ay, e.By)), tMaxY = target.Max(e => Math.Max(e.Ay, e.By));
            var found = new List<(Guess Guess, int Score)>();
            foreach (var theta in thetas)
            {
                double cos = Math.Cos(theta), sin = Math.Sin(theta);
                var rotated = sparse.Select(p => (X: cos * p.X - sin * p.Y, Y: sin * p.X + cos * p.Y)).ToList();
                double rMinX = rotated.Min(p => p.X), rMaxX = rotated.Max(p => p.X);
                double rMinY = rotated.Min(p => p.Y), rMaxY = rotated.Max(p => p.Y);
                double x0 = Math.Min(tMinX - rMinX, tMaxX - rMaxX) - 2, x1 = Math.Max(tMinX - rMinX, tMaxX - rMaxX) + 2;
                double y0 = Math.Min(tMinY - rMinY, tMaxY - rMaxY) - 2, y1 = Math.Max(tMinY - rMinY, tMaxY - rMaxY) + 2;
                var local = new List<(Guess Guess, int Score)>();
                for (var tx = x0; tx <= x1; tx += step)
                for (var ty = y0; ty <= y1; ty += step)
                {
                    var score = 0;
                    foreach (var p in rotated)
                        if (index.Nearest(p.X + tx, p.Y + ty, near, out _, out _, out _)) score++;
                    if (score >= sparse.Count / 4) local.Add((new Guess(theta, tx, ty), score));
                }
                // Лучшие и не соседние друг с другом (дальше 2 м).
                foreach (var candidate in local.OrderByDescending(c => c.Score))
                {
                    if (found.Count(f => f.Guess.Theta == theta) >= 4) break;
                    if (found.Any(f => f.Guess.Theta == theta &&
                                       Math.Abs(f.Guess.Tx - candidate.Guess.Tx) < 2 &&
                                       Math.Abs(f.Guess.Ty - candidate.Guess.Ty) < 2))
                        continue;
                    found.Add(candidate);
                }
            }
            return found.Select(f => f.Guess);
        }

        private static Fit? Icp(List<Point3> source, SegmentIndex index, double theta, double tx, double ty,
            bool fixedRotation, double startDist = 10.0)
        {
            var maxDist = startDist;
            var floor = Math.Min(startDist, 0.3);
            var pairs = new List<(double Ax, double Ay, double Bx, double By, double D)>(source.Count);
            for (var iteration = 0; iteration < 120; iteration++)
            {
                double cos = Math.Cos(theta), sin = Math.Sin(theta);
                pairs.Clear();
                foreach (var s in source)
                {
                    double qx = cos * s.X - sin * s.Y + tx, qy = sin * s.X + cos * s.Y + ty;
                    if (index.Nearest(qx, qy, maxDist, out var nx, out var ny, out var d))
                        pairs.Add((s.X, s.Y, nx, ny, d));
                }
                if (pairs.Count < Math.Max(10, source.Count / 10)) return null;

                double ax = 0, ay = 0, bx = 0, by = 0;
                foreach (var p in pairs) { ax += p.Ax; ay += p.Ay; bx += p.Bx; by += p.By; }
                ax /= pairs.Count; ay /= pairs.Count; bx /= pairs.Count; by /= pairs.Count;
                var newTheta = theta;
                if (!fixedRotation)
                {
                    double sxx = 0, sxy = 0;
                    foreach (var p in pairs)
                    {
                        double dax = p.Ax - ax, day = p.Ay - ay, dbx = p.Bx - bx, dby = p.By - by;
                        sxx += dax * dbx + day * dby;
                        sxy += dax * dby - day * dbx;
                    }
                    newTheta = Math.Atan2(sxy, sxx);
                }
                double nc = Math.Cos(newTheta), ns = Math.Sin(newTheta);
                var newTx = bx - (nc * ax - ns * ay);
                var newTy = by - (ns * ax + nc * ay);
                var change = Math.Abs(newTheta - theta) * 50 + Math.Abs(newTx - tx) + Math.Abs(newTy - ty);
                theta = newTheta; tx = newTx; ty = newTy;

                // Радиус не сужается ниже 0,3 м до сходимости: большинство точек скользит вдоль
                // длинных кромок с нулевой ошибкой, и медиана иначе отсекла бы короткие кромки,
                // которые одни фиксируют сдвиг вдоль длинных. Потом — уточнение при 0,05 м.
                var median = pairs.Select(p => p.D).OrderBy(d => d).ElementAt(pairs.Count / 2);
                var newMax = Math.Max(floor, Math.Min(maxDist, 3 * median + 0.02));
                var shrunk = newMax < maxDist - 1e-6;
                maxDist = newMax;
                if (change < 1e-6 && !shrunk)
                {
                    if (floor <= 0.05) break;
                    floor = 0.05;
                }
            }

            double c = Math.Cos(theta), si = Math.Sin(theta);
            int matched = 0, near = 0;
            double sum = 0;
            foreach (var s in source)
            {
                double qx = c * s.X - si * s.Y + tx, qy = si * s.X + c * s.Y + ty;
                if (!index.Nearest(qx, qy, RmsToleranceM, out _, out _, out var d)) continue;
                near++;
                sum += d * d;
                if (d <= MatchToleranceM) matched++;
            }
            return new Fit
            {
                Theta = theta,
                Tx = tx,
                Ty = ty,
                Share = (double)matched / source.Count,
                RmsM = near > 0 ? Math.Sqrt(sum / near) : double.MaxValue
            };
        }

        private static List<Edge> SourceEdges(AnalysisResult result)
        {
            var edges = new List<Edge>();
            if (result.Outline != null && result.Outline.Count >= 3)
                edges.AddRange(LoopEdges(result.Outline));
            foreach (var opening in result.Openings ?? new List<OpeningInfo>())
            {
                if (opening.MaxXM - opening.MinXM < 0.05 || opening.MaxYM - opening.MinYM < 0.05) continue;
                edges.AddRange(LoopEdges(new List<Point3>
                {
                    new Point3(opening.MinXM, opening.MinYM, 0), new Point3(opening.MaxXM, opening.MinYM, 0),
                    new Point3(opening.MaxXM, opening.MaxYM, 0), new Point3(opening.MinXM, opening.MaxYM, 0)
                }));
            }
            return edges.Where(edge => edge.Length > 1e-6).ToList();
        }

        private static IEnumerable<Edge> LoopEdges(IList<Point3> loop)
        {
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                yield return new Edge(a.X, a.Y, b.X, b.Y);
            }
        }

        private static List<Point3> Sample(IEnumerable<Edge> edges, double step)
        {
            var points = new List<Point3>();
            foreach (var edge in edges)
            {
                var n = Math.Max(1, (int)Math.Ceiling(edge.Length / step));
                for (var i = 0; i < n; i++)
                {
                    var t = (double)i / n;
                    points.Add(new Point3(edge.Ax + (edge.Bx - edge.Ax) * t, edge.Ay + (edge.By - edge.Ay) * t, 0));
                }
            }
            return points;
        }

        private static (double X, double Y) Centroid(IReadOnlyCollection<Point3> points) =>
            points.Count == 0 ? (0, 0) : (points.Average(p => p.X), points.Average(p => p.Y));

        /// <summary>Преобладающее направление кромок по модулю 90°, рад; NaN — кромок нет.</summary>
        internal static double DominantAngle(IList<Edge> edges)
        {
            if (edges.Count == 0) return double.NaN;
            var bins = new double[90];
            foreach (var edge in edges)
                bins[(int)Math.Floor(QuarterDeg(edge)) % 90] += edge.Length;
            var peak = 0;
            for (var i = 1; i < bins.Length; i++)
                if (bins[i] > bins[peak]) peak = i;
            if (bins[peak] <= 0) return double.NaN;

            // Уточнение: среднее по 4θ только для кромок в пределах ±2° от пика.
            double sx = 0, sy = 0;
            foreach (var edge in edges)
            {
                var deg = QuarterDeg(edge);
                var diff = Math.Abs(deg - (peak + 0.5));
                if (Math.Min(diff, 90 - diff) > 2) continue;
                var a = 4 * deg * Math.PI / 180;
                sx += edge.Length * Math.Cos(a);
                sy += edge.Length * Math.Sin(a);
            }
            var mean = Math.Atan2(sy, sx) / 4;
            return mean < 0 ? mean + Math.PI / 2 : mean;
        }

        private static double QuarterDeg(Edge edge)
        {
            var deg = Math.Atan2(edge.By - edge.Ay, edge.Bx - edge.Ax) * 180 / Math.PI;
            deg %= 90;
            return deg < 0 ? deg + 90 : deg;
        }

        /// <summary>Угол, приведённый к (−45°; 45°], рад.</summary>
        internal static double WrapQuarter(double angle)
        {
            var quarter = Math.PI / 2;
            angle %= quarter;
            if (angle > quarter / 2) angle -= quarter;
            if (angle <= -quarter / 2) angle += quarter;
            return angle;
        }

        internal readonly struct Edge
        {
            public readonly double Ax, Ay, Bx, By;
            public Edge(double ax, double ay, double bx, double by) { Ax = ax; Ay = ay; Bx = bx; By = by; }
            public double Length => Math.Sqrt((Bx - Ax) * (Bx - Ax) + (By - Ay) * (By - Ay));
        }

        /// <summary>Равномерная сетка для поиска ближайшей точки на отрезках.</summary>
        private sealed class SegmentIndex
        {
            private readonly List<Edge> _edges;
            private readonly double _cell;
            private readonly Dictionary<(int, int), List<int>> _cells = new Dictionary<(int, int), List<int>>();
            private readonly int[] _stamp;
            private int _query;

            public SegmentIndex(List<Edge> edges, double cell)
            {
                _edges = edges;
                _cell = cell;
                _stamp = new int[edges.Count];
                for (var i = 0; i < edges.Count; i++)
                {
                    var e = edges[i];
                    int x0 = Key(Math.Min(e.Ax, e.Bx)), x1 = Key(Math.Max(e.Ax, e.Bx));
                    int y0 = Key(Math.Min(e.Ay, e.By)), y1 = Key(Math.Max(e.Ay, e.By));
                    for (var x = x0; x <= x1; x++)
                    for (var y = y0; y <= y1; y++)
                    {
                        if (!_cells.TryGetValue((x, y), out var list)) _cells[(x, y)] = list = new List<int>();
                        list.Add(i);
                    }
                }
            }

            private int Key(double v) => (int)Math.Floor(v / _cell);

            public bool Nearest(double x, double y, double maxDist, out double nx, out double ny, out double dist)
            {
                _query++;
                nx = ny = 0;
                dist = double.MaxValue;
                int cx = Key(x), cy = Key(y);
                var rings = (int)Math.Ceiling(maxDist / _cell) + 1;
                for (var r = 0; r <= rings; r++)
                {
                    for (var ix = cx - r; ix <= cx + r; ix++)
                    for (var iy = cy - r; iy <= cy + r; iy++)
                    {
                        if (Math.Max(Math.Abs(ix - cx), Math.Abs(iy - cy)) != r) continue;
                        if (!_cells.TryGetValue((ix, iy), out var list)) continue;
                        foreach (var i in list)
                        {
                            if (_stamp[i] == _query) continue;
                            _stamp[i] = _query;
                            var e = _edges[i];
                            double dx = e.Bx - e.Ax, dy = e.By - e.Ay;
                            var len2 = dx * dx + dy * dy;
                            var t = len2 > 0 ? Math.Max(0, Math.Min(1, ((x - e.Ax) * dx + (y - e.Ay) * dy) / len2)) : 0;
                            double px = e.Ax + dx * t, py = e.Ay + dy * t;
                            var d = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                            if (d < dist) { dist = d; nx = px; ny = py; }
                        }
                    }
                    // Всё, что дальше просмотренных колец, не ближе r·cell.
                    if (dist <= r * _cell) break;
                }
                return dist <= maxDist;
            }
        }
    }
}
