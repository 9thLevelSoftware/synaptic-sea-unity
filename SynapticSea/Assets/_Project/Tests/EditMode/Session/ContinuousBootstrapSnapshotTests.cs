using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class ContinuousBootstrapSnapshotTests:InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp]public void Engine(){_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown]public void ResetEngine(){CoreServices.Engine=_engine;}
        static void UseCanonicalAuxiliaryFixture(RunSessionDeps deps)
        {const string p="res://data/diagnostics/earned-services-home-v1/";deps.LayoutPath=p+"layout.json";deps.GameplaySlicePath=p+"gameplay_slice.json";deps.BlueprintPath=p+"blueprint.json";}
        [Test]public void ActualBootAssemblerCacheIsOwnedWhileRealVitalsAndPoseAdvance()
        {
            var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);deps.EnablePaidCrafting=true;deps.EnableBitExactPaidCompatibility=true;deps.EnableAuxiliaryServices=true;deps.EnableManualStudy=true;deps.EnableComponentIntegration=true;deps.EnableContinuousAuxiliaryDiagnostic=true;UseCanonicalAuxiliaryFixture(deps);
            var session=RunSession.Create(deps);rig.Session=session;Assert.IsTrue(session.PlayableStarted,session.LastFailureReason);
            Assert.IsTrue(ContinuousBootstrapSnapshot.TryReadFreshBeforeContinuousLoop(session,out var cache,out string reason),reason);
            Assert.Greater(cache.ArtifactCount,0);Assert.IsTrue(cache.TryReadArtifact(0,out string path,out string kind,out string text,out string version));
            Assert.IsNotEmpty(path);Assert.IsNotEmpty(kind);Assert.IsNotEmpty(text);Assert.IsNotEmpty(version);
            var first=cache.BeginTraversal(false);while(first.Advance(1)==ContinuousTraversalStatus.Pending){}int before=first.VisitedNodes;
            session.VitalsState.ApplyDelta(new SynapticSea.Core.Variant.GdDict{{"health",-5.0},{"stamina",-8.0}});
            rig.Scene.PlayerPosition=new SynapticSea.Core.Variant.Vec3(2,.5,3);
            var second=cache.BeginTraversal(false);Assert.AreEqual(ContinuousTraversalStatus.Pending,second.Advance(0));
            while(second.Advance(1)==ContinuousTraversalStatus.Pending){}Assert.AreEqual(ContinuousTraversalStatus.CompletePartialDiagnostic,second.Status);Assert.AreEqual(before,second.VisitedNodes);
            Assert.IsFalse(cache.TryBuildWholeWorldSave(out reason));Assert.AreEqual("continuous_dynamic_producers_not_enrolled",reason);
            var world=cache.BeginTraversal(true);world.Advance(1);world.Cancel();Assert.AreEqual(ContinuousTraversalStatus.Cancelled,world.Advance(64));
        }
        [Test]public void ExplicitConstructionPhaseAllowsNonzeroRestoredTimeAndClosesOnFirstTick()
        {
            var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);
            deps.EnablePaidCrafting=true;deps.EnableBitExactPaidCompatibility=true;deps.EnableAuxiliaryServices=true;
            deps.EnableManualStudy=true;deps.EnableComponentIntegration=true;deps.EnableContinuousAuxiliaryDiagnostic=true;UseCanonicalAuxiliaryFixture(deps);
            var session=RunSession.Create(deps);rig.Session=session;Assert.IsTrue(session.PlayableStarted,session.LastFailureReason);
            session.RunPlayTimeSeconds=432;session.WorldTime=123;
            Assert.IsTrue(ContinuousBootstrapSnapshot.TryReadFreshBeforeContinuousLoop(session,out _,out string reason),reason);
            session.Tick(new TickContext{Delta=0,HasPlayer=false});
            Assert.IsFalse(ContinuousBootstrapSnapshot.TryReadFreshBeforeContinuousLoop(session,out _,out reason));
            Assert.AreEqual("continuous_bootstrap_not_fresh",reason);
        }
        [Test]public void DefaultOffOrdinaryBootCannotMintDiagnosticGenesis()
        {
            var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);
            var session=RunSession.Create(deps);rig.Session=session;Assert.IsTrue(session.PlayableStarted,session.LastFailureReason);
            Assert.IsFalse(ContinuousBootstrapSnapshot.TryReadFreshBeforeContinuousLoop(session,out _,out _));
        }
        [Test]public void MissingActualSessionCannotMintBootstrapCache()
        {Assert.IsFalse(ContinuousBootstrapSnapshot.TryReadFreshBeforeContinuousLoop(null,out _,out string reason));Assert.AreEqual("continuous_bootstrap_not_fresh",reason);}
    }
}
