using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class AmbientZoneStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var model = new AmbientZoneState();
            model.Configure(new GdDict { { "crossfade_seconds", 2.0 }, { "initial_role", AudioEventSeam.ROOM_ROLE_CARGO }, { "initial_threat", 0.8 } });
            model.SetRoomRole(AudioEventSeam.ROOM_ROLE_ENGINE);
            model.Tick(0.5);
            GdDict summary = model.GetSummary();

            var restored = new AmbientZoneState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()), restored.GetSummary().ToString());
            Assert.IsFalse(restored.ApplySummary(new GdDict { { "kind", "other" } }));
        }

        [Test]
        public void CrossfadeBlendsLayers_ThreatBoostsAboveThreshold_UnknownRoleIgnored()
        {
            var model = new AmbientZoneState();
            model.Configure(new GdDict());
            model.SetRoomRole(AudioEventSeam.ROOM_ROLE_ENGINE);
            Assert.IsTrue(model.IsCrossfadeActive());
            Assert.IsFalse(model.Tick(0.75));
            GdDict gains = model.GetLayerGains();
            Assert.AreEqual(0.5, gains.GetFloat("current_gain"), 1e-9);
            Assert.AreEqual(0.5, gains.GetFloat("previous_gain"), 1e-9);
            Assert.IsTrue(model.Tick(0.75));
            Assert.AreEqual(AudioEventSeam.AMB_ENGINE, model.GetCurrentTrackId());

            model.SetThreatLevel(1.0);
            Assert.AreEqual(1.25, model.GetLayerGains().GetFloat("threat_multiplier"), 1e-9);

            var log = new CollectingLog();
            ILog previous = CoreServices.Log;
            CoreServices.Log = log;
            try { model.SetRoomRole("bogus"); }
            finally { CoreServices.Log = previous; }
            Assert.AreEqual(AudioEventSeam.ROOM_ROLE_ENGINE, model.GetCurrentRole());
            Assert.AreEqual(1, log.Warnings.Count);
        }
    }

    public class AudioBusConfigTests
    {
        [Test]
        public void Summary_RoundTripsThroughDefaultResource()
        {
            var cfg = AudioBusConfig.MakeDefault();
            Assert.IsTrue(cfg.IsValidated());
            Assert.IsTrue(cfg.SetVolumeDb(AudioEventSeam.BUS_MUSIC, -12.0));
            Assert.IsTrue(cfg.SetMuted(AudioEventSeam.BUS_VOICE, true));
            GdDict summary = cfg.GetSummary();

            var restored = AudioBusConfig.LoadDefaultResource();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()), restored.GetSummary().ToString());
            Assert.IsTrue(restored.IsMuted(AudioEventSeam.BUS_VOICE));
        }

        [Test]
        public void TresMatchesDefaults_AndValidationRejectsBadLayouts()
        {
            var tres = AudioBusConfig.LoadDefaultResource();
            Assert.IsTrue(tres.Validate());
            Assert.IsTrue(V.VariantEquals(AudioBusConfig.MakeDefault().Buses, tres.Buses));
            Assert.AreEqual(-9.0, tres.GetVolumeDb(AudioEventSeam.BUS_AMBIENT));
            Assert.IsFalse(tres.SetVolumeDb(AudioEventSeam.BUS_SFX, 3.0), "out of range");
            Assert.IsFalse(tres.SetVolumeDb("nope", -3.0));

            var bad = AudioBusConfig.MakeDefault();
            ((GdDict)bad.Buses[1])["parent_id"] = "music";
            Assert.IsFalse(bad.Validate(false));
            bad.Buses.RemoveAt(0);
            Assert.IsFalse(bad.Validate(false));
            Assert.IsFalse(bad.IsValidated());
        }
    }

    public class DynamicMusicStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var model = new DynamicMusicState();
            model.Configure(new GdDict { { "crossfade_seconds", 2.0 } });
            model.SetFlags(true, true, false);
            model.Tick(0.5);
            GdDict summary = model.GetSummary();

            var restored = new DynamicMusicState();
            restored.Configure(new GdDict());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()), restored.GetSummary().ToString());
        }

        [Test]
        public void PriorityResolution_AndLayersConvergeToTargets()
        {
            var model = new DynamicMusicState();
            model.Configure(new GdDict());
            model.SetFlags(false, true, false);
            Assert.AreEqual(AudioEventSeam.MUSIC_STATE_TENSION, model.GetState());
            model.SetFlags(true, true, true);
            Assert.AreEqual(AudioEventSeam.MUSIC_STATE_CRITICAL, model.GetState());
            Assert.IsTrue(model.Tick(1.0));
            Assert.AreEqual(0.65, model.GetLayerGains().GetFloat(AudioEventSeam.MUSIC_LAYER_BASE), 1e-9);
            for (int i = 0; i < 200; i++) model.Tick(0.5);
            Assert.IsTrue(V.VariantEquals(model.GetTargetGains(), model.GetLayerGains()));
            Assert.IsFalse(model.OverrideState("NOPE", false));
        }
    }

    public class MetaEventStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var meta = new MetaEventState();
            meta.Configure(new GdDict());
            meta.Tick(20.0);
            GdDict summary = meta.GetSummary();

            var restored = new MetaEventState();
            restored.Configure(new GdDict());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()), restored.GetSummary().ToString());
        }

        [Test]
        public void DefaultScheduleFiresInOrderOnce_AndSeedOffsetsDeterministically()
        {
            var meta = new MetaEventState();
            meta.Configure(new GdDict());
            GdArray due1 = meta.Tick(20.0);
            Assert.AreEqual(1, due1.Count);
            Assert.AreEqual(AudioEventSeam.META_EVENT_BEACON, ((GdDict)due1[0]).GetString("id"));
            Assert.AreEqual(1, meta.Tick(20.0).Count);
            Assert.AreEqual(1, meta.Tick(20.0).Count);
            Assert.AreEqual(0, meta.Tick(10.0).Count);
            Assert.AreEqual(3L, meta.GetFiredCount());

            var seeded = new MetaEventState();
            seeded.Configure(new GdDict { { "run_seed", 7L } }); // 7 % 7 == 0 -> no offset
            Assert.AreEqual(12.0, ((GdDict)seeded.GetPendingEvents()[0]).GetFloat("trigger_time"));
            seeded.Configure(new GdDict { { "run_seed", 9L } }); // 9 % 7 == 2 -> +1.0s
            Assert.AreEqual(13.0, ((GdDict)seeded.GetPendingEvents()[0]).GetFloat("trigger_time"));
        }
    }

    public class SfxEventRouterTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var router = new SfxEventRouter();
            router.Configure(new GdDict { { "caption_duration", 3.0 } });
            router.Route(AudioEventSeam.SFX_TOOL_PICKUP);
            router.Route("nope", false);
            router.Tick(0.2);
            GdDict summary = router.GetSummary();

            var restored = new SfxEventRouter();
            restored.Configure(new GdDict());
            Assert.IsTrue(restored.ApplySummary(summary));
            GdDict restoredSummary = restored.GetSummary();
            // caption_queue_size is derived from the (unsaved) live caption queue, as in Godot.
            restoredSummary["caption_queue_size"] = summary["caption_queue_size"];
            Assert.IsTrue(V.VariantEquals(summary, restoredSummary), restoredSummary.ToString());
        }

        [Test]
        public void RoutesToBus_CooldownSuppresses_UnknownIdsDropped()
        {
            var router = new SfxEventRouter();
            router.Configure(new GdDict());
            GdDict routed = router.Route(AudioEventSeam.UI_VITALS_LOW);
            Assert.AreEqual(AudioEventSeam.BUS_UI, routed.GetString("bus"));
            Assert.AreEqual(-3.0, routed.GetFloat("volume_db"));
            Assert.IsNull(router.Route(AudioEventSeam.UI_VITALS_LOW), "4s cooldown");
            router.Tick(4.0);
            Assert.IsNotNull(router.Route(AudioEventSeam.UI_VITALS_LOW));
            Assert.IsNull(router.Route("sfx.not.real", false));
            Assert.AreEqual(2L, router.GetDroppedCount());
            Assert.AreEqual(2L, router.GetRoutedCount(AudioEventSeam.UI_VITALS_LOW));
            Assert.AreEqual(1, router.GetPendingCaptions().Count, "the first caption expired after 2.5s");
            Assert.AreEqual(AudioEventSeam.BUS_SFX, SfxEventRouter.GetBusForEvent("unknown"));
        }
    }
}
