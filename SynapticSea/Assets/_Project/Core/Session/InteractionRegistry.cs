// The priority-ordered interact dispatcher of scripts/procgen/playable_generated_ship.gd @ 96ecb2b0
// (_on_player_interact_requested, 7953-8077) as one ordered handler table. docs/InteractionOrder.md mirrors it.
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>Which dispatcher branch a handler belongs to.</summary>
    public enum InteractionScope
    {
        Both,
        Home,
        Away,
    }

    /// <summary>One interaction handler: claims the request (returns true) or passes to the next.</summary>
    public sealed class InteractionHandler
    {
        public readonly string Id;
        public readonly InteractionScope Scope;
        public readonly string GodotSource;
        readonly Func<RunSession, Vec3, bool> _try;

        public InteractionHandler(string id, InteractionScope scope, string godotSource, Func<RunSession, Vec3, bool> tryHandle)
        {
            Id = id;
            Scope = scope;
            GodotSource = godotSource;
            _try = tryHandle;
        }

        public bool AppliesTo(SessionLocation location) =>
            Scope == InteractionScope.Both || (Scope == InteractionScope.Home) == (location == SessionLocation.Home);

        public bool TryHandle(RunSession session, Vec3 playerPosition) => _try(session, playerPosition);
    }

    /// <summary>
    /// The single ordered interaction table. Filtering it by <see cref="InteractionScope"/> reproduces both Godot chains
    /// exactly (home: stations/pickups/objectives; away: portals/hatches/derelict objectives). When nothing claims the
    /// request the session plays the soft-miss cue (<see cref="MissHandlerId"/>).
    /// </summary>
    public static class InteractionRegistry
    {
        public const string MissHandlerId = "miss_sfx";

        public static readonly IReadOnlyList<InteractionHandler> Handlers = new[]
        {
            new InteractionHandler("dock_barrier", InteractionScope.Both, "dock_barriers: b.try_start (unopened)", (s, p) => s.TryDockBarriers(p)),
            new InteractionHandler("bridge_terminal", InteractionScope.Both, "bridge_terminals: t.try_login", (s, p) => s.TryBridgeTerminals(p)),
            new InteractionHandler("fire_suppression_point", InteractionScope.Both, "fire_suppression_points: fp.try_start", (s, p) => s.TryFireSuppressionPoints(p)),
            new InteractionHandler("repair_point", InteractionScope.Both, "repair_points: rp.try_start", (s, p) => s.TryRepairPoints(p)),
            new InteractionHandler("breach_seal_point", InteractionScope.Both, "breach_seal_points: sp.try_start", (s, p) => s.TryBreachSealPoints(p)),
            new InteractionHandler("crafting_station", InteractionScope.Home, "crafting_stations: st.try_interact", (s, p) => s.TryCraftingStations(p)),
            new InteractionHandler("production_station", InteractionScope.Home, "production_stations: st.try_interact", (s, p) => s.TryProductionStations(p)),
            new InteractionHandler("loot_container", InteractionScope.Both, "loot_containers: lc.try_interact", (s, p) => s.TryLootContainers(p)),
            new InteractionHandler("authored_portal", InteractionScope.Away, "_try_authored_portal_interact", (s, p) => s.TryAuthoredPortalInteract(p)),
            new InteractionHandler("hatch_bypass", InteractionScope.Away, "_try_bypass_nearest_hatch", (s, p) => s.TryBypassNearestHatch(p)),
            new InteractionHandler("hatch_reseal", InteractionScope.Away, "_try_reseal_nearest_hatch", (s, p) => s.TryResealNearestHatch(p)),
            new InteractionHandler("derelict_objective", InteractionScope.Away, "derelict_interactables: it.try_interact", (s, p) => s.TryDerelictObjectives(p)),
            new InteractionHandler("tool_pickup", InteractionScope.Home, "_try_tool_pickup_interact(tool_pickup)", (s, p) => s.TryToolPickupInteract(s.ToolPickup, p)),
            new InteractionHandler("junction_calibrator_pickup", InteractionScope.Home, "_try_tool_pickup_interact(junction_calibrator_pickup)", (s, p) => s.TryToolPickupInteract(s.JunctionCalibratorPickup, p)),
            new InteractionHandler("home_objective", InteractionScope.Home, "interactables: interactable.try_interact", (s, p) => s.TryHomeObjectives(p)),
            new InteractionHandler("hangar", InteractionScope.Both, "_try_hangar_interact", (s, p) => s.TryHangarInteract(p)),
            new InteractionHandler("cargo_deposit", InteractionScope.Both, "_try_cargo_deposit", (s, p) => s.TryCargoDeposit(p)),
            new InteractionHandler("cart", InteractionScope.Both, "_try_cart_interact", (s, p) => s.TryCartInteract(p)),
            new InteractionHandler("work_yield_drop", InteractionScope.Both, "_try_work_yield_drop_interact", (s, p) => s.TryWorkYieldDropInteract(p)),
            new InteractionHandler("work_action", InteractionScope.Both, "_try_work_action_interact", (s, p) => s.TryWorkActionInteract(p)),
        };

        /// <summary>The handler ids in dispatch order for one location (the miss cue is implicit at the end).</summary>
        public static List<string> OrderFor(SessionLocation location)
        {
            var ids = new List<string>();
            foreach (InteractionHandler h in Handlers)
            {
                if (h.AppliesTo(location))
                    ids.Add(h.Id);
            }
            return ids;
        }
    }
}
