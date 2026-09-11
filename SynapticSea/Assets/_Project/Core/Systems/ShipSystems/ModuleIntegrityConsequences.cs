// Ported from scripts/systems/module_integrity_consequences.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// RUNTIME: one structural wrapper node (Godot: the placed module's Node3D with a <c>Visual</c> group holding
    /// <c>VisualInstance_Intact</c> / <c>VisualInstance_Damaged</c> / <c>VisualInstance_Breached</c>, or the legacy single
    /// <c>VisualInstance</c>; Unity: the StructuralModule component). Core decides WHAT changes; the Runtime layer
    /// implements these primitive scene effects. Used by <see cref="ModuleIntegrityConsequences.ApplyToNode"/> and
    /// <see cref="IntegrityVisualResolver"/>.
    /// </summary>
    public interface IModuleSceneView
    {
        /// <summary><c>node.name</c>: the module key the map is indexed by.</summary>
        string ModuleKey { get; }

        /// <summary><c>node.get_node_or_null("Visual") != null</c>.</summary>
        bool HasVisualGroup { get; }

        /// <summary>
        /// <c>node.get_node_or_null("Visual/" + childName) != null</c> for "VisualInstance_Intact",
        /// "VisualInstance_Damaged", "VisualInstance_Breached" or the legacy "VisualInstance". Visual children are
        /// always Node3D in the wrapper scenes, so "exists" and "is Node3D" are the same test.
        /// </summary>
        bool HasVisual(string childName);

        /// <summary><c>(Visual/childName as Node3D).visible = visible</c>.</summary>
        void SetVisualVisible(string childName, bool visible);

        /// <summary>
        /// Albedo material override on every mesh under <paramref name="childName"/> (<c>Visual/childName</c>), or under
        /// the whole wrapper when <paramref name="childName"/> is null. Alpha below 0.99 switches the material to
        /// alpha transparency.
        /// </summary>
        void TintMeshes(string childName, double r, double g, double b, double a);

        /// <summary><c>CollisionShape3D/CollisionPolygon3D.disabled = !enabled</c> for every collider under the wrapper.</summary>
        void SetCollisionEnabled(bool enabled);

        /// <summary>
        /// <c>node.set_meta(key, value)</c>: "integrity_state" (string), "mesh_suffix" (string), "crawl_passable",
        /// "atmosphere_link", "nav_gap" (bool).
        /// </summary>
        void SetMeta(string key, object value);
    }

    /// <summary>
    /// PKG-B2.1b: pure scene-consequence contract for ModuleIntegrityState (ADR-0051). Maps integrity states to
    /// collision / passability / atmosphere / mesh tags. Also seeds maps from layouts and routes fire damage into
    /// wall modules.
    /// </summary>
    public static class ModuleIntegrityConsequences
    {
        public static readonly IReadOnlyList<string> WALL_PREFIXES = new[]
        {
            "wall_", "bulkhead_", "panel_", "door_",
        };

        public static readonly IReadOnlyList<string> STRUCTURAL_PREFIXES = new[]
        {
            "wall_", "bulkhead_", "panel_", "door_",
            "doorway_", "floor_", "corridor_", "pillar_", "ramp_",
        };

        /// <summary>Damage to a wall module per unit fire intensity per second.</summary>
        public const double FIRE_MODULE_DAMAGE_PER_INTENSITY = 0.08;

        public static bool IsWallKind(string kind)
        {
            if (string.IsNullOrEmpty(kind))
                return false;
            string k = kind.ToLowerInvariant();
            foreach (string prefix in WALL_PREFIXES)
            {
                if (GdString.BeginsWith(k, prefix) || GdString.Find(k, prefix) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>True for every P0 structural module family, including floors and traversal pieces.</summary>
        public static bool IsStructuralKind(string kind)
        {
            if (string.IsNullOrEmpty(kind))
                return false;
            string k = kind.ToLowerInvariant();
            foreach (string prefix in STRUCTURAL_PREFIXES)
            {
                if (GdString.BeginsWith(k, prefix) || GdString.Find(k, prefix) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>Pure consequence descriptor consumed by scene / nav layers.</summary>
        public static GdDict ConsequenceForState(string state)
        {
            switch (state)
            {
                case ModuleIntegrityState.STATE_INTACT:
                    return new GdDict
                    {
                        { "state", state },
                        { "collision_enabled", true },
                        { "crawl_passable", false },
                        { "atmosphere_link", false },
                        { "nav_gap", false },
                        { "mesh_suffix", "" },
                        { "modulate", GdArray.Of(1.0, 1.0, 1.0, 1.0) },
                    };
                case ModuleIntegrityState.STATE_DAMAGED:
                    return new GdDict
                    {
                        { "state", state },
                        { "collision_enabled", true },
                        { "crawl_passable", false },
                        { "atmosphere_link", false },
                        { "nav_gap", false },
                        { "mesh_suffix", "_damaged" },
                        { "modulate", GdArray.Of(0.85, 0.70, 0.55, 1.0) },
                    };
                case ModuleIntegrityState.STATE_BREACHED:
                    return new GdDict
                    {
                        { "state", state },
                        { "collision_enabled", true },
                        { "crawl_passable", true },
                        { "atmosphere_link", true },
                        { "nav_gap", true },
                        { "mesh_suffix", "_breached" },
                        { "modulate", GdArray.Of(0.55, 0.60, 0.75, 1.0) },
                    };
                case ModuleIntegrityState.STATE_DESTROYED:
                    return new GdDict
                    {
                        { "state", state },
                        { "collision_enabled", false },
                        { "crawl_passable", true },
                        { "atmosphere_link", true },
                        { "nav_gap", true },
                        { "mesh_suffix", "_destroyed" },
                        { "modulate", GdArray.Of(0.35, 0.35, 0.35, 0.55) },
                    };
                default:
                    return ConsequenceForState(ModuleIntegrityState.STATE_INTACT);
            }
        }

        /// <summary>Count wall modules that open atmosphere (breached or destroyed).</summary>
        public static long DerivedBreachCount(ModuleIntegrityMap moduleMap)
        {
            if (moduleMap == null)
                return 0;
            long count = 0;
            GdDict summary = moduleMap.GetSummary();
            object deltas = summary.Get("deltas", new GdArray());
            if (!(deltas is GdArray deltasArr))
            {
                // fall back: walk registered modules via fingerprint-like iteration
                return CountBreachesViaSize(moduleMap);
            }
            foreach (object entry in deltasArr)
            {
                if (!(entry is GdDict row))
                    continue;
                string kind = V.Str(row.Get("kind", ""));
                if (!IsWallKind(kind))
                    continue;
                string st = V.Str(row.Get("state", ""));
                if (st == ModuleIntegrityState.STATE_BREACHED || st == ModuleIntegrityState.STATE_DESTROYED)
                    count += 1;
            }
            return count;
        }

        // Prefer explicit iteration API if present (ModuleIntegrityMap always has count_wall_breaches).
        static long CountBreachesViaSize(ModuleIntegrityMap moduleMap) => moduleMap.CountWallBreaches();

        /// <summary>Seed wall modules from a layout.json-shaped document. Returns modules registered.</summary>
        public static long SeedMapFromLayout(ModuleIntegrityMap moduleMap, GdDict layout) =>
            SeedMapFromLayoutFiltered(moduleMap, layout, IsWallKind);

        /// <summary>
        /// Seed every P0 structural module family from a layout.json-shaped document. This companion path preserves
        /// the wall-only seed API for existing callers while allowing visual/integrity systems to register floors,
        /// corridors, pillars, ramps, and doorway modules as well.
        /// </summary>
        public static long SeedStructuralMapFromLayout(ModuleIntegrityMap moduleMap, GdDict layout) =>
            SeedMapFromLayoutFiltered(moduleMap, layout, IsStructuralKind);

        /// <summary>
        /// Register compiler-keyed modules (floor/&lt;cell&gt;, edge/&lt;edge&gt;, ceiling/&lt;cell&gt;) and optionally
        /// apply layout.module_damage. Falls back to placement names when no plan.
        /// </summary>
        public static long SeedMapFromCompiledLayout(ModuleIntegrityMap moduleMap, GdDict layout, bool applyWreck = true)
        {
            if (moduleMap == null)
                return 0;
            object planV = layout.Get("structural_plan", new GdDict());
            if (!(planV is GdDict plan) || plan.IsEmpty)
                return SeedMapFromLayout(moduleMap, layout);
            long registered = 0;
            registered += SeedCompiledRecords(moduleMap, plan.Get("floor_placements", new GdArray()), "floor");
            registered += SeedCompiledRecords(moduleMap, plan.Get("placements", new GdArray()), "edge");
            registered += SeedCompiledRecords(moduleMap, plan.Get("ceiling_placements", new GdArray()), "ceiling");
            if (!applyWreck)
                return registered;
            object mdV = layout.Get("module_damage", new GdArray());
            if (mdV is GdArray md)
            {
                foreach (object rowV in md)
                {
                    if (!(rowV is GdDict row))
                        continue;
                    string mid = V.Str(row.Get("module_key", row.Get("module_id", "")));
                    if (mid.Length == 0)
                        continue;
                    string kind = V.Str(row.Get("kind", ""));
                    string roomId = V.Str(row.Get("room_id", ""));
                    moduleMap.EnsureModule(mid, kind, new GdDict(), roomId);
                    double amount = V.F64(row.Get("amount", 0.0));
                    if (amount > 0.0)
                        moduleMap.ApplyDamage(mid, amount, kind);
                }
            }
            return registered;
        }

        static long SeedCompiledRecords(ModuleIntegrityMap moduleMap, object recordsV, string layer)
        {
            if (!(recordsV is GdArray records))
                return 0;
            long n = 0;
            foreach (object recV in records)
            {
                if (!(recV is GdDict rec))
                    continue;
                string kind = V.Str(rec.Get("module_id", rec.Get("module", "")));
                string keyPart = layer == "edge"
                    ? V.Str(rec.Get("edge_key", rec.Get("key", "")))
                    : V.Str(rec.Get("cell_key", ""));
                if (keyPart.Length == 0)
                    continue;
                string mid = layer + "/" + keyPart;
                var owners = new List<string>();
                object roomsV = rec.Get("room_ids", new GdArray());
                if (roomsV is GdArray rooms)
                {
                    foreach (object ridV in rooms)
                    {
                        string rid = V.Str(ridV);
                        if (rid.Length != 0 && !owners.Contains(rid))
                            owners.Add(rid);
                    }
                }
                string roomId = V.Str(rec.Get("room_id", ""));
                if (roomId.Length != 0 && !owners.Contains(roomId))
                    owners.Insert(0, roomId);
                string primary = owners.Count > 0 ? owners[0] : "";
                ModuleIntegrityState inst = moduleMap.EnsureModule(mid, kind, new GdDict(), primary);
                if (inst != null && owners.Count > 1)
                    inst.OwnerRooms = owners;
                n += 1;
            }
            return n;
        }

        static long SeedMapFromLayoutFiltered(ModuleIntegrityMap moduleMap, GdDict layout, Func<string, bool> kindFilter)
        {
            if (moduleMap == null)
                return 0;
            object roomsV = layout.Get("rooms", new GdArray());
            if (!(roomsV is GdArray rooms))
                return 0;
            long registered = 0;
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room))
                    continue;
                string roomId = V.Str(room.Get("id", ""));
                object placementsV = room.Get("structural_placements", new GdArray());
                if (!(placementsV is GdArray placements))
                    continue;
                foreach (object placementV in placements)
                {
                    if (!(placementV is GdDict placement))
                        continue;
                    string kind = V.Str(placement.Get("module_id", placement.Get("module", "")));
                    if (!kindFilter(kind))
                        continue;
                    string pname = V.Str(placement.Get("name", kind));
                    string mid = roomId + "/" + pname;
                    moduleMap.EnsureModule(mid, kind, new GdDict(), roomId);
                    registered += 1;
                }
            }
            return registered;
        }

        /// <summary>
        /// Apply fire intensity damage to wall modules in rooms belonging to burning compartments.
        /// compartment_for_role: room_role -> compartment_id; burning: compartment_id -> intensity.
        /// Returns list of module_ids whose state changed.
        /// </summary>
        public static GdArray ApplyFireDamage(
            IModuleIntegrityMap moduleMap,
            GdDict layout,
            GdDict burning,
            GdDict compartmentForRole,
            double delta,
            double damageRate = FIRE_MODULE_DAMAGE_PER_INTENSITY)
        {
            var changed = new GdArray();
            if (moduleMap == null || delta <= 0.0 || burning.IsEmpty)
                return changed;
            object roomsV = layout.Get("rooms", new GdArray());
            if (!(roomsV is GdArray rooms))
                return changed;
            // Shared compiled walls can belong to two burning compartments. Keep the max intensity per module so
            // room order cannot suppress a hotter fire.
            var exposure = new GdDict();
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room))
                    continue;
                string role = V.Str(room.Get("room_role", room.Get("role", "")));
                string compartment = V.Str(compartmentForRole.Get(role, ""));
                if (compartment.Length == 0 || !burning.Has(compartment))
                    continue;
                double intensity = V.F64(burning[compartment]);
                if (intensity <= 0.0)
                    continue;
                string roomId = V.Str(room.Get("id", ""));
                GdArray compiledIds = RegisteredIdsForRoom(moduleMap, roomId, true);
                if (!compiledIds.IsEmpty)
                {
                    foreach (object midV in compiledIds)
                    {
                        string mid = V.Str(midV);
                        ModuleIntegrityState rec = moduleMap.GetModule(mid);
                        string kind = rec != null ? rec.Kind : "";
                        AccumulateFireExposure(exposure, mid, intensity, kind);
                    }
                    continue;
                }
                object placementsV = room.Get("structural_placements", new GdArray());
                if (!(placementsV is GdArray placements))
                    continue;
                foreach (object placementV in placements)
                {
                    if (!(placementV is GdDict placement))
                        continue;
                    string kind = V.Str(placement.Get("module_id", placement.Get("module", "")));
                    if (!IsWallKind(kind))
                        continue;
                    string pname = V.Str(placement.Get("name", kind));
                    string mid = roomId + "/" + pname;
                    AccumulateFireExposure(exposure, mid, intensity, kind);
                }
            }
            foreach (object midV in new List<object>(exposure.Keys))
            {
                string mid = V.Str(midV);
                var recExp = (GdDict)exposure[mid];
                double dmg = damageRate * V.F64(recExp.Get("intensity", 0.0)) * delta;
                string kind = V.Str(recExp.Get("kind", ""));
                string before = moduleMap.GetState(mid);
                string after = moduleMap.ApplyDamage(mid, dmg, kind);
                if (after != before)
                    changed.Add(mid);
            }
            return changed;
        }

        static void AccumulateFireExposure(GdDict exposure, string mid, double intensity, string kind)
        {
            if (mid.Length == 0 || intensity <= 0.0)
                return;
            if (exposure.Has(mid))
            {
                var prev = (GdDict)exposure[mid];
                if (intensity > V.F64(prev.Get("intensity", 0.0)))
                    prev["intensity"] = intensity;
                if (V.Str(prev.Get("kind", "")).Length == 0 && kind.Length != 0)
                    prev["kind"] = kind;
                return;
            }
            exposure[mid] = new GdDict { { "intensity", intensity }, { "kind", kind } };
        }

        /// <summary>
        /// Registered map ids for a room. Prefers compiler <c>edge/</c> <c>floor/</c> <c>ceiling/</c> keys already
        /// seeded on the map; empty means the caller should use placement names.
        /// </summary>
        public static GdArray RegisteredIdsForRoom(IModuleIntegrityMap moduleMap, string roomId, bool wallsOnly)
        {
            var output = new GdArray();
            if (moduleMap == null || string.IsNullOrEmpty(roomId))
                return output;
            foreach (string mid in new List<string>(moduleMap.ModuleIds()))
            {
                ModuleIntegrityState rec = moduleMap.GetModule(mid);
                if (rec == null)
                    continue;
                if (!ModuleOwnedByRoom(rec, roomId))
                    continue;
                string kind = rec.Kind;
                if (wallsOnly && !IsWallKind(kind))
                    continue;
                output.Add(mid);
            }
            return output;
        }

        static bool ModuleOwnedByRoom(ModuleIntegrityState rec, string roomId)
        {
            if (rec.RoomId == roomId)
                return true;
            return rec.OwnerRooms != null && rec.OwnerRooms.Contains(roomId);
        }

        /// <summary>Apply collision / modulate consequences to a structural wrapper node.</summary>
        public static void ApplyToNode(IModuleSceneView node, string state)
        {
            if (node == null)
                return;
            GdDict cons = ConsequenceForState(state);
            // RUNTIME: node.set_meta(...) on the wrapper Node3D.
            node.SetMeta("integrity_state", state);
            node.SetMeta("mesh_suffix", V.Str(cons.Get("mesh_suffix", "")));
            node.SetMeta("crawl_passable", V.Bool(cons.Get("crawl_passable", false)));
            node.SetMeta("atmosphere_link", V.Bool(cons.Get("atmosphere_link", false)));
            node.SetMeta("nav_gap", V.Bool(cons.Get("nav_gap", false)));
            // Variant wrappers swap Intact/Damaged/Breached children; albedo tint fights IntegrityVisualResolver and
            // is reserved for legacy single-child meshes.
            if (!WrapperHasVariantVisuals(node))
            {
                object modV = cons.Get("modulate", GdArray.Of(1.0, 1.0, 1.0, 1.0));
                if (modV is GdArray mod && mod.Count >= 3)
                {
                    // RUNTIME: _tint_meshes walked every MeshInstance3D under the wrapper and set a
                    // StandardMaterial3D albedo override (alpha transparency below 0.99). Color is float32 in Godot.
                    node.TintMeshes(
                        null,
                        (float)V.F64(mod[0]),
                        (float)V.F64(mod[1]),
                        (float)V.F64(mod[2]),
                        mod.Count > 3 ? (float)V.F64(mod[3]) : 1.0f);
                }
            }
            bool collisionOn = V.Bool(cons.Get("collision_enabled", true));
            // RUNTIME: _set_collisions_enabled walked every CollisionShape3D / CollisionPolygon3D under the wrapper.
            node.SetCollisionEnabled(collisionOn);
        }

        static bool WrapperHasVariantVisuals(IModuleSceneView node)
        {
            if (node == null)
                return false;
            if (!node.HasVisualGroup)
                return false;
            return node.HasVisual("VisualInstance_Intact")
                || node.HasVisual("VisualInstance_Damaged")
                || node.HasVisual("VisualInstance_Breached");
        }

        /// <summary>Open nav gaps for rooms that have breached/destroyed walls (lower edge cost).</summary>
        public static void ApplyNavGaps(ShipNavGraph navGraph, GdArray roomIdsWithGaps)
        {
            if (navGraph == null || roomIdsWithGaps.IsEmpty)
                return;
            var gapSet = new GdDict();
            foreach (object rid in roomIdsWithGaps)
                gapSet[V.Str(rid)] = true;
            // Soften edges that touch gap rooms (walkable hole fantasy).
            GdDict nodes = navGraph.Nodes;
            foreach (object key in new List<object>(nodes.Keys))
            {
                string roomId = navGraph.GetNodeRoom(V.Str(key));
                if (!gapSet.Has(roomId))
                    continue;
                GdArray neigh = navGraph.Neighbors(V.Str(key));
                foreach (object n in neigh)
                {
                    if (!(n is GdDict nd))
                        continue;
                    string toId = V.Str(nd.Get("to", ""));
                    if (toId.Length == 0)
                        continue;
                    navGraph.SetEdgeCostMultiplier(V.Str(key), toId, 0.35);
                }
            }
        }
    }
}
