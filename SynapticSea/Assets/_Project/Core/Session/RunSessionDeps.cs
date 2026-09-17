// Construction inputs for the RunSession (the exported vars + injected services of playable_generated_ship.gd @ 96ecb2b0).
using System;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// RUNTIME: the physics raycast <c>update_threat_engaged_los()</c> ran through
    /// <c>get_world_3d().direct_space_state.intersect_ray(...)</c> (bodies only).
    /// </summary>
    public interface ILineOfSightProbe
    {
        /// <summary>False when no physics space is available (Godot returned early without touching LOS flags).</summary>
        bool HasSpace { get; }

        /// <summary>Ray from <paramref name="from"/> to <paramref name="to"/>: true + hit position when it hit a body.</summary>
        bool IntersectRay(Vec3 from, Vec3 to, out Vec3 hitPosition);
    }

    /// <summary>
    /// RUNTIME/UI: the HUD panel + menu state the coordinator consulted before opening panels
    /// (<c>recipe_picker_panel.is_open()</c>, <c>scanner_panel.is_open()</c>, <c>inventory_panel.is_open()</c>,
    /// <c>_menus_are_closed()</c>). Null = everything closed / in play.
    /// </summary>
    public interface IRunUiState
    {
        bool RecipePickerOpen { get; }
        bool ScannerOpen { get; }
        bool InventoryOpen { get; }
        bool MenusClosed { get; }
    }

    /// <summary>
    /// Everything a <see cref="RunSession"/> needs from outside Core. Scene ports may be null for headless use: a null
    /// <see cref="Scene"/> means "no player" (every player-dependent branch takes its Godot no-player path), a null
    /// <see cref="ShipHost"/> means no ship can be loaded (the session reports <c>PlayableFailed</c>).
    /// </summary>
    public sealed class RunSessionDeps
    {
        public IStorage Storage = CoreServices.UserStorage;
        public IClock Clock = CoreServices.Clock;
        public ILog Log = CoreServices.Log;

        /// <summary>Stamped into saves as <c>godot_version</c>; defaults to <see cref="CoreServices.Engine"/>.</summary>
        public IEngineInfo Engine = CoreServices.Engine;

        public IRunSceneState Scene;
        public IShipSceneHost ShipHost;
        public IAudioSink AudioSink;
        public ILineOfSightProbe LosProbe;
        public IRunUiState UiState;

        // ---- the @export vars
        public string LayoutPath = RunSession.DEFAULT_LAYOUT_PATH;
        /// <summary>
        /// The home kit document. Empty = resolve from the layout's <c>kit_id</c>; a kit without a complete
        /// <c>modules[].godot_wrapper_scene</c> map falls back to v0 (<see cref="Procgen.ShipGenerator.KitPathForLayout"/>).
        /// The resolved path is what <see cref="IShipSceneHost.LoadHomeShip"/> receives (<see cref="RunSession.KitPath"/>).
        /// </summary>
        public string KitPath = RunSession.DEFAULT_KIT_PATH;
        public string GameplaySlicePath = RunSession.DEFAULT_GAMEPLAY_SLICE_PATH;
        public string BlueprintPath = "res://data/procgen/golden/coherent_ship_001/blueprint.json";
        public string StartingClassId = "engineer";

        /// <summary>
        /// The run difficulty (<c>data/procgen/difficulty/&lt;id&gt;.json</c>). Its dials apply to the HOME ship (threat count
        /// and aggression, hazard seeding, loot quality, ambient intensity); derelicts keep Godot's depth-derived context.
        /// "standard" (all dials 1.0) reproduces the Godot behaviour exactly.
        /// </summary>
        public string DifficultyId = Procgen.DifficultyProfile.STANDARD_ID;

        /// <summary>
        /// The run biome (<c>data/procgen/biomes/&lt;id&gt;.json</c>) for the home ship. "" = Godot's seed-selected loot biome
        /// and no biome dials on the home ship.
        /// </summary>
        public string BiomeId = "";

        /// <summary>The run seed recorded in saves; null = the home blueprint's seed.</summary>
        public long? RunSeed;

        /// <summary>
        /// Unity port tuning: the powered ratio the home ship's emergency cells guarantee its life support while the power
        /// grid cannot allocate to it (a starved grid or a non-operational power dependency). The floor only bridges the
        /// power dependency: a life support system whose own subsystems are broken still starves. At 0.75 the scrubbers
        /// recover 1.5 %/s, which holds exactly one unsealed breach (1.5 %/s leak); every further breach loses ground.
        /// 0.0 is Godot's behaviour, where an idle player on golden <c>coherent_ship_001</c> suffocates at 29.25 s because
        /// the wearing grid allocates nothing to life support.
        /// </summary>
        public double HomeLifeSupportPowerFloor = 0.75;

        /// <summary>An externally injected AchievementState (the build script path); null = the session builds its own.</summary>
        public AchievementState AchievementState;

        /// <summary>The accessibility settings object (<c>AccessibilitySettings</c> in Godot); only its captions/scale are used by Core.</summary>
        public SettingsState SettingsState;
    }
}
