using System;
using System.Threading;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        readonly object _continuousOrdinaryStartIssuer=new object();
        // Only the actual selected runtime inside its synchronous Tick may retain a stock source.
        // This is ordinary semantic equality, not a protected dictionary/ABA certificate.
        bool TryCaptureContinuousOrdinaryStartSource(out ContinuousOrdinaryStartSource source,out string reason)
        {
            source=null;reason="continuous_ordinary_start_source_unavailable";
            if(Monitor.IsEntered(CommonParticipantGate.SyncRoot))throw new InvalidOperationException("ordinary_start_capture_requires_outside_gate");
            if(!ContinuousAuxiliaryRuntimeActive||!_inTick||_componentDomain==null||_continuousWorkResources==null||!_continuousWorkResources.IsCurrent)return false;
            var owner=_componentDomain;var inventory=InventoryState;var progression=PlayerProgression;var training=TrainingEventBus;
            var scene=Scene;var home=HomeShip;string run=RunId;int thread=Thread.CurrentThread.ManagedThreadId;
            var before=CapturePaidCraftingDomain(); // stock refresh and ordinary callbacks happen outside final fence
            if(!ContinuousAuxiliaryRuntimeActive||!_inTick||!ReferenceEquals(owner,_componentDomain)||!ReferenceEquals(inventory,InventoryState)||
                !ReferenceEquals(progression,PlayerProgression)||!ReferenceEquals(training,TrainingEventBus)||!ReferenceEquals(scene,Scene)||!ReferenceEquals(home,HomeShip)||
                run!=RunId||!_continuousWorkResources.IsCurrent||before.GetInt("revision",-1)!=owner.CurrentRevision)
            {reason="continuous_ordinary_start_source_changed";return false;}
            source=new ContinuousOrdinaryStartSource(this,_continuousOrdinaryStartIssuer,owner,before,inventory,progression,training,_continuousWorkResources,scene,home,run,thread);
            reason="";return true;
        }
        internal sealed class ContinuousOrdinaryStartSource
        {
            readonly RunSession _session;readonly DomainTransactionCoordinator _owner;readonly object _ownerSourceIdentity;readonly GdDict _before;
            readonly InventoryState _inventory;readonly PlayerProgressionState _progression;readonly TrainingEventBus _training;
            readonly ResourceAuthorityLease _resources;readonly object _scene,_home;readonly string _run;readonly int _thread;readonly long _revision;
            bool _freshConfirmed;ulong _confirmedBoundary;long _confirmedWorldBits,_confirmedPlayBits;
            internal ContinuousOrdinaryStartSource(RunSession session,object issuer,DomainTransactionCoordinator owner,GdDict before,
                InventoryState inventory,PlayerProgressionState progression,TrainingEventBus training,ResourceAuthorityLease resources,
                object scene,object home,string run,int thread)
            {
                if(session==null||!ReferenceEquals(issuer,session._continuousOrdinaryStartIssuer))throw new InvalidOperationException("unissued_ordinary_start_source");
                _session=session;_owner=owner;_ownerSourceIdentity=owner.CurrentSourceIdentity;_before=before.DeepCopy();_revision=_before.GetInt("revision");_inventory=inventory;_progression=progression;_training=training;
                _resources=resources;_scene=scene;_home=home;_run=run;_thread=thread;
            }
            internal GdDict CaptureBeforeOutsideGate()=>_before.DeepCopy();
            bool SourceStillCurrent(RunSession session)
                =>ReferenceEquals(session,_session)&&Thread.CurrentThread.ManagedThreadId==_thread&&session.ContinuousAuxiliaryRuntimeActive&&session._inTick&&
                  ReferenceEquals(session._componentDomain,_owner)&&ReferenceEquals(_owner.CurrentSourceIdentity,_ownerSourceIdentity)&&_owner.CurrentRevision==_revision&&ReferenceEquals(session.InventoryState,_inventory)&&
                  ReferenceEquals(session.PlayerProgression,_progression)&&ReferenceEquals(session.TrainingEventBus,_training)&&
                  ReferenceEquals(session.Scene,_scene)&&ReferenceEquals(session.HomeShip,_home)&&session.RunId==_run&&
                  ReferenceEquals(session._continuousWorkResources,_resources)&&_resources.IsCurrent;
            // Must immediately precede the uninterrupted final section, on this same Tick thread.
            internal bool TryConfirmFreshOutsideGate(RunSession session,out string reason)
            {
                _freshConfirmed=false;reason="continuous_ordinary_start_source_changed";
                if(Monitor.IsEntered(CommonParticipantGate.SyncRoot))throw new InvalidOperationException("ordinary_start_confirmation_requires_outside_gate");
                if(!SourceStillCurrent(session))return false;
                var context=session.OwnerHashContext(_before);
                var actual=session.ReadComponentParticipants(PaidState(_before.DeepCopy()),context);
                if(!PaidEqual(context,_before.Get("participating_state"),actual)||!SourceStillCurrent(session))return false;
                _confirmedBoundary=session._continuousBoundaryEpoch;
                _confirmedWorldBits=BitConverter.DoubleToInt64Bits(session.WorldTime);
                _confirmedPlayBits=BitConverter.DoubleToInt64Bits(session.RunPlayTimeSeconds);
                _freshConfirmed=true;reason="";return true;
            }
            internal bool MatchesUnderGate(RunSession session)
            { CommonParticipantGate.RequireHeld();return _freshConfirmed&&SourceStillCurrent(session)&&session._continuousBoundaryEpoch==_confirmedBoundary&&
                BitConverter.DoubleToInt64Bits(session.WorldTime)==_confirmedWorldBits&&BitConverter.DoubleToInt64Bits(session.RunPlayTimeSeconds)==_confirmedPlayBits; }
        }
    }
}
