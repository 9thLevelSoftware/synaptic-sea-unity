using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary>Live reachable work identities, including blocked repairs. No focus hiding or automatic selection.</summary>
        public IReadOnlyList<SessionInteractable> ListNearbyWorkTargets()
        {
            return RepairPoints.Cast<SessionInteractable>().Concat(BreachSealPoints)
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
            return new GdDict { { "id", id }, { "target", id }, { "kind", target?.Kind ?? "" },
                { "handler", target?.Kind ?? "" }, { "action", action }, { "label", label },
                { "owner_id", (AwayFromStart ? CurrentShip : LifeboatShip)?.ShipId ?? "" },
                { "status", reason == "ok" ? "ready" : "blocked" }, { "reason", reason == "ok" ? "ready" : reason },
                { "requirements", requirements } };
        }

        /// <summary>Dispatch exactly the selected live work object. A denial never falls through to another target.</summary>
        public GdDict RequestWorkTarget(SessionInteractable target)
        {
            string reason = WorkTargetGate(target), id = WorkTargetId(target);
            bool handled = false, started = false;
            // These model-level refusals intentionally enter the normal TryStart feedback path.
            bool modelDenial = reason == "missing_parts" || reason == "missing_tools" || reason == "insufficient_skill"
                || reason == "already_functional" || reason == "not_breached" || reason == "missing_sealant";
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
                if (handled) LastInteractHandlerId = target.Kind;
                if (!started && reason == "ok") reason = "work_not_started";
            }
            return new GdDict { { "ok", started }, { "started", started }, { "handled", handled },
                { "reason", started ? "started" : reason }, { "handler", target?.Kind ?? "" }, { "target", id }, { "id", id } };
        }

        static string WorkTargetId(SessionInteractable target) => target is RepairPoint repair
            ? repair.SystemId + "." + repair.SubcomponentId : target is BreachSealPoint seal ? seal.CompartmentId : "";

        string WorkTargetIdentityReason(SessionInteractable target)
        {
            if (target == null || !(target is RepairPoint) && !(target is BreachSealPoint)) return "unsupported_target";
            if (!target.IsValid || !target.IsInsideTree) return "stale_target";
            IShipSceneRoot expected = AwayFromStart && CurrentShip != null && RootValid(CurrentShip.SceneRoot)
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
            string auxiliary = GetAuxiliaryServiceState().GetDictOrEmpty("job").GetString("status");
            if (study == "running" || study == "paused" || auxiliary == "running" || auxiliary == "paused") return true;
            return domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting").GetDictOrEmpty("jobs")
                .Values.OfType<GdDict>().Any(j => j.GetString("input_state") == "paid" && !PaidCraftingState.Terminal(j));
        }
    }
}
