using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class LootDistributionTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        static GdDict Tables() => new GdDict
        {
            {
                "crate", new GdDict
                {
                    { "rolls", 6L },
                    {
                        "entries", GdArray.Of(
                            new GdDict { { "item_id", "scrap_metal" }, { "weight", 5.0 }, { "qty_min", 1L }, { "qty_max", 2L } },
                            new GdDict { { "item_id", "relic" }, { "unique_id", "relic_one" }, { "weight", 50.0 }, { "rarity", "common" } },
                            new GdDict { { "item_id", "ration_pack" }, { "weight", 1.0 }, { "biome_weights", new GdDict { { "dead_fleet", 0.0 } } } })
                    },
                }
            },
        };

        [Test]
        public void DeterministicMergedAndSorted()
        {
            GdArray a = LootDistribution.Roll("crate", "seed-a", Tables());
            GdArray b = LootDistribution.Roll("crate", "seed-a", Tables());
            Assert.IsTrue(V.VariantEquals(a, b), "same inputs, same rolls");
            Assert.That(a.Count, Is.GreaterThan(0));
            var keys = a.Cast<GdDict>().Select(e => V.Str(e.Get("unique_id", e["item_id"]))).ToList();
            Assert.AreEqual(keys.Distinct().Count(), keys.Count, "merged by unique_id/item_id");
            var sorted = keys.ToList();
            GdString.SortStrings(sorted);
            Assert.AreEqual(sorted, keys, "ordered by merge key");
            Assert.IsEmpty(LootDistribution.Roll("missing", "seed-a", Tables()));
        }

        [Test]
        public void ContextWeightsAndUniqueClaims()
        {
            var ctx = new GdDict { { "biome_id", "dead_fleet" } };
            for (int i = 0; i < 20; i++)
            {
                GdArray rolls = LootDistribution.Roll("crate", "s" + i, Tables(), ctx);
                Assert.IsFalse(rolls.Cast<GdDict>().Any(e => V.Str(e["item_id"]) == "ration_pack"), "biome weight 0 excludes the entry");
            }

            var unique = new UniqueItemState();
            unique.Claim("relic_one");
            for (int i = 0; i < 20; i++)
            {
                GdArray rolls = LootDistribution.RollWithUniqueState("crate", "s" + i, Tables(), new GdDict(), unique);
                Assert.IsFalse(rolls.Cast<GdDict>().Any(e => e.Has("unique_id")), "claimed uniques never drop");
            }
            GdArray withRelic = LootDistribution.Roll("crate", "s0", Tables());
            GdDict relic = withRelic.Cast<GdDict>().FirstOrDefault(e => e.Has("unique_id"));
            Assert.IsNotNull(relic, "heavily weighted unique drops without a unique_state");
            Assert.AreEqual(true, relic["world_unique"]);
            StringAssert.StartsWith("s0|", V.Str(relic["seed_key"]));
            StringAssert.EndsWith("|relic", V.Str(relic["seed_key"]));
        }
    }
}
