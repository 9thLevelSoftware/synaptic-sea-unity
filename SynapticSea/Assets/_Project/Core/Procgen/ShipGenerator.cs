// Ported from scripts/procgen/ship_generator.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// The Rust <c>DerelictGenerator</c> GDExtension seam (addons/derelict ships only prebuilt binaries, no source).
    /// There is no native implementation; with no source set, <see cref="ShipGenerator"/> falls back to the GDScript
    /// pipeline exactly like Godot on a platform without the extension. A future Rust cdylib over P/Invoke implements
    /// this interface.
    /// </summary>
    public interface IDerelictLayoutSource
    {
        /// <summary><c>generator_version()</c>; must equal <see cref="ShipGenerator.WORLDGEN_VERSION"/>.</summary>
        long GeneratorVersion();

        /// <summary><c>export_layout_json(seed, params, kit_id)</c>: layout.json text, "" on failure.</summary>
        string ExportLayoutJson(long seedValue, GdDict parameters, string kitId);

        /// <summary><c>export_gameplay_slice_json(seed, params)</c>: gameplay_slice.json text, "" on failure.</summary>
        string ExportGameplaySliceJson(long seedValue, GdDict parameters);
    }

    /// <summary>
    /// RUNTIME: the documents GeneratedShipLoader consumed. Godot handed them to
    /// <c>load_from_paths(layout, kit, gameplay, is_away)</c> (pipeline path, via JSON files under
    /// user://procgen_temp) or <c>load_from_documents(...)</c> (worldgen path); the Unity ShipSceneBuilder takes this
    /// bundle instead and names the resulting root <see cref="Name"/>.
    /// </summary>
    public sealed class ShipDocuments
    {
        /// <summary>
        /// The layout as the loader saw it. For the pipeline path this is the JSON round trip of the file Godot wrote
        /// (<c>JSON.stringify(layout, "  ")</c> then <c>JSON.parse_string</c>): numbers are doubles and Vector2i/Vector3
        /// values are "(x, y)" strings, exactly what load_from_paths parsed.
        /// </summary>
        public GdDict Layout;

        /// <summary>The structural kit document (res://data/kits/*.json).</summary>
        public GdDict Kit;

        /// <summary>The gameplay slice (JSON round trip for the pipeline path, like <see cref="Layout"/>).</summary>
        public GdDict GameplaySlice;

        /// <summary>Generated derelicts are the away branch (atmosphere hook).</summary>
        public bool IsAway;

        /// <summary>
        /// Pipeline path: the exact layout.json text Godot wrote (<c>JSON.stringify(layout, "  ")</c>); null for the
        /// worldgen path, which handed the dictionaries over directly.
        /// </summary>
        public string LayoutJson;

        /// <summary>
        /// Pipeline path: the in-memory layout before it was written (Vector2i / Vector3 typed); the worldgen path sets
        /// it to <see cref="Layout"/>.
        /// </summary>
        public GdDict SourceLayout;

        /// <summary>Pipeline path: the exact gameplay_slice.json text; null for the worldgen path.</summary>
        public string GameplaySliceJson;

        /// <summary>res:// path of <see cref="Kit"/>.</summary>
        public string KitPath = "";

        /// <summary>Node name Godot gave the loader root.</summary>
        public string Name = "GeneratedShip";

        public GdDict ToDict() => new GdDict
        {
            { "layout", Layout },
            { "kit", Kit },
            { "gameplay_slice", GameplaySlice },
            { "is_away", IsAway },
            { "kit_path", KitPath },
            { "name", Name },
        };
    }

    /// <summary>
    /// Orchestrator wiring the ShipBlueprint-driven procgen pipeline end to end. Uses <see cref="ShipLayoutGenerator"/>
    /// for the layout, builds the gameplay slice and returns the loader documents (RUNTIME: GDScript wrote them to
    /// temp files and returned the loaded GeneratedShipLoader Node3D).
    /// </summary>
    public sealed class ShipGenerator : IShipGenerator
    {
        public const bool USE_WORLDGEN = true;
        public const long WORLDGEN_VERSION = 2;
        public const string WORLDGEN_KIT_ID = "ship_structural_v0";
        public const string WORLDGEN_KIT_PATH = "res://data/kits/ship_structural_v0.json";

        public static readonly GdDict WORLDGEN_ARCHETYPE_BY_SIZE = new GdDict { { 0L, "shuttle" }, { 1L, "corvette" }, { 2L, "freighter" } };
        public static readonly GdDict WORLDGEN_INTACTNESS_BY_CONDITION = new GdDict { { 0L, 9500L }, { 1L, 6000L }, { 2L, 2000L } };

        public ShipLayoutGenerator LayoutGenerator = new ShipLayoutGenerator();

        /// <summary>
        /// The DerelictGenerator seam (GDScript <c>ClassDB.class_exists("DerelictGenerator")</c>). Null = extension not
        /// loaded: <see cref="GenerateFromSeed"/> uses the GDScript pipeline.
        /// </summary>
        public IDerelictLayoutSource DerelictSource;

        bool _worldgenKitLoaded;
        GdDict _worldgenKitDoc = new GdDict();

        /// <summary>Per-derelict run context forwarded to generate_with_options (empty = legacy bare geometry).</summary>
        public string BiomeId = "";
        public string DifficultyId = "";

        readonly GdDict _wrapperMapCache = new GdDict();

        /// <summary>Sets the biome / difficulty applied to the NEXT Generate()/GenerateFromSeed() call.</summary>
        public void ConfigureRunContext(string pBiomeId, string pDifficultyId)
        {
            BiomeId = pBiomeId ?? "";
            DifficultyId = pDifficultyId ?? "";
        }

        /// <summary>Builds the ship documents for <paramref name="blueprint"/>; null on failure.</summary>
        public ShipDocuments Generate(ShipBlueprint blueprint, GdDict archetype = null)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint), "ShipGenerator: blueprint must not be null");
            archetype = archetype ?? new GdDict();

            // F5: production travel often passed {}; load derelict archetype defaults so guaranteed_roles /
            // role_weights actually apply.
            if (archetype.IsEmpty && (BiomeId.Length != 0 || DifficultyId.Length != 0))
                archetype = DefaultDerelictArchetype();

            GdDict layout = LayoutGenerator.GenerateWithOptions(blueprint, archetype, BiomeId, DifficultyId, ExtendedFor(DifficultyId));
            if (layout.IsEmpty)
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL layout generation returned empty");
                return null;
            }

            return LoadLayoutAsDocuments(layout);
        }

        static GdDict DefaultDerelictArchetype()
        {
            const string path = "res://data/procgen/archetypes/derelict.json";
            if (CatalogRegistry.Exists(path))
            {
                GdDict parsed = CatalogRegistry.LoadDict(path);
                if (parsed != null) return parsed.DeepCopy();
            }
            return new GdDict
            {
                { "name", "Derelict" },
                { "guaranteed_roles", GdArray.Of("dock") },
                { "max_duplicates", 3L },
                {
                    "role_weights", new GdDict
                    {
                        { "cargo", 4L }, { "corridor", 3L }, { "bridge", 3L }, { "crew_quarters", 2L }, { "hangar", 2L },
                    }
                },
            };
        }

        /// <summary>E1: any real difficulty (production travel always sets one) unlocks the extended template pool.</summary>
        static bool ExtendedFor(string diffId) => !string.IsNullOrEmpty(diffId);

        public GdDict GenerateLayout(ShipBlueprint blueprint, GdDict archetype = null)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint), "ShipGenerator: blueprint must not be null");
            return LayoutGenerator.Generate(blueprint, archetype ?? new GdDict());
        }

        /// <summary>
        /// Builds a blueprint from seed/size/condition and generates. Prefers the DerelictGenerator seam when one is
        /// set; otherwise the layout pipeline.
        /// </summary>
        public ShipDocuments GenerateFromSeed(long seedValue, long size = 0, long condition = 1)
        {
            if (USE_WORLDGEN && DerelictSource != null) return GenerateViaWorldgen(seedValue, size, condition);
            var blueprint = new ShipBlueprint(size, condition, seedValue);
            return Generate(blueprint);
        }

        object IShipGenerator.GenerateFromSeed(long seedValue, long size, long condition) => GenerateFromSeed(seedValue, size, condition);

        ShipDocuments GenerateViaWorldgen(long seedValue, long size, long condition)
        {
            IDerelictLayoutSource generator = DerelictSource;
            if (generator == null)
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL DerelictGenerator class unavailable");
                return null;
            }
            long generatorVersion = generator.GeneratorVersion();
            if (generatorVersion != WORLDGEN_VERSION)
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL unsupported DerelictGenerator version: " + GdString.FormatInt(generatorVersion));
                return null;
            }
            if (!WORLDGEN_ARCHETYPE_BY_SIZE.Has(size))
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL unsupported worldgen size: " + GdString.FormatInt(size));
                return null;
            }
            if (!WORLDGEN_INTACTNESS_BY_CONDITION.Has(condition))
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL unsupported worldgen condition: " + GdString.FormatInt(condition));
                return null;
            }
            string archetypeId = V.Str(WORLDGEN_ARCHETYPE_BY_SIZE[size]);
            long intactnessBp = V.I64(WORLDGEN_INTACTNESS_BY_CONDITION[condition]);
            var parameters = new GdDict { { "archetype_id", archetypeId }, { "intactness_override", intactnessBp } };

            string layoutText = V.Str(generator.ExportLayoutJson(seedValue, parameters, WORLDGEN_KIT_ID) ?? "");
            if (layoutText.Length == 0)
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL worldgen layout export returned empty");
                return null;
            }
            if (!(GdJson.ParseString(layoutText) is GdDict layoutParsed))
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL worldgen layout export was not a Dictionary");
                return null;
            }
            GdDict layout = layoutParsed.DeepCopy();

            string gameplayText = V.Str(generator.ExportGameplaySliceJson(seedValue, parameters) ?? "");
            if (gameplayText.Length == 0)
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL worldgen gameplay slice export returned empty");
                return null;
            }
            if (!(GdJson.ParseString(gameplayText) is GdDict gameplayParsed))
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL worldgen gameplay slice export was not a Dictionary");
                return null;
            }
            GdDict exportedGameplay = gameplayParsed.DeepCopy();

            layout["kit_id"] = WORLDGEN_KIT_ID;
            layout["biome_id"] = BiomeId;
            layout["difficulty_id"] = DifficultyId;
            BiomeProfile biome = BiomeProfile.FromDict(LayoutGenerator.ResolveBiome(BiomeId));
            DifficultyProfile difficulty = DifficultyProfile.FromDict(LayoutGenerator.ResolveDifficulty(DifficultyId));
            layout = new EncounterInjector().Inject(layout, biome, difficulty, seedValue);

            GdDict gameplay = new GameplaySliceBuilder().Build(layout);
            if (gameplay.IsEmpty || !(gameplay.Get("objectives", new GdArray()) is GdArray objectives) || objectives.IsEmpty)
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL worldgen gameplay slice builder returned no objectives");
                return null;
            }
            GdDict lootTables = LootRoller.LoadTables();
            if (lootTables.IsEmpty)
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL game loot registry is empty");
                return null;
            }
            if (!ResolveWorldgenLootContainers(gameplay, exportedGameplay, lootTables)) return null;

            GdDict kit = LoadWorldgenKit();
            if (kit.IsEmpty) return null;
            // RUNTIME: GeneratedShipLoader.load_from_documents(layout, kit, gameplay, true); root named "GeneratedShip".
            return new ShipDocuments { Layout = layout, Kit = kit, GameplaySlice = gameplay, IsAway = true, KitPath = WORLDGEN_KIT_PATH, SourceLayout = layout };
        }

        GdDict LoadWorldgenKit()
        {
            if (_worldgenKitLoaded) return _worldgenKitDoc.DeepCopy();
            _worldgenKitLoaded = true;
            if (!CatalogRegistry.Exists(WORLDGEN_KIT_PATH))
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL structural kit not found: " + WORLDGEN_KIT_PATH);
                return new GdDict();
            }
            GdDict parsed = CatalogRegistry.LoadDict(WORLDGEN_KIT_PATH);
            if (parsed == null)
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL structural kit JSON is invalid: " + WORLDGEN_KIT_PATH);
                return new GdDict();
            }
            _worldgenKitDoc = parsed.DeepCopy();
            return _worldgenKitDoc.DeepCopy();
        }

        /// <summary>
        /// Worldgen merge: every builder container must name a known loot table; exported containers are remapped
        /// ("worldgen_seeded" -&gt; a game table by container kind) and appended unless one already sits at the same
        /// room + approach cell.
        /// </summary>
        public static bool ResolveWorldgenLootContainers(GdDict gameplay, GdDict exportedGameplay, GdDict lootTables)
        {
            if (!(gameplay.Get("loot_containers", new GdArray()) is GdArray builderContainers))
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL gameplay builder loot_containers is not an Array");
                return false;
            }
            foreach (var containerVariant in builderContainers)
            {
                if (!(containerVariant is GdDict container)) continue;
                string tableId = V.Str(container.Get("loot_table", ""));
                if (!lootTables.Has(tableId))
                {
                    CoreServices.Log.Error("SHIP GENERATOR FAIL gameplay builder loot table missing: " + tableId);
                    return false;
                }
            }

            if (!(exportedGameplay.Get("loot_containers", new GdArray()) is GdArray exportedContainers))
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL worldgen loot_containers is not an Array");
                return false;
            }
            GdArray mergedContainers = builderContainers.DeepCopy();
            foreach (var exportedVariant in exportedContainers)
            {
                if (!(exportedVariant is GdDict exportedSource)) continue;
                GdDict exportedContainer = exportedSource.DeepCopy();
                string tableId = V.Str(exportedContainer.Get("loot_table", ""));
                if (!lootTables.Has(tableId))
                {
                    if (tableId != "worldgen_seeded")
                    {
                        CoreServices.Log.Error("SHIP GENERATOR FAIL worldgen loot table missing: " + tableId);
                        return false;
                    }
                    tableId = MapWorldgenLootTable(V.Str(exportedContainer.Get("kind", "")), lootTables);
                    if (tableId.Length == 0)
                    {
                        CoreServices.Log.Error("SHIP GENERATOR FAIL no game loot table mapping for worldgen container kind: " +
                                               V.Str(exportedContainer.Get("kind", "")));
                        return false;
                    }
                    exportedContainer["loot_table"] = tableId;
                }
                if (!HasLootContainerAt(mergedContainers, exportedContainer)) mergedContainers.Append(exportedContainer);
            }
            gameplay["loot_containers"] = mergedContainers;
            return true;
        }

        public static string MapWorldgenLootTable(string containerKind, GdDict lootTables)
        {
            string candidate = "";
            switch (containerKind)
            {
                case "cargo_crate":
                case "supply_crate":
                    candidate = "salvage_cargo";
                    break;
                case "parts_locker":
                case "tool_rack":
                    candidate = "salvage_engineering";
                    break;
                case "bridge_locker":
                case "footlocker":
                case "med_cabinet":
                case "food_locker":
                case "weapon_locker":
                case "ammo_crate":
                case "filter_cabinet":
                case "suit_locker":
                    candidate = "generic_locker";
                    break;
                default:
                    if (GdString.Contains(containerKind, "crate")) candidate = "generic_crate";
                    else if (GdString.Contains(containerKind, "locker")) candidate = "generic_locker";
                    break;
            }
            return lootTables.Has(candidate) ? candidate : "";
        }

        static bool HasLootContainerAt(GdArray containers, GdDict candidate)
        {
            string candidateRoom = V.Str(candidate.Get("room_id", ""));
            object candidateCell = candidate.Get("approach_cell", new GdArray());
            foreach (var existingVariant in containers)
            {
                if (!(existingVariant is GdDict existing)) continue;
                if (V.Str(existing.Get("room_id", "")) != candidateRoom) continue;
                if (V.Str(existing.Get("approach_cell", new GdArray())) == V.Str(candidateCell)) return true;
            }
            return false;
        }

        /// <summary>
        /// GDScript <c>_load_layout_as_scene</c>: recompiles/validates only when no validated plan is stamped, builds
        /// the gameplay slice (copying its arc_zones onto an arc-free layout) and returns the loader documents.
        /// RUNTIME: GDScript wrote layout.json / gameplay_slice.json under user://procgen_temp and called
        /// <c>GeneratedShipLoader.load_from_paths(layout, kit_path, gameplay, true)</c>.
        /// </summary>
        public ShipDocuments LoadLayoutAsDocuments(GdDict layout)
        {
            // Skip recompile when ShipLayoutGenerator already stamped a validated plan. Never restamp wreck here.
            bool planReady = layout.Get("structural_plan", new GdDict()) is GdDict existingPlan && !existingPlan.IsEmpty &&
                             V.Bool(layout.Get("structural_plan_validated", false));
            if (!planReady)
            {
                GdDict structuralPlan = new StructuralEdgeCompiler().Compile(layout);
                GdDict verdict = new StructuralPlanValidator().Validate(structuralPlan, layout);
                if (!V.Bool(verdict.Get("ok", false)))
                {
                    CoreServices.Log.Error("SHIP GENERATOR FAIL structural plan validation failed: " + GdJson.Stringify(verdict.Get("errors", new GdArray())));
                    return null;
                }
                layout["structural_plan"] = structuralPlan;
                layout["structural_plan_validated"] = true;
            }

            // Build the gameplay slice first so builder-authored hazard links land on the layout before it is written
            // (the loader reads arc_zones from layout.json).
            GdDict gameplay = new GameplaySliceBuilder().Build(layout);
            object layoutArcs = layout.Get("arc_zones", new GdArray());
            object sliceArcs = gameplay.Get("arc_zones", new GdArray());
            if ((!(layoutArcs is GdArray la) || la.IsEmpty) && sliceArcs is GdArray sa && !sa.IsEmpty)
                layout["arc_zones"] = sa.DeepCopy();

            // RUNTIME: layout written as JSON.stringify(layout, "  ") and read back by the loader.
            string layoutJson = GdJson.Stringify(layout, "  ");
            GdDict layoutDoc = GdJson.ParseDict(layoutJson);

            // Layout kit_id selects the structural JSON; kits without a wrapper map fall back to v0.
            string kitPath = KitPathForLayout(layout);
            if (!CatalogRegistry.Exists(kitPath))
            {
                CoreServices.Log.Error("SHIP GENERATOR FAIL structural kit not found: " + kitPath);
                return null;
            }

            string gameplayJson = GdJson.Stringify(gameplay, "  ");
            GdDict gameplayDoc = GdJson.ParseDict(gameplayJson);
            GdDict kit = CatalogRegistry.LoadDict(kitPath) ?? new GdDict();
            // Generated derelicts are the away branch.
            return new ShipDocuments
            {
                Layout = layoutDoc, Kit = kit, GameplaySlice = gameplayDoc, IsAway = true, KitPath = kitPath,
                LayoutJson = layoutJson, GameplaySliceJson = gameplayJson, SourceLayout = layout,
            };
        }

        public string KitPathForLayout(GdDict layout)
        {
            string kitId = V.Str(layout.Get("kit_id", "ship_structural_v0"));
            if (kitId.Length == 0) kitId = "ship_structural_v0";
            string kitPath = "res://data/kits/" + kitId + ".json";
            if (!KitHasWrapperMap(kitPath)) kitPath = "res://data/kits/ship_structural_v0.json";
            return kitPath;
        }

        bool KitHasWrapperMap(string kitPath)
        {
            if (_wrapperMapCache.Has(kitPath)) return V.Bool(_wrapperMapCache[kitPath]);
            bool ok = false;
            if (CatalogRegistry.Exists(kitPath))
            {
                GdDict parsed = CatalogRegistry.LoadDict(kitPath);
                if (parsed != null && parsed.Get("modules", new GdArray()) is GdArray modules && !modules.IsEmpty)
                {
                    ok = true;
                    foreach (var entry in modules)
                    {
                        if (!(entry is GdDict e) || V.Str(e.Get("module_id", "")).Length == 0 || V.Str(e.Get("godot_wrapper_scene", "")).Length == 0)
                        {
                            ok = false;
                            break;
                        }
                    }
                }
            }
            _wrapperMapCache[kitPath] = ok;
            return ok;
        }
    }
}
