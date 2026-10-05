using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    [NonParallelizable]
    public sealed class WorkerOriginAdmissionTests : InfraDataTestBase
    {
        static GdDict EmptyOwner() => new GdDict { { "schema_version", 1L }, { "revision", 0L },
            { "registry", new GdDict { { "schema_version", 1L }, { "instances", new GdDict() } } },
            { "holders", new GdDict() }, { "machinery", new GdDict() }, { "receipts", new GdDict() } };
        static ResourceAuthorityLease Lease()
        {
            ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(new Dictionary<string,string>()));
            Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var lease, out _)); return lease;
        }
        static DomainBundle.WorkerInput Input(GdDict owner)
        { Assert.IsTrue(DomainBundle.TryCreateWorkerInput(owner, out var input, out var reason), reason); return input; }
        static WorkerAdmissionOutcome Take(WorkerOriginAdmissionQueue queue, WorkerAdmissionJob job)
        {
            // Test-only wait. Production queue Poll/TryTake never wait for an incomplete task.
            Assert.IsTrue(job.WaitForDiagnosticTest(30000), "diagnostic worker did not exit");
            Assert.IsTrue(queue.TryTake(job, out var result)); return result;
        }
        static WorkerAdmissionState State(out WorkerAdmissionSlot slot)
        {
            var state = new WorkerAdmissionState(new WorkerAdmissionEpoch("session", "run", "actor"), 1, 2, 3, 4);
            slot = new WorkerAdmissionSlot(state); return state;
        }
        [Test]
        public void FreshOrdinaryWorkerAdmissionRetainsImmutableOriginNotInputBundleOrGetterClone()
        {
            var lease = Lease(); var input = Input(EmptyOwner()); var state = State(out var slot); var queue = new WorkerOriginAdmissionQueue(slot);
            Assert.Greater(input.LogicalRetainedBytes, 0); Assert.LessOrEqual(input.LogicalRetainedBytes, DomainBundle.WorkerInput.MaximumLogicalBytes);
            Assert.IsTrue(queue.BeginOrigin(input, lease, state, out var job, out _)); var result = Take(queue, job);
            Assert.AreEqual(WorkerAdmissionStatus.Accepted, result.Status, result.Reason); Assert.IsNotNull(result.Origin);
            Assert.AreNotSame(input.Bundle, result.Origin.Bundle, "issuer performs fresh TryCreate rather than trusting bare immutable handle");
            var mutable = result.Origin.Bundle.GetSummary(); mutable["revision"] = 999L;
            Assert.AreEqual(0, result.Origin.Revision); Assert.AreEqual(PaidHashContext.Legacy.Algorithm, result.Origin.HashAlgorithm);
            Assert.AreEqual(lease.Snapshot.ContentSha256, result.Origin.ResourceFingerprint);
            Assert.IsFalse(queue.TryTake(job, out _), "job is once-only"); Assert.AreEqual(0, queue.RetainedJobs);
        }
        [Test]
        public void BareConstructorOrPreparationLikeHandleCannotBypassFreshWorkerAdmission()
        {
            var invalid = EmptyOwner(); invalid.Erase("registry");
            var constructor = typeof(DomainBundle).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(GdDict), typeof(long), typeof(GdDict), typeof(PaidHashContext) }, null);
            Assert.IsNotNull(constructor);
            var bare = (DomainBundle)constructor.Invoke(new object[] { invalid, 0L, new GdDict(), PaidHashContext.Legacy });
            var input = new DomainBundle.WorkerInput(bare); var state = State(out var slot); var queue = new WorkerOriginAdmissionQueue(slot);
            Assert.IsTrue(queue.BeginOrigin(input, Lease(), state, out var job, out _));
            var result = Take(queue, job); Assert.AreEqual(WorkerAdmissionStatus.Refused, result.Status); Assert.IsNull(result.Origin);
        }
        [Test]
        public void ForgedJobSealCannotInjectAcceptedTaskOrIssueOriginFromAnotherQueue()
        {
            var input = Input(EmptyOwner()); var lease = Lease(); var state = State(out var slot); var queue = new WorkerOriginAdmissionQueue(slot);
            var forged = new WorkerAdmissionJob(queue, new object(), state, lease, 1, input, null, null, "");
            Assert.IsTrue(forged.WaitForDiagnosticTest(30000));
            Assert.AreEqual(WorkerAdmissionStatus.Refused, forged.CompletedOutcome().Status);
            Assert.IsNull(forged.CompletedOutcome().Origin); Assert.IsFalse(queue.TryTake(forged, out _));
            Assert.IsNull(typeof(WorkerAdmissionJob).GetField("Task", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
            Assert.AreEqual(0, queue.RetainedJobs);
        }

        [Test]
        public void CancelledJobsKeepQueueReservationUntilActualCompletedConsumption()
        {
            var input = Input(EmptyOwner()); var lease = Lease(); var state = State(out var slot); var queue = new WorkerOriginAdmissionQueue(slot);
            Assert.IsTrue(queue.BeginOrigin(input, lease, state, out var first, out _));
            Assert.IsTrue(queue.BeginOrigin(input, lease, state, out var second, out _)); first.Cancel(); second.Cancel();
            Assert.AreEqual(2, queue.RetainedJobs); Assert.Greater(queue.ReservedLogicalBytes, 0);
            Assert.IsFalse(queue.BeginOrigin(input, lease, state, out _, out var reason)); Assert.AreEqual("admission_capacity", reason);
            Assert.AreEqual(WorkerAdmissionStatus.Cancelled, Take(queue, first).Status);
            Assert.AreEqual(1, queue.RetainedJobs); Assert.AreEqual(WorkerAdmissionStatus.Cancelled, Take(queue, second).Status);
            Assert.AreEqual(0, queue.RetainedJobs); Assert.AreEqual(0, queue.ReservedLogicalBytes);
        }
        [Test]
        public void MainThreadFinalSlotStampsEpochAndResourceChecksRejectStaleResults()
        {
            var input = Input(EmptyOwner()); var lease = Lease(); var state = State(out var slot); var queue = new WorkerOriginAdmissionQueue(slot);
            Assert.IsTrue(queue.BeginOrigin(input, lease, state, out var changed, out _));
            slot.Replace(new WorkerAdmissionState(state.Epoch, 2, 2, 3, 4));
            Assert.AreEqual(WorkerAdmissionStatus.Stale, Take(queue, changed).Status);
            slot.Replace(state); Assert.IsTrue(queue.BeginOrigin(input, lease, state, out var death, out _)); state.Epoch.Revoke();
            Assert.AreEqual(WorkerAdmissionStatus.Cancelled, Take(queue, death).Status);
            var replacement = State(out var anotherSlot); var next = new WorkerOriginAdmissionQueue(anotherSlot);
            Assert.IsTrue(next.BeginOrigin(input, lease, replacement, out var resource, out _)); CatalogRegistry.Clear();
            var result = Take(next, resource); Assert.AreEqual(WorkerAdmissionStatus.Stale, result.Status); Assert.IsNull(result.Origin);
        }
        [Test]
        public void UndeclaredCatalogDependencyRefusesWithoutGlobalReaderFallback()
        {
            var owner = EmptyOwner(); owner["schema_version"] = 2L;
            owner["physical_slots"] = new GdDict(); owner["component_work"] = new GdDict(); owner["registered_owners"] = new GdArray();
            owner["command_sequence"] = 0L; owner["participating_state"] = new GdDict {
                { "inventory", new GdDict() }, { "progression", new GdDict() }, { "training", new GdDict { { "log", new GdArray() } } } };
            // Preparation sees the actual source closure; worker is deliberately pinned to an empty declaration.
            var input = Input(owner); var lease = Lease(); var state = State(out var slot); var queue = new WorkerOriginAdmissionQueue(slot);
            Assert.IsTrue(queue.BeginOrigin(input, lease, state, out var job, out _));
            var result = Take(queue, job); Assert.AreEqual(WorkerAdmissionStatus.Refused, result.Status);
            StringAssert.StartsWith("undeclared_resource:", result.Reason); Assert.IsNull(result.Origin);
        }

        [Test]
        public void OptInWorkerInputRefusesOversizedPrivateStringsWithoutChangingStockAdmission()
        {
            var owner = EmptyOwner(); owner.GetDictOrEmpty("holders")["holder"] = new GdDict {
                { "holder_id", "holder" }, { "kind", "player" }, { "owner_id", new string('x', DomainBundle.WorkerInput.MaximumStringCharacters + 1) }, { "revision", 0L } };
            Assert.IsTrue(DomainBundle.TryCreate(owner, out _, out _), "default legacy predicate stays unchanged");
            Assert.IsFalse(DomainBundle.TryCreateWorkerInput(owner, out _, out var reason)); Assert.AreEqual("worker_input_string_capacity", reason);
        }
        [Test]
        public void RealAuxiliaryCandidateUsesFreshFullAdmissionConservationAndExactReceiptIdentity()
        {
            // Test-only fixture reuse; no private/live fixture object is sent to workers.
            var fixture = new DiagnosticAuxiliaryPublisherTests();
            var factory = typeof(DiagnosticAuxiliaryPublisherTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Instance);
            var rig = factory.Invoke(fixture, new object[] { true });
            try
            {
                object Field(string name) => rig.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(rig);
                var before = (GdDict)Field("Before"); var candidate = (GdDict)Field("Candidate"); var lease = (ResourceAuthorityLease)Field("Lease");
                var state = AuxiliaryServiceState.State(before);
                var epoch = new WorkerAdmissionEpoch("real-fixture-session", state.GetString("run_id"), state.GetString("actor_id"));
                var expected = new WorkerAdmissionState(epoch, 10, 20, 30, 40); var slot = new WorkerAdmissionSlot(expected); var queue = new WorkerOriginAdmissionQueue(slot);
                Assert.IsTrue(queue.BeginOrigin(Input(before), lease, expected, out var originJob, out _)); var originResult = Take(queue, originJob);
                Assert.AreEqual(WorkerAdmissionStatus.Accepted, originResult.Status, originResult.Reason);
                string receipt = candidate.GetDictOrEmpty("receipts").Single(row => row.Value is GdDict r && r.GetDictOrEmpty("result").GetString("operation") == "aux_complete").Key as string;
                Assert.IsTrue(queue.BeginCandidate(originResult.Origin, Input(candidate), receipt, lease, expected, out var candidateJob, out _));
                var admitted = Take(queue, candidateJob); Assert.AreEqual(WorkerAdmissionStatus.Accepted, admitted.Status, admitted.Reason);
                Assert.IsNotNull(admitted.Candidate); Assert.AreEqual(receipt, admitted.Candidate.ReceiptId);
                Assert.IsTrue(admitted.Candidate.Bundle.HashContext.Equal(candidate, admitted.Candidate.Bundle.GetSummary()));
                Assert.IsTrue(queue.BeginCandidate(originResult.Origin, Input(candidate), "wrong-receipt", lease, expected, out var wrong, out _));
                Assert.AreEqual(WorkerAdmissionStatus.Refused, Take(queue, wrong).Status);
            }
            finally { ((IDisposable)rig).Dispose(); }
        }
    }
}
