// First-away candidate gate for Milestone A (REQ-SLICE-001).
// Evaluates preferred seeds in authored order through the production ShipGenerator route, then the
// complete first-run contract including standing start→goal navigation. Does not mutate markers.
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Pure first-away seed picker. Callers supply the same <see cref="ShipGenerator.GenerateFromSeed"/> used
    /// for boarding; this gate never flag-flips <c>away_from_start</c>.
    /// </summary>
    public static class FirstRunAwayGate
    {
        public const string UnsatisfiedReason = "first_run_contract_unsatisfied";

        /// <summary>Derived re-roll seeds tried per preferred seed after the patch fails to satisfy the contract.</summary>
        public const int MaxRerolls = 3;

        static readonly string[] BreachRoles = { "cargo", "engineering" };

        public sealed class Result
        {
            public bool Success;
            public long Seed;
            public string Reason = UnsatisfiedReason;
            public readonly List<long> EvaluatedSeeds = new List<long>();
        }

        /// <summary>
        /// Walks contract <c>preferred_seeds</c> in authored order. Returns the first seed whose generated
        /// payload satisfies the complete first-run contract. When none pass, <see cref="Result.Success"/> is
        /// false and no seed is chosen (fail closed — no fallback to preferred[0]).
        /// </summary>
        public static Result EvaluateCandidates(
            FirstRunContract contract,
            long sizeClass,
            long condition,
            Func<long, long, long, ShipDocuments> generateFromSeed)
        {
            var result = new Result();
            if (contract == null || contract.Contract.IsEmpty || generateFromSeed == null)
            {
                result.Reason = ReadableUnsatisfied("first-run contract is missing");
                return result;
            }
            GdArray preferred = contract.Contract.GetArrayOrEmpty("preferred_seeds");
            if (preferred.IsEmpty)
            {
                result.Reason = ReadableUnsatisfied("preferred_seeds is empty");
                return result;
            }
            foreach (object seedVariant in preferred)
            {
                long preferredSeed = V.I64(seedVariant);
                for (int attempt = 0; attempt <= MaxRerolls; attempt++)
                {
                    long seed = attempt == 0 ? preferredSeed : DeriveSeed(preferredSeed, attempt);
                    result.EvaluatedSeeds.Add(seed);
                    ShipDocuments docs = generateFromSeed(seed, sizeClass, condition);
                    if (docs == null || docs.Layout == null || docs.GameplaySlice == null) break; // nothing generated: next preferred seed
                    if (RejectReason(contract, docs.Layout, docs.GameplaySlice, condition) == "validate")
                        Patch(contract, docs);
                    if (SatisfiesCompleteContract(contract, docs.Layout, docs.GameplaySlice, condition))
                    {
                        result.Success = true;
                        result.Seed = seed;
                        result.Reason = "";
                        return result;
                    }
                }
            }
            result.Reason = ReadableUnsatisfied("no preferred seed produced a valid first-run wreck (tried "
                                               + string.Join(", ", result.EvaluatedSeeds) + ")");
            return result;
        }

        /// <summary>Deterministic re-roll seed: same (seed, attempt) always gives the same value.</summary>
        public static long DeriveSeed(long seed, int attempt) => (seed * 1000003L + attempt) & 0x7FFFFFFFL;

        /// <summary>
        /// Deterministic repair of the two misses the ordinary generator makes against the first-run contract: no
        /// encounter marker, and no required hazard. Idempotent; a no-op on a wreck that already has both. Boarding
        /// applies the same patch (see <see cref="PatchedGenerator"/>) so the player gets the wreck the gate accepted.
        /// </summary>
        public static void Patch(FirstRunContract contract, ShipDocuments docs)
        {
            if (contract == null || docs?.Layout == null || docs.GameplaySlice == null) return;
            PatchLayout(contract, docs.Layout, docs.GameplaySlice);
            if (docs.SourceLayout != null && !ReferenceEquals(docs.SourceLayout, docs.Layout))
                PatchLayout(contract, docs.SourceLayout, docs.GameplaySlice);
            if (docs.LayoutJson != null) docs.LayoutJson = GdJson.Stringify(docs.Layout, "  ");
            AddFirstWreckStores(docs.Layout, docs.GameplaySlice);
            // Phase 1.9: the stores land on the slice document only; the archived/saved text mirror must follow it (RunSession.Generation prefers GameplaySliceJson).
            if (docs.GameplaySliceJson != null) docs.GameplaySliceJson = GdJson.Stringify(docs.GameplaySlice, "  ");
        }

        /// <summary>Id of the authored emergency-stores container the first wreck always carries (Phase 1.3).</summary>
        public const string FirstWreckStoresId = "first_wreck_stores";

        /// <summary>
        /// Phase 1.3: the first wreck always holds one authored cache of food, water and medicine, so the journey out to it is
        /// survivable. It is a separate container beside the rolled ones (an authored <c>contents</c> list replaces a container's roll,
        /// so the existing containers keep their loot). Placed on the first free interior slot of the first non-start room that already holds
        /// loot. Deterministic and idempotent; a wreck with no free slot gets no cache.
        /// </summary>
        static void AddFirstWreckStores(GdDict layout, GdDict gameplaySlice)
        {
            if (layout == null || gameplaySlice == null) return;
            GdArray containers = gameplaySlice.Get("loot_containers", null) as GdArray;
            if (containers == null || containers.IsEmpty) return;
            foreach (object existing in containers)
                if (existing is GdDict c && V.Str(c.Get("id", "")) == FirstWreckStoresId) return;
            string startRoom = V.Str(gameplaySlice.Get("start_room", ""));
            foreach (object containerV in containers)
            {
                if (!(containerV is GdDict container)) continue;
                string roomId = V.Str(container.Get("room_id", ""));
                if (roomId.Length == 0 || roomId == startRoom) continue;
                if (!TryFindFreeSlot(layout, gameplaySlice, roomId, out GdArray cell, out string slotKind, out long slotIndex, out long deck)) continue;
                containers.Append(new GdDict
                {
                    { "id", FirstWreckStoresId },
                    { "kind", "generic_crate" },
                    { "room_id", roomId },
                    { "approach_cell", GdArray.Of(V.I64(cell[0]), V.I64(cell[1]), deck) },
                    { "loot_table", "generic_crate" },
                    { "slot_kind", slotKind },
                    { "slot_index", slotIndex },
                    { "contents", GdArray.Of(
                        Stack("ration_pack", 2), Stack("purified_water", 2), Stack("field_medkit", 1),
                        Stack("bandage_kit", 1), Stack("rad_patch", 1)) },
                });
                return;
            }
        }

        /// <summary>
        /// The first free interior loot slot of <paramref name="roomId"/> (center slots, then wall slots), by the rules authored caches follow:
        /// a slot is blocked when it is a reserved cell of the room or the approach cell of any loot container or objective already in the
        /// room. False when the room does not exist or has no free slot.
        /// </summary>
        public static bool TryFindFreeSlot(GdDict layout, GdDict gameplaySlice, string roomId, out GdArray cell, out string slotKind, out long slotIndex, out long deck)
        {
            cell = null;
            slotKind = "";
            slotIndex = 0;
            deck = 0;
            if (layout == null || gameplaySlice == null || roomId == null || roomId.Length == 0) return false;
            GdDict room = RoomById(layout.GetArrayOrEmpty("rooms"), roomId);
            if (room.IsEmpty) return false;
            deck = V.I64(room.Get("deck", 0L));
            GdDict interior = room.GetDictOrEmpty("interior_zones");
            var blocked = new HashSet<string>();
            foreach (object r in interior.GetArrayOrEmpty("reserved_cells"))
            {
                GdArray reserved = LayoutSerializer.ParseSlotCell(r);
                if (reserved.Count >= 2) blocked.Add(V.I64(reserved[0]) + "," + V.I64(reserved[1]));
            }
            foreach (object other in gameplaySlice.GetArrayOrEmpty("loot_containers"))
                BlockApproach(other as GdDict, roomId, blocked);
            foreach (object objective in gameplaySlice.GetArrayOrEmpty("objectives"))
            {
                BlockApproach(objective as GdDict, roomId, blocked);
                // A repair_junction objective also stands on the approach cell of each of its steps.
                if (objective is GdDict junction && V.Str(junction.Get("room_id", "")) == roomId)
                    foreach (object stepV in junction.GetArrayOrEmpty("steps"))
                        if (stepV is GdDict step)
                        {
                            GdArray stepCell = LayoutSerializer.ParseSlotCell(step.Get("approach_cell", new GdArray()));
                            if (stepCell.Count >= 2) blocked.Add(V.I64(stepCell[0]) + "," + V.I64(stepCell[1]));
                        }
            }
            // Phase 1.10: a generated home also places pickups (home_pickups); wrecks and the golden hub have none.
            foreach (object pickup in gameplaySlice.GetArrayOrEmpty("home_pickups"))
                BlockApproach(pickup as GdDict, roomId, blocked);
            foreach (string bucket in new[] { "center_slots", "wall_slots" })
            {
                GdArray slots = interior.GetArrayOrEmpty(bucket);
                for (int i = 0; i < slots.Count; i++)
                {
                    GdArray candidate = LayoutSerializer.ParseSlotCell(slots[i]);
                    if (candidate.Count < 2 || blocked.Contains(V.I64(candidate[0]) + "," + V.I64(candidate[1]))) continue;
                    cell = candidate;
                    slotKind = bucket == "center_slots" ? "center" : "wall";
                    slotIndex = i;
                    return true;
                }
            }
            return false;
        }

        public static GdDict Stack(string itemId, long qty) => new GdDict { { "item_id", itemId }, { "qty", qty } };

        public static void BlockApproach(GdDict row, string roomId, HashSet<string> blocked)
        {
            if (row == null || V.Str(row.Get("room_id", "")) != roomId) return;
            GdArray cell = LayoutSerializer.ParseSlotCell(row.Get("approach_cell", new GdArray()));
            if (cell.Count >= 2) blocked.Add(V.I64(cell[0]) + "," + V.I64(cell[1]));
        }

        static void PatchLayout(FirstRunContract contract, GdDict layout, GdDict gameplaySlice)
        {
            GdArray rooms = layout.GetArrayOrEmpty("rooms");
            long minEncounters = V.I64(contract.Contract.Get("require_min_encounters", 0L));
            if (layout.GetArrayOrEmpty("encounters").Count < minEncounters) InjectEncounter(layout, rooms);
            if (!contract.HasRequiredHazard(layout, gameplaySlice)) BreachRoom(rooms);
        }

        static void InjectEncounter(GdDict layout, GdArray rooms)
        {
            var critical = new HashSet<string>();
            if (layout.Get("critical_path", null) is GdArray cp) foreach (object r in cp) critical.Add(V.Str(r));
            else foreach (string r in TemplateCTraversal.CriticalPath(layout)) critical.Add(r);
            double cellSize = V.F64(layout.Get("cell_size", 4.0));
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room)) continue;
                string rid = V.Str(room.Get("id", ""));
                if (rid.Length == 0 || critical.Contains(rid)) continue;
                GdArray entries = EncounterInjector.FloorCellEntries(room, cellSize);
                if (entries.IsEmpty) continue;
                var pick = (GdDict)entries[entries.Count >> 1];
                string role = V.Str(room.Get("room_role", room.Get("role", "")));
                var marker = new GdDict
                {
                    { "id", "enc_" + rid + "_1" },
                    { "room_id", rid },
                    { "deck", V.I64(room.Get("deck", 0L)) },
                    { "cell", pick["cell"] },
                    { "local_position", pick["local_position"] },
                    { "encounter_kind", V.Str(EncounterInjector.ROLE_TO_ENCOUNTER_KIND.Get(role, EncounterInjector.DEFAULT_ENCOUNTER_KIND)) },
                    { "count", 1L },
                    { "difficulty_tier", V.Str(layout.Get("difficulty_id", "")) },
                    { "encounter_table_id", "" },
                    { "seed_offset", 1L },
                    { "tension_progress", 1.0 },
                    { "branch_depth", 0L },
                    { "patrol_crosses_critical", false },
                    { "spike", false },
                };
                var markers = layout.GetArrayOrEmpty("encounters");
                markers.Append(marker);
                layout["encounters"] = markers;
                return;
            }
        }

        static void BreachRoom(GdArray rooms)
        {
            GdDict fallback = null;
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room)) continue;
                string role = V.Str(room.Get("room_role", room.Get("role", "")));
                if (Array.IndexOf(BreachRoles, role) >= 0) { room["variant"] = "breached"; return; }
                if (fallback == null && FirstRunContract.IsHazardRole(role)) fallback = room;
            }
            if (fallback != null) fallback["variant"] = "breached";
        }

        /// <summary>Wraps a generator so every wreck it returns is patched like the gate's accepted candidate.</summary>
        public sealed class PatchedGenerator : IShipGenerator
        {
            readonly IShipGenerator _inner;
            readonly FirstRunContract _contract;
            public PatchedGenerator(IShipGenerator inner, FirstRunContract contract) { _inner = inner; _contract = contract; }
            public object GenerateFromSeed(long seedValue, long size = 0, long condition = 1)
            {
                object docs = _inner.GenerateFromSeed(seedValue, size, condition);
                if (docs is ShipDocuments d) Patch(_contract, d);
                return docs;
            }
        }

        public static string ReadableUnsatisfied(string detail) =>
            UnsatisfiedReason + ": " + detail;

        /// <summary>
        /// Complete first-run / boarded-slice contract used to accept a candidate: content validate,
        /// standing start→goal, ≥1 objective, ≥1 interior loot slot, wreck overlay when DAMAGED/WRECKED.
        /// </summary>
        public static bool SatisfiesCompleteContract(FirstRunContract contract, GdDict layout, GdDict gameplaySlice, long condition) =>
            RejectReason(contract, layout, gameplaySlice, condition).Length == 0;

        /// <summary>Empty when the candidate passes; otherwise the first failing check id.</summary>
        public static string RejectReason(FirstRunContract contract, GdDict layout, GdDict gameplaySlice, long condition)
        {
            if (contract == null || !contract.Validate(layout, gameplaySlice)) return "validate";
            if (!HasStandingStartToGoal(layout)) return "standing";
            if (ObjectiveCount(gameplaySlice) < 1) return "objectives";
            if (!HasInteriorLootSlot(layout, gameplaySlice)) return "interior_loot";
            if (RequiresWreckOverlay(condition) && !HasWreckOverlay(layout)) return "wreck";
            return "";
        }

        public static bool RequiresWreckOverlay(long condition) =>
            condition == (long)ShipBlueprint.Condition.Damaged || condition == (long)ShipBlueprint.Condition.Wrecked;

        public static long ObjectiveCount(GdDict gameplaySlice)
        {
            if (gameplaySlice == null) return 0;
            return gameplaySlice.GetArrayOrEmpty("objectives").Count;
        }

        public static bool HasInteriorLootSlot(GdDict layout, GdDict gameplaySlice)
        {
            if (layout == null || gameplaySlice == null) return false;
            GdArray rooms = layout.GetArrayOrEmpty("rooms");
            foreach (object lootV in gameplaySlice.GetArrayOrEmpty("loot_containers"))
            {
                if (!(lootV is GdDict row)) continue;
                GdDict room = RoomById(rooms, V.Str(row.Get("room_id", "")));
                GdArray approach = LayoutSerializer.ParseSlotCell(row.Get("approach_cell", new GdArray()));
                if (approach.Count < 2) continue;
                if (CellInSlots(room, approach)) return true;
            }
            return false;
        }

        public static bool HasWreckOverlay(GdDict layout)
        {
            if (layout == null || !layout.GetBool("wreck_applied")) return false;
            if (!layout.GetArrayOrEmpty("blocked_links").IsEmpty) return true;
            if (!layout.GetArrayOrEmpty("module_damage").IsEmpty) return true;
            GdDict edges = layout.GetDictOrEmpty("structural_plan").GetDictOrEmpty("edges");
            foreach (object edgeV in edges.Values)
            {
                if (!(edgeV is GdDict edge)) continue;
                string kind = V.Str(edge.Get("kind", "")).ToUpperInvariant();
                if (kind == "LOCKED" || kind == "BREACH") return true;
            }
            return false;
        }

        /// <summary>Godot <c>generated_seed_boarded_slice_smoke.gd</c> <c>_standing_start_to_goal</c> (layout-only).</summary>
        public static bool HasStandingStartToGoal(GdDict layout)
        {
            if (layout == null || layout.IsEmpty) return false;
            var graph = new ShipNavGraph();
            long nodeN = graph.BuildFromLayout(layout);
            if (nodeN <= 0) return false;
            GdDict proto = layout.GetDictOrEmpty("prototype");
            string startId = V.Str(proto.Get("start_room", ""));
            string goalId = V.Str(proto.Get("goal_room", ""));
            if (startId.Length == 0 || goalId.Length == 0) return false;
            Vec3 startPos = StandingRoomPos(layout, startId);
            Vec3 goalPos = StandingRoomPos(layout, goalId);
            if (startPos == Vec3.Inf || goalPos == Vec3.Inf) return false;
            string startNode = NearestNodeInRoom(graph, startPos, startId);
            if (startNode.Length == 0 || graph.GetNodeRoom(startNode) != startId) return false;
            if (startId == goalId) return true;
            string goalNode = NearestNodeInRoom(graph, goalPos, goalId);
            if (goalNode.Length == 0 || graph.GetNodeRoom(goalNode) != goalId) return false;
            return ThreatPathfinder.FindPath(graph, graph.GetNodePos(startNode), graph.GetNodePos(goalNode)).Count > 0;
        }

        internal static string NearestNodeInRoom(ShipNavGraph graph, Vec3 worldPos, string roomId)
        {
            string best = "";
            double bestD = double.PositiveInfinity;
            foreach (object key in graph.Nodes.Keys)
            {
                string nid = V.Str(key);
                if (graph.GetNodeRoom(nid) != roomId) continue;
                double d = graph.GetNodePos(nid).DistanceSquaredTo(worldPos);
                if (d < bestD)
                {
                    bestD = d;
                    best = nid;
                }
            }
            return best;
        }

        internal static Vec3 StandingRoomPos(GdDict layout, string roomId)
        {
            GdDict occupancy = layout.GetDictOrEmpty("structural_plan").GetDictOrEmpty("occupancy");
            foreach (object recordV in occupancy.Values)
            {
                if (!(recordV is GdDict record)) continue;
                if (V.Str(record.Get("room_id", "")) != roomId) continue;
                Vec3 pos = WalkabilityContract.OccupancyWorldPosition(record);
                if (pos != Vec3.Inf) return pos;
            }
            GdDict room = RoomById(layout.GetArrayOrEmpty("rooms"), roomId);
            foreach (object placementV in room.GetArrayOrEmpty("structural_placements"))
            {
                if (!(placementV is GdDict placement)) continue;
                string moduleId = V.Str(placement.Get("module_id", placement.Get("module", "")));
                if (!GdString.BeginsWith(moduleId, "floor_") && !GdString.BeginsWith(moduleId, "corridor_floor")) continue;
                object wp = placement.Get("world_position", null);
                if (wp is Vec3 v) return v;
                if (wp is GdArray arr && arr.Count >= 3) return new Vec3(V.F64(arr[0]), V.F64(arr[1]), V.F64(arr[2]));
            }
            return Vec3.Inf;
        }

        static GdDict RoomById(GdArray rooms, string roomId)
        {
            foreach (object roomV in rooms)
            {
                if (roomV is GdDict room && V.Str(room.Get("id", "")) == roomId) return room;
            }
            return new GdDict();
        }

        public static bool CellInSlots(GdDict room, GdArray cell)
        {
            if (room == null || cell.Count < 2) return false;
            GdDict interior = room.GetDictOrEmpty("interior_zones");
            foreach (string bucket in new[] { "center_slots", "wall_slots" })
            {
                foreach (object item in interior.GetArrayOrEmpty(bucket))
                {
                    GdArray parsed = LayoutSerializer.ParseSlotCell(item);
                    if (parsed.Count >= 2 && V.I64(parsed[0]) == V.I64(cell[0]) && V.I64(parsed[1]) == V.I64(cell[1]))
                        return true;
                }
            }
            return false;
        }
    }
}
