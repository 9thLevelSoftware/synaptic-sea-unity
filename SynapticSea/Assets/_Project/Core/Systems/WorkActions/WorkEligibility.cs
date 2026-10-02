using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Unused engine-free eligibility over explicit detached ticket/context evidence.</summary>
    public sealed class WorkEligibility
    {
        public GdDict Evaluate(GdDict ticket, GdDict context)
        {
            if (!WorkKernelData.ValidTicket(ticket)) return Result(ticket, "invalid_ticket");
            if (!WorkKernelData.ValidContext(context)) return Result(ticket, "invalid_context");
            if (context.GetString("actor_id") != ticket.GetString("actor_id")) return Result(ticket, "actor_changed");
            if (context.GetString("ship_id") != ticket.GetString("ship_id") ||
                context.GetInt("owner_revision") != ticket.GetInt("owner_revision")) return Result(ticket, "owner_changed");
            if (context.GetString("target_id") != ticket.GetString("target_id") ||
                context.GetInt("target_revision") != ticket.GetInt("target_revision")) return Result(ticket, "target_changed");
            if (context.GetString("definition_hash") != ticket.GetString("definition_hash")) return Result(ticket, "definition_changed");
            if (!context.GetBool("target_exists")) return Result(ticket, "target_missing");
            if (!context.GetBool("owner_valid")) return Result(ticket, "owner_invalid");
            Vec3 anchor = (Vec3)ticket["local_anchor"], actual = (Vec3)context["target_local_anchor"];
            if (actual != anchor) return Result(ticket, "target_anchor_changed");

            // Resolve distance in double precision rather than overflowing Vec3's float intermediates.
            Vec3 player = (Vec3)context["player_local_position"];
            double x = (double)player.X - anchor.X, y = (double)player.Y - anchor.Y, z = (double)player.Z - anchor.Z;
            double distance = Math.Sqrt(x * x + y * y + z * z), range = ticket.GetFloat("range_m");
            bool inRange = ticket.GetString("range_comparison") == "inclusive" ? distance <= range : distance < range;
            if (!inRange) return Result(ticket, "left_work_site");
            if (ticket.GetString("los_policy") == "required" && !context.GetBool("line_of_sight")) return Result(ticket, "line_of_sight");
            if (context.GetBool("dead")) return Result(ticket, "dead");
            GdDict effort = ticket.GetDict("effort_policy"), requirements = ticket.GetDict("requirements");
            if (effort.GetBool("interrupt_on_damage") && context.GetBool("damaged")) return Result(ticket, "damaged");
            if (effort.GetString("stamina_rule") == "per_eligible_second" &&
                context.GetFloat("stamina") <= effort.GetFloat("exhaustion_threshold")) return Result(ticket, "exhausted");
            string tool = requirements.GetString("tool_class"), skill = requirements.GetString("skill_id");
            if (tool.Length > 0 && context.GetString("tool_class") != tool) return Result(ticket, "tool");
            if (skill.Length > 0 && (context.GetString("skill_id") != skill ||
                context.GetInt("skill_level") < requirements.GetInt("min_skill_level"))) return Result(ticket, "skill");
            if (ticket.GetString("material_policy") == "final_commit")
                foreach (var item in requirements.GetDict("materials"))
                    if (context.GetDict("inventory").GetInt(item.Key) < (long)item.Value) return Result(ticket, "materials");
            if (ticket.GetString("material_policy") == "paid_receipt" &&
                context.GetString("payment_commit_id") != ticket.GetString("payment_commit_id")) return Result(ticket, "payment_receipt");
            if (requirements.GetBool("require_power") && !context.GetBool("power_available")) return Result(ticket, "power");
            if (context.GetString("input_mode") == "hold" && !context.GetBool("input_held")) return Result(ticket, "hold_released");
            return Result(ticket, "ok");
        }

        static GdDict Result(GdDict ticket, string reason) => new GdDict
        {
            { "ok", reason == "ok" }, { "reason", reason },
            { "job_id", ticket?.Get("job_id") is string id ? id : "" },
            { "status", ticket?.Get("status") is string status ? status : "" },
            { "owner_revision", ticket?.Get("owner_revision") is long owner && owner >= 0 ? owner : 0L },
            { "target_revision", ticket?.Get("target_revision") is long target && target >= 0 ? target : 0L },
            { "missing_requirements", reason == "ok" ? new GdArray() : GdArray.Of(reason) },
        };
    }

    // Work-owned validation only. No concrete domain, progression, save or live session dependency.
    internal static class WorkKernelData
    {
        internal static readonly string[] StateFields =
            { "status", "pause_reason", "resume_required", "prepared_transaction_id", "committed_result" };

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal static bool Nonblank(string value) => !string.IsNullOrWhiteSpace(value);
        internal static bool Text(GdDict value, string key, bool allowEmpty = false) =>
            value?.Get(key) is string text && (allowEmpty ? text.Length == 0 || Nonblank(text) : Nonblank(text));
        internal static bool Integer(GdDict value, string key, long minimum = 0) => value?.Get(key) is long number && number >= minimum;
        internal static bool Boolean(GdDict value, string key) => value?.Get(key) is bool;
        internal static bool Number(GdDict value, string key, double minimum = 0, bool exclusive = false)
        {
            object raw = value?.Get(key);
            if (!(raw is double) && !(raw is long)) return false;
            double number = V.F64(raw);
            return Finite(number) && (exclusive ? number > minimum : number >= minimum);
        }
        static bool Position(GdDict value, string key) => value?.Get(key) is Vec3 p && Finite(p.X) && Finite(p.Y) && Finite(p.Z);
        internal static bool Mode(string mode) => mode == "hold" || mode == "toggle" || mode == "accessibility";

        internal static bool SafeSnapshot(object value) => SafeSnapshot(value, new HashSet<object>());
        static bool SafeSnapshot(object value, HashSet<object> ancestors)
        {
            if (value is GdDict dict)
            {
                if (!ancestors.Add(dict)) return false;
                try
                {
                    foreach (var entry in dict)
                        if (entry.Key is GdDict || entry.Key is GdArray || !SafeSnapshot(entry.Key, ancestors) ||
                            !SafeSnapshot(entry.Value, ancestors)) return false;
                    return true;
                }
                finally { ancestors.Remove(dict); }
            }
            if (value is GdArray array)
            {
                if (!ancestors.Add(array)) return false;
                try
                {
                    foreach (object item in array) if (!SafeSnapshot(item, ancestors)) return false;
                    return true;
                }
                finally { ancestors.Remove(array); }
            }
            if (value is double number) return Finite(number);
            if (value is Vec3 position) return Finite(position.X) && Finite(position.Y) && Finite(position.Z);
            return value == null || value is string || value is long || value is bool || value is Vec2i;
        }

        static bool Quantities(GdDict values, bool positive)
        {
            if (values == null) return false;
            foreach (var entry in values)
                if (!(entry.Key is string id) || !Nonblank(id) || !(entry.Value is long count) || count < (positive ? 1 : 0)) return false;
            return true;
        }

        internal static bool ValidTicket(GdDict ticket)
        {
            if (ticket == null || !SafeSnapshot(ticket) || ticket.Get("schema_version") is not long schema || schema != 1 ||
                ticket.Has("input_held") || ticket.Has("ticket")) return false;
            foreach (string field in new[] { "job_id", "action_id", "actor_id", "ship_id", "target_id", "definition_hash", "output_owner" })
                if (!Text(ticket, field)) return false;
            if (!Integer(ticket, "owner_revision") || !Integer(ticket, "target_revision") || !Position(ticket, "local_anchor") ||
                !Number(ticket, "range_m", exclusive: true) || !Number(ticket, "duration_seconds", exclusive: true) ||
                !Number(ticket, "progress_seconds") || ticket.GetFloat("progress_seconds") > ticket.GetFloat("duration_seconds") ||
                !Text(ticket, "payment_commit_id", true) || !Mode(ticket.GetString("input_mode"))) return false;
            string comparator = ticket.GetString("range_comparison"), los = ticket.GetString("los_policy"), material = ticket.GetString("material_policy");
            if ((comparator != "inclusive" && comparator != "exclusive") || (los != "none" && los != "required") ||
                (material != "none" && material != "final_commit" && material != "paid_receipt")) return false;
            if ((material == "paid_receipt") != Nonblank(ticket.GetString("payment_commit_id"))) return false;
            GdDict requirements = ticket.GetDict("requirements"), effort = ticket.GetDict("effort_policy"), command = ticket.GetDict("completion_command");
            if (requirements == null || !Text(requirements, "tool_class", true) || !Text(requirements, "skill_id", true) ||
                !Integer(requirements, "min_skill_level") || !Boolean(requirements, "require_power") ||
                !Quantities(requirements.GetDict("materials"), true) ||
                (material == "none" && !requirements.GetDict("materials").IsEmpty) || command == null || !Text(command, "command_id")) return false;
            if (ticket.Get("committed_effect_ids") is not GdArray effects) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object effect in effects) if (!(effect is string id) || !Nonblank(id) || !ids.Add(id)) return false;
            if (effort == null || !Text(effort, "definition_hash") || effort.GetString("definition_hash") != ticket.GetString("definition_hash") ||
                effort.Get("duration_basis") is not string basis || basis != "eligible_seconds" ||
                effort.Get("speed_rule") is not string speed || (speed != "constant" && speed != "ordinary_work") ||
                !Number(effort, "base_speed", exclusive: true) || !Number(effort, "stamina_per_eligible_second") ||
                !Number(effort, "exhaustion_threshold") || !Boolean(effort, "interrupt_on_damage") || !Boolean(effort, "clip_final_tick") ||
                effort.Get("power_loss_behavior") is not string power || (power != "pause" && power != "interrupt") ||
                effort.Get("stamina_rule") is not string stamina || (stamina != "none" && stamina != "per_eligible_second")) return false;
            return stamina != "none" || effort.GetFloat("stamina_per_eligible_second") == 0;
        }

        internal static bool ValidContext(GdDict context)
        {
            if (context == null || !SafeSnapshot(context)) return false;
            foreach (string field in new[] { "actor_id", "ship_id", "target_id", "definition_hash" }) if (!Text(context, field)) return false;
            foreach (string field in new[] { "owner_revision", "target_revision", "skill_level", "inventory_revision" }) if (!Integer(context, field)) return false;
            foreach (string field in new[] { "owner_valid", "target_exists", "line_of_sight", "power_available", "damaged", "dead", "input_held", "resume_requested" })
                if (!Boolean(context, field)) return false;
            return Position(context, "player_local_position") && Position(context, "target_local_anchor") &&
                Text(context, "tool_class", true) && Text(context, "skill_id", true) && Text(context, "payment_commit_id", true) &&
                context.Get("input_mode") is string mode && Mode(mode) && Quantities(context.GetDict("inventory"), false) &&
                Number(context, "max_stamina", exclusive: true) && Number(context, "stamina") &&
                context.GetFloat("stamina") <= context.GetFloat("max_stamina") && Number(context, "wound_speed_mult", exclusive: true);
        }

        internal static GdDict TicketCopy(GdDict row)
        {
            GdDict copy = row.DeepCopy();
            foreach (string field in StateFields) copy.Erase(field);
            return copy;
        }
        internal static bool SameTicket(GdDict a, GdDict b)
        {
            GdDict left = TicketCopy(a), right = TicketCopy(b);
            left.Erase("progress_seconds"); right.Erase("progress_seconds");
            return ExactEquals(left, right);
        }

        internal static bool ExactEquals(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.GetType() != b.GetType()) return false;
            if (a is GdDict left && b is GdDict right)
            {
                if (left.Count != right.Count) return false;
                foreach (var entry in left)
                {
                    bool found = false;
                    foreach (var other in right)
                        if (ExactEquals(entry.Key, other.Key)) { if (!ExactEquals(entry.Value, other.Value)) return false; found = true; break; }
                    if (!found) return false;
                }
                return true;
            }
            if (a is GdArray list && b is GdArray otherList)
            {
                if (list.Count != otherList.Count) return false;
                for (int i = 0; i < list.Count; i++) if (!ExactEquals(list[i], otherList[i])) return false;
                return true;
            }
            return a.Equals(b);
        }

        internal static bool ConfirmedReceipt(GdDict receipt, string transactionId, string commandId) =>
            receipt != null && SafeSnapshot(receipt) && receipt.Get("committed") is bool committed && committed &&
            receipt.Get("transaction_id") is string transaction && transaction == transactionId &&
            receipt.Get("command_id") is string command && command == commandId && Text(receipt, "commit_id") && receipt.Get("result") is GdDict;

        internal static bool ValidRow(GdDict row)
        {
            if (!ValidTicket(row) || !Text(row, "status") || !Text(row, "pause_reason", true) || !Boolean(row, "resume_required") ||
                !Text(row, "prepared_transaction_id", true) || row.Get("committed_result") is not GdDict result) return false;
            string status = row.GetString("status"), prepared = row.GetString("prepared_transaction_id"), reason = row.GetString("pause_reason");
            bool resume = row.GetBool("resume_required"), complete = row.GetFloat("progress_seconds") == row.GetFloat("duration_seconds");
            if (prepared.Length > 0 && !complete) return false;
            if (status == "committed")
                return prepared.Length > 0 && complete && !resume && reason.Length == 0 &&
                    ConfirmedReceipt(result, prepared, row.GetDict("completion_command").GetString("command_id")) &&
                    result.Get("ok") is bool ok && ok && result.Get("job_id") is string id && id == row.GetString("job_id") &&
                    result.Get("status") is string savedStatus && savedStatus == "committed" && Text(result, "reason") &&
                    result.Get("owner_revision") is long owner && owner == row.GetInt("owner_revision") &&
                    result.Get("target_revision") is long target && target == row.GetInt("target_revision") &&
                    Number(result, "progress_seconds") && result.GetFloat("progress_seconds") == row.GetFloat("progress_seconds") &&
                    result.Get("missing_requirements") is GdArray missing && missing.IsEmpty;
            if (!result.IsEmpty) return false;
            switch (status)
            {
                case "active": return !complete && !resume && reason.Length == 0 && prepared.Length == 0;
                case "ready": return complete && !resume && reason.Length == 0 && prepared.Length == 0;
                case "paused_hold": return !resume && reason == "hold_released" && prepared.Length == 0;
                case "paused_power": return resume && reason == "power";
                case "interrupted": return resume && Nonblank(reason);
                case "paused_restore": return resume && reason == "resume_required";
                case "pending_commit": return complete && !resume && prepared.Length > 0;
                default: return false;
            }
        }
    }
}
