using System;
using SynapticSea.Core.Session;

namespace SynapticSea.Core.Systems
{
    /// <summary>Shadow-only bounded arithmetic witness. Unwired; owns no live vitals, receipts or resource lease.</summary>
    public sealed partial class AuxiliaryWorkRuntime
    {
        public const int MaximumRetainedSteps = 256;
        readonly object _sync = new object();
        AuxiliaryAcceptedStep[] _steps;
        readonly int _capacity;
        State _state;
        int _count;
        public string OwnerId { get; }
        public string RunId { get; }
        public string ActorId { get; }
        public string ServiceId { get; }
        public long BaseRevision { get; }
        public string HashAlgorithm { get; }
        public double RequiredSeconds { get; }
        public int Capacity => _capacity;
        public int RetainedStepCount { get { lock (_sync) return _count; } }
        // A private immutable scalar state prevents aliases and partial published shadow steps.
        sealed class State
        {
            internal readonly double Progress, Eligible, Stamina;
            internal readonly long Sequence;
            internal State(double progress, double eligible, double stamina, long sequence)
            { Progress = progress; Eligible = eligible; Stamina = stamina; Sequence = sequence; }
        }
        public AuxiliaryWorkRuntime(string ownerId, string runId, string actorId, string serviceId,
            long baseRevision, string hashAlgorithm, double requiredSeconds, double progressSeconds,
            double eligibleSeconds, double initialStamina, long eligibleSteps, int capacity)
        {
            if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(actorId) ||
                string.IsNullOrWhiteSpace(serviceId) || baseRevision < 0 ||
                (hashAlgorithm != PaidHashContext.Legacy.Algorithm && hashAlgorithm != PaidHashContext.BitsV2.Algorithm) ||
                !Finite(requiredSeconds) || requiredSeconds <= 0 || !Finite(progressSeconds) || progressSeconds < 0 || progressSeconds > requiredSeconds ||
                !Finite(eligibleSeconds) || eligibleSeconds < progressSeconds || !Finite(initialStamina) || initialStamina < 0 || eligibleSteps < 0 ||
                capacity < 1 || capacity > MaximumRetainedSteps) throw new ArgumentException("invalid_auxiliary_shadow_state");
            OwnerId = ownerId; RunId = runId; ActorId = actorId; ServiceId = serviceId; BaseRevision = baseRevision;
            HashAlgorithm = hashAlgorithm; RequiredSeconds = requiredSeconds;
            _state = new State(progressSeconds, eligibleSeconds, initialStamina, eligibleSteps);
            _steps = new AuxiliaryAcceptedStep[capacity]; _capacity = capacity;
        }
        public AuxiliaryWorkSnapshot Snapshot()
        { lock (_sync) return View(_state); }
        // Default shadow mode retains its complete prefix. Explicit handoff mode returns only the active
        // suffix; full accepted lineage also includes immutable journal history and the pending sealed chunk.
        public AuxiliaryAcceptedStep[] CopyAcceptedSteps()
        {
            lock (_sync)
            { var copy = new AuxiliaryAcceptedStep[_count]; Array.Copy(_steps, copy, _count); return copy; }
        }
        static AuxiliaryWorkSnapshot View(State state) => new AuxiliaryWorkSnapshot(state.Progress, state.Eligible, state.Stamina, state.Sequence);
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        AuxiliaryWorkStepResult NoStep(AuxiliaryWorkStepStatus status, string reason, AuxiliaryWorkFrame frame)
            => new AuxiliaryWorkStepResult(status, reason, View(_state), frame.Stamina, frame.Stamina, null);
        public AuxiliaryWorkStepResult Step(AuxiliaryWorkFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            lock (_sync)
            {
                if (_paired != null || _actualPair != null || _runSessionPair != null) return NoStep(AuxiliaryWorkStepStatus.Refused, "paired_step_only", frame);
                // Preserve TickAuxiliaryWork order: invalid delta precedes live gates and held-input checks.
                if (frame.Delta <= 0 || !Finite(frame.Delta)) return NoStep(AuxiliaryWorkStepStatus.IgnoredDelta, "", frame);
                string gate = frame.GateReason;
                if (!frame.Consent) gate = "explicit_resume_required";
                if (frame.Moving) gate = "moving";
                if (frame.Damaged) gate = "damage";
                if (gate != "ready") return NoStep(AuxiliaryWorkStepStatus.Interrupted, gate, frame);
                if (frame.HoldRequired && !frame.Held) return NoStep(AuxiliaryWorkStepStatus.HeldReleased, "", frame);
                if (_state.Progress == RequiredSeconds) return NoStep(AuxiliaryWorkStepStatus.CompletionReady, "", frame);
                if (!Finite(frame.Stamina) || !Finite(frame.MaxStamina) || !Finite(frame.WoundSpeed))
                    return NoStep(AuxiliaryWorkStepStatus.Refused, "invalid_numeric", frame);
                // Exact legacy operation order. Do not aggregate deltas or substitute average wound/stamina speed.
                double ratio = Math.Max(0, Math.Min(1, frame.Stamina / Math.Max(1, frame.MaxStamina)));
                double speed = frame.WoundSpeed * (.35 + .65 * ratio);
                double remaining = RequiredSeconds - _state.Progress;
                double elapsed = Math.Min(frame.Delta, Math.Min(remaining / speed, frame.Stamina / 8));
                if (elapsed <= 0) return NoStep(AuxiliaryWorkStepStatus.Exhausted, "exhausted", frame);
                double delta = Math.Min(remaining, elapsed * speed);
                double staminaAfter = Math.Max(0, frame.Stamina - 8 * elapsed);
                // Equivalent committed-state finite/aux_progress admission bounds; reject before shadow publication.
                if (!Finite(speed) || speed <= 0 || speed > 1 || !Finite(elapsed) || !Finite(delta) || delta <= 0 ||
                    Math.Abs(delta - elapsed * speed) > 1e-8 || _state.Progress + delta > RequiredSeconds + 1e-8 ||
                    !Finite(staminaAfter) || Math.Abs(staminaAfter - Math.Max(0, frame.Stamina - 8 * elapsed)) > 1e-8)
                    return NoStep(AuxiliaryWorkStepStatus.Refused, "invalid_aux_work", frame);
                double progressAfter = Math.Min(RequiredSeconds, _state.Progress + delta);
                double eligibleAfter = _state.Eligible + elapsed;
                if (!Finite(progressAfter) || !Finite(eligibleAfter)) return NoStep(AuxiliaryWorkStepStatus.Refused, "invalid_numeric", frame);
                if (_count == _steps.Length) return NoStep(AuxiliaryWorkStepStatus.Backpressure, "step_log_full", frame);
                if (_evidenceJournal != null && !_evidenceJournal.CanAcceptStepUnderGate())
                    return NoStep(AuxiliaryWorkStepStatus.Backpressure, "complete_history_capacity", frame);
                if (_state.Sequence == long.MaxValue) return NoStep(AuxiliaryWorkStepStatus.Refused, "sequence_overflow", frame);
                var accepted = new AuxiliaryAcceptedStep(_state.Sequence + 1, frame.Delta, frame.MaxStamina, frame.WoundSpeed,
                    _state.Progress, progressAfter, _state.Eligible, eligibleAfter, delta, elapsed, speed, frame.Stamina, staminaAfter);
                var next = new State(progressAfter, eligibleAfter, staminaAfter, accepted.Sequence);
                var result = new AuxiliaryWorkStepResult(AuxiliaryWorkStepStatus.Accepted, "", View(next), frame.Stamina, staminaAfter, accepted);
                // One lock publishes the private witness+shadow progress/stamina pair. Caller still owns live atomic application.
                _steps[_count++] = accepted; _state = next;
                if (_evidenceJournal != null) _evidenceJournal.RecordAcceptedStepUnderGate();
                return result;
            }
        }
    }
    public enum AuxiliaryWorkStepStatus { Accepted, IgnoredDelta, Interrupted, HeldReleased, CompletionReady, Exhausted, Refused, Backpressure }
    public sealed class AuxiliaryWorkFrame
    {
        public double Delta { get; }
        public double Stamina { get; }
        public double MaxStamina { get; }
        public double WoundSpeed { get; }
        public string GateReason { get; }
        public bool Consent { get; }
        public bool HoldRequired { get; }
        public bool Held { get; }
        public bool Moving { get; }
        public bool Damaged { get; }
        public AuxiliaryWorkFrame(double delta, double stamina, double maxStamina, double woundSpeed, string gateReason = "ready",
            bool consent = true, bool holdRequired = true, bool held = true, bool moving = false, bool damaged = false)
        { Delta = delta; Stamina = stamina; MaxStamina = maxStamina; WoundSpeed = woundSpeed; GateReason = gateReason;
            Consent = consent; HoldRequired = holdRequired; Held = held; Moving = moving; Damaged = damaged; }
    }
    public sealed class AuxiliaryWorkSnapshot
    {
        public double ProgressSeconds { get; }
        public double EligibleSeconds { get; }
        public double LastAcceptedStamina { get; }
        public long EligibleSteps { get; }
        internal AuxiliaryWorkSnapshot(double progress, double eligible, double stamina, long steps)
        { ProgressSeconds = progress; EligibleSeconds = eligible; LastAcceptedStamina = stamina; EligibleSteps = steps; }
    }
    public sealed class AuxiliaryAcceptedStep
    {
        public long Sequence { get; }
        public double RequestedDelta { get; }
        public double MaxStamina { get; }
        public double WoundSpeed { get; }
        public double ProgressBefore { get; }
        public double ProgressAfter { get; }
        public double EligibleBefore { get; }
        public double EligibleAfter { get; }
        public double DeltaSeconds { get; }
        public double ElapsedSeconds { get; }
        public double Speed { get; }
        public double StaminaBefore { get; }
        public double StaminaAfter { get; }
        internal AuxiliaryAcceptedStep(long sequence, double request, double maximum, double wound, double progressBefore, double progressAfter,
            double eligibleBefore, double eligibleAfter, double delta, double elapsed, double speed, double staminaBefore, double staminaAfter)
        { Sequence = sequence; RequestedDelta = request; MaxStamina = maximum; WoundSpeed = wound; ProgressBefore = progressBefore;
            ProgressAfter = progressAfter; EligibleBefore = eligibleBefore; EligibleAfter = eligibleAfter; DeltaSeconds = delta;
            ElapsedSeconds = elapsed; Speed = speed; StaminaBefore = staminaBefore; StaminaAfter = staminaAfter; }
    }
    public sealed class AuxiliaryWorkStepResult
    {
        public AuxiliaryWorkStepStatus Status { get; }
        public string Reason { get; }
        public AuxiliaryWorkSnapshot Snapshot { get; }
        public double StaminaBefore { get; }
        public double StaminaAfter { get; }
        public AuxiliaryAcceptedStep AcceptedStep { get; }
        internal AuxiliaryWorkStepResult(AuxiliaryWorkStepStatus status, string reason, AuxiliaryWorkSnapshot snapshot,
            double before, double after, AuxiliaryAcceptedStep accepted)
        { Status = status; Reason = reason; Snapshot = snapshot; StaminaBefore = before; StaminaAfter = after; AcceptedStep = accepted; }
    }
}
