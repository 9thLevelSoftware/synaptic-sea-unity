using System;
using System.Threading;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public sealed partial class AuxiliaryWorkRuntime
    {
        // Unwired same-live-model pair kernel; supplied context still requires authentic scene/read-set issuer.
        // This does not activate RunSession or certify whole-world eligibility/save/completion.
        RunSessionPairBinding _runSessionPair;
        sealed class RunSessionBudget
        {
            internal readonly AuxReplayBudgetReport Report;
            internal RunSessionBudget(AuxReplayBudgetReport report) { Report = report; }
        }
        sealed class RunSessionPairBinding
        {
            internal readonly object Issuer = new object();
            internal readonly int ThreadId = Thread.CurrentThread.ManagedThreadId;
            internal readonly ActualVitalsProjectionBridge Vitals;
            internal readonly DiagnosticAuxiliaryPairContext Context;
            internal RunSessionBudget Budget;
            internal RunSessionPairBinding(ActualVitalsProjectionBridge vitals, DiagnosticAuxiliaryPairContext context, AuxReplayBudgetReport report)
            { Vitals = vitals; Context = context; Budget = new RunSessionBudget(report); }
            internal void RequireThread()
            { if (ThreadId != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("actual_pair_wrong_thread"); }
        }
        internal static AuxiliaryWorkRuntime CreateRunSessionVitalsPaired(string owner, long revision, string algorithm,
            UntrustedAuxReplaySeed seed, int capacity, AuxiliaryEvidenceLimits limits,
            ActualVitalsProjectionBridge vitals, DiagnosticAuxiliaryPairContext context)
        {
            if (seed == null || vitals == null || context == null || !AuxReplayCodecBudget.TrySeed(seed, out var budget))
                throw new ArgumentException("invalid_actual_pair_birth");
            context.RequireThread(); var current = vitals.Read();
            var runtime = CreateWithEvidenceHandoff(owner, seed.Run, seed.Actor, seed.Service, revision, algorithm,
                seed.Duration, seed.Progress, seed.Eligible, current.Values.Stamina, seed.AcceptedSteps, capacity, limits);
            runtime._runSessionPair = new RunSessionPairBinding(vitals, context, budget); return runtime;
        }
        internal bool IsRunSessionPairIssuer(object issuer) => _runSessionPair != null && ReferenceEquals(issuer, _runSessionPair.Issuer);
        internal bool TryPrepareRunSessionPair(out PreparedRunSessionPair plan, out AuxiliaryWorkStepResult result, out string reason)
        {
            plan = null; result = null; reason = "actual_pair_required";
            if (_runSessionPair == null) return false;
            _runSessionPair.RequireThread(); _runSessionPair.Context.RequireThread();
            State before; AuxiliaryAcceptedStep[] buffer; int count, accepted, sealedCount; ulong revision;
            long lastFrame; RunSessionBudget budget; ActualVitalsProjectionSnapshot vitals; DiagnosticAuxiliaryPairContext.Capture context;
            lock (CommonParticipantGate.SyncRoot)
            {
                before = _state; buffer = _steps; count = _count; revision = _pairRevision; lastFrame = _lastPairedFrame;
                accepted = _evidenceJournal.AcceptedStepCount; sealedCount = _evidenceJournal.NextOrdinalUnderGate;
                budget = _runSessionPair.Budget; vitals = _runSessionPair.Vitals.Read(); context = _runSessionPair.Context.ReadUnderGate();
                if (!context.Resources.IsCurrent) { reason = "stale_resources"; return false; }
                if (context.Ordinal <= lastFrame) { reason = "frame_already_applied"; return false; }
                if (!RunSessionBitsEqual(context.Frame.Stamina, vitals.Values.Stamina) || !RunSessionBitsEqual(context.Frame.MaxStamina, vitals.Values.MaxStamina))
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
            var nextBudget = new RunSessionBudget(nextReport);
            var nextState = new State(result.Snapshot.ProgressSeconds, result.Snapshot.EligibleSeconds, result.StaminaAfter, result.Snapshot.EligibleSteps);
            AuxiliaryEvidenceJournal.PreparedActualCount history;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!RunSessionPrepareMatches(before, buffer, count, revision, lastFrame, budget, vitals, context))
                { reason = "stale_actual_pair"; return false; }
                if (!_evidenceJournal.ActualPairCapacityUnderGate(nextReport, accepted, sealedCount))
                { reason = "complete_history_capacity"; return false; }
                history = _evidenceJournal.PrepareActualCountUnderGate(_runSessionPair.Issuer, nextReport, accepted, sealedCount);
            }
            if (!_runSessionPair.Vitals.PrepareNext(vitals, StaminaOnly(vitals.Values, result.StaminaAfter), out var debit, out reason)) return false;
            plan = PreparedRunSessionPair.Create(_runSessionPair.Issuer, this, before, nextState, buffer, count, accepted,
                sealedCount, revision, lastFrame, budget, nextBudget, vitals, context, debit, history, result);
            reason = ""; return true;
        }
        static DiagnosticVitalsValues StaminaOnly(DiagnosticVitalsValues before, double after)
        {
            var scratch = before.ExactScratch(); scratch.Stamina = after;
            return DiagnosticVitalsValues.FromModel(scratch);
        }
        static bool RunSessionBitsEqual(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
        bool RunSessionPrepareMatches(State before, AuxiliaryAcceptedStep[] buffer, int count, ulong revision, long lastFrame,
            RunSessionBudget budget, ActualVitalsProjectionSnapshot vitals, DiagnosticAuxiliaryPairContext.Capture context)
        {
            CommonParticipantGate.RequireHeld();
            return ReferenceEquals(_state, before) && ReferenceEquals(_steps, buffer) && _count == count &&
                _pairRevision == revision && _lastPairedFrame == lastFrame && ReferenceEquals(_runSessionPair.Budget, budget) &&
                vitals.MatchesUnderGate(_runSessionPair.Vitals) && _runSessionPair.Context.MatchesUnderGate(context);
        }
        internal RunSessionPairSnapshot CaptureRunSessionPair()
        {
            if (_runSessionPair == null) throw new InvalidOperationException("actual_pair_required"); _runSessionPair.RequireThread();
            lock (CommonParticipantGate.SyncRoot)
                return new RunSessionPairSnapshot(_runSessionPair.Vitals.Read(), View(_state), _runSessionPair.Budget.Report,
                    _pairRevision, _lastPairedFrame, _count, _evidenceJournal.AcceptedStepCount);
        }
        internal sealed class RunSessionPairSnapshot
        {
            internal readonly ActualVitalsProjectionSnapshot Vitals;
            internal readonly AuxiliaryWorkSnapshot Work;
            internal readonly AuxReplayBudgetReport Budget;
            internal readonly ulong DirtyRevision;
            internal readonly long LastFrame;
            internal readonly int ActiveSteps, AcceptedSteps;
            internal RunSessionPairSnapshot(ActualVitalsProjectionSnapshot vitals, AuxiliaryWorkSnapshot work, AuxReplayBudgetReport budget,
                ulong revision, long frame, int active, int accepted)
            { Vitals = vitals; Work = work; Budget = budget; DirtyRevision = revision; LastFrame = frame; ActiveSteps = active; AcceptedSteps = accepted; }
        }
        internal sealed class PreparedRunSessionPair : IDisposable
        {
            readonly AuxiliaryWorkRuntime _runtime;
            readonly RunSessionPairBinding _binding;
            readonly State _before, _after;
            readonly AuxiliaryAcceptedStep[] _buffer;
            readonly int _count, _nextCount, _accepted, _nextAccepted, _sealed;
            readonly ulong _revision, _nextRevision;
            readonly long _lastFrame;
            readonly RunSessionBudget _budget, _nextBudget;
            readonly ActualVitalsProjectionSnapshot _vitals;
            readonly DiagnosticAuxiliaryPairContext.Capture _context;
            readonly ActualVitalsProjectionBridge.PreparedChange _debit;
            readonly AuxiliaryEvidenceJournal.PreparedActualCount _history;
            readonly AuxiliaryWorkStepResult _result;
            bool _used;
            PreparedRunSessionPair(AuxiliaryWorkRuntime runtime, State before, State after, AuxiliaryAcceptedStep[] buffer,
                int count, int accepted, int sealedCount, ulong revision, long lastFrame, RunSessionBudget budget, RunSessionBudget nextBudget,
                ActualVitalsProjectionSnapshot vitals, DiagnosticAuxiliaryPairContext.Capture context,
                ActualVitalsProjectionBridge.PreparedChange debit, AuxiliaryEvidenceJournal.PreparedActualCount history, AuxiliaryWorkStepResult result)
            { _runtime = runtime; _binding = runtime._runSessionPair; _before = before; _after = after; _buffer = buffer;
                _count = count; _nextCount = checked(count + 1); _accepted = accepted; _nextAccepted = checked(accepted + 1); _sealed = sealedCount;
                _revision = revision; _nextRevision = checked(revision + 1); _lastFrame = lastFrame; _budget = budget; _nextBudget = nextBudget;
                _vitals = vitals; _context = context; _debit = debit; _history = history; _result = result; }
            internal static PreparedRunSessionPair Create(object issuer, AuxiliaryWorkRuntime runtime, object before, object after,
                AuxiliaryAcceptedStep[] buffer, int count, int accepted, int sealedCount, ulong revision, long lastFrame, object budget, object nextBudget,
                ActualVitalsProjectionSnapshot vitals, DiagnosticAuxiliaryPairContext.Capture context,
                ActualVitalsProjectionBridge.PreparedChange debit, AuxiliaryEvidenceJournal.PreparedActualCount history, AuxiliaryWorkStepResult result)
            {
                if (runtime == null || issuer == null || !ReferenceEquals(issuer, runtime._runSessionPair.Issuer)) throw new InvalidOperationException("foreign_actual_pair_issuer");
                return new PreparedRunSessionPair(runtime, (State)before, (State)after, buffer, count, accepted, sealedCount,
                    revision, lastFrame, (RunSessionBudget)budget, (RunSessionBudget)nextBudget, vitals, context, debit, history, result);
            }
            internal AuxiliaryWorkStepResult Result => _result;
            internal bool TryInstallUnderGate(AuxiliaryWorkRuntime runtime, ParticipantPublicationAttempt attempt, out string reason)
            {
                CommonParticipantGate.RequireHeld(); _binding.RequireThread(); reason = "stale_actual_pair";
                if (_used || attempt == null || !attempt.IsOpenUnderGate || !ReferenceEquals(runtime, _runtime)) return false;
                _used = true;
                if (!ReferenceEquals(runtime._runSessionPair, _binding) || _count < 0 || _count >= _buffer.Length || _buffer[_count] != null ||
                    _context.Ordinal <= _lastFrame || !runtime.RunSessionPrepareMatches(_before, _buffer, _count, _revision, _lastFrame, _budget, _vitals, _context) ||
                    !_debit.MatchesUnderGate() || !_history.MatchesUnderGate() || !runtime._evidenceJournal.ActualPairCapacityUnderGate(_nextBudget.Report, _accepted, _sealed)) return false;
                // Adapter may refuse BEFORE actual model mutation. No runtime/evidence assignments preceded it.
                if (!_debit.TryInstallActualRawAndProjectionUnderGate(out reason)) return false;
                // No-fail suffix: private held targets, proven index and precomputed scalar/pointer assignments only.
                _buffer[_count] = _result.AcceptedStep;
                runtime._count = _nextCount; runtime._state = _after;
                runtime._pairRevision = _nextRevision; runtime._lastPairedFrame = _context.Ordinal;
                _history.AssignPrevalidatedNoFail();
                _binding.Budget = _nextBudget;
                reason = ""; return true;
            }
            // Stamina-only commit produces no health notification; no callback authority is synthesized.
            public void Dispose() { _used = true; _debit.Dispose(); }
        }
    }
}
