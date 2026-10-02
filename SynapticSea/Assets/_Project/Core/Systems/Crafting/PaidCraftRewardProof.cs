using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Lossless immutable proof storage. The existing domain coordinator alone publishes it.</summary>
    internal static class PaidCraftRewardProof
    {
        // Scoped to one admission/conservation call. No live policy delegates or expanded results are cached.
        internal sealed class ValidationContext
        {
            Dictionary<string, ClassDefinition> _classes;
            GdDict _skills, _actions;
            InventoryState _inventory;
            internal Dictionary<string, ClassDefinition> Classes => _classes ?? (_classes = ClassDefinition.LoadAll());
            internal GdDict Skills => _skills ?? (_skills = PlayerProgressionState.LoadSkillsCatalog());
            internal GdDict Actions => _actions ?? (_actions = CatalogRegistry.Load(TrainingEventBus.DEFAULT_TRAINING_ACTIONS_PATH) as GdDict ?? new GdDict());
            internal InventoryState Inventory => _inventory ?? (_inventory = new InventoryState());
            internal HistoryValidation History;
            readonly HashSet<string> _categories = new HashSet<string>(StringComparer.Ordinal);
            bool _categoriesReady;
            void LoadCategories()
            {
                if (_categoriesReady) return;
                foreach (GdDict skill in Skills.Values.OfType<GdDict>()) AddCategory(skill.GetString("category"));
                foreach (GdDict action in Actions.GetArrayOrEmpty("training_actions").OfType<GdDict>()) AddCategory(action.GetString("category"));
                foreach (ClassDefinition definition in Classes.Values)
                    foreach (object category in definition.XpMultipliers.Keys) if (category is string text) AddCategory(text);
                _categoriesReady = true;
            }
            void AddCategory(string category) { if (!string.IsNullOrWhiteSpace(category)) _categories.Add(category); }
            internal bool ValidMultipliers(object value)
            {
                LoadCategories();
                return value is GdDict multipliers && multipliers.All(pair => pair.Key is string category && _categories.Contains(category) &&
                    (pair.Value is double || pair.Value is long) && PaidCraftingState.Finite(V.F64(pair.Value)) && V.F64(pair.Value) >= 0);
            }
        }
        // Keys are constructed only after exact reference validation. Neither a caller's expected
        // digest nor an already-published owner can authorize a reference in a later admission.
        internal readonly struct ReferenceKey : IEquatable<ReferenceKey>
        {
            internal readonly string Tip;
            internal readonly long Count, Dropped, Xp;
            internal ReferenceKey(string tip, long count, long dropped, long xp) { Tip = tip; Count = count; Dropped = dropped; Xp = xp; }
            public bool Equals(ReferenceKey other) => Tip == other.Tip && Count == other.Count && Dropped == other.Dropped && Xp == other.Xp;
            public override bool Equals(object other) => other is ReferenceKey key && Equals(key);
            public override int GetHashCode()
            { unchecked { int hash = StringComparer.Ordinal.GetHashCode(Tip); hash = hash * 397 ^ Count.GetHashCode(); hash = hash * 397 ^ Dropped.GetHashCode(); return hash * 397 ^ Xp.GetHashCode(); } }
        }
        internal sealed class HistoryNode
        {
            internal string Hash, Parent;
            internal long Count;
            internal GdDict Row;
            internal bool PrefixValid;
            internal readonly List<HistoryNode> Children = new List<HistoryNode>();
            internal readonly List<ReferenceFact> References = new List<ReferenceFact>();
        }
        internal sealed class ReferenceFact
        {
            internal ReferenceKey Key;
            internal HistoryNode Node;
            internal bool NeedsHash;
            internal string Digest;
            internal readonly Dictionary<string, bool> OwnAbsent = new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        sealed class HistoryFrame
        {
            internal HistoryNode Node;
            internal bool Exit, AddedOwned, Conflict;
        }
        // Reachable auxiliary state is graph + scalar references + one ancestry path. Expanded arrays,
        // typed encoded trees and JSON strings for previous prefixes are never retained here.
        internal sealed class HistoryValidation
        {
            readonly Dictionary<string, HistoryNode> _nodes = new Dictionary<string, HistoryNode>(StringComparer.Ordinal);
            readonly Dictionary<ReferenceKey, ReferenceFact> _references = new Dictionary<ReferenceKey, ReferenceFact>();
            internal readonly Dictionary<string, GdDict> Progression = new Dictionary<string, GdDict>(StringComparer.Ordinal);
            readonly HistoryNode _root = new HistoryNode { Hash = "", Parent = "", Count = 0, PrefixValid = true };
            ReferenceFact _current;
            bool _currentMatches;

            bool TryKey(GdDict reference, out ReferenceKey key)
            {
                key = default;
                if (!ExactKeys(reference, "schema_version", "tip_hash", "count", "dropped", "xp_total", "event_count") || !Version(reference) ||
                    !(reference.Get("tip_hash") is string tip) || !PaidCraftRewardProof.Count(reference, "count") || !PaidCraftRewardProof.Count(reference, "dropped") ||
                    !PaidCraftRewardProof.Count(reference, "xp_total") || !PaidCraftRewardProof.Count(reference, "event_count")) return false;
                long count = reference.GetInt("count");
                if (count != reference.GetInt("event_count") || (count == 0 ? tip.Length != 0 : !HashText(tip) || !_nodes.TryGetValue(tip, out HistoryNode node) || node.Count != count)) return false;
                // Version is the literal long 1 and event_count is the literal long Count, so these
                // two independently validated fields are losslessly represented by the typed key.
                key = new ReferenceKey(tip, count, reference.GetInt("dropped"), reference.GetInt("xp_total")); return true;
            }
            bool Register(GdDict reference, bool needsHash, out ReferenceFact fact)
            {
                fact = null; if (!TryKey(reference, out ReferenceKey key)) return false;
                if (!_references.TryGetValue(key, out fact))
                {
                    fact = new ReferenceFact { Key = key, Node = key.Count == 0 ? _root : _nodes[key.Tip] };
                    _references.Add(key, fact); fact.Node.References.Add(fact);
                }
                fact.NeedsHash |= needsHash; return true;
            }
            internal bool Find(GdDict reference, out ReferenceFact fact)
            { fact = null; return TryKey(reference, out ReferenceKey key) && _references.TryGetValue(key, out fact); }
            static GdDict Training(GdArray path, ReferenceKey key) => new GdDict {
                { "log", path }, { "dropped", key.Dropped }, { "xp_total", key.Xp }, { "event_count", key.Count }
            };
            void Visit(HistoryNode node, GdArray path, Dictionary<string, int> occurrences, GdDict current)
            {
                foreach (ReferenceFact fact in node.References)
                {
                    // Hash is synchronous and read-only; the shallow path refers only to this private
                    // candidate. Keep just the digest after the existing codec/hash call returns.
                    GdDict summary = Training(path, fact.Key);
                    if (fact.NeedsHash) fact.Digest = PaidCraftingState.Hash(summary);
                    if (ReferenceEquals(fact, _current)) _currentMatches = Equal(summary, current);
                    foreach (string commit in fact.OwnAbsent.Keys.ToArray()) fact.OwnAbsent[commit] = !occurrences.ContainsKey(commit);
                }
            }
            internal bool Build(GdDict paid, GdDict training, GdDict domain)
            {
                GdDict history = PaidCraftRewardProof.History(paid);
                if (!ExactKeys(history, "schema_version", "training_nodes", "progression_nodes", "current_training_ref") || !Version(history) ||
                    !(history.Get("training_nodes") is GdDict nodes) || !(history.Get("progression_nodes") is GdDict progression)) return false;
                WorkActionCatalog workCatalog = null;
                foreach (var entry in nodes)
                {
                    if (!(entry.Key is string hash) || !HashText(hash) || !(entry.Value is GdDict node) || !ExactKeys(node, "schema_version", "parent_hash", "count", "row") || !Version(node) ||
                        !(node.Get("parent_hash") is string parent) || !(node.Get("count") is long count) || count <= 0 || count > nodes.Count || !(node.Get("row") is GdDict row) ||
                        PaidCraftingState.Hash(node) != hash) return false;
                    if (row.Has("receipt_owned") || row.Has("commit_id"))
                    {
                        if (!new TrainingEventBus().ApplySummary(new GdDict { { "log", GdArray.Of(row) } })) return false;
                        GdDict receipt = domain.GetDictOrEmpty("receipts").GetDictOrEmpty(row.GetString("commit_id"));
                        if (receipt.IsEmpty) return false;
                        GdDict effect = receipt.GetDictOrEmpty("result");
                        if (effect.GetString("operation") == "craft_complete")
                        { if (!ValidRow(row, count - 1) || !Equal(row, effect.Get("training_record"))) return false; }
                        else
                        {
                            if (workCatalog == null) { workCatalog = new WorkActionCatalog(); workCatalog.LoadDefault(); }
                            GdDict holders = domain.GetDictOrEmpty("holders");
                            string action = holders.GetDictOrEmpty(effect.GetString("source_holder_id")).GetString("kind") == "slot" ? "dismount_component" :
                                holders.GetDictOrEmpty(effect.GetString("destination_holder_id")).GetString("kind") == "slot" ? "mount_component" : "";
                            if (action.Length == 0 || row.GetString("target_id") != effect.GetString("instance_id") || row.GetString("event_id") != workCatalog.GetAction(action).GetString("xp_event")) return false;
                        }
                    }
                    _nodes.Add(hash, new HistoryNode { Hash = hash, Parent = parent, Count = count, Row = row });
                }
                foreach (HistoryNode node in _nodes.Values)
                {
                    HistoryNode parent;
                    if (node.Count == 1) { if (node.Parent.Length != 0) return false; parent = _root; }
                    else if (!_nodes.TryGetValue(node.Parent, out parent) || parent.Count != node.Count - 1) return false;
                    parent.Children.Add(node);
                }
                foreach (var entry in progression)
                {
                    if (!(entry.Key is string hash) || !HashText(hash) || !(entry.Value is GdDict node) || !ExactKeys(node, "schema_version", "summary") || !Version(node) ||
                        !(node.Get("summary") is GdDict summary) || !ValidProgression(summary) || PaidCraftingState.Hash(node) != hash) return false;
                    Progression.Add(hash, summary);
                }
                if (!Register(history.GetDictOrEmpty("current_training_ref"), false, out _current)) return false;
                foreach (GdDict receipt in domain.GetDictOrEmpty("receipts").Values.OfType<GdDict>())
                {
                    GdDict effect = receipt.GetDictOrEmpty("result"); if (effect.GetString("operation") != "craft_complete") continue;
                    GdDict proof = effect.GetDictOrEmpty("reward_proof");
                    if (!Register(proof.GetDictOrEmpty("training_before_ref"), true, out ReferenceFact before) ||
                        !Register(proof.GetDictOrEmpty("training_after_ref"), true, out _)) return false;
                    before.OwnAbsent[receipt.GetString("commit_id")] = false;
                }
                var path = new GdArray();
                var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                var firstOwned = new Dictionary<string, GdDict>(StringComparer.Ordinal);
                Visit(_root, path, occurrences, training);
                var stack = new Stack<HistoryFrame>();
                foreach (HistoryNode child in _root.Children) stack.Push(new HistoryFrame { Node = child });
                int invalidDuplicates = 0;
                while (stack.Count != 0)
                {
                    HistoryFrame frame = stack.Pop(); HistoryNode node = frame.Node; string identity = node.Row.GetString("commit_id");
                    if (frame.Exit)
                    {
                        path.RemoveAt(path.Count - 1);
                        if (identity.Length != 0) { int remaining = occurrences[identity] - 1; if (remaining == 0) occurrences.Remove(identity); else occurrences[identity] = remaining; }
                        if (frame.AddedOwned) firstOwned.Remove(identity);
                        if (frame.Conflict) invalidDuplicates--;
                        continue;
                    }
                    path.Add(node.Row);
                    if (identity.Length != 0) { occurrences.TryGetValue(identity, out int count); occurrences[identity] = count + 1; }
                    if (node.Row.Has("receipt_owned") || node.Row.Has("commit_id"))
                    {
                        if (firstOwned.TryGetValue(identity, out GdDict prior))
                        {
                            // Reuse the original comparator: sequence excluded, only base_xp permits
                            // validated whole-double equivalence, opaque metadata stays exact.
                            frame.Conflict = !new TrainingEventBus().ApplySummary(new GdDict { { "log", GdArray.Of(prior, node.Row) } });
                            if (frame.Conflict) invalidDuplicates++;
                        }
                        else { firstOwned.Add(identity, node.Row); frame.AddedOwned = true; }
                    }
                    node.PrefixValid = invalidDuplicates == 0;
                    Visit(node, path, occurrences, training);
                    frame.Exit = true; stack.Push(frame);
                    foreach (HistoryNode child in node.Children) stack.Push(new HistoryFrame { Node = child });
                }
                // Conflicts on an unreferenced branch are facts, not a new global rejection policy.
                // Only reward before-prefixes previously passed to ApplySummary require PrefixValid.
                return _currentMatches;
            }
        }
        static readonly string[] Expanded = { "progression_before", "progression_after", "training_before", "training_after" };
        internal static bool ExactKeys(GdDict value, params string[] keys) => value != null && value.Count == keys.Length && keys.All(value.Has);
        internal static bool Version(GdDict value) => value.Get("schema_version") is long version && version == 1;
        static bool Count(GdDict value, string key) => value.Get(key) is long number && number >= 0;
        static bool HashText(string text) => text.Length == 64 && text.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        static bool Equal(object left, object right) => V.VariantEquals(left, right);
        internal static GdDict NewHistory() => new GdDict { { "schema_version", 1L }, { "training_nodes", new GdDict() }, { "progression_nodes", new GdDict() },
            { "current_training_ref", new GdDict { { "schema_version", 1L }, { "tip_hash", "" }, { "count", 0L }, { "dropped", 0L }, { "xp_total", 0L }, { "event_count", 0L } } } };
        internal static GdDict History(GdDict paid) => paid.GetDictOrEmpty("reward_history");
        internal static void RefreshCurrent(GdDict paid, GdDict training)
        {
            GdDict history = History(paid);
            if (history.IsEmpty) { history = NewHistory(); paid["reward_history"] = history; }
            history["current_training_ref"] = InternTraining(history, training);
        }
        static GdDict InternTraining(GdDict history, GdDict training)
        {
            string parent = ""; long count = 0;
            foreach (object raw in training.GetArrayOrEmpty("log"))
            {
                var node = new GdDict { { "schema_version", 1L }, { "parent_hash", parent }, { "count", ++count }, { "row", V.DeepCopy(raw) } };
                parent = PaidCraftingState.Hash(node);
                if (!history.GetDictOrEmpty("training_nodes").Has(parent)) history.GetDictOrEmpty("training_nodes")[parent] = node;
            }
            return new GdDict { { "schema_version", 1L }, { "tip_hash", parent }, { "count", count }, { "dropped", training.Get("dropped") },
                { "xp_total", training.Get("xp_total") }, { "event_count", training.Get("event_count") } };
        }
        static string InternProgression(GdDict history, GdDict progression)
        {
            var node = new GdDict { { "schema_version", 1L }, { "summary", progression.DeepCopy() } }; string hash = PaidCraftingState.Hash(node);
            if (!history.GetDictOrEmpty("progression_nodes").Has(hash)) history.GetDictOrEmpty("progression_nodes")[hash] = node;
            return hash;
        }
        internal static void Compact(GdDict paid, GdDict effect)
        {
            GdDict history = History(paid); effect["reward_proof"] = MakeProof(history, effect);
            foreach (string key in Expanded) effect.Erase(key);
        }
        static GdDict MakeProof(GdDict history, GdDict expanded)
        {
            GdDict record = expanded.Get("training_record") as GdDict;
            return new GdDict { { "schema_version", 1L },
                { "progression_before_hash", InternProgression(history, expanded.GetDictOrEmpty("progression_before")) },
                { "progression_after_hash", InternProgression(history, expanded.GetDictOrEmpty("progression_after")) },
                { "training_before_ref", InternTraining(history, expanded.GetDictOrEmpty("training_before")) },
                { "training_after_ref", InternTraining(history, expanded.GetDictOrEmpty("training_after")) },
                { "training_before_hash", PaidCraftingState.Hash(expanded.GetDictOrEmpty("training_before")) },
                { "training_after_hash", PaidCraftingState.Hash(expanded.GetDictOrEmpty("training_after")) },
                { "training_outcome", expanded.GetString("training_event").Length == 0 ? "none" : record == null ? "filtered" : record.GetBool("gated") ? "gated" : "accepted" } };
        }
        static bool TryTraining(GdDict history, GdDict reference, out GdDict training)
        {
            training = null;
            if (!ExactKeys(reference, "schema_version", "tip_hash", "count", "dropped", "xp_total", "event_count") || !Version(reference) ||
                !(reference.Get("tip_hash") is string tip) || !Count(reference, "count") || !Count(reference, "dropped") || !Count(reference, "xp_total") ||
                !Count(reference, "event_count") || reference.GetInt("count") != reference.GetInt("event_count")) return false;
            long remaining = reference.GetInt("count"); GdDict nodes = history.GetDictOrEmpty("training_nodes");
            if (remaining > nodes.Count) return false;
            var reversed = new List<object>();
            while (remaining > 0)
            {
                if (!HashText(tip) || !(nodes.Get(tip) is GdDict node) || node.GetInt("count") != remaining) return false;
                reversed.Add(node.Get("row")); tip = node.GetString("parent_hash"); remaining--;
            }
            if (tip.Length != 0) return false;
            reversed.Reverse();
            training = new GdDict { { "log", new GdArray(reversed.Select(V.DeepCopy)) }, { "dropped", reference.Get("dropped") },
                { "xp_total", reference.Get("xp_total") }, { "event_count", reference.Get("event_count") } };
            return true;
        }
        internal static GdDict Expand(GdDict paid, GdDict effect)
        {
            GdDict result = effect.DeepCopy(); if (effect.GetString("operation") != "craft_complete") return result;
            GdDict history = History(paid), proof = effect.GetDictOrEmpty("reward_proof");
            foreach (string side in new[] { "before", "after" })
            {
                if (!TryTraining(history, proof.GetDictOrEmpty("training_" + side + "_ref"), out GdDict training)) throw new ArgumentException("invalid_training_proof_reference");
                result["training_" + side] = training;
                GdDict node = history.GetDictOrEmpty("progression_nodes").GetDictOrEmpty(proof.GetString("progression_" + side + "_hash"));
                if (!(node.Get("summary") is GdDict progression)) throw new ArgumentException("invalid_progression_proof_reference");
                result["progression_" + side] = progression.DeepCopy();
            }
            return result;
        }
        internal static bool Conserved(GdDict beforeParticipants, GdDict afterParticipants, GdDict effect = null)
        {
            GdDict beforePaid = beforeParticipants.GetDictOrEmpty("paid_crafting"), afterPaid = afterParticipants.GetDictOrEmpty("paid_crafting");
            GdDict expected = beforePaid.DeepCopy();
            if (effect != null && effect.GetString("operation") == "craft_complete")
            {
                GdDict facts = effect.DeepCopy();
                foreach (string side in new[] { "before", "after" })
                {
                    GdDict participant = side == "before" ? beforeParticipants : afterParticipants;
                    facts["progression_" + side] = participant.GetDictOrEmpty("progression").DeepCopy();
                    facts["training_" + side] = participant.GetDictOrEmpty("training").DeepCopy();
                }
                if (!Equal(MakeProof(History(expected), facts), effect.Get("reward_proof"))) return false;
            }
            RefreshCurrent(expected, afterParticipants.GetDictOrEmpty("training"));
            return Equal(History(expected), History(afterPaid));
        }
        static bool ValidRow(GdDict row, long sequence)
        {
            bool owned = row.Has("receipt_owned") || row.Has("commit_id");
            string[] keys = owned ? new[] { "event_id", "target_id", "skill_id", "base_xp", "category", "is_cross_training", "sequence", "gated", "receipt_owned", "commit_id" }
                : new[] { "event_id", "target_id", "skill_id", "base_xp", "category", "is_cross_training", "sequence", "gated" };
            return ExactKeys(row, keys) && new[] { "event_id", "target_id", "skill_id" }.All(k => row.Get(k) is string text && text.Length > 0) &&
                row.Get("base_xp") is long xp && xp > 0 && row.Get("category") is string && row.Get("is_cross_training") is bool && row.Get("gated") is bool &&
                row.Get("sequence") is long index && index == sequence && (!owned || row.Get("receipt_owned") is bool yes && yes && row.Get("commit_id") is string id && id.Length > 0);
        }
        internal static bool ValidProgression(GdDict summary)
        {
            if (!ExactKeys(summary, "schema", "class_id", "skills", "skill_xp", "skill_xp_fractional", "cross_training", "books_read") ||
                summary.GetString("schema") != PlayerProgressionState.SCHEMA_VERSION || !(summary.Get("class_id") is string id) || id.Length == 0) return false;
            foreach (string key in new[] { "skills", "skill_xp", "cross_training" })
                if (!(summary.Get(key) is GdDict values) || values.Any(pair => !(pair.Key is string) || !(pair.Value is long n) || n < 0)) return false;
            if (!(summary.Get("skill_xp_fractional") is GdDict fractions) || fractions.Any(pair => !(pair.Key is string) || !(pair.Value is double n) || !PaidCraftingState.Finite(n) || n < 0 || n >= 1)) return false;
            return summary.Get("books_read") is GdDict books && books.All(pair => pair.Key is string && pair.Value is bool yes && yes);
        }
        // Configure catalog metadata first, then copy a validated transaction before-image without
        // the legacy import adapter's epsilon/clamp. This seam is used only by schema3 rewards.
        internal static bool CopyProgressionExact(PlayerProgressionState target, GdDict summary)
        {
            if (!ValidProgression(summary) || target.ClassId != summary.GetString("class_id")) return false;
            target.ClassId = summary.GetString("class_id");
            target.Skills = summary.GetDictOrEmpty("skills").DeepCopy();
            target.SkillXp = summary.GetDictOrEmpty("skill_xp").DeepCopy();
            target.SkillXpFractional = summary.GetDictOrEmpty("skill_xp_fractional").DeepCopy();
            target.CrossTraining = summary.GetDictOrEmpty("cross_training").DeepCopy();
            target.BooksRead = summary.GetDictOrEmpty("books_read").DeepCopy();
            return true;
        }
        internal static bool ValidAppend(GdDict before, GdDict after, GdDict record, string commit)
        {
            if (!(before.Get("log") is GdArray prefix) || !(after.Get("log") is GdArray actual) ||
                !(before.Get("event_count") is long beforeCount) || beforeCount != prefix.Count ||
                !(after.Get("event_count") is long afterCount) || afterCount != actual.Count ||
                prefix.OfType<GdDict>().Any(row => row.GetString("commit_id") == commit)) return false;
            GdArray expected = prefix.DeepCopy();
            if (record != null)
            {
                if (!ValidRow(record, beforeCount) || !record.GetBool("receipt_owned") || record.GetString("commit_id") != commit) return false;
                expected.Add(record.DeepCopy());
            }
            return afterCount == beforeCount + (record == null ? 0 : 1) && PaidCraftingState.Hash(expected) == PaidCraftingState.Hash(actual);
        }
        internal static bool ValidateHistory(GdDict paid, GdDict training, GdDict domain, ValidationContext context)
        {
            var history = new HistoryValidation();
            if (!history.Build(paid, training, domain)) return false;
            context.History = history; return true;
        }
        internal static bool ValidateReward(GdDict paid, GdDict job, GdDict effect, ValidationContext context)
        {
            GdDict proof = effect.GetDictOrEmpty("reward_proof");
            if (!ExactKeys(proof, "schema_version", "progression_before_hash", "progression_after_hash", "training_before_ref", "training_after_ref", "training_before_hash", "training_after_hash", "training_outcome") || !Version(proof)) return false;
            HistoryValidation history = context.History;
            if (history == null || !history.Find(proof.GetDictOrEmpty("training_before_ref"), out ReferenceFact before) ||
                !history.Find(proof.GetDictOrEmpty("training_after_ref"), out ReferenceFact after) ||
                before.Digest != proof.GetString("training_before_hash") || after.Digest != proof.GetString("training_after_hash") ||
                !history.Progression.TryGetValue(proof.GetString("progression_before_hash"), out GdDict progressionBefore) ||
                !history.Progression.TryGetValue(proof.GetString("progression_after_hash"), out GdDict progressionAfter)) return false;
            GdDict record = effect.Get("training_record") as GdDict;
            string commit = job.GetString("completion_commit_id");
            if (!before.Node.PrefixValid || !before.OwnAbsent.TryGetValue(commit, out bool absent) || !absent) return false;
            if (record != null)
            {
                if (!ValidRow(record, before.Key.Count) || !record.GetBool("receipt_owned") || record.GetString("commit_id") != commit ||
                    before.Key.Count == long.MaxValue || after.Key.Count != before.Key.Count + 1 || after.Node.Parent != before.Key.Tip || !Equal(after.Node.Row, record)) return false;
            }
            else if (after.Key.Count != before.Key.Count || after.Key.Tip != before.Key.Tip) return false;
            string outcome = effect.GetString("training_event").Length == 0 ? "none" : record == null ? "filtered" : record.GetBool("gated") ? "gated" : "accepted";
            if (proof.GetString("training_outcome") != outcome) return false;
            return ReconstructReward(progressionBefore, progressionAfter, before.Key.Dropped, before.Key.Xp,
                after.Key.Dropped, after.Key.Xp, before.Key.Count, job, effect, context);
        }
        // Shared arithmetic for historical graph proofs and actual live conservation. Historical
        // prefix admission/absence/append is independently proven before this small-log operation.
        internal static bool ReconstructReward(GdDict before, GdDict after, long dropped, long xp, long afterDropped, long afterXp,
            long sequence, GdDict job, GdDict effect, ValidationContext context)
        {
            if (!context.ValidMultipliers(effect.Get("xp_multipliers"))) return false;
            GdDict record = effect.Get("training_record") as GdDict;
            var progression = new PlayerProgressionState();
            if (!context.Classes.TryGetValue(before.GetString("class_id"), out ClassDefinition definition)) return false;
            progression.Configure(definition, context.Skills, new GdDict());
            if (!CopyProgressionExact(progression, before)) return false;
            progression.XpMultipliers.Clear(); foreach (var item in effect.GetDictOrEmpty("xp_multipliers")) progression.XpMultipliers[item.Key] = item.Value;
            var emitter = new TrainingEventBus(); emitter.Configure(context.Actions);
            if (!emitter.ApplySummary(new GdDict { { "log", new GdArray() }, { "dropped", dropped }, { "xp_total", xp }, { "event_count", 0L } })) return false;
            string evt = effect.GetString("training_event"), kind = job.GetString("station_kind"), recipe = job.GetString("recipe_id");
            string output = job.GetDictOrEmpty("recipe_definition").GetDictOrEmpty("produces").GetString("item_id");
            InventoryState inventory = context.Inventory;
            string expectedEvent = kind == "kitchen" || kind == "synthesizer" && (job.GetDictOrEmpty("recipe_definition").GetString("category") == "cooking" || inventory.GetCategory(output) == "food" || inventory.GetCategory(output) == "drink")
                ? "cook_meal" : kind == "workbench" || kind == "fabricator" || kind == "field_crafting" ? "fabricate_part" : kind == "medbay" && (recipe.Contains("stim") || output.Contains("stim")) ? "compound_stimulant" : "";
            if (evt != expectedEvent) return false;
            emitter.SkillGate = skill => record == null || !record.GetBool("gated"); emitter.EventFilter = (eventId, target) => record == null;
            GdDict generated = evt.Length == 0 ? null : emitter.Emit(evt, output, progression);
            if (generated == null && record != null) return false;
            if (generated != null)
            {
                var recorded = new TrainingEventBus(); recorded.RecordApplied(generated, job.GetString("completion_commit_id"));
                GdDict tagged = (GdDict)recorded.GetLog()[0]; tagged["sequence"] = sequence;
                if (!Equal(tagged, record)) return false;
            }
            return Equal(progression.GetSummary(), after) && emitter.GetDroppedCount() == afterDropped && emitter.GetTotalXpDelivered() == afterXp;
        }
    }
}
