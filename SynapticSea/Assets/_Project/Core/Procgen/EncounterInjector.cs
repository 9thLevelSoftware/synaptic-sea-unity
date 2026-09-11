// Ported from scripts/procgen/encounter_injector.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Pure deterministic encounter spawn marker generator. Walks every non-critical-path room in a layout and rolls
    /// a per-room encounter marker against the biome's encounter table, scaled by the combined biome x difficulty
    /// density multiplier (PKG-C5.3 tension budget: quiet entry, escalation toward the objective, branch depth
    /// risk/reward, one authored spike slot). Emits <c>layout.encounters</c> and <c>layout.encounter_pacing</c>.
    /// Critical-path rooms are never spawn camps (REQ-PG-007 + RISK-011).
    /// <para>
    /// Marker keys: id, room_id, deck, cell [x, y], local_position [x, y, z], encounter_kind, count,
    /// difficulty_tier, encounter_table_id, seed_offset, tension_progress, branch_depth, patrol_crosses_critical,
    /// spike.
    /// </para>
    /// </summary>
    public sealed class EncounterInjector
    {
        public const string DIAL_HAZARD = "hazard_modifier";
        public const string DIAL_ENCOUNTER = "encounter_density_modifier";

        /// <summary>Base encounter probability per room role.</summary>
        public static readonly GdDict ENCOUNTER_BASE_PROBABILITY = new GdDict
        {
            { "airlock", 0.10 },
            { "corridor", 0.20 },
            { "bridge", 0.15 },
            { "cargo", 0.25 },
            { "medical", 0.20 },
            { "crew_quarters", 0.20 },
            { "engineering", 0.30 },
            { "maintenance", 0.35 },
            { "reactor", 0.40 },
            { "ramp", 0.05 },
            { "elevator", 0.05 },
            { "hub", 0.10 },
            { "dock", 0.10 },
            { "compartment", 0.30 },
            { "bay", 0.25 },
            { "quarters", 0.20 },
            { "hangar", 0.20 },
            { "main_spine", 0.15 },
            { "mess_hall", 0.15 },
            { "armory", 0.25 },
            { "storage", 0.20 },
            { "tool_storage", 0.20 },
            { "cockpit", 0.10 },
            { "engine_bay", 0.30 },
        };

        public const double DEFAULT_BASE_PROBABILITY = 0.20;

        /// <summary>Room role -&gt; encounter_kind id (the procgen/combat contract).</summary>
        public static readonly GdDict ROLE_TO_ENCOUNTER_KIND = new GdDict
        {
            { "airlock", "breach_lurker" },
            { "corridor", "biomatter_lurker" },
            { "bridge", "drone_scout" },
            { "cargo", "biomatter_lurker" },
            { "medical", "biomatter_lurker" },
            { "crew_quarters", "biomatter_lurker" },
            { "engineering", "drone_swarm" },
            { "maintenance", "biomatter_lurker" },
            { "reactor", "drone_swarm" },
            { "ramp", "" },
            { "elevator", "" },
            { "hub", "drone_scout" },
            { "dock", "drone_scout" },
            { "compartment", "biomatter_lurker" },
            { "bay", "drone_swarm" },
            { "quarters", "biomatter_lurker" },
            { "hangar", "drone_swarm" },
            { "main_spine", "biomatter_lurker" },
            { "mess_hall", "biomatter_lurker" },
            { "armory", "drone_swarm" },
            { "storage", "biomatter_lurker" },
            { "tool_storage", "biomatter_lurker" },
            { "cockpit", "drone_scout" },
            { "engine_bay", "drone_swarm" },
        };

        public const string DEFAULT_ENCOUNTER_KIND = "biomatter_lurker";

        public const string ENCOUNTER_TABLE_DIR = "res://data/procgen/encounter_tables/";

        // table_id -> Dictionary ({} = missing/malformed, warned once). Static so the warn-once contract holds
        // across injector instances; tables are read-only data, so caching cannot affect per-seed determinism.
        static readonly Dictionary<string, GdDict> _tableCache = new Dictionary<string, GdDict>(StringComparer.Ordinal);

        /// <summary>Clears the process-wide encounter table cache (tests that swap the resource reader).</summary>
        public static void ClearTableCache() => _tableCache.Clear();

        /// <summary>
        /// Injects encounter spawn markers into <paramref name="layout"/> in place and returns the same dictionary.
        /// <paramref name="biome"/> / <paramref name="difficulty"/> may be null (GDScript duck-typed profiles:
        /// normally <see cref="BiomeProfile"/> / <see cref="DifficultyProfile"/>).
        /// </summary>
        public GdDict Inject(GdDict layout, IModifierSource biome, IModifierSource difficulty, long seedValue)
        {
            // Critical-path room id set. Prefer the cached field; fall back to TemplateCTraversal.critical_path().
            var criticalSet = new GdDict();
            object cachedCritical = layout.Get("critical_path", null);
            if (cachedCritical is GdArray cachedArr)
            {
                foreach (var rid in cachedArr) criticalSet[V.Str(rid)] = true;
            }
            else
            {
                List<string> path = TemplateCTraversal.CriticalPath(layout);
                foreach (string rid in path) criticalSet[rid] = true;
            }

            // Combined density multiplier for the encounter dial: floored at 0 but NOT capped at 1.0 (density > 1
            // must raise the spawn rate; the per-room probability is clamped below).
            double density = Math.Max(SafeCombined(biome, difficulty, DIAL_ENCOUNTER), 0.0);

            string difficultyId = SafeDifficultyId(difficulty);
            string encounterTableId = SafeEncounterTableId(biome);
            double cellSize = V.F64(layout.Get("cell_size", 4.0));

            object roomsRaw = layout.Get("rooms", new GdArray());
            var rooms = new GdArray();
            if (roomsRaw is GdArray roomsArr) rooms = roomsArr;
            if (rooms.IsEmpty)
            {
                layout["encounters"] = new GdArray();
                return layout;
            }

            var markers = new GdArray();
            var rng = new GodotRandom();
            rng.Seed = (seedValue ^ 0xDEADBEEFL) & 0x7FFFFFFF;
            if (rng.Seed == 0) rng.Seed = 1;

            // PKG-C5.3: graph metrics for tension pacing.
            GdDict adjacency = BuildRoomAdjacency(layout, rooms);
            string startId = ResolveStartRoomId(layout, rooms, criticalSet);
            string goalId = ResolveGoalRoomId(layout, rooms, criticalSet);
            GdDict distFromStart = BfsDistances(adjacency, startId);
            GdDict branchDepth = BranchDepths(adjacency, criticalSet);
            double maxProgress = 1.0;
            foreach (var ridKey in distFromStart.Keys)
                maxProgress = Math.Max(maxProgress, V.F64(distFromStart[ridKey]));

            long markerIndex = 0;
            string spikedRoom = "";
            var candidates = new GdArray(); // non-critical rooms eligible for rolls
            foreach (var roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room)) continue;
                string rid = V.Str(room.Get("id", ""));
                if (rid.Length == 0 || criticalSet.Has(rid)) continue;
                candidates.Append(room);
            }

            // Prefer a high-progress branch room for the authored spike slot.
            string spikeTarget = PickSpikeRoom(candidates, distFromStart, goalId, maxProgress);

            foreach (var candidate in candidates)
            {
                var room = (GdDict)candidate;
                string rid = V.Str(room.Get("id", ""));
                string role = V.Str(room.Get("room_role", room.Get("role", "")));
                double pBase = V.F64(ENCOUNTER_BASE_PROBABILITY.Get(role, DEFAULT_BASE_PROBABILITY));
                double progress = V.F64(distFromStart.Get(rid, 0L));
                double progressRatio = GdMath.Clampf(progress / maxProgress, 0.0, 1.0);
                // Entry quiet, escalate toward objective.
                double tension = GdMath.Lerpf(0.35, 1.45, progressRatio);
                if (progressRatio <= 0.15) tension *= 0.25; // guaranteed quieter near entry
                long depth = V.I64(branchDepth.Get(rid, 0L));
                double depthMult = 1.0 + 0.22 * (double)Math.Min(depth, 4L);
                double pFinal = GdMath.Clampf(pBase * density * tension * depthMult, 0.0, 1.0);
                bool forceSpike = rid == spikeTarget && density >= 1.0 && pFinal > 0.05;
                if (pFinal <= 0.0 && !forceSpike) continue;
                double roll = rng.Randf();
                if (!forceSpike && roll >= pFinal) continue;
                if (forceSpike) spikedRoom = rid;

                string encounterKind;
                long markerCount = 1;
                GdArray tableRolls = TableRollsForRole(encounterTableId, role);
                if (!tableRolls.IsEmpty)
                {
                    GdDict pick = PickTableRoll(tableRolls, rng);
                    encounterKind = V.Str(pick.Get("encounter_kind", ""));
                    markerCount = ResolveRollCount(pick.Get("count", 1L), rng);
                }
                else
                {
                    encounterKind = V.Str(ROLE_TO_ENCOUNTER_KIND.Get(role, DEFAULT_ENCOUNTER_KIND));
                }
                if (encounterKind.Length == 0) continue;

                GdArray entries = FloorCellEntries(room, cellSize);
                object cellEntry = GdArray.Of(0L, 0L);
                object localPosition = GdArray.Of(0.0, 0.0, 0.0);
                if (!entries.IsEmpty)
                {
                    var cellPick = (GdDict)entries[entries.Count >> 1];
                    cellEntry = cellPick["cell"];
                    localPosition = cellPick["local_position"];
                }

                markerIndex += 1;
                bool patrolCross = AdjacentToCritical(rid, adjacency, criticalSet);
                var marker = new GdDict
                {
                    { "id", "enc_" + rid + "_" + GdString.FormatInt(markerIndex) },
                    { "room_id", rid },
                    { "deck", V.I64(room.Get("deck", 0L)) },
                    { "cell", cellEntry },
                    { "local_position", localPosition },
                    { "encounter_kind", encounterKind },
                    { "count", markerCount },
                    { "difficulty_tier", difficultyId },
                    { "encounter_table_id", encounterTableId },
                    { "seed_offset", markerIndex },
                    { "tension_progress", progressRatio },
                    { "branch_depth", depth },
                    { "patrol_crosses_critical", patrolCross },
                    { "spike", forceSpike },
                };
                markers.Append(marker);
            }

            layout["encounters"] = markers;
            layout["encounter_pacing"] = new GdDict
            {
                { "model", "tension_budget_v1" },
                { "spike_room", spikedRoom },
                { "max_progress", maxProgress },
                { "density", density },
            };
            return layout;
        }

        static GdDict BuildRoomAdjacency(GdDict layout, GdArray rooms)
        {
            var adj = new GdDict();
            foreach (var roomVariant in rooms)
            {
                if (roomVariant is GdDict room)
                {
                    string rid = V.Str(room.Get("id", ""));
                    if (rid.Length != 0) adj[rid] = new GdArray();
                }
            }
            object links = layout.Get("room_links", layout.Get("adjacencies", new GdArray()));
            if (links is GdArray linksArr)
            {
                foreach (var linkVariant in linksArr)
                {
                    if (!(linkVariant is GdDict link)) continue;
                    string a = V.Str(link.Get("from_room", ""));
                    string b = V.Str(link.Get("to_room", ""));
                    if (a.Length == 0 || b.Length == 0) continue;
                    if (!adj.Has(a)) adj[a] = new GdArray();
                    if (!adj.Has(b)) adj[b] = new GdArray();
                    ((GdArray)adj[a]).Append(b);
                    ((GdArray)adj[b]).Append(a);
                }
            }
            return adj;
        }

        static GdDict BfsDistances(GdDict adjacency, string startId)
        {
            var dist = new GdDict();
            if (startId.Length == 0 || !adjacency.Has(startId))
            {
                foreach (var rid in adjacency.Keys) dist[V.Str(rid)] = 0L;
                return dist;
            }
            var q = new List<string> { startId };
            dist[startId] = 0L;
            int head = 0;
            while (head < q.Count)
            {
                string cur = q[head];
                head += 1;
                var neighbors = adjacency.Get(cur, null) as GdArray ?? new GdArray();
                foreach (var n in neighbors)
                {
                    string nid = V.Str(n);
                    if (dist.Has(nid)) continue;
                    dist[nid] = V.I64(dist[cur]) + 1;
                    q.Add(nid);
                }
            }
            foreach (var rid in adjacency.Keys)
                if (!dist.Has(V.Str(rid))) dist[V.Str(rid)] = 0L;
            return dist;
        }

        /// <summary>Distance to the nearest critical-path room (0 if on the critical path).</summary>
        static GdDict BranchDepths(GdDict adjacency, GdDict criticalSet)
        {
            var dist = new GdDict();
            var q = new List<string>();
            foreach (var rid in criticalSet.Keys)
            {
                string sid = V.Str(rid);
                dist[sid] = 0L;
                q.Add(sid);
            }
            int head = 0;
            while (head < q.Count)
            {
                string cur = q[head];
                head += 1;
                var neighbors = adjacency.Get(cur, null) as GdArray ?? new GdArray();
                foreach (var n in neighbors)
                {
                    string nid = V.Str(n);
                    if (dist.Has(nid)) continue;
                    dist[nid] = V.I64(dist[cur]) + 1;
                    q.Add(nid);
                }
            }
            foreach (var rid in adjacency.Keys)
                if (!dist.Has(V.Str(rid))) dist[V.Str(rid)] = 0L;
            return dist;
        }

        static string ResolveStartRoomId(GdDict layout, GdArray rooms, GdDict criticalSet)
        {
            object proto = layout.Get("prototype", new GdDict());
            if (proto is GdDict protoDict && V.Str(protoDict.Get("start_room", "")).Length != 0)
                return V.Str(protoDict.Get("start_room", ""));
            object cp = layout.Get("critical_path", new GdArray());
            if (cp is GdArray cpArr && cpArr.Count > 0) return V.Str(cpArr[0]);
            if (rooms.Count > 0 && rooms[0] is GdDict first) return V.Str(first.Get("id", ""));
            return "";
        }

        static string ResolveGoalRoomId(GdDict layout, GdArray rooms, GdDict criticalSet)
        {
            object proto = layout.Get("prototype", new GdDict());
            if (proto is GdDict protoDict && V.Str(protoDict.Get("goal_room", "")).Length != 0)
                return V.Str(protoDict.Get("goal_room", ""));
            object cp = layout.Get("critical_path", new GdArray());
            if (cp is GdArray cpArr && cpArr.Count > 0) return V.Str(cpArr[cpArr.Count - 1]);
            if (rooms.Count > 0 && rooms[rooms.Count - 1] is GdDict last) return V.Str(last.Get("id", ""));
            return "";
        }

        static string PickSpikeRoom(GdArray candidates, GdDict distFromStart, string goalId, double maxProgress)
        {
            string bestId = "";
            double bestScore = -1.0;
            foreach (var roomVariant in candidates)
            {
                if (!(roomVariant is GdDict room)) continue;
                string rid = V.Str(room.Get("id", ""));
                double progress = V.F64(distFromStart.Get(rid, 0L));
                double score = progress;
                if (rid == goalId) score += 2.0;
                // Prefer late-ship rooms.
                if (progress < maxProgress * 0.55) continue;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = rid;
                }
            }
            return bestId;
        }

        static bool AdjacentToCritical(string rid, GdDict adjacency, GdDict criticalSet)
        {
            var neighbors = adjacency.Get(rid, null) as GdArray ?? new GdArray();
            foreach (var n in neighbors)
                if (criticalSet.Has(V.Str(n))) return true;
            return false;
        }

        /// <summary>
        /// Validates the encounter markers embedded in <paramref name="layout"/>. Returns
        /// <c>{valid, marker_count, missing_room, critical_path_violation, missing_cell, duplicate_id, bad_kind}</c>,
        /// stopping at the first violation.
        /// </summary>
        public static GdDict Validate(GdDict layout)
        {
            var result = new GdDict
            {
                { "valid", true },
                { "marker_count", 0L },
                { "missing_room", "" },
                { "critical_path_violation", "" },
                { "missing_cell", "" },
                { "duplicate_id", "" },
                { "bad_kind", "" },
            };

            object roomsRaw = layout.Get("rooms", new GdArray());
            if (!(roomsRaw is GdArray roomsArr))
            {
                result["valid"] = false;
                result["missing_room"] = "<no rooms array>";
                return result;
            }

            double validateCellSize = V.F64(layout.Get("cell_size", 4.0));
            var roomLookup = new GdDict();
            foreach (var roomVariant in roomsArr)
            {
                if (!(roomVariant is GdDict room)) continue;
                string rid = V.Str(room.Get("id", ""));
                if (rid.Length == 0) continue;
                var cells = new GdDict();
                foreach (var entry in FloorCellEntries(room, validateCellSize))
                {
                    var xz = (GdArray)((GdDict)entry)["cell"];
                    cells[GdString.FormatInt(V.I64(xz[0])) + "," + GdString.FormatInt(V.I64(xz[1]))] = true;
                }
                roomLookup[rid] = new GdDict
                {
                    { "deck", V.I64(room.Get("deck", 0L)) },
                    { "cells", cells },
                };
            }

            var criticalSet = new GdDict();
            object cp = layout.Get("critical_path", null);
            if (cp is GdArray cpArr)
            {
                foreach (var r in cpArr) criticalSet[V.Str(r)] = true;
            }

            object markersRaw = layout.Get("encounters", null);
            // No encounters field is valid (older 1.1.0 layouts or zero density).
            if (markersRaw == null) return result;
            if (!(markersRaw is GdArray markersArr))
            {
                result["valid"] = false;
                result["bad_kind"] = "encounters_not_array";
                return result;
            }

            var seenIds = new GdDict();
            foreach (var markerVariant in markersArr)
            {
                if (!(markerVariant is GdDict marker))
                {
                    result["valid"] = false;
                    result["bad_kind"] = "marker_not_dict";
                    return result;
                }
                result["marker_count"] = V.I64(result["marker_count"]) + 1;
                string mid = V.Str(marker.Get("id", ""));
                if (mid.Length == 0 || seenIds.Has(mid))
                {
                    result["valid"] = false;
                    result["duplicate_id"] = mid;
                    return result;
                }
                seenIds[mid] = true;

                string rid = V.Str(marker.Get("room_id", ""));
                if (!roomLookup.Has(rid))
                {
                    result["valid"] = false;
                    result["missing_room"] = rid;
                    return result;
                }
                if (criticalSet.Has(rid))
                {
                    result["valid"] = false;
                    result["critical_path_violation"] = rid;
                    return result;
                }

                var roomData = (GdDict)roomLookup[rid];
                long markerDeck = V.I64(marker.Get("deck", -1L));
                if (markerDeck >= 0 && markerDeck != V.I64(roomData.Get("deck", 0L)))
                {
                    result["valid"] = false;
                    result["missing_room"] = rid + " (deck mismatch)";
                    return result;
                }

                object cellRaw = marker.Get("cell", null);
                if (cellRaw is GdArray cellArr && cellArr.Count >= 2)
                {
                    string cellKey = GdString.FormatInt(V.I64(cellArr[0])) + "," + GdString.FormatInt(V.I64(cellArr[1]));
                    GdDict cells = roomData.GetDictOrEmpty("cells");
                    if (!cells.IsEmpty && !cells.Has(cellKey))
                    {
                        result["valid"] = false;
                        result["missing_cell"] = rid + "/" + cellKey;
                        return result;
                    }
                }

                string kind = V.Str(marker.Get("encounter_kind", ""));
                if (kind.Length == 0)
                {
                    result["valid"] = false;
                    result["bad_kind"] = rid + " (empty kind)";
                    return result;
                }

                long count = V.I64(marker.Get("count", 0L));
                if (count < 1)
                {
                    result["valid"] = false;
                    result["bad_kind"] = rid + " (count<1)";
                    return result;
                }
            }

            return result;
        }

        // --- Internal helpers ---

        /// <summary>
        /// Enumerates a room's floor cells as <c>[{cell: [x, z], local_position: [x, y, z]}]</c>. Prefers an explicit
        /// <c>cells</c> array; otherwise derives from the serialized <c>structural_placements</c> "floor_cell_*"
        /// entries (world_position = cell * cell_size).
        /// </summary>
        public static GdArray FloorCellEntries(GdDict room, double cellSize)
        {
            // Guard degenerate cell_size once for BOTH branches (grid contract is CELL_SIZE = 4.0).
            double safeSize = cellSize > 0.0 ? cellSize : 4.0;
            var output = new GdArray();
            object cellsRaw = room.Get("cells", new GdArray());
            if (cellsRaw is GdArray cellsArr)
            {
                foreach (var c in cellsArr)
                {
                    GdArray xz = null;
                    if (c is Vec2i v) xz = GdArray.Of((long)v.X, (long)v.Y);
                    else if (c is GdArray ca && ca.Count >= 2) xz = GdArray.Of(V.I64(ca[0]), V.I64(ca[1]));
                    if (xz != null)
                    {
                        output.Append(new GdDict
                        {
                            { "cell", xz },
                            {
                                "local_position",
                                GdArray.Of(V.F64(xz[0]) * safeSize, 0.0, V.F64(xz[1]) * safeSize)
                            },
                        });
                    }
                }
            }
            if (!output.IsEmpty) return output;
            object placementsRaw = room.Get("structural_placements", new GdArray());
            if (placementsRaw is GdArray placementsArr)
            {
                foreach (var pVariant in placementsArr)
                {
                    if (!(pVariant is GdDict p)) continue;
                    if (!GdString.BeginsWith(V.Str(p.Get("name", "")), "floor_cell")) continue;
                    object wp = p.Get("world_position", null);
                    if (wp is GdArray wpArr && wpArr.Count >= 3)
                    {
                        output.Append(new GdDict
                        {
                            {
                                "cell",
                                GdArray.Of(
                                    GdMath.Trunc(GdMath.Round(V.F64(wpArr[0]) / safeSize)),
                                    GdMath.Trunc(GdMath.Round(V.F64(wpArr[2]) / safeSize)))
                            },
                            { "local_position", GdArray.Of(V.F64(wpArr[0]), V.F64(wpArr[1]), V.F64(wpArr[2])) },
                        });
                    }
                }
            }
            return output;
        }

        /// <summary>
        /// Loads (and caches) the encounter table for <paramref name="tableId"/>, returning the rolls whose
        /// <c>role</c> matches. Missing/malformed tables warn once and resolve to {} (role-constant fallback).
        /// </summary>
        GdArray TableRollsForRole(string tableId, string role)
        {
            if (tableId.Length == 0 || role.Length == 0) return new GdArray();
            GdDict table = LoadEncounterTable(tableId);
            object rollsRaw = table.Get("rolls", new GdArray());
            if (!(rollsRaw is GdArray rollsArr)) return new GdArray();
            var matched = new GdArray();
            foreach (var rollVariant in rollsArr)
            {
                if (!(rollVariant is GdDict rollDict)) continue;
                if (V.Str(rollDict.Get("role", "")) == role) matched.Append(rollVariant);
            }
            return matched;
        }

        GdDict LoadEncounterTable(string tableId)
        {
            if (_tableCache.TryGetValue(tableId, out GdDict cached)) return cached;
            string path = ENCOUNTER_TABLE_DIR + tableId + ".json";
            var result = new GdDict();
            if (!CatalogRegistry.Exists(path))
            {
                CoreServices.Log.Warning("EncounterInjector: encounter table file missing, falling back to role constants: " + path);
            }
            else
            {
                GdDict parsed = CatalogRegistry.LoadDict(path);
                if (parsed != null) result = parsed;
                else
                    CoreServices.Log.Warning("EncounterInjector: encounter table is not a JSON object, falling back to role constants: " + path);
            }
            _tableCache[tableId] = result;
            return result;
        }

        /// <summary>
        /// Deterministic weighted pick among a role's table rolls. A single roll is returned without consuming an rng
        /// draw; non-positive weights count as 1.
        /// </summary>
        static GdDict PickTableRoll(GdArray rolls, GodotRandom rng)
        {
            if (rolls.Count == 1) return (GdDict)rolls[0];
            long total = 0;
            var weights = new List<long>();
            foreach (var roll in rolls)
            {
                long w = V.I64(((GdDict)roll).Get("weight", 1L));
                if (w <= 0) w = 1;
                weights.Add(w);
                total += w;
            }
            long drawn = rng.RandiRange(1, total);
            long cumulative = 0;
            for (int i = 0; i < rolls.Count; i++)
            {
                cumulative += weights[i];
                if (drawn <= cumulative) return (GdDict)rolls[i];
            }
            return (GdDict)rolls[0];
        }

        /// <summary>
        /// Authored count is an int or an inclusive [min, max] range (one rng draw); floored at 1. A single-element
        /// [n] is an explicit count (warned).
        /// </summary>
        static long ResolveRollCount(object countValue, GodotRandom rng)
        {
            if (countValue is GdArray arr)
            {
                if (arr.Count >= 2)
                {
                    long lo = V.I64(arr[0]);
                    long hi = V.I64(arr[1]);
                    if (hi < lo) hi = lo;
                    return Math.Max(1L, rng.RandiRange(lo, hi));
                }
                if (arr.Count == 1)
                {
                    CoreServices.Log.Warning("EncounterInjector: roll count " + V.Str(countValue) +
                                             " is a single-element array; treating as int");
                    return Math.Max(1L, V.I64(arr[0]));
                }
                return 1;
            }
            return Math.Max(1L, V.I64(countValue));
        }

        static double SafeCombined(IModifierSource biome, IModifierSource difficulty, string dial)
        {
            if (biome != null && difficulty != null) return DifficultyProfile.CombinedModifier(biome, difficulty, dial);
            if (biome != null) return SafeModifier(biome, dial);
            if (difficulty != null) return SafeModifier(difficulty, dial);
            return 1.0;
        }

        static double SafeModifier(IModifierSource profile, string dial)
        {
            if (profile == null) return 1.0;
            return profile.Modifier(dial);
        }

        /// <summary>GDScript <c>"id" in difficulty</c>: any profile with an <c>id</c> property.</summary>
        static string SafeDifficultyId(IModifierSource difficulty)
        {
            switch (difficulty)
            {
                case null: return "standard";
                case DifficultyProfile d: return d.Id;
                case BiomeProfile b: return b.Id;
                default: return "standard";
            }
        }

        /// <summary>GDScript <c>"encounter_table_id" in biome</c>.</summary>
        static string SafeEncounterTableId(IModifierSource biome)
        {
            if (biome is BiomeProfile b) return b.EncounterTableId;
            return "";
        }
    }
}
