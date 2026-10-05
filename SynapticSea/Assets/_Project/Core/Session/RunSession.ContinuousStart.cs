using System;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        WorkerAdmissionEpoch _continuousStartEpoch;
        WorkerAdmissionSlot _continuousStartSlot;
        WorkerOriginAdmissionQueue _continuousStartQueue;
        WorkerAdmissionJob _continuousStartJob;
        DomainTransactionCoordinator.ContinuousStartPending _continuousStartPreparation;
        ContinuousOrdinaryStartSource _continuousStartParticipants;
        object _continuousStartInputGeneration,_continuousStartScene,_continuousStartPointRoot;
        AuxiliaryServicePoint _continuousStartPoint;
        Vec3 _continuousStartPosition,_continuousStartPointPosition;
        string _continuousStartTransaction,_continuousStartService;
        ulong _continuousStartDamage;
        CheckpointProofAdmission.Result _continuousRetainedProofResult;
        AdmittedAuxiliaryHistory _continuousWorkHistory;
        DomainTransactionCoordinator _continuousWorkOwner;
        long _continuousWorkOwnerRevision;
        string _continuousWorkDescriptorHash;
        AuxiliaryServicePoint _continuousBoundWorkPoint;
        object _continuousBoundWorkPointRoot;
        Vec3 _continuousBoundWorkPointLocal;

        // Ordinary admitted Continue calls during the private bootstrap window after actual
        // restored models/cohort are selected. No fresh aux_start receipt or stamina replay.
        void BindContinuousResumedAuxiliary(AdmittedAuxiliaryHistory history,CheckpointProofAdmission.Result admitted)
        {
            if(!ContinuousDiagnosticBootstrapOpen||_continuousWorldCohort==null||_continuousAuxiliaryRuntime!=null||
                history==null||admitted==null||!admitted.Lease.IsCurrent||!history.ResourcesCurrent||history.OriginalSeed.Run!=RunId||history.OriginalSeed.Actor!=PLAYER_LOCAL_ID||
                !ReferenceEquals(history.Lease,_continuousWorldCohort.ResourceLease)||_continuousWorkResources==null)
                throw new InvalidOperationException("resume_binding_unavailable");
            var owner=_componentDomain.GetSummary();
            if(_componentDomain.HashContext.Hash(owner)!=history.OwnerDigest)throw new InvalidOperationException("resume_owner_mismatch");
            var point=AuxiliaryServicePoints.Find(p=>p.ServiceId==history.OriginalSeed.Service&&p.OwnerId==HomeShip?.ShipId&&p.IsValid&&p.IsInsideTree);
            if(point==null)throw new InvalidOperationException("resume_authored_service_missing");
            var descriptors=_componentDomain.GetParticipantProjection("auxiliary_services").GetDictOrEmpty("descriptors");
            var original=history.CopyOriginalOwner().GetDictOrEmpty("participating_state").GetDictOrEmpty("auxiliary_services").GetDictOrEmpty("descriptors");
            if(!descriptors.Has(point.ServiceId)||!_componentDomain.HashContext.Equal(descriptors.Get(point.ServiceId),original.Get(point.ServiceId)))
                throw new InvalidOperationException("resume_service_descriptor_mismatch");
            var frame=new AuxiliaryWorkFrame(0,VitalsState.Stamina,VitalsState.MaxStamina,WoundState?.WorkSpeedMultiplier()??1,"explicit_resume_required",false,HoldToWorkEnabled,false,false,false);
            var context=new DiagnosticAuxiliaryPairContext(_continuousWorkFrameOrdinal,frame,InventoryState,history.Lease,_continuousOrdinaryContextBinding);
            var runtime=AuxiliaryWorkRuntime.CreateFromAdmittedRunSessionHistory(HomeShip.ShipId,_componentDomain.HashContext.Algorithm,
                history,AuxiliaryWorkRuntime.MaximumRetainedSteps,new AuxiliaryEvidenceLimits(256,65536,2),_continuousWorldCohort.VitalsBridge,context);
            var pointRoot=point.Parent;var local=point.LocalPosition;string descriptorHash=_componentDomain.HashContext.Hash(descriptors.Get(point.ServiceId));
            var actualParticipants=ReadComponentParticipants(PaidState(owner),OwnerHashContext(owner));
            if(!PaidEqual(OwnerHashContext(owner),owner.Get("participating_state"),actualParticipants))throw new InvalidOperationException("resume_actual_participants_mismatch");
            var actualInventory=InventoryState;var actualProgression=PlayerProgression;var actualTraining=TrainingEventBus;
            using(var attempt=CommonParticipantGate.BeginAttempt())
            {
                if(!ContinuousDiagnosticBootstrapOpen||!history.ResourcesCurrent||!ReferenceEquals(actualInventory,InventoryState)||!ReferenceEquals(actualProgression,PlayerProgression)||!ReferenceEquals(actualTraining,TrainingEventBus))
                    throw new InvalidOperationException("resume_binding_stale");
                _continuousAuxiliaryRuntime=runtime;_continuousWorkContext=context;_continuousWorkHistory=history;
                _continuousWorkOwner=_componentDomain;_continuousWorkOwnerRevision=_componentDomain.ContinuousRevisionUnderGate;
                _continuousWorkDescriptorHash=descriptorHash;_continuousBoundWorkPoint=point;_continuousBoundWorkPointRoot=pointRoot;_continuousBoundWorkPointLocal=local;
                _auxConsent=false;_continuousConsentCaptured=false;_continuousResumeIntent=false;_workHoldInput=false;
                _continuousPauseReason="explicit_resume_required";_continuousRetainedProofResult=admitted;
            }
        }

        void CancelContinuousQueuedStart()
        {
            _continuousStartJob?.Cancel();_continuousStartEpoch?.Revoke();
            _continuousStartPreparation?.CancelExact();_continuousStartPreparation=null;
            // Running cancelled task retains queue reservation until completed-only TryTake.
            // No accepted work/owner/stamina is restored or discarded.
        }
        // Called within the actual outer Tick batch. Polling never waits for the worker.
        void PumpContinuousAuxiliaryStart()
        {
            if(!ContinuousAuxiliaryRuntimeActive||!_inTick)return;
            if(_continuousStartJob!=null)
            {
                bool stillOwned=ReferenceEquals(_continuousStartInputGeneration,_continuousInputGeneration)&&
                    ReferenceEquals(_continuousStartInputGeneration,_continuousPendingGeneration)&&_continuousPendingOperation=="start";
                if(!stillOwned||SliceComplete||VitalsState.Health<=0||PlayerMoving||PlayerPos!=_continuousStartPosition||ContinuousAuxiliaryDamageEpoch!=_continuousStartDamage)
                    CancelContinuousQueuedStart();
                if(_continuousStartQueue.Poll(_continuousStartJob)==WorkerAdmissionStatus.Pending)return;
                var job=_continuousStartJob;_continuousStartJob=null;
                if(!_continuousStartQueue.TryTake(job,out var outcome))return;
                if(!stillOwned){CancelContinuousQueuedStart();return;}
                if(outcome.Status!=WorkerAdmissionStatus.Accepted||
                    !AdmittedAuxiliaryHistory.TryIssueFresh(outcome.Origin,out var history,out var reason))
                {PauseContinuousAuxiliaryIntent(outcome.Reason);return;}
                if(!FinishContinuousAuxiliaryStart(outcome.Origin,history,out reason))PauseContinuousAuxiliaryIntent(reason);
                return;
            }
            if(_continuousPendingOperation==null)return;
            if(_continuousPendingOperation!="start")
            {PauseContinuousAuxiliaryIntent("unfinished_witness_take_not_supported");return;}
            if(_continuousAuxiliaryRuntime!=null){PauseContinuousAuxiliaryIntent("auxiliary_busy");return;}
            string service=_continuousPendingService;
            string gate=AuxiliaryGate(service);
            if(gate!="ready"){PauseContinuousAuxiliaryIntent(gate);return;}
            var cohort=_continuousWorldCohort;
            if(cohort==null||!TryCaptureContinuousOrdinaryStartSource(out var participants,out var failure))
            {PauseContinuousAuxiliaryIntent("continuous_participants_unavailable");return;}
            var point=AuxiliaryServicePoints.Find(p=>p.ServiceId==service&&p.OwnerId==HomeShip?.ShipId&&p.IsValid&&p.IsInsideTree);
            if(point==null){PauseContinuousAuxiliaryIntent("missing_service_point");return;}
            var before=participants.CaptureBeforeOutsideGate();
            var command=new GdDict{{"command_id","aux:"+RunId+":"+(before.GetInt("command_sequence")+1)},
                {"operation","aux_start"},{"run_id",RunId},{"actor_id",PLAYER_LOCAL_ID},{"service_id",service},
                {"delta_seconds",0.0},{"elapsed_seconds",0.0},{"speed",1.0},{"stamina_before",0.0},{"stamina_after",0.0},{"reason",""}};
            // Stock preparation validates exact ordinary start/conservation. No live summary is applied.
            var prepared=_componentDomain.PrepareAuxiliary(command,candidate=>
            {var effect=AuxiliaryServiceState.Apply(candidate,"aux_start",service);candidate["command_sequence"]=checked(candidate.GetInt("command_sequence")+1);SetPaidProjections(candidate);return effect;});
            if(!prepared.GetBool("ok")||prepared.GetBool("committed"))
            {PauseContinuousAuxiliaryIntent(prepared.GetString("reason","start_preparation_refused"));return;}
            _continuousStartPreparation=_componentDomain.CaptureContinuousStartPending(prepared.GetString("transaction_id"));
            if(!DomainBundle.TryCreateWorkerInput(prepared.GetDictOrEmpty("candidate"),out var input,out failure))
            {PauseContinuousAuxiliaryIntent(failure);return;}
            _continuousStartEpoch?.Revoke();
            _continuousStartEpoch=new WorkerAdmissionEpoch(RunId+":continuous-start",RunId,PLAYER_LOCAL_ID);
            var basis=new WorkerAdmissionState(_continuousStartEpoch,0,0,0,0);
            _continuousStartSlot=new WorkerAdmissionSlot(basis);_continuousStartQueue=new WorkerOriginAdmissionQueue(_continuousStartSlot);
            if(!_continuousStartQueue.BeginOrigin(input,cohort.ResourceLease,basis,out _continuousStartJob,out failure))
            {PauseContinuousAuxiliaryIntent(failure);return;}
            _continuousStartParticipants=participants;_continuousStartInputGeneration=_continuousPendingGeneration;
            _continuousStartScene=Scene;_continuousStartPosition=PlayerPos;_continuousStartPoint=point;
            _continuousStartPointRoot=point.Parent;_continuousStartPointPosition=point.GlobalPosition;
            _continuousStartTransaction=prepared.GetString("transaction_id");_continuousStartService=service;
            _continuousStartDamage=ContinuousAuxiliaryDamageEpoch;
        }
        bool FinishContinuousAuxiliaryStart(AdmittedWorkerOrigin admitted,AdmittedAuxiliaryHistory history,out string reason)
        {
            reason="stale_start_context";
            var cohort=_continuousWorldCohort;var point=_continuousStartPoint;var owner=_componentDomain;
            if(!ContinuousAuxiliaryRuntimeActive||cohort==null||history.SourceSessionId!=_continuousStartEpoch.SessionId||
                history.OriginalSeed.Service!=_continuousStartService||history.OriginalSeed.Run!=RunId||history.AcceptedSteps!=0||
                !history.ResourcesCurrent||!ReferenceEquals(history.Lease,cohort.ResourceLease)||
                !ReferenceEquals(_continuousStartInputGeneration,_continuousInputGeneration)||
                !ReferenceEquals(_continuousStartInputGeneration,_continuousPendingGeneration)||
                !ReferenceEquals(_continuousStartScene,Scene)||PlayerPos!=_continuousStartPosition||
                point==null||!point.IsValid||!point.IsInsideTree||!ReferenceEquals(point.Parent,_continuousStartPointRoot)||
                point.GlobalPosition!=_continuousStartPointPosition||point.OwnerId!=HomeShip?.ShipId||
                AuxiliaryGate(_continuousStartService)!="ready"||ContinuousAuxiliaryDamageEpoch!=_continuousStartDamage)return false;
            if(!owner.TryPrepareContinuousStartPublication(_continuousStartTransaction,admitted,out var publication,out reason))return false;
            if(_continuousWorkResources==null){reason="work_context_not_bootstrapped";return false;}
            var initialFrame=new AuxiliaryWorkFrame(0,VitalsState.Stamina,VitalsState.MaxStamina,WoundState?.WorkSpeedMultiplier()??1,"ready",true,HoldToWorkEnabled,IsWorkInteractHeld,false,false);
            var context=new DiagnosticAuxiliaryPairContext(_continuousWorkFrameOrdinal,initialFrame,InventoryState,cohort.ResourceLease,_continuousOrdinaryContextBinding);
            var runtime=AuxiliaryWorkRuntime.CreateFromAdmittedRunSessionHistory(HomeShip.ShipId,
                admitted.HashAlgorithm,history,AuxiliaryWorkRuntime.MaximumRetainedSteps,
                new AuxiliaryEvidenceLimits(256,65536,2),cohort.VitalsBridge,context);
            var descriptor=admitted.Bundle.GetParticipantProjection("auxiliary_services").GetDictOrEmpty("descriptors").GetDictOrEmpty(runtime.ServiceId);
            string descriptorHash=admitted.Bundle.HashContext.Hash(descriptor);var localPosition=point.LocalPosition;
            if(!_continuousStartParticipants.TryConfirmFreshOutsideGate(this,out reason))return false;
            if(!point.IsValid||!point.IsInsideTree||point.GlobalPosition!=_continuousStartPointPosition||Scene.PlayerPosition!=_continuousStartPosition){reason="stale_start_scene_after_source_confirmation";return false;}
            using(var attempt=CommonParticipantGate.BeginAttempt())
            {
                if(!_inTick||!ContinuousAuxiliaryRuntimeActive||!ReferenceEquals(cohort,_continuousWorldCohort)||
                    !ReferenceEquals(owner,_componentDomain)||_continuousAuxiliaryRuntime!=null||
                    !ReferenceEquals(_continuousStartInputGeneration,_continuousInputGeneration)||
                    !ReferenceEquals(_continuousStartScene,Scene)||PlayerPos!=_continuousStartPosition||PlayerMoving||
                    VitalsState.Health<=0||ContinuousAuxiliaryDamageEpoch!=_continuousStartDamage||
                    !point.IsValid||!ReferenceEquals(point.Parent,_continuousStartPointRoot)||
                    !_continuousStartParticipants.MatchesUnderGate(this)||
                    !history.ResourcesCurrent||!publication.MatchesUnderGate())return false;
                if(!publication.TryInstallUnderGate(attempt)){reason="start_publication_refused";return false;}
                // All preparation is complete. No callback, allocation or historical Vitals write after owner install.
                _continuousAuxiliaryRuntime=runtime;_continuousWorkContext=context;_continuousWorkHistory=history;
                _continuousWorkOwner=owner;_continuousWorkOwnerRevision=admitted.Revision;_continuousWorkDescriptorHash=descriptorHash;
                _continuousBoundWorkPoint=point;_continuousBoundWorkPointRoot=_continuousStartPointRoot;_continuousBoundWorkPointLocal=localPosition;
                _auxConsent=true;_auxPosition=_continuousStartPosition;_continuousConsentDamageEpoch=_continuousStartDamage;
                _continuousConsentCaptured=true;_continuousPendingOperation=null;_continuousPendingService=null;_continuousPendingGeneration=null;
            }
            publication.RetirePendingAfterGate();_continuousStartPreparation=null;_continuousStartParticipants=null;
            _continuousStartQueue=null;_continuousStartSlot=null;RefreshContinuousAuxiliaryHud();reason="start_committed";return true;
        }
    }
}
