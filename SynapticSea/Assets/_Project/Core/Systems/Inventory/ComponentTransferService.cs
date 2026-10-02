using System;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Unused, detached component preparation. Instance holders are the sole membership authority;
    /// publication and completion receipts belong to DomainTransactionCoordinator.
    /// </summary>
    public sealed class ComponentTransferService
    {
        public GdDict Prepare(GdDict command, GdDict domainSnapshot)
        {
            if (!ItemInstanceState.IsSafeSnapshot(command)) return Failure("invalid_command");
            GdDict request = command?.DeepCopy();
            if (!ValidCommand(request)) return Failure("invalid_command");
            if (!ItemInstanceState.IsSafeSnapshot(domainSnapshot)) return Failure("invalid_domain");
            if (!DomainBundle.TryCreate(domainSnapshot?.DeepCopy(), out DomainBundle original, out string detail))
                return Failure("invalid_domain", detail);

            // GetSummary returns a private working copy; no input or returned candidate is retained here.
            GdDict candidate = original.GetSummary();
            GdDict instances = candidate.GetDictOrEmpty("registry").GetDictOrEmpty("instances");
            GdDict holders = candidate.GetDictOrEmpty("holders");
            string commandId = request.GetString("command_id");
            string instanceId = request.GetString("instance_id");
            string sourceId = request.GetString("source_holder_id");
            string destinationId = request.GetString("destination_holder_id");
            bool swap = request.GetString("operation") == "swap";

            foreach (var entry in candidate.GetDictOrEmpty("receipts"))
                if (((GdDict)entry.Value).GetString("command_id") == commandId)
                    return Failure("duplicate_command");

            if (request.GetInt("expected_domain_revision") != original.Revision) return Failure("stale_domain");
            if (!instances.Has(instanceId)) return Failure("missing_instance");
            if (!holders.Has(sourceId) || !holders.Has(destinationId)) return Failure("missing_holder");
            GdDict instance = (GdDict)instances[instanceId];
            GdDict source = (GdDict)holders[sourceId];
            GdDict destination = (GdDict)holders[destinationId];
            if (request.GetInt("expected_instance_revision") != instance.GetInt("revision")) return Failure("stale_instance");
            if (request.GetInt("expected_source_revision") != source.GetInt("revision")) return Failure("stale_source");
            if (request.GetInt("expected_destination_revision") != destination.GetInt("revision")) return Failure("stale_destination");
            if (instance.GetString("holder") != sourceId) return Failure("source_mismatch");

            GdDict counterpart = null;
            if (swap)
            {
                string counterpartId = request.GetString("swap_instance_id");
                if (!instances.Has(counterpartId)) return Failure("missing_swap_instance");
                counterpart = (GdDict)instances[counterpartId];
                if (request.GetInt("expected_swap_instance_revision") != counterpart.GetInt("revision")) return Failure("stale_swap");
                if (counterpart.GetString("holder") != destinationId) return Failure("swap_source_mismatch");
            }

            string gate = InstallationReason(instance, destination);
            if (gate != "ok") return Failure(gate);
            if (swap)
            {
                gate = InstallationReason(counterpart, source);
                if (gate != "ok") return Failure(gate);
            }
            if (original.Revision == long.MaxValue || instance.GetInt("revision") == long.MaxValue ||
                source.GetInt("revision") == long.MaxValue || destination.GetInt("revision") == long.MaxValue ||
                (swap && counterpart.GetInt("revision") == long.MaxValue))
                return Failure("revision_overflow");

            // Only these fields change. Move both swap rows before evaluating any capacity or occupancy.
            instance["holder"] = destinationId;
            instance["revision"] = instance.GetInt("revision") + 1;
            if (swap)
            {
                counterpart["holder"] = sourceId;
                counterpart["revision"] = counterpart.GetInt("revision") + 1;
            }
            source["revision"] = source.GetInt("revision") + 1;
            destination["revision"] = destination.GetInt("revision") + 1;
            candidate["revision"] = original.Revision + 1;

            gate = CapacityReason(destinationId, destination, instances);
            if (gate != "ok") return Failure(gate);
            gate = CapacityReason(sourceId, source, instances);
            if (gate != "ok") return Failure(gate);
            if (!DomainBundle.TryCreate(candidate, out DomainBundle validated, out detail))
                return Failure("invalid_domain", detail);

            var effect = new GdDict
            {
                { "operation", request.GetString("operation") }, { "instance_id", instanceId },
                { "source_holder_id", sourceId }, { "destination_holder_id", destinationId },
            };
            if (swap) effect["swap_instance_id"] = request.GetString("swap_instance_id");
            return new GdDict
            {
                { "ok", true }, { "reason", "ok" }, { "transaction_id", "component_transfer:" + commandId },
                { "expected_revision", original.Revision }, { "candidate", validated.GetSummary() }, { "result", effect },
            };
        }

        static bool ValidCommand(GdDict command)
        {
            if (command == null || !(command.Get("schema_version") is long schema) || schema != 1 ||
                !NonblankString(command.Get("command_id")) || !NonblankString(command.Get("instance_id")) ||
                !NonblankString(command.Get("source_holder_id")) || !NonblankString(command.Get("destination_holder_id")) ||
                command.GetString("source_holder_id") == command.GetString("destination_holder_id")) return false;
            foreach (string key in new[] { "expected_domain_revision", "expected_instance_revision", "expected_source_revision", "expected_destination_revision" })
                if (!(command.Get(key) is long revision) || revision < 0) return false;
            if (command.Get("operation") is string operation && operation == "swap")
                return NonblankString(command.Get("swap_instance_id")) &&
                    command.GetString("swap_instance_id") != command.GetString("instance_id") &&
                    command.Get("expected_swap_instance_revision") is long swapRevision && swapRevision >= 0;
            return command.Get("operation") is string transfer && transfer == "transfer" &&
                !command.Has("swap_instance_id") && !command.Has("expected_swap_instance_revision");
        }

        static bool NonblankString(object value) => value is string text && !string.IsNullOrWhiteSpace(text);

        static string InstallationReason(GdDict instance, GdDict holder)
        {
            if (holder.GetString("kind") != "slot") return "ok";
            if (instance.GetString("condition_state") == "unknown") return "condition_unknown";
            return holder.GetArrayOrEmpty("accepted_forms").Contains(instance.Get("item_form")) ? "ok" : "form_incompatible";
        }

        static string CapacityReason(string holderId, GdDict holder, GdDict instances)
        {
            long count = 0;
            double mass = 0.0;
            foreach (var entry in instances)
            {
                GdDict instance = (GdDict)entry.Value;
                if (instance.GetString("holder") != holderId) continue;
                count++;
                mass += instance.GetFloat("mass");
            }
            if (holder.GetString("kind") == "slot" && count > 1) return "slot_occupied";
            if (holder.Has("capacity_count") && count > holder.GetInt("capacity_count")) return "capacity_count";
            if (double.IsNaN(mass) || double.IsInfinity(mass)) return "mass_overflow";
            if (holder.Has("capacity_mass") && mass > holder.GetFloat("capacity_mass")) return "capacity_mass";
            return "ok";
        }

        static GdDict Failure(string reason, string detail = null)
        {
            var result = new GdDict { { "ok", false }, { "reason", reason } };
            if (!string.IsNullOrEmpty(detail)) result["detail"] = detail;
            return result;
        }
    }
}
