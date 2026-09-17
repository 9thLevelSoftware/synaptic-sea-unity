// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _process (8525-8576) and the per-frame helpers
// _tick_present_ships (8582-8605), _tick_sanity_and_hallucinations (8631-8647), _tick_electrical_arc,
// _tick_ammo_and_consumable_decay, _tick_field_craft_and_autosave, _tick_audio_runtime, _tick_footstep_sfx (8649-8705),
// _tick_survival_attrition (8718-8839), _tick_food_runtime, _check_vitals_death (8846-8861), _tick_threat_runtime and
// its nav/LOS helpers (4886-4909, 7405-7464), _refresh_tooltip_focus (7603-7622).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary>Probe: raised after every stage ran (tests assert stages run in both locations).</summary>
        public event Action<string, SessionLocation> StageRan;

        /// <summary>
        /// <c>_process(delta)</c>. Advances <see cref="WorldTime"/> (always) and <see cref="RunPlayTimeSeconds"/> (while
        /// playable) before choosing the branch, then runs the branch's stage order (<see cref="TickOrder"/>) unless the
        /// slice is not started / complete (and, at home, oxygen is missing). Afterwards the interaction nodes' own
        /// <c>_process</c> (repair/seal/extinguish/breach channels, recharge port) runs, as Godot processed those children
        /// after the coordinator every frame regardless of its early returns.
        /// </summary>
        public void Tick(in TickContext ctx)
        {
            _frame = ctx;
            _inTick = true;
            try
            {
                double delta = ctx.Delta;
                WorldTime += delta;
                if (PlayableStarted && !SliceComplete)
                    RunPlayTimeSeconds += delta;
                SessionLocation location = AwayFromStart ? SessionLocation.Away : SessionLocation.Home;
                bool run = PlayableStarted && !SliceComplete && (location == SessionLocation.Away || OxygenState != null);
                if (run)
                {
                    foreach (string id in TickOrder.OrderFor(location))
                    {
                        TickOrder.Get(id).Run(this, location, delta);
                        StageRan?.Invoke(id, location);
                    }
                }
                ProcessInteractableNodes(delta);
            }
            finally
            {
                _inTick = false;
            }
        }

        // ------------------------------------------------------------------ stage entry points (TickOrder)
        internal void StageAutosave(double delta) => TickAutosavePolicy(delta);
        internal void StageOxygen(double delta) => RefreshOxygenState(false, delta);
        internal void StageThreat(double delta) => TickThreatRuntime(delta);

        internal void StageSanityHallucination(double delta, SessionLocation location)
        {
            bool inSafe = false;
            if (location == SessionLocation.Home)
                inSafe = !AwayFromStart && (OxygenState == null || !OxygenState.GetSummary().GetBool("breach_open"));
            TickSanityAndHallucinations(delta, inSafe);
        }

        internal void StageActiveFire(double delta) => TickActiveFire(delta);

        internal void StageFieldCraft(double delta)
        {
            if (FieldCraftingState != null && FieldCraftingState.Tick(delta))
                OnFieldCraftCompleted();
        }

        internal void StageSurvivalAttrition(double delta) => TickSurvivalAttrition(delta);
        internal void StagePlayerVitals(double delta) => RefreshPlayerVitals(delta);
        internal void StageTrackerStatus() => RefreshTrackerSystemStatusLines();
        internal void StageAudio(double delta) => TickAudioRuntime(delta);
        internal void StagePresentShips(double delta) => TickPresentShips(delta);

        internal void StageRechargePortPower()
        {
            if (ExtinguisherRechargePort != null && ExtinguisherRechargePort.IsValid)
            {
                ShipSystemsManager dmgr = ActiveSystemsManager();
                ExtinguisherRechargePort.SetPowered(dmgr != null && dmgr.IsOperational("power"));
            }
        }

        internal void StageFood(double delta) => TickFoodRuntime(delta);
        internal void StageAmmoConsumableDecay(double delta) => TickAmmoAndConsumableDecay(delta);
        internal void StageElectricalArc(double delta) => TickElectricalArc(delta);
        internal void StageWorkAction(double delta) => TickWorkAction(delta);

        /// <summary>The child interaction nodes' own <c>_process(delta)</c> (channels + recharge), in spawn order.</summary>
        void ProcessInteractableNodes(double delta)
        {
            bool playerValid = HasPlayer;
            Vec3 p = PlayerPos;
            foreach (DockPortBarrier b in new List<DockPortBarrier>(DockBarriers))
                if (b.IsValid) b.Process(delta, p, playerValid);
            foreach (RepairPoint rp in new List<RepairPoint>(RepairPoints))
                if (rp.IsValid) rp.Process(delta, p, playerValid);
            foreach (BreachSealPoint sp in new List<BreachSealPoint>(BreachSealPoints))
                if (sp.IsValid) sp.Process(delta, p, playerValid);
            foreach (FireSuppressionPoint fp in new List<FireSuppressionPoint>(FireSuppressionPoints))
                if (fp.IsValid) fp.Process(delta, p, playerValid);
            if (ExtinguisherRechargePort != null && ExtinguisherRechargePort.IsValid)
                ExtinguisherRechargePort.Process(delta, p, playerValid);
        }

        // ------------------------------------------------------------------ present ships
        /// <summary>PKG-A1c / A3: advance present ships every frame; hub expanded recompute on the SLOW band only.</summary>
        void TickPresentShips(double delta)
        {
            double covBefore = 0.0;
            if (HullWebState != null)
                covBefore = HullWebState.Coverage;
            AdvanceShip(HomeShip, delta);
            if (AwayFromStart && CurrentShip != null && CurrentShip != HomeShip)
                AdvanceShip(CurrentShip, delta);
            _biomatterPulseCooldown = Math.Max(0.0, _biomatterPulseCooldown - delta);
            if (HullWebState != null && HullWebState.Coverage > covBefore + 0.0001)
                MaybeEmitBiomatterPulse();
            _hubSlowAcc += delta;
            if (_hubSlowAcc >= ShipRuntime.SLOW_INTERVAL_SECONDS)
            {
                double slowDt = _hubSlowAcc;
                _hubSlowAcc = 0.0;
                RecomputeExpandedShipSystems(slowDt);
            }
        }

        void MaybeEmitBiomatterPulse()
        {
            if (_biomatterPulseCooldown > 0.0)
                return;
            PlaySfx(AudioEventSeam.META_BIOMATTER_PULSE);
            _biomatterPulseCooldown = 4.0;
        }

        void EmitDockLandSfx() => PlaySfx(AudioEventSeam.SFX_DOCK_LAND);
        void EmitTravelDeniedSfx() => PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);

        // ------------------------------------------------------------------ sanity / arc / ammo / audio
        void TickSanityAndHallucinations(double delta, bool inSafe)
        {
            if (SanityState == null)
                return;
            SanityState.InSafeZone = inSafe;
            SanityState.SteadyMultiplier = StatusEffectsState != null && StatusEffectsState.HasEffect("utility_flare") ? 0.5 : 1.0;
            SanityState.Tick(delta);
            if (HallucinationDirector == null)
                return;
            var anchors = new GdArray();
            foreach (Vec3 p in DistributedRoomPositions())
                anchors.Add(p);
            HallucinationDirector.Tick(delta, new GdDict
            {
                { "sanity", SanityState.Sanity },
                { "in_safe_zone", inSafe },
                { "anchor_positions", anchors },
            });
            if (HallucinationManager != null)
            {
                Vec3 ppos = HasPlayer ? PlayerPos : Vec3.Zero;
                HallucinationManager.Render(delta, ppos);
            }
        }

        void TickElectricalArc(double delta)
        {
            if (ElectricalArcState == null)
                return;
            ElectricalArcState.Tick(delta, new GdDict());
            RefreshArcState(false);
        }

        void TickAmmoAndConsumableDecay(double delta)
        {
            if (AmmoState != null && !AmmoState.Tick(delta).IsEmpty)
                RefreshWeaponHotbar();
            StimulantState?.Tick(delta, AddictionState, ConsumablePipelineContext());
            AddictionState?.Tick(delta, StatusEffectsState);
        }

        void TickAudioRuntime(double delta)
        {
            if (AudioManager != null)
            {
                AudioManager.Tick(delta);
                RefreshAudioState(false, delta);
            }
            TickFootstepSfx(delta);
        }

        /// <summary>Live footstep call site (both branches via the audio stage).</summary>
        void TickFootstepSfx(double delta)
        {
            bool moving = HasPlayer && PlayerMoving;
            if (!moving)
            {
                _footstepAcc = 0.0;
                return;
            }
            double interval = FOOTSTEP_INTERVAL_WALK;
            if (PlayerCrouching)
                interval = FOOTSTEP_INTERVAL_CROUCH;
            _footstepAcc += Math.Max(0.0, delta);
            if (_footstepAcc < interval)
                return;
            _footstepAcc = 0.0;
            PlaySfx(AudioEventSeam.SFX_FOOTSTEP, PlayerPos);
        }

        // ------------------------------------------------------------------ survival attrition
        /// <summary>Domain 1: the single survival-attrition tick (both branches).</summary>
        void TickSurvivalAttrition(double delta)
        {
            if (VitalsState == null)
                return;
            bool breachOpen = OxygenState != null && OxygenState.GetSummary().GetBool("breach_open");
            bool inHazardEnv = AwayFromStart || breachOpen;
            bool? inAuthoredRadiation = null;
            bool hasAuthoredRadiationSource = false;
            bool hasAuthoredTemperatureSource = false;
            bool inAuthoredTemperatureSource = false;
            IShipLoaderView authoredLoader = AwayFromStart && CurrentShip != null ? CurrentShip.SceneRoot as IShipLoaderView : Loader;
            var authoredAtmosphere = new GdDict();
            if (authoredLoader != null && authoredLoader.IsValid && HasPlayer)
                authoredAtmosphere = authoredLoader.GetAuthoredAtmosphereAt(ToLocal(authoredLoader, PlayerPos)) ?? new GdDict();
            if (authoredLoader != null && authoredLoader.IsValid)
            {
                GdArray authoredRadiationSpecs = authoredLoader.GetRadiationZoneSpecs() ?? new GdArray();
                hasAuthoredRadiationSource = !authoredRadiationSpecs.IsEmpty;
                if (!authoredRadiationSpecs.IsEmpty && HasPlayer)
                {
                    GdDict authoredRadiation = authoredLoader.GetRadiationZoneAt(ToLocal(authoredLoader, PlayerPos)) ?? new GdDict();
                    inAuthoredRadiation = !authoredRadiation.IsEmpty;
                }
                GdArray atmosphereSpecs = authoredLoader.AuthoredAtmosphereSpecs;
                if (atmosphereSpecs != null)
                {
                    foreach (object specObj in atmosphereSpecs)
                    {
                        if (specObj is GdDict spec)
                        {
                            if (V.I64(spec.Get("radiation_bp", 0L)) > 0)
                                hasAuthoredRadiationSource = true;
                            if (spec.Has("temperature_c"))
                                hasAuthoredTemperatureSource = true;
                        }
                    }
                }
            }
            if (!authoredAtmosphere.IsEmpty && authoredAtmosphere.Has("temperature_c"))
                inAuthoredTemperatureSource = true;
            double tempMult = 1.0;
            if (BodyTemperatureState != null)
                tempMult = BodyTemperatureState.GetThirstMultiplier();
            double radDrain = 0.0;
            if (RadiationState != null)
                radDrain = RadiationState.GetHealthDrainPerSecond();
            double statusMult = 1.0;
            if (StatusEffectsState != null)
                statusMult = StatusEffectsState.GetModifier("stamina_recovery");
            double atmoDrain = 0.0;
            if (LifeSupportExpandedState != null && !AwayFromStart)
            {
                atmoDrain = LifeSupportExpandedState.GetHealthDrainPerSecond();
                tempMult *= LifeSupportExpandedState.GetThirstMultiplier();
                // Unity port (decision 56): the suit supplies the player's air while its reserve lasts.
                if (SuitFilteringShipAir)
                    atmoDrain = 0.0;
            }
            double oxygenHealthDrain = 0.0;
            if (OxygenState != null && IsFieldSuitPressureActive())
            {
                GdDict o2 = OxygenState.GetSummary();
                double o2Level = V.F64(o2.Get("oxygen", 100.0));
                double o2Recovery = V.F64(o2.Get("recovery_threshold", 30.0));
                if (o2Level <= 0.001)
                    oxygenHealthDrain = 8.0;
                else if (o2Level <= o2Recovery + 0.001)
                    oxygenHealthDrain = 2.0;
            }
            GdDict hteeth = HallucinationDirector != null
                ? HallucinationDirector.GetDirectTeeth()
                : new GdDict { { "health_drain_per_second", 0.0 }, { "stamina_recovery_mult", 1.0 } };
            double encumbDrain = 0.0;
            if (InventoryState != null)
                encumbDrain = Encumbrance.HealthDrainPerSecond(InventoryState.GetLoadRatio());
            VitalsState.Tick(delta, new GdDict
            {
                { "temperature_thirst_mult", tempMult },
                { "radiation_health_drain", radDrain },
                { "atmosphere_health_drain", atmoDrain + oxygenHealthDrain },
                { "fire_health_drain", FIRE_HEALTH_DRAIN_PER_SECOND * PlayerFireIntensity() },
                { "status_stamina_recovery_mult", statusMult },
                { "sanity_health_drain", V.F64(hteeth["health_drain_per_second"]) },
                { "sanity_stamina_recovery_mult", V.F64(hteeth["stamina_recovery_mult"]) },
                { "encumbrance_health_drain", encumbDrain },
                // Unity port (E1): wound bleed and wound thirst (VitalsState's SimKeys; 0.0 / 1.0 without wounds).
                { SimKeys.WoundHealthDrain, WoundState != null ? WoundState.TotalBleedRate() : 0.0 },
                { SimKeys.WoundThirstMult, WoundState != null ? WoundState.ThirstDrainMultiplier() : 1.0 },
                { "moving", HasPlayer && PlayerMoving },
            });
            ApplyVitalsActionGating();
            CheckVitalsDeath();
            if (RadiationState != null)
            {
                bool? atmosphereRadiation = null;
                if (!authoredAtmosphere.IsEmpty)
                    atmosphereRadiation = V.I64(authoredAtmosphere.Get("radiation_bp", 0L)) > 0;
                if (hasAuthoredRadiationSource)
                    RadiationState.InRadiationZone = inAuthoredRadiation == true || atmosphereRadiation == true;
                else
                    RadiationState.InRadiationZone = inHazardEnv;
                RadiationState.Tick(delta);
            }
            if (BodyTemperatureState != null)
            {
                if (inAuthoredTemperatureSource)
                {
                    double ambient = V.F64(authoredAtmosphere.Get("temperature_c", BodyTemperatureState.DEFAULT_TEMPERATURE));
                    BodyTemperatureState.InExtremeZone = ambient < BodyTemperatureState.SafeMin || ambient > BodyTemperatureState.SafeMax;
                    BodyTemperatureState.Tick(delta, new GdDict { { "ambient_temperature_c", ambient } });
                }
                else if (hasAuthoredTemperatureSource)
                {
                    BodyTemperatureState.InExtremeZone = false;
                    BodyTemperatureState.Tick(delta);
                }
                else
                {
                    BodyTemperatureState.InExtremeZone = inHazardEnv;
                    BodyTemperatureState.Tick(delta);
                }
            }
            StatusEffectsState?.Tick(delta);
        }

        /// <summary>Domain 1: push the vitals movement gate onto the player every frame.</summary>
        void ApplyVitalsActionGating()
        {
            if (!HasPlayer || VitalsState == null || Scene == null)
                return;
            double mult = VitalsState.GetMovementSpeedMultiplier();
            // Unity port (E1): leg fractures slow the player (exactly 1.0 without one).
            if (WoundState != null)
                mult *= WoundState.MovementSpeedMultiplier();
            Scene.SetMovementSpeedMultiplier(mult);
        }

        /// <summary>Domain 3: spoilage + in-progress production (both branches; deliberately not paused while away).</summary>
        void TickFoodRuntime(double delta)
        {
            SpoilageState?.Tick(delta);
            if (HydroponicsState != null && HydroponicsState.CurrentState == (long)HydroponicsState.State.PLANTED)
                HydroponicsState.Tick(delta);
            if (WaterRecyclerState != null && WaterRecyclerState.CurrentState == (long)WaterRecyclerState.State.RECYCLING)
                WaterRecyclerState.Tick(delta);
        }

        /// <summary>Domain 1: terminal stake — incapacitation ends the run as a death (idempotent).</summary>
        void CheckVitalsDeath()
        {
            if (VitalsState == null || SliceComplete)
                return;
            if (VitalsState.IsIncapacitated())
                EndRun("death");
        }

        // ------------------------------------------------------------------ threat runtime
        /// <summary>Domain 2 (BP2): a powered active ship is lit.</summary>
        bool PlayerRoomLit()
        {
            ShipSystemsManager mgr = ActiveSystemsManager();
            return mgr != null && mgr.IsOperational("power");
        }

        void TickThreatRuntime(double delta)
        {
            if (ThreatManager == null)
                return;
            Vec3 playerPos = HasPlayer ? PlayerPos : Vec3.Zero;
            bool moving = HasPlayer && PlayerMoving;
            bool crouching = HasPlayer && PlayerCrouching;
            ThreatManager.SetPlayerSignals(moving ? 0.3 : 0.05, PlayerRoomLit() ? 0.6 : 0.15, 0.8, crouching, "");
            UpdateThreatEngagedLos();
            RefreshThreatNavCosts();
            ThreatManager.TickThreats(delta, VitalsState, StatusEffectsState, PlayerArmorProfile(), playerPos);
            if (ThreatManager.GetDetectedThreatCount() > 0)
                TriggerTutorial("threat_spotted", "any");
            SyncCurrentShipCombatSummary();
            RefreshWeaponHotbar();
        }

        /// <summary>PKG-C4.1b: one frame raycast pass for engaged LOS flags before the AI tick.</summary>
        public void UpdateThreatEngagedLos()
        {
            if (ThreatManager == null || !HasPlayer)
                return;
            ILineOfSightProbe probe = Deps.LosProbe;
            if (probe == null || !probe.HasSpace)
                return;
            Vec3 from = PlayerPos + new Vec3(0.0f, 1.2f, 0.0f);
            foreach (ThreatAIState threat in ThreatManager.Threats)
            {
                if (threat == null)
                    continue;
                string tid = threat.InstanceId;
                if (tid.Length == 0 || threat.WorldPosition.Count < 3)
                    continue;
                var to = new Vec3(V.F64(threat.WorldPosition[0]), V.F64(threat.WorldPosition[1]) + 1.0, V.F64(threat.WorldPosition[2]));
                bool hit = probe.IntersectRay(from, to, out Vec3 hp);
                bool hasLos = !hit;
                if (!hasLos)
                    hasLos = hp.DistanceTo(to) < 1.5;
                ThreatManager.SetEngagedLos(tid, hasLos);
            }
        }

        /// <summary>ADR-0049: push fire + sealed-hatch bulkhead costs into the threat nav graph.</summary>
        void RefreshThreatNavCosts()
        {
            if (ThreatManager == null)
                return;
            if (ThreatManager.NavGraph == null || ThreatManager.NavGraph.NodeCount() == 0)
                ThreatManager.ConfigureNavGraph(CombatLayoutForCurrentShip());
            var fireRooms = new GdDict();
            FireSuppressionState afs = ActiveFireState();
            if (afs != null)
            {
                foreach (object cid in afs.GetBurningCompartments())
                    fireRooms[V.Str(cid)] = afs.GetIntensity(V.Str(cid));
            }
            var bulkheads = new GdArray();
            foreach (SealedHatch h in SealedHatches)
            {
                if (h.IsValid && !h.Bypassed && h.CompartmentA.Length > 0 && h.CompartmentB.Length > 0)
                    bulkheads.Add(GdArray.Of(h.CompartmentA, h.CompartmentB));
            }
            ThreatManager.UpdateNavDynamicCosts(fireRooms, bulkheads);
            ApplyIntegrityNavGaps();
        }

        void ApplyIntegrityNavGaps()
        {
            if (ModuleIntegrityMap == null || ThreatManager == null)
                return;
            ShipNavGraph nav = ThreatManager.NavGraph;
            if (nav == null)
                return;
            ModuleIntegrityConsequences.ApplyNavGaps(nav, GdString.ToGdArray(ModuleIntegrityMap.RoomsWithNavGaps()));
        }

        // ------------------------------------------------------------------ tooltip focus
        /// <summary>
        /// Domain 10 (ADR-0045) tooltip trigger 1: nearest objective interactable whose Area3D overlaps the player; pushes a
        /// tooltip query only when the focused subject changes.
        /// </summary>
        internal void RefreshTooltipFocus()
        {
            if (!HasPlayer)
                return;
            ObjectiveInteractable nearest = null;
            double nearestDist = double.PositiveInfinity;
            Vec3 playerPos = PlayerPos;
            foreach (List<ObjectiveInteractable> collection in new[] { Interactables, DerelictInteractables })
            {
                foreach (ObjectiveInteractable it in collection)
                {
                    if (!it.IsValid || !it.CandidatePlayerInRange)
                        continue;
                    double dist = it.GlobalPosition.DistanceTo(playerPos);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = it;
                    }
                }
            }
            string subjectId = nearest != null ? nearest.TooltipSubjectId : "";
            if (subjectId == _lastTooltipFocusSubjectId)
                return;
            _lastTooltipFocusSubjectId = subjectId;
            Events.RaiseTooltipQuery(new GdDict { { "subject_kind", "interactable" }, { "subject_id", subjectId } });
        }

        void ResetTooltipFocus()
        {
            _lastTooltipFocusSubjectId = "";
            Events.RaiseTooltipQuery(new GdDict { { "subject_kind", "interactable" }, { "subject_id", "" } });
        }
    }
}
