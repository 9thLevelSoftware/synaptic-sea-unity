// Ported from scripts/systems/effect_dispatcher.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Shared effect executor for consumables, medicine, stimulants, ammo, and
    /// utility items. Pure-model only: mutates the supplied state objects, never the
    /// scene tree.
    /// </summary>
    /// <remarks>
    /// The GDScript context is a Dictionary of model objects (<c>{"vitals_state": vitals, ...}</c>), which a
    /// <see cref="GdDict"/> cannot hold, so it is an <c>IDictionary&lt;string, object&gt;</c> here. The
    /// <c>has_method(...)</c> duck-typing checks become the nested target interfaces below.
    /// </remarks>
    public sealed class EffectDispatcher : ISimModel, IStatusLineProvider
    {
        /// <summary>VitalsState surface: <c>apply_delta(delta) -> Dictionary</c>.</summary>
        public interface IVitalsTarget
        {
            GdDict ApplyDelta(GdDict delta);
            GdDict GetSummary();
        }

        /// <summary>SanityState surface: <c>adjust_sanity(amount) -> float</c>.</summary>
        public interface ISanityTarget
        {
            double AdjustSanity(double amount);
            GdDict GetSummary();
        }

        /// <summary>RadiationState surface: <c>adjust_radiation(amount) -> float</c>.</summary>
        public interface IRadiationTarget
        {
            double AdjustRadiation(double amount);
            GdDict GetSummary();
        }

        /// <summary>BodyTemperatureState surface: <c>adjust_temperature(amount) -> float</c>.</summary>
        public interface IBodyTemperatureTarget
        {
            double AdjustTemperature(double amount);
            GdDict GetSummary();
        }

        /// <summary>StatusEffectsState surface: <c>add_effect(id, duration, stacks)</c> / <c>remove_effect(id, stacks)</c>.</summary>
        public interface IStatusEffectsTarget
        {
            bool AddEffect(string effectId, double duration, long stacks = 1);
            bool RemoveEffect(string effectId, long stacks = 1);
            GdDict GetSummary();
        }

        public const string DEFINITIONS_PATH = "res://data/items/effect_definitions.json";

        public GdDict EffectDefinitions = new GdDict();

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            EffectDefinitions = LoadDefinitions();
            if (config.Has("effect_definitions") && config["effect_definitions"] is GdDict defs)
                EffectDefinitions = defs.DeepCopy();
        }

        public GdDict DispatchEffect(string effectId, IDictionary<string, object> context, GdDict overrides = null)
        {
            if (EffectDefinitions.IsEmpty) EffectDefinitions = LoadDefinitions();
            object raw = EffectDefinitions.Get(effectId, new GdDict());
            if (!(raw is GdDict rawDef))
                return new GdDict { { "ok", false }, { "effect_id", effectId }, { "reason", "unknown_effect" } };
            GdDict definition = rawDef.DeepCopy();
            if (overrides != null)
                foreach (var kv in overrides) definition[kv.Key] = kv.Value;
            return DispatchInline(effectId, definition, context);
        }

        static object Ctx(IDictionary<string, object> context, string key) =>
            context != null && context.TryGetValue(key, out object v) ? v : null;

        public GdDict DispatchInline(string effectId, GdDict definition, IDictionary<string, object> context)
        {
            string kind = V.Str(definition.Get("kind", ""));
            var result = new GdDict { { "ok", true }, { "effect_id", effectId }, { "kind", kind } };
            switch (kind)
            {
                case "vitals_delta":
                {
                    if (!(Ctx(context, "vitals_state") is IVitalsTarget vitals))
                        return MissingTarget(effectId, "vitals_state");
                    vitals.ApplyDelta(new GdDict
                    {
                        { "health", V.F64(definition.Get("health", 0.0)) },
                        { "stamina", V.F64(definition.Get("stamina", 0.0)) },
                        { "hunger", V.F64(definition.Get("hunger", 0.0)) },
                        { "thirst", V.F64(definition.Get("thirst", 0.0)) },
                    });
                    result["summary"] = vitals.GetSummary();
                    break;
                }
                case "sanity_delta":
                {
                    if (!(Ctx(context, "sanity_state") is ISanityTarget sanity))
                        return MissingTarget(effectId, "sanity_state");
                    sanity.AdjustSanity(V.F64(definition.Get("amount", 0.0)));
                    result["summary"] = sanity.GetSummary();
                    break;
                }
                case "radiation_delta":
                {
                    if (!(Ctx(context, "radiation_state") is IRadiationTarget radiation))
                        return MissingTarget(effectId, "radiation_state");
                    radiation.AdjustRadiation(V.F64(definition.Get("amount", 0.0)));
                    result["summary"] = radiation.GetSummary();
                    break;
                }
                case "temperature_delta":
                {
                    if (!(Ctx(context, "body_temperature_state") is IBodyTemperatureTarget temp))
                        return MissingTarget(effectId, "body_temperature_state");
                    temp.AdjustTemperature(V.F64(definition.Get("amount", 0.0)));
                    result["summary"] = temp.GetSummary();
                    break;
                }
                case "add_status":
                {
                    if (!(Ctx(context, "status_effects_state") is IStatusEffectsTarget statuses))
                        return MissingTarget(effectId, "status_effects_state");
                    string statusId = V.Str(definition.Get("status_id", effectId));
                    double duration = System.Math.Max(0.1, V.F64(definition.Get("duration", 1.0)));
                    long stacks = System.Math.Max(1L, V.I64(definition.Get("stacks", 1L)));
                    statuses.AddEffect(statusId, duration, stacks);
                    result["status_id"] = statusId;
                    result["summary"] = statuses.GetSummary();
                    break;
                }
                case "cure_status":
                {
                    if (!(Ctx(context, "status_effects_state") is IStatusEffectsTarget statuses))
                        return MissingTarget(effectId, "status_effects_state");
                    var cured = new GdArray();
                    if (definition.Get("status_ids", new GdArray()) is GdArray ids)
                    {
                        foreach (object statusIdVariant in ids)
                        {
                            string statusId = V.Str(statusIdVariant);
                            if (statuses.RemoveEffect(statusId, 9999)) cured.Add(statusId);
                        }
                    }
                    result["cured"] = cured;
                    result["summary"] = statuses.GetSummary();
                    break;
                }
                default:
                    result["ok"] = false;
                    result["reason"] = "unsupported_kind";
                    break;
            }
            return result;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "effect_count", (long)EffectDefinitions.Count },
                { "effect_ids", new GdArray(EffectDefinitions.Keys) },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null) return false;
            if (summary.Has("effect_definitions") && summary["effect_definitions"] is GdDict defs)
            {
                EffectDefinitions = defs.DeepCopy();
                return true;
            }
            return false;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            return new List<string> { "Effects: " + ItemsCompat.D(EffectDefinitions.Count) };
        }

        GdDict LoadDefinitions() => ItemsCompat.ReadJson(DEFINITIONS_PATH) as GdDict ?? new GdDict();

        static GdDict MissingTarget(string effectId, string target) =>
            new GdDict { { "ok", false }, { "effect_id", effectId }, { "reason", "missing_target" }, { "target", target } };
    }
}
