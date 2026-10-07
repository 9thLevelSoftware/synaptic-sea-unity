using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Tests.Session;
using SynapticSea.UI;

namespace SynapticSea.Tests.Unity
{
    /// <summary>Diagnostic session-to-UI seam; provisioned tools, real work dispatch and saved summary.</summary>
    public class WorkUiIntegrationTests : WorkSessionTestBase
    {
        [TestCase(false)]
        [TestCase(true)]
        public void RestoredWorkHud_RetainsTargetProgressAndResumeReasonUntilExplicitInput(bool tap)
        {
            SessionHarness.Rig source = Boot(tap);
            StartGoldenCut(source);
            Advance(source.Session, 2);
            source.Session.EndWorkHold();
            var snapshot = RunSnapshotAssembler.Build(source.Session);
            SessionHarness.Rig restored = Boot(tap);
            var strip = new WorkActionStrip();
            restored.Session.Events.WorkActionHudState += strip.SetWorkState;
            Assert.IsTrue(restored.Session.ApplyManualSlot(snapshot));
            WorkActionState work = restored.Session.WorkActionDriver.Work;
            Assert.IsTrue(strip.IsOpen());
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, strip.GetStatus());
            Assert.AreEqual(work.ProgressRatio(), strip.GetProgress());
            StringAssert.Contains(work.TargetId, strip.TitleText);
            StringAssert.Contains("resume required", strip.StateText);
            strip.SetCompact(true);
            StringAssert.Contains("resume required", strip.StateText);
            double progress = strip.GetProgress();
            Advance(restored.Session, 2);
            Assert.AreEqual(progress, strip.GetProgress());
            Assert.IsTrue(restored.Session.BeginWorkHold());
            if (tap) restored.Session.EndWorkHold();
            Advance(restored.Session, 2);
            Assert.AreEqual(WorkActionState.STATUS_ACTIVE, strip.GetStatus());
            StringAssert.DoesNotContain("resume required", strip.StateText);
            Assert.Greater(strip.GetProgress(), progress);
        }
    }
}
