using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ElectricalArcStateTests
    {
        static GdDict SmokeConfig() => new GdDict
        {
            { "zone_ids", GdArray.Of("side_corridor_arc") },
            { "arcing_duration", 2.5 },
            { "discharged_duration", 1.5 },
        };

        [Test]
        public void Summary_RoundTripsThroughBareInstance()
        {
            var model = new ElectricalArcState();
            model.Configure(SmokeConfig());
            model.Tick(2.0);
            GdDict summary = model.GetSummary();

            var restored = new ElectricalArcState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()), restored.GetSummary().ToString());
            Assert.IsFalse(restored.ApplySummary(new GdDict { { "hazard_kind", "oxygen" }, { "phase", 0L } }));
        }

        [Test]
        public void CycleFlipsToArcingAndBlocksPassability()
        {
            var model = new ElectricalArcState();
            model.Configure(SmokeConfig());
            Assert.IsFalse(model.IsPassabilityBlocked());
            Assert.IsFalse(model.Tick(1.0));
            Assert.IsTrue(model.Tick(0.6)); // 1.6s >= 1.5s DISCHARGED -> ARCING
            Assert.IsTrue(model.IsPassabilityBlocked());
            Assert.AreEqual("ARCING", model.GetSummary().GetString("state"));
            Assert.IsTrue(model.Tick(2.5)); // back to DISCHARGED
            Assert.IsFalse(model.IsPassabilityBlocked());
        }

        [Test]
        public void ArcingFirstStartsBlocked()
        {
            var model = new ElectricalArcState();
            var config = SmokeConfig();
            config["arcing_first"] = true;
            model.Configure(config);
            Assert.IsTrue(model.IsPassabilityBlocked());
            Assert.AreEqual(1L, model.GetSummary().Get("phase"));
        }
    }

    public class FireSuppressionStateTests
    {
        static GdDict SmokeConfig() => new GdDict
        {
            { "compartments", GdArray.Of("bridge", "engineering", "hydroponics", "cargo") },
            {
                "adjacency", new GdDict
                {
                    { "bridge", GdArray.Of("engineering") },
                    { "engineering", GdArray.Of("bridge", "hydroponics", "cargo") },
                    { "hydroponics", GdArray.Of("engineering") },
                    { "cargo", GdArray.Of("engineering") },
                }
            },
            { "suppressant_units", 100.0 },
            { "suppression_rate_per_second", 25.0 },
            { "power_threshold", 0.5 },
            { "spread_rate_per_second", 0.15 },
            { "ignition_rate_per_second", 0.2 },
            { "cascade_rate_per_second", 0.5 },
            { "arc_compartment", "engineering" },
        };

        static GdDict Ctx(double powered = 0.0, GdArray breached = null, GdArray damaged = null, bool arc = false) => new GdDict
        {
            { "breached_compartments", breached ?? new GdArray() },
            { "damaged_compartments", damaged ?? new GdArray() },
            { "ship_oxygen_present", true },
            { "powered_ratio", powered },
            { "arc_arcing", arc },
        };

        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var model = new FireSuppressionState();
            model.Configure(SmokeConfig());
            model.Ignite("engineering", 1.0);
            model.SetLinkClosed("engineering", "bridge");
            model.SetVented("cargo");
            model.Tick(2.0, Ctx());
            GdDict summary = model.GetSummary();

            var restored = new FireSuppressionState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()), restored.GetSummary().ToString());
        }

        [Test]
        public void FirePersistsSpreadsAndVentingExtinguishes()
        {
            var model = new FireSuppressionState();
            model.Configure(SmokeConfig());
            Assert.IsTrue(model.Ignite("engineering"));
            model.Tick(5.0, Ctx());
            Assert.IsTrue(model.IsBurning("engineering"), "fire persists without suppression");
            model.Tick(5.0, Ctx());
            Assert.IsTrue(model.IsBurning("bridge"), "fire spreads to adjacent oxygenated compartments");

            Assert.IsTrue(model.Tick(0.1, Ctx(breached: GdArray.Of("bridge"))));
            Assert.IsFalse(model.IsBurning("bridge"), "breach extinguishes");
            Assert.IsTrue(model.DeliberateVent("engineering"));
            Assert.IsFalse(model.IsBurning("engineering"));
        }

        [Test]
        public void PoweredSuppressionDrainsSuppressantAndDamageIgnites()
        {
            var model = new FireSuppressionState();
            model.Configure(SmokeConfig());
            model.Ignite("engineering");
            Assert.IsTrue(model.Tick(0.5, Ctx(powered: 1.0)));
            Assert.AreEqual(0.5, model.GetIntensity("engineering"), 1e-9);
            Assert.AreEqual(99.75, model.SuppressantUnits, 1e-9);

            var fresh = new FireSuppressionState();
            fresh.Configure(SmokeConfig());
            Assert.IsTrue(fresh.Tick(5.0, Ctx(damaged: GdArray.Of("hydroponics"))));
            Assert.IsTrue(fresh.IsBurning("hydroponics"));
        }
    }
}
