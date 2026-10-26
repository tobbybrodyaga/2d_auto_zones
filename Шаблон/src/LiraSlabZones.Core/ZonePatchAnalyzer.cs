using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public static class ZonePatchAnalyzer
    {
        private readonly struct CellCandidate
        {
            public readonly int X;
            public readonly int Y;
            public readonly double Value;

            public CellCandidate(int x, int y, double value)
            {
                X = x;
                Y = y;
                Value = value;
            }
        }

        public static List<ZonePatch> Build(
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            double levelZM)
        {
            settings.SyncBackgroundAsFromBars();
            var result = new List<ZonePatch>();
            var patchId = 0;
            var layers = new[]
            {
                (Layer: RebarLayer.As1, Show: settings.ShowAs1, Background: settings.AsMainAs1),
                (Layer: RebarLayer.As2, Show: settings.ShowAs2, Background: settings.AsMainAs2),
                (Layer: RebarLayer.As3, Show: settings.ShowAs3, Background: settings.AsMainAs3),
                (Layer: RebarLayer.As4, Show: settings.ShowAs4, Background: settings.AsMainAs4)
            };

            foreach (var layer in layers)
            {
                if (!layer.Show) continue;
                var mosaic = MosaicBuilder.Build(plates, layer.Layer, layer.Background,
                    Math.Max(1, settings.GridCellMm), levelZM);
                if (mosaic.Nx == 0 || mosaic.Ny == 0) continue;

                var detailStep = DetailOptimizer.StepIndexFromSlider(settings.DetailSlider);
                var values = detailStep == DetailOptimizer.StepCount - 1
                    ? mosaic.Values
                    : MosaicBuilder.SmoothSingleSpike(mosaic.Values);
                var peaks = new List<CellCandidate>();
                for (var iy = 0; iy < mosaic.Ny; iy++)
                for (var ix = 0; ix < mosaic.Nx; ix++)
                {
                    var value = values[iy][ix];
                    if (value > MosaicBuilder.PositiveResidualToleranceCm2PerM)
                        peaks.Add(new CellCandidate(ix, iy, value));
                }
                peaks = peaks.OrderByDescending(cell => cell.Value).ToList();

                var assigned = new bool[mosaic.Ny][];
                for (var iy = 0; iy < mosaic.Ny; iy++)
                    assigned[iy] = new bool[mosaic.Nx];

                var direction = RebarTables.DirectionForLayer(layer.Layer, settings.ReverseZoneDirections);
                var thresholdRatio = DetailOptimizer.ThresholdRatioForSlider(settings.DetailSlider);
                var thresholdIsNearZero = detailStep == DetailOptimizer.StepCount - 1;

                foreach (var peak in peaks)
                {
                    if (assigned[peak.Y][peak.X]) continue;
                    var threshold = thresholdIsNearZero
                        ? MosaicBuilder.PositiveResidualToleranceCm2PerM
                        : peak.Value * thresholdRatio;
                    var cells = GrowPatch(mosaic, values, assigned, peak, threshold, direction);
                    if (cells.Count == 0) continue;

                    var elementIds = cells
                        .SelectMany(cell => mosaic.PlateIds[cell.Y][cell.X])
                        .Distinct()
                        .OrderBy(id => id)
                        .ToList();
                    if (settings.MinActiveElements > 0 && elementIds.Count < settings.MinActiveElements)
                        continue;

                    var minX = cells.Min(cell => cell.X);
                    var maxX = cells.Max(cell => cell.X);
                    var minY = cells.Min(cell => cell.Y);
                    var maxY = cells.Max(cell => cell.Y);
                    var cellM = mosaic.CellMm / 1000.0;
                    var elementBounds = elementIds
                        .Where(id => mosaic.PlateBounds.ContainsKey(id))
                        .Select(id => mosaic.PlateBounds[id])
                        .ToList();
                    var minXM = elementBounds.Count > 0
                        ? elementBounds.Min(bounds => bounds.MinX)
                        : mosaic.OriginXM + minX * cellM;
                    var maxXM = elementBounds.Count > 0
                        ? elementBounds.Max(bounds => bounds.MaxX)
                        : mosaic.OriginXM + (maxX + 1) * cellM;
                    var minYM = elementBounds.Count > 0
                        ? elementBounds.Min(bounds => bounds.MinY)
                        : mosaic.OriginYM + minY * cellM;
                    var maxYM = elementBounds.Count > 0
                        ? elementBounds.Max(bounds => bounds.MaxY)
                        : mosaic.OriginYM + (maxY + 1) * cellM;
                    var patch = new ZonePatch
                    {
                        PatchId = ++patchId,
                        Layer = layer.Layer,
                        Direction = direction,
                        PeakAsAdditionalCm2PerM = cells.Max(cell => values[cell.Y][cell.X]),
                        MinCellX = minX,
                        MaxCellX = maxX,
                        MinCellY = minY,
                        MaxCellY = maxY,
                        MinXM = minXM,
                        MaxXM = maxXM,
                        MinYM = minYM,
                        MaxYM = maxYM,
                        ElementIds = elementIds
                    };

                    foreach (var cell in cells)
                    {
                        patch.Cells.Add(new ZonePatchCell
                        {
                            Ix = cell.X,
                            Iy = cell.Y,
                            AsAdditionalCm2PerM = values[cell.Y][cell.X],
                            RawAsAdditionalCm2PerM = mosaic.Values[cell.Y][cell.X],
                            MinXM = mosaic.OriginXM + cell.X * cellM,
                            MaxXM = mosaic.OriginXM + (cell.X + 1) * cellM,
                            MinYM = mosaic.OriginYM + cell.Y * cellM,
                            MaxYM = mosaic.OriginYM + (cell.Y + 1) * cellM
                        });
                    }

                    result.Add(patch);
                }
            }

            return result;
        }

        private static List<(int X, int Y)> GrowPatch(
            MosaicGrid mosaic,
            double[][] values,
            bool[][] assigned,
            CellCandidate peak,
            double threshold,
            ZoneDirection direction)
        {
            var cells = new List<(int X, int Y)>();
            var queue = new Queue<(int X, int Y)>();
            assigned[peak.Y][peak.X] = true;
            queue.Enqueue((peak.X, peak.Y));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                cells.Add(current);
                foreach (var next in OrderedNeighbors(current.X, current.Y, direction))
                {
                    if (next.X < 0 || next.X >= mosaic.Nx || next.Y < 0 || next.Y >= mosaic.Ny ||
                        assigned[next.Y][next.X] ||
                        values[next.Y][next.X] <= MosaicBuilder.PositiveResidualToleranceCm2PerM)
                        continue;

                    var alongPlacementAxis = direction == ZoneDirection.X
                        ? next.Y == current.Y
                        : next.X == current.X;
                    if (alongPlacementAxis && values[next.Y][next.X] + 1e-12 < threshold)
                        continue;

                    assigned[next.Y][next.X] = true;
                    queue.Enqueue(next);
                }
            }

            return cells;
        }

        private static IEnumerable<(int X, int Y)> OrderedNeighbors(int x, int y, ZoneDirection direction)
        {
            if (direction == ZoneDirection.X)
            {
                yield return (x - 1, y);
                yield return (x + 1, y);
                yield return (x, y + 1);
                yield return (x, y - 1);
            }
            else
            {
                yield return (x, y + 1);
                yield return (x, y - 1);
                yield return (x - 1, y);
                yield return (x + 1, y);
            }
        }
    }
}
