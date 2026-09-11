using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class DetectionStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var detection = new DetectionState();
            detection.Configure(new GdDict { { "detect_threshold", 0.75 }, { "memory_seconds", 3.0 } });
            detection.UpdateInputs(0.9, 0.1, 0.2, false, "corridor_a");
            detection.Tick(0.1);
            GdDict summary = detection.GetSummary();

            var restored = new DetectionState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsFalse(restored.ApplySummary(summary));
        }

        [Test]
        public void DetectsBySound_RemembersThenExpires()
        {
            var detection = new DetectionState();
            detection.Configure(new GdDict { { "detect_threshold", 0.75 }, { "memory_seconds", 3.0 } });
            detection.UpdateInputs(0.9, 0.1, 0.2, false, "corridor_a");
            detection.Tick(0.1);
            Assert.IsTrue(detection.Detected);
            Assert.AreEqual("sound", detection.LastReason);
            detection.UpdateInputs(0.0, 0.0, 0.0, true, "corridor_a");
            detection.Tick(1.0);
            Assert.IsTrue(detection.Detected);
            Assert.AreEqual("memory", detection.LastReason);
            detection.Tick(4.0);
            Assert.IsFalse(detection.Detected);
            CollectionAssert.AreEqual(new[]
            {
                "Detection: score=0.00 detected=false reason=memory",
                "Stealth: noise=0.00 light=0.00 sight=0.00 crouch=true",
            }, detection.GetStatusLines());
        }

        [Test]
        public void EmittedProfileAppliesCrouchOnce()
        {
            var de = new DetectionState();
            de.Configure();
            de.UpdateInputs(1.0, 0.5, 0.8, true, "");
            GdDict prof = de.GetEmittedProfile();
            Assert.AreEqual(0.65, V.F64(prof["noise"]), 0.001);
            Assert.AreEqual(0.52, V.F64(prof["visibility"]), 0.001);
        }
    }
}
