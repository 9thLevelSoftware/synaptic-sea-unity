// The two _process branches of scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 (lines 8525-8576) as one stage set
// with two explicit order tables. docs/TickOrder.md mirrors this file.
using System;
using System.Collections.Generic;

namespace SynapticSea.Core.Session
{
    /// <summary>Which <c>_process</c> branch ran: home complex (<c>away_from_start == false</c>) or a boarded derelict.</summary>
    public enum SessionLocation
    {
        Home,
        Away,
    }

    /// <summary>Where a stage runs. Location-specific stages must say why (see <see cref="ITickStage.ScopeReason"/>).</summary>
    public enum StageScope
    {
        Both,
        HomeOnly,
        AwayOnly,
    }

    /// <summary>One order-sensitive step of the per-frame tick.</summary>
    public interface ITickStage
    {
        /// <summary>Stable id used by the order tables and the tick-order test.</summary>
        string Id { get; }

        StageScope Scope { get; }

        /// <summary>Required when <see cref="Scope"/> is not <see cref="StageScope.Both"/>: why the other branch has no call.</summary>
        string ScopeReason { get; }

        /// <summary>The GDScript helper(s) the stage runs.</summary>
        string GodotSource { get; }

        void Run(RunSession session, SessionLocation location, double delta);
    }

    /// <summary>A stage backed by a delegate into the session.</summary>
    public sealed class TickStage : ITickStage
    {
        readonly Action<RunSession, SessionLocation, double> _run;

        public TickStage(string id, string godotSource, Action<RunSession, SessionLocation, double> run, StageScope scope = StageScope.Both, string scopeReason = "")
        {
            Id = id;
            GodotSource = godotSource;
            _run = run;
            Scope = scope;
            ScopeReason = scopeReason ?? "";
        }

        public string Id { get; }
        public StageScope Scope { get; }
        public string ScopeReason { get; }
        public string GodotSource { get; }
        public void Run(RunSession session, SessionLocation location, double delta) => _run(session, location, delta);
    }

    /// <summary>
    /// The single stage set and the two order tables that reproduce the Godot branches exactly. The away branch ticks
    /// oxygen first; the home branch ticks autosave/threat/ships first. Both orders are asserted by
    /// <c>TickOrderTests</c>: every stage appears in both tables unless it declares a location scope with a reason.
    /// </summary>
    public static class TickOrder
    {
        public const string Autosave = "autosave";
        public const string Oxygen = "oxygen";
        public const string Threat = "threat";
        public const string SanityHallucination = "sanity_hallucination";
        public const string ActiveFire = "active_fire";
        public const string FieldCraft = "field_craft";
        public const string SurvivalAttrition = "survival_attrition";
        public const string PlayerVitals = "player_vitals";
        public const string TrackerStatus = "tracker_status";
        public const string Audio = "audio";
        public const string PresentShips = "present_ships";
        public const string RechargePortPower = "recharge_port_power";
        public const string Food = "food";
        public const string AmmoConsumableDecay = "ammo_consumable_decay";
        public const string ElectricalArc = "electrical_arc";
        public const string WorkAction = "work_action";
        public const string WorkActionHud = "work_action_hud";
        public const string TooltipFocus = "tooltip_focus";

