using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// RoomGraph / ShipBlueprint / RoomVariantSelector / TemplateCTraversal / StructuralEdgePlan /
    /// FireCompartmentResolver, following room_graph_smoke, ship_blueprint_smoke, room_variant_selector_smoke and
    /// template_c_traversal_smoke.
    /// </summary>
    public class ProcgenDataClassTests
    {
        [Test]
        public void RoomGraph_RoundTripsAndChecksConnectivity()
        {
            var g = new RoomGraph();
            g.AddRoom("airlock_01", "airlock");
            g.AddRoom("corridor_01", "corridor");
            g.AddRoom("bridge_01", "bridge", 1);
            g.AddLink("airlock_01", "corridor_01");
            Assert.IsFalse(g.IsFullyConnected());
            g.AddLink("corridor_01", "bridge_01", "ladder");
            Assert.IsTrue(g.IsFullyConnected());
            CollectionAssert.AreEqual(new[] { "airlock_01", "bridge_01" }, g.GetConnectedRooms("corridor_01"));

            var copy = RoomGraph.FromDict(g.ToDict());
            Assert.IsTrue(V.VariantEquals(g.ToDict(), copy.ToDict()));
            Assert.AreEqual(1L, copy.GetRoom("bridge_01")["deck"]);
            Assert.IsTrue(copy.GetRoom("missing").IsEmpty);
        }

        [Test]
        public void ShipBlueprint_RoundTripsAndDerivesRange()
        {
            var bp = new ShipBlueprint(ShipBlueprint.Size.Small, ShipBlueprint.Condition.Wrecked, 1234);
            Assert.AreEqual(new Vec2i(4, 8), bp.RoomCountRange);
            Assert.AreEqual(0.2, bp.GetSystemOnlineChance());
            var copy = ShipBlueprint.FromDict(GdJson.ParseDict(GdJson.Stringify(bp.ToDict())));
            Assert.IsTrue(V.VariantEquals(bp.ToDict(), copy.ToDict()));
            Assert.AreEqual(new Vec2i(2, 4), ShipBlueprint.FromDict(new GdDict { { "size", 0L } }).RoomCountRange);
        }

        [Test]
        public void RoomVariantSelector_IsDeterministicAndSpreads()
        {
            var selector = new RoomVariantSelector();
            string a = selector.Pick("airlock", 0, 42);
            Assert.AreEqual(a, selector.Pick("airlock", 0, 42));
            CollectionAssert.Contains(selector.VariantsForRole("airlock"), a);

            var byIndex = new HashSet<string>();
            for (int i = 0; i < 8; i++) byIndex.Add(selector.Pick("corridor", i, 12345));
            Assert.That(byIndex.Count, Is.GreaterThanOrEqualTo(2));
            var bySeed = new HashSet<string>();
            for (int s = 0; s < 10; s++) bySeed.Add(selector.Pick("corridor", 0, s * 137));
            Assert.That(bySeed.Count, Is.GreaterThanOrEqualTo(2));

            Assert.AreEqual("standard_totally_made_up_role", selector.Pick("totally_made_up_role", 0, 42));
            Assert.AreEqual("standard", selector.Pick("", 0, 42));
            Assert.AreEqual(selector.Pick("cargo", 3, 99, "dead_fleet"), selector.Pick("cargo", 3, 99, "dead_fleet"));
            Assert.AreEqual("scorch", selector.EffectsFor("burned_out")["dressing"]);
            Assert.IsTrue(selector.EffectsFor("standard").IsEmpty);
            Assert.AreEqual(10, selector.KnownDressingIds().Count);
            Assert.AreEqual("biomatter", selector.KnownDressingIds()[0]);
        }

        static GdDict StackedLayout()
        {
            return new GdDict
            {
                {
                    "rooms", GdArray.Of(
                        new GdDict { { "id", "airlock_01" }, { "room_role", "airlock" }, { "deck", 0L }, { "cells", GdArray.Of(new Vec2i(0, 0)) } },
                        new GdDict { { "id", "ramp_room" }, { "room_role", "ramp" }, { "deck", 0L }, { "cells", GdArray.Of(new Vec2i(1, 0)) } },
                        new GdDict { { "id", "upper_room" }, { "room_role", "corridor" }, { "deck", 1L }, { "cells", GdArray.Of(new Vec2i(1, 0)) } },
                        new GdDict { { "id", "bridge_room" }, { "room_role", "bridge" }, { "deck", 1L }, { "cells", GdArray.Of(new Vec2i(2, 0)) } })
                },
                {
                    "room_links", GdArray.Of(
                        new GdDict { { "from_room", "airlock_01" }, { "to_room", "ramp_room" } },
                        new GdDict { { "from_room", "ramp_room" }, { "to_room", "upper_room" } },
                        new GdDict { { "from_room", "upper_room" }, { "to_room", "bridge_room" } })
                },
                {
                    "vertical_connections", GdArray.Of(new GdDict
                    {
                        { "id", "ramp_room_to_upper_room" }, { "from_room", "ramp_room" }, { "to_room", "upper_room" },
                        { "from_cell", new Vec2i(1, 0) }, { "to_cell", GdArray.Of(1.0, 0.0, 1.0) },
                    })
                },
            };
        }

        static GdDict WithTransition(string from, string to, object fromCell)
        {
            var layout = StackedLayout();
            var t = (GdDict)((GdArray)layout["vertical_connections"])[0];
            t["from_room"] = from;
            t["to_room"] = to;
            t["from_cell"] = fromCell;
            return layout;
        }

        [Test]
        public void TemplateCTraversal_ValidatesAndReportsStableErrorCodes()
        {
            GdDict ok = TemplateCTraversal.Validate(StackedLayout());
            Assert.IsTrue(V.Bool(ok["valid"]), V.Str(ok["error_code"]));
            Assert.AreEqual(1L, ok["transitions_checked"]);
            Assert.AreEqual(1L, ok["transitions_valid"]);

            Assert.AreEqual(TemplateCTraversal.ERROR_MISSING_ROOM,
                TemplateCTraversal.Validate(WithTransition("ghost", "upper_room", new Vec2i(1, 0)))["error_code"]);
            Assert.AreEqual(TemplateCTraversal.ERROR_DECK_MISMATCH,
                TemplateCTraversal.Validate(WithTransition("airlock_01", "ramp_room", new Vec2i(0, 0)))["error_code"]);
            Assert.AreEqual(TemplateCTraversal.ERROR_CELL_MISSING,
                TemplateCTraversal.Validate(WithTransition("ramp_room", "upper_room", new Vec2i(9, 9)))["error_code"]);
            GdDict self = TemplateCTraversal.Validate(WithTransition("ramp_room", "ramp_room", new Vec2i(1, 0)));
            Assert.AreEqual(TemplateCTraversal.ERROR_SELF_TRANSITION, self["error_code"]);
            Assert.AreEqual("ramp_room_to_upper_room", self["error_transition"]);

            CollectionAssert.AreEqual(
                new[] { "airlock_01", "ramp_room", "upper_room", "bridge_room" },
                TemplateCTraversal.CriticalPath(StackedLayout()));
        }

        [Test]
        public void StructuralEdgePlan_EdgeKeysAreReciprocal()
        {
            var cell = new Vec2i(3, -2);
            Assert.AreEqual(StructuralEdgePlan.EdgeKey(1, cell, "east"), StructuralEdgePlan.EdgeKey(1, new Vec2i(4, -2), "west"));
            Assert.AreEqual(StructuralEdgePlan.EdgeKey(1, cell, "north"), StructuralEdgePlan.EdgeKey(1, new Vec2i(3, -3), "south"));
            Assert.AreEqual("1|h|-3|3", StructuralEdgePlan.EdgeKey(1, cell, "north"));
            Assert.AreEqual("1|v|-2|3", StructuralEdgePlan.EdgeKey(1, cell, "east"));
            Assert.AreEqual("2|3|-2", StructuralEdgePlan.CellKey(2, cell));

            string key = StructuralEdgePlan.EdgeKey(1, cell, "south");
            GdDict edge = StructuralEdgePlan.MakeEdge(key, "DOOR", "a", "b", cell, "south");
            Assert.AreEqual("bulkhead_portal_2x1", edge["module_id"]);
            Assert.AreEqual(new Vec3(12f, 4f, -6f), edge["position"]);
            Assert.AreEqual(0.0, edge["yaw_degrees"]);
            Assert.AreEqual("north", edge["opposite_direction"]);
            Assert.AreEqual(1L, edge["deck"]);
            Assert.AreEqual(new Vec2i(3, -1), ((GdArray)edge["source_cells"])[1]);
            Assert.AreEqual(new Vec3(12f, 8f, -8f), StructuralEdgePlan.MakeCell(2, cell, "a")["position"]);
        }

        [Test]
        public void FireCompartmentResolver_MapsRolesRoomsAndZones()
        {
            Assert.AreEqual("engineering", FireCompartmentResolver.FromToken(" reactor\t"));
            Assert.AreEqual("cargo", FireCompartmentResolver.FromToken("cargo"));
            Assert.AreEqual("", FireCompartmentResolver.FromToken("medical"));
            var sources = GdArray.Of(StackedLayout());
            Assert.AreEqual("bridge", FireCompartmentResolver.FromRoomId("bridge_room", sources));
            Assert.AreEqual("bridge", FireCompartmentResolver.FromZone(
                new GdDict { { "compartment_id", "nope" }, { "to_room", "bridge_room" } }, sources));
        }
    }
}
