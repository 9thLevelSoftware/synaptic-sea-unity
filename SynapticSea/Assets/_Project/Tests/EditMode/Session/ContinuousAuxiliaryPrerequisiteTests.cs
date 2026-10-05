using System;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    public sealed class ContinuousAuxiliaryPrerequisiteTests
    {
        static AuxiliaryWorkRuntime Runtime() => AuxiliaryWorkRuntime.CreateWithEvidenceHandoff("home", "run", "actor", "service", 1,
            PaidHashContext.BitsV2.Algorithm, 12, 0, 0, 100, 0, 4, new AuxiliaryEvidenceLimits(8, 100, 1));
        static void Step(AuxiliaryWorkRuntime runtime) => Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted,
            runtime.Step(new AuxiliaryWorkFrame(.1, 100, 100, 1)).Status);
        [Test] public void RequestCutPermanentlySealsPrefixAndEmptyCutIsIdempotent()
        {
            var runtime = Runtime(); Step(runtime); var before = runtime.Snapshot();
            Assert.IsTrue(runtime.TryPrepareEvidenceRequestCut(0, out var cut, out var reason), reason);
            AuxiliarySealedEvidenceChunk chunk;
            lock (CommonParticipantGate.SyncRoot) Assert.IsTrue(cut.TryInstallTransportUnderGate(out chunk));
            Assert.AreSame(chunk, runtime.EvidenceJournal.ReadRetainedChunk(0)); Assert.AreEqual(1UL, runtime.EvidenceJournal.StructuralVersion);
            Assert.AreEqual(before.ProgressSeconds, runtime.Snapshot().ProgressSeconds); Assert.AreEqual(before.LastAcceptedStamina, runtime.Snapshot().LastAcceptedStamina);
            Assert.AreEqual(0, runtime.RetainedStepCount); Step(runtime);
            Assert.AreEqual(1, chunk.Count); Assert.AreEqual(1L, chunk.LastSequence);
            Assert.IsTrue(runtime.EvidenceJournal.TryAcquireQueuedChunk(out var dequeued)); Assert.AreSame(chunk, dequeued);
            Assert.IsTrue(runtime.TryPrepareEvidenceRequestCut(1, out var second, out reason), reason);
            lock (CommonParticipantGate.SyncRoot) Assert.IsTrue(second.TryInstallTransportUnderGate(out _));
            Assert.IsTrue(runtime.TryPrepareEvidenceRequestCut(2, out var empty, out reason), reason);
            lock (CommonParticipantGate.SyncRoot) { Assert.IsTrue(empty.TryInstallTransportUnderGate(out var tail)); Assert.IsNull(tail); Assert.IsFalse(empty.TryInstallTransportUnderGate(out _)); }
            Assert.AreEqual(2UL, runtime.EvidenceJournal.StructuralVersion); Assert.AreEqual(2, runtime.EvidenceJournal.AcceptedStepCount);
        }
        [Test] public void StaleCutAndQueuePressureLeaveEarnedStateIntact()
        {
            var runtime = Runtime(); Step(runtime); Assert.IsTrue(runtime.TryPrepareEvidenceRequestCut(0, out var stale, out _)); Step(runtime);
            var before = runtime.Snapshot();
            lock (CommonParticipantGate.SyncRoot) Assert.IsFalse(stale.TryInstallTransportUnderGate(out _));
            Assert.AreEqual(0, runtime.EvidenceJournal.RetainedChunkCount); Assert.AreEqual(2, runtime.RetainedStepCount);
            Assert.IsTrue(runtime.TryPrepareEvidenceRequestCut(0, out var fresh, out _));
            lock (CommonParticipantGate.SyncRoot) Assert.IsTrue(fresh.TryInstallTransportUnderGate(out _));
            Step(runtime); var pressureBefore = runtime.Snapshot();
            Assert.IsFalse(runtime.TryPrepareEvidenceRequestCut(1, out _, out var reason)); Assert.AreEqual("custody_capacity", reason);
            Assert.AreEqual(pressureBefore.ProgressSeconds, runtime.Snapshot().ProgressSeconds); Assert.AreEqual(1, runtime.RetainedStepCount);
            Assert.AreEqual(before.EligibleSteps + 1, runtime.Snapshot().EligibleSteps);
        }
        [Test] public void EvaluatorKeepsActualModelUnchangedAndPreservesOrderedDamage()
        {
            var actual = new VitalsState(); var values = DiagnosticVitalsValues.FromModel(actual);
            var proposal = ContinuousVitalsEvaluator.Delta(values, -7, -3, -2, -1);
            Assert.AreEqual(100, actual.Health); Assert.AreEqual(100, actual.Stamina);
            Assert.AreEqual(93, proposal.After.Health); Assert.AreEqual(97, proposal.After.Stamina);
            Assert.AreEqual(1, proposal.DamageCount); Assert.AreEqual("direct_vitals_delta", proposal.DamageSource(0)); Assert.AreEqual(7, proposal.DamageAmount(0));
            var combat = ContinuousVitalsEvaluator.CombatHealth(values, 80);
            Assert.AreEqual("combat", combat.DamageSource(0)); Assert.AreEqual(20, combat.DamageAmount(0));
            Assert.Throws<ArgumentException>(() => ContinuousVitalsEvaluator.CombatHealth(values, double.NaN));
        }
    }
}
