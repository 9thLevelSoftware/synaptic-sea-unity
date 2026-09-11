using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class DeconstructionResolverTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void RoundTrip_SummaryIsCatalogCountsOnly()
        {
            var r = new DeconstructionResolver();
            GdDict summary = r.GetSummary();
            Assert.Greater(summary.GetInt("deconstruction_recipes"), 0L);
            Assert.Greater(summary.GetInt("junk_catalog_items"), 0L);
            var fresh = new DeconstructionResolver();
            Assert.IsFalse(fresh.ApplySummary(summary), "apply_summary never reports a change");
            Assert.IsTrue(V.VariantEquals(summary, fresh.GetSummary()));
        }

        [Test]
        public void ListSalvageEntries_SortedWithReadyStatus()
        {
            var r = new DeconstructionResolver();
            var inv = new InventoryState();
            foreach (object e in r.ListSalvageEntries(inv))
                Assert.IsFalse(((GdDict)e).GetBool("craftable"), "empty inventory has nothing ready");

            inv.AddItem("plating", 2);
            inv.AddItem("scrap_metal", 4);
            inv.AddItem("frayed_cable_coil", 1);
            GdArray entries = r.ListSalvageEntries(inv);
            string prev = "";
            bool sawJunk = false;
            foreach (object e in entries)
            {
                var d = (GdDict)e;
                string rid = d.GetString("recipe_id");
                Assert.IsFalse(prev.Length > 0 && GdString.Less(rid, prev), $"{prev} before {rid}");
                prev = rid;
                if (rid == "junk:frayed_cable_coil")
                {
                    sawJunk = true;
                    Assert.AreEqual("ready", d.GetString("status"));
                    Assert.AreEqual("wiring_bundle", d.GetDictOrEmpty("produces").GetString("item_id"));
                }
            }
            Assert.IsTrue(sawJunk);
            string first = r.FirstReadySalvageId(inv);
            Assert.IsNotEmpty(first);
            Assert.IsFalse(r.ExecuteSalvageTarget(first, inv, new MaterialState()).IsEmpty);
        }

        [Test]
        public void Deconstruct_ConsumesAndInheritsQuality()
        {
            var r = new DeconstructionResolver();
            var inv = new InventoryState();
            var mat = new MaterialState();
            inv.AddItem("plating", 1);
            Assert.IsTrue(r.CanDeconstruct("deconstruct_plating", inv));
            GdDict result = r.Deconstruct("deconstruct_plating", inv, mat, new GdDict { { "skill_level", 10L } });
            Assert.AreEqual("scrap_metal", result.GetString("item_id"));
            Assert.AreEqual(0L, inv.GetQuantity("plating"));
            double expected = GdMath.Clampf(result.GetFloat("source_quality") * 1.2, 0.0, 1.0);
            Assert.AreEqual(expected, result.GetFloat("quality"), 1e-12);
            Assert.IsTrue(r.Deconstruct("deconstruct_plating", inv, mat).IsEmpty, "nothing left to break down");

            var junkInv = new InventoryState();
            junkInv.AddItem("frayed_cable_coil", 1);
            GdDict junk = r.SalvageJunk(junkInv, mat);
            Assert.AreEqual("frayed_cable_coil", junk.GetString("source_junk"));
            Assert.IsTrue(junk.GetBool("multi_yield"));
            Assert.AreEqual(0L, junkInv.GetQuantity("frayed_cable_coil"));
            Assert.AreEqual(2L, junkInv.GetQuantity("wiring_bundle"));
        }
    }
}
