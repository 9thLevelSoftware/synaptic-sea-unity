// Ported from scripts/systems/training_event_bus.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-PM-002 / ADR-0033 deterministic training-event bus. A pure ordered log of TrainingEvent records,
    /// ordinary events resolve through <c>data/player/training_actions.json</c> and forward progression grants.
    /// Receipt-owned records log effects already applied elsewhere and never grant or replay XP.
    /// No RNG; ordinary events are processed and replayed in insertion order.
    /// </summary>
    public partial class TrainingEventBus
    {
        public const string DEFAULT_TRAINING_ACTIONS_PATH = "res://data/player/training_actions.json";

        /// <summary>Optional signal-like callback (GDScript Callable <c>on_event_resolved(event)</c>).</summary>
        Action<GdDict> _onEventResolved;
        public Action<GdDict> OnEventResolved { get { if (_trackedOwner == null) return _onEventResolved; lock (CommonParticipantGate.SyncRoot) return _onEventResolved; } set => SetDelegate(ref _onEventResolved, value); }

        /// <summary>
        /// Optional per-event suppression (<c>event_filter(event_id, target_id)</c>).
        /// Convention: returning true SUPPRESSES/DROPS the event (opposite of <see cref="SkillGate"/>).
        /// </summary>
        Func<string, string, bool> _eventFilter;
        public Func<string, string, bool> EventFilter { get { if (_trackedOwner == null) return _eventFilter; lock (CommonParticipantGate.SyncRoot) return _eventFilter; } set => SetDelegate(ref _eventFilter, value); }

        /// <summary>
        /// Optional Domain 6 skill gate (<c>skill_gate(skill_id)</c>).
        /// Convention: returning true means the skill is ALLOWED to train; false drops the XP grant
        /// (opposite of <see cref="EventFilter"/>).
        /// </summary>
        Func<string, bool> _skillGate;
        public Func<string, bool> SkillGate { get { if (_trackedOwner == null) return _skillGate; lock (CommonParticipantGate.SyncRoot) return _skillGate; } set => SetDelegate(ref _skillGate, value); }

        readonly GdDict _actionsById = new GdDict(); // event_id -> {target_skill, base_xp, category}
        GdArray _log = new GdArray();               // sole ordered authority, including already-applied receipts
        long _dropped = 0;
        long _xpTotal = 0;

        /// <summary>Loads the training-actions catalog. Returns false on parse error.</summary>
        bool ConfigureLegacy(GdDict actionsCatalog = null)
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
                _actionsById[eid] = ProtectRecord(new GdDict
                {
                    { "target_skill", V.Str(e.Get("target_skill", "")) },
                    { "base_xp", V.I64(e.Get("base_xp", 0L)) },
                    { "category", V.Str(e.Get("category", "")) },
                });
            }
            return true;
        }

        public bool IsKnown(string eventId) { if (_trackedOwner == null) return _actionsById.Has(eventId); lock (CommonParticipantGate.SyncRoot) return _actionsById.Has(eventId); }

        public long GetEventCount() { if (_trackedOwner == null) return _log.Count; lock (CommonParticipantGate.SyncRoot) return _log.Count; }

        public long GetDroppedCount() { if (_trackedOwner == null) return _dropped; lock (CommonParticipantGate.SyncRoot) return _dropped; }

        public long GetTotalXpDelivered() { if (_trackedOwner == null) return _xpTotal; lock (CommonParticipantGate.SyncRoot) return _xpTotal; }

        /// <summary>
        /// Emits a training event. Returns the resolved record on success; null on unknown id,
        /// EventFilter-suppressed event, or empty target skill. A SkillGate-rejected event is still logged
        /// (with <c>"gated": true</c>) but grants no XP and does not count as dropped.
        /// </summary>
        GdDict EmitLegacy(string eventId, string targetId, PlayerProgressionState progression)
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
            record = ProtectRecord(record);
            _log.Append(record);
            if (_trackedOwner == null) OnEventResolved?.Invoke(record);
            return record;
        }

        /// <summary>
        /// Record an already-applied receipt-owned event without delivering progression effects.
        /// Malformed input throws ArgumentException; a conflicting retained receipt throws InvalidOperationException.
        /// </summary>
        void RecordAppliedLegacy(GdDict eventRecord, string commitId)
        {
            if (string.IsNullOrWhiteSpace(commitId) || eventRecord == null || !IsSafeSnapshot(eventRecord) ||
                !ValidResolvedEvent(eventRecord) ||
                (eventRecord.Has("commit_id") && !(eventRecord.Get("commit_id") is string suppliedId && suppliedId == commitId)) ||
                (eventRecord.Has("receipt_owned") && !(eventRecord.Get("receipt_owned") is bool suppliedOwned && suppliedOwned)))
                throw new ArgumentException("Invalid already-applied event or receipt identity.");

            GdDict candidate = eventRecord.DeepCopy();
            candidate["receipt_owned"] = true;
            candidate["commit_id"] = commitId;
            candidate["sequence"] = (long)_log.Count;
            foreach (object item in _log)
            {
                if (!(item is GdDict prior) || !(prior.Get("receipt_owned") is bool owned && owned) ||
                    prior.Get("commit_id") as string != commitId) continue;
                if (!SameReceiptPayload(prior, candidate))
                    throw new InvalidOperationException("Conflicting already-applied receipt payload.");
                return;
            }
            _log.Append(ProtectRecord(candidate));
        }

        /// <summary>Replays ordinary log rows; receipt-owned rows already have their progression effect.</summary>
        long ReplayIntoLegacy(PlayerProgressionState progression)
        {
            if (progression == null)
                return 0;
            long delivered = 0;
            foreach (object recordVariant in _log)
            {
                GdDict record = recordVariant as GdDict ?? new GdDict();
                if (record.Get("receipt_owned") is bool owned && owned)
                    continue; // Progression was already published by the receipt owner.
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
        public GdArray GetLog() { if (_trackedOwner == null) return _log.DeepCopy(); lock (CommonParticipantGate.SyncRoot) return _log.DeepCopy(); }

        /// <summary>Empties the log and counters (start_new_run).</summary>
        void ResetLegacy()
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
        GdDict ToDictLegacy()
        {
            return new GdDict
            {
                { "log", _log.DeepCopy() },
                { "dropped", _dropped },
                { "xp_total", _xpTotal },
                { "event_count", (long)_log.Count },
            };
        }

        /// <summary>Validate receipt rows and preserve retained receipt identities before replacing the log/counters.</summary>
        bool ApplySummaryLegacy(GdDict summary)
        {
            if (summary == null || !IsSafeSnapshot(summary))
                return false;
            var candidate = new GdArray();
            var receipts = new Dictionary<string, GdDict>(StringComparer.Ordinal);
            object logVariant = summary.Get("log", new GdArray());
            if (logVariant is GdArray entries)
            {
                foreach (object entry in entries)
                {
                    if (!(entry is GdDict e))
                        continue;
                    if (e.Has("receipt_owned") || e.Has("commit_id"))
                    {
                        if (!(e.Get("receipt_owned") is bool owned && owned) ||
                            !(e.Get("commit_id") is string commitId) || string.IsNullOrWhiteSpace(commitId) ||
                            !ValidResolvedEvent(e)) return false;
                        if (receipts.TryGetValue(commitId, out GdDict prior) && !SameReceiptPayload(prior, e))
                            return false;
                        receipts[commitId] = e;
                    }
                    candidate.Append(e.DeepCopy());
                }
            }
            // Import may replace ordinary history and counters, but only Reset may forget confirmed receipts.
            // Compare every retained row, including identical repeats; sequence is already excluded by the
            // canonical comparison and receipt-local numeric equivalence preserves the validated XP value.
            foreach (object item in _log)
            {
                if (!(item is GdDict retained) || !(retained.Get("receipt_owned") is bool owned && owned))
                    continue;
                if (!(retained.Get("commit_id") is string retainedId) ||
                    !receipts.TryGetValue(retainedId, out GdDict incomingReceipt) ||
                    !SameReceiptPayload(retained, incomingReceipt))
                    return false;
            }
            long dropped = V.I64(summary.Get("dropped", 0L));
            long xpTotal = V.I64(summary.Get("xp_total", 0L));
            _log = candidate;
            _dropped = dropped;
            _xpTotal = xpTotal;
            return true;
        }

        static bool ValidResolvedEvent(GdDict row) =>
            NonemptyString(row.Get("event_id")) && NonemptyString(row.Get("target_id")) &&
            NonemptyString(row.Get("skill_id")) && ReceiptXp(row.Get("base_xp"), out _) &&
            row.Get("category") is string && row.Get("is_cross_training") is bool && row.Get("gated") is bool;

        static bool NonemptyString(object value) => value is string text && text.Length != 0;

        // This is the receipt-local numeric seam, not a general JSON codec. Validate the exclusive Int64 upper
        // bound before casting: converting long.MaxValue to double rounds it to the first out-of-range value.
        static bool ReceiptXp(object value, out long amount)
        {
            if (value is long integer) { amount = integer; return integer >= 0; }
            if (value is double number && !double.IsNaN(number) && !double.IsInfinity(number) &&
                number >= 0 && number < 9223372036854775808.0 && Math.Truncate(number) == number)
            {
                amount = (long)number;
                return true;
            }
            amount = 0;
            return false;
        }

        static bool SameReceiptPayload(GdDict left, GdDict right)
        {
            int leftCount = left.Count - (left.Has("sequence") ? 1 : 0);
            int rightCount = right.Count - (right.Has("sequence") ? 1 : 0);
            if (leftCount != rightCount) return false;
            foreach (var entry in left)
            {
                if (entry.Key is string sequence && sequence == "sequence") continue;
                if (!right.TryGetValue(entry.Key, out object other)) return false;
                if (entry.Key is string field && field == "base_xp")
                {
                    if (!ReceiptXp(entry.Value, out long first) || !ReceiptXp(other, out long second) || first != second)
                        return false;
                }
                else if (!ExactValue(entry.Value, other)) return false;
            }
            return true;
        }

        // Opaque metadata compares by exact Variant type/value. Only the declared base_xp field uses the
        // validated long/double equivalence above; never round adjacent large integers through double.
        static bool ExactValue(object left, object right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.GetType() != right.GetType()) return false;
            if (left is GdDict dictionary)
            {
                var other = (GdDict)right;
                if (dictionary.Count != other.Count) return false;
                foreach (var entry in dictionary)
                    if (!other.TryGetValue(entry.Key, out object value) || !ExactValue(entry.Value, value)) return false;
                return true;
            }
            if (left is GdArray array)
            {
                var other = (GdArray)right;
                if (array.Count != other.Count) return false;
                for (int i = 0; i < array.Count; i++)
                    if (!ExactValue(array[i], other[i])) return false;
                return true;
            }
            return left.Equals(right);
        }

        // Keep this boundary local: the detached F04A bus does not depend on component-owned services.
        // DeepCopy retains dictionary keys, so mutable keys and cyclic values must reject before copying.
        static bool IsSafeSnapshot(object snapshot) => IsSafeSnapshot(snapshot, new HashSet<object>());

        static bool IsSafeSnapshot(object snapshot, HashSet<object> ancestors)
        {
            if (snapshot is GdDict dictionary)
            {
                if (!ancestors.Add(dictionary)) return false;
                foreach (var entry in dictionary)
                {
                    if (entry.Key is GdDict || entry.Key is GdArray || !IsSafeSnapshot(entry.Value, ancestors))
                    {
                        ancestors.Remove(dictionary);
                        return false;
                    }
                }
                ancestors.Remove(dictionary);
            }
            else if (snapshot is GdArray array)
            {
                if (!ancestors.Add(array)) return false;
                foreach (object value in array)
                {
                    if (!IsSafeSnapshot(value, ancestors))
                    {
                        ancestors.Remove(array);
                        return false;
                    }
                }
                ancestors.Remove(array);
            }
            return true;
        }
    }
}
