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
    public class ContinuousSimulationVitalsSnapshotTests:InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp]public void Engine(){_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown]public void ResetEngine(){CoreServices.Engine=_engine;}
        static RunSession Boot(){var d=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(d);rig.Session=RunSession.Create(d);Assert.IsTrue(rig.Session.PlayableStarted,rig.Session.LastFailureReason);return rig.Session;}
        static void Native(ActualVitalsProjectionBridge b,ContinuousSimulationVitalsSnapshot s,ContinuousVitalsProposal proposal)
        {Assert.IsTrue(b.PrepareSimulationOnly(s,proposal,out var p,out string reason),reason);using(p)lock(CommonParticipantGate.SyncRoot){Assert.IsTrue(p.MatchesUnderGate());Assert.IsTrue(p.TryInstallNativeOnlyUnderGate(out reason),reason);}}
        [Test]public void ActualProjectionCapacityFailureStillAllowsFreshNativeSurvivalAndPermanentCaptureRefusal()
        {
            var session=Boot();var actual=session.VitalsState;using var b=new ActualVitalsProjectionBridge(session,new ProjectionLimits(retainedUnits:30));
            var sim=b.ReadSimulation();var proposal=ContinuousVitalsEvaluator.Tick(sim.Values,.25,new DiagnosticVitalsTickInput(moving:false,fire:2));
            var projected=b.Read();Assert.IsFalse(b.PrepareNext(projected,proposal.After,out _,out string reason));Assert.AreEqual("projection_preparation_retention_capacity",reason);
            Native(b,sim,proposal);Assert.AreSame(actual,session.VitalsState);Assert.Less(actual.Health,sim.Values.Health);
            lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(b.CaptureAvailableUnderGate());Assert.IsFalse(b.TryPin(out _,out _));
            var second=b.ReadSimulation();var next=ContinuousVitalsEvaluator.Tick(second.Values,.25,new DiagnosticVitalsTickInput(moving:false,fire:2));Native(b,second,next);Assert.Less(actual.Health,second.Values.Health);
        }
        [Test]public void NativeRevocationOccursBeforeWriteAndHistoricalPinsCannotBeObservedAfterward()
        {
            var session=Boot();using var b=new ActualVitalsProjectionBridge(session);Assert.IsTrue(b.TryPin(out var pin,out _));
            using(pin){var snapshot=b.ReadSimulation();Native(b,snapshot,ContinuousVitalsEvaluator.Delta(snapshot.Values,-2,0,0,0));Assert.IsFalse(pin.TryCreateCursor(out _,out _));lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(b.CaptureAvailableUnderGate());}
        }
        [Test]public void ProjectedSuccessRotatesGenerationAndCannotFallbackSameOldProposal()
        {
            var session=Boot();using var b=new ActualVitalsProjectionBridge(session);var sim=b.ReadSimulation();var proposal=ContinuousVitalsEvaluator.Delta(sim.Values,-1,0,0,0);var projected=b.Read();
            Assert.AreNotSame(sim.Values,projected.Values);Assert.IsTrue(b.PrepareNext(projected,proposal.After,out var p,out _));
            using(p)lock(CommonParticipantGate.SyncRoot){Assert.IsTrue(sim.MatchesUnderGate(b));Assert.IsTrue(p.TryInstallActualRawAndProjectionUnderGate(out _));Assert.IsFalse(sim.MatchesUnderGate(b));}
            Assert.IsFalse(b.PrepareSimulationOnly(sim,proposal,out _,out _));Assert.AreEqual(proposal.After.Health,session.VitalsState.Health);
        }
        [Test]public void NativeABAWithEqualBitsCannotReuseOldGeneration()
        {
            var session=Boot();using var b=new ActualVitalsProjectionBridge(session);var old=b.ReadSimulation();Native(b,old,ContinuousVitalsEvaluator.Delta(old.Values,-5,0,0,0));
            var changed=b.ReadSimulation();Native(b,changed,ContinuousVitalsEvaluator.Delta(changed.Values,5,0,0,0));Assert.AreEqual(old.Values.Health,session.VitalsState.Health);
            Assert.IsFalse(b.PrepareSimulationOnly(old,ContinuousVitalsEvaluator.Delta(old.Values,-5,0,0,0),out _,out _));
        }
        [Test]public void ForeignSnapshotOrDifferentBeforeObjectCannotCreateNativeInstaller()
        {
            var session=Boot();var foreign=Boot();using var b=new ActualVitalsProjectionBridge(session);using var other=new ActualVitalsProjectionBridge(foreign);
            var wrong=other.ReadSimulation();Assert.IsFalse(b.PrepareSimulationOnly(wrong,ContinuousVitalsEvaluator.Delta(wrong.Values,-1,0,0,0),out _,out _));
            var current=b.ReadSimulation();Assert.IsFalse(b.PrepareSimulationOnly(current,ContinuousVitalsEvaluator.Delta(b.ReadSimulation().Values,-1,0,0,0),out _,out _));
        }
        [Test]public void RawDriftAfterPreparingNativeProposalRefusesRatherThanApplyingStaleDelta()
        {
            var session=Boot();using var b=new ActualVitalsProjectionBridge(session);var s=b.ReadSimulation();Assert.IsTrue(b.PrepareSimulationOnly(s,ContinuousVitalsEvaluator.Delta(s.Values,-2,0,0,0),out var p,out _));
            session.VitalsState.Health-=1;using(p)lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(p.TryInstallNativeOnlyUnderGate(out _));Assert.AreEqual(s.Values.Health-1,session.VitalsState.Health);
        }
        [Test]public void CaptureStampSaturationDoesNotStopNativeDamageOrRotateBackToOldSnapshot()
        {
            var session=Boot();using var b=new ActualVitalsProjectionBridge(session);typeof(ActualVitalsProjectionBridge).GetField("_stamp",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(b,ulong.MaxValue);
            var s=b.ReadSimulation();Native(b,s,ContinuousVitalsEvaluator.CombatHealth(s.Values,s.Values.Health-3));
            var next=b.ReadSimulation();Native(b,next,ContinuousVitalsEvaluator.CombatHealth(next.Values,next.Values.Health-3));Assert.AreEqual(s.Values.Health-6,session.VitalsState.Health);
            lock(CommonParticipantGate.SyncRoot){Assert.IsFalse(b.CaptureAvailableUnderGate());Assert.IsFalse(s.MatchesUnderGate(b));}Assert.AreEqual(ulong.MaxValue,typeof(ActualVitalsProjectionBridge).GetField("_stamp",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(b));
        }
        [Test]public void LatestNativeTokenPreemptsOlderAndCancelDoesNotRewindActual()
        {
            var session=Boot();using var b=new ActualVitalsProjectionBridge(session);var s=b.ReadSimulation();Assert.IsTrue(b.PrepareSimulationOnly(s,ContinuousVitalsEvaluator.Delta(s.Values,-1,0,0,0),out var old,out _));
            Assert.IsTrue(b.PrepareSimulationOnly(s,ContinuousVitalsEvaluator.Delta(s.Values,-2,0,0,0),out var current,out _));old.Dispose();
            using(current)lock(CommonParticipantGate.SyncRoot){Assert.IsFalse(old.TryInstallNativeOnlyUnderGate(out _));Assert.IsTrue(current.TryInstallNativeOnlyUnderGate(out _));Assert.IsFalse(current.TryInstallNativeOnlyUnderGate(out _));}
            Assert.AreEqual(s.Values.Health-2,session.VitalsState.Health);
        }
        [Test]public void ReplacedActualModelCannotUseOldNativeContinuation()
        {
            var session=Boot();using var b=new ActualVitalsProjectionBridge(session);var s=b.ReadSimulation();session.VitalsState=new VitalsState();
            Assert.IsFalse(b.PrepareSimulationOnly(s,ContinuousVitalsEvaluator.Delta(s.Values,-1,0,0,0),out _,out _));Assert.Throws<InvalidOperationException>(()=>b.ReadSimulation());
        }
    }
}
