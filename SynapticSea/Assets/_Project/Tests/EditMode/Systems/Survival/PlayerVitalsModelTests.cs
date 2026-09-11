using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class PlayerVitalsModelTests
    {
        [Test]
        public void OxygenSuitAndLoadLines()
        {
            var m = new PlayerVitalsModel();
            m.ApplyOxygenSummary(new GdDict
            {
                { "oxygen", 87.0 }, { "breach_open", true }, { "breach_sealed", false },
                { "recovery_threshold", 30.0 }, { "equipment_drain_multiplier", 0.75 },
            });
            m.ApplyInventoryLoad(0.78, 1.0);
            var lines = m.GetStatusLines();
            CollectionAssert.Contains(lines, "Oxygen: 87 (BREACH)");
            CollectionAssert.Contains(lines, "Suit: -25% O2 drain");
            CollectionAssert.Contains(lines, "Load: 78%");
            CollectionAssert.Contains(lines, "Temp: 22.0C");

            GdDict vs = m.GetVitalsSummary();
            Assert.AreEqual(87L, vs["oxygen"]);
            Assert.AreEqual("breach", vs["breach_state"]);
            Assert.AreEqual(25L, vs["suit_drain_percent"]);
            Assert.AreEqual(false, vs["heavy"]);

            m.ApplyOxygenSummary(new GdDict
            {
                { "oxygen", 30.0 }, { "breach_open", false }, { "breach_sealed", false },
                { "recovery_threshold", 30.0 }, { "equipment_drain_multiplier", 1.0 },
            });
            CollectionAssert.Contains(m.GetStatusLines(), "Oxygen: 30 LOW");
            m.ApplyInventoryLoad(1.40, 0.70, 13.2);
            CollectionAssert.Contains(m.GetStatusLines(), "Load: 140% HEAVY (-30% move) (bags -13kg)");
        }

        [Test]
        public void RepairLineSupersedesBlockAndBlockExpires()
        {
            var m = new PlayerVitalsModel();
            m.SetRepairProgress(true, 0.47);
            m.NotifyRepairBlocked("missing_parts");
            CollectionAssert.Contains(m.GetStatusLines(), "Repairing 47%");
            m.SetRepairProgress(false, 0.0);
            CollectionAssert.Contains(m.GetStatusLines(), "Repair blocked: missing parts");
            m.Tick(PlayerVitalsModel.BLOCKED_DISPLAY_SECONDS + 0.1);
            foreach (string line in m.GetStatusLines()) StringAssert.DoesNotStartWith("Repair blocked", line);
        }
    }
}
