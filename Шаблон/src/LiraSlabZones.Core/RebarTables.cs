using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>Таблицы анкеровки / 2α-нахлёста / оправки (мм). Ø30 не используется.</summary>
    public static class RebarTables
    {
        public static readonly int[] AllowedDiametersMm = { 8, 10, 12, 16, 20, 22, 25, 28, 32, 36 };

        public static readonly int[] Sum3FamilyLengthsMm =
        {
            1460, 1950, 2340, 2900, 3900, 4680, 5850, 7800, 8800, 11700
        };

        public const string StraightFamily = "SUM-30-Зона дополнительного армирования.rfa";
        public const string LFamily = "SUM-31-Зона дополнительного армирования Г.rfa";
        public const string PEqualFamily = "SUM-32-Зона дополнительного армирования П-образная равнополочная.rfa";
        public const string PDiffFamily = "SUM-33-Зона дополнительного армирования П-образная разнополочная.rfa";
        public const string BentStickFamily = "SUM-34-Зона дополнительного армирования Гнутый стержень.rfa";

        public static readonly string[] AllFamilyFiles =
        {
            StraightFamily, LFamily, PEqualFamily, PDiffFamily, BentStickFamily
        };

        // Анкеровка A500, мм (уже округлено вверх до 10 в ТЗ)
        private static readonly Dictionary<string, Dictionary<int, int>> Anchorage = new()
        {
            ["B15"] = new() { [8]=470,[10]=580,[12]=700,[16]=930,[20]=1160,[22]=1280,[25]=1450,[28]=1630,[32]=1860,[36]=2320 },
            ["B20"] = new() { [8]=390,[10]=490,[12]=580,[16]=780,[20]=970,[22]=1070,[25]=1210,[28]=1360,[32]=1550,[36]=1940 },
            ["B25"] = new() { [8]=340,[10]=420,[12]=500,[16]=670,[20]=830,[22]=920,[25]=1040,[28]=1160,[32]=1330,[36]=1660 },
            ["B30"] = new() { [8]=310,[10]=380,[12]=460,[16]=610,[20]=760,[22]=840,[25]=950,[28]=1060,[32]=1220,[36]=1520 },
            ["B35"] = new() { [8]=270,[10]=340,[12]=410,[16]=540,[20]=670,[22]=740,[25]=840,[28]=940,[32]=1080,[36]=1340 },
            ["B40"] = new() { [8]=250,[10]=320,[12]=380,[16]=500,[20]=630,[22]=690,[25]=780,[28]=870,[32]=1000,[36]=1250 },
        };

        // 2α нахлёст A500, мм (округлено вверх до 50 в ТЗ)
        private static readonly Dictionary<string, Dictionary<int, int>> Lap = new()
        {
            ["B15"] = new() { [8]=930,[10]=1160,[12]=1400,[16]=1860,[20]=2320,[22]=2560,[25]=2900,[28]=3250,[32]=3720,[36]=4640 },
            ["B20"] = new() { [8]=780,[10]=970,[12]=1160,[16]=1550,[20]=1940,[22]=2130,[25]=2420,[28]=2710,[32]=3100,[36]=3870 },
            ["B25"] = new() { [8]=670,[10]=830,[12]=1000,[16]=1330,[20]=1660,[22]=1830,[25]=2080,[28]=2320,[32]=2660,[36]=3320 },
            ["B30"] = new() { [8]=610,[10]=760,[12]=910,[16]=1220,[20]=1520,[22]=1670,[25]=1900,[28]=2120,[32]=2430,[36]=3030 },
            ["B35"] = new() { [8]=540,[10]=670,[12]=810,[16]=1080,[20]=1340,[22]=1480,[25]=1680,[28]=1880,[32]=2150,[36]=2680 },
            ["B40"] = new() { [8]=500,[10]=630,[12]=750,[16]=1000,[20]=1250,[22]=1370,[25]=1560,[28]=1740,[32]=1990,[36]=2490 },
        };

        private static readonly Dictionary<int, int> Mandrel = new()
        {
            [8]=40,[10]=50,[12]=60,[16]=80,[20]=160,[22]=180,[25]=200,[28]=225,[32]=260,[36]=290
        };

        public static int CeilToStep(double valueMm, int stepMm)
        {
            if (stepMm <= 0) throw new ArgumentOutOfRangeException(nameof(stepMm));
            return (int)(Math.Ceiling(valueMm / stepMm) * stepMm);
        }

        public static int NearestSupportedDiameter(int requested, IEnumerable<int> available)
        {
            var list = available.OrderBy(d => d).ToList();
            if (list.Count == 0) throw new InvalidOperationException("No diameters.");
            if (list.Contains(requested)) return requested;
            var le = list.Where(d => d <= requested).ToList();
            return le.Count > 0 ? le.Max() : list.Min();
        }

        public static string NormalizeConcrete(string concreteClass)
        {
            var c = (concreteClass ?? "B25").Trim().ToUpperInvariant();
            if (!Anchorage.ContainsKey(c)) c = "B25";
            return c;
        }

        public static int AnchorageLenMm(string concreteClass, int diameterMm)
        {
            var c = NormalizeConcrete(concreteClass);
            var d = NearestSupportedDiameter(diameterMm, Anchorage[c].Keys);
            return CeilToStep(Anchorage[c][d], 10);
        }

        public static int LapLenMm(string concreteClass, int diameterMm)
        {
            var c = NormalizeConcrete(concreteClass);
            var d = NearestSupportedDiameter(diameterMm, Lap[c].Keys);
            return CeilToStep(Lap[c][d], 50);
        }

        public static int MandrelDiamMm(int diameterMm)
        {
            var d = NearestSupportedDiameter(diameterMm, Mandrel.Keys);
            return Mandrel[d];
        }

        public static int PickFamilyLength(double requiredLenMm)
        {
            foreach (var L in Sum3FamilyLengthsMm)
                if (L >= requiredLenMm) return L;
            return Sum3FamilyLengthsMm[Sum3FamilyLengthsMm.Length - 1];
        }

        /// <summary>Максимальная длина SUM-3, не превышающая доступный габарит (подрезка краем плиты).</summary>
        public static int PickFamilyLengthFit(double availableLenMm)
        {
            if (availableLenMm < Sum3FamilyLengthsMm[0] - 1) return 0;
            var best = 0;
            foreach (var L in Sum3FamilyLengthsMm)
            {
                if (L <= availableLenMm + 1) best = L;
                else break;
            }
            return best;
        }

        public static string FamilyFileName(ZoneFamilyKind kind) => kind switch
        {
            ZoneFamilyKind.L => LFamily,
            ZoneFamilyKind.PEqual => PEqualFamily,
            ZoneFamilyKind.PDiff => PDiffFamily,
            ZoneFamilyKind.BentStick => BentStickFamily,
            _ => StraightFamily
        };

        public static ZoneDirection DirectionForLayer(RebarLayer layer) =>
            layer is RebarLayer.As1 or RebarLayer.As3 ? ZoneDirection.X : ZoneDirection.Y;

        public static int RowForLayer(RebarLayer layer) => (int)layer;
    }
}
