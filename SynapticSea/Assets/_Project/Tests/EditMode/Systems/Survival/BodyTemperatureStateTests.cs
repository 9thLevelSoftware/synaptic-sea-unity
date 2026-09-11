using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class BodyTemperatureStateTests
    {
        [Test]
        public void SummaryRoundTrips()
        {
            var temp = new BodyTemperatureState();
            temp.Configure(new GdDict { { "temperature", 35.5 }, { "safe_max", 30.0 }, { "in_extreme_zone", true } });
            temp.Tick(2.0);
            GdDict summary = temp.GetSummary();
            var restored = new BodyTemperatureState();
            restored.Configure(new GdDict());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsFalse(restored.ApplySummary(summary), "re-applying an identical summary is not a change");
        }

        [Test]
        public void ExtremeZoneHeatsUp_AndRecoveryReturnsToDefault()
        {
            var temp = new BodyTemperatureState();
            temp.Configure(new GdDict());
            temp.InExtremeZone = true;
            Assert.IsTrue(temp.Tick(1.0));
            Assert.AreEqual(22.5, temp.Temperature);
            Assert.IsFalse(temp.Tick(0.0));
            temp.InExtremeZone = false;
            Assert.IsTrue(temp.Tick(5.0)); // recovery clamps at the distance to 22.0
            Assert.AreEqual(22.0, temp.Temperature);
            Assert.IsFalse(temp.Tick(1.0));
        }

        [Test]
        public void AmbientContext_AndMultipliers()
        {
            var temp = new BodyTemperatureState();
            temp.Configure(new GdDict());
            // Cold ambient (outside the safe band) drains at drain_rate toward the ambient.
            Assert.IsTrue(temp.Tick(4.0, new GdDict { { "ambient_temperature_c", 0.0 } }));
            Assert.AreEqual(20.0, temp.Temperature);
            temp.Temperature = 8.0;
            Assert.IsFalse(temp.IsSafe());
            Assert.AreEqual(1.8, temp.GetThirstMultiplier(), 1e-12);
            Assert.AreEqual(1.8, temp.GetHungerMultiplier(), 1e-12);
            temp.Temperature = 45.0;
            Assert.AreEqual(1.0, temp.GetHungerMultiplier(), "heat does not raise hunger");
            CollectionAssert.AreEqual(new[] { "Temp: 45.0C DANGER", "EXTREME TEMP -> thirst drain increased" }, temp.GetStatusLines());
            Assert.AreEqual(47.0, ((EffectDispatcher.IBodyTemperatureTarget)temp).AdjustTemperature(2.0));
        }
    }
}
