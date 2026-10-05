using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public sealed partial class AuxiliaryWorkRuntime
    {
        PairedBinding _paired;
        object _pairIssuer;
        ulong _pairRevision;
        long _lastPairedFrame = -1;
        sealed class PairedBinding
        {
            internal readonly DiagnosticAuxiliaryStaminaCell Stamina;
            internal readonly DiagnosticAuxiliaryPairContext Context;
            internal PairedBinding(DiagnosticAuxiliaryStaminaCell stamina, DiagnosticAuxiliaryPairContext context)
            { Stamina = stamina; Context = context; }
        }
        // Fresh diagnostic birth only: never converts an exposed shadow runtime or authenticates scene facts.
        internal static AuxiliaryWorkRuntime CreateDiagnosticPaired(string owner, string run, string actor, string service,
            long revision, string algorithm, double required, double progress, double eligible, long steps, int capacity,
            AuxiliaryEvidenceLimits limits, DiagnosticAuxiliaryStaminaCell stamina, DiagnosticAuxiliaryPairContext context)
        {
            if (stamina == null || context == null) throw new ArgumentNullException();
            stamina.RequireThread(); context.RequireThread();
            var runtime = CreateWithEvidenceHandoff(owner, run, actor, service, revision, algorithm, required, progress,
                eligible, stamina.Value, steps, capacity, limits);
            runtime._paired = new PairedBinding(stamina, context); runtime._pairIssuer = new object(); return runtime;
        }
        internal bool TryPrepareDiagnosticPair(out PreparedPair plan, out AuxiliaryWorkStepResult result, out string reason)
        {
            plan = null; result = null; reason = "paired_mode_required";
            if (_paired == null) return false;
            _paired.Stamina.RequireThread(); _paired.Context.RequireThread();
            State before; AuxiliaryAcceptedStep[] buffer; int count; ulong revision, staminaStamp;
            double stamina; DiagnosticAuxiliaryPairContext.Capture context;
            lock (CommonParticipantGate.SyncRoot)
            {
                before = _state; buffer = _steps; count = _count; revision = _pairRevision;
                stamina = _paired.Stamina.Value; staminaStamp = _paired.Stamina.Stamp; context = _paired.Context.ReadUnderGate();
                if (!context.Resources.IsCurrent) { reason = "stale_resources"; return false; }
                if (context.Ordinal <= _lastPairedFrame) { reason = "frame_already_applied"; return false; }
                if (BitConverter.DoubleToInt64Bits(stamina) != BitConverter.DoubleToInt64Bits(context.Frame.Stamina))
                { reason = "stale_frame_stamina"; return false; }
            }
            // Exact existing gate precedence and IEEE arithmetic, isolated from live cells/evidence.
            // All scalar computation/allocation occurs before the final publication guard.
            var reference = new AuxiliaryWorkRuntime(OwnerId, RunId, ActorId, ServiceId, BaseRevision, HashAlgorithm,
                RequiredSeconds, before.Progress, before.Eligible, stamina, before.Sequence, 1);
            result = reference.Step(context.Frame);
            if (result.Status != AuxiliaryWorkStepStatus.Accepted) { reason = result.Reason; return false; }
            var next = new State(result.Snapshot.ProgressSeconds, result.Snapshot.EligibleSeconds,
                result.StaminaAfter, result.Snapshot.EligibleSteps);
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!ReferenceEquals(before, _state) || !ReferenceEquals(buffer, _steps) || count != _count || revision != _pairRevision ||
                    !_paired.Context.MatchesUnderGate(context) || !_paired.Stamina.MatchesUnderGate(staminaStamp, stamina))
                { reason = "stale_pair"; return false; }
                if (count == buffer.Length) { reason = "step_log_full"; return false; }
                if (!_evidenceJournal.CanAcceptStepUnderGate()) { reason = "complete_history_capacity"; return false; }
                if (revision > ulong.MaxValue - 2) { reason = "pair_stamp_overflow"; return false; }
                var debit = _paired.Stamina.PrepareUnderGate(result.StaminaAfter);
                var history = _evidenceJournal.PrepareAcceptedCountUnderGate();
                plan = PreparedPair.Create(_pairIssuer, this, _paired, before, next, buffer, count, revision, _lastPairedFrame,
                    context, debit, history, result); reason = ""; return true;
            }
        }
        internal DiagnosticPairSnapshot CaptureDiagnosticPair()
        {
            if (_paired == null) throw new InvalidOperationException("paired_mode_required");
            _paired.Stamina.RequireThread(); _paired.Context.RequireThread();
            lock (CommonParticipantGate.SyncRoot)
                return new DiagnosticPairSnapshot(View(_state), _paired.Stamina.Value, _paired.Stamina.Stamp,
                    _pairRevision, _lastPairedFrame, _count, _evidenceJournal.AcceptedStepCount);
        }
        internal sealed class DiagnosticPairSnapshot
        {
            internal readonly AuxiliaryWorkSnapshot Work;
            internal readonly double CurrentStamina;
            internal readonly ulong StaminaStamp, DirtyRevision;
            internal readonly long LastAppliedFrame;
            internal readonly int ActiveSteps, TotalAcceptedSteps;
            internal DiagnosticPairSnapshot(AuxiliaryWorkSnapshot work, double stamina, ulong staminaStamp,
                ulong revision, long frame, int active, int accepted)
            { Work = work; CurrentStamina = stamina; StaminaStamp = staminaStamp; DirtyRevision = revision;
                LastAppliedFrame = frame; ActiveSteps = active; TotalAcceptedSteps = accepted; }
        }
        internal enum PairFault { None, AfterStamina, AfterSlot, AfterCount, AfterState, AfterClock, AfterHistory }
        internal sealed class PreparedPair
        {
            readonly AuxiliaryWorkRuntime _runtime;
            readonly PairedBinding _binding;
            readonly State _before, _after;
            readonly AuxiliaryAcceptedStep[] _buffer;
            readonly int _count, _nextCount;
            readonly ulong _revision, _nextRevision, _rollbackRevision;
            readonly long _lastFrame;
            readonly DiagnosticAuxiliaryPairContext.Capture _context;
            readonly DiagnosticAuxiliaryStaminaCell.PreparedDebit _debit;
            readonly AuxiliaryEvidenceJournal.PreparedAcceptedCount _history;
            readonly Exception _fault = new InvalidOperationException("diagnostic_pair_fault");
            readonly AuxiliaryWorkStepResult _result;
            ParticipantPublicationAttempt _attempt;
            bool _used, _staminaInstalled, _slotInstalled, _countInstalled, _stateInstalled, _clockInstalled, _historyInstalled;
            // Only the containing runtime can construct a pair; no caller-supplied accepted evidence is admitted.
            private PreparedPair(AuxiliaryWorkRuntime runtime, PairedBinding binding, State before, State after,
                AuxiliaryAcceptedStep[] buffer, int count, ulong revision, long lastFrame,
                DiagnosticAuxiliaryPairContext.Capture context, DiagnosticAuxiliaryStaminaCell.PreparedDebit debit,
                AuxiliaryEvidenceJournal.PreparedAcceptedCount history, AuxiliaryWorkStepResult result)
            { _runtime = runtime; _binding = binding; _before = before; _after = after; _buffer = buffer; _count = count;
                _revision = revision; _nextRevision = checked(revision + 1); _rollbackRevision = checked(revision + 2); _nextCount = checked(count + 1); _lastFrame = lastFrame; _context = context; _debit = debit; _history = history; _result = result; }
            internal static PreparedPair Create(object issuer, AuxiliaryWorkRuntime runtime, object binding, object before, object after,
                AuxiliaryAcceptedStep[] buffer, int count, ulong revision, long lastFrame,
                DiagnosticAuxiliaryPairContext.Capture context, DiagnosticAuxiliaryStaminaCell.PreparedDebit debit,
                AuxiliaryEvidenceJournal.PreparedAcceptedCount history, AuxiliaryWorkStepResult result)
            {
                if (runtime == null || issuer == null || !ReferenceEquals(issuer, runtime._pairIssuer))
                    throw new InvalidOperationException("foreign_pair_issuer");
                return new PreparedPair(runtime, (PairedBinding)binding, (State)before, (State)after, buffer,
                    count, revision, lastFrame, context, debit, history, result);
            }
            internal AuxiliaryWorkStepResult Result => _result;
            internal bool TryInstallUnderGate(AuxiliaryWorkRuntime runtime, ParticipantPublicationAttempt attempt, PairFault fault = PairFault.None)
            {
                CommonParticipantGate.RequireHeld();
                _binding.Stamina.RequireThread(); _binding.Context.RequireThread();
                if (_used || attempt == null || !attempt.IsOpenUnderGate || !ReferenceEquals(runtime, _runtime)) return false;
                _used = true; // every authorized install attempt consumes the plan, including stale refusal
                if (!ReferenceEquals(_binding, runtime._paired) || !ReferenceEquals(_before, runtime._state) ||
                    !ReferenceEquals(_buffer, runtime._steps) || _count != runtime._count || _buffer[_count] != null ||
                    runtime._pairRevision != _revision || runtime._lastPairedFrame != _lastFrame || _context.Ordinal <= _lastFrame ||
                    !_binding.Context.MatchesUnderGate(_context) || !_debit.MatchesUnderGate() || !_history.MatchesUnderGate()) return false;
                _attempt = attempt;
                try
                {
                    // No callback, loader, floating arithmetic, allocation or whole-model application follows this guard.
                    _debit.InstallUnderGate(attempt); _staminaInstalled = true; Fail(fault, PairFault.AfterStamina);
                    _buffer[_count] = _result.AcceptedStep; _slotInstalled = true; Fail(fault, PairFault.AfterSlot);
                    runtime._count = _nextCount; _countInstalled = true; Fail(fault, PairFault.AfterCount);
                    runtime._state = _after; _stateInstalled = true; Fail(fault, PairFault.AfterState);
                    runtime._pairRevision = _nextRevision; runtime._lastPairedFrame = _context.Ordinal; _clockInstalled = true; Fail(fault, PairFault.AfterClock);
                    _history.InstallUnderGate(attempt); _historyInstalled = true; Fail(fault, PairFault.AfterHistory);
                    return true;
                }
                catch
                { RollbackUnderGate(attempt); throw; }
            }
            void Fail(PairFault actual, PairFault selected) { if (actual == selected) throw _fault; }
            // Immediate attempted assignments only; token/thread/gate/current backing guards prohibit delayed undo.
            internal void RollbackUnderGate(ParticipantPublicationAttempt attempt)
            {
                CommonParticipantGate.RequireHeld();
                if (attempt == null || !attempt.IsOpenUnderGate || !ReferenceEquals(attempt, _attempt) || !_staminaInstalled ||
                    !ReferenceEquals(_runtime._paired, _binding) ||
                    !ReferenceEquals(_runtime._state, _stateInstalled ? _after : _before) || !ReferenceEquals(_runtime._steps, _buffer) ||
                    _runtime._count != (_countInstalled ? _nextCount : _count) ||
                    _runtime._pairRevision != (_clockInstalled ? _nextRevision : _revision) ||
                    _runtime._lastPairedFrame != (_clockInstalled ? _context.Ordinal : _lastFrame) ||
                    !ReferenceEquals(_buffer[_count], _slotInstalled ? _result.AcceptedStep : null) ||
                    !_binding.Context.MatchesUnderGate(_context) || !_debit.CanRollbackUnderGate(attempt) ||
                    (_historyInstalled ? !_history.CanRollbackUnderGate(attempt) : !_history.MatchesUnderGate()))
                    throw new InvalidOperationException("invalid_pair_rollback");
                // Every participant has passed rollback preflight before the first assignment.
                // Subsequent typed checks see unchanged participant fields under this same gate.
                if (_historyInstalled) _history.RollbackUnderGate(attempt);
                if (_clockInstalled) _runtime._lastPairedFrame = _lastFrame;
                if (_stateInstalled) _runtime._state = _before;
                if (_countInstalled) _runtime._count = _count;
                if (_slotInstalled) _buffer[_count] = null;
                _runtime._pairRevision = _rollbackRevision;
                _debit.RollbackUnderGate(attempt);
                _staminaInstalled = false;
            }
        }
    }
}
