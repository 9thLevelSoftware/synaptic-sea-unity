// Ported from scripts/systems/work_action_state.gd @ 96ecb2b0

using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-B2.2a pure timed work model: progress, interrupt, tool/skill/material gates.
    /// Scene layer drives start/tick; this never touches the tree.
    /// </summary>
    public class WorkActionState
    {
        public const string STATUS_IDLE = "idle";
        public const string STATUS_ACTIVE = "active";
        public const string STATUS_COMPLETED = "completed";
        public const string STATUS_INTERRUPTED = "interrupted";
        public const string STATUS_BLOCKED = "blocked";

        public string ActionId = "";
        public GdDict Definition = new GdDict();
        public string Status = STATUS_IDLE;
        public double Progress = 0.0;
        public double Duration = 1.0;
        public string TargetId = "";
        public string BlockReason = "";

        public void ConfigureAction(string pActionId, GdDict def)
        {
            ActionId = pActionId;
            Definition = (def ?? new GdDict()).DeepCopy();
            Duration = Math.Max(0.1, V.F64(Definition.Get("duration", 1.0)));
            Status = STATUS_IDLE;
            Progress = 0.0;
            BlockReason = "";
            TargetId = "";
        }

        /// <summary>context keys: tool_class, skill_id, skill_level, inventory (Dictionary item->qty), damaged (bool)</summary>
        public bool CanStart(GdDict context = null)
        {
            context = context ?? new GdDict();
            BlockReason = "";
            if (Definition.IsEmpty)
            {
                BlockReason = "no_definition";
                return false;
            }
            string requiredTool = V.Str(Definition.Get("tool_class", ""));
            if (requiredTool.Length != 0)
            {
                string haveTool = V.Str(context.Get("tool_class", ""));
                if (haveTool != requiredTool)
                {
                    BlockReason = "tool";
                    return false;
                }
            }
            string minSkill = V.Str(Definition.Get("min_skill", ""));
            if (minSkill.Length != 0)
            {
                string skillId = V.Str(context.Get("skill_id", ""));
                long skillLevel = V.I64(context.Get("skill_level", 0L));
                long need = V.I64(Definition.Get("min_skill_level", 0L));
                if (skillId != minSkill || skillLevel < need)
                {
                    BlockReason = "skill";
                    return false;
                }
            }
            object consumed = Definition.Get("materials_consumed", new GdDict());
            if (consumed is GdDict consumedDict && !consumedDict.IsEmpty)
            {
                object inv = context.Get("inventory", new GdDict());
                if (!(inv is GdDict invDict))
                {
                    BlockReason = "materials";
                    return false;
                }
                foreach (object itemId in consumedDict.Keys)
                {
                    long needQty = V.I64(consumedDict[itemId]);
                    long haveQty = V.I64(invDict.Get(V.Str(itemId), 0L));
                    if (haveQty < needQty)
                    {
                        BlockReason = "materials";
                        return false;
                    }
                }
            }
            return true;
        }

        public bool Start(string pTargetId, GdDict context = null)
        {
            if (Status == STATUS_ACTIVE)
                return false;
            if (!CanStart(context))
            {
                Status = STATUS_BLOCKED;
                return false;
            }
            TargetId = pTargetId;
            Progress = 0.0;
            Status = STATUS_ACTIVE;
            BlockReason = "";
            return true;
        }

        public string Tick(double delta, GdDict context = null)
        {
            context = context ?? new GdDict();
            if (Status != STATUS_ACTIVE)
                return Status;
            if (V.Bool(context.Get("damaged", false)) && V.Bool(Definition.Get("interruptible", true)))
            {
                Status = STATUS_INTERRUPTED;
                return Status;
            }
            if (delta <= 0.0)
                return Status;
            // Optional work-speed mult (wounds/arm injury later).
            double speed = Math.Max(0.05, V.F64(context.Get("work_speed_mult", 1.0)));
            Progress = Math.Min(Duration, Progress + delta * speed);
            if (Progress >= Duration - 0.0001)
            {
                Status = STATUS_COMPLETED;
                Progress = Duration;
            }
            return Status;
        }

        public void Interrupt()
        {
            if (Status == STATUS_ACTIVE)
                Status = STATUS_INTERRUPTED;
        }

        public void Reset()
        {
            Status = STATUS_IDLE;
            Progress = 0.0;
            TargetId = "";
            BlockReason = "";
        }

        public double ProgressRatio()
        {
            if (Duration <= 0.0)
                return 0.0;
            return GdMath.Clampf(Progress / Duration, 0.0, 1.0);
        }

        public double Noise() => Math.Max(0.0, V.F64(Definition.Get("noise", 0.0)));

        public string XpEvent() => V.Str(Definition.Get("xp_event", ""));

        public GdDict MaterialsYielded()
        {
            object y = Definition.Get("materials_yielded", new GdDict());
            if (y is GdDict yDict)
                return yDict.DeepCopy();
            return new GdDict();
        }

        public GdDict MaterialsConsumed()
        {
            object c = Definition.Get("materials_consumed", new GdDict());
            if (c is GdDict cDict)
                return cDict.DeepCopy();
            return new GdDict();
        }

        public GdDict GetSummary() =>
            new GdDict
            {
                { "action_id", ActionId },
                { "status", Status },
                { "progress", Progress },
                { "duration", Duration },
                { "target_id", TargetId },
                { "block_reason", BlockReason },
                { "definition", Definition.DeepCopy() },
            };

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            ActionId = V.Str(summary.Get("action_id", ActionId));
            Status = V.Str(summary.Get("status", STATUS_IDLE));
            Progress = V.F64(summary.Get("progress", 0.0));
            Duration = Math.Max(0.1, V.F64(summary.Get("duration", Duration)));
            TargetId = V.Str(summary.Get("target_id", ""));
            BlockReason = V.Str(summary.Get("block_reason", ""));
            object def = summary.Get("definition", new GdDict());
            if (def is GdDict defDict)
                Definition = defDict.DeepCopy();
            return true;
        }
    }
}
