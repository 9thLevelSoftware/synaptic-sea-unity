// Unity-port additions to scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 (no Godot source):
// C4 run difficulty / biome dials on the home ship, and the A4 kit-path contract for every loaded ship root.
using System;
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        // ------------------------------------------------------------------ C4: run context
        DifficultyProfile _runDifficulty;
        BiomeProfile _runBiome;

        /// <summary>The run difficulty id (<see cref="RunSessionDeps.DifficultyId"/>, restored from saves).</summary>
        public string DifficultyId { get; private set; } = DifficultyProfile.STANDARD_ID;

        /// <summary>The run biome id for the home ship ("" = Godot's seed-selected loot biome, no biome dials).</summary>
        public string BiomeId { get; private set; } = "";

        /// <summary>The run seed recorded in saves: <see cref="RunSessionDeps.RunSeed"/>, else the home blueprint seed.</summary>
        public long RunSeed { get; private set; }

        /// <summary>The resolved difficulty profile.</summary>
        public DifficultyProfile RunDifficulty => _runDifficulty ?? (_runDifficulty = DifficultyProfile.ForId(DifficultyId));

        /// <summary>The resolved home biome profile; null when <see cref="BiomeId"/> is empty.</summary>
        public BiomeProfile RunBiome => _runBiome;

        /// <summary>
        /// <c>biome.modifier(dial) * difficulty.modifier(dial)</c> clamped to [0, 3] for the home ship (a null biome is
        /// identity). Every dial is exactly 1.0 for "standard" with no biome.
        /// </summary>
        public double HomeDial(string dial) => DifficultyProfile.CombinedModifier(_runBiome, RunDifficulty, dial);

        /// <summary>The home ship's ambient intensity dial (the Runtime scales ambient light / beds with it).</summary>
        public double HomeAmbientIntensity => HomeDial(DifficultyProfile.DIAL_AMBIENT);

        /// <summary>The run context recorded in the run snapshot (<c>run_context</c>).</summary>
        public GdDict GetRunContextSummary() => new GdDict
        {
            { "seed", RunSeed },
            { "biome_id", BiomeId },
            { "difficulty_id", DifficultyId },
        };

        void ApplyRunContext(string difficultyId, string biomeId, long? seed)
        {
            DifficultyId = string.IsNullOrEmpty(difficultyId) ? DifficultyProfile.STANDARD_ID : difficultyId;
            BiomeId = biomeId ?? "";
            _runDifficulty = DifficultyProfile.ForId(DifficultyId);
            _runBiome = BiomeId.Length > 0 ? BiomeProfile.FromDict(new ShipLayoutGenerator().ResolveBiome(BiomeId)) : null;
            RunSeed = seed ?? LoadBlueprintForSystems().SeedValue;
        }

        /// <summary>Restores <c>run_context</c> from a snapshot (empty = keep the current context: pre-context saves).</summary>
        void ApplyRunContextSummary(GdDict ctx)
        {
            if (ctx == null || ctx.IsEmpty)
                return;
            ApplyRunContext(V.Str(ctx.Get("difficulty_id", DifficultyId)), V.Str(ctx.Get("biome_id", BiomeId)), V.I64(ctx.Get("seed", RunSeed)));
        }

        /// <summary>True when the home layout was generated with a run context (EncounterInjector already scaled its markers).</summary>
        bool HomeLayoutHasRunContext()
        {
            GdDict layout = Loader != null && Loader.IsValid ? Loader.LayoutDoc : null;
            if (layout == null)
                return false;
            return V.Str(layout.Get("difficulty_id", "")).Length > 0 || V.Str(layout.Get("biome_id", "")).Length > 0;
        }

        /// <summary>Encounter density / aggression dials pushed onto the threat runtime before it spawns for the current ship.</summary>
        void ApplyThreatRunModifiers()
        {
            if (ThreatManager == null)
                return;
            bool home = !AwayFromStart;
            ThreatManager.EncounterDensityModifier = home && !HomeLayoutHasRunContext() ? HomeDial(DifficultyProfile.DIAL_ENCOUNTER) : 1.0;
            ThreatManager.AggressionModifier = home ? HomeDial(DifficultyProfile.DIAL_HAZARD) : 1.0;
        }

        /// <summary>Hazard dial at home (1.0 away).</summary>
        double HomeHazardModifier() => AwayFromStart ? 1.0 : HomeDial(DifficultyProfile.DIAL_HAZARD);

        /// <summary>Difficulty loot quality multiplier at home (the biome half already rides the loot biome).</summary>
        double HomeDifficultyLootMultiplier() => AwayFromStart ? 1.0 : RunDifficulty.LootQualityModifier;

        // ------------------------------------------------------------------ A4: kit path per loaded ship root
        readonly Dictionary<IShipSceneRoot, string> _kitPathByRoot = new Dictionary<IShipSceneRoot, string>();

        /// <summary>
        /// The res:// kit document a loaded ship root was built from (home: the resolved <see cref="KitPath"/>; derelicts:
        /// <see cref="ShipDocuments.KitPath"/>; lifeboat: <see cref="LifeBoatBuilder.BuildResult.KitPath"/>). "" for an
        /// unknown root.
        /// </summary>
        public string KitPathForRoot(IShipSceneRoot root)
        {
            if (root == null)
                return "";
            return _kitPathByRoot.TryGetValue(root, out string p) ? p : "";
        }

        /// <summary><see cref="KitPathForRoot"/> of a ship instance's current scene root.</summary>
        public string KitPathForShip(ShipInstance ship) => ship == null ? "" : KitPathForRoot(ship.SceneRoot);

        /// <summary>
        /// Resolves the home kit: an explicit kit with a complete wrapper map is used as is; one without (hazard,
        /// industrial) falls back to v0; an empty path resolves from the layout's <c>kit_id</c> the same way. This is the
        /// Godot <c>ship_generator.gd kit_path_for_layout</c> rule applied to the home load.
        /// </summary>
        string ResolveHomeKitPath(string layoutPath, string kitPath)
        {
            var generator = ShipGenerator ?? new ShipGenerator();
            if (!string.IsNullOrEmpty(kitPath))
            {
                string kitId = KitIdFromPath(kitPath);
                // Only a kit under data/kits follows the layout rule; any other explicit document is used as given.
                if (kitPath != "res://data/kits/" + kitId + ".json")
                    return kitPath;
                return generator.KitPathForLayout(new GdDict { { "kit_id", kitId } });
            }
            GdDict layout = string.IsNullOrEmpty(layoutPath) || !CatalogRegistry.Exists(layoutPath) ? new GdDict() : CatalogRegistry.LoadDict(layoutPath) ?? new GdDict();
            return generator.KitPathForLayout(layout);
        }

        static string KitIdFromPath(string kitPath)
        {
            string file = kitPath;
            int slash = file.LastIndexOf('/');
            if (slash >= 0)
                file = file.Substring(slash + 1);
            return GdString.EndsWith(file, ".json") ? file.Substring(0, file.Length - 5) : file;
        }

        void RecordKitPath(IShipSceneRoot root, string kitPath)
        {
            if (root != null)
                _kitPathByRoot[root] = kitPath ?? "";
        }

        /// <summary><see cref="IShipSceneHost.BuildShipScene"/> + the kit-path record.</summary>
        IShipLoaderView BuildShipSceneFromDocuments(ShipDocuments docs)
        {
            if (docs == null)
                return null;
            IShipLoaderView root = ShipHost?.BuildShipScene(docs);
            RecordKitPath(root, docs.KitPath);
            return root;
        }
    }
}
