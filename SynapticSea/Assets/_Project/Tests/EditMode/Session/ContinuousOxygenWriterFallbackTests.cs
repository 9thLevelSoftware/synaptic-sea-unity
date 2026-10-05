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
    public class ContinuousOxygenWriterFallbackTests : InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp] public void Engine(){_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown] public void Restore(){CoreServices.Engine=_engine;}
        [TestCase(false)][TestCase(true)] public void CaptureRevocationOrClockExhaustionDoesNotStopNativeFieldPressure(bool exhaust)
        {
            var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);var session=RunSession.Create(deps);
            Assert.IsTrue(session.PlayableStarted,session.LastFailureReason);var actual=session.OxygenState;
            using var bridge=new ActualOxygenProjectionBridge(session);var writer=new ContinuousActualOxygenWriter(session,bridge);
            if(exhaust)typeof(ActualOxygenProjectionBridge).GetField("_stamp",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bridge,ulong.MaxValue);
            else lock(CommonParticipantGate.SyncRoot)bridge.RevokeCaptureForSimulationUnderGate();
            var before=ContinuousOxygenValues.CaptureExact(actual);var input=new ContinuousOxygenTickInput(false,true,1.4,.2);
            var expected=ContinuousOxygenEvaluator.EvaluateTick(before,.3,input);
            Assert.IsTrue(writer.TryTick(.3,input,out var reason),reason);
            Assert.IsTrue(expected.After.MatchesRaw(actual));Assert.AreSame(actual,session.OxygenState);Assert.IsFalse(writer.CaptureAvailable);
        }
    }
}
