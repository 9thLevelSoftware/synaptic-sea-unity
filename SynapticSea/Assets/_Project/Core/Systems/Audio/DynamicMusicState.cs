// Ported from scripts/systems/dynamic_music_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for the four-state music layer machine (REQ-AU-004, ADR-0029).
    /// States: EXPLORATION (default), TENSION (hazard non-safe), COMBAT (engagement), CRITICAL (vitals unsafe);
    /// CRITICAL beats COMBAT beats TENSION. Each state declares per-layer target gains; per-layer crossfades
    /// (default 2.0s) move the current gains toward them. Deterministic: no RNG.
    /// </summary>
    public sealed class DynamicMusicState : ISimModel, IStatusLineProvider
    {
        public const double DEFAULT_CROSSFADE_SECONDS = 2.0;

        /// <summary>Per-state target gains (0.0 .. 1.0). Never mutate.</summary>
        public static readonly GdDict STATE_TARGET_GAINS = new GdDict
        {
            {
                AudioEventSeam.MUSIC_STATE_EXPLORATION, new GdDict
                {
                    { AudioEventSeam.MUSIC_LAYER_BASE, 1.0 },
                    { AudioEventSeam.MUSIC_LAYER_TENSION_DRONE, 0.0 },
                    { AudioEventSeam.MUSIC_LAYER_COMBAT_PERCUSSION, 0.0 },
                    { AudioEventSeam.MUSIC_LAYER_CRITICAL_PAD, 0.0 },
                }
            },
            {
                AudioEventSeam.MUSIC_STATE_TENSION, new GdDict
                {
                    { AudioEventSeam.MUSIC_LAYER_BASE, 0.6 },
                    { AudioEventSeam.MUSIC_LAYER_TENSION_DRONE, 0.7 },
                    { AudioEventSeam.MUSIC_LAYER_COMBAT_PERCUSSION, 0.0 },
                    { AudioEventSeam.MUSIC_LAYER_CRITICAL_PAD, 0.0 },
                }
            },
            {
                AudioEventSeam.MUSIC_STATE_COMBAT, new GdDict
                {
                    { AudioEventSeam.MUSIC_LAYER_BASE, 0.5 },
                    { AudioEventSeam.MUSIC_LAYER_TENSION_DRONE, 0.4 },
                    { AudioEventSeam.MUSIC_LAYER_COMBAT_PERCUSSION, 0.9 },
                    { AudioEventSeam.MUSIC_LAYER_CRITICAL_PAD, 0.0 },
                }
            },
            {
                AudioEventSeam.MUSIC_STATE_CRITICAL, new GdDict
                {
                    { AudioEventSeam.MUSIC_LAYER_BASE, 0.3 },
                    { AudioEventSeam.MUSIC_LAYER_TENSION_DRONE, 0.4 },
                    { AudioEventSeam.MUSIC_LAYER_COMBAT_PERCUSSION, 0.7 },
                    { AudioEventSeam.MUSIC_LAYER_CRITICAL_PAD, 0.9 },
                }
            },
        };

        public double CrossfadeSeconds = DEFAULT_CROSSFADE_SECONDS;

        string _state = AudioEventSeam.MUSIC_STATE_EXPLORATION;
        bool _engagementFlag = false;
        bool _hazardActive = false;
        bool _vitalsCritical = false;

        /// <summary>Layer id -> current gain (always holds the four canonical layers).</summary>
        GdDict _layerGains = new GdDict
        {
            { AudioEventSeam.MUSIC_LAYER_BASE, 1.0 },
            { AudioEventSeam.MUSIC_LAYER_TENSION_DRONE, 0.0 },
            { AudioEventSeam.MUSIC_LAYER_COMBAT_PERCUSSION, 0.0 },
            { AudioEventSeam.MUSIC_LAYER_CRITICAL_PAD, 0.0 },
        };

        GdDict _targetGains;

        public DynamicMusicState()
        {
            _targetGains = _layerGains.DeepCopy();
        }

        public void Configure(GdDict config)
        {
            if (config == null) return;
            if (config.Has("crossfade_seconds"))
                CrossfadeSeconds = GdMath.Clampf(V.F64(config["crossfade_seconds"]), 0.1, 30.0);
            if (config.Has("initial_state"))
            {
                string newState = V.Str(config["initial_state"]);
                if (IsKnownState(newState))
                {
                    _state = newState;
                    _targetGains = TargetGainsForState(_state);
                }
            }
            // Snap current gains to target gains so a fresh configure behaves like a clean state transition.
            _layerGains = _targetGains.DeepCopy();
            if (config.Has("engagement_flag")) _engagementFlag = V.Bool(config["engagement_flag"]);
            if (config.Has("hazard_active")) _hazardActive = V.Bool(config["hazard_active"]);
            if (config.Has("vitals_critical")) _vitalsCritical = V.Bool(config["vitals_critical"]);
        }

        /// <summary>Updates the per-frame gameplay flags; the state follows <see cref="ResolveState"/>.</summary>
        public void SetFlags(bool engagement, bool hazardActive, bool vitalsCritical)
        {
            _engagementFlag = engagement;
            _hazardActive = hazardActive;
            _vitalsCritical = vitalsCritical;
            string newState = ResolveState();
            if (newState != _state)
            {
                _state = newState;
                _targetGains = TargetGainsForState(_state);
            }
        }

        /// <summary>Pure resolution: the priority-ordered state for the current flags.</summary>
        public string ResolveState()
        {
            if (_vitalsCritical) return AudioEventSeam.MUSIC_STATE_CRITICAL;
            if (_engagementFlag) return AudioEventSeam.MUSIC_STATE_COMBAT;
            if (_hazardActive) return AudioEventSeam.MUSIC_STATE_TENSION;
            return AudioEventSeam.MUSIC_STATE_EXPLORATION;
        }

        /// <summary>Force a state override (meta-events and scripted cues). False for unknown states.</summary>
        public bool OverrideState(string newState, bool emitWarning = true)
        {
            newState = newState ?? "";
            if (!IsKnownState(newState))
            {
                if (emitWarning) CoreServices.Log.Warning("DynamicMusicState: unknown state '" + newState + "'");
                return false;
            }
            _state = newState;
            _targetGains = TargetGainsForState(_state);
            return true;
        }

        /// <summary>Advances the per-layer crossfades. Returns true when any layer's gain changed.</summary>
        public bool Tick(double deltaSeconds)
        {
            if (deltaSeconds <= 0.0) return false;
            if (CrossfadeSeconds <= 0.0)
            {
                _layerGains = _targetGains.DeepCopy();
                return false;
            }
            double step = deltaSeconds / CrossfadeSeconds;
            bool changed = false;
            foreach (object layerId in new List<object>(_targetGains.Keys))
            {
                double target = V.F64(_targetGains[layerId]);
                double current = V.F64(_layerGains.Get(layerId, target));
                if (Math.Abs(current - target) < 0.0001)
                {
                    if (current != target)
                    {
                        _layerGains[layerId] = target;
                        changed = true;
                    }
                    continue;
                }
                double newGain = current + (target - current) * step;
                if ((target > current && newGain > target) || (target < current && newGain < target))
                    newGain = target;
                _layerGains[layerId] = GdMath.Clampf(newGain, 0.0, 1.0);
                changed = true;
            }
            return changed;
        }

        public GdDict GetLayerGains() => _layerGains.DeepCopy();

        public string GetState() => _state;

        public GdDict GetTargetGains() => _targetGains.DeepCopy();

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            return new List<string>
            {
                "Music: state=" + _state
                    + " base=" + GdString.FormatFixed(V.F64(_layerGains.Get(AudioEventSeam.MUSIC_LAYER_BASE, 0.0)), 2)
                    + " tension_drone=" + GdString.FormatFixed(V.F64(_layerGains.Get(AudioEventSeam.MUSIC_LAYER_TENSION_DRONE, 0.0)), 2)
                    + " combat_perc=" + GdString.FormatFixed(V.F64(_layerGains.Get(AudioEventSeam.MUSIC_LAYER_COMBAT_PERCUSSION, 0.0)), 2)
                    + " critical_pad=" + GdString.FormatFixed(V.F64(_layerGains.Get(AudioEventSeam.MUSIC_LAYER_CRITICAL_PAD, 0.0)), 2),
            };
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "kind", "dynamic_music_state" },
                { "state", _state },
                { "crossfade_seconds", CrossfadeSeconds },
                { "engagement_flag", _engagementFlag },
                { "hazard_active", _hazardActive },
                { "vitals_critical", _vitalsCritical },
                { "layer_gains", AsStrKeys(_layerGains) },
                { "target_gains", AsStrKeys(_targetGains) },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (V.Str(summary.Get("kind", "")) != "dynamic_music_state") return false;
            bool changed = false;
            if (summary.Has("crossfade_seconds"))
            {
                double newCf = GdMath.Clampf(V.F64(summary["crossfade_seconds"]), 0.1, 30.0);
                if (Math.Abs(newCf - CrossfadeSeconds) > 0.001)
                {
                    CrossfadeSeconds = newCf;
                    changed = true;
                }
            }
            if (summary.Has("state"))
            {
                string newState = V.Str(summary["state"]);
                if (IsKnownState(newState) && newState != _state)
                {
                    OverrideState(newState);
                    changed = true;
                }
            }
            if (summary.Has("engagement_flag")) _engagementFlag = V.Bool(summary["engagement_flag"]);
            if (summary.Has("hazard_active")) _hazardActive = V.Bool(summary["hazard_active"]);
            if (summary.Has("vitals_critical")) _vitalsCritical = V.Bool(summary["vitals_critical"]);
            if (summary.Has("layer_gains") && summary["layer_gains"] is GdDict lg)
            {
                _layerGains.Clear();
                foreach (var kv in lg) _layerGains[V.Str(kv.Key)] = GdMath.Clampf(V.F64(kv.Value), 0.0, 1.0);
                changed = true;
            }
            if (summary.Has("target_gains") && summary["target_gains"] is GdDict tg)
            {
                _targetGains.Clear();
                foreach (var kv in tg) _targetGains[V.Str(kv.Key)] = GdMath.Clampf(V.F64(kv.Value), 0.0, 1.0);
                changed = true;
            }
            return changed;
        }

        static bool IsKnownState(string stateId)
        {
            foreach (object known in AudioEventSeam.ALL_MUSIC_STATES)
            {
                if (V.Str(known) == stateId) return true;
            }
            return false;
        }

        GdDict TargetGainsForState(string stateId)
        {
            if (!(STATE_TARGET_GAINS.Get(stateId) is GdDict entry)) return _layerGains.DeepCopy();
            var result = new GdDict();
            foreach (object layerId in AudioEventSeam.ALL_MUSIC_LAYERS)
                result[layerId] = V.F64(entry.Get(layerId, 0.0));
            return result;
        }

        static GdDict AsStrKeys(GdDict gains)
        {
            var result = new GdDict();
            foreach (var kv in gains) result[V.Str(kv.Key)] = V.F64(kv.Value);
            return result;
        }
    }
}
