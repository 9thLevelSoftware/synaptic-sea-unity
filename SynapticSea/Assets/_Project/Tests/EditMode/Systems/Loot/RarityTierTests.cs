using NUnit.Framework;
using SynapticSea.Core.Systems;

namespace SynapticSea.Tests.Systems
{
    public class RarityTierTests
    {
        [Test]
        public void NormalizesRanksAndRolls()
        {
            Assert.AreEqual("legendary", RarityTier.Normalize("  LEGENDARY "));
            Assert.AreEqual("common", RarityTier.Normalize("unknown"));
            Assert.AreEqual("legendary", RarityTier.FromRoll(0.98));
            Assert.AreEqual("epic", RarityTier.FromRoll(0.86));
            Assert.AreEqual("epic", RarityTier.MaxRarity("rare", "epic"));
            Assert.AreEqual(4L, RarityTier.Rank("legendary"));
            Assert.AreEqual(0.45, RarityTier.WeightMultiplier("rare"));
            Assert.AreEqual("Legendary", RarityTier.Label("legendary"));
        }

        [Test]
        public void HexMatchesGodotToHtml()
        {
            Assert.AreEqual("9aa4afff", RarityTier.Hex("common"));
            Assert.AreEqual("4d9bffff", RarityTier.Hex("rare"));
            var lines = RarityTier.GetStatusLines();
            Assert.AreEqual(5, lines.Count);
            Assert.AreEqual("legendary=ffb347ff", lines[4]);
        }
    }
}
