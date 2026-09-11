using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class OxygenStateTests
    {
        static GdDict SmokeConfig() => new GdDict
        {
            { "zone_ids", GdArray.Of("corridor_to_reactor") },
            { "max_oxygen", 100.0 },
            { "drain_rate", 6.0 },
            { "regen_rate", 3.5 },
            { "recovery_threshold", 30.0 },
            { "safe_threshold", 35.0 },
        };

        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var model = new OxygenState();
            model.Configure(SmokeConfig());
            model.Tick(1.0, true);
            GdDict summary = model.GetSummary();

            var restored = new OxygenState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()), restored.GetSummary().ToString());
        }

        [Test]
        public void DrainsInsideBreach_RegensOutside_FieldDrainKeepsPassability()
        {
            var model = new OxygenState();
            model.Configure(SmokeConfig());
            Assert.IsTrue(model.Tick(1.0, true));
            Assert.AreEqual(94.0, model.Oxygen);
            model.Tick(1.0, false);
            Assert.AreEqual(97.5, model.Oxygen, 0.001);

            bool passBefore = model.PassabilityBlocked;
            Assert.IsTrue(model.Tick(1.0, new GdDict { { "field_atmosphere", true }, { "player_in_breach_zone", false } }));
            Assert.AreEqual(91.5, model.Oxygen, 0.001);
            Assert.AreEqual(passBefore, model.PassabilityBlocked);
        }

        [Test]
        public void SealingStopsDrain_AndZeroOxygenBlocksPassability()
        {
            var model = new OxygenState();
            model.Configure(SmokeConfig());
            Assert.IsTrue(model.ApplyShipSystemsSummary(new GdDict { { "main_power_restored", true } }));
            double before = model.Oxygen;
            model.Tick(1.0, true);
            Assert.AreEqual(before, model.Oxygen, 0.001);
            Assert.IsFalse(model.SealBreach("corridor_to_reactor"));
            Assert.IsFalse(model.ApplySummary(new GdDict { { "hazard_kind", "fire" } }));

            model.Configure(new GdDict
            {
                { "zone_ids", GdArray.Of("corridor_to_reactor") },
                { "max_oxygen", 30.0 },
                { "drain_rate", 100.0 },
                { "regen_rate", 0.0 },
                { "recovery_threshold", 30.0 },
                { "safe_threshold", 35.0 },
            });
            model.Tick(1.0, true);
            Assert.AreEqual(0.0, model.Oxygen);
            Assert.IsTrue(model.IsPassabilityBlocked());
            CollectionAssert.AreEqual(new[] { "Oxygen: 0 BREACH BLOCKED", "Breach: OPEN" }, model.GetStatusLines());
        }
    }
}
