using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Phase 1.10: the onboarding chain of a generated New Run home. The golden hub teaches the game through four typed objectives (supplies, a two-step
    /// junction repair, a log download, the reactor) whose completion force-repairs the flight path, grants experience and opens the powered routes; the
    /// generator only knows <c>salvage</c> and <c>interact</c>, so a generated home would teach nothing and bring no systems online. The composer replaces a
    /// generated home's objectives with those four types, placing each by room <b>role</b> (<see cref="StationPlacer.PREFERRED_ROOM_ROLES"/>) with a fallback
    /// to any usable room, on free interior slots of seeded rooms. It also places the two pickups the chain hands out (the portable oxygen pump and the
    /// junction calibrator) on the home's floor, in <c>home_pickups</c>, so the session never has to guess a position next to the survivor.
    /// <para>The composer is a pure post-pass on the generated documents (the pattern of <see cref="StartingHomeGuarantee"/> and <see cref="HomeDockPlanner"/>),
    /// deterministic per seed, and fails closed with a named reason so <see cref="StartSceneBuilder.BuildHomeStart"/> rolls the next seed.
    /// Generated homes only; the golden hub keeps its authored objectives.</para>
    /// </summary>
    public static class HomeObjectiveComposer
    {
        /// <summary>A home needs at least this many usable (non-connective, reachable) rooms to host the chain, its caches and its pickups.</summary>
        public const int MinUsableRooms = 3;
        public const string ReasonTooFewRooms = "too_few_rooms";
        public const string ReasonNoRoom = "no_room_for_objective";
        public const string ReasonNoPickupSlot = "no_slot_for_pickup";
        public const string ReasonUnreachable = "unreachable_objective";

        public const string PickupsKey = "home_pickups";
        public const string ToolPickupId = "portable_oxygen_pump";
        public const string CalibratorPickupId = "junction_calibrator";
        const int PlacementSalt = 0x0B7EC;

        /// <summary>The four onboarding objective types in sequence order, with the identity the golden slice gives each.</summary>
        sealed class Kind
        {
            public string Type = "";
            public string IdSuffix = "";
            public string PlacementId = "";
            public string Semantic = "";
            public bool Junction;
            public string RoleKey => StationPlacer.ObjectiveKindPrefix + Type;
        }

        static readonly Kind[] Chain =
        {
            new Kind { Type = "recover_supplies", IdSuffix = "cargo_supply_cache", PlacementId = "cargo_supply_cache", Semantic = "loot_container" },
            new Kind { Type = "restore_systems", IdSuffix = "junction_alpha", PlacementId = "maintenance_breaker_panel", Semantic = "junction_box", Junction = true },
            new Kind { Type = "download_logs", IdSuffix = "medbay_terminal", PlacementId = "medbay_terminal", Semantic = "command_console" },
            new Kind { Type = "stabilize_reactor", IdSuffix = "reactor_control_panel", PlacementId = "reactor_control_panel", Semantic = "reactor_control_panel" },
        };

        public static readonly IReadOnlyList<string> ChainTypes = Chain.Select(k => k.Type).ToList();

        static string RoleOf(GdDict room)
        {
            string role = V.Str(room.Get("room_role", ""));
            return role.Length != 0 ? role : V.Str(room.Get("role", ""));
        }

        static GdDict RoomById(GdDict layout, string roomId)
        {
            foreach (object roomV in layout.GetArrayOrEmpty("rooms"))
                if (roomV is GdDict room && V.Str(room.Get("id", "")) == roomId) return room;
            return new GdDict();
        }

        static GdArray Cell(GdArray xz, long deck) => GdArray.Of(V.I64(xz[0]), V.I64(xz[1]), deck);

        // ------------------------------------------------------------------ placement

        /// <summary>
        /// Rooms <paramref name="kind"/> may use, best first: unused rooms before reused ones, then the role's priority (a room whose role is not in the
        /// preferred list ranks last), then the seeded order.
        /// </summary>
        static List<string> Candidates(GdDict layout, IList<string> seeded, string roleKey, Dictionary<string, int> uses)
        {
            string[] roles = StationPlacer.PREFERRED_ROOM_ROLES.TryGetValue(roleKey, out string[] r) ? r : new string[0];
            int Priority(string roomId)
            {
                int i = Array.IndexOf(roles, RoleOf(RoomById(layout, roomId)));
                return i < 0 ? roles.Length : i;
            }
            return seeded
                .Select((id, rank) => (id, rank))
                .OrderBy(x => uses.TryGetValue(x.id, out int n) ? n : 0)
                .ThenBy(x => Priority(x.id))
                .ThenBy(x => x.rank)
                .Select(x => x.id)
                .ToList();
        }

        static GdDict Objective(Kind kind, long sequence, string roomId, string role, GdArray approach, GdArray secondary)
        {
            var row = new GdDict
            {
                { "id", roomId + ":" + kind.IdSuffix },
                { "sequence", sequence },
                { "type", kind.Type },
            };
            if (kind.Junction) row["kind"] = "repair_junction";
            row["room_id"] = roomId;
            row["room_role"] = role;
            row["placement_id"] = kind.PlacementId;
            row["semantic"] = kind.Semantic;
            row["cell"] = approach.ShallowCopy();
            row["approach_cell"] = approach.ShallowCopy();
            row["approach_distance_cells"] = 0L;
            row["interactable"] = true;
            if (kind.Junction && secondary != null)
            {
                row["steps"] = GdArray.Of(
                    new GdDict { { "step_id", "primary_coupling" }, { "approach_cell", approach.ShallowCopy() } },
                    new GdDict { { "step_id", "secondary_coupling" }, { "approach_cell", secondary.ShallowCopy() } });
            }
            return row;
        }

        /// <summary>
        /// Replaces the generated objectives of <paramref name="docs"/> with the four onboarding objectives, adds the two pickups, and brings every mirror
        /// of the gameplay slice up to date. "" on success, else the rejection reason; the documents are only changed on success.
        /// </summary>
        public static string Apply(ShipDocuments docs, long seed)
        {
            if (docs?.Layout == null || docs.GameplaySlice == null) return "no documents";
            GdDict layout = docs.Layout;
            GdDict slice = docs.GameplaySlice;

            List<string> eligible = StartingHomeGuarantee.EligibleRooms(layout, slice);
            if (eligible.Count < MinUsableRooms) return ReasonTooFewRooms + ":" + eligible.Count;
            GodotRandom rng = GodotRandom.FromSeed(FirstRunAwayGate.DeriveSeed(seed, PlacementSalt));
            for (int i = eligible.Count - 1; i > 0; i--)
            {
                int j = (int)rng.RandiRange(0, i);
                (eligible[i], eligible[j]) = (eligible[j], eligible[i]);
            }

            // A probe copy of the slice with no objectives: TryFindFreeSlot blocks the approach cells of every container, objective and pickup in a room.
            GdDict probe = slice.DeepCopy();
            var probeObjectives = new GdArray();
            probe["objectives"] = probeObjectives;
            var probePickups = new GdArray();
            probe[PickupsKey] = probePickups;
            var uses = new Dictionary<string, int>(StringComparer.Ordinal);

            var objectives = new GdArray();
            for (int i = 0; i < Chain.Length; i++)
            {
                Kind kind = Chain[i];
                bool placed = false;
                foreach (string roomId in Candidates(layout, eligible, kind.RoleKey, uses))
                {
                    if (!FirstRunAwayGate.TryFindFreeSlot(layout, probe, roomId, out GdArray cell, out _, out _, out long deck)) continue;
                    GdArray approach = Cell(cell, deck);
                    GdArray secondary = null;
                    GdDict row = Objective(kind, i + 1, roomId, RoleOf(RoomById(layout, roomId)), approach, null);
                    probeObjectives.Append(row);
                    if (kind.Junction)
                    {
                        if (!FirstRunAwayGate.TryFindFreeSlot(layout, probe, roomId, out GdArray cell2, out _, out _, out long deck2))
                        {
                            probeObjectives.RemoveAt(probeObjectives.Count - 1);
                            continue;
                        }
                        secondary = Cell(cell2, deck2);
                        row = Objective(kind, i + 1, roomId, RoleOf(RoomById(layout, roomId)), approach, secondary);
                        probeObjectives[probeObjectives.Count - 1] = row;
                        probeObjectives.Append(new GdDict { { "room_id", roomId }, { "approach_cell", secondary.ShallowCopy() } }); // blocks the second coupling's cell too
                    }
                    objectives.Append(row.DeepCopy());
                    uses[roomId] = (uses.TryGetValue(roomId, out int n) ? n : 0) + 1;
                    placed = true;
                    break;
                }
                if (!placed) return ReasonNoRoom + ":" + kind.Type;
            }

            var pickups = new GdArray();
            foreach ((string toolId, string roleKey) in new[] { (ToolPickupId, "tool_pickup"), (CalibratorPickupId, "calibrator_pickup") })
            {
                bool placed = false;
                foreach (string roomId in Candidates(layout, eligible, roleKey, uses))
                {
                    if (!FirstRunAwayGate.TryFindFreeSlot(layout, probe, roomId, out GdArray cell, out _, out _, out long deck)) continue;
                    var pickup = new GdDict { { "tool_id", toolId }, { "room_id", roomId }, { "approach_cell", Cell(cell, deck) } };
                    probePickups.Append(pickup);
                    pickups.Append(pickup.DeepCopy());
                    uses[roomId] = (uses.TryGetValue(roomId, out int n) ? n : 0) + 1;
                    placed = true;
                    break;
                }
                if (!placed) return ReasonNoPickupSlot + ":" + toolId;
            }

            // Commit, then bring both text mirrors up to date (and re-parse so the documents keep the types of their JSON text).
            slice["objectives"] = objectives;
            slice[PickupsKey] = pickups;
            docs.GameplaySliceJson = GdJson.Stringify(docs.GameplaySlice, "  ");
            docs.GameplaySlice = GdJson.ParseDict(docs.GameplaySliceJson);
            return "";
        }

        // ------------------------------------------------------------------ independent check

        /// <summary>
        /// Checks the finished documents without trusting <see cref="Apply"/>: the four typed objectives in order on real, distinct loot slots of usable
        /// rooms (a two-step junction with two distinct cells), both pickups on free slots, the text mirror in agreement, and at least
        /// <see cref="MinUsableRooms"/> usable rooms. "" when the chain is sound.
        /// </summary>
        public static string Validate(ShipDocuments docs)
        {
            if (docs?.Layout == null || docs.GameplaySlice == null) return "no documents";
            GdDict layout = docs.Layout;
            GdDict slice = docs.GameplaySlice;
            List<string> usable = StartingHomeGuarantee.EligibleRooms(layout, slice);
            if (usable.Count < MinUsableRooms) return ReasonTooFewRooms + ":" + usable.Count;
            var usableSet = new HashSet<string>(usable, StringComparer.Ordinal);

            GdArray objectives = slice.GetArrayOrEmpty("objectives");
            var types = objectives.OfType<GdDict>().Select(o => o.GetString("type")).ToList();
            if (!types.SequenceEqual(ChainTypes)) return "objective types [" + string.Join(",", types) + "] are not the onboarding chain";

            var cells = new HashSet<string>(StringComparer.Ordinal);
            foreach (GdDict container in slice.GetArrayOrEmpty("loot_containers").OfType<GdDict>())
                cells.Add(CellKey(V.Str(container.Get("room_id", "")), container.Get("approach_cell", new GdArray())));

            long sequence = 0;
            foreach (GdDict objective in objectives.OfType<GdDict>())
            {
                sequence++;
                string type = objective.GetString("type");
                if (V.I64(objective.Get("sequence", 0L)) != sequence) return type + " has sequence " + V.I64(objective.Get("sequence", 0L)) + ", expected " + sequence;
                string roomId = objective.GetString("room_id");
                if (!usableSet.Contains(roomId)) return type + " is in room " + roomId + ", which is not a usable room";
                GdDict room = RoomById(layout, roomId);
                var approach = LayoutSerializer.ParseSlotCell(objective.Get("approach_cell", new GdArray()));
                if (!FirstRunAwayGate.CellInSlots(room, approach)) return type + " is not on a loot slot of " + roomId;
                if (!cells.Add(CellKey(roomId, approach))) return type + " shares a cell with another container or objective in " + roomId;
                if (type == "restore_systems")
                {
                    GdArray steps = objective.GetArrayOrEmpty("steps");
                    if (objective.GetString("kind") != "repair_junction" || steps.Count < 2) return "restore_systems is not a two-step repair_junction";
                    var stepKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (GdDict step in steps.OfType<GdDict>())
                    {
                        var stepCell = LayoutSerializer.ParseSlotCell(step.Get("approach_cell", new GdArray()));
                        if (!FirstRunAwayGate.CellInSlots(room, stepCell)) return "a junction step is not on a loot slot of " + roomId;
                        if (!stepKeys.Add(CellKey(roomId, stepCell))) return "two junction steps share a cell";
                        if (CellKey(roomId, stepCell) != CellKey(roomId, approach) && !cells.Add(CellKey(roomId, stepCell)))
                            return "a junction step shares cell " + CellKey(roomId, stepCell) + " with another container or objective in " + roomId + " (primary " + CellKey(roomId, approach) + ")";
                    }
                }
            }

            GdArray pickups = slice.GetArrayOrEmpty(PickupsKey);
            foreach (string tool in new[] { ToolPickupId, CalibratorPickupId })
            {
                GdDict pickup = pickups.OfType<GdDict>().FirstOrDefault(p => p.GetString("tool_id") == tool);
                if (pickup == null) return "pickup " + tool + " missing";
                string roomId = pickup.GetString("room_id");
                if (!usableSet.Contains(roomId)) return "pickup " + tool + " is in room " + roomId + ", which is not a usable room";
                var cell = LayoutSerializer.ParseSlotCell(pickup.Get("approach_cell", new GdArray()));
                if (!FirstRunAwayGate.CellInSlots(RoomById(layout, roomId), cell)) return "pickup " + tool + " is not on a loot slot of " + roomId;
                if (!cells.Add(CellKey(roomId, cell))) return "pickup " + tool + " shares a cell with another container, objective or pickup in " + roomId;
            }

            if (docs.GameplaySliceJson == null) return "slice text mirror missing";
            GdDict text = GdJson.ParseDict(docs.GameplaySliceJson);
            var textIds = text.GetArrayOrEmpty("objectives").OfType<GdDict>().Select(o => o.GetString("id")).ToList();
            var ids = objectives.OfType<GdDict>().Select(o => o.GetString("id")).ToList();
            if (!ids.SequenceEqual(textIds)) return "slice text mirror disagrees on the objectives";
            if (text.GetArrayOrEmpty(PickupsKey).Count != pickups.Count) return "slice text mirror disagrees on the pickups";
            return "";
        }

        static string CellKey(string roomId, object cell)
        {
            GdArray parsed = LayoutSerializer.ParseSlotCell(cell);
            return parsed.Count < 2 ? roomId + "|?" : roomId + "|" + V.I64(parsed[0]) + "," + V.I64(parsed[1]);
        }

        // ------------------------------------------------------------------ walkability from the dock

        /// <summary>
        /// A cell-level standing path check, stricter than room adjacency: from the start/dock room's boarding cell to the approach cell of every
        /// objective (and junction step), pickup and loot container of the home. "" when all are reachable, else <c>unreachable_*:&lt;what&gt;:&lt;room&gt;</c>.
        /// </summary>
        public static string ReachabilityReason(ShipDocuments docs)
        {
            if (docs?.Layout == null || docs.GameplaySlice == null) return "no documents";
            GdDict layout = docs.Layout;
            GdDict slice = docs.GameplaySlice;
            string startRoom = V.Str(slice.Get("start_room", ""));
            var graph = new ShipNavGraph();
            if (graph.BuildFromLayout(layout) <= 0) return "no navigation graph";
            Vec3 startPos = FirstRunAwayGate.StandingRoomPos(layout, startRoom);
            string startNode = startPos == Vec3.Inf ? "" : FirstRunAwayGate.NearestNodeInRoom(graph, startPos, startRoom);
            if (startNode.Length == 0) return "no navigation node in the start room " + startRoom;

            foreach ((string label, string roomId, object cell) in Targets(slice))
            {
                if (roomId == startRoom) continue;
                GdArray parsed = LayoutSerializer.ParseSlotCell(cell);
                if (parsed.Count < 2) return ReasonUnreachable + ":" + label + ":" + roomId + " (no cell)";
                long deck = V.I64(RoomById(layout, roomId).Get("deck", 0L));
                if (cell is GdArray withDeck && withDeck.Count >= 3) deck = V.I64(withDeck[2]);
                Vec3 pos = StructuralEdgeCompiler.CellWorldPosition(deck, new Vec2i((int)V.I64(parsed[0]), (int)V.I64(parsed[1])));
                string node = FirstRunAwayGate.NearestNodeInRoom(graph, pos, roomId);
                if (node.Length == 0) return ReasonUnreachable + ":" + label + ":" + roomId + " (no navigation node)";
                if (ThreatPathfinder.FindPath(graph, graph.GetNodePos(startNode), graph.GetNodePos(node)).Count == 0)
                    return ReasonUnreachable + ":" + label + ":" + roomId;
            }
            return "";
        }

        static IEnumerable<(string label, string roomId, object cell)> Targets(GdDict slice)
        {
            foreach (GdDict objective in slice.GetArrayOrEmpty("objectives").OfType<GdDict>())
            {
                string room = objective.GetString("room_id");
                yield return (objective.GetString("type"), room, objective.Get("approach_cell", new GdArray()));
                foreach (GdDict step in objective.GetArrayOrEmpty("steps").OfType<GdDict>())
                    yield return (objective.GetString("type") + "/" + step.GetString("step_id"), room, step.Get("approach_cell", new GdArray()));
            }
            foreach (GdDict pickup in slice.GetArrayOrEmpty(PickupsKey).OfType<GdDict>())
                yield return ("pickup_" + pickup.GetString("tool_id"), pickup.GetString("room_id"), pickup.Get("approach_cell", new GdArray()));
            foreach (GdDict container in slice.GetArrayOrEmpty("loot_containers").OfType<GdDict>())
                yield return ("cache_" + container.GetString("id"), container.GetString("room_id"), container.Get("approach_cell", new GdArray()));
        }
    }
}
