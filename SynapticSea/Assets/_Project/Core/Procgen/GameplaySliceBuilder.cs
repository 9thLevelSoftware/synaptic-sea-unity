// Ported from scripts/procgen/gameplay_slice_builder.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Builds a gameplay_slice dictionary from a completed layout: start/goal rooms, objectives, loot containers and
    /// the arc_zones hazard array (fire/breach stay runtime-seeded). The layout pipeline produces structural geometry
    /// only; this builder adds the gameplay layer on top.
    /// </summary>
    public sealed class GameplaySliceBuilder
    {
        public static readonly IReadOnlyList<string> CONNECTIVE_ROLES = new[]
        {
            "corridor", "main_spine", "hub", "ramp", "elevator", "airlock", "dock",
        };

        readonly RoomVariantSelector _variantSelector = new RoomVariantSelector();

        /// <summary>The variant's loot_bias when present and non-empty, else the role-derived default.</summary>
        string LootTableForRoom(GdDict room, string roleDefault)
        {
            string variant = V.Str(room.Get("variant", "standard"));
            string bias = V.Str(_variantSelector.EffectsFor(variant).GetDictOrEmpty("sim").Get("loot_bias", ""));
            return bias.Length != 0 ? bias : roleDefault;
        }

        /// <summary>LayoutSerializer stamps the seed in program_id (<c>procgen-...-seed-N</c>).</summary>
        static long SeedFromLayoutDoc(GdDict layout)
        {
            if (layout.Has("seed_value")) return V.I64(layout.Get("seed_value", 0L));
            string pid = V.Str(layout.Get("program_id", ""));
            int idx = GdString.RFind(pid, "seed-");
            if (idx < 0) return 0;
            string tail = pid.Substring(idx + 5);
            string digits = "";
            for (int i = 0; i < tail.Length; i++)
            {
                string ch = tail.Substring(i, 1);
                if (GdString.IsValidInt(ch)) digits += ch;
                else break;
            }
            return GdString.IsValidInt(digits) ? V.StringToInt(digits) : 0;
        }

        static bool IsConnective(string role)
        {
            foreach (string r in CONNECTIVE_ROLES)
                if (r == role) return true;
            return false;
        }

        public GdDict Build(GdDict layout)
        {
            GdDict proto = layout.GetDictOrEmpty("prototype");
            GdArray rooms = layout.GetArrayOrEmpty("rooms");
            long seedValue = SeedFromLayoutDoc(layout);

            string startRoom = V.Str(proto.Get("start_room", ""));
            string goalRoom = V.Str(proto.Get("goal_room", ""));

            // Fallback: if prototype doesn't specify start/goal, pick from rooms.
            if (startRoom.Length == 0 || goalRoom.Length == 0)
            {
                string airlockId = "";
                string bridgeId = "";
                foreach (GdDict room in rooms)
                {
                    string role = V.Str(room.Get("room_role", ""));
                    string rid = V.Str(room.Get("id", ""));
                    if (role == "airlock" && airlockId.Length == 0) airlockId = rid;
                    if (role == "bridge" && bridgeId.Length == 0) bridgeId = rid;
                }
                if (startRoom.Length == 0)
                    startRoom = airlockId.Length != 0 ? airlockId : rooms.Count > 0 ? V.Str(((GdDict)rooms[0]).Get("id", "")) : "";
                if (goalRoom.Length == 0)
                    goalRoom = bridgeId.Length != 0 ? bridgeId : rooms.Count > 0 ? V.Str(((GdDict)rooms[rooms.Count - 1]).Get("id", "")) : "";
            }

            var occupied = new GdDict();
            GdDict boarding = BoardingInfo(rooms, startRoom);

            var objectives = new GdArray();
            long sequence = 1;

            // Salvage objectives in non-connective rooms.
            long roomIndex = 0;
            foreach (GdDict room in rooms)
            {
                string rid = V.Str(room.Get("id", ""));
                string role = V.Str(room.Get("room_role", ""));
                if (rid == startRoom || rid == goalRoom || IsConnective(role))
                {
                    roomIndex += 1;
                    continue;
                }
                GdDict salvagePick = PickSlotCell(room, "salvage", occupied, boarding, seedValue, roomIndex);
                GdArray approachCell = salvagePick.Get("cell", new GdArray()) as GdArray ?? new GdArray();
                if (approachCell.IsEmpty)
                {
                    roomIndex += 1;
                    continue;
                }
                var salvageObj = new GdDict
                {
                    { "id", "obj_salvage_" + rid },
                    { "sequence", sequence },
                    { "type", "salvage" },
                    { "kind", "single" },
                    { "room_id", rid },
                    { "approach_cell", approachCell },
                    { "loot_table", LootTableForRoom(room, SalvageLootTableForRole(role)) },
                };
                if (V.Str(salvagePick.Get("slot_kind", "")).Length != 0)
                {
                    salvageObj["slot_kind"] = V.Str(salvagePick.Get("slot_kind", ""));
                    salvageObj["slot_index"] = V.I64(salvagePick.Get("slot_index", 0L));
                }
                objectives.Append(salvageObj);
                sequence += 1;
                roomIndex += 1;
            }

            // Always add a "reach goal" objective as the final objective.
            GdDict goalRoomDict = FindRoom(rooms, goalRoom);
            GdDict goalPick = PickSlotCell(goalRoomDict, "loot", occupied, boarding, seedValue, roomIndex);
            GdArray goalApproach = goalPick.Get("cell", new GdArray()) as GdArray ?? new GdArray();
            if (goalApproach.IsEmpty)
            {
                CoreServices.Log.Warning("GameplaySliceBuilder: goal room '" + goalRoom + "' has no floor cells; using [0,0,0] fallback");
                goalApproach = GdArray.Of(0L, 0L, 0L);
            }
            var goalObj = new GdDict
            {
                { "id", "obj_reach_goal" },
                { "sequence", sequence },
                { "type", "interact" },
                { "kind", "single" },
                { "room_id", goalRoom },
                { "approach_cell", goalApproach },
            };
            if (V.Str(goalPick.Get("slot_kind", "")).Length != 0)
            {
                goalObj["slot_kind"] = V.Str(goalPick.Get("slot_kind", ""));
                goalObj["slot_index"] = V.I64(goalPick.Get("slot_index", 0L));
            }
            objectives.Append(goalObj);

            var lootContainers = new GdArray();
            long containerIndex = 0;
            roomIndex = 0;
            foreach (GdDict room in rooms)
            {
                string rid2 = V.Str(room.Get("id", ""));
                string role2 = V.Str(room.Get("room_role", ""));
                if (rid2 == startRoom || rid2 == goalRoom || IsConnective(role2))
                {
                    roomIndex += 1;
                    continue;
                }
                GdDict lootPick = PickSlotCell(room, "loot", occupied, boarding, seedValue, roomIndex);
                GdArray cell2 = lootPick.Get("cell", new GdArray()) as GdArray ?? new GdArray();
                if (cell2.IsEmpty)
                {
                    roomIndex += 1;
                    continue;
                }
                string kind2 = containerIndex % 2 == 1 ? "generic_locker" : "generic_crate";
                var lootEntry = new GdDict
                {
                    { "id", "loot_" + rid2 },
                    { "kind", kind2 },
                    { "room_id", rid2 },
                    { "approach_cell", cell2 },
                    { "loot_table", LootTableForRoom(room, kind2) },
                };
                if (V.Str(lootPick.Get("slot_kind", "")).Length != 0)
                {
                    lootEntry["slot_kind"] = V.Str(lootPick.Get("slot_kind", ""));
                    lootEntry["slot_index"] = V.I64(lootPick.Get("slot_index", 0L));
                }
                lootContainers.Append(lootEntry);
                containerIndex += 1;
                roomIndex += 1;
            }

            return new GdDict
            {
                { "start_room", startRoom },
                { "goal_room", goalRoom },
                { "objectives", objectives },
                { "loot_containers", lootContainers },
                { "fire_zones", new GdArray() },
                { "arc_zones", BuildArcZones(layout, startRoom, goalRoom, objectives) },
                { "breach_zones", new GdArray() },
            };
        }

        /// <summary>
        /// One deterministic arc zone on the first NON-critical link whose endpoints are off the critical path, not the
        /// start/goal room and host no objective; [] when no safe side link exists.
        /// </summary>
        static GdArray BuildArcZones(GdDict layout, string startRoom, string goalRoom, GdArray objectives)
        {
            if (!(layout.Get("room_links", new GdArray()) is GdArray links)) return new GdArray();
            var excluded = new HashSet<string> { startRoom, goalRoom };
            if (layout.Get("critical_path", new GdArray()) is GdArray cp)
                foreach (var rid in cp) excluded.Add(V.Str(rid));
            foreach (var objective in objectives)
                if (objective is GdDict o) excluded.Add(V.Str(o.Get("room_id", "")));
            foreach (var linkVariant in links)
            {
                if (!(linkVariant is GdDict link)) continue;
                string fromRoom = V.Str(link.Get("from_room", ""));
                string toRoom = V.Str(link.Get("to_room", ""));
                if (fromRoom.Length == 0 || toRoom.Length == 0) continue;
                if (excluded.Contains(fromRoom) || excluded.Contains(toRoom)) continue;
                var entry = new GdDict
                {
                    { "id", fromRoom + "_to_" + toRoom + "_arc" },
                    { "from_room", fromRoom },
                    { "to_room", toRoom },
                    { "kind", "electrical_arc" },
                    { "rationale", "generated: non-critical side link off the objective spine; arc cannot trap the player or block an objective" },
                };
                if (link.Has("from_cell")) entry["from_cell"] = link["from_cell"];
                if (link.Has("to_cell")) entry["to_cell"] = link["to_cell"];
                return GdArray.Of(entry);
            }
            return new GdArray();
        }

        static GdDict FindRoom(GdArray rooms, string roomId)
        {
            foreach (GdDict room in rooms)
                if (V.Str(room.Get("id", "")) == roomId) return room;
            return new GdDict();
        }

        /// <summary>Maps a room role to a salvage loot table key.</summary>
        static string SalvageLootTableForRole(string role)
        {
            switch (role)
            {
                case "engineering":
                case "engine":
                case "reactor":
                case "machine_shop":
                    return "salvage_engineering";
                default:
                    return "salvage_cargo";
            }
        }

        /// <summary>Start-room boarding xz: reserved_cells[0], else the first floor_cell_*.</summary>
        public static GdArray BoardingCellXz(GdDict room)
        {
            if (room.Get("interior_zones", new GdDict()) is GdDict interior)
            {
                if (interior.Get("reserved_cells", new GdArray()) is GdArray reserved && !reserved.IsEmpty)
                {
                    GdArray parsed = LayoutSerializer.ParseSlotCell(reserved[0]);
                    if (parsed.Count >= 2) return parsed;
                }
            }
            foreach (var placement in room.GetArrayOrEmpty("structural_placements"))
            {
                if (!(placement is GdDict p)) continue;
                string placementName = V.Str(p.Get("name", ""));
                if (!GdString.BeginsWith(placementName, "floor_cell")) continue;
                GdArray parsedFloor = LayoutSerializer.ParseSlotCell(placementName);
                if (parsedFloor.Count >= 2) return parsedFloor;
            }
            return new GdArray();
        }

        static GdDict BoardingInfo(GdArray rooms, string startRoom)
        {
            GdDict room = FindRoom(rooms, startRoom);
            if (room.IsEmpty || startRoom.Length == 0) return new GdDict();
            GdArray cell = BoardingCellXz(room);
            if (cell.Count < 2) return new GdDict();
            return new GdDict
            {
                { "room_id", startRoom },
                { "cell", cell },
                { "deck", V.I64(room.Get("deck", 0L)) },
            };
        }

        static GdArray InteriorCellList(GdDict room, string slotKey)
        {
            if (!(room.Get("interior_zones", new GdDict()) is GdDict interior)) return new GdArray();
            if (!(interior.Get(slotKey, new GdArray()) is GdArray raw)) return new GdArray();
            var output = new GdArray();
            foreach (var item in raw)
            {
                GdArray parsed = LayoutSerializer.ParseSlotCell(item);
                if (parsed.Count >= 2) output.Append(parsed);
            }
            return output;
        }

        static string CellKey(string roomId, GdArray cell)
        {
            if (cell.Count < 2) return "";
            return roomId + "|" + GdString.FormatInt(V.I64(cell[0])) + "|" + GdString.FormatInt(V.I64(cell[1]));
        }

        static bool IsBlocked(string roomId, GdArray cell, GdDict occupied, GdDict boarding)
        {
            if (cell.Count < 2) return true;
            if (V.Str(boarding.Get("room_id", "")) == roomId)
            {
                GdArray bcell = boarding.Get("cell", new GdArray()) as GdArray ?? new GdArray();
                if (bcell.Count >= 2 && V.I64(cell[0]) == V.I64(bcell[0]) && V.I64(cell[1]) == V.I64(bcell[1])) return true;
            }
            string key = CellKey(roomId, cell);
            return key.Length == 0 || occupied.Has(key);
        }

        static void Claim(string roomId, GdArray cell, GdDict occupied)
        {
            string key = CellKey(roomId, cell);
            if (key.Length != 0) occupied[key] = true;
        }

        static GdArray AllFloorCells(GdDict room)
        {
            var output = new GdArray();
            var seen = new HashSet<string>();
            long deck = V.I64(room.Get("deck", 0L));
            foreach (var placement in room.GetArrayOrEmpty("structural_placements"))
            {
                if (!(placement is GdDict p)) continue;
                string placementName = V.Str(p.Get("name", ""));
                if (!GdString.BeginsWith(placementName, "floor_cell")) continue;
                GdArray parsed = LayoutSerializer.ParseSlotCell(placementName);
                if (parsed.Count < 2) continue;
                string key = GdString.FormatInt(V.I64(parsed[0])) + "|" + GdString.FormatInt(V.I64(parsed[1]));
                if (!seen.Add(key)) continue;
                output.Append(GdArray.Of(V.I64(parsed[0]), V.I64(parsed[1]), deck));
            }
            return output;
        }

        static GodotRandom RngForSlot(long seedValue, long roomIndex)
        {
            var rng = new GodotRandom();
            rng.Seed = (seedValue ^ roomIndex ^ 0x51C7110) & 0x7FFFFFFF;
            if (rng.Seed == 0) rng.Seed = 1;
            return rng;
        }

        static GdDict PickEligible(GodotRandom rng, GdArray eligible)
        {
            if (eligible.IsEmpty) return new GdDict();
            return (GdDict)eligible[(int)(rng.Randi() % eligible.Count)];
        }

        static GdArray CellWithDeck(GdArray cell, long deck) => GdArray.Of(V.I64(cell[0]), V.I64(cell[1]), deck);

        static GdDict Slot(GdArray cell, string slotKind, long slotIndex) =>
            new GdDict { { "cell", cell }, { "slot_kind", slotKind }, { "slot_index", slotIndex } };

        GdDict PickSlotCell(GdDict room, string kind, GdDict occupied, GdDict boarding, long seedValue = 0, long roomIndex = 0)
        {
            string rid = V.Str(room.Get("id", ""));
            long deck = V.I64(room.Get("deck", 0L));
            GodotRandom rng = RngForSlot(seedValue, roomIndex);
            GdArray reserved = InteriorCellList(room, "reserved_cells");
            var reservedSet = new HashSet<string>();
            foreach (GdArray cell in reserved) reservedSet.Add(CellKey(rid, cell));
            GdArray centers = InteriorCellList(room, "center_slots");
            GdArray walls = InteriorCellList(room, "wall_slots");
            GdDict picked = kind == "salvage"
                ? PickSalvageSlot(rid, deck, centers, reserved, reservedSet, occupied, boarding, rng)
                : PickLootSlot(rid, deck, centers, walls, reservedSet, occupied, boarding, rng);
            if (!picked.IsEmpty)
            {
                Claim(rid, picked.Get("cell", new GdArray()) as GdArray ?? new GdArray(), occupied);
                return picked;
            }
            GdArray floors = AllFloorCells(room);
            var floorEligible = new GdArray();
            for (int i = 0; i < floors.Count; i++)
            {
                var fallback = (GdArray)floors[i];
                if (IsBlocked(rid, fallback, occupied, boarding)) continue;
                if (kind != "salvage" && reservedSet.Contains(CellKey(rid, fallback))) continue;
                floorEligible.Append(new GdDict { { "cell", fallback }, { "slot_kind", "floor" }, { "slot_index", (long)i }, { "fallback", true } });
            }
            // Compatibility fallback stays the first eligible floor in serialized order; RNG only picks interior slots.
            picked = !floorEligible.IsEmpty ? (GdDict)floorEligible[0] : new GdDict();
            if (!picked.IsEmpty)
            {
                CoreServices.Log.Info("GameplaySliceBuilder slot_fallback room=" + rid + " kind=" + kind);
                Claim(rid, picked.Get("cell", new GdArray()) as GdArray ?? new GdArray(), occupied);
                return picked;
            }
            return new GdDict();
        }

        static GdDict PickLootSlot(string rid, long deck, GdArray centers, GdArray walls, HashSet<string> reservedSet,
            GdDict occupied, GdDict boarding, GodotRandom rng)
        {
            var eligible = new GdArray();
            for (int i = 0; i < centers.Count; i++)
            {
                var cell = (GdArray)centers[i];
                if (IsBlocked(rid, cell, occupied, boarding)) continue;
                if (reservedSet.Contains(CellKey(rid, cell))) continue;
                eligible.Append(Slot(CellWithDeck(cell, deck), "center", i));
            }
            if (!eligible.IsEmpty) return PickEligible(rng, eligible);
            for (int i = 0; i < walls.Count; i++)
            {
                var cell = (GdArray)walls[i];
                if (IsBlocked(rid, cell, occupied, boarding)) continue;
                if (reservedSet.Contains(CellKey(rid, cell))) continue;
                eligible.Append(Slot(CellWithDeck(cell, deck), "wall", i));
            }
            return PickEligible(rng, eligible);
        }

        static GdDict PickSalvageSlot(string rid, long deck, GdArray centers, GdArray reserved, HashSet<string> reservedSet,
            GdDict occupied, GdDict boarding, GodotRandom rng)
        {
            var adjacent = new GdArray();
            for (int i = 0; i < centers.Count; i++)
            {
                var cell = (GdArray)centers[i];
                if (IsBlocked(rid, cell, occupied, boarding)) continue;
                if (reservedSet.Contains(CellKey(rid, cell))) continue;
                if (NeighborsReserved(cell, reservedSet, rid)) adjacent.Append(Slot(CellWithDeck(cell, deck), "center", i));
            }
            if (!adjacent.IsEmpty) return PickEligible(rng, adjacent);
            // Adjacent centers first, then reserved portal cells (REQ-FILL-001).
            var eligible = new GdArray();
            for (int i = 0; i < reserved.Count; i++)
            {
                var cell = (GdArray)reserved[i];
                if (IsBlocked(rid, cell, occupied, boarding)) continue;
                eligible.Append(Slot(CellWithDeck(cell, deck), "reserved", i));
            }
            if (!eligible.IsEmpty) return PickEligible(rng, eligible);
            for (int i = 0; i < centers.Count; i++)
            {
                var cell = (GdArray)centers[i];
                if (IsBlocked(rid, cell, occupied, boarding)) continue;
                if (reservedSet.Contains(CellKey(rid, cell))) continue;
                eligible.Append(Slot(CellWithDeck(cell, deck), "center", i));
            }
            return PickEligible(rng, eligible);
        }

        static bool NeighborsReserved(GdArray cell, HashSet<string> reservedSet, string rid)
        {
            if (cell.Count < 2) return false;
            long x = V.I64(cell[0]);
            long z = V.I64(cell[1]);
            foreach (var n in new[] { GdArray.Of(x + 1, z), GdArray.Of(x - 1, z), GdArray.Of(x, z + 1), GdArray.Of(x, z - 1) })
                if (reservedSet.Contains(CellKey(rid, n))) return true;
            return false;
        }
    }
}
