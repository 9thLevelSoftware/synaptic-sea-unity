// Ported from scripts/systems/status_effects_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Pure registry for active status effects (REQ-SV-005): id, remaining duration, stacks.</summary>
    public sealed class StatusEffectsState : ISimModel, ITickable, IStatusLineProvider
    {
        /// <summary>Array of { "id": String, "duration": float, "stacks": int }.</summary>
        public GdArray Effects = new GdArray();

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            Effects.Clear();
            object raw = config.Get("effects", new GdArray());
            if (raw is GdArray entries)
            {
                foreach (object entryVariant in entries)
                {
                    if (entryVariant is GdDict entry)
                        AddEffect(V.Str(entry.Get("id", "")), V.F64(entry.Get("duration", 0.0)), V.I64(entry.Get("stacks", 1L)));
                }
            }
        }

        public bool AddEffect(string effectId, double duration, long stacks = 1)
        {
            if (effectId.Length == 0 || duration <= 0.0 || stacks <= 0) return false;
            foreach (object item in Effects)
            {
                var e = (GdDict)item;
                if (V.Str(e.Get("id", "")) == effectId)
                {
                    e["stacks"] = V.I64(e.Get("stacks", 0L)) + stacks;
                    e["duration"] = Math.Max(V.F64(e.Get("duration", 0.0)), duration);
                    return true;
                }
            }
            Effects.Append(new GdDict { { "id", effectId }, { "duration", duration }, { "stacks", stacks } });
            return true;
        }

        public bool RemoveEffect(string effectId, long stacks = 1)
        {
            for (int i = 0; i < Effects.Count; i++)
            {
                var e = (GdDict)Effects[i];
                if (V.Str(e.Get("id", "")) == effectId)
                {
                    long current = V.I64(e.Get("stacks", 0L));
                    if (current <= stacks)
                        Effects.RemoveAt(i);
                    else
                        e["stacks"] = current - stacks;
                    return true;
                }
            }
            return false;
        }

        public bool Tick(double deltaSeconds, GdDict context = null)
        {
            if (deltaSeconds <= 0.0) return false;
            bool changed = false;
            int i = Effects.Count - 1;
            while (i >= 0)
            {
                var e = (GdDict)Effects[i];
                double remaining = V.F64(e.Get("duration", 0.0)) - deltaSeconds;
                if (remaining <= 0.0)
                {
                    Effects.RemoveAt(i);
                    changed = true;
                }
                else
                {
                    e["duration"] = remaining;
                    changed = true;
                }
                i -= 1;
            }
            return changed;
        }

        public bool HasEffect(string effectId)
        {
            foreach (object item in Effects)
            {
                if (V.Str(((GdDict)item).Get("id", "")) == effectId) return true;
            }
            return false;
        }

        public long GetStacks(string effectId)
        {
            foreach (object item in Effects)
            {
                var e = (GdDict)item;
                if (V.Str(e.Get("id", "")) == effectId) return V.I64(e.Get("stacks", 0L));
            }
            return 0;
        }

        /// <summary>Composite multiplier for a stat key: the product of every matching effect's multiplier (default 1.0).</summary>
        public double GetModifier(string statKey)
        {
            double mult = 1.0;
            // Hard-coded effect table for the core package.
            foreach (object item in Effects)
            {
                string id = V.Str(((GdDict)item).Get("id", ""));
                switch (id)
                {
                    case "radiation_sickness":
                        if (statKey == "stamina_recovery") mult *= 0.75;
                        if (statKey == "health_recovery") mult *= 0.5;
                        break;
                    case "hunger_weakened":
                        if (statKey == "stamina_recovery") mult *= 0.5;
                        break;
                    case "thirst_dazed":
                        if (statKey == "vision_clarity") mult *= 0.6;
                        break;
                    case "sanity_fractured":
                        if (statKey == "perception_clarity") mult *= 0.5;
                        break;
                    case "stim_focus":
                        if (statKey == "stamina_recovery") mult *= 1.25;
                        break;
                    case "stim_haste":
                        if (statKey == "stamina_recovery") mult *= 1.5;
                        break;
                    case "stim_steady":
                        if (statKey == "stamina_recovery") mult *= 1.15;
                        break;
                    case "withdrawal_shakes":
                    case "withdrawal_fatigue":
                        if (statKey == "stamina_recovery") mult *= 0.5;
                        break;
                }
            }
            return mult;
        }

        public GdDict GetSummary()
        {
            var output = new GdArray();
            foreach (object item in Effects)
            {
                var e = (GdDict)item;
                output.Append(new GdDict
                {
                    { "id", V.Str(e.Get("id", "")) },
                    { "duration", V.F64(e.Get("duration", 0.0)) },
                    { "stacks", V.I64(e.Get("stacks", 0L)) },
                });
            }
            return new GdDict
            {
                { "effects", output },
                { "count", (long)output.Count },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            object raw = summary.Get("effects", new GdArray());
            if (raw is GdArray entries)
            {
                var newEffects = new GdArray();
                foreach (object entryVariant in entries)
                {
                    if (entryVariant is GdDict entry)
                    {
                        newEffects.Append(new GdDict
                        {
                            { "id", V.Str(entry.Get("id", "")) },
                            { "duration", V.F64(entry.Get("duration", 0.0)) },
                            { "stacks", V.I64(entry.Get("stacks", 0L)) },
                        });
                    }
                }
                if (!V.VariantEquals(newEffects, Effects))
                {
                    Effects = newEffects;
                    changed = true;
                }
            }
            return changed;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (Effects.IsEmpty)
            {
                lines.Add("Status: none");
                return lines;
            }
            foreach (object item in Effects)
            {
                var e = (GdDict)item;
                string id = V.Str(e.Get("id", ""));
                long stacks = V.I64(e.Get("stacks", 0L));
                double dur = V.F64(e.Get("duration", 0.0));
                lines.Add("Status: " + id + " x" + SurvivalCompat.FormatD(stacks) + " (" + SurvivalCompat.FormatF(dur, 1) + "s)");
            }
            return lines;
        }
    }
}
