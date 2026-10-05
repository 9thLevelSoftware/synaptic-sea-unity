using System;
using System.Threading;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public sealed partial class AuxiliaryWorkRuntime
    {
        ActualPairBinding _actualPair;
        sealed class ActualBudget
        {
            internal readonly AuxReplayBudgetReport Report;
            internal ActualBudget(AuxReplayBudgetReport report) { Report = report; }
        }
        sealed class ActualPairBinding
        {
            internal readonly object Issuer = new object();
            internal readonly int ThreadId = Thread.CurrentThread.ManagedThreadId;
            internal readonly DiagnosticVitalsProjectionAdapter Vitals;
            internal readonly DiagnosticAuxiliaryPairContext Context;
            internal ActualBudget Budget;
            internal ActualPairBinding(DiagnosticVitalsProjectionAdapter vitals, DiagnosticAuxiliaryPairContext context, AuxReplayBudgetReport report)
            { Vitals = vitals; Context = context; Budget = new ActualBudget(report); }
            internal void RequireThread()
            { if (ThreadId != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("actual_pair_wrong_thread"); }
        }
        internal static AuxiliaryWorkRuntime CreateActualVitalsPaired(string owner, long revision, string algorithm,
            UntrustedAuxReplaySeed seed, int capacity, AuxiliaryEvidenceLimits limits,
            DiagnosticVitalsProjectionAdapter vitals, DiagnosticAuxiliaryPairContext context)
        {
            if (seed == null || vitals == null || context == null || !AuxReplayCodecBudget.TrySeed(seed, out var budget))
                throw new ArgumentException("invalid_actual_pair_birth");
            context.RequireThread(); var current = vitals.Read();
            var runtime = CreateWithEvidenceHandoff(owner, seed.Run, seed.Actor, seed.Service, revision, algorithm,
                seed.Duration, seed.Progress, seed.Eligible, current.Values.Stamina, seed.AcceptedSteps, capacity, limits);
            runtime._actualPair = new ActualPairBinding(vitals, context, budget); return runtime;
        }
        internal bool IsActualPairIssuer(object issuer) => (_actualPair != null && ReferenceEquals(issuer, _actualPair.Issuer)) || IsRunSessionPairIssuer(issuer);
        internal bool TryPrepareActualPair(out PreparedActualPair plan, out AuxiliaryWorkStepResult result, out string reason)
        {
            plan = null; result = null; reason = "actual_pair_required";
            if (_actualPair == null) return false;
            _actualPair.RequireThread(); _actualPair.Context.RequireThread();
            State before; AuxiliaryAcceptedStep[] buffer; int count, accepted, sealedCount; ulong revision;
            long lastFrame; ActualBudget budget; DiagnosticVitalsSnapshot vitals; DiagnosticAuxiliaryPairContext.Capture context;
            lock (CommonParticipantGate.SyncRoot)
            {
                before = _state; buffer = _steps; count = _count; revision = _pairRevision; lastFrame = _lastPairedFrame;
                accepted = _evidenceJournal.AcceptedStepCount; sealedCount = _evidenceJournal.NextOrdinalUnderGate;
                budget = _actualPair.Budget; vitals = _actualPair.Vitals.Read(); context = _actualPair.Context.ReadUnderGate();
                if (!context.Resources.IsCurrent) { reason = "stale_resources"; return false; }
                if (context.Ordinal <= lastFrame) { reason = "frame_already_applied"; return false; }
                if (!BitsEqual(context.Frame.Stamina, vitals.Values.Stamina) || !BitsEqual(context.Frame.MaxStamina, vitals.Values.MaxStamina))
                { reason = "stale_frame_vitals"; return false; }
            }
            // Exact ordinary arithmetic outside the final publication guard. Current actual stamina, never historical stamina.
            var arithmetic = new AuxiliaryWorkRuntime(OwnerId, RunId, ActorId, ServiceId, BaseRevision, HashAlgorithm,
                RequiredSeconds, before.Progress, before.Eligible, vitals.Values.Stamina, before.Sequence, 1);
            var frame = context.Frame;
            if (vitals.Values.Health <= 0)
                frame = new AuxiliaryWorkFrame(frame.Delta, frame.Stamina, frame.MaxStamina, frame.WoundSpeed,
                    "dead_actual_vitals", frame.Consent, frame.HoldRequired, frame.Held, frame.Moving, frame.Damaged);
            result = arithmetic.Step(frame);
            if (result.Status != AuxiliaryWorkStepStatus.Accepted) { reason = result.Reason; return false; }
            if (count == buffer.Length) { reason = "step_log_full"; return false; }
            if (revision > ulong.MaxValue - 1 || accepted == int.MaxValue) { reason = "actual_pair_stamp_overflow"; return false; }
            var nextReport = budget.Report;
            if (count == 0 && !AuxReplayCodecBudget.TryAppendReservedHeader(nextReport, sealedCount, result.AcceptedStep.Sequence, before.Sequence, out nextReport))
            { reason = "complete_history_capacity"; return false; }
            if (!AuxReplayCodecBudget.TryAppendStep(nextReport, result.AcceptedStep.Sequence, count, out nextReport))
            { reason = "complete_history_capacity"; return false; }
            var nextBudget = new ActualBudget(nextReport);
            var nextState = new State(result.Snapshot.ProgressSeconds, result.Snapshot.EligibleSeconds, result.StaminaAfter, result.Snapshot.EligibleSteps);
            AuxiliaryEvidenceJournal.PreparedActualCount history;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!ActualPrepareMatches(before, buffer, count, revision, lastFrame, budget, vitals, context))
                { reason = "stale_actual_pair"; return false; }
                if (!_evidenceJournal.ActualPairCapacityUnderGate(nextReport, accepted, sealedCount))
                { reason = "complete_history_capacity"; return false; }
                history = _evidenceJournal.PrepareActualCountUnderGate(_actualPair.Issuer, nextReport, accepted, sealedCount);
            }
            if (!_actualPair.Vitals.PrepareStaminaAfter(vitals, result.StaminaAfter, out var debit, out reason)) return false;
            plan = PreparedActualPair.Create(_actualPair.Issuer, this, before, nextState, buffer, count, accepted,
                sealedCount, revision, lastFrame, budget, nextBudget, vitals, context, debit, history, result);
            reason = ""; return true;
        }
        static bool BitsEqual(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
        bool ActualPrepareMatches(State before, AuxiliaryAcceptedStep[] buffer, int count, ulong revision, long lastFrame,
            ActualBudget budget, DiagnosticVitalsSnapshot vitals, DiagnosticAuxiliaryPairContext.Capture context)
        {
            CommonParticipantGate.RequireHeld();
            return ReferenceEquals(_state, before) && ReferenceEquals(_steps, buffer) && _count == count &&
                _pairRevision == revision && _lastPairedFrame == lastFrame && ReferenceEquals(_actualPair.Budget, budget) &&
                vitals.MatchesActualUnderGate(_actualPair.Vitals) && _actualPair.Context.MatchesUnderGate(context);
        }
        internal ActualPairSnapshot CaptureActualPair()
        {
            if (_actualPair == null) throw new InvalidOperationException("actual_pair_required"); _actualPair.RequireThread();
            lock (CommonParticipantGate.SyncRoot)
                return new ActualPairSnapshot(_actualPair.Vitals.Read(), View(_state), _actualPair.Budget.Report,
                    _pairRevision, _lastPairedFrame, _count, _evidenceJournal.AcceptedStepCount);
        }
        internal sealed class ActualPairSnapshot
        {
            internal readonly DiagnosticVitalsSnapshot Vitals;
            internal readonly AuxiliaryWorkSnapshot Work;
            internal readonly AuxReplayBudgetReport Budget;
            internal readonly ulong DirtyRevision;
            internal readonly long LastFrame;
            internal readonly int ActiveSteps, AcceptedSteps;
            internal ActualPairSnapshot(DiagnosticVitalsSnapshot vitals, AuxiliaryWorkSnapshot work, AuxReplayBudgetReport budget,
                ulong revision, long frame, int active, int accepted)
            { Vitals = vitals; Work = work; Budget = budget; DirtyRevision = revision; LastFrame = frame; ActiveSteps = active; AcceptedSteps = accepted; }
        }
        internal sealed class PreparedActualPair : IDisposable
        {
            readonly AuxiliaryWorkRuntime _runtime;
            readonly ActualPairBinding _binding;
            readonly State _before, _after;
            readonly AuxiliaryAcceptedStep[] _buffer;
            readonly int _count, _nextCount, _accepted, _nextAccepted, _sealed;
            readonly ulong _revision, _nextRevision;
            readonly long _lastFrame;
            readonly ActualBudget _budget, _nextBudget;
            readonly DiagnosticVitalsSnapshot _vitals;
            readonly DiagnosticAuxiliaryPairContext.Capture _context;
            readonly DiagnosticVitalsProjectionAdapter.PreparedMutation _debit;
            readonly AuxiliaryEvidenceJournal.PreparedActualCount _history;
            readonly AuxiliaryWorkStepResult _result;
            bool _used;
            PreparedActualPair(AuxiliaryWorkRuntime runtime, State before, State after, AuxiliaryAcceptedStep[] buffer,
                int count, int accepted, int sealedCount, ulong revision, long lastFrame, ActualBudget budget, ActualBudget nextBudget,
                DiagnosticVitalsSnapshot vitals, DiagnosticAuxiliaryPairContext.Capture context,
                DiagnosticVitalsProjectionAdapter.PreparedMutation debit, AuxiliaryEvidenceJournal.PreparedActualCount history, AuxiliaryWorkStepResult result)
            { _runtime = runtime; _binding = runtime._actualPair; _before = before; _after = after; _buffer = buffer;
                _count = count; _nextCount = checked(count + 1); _accepted = accepted; _nextAccepted = checked(accepted + 1); _sealed = sealedCount;
                _revision = revision; _nextRevision = checked(revision + 1); _lastFrame = lastFrame; _budget = budget; _nextBudget = nextBudget;
                _vitals = vitals; _context = context; _debit = debit; _history = history; _result = result; }
            internal static PreparedActualPair Create(object issuer, AuxiliaryWorkRuntime runtime, object before, object after,
                AuxiliaryAcceptedStep[] buffer, int count, int accepted, int sealedCount, ulong revision, long lastFrame, object budget, object nextBudget,
                DiagnosticVitalsSnapshot vitals, DiagnosticAuxiliaryPairContext.Capture context,
                DiagnosticVitalsProjectionAdapter.PreparedMutation debit, AuxiliaryEvidenceJournal.PreparedActualCount history, AuxiliaryWorkStepResult result)
            {
                if (runtime == null || issuer == null || !ReferenceEquals(issuer, runtime._actualPair.Issuer)) throw new InvalidOperationException("foreign_actual_pair_issuer");
                return new PreparedActualPair(runtime, (State)before, (State)after, buffer, count, accepted, sealedCount,
                    revision, lastFrame, (ActualBudget)budget, (ActualBudget)nextBudget, vitals, context, debit, history, result);
            }
            internal AuxiliaryWorkStepResult Result => _result;
            internal bool TryInstallUnderGate(AuxiliaryWorkRuntime runtime, ParticipantPublicationAttempt attempt, out string reason)
            {
                CommonParticipantGate.RequireHeld(); _binding.RequireThread(); reason = "stale_actual_pair";
                if (_used || attempt == null || !attempt.IsOpenUnderGate || !ReferenceEquals(runtime, _runtime)) return false;
                _used = true;
                if (!ReferenceEquals(runtime._actualPair, _binding) || _count < 0 || _count >= _buffer.Length || _buffer[_count] != null ||
                    _context.Ordinal <= _lastFrame || !runtime.ActualPrepareMatches(_before, _buffer, _count, _revision, _lastFrame, _budget, _vitals, _context) ||
                    !_debit.MatchesUnderGate() || !_history.MatchesUnderGate() || !runtime._evidenceJournal.ActualPairCapacityUnderGate(_nextBudget.Report, _accepted, _sealed)) return false;
                // Adapter may refuse BEFORE actual model mutation. No runtime/evidence assignments preceded it.
                if (!_debit.TryInstallUnderGate(out reason)) return false;
                // No-fail suffix: private held targets, proven index and precomputed scalar/pointer assignments only.
                _buffer[_count] = _result.AcceptedStep;
                runtime._count = _nextCount; runtime._state = _after;
                runtime._pairRevision = _nextRevision; runtime._lastPairedFrame = _context.Ordinal;
                _history.AssignPrevalidatedNoFail();
                _binding.Budget = _nextBudget;
                reason = ""; return true;
            }
            internal void NotifyAfterGate() => _debit.NotifyAfterGate();
            public void Dispose() { _used = true; _debit.Dispose(); }
        }
    }
}
