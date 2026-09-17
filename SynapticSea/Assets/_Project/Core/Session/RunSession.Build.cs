// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _build_runtime_nodes (1245-1454), the configure
// helpers (1460-1580), _build_hud_layer's model half (6740-6895), _on_ship_loaded (7676-7760), the lifeboat/docking boot
// (7792-7867) and _build_interactables (7893-7951).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary><c>_build_runtime_nodes()</c>: construct and configure every model, in the Godot order (RNG/catalog order matters).</summary>
        void BuildRuntimeNodes()
        {
            // Unity port (C4): the run context (difficulty / biome / seed) is known before any model reads it.
            ApplyRunContext(Deps.DifficultyId, Deps.BiomeId, Deps.RunSeed);
            ShipSystemsManager = new ShipSystemsManager();
            ShipBlueprint bp = LoadBlueprintForSystems();
            ShipSystemsManager.Configure(ShipSystemsManager.LoadDefinitions(), bp.ShipCondition, bp.SeedValue);
            ApplyLifeboatOpeningDamage();
            PlayerProgression = new PlayerProgressionState();
            ConfigurePlayerProgression();
            // REQ-PM-002 / ADR-0033: training event bus (after progression so it can read class multipliers).
            TrainingEventBus = new TrainingEventBus();
            TrainingEventBus.Configure();
            // REQ-PM-003: skill tree + book prereqs.
            SkillTreeState = new SkillTreeState();
            SkillTreeState.Configure(PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            SkillTreeState.LoadPrerequisites();
            // Domain 6 (WI-2): gate live XP so an advanced skill trains only once its skill-tree node is unlocked.
            TrainingEventBus.SkillGate = skillId =>
            {
                if (SkillTreeState == null)
                    return true;
                if (!SkillTreeState.IsGated(skillId))
                    return true;
                return SkillTreeState.IsUnlocked(skillId);
            };
            // REQ-PM-006: cross-run meta state (user://meta_progression.json; missing -> zeroed).
            MetaProgressionState = new MetaProgressionState(Storage, Clock);
            if (!MetaProgressionState.LoadFromDisk())
                MetaProgressionState.ResetAll();
            // REQ-PM-007: hub upgrade catalog.
            HubUpgradeState = new HubUpgradeState();
            HubUpgradeState.Configure();
            // REQ-PM-009: cross-run unlock registry (user://unlock_registry.json).
            UnlockRegistry = new UnlockRegistry(Storage, Clock);
            const string unlockCatalogPath = "res://data/player/unlock_tables.json";
            if (CatalogRegistry.Exists(unlockCatalogPath))
            {
                GdDict unlockParsed = CatalogRegistry.LoadDict(unlockCatalogPath);
                if (unlockParsed != null)
                    UnlockRegistry.Configure(unlockParsed);
                else
                    UnlockRegistry.Configure();
            }
            else
            {
                UnlockRegistry.Configure();
            }
            UnlockRegistry.LoadFromDisk();
            UniqueItemState = new UniqueItemState();
            UniqueItemState.Configure();
            // Re-run progression setup after loading cross-run meta + hub state so persistent bonuses apply.
            ConfigurePlayerProgression();
            RouteControlState = new RouteControlState();
            RouteGateNodes.Clear();
            ObjectiveProgressState = new ObjectiveProgressState();
            // RUNTIME: loader / interaction_root / affordance_root / route_control_root / oxygen_root / tool_pickup_root /
            // arc_root / audio_root / derelict_objective_root / loot_container_root / repair_point_root /
            // crafting_station_root nodes were created here; the Runtime owns them.
            OxygenState = new OxygenState();
            InventoryState = new InventoryState();
            EquipmentState = EquipmentState.Create();
            ElectricalArcState = new ElectricalArcState();
            VitalsState = new VitalsState();
            SanityState = new SanityState();
            RadiationState = new RadiationState();
            BodyTemperatureState = new BodyTemperatureState();
            StatusEffectsState = new StatusEffectsState();
            SpoilageState = new SpoilageState();
            HydroponicsState = new HydroponicsState();
            WaterRecyclerState = new WaterRecyclerState();
            ConfigureExpandedShipSystemModels();
            EffectDispatcher = new EffectDispatcher();
            EffectDispatcher.Configure(new GdDict());
            ConsumableState = new ConsumableState();
            ConsumableState.Configure(new GdDict());
            MedicineState = new MedicineState();
            MedicineState.Configure(new GdDict());
            StimulantState = new StimulantState();
            StimulantState.Configure(new GdDict());
            AddictionState = new AddictionState();
            AddictionState.Configure(new GdDict());
            AmmoState = new AmmoState();
            AmmoState.Configure(new GdDict());
            UtilityItemState = new UtilityItemResolver();
            UtilityItemState.Configure(new GdDict());
            // ThreatManager: add_child runs _ready (catalogs + pipeline configure) before the coordinator hooks callbacks.
            ThreatManager = new ThreatRuntime();
            ThreatManager.DamagePipeline.OnPlayerDamaged = OnPlayerCombatDamaged;
            ThreatManager.OnStructureAttack = OnThreatStructureAttack;
            ThreatManager.ThreatKilled += OnThreatKilled;
            // REQ-AU-001..010: the AudioManager service (models + the Runtime sink).
            AudioManager = new SessionAudio(Deps.AudioSink);
            AudioManager.VoiceLogPlayed += OnVoiceLogPlayed;
            // ADR-0038: crafting / salvage economy models.
            CraftingState = new CraftingState();
            MaterialState = new MaterialState();
            FieldCraftingState = new FieldCraftingState();
            DeconstructionResolver = new DeconstructionResolver();
            _loot_tables = LootRoller.LoadTables();
            // REQ-012: current-run save/load service (constructed before the HUD shell binds it).
            SaveLoadService = new SaveLoadService(Storage, Clock);
            _runId = GenerateRunId();
            SaveLoadService.SetActiveRunId(_runId);
            AutosavePolicy = new AutosavePolicy(Clock);
            LocalizationCatalog = new LocalizationCatalog();
            LocalizationCatalog.Configure(LoadJsonDict("res://data/release/localization_catalog.json"));
            BuildMetadataState = new BuildMetadataState();
            BuildMetadataState.Configure(LoadJsonDict("res://data/release/build_metadata.json"));
            DemoScopeGate = new DemoScopeGate();
            DemoScopeGate.Configure(LoadJsonDict("res://data/release/demo_scope_manifest.json"), BuildMetadataState);
            // RUNTIME: save_load_menu = SaveLoadMenu.new(); bind(save_load_service) is the UI slot presenter (UI layer).
            if (AchievementState == null)
            {
                AchievementState = new AchievementState(Storage, Clock);
                AchievementState.Configure(LoadJsonDict("res://data/release/achievement_catalog.json"));
            }
            BuildHudLayer();
            // Phase 4.5: Synaptic Sea map + scanner + travel, seeded from the starting blueprint.
            ShipBlueprint startBp = LoadBlueprintForSystems();
            SynapticSeaWorld = new SynapticSeaWorld(startBp.SeedValue, Vec3.Zero);
            ScannerState = new ScannerState();
            TravelController = new TravelController();
            ShipGenerator = new ShipGenerator();
            FirstRunContract = new FirstRunContract();
            FirstRunContract.LoadContract();
        }

        /// <summary>
        /// The first away derelict is the only travel the first-run contract may redirect. The marker is regenerated on
        /// every scan, so changing its seed affects this travel request only.
        /// </summary>
        bool ApplyFirstRunContractToMarker(ShipMarker marker)
        {
            if (marker == null || FirstRunContract == null || FirstRunContract.Contract.IsEmpty)
                return false;
            if (VisitedShips.Count > 0 || string.IsNullOrEmpty(marker.MarkerId))
                return false;
            var layoutGenerator = new ShipLayoutGenerator();
            var sliceBuilder = new GameplaySliceBuilder();
            var candidates = new GdDict();
            string biomeId = V.Str(FirstRunContract.Contract.Get("biome_id", ""));
            string difficultyId = V.Str(FirstRunContract.Contract.Get("difficulty_id", ""));
            foreach (object seedVariant in FirstRunContract.Contract.GetArrayOrEmpty("preferred_seeds"))
            {
                long seedValue = V.I64(seedVariant);
                var blueprint = new ShipBlueprint(marker.SizeClass, marker.Condition, seedValue);
                GdDict layout = layoutGenerator.GenerateWithOptions(blueprint, new GdDict(), biomeId, difficultyId, true);
                candidates[seedValue] = new GdDict
                {
                    { "layout", layout },
                    { "gameplay_slice", sliceBuilder.Build(layout) },
                };
            }
            long chosenSeed = FirstRunContract.PickSeed(candidates);
            marker.SeedValue = chosenSeed;
            return true;
        }

        /// <summary><c>_configure_player_progression()</c>: idempotent class + hub-bonus setup.</summary>
        void ConfigurePlayerProgression()
        {
            if (PlayerProgression == null)
                return;
            Dictionary<string, ClassDefinition> classes = ClassDefinition.LoadAll();
            string chosenClass = StartingClassId;
            if (MetaProgressionState != null)
            {
                string sel = MetaProgressionState.GetSelectedClass();
                if (sel.Length > 0 && classes.ContainsKey(sel))
                {
                    bool isUnlockable = classes[sel].Unlockable;
                    if (!isUnlockable || MetaProgressionState.IsClassUnlocked(sel))
                        chosenClass = sel;
                }
            }
            ClassDefinition classDef = classes.TryGetValue(chosenClass, out ClassDefinition c) ? c : (classes.TryGetValue("engineer", out ClassDefinition e) ? e : null);
            if (classDef == null)
                Log.Error("PlayableGeneratedShip: no class definition for '" + chosenClass + "' (fell back from starting_class_id '" + StartingClassId + "') or fallback 'engineer' (data/player/classes.json missing or malformed)");
            PlayerProgression.Configure(classDef, PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            if (HubUpgradeState != null && MetaProgressionState != null)
            {
                GdDict bonuses = HubUpgradeState.ComposeStartingSkillBonuses(MetaProgressionState);
                foreach (object sid in bonuses.Keys)
                {
                    if (PlayerProgression.Skills.Has(sid))
                    {
                        long current = V.I64(PlayerProgression.Skills[sid]);
                        long bonus = V.I64(bonuses[sid]);
                        PlayerProgression.Skills[sid] = GdMath.Clampi(current + bonus, 0, PlayerProgressionState.MAX_SKILL_LEVEL);
                    }
                }
            }
            if (HubUpgradeState != null && MetaProgressionState != null)
            {
                GdDict mults = HubUpgradeState.ComposeXpMultipliers(MetaProgressionState);
                foreach (object cat in mults.Keys)
                {
                    double m = V.F64(mults[cat]);
                    if (m > 1.0)
                    {
                        // PlayerProgressionState._xp_multipliers is keyed by category; the upgrade bonus layers on top.
                        double existing = V.F64(PlayerProgression.XpMultipliers.Get(cat, 1.0));
                        PlayerProgression.XpMultipliers[cat] = existing * m;
                    }
                }
            }
        }

        /// <summary><c>_load_blueprint_for_systems()</c>: the blueprint sidecar, or DAMAGED/MEDIUM/seed 17.</summary>
        ShipBlueprint LoadBlueprintForSystems()
        {
            var fallback = new ShipBlueprint(ShipBlueprint.Size.Medium, ShipBlueprint.Condition.Damaged, 17);
            if (string.IsNullOrEmpty(BlueprintPath) || !CatalogRegistry.Exists(BlueprintPath))
            {
                Log.Warning("PlayableGeneratedShip: blueprint sidecar missing at " + BlueprintPath + "; using DAMAGED/seed=17 default");
                return fallback;
            }
            GdDict parsed = CatalogRegistry.LoadDict(BlueprintPath);
            if (parsed == null)
            {
                Log.Warning("PlayableGeneratedShip: blueprint sidecar malformed at " + BlueprintPath + "; using default");
                return fallback;
            }
            return ShipBlueprint.FromDict(parsed);
        }

        void ConfigureExpandedShipSystemModels()
        {
            PowerGridState = new PowerGridState();
            PowerGridState.Configure(LoadJsonDict(POWER_GRID_CONFIG_PATH));
            HullIntegrityState = new HullIntegrityState();
            HullIntegrityState.Configure(LoadJsonDict(HULL_COMPARTMENTS_CONFIG_PATH));
            HullWebState = new WebInfestationState();
            HullWebState.Configure(LoadJsonDict(WEB_INFESTATION_CONFIG_PATH));
            GdDict tuning = LoadJsonDict(SHIP_SUBSYSTEM_TUNING_PATH);
            LifeSupportExpandedState = new LifeSupportState();
            LifeSupportExpandedState.Configure(tuning.GetDictOrEmpty("life_support"));
            FireSuppressionState = new FireSuppressionState();
            FireSuppressionState.Configure(tuning.GetDictOrEmpty("fire_suppression"));
            ModuleIntegrityMap = new ModuleIntegrityMap();
            ExtinguisherState = new ExtinguisherState();
            ExtinguisherState.Configure(tuning.GetDictOrEmpty("extinguisher"));
            PropulsionExpandedState = new PropulsionState();
            PropulsionExpandedState.Configure(tuning.GetDictOrEmpty("propulsion"));
            SustenanceState = new SustenanceState();
            SustenanceState.Configure(LoadJsonDict(FACILITY_UPGRADES_CONFIG_PATH));
        }

        /// <summary>Seed a freshly generated derelict's per-ship structural models and stamp its sim clock (first visit only).</summary>
        void SeedShipModels(ShipInstance inst)
        {
            if (inst == null)
                return;
            inst.GetHull().Configure(LoadJsonDict(HULL_COMPARTMENTS_CONFIG_PATH));
            inst.GetWeb().Configure(LoadJsonDict(WEB_INFESTATION_CONFIG_PATH));
            inst.LastSimTime = WorldTime;
        }

        /// <summary><c>_generate_run_id()</c>: <c>"%d-%04x" % [ticks_usec, randi() % 0x10000]</c>.</summary>
        string GenerateRunId() =>
            Clock.TicksUsec() + "-" + (GodotGlobalRandom.Randi() % 0x10000).ToString("x4", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// <c>_apply_lifeboat_opening_damage()</c>: every propulsion sub healthy except nav_linkage (DAMAGED_HEALTH), so the
        /// opening blocker is one low-skill repair.
        /// </summary>
        void ApplyLifeboatOpeningDamage()
        {
            if (ShipSystemsManager == null)
                return;
            ShipSystem prop = ShipSystemsManager.GetSystem("propulsion");
            if (prop == null)
                return;
            foreach (ShipSubcomponent sub in prop.Subcomponents)
                sub.Health = 1.0;
            ShipSubcomponent blocker = prop.GetSubcomponent("nav_linkage");
            if (blocker != null)
                blocker.Health = ShipSystemsManager.DAMAGED_HEALTH;
        }

        /// <summary>
        /// <c>_build_hud_layer()</c>, model half. The HUD nodes (tracker, vitals panel, hotbar, scanner, recipe picker, chart,
        /// work HUD, wounds, ship-mod, inventory panels, MenuCoordinator) are the UI layer's; the models they created are
        /// here, in the same order, because the reload path rebuilds them with the HUD.
        /// </summary>
        void BuildHudLayer()
        {
            VitalsModel = new PlayerVitalsModel();
            // PKG-D9a / B2.2b: WorkAction pure driver.
            WorkActionDriver = new WorkActionDriver();
            WorkActionDriver.Configure(new GdDict());
            // PKG-C3.1a / D9d: wounds model. Unity port: one instance for the session's lifetime (reconfigured = the fresh
            // model Godot rebuilt), so the Wounds panel binding survives reloads.
            if (WoundState == null)
                WoundState = new WoundState();
            WoundState.Configure(new GdDict());
            Events.RaiseWoundsChanged(WoundState);
            // PKG-D2.6 / D6.2 / D9b: hub install manifest.
            ShipModificationState = new ShipModificationState();
            ShipModificationState.Configure(new GdDict());
            // PKG-B2.3 / D6.1: component placement for the current ship (home at boot).
            ComponentCatalog = new ComponentCatalog();
            ComponentCatalog.LoadDefault();
            RestoreOrPopulateComponentPlacementForCurrentShip();
            SeaGraph = new SeaGraph();
            long ws = 0;
            if (SynapticSeaWorld != null)
                ws = SynapticSeaWorld.WorldSeed;
            SeaGraph.Configure(new GdDict { { "world_seed", ws } });
            // MenuCoordinator: its SettingsState is rebuilt with the HUD.
            SettingsState = Deps.SettingsState ?? new SettingsState();
            // MenuCoordinator's TutorialState (configured from tutorial_triggers.json; rebuilt with the HUD). Its signals
            // drove the coordinator's cue sfx: triggered/codex -> UI_OBJECTIVE_ADVANCE, dismissed -> UI_PANEL_CLOSE.
            // Unity port (A5): the session owns the ONE in-run TutorialState for its lifetime; the rebuild reconfigures it
            // (Configure resets fired/dismissed/codex exactly like Godot's fresh instance) so UI bindings stay valid.
            if (TutorialState == null)
            {
                TutorialState = new TutorialState();
                TutorialState.Triggered += (id, title, body) =>
                {
                    Events.RaiseTutorialShown(id, title, body);
                    PlaySfx(AudioEventSeam.UI_OBJECTIVE_ADVANCE);
                };
                TutorialState.Dismissed += id => PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                TutorialState.CodexUnlocked += id => PlaySfx(AudioEventSeam.UI_OBJECTIVE_ADVANCE);
            }
            if (!TutorialState.Configure(LoadJsonDict("res://data/ui/tutorial_triggers.json")))
                Log.Warning("PlayableGeneratedShip: MenuCoordinator configure returned false");
            Events.RaiseTutorialStateReset(TutorialState);
            Events.RaiseLoadAvailable(IsLoadAvailable());
            Events.RaiseInventoryItems(InventoryHotbarIds());
            Events.RaiseHotbarSlots(GetConsumableSlotLabels(), 0);
            // RUNTIME: menu_coordinator.open_main_menu() (boot menu; the title handoff dismisses it).
        }

        /// <summary>The synchronous <c>loader.load_from_paths(layout, kit, slice)</c> + its <c>ship_loaded</c> / <c>load_failed</c>.</summary>
        void LoadFromPaths(string layoutPath, string kitPath, string gameplaySlicePath)
        {
            if (ShipHost == null)
            {
                OnLoaderFailed("no_ship_host");
                return;
            }
            // Unity port (A4): the loader receives the kit document actually used (wrapper-map fallback to v0).
            kitPath = ResolveHomeKitPath(layoutPath, kitPath);
            KitPath = kitPath;
            IShipLoaderView view = ShipHost.LoadHomeShip(layoutPath, kitPath, gameplaySlicePath, out string reason);
            if (view == null)
            {
                OnLoaderFailed(string.IsNullOrEmpty(reason) ? "load_failed" : reason);
                return;
            }
            RecordKitPath(view, kitPath);
            Loader = view;
            OnShipLoaded(new GdDict());
        }

        /// <summary><c>_on_ship_loaded(summary)</c>.</summary>
        void OnShipLoaded(GdDict summary)
        {
            if (PlayableStarted)
                return;
            PlayableStarted = true;
            BuildHudLayer();
            SpawnPlayer();
            // RUNTIME: _spawn_camera() — the Runtime binds the rig to the spawned player (no ceiling fade: ceilings are culled in play).
            RefreshUiShellRuntime();
            if (CurrentShip == null)
            {
                CurrentShip = ShipInstance.Create("ship_start", "", LoadBlueprintForSystems(), ShipSystemsManager, Loader);
                HomeShip = CurrentShip;
                HomeShip.BuiltLayout = Loader.GetLayoutCopy();
                SpawnHangarControl(HomeShip);
                SpawnCargoHoldControl(HomeShip);
                SpawnCartControlsForShip(HomeShip);
                CurrentOccupancy = HomeShip;
                BuildLifeboatAtHome();
            }
            ConfigureThreatRuntimeForCurrentShip();
            BuildInteractables();
            BuildSliceAffordanceLabels();
            BuildRouteControlGates();
            RefreshRouteControlFromShipSystems();
            BuildBreachZone(false);
            BuildToolPickup();
            EnsureConsumableHotbarAssignments();
            RefreshConsumableUi();
            BuildJunctionCalibratorPickup();
            RefreshOxygenState(true, 0.0);
            RefreshWeaponHotbar();
            BuildArcZone();
            RestoreArcSummaryForCurrentShip();
            BuildLootContainers();
            BuildSealedHatches();
            BuildRepairPoints();
            BuildBreachSealPoints();
            SeedFiresFromDamage();
            BuildFireZones();
            BuildHallucinationRuntime();
            BuildFireSuppressionPoints();
            BuildExtinguisherRechargePort();
            BuildCraftingStations();
            BuildProductionStations();
            Events.RaiseTrackerObjectivesSet(Loader.GetObjectiveSpecsCopy());
            CurrentObjectiveSequence = 1;
            SliceComplete = false;
            ActivateCurrentObjective();
            ReadySummary = (summary ?? new GdDict()).DeepCopy();
            ReadySummary["player_spawned"] = HasPlayer;
            ReadySummary["camera_spawned"] = HasPlayer;
            ReadySummary["collision_shape_count"] = Loader.CountCollisionShapes();
            ReadySummary["playable_interactable_count"] = (long)Interactables.Count;
            Log.Info("PLAYABLE SHIP READY player_spawned=" + (HasPlayer ? "true" : "false") + " camera_spawned=" + (HasPlayer ? "true" : "false")
                     + " objectives=" + Interactables.Count + " collision_shapes=" + Loader.CountCollisionShapes());
            PlayableReady?.Invoke(GetPlayableSummary());
        }

        void OnLoaderFailed(string reason)
        {
            LastFailureReason = reason;
            Log.Error("PLAYABLE SHIP FAIL reason=" + reason);
            PlayableFailed?.Invoke(reason);
        }

        /// <summary><c>_spawn_player()</c>: at the loader start transform + spawn height.</summary>
        void SpawnPlayer()
        {
            if (Scene == null)
                return;
            Vec3 origin = Loader.GetStartTransform().Origin;
            Scene.SpawnPlayer(origin + new Vec3(0.0f, (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0.0f));
        }

        /// <summary><c>_break_ship_instance_cycles()</c> (NOTIFICATION_PREDELETE): sever dock back-pointers.</summary>
        public void Dispose()
        {
            if (LifeboatShip != null)
            {
                LifeboatShip.ParentShip = null;
                LifeboatShip.DockedShips.Clear();
            }
            HomeShip?.DockedShips.Clear();
            foreach (ShipInstance inst in VisitedShips.Values)
            {
                if (inst != null)
                {
                    inst.ParentShip = null;
                    inst.DockedShips.Clear();
                }
            }
        }

        /// <summary><c>_build_lifeboat_at_home()</c>: build the lifeboat, port-dock it to home, spawn its controls.</summary>
        void BuildLifeboatAtHome()
        {
            if (LifeboatShip != null && RootValid(LifeboatShip.SceneRoot))
            {
                ShipHost?.FreeShipRoot(LifeboatShip.SceneRoot);
                LifeboatShip = null;
            }
            // Skin the lifeboat's modules by the run's deterministic biome; the floorplan is fixed.
            LifeBoatBuilder.BuildResult built = LifeBoatBuilder.Build(ResolveCurrentLootBiomeId());
            IShipSceneRoot lbRoot = built != null ? ShipHost?.BuildLifeboatScene(built) : null;
            if (lbRoot == null)
            {
                Log.Error("PlayableGeneratedShip: LifeBoatBuilder.build() returned null; lifeboat not created");
                return;
            }
            RecordKitPath(lbRoot, built.KitPath);
            LifeboatShip = ShipInstance.Create("lifeboat", "", null, ShipSystemsManager, lbRoot);
            LifeboatShip.BuiltLayout = LifeBoatBuilder.BuildLayout();
            LifeboatShip.GetAccess().Claim(PLAYER_LOCAL_ID);
            ShipHost.AttachShipRoot(lbRoot);
            PilotedShip = LifeboatShip;
            GdDict dockResult = DockPilotedTo(HomeShip);
            if (!dockResult.GetBool("success"))
                Log.Error("PlayableGeneratedShip: boot dock failed — reason=" + V.Str(dockResult.Get("reason", "?")));
            SpawnDockBarrier(HomeShip);
            SpawnBridgeTerminal(LifeboatShip);
            SpawnHangarControl(LifeboatShip);
            SpawnCargoHoldControl(LifeboatShip);
            SpawnCartControlsForShip(LifeboatShip);
        }

        /// <summary><c>_piloted_port_local()</c>: the piloted ship's OWN dock port (ship-local).</summary>
        GdDict PilotedPortLocal()
        {
            if (PilotedShip == null)
                return new GdDict();
            if (PilotedShip == LifeboatShip)
                return DockPorts.ForLifeboat(PilotedShip.BuiltLayout);
            return DockPorts.ForDerelict(PilotedShip.BuiltLayout, ShipSeed(PilotedShip), ShipConditionClass(PilotedShip));
        }

        /// <summary><c>_dock_piloted_to(host)</c>.</summary>
        GdDict DockPilotedTo(ShipInstance host)
        {
            if (PilotedShip == null || host == null || host.SceneRoot == null)
                return new GdDict { { "success", false }, { "reason", "dock_failed" } };
            long cc = host == HomeShip ? 0 : ShipConditionClass(host);
            GdDict hostLocal = DockPorts.ForDerelict(host.BuiltLayout, ShipSeed(host), cc);
            GdDict hostWorld = DockingManager.HostPortToWorld(host, hostLocal);
            GdDict mobileLocal = PilotedPortLocal();
            if (!DockPorts.PortsCompatible(hostWorld, mobileLocal))
                return new GdDict { { "success", false }, { "reason", "dock_incompatible" } };
            return DockingManager.Dock(host, PilotedShip, hostWorld, mobileLocal);
        }

        static long ShipSeed(ShipInstance inst) => inst != null && inst.Blueprint != null ? inst.Blueprint.SeedValue : 0;

        static long ShipConditionClass(ShipInstance inst) => inst != null && inst.Blueprint != null ? inst.Blueprint.ShipCondition : 0;

        /// <summary><c>_build_interactables()</c>: the home objective interactables from the loader specs.</summary>
        void BuildInteractables()
        {
            foreach (ObjectiveInteractable it in Interactables)
                Despawn(it);
            Interactables.Clear();
            SequenceInteractables.Clear();
            SequenceKinds.Clear();
            ObjectiveProgressState?.Reset();
            foreach (object objectiveObj in Loader.GetObjectiveSpecsCopy())
            {
                if (!(objectiveObj is GdDict objective))
                    continue;
                long sequence = V.I64(objective.Get("sequence", 0L));
                string kind = V.Str(objective.Get("kind", "single"));
                SequenceKinds[sequence] = kind;
                GdArray steps = objective.Get("steps", new GdArray()) as GdArray ?? new GdArray();
                if (kind == "repair_junction" && steps.Count > 1)
                {
                    long requiredSteps = steps.Count;
                    string objectiveType = V.Str(objective.Get("type", "unknown"));
                    ObjectiveProgressState?.RegisterObjective(sequence, objectiveType, requiredSteps);
                    foreach (object stepObj in steps)
                    {
                        if (!(stepObj is GdDict step))
                            continue;
                        if (!(step.Get("position", Vec3.Inf) is Vec3 stepPos))
                            continue;
                        var interactable = new ObjectiveInteractable();
                        interactable.ConfigureFromStep(objective, step, stepPos, 1.8);
                        interactable.InteractionCompleted += OnInteractableCompleted;
                        Interactables.Add(Spawn(interactable));
                        AddInteractableToSequence(sequence, interactable);
                    }
                }
                else
                {
                    if (!(objective.Get("position", Vec3.Inf) is Vec3 pos))
                        continue;
                    var interactable = new ObjectiveInteractable();
                    interactable.ConfigureFromObjective(objective, pos, 1.8);
                    interactable.InteractionCompleted += OnInteractableCompleted;
                    Interactables.Add(Spawn(interactable));
                    AddInteractableToSequence(sequence, interactable);
                }
            }
        }

        void AddInteractableToSequence(long sequence, ObjectiveInteractable interactable)
        {
            if (!SequenceInteractables.ContainsKey(sequence))
                SequenceInteractables[sequence] = new List<ObjectiveInteractable>();
            SequenceInteractables[sequence].Add(interactable);
        }

        /// <summary>
        /// <c>_build_slice_affordance_labels()</c> + props: purely presentational (labels, readability props, route cues);
        /// the Runtime rebuilds them from the loader. Core only resets the restore_systems "blocked cleared" flag, which
        /// the rebuild made visible again.
        /// </summary>
        void BuildSliceAffordanceLabels()
        {
            BlockedAffordancesCleared = false;
            Events.RaiseAffordancesRebuilt();
        }

        /// <summary><c>_build_route_control_gates()</c>: one powered gate per blocked-route node.</summary>
        void BuildRouteControlGates()
        {
            foreach (SessionZone gate in RouteGateNodes)
                Events.RaiseZoneDespawned(gate);
            RouteGateNodes.Clear();
            var gateIds = new GdArray();
            if (Loader == null)
            {
                RouteControlState?.ConfigureFromBlockedRoutes(gateIds);
                return;
            }
            int index = 0;
            foreach (Vec3 local in Loader.GetBlockedRoutePositions())
            {
                index += 1;
                string gateId = "powered_route_gate_" + index.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
                var gate = new SessionZone
                {
                    Kind = "route_gate",
                    ZoneId = gateId,
                    NodeName = "RouteGate_" + index.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "_PoweredBlocker",
                    LocalPosition = ToGlobal(Loader, local),
                    CollisionEnabled = true,
                    VisualState = "closed",
                };
                gate.Meta["required_system"] = "main_power_restored";
                RouteGateNodes.Add(gate);
                Events.RaiseZoneSpawned(gate);
                gateIds.Add(gateId);
            }
            RouteControlState?.ConfigureFromBlockedRoutes(gateIds);
            ApplyRouteGateSceneState();
        }

        /// <summary><c>_activate_current_objective()</c>.</summary>
        void ActivateCurrentObjective()
        {
            foreach (ObjectiveInteractable it in Interactables)
                it.SetActive(it.Sequence == CurrentObjectiveSequence);
            Events.RaiseTrackerCurrentSequence(CurrentObjectiveSequence);
            GdDict progress = ObjectiveProgressState != null ? ObjectiveProgressState.GetStepProgress(CurrentObjectiveSequence) : new GdDict();
            Events.RaiseTrackerStepProgress(CurrentObjectiveSequence, progress);
            ObjectiveInteractable current = GetInteractableBySequence(CurrentObjectiveSequence);
            if (current != null)
                Events.RaiseTrackerInteractionPrompt(current.PromptText);
        }
    }
}
