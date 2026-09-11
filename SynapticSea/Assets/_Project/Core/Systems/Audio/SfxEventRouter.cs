// Ported from scripts/systems/sfx_event_router.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model that routes named audio events to buses (REQ-AU-001, ADR-0029). Each event carries a per-event
    /// volume, an optional closed caption, and a dedup cooldown. <see cref="Route"/> returns a routing record the
    /// AudioManager applies; captions queue separately for the HUD. Unknown ids are warned and dropped, NEVER routed
    /// to master (ADR-0029 invariant).
    /// </summary>
    public sealed class SfxEventRouter : ISimModel, IStatusLineProvider
    {
        public const double DEFAULT_CAPTION_DURATION = 2.5;
        public const long MAX_CAPTION_QUEUE = 16;

        static GdDict Spec(string bus, double volumeDb, double cooldown, string caption) =>
            new GdDict { { "bus", bus }, { "volume_db", volumeDb }, { "cooldown", cooldown }, { "caption", caption } };

        /// <summary>Per-event catalog: {bus, volume_db, cooldown, caption}. GDScript <c>static var</c>; never mutated by the model.</summary>
        public static readonly GdDict EVENT_CATALOG = new GdDict
        {
            { AudioEventSeam.SFX_TOOL_PICKUP, Spec(AudioEventSeam.BUS_SFX, -3.0, 0.10, "Tool acquired") },
            { AudioEventSeam.SFX_TOOL_USE, Spec(AudioEventSeam.BUS_SFX, -3.0, 0.05, "Tool used") },
            { AudioEventSeam.SFX_SUIT_BREATH, Spec(AudioEventSeam.BUS_SFX, -12.0, 2.0, "") },
            { AudioEventSeam.SFX_DOOR_OPEN, Spec(AudioEventSeam.BUS_SFX, -6.0, 0.10, "Door opened") },
            { AudioEventSeam.SFX_DOOR_CLOSE, Spec(AudioEventSeam.BUS_SFX, -6.0, 0.10, "Door closed") },
            { AudioEventSeam.SFX_FIRE_CRACKLE, Spec(AudioEventSeam.BUS_SFX, -6.0, 0.50, "") },
            { AudioEventSeam.SFX_ARC_ZAP, Spec(AudioEventSeam.BUS_SFX, -4.0, 0.50, "") },
            { AudioEventSeam.SFX_FOOTSTEP, Spec(AudioEventSeam.BUS_SFX, -10.0, 0.30, "") },
            { AudioEventSeam.SFX_DROP_ITEM, Spec(AudioEventSeam.BUS_SFX, -6.0, 0.05, "") },
            { AudioEventSeam.SFX_DOCK_LAND, Spec(AudioEventSeam.BUS_SFX, -3.0, 0.50, "Docked") },
            { AudioEventSeam.SFX_HALLUCINATION_WHISPER, Spec(AudioEventSeam.BUS_SFX, -14.0, 1.5, "") },
            // PKG-D10 pillar verbs (placeholders)
            { AudioEventSeam.SFX_WORK_CUT, Spec(AudioEventSeam.BUS_SFX, -4.0, 0.15, "Cutting") },
            { AudioEventSeam.SFX_WORK_WELD, Spec(AudioEventSeam.BUS_SFX, -4.0, 0.15, "Welding") },
            { AudioEventSeam.SFX_WORK_PATCH, Spec(AudioEventSeam.BUS_SFX, -5.0, 0.15, "Patching") },
            { AudioEventSeam.SFX_WORK_UNBOLT, Spec(AudioEventSeam.BUS_SFX, -6.0, 0.12, "Unbolting") },
            { AudioEventSeam.SFX_WORK_PRY, Spec(AudioEventSeam.BUS_SFX, -5.0, 0.12, "Prying") },
            { AudioEventSeam.SFX_WORK_SPLICE, Spec(AudioEventSeam.BUS_SFX, -6.0, 0.12, "Splicing") },
            { AudioEventSeam.SFX_WORK_HARVEST, Spec(AudioEventSeam.BUS_SFX, -8.0, 0.20, "Harvested") },
            { AudioEventSeam.SFX_WORK_PLANT, Spec(AudioEventSeam.BUS_SFX, -10.0, 0.20, "Planted") },
            { AudioEventSeam.SFX_WORK_MOUNT, Spec(AudioEventSeam.BUS_SFX, -6.0, 0.15, "Mounted") },
            { AudioEventSeam.SFX_COMBAT_HIT, Spec(AudioEventSeam.BUS_SFX, -3.0, 0.08, "") },
            { AudioEventSeam.SFX_COMBAT_THREAT_ALERT, Spec(AudioEventSeam.BUS_SFX, -2.0, 1.0, "Threat detected") },
            { AudioEventSeam.SFX_WOUND_BANDAGE, Spec(AudioEventSeam.BUS_SFX, -8.0, 0.20, "Bandaged") },
            { AudioEventSeam.SFX_WOUND_TREAT, Spec(AudioEventSeam.BUS_SFX, -6.0, 0.25, "Treated wound") },
            { AudioEventSeam.SFX_CRAFT_COMPLETE, Spec(AudioEventSeam.BUS_SFX, -5.0, 0.20, "Craft complete") },
            { AudioEventSeam.SFX_REPAIR_COMPLETE, Spec(AudioEventSeam.BUS_SFX, -5.0, 0.20, "Repair complete") },
            { AudioEventSeam.SFX_SANITY_AMBIENT, Spec(AudioEventSeam.BUS_SFX, -14.0, 1.0, "") },
            { AudioEventSeam.SFX_SANITY_HUD, Spec(AudioEventSeam.BUS_SFX, -10.0, 0.8, "") },
            { AudioEventSeam.SFX_SANITY_PHANTOM, Spec(AudioEventSeam.BUS_SFX, -12.0, 1.2, "") },

            { AudioEventSeam.UI_INVENTORY_OPEN, Spec(AudioEventSeam.BUS_UI, -6.0, 0.10, "") },
            { AudioEventSeam.UI_INVENTORY_CLOSE, Spec(AudioEventSeam.BUS_UI, -6.0, 0.10, "") },
            { AudioEventSeam.UI_PANEL_OPEN, Spec(AudioEventSeam.BUS_UI, -8.0, 0.10, "") },
            { AudioEventSeam.UI_PANEL_CLOSE, Spec(AudioEventSeam.BUS_UI, -8.0, 0.08, "") },
            { AudioEventSeam.UI_OBJECTIVE_ADVANCE, Spec(AudioEventSeam.BUS_UI, -6.0, 0.10, "Objective updated") },
            { AudioEventSeam.UI_SAVE, Spec(AudioEventSeam.BUS_UI, -6.0, 0.10, "") },
            { AudioEventSeam.UI_LOAD, Spec(AudioEventSeam.BUS_UI, -6.0, 0.10, "") },
            { AudioEventSeam.UI_VITALS_LOW, Spec(AudioEventSeam.BUS_UI, -3.0, 4.0, "Vitals low") },
            { AudioEventSeam.UI_WORK_PROGRESS, Spec(AudioEventSeam.BUS_UI, -10.0, 0.5, "") },
            { AudioEventSeam.UI_WOUNDS_OPEN, Spec(AudioEventSeam.BUS_UI, -8.0, 0.15, "") },
            { AudioEventSeam.UI_CHART_ROUTE, Spec(AudioEventSeam.BUS_UI, -8.0, 0.20, "Route plotted") },
            { AudioEventSeam.UI_SHIP_MOD_OPEN, Spec(AudioEventSeam.BUS_UI, -8.0, 0.15, "") },
            { AudioEventSeam.UI_SHIP_MOD_INSTALL, Spec(AudioEventSeam.BUS_UI, -6.0, 0.15, "Component installed") },
            { AudioEventSeam.UI_SHIP_MOD_UNINSTALL, Spec(AudioEventSeam.BUS_UI, -6.0, 0.15, "Component removed") },

            { AudioEventSeam.META_BEACON_DISTRESS, Spec(AudioEventSeam.BUS_META, -6.0, 0.0, "Distress signal received") },
            { AudioEventSeam.META_BIOMATTER_PULSE, Spec(AudioEventSeam.BUS_META, -6.0, 0.0, "") },
            { AudioEventSeam.META_HULL_GROAN, Spec(AudioEventSeam.BUS_META, -6.0, 0.0, "") },
            { AudioEventSeam.META_REACTOR_HUM, Spec(AudioEventSeam.BUS_META, -12.0, 0.0, "") },

            { AudioEventSeam.VOICE_LOG_PLAY, Spec(AudioEventSeam.BUS_VOICE, -3.0, 0.10, "") },

            { AudioEventSeam.AMB_CARGO, Spec(AudioEventSeam.BUS_AMBIENT, -3.0, 0.0, "") },
            { AudioEventSeam.AMB_ENGINE, Spec(AudioEventSeam.BUS_AMBIENT, -3.0, 0.0, "") },
            { AudioEventSeam.AMB_MED_BAY, Spec(AudioEventSeam.BUS_AMBIENT, -3.0, 0.0, "") },
            { AudioEventSeam.AMB_CREW_QUARTERS, Spec(AudioEventSeam.BUS_AMBIENT, -3.0, 0.0, "") },
            { AudioEventSeam.AMB_DOCKING, Spec(AudioEventSeam.BUS_AMBIENT, -3.0, 0.0, "") },
        };

        public bool CaptionsEnabled = true;
        public double CaptionDuration = DEFAULT_CAPTION_DURATION;
        GdDict _cooldownClock = new GdDict();
        double _tickElapsed = 0.0;
        GdArray _captionQueue = new GdArray();
        GdDict _routedCount = new GdDict();
        long _droppedCount = 0;

        /// <summary>Recognized keys: captions_enabled, caption_duration [0.5, 10]. Resets queues and counters.</summary>
        public void Configure(GdDict config)
        {
            if (config == null) return;
            if (config.Has("captions_enabled")) CaptionsEnabled = V.Bool(config["captions_enabled"]);
            if (config.Has("caption_duration")) CaptionDuration = GdMath.Clampf(V.F64(config["caption_duration"]), 0.5, 10.0);
            _cooldownClock.Clear();
            _captionQueue.Clear();
            _routedCount.Clear();
            _droppedCount = 0;
            _tickElapsed = 0.0;
        }

        /// <summary>
        /// Routes an event id. Returns {event_id, bus, volume_db} on success; null when the id is unknown or suppressed
        /// by cooldown. Captions are queued separately.
        /// </summary>
        public GdDict Route(string eventId, bool emitWarning = true)
        {
            string idStr = eventId ?? "";
            if (!EVENT_CATALOG.Has(idStr))
            {
                _droppedCount += 1;
                if (emitWarning) CoreServices.Log.Warning("SfxEventRouter: dropped unknown event id '" + idStr + "'");
                return null;
            }
            var spec = (GdDict)EVENT_CATALOG[idStr];
            // Cooldown compares the latest elapsed tick ("now") with the elapsed value at the previous fire ("last").
            double last = -1.0;
            if (_cooldownClock.Has(idStr)) last = V.F64(_cooldownClock[idStr]);
            double now = _tickElapsed;
            double cooldown = V.F64(spec.Get("cooldown", 0.0));
            if (last >= 0.0 && (now - last) < cooldown)
            {
                _droppedCount += 1;
                return null;
            }
            _cooldownClock[idStr] = now;
            _routedCount[idStr] = V.I64(_routedCount.Get(idStr, 0L)) + 1;
            string caption = V.Str(spec.Get("caption", ""));
            if (CaptionsEnabled && caption.Length != 0) EnqueueCaptionInternal(idStr, caption);
            return new GdDict
            {
                { "event_id", idStr },
                { "bus", spec.Get("bus", AudioEventSeam.BUS_SFX) },
                { "volume_db", V.F64(spec.Get("volume_db", -6.0)) },
            };
        }

        /// <summary>Queues a caption (honours captions_enabled and the MAX_CAPTION_QUEUE cap).</summary>
        public bool EnqueueCaption(string eventId, string text, double duration = -1.0) =>
            EnqueueCaptionInternal(eventId ?? "", text, duration);

        bool EnqueueCaptionInternal(string eventId, string text, double duration = -1.0)
        {
            if (!CaptionsEnabled) return false;
            if (string.IsNullOrEmpty(text)) return false;
            // Drop the oldest entry so a runaway emitter can't stall the HUD.
            if (_captionQueue.Count >= MAX_CAPTION_QUEUE) _captionQueue.PopFront();
            double captionDuration = duration < 0.0 ? CaptionDuration : GdMath.Clampf(duration, 0.5, 10.0);
            _captionQueue.Append(new GdDict
            {
                { "event_id", eventId },
                { "text", text },
                { "duration", captionDuration },
                { "elapsed", 0.0 },
            });
            return true;
        }

        /// <summary>Advances the cooldown clock and ages the caption queue. Returns true when any caption expired.</summary>
        public bool Tick(double deltaSeconds)
        {
            if (deltaSeconds <= 0.0) return false;
            _tickElapsed += deltaSeconds;
            long dropped = 0;
            int i = 0;
            while (i < _captionQueue.Count)
            {
                var entry = (GdDict)_captionQueue[i];
                entry["elapsed"] = V.F64(entry.Get("elapsed", 0.0)) + deltaSeconds;
                if (V.F64(entry.Get("elapsed", 0.0)) >= V.F64(entry.Get("duration", CaptionDuration)))
                {
                    _captionQueue.RemoveAt(i);
                    dropped += 1;
                }
                else
                {
                    _captionQueue[i] = entry;
                    i += 1;
                }
            }
            return dropped > 0;
        }

        /// <summary>Drains the caption queue.</summary>
        public GdArray GetPendingCaptions()
        {
            GdArray pending = _captionQueue.DeepCopy();
            _captionQueue.Clear();
            return pending;
        }

        /// <summary>Snapshot of the caption queue without draining.</summary>
        public GdArray PeekCaptions() => _captionQueue.DeepCopy();

        public long GetRoutedCount(string eventId) => V.I64(_routedCount.Get(eventId ?? "", 0L));

        public long GetDroppedCount() => _droppedCount;

        public static string GetBusForEvent(string eventId)
        {
            var spec = EVENT_CATALOG.Get(eventId ?? "", new GdDict()) as GdDict ?? new GdDict();
            return !spec.IsEmpty ? V.Str(spec.Get("bus", AudioEventSeam.BUS_SFX)) : AudioEventSeam.BUS_SFX;
        }

        public static double GetVolumeForEvent(string eventId)
        {
            var spec = EVENT_CATALOG.Get(eventId ?? "", new GdDict()) as GdDict ?? new GdDict();
            return !spec.IsEmpty ? V.F64(spec.Get("volume_db", -6.0)) : -6.0;
        }

        public static string GetCaptionForEvent(string eventId)
        {
            var spec = EVENT_CATALOG.Get(eventId ?? "", new GdDict()) as GdDict ?? new GdDict();
            return !spec.IsEmpty ? V.Str(spec.Get("caption", "")) : "";
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            long routedTotal = 0;
            foreach (var kv in _routedCount) routedTotal += V.I64(kv.Value);
            return new List<string>
            {
                "Sfx router: routed=" + GdString.FormatInt(routedTotal) + " dropped=" + GdString.FormatInt(_droppedCount) + " captions=" + GdString.FormatInt(_captionQueue.Count),
            };
        }

        public GdDict GetSummary()
        {
            var routed = new GdDict();
            foreach (var kv in _routedCount) routed[V.Str(kv.Key)] = V.I64(kv.Value);
            return new GdDict
            {
                { "kind", "sfx_event_router" },
                { "captions_enabled", CaptionsEnabled },
                { "caption_duration", CaptionDuration },
                { "cooldown_clock", _cooldownClock.DeepCopy() },
                { "routed_count", routed },
                { "dropped_count", _droppedCount },
                { "caption_queue_size", (long)_captionQueue.Count },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (V.Str(summary.Get("kind", "")) != "sfx_event_router") return false;
            bool changed = false;
            if (summary.Has("captions_enabled"))
            {
                bool newCe = V.Bool(summary["captions_enabled"]);
                if (newCe != CaptionsEnabled)
                {
                    CaptionsEnabled = newCe;
                    changed = true;
                }
            }
            if (summary.Has("caption_duration"))
            {
                double newCd = GdMath.Clampf(V.F64(summary["caption_duration"]), 0.5, 10.0);
                if (Math.Abs(newCd - CaptionDuration) > 0.001)
                {
                    CaptionDuration = newCd;
                    changed = true;
                }
            }
            if (summary.Has("cooldown_clock") && summary["cooldown_clock"] is GdDict cd)
            {
                _cooldownClock = cd.DeepCopy();
                changed = true;
            }
            if (summary.Has("routed_count") && summary["routed_count"] is GdDict rc)
            {
                _routedCount.Clear();
                foreach (var kv in rc) _routedCount[V.Str(kv.Key)] = V.I64(kv.Value);
                changed = true;
            }
            if (summary.Has("dropped_count")) _droppedCount = V.I64(summary["dropped_count"]);
            return changed;
        }
    }
}
