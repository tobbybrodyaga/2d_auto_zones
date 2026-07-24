using System;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Единицы: координаты Core — метры; стержни/шаг/длины — мм.
    /// Футы Revit — ТОЛЬКО на границе ZonePlacer.
    /// </summary>
    public static class UnitConversion
    {
        public const double MetersToFeet = 3.280839895;
        public const double MmPerMeter = 1000.0;

        public static double MetersToMm(double meters) => meters * MmPerMeter;
        public static double MmToMeters(double mm) => mm / MmPerMeter;

        public static double MetersToFeetValue(double meters) => meters * MetersToFeet;
        public static double MmToFeet(double mm) => (mm / MmPerMeter) * MetersToFeet;

        public static double FeetToMeters(double feet) => feet / MetersToFeet;
        public static double FeetToMm(double feet) => FeetToMeters(feet) * MmPerMeter;

        /// <summary>Round-trip check: mm → feet → mm drift must be tiny.</summary>
        public static bool RoundTripMmOk(double mm, double tolMm = 1e-6)
        {
            var back = FeetToMm(MmToFeet(mm));
            return Math.Abs(back - mm) <= tolMm;
        }
    }
}
