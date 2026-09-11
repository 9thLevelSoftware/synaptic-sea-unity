using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class LifeSupportSystemTests
    {
        static LifeSupportSystem Make()
        {
            var ls = new LifeSupportSystem("life_support", new[] { "power" });
            ls.AddSubcomponent(new ShipSubcomponent("air_recycler", null, null, 0, 5.0, 0.5));
            return ls;
        }

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var ls = Make();
            ls.Advance(1.0, false);
            GdDict summary = ls.GetSummary();
            Assert.IsInstanceOf<GdDict>(summary.Get("oxygen"));
            var fresh = Make();
            fresh.Advance(2.0, false);
            Assert.IsTrue(fresh.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, fresh.GetSummary()));
            Assert.AreEqual(ls.GetOxygenState().Oxygen, fresh.GetOxygenState().Oxygen, 1e-4);
        }

        [Test]
        public void Advance_DispatchesVirtuallyThroughShipSystem()
        {
            ShipSystem ls = Make();
            double start = ((LifeSupportSystem)ls).GetOxygenState().Oxygen;
            ls.Advance(1.0, false); // base-typed call must reach the override
            double drained = ((LifeSupportSystem)ls).GetOxygenState().Oxygen;
            Assert.Less(drained, start);
            ls.Advance(1.0, true);
            Assert.GreaterOrEqual(((LifeSupportSystem)ls).GetOxygenState().Oxygen, drained);
            Assert.IsTrue(ls.GetSummary().Has("oxygen"));
        }
    }

    public class PropulsionStateTests
    {
        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var p = new PropulsionState();
            p.Configure(new GdDict { { "thrust_percent", 40.0 }, { "power_threshold", 0.3 } });
            p.Tick(1.0, new GdDict { { SimKeys.PoweredRatio, 1.0 } });
            GdDict summary = p.GetSummary();
            var q = new PropulsionState();
            Assert.IsTrue(q.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, q.GetSummary()));
            Assert.IsFalse(q.ApplySummary(summary));
        }

        [Test]
        public void Tick_PowerAndHullGateThrust()
        {
            var p = new PropulsionState();
            p.Configure(new GdDict());
            for (int i = 0; i < 20; i++)
                p.Tick(1.0, new GdDict { { SimKeys.PoweredRatio, 1.0 }, { SimKeys.ManagerOperational, true } });
            Assert.IsTrue(p.CanPropel());
            Assert.Greater(p.ThrustPercent, 90.0);
            p.Tick(1.0, new GdDict { { SimKeys.PoweredRatio, 1.0 }, { SimKeys.HullPenalty, 0.7 } });
            Assert.IsFalse(p.Operational);
            Assert.IsFalse(p.CanPropel());
            string line = p.GetStatusLines()[0];
            StringAssert.StartsWith("Propulsion thrust=", line);
            StringAssert.EndsWith("C OFFLINE", line);
        }
    }

    public class ModuleIntegrityMapTests
    {
        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var map = new ModuleIntegrityMap();
            map.EnsureModule("eng_01/wall_n", "wall_straight", null, "eng_01");
            map.ApplyDamage("eng_01/wall_n", 0.7);
            map.ApplyDamage("bridge/floor_1", 0.1, "floor_1x1");
            map.EnsureModule("bridge/pristine", "floor_1x1");
            GdDict summary = map.GetSummary();
            Assert.AreEqual(2, summary.GetArray("deltas").Count); // pristine module is not persisted
            var restored = new ModuleIntegrityMap();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary.GetArray("deltas"), restored.GetSummary().GetArray("deltas")));
            Assert.AreEqual(map.GetState("eng_01/wall_n"), restored.GetState("eng_01/wall_n"));
        }

        [Test]
        public void BreachesNavGapsAndInterface()
        {
            IModuleIntegrityMap map = new ModuleIntegrityMap();
            Assert.AreEqual(ModuleIntegrityState.STATE_INTACT, map.GetState("missing"));
            Assert.AreEqual(ModuleIntegrityState.STATE_BREACHED, map.ApplyDamage("cargo/wall_e", 0.65, "Wall_Straight"));
            map.ApplyDamage("eng/door_1", 0.99, "door_single");
            map.ApplyDamage("eng/floor_1", 0.99, "floor_1x1");
            var concrete = (ModuleIntegrityMap)map;
            Assert.AreEqual(2, concrete.CountWallBreaches());
            Assert.AreEqual(new List<string> { "cargo", "eng" }, concrete.RoomsWithNavGaps());
            Assert.AreEqual(new List<string> { "cargo/wall_e", "eng/door_1", "eng/floor_1" }, new List<string>(map.ModuleIds()));
            Assert.AreEqual("cargo/wall_e:breached:0.3500|eng/door_1:destroyed:0.0100|eng/floor_1:destroyed:0.0100", concrete.Fingerprint());
            // Works as ModuleDamageRouter's module map.
            GdDict routed = ModuleDamageRouter.Apply(map, "cargo/wall_e", ModuleDamageRouter.SOURCE_TOOL, 0.5);
            Assert.IsTrue(routed.GetBool("ok"));
        }
    }
}
