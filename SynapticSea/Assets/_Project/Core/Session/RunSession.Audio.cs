// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _refresh_audio_state + ambient-zone push + captions
// (9614-9775), _build_hallucination_runtime (5110-5138) and _reconcile_captions_with_settings (10760-10765).
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary>
        /// <c>_refresh_audio_state(force_initial, delta)</c>: music flags from hazards/vitals/engagement, ambient zone from the
        /// nearest room role, hazard-coupled SFX (router cooldown-gated), rising-edge alerts, listener + spatial attenuation,
        /// and caption pumping into the HUD caption line.
        /// </summary>
        void RefreshAudioState(bool forceInitial, double deltaSeconds = 0.0)
        {
            if (AudioManager == null)
                return;
            FireSuppressionState afs = ActiveFireState();
            bool hazardActive = false;
            if (OxygenState != null && OxygenState.IsPassabilityBlocked())
                hazardActive = true;
            if (afs != null && !afs.GetBurningCompartments().IsEmpty)
                hazardActive = true;
            if (ElectricalArcState != null && ElectricalArcState.IsPassabilityBlocked())
                hazardActive = true;
            bool vitalsCritical = false;
            if (VitalsModel != null)
            {
                GdDict vs = VitalsModel.GetVitalsSummary();
                if (V.F64(vs.Get("oxygen", 1.0)) <= 0.0)
                    vitalsCritical = true;
                else if (V.F64(vs.Get("health", 100.0)) < 25.0)
                    vitalsCritical = true;
            }
            bool engagement = ThreatManager != null && ThreatManager.HasCombatEngagement();
            AudioManager.UpdateMusicFlags(engagement, hazardActive, vitalsCritical);
            PushAmbientZoneFromGameplay(hazardActive, engagement);
            if (afs != null && !afs.GetBurningCompartments().IsEmpty)
                PlaySfx(AudioEventSeam.SFX_FIRE_CRACKLE);
            if (ElectricalArcState != null && ElectricalArcState.CurrentPhaseValue == (long)ElectricalArcState.Phase.ARCING)
                PlaySfx(AudioEventSeam.SFX_ARC_ZAP);
            if (vitalsCritical)
                PlaySfx(AudioEventSeam.SFX_SUIT_BREATH);
            if (vitalsCritical && !_prevVitalsCritical)
                PlaySfx(AudioEventSeam.UI_VITALS_LOW);
            if (engagement && !_prevCombatEngaged)
                PlaySfx(AudioEventSeam.SFX_COMBAT_THREAT_ALERT);
            _prevVitalsCritical = vitalsCritical;
            _prevCombatEngaged = engagement;
            if (HasPlayer)
                AudioManager.AttachListener();
            AudioManager.ApplySpatialAttenuation();
            AudioManager.PumpCaptions(OnAudioCaption);
            if (_lastCaptionLine.Length > 0)
            {
                _captionExpirySeconds -= deltaSeconds;
                if (_captionExpirySeconds <= 0.0)
                {
                    _lastCaptionLine = "";
                    _captionExpirySeconds = 0.0;
                }
            }
        }

        /// <summary>Maps layout room_role strings onto AmbientZoneState ROOM_ROLE_* ids.</summary>
        static string AmbientRoleForLayoutRole(string layoutRole)
        {
            string r = layoutRole.ToLowerInvariant();
            switch (r)
            {
                case "cargo":
                case "storage":
                case "salvage":
                    return AudioEventSeam.ROOM_ROLE_CARGO;
                case "engine":
                case "engineering":
                case "reactor":
                case "engine_bay":
                case "propulsion":
                case "power":
                    return AudioEventSeam.ROOM_ROLE_ENGINE;
                case "medical":
                case "med_bay":
                case "triage":
                case "clinic":
                    return AudioEventSeam.ROOM_ROLE_MED_BAY;
                case "crew_quarters":
                case "quarters":
                case "mess_hall":
                case "habitation":
                case "galley":
                    return AudioEventSeam.ROOM_ROLE_CREW_QUARTERS;
                default:
                    return AudioEventSeam.ROOM_ROLE_DOCKING;
            }
        }

        /// <summary>Nearest layout room_role to the player (room world_position/position or the loader room center).</summary>
        string NearestLayoutRoomRole()
        {
            if (!HasPlayer)
                return "";
            GdDict layout = new GdDict();
            if (CurrentShip != null && CurrentShip.BuiltLayout != null)
                layout = CurrentShip.BuiltLayout;
            else if (Loader != null)
                layout = Loader.GetLayoutCopy();
            if (!(layout.Get("rooms", null) is GdArray rooms) || rooms.IsEmpty)
                return "";
            Vec3 playerPos = PlayerPos;
            string bestRole = "";
            double bestDist = double.PositiveInfinity;
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room))
                    continue;
                string role = V.Str(room.Get("room_role", room.Get("role", "")));
                if (role.Length == 0)
                    continue;
                Vec3 center;
                object posV = room.Get("world_position", room.Get("position", null));
                if (posV is Vec3 pv)
                    center = pv;
                else if (posV is GdArray arr && arr.Count >= 3)
                    center = new Vec3(V.F64(arr[0]), V.F64(arr[1]), V.F64(arr[2]));
                else if (Loader != null && room.Has("id"))
                    center = Loader.GetRoomCenter(V.Str(room.Get("id", "")));
                else
                    continue;
                Vec3 worldCenter = center;
                if (CurrentShip != null && RootInTree(CurrentShip.SceneRoot))
                    worldCenter = ToGlobal(CurrentShip.SceneRoot, center);
                double dist = playerPos.DistanceTo(worldCenter);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestRole = role;
                }
            }
            return bestRole;
        }

        void PushAmbientZoneFromGameplay(bool hazardActive, bool engagement)
        {
            if (AudioManager == null || AudioManager.AmbientZoneState == null)
                return;
            string layoutRole = NearestLayoutRoomRole();
            string ambientRole = AmbientRoleForLayoutRole(layoutRole);
            if (AudioManager.AmbientZoneState.GetCurrentRole() != ambientRole)
                AudioManager.AmbientZoneState.SetRoomRole(ambientRole, false, false);
            double threat = 0.0;
            if (engagement)
                threat = 1.0;
            else if (hazardActive)
                threat = 0.7;
            AudioManager.AmbientZoneState.SetThreatLevel(threat);
        }

        /// <summary>Records the most recent caption as the HUD caption line.</summary>
        void OnAudioCaption(GdDict caption)
        {
            string text = V.Str(caption.Get("text", ""));
            if (text.Length == 0)
                return;
            _lastCaptionLine = text;
            _captionExpirySeconds = V.F64(caption.Get("duration", SfxEventRouter.DEFAULT_CAPTION_DURATION));
        }

        /// <summary>ADR-0044: SettingsState.captions is the single source of truth for SfxEventRouter.captions_enabled.</summary>
        void ReconcileCaptionsWithSettings()
        {
            if (AudioManager == null || SettingsState == null)
                return;
            AudioManager.SfxRouter.CaptionsEnabled = SettingsState.IsCaptionsEnabled();
        }

        /// <summary>Applies a settings summary (title handoff + menu): captions follow the settings (ADR-0044).</summary>
        public bool ApplyUiSettingsSummary(GdDict summary)
        {
            if (SettingsState == null)
                return false;
            bool ok = SettingsState.ApplySummary(summary);
            if (AudioManager != null)
                AudioManager.SfxRouter.CaptionsEnabled = SettingsState.IsCaptionsEnabled();
            return ok;
        }

        /// <summary>ADR-0042: (re)build the hallucination director + manager for the active ship.</summary>
        void BuildHallucinationRuntime()
        {
            HallucinationManager?.ClearAll();
            HallucinationDirector = new HallucinationDirector();
            HallucinationDirector.Configure(new GdDict { { "seed", ShipSeed(CurrentShip) } });
            HallucinationManager = new HallucinationRuntime();
            HallucinationManager.Configure(HallucinationDirector);
            HallucinationManager.Parent = ActiveShipAttachRoot();
            HallucinationManager.SetChannels(AudioManager, true);
            HallucinationManager.FxIntensityChanged += v => Events.RaiseHallucinationFxIntensity(v);
        }
    }
}
