using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    // UNWIRED raw evidence custody. No admitted origin, digest, certificate, durable ACK or live vitals.
    internal sealed class AuxiliaryEvidenceLimits
    {
        internal readonly int MaximumChunks, MaximumSteps, QueueCapacity;
        internal AuxiliaryEvidenceLimits(int maximumChunks, int maximumSteps, int queueCapacity)
        {
            if (maximumChunks < 1 || maximumChunks > 256 || maximumSteps < 1 || maximumSteps > 65536 ||
                queueCapacity < 1 || queueCapacity > maximumChunks) throw new ArgumentException("evidence_limits");
            MaximumChunks = maximumChunks; MaximumSteps = maximumSteps; QueueCapacity = queueCapacity;
        }
        // Conservative replay-DTO evidence envelope only. Authenticated origin/resource closure and actual
        // complete save/candidate byte accounting remain separate live-activation prerequisites.
        internal bool Fits(int chunks, int steps)
        {
            long units = 1L + chunks + steps;
            return chunks <= MaximumChunks && steps <= MaximumSteps &&
                units * 256 <= AuxEvidenceReplayCursor.MaximumNodes &&
                8192 + (units - 1) * 4096 <= AuxEvidenceReplayCursor.MaximumBytes;
        }
    }
    internal sealed class AuxiliarySealedEvidenceChunk
    {
        readonly AuxiliaryAcceptedStep[] _steps;
        readonly object _epoch;
        internal readonly int Ordinal;
        internal readonly int Count;
        internal readonly long FirstSequence, LastSequence;
        internal readonly double ProgressBefore, ProgressAfter, EligibleBefore, EligibleAfter;
        internal bool IsAdmitted => false;
        internal bool IsDurable => false;
        internal readonly string OwnerId, RunId, ActorId, ServiceId, Algorithm;
        internal readonly long BaseRevision;
        internal bool MatchesEpoch(object epoch) => ReferenceEquals(epoch, _epoch);
        internal AuxiliarySealedEvidenceChunk(AuxiliaryWorkRuntime producer, object epoch, int ordinal,
            AuxiliaryAcceptedStep[] ownedSteps, int count)
        {
            if (producer == null || !producer.IsEvidenceIssuer(epoch) || ownedSteps == null || count < 1 ||
                count > ownedSteps.Length || count > AuxiliaryWorkRuntime.MaximumRetainedSteps)
                throw new ArgumentException("invalid_chunk_source");
            _epoch = epoch; Ordinal = ordinal; _steps = ownedSteps; Count = count;
            OwnerId = producer.OwnerId; RunId = producer.RunId; ActorId = producer.ActorId;
            ServiceId = producer.ServiceId; Algorithm = producer.HashAlgorithm; BaseRevision = producer.BaseRevision;
            FirstSequence = ownedSteps[0].Sequence; LastSequence = ownedSteps[count - 1].Sequence;
            ProgressBefore = ownedSteps[0].ProgressBefore; ProgressAfter = ownedSteps[count - 1].ProgressAfter;
            EligibleBefore = ownedSteps[0].EligibleBefore; EligibleAfter = ownedSteps[count - 1].EligibleAfter;
        }
        internal AuxiliaryAcceptedStep At(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _steps[index];
        }
    }
    internal enum AuxiliaryCustodyStatus { Accepted, AlreadyInCustody, QueueFull, Refused }
    internal sealed class AuxiliaryEvidenceCustodyAck
    {
        internal readonly AuxiliaryEvidenceJournal Journal;
        internal readonly AuxiliarySealedEvidenceChunk Chunk;
        readonly object _issuer;
        bool _used;
        internal AuxiliaryEvidenceCustodyAck(AuxiliaryEvidenceJournal journal, object issuer, AuxiliarySealedEvidenceChunk chunk)
        { Journal = journal; _issuer = issuer; Chunk = chunk; }
        internal bool CanUseUnderGate(AuxiliaryEvidenceJournal journal, AuxiliarySealedEvidenceChunk chunk)
            => !_used && ReferenceEquals(Journal, journal) && journal.IsAckIssuer(_issuer) && ReferenceEquals(Chunk, chunk);
        internal void ConsumeUnderGate() { _used = true; }
        internal bool IsAdmissionAck => false;
        internal bool IsDurableAck => false;
    }
    internal sealed partial class AuxiliaryEvidenceJournal
    {
        readonly AuxiliaryWorkRuntime _producer;
        readonly object _epoch, _ackIssuer = new object();
        readonly AuxiliaryEvidenceLimits _limits;
        readonly AuxiliarySealedEvidenceChunk[] _history;
        readonly AuxiliaryEvidenceCustodyAck[] _acks;
        int _historyCount, _nextQueued, _acceptedCount, _sealedCount;
        long _lastCustodySequence;
        double _custodyProgress, _custodyEligible;
        internal AuxiliaryEvidenceJournal(AuxiliaryWorkRuntime producer, object epoch, AuxiliaryEvidenceLimits limits,
            double progress, double eligible, long acceptedSteps)
        {
            _producer = producer; _epoch = epoch; _limits = limits;
            _history = new AuxiliarySealedEvidenceChunk[limits.MaximumChunks];
            _acks = new AuxiliaryEvidenceCustodyAck[limits.MaximumChunks];
            _custodyProgress = progress; _custodyEligible = eligible; _lastCustodySequence = acceptedSteps;
        }
        internal bool IsAckIssuer(object issuer) => ReferenceEquals(issuer, _ackIssuer);
        internal int AcceptedStepCount { get { lock (CommonParticipantGate.SyncRoot) return _acceptedCount; } }
        internal int RetainedChunkCount { get { lock (CommonParticipantGate.SyncRoot) return _historyCount; } }
        internal int QueuedChunkCount { get { lock (CommonParticipantGate.SyncRoot) return _historyCount - _nextQueued; } }
        internal int NextOrdinalUnderGate => _sealedCount;
        internal bool CanAcceptStepUnderGate() => _limits.Fits(_sealedCount + 1, _acceptedCount + 1);
        internal void RecordAcceptedStepUnderGate() { _acceptedCount++; }
        internal void RecordSealUnderGate() { _sealedCount++; }
        internal AuxiliaryCustodyStatus TryTakeCustody(AuxiliarySealedEvidenceChunk chunk, out AuxiliaryEvidenceCustodyAck ack)
        {
            ack = null;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (chunk == null || !chunk.MatchesEpoch(_epoch))
                    return AuxiliaryCustodyStatus.Refused;
                if (chunk.Ordinal >= 0 && chunk.Ordinal < _historyCount && ReferenceEquals(_history[chunk.Ordinal], chunk))
                { ack = _acks[chunk.Ordinal]; return AuxiliaryCustodyStatus.AlreadyInCustody; }
                if (chunk.Ordinal != _historyCount || !_producer.IsPendingEvidenceUnderGate(chunk) ||
                    _historyCount == _history.Length || _lastCustodySequence == long.MaxValue ||
                    chunk.FirstSequence != _lastCustodySequence + 1 || chunk.LastSequence - chunk.FirstSequence + 1 != chunk.Count ||
                    !Bits(chunk.ProgressBefore, _custodyProgress) || !Bits(chunk.EligibleBefore, _custodyEligible))
                    return AuxiliaryCustodyStatus.Refused;
                if (_historyCount - _nextQueued == _limits.QueueCapacity) return AuxiliaryCustodyStatus.QueueFull;
                // Prebuild issuer token before the nonthrowing custody assignments.
                ack = new AuxiliaryEvidenceCustodyAck(this, _ackIssuer, chunk);
                _history[_historyCount] = chunk; _acks[_historyCount] = ack; _historyCount++;
                _lastCustodySequence = chunk.LastSequence; _custodyProgress = chunk.ProgressAfter; _custodyEligible = chunk.EligibleAfter;
                return AuxiliaryCustodyStatus.Accepted;
            }
        }
        internal bool HasCustodyUnderGate(AuxiliaryEvidenceCustodyAck ack, AuxiliarySealedEvidenceChunk chunk)
            => ack != null && ack.CanUseUnderGate(this, chunk) && chunk.Ordinal >= 0 && chunk.Ordinal < _historyCount &&
                ReferenceEquals(_history[chunk.Ordinal], chunk) && ReferenceEquals(_acks[chunk.Ordinal], ack);
        // Dequeue hands an immutable reference to the computation queue, NOT deletion or validation acceptance.
        // Cancellation/fault cannot remove history. There is intentionally no archive/durable-release API.
        internal bool TryAcquireQueuedChunk(out AuxiliarySealedEvidenceChunk chunk)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                chunk = null; if (_nextQueued == _historyCount) return false;
                chunk = _history[_nextQueued++]; return true;
            }
        }
        internal AuxiliarySealedEvidenceChunk ReadRetainedChunk(int ordinal)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                if (ordinal < 0 || ordinal >= _historyCount) throw new ArgumentOutOfRangeException(nameof(ordinal));
                return _history[ordinal];
            }
        }
        static bool Bits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    }
}
