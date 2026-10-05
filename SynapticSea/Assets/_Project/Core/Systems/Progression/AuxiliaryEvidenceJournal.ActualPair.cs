using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    internal sealed partial class AuxiliaryEvidenceJournal
    {
        internal bool ActualPairCapacityUnderGate(AuxReplayBudgetReport next, int expectedAccepted, int expectedSealed)
        {
            CommonParticipantGate.RequireHeld();
            return _acceptedCount == expectedAccepted && _sealedCount == expectedSealed &&
                next.Steps == (long)expectedAccepted + 1 && next.Chunks == (long)expectedSealed + 1 &&
                AuxReplayCodecBudget.Fits(next, _limits.MaximumChunks, _limits.MaximumSteps);
        }
        internal PreparedActualCount PrepareActualCountUnderGate(object runtimeIssuer, AuxReplayBudgetReport report, int before, int sealedCount)
        {
            CommonParticipantGate.RequireHeld();
            if (!_producer.IsActualPairIssuer(runtimeIssuer) || !ActualPairCapacityUnderGate(report, before, sealedCount))
                throw new InvalidOperationException("foreign_or_stale_actual_count");
            return new PreparedActualCount(this, _ackIssuer, runtimeIssuer, report, before, sealedCount);
        }
        internal sealed class PreparedActualCount
        {
            readonly AuxiliaryEvidenceJournal _journal;
            readonly object _runtimeIssuer;
            readonly AuxReplayBudgetReport _report;
            readonly int _before, _next, _sealed;
            bool _used;
            internal PreparedActualCount(AuxiliaryEvidenceJournal journal, object privateIssuer, object runtimeIssuer,
                AuxReplayBudgetReport report, int before, int sealedCount)
            {
                if (journal == null || !journal.IsAckIssuer(privateIssuer)) throw new InvalidOperationException("foreign_actual_count_issuer");
                _journal = journal; _runtimeIssuer = runtimeIssuer; _report = report;
                _before = before; _next = checked(before + 1); _sealed = sealedCount;
            }
            internal bool MatchesUnderGate()
            {
                CommonParticipantGate.RequireHeld();
                return !_used && _journal._producer.IsActualPairIssuer(_runtimeIssuer) &&
                    _journal.ActualPairCapacityUnderGate(_report, _before, _sealed);
            }
            // Opaque capability retained privately in the pair. Its exact guard preceded adapter publication.
            // Assignment-only suffix: no callback, allocation, arithmetic, guard or failure path.
            internal void AssignPrevalidatedNoFail() { _journal._acceptedCount = _next; _used = true; }
        }
    }
}
