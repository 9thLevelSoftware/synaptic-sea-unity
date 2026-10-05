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
    public class ActualOxygenProjectionBridgeTests:InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp]public void Engine(){_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown]public void ResetEngine(){CoreServices.Engine=_engine;}
        static RunSession Boot()
        {var d=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(d);rig.Session=RunSession.Create(d);Assert.IsTrue(rig.Session.PlayableStarted,rig.Session.LastFailureReason);rig.Session.OxygenState.Configure(new GdDict{{"zone_ids",GdArray.Of("alpha")}});return rig.Session;}
        static void Install(ActualOxygenProjectionBridge.PreparedChange p)
        {using(p)lock(CommonParticipantGate.SyncRoot)Assert.IsTrue(p.TryInstallActualRawAndProjectionUnderGate(out string reason),reason);}
        static void Tick(ActualOxygenProjectionBridge bridge,double delta,bool field=false)
        {var old=bridge.Read();var scratch=old.Values.ExactScratch();scratch.Tick(delta,new GdDict{{"player_in_breach_zone",!field},{"field_atmosphere",field}});Assert.IsTrue(bridge.PrepareNext(old,ContinuousOxygenValues.CaptureExact(scratch),out var p,out string reason),reason);Install(p);}
        static void Drain(GuardedOxygenCaptureCursor c)
        {int calls=0;while(c.Advance(1)==ContinuousTraversalStatus.Pending){Assert.LessOrEqual(c.LastWorkUnits,1);Assert.Less(++calls,1000);}Assert.AreEqual(ContinuousTraversalStatus.CompletePartialDiagnostic,c.Status,c.Reason);}
        [Test]public void NativeCacheMultipliersSurviveSealedSummaryMaskAndFieldTickUsesExactActualModel()
        {
            var s=Boot();var actual=s.OxygenState;actual.ApplyInventorySummary(new GdDict{{"drain_multiplier",.5}});actual.ApplyEquipmentSummary(new GdDict{{"drain_multiplier",.8}});actual.SealBreach("alpha");
            Assert.AreEqual(1,actual.GetSummary().GetFloat("drain_multiplier"));using var b=new ActualOxygenProjectionBridge(s);
            Assert.AreEqual(.5,b.Read().Values.InventoryMultiplier);Assert.AreEqual(.8,b.Read().Values.EquipmentMultiplier);
            Tick(b,1,true);Assert.AreSame(actual,s.OxygenState);Assert.AreEqual(97.6,actual.Oxygen,1e-12);Assert.AreEqual(2.4,actual.EffectiveDrainRate,1e-12);Assert.IsTrue(actual.BreachSealed);
        }
        [Test]public void HistoricalGuardedProjectionSurvivesRegisteredSuccessorAndKeepsZoneCustody()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);Assert.IsTrue(GuardedOxygenCaptureCursor.TryBegin(b,out var c,out _));
            using(c){c.Advance(1);Tick(b,1);Drain(c);Assert.IsTrue(c.TryReadScalar("oxygen",out object oxygen,out _));Assert.AreEqual(100,(double)oxygen);Assert.AreEqual(94,s.OxygenState.Oxygen);Assert.IsTrue(c.TryReadZone(0,out string zone,out _));Assert.AreEqual("alpha",zone);Assert.IsFalse(c.TryBuildWholeWorldSave(out _));}
        }
        [Test]public void EqualZoneArrayReplacementRevokesSnapshotAndGuardedCapture()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);var old=b.Read();Assert.IsTrue(GuardedOxygenCaptureCursor.TryBegin(b,out var c,out _));
            using(c){s.OxygenState.BreachZoneIds=GdArray.Of("alpha");Assert.AreEqual(ContinuousTraversalStatus.Refused,c.Advance(1));Assert.IsFalse(b.PrepareNext(old,old.Values,out _,out _));}
        }
        [Test]public void HiddenCacheDriftAfterCaptureCompletionRefusesObservationEvenWhenPublicDrainIsOne()
        {
            var s=Boot();s.OxygenState.SealBreach("alpha");using var b=new ActualOxygenProjectionBridge(s);Assert.IsTrue(GuardedOxygenCaptureCursor.TryBegin(b,out var c,out _));
            using(c){Drain(c);s.OxygenState.ApplyInventorySummary(new GdDict{{"drain_multiplier",.5}});Assert.AreEqual(1,s.OxygenState.GetSummary().GetFloat("drain_multiplier"));Assert.IsFalse(c.TryReadScalar("oxygen",out _,out string reason));Assert.AreEqual("actual_oxygen_source_revoked",reason);}
        }
        [Test]public void TickOnlyConfigurationChangeRefusesWithoutCancellingValidPendingPlan()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);var old=b.Read();var scratch=old.Values.ExactScratch();scratch.Tick(.25,true);
            Assert.IsTrue(b.PrepareNext(old,ContinuousOxygenValues.CaptureExact(scratch),out var good,out _));scratch.DrainRate+=1;
            Assert.IsFalse(b.PrepareNext(old,ContinuousOxygenValues.CaptureExact(scratch),out _,out _));lock(CommonParticipantGate.SyncRoot)Assert.IsTrue(good.MatchesUnderGate());Install(good);
        }
        [Test]public void LatestCurrentProposalPreemptsOldHandleAndStaleDisposeCannotClearSuccessor()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);var old=b.Read();var scratch=old.Values.ExactScratch();scratch.Tick(1,true);
            Assert.IsTrue(b.PrepareNext(old,ContinuousOxygenValues.CaptureExact(scratch),out var stale,out _));scratch=old.Values.ExactScratch();scratch.Tick(.25,true);
            Assert.IsTrue(b.PrepareNext(old,ContinuousOxygenValues.CaptureExact(scratch),out var latest,out _));stale.Dispose();lock(CommonParticipantGate.SyncRoot){Assert.IsFalse(stale.TryInstallActualRawAndProjectionUnderGate(out _));Assert.IsTrue(latest.MatchesUnderGate());}Install(latest);Assert.AreEqual(98.5,s.OxygenState.Oxygen);
        }
        [Test]public void CrossOwnerSnapshotRefusesEvenWithEqualOxygen()
        {var a=Boot();var d=Boot();d.OxygenState.MaxOxygen=200;using var first=new ActualOxygenProjectionBridge(a);using var other=new ActualOxygenProjectionBridge(d);var wrong=other.Read();Assert.AreEqual(first.Read().Values.Oxygen,wrong.Values.Oxygen);Assert.IsFalse(first.PrepareNext(wrong,wrong.Values,out _,out _));}
        [Test]public void RegisteredABAAndRepeatedInstallRefuse()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);var old=b.Read();Tick(b,1);var current=b.Read();Assert.IsTrue(b.PrepareNext(current,old.Values,out var p,out _));Install(p);
            Assert.AreEqual(100,s.OxygenState.Oxygen);Assert.IsFalse(b.PrepareNext(old,old.Values,out _,out _));lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(p.TryInstallActualRawAndProjectionUnderGate(out _));
        }
        [TestCase(-1)][TestCase(65)]public void InvalidCaptureBudgetDoesNoWork(int budget)
        {var s=Boot();using var b=new ActualOxygenProjectionBridge(s);Assert.IsTrue(GuardedOxygenCaptureCursor.TryBegin(b,out var c,out _));using(c){Assert.AreEqual(ContinuousTraversalStatus.Refused,c.Advance(budget));Assert.AreEqual(0,c.LastWorkUnits);}}
        [Test]public void NonfiniteNativeCacheIsRefusedBeforeEnrollment()
        {var s=Boot();s.OxygenState.ApplyInventorySummary(new GdDict{{"drain_multiplier",double.NaN}});Assert.Throws<ArgumentException>(()=>new ActualOxygenProjectionBridge(s));}
        [Test]public void OversizedZoneListAndIdentifierRefuseBeforeEnrollment()
        {
            var s=Boot();s.OxygenState.BreachZoneIds.Clear();for(int i=0;i<257;i++)s.OxygenState.BreachZoneIds.Add("zone_"+i);
            Assert.Throws<ArgumentException>(()=>new ActualOxygenProjectionBridge(s));
            s.OxygenState.BreachZoneIds=GdArray.Of(new string('a',257));Assert.Throws<ArgumentException>(()=>new ActualOxygenProjectionBridge(s));
        }
        [Test]public void ZeroBudgetAndCancellationNeverPauseOrRestoreActualOxygen()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);Assert.IsTrue(GuardedOxygenCaptureCursor.TryBegin(b,out var c,out _));
            using(c){Assert.AreEqual(ContinuousTraversalStatus.Pending,c.Advance(0));Assert.AreEqual(0,c.LastWorkUnits);c.Cancel();Tick(b,1);Assert.AreEqual(ContinuousTraversalStatus.Cancelled,c.Advance(64));Assert.AreEqual(94,s.OxygenState.Oxygen);Assert.IsFalse(c.TryReadScalar("oxygen",out _,out _));}
        }
        [Test]public void SourceRateDriftRefusesBeforeActualInstallAndCancelNeverRestoresLiveState()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);var old=b.Read();var scratch=old.Values.ExactScratch();scratch.Tick(1,true);Assert.IsTrue(b.PrepareNext(old,ContinuousOxygenValues.CaptureExact(scratch),out var p,out _));
            s.OxygenState.RecoveryThreshold+=1;using(p)lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(p.TryInstallActualRawAndProjectionUnderGate(out _));Assert.AreEqual(100,s.OxygenState.Oxygen);
        }
    }
}
