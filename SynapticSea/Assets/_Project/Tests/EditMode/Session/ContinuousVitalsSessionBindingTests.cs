using System;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public sealed class ContinuousVitalsSessionBindingTests : InfraDataTestBase
    {
        [Test] public void ForeignSessionSharingActualModelCannotBindWriter()
        {
            var engine=CoreServices.Engine;
            try {
                CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);
                var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);
                var first=RunSession.Create(deps);Assert.IsTrue(first.PlayableStarted,first.LastFailureReason);
                using var bridge=new ActualVitalsProjectionBridge(first);
                var foreign=new RunSession(new RunSessionDeps());foreign.VitalsState=first.VitalsState;
                Assert.Throws<ArgumentException>(()=>new ContinuousActualVitalsWriter(foreign,bridge));
                Assert.DoesNotThrow(()=>new ContinuousActualVitalsWriter(first,bridge));
            } finally { CoreServices.Engine=engine; }
        }
    }
}
