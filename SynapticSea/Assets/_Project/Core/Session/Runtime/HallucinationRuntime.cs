// Ported from scripts/systems/hallucination_manager.gd @ 96ecb2b0 (the pure half; phantom nodes are events).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// The model half of <c>HallucinationManager</c> (a Node3D attached to the active ship root). Phantom placeholder
    /// nodes become <see cref="PhantomSpawned"/> / <see cref="PhantomFreed"/>; the screen-FX overlay's
    /// <c>hallucination_intensity</c> meta becomes <see cref="FxIntensity"/> + <see cref="FxIntensityChanged"/>.
    /// Phantom world positions follow the ship root it is attached to (<see cref="Parent"/>), exactly like the Godot
    /// children of the manager node.
    /// </summary>
    public sealed class HallucinationRuntime
    {
        public const string PHANTOM_ARCHETYPE = "stalker";

        public HallucinationDirector Director;
        public double MeleeRange = 1.2;

        /// <summary>RUNTIME: the ship scene root the manager was attached to (<c>_attach_zone_to_active_ship</c>); null = coordinator origin.</summary>
        public IShipSceneRoot Parent;

        /// <summary>Audio channel (<c>set_channels(audio_manager, ...)</c>); null disables the audio channel.</summary>
        public SessionAudio Audio;

        /// <summary>True once <c>set_channels</c> received an fx overlay.</summary>
        public bool HasFxOverlay;

        /// <summary>The <c>hallucination_intensity</c> meta last written to the overlay.</summary>
        public double FxIntensity;

        /// <summary>RUNTIME: a phantom placeholder node (<c>Phantom_&lt;id&gt;</c>) spawned at the given LOCAL position under <see cref="Parent"/>.</summary>
        public event Action<long, Vec3> PhantomSpawned;

        /// <summary>RUNTIME: the phantom node for the id was freed.</summary>
        public event Action<long> PhantomFreed;

        /// <summary>RUNTIME: the overlay intensity meta changed.</summary>
        public event Action<double> FxIntensityChanged;

        /// <summary>event_id -> local position (the Godot <c>_phantom_nodes</c>).</summary>
        readonly Dictionary<long, Vec3> _phantomNodes = new Dictionary<long, Vec3>();
        readonly List<long> _phantomOrder = new List<long>();
        double _ambientCooldown;
        bool _prevHudActive;

        public void Configure(HallucinationDirector director) => Director = director;

        public void SetChannels(SessionAudio audio, bool hasFxOverlay)
        {
            Audio = audio;
            HasFxOverlay = hasFxOverlay;
        }

        Vec3 GlobalOf(Vec3 local) =>
            Parent != null && Parent.IsValid && Parent.IsInsideTree ? Parent.GlobalTransform * local : local;

        public List<string> GetHallucinatedStatusLines()
        {
            var lines = new List<string>();
            if (Director == null)
                return lines;
            foreach (object e in Director.GetActiveEvents("hud"))
                lines.Add("CONTACT? bearing " + (V.I64(((GdDict)e)["id"]) * 37 % 360));
            return lines;
        }

        public void Render(double delta, Vec3 playerPosition)
        {
            if (Director == null)
            {
                ClearAll();
                return;
            }
            GdArray events = Director.GetActiveEvents("phantom");
            var liveIds = new HashSet<long>();
            foreach (object eObj in events)
            {
                var e = (GdDict)eObj;
                long id = V.I64(e["id"]);
                liveIds.Add(id);
                if (!_phantomNodes.ContainsKey(id))
                {
                    Vec3 pos = e["position"] is Vec3 p ? p : Vec3.FromArray(e["position"]);
                    _phantomNodes[id] = pos;
                    _phantomOrder.Add(id);
                    PhantomSpawned?.Invoke(id, pos);
                    if (Audio != null)
                    {
                        string ae = V.Str(e.Get("audio_event", ""));
                        Audio.PlaySfx(ae.Length == 0 ? AudioEventSeam.SFX_SANITY_PHANTOM : ae);
                    }
                }
            }
            foreach (long id in new List<long>(_phantomOrder))
            {
                if (!liveIds.Contains(id))
                    FreePhantom(id);
            }
            foreach (long id in new List<long>(_phantomOrder))
            {
                if (_phantomNodes.TryGetValue(id, out Vec3 local) && GlobalOf(local).DistanceTo(playerPosition) <= MeleeRange)
                    FreePhantom(id);
            }
            _ambientCooldown = Math.Max(0.0, _ambientCooldown - delta);
            if (Audio != null && !Director.GetActiveEvents("ambient").IsEmpty && _ambientCooldown <= 0.0)
            {
                Audio.PlaySfx(AudioEventSeam.SFX_HALLUCINATION_WHISPER);
                Audio.PlaySfx(AudioEventSeam.SFX_SANITY_AMBIENT);
                _ambientCooldown = 2.0;
            }
            bool hudActive = !Director.GetActiveEvents("hud").IsEmpty;
            if (hudActive && !_prevHudActive && Audio != null)
                Audio.PlaySfx(AudioEventSeam.SFX_SANITY_HUD);
            _prevHudActive = hudActive;
            if (HasFxOverlay)
                SetFx(Director.GetFxIntensity());
        }

        /// <summary>Vanish the nearest phantom within attack_range; returns whether one was dissipated.</summary>
        public bool DissipatePhantomInRange(Vec3 playerPosition, double attackRange = 1.6)
        {
            long bestId = -1;
            double bestD = attackRange;
            foreach (long id in _phantomOrder)
            {
                double d = GlobalOf(_phantomNodes[id]).DistanceTo(playerPosition);
                if (d <= bestD)
                {
                    bestD = d;
                    bestId = id;
                }
            }
            if (bestId >= 0)
            {
                FreePhantom(bestId);
                return true;
            }
            return false;
        }

        public long PhantomCount() => _phantomNodes.Count;

        public void ClearAll()
        {
            foreach (long id in new List<long>(_phantomOrder))
                FreePhantom(id);
            _phantomNodes.Clear();
            _phantomOrder.Clear();
            if (HasFxOverlay)
                SetFx(0.0);
        }

        void SetFx(double intensity)
        {
            FxIntensity = intensity;
            FxIntensityChanged?.Invoke(intensity);
        }

        void FreePhantom(long id)
        {
            if (_phantomNodes.Remove(id))
            {
                _phantomOrder.Remove(id);
                PhantomFreed?.Invoke(id);
            }
            Director?.RemoveEvent(id);
        }
    }
}
