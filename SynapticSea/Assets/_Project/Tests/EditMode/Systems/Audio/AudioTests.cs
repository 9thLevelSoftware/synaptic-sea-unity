using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class SpatialAudioResolverTests
    {
        static SpatialAudioResolver SmokeResolver()
        {
            var resolver = new SpatialAudioResolver();
            resolver.Configure(new GdDict
            {
                { "ref_distance", 2.0 },
                { "max_distance", 22.0 },
                { "max_attenuation_db", -36.0 },
                { "occlusion_penalty_db", -6.0 },
            });
            return resolver;
        }

        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            GdDict summary = SmokeResolver().GetSummary();
            var restored = new SpatialAudioResolver();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsFalse(restored.ApplySummary(new GdDict { { "kind", "other" }, { "ref_distance", 5.0 } }));
        }

        [Test]
        public void LinearFalloffAndOcclusion()
        {
            var resolver = SmokeResolver();
            Assert.AreEqual(0.0, resolver.ResolveVolumeDb(new Vec3(2.0, 0.0, 0.0), Vec3.Zero, false, 0.0), 0.001);
            Assert.AreEqual(-36.0, resolver.ResolveVolumeDb(new Vec3(22.0, 0.0, 0.0), Vec3.Zero, false, 0.0), 0.001);
            Assert.AreEqual(-18.0, resolver.ResolveVolumeDb(new Vec3(12.0, 0.0, 0.0), Vec3.Zero, false, 0.0), 0.001);
            Assert.AreEqual(-24.0, resolver.ResolveVolumeDb(new Vec3(12.0, 0.0, 0.0), Vec3.Zero, true, 0.0), 0.001);
            var nanPos = new Vec3(float.NaN, 0f, 0f);
            Assert.AreEqual(-3.0, resolver.ResolveVolumeDb(nanPos, Vec3.Zero, false, -3.0));
        }
    }

    public class AudioEventSeamTests
    {
        [Test]
        public void WorkVerbsMapToSfxWithToolUseFallback()
        {
            Assert.AreEqual(AudioEventSeam.SFX_WORK_WELD, AudioEventSeam.SfxForWorkVerb("WELD"));
            Assert.AreEqual(AudioEventSeam.SFX_CRAFT_COMPLETE, AudioEventSeam.SfxForWorkVerb("craft"));
            Assert.AreEqual(AudioEventSeam.SFX_TOOL_USE, AudioEventSeam.SfxForWorkVerb("juggle"));
            Assert.AreEqual(29, AudioEventSeam.ALL_SFX_IDS.Count);
            Assert.AreEqual(7, AudioEventSeam.ALL_BUS_IDS.Count);
        }
    }

    public class AudioLogTests
    {
        [Test]
        public void RegistryLooksUpEntriesInDeclarationOrder()
        {
            var log = new AudioLog();
            Assert.IsTrue(log.HasEntry("log.beacon_01"));
            Assert.AreEqual(5.0, log.GetEntry("log.beacon_01")["duration"]);
            Assert.IsTrue(log.GetEntry("missing").IsEmpty);
            GdArray ids = log.ListEntryIds();
            Assert.AreEqual(6, ids.Count);
            Assert.AreEqual("log.tutorial_calibrator", ids[5]);
            Assert.AreEqual(6, log.GetAllEntries().Count);
        }
    }
}
