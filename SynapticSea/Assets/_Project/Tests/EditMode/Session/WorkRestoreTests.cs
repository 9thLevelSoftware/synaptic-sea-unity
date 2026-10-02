using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>Invariant-only snapshot fixtures using real session dispatch and manual-slot application.</summary>
    public class WorkRestoreTests : WorkSessionTestBase
    {
        RunSnapshot SaveReleasedCut(bool tap)
        {
            SessionHarness.Rig source = Boot(tap);
            StartGoldenCut(source);
            Advance(source.Session, 2);
            source.Session.EndWorkHold();
            var snapshot = RunSnapshotAssembler.Build(source.Session);
            Assert.IsNotNull(snapshot);
            Assert.IsTrue(snapshot.WorkActionSummary.GetBool("active"));
            return snapshot;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RestoredWork_WaitsForExplicitPress_ThenResumesInCurrentInputMode(bool tap)
        {
            RunSnapshot snapshot = SaveReleasedCut(tap);
            SessionHarness.Rig rig = Boot(tap);
            RunSession session = rig.Session;
            Assert.IsTrue(session.ApplyManualSlot(snapshot));
            WorkActionState work = session.WorkActionDriver.Work;
            double progress = work.Progress;
            double stamina = session.VitalsState.Stamina;
            session.EndWorkHold();
            Advance(session, 2);
            Assert.AreEqual(progress, work.Progress, "restored work must await an explicit press in both input modes");
            Assert.AreEqual(stamina, session.VitalsState.Stamina);
            Assert.IsFalse(session.IsWorkInteractHeld);
            Assert.AreEqual("resume_required", work.BlockReason);
            Assert.IsTrue(session.BeginWorkHold(), "resume consumes the press before ordinary dispatch");
            if (tap) session.EndWorkHold();
            Advance(session, 2);
            Assert.Greater(work.Progress, progress);
            if (!tap)
            {
                progress = work.Progress;
                session.EndWorkHold();
                Advance(session, 2);
                Assert.AreEqual(progress, work.Progress, "held work still pauses on release after resume");
            }
        }

        [Test]
        public void Restore_ClearsAnAlreadyLatchedHold_AndReplacingWithNoWorkClearsOldJob()
        {
            RunSnapshot snapshot = SaveReleasedCut(false);
            SessionHarness.Rig rig = Boot();
            rig.Session.BeginWorkHold();
            Assert.IsTrue(rig.Session.ApplyManualSlot(snapshot));
            Assert.IsFalse(rig.Session.IsWorkInteractHeld, "loading cannot carry a prior input press into restored work");
            SessionHarness.Rig idle = Boot();
            Assert.IsTrue(rig.Session.ApplyManualSlot(RunSnapshotAssembler.Build(idle.Session)));
            Assert.IsFalse(rig.Session.WorkActionDriver.IsWorking(), "an idle snapshot replaces the existing work context");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RestoredResume_RemoteSite_RejectsWithoutProgressOrEffects(bool tap)
        {
            SessionHarness.Rig rig = Boot(tap);
            Assert.IsTrue(rig.Session.ApplyManualSlot(SaveReleasedCut(tap)));
            WorkActionState work = rig.Session.WorkActionDriver.Work;
            double progress = work.Progress;
            double stamina = rig.Session.VitalsState.Stamina;
            rig.Scene.PlayerPosition += new Vec3(1000, 0, 1000);
            Assert.IsTrue(rig.Session.BeginWorkHold());
            Advance(rig.Session);
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Status);
            Assert.AreEqual("left_work_site", work.BlockReason);
            Assert.AreEqual(progress, work.Progress);
            Assert.AreEqual(stamina, rig.Session.VitalsState.Stamina);
            Assert.AreEqual(0, rig.Session.InventoryState.GetQuantity("scrap_metal"));
            Assert.AreEqual(0, TrainingCount(rig.Session));
        }

        [Test]
        public void RestoredResume_MissingTool_RejectsUntilRequirementsAreSupplied()
        {
            SessionHarness.Rig rig = Boot();
            Assert.IsTrue(rig.Session.ApplyManualSlot(SaveReleasedCut(false)));
            WorkActionState work = rig.Session.WorkActionDriver.Work;
            double progress = work.Progress;
            rig.Session.InventoryState.RemoveItem("welding_lance", 1);
            Assert.IsTrue(rig.Session.BeginWorkHold());
            Advance(rig.Session, 2);
            Assert.AreEqual(progress, work.Progress);
            Assert.AreEqual("tool", work.BlockReason);
            rig.Session.InventoryState.AddItem("welding_lance", 1);
            rig.Session.EndWorkHold();
            Assert.IsTrue(rig.Session.BeginWorkHold());
            Advance(rig.Session, 2);
            Assert.Greater(work.Progress, progress);
        }

        [Test]
        public void SwitchingRestoredWorkToTap_DoesNotBypassExplicitResume()
        {
            SessionHarness.Rig rig = Boot();
            Assert.IsTrue(rig.Session.ApplyManualSlot(SaveReleasedCut(false)));
            WorkActionState work = rig.Session.WorkActionDriver.Work;
            double progress = work.Progress;
            rig.Session.SettingsState.SetHoldToTap(true);
            Advance(rig.Session, 2);
            Assert.AreEqual(progress, work.Progress);
            Assert.IsTrue(rig.Session.BeginWorkHold());
            rig.Session.EndWorkHold();
            Advance(rig.Session, 2);
            Assert.Greater(work.Progress, progress);
        }

        [TestCase("different-owner")]
        [TestCase("")]
        public void RestoredLegacyWork_WithUnknownOrDifferentOwner_FailsClosed(string savedLocation)
        {
            RunSnapshot snapshot = SaveReleasedCut(false);
            snapshot.CurrentLocation = savedLocation;
            SessionHarness.Rig rig = Boot();
            Assert.IsTrue(rig.Session.ApplyManualSlot(snapshot));
            WorkActionState work = rig.Session.WorkActionDriver.Work;
            double progress = work.Progress;
            Assert.IsTrue(rig.Session.BeginWorkHold());
            Advance(rig.Session, 2);
            Assert.AreEqual(progress, work.Progress);
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Status);
            Assert.AreEqual("work_context_changed", work.BlockReason);
        }

        [Test]
        public void RestoredPendingWork_CanBeExplicitlyCancelled()
        {
            SessionHarness.Rig rig = Boot();
            GdDict hud = null;
            rig.Session.Events.WorkActionHudState += state => hud = state;
            Assert.IsTrue(rig.Session.ApplyManualSlot(SaveReleasedCut(false)));
            WorkActionState work = rig.Session.WorkActionDriver.Work;
            Assert.IsTrue(rig.Session.CancelWorkAction());
            Assert.AreNotEqual("resume_required", work.BlockReason, "a cancelled job cannot continue to request resume");
            Assert.IsNotNull(hud);
            Assert.AreNotEqual("resume_required", hud.GetString("block_reason"));
            double progress = work.Progress;
            Advance(rig.Session, 2);
            Assert.AreEqual(progress, work.Progress);
            Assert.IsFalse(rig.Session.BeginWorkHold(), "a cancelled saved job does not consume a new work press");
        }
    }
}
