using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.UI;
using UnityEngine;

namespace SynapticSea.Game
{
    /// <summary>
    /// <see cref="IUiAudio"/> over the session's <see cref="SessionAudio"/> models (the single source of truth for bus
    /// volumes and the voice-log registry), played through the Runtime <see cref="AudioManager"/> sink.
    /// </summary>
    public sealed class SessionUiAudio : IUiAudio, IVoiceClipAvailability
    {
        readonly RunSession _session;
        readonly AudioManager _manager;

        public SessionUiAudio(RunSession session, AudioManager manager)
        {
            _session = session;
            _manager = manager;
        }

        SessionAudio A => _session?.AudioManager;

        public bool PlaySfx(string eventId) => A != null && A.PlaySfx(eventId);
        public double GetBusVolume(string busId) => A != null ? A.GetBusVolume(busId) : 0.0;
        public bool SetBusVolume(string busId, double volumeDb) => A != null && A.SetBusVolume(busId, volumeDb);
        public bool IsBusMuted(string busId) => A != null && A.IsBusMuted(busId);
        public bool SetBusMuted(string busId, bool muted) => A != null && A.SetBusMuted(busId, muted);
        public AudioLog AudioLog => A?.AudioLog;
        public string CurrentVoiceLogId => A != null ? A.CurrentVoiceLogId : "";
        public bool PlayVoiceLog(string entryId) => A != null && A.PlayVoiceLog(entryId);

        /// <summary>True when the build's audio catalog has a clip for <paramref name="clipPath"/> (Godot shipped no voice .ogg).</summary>
        public bool HasVoiceClip(string clipPath)
        {
            if (string.IsNullOrEmpty(clipPath)) return false;
            AudioCatalog catalog = _manager != null ? _manager.Catalog : null;
            if (catalog == null) catalog = Resources.Load<AudioCatalog>("Catalogs/AudioCatalog");
            return catalog != null && catalog.TryGetClip(clipPath, out AudioClip clip) && clip != null;
        }

        public void StopVoiceLog()
        {
            if (A != null) A.CurrentVoiceLogId = "";
            if (_manager != null) _manager.StopVoiceLog();
        }
    }

    /// <summary><see cref="IWoundTreatmentHost"/> over the session (items consumed, SFX and training events, refusal reasons).</summary>
    public sealed class SessionWoundHost : IWoundTreatmentHost
    {
        readonly RunSession _session;

        public SessionWoundHost(RunSession session) => _session = session;

        public WoundState WoundState => _session?.WoundState;
        public GdArray GetTreatableWounds() => _session != null ? _session.GetTreatableWounds() : new GdArray();
        public GdDict BandageWound(string woundId) => _session != null ? _session.BandageWound(woundId) : new GdDict { { "ok", false }, { "reason", "wounds_unavailable" } };
        public GdDict TreatWound(string woundId) => _session != null ? _session.TreatWound(woundId) : new GdDict { { "ok", false }, { "reason", "wounds_unavailable" } };
    }

    /// <summary><see cref="IScannerHost"/> over the session (scan + travel run the session's scene surgery).</summary>
    public sealed class SessionScannerHost : IScannerHost
    {
        readonly RunSession _session;

        public SessionScannerHost(RunSession session, IUiAudio audio)
        {
            _session = session;
            Audio = audio;
        }

        public IUiAudio Audio { get; }
        public GdDict Scan() => _session.Scan();
        public GdDict TravelToMarkerId(string markerId) => _session.TravelToMarkerId(markerId);
        public void EmitTrainingEvent(string eventId, string targetId) => _session.EmitTrainingEvent(eventId, targetId);
    }

    /// <summary><see cref="IRecipePickerHost"/> over the session (every station kind, field craft included).</summary>
    public sealed class SessionRecipeHost : IRecipePickerHost
    {
        readonly RunSession _session;

        public SessionRecipeHost(RunSession session, IUiAudio audio)
        {
            _session = session;
            Audio = audio;
        }

        public IUiAudio Audio { get; }
        public GdArray ListStationRecipeEntries(string stationKind) => _session.ListStationRecipeEntries(stationKind);
        public GdDict BeginCraftFromPicker(string stationKind, string recipeId) => _session.BeginCraftFromPicker(stationKind, recipeId);
    }
}
