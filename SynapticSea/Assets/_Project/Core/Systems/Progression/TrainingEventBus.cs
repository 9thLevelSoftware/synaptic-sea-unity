// Ported from scripts/systems/training_event_bus.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-PM-002 / ADR-0033 deterministic training-event bus. A pure ordered log of TrainingEvent records,
    /// each resolved through <c>data/player/training_actions.json</c> to a (skill_id, base_xp, category) triple
    /// and forwarded to <see cref="PlayerProgressionState.GrantXp"/>.
    /// No RNG; events are processed in insertion order; replaying the same sequence yields the same XP awards.
    /// </summary>
    public class TrainingEventBus
    {
        public const string DEFAULT_TRAINING_ACTIONS_PATH = "res://data/player/training_actions.json";

        /// <summary>Optional signal-like callback (GDScript Callable <c>on_event_resolved(event)</c>).</summary>
        public Action<GdDict> OnEventResolved;

        /// <summary>
        /// Optional per-event suppression (<c>event_filter(event_id, target_id)</c>).
        /// Convention: returning true SUPPRESSES/DROPS the event (opposite of <see cref="SkillGate"/>).
        /// </summary>
        public Func<string, string, bool> EventFilter;

        /// <summary>
        /// Optional Domain 6 skill gate (<c>skill_gate(skill_id)</c>).
        /// Convention: returning true means the skill is ALLOWED to train; false drops the XP grant
        /// (opposite of <see cref="EventFilter"/>).
        /// </summary>
        public Func<string, bool> SkillGate;

        readonly GdDict _actionsById = new GdDict(); // event_id -> {target_skill, base_xp, category}
        readonly GdArray _log = new GdArray();       // ordered list of resolved events
        long _dropped = 0;
        long _xpTotal = 0;

        /// <summary>Loads the training-actions catalog. Returns false on parse error.</summary>
        public bool Configure(GdDict actionsCatalog = null)
        {
            _actionsById.Clear();
            object variant;
            if (actionsCatalog == null || actionsCatalog.IsEmpty)
            {
                if (!CatalogRegistry.Exists(DEFAULT_TRAINING_ACTIONS_PATH))
                    return false;
                object parsed = CatalogRegistry.Load(DEFAULT_TRAINING_ACTIONS_PATH);
                if (!(parsed is GdDict root))
                    return false;
                variant = root.Get("training_actions", new GdArray());
            }
            else
            {
                variant = actionsCatalog.Get("training_actions", new GdArray());
            }
            if (!(variant is GdArray entries))
                return false;
            foreach (object entry in entries)
            {
                if (!(entry is GdDict e))
                    continue;
                string eid = V.Str(e.Get("event_id", ""));
                if (eid.Length == 0)
                    continue;
                _actionsById[eid] = new GdDict
                {
                    { "target_skill", V.Str(e.Get("target_skill", "")) },
                    { "base_xp", V.I64(e.Get("base_xp", 0L)) },
                    { "category", V.Str(e.Get("category", "")) },
                };
            }
            return true;
        }

        public bool IsKnown(string eventId) => _actionsById.Has(eventId);

        public long GetEventCount() => _log.Count;

        public long GetDroppedCount() => _dropped;

        public long GetTotalXpDelivered() => _xpTotal;

        /// <summary>
        /// Emits a training event. Returns the resolved record on success; null on unknown id,
        /// EventFilter-suppressed event, or empty target skill. A SkillGate-rejected event is still logged
        /// (with <c>"gated": true</c>) but grants no XP and does not count as dropped.
        /// </summary>
        public GdDict Emit(string eventId, string targetId, PlayerProgressionState progression)
        {
            if (!_actionsById.Has(eventId))
            {
                _dropped += 1;
                return null;
            }
            if (EventFilter != null && EventFilter(eventId, targetId))
            {
                _dropped += 1;
                return null;
            }
            GdDict action = _actionsById.GetDictOrEmpty(eventId);
            string skillId = V.Str(action.Get("target_skill", ""));
            long baseXp = V.I64(action.Get("base_xp", 0L));
            if (skillId.Length == 0 || baseXp <= 0)
            {
                _dropped += 1;
                return null;
            }
            bool isCross = IsCrossTraining(V.Str(action.Get("category", "")), progression);
            // Domain 6 skill gate (PR #55 Codex P1): suppress the XP grant but still log the event.
            bool gated = SkillGate != null && !SkillGate(skillId);
            if (!gated)
            {
                if (progression != null)
                    progression.GrantXp(skillId, baseXp, isCross);
                _xpTotal += baseXp;
            }
            var record = new GdDict
            {
                { "event_id", eventId },
                { "target_id", targetId },
                { "skill_id", skillId },
                { "base_xp", baseXp },
                { "category", V.Str(action.Get("category", "")) },
                { "is_cross_training", isCross },
                { "sequence", (long)_log.Count },
                { "gated", gated },
            };
            _log.Append(record);
            OnEventResolved?.Invoke(record);
            return record;
        }

        /// <summary>Replays the log into <paramref name="progression"/> for deterministic restoration.</summary>
        public long ReplayInto(PlayerProgressionState progression)
        {
            if (progression == null)
                return 0;
            long delivered = 0;
            foreach (object recordVariant in _log)
            {
                GdDict record = recordVariant as GdDict ?? new GdDict();
                string skillId = V.Str(record.Get("skill_id", ""));
                long baseXp = V.I64(record.Get("base_xp", 0L));
                bool isCross = V.Bool(record.Get("is_cross_training", false));
                if (skillId.Length == 0 || baseXp <= 0)
                    continue;
                if (V.Bool(record.Get("gated", false)))
                    continue;
                progression.GrantXp(skillId, baseXp, isCross);
                delivered += baseXp;
            }
            return delivered;
        }

        /// <summary>Returns a copy of the log.</summary>
        public GdArray GetLog() => _log.DeepCopy();

        /// <summary>Empties the log and counters (start_new_run).</summary>
        public void Reset()
        {
            _log.Clear();
            _dropped = 0;
            _xpTotal = 0;
        }

        /// <summary>
        /// True when the player's class multiplier for <paramref name="category"/> is NOT the highest among the
        /// class's multipliers (ties broken alphabetically).
        /// </summary>
        bool IsCrossTraining(string category, PlayerProgressionState progression)
        {
            if (progression == null)
                return false;
            if (category.Length == 0)
                return false;
            // Static catalog lookup: the class definition is the source of truth.
            string classId = progression.GetClassId();
            if (classId.Length == 0)
                return false;
            Dictionary<string, ClassDefinition> classes = ClassDefinition.LoadAll();
            if (!classes.TryGetValue(classId, out ClassDefinition classDef))
                return false;
            GdDict mults = classDef.XpMultipliers;
            if (mults.IsEmpty)
                return false;
            string bestCategory = "";
            double bestMult = -1.0;
            foreach (var kv in mults)
            {
                double m = V.F64(kv.Value);
                string cat = V.Str(kv.Key);
                if (m > bestMult || (m == bestMult && (bestCategory == "" || GdString.Less(cat, bestCategory))))
                {
                    bestMult = m;
                    bestCategory = cat;
                }
            }
            return bestCategory != category;
        }

        /// <summary>Deterministic summary for save/load.</summary>
        public GdDict ToDict()
        {
            return new GdDict
            {
                { "log", _log.DeepCopy() },
                { "dropped", _dropped },
                { "xp_total", _xpTotal },
                { "event_count", (long)_log.Count },
            };
        }

        /// <summary>Restores the log. Used by save/load to recover replayable state.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null)
                return false;
            _log.Clear();
            object logVariant = summary.Get("log", new GdArray());
            if (logVariant is GdArray entries)
            {
                foreach (object entry in entries)
                {
                    if (!(entry is GdDict e))
                        continue;
                    _log.Append(e.DeepCopy());
                }
            }
            _dropped = V.I64(summary.Get("dropped", 0L));
            _xpTotal = V.I64(summary.Get("xp_total", 0L));
            return true;
        }
    }
}
