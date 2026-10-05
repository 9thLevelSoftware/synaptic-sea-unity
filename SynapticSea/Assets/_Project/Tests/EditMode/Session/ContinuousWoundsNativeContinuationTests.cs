using System;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class ContinuousWoundsNativeContinuationTests:InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp]public void Setup(){_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown]public void Teardown(){CoreServices.Engine=_engine;}
        static RunSession Boot(){var d=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(d);rig.Session=RunSession.Create(d);Assert.IsTrue(rig.Session.PlayableStarted,rig.Session.LastFailureReason);rig.Session.WoundState.ApplyWound(new GdDict{{"kind","laceration"},{"body_part","arm"},{"severity",.7}});return rig.Session;}
        static void Native(ActualWoundsProjectionBridge b,double delta){Assert.IsTrue(b.PrepareNativeContinuation(b.ReadNativeContinuation(),ContinuousWoundsNativeOperation.PrepareTickHeal(delta,.03,.01),out var ticket,out var reason),reason);using(ticket)lock(CommonParticipantGate.SyncRoot)Assert.IsTrue(ticket.TryApplyActualUnderGate(out _,out _,out reason),reason);}
        [Test]public void BeyondProjectionCapacityActualNativeAgingContinuesWithoutChangingWrappers()
        {
            var s=Boot();var actual=s.WoundState;using var b=new ActualWoundsProjectionBridge(s);Native(b,0);var first=actual.Wounds[0];
            // Isolated forced-capacity setup after capture already revoked, not an earned damage route.
            for(int i=1;i<=ContinuousWoundsValues.MaximumRows;i++){var row=((GdDict)first).DeepCopy();row["wound_id"]="overflow_"+i;actual.Wounds.Add(row);}
            Assert.Throws<ArgumentException>(()=>ContinuousWoundsValues.CaptureExact(actual));Native(b,.25);Native(b,.25);
            Assert.AreSame(actual,s.WoundState);Assert.AreSame(first,actual.Wounds[0]);Assert.AreEqual(.5,((GdDict)first).Get("age_seconds"));Assert.IsFalse(b.TryPin(out _,out _));
        }
        [Test]public void NativeRevocationRefusesHistoricalObservationBeforeAnyMutation(){var s=Boot();using var b=new ActualWoundsProjectionBridge(s);Assert.IsTrue(GuardedWoundsCaptureCursor.TryBegin(b,out var c,out _));using(c){Native(b,.25);Assert.AreEqual(ContinuousTraversalStatus.Refused,c.Advance(1));Assert.AreEqual(.25,((GdDict)s.WoundState.Wounds[0]).Get("age_seconds"));}}
        [Test]public void ProjectedSuccessInvalidatesEarlierNativeSnapshot(){var s=Boot();using var b=new ActualWoundsProjectionBridge(s);var native=b.ReadNativeContinuation();var before=b.Read();var scratch=before.Values.ExactScratch();scratch.Tick(.25);Assert.IsTrue(b.PrepareNext(before,ContinuousWoundsValues.CaptureExact(scratch),out var p,out _));using(p)lock(CommonParticipantGate.SyncRoot)Assert.IsTrue(p.TryInstallActualRawAndProjectionUnderGate(out _));Assert.IsFalse(b.PrepareNativeContinuation(native,ContinuousWoundsNativeOperation.PrepareTickHeal(1,0,0),out _,out _));}
        [Test]public void ForeignStaleAndRepeatedTicketsRefuse(){var s=Boot();var other=Boot();using var b=new ActualWoundsProjectionBridge(s);using var foreign=new ActualWoundsProjectionBridge(other);var old=b.ReadNativeContinuation();Assert.IsFalse(b.PrepareNativeContinuation(foreign.ReadNativeContinuation(),ContinuousWoundsNativeOperation.PrepareTickHeal(1,0,0),out _,out _));Native(b,0);Assert.IsFalse(b.PrepareNativeContinuation(old,ContinuousWoundsNativeOperation.PrepareTickHeal(1,0,0),out _,out _));Assert.IsTrue(b.PrepareNativeContinuation(b.ReadNativeContinuation(),ContinuousWoundsNativeOperation.PrepareTickHeal(.25,0,0),out var t,out _));using(t)lock(CommonParticipantGate.SyncRoot){Assert.IsTrue(t.TryApplyActualUnderGate(out _,out _,out _));Assert.IsFalse(t.TryApplyActualUnderGate(out _,out _,out _));}}
        [Test]public void NativeSuccessorSurvivesOldDisposeAndLifecycleReplacementRefuses(){var s=Boot();using var b=new ActualWoundsProjectionBridge(s);var before=b.ReadNativeContinuation();Assert.IsTrue(b.PrepareNativeContinuation(before,ContinuousWoundsNativeOperation.PrepareTickHeal(1,0,0),out var old,out _));Assert.IsTrue(b.PrepareNativeContinuation(before,ContinuousWoundsNativeOperation.PrepareTickHeal(.25,0,0),out var latest,out _));old.Dispose();using(latest)lock(CommonParticipantGate.SyncRoot)Assert.IsTrue(latest.TryApplyActualUnderGate(out _,out _,out _));before=b.ReadNativeContinuation();s.WoundState=new WoundState();Assert.IsFalse(b.PrepareNativeContinuation(before,ContinuousWoundsNativeOperation.PrepareTickHeal(1,0,0),out _,out _));}
    }
}
