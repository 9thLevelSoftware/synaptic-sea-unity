// Ported from scripts/systems/deconstruction_resolver.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for breaking items down into base materials. Reads deconstruction recipes
    /// (category == "deconstruction") from the recipe catalog and resolves them against inventory. Also runs the
    /// JunkYieldResolver catalog for raw junk salvage (Stream E residual MVP). Never touches the scene tree.
    /// <para>
    /// Inventories are the duck-typed player inventory: <see cref="CargoTransfer.ICargoPlayer"/> (items,
    /// get_quantity, add_item, remove_item) plus the optional <see cref="IItemAcceptor"/> (<c>can_accept</c>).
    /// </para>
    /// </summary>
    public class DeconstructionResolver : IStatusLineProvider
    {
        CraftingState _craftingState = new CraftingState();
        GdDict _junkDefs = new GdDict();

        public DeconstructionResolver()
        {
            _junkDefs = JunkYieldResolver.LoadDefinitions();
        }

        /// <summary>Returns all deconstruction recipes.</summary>
        public GdArray GetDeconstructionRecipes() => _craftingState.GetRecipesByCategory("deconstruction");

        /// <summary>Returns true if the inventory has the target item to deconstruct.</summary>
        public bool CanDeconstruct(string recipeId, CargoTransfer.ICargoStore inventory)
        {
            GdDict recipe = _craftingState.GetRecipe(recipeId);
            if (recipe.IsEmpty)
                return false;
            if (V.Str(recipe.Get("category", "")) != "deconstruction")
                return false;
            return _craftingState.CanCraft(recipeId, inventory);
        }

        /// <summary>
        /// Deconstructs an item, consuming it and producing base materials. Returns the produces dict
        /// {item_id, quantity} or empty dict on failure. PKG-B2.4a: output quality inherits source material quality
        /// × skill × tool (no 0.5 hardcode). context optional keys: skill_level (int), tool_factor (float, default 1.0).
        /// </summary>
        public GdDict Deconstruct(string recipeId, CargoTransfer.ICargoStore inventory, MaterialState materialState, GdDict context = null)
        {
            context = context ?? new GdDict();
            GdDict recipe = _craftingState.GetRecipe(recipeId);
            if (recipe.IsEmpty)
                return new GdDict();
            if (V.Str(recipe.Get("category", "")) != "deconstruction")
                return new GdDict();
            if (!CanDeconstruct(recipeId, inventory))
                return new GdDict();
            double sourceQuality = SourceIngredientQuality(recipe, materialState);
            long skillLevel = V.I64(context.Get("skill_level", 0L));
            double toolFactor = Math.Max(0.25, V.F64(context.Get("tool_factor", 1.0)));
            double outQuality = ResolveYieldQuality(sourceQuality, skillLevel, toolFactor);
            if (_craftingState.ConsumeIngredients(recipeId, inventory))
            {
                GdDict produces = _craftingState.GetProduces(recipeId);
                string outId = V.Str(produces.Get("item_id", ""));
                long outQty = V.I64(produces.Get("quantity", 0L));
                if (outId.Length != 0 && outQty > 0)
                {
                    if (materialState != null && materialState.HasDefinition(outId))
                        materialState.SetQuality(outId, outQuality);
                    GdDict result = produces.ShallowCopy();
                    result["quality"] = outQuality;
                    result["source_quality"] = sourceQuality;
                    return result;
                }
            }
            return new GdDict();
        }

        double SourceIngredientQuality(GdDict recipe, MaterialState materialState)
        {
            if (materialState == null)
                return 0.5;
            object ingredients = recipe.Get("ingredients", new GdDict());
            if (!(ingredients is GdDict ingDict) || ingDict.IsEmpty)
                return 0.5;
            double total = 0.0;
            double weight = 0.0;
            foreach (object matId in ingDict.Keys)
            {
                double qty = V.F64(ingDict[matId]);
                double q = materialState.GetQuality(V.Str(matId));
                total += q * qty;
                weight += qty;
            }
            if (weight <= 0.0)
                return 0.5;
            return GdMath.Clampf(total / weight, 0.0, 1.0);
        }

        /// <summary>Quality inheritance curve: source × (0.8 + 0.04*skill) × tool_factor, clamped.</summary>
        static double ResolveYieldQuality(double sourceQuality, long skillLevel, double toolFactor)
        {
            double skillCurve = 0.80 + 0.04 * (double)GdMath.Clampi(skillLevel, 0, 10);
            return GdMath.Clampf(sourceQuality * skillCurve * toolFactor, 0.0, 1.0);
        }

        /// <summary>
        /// Auto-deconstruct: finds the first deconstruction recipe for a given item_id and executes it. Returns the
        /// produces dict or empty.
        /// </summary>
        public GdDict AutoDeconstruct(string itemId, CargoTransfer.ICargoStore inventory, MaterialState materialState)
        {
            foreach (object recipeV in GetDeconstructionRecipes())
            {
                var recipe = (GdDict)recipeV;
                object ingredients = recipe.Get("ingredients", new GdDict());
                if (ingredients is GdDict ingDict && ingDict.Has(itemId))
                    return Deconstruct(V.Str(recipe.Get("recipe_id", "")), inventory, materialState);
            }
            return new GdDict();
        }

        static bool LessByRecipeId(object a, object b) =>
            GdString.Less(V.Str(((GdDict)a).Get("recipe_id", "")), V.Str(((GdDict)b).Get("recipe_id", "")));

        // GDScript `inventory.has_method("can_accept") and not inventory.can_accept(id, qty)`.
        static bool RefusesStack(CargoTransfer.ICargoPlayer inventory, string itemId, long qty) =>
            inventory is IItemAcceptor acceptor && !acceptor.CanAccept(itemId, qty);

        /// <summary>
        /// REQ-CS-017: headless salvage target listing for the picker. Returns Array[Dictionary] sorted by recipe_id.
        /// Shape matches craft list rows so RecipePickerPanel can reuse them (recipe_id is the selection key).
        /// deconstruct: recipe_id = catalog id; junk: recipe_id = "junk:&lt;source_item_id&gt;".
        /// </summary>
        public GdArray ListSalvageEntries(CargoTransfer.ICargoPlayer inventory)
        {
            var output = new GdArray();
            if (inventory == null)
                return output;
            if (_junkDefs.IsEmpty)
                _junkDefs = JunkYieldResolver.LoadDefinitions();
            // 1) Deconstruction recipes (catalog order by recipe_id).
            GdArray recipes = GetDeconstructionRecipes();
            recipes.SortCustom(LessByRecipeId);
            foreach (object recipeV in recipes)
            {
                if (!(recipeV is GdDict recipe))
                    continue;
                string rid = V.Str(recipe.Get("recipe_id", ""));
                if (rid.Length == 0)
                    continue;
                var produces = new GdDict();
                object producesRaw = recipe.Get("produces", new GdDict());
                if (producesRaw is GdDict pr)
                    produces = pr.ShallowCopy();
                var ingredients = new GdDict();
                object ingredientsRaw = recipe.Get("ingredients", new GdDict());
                if (ingredientsRaw is GdDict ir)
                    ingredients = ir.ShallowCopy();
                string status = "ready";
                if (!CanDeconstruct(rid, inventory))
                {
                    status = "missing_ingredients";
                }
                else
                {
                    string outId = V.Str(produces.Get("item_id", ""));
                    long outQty = V.I64(produces.Get("quantity", 0L));
                    if (outId.Length != 0 && outQty > 0 && RefusesStack(inventory, outId, outQty))
                        status = "output_full";
                }
                output.Add(new GdDict
                {
                    { "recipe_id", rid },
                    { "display_name", V.Str(recipe.Get("display_name", rid)) },
                    { "category", "deconstruction" },
                    { "required_skill_level", 0L },
                    { "ingredients", ingredients },
                    { "produces", produces },
                    { "craft_time_seconds", 0.0 },
                    { "status", status },
                    { "craftable", status == "ready" },
                    { "salvage_kind", "deconstruct" },
                });
            }
            // 2) Junk catalog items currently in inventory (sorted by item id).
            GdArray ids = inventory.Items != null ? new GdArray(inventory.Items.Keys) : new GdArray();
            GdSort.Sort(ids);
            foreach (object itemIdVariant in ids)
            {
                string itemId = V.Str(itemIdVariant);
                if (inventory.GetQuantity(itemId) <= 0)
                    continue;
                GdArray yields = JunkYieldResolver.YieldsForItem(itemId, _junkDefs);
                if (yields.IsEmpty)
                    continue;
                var materials = new GdDict();
                string firstId = "";
                long firstQty = 0;
                bool canAll = true;
                foreach (object entryVariant in yields)
                {
                    if (!(entryVariant is GdDict entry))
                        continue;
                    string mid = V.Str(entry.Get("material_id", ""));
                    long qty = V.I64(entry.Get("quantity", 0L));
                    if (mid.Length == 0 || qty <= 0)
                        continue;
                    materials[mid] = V.I64(materials.Get(mid, 0L)) + qty;
                    if (firstId.Length == 0)
                    {
                        firstId = mid;
                        firstQty = qty;
                    }
                    if (RefusesStack(inventory, mid, qty))
                        canAll = false;
                }
                if (firstId.Length == 0)
                    continue;
                string jstatus = canAll ? "ready" : "output_full";
                output.Add(new GdDict
                {
                    { "recipe_id", "junk:" + itemId },
                    { "display_name", "Salvage " + itemId },
                    { "category", "junk" },
                    { "required_skill_level", 0L },
                    { "ingredients", new GdDict { { itemId, 1L } } },
                    { "produces", new GdDict { { "item_id", firstId }, { "quantity", firstQty } } },
                    { "craft_time_seconds", 0.0 },
                    { "status", jstatus },
                    { "craftable", jstatus == "ready" },
                    { "salvage_kind", "junk" },
                    { "source_item_id", itemId },
                    { "materials", materials },
                });
            }
            // Keep a single sorted list by selection key.
            output.SortCustom(LessByRecipeId);
            return output;
        }

        public string FirstReadySalvageId(CargoTransfer.ICargoPlayer inventory)
        {
            foreach (object entry in ListSalvageEntries(inventory))
            {
                if (entry is GdDict e && V.Bool(e.Get("craftable", false)))
                    return V.Str(e.Get("recipe_id", ""));
            }
            return "";
        }

        /// <summary>Execute a listed salvage target id (recipe_id from ListSalvageEntries).</summary>
        public GdDict ExecuteSalvageTarget(string targetId, CargoTransfer.ICargoPlayer inventory, MaterialState materialState)
        {
            if (string.IsNullOrEmpty(targetId) || inventory == null)
                return new GdDict();
            if (GdString.BeginsWith(targetId, "junk:"))
            {
                string junkId = targetId.Substring(5);
                return SalvageJunkItem(junkId, inventory, materialState);
            }
            GdDict produced = Deconstruct(targetId, inventory, materialState);
            return produced;
        }

        /// <summary>
        /// Stream E: salvage the first inventory junk item that has a JunkYieldResolver catalog entry. Deterministic
        /// (sorted item ids). Returns a produces-shaped dict for the primary material plus multi-yield metadata, or
        /// empty on no match. Shape on success: {item_id, quantity, source_junk, materials: {mid: qty}, multi_yield}.
        /// </summary>
        public GdDict SalvageJunk(CargoTransfer.ICargoPlayer inventory, MaterialState materialState)
        {
            if (inventory == null)
                return new GdDict();
            if (_junkDefs.IsEmpty)
                _junkDefs = JunkYieldResolver.LoadDefinitions();
            GdArray ids = inventory.Items != null ? new GdArray(inventory.Items.Keys) : new GdArray();
            GdSort.Sort(ids);
            foreach (object itemIdVariant in ids)
            {
                string itemId = V.Str(itemIdVariant);
                if (inventory.GetQuantity(itemId) <= 0)
                    continue;
                GdDict result = SalvageJunkItem(itemId, inventory, materialState);
                if (!result.IsEmpty)
                    return result;
            }
            return new GdDict();
        }

        /// <summary>Stream E + REQ-CS-017: salvage one specific junk item_id if catalogued.</summary>
        public GdDict SalvageJunkItem(string itemId, CargoTransfer.ICargoPlayer inventory, MaterialState materialState)
        {
            if (string.IsNullOrEmpty(itemId) || inventory == null)
                return new GdDict();
            if (inventory.GetQuantity(itemId) <= 0)
                return new GdDict();
            if (_junkDefs.IsEmpty)
                _junkDefs = JunkYieldResolver.LoadDefinitions();
            GdArray yields = JunkYieldResolver.YieldsForItem(itemId, _junkDefs);
            if (yields.IsEmpty)
                return new GdDict();
            // Pre-check stack room for every yield so we never consume junk without depositing its materials
            // (mirrors craft can_accept guards).
            bool canAll = true;
            foreach (object entryVariant in yields)
            {
                if (!(entryVariant is GdDict entry))
                    continue;
                string mid = V.Str(entry.Get("material_id", ""));
                long qty = V.I64(entry.Get("quantity", 0L));
                if (mid.Length == 0 || qty <= 0)
                    continue;
                // GDScript called inventory.can_accept() unconditionally (the player InventoryState always has it).
                if (RefusesStack(inventory, mid, qty))
                {
                    canAll = false;
                    break;
                }
            }
            if (!canAll)
                return new GdDict();
            if (inventory.RemoveItem(itemId, 1) != 1)
                return new GdDict();
            var materials = new GdDict();
            string firstId = "";
            long firstQty = 0;
            foreach (object entryVariant2 in yields)
            {
                if (!(entryVariant2 is GdDict y))
                    continue;
                string mid2 = V.Str(y.Get("material_id", ""));
                long qty2 = V.I64(y.Get("quantity", 0L));
                if (mid2.Length == 0 || qty2 <= 0)
                    continue;
                inventory.AddItem(mid2, qty2);
                if (materialState != null && materialState.HasDefinition(mid2))
                {
                    // PKG-B2.4a: junk salvage inherits a soft base from tool quality context later; default
                    // inheritance curve with standard source (0.5).
                    materialState.SetQuality(mid2, ResolveYieldQuality(0.5, 0, 1.0));
                }
                materials[mid2] = V.I64(materials.Get(mid2, 0L)) + qty2;
                if (firstId.Length == 0)
                {
                    firstId = mid2;
                    firstQty = qty2;
                }
            }
            if (firstId.Length == 0)
            {
                // No depositable yields — restore junk (should not happen after pre-check).
                inventory.AddItem(itemId, 1);
                return new GdDict();
            }
            return new GdDict
            {
                { "item_id", firstId },
                { "quantity", firstQty },
                { "source_junk", itemId },
                { "materials", materials },
                { "multi_yield", materials.Count > 1 },
            };
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "deconstruction_recipes", (long)GetDeconstructionRecipes().Count },
                { "junk_catalog_items", (long)_junkDefs.Count },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            return false;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Deconstruction recipes: " + GdString.FormatInt(GetDeconstructionRecipes().Count));
            return lines;
        }
    }
}
