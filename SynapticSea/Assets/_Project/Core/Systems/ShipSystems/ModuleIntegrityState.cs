// Ported from scripts/systems/module_integrity_state.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>PKG-B2.1a pure model: per placed structural module integrity (ADR-0051).</summary>
    public class ModuleIntegrityState : ISimModel
    {
        public const string STATE_INTACT = "intact";
        public const string STATE_DAMAGED = "damaged";
        public const string STATE_BREACHED = "breached";
        public const string STATE_DESTROYED = "destroyed";

        public const double THRESHOLD_DAMAGED = 0.75;
        public const double THRESHOLD_BREACHED = 0.40;
        public const double THRESHOLD_DESTROYED = 0.05;

        public string ModuleId = "";
        public string Kind = "";
        public double Integrity = 1.0;
        public string State = STATE_INTACT;
        public GdDict MaterialComposition = new GdDict();
        public GdArray MountedComponents = new GdArray();
        public double BaseIntegrity = 1.0;
        public string ToolClassRequired = "";

        /// <summary>Owning room (optional; used for fire/nav routing).</summary>
        public string RoomId = "";

        /// <summary>All rooms sharing this compiled module (shared walls).</summary>
        public List<string> OwnerRooms = new List<string>();

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            ModuleId = V.Str(config.Get("module_id", ModuleId));
            Kind = V.Str(config.Get("kind", Kind));
            RoomId = V.Str(config.Get("room_id", RoomId));
            BaseIntegrity = Math.Max(0.01, V.F64(config.Get("base_integrity", 1.0)));
            Integrity = GdMath.Clampf(V.F64(config.Get("integrity", BaseIntegrity)), 0.0, BaseIntegrity);
            ToolClassRequired = V.Str(config.Get("tool_class", ToolClassRequired));
            object comp = config.Get("material_composition", new GdDict());
            if (comp is GdDict compDict)
                MaterialComposition = compDict.DeepCopy();
            object mounted = config.Get("mounted_components", new GdArray());
            if (mounted is GdArray mountedArr)
                MountedComponents = mountedArr.DeepCopy();
            RecomputeState();
        }

        public string ApplyDamage(double amount)
        {
            if (amount <= 0.0 || State == STATE_DESTROYED)
                return State;
            Integrity = Math.Max(0.0, Integrity - amount);
            return RecomputeState();
        }

        public string Repair(double amount)
        {
            if (amount <= 0.0 || State == STATE_DESTROYED)
                return State;
            Integrity = Math.Min(BaseIntegrity, Integrity + amount);
            return RecomputeState();
        }

        string RecomputeState()
        {
            double ratio = BaseIntegrity > 0.0 ? Integrity / BaseIntegrity : 0.0;
            if (ratio <= THRESHOLD_DESTROYED)
                State = STATE_DESTROYED;
            else if (ratio <= THRESHOLD_BREACHED)
                State = STATE_BREACHED;
            else if (ratio <= THRESHOLD_DAMAGED)
                State = STATE_DAMAGED;
            else
                State = STATE_INTACT;
            return State;
        }

        public bool IsPristine()
        {
            // Sparse persistence must keep modules with component mutations even if undamaged.
            if (State != STATE_INTACT)
                return false;
            if (Math.Abs(Integrity - BaseIntegrity) > 0.0001)
                return false;
            if (!MountedComponents.IsEmpty)
                return false;
            return true;
        }

        public GdDict GetSummary() =>
            new GdDict
            {
                { "module_id", ModuleId },
                { "kind", Kind },
                { "room_id", RoomId },
                { "integrity", Integrity },
                { "base_integrity", BaseIntegrity },
                { "state", State },
                { "material_composition", MaterialComposition.DeepCopy() },
                { "mounted_components", MountedComponents.DeepCopy() },
                { "tool_class", ToolClassRequired },
            };

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            Configure(summary);
            return true;
        }
    }
}
