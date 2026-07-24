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
    }

    /// <summary>Строит регулярную мозаику As−фон из КЭ пластин.</summary>
    public static class MosaicBuilder
    {
        public static MosaicGrid Build(
            IList<LiraPlateElement> plates,
            RebarLayer layer,
            double asMainCm2PerM,
            int cellMm,
            double levelZM)
        {
            var ok = plates.Where(p => p.Rebar.Ok).ToList();
            if (ok.Count == 0)
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
            var minX = ok.Min(p => p.Centroid.X);
            var maxX = ok.Max(p => p.Centroid.X);
            var minY = ok.Min(p => p.Centroid.Y);
            var maxY = ok.Max(p => p.Centroid.Y);

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
                if (asAdd <= 0.01) continue;
                var ix = (int)Math.Floor((p.Centroid.X - minX) / cellM);
                var iy = (int)Math.Floor((p.Centroid.Y - minY) / cellM);
                if (ix < 0) ix = 0;
                if (ix > nx - 1) ix = nx - 1;
                if (iy < 0) iy = 0;
                if (iy > ny - 1) iy = ny - 1;
                values[iy][ix] = Math.Max(values[iy][ix], asAdd);
                ids[iy][ix].Add(p.Id);
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
                PlateIds = ids
            };
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
