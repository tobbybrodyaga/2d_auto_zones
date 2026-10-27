using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>Правила связи отметки ЛИРЫ с планами Revit.</summary>
    public static class RevitLinkRules
    {
        /// <summary>«План несущих конструкций. Низ по X на отметке +29.450».</summary>
        public static string PlanName(RebarLayer layer, bool reverseDirections, double elevationM) =>
            PlanNamePrefix(layer, reverseDirections) + FormatElevation(elevationM);

        /// <summary>Имя плана без отметки — по нему находится план, созданный с другой отметкой.</summary>
        public static string PlanNamePrefix(RebarLayer layer, bool reverseDirections)
        {
            var side = layer is RebarLayer.As1 or RebarLayer.As2 ? "Низ" : "Верх";
            var axis = RebarTables.DirectionForLayer(layer, reverseDirections) == ZoneDirection.X ? "X" : "Y";
            return $"План несущих конструкций. {side} по {axis} на отметке ";
        }

        /// <summary>Отметка для имён планов: верх самого большого перекрытия уровня, иначе отметка уровня.</summary>
        public static double PlanElevation(IEnumerable<RevitFloorOutline> floors, double levelElevationM)
        {
            var main = floors.OrderByDescending(floor => floor.AreaM2).FirstOrDefault();
            return main != null ? Math.Round(main.TopM, 4) : levelElevationM;
        }

        public static string FormatElevation(double elevationM)
        {
            var rounded = Math.Round(elevationM, 3);
            var text = Math.Abs(rounded).ToString("0.000", CultureInfo.InvariantCulture);
            if (rounded == 0) return "±" + text;
            return (rounded > 0 ? "+" : "-") + text;
        }

        /// <summary>Планы создаются для включённых рядов; если не включён ни один — для всех четырёх.</summary>
        public static List<RebarLayer> LayersForPlans(AnalysisSettings settings)
        {
            var layers = new List<RebarLayer>();
            if (settings.ShowAs1) layers.Add(RebarLayer.As1);
            if (settings.ShowAs2) layers.Add(RebarLayer.As2);
            if (settings.ShowAs3) layers.Add(RebarLayer.As3);
            if (settings.ShowAs4) layers.Add(RebarLayer.As4);
            return layers.Count > 0
                ? layers
                : new List<RebarLayer> { RebarLayer.As1, RebarLayer.As2, RebarLayer.As3, RebarLayer.As4 };
        }
    }

    /// <summary>
    /// Единое преобразование плана ЛИРЫ в координаты Revit: поворот на RotationDeg вокруг
    /// центра геометрии плиты, затем сдвиг OffsetX/Y. Одно и то же для предпросмотра и вставки.
    /// </summary>
    public readonly struct LiraRevitTransform
    {
        private readonly double _ox, _oy, _cos, _sin, _px, _py;

        public double PivotX => _px;
        public double PivotY => _py;

        public LiraRevitTransform(AnalysisSettings? settings, double pivotX, double pivotY)
        {
            _ox = settings?.OffsetXM ?? 0;
            _oy = settings?.OffsetYM ?? 0;
            var rad = (settings?.RotationDeg ?? 0) * Math.PI / 180.0;
            _cos = Math.Cos(rad);
            _sin = Math.Sin(rad);
            _px = pivotX;
            _py = pivotY;
        }

        public static LiraRevitTransform For(AnalysisResult result, AnalysisSettings? settings = null)
        {
            var (px, py) = ComputePivot(result);
            return new LiraRevitTransform(settings ?? result.Settings, px, py);
        }

        /// <summary>Центр исходной геометрии уровня: среднее точек контура плиты и контуров КЭ.</summary>
        public static (double X, double Y) ComputePivot(AnalysisResult? result)
        {
            if (result == null) return (0, 0);
            double sx = 0, sy = 0;
            var n = 0;
            if (result.Outline != null)
                foreach (var p in result.Outline) { sx += p.X; sy += p.Y; n++; }
            if (result.Plates != null)
                foreach (var plate in result.Plates)
                foreach (var p in plate.Contour) { sx += p.X; sy += p.Y; n++; }
            return n > 0 ? (sx / n, sy / n) : (0, 0);
        }

        public (double X, double Y) ToRevit(double x, double y)
        {
            double dx = x - _px, dy = y - _py;
            return (_cos * dx - _sin * dy + _px + _ox, _sin * dx + _cos * dy + _py + _oy);
        }

        public (double X, double Y) ToLira(double x, double y)
        {
            double dx = x - _ox - _px, dy = y - _oy - _py;
            return (_cos * dx + _sin * dy + _px, -_sin * dx + _cos * dy + _py);
        }
    }
}
