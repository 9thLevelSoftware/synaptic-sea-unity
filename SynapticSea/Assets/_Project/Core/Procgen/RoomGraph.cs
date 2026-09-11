// Ported from scripts/procgen/room_graph.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Ship room topology as an undirected graph. Room dict: <c>{ "id", "role", "deck" }</c>;
    /// link dict: <c>{ "from_room", "to_room", "type" }</c>. No scene nodes.
    /// </summary>
    public sealed class RoomGraph
    {
        /// <summary><c>Array[Dictionary]</c>.</summary>
        public List<GdDict> Rooms = new List<GdDict>();

        /// <summary><c>Array[Dictionary]</c>.</summary>
        public List<GdDict> Links = new List<GdDict>();

        public void AddRoom(string roomId, string role, long deck = 0)
        {
            Rooms.Add(new GdDict { { "id", roomId }, { "role", role }, { "deck", deck } });
        }

        public void AddLink(string fromRoom, string toRoom, string linkType = "door")
        {
            Links.Add(new GdDict { { "from_room", fromRoom }, { "to_room", toRoom }, { "type", linkType } });
        }

        /// <summary>The room dict (shared reference) for <paramref name="roomId"/>, or a new empty dictionary.</summary>
        public GdDict GetRoom(string roomId)
        {
            foreach (var room in Rooms)
                if (V.VariantEquals(room["id"], roomId)) return room;
            return new GdDict();
        }

        /// <summary>Ids of every room directly linked to <paramref name="roomId"/>, regardless of direction.</summary>
        public List<string> GetConnectedRooms(string roomId)
        {
            var connected = new List<string>();
            foreach (var link in Links)
            {
                if (V.VariantEquals(link["from_room"], roomId)) connected.Add(V.Str(link["to_room"]));
                else if (V.VariantEquals(link["to_room"], roomId)) connected.Add(V.Str(link["from_room"]));
            }
            return connected;
        }

        /// <summary>True iff every room is reachable from the first room. The empty graph is connected.</summary>
        public bool IsFullyConnected()
        {
            if (Rooms.Count == 0) return true;

            var visited = new HashSet<string>();
            var queue = new List<string> { V.Str(Rooms[0]["id"]) };
            visited.Add(V.Str(Rooms[0]["id"]));

            while (queue.Count > 0)
            {
                string current = queue[0];
                queue.RemoveAt(0);
                foreach (string connectedId in GetConnectedRooms(current))
                {
                    if (!visited.Contains(connectedId))
                    {
                        visited.Add(connectedId);
                        queue.Add(connectedId);
                    }
                }
            }

            return visited.Count == Rooms.Count;
        }

        public List<GdDict> GetRoomsByRole(string role)
        {
            var result = new List<GdDict>();
            foreach (var room in Rooms)
                if (V.VariantEquals(room["role"], role)) result.Add(room);
            return result;
        }

        /// <summary>
        /// <c>{"rooms": rooms, "links": links}</c>. GDScript stores the arrays by reference; here new arrays are
        /// built around the same room/link dictionaries.
        /// </summary>
        public GdDict ToDict()
        {
            return new GdDict { { "rooms", new GdArray(Rooms) }, { "links", new GdArray(Links) } };
        }

        public static RoomGraph FromDict(GdDict data)
        {
            var graph = new RoomGraph();
            if (data.Get("rooms", new GdArray()) is GdArray rawRooms)
            {
                foreach (var roomVariant in rawRooms)
                {
                    if (!(roomVariant is GdDict roomData)) continue;
                    string rid = V.Str(roomData.Get("id", ""));
                    string rrole = V.Str(roomData.Get("role", ""));
                    long rdeck = V.I64(roomData.Get("deck", 0L));
                    if (rid.Length == 0) continue;
                    graph.AddRoom(rid, rrole, rdeck);
                }
            }
            if (data.Get("links", new GdArray()) is GdArray rawLinks)
            {
                foreach (var linkVariant in rawLinks)
                {
                    if (!(linkVariant is GdDict linkData)) continue;
                    string fr = V.Str(linkData.Get("from_room", ""));
                    string tr = V.Str(linkData.Get("to_room", ""));
                    string lt = V.Str(linkData.Get("type", "door"));
                    if (fr.Length == 0 || tr.Length == 0) continue;
                    graph.AddLink(fr, tr, lt);
                }
            }
            return graph;
        }
    }
}
