using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace LiraSlabZones.Core
{
    public static partial class ZoneCoverageRules
    {
        /// <summary>
        /// Prepare once for a read-only batch of FE checks. Rebuild after any geometry,
        /// layer, capacity, step or design-cover change; this is deliberately not a global cache.
        /// </summary>
        internal static IList<AdditionalZone> Prepare(IList<AdditionalZone> zones) =>
            zones is PreparedZones ? zones : new PreparedZones(zones);

        private sealed class PreparedZones : ReadOnlyCollection<AdditionalZone>
        {
            private readonly ZoneSpatialIndex _index = new ZoneSpatialIndex();
            private readonly (double MinX, double MaxX, double MinY, double MaxY)[] _bounds;
            private readonly Dictionary<RebarLayer, double> _edgeCover = new();
            private readonly Dictionary<RebarLayer, List<(double Capacity, int Step)>> _steps = new();

            public PreparedZones(IList<AdditionalZone> zones) : base(zones.ToList())
            {
                _bounds = new (double, double, double, double)[Count];
                for (var i = 0; i < Count; i++)
                {
                    var zone = this[i];
                    if (!_edgeCover.TryGetValue(zone.Layer, out var cover) || zone.DesignEdgeCoverMm > cover)
                        _edgeCover[zone.Layer] = zone.DesignEdgeCoverMm;
                    if (zone.Contour.Count < 3) continue;
                    var b = _bounds[i] = Bounds(zone);
                    _index.Add(i, b.MinX, b.MaxX, b.MinY, b.MaxY);
                    if (!_steps.TryGetValue(zone.Layer, out var steps))
                        _steps[zone.Layer] = steps = new List<(double, int)>();
                    steps.Add((zone.AsCoveredCm2PerM, zone.BarStepMm));
                }
                // Only distinct capacities/steps matter for the original eligible-zone padding.
                foreach (var layer in _steps.Keys.ToList())
                    _steps[layer] = _steps[layer].Distinct().ToList();
            }

            public double EdgeCover(RebarLayer layer) =>
                _edgeCover.TryGetValue(layer, out var value) ? value : 0;

            public double SearchPadding(RebarLayer layer, double requiredAs)
            {
                var maximum = int.MinValue;
                if (_steps.TryGetValue(layer, out var steps))
                    foreach (var item in steps)
                        if (item.Capacity + 1e-6 >= requiredAs) maximum = Math.Max(maximum, item.Step);
                return maximum == int.MinValue ? 0.05 : Math.Max(0.05, maximum / 1000.0);
            }

            public List<AdditionalZone> Nearby(RebarLayer layer, double requiredAs,
                (double MinX, double MaxX, double MinY, double MaxY) footprint, double padding)
            {
                var result = new List<AdditionalZone>();
                // Keep input order: combined polygon operations and gap bridges must see
                // exactly the same candidates in exactly the same order as the full scan.
                foreach (var i in _index.Query(footprint.MinX - padding, footprint.MaxX + padding,
                             footprint.MinY - padding, footprint.MaxY + padding).OrderBy(i => i))
                {
                    var zone = this[i];
                    if (zone.Layer != layer || !(zone.AsCoveredCm2PerM + 1e-6 >= requiredAs)) continue;
                    var b = _bounds[i];
                    if (b.MaxX >= footprint.MinX - padding && b.MinX <= footprint.MaxX + padding &&
                        b.MaxY >= footprint.MinY - padding && b.MinY <= footprint.MaxY + padding)
                        result.Add(zone);
                }
                return result;
            }
        }
    }
}
