// Ported from scripts/systems/field_crafting_state.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for portable/field crafting. A subset of recipes with station_kind == "field_crafting" can be
    /// executed without a powered station. Uses the same recipe catalog as CraftingState but enforces the
    /// field-only restriction and skips quality bonuses from station level/power. Never touches the scene tree.
    /// </summary>
    /// <remarks>
    /// The duck-typed <c>inventory</c> is a <see cref="CargoTransfer.ICargoStore"/> (as in <see cref="CraftingState"/>);
    /// <c>material_state</c> is a <see cref="MaterialState"/>.
    /// </remarks>
    public sealed class FieldCraftingState : IStatusLineProvider
    {
        readonly CraftingState _craftingState = new CraftingState();

        /// <summary>Returns field-craftable recipes (station_kind == "field_crafting").</summary>
        public GdArray GetFieldRecipes() => _craftingState.GetRecipesForStation("field_crafting");

        /// <summary>
        /// REQ-CS-016 field residual: listing for the portable recipe picker. Field crafting is intentionally
        /// skill-ungated for start (skill only affects quality), so entries use a high skill level for status and
        /// only report missing_ingredients / output_full as blockers.
        /// </summary>
        public GdArray ListRecipeEntries(CargoTransfer.ICargoStore inventory) =>
            _craftingState.ListRecipeEntries("field_crafting", inventory, 999);

        public string FirstReadyRecipeId(CargoTransfer.ICargoStore inventory)
        {
            foreach (object entry in ListRecipeEntries(inventory))
            {
                if (entry is GdDict d && V.Bool(d.Get("craftable", false)))
                    return V.Str(d.Get("recipe_id", ""));
            }
            return "";
        }

        public bool CanCraft(string recipeId, CargoTransfer.ICargoStore inventory)
        {
            GdDict recipe = _craftingState.GetRecipe(recipeId);
            if (recipe.IsEmpty)
                return false;
            if (V.Str(recipe.Get("station_kind", "")) != "field_crafting")
                return false;
            return _craftingState.CanCraft(recipeId, inventory);
        }

        /// <summary>Begins a field craft. Quality is resolved with station_level=0 and powered=false.</summary>
        public bool BeginCraft(string recipeId, CargoTransfer.ICargoStore inventory, MaterialState materialState, long playerSkillLevel)
        {
            GdDict recipe = _craftingState.GetRecipe(recipeId);
            if (recipe.IsEmpty)
                return false;
            if (V.Str(recipe.Get("station_kind", "")) != "field_crafting")
                return false;
            if (!CanCraft(recipeId, inventory))
                return false;
            // Use the base CraftingState logic but force a synthetic field station
            var station = new StationState();
            // Field crafting is intentionally unpowered for quality resolution, but the portable craft itself must
            // still progress without entering the station's PAUSED_POWER state.
            station.Configure(new GdDict { { "station_kind", "field_crafting" }, { "level", 0L }, { "powered", true } });
            _craftingState.SetStationState("field_crafting", station);
            _craftingState.ConsumeIngredients(recipeId, inventory);
            double avgQuality = 0.5;
            object ingredients = recipe.Get("ingredients", new GdDict());
            if (ingredients is GdDict ingredientsDict)
                avgQuality = materialState.AverageIngredientQuality(ingredientsDict);
            var resolver = new QualityTierResolver();
            GdDict qualityResult = resolver.Resolve(avgQuality, playerSkillLevel, 0, false);
            _craftingState.SetActiveCraft(new GdDict
            {
                { "recipe_id", recipeId },
                { "station_kind", "field_crafting" },
                { "quality_tier", qualityResult["tier"] },
                { "quality_multiplier", qualityResult["multiplier"] },
                { "quality_score", qualityResult["score"] },
            });
            double craftTime = V.F64(recipe.Get("craft_time_seconds", 0.0));
            station.StartRecipe(recipeId, craftTime);
            return true;
        }

        public bool Tick(double deltaSeconds) => _craftingState.Tick(deltaSeconds);

        public GdDict FinishCraft() => _craftingState.FinishCraft();

        public bool IsCrafting() => _craftingState.IsCrafting();

        public string GetActiveRecipeId() => _craftingState.GetActiveRecipeId();

        public void CancelCraft() => _craftingState.CancelCraft();

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "field_crafting", _craftingState.GetSummary() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            object fc = summary.Get("field_crafting", new GdDict());
            if (fc is GdDict fcDict)
                return _craftingState.ApplySummary(fcDict);
            return false;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Field Crafting");
            foreach (string line in _craftingState.GetStatusLines())
                lines.Add(line);
            return lines;
        }
    }
}
