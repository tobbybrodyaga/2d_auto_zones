using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public sealed class MosaicGrid
    {
        public int Nx { get; set; }
        public int Ny { get; set; }
        public int CellMm { get; set; }
        public double OriginXM { get; set; }
        public double OriginYM { get; set; }
        public double LevelZM { get; set; }
        /// <summary>AsAdditional см²/м, [iy][ix]</summary>
        public double[][] Values { get; set; } = Array.Empty<double[]>();
        public List<int>[][] PlateIds { get; set; } = Array.Empty<List<int>[]>();
        public Dictionary<int, Point3> PlateCentroids { get; set; } = new Dictionary<int, Point3>();
        public Dictionary<int, ElementBounds> PlateBounds { get; set; } = new Dictionary<int, ElementBounds>();
    }

    public sealed class ElementBounds
    {
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }
    }

    /// <summary>Строит регулярную мозаику As−фон из КЭ пластин.</summary>
    public static class MosaicBuilder
    {
        public const double PositiveResidualToleranceCm2PerM = 1e-9;

        public static MosaicGrid Build(
            IList<LiraPlateElement> plates,
            RebarLayer layer,
            double asMainCm2PerM,
            int cellMm,
            double levelZM)
        {
            var selectedLevel = MeshBoundary.FilterNearestLevel(plates, levelZM);
            var levelPlates = selectedLevel.Plates;
            levelZM = selectedLevel.ElevationZM;
            var ok = levelPlates.Where(p => p.Rebar.Ok).ToList();
            if (levelPlates.Count == 0 || ok.Count == 0)
            {
                return new MosaicGrid
                {
                    Nx = 0,
                    Ny = 0,
                    CellMm = cellMm,
                    LevelZM = levelZM,
                    Values = Array.Empty<double[]>(),
                    PlateIds = Array.Empty<List<int>[]>()
                };
            }

            var cellM = cellMm / 1000.0;
            var allPoints = levelPlates.SelectMany(p => p.Contour != null && p.Contour.Count >= 3
                ? p.Contour
                : new List<Point3> { p.Centroid }).ToList();
            var minX = allPoints.Min(p => p.X);
            var maxX = allPoints.Max(p => p.X);
            var minY = allPoints.Min(p => p.Y);
            var maxY = allPoints.Max(p => p.Y);

            // pad half cell
            minX -= cellM * 0.5;
            minY -= cellM * 0.5;
            maxX += cellM * 0.5;
            maxY += cellM * 0.5;

            var nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cellM));
            var ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cellM));

            var values = new double[ny][];
            var ids = new List<int>[ny][];
            for (var iy = 0; iy < ny; iy++)
            {
                values[iy] = new double[nx];
                ids[iy] = new List<int>[nx];
                for (var ix = 0; ix < nx; ix++)
                    ids[iy][ix] = new List<int>();
            }

            foreach (var p in ok)
            {
                var asAdd = p.Rebar.Get(layer) - asMainCm2PerM;
                if (asAdd <= PositiveResidualToleranceCm2PerM) continue;
                var contour = p.Contour;
                if (contour == null || contour.Count < 3)
                {
                    AddAtCentroid(p, asAdd);
                    continue;
                }

                var ix0 = Math.Max(0, (int)Math.Floor((contour.Min(q => q.X) - minX) / cellM));
                var ix1 = Math.Min(nx - 1, (int)Math.Floor((contour.Max(q => q.X) - minX) / cellM));
                var iy0 = Math.Max(0, (int)Math.Floor((contour.Min(q => q.Y) - minY) / cellM));
                var iy1 = Math.Min(ny - 1, (int)Math.Floor((contour.Max(q => q.Y) - minY) / cellM));
                var added = false;
                for (var iy = iy0; iy <= iy1; iy++)
                for (var ix = ix0; ix <= ix1; ix++)
                {
                    var x0 = minX + ix * cellM;
                    var y0 = minY + iy * cellM;
                    if (PolygonRectIntersectionArea(contour, x0, x0 + cellM, y0, y0 + cellM) <= 1e-10)
                        continue;
                    values[iy][ix] = Math.Max(values[iy][ix], asAdd);
                    if (!ids[iy][ix].Contains(p.Id)) ids[iy][ix].Add(p.Id);
                    added = true;
                }
                if (!added) AddAtCentroid(p, asAdd);
            }

            void AddAtCentroid(LiraPlateElement plate, double asAdd)
            {
                var ix = Math.Max(0, Math.Min(nx - 1, (int)Math.Floor((plate.Centroid.X - minX) / cellM)));
                var iy = Math.Max(0, Math.Min(ny - 1, (int)Math.Floor((plate.Centroid.Y - minY) / cellM)));
                values[iy][ix] = Math.Max(values[iy][ix], asAdd);
                if (!ids[iy][ix].Contains(plate.Id)) ids[iy][ix].Add(plate.Id);
            }

            return new MosaicGrid
            {
                Nx = nx,
                Ny = ny,
                CellMm = cellMm,
                OriginXM = minX,
                OriginYM = minY,
                LevelZM = levelZM,
                Values = values,
                PlateIds = ids,
                PlateCentroids = ok.ToDictionary(p => p.Id, p => p.Centroid),
                PlateBounds = ok.ToDictionary(p => p.Id, p =>
                {
                    var points = p.Contour != null && p.Contour.Count >= 3
                        ? p.Contour
                        : new List<Point3> { p.Centroid };
                    return new ElementBounds
                    {
                        MinX = points.Min(q => q.X),
                        MaxX = points.Max(q => q.X),
                        MinY = points.Min(q => q.Y),
                        MaxY = points.Max(q => q.Y)
                    };
                })
            };
        }

        private static double PolygonRectIntersectionArea(
            IList<Point3> contour, double minX, double maxX, double minY, double maxY)
        {
            var polygon = contour.Select(p => (X: p.X, Y: p.Y)).ToList();
            polygon = ClipVertical(polygon, minX, keepGreater: true);
            polygon = ClipVertical(polygon, maxX, keepGreater: false);
            polygon = ClipHorizontal(polygon, minY, keepGreater: true);
            polygon = ClipHorizontal(polygon, maxY, keepGreater: false);
            if (polygon.Count < 3) return 0;

            double area = 0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(area) * 0.5;
        }

        private static List<(double X, double Y)> ClipVertical(
            List<(double X, double Y)> input, double bound, bool keepGreater)
        {
            return ClipPolygon(input,
                p => keepGreater ? p.X >= bound : p.X <= bound,
                (a, b) =>
                {
                    var t = Math.Abs(b.X - a.X) < 1e-12 ? 0 : (bound - a.X) / (b.X - a.X);
                    return (bound, a.Y + t * (b.Y - a.Y));
                });
        }

        private static List<(double X, double Y)> ClipHorizontal(
            List<(double X, double Y)> input, double bound, bool keepGreater)
        {
            return ClipPolygon(input,
                p => keepGreater ? p.Y >= bound : p.Y <= bound,
                (a, b) =>
                {
                    var t = Math.Abs(b.Y - a.Y) < 1e-12 ? 0 : (bound - a.Y) / (b.Y - a.Y);
                    return (a.X + t * (b.X - a.X), bound);
                });
        }

        private static List<(double X, double Y)> ClipPolygon(
            List<(double X, double Y)> input,
            Func<(double X, double Y), bool> inside,
            Func<(double X, double Y), (double X, double Y), (double X, double Y)> intersection)
        {
            var output = new List<(double X, double Y)>();
            if (input.Count == 0) return output;
            var previous = input[input.Count - 1];
            var previousInside = inside(previous);
            foreach (var current in input)
            {
                var currentInside = inside(current);
                if (currentInside != previousInside)
                    output.Add(intersection(previous, current));
                if (currentInside) output.Add(current);
                previous = current;
                previousInside = currentInside;
            }
            return output;
        }

        public static double[][] SmoothSingleSpike(double[][] area, double spikeRatio = 1.8)
        {
            var ny = area.Length;
            if (ny == 0) return area;
            var nx = area[0].Length;
            var output = new double[ny][];
            for (var iy = 0; iy < ny; iy++)
            {
                output[iy] = new double[nx];
                Array.Copy(area[iy], output[iy], nx);
            }

            for (var iy = 1; iy < ny - 1; iy++)
            {
                for (var ix = 1; ix < nx - 1; ix++)
                {
                    var v = area[iy][ix];
                    if (v <= 0) continue;
                    var mean = (area[iy - 1][ix] + area[iy + 1][ix] + area[iy][ix - 1] + area[iy][ix + 1]) / 4.0;
                    if (mean <= 0) continue;
                    if (v > spikeRatio * mean)
                        output[iy][ix] = mean;
                }
            }
            return output;
        }
    }
}
