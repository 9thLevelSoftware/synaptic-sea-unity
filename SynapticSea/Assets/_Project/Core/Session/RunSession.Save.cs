// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: end_run / permadeath freeze (1814-1881), the
// checkpoint + timed autosave + quicksave + manual save/load policy (10248-10518), _apply_run_snapshot's body
// (10524-10754), apply_manual_slot (10773-10790), the meta payout (10323-10361), the demo gates (10438-10468) and
// _reset_runtime_for_reload's model half (11418-11663).
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
        // ------------------------------------------------------------------ end of run
        /// <summary>
        /// REQ-PM-008 explicit end-of-run trigger (death, extraction, abandon): meta payout + persistence, then death
        /// FREEZES the run's slots (ADR-0043) while extraction deletes them. Idempotent (returns 0 once complete).
        /// </summary>
        public long EndRun(string reason = "extraction")
        {
            if (SliceComplete)
                return 0;
            SliceComplete = true;
            Events.RaiseTrackerRunComplete();
            TriggerTutorial("run_ended", reason);
            PlaySfx(reason == "death" ? AudioEventSeam.UI_VITALS_LOW : AudioEventSeam.UI_OBJECTIVE_ADVANCE);
            if (reason != "death")
                TryUnlockAchievement("run_complete", reason);
            long payout = ApplyMetaPayoutAndPersist(reason);
            if (SaveLoadService != null)
            {
                if (reason == "death")
                {
                    FreezeRunOnDeath();
                }
                else
                {
                    SaveLoadService.DeleteCurrentRun();
                    foreach (object slotId in SaveSlotState.AutosaveSlotIds)
                        SaveLoadService.DeleteSlot(V.Str(slotId));
                }
            }
            if (reason != "death")
                EmitTrainingEvent("transmit_relay", reason);
            GdDict completionSummary = GetSliceCompletionSummary();
            completionSummary["reason"] = reason;
            PlayableSliceCompleted?.Invoke(completionSummary);
            return payout;
        }

        /// <summary>ADR-0043: freeze every slot this run instance wrote (death record + epitaph).</summary>
        void FreezeRunOnDeath() =>
            SaveLoadService.FreezeRun(_runId, "death", BuildEpitaphText(), WorldTime, CurrentObjectiveSequence);

        string BuildEpitaphText()
        {
            string location = CurrentShip != null ? CurrentShip.MarkerId : "";
            string locationLabel = location.Length > 0 ? location : "the home ship";
            return "Died aboard " + locationLabel + " at objective " + CurrentObjectiveSequence + " (run time " + GdString.FormatFixed(WorldTime, 0) + "s)";
        }

        /// <summary>REQ-PM-008: fold the run into meta progression, bridge class unlocks, persist meta + unlocks.</summary>
        long ApplyMetaPayoutAndPersist(string reason = "completion")
        {
            if (MetaProgressionState == null)
                return 0;
            GdDict summary = GetSliceCompletionSummary();
            long completed = V.I64(summary.Get("completed_objectives", ObjectiveCompletionCount));
            var skillLevels = new GdDict();
            if (PlayerProgression != null)
            {
                foreach (object sid in PlayerProgression.Skills.Keys)
                    skillLevels[sid] = V.I64(PlayerProgression.Skills[sid]);
            }
            var runSummary = new GdDict
            {
                { "completed_objectives", completed },
                { "skill_levels", skillLevels },
                { "discoveries", 0L },
                { "reason", reason },
            };
            long payout = MetaProgressionState.ApplyMetaPayout(runSummary);
            if (UnlockRegistry != null)
            {
                if (TrainingEventBus != null)
                {
                    foreach (object entryObj in TrainingEventBus.GetLog())
                    {
                        var entry = entryObj as GdDict ?? new GdDict();
                        string evt = V.Str(entry.Get("event_id", ""));
                        string tgt = V.Str(entry.Get("target_id", ""));
                        if (evt.Length > 0)
                        {
                            UnlockRegistry.UnlockForTrigger(evt, tgt);
                            foreach (object cls in UnlockRegistry.ClassIdsForTrigger(evt, tgt))
                                MetaProgressionState.UnlockClass(V.Str(cls));
                        }
                    }
                }
                UnlockRegistry.SaveToDisk();
            }
            MetaProgressionState.SaveToDisk();
            Log.Info("META PAYOUT reason=" + reason + " payout=" + payout + " meta_currency=" + MetaProgressionState.MetaCurrency
                     + " runs_completed=" + MetaProgressionState.TotalRunsCompleted);
            return payout;
        }

        // ------------------------------------------------------------------ saves
        /// <summary>
        /// REQ-012 checkpoint save at every objective boundary: the WORLD format to <c>world.json</c>, keeping the in-memory
        /// RunSnapshot seam (<see cref="LastSavedSnapshot"/>).
        /// </summary>
        bool AutoSaveCurrentRun()
        {
            if (SaveLoadService == null || SliceComplete)
                return false;
            if (DemoSaveRefused())
                return false;
            WorldSnapshot ws = WorldSnapshotAssembler.Build(this);
            if (ws == null)
                return false;
            if (SaveLoadService.SaveWorld(ws))
            {
                LastSavedSnapshot = RunSnapshotAssembler.Build(this);
                return true;
            }
            return false;
        }

        long AutosaveEventCount()
        {
            long count = ObjectiveCompletionCount;
            if (TrainingEventBus != null)
                count += TrainingEventBus.GetLog().Count;
            return count;
        }

        /// <summary>Timed/rotating autosave loop (autosave_a/b/c), additive to the checkpoint save.</summary>
        void TickAutosavePolicy(double delta)
        {
            if (AutosavePolicy == null || SaveLoadService == null || SliceComplete)
                return;
            if (DemoSaveRefused())
                return;
            _autosaveRunSeconds += delta;
            GdDict r = AutosavePolicy.Tick(_autosaveRunSeconds, AutosaveEventCount());
            if (!r.GetBool("should_save"))
                return;
            RunSnapshot snap = RunSnapshotAssembler.Build(this);
            if (snap == null)
                return;
            string slotId = V.Str(r.Get("slot_id", ""));
            if (slotId.Length == 0)
                return;
            if (SaveLoadService.SaveToSlot(slotId, snap, SaveSlotState.SlotKindAuto, false, "Autosave"))
            {
                LastSavedSnapshot = snap;
                _lastAutosaveResult = r;
                PlaySfx(AudioEventSeam.UI_SAVE);
            }
        }

        /// <summary>Force one rotating autosave through the real path (was <c>force_autosave_for_validation</c>).</summary>
        public GdDict ForceAutosave()
        {
            if (AutosavePolicy == null)
                return new GdDict();
            AutosavePolicy.Force = true;
            TickAutosavePolicy(0.0);
            if (AutosavePolicy.Force)
                TickAutosavePolicy(0.0);
            return _lastAutosaveResult;
        }

        /// <summary>F6 quicksave (cooldown-gated) to the quicksave slot.</summary>
        public bool RequestQuicksave()
        {
            if (SliceComplete || SaveLoadService == null || AutosavePolicy == null)
                return false;
            if (DemoSaveRefused())
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return false;
            }
            GdDict gate = AutosavePolicy.TryQuicksave();
            if (!gate.GetBool("should_save"))
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return false;
            }
            RunSnapshot snap = RunSnapshotAssembler.Build(this);
            if (snap == null)
                return false;
            string slotId = V.Str(gate.Get("slot_id", SaveSlotState.QuicksaveSlotId));
            if (slotId.Length == 0)
                slotId = SaveSlotState.QuicksaveSlotId;
            if (SaveLoadService.SaveToSlot(slotId, snap, SaveSlotState.SlotKindQuick, true, "Quicksave"))
            {
                LastSavedSnapshot = snap;
                PlaySfx(AudioEventSeam.UI_SAVE);
                return true;
            }
            return false;
        }

        /// <summary>F5 / pause-menu save: the whole world (save-anywhere, ADR-0012). Refused before start / after completion.</summary>
        public bool RequestSave()
        {
            if (!PlayableStarted || SliceComplete)
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return false;
            }
            if (SaveLoadService == null)
                return false;
            if (DemoSaveRefused())
            {
                _lastLootFeedbackLine = "Demo build: save limit reached (" + (long)(DemoSaveCapSeconds() / 60.0) + " min)";
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return false;
            }
            WorldSnapshot ws = WorldSnapshotAssembler.Build(this);
            if (ws == null)
                return false;
            bool result = SaveLoadService.SaveWorld(ws);
            if (result)
            {
                PlaySfx(AudioEventSeam.UI_SAVE);
                Log.Info("PLAYABLE SHIP SAVED location=" + ws.CurrentLocation + " sequence=" + CurrentObjectiveSequence);
                TriggerTutorial("run_saved", "any");
                Events.RaiseLoadAvailable(true);
            }
            else
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            }
            return result;
        }

        /// <summary>ADR-0043 Save &amp; Exit: save through the same guarded path, leave to title only on success.</summary>
        public void SaveAndExit()
        {
            if (RequestSave())
                ReturnToTitleRequested?.Invoke();
            else
                TriggerTutorial("save_and_exit_failed", "any");
        }

        /// <summary>ADR-0043 "Quit to Main Menu".</summary>
        public void QuitToTitle() => ReturnToTitleRequested?.Invoke();

        double DemoSaveCapSeconds()
        {
            if (DemoScopeGate == null)
                return 1200.0;
            return V.F64(DemoScopeGate.GetParams("long_run.persistence").Get("max_play_seconds", 1200L));
        }

        bool DemoSaveRefused()
        {
            if (DemoScopeGate == null || !DemoScopeGate.IsBlocked("long_run.persistence"))
                return false;
            return RunPlayTimeSeconds >= DemoSaveCapSeconds();
        }

        long DemoMaxHazards()
        {
            if (DemoScopeGate == null || !DemoScopeGate.IsBlocked("multi_hazard.run"))
                return -1;
            return Math.Max(0L, V.I64(DemoScopeGate.GetParams("multi_hazard.run").Get("max_hazards", 1L)));
        }

        public bool IsLoadAvailable() => SaveLoadService != null && SaveLoadService.HasSave();

        /// <summary>F9 / Continue: load the whole world and apply it; adopts the loaded run_id.</summary>
        public bool RequestLoad()
        {
            if (SaveLoadService == null)
                return false;
            WorldSnapshot ws = SaveLoadService.LoadWorld();
            if (ws == null)
            {
                Log.Warning("PlayableGeneratedShip: no compatible world save to load");
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return false;
            }
            bool loaded = WorldSnapshotAssembler.Apply(this, ws);
            if (loaded)
            {
                _runId = ws.RunId.Length > 0 ? ws.RunId : GenerateRunId();
                SaveLoadService.SetActiveRunId(_runId);
                PlaySfx(AudioEventSeam.UI_LOAD);
                Events.RaiseLoadAvailable(true);
            }
            else
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            }
            return loaded;
        }

        /// <summary>ADR-0031/0043 slot screen: apply a manual-slot RunSnapshot onto the booted ship only.</summary>
        public bool ApplyManualSlot(RunSnapshot snapshot)
        {
            if (snapshot == null)
                return false;
            if (SliceComplete)
                return false;
            bool applied = RunSnapshotAssembler.Apply(this, snapshot);
            if (applied)
            {
                RunSnapshotAssembler.ApplyManualSlotWorldState(this, snapshot);
                TriggerTutorial("manual_slot_loaded", "any");
            }
            return applied;
        }

        // ------------------------------------------------------------------ assembler access (internal)
        internal void SyncArcSummaryForSave() => SyncCurrentShipArcSummary();
        internal void SyncPillarSummariesForSave() => SyncCurrentShipPillarSummaries();
        internal void SyncCombatSummaryForSave() => SyncCurrentShipCombatSummary();
        internal void SyncBreachEnvironmentForSave() => SyncCurrentShipBreachEnvironment();

        /// <summary>
        /// The home breach environment as a save records it, without mutating any ship: the live oxygen state while home,
        /// else the summary synced when the player left.
        /// </summary>
        internal GdDict HomeBreachEnvironmentForSave()
        {
            if (HomeShip == null)
                return new GdDict();
            if (CurrentShip == HomeShip && OxygenState != null)
                return BreachEnvironmentFrom(OxygenState.GetSummary());
            return HomeShip.BreachEnvironmentSummary.DeepCopy();
        }
        internal Vec3 HomePlayerPosition { get => _homePlayerPosition; set => _homePlayerPosition = value; }
        internal string RunIdInternal { get => _runId; set => _runId = value; }
        internal bool DemoCrossRunBlocked => DemoScopeGate != null && DemoScopeGate.IsBlocked("world_persistence.cross_run");
        internal List<ShipInstance> AllKnownShipsInternal() => AllKnownShips();
        internal ShipInstance FindShipByIdInternal(string id) => FindShipById(id);
        internal ShipInstance FindShipByIdOrMarkerInternal(string key) => FindShipByIdOrMarker(key);
        internal void RebuildBreachZoneForWorldLoad() => BuildBreachZone(false);
        internal void SpawnCartControlsForShipInternal(ShipInstance inst) => SpawnCartControlsForShip(inst);
        internal void RecomputeEncumbranceInternal() => RecomputePlayerEncumbrance();
        internal bool ActivateDerelictFromInstanceInternal(ShipInstance inst, GdArray posInShip) => ActivateDerelictFromInstance(inst, posInShip);
        internal void EnsureDerelictGeometryInternal(ShipInstance inst) => EnsureDerelictGeometry(inst);
        internal GdDict DockPilotedToInternal(ShipInstance host) => DockPilotedTo(host);

        internal void RebuildHomeLootAndHatchesForWorldLoad()
        {
            BuildLootContainers();
            BuildSealedHatches();
        }

        /// <summary><c>_redock_bayed(mobile, host, slot_index)</c>: re-peg a hangar edge (bay slot + parent link + slot anchor).</summary>
        internal void RedockBayedInternal(ShipInstance mobile, ShipInstance host, long slotIndex)
        {
            if (mobile == null || host == null)
                return;
            if (!RootValid(mobile.SceneRoot) || !RootValid(host.SceneRoot))
                return;
            ConfigureBayFromLayout(host);
            HangarBay bay = host.GetHangar();
            if (slotIndex >= 0 && slotIndex < bay.Slots.Count)
                bay.Slots[(int)slotIndex] = mobile.ShipId;
            else
                slotIndex = bay.Dock(mobile.ShipId, ShipDockSizeClass(mobile));
            mobile.ParentShip = host;
            if (!host.DockedShips.Contains(mobile))
                host.DockedShips.Add(mobile);
            PlaceInSlot(host, mobile, slotIndex);
        }

        /// <summary>The body of <c>_apply_run_snapshot</c>.</summary>
        internal bool ApplyRunSnapshotInternal(RunSnapshot snapshot)
        {
            if (snapshot == null || !PlayableStarted)
                return false;
            _isReloading = true;
            ResetRuntimeForReload();
            LayoutPath = snapshot.LayoutPath;
            KitPath = snapshot.KitPath;
            GameplaySlicePath = snapshot.GameplaySlicePath;
            PlayableStarted = false;
            LoadFromPaths(LayoutPath, KitPath, GameplaySlicePath);
            if (!PlayableStarted)
            {
                _isReloading = false;
                Log.Error("PlayableGeneratedShip: load failed because slice did not start");
                return false;
            }
            RunPlayTimeSeconds = snapshot.PlayTimeSeconds;
            if (ShipSystemsManager != null && !snapshot.ShipSystemsSummary.IsEmpty)
            {
                GdDict ss = snapshot.ShipSystemsSummary;
                ShipSystemsManager.ApplySummary(ss);
                PowerGridState?.ApplySummary(ss.GetDictOrEmpty("power_grid_summary"));
                LifeSupportExpandedState?.ApplySummary(ss.GetDictOrEmpty("life_support_state_summary"));
                HullIntegrityState?.ApplySummary(ss.GetDictOrEmpty("hull_integrity_summary"));
                HullWebState?.ApplySummary(ss.GetDictOrEmpty("web_infestation_summary"));
                FireSuppressionState?.ApplySummary(ss.GetDictOrEmpty("fire_suppression_summary"));
                ExtinguisherState?.ApplySummary(ss.GetDictOrEmpty("extinguisher_summary"));
                PropulsionExpandedState?.ApplySummary(ss.GetDictOrEmpty("propulsion_state_summary"));
                SustenanceState?.ApplySummary(ss.GetDictOrEmpty("sustenance_state_summary"));
                CompletedObjectiveTypes.Clear();
                foreach (object t in ss.GetArrayOrEmpty("completed_objective_types"))
                    CompletedObjectiveTypes[V.Str(t)] = true;
                ObjectiveCompletionCount = Math.Max(0L, snapshot.CurrentObjectiveSequence - 1);
                foreach (object completedType in CompletedObjectiveTypes.Keys)
                    ApplyShipSystemsConsequences(V.Str(completedType));
                RecomputeExpandedShipSystems(0.0);
                RefreshRouteControlFromShipSystems();
                OxygenState?.ApplyShipSystemsSummary(ManagerCompatSummary());
            }
            if (RouteControlState != null && !snapshot.RouteControlSummary.IsEmpty)
            {
                RouteControlState.ApplySummary(snapshot.RouteControlSummary);
                ApplyRouteGateSceneState();
            }
            if (OxygenState != null && !snapshot.OxygenSummary.IsEmpty)
            {
                OxygenState.ApplySummary(snapshot.OxygenSummary);
                RefreshOxygenState(true, 0.0);
            }
            if (CraftingState != null && !snapshot.CraftingSummary.IsEmpty)
            {
                CraftingState.ApplySummary(snapshot.CraftingSummary);
                FieldCraftingState?.ApplySummary(snapshot.CraftingSummary);
            }
            if (MaterialState != null && !snapshot.MaterialSummary.IsEmpty)
                MaterialState.ApplySummary(snapshot.MaterialSummary);
            if (InventoryState != null && !snapshot.InventorySummary.IsEmpty)
            {
                InventoryState.ApplySummary(snapshot.InventorySummary);
                _lastWeaponHotbarText = V.Str(snapshot.InventorySummary.Get("combat_hotbar_text", _lastWeaponHotbarText));
                if (ThreatManager != null && snapshot.InventorySummary.Get("threat_summary", null) is GdDict threatSummary && !threatSummary.IsEmpty)
                {
                    ThreatManager.ApplySummary(threatSummary);
                    SyncCurrentShipCombatSummary();
                }
            }
            if (ElectricalArcState != null && !snapshot.ElectricalArcSummary.IsEmpty)
            {
                ElectricalArcState.ApplySummary(snapshot.ElectricalArcSummary);
                RefreshArcState(true);
                SyncCurrentShipArcSummary();
            }
            if (ObjectiveProgressState != null && !snapshot.ObjectiveProgressSummary.IsEmpty)
                ObjectiveProgressState.ApplySummary(snapshot.ObjectiveProgressSummary);
            if (PlayerProgression != null && !snapshot.PlayerProgressionSummary.IsEmpty)
                PlayerProgression.ApplySummary(snapshot.PlayerProgressionSummary);
            if (SkillTreeState != null && !snapshot.SkillTreeSummary.IsEmpty)
                SkillTreeState.ApplySummary(snapshot.SkillTreeSummary);
            if (SettingsState != null && !snapshot.SettingsSummary.IsEmpty)
                SettingsState.ApplySummary(snapshot.SettingsSummary);
            if (AudioManager != null && !snapshot.AudioSummary.IsEmpty)
                AudioManager.ApplySummary(snapshot.AudioSummary);
            ReconcileCaptionsWithSettings();
            if (VitalsState != null && !snapshot.VitalsSummary.IsEmpty)
                VitalsState.ApplySummary(snapshot.VitalsSummary);
            if (SanityState != null && !snapshot.SanitySummary.IsEmpty)
                SanityState.ApplySummary(snapshot.SanitySummary);
            if (RadiationState != null && !snapshot.RadiationSummary.IsEmpty)
                RadiationState.ApplySummary(snapshot.RadiationSummary);
            if (BodyTemperatureState != null && !snapshot.TemperatureSummary.IsEmpty)
                BodyTemperatureState.ApplySummary(snapshot.TemperatureSummary);
            if (StatusEffectsState != null && !snapshot.StatusEffectsSummary.IsEmpty)
                StatusEffectsState.ApplySummary(snapshot.StatusEffectsSummary);
            if (HallucinationDirector != null && !snapshot.HallucinationSummary.IsEmpty)
                HallucinationDirector.ApplySummary(snapshot.HallucinationSummary);
            if (SpoilageState != null && !snapshot.SpoilageSummary.IsEmpty)
                SpoilageState.ApplySummary(snapshot.SpoilageSummary);
            if (HydroponicsState != null && !snapshot.HydroponicsSummary.IsEmpty)
                HydroponicsState.ApplySummary(snapshot.HydroponicsSummary);
            if (WaterRecyclerState != null && !snapshot.WaterRecyclerSummary.IsEmpty)
                WaterRecyclerState.ApplySummary(snapshot.WaterRecyclerSummary);
            if (ConsumableState != null && !snapshot.ConsumableSummary.IsEmpty)
                ConsumableState.ApplySummary(snapshot.ConsumableSummary);
            if (MedicineState != null && !snapshot.MedicineSummary.IsEmpty)
                MedicineState.ApplySummary(snapshot.MedicineSummary);
            if (StimulantState != null && !snapshot.StimulantSummary.IsEmpty)
                StimulantState.ApplySummary(snapshot.StimulantSummary);
            if (AddictionState != null && !snapshot.AddictionSummary.IsEmpty)
                AddictionState.ApplySummary(snapshot.AddictionSummary);
            if (AmmoState != null && !snapshot.AmmoSummary.IsEmpty)
                AmmoState.ApplySummary(snapshot.AmmoSummary);
            if (UtilityItemState != null && !snapshot.UtilitySummary.IsEmpty)
                UtilityItemState.ApplySummary(snapshot.UtilitySummary);
            if (ModuleIntegrityMap != null)
            {
                GdDict snapLayout = new GdDict();
                if (CurrentShip != null && CurrentShip.BuiltLayout != null)
                    snapLayout = CurrentShip.BuiltLayout;
                else if (Loader != null && Loader.IsValid)
                    snapLayout = Loader.GetLayoutCopy();
                if (!snapLayout.IsEmpty)
                    ModuleIntegrityConsequences.SeedMapFromCompiledLayout(ModuleIntegrityMap, snapLayout, false);
                if (!snapshot.ModuleIntegritySummary.IsEmpty)
                {
                    GdDict packed = snapshot.ModuleIntegritySummary;
                    if (packed.Get("deltas", null) is GdArray deltas)
                        ModuleIntegrityMap.ApplySparseDeltas(deltas);
                    else
                        ModuleIntegrityMap.ApplySummary(packed);
                    ApplyModuleIntegrityStateToScene();
                    if (CurrentShip != null)
                        CurrentShip.ModuleIntegritySummary = packed.DeepCopy();
                }
            }
            if (ComponentPlacementState != null && !snapshot.ComponentPlacementSummary.IsEmpty)
            {
                ComponentPlacementState.ApplySummary(snapshot.ComponentPlacementSummary);
                if (CurrentShip != null)
                    CurrentShip.ComponentPlacementSummary = snapshot.ComponentPlacementSummary.DeepCopy();
                RebuildComponentMarkers();
            }
            if (WorkActionDriver != null && !snapshot.WorkActionSummary.IsEmpty)
            {
                GdDict waPack = snapshot.WorkActionSummary;
                if (waPack.GetBool("active") && waPack.Get("summary", null) is GdDict waSummary)
                {
                    WorkActionDriver.Work = new WorkActionState();
                    WorkActionDriver.Work.ApplySummary(waSummary);
                    _workRequiresHold = false;
                }
            }
            if (ShipModificationState != null && !snapshot.ShipModificationSummary.IsEmpty)
            {
                ShipModificationState.ApplySummary(snapshot.ShipModificationSummary);
                ReapplyShipModRuntimeEffects();
            }
            ApplyPortSnapshotExtensions(snapshot);
            EnsureConsumableHotbarAssignments();
            RefreshConsumableUi();
            ReconcileJunctionCalibratorMarkerAfterReload();
            BuildRepairPoints();
            BuildBreachSealPoints();
            BuildFireZones();
            BuildFireSuppressionPoints();
            BuildExtinguisherRechargePort();
            BuildCraftingStations();
            BuildProductionStations();
            CurrentObjectiveSequence = Math.Max(1L, snapshot.CurrentObjectiveSequence);
            ActivateCurrentObjective();
            RefreshWeaponHotbar();
            if (HasPlayer && snapshot.PlayerPosition.Count >= 3)
                SetPlayerPosition(new Vec3(V.F64(snapshot.PlayerPosition[0]), V.F64(snapshot.PlayerPosition[1]), V.F64(snapshot.PlayerPosition[2])));
            LastSavedSnapshot = snapshot;
            _isReloading = false;
            Log.Info("PLAYABLE SHIP LOADED sequence=" + CurrentObjectiveSequence);
            return true;
        }

        /// <summary>
        /// Unity port (gate2-current-run-5): restores the keys Godot's run snapshot did not carry. An empty value (a
        /// migrated Godot save) means "not saved": wounds and tutorial state stay at the fresh state the reload built, the
        /// chart is cleared, and equipment / home loot / home cargo / run context keep their live values.
        /// </summary>
        void ApplyPortSnapshotExtensions(RunSnapshot snapshot)
        {
            ApplyRunContextSummary(snapshot.RunContext);
            if (WoundState != null && !snapshot.WoundSummary.IsEmpty)
            {
                WoundState.ApplySummary(snapshot.WoundSummary);
                Events.RaiseWoundsChanged(WoundState);
            }
            WebChartState?.ApplySummary(snapshot.WebChartSummary);
            if (TutorialState != null && !snapshot.TutorialSummary.IsEmpty)
            {
                TutorialState.ApplySummary(snapshot.TutorialSummary);
                Events.RaiseTutorialStateReset(TutorialState);
            }
            if (EquipmentState != null && !snapshot.EquipmentSummary.IsEmpty)
            {
                EquipmentState.ApplySummary(snapshot.EquipmentSummary);
                RecomputePlayerEncumbrance();
                RefreshWeaponHotbar();
            }
            if (HomeShip != null)
            {
                bool rebuildLoot = false;
                if (!snapshot.HomeLootedContainers.IsEmpty)
                {
                    HomeShip.LootedContainerIds = snapshot.HomeLootedContainers.ShallowCopy();
                    rebuildLoot = true;
                }
                if (!snapshot.HomeShipInventory.IsEmpty)
                    HomeShip.GetInventory().ApplySummary(snapshot.HomeShipInventory);
                if (rebuildLoot && !AwayFromStart)
                    BuildLootContainers();
            }
        }

        /// <summary>
        /// <c>_reset_runtime_for_reload()</c> (model half): return home when away (freeing every derelict root and severing
        /// dock cycles), tear down every interaction node, zone and HUD binding, drop the lifeboat + visited ships, and
        /// re-configure every model to its fresh state so the reload can re-apply the snapshot.
        /// </summary>
        void ResetRuntimeForReload()
        {
            if (AwayFromStart)
            {
                foreach (ShipInstance inst in AllKnownShips())
                {
                    if (inst == null || inst.MarkerId == "")
                        continue;
                    if (RootValid(inst.SceneRoot))
                        ShipHost?.FreeShipRoot(inst.SceneRoot);
                    inst.SceneRoot = null;
                }
                foreach (ShipInstance inst in AllKnownShips())
                {
                    if (inst == null)
                        continue;
                    inst.ParentShip = null;
                    inst.DockedShips.Clear();
                    if (inst.Hangar != null)
                    {
                        for (int i = 0; i < inst.Hangar.Slots.Count; i++)
                            inst.Hangar.Slots[i] = "";
                    }
                }
                AwayFromStart = false;
                ResetTooltipFocus();
                CurrentOccupancy = HomeShip;
                ClearDerelictObjectives();
                ClearLootContainers();
                ClearRepairPoints();
                ClearBreachSealPoints();
                ClearSealedHatches();
            }
            CurrentShip = null;
            // RUNTIME: player + camera rig freed (the scene respawns them on the reload's _on_ship_loaded).
            Scene?.DespawnPlayer();
            foreach (ObjectiveInteractable it in Interactables)
                Despawn(it);
            foreach (SessionZone gate in RouteGateNodes)
                Events.RaiseZoneDespawned(gate);
            foreach (SessionZone z in BreachZoneNodes)
                Events.RaiseZoneDespawned(z);
            if (ToolPickup != null)
                Despawn(ToolPickup);
            if (JunctionCalibratorPickup != null)
                Despawn(JunctionCalibratorPickup);
            HallucinationManager?.ClearAll();
            ClearFireZones();
            ClearFireSuppressionPoints();
            ClearExtinguisherRechargePort();
            foreach (SessionZone z in ArcZoneNodes)
                Events.RaiseZoneDespawned(z);
            if (LifeboatShip != null && RootValid(LifeboatShip.SceneRoot))
                ShipHost?.FreeShipRoot(LifeboatShip.SceneRoot);
            if (LifeboatShip != null)
                LifeboatShip.ParentShip = null;
            HomeShip?.DockedShips.Remove(LifeboatShip);
            LifeboatShip = null;
            PilotedShip = null;
            ClearDockBarriers();
            ClearBridgeTerminals();
            ClearHangarControls();
            ClearCargoHoldControls();
            ClearCartControls();
            GrabbedCart = null;
            // RUNTIME: hud_layer + tracker + panels freed (rebuilt by the reload's _build_hud_layer).
            Interactables.Clear();
            SequenceInteractables.Clear();
            RouteGateNodes.Clear();
            VisitedShips.Clear();
            ObjectiveCompletionCount = 0;
            SliceComplete = false;
            if (ShipSystemsManager != null)
            {
                ShipBlueprint bpReset = LoadBlueprintForSystems();
                ShipSystemsManager.Configure(ShipSystemsManager.LoadDefinitions(), bpReset.ShipCondition, bpReset.SeedValue);
                ApplyLifeboatOpeningDamage();
            }
            ConfigurePlayerProgression();
            CompletedObjectiveTypes.Clear();
            RouteControlState?.ConfigureFromBlockedRoutes(new GdArray());
            OxygenState?.Configure(new GdDict
            {
                { "zone_ids", new GdArray() },
                { "max_oxygen", OxygenState.DEFAULT_MAX_OXYGEN },
                { "drain_rate", OxygenState.DEFAULT_DRAIN_RATE },
                { "regen_rate", OxygenState.DEFAULT_REGEN_RATE },
                { "recovery_threshold", OxygenState.DEFAULT_RECOVERY_THRESHOLD },
                { "safe_threshold", OxygenState.DEFAULT_SAFE_THRESHOLD },
            });
            InventoryState?.Reset();
            FireSuppressionState?.Configure(LoadJsonDict(SHIP_SUBSYSTEM_TUNING_PATH).GetDictOrEmpty("fire_suppression"));
            ElectricalArcState?.Configure(new GdDict
            {
                { "zone_ids", new GdArray() },
                { "arcing_duration", ElectricalArcState.DEFAULT_ARCING_DURATION },
                { "discharged_duration", ElectricalArcState.DEFAULT_DISCHARGED_DURATION },
            });
            VitalsState?.Configure(new GdDict());
            SanityState?.Configure(new GdDict());
            RadiationState?.Configure(new GdDict());
            BodyTemperatureState?.Configure(new GdDict());
            StatusEffectsState?.Configure(new GdDict());
            ObjectiveProgressState?.Reset();
            BreachZoneNodes.Clear();
            UnsafeRoomMarkerVisible = false;
            ArcZoneNodes.Clear();
            ArcZoneResolvedRoomIds.Clear();
            ToolPickup = null;
            ArcZoneResolvedRoomId = "";
            JunctionCalibratorPickup = null;
            SequenceKinds.Clear();
            _autosaveRunSeconds = 0.0;
            _lastAutosaveResult = new GdDict();
            AutosavePolicy?.Reset();
        }
    }
}
