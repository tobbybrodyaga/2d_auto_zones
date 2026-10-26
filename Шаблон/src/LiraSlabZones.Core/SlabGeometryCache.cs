using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public static class SlabGeometryCache
    {
        private sealed class Entry
        {
            public List<Point3> Outline { get; set; } = new List<Point3>();
            public List<OpeningInfo> Openings { get; set; } = new List<OpeningInfo>();
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();

        public static (List<Point3> Outline, List<OpeningInfo> Openings) Get(IList<LiraPlateElement> plates)
        {
            var key = Fingerprint(plates);
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out var cached))
                    return (Copy(cached.Outline), Copy(cached.Openings));
            }
            var outline = MeshBoundary.BuildOuterContour(plates);
            var openings = SlabOpenings.Detect(plates, outline);
            lock (Sync)
            {
                if (Entries.Count >= 8) Entries.Remove(Entries.Keys.First());
                Entries[key] = new Entry { Outline = Copy(outline), Openings = Copy(openings) };
            }
            return (outline, openings);
        }

        private static string Fingerprint(IList<LiraPlateElement> plates)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                foreach (var plate in plates.OrderBy(p => p.Id))
                {
                    hash = (hash ^ plate.Id) * 1099511628211L;
                    foreach (var point in plate.Contour)
                    {
                        hash = (hash ^ Math.Round(point.X, 6).GetHashCode()) * 1099511628211L;
                        hash = (hash ^ Math.Round(point.Y, 6).GetHashCode()) * 1099511628211L;
                    }
                }
                return $"{plates.Count}:{hash}";
            }
        }

        private static List<Point3> Copy(IEnumerable<Point3> points) =>
            points.Select(p => new Point3(p.X, p.Y, p.Z)).ToList();

        private static List<OpeningInfo> Copy(IEnumerable<OpeningInfo> openings) => openings.Select(op =>
            new OpeningInfo
            {
                OpeningId = op.OpeningId,
                MinXM = op.MinXM,
                MaxXM = op.MaxXM,
                MinYM = op.MinYM,
                MaxYM = op.MaxYM
            }).ToList();
    }
}
