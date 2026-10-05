using System;
using System.Reflection;
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
    public class ContinuousSimulationOxygenSnapshotTests:InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp]public void Setup(){_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown]public void Teardown(){CoreServices.Engine=_engine;}
        static RunSession Boot(){var d=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(d);rig.Session=RunSession.Create(d);Assert.IsTrue(rig.Session.PlayableStarted,rig.Session.LastFailureReason);rig.Session.OxygenState.Configure(new GdDict{{"zone_ids",GdArray.Of("alpha")}});return rig.Session;}
        static ContinuousOxygenProposal Proposal(ContinuousSimulationOxygenSnapshot snap,double delta=1)=>ContinuousOxygenEvaluator.EvaluateTick(snap.Values,delta,new ContinuousOxygenTickInput(true,false,1,0));
        static void Native(ActualOxygenProjectionBridge b,ContinuousSimulationOxygenSnapshot snap,ContinuousOxygenProposal proposal){Assert.IsTrue(b.PrepareSimulationOnly(snap,proposal,out var p,out var reason),reason);using(p)lock(CommonParticipantGate.SyncRoot)Assert.IsTrue(p.TryInstallNativeOnlyUnderGate(out reason),reason);}
        [Test]public void ActualRetentionFailureRevokesCaptureAndContinuesSameNativeOxygen()
        {
            var s=Boot();var actual=s.OxygenState;using var b=new ActualOxygenProjectionBridge(s,new ProjectionLimits(retainedUnits:30));
            var snap=b.ReadSimulation();var proposal=Proposal(snap);Assert.IsFalse(b.PrepareNext(b.Read(),proposal.After,out _,out _));
            Native(b,snap,proposal);Assert.AreSame(actual,s.OxygenState);Assert.AreEqual(94,actual.Oxygen);
            lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(b.CaptureAvailableUnderGate());
            snap=b.ReadSimulation();Native(b,snap,Proposal(snap));Assert.AreEqual(88,actual.Oxygen);
        }
        [Test]public void RevocationPrecedesNativeWriteAndRefusesHistoricalObservation()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);Assert.IsTrue(GuardedOxygenCaptureCursor.TryBegin(b,out var cut,out _));
            using(cut){var snap=b.ReadSimulation();Native(b,snap,Proposal(snap));Assert.AreEqual(ContinuousTraversalStatus.Refused,cut.Advance(1));}
        }
        [Test]public void ProjectedInstallRotatesGenerationAndStaleNativeFallbackRefuses()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);var snap=b.ReadSimulation();var proposal=Proposal(snap);
            Assert.IsTrue(b.PrepareNext(b.Read(),proposal.After,out var p,out _));using(p)lock(CommonParticipantGate.SyncRoot)Assert.IsTrue(p.TryInstallActualRawAndProjectionUnderGate(out _));
            Assert.IsFalse(b.PrepareSimulationOnly(snap,proposal,out _,out _));Assert.AreEqual(94,s.OxygenState.Oxygen);
        }
        [Test]public void ForeignAndDifferentBeforeObjectRefuse()
        {
            var a=Boot();var other=Boot();using var b=new ActualOxygenProjectionBridge(a);using var foreign=new ActualOxygenProjectionBridge(other);
            var snap=b.ReadSimulation();var wrong=foreign.ReadSimulation();Assert.IsFalse(b.PrepareSimulationOnly(wrong,Proposal(wrong),out _,out _));
            Assert.IsFalse(b.PrepareSimulationOnly(snap,Proposal(b.ReadSimulation()),out _,out _));Assert.AreEqual(100,a.OxygenState.Oxygen);
        }
        [Test]public void RawDriftAndZoneReplacementRefuseStaleContinuation()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);var snap=b.ReadSimulation();Assert.IsTrue(b.PrepareSimulationOnly(snap,Proposal(snap),out var p,out _));
            using(p){s.OxygenState.Oxygen=99;lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(p.TryInstallNativeOnlyUnderGate(out _));}
            snap=b.ReadSimulation();s.OxygenState.BreachZoneIds=GdArray.Of("alpha");Assert.IsFalse(b.PrepareSimulationOnly(snap,Proposal(snap),out _,out _));
        }
        [Test]public void ExhaustedReportingStampDoesNotFreezeNativeContinuationOrRegrantCapture()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);typeof(ActualOxygenProjectionBridge).GetField("_stamp",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(b,ulong.MaxValue);
            var snap=b.ReadSimulation();Native(b,snap,Proposal(snap));snap=b.ReadSimulation();Native(b,snap,Proposal(snap));Assert.AreEqual(88,s.OxygenState.Oxygen);
            lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(b.CaptureAvailableUnderGate());Assert.AreEqual(ulong.MaxValue,typeof(ActualOxygenProjectionBridge).GetField("_stamp",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(b));
        }
        [Test]public void SuccessorNativeTokenSurvivesOldDisposeAndRepeatRefuses()
        {
            var s=Boot();using var b=new ActualOxygenProjectionBridge(s);var snap=b.ReadSimulation();Assert.IsTrue(b.PrepareSimulationOnly(snap,Proposal(snap),out var old,out _));
            Assert.IsTrue(b.PrepareSimulationOnly(snap,Proposal(snap,.25),out var latest,out _));old.Dispose();using(latest)lock(CommonParticipantGate.SyncRoot){Assert.IsFalse(old.MatchesUnderGate());Assert.IsTrue(latest.TryInstallNativeOnlyUnderGate(out _));Assert.IsFalse(latest.TryInstallNativeOnlyUnderGate(out _));}Assert.AreEqual(98.5,s.OxygenState.Oxygen);
        }
    }
}
