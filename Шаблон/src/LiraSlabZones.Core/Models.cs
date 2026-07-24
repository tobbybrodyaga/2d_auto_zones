using System;
using System.Collections.Generic;

namespace LiraSlabZones.Core
{
    public enum RebarLayer
    {
        As1 = 1,
        As2 = 2,
        As3 = 3,
        As4 = 4
    }

    /// <summary>Ось стержней зоны: As1/As3 → X, As2/As4 → Y.</summary>
    public enum ZoneDirection
    {
        X = 0,
        Y = 1
    }

    public enum DetailLevel
    {
        Exact = 0,
        Medium = 1,
        Coarse = 2
    }

    public enum ZoneFamilyKind
    {
        Straight = 0,   // SUM-30
        L = 1,          // SUM-31 Г
        PEqual = 2,     // SUM-32 П равнополочная
        PDiff = 3,      // SUM-33 П разнополочная
        BentStick = 4   // SUM-34
    }

    public sealed class Point3
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public Point3() { }

        public Point3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    public sealed class LiraNode
    {
        public int Id { get; set; }
        public Point3 Coord { get; set; } = new Point3();
    }

    public sealed class LiraPlateElement
    {
        public int Id { get; set; }
        public int TypeCode { get; set; }
        public List<int> NodeIds { get; set; } = new List<int>();
        public Point3 Centroid { get; set; } = new Point3();
        public List<Point3> Contour { get; set; } = new List<Point3>();
        public double WidthM { get; set; }
        public double LengthM { get; set; }
        public PlateReinforcement Rebar { get; set; } = new PlateReinforcement();
    }

    public sealed class PlateReinforcement
    {
        public double As1 { get; set; }
        public double As2 { get; set; }
        public double As3 { get; set; }
        public double As4 { get; set; }
        public double AsMax => Math.Max(Math.Max(As1, As2), Math.Max(As3, As4));
        public bool Ok { get; set; }

        public double Get(RebarLayer layer) => layer switch
        {
            RebarLayer.As1 => As1,
            RebarLayer.As2 => As2,
            RebarLayer.As3 => As3,
            RebarLayer.As4 => As4,
            _ => 0
        };
    }

    /// <summary>Прямоугольное отверстие на плане (м). Перпендикулярный габарит для X-зон = HeightM, для Y = WidthM.</summary>
    public sealed class OpeningInfo
    {
        public double MinXM { get; set; }
        public double MaxXM { get; set; }
        public double MinYM { get; set; }
        public double MaxYM { get; set; }
        public double WidthM => Math.Abs(MaxXM - MinXM);
        public double HeightM => Math.Abs(MaxYM - MinYM);
    }

    public sealed class AdditionalZone
    {
        public int ZoneId { get; set; }
        public int ElementId { get; set; }
        public RebarLayer Layer { get; set; } = RebarLayer.As1;
        public List<int> NodeIds { get; set; } = new List<int>();
        public Point3 Placement { get; set; } = new Point3();
        public List<Point3> Contour { get; set; } = new List<Point3>();

        /// <summary>Габарит зоны в метрах (для превью/совместимости).</summary>
        public double WidthM { get; set; }
        public double LengthM { get; set; }
        public double LevelZM { get; set; }
        public double AsRequired { get; set; }
        public double AsAdditional { get; set; }
        public PlateReinforcement Rebar { get; set; } = new PlateReinforcement();
        public string Comment { get; set; } = string.Empty;
        public bool IsValid { get; set; } = true;
        public string StatusColor { get; set; } = "ok"; // ok | warn | error

        // --- Автораскладка (мм / атрибуты семейства) ---
        public ZoneDirection Direction { get; set; } = ZoneDirection.X;
        public int DiameterMm { get; set; }
        public int BarStepMm { get; set; } = 200;
        public int BarCount { get; set; } = 1;
        public double WidthMm { get; set; }
        public double LengthMm { get; set; }
        public ZoneFamilyKind FamilyKind { get; set; } = ZoneFamilyKind.Straight;
        public string FamilyFileName { get; set; } = RebarTables.StraightFamily;
        public double AsCoveredCm2PerM { get; set; }
        public string ConcreteClass { get; set; } = "B25";
        public double AlphaCoef { get; set; } = 1.0;
        public double RotationDeg { get; set; }

        public string RnfSection { get; set; } = string.Empty;
        public string RnfMarkConstruction { get; set; } = string.Empty;
        public string RnfMarkAssembly { get; set; } = string.Empty;
        public string RnfMarkElement { get; set; } = string.Empty;
        public bool CountInSpec { get; set; } = true;
        public bool CountBars { get; set; }
        public double VerticalLegMm { get; set; }

