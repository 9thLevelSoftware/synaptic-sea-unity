using System;
using System.Collections.Generic;
using System.Threading;

namespace SynapticSea.Core.Variant
{
    internal sealed class ProjectionLimits
    {
        internal readonly int MaximumNodes, MaximumEntriesPerNode, MaximumPins, MaximumOutputs;
        internal readonly long MaximumRetainedUnits;
        internal readonly ulong MaximumNodeId, MaximumEpoch;
        internal ProjectionLimits(int nodes=4096,int entries=4096,int pins=8,int outputs=8,long retainedUnits=1000000,ulong maximumNodeId=ulong.MaxValue,ulong maximumEpoch=ulong.MaxValue)
        {
            if(nodes<1||nodes>4096||entries<0||entries>4096||pins<1||pins>16||outputs<1||outputs>16||retainedUnits<1||maximumNodeId==0||maximumEpoch==0)throw new ArgumentException("projection_limits");
            MaximumNodes=nodes;MaximumEntriesPerNode=entries;MaximumPins=pins;MaximumOutputs=outputs;MaximumRetainedUnits=retainedUnits;MaximumNodeId=maximumNodeId;MaximumEpoch=maximumEpoch;
        }
    }
    internal readonly struct ProjectionRootBinding
    {
        internal readonly string Name;
        internal readonly ProjectionNodeId Node;
        internal ProjectionRootBinding(string name,ProjectionNodeId node)
        {CheckName(name);if(node.Registry==0||node.Value==0)throw new ArgumentException("invalid_projection_root");Name=name;Node=node;}
        internal static void CheckName(string value){if(string.IsNullOrEmpty(value)||value.Length>1024)throw new ArgumentException("projection_identity_capacity");}
    }
    internal readonly struct ProjectionScalarBinding
    {
        internal readonly string Name;
        internal readonly ProjectionScalar Value;
        internal ProjectionScalarBinding(string name,ProjectionScalar value){ProjectionRootBinding.CheckName(name);Name=name;Value=value;}
    }
    internal sealed class ProjectionRootDescriptor
    {
        readonly ProjectionRootBinding[] _roots;
        readonly ProjectionScalarBinding[] _scalars;
        internal string CohortIdentity{get;}
        internal string PolicyIdentity{get;}
        internal ulong OwnerStamp{get;}
        internal int RootCount=>_roots.Length;
        internal int ScalarCount=>_scalars.Length;
        internal ProjectionRootDescriptor(string cohort,string policy,ulong ownerStamp,ProjectionRootBinding[] roots,ProjectionScalarBinding[] scalars=null)
        {
            ProjectionRootBinding.CheckName(cohort);ProjectionRootBinding.CheckName(policy);
            if(roots==null||roots.Length>16||scalars!=null&&scalars.Length>32)throw new ArgumentException("projection_descriptor_capacity");
            CohortIdentity=cohort;PolicyIdentity=policy;OwnerStamp=ownerStamp;_roots=(ProjectionRootBinding[])roots.Clone();_scalars=scalars==null?Array.Empty<ProjectionScalarBinding>():(ProjectionScalarBinding[])scalars.Clone();
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var root in _roots){ProjectionRootBinding.CheckName(root.Name);if(root.Node.Value==0||root.Node.Registry==0||!names.Add(root.Name))throw new ArgumentException("duplicate_or_invalid_projection_root");}
            names.Clear();foreach(var scalar in _scalars){ProjectionRootBinding.CheckName(scalar.Name);if(!names.Add(scalar.Name))throw new ArgumentException("duplicate_projection_scalar");}
        }
        internal ProjectionRootBinding Root(int index)=>_roots[index];
        internal ProjectionScalarBinding Scalar(int index)=>_scalars[index];
    }
    internal enum ProjectionReadiness { RebuildRequired, ReadyPartialDiagnostic, Capacity, Exhausted, Released }
    internal enum ProjectionCursorStatus { Pending, Complete, Refused, Cancelled, Released }
    internal enum ProjectionPinStatus { Active, Completed, Cancelled, Released }
    internal enum ProjectionOutputStatus { CompletePartialDiagnostic, Revoked, Released }
    internal sealed partial class ProjectionNodeHandle
    {
        readonly ParticipantProjectionRegistry _registry;
        readonly ProjectionNodeKind _kind;
        readonly ProjectionNodeIssuer _issuer;
        ulong _version;
        bool _retired;
        internal ProjectionNodeId Id{get;}
        internal ProjectionNodeHandle(ParticipantProjectionRegistry registry,ProjectionNodeId id,ProjectionNodeKind kind,ProjectionNodeIssuer issuer){_registry=registry;Id=id;_kind=kind;_issuer=issuer;}
        internal bool TryCreateVersion(ProjectionEntry[] entries,out ProjectionNodeVersion version,out string reason)
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                version=null;
                if(_retired||_registry.IsUnavailableUnderGate||!_registry.HandleCurrentUnderGate(Id,_issuer)){reason="retired_projection_node";return false;}
                if(_version==ulong.MaxValue){_registry.InvalidateUnderGate(ProjectionReadiness.Exhausted,"projection_version_exhausted");reason="projection_version_exhausted";return false;}
                // Only closed typed entries are accepted; no caller code or model accessors run here.
                _registry.CheckMaintenanceLifetimeCanAdvanceUnderGate();
                version=new ProjectionNodeVersion(Id,_version+1,_kind,entries,_registry.Limits.MaximumEntriesPerNode,_issuer);
                _version++;_registry.RecordIssuedUnderGate(_issuer,version);reason="";return true;
            }
        }
        internal bool TryReplaceValue(ProjectionNodeVersion before,int index,ProjectionValue value,out ProjectionNodeVersion version,out string reason)
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                version=null;
                if(_retired||_registry.IsUnavailableUnderGate||!_registry.HandleCurrentUnderGate(Id,_issuer)){reason="retired_projection_node";return false;}
                if(before==null||!before.Id.Equals(Id)||before.Kind!=_kind||!before.HasIssuer(_issuer)||before.Version>_version)throw new ArgumentException("projection_entry_update_binding");
                if(_version==ulong.MaxValue){_registry.InvalidateUnderGate(ProjectionReadiness.Exhausted,"projection_version_exhausted");reason="projection_version_exhausted";return false;}
                _registry.CheckMaintenanceLifetimeCanAdvanceUnderGate();
                version=before.ReplaceValue(_version+1,index,value);_version++;_registry.RecordIssuedUnderGate(_issuer,version);reason="";return true;
            }
        }
        internal bool TryRetire(out string reason)
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                if(_retired||_registry.IsReleasedUnderGate){_retired=true;reason="";return true;}
                if(_registry.ContainsUnderGate(Id)){reason="projection_node_still_indexed";return false;}
                _registry.CheckMaintenanceLifetimeCanAdvanceUnderGate();
                _retired=true;_registry.RetireHandleUnderGate(Id);reason="";return true;
            }
        }
    }
    internal sealed partial class ParticipantProjectionRegistry : IDisposable
    {
        static ulong _lastRegistryIdentity;
        readonly ulong _identity;
        readonly List<ProjectionPin> _pins;
        readonly List<ProjectionCaptureResult> _outputs;
        Dictionary<ulong,Lifetime> _lifetimes;
        ulong _maintenanceLifetimeEpoch;
        ProjectionVersionTable _table;
        ProjectionRootDescriptor _descriptor;
        ProjectionGraphPlan _plan;
        ProjectionPreparationCursor _preparation;
        ulong _lastNodeId,_revision,_readinessEpoch=1;
        int _activeHandles,_outputClaims;
        long _retainedUnits,_currentUnits,_preparedUnits;
        ProjectionReadiness _readiness=ProjectionReadiness.RebuildRequired;
        internal ProjectionLimits Limits{get;}
        internal ParticipantProjectionRegistry(ProjectionLimits limits=null)
        {
            Limits=limits??new ProjectionLimits();
            _pins=new List<ProjectionPin>(Limits.MaximumPins);_outputs=new List<ProjectionCaptureResult>(Limits.MaximumOutputs);_lifetimes=new Dictionary<ulong,Lifetime>(Limits.MaximumNodes);
            lock(CommonParticipantGate.SyncRoot)
            {
                if(_lastRegistryIdentity==ulong.MaxValue)throw new InvalidOperationException("projection_registry_identity_exhausted");
                _identity=++_lastRegistryIdentity;_table=new ProjectionVersionTable(_identity);
            }
        }
        internal ProjectionReadiness Readiness{get{lock(CommonParticipantGate.SyncRoot){OwnerLeaseCurrentUnderGate();return _readiness;}}}
        internal ulong Revision{get{lock(CommonParticipantGate.SyncRoot)return _revision;}}
        internal ulong ReadinessEpoch{get{lock(CommonParticipantGate.SyncRoot)return _readinessEpoch;}}
        internal long RetainedUnits{get{lock(CommonParticipantGate.SyncRoot)return _retainedUnits;}}
        internal bool IsUnavailableUnderGate=>IsReleasedUnderGate||_readiness==ProjectionReadiness.Exhausted;
        internal bool IsReleasedUnderGate=>_readiness==ProjectionReadiness.Released;
        internal static long OwnedTableUnits(ProjectionVersionTable table)=>table.Units+3L*table.Count+1;
        internal bool HandleCurrentUnderGate(ProjectionNodeId id, ProjectionNodeIssuer issuer)
            => _lifetimes != null && _lifetimes.TryGetValue(id.Value, out var life) && ReferenceEquals(life.Issuer, issuer);
        internal bool ContainsUnderGate(ProjectionNodeId id)=>_table?.Get(id)!=null;
        internal void RetireHandleUnderGate(ProjectionNodeId id){CommonParticipantGate.RequireHeld();AdvanceMaintenanceLifetimeEpochUnderGate();_preparation?.CancelUnderGate("projection_node_lifetime_changed");if(_lifetimes!=null&&_lifetimes.Remove(id.Value)) { _activeHandles--; if (_maintenanceAuxiliaryCharged) { _currentUnits--; _retainedUnits--; } }}
        internal void RecordIssuedUnderGate(ProjectionNodeIssuer issuer,ProjectionNodeVersion version)
        {
            CommonParticipantGate.RequireHeld();var before=_lifetimes[version.Id.Value];
            if(!ReferenceEquals(issuer,before.Issuer)||!version.HasIssuer(issuer)||version.Version<=before.LastIssued)throw new InvalidOperationException("invalid_projection_issuance");
            AdvanceMaintenanceLifetimeEpochUnderGate();
            before.Issued.SetTarget(version);_lifetimes[version.Id.Value]=new Lifetime(before.Kind,before.LastAdmitted,version.Version,before.Issuer,before.Issued);
        }
        internal readonly struct Lifetime { internal readonly ProjectionNodeKind Kind; internal readonly ulong LastAdmitted,LastIssued; internal readonly ProjectionNodeIssuer Issuer; internal readonly WeakReference<ProjectionNodeVersion> Issued; internal Lifetime(ProjectionNodeKind kind,ulong version,ulong issued,ProjectionNodeIssuer issuer,WeakReference<ProjectionNodeVersion> exact){Kind=kind;LastAdmitted=version;LastIssued=issued;Issuer=issuer;Issued=exact;} }
        internal bool TryCreateNode(ProjectionNodeKind kind,out ProjectionNodeHandle handle,out string reason)
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                handle=null;
                if(_readiness==ProjectionReadiness.Released||_readiness==ProjectionReadiness.Exhausted){reason="projection_registry_unavailable";return false;}
                if(kind!=ProjectionNodeKind.Dictionary&&kind!=ProjectionNodeKind.Array)throw new ArgumentException("projection_node_kind");
                if(_lastNodeId==Limits.MaximumNodeId){if (_ownerCohort == null) InvalidateUnderGate(ProjectionReadiness.Exhausted,"projection_node_id_exhausted");reason="projection_node_id_exhausted";return false;}
                if(_activeHandles==Limits.MaximumNodes){if (_ownerCohort == null) InvalidateUnderGate(ProjectionReadiness.Capacity,"projection_handle_capacity");reason="projection_handle_capacity";return false;}
                if (_maintenanceAuxiliaryCharged && _retainedUnits == Limits.MaximumRetainedUnits) { reason = "maintenance_ledger_retention_pressure"; return false; }
                AdvanceMaintenanceLifetimeEpochUnderGate();
                if (_maintenanceAuxiliaryCharged) { _currentUnits++; _retainedUnits++; }
                var issuer=new ProjectionNodeIssuer();handle=new ProjectionNodeHandle(this,new ProjectionNodeId(_identity,++_lastNodeId),kind,issuer);_lifetimes.Add(_lastNodeId,new Lifetime(kind,0,0,issuer,new WeakReference<ProjectionNodeVersion>(null)));_activeHandles++;reason="";return true;
            }
        }
        internal bool TryPrepare(ProjectionNodeVersion[] changes,ProjectionNodeId[] removals,ProjectionRootDescriptor descriptor,out ProjectionPreparationCursor cursor,out string reason,bool rebuild=false)
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                if (_maintenancePublication != null) { cursor = null; reason = "maintenance_publication_pending"; return false; }
                cursor=null;
                if(IsReleasedUnderGate||_readiness==ProjectionReadiness.Exhausted){reason="projection_registry_unavailable";return false;}
                if(_preparation!=null){reason="projection_preparation_busy";return false;}
                if(descriptor==null||changes==null||changes.Length+(removals?.Length??0)>32)throw new ArgumentException("projection_update_capacity");
                if(!rebuild&&_readiness!=ProjectionReadiness.ReadyPartialDiagnostic&&_descriptor!=null){reason="projection_rebuild_required";return false;}
                var candidate=rebuild?new ProjectionVersionTable(_identity):_table;
                var ids=new HashSet<ProjectionNodeId>();
                foreach(var change in changes)
                {
                    if(change==null||change.Id.Registry!=_identity||!_lifetimes.TryGetValue(change.Id.Value,out var life)||!ids.Add(change.Id)){reason="invalid_projection_node_binding";return false;}
                    if(!change.HasIssuer(life.Issuer)||change.Version!=life.LastIssued||!life.Issued.TryGetTarget(out var issuedVersion)||!ReferenceEquals(change,issuedVersion)){reason="invalid_projection_node_issuer";return false;}
                    if(change.Version<=life.LastAdmitted||change.Kind!=life.Kind){reason="stale_projection_node_lifetime";return false;}
                    var before=candidate.Get(change.Id);
                    if(before!=null&&(change.Version<=before.Version||change.Kind!=before.Kind)){reason="stale_projection_node_version";return false;}
                    candidate=candidate.Set(change.Id,change);
                }
                if(removals!=null)foreach(var id in removals)
                {
                    if(id.Registry!=_identity||id.Value==0||!ids.Add(id)){reason="invalid_projection_removal";return false;}
                    candidate=candidate.Set(id,null);
                }
                if(candidate.Count>Limits.MaximumNodes||OwnedTableUnits(candidate)>Limits.MaximumRetainedUnits)
                {InvalidateUnderGate(ProjectionReadiness.Capacity,"projection_table_capacity");reason="projection_table_capacity";return false;}
                for(int i=0;i<descriptor.RootCount;i++)if(descriptor.Root(i).Node.Registry!=_identity){reason="projection_cross_registry_root";return false;}
                long preparedUnits=OwnedTableUnits(candidate);
                while(_retainedUnits>Limits.MaximumRetainedUnits-preparedUnits)
                {
                    if(_pins.Count>0)_pins[0].CancelUnderGate("projection_retention_pressure");
                    else if(_outputs.Count>0)_outputs[0].RevokeUnderGate("projection_retention_pressure");
                    else {InvalidateUnderGate(ProjectionReadiness.Capacity,"projection_preparation_retention_capacity");reason="projection_preparation_retention_capacity";return false;}
                }
                _preparedUnits=preparedUnits;_retainedUnits+=preparedUnits;
                cursor=new ProjectionPreparationCursor(this,candidate,descriptor,_revision,_readinessEpoch,(ProjectionNodeVersion[])changes.Clone());_preparation=cursor;reason="";return true;
            }
        }
        internal bool PreparationCurrentUnderGate(ProjectionPreparationCursor cursor,ulong revision,ulong epoch)
            =>ReferenceEquals(_preparation,cursor)&&!IsReleasedUnderGate&&_revision==revision&&_readinessEpoch==epoch;
        internal void ReleasePreparationUnderGate(ProjectionPreparationCursor cursor)
        {if(ReferenceEquals(_preparation,cursor)){_preparation=null;_retainedUnits-=_preparedUnits;_preparedUnits=0;}}
        internal bool TryPublish(ProjectionPreparationCursor cursor,out string reason)
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                if(cursor==null||!cursor.IsComplete||!PreparationCurrentUnderGate(cursor,cursor.BaseRevision,cursor.BaseEpoch)){reason="stale_or_incomplete_projection_plan";return false;}
                if(_revision==ulong.MaxValue){InvalidateUnderGate(ProjectionReadiness.Exhausted,"projection_revision_exhausted");reason="projection_revision_exhausted";return false;}
                for(int i=0;i<cursor.ChangeCount;i++)
                {
                    var change=cursor.Change(i);var life=_lifetimes[change.Id.Value];
                    if(!life.Issued.TryGetTarget(out var exact)||!ReferenceEquals(change,exact)||change.Version!=life.LastIssued)
                    {cursor.CancelUnderGate("stale_projection_issuance");reason="stale_projection_issuance";return false;}
                }
                long nextUnits=_preparedUnits;
                // Candidate ownership becomes current ownership; release only the previous current cut.
                _retainedUnits-=_currentUnits;_currentUnits=nextUnits;_preparedUnits=0;
                for(int i=0;i<cursor.ChangeCount;i++){var change=cursor.Change(i);var life=_lifetimes[change.Id.Value];_lifetimes[change.Id.Value]=new Lifetime(change.Kind,change.Version,life.LastIssued,life.Issuer,life.Issued);}
                _table=cursor.Candidate;_descriptor=cursor.Descriptor;_plan=cursor.Plan;_revision++;
                _readiness=ProjectionReadiness.ReadyPartialDiagnostic;_preparation=null;cursor.TransferUnderGate();reason="";return true;
            }
        }
        internal bool TryPin(out ProjectionPin pin,out string reason)
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                pin=null;
                if(!OwnerLeaseCurrentUnderGate()){reason="owned_projection_resource_revoked";return false;}
                if(_readiness!=ProjectionReadiness.ReadyPartialDiagnostic){reason="projection_not_ready";return false;}
                if(_preparation!=null){reason="projection_unresolved_preparation";return false;}
                long pinUnits = OwnedTableUnits(_table); // a pin owns table/plan only, never mutable cohort/lifetime maps
                if(_pins.Count==Limits.MaximumPins||_retainedUnits>Limits.MaximumRetainedUnits-pinUnits){reason="projection_pin_capacity";return false;}
                pin=new ProjectionPin(this,_table,_descriptor,_plan,_revision,_readinessEpoch);_pins.Add(pin);_retainedUnits+=pinUnits;reason="";return true;
            }
        }
        internal bool CutEpochCurrentUnderGate(ulong epoch)=>OwnerLeaseCurrentUnderGate()&&_readiness==ProjectionReadiness.ReadyPartialDiagnostic&&epoch==_readinessEpoch;
        internal bool TryClaimOutputUnderGate(out string reason)
        {
            if(_outputClaims==Limits.MaximumOutputs){reason="projection_output_capacity";return false;}
            _outputClaims++;reason="";return true;
        }
        internal bool TryRetainCopyUnderGate(int units)
        {if(units<1||_retainedUnits>Limits.MaximumRetainedUnits-units)return false;_retainedUnits+=units;return true;}
        internal void ReleaseCopiesUnderGate(int copies){_retainedUnits-=copies;}
        internal void ReleaseOutputClaimUnderGate(){_outputClaims--;}
        internal void ReleasePinUnderGate(ProjectionPin pin,long units,bool transfer)
        {if(_pins.Remove(pin)&&!transfer)_retainedUnits-=units;}
        internal void AddOutputUnderGate(ProjectionCaptureResult result){_outputs.Add(result);}
        internal void ReleaseOutputUnderGate(ProjectionCaptureResult result,long units)
        {if(_outputs.Remove(result)){_retainedUnits-=units;_outputClaims--;}}
        internal void InvalidateUnderGate(ProjectionReadiness readiness,string reason)
        {
            CommonParticipantGate.RequireHeld();
            if(IsReleasedUnderGate)return;
            ReleaseOwnerPreparationUnderGate();
            ReleaseMaintenancePublicationUnderGate();
            if(_readinessEpoch==Limits.MaximumEpoch)readiness=ProjectionReadiness.Exhausted;else _readinessEpoch++;
            _readiness=readiness;
            while(_pins.Count>0)_pins[0].CancelUnderGate(reason);
            while(_outputs.Count>0)_outputs[0].RevokeUnderGate(reason);
            _preparation?.CancelUnderGate(reason);_preparation=null;
        }
        internal void Invalidate(string reason)
        {if(reason!=null&&reason.Length>1024)throw new ArgumentException("projection_reason_capacity");lock(CommonParticipantGate.SyncRoot)InvalidateUnderGate(ProjectionReadiness.RebuildRequired,reason??"projection_rebuild_required");}
        internal bool TryBuildWholeWorldSave(out string reason){reason="participants_not_fully_enrolled";return false;}
        public void Dispose()
        {
            lock(CommonParticipantGate.SyncRoot)
            {
                if(IsReleasedUnderGate)return;
                InvalidateUnderGate(ProjectionReadiness.Released,"projection_registry_released");_readiness=ProjectionReadiness.Released;
                _retainedUnits-=_currentUnits;_currentUnits=0;_table=null;_descriptor=null;_plan=null;_lifetimes=null;_activeHandles=0;
            }
        }
    }
    internal sealed class ProjectionGraphPlan
    {
        readonly IdPage _root;
        internal int NodeCount{get;}
        internal int SegmentCount{get;}
        internal ProjectionGraphPlan(){ }
        ProjectionGraphPlan(IdPage root,int count,int segments){_root=root;NodeCount=count;SegmentCount=segments;}
        internal ProjectionGraphPlan Append(ProjectionNodeId id,int segments)
        {if(NodeCount==4096)throw new InvalidOperationException("projection_plan_capacity");return new ProjectionGraphPlan(Update(_root,NodeCount,2,new PlanEntry(id,SegmentCount)),NodeCount+1,SegmentCount+segments);}
        internal ProjectionNodeId Node(int index)
        {if(index<0||index>=NodeCount)throw new ArgumentOutOfRangeException(nameof(index));var page=_root;for(int level=2;level>0;level--)page=page.Children[(index>>(level*5))&31];return page.Values[index&31].Id;}
        internal int Offset(int index)
        {if(index<0||index>=NodeCount)throw new ArgumentOutOfRangeException(nameof(index));var page=_root;for(int level=2;level>0;level--)page=page.Children[(index>>(level*5))&31];return page.Values[index&31].Offset;}
        readonly struct PlanEntry { internal readonly ProjectionNodeId Id; internal readonly int Offset; internal PlanEntry(ProjectionNodeId id,int offset){Id=id;Offset=offset;} }
        static IdPage Update(IdPage before,int index,int level,PlanEntry id)
        {
            if(level==0){var values=before==null?new PlanEntry[32]:(PlanEntry[])before.Values.Clone();values[index&31]=id;return new IdPage(values);}
            var children=before==null?new IdPage[32]:(IdPage[])before.Children.Clone();int at=(index>>(level*5))&31;children[at]=Update(children[at],index,level-1,id);return new IdPage(children);
        }
        sealed class IdPage
        {
            internal readonly IdPage[] Children;
            internal readonly PlanEntry[] Values;
            internal IdPage(IdPage[] values){Children=values;}
            internal IdPage(PlanEntry[] values){Values=values;}
        }
    }
    internal sealed class ProjectionPreparationCursor : IDisposable
    {
        readonly ParticipantProjectionRegistry _registry;
        readonly int _thread=Thread.CurrentThread.ManagedThreadId;
        Dictionary<ProjectionNodeId,int> _heights;
        HashSet<ProjectionNodeId> _active;
        Stack<Frame> _stack;
        ProjectionGraphPlan _order=new ProjectionGraphPlan();
        int _rootIndex;
        ProjectionCursorStatus _status=ProjectionCursorStatus.Pending;
        internal ProjectionVersionTable Candidate{get;private set;}
        internal ProjectionRootDescriptor Descriptor{get;private set;}
        internal ProjectionGraphPlan Plan{get;private set;}
        ProjectionNodeVersion[] _changes;
        internal int ChangeCount=>_changes.Length;
        internal ProjectionNodeVersion Change(int index)=>_changes[index];
        internal ulong BaseRevision{get;}
        internal ulong BaseEpoch{get;}
        internal bool IsComplete=>_status==ProjectionCursorStatus.Complete;
        internal ProjectionCursorStatus Status=>_status;
        internal string Reason{get;private set;}="";
        internal int LastWorkUnits{get;private set;}
        internal ProjectionPreparationCursor(ParticipantProjectionRegistry registry,ProjectionVersionTable table,ProjectionRootDescriptor descriptor,ulong revision,ulong epoch,ProjectionNodeVersion[] changes)
        {_registry=registry;Candidate=table;Descriptor=descriptor;BaseRevision=revision;BaseEpoch=epoch;_changes=changes;_heights=new Dictionary<ProjectionNodeId,int>(table.Count);_active=new HashSet<ProjectionNodeId>(129);_stack=new Stack<Frame>(129);}
        internal ProjectionCursorStatus Advance(int units)
        {
            if(Thread.CurrentThread.ManagedThreadId!=_thread)throw new InvalidOperationException("projection_cursor_thread");
            if(units<1||units>64)throw new ArgumentOutOfRangeException(nameof(units));LastWorkUnits=0;
            lock(CommonParticipantGate.SyncRoot)
            {
                if(_status!=ProjectionCursorStatus.Pending)return _status;
                if(!_registry.PreparationCurrentUnderGate(this,BaseRevision,BaseEpoch)){CancelUnderGate("stale_projection_preparation");return _status;}
            }
            lock(CommonParticipantGate.SyncRoot)
            {
                if(_status!=ProjectionCursorStatus.Pending)return _status;
                while(_status==ProjectionCursorStatus.Pending&&LastWorkUnits<units)
                {
                    LastWorkUnits++;
                    if(_stack.Count==0)
                    {
                        if(_rootIndex==Descriptor.RootCount){Plan=_order;_status=ProjectionCursorStatus.Complete;break;}
                        Enter(Descriptor.Root(_rootIndex++).Node,0);continue;
                    }
                    var frame=_stack.Peek();
                    if(frame.Next==frame.Node.EntryCount)
                    {
                        _stack.Pop();_active.Remove(frame.Node.Id);_heights.Add(frame.Node.Id,frame.Height);
                        if(_stack.Count>0)_stack.Peek().Height=Math.Max(_stack.Peek().Height,frame.Height+1);
                        continue;
                    }
                    var entry=frame.Node.GetEntry(frame.Next++);
                    if(entry.Value.IsChild)
                    {
                        if(_heights.TryGetValue(entry.Value.Child,out int height))
                        {if(frame.Depth+1+height>128)Refuse("projection_graph_depth");else frame.Height=Math.Max(frame.Height,height+1);}
                        else Enter(entry.Value.Child,frame.Depth+1);
                    }
                }
                return _status;
            }
        }
        void Enter(ProjectionNodeId id,int depth)
        {
            if(_active.Contains(id)){Refuse("projection_cycle");return;}
            if(_heights.TryGetValue(id,out int height)){if(depth+height>128)Refuse("projection_graph_depth");return;}
            if(depth>128||_order.NodeCount==_registry.Limits.MaximumNodes){Refuse("projection_graph_capacity");return;}
            var node=Candidate.Get(id);if(node==null){Refuse("projection_missing_child");return;}
            _active.Add(id);_order=_order.Append(id,node.PageCount+1);_stack.Push(new Frame(node,depth));
        }
        void Refuse(string reason)
        {lock(CommonParticipantGate.SyncRoot){Reason=reason;_status=ProjectionCursorStatus.Refused;ReleaseData();_registry.ReleasePreparationUnderGate(this);}}
        internal void CancelUnderGate(string reason)
        {Reason=reason;_status=ProjectionCursorStatus.Cancelled;ReleaseData();_registry.ReleasePreparationUnderGate(this);}
        internal void TransferUnderGate(){_status=ProjectionCursorStatus.Released;ReleaseData();}
        void ReleaseData(){Candidate=null;Descriptor=null;Plan=null;_changes=null;_stack=null;_active=null;_heights=null;_order=null;}
        public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(_status==ProjectionCursorStatus.Released)return;_status=ProjectionCursorStatus.Released;ReleaseData();_registry.ReleasePreparationUnderGate(this);}}
        sealed class Frame
        {internal readonly ProjectionNodeVersion Node;internal readonly int Depth;internal int Next,Height;internal Frame(ProjectionNodeVersion node,int depth){Node=node;Depth=depth;}}
    }
}
