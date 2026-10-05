using System;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;

namespace SynapticSea.Tests.Session
{
    public class AuxiliaryEvidenceHandoffTests
    {
        static AuxiliaryWorkRuntime Runtime(int capacity = 2, int maximumSteps = 20, int maximumChunks = 8, int queue = 1)
            => AuxiliaryWorkRuntime.CreateWithEvidenceHandoff("home", "run", "actor", "maintenance_fabricator_feed_01",
                7, PaidHashContext.BitsV2.Algorithm, 12, 0, 0, 100, 0, capacity,
                new AuxiliaryEvidenceLimits(maximumChunks, maximumSteps, queue));
        static AuxiliaryWorkStepResult Step(AuxiliaryWorkRuntime runtime, double delta = .1)
            => runtime.Step(new AuxiliaryWorkFrame(delta, 100, 100, 1));
        static AuxiliarySealedEvidenceChunk Seal(AuxiliaryWorkRuntime runtime)
        {
            Assert.IsTrue(runtime.TryPrepareEvidenceRotation(out var rotation, out string reason), reason);
            Assert.IsTrue(runtime.TryInstallEvidenceRotation(rotation));
            Assert.IsFalse(runtime.TryInstallEvidenceRotation(rotation), "Rotation token is one-use.");
            return runtime.PendingEvidence;
        }
        [Test] public void CustodyAckIsExactOnceAndNeverAdmissionOrDurability()
        {
            var runtime = Runtime(); Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, Step(runtime).Status);
            var chunk = Seal(runtime); var before = runtime.Snapshot();
            Assert.AreEqual(AuxiliaryCustodyStatus.Accepted, runtime.EvidenceJournal.TryTakeCustody(chunk, out var ack));
            Assert.IsFalse(ack.IsAdmissionAck); Assert.IsFalse(ack.IsDurableAck); Assert.IsFalse(chunk.IsAdmitted); Assert.IsFalse(chunk.IsDurable);
            Assert.AreEqual(AuxiliaryCustodyStatus.AlreadyInCustody, runtime.EvidenceJournal.TryTakeCustody(chunk, out var retry));
            Assert.AreSame(ack, retry); Assert.AreSame(chunk, runtime.PendingEvidence);
            Assert.IsTrue(runtime.TryAcknowledgeEvidenceCustody(retry)); Assert.IsFalse(runtime.TryAcknowledgeEvidenceCustody(ack));
            Assert.IsNull(runtime.PendingEvidence); Assert.AreSame(chunk, runtime.EvidenceJournal.ReadRetainedChunk(0));
            Assert.AreEqual(1, runtime.EvidenceJournal.RetainedChunkCount); Assert.AreEqual(1, runtime.EvidenceJournal.AcceptedStepCount);
            Assert.AreEqual(before.ProgressSeconds, runtime.Snapshot().ProgressSeconds); Assert.AreEqual(before.LastAcceptedStamina, runtime.Snapshot().LastAcceptedStamina);
        }
        [Test] public void QueuePressurePreservesSamePendingChunkActiveSuffixAndCompleteHistory()
        {
            var runtime = Runtime(); Step(runtime); Step(runtime); var first = Seal(runtime);
            Assert.AreEqual(AuxiliaryCustodyStatus.Accepted, runtime.EvidenceJournal.TryTakeCustody(first, out var firstAck));
            Assert.IsTrue(runtime.TryAcknowledgeEvidenceCustody(firstAck));
            Step(runtime); Step(runtime); var second = Seal(runtime);
            Assert.IsFalse(runtime.TryAcknowledgeEvidenceCustody(firstAck), "An old ACK cannot erase the next pending chunk.");
            Assert.AreSame(second, runtime.PendingEvidence);
            Assert.AreEqual(AuxiliaryCustodyStatus.QueueFull, runtime.EvidenceJournal.TryTakeCustody(second, out _));
            Assert.AreSame(second, runtime.PendingEvidence);
            Assert.IsFalse(runtime.TryPrepareEvidenceRotation(out _, out string reason)); Assert.AreEqual("custody_pending", reason);
            Step(runtime); Step(runtime); var before = runtime.Snapshot();
            Assert.AreEqual(AuxiliaryWorkStepStatus.Backpressure, Step(runtime).Status);
            Assert.AreEqual(before.ProgressSeconds, runtime.Snapshot().ProgressSeconds); Assert.AreEqual(before.LastAcceptedStamina, runtime.Snapshot().LastAcceptedStamina);
            Assert.IsTrue(runtime.EvidenceJournal.TryAcquireQueuedChunk(out var acquired)); Assert.AreSame(first, acquired);
            Assert.AreEqual(AuxiliaryCustodyStatus.Accepted, runtime.EvidenceJournal.TryTakeCustody(second, out var secondAck));
            Assert.IsTrue(runtime.TryAcknowledgeEvidenceCustody(secondAck));
            Assert.AreSame(first, runtime.EvidenceJournal.ReadRetainedChunk(0)); Assert.AreSame(second, runtime.EvidenceJournal.ReadRetainedChunk(1));
            Assert.AreEqual(6, runtime.EvidenceJournal.AcceptedStepCount); Assert.AreEqual(2, runtime.RetainedStepCount);
            Assert.AreEqual(1L, first.FirstSequence); Assert.AreEqual(2L, first.LastSequence);
            Assert.AreEqual(3L, second.FirstSequence); Assert.AreEqual(4L, second.LastSequence);
        }
        [Test] public void FailedStaleRotationLeavesActiveBufferAndCostsIntact()
        {
            var runtime = Runtime(); Step(runtime);
            Assert.IsTrue(runtime.TryPrepareEvidenceRotation(out var stale, out _)); Step(runtime);
            var before = runtime.Snapshot(); var rows = runtime.CopyAcceptedSteps();
            Assert.IsFalse(runtime.TryInstallEvidenceRotation(stale)); Assert.IsNull(runtime.PendingEvidence);
            Assert.AreEqual(2, runtime.RetainedStepCount); Assert.AreEqual(before.ProgressSeconds, runtime.Snapshot().ProgressSeconds);
            Assert.AreEqual(before.EligibleSeconds, runtime.Snapshot().EligibleSeconds); Assert.AreEqual(before.LastAcceptedStamina, runtime.Snapshot().LastAcceptedStamina);
            Assert.AreSame(rows[0], runtime.CopyAcceptedSteps()[0]); Assert.AreSame(rows[1], runtime.CopyAcceptedSteps()[1]);
        }
        [Test] public void ForeignEpochOrAckCannotEraseEitherRuntimeHistory()
        {
            var first = Runtime(); var second = Runtime(); Step(first); Step(second);
            var firstChunk = Seal(first); var secondChunk = Seal(second);
            Assert.AreEqual(AuxiliaryCustodyStatus.Refused, first.EvidenceJournal.TryTakeCustody(secondChunk, out _));
            Assert.AreEqual(AuxiliaryCustodyStatus.Accepted, second.EvidenceJournal.TryTakeCustody(secondChunk, out var foreignAck));
            Assert.IsFalse(first.TryAcknowledgeEvidenceCustody(foreignAck)); Assert.AreSame(firstChunk, first.PendingEvidence);
            Assert.AreSame(secondChunk, second.PendingEvidence); Assert.AreEqual(0, first.EvidenceJournal.RetainedChunkCount);
            Assert.AreEqual(1, second.EvidenceJournal.RetainedChunkCount);
        }
        [Test] public void WorkerFaultOrCancellationDoesNotDeleteDequeuedEvidence()
        {
            var runtime = Runtime(); Step(runtime); var chunk = Seal(runtime);
            runtime.EvidenceJournal.TryTakeCustody(chunk, out var ack); Assert.IsTrue(runtime.TryAcknowledgeEvidenceCustody(ack));
            Assert.IsTrue(runtime.EvidenceJournal.TryAcquireQueuedChunk(out var workerInput));
            try { throw new OperationCanceledException("diagnostic worker"); } catch (OperationCanceledException) { }
            Assert.AreSame(chunk, workerInput); Assert.AreSame(chunk, runtime.EvidenceJournal.ReadRetainedChunk(0));
            Assert.AreEqual(1, runtime.EvidenceJournal.AcceptedStepCount); Assert.AreEqual(0, runtime.EvidenceJournal.QueuedChunkCount);
            Assert.IsFalse(workerInput.IsAdmitted); Assert.IsFalse(workerInput.IsDurable);
        }
        [Test] public void FullHistoryBoundRefusesBeforeAnyNewProgressCostOrEvidence()
        {
            var runtime = Runtime(maximumSteps: 2); Step(runtime); Step(runtime); var chunk = Seal(runtime);
            runtime.EvidenceJournal.TryTakeCustody(chunk, out var ack); runtime.TryAcknowledgeEvidenceCustody(ack);
            var before = runtime.Snapshot(); var result = Step(runtime);
            Assert.AreEqual(AuxiliaryWorkStepStatus.Backpressure, result.Status); Assert.AreEqual("complete_history_capacity", result.Reason);
            Assert.AreEqual(before.ProgressSeconds, runtime.Snapshot().ProgressSeconds); Assert.AreEqual(before.EligibleSeconds, runtime.Snapshot().EligibleSeconds);
            Assert.AreEqual(before.LastAcceptedStamina, runtime.Snapshot().LastAcceptedStamina); Assert.AreEqual(before.EligibleSteps, runtime.Snapshot().EligibleSteps);
            Assert.AreEqual(2, runtime.EvidenceJournal.AcceptedStepCount); Assert.AreSame(chunk, runtime.EvidenceJournal.ReadRetainedChunk(0));
        }
        [Test] public void ConservativeWholeProofEnvelopeIncludesArchivedPendingAndActiveSteps()
        {
            var runtime = Runtime(capacity: 256, maximumSteps: 65536, maximumChunks: 256);
            for (int i = 0; i < 256; i++) Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, Step(runtime, .0001).Status);
            var first = Seal(runtime); runtime.EvidenceJournal.TryTakeCustody(first, out var ack); runtime.TryAcknowledgeEvidenceCustody(ack);
            for (int i = 256; i < 387; i++) Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, Step(runtime, .0001).Status);
            var before = runtime.Snapshot(); Assert.AreEqual(AuxiliaryWorkStepStatus.Backpressure, Step(runtime, .0001).Status);
            Assert.AreEqual(387, runtime.EvidenceJournal.AcceptedStepCount); Assert.AreEqual(before.ProgressSeconds, runtime.Snapshot().ProgressSeconds);
            Assert.AreEqual(131, runtime.RetainedStepCount); Assert.AreEqual(256, first.Count);
        }
        [Test] public void DefaultShadowPrefixBehaviorAndImmutableChunkViewsRemainSeparate()
        {
            var legacyShadow = new AuxiliaryWorkRuntime("home", "run", "actor", "service", 7, PaidHashContext.Legacy.Algorithm, 12, 0, 0, 100, 0, 2);
            Step(legacyShadow); Step(legacyShadow); Assert.AreEqual(2, legacyShadow.CopyAcceptedSteps().Length);
            Assert.IsNull(legacyShadow.EvidenceJournal); Assert.IsFalse(legacyShadow.TryPrepareEvidenceRotation(out _, out _));
            var runtime = Runtime(); Step(runtime); var accepted = runtime.CopyAcceptedSteps()[0]; var chunk = Seal(runtime);
            var activeCopy = runtime.CopyAcceptedSteps(); Assert.AreEqual(0, activeCopy.Length); Assert.AreSame(accepted, chunk.At(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => chunk.At(chunk.Count)); Assert.AreEqual(1, chunk.Count);
        }
    }
}
