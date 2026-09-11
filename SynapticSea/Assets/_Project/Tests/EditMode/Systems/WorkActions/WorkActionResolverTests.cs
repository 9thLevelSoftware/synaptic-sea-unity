using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class WorkActionResolverTests
    {
        WorkActionCatalog _cat;

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            _cat = new WorkActionCatalog();
            Assert.IsTrue(_cat.LoadDefault());
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        static GdDict Ctx(string skill, GdDict inventory) =>
            new GdDict { { "tool_class", "welding_lance" }, { "skill_id", skill }, { "skill_level", 0L }, { "inventory", inventory } };

        [Test]
        public void Cut_DestroysWallAndYieldsScrap()
        {
            var map = new ModuleIntegrityMap();
            map.EnsureModule("eng/wall_a", "wall_straight_1x1", new GdDict(), "eng");
            var cut = new WorkActionState();
            cut.ConfigureAction("cut_wall", _cat.GetAction("cut_wall"));
            Assert.IsTrue(cut.Start("eng/wall_a", Ctx("salvage", new GdDict())));
            GdDict early = WorkActionResolver.ResolveCompletion(cut, map, "eng/wall_a");
            Assert.AreEqual("not_completed", early.GetString("reason"));
            cut.Tick(10.0, new GdDict());
            GdDict res = WorkActionResolver.ResolveCompletion(cut, map, "eng/wall_a");
            Assert.IsTrue(res.GetBool("ok"), res.GetString("reason"));
            Assert.AreEqual("destroyed", map.GetState("eng/wall_a"));
            Assert.IsTrue(res.GetBool("nav_gap") && res.GetBool("atmosphere_link"));
            Assert.GreaterOrEqual(res.GetFloat("noise"), 0.5);
            Assert.IsNotEmpty(res.GetString("xp_event"));
            var inv = new GdDict();
            WorkActionResolver.ApplyYieldsToInventory(inv, res.GetDictOrEmpty("yields"));
            Assert.GreaterOrEqual(inv.GetInt("scrap_metal"), 1L);
            Assert.AreEqual("no_work", WorkActionResolver.ResolveCompletion(null).GetString("reason"));
        }

        [Test]
        public void Weld_ConsumesPlateAndRepairsWall()
        {
            var map = new ModuleIntegrityMap();
            map.EnsureModule("eng/wall_b", "wall_straight_1x1", new GdDict(), "eng");
            map.ApplyDamage("eng/wall_b", 0.4, "wall_straight_1x1");
            var weld = new WorkActionState();
            weld.ConfigureAction("weld_patch", _cat.GetAction("weld_patch"));
            var inv2 = new GdDict { { "hull_plate", 1L } };
            Assert.IsTrue(weld.Start("eng/wall_b", Ctx("repair", inv2)));
            Assert.IsTrue(WorkActionResolver.ConsumeFromInventory(inv2, weld.MaterialsConsumed()));
            Assert.IsFalse(inv2.Has("hull_plate"), "exhausted stacks are erased");
            Assert.IsFalse(WorkActionResolver.ConsumeFromInventory(inv2, weld.MaterialsConsumed()), "insufficient");
            weld.Tick(10.0, new GdDict());
            GdDict res = WorkActionResolver.ResolveCompletion(weld, map, "eng/wall_b");
            Assert.IsTrue(res.GetBool("ok"));
            Assert.Greater(map.GetModule("eng/wall_b").Integrity, 0.6);
        }
    }
}
