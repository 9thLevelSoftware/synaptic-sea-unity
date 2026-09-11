// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 (the model half of PlayableGeneratedShip).
// Partial-class layout (one concern per file): RunSession.Build (_build_runtime_nodes/_on_ship_loaded), .Tick (_process and
// the _tick_* helpers), .Hazards (oxygen/arc/breach zones), .Fire, .Survival, .Ships (runtime/occupancy/docking),
// .Travel, .Objectives, .Interact (the dispatcher), .Crafting, .Loot, .Combat, .WorkAction, .Audio, .StatusLines, .Save.
using System;
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// The engine-free run coordinator. It owns every pure model <c>PlayableGeneratedShip</c> owned, runs the single
    /// per-frame <see cref="Tick"/> (two explicit stage orders, <see cref="TickOrder"/>), dispatches interaction through
    /// <see cref="InteractionRegistry"/>, and assembles/applies saves (<see cref="RunSnapshotAssembler"/>,
    /// <see cref="WorldSnapshotAssembler"/>). Everything the GDScript did to scene nodes goes through the ports in
    /// <see cref="RunSessionDeps"/> or is raised on <see cref="Events"/>.
    /// </summary>
    public sealed partial class RunSession : CraftingStation.ISurgeryProvider
    {
        // ------------------------------------------------------------------ signals
        /// <summary><c>signal playable_ready(summary)</c>.</summary>
        public event Action<GdDict> PlayableReady;

        /// <summary><c>signal playable_failed(reason)</c>.</summary>
        public event Action<string> PlayableFailed;

        /// <summary><c>signal playable_interaction_completed(interaction_id, objective_id, sequence, objective_type, room_id)</c>.</summary>
        public event Action<string, string, long, string, string> PlayableInteractionCompleted;

        /// <summary><c>signal playable_slice_completed(summary)</c>.</summary>
        public event Action<GdDict> PlayableSliceCompleted;

        /// <summary><c>signal return_to_title_requested</c> (ADR-0043).</summary>
        public event Action ReturnToTitleRequested;

        /// <summary>Scene/HUD/menu side effects (see <see cref="SessionEvents"/>).</summary>
        public readonly SessionEvents Events = new SessionEvents();

        // ------------------------------------------------------------------ constants
        public const string DEFAULT_LAYOUT_PATH = "res://data/procgen/smoke/seed_000017/layout.json";
        public const string DEFAULT_KIT_PATH = "res://data/kits/ship_structural_v0.json";
        public const string DEFAULT_GAMEPLAY_SLICE_PATH = "res://data/procgen/smoke/seed_000017/gameplay_slice.json";
        public const double PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR = 0.55;

        /// <summary>A traveled derelict's scene_root is placed at this world position (co-presence).</summary>
        public static readonly Vec3 DERELICT_DOCK_OFFSET = new Vec3(100.0f, 0.0f, 0.0f);

        public const string BREACH_ZONE_FALLBACK_ID = "corridor_to_reactor";
        public const double BREACH_ZONE_PROXIMITY_RADIUS = 2.4;
        public const string BREACH_ZONE_UNSAFE_LABEL_TEXT = "OXYGEN LOW";
        public const string FIRE_ZONE_FALLBACK_ROOM_ID = "cargo_01";
        public const string ARC_ZONE_FALLBACK_ID = "side_corridor_arc";
        public const string ARC_ZONE_LABEL_TEXT_DISCHARGED = "ARC GROUNDED — CROSS";
        public const string ARC_ZONE_LABEL_TEXT_ARCING = "ARC LIVE — WAIT";
        public const string POWER_GRID_CONFIG_PATH = "res://data/ship_systems/power_budget_tables.json";
        public const string HULL_COMPARTMENTS_CONFIG_PATH = "res://data/ship_systems/hull_compartments.json";
        public const string WEB_INFESTATION_CONFIG_PATH = "res://data/ship_systems/web_infestation.json";
        public const string FACILITY_UPGRADES_CONFIG_PATH = "res://data/ship_systems/facility_upgrades.json";
        public const string HYDROPONICS_CROPS_CONFIG_PATH = "res://data/crops/hydroponics_crops.json";
        public const string SHIP_SUBSYSTEM_TUNING_PATH = "res://data/ship_systems/subsystem_tuning.json";
        public const long SEALED_HATCH_COUNT = 2;

        /// <summary>Objective bridge: which manager subcomponents each objective brings operational.</summary>
        public static readonly GdDict OBJECTIVE_REPAIR_MAP = new GdDict
        {
            { "restore_systems", GdArray.Of(GdArray.Of("power", "power_distribution"), GdArray.Of("power", "battery_cells")) },
            { "download_logs", GdArray.Of(GdArray.Of("navigation", "nav_computer")) },
            { "stabilize_reactor", GdArray.Of(GdArray.Of("power", "reactor_core")) },
        };

        public static readonly GdDict FIRE_COMPARTMENT_SYSTEM = new GdDict
        {
            { "bridge", "navigation" },
            { "engineering", "power" },
            { "hydroponics", "life_support" },
            { "cargo", "" },
        };

        public static GdDict COMPARTMENT_FOR_ROLE => FireCompartmentResolver.COMPARTMENT_FOR_ROLE;

        public const double OXYGEN_MIN_FOR_FIRE = 5.0;
        public const double FIRE_HEALTH_DRAIN_PER_SECOND = 2.0;
        public const double FIRE_SYSTEM_DAMAGE_PER_SECOND = 0.05;
        public const double FIRE_OXYGEN_DRAIN_PER_INTENSITY = 1.5;
        public const double SURGERY_HEALTH_THRESHOLD = 75.0;
        public const double SURGERY_HEAL_AMOUNT = 40.0;

        public static readonly GdArray HATCH_BULKHEAD_LINKS = GdArray.Of(
            GdArray.Of("bridge", "engineering"),
            GdArray.Of("engineering", "cargo"),
            GdArray.Of("engineering", "hydroponics"));

        public const long FIRE_PRESENCE_PERCENT = 15;
        public static readonly IReadOnlyList<string> CRAFTING_STATION_KINDS = new[] { "fabricator", "medbay", "kitchen", "synthesizer", "workbench", "salvage" };
        public const double TOOL_PICKUP_INTERACTION_RADIUS = 1.8;
        public static readonly Vec3 TOOL_PICKUP_FALLBACK_OFFSET = new Vec3(4.0f, 0.0f, 0.0f);
        public const double JUNCTION_CALIBRATOR_INTERACTION_RADIUS = 1.8;
        public static readonly Vec3 JUNCTION_CALIBRATOR_FALLBACK_OFFSET = new Vec3(-4.0f, 0.0f, 0.0f);
        public const string JUNCTION_CALIBRATOR_FALLBACK_ROOM_ID = "galley_01";
        public const string PLAYER_LOCAL_ID = "player_local";
        public const double FOOTSTEP_INTERVAL_WALK = 0.40;
        public const double FOOTSTEP_INTERVAL_CROUCH = 0.55;
        public static readonly IReadOnlyList<string> BANDAGE_ITEM_IDS = new[] { "bandage_kit", "bandage", "field_dressing" };
        public static readonly IReadOnlyList<string> TREAT_ITEM_IDS = new[] { "medkit", "stim_pack", "antibiotic" };
        public const double WORK_ACTION_INTERACT_RANGE = 3.5;

        // ------------------------------------------------------------------ services / ports
        public readonly RunSessionDeps Deps;
        public IStorage Storage => Deps.Storage;
        public IClock Clock => Deps.Clock;
        public ILog Log => Deps.Log;
        public IRunSceneState Scene => Deps.Scene;
        public IShipSceneHost ShipHost => Deps.ShipHost;

        // ------------------------------------------------------------------ the @export vars
        public string LayoutPath;
        public string KitPath;
        public string GameplaySlicePath;
        public string BlueprintPath;
        public string StartingClassId;

        // ------------------------------------------------------------------ run state
        /// <summary>RUNTIME: the home <c>GeneratedShipLoader</c> (null until the ship loads).</summary>
        public IShipLoaderView Loader;

        public long ObjectiveCompletionCount;
        public long CurrentObjectiveSequence = 1;
        public bool SliceComplete;
        public GdDict ReadySummary = new GdDict();
        public bool PlayableStarted;
        public string LastFailureReason = "";

        /// <summary>True while the player is aboard a traveled derelict (not the home complex).</summary>
        public bool AwayFromStart;

        /// <summary>marker_id -> retained ShipInstance (every visited derelict).</summary>
        public readonly OrderedMap<string, ShipInstance> VisitedShips = new OrderedMap<string, ShipInstance>();

        public ShipInstance HomeShip;
        public ShipInstance CurrentShip;

        /// <summary>The ShipInstance the player currently occupies (defaults to home_ship).</summary>
        public ShipInstance CurrentOccupancy;

        /// <summary>Monotonic in-run simulation clock (seconds); advances every tick before any branch.</summary>
        public double WorldTime;

        /// <summary>ADR-0046: accumulated in-run play time (started and not complete).</summary>
        public double RunPlayTimeSeconds;

        public ShipInstance LifeboatShip;
        public ShipInstance PilotedShip;

        /// <summary>Narrative objective flags with no manager backing (supplies/logs), in insertion order.</summary>
        public readonly GdDict CompletedObjectiveTypes = new GdDict();

        /// <summary>Stream E: "&lt;ship&gt;:&lt;room&gt;" keys first touched via objective completion.</summary>
        public readonly GdDict DiscoveredRoomIds = new GdDict();

        public long ThreatsKilledCount;

        /// <summary>REQ-014: sequence -> objective kind ("repair_junction"/"single").</summary>
        public readonly GdDict SequenceKinds = new GdDict();

        // ------------------------------------------------------------------ pure models (every model the coordinator owned)
        public ShipSystemsManager ShipSystemsManager;
        public PlayerProgressionState PlayerProgression;
        public TrainingEventBus TrainingEventBus;
        public SkillTreeState SkillTreeState;
        public HubUpgradeState HubUpgradeState;
        public MetaProgressionState MetaProgressionState;
        public UnlockRegistry UnlockRegistry;
        public UniqueItemState UniqueItemState;
        public RouteControlState RouteControlState;
        public ObjectiveProgressState ObjectiveProgressState;
        public OxygenState OxygenState;
        public InventoryState InventoryState;
        public EquipmentState EquipmentState;
        public ElectricalArcState ElectricalArcState;
        public VitalsState VitalsState;
        public SanityState SanityState;
        public RadiationState RadiationState;
        public BodyTemperatureState BodyTemperatureState;
        public StatusEffectsState StatusEffectsState;
        public SpoilageState SpoilageState;
        public HydroponicsState HydroponicsState;
        public WaterRecyclerState WaterRecyclerState;
        public PowerGridState PowerGridState;
        public LifeSupportState LifeSupportExpandedState;
        public HullIntegrityState HullIntegrityState;
        public WebInfestationState HullWebState;
        public FireSuppressionState FireSuppressionState;
        public ModuleIntegrityMap ModuleIntegrityMap;
        public ExtinguisherState ExtinguisherState;
        public PropulsionState PropulsionExpandedState;
        public SustenanceState SustenanceState;
        public EffectDispatcher EffectDispatcher;
        public ConsumableState ConsumableState;
        public MedicineState MedicineState;
        public StimulantState StimulantState;
        public AddictionState AddictionState;
        public AmmoState AmmoState;
        public UtilityItemResolver UtilityItemState;
        public ThreatRuntime ThreatManager;
        public HallucinationDirector HallucinationDirector;
        public HallucinationRuntime HallucinationManager;
        public SessionAudio AudioManager;
        public CraftingState CraftingState;
        public MaterialState MaterialState;
        public FieldCraftingState FieldCraftingState;
        public DeconstructionResolver DeconstructionResolver;
        public SaveLoadService SaveLoadService;
        public RunSnapshot LastSavedSnapshot;
        public AutosavePolicy AutosavePolicy;
        public LocalizationCatalog LocalizationCatalog;
        public BuildMetadataState BuildMetadataState;
        public DemoScopeGate DemoScopeGate;
        public AchievementState AchievementState;
        public SynapticSeaWorld SynapticSeaWorld;
        public ScannerState ScannerState;
        public TravelController TravelController;
        public FirstRunContract FirstRunContract;
        public ShipGenerator ShipGenerator;
        public WebChartState WebChartState = new WebChartState();
        public WorkActionDriver WorkActionDriver;
        public WoundState WoundState;
        public ShipModificationState ShipModificationState;
        public ComponentPlacementState ComponentPlacementState;
        public ComponentCatalog ComponentCatalog;
        public SeaGraph SeaGraph;
        public PlayerVitalsModel VitalsModel;

        /// <summary>The MenuCoordinator's SettingsState (rebuilt with the HUD, like Godot).</summary>
        public SettingsState SettingsState;

        // ------------------------------------------------------------------ interaction nodes (model halves)
        public readonly List<ObjectiveInteractable> Interactables = new List<ObjectiveInteractable>();

        /// <summary>sequence -> interactables of that sequence (insertion-ordered like the Godot dictionary).</summary>
        public readonly OrderedMap<long, List<ObjectiveInteractable>> SequenceInteractables = new OrderedMap<long, List<ObjectiveInteractable>>();

        public readonly List<ObjectiveInteractable> DerelictInteractables = new List<ObjectiveInteractable>();
        public readonly List<LootContainer> LootContainers = new List<LootContainer>();
        public readonly List<WorkYieldDrop> WorkYieldDrops = new List<WorkYieldDrop>();
        public readonly List<SealedHatch> SealedHatches = new List<SealedHatch>();
        public readonly List<RepairPoint> RepairPoints = new List<RepairPoint>();
        public readonly List<BreachSealPoint> BreachSealPoints = new List<BreachSealPoint>();
        public readonly List<FireSuppressionPoint> FireSuppressionPoints = new List<FireSuppressionPoint>();
        public ExtinguisherRechargePort ExtinguisherRechargePort;
        public readonly List<CraftingStation> CraftingStations = new List<CraftingStation>();
        public readonly List<ProductionStation> ProductionStations = new List<ProductionStation>();
        public readonly List<DockPortBarrier> DockBarriers = new List<DockPortBarrier>();
        public readonly List<BridgeTerminal> BridgeTerminals = new List<BridgeTerminal>();
        public readonly List<HangarBayControl> HangarControls = new List<HangarBayControl>();
        public readonly List<CargoHoldControl> CargoHoldControls = new List<CargoHoldControl>();
        public readonly List<CartControl> CartControls = new List<CartControl>();
        public ToolPickup ToolPickup;
        public ToolPickup JunctionCalibratorPickup;

        /// <summary>CartState currently pushed by the player (or null).</summary>
        public CartState GrabbedCart;

        // ------------------------------------------------------------------ zone nodes (model halves)
        public readonly List<SessionZone> RouteGateNodes = new List<SessionZone>();
        public readonly List<SessionZone> BreachZoneNodes = new List<SessionZone>();
        public readonly List<SessionZone> ArcZoneNodes = new List<SessionZone>();

        /// <summary>compartment_id (or "cid#n") -> fire zone node.</summary>
        public readonly OrderedMap<string, SessionZone> FireZoneNodes = new OrderedMap<string, SessionZone>();

        public bool UnsafeRoomMarkerVisible;
        public string ArcZoneResolvedRoomId = "";
        public readonly List<string> ArcZoneResolvedRoomIds = new List<string>();

        /// <summary>BlockedAffordance_* props hidden by restore_systems (the scene reads it on rebuild).</summary>
        public bool BlockedAffordancesCleared;

        /// <summary>Mounted-component marker records (<c>component_markers</c>).</summary>
        public readonly List<GdDict> ComponentMarkers = new List<GdDict>();

        // ------------------------------------------------------------------ private state
        GdDict _loot_tables = new GdDict();
        readonly GdDict _salvageLootTables = new GdDict();
        readonly List<string> _lootBiomeIdsCache = new List<string>();
        string _lastLootFeedbackLine = "";
        string _lastCaptionLine = "";
        string _lastTooltipFocusSubjectId = "";
        double _captionExpirySeconds;
        Vec3 _homePlayerPosition = Vec3.Zero;
        string _lastWeaponHotbarText = "";
        bool _prevVitalsCritical;
        bool _prevCombatEngaged;
        double _biomatterPulseCooldown;
        double _footstepAcc;
        bool _prevEncumbranceOverloaded;
        bool _isReloading;
        double _autosaveRunSeconds;
        GdDict _lastAutosaveResult = new GdDict();
        long _lastDerelictHazardBudget = -1;
        readonly GdArray _lastDerelictHazardsSeeded = new GdArray();
        string _runId = "";
        GdDict _itemDefs = new GdDict();
        double _hubSlowAcc;
        bool _workRequiresHold;

        /// <summary>The current frame's scene inputs while <see cref="Tick"/> runs (player position etc.).</summary>
        TickContext _frame;
        bool _inTick;

        // ------------------------------------------------------------------ construction
        public RunSession(RunSessionDeps deps)
        {
            Deps = deps ?? new RunSessionDeps();
            LayoutPath = Deps.LayoutPath;
            KitPath = Deps.KitPath;
            GameplaySlicePath = Deps.GameplaySlicePath;
            BlueprintPath = Deps.BlueprintPath;
            StartingClassId = Deps.StartingClassId;
            AchievementState = Deps.AchievementState;
        }

        /// <summary>
        /// <c>_ready()</c>: <c>_build_runtime_nodes()</c>, then the synchronous <c>loader.load_from_paths(...)</c>, whose
        /// <c>ship_loaded</c> runs <c>_on_ship_loaded</c> on the same call stack. Returns the session whether or not the ship
        /// loaded (check <see cref="PlayableStarted"/> / <see cref="LastFailureReason"/>).
        /// </summary>
        public static RunSession Create(RunSessionDeps deps)
        {
            var session = new RunSession(deps);
            session.BuildRuntimeNodes();
            session.LoadFromPaths(session.LayoutPath, session.KitPath, session.GameplaySlicePath);
            return session;
        }

        // ------------------------------------------------------------------ player access helpers
        bool HasPlayer => _inTick ? _frame.HasPlayer : (Scene != null && Scene.HasPlayer);

        /// <summary>The player's world position (the frame's position during a tick, the scene's otherwise).</summary>
        Vec3 PlayerPos
        {
            get
            {
                if (_inTick)
                    return _frame.PlayerPosition;
                return Scene != null && Scene.HasPlayer ? Scene.PlayerPosition : Vec3.Zero;
            }
        }

        void SetPlayerPosition(Vec3 p)
        {
            if (Scene == null || !Scene.HasPlayer)
                return;
            Scene.PlayerPosition = p;
            if (_inTick)
                _frame.PlayerPosition = p;
        }

        void TeleportPlayer(Vec3 p)
        {
            if (Scene == null || !Scene.HasPlayer)
                return;
            Scene.TeleportPlayer(p);
            if (_inTick)
                _frame.PlayerPosition = p;
        }

        bool PlayerMoving => _inTick && _frame.Moving;
        bool PlayerCrouching => _inTick && _frame.Crouching;

        // ------------------------------------------------------------------ small shared helpers
        GdDict LoadJsonDict(string path)
        {
            if (string.IsNullOrEmpty(path) || !CatalogRegistry.Exists(path))
                return new GdDict();
            return CatalogRegistry.LoadDict(path) ?? new GdDict();
        }

        void PlaySfx(string eventId, Vec3? position = null) => AudioManager?.PlaySfx(eventId, position);

        void TriggerTutorial(string trigger, string target) => Events.RaiseTutorialTriggered(trigger, target);

        static bool RootValid(IShipSceneRoot root) => root != null && root.IsValid;

        static bool RootInTree(IShipSceneRoot root) => root != null && root.IsValid && root.IsInsideTree;

        /// <summary><c>scene_root.to_global(local)</c> / <c>global_transform * local</c>.</summary>
        static Vec3 ToGlobal(IShipSceneRoot root, Vec3 local) => RootInTree(root) ? root.GlobalTransform * local : local;

        /// <summary><c>scene_root.to_local(world)</c> / <c>global_transform.affine_inverse() * world</c>.</summary>
        static Vec3 ToLocal(IShipSceneRoot root, Vec3 world) => RootInTree(root) ? SessionMath.AffineInverse(root.GlobalTransform) * world : world;

        /// <summary>Registers a spawned interaction node (Godot <c>add_child</c>).</summary>
        T Spawn<T>(T node) where T : SessionInteractable
        {
            Events.RaiseInteractableSpawned(node);
            return node;
        }

        /// <summary>Frees an interaction node (Godot <c>queue_free</c>).</summary>
        void Despawn(SessionInteractable node)
        {
            if (node == null || !node.IsValid)
                return;
            node.Free();
            Events.RaiseInteractableDespawned(node);
        }

        /// <summary><c>emit_training_event(event_id, target_id)</c>: the resolved record, or null on rejection.</summary>
        public GdDict EmitTrainingEvent(string eventId, string targetId = "")
        {
            if (TrainingEventBus == null || PlayerProgression == null)
                return null;
            return TrainingEventBus.Emit(eventId, targetId, PlayerProgression);
        }

        // ------------------------------------------------------------------ queries (the non-validation getters)
        public GdDict GetPlayableSummary() => new GdDict
        {
            { "loaded", Loader != null && Loader.HasLoadedShip },
            { "player_spawned", Scene != null && Scene.HasPlayer },
            { "camera_spawned", Scene != null && Scene.HasPlayer },
            { "objective_count", (long)Interactables.Count },
            { "objective_sequence_count", (long)SequenceInteractables.Count },
            { "objectives_completed", ObjectiveCompletionCount },
            { "collision_shape_count", Loader != null ? Loader.CountCollisionShapes() : 0L },
            { "start_position", Scene != null && Scene.HasPlayer ? (object)Scene.PlayerPosition : Vec3.Inf },
            { "goal_position", Loader != null ? Loader.GetGoalPosition() : Vec3.Inf },
        };

        public long GetCurrentObjectiveSequence() => CurrentObjectiveSequence;

        public GdDict GetSliceCompletionSummary() => new GdDict
        {
            { "objective_count", (long)Interactables.Count },
            { "objectives_completed", ObjectiveCompletionCount },
            { "current_sequence", CurrentObjectiveSequence },
            { "run_complete", SliceComplete },
            { "play_time_seconds", RunPlayTimeSeconds },
            { "rooms_discovered", (long)DiscoveredRoomIds.Count },
            { "threats_killed", ThreatsKilledCount },
            { "player_spawned", Scene != null && Scene.HasPlayer },
            { "camera_spawned", Scene != null && Scene.HasPlayer },
        };

        public GdDict GetObjectiveProgressSummary() => ObjectiveProgressState == null ? new GdDict() : ObjectiveProgressState.GetSummary();

        public ObjectiveInteractable GetInteractableBySequence(long sequence)
        {
            if (!SequenceInteractables.TryGetValue(sequence, out List<ObjectiveInteractable> group))
                return null;
            foreach (ObjectiveInteractable it in group)
            {
                if (it.IsValid && it.Sequence == sequence)
                    return it;
            }
            return null;
        }

        public List<ObjectiveInteractable> GetInteractablesBySequence(long sequence)
        {
            var output = new List<ObjectiveInteractable>();
            if (SequenceInteractables.TryGetValue(sequence, out List<ObjectiveInteractable> group))
            {
                foreach (ObjectiveInteractable it in group)
                {
                    if (it.IsValid && it.Sequence == sequence)
                        output.Add(it);
                }
            }
            return output;
        }

        public string RunId => _runId;
        public string LastLootFeedbackLine => _lastLootFeedbackLine;
        public string LastCaptionLine => _lastCaptionLine;
        public string FocusedTooltipSubject => _lastTooltipFocusSubjectId;
        public GdDict LastAutosaveResult => _lastAutosaveResult;
        public long LastDerelictHazardBudget => _lastDerelictHazardBudget;
        public GdArray LastDerelictHazardsSeeded => _lastDerelictHazardsSeeded;
        public string LastWeaponHotbarText => _lastWeaponHotbarText;
        public bool WorkRequiresHold { get => _workRequiresHold; set => _workRequiresHold = value; }
    }
}
