// Scene halves of scripts/audio/audio_manager.gd (players/listener) and update_threat_engaged_los()'s raycast
// in scripts/procgen/playable_generated_ship.gd @ 96ecb2b0.
using System;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary><see cref="IAudioSink"/> over the Runtime <see cref="AudioManager"/> (players, pools, listener).</summary>
    public sealed class AudioManagerSink : IAudioSink
    {
        public readonly AudioManager Manager;
        readonly Func<Transform> _listenerAnchor;

        public AudioManagerSink(AudioManager manager, Func<Transform> listenerAnchor)
        {
            Manager = manager != null ? manager : throw new ArgumentNullException(nameof(manager));
            _listenerAnchor = listenerAnchor;
            Manager.Initialize();
        }

        public void ApplyBusVolumes(AudioBusConfig busConfig) => Manager.ApplyBusConfig(busConfig);

        public void PlayOnBus(string busId, double volumeDb, string eventId, string streamPath) =>
            Manager.PlayOnBus(busId, volumeDb, eventId, streamPath);

        public void PlaySpatial(string eventId, Vec3 position, string busId, double volumeDb) =>
            Manager.PlaySpatialAt(eventId, Frame.ToUnity(position), busId, volumeDb);

        public void SetMusicVolumeDb(double volumeDb) => Manager.SetMusicVolumeDb(volumeDb);

        public void AttachListenerToPlayer()
        {
            Transform anchor = _listenerAnchor?.Invoke();
            if (anchor != null) Manager.AttachListener(anchor);
        }

        public long ApplySpatialAttenuation(SpatialAudioResolver resolver) => Manager.ApplySpatialAttenuation(resolver);
    }

    /// <summary>
    /// <see cref="ILineOfSightProbe"/>: <c>Physics.Raycast</c> against Structure | ZoneBlocker | Portal (triggers
    /// ignored), Godot-frame in and out.
    /// </summary>
    public sealed class PhysicsLineOfSightProbe : ILineOfSightProbe
    {
        public const int Mask = (1 << PhysicsLayers.Structure) | (1 << PhysicsLayers.ZoneBlocker) | (1 << PhysicsLayers.Portal);

        public bool HasSpace => true;

        public bool IntersectRay(Vec3 from, Vec3 to, out Vec3 hitPosition)
        {
            Vector3 a = Frame.ToUnity(from);
            Vector3 b = Frame.ToUnity(to);
            Vector3 d = b - a;
            float length = d.magnitude;
            hitPosition = Vec3.Zero;
            if (length <= 1e-6f) return false;
            if (!Physics.Raycast(a, d / length, out RaycastHit hit, length, Mask, QueryTriggerInteraction.Ignore)) return false;
            hitPosition = Frame.ToGodot(hit.point);
            return true;
        }
    }
}
