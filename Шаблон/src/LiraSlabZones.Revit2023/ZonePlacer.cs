using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LiraSlabZones.Core;

namespace LiraSlabZones.Revit2023
{
    internal sealed class ZonePlacementReport
    {
        public int Placed { get; set; }
        public Dictionary<RebarLayer, int> PlacedByLayer { get; } = new Dictionary<RebarLayer, int>();
        public List<string> Failures { get; } = new List<string>();
    }

    internal static class ZonePlacer
    {
        private const double MetersToFeet = UnitConversion.MetersToFeet;

        /// <summary>
        /// Семейства SUM-30…34 — «Элементы узлов» (привязаны к виду): каждая зона ставится
        /// на план своего ряда армирования.
        /// </summary>
        public static ZonePlacementReport Place(Document doc, FamilySymbol? defaultSymbol, AnalysisResult analysis,
            Func<RebarLayer, View?> viewForLayer)
        {
            var report = new ZonePlacementReport();
            if (analysis.Zones == null || analysis.Zones.Count == 0)
                return report;

            var overlong = analysis.Zones.Where(RebarTables.ExceedsMaxBarLength).ToList();
            if (overlong.Count > 0)
                throw new InvalidOperationException(
                    $"Размещение остановлено: {overlong.Count} зон превышают 11700 мм " +
                    $"(№ {string.Join(", ", overlong.Take(10).Select(z => z.ZoneId))}). " +
                    "Разделите эти зоны в предпросмотре перед размещением.");

            var settings = analysis.Settings ?? AppConfig.LoadEffectiveSettings();
            var transform = LiraRevitTransform.For(analysis, settings);
            var cache = new Dictionary<string, FamilySymbol?>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in analysis.Zones.GroupBy(z =>
                string.IsNullOrWhiteSpace(z.FamilyFileName)
                    ? settings.GetFamilyName(z.FamilyKind)
                    : AppConfig.StripRfa(z.FamilyFileName)))
            {
                var familyName = group.Key;
                if (!cache.TryGetValue(familyName, out var symbol))
                {
                    symbol = FamilyLoader.FindSymbol(doc, familyName);
                    if (symbol == null
                        && defaultSymbol != null
                        && familyName.Equals(settings.GetFamilyName(ZoneFamilyKind.Straight), StringComparison.OrdinalIgnoreCase))
                        symbol = defaultSymbol;
                    cache[familyName] = symbol;
                }

                if (symbol == null)
                    throw new InvalidOperationException(
                        "Семейство «" + familyName + "» не найдено в проекте Revit.\n" +
                        "Плагин не подгружает .rfa — загрузите семейство в проект вручную.\n" +
                        "Имя настраивается в " + AppConfig.UserConfigPath);

                if (!symbol.IsActive) symbol.Activate();

                foreach (var zone in group)
                {
                    var view = viewForLayer(zone.Layer);
                    var error = view == null
                        ? "нет плана для этого ряда"
                        : PlaceOne(doc, symbol, view, zone, settings, transform);
                    if (error != null)
                    {
                        report.Failures.Add($"зона {zone.ZoneId} ({zone.Layer}, {familyName}): {error}");
                        continue;
                    }
                    report.Placed++;
                    report.PlacedByLayer[zone.Layer] =
                        report.PlacedByLayer.TryGetValue(zone.Layer, out var count) ? count + 1 : 1;
                }
            }

            return report;
        }

