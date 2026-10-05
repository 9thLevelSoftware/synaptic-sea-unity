using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    [NonParallelizable]
    public class AuxiliaryPreparedPairTests
    {
        IResourceReader _before;
        [SetUp] public void SetUp()
        { _before = ResourceAuthorityPublication.Reader; Publish(); }
        [TearDown] public void TearDown() => ResourceAuthorityPublication.ReplaceReader(_before);
        static void Publish() => ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(new Dictionary<string, string>()));
        sealed class Rig
        {
            internal readonly InventoryState Inventory = InventoryState.CreateTracked(new GdDict());
            internal readonly DiagnosticAuxiliaryStaminaCell Stamina = new DiagnosticAuxiliaryStaminaCell(100);
            internal readonly DiagnosticAuxiliaryPairContext Context;
            internal readonly AuxiliaryWorkRuntime Runtime;
            internal readonly ResourceAuthorityLease Lease;
            internal Rig(int capacity = 2, int maxSteps = 10, long sequence = 0)
            {
                Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out Lease, out _));
                Context = new DiagnosticAuxiliaryPairContext(0, Frame(), Inventory, Lease);
                Runtime = AuxiliaryWorkRuntime.CreateDiagnosticPaired("home", "run", "actor", "utility", 0,
                    PaidHashContext.BitsV2.Algorithm, 12, 0, 0, sequence, capacity, new AuxiliaryEvidenceLimits(8, maxSteps, 2), Stamina, Context);
            }
            internal AuxiliaryWorkFrame Frame(double delta = .1) => new AuxiliaryWorkFrame(delta, Stamina.Value, 100, 1);
            internal void Next(long ordinal, AuxiliaryWorkFrame frame = null) => Context.Replace(ordinal, frame ?? Frame(), Inventory, Lease);
            internal AuxiliaryWorkRuntime.PreparedPair Prepare()
            { Assert.IsTrue(Runtime.TryPrepareDiagnosticPair(out var plan, out var result, out string reason), reason); Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, result.Status); return plan; }
            internal bool Install(AuxiliaryWorkRuntime.PreparedPair plan)
            { using (var attempt = CommonParticipantGate.BeginAttempt()) return plan.TryInstallUnderGate(Runtime, attempt); }
        }
        [Test] public void ExactLegacyArithmeticAndFreshWorldRecoveryArePaired()
        {
            var rig = new Rig(); var reference = new AuxiliaryWorkRuntime("home", "run", "actor", "utility", 0,
                PaidHashContext.BitsV2.Algorithm, 12, 0, 0, 100, 0, 2);
            var expected = reference.Step(rig.Frame()); var plan = rig.Prepare(); Assert.IsTrue(rig.Install(plan));
            Assert.AreEqual(expected.Snapshot.ProgressSeconds, rig.Runtime.Snapshot().ProgressSeconds);
            Assert.AreEqual(expected.StaminaAfter, rig.Stamina.Value); Assert.AreEqual(1, rig.Runtime.EvidenceJournal.AcceptedStepCount);
            rig.Stamina.Set(100); rig.Next(1); expected = reference.Step(rig.Frame()); Assert.IsTrue(rig.Install(rig.Prepare()));
            Assert.AreEqual(expected.Snapshot.ProgressSeconds, rig.Runtime.Snapshot().ProgressSeconds);
            Assert.AreEqual(expected.StaminaAfter, rig.Stamina.Value); Assert.AreEqual(100, rig.Runtime.CopyAcceptedSteps()[1].StaminaBefore);
            Assert.IsFalse(rig.Context.IsLiveSceneAuthority); Assert.IsFalse(rig.Stamina.IsLiveSceneAuthority);
        }
        [Test] public void PublicStepCannotAdvanceFreshPairedBirthButLegacyRemainsUsable()
        {
            var rig = new Rig(); var result = rig.Runtime.Step(rig.Frame()); Assert.AreEqual("paired_step_only", result.Reason);
            Assert.AreEqual(0, rig.Runtime.Snapshot().EligibleSteps); Assert.AreEqual(100, rig.Stamina.Value);
            var legacy = new AuxiliaryWorkRuntime("h", "r", "a", "s", 0, PaidHashContext.Legacy.Algorithm, 12, 0, 0, 100, 0, 1);
            Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, legacy.Step(rig.Frame()).Status);
        }
        [Test] public void DuplicatePlanAndFrameNeverDebitTwice()
        {
            var rig = new Rig(); var first = rig.Prepare(); var duplicate = rig.Prepare(); Assert.IsTrue(rig.Install(first));
            double after = rig.Stamina.Value; Assert.IsFalse(rig.Install(first)); Assert.IsFalse(rig.Install(duplicate));
            Assert.IsFalse(rig.Runtime.TryPrepareDiagnosticPair(out _, out _, out string reason)); Assert.AreEqual("frame_already_applied", reason);
            Assert.AreEqual(after, rig.Stamina.Value); Assert.AreEqual(1, rig.Runtime.EvidenceJournal.AcceptedStepCount);
        }
        [TestCase("stamina")][TestCase("context")][TestCase("inventory")][TestCase("resources")]
        public void SameValueAndDependencyChangesInvalidatePlan(string dependency)
        {
            var rig = new Rig(); var plan = rig.Prepare();
            switch (dependency)
            {
                case "stamina": rig.Stamina.Set(100); break;
                case "context": rig.Next(0); break;
                case "inventory": rig.Inventory.BonusCapacity = rig.Inventory.BonusCapacity; break;
                case "resources": Publish(); break;
            }
            Assert.IsFalse(rig.Install(plan)); Assert.AreEqual(100, rig.Stamina.Value); Assert.AreEqual(0, rig.Runtime.Snapshot().EligibleSteps);
        }
        [Test] public void RotationInvalidatesSlotBindingWithoutLosingAcceptedHistory()
        {
            var rig = new Rig(); Assert.IsTrue(rig.Install(rig.Prepare())); rig.Next(1); var stale = rig.Prepare();
            Assert.IsTrue(rig.Runtime.TryPrepareEvidenceRotation(out var rotate, out _)); Assert.IsTrue(rig.Runtime.TryInstallEvidenceRotation(rotate));
            Assert.IsFalse(rig.Install(stale)); Assert.AreEqual(1, rig.Runtime.EvidenceJournal.AcceptedStepCount);
            Assert.IsTrue(rig.Install(rig.Prepare())); Assert.AreEqual(2, rig.Runtime.EvidenceJournal.AcceptedStepCount);
        }
        [TestCase(1, 10, "step_log_full")][TestCase(2, 1, "complete_history_capacity")]
        public void CapacityRefusesBeforeDebit(int slots, int history, string refusal)
        {
            var rig = new Rig(slots, history); Assert.IsTrue(rig.Install(rig.Prepare())); rig.Next(1); double before = rig.Stamina.Value;
            Assert.IsFalse(rig.Runtime.TryPrepareDiagnosticPair(out _, out _, out string reason)); Assert.AreEqual(refusal, reason);
            Assert.AreEqual(before, rig.Stamina.Value); Assert.AreEqual(1, rig.Runtime.EvidenceJournal.AcceptedStepCount);
        }
        [TestCase(1)][TestCase(2)]
        [TestCase(3)][TestCase(4)]
        [TestCase(5)][TestCase(6)]
        public void EveryAttemptedWriteRollsBackImmediatelyWithoutRefundingLaterWorldChanges(int fault)
        {
            var rig = new Rig(); var plan = rig.Prepare(); ulong stamp = rig.Stamina.Stamp;
            using (var attempt = CommonParticipantGate.BeginAttempt())
                Assert.Throws<InvalidOperationException>(() => plan.TryInstallUnderGate(rig.Runtime, attempt, (AuxiliaryWorkRuntime.PairFault)fault));
            Assert.AreEqual(100, rig.Stamina.Value); Assert.Greater(rig.Stamina.Stamp, stamp);
            Assert.AreEqual(0, rig.Runtime.Snapshot().EligibleSteps); Assert.AreEqual(0, rig.Runtime.RetainedStepCount);
            Assert.AreEqual(0, rig.Runtime.EvidenceJournal.AcceptedStepCount);
            rig.Stamina.Set(80); rig.Next(1);
            using (var attempt = CommonParticipantGate.BeginAttempt()) Assert.Throws<InvalidOperationException>(() => plan.RollbackUnderGate(attempt));
            Assert.AreEqual(80, rig.Stamina.Value); Assert.IsTrue(rig.Install(rig.Prepare()));
        }
        [Test] public void ForeignRuntimeClosedAttemptAndWrongThreadRefuse()
        {
            var rig = new Rig(); var other = new Rig(); var plan = rig.Prepare();
            using (var attempt = CommonParticipantGate.BeginAttempt()) Assert.IsFalse(plan.TryInstallUnderGate(other.Runtime, attempt));
            var closed = CommonParticipantGate.BeginAttempt(); closed.Dispose();
            using (var current = CommonParticipantGate.BeginAttempt()) Assert.IsFalse(plan.TryInstallUnderGate(rig.Runtime, closed));
            Exception caught = null; var thread = new Thread(() => { try { rig.Runtime.TryPrepareDiagnosticPair(out _, out _, out _); } catch (Exception ex) { caught = ex; } });
            thread.Start(); thread.Join(); Assert.IsInstanceOf<InvalidOperationException>(caught);
            Assert.AreEqual(100, rig.Stamina.Value); Assert.IsTrue(rig.Install(plan));
        }
        [Test] public void SameAttemptStaminaInterventionRefusesRollbackBeforeAnyRuntimeOrHistoryWrite()
        {
            var rig = new Rig(); var plan = rig.Prepare();
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                Assert.IsTrue(plan.TryInstallUnderGate(rig.Runtime, attempt));
                rig.Stamina.Set(80); var before = rig.Runtime.CaptureDiagnosticPair();
                var step = rig.Runtime.CopyAcceptedSteps()[0];
                Assert.Throws<InvalidOperationException>(() => plan.RollbackUnderGate(attempt));
                var after = rig.Runtime.CaptureDiagnosticPair();
                Assert.AreEqual(before.Work.ProgressSeconds, after.Work.ProgressSeconds);
                Assert.AreEqual(before.Work.EligibleSeconds, after.Work.EligibleSeconds);
                Assert.AreEqual(before.DirtyRevision, after.DirtyRevision); Assert.AreEqual(before.StaminaStamp, after.StaminaStamp);
                Assert.AreEqual(before.LastAppliedFrame, after.LastAppliedFrame); Assert.AreEqual(before.ActiveSteps, after.ActiveSteps);
                Assert.AreEqual(before.TotalAcceptedSteps, after.TotalAcceptedSteps); Assert.AreEqual(80, after.CurrentStamina);
                Assert.AreSame(step, rig.Runtime.CopyAcceptedSteps()[0]);
            }
        }
        [TestCase("context")][TestCase("rotation")]
        public void SameAttemptContextOrEvidenceInterventionPreservesCurrentStateOnRollbackRefusal(string change)
        {
            var rig = new Rig(); var plan = rig.Prepare();
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                Assert.IsTrue(plan.TryInstallUnderGate(rig.Runtime, attempt));
                if (change == "context") rig.Next(1);
                else
                { Assert.IsTrue(rig.Runtime.TryPrepareEvidenceRotation(out var rotate, out _)); Assert.IsTrue(rig.Runtime.TryInstallEvidenceRotation(rotate)); }
                var before = rig.Runtime.CaptureDiagnosticPair(); var pending = rig.Runtime.PendingEvidence;
                Assert.Throws<InvalidOperationException>(() => plan.RollbackUnderGate(attempt));
                var after = rig.Runtime.CaptureDiagnosticPair();
                Assert.AreEqual(before.Work.ProgressSeconds, after.Work.ProgressSeconds); Assert.AreEqual(before.CurrentStamina, after.CurrentStamina);
                Assert.AreEqual(before.StaminaStamp, after.StaminaStamp); Assert.AreEqual(before.DirtyRevision, after.DirtyRevision);
                Assert.AreEqual(before.ActiveSteps, after.ActiveSteps); Assert.AreEqual(before.TotalAcceptedSteps, after.TotalAcceptedSteps);
                Assert.AreEqual(before.LastAppliedFrame, after.LastAppliedFrame); Assert.AreSame(pending, rig.Runtime.PendingEvidence);
            }
        }
        [Test] public void ACommittedPlanCannotUndoAcrossAttemptsOrAfterWorldRecovery()
        {
            var rig = new Rig(); var plan = rig.Prepare(); Assert.IsTrue(rig.Install(plan));
            var before = rig.Runtime.CaptureDiagnosticPair(); rig.Stamina.Set(99);
            using (var attempt = CommonParticipantGate.BeginAttempt())
                Assert.Throws<InvalidOperationException>(() => plan.RollbackUnderGate(attempt));
            var after = rig.Runtime.CaptureDiagnosticPair();
            Assert.AreEqual(99, after.CurrentStamina); Assert.AreEqual(before.Work.ProgressSeconds, after.Work.ProgressSeconds);
            Assert.AreEqual(1, after.ActiveSteps); Assert.AreEqual(1, after.TotalAcceptedSteps); Assert.AreEqual(0, after.LastAppliedFrame);
        }
        [TestCase("consent")][TestCase("moving")][TestCase("damage")][TestCase("held")][TestCase("delta")]
        public void ExactLegacyGatePrecedenceNeverPublishesOrDebits(string blocked)
        {
            var rig = new Rig(); var frame = new AuxiliaryWorkFrame(blocked == "delta" ? double.NaN : .1, 100, 100, 1,
                consent: blocked != "consent", held: blocked != "held", moving: blocked == "moving", damaged: blocked == "damage");
            rig.Next(0, frame);
            var reference = new AuxiliaryWorkRuntime("h", "r", "a", "s", 0, PaidHashContext.Legacy.Algorithm, 12, 0, 0, 100, 0, 1);
            var expected = reference.Step(frame);
            Assert.IsFalse(rig.Runtime.TryPrepareDiagnosticPair(out _, out var result, out _));
            Assert.AreEqual(expected.Status, result.Status); Assert.AreEqual(expected.Reason, result.Reason);
            var captured = rig.Runtime.CaptureDiagnosticPair(); Assert.AreEqual(100, captured.CurrentStamina);
            Assert.AreEqual(0, captured.DirtyRevision); Assert.AreEqual(0, captured.TotalAcceptedSteps);
        }
        [Test] public void SequenceAndStampOverflowRefuseBeforeAnyDebit()
        {
            var sequence = new Rig(sequence: long.MaxValue); Assert.IsFalse(sequence.Runtime.TryPrepareDiagnosticPair(out _, out _, out string reason));
            Assert.AreEqual("sequence_overflow", reason); Assert.AreEqual(100, sequence.Stamina.Value);
            var rig = new Rig(); typeof(DiagnosticAuxiliaryStaminaCell).GetField("_stamp", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(rig.Stamina, ulong.MaxValue - 1);
            Assert.Throws<InvalidOperationException>(() => rig.Runtime.TryPrepareDiagnosticPair(out _, out _, out _));
            Assert.AreEqual(100, rig.Stamina.Value); Assert.AreEqual(0, rig.Runtime.EvidenceJournal.AcceptedStepCount);
        }
    }
}
