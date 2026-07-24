using System;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// As (см²/м) ↔ Ø / шаг / число стержней.
    /// As_cm2_per_m = (A_bar_mm2 / 100) * (1000 / step_mm)
    /// </summary>
    public static class BarCapacity
    {
        public static double BarAreaMm2(int diameterMm)
        {
            var d = (double)diameterMm;
            return Math.PI * d * d / 4.0;
        }

        public static double AsCm2PerM(int diameterMm, int stepMm)
        {
            if (stepMm <= 0) throw new ArgumentOutOfRangeException(nameof(stepMm));
            return (BarAreaMm2(diameterMm) / 100.0) * (1000.0 / stepMm);
        }

        public static double SteelKgPerM(int diameterMm) => BarAreaMm2(diameterMm) * 7.85e-3;

        /// <summary>Минимальный Ø ≤ maxDiameter, дающий As ≥ required при заданном шаге.</summary>
        public static int MinDiameterForAs(double requiredAsCm2PerM, int stepMm, int maxDiameterMm)
        {
            if (requiredAsCm2PerM <= 0.01) return 0;
            var maxD = Math.Min(maxDiameterMm, RebarTables.AllowedDiametersMm.Max());
            foreach (var d in RebarTables.AllowedDiametersMm.Where(x => x <= maxD))
            {
                if (AsCm2PerM(d, stepMm) + 1e-9 >= requiredAsCm2PerM)
                    return d;
            }
            return maxD;
        }

        /// <summary>Число стержней: ширина = (N-1)*step ≥ spanMm, и As покрытия.</summary>
        public static (int BarCount, double WidthMm) BarsForSpanAndAs(
            double requiredAsCm2PerM,
            int diameterMm,
            int stepMm,
            double spanPerpMm)
        {
            if (diameterMm <= 0 || stepMm <= 0)
                return (1, 0);

            var asOne = AsCm2PerM(diameterMm, stepMm);
            // Для зоны: As зоны ≈ asOne (один ряд стержней с шагом step).
            // Число стержней определяется геометрией span.
            var minCount = Math.Max(1, (int)Math.Ceiling(spanPerpMm / stepMm) + 1);
            // если As пика сильно больше — можно сгустить (уменьшить эффективный шаг через больше стержней на том же span не помогает As;
            // As определяется Ø и step. При фиксированном step увеличиваем Ø выше.
            _ = requiredAsCm2PerM;
            _ = asOne;
            var width = (minCount - 1) * stepMm;
            return (minCount, width);
        }
    }
}
