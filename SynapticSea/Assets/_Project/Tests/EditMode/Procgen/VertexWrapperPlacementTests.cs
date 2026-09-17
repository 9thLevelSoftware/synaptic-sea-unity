using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// The port deviation in <see cref="VertexWrapperPlacement"/>: the multi-wing wrappers are built at the cell
    /// centre their wings belong to, not at the edge midpoint Godot recorded. The invariants below are what keep
    /// the geometry sound — every solid edge walled exactly once, no wall in open space, no opening sealed — which
    /// is what the NavMesh reads. Checked against every Godot capture layout plus the shipped ship data.
    /// </summary>
    public class VertexWrapperPlacementTests
    {
        static readonly string[] CaptureLayouts =
        {
            "godot/procgen/layout_s17_medium_pristine.json",
            "godot/procgen/layout_s42_abyssal_synaptic_sea_deep_dive.json",
            "godot/procgen/layout_s42_abyssal_synaptic_sea_hardened.json",
            "godot/procgen/layout_s42_abyssal_synaptic_sea_standard.json",
            "godot/procgen/layout_s42_breach_field_deep_dive.json",
            "godot/procgen/layout_s42_dead_fleet_standard.json",
            "godot/procgen/layout_s42_small_wrecked_ext.json",
            "godot/procgen/layout_s777_small_wrecked_ext.json",
            "godot/procgen/layout_s999_small_wrecked_ext.json",
            "godot/procgen/layout_s7777_small_wrecked_ext.json",
        };

        [SetUp]
        public void SetUp() => CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);

        static GdDict PlanOf(string fixturePath)
        {
            Fixtures.Require(fixturePath);
            GdDict plan = Fixtures.ReadDict(fixturePath).GetDict("structural_plan");
            Assert.IsNotNull(plan, fixturePath + " has no structural_plan");
            return plan;
        }

        /// <summary>Godot's own captures carry the misplaced wrappers, which is why the deviation exists.</summary>
        [Test]
        public void CaptureLayoutsCarryVertexWrappersOnEdgeRecords()
        {
            GdDict plan = PlanOf(CaptureLayouts[0]);
            int vertexWrappers = 0;
            foreach (object p in plan.GetArrayOrEmpty("placements"))
                if (p is GdDict placement && VertexWrapperPlacement.IsVertexModule(placement.GetString("module_id")))
                    vertexWrappers++;
            Assert.Greater(vertexWrappers, 0, "capture has no vertex wrappers, so the deviation is untested");
        }

        [Test]
        public void EveryWingLandsOnASolidEdgeAndNoEdgeIsWalledTwice([ValueSource(nameof(CaptureLayouts))] string fixturePath)
        {
            GdDict plan = PlanOf(fixturePath);
            AssertPlanIsSound(plan, fixturePath);
        }

        [Test]
        public void ShippedShipDataResolvesSoundly()
        {
            foreach (string path in new[]
                     {
                         "res://data/procgen/golden/coherent_ship_001/layout.json",
                         "res://data/procgen/smoke/seed_000017/layout.json",
                     })
            {
                string text = CoreServices.Resources.ReadText(path);
                Assert.IsNotNull(text, "missing ship data: " + path);
                GdDict layout = GdJson.ParseString(text) as GdDict;
                Assert.IsNotNull(layout, path + " is not a JSON object");
                // The generator compiles the plan when the layout ships without a validated one.
                GdDict plan = layout.GetDict("structural_plan");
                if (plan == null || plan.IsEmpty || !layout.GetBool("structural_plan_validated"))
                    plan = new StructuralEdgeCompiler().Compile(layout);
                AssertPlanIsSound(plan, path);
            }
        }

        /// <summary>
        /// Walks the plan the way the scene builder does and checks the geometry it would produce: one wrapper per
        /// solid edge, every wrapper standing on an edge the plan declares solid.
        /// </summary>
        static void AssertPlanIsSound(GdDict plan, string label)
        {
            VertexWrapperPlacement.Result resolved = VertexWrapperPlacement.Resolve(plan);
            GdDict edges = plan.GetDictOrEmpty("edges");

            // Nothing is covered unless it is a solid, non-portal edge: a covered edge builds no wrapper of its own.
            foreach (string coveredKey in resolved.Covered)
            {
                GdDict edge = edges.Get(coveredKey, null) as GdDict;
                Assert.IsNotNull(edge, $"{label}: covered edge {coveredKey} is not in the plan");
                Assert.AreEqual("SOLID", V.Str(edge.Get("kind", edge.Get("state", ""))), $"{label}: covered edge {coveredKey} is not solid");
                Assert.IsFalse(V.Bool(edge.Get("portal", false)), $"{label}: covered edge {coveredKey} is a portal");
            }

            // Count what the builder would instantiate per edge: its own wrapper, or a neighbour's wing.
            var walledBy = new Dictionary<string, string>();
            foreach (object placementVariant in plan.GetArrayOrEmpty("placements"))
            {
                if (!(placementVariant is GdDict placement)) continue;
                string edgeKey = placement.GetString("edge_key");
                if (resolved.Covered.Contains(edgeKey)) continue; // skipped: a wing walls it
                Claim(walledBy, edgeKey, edgeKey, label);
                if (!resolved.Poses.TryGetValue(edgeKey, out VertexWrapperPlacement.Pose pose)) continue;

                // A relocated wrapper must sit on a cell centre, and cover only solid edges of that cell.
                Assert.IsTrue(VertexWrapperPlacement.IsVertexModule(placement.GetString("module_id")),
                    $"{label}: {edgeKey} was relocated but is not a vertex wrapper");
                foreach (string wingKey in WingEdges(plan, edgeKey, pose, placement))
                {
                    GdDict wing = edges.Get(wingKey, null) as GdDict;
                    Assert.IsNotNull(wing, $"{label}: wrapper {edgeKey} has a wing on missing edge {wingKey}");
                    Assert.AreEqual("SOLID", V.Str(wing.Get("kind", wing.Get("state", ""))),
                        $"{label}: wrapper {edgeKey} walls non-solid edge {wingKey}");
                    if (wingKey != edgeKey) Claim(walledBy, wingKey, edgeKey, label);
                }
            }

            // Every edge that needs geometry got exactly one wrapper (Claim rejects a second).
            foreach (var kv in edges)
            {
                string edgeKey = V.Str(kv.Key);
                if (!(kv.Value is GdDict edge)) continue;
                string kind = V.Str(edge.Get("kind", edge.Get("state", "")));
                if (kind == "OPEN" || !V.Bool(edge.Get("wrapper_required", edge.Get("placement_required", true)))) continue;
                Assert.IsTrue(walledBy.ContainsKey(edgeKey), $"{label}: edge {edgeKey} ({kind}) ends up with no wrapper");
            }
        }

        static void Claim(Dictionary<string, string> walledBy, string edgeKey, string by, string label)
        {
            Assert.IsFalse(walledBy.ContainsKey(edgeKey),
                $"{label}: edge {edgeKey} is walled twice (by {(walledBy.TryGetValue(edgeKey, out string first) ? first : "?")} and {by})");
            walledBy[edgeKey] = by;
        }

        /// <summary>The edges a relocated wrapper's wings cover, derived from its pose the way the wrapper is built.</summary>
        static List<string> WingEdges(GdDict plan, string edgeKey, VertexWrapperPlacement.Pose pose, GdDict placement)
        {
            long deck = V.I64(placement.Get("deck", 0L));
            // The pose is a cell centre, so the cell comes straight back out of it.
            var cell = new Vec2i(
                (int)System.Math.Round(pose.Position.X / StructuralEdgeCompiler.CELL_SIZE),
                (int)System.Math.Round(pose.Position.Z / StructuralEdgeCompiler.CELL_SIZE));
            Assert.AreEqual(StructuralEdgeCompiler.CellWorldPosition(deck, cell), pose.Position,
                $"wrapper {edgeKey} is not on a cell centre");

            string[] wings = WingDirections(pose.YawDegrees);
            bool tJunction = placement.GetString("module_id") == StructuralEdgeCompiler.WALL_T_JUNCTION_MODULE;
            var keys = new List<string>();
            for (int i = 0; i < (tJunction ? 3 : 2); i++) keys.Add(StructuralEdgeCompiler.EdgeKey(deck, cell, wings[i]));
            CollectionAssert.Contains(keys, edgeKey, $"wrapper {edgeKey} does not wall its own edge");
            return keys;
        }

        static string[] WingDirections(double yaw)
        {
            if (yaw == 0.0) return new[] { "north", "east", "west" };
            if (yaw == 90.0) return new[] { "west", "north", "south" };
            if (yaw == 180.0) return new[] { "south", "west", "east" };
            if (yaw == 270.0) return new[] { "east", "south", "north" };
            Assert.Fail("wrapper yaw is not a cardinal: " + yaw);
            return null;
        }
    }
}
