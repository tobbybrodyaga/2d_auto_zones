using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Привязка прямоугольника зоны к ближайшим осям с кратностью смещения 10 мм.
    /// Сдвигает зону целиком (ширина/длина не меняются).
    /// </summary>
    public static class AxisSnapper
    {
        public const int SnapStepMm = 10;

        public sealed class TieInfo
        {
            public string AxisNameX { get; set; } = "";
            public double AxisPosXM { get; set; }
            public double OffsetFromAxisXMm { get; set; }
            public string AxisNameY { get; set; } = "";
            public double AxisPosYM { get; set; }
            public double OffsetFromAxisYMm { get; set; }

            public string Label
            {
                get
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(AxisNameX))
                        parts.Add($"от {AxisNameX}:{FormatOffset(OffsetFromAxisXMm)}");
                    if (!string.IsNullOrEmpty(AxisNameY))
                        parts.Add($"от {AxisNameY}:{FormatOffset(OffsetFromAxisYMm)}");
                    return string.Join(" · ", parts);
                }
            }

            private static string FormatOffset(double mm)
            {
                var sign = mm >= 0 ? "+" : "";
                return sign + mm.ToString("0", CultureInfo.InvariantCulture);
            }
        }

        public static double RoundToStepMm(double meters, int stepMm = SnapStepMm)
        {
            if (stepMm <= 0) stepMm = SnapStepMm;
            var mm = meters * 1000.0;
            var rounded = Math.Round(mm / stepMm, MidpointRounding.AwayFromZero) * stepMm;
            return rounded / 1000.0;
        }

        /// <summary>
        /// Привязка к ближайшим осям с кратностью 10 мм.
        /// Сдвиг только если ось рядом (≤ maxSnapM) — иначе зоны «улетают» к чужим осям.
        /// </summary>
        public static TieInfo SnapRect(
            ref double minXM,
            ref double maxXM,
            ref double minYM,
            ref double maxYM,
            IList<ConstructionAxis>? axes,
            double maxSnapM = 12.0)
        {
            var info = new TieInfo();
            if (axes == null || axes.Count == 0) return info;

            var verts = new List<(string Name, double Pos)>();
            var hors = new List<(string Name, double Pos)>();
            foreach (var ax in axes)
            {
                Resolve(ax, out var vertical, out var pos, out var name);
                if (string.IsNullOrWhiteSpace(name)) name = "?";
                if (vertical) verts.Add((name, pos));
                else hors.Add((name, pos));
            }

            if (verts.Count > 0)
            {
                var left = minXM;
                var nearest = verts.OrderBy(v => Math.Abs(v.Pos - left)).First();
                if (Math.Abs(nearest.Pos - left) <= maxSnapM)
                {
                    var offsetM = RoundToStepMm(left - nearest.Pos);
                    var newLeft = nearest.Pos + offsetM;
                    var dx = newLeft - left;
                    // Не уезжать дальше половины зоны
                    if (Math.Abs(dx) <= Math.Max(0.15, (maxXM - minXM) * 0.5))
                    {
                        minXM += dx;
                        maxXM += dx;
                    }
                    info.AxisNameX = nearest.Name;
                    info.AxisPosXM = nearest.Pos;
                    info.OffsetFromAxisXMm = Math.Round((minXM - nearest.Pos) * 1000.0 / SnapStepMm) * SnapStepMm;
                }
            }

            if (hors.Count > 0)
            {
                var bottom = minYM;
                var nearest = hors.OrderBy(h => Math.Abs(h.Pos - bottom)).First();
                if (Math.Abs(nearest.Pos - bottom) <= maxSnapM)
                {
                    var offsetM = RoundToStepMm(bottom - nearest.Pos);
                    var newBottom = nearest.Pos + offsetM;
                    var dy = newBottom - bottom;
                    if (Math.Abs(dy) <= Math.Max(0.15, (maxYM - minYM) * 0.5))
                    {
                        minYM += dy;
                        maxYM += dy;
                    }
                    info.AxisNameY = nearest.Name;
                    info.AxisPosYM = nearest.Pos;
                    info.OffsetFromAxisYMm = Math.Round((minYM - nearest.Pos) * 1000.0 / SnapStepMm) * SnapStepMm;
                }
            }

            return info;
        }

        private static void Resolve(ConstructionAxis ax, out bool vertical, out double pos, out string name)
        {
            name = ax.Name ?? "";
            vertical = ax.Vertical;
            pos = ax.Position;
            if (!ax.IsSegment) return;

            var dx = Math.Abs(ax.X2 - ax.X1);
            var dy = Math.Abs(ax.Y2 - ax.Y1);
            vertical = dx <= dy;
            pos = vertical ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
        }
    }
}
