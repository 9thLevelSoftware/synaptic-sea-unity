using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class JunkYieldResolverTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void JunkCatalogExposesYields()
        {
            GdDict junk = JunkYieldResolver.LoadDefinitions();
            Assert.That(junk.Count, Is.GreaterThanOrEqualTo(4));
            GdArray yields = JunkYieldResolver.YieldsForItem("frayed_cable_coil");
            Assert.That(yields.Count, Is.GreaterThanOrEqualTo(2));
            Assert.AreEqual(3L, JunkYieldResolver.TotalMaterialValue("frayed_cable_coil", junk));
            Assert.AreEqual("frayed_cable_coil -> wiring_bundle x2, polymer_pellet x1", JunkYieldResolver.ToStatusLine("frayed_cable_coil", junk));
            Assert.IsTrue(JunkYieldResolver.YieldsForItem("scrap_metal", junk).IsEmpty);
        }
    }
}
