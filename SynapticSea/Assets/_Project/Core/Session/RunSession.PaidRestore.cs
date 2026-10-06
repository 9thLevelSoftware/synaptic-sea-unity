using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        PaidRestoreOperation _paidRestoreOperation;

        // Only the owning session retains this token. Public snapshot APIs never obtain it or bypass admission.
        internal sealed class PaidRestoreOperation
        {
            internal Dictionary<string, IShipSceneRoot> ActiveRoots;
            internal readonly Dictionary<string, IShipSceneRoot> SelectedRoots = new Dictionary<string, IShipSceneRoot>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IShipSceneRoot> BeforeRoots = new Dictionary<string, IShipSceneRoot>(StringComparer.Ordinal);
            internal readonly HashSet<string> TerminalRuns = new HashSet<string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, GdDict> TerminalResults = new Dictionary<string, GdDict>(StringComparer.Ordinal);
            internal IShipLoaderView HomeToInstall;
            internal GdDict Selection;
            internal GdDict TerminalMeta;
            internal bool RollingBack, Destructive, Committed, ManualViewsFailed;
            internal readonly RunSession Owner;
            internal PaidRestoreOperation(RunSession owner) { Owner = owner; }
        }

        internal void RequirePaidRestoreOperation(PaidRestoreOperation operation)
        {
            if (operation == null || !ReferenceEquals(_paidRestoreOperation, operation) || operation.Owner != this ||
                !ComponentGenerationRestoreInProgress || !PaidCraftingEnabled)
                throw new InvalidOperationException("restore_operation_required");
        }

        sealed class PaidRestoreContext
        {
            internal string RunId;
            internal ShipInstance Home;
            internal IShipSceneRoot HomeRoot;
            internal CraftingState Crafting;
            internal List<CraftingStation> Stations;
            internal Dictionary<string, StationState> Models;
            internal Dictionary<string, ShipInstance> Ships;
            internal string StationOwner(string kind) => kind == "field_crafting" ? PLAYER_LOCAL_ID : Home?.ShipId ?? "";
            internal string StationId(string kind) => kind == "field_crafting" ? "portable:" + RunId + ":" + PLAYER_LOCAL_ID : "station:" + RunId + ":" + StationOwner(kind) + ":" + kind;
            internal bool StationExists(string kind) => kind == "field_crafting" || Home?.SceneRoot?.IsValid == true &&
                Crafting?.GetStation(kind) != null && Stations.Any(st => st.IsValid && st.StationKind == kind &&
                    ReferenceEquals(st.Parent, Home.SceneRoot) && ReferenceEquals(st.CraftingState, Crafting));
        }
        PaidRestoreContext _paidRestoreFinalContext;

        PaidRestoreContext CurrentPaidRestoreContext() => new PaidRestoreContext
        {
            RunId = RunId, Home = HomeShip, HomeRoot = HomeShip?.SceneRoot, Crafting = CraftingState, Stations = new List<CraftingStation>(CraftingStations),
            Models = CRAFTING_STATION_KINDS.ToDictionary(k => k, k => CraftingState?.GetStation(k), StringComparer.Ordinal),
            Ships = AllKnownShips().Where(s => s != null).GroupBy(s => s.ShipId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
        };

        PaidRestoreContext PreparePaidRestoreContext(string run, IShipLoaderView loader, WorldSnapshot world,
            Dictionary<string, IShipSceneRoot> roots, GdDict domain, GdDict payload)
        {
            GdDict finiteHome = world.HomeShip.GetDictOrEmpty("home_finite_loot");
            if (world.HomeShip.Has("home_finite_loot") && !(world.HomeShip.Get("home_finite_loot") is GdDict) ||
                !ManualStudyEnabled && !finiteHome.IsEmpty || !FiniteLootState.ValidateSources(finiteHome, "ship_start", loader.GetLootContainerSpecsCopy(), out _))
                throw new InvalidOperationException("invalid_home_finite_loot");
            if (domain.GetInt("schema_version") == 5) throw new InvalidOperationException("auxiliary_source_mismatch");
            var crafting = new CraftingState();
            var materials = new MaterialState(); var inventory = new InventoryState();
            var deconstruction = new DeconstructionResolver(); var progression = new PlayerProgressionState();
            var homeSystems = new ShipSystemsManager(); homeSystems.Configure(homeSystems.LoadDefinitions(), 0, 0);
            var home = ShipInstance.Create("ship_start", "", new ShipBlueprint(), homeSystems, loader);
            home.BuiltLayout = loader.GetLayoutCopy();
            var context = new PaidRestoreContext { RunId = run, Home = home, HomeRoot = loader, Crafting = crafting,
                Stations = new List<CraftingStation>(), Models = new Dictionary<string, StationState>(StringComparer.Ordinal),
                Ships = new Dictionary<string, ShipInstance>(StringComparer.Ordinal) { { "ship_start", home } } };
            // Prospective occupancy belongs to the selected loader, never the old session's _liveNodes.
            var occupied = new List<StationPlacer.Occupied> { new StationPlacer.Occupied(
                loader.GetStartTransform().Origin + new Vec3(0, PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0), STATION_INTERACTION_RADIUS) };
            var positions = loader.StructureRoomLocalPositions().Select(p => p + new Vec3(0, PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0)).ToList();
            if (positions.Count == 0)
                for (int i = 0; i < CRAFTING_STATION_KINDS.Count; i++) positions.Add(new Vec3(i * 2.0, PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0));
            foreach (StationPlacer.Placement placement in PlaceCraftingServices(home.BuiltLayout, CRAFTING_STATION_KINDS, occupied, positions))
            {
                CraftingStation station = ConfigureCraftingStation(loader, crafting, materials, inventory, deconstruction, progression, placement, true);
                context.Stations.Add(station); context.Models[station.StationKind] = crafting.GetStation(station.StationKind);
            }
            var archive = payload.GetArrayOrEmpty("artifacts").OfType<GdDict>().ToDictionary(a => a.GetString("logical_path"), StringComparer.Ordinal);
            GdDict references = payload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references");
            foreach (var pair in world.VisitedShips)
            {
                var ship = ShipInstance.Create("", "", new ShipBlueprint(), null, null);
                if (!(pair.Value is GdDict row) || !ship.ApplySummary(row) || ship.MarkerId != V.Str(pair.Key))
                    throw new InvalidOperationException("invalid_retained_ship");
                if (roots.TryGetValue(ship.ShipId, out IShipSceneRoot root)) ship.SceneRoot = root;
                ship.BuiltLayout = ParseGenerationDocument(archive[references.GetDictOrEmpty(ship.ShipId).GetString("layout_path")]);
                ValidatePaidFiniteStock(ship);
                context.Ships[ship.ShipId] = ship;
            }
            var boatSystems = new ShipSystemsManager(); boatSystems.Configure(boatSystems.LoadDefinitions(), 0, 0);
            var boat = ShipInstance.Create("lifeboat", "", null, boatSystems, roots["lifeboat"]);
            if (!world.MobileHomeState.GetDictOrEmpty("lifeboat").IsEmpty) boat.ApplySummary(world.MobileHomeState.GetDictOrEmpty("lifeboat"));
            boat.BuiltLayout = ParseGenerationDocument(archive[references.GetDictOrEmpty("lifeboat").GetString("layout_path")]);
            ValidatePaidFiniteStock(boat);
            context.Ships["lifeboat"] = boat;
            ValidatePaidRestoreTargets(context, domain);
            return context;
        }

        void ValidatePaidFiniteStock(ShipInstance ship)
        {
            GdDict stock = ship.FiniteLootSummary;
            if (!ManualStudyEnabled && !stock.IsEmpty) throw new InvalidOperationException("finite_loot_inactive");
            if (ship.SceneRoot is IShipLoaderView root)
            {
                if (!FiniteLootState.ValidateSources(stock, ship.ShipId, root.GetLootContainerSpecsCopy(), out _)) throw new InvalidOperationException("finite_loot_source_mismatch");
            }
            else if (!stock.IsEmpty) throw new InvalidOperationException("finite_loot_source_missing");
        }

        void ValidatePaidRestoreTargets(PaidRestoreContext context, GdDict domain)
        {
            if (!ComponentIntegrationEnabled) return;
            foreach (GdDict machine in domain.GetDictOrEmpty("machinery").Values.OfType<GdDict>())
                if (!context.Ships.TryGetValue(machine.GetString("owner_id"), out ShipInstance ship) ||
                    ship.SystemsManager?.GetSystem(machine.GetString("system_id"))?.GetSubcomponent(machine.GetString("subcomponent_id")) == null)
                    throw new InvalidOperationException("component_machinery_owner_missing");
            foreach (object owner in domain.GetArrayOrEmpty("registered_owners"))
            {
                string id = V.Str(owner);
                if (!context.Ships.TryGetValue(id, out ShipInstance ship)) throw new InvalidOperationException("component_owner_missing");
                GdDict layout = ship.SceneRoot is IShipLoaderView view ? view.GetLayoutCopy() : ship.BuiltLayout;
                if (!ValidateComponentPhysicalLayout(domain, id, layout, out string reason)) throw new InvalidOperationException(reason);
            }
        }

        void CheckPaidRestoreAuthority(PaidRestoreOperation operation)
        {
            RequirePaidRestoreOperation(operation);
            ObservePaidRestoreTerminal(operation);
            if (ComponentTerminalPending || SliceComplete || operation.TerminalRuns.Count > 0)
                throw new InvalidOperationException("terminal_pending");
            GdDict selection = operation.Selection;
            GdDict exact = SaveLoadService.ReadGeneration(selection.GetString("run_id"), selection.GetString("slot_id"),
                selection.GetString("generation_id"), selection.GetString("manifest_sha256"));
            if (!exact.GetBool("ok"))
            {
                string reason = exact.GetString("reason");
                if (reason == "run_terminal" || reason == "legacy_death")
                    operation.TerminalRuns.Add(selection.GetString("run_id"));
                operation.TerminalResults[selection.GetString("run_id")] = exact.DeepCopy();
                throw new InvalidOperationException(reason);
            }
            if (!PaidSnapshotCodec.Same(selection.Get("payloads"), exact.Get("payloads"))) throw new InvalidOperationException("selection_mismatch");
        }

        void ObservePaidRestoreTerminal(PaidRestoreOperation operation)
        {
            if (ComponentTerminalPending || SliceComplete)
            {
                operation.TerminalRuns.Add(_runId);
                operation.TerminalResults[_runId] = LastSaveResult.DeepCopy();
                if (!operation.RollingBack) operation.TerminalMeta = MetaProgressionState?.ToDict().DeepCopy();
            }
        }

        bool ReadRestoreRunLive(string run, string slot, out GdDict evidence)
        {
            // ReadCommitParent performs the existing complete RequireLive check BEFORE consulting the slot pointer.
            // A missing or differently owned pointer therefore does not erase independently established run authority.
            evidence = SaveLoadService.ReadComponentCommitParent(run, slot);
            string reason = evidence.GetString("reason");
            return evidence.GetBool("ok") || reason == "not_found" || reason == "slot_owner_conflict";
        }

        bool CheckCommittedPaidRestore(PaidRestoreOperation operation, out GdDict evidence)
        {
            ObservePaidRestoreTerminal(operation);
            bool live = ReadRestoreRunLive(_runId, operation.Selection.GetString("slot_id"), out evidence);
            if (evidence.GetString("reason") == "run_terminal" || evidence.GetString("reason") == "legacy_death")
                operation.TerminalRuns.Add(_runId);
            if (operation.TerminalRuns.Contains(_runId))
            {
                ComponentTerminalPending = true; SliceComplete = true;
                operation.TerminalResults[_runId] = evidence.DeepCopy();
                return false;
            }
            if (!live) { PlayableStarted = false; operation.TerminalResults[_runId] = evidence.DeepCopy(); }
            return live;
        }

        void ValidateFinalPaidRestore(GdDict candidate)
        {
            PaidRestoreOperation operation = _paidRestoreOperation;
            RequirePaidRestoreOperation(operation);
            if (operation.RollingBack) throw new InvalidOperationException("rollback_publication_forbidden");
            PaidRestoreContext context = _paidRestoreFinalContext;
            if (context == null || RunId != context.RunId || SaveLoadService.GetActiveRunId() != context.RunId ||
                !ReferenceEquals(HomeShip, context.Home) || !ReferenceEquals(HomeShip?.SceneRoot, context.HomeRoot) || context.HomeRoot?.IsValid != true || !ReferenceEquals(CraftingState, context.Crafting) ||
                context.Stations.Count != CraftingStations.Count || context.Stations.Any(st => !CraftingStations.Contains(st)) ||
                context.Stations.Any(st => !st.IsValid || !ReferenceEquals(st.Parent, context.HomeRoot) || !ReferenceEquals(st.CraftingState, context.Crafting)) ||
                context.Ships.Any(pair => !ReferenceEquals(pair.Value, FindShipByIdInternal(pair.Key))) ||
                context.Models.Any(pair => !ReferenceEquals(context.Crafting.GetStation(pair.Key), pair.Value)))
                throw new InvalidOperationException("stale_restore_context");
            if (!ValidatePaidCraftingRestoreInContext(candidate, context, out string reason)) throw new InvalidOperationException(reason);
            ValidatePaidRestoreTargets(context, candidate);
            CheckPaidRestoreAuthority(operation);
        }

        void LoadPreparedHome(PaidRestoreOperation operation, string layout, string kit, string slice)
        {
            RequirePaidRestoreOperation(operation);
            IShipLoaderView loader = operation.HomeToInstall;
            if (loader?.IsValid != true) throw new InvalidOperationException("prepared_home_missing");
            RecordKitPath(loader, kit); RememberHomeGenerationDocuments(loader, layout, kit, slice);
            Loader = loader; OnShipLoaded(new GdDict());
        }

        static ShipDocuments PaidHomeDocuments(GdDict payload)
        {
            GdDict r = payload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty("ship_start");
            var archive = payload.GetArrayOrEmpty("artifacts").OfType<GdDict>().ToDictionary(a => a.GetString("logical_path"), StringComparer.Ordinal);
            return new ShipDocuments { Layout = ParseGenerationDocument(archive[r.GetString("layout_path")]),
                Kit = ParseGenerationDocument(archive[r.GetString("kit_path")]), KitPath = r.GetString("kit_path"),
                GameplaySlice = ParseGenerationDocument(archive[r.GetString("gameplay_slice_path")]),
                LayoutJson = archive[r.GetString("layout_path")].GetString("text"), GameplaySliceJson = archive[r.GetString("gameplay_slice_path")].GetString("text"),
                IsAway = false, Name = "GeneratedShipLoader" };
        }

        sealed class PaidRestoreBefore
        {
            internal DomainTransactionCoordinator Coordinator;
            internal GdDict Owner, Participants, Crafting, Field, Knowledge, Multipliers, Selection, Ready, Autosave, Work;
            internal Dictionary<IShipSceneRoot, GdDict> RootDocuments;
            internal Dictionary<string, GdDict> ShipDocuments;
            internal Dictionary<string, GdDict> ShipCaches;
            internal WorldSnapshot World;
            internal GdDict Documents;
            internal IResourceReader Reader;
            internal IShipLoaderView HomeLoader;
            internal string Run, ServiceRun, Blueprint, Layout, Kit, Slice;
            internal long CaptureRevision;
            internal double AutosaveSeconds;
            internal string OpenHolder;
            internal TickContext Frame;
            internal string WorkLocation, WorkShipId, WorkRoom;
            internal bool Held, Awaiting, RequiresHold, Reloading, Complete, Playable;
            internal Vec3 HomePosition, PlayerPosition;
            internal RunSnapshot LastSaved;
        }

        PaidRestoreBefore CapturePaidRestoreBefore(PaidRestoreOperation operation)
        {
            RequirePaidRestoreOperation(operation);
            if (_componentDomain == null || !PaidCraftingState.IsDomainVersion(_componentDomain.SchemaVersion)) throw new InvalidOperationException("paid_owner_missing");
            var before = new PaidRestoreBefore
            {
                Coordinator = _componentDomain, Owner = _componentDomain.GetSummary(),
                CaptureRevision = _captureRevision, HomeLoader = Loader,
                RootDocuments = _generationRootDocuments.ToDictionary(p => p.Key, p => p.Value.DeepCopy()),
                ShipDocuments = _generationShipDocuments.ToDictionary(p => p.Key, p => p.Value.DeepCopy(), StringComparer.Ordinal),
                Selection = _selectedGeneration?.DeepCopy(), Run = _runId, ServiceRun = SaveLoadService.GetActiveRunId(),
                Blueprint = BlueprintPath, Layout = LayoutPath, Kit = KitPath, Slice = GameplaySlicePath,
                Crafting = CraftingState.GetSummary().DeepCopy(), Field = FieldCraftingState.GetSummary().DeepCopy(),
                Knowledge = RecipeKnowledge.GetSummary().DeepCopy(), Multipliers = PlayerProgression.XpMultipliers.DeepCopy(),
                Held = _workHoldInput, Awaiting = _workAwaitingResume, RequiresHold = _workRequiresHold,
                AutosaveSeconds = _autosaveRunSeconds, Autosave = _lastAutosaveResult.DeepCopy(), OpenHolder = _componentOpenHolder,
                Frame = _frame, Work = WorkActionDriver?.Work?.GetSummary().DeepCopy(), WorkLocation = _restoredWorkLocation,
                WorkShipId = _workSiteShipId, WorkRoom = _workSiteRoomCenterId,
                Reloading = _isReloading, Complete = SliceComplete, Playable = PlayableStarted,
                HomePosition = HomePlayerPosition, PlayerPosition = Scene.PlayerPosition,
                LastSaved = LastSavedSnapshot, Ready = ReadySummary.DeepCopy(),
                ShipCaches = AllKnownShips().Where(s => s != null).GroupBy(s => s.ShipId).ToDictionary(g => g.Key, g => g.First().GetSummary().DeepCopy(), StringComparer.Ordinal)
            };
            // This read mutates only its supplied detached paid state. The committed owner is never refreshed.
            before.Participants = ReadComponentParticipants(PaidState(before.Owner).DeepCopy()).DeepCopy();
            before.World = SavePayloadAssembler.CaptureBeforeWorld(this, operation, out GdDict documents);
            before.Documents = documents;
            var privateReader = new GdDict { { "ok", true }, { "payloads", documents }, { "payloads_sha256", SaveGenerationArtifacts.Hash(GdJson.Stringify(documents)) } };
            if (!SaveGenerationArtifacts.TryCreateReader(privateReader, CoreServices.Resources, out before.Reader, out string reason))
                throw new InvalidOperationException(reason);
            if (!ReferenceEquals(_componentDomain, before.Coordinator) || !PaidSnapshotCodec.Same(before.Owner, _componentDomain.GetSummary()))
                throw new InvalidOperationException("before_owner_changed");
            return before;
        }

        void RestorePaidBookkeeping(PaidRestoreBefore before)
        {
            _captureRevision = before.CaptureRevision;
            _generationRootDocuments.Clear(); foreach (var pair in before.RootDocuments) _generationRootDocuments[pair.Key] = pair.Value.DeepCopy();
            _generationShipDocuments.Clear(); foreach (var pair in before.ShipDocuments) _generationShipDocuments[pair.Key] = pair.Value.DeepCopy();
            _selectedGeneration = before.Selection;
            _runId = before.Run; SaveLoadService.SetActiveRunId(before.ServiceRun);
            BlueprintPath = before.Blueprint; LayoutPath = before.Layout; KitPath = before.Kit; GameplaySlicePath = before.Slice;
            HomePlayerPosition = before.HomePosition; LastSavedSnapshot = before.LastSaved; ReadySummary = before.Ready;
            _isReloading = before.Reloading;
            _autosaveRunSeconds = before.AutosaveSeconds; _lastAutosaveResult = before.Autosave; _componentOpenHolder = before.OpenHolder;
        }

        void RestorePaidRawBefore(PaidRestoreBefore before)
        {
            ApplyComponentParticipants(before.Participants, before.Multipliers);
            CraftingState.ApplyOwnedSummary(before.Crafting); FieldCraftingState.ApplyOwnedSummary(before.Field);
            RecipeKnowledge.ApplySummary(before.Knowledge);
            _componentDomain = before.Coordinator;
            if (ComponentIntegrationEnabled) BindComponentReadViews();
            BindPaidCraftingModels();
            foreach (var pair in before.ShipCaches)
            {
                ShipInstance ship = FindShipByIdInternal(pair.Key); if (ship == null) continue;
                GdDict raw = pair.Value;
                ship.CombatSummary = raw.GetDictOrEmpty("combat").DeepCopy(); ship.ArcSummary = raw.GetDictOrEmpty("arc").DeepCopy();
                ship.BreachEnvironmentSummary = raw.GetDictOrEmpty("breach_environment").DeepCopy();
                ship.ModuleIntegritySummary = raw.GetDictOrEmpty("module_integrity").DeepCopy();
                ship.ComponentPlacementSummary = raw.GetDictOrEmpty("component_placement").DeepCopy();
            }
            _workHoldInput = before.Held; _workAwaitingResume = before.Awaiting; _workRequiresHold = before.RequiresHold;
            _frame = before.Frame; _restoredWorkLocation = before.WorkLocation;
            if (WorkActionDriver != null)
            {
                GdDict work = before.Work;
                WorkActionDriver.Work = work == null ? null : new WorkActionState { ActionId = work.GetString("action_id"),
                    Definition = work.GetDictOrEmpty("definition").DeepCopy(), Status = work.GetString("status"),
                    Progress = work.GetFloat("progress"), Duration = work.GetFloat("duration"),
                    TargetId = work.GetString("target_id"), BlockReason = work.GetString("block_reason") };
                _workSiteWork = WorkActionDriver.Work;
                _workSiteShipId = before.WorkShipId; _workSiteShip = FindShipByIdInternal(before.WorkShipId);
                _workSiteRoot = _workSiteShip?.SceneRoot; _workSiteModules = ModuleIntegrityMap; _workSiteComponents = ComponentPlacementState;
                _workSiteRoomCenterId = before.WorkRoom;
            }
            // Reload rebuilds derived systems and may tick zero-time commissioning. Rollback restores
            // the captured raw models as well as canonical participants; it is not a new simulation step.
            GdDict systems = before.World.HomeShip.GetDictOrEmpty("ship_systems_summary");
            PowerGridState?.ApplySummary(systems.GetDictOrEmpty("power_grid_summary"));
            LifeSupportExpandedState?.ApplySummary(systems.GetDictOrEmpty("life_support_state_summary"));
            HullIntegrityState?.ApplySummary(systems.GetDictOrEmpty("hull_integrity_summary"));
            HullWebState?.ApplySummary(systems.GetDictOrEmpty("web_infestation_summary"));
            FireSuppressionState?.ApplySummary(systems.GetDictOrEmpty("fire_suppression_summary"));
            ExtinguisherState?.ApplySummary(systems.GetDictOrEmpty("extinguisher_summary"));
            PropulsionExpandedState?.ApplySummary(systems.GetDictOrEmpty("propulsion_state_summary"));
            SustenanceState?.ApplySummary(systems.GetDictOrEmpty("sustenance_state_summary"));
            SetPlayerPosition(before.PlayerPosition);
        }

        void FreePaidStagedRoots(IEnumerable<IShipSceneRoot> roots)
        {
            Exception failure = null;
            foreach (IShipSceneRoot root in roots.Distinct())
                if (root?.IsValid == true)
                    try { ShipHost.FreeShipRoot(root); }
                    catch (Exception error) { failure = failure ?? error; }
                    finally { if (!root.IsValid) _generationRootDocuments.Remove(root); }
            if (failure != null) throw failure;
        }

        // Boot and runtime replacement share this sole selected-world/owner publisher. Only their lifecycles differ.
        // Rebind only after the selected home has committed. Before then the original
        // points remain available for exact rollback against the retained home root.
        void RebindPaidManualRestoreViews(PaidRestoreOperation operation)
        {
            RequirePaidRestoreOperation(operation);
            if (!operation.Committed) throw new InvalidOperationException("manual_restore_views_before_commit");
            try
            {
                _workHoldInput = false; _studyConsent = false;
                RefreshStudyHud();
            }
            catch { operation.ManualViewsFailed = true; throw; }
        }

        void InstallPaidSelectedWorld(PaidRestoreOperation operation, GdDict payload, WorldSnapshot world, GdDict owner, IResourceReader reader)
        {
            RequirePaidRestoreOperation(operation);
            _runId = world.RunId; SaveLoadService.SetActiveRunId(_runId);
            WithArtifactReader(reader, () =>
            {
                RestoreGenerationDocuments(payload); _selectedGeneration = operation.Selection.DeepCopy();
                BlueprintPath = payload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty("ship_start").GetString("blueprint_path");
                if (!WorldSnapshotAssembler.ApplyOwned(this, world, operation)) throw new InvalidOperationException("generation_apply_failed");
                GdDict candidate = owner.DeepCopy();
                PauseSavedManualJobs(candidate);
                foreach (GdDict job in PaidState(candidate).GetDictOrEmpty("jobs").Values.OfType<GdDict>())
                    if (!PaidCraftingState.Terminal(job)) { job["resume_required"] = true; if (job.GetString("status") == "running") job["status"] = "paused"; }
                PauseSavedPaidMirrors(candidate.GetDictOrEmpty("participating_state"));
                GdDict work = candidate.GetDictOrEmpty("component_work");
                if (!work.IsEmpty && work.GetString("status") != "committed") { work["status"] = "paused_restore"; work["resume_required"] = true; work["reason"] = "explicit_resume_required"; }
                _paidRestoreFinalContext = CurrentPaidRestoreContext();
                DomainTransactionCoordinator next = NewComponentOwner(candidate);
                ApplyComponentViews(candidate); _componentDomain = next;
                if (ComponentIntegrationEnabled) BindComponentReadViews();
                BindPaidCraftingModels(); _workHoldInput = false; _studyConsent = false;
                _workAwaitingResume = !work.IsEmpty && work.GetBool("resume_required"); MirrorComponentWork(work);
                return true;
            });
        }

        void BootPaidSelectedGeneration(Action<RunSession> beforeReady)
        {
            var operation = new PaidRestoreOperation(this) { Selection = Deps.SelectedSaveGeneration.DeepCopy() };
            _paidRestoreOperation = operation; ComponentGenerationRestoreInProgress = true;
            IPreparedHome prepared = null;
            var selectedOwned = new List<IShipSceneRoot>();
            bool release = true, modelsStarted = false;
            try
            {
                beforeReady?.Invoke(this);
                if (!(ShipHost is IPreparedHomeSceneHost host)) throw new InvalidOperationException("prepared_home_unsupported");
                if (!PrepareGenerationBoot()) return;
                GdDict exact = operation.Selection;
                string mode = ComponentIntegrationEnabled ? PaidSnapshotCodec.DiagnosticMode : PaidSnapshotCodec.OrdinaryMode;
                if (!SaveGenerationArtifacts.TryCreateReader(exact, CoreServices.Resources, out IResourceReader reader, out string reason, true, mode))
                    throw new InvalidOperationException(reason);
                GdDict payload = exact.GetDictOrEmpty("payloads");
                GdDict worldDict = PaidSnapshotCodec.Parse(payload.GetString("world_text"), PaidSnapshotCodec.SnapshotPolicy(ComponentIntegrationEnabled, true));
                GdDict envelope = worldDict?.GetDictOrEmpty("home_ship").GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft");
                if (envelope == null || !ComponentDomainCodec.TryDecode(envelope.GetDictOrEmpty("domain"), out GdDict owner, out reason))
                    throw new InvalidOperationException(reason ?? "invalid_paid_world");
                WorldSnapshot world = WorldSnapshot.FromDict(worldDict, ComponentIntegrationEnabled ? WorldSnapshot.ComponentIntegrationVersion : WorldSnapshot.WorldSliceVersion, Deps.Engine.VersionString);
                if (world == null || !WorldSnapshotAssembler.ValidateConnectionSnapshot(world, "ship_start", "lifeboat", out _, out reason))
                    throw new InvalidOperationException(reason ?? "invalid_world");
                modelsStarted = true;
                WithArtifactReader(reader, () => { BuildRuntimeNodes(); return true; });
                prepared = WithArtifactReader(reader, () => host.PrepareHome(PaidHomeDocuments(payload), null, out reason));
                if (prepared == null) throw new InvalidOperationException(reason ?? "prepared_home_failed");
                bool staged = WithArtifactReader(reader, () => StageGenerationRoots(payload, world, operation.SelectedRoots));
                selectedOwned.AddRange(operation.SelectedRoots.Values);
                if (!staged) throw new InvalidOperationException("required_ship_host_failed");
                PaidRestoreContext prospective = WithArtifactReader(reader, () => PreparePaidRestoreContext(world.RunId, prepared.PreparedLoader, world, operation.SelectedRoots, owner, payload));
                if (!ValidatePaidCraftingRestoreInContext(owner, prospective, out reason)) throw new InvalidOperationException(reason);
                CheckPaidRestoreAuthority(operation);
                if (!prepared.TryAdopt(out reason)) throw new InvalidOperationException(reason);
                CheckPaidRestoreAuthority(operation);
                operation.HomeToInstall = prepared.PreparedLoader; operation.ActiveRoots = operation.SelectedRoots;
                InstallPaidSelectedWorld(operation, payload, world, owner, reader);
                CheckPaidRestoreAuthority(operation);
                prepared.Commit(); operation.Committed = true;
                FreePaidStagedRoots(operation.SelectedRoots.Values);
                RebindPaidManualRestoreViews(operation);
                if (!CheckCommittedPaidRestore(operation, out GdDict authority))
                {
                    release = ComponentTerminalPending;
                    LastFailureReason = "generation_loaded_unavailable";
                    LastSaveResult = new GdDict { { "ok", false }, { "applied", true }, { "reason", LastFailureReason }, { "authority", authority } };
                    PlayableStarted = false; PlayableFailed?.Invoke(LastFailureReason); return;
                }
                PlayableReady?.Invoke(GetPlayableSummary());
                if (!CheckCommittedPaidRestore(operation, out authority))
                {
                    release = ComponentTerminalPending; PlayableStarted = false;
                    LastFailureReason = "generation_loaded_unavailable";
                    LastSaveResult = new GdDict { { "ok", false }, { "applied", true }, { "reason", LastFailureReason }, { "authority", authority } };
                    return;
                }
                LastSaveResult = new GdDict { { "ok", true }, { "reason", "generation_loaded" } };
            }
            catch (Exception error)
            {
                ObservePaidRestoreTerminal(operation);
                if (operation.Committed)
                {
                    bool live = CheckCommittedPaidRestore(operation, out GdDict authority);
                    if (operation.ManualViewsFailed) live = false;
                    if (!live) { release = ComponentTerminalPending; PlayableStarted = false; }
                    LastFailureReason = live ? "" : "generation_loaded_unavailable";
                    LastSaveResult = new GdDict { { "ok", live }, { "applied", true }, { "reason", live ? "generation_loaded" : LastFailureReason },
                        { "detail", operation.ManualViewsFailed ? "manual_restore_views_failed" : "post_commit_notification_failed" }, { "authority", authority } };
                    return;
                }
                bool cleaned = true;
                try { if (prepared?.IsAdopted == true) prepared.RestoreRetainedHome(); } catch (Exception) { cleaned = false; }
                try { if (modelsStarted) ResetRuntimeForReload(); } catch (Exception) { cleaned = false; }
                try { FreePaidStagedRoots(selectedOwned.Concat(operation.SelectedRoots.Values)); } catch (Exception) { cleaned = false; }
                try { prepared?.Dispose(); } catch (Exception) { cleaned = false; }
                try { Scene?.DespawnPlayer(); } catch (Exception) { cleaned = false; }
                _componentDomain = null; Loader = null; HomeShip = null; CurrentShip = null; CurrentOccupancy = null;
                PlayableStarted = false; _isReloading = false;
                if (operation.TerminalRuns.Contains(_runId)) { ComponentTerminalPending = true; SliceComplete = true; }
                release = cleaned;
                LastFailureReason = cleaned ? error.Message : "restore_cleanup_failed";
                LastSaveResult = new GdDict { { "ok", false }, { "reason", LastFailureReason }, { "detail", error.GetType().Name } };
                try { PlayableFailed?.Invoke(LastFailureReason); } catch (Exception) { /* Failure notification cannot make this scene playable. */ }
            }
            finally
            {
                _paidRestoreFinalContext = null;
                if (release) { _paidRestoreOperation = null; ComponentGenerationRestoreInProgress = false; }
            }
        }

        bool ApplyPaidSelectedGeneration(GdDict selection)
        {
            bool Fail(string reason) { LastSaveResult = new GdDict { { "ok", false }, { "reason", reason } }; return false; }
            if (!PlayableStarted || SliceComplete || ComponentTerminalPending || SaveLoadService == null) return Fail("run_not_playable");
            if (!(ShipHost is IPreparedHomeSceneHost host)) return Fail("prepared_home_unsupported");
            if (selection == null || !selection.GetBool("ok")) return Fail(selection?.GetString("reason", "invalid_selection") ?? "invalid_selection");
            GdDict exact = SaveLoadService.ReadGeneration(selection.GetString("run_id"), selection.GetString("slot_id"), selection.GetString("generation_id"), selection.GetString("manifest_sha256"));
            if (!exact.GetBool("ok")) { LastSaveResult = exact; return false; }
            if (!PaidSnapshotCodec.Same(selection.Get("payloads"), exact.Get("payloads"))) return Fail("selection_mismatch");
            string mode = ComponentIntegrationEnabled ? PaidSnapshotCodec.DiagnosticMode : PaidSnapshotCodec.OrdinaryMode;
            if (!SaveGenerationArtifacts.TryCreateReader(exact, CoreServices.Resources, out IResourceReader reader, out string reason, true, mode)) return Fail(reason);
            GdDict payload = exact.GetDictOrEmpty("payloads");
            GdDict worldDict = PaidSnapshotCodec.Parse(payload.GetString("world_text"), PaidSnapshotCodec.SnapshotPolicy(ComponentIntegrationEnabled, true));
            GdDict envelope = worldDict?.GetDictOrEmpty("home_ship").GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft");
            if (envelope == null || !ComponentDomainCodec.TryDecode(envelope.GetDictOrEmpty("domain"), out GdDict owner, out reason)) return Fail(reason ?? "invalid_paid_world");
            WorldSnapshot world = WorldSnapshot.FromDict(worldDict, ComponentIntegrationEnabled ? WorldSnapshot.ComponentIntegrationVersion : WorldSnapshot.WorldSliceVersion, Deps.Engine.VersionString);
            if (world == null || !WorldSnapshotAssembler.ValidateConnectionSnapshot(world, "ship_start", "lifeboat", out _, out reason)) return Fail(reason ?? "invalid_world");
            var operation = new PaidRestoreOperation(this) { Selection = exact.DeepCopy() };
            _paidRestoreOperation = operation; ComponentGenerationRestoreInProgress = true;
            PaidRestoreBefore before = null; IPreparedHome prepared = null;
            var selectedOwned = new List<IShipSceneRoot>(); var beforeOwned = new List<IShipSceneRoot>();
            bool release = true;
            try
            {
                before = CapturePaidRestoreBefore(operation);
                prepared = WithArtifactReader(reader, () => host.PrepareHome(PaidHomeDocuments(payload), before.HomeLoader, out reason));
                if (prepared == null) throw new InvalidOperationException(reason ?? "prepared_home_failed");
                bool staged = WithArtifactReader(reader, () => StageGenerationRoots(payload, world, operation.SelectedRoots));
                selectedOwned.AddRange(operation.SelectedRoots.Values);
                if (!staged) throw new InvalidOperationException("required_ship_host_failed");
                staged = WithArtifactReader(before.Reader, () => StageGenerationRoots(before.Documents, before.World, operation.BeforeRoots));
                beforeOwned.AddRange(operation.BeforeRoots.Values);
                if (!staged) throw new InvalidOperationException("rollback_ship_host_failed");
                PaidRestoreContext prospective = WithArtifactReader(reader, () => PreparePaidRestoreContext(world.RunId, prepared.PreparedLoader, world, operation.SelectedRoots, owner, payload));
                if (!ValidatePaidCraftingRestoreInContext(owner, prospective, out reason)) throw new InvalidOperationException(reason);
                CheckPaidRestoreAuthority(operation);
                if (!ReferenceEquals(_componentDomain, before.Coordinator) || !PaidSnapshotCodec.Same(before.Owner, _componentDomain.GetSummary())) throw new InvalidOperationException("before_owner_changed");
                if (!prepared.TryAdopt(out reason)) throw new InvalidOperationException(reason);
                CheckPaidRestoreAuthority(operation);
                operation.Destructive = true;
                operation.HomeToInstall = prepared.PreparedLoader; operation.ActiveRoots = operation.SelectedRoots;
                InstallPaidSelectedWorld(operation, payload, world, owner, reader);
                CheckPaidRestoreAuthority(operation);
                operation.Committed = true;
                try { prepared.Commit(); }
                catch { if (before.HomeLoader.IsValid) operation.Committed = false; throw; }
                _generationRootDocuments.Remove(before.HomeLoader);
                FreePaidStagedRoots(operation.SelectedRoots.Values.Concat(beforeOwned));
                RebindPaidManualRestoreViews(operation);
                if (!CheckCommittedPaidRestore(operation, out GdDict committedAuthority))
                {
                    release = ComponentTerminalPending;
                    LastSaveResult = new GdDict { { "ok", false }, { "applied", true }, { "reason", "generation_loaded_unavailable" }, { "authority", committedAuthority } };
                    return false;
                }
                // Ready observes a fully installed owner/world, but callback mutations remain excluded until return.
                PlayableReady?.Invoke(GetPlayableSummary());
                if (!CheckCommittedPaidRestore(operation, out committedAuthority))
                {
                    release = ComponentTerminalPending;
                    LastSaveResult = new GdDict { { "ok", false }, { "applied", true }, { "reason", "generation_loaded_unavailable" }, { "authority", committedAuthority } };
                    return false;
                }
                LastSaveResult = new GdDict { { "ok", true }, { "reason", "generation_loaded" } };
                return true;
            }
            catch (Exception e)
            {
                ObservePaidRestoreTerminal(operation);
                if (operation.Committed)
                {
                    try { FreePaidStagedRoots(operation.SelectedRoots.Values.Concat(beforeOwned)); }
                    catch (Exception) { /* Per-root cleanup continues before reporting the post-commit failure. */ }
                    // Ownership has committed; never reconstruct against an already retired home.
                    bool available = CheckCommittedPaidRestore(operation, out GdDict evidence);
                    if (operation.ManualViewsFailed) { available = false; PlayableStarted = false; }
                    if (!available) release = ComponentTerminalPending;
                    LastSaveResult = new GdDict { { "ok", available }, { "applied", true },
                        { "reason", available ? "generation_loaded" : "generation_loaded_unavailable" },
                        { "detail", operation.ManualViewsFailed ? "manual_restore_views_failed" : "post_commit_notification_failed" }, { "authority", evidence } };
                    return available;
                }
                bool rolledBack = !operation.Destructive;
                try
                {
                    if (prepared?.IsAdopted == true) prepared.RestoreRetainedHome();
                    if (operation.Destructive && before != null)
                    {
                        operation.RollingBack = true; operation.HomeToInstall = before.HomeLoader; operation.ActiveRoots = operation.BeforeRoots;
                        _runId = before.Run; SaveLoadService.SetActiveRunId(before.ServiceRun);
                        rolledBack = WithArtifactReader(before.Reader, () =>
                        {
                            RestoreGenerationDocuments(before.Documents);
                            BlueprintPath = before.Documents.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty("ship_start").GetString("blueprint_path");
                            if (!WorldSnapshotAssembler.ApplyOwned(this, before.World, operation)) return false;
                            RestorePaidRawBefore(before); return true;
                        });
                    }
                }
                catch (Exception) { rolledBack = false; }
                try { FreePaidStagedRoots(selectedOwned.Concat(operation.Destructive && rolledBack ? operation.BeforeRoots.Values : (IEnumerable<IShipSceneRoot>)beforeOwned)); }
                catch (Exception) { rolledBack = false; }
                try { prepared?.Dispose(); } catch (Exception) { rolledBack = false; }
                try { if (before != null) RestorePaidBookkeeping(before); } catch (Exception) { rolledBack = false; }
                // A death callback's global meta result is terminal evidence too; a world before-image cannot undo it.
                try { if (operation.TerminalMeta != null) MetaProgressionState?.ApplySummary(operation.TerminalMeta); }
                catch (Exception) { rolledBack = false; }
                string restoredRun = before?.Run ?? _runId;
                GdDict authority = null;
                bool live = rolledBack && ReadRestoreRunLive(restoredRun, exact.GetString("slot_id"), out authority);
                if (authority?.GetString("reason") == "run_terminal" || authority?.GetString("reason") == "legacy_death")
                    operation.TerminalRuns.Add(restoredRun);
                if (operation.TerminalRuns.Contains(restoredRun))
                { ComponentTerminalPending = true; SliceComplete = true; }
                else if (!live)
                { ComponentTerminalPending = false; release = false; PlayableStarted = false; }
                else if (before != null)
                {
                    // A selected foreign run's pending flag cannot terminalize this independently rechecked run.
                    ComponentTerminalPending = false; SliceComplete = before.Complete; PlayableStarted = before.Playable;
                }
                if (!rolledBack) { release = false; PlayableStarted = false; }
                LastSaveResult = new GdDict { { "ok", false }, { "reason", rolledBack ? e.Message : "restore_rollback_failed" }, { "detail", e.GetType().Name } };
                if (authority != null && !live) LastSaveResult["restored_run_authority"] = authority.DeepCopy();
                if (operation.TerminalRuns.Count > 0) LastSaveResult["observed_terminal_runs"] = new GdArray(operation.TerminalRuns.OrderBy(id => id, StringComparer.Ordinal).Select(id => (object)id));
                if (operation.TerminalResults.Count > 0)
                {
                    var evidence = new GdDict(); foreach (var pair in operation.TerminalResults) evidence[pair.Key] = pair.Value.DeepCopy();
                    LastSaveResult["authority_evidence"] = evidence;
                }
                return false;
            }
            finally
            {
                _paidRestoreFinalContext = null;
                if (release) { _paidRestoreOperation = null; ComponentGenerationRestoreInProgress = false; }
            }
        }
    }
}
