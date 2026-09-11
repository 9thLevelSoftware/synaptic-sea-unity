// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: the objective chain _on_interactable_completed
// (8178-8313), the derelict objective loop (2901-3001, 6408-6485), the junction calibrator (9883-9997), and the
// objective-completion verbs that replaced complete_*_for_validation (828-870).
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary><c>_on_interactable_completed(...)</c>: the home objective chain.</summary>
        void OnInteractableCompleted(string interactionId, string objectiveId, long sequence, string objectiveType, string roomId, string stepId)
        {
            if (sequence != CurrentObjectiveSequence)
                return;
            // REQ-014: the pre-calibration required_steps decides the multi-step path (blocking finding A).
            long preRequiredSteps = 1;
            if (ObjectiveProgressState != null)
                preRequiredSteps = V.I64(ObjectiveProgressState.GetStepProgress(sequence).Get("required_steps", 1L));
            ConsumeJunctionCalibratorIfEligible(sequence);
            bool isMultiStep = preRequiredSteps > 1;
            if (isMultiStep)
            {
                bool stepChanged = ObjectiveProgressState.CompleteStep(sequence, stepId);
                if (!stepChanged)
                    return;
                GdDict progress = ObjectiveProgressState.GetStepProgress(sequence);
                Events.RaiseTrackerStepProgress(sequence, progress);
                Log.Info("OBJECTIVE STEP COMPLETED sequence=" + sequence + " step=" + stepId + " progress="
                         + V.I64(progress.Get("completed_steps", 0L)) + "/" + V.I64(progress.Get("required_steps", 1L)));
                if (!ObjectiveProgressState.IsSequenceComplete(sequence))
                    return;
            }
            ObjectiveCompletionCount += 1;
            if (ObjectiveCompletionCount == 1)
                TryUnlockAchievement("objective_completed", "objectives[0]");
            TryUnlockAchievement("objective_completed", objectiveType);
            if (objectiveType == "stabilize_reactor")
            {
                TryUnlockAchievement("reactor_stabilized", "reactor");
                PlaySfx(AudioEventSeam.META_REACTOR_HUM);
            }
            if (ShipSystemsManager != null)
            {
                CompletedObjectiveTypes[objectiveType] = true;
                foreach (object pairObj in OBJECTIVE_REPAIR_MAP.Get(objectiveType, new GdArray()) as GdArray ?? new GdArray())
                {
                    var pair = (GdArray)pairObj;
                    ShipSystemsManager.ForceRepair(V.Str(pair[0]), V.Str(pair[1]));
                }
                if (PlayerProgression != null && (objectiveType == "restore_systems" || objectiveType == "stabilize_reactor"))
                {
                    // Domain 6 (WI-5): the bus is the single XP ingest path (repair_full_system = 120).
                    TrainingEventBus?.Emit("repair_full_system", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), PlayerProgression);
                }
                GdDict compat = ManagerCompatSummary();
                ApplyShipSystemsConsequences(objectiveType);
                RefreshRouteControlFromShipSystems();
                if (OxygenState != null)
                {
                    OxygenState.ApplyShipSystemsSummary(compat);
                    RefreshOxygenState(false, 0.0);
                }
                GdDict routeSummary = GetRouteControlSummary();
                Log.Info("SHIP SYSTEM UPDATED sequence=" + sequence + " type=" + objectiveType
                         + " power=" + V.I64(compat.Get("power_percent", 0L))
                         + " reactor=" + V.I64(compat.Get("reactor_stability_percent", 0L))
                         + " extraction=" + (compat.GetBool("extraction_unlocked") ? "true" : "false")
                         + " route_opened=" + V.I64(routeSummary.Get("opened_gate_count", 0L))
                         + " blockers=" + V.I64(routeSummary.Get("active_blocker_count", 0L)));
            }
            Events.RaiseTrackerCompleted(sequence);
            EmitObjectiveTraining(objectiveType, roomId, objectiveId);
            Log.Info("PLAYABLE INTERACTION interaction=" + interactionId + " objective=" + objectiveId + " sequence=" + sequence
                     + " type=" + objectiveType + " room=" + roomId);
            PlayableInteractionCompleted?.Invoke(interactionId, objectiveId, sequence, objectiveType, roomId);
            long totalSequences = SequenceInteractables.Count;
            if (ObjectiveCompletionCount >= totalSequences)
            {
                SliceComplete = true;
                CurrentObjectiveSequence = totalSequences + 1;
                Events.RaiseTrackerRunComplete();
                TryUnlockAchievement("run_complete", "complete");
                Log.Info("PLAYABLE SLICE COMPLETE objectives_completed=" + ObjectiveCompletionCount);
                ApplyMetaPayoutAndPersist("completion");
                if (SaveLoadService != null)
                {
                    SaveLoadService.DeleteCurrentRun();
                    foreach (object slotId in SaveSlotState.AutosaveSlotIds)
                        SaveLoadService.DeleteSlot(V.Str(slotId));
                }
                GdDict objectiveCompletionSummary = GetSliceCompletionSummary();
                objectiveCompletionSummary["reason"] = "complete";
                PlayableSliceCompleted?.Invoke(objectiveCompletionSummary);
                return;
            }
            // REQ-012: advance FIRST so the checkpoint captures the resumed sequence, then auto-save.
            CurrentObjectiveSequence += 1;
            PlaySfx(AudioEventSeam.UI_OBJECTIVE_ADVANCE);
            AutoSaveCurrentRun();
            ActivateCurrentObjective();
        }

        void ApplyShipSystemsConsequences(string objectiveType)
        {
            if (objectiveType == "restore_systems")
                ClearBlockedAffordances();
        }

        void ClearBlockedAffordances()
        {
            BlockedAffordancesCleared = true;
            Events.RaiseBlockedAffordancesCleared();
        }

        /// <summary>Stream E: objective completions mapped to training events (discover_room once per ship+room).</summary>
        void EmitObjectiveTraining(string objectiveType, string roomId, string objectiveId)
        {
            if (!string.IsNullOrEmpty(roomId))
            {
                string shipKey = "home";
                if (CurrentShip != null)
                {
                    shipKey = CurrentShip.MarkerId;
                    if (shipKey.Length == 0)
                        shipKey = CurrentShip.ShipId;
                    if (shipKey.Length == 0)
                        shipKey = "home";
                }
                string discKey = shipKey + ":" + roomId;
                if (!DiscoveredRoomIds.Has(discKey))
                {
                    DiscoveredRoomIds[discKey] = true;
                    EmitTrainingEvent("discover_room", discKey);
                    PlaySfx(AudioEventSeam.UI_OBJECTIVE_ADVANCE);
                }
            }
            if (objectiveType == "download_logs")
            {
                EmitTrainingEvent("extract_data", !string.IsNullOrEmpty(objectiveId) ? objectiveId : "download_logs");
                PlaySfx(AudioEventSeam.SFX_TOOL_USE);
            }
            if (objectiveType == "restore_systems" || objectiveType == "stabilize_reactor")
            {
                EmitTrainingEvent("inspire_crew", objectiveType);
                PlaySfx(AudioEventSeam.UI_OBJECTIVE_ADVANCE);
            }
        }

        /// <summary>
        /// Completes every interactable of the current sequence through the real interaction path (was
        /// <c>complete_objective_sequence_for_validation</c>): teleport onto each, mark the Area3D overlap, interact via
        /// the dispatcher, retrying the interactable directly when a co-located affordance claimed the request.
        /// </summary>
        public bool CompleteObjectiveSequence(long sequence)
        {
            if (sequence != CurrentObjectiveSequence)
                return false;
            if (!SequenceInteractables.TryGetValue(sequence, out List<ObjectiveInteractable> group) || group.Count == 0)
                return false;
            foreach (ObjectiveInteractable interactable in new List<ObjectiveInteractable>(group))
            {
                TeleportPlayer(interactable.GlobalPosition);
                interactable.SetValidationPlayerInRange(true);
                if (interactable.Completed)
                    continue;
                RequestInteract();
                if (!interactable.Completed)
                    interactable.TryInteract(PlayerPos);
            }
            return CurrentObjectiveSequence > sequence || SliceComplete;
        }

        /// <summary>Completes every home sequence in order (was <c>complete_all_objectives_for_validation</c>).</summary>
        public bool CompleteAllObjectives()
        {
            long expectedTotal = SequenceInteractables.Count;
            if (expectedTotal <= 0)
                return false;
            while (!SliceComplete)
            {
                long sequence = CurrentObjectiveSequence;
                if (sequence > expectedTotal)
                    break;
                if (!CompleteObjectiveSequence(sequence))
                    return false;
            }
            return SliceComplete && ObjectiveCompletionCount == expectedTotal;
        }

        /// <summary>Teleport the player onto the first interactable of a sequence (was <c>teleport_player_to_objective_for_validation</c>).</summary>
        public bool TeleportPlayerToObjective(long sequence)
        {
            if (!HasPlayer)
                return false;
            ObjectiveInteractable it = GetInteractableBySequence(sequence);
            if (it == null)
                return false;
            TeleportPlayer(it.GlobalPosition);
            return true;
        }

        /// <summary>Teleport to a loader room center + spawn height (was <c>teleport_player_to_room_for_validation</c>).</summary>
        public bool TeleportPlayerToRoom(string roomId)
        {
            if (!HasPlayer || Loader == null)
                return false;
            Vec3 roomCenter = Loader.GetRoomCenter(roomId);
            if (roomCenter == Vec3.Inf)
                return false;
            TeleportPlayer(ToGlobal(Loader, roomCenter) + new Vec3(0.0f, (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0.0f));
            return true;
        }

        // ------------------------------------------------------------------ derelict objectives
        /// <summary>Builds the active derelict's objective interactables and restores state from its controller.</summary>
        void BuildDerelictObjectives()
        {
            ClearDerelictObjectives();
            _salvageLootTables.Clear();
            if (CurrentShip == null || CurrentShip.MarkerId == "")
                return;
            if (!(CurrentShip.SceneRoot is IShipLoaderView activeLoader) || !activeLoader.IsValid)
                return;
            GdArray specs = activeLoader.GetObjectiveSpecsCopy();
            DerelictObjectiveController controller = CurrentShip.GetObjectiveController();
            controller.Configure(specs);
            foreach (object specObj in specs)
            {
                if (!(specObj is GdDict spec))
                    continue;
                long sequence = V.I64(spec.Get("sequence", 0L));
                if (sequence <= 0)
                    continue;
                if (V.Str(spec.Get("type", "")) == "salvage")
                    _salvageLootTables[V.Str(spec.Get("id", ""))] = V.Str(spec.Get("loot_table", "salvage_cargo"));
                GdArray steps = spec.Get("steps", new GdArray()) as GdArray ?? new GdArray();
                if (V.Str(spec.Get("kind", "single")) == "repair_junction" && steps.Count > 1)
                {
                    foreach (object stepObj in steps)
                    {
                        if (!(stepObj is GdDict step))
                            continue;
                        if (!(step.Get("position", Vec3.Inf) is Vec3 stepPos) || stepPos == Vec3.Inf)
                            continue;
                        var stepInteractable = new ObjectiveInteractable();
                        stepInteractable.ConfigureFromStep(spec, step, stepPos, 1.8);
                        stepInteractable.InteractionCompleted += OnDerelictInteractableCompleted;
                        if (controller.IsStepComplete(sequence, V.Str(step.Get("step_id", ""))))
                        {
                            stepInteractable.Completed = true;
                            stepInteractable.SetActive(false);
                        }
                        stepInteractable.Parent = CurrentShip.SceneRoot;
                        DerelictInteractables.Add(Spawn(stepInteractable));
                    }
                }
                else
                {
                    if (!(spec.Get("position", Vec3.Inf) is Vec3 pos))
                        continue;
                    var interactable = new ObjectiveInteractable();
                    interactable.ConfigureFromObjective(spec, pos, 1.8);
                    interactable.InteractionCompleted += OnDerelictInteractableCompleted;
                    if (controller.IsObjectiveComplete(sequence))
                    {
                        interactable.Completed = true;
                        interactable.SetActive(false);
                    }
                    interactable.Parent = CurrentShip.SceneRoot;
                    DerelictInteractables.Add(Spawn(interactable));
                }
            }
            Events.RaiseTrackerObjectivesSet(specs);
            RefreshDerelictTracker();
        }

        /// <summary>Mirrors the active derelict's controller state into the tracker (idempotent).</summary>
        void RefreshDerelictTracker()
        {
            if (CurrentShip == null || CurrentShip.MarkerId == "")
                return;
            DerelictObjectiveController controller = CurrentShip.GetObjectiveController();
            long firstIncomplete = -1;
            foreach (ObjectiveInteractable it in DerelictInteractables)
            {
                if (!it.IsValid)
                    continue;
                long seq = it.Sequence;
                if (controller.IsObjectiveComplete(seq))
                    Events.RaiseTrackerCompleted(seq);
                else if (firstIncomplete < 0 || seq < firstIncomplete)
                    firstIncomplete = seq;
            }
            if (controller.IsCleared())
                Events.RaiseTrackerRunComplete();
            else if (firstIncomplete > 0)
                Events.RaiseTrackerCurrentSequence(firstIncomplete);
        }

        void ClearDerelictObjectives()
        {
            foreach (ObjectiveInteractable it in DerelictInteractables)
                Despawn(it);
            DerelictInteractables.Clear();
        }

        /// <summary>Routes a derelict interactable completion to the active ship's controller (+ salvage loot, training).</summary>
        void OnDerelictInteractableCompleted(string interactionId, string objectiveId, long sequence, string objectiveType, string roomId, string stepId)
        {
            if (CurrentShip == null)
                return;
            DerelictObjectiveController controller = CurrentShip.GetObjectiveController();
            if (!controller.Complete(sequence, stepId))
                return;
            RefreshDerelictTracker();
            if (!controller.IsObjectiveComplete(sequence))
                return;
            if (objectiveType == "salvage" && _salvageLootTables.Has(objectiveId))
            {
                string seedSource = CurrentShip.MarkerId + ":" + objectiveId;
                GdArray rolled = LootDistribution.RollWithUniqueState(V.Str(_salvageLootTables[objectiveId]), seedSource, _loot_tables, new GdDict
                {
                    { "biome_id", ResolveCurrentLootBiomeId() },
                    { "loot_quality_modifier", ResolveCurrentLootQualityModifier() },
                    { "depth", ResolveCurrentLootDepth() },
                    { "condition", ResolveCurrentLootCondition() },
                    { "container_kind", "salvage_objective" },
                    { "item_definitions", ItemDefs.LoadDefinitions() },
                }, UniqueItemState);
                var granted = new GdArray();
                foreach (object entryObj in rolled)
                {
                    var entry = (GdDict)entryObj;
                    long added = InventoryState.AddItem(V.Str(entry.Get("item_id", "")), V.I64(entry.Get("quantity", 0L)));
                    if (added > 0)
                    {
                        GdDict grantedEntry = entry.DeepCopy();
                        grantedEntry["quantity"] = added;
                        granted.Add(grantedEntry);
                    }
                }
                PostprocessLootGrants(granted, objectiveId, null);
                RefreshInventoryHud();
                RecomputePlayerEncumbrance();
            }
            EmitObjectiveTraining(objectiveType, roomId, objectiveId);
            Log.Info("DERELICT OBJECTIVE COMPLETE marker=" + CurrentShip.MarkerId + " sequence=" + sequence + " type=" + objectiveType
                     + " cleared=" + (controller.IsCleared() ? "true" : "false"));
            TriggerTutorial("objective_completed", objectiveType);
        }

        /// <summary>Complete a derelict objective by sequence through its interactable (was <c>complete_derelict_objective_for_validation</c>).</summary>
        public bool CompleteDerelictObjective(long sequence)
        {
            foreach (ObjectiveInteractable it in DerelictInteractables)
            {
                if (it.IsValid && it.Sequence == sequence && !it.Completed)
                {
                    it.SetValidationPlayerInRange(true);
                    return it.TryInteract(PlayerPos);
                }
            }
            return false;
        }

        void RefreshHomeTrackerCompleted()
        {
            for (long s = 1; s < CurrentObjectiveSequence; s++)
                Events.RaiseTrackerCompleted(s);
            Events.RaiseTrackerCurrentSequence(CurrentObjectiveSequence);
            if (SliceComplete)
                Events.RaiseTrackerRunComplete();
        }

        // ------------------------------------------------------------------ tool pickups + junction calibrator
        void BuildToolPickup()
        {
            if (ToolPickup != null)
                Despawn(ToolPickup);
            ToolPickup = null;
            Vec3 worldPosition = ResolveToolPickupWorldPosition();
            var pickup = new ToolPickup();
            pickup.Configure("portable_oxygen_pump", InventoryState, worldPosition, TOOL_PICKUP_INTERACTION_RADIUS);
            pickup.ToolAcquired += OnToolPickupAcquired;
            ToolPickup = Spawn(pickup);
        }

        Vec3 ResolveToolPickupWorldPosition()
        {
            if (Loader != null)
            {
                Vec3 roomCenter = Loader.GetRoomCenter("tool_storage_01");
                if (roomCenter != Vec3.Inf)
                    return ToGlobal(Loader, roomCenter) + new Vec3(0.0f, (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0.0f);
            }
            if (HasPlayer)
                return PlayerPos + TOOL_PICKUP_FALLBACK_OFFSET;
            if (Loader != null)
                return Loader.GetStartTransform().Origin + TOOL_PICKUP_FALLBACK_OFFSET + new Vec3(0.0f, (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0.0f);
            return TOOL_PICKUP_FALLBACK_OFFSET;
        }

        void OnToolPickupAcquired(string toolId)
        {
            RefreshTrackerSystemStatusLines();
            Log.Info("PLAYABLE TOOL ACQUIRED tool_id=" + toolId);
            PlaySfx(AudioEventSeam.SFX_TOOL_PICKUP);
            RecomputePlayerEncumbrance();
            EnsureConsumableHotbarAssignments();
            RefreshConsumableUi();
            TryUnlockAchievement("tool_acquired", toolId);
        }

        /// <summary>REQ-RL-003: resolve (trigger_event, trigger_target) against the per-run achievement catalog.</summary>
        void TryUnlockAchievement(string triggerEvent, string triggerTarget)
        {
            if (AchievementState == null || string.IsNullOrEmpty(triggerEvent))
                return;
            string unlockedId = AchievementState.UnlockForTrigger(triggerEvent, triggerTarget) ?? "";
            if (unlockedId.Length > 0)
            {
                Log.Info("ACHIEVEMENT UNLOCKED trigger=" + triggerEvent + " target=" + triggerTarget + " id=" + unlockedId);
                PlaySfx(AudioEventSeam.UI_OBJECTIVE_ADVANCE);
            }
        }

        void BuildJunctionCalibratorPickup()
        {
            Vec3 worldPosition = ResolveJunctionCalibratorWorldPosition();
            var pickup = new ToolPickup();
            pickup.Configure("junction_calibrator", InventoryState, worldPosition, JUNCTION_CALIBRATOR_INTERACTION_RADIUS);
            pickup.ToolAcquired += OnToolPickupAcquired;
            JunctionCalibratorPickup = Spawn(pickup);
        }

        Vec3 ResolveJunctionCalibratorWorldPosition()
        {
            if (Loader != null)
            {
                Vec3 roomCenter = Loader.GetRoomCenter(JUNCTION_CALIBRATOR_FALLBACK_ROOM_ID);
                if (roomCenter != Vec3.Inf)
                    return ToGlobal(Loader, roomCenter) + new Vec3(0.0f, (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0.0f);
            }
            if (HasPlayer)
                return PlayerPos + JUNCTION_CALIBRATOR_FALLBACK_OFFSET;
            if (Loader != null)
                return Loader.GetStartTransform().Origin + JUNCTION_CALIBRATOR_FALLBACK_OFFSET + new Vec3(0.0f, (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0.0f);
            return JUNCTION_CALIBRATOR_FALLBACK_OFFSET;
        }

        /// <summary>REQ-014: hide the calibrator marker after a reload when it is already carried or spent.</summary>
        void ReconcileJunctionCalibratorMarkerAfterReload()
        {
            if (JunctionCalibratorPickup == null)
                return;
            bool carried = InventoryState != null && InventoryState.HasTool("junction_calibrator");
            bool spent = false;
            if (ObjectiveProgressState != null)
            {
                foreach (object seqV in ObjectiveProgressState.GetSummary().Keys)
                {
                    long seq = V.I64(seqV);
                    if (seq <= 0)
                        continue;
                    if (ObjectiveProgressState.HasCalibratorApplied(seq))
                    {
                        spent = true;
                        break;
                    }
                }
            }
            if (carried || spent)
                JunctionCalibratorPickup.SetMarkerVisible(false);
            RefreshTrackerSystemStatusLines();
        }

        /// <summary>REQ-014: consume a carried calibrator against a repair_junction sequence (before the step completes).</summary>
        void ConsumeJunctionCalibratorIfEligible(long sequence)
        {
            if (sequence <= 0)
                return;
            if (InventoryState == null || ObjectiveProgressState == null)
                return;
            if (V.Str(SequenceKinds.Get(sequence, "single")) != "repair_junction")
                return;
            if (!InventoryState.HasTool("junction_calibrator"))
                return;
            if (!ObjectiveProgressState.ApplyJunctionCalibrator(sequence))
                return;
            if (!InventoryState.RemoveTool("junction_calibrator"))
            {
                GdDict before = ObjectiveProgressState.GetStepProgress(sequence);
                GdDict after = before.ShallowCopy();
                after["required_steps"] = V.I64(before.Get("required_steps", 1L)) + 1;
                after["calibrator_applied"] = false;
                ObjectiveProgressState.ApplySummary(new GdDict
                {
                    {
                        sequence, new GdDict
                        {
                            { "objective_type", V.Str(after.Get("objective_type", "")) },
                            { "required_steps", V.I64(after.Get("required_steps", 1L)) },
                            { "completed_steps", V.I64(before.Get("completed_steps", 0L)) },
                            { "completed_step_ids", (before.Get("completed_step_ids", new GdArray()) as GdArray ?? new GdArray()).ShallowCopy() },
                            { "complete", V.Bool(before.Get("complete", false)) },
                            { "calibrator_applied", false },
                        }
                    },
                });
                Log.Warning("PlayableGeneratedShip: junction_calibrator consumed by model but not removed from inventory; rolled back");
            }
            JunctionCalibratorPickup?.SetMarkerVisible(false);
            PlaySfx(AudioEventSeam.SFX_TOOL_USE);
            RefreshTrackerSystemStatusLines();
            GdDict progress = ObjectiveProgressState.GetStepProgress(sequence);
            Log.Info("JUNCTION CALIBRATOR APPLIED sequence=" + sequence + " required_steps=" + V.I64(progress.Get("required_steps", 1L))
                     + " completed_steps=" + V.I64(progress.Get("completed_steps", 0L)));
        }

        /// <summary>Acquire a pickup through the interaction path (was <c>acquire_tool_for_validation</c>).</summary>
        public bool AcquireTool(string toolId)
        {
            ToolPickup pickup = ToolPickup != null && ToolPickup.ToolId == toolId ? ToolPickup
                : (JunctionCalibratorPickup != null && JunctionCalibratorPickup.ToolId == toolId ? JunctionCalibratorPickup : null);
            if (pickup == null || InventoryState == null)
                return false;
            TeleportPlayer(pickup.GlobalPosition);
            pickup.SetValidationPlayerInRange(true);
            RequestInteract();
            return InventoryState.HasTool(toolId);
        }
    }
}
