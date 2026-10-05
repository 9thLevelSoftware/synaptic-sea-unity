using System;
using System.Linq;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Version-one retained manual study. Only its typed receipt can authorize a book reward.</summary>
    public static class ManualStudyState
    {
        public const double RequiredSeconds = 30.0;
        public static GdDict State(GdDict domain) => domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("manual_study");
        public static GdDict New(string run, string actor) => new GdDict { { "schema_version", 1L }, { "run_id", run }, { "actor_id", actor }, { "job", new GdDict() }, { "completed", new GdDict() } };
        public static bool IsOperation(string op) => new[] { "study_start", "study_progress", "study_pause", "study_complete" }.Contains(op);
        static bool Eq(object a, object b) => V.VariantEquals(a, b);
        static bool Text(GdDict d, string k) => d.Get(k) is string s && !string.IsNullOrWhiteSpace(s);
        static bool Finite(object o, out double value) { value = o is double d ? d : double.NaN; return !double.IsNaN(value) && !double.IsInfinity(value); }
        static bool Keys(GdDict d, params string[] k) => PaidCraftRewardProof.ExactKeys(d, k);
        internal static bool ValidJob(GdDict job)
        {
            if (job.IsEmpty) return true;
            return Keys(job, "book_id", "progress_seconds", "status", "resume_required", "reason") && Text(job, "book_id") &&
                Finite(job.Get("progress_seconds"), out double progress) && progress >= 0 && progress <= RequiredSeconds &&
                job.Get("resume_required") is bool && job.Get("reason") is string &&
                new[] { "running", "paused", "completed" }.Contains(job.GetString("status")) &&
                (job.GetString("status") != "running" || !job.GetBool("resume_required")) &&
                (job.GetString("status") != "completed" || progress == RequiredSeconds && !job.GetBool("resume_required"));
        }
        internal static GdDict Recipes()
        {
            var model = new CraftingState(); var result = new GdDict();
            foreach (object id in model.GetAllRecipeIds()) result[id] = model.GetRecipe(V.Str(id)).DeepCopy();
            return result;
        }
        internal static GdDict Progression(GdDict domain, string hash)
            => PaidCraftRewardProof.History(PaidCraftingState.State(domain)).GetDictOrEmpty("progression_nodes").GetDictOrEmpty(hash).GetDictOrEmpty("summary");
        internal static string Intern(GdDict domain, GdDict summary)
        {
            var node = new GdDict { { "schema_version", 1L }, { "summary", summary.DeepCopy() } };
            string hash = PaidCraftingState.Hash(node);
            PaidCraftRewardProof.History(PaidCraftingState.State(domain)).GetDictOrEmpty("progression_nodes")[hash] = node;
            return hash;
        }
        internal static GdDict Reward(GdDict domain, string book, string commit)
        {
            GdDict p = domain.GetDictOrEmpty("participating_state"), before = p.GetDictOrEmpty("progression");
            GdDict jobBefore = State(domain).GetDictOrEmpty("job").DeepCopy();
            if (jobBefore.GetString("book_id") != book || jobBefore.GetString("status") != "running" || jobBefore.GetBool("resume_required") || jobBefore.GetFloat("progress_seconds") != RequiredSeconds || p.GetDictOrEmpty("inventory").GetDictOrEmpty("items").GetInt(book) < 1) throw new ArgumentException("study_incomplete");
            GdDict jobAfter = jobBefore.DeepCopy(); jobAfter["status"] = "completed";
            string progressReceipt = domain.GetDictOrEmpty("receipts").Where(e => e.Value is GdDict row && row.GetDictOrEmpty("result").GetString("operation") == "study_progress" && row.GetDictOrEmpty("result").GetString("book_id") == book)
                .OrderByDescending(e => ((GdDict)e.Value).GetInt("revision")).Select(e => V.Str(e.Key)).FirstOrDefault() ?? "";
            var classes = ClassDefinition.LoadAll();
            if (!classes.TryGetValue(before.GetString("class_id"), out ClassDefinition definition)) throw new ArgumentException("study_class_missing");
            var progression = new PlayerProgressionState();
            progression.Configure(definition, PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            if (!PaidCraftRewardProof.CopyProgressionExact(progression, before) || progression.HasReadBook(book) || !progression.GetBooksCatalog().Has(book)) throw new ArgumentException("invalid_study_reward");
            GdDict bookDefinition = progression.GetBooksCatalog().GetDictOrEmpty(book);
            if (!progression.GrantXpFromBook(book)) throw new ArgumentException("invalid_study_book");
            var knowledge = new RecipeKnowledgeState(); knowledge.ApplySummary(PaidCraftingState.State(domain).GetDictOrEmpty("knowledge"));
            GdDict knowledgeBefore = knowledge.GetSummary(); knowledge.LearnFromBook(book, Recipes());
            GdDict trainingBefore = p.GetDictOrEmpty("training").DeepCopy();
            var log = new TrainingEventBus(); log.Configure(); if (!log.ApplySummary(trainingBefore)) throw new ArgumentException("invalid_study_training");
            GdDict record = null;
            if (bookDefinition.GetInt("book_xp") > 0)
            {
                record = new GdDict { { "event_id", "study_manual" }, { "target_id", book }, { "skill_id", bookDefinition.GetString("target_skill") },
                    { "base_xp", bookDefinition.GetInt("book_xp") }, { "category", PlayerProgressionState.LoadSkillsCatalog().GetDictOrEmpty(bookDefinition.GetString("target_skill")).GetString("category") },
                    { "is_cross_training", false }, { "sequence", log.GetEventCount() }, { "gated", false } };
                log.RecordApplied(record, commit); record = (GdDict)log.GetLog()[log.GetLog().Count - 1];
            }
            var effect = new GdDict { { "operation", "study_complete" }, { "reason", "studied" }, { "book_id", book },
                { "progression_before_hash", Intern(domain, before) }, { "progression_after_hash", Intern(domain, progression.GetSummary()) },
                { "knowledge_before", knowledgeBefore }, { "knowledge_after", knowledge.GetSummary() }, { "book_definition", bookDefinition.DeepCopy() },
                { "training_before", trainingBefore }, { "training_after", log.ToDict() }, { "training_record", record },
                { "job_before", jobBefore }, { "job_after", jobAfter }, { "inventory_before", p.GetDictOrEmpty("inventory").DeepCopy() }, { "inventory_after", p.GetDictOrEmpty("inventory").DeepCopy() }, { "progress_receipt_id", progressReceipt } };
            p["progression"] = progression.GetSummary(); p["training"] = log.ToDict();
            PaidCraftingState.State(domain)["knowledge"] = knowledge.GetSummary();
            PaidCraftRewardProof.RefreshCurrent(PaidCraftingState.State(domain), log.ToDict());
            return effect;
        }
        internal static bool ValidReward(GdDict domain, GdDict effect, string commit)
        {
            if (!Keys(effect, "operation", "reason", "book_id", "progression_before_hash", "progression_after_hash", "knowledge_before", "knowledge_after", "book_definition", "training_before", "training_after", "training_record", "job_before", "job_after", "inventory_before", "inventory_after", "progress_receipt_id") ||
                effect.GetString("operation") != "study_complete" || effect.GetString("reason") != "studied") return false;
            string book = effect.GetString("book_id");
            GdDict jobBefore = effect.GetDictOrEmpty("job_before"), jobAfter = effect.GetDictOrEmpty("job_after"), expectedJob = jobBefore.DeepCopy(); expectedJob["status"] = "completed";
            GdDict progressReceipt = domain.GetDictOrEmpty("receipts").GetDictOrEmpty(effect.GetString("progress_receipt_id"));
            if (!ValidJob(jobBefore) || jobBefore.IsEmpty || jobBefore.GetString("book_id") != book || jobBefore.GetString("status") != "running" || jobBefore.GetBool("resume_required") || jobBefore.GetFloat("progress_seconds") != RequiredSeconds ||
                !Eq(expectedJob, jobAfter) || !Eq(effect.Get("inventory_before"), effect.Get("inventory_after")) || effect.GetDictOrEmpty("inventory_before").GetDictOrEmpty("items").GetInt(book) < 1 ||
                !ValidReceipt(domain, progressReceipt, effect.GetString("progress_receipt_id")) || progressReceipt.GetDictOrEmpty("result").GetString("operation") != "study_progress" || !Eq(progressReceipt.GetDictOrEmpty("result").Get("job_after"), jobBefore)) return false;
            GdDict before = Progression(domain, effect.GetString("progression_before_hash")), after = Progression(domain, effect.GetString("progression_after_hash"));
            if (!PaidCraftRewardProof.ValidProgression(before) || !PaidCraftRewardProof.ValidProgression(after)) return false;
            var classes = ClassDefinition.LoadAll(); if (!classes.TryGetValue(before.GetString("class_id"), out ClassDefinition definition)) return false;
            var progression = new PlayerProgressionState(); progression.Configure(definition, PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            if (!PaidCraftRewardProof.CopyProgressionExact(progression, before) || progression.HasReadBook(book) ||
                !Eq(progression.GetBooksCatalog().Get(book), effect.Get("book_definition")) || !progression.GrantXpFromBook(book) || !Eq(progression.GetSummary(), after)) return false;
            var knowledge = new RecipeKnowledgeState(); knowledge.ApplySummary(effect.GetDictOrEmpty("knowledge_before")); knowledge.LearnFromBook(book, Recipes());
            if (!Eq(knowledge.GetSummary(), effect.Get("knowledge_after"))) return false;
            var log = new TrainingEventBus(); log.Configure(); if (!log.ApplySummary(effect.GetDictOrEmpty("training_before"))) return false;
            GdDict expected = null, bookDefinition = progression.GetBooksCatalog().GetDictOrEmpty(book);
            if (bookDefinition.GetInt("book_xp") > 0)
            {
                expected = new GdDict { { "event_id", "study_manual" }, { "target_id", book }, { "skill_id", bookDefinition.GetString("target_skill") },
                    { "base_xp", bookDefinition.GetInt("book_xp") }, { "category", PlayerProgressionState.LoadSkillsCatalog().GetDictOrEmpty(bookDefinition.GetString("target_skill")).GetString("category") },
                    { "is_cross_training", false }, { "sequence", log.GetEventCount() }, { "gated", false } };
                log.RecordApplied(expected, commit); expected = (GdDict)log.GetLog()[log.GetLog().Count - 1];
            }
            return Eq(expected, effect.Get("training_record")) && Eq(log.ToDict(), effect.Get("training_after"));
        }
        internal static bool ValidReceipt(GdDict domain, GdDict receipt, string id)
        {
            if (!Keys(receipt, "schema_version", "transaction_id", "commit_id", "command_id", "command", "command_hash", "revision", "result") || !PaidCraftRewardProof.Version(receipt) ||
                receipt.GetString("commit_id") != id || receipt.GetString("transaction_id") != id || !Text(receipt, "command_id") || !(receipt.Get("revision") is long rev) || rev <= 0 || rev > domain.GetInt("revision")) return false;
            GdDict c = receipt.GetDictOrEmpty("command"), e = receipt.GetDictOrEmpty("result"), s = State(domain);
            if (!Keys(c, "command_id", "action", "run_id", "actor_id", "book_id", "delta_seconds", "reason") ||
                c.GetString("command_id") != receipt.GetString("command_id") || PaidCraftingState.Hash(c) != receipt.GetString("command_hash") ||
                c.GetString("run_id") != s.GetString("run_id") || c.GetString("actor_id") != s.GetString("actor_id") || !Text(c, "book_id") ||
                !Finite(c.Get("delta_seconds"), out double delta) || !(c.Get("reason") is string) || e.GetString("book_id") != c.GetString("book_id")) return false;
            string op = e.GetString("operation"), action = c.GetString("action");
            if (op != "study_" + action || !IsOperation(op)) return false;
            if (op == "study_complete") return id == CompletionId(s, c.GetString("book_id")) && delta == 0 && ValidReward(domain, e, id);
            string[] effectKeys = action == "progress"
                ? new[] { "operation", "reason", "book_id", "job_before", "job_after", "origin_receipt_id", "eligible_steps" }
                : new[] { "operation", "reason", "book_id", "job_before", "job_after" };
            if (id != "manual_study:" + c.GetString("command_id") || !Keys(e, effectKeys) ||
                !ValidJob(e.GetDictOrEmpty("job_before")) || !ValidJob(e.GetDictOrEmpty("job_after"))) return false;
            GdDict a = e.GetDictOrEmpty("job_before"), b = e.GetDictOrEmpty("job_after");
            if (b.GetString("book_id") != c.GetString("book_id")) return false;
            if (action == "progress")
            {
                GdDict origin = domain.GetDictOrEmpty("receipts").GetDictOrEmpty(e.GetString("origin_receipt_id"));
                if (!(e.Get("eligible_steps") is long steps) || steps <= 0 || !ValidReceipt(domain, origin, e.GetString("origin_receipt_id")) ||
                    origin.GetDictOrEmpty("result").GetString("operation") != "study_start" || origin.GetDictOrEmpty("result").GetString("book_id") != c.GetString("book_id") ||
                    origin.GetDictOrEmpty("result").GetDictOrEmpty("job_after").GetFloat("progress_seconds") != 0) return false;
                return delta > 0 && delta <= RequiredSeconds && a.GetString("status") == "running" && !a.GetBool("resume_required") &&
                b.GetFloat("progress_seconds") == Math.Min(RequiredSeconds, a.GetFloat("progress_seconds") + delta) &&
                Eq(a.Get("book_id"), b.Get("book_id")) && b.GetString("status") == "running" && !b.GetBool("resume_required") && b.GetString("reason") == "";
            }
            if (action == "pause") return delta == 0 && a.GetString("book_id") == b.GetString("book_id") && a.GetString("status") != "completed" &&
                b.GetFloat("progress_seconds") == a.GetFloat("progress_seconds") && b.GetString("status") == "paused" && b.GetBool("resume_required") && b.GetString("reason") == c.GetString("reason");
            return action == "start" && (a.IsEmpty || a.GetString("status") == "completed" || a.GetString("book_id") == b.GetString("book_id")) && delta == 0 && b.GetString("status") == "running" && !b.GetBool("resume_required") && b.GetString("reason") == "" &&
                b.GetFloat("progress_seconds") == (a.GetString("book_id") == b.GetString("book_id") && a.GetString("status") != "completed" ? a.GetFloat("progress_seconds") : 0);
        }
        internal static GdDict LatestProgress(GdDict domain, string book) => domain.GetDictOrEmpty("receipts").Values.OfType<GdDict>()
            .Where(row => row.GetDictOrEmpty("result").GetString("operation") == "study_progress" && row.GetDictOrEmpty("result").GetString("book_id") == book)
            .OrderByDescending(row => row.GetInt("revision")).FirstOrDefault() ?? new GdDict();
        internal static string OriginReceipt(GdDict domain, string book) => domain.GetDictOrEmpty("receipts")
            .Where(entry => entry.Value is GdDict row && row.GetDictOrEmpty("result").GetString("operation") == "study_start" &&
                row.GetDictOrEmpty("result").GetString("book_id") == book && row.GetDictOrEmpty("result").GetDictOrEmpty("job_after").GetFloat("progress_seconds") == 0)
            .OrderBy(entry => ((GdDict)entry.Value).GetInt("revision")).Select(entry => V.Str(entry.Key)).FirstOrDefault() ?? "";
        internal static void PruneProgress(GdDict candidate, GdDict command)
        {
            if (command.GetString("action") != "progress") return;
            foreach (object id in candidate.GetDictOrEmpty("receipts").Keys.ToArray())
            {
                GdDict row = candidate.GetDictOrEmpty("receipts").GetDictOrEmpty(id), effect = row.GetDictOrEmpty("result");
                if (effect.GetString("operation") == "study_progress" && effect.GetString("book_id") == command.GetString("book_id")) candidate.GetDictOrEmpty("receipts").Erase(id);
            }
        }
        internal static bool Conserved(GdDict before, GdDict after, GdDict effect)
        {
            if ((before.GetInt("schema_version") != 4 && before.GetInt("schema_version") != 5) || after.GetInt("schema_version") != before.GetInt("schema_version") || after.GetInt("revision") != before.GetInt("revision") + 1 || after.GetInt("command_sequence") != before.GetInt("command_sequence") + 1) return false;
            GdDict expected = before.DeepCopy(), a = State(before), b = State(after); string op = effect.GetString("operation"), book = effect.GetString("book_id");
            if (!IsOperation(op)) return false;
            if (op == "study_complete")
            {
                if (!Eq(a.Get("job"), effect.Get("job_before")) || !Eq(before.GetDictOrEmpty("participating_state").Get("inventory"), effect.Get("inventory_before")) || a.GetDictOrEmpty("completed").Has(book)) return false;
                string commit = CompletionId(a, book);
                if (!Eq(Reward(expected, book, commit), effect)) return false;
                State(expected)["job"] = effect.GetDictOrEmpty("job_after").DeepCopy(); State(expected).GetDictOrEmpty("completed")[book] = commit;
            }
            else
            {
                if (!Eq(a.Get("job"), effect.Get("job_before")) || a.GetDictOrEmpty("completed").Has(book)) return false;
                if (op == "study_progress" && (effect.GetString("origin_receipt_id") != OriginReceipt(before, book) ||
                    effect.GetInt("eligible_steps") != LatestProgress(before, book).GetDictOrEmpty("result").GetInt("eligible_steps") + 1)) return false;
                State(expected)["job"] = effect.GetDictOrEmpty("job_after").DeepCopy();
            }
            // The coordinator may replace only this book's prior internal progress proof, retaining completed proofs.
            foreach (var entry in before.GetDictOrEmpty("receipts"))
            {
                if (Eq(entry.Value, after.GetDictOrEmpty("receipts").Get(entry.Key))) continue;
                GdDict prior = entry.Value as GdDict, oldEffect = prior?.GetDictOrEmpty("result");
                if (op != "study_progress" || after.GetDictOrEmpty("receipts").Has(entry.Key) || oldEffect?.GetString("operation") != "study_progress" || oldEffect.GetString("book_id") != book) return false;
            }
            int added = after.GetDictOrEmpty("receipts").Keys.Count(k => !before.GetDictOrEmpty("receipts").Has(k));
            if (added > 1) return false;
            // Projections have no study mirrors; study rewards leave every craft job and station untouched.
            GdDict expectedParticipants = expected.GetDictOrEmpty("participating_state"), actualParticipants = after.GetDictOrEmpty("participating_state");
            if (!Eq(expectedParticipants, actualParticipants)) return false;
            foreach (string key in new[] { "schema_version", "domain_mode", "registry", "holders", "machinery", "physical_slots", "component_work", "registered_owners" })
                if (!Eq(expected.Get(key), after.Get(key))) return false;
            return Eq(a.Get("run_id"), b.Get("run_id")) && Eq(a.Get("actor_id"), b.Get("actor_id"));
        }
        internal static string CompletionId(GdDict state, string book) => "manual_study:complete:" + PaidCraftingState.Hash(GdArray.Of(state.GetString("run_id"), state.GetString("actor_id"), book));
        public static bool Validate(GdDict domain, out string reason)
        {
            reason = "invalid_manual_study"; GdDict s = State(domain), paid = PaidCraftingState.State(domain), p = domain.GetDictOrEmpty("participating_state");
            if (!Keys(p, domain.GetInt("schema_version") == 5 ? new[] { "inventory", "progression", "training", "crafting", "field_crafting", "stacks", "paid_crafting", "spoilage", "manual_study", "auxiliary_services" } : new[] { "inventory", "progression", "training", "crafting", "field_crafting", "stacks", "paid_crafting", "spoilage", "manual_study" })) return false;
            if (!Keys(s, "schema_version", "run_id", "actor_id", "job", "completed") || !PaidCraftRewardProof.Version(s) || !Text(s, "run_id") || !Text(s, "actor_id") ||
                s.GetString("run_id") != paid.GetString("run_id") || s.GetString("actor_id") != paid.GetString("actor_id") || !(s.Get("job") is GdDict job) || !ValidJob(job) || !(s.Get("completed") is GdDict completed)) return false;
            GdDict books = PlayerProgressionState.LoadBooksCatalog();
            if (!job.IsEmpty && !books.Has(job.GetString("book_id"))) return false;
            foreach (var entry in completed)
            {
                if (!(entry.Key is string book) || !(entry.Value is string id) || id != CompletionId(s, book) || !p.GetDictOrEmpty("progression").GetDictOrEmpty("books_read").GetBool(book)) return false;
                GdDict receipt = domain.GetDictOrEmpty("receipts").GetDictOrEmpty(id);
                if (!ValidReceipt(domain, receipt, id) || receipt.GetDictOrEmpty("result").GetString("operation") != "study_complete" || receipt.GetDictOrEmpty("result").GetString("book_id") != book) return false;
                GdDict after = Progression(domain, receipt.GetDictOrEmpty("result").GetString("progression_after_hash")), current = p.GetDictOrEmpty("progression");
                if (after.GetString("class_id") != current.GetString("class_id")) return false;
                foreach (var skill in after.GetDictOrEmpty("skills"))
                {
                    long level = current.GetDictOrEmpty("skills").GetInt(skill.Key), oldLevel = V.I64(skill.Value);
                    if (level < oldLevel || level == oldLevel && current.GetDictOrEmpty("skill_xp").GetInt(skill.Key) < after.GetDictOrEmpty("skill_xp").GetInt(skill.Key)) return false;
                }
                foreach (var learned in receipt.GetDictOrEmpty("result").GetDictOrEmpty("knowledge_after").GetDictOrEmpty("known"))
                    if (!paid.GetDictOrEmpty("knowledge").GetDictOrEmpty("known").GetBool(learned.Key)) return false;
            }
            foreach (var entry in domain.GetDictOrEmpty("receipts"))
                if (entry.Value is GdDict receipt && IsOperation(receipt.GetDictOrEmpty("result").GetString("operation")) &&
                    (!ValidReceipt(domain, receipt, V.Str(entry.Key)) || receipt.GetDictOrEmpty("result").GetString("operation") == "study_complete" && completed.GetString(receipt.GetDictOrEmpty("result").GetString("book_id")) != V.Str(entry.Key))) return false;
            // Latest cumulative receipt is the bounded authority for exact saved work. Continue changes consent only.
            if (!job.IsEmpty)
            {
                GdDict latest = domain.GetDictOrEmpty("receipts").Values.OfType<GdDict>()
                    .Where(row => IsOperation(row.GetDictOrEmpty("result").GetString("operation")) && row.GetDictOrEmpty("result").GetString("book_id") == job.GetString("book_id"))
                    .OrderByDescending(row => row.GetInt("revision")).FirstOrDefault();
                if (latest == null) return false;
                GdDict witnessed = latest.GetDictOrEmpty("result").GetDictOrEmpty("job_after");
                if (!Eq(job, witnessed))
                {
                    GdDict paused = witnessed.DeepCopy(); paused["status"] = "paused"; paused["resume_required"] = true; paused["reason"] = "explicit_resume_required";
                    if (witnessed.GetString("status") == "completed" || !Eq(job, paused)) return false;
                }
            }
            else if (domain.GetDictOrEmpty("receipts").Values.OfType<GdDict>().Any(row => IsOperation(row.GetDictOrEmpty("result").GetString("operation")) && !completed.Has(row.GetDictOrEmpty("result").GetString("book_id")))) return false;
            if (job.GetString("status") == "completed" && !completed.Has(job.GetString("book_id"))) return false;
            reason = "ok"; return true;
        }
    }
}
