using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>Behaviour checks from scripts/validation/cell_layout_engine_smoke.gd plus determinism.</summary>
    public class CellLayoutEngineTests
    {
        CollectingLog _log;

        [SetUp]
        public void SetUp()
        {
            _log = new CollectingLog();
            CoreServices.Log = _log;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Log = NullLog.Instance;
        }

        static GdDict SmokeTemplateData()
        {
            GdDict Zone(string id, string role, long count, string hint, string layout, string attach) => new GdDict
            {
                { "id", id }, { "role_pool", GdArray.Of(role) }, { "count", count }, { "position_hint", hint },
                { "deck", 0L }, { "layout", layout }, { "attach_to", attach },
            };
            GdDict Conn(string from, string to, string distribution) =>
                new GdDict { { "from", from }, { "to", to }, { "distribution", distribution } };
            return new GdDict
            {
                { "id", "test" },
                { "description", "Test" },
                {
                    "zones", GdArray.Of(
                        Zone("entry", "airlock", 1, "bow", "single", ""),
                        Zone("spine", "corridor", 3, "center", "linear", "entry"),
                        Zone("side", "cargo", 1, "lateral", "clustered", "spine"),
                        Zone("destination", "reactor", 1, "stern", "single", "spine"))
                },
                {
                    "connections", GdArray.Of(
                        Conn("entry", "spine[0]", "adjacent"),
                        Conn("spine[*]", "spine[*+1]", "adjacent"),
                        Conn("spine[*]", "side", "spread"),
                        Conn("spine[-1]", "destination", "adjacent"))
                },
                { "deck_config", new GdDict { { "max_decks", 1L }, { "vertical_transition_probability", 0.0 } } },
            };
        }

        /// <summary>
        /// A RoomAssigner-shaped plan (RoomAssigner is not ported yet): zones in template order, the first role of
        /// each pool, "&lt;role&gt;_NN" ids, and a role-dependent footprint.
        /// </summary>
        static List<GdDict> BuildPlan(TopologyTemplate template)
        {
            var plan = new List<GdDict>();
            var counter = new Dictionary<string, int>();
            foreach (var zone in template.Zones)
            {
                object countRaw = zone["count"];
                long count = countRaw is GdArray range ? V.I64(range[0]) : V.I64(countRaw);
                string role = V.Str(((GdArray)zone["role_pool"])[0]);
                for (int i = 0; i < count; i++)
                {
                    counter.TryGetValue(role, out int idx);
                    idx += 1;
                    counter[role] = idx;
                    Vec2i fp = role == "corridor" ? new Vec2i(1, 3) : (role == "cargo" ? new Vec2i(3, 2) : new Vec2i(2, 2));
                    plan.Add(new GdDict
                    {
                        { "id", role + "_" + idx.ToString("00") },
                        { "role", role },
                        { "variant", "standard" },
                        { "zone_id", zone["id"] },
                        { "deck", zone["deck"] },
                        { "position_hint", zone["position_hint"] },
                        { "target_cells", (long)(fp.X * fp.Y) },
                        { "footprint", fp },
                    });
                }
            }
            return plan;
        }

        static bool RoomsShareEdge(GdDict rooms, string a, string b)
        {
            var aSet = new HashSet<Vec2i>();
            foreach (var c in ((GdDict)rooms[a]).GetArray("cells")) aSet.Add((Vec2i)c);
            foreach (var c in ((GdDict)rooms[b]).GetArray("cells"))
            {
                var cell = (Vec2i)c;
                foreach (var dir in CellLayoutEngine.ALL_DIRS)
                    if (aSet.Contains(cell + dir)) return true;
            }
            return false;
        }

        [Test]
        public void SmokeTemplate_PlacesEveryRoom_NoOverlap_ConnectedAndChained()
        {
            var template = TopologyTemplate.FromDict(SmokeTemplateData());
            var plan = BuildPlan(template);
            GdDict grid = new CellLayoutEngine().Layout(plan, template, 42);
            var rooms = grid.GetDict("rooms");
            var adjacencies = grid.GetArray("adjacencies");

            Assert.IsEmpty(_log.Errors, string.Join("\n", _log.Errors));
            var occupied = new HashSet<(int, int, long)>();
            foreach (var room in plan)
            {
                string rid = V.Str(room["id"]);
                Assert.IsTrue(rooms.Has(rid), "room not placed: " + rid);
                var data = (GdDict)rooms[rid];
                foreach (var c in data.GetArray("cells"))
                {
                    var cell = (Vec2i)c;
                    Assert.IsTrue(occupied.Add((cell.X, cell.Y, data.GetInt("deck"))), "overlap at " + cell);
                }
            }

            Assert.That(adjacencies.Count, Is.GreaterThan(0));
            var adj = new Dictionary<string, List<string>>();
            foreach (GdDict a in adjacencies)
            {
                string fr = V.Str(a["from_room"]), tr = V.Str(a["to_room"]);
                if (!adj.ContainsKey(fr)) adj[fr] = new List<string>();
                if (!adj.ContainsKey(tr)) adj[tr] = new List<string>();
                adj[fr].Add(tr);
                adj[tr].Add(fr);
                var fc = (Vec2i)a["from_cell"];
                var tc = (Vec2i)a["to_cell"];
                Assert.AreEqual(1, fc.ManhattanTo(tc), "non-cardinal same-deck adjacency " + fr + " -> " + tr);
            }
            var visited = new HashSet<string> { "airlock_01" };
            var queue = new Queue<string>(visited);
            while (queue.Count > 0)
                foreach (string n in adj.TryGetValue(queue.Dequeue(), out var ns) ? ns : new List<string>())
                    if (visited.Add(n)) queue.Enqueue(n);
            Assert.AreEqual(rooms.Count, visited.Count, "connectivity");

            Assert.IsTrue(RoomsShareEdge(rooms, "airlock_01", "corridor_01"), "entry must touch spine[0]");
            Assert.IsTrue(RoomsShareEdge(rooms, "corridor_01", "corridor_02"));
            Assert.IsTrue(RoomsShareEdge(rooms, "corridor_02", "corridor_03"));
            Assert.IsTrue(RoomsShareEdge(rooms, "corridor_03", "reactor_01"), "spine[-1] must touch destination");
        }

        [Test]
        public void SameSeedTwice_ProducesIdenticalLayout()
        {
            var template = TopologyTemplate.FromDict(SmokeTemplateData());
            var plan = BuildPlan(template);
            var engine = new CellLayoutEngine();
            string a = GdJson.Stringify(engine.Layout(plan, template, 42));
            string b = GdJson.Stringify(engine.Layout(plan, template, 42));
            string c = GdJson.Stringify(new CellLayoutEngine().Layout(plan, template, 42));
            Assert.AreEqual(a, b);
            Assert.AreEqual(a, c);
        }

        [Test]
        public void StackedV2_EmitsDeclaredElevatorToUpperHubVerticalLink_Deterministically()
        {
            GdDict data = CatalogRegistry.LoadDict("res://data/procgen/templates/stacked_v2.json");
            Assert.IsNotNull(data);
            var template = TopologyTemplate.FromDict(data);
            var plan = BuildPlan(template);
            var engine = new CellLayoutEngine();
            GdDict grid = engine.Layout(plan, template, 42);
            Assert.IsEmpty(_log.Errors, string.Join("\n", _log.Errors));

            bool linked = false;
            foreach (GdDict a in grid.GetArray("adjacencies"))
            {
                string fr = V.Str(a["from_room"]), tr = V.Str(a["to_room"]);
                if ((fr == "elevator_01" && tr == "hub_02") || (fr == "hub_02" && tr == "elevator_01")) linked = true;
            }
            Assert.IsTrue(linked, "elevator -> upper_hub cross-deck edge not emitted");
            Assert.AreEqual(GdJson.Stringify(grid), GdJson.Stringify(engine.Layout(plan, template, 42)));
        }
    }
}
