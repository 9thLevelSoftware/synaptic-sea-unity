using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class EncumbranceTests
    {
        [Test]
        public void HeavyLoadCurve()
        {
            Assert.AreEqual(1.0, Encumbrance.MoveSpeedMultiplier(1.0));
            Assert.AreEqual(0.63, Encumbrance.MoveSpeedMultiplier(1.25), 0.01);
            Assert.AreEqual(0.25, Encumbrance.MoveSpeedMultiplier(3.0));
            Assert.AreEqual(0.0, Encumbrance.HealthDrainPerSecond(1.0));
            Assert.AreEqual(2.0, Encumbrance.HealthDrainPerSecond(1.75), 0.01);
        }

        [Test]
        public void WeightReductionFillsBestFirst()
        {
            var bags = GdArray.Of(
                new GdDict { { "capacity", 30.0 }, { "reduction", 0.10 } },
                new GdDict { { "capacity", 30.0 }, { "reduction", 0.50 } });
            Assert.AreEqual(16.0, Encumbrance.WeightReductionSaved(40.0, bags), 1e-9);
            var spec = GdArray.Of(
                new GdDict { { "capacity", 40.0 }, { "reduction", 0.30 } },
                new GdDict { { "capacity", 12.0 }, { "reduction", 0.10 } });
            Assert.AreEqual(13.2, Encumbrance.WeightReductionSaved(70.0, spec), 1e-9);
            Assert.AreEqual(0.0, Encumbrance.WeightReductionSaved(-5.0, spec));
        }
    }
}
