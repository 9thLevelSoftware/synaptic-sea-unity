using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Actual assembler bootstrap cache, NOT enrolled world authority or a save receipt.
    // No save request calls the mutable assembler through this type.
    internal sealed class ContinuousBootstrapSnapshot
    {
        readonly GdDict _run,_world;
        readonly string _runId;
        readonly RunSession _session;
        readonly Artifact[] _artifacts;
        sealed class Artifact
        {internal readonly string Path,Kind,Text;internal readonly string Version;internal Artifact(string path,string kind,string text,string version){Path=path;Kind=kind;Text=text;Version=version;}}
        ContinuousBootstrapSnapshot(GdDict run,GdDict world,RunSession session,Artifact[] artifacts){_run=run;_world=world;_runId=session.RunIdInternal;_session=session;_artifacts=artifacts;}
        internal string RunId=>_runId;
        internal static bool TryReadFreshBeforeContinuousLoop(RunSession session,out ContinuousBootstrapSnapshot snapshot,out string reason)
        {
            snapshot=null;reason="continuous_bootstrap_not_fresh";
            if(session==null||!session.ContinuousDiagnosticBootstrapOpen||!session.PlayableStarted||session.SliceComplete||session.ComponentGenerationRestoreInProgress)return false;
            // DISCLOSED synchronous bootstrap work, never per-save or background mutable read.
            // Root constructor snapshots opt-in prerequisites; first Tick closes this phase.
            // Actual playable post-launch state and no restore are additionally required.
            var built=SavePayloadAssembler.Build(session,"world",SaveSlotState.SlotKindWorld);
            if(!built.GetBool("ok")){reason=built.GetString("reason");return false;}
            var payload=built.GetDictOrEmpty("payloads");
            var run=PaidSnapshotCodec.Parse(payload.GetString("run_text"),PaidSnapshotCodec.SnapshotPolicy(session.ComponentIntegrationEnabled,false)) as GdDict;
            var world=PaidSnapshotCodec.Parse(payload.GetString("world_text"),PaidSnapshotCodec.SnapshotPolicy(session.ComponentIntegrationEnabled,true)) as GdDict;
            if(run==null||world==null||!ContinuousSnapshotFieldPlan.InspectBootstrapLayout(run,false,out _,out reason)||
                !ContinuousSnapshotFieldPlan.InspectBootstrapLayout(world,true,out _,out reason))return false;
            var rows=payload.Get("artifacts") as GdArray;
            if(rows==null||rows.Count==0||rows.Count>512){reason="continuous_genesis_artifact_bound";return false;}
            var artifacts=new Artifact[rows.Count];long textBytes=0;
            var paths=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<rows.Count;i++)
            {
                if(!(rows[i] is GdDict row)||row.Count!=4||!(row.Get("logical_path") is string path)||path.Length==0||path.Length>512||
                    !(row.Get("document_kind") is string kind)||kind.Length==0||kind.Length>64||!(row.Get("text") is string text)||
                    !(row.Get("schema_version") is string version)||version.Length==0||version.Length>64||!paths.Add(path))
                {reason="continuous_genesis_artifact_shape";return false;}
                // One disclosed bootstrap scan. No hash/UTF8 scans on later capture requests.
                textBytes+=System.Text.Encoding.UTF8.GetByteCount(text);
                if(textBytes>4L*1024*1024){reason="continuous_genesis_artifact_bound";return false;}
                artifacts[i]=new Artifact(path,kind,text,version);
            }
            snapshot=new ContinuousBootstrapSnapshot(run,world,session,artifacts);reason="";return true;
        }
        internal int ArtifactCount=>_artifacts.Length;
        // Strings are immutable original archive bytes; no dictionary/array/model escapes.
        internal bool TryReadArtifact(int index,out string path,out string kind,out string text,out string version)
        {
            path=kind=text=version=null;
            if(index<0||index>=_artifacts.Length)return false;
            var row=_artifacts[index];path=row.Path;kind=row.Kind;text=row.Text;version=row.Version;return true;
        }
        // Diagnostic overlay only. Same actual model source is checked before pinning,
        // each bounded advancement and every completed observation. No world authority.
        internal bool TryBeginVitalsOverlay(ActualVitalsProjectionBridge bridge,out ContinuousVitalsOverlayCursor cursor,out string reason)
        {
            cursor=null;reason="continuous_vitals_source_mismatch";
            if(bridge==null)return false;
            lock(CommonParticipantGate.SyncRoot)
            {
                if(_session.RunIdInternal!=_runId||!bridge.IsBoundActualSessionUnderGate(_session))return false;
                if(!bridge.TryPin(out var pin,out reason))return false;
                if(!pin.TryCreateCursor(out var capture,out reason)){pin.Dispose();return false;}
                cursor=new ContinuousVitalsOverlayCursor(_run,_session,bridge,pin,capture);reason="";return true;
            }
        }
        // Enumerates owned parsed bootstrap only. No mutable model/scene access occurs in this cursor.
        internal ContinuousBootstrapTraversalCursor BeginTraversal(bool world)=>new ContinuousBootstrapTraversalCursor(world?_world:_run);
        internal bool TryBuildWholeWorldSave(out string reason){reason="continuous_dynamic_producers_not_enrolled";return false;}
    }
    internal enum ContinuousTraversalStatus{Pending,CompletePartialDiagnostic,Refused,Cancelled}
    internal sealed class ContinuousBootstrapTraversalCursor
    {
        const int MaximumDepth=128,MaximumNodes=100000;
        readonly System.Collections.Generic.Stack<Frame> _frames=new System.Collections.Generic.Stack<Frame>();
        bool _terminal;
        internal string Reason{get;private set;}="";
        internal int VisitedNodes{get;private set;}
        internal ContinuousTraversalStatus Status{get;private set;}=ContinuousTraversalStatus.Pending;
        sealed class Frame
        {
            internal readonly System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<object,object>> Dict;
            internal readonly System.Collections.Generic.IEnumerator<object> Array;
            internal readonly int Depth;
            internal Frame(GdDict value,int depth){Dict=value.GetEnumerator();Depth=depth;}
            internal Frame(GdArray value,int depth){Array=value.GetEnumerator();Depth=depth;}
        }
        internal ContinuousBootstrapTraversalCursor(GdDict ownedRoot){_frames.Push(new Frame(ownedRoot,0));VisitedNodes=1;}
        internal ContinuousTraversalStatus Advance(int maximumValues)
        {
            if(_terminal)return Status;if(maximumValues<0||maximumValues>64)return Refuse("invalid_capture_budget");
            int work=0;
            while(_frames.Count!=0&&work<maximumValues)
            {
                var f=_frames.Peek();object value;
                if(f.Dict!=null){if(!f.Dict.MoveNext()){f.Dict.Dispose();_frames.Pop();work++;continue;}value=f.Dict.Current.Value;}
                else{if(!f.Array.MoveNext()){f.Array.Dispose();_frames.Pop();work++;continue;}value=f.Array.Current;}
                work++;if(++VisitedNodes>MaximumNodes||f.Depth+1>MaximumDepth)return Refuse("capture_graph_bound");
                if(value is GdDict d)_frames.Push(new Frame(d,f.Depth+1));
                else if(value is GdArray a)_frames.Push(new Frame(a,f.Depth+1));
                else if(!(value==null||value is string||value is long||value is bool||value is double))return Refuse("unsupported_capture_value");
            }
            if(_frames.Count==0){_terminal=true;Status=ContinuousTraversalStatus.CompletePartialDiagnostic;}return Status;
        }
        ContinuousTraversalStatus Refuse(string reason){Reason=reason;Release();_terminal=true;Status=ContinuousTraversalStatus.Refused;return Status;}
        void Release(){while(_frames.Count!=0){var f=_frames.Pop();f.Dict?.Dispose();f.Array?.Dispose();}}
        internal void Cancel(){if(_terminal)return;Release();_terminal=true;Status=ContinuousTraversalStatus.Cancelled;Reason="cancelled";}
    }
}
