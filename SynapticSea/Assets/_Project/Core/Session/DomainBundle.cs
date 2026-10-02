using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>Immutable component domain. Schema one retains detached compatibility; schema two binds diagnostic live views.</summary>
    public sealed class DomainBundle
    {
        readonly GdDict _summary;
        readonly GdDict _projections;
        public long Revision { get; }
        DomainBundle(GdDict summary, long revision, GdDict projections)
        {
            _summary = summary.DeepCopy();
            _projections = projections.DeepCopy();
            Revision = revision;
        }
        public GdDict GetSummary() => _summary.DeepCopy();
        public GdDict GetProjections() => _projections.DeepCopy();

        // Preparation validates with a private prospective receipt. Published/imported bundles never use this seam.
        internal static bool TryCreatePreparation(GdDict summary, GdDict command, out DomainBundle bundle, out string reason)
        {
            if (summary.GetInt("schema_version") != 2) return TryCreate(summary, out bundle, out reason);
            GdDict validation = summary.DeepCopy();
            string id = "component_transfer:" + command.GetString("command_id");
            var effect = new GdDict { { "operation", command.Get("operation") }, { "instance_id", command.Get("instance_id") },
                { "source_holder_id", command.Get("source_holder_id") }, { "destination_holder_id", command.Get("destination_holder_id") } };
            if (command.GetString("operation") == "swap") effect["swap_instance_id"] = command.Get("swap_instance_id");
            validation.GetDictOrEmpty("receipts")[id] = new GdDict { { "schema_version", 1L }, { "transaction_id", id },
                { "command_id", command.Get("command_id") }, { "commit_id", id }, { "revision", validation.Get("revision") }, { "result", effect } };
            GdDict work = validation.GetDictOrEmpty("component_work");
            if (work.GetString("command_id") == command.GetString("command_id"))
            { work["status"] = "committed"; work["resume_required"] = false; }
            if (!TryCreate(validation, out DomainBundle valid, out reason)) { bundle = null; return false; }
            bundle = new DomainBundle(summary, valid.Revision, valid.GetProjections()); return true;
        }

        public static bool TryCreate(GdDict summary, out DomainBundle bundle, out string reason)
        {
            bundle = null; reason = "invalid_summary";
            if (summary == null || !ItemInstanceState.IsSafeSnapshot(summary)) return false;
            GdDict owned = summary.DeepCopy();
            if (!Integer(owned.Get("schema_version"), out long schema) || (schema != 1 && schema != 2) ||
                !Keys(owned, schema == 1 ? new[] { "schema_version", "revision", "registry", "holders", "machinery", "receipts" }
                    : new[] { "schema_version", "revision", "registry", "holders", "machinery", "receipts", "physical_slots", "component_work", "participating_state", "command_sequence", "registered_owners" }) ||
                !Integer(owned.Get("revision"), out long revision) ||
                !(owned.Get("registry") is GdDict registry) || !(owned.Get("holders") is GdDict holders) ||
                !(owned.Get("machinery") is GdDict machinery) || !(owned.Get("receipts") is GdDict receipts)) return false;

            // Reuse the registry authority; the bundle adds relationships without inventing instance defaults.
            var instancesState = new ItemInstanceState();
            if (!instancesState.ApplySummary(registry)) { reason = "invalid_registry"; return false; }
            GdDict instances = instancesState.GetSummary().GetDictOrEmpty("instances");
            var slotsByOwner = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var linkedMachines = new HashSet<string>(StringComparer.Ordinal);
            var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var masses = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (object key in holders.Keys)
            {
                reason = "invalid_holder";
                if (!Text(key) || !(holders[key] is GdDict holder) ||
                    !Keys(holder, "holder_id", "kind", "owner_id", "revision", "capacity_count", "capacity_mass", "slot_id", "accepted_forms", "machinery_id") ||
                    holder.Get("holder_id") as string != (string)key || !Text(holder.Get("owner_id")) ||
                    !Integer(holder.Get("revision"), out _)) return false;
                string id = (string)key, kind = holder.Get("kind") as string;
                if (kind != "player" && kind != "ship_cargo" && kind != "cart" && kind != "container" && kind != "slot") return false;
                if (holder.Has("capacity_count") && !Integer(holder.Get("capacity_count"), out _)) return false;
                if (holder.Has("capacity_mass") && (!Number(holder.Get("capacity_mass"), out double capacity) || capacity < 0)) return false;
                if (kind == "slot")
                {
                    if (!Text(holder.Get("slot_id")) || !(holder.Get("accepted_forms") is GdArray forms) || forms.Count == 0) return false;
                    var uniqueForms = new HashSet<string>(StringComparer.Ordinal);
                    foreach (object form in forms) if (!Text(form) || !uniqueForms.Add((string)form)) return false;
                    string owner = (string)holder.Get("owner_id");
                    if (!slotsByOwner.TryGetValue(owner, out HashSet<string> ownerSlots))
                        slotsByOwner[owner] = ownerSlots = new HashSet<string>(StringComparer.Ordinal);
                    if (!ownerSlots.Add((string)holder.Get("slot_id"))) { reason = "duplicate_slot"; return false; }
                    if (holder.Has("machinery_id"))
                    {
                        if (!Text(holder.Get("machinery_id"))) return false;
                        string machineId = (string)holder.Get("machinery_id");
                        if ((!linkedMachines.Add(machineId) && schema == 1) || !(machinery.Get(machineId) is GdDict machine) ||
                            machine.Get("owner_id") as string != owner) { reason = "invalid_machinery_link"; return false; }
                    }
                }
                else if (holder.Has("slot_id") || holder.Has("accepted_forms") || holder.Has("machinery_id")) return false;
                members[id] = new List<string>(); masses[id] = 0;
            }
            foreach (object key in machinery.Keys)
            {
                reason = "invalid_machinery";
                if (!Text(key) || !(machinery[key] is GdDict machine) || !Keys(machine, schema == 1 ? new[] { "machinery_id", "owner_id", "health" }
                        : new[] { "machinery_id", "owner_id", "health", "system_id", "subcomponent_id" }) ||
                    machine.Get("machinery_id") as string != (string)key || !Text(machine.Get("owner_id")) ||
                    !Number(machine.Get("health"), out double health) || health < 0 || health > 1) return false;
            }
            foreach (object key in instances.Keys)
            {
                GdDict row = instances.GetDictOrEmpty(key);
                string holderId = row.Get("holder") as string;
                if (holderId == null || !members.ContainsKey(holderId)) { reason = "missing_holder"; return false; }
                members[holderId].Add((string)key);
                masses[holderId] += row.GetFloat("mass");
                if (double.IsInfinity(masses[holderId]) || double.IsNaN(masses[holderId])) { reason = "mass_overflow"; return false; }
                GdDict holder = holders.GetDictOrEmpty(holderId);
                if (holder.GetString("kind") == "slot" && !holder.GetArrayOrEmpty("accepted_forms").Contains(row.Get("item_form")))
                { reason = "form_incompatible"; return false; }
            }
            foreach (string id in members.Keys)
            {
                GdDict holder = holders.GetDictOrEmpty(id);
                if (holder.GetString("kind") == "slot" && members[id].Count > 1) { reason = "slot_overfull"; return false; }
                if (holder.Has("capacity_count") && members[id].Count > holder.GetInt("capacity_count")) { reason = "capacity_count"; return false; }
                if (holder.Has("capacity_mass") && masses[id] > holder.GetFloat("capacity_mass")) { reason = "capacity_mass"; return false; }
                members[id].Sort(StringComparer.Ordinal);
            }
            var commands = new HashSet<string>(StringComparer.Ordinal);
            foreach (object key in receipts.Keys)
            {
                reason = "invalid_receipt";
                if (!Text(key) || !(receipts[key] is GdDict receipt) ||
                    !Keys(receipt, schema == 1 ? new[] { "schema_version", "transaction_id", "command_id", "revision", "result" }
                        : new[] { "schema_version", "transaction_id", "command_id", "commit_id", "revision", "result" }) ||
                    !Integer(receipt.Get("schema_version"), out long receiptSchema) || receiptSchema != 1 ||
                    receipt.Get("transaction_id") as string != (string)key || !Text(receipt.Get("command_id")) ||
                    (string)key != "component_transfer:" + (string)receipt.Get("command_id") ||
                    !commands.Add((string)receipt.Get("command_id")) ||
                    !Integer(receipt.Get("revision"), out long committedRevision) || committedRevision == 0 || committedRevision > revision ||
                    (schema == 2 && (!(receipt.Get("commit_id") is string commitId) || commitId != (string)key)) ||
                    !(receipt.Get("result") is GdDict result) || !ValidResult(result)) return false;
            }
            var holderProjection = new GdDict();
            foreach (string id in members.Keys)
                holderProjection[id] = new GdDict { { "instance_ids", new GdArray(members[id]) }, { "count", (long)members[id].Count }, { "mass", masses[id] } };
            var machineProjection = new GdDict();
            foreach (object key in machinery.Keys)
                machineProjection[key] = new GdDict { { "component_available", false }, { "effective_health", 0.0 } };
            foreach (object key in holders.Keys)
            {
                GdDict holder = holders.GetDictOrEmpty(key);
                if (!holder.Has("machinery_id") || members[(string)key].Count == 0) continue;
                GdDict row = instances.GetDictOrEmpty(members[(string)key][0]);
                if (row.GetString("condition_state") != "known") continue;
                string machineId = holder.GetString("machinery_id");
                machineProjection[machineId] = new GdDict { { "component_available", true },
                    { "effective_health", Math.Min(machinery.GetDictOrEmpty(machineId).GetFloat("health"), row.GetFloat("condition")) } };
            }
            if (schema == 2)
            {
                if (!(owned.Get("physical_slots") is GdDict slots) || !(owned.Get("component_work") is GdDict work) ||
                    !(owned.Get("participating_state") is GdDict participants) || !(owned.Get("registered_owners") is GdArray owners) ||
                    !Integer(owned.Get("command_sequence"), out _) || !participants.Has("inventory") || !participants.Has("progression") || !participants.Has("training"))
                { reason = "invalid_live_state"; return false; }
                var ownerIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (object owner in owners) if (!Text(owner) || !ownerIds.Add((string)owner)) { reason = "invalid_live_owner"; return false; }
                foreach (object key in slots.Keys)
                {
                    if (!(slots[key] is GdDict slot) || !holders.Has(key) || holders.GetDictOrEmpty(key).GetString("kind") != "slot" ||
                        slot.GetString("holder_id") != (string)key || slot.GetString("ship_id") != holders.GetDictOrEmpty(key).GetString("owner_id") ||
                        slot.GetString("slot_id") != holders.GetDictOrEmpty(key).GetString("slot_id") || !Text(slot.Get("room_id")) ||
                        !Text(slot.Get("slot_kind")) || !Integer(slot.Get("slot_index"), out _) || !(slot.Get("local_position") is Vec3 position) ||
                        !FiniteVector(position)) { reason = "invalid_physical_slot"; return false; }
                }
                foreach (object key in holders.Keys)
                    if (holders.GetDictOrEmpty(key).GetString("kind") == "slot" && !slots.Has(key)) { reason = "missing_physical_slot"; return false; }
                // Every established physical link is required; one saved damage authority is capped by their minimum.
                foreach (object machineKey in machinery.Keys)
                {
                    double effective = machinery.GetDictOrEmpty(machineKey).GetFloat("health");
                    bool available = true, linked = false;
                    foreach (object holderKey in holders.Keys)
                    {
                        if (holders.GetDictOrEmpty(holderKey).GetString("machinery_id") != (string)machineKey) continue;
                        linked = true;
                        if (members[(string)holderKey].Count != 1) { available = false; continue; }
                        GdDict component = instances.GetDictOrEmpty(members[(string)holderKey][0]);
                        if (component.GetString("condition_state") != "known") { available = false; continue; }
                        effective = Math.Min(effective, component.GetFloat("condition"));
                    }
                    machineProjection[machineKey] = new GdDict { { "component_available", linked && available },
                        { "effective_health", linked && available ? effective : 0.0 } };
                }
                if (!work.IsEmpty && (!Text(work.Get("job_id")) || !Text(work.Get("instance_id")) || !instances.Has(work.Get("instance_id")) ||
                    !Text(work.Get("source_holder_id")) || !holders.Has(work.Get("source_holder_id")) ||
                    !Text(work.Get("destination_holder_id")) || !holders.Has(work.Get("destination_holder_id")) ||
                    !Integer(work.Get("expected_instance_revision"), out _) || !Integer(work.Get("expected_source_revision"), out _) ||
                    !Integer(work.Get("expected_destination_revision"), out _) || !Number(work.Get("progress"), out double progress) ||
                    !Number(work.Get("duration"), out double duration) || duration <= 0 || progress < 0 || progress > duration ||
                    !(work.Get("resume_required") is bool))) { reason = "invalid_component_work"; return false; }
                if (!work.IsEmpty && !ValidLiveWork(work, owned, instances, holders, slots, receipts))
                { reason = "invalid_component_work_binding"; return false; }
                var seenTrainingReceipts = new HashSet<string>(StringComparer.Ordinal);
                var workCatalog = new WorkActionCatalog(); workCatalog.LoadDefault();
                foreach (object logRow in participants.GetDictOrEmpty("training").GetArrayOrEmpty("log"))
                {
                    if (!(logRow is GdDict record) || !record.GetBool("receipt_owned")) continue;
                    string commitId = record.GetString("commit_id");
                    GdDict receipt = receipts.Get(commitId) as GdDict;
                    if (receipt == null || !seenTrainingReceipts.Add(commitId)) { reason = "invalid_component_training_receipt"; return false; }
                    GdDict effect = receipt.GetDictOrEmpty("result");
                    string sourceKind = holders.GetDictOrEmpty(effect.GetString("source_holder_id")).GetString("kind");
                    string destinationKind = holders.GetDictOrEmpty(effect.GetString("destination_holder_id")).GetString("kind");
                    string action = sourceKind == "slot" ? "dismount_component" : destinationKind == "slot" ? "mount_component" : "";
                    if (action.Length == 0 || record.GetString("target_id") != effect.GetString("instance_id") ||
                        record.GetString("event_id") != workCatalog.GetAction(action).GetString("xp_event"))
                    { reason = "invalid_component_training_receipt"; return false; }
                }
            }
            bundle = new DomainBundle(owned, revision, new GdDict { { "holders", holderProjection }, { "machinery", machineProjection } });
            reason = "ok"; return true;
        }

        static bool ValidResult(GdDict result)
        {
            if (!Keys(result, "operation", "instance_id", "source_holder_id", "destination_holder_id", "swap_instance_id") ||
                !Text(result.Get("instance_id")) || !Text(result.Get("source_holder_id")) || !Text(result.Get("destination_holder_id")) ||
                result.Get("source_holder_id") as string == result.Get("destination_holder_id") as string) return false;
            string operation = result.Get("operation") as string;
            if (operation == "transfer") return !result.Has("swap_instance_id");
            return operation == "swap" && Text(result.Get("swap_instance_id")) && result.Get("swap_instance_id") as string != result.Get("instance_id") as string;
        }

        static bool ValidLiveWork(GdDict work, GdDict domain, GdDict instances, GdDict holders, GdDict slots, GdDict receipts)
        {
            string action = work.GetString("action_id"), status = work.GetString("status"), command = work.GetString("command_id"), job = work.GetString("job_id");
            if ((action != "dismount_component" && action != "mount_component") ||
                (status != "active" && status != "interrupted" && status != "paused_restore" && status != "committed") ||
                !command.StartsWith("live_component:", StringComparison.Ordinal) || !job.StartsWith("component_work:", StringComparison.Ordinal) ||
                command.Substring("live_component:".Length) != job.Substring("component_work:".Length)) return false;
            int separator = command.LastIndexOf(':');
            if (separator <= "live_component:".Length || !long.TryParse(command.Substring(separator + 1), out long sequence) || sequence <= 0 || sequence > domain.GetInt("command_sequence")) return false;
            string physicalId = work.GetString("physical_holder_id"), source = work.GetString("source_holder_id"), destination = work.GetString("destination_holder_id");
            GdDict physical = slots.Get(physicalId) as GdDict;
            if (physical == null || source == destination || physical.GetString("ship_id") != work.GetString("ship_id") ||
                physical.GetString("slot_id") != work.GetString("slot_id")) return false;
            if (action == "dismount_component" && (source != physicalId || destination != "player:player_local") ||
                action == "mount_component" && (source != "player:player_local" || destination != physicalId)) return false;
            var catalog = new WorkActionCatalog(); catalog.LoadDefault();
            if (work.GetFloat("duration") != catalog.GetAction(action).GetFloat("duration")) return false;
            string receiptId = "component_transfer:" + command;
            if (status == "committed")
            {
                GdDict receipt = receipts.Get(receiptId) as GdDict;
                GdDict effect = receipt?.GetDictOrEmpty("result");
                return receipt != null && !work.GetBool("resume_required") && work.GetFloat("progress") == work.GetFloat("duration") &&
                    effect.GetString("operation") == "transfer" && effect.GetString("instance_id") == work.GetString("instance_id") &&
                    effect.GetString("source_holder_id") == source && effect.GetString("destination_holder_id") == destination;
            }
            if (receipts.Has(receiptId)) return false;
            GdDict row = instances.GetDictOrEmpty(work.GetString("instance_id"));
            return row.GetString("holder") == source && row.GetInt("revision") == work.GetInt("expected_instance_revision") &&
                holders.GetDictOrEmpty(source).GetInt("revision") == work.GetInt("expected_source_revision") &&
                holders.GetDictOrEmpty(destination).GetInt("revision") == work.GetInt("expected_destination_revision");
        }

        static bool Keys(GdDict dict, params string[] allowed)
        {
            var names = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (object key in dict.Keys) if (!(key is string name) || !names.Contains(name)) return false;
            return true;
        }
        static bool Text(object value) => value is string text && !string.IsNullOrWhiteSpace(text);
        static bool FiniteVector(Vec3 value) => !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
            !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) && !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
        static bool Number(object value, out double number)
        {
            number = 0;
            if (!(value is long) && !(value is double)) return false;
            number = V.F64(value);
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
        static bool Integer(object value, out long integer)
        {
            integer = 0;
            if (value is long l) { integer = l; return l >= 0; }
            return false;
        }
    }
}
