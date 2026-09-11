using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ShipNavGraphTests
    {
        const string Golden001 = "res://data/procgen/golden/coherent_ship_001/layout.json";
        const string Golden002 = "res://data/procgen/golden/coherent_ship_002/layout.json";

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
        }

        static GdDict Occupancy(GdDict layout) =>
            layout.GetDictOrEmpty("structural_plan").GetDictOrEmpty("occupancy");

        static GdDict FindById(GdDict layout, string listKey, string id)
        {
            foreach (object item in layout.GetArrayOrEmpty(listKey))
            {
                if (item is GdDict d && V.Str(d.Get("id", "")) == id) return d;
            }
            return new GdDict();
        }

        [Test]
        public void GoldenLayoutBuilds_ShortcutIsStandingBlocked_SummaryShape()
        {
            GdDict layout = CatalogRegistry.LoadDict(Golden001);
            Assert.IsNotNull(layout, "golden layout missing");
            var graph = new ShipNavGraph();
            long n = graph.BuildFromLayout(layout);
            Assert.That(n, Is.GreaterThanOrEqualTo(8));
            Assert.That(graph.EdgeCount(), Is.GreaterThanOrEqualTo(4));

            GdDict summary = graph.GetSummary();
            Assert.AreEqual(n, summary["node_count"]);
            Assert.AreEqual(false, summary["dirty"]);

            GdDict occupancy = Occupancy(layout);
            GdDict shortcut = FindById(layout, "blocked_links", "spine_to_reactor_blocked_shortcut");
            Assert.IsFalse(shortcut.IsEmpty);
            string fromKey = graph.NodeKeyForCell(shortcut.Get("from_cell"), 1, occupancy);
            string toKey = graph.NodeKeyForCell(shortcut.Get("to_cell"), 1, occupancy);
            Assert.IsNotEmpty(toKey);
            if (fromKey.Length != 0 && fromKey != toKey)
                Assert.That(graph.EdgeCost(fromKey, toKey), Is.GreaterThanOrEqualTo(ShipNavGraph.BLOCKED_COST));
        }

        [Test]
        public void BlockedLinksOverlayStoredDoor()
        {
            var occupancy = new GdDict
            {
                { "0|0|0", new GdDict { { "cell_key", "0|0|0" }, { "deck", 0L }, { "cell", GdArray.Of(0L, 0L) }, { "room_id", "a" }, { "position", GdArray.Of(0.0, 0.0, 0.0) } } },
                { "0|1|0", new GdDict { { "cell_key", "0|1|0" }, { "deck", 0L }, { "cell", GdArray.Of(1L, 0L) }, { "room_id", "b" }, { "position", GdArray.Of(4.0, 0.0, 0.0) } } },
            };
            var layout = new GdDict
            {
                { "cell_size", 4.0 },
                { "deck_height", 4.0 },
                {
                    "rooms", GdArray.Of(
                        new GdDict { { "id", "a" }, { "deck", 0L } },
                        new GdDict { { "id", "b" }, { "deck", 0L } })
                },
                {
                    "blocked_links", GdArray.Of(new GdDict
                    {
                        { "id", "synthetic_blocked_door" }, { "from_room", "a" }, { "to_room", "b" },
                        { "from_cell", GdArray.Of(0L, 0L, 0L) }, { "to_cell", GdArray.Of(1L, 0L, 0L) },
                    })
                },
                { "vertical_connections", new GdArray() },
                {
                    "structural_plan", new GdDict
                    {
                        { "occupancy", occupancy },
                        {
                            "edges", new GdDict
                            {
                                {
                                    "0|v|0|0", new GdDict
                                    {
                                        { "kind", "DOOR" }, { "state", "DOOR" }, { "deck", 0L }, { "cell", GdArray.Of(0L, 0L) },
                                        { "direction", "east" }, { "source_cells", GdArray.Of(GdArray.Of(0L, 0L, 0L), GdArray.Of(1L, 0L, 0L)) },
                                    }
                                },
                            }
                        },
                    }
                },
            };
            var graph = new ShipNavGraph();
            Assert.AreEqual(2, graph.BuildFromLayout(layout));
            string a = graph.NodeKeyForCell(GdArray.Of(0L, 0L, 0L), 0, occupancy);
            string b = graph.NodeKeyForCell(GdArray.Of(1L, 0L, 0L), 0, occupancy);
            Assert.AreEqual("0:0:0", a);
            Assert.AreEqual("1:0:0", b);
            Assert.IsTrue(graph.HasBaseEdge(a, b));
            Assert.That(graph.BaseEdgeCost(a, b), Is.GreaterThanOrEqualTo(ShipNavGraph.BLOCKED_COST));
            Assert.IsTrue(graph.Neighbors(a).IsEmpty);

            graph.SetEdgeBlocked(a, b, false);
            Assert.AreEqual(ShipNavGraph.BLOCKED_COST, graph.EdgeCost(a, b));
            Assert.AreEqual(a, graph.NearestNode(new Vec3(1.0, 0.0, 0.5)));
        }

        [Test]
        public void GoldenLogicalPortalConnectsArcSide()
        {
            GdDict layout = CatalogRegistry.LoadDict(Golden002);
            Assert.IsNotNull(layout, "golden coherent_ship_002 missing");
            GdDict occupancy = Occupancy(layout);
            var graph = new ShipNavGraph();
            Assert.That(graph.BuildFromLayout(layout), Is.GreaterThanOrEqualTo(2));
            GdDict portal = FindById(layout, "portals", "corridor_to_arc_side");
            Assert.IsFalse(portal.IsEmpty);
            string fromKey = graph.NodeKeyForCell(portal.Get("logical_from_cell", portal.Get("from_cell")), 0, occupancy);
            string toKey = graph.NodeKeyForCell(portal.Get("logical_to_cell", portal.Get("to_cell")), 0, occupancy);
            Assert.IsNotEmpty(fromKey);
            Assert.IsNotEmpty(toKey);
            Assert.IsTrue(graph.HasBaseEdge(fromKey, toKey));
            Assert.That(graph.EdgeCost(fromKey, toKey), Is.LessThan(ShipNavGraph.BLOCKED_COST));
        }

        [Test]
        public void FloorPlacementFallbackConnectsOrthogonalNeighbors()
        {
            var layout = new GdDict
            {
                {
                    "rooms", GdArray.Of(new GdDict
                    {
                        { "id", "hall" },
                        {
                            "structural_placements", GdArray.Of(
                                new GdDict { { "module_id", "floor_plate" }, { "world_position", GdArray.Of(0.0, 0.0, 0.0) } },
                                new GdDict { { "module_id", "corridor_floor_a" }, { "world_position", GdArray.Of(4.1, 0.0, 0.0) } },
                                new GdDict { { "module_id", "wall_panel" }, { "world_position", GdArray.Of(8.0, 0.0, 0.0) } },
                                new GdDict { { "module_id", "floor_plate" }, { "position", GdArray.Of(4.0, 4.0, 0.0) } })
                        },
                    })
                },
            };
            var graph = new ShipNavGraph();
            Assert.AreEqual(3, graph.BuildFromLayout(layout));
            Assert.AreEqual(1.0, graph.EdgeCost("0:0:0", "1:0:0"));
            Assert.AreEqual(1.25, graph.EdgeCost("1:0:0", "1:1:0"));
            Assert.AreEqual(1.5, graph.EdgeCost("0:0:0", "1:1:0"));
            graph.ApplyFireCosts(new GdDict { { "hall", 2.0 } });
            Assert.AreEqual(12.0, graph.EdgeCost("0:0:0", "1:0:0"));
            graph.ResetDynamicCosts();
            Assert.AreEqual(1.0, graph.EdgeCost("0:0:0", "1:0:0"));
        }
    }
}
