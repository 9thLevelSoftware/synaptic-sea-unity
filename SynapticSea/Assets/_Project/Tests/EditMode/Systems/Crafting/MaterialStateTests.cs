using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class MaterialStateTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void SummaryRoundTrips()
        {
            var mat = new MaterialState();
            mat.SetQuality("scrap_metal", 0.8);
            mat.SetQuality("adhesive_paste", 0.6);
            GdDict summary = mat.GetSummary();
            Assert.That(V.I64(summary["defined_count"]), Is.GreaterThanOrEqualTo(30));
            var restored = new MaterialState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void DefinitionsAndWeightedQuality()
        {
            var mat = new MaterialState();
            Assert.IsTrue(mat.HasDefinition("nanite_slurry"));
            Assert.AreEqual("Scrap Metal", mat.GetDisplayName("scrap_metal"));
            Assert.AreEqual("part", mat.GetCategory("circuit_board"));
            Assert.AreEqual(1.0, mat.GetWeight("purified_water"));
            Assert.AreEqual(50L, mat.GetMaxStack("graphene_sheet"));
            Assert.AreEqual(mat.GetBaseQuality("circuit_board"), mat.GetQuality("circuit_board"), 0.001);
            mat.SetQuality("scrap_metal", 0.8);
            mat.SetQuality("adhesive_paste", 0.6);
            double avg = mat.AverageIngredientQuality(new GdDict { { "scrap_metal", 2L }, { "adhesive_paste", 1L } });
            Assert.AreEqual((0.8 * 2.0 + 0.6) / 3.0, avg, 0.001);
        }
    }
}
