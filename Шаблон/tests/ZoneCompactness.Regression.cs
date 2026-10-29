using System;
using System.Linq;
using System.Collections.Generic;
using LiraSlabZones.Core;
using Newtonsoft.Json;
public static class ZoneCompactnessRegression
{
    private static List<Point3> Rect(double a,double b,double c,double d){return new List<Point3>{new Point3(a,c,0),new Point3(b,c,0),new Point3(b,d,0),new Point3(a,d,0)};}
    private static void Assert(bool ok,string text){if(!ok)throw new Exception(text);}
    private static AdditionalZone Zone(bool y,double from,double to,double cross){return new AdditionalZone {
        ZoneId=1,Layer=y?RebarLayer.As2:RebarLayer.As1,Direction=y?ZoneDirection.Y:ZoneDirection.X,
        Contour=y?Rect(cross,cross+0.4,from,to):Rect(from,to,cross,cross+0.4),LengthMm=(to-from)*1000,LengthM=to-from,
        WidthMm=400,WidthM=0.4,DiameterMm=8,BarStepMm=200,BarCount=3,ConcreteClass="B30",AsCoveredCm2PerM=2.51,
        FamilyKind=ZoneFamilyKind.Straight,IsValid=true,DesignEdgeCoverMm=50,AsAdditional=2.51 };}
    public static void Run()
    {
        foreach(var y in new[]{false,true})
        {
            var cfg=new AnalysisSettings {ConcreteClass="B30",BgBottomDiameterMm=8,BgBottomStepMm=200};cfg.SyncBackgroundAsFromBars();
            var outline=Rect(0,20,0,20);var constraints=new ZoneBoundaryConstraints(outline,null,50);
            var original=Zone(y,2,13.7,2);var zones=new List<AdditionalZone>{original};
            var plates=new List<LiraPlateElement>();
            foreach(var start in new[]{3.0,10.0})
            {
                var p=new LiraPlateElement {Id=plates.Count+1,Contour=y?Rect(2.1,2.3,start,start+1):Rect(start,start+1,2.1,2.3),
                    Rebar=new PlateReinforcement {Ok=true,As1=cfg.AsMainAs1+2,As2=cfg.AsMainAs2+2}};plates.Add(p);
            }
            var raw=JsonConvert.SerializeObject(plates);
            Assert(ZoneLayoutCompactness.AxialComponents(plates,original.Direction,1).Count==2,
                "Separate axial spots were combined into a continuous long run");
            Assert(ZoneLayoutCompactness.AxialComponents(plates,original.Direction,8).Count==1,
                "A short anchorage interval was incorrectly divided");
            Assert(ZoneLayoutCompactness.Refine(zones,plates,cfg,outline,null,constraints)>0,"Sparse axial demand was not split");
            Assert(zones.Count==2 && zones.Sum(z=>z.LengthMm)<5000,"Empty axial run was retained");
            Assert(zones.All(z=>z.BarCount==3 && z.DiameterMm==8 && z.BarStepMm==200),"Split changed the bar parameters");
            Assert(!ZoneEditor.HasPlacementConflict(zones[0],zones[1]),"Split introduced overlap");
            foreach(var p in plates) Assert(ZoneCoverageRules.CoversOrBridgesGap(zones,p,original.Layer,2,outline,null),"Lost FE coverage");
            var anchor=RebarTables.AnchorageLenMm("B30",8)/1000.0;
            for(var i=0;i<zones.Count;i++)
            {
                var b=ZoneBoundaryConstraints.Bounds(zones.OrderBy(z=>y?z.Contour.Min(v=>v.Y):z.Contour.Min(v=>v.X)).ElementAt(i).Contour);
                var start=i==0?3:10;
                Assert((y?b.MinY:b.MinX)<=start-anchor+0.000002 && (y?b.MaxY:b.MaxX)>=start+1+anchor-0.000002,"Shortened straight anchorage");
            }
            Assert(raw==JsonConvert.SerializeObject(plates),"Source FE/As mutated");
            var stable=JsonConvert.SerializeObject(zones);
            ZoneLayoutCompactness.Refine(zones,plates,cfg,outline,null,constraints);
            Assert(stable==JsonConvert.SerializeObject(zones),"Refinement is not idempotent");
            // A required FE may lie entirely between two rows and need both of them.
            // Shortening must preserve this bridge even when the FE does not touch either rectangle.
            zones=new List<AdditionalZone>{Zone(y,2,13.7,2),Zone(y,2,13.7,2.6)};
            var gap=new LiraPlateElement {Id=3,Contour=y?Rect(2.45,2.55,7,8):Rect(7,8,2.45,2.55),
                Rebar=new PlateReinforcement{Ok=true,As1=cfg.AsMainAs1+2,As2=cfg.AsMainAs2+2}};
            plates.Add(gap);
            Assert(ZoneCoverageRules.CoversOrBridgesGap(zones,gap,original.Layer,2,outline,null),"Invalid bridge fixture");
            ZoneLayoutCompactness.Refine(zones,plates,cfg,outline,null,constraints);
            Assert(ZoneCoverageRules.CoversOrBridgesGap(zones,gap,original.Layer,2,outline,null),"Optimization lost an FE in the inter-row gap");
            zones=new List<AdditionalZone>{Zone(y,2,7,2),Zone(y,2,7,2.6),Zone(y,2,7,3.2)};
            for(var i=0;i<zones.Count;i++)zones[i].AsRequired=4+i;
            Assert(ZoneLayoutCompactness.MergeRows(zones,cfg,constraints)==2 && zones.Count==1 && zones[0].BarCount==9,
                "Equal rows one pitch apart were not joined with exact bar count");
            Assert(zones[0].Rebar.Get(zones[0].Layer)==6,"Merged row reinforcement metadata does not match its governing demand");
            var other=Zone(y,2,7,3.8);other.DiameterMm=10;zones.Add(other);
            Assert(ZoneLayoutCompactness.MergeRows(zones,cfg,constraints)==0,"Different diameters were joined");
            var wide=Zone(y,2,8,2);
            wide.Contour=y?Rect(2,6,2,8):Rect(2,8,2,6);
            wide.WidthM=4;wide.WidthMm=4000;wide.BarStepMm=100;wide.BarCount=41;wide.DiameterMm=16;
            wide.AsCoveredCm2PerM=BarCapacity.AsCm2PerM(16,100);
            zones=new List<AdditionalZone>{wide};plates.Clear();
            foreach(var strong in new[]{false,true})
            {
                var low=strong?5.5:2.1;var high=strong?5.8:2.3;var demand=strong?15:2;
                plates.Add(new LiraPlateElement {Id=plates.Count+1,Contour=y?Rect(low,high,3,4):Rect(3,4,low,high),
                    Rebar=new PlateReinforcement {Ok=true,As1=cfg.AsMainAs1+demand,As2=cfg.AsMainAs2+demand}});
            }
            var steel=wide.LengthMm*wide.BarCount*wide.DiameterMm*wide.DiameterMm;
            Assert(ZoneLayoutCompactness.Refine(zones,plates,cfg,outline,null,constraints)>0,"Wide peak-controlled zone was not localized");
            Assert(zones.Sum(z=>z.LengthMm*z.BarCount*z.DiameterMm*z.DiameterMm)<steel*0.75,"Local demand did not reduce excessive steel consumption");
            Assert(zones.Any(z=>z.DiameterMm==8) && zones.Any(z=>z.DiameterMm==16),"Local diameter differences were lost");
            foreach(var p in plates)Assert(ZoneCoverageRules.CoversOrBridgesGap(zones,p,wide.Layer,p.Rebar.Get(wide.Layer)-(y?cfg.AsMainAs2:cfg.AsMainAs1),outline,null),
                "Local capacity partition lost an FE footprint");
        }
        Console.WriteLine("PASS compactness X/Y: separated axial demand, table anchorage, exact bar counts, equal-row merge, no mutation, idempotence");
    }
}
