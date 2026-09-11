// Ported from scripts/systems/food_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for individual food item freshness and consumable effects.
    /// Per-item spoilage stage (Fresh -> Stale -> Rotten) affects hunger,
    /// sanity, and sickness risk when consumed. Never touches the scene tree.
    /// </summary>
    public sealed class FoodState : ISimModel, IStatusLineProvider
    {
        public const double STALE_THRESHOLD = 0.5;  // >50% elapsed = Stale
        public const double ROTTEN_THRESHOLD = 1.0; // >=100% elapsed = Rotten

        public enum Stage { FRESH = 0, STALE = 1, ROTTEN = 2 }

        public string ItemId = "";
        public string DisplayName = "";
        /// <summary>GDScript <c>stage</c> (an int holding a <see cref="Stage"/> value).</summary>
        public long CurrentStage = (long)Stage.FRESH;
        public double ElapsedSeconds = 0.0;
        public double TotalSpoilageSeconds = 3600.0;
        public double HungerRestore = 0.0;
        public double ThirstRestore = 0.0;
        public double SanityRestore = 0.0;
        public double FreshMultiplier = 1.0;
        public double StaleMultiplier = 0.6;
        public double RottenMultiplier = 0.2;
        public double RottenSicknessRisk = 0.25;
        public string Icon = "";

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            ItemId = V.Str(config.Get("item_id", ""));
            DisplayName = V.Str(config.Get("display_name", ItemId));
            CurrentStage = (long)Stage.FRESH;
            ElapsedSeconds = 0.0;
            TotalSpoilageSeconds = Math.Max(1.0, V.F64(config.Get("spoilage_seconds", 3600.0)));
            HungerRestore = V.F64(config.Get("hunger_restore", 0.0));
            ThirstRestore = V.F64(config.Get("thirst_restore", 0.0));
            SanityRestore = V.F64(config.Get("sanity_restore", 0.0));
            FreshMultiplier = V.F64(config.Get("fresh_multiplier", 1.0));
            StaleMultiplier = V.F64(config.Get("stale_multiplier", 0.6));
            RottenMultiplier = V.F64(config.Get("rotten_multiplier", 0.2));
            RottenSicknessRisk = V.F64(config.Get("rotten_sickness_risk", 0.25));
            Icon = V.Str(config.Get("icon", ""));
        }

        public bool Tick(double deltaSeconds)
        {
            if (deltaSeconds <= 0.0) return false;
            long before = CurrentStage;
            ElapsedSeconds += deltaSeconds;
            double progress = ElapsedSeconds / TotalSpoilageSeconds;
            if (progress >= ROTTEN_THRESHOLD) CurrentStage = (long)Stage.ROTTEN;
            else if (progress >= STALE_THRESHOLD) CurrentStage = (long)Stage.STALE;
            else CurrentStage = (long)Stage.FRESH;
            return CurrentStage != before;
        }

        public GdDict GetEffectiveRestores()
        {
            double mult = FreshMultiplier;
            if (CurrentStage == (long)Stage.STALE) mult = StaleMultiplier;
            else if (CurrentStage == (long)Stage.ROTTEN) mult = RottenMultiplier;
            return new GdDict
            {
                { "hunger", HungerRestore * mult },
                { "thirst", ThirstRestore * mult },
                { "sanity", SanityRestore * mult },
                { "sickness_risk", CurrentStage == (long)Stage.ROTTEN ? RottenSicknessRisk : 0.0 },
            };
        }

        /// <summary>Consumption removes the item; the caller decrements inventory.</summary>
        public GdDict Consume() => GetEffectiveRestores();

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "item_id", ItemId },
                { "display_name", DisplayName },
                { "stage", CurrentStage },
                { "elapsed_seconds", ElapsedSeconds },
                { "total_spoilage_seconds", TotalSpoilageSeconds },
                { "hunger_restore", HungerRestore },
                { "thirst_restore", ThirstRestore },
                { "sanity_restore", SanityRestore },
                { "fresh_multiplier", FreshMultiplier },
                { "stale_multiplier", StaleMultiplier },
                { "rotten_multiplier", RottenMultiplier },
                { "rotten_sickness_risk", RottenSicknessRisk },
                { "icon", Icon },
            };
        }

        /// <summary>Returns true when any field changed (not whether the summary was accepted), like the GDScript.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            string newId = V.Str(summary.Get("item_id", ItemId));
            if (newId != ItemId)
            {
                ItemId = newId;
                changed = true;
            }
            long newStage = V.I64(summary.Get("stage", CurrentStage));
            if (newStage != CurrentStage)
            {
                CurrentStage = newStage;
                changed = true;
            }
            double newElapsed = V.F64(summary.Get("elapsed_seconds", ElapsedSeconds));
            if (Math.Abs(newElapsed - ElapsedSeconds) > 0.001)
            {
                ElapsedSeconds = newElapsed;
                changed = true;
            }
            double newTotal = V.F64(summary.Get("total_spoilage_seconds", TotalSpoilageSeconds));
            if (Math.Abs(newTotal - TotalSpoilageSeconds) > 0.001)
            {
                TotalSpoilageSeconds = Math.Max(1.0, newTotal);
                changed = true;
            }
            double newHunger = V.F64(summary.Get("hunger_restore", HungerRestore));
            if (Math.Abs(newHunger - HungerRestore) > 0.001)
            {
                HungerRestore = newHunger;
                changed = true;
            }
            double newThirst = V.F64(summary.Get("thirst_restore", ThirstRestore));
            if (Math.Abs(newThirst - ThirstRestore) > 0.001)
            {
                ThirstRestore = newThirst;
                changed = true;
            }
            double newSanity = V.F64(summary.Get("sanity_restore", SanityRestore));
            if (Math.Abs(newSanity - SanityRestore) > 0.001)
            {
                SanityRestore = newSanity;
                changed = true;
            }
            return changed;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            string name = "FRESH";
            if (CurrentStage == (long)Stage.STALE) name = "STALE";
            else if (CurrentStage == (long)Stage.ROTTEN) name = "ROTTEN";
            lines.Add("Food: " + DisplayName + " [" + name + "]");
            long pct = (long)GdMath.Round((ElapsedSeconds / Math.Max(1.0, TotalSpoilageSeconds)) * 100.0);
            lines.Add("  spoil=" + ItemsCompat.D(pct) + "%");
            GdDict eff = GetEffectiveRestores();
            lines.Add("  hunger=+" + ItemsCompat.Fmt(V.F64(eff["hunger"]), 1)
                + " thirst=+" + ItemsCompat.Fmt(V.F64(eff["thirst"]), 1)
                + " sanity=" + ItemsCompat.Fmt(V.F64(eff["sanity"]), 1, showSign: true));
            if (V.F64(eff["sickness_risk"]) > 0.0)
                lines.Add("  sickness_risk=" + ItemsCompat.Fmt(V.F64(eff["sickness_risk"]) * 100.0, 0) + "%");
            return lines;
        }

        public static string StageName(long s)
        {
            if (s == (long)Stage.STALE) return "STALE";
            if (s == (long)Stage.ROTTEN) return "ROTTEN";
            return "FRESH";
        }
    }
}
