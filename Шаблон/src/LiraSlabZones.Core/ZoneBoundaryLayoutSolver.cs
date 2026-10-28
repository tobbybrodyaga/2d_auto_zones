using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace LiraSlabZones.Core
{
    /// <summary>Подбор прямых зон до увязки соседей. Исходные КЭ и пятна не изменяются.</summary>
    public static class ZoneBoundaryLayoutSolver
    {
        private const double Scale = 1000000;
        private const double Eps = 0.000002;

        private sealed class Plan
        {
            public List<AdditionalZone> Zones = new();
            public Dictionary<AdditionalZone, ZonePatchFrameBounds> Supports = new();
            public Dictionary<AdditionalZone, ZonePatchFrameBounds> Patches = new();
        }

        private sealed class Target
        {
            public LiraPlateElement Plate = null!;
            public RebarLayer Layer;
            public double Demand;
        }

        private sealed class Evaluation
        {
            public bool[] Covered = Array.Empty<bool>();
            public bool[] Anchored = Array.Empty<bool>();
            public bool[] Safe = Array.Empty<bool>();
            public int Conflicts;
            public int BadZones;
            public int Count;
            public int[] Rank => new[] { Covered.Count(x => !x), Anchored.Count(x => !x),
                Safe.Count(x => !x), Conflicts, BadZones, Count };
        }

        public static List<AdditionalZone> Build(ZonePatchFrameSelection frame, double elevationZM,
            AnalysisSettings settings, IDictionary<AdditionalZone, ZonePatchFrameBounds>? supports = null,
            IDictionary<AdditionalZone, ZonePatchFrameBounds>? patches = null)
        {
            if (frame?.BoundaryConstraints?.HasOutline != true || frame.Patches == null || settings == null ||
                string.IsNullOrWhiteSpace(settings.ConcreteClass))
                return ZonePatchZoneBuilder.BuildCandidate(frame!, elevationZM, settings!, supports, patches);
            frame = ZoneOuterEdgeLayout.Prepare(frame);
            var result = new List<AdditionalZone>();
            foreach (var group in frame.Patches.GroupBy(p => p.Layer).OrderBy(g => g.Key))
            {
                var layerFrame = Slice(frame, new ZonePatchFrameBounds(frame.MinXM, frame.MaxXM,
                    frame.MinYM, frame.MaxYM), group.Key);
                var budget = 48;
                var plan = Solve(layerFrame, elevationZM, settings, 0, ref budget);
                foreach (var zone in plan.Zones)
                {
                    zone.ZoneId = result.Count + 1;
                    result.Add(zone);
                    if (plan.Supports.TryGetValue(zone, out var support)) supports?.Add(zone, support);
                    if (plan.Patches.TryGetValue(zone, out var patch)) patches?.Add(zone, patch);
                }
            }
            return result;
        }

        private static Plan Candidate(ZonePatchFrameSelection frame, double z, AnalysisSettings settings,
            bool shorter = false)
        {
            var plan = new Plan();
            plan.Zones = ZonePatchZoneBuilder.BuildCandidate(frame, z, settings, plan.Supports, plan.Patches, shorter);
            return plan;
        }

        /// <summary>Повторный подбор проблемных участков после общей увязки и защиты покрытия.</summary>
        public static int Repair(IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            AnalysisSettings settings, IList<Point3>? outline, IList<OpeningInfo>? openings,
            ZoneBoundaryConstraints constraints, IList<AlignAxis>? axes = null)
        {
            if (!constraints.HasOutline || outline == null || string.IsNullOrWhiteSpace(settings.ConcreteClass)) return 0;
            settings.SyncBackgroundAsFromBars();
            var variants = new List<AnalysisSettings> { settings };
            var steps = (settings.AllowedAdditionalBarStepsMm ?? new List<int> { 100, 200 })
                .Where(s => s == 100 || s == 200).Distinct().OrderBy(s => s).ToList();
            if (steps.Count > 1)
                foreach (var step in steps)
                {
                    var variant = Newtonsoft.Json.JsonConvert.DeserializeObject<AnalysisSettings>(
                        Newtonsoft.Json.JsonConvert.SerializeObject(settings))!;
                    variant.AllowedAdditionalBarStepsMm = new List<int> { step };
                    variants.Add(variant);
                }
            var repaired = 0;
            // Геометрический подбор зависит от исходных КЭ, а не от уже сдвинутых соседей.
            // В пределах одного прохода не повторяем одинаковый неудачный поиск.
            var optionCache = new Dictionary<string, List<List<AdditionalZone>>>();
            var limit = zones.Count(z => !constraints.Check(z.Contour, z.Direction).Allowed);
            for (var pass = 0; pass < limit; pass++)
            {
                var changed = false;
                foreach (var original in zones.ToList())
                {
                    if (original.FamilyKind != ZoneFamilyKind.Straight ||
                        constraints.Check(original.Contour, original.Direction).Allowed) continue;
                    var background = original.Layer switch {
                        RebarLayer.As1 => settings.AsMainAs1, RebarLayer.As2 => settings.AsMainAs2,
                        RebarLayer.As3 => settings.AsMainAs3, _ => settings.AsMainAs4 };
                    var layer = zones.Where(z => z.Layer == original.Layer).ToList();
                    bool Covers(IList<AdditionalZone> layout, LiraPlateElement plate) => ZoneCoverageRules.CoversOrBridgesGap(
                        layout, plate, original.Layer, plate.Rebar.Get(original.Layer) - background, outline, openings);
                    var required = plates.Where(p => p.Rebar.Ok &&
                        p.Rebar.Get(original.Layer) - background > MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                        Covers(layer, p)).ToList();
                    var without = layer.Where(z => !ReferenceEquals(z, original)).ToList();
                    var exclusive = required.Where(p => !Covers(without, p)).ToList();
                    var context = new ZonePatchFrameSelection { SlabOutline = outline.ToList(), BoundaryConstraints = constraints };
                    var targets = required.Select(p => new Target { Plate = p, Layer = original.Layer,
                        Demand = p.Rebar.Get(original.Layer) - background }).ToList();
                    Evaluation? previous = null;
                    bool TryCommit(List<AdditionalZone> replacements)
                    {
                        var proposal = constraints.Snapshot(without.Concat(replacements));
                        previous ??= Evaluate(new Plan { Zones = layer }, targets, context, settings);
                        if (ConflictCount(proposal, settings) > previous.Conflicts)
                            ZoneEditor.EnforceRequiredGaps(proposal, plates, settings, outline, openings);
                        if (axes?.Count > 0)
                        {
                            var oldAlignment = RevitGridAlignment.Apply(constraints.Snapshot(layer), axes, outline, openings, settings.EdgeOffsetMm);
                            var alignment = RevitGridAlignment.Apply(proposal, axes, outline, openings, settings.EdgeOffsetMm);
                            if (alignment.UntiedZones > oldAlignment.UntiedZones) return false;
                        }
                        var evaluated = Evaluate(new Plan { Zones = proposal }, targets, context, settings);
                        if (evaluated.Conflicts > previous.Conflicts || evaluated.BadZones >= previous.BadZones ||
                            !Preserves(previous.Anchored, evaluated.Anchored) || !Preserves(previous.Safe, evaluated.Safe) ||
                            required.Any(p => !Covers(proposal, p))) return false;
                        // Нельзя исправить грань ценой разрыва уже собранного продольного стыка.
                        for (var i = 0; i < layer.Count; i++)
                        for (var j = i + 1; j < layer.Count; j++)
                        {
                            if (!ZoneEditor.IsAllowedLongitudinalLapOverlap(layer[i], layer[j])) continue;
                            var first = ReferenceEquals(layer[i], original) ? proposal.Skip(without.Count)
                                : proposal.Skip(without.IndexOf(layer[i])).Take(1);
                            var second = ReferenceEquals(layer[j], original) ? proposal.Skip(without.Count)
                                : proposal.Skip(without.IndexOf(layer[j])).Take(1);
                            if (!first.Any(a => second.Any(b => ZoneEditor.IsAllowedLongitudinalLapOverlap(a, b)))) return false;
                        }
                        // Увязка соседей также должна сохранять типовые длины и кратность ширины.
                        for (var i = 0; i < without.Count; i++)
                        {
                            var a = ZoneBoundaryConstraints.Bounds(without[i].Contour);
                            var b = ZoneBoundaryConstraints.Bounds(proposal[i].Contour);
                            if (Math.Abs(a.MinX-b.MinX)+Math.Abs(a.MaxX-b.MaxX)+Math.Abs(a.MinY-b.MinY)+Math.Abs(a.MaxY-b.MaxY)<Eps) continue;
                            var zone = proposal[i];
                            if (zone.FamilyKind != ZoneFamilyKind.Straight) return false;
                            if (zone.BarStepMm <= 0 || Math.Abs(zone.WidthMm/zone.BarStepMm-Math.Round(zone.WidthMm/zone.BarStepMm))>1e-6 ||
                                !RebarTables.Sum3FamilyLengthsMm.Any(l => Math.Abs(l-zone.LengthMm)<0.001)) return false;
                        }
                        for (var i = zones.Count - 1; i >= 0; i--)
                            if (zones[i].Layer == original.Layer) zones.RemoveAt(i);
                        for (var i = 0; i < proposal.Count; i++)
                        {
                            if (i >= without.Count) proposal[i].Comment += "; автоисправление у границы после увязки";
                            zones.Add(proposal[i]);
                        }
                        return true;
                    }
                    if (exclusive.Count > 0)
                    {
                        var b = ZoneBoundaryConstraints.Bounds(exclusive.SelectMany(p => p.Contour).ToList());
                        var frame = new ZonePatchFrameSelection { BoundaryConstraints = constraints,
                            SlabOutline = outline.ToList(), MinXM = b.MinX, MaxXM = b.MaxX, MinYM = b.MinY, MaxYM = b.MaxY };
                        foreach (var plate in exclusive)
                        {
                            var bounds = ZoneBoundaryConstraints.Bounds(plate.Contour);
                            var demand = plate.Rebar.Get(original.Layer) - background;
                            frame.Elements.Add(new ZonePatchFrameElement { ElementId = plate.Id, Layer = original.Layer,
                                AsAdditionalCm2PerM = demand, Contour = plate.Contour });
                            frame.Patches.Add(new ZonePatch { PatchId = plate.Id, Layer = original.Layer, Direction = original.Direction,
                                MinXM = bounds.MinX, MaxXM = bounds.MaxX, MinYM = bounds.MinY, MaxYM = bounds.MaxY,
                                PeakAsAdditionalCm2PerM = demand, ElementIds = new List<int> { plate.Id },
                                Cells = new List<ZonePatchCell> { new ZonePatchCell { MinXM = bounds.MinX, MaxXM = bounds.MaxX,
                                    MinYM = bounds.MinY, MaxYM = bounds.MaxY, AsAdditionalCm2PerM = demand } } });
                        }
                        var key = original.Layer + ":" + original.Direction + ":" +
                            string.Join(",", exclusive.Select(p => p.Id).OrderBy(id => id));
                        if (!optionCache.TryGetValue(key, out var options))
                        {
                            options = new List<List<AdditionalZone>>();
                            foreach (var variant in variants)
                            {
                                var replacements = Build(frame, original.LevelZM, variant);
                                if (replacements.Count == 0 || replacements.Any(z => !z.IsValid ||
                                    !constraints.Check(z.Contour, z.Direction).Allowed)) continue;
                                var tested = Evaluate(new Plan { Zones = replacements }, Targets(frame), frame, variant);
                                if (tested.Covered.All(c => c) && tested.Anchored.All(c => c)) options.Add(replacements);
                            }
                            optionCache.Add(key, options);
                        }
                        foreach (var replacements in options)
                        {
                            if (TryCommit(replacements)) { changed = true; break; }
                        }
                    }
                    else
                    {
                        // Лишняя зона удаляется, только если оставшиеся стержни сохраняют анкеровку.
                        var affected = targets.Where(t => ZoneCoverageRules.IntersectsElementFootprint(original, t.Plate, outline)).ToList();
                        var tested = Evaluate(new Plan { Zones = without }, affected, context, settings);
                        if (tested.Anchored.All(c => c)) changed = TryCommit(new List<AdditionalZone>());
                    }
                    if (changed) { repaired++; break; }
                }
                if (!changed) break;
            }
            return repaired;
        }
        private static Plan Solve(ZonePatchFrameSelection frame, double z, AnalysisSettings settings,
            int depth, ref int budget)
        {
            budget--;
            var original = Candidate(frame, z, settings);
            if (original.Zones.Count == 0) return original;
            var targets = Targets(frame);
            var best = original;
            var score = Evaluate(best, targets, frame, settings);
            if (score.BadZones == 0) return best;

            var alternative = Candidate(frame, z, settings, true);
            Consider(alternative, targets, frame, settings, ref best, ref score);
            if (score.BadZones == 0 || depth >= 2 || budget < 2 || targets.Count == 0) return best;

            var direction = original.Zones[0].Direction;
            var min = direction == ZoneDirection.X ? frame.MinYM : frame.MinXM;
            var max = direction == ZoneDirection.X ? frame.MaxYM : frame.MaxXM;
            var minimum = Math.Max(0.1, settings.EffectiveMinZoneWidthM);
            var coordinates = frame.Elements.SelectMany(e => e.Contour)
                .Select(p => direction == ZoneDirection.X ? p.Y : p.X)
                .Concat(frame.Patches.SelectMany(p => p.Cells).SelectMany(c => direction == ZoneDirection.X
                    ? new[] { c.MinYM, c.MaxYM } : new[] { c.MinXM, c.MaxXM }))
                .Distinct().OrderBy(c => c).ToList();
            var orderedCuts = coordinates.Concat(coordinates.Zip(coordinates.Skip(1), (a, b) => (a + b) / 2))
                .Append((min + max) / 2)
                .Where(c => c - min >= minimum && max - c >= minimum)
                .Distinct().OrderBy(c => Math.Abs(c - (min + max) / 2));
            var cuts = new List<double>();
            foreach (var cut in orderedCuts)
            {
                if (cuts.Any(c => Math.Abs(c - cut) < Math.Max(0.05, minimum / 3) - Eps)) continue;
                cuts.Add(cut);
                if (cuts.Count == 5) break;
            }
            var gaps = (settings.AllowedAdditionalBarStepsMm ?? new List<int> { 100, 200 })
                .Where(s => s == 100 || s == 200).Distinct().OrderBy(s => s).ToList();
            if (gaps.Count == 0) gaps.Add(200);
            foreach (var cut in cuts)
            foreach (var step in gaps)
            {
                if (budget < 2) return best;
                var gap = step / 1000.0;
                if (cut - gap / 2 - min < minimum || max - cut - gap / 2 < minimum) continue;
                var lowerFrame = Slice(frame, CrossBounds(frame, direction, min, cut - gap / 2), targets[0].Layer);
                var upperFrame = Slice(frame, CrossBounds(frame, direction, cut + gap / 2, max), targets[0].Layer);
                // Один неудачный разрез не должен расходовать весь бюджет поиска в глубину.
                var lowerBudget = Math.Min(4, budget / 2);
                var allocated = lowerBudget;
                var lower = Solve(lowerFrame, z, settings, depth + 1, ref lowerBudget);
                budget -= allocated - lowerBudget;
                var upperBudget = Math.Min(4, budget);
                allocated = upperBudget;
                var upper = Solve(upperFrame, z, settings, depth + 1, ref upperBudget);
                budget -= allocated - upperBudget;
                if (lower.Zones.Count == 0 || upper.Zones.Count == 0) continue;
                if (!PlaceAtSeam(lower, direction, cut - gap / 2, true, frame) ||
                    !PlaceAtSeam(upper, direction, cut + gap / 2, false, frame)) continue;
                var combined = new Plan { Zones = lower.Zones.Concat(upper.Zones).ToList() };
                foreach (var part in new[] { lower, upper })
                {
                    foreach (var pair in part.Supports) combined.Supports.Add(pair.Key, pair.Value);
                    foreach (var pair in part.Patches) combined.Patches.Add(pair.Key, pair.Value);
                }
                // Проверяем полные исходные полигоны, включая полосу между новыми рядами.
                // Зазор допустим лишь при сохранённом покрытии и анкеровке каждого КЭ.
                if (Consider(combined, targets, frame, settings, ref best, ref score))
                    foreach (var zone in best.Zones)
                        zone.Comment += "; автопереразбивка у границы, покрытие и прямые участки анкеровки проверены";
                if (score.BadZones == 0 && score.Conflicts == 0) return best;
            }
            return best;
        }

        private static bool Consider(Plan proposal, List<Target> targets, ZonePatchFrameSelection frame,
            AnalysisSettings settings, ref Plan best, ref Evaluation score)
        {
            if (proposal.Zones.Count == 0) return false;
            var tested = Evaluate(proposal, targets, frame, settings);
            if (!Preserves(score.Covered, tested.Covered) || !Preserves(score.Anchored, tested.Anchored) ||
                !Preserves(score.Safe, tested.Safe) || tested.Conflicts > score.Conflicts) return false;
            var comparison = 0;
            var a = tested.Rank; var b = score.Rank;
            for (var i = 0; i < a.Length && comparison == 0; i++) comparison = a[i].CompareTo(b[i]);
            // Неразрешённый конфликт не оправдывает замену на просто более дешёвую раскладку.
            if (comparison >= 0) return false;
            best = proposal; score = tested;
            return true;
        }

        private static bool Preserves(bool[] before, bool[] after) =>
            !before.Where((value, i) => value && !after[i]).Any();

        private static Evaluation Evaluate(Plan plan, List<Target> targets, ZonePatchFrameSelection frame,
            AnalysisSettings settings)
        {
            var safe = plan.Zones.Where(z => z.IsValid && frame.BoundaryConstraints!.Check(z.Contour, z.Direction).Allowed).ToList();
            var cores = new List<AdditionalZone>();
            var safeCores = new List<AdditionalZone>();
            foreach (var zone in plan.Zones)
            {
                var b = ZoneBoundaryConstraints.Bounds(zone.Contour);
                var anchor = RebarTables.AnchorageLenMm(settings.ConcreteClass, zone.DiameterMm) / 1000.0;
                var startAnchor = zone.BendAtStart ? 0 : anchor;
                var endAnchor = zone.BendAtEnd ? 0 : anchor;
                var core = zone.Direction == ZoneDirection.X
                    ? new ZonePatchFrameBounds(b.MinX + startAnchor, b.MaxX - endAnchor, b.MinY, b.MaxY)
                    : new ZonePatchFrameBounds(b.MinX, b.MaxX, b.MinY + startAnchor, b.MaxY - endAnchor);
                if (core.MaxX <= core.MinX || core.MaxY <= core.MinY) continue;
                var coreZone = new AdditionalZone { Layer = zone.Layer, Direction = zone.Direction,
                    DesignEdgeCoverMm = zone.DesignEdgeCoverMm,
                    AsCoveredCm2PerM = zone.AsCoveredCm2PerM, BarStepMm = zone.BarStepMm, Contour = Rect(core, zone.LevelZM) };
                cores.Add(coreZone);
                if (safe.Contains(zone)) safeCores.Add(coreZone);
            }
            bool Covers(IList<AdditionalZone> zones, Target t, bool overlap = false) => ZoneCoverageRules.CoversOrBridgesGap(
                zones, t.Plate, t.Layer, t.Demand, frame.SlabOutline, null, overlap);
            var result = new Evaluation {
                Covered = targets.Select(t => Covers(plan.Zones, t)).ToArray(),
                Anchored = targets.Select(t => Covers(cores, t, true)).ToArray(),
                Safe = targets.Select(t => Covers(safe, t) && Covers(safeCores, t, true)).ToArray(),
                BadZones = plan.Zones.Count - safe.Count, Count = plan.Zones.Count
            };
            result.Conflicts = ConflictCount(plan.Zones, settings);
            return result;
        }

        internal static bool PreservesRequiredLayout(IList<AdditionalZone> before, IList<AdditionalZone> after,
            IList<LiraPlateElement> plates, AnalysisSettings settings, IList<Point3>? outline,
            IList<OpeningInfo>? openings, ZoneBoundaryConstraints constraints, out string rejection)
        {
            rejection=string.Empty;
            foreach (var layer in before.Select(z => z.Layer).Distinct())
            {
                var background = layer switch { RebarLayer.As1 => settings.AsMainAs1,
                    RebarLayer.As2 => settings.AsMainAs2, RebarLayer.As3 => settings.AsMainAs3, _ => settings.AsMainAs4 };
                var targets = plates.Where(p => p.Rebar.Ok && p.Rebar.Get(layer)-background > MosaicBuilder.PositiveResidualToleranceCm2PerM &&
                    ZoneCoverageRules.CoversOrBridgesGap(before,p,layer,p.Rebar.Get(layer)-background,outline,openings,true))
                    .Select(p => new Target { Plate=p,Layer=layer,Demand=p.Rebar.Get(layer)-background }).ToList();
                // Коллизии проверяются отдельно: расчётные КЭ, покрытые объединением старых
                // пересекающихся зон, нельзя терять при устранении этого пересечения.
                if (targets.Any(t => !ZoneCoverageRules.CoversOrBridgesGap(after,t.Plate,layer,t.Demand,outline,openings,true)))
                { rejection="Coverage";return false; }
                var frame = new ZonePatchFrameSelection { BoundaryConstraints=constraints,
                    SlabOutline=outline?.ToList() ?? new List<Point3>() };
                var oldScore=Evaluate(new Plan { Zones=before.Where(z=>z.Layer==layer).ToList() },targets,frame,settings);
                var newScore=Evaluate(new Plan { Zones=after.Where(z=>z.Layer==layer).ToList() },targets,frame,settings);
                if (!Preserves(oldScore.Anchored,newScore.Anchored)) {rejection="Anchorage";return false;}
                if (!Preserves(oldScore.Safe,newScore.Safe)) {rejection="SafeCoverage";return false;}
            }
            return true;
        }

        internal static int ConflictCount(IList<AdditionalZone> zones, AnalysisSettings settings)
        {
            var pairs = ZoneLayoutDiagnostics.AddInterZoneStepIssues(zones,
                new ZoneDiagnostics(), settings.GridCellMm / 1000.0);
            for (var i = 0; i < zones.Count; i++)
            for (var j = i + 1; j < zones.Count; j++)
                if (ZoneEditor.HasPlacementConflict(zones[i], zones[j])) pairs.Add((i,j));
            return pairs.Count;
        }

        private static bool PlaceAtSeam(Plan plan, ZoneDirection direction, double edge, bool lower,
            ZonePatchFrameSelection frame)
        {
            foreach (var zone in plan.Zones)
            {
                var b = ZoneBoundaryConstraints.Bounds(zone.Contour);
                var from = direction == ZoneDirection.X ? b.MinY : b.MinX;
                var to = direction == ZoneDirection.X ? b.MaxY : b.MaxX;
                var shift = lower ? Math.Min(0, edge - to) : Math.Max(0, edge - from);
                if (Math.Abs(shift) < 1e-9) continue;
                var moved = direction == ZoneDirection.X
                    ? new ZonePatchFrameBounds(b.MinX, b.MaxX, b.MinY + shift, b.MaxY + shift)
                    : new ZonePatchFrameBounds(b.MinX + shift, b.MaxX + shift, b.MinY, b.MaxY);
                if (!plan.Supports.TryGetValue(zone, out var required) ||
                    moved.MinX > required.MinX + Eps || moved.MaxX < required.MaxX - Eps ||
                    moved.MinY > required.MinY + Eps || moved.MaxY < required.MaxY - Eps) return false;
                var contour = Rect(moved, zone.LevelZM);
                if (frame.BoundaryConstraints!.Check(zone.Contour, direction).Allowed &&
                    !frame.BoundaryConstraints.Check(contour, direction).Allowed) return false;
                zone.Contour = contour;
                zone.Placement = new Point3((moved.MinX + moved.MaxX) / 2, (moved.MinY + moved.MaxY) / 2, zone.LevelZM);
                zone.BoundaryCheck = frame.BoundaryConstraints.Check(contour, direction);
            }
            return true;
        }

        private static ZonePatchFrameSelection Slice(ZonePatchFrameSelection source, ZonePatchFrameBounds b, RebarLayer layer) =>
            new ZonePatchFrameSelection { BoundaryConstraints = source.BoundaryConstraints, SlabOutline = source.SlabOutline,
                Patches = source.Patches.Where(p => p.Layer == layer).ToList(),
                Elements = source.Elements.Where(e => e.Layer == layer).ToList(),
                MinXM = b.MinX, MaxXM = b.MaxX, MinYM = b.MinY, MaxYM = b.MaxY };

        private static ZonePatchFrameBounds CrossBounds(ZonePatchFrameSelection frame, ZoneDirection direction,
            double min, double max) => direction == ZoneDirection.X
            ? new ZonePatchFrameBounds(frame.MinXM, frame.MaxXM, min, max)
            : new ZonePatchFrameBounds(min, max, frame.MinYM, frame.MaxYM);

        private static List<Target> Targets(ZonePatchFrameSelection frame)
        {
            var result = new List<Target>();
            var rectangle = new Paths64 { ToPath(Rect(new ZonePatchFrameBounds(frame.MinXM, frame.MaxXM,
                frame.MinYM, frame.MaxYM), 0)) };
            void Add(IList<Point3> contour, RebarLayer layer, double demand, int id)
            {
                if (contour.Count < 3 || demand <= MosaicBuilder.PositiveResidualToleranceCm2PerM) return;
                foreach (var path in Clipper.Intersect(new Paths64 { ToPath(contour) }, rectangle, FillRule.NonZero))
                {
                    if (Math.Abs(Clipper.Area(path)) < 1) continue;
                    var points = path.Select(p => new Point3(p.X / Scale, p.Y / Scale, 0)).ToList();
                    result.Add(new Target { Layer = layer, Demand = demand,
                        Plate = new LiraPlateElement { Id = id, Contour = points,
                            Centroid = new Point3(points.Average(p => p.X), points.Average(p => p.Y), 0) } });
                }
            }
            if (frame.Elements.Count > 0)
                foreach (var e in frame.Elements) Add(e.Contour, e.Layer, e.AsAdditionalCm2PerM, e.ElementId);
            else
                foreach (var patch in frame.Patches)
                foreach (var cell in patch.Cells)
                    Add(Rect(new ZonePatchFrameBounds(cell.MinXM, cell.MaxXM, cell.MinYM, cell.MaxYM), 0),
                        patch.Layer, cell.AsAdditionalCm2PerM, 0);
            return result;
        }

        private static Path64 ToPath(IEnumerable<Point3> contour) => new Path64(contour.Select(p =>
            new Point64((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale))));
        private static List<Point3> Rect(ZonePatchFrameBounds b, double z) => new List<Point3> {
            new Point3(b.MinX,b.MinY,z), new Point3(b.MaxX,b.MinY,z),
            new Point3(b.MaxX,b.MaxY,z), new Point3(b.MinX,b.MaxY,z) };
    }
}
