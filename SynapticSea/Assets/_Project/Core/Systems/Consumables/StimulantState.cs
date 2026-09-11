// Ported from scripts/systems/stimulant_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Timed stimulant buff tracker. Active debuffs/buffs are mirrored into
    /// StatusEffectsState through EffectDispatcher, but this model owns the use
    /// cadence, tolerance scaling, and expiry handoff into AddictionState.
    /// </summary>
    public sealed class StimulantState : ISimModel, IStatusLineProvider
    {
        public GdArray ActiveStims = new GdArray();
        public string LastUsedItem = "";

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            ActiveStims = new GdArray();
            LastUsedItem = "";
            if (config.Get("active_stims", new GdArray()) is GdArray raw)
                ActiveStims = raw.DeepCopy();
        }

        public GdDict UseStimulant(string itemId, GdDict definition, EffectDispatcher dispatcher, AddictionState addictionState, IDictionary<string, object> context)
        {
            LastUsedItem = itemId;
            double tolerance = addictionState != null ? addictionState.GetTolerance(itemId) : 0.0;
            double durationScale = GdMath.Clampf(1.0 - tolerance * 0.08, 0.45, 1.0);
            double duration = Math.Max(1.0, V.F64(definition.Get("stim_duration", 20.0)) * durationScale);
            var applied = new GdArray();
            if (definition.Get("effects", new GdArray()) is GdArray effects)
            {
                foreach (object effectIdVariant in effects)
                {
                    string effectId = V.Str(effectIdVariant);
                    GdDict res = dispatcher.DispatchEffect(effectId, context, new GdDict { { "duration", duration } });
                    if (V.Bool(res.Get("ok", false))) applied.Add(effectId);
                }
            }
            ActiveStims.Add(new GdDict
            {
                { "item_id", itemId },
                { "remaining", duration },
                { "base_duration", V.F64(definition.Get("stim_duration", duration)) },
                { "effects", applied.ShallowCopy() },
                { "withdrawal_effects", definition.Get("withdrawal_effects", new GdArray()) is GdArray we ? we.ShallowCopy() : new GdArray() },
            });
            addictionState?.RecordDose(itemId, definition);
            return new GdDict { { "ok", true }, { "item_id", itemId }, { "duration", duration }, { "effects", applied } };
        }

        public bool Tick(double deltaSeconds, AddictionState addictionState, IDictionary<string, object> context)
        {
            if (deltaSeconds <= 0.0) return false;
            bool changed = false;
            object statusEffectsState = null;
            context?.TryGetValue("status_effects_state", out statusEffectsState);
            for (int i = ActiveStims.Count - 1; i >= 0; i--)
            {
                var entry = (GdDict)ActiveStims[i];
                entry["remaining"] = Math.Max(0.0, V.F64(entry.Get("remaining", 0.0)) - deltaSeconds);
                changed = true;
                if (V.F64(entry.Get("remaining", 0.0)) <= 0.0)
                {
                    ActiveStims.RemoveAt(i);
                    addictionState?.ActivateWithdrawalIfNeeded(V.Str(entry.Get("item_id", "")), statusEffectsState as EffectDispatcher.IStatusEffectsTarget);
                }
                else
                {
                    ActiveStims[i] = entry;
                }
            }
            return changed;
        }

        public bool HasActiveStim(string itemId = "")
        {
            foreach (object entryVariant in ActiveStims)
            {
                var entry = (GdDict)entryVariant;
                if (string.IsNullOrEmpty(itemId) || V.Str(entry.Get("item_id", "")) == itemId) return true;
            }
            return false;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "active_stims", ActiveStims.DeepCopy() },
                { "last_used_item", LastUsedItem },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            Configure(new GdDict { { "active_stims", summary.Get("active_stims", new GdArray()) } });
            LastUsedItem = V.Str(summary.Get("last_used_item", LastUsedItem));
            return true;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            foreach (object entryVariant in ActiveStims)
            {
                var entry = (GdDict)entryVariant;
                lines.Add("Stim " + V.Str(entry.Get("item_id", "")) + " " + ItemsCompat.Fmt(V.F64(entry.Get("remaining", 0.0)), 1) + "s");
            }
            return lines;
        }
    }
}
