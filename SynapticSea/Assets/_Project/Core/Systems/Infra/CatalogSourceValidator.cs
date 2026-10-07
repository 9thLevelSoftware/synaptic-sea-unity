using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Read-only catalog diagnostics. Validation never changes launch eligibility.</summary>
    public sealed class CatalogSourceValidator
    {
        public const string SYSTEMS_PATH = "res://data/ship_systems/systems.json";
        public const string SKILL_TREE_PATH = "res://data/player/skill_tree.json";
        public const string LOOT_PATH = "res://data/items/loot_tables.json";

        /// <summary>
        /// Returns exact reference diagnostics and an AND-input potential-source closure. Finite inputs are not
        /// replenished or spent by this analysis; it deliberately cannot certify quantities, traversal or class routes.
        /// No definitions, aliases, sources, exposure choices or launch policies are changed.
        /// </summary>
        public GdDict Validate(GdDict catalog, GdDict sourceGraph, GdDict exposureManifest)
        {
            var errors = new GdArray();
            var warnings = new GdArray();
            var coverage = new GdArray();
            var dispositions = new GdArray();
            if (catalog == null || sourceGraph == null || exposureManifest == null)
            {
                Add(errors, "invalid_document", "", "", "Validate", "Catalog, source graph and exposure manifest must be dictionaries.");
                return Report(errors, warnings, coverage, dispositions, new HashSet<string>(), new GdDict());
            }
            foreach (string field in new[] { "items", "components", "skills", "books" })
                if (!(catalog.Get(field) is GdDict)) Add(errors, "invalid_document", "catalog", field, "catalog." + field, "Expected a normalized dictionary.");
            foreach (string field in new[] { "recipes", "consumers" })
                if (!(catalog.Get(field) is GdArray)) Add(errors, "invalid_document", "catalog", field, "catalog." + field, "Expected a normalized array.");
            if (!(sourceGraph.Get("edges") is GdArray)) Add(errors, "invalid_document", "catalog", "edges", "sourceGraph.edges", "Expected a source-edge array.");
            if (!errors.IsEmpty) return Report(errors, warnings, coverage, dispositions, new HashSet<string>(), new GdDict());
            GdDict items = catalog.GetDictOrEmpty("items");
            GdDict skills = catalog.GetDictOrEmpty("skills");
            GdDict stations = sourceGraph.GetDictOrEmpty("stations");
            foreach (object value in catalog.GetArrayOrEmpty("normalization_errors")) errors.Add(V.DeepCopy(value));
            ValidateItems(items, errors);
            var references = catalog.GetArrayOrEmpty("consumers").DeepCopy();
            var recipeGates = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (object value in catalog.GetArrayOrEmpty("recipes"))
            {
                if (!(value is GdDict recipe)) { Add(errors, "invalid_recipe", "", "", "recipes", "Recipe row must be a dictionary."); continue; }
                string rid = recipe.GetString("recipe_id");
                string path = recipe.GetString("source_path", CraftingState.RECIPE_DEFINITIONS_PATH + ":" + rid);
                bool exposed = IsProduction(rid, exposureManifest, errors, dispositions);
                string station = EffectiveStation(recipe);
                bool gateOk = true;
                if (!RegisteredStation(stations, station))
                {
                    if (exposed) Add(errors, "missing_station", rid, station, path + ".station_kind", "No live station or portable-craft registration.");
                    gateOk = false;
                }
                long tier = recipe.GetInt("station_tier_min");
                if (tier < 0) { Add(errors, "invalid_tier", rid, station, path + ".station_tier_min", "Tier must be nonnegative."); gateOk = false; }
                if (tier > 0 && !HasTier(station, tier, stations, sourceGraph.GetArrayOrEmpty("tier_sources")))
                {
                    if (exposed) Add(errors, "missing_tier_source", rid, station, path + ".station_tier_min", "Required tier=" + tier + "; no registered tier source meets it.");
                    gateOk = false;
                }
                GdDict registeredStation = stations.GetDictOrEmpty(station);
                if (tier > 0 && registeredStation.Has("precheck_tier") && registeredStation.GetInt("precheck_tier") < tier)
                {
                    if (exposed) Add(errors, "tier_not_forwarded", rid, station, "Core/Session/Interactables/CraftingStation.cs:TryCraftRecipe -> CraftingState.CanCraft", "Live spatial list/start precheck passes tier=" + registeredStation.GetInt("precheck_tier") + "; recipe requires=" + tier + ".");
                    gateOk = false;
                }
                string skill = recipe.GetString("required_skill", "fabrication");
                if (recipe.GetInt("required_skill_level") > 0 && !skills.Has(skill))
                {
                    if (exposed) Add(errors, "missing_skill_definition", rid, skill, path + ".required_skill_level", "Skill gate has no skill definition.");
                    gateOk = false;
                }
                string knowledge = recipe.GetString("knowledge_source", "starter");
                if (knowledge == "book")
                {
                    string book = recipe.GetString("knowledge_book_id");
                    if (book.Length == 0)
                    {
                        if (exposed) Add(errors, "missing_book_mapping", rid, "", path + ".knowledge_book_id", "Book recipe has no knowledge_book_id.");
                        gateOk = false;
                    }
                    else
                    {
                        Reference(references, rid, book, path + ".knowledge_book_id", "book", true);
                        if (!HasLearning(sourceGraph, "book", book))
                        {
                            if (exposed) Add(errors, "missing_learning_registration", rid, book, path + ".knowledge_book_id", "No live LearnFromBook registration.");
                            gateOk = false;
                        }
                    }
                }
                else if (knowledge != "starter" && knowledge.Length != 0)
                {
                    string key = knowledge == "codex" ? "knowledge_codex_id" : "reverse_engineer_component";
                    string target = recipe.GetString(key);
                    if (target.Length == 0 || !HasLearning(sourceGraph, knowledge, target))
                    {
                        if (exposed) Add(errors, "missing_learning_registration", rid, target, path + "." + key, "Knowledge gate is not bound to a live learning caller.");
                        gateOk = false;
                    }
                }
                recipeGates[rid] = gateOk && exposed;
                if (!(recipe.Get("ingredients") is GdDict ingredients))
                {
                    Add(errors, "invalid_inputs", rid, "", path + ".ingredients", "Ingredients must be an AND quantity dictionary.");
                    recipeGates[rid] = false;
                }
                else
                {
                    foreach (var ingredient in ingredients)
                    {
                        if (!PositiveInteger(ingredient.Value))
                        {
                            Add(errors, "invalid_quantity", rid, V.Str(ingredient.Key), path + ".ingredients." + V.Str(ingredient.Key), "Quantity must be a finite positive integer.");
                            recipeGates[rid] = false;
                        }
                        Reference(references, rid, V.Str(ingredient.Key), path + ".ingredients." + V.Str(ingredient.Key), "item", true);
                    }
                }
                GdDict output = recipe.GetDictOrEmpty("produces");
                if (!PositiveInteger(output.Get("quantity")))
                {
                    Add(errors, "invalid_quantity", rid, output.GetString("item_id"), path + ".produces.quantity", "Quantity must be a finite positive integer.");
                    recipeGates[rid] = false;
                }
                Reference(references, rid, output.GetString("item_id"), path + ".produces.item_id", "output", false);
            }
            var validEdges = new List<GdDict>();
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in sourceGraph.GetArrayOrEmpty("edges"))
            {
                if (!(value is GdDict edge)) { Add(errors, "invalid_edge", "", "", "edges", "Source edge must be a dictionary."); continue; }
                string eid = edge.GetString("edge_id");
                string cid = edge.GetString("consumer_id", edge.GetString("producer_id"));
                string path = edge.GetString("source_path", edge.GetString("registration_path"));
                bool valid = true;
                if (eid.Length == 0 || !edgeIds.Add(eid)) { Add(errors, "invalid_edge_id", cid, eid, path, "Edge IDs must be present and unique."); valid = false; }
                foreach (string field in new[] { "inputs", "outputs" })
                {
                    if (!(edge.Get(field) is GdDict quantities))
                    {
                        Add(errors, "invalid_inputs", cid, "", path + "." + field, "Expected an ID to positive integer quantity dictionary."); valid = false; continue;
                    }
                    foreach (var pair in quantities)
                    {
                        string id = V.Str(pair.Key);
                        if (!PositiveInteger(pair.Value)) { Add(errors, "invalid_quantity", cid, id, path + "." + field + "." + id, "Quantity must be a finite positive integer."); valid = false; }
                        if (!items.Has(id)) { Add(errors, "missing_definition", cid, id, path + "." + field + "." + id, "Producer references an undefined inventory item."); valid = false; }
                    }
                }
                if (edge.Has("tool_ids") && !(edge.Get("tool_ids") is GdArray))
                {
                    Add(errors, "invalid_tool_ids", cid, "", path + ".tool_ids", "A present tool_ids field must be an array of item IDs.");
                    valid = false;
                }
                foreach (object tool in edge.GetArrayOrEmpty("tool_ids"))
                    if (!items.Has(V.Str(tool))) { Add(errors, "missing_definition", cid, V.Str(tool), path + ".tool_ids", "Producer tool is undefined."); valid = false; }
                if (edge.GetString("producer_id").Length == 0 || edge.GetString("registration_path").Length == 0)
                {
                    foreach (object output in edge.GetDictOrEmpty("outputs").Keys) Add(errors, "unregistered_producer", cid, V.Str(output), path, "Producer has no live registration path or identity.");
                    valid = false;
                }
                if (edge.GetBool("diagnostic")) { Add(warnings, "diagnostic_producer_excluded", cid, "", path, "Diagnostic/grant-only edges cannot satisfy production."); valid = false; }
                string producerId = edge.GetString("producer_id");
                bool producerExposed = IsProduction(producerId, exposureManifest, errors, dispositions);
                bool callerExposed = cid == producerId || IsProduction(cid, exposureManifest, errors, dispositions);
                if (!producerExposed || !callerExposed)
                {
                    Add(warnings, "nonproduction_producer_excluded", cid, producerId, path, "Diagnostic or deferred producer/caller dispositions cannot satisfy production.");
                    valid = false;
                }
                string recipeId = edge.GetString("recipe_id");
                if (recipeId.Length != 0 && (!recipeGates.TryGetValue(recipeId, out bool gates) || !gates)) valid = false;
                if (valid) validEdges.Add(edge);
            }
            var potential = new HashSet<string>(StringComparer.Ordinal);
            var reachedEdges = new HashSet<string>(StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                foreach (GdDict edge in validEdges)
                {
                    bool ready = true;
                    foreach (object input in edge.GetDictOrEmpty("inputs").Keys) if (!potential.Contains(V.Str(input))) ready = false;
                    foreach (object tool in edge.GetArrayOrEmpty("tool_ids")) if (!potential.Contains(V.Str(tool))) ready = false;
                    string book = edge.GetString("book_id");
                    if (book.Length != 0 && !potential.Contains(book)) ready = false;
                    if (!ready) continue;
                    reachedEdges.Add(edge.GetString("edge_id"));
                    foreach (object output in edge.GetDictOrEmpty("outputs").Keys) if (potential.Add(V.Str(output))) changed = true;
                }
            } while (changed);
            foreach (object value in references)
            {
                if (!(value is GdDict row)) { Add(errors, "invalid_reference", "", "", "consumers", "Consumer row must be a dictionary."); continue; }
                string cid = row.GetString("consumer_id");
                string id = row.GetString("item_id");
                string path = row.GetString("source_path");
                string kind = row.GetString("reference_kind", "item");
                bool defined = kind == "skill" ? skills.Has(id) : kind == "component" ? catalog.GetDictOrEmpty("components").Has(id) : items.Has(id);
                bool production = IsProduction(cid, exposureManifest, errors, dispositions);
                bool needsSource = row.GetBool("requires_source", kind != "output" && kind != "skill" && kind != "component");
                if (!defined) Add(errors, kind == "skill" ? "missing_skill_definition" : "missing_definition", cid, id, path, "Referenced " + kind + " is absent from the normalized definitions.");
                else if (production && needsSource && !potential.Contains(id)) Add(errors, "missing_source", cid, id, path, "No registered seeded AND-input/tool/recipe-gate chain supplies this ID.");
                coverage.Add(new GdDict
                {
                    { "consumer_id", cid }, { "item_id", id }, { "reference_kind", kind }, { "source_path", path },
                    { "defined", defined }, { "potential_source", potential.Contains(id) }, { "exposure", production ? "production" : "deferred_or_diagnostic" },
                });
            }
            foreach (var item in items)
                if (!potential.Contains(V.Str(item.Key))) Add(warnings, "no_enumerated_source", "inventory", V.Str(item.Key), "items." + V.Str(item.Key), "Registered item has no source in these live registrations.");
            foreach (GdDict edge in validEdges)
                if (!reachedEdges.Contains(edge.GetString("edge_id")))
                {
                    var missing = new GdArray();
                    foreach (object input in edge.GetDictOrEmpty("inputs").Keys) if (!potential.Contains(V.Str(input))) missing.Add(input);
                    foreach (object tool in edge.GetArrayOrEmpty("tool_ids")) if (!potential.Contains(V.Str(tool))) missing.Add(tool);
                    Add(warnings, "unseeded_dependency", edge.GetString("consumer_id"), edge.GetString("edge_id"), edge.GetString("source_path"), "Missing AND inputs/tools: " + GdJson.Stringify(missing));
                }
            Add(warnings, "potential_sources_only", "catalog", "", "CatalogSourceValidator.Validate", "Closure ignores finite depletion, quantities, geometry, power availability, progression ordering and class feasibility; natural_traversal_witness=false.");
            var denominators = new GdDict
            {
                { "items", items.Count }, { "recipes", catalog.GetArrayOrEmpty("recipes").Count }, { "components", catalog.GetDictOrEmpty("components").Count },
                { "books", catalog.GetDictOrEmpty("books").Count }, { "skills", skills.Count }, { "source_edges", sourceGraph.GetArrayOrEmpty("edges").Count },
                { "consumer_references", coverage.Count }, { "potential_items", potential.Count },
            };
            return Report(errors, warnings, coverage, dispositions, potential, denominators);
        }

        /// <summary>Uses the production merge unchanged; provenance stays in sidecar records.</summary>
        public static GdDict LoadProductionCatalog()
        {
            var normalizationErrors = new GdArray();
            GdDict componentRoot = ReadProduction(ComponentCatalog.DEFAULT_PATH, normalizationErrors);
            GdDict workRoot = ReadProduction(WorkActionCatalog.DEFAULT_PATH, normalizationErrors);
            GdDict recipeRoot = ReadProduction(CraftingState.RECIPE_DEFINITIONS_PATH, normalizationErrors);
            GdDict systemsRoot = ReadProduction(SYSTEMS_PATH, normalizationErrors);
            GdDict lootRoot = ReadProduction(LOOT_PATH, normalizationErrors);
            GdDict treeRoot = ReadProduction(SKILL_TREE_PATH, normalizationErrors);
            GdDict cropsRoot = ReadProduction(RunSession.HYDROPONICS_CROPS_CONFIG_PATH, normalizationErrors);
            GdDict skillsRoot = ReadProduction(PlayerProgressionState.DEFAULT_SKILLS_PATH, normalizationErrors);
            GdDict booksRoot = ReadProduction(PlayerProgressionState.DEFAULT_BOOKS_PATH, normalizationErrors);
            // Validate source record shapes independently of the compatibility loaders, which retain their defaults.
            ArraySection(skillsRoot, "skills", PlayerProgressionState.DEFAULT_SKILLS_PATH, normalizationErrors);
            ArraySection(booksRoot, "books", PlayerProgressionState.DEFAULT_BOOKS_PATH, normalizationErrors);
            GdArray recipes = ArraySection(recipeRoot, "recipes", CraftingState.RECIPE_DEFINITIONS_PATH, normalizationErrors);
            GdArray systems = ArraySection(systemsRoot, "systems", SYSTEMS_PATH, normalizationErrors);
            GdArray prerequisiteRows = ArraySection(treeRoot, "skill_prerequisites", SKILL_TREE_PATH, normalizationErrors);
            ValidateDictionaryRows(lootRoot, "", LOOT_PATH, normalizationErrors);
            if (lootRoot != null) foreach (var table in lootRoot)
                if (table.Value is GdDict definition) ArraySection(definition, "entries", LOOT_PATH + ":" + V.Str(table.Key), normalizationErrors);
            var catalog = new GdDict
            {
                { "items", ItemDefs.LoadDefinitions() }, { "recipes", new GdArray() }, { "consumers", new GdArray() },
                { "components", DictionarySection(componentRoot, "components", ComponentCatalog.DEFAULT_PATH, normalizationErrors) },
                { "component_roles", DictionarySection(componentRoot, "role_sets", ComponentCatalog.DEFAULT_PATH, normalizationErrors) },
                { "skills", PlayerProgressionState.LoadSkillsCatalog() }, { "books", PlayerProgressionState.LoadBooksCatalog() },
                { "loot_tables", lootRoot ?? new GdDict() }, { "work_actions", DictionarySection(workRoot, "actions", WorkActionCatalog.DEFAULT_PATH, normalizationErrors) },
                { "crops", ArraySection(cropsRoot, "crops", RunSession.HYDROPONICS_CROPS_CONFIG_PATH, normalizationErrors) },
                { "normalization_errors", normalizationErrors }, { "origins", new GdDict() },
            };
            var origins = catalog.GetDictOrEmpty("origins");
            foreach (string path in new[]
            {
                ItemDefs.TOOL_DEFINITIONS_PATH, ItemDefs.ITEM_DEFINITIONS_PATH, ItemDefs.MEDICINE_DEFINITIONS_PATH,
                ItemDefs.STIMULANT_DEFINITIONS_PATH, ItemDefs.AMMO_DEFINITIONS_PATH, ItemDefs.UTILITY_DEFINITIONS_PATH,
                ItemDefs.TRADE_DEFINITIONS_PATH, ItemDefs.MATERIAL_DEFINITIONS_PATH, ItemDefs.EQUIPMENT_DEFINITIONS_PATH,
                ItemDefs.JUNK_ITEMS_PATH, ItemDefs.UNIQUE_ITEMS_PATH,
            })
            {
                GdDict raw = ReadProduction(path, normalizationErrors);
                if (raw == null) continue;
                GdDict entries = path == ItemDefs.MATERIAL_DEFINITIONS_PATH ? DictionarySection(raw, "materials", path, normalizationErrors) : path == ItemDefs.JUNK_ITEMS_PATH || path == ItemDefs.UNIQUE_ITEMS_PATH ? DictionarySection(raw, "items", path, normalizationErrors) : raw;
                if (entries == raw) ValidateDictionaryRows(entries, "", path, normalizationErrors);
                foreach (var entry in entries)
                {
                    if (!(entry.Value is GdDict def)) continue;
                    string id = path == ItemDefs.UNIQUE_ITEMS_PATH ? def.GetString("item_id", V.Str(entry.Key)) : V.Str(entry.Key);
                    if (!origins.Has(id)) origins[id] = new GdArray();
                    origins.GetArrayOrEmpty(id).Add(path + ":" + V.Str(entry.Key));
                }
            }
            var refs = catalog.GetArrayOrEmpty("consumers");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < recipes.Count; i++)
            {
                if (!(recipes[i] is GdDict raw)) continue;
                GdDict recipe = raw.DeepCopy();
                string id = recipe.GetString("recipe_id");
                recipe["source_path"] = CraftingState.RECIPE_DEFINITIONS_PATH + ":recipes[" + i + "]";
                recipe["effective_station_kind"] = EffectiveStation(recipe);
                if (id.Length == 0 || !ids.Add(id)) Add(catalog.GetArrayOrEmpty("normalization_errors"), "invalid_recipe_id", id, "", recipe.GetString("source_path"), "Recipe IDs must be present and unique.");
                // The current spatial station checks fabrication; portable skill changes quality only.
                recipe["required_skill"] = "fabrication";
                recipe["live_skill_gate"] = recipe.GetString("station_kind") != "field_crafting";
                catalog.GetArrayOrEmpty("recipes").Add(recipe);
            }
            foreach (var pair in catalog.GetDictOrEmpty("components"))
            {
                if (!(pair.Value is GdDict def)) continue;
                string id = V.Str(pair.Key);
                string path = ComponentCatalog.DEFAULT_PATH + ":components." + id;
                Reference(refs, id, def.GetString("item_form", id), path + ".item_form", "component_form", true);
                double mass = def.GetFloat("mass");
                if (!Finite(mass) || mass <= 0) Add(catalog.GetArrayOrEmpty("normalization_errors"), "invalid_component_mass", id, def.GetString("item_form", id), path + ".mass", "Component mass must be finite and positive.");
                GdDict form = catalog.GetDictOrEmpty("items").GetDictOrEmpty(def.GetString("item_form", id));
                if (!form.IsEmpty && Math.Abs(form.GetFloat("weight") - mass) > 0.000001) Add(catalog.GetArrayOrEmpty("normalization_errors"), "conflicting_component_mass", id, def.GetString("item_form", id), path + ".mass", "Carried form weight disagrees with component mass.");
            }
            foreach (var role in catalog.GetDictOrEmpty("component_roles"))
                if (role.Value is GdDict slots) foreach (var slot in slots)
                    if (slot.Value is GdArray entries) foreach (object value in entries)
                        if (value is GdDict candidate) Reference(refs, V.Str(role.Key), candidate.GetString("component_id"), ComponentCatalog.DEFAULT_PATH + ":role_sets." + V.Str(role.Key) + "." + V.Str(slot.Key), "component", false);
            foreach (object sysValue in systems)
                if (sysValue is GdDict system) foreach (object subValue in ArraySection(system, "subcomponents", SYSTEMS_PATH + ":systems." + system.GetString("system_id"), normalizationErrors))
                    if (subValue is GdDict sub)
                    {
                        string id = system.GetString("system_id") + "/" + sub.GetString("subcomponent_id");
                        string path = SYSTEMS_PATH + ":systems." + id;
                        foreach (object part in sub.GetArrayOrEmpty("required_parts")) Reference(refs, id, V.Str(part), path + ".required_parts", "item", true);
                        foreach (object tool in sub.GetArrayOrEmpty("required_tools")) Reference(refs, id, V.Str(tool), path + ".required_tools", "tool", true);
                    }
            foreach (var action in catalog.GetDictOrEmpty("work_actions"))
                if (action.Value is GdDict def)
                {
                    string id = V.Str(action.Key), path = WorkActionCatalog.DEFAULT_PATH + ":actions." + id;
                    string tool = def.GetString("tool_class");
                    if (tool.Length != 0) Reference(refs, id, tool, path + ".tool_class", "tool", true);
                    foreach (object input in def.GetDictOrEmpty("materials_consumed").Keys) Reference(refs, id, V.Str(input), path + ".materials_consumed." + V.Str(input), "item", true);
                    foreach (object output in def.GetDictOrEmpty("materials_yielded").Keys) Reference(refs, id, V.Str(output), path + ".materials_yielded." + V.Str(output), "output", false);
                }
            foreach (var book in catalog.GetDictOrEmpty("books")) Reference(refs, V.Str(book.Key), V.Str(book.Key), PlayerProgressionState.DEFAULT_BOOKS_PATH + ":books." + V.Str(book.Key), "book", true);
            foreach (object value in prerequisiteRows)
                if (value is GdDict row)
                {
                    string id = row.GetString("skill_id"), path = SKILL_TREE_PATH + ":skill_prerequisites." + id;
                    Reference(refs, id, id, path + ".skill_id", "skill", false);
                    foreach (object requirement in row.GetArrayOrEmpty("requires")) if (requirement is GdDict req) Reference(refs, id, req.GetString("skill_id"), path + ".requires", "skill", false);
                    if (row.GetString("book_prerequisite").Length != 0) Reference(refs, id, row.GetString("book_prerequisite"), path + ".book_prerequisite", "book", true);
                }
            Reference(refs, "medbay_surgery", "medical_gauze", "Core/Session/RunSession.Crafting.cs:TryMedbaySurgery", "item", true);
            foreach (string item in RunSession.BANDAGE_ITEM_IDS) Reference(refs, "bandage_wound", item, "Core/Session/RunSession.cs:BANDAGE_ITEM_IDS", "alternative_item", false);
            foreach (string item in RunSession.TREAT_ITEM_IDS) Reference(refs, "treat_wound", item, "Core/Session/RunSession.cs:TREAT_ITEM_IDS", "alternative_item", false);
            return catalog;
        }

        /// <summary>Consumes actual session objects projected to dictionaries, or actual generated gameplay slices.</summary>
        public static GdDict NormalizeSources(GdDict catalog, GdDict registrations)
        {
            catalog = catalog ?? new GdDict(); registrations = registrations ?? new GdDict();
            var graph = new GdDict
            {
                { "edges", new GdArray() }, { "stations", registrations.GetDictOrEmpty("stations").DeepCopy() },
                { "tier_sources", registrations.GetArrayOrEmpty("tier_sources").DeepCopy() }, { "learning", registrations.GetArrayOrEmpty("learning").DeepCopy() },
            };
            GdArray edges = graph.GetArrayOrEmpty("edges");
            var containers = registrations.GetArrayOrEmpty("containers").DeepCopy();
            foreach (object sliceValue in registrations.GetArrayOrEmpty("slices"))
                if (sliceValue is GdDict slice) foreach (object value in slice.GetArrayOrEmpty("loot_containers"))
                    if (value is GdDict container)
                    {
                        GdDict row = container.DeepCopy();
                        row["registration_path"] = "Core/Procgen/GameplaySliceBuilder.cs:Build -> RunSession.Loot.cs:BuildLootContainers";
                        containers.Add(row);
                    }
            long index = 0;
            foreach (object value in containers)
            {
                if (!(value is GdDict container)) continue;
                string cid = container.GetString("id"), prefix = "container:" + index++ + ":" + cid;
                string path = container.GetString("registration_path");
                if (container.Has("contents"))
                {
                    // Use the same override rule as LootContainer, but keep bad quantities for diagnostics.
                    long stackIndex = 0;
                    foreach (object stackValue in container.GetArrayOrEmpty("contents"))
                        if (stackValue is GdDict stack)
                        {
                            string item = stack.GetString("item_id");
                            edges.Add(Source(prefix + ":" + stackIndex++, cid, "authored_loot", new GdDict(), new GdDict { { item, stack.Get("qty", stack.Get("quantity", 0L)) } }, path, container.GetString("source_path", path) + ".contents"));
                        }
                }
                else
                {
                    string tableId = container.GetString("loot_table", "generic_crate");
                    GdDict table = catalog.GetDictOrEmpty("loot_tables").GetDictOrEmpty(tableId);
                    if (table.IsEmpty)
                        edges.Add(Source(prefix, cid, "loot", new GdDict(), new GdDict { { "", 0L } }, "", LOOT_PATH + ":" + tableId));
                    long entryIndex = 0;
                    foreach (object entryValue in table.GetArrayOrEmpty("entries"))
                        if (entryValue is GdDict entry && entry.GetFloat("weight") > 0 && table.GetInt("rolls") > 0)
                        {
                            GdDict edge = Source(prefix + ":" + entryIndex++, cid, "loot", new GdDict(), new GdDict { { entry.GetString("item_id"), entry.Get("qty_max", 1L) } }, path, LOOT_PATH + ":" + tableId + ".entries");
                            edge["random_weight"] = entry.GetFloat("weight"); edges.Add(edge);
                        }
                }
            }
            foreach (object value in registrations.GetArrayOrEmpty("pickups"))
                if (value is GdDict pickup) edges.Add(Source("pickup:" + pickup.GetString("item_id"), pickup.GetString("producer_id", pickup.GetString("item_id")), "pickup", new GdDict(), new GdDict { { pickup.GetString("item_id"), 1L } }, pickup.GetString("registration_path"), pickup.GetString("registration_path")));
            foreach (object value in catalog.GetArrayOrEmpty("recipes"))
                if (value is GdDict recipe)
                {
                    string id = recipe.GetString("recipe_id");
                    string station = EffectiveStation(recipe);
                    GdDict output = recipe.GetDictOrEmpty("produces");
                    GdDict edge = Source("recipe:" + id, id, "craft", recipe.GetDictOrEmpty("ingredients").DeepCopy(), new GdDict { { output.GetString("item_id"), output.Get("quantity", 0L) } }, graph.GetDictOrEmpty("stations").GetDictOrEmpty(station).GetString("registration_path"), recipe.GetString("source_path"));
                    edge["recipe_id"] = id; edge["station_kind"] = station; edge["station_tier_min"] = recipe.GetInt("station_tier_min");
                    edge["required_skill"] = recipe.GetString("required_skill", "fabrication"); edge["required_skill_level"] = recipe.GetInt("required_skill_level");
                    edge["knowledge_source"] = recipe.GetString("knowledge_source", "starter"); edge["book_id"] = recipe.GetString("knowledge_book_id");
                    edge["power_cost"] = recipe.GetFloat("power_cost"); edges.Add(edge);
                }
            foreach (var item in catalog.GetDictOrEmpty("items"))
                if (item.Value is GdDict def && def.GetArrayOrEmpty("yields").Count > 0)
                {
                    var outputs = new GdDict();
                    foreach (object value in def.GetArrayOrEmpty("yields")) if (value is GdDict yieldRow) outputs[yieldRow.GetString("material_id")] = yieldRow.Get("quantity", 0L);
                    edges.Add(Source("junk:" + V.Str(item.Key), "junk:" + V.Str(item.Key), "salvage", new GdDict { { item.Key, 1L } }, outputs, graph.GetDictOrEmpty("stations").GetDictOrEmpty("salvage").GetString("registration_path"), ItemDefs.JUNK_ITEMS_PATH + ":items." + V.Str(item.Key) + ".yields"));
                }
            foreach (object value in registrations.GetArrayOrEmpty("placed_components"))
                if (value is GdDict placed && placed.GetBool("mounted", true))
                {
                    string id = placed.GetString("component_id"), instance = placed.GetString("component_instance_id");
                    GdDict def = catalog.GetDictOrEmpty("components").GetDictOrEmpty(id);
                    GdDict edge = Source("component:" + instance, id, "component_salvage", new GdDict(), new GdDict { { def.GetString("item_form", id), 1L } }, registrations.GetBool("work_registered") ? "Core/Session/RunSession.WorkAction.cs:TryWorkActionInteract -> TickWorkAction" : "", ComponentCatalog.DEFAULT_PATH + ":components." + id + ".item_form");
                    edge["tool_ids"] = GdArray.Of("wrench"); edges.Add(edge);
                }
            if (registrations.GetBool("work_registered"))
                foreach (var pair in catalog.GetDictOrEmpty("work_actions"))
                    if (pair.Value is GdDict action && !action.GetDictOrEmpty("materials_yielded").IsEmpty)
                    {
                        string id = V.Str(pair.Key);
                        GdDict edge = Source("work:" + id, id, "work", action.GetDictOrEmpty("materials_consumed").DeepCopy(), action.GetDictOrEmpty("materials_yielded").DeepCopy(), "Core/Session/RunSession.WorkAction.cs:TryWorkActionInteract -> TickWorkAction", WorkActionCatalog.DEFAULT_PATH + ":actions." + id);
                        if (action.GetString("tool_class").Length != 0) edge["tool_ids"] = GdArray.Of(action.GetString("tool_class"));
                        edges.Add(edge);
                    }
            foreach (object value in registrations.GetArrayOrEmpty("crops"))
                if (value is GdDict crop)
                {
                    string id = crop.GetString("crop_id");
                    GdDict edge = Source("crop:" + id, id, "production", new GdDict { { "purified_water", (long)Math.Ceiling(crop.GetFloat("water_cost")) } }, new GdDict { { crop.GetString("produce_item_id"), crop.Get("produce_quantity", 0L) } }, graph.GetDictOrEmpty("stations").GetDictOrEmpty("hydroponics").GetString("registration_path"), RunSession.HYDROPONICS_CROPS_CONFIG_PATH + ":crops." + id);
                    edge["required_skill_level"] = crop.GetInt("required_skill_level"); edge["power_cost"] = crop.GetFloat("power_cost"); edges.Add(edge);
                }
            GdDict recycler = registrations.GetDictOrEmpty("recycler");
            if (!recycler.IsEmpty)
            {
                // The live wrapper loads contaminated_water; output identity/ratio come from its bound model.
                GdDict edge = Source("production:water_recycler", "water_recycler", "production", new GdDict { { "contaminated_water", 1L } }, new GdDict { { recycler.GetString("output_item_id"), recycler.Get("conversion_ratio", 0.0) } }, recycler.GetString("registration_path"), "Core/Session/Interactables/ProductionStation.cs:InteractRecycler");
                edge["power_cost"] = recycler.GetFloat("power_cost"); edges.Add(edge);
            }
            return graph;
        }

        static GdDict Source(string eid, string pid, string kind, GdDict inputs, GdDict outputs, string registration, string path) => new GdDict
        {
            { "edge_id", eid }, { "producer_id", pid }, { "consumer_id", pid }, { "kind", kind }, { "inputs", inputs }, { "outputs", outputs },
            { "tool_ids", new GdArray() }, { "registration_path", registration }, { "source_path", path }, { "diagnostic", false }, { "finite", true },
        };

        static string EffectiveStation(GdDict recipe) => recipe.GetString("category") == "deconstruction" ? "salvage" : recipe.GetString("station_kind");

        static GdDict ReadProduction(string path, GdArray errors)
        {
            if (!CatalogRegistry.Exists(path))
            {
                Add(errors, "missing_catalog", "catalog", "", path, "Production resource is missing.");
                return null;
            }
            if (!(CatalogRegistry.Load(path) is GdDict root))
            {
                Add(errors, "malformed_catalog", "catalog", "", path, "Production resource must contain a parsed JSON dictionary.");
                return null;
            }
            return root;
        }

        static GdArray ArraySection(GdDict root, string member, string path, GdArray errors)
        {
            if (root == null) return new GdArray();
            if (!(root.Get(member) is GdArray rows))
            {
                Add(errors, "invalid_catalog_shape", "catalog", member, path + ":" + member, "Expected an array of production records.");
                return new GdArray();
            }
            for (int i = 0; i < rows.Count; i++)
                if (!(rows[i] is GdDict)) Add(errors, "invalid_catalog_row", "catalog", member + "[" + i + "]", path + ":" + member + "[" + i + "]", "Production record must be a dictionary.");
            return rows;
        }

        static GdDict DictionarySection(GdDict root, string member, string path, GdArray errors)
        {
            if (root == null) return new GdDict();
            if (!(root.Get(member) is GdDict rows))
            {
                Add(errors, "invalid_catalog_shape", "catalog", member, path + ":" + member, "Expected a dictionary of production records.");
                return new GdDict();
            }
            ValidateDictionaryRows(rows, member, path, errors);
            return rows;
        }

        static void ValidateDictionaryRows(GdDict rows, string member, string path, GdArray errors)
        {
            if (rows == null) return;
            foreach (var pair in rows)
            {
                string key = V.Str(pair.Key);
                if (key.StartsWith("_", StringComparison.Ordinal) || key == "version" || key == "schema_version" || key == "schema_notes") continue;
                string row = member.Length == 0 ? key : member + "." + key;
                if (!(pair.Value is GdDict)) Add(errors, "invalid_catalog_row", "catalog", row, path + ":" + row, "Production record must be a dictionary.");
            }
        }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static bool PositiveInteger(object value) => (value is long n && n > 0) || (value is double d && Finite(d) && d > 0 && d <= long.MaxValue && Math.Floor(d) == d);
        static void Reference(GdArray rows, string cid, string id, string path, string kind, bool source) => rows.Add(new GdDict
        {
            { "consumer_id", cid }, { "item_id", id }, { "source_path", path }, { "reference_kind", kind }, { "requires_source", source },
        });

        static bool HasLearning(GdDict graph, string kind, string id)
        {
            foreach (object value in graph.GetArrayOrEmpty("learning")) if (value is GdDict row && row.GetString("kind") == kind && row.GetString("id") == id && row.GetString("registration_path").Length != 0 && !row.GetBool("diagnostic")) return true;
            return false;
        }

        static bool HasTier(string kind, long need, GdDict stations, GdArray sources)
        {
            if (RegisteredStation(stations, kind) && stations.GetDictOrEmpty(kind).GetInt("tier") >= need) return true;
            foreach (object value in sources) if (value is GdDict row && row.GetString("station_kind") == kind && row.GetInt("tier") >= need && row.GetString("registration_path").Length != 0 && !row.GetBool("diagnostic")) return true;
            return false;
        }

        static bool RegisteredStation(GdDict stations, string kind) =>
            stations.GetDictOrEmpty(kind).GetString("registration_path").Length != 0 && !stations.GetDictOrEmpty(kind).GetBool("diagnostic");

        static bool IsProduction(string id, GdDict manifest, GdArray errors, GdArray dispositions)
        {
            GdDict row = manifest.GetDictOrEmpty(id);
            string state = row.GetString("state", "production");
            bool valid = state == "production" || state == "diagnostic" || (state == "deferred" && row.GetString("reason").Length != 0 && row.GetString("owner").Length != 0);
            if (!valid) Add(errors, "invalid_disposition", id, "", "exposureManifest." + id, "A deferral requires a reason and owner; otherwise production remains required.");
            foreach (object existing in dispositions) if (existing is GdDict d && d.GetString("consumer_id") == id) return !valid || state == "production";
            dispositions.Add(new GdDict { { "consumer_id", id }, { "state", valid ? state : "production" }, { "reason", row.GetString("reason") }, { "owner", row.GetString("owner") } });
            return !valid || state == "production";
        }

        static void ValidateItems(GdDict items, GdArray errors)
        {
            foreach (var pair in items)
            {
                string id = V.Str(pair.Key), path = "items." + id;
                if (!(pair.Value is GdDict def)) { Add(errors, "invalid_definition", "inventory", id, path, "Item definition must be a dictionary."); continue; }
                double weight = def.GetFloat("weight");
                if (!Finite(weight) || weight < 0) Add(errors, "invalid_weight", "inventory", id, path + ".weight", "Weight must be finite and nonnegative.");
                if (def.Has("max_stack") && !PositiveInteger(def.Get("max_stack"))) Add(errors, "invalid_stack", "inventory", id, path + ".max_stack", "Stack limit must be a finite positive integer.");
            }
        }

        static void Add(GdArray rows, string code, string cid, string id, string path, string detail)
        {
            foreach (object value in rows) if (value is GdDict prior && prior.GetString("code") == code && prior.GetString("consumer_id") == cid && prior.GetString("item_id") == id && prior.GetString("source_path") == path) return;
            rows.Add(new GdDict { { "code", code }, { "consumer_id", cid }, { "item_id", id }, { "source_path", path }, { "detail", detail } });
        }

        static GdDict Report(GdArray errors, GdArray warnings, GdArray coverage, GdArray dispositions, HashSet<string> potential, GdDict denominators)
        {
            var ids = new List<string>(potential); ids.Sort(StringComparer.Ordinal);
            return new GdDict
            {
                { "ok", errors.IsEmpty }, { "errors", errors }, { "warnings", warnings }, { "coverage", coverage }, { "dispositions", dispositions },
                { "potential_item_ids", new GdArray(ids) }, { "denominators", denominators }, { "natural_traversal_witness", false },
            };
        }
    }
}
