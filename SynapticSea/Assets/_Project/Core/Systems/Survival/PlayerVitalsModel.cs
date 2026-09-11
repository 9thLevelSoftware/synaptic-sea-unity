// Ported from scripts/systems/player_vitals_model.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure formatting model for the player-vitals HUD panel (Phase 7 sub-project C). Turns raw model numbers into
    /// ASCII status lines. No persistence: vitals are live-derived each frame via the setters below.
    /// </summary>
    public sealed class PlayerVitalsModel : IStatusLineProvider
    {
        public const double BLOCKED_DISPLAY_SECONDS = 3.0;
        public const double DEFAULT_RECOVERY_THRESHOLD = 30.0;

        GdDict _oxygenSummary = new GdDict();
        double _loadRatio = 0.0;
        double _moveMultiplier = 1.0;
        double _weightSaved = 0.0;
        bool _repairChanneling = false;
        double _repairProgress = 0.0;
        string _blockedReason = "";
        double _blockedRemaining = 0.0;

        // REQ-SV: survival vitals summaries
        GdDict _vitalsSummary = new GdDict();
        GdDict _sanitySummary = new GdDict();
        GdDict _radiationSummary = new GdDict();
        GdDict _temperatureSummary = new GdDict();
        GdDict _statusEffectsSummary = new GdDict();

        public void ApplyOxygenSummary(GdDict summary) => _oxygenSummary = summary.DeepCopy();

        public void ApplyInventoryLoad(double loadRatio, double moveMultiplier, double weightSaved = 0.0)
        {
            _loadRatio = Math.Max(0.0, loadRatio);
            _moveMultiplier = moveMultiplier;
            _weightSaved = Math.Max(0.0, weightSaved);
        }

        public void SetRepairProgress(bool channeling, double progress)
        {
            _repairChanneling = channeling;
            _repairProgress = GdMath.Clampf(progress, 0.0, 1.0);
        }

        public void NotifyRepairBlocked(string reason)
        {
            _blockedReason = reason;
            _blockedRemaining = BLOCKED_DISPLAY_SECONDS;
        }

        public void ApplyVitalsSummary(GdDict summary) => _vitalsSummary = summary.DeepCopy();
        public void ApplySanitySummary(GdDict summary) => _sanitySummary = summary.DeepCopy();
        public void ApplyRadiationSummary(GdDict summary) => _radiationSummary = summary.DeepCopy();
        public void ApplyTemperatureSummary(GdDict summary) => _temperatureSummary = summary.DeepCopy();
        public void ApplyStatusEffectsSummary(GdDict summary) => _statusEffectsSummary = summary.DeepCopy();

        public void Tick(double delta)
        {
            if (_blockedRemaining > 0.0)
            {
                _blockedRemaining = Math.Max(0.0, _blockedRemaining - delta);
                if (_blockedRemaining <= 0.0) _blockedReason = "";
            }
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add(OxygenLine());
            string suit = SuitLine();
            if (suit != "") lines.Add(suit);
            lines.Add(LoadLine());
            string repair = RepairLine();
            if (repair != "") lines.Add(repair);
            // REQ-SV: append survival vitals lines
            lines.AddRange(VitalsLines());
            lines.AddRange(SanityLines());
            lines.AddRange(RadiationLines());
            lines.AddRange(TemperatureLines());
            lines.AddRange(StatusEffectsLines());
            return lines;
        }

        public GdDict GetVitalsSummary()
        {
            return new GdDict
            {
                { "oxygen", GdMath.RoundI(V.F64(_oxygenSummary.Get("oxygen", 0.0))) },
                { "breach_state", BreachState() },
                { "suit_drain_percent", SuitPercent() },
                { "load_percent", GdMath.RoundI(_loadRatio * 100.0) },
                { "heavy", _loadRatio > 1.0 },
                { "move_penalty_percent", GdMath.RoundI((1.0 - _moveMultiplier) * 100.0) },
                { "repair_line", RepairLine() },
                { "blocked_active", _blockedRemaining > 0.0 && !_repairChanneling },
                { "health", GdMath.RoundI(V.F64(_vitalsSummary.Get("health", 0.0))) },
                { "stamina", GdMath.RoundI(V.F64(_vitalsSummary.Get("stamina", 0.0))) },
                { "hunger", GdMath.RoundI(V.F64(_vitalsSummary.Get("hunger", 0.0))) },
                { "thirst", GdMath.RoundI(V.F64(_vitalsSummary.Get("thirst", 0.0))) },
                { "sanity", GdMath.RoundI(V.F64(_sanitySummary.Get("sanity", 0.0))) },
                { "radiation", GdMath.RoundI(V.F64(_radiationSummary.Get("radiation", 0.0))) },
                { "temperature", V.F64(_temperatureSummary.Get("temperature", 22.0)) },
                { "status_effects_count", V.I64(_statusEffectsSummary.Get("count", 0L)) },
            };
        }

        // --- line composers ---

        string OxygenLine()
        {
            long oxygen = GdMath.RoundI(V.F64(_oxygenSummary.Get("oxygen", 0.0)));
            string line = "Oxygen: " + SurvivalCompat.FormatD(oxygen);
            string state = BreachState();
            if (state == "breach")
                line += " (BREACH)";
            else if (state == "sealed")
                line += " (SEALED)";
            double threshold = V.F64(_oxygenSummary.Get("recovery_threshold", DEFAULT_RECOVERY_THRESHOLD));
            if ((double)oxygen <= threshold) line += " LOW";
            return line;
        }

        string BreachState()
        {
            if (V.Bool(_oxygenSummary.Get("breach_sealed", false))) return "sealed";
            if (V.Bool(_oxygenSummary.Get("breach_open", false))) return "breach";
            return "closed";
        }

        long SuitPercent()
        {
            double mult = V.F64(_oxygenSummary.Get("equipment_drain_multiplier", 1.0));
            return GdMath.RoundI((1.0 - mult) * 100.0);
        }

        string SuitLine()
        {
            double mult = V.F64(_oxygenSummary.Get("equipment_drain_multiplier", 1.0));
            if (mult >= 1.0) return "";
            return "Suit: -" + SurvivalCompat.FormatD(SuitPercent()) + "% O2 drain";
        }

        string LoadLine()
        {
            long pct = GdMath.RoundI(_loadRatio * 100.0);
            long savedKg = GdMath.RoundI(_weightSaved);
            string suffix = savedKg >= 1 ? " (bags -" + SurvivalCompat.FormatD(savedKg) + "kg)" : "";
            if (_loadRatio > 1.0)
            {
                long penalty = GdMath.RoundI((1.0 - _moveMultiplier) * 100.0);
                return "Load: " + SurvivalCompat.FormatD(pct) + "% HEAVY (-" + SurvivalCompat.FormatD(penalty) + "% move)" + suffix;
            }
            return "Load: " + SurvivalCompat.FormatD(pct) + "%" + suffix;
        }

        string RepairLine()
        {
            if (_repairChanneling)
                return "Repairing " + SurvivalCompat.FormatD(GdMath.RoundI(_repairProgress * 100.0)) + "%";
            if (_blockedRemaining > 0.0 && _blockedReason != "")
                return "Repair blocked: " + BlockedReasonText(_blockedReason);
            return "";
        }

        static string BlockedReasonText(string reason)
        {
            if (reason == "missing_parts") return "missing parts";
            if (reason == "missing_tools") return "missing tools";
            if (reason == "insufficient_skill") return "need higher repair skill";
            if (reason == "already_functional") return "already repaired";
            return reason;
        }

        // REQ-SV: survival vitals line composers

        List<string> VitalsLines()
        {
            var lines = new List<string>();
            double health = V.F64(_vitalsSummary.Get("health", 0.0));
            double maxHealth = V.F64(_vitalsSummary.Get("max_health", 100.0));
            double stamina = V.F64(_vitalsSummary.Get("stamina", 0.0));
            double maxStamina = V.F64(_vitalsSummary.Get("max_stamina", 100.0));
            double hunger = V.F64(_vitalsSummary.Get("hunger", 0.0));
            double maxHunger = V.F64(_vitalsSummary.Get("max_hunger", 100.0));
            double thirst = V.F64(_vitalsSummary.Get("thirst", 0.0));
            double maxThirst = V.F64(_vitalsSummary.Get("max_thirst", 100.0));
            lines.Add(VitalLine("Health", health, maxHealth, 25.0));
            lines.Add(VitalLine("Stamina", stamina, maxStamina, 20.0));
            lines.Add(VitalLine("Hunger", hunger, maxHunger, 15.0));
            lines.Add(VitalLine("Thirst", thirst, maxThirst, 15.0));
            if (V.Bool(_vitalsSummary.Get("hunger_stamina_cascade_active", false)))
                lines.Add("HUNGER LOW -> stamina recovery halved");
            if (V.Bool(_vitalsSummary.Get("thirst_vision_warning_active", false)))
                lines.Add("THIRST LOW -> vision impaired");
            return lines;
        }

        List<string> SanityLines()
        {
            var lines = new List<string>();
            double sanity = V.F64(_sanitySummary.Get("sanity", 0.0));
            double maxSanity = V.F64(_sanitySummary.Get("max_sanity", 100.0));
            long pct = maxSanity > 0.0 ? GdMath.RoundI((sanity / maxSanity) * 100.0) : 0;
            string suffix = "";
            if (sanity < 40.0) suffix = " CRITICAL";
            lines.Add("Sanity: " + SurvivalCompat.FormatD(pct) + "%" + suffix);
            if (V.Bool(_sanitySummary.Get("perception_pressure_active", false)))
                lines.Add("PERCEPTION PRESSURE -> hallucination risk");
            return lines;
        }

        List<string> RadiationLines()
        {
            var lines = new List<string>();
            double radiation = V.F64(_radiationSummary.Get("radiation", 0.0));
            double maxRadiation = V.F64(_radiationSummary.Get("max_radiation", 100.0));
            long pct = maxRadiation > 0.0 ? GdMath.RoundI((radiation / maxRadiation) * 100.0) : 0;
            string suffix = "";
            if (V.Bool(_radiationSummary.Get("health_drain_active", false))) suffix = " CRITICAL";
            lines.Add("Radiation: " + SurvivalCompat.FormatD(pct) + "%" + suffix);
            if (V.Bool(_radiationSummary.Get("health_drain_active", false)))
                lines.Add("RADIATION SICKNESS -> health drain");
            return lines;
        }

        List<string> TemperatureLines()
        {
            var lines = new List<string>();
            double temp = V.F64(_temperatureSummary.Get("temperature", 22.0));
            string suffix = "";
            if (!V.Bool(_temperatureSummary.Get("is_safe", true))) suffix = " DANGER";
            lines.Add("Temp: " + SurvivalCompat.FormatF(temp, 1) + "C" + suffix);
            if (!V.Bool(_temperatureSummary.Get("is_safe", true)))
                lines.Add("EXTREME TEMP -> thirst drain increased");
            return lines;
        }

        List<string> StatusEffectsLines()
        {
            var lines = new List<string>();
            var effects = new GdArray();
            object raw = _statusEffectsSummary.Get("effects", new GdArray());
            if (raw is GdArray arr) effects = arr;
            if (effects.IsEmpty) return lines;
            foreach (object item in effects)
            {
                if (item is GdDict e)
                {
                    string id = V.Str(e.Get("id", ""));
                    long stacks = V.I64(e.Get("stacks", 0L));
                    double dur = V.F64(e.Get("duration", 0.0));
                    if (id.Length != 0)
                        lines.Add("Status: " + id + " x" + SurvivalCompat.FormatD(stacks) + " (" + SurvivalCompat.FormatF(dur, 1) + "s)");
                }
            }
            return lines;
        }

        static string VitalLine(string name, double value, double maxv, double critical)
        {
            long pct = maxv > 0.0 ? GdMath.RoundI((value / maxv) * 100.0) : 0;
            string suffix = "";
            if (value <= critical) suffix = " CRITICAL";
            return name + ": " + SurvivalCompat.FormatD(pct) + "%" + suffix;
        }
    }
}