        /// <summary>Привязка к ближайшей вертикальной оси (имя).</summary>
        public string AxisNameX { get; set; } = string.Empty;
        /// <summary>Положение вертикальной оси привязки, м.</summary>
        public double AxisPosXM { get; set; }
        /// <summary>Смещение левой грани от оси X, мм (кратно 10).</summary>
        public double OffsetFromAxisXMm { get; set; }
        /// <summary>Привязка к ближайшей горизонтальной оси (имя).</summary>
        public string AxisNameY { get; set; } = string.Empty;
        /// <summary>Положение горизонтальной оси привязки, м.</summary>
        public double AxisPosYM { get; set; }
        /// <summary>Смещение нижней грани от оси Y, мм (кратно 10).</summary>
        public double OffsetFromAxisYMm { get; set; }
        /// <summary>Короткая подпись привязки, напр. «от 2:+1250 · от Б:+400».</summary>
        public string AxisTieLabel { get; set; } = string.Empty;
    }

    public sealed class ConstructionAxis
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>true = вертикальная ось (постоянный X), false = горизонтальная (постоянный Y).</summary>
        public bool Vertical { get; set; }
        public double Position { get; set; }

        /// <summary>Ось задана отрезком (X1,Y1)–(X2,Y2) — типичный формат таблицы ЛИРА.</summary>
        public bool IsSegment { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }

    /// <summary>Уровень / отметка из ЛИРА или из Z-корзин КЭ.</summary>
    public sealed class ElevationLevelInfo
    {
        public string Label { get; set; } = string.Empty;
        public double ZM { get; set; }
        public int PlateCount { get; set; }
        public bool FromMarks { get; set; }

        public override string ToString() =>
            PlateCount > 0 ? $"{Label}  ({PlateCount} КЭ)" : Label;
    }

    public sealed class AnalysisResult
    {
        public string DocumentName { get; set; } = string.Empty;
        public string DocumentPath { get; set; } = string.Empty;
        public string UnitsNote { get; set; } =
            "Координаты: м. Ø/шаг/длины зон: мм. As/фон: см²/м. В Revit длины м/мм → футы только в ZonePlacer (× 3.280839895). Core не хранит футы.";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public AnalysisSettings Settings { get; set; } = new AnalysisSettings();
        public int NodeCount { get; set; }
        public int PlateCount { get; set; }
        public List<LiraPlateElement> Plates { get; set; } = new List<LiraPlateElement>();

        /// <summary>Все пластины фрагмента (все уровни) — для смены отметки без повторного чтения ЛИРА.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public List<LiraPlateElement> AllPlates { get; set; } = new List<LiraPlateElement>();

        public List<Point3> Outline { get; set; } = new List<Point3>();
        public List<AdditionalZone> Zones { get; set; } = new List<AdditionalZone>();
        public List<ConstructionAxis> Axes { get; set; } = new List<ConstructionAxis>();
        public List<ElevationLevelInfo> AvailableLevels { get; set; } = new List<ElevationLevelInfo>();
        public List<OpeningInfo> Openings { get; set; } = new List<OpeningInfo>();

        /// <summary>Средняя Z выбранного уровня, м.</summary>
        public double ElevationZM { get; set; }

        /// <summary>Подпись отметки (из ElevationMarks или Z=…).</summary>
        public string ElevationLabel { get; set; } = string.Empty;

        public PreviewStats Stats { get; set; } = new PreviewStats();
    }

    public sealed class PreviewStats
    {
        public int ZonesAs1 { get; set; }
        public int ZonesAs2 { get; set; }
        public int ZonesAs3 { get; set; }
        public int ZonesAs4 { get; set; }
        public double AreaAs1M2 { get; set; }
        public double AreaAs2M2 { get; set; }
        public double AreaAs3M2 { get; set; }
        public double AreaAs4M2 { get; set; }
        public double MaxAs { get; set; }
        public int WarnCount { get; set; }
        public int ErrorCount { get; set; }
        public double TotalSteelMassKg { get; set; }
        public double SteelKgPerM3 { get; set; }
        public string DetailLevelLabel { get; set; } = string.Empty;
    }

    /// <summary>
    /// Настройки: фон, габариты, слои As1–As4, автораскладка.
    /// </summary>
    public sealed class AnalysisSettings
    {
        public double AsMainCm2PerM { get; set; } = 0;
        public double AsMainAs1 { get; set; } = 0;
        public double AsMainAs2 { get; set; } = 0;
        public double AsMainAs3 { get; set; } = 0;
        public double AsMainAs4 { get; set; } = 0;

        /// <summary>Фон низа (As1+As2): диаметр, мм. 0 = не задан.</summary>
        public int BgBottomDiameterMm { get; set; }

        /// <summary>Фон низа: шаг, мм. 0 = не задан.</summary>
        public int BgBottomStepMm { get; set; }

        /// <summary>Фон верха (As3+As4): диаметр, мм. 0 = не задан.</summary>
        public int BgTopDiameterMm { get; set; }

        /// <summary>Фон верха: шаг, мм. 0 = не задан.</summary>
        public int BgTopStepMm { get; set; }

        /// <summary>Плита/уровень подтверждены («Взять плиту…» или демо).</summary>
        public bool SlabSelected { get; set; }

