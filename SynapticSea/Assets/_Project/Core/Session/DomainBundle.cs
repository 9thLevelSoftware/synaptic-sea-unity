using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>Detached immutable component-domain snapshot; never bound to live inventory or game saves.</summary>
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

        public static bool TryCreate(GdDict summary, out DomainBundle bundle, out string reason)
        {
            bundle = null; reason = "invalid_summary";
            if (summary == null || !ItemInstanceState.IsSafeSnapshot(summary)) return false;
            GdDict owned = summary.DeepCopy();
            if (!Keys(owned, "schema_version", "revision", "registry", "holders", "machinery", "receipts") ||
                !Integer(owned.Get("schema_version"), out long schema) || schema != 1 ||
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
                        if (!linkedMachines.Add(machineId) || !(machinery.Get(machineId) is GdDict machine) ||
                            machine.Get("owner_id") as string != owner) { reason = "invalid_machinery_link"; return false; }
                    }
                }
                else if (holder.Has("slot_id") || holder.Has("accepted_forms") || holder.Has("machinery_id")) return false;
                members[id] = new List<string>(); masses[id] = 0;
            }
            foreach (object key in machinery.Keys)
            {
                reason = "invalid_machinery";
                if (!Text(key) || !(machinery[key] is GdDict machine) || !Keys(machine, "machinery_id", "owner_id", "health") ||
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
                    !Keys(receipt, "schema_version", "transaction_id", "command_id", "revision", "result") ||
                    !Integer(receipt.Get("schema_version"), out long receiptSchema) || receiptSchema != 1 ||
                    receipt.Get("transaction_id") as string != (string)key || !Text(receipt.Get("command_id")) ||
                    (string)key != "component_transfer:" + (string)receipt.Get("command_id") ||
                    !commands.Add((string)receipt.Get("command_id")) ||
                    !Integer(receipt.Get("revision"), out long committedRevision) || committedRevision == 0 || committedRevision > revision ||
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

        static bool Keys(GdDict dict, params string[] allowed)
        {
            var names = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (object key in dict.Keys) if (!(key is string name) || !names.Contains(name)) return false;
            return true;
        }
        static bool Text(object value) => value is string text && !string.IsNullOrWhiteSpace(text);
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
