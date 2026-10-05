using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Owned actual genesis plus ONE historical producer: diagnostic, never SaveReady.
    // All source containers are private bootstrap-owned trees; all output shells are new.
    internal sealed class ContinuousVitalsOverlayCursor:IDisposable
    {
        readonly RunSession _session;
        readonly ActualVitalsProjectionBridge _bridge;
        readonly ProjectionPin _pin;
        readonly ProjectionCaptureCursor _capture;
        readonly Stack<Frame> _frames=new Stack<Frame>();
        readonly GdDict _output=new GdDict(),_vitals=new GdDict();
        ProjectionCaptureResult _result;
        int _phase,_entry,_nodes=23;
        bool _foundVitals,_disposed;
        internal ContinuousTraversalStatus Status{get;private set;}=ContinuousTraversalStatus.Pending;
        internal string Reason{get;private set;}="";
        internal int LastWorkUnits{get;private set;}
        internal ulong SourceRevision=>_pin.Revision;
        internal ulong SourceEpoch=>_pin.ReadinessEpoch;
        sealed class Frame
        {
            internal readonly IEnumerator<KeyValuePair<object,object>> Dict;
            internal readonly IEnumerator<object> Array;
            internal readonly GdDict TargetDict;
            internal readonly GdArray TargetArray;
            internal readonly int Depth;
            internal Frame(GdDict source,GdDict target,int depth){Dict=source.GetEnumerator();TargetDict=target;Depth=depth;}
            internal Frame(GdArray source,GdArray target,int depth){Array=source.GetEnumerator();TargetArray=target;Depth=depth;}
        }
        internal ContinuousVitalsOverlayCursor(GdDict ownedGenesis,RunSession session,ActualVitalsProjectionBridge bridge,ProjectionPin pin,ProjectionCaptureCursor capture)
        {_session=session;_bridge=bridge;_pin=pin;_capture=capture;_frames.Push(new Frame(ownedGenesis,_output,0));}
        bool SourceGuard()
        {lock(CommonParticipantGate.SyncRoot)return !_disposed&&_bridge.IsBoundActualSessionUnderGate(_session)&&(_result==null||_result.Status==ProjectionOutputStatus.CompletePartialDiagnostic);}
        internal ContinuousTraversalStatus Advance(int maximumUnits)
        {
            LastWorkUnits=0;
            if(maximumUnits<0||maximumUnits>64)return Refuse("invalid_capture_budget");
            if(Status==ContinuousTraversalStatus.Refused||Status==ContinuousTraversalStatus.Cancelled)return Status;
            if(!SourceGuard())return Refuse("continuous_vitals_source_revoked");
            if(Status!=ContinuousTraversalStatus.Pending)return Status;
            while(LastWorkUnits<maximumUnits&&Status==ContinuousTraversalStatus.Pending)
            {
                if(_phase==0)
                {
                    var state=_capture.Advance(1);LastWorkUnits++;
                    if(state==ProjectionCursorStatus.Complete)
                    {
                        _result=_capture.Result;
                        if(_result.NodeCount!=1||_result.Node(0).EntryCount!=21)return Refuse("continuous_vitals_projection_shape");
                        _phase=1;
                    }
                    else if(state!=ProjectionCursorStatus.Pending)return Refuse("continuous_vitals_projection_refused");
                    continue;
                }
                if(_phase==1)
                {
                    var row=_result.Entry(0,_entry++);LastWorkUnits++;
                    if(!(row.Key.ToNormalized() is string projectedKey)||row.Value.IsChild)return Refuse("continuous_vitals_projection_shape");
                    _vitals[projectedKey]=row.Value.Scalar.ToNormalized();
                    if(_entry==21)_phase=2;
                    continue;
                }
                if(_frames.Count==0)
                {
                    LastWorkUnits++;
                    if(!_foundVitals)return Refuse("continuous_vitals_field_missing");
                    Status=ContinuousTraversalStatus.CompletePartialDiagnostic;break;
                }
                var frame=_frames.Peek();object key=null,value;
                if(frame.Dict!=null)
                {
                    if(!frame.Dict.MoveNext()){frame.Dict.Dispose();_frames.Pop();LastWorkUnits++;continue;}
                    key=frame.Dict.Current.Key;value=frame.Dict.Current.Value;
                }
                else
                {
                    if(!frame.Array.MoveNext()){frame.Array.Dispose();_frames.Pop();LastWorkUnits++;continue;}
                    value=frame.Array.Current;
                }
                LastWorkUnits++;
                if(key is string keyText&&keyText.Length>256)return Refuse("continuous_overlay_key_bound");
                if(++_nodes>100000||frame.Depth+1>128)return Refuse("continuous_overlay_graph_bound");
                if(frame.Depth==0&&key is string rootKey&&rootKey=="vitals_summary")
                {_foundVitals=true;frame.TargetDict[key]=_vitals;continue;}
                object target=value;
                if(value is GdDict dict){if(dict.Count>256)return Refuse("continuous_overlay_container_bound");var child=new GdDict();target=child;_frames.Push(new Frame(dict,child,frame.Depth+1));}
                else if(value is GdArray array){if(array.Count>256)return Refuse("continuous_overlay_container_bound");var child=new GdArray();target=child;_frames.Push(new Frame(array,child,frame.Depth+1));}
                else if(!(value==null||value is string||value is long||value is bool||value is double))return Refuse("continuous_overlay_value");
                if(frame.TargetDict!=null)frame.TargetDict[key]=target;else frame.TargetArray.Add(target);
            }
            return Status;
        }
        // Closed scalar-only diagnostic observation; no mutable graph or actual model escapes.
        internal bool TryReadVitalsScalar(string key,out object value,out string reason)
        {
            value=null;reason="continuous_overlay_not_complete";
            if(Status!=ContinuousTraversalStatus.CompletePartialDiagnostic)return false;
            if(!SourceGuard()){Refuse("continuous_vitals_source_revoked");reason=Reason;return false;}
            if(key==null||!_vitals.Has(key)){reason="continuous_vitals_field_unknown";return false;}
            value=_vitals[key];reason="";return true;
        }
        internal bool TryBuildWholeWorldSave(out string reason)
        {reason="continuous_overlay_other_producers_not_enrolled";return false;}
        ContinuousTraversalStatus Refuse(string reason)
        {Reason=reason;Release();Status=ContinuousTraversalStatus.Refused;return Status;}
        void Release()
        {while(_frames.Count!=0){var f=_frames.Pop();f.Dict?.Dispose();f.Array?.Dispose();}_result?.Dispose();_capture.Dispose();_pin.Dispose();}
        internal void Cancel(){if(Status!=ContinuousTraversalStatus.Pending)return;Release();Status=ContinuousTraversalStatus.Cancelled;Reason="cancelled";}
        public void Dispose(){if(_disposed)return;_disposed=true;Release();if(Status==ContinuousTraversalStatus.Pending){Status=ContinuousTraversalStatus.Cancelled;Reason="disposed";}}
    }
}
