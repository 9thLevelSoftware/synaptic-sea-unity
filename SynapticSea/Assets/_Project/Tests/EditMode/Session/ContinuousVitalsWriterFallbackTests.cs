using System;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public sealed class ContinuousVitalsWriterFallbackTests : InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp] public void Engine(){_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown] public void Restore(){CoreServices.Engine=_engine;}
        static RunSession Boot(){var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);var s=RunSession.Create(deps);Assert.IsTrue(s.PlayableStarted,s.LastFailureReason);return s;}
        [Test] public void RevokedCaptureDoesNotFreezeSameActualSurvivalOrCombat()
        {
            var s=Boot();var actual=s.VitalsState;using var bridge=new ActualVitalsProjectionBridge(s);
            var writer=new ContinuousActualVitalsWriter(s,bridge);
            lock(CommonParticipantGate.SyncRoot)bridge.RevokeCaptureForSimulationUnderGate();
            Assert.IsFalse(writer.CaptureAvailable);
            Assert.IsTrue(writer.TryTick(1,new DiagnosticVitalsTickInput(moving:true),out var reason),reason);
            Assert.Less(actual.Hunger,100);Assert.AreSame(actual,s.VitalsState);
            var target=(IDamageVitalsTarget)writer;double health=target.Health;target.Health=health-3;
            Assert.AreEqual(health-3,actual.Health);Assert.IsFalse(writer.CaptureAvailable);
        }
        [Test] public void NativeGenerationRejectsSameValueDamageABAWithoutRefund()
        {
            var s=Boot();using var bridge=new ActualVitalsProjectionBridge(s);var writer=new ContinuousActualVitalsWriter(s,bridge);
            lock(CommonParticipantGate.SyncRoot)bridge.RevokeCaptureForSimulationUnderGate();
            var target=(IDamageVitalsTarget)writer;double before=target.Health;
            Assert.IsTrue(writer.TryDelta(-2,0,0,0,out _));Assert.IsTrue(writer.TryDelta(2,0,0,0,out _));
            Assert.Throws<InvalidOperationException>(()=>target.Health=before-9);
            Assert.AreEqual(before,s.VitalsState.Health);Assert.AreEqual(1UL,writer.DamageEpoch);
        }
        [Test] public void ExhaustedDamageCounterRevokesCaptureButActualDamageAndSurvivalContinue()
        {
            var s=Boot();using var bridge=new ActualVitalsProjectionBridge(s);var writer=new ContinuousActualVitalsWriter(s,bridge);
            typeof(ContinuousActualVitalsWriter).GetField("_damageEpoch",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(writer,ulong.MaxValue);
            Assert.IsTrue(writer.TryDelta(-2,0,0,0,out var reason),reason);
            Assert.AreEqual(98,s.VitalsState.Health);Assert.AreEqual(ulong.MaxValue,writer.DamageEpoch);Assert.IsFalse(writer.CaptureAvailable);
            Assert.IsTrue(writer.TryTick(1,new DiagnosticVitalsTickInput(moving:false),out reason),reason);Assert.Less(s.VitalsState.Hunger,100);
        }
    }
}
