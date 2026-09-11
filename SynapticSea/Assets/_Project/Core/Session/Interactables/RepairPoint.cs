// Ported from scripts/tools/repair_point.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// A spatial, parts-gated, timed repair node bound to one (system_id, subcomponent_id) of a specific ship's
    /// ShipSystemsManager. Interacting starts a PZ-style channel ticked by <see cref="Process"/>; leaving range
    /// cancels with no part loss; completing consumes the parts and restores the subcomponent.
    /// PKG-B2.5: progress/interrupt rides WorkActionChannel (action repair_subcomponent).
    /// </summary>
    public sealed class RepairPoint : SessionInteractable
    {
        public override string Kind => "repair_point";

        public const string WORK_ACTION_ID = "repair_subcomponent";

        /// <summary>signal repair_completed(system_id, subcomponent_id)</summary>
        public event Action<string, string> RepairCompleted;
        /// <summary>signal repair_blocked(system_id, subcomponent_id, reason)</summary>
        public event Action<string, string, string> RepairBlocked;
        /// <summary>signal repair_started(system_id, subcomponent_id) — fired when a channel successfully begins.</summary>
        public event Action<string, string> RepairStarted;

        public string SystemId = "";
        public string SubcomponentId = "";
        public ShipSystemsManager TargetManager;
        public InventoryState InventoryState;
        public PlayerProgressionState PlayerProgression;
        public double RepairSeconds = 8.0;
        public long MinSkill = 0;

        public bool Channeling = false;
        /// <summary>0..1</summary>
        public double Progress = 0.0;
        public bool Repaired = false;
        public bool MarkerVisible = true;

        double _scaledSeconds = 1.0;
        WorkActionChannel _workChannel = null; // WorkActionChannel while channeling

        /// <summary>RUNTIME: <c>marker.visible = marker_visible and not repaired</c>.</summary>
        public bool MarkerShown => MarkerVisible && !Repaired;

        /// <summary>RUNTIME: <c>collision_shape.disabled = repaired</c>.</summary>
        public bool CollisionDisabled => Repaired;

        public void Configure(string systemId, string subcomponentId, ShipSystemsManager targetManager, InventoryState inventoryState, PlayerProgressionState playerProgression, Vec3 worldPosition, double repairSeconds, long minSkill, double radius = 1.8)
        {
            SystemId = systemId;
            SubcomponentId = subcomponentId;
            TargetManager = targetManager;
            InventoryState = inventoryState;
            PlayerProgression = playerProgression;
            RepairSeconds = repairSeconds;
            MinSkill = minSkill;
            InteractionRadius = radius;
            Channeling = false;
            Progress = 0.0;
            Repaired = false;
            CandidatePlayerInRange = false;
            LocalPosition = worldPosition;
            NodeName = "RepairPoint_" + systemId + "_" + subcomponentId;
            // RUNTIME: set_meta("repair_point", true); sphere collision (radius, disabled = repaired); box marker
            // (radius*0.5 cube, orange 0.95,0.45,0.15,0.7 unshaded, no shadow), visible = MarkerShown.
        }

        public void SetRepaired(bool value)
        {
            Repaired = value;
            Channeling = false;
            Progress = value ? 1.0 : 0.0;
            SetMarkerVisible(MarkerVisible);
            // RUNTIME: collision_shape.disabled = repaired.
            NotifyChanged();
        }

        public void SetMarkerVisible(bool isVisible)
        {
            MarkerVisible = isVisible;
            // RUNTIME: marker.visible = marker_visible and not repaired.
            NotifyChanged();
        }

        long PlayerSkill()
        {
            if (PlayerProgression != null)
                return PlayerProgression.GetSkillLevel("repair");
            return 0;
        }

        /// <summary>
        /// Begins the channel if the player is in range and a dry-run of the gated repair would succeed (carries
        /// parts/tools, meets skill). Returns true if the interaction was consumed.
        /// </summary>
        public bool TryStart(Vec3 playerPosition)
        {
            if (Channeling)
                // Already repairing — consume interact so lower-priority handlers do not fire.
                return true;
            if (Repaired || TargetManager == null)
                return false;
            // Pure range gate (no candidate_player bypass): the player must be at the point to start.
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            // Dry-run precheck WITHOUT consuming.
            ShipSystem system = TargetManager.GetSystem(SystemId);
            ShipSubcomponent sub = system != null ? system.GetSubcomponent(SubcomponentId) : null;
            if (sub == null)
                return false;
            if (sub.IsFunctional())
            {
                RepairBlocked?.Invoke(SystemId, SubcomponentId, "already_functional");
                return true; // consume interact (blocked SFX via signal); channel not started
            }
            long skill = PlayerSkill();
            string reason = PrecheckReason(sub, skill);
            if (reason != "ok")
            {
                RepairBlocked?.Invoke(SystemId, SubcomponentId, reason);
                return true;
            }
            double factor = 1.0 + 0.1 * (double)Math.Max(0L, skill - MinSkill);
            _scaledSeconds = Math.Max(0.01, RepairSeconds / factor);
            var channel = new WorkActionChannel();
            string targetKey = SystemId + "/" + SubcomponentId;
            if (!channel.Begin(WORK_ACTION_ID, targetKey, _scaledSeconds, new GdDict()))
            {
                RepairBlocked?.Invoke(SystemId, SubcomponentId, "work_action");
                return true;
            }
            _workChannel = channel;
            Channeling = true;
            Progress = 0.0;
            NotifyChanged();
            RepairStarted?.Invoke(SystemId, SubcomponentId);
            return true;
        }

        /// <summary>Returns "ok" or a rejection reason, without mutating anything.</summary>
        string PrecheckReason(ShipSubcomponent sub, long skill)
        {
            var parts = new List<string>();
            var tools = new List<string>();
            if (InventoryState != null)
            {
                foreach (object entry in InventoryState.GetItemsByCategory("part"))
                    parts.Add(V.Str(((GdDict)entry)["id"]));
                foreach (object entry in InventoryState.GetItemsByCategory("tool"))
                    tools.Add(V.Str(((GdDict)entry)["id"]));
            }
            foreach (string part in sub.RequiredParts)
                if (!parts.Contains(part))
                    return "missing_parts";
            foreach (string tool in sub.RequiredTools)
                if (!tools.Contains(tool))
                    return "missing_tools";
            if (skill < MinSkill)
                return "insufficient_skill";
            return "ok";
        }

        /// <summary>
        /// Godot <c>_process(delta)</c>: cancels if the channelling player was freed (<paramref name="playerValid"/>
        /// false) or left range (pure strict range check — no candidate_player bypass), else advances the channel.
        /// </summary>
        public void Process(double delta, Vec3 playerPosition, bool playerValid = true)
        {
            // Godot only runs _process on live, in-tree nodes.
            if (!IsValid || !IsInsideTree)
                return;
            if (!Channeling)
                return;
            if (!playerValid)
            {
                Cancel();
                return;
            }
            if (!IsPlayerInDirectRangeStrict(playerPosition))
            {
                Cancel();
                return;
            }
            AdvanceChannel(delta);
        }

        /// <summary>Pumps the channel by delta; completes the repair when progress reaches 1.0.</summary>
        public void AdvanceChannel(double delta)
        {
            if (!Channeling || _workChannel == null)
                return;
            string st = _workChannel.Tick(delta, new GdDict());
            Progress = _workChannel.ProgressRatio();
            NotifyChanged();
            if (st == "completed" || Progress >= 1.0)
                Complete();
        }

        void Complete()
        {
            Channeling = false;
            if (_workChannel != null)
            {
                _workChannel.Cancel();
                _workChannel = null;
            }
            long skill = PlayerSkill();
            GdDict result = TargetManager.RepairWithInventory(SystemId, SubcomponentId, InventoryState, skill);
            if (V.Bool(result.Get("success", false)))
            {
                SetRepaired(true);
                if (PlayerProgression != null)
                    PlayerProgression.GrantXp("repair", 25);
                RepairCompleted?.Invoke(SystemId, SubcomponentId);
            }
            else
            {
                // Lost the parts/tools mid-channel (shouldn't normally happen); reset to idle.
                Progress = 0.0;
                NotifyChanged();
                RepairBlocked?.Invoke(SystemId, SubcomponentId, V.Str(result.Get("reason", "failed")));
            }
        }

        void Cancel()
        {
            Channeling = false;
            Progress = 0.0;
            if (_workChannel != null)
            {
                _workChannel.Cancel();
                _workChannel = null;
            }
            NotifyChanged();
        }

        /// <summary>PKG-B2.5: catalog action driving this channel (empty when idle).</summary>
        public string GetWorkActionId()
        {
            if (_workChannel != null)
                return _workChannel.ActionId;
            return "";
        }
    }
}
