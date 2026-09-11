// Ported from scripts/procgen/room_variant_selector.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Deterministic per-room variant selection: same (role, room_index, seed, biome) always returns the same
    /// variant. Pure; RoomAssigner writes the result into the room dict under "variant".
    /// </summary>
    public sealed class RoomVariantSelector
    {
        public const string VARIANT_STANDARD = "standard";

        /// <summary>Role -&gt; variants; the first variant is always <see cref="VARIANT_STANDARD"/>.</summary>
        public static readonly GdDict VARIANTS_BY_ROLE = new GdDict
        {
            { "airlock", GdArray.Of("standard", "bio_seal", "maintenance_hatch", "cargo_lock") },
            { "corridor", GdArray.Of("standard", "narrow", "wide", "junction", "flooded", "collapsed", "biomatter_crusted") },
            { "main_spine", GdArray.Of("standard", "narrow", "wide", "junction") },
            { "bridge", GdArray.Of("standard", "command", "observation", "dark_bridge") },
            { "cargo", GdArray.Of("standard", "hold", "refrigerated", "secure", "empty_hold", "breached") },
            { "medical", GdArray.Of("standard", "triage", "surgery", "contaminated") },
            { "crew_quarters", GdArray.Of("standard", "bunks", "officer", "derelict_bunks") },
            { "engineering", GdArray.Of("standard", "reactor", "life_support", "propulsion", "burned_out", "breached") },
            { "maintenance", GdArray.Of("standard", "tool_storage", "junction", "sealed") },
            { "reactor", GdArray.Of("standard", "primary", "secondary", "unstable") },
            { "ramp", GdArray.Of("standard", "narrow", "service") },
            { "elevator", GdArray.Of("standard", "service", "cargo") },
            { "hub", GdArray.Of("standard", "central", "command") },
            { "dock", GdArray.Of("standard", "lifeboat", "cargo") },
            { "compartment", GdArray.Of("standard", "storage", "collapsed", "flooded") },
            { "bay", GdArray.Of("standard", "service", "cargo") },
            { "quarters", GdArray.Of("standard", "bunks", "officer", "derelict_bunks") },
            { "hangar", GdArray.Of("standard", "small_craft", "cargo") },
            { "mess_hall", GdArray.Of("standard", "long_table", "mess") },
            { "armory", GdArray.Of("standard", "locked", "sealed") },
            { "storage", GdArray.Of("standard", "general", "climate_controlled") },
            { "tool_storage", GdArray.Of("standard", "secure", "open_rack") },
            { "cockpit", GdArray.Of("standard", "command", "two_seat") },
            { "engine_bay", GdArray.Of("standard", "primary", "service") },
        };

        static GdDict Sim(string lootBias, string hazardKind, double weight)
        {
            var sim = new GdDict();
            if (lootBias != null) sim["loot_bias"] = lootBias;
            if (hazardKind != null) sim["hazard"] = new GdDict { { "kind", hazardKind }, { "weight", weight } };
            return sim;
        }

        /// <summary>
        /// Variant -&gt; gameplay/dressing effect payload. Sparse: unmapped variants resolve to {} via
        /// <see cref="EffectsFor"/>.
        /// </summary>
        public static readonly GdDict VARIANT_EFFECTS = new GdDict
        {
            // --- fire ---
            { "burned_out", new GdDict { { "sim", Sim("salvage_engineering", "fire", 0.6) }, { "dressing", "scorch" } } },
            { "unstable", new GdDict { { "sim", Sim(null, "fire", 0.5) }, { "dressing", "sparks" } } },
            // --- breach ---
            { "breached", new GdDict { { "sim", Sim("salvage_cargo", "breach", 0.6) }, { "dressing", "vacuum" } } },
            { "collapsed", new GdDict { { "sim", Sim(null, "breach", 0.4) }, { "dressing", "rubble" } } },
            // --- loot-bias only ---
            { "refrigerated", new GdDict { { "sim", Sim("salvage_cargo", null, 0.0) }, { "dressing", "frost" } } },
            { "secure", new GdDict { { "sim", Sim("hidden_cache", null, 0.0) }, { "dressing", "locked" } } },
            { "triage", new GdDict { { "sim", Sim("repair_parts_common", null, 0.0) }, { "dressing", "medical" } } },
            // --- dressing only ---
            { "flooded", new GdDict { { "dressing", "water_plane" } } },
            { "biomatter_crusted", new GdDict { { "dressing", "biomatter" } } },
            { "contaminated", new GdDict { { "dressing", "haze" } } },
        };

        public GdDict EffectsFor(string variant)
        {
            if (VARIANT_EFFECTS.Get(variant, new GdDict()) is GdDict raw) return raw.DeepCopy();
            return new GdDict();
        }

        static GdDict Preset(double fog, GdArray tint, double lightEnergy, GdArray lightColor, double propDensity) =>
            new GdDict
            {
                { "fog_density", fog },
                { "tint", tint },
                { "light_energy", lightEnergy },
                { "light_color", lightColor },
                { "prop_density", propDensity },
            };

        /// <summary>PKG-B5.1: visual/atmosphere preset per dressing id.</summary>
        public static readonly GdDict DRESSING_PRESETS = new GdDict
        {
            { "scorch", Preset(0.035, GdArray.Of(0.55, 0.22, 0.12, 1.0), 0.55, GdArray.Of(1.0, 0.45, 0.25, 1.0), 0.65) },
            { "sparks", Preset(0.02, GdArray.Of(0.45, 0.40, 0.20, 1.0), 0.85, GdArray.Of(1.0, 0.85, 0.40, 1.0), 0.80) },
            { "vacuum", Preset(0.0, GdArray.Of(0.15, 0.18, 0.28, 1.0), 0.25, GdArray.Of(0.55, 0.65, 1.0, 1.0), 0.40) },
            { "rubble", Preset(0.04, GdArray.Of(0.35, 0.32, 0.28, 1.0), 0.40, GdArray.Of(0.70, 0.65, 0.55, 1.0), 1.20) },
            { "frost", Preset(0.025, GdArray.Of(0.55, 0.72, 0.90, 1.0), 0.50, GdArray.Of(0.70, 0.85, 1.0, 1.0), 0.75) },
            { "locked", Preset(0.01, GdArray.Of(0.30, 0.30, 0.35, 1.0), 0.45, GdArray.Of(0.85, 0.80, 0.55, 1.0), 0.90) },
            { "medical", Preset(0.015, GdArray.Of(0.70, 0.85, 0.80, 1.0), 0.70, GdArray.Of(0.75, 1.0, 0.90, 1.0), 0.85) },
            { "water_plane", Preset(0.05, GdArray.Of(0.20, 0.35, 0.50, 1.0), 0.35, GdArray.Of(0.40, 0.60, 0.85, 1.0), 0.55) },
            { "biomatter", Preset(0.045, GdArray.Of(0.35, 0.15, 0.20, 1.0), 0.45, GdArray.Of(0.90, 0.25, 0.35, 1.0), 1.10) },
            { "haze", Preset(0.03, GdArray.Of(0.45, 0.50, 0.35, 1.0), 0.40, GdArray.Of(0.70, 0.75, 0.45, 1.0), 0.70) },
        };

        public GdDict DressingPreset(string dressingId)
        {
            if (string.IsNullOrEmpty(dressingId)) return new GdDict();
            if (DRESSING_PRESETS.Get(dressingId, new GdDict()) is GdDict raw) return raw.DeepCopy();
            return new GdDict();
        }

        /// <summary><c>PackedStringArray</c> of dressing ids, sorted.</summary>
        public List<string> KnownDressingIds()
        {
            var output = new List<string>();
            foreach (var k in DRESSING_PRESETS.Keys) output.Add(V.Str(k));
            ProcgenCompat.SortStrings(output);
            return output;
        }

        /// <summary>
        /// Variant for <paramref name="role"/> at <paramref name="roomIndex"/> under <paramref name="seedValue"/>.
        /// A non-empty biome weights its preferred variants 3:1.
        /// </summary>
        public string Pick(string role, long roomIndex, long seedValue, string biome = "")
        {
            biome = biome ?? "";
            List<string> variantList = VariantsForRoleInternal(role);
            if (variantList.Count == 0) return FallbackForUnknown(role, seedValue);

            var rng = new GodotRandom();
            rng.Seed = SeedFor(role, roomIndex, seedValue, biome);
            if (biome.Length == 0)
            {
                long idx = rng.RandiRange(0, variantList.Count - 1);
                return variantList[(int)idx];
            }
            // Weighted: preferred variants get weight 3, others 1.
            HashSet<string> preferred = BiomePreferredVariants(biome);
            var weights = new List<long>();
            long total = 0;
            foreach (string v in variantList)
            {
                long w = preferred.Contains(v) ? 3 : 1;
                weights.Add(w);
                total += w;
            }
            long roll = rng.RandiRange(1, System.Math.Max(1, total));
            long cumulative = 0;
            for (int i = 0; i < variantList.Count; i++)
            {
                cumulative += weights[i];
                if (roll <= cumulative) return variantList[i];
            }
            return variantList[0];
        }

        /// <summary>Biome -&gt; set of preferred variant strings (E2). Unknown biomes = no bias.</summary>
        HashSet<string> BiomePreferredVariants(string biome)
        {
            string[] list;
            switch (biome)
            {
                case "abyssal_synaptic_sea":
                    list = new[] { "biomatter_crusted", "flooded", "contaminated", "collapsed", "breached" };
                    break;
                case "breach_field":
                    list = new[] { "breached", "collapsed", "burned_out", "unstable", "flooded" };
                    break;
                case "dead_fleet":
                    list = new[] { "burned_out", "empty_hold", "derelict_bunks", "collapsed", "secure" };
                    break;
                default:
                    list = new string[0];
                    break;
            }
            return new HashSet<string>(list);
        }

        /// <summary>Variants registered for <paramref name="role"/>, or empty for unknown roles. No RNG.</summary>
        public List<string> VariantsForRole(string role) => VariantsForRoleInternal(role);

        /// <summary>Variant count for <paramref name="role"/>; 1 for unknown roles (the standard fallback).</summary>
        public long VariantCount(string role)
        {
            List<string> arr = VariantsForRoleInternal(role);
            if (arr.Count == 0) return 1;
            return arr.Count;
        }

        /// <summary>Stable 31-bit hash of a role name over its Unicode code points (<c>unicode_at</c>).</summary>
        public static long RoleHash(string role)
        {
            long h = 0;
            if (string.IsNullOrEmpty(role)) return h;
            int i = 0;
            while (i < role.Length)
            {
                int cp = V.NextCodePoint(role, ref i);
                h = (h * 31 + cp) & 0x7FFFFFFF;
            }
            return h;
        }

        List<string> VariantsForRoleInternal(string role)
        {
            var typed = new List<string>();
            if (role == null || !VARIANTS_BY_ROLE.Has(role)) return typed;
            if (!(VARIANTS_BY_ROLE[role] is GdArray raw)) return typed;
            foreach (var entry in raw) typed.Add(V.Str(entry));
            return typed;
        }

        /// <summary>Combines role + index + seed + biome into one 31-bit seed (0 is remapped to 1).</summary>
        long SeedFor(string role, long roomIndex, long seedValue, string biome)
        {
            long h = seedValue & 0x7FFFFFFF;
            h = (h ^ RoleHash(role)) & 0x7FFFFFFF;
            h = (h ^ unchecked(roomIndex * 2654435761L)) & 0x7FFFFFFF;
            if (biome.Length != 0) h = (h ^ RoleHash(biome)) & 0x7FFFFFFF;
            if (h == 0) h = 1; // Godot's RNG.seed = 0 means default seed; offset by 1.
            return h;
        }

        string FallbackForUnknown(string role, long seedValue)
        {
            if (string.IsNullOrEmpty(role)) return VARIANT_STANDARD;
            return VARIANT_STANDARD + "_" + role;
        }
    }
}
