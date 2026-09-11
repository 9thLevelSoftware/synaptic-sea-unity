// Ported from scripts/systems/threat_ai_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// One threat's data-driven FSM (idle / investigate / hunt / telegraph / attack / flee / stun / dead).
    /// PKG-C4.2 behavior modifiers (ambush_hold, stalk_range, swarm_split, anchored, telegraph) shape one FSM
    /// per archetype. Pure model; the scene wrapper moves the body and applies attacks.
    /// </summary>
    public class ThreatAIState : ISimModel, ITickable
    {
        public const string STATE_IDLE = "idle";
        public const string STATE_INVESTIGATE = "investigate";
        public const string STATE_HUNT = "hunt";
        public const string STATE_ATTACK = "attack";
        public const string STATE_TELEGRAPH = "telegraph"; // PKG-C4.2 windup before strike
        public const string STATE_FLEE = "flee";
        public const string STATE_STUN = "stun";
        public const string STATE_DEAD = "dead";

        public string InstanceId = "";
        public string ArchetypeId = "";
        public string DisplayName = "Threat";
        public string RoomId = "";
        public GdArray Cell = GdArray.Of(0L, 0L);
        public GdArray WorldPosition = GdArray.Of(0.0, 0.0, 0.0);
        public string State = STATE_IDLE;
        public string PreviousState = STATE_IDLE;
        public double MaxHealth = 20.0;
        public double Health = 20.0;
        public double AttackDamage = 5.0;
        /// <summary>REQ-MI-004: optional structure damage applied to ModuleIntegrityMap (hull tendril).</summary>
        public double StructureDamage = 0.0;
        public string AttackType = "physical";
        public double AttackNoise = 0.4;
        public double AttackInterval = 1.4;
        public double AttackCooldown = 0.0;
        public double NoiseSensitivity = 1.0;
        public double LightSensitivity = 1.0;
        public double SightSensitivity = 1.0;
        public double MemorySeconds = 5.0;
        public double MemoryRemaining = 0.0;
        public double FleeThreshold = 0.15;
        public double StunnedRemaining = 0.0;
        public double AwarenessScore = 0.0;
        public string LastKnownRoom = "";
        public string StatusOnHit = "";
        public GdDict ArmorProfile = new GdDict();
        public GdArray Tags = new GdArray();
        /// <summary>ADR-0049: metres per second (base). Multipliers scale by AI state.</summary>
        public double MoveSpeed = 2.5;
        public double HuntSpeedMult = 1.0;
        public double FleeSpeedMult = 1.35;
        public double InvestigateSpeedMult = 0.7;
        public double AttackRange = 1.4;
        /// <summary>Last known player world position for INVESTIGATE (optional; room_id still used).</summary>
        public GdArray LastKnownPosition = new GdArray();
        // PKG-C4.2 data-driven FSM modifiers (one FSM, per-archetype behavior).
        public bool AmbushHold = false;
        public double StalkRange = 0.0;    // metres; 0 = attack immediately when same room
        public bool SwarmSplit = false;
        public bool Anchored = false;
        public double TelegraphSeconds = 0.0;
        public double TelegraphRemaining = 0.0;
        public string PlayerVerb = "fight"; // distinct player response verb

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            InstanceId = V.Str(config.Get("instance_id", InstanceId));
            ArchetypeId = V.Str(config.Get("archetype_id", ArchetypeId));
            DisplayName = V.Str(config.Get("display_name", DisplayName));
            RoomId = V.Str(config.Get("room_id", RoomId));
            object rawCell = config.Get("cell", Cell);
            if (rawCell is GdArray cellArr)
                Cell = cellArr.DeepCopy();
            object rawPos = config.Get("world_position", WorldPosition);
            if (rawPos is GdArray posArr && posArr.Count >= 3)
                WorldPosition = GdArray.Of(V.F64(posArr[0]), V.F64(posArr[1]), V.F64(posArr[2]));
            State = V.Str(config.Get("state", State));
            PreviousState = V.Str(config.Get("previous_state", PreviousState));
            MaxHealth = Math.Max(1.0, V.F64(config.Get("max_health", MaxHealth)));
            Health = GdMath.Clampf(V.F64(config.Get("health", MaxHealth)), 0.0, MaxHealth);
            AttackDamage = Math.Max(0.0, V.F64(config.Get("attack_damage", AttackDamage)));
            StructureDamage = Math.Max(0.0, V.F64(config.Get("structure_damage", StructureDamage)));
            AttackType = V.Str(config.Get("attack_type", AttackType));
            AttackNoise = Math.Max(0.0, V.F64(config.Get("attack_noise", AttackNoise)));
            AttackInterval = Math.Max(0.1, V.F64(config.Get("attack_interval", AttackInterval)));
            AttackCooldown = Math.Max(0.0, V.F64(config.Get("attack_cooldown", AttackCooldown)));
            NoiseSensitivity = Math.Max(0.0, V.F64(config.Get("noise_sensitivity", NoiseSensitivity)));
            LightSensitivity = Math.Max(0.0, V.F64(config.Get("light_sensitivity", LightSensitivity)));
            SightSensitivity = Math.Max(0.0, V.F64(config.Get("sight_sensitivity", SightSensitivity)));
            MemorySeconds = Math.Max(0.0, V.F64(config.Get("memory_seconds", MemorySeconds)));
            MemoryRemaining = Math.Max(0.0, V.F64(config.Get("memory_remaining", MemoryRemaining)));
            FleeThreshold = GdMath.Clampf(V.F64(config.Get("flee_threshold", FleeThreshold)), 0.0, 0.95);
            StunnedRemaining = Math.Max(0.0, V.F64(config.Get("stunned_remaining", StunnedRemaining)));
            AwarenessScore = GdMath.Clampf(V.F64(config.Get("awareness_score", AwarenessScore)), 0.0, 3.0);
            LastKnownRoom = V.Str(config.Get("last_known_room", LastKnownRoom));
            StatusOnHit = V.Str(config.Get("status_on_hit", StatusOnHit));
            object armor = config.Get("armor", config.Get("armor_profile", ArmorProfile));
            ArmorProfile = armor is GdDict armorDict ? armorDict.DeepCopy() : new GdDict();
            object rawTags = config.Get("tags", Tags);
            if (rawTags is GdArray tagsArr)
                Tags = tagsArr.DeepCopy();
            MoveSpeed = Math.Max(0.1, V.F64(config.Get("move_speed", MoveSpeed)));
            HuntSpeedMult = Math.Max(0.1, V.F64(config.Get("hunt_speed_mult", HuntSpeedMult)));
            FleeSpeedMult = Math.Max(0.1, V.F64(config.Get("flee_speed_mult", FleeSpeedMult)));
            InvestigateSpeedMult = Math.Max(0.1, V.F64(config.Get("investigate_speed_mult", InvestigateSpeedMult)));
            AttackRange = Math.Max(0.3, V.F64(config.Get("attack_range", AttackRange)));
            object lkp = config.Get("last_known_position", LastKnownPosition);
            if (lkp is GdArray lkpArr && lkpArr.Count >= 3)
                LastKnownPosition = GdArray.Of(V.F64(lkpArr[0]), V.F64(lkpArr[1]), V.F64(lkpArr[2]));
            // PKG-C4.2 behavior modifiers (top-level or nested "behavior")
            var beh = new GdDict();
            object behRaw = config.Get("behavior", new GdDict());
            if (behRaw is GdDict behDict)
                beh = behDict;
            AmbushHold = V.Bool(config.Get("ambush_hold", beh.Get("ambush_hold", AmbushHold)));
            StalkRange = Math.Max(0.0, V.F64(config.Get("stalk_range", beh.Get("stalk_range", StalkRange))));
            SwarmSplit = V.Bool(config.Get("swarm_split", beh.Get("swarm_split", SwarmSplit)));
            Anchored = V.Bool(config.Get("anchored", beh.Get("anchored", Anchored)));
            TelegraphSeconds = Math.Max(0.0, V.F64(config.Get("telegraph_seconds", beh.Get("telegraph_seconds", TelegraphSeconds))));
            TelegraphRemaining = Math.Max(0.0, V.F64(config.Get("telegraph_remaining", TelegraphRemaining)));
            PlayerVerb = V.Str(config.Get("player_verb", beh.Get("player_verb", PlayerVerb)));
            if (PlayerVerb.Length == 0)
                PlayerVerb = "fight";
        }

        public bool Tick(double delta, GdDict context = null)
        {
            context = context ?? new GdDict();
            if (delta < 0.0)
                return false;
            if (AttackCooldown > 0.0)
                AttackCooldown = Math.Max(0.0, AttackCooldown - delta);
            if (Health <= 0.0)
            {
                ChangeState(STATE_DEAD);
                return true;
            }
            if (StunnedRemaining > 0.0)
            {
                StunnedRemaining = Math.Max(0.0, StunnedRemaining - delta);
                MemoryRemaining = Math.Max(MemoryRemaining, delta);
                ChangeState(STATE_STUN);
                return true;
            }
            // Telegraph windup resolves into attack
            if (State == STATE_TELEGRAPH)
            {
                TelegraphRemaining = Math.Max(0.0, TelegraphRemaining - delta);
                if (TelegraphRemaining <= 0.0)
                    ChangeState(STATE_ATTACK);
                return true;
            }
            AwarenessScore = GdMath.Clampf(
                V.F64(context.Get(SimKeys.NoiseLevel, 0.0)) * NoiseSensitivity +
                V.F64(context.Get(SimKeys.LightLevel, 0.0)) * LightSensitivity +
                V.F64(context.Get(SimKeys.SightLevel, 0.0)) * SightSensitivity,
                0.0,
                3.0);
            double crouchMult = V.Bool(context.Get(SimKeys.Crouching, false)) ? 0.65 : 1.0;
            AwarenessScore *= crouchMult;
            // Swarm split: low HP spikes awareness / commitment
            if (SwarmSplit && Health / MaxHealth <= 0.55)
                AwarenessScore = Math.Min(3.0, AwarenessScore + 0.35);
            bool sameRoom = V.Bool(context.Get(SimKeys.SameRoom, true));
            double detectionThreshold = V.F64(context.Get(SimKeys.DetectThreshold, 0.85));
            double playerDistance = V.F64(context.Get("player_distance", 0.0));
            if (AwarenessScore >= detectionThreshold)
            {
                MemoryRemaining = MemorySeconds;
                LastKnownRoom = V.Str(context.Get(SimKeys.RoomId, RoomId));
                object ppos = context.Get(SimKeys.PlayerPosition, null);
                if (ppos is Vec3 pv)
                    LastKnownPosition = GdArray.Of((double)pv.X, (double)pv.Y, (double)pv.Z);
                else if (ppos is GdArray pArr && pArr.Count >= 3)
                    LastKnownPosition = GdArray.Of(V.F64(pArr[0]), V.F64(pArr[1]), V.F64(pArr[2]));
                if (sameRoom)
                    ResolveEngagement(playerDistance);
                else
                    ChangeState(STATE_HUNT);
            }
            else if (AmbushHold && State == STATE_IDLE && AwarenessScore < detectionThreshold)
            {
                // Stay hidden until committed detection
                ChangeState(STATE_IDLE);
            }
            else if (MemoryRemaining > 0.0)
            {
                MemoryRemaining = Math.Max(0.0, MemoryRemaining - delta);
                ChangeState(MemoryRemaining > MemorySeconds * 0.4 ? STATE_HUNT : STATE_INVESTIGATE);
            }
            else if (AwarenessScore > 0.35)
            {
                if (AmbushHold)
                    ChangeState(STATE_IDLE); // hold until full detect
                else
                    ChangeState(STATE_INVESTIGATE);
            }
            else
            {
                ChangeState(STATE_IDLE);
            }
            if (Health / MaxHealth <= FleeThreshold && Health > 0.0 && !Anchored)
                ChangeState(STATE_FLEE);
            return true;
        }

        void ResolveEngagement(double playerDistance)
        {
            // Stalk: keep hunting until within stalk_range (or attack_range if stalk unset).
            if (StalkRange > 0.0 && playerDistance > StalkRange)
            {
                ChangeState(STATE_HUNT);
                return;
            }
            EnterAttackPipeline();
        }

        void EnterAttackPipeline()
        {
            if (TelegraphSeconds > 0.0 && State != STATE_ATTACK && State != STATE_TELEGRAPH)
            {
                TelegraphRemaining = TelegraphSeconds;
                ChangeState(STATE_TELEGRAPH);
            }
            else
            {
                ChangeState(STATE_ATTACK);
            }
        }

        public bool CanAttack() => State == STATE_ATTACK && AttackCooldown <= 0.0 && Health > 0.0;

        public void ConsumeAttack()
        {
            AttackCooldown = AttackInterval;
        }

        public GdDict ApplyDamage(GdDict payload)
        {
            double damage = Math.Max(0.0, V.F64(payload.Get("final_damage", payload.Get("amount", 0.0))));
            Health = Math.Max(0.0, Health - damage);
            if (payload.Has("armor_profile") && payload.Get("armor_profile") is GdDict armor)
                ArmorProfile = armor.DeepCopy();
            double stunSeconds = Math.Max(0.0, V.F64(payload.Get("stun_seconds", 0.0)));
            if (stunSeconds > 0.0)
                StunnedRemaining = Math.Max(StunnedRemaining, stunSeconds);
            if (Health <= 0.0)
            {
                ChangeState(STATE_DEAD);
            }
            else if (stunSeconds > 0.0)
            {
                ChangeState(STATE_STUN);
            }
            else if (Health / MaxHealth <= FleeThreshold && !Anchored)
            {
                ChangeState(STATE_FLEE);
            }
            else if (SwarmSplit && Health > 0.0 && Health / MaxHealth <= 0.55)
            {
                // Swarm under pressure disperses (flee) unless anchored
                if (!Anchored)
                    ChangeState(STATE_FLEE);
            }
            return GetSummary();
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "instance_id", InstanceId },
                { "archetype_id", ArchetypeId },
                { "display_name", DisplayName },
                { "room_id", RoomId },
                { "cell", Cell.DeepCopy() },
                { "world_position", WorldPosition.DeepCopy() },
                { "state", State },
                { "previous_state", PreviousState },
                { "max_health", MaxHealth },
                { "health", Health },
                { "attack_damage", AttackDamage },
                { "attack_type", AttackType },
                { "attack_noise", AttackNoise },
                { "attack_interval", AttackInterval },
                { "attack_cooldown", AttackCooldown },
                { "noise_sensitivity", NoiseSensitivity },
                { "light_sensitivity", LightSensitivity },
                { "sight_sensitivity", SightSensitivity },
                { "memory_seconds", MemorySeconds },
                { "memory_remaining", MemoryRemaining },
                { "flee_threshold", FleeThreshold },
                { "stunned_remaining", StunnedRemaining },
                { "awareness_score", AwarenessScore },
                { "last_known_room", LastKnownRoom },
                { "status_on_hit", StatusOnHit },
                { "armor_profile", ArmorProfile.DeepCopy() },
                { "tags", Tags.DeepCopy() },
                { "move_speed", MoveSpeed },
                { "hunt_speed_mult", HuntSpeedMult },
                { "flee_speed_mult", FleeSpeedMult },
                { "investigate_speed_mult", InvestigateSpeedMult },
                { "attack_range", AttackRange },
                { "last_known_position", LastKnownPosition.DeepCopy() },
                { "ambush_hold", AmbushHold },
                { "stalk_range", StalkRange },
                { "swarm_split", SwarmSplit },
                { "anchored", Anchored },
                { "telegraph_seconds", TelegraphSeconds },
                { "telegraph_remaining", TelegraphRemaining },
                { "player_verb", PlayerVerb },
            };
        }

        public double EffectiveMoveSpeed()
        {
            if (Anchored && State != STATE_ATTACK && State != STATE_TELEGRAPH)
                return 0.0;
            switch (State)
            {
                case STATE_FLEE:
                    return MoveSpeed * FleeSpeedMult;
                case STATE_INVESTIGATE:
                    return MoveSpeed * InvestigateSpeedMult;
                case STATE_HUNT:
                case STATE_ATTACK:
                case STATE_TELEGRAPH:
                    return MoveSpeed * HuntSpeedMult;
                default:
                    return 0.0;
            }
        }

        /// <summary>PKG-C4.2: player-facing response verb for this archetype (distinct per role).</summary>
        public string GetPlayerVerb() => PlayerVerb;

        public Vec3 LastKnownWorldPosition()
        {
            if (LastKnownPosition.Count >= 3)
                return new Vec3(V.F64(LastKnownPosition[0]), V.F64(LastKnownPosition[1]), V.F64(LastKnownPosition[2]));
            return Vec3.Inf;
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            string before = GdJson.Stringify(GetSummary());
            Configure(summary);
            return before != GdJson.Stringify(GetSummary());
        }

        public List<string> GetStatusLines()
        {
            return new List<string>
            {
                DisplayName + ": " + State + " hp=" + GdString.FormatFixed(Health, 1) + "/" + GdString.FormatFixed(MaxHealth, 1),
                DisplayName + " awareness=" + GdString.FormatFixed(AwarenessScore, 2) + " room=" + RoomId +
                " memory=" + GdString.FormatFixed(MemoryRemaining, 1),
            };
        }

        void ChangeState(string nextState)
        {
            if (State == nextState)
                return;
            PreviousState = State;
            State = nextState;
        }
    }
}
