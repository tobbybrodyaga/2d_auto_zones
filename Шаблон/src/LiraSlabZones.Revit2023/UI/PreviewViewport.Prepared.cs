using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiraSlabZones.Core;

namespace LiraSlabZones.Revit2023.UI
{
    public sealed partial class PreviewViewport
    {
        /// <summary>Только данные и замороженная геометрия; элементы WPF между потоками не передаются.</summary>
        public sealed class PreparedLayout
        {
            public AnalysisResult Result { get; }
            private readonly Action<PreviewViewport> _apply;
            internal PreparedLayout(PreviewViewport worker)
            {
                Result=worker._result!;
                var plates=worker._plateShapes.ToArray();var zones=worker._drawZones.ToArray();
                var fills=worker._drawPatchFills.ToArray();var frames=worker._drawPatchFrames.ToArray();
                var patchZones=worker._drawPatchZones.ToArray();var candidates=worker._patchCandidateZones.ToArray();
                var values=worker._patchElementValues.ToArray();
                if(plates.Concat(zones).Concat(patchZones).Concat(fills.Select(f=>f.Shape)).Concat(frames.Select(f=>f.Shape))
                    .Any(shape=>!shape.Geometry.IsFrozen)) throw new InvalidOperationException("Геометрия для предпросмотра не заморожена");
                var settings=worker._settings;var conflict=worker.LastConflictResolution;var alignment=worker.LastAlignment;
                var minX=worker._modelMinX;var maxX=worker._modelMaxX;var minY=worker._modelMinY;var maxY=worker._modelMaxY;
                var pivotX=worker._pivotX;var pivotY=worker._pivotY;
                _apply=target=>
                {
                    target._result=Result;target._settings=settings;target._coverageReference=null;
                    Copy(target._plateShapes,plates);Copy(target._drawZones,zones);Copy(target._drawPatchFills,fills);
                    Copy(target._drawPatchFrames,frames);Copy(target._drawPatchZones,patchZones);
                    Copy(target._patchCandidateZones,candidates);Copy(target._patchElementValues,values);
                    target._modelMinX=minX;target._modelMaxX=maxX;target._modelMinY=minY;target._modelMaxY=maxY;
                    target._pivotX=pivotX;target._pivotY=pivotY;
                    target.LastConflictResolution=conflict;target.LastAlignment=alignment;
                    target._undo.Clear();target._pendingUndo=null;target._selectedZoneId=null;target._selectedPatchFrameId=null;
                    target._editZone=null;target._editStart=null;target._mergeZone=null;target._gapMovingZone=null;
                };
            }
            private static void Copy<T>(List<T> target,IEnumerable<T> values){target.Clear();target.AddRange(values);}
            internal void Apply(PreviewViewport target)=>_apply(target);
        }

        public static Task<PreparedLayout> PrepareLayoutAsync(AnalysisResult result,AnalysisSettings settings,
            IList<AdditionalZone>? coverageReference,Action<string,int,int>? progress)
        {
            var completion=new TaskCompletionSource<PreparedLayout>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>
            {
                try
                {
                    var worker=new PreviewViewport {LayoutProgress=progress};
                    worker.SetData(result,settings,true,true,fitView:false,coverageReference:coverageReference);
                    completion.TrySetResult(new PreparedLayout(worker));
                }
                catch(Exception ex){completion.TrySetException(ex);}
            }) {IsBackground=true,Name="LiraSlabZones: пересчёт"};
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }

        public void ApplyPreparedLayout(PreparedLayout prepared,bool mesh,bool iso,bool axes,bool boundaries)
        {
            VerifyAccess();
            prepared.Apply(this);
            _showMesh=mesh;_showIso=iso;_showAxes=axes;_showPatchBoundaries=boundaries;
            InvalidateVisual();RaiseStatus();
        }
    }
}
