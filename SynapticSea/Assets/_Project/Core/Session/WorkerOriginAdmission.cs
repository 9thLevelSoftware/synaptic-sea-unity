using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    // Unwired diagnostic epoch. Not a live scene, canonical owner or terminal-save authority.
    internal sealed class WorkerAdmissionEpoch
    {
        readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        int _revoked;
        internal string SessionId { get; }
        internal string RunId { get; }
        internal string ActorId { get; }
        internal bool Active => Volatile.Read(ref _revoked) == 0;
        internal WorkerAdmissionEpoch(string session, string run, string actor)
        {
            foreach (string id in new[] { session, run, actor })
                if (id == null || id.Length > 128 || string.IsNullOrWhiteSpace(id)) throw new ArgumentException("invalid_admission_epoch");
            SessionId = session; RunId = run; ActorId = actor;
        }
        internal void Revoke()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("admission_wrong_thread");
            using (CommonParticipantGate.BeginAttempt()) Interlocked.Exchange(ref _revoked, 1);
        }
    }
    internal sealed class WorkerAdmissionState
    {
        internal WorkerAdmissionEpoch Epoch { get; }
        internal ulong OwnerStamp { get; }
        internal ulong InventoryStamp { get; }
        internal ulong ProgressionStamp { get; }
        internal ulong TrainingStamp { get; }
        internal WorkerAdmissionState(WorkerAdmissionEpoch epoch, ulong owner, ulong inventory, ulong progression, ulong training)
        { Epoch = epoch ?? throw new ArgumentNullException(nameof(epoch)); OwnerStamp = owner; InventoryStamp = inventory; ProgressionStamp = progression; TrainingStamp = training; }
        internal bool Same(WorkerAdmissionState other) => other != null && ReferenceEquals(Epoch, other.Epoch) &&
            OwnerStamp == other.OwnerStamp && InventoryStamp == other.InventoryStamp && ProgressionStamp == other.ProgressionStamp && TrainingStamp == other.TrainingStamp;
    }
    internal sealed class WorkerAdmissionSlot
    {
        readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        WorkerAdmissionState _current;
        internal WorkerAdmissionSlot(WorkerAdmissionState initial) { _current = initial ?? throw new ArgumentNullException(nameof(initial)); }
        internal WorkerAdmissionState Capture() { lock (CommonParticipantGate.SyncRoot) return _current; }
        internal WorkerAdmissionState CurrentUnderGate { get { CommonParticipantGate.RequireHeld(); return _current; } }
        internal void Replace(WorkerAdmissionState next)
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("admission_wrong_thread");
            if (next == null) throw new ArgumentNullException(nameof(next));
            using (CommonParticipantGate.BeginAttempt()) _current = next;
        }
    }
    internal enum WorkerAdmissionStatus { Pending, Accepted, Refused, Cancelled, Stale, Faulted, Consumed }
    /// <summary>Issued only by fresh ordinary admission. No bare/preparation DomainBundle grants this seal.</summary>
    internal sealed class AdmittedWorkerOrigin
    {
        internal readonly DomainBundle Bundle;
        internal readonly ResourceAuthorityLease Lease;
        internal readonly WorkerAdmissionEpoch Epoch;
        internal readonly object IssuerSeal;
        internal readonly WorkerAdmissionState Basis;
        int _granted;
        internal bool Granted => Volatile.Read(ref _granted) != 0;
        internal string OwnerDigest { get; }
        internal long BundleWorkerLogicalBytes { get; }
        internal string ResourceFingerprint => Lease.Snapshot.ContentSha256;
        internal long ResourceVersion => Lease.Version;
        internal long Revision => Bundle.Revision;
        internal long FeatureSchema => Bundle.FeatureSchema;
        internal string HashAlgorithm => Bundle.HashContext.Algorithm;
        // Private issuance via the sealed queue's nested issuer; no serializer or imported caller flag.
        AdmittedWorkerOrigin(DomainBundle bundle, ResourceAuthorityLease lease, WorkerAdmissionEpoch epoch, object seal, string digest, long logicalBytes, WorkerAdmissionState basis)
        { Bundle = bundle; Lease = lease; Epoch = epoch; IssuerSeal = seal; OwnerDigest = digest; BundleWorkerLogicalBytes = logicalBytes; Basis = basis; }
        internal sealed class Issuer
        {
            readonly object _seal = new object();
            internal bool Owns(AdmittedWorkerOrigin origin) => origin != null && ReferenceEquals(origin.IssuerSeal, _seal);
            internal void GrantUnderGate(AdmittedWorkerOrigin origin)
            {
                CommonParticipantGate.RequireHeld();
                if (!Owns(origin)) throw new InvalidOperationException("origin_issuer_mismatch");
                Interlocked.Exchange(ref origin._granted, 1);
            }
            internal AdmittedWorkerOrigin Issue(DomainBundle ordinaryAdmitted, ResourceAuthorityLease lease, WorkerAdmissionEpoch epoch, GdDict ownedSummary, long logicalBytes, WorkerAdmissionState basis)
                => new AdmittedWorkerOrigin(ordinaryAdmitted, lease, epoch, _seal, ordinaryAdmitted.HashContext.Hash(ownedSummary), logicalBytes, basis);
        }
    }
    internal sealed class AdmittedWorkerCandidate
    {
        internal readonly DomainBundle Bundle;
        internal readonly AdmittedWorkerOrigin Origin;
        internal readonly string ReceiptId;
        internal AdmittedWorkerCandidate(DomainBundle admitted, AdmittedWorkerOrigin origin, string receipt)
        { Bundle = admitted; Origin = origin; ReceiptId = receipt; }
    }
    internal sealed class WorkerAdmissionOutcome
    {
        internal WorkerAdmissionStatus Status { get; }
        internal string Reason { get; }
        internal AdmittedWorkerOrigin Origin { get; }
        internal AdmittedWorkerCandidate Candidate { get; }
        internal WorkerAdmissionOutcome(WorkerAdmissionStatus status, string reason, AdmittedWorkerOrigin origin = null, AdmittedWorkerCandidate candidate = null)
        { Status = status; Reason = reason; Origin = origin; Candidate = candidate; }
    }
    internal sealed class WorkerAdmissionJob
    {
        internal readonly WorkerAdmissionState Expected;
        internal readonly ResourceAuthorityLease Lease;
        internal readonly long Reservation;
        readonly Task<WorkerAdmissionOutcome> _task;
        readonly object _jobSeal;
        internal bool IsCompleted => _task.IsCompleted;
        internal bool HasSeal(object seal) => ReferenceEquals(_jobSeal, seal);
        internal WorkerAdmissionOutcome CompletedOutcome()
        {
            if (!_task.IsCompleted) throw new InvalidOperationException("admission_pending");
            return _task.GetAwaiter().GetResult();
        }
        internal bool WaitForDiagnosticTest(int milliseconds) => _task.Wait(milliseconds);
        int _cancelled;
        internal bool Cancelled => Volatile.Read(ref _cancelled) != 0;
        internal bool Consumed;
        internal WorkerAdmissionJob(WorkerOriginAdmissionQueue queue, object seal, WorkerAdmissionState state, ResourceAuthorityLease lease,
            long reservation, DomainBundle.WorkerInput input, DomainBundle.WorkerInput candidate, AdmittedWorkerOrigin origin, string receipt)
        {
            Expected = state; Lease = lease; Reservation = reservation; _jobSeal = seal;
            _task = System.Threading.Tasks.Task.Run(() => queue.Admit(this, input, candidate, origin, receipt));
        }
        internal void Cancel() => Interlocked.Exchange(ref _cancelled, 1);
    }
    /// <summary>Closed intermediate worker queue; stock admission remains indivisible. No live publication or await under scope.</summary>
    internal sealed class WorkerOriginAdmissionQueue
    {
        internal const int MaximumJobs = 2;
        internal const long MaximumReservedLogicalBytes = 64L * 1024 * 1024;
        readonly int _mainThread = Thread.CurrentThread.ManagedThreadId;
        readonly List<WorkerAdmissionJob> _jobs = new List<WorkerAdmissionJob>();
        readonly AdmittedWorkerOrigin.Issuer _issuer = new AdmittedWorkerOrigin.Issuer();
        readonly WorkerAdmissionSlot _slot;
        readonly object _jobSeal = new object();
        internal WorkerOriginAdmissionQueue(WorkerAdmissionSlot slot) { _slot = slot ?? throw new ArgumentNullException(nameof(slot)); }
        long _reserved;
        internal int RetainedJobs { get { RequireMainThread(); return _jobs.Count; } }
        internal long ReservedLogicalBytes { get { RequireMainThread(); return _reserved; } }
        void RequireMainThread()
        { if (Thread.CurrentThread.ManagedThreadId != _mainThread) throw new InvalidOperationException("admission_wrong_thread"); }
        internal bool BeginOrigin(DomainBundle.WorkerInput input, ResourceAuthorityLease lease, WorkerAdmissionState expected,
            out WorkerAdmissionJob job, out string reason)
            => Begin(input, null, null, "", lease, expected, out job, out reason);
        internal bool BeginCandidate(AdmittedWorkerOrigin origin, DomainBundle.WorkerInput candidate, string receiptId,
            ResourceAuthorityLease lease, WorkerAdmissionState expected, out WorkerAdmissionJob job, out string reason)
        {
            RequireMainThread();
            if (!_issuer.Owns(origin) || !origin.Granted || !origin.Basis.Same(expected) || candidate == null || receiptId == null || receiptId.Length > 128 || string.IsNullOrWhiteSpace(receiptId))
            { job = null; reason = "invalid_candidate_request"; return false; }
            return Begin(null, candidate, origin, receiptId, lease, expected, out job, out reason);
        }
        bool Begin(DomainBundle.WorkerInput input, DomainBundle.WorkerInput candidate, AdmittedWorkerOrigin origin, string receipt,
            ResourceAuthorityLease lease, WorkerAdmissionState expected, out WorkerAdmissionJob job, out string reason)
        {
            RequireMainThread(); job = null; reason = "";
            if (lease == null || expected == null || !expected.Same(_slot.Capture()) || !expected.Epoch.Active || !lease.IsCurrent || input == null && candidate == null)
            { reason = "stale_admission_request"; return false; }
            if (origin != null && (!ReferenceEquals(origin.Epoch, expected.Epoch) || !ReferenceEquals(origin.Lease, lease)))
            { reason = "origin_binding_mismatch"; return false; }
            // Closed metadata known before scheduling. Four-times logical handle allowance is a reservation, not a heap/CPU bound.
            long bytes = checked((input?.LogicalRetainedBytes ?? 0) + (candidate?.LogicalRetainedBytes ?? 0) + (origin?.BundleWorkerLogicalBytes ?? 0));
            long reservation = checked(bytes * 4);
            if (_jobs.Count >= MaximumJobs || reservation > MaximumReservedLogicalBytes - _reserved)
            { reason = "admission_capacity"; return false; }
            var created = new WorkerAdmissionJob(this, _jobSeal, expected, lease, reservation, input, candidate, origin, receipt);
            _jobs.Add(created); _reserved += reservation;
            job = created; return true;
        }
        internal WorkerAdmissionOutcome Admit(WorkerAdmissionJob job, DomainBundle.WorkerInput input, DomainBundle.WorkerInput candidate,
            AdmittedWorkerOrigin origin, string receipt)
        {
            if (job == null || !job.HasSeal(_jobSeal)) return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Refused, "job_issuer_mismatch");
            if (job.Cancelled || !job.Expected.Epoch.Active) return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Cancelled, "admission_cancelled");
            if (!job.Lease.IsCurrent) return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Stale, "resource_changed");
            try
            {
                using (new PinnedAdmissionResourceScope(job.Lease))
                {
                    GdDict source = (input ?? candidate).Bundle.GetSummary(); // worker-private clone; never schedule caller-owned Gd graphs
                    if (!DomainBundle.TryCreate(source, out var admitted, out string reason))
                        return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Refused, reason);
                    var paid = PaidCraftingState.State(source);
                    if (PaidCraftingState.IsDomainVersion(admitted.SchemaVersion) &&
                        (paid.GetString("run_id") != job.Expected.Epoch.RunId || paid.GetString("actor_id") != job.Expected.Epoch.ActorId))
                        return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Refused, "epoch_owner_mismatch");
                    AdmittedWorkerOrigin issued = origin; AdmittedWorkerCandidate issuedCandidate = null;
                    if (origin == null)
                    {
                        issued = _issuer.Issue(admitted, job.Lease, job.Expected.Epoch, source, input.LogicalRetainedBytes, job.Expected);
                    }
                    else
                    {
                        if (!_issuer.Owns(origin) || !ReferenceEquals(origin.Bundle.HashContext, admitted.HashContext) || origin.FeatureSchema != admitted.FeatureSchema ||
                            origin.Bundle.SchemaVersion != admitted.SchemaVersion)
                            return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Refused, "candidate_lineage_mismatch");
                        GdDict before = origin.Bundle.GetSummary();
                        var effect = source.GetDictOrEmpty("receipts").GetDictOrEmpty(receipt).GetDictOrEmpty("result");
                        if (effect.GetString("operation") != "aux_complete" ||
                            !AuxiliaryServiceState.Conserved(before, admitted.GetSummary(), effect))
                            return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Refused, "candidate_conservation_failed");
                        issuedCandidate = new AdmittedWorkerCandidate(admitted, origin, receipt);
                    }
                    if (job.Cancelled || !job.Expected.Epoch.Active) return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Cancelled, "admission_cancelled");
                    if (!job.Lease.IsCurrent) return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Stale, "resource_changed");
                    return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Accepted, "admitted", issued, issuedCandidate);
                }
            }
            catch (UnsupportedWorkerPortException error) { return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Refused, error.Message); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("undeclared_resource", StringComparison.Ordinal))
            { return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Refused, error.Message); }
            catch (Exception error) { return new WorkerAdmissionOutcome(WorkerAdmissionStatus.Faulted, error.GetType().Name + ":" + error.Message); }
        }
        internal WorkerAdmissionStatus Poll(WorkerAdmissionJob job)
        {
            RequireMainThread();
            if (job == null || !_jobs.Contains(job)) return WorkerAdmissionStatus.Consumed;
            if (!job.IsCompleted) return WorkerAdmissionStatus.Pending; // no Result/GetResult on an incomplete task
            return job.CompletedOutcome().Status;
        }
        internal bool TryTake(WorkerAdmissionJob job, out WorkerAdmissionOutcome outcome)
        {
            RequireMainThread(); outcome = null;
            if (job == null || !_jobs.Contains(job) || !job.IsCompleted) return false;
            var completed = job.CompletedOutcome();
            using (CommonParticipantGate.BeginAttempt())
            {
                if (job.Consumed) return false;
                job.Consumed = true;
                if (job.Cancelled || !job.Expected.Epoch.Active) outcome = new WorkerAdmissionOutcome(WorkerAdmissionStatus.Cancelled, "admission_cancelled");
                else if (!job.Expected.Same(_slot.CurrentUnderGate) || !job.Lease.IsCurrent) outcome = new WorkerAdmissionOutcome(WorkerAdmissionStatus.Stale, "admission_inputs_changed");
                else
                {
                    outcome = completed;
                    if (completed.Status == WorkerAdmissionStatus.Accepted && completed.Origin != null)
                        _issuer.GrantUnderGate(completed.Origin);
                }
            }
            // Cancelled-but-running work retains its slot/reservation until this completed-only consumption.
            _jobs.Remove(job); _reserved -= job.Reservation; return true;
        }
    }
}
