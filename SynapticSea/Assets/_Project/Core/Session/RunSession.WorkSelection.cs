using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary>Live reachable work identities, including blocked repairs and the workbench recipe picker. No focus hiding or automatic selection.</summary>
        public IReadOnlyList<SessionInteractable> ListNearbyWorkTargets()
        {
            return RepairPoints.Cast<SessionInteractable>().Concat(BreachSealPoints).Concat(CraftingStations.Where(st => st.StationKind == "workbench"))
                .Where(p => WorkTargetIdentityReason(p) == "ok" && HasPlayer
                    && HasInteractionSightAndReach(p)
                    && !(p is RepairPoint r && r.Repaired) && !(p is BreachSealPoint s && s.Sealed))
                .OrderBy(p => p.GlobalPosition.DistanceSquaredTo(PlayerPos))
                .ThenBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.NodeName, StringComparer.Ordinal).ToList();
        }

        /// <summary>Pure advisory description; admission is always repeated at confirmation.</summary>
        public GdDict DescribeWorkTarget(SessionInteractable target)
        {
            string id = WorkTargetId(target), reason = WorkTargetGate(target);
            var requirements = new GdDict { { "parts", new GdArray() }, { "tools", new GdArray() },
                { "skill_id", "repair" }, { "min_skill", 0L }, { "required_item", "" } };
            string label = "Unavailable work", action = "";
            if (target is RepairPoint repair)
            {
                label = "Repair " + repair.SystemId + ": " + repair.SubcomponentId;
                action = RepairPoint.WORK_ACTION_ID;
                requirements["min_skill"] = repair.MinSkill;
                var sub = repair.TargetManager?.GetSystem(repair.SystemId)?.GetSubcomponent(repair.SubcomponentId);
                if (sub != null)
                {
                    requirements["parts"] = new GdArray(sub.RequiredParts.Select(p => (object)p));
                    requirements["tools"] = new GdArray(sub.RequiredTools.Select(p => (object)p));
                }
            }
            else if (target is BreachSealPoint seal)
            {
                label = "Seal breach: " + seal.CompartmentId;
                action = BreachSealPoint.WORK_ACTION_ID;
                requirements["required_item"] = seal.RequiredItem;
                if (!string.IsNullOrEmpty(seal.RequiredItem)) requirements["parts"] = GdArray.Of(seal.RequiredItem);
            }
            else if (target is CraftingStation station && station.StationKind == "workbench")
            {
                label = "Use workbench";
                action = "open_recipe_picker";
                requirements["skill_id"] = "fabrication";
                requirements["effective_tier"] = station.EffectiveTier;
                requirements["station_kind"] = station.StationKind;
            }
            return new GdDict { { "id", id }, { "target", id }, { "kind", target?.Kind ?? "" },
                { "handler", target?.Kind ?? "" }, { "action", action }, { "label", label },
                { "owner_id", (target is CraftingStation ? HomeShip : AwayFromStart ? CurrentShip : LifeboatShip)?.ShipId ?? "" },
                { "station_kind", target is CraftingStation describedStation ? describedStation.StationKind : "" },
                { "status", reason == "ok" ? "ready" : "blocked" }, { "reason", reason == "ok" ? "ready" : reason },
                { "requirements", requirements } };
        }

        /// <summary>Dispatch exactly the selected live work object. A denial never falls through to another target.</summary>
        public GdDict RequestWorkTarget(SessionInteractable target)
        {
            string reason = WorkTargetGate(target), id = WorkTargetId(target);
            bool handled = false, started = false, opened = false;
            // These model-level refusals intentionally enter the normal TryStart feedback path.
            bool modelDenial = reason == "missing_parts" || reason == "missing_tools" || reason == "insufficient_skill"
                || reason == "already_functional" || reason == "not_breached" || reason == "missing_sealant"
                || reason == "busy" && target is CraftingStation;
            if (reason == "ok" || modelDenial)
            {
                if (target is RepairPoint repair)
                {
                    string denied = "";
                    Action<string, string, string> blocked = (sys, sub, why) => denied = why;
                    repair.RepairBlocked += blocked;
                    try { handled = repair.TryStart(PlayerPos); started = repair.Channeling; }
                    finally { repair.RepairBlocked -= blocked; }
                    if (denied.Length > 0) reason = denied;
                }
                else if (target is BreachSealPoint seal)
                {
                    string denied = "";
                    Action<string, string> blocked = (compartment, why) => denied = why;
                    seal.SealBlocked += blocked;
                    try { handled = seal.TryStart(PlayerPos); started = seal.Channeling; }
                    finally { seal.SealBlocked -= blocked; }
                    if (denied.Length > 0) reason = denied;
                }
                else if (target is CraftingStation station)
                {
                    string denied = "";
                    Action<string, string> blocked = (kind, why) => denied = why;
                    Action<string, GdDict> requested = (panel, args) => {
                        if (panel == "recipe_picker" && args.GetString("station_kind") == station.StationKind) opened = true;
                    };
                    station.CraftBlocked += blocked;
                    Events.PanelRequested += requested;
                    try { handled = station.TryInteract(PlayerPos); }
                    finally { station.CraftBlocked -= blocked; Events.PanelRequested -= requested; }
                    if (denied.Length > 0) reason = denied;
                }
                if (handled) LastInteractHandlerId = target.Kind;
                if (!started && !opened && reason == "ok") reason = "work_not_started";
            }
            return new GdDict { { "ok", started || opened }, { "started", started }, { "opened", opened }, { "handled", handled },
                { "station_kind", target is CraftingStation resultStation ? resultStation.StationKind : "" },
                { "reason", started ? "started" : opened ? "opened" : reason }, { "handler", target?.Kind ?? "" }, { "target", id }, { "id", id } };
        }

        static string WorkTargetId(SessionInteractable target) => target is RepairPoint repair
            ? repair.SystemId + "." + repair.SubcomponentId : target is BreachSealPoint seal ? seal.CompartmentId
                : target is CraftingStation station && station.StationKind == "workbench" ? station.StationKind : "";

        string WorkTargetIdentityReason(SessionInteractable target)
        {
            if (target == null || !(target is RepairPoint) && !(target is BreachSealPoint)
                && !(target is CraftingStation supportedStation && supportedStation.StationKind == "workbench")) return "unsupported_target";
            if (!target.IsValid || !target.IsInsideTree) return "stale_target";
            IShipSceneRoot expected = target is CraftingStation ? HomeShip?.SceneRoot : AwayFromStart && CurrentShip != null && RootValid(CurrentShip.SceneRoot)
                ? CurrentShip.SceneRoot : LifeboatShip != null && RootValid(LifeboatShip.SceneRoot) ? LifeboatShip.SceneRoot : null;
            if (!ReferenceEquals(target.Parent, expected)) return "stale_owner";
            if (target is RepairPoint repair)
            {
                if (!RepairPoints.Contains(repair)) return "stale_target";
                if (!ReferenceEquals(repair.TargetManager, ActiveSystemsManager()) || repair.TargetManager == null
                    || !ReferenceEquals(repair.InventoryState, InventoryState) || InventoryState == null
                    || !ReferenceEquals(repair.PlayerProgression, PlayerProgression) || PlayerProgression == null) return "invalid_binding";
            }
            if (target is BreachSealPoint seal)
            {
                if (!BreachSealPoints.Contains(seal)) return "stale_target";
                if (!ReferenceEquals(seal.HullState, ActiveHull()) || seal.HullState == null
                    || !ReferenceEquals(seal.InventoryState, InventoryState) || InventoryState == null
                    || !ReferenceEquals(seal.PlayerProgression, PlayerProgression) || PlayerProgression == null) return "invalid_binding";
            }
            if (target is CraftingStation station)
            {
                if (!CraftingStations.Contains(station)) return "stale_target";
                if (AwayFromStart || !RootValid(HomeShip?.SceneRoot)) return "stale_owner";
                if (!ReferenceEquals(station.CraftingState, CraftingState) || CraftingState == null
                    || !ReferenceEquals(station.MaterialState, MaterialState) || MaterialState == null
                    || !ReferenceEquals(station.InventoryState, InventoryState) || InventoryState == null
                    || !ReferenceEquals(station.DeconstructionResolver, DeconstructionResolver) || DeconstructionResolver == null
                    || !ReferenceEquals(station.PlayerProgression, PlayerProgression) || PlayerProgression == null) return "invalid_binding";
            }
            return "ok";
        }

        string WorkTargetGate(SessionInteractable target)
        {
            if (ComponentGenerationRestoreInProgress) return "restore_in_progress";
            if (DomainPublicationInProgress) return "reentrant_mutation";
            if (ComponentTerminalPending || SliceComplete) return "terminal_pending";
            if (!PlayableStarted || !HasPlayer || VitalsState?.IsIncapacitated() == true) return "actor_unavailable";
            string identity = WorkTargetIdentityReason(target);
            if (identity != "ok") return identity;
            if (!target.IsPlayerInDirectRangeStrict(PlayerPos)) return "out_of_range";
            if (!HasInteractionSightAndReach(target)) return "no_line_of_sight";
            if (WorkSelectionBusy()) return "work_busy";
            if (target is CraftingStation station)
            {
                // Opening is not crafting: leave power, recipe skill, tier and payment to the existing recipe picker.
                if (UiRecipePickerOpen || UiScannerOpen || UiInventoryOpen) return "ui_busy";
                if (!UiMenusClosed) return "menu_open";
                return station.CraftingState.IsCrafting() && !station.CraftingState.HasPaidOwner ? "busy" : "ok";
            }
            return target is RepairPoint repair ? repair.DescribeReason() : ((BreachSealPoint)target).DescribeReason();
        }

        bool WorkSelectionBusy()
        {
            if (_workAwaitingResume || WorkActionDriver?.IsWorking() == true
                || RepairPoints.Any(p => p.Channeling) || BreachSealPoints.Any(p => p.Channeling)
                || FireSuppressionPoints.Any(p => p.Channeling) || DockBarriers.Any(p => p.Channeling)) return true;
            GdDict domain = _componentDomain?.GetSummary() ?? new GdDict();
            string component = domain.GetDictOrEmpty("component_work").GetString("status");
            if (component == "active" || component.StartsWith("paused", StringComparison.Ordinal)) return true;
            string study = GetManualStudyState().GetDictOrEmpty("job").GetString("status");
            if (study == "running" || study == "paused") return true;
            return domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting").GetDictOrEmpty("jobs")
                .Values.OfType<GdDict>().Any(j => j.GetString("input_state") == "paid" && !PaidCraftingState.Terminal(j));
        }
    }
}
