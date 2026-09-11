// Ported from scripts/systems/electrical_arc_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Runtime model for the Alpha electrical-arc hazard (REQ-013, ADR-0005).
    /// Never reaches into the scene tree; the ship coordinator applies scene consequences from the summary.
    /// Owns a <see cref="PhaseTimer"/> (the only timer-based hazard): A = DISCHARGED (passable), B = ARCING (blocks).
    /// The cycle is short-safe (1.5s DISCHARGED) then short-danger (2.5s ARCING) on a 4.0s loop.
    /// </summary>
    public sealed class ElectricalArcState : IHazardState, ITickable
    {
        public const double DEFAULT_ARCING_DURATION = 2.5;
        public const double DEFAULT_DISCHARGED_DURATION = 1.5;
        public const string HAZARD_KIND = "electrical_arc";

        public enum Phase
        {
            DISCHARGED = 0,
            ARCING = 1,
        }

        public GdArray ZoneIds = new GdArray();
        public double ArcingDuration = DEFAULT_ARCING_DURATION;
        public double DischargedDuration = DEFAULT_DISCHARGED_DURATION;

        /// <summary>
        /// GDScript <c>var phase: int</c>. Renamed because C# cannot have a member and a nested type both named
        /// <c>Phase</c> (same convention as <see cref="PhaseTimer.CurrentPhaseValue"/>).
        /// </summary>
        public long CurrentPhaseValue = (long)Phase.DISCHARGED;

        public double TimeInPhase = 0.0;
        public bool PassabilityBlocked = false;

        readonly PhaseTimer _phaseTimer = new PhaseTimer();

        public string HazardKind => HAZARD_KIND;

        /// <summary>
        /// Recognized keys: zone_ids, arcing_duration, discharged_duration, arcing_first.
        /// Resets the phase to DISCHARGED (or ARCING when arcing_first) with zero time in phase.
        /// </summary>
        public void Configure(GdDict config)
        {
            ZoneIds.Clear();
            if (config != null && config.Has("zone_ids"))
            {
                object zoneIdsVariant = config["zone_ids"];
                if (zoneIdsVariant is GdArray zoneIdsArr)
                {
                    foreach (object zoneIdVariant in zoneIdsArr)
                    {
                        string zoneId = V.Str(zoneIdVariant);
                        if (zoneId.Length == 0) continue;
                        ZoneIds.Append(zoneId);
                    }
                }
            }
            if (config != null && config.Has("arcing_duration"))
                ArcingDuration = Math.Max(PhaseTimer.MINIMUM_PHASE_DURATION, V.F64(config["arcing_duration"]));
            if (config != null && config.Has("discharged_duration"))
                DischargedDuration = Math.Max(PhaseTimer.MINIMUM_PHASE_DURATION, V.F64(config["discharged_duration"]));
            bool arcingFirst = false;
            if (config != null && config.Has("arcing_first"))
                arcingFirst = V.Bool(config["arcing_first"]);
            // PhaseTimer A = DISCHARGED (safe/passable), B = ARCING (blocks).
            _phaseTimer.Configure(new GdDict { { "A", DischargedDuration }, { "B", ArcingDuration } });
            _phaseTimer.CurrentPhaseValue = arcingFirst ? PhaseTimer.Phase.B : PhaseTimer.Phase.A;
            _phaseTimer.TimeInPhase = 0.0;
            SyncPhaseFromTimer();
            RecomputePassabilityBlocked();
        }

        /// <summary>The context is unused (kept for the uniform ADR-0005 contract). Returns true on a flip or passability change.</summary>
        public bool Tick(double deltaSeconds, GdDict context = null)
        {
            if (deltaSeconds <= 0.0) return false;
            bool before = PassabilityBlocked;
            bool flipped = _phaseTimer.Tick(deltaSeconds);
            SyncPhaseFromTimer();
            RecomputePassabilityBlocked();
            if (flipped) return true;
            return PassabilityBlocked != before;
        }

        public bool IsPassabilityBlocked() => PassabilityBlocked;

        public GdDict GetSummary()
        {
            bool arcing = CurrentPhaseValue == (long)Phase.ARCING;
            double currentDuration = arcing ? ArcingDuration : DischargedDuration;
            return new GdDict
            {
                { "hazard_kind", HAZARD_KIND },
                { "state", arcing ? "ARCING" : "DISCHARGED" },
                { "phase", CurrentPhaseValue },
                { "time_in_state", TimeInPhase },
                { "cycle_duration", ArcingDuration + DischargedDuration },
                { "arcing", arcing },
                { "passability_blocked", PassabilityBlocked },
                { "arcing_duration", ArcingDuration },
                { "discharged_duration", DischargedDuration },
                { "remaining_in_state", Math.Max(0.0, currentDuration - TimeInPhase) },
                { "zone_ids", ZoneIds.ShallowCopy() },
            };
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (CurrentPhaseValue == (long)Phase.ARCING)
                lines.Add("Arc: ARCING — WAIT");
            else
                lines.Add("Arc: DISCHARGED — CROSS");
            return lines;
        }

        /// <summary>Rejects a mismatched hazard_kind or an empty summary. Returns true when any field changed.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (V.Str(summary.Get("hazard_kind", "")) != HAZARD_KIND) return false;
            bool changed = false;
            object newPhaseVariant = summary.Get("phase", CurrentPhaseValue);
            long newPhase = V.I64(newPhaseVariant);
            if (newPhase != CurrentPhaseValue)
            {
                CurrentPhaseValue = newPhase;
                changed = true;
            }
            double newArcing = V.F64(summary.Get("arcing_duration", ArcingDuration));
            if (Math.Abs(newArcing - ArcingDuration) > 0.001)
            {
                ArcingDuration = Math.Max(PhaseTimer.MINIMUM_PHASE_DURATION, newArcing);
                changed = true;
            }
            double newDischarged = V.F64(summary.Get("discharged_duration", DischargedDuration));
            if (Math.Abs(newDischarged - DischargedDuration) > 0.001)
            {
                DischargedDuration = Math.Max(PhaseTimer.MINIMUM_PHASE_DURATION, newDischarged);
                changed = true;
            }
            // Re-sync the helper so subsequent ticks advance against the restored durations.
            _phaseTimer.Configure(new GdDict { { "A", DischargedDuration }, { "B", ArcingDuration } });
            _phaseTimer.CurrentPhaseValue = CurrentPhaseValue == (long)Phase.ARCING ? PhaseTimer.Phase.B : PhaseTimer.Phase.A;
            double newTime = V.F64(summary.Get("time_in_state", TimeInPhase));
            if (Math.Abs(newTime - TimeInPhase) > 0.001)
            {
                TimeInPhase = newTime;
                changed = true;
            }
            _phaseTimer.TimeInPhase = TimeInPhase;
            object newZoneIdsVariant = summary.Get("zone_ids", ZoneIds);
            if (newZoneIdsVariant is GdArray newZoneIds && !V.VariantEquals(newZoneIds, ZoneIds))
            {
                ZoneIds = new GdArray();
                foreach (object zoneId in newZoneIds) ZoneIds.Append(V.Str(zoneId));
                changed = true;
            }
            RecomputePassabilityBlocked();
            return changed;
        }

        void SyncPhaseFromTimer()
        {
            PhaseTimer.Phase timerPhase = _phaseTimer.CurrentPhase();
            CurrentPhaseValue = timerPhase == PhaseTimer.Phase.A ? (long)Phase.DISCHARGED : (long)Phase.ARCING;
            TimeInPhase = _phaseTimer.GetTimeInPhase();
        }

        void RecomputePassabilityBlocked()
        {
            PassabilityBlocked = CurrentPhaseValue == (long)Phase.ARCING;
        }
    }
}
