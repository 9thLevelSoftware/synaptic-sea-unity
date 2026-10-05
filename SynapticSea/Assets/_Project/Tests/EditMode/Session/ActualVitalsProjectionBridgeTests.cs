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
    public class ActualVitalsProjectionBridgeTests:InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp]public void Engine(){_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown]public void RestoreEngine(){CoreServices.Engine=_engine;}
        static RunSession Boot(){var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);rig.Session=RunSession.Create(deps);Assert.IsTrue(rig.Session.PlayableStarted,rig.Session.LastFailureReason);return rig.Session;}
        static void Install(ActualVitalsProjectionBridge.PreparedChange p)
        {using(p){lock(CommonParticipantGate.SyncRoot){Assert.IsTrue(p.MatchesUnderGate());Assert.IsTrue(p.TryInstallActualRawAndProjectionUnderGate(out string reason),reason);}}}
        static ProjectionCaptureResult Drain(ProjectionPin pin)
        {Assert.IsTrue(pin.TryCreateCursor(out var c,out _));using(c){while(c.Status==ProjectionCursorStatus.Pending)c.Advance(1);Assert.AreEqual(ProjectionCursorStatus.Complete,c.Status);return c.Result;}}
        [Test]public void SameActualRunSessionModelAndHistoricalCutSurviveRegisteredWrites()
        {
            var session=Boot();var actual=session.VitalsState;using var bridge=new ActualVitalsProjectionBridge(session);
            var before=bridge.Read();Assert.IsTrue(bridge.TryPin(out var pin,out _));
            var scratch=before.Values.ExactScratch();scratch.Tick(.25,new DiagnosticVitalsTickInput(moving:false,fire:2).CopyForModel());
            var next=DiagnosticVitalsValues.FromModel(scratch);Assert.IsTrue(bridge.PrepareNext(before,next,out var p,out _));Install(p);
            Assert.AreSame(actual,session.VitalsState);Assert.AreEqual(next.Health,actual.Health);Assert.Greater(bridge.Read().Stamp,before.Stamp);
            using(pin)using(var history=Drain(pin))
            {Assert.AreEqual(before.Values.Health,(double)history.Entry(0,0).Value.Scalar.ToNormalized());Assert.IsFalse(history.TryBuildWholeWorldSave(out _));}
            Assert.IsTrue(bridge.TryPin(out pin,out _));using(pin)using(var current=Drain(pin))Assert.AreEqual(next.Health,(double)current.Entry(0,0).Value.Scalar.ToNormalized());
        }
        [Test]public void CrossBridgeSnapshotAndRegisteredABACannotPassFinalGuard()
        {
            var a=Boot();var b=Boot();using var first=new ActualVitalsProjectionBridge(a);using var second=new ActualVitalsProjectionBridge(b);
            var foreign=first.Read();Assert.IsFalse(second.PrepareNext(foreign,foreign.Values,out _,out _));
            var old=first.Read();var loss=old.Values.ExactScratch();loss.Health-=5;Assert.IsTrue(first.PrepareNext(old,DiagnosticVitalsValues.FromModel(loss),out var p,out _));Install(p);
            var changed=first.Read();Assert.IsTrue(first.PrepareNext(changed,old.Values,out p,out _));Install(p);
            Assert.AreEqual(old.Values.Health,a.VitalsState.Health);Assert.IsFalse(first.PrepareNext(old,old.Values,out _,out _));
        }
        [Test]public void ActualRawFieldDriftRefusesAndRevokesCaptureWithoutResynchronizing()
        {
            var session=Boot();using var bridge=new ActualVitalsProjectionBridge(session);var snapshot=bridge.Read();Assert.IsTrue(bridge.TryPin(out var pin,out _));
            session.VitalsState.MaxStamina+=1;
            Assert.IsFalse(bridge.TryPin(out _,out string reason));Assert.AreEqual("actual_vitals_capture_unavailable",reason);Assert.IsFalse(pin.TryCreateCursor(out _,out _));pin.Dispose();
            Assert.IsFalse(bridge.PrepareNext(snapshot,snapshot.Values,out _,out _));Assert.AreEqual(snapshot.Values.MaxStamina+1,session.VitalsState.MaxStamina);
        }
        [Test]public void PendingCancelAndRepeatedInstallCannotChangeActualTwice()
        {
            var session=Boot();using var bridge=new ActualVitalsProjectionBridge(session);var before=bridge.Read();var next=before.Values.ExactScratch();next.Stamina-=1;
            Assert.IsTrue(bridge.PrepareNext(before,DiagnosticVitalsValues.FromModel(next),out var p,out _));p.Dispose();Assert.AreEqual(before.Values.Stamina,session.VitalsState.Stamina);
            Assert.IsTrue(bridge.PrepareNext(before,DiagnosticVitalsValues.FromModel(next),out p,out _));Install(p);
            lock(CommonParticipantGate.SyncRoot)Assert.IsFalse(p.TryInstallActualRawAndProjectionUnderGate(out _));Assert.AreEqual(next.Stamina,session.VitalsState.Stamina);
        }
        [Test]public void CurrentSurvivalProposalSupersedesSpeculativeDebitWithoutWaitingOrDoubleDebit()
        {
            var session=Boot();using var bridge=new ActualVitalsProjectionBridge(session);var before=bridge.Read();
            var work=before.Values.ExactScratch();work.Stamina-=8;
            Assert.IsTrue(bridge.PrepareNext(before,DiagnosticVitalsValues.FromModel(work),out var stale,out _));
            var survival=before.Values.ExactScratch();survival.Tick(.25,new DiagnosticVitalsTickInput(moving:false,fire:2).CopyForModel());
            Assert.IsTrue(bridge.PrepareNext(before,DiagnosticVitalsValues.FromModel(survival),out var current,out string reason),reason);
            stale.Dispose();
            lock(CommonParticipantGate.SyncRoot){Assert.IsFalse(stale.MatchesUnderGate());Assert.IsFalse(stale.TryInstallActualRawAndProjectionUnderGate(out _));Assert.IsTrue(current.MatchesUnderGate());}
            Install(current);Assert.AreEqual(survival.Health,session.VitalsState.Health);Assert.AreEqual(survival.Stamina,session.VitalsState.Stamina);
        }
        [Test]public void ForeignSnapshotCannotSupersedeCurrentExactPendingPlan()
        {
            var session=Boot();var other=Boot();using var bridge=new ActualVitalsProjectionBridge(session);using var foreign=new ActualVitalsProjectionBridge(other);
            var before=bridge.Read();var next=before.Values.ExactScratch();next.Stamina-=1;
            Assert.IsTrue(bridge.PrepareNext(before,DiagnosticVitalsValues.FromModel(next),out var current,out _));
            var wrong=foreign.Read();Assert.IsFalse(bridge.PrepareNext(wrong,wrong.Values,out _,out _));
            lock(CommonParticipantGate.SyncRoot)Assert.IsTrue(current.MatchesUnderGate());Install(current);
        }
        [Test]public void Raw14ConfigChangesInvalidatePreparedProposalBeforeAnyInstall()
        {
            var session=Boot();using var bridge=new ActualVitalsProjectionBridge(session);var before=bridge.Read();var next=before.Values.ExactScratch();next.Stamina-=8;
            Assert.IsTrue(bridge.PrepareNext(before,DiagnosticVitalsValues.FromModel(next),out var p,out _));
            session.VitalsState.HealthDrainRate+=.125;
            using(p)lock(CommonParticipantGate.SyncRoot){Assert.IsFalse(p.MatchesUnderGate());Assert.IsFalse(p.TryInstallActualRawAndProjectionUnderGate(out _));}
            Assert.AreEqual(before.Values.Stamina,session.VitalsState.Stamina);Assert.IsFalse(bridge.TryBuildWholeWorldSave(out _));
        }
    }
}
