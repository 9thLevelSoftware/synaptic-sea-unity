using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class SeaGraphTests
    {
        static SeaGraph SmokeGraph()
        {
            var graph = new SeaGraph();
            graph.Configure(new GdDict { { "world_seed", 42L }, { "fuel_per_unit", 0.1 }, { "food_per_unit", 0.05 } });
            var markers = GdArray.Of(
                new GdDict { { "marker_id", "m_a" }, { "position", GdArray.Of(30.0, 0.0, 0.0) }, { "ship_type", "shuttle" } },
                new GdDict { { "marker_id", "m_b" }, { "position", GdArray.Of(80.0, 0.0, 40.0) }, { "ship_type", "freighter" } },
                new GdDict { { "marker_id", "m_c" }, { "position", GdArray.Of(150.0, 0.0, 150.0) }, { "ship_type", "derelict_hauler" } });
            Assert.AreEqual(5, graph.BuildFromMarkers(markers, Vec3.Zero, new Vec3(200f, 0f, 200f)));
            return graph;
        }

        [Test]
        public void SummaryRoundTrips()
        {
            SeaGraph graph = SmokeGraph();
            GdDict summary = graph.GetSummary();
            var restored = new SeaGraph();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsTrue(V.VariantEquals(graph.RouteToExtraction(), restored.RouteToExtraction()));
        }

        [Test]
        public void RouteToExtraction_CostsFuelAndFood()
        {
            SeaGraph graph = SmokeGraph();
            Assert.That(graph.EdgeCount(), Is.GreaterThanOrEqualTo(3));
            Assert.Less(graph.GetNode("m_a").GetInt("biome_band"), graph.GetNode("m_c").GetInt("biome_band"));
            Assert.AreEqual("near_field", SeaGraph.BiomeBandName(0));
            GdDict route = graph.RouteToExtraction();
            Assert.IsTrue(route.GetBool("ok"), route.GetString("reason"));
            GdArray path = route.GetArray("path");
            Assert.AreEqual(SeaGraph.HUB_NODE_ID, path[0]);
            Assert.AreEqual(SeaGraph.EXTRACTION_NODE_ID, path[path.Count - 1]);
            Assert.Greater(route.GetFloat("fuel"), 0.0);
            Assert.Greater(route.GetFloat("food"), 0.0);

            var resources = new GdDict { { "fuel", 1000.0 }, { "food", 1000.0 } };
            Assert.IsTrue(graph.ApplyTravelCost(resources, route).GetBool("ok"));
            Assert.Less(resources.GetFloat("fuel"), 1000.0);
            GdDict fail = graph.ApplyTravelCost(new GdDict { { "fuel", 0.01 }, { "food", 1000.0 } }, route);
            Assert.AreEqual("insufficient_fuel", fail.GetString("reason"));
            Assert.AreEqual("unknown_node", graph.FindRoute("hub", "nowhere").GetString("reason"));
        }

        [Test]
        public void WorldSeedBuild_IsDeterministic()
        {
            var g2 = new SeaGraph();
            var g3 = new SeaGraph();
            Assert.That(g2.BuildFromWorldSeed(99, 1), Is.GreaterThanOrEqualTo(5));
            g3.BuildFromWorldSeed(99, 1);
            Assert.IsTrue(V.VariantEquals(g2.GetSummary(), g3.GetSummary()));
            Assert.IsTrue(V.VariantEquals(g2.RouteToExtraction(), g3.RouteToExtraction()));
        }
    }

    public class SynapticSeaWorldTests
    {
        [Test]
        public void SummaryRoundTrips()
        {
            var world = new SynapticSeaWorld(42, Vec3.Zero);
            List<ShipMarker> near = world.MarkersInRange(250.0);
            world.MarkGenerated(near[0].MarkerId);
            world.SetPlayerPosition(new Vec3(123f, 0f, -45f));
            GdDict summary = world.GetSummary();
            var world2 = new SynapticSeaWorld(0, Vec3.Zero);
            Assert.IsTrue(world2.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, world2.GetSummary()));
            Assert.AreEqual(42, world2.WorldSeed);
            Assert.IsTrue(world2.IsGenerated(near[0].MarkerId));
            Assert.IsFalse(world2.ApplySummary(new GdDict()));
        }

        [Test]
        public void MarkersInRange_AreDistinctSortedAndWithinRadius()
        {
            IMarkerWorld world = new SynapticSeaWorld(42, Vec3.Zero);
            IReadOnlyList<ShipMarker> near = world.MarkersInRange(250.0);
            Assert.IsNotEmpty(near);
            var ids = new HashSet<string>();
            for (int i = 0; i < near.Count; i++)
            {
                Assert.LessOrEqual(near[i].Position.DistanceTo(world.PlayerPosition), 250.0 + 0.001);
                Assert.IsTrue(ids.Add(near[i].MarkerId), "duplicate marker id");
                if (i > 0) Assert.LessOrEqual(near[i - 1].Position.DistanceTo(world.PlayerPosition), near[i].Position.DistanceTo(world.PlayerPosition));
            }
            Assert.That(world.MarkersInRange(500.0).Count, Is.GreaterThanOrEqualTo(near.Count));
            var concrete = (SynapticSeaWorld)world;
            Assert.IsFalse(concrete.IsGenerated(near[0].MarkerId));
            world.MarkGenerated(near[0].MarkerId);
            Assert.IsTrue(concrete.IsGenerated(near[0].MarkerId));
            concrete.UnmarkGenerated(near[0].MarkerId);
            Assert.IsFalse(concrete.IsGenerated(near[0].MarkerId));
        }
    }
}
