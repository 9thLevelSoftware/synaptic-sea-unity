using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>Scene-root stand-in: a positioned root with room nodes under ShipStructure.</summary>
    internal sealed class FakeShipSceneRoot : IShipSceneRoot, IShipInteriorView
    {
        public readonly List<Vec3> Rooms = new List<Vec3>();
        public bool IsValid { get; set; } = true;
        public bool IsInsideTree { get; set; }
        public Xform3 Transform { get; set; } = Xform3.Identity;
        public Xform3 GlobalTransform => Transform;
        public IReadOnlyList<Vec3> StructureRoomLocalPositions() => Rooms;
    }

    public class ShipInstanceTests
    {
        [SetUp]
        public void SetUp()
        {
            CatalogRegistry.Clear();
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        static ShipInstance MakeShip()
        {
            var bp = new ShipBlueprint(ShipBlueprint.Size.Small, ShipBlueprint.Condition.Damaged, 4242);
            var mgr = new ShipSystemsManager();
            mgr.Configure(mgr.LoadDefinitions(), bp.ShipCondition, bp.SeedValue);
            return ShipInstance.Create("ship_test", "3:1:2", bp, mgr, null);
        }

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            ShipInstance inst = MakeShip();
            Assert.IsNull(inst.ParentShip);
            Assert.AreEqual(0, inst.DockedShips.Count);
            Assert.IsTrue(inst.DockingPorts.IsEmpty);
            Assert.IsFalse(inst.GetSummary().Has("inventory"), "empty hold omitted");
            Assert.IsFalse(inst.GetSummary().Has("carts"), "no carts omitted");

            GdDict bare = inst.GetSummary();
            var rebuilt = ShipInstance.Create("", "", new ShipBlueprint(), new ShipSystemsManager(), null);
            Assert.IsTrue(rebuilt.ApplySummary(bare));
            Assert.AreEqual((long)ShipBlueprint.Size.Small, rebuilt.Blueprint.ShipSize);
            Assert.AreEqual(4242L, rebuilt.Blueprint.SeedValue);

            ShipInventory hold = inst.GetInventory();
            hold.AddItem("scrap_metal", 2);
            var cart = CartState.Create("cart_a", 200.0);
            cart.GetHold().AddItem("scrap_metal", 3);
            inst.GetCarts().Add(cart);
            inst.LootedContainerIds = GdArray.Of("crate_3", "locker_1");
            inst.PendingCorpseLoot.Add(new GdDict { { "container_id", "corpse_t1" }, { "position", GdArray.Of(1.0, 0.0, 2.0) } });
            DerelictObjectiveController controller = inst.GetObjectiveController();
            controller.Configure(GdArray.Of(
                new GdDict { { "id", "obj_salvage_a" }, { "sequence", 1L }, { "type", "salvage" }, { "kind", "single" }, { "room_id", "a" } },
                new GdDict { { "id", "obj_reach_goal" }, { "sequence", 2L }, { "type", "interact" }, { "kind", "single" }, { "room_id", "b" } }));
            controller.Complete(1);
            controller.Complete(2);
            inst.FireSeeded = true;
            inst.GetFire();
            inst.LastSimTime = 12.5;

            GdDict summary = inst.GetSummary();
            foreach (string key in new[] { "objective", "looted_containers", "pending_corpse_loot", "inventory", "carts", "fire", "fire_seeded", "last_sim_time" })
                Assert.IsTrue(summary.Has(key), key);
            var clone = ShipInstance.Create("x", "y", null, null, null);
            Assert.IsTrue(clone.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, clone.GetSummary()), GdJson.Stringify(clone.GetSummary()));
            Assert.IsTrue(clone.GetObjectiveController().IsCleared());
            Assert.AreEqual(3L, clone.GetCarts()[0].GetHold().GetQuantity("scrap_metal"));
            Assert.IsFalse(clone.ApplySummary(null));
            Assert.IsFalse(clone.ApplySummary(new GdDict()));
        }

        [Test]
        public void InteriorAabb_MergesRoomBoxesAndFallsBackToOrigin()
        {
            ShipInstance inst = MakeShip();
            Assert.AreEqual(new Aabb3(Vec3.Zero, Vec3.Zero), inst.InteriorAabb(), "no scene root");

            var root = new FakeShipSceneRoot { Transform = new Xform3(Basis3.Identity, new Vec3(10f, 0f, 0f)) };
            inst.SceneRoot = root;
            Assert.AreEqual(new Aabb3(new Vec3(10f, 0f, 0f), Vec3.Zero), inst.InteriorAabb(), "unbuilt: zero box at root origin");

            root.Rooms.Add(new Vec3(0f, 0f, 0f));
            root.Rooms.Add(new Vec3(8f, 0f, 4f));
            Aabb3 box = inst.InteriorAabb();
            Assert.AreEqual(new Vec3(6f, -3f, -4f), box.Position);
            Assert.AreEqual(new Vec3(16f, 6f, 12f), box.Size);

            // A quarter-turn yaw swaps the X/Z extents.
            root.IsInsideTree = true;
            root.Transform = new Xform3(Basis3.FromAxisAngle(Vec3.Up, (float)(System.Math.PI / 2.0)), Vec3.Zero);
            Aabb3 turned = inst.InteriorAabb();
            Assert.AreEqual(12f, turned.Size.X, 1e-4);
            Assert.AreEqual(16f, turned.Size.Z, 1e-4);
        }

        [Test]
        public void DockingManager_WritesDockFields()
        {
            ShipInstance host = MakeShip();
            ShipInstance mobile = MakeShip();
            host.SceneRoot = new FakeShipSceneRoot();
            mobile.SceneRoot = new FakeShipSceneRoot();
            var port = new GdDict { { "position", Vec3.Zero }, { "facing", Vec3.Back } };
            GdDict res = DockingManager.Dock(host, mobile, port, port);
            Assert.IsTrue(res.GetBool("success"), res.ToString());
            Assert.AreSame(host, mobile.ParentShip);
            CollectionAssert.Contains(host.DockedShips, mobile);
            Assert.IsTrue(mobile.IsWorkingVessel() || !mobile.SystemsManager.IsOperational("propulsion"));
        }
    }
}
