using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Внешний контур плиты по граничным рёбрам КЭ (не AABB).
    /// </summary>
    public static class MeshBoundary
    {
        public static List<Point3> BuildOuterContour(IList<LiraPlateElement> plates)
        {
            if (plates == null || plates.Count == 0)
                return new List<Point3>();

            var edgeCount = new Dictionary<(int A, int B), int>();
            var edgeGeom = new Dictionary<(int A, int B), (Point3 Pa, Point3 Pb, int NodeA, int NodeB)>();

            foreach (var plate in plates)
            {
                var ids = plate.NodeIds;
                var pts = plate.Contour;
                if (ids.Count < 3 || pts.Count < 3) continue;
                int n = Math.Min(ids.Count, pts.Count);
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    int na = ids[i], nb = ids[j];
                    if (na == nb) continue;
                    var key = na < nb ? (na, nb) : (nb, na);
                    edgeCount.TryGetValue(key, out int c);
                    edgeCount[key] = c + 1;
                    if (c == 0)
                    {
                        // Pa соответствует меньшему номеру узла в key
                        if (na < nb)
                            edgeGeom[key] = (pts[i], pts[j], na, nb);
                        else
                            edgeGeom[key] = (pts[j], pts[i], nb, na);
                    }
                }
            }

            var adj = new Dictionary<int, List<(int To, Point3 FromPt, Point3 ToPt)>>();
            foreach (var kv in edgeCount)
            {
                if (kv.Value != 1) continue;
                if (!edgeGeom.TryGetValue(kv.Key, out var g)) continue;
                Add(adj, g.NodeA, g.NodeB, g.Pa, g.Pb);
                Add(adj, g.NodeB, g.NodeA, g.Pb, g.Pa);
            }

            if (adj.Count == 0)
                return FallbackAabb(plates);

            var loops = new List<List<Point3>>();
            var used = new HashSet<(int, int)>();

            foreach (var startNode in adj.Keys)
            {
                if (!adj.TryGetValue(startNode, out var starts)) continue;
                foreach (var first in starts)
                {
                    var e0 = Norm(startNode, first.To);
                    if (!used.Add(e0)) continue;

                    var loop = new List<Point3> { first.FromPt };
                    int prev = startNode;
                    int cur = first.To;
                    var curPt = first.ToPt;
                    int safety = 0;
                    bool closed = false;

                    while (safety++ < 500000)
                    {
                        loop.Add(curPt);
                        if (cur == startNode && loop.Count > 3)
                        {
                            closed = true;
                            break;
                        }

                        if (!adj.TryGetValue(cur, out var nexts))
                            break;

                        (int To, Point3 FromPt, Point3 ToPt)? pick = null;
                        foreach (var cand in nexts)
                        {
                            if (cand.To == prev) continue;
                            var ek = Norm(cur, cand.To);
                            if (used.Contains(ek)) continue;
                            pick = cand;
                            break;
                        }

                        if (pick == null)
                        {
                            foreach (var cand in nexts)
                            {
                                if (cand.To == startNode && Norm(cur, cand.To) != e0 ||
                                    cand.To == startNode)
                                {
                                    pick = cand;
                                    break;
                                }
                            }
                        }

                        if (pick == null) break;

                        used.Add(Norm(cur, pick.Value.To));
                        prev = cur;
                        cur = pick.Value.To;
                        curPt = pick.Value.ToPt;
                    }

                    if (closed && loop.Count >= 4)
                        loops.Add(loop);
                }
            }

            if (loops.Count == 0)
                return FallbackAabb(plates);

            return loops.OrderByDescending(l => Math.Abs(PolygonArea(l))).First();
        }

        public static List<LiraPlateElement> FilterDominantLevel(IList<LiraPlateElement> plates, double zTol = 0.15) =>
            FilterDominantLevelEx(plates, zTol).Plates;

        /// <summary>
        /// Только пластины, у которых все узлы на одном уровне Z (стены/наклонные отсекаются).
        /// </summary>
        public static List<LiraPlateElement> FilterHorizontalPlates(
            IList<LiraPlateElement> plates, double sameZTolM = 0.005)
        {
            var list = new List<LiraPlateElement>(plates.Count);
            foreach (var p in plates)
            {
                if (IsHorizontalPlate(p, sameZTolM))
                    list.Add(p);
            }
            return list;
        }

        /// <summary>
        /// true, если все узлы контура лежат на одном Z (в пределах sameZTolM).
        /// </summary>
        public static bool IsHorizontalPlate(LiraPlateElement p, double sameZTolM = 0.005)
        {
            if (p?.Contour == null || p.Contour.Count < 3)
                return false;

            double z0 = p.Contour[0].Z;
            for (int i = 1; i < p.Contour.Count; i++)
            {
                if (Math.Abs(p.Contour[i].Z - z0) > sameZTolM)
                    return false;
            }
            return true;
        }

        public static (List<LiraPlateElement> Plates, double ElevationZM) FilterDominantLevelEx(
            IList<LiraPlateElement> plates, double zTol = 0.15)
        {
            if (plates.Count == 0)
                return (new List<LiraPlateElement>(), 0);

            var buckets = BuildZBuckets(plates, zTol);
            var best = buckets.Values.OrderByDescending(v => v.Count).First();
            double z = best.Average(p => p.Centroid.Z);
            return (best, z);
        }

        /// <summary>
        /// Берёт корзину КЭ, ближайшую к targetZM (не обязательно самую многочисленную).
        /// </summary>
        public static (List<LiraPlateElement> Plates, double ElevationZM) FilterNearestLevel(
            IList<LiraPlateElement> plates, double targetZM, double zTol = 0.15)
        {
            if (plates.Count == 0)
                return (new List<LiraPlateElement>(), targetZM);

            var buckets = BuildZBuckets(plates, zTol);
            var best = buckets.Values
                .OrderBy(v => Math.Abs(v.Average(p => p.Centroid.Z) - targetZM))
                .ThenByDescending(v => v.Count)
                .First();
            double z = best.Average(p => p.Centroid.Z);
            return (best, z);
        }

        public static List<ElevationLevelInfo> CollectLevels(
            IList<LiraPlateElement> plates,
            IList<(string Name, double Z)>? marks,
            double zTol = 0.15)
        {
            var list = new List<ElevationLevelInfo>();
            var buckets = BuildZBuckets(plates, zTol);

            foreach (var kv in buckets.OrderByDescending(b => b.Value.Average(p => p.Centroid.Z)))
            {
                double z = kv.Value.Average(p => p.Centroid.Z);
                string? markName = null;
                if (marks != null)
                {
                    foreach (var (name, mz) in marks)
                    {
                        if (Math.Abs(mz - z) <= Math.Max(zTol, 0.05) && !string.IsNullOrWhiteSpace(name))
                        {
                            markName = name;
                            break;
                        }
                    }
                }

                list.Add(new ElevationLevelInfo
                {
                    ZM = z,
                    PlateCount = kv.Value.Count,
                    FromMarks = markName != null,
                    Label = markName != null
                        ? $"{markName}  (Z={z:F3} м)"
                        : $"Z = {z:F3} м"
                });
            }

            // отметки без КЭ рядом — тоже в список (пользователь увидит 0 КЭ)
            if (marks != null)
            {
                foreach (var (name, mz) in marks)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (list.Any(l => Math.Abs(l.ZM - mz) <= Math.Max(zTol, 0.05))) continue;
                    list.Add(new ElevationLevelInfo
                    {
                        ZM = mz,
                        PlateCount = 0,
                        FromMarks = true,
                        Label = $"{name}  (Z={mz:F3} м)"
                    });
                }
            }

            return list.OrderByDescending(l => l.ZM).ToList();
        }

        private static Dictionary<int, List<LiraPlateElement>> BuildZBuckets(
            IList<LiraPlateElement> plates, double zTol)
        {
            var buckets = new Dictionary<int, List<LiraPlateElement>>();
            foreach (var p in plates)
            {
                int key = (int)Math.Round(p.Centroid.Z / zTol);
                if (!buckets.TryGetValue(key, out var list))
                {
                    list = new List<LiraPlateElement>();
                    buckets[key] = list;
                }
                list.Add(p);
            }
            return buckets;
        }

        private static void Add(
            Dictionary<int, List<(int To, Point3 FromPt, Point3 ToPt)>> adj,
            int from, int to, Point3 fromPt, Point3 toPt)
        {
            if (!adj.TryGetValue(from, out var list))
            {
                list = new List<(int, Point3, Point3)>();
                adj[from] = list;
            }
            list.Add((to, fromPt, toPt));
        }

        private static (int, int) Norm(int a, int b) => a < b ? (a, b) : (b, a);

        private static double PolygonArea(List<Point3> pts)
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

        private static List<Point3> FallbackAabb(IList<LiraPlateElement> plates)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, z = 0;
            foreach (var p in plates)
            foreach (var c in p.Contour)
            {
                if (c.X < minX) minX = c.X;
                if (c.Y < minY) minY = c.Y;
                if (c.X > maxX) maxX = c.X;
                if (c.Y > maxY) maxY = c.Y;
                z = c.Z;
            }
            return new List<Point3>
            {
                new Point3(minX, minY, z),
                new Point3(maxX, minY, z),
                new Point3(maxX, maxY, z),
                new Point3(minX, maxY, z)
            };
        }

        /// <summary>Точка внутри полигона контура (ray casting).</summary>
        public static bool PointInPolygon(double x, double y, IList<Point3> poly)
        {
            if (poly == null || poly.Count < 3) return true;
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;
                if (((yi > y) != (yj > y)) &&
                    (x < (xj - xi) * (y - yi) / ((yj - yi) + 1e-30) + xi))
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// Подрезка AABB зоны inset-контуром плиты (как SmartRebar: зона не выходит за опалубку).
        /// Центр зоны обязан лежать внутри полигона.
        /// </summary>
        public static bool ClipRectToSlab(
            ref double minX, ref double maxX, ref double minY, ref double maxY,
            IList<Point3>? outline, double insetMm)
        {
            if (outline == null || outline.Count < 3) return true;
            var inset = (insetMm > 0 ? insetMm : 30) / 1000.0;
            var oMinX = outline.Min(p => p.X) + inset;
            var oMaxX = outline.Max(p => p.X) - inset;
            var oMinY = outline.Min(p => p.Y) + inset;
            var oMaxY = outline.Max(p => p.Y) - inset;
            if (oMaxX <= oMinX || oMaxY <= oMinY) return false;

            // Жёсткая подрезка пересечением с AABB
            minX = Math.Max(minX, oMinX);
            maxX = Math.Min(maxX, oMaxX);
            minY = Math.Max(minY, oMinY);
            maxY = Math.Min(maxY, oMaxY);
            if (maxX - minX < 0.05 || maxY - minY < 0.05) return false;

            var cx = (minX + maxX) * 0.5;
            var cy = (minY + maxY) * 0.5;
            if (!PointInPolygon(cx, cy, outline)) return false;

            // Углы: хотя бы 3 из 4 внутри или на границе AABB (уже внутри AABB)
            int inside = 0;
            if (PointInPolygon(minX, minY, outline)) inside++;
            if (PointInPolygon(maxX, minY, outline)) inside++;
            if (PointInPolygon(maxX, maxY, outline)) inside++;
            if (PointInPolygon(minX, maxY, outline)) inside++;
            // Для вогнутых контуров AABB-углы могут вылезать — требуем центр + ≥2 угла
            return inside >= 2;
        }
    }
}
