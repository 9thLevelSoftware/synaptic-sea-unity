using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    internal sealed partial class AuxiliaryEvidenceJournal
    {
        ulong _structuralVersion;
        internal ulong StructuralVersion { get { lock (CommonParticipantGate.SyncRoot) return _structuralVersion; } }
        internal bool CanPrepareRequestCutUnderGate(ulong expected)
        {
            CommonParticipantGate.RequireHeld();
            return expected == _structuralVersion && _structuralVersion < ulong.MaxValue &&
                _historyCount == _sealedCount && _historyCount < _history.Length &&
                _historyCount - _nextQueued < _limits.QueueCapacity;
        }
        internal PreparedRequestCustody PrepareRequestCustodyUnderGate(object runtimeIssuer, AuxiliarySealedEvidenceChunk chunk, ulong expected)
        {
            CommonParticipantGate.RequireHeld();
            if (!_producer.IsEvidenceIssuer(runtimeIssuer) || !CanPrepareRequestCutUnderGate(expected) || chunk == null || !chunk.MatchesEpoch(_epoch) ||
                chunk.Ordinal != _historyCount || _lastCustodySequence == long.MaxValue ||
                chunk.FirstSequence != _lastCustodySequence + 1 || chunk.LastSequence - chunk.FirstSequence + 1 != chunk.Count ||
                !Bits(chunk.ProgressBefore, _custodyProgress) || !Bits(chunk.EligibleBefore, _custodyEligible))
                throw new InvalidOperationException("request_cut_custody_refused");
            return new PreparedRequestCustody(this, _ackIssuer, chunk, expected, _historyCount, _nextQueued, _acceptedCount);
        }
        internal sealed class PreparedRequestCustody
        {
            readonly AuxiliaryEvidenceJournal _journal;
            readonly AuxiliarySealedEvidenceChunk _chunk;
            readonly AuxiliaryEvidenceCustodyAck _ack;
            readonly ulong _beforeVersion, _nextVersion;
            readonly int _beforeCount, _nextCount, _queued, _accepted;
            bool _used;
            internal PreparedRequestCustody(AuxiliaryEvidenceJournal journal, object issuer,
                AuxiliarySealedEvidenceChunk chunk, ulong version, int count, int queued, int accepted)
            {
                if (!journal.IsAckIssuer(issuer)) throw new InvalidOperationException("foreign_request_cut");
                _journal = journal; _chunk = chunk; _beforeVersion = version; _nextVersion = checked(version + 1);
                _beforeCount = count; _nextCount = checked(count + 1); _queued = queued; _accepted = accepted;
                _ack = new AuxiliaryEvidenceCustodyAck(journal, issuer, chunk);
            }
            internal bool MatchesUnderGate()
            {
                CommonParticipantGate.RequireHeld();
                return !_used && _journal.CanPrepareRequestCutUnderGate(_beforeVersion) &&
                    _journal._historyCount == _beforeCount && _journal._nextQueued == _queued &&
                    _journal._acceptedCount == _accepted && _chunk.Ordinal == _beforeCount;
            }
            // Only retained by the runtime-issued cut. Full guard must precede first assignment.
            internal void InstallPrevalidatedNoFail()
            {
                _journal._history[_beforeCount] = _chunk; _journal._acks[_beforeCount] = _ack;
                _journal._historyCount = _nextCount; _journal._sealedCount = _nextCount;
                _journal._lastCustodySequence = _chunk.LastSequence;
                _journal._custodyProgress = _chunk.ProgressAfter; _journal._custodyEligible = _chunk.EligibleAfter;
                _journal._structuralVersion = _nextVersion; _used = true;
            }
        }
    }
}
