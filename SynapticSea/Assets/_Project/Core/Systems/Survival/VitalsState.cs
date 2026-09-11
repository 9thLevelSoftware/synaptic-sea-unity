// Ported from scripts/systems/vitals_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for player core vitals: health, stamina, hunger, thirst (REQ-SV-001). No scene-tree access.
    /// PKG-C3.1b: response curves (not cliffs) + cross-coupling (each stat feeds at least two others).
    /// Implements <see cref="EffectDispatcher.IVitalsTarget"/> (<c>apply_delta</c>) for the consumable pipeline.
    /// </summary>
    public sealed class VitalsState : ISimModel, ITickable, IStatusLineProvider, EffectDispatcher.IVitalsTarget
    {
        public const double DEFAULT_MAX_HEALTH = 100.0;
        public const double DEFAULT_MAX_STAMINA = 100.0;
        public const double DEFAULT_MAX_HUNGER = 100.0;
        public const double DEFAULT_MAX_THIRST = 100.0;
        public const double DEFAULT_HEALTH_DRAIN = 0.0;
        public const double DEFAULT_STAMINA_DRAIN = 2.0;
        public const double DEFAULT_HUNGER_DRAIN = 0.5;
        public const double DEFAULT_THIRST_DRAIN = 0.8;
        public const double DEFAULT_STAMINA_RECOVERY = 5.0;
        public const double DEFAULT_HEALTH_RECOVERY = 0.0;
        // Soft UI warning thresholds (not hard gameplay cliffs).
        public const double HUNGER_STAMINA_CASCADE_THRESHOLD = 30.0;
        public const double THIRST_VISION_WARNING_THRESHOLD = 20.0;
        public const double EXHAUSTION_STAMINA_THRESHOLD = 15.0;

        public double MaxHealth = DEFAULT_MAX_HEALTH;
        public double MaxStamina = DEFAULT_MAX_STAMINA;
        public double MaxHunger = DEFAULT_MAX_HUNGER;
        public double MaxThirst = DEFAULT_MAX_THIRST;
        public double HealthDrainRate = DEFAULT_HEALTH_DRAIN;
        public double StaminaDrainRate = DEFAULT_STAMINA_DRAIN;
        public double HungerDrainRate = DEFAULT_HUNGER_DRAIN;
        public double ThirstDrainRate = DEFAULT_THIRST_DRAIN;
        public double StaminaRecoveryRate = DEFAULT_STAMINA_RECOVERY;
        public double HealthRecoveryRate = DEFAULT_HEALTH_RECOVERY;

        public double Health = DEFAULT_MAX_HEALTH;
        public double Stamina = DEFAULT_MAX_STAMINA;
        public double Hunger = DEFAULT_MAX_HUNGER;
        public double Thirst = DEFAULT_MAX_THIRST;

        public void Configure(GdDict config)
        {
            if (config == null) config = new GdDict();
            MaxHealth = F(config, "max_health", DEFAULT_MAX_HEALTH);
            MaxStamina = F(config, "max_stamina", DEFAULT_MAX_STAMINA);
            MaxHunger = F(config, "max_hunger", DEFAULT_MAX_HUNGER);
            MaxThirst = F(config, "max_thirst", DEFAULT_MAX_THIRST);
            HealthDrainRate = F(config, "health_drain_rate", DEFAULT_HEALTH_DRAIN);
            StaminaDrainRate = F(config, "stamina_drain_rate", DEFAULT_STAMINA_DRAIN);
            HungerDrainRate = F(config, "hunger_drain_rate", DEFAULT_HUNGER_DRAIN);
            ThirstDrainRate = F(config, "thirst_drain_rate", DEFAULT_THIRST_DRAIN);
            StaminaRecoveryRate = F(config, "stamina_recovery_rate", DEFAULT_STAMINA_RECOVERY);
            HealthRecoveryRate = F(config, "health_recovery_rate", DEFAULT_HEALTH_RECOVERY);
            Health = GdMath.Clampf(F(config, "health", Health), 0.0, MaxHealth);
            Stamina = GdMath.Clampf(F(config, "stamina", Stamina), 0.0, MaxStamina);
            Hunger = GdMath.Clampf(F(config, "hunger", Hunger), 0.0, MaxHunger);
            Thirst = GdMath.Clampf(F(config, "thirst", Thirst), 0.0, MaxThirst);
        }

        /// <summary>Hermite smoothstep t in [0,1] to [0,1].</summary>
        public static double Smoothstep01(double t)
        {
            double x = GdMath.Clampf(t, 0.0, 1.0);
            return x * x * (3.0 - 2.0 * x);
        }

        /// <summary>Hunger to stamina recovery mult. Full recovery above 50% hunger; smooth down to 0.25 at empty.</summary>
        public static double HungerStaminaRecoveryCurve(double hungerValue, double maxH)
        {
            if (maxH <= 0.0) return 1.0;
            double r = GdMath.Clampf(hungerValue / maxH, 0.0, 1.0);
            if (r >= 0.5) return 1.0;
            double t = r / 0.5;
            return 0.25 + 0.75 * Smoothstep01(t);
        }

        /// <summary>Thirst to vision clarity mult (1.0 clear to 0.35 at empty). Continuous, not a cliff.</summary>
        public static double ThirstVisionCurve(double thirstValue, double maxT)
        {
            if (maxT <= 0.0) return 1.0;
            double r = GdMath.Clampf(thirstValue / maxT, 0.0, 1.0);
            if (r >= 0.35) return 1.0;
            double t = r / 0.35;
            return 0.35 + 0.65 * Smoothstep01(t);
        }

        /// <summary>Thirst to stamina drain mult when dehydrated (1.0 full to 1.75 empty).</summary>
        public static double ThirstStaminaDrainCurve(double thirstValue, double maxT)
        {
            if (maxT <= 0.0) return 1.0;
            double r = GdMath.Clampf(thirstValue / maxT, 0.0, 1.0);
            if (r >= 0.4) return 1.0;
            double deficit = 1.0 - (r / 0.4);
            return 1.0 + 0.75 * Smoothstep01(deficit);
        }

        /// <summary>Hunger to passive health drain when starving (0 at 25%+, up to 2.0/s at 0).</summary>
        public static double HungerHealthDrainCurve(double hungerValue, double maxH)
        {
            if (maxH <= 0.0) return 0.0;
            double r = GdMath.Clampf(hungerValue / maxH, 0.0, 1.0);
            if (r >= 0.25) return 0.0;
            double deficit = 1.0 - (r / 0.25);
            return 2.0 * Smoothstep01(deficit);
        }

        /// <summary>Stamina to movement mult. Full speed above 30%; smooth down to 0.35 at empty.</summary>
        public static double StaminaMoveCurve(double staminaValue, double maxS)
        {
            if (maxS <= 0.0) return 1.0;
            double r = GdMath.Clampf(staminaValue / maxS, 0.0, 1.0);
            if (r >= 0.3) return 1.0;
            double t = r / 0.3;
            return 0.35 + 0.65 * Smoothstep01(t);
        }

        /// <summary>Cold (below safe) raises hunger drain. Returns a mult of at least 1.</summary>
        public static double ColdHungerCurve(double temperature, double safeMin)
        {
            if (temperature >= safeMin) return 1.0;
            double deficit = GdMath.Clampf((safeMin - temperature) / 10.0, 0.0, 1.0);
            return 1.0 + 0.8 * Smoothstep01(deficit);
        }

        /// <summary>
        /// Updates all four vitals. Context keys are the <see cref="SimKeys"/> vitals hot-path keys
        /// (moving, *_health_drain, temperature/wound thirst and hunger mults, stamina recovery mults).
        /// </summary>
        public bool Tick(double deltaSeconds, GdDict context = null)
        {
            if (deltaSeconds <= 0.0) return false;
            if (context == null) context = new GdDict();
            bool changed = false;
            // Hunger -> stamina recovery (curve, not cliff).
            double staminaRecoveryMult = HungerStaminaRecoveryCurve(Hunger, MaxHunger);
            if (context.Has(SimKeys.StatusStaminaRecoveryMult))
                staminaRecoveryMult *= V.F64(context.Get(SimKeys.StatusStaminaRecoveryMult, 1.0));
            if (context.Has(SimKeys.SanityStaminaRecoveryMult))
                staminaRecoveryMult *= V.F64(context.Get(SimKeys.SanityStaminaRecoveryMult, 1.0));
            // Thirst -> stamina drain when moving
            double staminaDrainMult = ThirstStaminaDrainCurve(Thirst, MaxThirst);
            // Stamina
            bool moving = V.Bool(context.Get(SimKeys.Moving, true));
            if (moving)
            {
                double sDrain = StaminaDrainRate * staminaDrainMult * deltaSeconds;
                if (sDrain > 0.0 && Stamina > 0.0)
                {
                    Stamina = Math.Max(0.0, Stamina - sDrain);
                    changed = true;
                }
            }
            else
            {
                double sRecover = StaminaRecoveryRate * staminaRecoveryMult * deltaSeconds;
                if (sRecover > 0.0 && Stamina < MaxStamina)
                {
                    Stamina = Math.Min(MaxStamina, Stamina + sRecover);
                    changed = true;
                }
            }
            // Health (passive + hazards + wound bleed + starvation curve)
            double hDrain = HealthDrainRate * deltaSeconds;
            hDrain += HungerHealthDrainCurve(Hunger, MaxHunger) * deltaSeconds;
            if (context.Has(SimKeys.RadiationHealthDrain))
                hDrain += V.F64(context.Get(SimKeys.RadiationHealthDrain, 0.0)) * deltaSeconds;
            if (context.Has(SimKeys.AtmosphereHealthDrain))
                hDrain += V.F64(context.Get(SimKeys.AtmosphereHealthDrain, 0.0)) * deltaSeconds;
            if (context.Has(SimKeys.FireHealthDrain))
                hDrain += V.F64(context.Get(SimKeys.FireHealthDrain, 0.0)) * deltaSeconds;
            if (context.Has(SimKeys.SanityHealthDrain))
                hDrain += V.F64(context.Get(SimKeys.SanityHealthDrain, 0.0)) * deltaSeconds;
            if (context.Has(SimKeys.EncumbranceHealthDrain))
                hDrain += V.F64(context.Get(SimKeys.EncumbranceHealthDrain, 0.0)) * deltaSeconds;
            if (context.Has(SimKeys.WoundHealthDrain))
                hDrain += V.F64(context.Get(SimKeys.WoundHealthDrain, 0.0)) * deltaSeconds;
            if (hDrain > 0.0 && Health > 0.0)
            {
                Health = Math.Max(0.0, Health - hDrain);
                changed = true;
            }
            else if (HealthRecoveryRate > 0.0 && Health < MaxHealth)
            {
                double hRecover = HealthRecoveryRate * deltaSeconds;
                Health = Math.Min(MaxHealth, Health + hRecover);
                changed = true;
            }
            // Hunger (cold cascade via temperature_hunger_mult)
            double hgrMult = V.F64(context.Get(SimKeys.TemperatureHungerMult, 1.0));
            double hgrDrain = HungerDrainRate * Math.Max(0.0, hgrMult) * deltaSeconds;
            if (hgrDrain > 0.0 && Hunger > 0.0)
            {
                Hunger = Math.Max(0.0, Hunger - hgrDrain);
                changed = true;
            }
            // Thirst (temperature + wounds)
            double tMult = V.F64(context.Get(SimKeys.TemperatureThirstMult, 1.0));
            double woundT = V.F64(context.Get(SimKeys.WoundThirstMult, 1.0));
            double tDrain = ThirstDrainRate * Math.Max(0.0, tMult) * Math.Max(0.0, woundT) * deltaSeconds;
            if (tDrain > 0.0 && Thirst > 0.0)
            {
                Thirst = Math.Max(0.0, Thirst - tDrain);
                changed = true;
            }
            return changed;
        }

        public GdDict ApplyDelta(GdDict delta)
        {
            if (delta == null) delta = new GdDict();
            Health = GdMath.Clampf(Health + V.F64(delta.Get("health", 0.0)), 0.0, MaxHealth);
            Stamina = GdMath.Clampf(Stamina + V.F64(delta.Get("stamina", 0.0)), 0.0, MaxStamina);
            Hunger = GdMath.Clampf(Hunger + V.F64(delta.Get("hunger", 0.0)), 0.0, MaxHunger);
            Thirst = GdMath.Clampf(Thirst + V.F64(delta.Get("thirst", 0.0)), 0.0, MaxThirst);
            return GetSummary();
        }

        /// <summary>Domain 1 (survival_vitals stakes): true when the player has bled out.</summary>
        public bool IsIncapacitated() => Health <= 0.0;

        /// <summary>Domain 1: low-vitals action gating as a continuous movement-speed multiplier.</summary>
        public double GetMovementSpeedMultiplier()
        {
            if (IsIncapacitated()) return 0.0;
            return StaminaMoveCurve(Stamina, MaxStamina);
        }

        /// <summary>PKG-C3.1b: continuous vision clarity from thirst.</summary>
        public double GetVisionMultiplier() => ThirstVisionCurve(Thirst, MaxThirst);

        /// <summary>PKG-C3.1b: current hunger to stamina recovery mult.</summary>
        public double GetHungerStaminaRecoveryMult() => HungerStaminaRecoveryCurve(Hunger, MaxHunger);

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "health", Health },
                { "max_health", MaxHealth },
                { "stamina", Stamina },
                { "max_stamina", MaxStamina },
                { "hunger", Hunger },
                { "max_hunger", MaxHunger },
                { "thirst", Thirst },
                { "max_thirst", MaxThirst },
                { "health_drain_rate", HealthDrainRate },
                { "stamina_drain_rate", StaminaDrainRate },
                { "hunger_drain_rate", HungerDrainRate },
                { "thirst_drain_rate", ThirstDrainRate },
                { "stamina_recovery_rate", StaminaRecoveryRate },
                { "health_recovery_rate", HealthRecoveryRate },
                { "hunger_stamina_cascade_active", Hunger < HUNGER_STAMINA_CASCADE_THRESHOLD },
                { "thirst_vision_warning_active", Thirst < THIRST_VISION_WARNING_THRESHOLD },
                { "hunger_stamina_recovery_mult", GetHungerStaminaRecoveryMult() },
                { "vision_mult", GetVisionMultiplier() },
                { "move_mult", GetMovementSpeedMultiplier() },
                { "thirst_stamina_drain_mult", ThirstStaminaDrainCurve(Thirst, MaxThirst) },
                { "starvation_health_drain", HungerHealthDrainCurve(Hunger, MaxHunger) },
            };
        }

        /// <summary>GDScript <c>field_map</c>: summary key -> property name (identical names), in declaration order.</summary>
        static readonly string[] FieldMapKeys =
        {
            "health", "max_health",
            "stamina", "max_stamina",
            "hunger", "max_hunger",
            "thirst", "max_thirst",
            "health_drain_rate", "stamina_drain_rate",
            "hunger_drain_rate", "thirst_drain_rate",
            "stamina_recovery_rate", "health_recovery_rate",
        };

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            foreach (string key in FieldMapKeys)
            {
                if (summary.Has(key))
                {
                    double newVal = V.F64(summary.Get(key, 0.0));
                    double current = GetField(key);
                    if (Math.Abs(newVal - current) > 0.001)
                    {
                        SetField(key, newVal);
                        changed = true;
                    }
                }
            }
            // Re-clamp
            Health = GdMath.Clampf(Health, 0.0, MaxHealth);
            Stamina = GdMath.Clampf(Stamina, 0.0, MaxStamina);
            Hunger = GdMath.Clampf(Hunger, 0.0, MaxHunger);
            Thirst = GdMath.Clampf(Thirst, 0.0, MaxThirst);
            return changed;
        }

        double GetField(string key)
        {
            switch (key)
            {
                case "health": return Health;
                case "max_health": return MaxHealth;
                case "stamina": return Stamina;
                case "max_stamina": return MaxStamina;
                case "hunger": return Hunger;
                case "max_hunger": return MaxHunger;
                case "thirst": return Thirst;
                case "max_thirst": return MaxThirst;
                case "health_drain_rate": return HealthDrainRate;
                case "stamina_drain_rate": return StaminaDrainRate;
                case "hunger_drain_rate": return HungerDrainRate;
                case "thirst_drain_rate": return ThirstDrainRate;
                case "stamina_recovery_rate": return StaminaRecoveryRate;
                case "health_recovery_rate": return HealthRecoveryRate;
                default: throw new ArgumentException(key);
            }
        }

        void SetField(string key, double value)
        {
            switch (key)
            {
                case "health": Health = value; break;
                case "max_health": MaxHealth = value; break;
                case "stamina": Stamina = value; break;
                case "max_stamina": MaxStamina = value; break;
                case "hunger": Hunger = value; break;
                case "max_hunger": MaxHunger = value; break;
                case "thirst": Thirst = value; break;
                case "max_thirst": MaxThirst = value; break;
                case "health_drain_rate": HealthDrainRate = value; break;
                case "stamina_drain_rate": StaminaDrainRate = value; break;
                case "hunger_drain_rate": HungerDrainRate = value; break;
                case "thirst_drain_rate": ThirstDrainRate = value; break;
                case "stamina_recovery_rate": StaminaRecoveryRate = value; break;
                case "health_recovery_rate": HealthRecoveryRate = value; break;
                default: throw new ArgumentException(key);
            }
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>
            {
                VitalLine("Health", Health, MaxHealth, 25.0),
                VitalLine("Stamina", Stamina, MaxStamina, 20.0),
                VitalLine("Hunger", Hunger, MaxHunger, 15.0),
                VitalLine("Thirst", Thirst, MaxThirst, 15.0),
            };
            if (Hunger < HUNGER_STAMINA_CASCADE_THRESHOLD)
                lines.Add("HUNGER LOW -> stamina recovery ×" + GdString.FormatFixed(GetHungerStaminaRecoveryMult(), 2));
            if (Thirst < THIRST_VISION_WARNING_THRESHOLD)
                lines.Add("THIRST LOW -> vision impaired (×" + GdString.FormatFixed(GetVisionMultiplier(), 2) + ")");
            return lines;
        }

        static string VitalLine(string name, double value, double maxv, double critical)
        {
            long pct = maxv > 0.0 ? GdMath.RoundI((value / maxv) * 100.0) : 0;
            string suffix = "";
            if (value <= critical) suffix = " CRITICAL";
            return name + ": " + GdString.FormatInt(pct) + "%" + suffix;
        }

        static double F(GdDict config, string key, double fallback)
        {
            if (config.Has(key)) return Math.Max(0.0, V.F64(config.Get(key, fallback)));
            return fallback;
        }
    }
}
