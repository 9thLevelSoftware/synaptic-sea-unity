// Ported from scripts/systems/detection_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Player stealth/detection model: noise/light/sight inputs, crouch, awareness score, and memory decay.</summary>
    public sealed class DetectionState : ISimModel, ITickable, IStatusLineProvider
    {
        public const double DEFAULT_MEMORY_SECONDS = 5.0;
        public const double DEFAULT_DETECT_THRESHOLD = 0.75;

        public double NoiseLevel = 0.0;
        public double LightLevel = 0.0;
        public double SightLevel = 0.0;
        public bool Crouching = false;
        public string RoomId = "";
        public double DetectThreshold = DEFAULT_DETECT_THRESHOLD;
        public double MemorySeconds = DEFAULT_MEMORY_SECONDS;
        public double MemoryRemaining = 0.0;
        public double AwarenessScore = 0.0;
        public bool Detected = false;
        public bool Heard = false;
        public bool Seen = false;
        public string LastReason = "idle";

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            DetectThreshold = Math.Max(0.0, V.F64(config.Get("detect_threshold", DEFAULT_DETECT_THRESHOLD)));
            MemorySeconds = Math.Max(0.0, V.F64(config.Get("memory_seconds", DEFAULT_MEMORY_SECONDS)));
            NoiseLevel = GdMath.Clampf(V.F64(config.Get("noise_level", 0.0)), 0.0, 2.0);
            LightLevel = GdMath.Clampf(V.F64(config.Get("light_level", 0.0)), 0.0, 2.0);
            SightLevel = GdMath.Clampf(V.F64(config.Get("sight_level", 0.0)), 0.0, 2.0);
            Crouching = V.Bool(config.Get("crouching", false));
            RoomId = V.Str(config.Get("room_id", ""));
            MemoryRemaining = Math.Max(0.0, V.F64(config.Get("memory_remaining", 0.0)));
            Detected = V.Bool(config.Get("detected", false));
            Heard = V.Bool(config.Get("heard", false));
            Seen = V.Bool(config.Get("seen", false));
            AwarenessScore = GdMath.Clampf(V.F64(config.Get("awareness_score", 0.0)), 0.0, 3.0);
            LastReason = V.Str(config.Get("last_reason", "idle"));
        }

        public void UpdateInputs(double noise, double light, double sight, bool isCrouching, string currentRoomId = "")
        {
            NoiseLevel = GdMath.Clampf(noise, 0.0, 2.0);
            LightLevel = GdMath.Clampf(light, 0.0, 2.0);
            SightLevel = GdMath.Clampf(sight, 0.0, 2.0);
            Crouching = isCrouching;
            RoomId = currentRoomId;
        }

        /// <summary>
        /// Domain 2: the player's emitted detectability profile, the single signal the threat AI consumes.
        /// Crouch is applied here once; the AI must not re-apply it.
        /// </summary>
        public GdDict GetEmittedProfile()
        {
            double crouchMult = Crouching ? 0.65 : 1.0;
            return new GdDict
            {
                { "noise", NoiseLevel * crouchMult },
                { "light", LightLevel * crouchMult },
                { "visibility", SightLevel * crouchMult },
            };
        }

        /// <summary>Recomputes awareness from the current inputs; <paramref name="weights"/> may override per-signal weights.</summary>
        public bool Tick(double delta, GdDict weights = null)
        {
            if (delta < 0.0) return false;
            weights = weights ?? new GdDict();
            double noiseWeight = Math.Max(0.0, V.F64(weights.Get("noise_weight", 1.0)));
            double lightWeight = Math.Max(0.0, V.F64(weights.Get("light_weight", 1.0)));
            double sightWeight = Math.Max(0.0, V.F64(weights.Get("sight_weight", 1.0)));
            double crouchMult = V.F64(weights.Get("crouch_multiplier", Crouching ? 0.65 : 1.0));
            AwarenessScore = GdMath.Clampf((NoiseLevel * noiseWeight + LightLevel * lightWeight + SightLevel * sightWeight) * crouchMult, 0.0, 3.0);
            Heard = NoiseLevel * noiseWeight * crouchMult >= DetectThreshold;
            Seen = (LightLevel * lightWeight + SightLevel * sightWeight) * crouchMult >= DetectThreshold;
            if (AwarenessScore >= DetectThreshold)
            {
                Detected = true;
                MemoryRemaining = MemorySeconds;
                LastReason = Heard && !Seen ? "sound" : (Seen && !Heard ? "sight" : "combined");
            }
            else if (MemoryRemaining > 0.0)
            {
                MemoryRemaining = Math.Max(0.0, MemoryRemaining - delta);
                Detected = MemoryRemaining > 0.0;
                LastReason = "memory";
            }
            else
            {
                Detected = false;
                LastReason = "idle";
            }
            return true;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "noise_level", NoiseLevel },
                { "light_level", LightLevel },
                { "sight_level", SightLevel },
                { "crouching", Crouching },
                { "room_id", RoomId },
                { "detect_threshold", DetectThreshold },
                { "memory_seconds", MemorySeconds },
                { "memory_remaining", MemoryRemaining },
                { "awareness_score", AwarenessScore },
                { "detected", Detected },
                { "heard", Heard },
                { "seen", Seen },
                { "last_reason", LastReason },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            string before = GdJson.Stringify(GetSummary());
            Configure(summary);
            return before != GdJson.Stringify(GetSummary());
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Detection: score=" + SurvivalCompat.FormatF(AwarenessScore, 2) +
                      " detected=" + V.Str(Detected).ToLowerInvariant() + " reason=" + LastReason);
            lines.Add("Stealth: noise=" + SurvivalCompat.FormatF(NoiseLevel, 2) + " light=" + SurvivalCompat.FormatF(LightLevel, 2) +
                      " sight=" + SurvivalCompat.FormatF(SightLevel, 2) + " crouch=" + V.Str(Crouching).ToLowerInvariant());
            return lines;
        }
    }
}
