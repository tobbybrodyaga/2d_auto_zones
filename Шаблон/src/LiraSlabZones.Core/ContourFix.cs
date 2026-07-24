using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Упорядочивание узлов КЭ в простой (непересекающийся) контур:
    /// в таблице TypeAndNodes порядок часто даёт «ромбы»/бантики вместо прямоугольников.
    /// </summary>
    public static class ContourFix
    {
        public static List<Point3> OrderAsSimplePolygon(IList<Point3> pts)
        {
            if (pts == null || pts.Count <= 3)
                return pts?.ToList() ?? new List<Point3>();

            double cx = 0, cy = 0, cz = 0;
            foreach (var p in pts)
            {
                cx += p.X;
                cy += p.Y;
                cz += p.Z;
            }
            double inv = 1.0 / pts.Count;
            cx *= inv;
            cy *= inv;
            cz *= inv;

            // Угловая сортировка вокруг центра → выпуклый обход (прямоугольник без бантика)
            var ordered = pts
                .Select(p => (P: p, A: Math.Atan2(p.Y - cy, p.X - cx)))
                .OrderBy(t => t.A)
                .Select(t => new Point3(t.P.X, t.P.Y, t.P.Z))
                .ToList();

            // Для 4 узлов: если площадь почти 0 — попробовать порядок по кратчайшему циклу
            if (ordered.Count == 4 && Math.Abs(SignedArea(ordered)) < 1e-8)
                ordered = OrderByNearestNeighbor(pts.ToList());

            // Нормальная ориентация (CCW в плане XY)
            if (SignedArea(ordered) < 0)
                ordered.Reverse();

            return ordered;
        }

        /// <summary>
        /// Габариты вдоль рёбер (не AABB): для прямоугольника — стороны, даже если повернут.
        /// </summary>
        public static void EdgeAlignedSize(IList<Point3> contour, out double widthM, out double lengthM)
        {
            widthM = 0.05;
            lengthM = 0.05;
            if (contour == null || contour.Count < 3) return;

            var edges = new List<double>();
            for (int i = 0; i < contour.Count; i++)
            {
                var a = contour[i];
                var b = contour[(i + 1) % contour.Count];
                edges.Add(Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)));
            }

            if (contour.Count == 4)
            {
                // противоположные рёбра: (0,2) и (1,3)
                widthM = Math.Max(0.05, (edges[0] + edges[2]) * 0.5);
                lengthM = Math.Max(0.05, (edges[1] + edges[3]) * 0.5);
                if (widthM > lengthM)
                    (widthM, lengthM) = (lengthM, widthM);
                return;
            }

            edges.Sort();
            widthM = Math.Max(0.05, edges[0]);
            lengthM = Math.Max(0.05, edges[edges.Count - 1]);
        }

        /// <summary>
        /// Угол сетки по осям (градусы): среднее направление «вертикальных» осей → 0 = оси X=const.
        /// </summary>
        public static double EstimateGridAngleDeg(IList<ConstructionAxis> axes)
        {
            if (axes == null || axes.Count == 0) return 0;
            var angles = new List<double>();
            foreach (var ax in axes)
            {
                if (ax.IsSegment)
                {
                    double dx = ax.X2 - ax.X1;
                    double dy = ax.Y2 - ax.Y1;
                    if (Math.Abs(dx) + Math.Abs(dy) < 1e-9) continue;
                    // угол линии к оси Y (вертикаль экрана / X=const)
                    double a = Math.Atan2(dx, dy) * 180.0 / Math.PI; // 0 = вертикаль
                    angles.Add(NormalizeAngle(a));
                }
                else if (ax.Vertical)
                {
                    angles.Add(0);
                }
                else
                {
                    angles.Add(90);
                }
            }

            if (angles.Count == 0) return 0;
            // медиана
            angles.Sort();
            return angles[angles.Count / 2];
        }

        private static List<Point3> OrderByNearestNeighbor(List<Point3> pts)
        {
            var left = new List<Point3>(pts);
            var result = new List<Point3> { left[0] };
            left.RemoveAt(0);
            while (left.Count > 0)
            {
                var last = result[result.Count - 1];
                int best = 0;
                double bestD = double.MaxValue;
                for (int i = 0; i < left.Count; i++)
                {
                    double d = Dist2(last, left[i]);
                    if (d < bestD) { bestD = d; best = i; }
                }
                result.Add(left[best]);
                left.RemoveAt(best);
            }
            return result;
        }

        private static double Dist2(Point3 a, Point3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static double SignedArea(IList<Point3> pts)
        {
            double a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var q = pts[(i + 1) % pts.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a * 0.5;
        }

        private static double NormalizeAngle(double deg)
        {
            while (deg > 90) deg -= 180;
            while (deg <= -90) deg += 180;
            return deg;
        }
    }
}