        public static readonly IReadOnlyList<ITickStage> Stages = new ITickStage[]
        {
            new TickStage(Autosave, "_tick_autosave_policy (home) / second half of _tick_field_craft_and_autosave (away)",
                (s, loc, d) => s.StageAutosave(d)),
            new TickStage(Oxygen, "_refresh_oxygen_state(false, delta)",
                (s, loc, d) => s.StageOxygen(d)),
            new TickStage(Threat, "_tick_threat_runtime",
                (s, loc, d) => s.StageThreat(d)),
            new TickStage(SanityHallucination, "_tick_sanity_and_hallucinations(delta, in_safe)",
                (s, loc, d) => s.StageSanityHallucination(d, loc)),
            new TickStage(ActiveFire, "_tick_active_fire",
                (s, loc, d) => s.StageActiveFire(d)),
            new TickStage(FieldCraft, "field_crafting_state.tick -> _on_field_craft_completed (home inline / away via _tick_field_craft_and_autosave)",
                (s, loc, d) => s.StageFieldCraft(d)),
            new TickStage(SurvivalAttrition, "_tick_survival_attrition",
                (s, loc, d) => s.StageSurvivalAttrition(d)),
            new TickStage(PlayerVitals, "_refresh_player_vitals",
                (s, loc, d) => s.StagePlayerVitals(d), StageScope.AwayOnly,
                "Home refreshes the vitals panel inside the oxygen stage (_refresh_oxygen_state calls _refresh_player_vitals); the Godot home branch has no separate call, so a second home call would double-tick the PlayerVitalsModel."),
            new TickStage(TrackerStatus, "_refresh_tracker_system_status_lines",
                (s, loc, d) => s.StageTrackerStatus(), StageScope.AwayOnly,
                "Home refreshes the tracker lines inside the oxygen stage (_refresh_oxygen_state calls _refresh_tracker_system_status_lines); the Godot home branch has no separate call."),
            new TickStage(Audio, "_tick_audio_runtime (audio_manager.tick + _refresh_audio_state + _tick_footstep_sfx)",
                (s, loc, d) => s.StageAudio(d)),
            new TickStage(PresentShips, "_tick_present_ships (per-ship ShipRuntime FRAME band + hub SLOW-band _recompute_expanded_ship_systems)",
                (s, loc, d) => s.StagePresentShips(d)),
            new TickStage(RechargePortPower, "extinguisher_recharge_port.set_powered(active manager power operational)",
                (s, loc, d) => s.StageRechargePortPower(), StageScope.AwayOnly,
                "Away-only derelict power gate: it must win over the hub 'stations' allocation _recompute_expanded_ship_systems pushes; at home that SLOW-band recompute (present_ships) is the port's only power source."),
            new TickStage(Food, "_tick_food_runtime",
                (s, loc, d) => s.StageFood(d)),
            new TickStage(AmmoConsumableDecay, "_tick_ammo_and_consumable_decay",
                (s, loc, d) => s.StageAmmoConsumableDecay(d)),
            new TickStage(ElectricalArc, "_tick_electrical_arc",
                (s, loc, d) => s.StageElectricalArc(d)),
            new TickStage(WorkAction, "_tick_work_action",
                (s, loc, d) => s.StageWorkAction(d)),
            new TickStage(WorkActionHud, "_refresh_work_action_hud (scene refresh -> SessionEvents.WorkActionHudState)",
                (s, loc, d) => s.RefreshWorkActionHud()),
            new TickStage(TooltipFocus, "_refresh_tooltip_focus (scene refresh -> SessionEvents.TooltipQuery)",
                (s, loc, d) => s.RefreshTooltipFocus()),
        };

        /// <summary>The <c>if away_from_start:</c> branch (8531-8554).</summary>
        public static readonly IReadOnlyList<string> AwayOrder = new[]
        {
            Oxygen, Threat, SanityHallucination, ActiveFire, SurvivalAttrition, PlayerVitals, TrackerStatus,
            FieldCraft, Autosave, Audio, PresentShips, RechargePortPower, Food, AmmoConsumableDecay, ElectricalArc,
            WorkAction, WorkActionHud, TooltipFocus,
        };

        /// <summary>The home branch (8555-8575).</summary>
        public static readonly IReadOnlyList<string> HomeOrder = new[]
        {
            Autosave, Threat, PresentShips, ActiveFire, FieldCraft, Oxygen, ElectricalArc, AmmoConsumableDecay,
            SurvivalAttrition, SanityHallucination, Food, Audio, WorkAction, WorkActionHud, TooltipFocus,
        };

        public static IReadOnlyList<string> OrderFor(SessionLocation location) => location == SessionLocation.Away ? AwayOrder : HomeOrder;

        static Dictionary<string, ITickStage> _byId;

        public static ITickStage Get(string id)
        {
            if (_byId == null)
            {
                var map = new Dictionary<string, ITickStage>(StringComparer.Ordinal);
                foreach (ITickStage stage in Stages)
                    map[stage.Id] = stage;
                _byId = map;
            }
            return _byId.TryGetValue(id, out ITickStage s) ? s : null;
        }
    }
}
