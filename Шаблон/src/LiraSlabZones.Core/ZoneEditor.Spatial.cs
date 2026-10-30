using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public static partial class ZoneEditor
    {
        // FE geometry is fixed throughout EnforceRequiredGaps; zones remain mutable.
        // Keep the original FE order, including when IDs explicitly assigned to a zone
        // lie outside its current bounds.
        private sealed class IndexedPlates : Dictionary<int, LiraPlateElement>
        {
            private readonly ZoneSpatialIndex _index = new ZoneSpatialIndex();
            private readonly IList<LiraPlateElement> _plates;
            private readonly Dictionary<int, int> _positions = new();

            public IndexedPlates(IList<LiraPlateElement> plates)
            {
                _plates = plates;
                for (var i = 0; i < plates.Count; i++)
                {
                    var plate = plates[i];
                    Add(plate.Id, plate);
                    _positions.Add(plate.Id, i);
                    var b = PlateBounds(plate);
                    _index.Add(i, b.MinX, b.MaxX, b.MinY, b.MaxY);
                }
            }

            public IEnumerable<LiraPlateElement> Query(
                (double MinX, double MaxX, double MinY, double MaxY) b, IEnumerable<int>? assigned)
            {
                var indices = _index.Query(b.MinX, b.MaxX, b.MinY, b.MaxY);
                if (assigned != null)
                    indices = indices.Concat(assigned.Where(_positions.ContainsKey).Select(id => _positions[id]));
                return indices.Distinct().OrderBy(i => i).Select(i => _plates[i]);
            }
        }

        private static IEnumerable<LiraPlateElement> NearbyPlates(
            IReadOnlyDictionary<int, LiraPlateElement> plates,
            (double MinX, double MaxX, double MinY, double MaxY) bounds,
            double padding = 0, IEnumerable<int>? assigned = null) =>
            plates is IndexedPlates indexed
                ? indexed.Query((bounds.MinX - padding - 1e-6, bounds.MaxX + padding + 1e-6,
                    bounds.MinY - padding - 1e-6, bounds.MaxY + padding + 1e-6), assigned)
                : plates.Values;
    }
}
