// Ported from scripts/procgen/room_assigner.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Anything with a <c>pick(role, room_index, seed, biome)</c> method (GDScript duck typing via
    /// <c>has_method("pick")</c> in <see cref="RoomAssigner"/>). <see cref="RoomVariantSelector"/> is accepted
    /// directly without implementing it.
    /// </summary>
    public interface IRoomVariantPicker
    {
        string Pick(string role, long roomIndex, long seedValue, string biome);
    }

    /// <summary>
    /// Fills template zones with concrete rooms. Each zone produces 1..N rooms based on its count field. Roles are
    /// picked from the zone's role_pool using archetype weights when available. Each room gets a footprint based on
    /// <see cref="ROOM_FOOTPRINT_OPTIONS"/> and the blueprint size.
    /// </summary>
    public sealed class RoomAssigner
    {
        static GdArray Fps(params Vec2i[] fps) => new GdArray(fps);

        /// <summary>
        /// Footprint options per role (<c>Vector2i</c> choices). SMALL / LIFE_BOAT blueprints pick from the first
        /// half of the options; larger blueprints from the full range.
        /// </summary>
        public static readonly GdDict ROOM_FOOTPRINT_OPTIONS = new GdDict
        {
            { "airlock", Fps(new Vec2i(2, 2), new Vec2i(3, 2)) },
            { "dock", Fps(new Vec2i(2, 2), new Vec2i(3, 2)) },
            { "corridor", Fps(new Vec2i(3, 1), new Vec2i(4, 1), new Vec2i(2, 1), new Vec2i(5, 1)) },
            { "engineering", Fps(new Vec2i(2, 2), new Vec2i(3, 2), new Vec2i(3, 3)) },
            { "bridge", Fps(new Vec2i(3, 2), new Vec2i(3, 3)) },
            { "cargo", Fps(new Vec2i(2, 2), new Vec2i(3, 3), new Vec2i(2, 3)) },
            { "bay", Fps(new Vec2i(2, 2), new Vec2i(3, 3), new Vec2i(2, 3)) },
            { "hangar", Fps(new Vec2i(2, 2), new Vec2i(3, 3), new Vec2i(2, 3)) },
            { "compartment", Fps(new Vec2i(2, 2)) },
            { "medical", Fps(new Vec2i(2, 2), new Vec2i(2, 1)) },
            { "quarters", Fps(new Vec2i(2, 2)) },
            { "crew_quarters", Fps(new Vec2i(2, 2), new Vec2i(2, 1)) },
            { "mess_hall", Fps(new Vec2i(2, 2), new Vec2i(3, 2)) },
            { "armory", Fps(new Vec2i(1, 2), new Vec2i(2, 2)) },
            { "maintenance", Fps(new Vec2i(1, 2), new Vec2i(2, 2)) },
            { "life_support", Fps(new Vec2i(2, 2)) },
            { "reactor", Fps(new Vec2i(3, 3), new Vec2i(2, 3), new Vec2i(3, 2)) },
            { "main_spine", Fps(new Vec2i(3, 3), new Vec2i(2, 2)) },
            { "hub", Fps(new Vec2i(3, 3), new Vec2i(2, 2)) },
            { "ramp", Fps(new Vec2i(1, 1)) },
            { "elevator", Fps(new Vec2i(1, 1)) },
            { "storage", Fps(new Vec2i(1, 2), new Vec2i(2, 2)) },
            { "tool_storage", Fps(new Vec2i(2, 2)) },
        };

        public static readonly Vec2i DEFAULT_FOOTPRINT = new Vec2i(2, 2);

        /// <summary>
        /// Procgen program F1: alias authored archetype role names onto template role_pool tokens so
        /// guaranteed_roles / role_weights actually match zone pools.
        /// </summary>
        public static readonly GdDict ROLE_ALIASES = new GdDict
        {
            { "compartment", "cargo" },
            { "bay", "cargo" },
            { "quarters", "crew_quarters" },
            { "tool_storage", "storage" },
            { "engine_bay", "engineering" },
            { "cockpit", "bridge" },
        };

        public GodotRandom Rng = new GodotRandom();

        /// <summary>
        /// Set by <see cref="AssignWithSelector"/>: a <see cref="RoomVariantSelector"/>, an
        /// <see cref="IRoomVariantPicker"/>, any other object (no <c>pick</c> method: "standard"), or null.
        /// </summary>
        public object VariantSelector;

        public List<GdDict> Assign(TopologyTemplate template, ShipBlueprint blueprint, GdDict archetype)
        {
            return AssignWithSelector(template, blueprint, archetype, null);
        }

        /// <summary>Normalize a single role token through <see cref="ROLE_ALIASES"/> (identity if unmapped).</summary>
        public static string NormalizeRole(string role)
        {
            string r = role ?? "";
            if (ROLE_ALIASES.Has(r)) return V.Str(ROLE_ALIASES[r]);
            return r;
        }

        /// <summary>Returns a deep-copied, normalized archetype dict (weights + guarantees remapped).</summary>
        public static GdDict NormalizeArchetype(GdDict archetype)
        {
            if (archetype == null || archetype.IsEmpty) return new GdDict();
            GdDict output = archetype.DeepCopy();
            object weightsV = output.Get("role_weights", new GdDict());
            if (weightsV is GdDict weights)
            {
                var nw = new GdDict();
                foreach (var kv in weights)
                {
                    string nk = NormalizeRole(V.Str(kv.Key));
                    nw[nk] = V.I64(nw.Get(nk, 0L)) + V.I64(kv.Value);
                }
                output["role_weights"] = nw;
            }
            object gV = output.Get("guaranteed_roles", new GdArray());
            if (gV is GdArray guaranteed)
            {
                var ng = new GdArray();
                var seen = new HashSet<string>();
                foreach (var entry in guaranteed)
                {
                    string nr = NormalizeRole(V.Str(entry));
                    if (seen.Contains(nr)) continue;
                    seen.Add(nr);
                    ng.Append(nr);
                }
                output["guaranteed_roles"] = ng;
            }
            return output;
        }

        /// <summary>
        /// Same as <see cref="Assign"/> but also accepts a variant selector (see <see cref="VariantSelector"/>).
        /// Every room dict gets a <c>variant</c> key ("standard" without a selector).
        /// </summary>
        public List<GdDict> AssignWithSelector(
            TopologyTemplate template,
            ShipBlueprint blueprint,
            GdDict archetype,
            object selector,
            string biome = "")
        {
            biome = biome ?? "";
            VariantSelector = selector;

            Rng.Seed = blueprint.SeedValue;
            // F1: normalize before any pick/guarantee so weights and pools share a vocabulary.
            archetype = NormalizeArchetype(archetype);

            var roomPlan = new List<GdDict>();
            var roleCounter = new Dictionary<string, long>(); // role -> next index
            var zonePools = new Dictionary<string, List<string>>(); // zone_id -> role pool

            // Process zones in template order: entry first, destination last.
            foreach (var zone in template.Zones)
            {
                string zoneId = V.Str(zone.Get("id", ""));
                object rolePoolRaw = zone.Get("role_pool", new GdArray());
                var rolePool = new List<string>();
                if (rolePoolRaw is GdArray rawPool)
                {
                    var seenPool = new HashSet<string>();
                    foreach (var entry in rawPool)
                    {
                        string nr = NormalizeRole(V.Str(entry));
                        if (seenPool.Contains(nr)) continue;
                        seenPool.Add(nr);
                        rolePool.Add(nr);
                    }
                }
                zonePools[zoneId] = rolePool;

                long count = ResolveCount(zone.Get("count", 1L));
                long deck = V.I64(zone.Get("deck", 0L));
                string positionHint = V.Str(zone.Get("position_hint", "center"));

                for (long i = 0; i < count; i++)
                {
                    string role = PickRole(rolePool, archetype, roleCounter);
                    long idx = NextIndex(role, roleCounter);
                    string roomId = role + "_" + GdString.FormatIntPadded(idx, 2);
                    Vec2i footprint = PickFootprint(role, blueprint);
                    string variant = PickVariant(role, roomPlan.Count, blueprint, biome);

                    roomPlan.Add(new GdDict
                    {
                        { "id", roomId },
                        { "role", role },
                        { "variant", variant },
                        { "zone_id", zoneId },
                        { "deck", deck },
                        { "position_hint", positionHint },
                        { "target_cells", (long)footprint.X * footprint.Y },
                        { "footprint", footprint },
                    });
                }
            }

            // Tranche 5: enforce archetype guaranteed_roles. Deterministic post-pass; only the replacement's
            // footprint re-roll draws from the seeded rng.
            EnforceGuaranteedRoles(roomPlan, archetype, zonePools, blueprint, biome);

            return roomPlan;
        }

        /// <summary>
        /// Ensures every archetype guaranteed_role appears at least once, replacing the most-duplicated
        /// non-guaranteed room whose zone role_pool permits the missing role. Entry (first) and destination (last)
        /// rooms are never replaced; later plan index breaks ties.
        /// </summary>
        void EnforceGuaranteedRoles(List<GdDict> roomPlan, GdDict archetype,
            Dictionary<string, List<string>> zonePools, ShipBlueprint blueprint, string biome)
        {
            object guaranteedRaw = archetype.Get("guaranteed_roles", new GdArray());
            if (!(guaranteedRaw is GdArray guaranteedArr) || guaranteedArr.IsEmpty) return;
            var guaranteed = new List<string>();
            foreach (var entry in guaranteedArr) guaranteed.Add(V.Str(entry));

            bool replacedAny = false;
            foreach (string wanted in guaranteed)
            {
                bool present = false;
                foreach (var room in roomPlan)
                {
                    if (V.Str(room.Get("role", "")) == wanted)
                    {
                        present = true;
                        break;
                    }
                }
                if (present) continue;

                var roleCounts = new Dictionary<string, long>();
                foreach (var room in roomPlan)
                {
                    string r = V.Str(room.Get("role", ""));
                    roleCounts.TryGetValue(r, out long c);
                    roleCounts[r] = c + 1;
                }

                int bestIndex = -1;
                long bestCount = 0;
                for (int i = 1; i < roomPlan.Count - 1; i++) // never the entry or destination
                {
                    GdDict room = roomPlan[i];
                    string role = V.Str(room.Get("role", ""));
                    if (guaranteed.Contains(role)) continue;
                    zonePools.TryGetValue(V.Str(room.Get("zone_id", "")), out List<string> pool);
                    if (pool == null || !pool.Contains(wanted)) continue;
                    roleCounts.TryGetValue(role, out long count);
                    if (count >= bestCount) // >= so later plan index wins ties
                    {
                        bestCount = count;
                        bestIndex = i;
                    }
                }
                if (bestIndex < 0)
                {
                    CoreServices.Log.Warning("RoomAssigner: guaranteed role '" + wanted +
                                             "' has no eligible zone in this template; guarantee skipped");
                    continue;
                }

                GdDict target = roomPlan[bestIndex];
                target["role"] = wanted;
                Vec2i footprint = PickFootprint(wanted, blueprint);
                target["footprint"] = footprint;
                target["target_cells"] = (long)footprint.X * footprint.Y;
                target["variant"] = PickVariant(wanted, bestIndex, blueprint, biome);
                replacedAny = true;
            }

            if (replacedAny)
            {
                // Re-derive ids so role indices stay contiguous and unique in plan order.
                var counter = new Dictionary<string, long>();
                foreach (var room in roomPlan)
                {
                    string role = V.Str(room.Get("role", ""));
                    room["id"] = role + "_" + GdString.FormatIntPadded(NextIndex(role, counter), 2);
                }
            }
        }

        /// <summary>Picks a variant via the selector (if any); "standard" otherwise.</summary>
        string PickVariant(string role, long roomIndex, ShipBlueprint blueprint, string biome)
        {
            if (VariantSelector == null) return "standard";
            if (VariantSelector is RoomVariantSelector selector)
                return selector.Pick(role, roomIndex, blueprint.SeedValue, biome);
            if (VariantSelector is IRoomVariantPicker picker)
                return V.Str(picker.Pick(role, roomIndex, blueprint.SeedValue, biome));
            // GDScript: not variant_selector.has_method("pick") -> "standard".
            return "standard";
        }

        long ResolveCount(object countValue)
        {
            if (countValue is GdArray arr)
            {
                if (arr.Count >= 2)
                {
                    long lo = V.I64(arr[0]);
                    long hi = V.I64(arr[1]);
                    if (hi < lo) hi = lo;
                    return Rng.RandiRange(lo, hi);
                }
            }
            // GDScript int(<Array>) is an invalid conversion; the kernel coercion yields 0.
            return V.I64(countValue);
        }

        string PickRole(List<string> pool, GdDict archetype, Dictionary<string, long> roleCounter)
        {
            if (pool.Count == 0) return "corridor";
            // Single-role zones are authored intent; max_duplicates applies only where alternatives exist.
            if (pool.Count == 1) return pool[0];

            // Tranche 5: roles already at max_duplicates are excluded; if EVERY pool role is capped, fall back to
            // the least-used pool role.
            long maxDup = V.I64(archetype.Get("max_duplicates", 0L)); // 0 = unlimited

            GdDict weights = archetype.GetDictOrEmpty("role_weights");
            var candidates = new List<string>();
            var candidateWeights = new List<long>();
            long total = 0;

            foreach (string role in pool)
            {
                if (maxDup > 0 && CounterOf(roleCounter, role) >= maxDup) continue;
                long w = V.I64(weights.Get(role, 1L));
                if (w <= 0) w = 1;
                candidates.Add(role);
                candidateWeights.Add(w);
                total += w;
            }

            if (candidates.Count == 0)
            {
                string leastUsed = pool[0];
                long leastCount = CounterOf(roleCounter, pool[0]);
                foreach (string role in pool)
                {
                    long used = CounterOf(roleCounter, role);
                    if (used < leastCount)
                    {
                        leastCount = used;
                        leastUsed = role;
                    }
                }
                return leastUsed;
            }

            long roll = Rng.RandiRange(1, total);
            long cumulative = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                cumulative += candidateWeights[i];
                if (roll <= cumulative) return candidates[i];
            }

            return candidates[0];
        }

        static long CounterOf(Dictionary<string, long> counter, string role) =>
            counter.TryGetValue(role, out long v) ? v : 0;

        static long NextIndex(string role, Dictionary<string, long> roleCounter)
        {
            if (!roleCounter.ContainsKey(role)) roleCounter[role] = 1;
            else roleCounter[role] = roleCounter[role] + 1;
            return roleCounter[role];
        }

        Vec2i PickFootprint(string role, ShipBlueprint blueprint)
        {
            if (!ROOM_FOOTPRINT_OPTIONS.Has(role)) return DEFAULT_FOOTPRINT;

            var options = (GdArray)ROOM_FOOTPRINT_OPTIONS[role];
            if (options.IsEmpty) return DEFAULT_FOOTPRINT;

            // SMALL / LIFE_BOAT prefer smaller footprints (first half of options); MEDIUM picks the full range.
            long maxIdx = options.Count - 1;
            if (blueprint.ShipSize <= 1 && options.Count > 1)
                maxIdx = (long)GdMath.Ceil((double)options.Count / 2.0) - 1;

            long idx = Rng.RandiRange(0, maxIdx);
            return (Vec2i)options[(int)idx];
        }
    }
}
