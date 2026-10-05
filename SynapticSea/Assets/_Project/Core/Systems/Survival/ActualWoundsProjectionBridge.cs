using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Diagnostic projection seam. Runtime evaluator/routing supplies transition authority separately.
    internal sealed class ActualWoundsProjectionBridge : IDisposable
    {
        readonly RunSession _session; readonly WoundState _actual; readonly GdArray _array;
        readonly object _scene; readonly string _run; readonly ParticipantProjectionRegistry _registry;
        readonly ProjectionNodeHandle _root, _rows;
        readonly ProjectionNodeHandle[] _rowNodes = new ProjectionNodeHandle[ContinuousWoundsValues.MaximumRows];
        GdDict[] _rowIdentities; ContinuousWoundsValues _values; object _generation = new object();
        ulong _stamp = 1; bool _disposed, _invalid; PreparedChange _pending; NativeOperationTicket _nativePending;
        internal ActualWoundsProjectionBridge(RunSession session, ProjectionLimits limits = null)
        {
            if (session == null || !session.PlayableStarted || session.SliceComplete || session.WoundState == null)
                throw new ArgumentException("actual_wounds_binding_unavailable");
            _session=session; _actual=session.WoundState; _array=_actual.Wounds; _scene=session.Scene; _run=session.RunIdInternal;
            if(!PlainContainers())throw new ArgumentException("protected_wounds_containers_unsupported");
            _values=ContinuousWoundsValues.CaptureExact(_actual); _rowIdentities=Identities(); _registry=new ParticipantProjectionRegistry(limits);
            try {
                if (!_registry.TryCreateNode(ProjectionNodeKind.Dictionary,out _root,out string reason) ||
                    !_registry.TryCreateNode(ProjectionNodeKind.Array,out _rows,out reason)) throw new InvalidOperationException(reason);
                for(int i=0;i<_rowNodes.Length;i++) if(!_registry.TryCreateNode(ProjectionNodeKind.Dictionary,out _rowNodes[i],out reason)) throw new InvalidOperationException(reason);
                if(!BuildPlan(_values,1,true,out var plan,out reason)) throw new InvalidOperationException(reason);
                using(plan) if(!_registry.TryPublish(plan,out reason)) throw new InvalidOperationException(reason);
            } catch {_registry.Dispose();throw;}
        }
        bool PlainContainers()
        {
            if(_array==null||_array.Owner!=null||_array.Count>ContinuousWoundsValues.MaximumRows)return false;
            for(int i=0;i<_array.Count;i++)if(!(_array[i] is GdDict row)||row.Owner!=null)return false;
            return true;
        }
        GdDict[] Identities() { var rows=new GdDict[_array.Count];for(int i=0;i<rows.Length;i++) rows[i]=(GdDict)_array[i];return rows; }
        bool LifecycleUnderGate() => !_disposed && ReferenceEquals(_session.WoundState,_actual) && ReferenceEquals(_actual.Wounds,_array) &&
            ReferenceEquals(_session.Scene,_scene) && _session.RunIdInternal==_run && !_session.ComponentGenerationRestoreInProgress;
        bool BoundUnderGate()
        {
            CommonParticipantGate.RequireHeld();if(_invalid||!LifecycleUnderGate())return false;
            bool same=PlainContainers()&&_values.MatchesRaw(_actual)&&_rowIdentities.Length==_array.Count;
            for(int i=0;same&&i<_rowIdentities.Length;i++)same=ReferenceEquals(_rowIdentities[i],_array[i]);
            if(!same){_invalid=true;_registry.Invalidate("actual_wounds_unregistered_writer");}return same;
        }
        internal bool SourceCurrentUnderGate(){CommonParticipantGate.RequireHeld();return BoundUnderGate();}
        internal bool IsBoundActualSessionUnderGate(RunSession session){CommonParticipantGate.RequireHeld();return ReferenceEquals(session,_session)&&BoundUnderGate();}
        internal Snapshot Read(){lock(CommonParticipantGate.SyncRoot){if(!BoundUnderGate())throw new InvalidOperationException("actual_wounds_binding_invalid");return new Snapshot(this,_values,_generation,_stamp,_registry.ReadinessEpoch);}}
        internal sealed class Snapshot
        {
            readonly ActualWoundsProjectionBridge _owner; readonly object _generation; readonly ulong _stamp,_epoch;
            internal readonly ContinuousWoundsValues Values;
            internal Snapshot(ActualWoundsProjectionBridge owner,ContinuousWoundsValues values,object generation,ulong stamp,ulong epoch)
            {_owner=owner;Values=values;_generation=generation;_stamp=stamp;_epoch=epoch;}
            internal bool MatchesUnderGate(ActualWoundsProjectionBridge owner)=>ReferenceEquals(owner,_owner)&&ReferenceEquals(_generation,owner._generation)&&_stamp==owner._stamp&&_epoch==owner._registry.ReadinessEpoch&&owner.BoundUnderGate()&&ReferenceEquals(Values,owner._values);
        }
        bool BuildPlan(ContinuousWoundsValues values,ulong stamp,bool rebuild,out ProjectionPreparationCursor plan,out string reason)
        {
            plan=null;var versions=new List<ProjectionNodeVersion>();var rows=new ProjectionEntry[values.Count];
            for(int i=0;i<values.Count;i++){
                var row=values.CopyRow(i);var fields=new ProjectionEntry[row.Count];int j=0;
                foreach(var entry in row)fields[j++]=new ProjectionEntry(ProjectionScalar.FromNormalized(entry.Key),new ProjectionValue(ProjectionScalar.FromNormalized(entry.Value)));
                if(!_rowNodes[i].TryCreateVersion(fields,out var version,out reason))return false;
                versions.Add(version);rows[i]=new ProjectionEntry(new ProjectionValue(_rowNodes[i].Id));
            }
            if(!_rows.TryCreateVersion(rows,out var rowVersion,out reason))return false;versions.Add(rowVersion);
            var summary=values.ExactScratch().GetSummary();var root=new ProjectionEntry[summary.Count];int at=0;
            foreach(var entry in summary)root[at++]=new ProjectionEntry(ProjectionScalar.FromNormalized(entry.Key),entry.Key as string=="wounds"?new ProjectionValue(_rows.Id):new ProjectionValue(ProjectionScalar.FromNormalized(entry.Value)));
            if(!_root.TryCreateVersion(root,out var rootVersion,out reason))return false;versions.Add(rootVersion);
            var descriptor=new ProjectionRootDescriptor("same-runsession-wounds-partial-v1","restricted-diagnostic-NOT-world-authority",stamp,new[]{new ProjectionRootBinding("wounds_summary",_root.Id)});
            if(!_registry.TryPrepare(versions.ToArray(),null,descriptor,out plan,out reason,rebuild))return false;
            int calls=0;while(plan.Status==ProjectionCursorStatus.Pending){plan.Advance(64);if(++calls>1024){plan.Dispose();plan=null;reason="wounds_prepare_bound";return false;}}
            if(plan.Status!=ProjectionCursorStatus.Complete){reason=plan.Reason;plan.Dispose();plan=null;return false;}return true;
        }
        internal bool PrepareNext(Snapshot expected,ContinuousWoundsValues next,out PreparedChange prepared,out string reason)
        {
            prepared=null;reason="actual_wounds_stale";ulong stamp;
            lock(CommonParticipantGate.SyncRoot){if(expected==null||next==null||!expected.MatchesUnderGate(this)||_stamp==ulong.MaxValue)return false;_pending?.Dispose();stamp=_stamp;}
            if(!BuildPlan(next,stamp+1,false,out var plan,out reason))return false;
            PreparedChange candidate;
            try { candidate=new PreparedChange(this,expected,next,plan); } catch { plan.Dispose(); throw; }
            lock(CommonParticipantGate.SyncRoot){if(!expected.MatchesUnderGate(this)||_pending!=null){candidate.Dispose();reason="actual_wounds_stale";return false;}_pending=candidate;prepared=candidate;return true;}
        }
        // Lifecycle-only CURRENT native operation ticket. No capped values/history clone after revocation.
        internal NativeSnapshot ReadNativeContinuation()
        {lock(CommonParticipantGate.SyncRoot){if(!LifecycleUnderGate())throw new InvalidOperationException("wounds_native_lifecycle_changed");return new NativeSnapshot(this,_generation);}}
        internal sealed class NativeSnapshot
        {
            readonly ActualWoundsProjectionBridge _owner;readonly object _generation;
            internal NativeSnapshot(ActualWoundsProjectionBridge owner,object generation){_owner=owner;_generation=generation;}
            internal bool MatchesUnderGate(ActualWoundsProjectionBridge owner)
            {CommonParticipantGate.RequireHeld();return ReferenceEquals(owner,_owner)&&ReferenceEquals(_generation,owner._generation)&&owner.LifecycleUnderGate();}
        }
        internal bool PrepareNativeContinuation(NativeSnapshot expected,ContinuousWoundsNativeOperation operation,out NativeOperationTicket ticket,out string reason)
        {
            ticket=null;reason="wounds_native_stale";if(expected==null||operation==null||!operation.IsIssued)return false;
            lock(CommonParticipantGate.SyncRoot)
            {
                if(!expected.MatchesUnderGate(this))return false;
                _nativePending?.Dispose();var candidate=new NativeOperationTicket(this,expected,operation);
                _nativePending=candidate;ticket=candidate;reason="";return true;
            }
        }
        internal sealed class NativeOperationTicket:IDisposable
        {
            readonly ActualWoundsProjectionBridge _owner;readonly NativeSnapshot _before;readonly ContinuousWoundsNativeOperation _operation;
            readonly object _nextGeneration=new object();bool _used;
            internal NativeOperationTicket(ActualWoundsProjectionBridge owner,NativeSnapshot before,ContinuousWoundsNativeOperation operation)
            {_owner=owner;_before=before;_operation=operation;}
            internal bool MatchesUnderGate(){CommonParticipantGate.RequireHeld();return !_used&&ReferenceEquals(_owner._nativePending,this)&&_before.MatchesUnderGate(_owner)&&_operation.IsIssued;}
            internal bool TryApplyActualUnderGate(out long healed,out string woundId,out string reason)
            {
                CommonParticipantGate.RequireHeld();healed=0;woundId="";reason="wounds_native_stale";if(!MatchesUnderGate())return false;
                // No capture/rollback promise: retire authority and token BEFORE native operation.
                _owner._pending?.Dispose();if(!_owner._invalid){_owner._invalid=true;_owner._registry.Invalidate("wounds_native_noncapturing_continuation");}
                _owner._generation=_nextGeneration;_owner._nativePending=null;_used=true;
                // May throw after partial stock native operation; caller faults world batch, never restores history.
                _operation.ApplyToActual(_owner._actual,out healed,out woundId);reason="";return true;
            }
            public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(ReferenceEquals(_owner._nativePending,this))_owner._nativePending=null;_used=true;}}
        }
        internal bool TryPin(out ProjectionPin pin,out string reason){lock(CommonParticipantGate.SyncRoot){if(!BoundUnderGate()||_pending!=null){pin=null;reason="actual_wounds_capture_unavailable";return false;}return _registry.TryPin(out pin,out reason);}}
        internal sealed class PreparedChange:IDisposable
        {
            readonly ActualWoundsProjectionBridge _owner;readonly Snapshot _before;readonly ContinuousWoundsValues _next;readonly ProjectionPreparationCursor _plan;
            readonly GdDict[] _rows;readonly GdDict.Storage[] _backings;readonly List<object> _arrayBacking;readonly object _nextGeneration=new object();bool _used;
            internal PreparedChange(ActualWoundsProjectionBridge owner,Snapshot before,ContinuousWoundsValues next,ProjectionPreparationCursor plan)
            {
                _owner=owner;_before=before;_next=next;_plan=plan;_rows=new GdDict[next.Count];_backings=new GdDict.Storage[next.Count];_arrayBacking=new List<object>(next.Count);
                var old=new Dictionary<string,GdDict>(StringComparer.Ordinal);
                foreach(var row in owner._rowIdentities){var id=(string)row.Get("wound_id");if(old.ContainsKey(id))throw new ArgumentException("duplicate_wound_id");old.Add(id,row);}
                for(int i=0;i<next.Count;i++){var copy=next.CopyRow(i);var id=(string)copy.Get("wound_id");_rows[i]=old.TryGetValue(id,out var row)?row:copy;_backings[i]=copy.RawStorage;_arrayBacking.Add(_rows[i]);}
            }
            internal bool MatchesUnderGate(){CommonParticipantGate.RequireHeld();return !_used&&ReferenceEquals(_owner._pending,this)&&_before.MatchesUnderGate(_owner)&&_plan.Status==ProjectionCursorStatus.Complete;}
            internal bool TryInstallActualRawAndProjectionUnderGate(out string reason)
            {
                CommonParticipantGate.RequireHeld();if(!MatchesUnderGate()){reason="actual_wounds_final_guard";return false;}if(!_owner._registry.TryPublish(_plan,out reason))return false;
                for(int i=0;i<_rows.Length;i++)_rows[i].RawStorage=_backings[i];
                _owner._array.RawStorage=_arrayBacking;_owner._actual.InstallContinuousNextId(_next.NextId);
                _owner._rowIdentities=_rows;_owner._values=_next;_owner._generation=_nextGeneration;_owner._stamp++;_owner._pending=null;_used=true;reason="";return true;
            }
            public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(ReferenceEquals(_owner._pending,this))_owner._pending=null;_used=true;_plan.Dispose();}}
        }
        public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(_disposed)return;_disposed=true;_pending?.Dispose();_nativePending?.Dispose();_registry.Dispose();}}
    }
}
