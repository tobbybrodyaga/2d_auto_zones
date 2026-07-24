using System;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Цвета изополей As доп. как в Ogibayushchaya: значение → интервал шкалы → фиксированный цвет.
    /// VisualizationScale = ширина интервала (см²/м).
    /// </summary>
    public static class IsoColorScale
    {
        public readonly struct Rgb
        {
            public readonly byte R, G, B;
            public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        }

        // Типовые ACI AutoCAD → RGB (как ezdxf.aci2rgb)
        private static readonly Rgb[] IntervalColors =
        {
            new Rgb(255, 0, 0),
            new Rgb(255, 255, 0),
            new Rgb(0, 255, 0),
            new Rgb(0, 255, 255),
            new Rgb(0, 0, 255),
            new Rgb(255, 0, 255),
            new Rgb(255, 127, 0),
            new Rgb(255, 191, 127),
            new Rgb(127, 255, 127),
            new Rgb(0, 127, 255),
            new Rgb(127, 0, 255),
            new Rgb(255, 0, 127),
            new Rgb(191, 0, 0),
            new Rgb(191, 191, 0),
            new Rgb(0, 191, 0),
            new Rgb(0, 191, 191),
            new Rgb(0, 0, 191),
            new Rgb(191, 0, 191),
            new Rgb(255, 128, 64),
            new Rgb(64, 128, 255),
            new Rgb(128, 64, 0),
            new Rgb(0, 128, 128),
            new Rgb(128, 0, 64),
            new Rgb(64, 64, 128),
        };

        private static readonly Rgb PinkBelow = new Rgb(255, 200, 235);
        private static readonly Rgb PinkAbove = new Rgb(255, 64, 160);

        /// <summary>
        /// 0 = ниже/нет; 1..N = интервал шкалы; N+1 = выше последнего.
        /// Пороги: step, 2*step, … (VisualizationScale).
        /// </summary>
        public static int LevelForValue(double asAdditional, double intervalCm2PerM)
        {
            var step = intervalCm2PerM > 1e-9 ? intervalCm2PerM : 1.0;
            var n = IntervalColors.Length;
            if (asAdditional <= 0.01) return 0;
            for (var i = 0; i < n; i++)
            {
                var lo = step * i;
                var hi = step * (i + 1);
                if (asAdditional >= lo && asAdditional < hi)
                    return i + 1;
            }
            return n + 1;
        }

        public static Rgb ColorForValue(double asAdditional, double intervalCm2PerM)
        {
            var lvl = LevelForValue(asAdditional, intervalCm2PerM);
            if (lvl <= 0) return PinkBelow;
            if (lvl > IntervalColors.Length) return PinkAbove;
            return IntervalColors[lvl - 1];
        }
    }
}
