using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class UniqueItemStateTests
    {
        static UniqueItemState Build()
        {
            var state = new UniqueItemState();
            state.Configure();
            Assert.IsTrue(state.Claim("captains_black_box", "seed:a", "captains_black_box"));
            Assert.IsTrue(state.RecordCodexUnlock("synaptic_sea_reliquary"));
            return state;
        }

        [Test]
        public void SummaryRoundTrips()
        {
            var state = Build();
            GdDict summary = state.GetSummary();
            var clone = new UniqueItemState();
            clone.Configure(summary);
            Assert.IsTrue(V.VariantEquals(summary, clone.GetSummary()));
            Assert.IsTrue(clone.IsSeedClaimed("seed:a"));
        }

        [Test]
        public void ClaimsAndCodexUnlocksAreOnce()
        {
            var state = Build();
            Assert.IsFalse(state.CanClaim("captains_black_box", "seed:a"));
            Assert.IsFalse(state.Claim("captains_black_box", "seed:a", "captains_black_box"));
            Assert.IsFalse(state.RecordCodexUnlock("synaptic_sea_reliquary"));
            Assert.AreEqual("unique_codex=2", state.GetStatusLines()[2]);
        }
    }
}
