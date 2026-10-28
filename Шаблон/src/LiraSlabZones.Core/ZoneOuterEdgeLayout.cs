using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>Параллельная грань: отступ. Торец у грани: загиб внутри того же отступа.</summary>
    public static class ZoneOuterEdgeLayout
    {
        public static ZonePatchFrameSelection Prepare(ZonePatchFrameSelection source)
        {
            if(source.BoundaryConstraints?.HasOutline!=true) return source;
            var elements=new List<ZonePatchFrameElement>();
            foreach(var element in source.Elements)
            foreach(var contour in source.BoundaryConstraints.ClipDesignContour(element.Contour))
                elements.Add(new ZonePatchFrameElement {ElementId=element.ElementId,Layer=element.Layer,
                    AsAdditionalCm2PerM=element.AsAdditionalCm2PerM,Contour=contour});
            if(source.Elements.Count==0)
                foreach(var patch in source.Patches)
                foreach(var cell in patch.Cells)
                foreach(var contour in source.BoundaryConstraints.ClipDesignContour(Rect(
                    new ZonePatchFrameBounds(cell.MinXM,cell.MaxXM,cell.MinYM,cell.MaxYM),0)))
                    elements.Add(new ZonePatchFrameElement {ElementId=patch.ElementIds.FirstOrDefault(),Layer=patch.Layer,
                        AsAdditionalCm2PerM=cell.AsAdditionalCm2PerM,Contour=contour});
            var ids=new HashSet<(RebarLayer,int)>(elements.Select(e=>(e.Layer,e.ElementId)));
            return new ZonePatchFrameSelection {BoundaryConstraints=source.BoundaryConstraints,SlabOutline=source.SlabOutline,
                MinXM=source.MinXM,MaxXM=source.MaxXM,MinYM=source.MinYM,MaxYM=source.MaxYM,
                Elements=elements,Patches=source.Patches.Where(p=>p.ElementIds.Any(id=>ids.Contains((p.Layer,id)))).ToList()};
        }

        public static bool Adapt(AdditionalZone zone,ZonePatchFrameBounds support,AnalysisSettings settings,
            ZoneBoundaryConstraints constraints)
        {
            zone.DesignEdgeCoverMm=constraints.EdgeOffsetMm;
            var b=ZoneBoundaryConstraints.Bounds(zone.Contour);
            var anchor=RebarTables.AnchorageLenMm(zone.ConcreteClass,zone.DiameterMm)/1000.0;
            var from=zone.Direction==ZoneDirection.X?b.MinX:b.MinY;
            var to=zone.Direction==ZoneDirection.X?b.MaxX:b.MaxY;
            var coreMin=Math.Max(zone.Direction==ZoneDirection.X?support.MinX:support.MinY,from+anchor);
            var coreMax=Math.Min(zone.Direction==ZoneDirection.X?support.MaxX:support.MaxY,to-anchor);
            if(coreMax<=coreMin) return false;
            var coreBand=zone.Direction==ZoneDirection.X
                ?new ZonePatchFrameBounds(coreMin,coreMax,b.MinY,b.MaxY)
                :new ZonePatchFrameBounds(b.MinX,b.MaxX,coreMin,coreMax);
            var required=zone.Direction==ZoneDirection.X
                ?new ZonePatchFrameBounds(coreMin,coreMax,support.MinY,support.MaxY)
                :new ZonePatchFrameBounds(support.MinX,support.MaxX,coreMin,coreMax);
            if(!constraints.TryFit(coreBand,required,zone.Direction,out var fitted,out var report) || report.CenteredException) return false;
            // Если все торцы с запасом внутри, пересечение с гранью искать не требуется.
            var placed=zone.Direction==ZoneDirection.X
                ?new ZonePatchFrameBounds(from,to,fitted.MinY,fitted.MaxY)
                :new ZonePatchFrameBounds(fitted.MinX,fitted.MaxX,from,to);
            var checkedPlaced=constraints.Check(placed,zone.Direction);
            double min,max;
            if(checkedPlaced.Allowed && !checkedPlaced.CenteredException && checkedPlaced.ActualMm>constraints.EdgeOffsetMm+0.1)
            {min=from-1;max=to+1;}
            else if(!constraints.TryAxialLimits(fitted,zone.Direction,out min,out max)) return false;
            var bendStart=from<=min+0.00001;
            var bendEnd=to>=max-0.00001;
            from=Math.Max(from,min);to=Math.Min(to,max);
            if(from>coreMin+0.000002 || to<coreMax-0.000002) return false;
            placed=zone.Direction==ZoneDirection.X?new ZonePatchFrameBounds(from,to,fitted.MinY,fitted.MaxY)
                :new ZonePatchFrameBounds(fitted.MinX,fitted.MaxX,from,to);
            var contour=Rect(placed,zone.LevelZM);
            var check=constraints.Check(contour,zone.Direction);
            if(!check.Allowed || check.CenteredException) return false;
            var family=ZoneFamilyKind.Straight;
            var leg=0.0;
            if(bendStart || bendEnd)
            {
                leg=HoleBentRules.VerticalLegAvailableMm(settings,zone.Layer,zone.DiameterMm);
                var needed=RebarTables.MandrelDiamMm(zone.DiameterMm)*(bendStart&&bendEnd?2:1);
                if(leg+1e-6<needed)
                {
                    zone.IsValid=false;
                    zone.Comment+=$"; загиб не помещается по высоте: {leg:0.#} < {needed:0.#} мм";
                    return false;
                }
                family=bendStart&&bendEnd?ZoneFamilyKind.PEqual:ZoneFamilyKind.L;
            }
            var planMm=(to-from)*1000;
            var total=family==ZoneFamilyKind.Straight?planMm:RebarTables.BentBarTotalLengthMm(planMm,leg,family);
            if(total>11700.001)
            {
                zone.IsValid=false;
                zone.Comment+="; полная длина с загибом превышает 11700 мм: требуется дополнительный стык";
                return false;
            }
            zone.Contour=contour;
            zone.Placement=new Point3((placed.MinX+placed.MaxX)/2,(placed.MinY+placed.MaxY)/2,zone.LevelZM);
            zone.LengthM=planMm/1000;
            zone.LengthMm=total;
            zone.FamilyKind=family;
            zone.FamilyFileName=settings.GetFamilyName(family);
            zone.VerticalLegMm=leg;
            zone.BendAtStart=bendStart;zone.BendAtEnd=bendEnd;
            zone.RotationDeg=(zone.Direction==ZoneDirection.Y?90:0)+(bendStart&&!bendEnd?180:0);
            zone.BoundaryCheck=check;
            zone.Comment=string.Join("; ",zone.Comment.Split(';').Select(s=>s.Trim()).Where(s=>s.Length>0 &&
                !s.Contains("анкеровка выходит") && !s.Contains("эталонная длина") && !s.Contains("сдвиг невозможен") &&
                !s.Contains("требуется переразбивка") && !s.Contains("Зона выходит") && !s.Contains("Отступ от грани")));
            if(family!=ZoneFamilyKind.Straight)
            {
                zone.CountBars=true;zone.CountInSpec=false;
                zone.Comment+=$"; торец: загиб {(bendStart&&bendEnd?"с двух сторон":bendStart?"в начале":"в конце")}, отступ {constraints.EdgeOffsetMm:0.#} мм";
            }
            zone.StatusColor=zone.IsValid?"ok":"warn";
            return true;
        }

        private static List<Point3> Rect(ZonePatchFrameBounds b,double z)=>new List<Point3>{new Point3(b.MinX,b.MinY,z),
            new Point3(b.MaxX,b.MinY,z),new Point3(b.MaxX,b.MaxY,z),new Point3(b.MinX,b.MaxY,z)};
    }
}
