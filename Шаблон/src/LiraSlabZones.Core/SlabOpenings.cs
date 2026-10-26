using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace LiraSlabZones.Core
{
    public static class SlabOpenings
    {
        private const double Scale = 1000000.0;

        public static List<OpeningInfo> Detect(IList<LiraPlateElement> plates, IList<Point3> outline)
        {
            if (plates.Count == 0 || outline.Count < 3) return new List<OpeningInfo>();
            var widths = plates.Where(p => p.Contour.Count >= 3)
                .Select(p => p.Contour.Max(q => q.X) - p.Contour.Min(q => q.X))
                .Where(v => v > 1e-5).OrderBy(v => v).ToList();
            var heights = plates.Where(p => p.Contour.Count >= 3)
                .Select(p => p.Contour.Max(q => q.Y) - p.Contour.Min(q => q.Y))
                .Where(v => v > 1e-5).OrderBy(v => v).ToList();
            if (widths.Count == 0 || heights.Count == 0) return new List<OpeningInfo>();
            var minWidth = 2 * widths[widths.Count / 2];
            var minHeight = 2 * heights[heights.Count / 2];
            var occupied = Clipper.Union(new Paths64(plates.Where(p => p.Contour.Count >= 3)
                .Select(p => ToPath(p.Contour))), FillRule.NonZero);
            var empty = Clipper.Difference(new Paths64 { ToPath(outline) }, occupied, FillRule.NonZero);
            var result = new List<OpeningInfo>();
            foreach (var path in empty.Where(p => p.Count >= 3))
            {
                var minX = path.Min(p => p.X) / Scale;
                var maxX = path.Max(p => p.X) / Scale;
                var minY = path.Min(p => p.Y) / Scale;
                var maxY = path.Max(p => p.Y) / Scale;
                if (maxX - minX + 1e-6 < minWidth || maxY - minY + 1e-6 < minHeight) continue;
                result.Add(new OpeningInfo
                {
                    OpeningId = result.Count + 1,
                    MinXM = minX,
                    MaxXM = maxX,
                    MinYM = minY,
                    MaxYM = maxY
                });
            }
            return result;
        }

        private static Path64 ToPath(IEnumerable<Point3> contour) => new Path64(contour.Select(p =>
            new Point64((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale))));
    }
}
