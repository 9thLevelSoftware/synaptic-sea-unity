using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class RouteControlStateTests
    {
        static GdDict Systems(bool power, bool cleared, bool extraction) => new GdDict
        {
            { "main_power_restored", power },
            { "blocked_routes_cleared", cleared },
            { "extraction_unlocked", extraction },
        };

        [Test]
        public void RoundTrip()
        {
            var model = new RouteControlState();
            model.ConfigureFromBlockedRoutes(GdArray.Of("gate_beta", "gate_alpha", ""));
            model.ApplySummary(new GdDict { { "gate_records", new GdDict { { "gate_alpha", new GdDict { { "open", true } } } } } });
            GdDict summary = model.GetSummary();
            Assert.IsTrue(V.VariantEquals(GdArray.Of("gate_alpha", "gate_beta"), summary["gate_ids"]));

            var fresh = new RouteControlState();
            Assert.IsTrue(fresh.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, fresh.GetSummary()));
        }

        [Test]
        public void GatesOpenOnlyWithPowerAndClearance_ThenExtraction()
        {
            var model = new RouteControlState();
            model.ConfigureFromBlockedRoutes(GdArray.Of("gate_alpha", "gate_beta"));
            GdDict initial = model.GetSummary();
            Assert.AreEqual(2L, initial["route_gate_count"]);
            Assert.AreEqual(2L, initial["active_blocker_count"]);
            Assert.AreEqual(false, initial["powered_gates_open"]);

            Assert.IsFalse(model.ApplyShipSystemsSummary(Systems(true, false, false)));
            Assert.IsFalse(model.IsGateOpen("gate_alpha"));
            Assert.IsTrue(model.ApplyShipSystemsSummary(Systems(true, true, false)));
            Assert.IsTrue(model.IsGateOpen("gate_alpha") && model.IsGateOpen("gate_beta"));
            Assert.AreEqual(true, model.GetSummary()["powered_gates_open"]);
            Assert.IsFalse(model.ApplyShipSystemsSummary(Systems(true, true, false)));
            Assert.IsTrue(model.ApplyShipSystemsSummary(Systems(true, true, true)));
            Assert.IsTrue(model.IsExtractionUnlocked());
            CollectionAssert.AreEqual(new[] { "Routes: POWERED OPEN", "Extraction: UNLOCKED" }, model.GetStatusLines());
        }
    }
}
