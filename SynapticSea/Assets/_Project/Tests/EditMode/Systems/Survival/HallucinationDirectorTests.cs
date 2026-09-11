using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class HallucinationDirectorTests
    {
        static GdArray Anchors() => GdArray.Of(new Vec3(1f, 0f, 0f), new Vec3(0f, 0f, 2f), new Vec3(3f, 0f, 3f));

        static GdDict Ctx(double sanity, bool safe = false, GdArray anchors = null) => new GdDict
        {
            { "sanity", sanity },
            { "in_safe_zone", safe },
            { "anchor_positions", anchors ?? Anchors() },
        };

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var r = new HallucinationDirector();
            r.Configure(new GdDict { { "seed", 11L } });
            for (int i = 0; i < 20; i++) r.Tick(0.3, Ctx(12.0));
            GdDict summary = r.GetSummary();
            Assert.IsTrue(summary.GetBool("pool_loaded"));
            Assert.IsNotEmpty(summary.GetArray("active_events"));

            var r2 = new HallucinationDirector();
            r2.Configure(new GdDict { { "seed", 0L } });
            Assert.IsTrue(r2.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, r2.GetSummary()), r2.GetSummary().ToString());
        }

        [Test]
        public void TiersGateKinds_AndSafeZoneClearsEverything()
        {
            var d = new HallucinationDirector();
            d.Configure(new GdDict { { "seed", 7L } });
            d.Tick(0.1, Ctx(90.0)); Assert.AreEqual(0L, d.GetTier());
            d.Tick(0.1, Ctx(35.0)); Assert.AreEqual(1L, d.GetTier());
            d.Tick(0.1, Ctx(20.0)); Assert.AreEqual(2L, d.GetTier());
            d.Tick(0.1, Ctx(10.0)); Assert.AreEqual(3L, d.GetTier());

            var g = new HallucinationDirector();
            g.Configure(new GdDict { { "seed", 3L } });
            for (int i = 0; i < 400; i++) g.Tick(0.5, Ctx(35.0));
            Assert.IsEmpty(g.GetActiveEvents("hud"));
            Assert.IsEmpty(g.GetActiveEvents("phantom"));
            Assert.IsNotEmpty(g.GetActiveEvents("ambient"));
            g.Tick(0.5, Ctx(10.0, safe: true));
            Assert.IsEmpty(g.GetActiveEvents());
            Assert.AreEqual(0L, g.GetTier());
            Assert.AreEqual(0.0, g.GetDirectTeeth().GetFloat("health_drain_per_second"));
            Assert.AreEqual(0.0, g.GetFxIntensity());
        }

        [Test]
        public void SameSeedIsDeterministic_AndSpawnTimersResumeAfterRestore()
        {
            var a = new HallucinationDirector();
            var b = new HallucinationDirector();
            a.Configure(new GdDict { { "seed", 42L } });
            b.Configure(new GdDict { { "seed", 42L } });
            for (int i = 0; i < 60; i++)
            {
                a.Tick(0.25, Ctx(10.0));
                b.Tick(0.25, Ctx(10.0));
            }
            Assert.IsTrue(V.VariantEquals(a.GetSummary(), b.GetSummary()));

            var src = new HallucinationDirector();
            src.Configure(new GdDict { { "seed", 13L } });
            src.Tick(5.5, Ctx(35.0));
            GdDict summary = src.GetSummary();
            Assert.Greater(summary.GetDict("spawn_timers").GetFloat("ambient"), 5.0);
            var dst = new HallucinationDirector();
            dst.Configure(new GdDict { { "seed", 0L } });
            Assert.IsTrue(dst.ApplySummary(summary));
            dst.Tick(0.6, Ctx(35.0));
            Assert.IsNotEmpty(dst.GetActiveEvents("ambient"));
        }
    }
}
