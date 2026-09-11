// Ported from scripts/systems/ambient_zone_state.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for per-room-role ambient layers (REQ-AU-003, ADR-0029). Owns the active room role, a crossfade
    /// (previous layer at <c>1 - t</c>, new layer at <c>t</c> over <c>crossfade_seconds</c>), and a threat gain that
    /// boosts intensity above the threat threshold. AudioManager reads the summary and applies it to players/buses.
    /// </summary>
    public sealed class AmbientZoneState : ISimModel, IStatusLineProvider
    {
        public const double DEFAULT_CROSSFADE_SECONDS = 1.5;
        public const double DEFAULT_THREAT_THRESHOLD = 0.5;
        public const double DEFAULT_THREAT_BOOST = 0.25;

        /// <summary>Per-room-role ambient intensities, keyed by AudioEventSeam.ROOM_ROLE_*. Never mutate.</summary>
        public static readonly GdDict ROLE_INTENSITIES = new GdDict
        {
            { AudioEventSeam.ROOM_ROLE_CARGO, 0.55 },
            { AudioEventSeam.ROOM_ROLE_ENGINE, 0.70 },
            { AudioEventSeam.ROOM_ROLE_MED_BAY, 0.45 },
            { AudioEventSeam.ROOM_ROLE_CREW_QUARTERS, 0.40 },
            { AudioEventSeam.ROOM_ROLE_DOCKING, 0.60 },
        };

        /// <summary>Per-room-role ambient track ids. Never mutate.</summary>
        public static readonly GdDict ROLE_TRACK_IDS = new GdDict
        {
            { AudioEventSeam.ROOM_ROLE_CARGO, AudioEventSeam.AMB_CARGO },
            { AudioEventSeam.ROOM_ROLE_ENGINE, AudioEventSeam.AMB_ENGINE },
            { AudioEventSeam.ROOM_ROLE_MED_BAY, AudioEventSeam.AMB_MED_BAY },
            { AudioEventSeam.ROOM_ROLE_CREW_QUARTERS, AudioEventSeam.AMB_CREW_QUARTERS },
            { AudioEventSeam.ROOM_ROLE_DOCKING, AudioEventSeam.AMB_DOCKING },
        };

        public double CrossfadeSeconds = DEFAULT_CROSSFADE_SECONDS;
        public double ThreatThreshold = DEFAULT_THREAT_THRESHOLD;
        public double ThreatBoost = DEFAULT_THREAT_BOOST;

        string _currentRole = AudioEventSeam.ROOM_ROLE_DOCKING;
        double _currentIntensity = V.F64(ROLE_INTENSITIES.Get(AudioEventSeam.ROOM_ROLE_DOCKING, 0.6));
        string _currentTrackId = V.Str(ROLE_TRACK_IDS.Get(AudioEventSeam.ROOM_ROLE_DOCKING, AudioEventSeam.AMB_DOCKING));

        string _previousRole = "";
        double _previousIntensity = 0.0;
        string _previousTrackId = "";

        double _crossfadeTime = 0.0;
        bool _crossfadeActive = false;

        double _threatLevel = 0.0;

        /// <summary>
        /// Recognized keys: crossfade_seconds [0.1, 10], threat_threshold [0, 1], threat_boost [0, 1], initial_role,
        /// initial_threat [0, 1]. Missing keys keep their values; the crossfade is always reset.
        /// </summary>
        public void Configure(GdDict config)
        {
            if (config == null) return;
            if (config.Has("crossfade_seconds"))
                CrossfadeSeconds = GdMath.Clampf(V.F64(config["crossfade_seconds"]), 0.1, 10.0);
            if (config.Has("threat_threshold"))
                ThreatThreshold = GdMath.Clampf(V.F64(config["threat_threshold"]), 0.0, 1.0);
            if (config.Has("threat_boost"))
                ThreatBoost = GdMath.Clampf(V.F64(config["threat_boost"]), 0.0, 1.0);
            if (config.Has("initial_role"))
            {
                string role = V.Str(config["initial_role"]);
                _currentRole = role;
                _currentIntensity = IntensityForRole(_currentRole);
                _currentTrackId = TrackIdForRole(_currentRole);
            }
            if (config.Has("initial_threat"))
                _threatLevel = GdMath.Clampf(V.F64(config["initial_threat"]), 0.0, 1.0);
            _previousRole = "";
            _previousIntensity = 0.0;
            _previousTrackId = "";
            _crossfadeTime = 0.0;
            _crossfadeActive = false;
        }

        /// <summary>
        /// Starts a crossfade to <paramref name="roleId"/> (or restarts the current role when forced). Unknown roles
        /// keep the current state and, by default, log a warning.
        /// </summary>
        public void SetRoomRole(string roleId, bool forceRestart = false, bool emitWarning = true)
        {
            roleId = roleId ?? "";
            if (!IsKnownRole(roleId))
            {
                if (emitWarning)
                    CoreServices.Log.Warning("AmbientZoneState: unknown room role '" + roleId + "' (keeping '" + _currentRole + "')");
                return;
            }
            if (roleId == _currentRole && !forceRestart && !_crossfadeActive) return;
            _previousRole = _currentRole;
            _previousIntensity = _currentIntensity;
            _previousTrackId = _currentTrackId;
            _currentRole = roleId;
            _currentIntensity = IntensityForRole(_currentRole);
            _currentTrackId = TrackIdForRole(_currentRole);
            _crossfadeTime = 0.0;
            _crossfadeActive = true;
        }

        /// <summary>0.0 = calm, 1.0 = maximum threat. Clamped to [0, 1].</summary>
        public void SetThreatLevel(double level)
        {
            _threatLevel = GdMath.Clampf(level, 0.0, 1.0);
        }

        /// <summary>Advances the crossfade. Returns true when it completed during this tick.</summary>
        public bool Tick(double deltaSeconds)
        {
            if (deltaSeconds <= 0.0) return false;
            if (!_crossfadeActive) return false;
            _crossfadeTime += deltaSeconds;
            if (_crossfadeTime >= CrossfadeSeconds)
            {
                _crossfadeActive = false;
                _crossfadeTime = CrossfadeSeconds;
                _previousRole = "";
                _previousIntensity = 0.0;
                _previousTrackId = "";
                return true;
            }
            return false;
        }

        /// <summary>current_gain / previous_gain of the two layers in flight, the threat multiplier, and both intensities.</summary>
        public GdDict GetLayerGains()
        {
            double currentGain = 1.0;
            double previousGain = 0.0;
            if (_crossfadeActive && CrossfadeSeconds > 0.0)
            {
                double t = GdMath.Clampf(_crossfadeTime / CrossfadeSeconds, 0.0, 1.0);
                currentGain = t;
                previousGain = 1.0 - t;
            }
            double threatMultiplier = 1.0;
            if (_threatLevel > ThreatThreshold)
                threatMultiplier = 1.0 + (_threatLevel - ThreatThreshold) * ThreatBoost * 2.0;
            return new GdDict
            {
                { "current_gain", currentGain },
                { "previous_gain", previousGain },
                { "threat_multiplier", threatMultiplier },
                { "current_intensity", _currentIntensity },
                { "previous_intensity", _previousIntensity },
            };
        }

        public string GetCurrentRole() => _currentRole;

        public string GetCurrentTrackId() => _currentTrackId;

        public double GetThreatLevel() => _threatLevel;

        public bool IsCrossfadeActive() => _crossfadeActive;

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>
            {
                "Ambient: " + _currentRole + " (intensity=" + GdString.FormatFixed(_currentIntensity, 2) + " threat=" + GdString.FormatFixed(_threatLevel, 2) + ")",
            };
            if (_crossfadeActive)
            {
                double t = GdMath.Clampf(_crossfadeTime / CrossfadeSeconds, 0.0, 1.0);
                lines.Add("Ambient crossfade: " + _previousRole + " -> " + _currentRole + " t=" + GdString.FormatFixed(t, 2));
            }
            return lines;
        }

        /// <summary>Summary dictionary for save/load (REQ-AU-010). Pure data; no live refs.</summary>
        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "kind", "ambient_zone_state" },
                { "current_role", _currentRole },
                { "current_track_id", _currentTrackId },
                { "current_intensity", _currentIntensity },
                { "previous_role", _previousRole },
                { "previous_intensity", _previousIntensity },
                { "previous_track_id", _previousTrackId },
                { "crossfade_active", _crossfadeActive },
                { "crossfade_time", _crossfadeTime },
                { "crossfade_seconds", CrossfadeSeconds },
                { "threat_level", _threatLevel },
                { "threat_threshold", ThreatThreshold },
                { "threat_boost", ThreatBoost },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (V.Str(summary.Get("kind", "")) != "ambient_zone_state") return false;
            bool changed = false;
            string newRole = V.Str(summary.Get("current_role", _currentRole));
            if (newRole != _currentRole)
            {
                SetRoomRole(newRole, true);
                changed = true;
            }
            if (summary.Has("crossfade_seconds"))
            {
                double newCf = GdMath.Clampf(V.F64(summary["crossfade_seconds"]), 0.1, 10.0);
                if (System.Math.Abs(newCf - CrossfadeSeconds) > 0.001)
                {
                    CrossfadeSeconds = newCf;
                    changed = true;
                }
            }
            if (summary.Has("threat_threshold"))
            {
                double newTt = GdMath.Clampf(V.F64(summary["threat_threshold"]), 0.0, 1.0);
                if (System.Math.Abs(newTt - ThreatThreshold) > 0.001)
                {
                    ThreatThreshold = newTt;
                    changed = true;
                }
            }
            if (summary.Has("threat_boost"))
            {
                double newTb = GdMath.Clampf(V.F64(summary["threat_boost"]), 0.0, 1.0);
                if (System.Math.Abs(newTb - ThreatBoost) > 0.001)
                {
                    ThreatBoost = newTb;
                    changed = true;
                }
            }
            if (summary.Has("threat_level"))
            {
                double newThreat = GdMath.Clampf(V.F64(summary["threat_level"]), 0.0, 1.0);
                if (System.Math.Abs(newThreat - _threatLevel) > 0.001)
                {
                    SetThreatLevel(newThreat);
                    changed = true;
                }
            }
            if (summary.Has("crossfade_active"))
            {
                _crossfadeActive = V.Bool(summary["crossfade_active"]);
                changed = true;
            }
            if (summary.Has("crossfade_time"))
            {
                _crossfadeTime = GdMath.Clampf(V.F64(summary["crossfade_time"]), 0.0, CrossfadeSeconds);
                changed = true;
            }
            if (summary.Has("previous_role"))
                _previousRole = V.Str(summary["previous_role"]);
            if (summary.Has("previous_track_id"))
                _previousTrackId = V.Str(summary["previous_track_id"]);
            if (summary.Has("previous_intensity"))
                _previousIntensity = V.F64(summary["previous_intensity"]);
            return changed;
        }

        static double IntensityForRole(string roleId) => V.F64(ROLE_INTENSITIES.Get(roleId, 0.6));

        static string TrackIdForRole(string roleId) => V.Str(ROLE_TRACK_IDS.Get(roleId, AudioEventSeam.AMB_DOCKING));

        static bool IsKnownRole(string roleId)
        {
            foreach (object known in AudioEventSeam.ALL_ROOM_ROLES)
            {
                if (V.Str(known) == roleId) return true;
            }
            return false;
        }
    }
}
