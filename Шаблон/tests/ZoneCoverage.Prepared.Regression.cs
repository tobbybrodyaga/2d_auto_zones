using System;
using System.Collections.Generic;
using System.Reflection;
using LiraSlabZones.Core;

public static class ZoneCoveragePreparedRegression
{
    private static readonly MethodInfo Prepare = typeof(ZoneCoverageRules).GetMethod(
        "Prepare", BindingFlags.Static | BindingFlags.NonPublic);

    private static List<Point3> Rect(double x, double y, double w, double h)
    {
        return new List<Point3> { new Point3(x,y,0), new Point3(x+w,y,0),
            new Point3(x+w,y+h,0), new Point3(x,y+h,0) };
    }

    private static IList<AdditionalZone> Batch(List<AdditionalZone> zones)
    {
        return (IList<AdditionalZone>)Prepare.Invoke(null, new object[] { zones });
    }

    public static void Run()
    {
        var random = new Random(130);
        var outline = Rect(-3,-3,26,26);
        var openings = new List<OpeningInfo> {
            new OpeningInfo { MinXM=6, MaxXM=7, MinYM=4, MaxYM=6 } };
        int checks=0;
        for (int scenario=0; scenario<36; scenario++)
        {
            var zones = new List<AdditionalZone>();
            for (int i=0; i<scenario*3; i++)
            {
                var contour=Rect(random.Next(-2,20), random.Next(-2,20),
                    0.1+random.Next(1,40)/10.0, 0.1+random.Next(1,40)/10.0);
                if (i%11==0) contour.RemoveAt(2); // Also exercise nonrectangular coverage.
                zones.Add(new AdditionalZone { Layer=(RebarLayer)(i%4),
                    Direction=i%2==0 ? ZoneDirection.X : ZoneDirection.Y,
                    Contour=contour, AsCoveredCm2PerM=1+i%5,
                    BarStepMm=new[] {100,200,500}[i%3], DesignEdgeCoverMm=i%3==0 ? 50 : 0 });
            }
            var prepared=Batch(zones);
            for (int i=0; i<150; i++)
            {
                double x=random.Next(-4,24)+random.NextDouble();
                double y=random.Next(-4,24)+random.NextDouble();
                var plate=new LiraPlateElement { Centroid=new Point3(x+0.2,y+0.2,0),
                    Contour=Rect(x,y,0.4,0.4) };
                if (i%13==0) plate.Contour.Clear();
                if (i%17==0) plate.Contour=Rect(x,y,0,1); // Degenerate FE uses its centroid.
                var layer=(RebarLayer)(i%4);
                var demand=new[] {0.0,1.0,2.000001,4.0,7.0}[i%5];
                var slab=i%3==0 ? null : outline;
                var holes=i%4==0 ? null : openings;
                foreach (bool overlap in new[] {false,true})
                {
                    bool expected=ZoneCoverageRules.CoversOrBridgesGap(zones,plate,layer,demand,slab,holes,overlap);
                    bool actual=ZoneCoverageRules.CoversOrBridgesGap(prepared,plate,layer,demand,slab,holes,overlap);
                    if (expected!=actual) throw new Exception("Prepared coverage differs: "+scenario+"/"+i);
                    checks++;
                }
            }
            // Every changed layout gets a new batch, including in-place geometry edits.
            if (zones.Count>0)
            {
                zones[0].Contour[0].X+=0.05;
                zones[0].AsCoveredCm2PerM=9;
                zones[0].BarStepMm=100;
                zones[0].DesignEdgeCoverMm=100;
                var plate=new LiraPlateElement {Contour=Rect(1,1,0.2,0.2)};
                bool expected=ZoneCoverageRules.CoversOrBridgesGap(zones,plate,zones[0].Layer,3,outline,openings,true);
                bool actual=ZoneCoverageRules.CoversOrBridgesGap(Batch(zones),plate,zones[0].Layer,3,outline,openings,true);
                if (expected!=actual) throw new Exception("Rebuilt coverage retained stale geometry");
            }
        }
        var fallback=new LiraPlateElement {Contour=Rect(100,100,0,0),Centroid=new Point3(0.005,0.005,0)};
        var zone=new AdditionalZone {Contour=Rect(-1,-1,1,1)};
        if (!ZoneCoverageRules.IntersectsElementFootprint(zone,fallback,outline))
            throw new Exception("Early bounds rejection lost degenerate FE centroid fallback");
        Console.WriteLine("PASS "+checks+" prepared/full-scan coverage comparisons: layers, capacities, steps, polygons, gaps, openings, cover, fallback and rebuilt batches");
    }
}
