using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class WorkActionCatalogTests
    {
        [SetUp]
        public void SetUp() => CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        [Test]
        public void LoadsDefault_WithSmokeActions_SortedIds()
        {
            var cat = new WorkActionCatalog();
            Assert.IsTrue(cat.LoadDefault());
            Assert.GreaterOrEqual(cat.ActionCount(), 6);
            foreach (string needed in new[] { "cut_wall", "unbolt_component", "weld_patch", "patch_breach", "pry_panel", "splice_conduit" })
                Assert.IsTrue(cat.HasAction(needed), needed);
            var ids = cat.ActionIds();
            for (int i = 1; i < ids.Count; i++)
                Assert.Less(string.CompareOrdinal(ids[i - 1], ids[i]), 0);
            Assert.IsTrue(cat.GetAction("nope").IsEmpty);
        }
    }

    public class WorkActionStateTests
    {
        WorkActionCatalog _cat;

        static GdDict Ctx(string tool, string skill) =>
            new GdDict { { "tool_class", tool }, { "skill_id", skill }, { "skill_level", 0L }, { "inventory", new GdDict() } };

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            _cat = new WorkActionCatalog();
            Assert.IsTrue(_cat.LoadDefault());
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        [Test]
        public void RoundTrip_MidWork()
        {
            var work = new WorkActionState();
            work.ConfigureAction("pry_panel", _cat.GetAction("pry_panel"));
            Assert.IsTrue(work.Start("panel_a", Ctx("prybar", "salvage")));
            work.Tick(1.0, new GdDict());
            GdDict snap = work.GetSummary();
            var restored = new WorkActionState();
            Assert.IsTrue(restored.ApplySummary(snap));
            Assert.IsTrue(V.VariantEquals(snap, restored.GetSummary()));
        }

        [TestCase("secure_connection", 6.0, 2L, 0L, 6.0 * 1.5)]
        [TestCase("secure_connection", 6.0, 2L, 1L, 6.0 * 1.25)]
        [TestCase("secure_connection", 6.0, 2L, 2L, 6.0)]
        [TestCase("commission_home_propulsion", 8.0, 4L, 0L, 8.0 * 2.0)]
        [TestCase("cut_web_attachment", 4.0, 2L, 0L, 4.0 * 1.5)]
        public void RepairWork_IsSlowerBelowTheRequiredSkillButNeverBlocked(string actionId, double baseSeconds, long need, long have, double expected)
        {
            GdDict def = _cat.GetAction(actionId);
            Assert.AreEqual(baseSeconds, def.GetFloat("duration"), 1e-9);
            Assert.AreEqual(need, def.GetInt("min_skill_level"));
            string tool = def.GetString("tool_class");
            var work = new WorkActionState();
            work.ConfigureAction(actionId, def);
            var inventory = new GdDict();
            if (def.Get("materials_consumed", null) is GdDict mats) foreach (object k in mats.Keys) inventory[V.Str(k)] = mats[k];
            var ctx = new GdDict { { "tool_class", tool }, { "skill_id", "repair" }, { "skill_level", have }, { "inventory", inventory } };
            Assert.IsTrue(work.Start("target", ctx), work.BlockReason);
            Assert.AreEqual(baseSeconds, work.Duration, 1e-9, "the authored duration is unchanged");
            Assert.AreEqual(expected, work.EffectiveDuration, 1e-9);
            work.Tick(baseSeconds, new GdDict());
            Assert.AreEqual(baseSeconds / expected, work.ProgressRatio(), 1e-9, "progress runs at 1/slowdown");
            var restored = new WorkActionState();
            Assert.IsTrue(restored.ApplySummary(work.GetSummary()));
            Assert.AreEqual(expected, restored.EffectiveDuration, 1e-9, "the slowdown survives a save");
        }

        [Test]
        public void NonRepairSkillGatesStillBlock()
        {
            var unbolt = new WorkActionState();
            unbolt.ConfigureAction("unbolt_component", _cat.GetAction("unbolt_component")); // salvage 1
            Assert.IsFalse(unbolt.CanStart(Ctx("wrench", "salvage")));
            Assert.AreEqual("skill", unbolt.BlockReason);
        }

        [Test]
        public void Gates_Progress_Interrupt_Complete()
        {
            GdDict cutDef = _cat.GetAction("cut_wall");
            var work = new WorkActionState();
            work.ConfigureAction("cut_wall", cutDef);
            Assert.IsFalse(work.CanStart(new GdDict { { "tool_class", "wrench" }, { "skill_id", "salvage" }, { "skill_level", 5L }, { "inventory", new GdDict() } }));
            Assert.AreEqual("tool", work.BlockReason);

            var weld = new WorkActionState();
            weld.ConfigureAction("weld_patch", _cat.GetAction("weld_patch"));
            Assert.IsFalse(weld.CanStart(Ctx("welding_lance", "repair")));

            Assert.IsTrue(work.Start("wall_01", Ctx("welding_lance", "salvage")));
            work.Tick(2.0, new GdDict());
            Assert.That(work.ProgressRatio(), Is.InRange(0.4, 0.6));
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Tick(0.1, new GdDict { { "damaged", true } }));

            var work2 = new WorkActionState();
            work2.ConfigureAction("cut_wall", cutDef);
            work2.Start("wall_02", Ctx("welding_lance", "salvage"));
            work2.Tick(10.0, new GdDict { { "work_speed_mult", 1.0 } });
            Assert.AreEqual(WorkActionState.STATUS_COMPLETED, work2.Status);
            Assert.GreaterOrEqual(work2.Noise(), 0.5);
            Assert.GreaterOrEqual(work2.MaterialsYielded().GetInt("scrap_metal", 0), 1);
            Assert.IsNotEmpty(work2.XpEvent());
        }
    }
}
