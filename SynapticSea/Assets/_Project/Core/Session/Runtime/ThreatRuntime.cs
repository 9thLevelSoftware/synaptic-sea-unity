// Ported from scripts/systems/threat_manager.gd @ 96ecb2b0 (the pure half; the Node3D placeholders are events).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// The model half of <c>ThreatManager</c> (a Node3D in Godot): encounter spawning, per-threat AI ticks, detection,
    /// the damage pipeline, nav-graph pathfollowing and the summary that rides <c>inventory_summary.threat_summary</c>.
    /// The placeholder meshes it parented under itself become <see cref="PlaceholderSpawned"/> /
    /// <see cref="PlaceholderMoved"/> / <see cref="PlaceholderRemoved"/> / <see cref="PlaceholdersCleared"/>, which the
    /// Runtime <c>ThreatPlaceholderView</c> applies.
    /// </summary>
    public sealed class ThreatRuntime : WorkActionDriver.IPlayerNoiseTarget, WorkActionDriver.IPlayerSignalsTarget
    {
        public const string THREAT_ARCHETYPE_PATH = "res://data/combat/threat_archetypes.json";
        public const string WEAPON_DEFINITIONS_PATH = "res://data/combat/weapon_definitions.json";
        public const string AMMO_DEFINITIONS_PATH = "res://data/combat/ammo_definitions.json";
        public const double SIGHT_RANGE = 12.0;
        public const double REPATH_INTERVAL = 0.35;
        public const double REPATH_TARGET_MOVE = 1.25;

        static readonly string[] FallbackArchetypes = { "biomatter_swarm", "puppet_corpse", "stalker", "mimic", "hull_tendril" };

        /// <summary><c>signal threat_killed(record)</c>. The record's <c>position</c> is a <see cref="Vec3"/>.</summary>
        public event Action<GdDict> ThreatKilled;

        /// <summary><see cref="ThreatAttacked"/> target kind: a threat hit the player (result = the DamagePipeline vitals result).</summary>
        public const string ATTACK_TARGET_PLAYER = "player";

        /// <summary><see cref="ThreatAttacked"/> target kind: a threat applied structure damage (result = {structure_damage, source_id}).</summary>
        public const string ATTACK_TARGET_STRUCTURE = "structure";

        /// <summary><see cref="ThreatAttacked"/> target kind: the player's weapon hit this threat (result = the weapon attack result).</summary>
        public const string ATTACK_TARGET_THREAT = "threat";

        /// <summary>
        /// Unity-port addition (D3): raised wherever <see cref="LastAttackResult"/> is set, plus structure attacks:
        /// <c>(threat_instance_id, target_kind, damage, result)</c>. <c>target_kind</c> is one of the ATTACK_TARGET_*
        /// constants; <c>damage</c> is the final damage (structure amount for structure attacks). <c>result</c> is a copy
        /// that also carries <c>position</c>: the threat's world position (<see cref="Vec3"/>, Godot frame).
        /// </summary>
        public event Action<string, string, double, GdDict> ThreatAttacked;

        /// <summary>
        /// Unity-port addition (C4): scales the spawned count per encounter marker (fractional carry across markers keeps the
        /// total at <c>sum(count) * modifier</c>). 1.0 = Godot.
        /// </summary>
        public double EncounterDensityModifier = 1.0;

        /// <summary>Unity-port addition (C4): multiplies a newly spawned threat's attack damage and divides its attack interval. 1.0 = Godot.</summary>
        public double AggressionModifier = 1.0;

        /// <summary>RUNTIME: <c>_spawn_placeholder(threat, index, anchor)</c> built a ThreatPlaceholderRenderer node at the threat's world position.</summary>
        public event Action<ThreatAIState, long> PlaceholderSpawned;

        /// <summary>RUNTIME: <c>_update_placeholder</c>: the placeholder moved to the given world position (y-bob applied).</summary>
        public event Action<string, Vec3> PlaceholderMoved;

        /// <summary>RUNTIME: <c>_remove_threat</c> freed the placeholder of the given instance id.</summary>
        public event Action<string> PlaceholderRemoved;

        /// <summary>RUNTIME: <c>_clear_runtime_nodes</c> freed every child node.</summary>
        public event Action PlaceholdersCleared;

        public GdDict ThreatArchetypes = new GdDict();
        public GdDict WeaponDefinitions = new GdDict();
        public GdDict AmmoDefinitions = new GdDict();
        public GdArray EncounterMarkers = new GdArray();
        public readonly List<ThreatAIState> Threats = new List<ThreatAIState>();
        public DetectionState DetectionState = new DetectionState();
        public DamagePipeline DamagePipeline = new DamagePipeline();
        public double PlayerNoiseValue = 0.1;
        public double PlayerLightValue = 0.35;
        public double PlayerSightValue = 0.5;
        public bool PlayerCrouchingValue;
        public string PlayerRoomIdValue = "";
        public Vec3 FallbackAnchor = Vec3.Zero;
        public double AwarenessIndicator;
        public bool CombatEngaged;
        public GdDict LastAttackResult = new GdDict();

        /// <summary>instance_id -> placeholder alive (the Godot <c>placeholder_nodes</c> keys).</summary>
        readonly GdDict _placeholderNodes = new GdDict();
        readonly GdDict _rewardedKills = new GdDict();
        string _lastAttackWeaponId = "";

        /// <summary>ADR-0049: pure nav graph for pathfollowing (null = legacy hold still).</summary>
        public ShipNavGraph NavGraph;

        /// <summary>
        /// Unity port: the scene's NavMesh agents, which move the threats when a ship is built
        /// (<see cref="Session.IThreatNavigation"/>). Null headless, where the nav graph moves them instead. The
        /// graph is still built either way: it resolves each threat's room and the FLEE goal.
        /// </summary>
        public Session.IThreatNavigation Navigation;

        /// <summary>instance_id -> {waypoints, index, target, repath_cooldown}.</summary>
        readonly GdDict _pathRuntime = new GdDict();

        /// <summary>PKG-C4.1b: room-graph perception. Null = legacy same-room only.</summary>
        public SpatialPerceptionState SpatialPerception;

        /// <summary>threat.instance_id -> bool (physics raycast result from the scene).</summary>
        public GdDict EngagedLos = new GdDict();

        /// <summary>REQ-MI-004: Callable(threat, amount) when a threat applies structure_damage.</summary>
        public Action<ThreatAIState, double> OnStructureAttack;

        /// <summary><c>_ready()</c>: load catalogs and configure the pipeline + detection.</summary>
        public ThreatRuntime()
        {
            ThreatArchetypes = LoadJsonDict(THREAT_ARCHETYPE_PATH);
            WeaponDefinitions = LoadJsonDict(WEAPON_DEFINITIONS_PATH);
            AmmoDefinitions = LoadJsonDict(AMMO_DEFINITIONS_PATH);
            DamagePipeline.Configure(new GdDict());
            DetectionState.Configure(new GdDict());
        }

        // WorkActionDriver.IPlayerNoiseTarget / IPlayerSignalsTarget (apply_noise_to_detection duck-typing)
        public double PlayerNoise { get => PlayerNoiseValue; set => PlayerNoiseValue = value; }
        public double PlayerLight => PlayerLightValue;
        public double PlayerSight => PlayerSightValue;
        public bool PlayerCrouching => PlayerCrouchingValue;
        public string PlayerRoomId => PlayerRoomIdValue;

        public void ConfigureForLayout(GdDict layout, GdArray markers, Vec3 anchor)
        {
            FallbackAnchor = anchor;
            EncounterMarkers = (markers ?? new GdArray()).DeepCopy();
            if (EncounterMarkers.IsEmpty)
                EncounterMarkers = FallbackMarkersFromLayout(layout);
            ConfigureNavGraph(layout);
            ConfigureSpatialPerception(layout);
            SpawnFromMarkers(EncounterMarkers, FallbackAnchor);
        }

        /// <summary>PKG-C4.1b: build SpatialPerceptionState from layout room_links / blocked_links.</summary>
        public long ConfigureSpatialPerception(GdDict layout)
        {
            SpatialPerception = new SpatialPerceptionState();
            long n = SpatialPerception.ConfigureFromLayout(layout ?? new GdDict());
            EngagedLos.Clear();
            return n;
        }

        /// <summary>Scene injects one raycast result per engaged threat (FRAME). Missing keys mean "unknown".</summary>
        public void SetEngagedLos(string instanceId, bool hasLos)
        {
            if (string.IsNullOrEmpty(instanceId))
                return;
            EngagedLos[instanceId] = hasLos;
        }

        public void ClearEngagedLos() => EngagedLos.Clear();

        /// <summary>ADR-0049: (re)build the pure nav graph for the active ship layout.</summary>
        public long ConfigureNavGraph(GdDict layout)
        {
            NavGraph = new ShipNavGraph();
            long n = NavGraph.BuildFromLayout(layout ?? new GdDict());
            _pathRuntime.Clear();
            return n;
        }

        /// <summary>fire_rooms: room_id -> intensity; blocked_bulkheads: Array of [a,b] pairs (or {a,b} dicts).</summary>
        public void UpdateNavDynamicCosts(GdDict fireRooms, GdArray blockedBulkheads)
        {
            if (NavGraph == null)
                return;
            NavGraph.ResetDynamicCosts();
            fireRooms = fireRooms ?? new GdDict();
            if (!fireRooms.IsEmpty)
                NavGraph.ApplyFireCosts(fireRooms);
            foreach (object pair in blockedBulkheads ?? new GdArray())
            {
                if (pair is GdArray arr && arr.Count >= 2)
                    NavGraph.BlockBulkhead(V.Str(arr[0]), V.Str(arr[1]));
                else if (pair is GdDict d)
                    NavGraph.BlockBulkhead(V.Str(d.Get("a", "")), V.Str(d.Get("b", "")));
            }
            Navigation?.SetAvoidedCells(BurningCells(fireRooms), NavGraph.CellSize);
        }

        /// <summary>
        /// The nav graph's cell centres inside burning rooms, for the scene's NavMesh to charge the same way the
        /// graph does (<see cref="ShipNavGraph.ApplyFireCosts"/>).
        /// </summary>
        GdArray BurningCells(GdDict fireRooms)
        {
            var cells = new GdArray();
            if (NavGraph == null || fireRooms == null || fireRooms.IsEmpty)
                return cells;
            foreach (object key in NavGraph.Nodes.Keys)
            {
                string nodeId = V.Str(key);
                if (!fireRooms.Has(NavGraph.GetNodeRoom(nodeId)))
                    continue;
                Vec3 pos = NavGraph.GetNodePos(nodeId);
                if (pos != Vec3.Inf)
                    cells.Append(pos);
            }
            return cells;
        }

        public void InjectValidationEncounter(GdArray archetypeIds, Vec3 anchor)
        {
            var markers = new GdArray();
            long idx = 0;
            foreach (object archetypeId in archetypeIds)
            {
                markers.Add(new GdDict
                {
                    { "id", "validation_" + idx },
                    { "room_id", "validation_room_" + idx },
                    { "cell", GdArray.Of(idx, 0L) },
                    { "encounter_kind", V.Str(archetypeId) },
                    { "count", 1L },
                });
                idx += 1;
            }
            EncounterMarkers = markers;
            SpawnFromMarkers(markers, anchor, false);
        }

        public void SetPlayerSignals(double noise, double light, double sight, bool crouching, string roomId = "")
        {
            PlayerNoiseValue = GdMath.Clampf(noise, 0.0, 2.0);
            PlayerLightValue = GdMath.Clampf(light, 0.0, 2.0);
            PlayerSightValue = GdMath.Clampf(sight, 0.0, 2.0);
            PlayerCrouchingValue = crouching;
            PlayerRoomIdValue = roomId ?? "";
        }

        static Vec3 ThreatPos(ThreatAIState threat) =>
            new Vec3(V.F64(threat.WorldPosition[0]), V.F64(threat.WorldPosition[1]), V.F64(threat.WorldPosition[2]));

        public void TickThreats(double delta, VitalsState vitalsState, StatusEffectsState statusEffectsState, GdDict playerArmorProfile, Vec3 playerPosition)
        {
            DetectionState.UpdateInputs(PlayerNoiseValue, PlayerLightValue, PlayerSightValue, PlayerCrouchingValue, PlayerRoomIdValue);
            DetectionState.Tick(delta);
            AwarenessIndicator = 0.0;
            CombatEngaged = false;
            GdDict profile = DetectionState.GetEmittedProfile();
            // Godot iterates `threats` while _advance_threat_motion may not remove entries; removal happens in the sweep.
            foreach (ThreatAIState threat in new List<ThreatAIState>(Threats))
            {
                if (threat == null)
                    continue;
                bool sameRoom = PlayerRoomIdValue.Length == 0 || threat.RoomId == PlayerRoomIdValue;
                double prox = ProximityFactor(threat, playerPosition);
                double playerDistance = 0.0;
                if (threat.WorldPosition.Count >= 3)
                    playerDistance = playerPosition.DistanceTo(ThreatPos(threat));
                double sightMult = prox;
                double noiseAt = V.F64(profile["noise"]);
                bool canSee = sameRoom;
                if (SpatialPerception != null && PlayerRoomIdValue.Length > 0 && threat.RoomId.Length > 0)
                {
                    canSee = SpatialPerception.CanSee(PlayerRoomIdValue, threat.RoomId);
                    noiseAt = SpatialPerception.AttenuateNoise(PlayerRoomIdValue, threat.RoomId, V.F64(profile["noise"]));
                    if (!canSee)
                        sightMult = 0.0;
                }
                if (EngagedLos.Has(threat.InstanceId))
                {
                    if (!V.Bool(EngagedLos[threat.InstanceId]))
                    {
                        canSee = false;
                        sightMult = 0.0;
                    }
                    else
                    {
                        canSee = true;
                    }
                }
                bool engageSame = sameRoom && canSee;
                threat.Tick(delta, new GdDict
                {
                    { "noise_level", noiseAt },
                    { "light_level", V.F64(profile["light"]) * (canSee ? 1.0 : 0.35) },
                    { "sight_level", V.F64(profile["visibility"]) * sightMult },
                    { "crouching", false },
                    { "room_id", PlayerRoomIdValue },
                    { "same_room", engageSame },
                    { "detect_threshold", DetectionState.DetectThreshold },
                    { "player_position", playerPosition },
                    { "player_distance", playerDistance },
                });
                AwarenessIndicator = Math.Max(AwarenessIndicator, threat.AwarenessScore);
                if (engageSame && threat.CanAttack() && vitalsState != null)
                {
                    LastAttackResult = DamagePipeline.ApplyToVitals(vitalsState, statusEffectsState, playerArmorProfile, new GdDict
                    {
                        { "damage_type", threat.AttackType },
                        { "amount", threat.AttackDamage },
                        { "noise", threat.AttackNoise },
                        { "status_effect_id", threat.StatusOnHit },
                        { "source_id", threat.InstanceId },
                    });
                    RaiseAttacked(threat, ATTACK_TARGET_PLAYER, FinalDamage(LastAttackResult), LastAttackResult);
                    double structAmt = threat.StructureDamage;
                    if (structAmt > 0.0 && OnStructureAttack != null)
                        OnStructureAttack(threat, structAmt);
                    if (structAmt > 0.0)
                        RaiseAttacked(threat, ATTACK_TARGET_STRUCTURE, structAmt, new GdDict { { "structure_damage", structAmt }, { "source_id", threat.InstanceId } });
                    threat.ConsumeAttack();
                    CombatEngaged = true;
                }
                AdvanceThreatMotion(threat, delta, playerPosition);
                UpdatePlaceholder(threat);
            }
            SweepDeadThreats();
        }

        public GdDict AttackWithWeapon(string weaponId, InventoryState inventoryState, EquipmentState equipmentState, AmmoState ammoState = null, string targetId = "")
        {
            if (inventoryState == null)
                throw new ArgumentNullException(nameof(inventoryState), "inventory_state dependency cannot be null");
            if (equipmentState == null)
                throw new ArgumentNullException(nameof(equipmentState), "equipment_state dependency cannot be null");
            GdDict weapon = WeaponDefinitions.Get(weaponId, new GdDict()) as GdDict ?? new GdDict();
            if (weapon.IsEmpty)
                return new GdDict { { "ok", false }, { "reason", "unknown_weapon" } };
            string primary = V.Str(equipmentState.GetEquipped("primary_hand"));
            string secondary = V.Str(equipmentState.GetEquipped("secondary_hand"));
            if (primary != weaponId && secondary != weaponId)
                return new GdDict { { "ok", false }, { "reason", "weapon_not_equipped" } };
            string ammoItemId = V.Str(weapon.Get("ammo_item_id", ""));
            if (ammoItemId.Length > 0)
            {
                if (ammoState == null)
                    return new GdDict { { "ok", false }, { "reason", "no_ammo" }, { "ammo_item_id", ammoItemId } };
                if (ammoState.IsReloading())
                    return new GdDict { { "ok", false }, { "reason", "reloading" }, { "ammo_item_id", ammoItemId } };
                if (!ammoState.Spend(weaponId))
                    return new GdDict { { "ok", false }, { "reason", "empty_magazine" }, { "ammo_item_id", ammoItemId } };
            }
            ThreatAIState target = PickTarget(targetId);
            if (target == null)
                return new GdDict { { "ok", false }, { "reason", "no_target" } };
            GdDict result = DamagePipeline.ApplyToThreat(target, new GdDict
            {
                { "damage_type", V.Str(weapon.Get("damage_type", "physical")) },
                { "amount", V.F64(weapon.Get("damage", 0.0)) },
                { "noise", V.F64(weapon.Get("noise", 0.0)) },
                { "stun_seconds", V.F64(weapon.Get("stun_seconds", 0.0)) },
                { "status_effect_id", V.Str(weapon.Get("status_effect_id", "")) },
                { "source_id", weaponId },
            });
            PlayerNoiseValue = Math.Max(PlayerNoiseValue, V.F64(weapon.Get("noise", 0.0)));
            AwarenessIndicator = Math.Max(AwarenessIndicator, PlayerNoiseValue);
            result["ok"] = true;
            result["weapon_id"] = weaponId;
            result["target_id"] = target.InstanceId;
            result["ammo_item_id"] = ammoItemId;
            result["ammo_remaining"] = ammoState != null && ammoItemId.Length > 0 ? ammoState.Loaded(weaponId) : -1L;
            LastAttackResult = result.DeepCopy();
            _lastAttackWeaponId = weaponId;
            RaiseAttacked(target, ATTACK_TARGET_THREAT, FinalDamage(result), result);
            return result;
        }

        static double FinalDamage(GdDict result) => V.F64(result.Get("final_damage", result.Get("amount", 0.0)));

        void RaiseAttacked(ThreatAIState threat, string targetKind, double damage, GdDict result)
        {
            if (ThreatAttacked == null || threat == null)
                return;
            GdDict copy = (result ?? new GdDict()).DeepCopy();
            copy["position"] = threat.WorldPosition.Count >= 3 ? ThreatPos(threat) : Vec3.Zero;
            ThreatAttacked(threat.InstanceId, targetKind, damage, copy);
        }

        public GdDict GetSummary()
        {
            var threatSummaries = new GdArray();
            foreach (ThreatAIState threat in Threats)
                threatSummaries.Add(threat.GetSummary());
            return new GdDict
            {
                { "encounter_markers", EncounterMarkers.DeepCopy() },
                { "threats", threatSummaries },
                { "detection", DetectionState.GetSummary() },
                { "awareness_indicator", AwarenessIndicator },
                { "combat_engaged", CombatEngaged },
                { "last_attack_result", LastAttackResult.DeepCopy() },
                { "damage_pipeline", DamagePipeline.GetSummary() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            EncounterMarkers = summary.Get("encounter_markers", new GdArray()) is GdArray markers ? markers.DeepCopy() : new GdArray();
            if (summary.Get("detection", null) is GdDict detection)
                DetectionState.ApplySummary(detection);
            if (summary.Get("damage_pipeline", null) is GdDict pipeline)
                DamagePipeline.ApplySummary(pipeline);
            AwarenessIndicator = V.F64(summary.Get("awareness_indicator", 0.0));
            CombatEngaged = V.Bool(summary.Get("combat_engaged", false));
            // Godot assigns the dictionary itself (no duplicate).
            LastAttackResult = summary.Get("last_attack_result", new GdDict()) as GdDict ?? new GdDict();
            ClearRuntimeNodes();
            long idx = 0;
            if (summary.Get("threats", new GdArray()) is GdArray rawThreats)
            {
                foreach (object entry in rawThreats)
                {
                    if (!(entry is GdDict entryDict))
                        continue;
                    var threat = new ThreatAIState();
                    threat.Configure(entryDict);
                    Threats.Add(threat);
                    SpawnPlaceholder(threat, idx);
                    idx += 1;
                }
            }
            return true;
        }

        public List<string> GetStatusLines()
        {
            long alive = 0;
            long attacking = 0;
            foreach (ThreatAIState threat in Threats)
            {
                if (threat.Health > 0.0)
                    alive += 1;
                if (threat.State == ThreatAIState.STATE_ATTACK)
                {
                    attacking += 1;
                    CombatEngaged = true;
                }
            }
            return new List<string>
            {
                "Threats: alive=" + alive + " archetypes=" + UniqueArchetypeCount() + " attacking=" + attacking,
                "Threat Indicator: " + GdString.FormatFixed(AwarenessIndicator, 2) + " detected=" + (DetectionState.Detected ? "true" : "false"),
            };
        }

        public bool HasCombatEngagement() => CombatEngaged;

        public long GetActiveThreatCount() => Threats.Count;

        public long GetDetectedThreatCount()
        {
            long count = 0;
            foreach (ThreatAIState threat in Threats)
            {
                if (threat.State == ThreatAIState.STATE_INVESTIGATE || threat.State == ThreatAIState.STATE_HUNT || threat.State == ThreatAIState.STATE_ATTACK)
                    count += 1;
            }
            return count;
        }

        void SpawnFromMarkers(GdArray markers, Vec3 anchor, bool applyRunModifiers = true)
        {
            ClearRuntimeNodes();
            long idx = 0;
            double density = applyRunModifiers ? Math.Max(0.0, EncounterDensityModifier) : 1.0;
            double aggression = applyRunModifiers ? GdMath.Clampf(AggressionModifier, 0.1, 3.0) : 1.0;
            double carry = 0.0;
            foreach (object markerObj in markers)
            {
                if (!(markerObj is GdDict marker))
                    continue;
                string encounterKind = NormalizeEncounterKind(V.Str(marker.Get("encounter_kind", "biomatter_swarm")));
                long count = Math.Max(1L, V.I64(marker.Get("count", 1L)));
                if (density != 1.0)
                {
                    double want = count * density + carry;
                    long scaled = (long)Math.Floor(want + 1e-9);
                    carry = Math.Max(0.0, want - scaled);
                    count = scaled;
                }
                object localPos = marker.Get("local_position", null);
                for (long i = 0; i < count; i++)
                {
                    GdDict def = ThreatArchetypes.Get(encounterKind, new GdDict()) as GdDict ?? new GdDict();
                    if (def.IsEmpty)
                        continue;
                    var threat = new ThreatAIState();
                    GdDict merged = def.DeepCopy();
                    merged["instance_id"] = V.Str(marker.Get("id", encounterKind)) + "_" + i;
                    merged["archetype_id"] = encounterKind;
                    merged["room_id"] = V.Str(marker.Get("room_id", ""));
                    merged["cell"] = marker.Get("cell", GdArray.Of(0L, 0L));
                    if (localPos is GdArray lp && lp.Count >= 3)
                    {
                        merged["world_position"] = GdArray.Of(
                            (double)anchor.X + V.F64(lp[0]) + (double)i * 0.5,
                            (double)anchor.Y + V.F64(lp[1]),
                            (double)anchor.Z + V.F64(lp[2]));
                    }
                    else
                    {
                        merged["world_position"] = GdArray.Of(
                            (double)anchor.X + Math.Cos(idx) * 4.0,
                            (double)anchor.Y,
                            (double)anchor.Z + Math.Sin(idx) * 4.0);
                    }
                    threat.Configure(merged);
                    if (aggression != 1.0)
                    {
                        threat.AttackDamage *= aggression;
                        threat.AttackInterval /= aggression;
                    }
                    Threats.Add(threat);
                    SpawnPlaceholder(threat, idx);
                    idx += 1;
                }
            }
        }

        static GdArray FallbackMarkersFromLayout(GdDict layout)
        {
            var markers = new GdArray();
            var roomIds = new List<string>();
            if ((layout ?? new GdDict()).Get("rooms", new GdArray()) is GdArray rooms)
            {
                foreach (object room in rooms)
                {
                    if (room is GdDict r)
                    {
                        string rid = V.Str(r.Get("id", ""));
                        if (rid.Length > 0)
                            roomIds.Add(rid);
                    }
                }
            }
            for (int i = 0; i < FallbackArchetypes.Length; i++)
            {
                markers.Add(new GdDict
                {
                    { "id", "fallback_" + i },
                    { "room_id", roomIds.Count > 0 ? roomIds[i % Math.Max(1, roomIds.Count)] : "fallback_room_" + i },
                    { "cell", GdArray.Of((long)i, 0L) },
                    { "encounter_kind", FallbackArchetypes[i] },
                    { "count", 1L },
                });
            }
            return markers;
        }

        static string NormalizeEncounterKind(string kind)
        {
            switch (kind)
            {
                case "biomatter_lurker": return "biomatter_swarm";
                case "breach_lurker": return "mimic";
                case "drone_scout": return "stalker";
                case "derelict_pirate": return "puppet_corpse";
                default: return kind;
            }
        }

        ThreatAIState PickTarget(string targetId = "")
        {
            foreach (ThreatAIState threat in Threats)
            {
                if (threat.Health <= 0.0)
                    continue;
                if (string.IsNullOrEmpty(targetId) || threat.InstanceId == targetId)
                    return threat;
            }
            return null;
        }

        void SpawnPlaceholder(ThreatAIState threat, long index)
        {
            _placeholderNodes[threat.InstanceId] = true;
            PlaceholderSpawned?.Invoke(threat, index);
        }

        /// <summary>Domain 2 (BP3): reward + remove threats that died this frame, exactly once.</summary>
        void SweepDeadThreats()
        {
            var dead = new List<ThreatAIState>();
            foreach (ThreatAIState threat in Threats)
            {
                if (threat != null && threat.Health <= 0.0 && !_rewardedKills.Has(threat.InstanceId))
                {
                    _rewardedKills[threat.InstanceId] = true;
                    dead.Add(threat);
                }
            }
            foreach (ThreatAIState threat in dead)
            {
                GdDict archetype = ThreatArchetypes.Get(threat.ArchetypeId, new GdDict()) as GdDict ?? new GdDict();
                ThreatKilled?.Invoke(new GdDict
                {
                    { "instance_id", threat.InstanceId },
                    { "archetype_id", threat.ArchetypeId },
                    { "position", ThreatPos(threat) },
                    { "loot_table", V.Str(archetype.Get("loot_table", "combat_drop_common")) },
                    { "weapon_id", _lastAttackWeaponId },
                });
                RemoveThreat(threat);
            }
        }

        void RemoveThreat(ThreatAIState threat)
        {
            Navigation?.Release(threat.InstanceId);
            if (_placeholderNodes.Has(threat.InstanceId))
                PlaceholderRemoved?.Invoke(threat.InstanceId);
            _placeholderNodes.Erase(threat.InstanceId);
            Threats.Remove(threat);
        }

        static double ProximityFactor(ThreatAIState threat, Vec3 playerPosition) =>
            GdMath.Clampf(1.0 - ThreatPos(threat).DistanceTo(playerPosition) / SIGHT_RANGE, 0.0, 1.0);

        void AdvanceThreatMotion(ThreatAIState threat, double delta, Vec3 playerPosition)
        {
            if (threat == null || delta <= 0.0)
                return;
            if (threat.State == ThreatAIState.STATE_IDLE || threat.State == ThreatAIState.STATE_STUN || threat.State == ThreatAIState.STATE_DEAD)
            {
                _pathRuntime.Erase(threat.InstanceId);
                Navigation?.Hold(threat.InstanceId);
                return;
            }
            double speed = threat.EffectiveMoveSpeed();
            if (speed <= 0.0)
            {
                Navigation?.Hold(threat.InstanceId);
                return;
            }
            Vec3 current = ThreatPos(threat);
            Vec3 target = MotionTargetFor(threat, playerPosition);
            if (target == Vec3.Inf)
            {
                Navigation?.Hold(threat.InstanceId);
                return;
            }
            if (threat.State == ThreatAIState.STATE_ATTACK)
            {
                double ar = threat.AttackRange;
                if (current.DistanceTo(playerPosition) <= ar)
                {
                    Navigation?.Hold(threat.InstanceId);
                    return;
                }
            }
            if (AdvanceOnNavMesh(threat, current, target, playerPosition, speed, delta))
                return;
            if (NavGraph == null || NavGraph.NodeCount() == 0)
            {
                Vec3 step = current.MoveToward(target, (float)(speed * delta));
                threat.WorldPosition = GdArray.Of((double)step.X, (double)step.Y, (double)step.Z);
                return;
            }
            GdDict rt = _pathRuntime.Get(threat.InstanceId, new GdDict()) as GdDict ?? new GdDict();
            if (rt.IsEmpty)
                rt = new GdDict { { "waypoints", new GdArray() }, { "index", 0L }, { "target", Vec3.Inf }, { "repath_cooldown", 0.0 } };
            rt["repath_cooldown"] = Math.Max(0.0, V.F64(rt.Get("repath_cooldown", 0.0)) - delta);
            bool needRepath = false;
            GdArray waypoints = rt.Get("waypoints", new GdArray()) as GdArray ?? new GdArray();
            Vec3 prevTarget = rt.Get("target", Vec3.Inf) is Vec3 pt ? pt : Vec3.Inf;
            if (waypoints.IsEmpty || V.I64(rt.Get("index", 0L)) >= waypoints.Count)
                needRepath = true;
            else if (prevTarget != Vec3.Inf && prevTarget.DistanceTo(target) > REPATH_TARGET_MOVE)
                needRepath = true;
            else if (V.F64(rt.Get("repath_cooldown", 0.0)) <= 0.0)
                needRepath = true;
            if (needRepath)
            {
                GdArray path;
                if (threat.State == ThreatAIState.STATE_FLEE)
                {
                    Vec3 fleeGoal = ThreatPathfinder.FarthestPoint(NavGraph, current, playerPosition);
                    path = ThreatPathfinder.FindPath(NavGraph, current, fleeGoal);
                }
                else
                {
                    path = ThreatPathfinder.FindPath(NavGraph, current, target);
                }
                rt["waypoints"] = path;
                rt["index"] = 0L;
                rt["target"] = target;
                rt["repath_cooldown"] = REPATH_INTERVAL;
                waypoints = path;
            }
            GdDict stepResult = ThreatPathfinder.StepAlongPath(waypoints, V.I64(rt.Get("index", 0L)), current, speed, delta);
            Vec3 newPos = stepResult.Get("position", current) is Vec3 np ? np : current;
            rt["index"] = V.I64(stepResult.Get("path_index", 0L));
            _pathRuntime[threat.InstanceId] = rt;
            ApplyMotionResult(threat, newPos);
        }

        /// <summary>Stores the position the threat reached and the room the nav graph puts it in.</summary>
        void ApplyMotionResult(ThreatAIState threat, Vec3 position)
        {
            threat.WorldPosition = GdArray.Of((double)position.X, (double)position.Y, (double)position.Z);
            if (NavGraph == null || NavGraph.NodeCount() == 0)
                return;
            string nodeId = NavGraph.NearestNode(position);
            if (nodeId.Length == 0)
                return;
            string roomId = NavGraph.GetNodeRoom(nodeId);
            if (roomId.Length > 0)
                threat.RoomId = roomId;
        }

        /// <summary>
        /// Unity port: in a scene the threat's <c>NavMeshAgent</c> walks it, and this only hands the agent its
        /// destination and stores where the agent got to. False falls through to the ADR-0049 A* stepping, which is
        /// what headless sessions run and what covers an agent that is off the NavMesh.
        /// </summary>
        bool AdvanceOnNavMesh(ThreatAIState threat, Vec3 current, Vec3 target, Vec3 playerPosition, double speed, double delta)
        {
            if (Navigation == null || !Navigation.HasNavMesh)
                return false;
            Vec3 goal = threat.State == ThreatAIState.STATE_FLEE ? FleeGoal(threat, current, playerPosition, delta) : target;
            if (!Navigation.TryAdvance(threat.InstanceId, current, goal, speed, delta, out Vec3 position))
                return false;
            if (threat.State != ThreatAIState.STATE_FLEE)
                _pathRuntime.Erase(threat.InstanceId);
            ApplyMotionResult(threat, position);
            return true;
        }

        /// <summary>
        /// Where a fleeing threat runs to: the nav graph's farthest reachable node, kept for
        /// <see cref="REPATH_INTERVAL"/> so the Dijkstra sweep does not run every frame (the graph path follower
        /// repaths on the same interval). Without a graph it simply runs directly away from the player.
        /// </summary>
        Vec3 FleeGoal(ThreatAIState threat, Vec3 current, Vec3 playerPosition, double delta)
        {
            GdDict rt = _pathRuntime.Get(threat.InstanceId, null) as GdDict ?? new GdDict();
            double cooldown = Math.Max(0.0, V.F64(rt.Get("repath_cooldown", 0.0)) - delta);
            Vec3 goal = rt.Get("target", Vec3.Inf) is Vec3 stored ? stored : Vec3.Inf;
            if (goal == Vec3.Inf || cooldown <= 0.0)
            {
                goal = NavGraph != null && NavGraph.NodeCount() != 0
                    ? ThreatPathfinder.FarthestPoint(NavGraph, current, playerPosition)
                    : current + (current - playerPosition);
                cooldown = REPATH_INTERVAL;
            }
            rt["target"] = goal;
            rt["repath_cooldown"] = cooldown;
            _pathRuntime[threat.InstanceId] = rt;
            return goal;
        }

        static Vec3 MotionTargetFor(ThreatAIState threat, Vec3 playerPosition)
        {
            switch (threat.State)
            {
                case ThreatAIState.STATE_HUNT:
                case ThreatAIState.STATE_ATTACK:
                    return playerPosition;
                case ThreatAIState.STATE_INVESTIGATE:
                    {
                        Vec3 lkp = threat.LastKnownWorldPosition();
                        if (lkp != Vec3.Inf)
                            return lkp;
                        return playerPosition;
                    }
                case ThreatAIState.STATE_FLEE:
                    return playerPosition;
                default:
                    return Vec3.Inf;
            }
        }

        void UpdatePlaceholder(ThreatAIState threat)
        {
            if (!_placeholderNodes.Has(threat.InstanceId))
                return;
            double yBob = threat.State == ThreatAIState.STATE_ATTACK ? 0.2 : 0.0;
            PlaceholderMoved?.Invoke(threat.InstanceId, new Vec3(V.F64(threat.WorldPosition[0]), V.F64(threat.WorldPosition[1]) + yBob, V.F64(threat.WorldPosition[2])));
        }

        void ClearRuntimeNodes()
        {
            if (Navigation != null)
                foreach (ThreatAIState threat in Threats)
                    if (threat != null) Navigation.Release(threat.InstanceId);
            PlaceholdersCleared?.Invoke();
            _placeholderNodes.Clear();
            _rewardedKills.Clear();
            Threats.Clear();
            _pathRuntime.Clear();
            CombatEngaged = false;
            AwarenessIndicator = 0.0;
        }

        long UniqueArchetypeCount()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ThreatAIState threat in Threats)
                seen.Add(threat.ArchetypeId);
            return seen.Count;
        }

        static GdDict LoadJsonDict(string path)
        {
            if (string.IsNullOrEmpty(path))
                return new GdDict();
            return CatalogRegistry.LoadDict(path) ?? new GdDict();
        }
    }
}
