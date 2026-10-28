using System;
using System.Collections.Generic;
using System.Linq;
using LiraSlabZones.Core;
using Newtonsoft.Json;

public static class ZoneConflictGroupsRegression
{
    private static void Assert(bool value,string message) {if(!value) throw new Exception(message);}
    private static List<Point3> Rect(double x1,double x2,double y1,double y2)
    {return new List<Point3>{new Point3(x1,y1,0),new Point3(x2,y1,0),new Point3(x2,y2,0),new Point3(x1,y2,0)};}
    private static AnalysisSettings Settings()
    {return new AnalysisSettings{ConcreteClass="B30",BgBottomDiameterMm=8,BgBottomStepMm=200,MinZoneWidthM=0.3};}
    private static void Add(List<AdditionalZone> zones,List<LiraPlateElement> plates,double x,double y,double demand,
        AnalysisSettings cfg,ZoneBoundaryConstraints constraints,List<Point3> outline)
    {
        cfg.SyncBackgroundAsFromBars();
        int id=plates.Count+1;
        var plate=new LiraPlateElement{Id=id,Contour=Rect(x,x+1,y,y+0.4),
            Rebar=new PlateReinforcement{Ok=true,As1=cfg.AsMainAs1+demand}};
        plates.Add(plate);
        var frame=new ZonePatchFrameSelection{BoundaryConstraints=constraints,SlabOutline=outline,
            MinXM=x,MaxXM=x+1,MinYM=y,MaxYM=y+0.4};
        frame.Elements.Add(new ZonePatchFrameElement{ElementId=id,Layer=RebarLayer.As1,Contour=plate.Contour,AsAdditionalCm2PerM=demand});
        frame.Patches.Add(new ZonePatch{Layer=RebarLayer.As1,Direction=ZoneDirection.X,PatchId=id,
            MinXM=x,MaxXM=x+1,MinYM=y,MaxYM=y+0.4,ElementIds=new List<int>{id},PeakAsAdditionalCm2PerM=demand,
            Cells=new List<ZonePatchCell>{new ZonePatchCell{MinXM=x,MaxXM=x+1,MinYM=y,MaxYM=y+0.4,AsAdditionalCm2PerM=demand}}});
        foreach(var z in ZonePatchZoneBuilder.Build(frame,0,cfg)){z.ZoneId=zones.Count+1;zones.Add(z);}
    }
    private static void Check(List<AdditionalZone> zones,List<LiraPlateElement> plates,AnalysisSettings cfg,
        List<Point3> outline,ZoneBoundaryConstraints c)
    {
        for(var i=0;i<zones.Count;i++)
        {
            Assert(c.Check(zones[i].Contour,zones[i].Direction).Allowed,"A group was moved through the slab boundary");
            for(var j=i+1;j<zones.Count;j++)
                Assert(!ZoneEditor.HasPlacementConflict(zones[i],zones[j]),"Group solver left a forbidden pair");
        }
        foreach(var plate in plates)
            Assert(ZoneCoverageRules.CoversOrBridgesGap(zones,plate,RebarLayer.As1,plate.Rebar.As1-cfg.AsMainAs1,outline,null),
                "Group solver lost a finite-element footprint or capacity");
        Assert(ZoneLayoutDiagnostics.Evaluate(plates,zones,cfg,0,true,outline,null).ConflictCount==0,
            "Group solver left an incorrect inter-row spacing");
    }
    public static void Run()
    {
        var lapA=new AdditionalZone{ZoneId=1,Layer=RebarLayer.As1,Direction=ZoneDirection.X,
            DiameterMm=8,BarStepMm=200,AsCoveredCm2PerM=2.51,ConcreteClass="B30",LengthMm=11700,LengthM=11.7,Contour=Rect(0,11.7,2,2.4)};
        var lapB=new AdditionalZone{ZoneId=2,Layer=RebarLayer.As1,Direction=ZoneDirection.X,
            DiameterMm=8,BarStepMm=200,AsCoveredCm2PerM=2.51,ConcreteClass="B30",LengthMm=5850,LengthM=5.85,Contour=Rect(10.4,16.25,2,2.4)};
        var across=new LiraPlateElement{Contour=Rect(9,14,2.1,2.3)};
        var splice=new List<AdditionalZone>{lapA,lapB};
        Assert(ZoneCoverageRules.CoversOrBridgesGap(splice,across,RebarLayer.As1,2,null,null),
            "A valid 11700 mm longitudinal lap must cover an FE crossing the joint");
        lapB.Contour=Rect(10.402,16.252,2,2.4);
        Assert(!ZoneCoverageRules.CoversOrBridgesGap(splice,across,RebarLayer.As1,2,null,null),
            "Coverage must not accept a lap shorter than the required value beyond the geometry tolerance");
        lapB.Contour=Rect(10.4,16.25,2.002,2.402);
        Assert(!ZoneCoverageRules.CoversOrBridgesGap(splice,across,RebarLayer.As1,2,null,null),
            "Side overlaps must not be disguised as valid longitudinal splices");
        Console.WriteLine("PASS coverage across approved splices; insufficient laps and lateral overlaps remain invalid");
        var outline=Rect(0,20,0,10);
        var c=new ZoneBoundaryConstraints(outline,null,50);
        var cfg=Settings();
        var zones=new List<AdditionalZone>();var plates=new List<LiraPlateElement>();
        Add(zones,plates,4,1.9,3,cfg,c,outline);
        Add(zones,plates,4,2.1,7,cfg,c,outline);
        Add(zones,plates,11,1.9,3,cfg,c,outline);
        Add(zones,plates,11,2.1,7,cfg,c,outline);
        Add(zones,plates,16,7,3,cfg,c,outline);
        zones[zones.Count-1].NodeIds.Clear();
        var input=JsonConvert.SerializeObject(plates);
        var settings=JsonConvert.SerializeObject(cfg);
        var report=ZoneConflictGroupLayout.Resolve(zones,plates,cfg,outline,null,c);
        Assert(report.PairsBefore==2 && report.PairsAfter==0 && report.GroupsResolved==2 && report.Solved,
            "Independent groups must both be resolved, even after replacing the first group: "+JsonConvert.SerializeObject(report));
        Assert(zones.Count<=5,"Solving two simple groups must not introduce unnecessary extra arrays");
        Assert(zones.All(z=>z.NodeIds.Count>0),"After neighbor adjustment FE witnesses must be refreshed even when no array is removed");
        Assert(zones.Select(z=>z.ZoneId).Distinct().Count()==zones.Count,"Replacement arrays must have unique final IDs");
        Check(zones,plates,cfg,outline,c);
        Assert(input==JsonConvert.SerializeObject(plates) && settings==JsonConvert.SerializeObject(cfg),
            "Group solver changed source FE data or allowed reinforcement settings");
        var stable=JsonConvert.SerializeObject(zones);
        var again=ZoneConflictGroupLayout.Resolve(zones,plates,cfg,outline,null,c);
        Assert(again.Solved && again.GroupsResolved==0 && stable==JsonConvert.SerializeObject(zones),
            "A collision-free layout must be idempotent");
        Console.WriteLine("PASS group replacement: mixed capacities, two independent groups, full coverage, exact gaps, idempotence");

        zones.Clear();plates.Clear();
        Add(zones,plates,4,1.9,3,cfg,c,outline);
        Add(zones,plates,4,2.1,3,cfg,c,outline);
        plates.Clear();
        plates.Add(new LiraPlateElement{Id=100,Contour=Rect(4,5,1.9,2.5),
            Rebar=new PlateReinforcement{Ok=true,As1=cfg.AsMainAs1+3}});
        var unionOnly=ZoneConflictGroupLayout.Resolve(zones,plates,cfg,outline,null,c);
        Assert(unionOnly.Solved && zones.Count>0,"An FE covered only by the old overlapping union must not be discarded");
        Check(zones,plates,cfg,outline,c);
        Console.WriteLine("PASS full demand preserved when neither old rectangle covers the FE alone");

        zones.Clear();plates.Clear();
        cfg.SlabThicknessMm=50; // A bend physically cannot fit between the protective layers.
        Add(zones,plates,0.05,2,3,cfg,c,outline);
        Add(zones,plates,0.05,2.2,3,cfg,c,outline);
        stable=JsonConvert.SerializeObject(zones);
        var blocked=ZoneConflictGroupLayout.Resolve(zones,plates,cfg,outline,null,c);
        Assert(!blocked.Solved && blocked.BoundaryAfter>0 && blocked.PairsAfter>0,
            "An impossible group must not be reported as collision-free");
        Assert(stable==JsonConvert.SerializeObject(zones),"Failed group search partially modified live zones");
        Console.WriteLine("PASS failed group is atomic, retains demand and does not fake success");
    }
}
