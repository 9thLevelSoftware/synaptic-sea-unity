using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public sealed partial class AuxiliaryWorkRuntime
    {
        readonly AuxiliaryEvidenceJournal _evidenceJournal;
        readonly object _evidenceEpoch;
        AuxiliarySealedEvidenceChunk _pendingEvidence;
        AuxiliaryWorkRuntime(string ownerId, string runId, string actorId, string serviceId,
            long baseRevision, string algorithm, double required, double progress, double eligible,
            double stamina, long steps, int capacity, AuxiliaryEvidenceLimits limits)
            : this(ownerId, runId, actorId, serviceId, baseRevision, algorithm, required, progress, eligible, stamina, steps, capacity)
        {
            if (limits == null || ownerId.Length > 256 || runId.Length > 256 || actorId.Length > 256 || serviceId.Length > 256)
                throw new ArgumentException("invalid_handoff_binding");
            _sync = CommonParticipantGate.SyncRoot; // selected before this diagnostic instance is exposed
            _evidenceEpoch = new object();
            _evidenceJournal = new AuxiliaryEvidenceJournal(this, _evidenceEpoch, limits, progress, eligible, steps);
        }
        // Diagnostic opt-in only. Typed identifiers/algorithm are transport labels, not admitted origin authority.
        internal static AuxiliaryWorkRuntime CreateWithEvidenceHandoff(string ownerId, string runId, string actorId, string serviceId,
            long baseRevision, string algorithm, double required, double progress, double eligible, double stamina,
            long steps, int capacity, AuxiliaryEvidenceLimits limits)
            => new AuxiliaryWorkRuntime(ownerId, runId, actorId, serviceId, baseRevision, algorithm,
                required, progress, eligible, stamina, steps, capacity, limits);
        internal AuxiliaryEvidenceJournal EvidenceJournal => _evidenceJournal;
        internal bool IsEvidenceIssuer(object epoch) => _evidenceJournal != null && ReferenceEquals(epoch, _evidenceEpoch);
        internal bool IsPendingEvidenceUnderGate(AuxiliarySealedEvidenceChunk chunk) => ReferenceEquals(_pendingEvidence, chunk);
        internal AuxiliarySealedEvidenceChunk PendingEvidence
        { get { lock (_sync) return _pendingEvidence; } }
        internal bool TryPrepareEvidenceRotation(out PreparedEvidenceRotation rotation, out string reason)
        {
            rotation = null; reason = "handoff_disabled"; if (_evidenceJournal == null) return false;
            lock (_sync)
            {
                if (_pendingEvidence != null) { reason = "custody_pending"; return false; }
                if (_count == 0) { reason = "no_steps"; return false; }
                // All allocation occurs before rotation. Failure leaves active buffer/progress/cost/evidence intact.
                var next = new AuxiliaryAcceptedStep[_capacity];
                // The captured prefix is stable because accepted slots are append-only. Keep its wrapper
                // private in the plan: no worker/queue exposure before successful rotation and custody.
                var chunk = new AuxiliarySealedEvidenceChunk(this, _evidenceEpoch,
                    _evidenceJournal.NextOrdinalUnderGate, _steps, _count);
                rotation = new PreparedEvidenceRotation(this, _state, _steps, _count, next, chunk);
                reason = ""; return true;
            }
        }
        internal bool TryInstallEvidenceRotation(PreparedEvidenceRotation rotation)
        {
            if (_evidenceJournal == null || rotation == null) return false;
            lock (_sync)
            {
                return rotation.TryInstallUnderGate(this);
            }
        }
        internal bool TryAcknowledgeEvidenceCustody(AuxiliaryEvidenceCustodyAck ack)
        {
            if (_evidenceJournal == null) return false;
            lock (_sync)
            {
                if (_pendingEvidence == null || !_evidenceJournal.HasCustodyUnderGate(ack, _pendingEvidence)) return false;
                ack.ConsumeUnderGate(); _pendingEvidence = null; return true;
            }
        }
        internal sealed class PreparedEvidenceRotation
        {
            internal readonly AuxiliaryWorkRuntime Producer;
            readonly object _expectedState;
            readonly AuxiliaryAcceptedStep[] _expectedBuffer, _nextBuffer;
            readonly int _expectedCount;
            readonly AuxiliarySealedEvidenceChunk _chunk;
            bool _consumed;
            internal PreparedEvidenceRotation(AuxiliaryWorkRuntime producer, object state, AuxiliaryAcceptedStep[] before,
                int count, AuxiliaryAcceptedStep[] next, AuxiliarySealedEvidenceChunk chunk)
            { Producer = producer; _expectedState = state; _expectedBuffer = before; _expectedCount = count; _nextBuffer = next; _chunk = chunk; }
            internal bool TryInstallUnderGate(AuxiliaryWorkRuntime producer)
            {
                CommonParticipantGate.RequireHeld();
                if (!ReferenceEquals(Producer, producer) || _consumed || producer._pendingEvidence != null ||
                    !ReferenceEquals(_expectedState, producer._state) || !ReferenceEquals(_expectedBuffer, producer._steps) ||
                    _expectedCount != producer._count || _chunk.Ordinal != producer._evidenceJournal.NextOrdinalUnderGate)
                    return false;
                // Private buffers never escape the token. No callbacks/copy/hash/allocations after this guard.
                producer._steps = _nextBuffer; producer._count = 0; producer._pendingEvidence = _chunk;
                producer._evidenceJournal.RecordSealUnderGate(); _consumed = true; return true;
            }
        }
    }
}
