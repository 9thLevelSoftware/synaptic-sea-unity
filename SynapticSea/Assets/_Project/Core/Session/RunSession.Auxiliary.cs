using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        public bool AuxiliaryServicesEnabled => Deps.EnableAuxiliaryServices && ManualStudyEnabled;
        public readonly List<AuxiliaryServicePoint> AuxiliaryServicePoints = new List<AuxiliaryServicePoint>();
        bool _auxConsent;
        Vec3 _auxPosition;
        double _auxHealth;
        Action _auxPublicationGate;
        double? _auxStaminaAfter;
        public GdDict GetAuxiliaryServiceState() => ContinuousAuxiliaryRuntimeActive ? GetContinuousAuxiliaryPresentation() : AuxiliaryServicesEnabled && CurrentPaidFeatureSchema == 5 ? _componentDomain.GetParticipantProjection("auxiliary_services") : new GdDict();
        public bool AuxiliaryWorkRunning => ContinuousAuxiliaryRuntimeActive ? ContinuousAuxiliaryWorkRunning : GetAuxiliaryServiceState().GetDictOrEmpty("job").GetString("status") == "running";
        public bool IsAuxiliaryHardwareReady(string ownerId, string id) => ownerId == HomeShip?.ShipId && GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetBool("hardware_ready");
        public GdDict GetAuxRackRemaining(string id) => GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetDictOrEmpty("remaining").DeepCopy();
        GdDict AuxiliaryDescriptors(IShipLoaderView loader) => AuxiliaryServiceState.Descriptors(loader?.GameplayDoc.GetArrayOrEmpty("auxiliary_services") ?? new GdArray());
        void InitializeAuxiliaryServices()
        {
            if (!AuxiliaryServicesEnabled || CurrentPaidFeatureSchema != 4 || _componentDomain.SchemaVersion == 6) return;
            var candidate = _componentDomain.GetSummary(); candidate["schema_version"] = 5L;
            candidate.GetDictOrEmpty("participating_state")["auxiliary_services"] = AuxiliaryServiceState.New(RunId, PLAYER_LOCAL_ID, AuxiliaryDescriptors(Loader));
            _componentDomain = NewComponentOwner(candidate); BuildAuxiliaryServicePoints();
        }
        void BuildAuxiliaryServicePoints()
        {
            foreach (var point in AuxiliaryServicePoints) Despawn(point); AuxiliaryServicePoints.Clear();
            if (!AuxiliaryServicesEnabled || HomeShip?.SceneRoot == null || Loader == null) return;
            foreach (GdDict spec in new GeneratedShipLayout(Loader.LayoutDoc, Loader.GameplayDoc).BuildAuxiliaryServiceSpecs())
            {
                var point = new AuxiliaryServicePoint { ServiceId = spec.GetString("id"), ServiceKind = spec.GetString("kind"), OwnerId = HomeShip.ShipId,
                    NodeName = "AuxiliaryService_" + spec.GetString("id"), LocalPosition = (Vec3)spec.Get("position"), Parent = HomeShip.SceneRoot };
                point.Interact = id => GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetBool("released") ? RequestTakeAuxRack(id) : RequestAuxiliaryService(id);
                point.Actionable = id => { var row = GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id); return row.GetString("kind") == "utility" ? !row.GetBool("hardware_ready") : !row.GetBool("released") || row.GetDictOrEmpty("remaining").Values.Any(n => V.I64(n) > 0); };
                AuxiliaryServicePoints.Add(Spawn(point));
            }
        }
        string AuxiliaryGate(string id, bool take = false, bool publication = false)
        {
            if (!AuxiliaryServicesEnabled || CurrentPaidFeatureSchema != 5) return "auxiliary_inactive";
            if (!publication && DomainPublicationInProgress || ComponentGenerationRestoreInProgress || ComponentTerminalPending || SliceComplete) return "auxiliary_unavailable";
            if (!PlayableStarted || !HasPlayer || VitalsState?.IsIncapacitated() == true || AwayFromStart) return "actor_unavailable";
            var point = AuxiliaryServicePoints.FirstOrDefault(p => p.ServiceId == id && p.IsValid && p.IsInsideTree && p.OwnerId == HomeShip?.ShipId);
            if (point == null || !point.IsPlayerInDirectRangeStrict(PlayerPos)) return "out_of_range";
            if (Deps.LosProbe?.HasSpace == true && Deps.LosProbe.IntersectRay(PlayerPos + new Vec3(0, .8, 0), point.GlobalPosition, out Vec3 hit) && (hit - point.GlobalPosition).LengthSquared() > .04) return "no_line_of_sight";
            if (PlayerMoving) return "moving";
            if (ManualStudyRunning || WorkActionDriver?.IsWorking() == true || RepairPoints.Any(p => p.Channeling) || BreachSealPoints.Any(p => p.Channeling) || FireSuppressionPoints.Any(p => p.Channeling) || DockBarriers.Any(p => p.Channeling) || _componentDomain.GetParticipantProjection("paid_crafting").GetDictOrEmpty("jobs").Values.OfType<GdDict>().Any(j => j.GetString("input_state") == "paid" && !PaidCraftingState.Terminal(j))) return "work_busy";
            if (take) return "ready";
            if (InventoryState.GetQuantity("crowbar") < 1) return "missing_crowbar";
            if (VitalsState.Stamina <= .001 && GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds") < GetAuxiliaryServiceState().GetDictOrEmpty("descriptors").GetDictOrEmpty(id).GetFloat("required_seconds")) return "exhausted";
            foreach (var part in GetAuxiliaryServiceState().GetDictOrEmpty("descriptors").GetDictOrEmpty(id).GetDictOrEmpty("materials_consumed")) if (InventoryState.GetQuantity(V.Str(part.Key)) < V.I64(part.Value)) return "missing_materials";
            return "ready";
        }
        GdDict ExecuteAuxiliary(string operation, string id, double delta = 0, double elapsed = 0, double speed = 1, double staminaBefore = 0, double staminaAfter = 0, string reason = "")
        {
            GdDict before;
            before = CapturePaidCraftingDomain();
            var command = new GdDict { { "command_id", "aux:" + RunId + ":" + (before.GetInt("command_sequence") + 1) }, { "operation", operation }, { "run_id", RunId }, { "actor_id", PLAYER_LOCAL_ID }, { "service_id", id }, { "delta_seconds", delta }, { "elapsed_seconds", elapsed }, { "speed", speed }, { "stamina_before", staminaBefore }, { "stamina_after", staminaAfter }, { "reason", reason } };
            Vec3 position = PlayerPos; double health = VitalsState.Health, stamina = VitalsState.Stamina;
            _paidPublicationContext = PaidContextFingerprint(); _componentMutating = true;
            _auxStaminaAfter = operation == "aux_progress" ? staminaAfter : (double?)null;
            _auxPublicationGate = () => { if (PlayerPos != position || VitalsState.Health != health || VitalsState.Stamina != stamina || operation != "aux_pause" && AuxiliaryGate(id, operation == "aux_take", true) != "ready") throw new InvalidOperationException("stale_auxiliary_context"); };
            try
            {
                GdDict prepared;
                prepared = _componentDomain.PrepareAuxiliary(command, candidate => { var effect = AuxiliaryServiceState.Apply(candidate, operation, id, delta, elapsed, speed, staminaBefore, staminaAfter, reason); candidate["command_sequence"] = candidate.GetInt("command_sequence") + 1; SetPaidProjections(candidate); return effect; });
                if (!prepared.GetBool("ok") || prepared.GetBool("committed")) return prepared;
                GdDict result;
                result = _componentDomain.Commit(prepared.GetString("transaction_id"));
                if (result.GetBool("committed") && operation == "aux_complete") RecomputeExpandedShipSystems(0);
                RefreshAuxiliaryHud(); return result;
            }
            finally { _componentMutating = false; _paidPublicationContext = null; _auxPublicationGate = null; _auxStaminaAfter = null; }
        }
        public GdDict RequestAuxiliaryService(string id)
        {
            if(ContinuousAuxiliaryRuntimeActive)return RequestContinuousAuxiliaryIntent("start",id);
            if (!AuxiliaryServicesEnabled || !EnsurePaidOwner()) return PaidFailure("auxiliary_inactive");
            string reason = AuxiliaryGate(id); if (reason != "ready") return PaidFailure(reason);
            if (AuxiliaryWorkRunning) return PaidFailure("auxiliary_busy");
            var result = ExecuteAuxiliary("aux_start", id);
            if (result.GetBool("committed")) { _auxConsent = true; _auxPosition = PlayerPos; _auxHealth = VitalsState.Health; }
            return result;
        }
        public GdDict PauseAuxiliaryService(string reason = "paused")
        {
            if(ContinuousAuxiliaryRuntimeActive){PauseContinuousAuxiliaryIntent(reason);return new GdDict{{"ok",true},{"committed",false},{"presentation_only",true}};}
            _auxConsent = false; var job = GetAuxiliaryServiceState().GetDictOrEmpty("job");
            if (job.GetString("status") != "running" || ComponentGenerationRestoreInProgress) return PaidFailure("auxiliary_not_running");
            return ExecuteAuxiliary("aux_pause", job.GetString("service_id"), reason: reason);
        }
        public GdDict RequestTakeAuxRack(string id)
        {
            if(ContinuousAuxiliaryRuntimeActive)return RequestContinuousAuxiliaryIntent("take",id);
            string reason = AuxiliaryGate(id, true); if (reason != "ready") return PaidFailure(reason);
            if (AuxiliaryWorkRunning) return PaidFailure("auxiliary_busy");
            return ExecuteAuxiliary("aux_take", id);
        }
        bool TickAuxiliaryWork(double delta)
        {
            if (!AuxiliaryWorkRunning) return false;
            if (delta <= 0 || double.IsNaN(delta) || double.IsInfinity(delta)) return true;
            var state = GetAuxiliaryServiceState(); var job = state.GetDictOrEmpty("job"); string id = job.GetString("service_id"), gate = AuxiliaryGate(id);
            if (!_auxConsent) gate = "explicit_resume_required";
            if (PlayerMoving || (PlayerPos - _auxPosition).LengthSquared() > .0001) gate = "moving";
            if (VitalsState.Health < _auxHealth) gate = "damage";
            if (gate != "ready") { PauseAuxiliaryService(gate); return true; }
            _auxHealth = VitalsState.Health;
            if (HoldToWorkEnabled && !IsWorkInteractHeld) return true;
            if (job.GetFloat("progress_seconds") == state.GetDictOrEmpty("descriptors").GetDictOrEmpty(id).GetFloat("required_seconds"))
            { var completed = ExecuteAuxiliary("aux_complete", id); if (completed.GetBool("committed")) _auxConsent = false; return true; }
            double ratio = Math.Max(0, Math.Min(1, VitalsState.Stamina / Math.Max(1, VitalsState.MaxStamina)));
            double speed = (WoundState?.WorkSpeedMultiplier() ?? 1) * (.35 + .65 * ratio);
            double remaining = state.GetDictOrEmpty("descriptors").GetDictOrEmpty(id).GetFloat("required_seconds") - job.GetFloat("progress_seconds");
            double elapsed = Math.Min(delta, Math.Min(remaining / speed, VitalsState.Stamina / 8));
            if (elapsed <= 0) { PauseAuxiliaryService("exhausted"); return true; }
            double stamina = VitalsState.Stamina;
            var result = ExecuteAuxiliary("aux_progress", id, Math.Min(remaining, elapsed * speed), elapsed, speed, stamina, Math.Max(0, stamina - 8 * elapsed));
            if (!result.GetBool("committed")) { PauseAuxiliaryService(result.GetString("reason")); return true; }
            if (GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds") == state.GetDictOrEmpty("descriptors").GetDictOrEmpty(id).GetFloat("required_seconds"))
            { result = ExecuteAuxiliary("aux_complete", id); if (result.GetBool("committed")) _auxConsent = false; }
            return true;
        }
        void RefreshAuxiliaryHud()
        {
            if(ContinuousAuxiliaryRuntimeActive){RefreshContinuousAuxiliaryHud();return;}
            var state = GetAuxiliaryServiceState(); var job = state.GetDictOrEmpty("job"); if (job.IsEmpty) return;
            Events.RaiseWorkActionHudState(new GdDict { { "action_id", "auxiliary_service" }, { "target_id", job.GetString("service_id") }, { "verb", "Service" }, { "progress", job.GetFloat("progress_seconds") / state.GetDictOrEmpty("descriptors").GetDictOrEmpty(job.GetString("service_id")).GetFloat("required_seconds") }, { "status", job.GetString("status") == "running" ? "active" : job.GetString("status") }, { "block_reason", job.GetString("reason") }, { "noise", 0.0 } });
        }
        internal bool TryAuxiliaryServices(Vec3 position)
        {
            foreach (var point in NearestInteractables(AuxiliaryServicePoints, position))
            {
                if(ContinuousAuxiliaryRuntimeActive)
                {
                    if(!point.IsValid||!point.IsInsideTree||!point.IsPlayerInDirectRangeStrict(position)||point.Actionable?.Invoke(point.ServiceId)==false)continue;
                    var queued=point.Interact?.Invoke(point.ServiceId);
                    if(queued?.GetBool("ok")==true&&queued.GetBool("queued"))return true;
                }
                else if(point.TryInteract(position))return true;
            }
            return false;
        }
    }
}
