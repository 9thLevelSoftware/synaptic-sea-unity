using System;
using System.Threading;

namespace SynapticSea.Core.Variant
{
    internal sealed class ProjectionPin : IDisposable
    {
        readonly ParticipantProjectionRegistry _registry;
        ProjectionVersionTable _table;
        ProjectionRootDescriptor _descriptor;
        ProjectionGraphPlan _plan;
        ProjectionCaptureCursor _cursor;
        ProjectionPinStatus _status=ProjectionPinStatus.Active;
        readonly long _sourceUnits;
        internal ulong Revision{get;}
        internal ulong ReadinessEpoch{get;}
        internal ProjectionPinStatus Status{get{lock(CommonParticipantGate.SyncRoot){if(_status==ProjectionPinStatus.Active)_registry.CutEpochCurrentUnderGate(ReadinessEpoch);return _status;}}}
        internal string Reason{get;private set;}="";
        internal ProjectionPin(ParticipantProjectionRegistry registry,ProjectionVersionTable table,ProjectionRootDescriptor descriptor,ProjectionGraphPlan plan,ulong revision,ulong epoch)
        {_registry=registry;_table=table;_descriptor=descriptor;_plan=plan;_sourceUnits=ParticipantProjectionRegistry.OwnedTableUnits(table);Revision=revision;ReadinessEpoch=epoch;}
        internal bool IsActiveUnderGate=>_status==ProjectionPinStatus.Active&&_registry.CutEpochCurrentUnderGate(ReadinessEpoch);
        internal bool TryCreateCursor(out ProjectionCaptureCursor cursor,out string reason)
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                cursor=null;
                if(!IsActiveUnderGate){reason="projection_pin_inactive";return false;}
                if(_cursor!=null){reason="projection_pin_cursor_exists";return false;}
                if(!_registry.TryClaimOutputUnderGate(out reason))return false;
                cursor=new ProjectionCaptureCursor(_registry,this,_table,_descriptor,_plan);_cursor=cursor;reason="";return true;
            }
        }
        internal ProjectionCaptureResult CompleteUnderGate(ProjectionCaptureCursor cursor,ProjectionCapturedIndex index,int copyUnits)
        {
            if(!IsActiveUnderGate||!ReferenceEquals(cursor,_cursor))throw new InvalidOperationException("projection_pin_completion_refused");
            var result=new ProjectionCaptureResult(_registry,_table,_descriptor,_plan,index,Revision,ReadinessEpoch,_sourceUnits+copyUnits);
            _registry.ReleasePinUnderGate(this,_sourceUnits,true);_registry.AddOutputUnderGate(result);
            _status=ProjectionPinStatus.Completed;_cursor=null;_table=null;_descriptor=null;_plan=null;return result;
        }
        internal void CancelUnderGate(string reason)
        {
            if(_status!=ProjectionPinStatus.Active)return;
            _status=ProjectionPinStatus.Cancelled;Reason=reason;_cursor?.CancelUnderGate(reason);_cursor=null;
            _registry.ReleasePinUnderGate(this,_sourceUnits,false);_table=null;_descriptor=null;_plan=null;
        }
        public void Dispose()
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                if(_status==ProjectionPinStatus.Released)return;
                CancelUnderGate("projection_pin_released");_status=ProjectionPinStatus.Released;
            }
        }
    }
    internal readonly struct ProjectionSegmentHeader
    {
        internal readonly ProjectionNodeId Node;
        internal readonly ulong Version;
        internal readonly ProjectionNodeKind Kind;
        internal readonly int EntryCount,PageIndex;
        internal ProjectionSegmentHeader(ProjectionNodeVersion node,int page){Node=node.Id;Version=node.Version;Kind=node.Kind;EntryCount=node.EntryCount;PageIndex=page;}
    }
    internal sealed class ProjectionCapturedSegment
    {
        internal readonly ProjectionSegmentHeader Header;
        internal readonly ProjectionEntryPage Page;
        internal ProjectionCapturedSegment(ProjectionNodeVersion node,int page){Header=new ProjectionSegmentHeader(node,page);Page=page<0?null:node.GetPage(page).Copy();}
    }
    internal sealed class ProjectionCapturedIndex
    {
        // Four fixed radix32 levels address at most 4096*(1+128) captured segments.
        readonly Page _root;
        internal int Count{get;}
        internal const int ConservativeUnitsPerSegment=6;
        internal ProjectionCapturedIndex(){ }
        ProjectionCapturedIndex(Page root,int count){_root=root;Count=count;}
        internal ProjectionCapturedIndex Append(ProjectionCapturedSegment segment)
        {if(Count>=4096*129)throw new InvalidOperationException("projection_segment_capacity");return new ProjectionCapturedIndex(Update(_root,Count,3,segment),Count+1);}
        internal ProjectionCapturedSegment Get(int index)
        {if(index<0||index>=Count)throw new ArgumentOutOfRangeException(nameof(index));var page=_root;for(int level=3;level>0;level--)page=page.Children[(index>>(level*5))&31];return page.Values[index&31];}
        static Page Update(Page before,int index,int level,ProjectionCapturedSegment segment)
        {
            if(level==0){var values=before==null?new ProjectionCapturedSegment[32]:(ProjectionCapturedSegment[])before.Values.Clone();values[index&31]=segment;return new Page(values);}
            var children=before==null?new Page[32]:(Page[])before.Children.Clone();int at=(index>>(level*5))&31;children[at]=Update(children[at],index,level-1,segment);return new Page(children);
        }
        sealed class Page
        {
            internal readonly Page[] Children;
            internal readonly ProjectionCapturedSegment[] Values;
            internal Page(Page[] values){Children=values;}
            internal Page(ProjectionCapturedSegment[] values){Values=values;}
        }
    }
    internal sealed class ProjectionCaptureCursor : IDisposable
    {
        readonly ParticipantProjectionRegistry _registry;
        readonly ProjectionPin _pin;
        readonly int _thread=Thread.CurrentThread.ManagedThreadId;
        ProjectionVersionTable _table;
        ProjectionRootDescriptor _descriptor;
        ProjectionGraphPlan _plan;
        ProjectionCapturedIndex _index=new ProjectionCapturedIndex();
        ProjectionNodeVersion _current;
        int _nodeIndex,_page=-1,_copyUnits;
        ProjectionCursorStatus _status=ProjectionCursorStatus.Pending;
        ProjectionCaptureResult _result;
        internal ProjectionCursorStatus Status{get{lock(CommonParticipantGate.SyncRoot)return _status;}}
        internal ProjectionCaptureResult Result{get{lock(CommonParticipantGate.SyncRoot)return _result;}}
        internal string Reason{get;private set;}="";
        internal int LastWorkUnits{get;private set;}
        internal ProjectionCaptureCursor(ParticipantProjectionRegistry registry,ProjectionPin pin,ProjectionVersionTable table,ProjectionRootDescriptor descriptor,ProjectionGraphPlan plan)
        {_registry=registry;_pin=pin;_table=table;_descriptor=descriptor;_plan=plan;}
        internal ProjectionCursorStatus Advance(int units)
        {
            if(Thread.CurrentThread.ManagedThreadId!=_thread)throw new InvalidOperationException("projection_cursor_thread");
            if(units<1||units>64)throw new ArgumentOutOfRangeException(nameof(units));
            lock(CommonParticipantGate.SyncRoot)
            {
                LastWorkUnits=0;
                if(_status!=ProjectionCursorStatus.Pending)return _status;
                if(!_pin.IsActiveUnderGate){CancelUnderGate("projection_pin_inactive");return _status;}
                while(LastWorkUnits<units&&_status==ProjectionCursorStatus.Pending)
                {
                    LastWorkUnits++;
                    if(_nodeIndex==_plan.NodeCount)
                    {
                        _result=_pin.CompleteUnderGate(this,_index,_copyUnits);_copyUnits=0;_status=ProjectionCursorStatus.Complete;
                        DropData();break;
                    }
                    if(!_registry.TryRetainCopyUnderGate(ProjectionCapturedIndex.ConservativeUnitsPerSegment))
                    {_pin.CancelUnderGate("projection_capture_retention_pressure");break;}
                    _copyUnits+=ProjectionCapturedIndex.ConservativeUnitsPerSegment;
                    if(_page<0)_current=_table.Get(_plan.Node(_nodeIndex));
                    _index=_index.Append(new ProjectionCapturedSegment(_current,_page));
                    _page++;
                    if(_page==_current.PageCount){_nodeIndex++;_page=-1;_current=null;}
                }
                return _status;
            }
        }
        internal void CancelUnderGate(string reason)
        {
            if(_status!=ProjectionCursorStatus.Pending)return;
            _status=ProjectionCursorStatus.Cancelled;Reason=reason;_registry.ReleaseCopiesUnderGate(_copyUnits);_copyUnits=0;_registry.ReleaseOutputClaimUnderGate();DropData();
        }
        void DropData(){_table=null;_descriptor=null;_plan=null;_index=null;_current=null;}
        public void Dispose()
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                if(_status==ProjectionCursorStatus.Released)return;
                if(_status==ProjectionCursorStatus.Pending)_pin.CancelUnderGate("projection_cursor_released");
                _status=ProjectionCursorStatus.Released;_result=null;DropData();
            }
        }
    }
    internal sealed class ProjectionCaptureResult : IDisposable
    {
        readonly ParticipantProjectionRegistry _registry;
        ProjectionVersionTable _table;
        ProjectionRootDescriptor _descriptor;
        ProjectionGraphPlan _plan;
        ProjectionCapturedIndex _index;
        readonly long _ownedUnits;
        ProjectionOutputStatus _status=ProjectionOutputStatus.CompletePartialDiagnostic;
        internal ulong Revision{get;}
        internal ulong ReadinessEpoch{get;}
        internal ProjectionOutputStatus Status{get{lock(CommonParticipantGate.SyncRoot){if(_status==ProjectionOutputStatus.CompletePartialDiagnostic)_registry.CutEpochCurrentUnderGate(ReadinessEpoch);return _status;}}}
        internal string Reason{get;private set;}="";
        internal ProjectionCaptureResult(ParticipantProjectionRegistry registry,ProjectionVersionTable table,ProjectionRootDescriptor descriptor,ProjectionGraphPlan plan,ProjectionCapturedIndex index,ulong revision,ulong epoch,long ownedUnits)
        {_registry=registry;_table=table;_descriptor=descriptor;_plan=plan;_index=index;Revision=revision;ReadinessEpoch=epoch;_ownedUnits=ownedUnits;}
        void RequireReadable(){if(_status!=ProjectionOutputStatus.CompletePartialDiagnostic||!_registry.CutEpochCurrentUnderGate(ReadinessEpoch))throw new InvalidOperationException("projection_output_inactive");}
        internal int NodeCount{get{lock(CommonParticipantGate.SyncRoot){RequireReadable();return _plan.NodeCount;}}}
        internal ProjectionRootBinding Root(int index){lock(CommonParticipantGate.SyncRoot){RequireReadable();return _descriptor.Root(index);}}
        internal ProjectionScalarBinding Scalar(int index){lock(CommonParticipantGate.SyncRoot){RequireReadable();return _descriptor.Scalar(index);}}
        internal ProjectionSegmentHeader Node(int index){lock(CommonParticipantGate.SyncRoot){RequireReadable();return _index.Get(_plan.Offset(index)).Header;}}
        internal ProjectionEntry Entry(int nodeIndex,int entryIndex)
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                RequireReadable();int offset=_plan.Offset(nodeIndex);var header=_index.Get(offset).Header;
                if(entryIndex<0||entryIndex>=header.EntryCount)throw new ArgumentOutOfRangeException(nameof(entryIndex));
                return _index.Get(offset+1+entryIndex/32).Page[entryIndex%32];
            }
        }
        internal bool TryBuildWholeWorldSave(out string reason){reason="participants_not_fully_enrolled";return false;}
        internal void RevokeUnderGate(string reason)
        {
            if(_status!=ProjectionOutputStatus.CompletePartialDiagnostic)return;
            _status=ProjectionOutputStatus.Revoked;Reason=reason;_registry.ReleaseOutputUnderGate(this,_ownedUnits);DropData();
        }
        void DropData(){_table=null;_descriptor=null;_plan=null;_index=null;}
        public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(_status==ProjectionOutputStatus.Released)return;RevokeUnderGate("projection_output_released");_status=ProjectionOutputStatus.Released;}}
    }
}
