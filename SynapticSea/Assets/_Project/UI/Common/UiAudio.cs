using SynapticSea.Core.Systems;
using SynapticSea.Runtime;

namespace SynapticSea.UI
{
    /// <summary>
    /// The AudioManager surface the Godot panels reached for (play_sfx, bus volume/mute, the voice-log registry).
    /// Panels depend on this seam so they stay testable; <see cref="AudioManagerAdapter"/> wraps the Runtime manager.
    /// </summary>
    public interface IUiAudio
    {
        bool PlaySfx(string eventId);
        double GetBusVolume(string busId);
        bool SetBusVolume(string busId, double volumeDb);
        bool IsBusMuted(string busId);
        bool SetBusMuted(string busId, bool muted);
        /// <summary>The voice-log registry, or null when the manager has none.</summary>
        AudioLog AudioLog { get; }
        string CurrentVoiceLogId { get; }
        bool PlayVoiceLog(string entryId);
        /// <summary>Godot's Stop button set <c>current_voice_log_id = ""</c>.</summary>
        void StopVoiceLog();
    }

    /// <summary>Adapter over the Runtime <see cref="AudioManager"/>.</summary>
    public sealed class AudioManagerAdapter : IUiAudio
    {
        readonly AudioManager _manager;
        bool _stopped;

        public AudioManagerAdapter(AudioManager manager) => _manager = manager;

        public AudioManager Manager => _manager;

        public bool PlaySfx(string eventId) => _manager != null && _manager.PlaySfx(eventId);
        public double GetBusVolume(string busId) => _manager != null ? _manager.GetBusVolume(busId) : 0.0;
        public bool SetBusVolume(string busId, double volumeDb) => _manager != null && _manager.SetBusVolume(busId, volumeDb);
        public bool IsBusMuted(string busId) => _manager != null && _manager.IsBusMuted(busId);
        public bool SetBusMuted(string busId, bool muted) => _manager != null && _manager.SetBusMuted(busId, muted);
        public AudioLog AudioLog => _manager != null ? _manager.AudioLog : null;

        // The Runtime manager exposes no stop/clear API for CurrentVoiceLogId (its setter is private), so Stop is
        // mirrored here until the next PlayVoiceLog. Open item: add AudioManager.StopVoiceLog() in Runtime.
        public string CurrentVoiceLogId => _manager == null || _stopped ? "" : _manager.CurrentVoiceLogId;

        public bool PlayVoiceLog(string entryId)
        {
            if (_manager == null) return false;
            bool ok = _manager.PlayVoiceLog(entryId);
            if (ok) _stopped = false;
            return ok;
        }

        public void StopVoiceLog() => _stopped = true;
    }
}
