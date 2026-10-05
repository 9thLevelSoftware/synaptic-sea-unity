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
    public class ContinuousVitalsOverlayCursorTests:InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp]public void Engine(){_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown]public void RestoreEngine(){CoreServices.Engine=_engine;}
        static RunSession Boot(){var d=SessionHarness.GoldenDeps(out var r);SessionHarness.OverlayGamePlayability(d);d.EnablePaidCrafting=true;d.EnableBitExactPaidCompatibility=true;d.EnableAuxiliaryServices=true;d.EnableManualStudy=true;d.EnableComponentIntegration=true;d.EnableContinuousAuxiliaryDiagnostic=true;const string p="res://data/diagnostics/earned-services-home-v1/";d.LayoutPath=p+"layout.json";d.GameplaySlicePath=p+"gameplay_slice.json";d.BlueprintPath=p+"blueprint.json";r.Session=RunSession.Create(d);Assert.IsTrue(r.Session.PlayableStarted,r.Session.LastFailureReason);return r.Session;}
        static ContinuousBootstrapSnapshot Genesis(RunSession s){Assert.IsTrue(ContinuousBootstrapSnapshot.TryReadFreshBeforeContinuousLoop(s,out var g,out string reason),reason);return g;}
        static void Drain(ContinuousVitalsOverlayCursor c)
        {int calls=0;while(c.Advance(1)==ContinuousTraversalStatus.Pending){Assert.LessOrEqual(c.LastWorkUnits,1);Assert.Less(++calls,100000);}Assert.AreEqual(ContinuousTraversalStatus.CompletePartialDiagnostic,c.Status,c.Reason);}
        static void Publish(ActualVitalsProjectionBridge bridge,double health,double stamina)
        {
            var old=bridge.Read();var next=old.Values.ExactScratch();next.Health=health;next.Stamina=stamina;
            Assert.IsTrue(bridge.PrepareNext(old,DiagnosticVitalsValues.FromModel(next),out var p,out string reason),reason);
            using(p)lock(CommonParticipantGate.SyncRoot)Assert.IsTrue(p.TryInstallActualRawAndProjectionUnderGate(out reason),reason);
        }
        [Test]public void HistoricalActualVitalsOverlaySurvivesRegisteredWritesDuringBoundedCopy()
        {
            var s=Boot();var genesis=Genesis(s);using var bridge=new ActualVitalsProjectionBridge(s);
            Publish(bridge,80,BitConverter.Int64BitsToDouble(long.MinValue));
            Assert.IsTrue(genesis.TryBeginVitalsOverlay(bridge,out var c,out string reason),reason);
            using(c)
            {
                ulong revision=c.SourceRevision;Assert.AreEqual(ContinuousTraversalStatus.Pending,c.Advance(0));Assert.AreEqual(0,c.LastWorkUnits);
                c.Advance(1);Publish(bridge,70,12);Drain(c);
                Assert.IsTrue(c.TryReadVitalsScalar("health",out object health,out reason),reason);Assert.AreEqual(80,(double)health);
                Assert.IsTrue(c.TryReadVitalsScalar("stamina",out object stamina,out reason),reason);Assert.AreEqual(long.MinValue,BitConverter.DoubleToInt64Bits((double)stamina));
                Assert.AreEqual(70,s.VitalsState.Health);Assert.AreEqual(revision,c.SourceRevision);
                Assert.IsFalse(c.TryBuildWholeWorldSave(out reason));Assert.AreEqual("continuous_overlay_other_producers_not_enrolled",reason);
            }
        }
        [Test]public void CrossSessionProducerCannotOverlayAnotherActualGenesis()
        {var a=Boot();var b=Boot();var g=Genesis(a);using var foreign=new ActualVitalsProjectionBridge(b);Assert.IsFalse(g.TryBeginVitalsOverlay(foreign,out _,out string reason));Assert.AreEqual("continuous_vitals_source_mismatch",reason);}
        [Test]public void RawDriftAfterPinRefusesBeforeNextCopyAndObservation()
        {
            var s=Boot();var g=Genesis(s);using var b=new ActualVitalsProjectionBridge(s);Assert.IsTrue(g.TryBeginVitalsOverlay(b,out var c,out _));
            using(c){c.Advance(1);s.VitalsState.Health-=1;Assert.AreEqual(ContinuousTraversalStatus.Refused,c.Advance(1));Assert.AreEqual(0,c.LastWorkUnits);Assert.IsFalse(c.TryReadVitalsScalar("health",out _,out _));}
        }
        [Test]public void DriftAfterCompletionRevokesClosedScalarObservation()
        {
            var s=Boot();var g=Genesis(s);using var b=new ActualVitalsProjectionBridge(s);Assert.IsTrue(g.TryBeginVitalsOverlay(b,out var c,out _));
            using(c){Drain(c);s.VitalsState.MaxStamina+=1;Assert.IsFalse(c.TryReadVitalsScalar("health",out _,out string reason));Assert.AreEqual("continuous_vitals_source_revoked",reason);Assert.AreEqual(ContinuousTraversalStatus.Refused,c.Status);}
        }
        [TestCase(-1)][TestCase(65)]public void InvalidBudgetDoesNoWorkAndRefuses(int budget)
        {var s=Boot();var g=Genesis(s);using var b=new ActualVitalsProjectionBridge(s);Assert.IsTrue(g.TryBeginVitalsOverlay(b,out var c,out _));using(c){Assert.AreEqual(ContinuousTraversalStatus.Refused,c.Advance(budget));Assert.AreEqual(0,c.LastWorkUnits);}}
        [Test]public void CancellationNeverPublishesOrRestoresActualVitals()
        {var s=Boot();var g=Genesis(s);using var b=new ActualVitalsProjectionBridge(s);Assert.IsTrue(g.TryBeginVitalsOverlay(b,out var c,out _));using(c){c.Advance(1);c.Cancel();Publish(b,66,9);Assert.AreEqual(ContinuousTraversalStatus.Cancelled,c.Advance(64));Assert.AreEqual(66,s.VitalsState.Health);Assert.IsFalse(c.TryReadVitalsScalar("health",out _,out _));}}
    }
}
