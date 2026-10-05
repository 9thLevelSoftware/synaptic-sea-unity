using System;
using System.Linq;
using System.Collections.Generic;
using SynapticSea.Core.Session;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Diagnostic F08 owner. Hardware and finite rack stock have one canonical owner.</summary>
    public static class AuxiliaryServiceState
    {
        [ThreadStatic] static ValidationContext _validation;
        internal sealed class ReceiptProof
        {
            internal GdDict Receipt;
            internal bool Valid;
        }
        static bool ExactReceipt(object left, object right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (left.GetType() != right.GetType()) return false;
            if (left is GdDict ld && right is GdDict rd)
            {
                if (ld.Count != rd.Count) return false;
                foreach (var row in ld) if (!rd.TryGetValue(row.Key, out object value) || !ExactReceipt(row.Value, value)) return false;
                return true;
            }
            if (left is GdArray la && right is GdArray ra)
            {
                if (la.Count != ra.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!ExactReceipt(la[i], ra[i])) return false;
                return true;
            }
            return left.Equals(right);
        }
        internal sealed class ValidationContext : IDisposable
        {
            readonly ValidationContext _previous;
            internal readonly Dictionary<string, ReceiptProof> Receipts = new Dictionary<string, ReceiptProof>(StringComparer.Ordinal);
            internal GdDict Domain;
            internal GdDict Descriptors;
            internal GdDict InventoryDefinitions;
            internal ValidationContext() { _previous = _validation; _validation = this; }
            public void Dispose() { _validation = _previous; }
        }
        internal static IDisposable BeginValidation() => new ValidationContext();
        public const string SourcePath = "res://data/diagnostics/earned-services-home-v1/gameplay_slice.json";
        public static GdDict State(GdDict domain) => domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("auxiliary_services");
        internal static bool Eq(object a, object b) => V.VariantEquals(a, b);
        static bool Keys(GdDict d, params string[] keys) => PaidCraftRewardProof.ExactKeys(d, keys);
        static bool Finite(object o, out double n) { n = o is double v ? v : double.NaN; return !double.IsNaN(n) && !double.IsInfinity(n); }
        static long Count(object value)
        {
            double n = V.F64(value, double.NaN);
            if (!V.IsNumber(value) || double.IsNaN(n) || double.IsInfinity(n) || n < 0 || n > 100 || n != Math.Floor(n)) throw new ArgumentException("invalid_aux_count");
            return (long)n;
        }
        public static GdDict Descriptors(GdArray rows)
        {
            var result = new GdDict();
            foreach (object raw in rows)
            {
                if (!(raw is GdDict row) || !Keys(row, "id", "kind", "room_id", "approach_cell", "position_offset", "required_seconds", "required_tools", "materials_consumed", "reward")) throw new ArgumentException("invalid_aux_descriptor");
                string id = row.GetString("id"), kind = row.GetString("kind");
                if (id.Length == 0 || result.Has(id) || row.GetString("room_id").Length == 0 || !(row.Get("required_seconds") is double seconds) || seconds != (kind == "utility" ? 12.0 : 8.0) ||
                    !Eq(row.Get("required_tools"), GdArray.Of("crowbar")) || (kind != "utility" && kind != "recovery_rack")) throw new ArgumentException("invalid_aux_descriptor");
                foreach (string key in new[] { "approach_cell", "position_offset" })
                {
                    if (!(row.Get(key) is GdArray cell) || cell.Count != 3 || cell.Any(x => !V.IsNumber(x) || double.IsNaN(V.F64(x)) || double.IsInfinity(V.F64(x)))) throw new ArgumentException("invalid_aux_anchor");
                    if (key == "approach_cell" && cell.Any(x => V.F64(x) != Math.Floor(V.F64(x)))) throw new ArgumentException("invalid_aux_cell");
                }
                GdDict descriptor = row.DeepCopy(), materials = descriptor.GetDictOrEmpty("materials_consumed"), reward = descriptor.GetDictOrEmpty("reward");
                if (kind == "utility")
                {
                    if (!Keys(materials, "scrap_metal", "wiring_bundle") || Count(materials.Get("scrap_metal")) != 1 || Count(materials.Get("wiring_bundle")) != 1 || !Keys(reward, "repair_xp") || Count(reward.Get("repair_xp")) != 60) throw new ArgumentException("invalid_aux_policy");
                    materials["scrap_metal"] = 1L; materials["wiring_bundle"] = 1L; reward["repair_xp"] = 60L;
                }
                else
                {
                    GdDict items = reward.GetDictOrEmpty("items");
                    if (!materials.IsEmpty || !Keys(reward, "items") || !Keys(items, "scrap_metal", "wiring_bundle") || Count(items.Get("scrap_metal")) != 4 || Count(items.Get("wiring_bundle")) != 4) throw new ArgumentException("invalid_aux_policy");
                    items["scrap_metal"] = 4L; items["wiring_bundle"] = 4L;
                }
                result[id] = descriptor;
            }
            return result;
        }
        static GdDict TrustedDescriptors()
        {
            if (_validation?.Descriptors != null) return _validation.Descriptors;
            var descriptors = Descriptors(CatalogRegistry.LoadDict(SourcePath)?.GetArrayOrEmpty("auxiliary_services") ?? new GdArray());
            if (_validation != null) _validation.Descriptors = descriptors;
            return descriptors;
        }
        public static GdDict New(string run, string actor, GdDict descriptors)
        {
            if (!Eq(descriptors, TrustedDescriptors()) || descriptors.Count != 6) throw new ArgumentException("aux_source_mismatch");
            var services = new GdDict();
            foreach (var pair in descriptors)
            {
                var d = (GdDict)pair.Value;
                var row = new GdDict { { "kind", d.GetString("kind") }, { "descriptor_sha256", PaidCraftingState.Hash(d) }, { "hardware_ready", false }, { "completion_commit_id", "" } };
                if (d.GetString("kind") == "recovery_rack") { row["released"] = false; row["remaining"] = new GdDict(); row["accepted"] = new GdDict(); }
                services[pair.Key] = row;
            }
            return new GdDict { { "schema_version", 1L }, { "run_id", run }, { "actor_id", actor }, { "source_sha256", PaidCraftingState.Hash(descriptors) }, { "descriptors", descriptors.DeepCopy() }, { "services", services }, { "job", new GdDict() } };
        }
        public static bool IsOperation(string op) => new[] { "aux_start", "aux_progress", "aux_pause", "aux_complete", "aux_take" }.Contains(op);
        internal static string CompletionId(GdDict state, string id) => "auxiliary:complete:" + PaidCraftingState.Hash(GdArray.Of(state.GetString("run_id"), state.GetString("actor_id"), id));
        internal static GdDict Latest(GdDict domain, string id, string operation = "") => domain.GetDictOrEmpty("receipts").Values.OfType<GdDict>()
            .Where(r => r.GetDictOrEmpty("result").GetString("service_id") == id && IsOperation(r.GetDictOrEmpty("result").GetString("operation")) && (operation == "" || r.GetDictOrEmpty("result").GetString("operation") == operation))
            .OrderByDescending(r => r.GetInt("revision")).FirstOrDefault() ?? new GdDict();
        internal static string Origin(GdDict domain, string id) => domain.GetDictOrEmpty("receipts").Where(e => e.Value is GdDict r && r.GetDictOrEmpty("result").GetString("operation") == "aux_start" && r.GetDictOrEmpty("result").GetString("service_id") == id && r.GetDictOrEmpty("result").GetDictOrEmpty("job_after").GetFloat("progress_seconds") == 0)
            .OrderBy(e => ((GdDict)e.Value).GetInt("revision")).Select(e => V.Str(e.Key)).FirstOrDefault() ?? "";
        static PlayerProgressionState Progression(GdDict before)
        {
            if (!ClassDefinition.LoadAll().TryGetValue(before.GetString("class_id"), out var definition)) throw new ArgumentException("aux_class_missing");
            var model = new PlayerProgressionState(); model.Configure(definition, PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            if (!PaidCraftRewardProof.CopyProgressionExact(model, before)) throw new ArgumentException("invalid_aux_progression"); return model;
        }
        internal static GdDict Apply(GdDict domain, string operation, string id, double delta = 0, double elapsed = 0, double speed = 1, double staminaBefore = 0, double staminaAfter = 0, string reason = "")
        {
            var state = State(domain); var descriptor = state.GetDictOrEmpty("descriptors").GetDictOrEmpty(id);
            if (descriptor.IsEmpty) throw new ArgumentException("unknown_aux_service");
            var p = domain.GetDictOrEmpty("participating_state");
            var effect = new GdDict { { "operation", operation }, { "reason", reason }, { "service_id", id }, { "descriptor_sha256", PaidCraftingState.Hash(descriptor) },
                { "job_before", state.GetDictOrEmpty("job").DeepCopy() }, { "service_before", state.GetDictOrEmpty("services").GetDictOrEmpty(id).DeepCopy() },
                { "inventory_before", p.GetDictOrEmpty("inventory").DeepCopy() }, { "progression_before", p.GetDictOrEmpty("progression").DeepCopy() }, { "training_before", p.GetDictOrEmpty("training").DeepCopy() },
                { "delta_seconds", delta }, { "elapsed_seconds", elapsed }, { "speed", speed }, { "stamina_before", staminaBefore }, { "stamina_after", staminaAfter },
                { "origin_receipt_id", Origin(domain, id) }, { "work_receipt_id", operation == "aux_complete" || operation == "aux_take" ? Latest(domain, id).GetString("commit_id") : "" }, { "eligible_steps", Latest(domain, id, "aux_progress").GetDictOrEmpty("result").GetInt("eligible_steps") }, { "accepted", new GdDict() }, { "training_record", null } };
            var job = state.GetDictOrEmpty("job").DeepCopy(); var service = state.GetDictOrEmpty("services").GetDictOrEmpty(id).DeepCopy();
            // The context expires at the end of this one admission; post-hook admission
            // reloads the exact merged policy. Only private reconstruction models borrow it.
            InventoryState inventory;
            { inventory = _validation == null ? new InventoryState() : new InventoryState(_validation.InventoryDefinitions ?? (_validation.InventoryDefinitions = ItemDefs.LoadDefinitions())); if (!inventory.ApplySummary(p.GetDictOrEmpty("inventory"))) throw new ArgumentException("invalid_aux_inventory"); }
            double duration = descriptor.GetFloat("required_seconds");
            if (operation == "aux_start")
            {
                if (service.GetString("completion_commit_id") != "" || !job.IsEmpty && job.GetString("status") != "completed" && job.GetString("service_id") != id) throw new ArgumentException("aux_completed_or_pending");
                job = new GdDict { { "service_id", id }, { "progress_seconds", job.GetString("service_id") == id && job.GetString("status") != "completed" ? job.GetFloat("progress_seconds") : 0.0 }, { "eligible_seconds", job.GetString("service_id") == id && job.GetString("status") != "completed" ? job.GetFloat("eligible_seconds") : 0.0 }, { "status", "running" }, { "resume_required", false }, { "reason", "" } };
            }
            else if (operation == "aux_pause") { job["status"] = "paused"; job["resume_required"] = true; job["reason"] = reason; }
            else if (operation == "aux_progress")
            {
                if (job.GetString("service_id") != id || job.GetString("status") != "running" || job.GetBool("resume_required") || delta <= 0 || elapsed <= 0 || speed <= 0 || speed > 1 || Math.Abs(delta - elapsed * speed) > 1e-8 || job.GetFloat("progress_seconds") + delta > duration + 1e-8 || Math.Abs(staminaAfter - Math.Max(0, staminaBefore - 8 * elapsed)) > 1e-8) throw new ArgumentException("invalid_aux_work");
                job["progress_seconds"] = Math.Min(duration, job.GetFloat("progress_seconds") + delta); job["eligible_seconds"] = job.GetFloat("eligible_seconds") + elapsed;
                effect["eligible_steps"] = effect.GetInt("eligible_steps") + 1;
            }
            else if (operation == "aux_complete")
            {
                if (job.GetString("service_id") != id || job.GetString("status") != "running" || job.GetBool("resume_required") || job.GetFloat("progress_seconds") != duration || service.GetString("completion_commit_id") != "" || inventory.GetQuantity("crowbar") < 1) throw new ArgumentException("aux_incomplete");
                string commit = CompletionId(state, id); service["completion_commit_id"] = commit;
                job["status"] = "completed";
                if (descriptor.GetString("kind") == "utility")
                {
                    foreach (var part in descriptor.GetDictOrEmpty("materials_consumed")) if (inventory.RemoveItem(V.Str(part.Key), V.I64(part.Value)) != V.I64(part.Value)) throw new ArgumentException("aux_missing_materials");
                    service["hardware_ready"] = true;
                    var progression = Progression(p.GetDictOrEmpty("progression")); progression.GrantXp("repair", 60);
                    p["progression"] = progression.GetSummary();
                    TrainingEventBus bus;
                    { bus = new TrainingEventBus(); bus.Configure(); if (!bus.ApplySummary(p.GetDictOrEmpty("training"))) throw new ArgumentException("invalid_aux_training"); }
                    var row = new GdDict { { "event_id", "auxiliary_utility_repair" }, { "target_id", id }, { "skill_id", "repair" }, { "base_xp", 60L }, { "category", "technical" }, { "is_cross_training", false }, { "sequence", bus.GetEventCount() }, { "gated", false } };
                    bus.RecordApplied(row, commit); effect["training_record"] = bus.GetLog()[bus.GetLog().Count - 1]; p["training"] = bus.ToDict();
                    ManualStudyState.Intern(domain, effect.GetDictOrEmpty("progression_before")); ManualStudyState.Intern(domain, progression.GetSummary());
                    PaidCraftRewardProof.RefreshCurrent(PaidCraftingState.State(domain), bus.ToDict());
                }
                else { service["released"] = true; service["remaining"] = descriptor.GetDictOrEmpty("reward").GetDictOrEmpty("items").DeepCopy(); service["accepted"] = new GdDict { { "scrap_metal", 0L }, { "wiring_bundle", 0L } }; }
            }
            else if (operation == "aux_take")
            {
                if (descriptor.GetString("kind") != "recovery_rack" || !service.GetBool("released")) throw new ArgumentException("rack_unreleased");
                foreach (var stock in service.GetDictOrEmpty("remaining").ToArray())
                {
                    long accepted = inventory.AddItem(V.Str(stock.Key), V.I64(stock.Value));
                    if (accepted == 0) continue;
                    effect.GetDictOrEmpty("accepted")[stock.Key] = accepted;
                    service.GetDictOrEmpty("remaining")[stock.Key] = V.I64(stock.Value) - accepted;
                    service.GetDictOrEmpty("accepted")[stock.Key] = service.GetDictOrEmpty("accepted").GetInt(V.Str(stock.Key)) + accepted;
                }
                if (effect.GetDictOrEmpty("accepted").IsEmpty) throw new ArgumentException("inventory_full_or_rack_empty");
            }
            else throw new ArgumentException("unknown_aux_operation");
            state["job"] = job; state.GetDictOrEmpty("services")[id] = service; p["inventory"] = inventory.GetSummary();
            foreach (string key in new[] { "job", "service", "inventory", "progression", "training" }) effect[key + "_after"] = (key == "job" ? job : key == "service" ? service : p.GetDictOrEmpty(key)).DeepCopy();
            return effect;
        }
        internal static void PruneProgress(GdDict domain, string id)
        {
            foreach (var row in domain.GetDictOrEmpty("receipts").ToArray()) if (row.Value is GdDict receipt && receipt.GetDictOrEmpty("result").GetString("operation") == "aux_progress" && receipt.GetDictOrEmpty("result").GetString("service_id") == id) domain.GetDictOrEmpty("receipts").Erase(row.Key);
        }
        internal static bool ValidReceipt(GdDict domain, GdDict receipt, string key)
        {
            // One private immutable admission only. Bind the domain identity, then
            // compare detached exact typed receipt bytes without repeatedly serializing
            // the same large proof. No result survives post-hook/new-domain admission.
            if (_validation != null && !ReferenceEquals(_validation.Domain, domain))
            {
                _validation.Receipts.Clear();
                _validation.Domain = domain;
            }
                if (_validation != null && _validation.Receipts.TryGetValue(key, out var admitted) && ExactReceipt(admitted.Receipt, receipt)) return admitted.Valid;
            bool valid = ValidReceiptBody(domain, receipt, key);
            if (_validation != null) _validation.Receipts[key] = new ReceiptProof { Receipt = receipt.DeepCopy(), Valid = valid };
            return valid;
        }
        static bool ValidReceiptBody(GdDict domain, GdDict receipt, string key)
        {
            try
            {
                if (!Keys(receipt, "schema_version", "transaction_id", "commit_id", "command_id", "command", "command_hash", "revision", "result") || !(receipt.Get("schema_version") is long v) || v != 1 || receipt.GetString("transaction_id") != key || receipt.GetString("commit_id") != key || !(receipt.Get("revision") is long revision) || revision <= 0 || revision > domain.GetInt("revision")) return false;
                var command = receipt.GetDictOrEmpty("command"); var e = receipt.GetDictOrEmpty("result"); string op = e.GetString("operation"), id = e.GetString("service_id");
                if (!Keys(command, "command_id", "operation", "run_id", "actor_id", "service_id", "delta_seconds", "elapsed_seconds", "speed", "stamina_before", "stamina_after", "reason") || command.GetString("command_id") != receipt.GetString("command_id") || receipt.GetString("command_hash") != PaidCraftingState.Hash(command) || command.GetString("operation") != op || command.GetString("service_id") != id || command.GetString("run_id") != State(domain).GetString("run_id") || command.GetString("actor_id") != State(domain).GetString("actor_id") || !IsOperation(op)) return false;
                string expectedId = op == "aux_complete" ? CompletionId(State(domain), id) : "auxiliary:" + command.GetString("command_id"); if (key != expectedId) return false;
                if (!Keys(e, "operation", "reason", "service_id", "descriptor_sha256", "job_before", "service_before", "inventory_before", "progression_before", "training_before", "delta_seconds", "elapsed_seconds", "speed", "stamina_before", "stamina_after", "origin_receipt_id", "work_receipt_id", "eligible_steps", "accepted", "training_record", "job_after", "service_after", "inventory_after", "progression_after", "training_after")) return false;
                foreach (string field in new[] { "delta_seconds", "elapsed_seconds", "speed", "stamina_before", "stamina_after" }) if (!Finite(e.Get(field), out _) || !Eq(e.Get(field), command.Get(field))) return false;
                // Reconstruct the complete effect without copying unrelated historical receipts.
                // Apply only reads descriptors/receipts (Origin/Latest); all targets it writes below
                // are fresh. The caller's immutable admission copy is never mutated.
                var admittedState = State(domain);
                var proofState = new GdDict { { "run_id", admittedState.Get("run_id") }, { "actor_id", admittedState.Get("actor_id") },
                    { "descriptors", admittedState.Get("descriptors") }, { "job", e.GetDictOrEmpty("job_before").DeepCopy() },
                    { "services", new GdDict { { id, e.GetDictOrEmpty("service_before").DeepCopy() } } } };
                var p = new GdDict { { "auxiliary_services", proofState },
                    { "paid_crafting", new GdDict { { "reward_history", PaidCraftRewardProof.NewHistory() } } } };
                foreach (string field in new[] { "inventory", "progression", "training" }) p[field] = e.GetDictOrEmpty(field + "_before").DeepCopy();
                var proof = new GdDict { { "participating_state", p }, { "receipts", domain.Get("receipts") } };
                GdDict computed;
                { computed = Apply(proof, op, id, command.GetFloat("delta_seconds"), command.GetFloat("elapsed_seconds"), command.GetFloat("speed"), command.GetFloat("stamina_before"), command.GetFloat("stamina_after"), command.GetString("reason")); }
                // The cumulative count is validated against the retained origin and current proof below.
                computed["origin_receipt_id"] = e.Get("origin_receipt_id"); computed["eligible_steps"] = e.Get("eligible_steps"); computed["work_receipt_id"] = e.Get("work_receipt_id");
                { if (!Eq(computed, e)) return false; }
                if (op == "aux_complete" || op == "aux_take")
                {
                    var prior = domain.GetDictOrEmpty("receipts").GetDictOrEmpty(e.GetString("work_receipt_id")); var priorEffect = prior.GetDictOrEmpty("result");
                    if (prior.IsEmpty || prior.GetInt("revision") >= revision || priorEffect.GetString("service_id") != id) return false;
                    if (op == "aux_complete" && (!new[] { "aux_start", "aux_progress" }.Contains(priorEffect.GetString("operation")) || !Eq(priorEffect.Get("job_after"), e.Get("job_before")))) return false;
                    if (op == "aux_take" && (!new[] { "aux_complete", "aux_take" }.Contains(priorEffect.GetString("operation")) || !Eq(priorEffect.Get("service_after"), e.Get("service_before")))) return false;
                }
                if (op == "aux_progress" || op == "aux_complete")
                {
                    var origin = domain.GetDictOrEmpty("receipts").GetDictOrEmpty(e.GetString("origin_receipt_id")).GetDictOrEmpty("result");
                    if (origin.GetString("operation") != "aux_start" || origin.GetString("service_id") != id || origin.GetDictOrEmpty("job_after").GetFloat("progress_seconds") != 0 || !(e.Get("eligible_steps") is long steps) || steps < 1) return false;
                }
                return true;
            }
            catch (ArgumentException) { return false; }
        }
        internal static bool Conserved(GdDict before, GdDict after, GdDict effect)
        {
            try
            {
                if (before.GetInt("schema_version") != 5 || after.GetInt("schema_version") != 5 || after.GetInt("revision") != before.GetInt("revision") + 1 || after.GetInt("command_sequence") != before.GetInt("command_sequence") + 1) return false;
                var expected = before.DeepCopy(); var computed = Apply(expected, effect.GetString("operation"), effect.GetString("service_id"), effect.GetFloat("delta_seconds"), effect.GetFloat("elapsed_seconds"), effect.GetFloat("speed"), effect.GetFloat("stamina_before"), effect.GetFloat("stamina_after"), effect.GetString("reason"));
                if (!Eq(computed, effect)) return false;
                foreach (string key in new[] { "participating_state", "registry", "holders", "machinery", "physical_slots", "component_work", "registered_owners", "domain_mode" }) if (!Eq(expected.Get(key), after.Get(key))) return false;
                return true;
            }
            catch (ArgumentException) { return false; }
        }
        internal static bool Validate(GdDict domain, out string reason)
        {
            reason = "invalid_auxiliary_services";
            try
            {
                var s = State(domain); var p = domain.GetDictOrEmpty("participating_state"); var paid = PaidCraftingState.State(domain);
                if (!Keys(s, "schema_version", "run_id", "actor_id", "source_sha256", "descriptors", "services", "job") || !(s.Get("schema_version") is long v) || v != 1 || s.GetString("run_id") != paid.GetString("run_id") || s.GetString("actor_id") != paid.GetString("actor_id") || !Eq(s.Get("descriptors"), TrustedDescriptors()) || s.GetString("source_sha256") != PaidCraftingState.Hash(s.Get("descriptors"))) return false;
                GdDict services = s.GetDictOrEmpty("services"), descriptors = s.GetDictOrEmpty("descriptors"); if (services.Count != descriptors.Count) return false;
                foreach (var pair in descriptors)
                {
                    string id = V.Str(pair.Key); var d = (GdDict)pair.Value; var row = services.GetDictOrEmpty(id); bool rack = d.GetString("kind") == "recovery_rack";
                    if (!Keys(row, rack ? new[] { "kind", "descriptor_sha256", "hardware_ready", "completion_commit_id", "released", "remaining", "accepted" } : new[] { "kind", "descriptor_sha256", "hardware_ready", "completion_commit_id" }) || row.GetString("kind") != d.GetString("kind") || row.GetString("descriptor_sha256") != PaidCraftingState.Hash(d) || !(row.Get("hardware_ready") is bool)) return false;
                    string commit = row.GetString("completion_commit_id");
                    if (commit == "") { if (row.GetBool("hardware_ready") || rack && (row.GetBool("released") || !row.GetDictOrEmpty("remaining").IsEmpty || !row.GetDictOrEmpty("accepted").IsEmpty)) return false; }
                    else
                    {
                        var receipt = domain.GetDictOrEmpty("receipts").GetDictOrEmpty(commit); if (!ValidReceipt(domain, receipt, commit) || receipt.GetDictOrEmpty("result").GetString("operation") != "aux_complete" || receipt.GetDictOrEmpty("result").GetString("service_id") != id || commit != CompletionId(s, id)) return false;
                        if (!rack && !row.GetBool("hardware_ready") || rack && (!row.GetBool("released") || row.GetBool("hardware_ready"))) return false;
                        if (rack) foreach (var item in d.GetDictOrEmpty("reward").GetDictOrEmpty("items")) if (!(row.GetDictOrEmpty("remaining").Get(item.Key) is long n) || !(row.GetDictOrEmpty("accepted").Get(item.Key) is long a) || n < 0 || a < 0 || n + a != V.I64(item.Value)) return false;
                        var latest = Latest(domain, id, "aux_take"); if (rack && !Eq(row, latest.IsEmpty ? receipt.GetDictOrEmpty("result").Get("service_after") : latest.GetDictOrEmpty("result").Get("service_after"))) return false;
                    }
                }
                foreach (var pair in domain.GetDictOrEmpty("receipts")) if (pair.Value is GdDict receipt && IsOperation(receipt.GetDictOrEmpty("result").GetString("operation")) && !ValidReceipt(domain, receipt, V.Str(pair.Key))) return false;
                var job = s.GetDictOrEmpty("job");
                if (!job.IsEmpty)
                {
                    if (!Keys(job, "service_id", "progress_seconds", "eligible_seconds", "status", "resume_required", "reason") || !descriptors.Has(job.GetString("service_id")) || !Finite(job.Get("progress_seconds"), out double progress) || !Finite(job.Get("eligible_seconds"), out double elapsed) || progress < 0 || elapsed < progress || progress > descriptors.GetDictOrEmpty(job.GetString("service_id")).GetFloat("required_seconds") || !(job.Get("resume_required") is bool) || !(job.Get("reason") is string) || !new[] { "running", "paused", "completed" }.Contains(job.GetString("status"))) return false;
                    var latest = domain.GetDictOrEmpty("receipts").Values.OfType<GdDict>().Where(r => r.GetDictOrEmpty("result").GetString("service_id") == job.GetString("service_id") && IsOperation(r.GetDictOrEmpty("result").GetString("operation")) && r.GetDictOrEmpty("result").GetString("operation") != "aux_take").OrderByDescending(r => r.GetInt("revision")).FirstOrDefault();
                    if (latest == null) return false; var expected = latest.GetDictOrEmpty("result").GetDictOrEmpty("job_after").DeepCopy();
                    if (!Eq(expected, job)) { if (expected.GetString("status") == "completed") return false; expected["status"] = "paused"; expected["resume_required"] = true; expected["reason"] = "explicit_resume_required"; if (!Eq(expected, job)) return false; }
                }
                else if (domain.GetDictOrEmpty("receipts").Values.OfType<GdDict>().Any(r => IsOperation(r.GetDictOrEmpty("result").GetString("operation")))) return false;
                reason = "ok"; return true;
            }
            catch (ArgumentException) { return false; }
        }
    }
}
