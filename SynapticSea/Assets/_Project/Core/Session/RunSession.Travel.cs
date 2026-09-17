// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: scan / travel_to_marker_id / _attach_derelict_active
// (2171-2290), travel_to (6489-6607), travel_home (6616-6705), the per-ship summary sync + restore helpers (7169-7302),
// derelict regeneration (10996-11037), and the run-context resolvers (11296-11363).
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
        /// <summary>Resolves visible markers at the gated detail level (current systems + scanner skill); records web-chart views.</summary>
        public GdDict Scan()
        {
            if (CurrentShip == null || SynapticSeaWorld == null || ScannerState == null)
                return new GdDict { { "detail_level", 0L }, { "markers", new GdArray() } };
            GdDict ops = CurrentSystemsOps();
            long skill = 0;
            if (PlayerProgression != null)
                skill = PlayerProgression.GetSkillLevel("scanner_operation");
            GdDict result = ScannerState.Scan(SynapticSeaWorld, ops, skill);
            if (InventoryState != null && InventoryState.GetQuantity("web_chart") > 0)
                WebChartState.RecordViews(result.GetArrayOrEmpty("markers"), V.I64(result.Get("detail_level", 0L)));
            return result;
        }

        /// <summary>Travel to an in-range marker by id; {success:false, reason:"unknown_marker"} otherwise.</summary>
        public GdDict TravelToMarkerId(string markerId)
        {
            if (SynapticSeaWorld == null || ScannerState == null)
                return new GdDict { { "success", false }, { "reason", "not_ready" } };
            foreach (ShipMarker m in SynapticSeaWorld.MarkersInRange(ScannerState.RangeRadius))
            {
                if (m.MarkerId == markerId)
                {
                    GdDict result = TravelTo(m);
                    if (result.GetBool("success"))
                        TriggerTutorial("ship_traveled", "any");
                    return result;
                }
            }
            return new GdDict { { "success", false }, { "reason", "unknown_marker" } };
        }

        /// <summary>
        /// Makes <paramref name="inst"/> the active boarded derelict: attaches <paramref name="newRoot"/> at
        /// DERELICT_DOCK_OFFSET, re-docks the piloted ship to it (carrying the player + nested docked ships), spawns the
        /// seam barrier + controls, rebuilds the derelict's interactables, and seeds hazards breach -> fire -> arc under the
        /// demo hazard budget.
        /// </summary>
        void AttachDerelictActive(ShipInstance inst, IShipLoaderView newRoot, double preservedPlayerOxygen = -1.0)
        {
            CatchUpShip(inst);
            inst.SceneRoot = newRoot;
            ShipHost?.AttachShipRoot(newRoot);
            ShipHost?.SetShipRootPosition(newRoot, DERELICT_DOCK_OFFSET);
            if (inst.BuiltLayout.IsEmpty && newRoot != null)
                inst.BuiltLayout = newRoot.GetLayoutCopy();
            CurrentShip = inst;
            AwayFromStart = true;
            ResetTooltipFocus();
            if (PilotedShip != null)
            {
                GdDict carry = CapturePlayerCarry();
                List<SubtreeCapture> childCarry = CaptureSubtree();
                UnbayFromParent(PilotedShip);
                DockingManager.Undock(PilotedShip);
                GdDict dockRes = DockPilotedTo(inst);
                if (!dockRes.GetBool("success"))
                {
                    Log.Error("PlayableGeneratedShip: travel dock failed (" + V.Str(dockRes.Get("reason", "?")) + ") — re-docking piloted ship to home");
                    DockPilotedTo(HomeShip);
                }
                else
                {
                    EmitDockLandSfx();
                }
                ApplyPlayerCarry(carry);
                RepositionSubtree(childCarry);
            }
            SpawnDockBarrier(inst);
            SpawnBridgeTerminal(inst);
            SpawnHangarControl(inst);
            SpawnCargoHoldControl(inst);
            SpawnCartControlsForShip(inst);
            CurrentOccupancy = PilotedShip ?? inst;
            RestoreAuthoredPortalStates();
            BuildDerelictObjectives();
            BuildLootContainers();
            BuildSealedHatches();
            BuildRepairPoints();
            BuildBreachZone(false, preservedPlayerOxygen);
            // Hazard seeding order is breach -> fire -> arc; demo builds cap the number of hazard KINDS in that order.
            _lastDerelictHazardBudget = DemoMaxHazards();
            _lastDerelictHazardsSeeded.Clear();
            if (_lastDerelictHazardBudget != 0)
            {
                SeedDerelictBreaches();
                _lastDerelictHazardsSeeded.Add("breach");
            }
            BuildBreachSealPoints();
            if (_lastDerelictHazardBudget < 0 || _lastDerelictHazardBudget > 1)
            {
                SeedDerelictFire();
                _lastDerelictHazardsSeeded.Add("fire");
            }
            BuildFireZones();
            BuildFireSuppressionPoints();
            BuildExtinguisherRechargePort();
            if (_lastDerelictHazardBudget < 0 || _lastDerelictHazardBudget > 2)
            {
                BuildArcZone();
                _lastDerelictHazardsSeeded.Add("arc");
            }
            RestoreArcSummaryForCurrentShip();
            RestoreModuleIntegrityForCurrentShip();
            RestoreOrPopulateComponentPlacementForCurrentShip();
            // Unity port: the derelict gets the home ship's readability props and labels (Godot built them for home only).
            if (newRoot != null)
                Events.RaiseAffordancesRebuilt(newRoot);
        }

        /// <summary>Validates + executes a jump to a marker (gated by the PILOTED ship's propulsion).</summary>
        public GdDict TravelTo(ShipMarker marker)
        {
            if (CurrentShip == null || SynapticSeaWorld == null || TravelController == null || ShipGenerator == null)
                return new GdDict { { "success", false }, { "reason", "not_ready" } };
            if (PilotedShip != null && CurrentShip == PilotedShip && marker.MarkerId == CurrentShip.MarkerId)
            {
                EmitTravelDeniedSfx();
                return new GdDict { { "success", false }, { "reason", "already_here" } };
            }
            RecomputeOccupancy();
            if (PilotedShip != null && CurrentOccupancy != PilotedShip)
            {
                EmitTravelDeniedSfx();
                return new GdDict { { "success", false }, { "reason", "not_aboard_ship" } };
            }
            GdDict firstRunResult = ApplyFirstRunContractToMarker(marker);
            if (firstRunResult.GetBool("applicable") && !firstRunResult.GetBool("success"))
            {
                EmitTravelDeniedSfx();
                return new GdDict
                {
                    { "success", false },
                    { "reason", V.Str(firstRunResult.Get("reason", FirstRunAwayGate.UnsatisfiedReason)) },
                };
            }
            bool firstRunContractApplied = firstRunResult.GetBool("applied");
            double playerOxygenBeforeTransition = OxygenState != null ? V.F64(OxygenState.GetSummary().Get("oxygen", -1.0)) : -1.0;
            Vec3 prevPlayerPos = SynapticSeaWorld.PlayerPosition;
            bool wasGenerated = SynapticSeaWorld.IsGenerated(marker.MarkerId);
            var opsT = new GdDict { { "propulsion", CurrentSystemsOps().GetBool("propulsion") } };
            GdDict runCtx = ResolveDerelictRunContext(marker);
            if (firstRunContractApplied)
            {
                GdDict contractCtx = firstRunResult.GetDictOrEmpty("run_context");
                if (!contractCtx.IsEmpty) runCtx = contractCtx;
            }
            ShipGenerator.ConfigureRunContext(V.Str(runCtx.Get("biome", "")), V.Str(runCtx.Get("difficulty", "")));
            TravelAttemptResult result = TravelController.AttemptTravel(marker, opsT, SynapticSeaWorld, ShipGenerator, ScannerState.RangeRadius);
            if (!result.Success)
            {
                EmitTravelDeniedSfx();
                return result.ToDict();
            }
            IShipLoaderView newRoot = result.Ship is ShipDocuments docs ? BuildShipSceneFromDocuments(docs) : result.Ship as IShipLoaderView;
            if (newRoot == null)
            {
                EmitTravelDeniedSfx();
                return new GdDict { { "success", false }, { "reason", "generation_failed" } };
            }
            // Abort with dock_incompatible BEFORE freeing the current host (no half-undocked state); roll the world back.
            if (PilotedShip != null)
            {
                GdDict targetLocal = DockPorts.ForDerelict(newRoot.GetLayoutCopy(), marker.SeedValue, marker.Condition);
                GdDict lbLocal = PilotedPortLocal();
                if (!DockPorts.PortsCompatible(targetLocal, lbLocal))
                {
                    ShipHost?.FreeShipRoot(newRoot);
                    SynapticSeaWorld.SetPlayerPosition(prevPlayerPos);
                    if (!wasGenerated)
                        SynapticSeaWorld.UnmarkGenerated(marker.MarkerId);
                    EmitTravelDeniedSfx();
                    return new GdDict { { "success", false }, { "reason", "dock_incompatible" } };
                }
            }
            SyncCurrentShipCombatSummary();
            SyncCurrentShipArcSummary();
            SyncCurrentShipBreachEnvironment();
            SyncCurrentShipPillarSummaries();
            ShipInstance leaving = CurrentShip;
            if (leaving.MarkerId != "" && leaving.SceneRoot is IShipLoaderView leavingLoader)
                Events.RaiseAffordancesCleared(leavingLoader);
            if (leaving.MarkerId == "")
            {
                if (HasPlayer)
                    _homePlayerPosition = PlayerPos;
            }
            else if (leaving != PilotedShip && RootValid(leaving.SceneRoot))
            {
                ShipHost?.FreeShipRoot(leaving.SceneRoot);
                leaving.SceneRoot = null;
            }
            string mid = marker.MarkerId;
            ShipInstance inst;
            if (VisitedShips.ContainsKey(mid))
            {
                inst = VisitedShips[mid];
            }
            else
            {
                var newBp = new ShipBlueprint(marker.SizeClass, marker.Condition, marker.SeedValue);
                var newMgr = new ShipSystemsManager();
                newMgr.Configure(newMgr.LoadDefinitions(), newBp.ShipCondition, newBp.SeedValue);
                inst = ShipInstance.Create("ship_" + mid, mid, newBp, newMgr, null);
                VisitedShips[mid] = inst;
                SeedShipModels(inst);
            }
            AttachDerelictActive(inst, newRoot, playerOxygenBeforeTransition);
            ConfigureThreatRuntimeForCurrentShip();
            RecomputeOccupancy();
            EmitTrainingEvent("plot_course", mid);
            EmitTrainingEvent("complete_astrogation", mid);
            return result.ToDict();
        }

        /// <summary>Returns to the home ship: frees the derelict scene, re-docks the ride home, rebuilds home interactables.</summary>
        public bool TravelHome()
        {
            if (!AwayFromStart || HomeShip == null)
                return false;
            double playerOxygenBeforeTransition = OxygenState != null ? V.F64(OxygenState.GetSummary().Get("oxygen", -1.0)) : -1.0;
            SyncCurrentShipCombatSummary();
            SyncCurrentShipArcSummary();
            SyncCurrentShipBreachEnvironment();
            SyncCurrentShipPillarSummaries();
            GdDict carry = CapturePlayerCarry();
            List<SubtreeCapture> childCarry = CaptureSubtree();
            if (PilotedShip != null)
            {
                UnbayFromParent(PilotedShip);
                DockingManager.Undock(PilotedShip);
            }
            ShipInstance leaving = CurrentShip;
            if (leaving != null && leaving.MarkerId != "")
            {
                if (leaving.SceneRoot is IShipLoaderView leavingLoader)
                    Events.RaiseAffordancesCleared(leavingLoader);
                if (leaving != PilotedShip && RootValid(leaving.SceneRoot))
                {
                    ShipHost?.FreeShipRoot(leaving.SceneRoot);
                    leaving.SceneRoot = null;
                }
            }
            CurrentShip = HomeShip;
            AwayFromStart = false;
            RestoreModuleIntegrityForCurrentShip();
            RestoreOrPopulateComponentPlacementForCurrentShip();
            ConfigureThreatRuntimeForCurrentShip();
            ResetTooltipFocus();
            ClearDerelictObjectives();
            ClearLootContainers();
            ClearRepairPoints();
            ClearBreachSealPoints();
            HallucinationManager?.ClearAll();
            ClearFireZones();
            ClearFireSuppressionPoints();
            ClearExtinguisherRechargePort();
            BuildLootContainers();
            BuildSealedHatches();
            BuildRepairPoints();
            BuildBreachZone(false, playerOxygenBeforeTransition);
            BuildBreachSealPoints();
            SeedFiresFromDamage();
            BuildFireZones();
            BuildArcZone();
            RestoreArcSummaryForCurrentShip();
            BuildHallucinationRuntime();
            BuildFireSuppressionPoints();
            BuildExtinguisherRechargePort();
            BuildCraftingStations();
            BuildProductionStations();
            if (Loader != null)
            {
                Events.RaiseTrackerObjectivesSet(Loader.GetObjectiveSpecsCopy());
                RefreshHomeTrackerCompleted();
            }
            if (PilotedShip != null)
            {
                DockPilotedTo(HomeShip);
                ApplyPlayerCarry(carry);
                RepositionSubtree(childCarry);
            }
            SpawnDockBarrier(HomeShip);
            CurrentOccupancy = PilotedShip ?? HomeShip;
            RecomputeOccupancy();
            EmitDockLandSfx();
            return true;
        }

        public IReadOnlyList<string> GetVisitedShipIds() => VisitedShips.Keys;

        // ------------------------------------------------------------------ per-ship summary sync / restore
        void SyncCurrentShipCombatSummary()
        {
            if (CurrentShip == null || ThreatManager == null)
                return;
            CurrentShip.CombatSummary = ThreatManager.GetSummary();
        }

        void SyncCurrentShipArcSummary()
        {
            if (CurrentShip == null || ElectricalArcState == null)
                return;
            CurrentShip.ArcSummary = ElectricalArcState.GetSummary().DeepCopy();
        }

        void SyncCurrentShipBreachEnvironment()
        {
            if (CurrentShip == null || OxygenState == null)
                return;
            CurrentShip.BreachEnvironmentSummary = BreachEnvironmentFrom(OxygenState.GetSummary());
        }

        static GdDict BreachEnvironmentFrom(GdDict oxygenSummary) => new GdDict
        {
            { "hazard_kind", "oxygen" },
            { "breach_open", oxygenSummary.GetBool("breach_open") },
            { "breach_sealed", oxygenSummary.GetBool("breach_sealed") },
            { "passability_blocked", oxygenSummary.GetBool("passability_blocked") },
            { "breach_zone_ids", oxygenSummary.GetArrayOrEmpty("breach_zone_ids").ShallowCopy() },
        };

        /// <summary>PKG-D6.1: flush live module integrity + component placement onto the current ship.</summary>
        void SyncCurrentShipPillarSummaries()
        {
            if (CurrentShip == null)
                return;
            if (ModuleIntegrityMap != null)
            {
                if (CurrentShip.ModuleIntegritySummary.IsEmpty)
                {
                    GdDict live = ModuleIntegrityMap.GetSummary();
                    if (!(live.Get("deltas", null) is GdArray liveD) || liveD.IsEmpty)
                        RestoreModuleIntegrityForCurrentShip();
                }
                CurrentShip.ModuleIntegritySummary = ModuleIntegrityMap.GetSummary().DeepCopy();
            }
            if (ComponentPlacementState != null)
            {
                GdDict cp = ComponentPlacementState.GetSummary();
                GdArray placed = cp.Get("placed", new GdArray()) as GdArray ?? new GdArray();
                CurrentShip.ComponentPlacementSummary = placed.IsEmpty ? new GdDict() : cp.DeepCopy();
            }
        }

        /// <summary>PKG-D6.1: restore per-ship integrity after attach/home return (empty = pristine).</summary>
        void RestoreModuleIntegrityForCurrentShip()
        {
            ModuleIntegrityMap = new ModuleIntegrityMap();
            if (CurrentShip == null)
                return;
            GdDict layout = new GdDict();
            if (CurrentShip.BuiltLayout != null)
                layout = CurrentShip.BuiltLayout;
            else if (Loader != null && Loader.IsValid)
                layout = Loader.GetLayoutCopy();
            GdDict packed = CurrentShip.ModuleIntegritySummary;
            bool hasDeltas = packed != null && !packed.IsEmpty;
            if (!layout.IsEmpty)
                ModuleIntegrityConsequences.SeedMapFromCompiledLayout(ModuleIntegrityMap, layout, !hasDeltas);
            if (hasDeltas && packed.Get("deltas", null) is GdArray deltas)
                ModuleIntegrityMap.ApplySparseDeltas(deltas);
            ApplyModuleIntegrityStateToScene();
        }

        /// <summary>PKG-B2.3 / D6.1: restore the current ship's component placement or populate it from layout slots.</summary>
        void RestoreOrPopulateComponentPlacementForCurrentShip()
        {
            if (ComponentCatalog == null)
            {
                ComponentCatalog = new ComponentCatalog();
                ComponentCatalog.LoadDefault();
            }
            ComponentPlacementState = new ComponentPlacementState();
            if (CurrentShip != null && !CurrentShip.ComponentPlacementSummary.IsEmpty)
            {
                ComponentPlacementState.ApplySummary(CurrentShip.ComponentPlacementSummary);
                RebuildComponentMarkers();
                return;
            }
            GdDict layout = ActiveLayoutForWork();
            if (layout.IsEmpty)
            {
                ClearComponentMarkers();
                return;
            }
            long seedV = ComponentPlacementSeedForCurrentShip();
            ComponentPlacementState.Populate(layout, ComponentCatalog, seedV, SlotOccupancyFromLoader());
            GdDict systemsDoc = LoadJsonDict("res://data/ship_systems/systems.json");
            if (!systemsDoc.IsEmpty)
                ComponentPlacementState.LinkShipSystems(systemsDoc, ComponentCatalog);
            if (CurrentShip != null && ComponentPlacementState.Placed.Count > 0)
                CurrentShip.ComponentPlacementSummary = ComponentPlacementState.GetSummary();
            RebuildComponentMarkers();
        }

        long ComponentPlacementSeedForCurrentShip()
        {
            if (CurrentShip == null)
                return 1;
            if (CurrentShip.Blueprint != null)
            {
                long sv = CurrentShip.Blueprint.SeedValue;
                if (sv != 0)
                    return sv;
            }
            string mid = CurrentShip.MarkerId;
            if (mid.Length == 0)
                return 17;
            return GodotHash.StringHash(mid) & 0x7FFFFFFF;
        }

        void RestoreArcSummaryForCurrentShip()
        {
            if (CurrentShip == null || ElectricalArcState == null)
                return;
            if (!CurrentShip.ArcSummary.IsEmpty)
                ElectricalArcState.ApplySummary(CurrentShip.ArcSummary);
            RefreshArcState(true);
            SyncCurrentShipArcSummary();
        }

        void ConfigureThreatRuntimeForCurrentShip()
        {
            if (ThreatManager == null)
                return;
            Vec3 anchor = CombatAnchorForCurrentShip();
            ThreatManager.FallbackAnchor = anchor;
            if (CurrentShip != null && !CurrentShip.CombatSummary.IsEmpty)
            {
                ThreatManager.ApplySummary(CurrentShip.CombatSummary);
                ThreatManager.ConfigureNavGraph(CombatLayoutForCurrentShip());
            }
            else
            {
                ApplyThreatRunModifiers();
                ThreatManager.ConfigureForLayout(CombatLayoutForCurrentShip(), CombatMarkersForCurrentShip(), anchor);
            }
            ApplyIntegrityNavGaps();
            RefreshWeaponHotbar();
        }

        Vec3 CombatAnchorForCurrentShip()
        {
            if (CurrentShip != null && RootInTree(CurrentShip.SceneRoot))
                return CurrentShip.SceneRoot.GlobalTransform.Origin;
            if (CurrentShip != null && RootValid(CurrentShip.SceneRoot))
                return CurrentShip.SceneRoot.Transform.Origin;
            return Vec3.Zero;
        }

        GdDict CombatLayoutForCurrentShip()
        {
            if (CurrentShip != null && CurrentShip.BuiltLayout != null && !CurrentShip.BuiltLayout.IsEmpty)
                return CurrentShip.BuiltLayout;
            if (Loader != null)
                return Loader.GetLayoutCopy();
            return new GdDict();
        }

        GdArray CombatMarkersForCurrentShip()
        {
            GdDict layout = CombatLayoutForCurrentShip();
            if (layout.Get("encounters", null) is GdArray raw && !raw.IsEmpty)
                return raw.DeepCopy();
            if (Loader != null)
                return Loader.GetEncounterMarkers() ?? new GdArray();
            return new GdArray();
        }

        // ------------------------------------------------------------------ derelict regeneration (world load)
        /// <summary>Regenerates a derelict from its retained blueprint, activates it, and re-homes the player.</summary>
        bool ActivateDerelictFromInstance(ShipInstance inst, GdArray posInShip)
        {
            if (inst == null || ShipGenerator == null)
                return false;
            ApplyRunContextFromBlueprint(inst.Blueprint);
            IShipLoaderView newRoot = GenerateShipScene(inst.Blueprint);
            if (newRoot == null)
                return false;
            AttachDerelictActive(inst, newRoot);
            ConfigureThreatRuntimeForCurrentShip();
            if (HasPlayer && posInShip != null && posInShip.Count >= 3)
                SetPlayerPosition(new Vec3(V.F64(posInShip[0]), V.F64(posInShip[1]), V.F64(posInShip[2])));
            return true;
        }

        IShipLoaderView GenerateShipScene(ShipBlueprint blueprint)
        {
            ShipDocuments docs = ShipGenerator.Generate(blueprint);
            if (docs == null)
                return null;
            return BuildShipSceneFromDocuments(docs);
        }

        /// <summary>Regenerates geometry for a co-present derelict that is not the active ship (dock-edge endpoint).</summary>
        void EnsureDerelictGeometry(ShipInstance inst)
        {
            if (inst == null || ShipGenerator == null)
                return;
            if (inst.MarkerId == "" || RootValid(inst.SceneRoot))
                return;
            ApplyRunContextFromBlueprint(inst.Blueprint);
            IShipLoaderView newRoot = GenerateShipScene(inst.Blueprint);
            if (newRoot == null)
                return;
            inst.SceneRoot = newRoot;
            ShipHost?.AttachShipRoot(newRoot);
            ShipHost?.SetShipRootPosition(newRoot, DERELICT_DOCK_OFFSET);
            if (inst.BuiltLayout.IsEmpty)
                inst.BuiltLayout = newRoot.GetLayoutCopy();
            SpawnBridgeTerminal(inst);
            SpawnHangarControl(inst);
            SpawnCargoHoldControl(inst);
            SpawnCartControlsForShip(inst);
        }

        // ------------------------------------------------------------------ run context
        GdDict ResolveRunContext(long seedValue, long size, long condition)
        {
            string biome = "abyssal_synaptic_sea";
            List<string> biomeIds = LootBiomeIds();
            if (biomeIds.Count > 0)
                biome = BiomeProfile.SelectBiome(seedValue, biomeIds);
            return new GdDict { { "biome", biome }, { "difficulty", DifficultyIdForDepth(size * 2 + condition) } };
        }

        static string DifficultyIdForDepth(long depth)
        {
            if (depth >= 5)
                return "deep_dive";
            if (depth >= 3)
                return "hardened";
            return "standard";
        }

        GdDict ResolveDerelictRunContext(ShipMarker marker)
        {
            if (marker == null)
                return new GdDict { { "biome", "abyssal_synaptic_sea" }, { "difficulty", "standard" } };
            return ResolveRunContext(marker.SeedValue, marker.SizeClass, marker.Condition);
        }

        void ApplyRunContextFromBlueprint(ShipBlueprint blueprint)
        {
            if (ShipGenerator == null)
                return;
            if (blueprint == null)
            {
                ShipGenerator.ConfigureRunContext("", "");
                return;
            }
            GdDict ctx = ResolveRunContext(blueprint.SeedValue, blueprint.ShipSize, blueprint.ShipCondition);
            ShipGenerator.ConfigureRunContext(V.Str(ctx.Get("biome", "")), V.Str(ctx.Get("difficulty", "")));
        }

        /// <summary>The in-range marker ids (was <c>scannable_marker_ids_for_validation</c>).</summary>
        public List<string> ScannableMarkerIds()
        {
            var output = new List<string>();
            if (SynapticSeaWorld == null || ScannerState == null)
                return output;
            foreach (ShipMarker m in SynapticSeaWorld.MarkersInRange(ScannerState.RangeRadius))
                output.Add(m.MarkerId);
            return output;
        }

        /// <summary>In-range markers whose generated layout has a bridge room (was <c>claimable_marker_ids_for_validation</c>).</summary>
        public List<string> ClaimableMarkerIds()
        {
            var output = new List<string>();
            if (SynapticSeaWorld == null || ScannerState == null || ShipGenerator == null)
                return output;
            foreach (ShipMarker marker in SynapticSeaWorld.MarkersInRange(ScannerState.RangeRadius))
            {
                GdDict ctx = ResolveDerelictRunContext(marker);
                ShipGenerator.ConfigureRunContext(V.Str(ctx.Get("biome", "")), V.Str(ctx.Get("difficulty", "")));
                ShipDocuments built = ShipGenerator.GenerateFromSeed(marker.SeedValue, marker.SizeClass, marker.Condition);
                if (built != null && LayoutHasBridge(built.Layout))
                    output.Add(marker.MarkerId);
            }
            return output;
        }

        /// <summary>
        /// Unity-port validation helper (tests): the structural kit id an in-range marker's derelict generates with, through
        /// the same run context and generator the travel path uses ("" for an unknown marker). The first-run contract, which
        /// replaces the seed of the run's first travel, is not applied.
        /// </summary>
        public string MarkerKitId(string markerId)
        {
            if (SynapticSeaWorld == null || ScannerState == null || ShipGenerator == null)
                return "";
            foreach (ShipMarker marker in SynapticSeaWorld.MarkersInRange(ScannerState.RangeRadius))
            {
                if (marker.MarkerId != markerId)
                    continue;
                GdDict ctx = ResolveDerelictRunContext(marker);
                ShipGenerator.ConfigureRunContext(V.Str(ctx.Get("biome", "")), V.Str(ctx.Get("difficulty", "")));
                ShipDocuments built = ShipGenerator.GenerateFromSeed(marker.SeedValue, marker.SizeClass, marker.Condition);
                return built != null && built.Layout != null ? V.Str(built.Layout.Get("kit_id", "")) : "";
            }
            return "";
        }

        static bool LayoutHasBridge(GdDict layout)
        {
            if (!(layout?.Get("rooms", null) is GdArray rooms))
                return false;
            foreach (object roomV in rooms)
            {
                if (roomV is GdDict room && V.Str(room.Get("room_role", "")) == "bridge")
                    return true;
            }
            return false;
        }
    }
}
