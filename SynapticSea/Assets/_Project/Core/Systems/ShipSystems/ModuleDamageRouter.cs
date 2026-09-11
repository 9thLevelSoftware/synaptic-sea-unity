// Ported from scripts/systems/module_damage_router.gd @ 96ecb2b0

using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The members of ModuleIntegrityMap that <see cref="ModuleDamageRouter"/> calls. The GDScript probed each one
    /// with <c>has_method</c>; ModuleIntegrityMap has all four, so the port requires them through this interface
    /// (ModuleIntegrityMap's C# port should implement it).
    /// </summary>
    public interface IModuleIntegrityMap
    {
        /// <summary><c>get_state(module_id)</c>: the module's state, "intact" when unregistered.</summary>
        string GetState(string moduleId);

        /// <summary><c>apply_damage(module_id, amount, kind)</c>: registers the module if needed; returns the new state.</summary>
        string ApplyDamage(string moduleId, double amount, string kind = "");

        /// <summary><c>module_ids()</c>: registered ids, sorted.</summary>
        IEnumerable<string> ModuleIds();

        /// <summary><c>get_module(module_id)</c>: the record or null.</summary>
        ModuleIntegrityState GetModule(string moduleId);
    }

    /// <summary>
    /// REQ-MI-004: single entry for structure damage into ModuleIntegrityMap.
    /// Sources: fire | decompression | threat | tool. Pure: never touches the scene tree.
    /// </summary>
    public static class ModuleDamageRouter
    {
        public const string SOURCE_FIRE = "fire";
        public const string SOURCE_DECOMPRESSION = "decompression";
        public const string SOURCE_THREAT = "threat";
        public const string SOURCE_TOOL = "tool";

        public const double DEFAULT_FIRE_AMOUNT = 0.08;
        public const double DEFAULT_DECOMPRESSION_AMOUNT = 0.25;
        public const double DEFAULT_THREAT_AMOUNT = 0.35;
        public const double DEFAULT_TOOL_AMOUNT = 1.0;

        /// <summary>
        /// Apply damage from a named source. Returns { ok, source, module_id, amount, state_before, state_after, reason }.
        /// resist: 0..1 fraction reduced before apply (e.g. hub hull_plating_bonus).
        /// </summary>
        public static GdDict Apply(
            IModuleIntegrityMap moduleMap,
            string moduleId,
            string source,
            double amount = -1.0,
            string kind = "",
            double resist = 0.0)
        {
            var output = new GdDict
            {
                { "ok", false },
                { "source", source },
                { "module_id", moduleId },
                { "amount", 0.0 },
                { "state_before", "" },
                { "state_after", "" },
                { "reason", "" },
            };
            if (moduleMap == null || string.IsNullOrEmpty(moduleId))
            {
                output["reason"] = "bad_args";
                return output;
            }
            if (!IsKnownSource(source))
            {
                output["reason"] = "unknown_source";
                return output;
            }
            double dmg = amount;
            if (dmg < 0.0)
                dmg = DefaultAmount(source);
            double r = GdMath.Clampf(resist, 0.0, 0.9);
            if (r > 0.0)
                dmg *= (1.0 - r);
            if (dmg <= 0.0)
            {
                output["reason"] = "zero_damage";
                return output;
            }
            output["amount"] = dmg;
            output["state_before"] = moduleMap.GetState(moduleId);
            // The GDScript fell through to reason "no_apply" when the map lacked apply_damage; the interface
            // guarantees it, so that branch cannot occur here.
            string after = moduleMap.ApplyDamage(moduleId, dmg, kind);
            output["state_after"] = after;
            output["ok"] = true;
            return output;
        }

        /// <summary>
        /// Damage all wall modules whose room_id maps to a compartment via role map.
        /// compartment_for_role: room_role -> compartment_id. Returns list of module_ids changed.
        /// </summary>
        public static GdArray ApplyDecompressionToCompartment(
            IModuleIntegrityMap moduleMap,
            GdDict layout,
            string compartmentId,
            GdDict compartmentForRole,
            double amount = DEFAULT_DECOMPRESSION_AMOUNT)
        {
            var changed = new GdArray();
            if (moduleMap == null || string.IsNullOrEmpty(compartmentId) || amount <= 0.0)
                return changed;
            compartmentForRole = compartmentForRole ?? new GdDict();
            object roomsV = layout?.Get("rooms", new GdArray());
            if (!(roomsV is GdArray rooms))
                return changed;
            var seen = new GdDict();
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room))
                    continue;
                string role = V.Str(room.Get("room_role", room.Get("role", "")));
                string comp = V.Str(compartmentForRole.Get(role, role));
                if (comp != compartmentId && role != compartmentId)
                {
                    // Also match room id directly for simple layouts.
                    if (V.Str(room.Get("id", "")) != compartmentId)
                        continue;
                }
                string roomId = V.Str(room.Get("id", ""));
                GdArray registered = RegisteredIdsForRoom(moduleMap, roomId);
                if (!registered.IsEmpty)
                {
                    foreach (object midV in registered)
                    {
                        string mid = V.Str(midV);
                        if (seen.Has(mid))
                            continue;
                        seen[mid] = true;
                        ModuleIntegrityState rec = moduleMap.GetModule(mid);
                        string kind = rec != null ? rec.Kind : "";
                        GdDict r = Apply(moduleMap, mid, SOURCE_DECOMPRESSION, amount, kind);
                        if (r.GetBool("ok", false))
                            changed.Add(mid);
                    }
                    continue;
                }
                object placementsV = room.Get("structural_placements", new GdArray());
                if (!(placementsV is GdArray placements))
                {
                    // Fall back: damage any registered modules with this room_id prefix.
                    // module_ids() returned a PackedStringArray copy; snapshot it before applying damage.
                    foreach (string mid in new List<string>(moduleMap.ModuleIds()))
                    {
                        if (ShipCompat.BeginsWith(mid, roomId + "/") || ShipCompat.GdContains(mid, roomId))
                        {
                            GdDict r = Apply(moduleMap, mid, SOURCE_DECOMPRESSION, amount);
                            if (r.GetBool("ok", false))
                                changed.Add(mid);
                        }
                    }
                    continue;
                }
                foreach (object pV in placements)
                {
                    if (!(pV is GdDict p))
                        continue;
                    string kind = V.Str(p.Get("module_id", p.Get("module", "")));
                    string pname = V.Str(p.Get("name", kind));
                    string mid2 = roomId + "/" + pname;
                    GdDict r2 = Apply(moduleMap, mid2, SOURCE_DECOMPRESSION, amount, kind);
                    if (r2.GetBool("ok", false))
                        changed.Add(mid2);
                }
            }
            return changed;
        }

        static GdArray RegisteredIdsForRoom(IModuleIntegrityMap moduleMap, string roomId)
        {
            var output = new GdArray();
            if (moduleMap == null || string.IsNullOrEmpty(roomId))
                return output;
            foreach (string mid in new List<string>(moduleMap.ModuleIds()))
            {
                ModuleIntegrityState rec = moduleMap.GetModule(mid);
                if (rec == null)
                    continue;
                if (rec.RoomId != roomId)
                {
                    bool owned = rec.OwnerRooms != null && rec.OwnerRooms.Contains(roomId);
                    if (!owned)
                        continue;
                }
                output.Add(mid);
            }
            return output;
        }

        /// <summary>Threat structure strike against a single module (hull tendril fantasy).</summary>
        public static GdDict ApplyThreatStructureHit(
            IModuleIntegrityMap moduleMap,
            string moduleId,
            double amount = DEFAULT_THREAT_AMOUNT,
            string kind = "",
            double resist = 0.0) =>
            Apply(moduleMap, moduleId, SOURCE_THREAT, amount, kind, resist);

        /// <summary>
        /// Player tool damage (WorkAction cut/pry already uses map.apply_damage; this keeps a uniform source tag
        /// for audits/smokes).
        /// </summary>
        public static GdDict ApplyToolDamage(
            IModuleIntegrityMap moduleMap,
            string moduleId,
            double amount = DEFAULT_TOOL_AMOUNT,
            string kind = "",
            double resist = 0.0) =>
            Apply(moduleMap, moduleId, SOURCE_TOOL, amount, kind, resist);

        static bool IsKnownSource(string source) =>
            source == SOURCE_FIRE || source == SOURCE_DECOMPRESSION || source == SOURCE_THREAT || source == SOURCE_TOOL;

        static double DefaultAmount(string source)
        {
            switch (source)
            {
                case SOURCE_FIRE: return DEFAULT_FIRE_AMOUNT;
                case SOURCE_DECOMPRESSION: return DEFAULT_DECOMPRESSION_AMOUNT;
                case SOURCE_THREAT: return DEFAULT_THREAT_AMOUNT;
                case SOURCE_TOOL: return DEFAULT_TOOL_AMOUNT;
                default: return 0.0;
            }
        }

        public static List<string> KnownSources() =>
            new List<string> { SOURCE_FIRE, SOURCE_DECOMPRESSION, SOURCE_THREAT, SOURCE_TOOL };
    }
}
