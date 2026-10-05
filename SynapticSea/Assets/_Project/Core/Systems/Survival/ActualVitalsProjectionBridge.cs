using System;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    // Data/projection seam only; NOT runtime evaluator, resource/world authority or observer dispatcher.
    internal sealed class ActualVitalsProjectionSnapshot
    {
        readonly ActualVitalsProjectionBridge _issuer;
        internal readonly DiagnosticVitalsValues Values;
        internal readonly ulong Stamp,Epoch;
        ActualVitalsProjectionSnapshot(ActualVitalsProjectionBridge issuer,DiagnosticVitalsValues values,ulong stamp,ulong epoch)
        {_issuer=issuer;Values=values;Stamp=stamp;Epoch=epoch;}
        internal static ActualVitalsProjectionSnapshot CaptureActual(ActualVitalsProjectionBridge bridge)
        {lock(CommonParticipantGate.SyncRoot){bridge.ReadBoundUnderGate(out var values,out ulong stamp,out ulong epoch);return new ActualVitalsProjectionSnapshot(bridge,values,stamp,epoch);}}
        internal bool MatchesUnderGate(ActualVitalsProjectionBridge bridge)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(bridge,_issuer)&&bridge.MatchesCapturedUnderGate(Values,Stamp,Epoch);}
    }
    internal sealed class ActualVitalsProjectionBridge:IDisposable
    {
        readonly RunSession _session;
        readonly VitalsState _actual;
        readonly object _scene;
        readonly string _run;
        readonly ParticipantProjectionRegistry _registry;
        readonly ProjectionNodeHandle _node;
        DiagnosticVitalsValues _values;
        ulong _stamp=1;
        bool _disposed,_invalid;
        PreparedChange _pending;
        object _simulationGeneration=new object();
        PreparedSimulationMutation _simulationPending;
        internal ActualVitalsProjectionBridge(RunSession session,ProjectionLimits limits=null)
        {
            if(session==null||session.VitalsState==null||!session.PlayableStarted||session.SliceComplete)throw new ArgumentException("actual_vitals_binding_unavailable");
            _session=session;_actual=session.VitalsState;_scene=session.Scene;_run=session.RunIdInternal;
            _values=DiagnosticVitalsValues.FromModel(_actual);_registry=new ParticipantProjectionRegistry(limits);
            try
            {
                if(!_registry.TryCreateNode(ProjectionNodeKind.Dictionary,out _node,out string reason))throw new InvalidOperationException(reason);
                if(!BuildPlan(_values,1,true,out var plan,out reason))throw new InvalidOperationException(reason);
                using(plan){if(!_registry.TryPublish(plan,out reason))throw new InvalidOperationException(reason);}
            }
            catch{_registry.Dispose();throw;}
        }
        static bool B(double a,double b)=>BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);
        static bool RawMatches(DiagnosticVitalsValues v,VitalsState m)=>
            B(v.At(0),m.Health)&&B(v.At(1),m.MaxHealth)&&B(v.At(2),m.Stamina)&&B(v.At(3),m.MaxStamina)&&
            B(v.At(4),m.Hunger)&&B(v.At(5),m.MaxHunger)&&B(v.At(6),m.Thirst)&&B(v.At(7),m.MaxThirst)&&
            B(v.At(8),m.HealthDrainRate)&&B(v.At(9),m.StaminaDrainRate)&&B(v.At(10),m.HungerDrainRate)&&B(v.At(11),m.ThirstDrainRate)&&
            B(v.At(12),m.StaminaRecoveryRate)&&B(v.At(13),m.HealthRecoveryRate);
        bool BoundUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            if(_disposed||_invalid)return false;
            if(!ReferenceEquals(_session.VitalsState,_actual)||!ReferenceEquals(_session.Scene,_scene)||_session.RunIdInternal!=_run||
                _session.ComponentGenerationRestoreInProgress||!RawMatches(_values,_actual))
            {_invalid=true;_registry.Invalidate("actual_vitals_unregistered_writer_or_binding_change");return false;}
            return true;
        }
        internal bool IsBoundActualSessionUnderGate(RunSession session)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(_session,session)&&BoundUnderGate();}
        internal bool IsBoundActualModelUnderGate(VitalsState actual)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(_actual,actual)&&BoundUnderGate();}
        internal void ReadBoundUnderGate(out DiagnosticVitalsValues values,out ulong stamp,out ulong epoch)
        {CommonParticipantGate.RequireHeld();if(!BoundUnderGate())throw new InvalidOperationException("actual_vitals_binding_invalid");values=_values;stamp=_stamp;epoch=_registry.ReadinessEpoch;}
        internal ActualVitalsProjectionSnapshot Read()=>ActualVitalsProjectionSnapshot.CaptureActual(this);
        internal bool MatchesCapturedUnderGate(DiagnosticVitalsValues values,ulong stamp,ulong epoch)
        {
            CommonParticipantGate.RequireHeld();if(!BoundUnderGate()||values==null||stamp!=_stamp||epoch!=_registry.ReadinessEpoch)return false;
            return RawMatches(values,_actual);
        }
        bool BuildPlan(DiagnosticVitalsValues values,ulong nextStamp,bool rebuild,out ProjectionPreparationCursor plan,out string reason)
        {
            plan=null;var summary=values.ExactScratch().GetSummary(); // Non-current proposal actual-model numeric oracle.
            if(summary.Count!=21){reason="actual_vitals_summary_shape";return false;}
            var entries=new ProjectionEntry[21];int i=0;
            foreach(var row in summary)
            {
                if(row.Value is double d&&(double.IsNaN(d)||double.IsInfinity(d))){reason="actual_vitals_nonfinite_projection";return false;}
                entries[i++]=new ProjectionEntry(ProjectionScalar.FromNormalized(row.Key),new ProjectionValue(ProjectionScalar.FromNormalized(row.Value)));
            }
            if(!_node.TryCreateVersion(entries,out var version,out reason))return false;
            var descriptor=new ProjectionRootDescriptor("same-runsession-vitals-partial-v1","restricted-diagnostic-producer-NOT-world-authority",nextStamp,new[]{new ProjectionRootBinding("vitals_summary",_node.Id)});
            if(!_registry.TryPrepare(new[]{version},null,descriptor,out plan,out reason,rebuild))return false;
            int slices=0;while(plan.Status==ProjectionCursorStatus.Pending){plan.Advance(8);if(++slices>32){plan.Dispose();plan=null;reason="actual_vitals_plan_bound";return false;}}
            if(plan.Status!=ProjectionCursorStatus.Complete){reason=plan.Reason;plan.Dispose();plan=null;return false;}return true;
        }
        internal bool PrepareNext(ActualVitalsProjectionSnapshot expected,DiagnosticVitalsValues next,out PreparedChange prepared,out string reason)
        {
            prepared=null;reason="actual_vitals_stale_or_busy";ulong before;
            lock(CommonParticipantGate.SyncRoot)
            {
                if(expected==null||next==null||!expected.MatchesUnderGate(this)||_stamp==ulong.MaxValue)return false;
                // A current ordinary producer must not wait for a speculative work debit.
                // Validate source FIRST: stale/foreign requests cannot cancel current work.
                // Dispose clears only the exact old pointer; old handles cannot clear its successor.
                _pending?.Dispose();before=_stamp;
            }
            if(!BuildPlan(next,before+1,false,out var plan,out reason))return false;
            var candidate=new PreparedChange(this,expected,next,plan);
            lock(CommonParticipantGate.SyncRoot)
            {
                if(!expected.MatchesUnderGate(this)||_pending!=null){candidate.Dispose();reason="actual_vitals_stale_or_busy";return false;}
                _pending=candidate;prepared=candidate;reason="";return true;
            }
        }
        internal bool TryPin(out ProjectionPin pin,out string reason)
        {lock(CommonParticipantGate.SyncRoot){if(!BoundUnderGate()||_pending!=null){pin=null;reason="actual_vitals_capture_unavailable";return false;}return _registry.TryPin(out pin,out reason);}}
        bool SimulationLifecycleUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            return !_disposed&&ReferenceEquals(_session.VitalsState,_actual)&&ReferenceEquals(_session.Scene,_scene)&&
                _session.RunIdInternal==_run&&!_session.ComponentGenerationRestoreInProgress;
        }
        internal bool IsBoundSimulationSessionUnderGate(RunSession session)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(session,_session)&&SimulationLifecycleUnderGate();}
        internal bool IsBoundSimulationModelUnderGate(VitalsState actual)
        {CommonParticipantGate.RequireHeld();return ReferenceEquals(actual,_actual)&&SimulationLifecycleUnderGate();}
        internal bool CaptureAvailableUnderGate()
        {CommonParticipantGate.RequireHeld();return BoundUnderGate()&&_stamp!=ulong.MaxValue&&_registry.Readiness==ProjectionReadiness.ReadyPartialDiagnostic;}
        internal ContinuousSimulationVitalsSnapshot ReadSimulation()=>ContinuousSimulationVitalsSnapshot.CaptureFresh(this);
        internal void ReadSimulationUnderGate(out DiagnosticVitalsValues values,out object generation)
        {
            CommonParticipantGate.RequireHeld();
            if(!SimulationLifecycleUnderGate())throw new InvalidOperationException("simulation_vitals_lifecycle_changed");
            // Native fresh read, deliberately independent of stale/unavailable projection cache.
            values=DiagnosticVitalsValues.FromModel(_actual);generation=_simulationGeneration;
        }
        internal bool SimulationMatchesUnderGate(DiagnosticVitalsValues values,object generation)
        {CommonParticipantGate.RequireHeld();return SimulationLifecycleUnderGate()&&ReferenceEquals(generation,_simulationGeneration)&&values!=null&&RawMatches(values,_actual);}
        internal bool PrepareSimulationOnly(ContinuousSimulationVitalsSnapshot expected,ContinuousVitalsProposal proposal,out PreparedSimulationMutation prepared,out string reason)
        {
            prepared=null;reason="stale_simulation_vitals_proposal";
            if(expected==null||proposal==null||!ReferenceEquals(expected.Values,proposal.Before))return false;
            lock(CommonParticipantGate.SyncRoot)
            {if(!expected.MatchesUnderGate(this))return false;_simulationPending?.Dispose();}
            var candidate=new PreparedSimulationMutation(this,expected,proposal);
            lock(CommonParticipantGate.SyncRoot)
            {if(!expected.MatchesUnderGate(this)||_simulationPending!=null){candidate.Dispose();return false;}_simulationPending=candidate;prepared=candidate;reason="";return true;}
        }
        internal void RevokeCaptureForSimulationUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            if(!SimulationLifecycleUnderGate())throw new InvalidOperationException("simulation_vitals_lifecycle_changed");
            _pending?.Dispose();
            if(!_invalid){_invalid=true;_registry.Invalidate("simulation_vitals_noncapturing_continuation");}
        }
        internal sealed class PreparedSimulationMutation:IDisposable
        {
            readonly ActualVitalsProjectionBridge _owner;
            readonly ContinuousSimulationVitalsSnapshot _before;
            readonly ContinuousVitalsProposal _proposal;
            readonly object _nextGeneration=new object();
            bool _used;
            internal PreparedSimulationMutation(ActualVitalsProjectionBridge owner,ContinuousSimulationVitalsSnapshot before,ContinuousVitalsProposal proposal)
            {_owner=owner;_before=before;_proposal=proposal;}
            internal bool MatchesUnderGate()
            {CommonParticipantGate.RequireHeld();return !_used&&ReferenceEquals(_owner._simulationPending,this)&&_before.MatchesUnderGate(_owner)&&ReferenceEquals(_before.Values,_proposal.Before);}
            internal bool TryInstallNativeOnlyUnderGate(out string reason)
            {
                CommonParticipantGate.RequireHeld();if(!MatchesUnderGate()){reason="stale_simulation_vitals_proposal";return false;}
                // All revocation/disposal work BEFORE the first actual assignment.
                _owner.RevokeCaptureForSimulationUnderGate();
                var m=_owner._actual;var v=_proposal.After;
                m.Health=v.At(0);m.MaxHealth=v.At(1);m.Stamina=v.At(2);m.MaxStamina=v.At(3);m.Hunger=v.At(4);m.MaxHunger=v.At(5);m.Thirst=v.At(6);m.MaxThirst=v.At(7);
                m.HealthDrainRate=v.At(8);m.StaminaDrainRate=v.At(9);m.HungerDrainRate=v.At(10);m.ThirstDrainRate=v.At(11);m.StaminaRecoveryRate=v.At(12);m.HealthRecoveryRate=v.At(13);
                _owner._values=v;_owner._simulationGeneration=_nextGeneration;_owner._simulationPending=null;_used=true;reason="";return true;
            }
            public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(ReferenceEquals(_owner._simulationPending,this))_owner._simulationPending=null;_used=true;}}
        }
        internal bool TryBuildWholeWorldSave(out string reason){reason="other_world_producers_not_enrolled";return false;}
        internal sealed class PreparedChange:IDisposable
        {
            readonly ActualVitalsProjectionBridge _owner;
            readonly ActualVitalsProjectionSnapshot _before;
            readonly DiagnosticVitalsValues _next;
            readonly ProjectionPreparationCursor _plan;
            readonly object _nextSimulationGeneration=new object();
            bool _used;
            internal PreparedChange(ActualVitalsProjectionBridge owner,ActualVitalsProjectionSnapshot before,DiagnosticVitalsValues next,ProjectionPreparationCursor plan)
            {_owner=owner;_before=before;_next=next;_plan=plan;}
            internal bool MatchesUnderGate()
            {CommonParticipantGate.RequireHeld();return !_used&&ReferenceEquals(_owner._pending,this)&&_before.MatchesUnderGate(_owner)&&_plan.Status==ProjectionCursorStatus.Complete;}
            internal bool TryInstallActualRawAndProjectionUnderGate(out string reason)
            {
                CommonParticipantGate.RequireHeld();if(!MatchesUnderGate()){reason="actual_vitals_final_guard";return false;}
                if(!_owner._registry.TryPublish(_plan,out reason))return false;
                // SAME actual model; no delegates, virtual setters, callbacks or allocations in suffix.
                var m=_owner._actual;var v=_next;
                m.Health=v.At(0);m.MaxHealth=v.At(1);m.Stamina=v.At(2);m.MaxStamina=v.At(3);m.Hunger=v.At(4);m.MaxHunger=v.At(5);m.Thirst=v.At(6);m.MaxThirst=v.At(7);
                m.HealthDrainRate=v.At(8);m.StaminaDrainRate=v.At(9);m.HungerDrainRate=v.At(10);m.ThirstDrainRate=v.At(11);m.StaminaRecoveryRate=v.At(12);m.HealthRecoveryRate=v.At(13);
                _owner._values=v;_owner._simulationGeneration=_nextSimulationGeneration;_owner._stamp=_before.Stamp+1;_owner._pending=null;_used=true;return true;
            }
            public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(!_used){_used=true;if(ReferenceEquals(_owner._pending,this))_owner._pending=null;}_plan.Dispose();}}
        }
        public void Dispose(){lock(CommonParticipantGate.SyncRoot){if(_disposed)return;_disposed=true;_pending?.Dispose();_simulationPending?.Dispose();_registry.Dispose();}}
    }
}
