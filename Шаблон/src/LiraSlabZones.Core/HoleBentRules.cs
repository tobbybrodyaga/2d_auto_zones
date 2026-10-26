using System;

namespace LiraSlabZones.Core
{
    /// <summary>Выбор гнутых семейств Legacy с учётом диаметра противоположного фонового ряда.</summary>
    public static class HoleBentRules
    {
        public static int OppositeEdgeRow(int currentRow)
        {
            // ТЗ: ряд 1↔3, ряд 2↔4
            switch (currentRow)
            {
                case 1: return 3;
                case 3: return 1;
                case 2: return 4;
                case 4: return 2;
                default: throw new ArgumentOutOfRangeException(nameof(currentRow));
            }
        }

        public static double VerticalLegAvailableMm(
            double thicknessMm,
            double coverTopMm,
            double coverBottomMm,
            int oppositeEdgeRowDiameterMm)
        {
            return thicknessMm - (coverTopMm + coverBottomMm + oppositeEdgeRowDiameterMm);
        }

        public static double VerticalLegAvailableMm(
            AnalysisSettings settings, RebarLayer currentLayer, int fallbackDiameterMm)
        {
            var oppositeRow = OppositeEdgeRow((int)currentLayer);
            var oppositeDiameter = oppositeRow <= 2
                ? settings.BgBottomDiameterMm
                : settings.BgTopDiameterMm;
            if (oppositeDiameter <= 0)
                oppositeDiameter = fallbackDiameterMm;
            return VerticalLegAvailableMm(settings.SlabThicknessMm, settings.CoverTopMm,
                settings.CoverBottomMm, oppositeDiameter);
        }

        public static ZoneFamilyKind ChooseBentFamily(
            double verticalAvailableMm,
            int diameterMm)
        {
            var mandrel = RebarTables.MandrelDiamMm(diameterMm);
            if (verticalAvailableMm >= 2.0 * mandrel)
                return ZoneFamilyKind.PEqual;
            if (verticalAvailableMm >= mandrel)
                return ZoneFamilyKind.L;
            return ZoneFamilyKind.BentStick;
        }

        /// <summary>
        /// Отверстие игнорируется, если габарит перпендикулярно направлению стержней ≤ ignoreMm.
        /// Для X: высота отверстия; для Y: ширина.
        /// </summary>
        public static bool ShouldIgnoreOpening(OpeningInfo op, ZoneDirection dir, double ignoreMm)
        {
            var perpMm = UnitConversion.MetersToMm(dir == ZoneDirection.X ? op.HeightM : op.WidthM);
            return perpMm <= ignoreMm + 1e-6;
        }

        public static bool RectIntersects(OpeningInfo op, double minX, double maxX, double minY, double maxY)
        {
            return !(maxX < op.MinXM || minX > op.MaxXM || maxY < op.MinYM || minY > op.MaxYM);
        }
    }
}
