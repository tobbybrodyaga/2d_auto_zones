using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace LiraSlabZones.Core
{
    /// <summary>Убирает пустые продольные пробеги и объединяет непрерывные одинаковые ряды.</summary>
    public static class ZoneLayoutCompactness
    {
        private const double Eps = 0.000002;

        /// <summary>Отдельные участки по оси стержней; промежуток задаётся с учётом двух анкеровок.</summary>
        public static List<List<LiraPlateElement>> AxialComponents(IEnumerable<LiraPlateElement> plates,
            ZoneDirection direction, double maximumSupportGapM)
        {
            var result = new List<List<LiraPlateElement>>();
            double end = double.NegativeInfinity;
            var projected = plates.Select(p => new { Plate=p, Bounds=ZoneBoundaryConstraints.Bounds(p.Contour) })
                .OrderBy(p => direction==ZoneDirection.X ? p.Bounds.MinX : p.Bounds.MinY);
            foreach (var p in projected)
            {
                var min=direction==ZoneDirection.X?p.Bounds.MinX:p.Bounds.MinY;
                var max=direction==ZoneDirection.X?p.Bounds.MaxX:p.Bounds.MaxY;
                if(result.Count==0 || min>end+maximumSupportGapM+Eps)
                { result.Add(new List<LiraPlateElement>());end=max; }
                result[result.Count-1].Add(p.Plate);end=Math.Max(end,max);
            }
            return result;
        }

        public static int Refine(IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            AnalysisSettings settings, IList<Point3>? outline, IList<OpeningInfo>? openings,
            ZoneBoundaryConstraints constraints, Action<string, int, int>? progress = null)
        {
            if (!constraints.HasOutline || string.IsNullOrWhiteSpace(settings.ConcreteClass)) return 0;
            var baseline = constraints.Snapshot(zones);
            var bounds = plates.Where(p => p.Contour.Count >= 3).ToDictionary(p => p, p => ZoneBoundaryConstraints.Bounds(p.Contour));
            var changed = SplitWideBands(zones, plates, settings, outline, openings, constraints, bounds);
            changed += MergeSplicedRows(zones, settings, constraints);
            var originals = zones.ToList();
            for (var index = 0; index < originals.Count; index++)
            {
                progress?.Invoke("Длины и свободные промежутки", index + 1, originals.Count);
                var source = originals[index];
                if (!zones.Contains(source) || !source.IsValid || source.LengthM < 2) continue;
                var b = ZoneBoundaryConstraints.Bounds(source.Contour);
                // Стержень может покрывать КЭ в разрешённом промежутке между рядами.
                // Такие КЭ тоже входят в контроль изменения длины, даже не касаясь рамки.
                var margin = Math.Max(source.BarStepMm, settings.GridCellMm) / 1000.0 + Eps;
                var background = Background(settings, source.Layer);
                var nearby = bounds.Where(p => p.Key.Rebar.Ok && p.Key.Rebar.Get(source.Layer) - background > MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                    p.Value.MaxX >= b.MinX - margin && p.Value.MinX <= b.MaxX + margin && p.Value.MaxY >= b.MinY - margin && p.Value.MinY <= b.MaxY + margin)
                    .Select(p => p.Key).ToList();
                var intervals = new List<(double Min, double Max)>();
                foreach (var plate in nearby)
                foreach (var polygon in constraints.ClipDesignContour(plate.Contour))
                {
                    var p = ZoneBoundaryConstraints.Bounds(polygon);
                    if (p.MaxX <= b.MinX || p.MinX >= b.MaxX || p.MaxY <= b.MinY || p.MinY >= b.MaxY) continue;
                    intervals.Add(source.Direction == ZoneDirection.X
                        ? (Math.Max(b.MinX, p.MinX), Math.Min(b.MaxX, p.MaxX))
                        : (Math.Max(b.MinY, p.MinY), Math.Min(b.MaxY, p.MaxY)));
                }
                if (intervals.Count == 0) continue;
                var anchor = RebarTables.AnchorageLenMm(source.ConcreteClass, source.DiameterMm) / 1000.0;
                var clusters = new List<(double Min, double Max)>();
                foreach (var span in intervals.OrderBy(s => s.Min))
                {
                    if (clusters.Count == 0 || span.Min > clusters[clusters.Count - 1].Max + 2 * anchor + 0.05)
                        clusters.Add(span);
                    else
                    {
                        var last = clusters[clusters.Count - 1];
                        clusters[clusters.Count - 1] = (last.Min, Math.Max(last.Max, span.Max));
                    }
                }
                var parts = new List<AdditionalZone>();
                var oldMin = source.Direction == ZoneDirection.X ? b.MinX : b.MinY;
                var oldMax = source.Direction == ZoneDirection.X ? b.MaxX : b.MaxY;
                foreach (var span in clusters)
                {
                    var min = Math.Max(oldMin, span.Min - anchor);
                    var max = Math.Min(oldMax, span.Max + anchor);
                    var length = RebarTables.Sum3FamilyLengthsMm.Where(l => l / 1000.0 >= max - min - Eps && l / 1000.0 <= oldMax - oldMin + Eps)
                        .Select(l => l / 1000.0).DefaultIfEmpty(oldMax - oldMin).Min();
                    var start = Math.Max(oldMin, Math.Min((min + max - length) / 2, oldMax - length));
                    if (start > min + Eps || start + length < max - Eps) { parts.Clear(); break; }
                    var part = Clone(source);
                    part.BendAtStart = part.BendAtEnd = false;
                    part.FamilyKind = ZoneFamilyKind.Straight;
                    part.VerticalLegMm = 0;
                    SetBounds(part, source.Direction == ZoneDirection.X
                        ? new ZonePatchFrameBounds(start, start + length, b.MinY, b.MaxY)
                        : new ZonePatchFrameBounds(b.MinX, b.MaxX, start, start + length));
                    var support = source.Direction == ZoneDirection.X ? new ZonePatchFrameBounds(span.Min, span.Max, b.MinY, b.MaxY)
                        : new ZonePatchFrameBounds(b.MinX, b.MaxX, span.Min, span.Max);
                    if (!ZoneOuterEdgeLayout.Adapt(part, support, settings, constraints) || !part.IsValid) { parts.Clear(); break; }
                    if (part.BarCount != source.BarCount) { parts.Clear(); break; }
                    if (part.FamilyKind == ZoneFamilyKind.Straight) { part.CountInSpec = true; part.CountBars = false; }
                    parts.Add(part);
                }
                if (parts.Count == 0 || parts.Sum(p => p.LengthMm * p.BarCount) >= source.LengthMm * source.BarCount - 50) continue;
                if (parts.Any(p => RebarTables.ExceedsMaxBarLength(p))) continue;
                var proposal = zones.Where(z => !ReferenceEquals(z, source)).Concat(parts).ToList();
                if (ZoneBoundaryLayoutSolver.ConflictCount(proposal, settings) > ZoneBoundaryLayoutSolver.ConflictCount(zones, settings)) continue;
                if (!ZoneBoundaryLayoutSolver.PreservesRequiredLayout(zones, proposal, nearby, settings, outline, openings, constraints, out _)) continue;
                zones.Remove(source);
                foreach (var part in parts) { part.Comment += "; сокращён свободный продольный пробег с сохранением покрытия"; zones.Add(part); }
                changed++;
            }
            changed += MergeRows(zones, settings, constraints);
            changed += MergeSplicedRows(zones, settings, constraints);
            if (changed > 0)
            {
                var compact = constraints.Snapshot(zones);
                ZoneEditor.RemoveZonesWithoutRequiredElements(compact, plates, settings, outline, openings);
                if (ZoneBoundaryLayoutSolver.PreservesRequiredLayout(zones, compact, plates, settings, outline, openings, constraints, out _))
                { zones.Clear(); foreach (var zone in compact) zones.Add(zone); }
                if (!ZoneBoundaryLayoutSolver.PreservesRequiredLayout(baseline, zones, plates, settings, outline, openings, constraints, out _))
                { zones.Clear(); foreach (var zone in baseline) zones.Add(zone); return 0; }
                for (var i = 0; i < zones.Count; i++) zones[i].ZoneId = i + 1;
                constraints.CheckZones(zones);
            }
            return changed;
        }

        private static int SplitWideBands(IList<AdditionalZone> zones, IList<LiraPlateElement> plates, AnalysisSettings settings,
            IList<Point3>? outline, IList<OpeningInfo>? openings, ZoneBoundaryConstraints constraints,
            Dictionary<LiraPlateElement,ZonePatchFrameBounds> bounds)
        {
            var changed=0;
            var seen=new HashSet<AdditionalZone>();
            foreach(var source in zones.ToList())
            {
                if(!seen.Add(source) || !source.IsValid || source.BarStepMm<=0) continue;
                var chain=string.IsNullOrEmpty(source.SpliceGroupId) ? new List<AdditionalZone>{source} :
                    zones.Where(z=>z.SpliceGroupId==source.SpliceGroupId).OrderBy(z=>z.SplicePartIndex).ToList();
                foreach(var part in chain) seen.Add(part);
                if(chain.Any(z=>!z.IsValid || z.DiameterMm!=source.DiameterMm || z.BarStepMm!=source.BarStepMm)) continue;
                if(chain.Count>1 && (chain.Count!=source.SplicePartCount || chain.Select(z=>z.SplicePartIndex).Distinct().Count()!=chain.Count)) continue;
                var b=ZoneBoundaryConstraints.Bounds(source.Contour);
                var crossMin=source.Direction==ZoneDirection.X?b.MinY:b.MinX;
                var crossMax=source.Direction==ZoneDirection.X?b.MaxY:b.MaxX;
                if(chain.Any(z=> {var q=ZoneBoundaryConstraints.Bounds(z.Contour);return Math.Abs((z.Direction==ZoneDirection.X?q.MinY:q.MinX)-crossMin)>Eps ||
                    Math.Abs((z.Direction==ZoneDirection.X?q.MaxY:q.MaxX)-crossMax)>Eps;})) continue;
                var step=source.BarStepMm/1000.0;
                var total=(int)Math.Round((crossMax-crossMin)/step)+1;
                var minimum=(int)Math.Ceiling(settings.EffectiveMinZoneWidthM/step-1e-6)+1;
                minimum=Math.Max(2,minimum);
                if(total<minimum*2 || total!=source.BarCount || Math.Abs((total-1)*step-(crossMax-crossMin))>Eps) continue;
                var envelope=ZoneBoundaryConstraints.Bounds(chain.SelectMany(z=>z.Contour).ToList());
                var margin=Math.Max(step,settings.GridCellMm/1000.0)+Eps;
                var bg=Background(settings,source.Layer);
                var nearby=bounds.Where(p=>p.Key.Rebar.Ok && p.Key.Rebar.Get(source.Layer)-bg>MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                    p.Value.MinX<=envelope.MaxX+margin && p.Value.MaxX>=envelope.MinX-margin &&
                    p.Value.MinY<=envelope.MaxY+margin && p.Value.MaxY>=envelope.MinY-margin).Select(p=>p.Key).ToList();
                var minDiameter=source.Layer==RebarLayer.As1 || source.Layer==RebarLayer.As2 ? settings.BgBottomDiameterMm : settings.BgTopDiameterMm;
                var parts=new List<AdditionalZone>();
                for(var first=0;first<total;)
                {
                    var count=total-first<minimum*2 ? total-first : minimum;
                    var from=crossMin+first*step;var to=from+(count-1)*step;
                    var demand=nearby.Where(p=>p.Rebar.Get(source.Layer)-bg<=source.AsCoveredCm2PerM+1e-6 &&
                        (source.Direction==ZoneDirection.X ? bounds[p].MaxY>=from-step-Eps && bounds[p].MinY<=to+step+Eps :
                        bounds[p].MaxX>=from-step-Eps && bounds[p].MinX<=to+step+Eps))
                        .Select(p=>p.Rebar.Get(source.Layer)-bg).DefaultIfEmpty(0).Max();
                    if(demand<=MosaicBuilder.PositiveResidualToleranceCm2PerM){first+=count;continue;}
                    var d=BarCapacity.MinDiameterForAs(demand,source.BarStepMm,source.DiameterMm,minDiameter,settings.ExcludedZoneDiametersMm?.ToArray());
                    if(d<=0 || BarCapacity.AsCm2PerM(d,source.BarStepMm)+1e-6<demand) d=source.DiameterMm;
                    var id=chain.Count>1?Guid.NewGuid().ToString("N"):string.Empty;
                    foreach(var original in chain)
                    {
                        var q=ZoneBoundaryConstraints.Bounds(original.Contour);
                        var part=Clone(original);
                        part.DiameterMm=d;part.AsCoveredCm2PerM=BarCapacity.AsCm2PerM(d,part.BarStepMm);
                        part.AsAdditional=demand;part.AsRequired=bg+demand;
                        part.SpliceGroupId=id;
                        SetBounds(part,source.Direction==ZoneDirection.X?new ZonePatchFrameBounds(q.MinX,q.MaxX,from,to):
                            new ZonePatchFrameBounds(from,to,q.MinY,q.MaxY));
                        UpdateReinforcement(part);
                        part.Comment+="; диаметр ряда уточнён по местной потребности As";
                        parts.Add(part);
                    }
                    first+=count;
                }
                if(Steel(parts)>=Steel(chain)-1) continue;
                var proposal=zones.Where(z=>!chain.Contains(z)).Concat(parts).ToList();
                if(ZoneBoundaryLayoutSolver.ConflictCount(proposal,settings)>ZoneBoundaryLayoutSolver.ConflictCount(zones,settings)) continue;
                if(!ZoneBoundaryLayoutSolver.PreservesRequiredLayout(zones,proposal,nearby,settings,outline,openings,constraints,out _)) continue;
                foreach(var old in chain)zones.Remove(old);
                foreach(var part in parts)zones.Add(part);
                changed++;
            }
            return changed;
        }

        private static int MergeSplicedRows(IList<AdditionalZone> zones,AnalysisSettings settings,ZoneBoundaryConstraints constraints)
        {
            var changed=0;
            for(var again=true;again;)
            {
                again=false;
                var chains=zones.Where(z=>z.SplicePartCount>1 && !string.IsNullOrEmpty(z.SpliceGroupId))
                    .GroupBy(z=>z.SpliceGroupId).Select(g=>g.OrderBy(z=>z.SplicePartIndex).ToList())
                    .Where(g=>g.Count==g[0].SplicePartCount && g.Select(z=>z.SplicePartIndex).Distinct().Count()==g.Count).ToList();
                for(var i=0;i<chains.Count && !again;i++)
                for(var j=i+1;j<chains.Count;j++)
                {
                    var a=chains[i];var b=chains[j];
                    if(a.Count!=b.Count)continue;
                    var merged=new List<AdditionalZone>();var id=Guid.NewGuid().ToString("N");
                    for(var k=0;k<a.Count;k++)
                    {
                        var first=a[k];var second=b[k];
                        if(first.Layer!=second.Layer || first.Direction!=second.Direction || first.DiameterMm!=second.DiameterMm ||
                            first.BarStepMm!=second.BarStepMm || first.FamilyKind!=second.FamilyKind || first.FamilyFileName!=second.FamilyFileName || first.BendAtStart!=second.BendAtStart ||
                            first.BendAtEnd!=second.BendAtEnd || Math.Abs(first.VerticalLegMm-second.VerticalLegMm)>0.01)break;
                        var x=ZoneBoundaryConstraints.Bounds(first.Contour);var y=ZoneBoundaryConstraints.Bounds(second.Contour);
                        var same=first.Direction==ZoneDirection.X ? Math.Abs(x.MinX-y.MinX)<Eps && Math.Abs(x.MaxX-y.MaxX)<Eps :
                            Math.Abs(x.MinY-y.MinY)<Eps && Math.Abs(x.MaxY-y.MaxY)<Eps;
                        var gap=first.Direction==ZoneDirection.X ? Math.Max(x.MinY,y.MinY)-Math.Min(x.MaxY,y.MaxY) :
                            Math.Max(x.MinX,y.MinX)-Math.Min(x.MaxX,y.MaxX);
                        if(!same || Math.Abs(gap-first.BarStepMm/1000.0)>Eps)break;
                        var part=Clone(first);part.SpliceGroupId=id;
                        SetBounds(part,new ZonePatchFrameBounds(Math.Min(x.MinX,y.MinX),Math.Max(x.MaxX,y.MaxX),Math.Min(x.MinY,y.MinY),Math.Max(x.MaxY,y.MaxY)));
                        if(part.BarCount!=first.BarCount+second.BarCount || !constraints.Check(part.Contour,part.Direction).Allowed)break;
                        part.NodeIds=first.NodeIds.Concat(second.NodeIds).Distinct().ToList();
                        part.ElementId=part.NodeIds.FirstOrDefault();
                        part.AsAdditional=Math.Max(first.AsAdditional,second.AsAdditional);part.AsRequired=Math.Max(first.AsRequired,second.AsRequired);
                        UpdateReinforcement(part);
                        part.Comment+="; одинаковые ряды со стыками объединены вместе";
                        merged.Add(part);
                    }
                    if(merged.Count!=a.Count)continue;
                    var proposal=zones.Where(z=>!a.Contains(z)&&!b.Contains(z)).Concat(merged).ToList();
                    if(ZoneBoundaryLayoutSolver.ConflictCount(proposal,settings)>ZoneBoundaryLayoutSolver.ConflictCount(zones,settings))continue;
                    foreach(var old in a.Concat(b))zones.Remove(old);
                    foreach(var part in merged)zones.Add(part);
                    changed++;again=true;break;
                }
            }
            return changed;
        }

        private static double Steel(IEnumerable<AdditionalZone> zones)=>zones.Sum(z=>z.LengthMm*z.BarCount*z.DiameterMm*z.DiameterMm);

        public static int MergeRows(IList<AdditionalZone> zones, AnalysisSettings settings, ZoneBoundaryConstraints constraints)
        {
            var count = 0;
            for (var again = true; again;)
            {
                again = false;
                for (var i = 0; i < zones.Count && !again; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var a = zones[i]; var c = zones[j];
                    if (!a.IsValid || !c.IsValid || a.Layer != c.Layer || a.Direction != c.Direction || a.DiameterMm != c.DiameterMm ||
                        a.BarStepMm != c.BarStepMm || a.FamilyKind != c.FamilyKind || a.FamilyFileName != c.FamilyFileName ||
                        a.BendAtStart != c.BendAtStart || a.BendAtEnd != c.BendAtEnd || Math.Abs(a.VerticalLegMm - c.VerticalLegMm) > 0.01) continue;
                    var x = ZoneBoundaryConstraints.Bounds(a.Contour); var y = ZoneBoundaryConstraints.Bounds(c.Contour);
                    var sameRun = a.Direction == ZoneDirection.X ? Math.Abs(x.MinX-y.MinX)<Eps && Math.Abs(x.MaxX-y.MaxX)<Eps
                        : Math.Abs(x.MinY-y.MinY)<Eps && Math.Abs(x.MaxY-y.MaxY)<Eps;
                    var gap = a.Direction == ZoneDirection.X ? Math.Max(x.MinY,y.MinY)-Math.Min(x.MaxY,y.MaxY)
                        : Math.Max(x.MinX,y.MinX)-Math.Min(x.MaxX,y.MaxX);
                    if (!sameRun || Math.Abs(gap-a.BarStepMm/1000.0)>Eps) continue;
                    var merged = Clone(a);
                    SetBounds(merged, new ZonePatchFrameBounds(Math.Min(x.MinX,y.MinX),Math.Max(x.MaxX,y.MaxX),Math.Min(x.MinY,y.MinY),Math.Max(x.MaxY,y.MaxY)));
                    if (merged.BarCount != a.BarCount+c.BarCount || RebarTables.ExceedsMaxBarLength(merged) ||
                        !constraints.Check(merged.Contour,merged.Direction).Allowed) continue;
                    if (zones.Any(z => !ReferenceEquals(z,a) && !ReferenceEquals(z,c) && ZoneEditor.HasPlacementConflict(merged,z))) continue;
                    var proposal=zones.Where(z=>!ReferenceEquals(z,a)&&!ReferenceEquals(z,c)).Concat(new[]{merged}).ToList();
                    if (ZoneBoundaryLayoutSolver.ConflictCount(proposal,settings)>ZoneBoundaryLayoutSolver.ConflictCount(zones,settings)) continue;
                    merged.NodeIds=a.NodeIds.Concat(c.NodeIds).Distinct().ToList();
                    merged.ElementId=merged.NodeIds.FirstOrDefault();
                    merged.AsAdditional=Math.Max(a.AsAdditional,c.AsAdditional);merged.AsRequired=Math.Max(a.AsRequired,c.AsRequired);
                    UpdateReinforcement(merged);
                    merged.DesignEdgeCoverMm=Math.Max(a.DesignEdgeCoverMm,c.DesignEdgeCoverMm);
                    merged.Comment += "; одинаковые соседние ряды объединены без изменения шага";
                    zones[i]=merged;zones.RemoveAt(j);count++;again=true;break;
                }
            }
            return count;
        }

        private static AdditionalZone Clone(AdditionalZone source) => JsonConvert.DeserializeObject<AdditionalZone>(JsonConvert.SerializeObject(source))!;
        private static void UpdateReinforcement(AdditionalZone zone)
        {
            switch(zone.Layer){case RebarLayer.As1:zone.Rebar.As1=zone.AsRequired;break;case RebarLayer.As2:zone.Rebar.As2=zone.AsRequired;break;
                case RebarLayer.As3:zone.Rebar.As3=zone.AsRequired;break;case RebarLayer.As4:zone.Rebar.As4=zone.AsRequired;break;}
        }
        private static void SetBounds(AdditionalZone zone,ZonePatchFrameBounds b)
        {
            zone.Contour=new List<Point3>{new Point3(b.MinX,b.MinY,zone.LevelZM),new Point3(b.MaxX,b.MinY,zone.LevelZM),
                new Point3(b.MaxX,b.MaxY,zone.LevelZM),new Point3(b.MinX,b.MaxY,zone.LevelZM)};
            zone.Placement=new Point3((b.MinX+b.MaxX)/2,(b.MinY+b.MaxY)/2,zone.LevelZM);
            zone.LengthM=zone.Direction==ZoneDirection.X?b.MaxX-b.MinX:b.MaxY-b.MinY;
            zone.WidthM=zone.Direction==ZoneDirection.X?b.MaxY-b.MinY:b.MaxX-b.MinX;
            zone.WidthMm=zone.WidthM*1000;
            zone.BarCount=(int)Math.Round(zone.WidthMm/zone.BarStepMm)+1;
            zone.LengthMm=zone.FamilyKind==ZoneFamilyKind.Straight?zone.LengthM*1000:
                RebarTables.BentBarTotalLengthMm(zone.LengthM*1000,zone.VerticalLegMm,zone.FamilyKind);
        }
        private static double Background(AnalysisSettings s,RebarLayer l)=>l switch {
            RebarLayer.As1=>s.AsMainAs1,RebarLayer.As2=>s.AsMainAs2,RebarLayer.As3=>s.AsMainAs3,_=>s.AsMainAs4};
    }
}
