// Ported from scripts/audio/audio_manager.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Audio service owned by the playable session (ADR-0029; not a singleton). One 2D <see cref="AudioSource"/> per bus
    /// (sfx, music, voice, ui, ambient, meta), a per-event pool of 3D sources, and an <see cref="AudioListener"/> that
    /// follows the player anchor. The six pure audio models own all decisions; this class only applies them.
    ///
    /// Buses: Godot routed every bus through AudioServer's Master with per-bus dB and mute. With no AudioMixer asset,
    /// each source's linear volume is computed as dB(own) + dB(bus) + dB(master), silenced when the bus or master is
    /// muted — the same result for the volume/mute-only bus layout the game used.
    /// Spatial: logarithmic rolloff with minDistance 10 m reproduces AudioStreamPlayer3D's default inverse-distance
    /// attenuation (unit_size 10); the resolver's per-frame dB adjustment is applied on top, as in Godot.
    /// </summary>
    public sealed class AudioManager : MonoBehaviour
    {
        /// <summary>Event/layer id → Godot resource path (kept here, like Godot, so the router stays pure; ADR-0044).</summary>
        public static readonly IReadOnlyDictionary<string, string> StreamCatalog = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "sfx.tool.pickup", "res://data/audio/sfx/tool_pickup.wav" },
            { "sfx.footstep", "res://data/audio/sfx/footstep.wav" },
            { "ui.panel.open", "res://data/audio/ui/panel_open.wav" },
            { "ui.panel.close", "res://data/audio/ui/panel_close.wav" },
            { "sfx.fire.crackle", "res://data/audio/sfx/fire_crackle.wav" },
            { "meta.hull.groan", "res://assets/audio/hull_groan.wav" },
            { "sfx.combat.hit", "res://data/audio/sfx/combat_hit.wav" },
            { "sfx.combat.threat_alert", "res://data/audio/sfx/threat_alert.wav" },
            { "sfx.door.open", "res://data/audio/sfx/door_open.wav" },
            { "sfx.door.close", "res://data/audio/sfx/door_close.wav" },
            { "sfx.dock.land", "res://data/audio/sfx/dock_land.wav" },
            { "ui.vitals.low", "res://data/audio/sfx/vitals_low.wav" },
            { "layer.base", "res://data/audio/music/exploration_base.wav" },
            { "layer.tension_drone", "res://data/audio/music/tension_drone.wav" },
            { "layer.critical_pad", "res://data/audio/music/critical_pad.wav" },

            // Godot's unreferenced slice clips (assets/audio, commit a779fac6), mapped by name and sound (docs/port-status.md).
            { "sfx.work.weld", "res://assets/audio/weld.wav" },
            { "sfx.work.cut", "res://assets/audio/cut.wav" },
            { "sfx.work.patch", "res://assets/audio/patch.wav" },
            { "sfx.work.unbolt", "res://assets/audio/unbolt_pry.wav" },
            { "sfx.work.pry", "res://assets/audio/unbolt_pry.wav" },
            { "sfx.work.mount", "res://assets/audio/unbolt_pry.wav" },
            { "sfx.work.splice", "res://assets/audio/tool_use.wav" },
            { "sfx.work.harvest", "res://assets/audio/pickup.wav" },
            { "sfx.work.plant", "res://assets/audio/drop.wav" },
            { "sfx.drop.item", "res://assets/audio/drop.wav" },
            { "sfx.tool.use", "res://assets/audio/tool_use.wav" },
            { "sfx.suit.breath", "res://assets/audio/suit_breath.wav" },
            { "sfx.arc.zap", "res://assets/audio/weld.wav" },
            { "sfx.wound.bandage", "res://assets/audio/patch.wav" },
            { "sfx.wound.treat", "res://assets/audio/patch.wav" },
            { "sfx.craft.complete", "res://assets/audio/dock_land.wav" },
            { "sfx.repair.complete", "res://assets/audio/door_close.wav" },
            { "meta.reactor.hum", "res://assets/audio/reactor_hum.wav" },
            { "meta.biomatter.pulse", "res://assets/audio/biomatter_pulse.wav" },
            { "ui.inventory.open", "res://data/audio/ui/panel_open.wav" },
            { "ui.inventory.close", "res://data/audio/ui/panel_close.wav" },
            { "ui.wounds.open", "res://data/audio/ui/panel_open.wav" },
            { "ui.ship_mod.open", "res://data/audio/ui/panel_open.wav" },
            { "ui.ship_mod.install", "res://assets/audio/unbolt_pry.wav" },
            { "ui.ship_mod.uninstall", "res://assets/audio/unbolt_pry.wav" },
            { "amb.docking", "res://assets/audio/ambient_docking.wav" },
            { "amb.engine", "res://assets/audio/ambient_reactor.wav" },
            { "amb.cargo", "res://assets/audio/ambient_engineering.wav" },
            { "amb.med_bay", "res://assets/audio/ambient_medical.wav" },
            { "amb.crew_quarters", "res://assets/audio/ambient_corridor.wav" },
        };

        /// <summary>Music stems in layer order; a layer without a stream (combat percussion) keeps a silent source.</summary>
        public static readonly string[] MusicStemLayers =
        {
            AudioEventSeam.MUSIC_LAYER_BASE, AudioEventSeam.MUSIC_LAYER_TENSION_DRONE,
            AudioEventSeam.MUSIC_LAYER_COMBAT_PERCUSSION, AudioEventSeam.MUSIC_LAYER_CRITICAL_PAD,
        };

        /// <summary>Stem gain curve: Godot's collapsed music level (-24 dB at gain 0, 0 dB at gain 1); silent at (near) zero gain.</summary>
        public const double MusicStemFloorDb = -24.0;
        public const double SilentGain = 0.001;
        public const double SilentDb = -80.0;

        /// <summary>DSP lead time so every stem is scheduled on the same future sample.</summary>
        public const double StemScheduleLeadSeconds = 0.1;

        /// <summary>Stream F: a voice log was scheduled (decode_signal training).</summary>
        public event Action<string> VoiceLogPlayed;

        [SerializeField] AudioCatalog catalog;

        public AudioBusConfig BusConfig { get; private set; } = AudioBusConfig.MakeDefault();
        public AmbientZoneState AmbientZoneState { get; } = new AmbientZoneState();
        public SfxEventRouter SfxRouter { get; } = new SfxEventRouter();
        public DynamicMusicState MusicState { get; } = new DynamicMusicState();
        public SpatialAudioResolver SpatialResolver { get; } = new SpatialAudioResolver();
        public MetaEventState MetaEventState { get; } = new MetaEventState();
        public AudioLog AudioLog { get; } = new AudioLog();

        public string CurrentVoiceLogId { get; private set; } = "";

        readonly Dictionary<string, AudioSource> _busPlayers = new Dictionary<string, AudioSource>(StringComparer.Ordinal);
        readonly Dictionary<AudioSource, (string bus, double db)> _sourceLevels = new Dictionary<AudioSource, (string, double)>();
        readonly Dictionary<string, List<AudioSource>> _spatialPool = new Dictionary<string, List<AudioSource>>(StringComparer.Ordinal);
        readonly HashSet<string> _warnedMissingPaths = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, AudioSource> _musicStems = new Dictionary<string, AudioSource>(StringComparer.Ordinal);
        AudioSource _ambientCurrent;
        AudioSource _ambientPrevious;
        AudioListener _listener;
        Transform _listenerAnchor;
        bool _headless;
        bool _initialized;

        /// <summary>The music model whose per-layer gains drive the stems (the session's; null = <see cref="MusicState"/>).</summary>
        public DynamicMusicState MusicGainSource { get; set; }

        /// <summary>The ambient model whose role track and crossfade drive the ambient beds (the session's; null = <see cref="AmbientZoneState"/>).</summary>
        public AmbientZoneState AmbientSource { get; set; }

        /// <summary>True once the stems were scheduled (clips assigned; playback is skipped in batch mode).</summary>
        public bool MusicStemsStarted { get; private set; }

        /// <summary>The DSP time every stem was scheduled to start on.</summary>
        public double MusicStemStartDspTime { get; private set; }

        /// <summary>Last level the session pushed through <see cref="SetMusicVolumeDb"/> (Godot's collapsed max-layer level).</summary>
        public double SessionMusicLevelDb { get; private set; } = MusicStemFloorDb;

        public AudioCatalog Catalog
        {
            get => catalog;
            set => catalog = value;
        }

        void Awake() => Initialize();

        /// <summary>Godot <c>_ready</c>: build players, push bus volumes, configure the six models. Idempotent.</summary>
        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _headless = Application.isBatchMode;
            if (catalog == null) catalog = Resources.Load<AudioCatalog>("Catalogs/AudioCatalog");
            BuildStreamPlayers();
            ApplyBusVolumes();
            AmbientZoneState.Configure(new GdDict());
            SfxRouter.Configure(new GdDict());
            MusicState.Configure(new GdDict());
            SpatialResolver.Configure(new GdDict());
            MetaEventState.Configure(new GdDict());
        }

        void OnDestroy()
        {
            foreach (var p in _busPlayers.Values)
                if (p != null) { p.Stop(); p.clip = null; }
            foreach (var p in _musicStems.Values)
                if (p != null) { p.Stop(); p.clip = null; }
            foreach (var p in new[] { _ambientCurrent, _ambientPrevious })
                if (p != null) { p.Stop(); p.clip = null; }
            foreach (var pool in _spatialPool.Values)
                foreach (var p in pool)
                    if (p != null) { p.Stop(); p.clip = null; }
        }

        void BuildStreamPlayers()
        {
            foreach (object busObj in AudioEventSeam.ALL_BUS_IDS)
            {
                string busId = V.Str(busObj);
                if (busId == AudioEventSeam.BUS_MASTER) continue;
                var go = new GameObject("AudioStreamPlayer_" + busId);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f;
                _busPlayers[busId] = src;
                _sourceLevels[src] = (busId, 0.0);
            }
            foreach (string layer in MusicStemLayers) _musicStems[layer] = MakeLoopSource("MusicStem_" + layer, AudioEventSeam.BUS_MUSIC);
            _ambientCurrent = MakeLoopSource("AmbientBed_Current", AudioEventSeam.BUS_AMBIENT);
            _ambientPrevious = MakeLoopSource("AmbientBed_Previous", AudioEventSeam.BUS_AMBIENT);
        }

        AudioSource MakeLoopSource(string name, string busId)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.loop = true;
            _sourceLevels[src] = (busId, SilentDb);
            return src;
        }

        // ------------------------------------------------------------------ bus volumes

        void ApplyBusVolumes()
        {
            foreach (var kv in _sourceLevels) Apply(kv.Key);
        }

        void Apply(AudioSource src)
        {
            if (src == null || !_sourceLevels.TryGetValue(src, out var level)) return;
            bool muted = BusConfig.IsMuted(level.bus) || BusConfig.IsMuted(AudioEventSeam.BUS_MASTER);
            double db = level.db + BusConfig.GetVolumeDb(level.bus) + BusConfig.GetVolumeDb(AudioEventSeam.BUS_MASTER);
            src.volume = muted ? 0f : Mathf.Clamp01((float)Math.Pow(10.0, db / 20.0));
        }

        void SetSourceDb(AudioSource src, string bus, double db)
        {
            _sourceLevels[src] = (bus, db);
            Apply(src);
        }

        public bool SetBusVolume(string busId, double volumeDb)
        {
            if (!BusConfig.SetVolumeDb(busId, volumeDb)) return false;
            ApplyBusVolumes();
            return true;
        }

        public double GetBusVolume(string busId) => BusConfig.GetVolumeDb(busId);

        public bool SetBusMuted(string busId, bool muted)
        {
            if (!BusConfig.SetMuted(busId, muted)) return false;
            ApplyBusVolumes();
            return true;
        }

        public bool IsBusMuted(string busId) => BusConfig.IsMuted(busId);

        // ------------------------------------------------------------------ summaries

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            if (summary.Get("bus_config") is GdDict bus && BusConfig.ApplySummary(bus))
            {
                changed = true;
                ApplyBusVolumes();
            }
            if (summary.Get("ambient") is GdDict ambient && AmbientZoneState.ApplySummary(ambient)) changed = true;
            if (summary.Get("sfx_router") is GdDict sfx && SfxRouter.ApplySummary(sfx)) changed = true;
            if (summary.Get("music") is GdDict music && MusicState.ApplySummary(music)) changed = true;
            if (summary.Get("spatial") is GdDict spatial && SpatialResolver.ApplySummary(spatial)) changed = true;
            if (summary.Get("meta_event") is GdDict meta && MetaEventState.ApplySummary(meta)) changed = true;
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
            { "listener_attached", _listenerAnchor != null },
        };

        // ------------------------------------------------------------------ per-frame (driven by the session)

        public void UpdateMusicFlags(bool engagement, bool hazardActive, bool vitalsCritical) =>
            MusicState.SetFlags(engagement, hazardActive, vitalsCritical);

        /// <summary>Advances crossfades, SFX cooldowns/captions, and the meta-event scheduler; returns fired meta events.</summary>
        public GdArray Tick(double deltaSeconds)
        {
            if (deltaSeconds <= 0.0) return new GdArray();
            AmbientZoneState.Tick(deltaSeconds);
            SfxRouter.Tick(deltaSeconds);
            MusicState.Tick(deltaSeconds);
            ApplyMusicLayerGains();
            GdArray due = MetaEventState.Tick(deltaSeconds);
            foreach (object evObj in due)
            {
                if (!(evObj is GdDict ev)) continue;
                string id = ev.GetString("id");
                string catalogId = CatalogIdForMetaSchedule(id);
                if (catalogId.Length > 0) PlaySfx(catalogId);
                else PlayViaBus(BusForMetaEventId(id), ev.GetFloat("volume_db", -6.0));
                string voiceLogId = ev.GetString("voice_log_id");
                if (voiceLogId.Length > 0) PlayVoiceLog(voiceLogId);
            }
            return due;
        }

        /// <summary>Fire a named SFX event; spatial when a Unity world position is given.</summary>
        public bool PlaySfx(string eventId, Vector3? unityPosition = null)
        {
            GdDict route = SfxRouter.Route(eventId);
            if (route == null) return false;
            string bus = route.GetString("bus", AudioEventSeam.BUS_SFX);
            double volDb = route.GetFloat("volume_db", -6.0);
            if (unityPosition.HasValue) PlaySpatial(eventId, unityPosition.Value, bus, volDb);
            else PlayViaBus(bus, volDb, eventId);
            return true;
        }

        public GdArray DrainCaptions() => SfxRouter.GetPendingCaptions();

        public int PumpCaptions(Action<object> captionTarget = null)
        {
            GdArray captions = DrainCaptions();
            if (captionTarget != null) foreach (object c in captions) captionTarget(c);
            return captions.Count;
        }

        /// <summary>Resolver attenuation for live spatial sources relative to the listener anchor.</summary>
        public int ApplySpatialAttenuation()
        {
            if (_listenerAnchor == null) return 0;
            Vec3 listener = Frame.ToGodot(_listenerAnchor.position);
            int touched = 0;
            foreach (var kv in _spatialPool)
            {
                foreach (var src in kv.Value)
                {
                    if (src == null) continue;
                    Vec3 emitter = Frame.ToGodot(src.transform.position);
                    bool occluded = IsOccluded(emitter, listener);
                    double baseDb = SfxEventRouter.GetVolumeForEvent(kv.Key);
                    SetSourceDb(src, _sourceLevels[src].bus, SpatialResolver.ResolveVolumeDb(emitter, listener, occluded, baseDb));
                    touched++;
                }
            }
            return touched;
        }

        public void UpdateListenerTransform()
        {
            if (_listenerAnchor == null) return;
            if (_listener == null)
            {
                var go = new GameObject("AudioListener");
                go.transform.SetParent(_listenerAnchor, false);
                _listener = go.AddComponent<AudioListener>();
            }
        }

        public bool TransitionMusic(string targetState) => MusicState.OverrideState(targetState);

        public bool PlayVoiceLog(string entryId)
        {
            if (!AudioLog.HasEntry(entryId))
            {
                CoreServices.Log.Warning($"AudioManager: unknown voice log entry '{entryId}'");
                return false;
            }
            GdDict entry = AudioLog.GetEntry(entryId);
            PlayViaBus(AudioEventSeam.BUS_VOICE, entry.GetFloat("volume_db", -3.0), "", entry.GetString("clip_path"));
            CurrentVoiceLogId = entryId;
            VoiceLogPlayed?.Invoke(entryId);
            return true;
        }

        public bool TriggerMetaEvent(string eventId)
        {
            if (string.IsNullOrEmpty(eventId)) return false;
            double vol = eventId == AudioEventSeam.META_BEACON_DISTRESS ? -3.0 : -6.0;
            PlayViaBus(BusForMetaEventId(eventId), vol);
            return true;
        }

        public void AttachListener(Transform anchor)
        {
            _listenerAnchor = anchor;
            UpdateListenerTransform();
        }

        public Transform GetListenerAnchor() => _listenerAnchor;

        // ------------------------------------------------------------------ session sink (RunSession owns the models)

        /// <summary>Godot <c>_apply_bus_volumes</c> with the session's bus model (the session's SessionAudio is the source of truth).</summary>
        public void ApplyBusConfig(AudioBusConfig busConfig)
        {
            if (busConfig != null) BusConfig = busConfig;
            ApplyBusVolumes();
        }

        /// <summary>Godot <c>_play_via_bus</c>; <paramref name="streamPath"/> wins over the stream catalog lookup.</summary>
        public void PlayOnBus(string busId, double volumeDb, string eventId, string streamPath) => PlayViaBus(busId, volumeDb, eventId ?? "", streamPath ?? "");

        /// <summary>Godot <c>_play_spatial</c> at a Unity world position.</summary>
        public void PlaySpatialAt(string eventId, Vector3 unityPosition, string busId, double volumeDb) => PlaySpatial(eventId, unityPosition, busId, volumeDb);

        /// <summary>
        /// Godot <c>_apply_music_layer_gains</c> for the session sink. Godot collapsed the four layers into one player at
        /// <paramref name="volumeDb"/> (the loudest layer); here the stems start sample-aligned on the first call and each
        /// follows its own layer gain from <see cref="MusicGainSource"/>. The ambient beds are refreshed on the same beat.
        /// </summary>
        public void SetMusicVolumeDb(double volumeDb)
        {
            SessionMusicLevelDb = volumeDb;
            ApplyMusicStemGains();
            ApplyAmbientBeds();
        }

        /// <summary>Points the stems and ambient beds at the session's models (RunSession.AudioManager owns them).</summary>
        public void BindSessionModels(DynamicMusicState music, AmbientZoneState ambient)
        {
            MusicGainSource = music;
            AmbientSource = ambient;
        }

        /// <summary>Schedules every stem that has a clip on one shared DSP start time (idempotent).</summary>
        public bool StartMusicStems()
        {
            if (MusicStemsStarted) return true;
            var ready = new List<AudioSource>();
            foreach (string layer in MusicStemLayers)
            {
                if (!StreamCatalog.TryGetValue(layer, out string path)) continue;
                AudioClip clip = LoadClip(path);
                if (clip == null) continue;
                AudioSource stem = _musicStems[layer];
                stem.clip = clip;
                stem.loop = true;
                ready.Add(stem);
            }
            if (ready.Count == 0) return false;
            MusicStemStartDspTime = AudioSettings.dspTime + StemScheduleLeadSeconds;
            if (!_headless)
                foreach (AudioSource stem in ready) stem.PlayScheduled(MusicStemStartDspTime);
            MusicStemsStarted = true;
            return true;
        }

        /// <summary>The stem source for a music layer (null for unknown layers).</summary>
        public AudioSource GetMusicStem(string layerId) => _musicStems.TryGetValue(layerId ?? "", out var p) ? p : null;

        /// <summary>Stem dB for a layer gain: <see cref="MusicStemFloorDb"/> + gain * 24, silent at (near) zero gain.</summary>
        public static double StemDbForGain(double gain) =>
            gain <= SilentGain ? SilentDb : MusicStemFloorDb + GdMath.Clampf(gain, 0.0, 1.0) * -MusicStemFloorDb;

        /// <summary>The source's own dB before bus and master (stems, beds, bus players, spatial sources).</summary>
        public double GetSourceDb(AudioSource src) => src != null && _sourceLevels.TryGetValue(src, out var level) ? level.db : SilentDb;

        void ApplyMusicStemGains()
        {
            StartMusicStems();
            GdDict gains = (MusicGainSource ?? MusicState).GetLayerGains();
            foreach (string layer in MusicStemLayers)
            {
                AudioSource stem = _musicStems[layer];
                SetSourceDb(stem, AudioEventSeam.BUS_MUSIC, stem.clip == null ? SilentDb : StemDbForGain(gains.GetFloat(layer, 0.0)));
            }
        }

        /// <summary>Current / previous ambient bed of the role crossfade, each at crossfade gain * role intensity * threat multiplier.</summary>
        void ApplyAmbientBeds()
        {
            AmbientZoneState model = AmbientSource ?? AmbientZoneState;
            GdDict gains = model.GetLayerGains();
            double threat = gains.GetFloat("threat_multiplier", 1.0);
            string previousTrack = model.GetSummary().GetString("previous_track_id");
            // A role change hands the playing bed to the previous-role source, so it fades out without restarting.
            AudioClip previousClip = ClipForTrack(previousTrack);
            if (previousClip != null && _ambientCurrent.clip == previousClip && _ambientPrevious.clip != previousClip)
                (_ambientCurrent, _ambientPrevious) = (_ambientPrevious, _ambientCurrent);
            ApplyAmbientBed(_ambientCurrent, model.GetCurrentTrackId(), gains.GetFloat("current_gain", 1.0) * gains.GetFloat("current_intensity", 0.0) * threat);
            ApplyAmbientBed(_ambientPrevious, previousTrack, gains.GetFloat("previous_gain", 0.0) * gains.GetFloat("previous_intensity", 0.0) * threat);
        }

        void ApplyAmbientBed(AudioSource src, string trackId, double linearGain)
        {
            if (src == null) return;
            AudioClip clip = ClipForTrack(trackId);
            if (clip == null)
            {
                SetSourceDb(src, AudioEventSeam.BUS_AMBIENT, SilentDb);
                src.Stop();
                src.clip = null;
                return;
            }
            if (src.clip != clip || (!_headless && !src.isPlaying))
            {
                src.clip = clip;
                if (!_headless) src.Play();
            }
            SetSourceDb(src, AudioEventSeam.BUS_AMBIENT, linearGain <= SilentGain ? SilentDb : 20.0 * Math.Log10(linearGain));
        }

        AudioClip ClipForTrack(string trackId) =>
            !string.IsNullOrEmpty(trackId) && StreamCatalog.TryGetValue(trackId, out string path) ? LoadClip(path) : null;

        /// <summary>The ambient bed sources: 0 = current role, 1 = previous role (crossfading out).</summary>
        public AudioSource GetAmbientBed(int index) => index == 0 ? _ambientCurrent : index == 1 ? _ambientPrevious : null;

        /// <summary><c>apply_spatial_attenuation</c> resolved with the session's resolver.</summary>
        public int ApplySpatialAttenuation(SpatialAudioResolver resolver)
        {
            if (_listenerAnchor == null || resolver == null) return 0;
            Vec3 listener = Frame.ToGodot(_listenerAnchor.position);
            int touched = 0;
            foreach (var kv in _spatialPool)
            {
                foreach (var src in kv.Value)
                {
                    if (src == null) continue;
                    Vec3 emitter = Frame.ToGodot(src.transform.position);
                    double baseDb = SfxEventRouter.GetVolumeForEvent(kv.Key);
                    SetSourceDb(src, _sourceLevels[src].bus, resolver.ResolveVolumeDb(emitter, listener, IsOccluded(emitter, listener), baseDb));
                    touched++;
                }
            }
            return touched;
        }

        /// <summary>Godot's audio-log Stop button (<c>current_voice_log_id = ""</c>) and the bus player stop.</summary>
        public void StopVoiceLog()
        {
            CurrentVoiceLogId = "";
            if (_busPlayers.TryGetValue(AudioEventSeam.BUS_VOICE, out AudioSource player) && player != null) player.Stop();
        }

        public AudioSource GetBusPlayer(string busId) => _busPlayers.TryGetValue(busId ?? "", out var p) ? p : null;

        public int GetSpatialPlayerCount()
        {
            int total = 0;
            foreach (var pool in _spatialPool.Values) total += pool.Count;
            return total;
        }

        // ------------------------------------------------------------------ internals

        AudioClip LoadClip(string resPath)
        {
            if (catalog != null && catalog.TryGetClip(resPath, out AudioClip clip)) return clip;
            if (_warnedMissingPaths.Add(resPath)) CoreServices.Log.Warning($"AudioManager: stream file missing, path='{resPath}'");
            return null;
        }

        void PlayViaBus(string busId, double volumeDb, string eventId = "", string streamPath = "")
        {
            if (!_busPlayers.TryGetValue(busId ?? "", out AudioSource player)) return;
            SetSourceDb(player, busId, volumeDb);
            string path = streamPath;
            if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(eventId) && StreamCatalog.TryGetValue(eventId, out string catalogPath)) path = catalogPath;
            if (string.IsNullOrEmpty(path)) return;
            AudioClip clip = LoadClip(path);
            if (clip == null) return;
            if (player.clip != clip) player.clip = clip;
            if (!_headless) player.Play();
        }

        void PlaySpatial(string eventId, Vector3 unityPosition, string busId, double volumeDb)
        {
            if (!_spatialPool.TryGetValue(eventId, out List<AudioSource> pool))
            {
                pool = new List<AudioSource>();
                _spatialPool[eventId] = pool;
            }
            AudioSource player = null;
            foreach (var candidate in pool)
                if (candidate != null) { player = candidate; break; }
            if (player == null)
            {
                var go = new GameObject("AudioStreamPlayer3D_" + eventId);
                go.transform.SetParent(transform, false);
                player = go.AddComponent<AudioSource>();
                player.playOnAwake = false;
                player.spatialBlend = 1f;
                player.rolloffMode = AudioRolloffMode.Logarithmic;
                player.minDistance = 10f; // Godot AudioStreamPlayer3D unit_size
                player.maxDistance = 500f;
                player.dopplerLevel = 0f;
                pool.Add(player);
            }
            SetSourceDb(player, busId, volumeDb);
            player.transform.position = unityPosition;
            if (!StreamCatalog.TryGetValue(eventId, out string path)) return;
            AudioClip clip = LoadClip(path);
            if (clip == null) return;
            if (player.clip != clip) player.clip = clip;
            if (!_headless) player.Play();
        }

        /// <summary>Godot placeholder occlusion: more than 1.5 m apart vertically and more than 4 m apart.</summary>
        static bool IsOccluded(Vec3 emitter, Vec3 listener)
        {
            Vec3 delta = emitter - listener;
            return Math.Abs(delta.Y) > 1.5f && delta.Length() > 4f;
        }

        void ApplyMusicLayerGains()
        {
            ApplyMusicStemGains();
            ApplyAmbientBeds();
        }

        static string CatalogIdForMetaSchedule(string id)
        {
            if (id == AudioEventSeam.META_EVENT_BEACON || id == AudioEventSeam.META_BEACON_DISTRESS) return AudioEventSeam.META_BEACON_DISTRESS;
            if (id == AudioEventSeam.META_EVENT_PULSE || id == AudioEventSeam.META_BIOMATTER_PULSE) return AudioEventSeam.META_BIOMATTER_PULSE;
            if (id == AudioEventSeam.META_EVENT_GROAN || id == AudioEventSeam.META_HULL_GROAN) return AudioEventSeam.META_HULL_GROAN;
            if (id == AudioEventSeam.META_REACTOR_HUM) return AudioEventSeam.META_REACTOR_HUM;
            return "";
        }

        static string BusForMetaEventId(string id)
        {
            if (id == AudioEventSeam.META_BEACON_DISTRESS || id == AudioEventSeam.META_BIOMATTER_PULSE ||
                id == AudioEventSeam.META_HULL_GROAN || id == AudioEventSeam.META_REACTOR_HUM ||
                id == AudioEventSeam.META_EVENT_BEACON || id == AudioEventSeam.META_EVENT_PULSE || id == AudioEventSeam.META_EVENT_GROAN)
                return AudioEventSeam.BUS_META;
            return AudioEventSeam.BUS_SFX;
        }
    }
}
