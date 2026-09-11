// Ported from scripts/systems/cooking_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for cooking station state machine (RETIRED from the live run).
    /// Consumes ingredients and power, ticks a timer, produces food items.
    /// Superseded by the ADR-0038 kitchen crafting station; kept so historical smokes/docs
    /// can still reference the shape.
    /// </summary>
    public sealed class CookingState : ISimModel, IStatusLineProvider
    {
        public enum State { IDLE = 0, COOKING = 1, COMPLETE = 2 }

        public string RecipeId = "";
        public string RecipeName = "";
        /// <summary>item_id -> quantity required.</summary>
        public GdDict Ingredients = new GdDict();
        public string ProducesItemId = "";
        public long ProducesQuantity = 1;
        public double PowerCost = 0.0;
        public double CookTimeSeconds = 0.0;
        public long RequiredSkillLevel = 0;
        public string StationKind = "galley";

        /// <summary>GDScript <c>state</c> (an int holding a <see cref="State"/> value).</summary>
        public long CurrentState = (long)State.IDLE;
        public double ProgressSeconds = 0.0;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            RecipeId = V.Str(config.Get("recipe_id", ""));
            RecipeName = V.Str(config.Get("display_name", RecipeId));
            object ing = config.Get("ingredients", new GdDict());
            Ingredients = ing as GdDict ?? new GdDict(); // by reference, like the GDScript
            if (config.Get("produces", new GdDict()) is GdDict prod)
            {
                ProducesItemId = V.Str(prod.Get("item_id", ""));
                ProducesQuantity = V.I64(prod.Get("quantity", 1L));
            }
            else
            {
                ProducesItemId = "";
                ProducesQuantity = 1;
            }
            PowerCost = V.F64(config.Get("power_cost", 0.0));
            CookTimeSeconds = Math.Max(0.1, V.F64(config.Get("cook_time_seconds", 0.0)));
            RequiredSkillLevel = V.I64(config.Get("required_skill_level", 0L));
            StationKind = V.Str(config.Get("station_kind", "galley"));
            CurrentState = (long)State.IDLE;
            ProgressSeconds = 0.0;
        }

        /// <summary>Start cooking. inventory_summary shape: {"items": {"item_id": qty, ...}}.</summary>
        public GdDict StartCooking(GdDict inventorySummary, long skillLevel, double availablePower)
        {
            if (CurrentState != (long)State.IDLE) return new GdDict { { "ok", false }, { "reason", "not_idle" } };
            if (skillLevel < RequiredSkillLevel) return new GdDict { { "ok", false }, { "reason", "insufficient_skill" } };
            if (availablePower < PowerCost) return new GdDict { { "ok", false }, { "reason", "insufficient_power" } };
            inventorySummary = inventorySummary ?? new GdDict();
            var invItems = inventorySummary.Get("items", inventorySummary) as GdDict ?? new GdDict();
            foreach (var kv in Ingredients)
            {
                long needed = V.I64(kv.Value);
                long have = V.I64(invItems.Get(kv.Key, 0L));
                if (have < needed)
                    return new GdDict { { "ok", false }, { "reason", "missing_ingredient_" + V.Str(kv.Key) } };
            }
            CurrentState = (long)State.COOKING;
            ProgressSeconds = 0.0;
            return new GdDict { { "ok", true }, { "reason", "" }, { "power_consumed", PowerCost } };
        }

        public bool Tick(double deltaSeconds)
        {
            if (CurrentState != (long)State.COOKING) return false;
            if (deltaSeconds <= 0.0) return false;
            ProgressSeconds += deltaSeconds;
            if (ProgressSeconds >= CookTimeSeconds)
            {
                CurrentState = (long)State.COMPLETE;
                return true;
            }
            return false;
        }

        public double GetProgressRatio()
        {
            if (CookTimeSeconds <= 0.0) return 0.0;
            return GdMath.Clampf(ProgressSeconds / CookTimeSeconds, 0.0, 1.0);
        }

        public bool IsComplete() => CurrentState == (long)State.COMPLETE;

        public GdDict CollectResult()
        {
            if (CurrentState != (long)State.COMPLETE)
                return new GdDict { { "ok", false }, { "item_id", "" }, { "quantity", 0L } };
            var outDict = new GdDict
            {
                { "ok", true },
                { "item_id", ProducesItemId },
                { "quantity", ProducesQuantity },
            };
            CurrentState = (long)State.IDLE;
            ProgressSeconds = 0.0;
            return outDict;
        }

        public void Cancel()
        {
            CurrentState = (long)State.IDLE;
            ProgressSeconds = 0.0;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "recipe_id", RecipeId },
                { "recipe_name", RecipeName },
                { "state", CurrentState },
                { "progress_seconds", ProgressSeconds },
                { "cook_time_seconds", CookTimeSeconds },
                { "progress_ratio", GetProgressRatio() },
                { "ingredients", Ingredients.DeepCopy() },
                { "produces_item_id", ProducesItemId },
                { "produces_quantity", ProducesQuantity },
                { "power_cost", PowerCost },
                { "required_skill_level", RequiredSkillLevel },
                { "station_kind", StationKind },
            };
        }

        /// <summary>Returns true when any field changed. Note: <c>ingredients</c> is not restored (GDScript parity).</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            string newRid = V.Str(summary.Get("recipe_id", RecipeId));
            if (newRid != RecipeId) { RecipeId = newRid; changed = true; }
            string newName = V.Str(summary.Get("recipe_name", RecipeName));
            if (newName != RecipeName) { RecipeName = newName; changed = true; }
            long newState = V.I64(summary.Get("state", CurrentState));
            if (newState != CurrentState) { CurrentState = newState; changed = true; }
            double newProgress = V.F64(summary.Get("progress_seconds", ProgressSeconds));
            if (Math.Abs(newProgress - ProgressSeconds) > 0.001) { ProgressSeconds = newProgress; changed = true; }
            double newCookTime = V.F64(summary.Get("cook_time_seconds", CookTimeSeconds));
            if (Math.Abs(newCookTime - CookTimeSeconds) > 0.001) { CookTimeSeconds = Math.Max(0.1, newCookTime); changed = true; }
            string newProd = V.Str(summary.Get("produces_item_id", ProducesItemId));
            if (newProd != ProducesItemId) { ProducesItemId = newProd; changed = true; }
            long newQty = V.I64(summary.Get("produces_quantity", ProducesQuantity));
            if (newQty != ProducesQuantity) { ProducesQuantity = newQty; changed = true; }
            double newPower = V.F64(summary.Get("power_cost", PowerCost));
            if (Math.Abs(newPower - PowerCost) > 0.001) { PowerCost = newPower; changed = true; }
            long newSkill = V.I64(summary.Get("required_skill_level", RequiredSkillLevel));
            if (newSkill != RequiredSkillLevel) { RequiredSkillLevel = newSkill; changed = true; }
            string newKind = V.Str(summary.Get("station_kind", StationKind));
            if (newKind != StationKind) { StationKind = newKind; changed = true; }
            return changed;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            string stateName = "IDLE";
            if (CurrentState == (long)State.COOKING) stateName = "COOKING";
            else if (CurrentState == (long)State.COMPLETE) stateName = "COMPLETE";
            lines.Add("Cooking: " + RecipeName + " [" + stateName + "]");
            if (CurrentState == (long)State.COOKING)
                lines.Add("  progress=" + ItemsCompat.D((long)GdMath.Round(GetProgressRatio() * 100.0)) + "%");
            else if (CurrentState == (long)State.COMPLETE)
                lines.Add("  ready: " + ProducesItemId + " x" + ItemsCompat.D(ProducesQuantity));
            return lines;
        }
    }
}
