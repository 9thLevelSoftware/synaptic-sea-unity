// Ported from scripts/systems/phase_timer.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// ADR-0005 shared two-phase timer helper for timer hazards (composed, not inherited, by ElectricalArcState).
    /// <c>Tick</c> flips phase at most once per call and carries the remainder; durations clamp to
    /// <see cref="MINIMUM_PHASE_DURATION"/>.
    /// </summary>
    public sealed class PhaseTimer
    {
        public const double MINIMUM_PHASE_DURATION = 0.1;

        /// <summary>Generic two-phase enum; owners map it to their own typed enum.</summary>
        public enum Phase
        {
            A = 0,
            B = 1,
        }

        /// <summary>
        /// GDScript <c>var phase: int</c>. Renamed because C# cannot have a member and a nested type both named
        /// <c>Phase</c>; owners that assigned <c>_phase_timer.phase</c> assign this instead.
        /// </summary>
        public Phase CurrentPhaseValue = Phase.A;

        /// <summary>GDScript <c>var time_in_phase</c>.</summary>
        public double TimeInPhase = 0.0;

        double _phaseADuration = MINIMUM_PHASE_DURATION;
        double _phaseBDuration = MINIMUM_PHASE_DURATION;

        /// <summary>
        /// Keys "A" and "B" (seconds). Missing keys keep the existing duration; non-numeric or &lt;= 0.1 values clamp
        /// to the minimum. Always resets to Phase.A with zero time in phase.
        /// </summary>
        public void Configure(GdDict phaseDurations)
        {
            if (phaseDurations == null) phaseDurations = new GdDict();
            object aVariant = phaseDurations.Get("A", _phaseADuration);
            object bVariant = phaseDurations.Get("B", _phaseBDuration);
            _phaseADuration = ClampDuration(aVariant);
            _phaseBDuration = ClampDuration(bVariant);
            CurrentPhaseValue = Phase.A;
            TimeInPhase = 0.0;
        }

        /// <summary>Advances the clock; returns true when a phase transition occurred. Non-positive delta is a no-op.</summary>
        public bool Tick(double deltaSeconds)
        {
            if (deltaSeconds <= 0.0) return false;
            TimeInPhase += deltaSeconds;
            double duration = CurrentPhaseValue == Phase.A ? _phaseADuration : _phaseBDuration;
            if (TimeInPhase >= duration)
            {
                TimeInPhase -= duration;
                CurrentPhaseValue = CurrentPhaseValue == Phase.A ? Phase.B : Phase.A;
                return true;
            }
            return false;
        }

        public Phase CurrentPhase() => CurrentPhaseValue;

        public double GetTimeInPhase() => TimeInPhase;

        /// <summary>0.0 at the start of a phase, capped at 1.0.</summary>
        public double NormalizedProgress()
        {
            double duration = CurrentPhaseValue == Phase.A ? _phaseADuration : _phaseBDuration;
            if (duration <= 0.0) return 0.0;
            return GdMath.Clampf(TimeInPhase / duration, 0.0, 1.0);
        }

        public double CurrentPhaseDuration() => CurrentPhaseValue == Phase.A ? _phaseADuration : _phaseBDuration;

        static double ClampDuration(object value)
        {
            if (!(value is long) && !(value is double)) return MINIMUM_PHASE_DURATION;
            return Math.Max(MINIMUM_PHASE_DURATION, V.F64(value));
        }
    }
}
