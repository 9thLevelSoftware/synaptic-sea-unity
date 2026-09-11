// Scene boundary for scripts/audio/audio_manager.gd @ 96ecb2b0 (the node half of the AudioManager).
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// RUNTIME: the AudioStreamPlayer / AudioServer work <c>audio_manager.gd</c> did after its pure models decided what to
    /// play. <see cref="SessionAudio"/> owns the models and calls this sink; the Runtime <c>AudioManager</c> implements it.
    /// Positions are Godot-frame world space.
    /// </summary>
    public interface IAudioSink
    {
        /// <summary><c>_apply_bus_volumes()</c>: push every bus volume / mute from <paramref name="busConfig"/>.</summary>
        void ApplyBusVolumes(AudioBusConfig busConfig);

        /// <summary>
        /// <c>_play_via_bus(bus_id, volume_db, event_id, stream_path)</c>. <paramref name="streamPath"/> wins over the
        /// STREAM_CATALOG lookup for <paramref name="eventId"/>; with neither, only the volume push happens.
        /// </summary>
        void PlayOnBus(string busId, double volumeDb, string eventId, string streamPath);

        /// <summary><c>_play_spatial(event_id, position, bus_id, volume_db)</c>.</summary>
        void PlaySpatial(string eventId, Vec3 position, string busId, double volumeDb);

        /// <summary><c>_apply_music_layer_gains()</c>: music bus player volume (and lazy base-layer stream start).</summary>
        void SetMusicVolumeDb(double volumeDb);

        /// <summary>
        /// <c>attach_listener(player)</c> + <c>update_listener_transform()</c>: parent the listener to the player anchor.
        /// </summary>
        void AttachListenerToPlayer();

        /// <summary>
        /// <c>apply_spatial_attenuation()</c>: re-resolve every live spatial source against the listener with
        /// <paramref name="resolver"/> (base dB from <c>SfxEventRouter.get_volume_for_event</c>). Returns the count touched.
        /// </summary>
        long ApplySpatialAttenuation(SpatialAudioResolver resolver);
    }
}
