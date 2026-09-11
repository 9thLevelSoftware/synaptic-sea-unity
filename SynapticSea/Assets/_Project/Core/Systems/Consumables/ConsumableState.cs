// Ported from scripts/systems/consumable_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Shared consumable/effect pipeline. Owns hotbar slot assignments and routes use
    /// requests into MedicineState / StimulantState / AmmoState / UtilityItemResolver
    /// through one EffectDispatcher.
    /// </summary>
    /// <remarks>
    /// The GDScript <c>pipeline_context</c> Dictionary holds model objects, so (like <see cref="EffectDispatcher"/>)
    /// it is an <c>IDictionary&lt;string, object&gt;</c> here. Recognised keys: <c>effect_dispatcher</c>
    /// (<see cref="EffectDispatcher"/>), <c>medicine_state</c> (<see cref="MedicineState"/>), <c>stimulant_state</c>
    /// (<see cref="StimulantState"/>), <c>addiction_state</c> (<see cref="AddictionState"/>), <c>utility_state</c>
    /// (<see cref="UtilityItemResolver"/>), <c>spoilage_state</c> (<see cref="ISpoilageFoodSource"/>),
    /// <c>vitals_state</c> (<see cref="EffectDispatcher.IVitalsTarget"/>), <c>sanity_state</c>
    /// (<see cref="EffectDispatcher.ISanityTarget"/>), plus whatever the dispatcher targets read.
    /// The duck-typed <c>inventory_state</c> is a <see cref="CargoTransfer.ICargoStore"/>.
    /// </remarks>
    public sealed class ConsumableState : ISimModel, IStatusLineProvider
    {
        /// <summary>SpoilageState <c>get_food(item_id) -> FoodState</c> (null when untracked).</summary>
        public interface ISpoilageFoodSource
        {
            FoodState GetFood(string itemId);
        }

        public const long HOTBAR_SLOT_COUNT = 3;

        static readonly GdArray UseCategories = GdArray.Of("medicine", "stimulant", "ammo", "utility", "food", "drink");

        public GdDict Definitions = new GdDict();
        public List<string> HotbarSlots = new List<string>();
        public GdDict LastResult = new GdDict();

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            Definitions = ItemDefs.LoadDefinitions();
            HotbarSlots = new List<string>();
            for (long i = 0; i < HOTBAR_SLOT_COUNT; i++)
                HotbarSlots.Add("");
            object rawSlots = config.Get("hotbar_slots", new GdArray());
            if (rawSlots is GdArray rawArr)
            {
                for (int i = 0; i < Math.Min((int)HOTBAR_SLOT_COUNT, rawArr.Count); i++)
                    HotbarSlots[i] = V.Str(rawArr[i]);
            }
            LastResult = new GdDict();
        }

        public bool HasUseAction(string itemId)
        {
            GdDict definition = ItemDefs.GetDefinition(Definitions, itemId);
            string category = V.Str(definition.Get("category", ""));
            return UseCategories.Contains(category);
        }

        public bool AssignHotbarSlot(long slotIndex, string itemId)
        {
            if (slotIndex < 0 || slotIndex >= HOTBAR_SLOT_COUNT)
                return false;
            if (!string.IsNullOrEmpty(itemId) && !HasUseAction(itemId))
                return false;
            HotbarSlots[(int)slotIndex] = itemId ?? "";
            return true;
        }

        public GdDict UseHotbarSlot(long slotIndex, CargoTransfer.ICargoStore inventoryState, IDictionary<string, object> pipelineContext)
        {
            if (slotIndex < 0 || slotIndex >= HotbarSlots.Count)
                return new GdDict { { "ok", false }, { "reason", "bad_slot" } };
            return UseItem(HotbarSlots[(int)slotIndex], inventoryState, pipelineContext, false);
        }

        public GdDict UseItem(string itemId, CargoTransfer.ICargoStore inventoryState, IDictionary<string, object> pipelineContext, bool useAll = false)
        {
            if (string.IsNullOrEmpty(itemId) || inventoryState == null)
                return new GdDict { { "ok", false }, { "reason", "missing_item" } };
            long quantity = inventoryState.GetQuantity(itemId);
            if (quantity <= 0)
                return new GdDict { { "ok", false }, { "reason", "missing_quantity" }, { "item_id", itemId } };
            GdDict definition = ItemDefs.GetDefinition(Definitions, itemId);
            if (definition.IsEmpty)
                return new GdDict { { "ok", false }, { "reason", "unknown_definition" }, { "item_id", itemId } };
            string category = V.Str(definition.Get("category", ""));
            long iterations = useAll ? quantity : 1;
            iterations = Math.Max(1L, iterations);
            long successes = 0;
            var results = new GdArray();
            for (long i = 0; i < iterations; i++)
            {
                if (inventoryState.GetQuantity(itemId) <= 0)
                    break;
                GdDict result = UseOnce(itemId, category, definition, inventoryState, pipelineContext);
                results.Add(result);
                if (!V.Bool(result.Get("ok", false)))
                    break;
                successes += 1;
            }
            LastResult = new GdDict
            {
                { "item_id", itemId },
                { "category", category },
                { "use_all", useAll },
                { "used", successes },
                { "results", results.DeepCopy() },
            };
            return new GdDict { { "ok", successes > 0 }, { "item_id", itemId }, { "used", successes }, { "results", results.DeepCopy() } };
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "hotbar_slots", GdString.ToGdArray(HotbarSlots) },
                { "last_result", LastResult.DeepCopy() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            Configure(new GdDict { { "hotbar_slots", summary.Get("hotbar_slots", new GdArray()) } });
            LastResult = summary.Get("last_result", new GdDict()) is GdDict lr ? lr.DeepCopy() : new GdDict();
            return true;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            for (int i = 0; i < HotbarSlots.Count; i++)
            {
                string itemId = HotbarSlots[i];
                if (itemId.Length == 0)
                    lines.Add("Hotbar " + GdString.FormatInt(i + 1) + ": (empty)");
                else
                    lines.Add("Hotbar " + GdString.FormatInt(i + 1) + ": " + ItemDefs.DisplayName(Definitions, itemId));
            }
            return lines;
        }

        static object Ctx(IDictionary<string, object> context, string key) =>
            context != null && context.TryGetValue(key, out object v) ? v : null;

        GdDict UseOnce(string itemId, string category, GdDict definition, CargoTransfer.ICargoStore inventoryState, IDictionary<string, object> pipelineContext)
        {
            var dispatcher = Ctx(pipelineContext, "effect_dispatcher") as EffectDispatcher;
            switch (category)
            {
                case "medicine":
                {
                    var med = Ctx(pipelineContext, "medicine_state") as MedicineState;
                    if (med == null || dispatcher == null)
                        return new GdDict { { "ok", false }, { "reason", "medicine_pipeline_missing" } };
                    GdDict medResult = med.UseMedicine(itemId, definition, dispatcher, pipelineContext);
                    if (V.Bool(medResult.Get("ok", false)))
                        inventoryState.RemoveItem(itemId, 1);
                    return medResult;
                }
                case "stimulant":
                {
                    var stim = Ctx(pipelineContext, "stimulant_state") as StimulantState;
                    if (stim == null || dispatcher == null)
                        return new GdDict { { "ok", false }, { "reason", "stimulant_pipeline_missing" } };
                    GdDict stimResult = stim.UseStimulant(itemId, definition, dispatcher, Ctx(pipelineContext, "addiction_state") as AddictionState, pipelineContext);
                    if (V.Bool(stimResult.Get("ok", false)))
                        inventoryState.RemoveItem(itemId, 1);
                    return stimResult;
                }
                case "ammo":
                {
                    // Domain 5: ammo reserve lives in inventory; the magazine (AmmoState) is
                    // loaded via the reload pipeline (KEY_R). Per-round items (flare_round,
                    // capacitor_cell, fuel_canister) carry no effects and return ok=false so they
                    // are never consumed via the hotbar — only the reload path touches them.
                    if (dispatcher == null)
                        return new GdDict { { "ok", false }, { "reason", "effect_dispatcher_missing" } };
                    object effects = definition.Get("effects", new GdArray());
                    var ammoResults = new GdArray();
                    bool anyOk = false;
                    if (effects is GdArray effectsArr)
                    {
                        foreach (object effectIdVariant in effectsArr)
                        {
                            GdDict er = dispatcher.DispatchEffect(V.Str(effectIdVariant), pipelineContext);
                            ammoResults.Add(er);
                            if (V.Bool(er.Get("ok", false)))
                                anyOk = true;
                        }
                    }
                    if (anyOk)
                    {
                        if (inventoryState != null)
                            inventoryState.RemoveItem(itemId, 1);
                        return new GdDict { { "ok", true }, { "item_id", itemId }, { "results", ammoResults } };
                    }
                    return new GdDict { { "ok", false }, { "reason", "ammo_no_effect" } };
                }
                case "utility":
                {
                    var utility = Ctx(pipelineContext, "utility_state") as UtilityItemResolver;
                    if (utility == null || dispatcher == null)
                        return new GdDict { { "ok", false }, { "reason", "utility_pipeline_missing" } };
                    GdDict utilityResult = utility.UseItem(itemId, definition, dispatcher, pipelineContext);
                    if (V.Bool(utilityResult.Get("ok", false)))
                        inventoryState.RemoveItem(itemId, 1);
                    return utilityResult;
                }
                case "food":
                case "drink":
                {
                    if (dispatcher == null)
                        return new GdDict { { "ok", false }, { "reason", "effect_dispatcher_missing" } };
                    // Legacy/explicit effects path — kept for any food that declares an effects array.
                    object effects = definition.Get("effects", new GdArray());
                    if (effects is GdArray effectsArr)
                    {
                        foreach (object effectIdVariant in effectsArr)
                            dispatcher.DispatchEffect(V.Str(effectIdVariant), pipelineContext);
                    }
                    // REQ-FC: apply the food/drink's hunger/thirst/sanity restores to live vitals.
                    // Routed through FoodState so the spoilage multiplier is honoured.
                    GdDict restored = ApplyFoodRestores(itemId, definition, pipelineContext);
                    inventoryState.RemoveItem(itemId, 1);
                    return new GdDict
                    {
                        { "ok", true }, { "item_id", itemId }, { "category", category },
                        { "hunger_restored", V.F64(restored.Get("hunger", 0.0)) },
                        { "thirst_restored", V.F64(restored.Get("thirst", 0.0)) },
                        { "sanity_restored", V.F64(restored.Get("sanity", 0.0)) },
                    };
                }
            }
            return new GdDict { { "ok", false }, { "reason", "unsupported_category" }, { "category", category } };
        }

        /// <summary>
        /// REQ-FC: applies a food/drink definition's spoilage-scaled restores to the live
        /// vitals_state / sanity_state in the pipeline context. Uses per-item tracked spoilage
        /// stage from spoilage_state when available; falls back to FRESH stage when the item has no
        /// tracked entry or spoilage_state is absent. Returns the applied {hunger, thirst, sanity} amounts.
        /// </summary>
        GdDict ApplyFoodRestores(string itemId, GdDict definition, IDictionary<string, object> pipelineContext)
        {
            // Always configure from the item definition so base restore values are correct.
            var food = new FoodState();
            food.Configure(definition);
            // Override the stage from the per-item tracked spoilage entry if available.
            if (Ctx(pipelineContext, "spoilage_state") is ISpoilageFoodSource spoilageState)
            {
                FoodState tracked = spoilageState.GetFood(itemId);
                if (tracked != null)
                    food.CurrentStage = tracked.CurrentStage;
            }
            GdDict r = food.GetEffectiveRestores();
            double hunger = V.F64(r.Get("hunger", 0.0));
            double thirst = V.F64(r.Get("thirst", 0.0));
            double sanity = V.F64(r.Get("sanity", 0.0));
            if (Ctx(pipelineContext, "vitals_state") is EffectDispatcher.IVitalsTarget vitals && (hunger != 0.0 || thirst != 0.0))
                vitals.ApplyDelta(new GdDict { { "hunger", hunger }, { "thirst", thirst } });
            if (sanity != 0.0 && Ctx(pipelineContext, "sanity_state") is EffectDispatcher.ISanityTarget sanityState)
                sanityState.AdjustSanity(sanity);
            return new GdDict { { "hunger", hunger }, { "thirst", thirst }, { "sanity", sanity } };
        }
    }
}
