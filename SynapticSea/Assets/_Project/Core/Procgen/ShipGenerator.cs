// Ported from scripts/procgen/ship_generator.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
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
        /// <summary>Witness that GameplaySliceBuilder produced this document; diagnostic archival preserves its separate contract.</summary>
        public bool RuntimeGeneratedGameplay;

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
        public ShipLayoutGenerator LayoutGenerator = new ShipLayoutGenerator();

        /// <summary>Per-derelict run context forwarded to generate_with_options (empty = legacy bare geometry).</summary>
        public string BiomeId = "";
        public string DifficultyId = "";
        public bool RichExpeditions;
        public string ExpeditionProfile = ConstrainedExpedition.Profile;

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

        /// <summary>Builds a blueprint from seed/size/condition and runs the layout pipeline.</summary>
        public ShipDocuments GenerateFromSeed(long seedValue, long size = 0, long condition = 1)
        {
            var blueprint = new ShipBlueprint(size, condition, seedValue);
            if (RichExpeditions && size >= 1 && size <= 2) blueprint.GenerationProfile = ExpeditionProfile;
            return Generate(blueprint);
        }

        object IShipGenerator.GenerateFromSeed(long seedValue, long size, long condition) => GenerateFromSeed(seedValue, size, condition);

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
            PurposefulExpedition.Furnish(layout, gameplay);
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
                RuntimeGeneratedGameplay = true,
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
