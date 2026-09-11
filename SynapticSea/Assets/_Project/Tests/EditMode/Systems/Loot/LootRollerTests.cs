using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class LootRollerTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void RollsAreDeterministicPerSeedAndVaryBySeed()
        {
            GdDict tables = LootRoller.LoadTables();
            GdArray a = LootRoller.Roll("generic_crate", "marker7:crate_3", tables);
            GdArray b = LootRoller.Roll("generic_crate", "marker7:crate_3", tables);
            Assert.IsFalse(a.IsEmpty);
            Assert.AreEqual(GdJson.Stringify(a), GdJson.Stringify(b));
            GdArray c = LootRoller.Roll("generic_crate", "marker9:crate_8", tables);
            Assert.AreNotEqual(GdJson.Stringify(a), GdJson.Stringify(c), "loot_table_smoke: different seeds differ");
        }

        [Test]
        public void ResultIsMergedSortedAndInRange()
        {
            GdDict tables = LootRoller.LoadTables();
            var allowed = new HashSet<string>();
            foreach (object e in ((GdDict)tables["generic_crate"]).GetArray("entries")) allowed.Add(((GdDict)e).GetString("item_id"));
            for (int i = 0; i < 50; i++)
            {
                GdArray rolled = LootRoller.Roll("generic_crate", "seed-" + i, tables);
                string prev = null;
                long total = 0;
                foreach (object o in rolled)
                {
                    var entry = (GdDict)o;
                    string id = (string)entry["item_id"];
                    Assert.IsTrue(allowed.Contains(id));
                    Assert.IsInstanceOf<long>(entry["quantity"]);
                    if (prev != null) Assert.Less(string.CompareOrdinal(prev, id), 0, "ids sorted, merged");
                    prev = id;
                    total += (long)entry["quantity"];
                }
                Assert.That(total, Is.InRange(2L, 6L), "2 rolls of qty 1..3");
            }
            Assert.IsTrue(LootRoller.Roll("no_such_table", "x", tables).IsEmpty);
        }
    }
}
