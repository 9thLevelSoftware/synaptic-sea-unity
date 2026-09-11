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
