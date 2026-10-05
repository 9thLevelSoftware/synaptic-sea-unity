using System;
using System.Linq;
using System.Threading;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        // Private bootstrap enrollment only. Not activation, SaveReady, or paid completion authority.
        ResourceAuthorityLease _continuousWorkResources;
        InventoryState _continuousWorkInventory;
        DiagnosticAuxiliaryPairContext _continuousWorkContext;
        long _continuousWorkFrameOrdinal;
        ulong _continuousConsentDamageEpoch;
        bool _continuousConsentCaptured;
        // Source-authenticated start, or explicit presentation-only resume after current damage/input checks.
        void CaptureContinuousAuxiliaryConsent()
        {
            if(!_auxConsent || _continuousActualVitalsWriter==null)throw new InvalidOperationException("continuous_consent_not_committed");
            _continuousConsentDamageEpoch=_continuousActualVitalsWriter.DamageEpoch;_continuousConsentCaptured=true;_auxPosition=PlayerPos;
        }
        readonly object _continuousWorkContextIssuer=new object();
        void BindContinuousWorkContext(ResourceAuthorityLease resources)
        {
            if(!ContinuousDiagnosticBootstrapOpen || _continuousWorkResources!=null || resources==null || !resources.IsCurrent)
                throw new InvalidOperationException("continuous_context_bootstrap");
            _continuousOrdinaryContextBinding=new ContinuousOrdinaryWorkBinding(this,_continuousWorkContextIssuer); // privately issued exact actual cohort binding
            _continuousWorkResources=resources;_continuousWorkInventory=InventoryState;
        }
        bool TryPrepareContinuousWorkFrame(string service,double delta,out ContinuousWorkFrameReceipt receipt,out string reason)
        {
            receipt=null;reason="continuous_context_unbound";
            if(_continuousWorkResources==null || _continuousActualVitalsWriter==null)return false;
            if(!_inTick || _continuousMutationDepth<=0){reason="work_requires_active_tick";return false;}
            if(!_continuousWorldCohort.CanAcceptAuxiliaryUnderGateFromMainThread()||!_continuousActualVitalsWriter.CaptureAvailable){reason="continuous_capture_revoked";return false;}
            if(_continuousWorkFrameOrdinal==long.MaxValue){reason="continuous_frame_capacity";return false;}
            var sampledScene=Scene;var sampledHome=HomeShip;var sampledPosition=PlayerPos;
            // Geometry/engine/ordinary policy reads occur BEFORE the no-external-call final section.
            var point=_continuousBoundWorkPoint;
            if(point==null||point.ServiceId!=service||!point.IsValid||!point.IsInsideTree||point.OwnerId!=HomeShip?.ShipId||!ReferenceEquals(point.Parent,_continuousBoundWorkPointRoot)||point.LocalPosition!=_continuousBoundWorkPointLocal)
            {reason="stale_bound_service_point";return false;}
            var pointRoot=point?.Parent;var pointPosition=point==null?Vec3.Inf:point.GlobalPosition;
            string gate="ready";
            if(!PlayableStarted || SliceComplete || !HasPlayer || VitalsState==null || VitalsState.IsIncapacitated() || AwayFromStart)gate="actor_unavailable";
            else if(point==null || !point.IsPlayerInDirectRangeStrict(PlayerPos))gate="out_of_range";
            else if(Deps.LosProbe?.HasSpace==true && Deps.LosProbe.IntersectRay(PlayerPos+new Vec3(0,.8,0),point.GlobalPosition,out Vec3 hit) && (hit-point.GlobalPosition).LengthSquared()>.04)gate="no_line_of_sight";
            else if(PlayerMoving || PlayerPos!=_auxPosition)gate="moving";
            else if(!_auxConsent || !_continuousConsentCaptured)gate="explicit_resume_required";
            else if(_continuousActualVitalsWriter.DamageEpoch!=_continuousConsentDamageEpoch)gate="damage";
            else if(ManualStudyRunning || WorkActionDriver?.IsWorking()==true || RepairPoints.Any(p=>p.Channeling) || BreachSealPoints.Any(p=>p.Channeling) || FireSuppressionPoints.Any(p=>p.Channeling) || DockBarriers.Any(p=>p.Channeling) || _componentDomain.GetParticipantProjection("paid_crafting").GetDictOrEmpty("jobs").Values.OfType<GdDict>().Any(j=>j.GetString("input_state")=="paid" && !PaidCraftingState.Terminal(j)))gate="work_busy";
            else if(InventoryState.GetQuantity("crowbar")<1)gate="missing_crowbar";
            var descriptors=_componentDomain.GetParticipantProjection("auxiliary_services").GetDictOrEmpty("descriptors");
            if(!descriptors.Has(service)){reason="missing_authenticated_service";return false;}
            var materials=descriptors.GetDictOrEmpty(service).GetDictOrEmpty("materials_consumed");
            if(gate=="ready")foreach(var part in materials)if(InventoryState.GetQuantity(V.Str(part.Key))<V.I64(part.Value)){gate="missing_materials";break;}
            if(gate!="ready"){reason=gate;return false;}
            var values=_continuousActualVitalsWriter; // identity guard; live values sampled after engine reads
            double stamina=VitalsState.Stamina,max=VitalsState.MaxStamina,wounds=WoundState?.WorkSpeedMultiplier()??1;
            if(!_inTick||!ReferenceEquals(sampledScene,Scene)||!ReferenceEquals(sampledHome,HomeShip)||sampledPosition!=PlayerPos||
                point==null||!point.IsValid||!point.IsInsideTree||!ReferenceEquals(pointRoot,point.Parent)||point.OwnerId!=HomeShip?.ShipId||point.ServiceId!=service||point.GlobalPosition!=pointPosition||!point.IsPlayerInDirectRangeStrict(PlayerPos)){reason="stale_scene_sampling";return false;}
            var frame=new AuxiliaryWorkFrame(delta,stamina,max,wounds,gate,_auxConsent,HoldToWorkEnabled,IsWorkInteractHeld,PlayerMoving,values.DamageEpoch!=_continuousConsentDamageEpoch);
            long ordinal=_continuousWorkFrameOrdinal+1;
            if(_continuousWorkContext==null)_continuousWorkContext=new DiagnosticAuxiliaryPairContext(ordinal,frame,InventoryState,_continuousWorkResources,_continuousOrdinaryContextBinding);
            else _continuousWorkContext.Replace(ordinal,frame,InventoryState,_continuousWorkResources);
            _continuousWorkFrameOrdinal=ordinal;
            if(!TryCaptureContinuousOrdinaryStartSource(out _continuousWorkFrameSource,out reason)||!_continuousWorkFrameSource.TryConfirmFreshOutsideGate(this,out reason))return false;
            receipt=new ContinuousWorkFrameReceipt(this,_continuousWorkContextIssuer,point,values,ordinal);
            reason=gate;return true;
        }
        AuxiliaryWorkRuntime _continuousAuxiliaryRuntime;
        // Authentic admitted-start/history factory must set this private binding before dispatch.
        // This setter is intentionally absent until its paid-owner capability API is reviewed.
        bool TryTickContinuousAuxiliaryWork(double delta,out bool committed,out AuxiliaryWorkStepResult result,out string reason)
        {
            committed=false;result=null;reason="continuous_work_unbound";
            var runtime=_continuousAuxiliaryRuntime;if(!ContinuousAuxiliaryRuntimeActive || runtime==null)return false;
            if(runtime.RetainedStepCount==runtime.Capacity)
            {
                if(!runtime.TryPrepareEvidenceRequestCut(runtime.EvidenceJournal.StructuralVersion,out var cut,out reason))return true;
                using(var attempt=CommonParticipantGate.BeginAttempt())
                    if(!ReferenceEquals(runtime,_continuousAuxiliaryRuntime)||!cut.TryInstallTransportUnderGate(out _)){reason="evidence_rotation_refused";return true;}
            }
            ConsumeContinuousResumeIntent();
            if(!TryPrepareContinuousWorkFrame(runtime.ServiceId,delta,out var receipt,out reason)){PauseContinuousAuxiliaryIntent(reason);return true;}
            if(!runtime.TryPrepareRunSessionPair(out var plan,out result,out reason))return true;
            using(plan)using(var attempt=CommonParticipantGate.BeginAttempt())
            {
                if(!ReferenceEquals(runtime,_continuousAuxiliaryRuntime)||!runtime.MatchesRunSessionContextUnderGate(this,_continuousWorkContext)||!receipt.MatchesUnderGate(this))
                {reason="stale_actual_work_context";return true;}
                // Exact paired stamina/progress/history installation once. No callbacks in this scope.
                committed=plan.TryInstallUnderGate(runtime,attempt,out reason);
            }
            return true; // handled, not canonical completion/reward; spent accepted steps remain retained
        }
        internal sealed class ContinuousWorkFrameReceipt
        {
            readonly RunSession _session;readonly object _issuer,_scene,_home,_occupancy,_point;
            readonly string _run;readonly Vec3 _position;readonly bool _moving,_consent,_held;
            readonly ContinuousActualVitalsWriter _writer;readonly ulong _damage,_boundary;readonly long _ordinal;
            readonly InventoryState _inventory;readonly ulong _inventoryStamp;readonly ResourceAuthorityLease _resources;
            internal ContinuousWorkFrameReceipt(RunSession s,object issuer,object point,ContinuousActualVitalsWriter writer,long ordinal)
            {
                if(!ReferenceEquals(issuer,s._continuousWorkContextIssuer))throw new InvalidOperationException("foreign_context_issuer");
                _session=s;_issuer=issuer;_scene=s.Scene;_run=s.RunId;_home=s.HomeShip;_occupancy=s.CurrentOccupancy;_point=point;
                _position=s.PlayerPos;_moving=s.PlayerMoving;_consent=s._auxConsent;_held=s.IsWorkInteractHeld;_writer=writer;_damage=writer.DamageEpoch;_boundary=s._continuousBoundaryEpoch;_ordinal=ordinal;
                _inventory=s.InventoryState;_inventoryStamp=0;_resources=s._continuousWorkResources;
            }
            internal bool MatchesUnderGate(RunSession s)
            {
                CommonParticipantGate.RequireHeld();
                // No raycasts, engine access, callbacks, model summaries, or historical Vitals restoration here.
                return s._inTick&&ReferenceEquals(s,_session)&&ReferenceEquals(_issuer,s._continuousWorkContextIssuer)&&ReferenceEquals(_scene,s.Scene)&&s.RunId==_run&&
                    ReferenceEquals(_home,s.HomeShip)&&ReferenceEquals(_occupancy,s.CurrentOccupancy)&&ReferenceEquals(_writer,s._continuousActualVitalsWriter)&&
                    s._continuousBoundaryEpoch==_boundary&&s._continuousMutationDepth>0&&s._continuousMutationThread==Thread.CurrentThread.ManagedThreadId&&s._continuousWorkFrameOrdinal==_ordinal&&
                    s.HasPlayer&&!s.SliceComplete&&!s.ComponentGenerationRestoreInProgress&&s.PlayerPos==_position&&s.PlayerMoving==_moving&&s._auxConsent==_consent&&s.IsWorkInteractHeld==_held&&
                    s.VitalsState!=null&&s.VitalsState.Health>0&&_writer.DamageEpoch==_damage&&_writer.CaptureAvailable&&
                    ReferenceEquals(_inventory,s.InventoryState)&&ReferenceEquals(_inventory,s._continuousWorkInventory)&&s._continuousWorkFrameSource!=null&&s._continuousWorkFrameSource.MatchesUnderGate(s)&&
                    ReferenceEquals(_resources,s._continuousWorkResources)&&_resources.IsCurrent&&
                    s._continuousWorkHistory!=null&&s._continuousWorkHistory.ResourcesCurrent&&
                    ReferenceEquals(s._continuousWorkOwner,s._componentDomain)&&s._continuousWorkOwner.ContinuousRevisionUnderGate==s._continuousWorkOwnerRevision&&
                    ReferenceEquals(_point,s._continuousBoundWorkPoint)&&s._continuousBoundWorkPoint.IsValid&&
                    ReferenceEquals(s._continuousBoundWorkPoint.Parent,s._continuousBoundWorkPointRoot)&&
                    s._continuousBoundWorkPoint.LocalPosition==s._continuousBoundWorkPointLocal&&
                    s._continuousBoundWorkPoint.ServiceId==s._continuousWorkHistory.OriginalSeed.Service&&s._continuousBoundWorkPoint.OwnerId==s.HomeShip?.ShipId;
            }
        }
    }
}
