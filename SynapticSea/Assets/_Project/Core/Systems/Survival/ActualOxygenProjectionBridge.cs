using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    internal sealed class ActualOxygenProjectionSnapshot
    {
        readonly ActualOxygenProjectionBridge _issuer;
        internal readonly ContinuousOxygenValues Values;
        internal readonly ulong Stamp,Epoch;
        ActualOxygenProjectionSnapshot(ActualOxygenProjectionBridge issuer,ContinuousOxygenValues values,ulong stamp,ulong epoch)
        {_issuer=issuer;Values=values;Stamp=stamp;Epoch=epoch;}
        internal static ActualOxygenProjectionSnapshot ReadActual(ActualOxygenProjectionBridge issuer)
        {lock(CommonParticipantGate.SyncRoot){issuer.ReadBoundUnderGate(out var values,out ulong stamp,out ulong epoch);return new ActualOxygenProjectionSnapshot(issuer,values,stamp,epoch);}}
        internal bool MatchesUnderGate(ActualOxygenProjectionBridge owner)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(_issuer,owner)&&owner.MatchesUnderGate(Values,Stamp,Epoch);}
    }
    internal sealed class ActualOxygenProjectionBridge:IDisposable
    {
        readonly RunSession _session;
        readonly OxygenState _actual;
        readonly GdArray _zonesIdentity;
        readonly object _scene;
        readonly string _run;
        readonly ParticipantProjectionRegistry _registry;
        readonly ProjectionNodeHandle _summaryNode,_zonesNode;
        readonly ProjectionNodeVersion _zonesVersion;
        ContinuousOxygenValues _values;
        ulong _stamp=1;
        bool _disposed,_invalid;
        PreparedChange _pending;
        object _simulationGeneration=new object();
        PreparedSimulationMutation _simulationPending;
        internal ActualOxygenProjectionBridge(RunSession session,ProjectionLimits limits=null)
        {
            if(session==null||!session.PlayableStarted||session.SliceComplete||session.OxygenState==null)throw new ArgumentException("actual_oxygen_binding_unavailable");
            _session=session;_actual=session.OxygenState;_zonesIdentity=_actual.BreachZoneIds;_scene=session.Scene;_run=session.RunIdInternal;
            _values=ContinuousOxygenValues.CaptureExact(_actual);_registry=new ParticipantProjectionRegistry(limits);
            try
            {
                if(!_registry.TryCreateNode(ProjectionNodeKind.Dictionary,out _summaryNode,out string reason)||
                    !_registry.TryCreateNode(ProjectionNodeKind.Array,out _zonesNode,out reason))throw new InvalidOperationException(reason);
                var zones=new ProjectionEntry[_values.ZoneCount];
                for(int i=0;i<zones.Length;i++)zones[i]=new ProjectionEntry(new ProjectionValue(ProjectionScalar.FromNormalized(_values.Zone(i))));
                if(!_zonesNode.TryCreateVersion(zones,out _zonesVersion,out reason)||!BuildPlan(_values,1,true,out var plan,out reason))throw new InvalidOperationException(reason);
                using(plan){if(!_registry.TryPublish(plan,out reason))throw new InvalidOperationException(reason);}
            }
            catch{_registry.Dispose();throw;}
        }
        bool BoundUnderGate()
        {
            CommonParticipantGate.RequireHeld();if(_disposed||_invalid)return false;
            if(!ReferenceEquals(_session.OxygenState,_actual)||!ReferenceEquals(_actual.BreachZoneIds,_zonesIdentity)||!ReferenceEquals(_session.Scene,_scene)||
                _session.RunIdInternal!=_run||_session.ComponentGenerationRestoreInProgress||!_values.MatchesRaw(_actual))
            {_invalid=true;_registry.Invalidate("actual_oxygen_unregistered_writer_or_binding_change");return false;}return true;
        }
        internal bool SourceCurrentUnderGate(){CommonParticipantGate.RequireHeld();return BoundUnderGate();}
        internal bool IsBoundActualSessionUnderGate(RunSession session)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(session,_session)&&BoundUnderGate();}
        internal void ReadBoundUnderGate(out ContinuousOxygenValues values,out ulong stamp,out ulong epoch)
        {CommonParticipantGate.RequireHeld();if(!BoundUnderGate())throw new InvalidOperationException("actual_oxygen_binding_invalid");values=_values;stamp=_stamp;epoch=_registry.ReadinessEpoch;}
        internal ActualOxygenProjectionSnapshot Read()=>ActualOxygenProjectionSnapshot.ReadActual(this);
        internal bool MatchesUnderGate(ContinuousOxygenValues values,ulong stamp,ulong epoch)
        {CommonParticipantGate.RequireHeld();return BoundUnderGate()&&values!=null&&stamp==_stamp&&epoch==_registry.ReadinessEpoch&&values.MatchesRaw(_actual);}
        bool BuildPlan(ContinuousOxygenValues next,ulong stamp,bool rebuild,out ProjectionPreparationCursor plan,out string reason)
        {
            plan=null;var summary=next.ExactScratch().GetSummary();
            if(summary.Count!=15){reason="actual_oxygen_summary_shape";return false;}
            var entries=new ProjectionEntry[15];int i=0;
            foreach(var row in summary)
            {
                if(row.Key is string key&&key=="breach_zone_ids")entries[i++]=new ProjectionEntry(ProjectionScalar.FromNormalized(key),new ProjectionValue(_zonesNode.Id));
                else
                {
                    if(row.Value is double d&&(double.IsNaN(d)||double.IsInfinity(d))){reason="actual_oxygen_nonfinite_projection";return false;}
                    entries[i++]=new ProjectionEntry(ProjectionScalar.FromNormalized(row.Key),new ProjectionValue(ProjectionScalar.FromNormalized(row.Value)));
                }
            }
            if(!_summaryNode.TryCreateVersion(entries,out var version,out reason))return false;
            var roots=new ProjectionRootDescriptor("same-runsession-oxygen-partial-v1","restricted-diagnostic-NOT-world-authority",stamp,new[]{new ProjectionRootBinding("oxygen_summary",_summaryNode.Id)});
            if(!_registry.TryPrepare(rebuild?new[]{version,_zonesVersion}:new[]{version},null,roots,out plan,out reason,rebuild))return false;
            int calls=0;while(plan.Status==ProjectionCursorStatus.Pending){plan.Advance(8);if(++calls>512){reason="actual_oxygen_prepare_bound";plan.Dispose();plan=null;return false;}}
            if(plan.Status!=ProjectionCursorStatus.Complete){reason=plan.Reason;plan.Dispose();plan=null;return false;}return true;
        }
        internal bool PrepareNext(ActualOxygenProjectionSnapshot expected,ContinuousOxygenValues next,out PreparedChange prepared,out string reason)
        {
            prepared=null;reason="actual_oxygen_stale_or_configuration_change";ulong before;
            lock(CommonParticipantGate.SyncRoot)
            {
                if(expected==null||next==null||!expected.MatchesUnderGate(this)||!_values.SameTickConfiguration(next)||_stamp==ulong.MaxValue)return false;
                _pending?.Dispose();before=_stamp;
            }
            if(!BuildPlan(next,before+1,false,out var plan,out reason))return false;
            var candidate=new PreparedChange(this,expected,next,plan);
            lock(CommonParticipantGate.SyncRoot)
            {if(!expected.MatchesUnderGate(this)||_pending!=null){candidate.Dispose();reason="actual_oxygen_stale_or_busy";return false;}_pending=candidate;prepared=candidate;reason="";return true;}
        }
        // Raw pin alone is historical diagnostic data; observers MUST use guarded wrapper below.
        internal bool TryPin(out ProjectionPin pin,out string reason)
        {lock(CommonParticipantGate.SyncRoot){if(!BoundUnderGate()||_pending!=null){pin=null;reason="actual_oxygen_capture_unavailable";return false;}return _registry.TryPin(out pin,out reason);}}
        bool SimulationLifecycleUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            return !_disposed&&ReferenceEquals(_session.OxygenState,_actual)&&ReferenceEquals(_actual.BreachZoneIds,_zonesIdentity)&&ReferenceEquals(_session.Scene,_scene)&&
                _session.RunIdInternal==_run&&!_session.ComponentGenerationRestoreInProgress;
        }
        internal bool IsBoundSimulationSessionUnderGate(RunSession session)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(session,_session)&&SimulationLifecycleUnderGate();}
        internal bool IsBoundSimulationModelUnderGate(OxygenState actual)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(actual,_actual)&&SimulationLifecycleUnderGate();}
        internal bool CaptureAvailableUnderGate()
        {CommonParticipantGate.RequireHeld();return BoundUnderGate()&&_stamp!=ulong.MaxValue&&_registry.Readiness==ProjectionReadiness.ReadyPartialDiagnostic;}
        internal ContinuousSimulationOxygenSnapshot ReadSimulation()=>ContinuousSimulationOxygenSnapshot.CaptureFresh(this);
        internal void ReadSimulationUnderGate(out ContinuousOxygenValues values,out object generation)
        {
            CommonParticipantGate.RequireHeld();
            if(!SimulationLifecycleUnderGate())throw new InvalidOperationException("simulation_oxygen_lifecycle_changed");
            // Native fresh read, deliberately independent of stale/unavailable projection cache.
            values=ContinuousOxygenValues.CaptureExact(_actual);generation=_simulationGeneration;
        }
        internal bool SimulationMatchesUnderGate(ContinuousOxygenValues values,object generation)
        {CommonParticipantGate.RequireHeld();return SimulationLifecycleUnderGate()&&ReferenceEquals(generation,_simulationGeneration)&&values!=null&&values.MatchesRaw(_actual);}
        internal bool PrepareSimulationOnly(ContinuousSimulationOxygenSnapshot expected,ContinuousOxygenProposal proposal,out PreparedSimulationMutation prepared,out string reason)
        {
            prepared=null;reason="stale_simulation_oxygen_proposal";
            if(expected==null||proposal==null||!ReferenceEquals(expected.Values,proposal.Before)||!proposal.Before.SameTickConfiguration(proposal.After))return false;
            lock(CommonParticipantGate.SyncRoot)
            {if(!expected.MatchesUnderGate(this))return false;_simulationPending?.Dispose();}
            var candidate=new PreparedSimulationMutation(this,expected,proposal);
            lock(CommonParticipantGate.SyncRoot)
            {if(!expected.MatchesUnderGate(this)||_simulationPending!=null){candidate.Dispose();return false;}_simulationPending=candidate;prepared=candidate;reason="";return true;}
        }
        internal void RevokeCaptureForSimulationUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            if(!SimulationLifecycleUnderGate())throw new InvalidOperationException("simulation_oxygen_lifecycle_changed");
            _pending?.Dispose();
            if(!_invalid){_invalid=true;_registry.Invalidate("simulation_oxygen_noncapturing_continuation");}
        }
        internal sealed class PreparedSimulationMutation:IDisposable
        {
            readonly ActualOxygenProjectionBridge _owner;
            readonly ContinuousSimulationOxygenSnapshot _before;
            readonly ContinuousOxygenProposal _proposal;
            readonly object _nextGeneration=new object();
            bool _used;
            internal PreparedSimulationMutation(ActualOxygenProjectionBridge owner,ContinuousSimulationOxygenSnapshot before,ContinuousOxygenProposal proposal)
            {_owner=owner;_before=before;_proposal=proposal;}
            internal bool MatchesUnderGate()
            {CommonParticipantGate.RequireHeld();return !_used&&ReferenceEquals(_owner._simulationPending,this)&&_before.MatchesUnderGate(_owner)&&ReferenceEquals(_before.Values,_proposal.Before);}
            internal bool TryInstallNativeOnlyUnderGate(out string reason)
            {
                CommonParticipantGate.RequireHeld();if(!MatchesUnderGate()){reason="stale_simulation_oxygen_proposal";return false;}
                // All revocation/disposal work BEFORE the first actual assignment.
                _owner.RevokeCaptureForSimulationUnderGate();
                var m=_owner._actual;var v=_proposal.After;
                m.Oxygen=v.Oxygen;m.EffectiveDrainRate=v.EffectiveDrainRate;
                m.PassabilityBlocked=v.PassabilityBlocked;m.LastPlayerInBreachZone=v.LastPlayerInBreachZone;
                _owner._values=v;_owner._simulationGeneration=_nextGeneration;_owner._simulationPending=null;_used=true;reason="";return true;
            }
            public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(ReferenceEquals(_owner._simulationPending,this))_owner._simulationPending=null;_used=true;}}
        }
        internal bool TryBuildWholeWorldSave(out string reason){reason="other_world_producers_not_enrolled";return false;}
        internal sealed class PreparedChange:IDisposable
        {
            readonly ActualOxygenProjectionBridge _owner;
            readonly ActualOxygenProjectionSnapshot _before;
            readonly ContinuousOxygenValues _next;
            readonly ProjectionPreparationCursor _plan;
            readonly object _nextSimulationGeneration=new object();
            bool _used;
            internal PreparedChange(ActualOxygenProjectionBridge owner,ActualOxygenProjectionSnapshot before,ContinuousOxygenValues next,ProjectionPreparationCursor plan)
            {_owner=owner;_before=before;_next=next;_plan=plan;}
            internal bool MatchesUnderGate()
            {CommonParticipantGate.RequireHeld();return !_used&&ReferenceEquals(_owner._pending,this)&&_before.MatchesUnderGate(_owner)&&_plan.Status==ProjectionCursorStatus.Complete;}
            internal bool TryInstallActualRawAndProjectionUnderGate(out string reason)
            {
                CommonParticipantGate.RequireHeld();if(!MatchesUnderGate()){reason="actual_oxygen_final_guard";return false;}
                if(!_owner._registry.TryPublish(_plan,out reason))return false;
                // Configuration/cache/zone identity is invariant in this Tick-only seam.
                _owner._actual.Oxygen=_next.Oxygen;_owner._actual.EffectiveDrainRate=_next.EffectiveDrainRate;
                _owner._actual.PassabilityBlocked=_next.PassabilityBlocked;_owner._actual.LastPlayerInBreachZone=_next.LastPlayerInBreachZone;
                _owner._values=_next;_owner._simulationGeneration=_nextSimulationGeneration;_owner._stamp++;_owner._pending=null;_used=true;reason="";return true;
            }
            public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(ReferenceEquals(_owner._pending,this))_owner._pending=null;_used=true;_plan.Dispose();}}
        }
        public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(_disposed)return;_disposed=true;_pending?.Dispose();_simulationPending?.Dispose();_registry.Dispose();}}
    }
}
