// Ported from scripts/systems/ship_systems_manager.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Owns the six ship systems, resolves dependency cascades on demand, and (Task 6) drives time effects and
    /// repair. Pure data model — no scene tree.
    /// </summary>
    public class ShipSystemsManager : IAdvanceable
    {
        public const string DEFINITIONS_PATH = "res://data/ship_systems/systems.json";

        // Mirrors ShipBlueprint.Condition (PRISTINE=0, DAMAGED=1, WRECKED=2).
        public const long CONDITION_PRISTINE = 0;
        public const long CONDITION_DAMAGED = 1;
        public const long CONDITION_WRECKED = 2;

        /// <summary>Health a "broken" subcomponent is set to.</summary>
        public const double DAMAGED_HEALTH = 0.2;

        /// <summary>system_id -> ShipSystem.</summary>
        public Dictionary<string, ShipSystem> Systems = new Dictionary<string, ShipSystem>(StringComparer.Ordinal);
        public List<string> SystemOrder = new List<string>();

        /// <summary>
        /// GDScript <c>FileAccess.get_file_as_string</c> + <c>JSON.parse_string</c>; anything but a dictionary
        /// (missing file, parse failure) returns {}.
        /// </summary>
        public GdDict LoadDefinitions()
        {
            GdDict parsed = CatalogRegistry.LoadDict(DEFINITIONS_PATH);
            if (parsed == null)
                return new GdDict();
            return parsed;
        }

        public void Configure(GdDict definitions, long condition, long seedValue)
        {
            Systems.Clear();
            SystemOrder.Clear();
            if (definitions == null || definitions.IsEmpty)
                return;
            object systemsVariant = definitions.Get("systems", new GdArray());
            if (!(systemsVariant is GdArray systemsArr))
                return;
            foreach (object sysVariant in systemsArr)
            {
                if (!(sysVariant is GdDict sysDef))
                    continue;
                string sid = V.Str(sysDef.Get("system_id", ""));
                if (sid.Length == 0)
                    continue;
                var deps = new List<string>();
                foreach (object d in IterOrEmpty(sysDef.Get("dependency_ids", new GdArray())))
                    deps.Add(V.Str(d));
                ShipSystem system;
                if (sid == "life_support")
                    system = new LifeSupportSystem(sid, deps);
                else
                    system = new ShipSystem(sid, deps);
                foreach (object subVariant in IterOrEmpty(sysDef.Get("subcomponents", new GdArray())))
                {
                    if (!(subVariant is GdDict subDef))
                        continue;
                    var parts = new List<string>();
                    foreach (object p in IterOrEmpty(subDef.Get("required_parts", new GdArray())))
                        parts.Add(V.Str(p));
                    var tools = new List<string>();
                    foreach (object t in IterOrEmpty(subDef.Get("required_tools", new GdArray())))
                        tools.Add(V.Str(t));
                    var sub = new ShipSubcomponent(
                        V.Str(subDef.Get("subcomponent_id", "")),
                        parts,
                        tools,
                        V.I64(subDef.Get("min_skill", 0L)),
                        V.F64(subDef.Get("repair_seconds", 5.0)),
                        V.F64(subDef.Get("operational_threshold", 0.5)));
                    system.AddSubcomponent(sub);
                }
                Systems[sid] = system;
                SystemOrder.Add(sid);
            }
            ApplyConditionDamage(condition, seedValue);
        }

        // GDScript `for x in d.get(k, [])` iterates an Array; the data always carries arrays here.
        static IEnumerable<object> IterOrEmpty(object v) => v as GdArray ?? new GdArray();

        /// <summary>
        /// Deterministically damages subcomponents based on condition. A seeded RNG walks subcomponents in
        /// declaration order so the same (condition, seed) always produces the same damage set.
        /// </summary>
        void ApplyConditionDamage(long condition, long seedValue)
        {
            double breakChance;
            switch (condition)
            {
                case CONDITION_PRISTINE:
                    breakChance = 0.0;
                    break;
                case CONDITION_DAMAGED:
                    breakChance = 0.4;
                    break;
                case CONDITION_WRECKED:
                    breakChance = 0.8;
                    break;
                default:
                    breakChance = 0.0;
                    break;
            }
            if (breakChance <= 0.0)
                return;
            var rng = GodotRandom.FromSeed(seedValue);
            foreach (string sid in SystemOrder)
            {
                foreach (ShipSubcomponent sub in Systems[sid].Subcomponents)
                {
                    if (rng.Randf() < breakChance)
                        sub.Health = DAMAGED_HEALTH;
                }
            }
        }

        public ShipSystem GetSystem(string systemId) =>
            Systems.TryGetValue(systemId, out ShipSystem s) ? s : null;

        /// <summary>
        /// Derived operational status: self-functional AND every dependency operational.
        /// Cycle-safe via the visiting set (no cycles are expected in the data).
        /// </summary>
        public bool IsOperational(string systemId) => ResolveOperational(systemId, new HashSet<string>(StringComparer.Ordinal));

        bool ResolveOperational(string systemId, HashSet<string> visiting)
        {
            if (!Systems.ContainsKey(systemId))
                return false;
            if (visiting.Contains(systemId))
                return false;
            ShipSystem system = Systems[systemId];
            if (!system.IsSelfFunctional())
                return false;
            visiting.Add(systemId);
            foreach (string dep in system.DependencyIds)
            {
                if (!ResolveOperational(dep, visiting))
                {
                    visiting.Remove(systemId); // stack-pop: no longer an ancestor on the current path
                    return false;
                }
            }
            visiting.Remove(systemId); // stack-pop: no longer an ancestor on the current path
            return true;
        }

        /// <summary>
        /// Flat, ordered list of every subcomponent health — used by smokes to assert deterministic builds
        /// without depending on dictionary ordering.
        /// </summary>
        public GdArray GetSummaryHealthList()
        {
            var output = new GdArray();
            foreach (string sid in SystemOrder)
                foreach (ShipSubcomponent sub in Systems[sid].Subcomponents)
                    output.Add(sub.Health);
            return output;
        }

        /// <summary>
        /// Ticks every system with its resolved operational status. Only LifeSupport acts on the time delta
        /// (oxygen drain when offline).
        /// </summary>
        public void Advance(double delta)
        {
            foreach (string sid in SystemOrder)
                Systems[sid].Advance(delta, IsOperational(sid));
        }

        static GdDict Rejection(string reason) =>
            new GdDict { { "success", false }, { "reason", reason }, { "seconds", 0.0 } };

        /// <summary>
        /// Parameterized repair routed to the named subcomponent. Returns the subcomponent's RepairResult, or an
        /// unknown_system / unknown_subcomponent rejection.
        /// </summary>
        public GdDict Repair(string systemId, string subcomponentId, GdArray availableParts, GdArray availableTools, long skillLevel)
        {
            if (!Systems.ContainsKey(systemId))
                return Rejection("unknown_system");
            ShipSubcomponent sub = Systems[systemId].GetSubcomponent(subcomponentId);
            if (sub == null)
                return Rejection("unknown_subcomponent");
            return sub.Repair(availableParts, availableTools, skillLevel);
        }

        /// <summary>
        /// Gated repair fed by a player InventoryState. Gathers the carried part/tool ids, runs the deterministic
        /// Repair(), and on success consumes ONE of each required part. Consumes nothing on failure. Returns the
        /// Repair() result dict.
        /// </summary>
        public GdDict RepairWithInventory(string systemId, string subcomponentId, CargoTransfer.ICargoHold inventoryState, long skillLevel)
        {
            if (!Systems.ContainsKey(systemId))
                return Rejection("unknown_system");
            ShipSubcomponent sub = Systems[systemId].GetSubcomponent(subcomponentId);
            if (sub == null)
                return Rejection("unknown_subcomponent");
            var availableParts = new GdArray();
            var availableTools = new GdArray();
            if (inventoryState != null)
            {
                foreach (object entry in inventoryState.GetItemsByCategory("part"))
                    availableParts.Add(V.Str(((GdDict)entry)["id"]));
                foreach (object entry in inventoryState.GetItemsByCategory("tool"))
                    availableTools.Add(V.Str(((GdDict)entry)["id"]));
            }
            GdDict result = sub.Repair(availableParts, availableTools, skillLevel);
            if (V.Bool(result.Get("success", false)) && inventoryState != null)
            {
                foreach (string part in sub.RequiredParts)
                    inventoryState.RemoveItem(part, 1);
            }
            return result;
        }

        /// <summary>
        /// Deterministically brings a subcomponent to full health (operational). Used by the objective bridge,
        /// which has no parts/tools/skill inventory feeding the gated Repair() path yet. Returns false for an
        /// unknown system or subcomponent.
        /// </summary>
        public bool ForceRepair(string systemId, string subcomponentId)
        {
            if (!Systems.ContainsKey(systemId))
                return false;
            ShipSubcomponent sub = Systems[systemId].GetSubcomponent(subcomponentId);
            if (sub == null)
                return false;
            sub.Health = 1.0;
            return true;
        }

        /// <summary>
        /// Reduces every subcomponent's health in a system by <paramref name="amount"/> (clamp &gt;= 0). Used by the
        /// coordinator to apply fire -> system degradation. Returns false for an unknown system.
        /// </summary>
        public bool DamageSystem(string systemId, double amount)
        {
            if (!Systems.ContainsKey(systemId) || amount <= 0.0)
                return Systems.ContainsKey(systemId);
            foreach (ShipSubcomponent sub in Systems[systemId].Subcomponents)
                sub.Health = Math.Max(0.0, sub.Health - amount);
            return true;
        }

        /// <summary>
        /// REQ-CMP-002: damage one named subcomponent (e.g. when its physical component is stripped).
        /// Returns false if system/sub unknown.
        /// </summary>
        public bool DamageSubcomponent(string systemId, string subcomponentId, double amount = 1.0)
        {
            if (!Systems.ContainsKey(systemId) || amount <= 0.0 || string.IsNullOrEmpty(subcomponentId))
                return false;
            ShipSubcomponent sub = Systems[systemId].GetSubcomponent(subcomponentId);
            if (sub == null)
                return false;
            sub.Health = Math.Max(0.0, sub.Health - amount);
            return true;
        }

        /// <summary>
        /// REQ-CMP-002: remounting a physical component reconnects the linked sub at partial health. Does not
        /// full-repair (that remains a WorkAction / repair_point); sets health to at least min_health.
        /// </summary>
        public bool RestoreSubcomponentOnRemount(string systemId, string subcomponentId, double minHealth = 0.55)
        {
            if (!Systems.ContainsKey(systemId) || string.IsNullOrEmpty(subcomponentId))
                return false;
            ShipSubcomponent sub = Systems[systemId].GetSubcomponent(subcomponentId);
            if (sub == null)
                return false;
            double target = GdMath.Clampf(minHealth, 0.0, 1.0);
            if (sub.Health < target)
                sub.Health = target;
            return true;
        }

        /// <summary>Returns a lightweight {system_id: {operational, health}} dict for HUD/diagnostics.</summary>
        public GdDict GetStatusSummary()
        {
            var output = new GdDict();
            foreach (string sid in SystemOrder)
                output[sid] = new GdDict { { "operational", IsOperational(sid) }, { "health", Systems[sid].Health() } };
            return output;
        }

        /// <summary>Returns a full snapshot of every system's subcomponent state for save/load round-trips.</summary>
        public GdDict GetSummary()
        {
            var sysSummaries = new GdDict();
            foreach (string sid in SystemOrder)
                sysSummaries[sid] = Systems[sid].GetSummary();
            return new GdDict { { "systems", sysSummaries }, { "system_order", GdString.ToGdArray(SystemOrder) } };
        }

        /// <summary>
        /// Restores per-system subcomponent state by id. Does NOT restore <c>system_order</c> (that is established
        /// by Configure and must match before calling this).
        /// </summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            object sysSummariesVariant = summary.Get("systems", new GdDict());
            if (!(sysSummariesVariant is GdDict sysSummaries))
                return false;
            bool changed = false;
            foreach (var kv in sysSummaries)
            {
                // Dictionary keys from JSON are strings; a non-string key can never match a system id.
                if (!(kv.Key is string sid) || !Systems.ContainsKey(sid))
                    continue;
                if (kv.Value is GdDict sysSummary)
                {
                    if (Systems[sid].ApplySummary(sysSummary))
                        changed = true;
                }
            }
            return changed;
        }
    }
}
