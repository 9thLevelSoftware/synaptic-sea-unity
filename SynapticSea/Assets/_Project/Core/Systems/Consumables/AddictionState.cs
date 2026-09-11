// Ported from scripts/systems/addiction_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Tracks stimulant tolerance, dependence, and withdrawal windows. The active
    /// withdrawal debuffs themselves are carried by StatusEffectsState; this model is
    /// the durable state that decides when to start/stop them.
    /// </summary>
    public sealed class AddictionState : ISimModel, IStatusLineProvider
    {
        public const string TUNING_PATH = "res://data/items/addiction_tuning.json";

        public GdDict Tuning = new GdDict();
        public GdDict Profiles = new GdDict();

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            Tuning = LoadTuning();
            Profiles.Clear();
            if (config.Get("profiles", new GdDict()) is GdDict raw)
            {
                // Values are stored by reference, as in GDScript.
                foreach (var kv in raw) Profiles[V.Str(kv.Key)] = kv.Value;
            }
        }

        public void RecordDose(string itemId, GdDict definition)
        {
            GdDict profile = ProfileFor(itemId);
            profile["tolerance"] = GdMath.Clampf(V.F64(profile.Get("tolerance", 0.0)) + V.F64(definition.Get("tolerance_gain", 0.25)), 0.0, 5.0);
            profile["dependence"] = GdMath.Clampf(V.F64(profile.Get("dependence", 0.0)) + V.F64(definition.Get("dependence_gain", 0.34)), 0.0, 10.0);
            profile["withdrawal_duration"] = Math.Max(V.F64(profile.Get("withdrawal_duration", 0.0)), V.F64(definition.Get("withdrawal_duration", DefaultWithdrawalDuration(itemId))));
            profile["withdrawal_effects"] = definition.Get("withdrawal_effects", new GdArray()) is GdArray effects ? effects.ShallowCopy() : new GdArray();
            Profiles[itemId] = profile;
        }

        public GdDict ActivateWithdrawalIfNeeded(string itemId, EffectDispatcher.IStatusEffectsTarget statusEffectsState)
        {
            GdDict profile = ProfileFor(itemId);
            double threshold = WithdrawalThreshold(itemId);
            if (V.F64(profile.Get("dependence", 0.0)) < threshold)
                return new GdDict { { "ok", false }, { "reason", "below_threshold" }, { "item_id", itemId } };
            double duration = Math.Max(V.F64(profile.Get("withdrawal_duration", 0.0)), DefaultWithdrawalDuration(itemId));
            profile["withdrawal_remaining"] = duration;
            Profiles[itemId] = profile;
            var applied = new GdArray();
            object effects = profile.Get("withdrawal_effects", new GdArray());
            if (effects is GdArray effectList && statusEffectsState != null)
            {
                foreach (object effectIdVariant in effectList)
                {
                    string effectId = V.Str(effectIdVariant);
                    statusEffectsState.AddEffect(effectId, duration, 1);
                    applied.Add(effectId);
                }
            }
            return new GdDict { { "ok", true }, { "item_id", itemId }, { "applied", applied }, { "duration", duration } };
        }

        public bool Tick(double deltaSeconds, EffectDispatcher.IStatusEffectsTarget statusEffectsState = null)
        {
            if (deltaSeconds <= 0.0) return false;
            bool changed = false;
            foreach (object itemId in new List<object>(Profiles.Keys))
            {
                // GDScript: `var profile: Dictionary = profiles[item_id]` (mutated in place).
                if (!(Profiles[itemId] is GdDict profile)) continue;
                double tolerance = V.F64(profile.Get("tolerance", 0.0));
                if (tolerance > 0.0)
                {
                    profile["tolerance"] = Math.Max(0.0, tolerance - deltaSeconds * 0.01);
                    changed = true;
                }
                double dependence = V.F64(profile.Get("dependence", 0.0));
                if (dependence > 0.0)
                {
                    profile["dependence"] = Math.Max(0.0, dependence - deltaSeconds * 0.002);
                    changed = true;
                }
                double remaining = V.F64(profile.Get("withdrawal_remaining", 0.0));
                if (remaining > 0.0)
                {
                    remaining = Math.Max(0.0, remaining - deltaSeconds);
                    profile["withdrawal_remaining"] = remaining;
                    changed = true;
                    if (remaining <= 0.0 && statusEffectsState != null)
                    {
                        if (profile.Get("withdrawal_effects", new GdArray()) is GdArray effects)
                            foreach (object effectIdVariant in effects)
                                statusEffectsState.RemoveEffect(V.Str(effectIdVariant), 9999);
                    }
                }
                Profiles[itemId] = profile;
            }
            return changed;
        }

        public double GetTolerance(string itemId) => V.F64(ProfileFor(itemId).Get("tolerance", 0.0));

        public bool HasWithdrawal()
        {
            foreach (var kv in Profiles)
                if ((kv.Value as GdDict).GetFloat("withdrawal_remaining", 0.0) > 0.0) return true;
            return false;
        }

        public GdDict GetSummary()
        {
            return new GdDict { { "profiles", Profiles.DeepCopy() } };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            Configure(new GdDict { { "profiles", summary.Get("profiles", new GdDict()) } });
            return true;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            foreach (object itemId in new List<object>(Profiles.Keys))
            {
                var profile = Profiles[itemId] as GdDict ?? new GdDict();
                double dependence = V.F64(profile.Get("dependence", 0.0));
                double remaining = V.F64(profile.Get("withdrawal_remaining", 0.0));
                if (dependence > 0.0)
                    lines.Add("Addiction " + V.Str(itemId) + " dep=" + ItemsCompat.Fmt(dependence, 2) + " tol=" + ItemsCompat.Fmt(V.F64(profile.Get("tolerance", 0.0)), 2));
                if (remaining > 0.0)
                    lines.Add("Withdrawal " + V.Str(itemId) + " " + ItemsCompat.Fmt(remaining, 1) + "s");
            }
            return lines;
        }

        GdDict ProfileFor(string itemId)
        {
            object raw = Profiles.Get(itemId, new GdDict());
            if (raw is GdDict dict) return dict.DeepCopy();
            return new GdDict
            {
                { "tolerance", 0.0 },
                { "dependence", 0.0 },
                { "withdrawal_remaining", 0.0 },
                { "withdrawal_duration", DefaultWithdrawalDuration(itemId) },
                { "withdrawal_effects", DefaultWithdrawalEffects(itemId) },
            };
        }

        static GdDict LoadTuning() => ItemsCompat.ReadJson(TUNING_PATH) as GdDict ?? new GdDict();

        double WithdrawalThreshold(string itemId)
        {
            if (Tuning.Get(itemId, new GdDict()) is GdDict raw) return V.F64(raw.Get("withdrawal_threshold", 1.0));
            return 1.0;
        }

        double DefaultWithdrawalDuration(string itemId)
        {
            if (Tuning.Get(itemId, new GdDict()) is GdDict raw) return V.F64(raw.Get("withdrawal_duration", 25.0));
            return 25.0;
        }

        GdArray DefaultWithdrawalEffects(string itemId)
        {
            if (Tuning.Get(itemId, new GdDict()) is GdDict raw && raw.Get("withdrawal_effects", new GdArray()) is GdArray effects)
                return effects.ShallowCopy();
            return new GdArray();
        }
    }
}
