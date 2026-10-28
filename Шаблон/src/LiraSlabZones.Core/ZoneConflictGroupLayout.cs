using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace LiraSlabZones.Core
{
    public sealed class ZoneConflictGroupResult
    {
        public int PairsBefore { get; set; }
        public int PairsAfter { get; set; }
        public int GroupsResolved { get; set; }
        public int BoundaryBefore { get; set; }
        public int BoundaryAfter { get; set; }
        public bool BoundaryChecked { get; set; }
        public Dictionary<string,Dictionary<string,int>> Rejections { get; set; } = new();
        public List<List<int>> UnresolvedZoneIds { get; set; } = new();
        public List<int> InvalidZoneIds { get; set; } = new();
        public bool Solved => BoundaryChecked && PairsAfter == 0 && BoundaryAfter == 0 && InvalidZoneIds.Count == 0;
    }

    /// <summary>Заменяет конфликтующую группу целиком; частичная коллизионная замена не принимается.</summary>
    public static class ZoneConflictGroupLayout
    {
        public static ZoneConflictGroupResult Resolve(IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            AnalysisSettings settings, IList<Point3>? outline, IList<OpeningInfo>? openings,
            ZoneBoundaryConstraints constraints, IList<AlignAxis>? axes = null)
        {
            var result=new ZoneConflictGroupResult {PairsBefore=ZoneBoundaryLayoutSolver.ConflictCount(zones,settings),
                BoundaryChecked=constraints.HasOutline,BoundaryBefore=zones.Count(z=>!constraints.Check(z.Contour,z.Direction).Allowed)};
            var previous=result.PairsBefore;
            // Позднее исправление соседней группы может разблокировать ранее отклонённую.
            // Повторяем автоматически, пока число пар строго уменьшается.
            for(var pass=0;pass<result.PairsBefore && previous>0;pass++)
            {
                var step=ResolveCore(zones,plates,settings,outline,openings,constraints,axes,null,0);
                result.GroupsResolved+=step.GroupsResolved;
                foreach(var rejection in step.Rejections) result.Rejections[rejection.Key]=rejection.Value;
                if(step.PairsAfter>=previous) break;
                previous=step.PairsAfter;
            }
            if(result.GroupsResolved>0)
            {
                var compact=constraints.Snapshot(zones);
                ZoneEditor.RemoveZonesWithoutRequiredElements(compact,plates,settings,outline,openings);
                if(ZoneBoundaryLayoutSolver.PreservesRequiredLayout(zones,compact,
                    plates,settings,outline,openings,constraints,out _))
                {
                    zones.Clear();
                    foreach(var zone in compact) zones.Add(zone);
                }
                for(var i=0;i<zones.Count;i++) zones[i].ZoneId=i+1;
                constraints.CheckZones(zones);
            }
            result.PairsAfter=ZoneBoundaryLayoutSolver.ConflictCount(zones,settings);
            result.BoundaryAfter=zones.Count(z=>!constraints.Check(z.Contour,z.Direction).Allowed);
            result.UnresolvedZoneIds=Components(zones,settings).Select(g=>g.Select(z=>z.ZoneId).ToList()).ToList();
            result.InvalidZoneIds=zones.Where(z=>!z.IsValid || RebarTables.ExceedsMaxBarLength(z)).Select(z=>z.ZoneId).ToList();
            return result;
        }

        private static ZoneConflictGroupResult ResolveCore(IList<AdditionalZone> zones,IList<LiraPlateElement> plates,
            AnalysisSettings settings,IList<Point3>? outline,IList<OpeningInfo>? openings,
            ZoneBoundaryConstraints constraints,IList<AlignAxis>? axes,List<List<AdditionalZone>>? requested,int depth)
        {
            var result = new ZoneConflictGroupResult { PairsBefore=ZoneBoundaryLayoutSolver.ConflictCount(zones,settings),
                BoundaryChecked=constraints.HasOutline, BoundaryBefore=zones.Count(z=>!constraints.Check(z.Contour,z.Direction).Allowed) };
            result.BoundaryAfter=result.BoundaryBefore;
            if (result.PairsBefore==0 || outline==null || !constraints.HasOutline || string.IsNullOrWhiteSpace(settings.ConcreteClass))
            { result.PairsAfter=result.PairsBefore; return result; }
            settings.SyncBackgroundAsFromBars();
            var groups=requested??Components(zones,settings);
            foreach(var group in groups)
            {
                if(group.Any(z=>!zones.Contains(z))) continue;
                if(ZoneBoundaryLayoutSolver.ConflictCount(group,settings)==0) {result.GroupsResolved++;continue;}
                if(group.Any(z=>z.FamilyKind!=ZoneFamilyKind.Straight && !z.BendAtStart && !z.BendAtEnd))
                { result.UnresolvedZoneIds.Add(group.Select(z=>z.ZoneId).ToList()); continue; }
                var layer=group[0].Layer;
                var direction=group[0].Direction;
                var allLayer=zones.Where(z=>z.Layer==layer).ToList();
                var fixedZones=allLayer.Where(z=>!group.Contains(z)).ToList();
                var bg=Background(settings,layer);
                var targets=plates.Where(p=>p.Rebar.Ok && p.Contour.Count>=3 &&
                    p.Rebar.Get(layer)-bg>MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                    ZoneCoverageRules.CoversOrBridgesGap(allLayer,p,layer,p.Rebar.Get(layer)-bg,outline,openings,true) &&
                    !ZoneCoverageRules.CoversOrBridgesGap(fixedZones,p,layer,p.Rebar.Get(layer)-bg,outline,openings,true)).ToList();
                var accepted=false;
                var rejections=new Dictionary<string,int>();
                var attempts=0;
                var haloComponents=Components(allLayer,settings);
                List<AdditionalZone>? halo=null;
                var haloCost=int.MaxValue;
                void RememberHalo(IEnumerable<int> indices)
                {
                    var next=indices.Distinct().Select(i=>fixedZones[i]).ToList();
                    foreach(var connected in haloComponents)
                        if(connected.Any(next.Contains)) next.AddRange(connected);
                    next=next.Distinct().Where(z=>!group.Contains(z)).ToList();
                    if(next.Count==0 || next.Count>6 || group.Count+next.Count>24) return;
                    var cost=next.Count+100*next.Count(z=>!constraints.Check(z.Contour,z.Direction).Allowed);
                    if(cost<haloCost) {halo=next;haloCost=cost;}
                }
                bool Reject(string reason) {rejections[reason]=rejections.TryGetValue(reason,out var n)?n+1:1;return false;}
                bool TryPlan(List<AdditionalZone> replacement)
                {
                    if(replacement.Any(z=>!z.IsValid)) return Reject("Capacity");
                    if(replacement.Any(z=>!constraints.Check(z.Contour,z.Direction).Allowed)) return Reject("Boundary");
                    var adjust=attempts++<4;
                    var proposal=adjust || axes?.Count>0 ? constraints.Snapshot(fixedZones.Concat(replacement))
                        :fixedZones.Concat(replacement).ToList();
                    // Соседям разрешено совместно уступить запас габарита; принятие остаётся атомарным.
                    if(adjust) AdjustNeighbors(proposal,fixedZones.Count,plates,settings,outline,openings);
                    if(axes?.Count>0)
                    {
                        var oldTie=RevitGridAlignment.Apply(constraints.Snapshot(allLayer),axes,outline,openings,settings.EdgeOffsetMm);
                        var newTie=RevitGridAlignment.Apply(proposal,axes,outline,openings,settings.EdgeOffsetMm);
                        if(newTie.UntiedZones>oldTie.UntiedZones) return Reject("AxisTie");
                    }
                    if(proposal.Skip(fixedZones.Count).Any(z=>!constraints.Check(z.Contour,z.Direction).Allowed)) return Reject("Boundary");
                    // Проверяется каждая новая пара, включая соседей за пределами группы.
                    var blockers=new HashSet<int>();
                    for(var i=0;i<proposal.Count;i++)
                    for(var j=Math.Max(i+1,fixedZones.Count);j<proposal.Count;j++)
                        if(ZoneEditor.HasPlacementConflict(proposal[i],proposal[j]))
                        {
                            if(i>=fixedZones.Count) return Reject("InternalOverlap");
                            blockers.Add(i);
                        }
                    if(blockers.Count>0) {RememberHalo(blockers);return Reject("Overlap");}
                    for(var i=0;i<fixedZones.Count;i++)
                    for(var j=i+1;j<fixedZones.Count;j++)
                        if(!ZoneEditor.HasPlacementConflict(fixedZones[i],fixedZones[j]) &&
                            ZoneEditor.HasPlacementConflict(proposal[i],proposal[j]))
                        {RememberHalo(new[]{i,j});return Reject("NeighborOverlap");}
                    var badSpacing=ZoneLayoutDiagnostics.AddInterZoneStepIssues(proposal,new ZoneDiagnostics(),settings.GridCellMm/1000.0)
                        .Where(pair=>pair.First>=fixedZones.Count || pair.Second>=fixedZones.Count).ToList();
                    if(badSpacing.Count>0)
                    {RememberHalo(badSpacing.SelectMany(p=>new[]{p.First,p.Second}).Where(i=>i<fixedZones.Count));return Reject("Spacing");}
                    if(ZoneBoundaryLayoutSolver.ConflictCount(proposal,settings)>=ZoneBoundaryLayoutSolver.ConflictCount(allLayer,settings)) return Reject("NoProgress");
                    if(!ZoneBoundaryLayoutSolver.PreservesRequiredLayout(allLayer,proposal,plates,settings,outline,openings,constraints,out var reason)) return Reject(reason);
                    foreach(var original in group) zones.Remove(original);
                    foreach(var zone in proposal.Skip(fixedZones.Count))
                    {
                        zone.Comment+="; конфликтующая группа переразложена без коллизий";
                        zones.Add(zone);
                    }
                    // Привязка может сдвинуть и фиксированного соседа: переносим проверенное состояние.
                    for(var i=0;i<fixedZones.Count;i++)
                    {
                        if(!ReferenceEquals(proposal[i],fixedZones[i]))
                            JsonConvert.PopulateObject(JsonConvert.SerializeObject(proposal[i]),fixedZones[i],
                                new JsonSerializerSettings {ObjectCreationHandling=ObjectCreationHandling.Replace});
                        fixedZones[i].BoundaryConstraints=constraints;
                    }
                    return true;
                }
                bool TryPositions(List<AdditionalZone> replacement)
                {
                    if(TryPlan(replacement)) return true;
                    var shifts=new HashSet<(double X,double Y)>();
                    foreach(var moving in replacement)
                    foreach(var neighbor in fixedZones)
                    {
                        var a=ZoneBoundaryConstraints.Bounds(moving.Contour);
                        var b=ZoneBoundaryConstraints.Bounds(neighbor.Contour);
                        var gap=Math.Min(moving.BarStepMm,neighbor.BarStepMm)/1000.0;
                        if(moving.Direction==ZoneDirection.X)
                        {
                            shifts.Add((0,b.MinY-gap-a.MaxY));shifts.Add((0,b.MaxY+gap-a.MinY));
                            shifts.Add((b.MinX-a.MaxX,0));shifts.Add((b.MaxX-a.MinX,0));
                        }
                        else
                        {
                            shifts.Add((b.MinX-gap-a.MaxX,0));shifts.Add((b.MaxX+gap-a.MinX,0));
                            shifts.Add((0,b.MinY-a.MaxY));shifts.Add((0,b.MaxY-a.MinY));
                        }
                    }
                    foreach(var shift in shifts.Where(s=>Math.Abs(s.X)<=3 && Math.Abs(s.Y)<=3)
                        .OrderBy(s=>Math.Abs(s.X)+Math.Abs(s.Y)).Take(96))
                    {
                        var moved=constraints.Snapshot(replacement);
                        if(moved.All(z=>ZoneEditor.Move(z,shift.X,shift.Y,outline,false)) && TryPlan(moved)) return true;
                    }
                    return false;
                }
                if(targets.Count==0) accepted=TryPlan(new List<AdditionalZone>());
                else
                {
                    foreach(var variant in Variants(settings))
                    {
                        var frame=Frame(targets,layer,direction,bg,outline,constraints);
                        var merged=ZoneBoundaryLayoutSolver.Build(frame,group[0].LevelZM,variant);
                        if(merged.Count>0 && TryPositions(merged)) { accepted=true; break; }
                        foreach(var stripPlan in Strips(targets,layer,direction,bg,group[0].LevelZM,variant,outline,constraints))
                            if(TryPlan(stripPlan)) { accepted=true; break; }
                        if(accepted) break;
                    }
                }
                if(!accepted && halo!=null && depth<2)
                {
                    var expanded=group.Concat(halo).Distinct().ToList();
                    var nested=ResolveCore(zones,plates,settings,outline,openings,constraints,axes,
                        new List<List<AdditionalZone>>{expanded},depth+1);
                    accepted=nested.GroupsResolved>0;
                }
                if(accepted) result.GroupsResolved++;
                else result.UnresolvedZoneIds.Add(group.Select(z=>z.ZoneId).ToList());
                result.Rejections[string.Join(",",group.Select(z=>z.ZoneId))]=rejections;
            }
            result.PairsAfter=ZoneBoundaryLayoutSolver.ConflictCount(zones,settings);
            result.BoundaryAfter=zones.Count(z=>!constraints.Check(z.Contour,z.Direction).Allowed);
            return result;
        }

        private static List<List<AdditionalZone>> Components(IList<AdditionalZone> zones, AnalysisSettings settings)
        {
            var edges=ZoneLayoutDiagnostics.AddInterZoneStepIssues(zones,new ZoneDiagnostics(),settings.GridCellMm/1000.0);
            for(var i=0;i<zones.Count;i++)
            for(var j=i+1;j<zones.Count;j++)
                if(ZoneEditor.HasPlacementConflict(zones[i],zones[j]) || ZoneEditor.IsAllowedLongitudinalLapOverlap(zones[i],zones[j])) edges.Add((i,j));
            var seen=new HashSet<int>();
            var result=new List<List<AdditionalZone>>();
            for(var i=0;i<zones.Count;i++)
            {
                if(!seen.Add(i)) continue;
                var pending=new List<int>{i};
                for(var q=0;q<pending.Count;q++)
                foreach(var edge in edges.Where(e=>e.First==pending[q] || e.Second==pending[q]))
                {
                    var other=edge.First==pending[q]?edge.Second:edge.First;
                    if(seen.Add(other)) pending.Add(other);
                }
                if(pending.Count<2) continue;
                var group=pending.Select(index=>zones[index]).ToList();
                if(ZoneBoundaryLayoutSolver.ConflictCount(group,settings)>0) result.Add(group);
            }
            return result;
        }

        private static void AdjustNeighbors(List<AdditionalZone> proposal,int fixedCount,IList<LiraPlateElement> plates,
            AnalysisSettings settings,IList<Point3> outline,IList<OpeningInfo>? openings)
        {
            var active=new HashSet<int>(Enumerable.Range(fixedCount,proposal.Count-fixedCount));
            var pairs=ZoneLayoutDiagnostics.AddInterZoneStepIssues(proposal,new ZoneDiagnostics(),settings.GridCellMm/1000.0);
            for(var i=0;i<proposal.Count;i++)
            for(var j=i+1;j<proposal.Count;j++)
                if(ZoneEditor.HasPlacementConflict(proposal[i],proposal[j])) pairs.Add((i,j));
            for(var pass=0;pass<2;pass++)
            {
                var neighbors=pairs.Where(p=>active.Contains(p.First)||active.Contains(p.Second))
                    .SelectMany(p=>new[]{p.First,p.Second}).Distinct().ToList();
                foreach(var i in neighbors) active.Add(i);
            }
            if(active.Count>24) return;
            var local=active.OrderBy(i=>i).Select(i=>proposal[i]).ToList();
            ZoneEditor.EnforceRequiredGaps(local,plates,settings,outline,openings);
        }

        private static IEnumerable<AnalysisSettings> Variants(AnalysisSettings settings)
        {
            yield return settings;
            var steps=(settings.AllowedAdditionalBarStepsMm??new List<int>{100,200}).Where(s=>s==100||s==200).Distinct().ToList();
            if(steps.Count<2) yield break;
            foreach(var step in steps.OrderBy(s=>s))
            {
                var copy=JsonConvert.DeserializeObject<AnalysisSettings>(JsonConvert.SerializeObject(settings))!;
                copy.AllowedAdditionalBarStepsMm=new List<int>{step};
                yield return copy;
            }
        }

        private static IEnumerable<List<AdditionalZone>> Strips(List<LiraPlateElement> plates, RebarLayer layer,
            ZoneDirection direction,double bg,double level,AnalysisSettings settings,IList<Point3> outline,
            ZoneBoundaryConstraints constraints)
        {
            var bounds=plates.ToDictionary(p=>p,p=>ZoneBoundaryConstraints.Bounds(p.Contour));
            var min=direction==ZoneDirection.X?bounds.Values.Min(b=>b.MinY):bounds.Values.Min(b=>b.MinX);
            var max=direction==ZoneDirection.X?bounds.Values.Max(b=>b.MaxY):bounds.Values.Max(b=>b.MaxX);
            foreach(var step in (settings.AllowedAdditionalBarStepsMm??new List<int>{100,200}).Where(s=>s==100||s==200).Distinct())
            for(var extra=0;extra<3;extra++)
            {
                var width=(Math.Ceiling(settings.EffectiveMinZoneWidthM*1000/step)+extra)*step/1000.0;
                width=Math.Max(width,step/1000.0);
                var gap=step/1000.0;
                var variant=JsonConvert.DeserializeObject<AnalysisSettings>(JsonConvert.SerializeObject(settings))!;
                variant.AllowedAdditionalBarStepsMm=new List<int>{step};
                for(var phase=0;phase<=8;phase++)
                {
                    var start=min-width*phase/8;
                    var plan=new List<AdditionalZone>();
                    var valid=true;
                    for(var cross=start;cross<max-1e-6;cross+=width+gap)
                    {
                        var nearby=plates.Where(p=>direction==ZoneDirection.X
                            ? bounds[p].MinY<cross+width+gap && bounds[p].MaxY>cross-gap
                            : bounds[p].MinX<cross+width+gap && bounds[p].MaxX>cross-gap).ToList();
                        if(nearby.Count==0) continue;
                        var axialMin=direction==ZoneDirection.X?nearby.Min(p=>bounds[p].MinX):nearby.Min(p=>bounds[p].MinY);
                        var axialMax=direction==ZoneDirection.X?nearby.Max(p=>bounds[p].MaxX):nearby.Max(p=>bounds[p].MaxY);
                        var b=direction==ZoneDirection.X?new ZonePatchFrameBounds(axialMin,axialMax,cross,cross+width)
                            :new ZonePatchFrameBounds(cross,cross+width,axialMin,axialMax);
                        var proxy=new LiraPlateElement { Id=nearby[0].Id,Contour=Rect(b,level),
                            Rebar=new PlateReinforcement {Ok=true} };
                        var demand=nearby.Max(p=>p.Rebar.Get(layer));
                        switch(layer){case RebarLayer.As1:proxy.Rebar.As1=demand;break;case RebarLayer.As2:proxy.Rebar.As2=demand;break;
                            case RebarLayer.As3:proxy.Rebar.As3=demand;break;default:proxy.Rebar.As4=demand;break;}
                        var frame=Frame(new List<LiraPlateElement>{proxy},layer,direction,bg,outline,constraints);
                        var rows=ZonePatchZoneBuilder.BuildCandidate(frame,level,variant,null,null);
                        foreach(var zone in rows) zone.NodeIds=nearby.Select(p=>p.Id).ToList();
                        if(rows.Count==0 || rows.Any(z=>!z.IsValid || !constraints.Check(z.Contour,z.Direction).Allowed)) {valid=false;break;}
                        plan.AddRange(rows);
                    }
                    if(valid && plan.Count>0) yield return plan;
                }
            }
        }

        private static ZonePatchFrameSelection Frame(List<LiraPlateElement> plates,RebarLayer layer,ZoneDirection direction,
            double bg,IList<Point3> outline,ZoneBoundaryConstraints constraints)
        {
            var b=ZoneBoundaryConstraints.Bounds(plates.SelectMany(p=>p.Contour).ToList());
            var frame=new ZonePatchFrameSelection {SlabOutline=outline.ToList(),BoundaryConstraints=constraints,
                MinXM=b.MinX,MaxXM=b.MaxX,MinYM=b.MinY,MaxYM=b.MaxY};
            foreach(var plate in plates)
            {
                var p=ZoneBoundaryConstraints.Bounds(plate.Contour);
                var demand=plate.Rebar.Get(layer)-bg;
                frame.Elements.Add(new ZonePatchFrameElement {ElementId=plate.Id,Layer=layer,Contour=plate.Contour,AsAdditionalCm2PerM=demand});
                frame.Patches.Add(new ZonePatch {PatchId=plate.Id,Layer=layer,Direction=direction,MinXM=p.MinX,MaxXM=p.MaxX,MinYM=p.MinY,MaxYM=p.MaxY,
                    PeakAsAdditionalCm2PerM=demand,ElementIds=new List<int>{plate.Id},Cells=new List<ZonePatchCell>{new ZonePatchCell {
                        MinXM=p.MinX,MaxXM=p.MaxX,MinYM=p.MinY,MaxYM=p.MaxY,AsAdditionalCm2PerM=demand}}});
            }
            return frame;
        }

        private static double Background(AnalysisSettings s,RebarLayer layer)=>layer switch {
            RebarLayer.As1=>s.AsMainAs1,RebarLayer.As2=>s.AsMainAs2,RebarLayer.As3=>s.AsMainAs3,_=>s.AsMainAs4};
        private static List<Point3> Rect(ZonePatchFrameBounds b,double z)=>new List<Point3>{new Point3(b.MinX,b.MinY,z),
            new Point3(b.MaxX,b.MinY,z),new Point3(b.MaxX,b.MaxY,z),new Point3(b.MinX,b.MaxY,z)};
    }
}
