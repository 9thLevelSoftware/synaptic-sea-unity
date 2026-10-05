using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public sealed partial class AuxiliaryWorkRuntime
    {
        // Transport-only prerequisite. Joining this cut with an authenticated world cohort is NOT implemented here.
        internal bool TryPrepareEvidenceRequestCut(ulong expectedStructuralVersion, out PreparedEvidenceRequestCut cut, out string reason)
        {
            cut = null; reason = "handoff_disabled";
            if (_evidenceJournal == null) return false;
            lock (_sync)
            {
                if (_evidenceJournal.StructuralVersion != expectedStructuralVersion) { reason = "stale_structure"; return false; }
                if (_pendingEvidence != null) { reason = "custody_pending"; return false; }
                if (_count == 0)
                { cut = new PreparedEvidenceRequestCut(this, _state, _steps, 0, expectedStructuralVersion, null, null, null); reason = ""; return true; }
                if (!_evidenceJournal.CanPrepareRequestCutUnderGate(expectedStructuralVersion)) { reason = "custody_capacity"; return false; }
                // Every allocation precedes rotation. The append-only captured prefix remains private until installation.
                var next = new AuxiliaryAcceptedStep[_capacity];
                var chunk = new AuxiliarySealedEvidenceChunk(this, _evidenceEpoch, _evidenceJournal.NextOrdinalUnderGate, _steps, _count);
                var custody = _evidenceJournal.PrepareRequestCustodyUnderGate(_evidenceEpoch, chunk, expectedStructuralVersion);
                cut = new PreparedEvidenceRequestCut(this, _state, _steps, _count, expectedStructuralVersion, next, chunk, custody);
                reason = ""; return true;
            }
        }
        internal sealed class PreparedEvidenceRequestCut
        {
            readonly AuxiliaryWorkRuntime _runtime;
            readonly object _state;
            readonly AuxiliaryAcceptedStep[] _buffer, _next;
            readonly int _count;
            readonly ulong _version;
            readonly AuxiliarySealedEvidenceChunk _chunk;
            readonly AuxiliaryEvidenceJournal.PreparedRequestCustody _custody;
            bool _used;
            internal PreparedEvidenceRequestCut(AuxiliaryWorkRuntime runtime, object state, AuxiliaryAcceptedStep[] buffer,
                int count, ulong version, AuxiliaryAcceptedStep[] next, AuxiliarySealedEvidenceChunk chunk,
                AuxiliaryEvidenceJournal.PreparedRequestCustody custody)
            { _runtime = runtime; _state = state; _buffer = buffer; _count = count; _version = version; _next = next; _chunk = chunk; _custody = custody; }
            internal bool MatchesUnderGate()
            {
                CommonParticipantGate.RequireHeld();
                return !_used && ReferenceEquals(_runtime._state, _state) && ReferenceEquals(_runtime._steps, _buffer) &&
                    _runtime._count == _count && _runtime._pendingEvidence == null &&
                    _runtime._evidenceJournal.StructuralVersion == _version && (_custody == null || _custody.MatchesUnderGate());
            }
            internal bool TryInstallTransportUnderGate(out AuxiliarySealedEvidenceChunk sealedTail)
            {
                CommonParticipantGate.RequireHeld(); sealedTail = null;
                if (!MatchesUnderGate()) return false;
                if (_custody != null)
                {
                    _custody.InstallPrevalidatedNoFail();
                    _runtime._steps = _next; _runtime._count = 0;
                }
                _used = true; sealedTail = _chunk; return true;
            }
        }
    }
}
