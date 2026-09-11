// Ported from scripts/systems/oxygen_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Runtime model for the Gate 1 hazard pressure loop (ADR-0005 HazardStateContract).
    /// Never reaches into the scene tree; the ship coordinator applies scene consequences from the summary.
    /// Oxygen is a resource-drain model and intentionally does not use <see cref="PhaseTimer"/>.
    /// </summary>
    public sealed class OxygenState : IHazardState, ITickable
    {
        public const double DEFAULT_MAX_OXYGEN = 100.0;
        public const double DEFAULT_DRAIN_RATE = 6.0;
        public const double DEFAULT_REGEN_RATE = 3.5;
        public const double DEFAULT_RECOVERY_THRESHOLD = 30.0;
        public const double DEFAULT_SAFE_THRESHOLD = 35.0;
        public const string HAZARD_KIND = "oxygen";

        public GdArray BreachZoneIds = new GdArray();
        public double MaxOxygen = DEFAULT_MAX_OXYGEN;
        public double DrainRate = DEFAULT_DRAIN_RATE;
        public double RegenRate = DEFAULT_REGEN_RATE;
        public double RecoveryThreshold = DEFAULT_RECOVERY_THRESHOLD;
        public double SafeThreshold = DEFAULT_SAFE_THRESHOLD;

        public double Oxygen = DEFAULT_MAX_OXYGEN;
        public bool BreachOpen = true;
        public bool BreachSealed = false;
        public bool PassabilityBlocked = false;
        public bool LastPlayerInBreachZone = false;

        // Inventory/tool summary cache (REQ-007: the portable oxygen pump halves the drain).
        GdDict _inventorySummary = new GdDict();

        // Worn-equipment summary cache; stacks multiplicatively with the inventory multiplier.
        GdDict _equipmentSummary = new GdDict();

        public double EffectiveDrainRate = DEFAULT_DRAIN_RATE;

        public string HazardKind => HAZARD_KIND;

        /// <summary>
        /// Recognized keys: zone_ids, max_oxygen, drain_rate, regen_rate, recovery_threshold, safe_threshold.
        /// Resets oxygen to max, opens the breach when zone_ids is non-empty, and clears the sealed state.
        /// </summary>
        public void Configure(GdDict config)
        {
            BreachZoneIds.Clear();
            if (config != null && config.Has("zone_ids"))
            {
                object zoneIdsVariant = config["zone_ids"];
                if (zoneIdsVariant is GdArray zoneIds)
                {
                    foreach (object zoneIdVariant in zoneIds)
                    {
                        string zoneId = V.Str(zoneIdVariant);
                        if (zoneId.Length == 0) continue;
                        BreachZoneIds.Append(zoneId);
                    }
                }
            }
            if (config != null && config.Has("max_oxygen"))
                MaxOxygen = Math.Max(0.0, V.F64(config["max_oxygen"]));
            if (config != null && config.Has("drain_rate"))
                DrainRate = Math.Max(0.0, V.F64(config["drain_rate"]));
            if (config != null && config.Has("regen_rate"))
                RegenRate = Math.Max(0.0, V.F64(config["regen_rate"]));
            if (config != null && config.Has("recovery_threshold"))
                RecoveryThreshold = GdMath.Clampf(V.F64(config["recovery_threshold"]), 0.0, MaxOxygen);
            if (config != null && config.Has("safe_threshold"))
                SafeThreshold = GdMath.Clampf(V.F64(config["safe_threshold"]), RecoveryThreshold, MaxOxygen);
            Oxygen = MaxOxygen;
            BreachOpen = BreachZoneIds.Count > 0;
            BreachSealed = false;
            PassabilityBlocked = false;
            LastPlayerInBreachZone = false;
            _inventorySummary = new GdDict();
            _equipmentSummary = new GdDict();
            EffectiveDrainRate = DrainRate;
            RecomputePassabilityBlocked();
        }

        bool ITickable.Tick(double delta, GdDict context) => Tick(delta, (object)context);

        /// <summary>
        /// ADR-0005 tick. <paramref name="context"/> is either a context dictionary
        /// (player_in_breach_zone, field_atmosphere, field_atmosphere_multiplier, fire_oxygen_drain)
        /// or the legacy positional bool meaning player_in_breach_zone. Returns true when state changed.
        /// </summary>
        public bool Tick(double deltaSeconds, object context = null)
        {
            bool playerInBreachZone = false;
            bool fieldAtmosphere = false;
            double fieldAtmosphereMultiplier = 1.0;
            double fireOxygenDrain = 0.0; // Fire B2: extra O2/s from active fires
            if (context is bool legacy)
            {
                playerInBreachZone = legacy;
            }
            else if (context is GdDict ctx)
            {
                if (ctx.Has("player_in_breach_zone"))
                    playerInBreachZone = V.Bool(ctx["player_in_breach_zone"]);
                if (ctx.Has("field_atmosphere"))
                    fieldAtmosphere = V.Bool(ctx["field_atmosphere"]);
                if (ctx.Has("field_atmosphere_multiplier"))
                    fieldAtmosphereMultiplier = GdMath.Clampf(V.F64(ctx["field_atmosphere_multiplier"]), 0.0, 1.0);
                if (ctx.Has("fire_oxygen_drain"))
                    fireOxygenDrain = Math.Max(0.0, V.F64(ctx["fire_oxygen_drain"]));
            }
            LastPlayerInBreachZone = playerInBreachZone || fieldAtmosphere;
            if (deltaSeconds <= 0.0)
            {
                EffectiveDrainRate = DrainRate * (
                    fieldAtmosphere ? ComputeFieldDrainMultiplier() * fieldAtmosphereMultiplier : ComputeDrainMultiplier());
                return false;
            }
            bool changed = false;
            // Field atmosphere (boarded derelict) always drains suit O2, independent of the home breach seal.
            bool draining = fieldAtmosphere || (BreachOpen && !BreachSealed && playerInBreachZone);
            if (draining)
            {
                double multiplier = fieldAtmosphere
                    ? ComputeFieldDrainMultiplier() * fieldAtmosphereMultiplier
                    : ComputeDrainMultiplier();
                EffectiveDrainRate = DrainRate * multiplier;
                double drained = EffectiveDrainRate * deltaSeconds;
                if (drained > 0.0)
                {
                    Oxygen = Math.Max(0.0, Oxygen - drained);
                    changed = true;
                }
            }
            else
            {
                EffectiveDrainRate = DrainRate * ComputeDrainMultiplier();
                if (!playerInBreachZone && !fieldAtmosphere && Oxygen < MaxOxygen)
                {
                    double regenerated = RegenRate * deltaSeconds;
                    if (regenerated > 0.0)
                    {
                        Oxygen = Math.Min(MaxOxygen, Oxygen + regenerated);
                        changed = true;
                    }
                }
            }
            // Fire B2: active fires consume ambient/suit oxygen even outside a breach zone.
            if (fireOxygenDrain > 0.0 && Oxygen > 0.0)
            {
                double fireDrained = fireOxygenDrain * deltaSeconds;
                if (fireDrained > 0.0)
                {
                    Oxygen = Math.Max(0.0, Oxygen - fireDrained);
                    changed = true;
                }
            }
            // Home-corridor passability is a breach-zone consequence only; field drain must not flip it.
            if (!fieldAtmosphere) RecomputePassabilityBlocked();
            return changed;
        }

        /// <summary>REQ-007 hazard-side gate: sealed or closed breach forces the neutral 1.0.</summary>
        double ComputeDrainMultiplier()
        {
            if (BreachSealed || !BreachOpen) return 1.0;
            return SummaryDrainMult(_inventorySummary) * SummaryDrainMult(_equipmentSummary);
        }

        /// <summary>Field (derelict) drain always applies inventory/suit multipliers.</summary>
        double ComputeFieldDrainMultiplier() =>
            SummaryDrainMult(_inventorySummary) * SummaryDrainMult(_equipmentSummary);

        static double SummaryDrainMult(GdDict summary)
        {
            object value = summary.Get("drain_multiplier", 1.0);
            if (value is double || value is long) return V.F64(value);
            return 1.0;
        }

        public void ApplyInventorySummary(GdDict summary)
        {
            _inventorySummary = summary.DeepCopy();
        }

        public void ApplyEquipmentSummary(GdDict summary)
        {
            _equipmentSummary = summary.DeepCopy();
        }

        public bool SealBreach(string zoneId)
        {
            if (!BreachOpen) return false;
            if (BreachSealed) return false;
            BreachOpen = false;
            BreachSealed = true;
            RecomputePassabilityBlocked();
            return true;
        }

        public bool ApplyShipSystemsSummary(GdDict summary)
        {
            // Completing objective 2 (main_power_restored) seals the breach.
            if (!V.Bool(summary.Get("main_power_restored", false))) return false;
            if (BreachSealed) return false;
            BreachOpen = false;
            BreachSealed = true;
            RecomputePassabilityBlocked();
            return true;
        }

        public bool IsPassabilityBlocked() => PassabilityBlocked;

        public bool IsPlayerInBreachZone() => LastPlayerInBreachZone;

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "hazard_kind", HAZARD_KIND },
                { "oxygen", Oxygen },
                { "max_oxygen", MaxOxygen },
                { "drain_rate", DrainRate },
                { "effective_drain_rate", EffectiveDrainRate },
                { "drain_multiplier", ComputeDrainMultiplier() },
                { "equipment_drain_multiplier", SummaryDrainMult(_equipmentSummary) },
                { "regen_rate", RegenRate },
                { "recovery_threshold", RecoveryThreshold },
                { "safe_threshold", SafeThreshold },
                { "breach_open", BreachOpen },
                { "breach_sealed", BreachSealed },
                { "passability_blocked", PassabilityBlocked },
                { "player_in_breach_zone", LastPlayerInBreachZone },
                { "breach_zone_ids", BreachZoneIds.ShallowCopy() },
            };
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (Oxygen <= 0.001)
                lines.Add("Oxygen: 0 BREACH BLOCKED");
            else if (Oxygen <= RecoveryThreshold + 0.001)
                lines.Add("Oxygen: " + SurvivalCompat.FormatD(GdMath.RoundI(Oxygen)) + " LOW");
            else if (Oxygen <= SafeThreshold + 0.001)
                lines.Add("Oxygen: " + SurvivalCompat.FormatD(GdMath.RoundI(Oxygen)));
            else
                lines.Add("Oxygen: " + SurvivalCompat.FormatD(GdMath.RoundI(Oxygen)));
            if (BreachSealed)
                lines.Add("Breach: SEALED");
            else if (BreachOpen)
                lines.Add("Breach: OPEN");
            else
                lines.Add("Breach: CLOSED");
            return lines;
        }

        void RecomputePassabilityBlocked()
        {
            if (Oxygen <= RecoveryThreshold + 0.001 && !BreachSealed)
                PassabilityBlocked = true;
            else
                PassabilityBlocked = false;
        }

        /// <summary>
        /// Rejects a mismatched hazard_kind or an empty summary. Returns true when any field changed.
        /// The inventory/equipment summaries are intentionally not restored (the coordinator re-feeds them live).
        /// </summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (V.Str(summary.Get("hazard_kind", "")) != HAZARD_KIND) return false;
            bool changed = false;
            double newOxygen = V.F64(summary.Get("oxygen", Oxygen));
            if (Math.Abs(newOxygen - Oxygen) > 0.001)
            {
                Oxygen = GdMath.Clampf(newOxygen, 0.0, MaxOxygen);
                changed = true;
            }
            double newMax = V.F64(summary.Get("max_oxygen", MaxOxygen));
            if (Math.Abs(newMax - MaxOxygen) > 0.001)
            {
                MaxOxygen = Math.Max(0.0, newMax);
                changed = true;
            }
            double newDrain = V.F64(summary.Get("drain_rate", DrainRate));
            if (Math.Abs(newDrain - DrainRate) > 0.001)
            {
                DrainRate = Math.Max(0.0, newDrain);
                changed = true;
            }
            double newRegen = V.F64(summary.Get("regen_rate", RegenRate));
            if (Math.Abs(newRegen - RegenRate) > 0.001)
            {
                RegenRate = Math.Max(0.0, newRegen);
                changed = true;
            }
            double newRecovery = V.F64(summary.Get("recovery_threshold", RecoveryThreshold));
            if (Math.Abs(newRecovery - RecoveryThreshold) > 0.001)
            {
                RecoveryThreshold = GdMath.Clampf(newRecovery, 0.0, MaxOxygen);
                changed = true;
            }
            double newSafe = V.F64(summary.Get("safe_threshold", SafeThreshold));
            if (Math.Abs(newSafe - SafeThreshold) > 0.001)
            {
                SafeThreshold = GdMath.Clampf(newSafe, RecoveryThreshold, MaxOxygen);
                changed = true;
            }
            bool newBreachOpen = V.Bool(summary.Get("breach_open", BreachOpen));
            if (newBreachOpen != BreachOpen)
            {
                BreachOpen = newBreachOpen;
                changed = true;
            }
            bool newBreachSealed = V.Bool(summary.Get("breach_sealed", BreachSealed));
            if (newBreachSealed != BreachSealed)
            {
                BreachSealed = newBreachSealed;
                changed = true;
            }
            bool newPlayerIn = V.Bool(summary.Get("player_in_breach_zone", LastPlayerInBreachZone));
            if (newPlayerIn != LastPlayerInBreachZone)
            {
                LastPlayerInBreachZone = newPlayerIn;
                changed = true;
            }
            object newZoneIdsVariant = summary.Get("breach_zone_ids", BreachZoneIds);
            if (newZoneIdsVariant is GdArray newZoneIds && !V.VariantEquals(newZoneIds, BreachZoneIds))
            {
                BreachZoneIds = new GdArray();
                foreach (object zoneId in newZoneIds) BreachZoneIds.Append(V.Str(zoneId));
                changed = true;
            }
            RecomputePassabilityBlocked();
            return changed;
        }
    }
}
