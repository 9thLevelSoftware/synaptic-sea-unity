// Ported from scripts/systems/spatial_audio_resolver.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Deterministic spatial attenuation and occlusion (REQ-AU-005, ADR-0029). A pure function of
    /// (emitter_pos, listener_pos, occluded, base_db): linear dB falloff between ref_distance and max_distance,
    /// plus an occlusion penalty. Positions are Godot-frame <see cref="Vec3"/> (float32 like Vector3).
    /// </summary>
    public sealed class SpatialAudioResolver : ISimModel, IStatusLineProvider
    {
        public const double DEFAULT_REF_DISTANCE = 2.0;
        public const double DEFAULT_MAX_DISTANCE = 25.0;
        public const double DEFAULT_MAX_ATTENUATION_DB = -36.0;
        public const double DEFAULT_OCCLUSION_PENALTY_DB = -6.0;

        public double RefDistance = DEFAULT_REF_DISTANCE;
        public double MaxDistance = DEFAULT_MAX_DISTANCE;
        public double MaxAttenuationDb = DEFAULT_MAX_ATTENUATION_DB;
        public double OcclusionPenaltyDb = DEFAULT_OCCLUSION_PENALTY_DB;

        public void Configure(GdDict config)
        {
            if (config == null) return;
            if (config.Has("ref_distance"))
                RefDistance = GdMath.Clampf(V.F64(config["ref_distance"]), 0.01, 1000.0);
            if (config.Has("max_distance"))
                MaxDistance = GdMath.Clampf(V.F64(config["max_distance"]), RefDistance + 0.01, 10000.0);
            if (config.Has("max_attenuation_db"))
                MaxAttenuationDb = GdMath.Clampf(V.F64(config["max_attenuation_db"]), -120.0, 0.0);
            if (config.Has("occlusion_penalty_db"))
                OcclusionPenaltyDb = GdMath.Clampf(V.F64(config["occlusion_penalty_db"]), -60.0, 0.0);
        }

        /// <summary>
        /// Final dB for an emitter heard by a listener. Identical positions return base_db; non-finite position
        /// components are zeroed; a non-finite result clamps to -60.
        /// </summary>
        public double ResolveVolumeDb(Vec3 emitterPos, Vec3 listenerPos, bool occluded, double baseDb)
        {
            Vec3 ep = SafeVector(emitterPos);
            Vec3 lp = SafeVector(listenerPos);
            double distance = (ep - lp).Length();
            if (!IsFinite(distance)) distance = 0.0;
            double attenuation = AttenuationForDistance(distance);
            double penalty = occluded ? OcclusionPenaltyDb : 0.0;
            double result = baseDb + attenuation + penalty;
            if (!IsFinite(result))
            {
                // Callers must never see NaN/Inf in a volume_db property.
                result = -60.0;
            }
            return result;
        }

        double AttenuationForDistance(double distance)
        {
            if (MaxDistance <= RefDistance) return distance > RefDistance ? MaxAttenuationDb : 0.0;
            if (distance <= RefDistance) return 0.0;
            if (distance >= MaxDistance) return MaxAttenuationDb;
            double t = (distance - RefDistance) / (MaxDistance - RefDistance);
            return MaxAttenuationDb * t;
        }

        static Vec3 SafeVector(Vec3 v)
        {
            return new Vec3(
                !IsFinite(v.X) ? 0f : v.X,
                !IsFinite(v.Y) ? 0f : v.Y,
                !IsFinite(v.Z) ? 0f : v.Z);
        }

        static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        /// <summary>Distance between two points (float32 Vector3 length widened to float).</summary>
        public static double Distance(Vec3 emitterPos, Vec3 listenerPos) => (emitterPos - listenerPos).Length();

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Spatial: ref=" + SurvivalCompat.FormatF(RefDistance, 2) + " max=" + SurvivalCompat.FormatF(MaxDistance, 2) +
                      " max_atten=" + SurvivalCompat.FormatF(MaxAttenuationDb, 2) +
                      " occlusion=" + SurvivalCompat.FormatF(OcclusionPenaltyDb, 2));
            return lines;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "kind", "spatial_audio_resolver" },
                { "ref_distance", RefDistance },
                { "max_distance", MaxDistance },
                { "max_attenuation_db", MaxAttenuationDb },
                { "occlusion_penalty_db", OcclusionPenaltyDb },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (V.Str(summary.Get("kind", "")) != "spatial_audio_resolver") return false;
            bool changed = false;
            if (summary.Has("ref_distance"))
            {
                double newRd = GdMath.Clampf(V.F64(summary["ref_distance"]), 0.01, 1000.0);
                if (Math.Abs(newRd - RefDistance) > 0.001)
                {
                    RefDistance = newRd;
                    changed = true;
                }
            }
            if (summary.Has("max_distance"))
            {
                double newMd = GdMath.Clampf(V.F64(summary["max_distance"]), RefDistance + 0.01, 10000.0);
                if (Math.Abs(newMd - MaxDistance) > 0.001)
                {
                    MaxDistance = newMd;
                    changed = true;
                }
            }
            if (summary.Has("max_attenuation_db"))
            {
                double newMa = GdMath.Clampf(V.F64(summary["max_attenuation_db"]), -120.0, 0.0);
                if (Math.Abs(newMa - MaxAttenuationDb) > 0.001)
                {
                    MaxAttenuationDb = newMa;
                    changed = true;
                }
            }
            if (summary.Has("occlusion_penalty_db"))
            {
                double newOp = GdMath.Clampf(V.F64(summary["occlusion_penalty_db"]), -60.0, 0.0);
                if (Math.Abs(newOp - OcclusionPenaltyDb) > 0.001)
                {
                    OcclusionPenaltyDb = newOp;
                    changed = true;
                }
            }
            return changed;
        }
    }
}
