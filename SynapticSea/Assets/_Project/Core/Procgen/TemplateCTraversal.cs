// Ported from scripts/procgen/template_c_traversal.gd @ 96ecb2b0
using System.Collections.Generic;
using System.Globalization;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Pure validator for Template C and any other multi-deck layout. Walks <c>layout.vertical_connections</c>
    /// and checks every entry is a real, fully-anchored transition between two decks. Never logs.
    /// </summary>
    public static class TemplateCTraversal
    {
        public const string ERROR_NONE = "";
        public const string ERROR_NO_TRANSITIONS = "no_transitions";
        public const string ERROR_MISSING_ROOM = "missing_room";
        public const string ERROR_DECK_MISMATCH = "deck_mismatch";
        public const string ERROR_CELL_MISSING = "cell_missing";
        public const string ERROR_SELF_TRANSITION = "self_transition";

        /// <summary>
        /// Validates a layout.json-shaped dictionary. Returns
        /// <c>{valid, error_code, error_room, error_transition, transitions_checked, transitions_valid}</c>.
        /// </summary>
        public static GdDict Validate(GdDict layout)
        {
            var result = new GdDict
            {
                { "valid", true },
                { "error_code", ERROR_NONE },
                { "error_room", "" },
                { "error_transition", "" },
                { "transitions_checked", 0L },
                { "transitions_valid", 0L },
            };

            if (!(layout.Get("rooms", new GdArray()) is GdArray roomsData))
            {
                result["valid"] = false;
                result["error_code"] = "no_rooms";
                return result;
            }

            // room_id -> {deck, cells_set}
            var roomLookup = new Dictionary<string, RoomInfo>();
            foreach (var roomVariant in roomsData)
            {
                if (!(roomVariant is GdDict room)) continue;
                string rid = V.Str(room.Get("id", ""));
                if (rid.Length == 0) continue;
                var cellsSet = new HashSet<string>();
                if (room.Get("cells", new GdArray()) is GdArray cellsRaw)
                {
                    foreach (var cell in cellsRaw) cellsSet.Add(CellKey(cell));
                }
                roomLookup[rid] = new RoomInfo { Deck = V.I64(room.Get("deck", 0L)), CellsSet = cellsSet };
            }

            if (!(layout.Get("vertical_connections", new GdArray()) is GdArray verticalRaw) || verticalRaw.IsEmpty)
            {
                // No vertical connections is valid for a single-deck template; transitions_checked stays 0.
                return result;
            }

            foreach (var transitionVariant in verticalRaw)
            {
                if (!(transitionVariant is GdDict transition)) continue;
                result["transitions_checked"] = V.I64(result["transitions_checked"]) + 1;

                string fromRoom = V.Str(transition.Get("from_room", ""));
                string toRoom = V.Str(transition.Get("to_room", ""));
                string transitionId = V.Str(transition.Get("id", fromRoom + "_to_" + toRoom));

                if (fromRoom.Length == 0 || toRoom.Length == 0)
                    return Fail(result, ERROR_MISSING_ROOM, fromRoom.Length == 0 ? fromRoom : toRoom, transitionId);

                if (fromRoom == toRoom)
                    return Fail(result, ERROR_SELF_TRANSITION, fromRoom, transitionId);

                if (!roomLookup.ContainsKey(fromRoom))
                    return Fail(result, ERROR_MISSING_ROOM, fromRoom, transitionId);
                if (!roomLookup.ContainsKey(toRoom))
                    return Fail(result, ERROR_MISSING_ROOM, toRoom, transitionId);

                RoomInfo fromData = roomLookup[fromRoom];
                RoomInfo toData = roomLookup[toRoom];
                if (fromData.Deck == toData.Deck)
                    return Fail(result, ERROR_DECK_MISMATCH, fromRoom, transitionId);

                // from_cell / to_cell — accept Vector2i, [x, y], or [x, y, deck].
                object fromCell = transition.Get("from_cell");
                object toCell = transition.Get("to_cell");

                if (fromCell != null)
                {
                    Vec2i fromXy = CellXy(fromCell);
                    if (fromData.CellsSet.Count > 0 && !fromData.CellsSet.Contains(XyKey(fromXy)))
                        return Fail(result, ERROR_CELL_MISSING, fromRoom, transitionId);
                }

                if (toCell != null)
                {
                    Vec2i toXy = CellXy(toCell);
                    if (toData.CellsSet.Count > 0 && !toData.CellsSet.Contains(XyKey(toXy)))
                        return Fail(result, ERROR_CELL_MISSING, toRoom, transitionId);
                }

                result["transitions_valid"] = V.I64(result["transitions_valid"]) + 1;
            }

            return result;
        }

        static GdDict Fail(GdDict result, string code, string room, string transitionId)
        {
            result["valid"] = false;
            result["error_code"] = code;
            result["error_room"] = room;
            result["error_transition"] = transitionId;
            return result;
        }

        /// <summary>
        /// Critical-path room ids via the same BFS as <c>LayoutSerializer._build_critical_path()</c>
        /// (rooms[0] to rooms[-1] over <c>room_links</c>).
        /// </summary>
        public static List<string> CriticalPath(GdDict layout)
        {
            var rooms = layout.Get("rooms", new GdArray()) as GdArray ?? new GdArray();
            if (rooms.IsEmpty) return new List<string>();
            string entryId = V.Str(V.Dict(rooms[0])?.Get("id", "") ?? "");
            string destId = V.Str(V.Dict(rooms[rooms.Count - 1])?.Get("id", "") ?? "");
            if (entryId.Length == 0 || destId.Length == 0) return new List<string>();

            var adjacencies = layout.Get("room_links", new GdArray()) as GdArray ?? new GdArray();
            var adjMap = new Dictionary<string, List<string>>();
            foreach (var linkVariant in adjacencies)
            {
                if (!(linkVariant is GdDict link)) continue;
                string fr = V.Str(link.Get("from_room", ""));
                string tr = V.Str(link.Get("to_room", ""));
                if (fr.Length == 0 || tr.Length == 0) continue;
                if (!adjMap.ContainsKey(fr)) adjMap[fr] = new List<string>();
                adjMap[fr].Add(tr);
                if (!adjMap.ContainsKey(tr)) adjMap[tr] = new List<string>();
                adjMap[tr].Add(fr);
            }

            var visited = new Dictionary<string, string> { { entryId, "" } };
            var queue = new Queue<string>();
            queue.Enqueue(entryId);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (current == destId) break;
                if (!adjMap.TryGetValue(current, out var neighbors)) continue;
                foreach (string neighbor in neighbors)
                {
                    if (!visited.ContainsKey(neighbor))
                    {
                        visited[neighbor] = current;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            if (!visited.ContainsKey(destId)) return new List<string> { entryId };

            var path = new List<string>();
            string walk = destId;
            while (walk.Length != 0)
            {
                path.Insert(0, walk);
                walk = visited.TryGetValue(walk, out string prev) ? prev : "";
            }
            return path;
        }

        // --- Internal helpers ---

        sealed class RoomInfo
        {
            public long Deck;
            public HashSet<string> CellsSet;
        }

        static string D(long v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Layout cell entry (Vector2i, [x,y], or [x,y,deck]) to a set key.</summary>
        static string CellKey(object cell)
        {
            if (cell is Vec2i v) return XyKey(v);
            if (cell is GdArray arr && arr.Count >= 2) return D(V.I64(arr[0])) + "," + D(V.I64(arr[1]));
            return "";
        }

        /// <summary>Layout cell entry to a Vector2i (deck stripped).</summary>
        static Vec2i CellXy(object cell)
        {
            if (cell is Vec2i v) return v;
            if (cell is GdArray arr && arr.Count >= 2) return new Vec2i(V.I32(arr[0]), V.I32(arr[1]));
            return Vec2i.Zero;
        }

        static string XyKey(Vec2i v) => D(v.X) + "," + D(v.Y);
    }
}
