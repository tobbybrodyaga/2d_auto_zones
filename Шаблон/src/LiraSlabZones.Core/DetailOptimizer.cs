using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Оптимизация детализации как в референсе: Max / 4 / 3 / 2 / Min.
    /// Max — точная раскладка (больше расход); Min — укрупнение (меньше расход).
    /// </summary>
    public static class DetailOptimizer
    {
        public const int StepCount = 5;

        /// <summary>0=Max … 4=Min (как на слайдере сверху вниз в референсе).</summary>
        public static int StepIndexFromSlider(double slider01)
        {
            var t = Math.Max(0, Math.Min(1, slider01));
            // 0 → Max(0), 1 → Min(4)
            return Math.Min(StepCount - 1, (int)Math.Round(t * (StepCount - 1)));
        }

        public static double SliderFromStepIndex(int stepIndex)
        {
            stepIndex = Math.Max(0, Math.Min(StepCount - 1, stepIndex));
            return stepIndex / (double)(StepCount - 1);
        }

        public static string StepLabel(int stepIndex) => stepIndex switch
        {
            0 => "Max",
            1 => "4",
            2 => "3",
            3 => "2",
            _ => "Min"
        };

        public static DetailLevel FromSlider(double slider01)
        {
            var i = StepIndexFromSlider(slider01);
            if (i <= 1) return DetailLevel.Exact;
            if (i <= 3) return DetailLevel.Medium;
            return DetailLevel.Coarse;
        }

        public static string Label(DetailLevel level) => level switch
        {
            DetailLevel.Exact => "Max / точный",
            DetailLevel.Medium => "средний",
            _ => "Min / укрупнённый"
        };

        public static string LabelWithMass(double slider01, double kgPerM3)
        {
            var i = StepIndexFromSlider(slider01);
            if (kgPerM3 > 0.01)
                return $"{StepLabel(i)} ({kgPerM3:0.0} кг/м³)";
            return StepLabel(i);
        }

        /// <summary>Ниже порог → зона растёт шире (укрупнение → Min).</summary>
        public static double ThresholdRatio(DetailLevel level) => level switch
        {
            DetailLevel.Exact => 0.95,
            DetailLevel.Medium => 0.70,
            _ => 0.40
        };

        public static double ThresholdRatioForSlider(double slider01)
        {
            var i = StepIndexFromSlider(slider01);
            return i switch
            {
                0 => 0.95,
                1 => 0.85,
                2 => 0.70,
                3 => 0.55,
                _ => 0.40
            };
        }

        /// <summary>Оценка расхода кг/м³ по зонам и толщине плиты.</summary>
        public static double EstimateKgPerM3(
            IEnumerable<AdditionalZone> zones,
            double slabAreaM2,
            double thicknessMm)
        {
            if (slabAreaM2 < 1e-6 || thicknessMm < 1) return 0;
            double mass = 0;
            foreach (var z in zones)
            {
                if (z.DiameterMm <= 0 || z.LengthMm <= 0 || z.BarCount <= 0) continue;
                mass += BarCapacity.SteelKgPerM(z.DiameterMm) *
                        (z.LengthMm / 1000.0) *
                        z.BarCount;
            }
            var vol = slabAreaM2 * (thicknessMm / 1000.0);
            return vol > 1e-9 ? mass / vol : 0;
        }

        public static double SlabAreaM2(IList<Point3>? outline, IList<LiraPlateElement>? plates)
        {
            if (outline != null && outline.Count >= 3)
            {
                // shoelace
                double a = 0;
                for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
                    a += (outline[j].X + outline[i].X) * (outline[j].Y - outline[i].Y);
                return Math.Abs(a) * 0.5;
            }
            if (plates == null || plates.Count == 0) return 0;
            return plates.Sum(p => Math.Max(p.WidthM * p.LengthM, 0.01));
        }
    }
}
