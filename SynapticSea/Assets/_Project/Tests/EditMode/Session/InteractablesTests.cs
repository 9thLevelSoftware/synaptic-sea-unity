using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>Behavior checks for the engine-free interaction nodes (scripts/tools/*.gd, scripts/interaction/*.gd).</summary>
    public class InteractablesTests
    {
        static readonly Vec3 Here = Vec3.Zero;
        static readonly Vec3 Far = new Vec3(10f, 0f, 0f);

        [SetUp]
        public void SetUp()
        {
            CatalogRegistry.Clear();
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            WorkActionChannel.ResetSharedCatalog();
        }

        [TearDown]
        public void TearDown()
        {
            WorkActionChannel.ResetSharedCatalog();
            CatalogRegistry.Clear();
        }

        static ShipSystemsManager BrokenReactorShip()
        {
            var ship = new ShipSystemsManager();
            ship.Configure(ship.LoadDefinitions(), 0, 1);
            ShipSubcomponent sub = ship.GetSystem("power").GetSubcomponent("reactor_core");
            sub.Health = 0.0;
            sub.RequiredParts = new List<string> { "reactor_core" };
            sub.RequiredTools = new List<string>();
            sub.MinSkill = 0;
            return ship;
        }

        [Test]
        public void RepairPoint_ChannelCompletesAndConsumesParts()
        {
            ShipSystemsManager ship = BrokenReactorShip();
            var inv = new InventoryState();
            var point = new RepairPoint();
            point.Configure("power", "reactor_core", ship, inv, null, Here, 4.0, 0);
            Assert.AreEqual("RepairPoint_power_reactor_core", point.NodeName);

            var blocked = new List<string>();
            int started = 0, completed = 0;
            point.RepairBlocked += (s, c, reason) => blocked.Add(reason);
            point.RepairStarted += (s, c) => started++;
            point.RepairCompleted += (s, c) => completed++;

            Assert.IsFalse(point.TryStart(Far), "strict range gate");
            Assert.IsTrue(point.TryStart(Here), "missing parts still consumes the interact");
            CollectionAssert.AreEqual(new[] { "missing_parts" }, blocked);
            Assert.IsFalse(point.Channeling);

            Assert.AreEqual(1, inv.AddItem("reactor_core", 1));
            Assert.IsTrue(point.TryStart(Here));
            Assert.IsTrue(point.Channeling);
            Assert.AreEqual(1, started);
            Assert.AreEqual(RepairPoint.WORK_ACTION_ID, point.GetWorkActionId());

            point.AdvanceChannel(1.0);
            Assert.AreEqual(0.25, point.Progress, 1e-9);
            Assert.AreEqual(0, completed);
            point.AdvanceChannel(3.0);
            Assert.AreEqual(1, completed);
            Assert.IsTrue(point.Repaired);
            Assert.IsFalse(point.Channeling);
            Assert.AreEqual(1.0, point.Progress);
            Assert.IsTrue(point.CollisionDisabled);
            Assert.IsFalse(point.MarkerShown);
            Assert.AreEqual(0, inv.GetQuantity("reactor_core"), "part consumed on completion");
            Assert.IsTrue(ship.GetSystem("power").GetSubcomponent("reactor_core").IsFunctional());
            Assert.AreEqual("", point.GetWorkActionId());
        }

        [Test]
        public void RepairPoint_LeavingRangeCancelsWithoutPartLoss()
        {
            ShipSystemsManager ship = BrokenReactorShip();
            var inv = new InventoryState();
            inv.AddItem("reactor_core", 1);
            var point = new RepairPoint();
            point.Configure("power", "reactor_core", ship, inv, null, Here, 4.0, 0);
            Assert.IsTrue(point.TryStart(Here));
            point.Process(1.0, Here);
            Assert.AreEqual(0.25, point.Progress, 1e-9);
            point.Process(1.0, Far);
            Assert.IsFalse(point.Channeling);
            Assert.AreEqual(0.0, point.Progress);
            Assert.AreEqual(1, inv.GetQuantity("reactor_core"));
            Assert.IsFalse(point.Repaired);
        }

        [Test]
        public void DockPortBarrier_IntactOpensInOneInteract_BrokenChannels()
        {
            var intact = new DockPortBarrier();
            intact.Configure("dock_a", "intact", null, Here, 6.0);
            var opened = new List<string>();
            intact.BreachOpened += opened.Add;
            Assert.IsFalse(intact.TryStart(Far));
            Assert.IsTrue(intact.TryStart(Here));
            Assert.IsTrue(intact.Opened);
            Assert.IsTrue(intact.CollisionDisabled);
            CollectionAssert.AreEqual(new[] { "dock_a" }, opened);
            Assert.IsFalse(intact.TryStart(Here), "already open");

            var broken = new DockPortBarrier();
            broken.Configure("dock_b", "broken", null, Here, 6.0);
            broken.BreachOpened += opened.Add;
            Assert.IsTrue(broken.TryStart(Here));
            Assert.IsTrue(broken.Channeling);
            Assert.IsFalse(broken.Opened);
            broken.Process(3.0, Here);
            Assert.AreEqual(0.5, broken.Progress, 1e-9);
            broken.Process(0.1, Far);
            Assert.IsFalse(broken.Channeling, "leaving range cancels");
            Assert.AreEqual(0.0, broken.Progress);
            Assert.IsTrue(broken.TryStart(Here));
            broken.AdvanceChannel(6.0);
            Assert.IsTrue(broken.Opened);
            CollectionAssert.AreEqual(new[] { "dock_a", "dock_b" }, opened);
        }

        [Test]
        public void LootContainer_AuthoredContentsGrantedOnce()
        {
            var inv = new InventoryState();
            var box = new LootContainer();
            var context = new GdDict
            {
                { "contents", GdArray.Of(new GdDict { { "item_id", "scrap_metal" }, { "qty", 3L } }, new GdDict { { "item_id", "" }, { "qty", 1L } }) },
            };
            box.Configure("crate_01", "derelict_common", "seed|crate_01", inv, new GdDict(), Here, 1.8, context);
            Assert.AreEqual("LootContainer_crate_01", box.NodeName);
            Assert.AreEqual("loot_crate", box.PropId);

            var searches = new List<GdArray>();
            box.ContainerSearched += (id, granted) => searches.Add(granted);

            Assert.IsFalse(box.TryInteract(Far));
            box.SetValidationPlayerInRange(true);
            Assert.IsTrue(box.TryInteract(Far), "candidate overlap bypasses the distance check");
            Assert.IsTrue(box.Searched);
            Assert.IsTrue(box.CollisionDisabled);
            Assert.IsFalse(box.MarkerShown);
            Assert.AreEqual(3, inv.GetQuantity("scrap_metal"));
            Assert.AreEqual(1, searches.Count);
            Assert.AreEqual(1, searches[0].Count);
            var entry = (GdDict)searches[0][0];
            Assert.AreEqual("scrap_metal", entry.GetString("item_id"));
            Assert.AreEqual(3L, entry["quantity"]);
            Assert.AreEqual("seed|crate_01|scrap_metal", entry.GetString("seed_key"));

            Assert.IsFalse(box.TryInteract(Here), "searched once");
            Assert.AreEqual(1, searches.Count);
            Assert.AreEqual(3, inv.GetQuantity("scrap_metal"));

            GdArray normalized = LootContainer.NormalizedContents(new GdDict { { "contents", GdArray.Of(new GdDict { { "item_id", "a" }, { "quantity", 2.0 } }) } });
            Assert.IsTrue(V.VariantEquals(GdArray.Of(new GdDict { { "item_id", "a" }, { "qty", 2L }, { "quantity", 2L } }), normalized));
        }

        [Test]
        public void SealedHatch_BypassNeedsFlagThenReseals()
        {
            var hatch = new SealedHatch();
            hatch.Configure("hatch_1", "bogus_kind", Here);
            Assert.AreEqual(SealedHatch.MECHANICAL, hatch.LockKind);
            Assert.AreEqual("lockpick", hatch.RequiredFlag());
            var bypassed = new List<string>();
            hatch.HatchBypassed += (id, kind) => bypassed.Add(id);

            Assert.AreEqual("out_of_range", hatch.TryBypass(Far, new GdDict { { "lockpick", true } }).GetString("reason"));
            GdDict locked = hatch.TryBypass(Here, new GdDict { { "hack_chip", true } });
            Assert.IsFalse(locked.GetBool("ok"));
            Assert.AreEqual("locked", locked.GetString("reason"));
            Assert.AreEqual("lockpick", locked.GetString("needs"));
            Assert.IsFalse(hatch.Bypassed);

            GdDict ok = hatch.TryBypass(Here, new GdDict { { "lockpick", true } });
            Assert.IsTrue(V.VariantEquals(new GdDict { { "ok", true }, { "hatch_id", "hatch_1" }, { "lock_kind", "mechanical" }, { "consumed_flag", "lockpick" } }, ok));
            Assert.IsTrue(hatch.Bypassed);
            Assert.IsTrue(hatch.BlockerDisabled);
            CollectionAssert.AreEqual(new[] { "hatch_1" }, bypassed);
            Assert.AreEqual("already_open", hatch.TryBypass(Here, new GdDict { { "lockpick", true } }).GetString("reason"));

            Assert.IsTrue(hatch.TryReseal(Here).GetBool("ok"));
            Assert.IsFalse(hatch.Bypassed);
            Assert.AreEqual("already_sealed", hatch.TryReseal(Here).GetString("reason"));
        }

        [Test]
        public void WorkYieldDrop_PartialThenFullScoopFreesNode()
        {
            var inv = new InventoryState();
            inv.AddItem("reactor_core", 1); // max_stack 2 -> only one more fits
            var drop = new WorkYieldDrop();
            drop.Configure("d1", new GdDict { { "reactor_core", 3L } }, inv, Here);
            var grants = new List<GdDict>();
            drop.Scooped += (id, granted) => grants.Add(granted);

            Assert.IsTrue(drop.TryInteract(Here));
            Assert.AreEqual(1L, grants[0]["reactor_core"]);
            Assert.AreEqual(2L, drop.Items["reactor_core"]);
            Assert.IsFalse(drop.ScoopedFlag);
            Assert.IsFalse(drop.TryInteract(Here), "stack full leaves the pile");

            inv.RemoveItem("reactor_core", 2);
            Assert.IsTrue(drop.TryInteract(Here));
            Assert.IsTrue(drop.ScoopedFlag);
            Assert.IsFalse(drop.IsValid, "queue_free");
            Assert.AreEqual(2, grants.Count);
        }
    }
}
