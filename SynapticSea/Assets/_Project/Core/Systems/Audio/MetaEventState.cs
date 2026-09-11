// Ported from scripts/systems/meta_event_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Deterministic seed-derived meta-event scheduler (REQ-AU-007, ADR-0029). Holds a schedule of scripted meta-events
    /// (id, trigger_time, voice_log_id, volume_db); when elapsed time reaches trigger_time the event fires once and is
    /// recorded. Trigger times are kept sorted ascending. Pure: AudioManager routes due events via SfxEventRouter.
    /// </summary>
    public sealed class MetaEventState : ISimModel, IStatusLineProvider
    {
        /// <summary>Default trigger schedule (seconds from run start), one of each canonical kind. Never mutate.</summary>
        public static readonly GdArray DEFAULT_SCHEDULE = GdArray.Of(
            new GdDict { { "id", AudioEventSeam.META_EVENT_BEACON }, { "trigger_time", 12.0 }, { "voice_log_id", "log.beacon_01" }, { "volume_db", -3.0 } },
            new GdDict { { "id", AudioEventSeam.META_EVENT_PULSE }, { "trigger_time", 30.0 }, { "voice_log_id", "log.pulse_01" }, { "volume_db", -6.0 } },
            new GdDict { { "id", AudioEventSeam.META_EVENT_GROAN }, { "trigger_time", 55.0 }, { "voice_log_id", "" }, { "volume_db", -6.0 } });

        long _runSeed = 0;
        double _elapsed = 0.0;
        GdArray _events = new GdArray();
        GdArray _firedEvents = new GdArray();

        static bool ByTriggerTime(object a, object b) =>
            V.F64(((GdDict)a).Get("trigger_time", 0.0)) < V.F64(((GdDict)b).Get("trigger_time", 0.0));

        public void Configure(GdDict config)
        {
            if (config == null) config = new GdDict();
            _runSeed = V.I64(config.Get("run_seed", 0L));
            object eventsIn = config.Get("events", DEFAULT_SCHEDULE);
            _events.Clear();
            _firedEvents.Clear();
            _elapsed = V.F64(config.Get("initial_elapsed", 0.0));
            if (eventsIn is GdArray eventsArr)
            {
                foreach (object ev in eventsArr)
                {
                    if (!(ev is GdDict evDict)) continue;
                    object idValue = evDict.Get("id");
                    if (idValue == null) continue;
                    _events.Append(new GdDict
                    {
                        { "id", V.Str(idValue) },
                        { "trigger_time", Math.Max(0.0, V.F64(evDict.Get("trigger_time", 0.0))) },
                        { "voice_log_id", V.Str(evDict.Get("voice_log_id", "")) },
                        { "volume_db", GdMath.Clampf(V.F64(evDict.Get("volume_db", -6.0)), -60.0, 0.0) },
                    });
                }
            }
            _events.SortCustom(ByTriggerTime);
            // Seed determinism: with a seed and no explicit events, derive a deterministic time offset that
            // perturbs the schedule consistently without touching the order.
            if (_runSeed != 0 && config.Get("events") == null)
            {
                long absSeed = _runSeed == long.MinValue ? long.MinValue : Math.Abs(_runSeed); // GDScript abs() wraps
                double offset = (absSeed % 7) * 0.5;
                for (int i = 0; i < _events.Count; i++)
                {
                    var ev = (GdDict)_events[i];
                    ev["trigger_time"] = V.F64(ev.Get("trigger_time", 0.0)) + offset;
                    _events[i] = ev;
                }
                _events.SortCustom(ByTriggerTime);
            }
        }

        /// <summary>Advances time and returns the events that fired this tick (each with <c>fired_at</c>).</summary>
        public GdArray Tick(double deltaSeconds)
        {
            if (deltaSeconds <= 0.0) return new GdArray();
            _elapsed += deltaSeconds;
            var due = new GdArray();
            var keep = new GdArray();
            foreach (object evObj in _events)
            {
                var ev = (GdDict)evObj;
                double triggerTime = V.F64(ev.Get("trigger_time", 0.0));
                if (_elapsed >= triggerTime)
                {
                    GdDict fired = ev.DeepCopy();
                    fired["fired_at"] = _elapsed;
                    due.Append(fired);
                    _firedEvents.Append(fired);
                }
                else
                {
                    keep.Append(ev);
                }
            }
            _events = keep;
            return due;
        }

        /// <summary>Injects an event at a specific time. Returns true when added.</summary>
        public bool ScheduleEvent(string eventId, double triggerTime, string voiceLogId = "", double volumeDb = -6.0)
        {
            if (string.IsNullOrEmpty(eventId)) return false;
            _events.Append(new GdDict
            {
                { "id", eventId },
                { "trigger_time", Math.Max(0.0, triggerTime) },
                { "voice_log_id", voiceLogId ?? "" },
                { "volume_db", GdMath.Clampf(volumeDb, -60.0, 0.0) },
            });
            _events.SortCustom(ByTriggerTime);
            return true;
        }

        public double GetElapsed() => _elapsed;

        public long GetPendingCount() => _events.Count;

        public long GetFiredCount() => _firedEvents.Count;

        public GdArray GetPendingEvents() => _events.DeepCopy();

        public GdArray GetFiredEvents() => _firedEvents.DeepCopy();

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            return new List<string>
            {
                "Meta events: elapsed=" + GdString.FormatFixed(_elapsed, 2) + " pending=" + GdString.FormatInt(_events.Count) + " fired=" + GdString.FormatInt(_firedEvents.Count),
            };
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "kind", "meta_event_state" },
                { "run_seed", _runSeed },
                { "elapsed", _elapsed },
                { "pending", _events.DeepCopy() },
                { "fired", _firedEvents.DeepCopy() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (V.Str(summary.Get("kind", "")) != "meta_event_state") return false;
            bool changed = false;
            if (summary.Has("run_seed")) _runSeed = V.I64(summary["run_seed"]);
            if (summary.Has("elapsed"))
            {
                _elapsed = V.F64(summary["elapsed"]);
                changed = true;
            }
            if (summary.Has("pending") && summary["pending"] is GdArray pending)
            {
                _events = pending.DeepCopy();
                changed = true;
            }
            if (summary.Has("fired") && summary["fired"] is GdArray fired)
            {
                _firedEvents = fired.DeepCopy();
                changed = true;
            }
            return changed;
        }
    }
}
