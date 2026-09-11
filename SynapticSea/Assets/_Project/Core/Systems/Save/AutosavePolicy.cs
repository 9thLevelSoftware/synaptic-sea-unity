// Ported from scripts/systems/autosave_policy.gd @ 96ecb2b0
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Autosave policy (ADR-0031, ADR-0032). Pure model: <see cref="Tick"/> reports when the service should
    /// fire an autosave. Tracks the last autosave wall-clock, in-game time, and event counter. The default
    /// slot_rotation matches REQ-SL-006's "at most 3 autosave slots" cap.
    /// </summary>
    /// <remarks><c>Time.get_ticks_msec()</c> is read from an injected <see cref="IClock"/>.</remarks>
    public class AutosavePolicy
    {
        public const double DEFAULT_CADENCE_SECONDS = 90.0;
        public const long DEFAULT_CADENCE_EVENTS = 8;
        public const double DEFAULT_MIN_REAL_INTERVAL_SECONDS = 5.0;
        public const double DEFAULT_QUICKSAVE_COOLDOWN_SECONDS = 10.0;

        public double CadenceSeconds = DEFAULT_CADENCE_SECONDS;
        public long CadenceEvents = DEFAULT_CADENCE_EVENTS;
        public double MinRealIntervalSeconds = DEFAULT_MIN_REAL_INTERVAL_SECONDS;
        public double QuicksaveCooldownSeconds = DEFAULT_QUICKSAVE_COOLDOWN_SECONDS;

        public bool Force = false;
        double _lastRealTime = -1.0;      // ticks_msec / 1000.0; -1 = never saved
        double _lastGameSeconds = 0.0;    // cumulative in-game seconds
        long _lastEventCount = 0;
        long _lastAutosaveSlotIndex = 0;
        public GdArray SlotRotation = GdArray.Of("autosave_a", "autosave_b", "autosave_c");
        double _lastQuicksaveRealTime = -1.0;

        IClock _clock;

        public AutosavePolicy(IClock clock = null)
        {
            _clock = clock;
        }

        public IClock Clock
        {
            get => _clock ?? CoreServices.Clock;
            set => _clock = value;
        }

        double NowReal() => (double)Clock.TicksMsec() / 1000.0;

        public void Reset()
        {
            _lastRealTime = -1.0;
            _lastGameSeconds = 0.0;
            _lastEventCount = 0;
            _lastAutosaveSlotIndex = 0;
            _lastQuicksaveRealTime = -1.0;
            Force = false;
        }

        /// <summary>Returns {should_save:bool, slot_id:String, reason:String}.</summary>
        public GdDict Tick(double gameSeconds, long eventCount)
        {
            double nowReal = NowReal();
            var result = new GdDict { { "should_save", false }, { "slot_id", "" }, { "reason", "no_trigger" } };
            if (_lastRealTime < 0.0)
            {
                // First invocation: do not fire on tick 0; just seed the counters.
                _lastRealTime = nowReal;
                _lastGameSeconds = gameSeconds;
                _lastEventCount = eventCount;
                return result;
            }
            if (Force)
            {
                Force = false;
                AdvanceRotation();
                _lastRealTime = nowReal;
                _lastGameSeconds = gameSeconds;
                _lastEventCount = eventCount;
                return new GdDict { { "should_save", true }, { "slot_id", CurrentSlot() }, { "reason", "forced" } };
            }
            if ((nowReal - _lastRealTime) < MinRealIntervalSeconds)
                return result; // budget guard
            if ((gameSeconds - _lastGameSeconds) >= CadenceSeconds)
            {
                AdvanceRotation();
                _lastRealTime = nowReal;
                _lastGameSeconds = gameSeconds;
                _lastEventCount = eventCount;
                return new GdDict { { "should_save", true }, { "slot_id", CurrentSlot() }, { "reason", "cadence" } };
            }
            if ((eventCount - _lastEventCount) >= CadenceEvents)
            {
                AdvanceRotation();
                _lastRealTime = nowReal;
                _lastGameSeconds = gameSeconds;
                _lastEventCount = eventCount;
                return new GdDict { { "should_save", true }, { "slot_id", CurrentSlot() }, { "reason", "events" } };
            }
            return result;
        }

        object CurrentSlot() => SlotRotation[(int)(_lastAutosaveSlotIndex % SlotRotation.Count)];

        void AdvanceRotation()
        {
            _lastAutosaveSlotIndex = (_lastAutosaveSlotIndex + 1) % SlotRotation.Count;
        }

        /// <summary>Quicksave guard: should_save false with reason "cooldown" while the cooldown is active.</summary>
        public GdDict TryQuicksave()
        {
            double nowReal = NowReal();
            if (_lastQuicksaveRealTime >= 0.0 && (nowReal - _lastQuicksaveRealTime) < QuicksaveCooldownSeconds)
                return new GdDict { { "should_save", false }, { "slot_id", SaveSlotState.QuicksaveSlotId }, { "reason", "cooldown" } };
            _lastQuicksaveRealTime = nowReal;
            return new GdDict { { "should_save", true }, { "slot_id", SaveSlotState.QuicksaveSlotId }, { "reason", "manual" } };
        }

        /// <summary>GDScript <c>_set_last_quicksave_real_time</c>: direct quicksave guard for tests. Test-only.</summary>
        public void SetLastQuicksaveRealTime(double value)
        {
            _lastQuicksaveRealTime = value;
        }
    }
}
