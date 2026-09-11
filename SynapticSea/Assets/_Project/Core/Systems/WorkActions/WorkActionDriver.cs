// Ported from scripts/systems/work_action_driver.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-B2.2b: pure interact-chain driver for WorkActions. The scene/coordinator owns hold-to-work input and range;
    /// this owns start/tick, completion resolve, inventory yields, the noise pulse and XP event ids. Never touches the
    /// scene tree.
    /// </summary>
    public class WorkActionDriver
    {
        /// <summary>A target exposing a <c>player_noise</c> property (dict-shaped doubles and some managers).</summary>
        public interface IPlayerNoiseTarget
        {
            double PlayerNoise { get; set; }
        }

        /// <summary>
        /// ThreatManager-like target with <c>set_player_signals(noise, light, sight, crouching, room_id)</c> and the
        /// matching player_* properties (Runtime implements it).
        /// </summary>
        public interface IPlayerSignalsTarget
        {
            double PlayerLight { get; }
            double PlayerSight { get; }
            bool PlayerCrouching { get; }
            string PlayerRoomId { get; }
            void SetPlayerSignals(double noise, double light, double sight, bool crouching, string roomId);
        }

        public const double PROGRESS_NOISE_INTERVAL = 1.0;
        /// <summary>Fraction of verb noise per pulse.</summary>
        public const double PROGRESS_NOISE_FRACTION = 0.35;

        public WorkActionCatalog Catalog;
        public SkillEffectsResolver SkillEffects;
        public WorkActionState Work;
        public GdDict LastResolve = new GdDict();
        public double LastNoisePulse = 0.0;
        public string LastXpEvent = "";
        public GdDict PendingYields = new GdDict();
        public double CartMass = 0.0;
        public double CartCapacity = 100.0;
        public bool Overloaded = false;
        /// <summary>Progress-noise accumulator (loud strip verbs pulse while working).</summary>
        double _progressNoiseAcc = 0.0;
        public double LastProgressNoise = 0.0;

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            Catalog = new WorkActionCatalog();
            Catalog.LoadDefault();
            SkillEffects = new SkillEffectsResolver();
            SkillEffects.LoadDefault();
            Work = null;
            LastResolve = new GdDict();
            LastNoisePulse = 0.0;
            LastXpEvent = "";
            PendingYields = new GdDict();
            CartMass = V.F64(config.Get("cart_mass", 0.0));
            CartCapacity = Math.Max(1.0, V.F64(config.Get("cart_capacity", 100.0)));
            Overloaded = CartMass > CartCapacity;
            _progressNoiseAcc = 0.0;
            LastProgressNoise = 0.0;
        }

        public bool IsWorking()
        {
            if (Work == null) return false;
            return Work.Status == WorkActionState.STATUS_ACTIVE;
        }

        public string GetStatus()
        {
            if (Work == null) return WorkActionState.STATUS_IDLE;
            return Work.Status;
        }

        public double ProgressRatio()
        {
            if (Work == null) return 0.0;
            return Work.ProgressRatio();
        }

        /// <summary>Builds a start context from tool, skill, inventory and optional progression.</summary>
        public GdDict BuildContext(
            string toolClass,
            string skillId,
            long skillLevel,
            GdDict inventory,
            object progression = null,
            string classId = "",
            bool damaged = false)
        {
            string verb = "";
            var ctx = new GdDict
            {
                { "tool_class", toolClass },
                { "skill_id", skillId },
                { "skill_level", skillLevel },
                { "inventory", inventory.DeepCopy() },
                { "damaged", damaged },
                { "work_speed_mult", 1.0 },
            };
            if (SkillEffects != null && Work != null)
            {
                GdDict def = Work.GetSummary().GetDictOrEmpty("definition");
                verb = V.Str(def.Get("verb", ""));
            }
            if (SkillEffects != null)
            {
                GdDict frag = SkillEffects.BuildWorkContext(progression, verb, skillId, classId);
                ctx["work_speed_mult"] = V.F64(frag.Get("work_speed_mult", 1.0));
                if (V.I64(frag.Get("skill_level", 0L)) > skillLevel) ctx["skill_level"] = V.I64(frag["skill_level"]);
            }
            return ctx;
        }

        /// <summary>
        /// Starts <paramref name="actionId"/> on <paramref name="targetId"/>. GDScript read an optional
        /// <c>context["progression"]</c> object; a GdDict cannot hold it, so it is the <paramref name="progression"/>
        /// parameter here (non-null = the key was present).
        /// </summary>
        public bool StartAction(string actionId, string targetId, GdDict context = null, object progression = null)
        {
            context = context ?? new GdDict();
            LastResolve = new GdDict();
            LastNoisePulse = 0.0;
            LastXpEvent = "";
            PendingYields = new GdDict();
            _progressNoiseAcc = 0.0;
            LastProgressNoise = 0.0;
            if (Catalog == null || !Catalog.HasAction(actionId)) return false;
            GdDict def = Catalog.GetAction(actionId);
            string verb = V.Str(def.Get("verb", ""));
            // Cart overload blocks strip/cut yields path start (still allow weld/repair).
            if (Overloaded && (verb == "unbolt" || verb == "pry" || verb == "cut")) return false;
            Work = new WorkActionState();
            Work.ConfigureAction(actionId, def);
            GdDict startCtx = context.DeepCopy();
            // Inject skill work speed if progression provided.
            if (SkillEffects != null && progression != null)
            {
                GdDict frag = SkillEffects.BuildWorkContext(progression, verb, V.Str(startCtx.Get("skill_id", "")), V.Str(startCtx.Get("class_id", "")));
                if (!startCtx.Has("work_speed_mult")) startCtx["work_speed_mult"] = V.F64(frag.Get("work_speed_mult", 1.0));
            }
            return Work.Start(targetId, startCtx);
        }

        /// <summary>
        /// Ticks active work and returns the status. Loud strip verbs (cut/pry/unbolt) accumulate progress noise
        /// pulses so dismantling under threat pressure has continuous detection teeth.
        /// </summary>
        public string Tick(double delta, GdDict context = null)
        {
            LastProgressNoise = 0.0;
            if (Work == null) return WorkActionState.STATUS_IDLE;
            string st = Work.Tick(delta, context ?? new GdDict());
            if (st == WorkActionState.STATUS_ACTIVE && delta > 0.0)
            {
                double noise = Work.Noise();
                GdDict sum = Work.GetSummary();
                GdDict def = sum.Get("definition", new GdDict()) as GdDict ?? new GdDict();
                string verb = V.Str(def.Get("verb", ""));
                if (noise <= 0.0) noise = V.F64(def.Get("noise", 0.0));
                if ((verb == "cut" || verb == "pry" || verb == "unbolt") && noise > 0.05)
                {
                    _progressNoiseAcc += delta;
                    if (_progressNoiseAcc >= PROGRESS_NOISE_INTERVAL)
                    {
                        _progressNoiseAcc = 0.0;
                        LastProgressNoise = noise * PROGRESS_NOISE_FRACTION;
                        LastNoisePulse = Math.Max(LastNoisePulse, LastProgressNoise);
                    }
                }
            }
            // Auto-resolve on completion is opt-in via Complete() so the scene can choose the module map.
            return st;
        }

        /// <summary>Completes against a module map + simple inventory dictionary; returns the resolve dictionary.</summary>
        public GdDict Complete(ModuleIntegrityMap moduleMap = null, GdDict inventory = null)
        {
            inventory = inventory ?? new GdDict();
            LastResolve = new GdDict();
            LastNoisePulse = 0.0;
            LastXpEvent = "";
            PendingYields = new GdDict();
            if (Work == null) return new GdDict { { "ok", false }, { "reason", "no_work" } };
            if (Work.Status != WorkActionState.STATUS_COMPLETED) return new GdDict { { "ok", false }, { "reason", "not_completed" } };
            string targetId = Work.TargetId;
            // Consume materials first (if any).
            GdDict consumed = Work.MaterialsConsumed();
            if (!consumed.IsEmpty)
            {
                if (!WorkActionResolver.ConsumeFromInventory(inventory, consumed))
                {
                    Work.Reset();
                    return new GdDict { { "ok", false }, { "reason", "consume_failed" } };
                }
            }
            GdDict res = WorkActionResolver.ResolveCompletion(Work, moduleMap, targetId);
            if (!V.Bool(res.Get("ok", false))) return res;
            GdDict yields = res.Get("yields", new GdDict()) as GdDict ?? new GdDict();
            // Cart overload: reject yields that would overfill (leave on floor as pending).
            double yieldMass = EstimateYieldMass(yields);
            if (CartMass + yieldMass > CartCapacity && yieldMass > 0.0)
            {
                PendingYields = yields.DeepCopy();
                Overloaded = true;
                res["cart_overload"] = true;
                res["yields_applied"] = false;
            }
            else
            {
                WorkActionResolver.ApplyYieldsToInventory(inventory, yields);
                CartMass += yieldMass;
                res["yields_applied"] = true;
                res["cart_overload"] = false;
                if (CartMass > CartCapacity) Overloaded = true;
            }
            LastNoisePulse = V.F64(res.Get("noise", 0.0));
            LastXpEvent = V.Str(res.Get("xp_event", ""));
            LastResolve = res.DeepCopy();
            // PKG-D10: stamp the audio event id for scene/SfxEventRouter consumers.
            string verb = V.Str(res.Get("verb", ""));
            res["audio_event"] = AudioEventSeam.SfxForWorkVerb(verb);
            LastResolve["audio_event"] = res["audio_event"];
            return res;
        }

        /// <summary>Routes the completion SFX through an optional SfxEventRouter; returns the routed bus or "".</summary>
        public string EmitCompletionSfx(SfxEventRouter sfxRouter)
        {
            string eid = V.Str(LastResolve.Get("audio_event", ""));
            if (eid.Length == 0 || sfxRouter == null) return "";
            GdDict routed = sfxRouter.Route(eid, false);
            return routed != null ? V.Str(routed.Get("bus", "")) : "";
        }

        public void Interrupt() => Work?.Interrupt();

        public void Reset()
        {
            Work?.Reset();
            Work = null;
            LastResolve = new GdDict();
            LastNoisePulse = 0.0;
            LastXpEvent = "";
        }

        /// <summary>
        /// Applies the noise pulse to a DetectionState / ThreatManager-like target: a GdDict (<c>player_noise</c>), an
        /// <see cref="IPlayerNoiseTarget"/>, an <see cref="IPlayerSignalsTarget"/> and/or a <see cref="DetectionState"/>
        /// (<c>noise_level</c>). Returns the pulse applied (0 when none).
        /// </summary>
        public double ApplyNoiseToDetection(object detectionOrManager)
        {
            if (LastNoisePulse <= 0.0 || detectionOrManager == null) return 0.0;
            // Dict-shaped test doubles expose player_noise as a key.
            if (detectionOrManager is GdDict d)
            {
                d["player_noise"] = Math.Max(V.F64(d.Get("player_noise", 0.0)), LastNoisePulse);
                return LastNoisePulse;
            }
            if (detectionOrManager is IPlayerNoiseTarget noiseTarget)
                noiseTarget.PlayerNoise = Math.Max(noiseTarget.PlayerNoise, LastNoisePulse);
            if (detectionOrManager is IPlayerSignalsTarget signals)
            {
                // ThreatManager: boost the noise channel.
                signals.SetPlayerSignals(LastNoisePulse, signals.PlayerLight, signals.PlayerSight, signals.PlayerCrouching, signals.PlayerRoomId ?? "");
            }
            if (detectionOrManager is DetectionState detection)
                detection.NoiseLevel = Math.Max(detection.NoiseLevel, LastNoisePulse);
            return LastNoisePulse;
        }

        /// <summary>
        /// Emits XP via the TrainingEventBus when <see cref="LastXpEvent"/> is a known training event id, else via
        /// <c>progression.GrantXp(skill, amount)</c>.
        /// </summary>
        public bool ApplyXp(TrainingEventBus trainingBus = null, PlayerProgressionState progression = null, long amount = 15)
        {
            if (LastXpEvent.Length == 0) return false;
            if (trainingBus != null && trainingBus.IsKnown(LastXpEvent))
            {
                trainingBus.Emit(LastXpEvent, "work_action", progression);
                return true;
            }
            if (progression != null)
            {
                // Map xp_event string to skill when possible.
                string skill = LastXpEvent;
                if (skill == "weld") skill = "welding";
                else if (skill == "salvage") skill = "scavenging";
                progression.GrantXp(skill, amount);
                return true;
            }
            return false;
        }

        public GdDict GetPersistenceSummary() => PillarPersistence.PackWorkAction(Work);

        public bool ApplyPersistenceSummary(GdDict summary)
        {
            Work = PillarPersistence.UnpackWorkAction(summary);
            return Work != null;
        }

        /// <summary>Candidate targets for a verb from a module map + component placement (pure ids).</summary>
        public GdArray ListTargets(string verb, ModuleIntegrityMap moduleMap = null, ComponentPlacementState placement = null)
        {
            var output = new GdArray();
            if (moduleMap != null)
            {
                foreach (var d in moduleMap.ToSparseDeltas())
                {
                    if (!(d is GdDict delta)) continue;
                    output.Append(new GdDict
                    {
                        { "target_id", V.Str(delta.Get("module_id", "")) },
                        { "kind", "module" },
                        { "verb", verb },
                    });
                }
            }
            if (placement != null && placement.Placed != null)
            {
                foreach (var e in placement.Placed)
                {
                    if (!(e is GdDict entry)) continue;
                    if (!V.Bool(entry.Get("mounted", true))) continue;
                    output.Append(new GdDict
                    {
                        { "target_id", V.Str(entry.Get("component_instance_id", "")) },
                        { "kind", "component" },
                        { "verb", verb },
                    });
                }
            }
            return output;
        }

        static double EstimateYieldMass(GdDict yields)
        {
            double mass = 0.0;
            foreach (var kv in yields) mass += 2.0 * V.F64(kv.Value); // 2 mass units per scrap unit (simple)
            return mass;
        }
    }
}
