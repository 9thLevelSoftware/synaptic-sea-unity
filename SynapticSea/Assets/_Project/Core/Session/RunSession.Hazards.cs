// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: the breach/oxygen integration (8863-9230), the arc
// zone integration (9318-9606), route gates (8332-8365), and _refresh_player_vitals (9106-9141).
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
        // ------------------------------------------------------------------ breach / oxygen
        /// <summary><c>_build_breach_zone(preserve_active_environment, preserved_player_oxygen)</c>.</summary>
        void BuildBreachZone(bool preserveActiveEnvironment = true, double preservedPlayerOxygen = -1.0)
        {
            GdDict previousOxygenSummary = new GdDict();
            if (OxygenState != null)
                previousOxygenSummary = OxygenState.GetSummary();
            foreach (SessionZone z in BreachZoneNodes)
                Events.RaiseZoneDespawned(z);
            BreachZoneNodes.Clear();
            UnsafeRoomMarkerVisible = false;
            IShipLoaderView breachLoader = ActiveBreachLoader();
            var positions = new List<Vec3>();
            var zoneIds = new GdArray();
            if (breachLoader != null)
            {
                foreach (Vec3 marker in breachLoader.GetBreachZoneMarkers())
                {
                    if (marker != Vec3.Inf)
                        positions.Add(ToGlobal(breachLoader, marker));
                }
            }
            GdArray specs = breachLoader != null ? breachLoader.GetBreachZoneSpecs() ?? new GdArray() : new GdArray();
            for (int index = 0; index < positions.Count; index++)
            {
                string zoneId = "breach_" + index;
                if (index < specs.Count && specs[index] is GdDict spec)
                {
                    string authoredZoneId = V.Str(spec.Get("zone_id", spec.Get("id", "")));
                    if (authoredZoneId.Length > 0)
                        zoneId = authoredZoneId;
                }
                zoneIds.Add(zoneId);
            }
            if (positions.Count == 0)
            {
                positions.Add(ResolveBreachZoneWorldPosition(breachLoader));
                zoneIds.Add(BREACH_ZONE_FALLBACK_ID);
            }
            if (OxygenState == null)
                OxygenState = new OxygenState();
            OxygenState.Configure(new GdDict
            {
                { "zone_ids", zoneIds },
                { "max_oxygen", OxygenState.DEFAULT_MAX_OXYGEN },
                // Unity port (C4): the home hazard dial scales the breach drain (exactly the default for "standard").
                { "drain_rate", OxygenState.DEFAULT_DRAIN_RATE * HomeHazardModifier() },
                { "regen_rate", OxygenState.DEFAULT_REGEN_RATE },
                { "recovery_threshold", OxygenState.DEFAULT_RECOVERY_THRESHOLD },
                { "safe_threshold", OxygenState.DEFAULT_SAFE_THRESHOLD },
            });
            if (!previousOxygenSummary.IsEmpty)
            {
                if (preserveActiveEnvironment)
                {
                    previousOxygenSummary["breach_zone_ids"] = zoneIds.ShallowCopy();
                    OxygenState.ApplySummary(previousOxygenSummary);
                }
                else
                {
                    double playerOxygen = preservedPlayerOxygen >= 0.0 ? preservedPlayerOxygen : V.F64(previousOxygenSummary.Get("oxygen", OxygenState.Oxygen));
                    OxygenState.ApplySummary(new GdDict { { "hazard_kind", "oxygen" }, { "oxygen", playerOxygen } });
                    if (CurrentShip != null && !CurrentShip.BreachEnvironmentSummary.IsEmpty)
                    {
                        GdDict activeEnvironment = CurrentShip.BreachEnvironmentSummary.DeepCopy();
                        activeEnvironment["hazard_kind"] = "oxygen";
                        activeEnvironment["breach_zone_ids"] = zoneIds.ShallowCopy();
                        OxygenState.ApplySummary(activeEnvironment);
                    }
                }
            }
            for (int index = 0; index < positions.Count; index++)
            {
                string zid = V.Str(zoneIds[index]);
                var zone = new SessionZone
                {
                    Kind = "breach",
                    ZoneId = zid,
                    NodeName = "BreachZone_" + zid,
                    LocalPosition = positions[index],
                    VisualState = "open",
                };
                BreachZoneNodes.Add(zone);
                Events.RaiseZoneSpawned(zone);
            }
            ApplyBreachZoneSceneState();
        }

        IShipLoaderView ActiveBreachLoader()
        {
            if (AwayFromStart && CurrentShip != null && CurrentShip.SceneRoot is IShipLoaderView derelict && derelict.IsValid)
                return derelict;
            return Loader != null && Loader.IsValid ? Loader : null;
        }

        Vec3 ResolveBreachZoneWorldPosition(IShipLoaderView breachLoader)
        {
            if (breachLoader != null)
            {
                IReadOnlyList<Vec3> markers = breachLoader.GetBreachZoneMarkers();
                if (markers.Count > 0 && markers[0] != Vec3.Inf)
                    return ToGlobal(breachLoader, markers[0]);
            }
            Vec3 obj3 = Vec3.Inf;
            Vec3 obj4 = Vec3.Inf;
            foreach (ObjectiveInteractable it in Interactables)
            {
                if (it.Sequence == 3)
                    obj3 = it.GlobalPosition;
                else if (it.Sequence == 4)
                    obj4 = it.GlobalPosition;
            }
            if (obj3 == Vec3.Inf || obj4 == Vec3.Inf)
            {
                if (HasPlayer)
                    return PlayerPos + new Vec3(0.0f, 0.0f, 6.0f);
                return Vec3.Zero;
            }
            return (obj3 + obj4) * 0.5f;
        }

        /// <summary><c>_refresh_oxygen_state(force_initial, delta_seconds)</c>.</summary>
        void RefreshOxygenState(bool forceInitial, double deltaSeconds)
        {
            if (OxygenState == null)
            {
                RefreshTrackerSystemStatusLines();
                return;
            }
            if (InventoryState != null)
                OxygenState.ApplyInventorySummary(InventoryState.GetSummary());
            if (EquipmentState != null)
                OxygenState.ApplyEquipmentSummary(new GdDict { { "drain_multiplier", EquipmentState.GetOxygenDrainMultiplier() } });
            if (forceInitial)
            {
                OxygenState.ApplyShipSystemsSummary(new GdDict());
                ApplyBreachZoneSceneState();
                RefreshTrackerSystemStatusLines();
                RefreshPlayerVitals(deltaSeconds);
                return;
            }
            double fireO2 = 0.0;
            FireSuppressionState afsO2 = ActiveFireState();
            if (afsO2 != null)
                fireO2 = FIRE_OXYGEN_DRAIN_PER_INTENSITY * afsO2.GetTotalIntensity();
            if (IsFieldSuitPressureActive())
            {
                double authoredAtmosphereMultiplier = 1.0;
                IShipLoaderView atmosphereLoader = AwayFromStart && CurrentShip != null ? CurrentShip.SceneRoot as IShipLoaderView : Loader;
                if (atmosphereLoader != null && atmosphereLoader.IsValid && HasPlayer)
                    authoredAtmosphereMultiplier = atmosphereLoader.GetAuthoredAtmosphereDrainMultiplierAt(ToLocal(atmosphereLoader, PlayerPos));
                OxygenState.Tick(deltaSeconds, new GdDict
                {
                    { "field_atmosphere", true },
                    { "field_atmosphere_multiplier", authoredAtmosphereMultiplier },
                    { "player_in_breach_zone", false },
                    { "fire_oxygen_drain", fireO2 },
                });
            }
            else
            {
                double suitBefore = OxygenState.Oxygen;
                OxygenState.Tick(deltaSeconds, new GdDict
                {
                    { "player_in_breach_zone", !AwayFromStart && IsPlayerInBreachZone() },
                    { "field_atmosphere", false },
                    { "fire_oxygen_drain", fireO2 },
                });
                ApplySuitAirReserve(deltaSeconds, suitBefore);
            }
            ApplyBreachZoneSceneState();
            RefreshTrackerSystemStatusLines();
            RefreshPlayerVitals(deltaSeconds);
            GdDict oxygenSummary = OxygenState.GetSummary();
            if (V.F64(oxygenSummary.Get("oxygen", 100.0)) <= V.F64(oxygenSummary.Get("safe_threshold", 35.0)))
                TriggerTutorial("vitals_warning", "oxygen_low");
        }

        /// <summary>
        /// Unity port (decision 56): how fouled the home ship's air is, 0 (breathable) to 1 (the maximum atmosphere health
        /// drain). 0 when away or when the suit reserve is disabled.
        /// </summary>
        internal double HomeAtmosphereSeverity()
        {
            if (AwayFromStart || Deps.HomeSuitAirReserveSeconds <= 0.0 || LifeSupportExpandedState == null)
                return 0.0;
            double max = LifeSupportExpandedState.MaxAtmosphereHealthDrain;
            if (max <= 0.0)
                return 0.0;
            return GdMath.Clampf(LifeSupportExpandedState.GetHealthDrainPerSecond() / max, 0.0, 1.0);
        }

        /// <summary>True while the suit is supplying the player's air on the home ship (fouled air, suit O2 left).</summary>
        public bool SuitFilteringShipAir => OxygenState != null && HomeAtmosphereSeverity() > 0.0 && OxygenState.Oxygen > 0.001;

        /// <summary>
        /// Unity port (decision 56): while the home air is fouled the suit supplies the player's air. The tick's regen is
        /// withheld (breach-zone and fire drains still apply) and the reserve drains by severity, scaled by the hazard dial.
        /// </summary>
        void ApplySuitAirReserve(double deltaSeconds, double suitBefore)
        {
            double severity = HomeAtmosphereSeverity();
            if (severity <= 0.0 || deltaSeconds <= 0.0 || OxygenState == null)
                return;
            double perSecond = severity * OxygenState.MaxOxygen / Deps.HomeSuitAirReserveSeconds * Math.Max(0.1, HomeHazardModifier());
            double level = Math.Min(OxygenState.Oxygen, suitBefore);
            OxygenState.Oxygen = Math.Max(0.0, level - perSecond * deltaSeconds);
        }

        /// <summary>True when suit O2 drains as hostile field atmosphere: aboard a derelict hull, not inside lifeboat/home.</summary>
        bool IsFieldSuitPressureActive()
        {
            if (!AwayFromStart)
                return false;
            if (LifeboatShip != null && CurrentOccupancy == LifeboatShip)
                return false;
            if (HomeShip != null && CurrentOccupancy == HomeShip)
                return false;
            return true;
        }

        /// <summary><c>_refresh_player_vitals(delta)</c>: feed the HUD vitals model and push its lines.</summary>
        void RefreshPlayerVitals(double deltaSeconds)
        {
            if (VitalsModel == null)
                return;
            if (OxygenState != null)
                VitalsModel.ApplyOxygenSummary(OxygenState.GetSummary());
            if (InventoryState != null)
            {
                double ratio = InventoryState.GetLoadRatio();
                VitalsModel.ApplyInventoryLoad(ratio, Encumbrance.MoveSpeedMultiplier(ratio), InventoryState.WeightReduction);
            }
            bool channeling = false;
            double progress = 0.0;
            foreach (RepairPoint rp in RepairPoints)
            {
                if (rp.IsValid && rp.Channeling)
                {
                    channeling = true;
                    progress = rp.Progress;
                    break;
                }
            }
            VitalsModel.SetRepairProgress(channeling, progress);
            if (VitalsState != null)
                VitalsModel.ApplyVitalsSummary(VitalsState.GetSummary());
            if (SanityState != null)
                VitalsModel.ApplySanitySummary(SanityState.GetSummary());
            if (RadiationState != null)
                VitalsModel.ApplyRadiationSummary(RadiationState.GetSummary());
            if (BodyTemperatureState != null)
                VitalsModel.ApplyTemperatureSummary(BodyTemperatureState.GetSummary());
            if (StatusEffectsState != null)
                VitalsModel.ApplyStatusEffectsSummary(StatusEffectsState.GetSummary());
            VitalsModel.Tick(deltaSeconds);
            Events.RaiseVitalsPanelLines(VitalsModel.GetStatusLines());
        }

        public List<string> GetPlayerVitalsLines() => VitalsModel == null ? new List<string>() : VitalsModel.GetStatusLines();

        void ApplyBreachZoneSceneState()
        {
            if (OxygenState == null || BreachZoneNodes.Count == 0)
                return;
            GdDict summary = OxygenState.GetSummary();
            bool breachOpen = summary.GetBool("breach_open");
            bool breachSealed = summary.GetBool("breach_sealed");
            bool passabilityBlocked = summary.GetBool("passability_blocked");
            bool collisionEnabled = breachOpen && passabilityBlocked;
            foreach (SessionZone zone in BreachZoneNodes)
            {
                zone.Meta["breach_zone_open"] = breachOpen;
                zone.Meta["breach_zone_sealed"] = breachSealed;
                zone.Meta["breach_zone_passability_blocked"] = passabilityBlocked;
                zone.CollisionEnabled = collisionEnabled;
                zone.VisualState = passabilityBlocked ? "blocked" : (breachOpen ? "open" : "sealed");
                zone.VisualVisible = breachOpen;
                Events.RaiseZoneStateChanged(zone);
            }
            UnsafeRoomMarkerVisible = breachOpen && !breachSealed;
            Events.RaiseBreachUnsafeMarkerVisible(UnsafeRoomMarkerVisible);
        }

        public GdDict GetOxygenSummary()
        {
            if (OxygenState == null)
            {
                return new GdDict
                {
                    { "oxygen", 0.0 }, { "max_oxygen", 0.0 }, { "drain_rate", 0.0 }, { "regen_rate", 0.0 },
                    { "recovery_threshold", 0.0 }, { "safe_threshold", 0.0 }, { "breach_open", false },
                    { "breach_sealed", false }, { "passability_blocked", false }, { "player_in_breach_zone", false },
                    { "breach_zone_ids", new GdArray() },
                };
            }
            return OxygenState.GetSummary();
        }

        public long GetBreachZoneCollisionEnabledCount()
        {
            long n = 0;
            foreach (SessionZone z in BreachZoneNodes)
                if (z.CollisionEnabled) n++;
            return n;
        }

        /// <summary><c>is_player_in_breach_zone()</c>: horizontal radius 2.4 around any breach zone node.</summary>
        public bool IsPlayerInBreachZone()
        {
            if (_inTick && _frame.InBreachZone.HasValue)
                return _frame.InBreachZone.Value;
            if (BreachZoneNodes.Count == 0 || !HasPlayer)
                return false;
            Vec3 playerPos = PlayerPos;
            foreach (SessionZone zone in BreachZoneNodes)
            {
                Vec3 zonePos = zone.LocalPosition;
                double dx = playerPos.X - zonePos.X;
                double dz = playerPos.Z - zonePos.Z;
                if (dx * dx + dz * dz <= BREACH_ZONE_PROXIMITY_RADIUS * BREACH_ZONE_PROXIMITY_RADIUS)
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ route gates
        void RefreshRouteControlFromShipSystems()
        {
            if (RouteControlState == null || ShipSystemsManager == null)
            {
                RefreshTrackerSystemStatusLines();
                return;
            }
            RouteControlState.ApplyShipSystemsSummary(ManagerCompatSummary());
            ApplyRouteGateSceneState();
            RefreshTrackerSystemStatusLines();
        }

        void ApplyRouteGateSceneState()
        {
            if (RouteControlState == null)
                return;
            foreach (SessionZone gate in RouteGateNodes)
            {
                bool isOpen = RouteControlState.IsGateOpen(gate.ZoneId);
                gate.Meta["route_gate_open"] = isOpen;
                gate.Meta["system_cleared"] = isOpen;
                gate.CollisionEnabled = !isOpen;
                gate.VisualState = isOpen ? "open" : "closed";
                gate.VisualVisible = !isOpen;
                if (Loader != null && Loader.IsValid && gate.Meta.Has("blocked_route_index"))
                    Loader.SetBlockedRouteCollisionEnabled(V.I32(gate.Meta["blocked_route_index"]), !isOpen);
                Events.RaiseZoneStateChanged(gate);
            }
        }

        public GdDict GetRouteControlSummary()
        {
            if (RouteControlState == null)
            {
                return new GdDict
                {
                    { "route_gate_count", 0L }, { "active_blocker_count", 0L }, { "opened_gate_count", 0L },
                    { "powered_gates_open", false }, { "extraction_unlocked", false }, { "gate_ids", new GdArray() },
                    { "route_gate_collision_enabled_count", 0L },
                };
            }
            GdDict summary = RouteControlState.GetSummary();
            summary["route_gate_collision_enabled_count"] = GetRouteGateCollisionEnabledCount();
            return summary;
        }

        public long GetRouteGateCollisionEnabledCount()
        {
            long n = 0;
            foreach (SessionZone g in RouteGateNodes)
                if (g.CollisionEnabled) n++;
            return n;
        }

        // ------------------------------------------------------------------ electrical arc
        /// <summary>Smallest hazard dial the arc timing divides by (a biome may push the combined dial toward 0).</summary>
        const double ARC_MIN_HAZARD_MODIFIER = 0.1;

        /// <summary>
        /// Unity port (C4) tuning, not parity: the home hazard dial lengthens the arcing phase and shortens the safe
        /// discharged window (<c>arcing x dial</c>, <c>discharged / dial</c>). Both durations are exactly Godot's defaults
        /// for "standard" and away from home, like the breach drain.
        /// </summary>
        GdDict ArcConfig(GdArray zoneIds)
        {
            double modifier = System.Math.Max(ARC_MIN_HAZARD_MODIFIER, HomeHazardModifier());
            return new GdDict
            {
                { "zone_ids", zoneIds },
                { "arcing_duration", ElectricalArcState.DEFAULT_ARCING_DURATION * modifier },
                { "discharged_duration", ElectricalArcState.DEFAULT_DISCHARGED_DURATION / modifier },
            };
        }

        /// <summary><c>_build_arc_zone()</c>: configure the model (always), then one zone per resolved marker.</summary>
        void BuildArcZone()
        {
            foreach (SessionZone z in ArcZoneNodes)
                Events.RaiseZoneDespawned(z);
            ArcZoneNodes.Clear();
            ArcZoneResolvedRoomId = "";
            ArcZoneResolvedRoomIds.Clear();
            if (ElectricalArcState == null)
                ElectricalArcState = new ElectricalArcState();
            ElectricalArcState.Configure(ArcConfig(new GdArray()));
            List<GdDict> resolutions = ResolveArcZoneWorldPositions();
            if (resolutions.Count == 0)
                return;
            var zoneIds = new GdArray();
            foreach (GdDict r in resolutions)
                zoneIds.Add(V.Str(r.Get("zone_id", ARC_ZONE_FALLBACK_ID)));
            ElectricalArcState.Configure(ArcConfig(zoneIds));
            foreach (GdDict r in resolutions)
            {
                Vec3 pos = r.Get("position", Vec3.Inf) is Vec3 p ? p : Vec3.Inf;
                string zoneId = V.Str(r.Get("zone_id", ARC_ZONE_FALLBACK_ID));
                string roomId = V.Str(r.Get("room_id", ""));
                var zone = new SessionZone
                {
                    Kind = "arc",
                    ZoneId = zoneId,
                    NodeName = "ElectricalArcZone_NonCriticalLink",
                    LocalPosition = pos,
                    CompartmentOrRoomId = roomId,
                    VisualState = "discharged",
                };
                zone.Meta["arc_zone_phase"] = "DISCHARGED";
                zone.Meta["label_text"] = ARC_ZONE_LABEL_TEXT_DISCHARGED;
                if (AwayFromStart && CurrentShip != null && RootValid(CurrentShip.SceneRoot))
                    zone.Parent = CurrentShip.SceneRoot;
                ArcZoneNodes.Add(zone);
                ArcZoneResolvedRoomIds.Add(roomId);
                Events.RaiseZoneSpawned(zone);
            }
            ArcZoneResolvedRoomId = ArcZoneResolvedRoomIds[0];
        }

        IShipLoaderView ActiveArcLoader()
        {
            if (AwayFromStart && CurrentShip != null && CurrentShip.SceneRoot is IShipLoaderView derelict && derelict.IsValid)
                return derelict;
            return Loader != null && Loader.IsValid ? Loader : null;
        }

        List<GdDict> ResolveArcZoneWorldPositions()
        {
            var resolved = new List<GdDict>();
            IShipLoaderView arcLoader = ActiveArcLoader();
            if (arcLoader == null)
                return resolved;
            IReadOnlyList<Vec3> markers = arcLoader.GetArcZoneMarkers();
            GdArray specs = arcLoader.GetArcZoneSpecs() ?? new GdArray();
            if (markers.Count != specs.Count)
                return resolved;
            for (int index = 0; index < markers.Count; index++)
            {
                if (markers[index] == Vec3.Inf)
                    continue;
                GdDict spec = specs[index] as GdDict ?? new GdDict();
                string zoneId = V.Str(spec.Get("id", spec.Get("zone_id", "")));
                if (zoneId.Length == 0)
                    zoneId = index == 0 ? ARC_ZONE_FALLBACK_ID : ARC_ZONE_FALLBACK_ID + "_" + index;
                resolved.Add(new GdDict
                {
                    { "position", markers[index] },
                    { "room_id", V.Str(spec.Get("to_room", spec.Get("from_room", ""))) },
                    { "zone_id", zoneId },
                });
            }
            return resolved;
        }

        void RefreshArcState(bool forceInitial)
        {
            if (ElectricalArcState == null)
                return;
            if (ArcZoneNodes.Count == 0)
                return;
            ApplyArcZoneSceneState();
        }

        void ApplyArcZoneSceneState()
        {
            if (ElectricalArcState == null || ArcZoneNodes.Count == 0)
                return;
            GdDict summary = ElectricalArcState.GetSummary();
            bool arcing = summary.GetBool("arcing");
            string stateText = V.Str(summary.Get("state", "DISCHARGED"));
            foreach (SessionZone zone in ArcZoneNodes)
            {
                zone.Meta["arc_zone_phase"] = stateText;
                zone.Meta["arc_zone_passability_blocked"] = arcing;
                zone.Meta["label_text"] = arcing ? ARC_ZONE_LABEL_TEXT_ARCING : ARC_ZONE_LABEL_TEXT_DISCHARGED;
                zone.CollisionEnabled = arcing;
                zone.VisualState = arcing ? "arcing" : "discharged";
                Events.RaiseZoneStateChanged(zone);
            }
        }

        public GdDict GetArcSummary()
        {
            if (ElectricalArcState == null)
            {
                return new GdDict
                {
                    { "hazard_kind", "electrical_arc" }, { "state", "DISCHARGED" }, { "phase", 0L }, { "time_in_state", 0.0 },
                    { "cycle_duration", 0.0 }, { "arcing", false }, { "passability_blocked", false }, { "arcing_duration", 0.0 },
                    { "discharged_duration", 0.0 }, { "zone_ids", new GdArray() },
                };
            }
            return ElectricalArcState.GetSummary();
        }

        public long GetArcZoneCollisionEnabledCount()
        {
            long n = 0;
            foreach (SessionZone z in ArcZoneNodes)
                if (z.CollisionEnabled) n++;
            return n;
        }
    }
}
