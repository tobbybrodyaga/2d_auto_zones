using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

namespace LiraSlabZones.Core
{
    /// <summary>Поворот исходной геометрии в строительную систему, до построения мозаики.</summary>
    public sealed class SlabCoordinateFrame
    {
        public double PivotXM { get; set; }
        public double PivotYM { get; set; }
        public double RotationDeg { get; set; }
    }

    public sealed class SlabAxisAlignmentReport
    {
        public AnalysisResult Result { get; internal set; } = new AnalysisResult();
        public string Source { get; internal set; } = "контур";
        public double RotationDeg { get; internal set; }
        public double ResidualArcSeconds { get; internal set; }
        public bool Changed { get; internal set; }
        public string Summary => $"Выравнивание: источник = {Source}, угол = {RotationDeg:0.#########}° " +
            $"({Dms(RotationDeg)}), остаточный перекос опорных рёбер ≤ {ResidualArcSeconds:0.###}″";

        private static string Dms(double value)
        {
            var seconds = Math.Abs(value) * 3600;
            var degrees = (int)(seconds / 3600);
            var minutes = (int)((seconds - degrees * 3600) / 60);
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}°{2}′{3:0.###}″",
                value < 0 ? "−" : "", degrees, minutes, seconds - degrees * 3600 - minutes * 60);
        }
    }

    public static class SlabAxisAlignment
    {
        private readonly struct Edge
        {
            public readonly double Angle, Length;
            public Edge(double x1, double y1, double x2, double y2)
            {
                Angle = Wrap(Math.Atan2(y2 - y1, x2 - x1) * 180 / Math.PI);
                Length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            }
        }

        /// <summary>Угол по модулю 90°, в диапазоне [−45°,45°).</summary>
        private static double Wrap(double angle) => angle - 90 * Math.Floor((angle + 45) / 90);

        private static IEnumerable<Edge> Loop(IEnumerable<Point3> points)
        {
            var loop = points.ToList();
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                yield return new Edge(a.X, a.Y, b.X, b.Y);
            }
        }

        // Круговая гистограмма: направления у 0°/90° принадлежат одному пику.
        // Уточнение МНК по 4θ сохраняет точность double и объединяет ортогональные рёбра.
        private static double Estimate(List<Edge> input, out double residual)
        {
            var edges = input.Where(e => e.Length >= 0.05).ToList();
            residual = 0;
            if (edges.Count == 0) return double.NaN;
            var bins = new double[360];
            foreach (var e in edges)
                bins[((int)Math.Round((e.Angle + 45) * 4)) % bins.Length] += e.Length;
            var peak = Array.IndexOf(bins, bins.Max()) / 4.0 - 45;
            var selected = edges.Where(e => Math.Abs(Wrap(e.Angle - peak)) <= 0.5).ToList();
            var angle = peak;
            for (var i = 0; i < 2; i++)
            {
                var sx = selected.Sum(e => e.Length * Math.Cos(e.Angle * Math.PI / 45));
                var sy = selected.Sum(e => e.Length * Math.Sin(e.Angle * Math.PI / 45));
                angle = Wrap(Math.Atan2(sy, sx) * 45 / Math.PI);
                selected = edges.Where(e => Math.Abs(Wrap(e.Angle - angle)) <= 0.5).ToList();
            }
            residual = selected.Max(e => Math.Abs(Wrap(e.Angle - angle))) * 3600;
            return angle;
        }

        internal static double OutlineAngle(AnalysisResult source)
        {
            return Estimate(ContourEdges(source), out _);
        }

        private static List<Edge> ContourEdges(AnalysisResult source)
        {
            var edges = Loop(source.Outline).ToList();
            // Отверстия представлены AABB; их реальные направления можно брать только из якорей КЭ.
            foreach (var opening in source.Openings.Where(o => o.MeshAnchors.Count >= 3))
                edges.AddRange(Loop(opening.MeshAnchors.Select(a => a.Position)));
            return edges;
        }

        public static SlabAxisAlignmentReport Align(AnalysisResult source, AnalysisSettings? settings = null)
        {
            var cfg = Clone(settings ?? source.Settings);
            var report = new SlabAxisAlignmentReport { Result = source };
            var contour = ContourEdges(source);
            var angle = Estimate(contour, out var residual);
            var delta = -angle;
            var link = source.RevitLink;
            if (link != null && link.Matches(source.ElevationZM) && link.Registration?.Reliable == true)
            {
                var target = Estimate(link.Grids.Select(g => new Edge(g.X1, g.Y1, g.X2, g.Y2)).ToList(), out residual);
                if (!double.IsNaN(target))
                {
                    delta = Wrap(cfg.RotationDeg - target);
                    report.Source = "оси Revit";
                }
            }
            if (double.IsNaN(delta))
            {
                angle = Estimate(source.Axes.Where(a => a.IsSegment)
                    .Select(a => new Edge(a.X1, a.Y1, a.X2, a.Y2)).ToList(), out residual);
                delta = -angle;
                report.Source = "оси ЛИРЫ (запасной вариант; проверьте направление)";
            }
            if (double.IsNaN(delta) || source.Plates.Count == 0)
                throw new InvalidOperationException("Не удалось определить направление: нет прямых рёбер контура или осей.");
            report.RotationDeg = delta;
            report.ResidualArcSeconds = residual;
            // Повторное нажатие не меняет геометрию и ручные правки зон.
            if (Math.Abs(delta) < 1e-10 && (link != null || Math.Abs(cfg.RotationDeg) < 1e-10)) return report;

            var (px, py) = LiraRevitTransform.ComputePivot(source);
            var rotation = new LiraRevitTransform(new AnalysisSettings { RotationDeg = delta }, px, py);
            Point3 Rotate(Point3 p)
            {
                var (x, y) = rotation.ToRevit(p.X, p.Y);
                return new Point3(x, y, p.Z);
            }
            var copies = new Dictionary<LiraPlateElement, LiraPlateElement>();
            LiraPlateElement Plate(LiraPlateElement p)
            {
                if (copies.TryGetValue(p, out var copy)) return copy;
                copy = new LiraPlateElement
                {
                    Id = p.Id, TypeCode = p.TypeCode, NodeIds = p.NodeIds.ToList(),
                    Centroid = Rotate(p.Centroid), Contour = p.Contour.Select(Rotate).ToList(),
                    WidthM = p.WidthM, LengthM = p.LengthM, Rebar = Clone(p.Rebar)
                };
                copies.Add(p, copy);
                return copy;
            }
            var plates = source.Plates.Select(Plate).ToList();
            var all = source.AllPlates.Select(Plate).ToList();
            var axes = Clone(source.Axes);
            foreach (var axis in axes)
            {
                // Оси X/Y без концов сначала преобразуются в отрезки.
                var a = Rotate(new Point3(axis.IsSegment ? axis.X1 : axis.Vertical ? axis.Position : px - 100,
                    axis.IsSegment ? axis.Y1 : axis.Vertical ? py - 100 : axis.Position, 0));
                var b = Rotate(new Point3(axis.IsSegment ? axis.X2 : axis.Vertical ? axis.Position : px + 100,
                    axis.IsSegment ? axis.Y2 : axis.Vertical ? py + 100 : axis.Position, 0));
                axis.IsSegment = true;
                axis.X1 = a.X; axis.Y1 = a.Y; axis.X2 = b.X; axis.Y2 = b.Y;
                axis.Vertical = Math.Abs(b.X - a.X) < Math.Abs(b.Y - a.Y);
                axis.Position = axis.Vertical ? (a.X + b.X) / 2 : (a.Y + b.Y) / 2;
            }
            var openings = source.UseCustomOpenings ? Clone(source.Openings) : null;
            if (openings != null)
                foreach (var opening in openings)
                {
                    var corners = opening.MeshAnchors.Count >= 3
                        ? opening.MeshAnchors.Select(a => a.Position).ToList()
                        : new List<Point3> { new Point3(opening.MinXM, opening.MinYM, 0),
                            new Point3(opening.MaxXM, opening.MinYM, 0), new Point3(opening.MaxXM, opening.MaxYM, 0),
                            new Point3(opening.MinXM, opening.MaxYM, 0) };
                    var rotated = corners.Select(Rotate).ToList();
                    opening.MinXM = rotated.Min(p => p.X); opening.MaxXM = rotated.Max(p => p.X);
                    opening.MinYM = rotated.Min(p => p.Y); opening.MaxYM = rotated.Max(p => p.Y);
                    foreach (var anchor in opening.MeshAnchors) anchor.Position = Rotate(anchor.Position);
                }
            var boundaries = Clone(source.BoundaryConditions);
            foreach (var edge in boundaries.SelectMany(b => b.Edges))
            { edge.Start = Rotate(edge.Start); edge.End = Rotate(edge.End); }

            // При связи сохраняем положение в Revit: Rостат = Rсовмещения − Rстроит.
            // Без связи предпросмотр показывает строительные X/Y горизонтально/вертикально.
            cfg.RotationDeg = link != null ? cfg.RotationDeg - delta : 0;
            var frame = new SlabCoordinateFrame
            {
                PivotXM = px, PivotYM = py,
                RotationDeg = (source.CoordinateFrame?.RotationDeg ?? 0) + delta
            };
            var result = SlabZoneAnalyzer.BuildResult(source.DocumentName, source.DocumentPath, source.NodeCount,
                plates, cfg, axes, source.ElevationZM, source.ElevationLabel, skipLevelFilter: true,
                openings: openings, useCustomOpenings: source.UseCustomOpenings, coordinateFrame: frame);
            result.AllPlates = all;
            result.AvailableLevels = Clone(source.AvailableLevels);
            result.BoundaryConditions = boundaries;
            result.UnitsNote = source.UnitsNote;
            result.RevitLink = Clone(link);
            if (result.RevitLink?.Registration != null)
            {
                result.RevitLink.Registration.RotationDeg = cfg.RotationDeg;
                result.RevitLink.Registration.OffsetXM = cfg.OffsetXM;
                result.RevitLink.Registration.OffsetYM = cfg.OffsetYM;
            }
            report.Result = result;
            report.Changed = true;
            return report;
        }

        private static T Clone<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))!;
    }
}
