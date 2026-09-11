using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ShipRuntimeTests
    {
        static GdDict TwoCompartments() => new GdDict
        {
            {
                "compartments", GdArray.Of(
                    new GdDict { { "compartment_id", "bridge" }, { "health", 1.0 }, { "breach_open", false } },
                    new GdDict { { "compartment_id", "engineering" }, { "health", 1.0 }, { "breach_open", false } })
            },
        };

        static ShipInstance MakeShip(string id)
        {
            var inst = ShipInstance.Create(id, "rt:1", null, null, null);
            inst.GetHull().Configure(TwoCompartments());
            inst.GetWeb().Configure(new GdDict { { "attached_to_web", true }, { "seed_coverage", 0.0 }, { "growth_rate", 0.05 }, { "damage_rate", 0.1 } });
            return inst;
        }

        [Test]
        public void RoundTrip_SnapshotMatches()
        {
            ShipInstance inst = MakeShip("runtime_test");
            var map = new ModuleIntegrityMap();
            map.ApplyDamage("eng/wall_a", 0.7, "wall_straight_1x1");
            var rt = new ShipRuntime();
            rt.Configure(inst, new ShipRuntimeOptions { ModuleIntegrity = map });
            rt.Advance(5.0, 5.0);
            GdDict snap = rt.ToSnapshot();
            Assert.AreEqual("ship_runtime_v1", snap.GetString("schema"));
            Assert.AreEqual("runtime_test", snap.GetString("ship_id"));

            ShipInstance inst2 = MakeShip("other");
            var map2 = new ModuleIntegrityMap();
            var rt2 = new ShipRuntime();
            rt2.Configure(inst2, new ShipRuntimeOptions { ModuleIntegrity = map2 });
            rt2.FromSnapshot(snap);
            Assert.AreEqual(5.0, inst2.LastSimTime);
            Assert.AreEqual(map.CountWallBreaches(), map2.CountWallBreaches());
            GdDict again = rt2.ToSnapshot();
            // from_snapshot always mirrors module_integrity onto the ShipInstance sparse pack (PKG-D6.1).
            GdDict mirroredShip = again.GetDictOrEmpty("ship_summary");
            Assert.IsTrue(V.VariantEquals(snap.Get("module_integrity"), mirroredShip.Get("module_integrity")));
            mirroredShip.Erase("module_integrity");
            Assert.IsTrue(V.VariantEquals(snap, again), GdJson.Stringify(again));

            GdDict bundle = ShipRuntime.ComposeRuntimeSnapshots(new[] { rt, rt2 });
            Assert.AreEqual(2, bundle.GetArrayOrEmpty("ships").Count);
        }

        [Test]
        public void AdvanceAndCatchUp_TickWebIntoHull()
        {
            ShipInstance inst = MakeShip("runtime_test");
            var rt = new ShipRuntime();
            rt.Configure(inst);
            double integ0 = inst.GetHull().AverageIntegrity();
            rt.Advance(5.0, 5.0);
            Assert.AreEqual(5.0, inst.LastSimTime);
            Assert.IsTrue(inst.GetWeb().Coverage > 0.0001 || inst.GetHull().AverageIntegrity() < integ0 - 0.0001);

            inst.GetWeb().Coverage = 0.2;
            double before = inst.GetHull().AverageIntegrity();
            rt.CatchUp(65.0);
            Assert.AreEqual(65.0, inst.LastSimTime);
            Assert.Less(inst.GetHull().AverageIntegrity(), before);
            Assert.AreEqual(1L + 20L, rt.FrameBandFires, "60 s gap in 3 s lazy quanta");
            double mid = inst.GetHull().AverageIntegrity();
            rt.CatchUp(65.0);
            Assert.AreEqual(mid, inst.GetHull().AverageIntegrity(), "idempotent catch-up");

            ShipInstance home = MakeShip("home");
            var homeRt = new ShipRuntime();
            bool asked = false;
            homeRt.Configure(home, new ShipRuntimeOptions { IsHome = true, ContactBoostProvider = () => { asked = true; return true; } });
            homeRt.CatchUp(500.0);
            Assert.AreEqual(0.0, home.LastSimTime, "home ships never catch up");
            homeRt.Advance(1.0, 1.0);
            Assert.IsTrue(asked, "home advance consults the contact boost provider");
        }

        [Test]
        public void PollBands_AccumulatesSlowAndLazy()
        {
            var rt = new ShipRuntime();
            rt.Configure(null);
            Assert.IsFalse(rt.PollBands(0.0).GetBool("frame"));
            Assert.IsFalse(rt.PollBands(0.2).GetBool("slow"));
            GdDict r = rt.PollBands(0.2);
            Assert.IsTrue(r.GetBool("slow"));
            Assert.AreEqual(0.4, r.GetFloat("slow_dt"), 1e-12);
            GdDict lazy = rt.PollBands(3.0);
            Assert.IsTrue(lazy.GetBool("lazy"));
            Assert.AreEqual(3.4, lazy.GetFloat("lazy_dt"), 1e-12);
            Assert.AreEqual(1L, rt.LazyBandFires);
            Assert.AreEqual(2L, rt.SlowBandFires);
        }
    }
}