        /// <returns>null при успехе, иначе причина пропуска.</returns>
        private static string? PlaceOne(
            Document doc,
            FamilySymbol symbol,
            View view,
            AdditionalZone zone,
            AnalysisSettings settings,
            LiraRevitTransform transform)
        {
            // То же преобразование, что и в предпросмотре (поворот вокруг центра плиты + сдвиг).
            var (px, py) = transform.ToRevit(zone.Placement.X, zone.Placement.Y);

            // Элемент узла лежит в плоскости вида; высотная отметка ЛИРЫ здесь не нужна.
            var z = (view as ViewPlan)?.GenLevel?.ProjectElevation ?? 0;
            var pt = new XYZ(px * MetersToFeet, py * MetersToFeet, z);
            FamilyInstance? inst;
            try
            {
                inst = doc.Create.NewFamilyInstance(pt, symbol, view);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            if (inst == null) return "Revit не создал экземпляр";

            var zoneRot = zone.Direction == ZoneDirection.Y ? 90.0 : 0.0;
            var totalRot = (settings.RotationDeg + zoneRot) * Math.PI / 180.0;
            if (Math.Abs(totalRot) > 1e-9)
            {
                try
                {
                    ElementTransformUtils.RotateElement(
                        doc,
                        inst.Id,
                        Line.CreateBound(pt, pt + XYZ.BasisZ),
                        totalRot);
                }
                catch
                {
                    // ignore rotate failures
                }
            }

            var lengthFeet = zone.LengthMm > 0
                ? UnitConversion.MmToFeet(zone.LengthMm)
                : zone.LengthM * MetersToFeet;
            var widthFeet = zone.WidthMm > 0
                ? UnitConversion.MmToFeet(zone.WidthMm)
                : zone.WidthM * MetersToFeet;

            TrySetDoubleParam(inst, new[] { "Длина", "Длинна", "Length", "L", "l" }, lengthFeet);
            TrySetDoubleParam(inst, new[] { "Ширина", "Width", "B", "b" }, widthFeet);
            TrySetDoubleParam(inst, new[] { "Шаг", "Step", "Spacing" }, UnitConversion.MmToFeet(zone.BarStepMm));
            TrySetDoubleParam(inst, new[] { "Ø", "Диаметр", "Diameter", "d" }, UnitConversion.MmToFeet(zone.DiameterMm));
            TrySetIntParam(inst, new[] { "RNF.Количество элементов", "Количество", "Count", "N" }, zone.BarCount);
            TrySetStringParam(inst, new[] { "Класс бетона", "Concrete", "Бетон" }, zone.ConcreteClass);
            TrySetDoubleParam(inst, new[] { "Коэф. α", "Alpha", "α" }, zone.AlphaCoef);
            TrySetStringParam(inst, new[] { "RNF.Раздел" }, zone.RnfSection);
            TrySetStringParam(inst, new[] { "RNF.Марка конструкции" }, zone.RnfMarkConstruction);
            TrySetStringParam(inst, new[] { "RNF.Марка сборки" }, zone.RnfMarkAssembly);
            TrySetStringParam(inst, new[] { "RNF.Марка элемента" }, zone.RnfMarkElement);
            TrySetStringParam(inst, new[] { "Комментарии", "Comments", "Mark", "Марка" },
                string.IsNullOrEmpty(zone.Comment)
                    ? $"As+={zone.AsAdditional:F2}; Ø{zone.DiameterMm}/{zone.BarStepMm}×{zone.BarCount}; {zone.Direction}"
                    : zone.Comment);
            // В семействах SUM-30…34 флаги называются «RNF.Учёт в спецификации» / «RNF.Подсчет стержней».
            TrySetBoolParam(inst, new[]
            {
                "RNF.Учёт в спецификации", "RNF.Учет в спецификации", "Учет в спецификации", "Учёт в спецификации"
            }, zone.CountInSpec);
            TrySetBoolParam(inst, new[]
            {
                "RNF.Подсчет стержней", "RNF.Подсчёт стержней", "подсчет стержней", "Подсчет стержней", "подсчёт стержней"
            }, zone.CountBars);

            return null;
        }

        private static void TrySetDoubleParam(Element e, string[] names, double valueFeet)
        {
            foreach (var name in names)
            {
                var p = e.LookupParameter(name) ?? e.GetParameters(name).FirstOrDefault();
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double)
                {
                    p.Set(valueFeet);
                    return;
                }
            }
        }

        private static void TrySetStringParam(Element e, string[] names, string value)
        {
            foreach (var name in names)
            {
                var p = e.LookupParameter(name) ?? e.GetParameters(name).FirstOrDefault();
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                {
                    p.Set(value);
                    return;
                }
            }
        }

        private static void TrySetIntParam(Element e, string[] names, int value)
        {
            foreach (var name in names)
            {
                var p = e.LookupParameter(name) ?? e.GetParameters(name).FirstOrDefault();
                if (p == null || p.IsReadOnly) continue;
                if (p.StorageType == StorageType.Integer) { p.Set(value); return; }
                if (p.StorageType == StorageType.Double) { p.Set((double)value); return; }
            }
        }

        private static void TrySetBoolParam(Element e, string[] names, bool value)
        {
            foreach (var name in names)
            {
                var p = e.LookupParameter(name) ?? e.GetParameters(name).FirstOrDefault();
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Integer)
                {
                    p.Set(value ? 1 : 0);
                    return;
                }
            }
        }
    }
}
