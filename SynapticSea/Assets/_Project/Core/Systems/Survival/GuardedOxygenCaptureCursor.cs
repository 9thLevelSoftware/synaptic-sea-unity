using System;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Source-guarded historical diagnostic data only, never a complete world/save receipt.
    internal sealed class GuardedOxygenCaptureCursor:IDisposable
    {
        readonly ActualOxygenProjectionBridge _owner;
        readonly ProjectionPin _pin;
        readonly ProjectionCaptureCursor _cursor;
        ProjectionCaptureResult _result;
        bool _disposed;
        internal ContinuousTraversalStatus Status{get;private set;}=ContinuousTraversalStatus.Pending;
        internal string Reason{get;private set;}="";
        internal int LastWorkUnits{get;private set;}
        internal ulong SourceRevision=>_pin.Revision;
        GuardedOxygenCaptureCursor(ActualOxygenProjectionBridge owner,ProjectionPin pin,ProjectionCaptureCursor cursor){_owner=owner;_pin=pin;_cursor=cursor;}
        internal static bool TryBegin(ActualOxygenProjectionBridge owner,out GuardedOxygenCaptureCursor capture,out string reason)
        {
            capture=null;reason="actual_oxygen_capture_unavailable";if(owner==null)return false;
            lock(CommonParticipantGate.SyncRoot)
            {
                if(!owner.SourceCurrentUnderGate()||!owner.TryPin(out var pin,out reason))return false;
                if(!pin.TryCreateCursor(out var cursor,out reason)){pin.Dispose();return false;}
                capture=new GuardedOxygenCaptureCursor(owner,pin,cursor);reason="";return true;
            }
        }
        bool Guard()
        {lock(CommonParticipantGate.SyncRoot)return !_disposed&&_owner.SourceCurrentUnderGate()&&(_result==null||_result.Status==ProjectionOutputStatus.CompletePartialDiagnostic);}
        internal ContinuousTraversalStatus Advance(int units)
        {
            LastWorkUnits=0;if(units<0||units>64)return Refuse("invalid_capture_budget");
            if(Status==ContinuousTraversalStatus.Refused||Status==ContinuousTraversalStatus.Cancelled)return Status;
            if(!Guard())return Refuse("actual_oxygen_source_revoked");
            if(Status!=ContinuousTraversalStatus.Pending||units==0)return Status;
            var state=_cursor.Advance(units);LastWorkUnits=_cursor.LastWorkUnits;
            if(state==ProjectionCursorStatus.Complete)
            {_result=_cursor.Result;Status=ContinuousTraversalStatus.CompletePartialDiagnostic;}
            else if(state!=ProjectionCursorStatus.Pending)return Refuse("actual_oxygen_capture_refused");return Status;
        }
        bool Readable(out string reason)
        {reason="actual_oxygen_capture_not_complete";if(Status!=ContinuousTraversalStatus.CompletePartialDiagnostic)return false;if(!Guard()){Refuse("actual_oxygen_source_revoked");reason=Reason;return false;}reason="";return true;}
        internal bool TryReadScalar(string key,out object value,out string reason)
        {
            value=null;if(!Readable(out reason))return false;
            if(key==null||key.Length>256){reason="actual_oxygen_field_unknown";return false;}
            for(int n=0;n<_result.NodeCount;n++)
            {
                var node=_result.Node(n);if(node.Kind!=ProjectionNodeKind.Dictionary)continue;
                for(int i=0;i<node.EntryCount;i++){var row=_result.Entry(n,i);if(row.Key.ToNormalized() is string name&&StringComparer.Ordinal.Equals(key,name)&&!row.Value.IsChild){value=row.Value.Scalar.ToNormalized();return true;}}
            }
            reason="actual_oxygen_field_unknown";return false;
        }
        internal bool TryReadZone(int index,out string zone,out string reason)
        {
            zone=null;if(!Readable(out reason))return false;
            for(int n=0;n<_result.NodeCount;n++){var node=_result.Node(n);if(node.Kind!=ProjectionNodeKind.Array)continue;if(index<0||index>=node.EntryCount)break;zone=_result.Entry(n,index).Value.Scalar.ToNormalized() as string;return zone!=null;}
            reason="actual_oxygen_zone_index";return false;
        }
        internal bool TryBuildWholeWorldSave(out string reason){reason="other_world_producers_not_enrolled";return false;}
        ContinuousTraversalStatus Refuse(string reason){Reason=reason;Release();Status=ContinuousTraversalStatus.Refused;return Status;}
        void Release(){_result?.Dispose();_cursor.Dispose();_pin.Dispose();}
        internal void Cancel(){if(Status!=ContinuousTraversalStatus.Pending)return;Release();Status=ContinuousTraversalStatus.Cancelled;Reason="cancelled";}
        public void Dispose(){if(_disposed)return;_disposed=true;Release();if(Status==ContinuousTraversalStatus.Pending){Status=ContinuousTraversalStatus.Cancelled;Reason="disposed";}}
    }
}
