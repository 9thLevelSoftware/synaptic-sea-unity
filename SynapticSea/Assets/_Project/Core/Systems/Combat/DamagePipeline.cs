// Ported from scripts/systems/damage_pipeline.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The VitalsState member <see cref="DamagePipeline.ApplyToVitals"/> writes (<c>vitals_state.health</c>).
    /// VitalsState's C# port should implement it.
    /// </summary>
    public interface IDamageVitalsTarget
    {
        double Health { get; set; }
    }

    /// <summary>Routes hits through <see cref="ArmorResolver"/> into player vitals / status effects or a threat.</summary>
    public class DamagePipeline
    {
        public const string STATUS_DEFINITIONS_PATH = "res://data/combat/status_effect_definitions.json";

        public ArmorResolver ArmorResolver = new ArmorResolver();
        public GdDict StatusEffectDefinitions = new GdDict();
        public long ProcessedHits = 0;
        public double TotalDamageApplied = 0.0;
        public double TotalNoiseGenerated = 0.0;
        public GdDict LastResult = new GdDict();

        /// <summary>REQ-WA-003: optional callback (damage, event) after player vitals are hit.</summary>
        public Action<double, GdDict> OnPlayerDamaged;

        /// <summary>
        /// GDScript read the optional <c>on_player_damaged</c> Callable out of <paramref name="config"/>; a GdDict
        /// cannot hold a delegate, so it is the separate <paramref name="onPlayerDamaged"/> parameter
        /// (null clears it, exactly like a config without the key).
        /// </summary>
        public void Configure(GdDict config = null, Action<double, GdDict> onPlayerDamaged = null)
        {
            config = config ?? new GdDict();
            ArmorResolver.Configure(config.Get("armor_profile", new GdDict()) as GdDict);
            StatusEffectDefinitions = LoadStatusDefs(V.Str(config.Get("status_defs_path", STATUS_DEFINITIONS_PATH)));
            ProcessedHits = 0;
            TotalDamageApplied = 0.0;
            TotalNoiseGenerated = 0.0;
            LastResult = new GdDict();
            OnPlayerDamaged = onPlayerDamaged;
        }

        public GdDict ApplyToVitals(IDamageVitalsTarget vitalsState, EffectDispatcher.IStatusEffectsTarget statusEffectsState, GdDict armorProfile, GdDict @event)
        {
            GdDict resolved = ArmorResolver.ResolveDamage(@event, armorProfile);
            double dmg = V.F64(resolved.Get("final_damage", 0.0));
            ApplyVitalsDamage(vitalsState, dmg);
            ApplyStatus(statusEffectsState, V.Str(@event.Get("status_effect_id", "")), V.F64(@event.Get("status_duration", -1.0)));
            if (dmg > 0.0 && OnPlayerDamaged != null)
                OnPlayerDamaged(dmg, @event);
            return FinalizeResult(@event, resolved);
        }

        public GdDict ApplyToThreat(ThreatAIState threatState, GdDict @event)
        {
            if (threatState == null)
                return new GdDict();
            GdDict resolved = ArmorResolver.ResolveDamage(@event, threatState.ArmorProfile);
            resolved["stun_seconds"] = Math.Max(0.0, V.F64(@event.Get("stun_seconds", 0.0)));
            threatState.ApplyDamage(resolved);
            return FinalizeResult(@event, resolved);
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "processed_hits", ProcessedHits },
                { "total_damage_applied", TotalDamageApplied },
                { "total_noise_generated", TotalNoiseGenerated },
                { "last_result", LastResult.DeepCopy() },
                { "armor_resolver", ArmorResolver.GetSummary() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            ProcessedHits = V.I64(summary.Get("processed_hits", ProcessedHits));
            TotalDamageApplied = V.F64(summary.Get("total_damage_applied", TotalDamageApplied));
            TotalNoiseGenerated = V.F64(summary.Get("total_noise_generated", TotalNoiseGenerated));
            LastResult = summary.Get("last_result", new GdDict()) as GdDict ?? new GdDict();
            if (summary.Get("armor_resolver", null) is GdDict armor)
                ArmorResolver.ApplySummary(armor);
            return true;
        }

        public List<string> GetStatusLines()
        {
            return new List<string>
            {
                "Damage: hits=" + GdString.FormatInt(ProcessedHits) + " total=" + GdString.FormatFixed(TotalDamageApplied, 1),
                "Noise emitted: " + GdString.FormatFixed(TotalNoiseGenerated, 2),
            };
        }

        GdDict FinalizeResult(GdDict @event, GdDict resolved)
        {
            ProcessedHits += 1;
            double finalDamage = V.F64(resolved.Get("final_damage", 0.0));
            double noise = Math.Max(0.0, V.F64(@event.Get("noise", 0.0)));
            TotalDamageApplied += finalDamage;
            TotalNoiseGenerated += noise;
            LastResult = resolved.DeepCopy();
            LastResult["source_id"] = V.Str(@event.Get("source_id", ""));
            LastResult["status_effect_id"] = V.Str(@event.Get("status_effect_id", ""));
            LastResult["noise"] = noise;
            return LastResult.DeepCopy();
        }

        void ApplyVitalsDamage(IDamageVitalsTarget vitalsState, double damage)
        {
            if (vitalsState == null || damage <= 0.0)
                return;
            vitalsState.Health = Math.Max(0.0, vitalsState.Health - damage);
        }

        void ApplyStatus(EffectDispatcher.IStatusEffectsTarget statusEffectsState, string effectId, double explicitDuration)
        {
            if (statusEffectsState == null || effectId.Length == 0)
                return;
            GdDict effectDef = StatusEffectDefinitions.Get(effectId, new GdDict()) as GdDict ?? new GdDict();
            double duration = explicitDuration > 0.0 ? explicitDuration : V.F64(effectDef.Get("duration", 0.0));
            long stacks = V.I64(effectDef.Get("stacks", 1L));
            if (duration > 0.0)
                statusEffectsState.AddEffect(effectId, duration, stacks);
        }

        static GdDict LoadStatusDefs(string path)
        {
            if (path.Length == 0 || !CatalogRegistry.Exists(path))
                return new GdDict();
            object parsed = CatalogRegistry.Load(path);
            return parsed as GdDict ?? new GdDict();
        }
    }
}
