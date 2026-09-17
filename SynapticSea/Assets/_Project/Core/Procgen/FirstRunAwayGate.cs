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
                long seed = V.I64(seedVariant);
                result.EvaluatedSeeds.Add(seed);
                ShipDocuments docs = generateFromSeed(seed, sizeClass, condition);
                if (docs == null || docs.Layout == null || docs.GameplaySlice == null) continue;
                if (SatisfiesCompleteContract(contract, docs.Layout, docs.GameplaySlice, condition))
                {
                    result.Success = true;
                    result.Seed = seed;
                    result.Reason = "";
                    return result;
                }
            }
            result.Reason = ReadableUnsatisfied("no preferred seed produced a valid first-run wreck (tried "
                                               + string.Join(", ", result.EvaluatedSeeds) + ")");
            return result;
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

        static string NearestNodeInRoom(ShipNavGraph graph, Vec3 worldPos, string roomId)
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

        static Vec3 StandingRoomPos(GdDict layout, string roomId)
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

        static bool CellInSlots(GdDict room, GdArray cell)
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
