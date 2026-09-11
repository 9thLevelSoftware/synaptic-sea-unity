using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class WoundStateTests
    {
        static WoundState SmokeWounds(out string lac, out string burn)
        {
            var ws = new WoundState();
            ws.Configure();
            lac = ws.ApplyWound(new GdDict { { "kind", WoundState.KIND_LACERATION }, { "body_part", WoundState.BODY_TORSO }, { "severity", 0.6 } });
            burn = ws.ApplyWound(new GdDict { { "kind", WoundState.KIND_BURN }, { "body_part", WoundState.BODY_TORSO }, { "severity", 0.7 } });
            ws.ApplyWound(new GdDict { { "kind", WoundState.KIND_PUNCTURE }, { "body_part", WoundState.BODY_ARM }, { "severity", 0.5 } });
            ws.ApplyWound(new GdDict { { "kind", WoundState.KIND_FRACTURE }, { "body_part", WoundState.BODY_ARM }, { "severity", 0.8 } });
            ws.ApplyWound(new GdDict { { "kind", WoundState.KIND_FRACTURE }, { "body_part", WoundState.BODY_LEG }, { "severity", 0.6 } });
            return ws;
        }

        [Test]
        public void Summary_RoundTripsThroughFreshInstance_AndSaveJson()
        {
            var ws = SmokeWounds(out string lac, out _);
            ws.Bandage(lac);
            ws.Tick(10.0);
            GdDict summary = ws.GetSummary();

            var restored = new WoundState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));

            // Through a JSON save: next_id comes back as a float and is coerced.
            var fromJson = new WoundState();
            Assert.IsTrue(fromJson.ApplySummary(GdJson.ParseDict(GdJson.Stringify(summary))));
            Assert.AreEqual("w6", fromJson.ApplyWound(new GdDict { { "kind", "burn" } }));
        }

        [Test]
        public void KindsBleedInfectionAndWorkSpeed()
        {
            var ws = new WoundState();
            ws.Configure();
            Assert.AreEqual("", ws.ApplyWound(new GdDict { { "kind", "magic" }, { "severity", 0.5 } }));
            ws = SmokeWounds(out string lac, out string burn);
            Assert.AreEqual("w1", lac);
            Assert.That(ws.PeakInfectionChance(), Is.GreaterThanOrEqualTo(0.3));
            double work = ws.WorkSpeedMultiplier();
            Assert.That(work, Is.InRange(0.05, 0.55));
            Assert.That(ws.MovementSpeedMultiplier(), Is.LessThan(0.95));
            Assert.That(ws.ThirstDrainMultiplier(), Is.GreaterThan(1.0));

            double before = ws.TotalBleedRate();
            Assert.IsTrue(ws.Bandage(lac));
            Assert.That(ws.TotalBleedRate(), Is.LessThan(before));
            Assert.IsTrue(ws.Treat(burn));
            GdDict burnEntry = ws.GetWound(burn);
            Assert.IsTrue(V.Bool(burnEntry["treated"]));
            Assert.AreEqual(0.0, V.F64(burnEntry["infection_chance"]));

            GdDict sug = WoundState.SuggestFromDamage(20.0, "burn", WoundState.BODY_TORSO);
            Assert.AreEqual(WoundState.KIND_BURN, sug["kind"]);
            Assert.AreEqual(0.5, sug["severity"]);
        }

        [Test]
        public void ConfigureIngestsSavedIdsAndAdvancesNextId()
        {
            var ws = new WoundState();
            ws.Configure(new GdDict { { "wounds", GdArray.Of(new GdDict { { "wound_id", "w7" }, { "kind", "burn" }, { "severity", 0.4 } }) } });
            Assert.AreEqual("w8", ws.ApplyWound(new GdDict { { "kind", "laceration" } }));
            Assert.AreEqual("Wounds: 2 bleed=0.14 work×1.00", ws.GetStatusLines()[0]);
        }
    }
}
