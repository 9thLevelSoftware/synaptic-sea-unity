using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    internal sealed partial class AuxiliaryEvidenceJournal
    {
        internal PreparedAcceptedCount PrepareAcceptedCountUnderGate()
        {
            CommonParticipantGate.RequireHeld();
            if (_acceptedCount == int.MaxValue || !CanAcceptStepUnderGate()) throw new InvalidOperationException("complete_history_capacity");
            return new PreparedAcceptedCount(this, _acceptedCount, _sealedCount);
        }
        internal sealed class PreparedAcceptedCount
        {
            readonly AuxiliaryEvidenceJournal _journal;
            readonly int _before, _after, _sealed;
            ParticipantPublicationAttempt _attempt;
            int _state;
            internal PreparedAcceptedCount(AuxiliaryEvidenceJournal journal, int before, int sealedCount)
            { _journal = journal; _before = before; _after = checked(before + 1); _sealed = sealedCount; }
            internal bool MatchesUnderGate()
            {
                CommonParticipantGate.RequireHeld();
                return _state == 0 && _journal._acceptedCount == _before && _journal._sealedCount == _sealed && _journal.CanAcceptStepUnderGate();
            }
            internal void InstallUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (attempt == null || !attempt.IsOpenUnderGate || !MatchesUnderGate()) throw new InvalidOperationException("stale_history_slot");
                _attempt = attempt; _journal._acceptedCount = _after; _state = 1; }
            internal bool CanRollbackUnderGate(ParticipantPublicationAttempt attempt)
            {
                CommonParticipantGate.RequireHeld();
                return _state == 1 && attempt != null && attempt.IsOpenUnderGate && ReferenceEquals(attempt, _attempt) &&
                    _journal._acceptedCount == _after && _journal._sealedCount == _sealed;
            }
            internal void RollbackUnderGate(ParticipantPublicationAttempt attempt)
            {
                if (!CanRollbackUnderGate(attempt)) throw new InvalidOperationException("invalid_history_rollback");
                _journal._acceptedCount = _before; _state = 2;
            }
        }
    }
}
