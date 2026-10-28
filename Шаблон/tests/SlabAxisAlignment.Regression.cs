using System;
using System.Collections.Generic;
using System.Linq;
using LiraSlabZones.Core;
using Newtonsoft.Json;

public static class SlabAlignmentRegression
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private static Point3 Rotate(double x, double y, double angle, double z)
    {
        var rad = angle * Math.PI / 180;
        return new Point3(100 + x * Math.Cos(rad) - y * Math.Sin(rad),
            50 + x * Math.Sin(rad) + y * Math.Cos(rad), z);
    }

    private static AnalysisResult Slab(double angle)
    {
        var plates = new List<LiraPlateElement>();
        for (var x = 0; x < 12; x++)
        for (var y = 0; y < 8; y++)
        {
            // Отверстие 4×4 м, окружённое КЭ со всех сторон.
            if (x >= 4 && x < 8 && y >= 2 && y < 6) continue;
            var plate = new LiraPlateElement { Id = plates.Count + 1,
                Centroid = Rotate(x + 0.5, y + 0.5, angle, 3.3), WidthM = 1, LengthM = 1 };
            plate.Contour = new List<Point3> { Rotate(x, y, angle, 3.3), Rotate(x + 1, y, angle, 3.3),
                Rotate(x + 1, y + 1, angle, 3.3), Rotate(x, y + 1, angle, 3.3) };
            plate.Rebar = new PlateReinforcement { Ok = true, As1 = 12, As2 = 10, As3 = 8, As4 = 6 };
            plates.Add(plate);
        }
        var cfg = new AnalysisSettings { ShowAs1 = true, ShowAs2 = true, ShowAs3 = true, ShowAs4 = true,
            GridCellMm = 300, ConcreteClass = "B30", BgBottomDiameterMm = 8, BgBottomStepMm = 200,
            BgTopDiameterMm = 8, BgTopStepMm = 200, SlabSelected = true };
        var result = SlabZoneAnalyzer.BuildResult("test", "", 0, plates, cfg, null, 3.3, "test", true, null, false);
        result.AllPlates = plates;
        return result;
    }

    private static void Aligned(AnalysisResult result)
    {
        foreach (var plate in result.Plates)
        for (var i = 0; i < plate.Contour.Count; i++)
        {
            var a = plate.Contour[i];
            var b = plate.Contour[(i + 1) % plate.Contour.Count];
            var error = Math.Atan2(Math.Min(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)),
                Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y))) * 180 / Math.PI * 3600;
            Check(error < 1, "Slab edge skew exceeds 1 arcsecond: " + error);
            Check(a.Z == 3.3, "Elevation changed");
        }
        Check(result.Openings.Count == 1, "Hole was lost or duplicated");
        Check(Math.Abs(result.Openings[0].WidthM - 4) < 0.00001 &&
            Math.Abs(result.Openings[0].HeightM - 4) < 0.00001, "Hole dimensions changed");
        Check(result.Patches.Count > 0 && result.Patches.All(p => p.Cells.Count > 0), "Mosaic not rebuilt");
    }

    private static void Repeat(AnalysisResult result)
    {
        var json = JsonConvert.SerializeObject(result);
        var again = SlabAxisAlignment.Align(result, null);
        Check(!again.Changed && Object.ReferenceEquals(again.Result, result), "Repeated alignment rebuilt geometry");
        Check(json == JsonConvert.SerializeObject(again.Result), "Repeated alignment changed data");
    }

    public static void Run(string scratch)
    {
        foreach (var angle in new double[] { 7.3456789, 33, 89.99, -33, 0 })
        {
            var source = Slab(angle);
            var original = JsonConvert.SerializeObject(source);
            var report = SlabAxisAlignment.Align(source, null);
            var result = report.Result;
            Aligned(result);
            Repeat(result);
            Check(JsonConvert.SerializeObject(source) == original, "Alignment mutated the input");
            Check(result.Plates[0].Rebar.As1 == 12 && result.Plates[0].Rebar.As4 == 6, "As changed");
            var path = System.IO.Path.Combine(scratch, "aligned.json");
            SlabZoneAnalyzer.SaveJson(result, path);
            var loaded = SlabZoneAnalyzer.LoadJson(path, null);
            Aligned(loaded);
            Repeat(loaded);
            var rebuilt = SlabZoneAnalyzer.RebuildForElevation(result, 3.3, result.Settings);
            Aligned(rebuilt);
            Repeat(rebuilt);
            SlabZoneAnalyzer.SaveAllPlatesJson(result, path);
            var allLoaded = SlabZoneAnalyzer.LoadJson(path, null);
            Aligned(allLoaded);
            Repeat(allLoaded);
            Console.WriteLine("PASS contour / hole / mosaic / repeat / JSON / level: " + angle);
        }

        foreach (var angle in new double[] { 7.3456789, 33, 89.99 })
        foreach (var revitAngle in new double[] { 0, 15 })
        {
            var source = Slab(angle);
            source.Settings.RotationDeg = revitAngle - angle;
            source.Settings.OffsetXM = 1.23456789;
            source.Settings.OffsetYM = -3.45678901;
            var before = LiraRevitTransform.For(source, null);
            var link = new RevitLinkInfo { LiraElevationZM = 3.3,
                Registration = new RevitRegistration { RotationDeg = revitAngle - angle,
                    OffsetXM = source.Settings.OffsetXM, OffsetYM = source.Settings.OffsetYM,
                    RmsMm = 0, MatchedShare = 1 } };
            // Оси Revit получены независимо от координат исходных КЭ.
            var a = Rotate(-20, 0, revitAngle, 0);
            var b = Rotate(20, 0, revitAngle, 0);
            var c = Rotate(0, -20, revitAngle, 0);
            var d = Rotate(0, 20, revitAngle, 0);
            link.Grids.Add(new RevitGridLine { Name = "A", X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y });
            link.Grids.Add(new RevitGridLine { Name = "1", X1 = c.X, Y1 = c.Y, X2 = d.X, Y2 = d.Y });
            source.RevitLink = link;
            var report = SlabAxisAlignment.Align(source, null);
            var result = report.Result;
            Check(report.Source == "оси Revit", "Revit grids not preferred");
            Aligned(result);
            Repeat(result);
            var after = LiraRevitTransform.For(result, null);
            for (var i = 0; i < source.Plates.Count; i++)
            for (var j = 0; j < 4; j++)
            {
                var p = source.Plates[i].Contour[j];
                var q = result.Plates[i].Contour[j];
                var oldPoint = before.ToRevit(p.X, p.Y);
                var newPoint = after.ToRevit(q.X, q.Y);
                Check(Math.Abs(oldPoint.Item1 - newPoint.Item1) < 1e-10 && Math.Abs(oldPoint.Item2 - newPoint.Item2) < 1e-10,
                    "Placement in Revit changed");
            }
            var axes = RevitGridAlignment.AxesInLira(result, result.Settings);
            Check(axes.Count == 2, "Grid alignment lost rotated Revit axes");
            var z = new AdditionalZone { Layer = RebarLayer.As1, Contour = new List<Point3> {
                new Point3(102, 50, 3.3), new Point3(104, 50, 3.3),
                new Point3(104, 51, 3.3), new Point3(102, 51, 3.3) } };
            var zones = new List<AdditionalZone> { z };
            RevitGridAlignment.Apply(zones, axes, null, null, 0);
            Check(RevitGridAlignment.Check(zones, axes, null, null, 0).Count == 0, "10 mm ties invalid");
            var path = System.IO.Path.Combine(scratch, "linked.json");
            SlabZoneAnalyzer.SaveJson(result, path);
            var loaded = SlabZoneAnalyzer.LoadJson(path, null);
            Check(loaded.CoordinateFrame != null && loaded.RevitLink != null, "Frame/link not restored");
            Repeat(loaded);
            Console.WriteLine("PASS Revit placement / axes / 10mm / repeat / JSON: " + angle + " -> " + revitAngle);
        }

        // Нельзя доводить угол совмещения непосредственно до угла осей Revit:
        // надо вычесть исходный наклон контура ЛИРЫ (особенно при наклоне < 0,3°).
        var rotatedSource = Slab(0.123456789);
        rotatedSource.Settings.RotationDeg = 15 - 0.123456789;
        var known = LiraRevitTransform.For(rotatedSource, null);
        var targetLink = new RevitLinkInfo { LiraElevationZM = 3.3 };
        var targetFloor = new RevitFloorOutline();
        targetFloor.Loops.Add(rotatedSource.Outline.Select(p => {
            var q = known.ToRevit(p.X, p.Y); return new Point3(q.Item1, q.Item2, p.Z);
        }).ToList());
        targetLink.Floors.Add(targetFloor);
        var ga = Rotate(-20, 0, 15, 0);
        var gb = Rotate(20, 0, 15, 0);
        targetLink.Grids.Add(new RevitGridLine { X1 = ga.X, Y1 = ga.Y, X2 = gb.X, Y2 = gb.Y });
        var solved = RevitRegistrationSolver.Solve(rotatedSource, targetLink, null);
        Check(solved != null && solved.Reliable && solved.GridSnapped, "Rotated-source registration failed");
        Check(Math.Abs(solved.RotationDeg - rotatedSource.Settings.RotationDeg) * 3600 < 1,
            "Registration snapped away from the source contour direction");
        Console.WriteLine("PASS registration of a tilted LIRA slab to 15-degree Revit grids");
    }
}
