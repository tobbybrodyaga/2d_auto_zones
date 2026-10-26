using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public readonly struct ZonePatchFrameBounds
    {
        public readonly double MinX;
        public readonly double MaxX;
        public readonly double MinY;
        public readonly double MaxY;

        public ZonePatchFrameBounds(double minX, double maxX, double minY, double maxY)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }
    }

    public sealed class ZonePatchFrameElement
    {
        public int ElementId { get; set; }
        public RebarLayer Layer { get; set; }
        public double AsAdditionalCm2PerM { get; set; }
        public IList<Point3> Contour { get; set; } = new List<Point3>();
    }

    public static class ZonePatchFramePartitioner
    {
        public static List<ZonePatchFrameBounds> Split(
            IList<ZonePatch> patches,
            double minX,
            double maxX,
            double minY,
            double maxY,
            double minFrameWidthM,
            IList<ZonePatchFrameElement> frameElements)
        {
            var single = new List<ZonePatchFrameBounds>
            {
                new ZonePatchFrameBounds(minX, maxX, minY, maxY)
            };
            if (patches == null || patches.Count == 0) return single;
            if (patches.Count < 2) return TrimPartitions(single, patches, frameElements);

            minFrameWidthM = Math.Max(0, minFrameWidthM);
            var patchList = patches.ToList();
            var xCuts = CutsFor(patchList.Where(patch => patch.Direction == ZoneDirection.Y),
                true, minX, maxX, minY, maxY, minFrameWidthM, frameElements);
            var yCuts = CutsFor(patchList.Where(patch => patch.Direction == ZoneDirection.X),
                false, minY, maxY, minX, maxX, minFrameWidthM, frameElements);
            if (xCuts.Count == 0 && yCuts.Count == 0)
                return TrimPartitions(single, patches, frameElements);

            var xBounds = new[] { minX }.Concat(xCuts).Concat(new[] { maxX }).ToList();
            var yBounds = new[] { minY }.Concat(yCuts).Concat(new[] { maxY }).ToList();
            var result = new List<ZonePatchFrameBounds>((xBounds.Count - 1) * (yBounds.Count - 1));
            for (var y = 0; y < yBounds.Count - 1; y++)
            for (var x = 0; x < xBounds.Count - 1; x++)
                result.Add(new ZonePatchFrameBounds(
                    xBounds[x], xBounds[x + 1], yBounds[y], yBounds[y + 1]));
            return TrimPartitions(result, patches, frameElements);
        }

        public static bool TryTrimToActiveCells(
            ZonePatchFrameBounds frame, IList<ZonePatch> patches,
            out ZonePatchFrameBounds trimmed)
        {
            return TryTrimToActiveCells(frame, patches, null, out trimmed);
        }

        public static bool TryTrimToActiveCells(
            ZonePatchFrameBounds frame, IList<ZonePatch> patches,
            IList<ZonePatchFrameElement>? frameElements, out ZonePatchFrameBounds trimmed)
        {
            trimmed = frame;
            if (patches == null) return true;

            var patchElementIds = new HashSet<(RebarLayer Layer, int ElementId)>(
                patches.SelectMany(patch => patch.ElementIds.Select(id => (patch.Layer, id))));
            var activeElements = frameElements?
                .Where(element => patchElementIds.Contains((element.Layer, element.ElementId)) &&
                                  element.Contour != null && element.Contour.Count >= 3)
                .ToList();
            if (activeElements != null && activeElements.Count > 0)
            {
                var contours = activeElements
                    .Select(element => ClipContour(element.Contour, frame))
                    .Where(polygon => polygon.Count >= 3 && PolygonArea(polygon) > 1e-12)
                    .ToList();
                if (contours.Count == 0) return false;

                trimmed = new ZonePatchFrameBounds(
                    contours.SelectMany(polygon => polygon).Min(point => point.X),
                    contours.SelectMany(polygon => polygon).Max(point => point.X),
                    contours.SelectMany(polygon => polygon).Min(point => point.Y),
                    contours.SelectMany(polygon => polygon).Max(point => point.Y));
                return trimmed.MaxX - trimmed.MinX > 1e-9 && trimmed.MaxY - trimmed.MinY > 1e-9;
            }

            if (!patches.Any(patch => patch.Cells != null && patch.Cells.Count > 0)) return true;

            var clippedCells = patches.SelectMany(patch => patch.Cells ?? new List<ZonePatchCell>())
                .Select(cell => new
                {
                    MinX = Math.Max(frame.MinX, cell.MinXM),
                    MaxX = Math.Min(frame.MaxX, cell.MaxXM),
                    MinY = Math.Max(frame.MinY, cell.MinYM),
                    MaxY = Math.Min(frame.MaxY, cell.MaxYM)
                })
                .Where(cell => cell.MaxX - cell.MinX > 1e-9 && cell.MaxY - cell.MinY > 1e-9)
                .ToList();
            if (clippedCells.Count == 0) return false;

            trimmed = new ZonePatchFrameBounds(
                clippedCells.Min(cell => cell.MinX), clippedCells.Max(cell => cell.MaxX),
                clippedCells.Min(cell => cell.MinY), clippedCells.Max(cell => cell.MaxY));
            return trimmed.MaxX - trimmed.MinX > 1e-9 && trimmed.MaxY - trimmed.MinY > 1e-9;
        }

        private static List<ZonePatchFrameBounds> TrimPartitions(
            IEnumerable<ZonePatchFrameBounds> partitions, IList<ZonePatch> patches,
            IList<ZonePatchFrameElement> frameElements)
        {
            var trimmed = new List<ZonePatchFrameBounds>();
            foreach (var partition in partitions)
                if (TryTrimToActiveCells(partition, patches, frameElements, out var activeBounds))
                    trimmed.Add(activeBounds);
            return trimmed;
        }

        private static List<(double X, double Y)> ClipContour(
            IList<Point3> contour, ZonePatchFrameBounds frame)
        {
            var polygon = contour.Select(point => (point.X, point.Y)).ToList();
            polygon = Clip(polygon, point => point.X >= frame.MinX,
                (a, b) => IntersectX(a, b, frame.MinX));
            polygon = Clip(polygon, point => point.X <= frame.MaxX,
                (a, b) => IntersectX(a, b, frame.MaxX));
            polygon = Clip(polygon, point => point.Y >= frame.MinY,
                (a, b) => IntersectY(a, b, frame.MinY));
            polygon = Clip(polygon, point => point.Y <= frame.MaxY,
                (a, b) => IntersectY(a, b, frame.MaxY));
            return polygon;
        }

        private static List<(double X, double Y)> Clip(
            List<(double X, double Y)> input, Func<(double X, double Y), bool> inside,
            Func<(double X, double Y), (double X, double Y), (double X, double Y)> intersection)
        {
            var output = new List<(double X, double Y)>();
            if (input.Count == 0) return output;

            var previous = input[input.Count - 1];
            var previousInside = inside(previous);
            foreach (var current in input)
            {
                var currentInside = inside(current);
                if (currentInside)
                {
                    if (!previousInside) output.Add(intersection(previous, current));
                    output.Add(current);
                }
                else if (previousInside)
                {
                    output.Add(intersection(previous, current));
                }

                previous = current;
                previousInside = currentInside;
            }
            return output;
        }

        private static (double X, double Y) IntersectX(
            (double X, double Y) a, (double X, double Y) b, double x)
        {
            var t = Math.Abs(b.X - a.X) < 1e-12 ? 0 : (x - a.X) / (b.X - a.X);
            return (x, a.Y + t * (b.Y - a.Y));
        }

        private static (double X, double Y) IntersectY(
            (double X, double Y) a, (double X, double Y) b, double y)
        {
            var t = Math.Abs(b.Y - a.Y) < 1e-12 ? 0 : (y - a.Y) / (b.Y - a.Y);
            return (a.X + t * (b.X - a.X), y);
        }

        private static double PolygonArea(IList<(double X, double Y)> polygon)
        {
            double area = 0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(area) * 0.5;
        }

        private static List<double> CutsFor(
            IEnumerable<ZonePatch> patches, bool alongX, double frameMin, double frameMax,
            double crossMin, double crossMax, double minSegmentWidthM,
            IList<ZonePatchFrameElement> frameElements)
        {
            (double Start, double End) Bounds(ZonePatch patch)
            {
                // FE contour bounds can overlap even when the occupied mosaic cells
                // are separated. Use the actual patch footprint to place the cut;
                // the enclosing FE geometry is still used for valid vertex snapping.
                if (patch.Cells != null && patch.Cells.Count > 0)
                {
                    var cellStart = patch.Cells.Min(cell => alongX ? cell.MinXM : cell.MinYM);
                    var cellEnd = patch.Cells.Max(cell => alongX ? cell.MaxXM : cell.MaxYM);
                    if (cellEnd > cellStart) return (cellStart, cellEnd);
                }

                var start = alongX ? patch.MinXM : patch.MinYM;
                var end = alongX ? patch.MaxXM : patch.MaxYM;
                return (start, end);
            }

            var intervals = patches.Select(Bounds)
                .Where(interval => interval.End > interval.Start)
                .OrderBy(interval => (interval.Start + interval.End) * 0.5)
                .ToList();
            var vertexCoordinates = frameElements
                .Where(element => element.Contour != null && element.Contour.Count >= 3)
                .SelectMany(element => element.Contour)
                .Where(point =>
                {
                    var cross = alongX ? point.Y : point.X;
                    return cross >= crossMin - 1e-9 && cross <= crossMax + 1e-9;
                })
                .Select(point => alongX ? point.X : point.Y)
                .Where(coordinate => coordinate > frameMin + 1e-9 && coordinate < frameMax - 1e-9)
                .OrderBy(coordinate => coordinate)
                .ToList();
            var distinctVertices = new List<double>(vertexCoordinates.Count);
            foreach (var coordinate in vertexCoordinates)
                if (distinctVertices.Count == 0 ||
                    coordinate > distinctVertices[distinctVertices.Count - 1] + 1e-9)
                    distinctVertices.Add(coordinate);

            var cuts = new List<double>();
            var segmentStart = frameMin;
            for (var i = 1; i < intervals.Count; i++)
            {
                var previous = intervals[i - 1];
                var current = intervals[i];
                var desiredCut = previous.End <= current.Start
                    ? (previous.End + current.Start) * 0.5
                    : ((previous.Start + previous.End) * 0.5 +
                       (current.Start + current.End) * 0.5) * 0.5;
                var minimumCut = Math.Max(frameMin + 1e-9, segmentStart + minSegmentWidthM - 1e-9);
                var maximumCut = Math.Min(frameMax - 1e-9, frameMax - minSegmentWidthM + 1e-9);
                var cut = distinctVertices
                    .Where(coordinate => coordinate >= minimumCut && coordinate <= maximumCut)
                    .OrderBy(coordinate => Math.Abs(coordinate - desiredCut))
                    .ThenBy(coordinate => coordinate)
                    .Where(coordinate => KeepsMaximumElementsOnOneSide(
                        coordinate, alongX, crossMin, crossMax, frameElements))
                    .DefaultIfEmpty(double.NaN)
                    .First();
                if (double.IsNaN(cut) ||
                    (cuts.Count > 0 && cut <= cuts[cuts.Count - 1] + 1e-9))
                    continue;
                if (cut - segmentStart < minSegmentWidthM - 1e-9 ||
                    frameMax - cut < minSegmentWidthM - 1e-9)
                    continue;
                cuts.Add(cut);
                segmentStart = cut;
            }
            return cuts;
        }

        private static bool KeepsMaximumElementsOnOneSide(
            double cut, bool alongX, double crossMin, double crossMax,
            IList<ZonePatchFrameElement> frameElements)
        {
            foreach (var layerElements in frameElements
                         .Where(element => element.Contour != null && element.Contour.Count >= 3)
                         .GroupBy(element => element.Layer))
            {
                var touching = layerElements.Select(element =>
                {
                    var axisCoordinates = element.Contour.Select(point => alongX ? point.X : point.Y);
                    var crossCoordinates = element.Contour.Select(point => alongX ? point.Y : point.X);
                    var minAxis = axisCoordinates.Min();
                    var maxAxis = axisCoordinates.Max();
                    var minCross = crossCoordinates.Min();
                    var maxCross = crossCoordinates.Max();
                    var touches = cut >= minAxis - 1e-9 && cut <= maxAxis + 1e-9 &&
                                  maxCross >= crossMin - 1e-9 && minCross <= crossMax + 1e-9;
                    return (Element: element, Touches: touches, MinAxis: minAxis, MaxAxis: maxAxis);
                })
                    .Where(item => item.Touches)
                    .ToList();
                if (touching.Count == 0) continue;

                var maxAs = touching.Max(item => item.Element.AsAdditionalCm2PerM);
                var tolerance = Math.Max(1e-9, Math.Abs(maxAs) * 1e-9);
                foreach (var item in touching.Where(item =>
                             maxAs - item.Element.AsAdditionalCm2PerM <= tolerance))
                {
                    var hasLowerSide = item.Element.Contour.Any(point =>
                        (alongX ? point.X : point.Y) < cut - 1e-9);
                    var hasUpperSide = item.Element.Contour.Any(point =>
                        (alongX ? point.X : point.Y) > cut + 1e-9);
                    if (hasLowerSide && hasUpperSide) return false;
                }
            }

            return true;
        }
    }
}
