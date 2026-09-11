// Ported from scripts/audio/audio_manager.gd @ 96ecb2b0 (the model-owning half; players/listener are an IAudioSink).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// The pure half of <c>AudioManager</c>: it owns the six audio models + the AudioLog, decides what plays, and keeps
    /// the <c>audio_summary</c> the run save carries. Everything that touched AudioStreamPlayer/AudioServer/AudioListener
    /// goes to <see cref="Sink"/> (null = headless: only the model state moves, exactly like the Godot headless path).
    /// </summary>
    public sealed class SessionAudio
    {
        /// <summary>Stream F: voice log successfully scheduled (decode_signal training).</summary>
        public event Action<string> VoiceLogPlayed;

        public IAudioSink Sink;

        public AudioBusConfig BusConfig = AudioBusConfig.MakeDefault();
        public AmbientZoneState AmbientZoneState = new AmbientZoneState();
        public SfxEventRouter SfxRouter = new SfxEventRouter();
        public DynamicMusicState MusicState = new DynamicMusicState();
        public SpatialAudioResolver SpatialResolver = new SpatialAudioResolver();
        public MetaEventState MetaEventState = new MetaEventState();
        public AudioLog AudioLog = new AudioLog();

        /// <summary>Last-played voice-log entry id.</summary>
        public string CurrentVoiceLogId = "";

        /// <summary>
        /// <c>_listener_anchor != null and is_instance_valid(_listener_anchor)</c>: set by <see cref="AttachListener"/>
        /// (the coordinator attaches the player each audio refresh), cleared by <see cref="DetachListener"/>.
        /// </summary>
        public bool ListenerAttached;

        /// <summary>SFX events played, in order (diagnostics for headless tests; the Godot router counts are in the summary).</summary>
        public readonly List<string> PlayedSfx = new List<string>();

        /// <summary><c>_ready()</c> (<c>_build_stream_players</c> is the Runtime's; bus volumes pushed; sub-models configured).</summary>
        public SessionAudio(IAudioSink sink = null)
        {
            Sink = sink;
            ApplyBusVolumes();
            InitializeSubModels();
        }

        void InitializeSubModels()
        {
            AmbientZoneState.Configure(new GdDict());
            SfxRouter.Configure(new GdDict());
            MusicState.Configure(new GdDict());
            SpatialResolver.Configure(new GdDict());
            MetaEventState.Configure(new GdDict());
        }

        void ApplyBusVolumes() => Sink?.ApplyBusVolumes(BusConfig);

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            bool changed = false;
            if (summary.Get("bus_config", null) is GdDict bus && BusConfig.ApplySummary(bus))
            {
                changed = true;
                ApplyBusVolumes();
            }
            if (summary.Get("ambient", null) is GdDict ambient && AmbientZoneState.ApplySummary(ambient))
                changed = true;
            if (summary.Get("sfx_router", null) is GdDict sfx && SfxRouter.ApplySummary(sfx))
                changed = true;
            if (summary.Get("music", null) is GdDict music && MusicState.ApplySummary(music))
                changed = true;
            if (summary.Get("spatial", null) is GdDict spatial && SpatialResolver.ApplySummary(spatial))
                changed = true;
            if (summary.Get("meta_event", null) is GdDict meta && MetaEventState.ApplySummary(meta))
                changed = true;
            return changed;
        }

        public GdDict GetSummary() => new GdDict
        {
            { "bus_config", BusConfig.GetSummary() },
            { "ambient", AmbientZoneState.GetSummary() },
            { "sfx_router", SfxRouter.GetSummary() },
            { "music", MusicState.GetSummary() },
            { "spatial", SpatialResolver.GetSummary() },
            { "meta_event", MetaEventState.GetSummary() },
            { "current_voice_log_id", CurrentVoiceLogId },
            { "listener_attached", ListenerAttached },
        };

        public void UpdateMusicFlags(bool engagement, bool hazardActive, bool vitalsCritical) =>
            MusicState.SetFlags(engagement, hazardActive, vitalsCritical);

        /// <summary>Per-frame tick. Returns the meta events that fired.</summary>
        public GdArray Tick(double deltaSeconds)
        {
            if (deltaSeconds <= 0.0)
                return new GdArray();
            AmbientZoneState.Tick(deltaSeconds);
            SfxRouter.Tick(deltaSeconds);
            MusicState.Tick(deltaSeconds);
            ApplyMusicLayerGains();
            GdArray due = MetaEventState.Tick(deltaSeconds);
            foreach (object evObj in due)
            {
                if (!(evObj is GdDict ev))
                    continue;
                string idStr = V.Str(ev.Get("id", ""));
                string catalogId = CatalogIdForMetaSchedule(idStr);
                if (catalogId.Length > 0)
                {
                    PlaySfx(catalogId);
                }
                else
                {
                    string busId = BusForMetaEventId(idStr);
                    double vol = V.F64(ev.Get("volume_db", -6.0));
                    PlayViaBus(busId, vol);
                }
                string voiceLogId = V.Str(ev.Get("voice_log_id", ""));
                if (voiceLogId.Length > 0)
                    PlayVoiceLog(voiceLogId);
            }
            return due;
        }

        /// <summary><c>play_sfx(event_id, position)</c>: route through the SfxEventRouter, then play spatially when a position is given.</summary>
        public bool PlaySfx(string eventId, Vec3? position = null)
        {
            GdDict route = SfxRouter.Route(eventId);
            if (route == null)
                return false;
            PlayedSfx.Add(eventId);
            string busId = V.Str(route.Get("bus", AudioEventSeam.BUS_SFX));
            double volDb = V.F64(route.Get("volume_db", -6.0));
            if (position.HasValue)
                Sink?.PlaySpatial(eventId, position.Value, busId, volDb);
            else
                PlayViaBus(busId, volDb, eventId);
            return true;
        }

        public GdArray DrainCaptions() => SfxRouter.GetPendingCaptions();

        public long PumpCaptions(Action<GdDict> captionTarget = null)
        {
            GdArray captions = DrainCaptions();
            if (captionTarget != null)
            {
                foreach (object caption in captions)
                {
                    if (caption is GdDict c)
                        captionTarget(c);
                }
            }
            return captions.Count;
        }

        /// <summary><c>apply_spatial_attenuation()</c>: 0 without a listener anchor.</summary>
        public long ApplySpatialAttenuation()
        {
            if (!ListenerAttached || Sink == null)
                return 0;
            return Sink.ApplySpatialAttenuation(SpatialResolver);
        }

        /// <summary><c>attach_listener(player)</c> + <c>update_listener_transform()</c>.</summary>
        public void AttachListener()
        {
            ListenerAttached = true;
            Sink?.AttachListenerToPlayer();
        }

        public void DetachListener() => ListenerAttached = false;

        public bool SetBusVolume(string busId, double volumeDb)
        {
            if (!BusConfig.SetVolumeDb(busId, volumeDb))
                return false;
            ApplyBusVolumes();
            return true;
        }

        public double GetBusVolume(string busId) => BusConfig.GetVolumeDb(busId);

        public bool SetBusMuted(string busId, bool muted)
        {
            if (!BusConfig.SetMuted(busId, muted))
                return false;
            ApplyBusVolumes();
            return true;
        }

        public bool IsBusMuted(string busId) => BusConfig.IsMuted(busId);

        public bool TransitionMusic(string targetState) => MusicState.OverrideState(targetState);

        public bool PlayVoiceLog(string entryId)
        {
            if (!AudioLog.HasEntry(entryId))
            {
                CoreServices.Log.Warning("AudioManager: unknown voice log entry '" + entryId + "'");
                return false;
            }
            GdDict entry = AudioLog.GetEntry(entryId);
            double volDb = V.F64(entry.Get("volume_db", -3.0));
            PlayViaBus(AudioEventSeam.BUS_VOICE, volDb, "", V.Str(entry.Get("clip_path", "")));
            CurrentVoiceLogId = entryId;
            VoiceLogPlayed?.Invoke(entryId);
            return true;
        }

        public bool TriggerMetaEvent(string eventId)
        {
            if (string.IsNullOrEmpty(eventId))
                return false;
            string busId = BusForMetaEventId(eventId);
            double vol = eventId == AudioEventSeam.META_BEACON_DISTRESS ? -3.0 : -6.0;
            PlayViaBus(busId, vol);
            return true;
        }

        void PlayViaBus(string busId, double volumeDb, string eventId = "", string streamPath = "") =>
            Sink?.PlayOnBus(busId, volumeDb, eventId ?? "", streamPath ?? "");

        void ApplyMusicLayerGains()
        {
            GdDict gains = MusicState.GetLayerGains();
            double combined = 0.0;
            foreach (object layerId in AudioEventSeam.ALL_MUSIC_LAYERS)
                combined = Math.Max(combined, V.F64(gains.Get(layerId, 0.0)));
            Sink?.SetMusicVolumeDb(-24.0 + combined * 24.0);
        }

        static string CatalogIdForMetaSchedule(string idStr)
        {
            if (idStr == AudioEventSeam.META_EVENT_BEACON || idStr == AudioEventSeam.META_BEACON_DISTRESS)
                return AudioEventSeam.META_BEACON_DISTRESS;
            if (idStr == AudioEventSeam.META_EVENT_PULSE || idStr == AudioEventSeam.META_BIOMATTER_PULSE)
                return AudioEventSeam.META_BIOMATTER_PULSE;
            if (idStr == AudioEventSeam.META_EVENT_GROAN || idStr == AudioEventSeam.META_HULL_GROAN)
                return AudioEventSeam.META_HULL_GROAN;
            if (idStr == AudioEventSeam.META_REACTOR_HUM)
                return AudioEventSeam.META_REACTOR_HUM;
            return "";
        }

        static string BusForMetaEventId(string idStr)
        {
            if (idStr == AudioEventSeam.META_BEACON_DISTRESS || idStr == AudioEventSeam.META_BIOMATTER_PULSE
                || idStr == AudioEventSeam.META_HULL_GROAN || idStr == AudioEventSeam.META_REACTOR_HUM)
                return AudioEventSeam.BUS_META;
            if (idStr == AudioEventSeam.META_EVENT_BEACON || idStr == AudioEventSeam.META_EVENT_PULSE || idStr == AudioEventSeam.META_EVENT_GROAN)
                return AudioEventSeam.BUS_META;
            return AudioEventSeam.BUS_SFX;
        }
    }
}