        /// <summary>ElementCenter = старый per-FE; AutoLayout = движок раскладки.</summary>
        public string PlacementMode { get; set; } = "AutoLayout";
        public int DesignOption { get; set; } = 1;

        public string FamilyName { get; set; } = "SUM-30-Зона дополнительного армирования";
        public string FamilyFileName { get; set; } = "SUM-30-Зона дополнительного армирования.rfa";

        /// <summary>Минимальная ширина зоны, м (0 = без фильтра). В UI вводится в мм.</summary>
        public double MinZoneWidthM { get; set; } = 0;

        /// <summary>Максимальная ширина зоны, м (0 = без ограничения). В UI вводится в мм.</summary>
        public double MaxZoneWidthM { get; set; } = 0;

        /// <summary>Минимальная длина зоны, м (0 = SUM-3 мин.). В UI вводится в мм.</summary>
        public double MinZoneLengthM { get; set; } = 0;

        /// <summary>Минимальное число КЭ в зоне (0 = без фильтра).</summary>
        public int MinActiveElements { get; set; } = 0;

        /// <summary>
        /// Шаг As для изополей, см²/м (0…25).
        /// </summary>
        public double VisualizationScale { get; set; } = 0;

        public bool ShowAs1 { get; set; } = true;
        public bool ShowAs2 { get; set; } = true;
        public bool ShowAs3 { get; set; } = false;
        public bool ShowAs4 { get; set; } = false;

        /// <summary>Смещение контура для сопоставления с Revit, м.</summary>
        public double OffsetXM { get; set; }
        public double OffsetYM { get; set; }
        public double RotationDeg { get; set; }

        /// <summary>
        /// Целевая отметка Z, м. NaN — авто (доминантный уровень).
        /// </summary>
        public double TargetElevationZM { get; set; } = double.NaN;

        public string ModelPart { get; set; } = "Visible";
        public bool LoadReinforcement { get; set; } = true;

        // --- Автораскладка ---
        public bool AutoLayout { get; set; } = true;
        public DetailLevel DetailLevel { get; set; } = DetailLevel.Exact;
        /// <summary>0=Exact … 1=Coarse, для UI-слайдера.</summary>
        public double DetailSlider { get; set; } = 0.0;
        public int BarStepMm { get; set; } = 200;
        /// <summary>Класс бетона (B15…B40). Пусто = не задан → раскладка допок запрещена.</summary>
        public string ConcreteClass { get; set; } = "";
        public double AlphaCoef { get; set; } = 1.0;
        public int GridCellMm { get; set; } = 300;
        public double SlabThicknessMm { get; set; } = 200;
        public double CoverTopMm { get; set; } = 25;
        public double CoverBottomMm { get; set; } = 25;
        public int MaxDiameterMm { get; set; } = 36;
        public bool ApplyHoleRules { get; set; } = true;
        public bool ApplyBentRules { get; set; } = true;
        public double HoleIgnorePerpMm { get; set; } = 200;
        public double EdgeOffsetMm { get; set; } = 50;
        /// <summary>Отступ зон от края плиты (контура), мм.</summary>
        public double SlabEdgeInsetMm { get; set; } = 30;
        /// <summary>true → шаг стержней 100 мм, иначе 200.</summary>
        public bool UseBarStep100 { get; set; }

        /// <summary>Пересчитать AsMainAs1…4 из Ø/шага фона (низ → As1/As2, верх → As3/As4).</summary>
        public void SyncBackgroundAsFromBars()
        {
            double bot = 0, top = 0;
            if (BgBottomDiameterMm > 0 && BgBottomStepMm > 0)
                bot = BarCapacity.AsCm2PerM(BgBottomDiameterMm, BgBottomStepMm);
            if (BgTopDiameterMm > 0 && BgTopStepMm > 0)
                top = BarCapacity.AsCm2PerM(BgTopDiameterMm, BgTopStepMm);
            AsMainAs1 = bot;
            AsMainAs2 = bot;
            AsMainAs3 = top;
            AsMainAs4 = top;
            AsMainCm2PerM = bot;
        }

        /// <summary>
        /// Раскладка допок разрешена только если: плита выбрана, фон низ+верх (Ø и шаг), класс бетона.
        /// </summary>
        public bool CanLayoutAdditionalZones(out string reason)
        {
            if (!SlabSelected)
            {
                reason = "Выберите плиту (уровень → «Взять плиту ближайшую к уровню» или демо).";
                return false;
            }
            if (BgBottomDiameterMm <= 0 || BgBottomStepMm <= 0)
            {
                reason = "Задайте фоновое армирование низа: диаметр и шаг.";
                return false;
            }
            if (BgTopDiameterMm <= 0 || BgTopStepMm <= 0)
            {
                reason = "Задайте фоновое армирование верха: диаметр и шаг.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(ConcreteClass))
            {
                reason = "Задайте класс бетона (влияет на анкеровку и длину зоны SUM-3).";
                return false;
            }
            reason = "";
            return true;
        }
    }
}
