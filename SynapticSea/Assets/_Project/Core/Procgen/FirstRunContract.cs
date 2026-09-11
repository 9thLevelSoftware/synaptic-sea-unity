// Ported from scripts/procgen/first_run_contract.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Data contract for the cold-player's first away derelict beat. Pure at validation time: callers provide the
    /// generated layout and gameplay slice dictionaries, while seed selection remains deterministic.
    /// </summary>
    public sealed class FirstRunContract
    {
        public const string CONTRACT_PATH = "res://data/procgen/slice/first_run_contract.json";

        static readonly string[] HAZARD_ROLES =
        {
            "bridge", "cockpit", "engineering", "reactor", "engine_bay",
            "hydroponics", "cargo", "storage",
        };

        public GdDict Contract = new GdDict();

        public bool LoadContract(string path = CONTRACT_PATH)
        {
            Contract = new GdDict();
            if (!CatalogRegistry.Exists(path)) return false;
            GdDict data = CatalogRegistry.LoadDict(path);
            if (data == null) return false;
            if (V.Str(data.Get("version", "")) != "first-run-contract-1") return false;
            if (V.Str(data.Get("biome_id", "")).Length == 0 || V.Str(data.Get("difficulty_id", "")).Length == 0) return false;
            if (!(data.Get("preferred_seeds", new GdArray()) is GdArray preferred) || preferred.IsEmpty) return false;
            Contract = data.DeepCopy();
            return true;
        }

        public bool Validate(GdDict layout, GdDict gameplaySlice = null)
        {
            gameplaySlice = gameplaySlice ?? new GdDict();
            if (Contract.IsEmpty && !LoadContract()) return false;
            if (layout == null || layout.IsEmpty || gameplaySlice.IsEmpty) return false;
            if (!layout.Has("biome_id") || V.Str(layout.Get("biome_id", "")) != V.Str(Contract.Get("biome_id", ""))) return false;
            if (!layout.Has("difficulty_id") || V.Str(layout.Get("difficulty_id", "")) != V.Str(Contract.Get("difficulty_id", ""))) return false;
            object loot = gameplaySlice.Get("loot_containers", new GdArray());
            if (!(loot is GdArray lootArr) || lootArr.Count < V.I64(Contract.Get("require_min_loot_containers", 0L))) return false;
            object encounters = layout.Get("encounters", new GdArray());
            if (!(encounters is GdArray encArr) || encArr.Count < V.I64(Contract.Get("require_min_encounters", 0L))) return false;
            object requiredHazards = Contract.Get("require_any", new GdArray());
            if (!(requiredHazards is GdArray required) || !HasAnyRequiredHazard(layout, gameplaySlice, required)) return false;
            return true;
        }

        /// <summary>
        /// Returns the first preferred seed whose generated payload the contract accepts. <paramref name="candidates"/>
        /// is a <see cref="GdDict"/> mapping seed -&gt; <c>{"layout", "gameplay_slice"}</c>, or a
        /// <c>Func&lt;long, object&gt;</c> provider (the GDScript Callable) for lazy generation; invalid candidates are
        /// skipped. Falls back to the first preferred seed (no new RNG).
        /// </summary>
        public long PickSeed(object candidates = null)
        {
            if (Contract.IsEmpty && !LoadContract()) return 0;
            GdArray preferred = Contract.Get("preferred_seeds", new GdArray()) as GdArray;
            if (preferred == null || preferred.IsEmpty) return 0;
            foreach (var seedVariant in preferred)
            {
                long seedValue = V.I64(seedVariant);
                object candidate = null;
                if (candidates is Func<long, object> provider)
                {
                    candidate = provider(seedValue);
                }
                else if (candidates is GdDict candidateMap)
                {
                    candidate = candidateMap.Get(seedValue, new GdDict());
                }
                if (!(candidate is GdDict candidateDict)) continue;
                if (Validate(candidateDict.GetDictOrEmpty("layout"), candidateDict.GetDictOrEmpty("gameplay_slice")))
                    return seedValue;
            }
            return V.I64(preferred[0]);
        }

        bool HasAnyRequiredHazard(GdDict layout, GdDict gameplaySlice, GdArray required)
        {
            var available = new GdDict();
            CollectHazardArray(layout.Get("fire_zones", new GdArray()), "fire_zone", available);
            CollectHazardArray(layout.Get("breach_zones", new GdArray()), "breach_zone", available);
            CollectHazardArray(gameplaySlice.Get("fire_zones", new GdArray()), "fire_zone", available);
            CollectHazardArray(gameplaySlice.Get("breach_zones", new GdArray()), "breach_zone", available);
            // The live derelict path seeds hazards from room variants after loading; count those same authoritative
            // variant effects so seed validation exercises the production generation contract.
            object rooms = layout.Get("rooms", new GdArray());
            if (rooms is GdArray roomsArr)
            {
                var selector = new RoomVariantSelector();
                foreach (var roomVariant in roomsArr)
                {
                    if (!(roomVariant is GdDict room)) continue;
                    string role = V.Str(room.Get("room_role", room.Get("role", "")));
                    if (Array.IndexOf(HAZARD_ROLES, role) < 0) continue;
                    string variant = V.Str(room.Get("variant", "standard"));
                    object hazard = selector.EffectsFor(variant).GetDictOrEmpty("sim").Get("hazard", new GdDict());
                    if (hazard is GdDict hazardDict)
                    {
                        string kind = V.Str(hazardDict.Get("kind", ""));
                        if (kind == "fire") available["fire_zone"] = true;
                        else if (kind == "breach") available["breach_zone"] = true;
                    }
                }
            }
            foreach (var requiredVariant in required)
                if (available.Has(V.Str(requiredVariant))) return true;
            return false;
        }

        static void CollectHazardArray(object value, string key, GdDict available)
        {
            if (value is GdArray arr && !arr.IsEmpty) available[key] = true;
        }
    }
}
