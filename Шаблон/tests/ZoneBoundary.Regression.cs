using System;
using System.Collections.Generic;
using System.Linq;
using LiraSlabZones.Core;
using Newtonsoft.Json;

public static class ZoneBoundaryRegression
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static ZonePatchFrameBounds B(double x1, double x2, double y1, double y2)
    { return new ZonePatchFrameBounds(x1, x2, y1, y2); }
    public static List<Point3> Rect(double x1, double x2, double y1, double y2)
    { return new List<Point3> { new Point3(x1,y1,0),new Point3(x2,y1,0),new Point3(x2,y2,0),new Point3(x1,y2,0) }; }
    private static bool Contains(ZonePatchFrameBounds b, ZonePatchFrameBounds r)
    { return b.MinX <= r.MinX + 0.000002 && b.MaxX >= r.MaxX - 0.000002 && b.MinY <= r.MinY + 0.000002 && b.MaxY >= r.MaxY - 0.000002; }
    private static ZonePatchFrameBounds Fit(ZoneBoundaryConstraints c, ZonePatchFrameBounds source,
        ZonePatchFrameBounds required, ZoneDirection direction)
    {
        ZonePatchFrameBounds fit; ZoneBoundaryCheck check;
        Assert(c.TryFit(source,required,direction,out fit,out check), check.Message);
        Assert(check.Allowed && Contains(fit,required), "Fit lost required footprint or anchorage");
        Assert(Math.Abs((fit.MaxX-fit.MinX)-(source.MaxX-source.MinX))<1e-9 &&
            Math.Abs((fit.MaxY-fit.MinY)-(source.MaxY-source.MinY))<1e-9, "Fit resized bars");
        return fit;
    }
    public static void Run()
    {
        var outline=Rect(0,20,0,10);
        var c=new ZoneBoundaryConstraints(outline,null,50);
        var top=Fit(c,B(2,8,9.7,10.1),B(2,8,9.75,9.85),ZoneDirection.X);
        Assert(Math.Abs(top.MaxY-9.95)<0.000002,"Top edge must move inward to 50 mm");
        var bottom=Fit(c,B(2,8,-0.1,0.3),B(2,8,0.15,0.25),ZoneDirection.X);
        Assert(Math.Abs(bottom.MinY-0.05)<0.000002,"Bottom edge moved outward");
        var left=Fit(c,B(-0.1,0.3,2,8),B(0.15,0.25,2,8),ZoneDirection.Y);
        Assert(Math.Abs(left.MinX-0.05)<0.000002,"Y bars: left edge moved outward");
        var right=Fit(c,B(19.7,20.1,2,8),B(19.75,19.85,2,8),ZoneDirection.Y);
        Assert(Math.Abs(right.MaxX-19.95)<0.000002,"Y bars: right edge moved outward");
        Fit(c,B(-0.05,0.45,9.6,10.1),B(0.15,0.35,9.75,9.9),ZoneDirection.X);
        Assert(c.Check(B(2,8,0.05,0.45),ZoneDirection.X).Allowed,"Exactly 50 mm rejected");
        Assert(!c.Check(B(2,8,0.049,0.449),ZoneDirection.X).Allowed,"49 mm accepted");
        Console.WriteLine("PASS four edges, both bar directions, outer corner, exact 50 mm");

        ZonePatchFrameBounds unchanged; ZoneBoundaryCheck blocked;
        var original=B(2,8,9.7,10.1);
        Assert(!c.TryFit(original,B(2,8,9.8,10),ZoneDirection.X,out unchanged,out blocked),"Coverage loss accepted");
        Assert(unchanged.MinY==original.MinY && unchanged.MaxY==original.MaxY,"Conflict silently moved the zone");
        Assert(blocked.Message.Contains("анкеровки"),"Missing reason for unsatisfied mandatory footprint");
        Assert(!c.TryFit(B(-0.1,1.9,2,3),B(-0.05,1.85,2.1,2.9),ZoneDirection.X,out unchanged,out blocked),
            "Longitudinal anchorage shortened");
        Console.WriteLine("PASS impossible fit retains geometry and reports coverage/anchorage conflict");

        var holes=new List<OpeningInfo>{new OpeningInfo{MinXM=8,MaxXM=10,MinYM=4,MaxYM=6}};
        var h=new ZoneBoundaryConstraints(outline,holes,50);
        Assert(!h.Check(B(7,11,3,7),ZoneDirection.X).Allowed,"Internal hole missed when all rectangle corners are outside hole");
        var nearHole=Fit(h,B(7.7,8.1,4.5,5.5),B(7.75,7.9,4.5,5.5),ZoneDirection.Y);
        Assert(Math.Abs(nearHole.MaxX-7.95)<0.000002,"Hole clearance not respected");
        var notch=new List<Point3>{new Point3(0,0,0),new Point3(10,0,0),new Point3(10,10,0),
            new Point3(6,10,0),new Point3(6,4,0),new Point3(4,4,0),new Point3(4,10,0),new Point3(0,10,0)};
        Assert(!new ZoneBoundaryConstraints(notch,null,50).Check(B(3,7,3,5),ZoneDirection.X).Allowed,
            "Concave notch crossing missed by corner-only check");
        var shaped=new List<Point3>{new Point3(1,1,0),new Point3(9,1,0),new Point3(9,3,0),
            new Point3(3,3,0),new Point3(3,9,0),new Point3(1,9,0)};
        Assert(new ZoneBoundaryConstraints(notch,null,50).Check(shaped,ZoneDirection.X).Allowed,
            "Valid nonrectangular zone rejected because of its bounding box");
        var triangle=new List<Point3>{new Point3(0,0,0),new Point3(10,0,0),new Point3(10,10,0)};
        var diagonal=new ZoneBoundaryConstraints(triangle,null,50);
        var diagonalFit=Fit(diagonal,B(3,5,2,3),B(3.1,4.9,2.1,2.8),ZoneDirection.X);
        Assert(diagonal.Check(diagonalFit,ZoneDirection.X).ActualMm>=49.99,"Sloping edge: distance is not normal to edge");
        Console.WriteLine("PASS hole, hole clearance, concave notch, sloping edge");

        var narrow=new ZoneBoundaryConstraints(Rect(0,20,0,1),null,50);
        var centered=narrow.Check(B(2,8,0.04,0.96),ZoneDirection.X);
        Assert(centered.Allowed && centered.CenteredException && centered.Message.Length>0,"Narrow exception hidden");
        Assert(!narrow.Check(B(2,8,0.02,0.94),ZoneDirection.X).Allowed,"Uncentered narrow zone accepted");
        Assert(!narrow.Check(B(2,8,0,1),ZoneDirection.X).Allowed,"Zero-clearance zone accepted as centered exception");
        var narrowFit=Fit(narrow,B(2,8,0.02,0.94),B(2,8,0.1,0.9),ZoneDirection.X);
        Assert(narrow.Check(narrowFit,ZoneDirection.X).CenteredException,"Narrow fit not explicitly flagged");
        var stripHole=new List<OpeningInfo>{new OpeningInfo{MinXM=4,MaxXM=6,MinYM=0.4,MaxYM=0.6}};
        Assert(!new ZoneBoundaryConstraints(Rect(0,20,0,1),stripHole,50).Check(B(2,8,0.04,0.96),ZoneDirection.X).Allowed,
            "Narrow exception concealed a hole intersection");
        Console.WriteLine("PASS narrow place centering with explicit exception");

        var z=new AdditionalZone{ZoneId=1,Direction=ZoneDirection.X,Contour=Rect(2,8,0.05,0.45),
            Placement=new Point3(5,0.25,0),LengthM=6,WidthM=0.4,LengthMm=6000,WidthMm=400,
            DiameterMm=8,BarStepMm=200,BarCount=3,AsCoveredCm2PerM=3,BoundaryConstraints=c};
        var before=JsonConvert.SerializeObject(z.Contour);
        Assert(!ZoneEditor.Move(z,0,-0.001,outline,false),"Move crossed the 50 mm limit");
        Assert(before==JsonConvert.SerializeObject(z.Contour),"Rejected move changed geometry");
        Assert(ZoneEditor.Move(z,0,0.05,outline,false),"Inward move rejected");
        Assert(ZoneEditor.Move(z,0.1,0,outline,false),"Tangential safe move rejected");
        var zones=new List<AdditionalZone>{z};
        var axes=new List<AlignAxis>{new AlignAxis{Name="A",Vertical=false,X1=0,Y1=0.007,X2=20,Y2=0.007}};
        RevitGridAlignment.Apply(zones,axes,outline,null,50);
        Assert(c.Check(z.Contour,z.Direction).Allowed,"10 mm snap violated boundary");
        c.CheckZones(zones);
        var json=JsonConvert.SerializeObject(z);
        Assert(!json.Contains("BoundaryConstraints"),"Runtime polygon duplicated in JSON");
        var loaded=JsonConvert.DeserializeObject<AdditionalZone>(json);
        Assert(loaded.BoundaryConstraints==null && loaded.BoundaryCheck.Checked,"JSON did not preserve report");
        Assert(c.CheckZones(new[]{loaded}).Count==0,"Reload boundary check differs");
        var baseline=c.Snapshot(zones);
        var plate=new LiraPlateElement{Id=1,Contour=Rect(3,4,0.15,0.25),
            Centroid=new Point3(3.5,0.2,0),Rebar=new PlateReinforcement{Ok=true,As1=1}};
        var preservedJson=JsonConvert.SerializeObject(baseline);
        z.Contour=Rect(2,8,2,2.4);
        var preserved=c.PreserveCoverage(zones,baseline,new List<LiraPlateElement>{plate},
            new AnalysisSettings(),outline,new List<OpeningInfo>());
        Assert(preserved==1 && zones.Any(item=>item.Contour.Min(p=>p.Y)<0.2),"Coverage loss was not restored");
        Assert(preservedJson==JsonConvert.SerializeObject(baseline),"Coverage rollback mutated the baseline");
        Assert(c.PreserveCoverage(zones,baseline,new List<LiraPlateElement>{plate},
            new AnalysisSettings(),outline,new List<OpeningInfo>())==0,"Idempotent coverage check rebuilt the row");
        Console.WriteLine("PASS move guard, inward/tangential movement, 10 mm snap, JSON");

        TestBuilder(9.6,9.92,true);
        TestBuilder(9.8,10,true);
        TestAutomaticLayout();
        TestEdgeForms();
        Assert(!new ZoneBoundaryConstraints(null,null,50).Check(B(0,1,0,1),ZoneDirection.X).Checked,
            "Missing outline must remain explicitly unchecked");
        Console.WriteLine("PASS builder preflight before length splitting; footprint and As unchanged");
    }

    private static ZonePatchFrameSelection Frame(List<Point3> outline, params ZonePatchFrameBounds[] bounds)
    {
        var frame=new ZonePatchFrameSelection{SlabOutline=outline,
            BoundaryConstraints=new ZoneBoundaryConstraints(outline,null,50),
            MinXM=bounds.Min(b=>b.MinX),MaxXM=bounds.Max(b=>b.MaxX),
            MinYM=bounds.Min(b=>b.MinY),MaxYM=bounds.Max(b=>b.MaxY)};
        for(var i=0;i<bounds.Length;i++)
        {
            var b=bounds[i];
            frame.Patches.Add(new ZonePatch{PatchId=i+1,Layer=RebarLayer.As1,Direction=ZoneDirection.X,
                MinXM=b.MinX,MaxXM=b.MaxX,MinYM=b.MinY,MaxYM=b.MaxY,PeakAsAdditionalCm2PerM=3,
                ElementIds=new List<int>{i+1},Cells=new List<ZonePatchCell>{new ZonePatchCell{
                    MinXM=b.MinX,MaxXM=b.MaxX,MinYM=b.MinY,MaxYM=b.MaxY,AsAdditionalCm2PerM=3}}});
            frame.Elements.Add(new ZonePatchFrameElement{ElementId=i+1,Layer=RebarLayer.As1,
                AsAdditionalCm2PerM=3,Contour=Rect(b.MinX,b.MaxX,b.MinY,b.MaxY)});
        }
        return frame;
    }

    private static void AssertSolved(ZonePatchFrameSelection frame, List<AdditionalZone> zones, AnalysisSettings cfg)
    {
        Assert(zones.Count>0 && zones.All(z=>z.BoundaryCheck.Allowed),"Automatic solution still violates boundary");
        var cores=new List<AdditionalZone>();
        foreach(var zone in zones)
        {
            Assert(zone.FamilyKind!=ZoneFamilyKind.Straight || RebarTables.Sum3FamilyLengthsMm.Any(l=>Math.Abs(l-zone.LengthMm)<0.001),"Unsupported stock length");
            Assert(!RebarTables.ExceedsMaxBarLength(zone),"Bent cut length exceeds stock");
            Assert(Math.Abs(zone.WidthMm/zone.BarStepMm-Math.Round(zone.WidthMm/zone.BarStepMm))<1e-6,
                "Automatic solution changed width to a nonmultiple of the step");
            var b=ZoneBoundaryConstraints.Bounds(zone.Contour);
            var a=RebarTables.AnchorageLenMm(cfg.ConcreteClass,zone.DiameterMm)/1000.0;
            var copy=JsonConvert.DeserializeObject<AdditionalZone>(JsonConvert.SerializeObject(zone));
            var start=zone.BendAtStart?0:a;var end=zone.BendAtEnd?0:a;
            copy.Contour=zone.Direction==ZoneDirection.X ? Rect(b.MinX+start,b.MaxX-end,b.MinY,b.MaxY)
                : Rect(b.MinX,b.MaxX,b.MinY+start,b.MaxY-end);
            cores.Add(copy);
        }
        foreach(var e in frame.Elements)
        {
            var plate=new LiraPlateElement{Contour=e.Contour.ToList()};
            Assert(ZoneCoverageRules.CoversOrBridgesGap(zones,plate,e.Layer,e.AsAdditionalCm2PerM,frame.SlabOutline,null),
                "Automatic split lost part of a finite element, including the new inter-row gap");
            Assert(ZoneCoverageRules.CoversOrBridgesGap(cores,plate,e.Layer,e.AsAdditionalCm2PerM,frame.SlabOutline,null,true),
                "Automatic solution shortened mandatory anchorage");
        }
        for(var i=0;i<zones.Count;i++) for(var j=i+1;j<zones.Count;j++)
            Assert(!ZoneEditor.HasPlacementConflict(zones[i],zones[j]),"Automatic split introduced an overlap or insufficient gap");
    }

    private static void TestAutomaticLayout()
    {
        var cfg=new AnalysisSettings{ConcreteClass="B30",BgBottomDiameterMm=8,BgBottomStepMm=200,MinZoneWidthM=0.3};
        var stock=Frame(Rect(0,6,0,5),B(0.5,4,2,2.4));
        var fitted=ZonePatchZoneBuilder.Build(stock,0,cfg);
        AssertSolved(stock,fitted,cfg);
        Assert(fitted.Count==1 && fitted[0].BendAtStart &&
            fitted[0].Contour.Min(p=>p.X)>=0.049999,"Stock length should extend inward instead of through the left edge");
        var shorter=Frame(Rect(0,5,0,5),B(0.4,1.5,2,2.4));
        var chosen=ZonePatchZoneBuilder.Build(shorter,0,cfg);
        AssertSolved(shorter,chosen,cfg);
        Assert(chosen.Count==1 && chosen[0].BendAtStart && chosen[0].FamilyKind==ZoneFamilyKind.L,
            "A terminating bar must bend rather than extend through the slab edge");
        cfg.AllowedAdditionalBarStepsMm=new List<int>{200};
        var forbidden=ZonePatchZoneBuilder.Build(shorter,0,cfg);
        Assert(forbidden.All(z=>z.BarStepMm==200),
            "Solver silently enabled a forbidden bar step");
        cfg.AllowedAdditionalBarStepsMm=new List<int>{100,200};
        cfg.ExcludedZoneDiametersMm=new List<int>{8};
        var excluded=ZonePatchZoneBuilder.Build(shorter,0,cfg);
        Assert(excluded.All(z=>z.DiameterMm!=8),
            "Solver silently enabled an excluded diameter");
        cfg.ExcludedZoneDiametersMm.Clear();

        var longBounds=Enumerable.Range(0,24).Select(i=>B(2+i,3+i,2,2.4)).ToArray();
        var longFrame=Frame(Rect(0,30,0,10),longBounds);
        var longZones=ZonePatchZoneBuilder.Build(longFrame,0,cfg).OrderBy(z=>z.Contour.Min(p=>p.X)).ToList();
        AssertSolved(longFrame,longZones,cfg);
        Assert(longZones.Count==3,"A long frame must retain the required stock-length split");
        for(var i=0;i<longZones.Count-1;i++)
        {
            var overlap=longZones[i].Contour.Max(p=>p.X)-longZones[i+1].Contour.Min(p=>p.X);
            Assert(overlap*1000+0.001>=2*RebarTables.LapLenMm(cfg.ConcreteClass,longZones[i].DiameterMm),
                "Stock placement shortened the mandatory longitudinal lap");
        }

        var repairFrame=Frame(Rect(0,6,0,10),B(0.5,4,2,2.4),B(0.5,4,7,7.4));
        var repairZones=new List<AdditionalZone>();
        var repairPlates=new List<LiraPlateElement>();
        for(var i=0;i<2;i++)
        {
            var old=JsonConvert.DeserializeObject<AdditionalZone>(JsonConvert.SerializeObject(fitted[0]));
            old.ZoneId=i+1;
            old.FamilyKind=ZoneFamilyKind.Straight;old.BendAtStart=false;old.BendAtEnd=false;old.VerticalLegMm=0;
            old.Contour=old.Contour.Select(p=>new Point3(p.X-0.2,p.Y+i*5,p.Z)).ToList();
            old.Placement=new Point3(old.Placement.X-0.2,old.Placement.Y+i*5,0);
            repairZones.Add(old);
            repairPlates.Add(new LiraPlateElement{Id=i+1,Contour=repairFrame.Elements[i].Contour.ToList(),
                Rebar=new PlateReinforcement{Ok=true,As1=cfg.AsMainAs1+3}});
        }
        var untouched=JsonConvert.SerializeObject(repairPlates);
        Assert(ZoneBoundaryLayoutSolver.Repair(repairZones,repairPlates,cfg,repairFrame.SlabOutline,null,
            repairFrame.BoundaryConstraints)==2,"Final pass must repair both areas after replacing a layer snapshot");
        AssertSolved(repairFrame,repairZones,cfg);
        Assert(untouched==JsonConvert.SerializeObject(repairPlates),"Final repair modified source FE demand or geometry");
        cfg.SlabThicknessMm=50;
        var impossibleFrame=Frame(Rect(0,6,0,5),B(0.05,1,2,2.4));
        var impossibleZones=ZonePatchZoneBuilder.Build(impossibleFrame,0,cfg);
        var impossiblePlates=new List<LiraPlateElement>{new LiraPlateElement{Id=1,
            Contour=impossibleFrame.Elements[0].Contour.ToList(),Rebar=new PlateReinforcement{Ok=true,As1=cfg.AsMainAs1+3}}};
        var unchanged=JsonConvert.SerializeObject(impossibleZones);
        Assert(ZoneBoundaryLayoutSolver.Repair(impossibleZones,impossiblePlates,cfg,impossibleFrame.SlabOutline,null,
            impossibleFrame.BoundaryConstraints)==0 && unchanged==JsonConvert.SerializeObject(impossibleZones),
            "Infeasible anchorage must retain geometry and diagnostics");
        Assert(impossibleZones.Any(z=>!z.IsValid),"An impossible bend height was accepted");
        cfg.SlabThicknessMm=200;
        var notch=new List<Point3>{new Point3(0,0,0),new Point3(10,0,0),new Point3(10,5,0),
            new Point3(5,5,0),new Point3(5,10,0),new Point3(0,10,0)};
        var irregular=Frame(notch,B(2,7,2,4),B(2,4,4.2,6.4));
        var original=JsonConvert.SerializeObject(irregular);
        var partitioned=ZonePatchZoneBuilder.Build(irregular,0,cfg);
        AssertSolved(irregular,partitioned,cfg);
        Assert(partitioned.Count==2,"Concave slab requires two different anchored lengths");
        Assert(original==JsonConvert.SerializeObject(irregular),"Solver modified source geometry, FE demand or patch cells");
        // Тот же кейс с вертикальными стержнями и реверсом направления As1.
        var mirrored=Frame(notch.Select(p=>new Point3(p.Y,p.X,p.Z)).ToList(),B(2,4,2,7),B(4.2,6.4,2,4));
        cfg.ReverseZoneDirections=true;
        var vertical=ZonePatchZoneBuilder.Build(mirrored,0,cfg);
        AssertSolved(mirrored,vertical,cfg);
        Assert(vertical.Count==2 && vertical.All(z=>z.Direction==ZoneDirection.Y),"Reverse direction ignored in boundary solver");
        Console.WriteLine("PASS automatic stock placement, allowed diameter/step choices, concave repartition, exact gaps, X/Y, final repair and anchorage");
    }

    private static void TestEdgeForms()
    {
        var cfg=new AnalysisSettings{ConcreteClass="B30",BgBottomDiameterMm=8,BgBottomStepMm=200,MinZoneWidthM=0.3};
        var outline=Rect(0,10,0,10);
        for(var reverse=0;reverse<2;reverse++)
        {
            cfg.ReverseZoneDirections=reverse==1;
            foreach(var edge in new[]{0,1,2,3,4})
            {
                // Parallel, start, end, both ends, corner (parallel + terminating).
                var b=edge==0?B(2,3,0,0.3):edge==1?B(0,1,2,2.4):edge==2?B(9,10,2,2.4):
                    edge==3?B(0,10,2,2.4):B(0,1,0,0.3);
                if(reverse==1) b=B(b.MinY,b.MaxY,b.MinX,b.MaxX);
                var frame=Frame(outline,b);
                var input=JsonConvert.SerializeObject(frame);
                var zones=ZonePatchZoneBuilder.Build(frame,0,cfg);
                AssertSolved(frame,zones,cfg);
                Assert(input==JsonConvert.SerializeObject(frame),"Edge treatment changed raw FE/patch demand");
                Assert(zones.Count==1,"Simple edge case introduced unnecessary arrays");
                var zone=zones[0];
                var start=edge==1||edge==3||edge==4;var end=edge==2||edge==3;
                Assert(zone.BendAtStart==start && zone.BendAtEnd==end,"Parallel edge and terminating ends confused");
                Assert(zone.FamilyKind==(start&&end?ZoneFamilyKind.PEqual:start||end?ZoneFamilyKind.L:ZoneFamilyKind.Straight),
                    "Incorrect edge family");
                Assert(zone.BoundaryCheck.ActualMm>=49.99 && Math.Abs(zone.DesignEdgeCoverMm-50)<0.001,
                    "Edge steel is not 50 mm inside");
                Assert(Math.Abs(zone.RotationDeg-((reverse==1?90:0)+(start&&!end?180:0)))<0.001,
                    "Starting L bend faces the wrong direction");
                var copy=JsonConvert.DeserializeObject<AdditionalZone>(JsonConvert.SerializeObject(zone));
                Assert(copy.BendAtStart==start && copy.BendAtEnd==end && copy.DesignEdgeCoverMm==50,
                    "Saved edge form lost end/cover information");
                if(start||end) Assert(zone.VerticalLegMm>0 && zone.LengthMm>zone.LengthM*1000,
                    "Bent cut length does not include its vertical leg");
            }
        }
        // Old JSON has no automatic cover domain: it must retain its original coverage semantics.
        var physical=new LiraPlateElement{Contour=Rect(2,3,0,0.2)};
        var oldZone=new AdditionalZone{Layer=RebarLayer.As1,Direction=ZoneDirection.X,AsCoveredCm2PerM=3,
            BarStepMm=200,Contour=Rect(1,4,0.05,0.45)};
        Assert(!ZoneCoverageRules.CoversOrBridgesGap(new[]{oldZone},physical,RebarLayer.As1,3,outline,null),
            "Legacy data silently received a relaxed coverage domain");
        oldZone.DesignEdgeCoverMm=50;
        Assert(ZoneCoverageRules.CoversOrBridgesGap(new[]{oldZone},physical,RebarLayer.As1,3,outline,null),
            "Explicit 50 mm protective cover domain rejected");
        Console.WriteLine("PASS parallel/terminating edges, X/Y, both ends, corners, bend orientation/length, immutable source, old JSON");
    }

    private static void TestBuilder(double minY,double maxY,bool fits)
    {
        var cfg=new AnalysisSettings{ConcreteClass="B30",BgBottomDiameterMm=8,BgBottomStepMm=200,MinZoneWidthM=0.3};
        var patch=new ZonePatch{PatchId=1,Layer=RebarLayer.As1,Direction=ZoneDirection.X,
            MinXM=4,MaxXM=5,MinYM=minY,MaxYM=maxY,PeakAsAdditionalCm2PerM=3,
            ElementIds=new List<int>{1},Cells=new List<ZonePatchCell>{new ZonePatchCell{
                MinXM=4,MaxXM=5,MinYM=minY,MaxYM=maxY,AsAdditionalCm2PerM=3}}};
        var frame=new ZonePatchFrameSelection{MinXM=4,MaxXM=5,MinYM=minY,MaxYM=maxY,
            SlabOutline=Rect(0,20,0,10),Patches=new List<ZonePatch>{patch},
            BoundaryConstraints=new ZoneBoundaryConstraints(Rect(0,20,0,10),null,50),
            Elements=new List<ZonePatchFrameElement>{new ZonePatchFrameElement{
                ElementId=1,Layer=RebarLayer.As1,AsAdditionalCm2PerM=3,Contour=Rect(4,5,minY,maxY)}}};
        var source=JsonConvert.SerializeObject(frame.Patches);
        var zones=ZonePatchZoneBuilder.Build(frame,0,cfg,null,null);
        Assert(zones.Count>0,"Preflight silently removed a required zone");
        Assert(zones.All(item=>item.BoundaryCheck.Checked && item.BoundaryCheck.Allowed==fits),"Builder boundary result incorrect");
        Assert(zones.All(item=>item.LengthMm<=11700.001 && Math.Abs(item.WidthMm-400)<0.001),"Bar dimensions changed incorrectly");
        Assert(source==JsonConvert.SerializeObject(frame.Patches),"Calculated patch or As changed");
        Assert(zones.All(item=>item.Contour.Min(p=>p.Y)<=minY+0.000002 && item.Contour.Max(p=>p.Y)>=Math.Min(maxY,9.95)-0.000002),
            "Builder lost required footprint");
    }
}
