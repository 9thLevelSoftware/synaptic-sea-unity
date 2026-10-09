using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Systems;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Phase 1.9: the one definition of the ship-systems damage a New Run opens with. <c>RunSession</c> (build and reload) and the
    /// starting-home guarantee both call it, so the parts the guarantee puts in the home are computed from exactly the damage the
    /// session will roll (an equality test pins the two together).
    /// <list type="bullet">
    /// <item>Every blueprint: the condition roll (<see cref="ShipSystemsManager.Configure"/>), then every propulsion part healthy except
    /// <c>nav_linkage</c>, so the opening blocker is one low-skill repair. This is the rule the golden hub has always used.</item>
    /// <item>A generated home (<see cref="ShipBlueprint.StartKind"/> = <see cref="GeneratedHomeKind"/>): the same, plus a seeded 0-2 more
    /// breaks drawn from a short list of cheap power and navigation parts (a power cell, a data core or a circuit board each). Life support,
    /// scanners and gravity are never touched, so the survivor always starts with air, a working scanner and gravity.</item>
    /// </list>
    /// </summary>
    public static class HomeOpeningState
    {
        public const string GeneratedHomeKind = "generated_home_v1";
        public const int MaxExtraBreaks = 2;
        /// <summary>Salt for the extra-break stream, distinct from every other seeded stream of the home.</summary>
        const int ExtraBreakSalt = 0x0B0A7;

        /// <summary>The cheap travel-gating parts a generated home may open with broken, in declaration order.</summary>
        public static readonly (string System, string Sub)[] ExtraBreakCandidates =
        {
            ("power", "battery_cells"),
            ("navigation", "star_charts"),
            ("power", "power_distribution"),
            ("navigation", "nav_computer"),
        };

        public static bool IsGeneratedHome(ShipBlueprint blueprint) => blueprint != null && blueprint.StartKind == GeneratedHomeKind;

        /// <summary>A fresh manager configured for <paramref name="blueprint"/> with the opening damage applied.</summary>
        public static ShipSystemsManager Build(ShipBlueprint blueprint)
        {
            var manager = new ShipSystemsManager();
            Configure(manager, blueprint);
            return manager;
        }

        /// <summary>Reconfigures <paramref name="manager"/> from <paramref name="blueprint"/> and applies the opening damage.</summary>
        public static void Configure(ShipSystemsManager manager, ShipBlueprint blueprint)
        {
            if (manager == null || blueprint == null) return;
            manager.Configure(manager.LoadDefinitions(), blueprint.ShipCondition, blueprint.SeedValue);
            ApplyOpeningDamage(manager, blueprint);
        }

        /// <summary>The opening damage on top of the condition roll. Deterministic for a given blueprint.</summary>
        public static void ApplyOpeningDamage(ShipSystemsManager manager, ShipBlueprint blueprint)
        {
            if (manager == null) return;
            ShipSystem propulsion = manager.GetSystem("propulsion");
            if (propulsion != null)
            {
                foreach (ShipSubcomponent sub in propulsion.Subcomponents) sub.Health = 1.0;
                ShipSubcomponent blocker = propulsion.GetSubcomponent("nav_linkage");
                if (blocker != null) blocker.Health = ShipSystemsManager.DAMAGED_HEALTH;
            }
            if (!IsGeneratedHome(blueprint)) return;
            foreach ((string system, string sub) in ExtraBreaks(blueprint))
            {
                ShipSubcomponent part = manager.GetSystem(system)?.GetSubcomponent(sub);
                if (part != null) part.Health = ShipSystemsManager.DAMAGED_HEALTH;
            }
        }

        /// <summary>The extra parts a generated home opens with broken: 0-2 distinct entries of <see cref="ExtraBreakCandidates"/>, seeded.</summary>
        public static List<(string System, string Sub)> ExtraBreaks(ShipBlueprint blueprint)
        {
            var result = new List<(string System, string Sub)>();
            if (!IsGeneratedHome(blueprint)) return result;
            GodotRandom rng = GodotRandom.FromSeed(FirstRunAwayGate.DeriveSeed(blueprint.SeedValue, ExtraBreakSalt));
            int count = (int)rng.RandiRange(0, MaxExtraBreaks);
            var pool = new List<(string System, string Sub)>(ExtraBreakCandidates);
            for (int i = 0; i < count && pool.Count > 0; i++)
            {
                int pick = (int)rng.RandiRange(0, pool.Count - 1);
                result.Add(pool[pick]);
                pool.RemoveAt(pick);
            }
            return result;
        }
    }
}
