// Ported from scripts/systems/crafting_state.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for the crafting engine. Loads recipes, validates ingredient
    /// availability against an InventoryState, resolves output quality via
    /// QualityTierResolver, and manages active craft progress via StationState.
    /// Never touches the scene tree.
    /// </summary>
    /// <remarks>
    /// The duck-typed <c>inventory</c> argument is a <see cref="CargoTransfer.ICargoStore"/> (get_quantity /
    /// remove_item); the optional <c>can_accept</c> probe is <see cref="IItemAcceptor"/>. The duck-typed
    /// <c>knowledge</c> is a <see cref="RecipeKnowledgeState"/>, <c>catalog</c> a <see cref="ComponentCatalog"/>.
    /// </remarks>
    public sealed class CraftingState : IStatusLineProvider
    {
        public const string RECIPE_DEFINITIONS_PATH = "res://data/recipes/recipe_definitions.json";

        GdDict _recipes = new GdDict();                                               // recipe_id -> recipe Dictionary
        readonly Dictionary<string, StationState> _stationStates = new Dictionary<string, StationState>(); // station_kind -> StationState
        readonly List<string> _stationOrder = new List<string>();                     // insertion order of _stationStates
        GdDict _activeCraft = new GdDict();                                           // recipe_id, station_kind, progress tracking

        public CraftingState()
        {
            LoadRecipes();
        }

        void LoadRecipes()
        {
            if (!CatalogRegistry.Exists(RECIPE_DEFINITIONS_PATH))
                return;
            object parsed = CatalogRegistry.Load(RECIPE_DEFINITIONS_PATH);
            if (parsed is GdDict parsedDict)
            {
                object recipesArray = parsedDict.Get("recipes", new GdArray());
                if (recipesArray is GdArray arr)
                {
                    foreach (object recipeVariant in arr)
                    {
                        if (recipeVariant is GdDict recipe)
                        {
                            string rid = V.Str(recipe.Get("recipe_id", ""));
                            if (rid.Length != 0)
                                _recipes[rid] = recipe;
                        }
                    }
                }
            }
        }

        public long RecipeCount() => _recipes.Count;

        public GdDict GetRecipe(string recipeId)
        {
            object r = _recipes.Get(recipeId, new GdDict());
            return r as GdDict ?? new GdDict();
        }

        public GdArray GetRecipesForStation(string stationKind)
        {
            var outArr = new GdArray();
            foreach (var kv in _recipes)
            {
                var recipe = (GdDict)kv.Value;
                if (V.Str(recipe.Get("station_kind", "")) == stationKind)
                    outArr.Add(recipe.DeepCopy());
            }
            return outArr;
        }

        public GdArray GetRecipesByCategory(string category)
        {
            var outArr = new GdArray();
            foreach (var kv in _recipes)
            {
                var recipe = (GdDict)kv.Value;
                if (V.Str(recipe.Get("category", "")) == category)
                    outArr.Add(recipe.DeepCopy());
            }
            return outArr;
        }

        public GdArray GetAllRecipeIds()
        {
            var ids = new GdArray(_recipes.Keys);
            GdSort.Sort(ids);
            return ids;
        }

        public bool HasRecipe(string recipeId) => _recipes.Has(recipeId);

        // --- ingredient validation ---

        /// <summary>
        /// Returns true if the inventory has enough of every ingredient.
        /// PKG-B2.4a: optional knowledge gate. PKG-B2.4b: optional station_tier gate; defaults to 0 (base stations).
        /// </summary>
        public bool CanCraft(string recipeId, CargoTransfer.ICargoStore inventory, RecipeKnowledgeState knowledge = null, long stationTier = 0)
        {
            GdDict recipe = GetRecipe(recipeId);
            if (recipe.IsEmpty)
                return false;
            if (knowledge != null && !knowledge.IsKnown(recipeId))
                return false;
            long needTier = V.I64(recipe.Get("station_tier_min", 0L));
            if (stationTier < needTier)
                return false;
            object ingredients = recipe.Get("ingredients", new GdDict());
            if (!(ingredients is GdDict ingredientsDict))
                return false;
            foreach (var kv in ingredientsDict)
            {
                long need = V.I64(kv.Value);
                if (inventory.GetQuantity(V.Str(kv.Key)) < need)
                    return false;
            }
            return true;
        }

        /// <summary>Consumes ingredients from inventory. Returns true if successful.</summary>
        public bool ConsumeIngredients(string recipeId, CargoTransfer.ICargoStore inventory)
        {
            GdDict recipe = GetRecipe(recipeId);
            if (recipe.IsEmpty)
                return false;
            object ingredients = recipe.Get("ingredients", new GdDict());
            if (!(ingredients is GdDict ingredientsDict))
                return false;
            // Verify first
            foreach (var kv in ingredientsDict)
            {
                long need = V.I64(kv.Value);
                if (inventory.GetQuantity(V.Str(kv.Key)) < need)
                    return false;
            }
            // Consume
            foreach (var kv in ingredientsDict)
            {
                long need = V.I64(kv.Value);
                inventory.RemoveItem(V.Str(kv.Key), need);
            }
            return true;
        }

        /// <summary>Returns the produced item_id and base quantity for a recipe.</summary>
        public GdDict GetProduces(string recipeId)
        {
            GdDict recipe = GetRecipe(recipeId);
            object produces = recipe.Get("produces", new GdDict());
            if (produces is GdDict producesDict)
                return producesDict.ShallowCopy();
            return new GdDict();
        }

        public long GetRequiredSkillLevel(string recipeId) => V.I64(GetRecipe(recipeId).Get("required_skill_level", 0L));

        public string GetStationKind(string recipeId) => V.Str(GetRecipe(recipeId).Get("station_kind", ""));

        public double GetCraftTime(string recipeId) => V.F64(GetRecipe(recipeId).Get("craft_time_seconds", 0.0));

        public double GetPowerCost(string recipeId) => V.F64(GetRecipe(recipeId).Get("power_cost", 0.0));

        // PKG-B2.4b schema accessors
        public long GetStationTierMin(string recipeId) => V.I64(GetRecipe(recipeId).Get("station_tier_min", 0L));

        public string GetKnowledgeSource(string recipeId) => V.Str(GetRecipe(recipeId).Get("knowledge_source", "starter"));

        public string GetWorkVerb(string recipeId) => V.Str(GetRecipe(recipeId).Get("work_verb", "craft"));

        /// <summary>
        /// PKG-B2.4b: derive station tier from placed components that declare station_tier_bonus
        /// and optional station_kind affinity.
        /// </summary>
        public static long DeriveTierFromComponents(string stationKind, GdArray placed, ComponentCatalog catalog = null)
        {
            long best = 0;
            if (placed == null)
                return best;
            foreach (object entry in placed)
            {
                if (!(entry is GdDict e))
                    continue;
                if (!V.Bool(e.Get("mounted", true)))
                    continue;
                long bonus = V.I64(e.Get("station_tier_bonus", 0L));
                string affinity = V.Str(e.Get("station_affinity", ""));
                if (bonus <= 0 && catalog != null)
                {
                    GdDict def = catalog.GetComponent(V.Str(e.Get("component_id", "")));
                    bonus = V.I64(def.Get("station_tier_bonus", 0L));
                    if (affinity.Length == 0)
                        affinity = V.Str(def.Get("station_affinity", ""));
                }
                if (bonus <= 0)
                    continue;
                if (affinity.Length != 0 && affinity != stationKind && affinity != "any")
                    continue;
                if (bonus > best)
                    best = bonus;
            }
            return best;
        }

        /// <summary>
        /// Headless listing for the station recipe picker (REQ-CS-016).
        /// Returns Array[Dictionary] sorted by recipe_id. Excludes deconstruction recipes.
        /// Each entry: recipe_id, display_name, category, required_skill_level, station_tier_min, work_verb,
        /// knowledge_source, ingredients, produces, craft_time_seconds,
        /// status ("ready"|"missing_ingredients"|"insufficient_skill"|"insufficient_tier"|"output_full"), craftable.
        /// </summary>
        public GdArray ListRecipeEntries(string stationKind, CargoTransfer.ICargoStore inventory, long playerSkillLevel, long stationTier = 0)
        {
            var outArr = new GdArray();
            GdArray recipes = GetRecipesForStation(stationKind);
            recipes.SortCustom((a, b) => GdString.Less(V.Str(((GdDict)a).Get("recipe_id", "")), V.Str(((GdDict)b).Get("recipe_id", ""))));
            foreach (object recipeV in recipes)
            {
                if (!(recipeV is GdDict recipe))
                    continue;
                string rid = V.Str(recipe.Get("recipe_id", ""));
                if (rid.Length == 0)
                    continue;
                if (V.Str(recipe.Get("category", "")) == "deconstruction")
                    continue;
                long requiredSkill = V.I64(recipe.Get("required_skill_level", 0L));
                long tierMin = V.I64(recipe.Get("station_tier_min", 0L));
                var produces = new GdDict();
                if (recipe.Get("produces", new GdDict()) is GdDict producesRaw)
                    produces = producesRaw.ShallowCopy();
                var ingredients = new GdDict();
                if (recipe.Get("ingredients", new GdDict()) is GdDict ingredientsRaw)
                    ingredients = ingredientsRaw.ShallowCopy();
                string status = "ready";
                if (playerSkillLevel < requiredSkill)
                {
                    status = "insufficient_skill";
                }
                else if (stationTier < tierMin)
                {
                    status = "insufficient_tier";
                }
                else if (!CanCraft(rid, inventory, null, stationTier))
                {
                    status = "missing_ingredients";
                }
                else if (inventory is IItemAcceptor acceptor)
                {
                    string outId = V.Str(produces.Get("item_id", ""));
                    long outQty = V.I64(produces.Get("quantity", 0L));
                    if (outId.Length != 0 && outQty > 0 && !acceptor.CanAccept(outId, outQty))
                        status = "output_full";
                }
                outArr.Add(new GdDict
                {
                    { "recipe_id", rid },
                    { "display_name", V.Str(recipe.Get("display_name", rid)) },
                    { "category", V.Str(recipe.Get("category", "")) },
                    { "required_skill_level", requiredSkill },
                    { "station_tier_min", tierMin },
                    { "work_verb", V.Str(recipe.Get("work_verb", "craft")) },
                    { "knowledge_source", V.Str(recipe.Get("knowledge_source", "starter")) },
                    { "ingredients", ingredients },
                    { "produces", produces },
                    { "craft_time_seconds", V.F64(recipe.Get("craft_time_seconds", 0.0)) },
                    { "status", status },
                    { "craftable", status == "ready" },
                });
            }
            return outArr;
        }

        // --- station management ---

        public StationState GetOrCreateStation(string stationKind)
        {
            if (_stationStates.TryGetValue(stationKind, out StationState existing))
                return existing;
            var station = new StationState();
            station.Configure(new GdDict { { "station_kind", stationKind }, { "level", 0L }, { "powered", true } });
            _stationStates[stationKind] = station;
            _stationOrder.Add(stationKind);
            return station;
        }

        public StationState GetStation(string stationKind) =>
            _stationStates.TryGetValue(stationKind, out StationState station) ? station : null;

        public void RemoveStation(string stationKind)
        {
            if (_stationStates.Remove(stationKind))
                _stationOrder.Remove(stationKind);
        }

        /// <summary>
        /// FieldCraftingState seam: GDScript assigns <c>_crafting_state._station_states[kind] = station</c> directly
        /// (an existing key keeps its insertion position).
        /// </summary>
        internal void SetStationState(string stationKind, StationState station)
        {
            if (!_stationStates.ContainsKey(stationKind))
                _stationOrder.Add(stationKind);
            _stationStates[stationKind] = station;
        }

        /// <summary>FieldCraftingState seam: GDScript assigns <c>_crafting_state._active_craft = {...}</c> directly.</summary>
        internal void SetActiveCraft(GdDict activeCraft)
        {
            _activeCraft = activeCraft;
        }

        // --- crafting execution ---

        /// <summary>
        /// Begins crafting a recipe at its designated station. Returns true if started.
        /// Pre-conditions: ingredients available, station powered (or will pause).
        /// </summary>
        public bool BeginCraft(string recipeId, CargoTransfer.ICargoStore inventory, MaterialState materialState, long playerSkillLevel)
        {
            GdDict recipe = GetRecipe(recipeId);
            if (recipe.IsEmpty)
                return false;
            string stationKind = V.Str(recipe.Get("station_kind", ""));
            if (stationKind.Length == 0)
                return false;
            StationState station = GetOrCreateStation(stationKind);
            long stTier = station.EffectiveTier();
            if (!CanCraft(recipeId, inventory, null, stTier))
                return false;
            // Enforce the recipe's skill gate (Codex PR #45). Station crafting rejects under-skilled
            // players; emergency field crafting (FieldCraftingState) stays ungated by design.
            if (playerSkillLevel < GetRequiredSkillLevel(recipeId))
                return false;
            double craftTime = V.F64(recipe.Get("craft_time_seconds", 0.0));
            if (craftTime <= 0.0)
                return false;
            ConsumeIngredients(recipeId, inventory);
            double avgQuality = 0.5;
            object ingredients = recipe.Get("ingredients", new GdDict());
            if (ingredients is GdDict ingredientsDict)
                avgQuality = materialState.AverageIngredientQuality(ingredientsDict);
            var resolver = new QualityTierResolver();
            GdDict qualityResult = resolver.Resolve(avgQuality, playerSkillLevel, station.Level, station.Powered);
            _activeCraft = new GdDict
            {
                { "recipe_id", recipeId },
                { "station_kind", stationKind },
                { "quality_tier", qualityResult["tier"] },
                { "quality_multiplier", qualityResult["multiplier"] },
                { "quality_score", qualityResult["score"] },
            };
            station.StartRecipe(recipeId, craftTime);
            return true;
        }

        /// <summary>
        /// PKG-B2.4b: queue a recipe (or batch) on its station without starting craft.
        /// Returns accepted queue count (0 if full / invalid).
        /// </summary>
        public long EnqueueCraft(string recipeId, long count = 1)
        {
            GdDict recipe = GetRecipe(recipeId);
            if (recipe.IsEmpty || count <= 0)
                return 0;
            string stationKind = V.Str(recipe.Get("station_kind", ""));
            if (stationKind.Length == 0)
                return 0;
            StationState station = GetOrCreateStation(stationKind);
            // StationState always has enqueue_batch; the GDScript per-item enqueue fallback is unreachable.
            return station.EnqueueBatch(recipeId, count);
        }

        /// <summary>Refresh station tier from a component placement array + optional catalog.</summary>
        public long RefreshStationTier(string stationKind, GdArray placed, ComponentCatalog catalog = null)
        {
            StationState station = GetOrCreateStation(stationKind);
            long derived = DeriveTierFromComponents(stationKind, placed, catalog);
            station.ApplyComponentTier(derived);
            return station.EffectiveTier();
        }

        /// <summary>Ticks the active station. Returns true when the craft completes.</summary>
        public bool Tick(double deltaSeconds)
        {
            if (_activeCraft.IsEmpty)
                return false;
            string stationKind = V.Str(_activeCraft.Get("station_kind", ""));
            StationState station = GetStation(stationKind);
            if (station == null)
                return false;
            bool completed = station.Tick(deltaSeconds);
            if (completed)
                return true;
            return false;
        }

        /// <summary>
        /// Call after tick returns true to collect the finished product.
        /// Returns {item_id, quantity, quality_tier, quality_multiplier, quality_score, station_kind, recipe_id} or empty dict.
        /// </summary>
        public GdDict FinishCraft()
        {
            if (_activeCraft.IsEmpty)
                return new GdDict();
            string stationKind = V.Str(_activeCraft.Get("station_kind", ""));
            StationState station = GetStation(stationKind);
            if (station == null)
                return new GdDict();
            if (station.CurrentStatus != 3)
                return new GdDict();
            string recipeId = V.Str(_activeCraft.Get("recipe_id", ""));
            GdDict produces = GetProduces(recipeId);
            var result = new GdDict
            {
                { "item_id", V.Str(produces.Get("item_id", "")) },
                { "quantity", V.I64(produces.Get("quantity", 0L)) },
                { "quality_tier", V.Str(_activeCraft.Get("quality_tier", "standard")) },
                { "quality_multiplier", V.F64(_activeCraft.Get("quality_multiplier", 1.0)) },
                { "quality_score", V.F64(_activeCraft.Get("quality_score", 0.5)) },
                // Stream D: station_kind/recipe_id survive finish so the coordinator can
                // route training emissions (cook_meal vs fabricate_part) without racing _active_craft.clear().
                { "station_kind", stationKind },
                { "recipe_id", recipeId },
            };
            string nextRecipe = station.FinishAndAdvance();
            if (nextRecipe.Length == 0)
            {
                _activeCraft.Clear();
            }
            else
            {
                // Auto-start next queued recipe if possible (simplified: just start it)
                double nextTime = GetCraftTime(nextRecipe);
                station.StartRecipe(nextRecipe, nextTime);
                _activeCraft["recipe_id"] = nextRecipe;
                _activeCraft["station_kind"] = stationKind;
            }
            return result;
        }

        public bool IsCrafting() => !_activeCraft.IsEmpty;

        public string GetActiveRecipeId() => V.Str(_activeCraft.Get("recipe_id", ""));

        public string GetActiveStationKind() => V.Str(_activeCraft.Get("station_kind", ""));

        public void CancelCraft()
        {
            _activeCraft.Clear();
            foreach (string stationKind in _stationOrder)
            {
                StationState station = _stationStates[stationKind];
                if (station.IsCrafting())
                {
                    station.CurrentStatus = 0;
                    station.ActiveRecipeId = "";
                    station.ProgressSeconds = 0.0;
                    station.RequiredSeconds = 0.0;
                }
            }
        }

        // --- save/load ---

        public GdDict GetSummary()
        {
            var stationSummaries = new GdDict();
            foreach (string sk in _stationOrder)
                stationSummaries[sk] = _stationStates[sk].GetSummary();
            return new GdDict
            {
                { "recipe_count", RecipeCount() },
                { "active_craft", _activeCraft.ShallowCopy() },
                { "station_summaries", stationSummaries },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            bool accepted = false;
            bool changed = false;
            object ac = summary.Get("active_craft", new GdDict());
            if (ac is GdDict d)
            {
                accepted = true;
                if (!V.VariantEquals(d, _activeCraft))
                {
                    _activeCraft = d.ShallowCopy();
                    changed = true;
                }
            }
            object ss = summary.Get("station_summaries", new GdDict());
            if (ss is GdDict ssDict)
            {
                accepted = true;
                foreach (var kv in ssDict)
                {
                    if (kv.Value is GdDict stationSummary)
                    {
                        StationState station = GetOrCreateStation(V.Str(kv.Key));
                        if (station.ApplySummary(stationSummary))
                            changed = true;
                    }
                }
            }
            return changed || accepted;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Recipes: " + GdString.FormatInt(RecipeCount()));
            if (IsCrafting())
                lines.Add("Crafting: " + GetActiveRecipeId() + " @ " + GetActiveStationKind());
            foreach (string sk in _stationOrder)
                foreach (string line in _stationStates[sk].GetStatusLines())
                    lines.Add(line);
            return lines;
        }
    }
}
