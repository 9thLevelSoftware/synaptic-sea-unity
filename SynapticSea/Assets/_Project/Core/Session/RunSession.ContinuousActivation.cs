using System;
using System.Threading;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        ContinuousWorldProducerCohort _continuousWorldCohort;
        public bool ContinuousAuxiliaryRuntimeActive=>_continuousWorldCohort!=null&&_continuousWorldCohort.IsRoutingActiveFor(this);
        // Actual bootstrap entry. Never resets/replaces actual models or issues authority from a test DTO.
        public bool TryActivateContinuousAuxiliaryDiagnostic(out string reason)
        {
            reason="continuous_bootstrap_unavailable";
            if(!Deps.EnableContinuousAuxiliaryDiagnostic||!ContinuousDiagnosticBootstrapOpen||_continuousWorldCohort!=null||
                _continuousActualVitalsWriter!=null||_continuousActualOxygenWriter!=null||_continuousWorkResources!=null||_continuousOrdinaryContextBinding!=null||
                !PlayableStarted||SliceComplete||Scene==null||HomeShip==null||Loader==null||VitalsState==null||OxygenState==null||WoundState==null||
                InventoryState==null||PlayerProgression==null||TrainingEventBus==null||!AuxiliaryServicesEnabled||_componentDomain==null)return false;
            if(!TryPublishContinuousBootstrapResources(out var resources,out reason))return false;
            if(SaveLoadService==null){reason="continuous_save_service_missing";return false;}
            if(!SaveLoadService.TryEnableContinuousDiagnosticReader(out reason))return false;
            // Synchronize authentic ordinary earned stock once before closing further participant writers.
            var actualOwner=CapturePaidCraftingDomain();
            if(!DomainBundle.TryCreate(actualOwner,out _,out reason))return false;
            ActualVitalsProjectionBridge vitals=null;ActualOxygenProjectionBridge oxygen=null;ActualWoundsProjectionBridge wounds=null;
            try
            {
                vitals=new ActualVitalsProjectionBridge(this);oxygen=new ActualOxygenProjectionBridge(this);wounds=new ActualWoundsProjectionBridge(this);
                var cohort=new ContinuousWorldProducerCohort(this,resources,vitals,oxygen,wounds);
                _continuousWorldCohort=cohort;
                BindContinuousVitalsProducer(vitals);BindContinuousOxygenProducer(oxygen);BindContinuousWorkContext(resources);
                _componentDomain.BindContinuousUnfinishedCommandFence(this);
                reason="continuous_actual_runtime_active";return true;
            }
            catch(Exception failure)
            {
                _continuousWorldCohort?.RevokeCapture("activation_failed");_continuousWorldCohort=null;
                // Entry refused preexisting bindings; restore ONLY fields created by this failed attempt.
                _continuousActualVitalsWriter=null;_continuousActualOxygenWriter=null;_continuousWorkResources=null;
                _continuousWorkInventory=null;_continuousOrdinaryContextBinding=null;
                vitals?.Dispose();oxygen?.Dispose();wounds?.Dispose();
                reason="continuous_activation_failed:"+failure.Message;return false;
            }
        }
        // Main-thread safe-end-Tick full synchronous cut is the selected intermediate route.
        // It does not certify incremental projection coverage or a frame-time bound.
        internal void RequireContinuousClosedCut(ContinuousSafeEndTick ticket)
        {
            lock(CommonParticipantGate.SyncRoot)
                if(_continuousWorldCohort==null||!_continuousWorldCohort.CanAcceptAuxiliaryUnderGate()||ticket==null||!ticket.MatchesUnderGate(this))
                    throw new InvalidOperationException("continuous_closed_cut_unavailable");
        }
        internal sealed class ContinuousOutputLifetime
        {
            readonly object _issuer;int _retired;
            internal readonly ResourceAuthorityLease ResourceLease;
            ContinuousOutputLifetime(object issuer,ResourceAuthorityLease resources){_issuer=issuer;ResourceLease=resources;}
            internal static ContinuousOutputLifetime Issue(object issuer,ResourceAuthorityLease resources)
            {
                if(!(issuer is ContinuousWorldProducerCohort))throw new InvalidOperationException("continuous_output_lifetime_unissued");
                return new ContinuousOutputLifetime(issuer,resources);
            }
            internal bool IsCurrent=>Volatile.Read(ref _retired)==0&&ResourceLease.IsCurrent;
            internal void Retire()=>Interlocked.Exchange(ref _retired,1);
        }
        internal ContinuousOutputLifetime GetContinuousOutputLifetime(ContinuousSafeEndTick ticket)
        {RequireContinuousClosedCut(ticket);return _continuousWorldCohort._outputLifetime;}
        sealed class ContinuousWorldProducerCohort
        {
            readonly RunSession _session;readonly object _scene;readonly string _run;
            readonly VitalsState _vitals;readonly OxygenState _oxygen;readonly WoundState _wounds;
            readonly InventoryState _inventory;readonly PlayerProgressionState _progression;readonly TrainingEventBus _training;
            readonly int _thread=Thread.CurrentThread.ManagedThreadId;bool _capture=true;
            internal readonly ContinuousOutputLifetime _outputLifetime;
            internal readonly ResourceAuthorityLease ResourceLease;
            internal readonly ActualVitalsProjectionBridge VitalsBridge;
            internal readonly ActualOxygenProjectionBridge OxygenBridge;
            internal readonly ActualWoundsProjectionBridge WoundsBridge;
            internal ContinuousWorldProducerCohort(RunSession session,ResourceAuthorityLease resources,ActualVitalsProjectionBridge vitals,ActualOxygenProjectionBridge oxygen,ActualWoundsProjectionBridge wounds)
            {_session=session;_scene=session.Scene;_run=session.RunId;_vitals=session.VitalsState;_oxygen=session.OxygenState;_wounds=session.WoundState;
             _inventory=session.InventoryState;_progression=session.PlayerProgression;_training=session.TrainingEventBus;ResourceLease=resources;VitalsBridge=vitals;OxygenBridge=oxygen;WoundsBridge=wounds;
             _outputLifetime=ContinuousOutputLifetime.Issue(this,resources);}
            internal bool IsRoutingActiveFor(RunSession session)=>ReferenceEquals(session,_session)&&Thread.CurrentThread.ManagedThreadId==_thread&&
                ReferenceEquals(session.Scene,_scene)&&session.RunId==_run&&ReferenceEquals(session.VitalsState,_vitals)&&ReferenceEquals(session.OxygenState,_oxygen)&&ReferenceEquals(session.WoundState,_wounds);
            internal bool CanAcceptAuxiliaryUnderGate()
            {
                CommonParticipantGate.RequireHeld();
                return _capture&&IsRoutingActiveFor(_session)&&!_session.SliceComplete&&_session.VitalsState.Health>0&&!_session.ComponentGenerationRestoreInProgress&&ResourceLease.IsCurrent&&
                    ReferenceEquals(_session.InventoryState,_inventory)&&ReferenceEquals(_session.PlayerProgression,_progression)&&ReferenceEquals(_session.TrainingEventBus,_training)&&
                    VitalsBridge.CaptureAvailableUnderGate()&&OxygenBridge.CaptureAvailableUnderGate()&&WoundsBridge.SourceCurrentUnderGate();
            }
            internal bool CanAcceptAuxiliaryUnderGateFromMainThread(){lock(CommonParticipantGate.SyncRoot)return CanAcceptAuxiliaryUnderGate();}
            internal void RevokeCapture(string reason){if(Thread.CurrentThread.ManagedThreadId!=_thread)throw new InvalidOperationException("continuous_cohort_thread");_outputLifetime.Retire();_capture=false;}
        }
    }
}
