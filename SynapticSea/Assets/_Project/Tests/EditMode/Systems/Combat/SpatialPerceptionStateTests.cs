using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class SpatialPerceptionStateTests
    {
        static GdDict SmokeLayout() => new GdDict
        {
            {
                "rooms", GdArray.Of(
                    new GdDict { { "id", "airlock" } },
                    new GdDict { { "id", "corridor" } },
                    new GdDict { { "id", "bridge" } },
                    new GdDict { { "id", "reactor" } })
            },
            {
                "room_links", GdArray.Of(
                    new GdDict { { "id", "air_to_cor" }, { "from_room", "airlock" }, { "to_room", "corridor" }, { "module_id", "doorway_frame_open_1x1" } },
                    new GdDict { { "id", "cor_to_br" }, { "from_room", "corridor" }, { "to_room", "bridge" }, { "module_id", "doorway_frame_open_1x1" } })
            },
            {
                "blocked_links", GdArray.Of(
                    new GdDict { { "id", "cor_to_reac" }, { "from_room", "corridor" }, { "to_room", "reactor" }, { "module_id", "doorway_frame_blocked_1x1" } })
            },
        };

        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var perc = new SpatialPerceptionState();
            Assert.AreEqual(3, perc.ConfigureFromLayout(SmokeLayout()));
            perc.SetDoorState("corridor", "bridge", "closed");
            GdDict summary = perc.GetSummary();

            var restored = new SpatialPerceptionState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.AreEqual(perc.CanSee("airlock", "bridge"), restored.CanSee("airlock", "bridge"));
        }

        [Test]
        public void ClosedHatchBreaksSightAndMuffles_BlockedNeedsUnblock()
        {
            var perc = new SpatialPerceptionState();
            perc.ConfigureFromLayout(SmokeLayout());
            Assert.IsTrue(perc.CanSee("airlock", "bridge"));
            double open = perc.AttenuateNoise("bridge", "corridor", 1.0);
            Assert.IsTrue(perc.SetDoorState("corridor", "bridge", "closed"));
            Assert.IsFalse(perc.CanSee("airlock", "bridge"));
            double closed = perc.AttenuateNoise("bridge", "corridor", 1.0);
            Assert.AreEqual(SpatialPerceptionState.OPEN_NOISE_ATTEN, open, 1e-12);
            Assert.AreEqual(SpatialPerceptionState.CLOSED_NOISE_ATTEN, closed, 1e-12);

            Assert.IsFalse(perc.CanSee("corridor", "reactor"));
            Assert.That(perc.AttenuateNoise("reactor", "corridor", 1.0), Is.LessThanOrEqualTo(0.1));
            Assert.IsFalse(perc.SetDoorState("corridor", "reactor", "open"));
            Assert.IsTrue(perc.UnblockLink("corridor", "reactor"));
            Assert.IsTrue(perc.CanSee("corridor", "reactor"));

            perc.SetDoorState("corridor", "bridge", "open");
            GdDict probe = perc.Probe("bridge", "airlock", 0.9, 1.0);
            Assert.IsTrue(V.Bool(probe["seen"]));
            Assert.AreEqual(0.9 * 0.85 * 0.85, V.F64(probe["noise_at_observer"]), 1e-12);
        }
    }
}
