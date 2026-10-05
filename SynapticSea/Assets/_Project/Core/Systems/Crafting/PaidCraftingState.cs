using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Pure schema and conservation rules. Publication belongs to DomainTransactionCoordinator.</summary>
    public static class PaidCraftingState
    {
        static readonly string[] Operations = { "craft_start", "craft_enqueue", "craft_progress", "craft_resume", "craft_block", "craft_complete", "craft_cancel", "craft_legacy_import", "craft_legacy_decision" };
        static readonly string[] PaymentFields = { "job_id", "recipe_id", "recipe_hash", "recipe_definition", "run_id", "actor_id", "inventory_owner_id", "station_owner_id", "station_id", "station_kind", "channel", "consumed", "start_skill", "start_tier", "start_level", "start_known", "start_powered", "material_quality", "quality_score", "quality_tier", "quality_multiplier", "required_seconds", "payment_commit_id", "completion_commit_id" };
        internal static bool IsDomainVersion(long version) => version == 3 || version == 4 || version == 5;
        public static GdDict State(GdDict domain) => domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting");
        public static bool IsOperation(string operation) => Operations.Contains(operation);
        public const string InternalCommandPrefix = "$paid-internal:";
        public static bool InternalReceipt(GdDict receipt, string jobId)
        {
            GdDict command = receipt.GetDictOrEmpty("command"), effect = receipt.GetDictOrEmpty("result");
            string op = effect.GetString("operation");
            bool internalStep = command.GetBool("internal");
            if (internalStep != effect.GetBool("internal") || internalStep != command.GetString("command_id").StartsWith(InternalCommandPrefix, StringComparison.Ordinal) ||
                internalStep && op != "craft_progress" && op != "craft_block") return false;
            return command.GetBool("internal") && command.GetString("command_id").StartsWith(InternalCommandPrefix, StringComparison.Ordinal) &&
                (op == "craft_progress" || op == "craft_block") && effect.GetString("job_id") == jobId;
        }
        internal static void PruneInternalReceipts(GdDict candidate, GdDict command, GdDict effect)
        {
            if (!command.GetBool("internal") || (effect.GetString("operation") != "craft_progress" && effect.GetString("operation") != "craft_block")) return;
            GdDict receipts = candidate.GetDictOrEmpty("receipts");
            foreach (object key in receipts.Keys.ToArray())
                if (receipts.Get(key) is GdDict receipt && InternalReceipt(receipt, effect.GetString("job_id"))) receipts.Erase(key);
        }
        public static bool Terminal(GdDict job) => job.GetString("status") == "completed_delivered" || job.GetString("status") == "cancelled";
        public static GdDict Payment(GdDict job)
        {
            var result = new GdDict();
            foreach (string key in PaymentFields) result[key] = V.DeepCopy(job.Get(key));
            return result;
        }
        public static string Hash(object value)
        {
            if (AdmissionHashMemo.TryGet(value, out string cached)) return cached;
            string digest = CanonicalHash(value); AdmissionHashMemo.Record(value, digest); return digest;
        }
        static string CanonicalHash(object value)
        {
            using (var sha = SHA256.Create())
            {
                GdDict envelope;
                envelope = ComponentDomainCodec.Encode(new GdDict { { "value", Sorted(value) } });
                byte[] input;
                input = Encoding.UTF8.GetBytes(GdJson.Stringify(envelope));
                byte[] bytes;
                bytes = sha.ComputeHash(input);
                string digest = string.Concat(bytes.Select(b => b.ToString("x2")));
                return digest;
            }
        }
        static object Sorted(object value)
        {
            if (value is GdDict dict)
            {
                var copy = new GdDict();
                foreach (object key in dict.Keys.OrderBy(V.Str, StringComparer.Ordinal)) copy[key] = Sorted(dict[key]);
                return copy;
            }
            if (value is GdArray array) return new GdArray(array.Select(Sorted));
            return value;
        }
        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        // Authored JSON uses Godot-compatible doubles; owned payment counts are exact Int64 values.
        internal static bool TryPositiveWholeQuantity(object value, out long quantity)
        {
            quantity = 0;
            if (value is long integer) { quantity = integer; return integer > 0; }
            if (!(value is double number) || !Finite(number) || number <= 0 || number >= 9223372036854775808.0 || Math.Truncate(number) != number) return false;
            quantity = checked((long)number);
            return true;
        }
        internal static bool TryRecipeIngredients(GdDict recipe, out GdDict ingredients)
        {
            ingredients = new GdDict();
            if (!(recipe.Get("ingredients") is GdDict authored)) return false;
            foreach (var entry in authored)
            {
                if (!(entry.Key is string id) || string.IsNullOrWhiteSpace(id) || !TryPositiveWholeQuantity(entry.Value, out long quantity)) return false;
                ingredients[id] = quantity;
            }
            return true;
        }
        static bool Text(GdDict row, string key) => row.Get(key) is string s && !string.IsNullOrWhiteSpace(s);
        static bool Integer(GdDict row, string key) => row.Get(key) is long n && n >= 0;
        static bool Equal(object a, object b) => V.VariantEquals(a, b);
        static bool Keys(GdDict row, params string[] allowed) => row.Keys.All(k => k is string s && allowed.Contains(s));
        public static bool ValidReceipt(GdDict receipt, string id, long revision)
        {
            try { return ValidReceiptCore(receipt, id, revision); }
            catch (ArgumentException) { return false; }
            catch (OverflowException) { return false; }
        }
        static bool ValidReceiptCore(GdDict receipt, string id, long revision)
        {
            if (receipt == null || !PaidCraftRewardProof.ExactKeys(receipt, "schema_version", "transaction_id", "commit_id", "command_id", "command", "command_hash", "revision", "result") || !PaidCraftRewardProof.Version(receipt) || receipt.GetString("transaction_id") != id || receipt.GetString("commit_id") != id ||
                !Text(receipt, "command_id") || !Integer(receipt, "revision") || receipt.GetInt("revision") <= 0 || receipt.GetInt("revision") > revision ||
                !(receipt.Get("command") is GdDict command) || receipt.GetString("command_hash") != Hash(command) || command.GetString("command_id") != receipt.GetString("command_id") ||
                !(receipt.Get("result") is GdDict effect) || !IsOperation(effect.GetString("operation"))) return false;
            string op = effect.GetString("operation"), action = command.GetString("action");
            string[] commandKeys = { "command_id", "internal", "action", "station_kind", "recipe_id", "job_id", "reconciliation_id", "decision" };
            if (action == "legacy_import") commandKeys = commandKeys.Concat(new[] { "source_id", "source_hash", "station_summary", "field_summary" }).ToArray();
            if (!PaidCraftRewardProof.ExactKeys(command, commandKeys) || !(command.Get("internal") is bool internalStep) ||
                commandKeys.Where(k => k != "internal" && k != "station_summary" && k != "field_summary").Any(k => !(command.Get(k) is string)) || !Text(effect, "reason")) return false;
            if (internalStep != effect.GetBool("internal") || internalStep != command.GetString("command_id").StartsWith(InternalCommandPrefix, StringComparison.Ordinal) ||
                internalStep && op != "craft_progress" && op != "craft_block") return false;
            if (op != "craft_complete" && id != "craft:" + command.GetString("command_id")) return false;
            bool direct = action == "start" || action == "enqueue", legacyImport = action == "legacy_import", legacyDecision = action == "legacy_decision";
            if (direct ? !Text(command, "station_kind") || !Text(command, "recipe_id") || command.GetString("job_id").Length != 0 :
                command.GetString("station_kind").Length != 0 || command.GetString("recipe_id").Length != 0 ||
                (legacyImport || legacyDecision ? command.GetString("job_id").Length != 0 : !Text(command, "job_id"))) return false;
            if (legacyDecision ? !Text(command, "reconciliation_id") || !new[] { "keep_paused", "abandon", "start_fresh" }.Contains(command.GetString("decision")) :
                command.GetString("reconciliation_id").Length != 0 || command.GetString("decision").Length != 0) return false;
            string[] fields;
            switch (op)
            {
                case "craft_start":
                    if (action != "start" && action != "resume" && action != "retry" || effect.GetString("reason") != "started") return false;
                    fields = new[] { "operation", "reason", "job_id", "recipe_id", "payment" }; break;
                case "craft_enqueue":
                    if (action != "enqueue" || effect.GetString("reason") != "queued") return false;
                    fields = new[] { "operation", "reason", "job_id", "recipe_id" }; break;
                case "craft_resume":
                    if (action != "resume" && action != "retry" || effect.GetString("reason") != "resumed") return false;
                    fields = new[] { "operation", "reason", "job_id" }; break;
                case "craft_cancel":
                    if (action != "cancel" || effect.GetString("reason") != "cancelled") return false;
                    fields = new[] { "operation", "reason", "job_id" }; break;
                case "craft_progress":
                    if (action != "progress" || !internalStep || effect.GetString("reason") != "advanced" || !(effect.Get("delta_seconds") is double delta) || !Finite(delta) || delta <= 0) return false;
                    fields = new[] { "operation", "reason", "job_id", "delta_seconds", "internal", "progress_seconds" }; break;
                case "craft_block":
                    if (action != "block" || !internalStep) return false;
                    fields = new[] { "operation", "reason", "job_id", "internal", "progress_seconds" }; break;
                case "craft_complete":
                    if (action != "retry" && action != "resume" && action != "complete" || effect.GetString("reason") != "delivered" ||
                        id != "craft:complete:" + effect.GetString("job_id") || action == "complete" && command.GetString("command_id") != "complete:" + effect.GetString("job_id") ||
                        !(effect.Get("output") is GdDict) || !(effect.Get("training_event") is string) || !(effect.Get("xp_multipliers") is GdDict) ||
                        effect.Get("training_record") != null && !(effect.Get("training_record") is GdDict) || !(effect.Get("reward_proof") is GdDict)) return false;
                    fields = new[] { "operation", "reason", "job_id", "recipe_id", "output", "training_event", "xp_multipliers", "training_record", "reward_proof" }; break;
                case "craft_legacy_import":
                    if (!legacyImport || effect.GetString("reason") != "legacy_quarantined" || !Text(command, "source_id") || command.GetString("source_hash").Length != 64 ||
                        command.GetString("source_hash").Any(c => !Uri.IsHexDigit(c)) || !(command.Get("station_summary") is GdDict) || !(command.Get("field_summary") is GdDict) || !(effect.Get("legacy_records") is GdDict)) return false;
                    fields = new[] { "operation", "reason", "legacy_records" }; break;
                case "craft_legacy_decision":
                    if (!legacyDecision || effect.GetString("decision") != command.GetString("decision") || effect.GetString("reconciliation_id") != command.GetString("reconciliation_id") ||
                        effect.GetString("reason") != (command.GetString("decision") == "start_fresh" ? "started_fresh" : command.GetString("decision") == "abandon" ? "abandoned" : "keep_paused")) return false;
                    fields = effect.GetString("decision") == "start_fresh" ? new[] { "operation", "reason", "job_id", "recipe_id", "payment", "decision", "reconciliation_id" } : new[] { "operation", "reason", "decision", "reconciliation_id" }; break;
                default: return false;
            }
            if (!PaidCraftRewardProof.ExactKeys(effect, fields) || fields.Where(k => k == "job_id" || k == "recipe_id" || k == "reconciliation_id").Any(k => !Text(effect, k))) return false;
            if (internalStep && (!(effect.Get("internal") is bool yes) || !yes || !(effect.Get("progress_seconds") is double progress) || !Finite(progress) || progress < 0)) return false;
            if (!direct && !legacyImport && !legacyDecision && effect.GetString("job_id") != command.GetString("job_id")) return false;
            if (op == "craft_start" || op == "craft_legacy_decision" && effect.GetString("decision") == "start_fresh")
            {
                GdDict payment = effect.GetDictOrEmpty("payment");
                if (!PaidCraftRewardProof.ExactKeys(payment, PaymentFields) || payment.GetString("payment_commit_id") != id || payment.GetString("job_id") != effect.GetString("job_id")) return false;
            }
            return true;
        }
        public static bool Validate(GdDict domain, out string reason)
        {
            try { return ValidateCore(domain, out reason); }
            catch (ArgumentException) { reason = "invalid_paid_value"; return false; }
            catch (OverflowException) { reason = "invalid_paid_value"; return false; }
            catch (InvalidOperationException) { reason = "invalid_paid_value"; return false; }
        }
        static bool ValidateCore(GdDict domain, out string reason)
        {
            reason = "invalid_paid_crafting";
            string mode = domain.GetString("domain_mode");
            if (mode != "craft_only" && mode != "components_and_craft") return false;
            if (mode == "craft_only" && (!domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").IsEmpty ||
                new[] { "holders", "machinery", "physical_slots", "component_work" }.Any(k => !domain.GetDictOrEmpty(k).IsEmpty) || domain.GetArrayOrEmpty("registered_owners").Count != 0)) return false;
            if (!PaidCraftRewardProof.ValidProgression(domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression")))
            { reason = "invalid_current_progression"; return false; }
            GdDict state = State(domain), receipts = domain.GetDictOrEmpty("receipts");
            var internalJobs = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (GdDict receipt in receipts.Values.OfType<GdDict>())
            {
                GdDict command = receipt.GetDictOrEmpty("command");
                if (!command.GetBool("internal")) continue;
                string commandId = command.GetString("command_id"), jobId = receipt.GetDictOrEmpty("result").GetString("job_id");
                int separator = commandId.LastIndexOf(':');
                if (!internalJobs.Add(jobId) || separator < 0 || !long.TryParse(commandId.Substring(separator + 1), out long sequence) || sequence <= 0 || sequence > domain.GetInt("command_sequence")) return false;
            }
            if (!Keys(state, "schema_version", "run_id", "actor_id", "jobs", "queues", "legacy", "knowledge", "reward_history") || !PaidCraftRewardProof.Version(state) || !Text(state, "run_id") || !Text(state, "actor_id") ||
                !(state.Get("jobs") is GdDict jobs) || !(state.Get("queues") is GdDict queues) || !(state.Get("legacy") is GdDict legacy) ||
                !(state.Get("knowledge") is GdDict knowledge) || !(knowledge.Get("known") is GdDict known) || !(knowledge.Get("dismantle_counts") is GdDict)) return false;
            foreach (var entry in known) if (!(entry.Key is string) || !(entry.Value is bool b) || !b) return false;
            var channels = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in jobs)
            {
                if (!(entry.Value is GdDict job) || job.GetString("job_id") != V.Str(entry.Key) || !Text(job, "recipe_id") ||
                    job.GetString("run_id") != state.GetString("run_id") || job.GetString("actor_id") != state.GetString("actor_id") ||
                    !Text(job, "station_id") || !Text(job, "station_owner_id") || !Text(job, "station_kind") || !(job.Get("resume_required") is bool)) return false;
                string channel = job.GetString("channel"), status = job.GetString("status");
                if (channel != "station" && channel != "field") return false;
                if (job.GetString("input_state") == "unpaid")
                {
                    if (status != "unpaid" || job.GetString("payment_commit_id").Length != 0 || job.Has("consumed")) return false;
                    if (!queues.GetArrayOrEmpty(channel).Contains(job.Get("job_id"))) return false;
                    continue;
                }
                if (job.GetString("input_state") != "paid" || !new[] { "running", "paused", "completed_pending_delivery", "completed_delivered", "cancelled" }.Contains(status)) return false;
                if (!Terminal(job) && !channels.Add(channel)) return false;
                if (!(job.Get("progress_seconds") is double progress) || !Finite(progress) || !(job.Get("required_seconds") is double duration) || !Finite(duration) || duration <= 0 || progress < 0 || progress > duration ||
                    !(job.Get("quality_score") is double quality) || !Finite(quality) || quality < 0 || quality > 1 ||
                    !(job.Get("quality_multiplier") is double multiplier) || !Finite(multiplier) || multiplier <= 0 || !Integer(job, "start_skill") || !Integer(job, "start_tier") || !(job.Get("start_known") is bool startKnown) || !startKnown) return false;
                GdDict definition = job.GetDictOrEmpty("recipe_definition");
                if (definition.GetString("recipe_id") != job.GetString("recipe_id") || Hash(definition) != job.GetString("recipe_hash") ||
                    !TryRecipeIngredients(definition, out GdDict ingredients) || !Equal(ingredients, job.Get("consumed")) ||
                    job.GetDictOrEmpty("consumed").Values.Any(value => !(value is long quantity) || quantity <= 0) ||
                    !TryPositiveWholeQuantity(definition.GetDictOrEmpty("produces").Get("quantity"), out _) || definition.GetFloat("craft_time_seconds") != duration) return false;
                if (!Integer(job, "start_level") || !(job.Get("start_powered") is bool) || !(job.Get("material_quality") is double material) || !Finite(material) ||
                    job.GetInt("start_tier") < definition.GetInt("station_tier_min") || job.GetString("station_kind") != "field_crafting" && job.GetInt("start_skill") < definition.GetInt("required_skill_level")) return false;
                GdDict qualityResult = new QualityTierResolver().Resolve(material, job.GetInt("start_skill"), job.GetInt("start_level"), job.GetBool("start_powered"));
                if (qualityResult.GetFloat("score") != quality || qualityResult.GetString("tier") != job.GetString("quality_tier") || qualityResult.GetFloat("multiplier") != multiplier) return false;
                if (job.GetString("station_kind") == "field_crafting" && (job.GetInt("start_level") != 0 || job.GetBool("start_powered"))) return false;
                GdDict receipt = receipts.Get(job.GetString("payment_commit_id")) as GdDict;
                if (receipt == null || !IsPayment(receipt.GetDictOrEmpty("result")) || !Equal(receipt.GetDictOrEmpty("result").Get("payment"), Payment(job))) { reason = "invalid_paid_payment_binding"; return false; }
                GdDict latestStep = receipts.Values.OfType<GdDict>().FirstOrDefault(row => InternalReceipt(row, job.GetString("job_id")));
                if (progress != (latestStep?.GetDictOrEmpty("result").GetFloat("progress_seconds") ?? 0.0)) { reason = "invalid_paid_progress_binding"; return false; }
                string completion = job.GetString("completion_commit_id");
                if (completion != "craft:complete:" + job.GetString("job_id")) return false;
                if (status == "completed_pending_delivery" || status == "completed_delivered") if (progress != duration) return false;
                if (status == "completed_delivered")
                {
                    GdDict done = receipts.GetDictOrEmpty(completion).GetDictOrEmpty("result");
                    if (done.GetString("operation") != "craft_complete" || done.GetString("job_id") != job.GetString("job_id") || !Equal(done.Get("output"), definition.Get("produces"))) return false;
                }
                else if (receipts.Has(completion)) return false;
                if (status == "cancelled")
                {
                    GdDict cancelled = receipts.GetDictOrEmpty(job.GetString("terminal_commit_id")).GetDictOrEmpty("result");
                    if (cancelled.GetString("operation") != "craft_cancel" || cancelled.GetString("job_id") != job.GetString("job_id")) return false;
                }
            }
            var queued = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in queues)
            {
                if ((V.Str(entry.Key) != "station" && V.Str(entry.Key) != "field") || !(entry.Value is GdArray ids)) return false;
                foreach (object id in ids) if (!(id is string s) || !queued.Add(s) || !jobs.Has(s) || jobs.GetDictOrEmpty(s).GetString("input_state") != "unpaid" || jobs.GetDictOrEmpty(s).GetString("channel") != V.Str(entry.Key)) return false;
            }
            foreach (var entry in legacy)
            {
                if (!(entry.Value is GdDict row) || row.GetString("reconciliation_id") != V.Str(entry.Key) || row.GetString("payment_state") != "unverified" || row.GetString("status") != "paused_unverified" ||
                    !Text(row, "source_id") || !Text(row, "source_hash") || !Text(row, "source_path") || !row.Has("original_record") || !(row.Get("original_context") is GdDict) ||
                    row.GetString("original_hash") != Hash(row.Get("original_record")) || row.GetString("reconciliation_id") != LegacyId(row.GetString("source_id"), row.GetString("source_hash"), row.GetString("source_path"))) return false;
                if (!new[] { "", "keep_paused", "abandoned", "started_fresh" }.Contains(row.GetString("disposition"))) return false;
                GdDict imported = receipts.GetDictOrEmpty(row.GetString("import_commit_id")).GetDictOrEmpty("result").GetDictOrEmpty("legacy_records").GetDictOrEmpty(entry.Key);
                foreach (string key in new[] { "original_record", "original_context", "original_hash", "source_id", "source_hash", "source_path", "recipe_id", "station_kind", "import_commit_id" })
                    if (!Equal(imported.Get(key), row.Get(key))) return false;
                if (row.GetString("decision_commit_id").Length > 0)
                {
                    GdDict effect = receipts.GetDictOrEmpty(row.GetString("decision_commit_id")).GetDictOrEmpty("result");
                    if (effect.GetString("operation") != "craft_legacy_decision" || effect.GetString("reconciliation_id") != V.Str(entry.Key)) return false;
                    string expected = effect.GetString("decision") == "start_fresh" ? "started_fresh" : effect.GetString("decision") == "abandon" ? "abandoned" : effect.GetString("decision") == "keep_paused" ? "keep_paused" : "invalid";
                    if (row.GetString("disposition") != expected) return false;
                }
                else if (row.GetString("disposition").Length > 0) return false;
            }
            if (!ReceiptTargets(domain)) { reason = "invalid_paid_receipt_target"; return false; }
            var rewardContext = new PaidCraftRewardProof.ValidationContext();
            if (!PaidCraftRewardProof.ValidateHistory(state, domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("training"), domain, rewardContext)) { reason = "invalid_reward_history"; return false; }
            foreach (GdDict receipt in receipts.Values.OfType<GdDict>())
                if (receipt.GetDictOrEmpty("result").GetString("operation") == "craft_complete" && !PaidCraftRewardProof.ValidateReward(state, jobs.GetDictOrEmpty(receipt.GetDictOrEmpty("result").GetString("job_id")), receipt.GetDictOrEmpty("result"), rewardContext))
                { reason = "invalid_reward_proof"; return false; }
            if (!RunSession.ValidatePaidMirrors(domain.GetDictOrEmpty("participating_state"))) { reason = "paid_projection_mismatch"; return false; }
            reason = "ok"; return true;
        }
        public static string LegacyId(string source, string hash, string path) => "legacy:" + Hash(GdArray.Of(source, hash, path));

        static bool IsPayment(GdDict effect) => effect.GetString("operation") == "craft_start" || effect.GetString("operation") == "craft_legacy_decision" && effect.GetString("decision") == "start_fresh";
        static bool ReceiptTargets(GdDict domain)
        {
            GdDict state = State(domain), jobs = state.GetDictOrEmpty("jobs"), legacy = state.GetDictOrEmpty("legacy");
            foreach (var entry in domain.GetDictOrEmpty("receipts"))
            {
                var receipt = (GdDict)entry.Value;
                GdDict effect = receipt.GetDictOrEmpty("result"), command = receipt.GetDictOrEmpty("command");
                string op = effect.GetString("operation"), id = V.Str(entry.Key), action = command.GetString("action");
                if (!IsOperation(op)) continue;
                if (effect.Has("job_id"))
                {
                    GdDict job = jobs.GetDictOrEmpty(effect.GetString("job_id"));
                    if (job.IsEmpty || effect.Has("recipe_id") && effect.GetString("recipe_id") != job.GetString("recipe_id")) return false;
                    if ((action == "start" || action == "enqueue") && (command.GetString("recipe_id") != job.GetString("recipe_id") || command.GetString("station_kind") != job.GetString("station_kind"))) return false;
                    if (IsPayment(effect) && (job.GetString("input_state") != "paid" || job.GetString("payment_commit_id") != id || !Equal(effect.Get("payment"), Payment(job)))) return false;
                    if (op == "craft_cancel" && (job.GetString("status") != "cancelled" || job.GetString("terminal_commit_id") != id)) return false;
                    if (op == "craft_complete" && (job.GetString("status") != "completed_delivered" || job.GetString("completion_commit_id") != id)) return false;
                    if ((op == "craft_resume" || op == "craft_progress") && job.GetString("input_state") != "paid") return false;
                }
                if (op == "craft_legacy_decision")
                {
                    GdDict row = legacy.GetDictOrEmpty(effect.GetString("reconciliation_id"));
                    if (row.IsEmpty || effect.GetString("decision") != "keep_paused" && row.GetString("decision_commit_id") != id) return false;
                    if (effect.GetString("decision") == "start_fresh")
                    {
                        GdDict job = jobs.GetDictOrEmpty(effect.GetString("job_id"));
                        if (row.GetString("recipe_id") != job.GetString("recipe_id") || row.GetString("station_kind") != job.GetString("station_kind")) return false;
                    }
                }
                if (op == "craft_legacy_import")
                    foreach (var imported in effect.GetDictOrEmpty("legacy_records"))
                    {
                        if (!(imported.Value is GdDict original)) return false;
                        GdDict row = legacy.GetDictOrEmpty(imported.Key);
                        if (row.IsEmpty || row.GetString("import_commit_id") != id || original.GetString("source_id") != command.GetString("source_id") || original.GetString("source_hash") != command.GetString("source_hash")) return false;
                        GdDict expected = row.DeepCopy(); expected["disposition"] = ""; expected["decision_commit_id"] = "";
                        if (!Equal(expected, original)) return false;
                    }
            }
            return true;
        }

        public static bool Conserved(GdDict before, GdDict after, GdDict effect)
        {
            string op = effect.GetString("operation"), id = effect.GetString("job_id");
            if (!IsOperation(op) || after.GetInt("revision") != before.GetInt("revision") + 1) return false;
            bool internalStep = (op == "craft_progress" || op == "craft_block") && effect.GetBool("internal");
            int removed = 0;
            foreach (var receipt in before.GetDictOrEmpty("receipts"))
            {
                if (Equal(receipt.Value, after.GetDictOrEmpty("receipts").Get(receipt.Key))) continue;
                if (!internalStep || after.GetDictOrEmpty("receipts").Has(receipt.Key) || !(receipt.Value is GdDict row) || !InternalReceipt(row, id)) return false;
                removed++;
            }
            int retained = before.GetDictOrEmpty("receipts").Count - removed;
            if (after.GetDictOrEmpty("receipts").Count < retained || after.GetDictOrEmpty("receipts").Count > retained + 1) return false;
            foreach (string key in new[] { "schema_version", "domain_mode", "registry", "holders", "machinery", "physical_slots", "component_work", "registered_owners" })
                if (!Equal(before.Get(key), after.Get(key))) return false;
            GdDict a = before.GetDictOrEmpty("participating_state"), b = after.GetDictOrEmpty("participating_state"), old = State(before), next = State(after);
            if (!Equal(old.Get("run_id"), next.Get("run_id")) || !Equal(old.Get("actor_id"), next.Get("actor_id")) ||
                after.GetInt("command_sequence") != before.GetInt("command_sequence") + 1) return false;
            if (!Equal(a.Get("manual_study"), b.Get("manual_study")) || !Equal(a.Get("auxiliary_services"), b.Get("auxiliary_services"))) return false;
            if (!Equal(a.Get("stacks"), b.Get("stacks")) || !Equal(old.Get("knowledge"), next.Get("knowledge"))) return false;
            GdDict job = next.GetDictOrEmpty("jobs").GetDictOrEmpty(id), prior = old.GetDictOrEmpty("jobs").GetDictOrEmpty(id);
            bool start = op == "craft_start" || op == "craft_legacy_decision" && effect.GetString("decision") == "start_fresh";
            GdDict expectedItems = a.GetDictOrEmpty("inventory").GetDictOrEmpty("items").DeepCopy();
            if (start)
            {
                if (!prior.IsEmpty && prior.GetString("input_state") != "unpaid" || job.GetString("input_state") != "paid" || job.GetFloat("progress_seconds") != 0 || !Equal(Payment(job), effect.Get("payment"))) return false;
                foreach (var item in job.GetDictOrEmpty("consumed"))
                {
                    if (!(item.Value is long count) || count <= 0 || expectedItems.GetInt(item.Key) < count) return false;
                    long remain = expectedItems.GetInt(item.Key) - count;
                    if (remain == 0) expectedItems.Erase(item.Key); else expectedItems[item.Key] = remain;
                }
            }
            else if (op == "craft_complete")
            {
                if (prior.IsEmpty || Terminal(prior) || prior.GetBool("resume_required") || prior.GetFloat("progress_seconds") != prior.GetFloat("required_seconds") || job.GetString("status") != "completed_delivered") return false;
                GdDict output = job.GetDictOrEmpty("recipe_definition").GetDictOrEmpty("produces");
                if (!Equal(output, effect.Get("output"))) return false;
                string item = output.GetString("item_id"); long count = output.GetInt("quantity");
                if (count <= 0 || expectedItems.GetInt(item) > long.MaxValue - count) return false;
                expectedItems[item] = expectedItems.GetInt(item) + count;
            }
            if (!Equal(expectedItems, b.GetDictOrEmpty("inventory").Get("items"))) return false;
            var expectedInventory = new InventoryState(); expectedInventory.ApplySummary(a.GetDictOrEmpty("inventory"));
            double equipmentMass = a.GetDictOrEmpty("inventory").GetFloat("total_weight") - expectedInventory.GetTotalWeight();
            expectedInventory.ComponentMass = () => equipmentMass; expectedInventory.Items.Clear();
            foreach (var item in expectedItems) expectedInventory.Items[item.Key] = item.Value;
            if (!Equal(expectedInventory.GetSummary(), b.Get("inventory"))) return false;
            if (op != "craft_complete" && (!Equal(a.Get("progression"), b.Get("progression")) || !Equal(a.Get("training"), b.Get("training")))) return false;
            if (op != "craft_complete" && !Equal(a.Get("spoilage"), b.Get("spoilage"))) return false;
            if (op == "craft_complete" && !ValidReward(a, b, job, effect)) return false;
            if (!PaidCraftRewardProof.Conserved(a, b, effect)) return false;
            if (!start && op != "craft_enqueue" && op != "craft_legacy_import" && op != "craft_legacy_decision" && prior.IsEmpty) return false;
            if (!prior.IsEmpty && prior.GetString("input_state") == "paid" && !Equal(Payment(prior), Payment(job))) return false;
            if (op == "craft_progress" && (Terminal(prior) || prior.GetBool("resume_required") || job.GetFloat("progress_seconds") < prior.GetFloat("progress_seconds"))) return false;
            if (internalStep && effect.GetFloat("progress_seconds") != job.GetFloat("progress_seconds")) return false;
            if (op == "craft_cancel" && (Terminal(prior) || job.GetString("status") != "cancelled")) return false;
            if (!start && op != "craft_enqueue" && !prior.IsEmpty)
            {
                GdDict expectedJob = prior.DeepCopy();
                if (op == "craft_progress")
                {
                    double delta = effect.GetFloat("delta_seconds");
                    if (!Finite(delta) || delta <= 0) return false;
                    expectedJob["progress_seconds"] = Math.Min(prior.GetFloat("required_seconds"), prior.GetFloat("progress_seconds") + delta);
                    expectedJob["status"] = expectedJob.GetFloat("progress_seconds") == expectedJob.GetFloat("required_seconds") ? "completed_pending_delivery" : "running";
                    expectedJob["blocked_reason"] = "";
                }
                if (op == "craft_resume") { expectedJob["resume_required"] = false; expectedJob["blocked_reason"] = ""; expectedJob["status"] = prior.GetFloat("progress_seconds") == prior.GetFloat("required_seconds") ? "completed_pending_delivery" : "running"; }
                if (op == "craft_block") { expectedJob["blocked_reason"] = effect.Get("reason"); if (prior.GetString("input_state") == "paid") expectedJob["status"] = "paused"; }
                if (op == "craft_cancel") { expectedJob["status"] = "cancelled"; expectedJob["resume_required"] = false; expectedJob["blocked_reason"] = "cancelled"; expectedJob["terminal_commit_id"] = job.Get("terminal_commit_id"); }
                if (op == "craft_complete") { expectedJob["status"] = "completed_delivered"; expectedJob["resume_required"] = false; expectedJob["blocked_reason"] = ""; }
                if (!Equal(expectedJob, job)) return false;
            }
            foreach (var entry in old.GetDictOrEmpty("jobs"))
            {
                if (!next.GetDictOrEmpty("jobs").Has(entry.Key)) return false;
                if (V.Str(entry.Key) == id) continue;
                var expected = ((GdDict)entry.Value).DeepCopy();
                if (op == "craft_cancel" && expected.GetString("input_state") == "unpaid" && expected.GetString("channel") == job.GetString("channel")) expected["resume_required"] = true;
                if (!Equal(expected, next.GetDictOrEmpty("jobs").Get(entry.Key))) return false;
            }
            if (next.GetDictOrEmpty("jobs").Count != old.GetDictOrEmpty("jobs").Count + ((start || op == "craft_enqueue") && prior.IsEmpty ? 1 : 0)) return false;
            GdDict expectedQueues = old.GetDictOrEmpty("queues").DeepCopy();
            if (op == "craft_enqueue") expectedQueues.GetArrayOrEmpty(job.GetString("channel")).Add(id);
            if (start && !prior.IsEmpty) expectedQueues.GetArrayOrEmpty(job.GetString("channel")).Remove(id);
            if (!Equal(expectedQueues, next.Get("queues"))) return false;
            foreach (var entry in old.GetDictOrEmpty("legacy"))
            {
                GdDict original = (GdDict)entry.Value, changed = next.GetDictOrEmpty("legacy").GetDictOrEmpty(entry.Key);
                if (op == "craft_legacy_decision" && V.Str(entry.Key) == effect.GetString("reconciliation_id"))
                {
                    foreach (string key in new[] { "original_record", "original_context", "original_hash", "source_id", "source_hash", "source_path", "reconciliation_id", "status", "payment_state" })
                        if (!Equal(original.Get(key), changed.Get(key))) return false;
                }
                else if (!Equal(original, changed)) return false;
            }
            if (op != "craft_legacy_import" && next.GetDictOrEmpty("legacy").Count != old.GetDictOrEmpty("legacy").Count) return false;
            return true;
        }

        internal static bool ValidReward(GdDict before, GdDict after, GdDict job, GdDict effect, bool checkSpoilage = true, PaidCraftRewardProof.ValidationContext context = null)
        {
            context = context ?? new PaidCraftRewardProof.ValidationContext();
            GdDict record = effect.Get("training_record") as GdDict;
            GdDict beforeTraining = before.GetDictOrEmpty("training"), afterTraining = after.GetDictOrEmpty("training");
            if (!PaidCraftRewardProof.ValidAppend(beforeTraining, afterTraining, record, job.GetString("completion_commit_id")) ||
                !new TrainingEventBus().ApplySummary(beforeTraining) || !PaidCraftRewardProof.ExactKeys(afterTraining, "log", "dropped", "xp_total", "event_count") ||
                !(beforeTraining.Get("dropped") is long dropped) || dropped < 0 || !(beforeTraining.Get("xp_total") is long xp) || xp < 0 ||
                !(afterTraining.Get("dropped") is long afterDropped) || afterDropped < 0 || !(afterTraining.Get("xp_total") is long afterXp) || afterXp < 0 ||
                !PaidCraftRewardProof.ReconstructReward(before.GetDictOrEmpty("progression"), after.GetDictOrEmpty("progression"),
                    dropped, xp, afterDropped, afterXp, beforeTraining.GetInt("event_count"), job, effect, context)) return false;
            if (!checkSpoilage) return true;
            string output = job.GetDictOrEmpty("recipe_definition").GetDictOrEmpty("produces").GetString("item_id");
            InventoryState inventory = context.Inventory;
            GdDict oldSpoilage = before.GetDictOrEmpty("spoilage"), newSpoilage = after.GetDictOrEmpty("spoilage");
            var expectedSpoilage = oldSpoilage.DeepCopy();
            if ((inventory.GetCategory(output) == "food" || inventory.GetCategory(output) == "drink") && !expectedSpoilage.GetDictOrEmpty("foods").Has(output))
            {
                var food = new FoodState(); GdDict configuration = inventory.GetDefinition(output).DeepCopy(); configuration["item_id"] = output; food.Configure(configuration);
                expectedSpoilage.GetDictOrEmpty("foods")[output] = food.GetSummary();
                expectedSpoilage["rotten_present"] = expectedSpoilage.GetDictOrEmpty("foods").Values.OfType<GdDict>().Any(row => row.GetInt("stage") == (long)FoodState.Stage.ROTTEN);
            }
            return Equal(expectedSpoilage, newSpoilage);
        }
    }
}
