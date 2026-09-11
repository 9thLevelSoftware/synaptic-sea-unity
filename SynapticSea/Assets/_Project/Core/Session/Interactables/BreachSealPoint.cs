// Ported from scripts/tools/breach_seal_point.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// A spatial, item-gated, timed seal node bound to one hull compartment of a HullIntegrityState. Modeled on
    /// RepairPoint: interacting starts a channel ticked by <see cref="Process"/>; leaving range cancels with no item
    /// loss; completing consumes the sealant and seals the compartment.
    /// PKG-B2.5: progress/interrupt rides WorkActionChannel (action patch_breach).
    /// </summary>
    public sealed class BreachSealPoint : SessionInteractable
    {
        public override string Kind => "breach_seal_point";

        public const string WORK_ACTION_ID = "patch_breach";

        /// <summary>signal breach_sealed(compartment_id)</summary>
        public event Action<string> BreachSealed;
        /// <summary>signal seal_blocked(compartment_id, reason)</summary>
        public event Action<string, string> SealBlocked;

        public string CompartmentId = "";
        public HullIntegrityState HullState;
        public InventoryState InventoryState;
        public PlayerProgressionState PlayerProgression;
        public double SealSeconds = 4.0;
        public string RequiredItem = "hull_sealant";
        public double SealAmount = 1.0;

        public bool Channeling = false;
        public double Progress = 0.0;
        public bool Sealed = false;
        public bool MarkerVisible = true;

        WorkActionChannel _workChannel = null; // WorkActionChannel while channeling

        /// <summary>RUNTIME: <c>marker.visible = marker_visible and not sealed</c>.</summary>
        public bool MarkerShown => MarkerVisible && !Sealed;

        /// <summary>RUNTIME: <c>collision_shape.disabled = sealed</c>.</summary>
        public bool CollisionDisabled => Sealed;

        public void Configure(string compartmentId, HullIntegrityState hullState, InventoryState inventoryState, PlayerProgressionState playerProgression, Vec3 worldPosition, double sealSeconds, string requiredItem, double sealAmount, double radius = 1.8)
        {
            CompartmentId = compartmentId;
            HullState = hullState;
            InventoryState = inventoryState;
            PlayerProgression = playerProgression;
            SealSeconds = Math.Max(0.01, sealSeconds);
            RequiredItem = requiredItem;
            SealAmount = Math.Max(0.0, sealAmount);
            InteractionRadius = radius;
            Channeling = false;
            Progress = 0.0;
            Sealed = false;
            CandidatePlayerInRange = false;
            LocalPosition = worldPosition;
            NodeName = "BreachSealPoint_" + compartmentId;
            // RUNTIME: set_meta("breach_seal_point", true); sphere collision (radius, disabled = sealed);
            // GameplayPropFactory.build("breach_patch_panel") visual, marker visible = MarkerShown.
        }

        public void SetSealed(bool value)
        {
            Sealed = value;
            Channeling = false;
            Progress = value ? 1.0 : 0.0;
            // RUNTIME: collision_shape.disabled = sealed; marker.visible = marker_visible and not sealed.
            NotifyChanged();
        }

        /// <summary>Begins the channel if the player is in range and a dry-run would succeed.</summary>
        public bool TryStart(Vec3 playerPosition)
        {
            if (Channeling)
                // Already sealing — consume interact so lower-priority handlers do not fire.
                return true;
            if (Sealed || HullState == null)
                return false;
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            if (!HullState.Compartments.Has(CompartmentId))
                return false;
            if (!V.Bool(HullState.Compartments.GetDictOrEmpty(CompartmentId).Get("breach_open", false)))
            {
                SealBlocked?.Invoke(CompartmentId, "not_breached");
                return true; // consume; seal not started
            }
            if (!HasRequiredItem())
            {
                SealBlocked?.Invoke(CompartmentId, "missing_sealant");
                return true;
            }
            long sealantQty = 0;
            if (InventoryState != null)
                sealantQty = InventoryState.GetQuantity(RequiredItem);
            var inventory = new GdDict();
            inventory[RequiredItem] = sealantQty;
            var ctx = new GdDict
            {
                { "tool_class", "sealant" },
                { "skill_id", "repair" },
                { "skill_level", 0L },
                { "inventory", inventory },
            };
            var channel = new WorkActionChannel();
            if (!channel.Begin(WORK_ACTION_ID, CompartmentId, SealSeconds, ctx))
            {
                SealBlocked?.Invoke(CompartmentId, "work_action");
                return true;
            }
            _workChannel = channel;
            Channeling = true;
            Progress = 0.0;
            NotifyChanged();
            return true;
        }

        bool HasRequiredItem()
        {
            if (InventoryState == null)
                return false;
            if (string.IsNullOrEmpty(RequiredItem))
                return true;
            return InventoryState.GetQuantity(RequiredItem) > 0;
        }

        /// <summary>Godot <c>_process(delta)</c>: cancel when the player was freed or left strict range, else advance.</summary>
        public void Process(double delta, Vec3 playerPosition, bool playerValid = true)
        {
            // Godot only runs _process on live, in-tree nodes.
            if (!IsValid || !IsInsideTree)
                return;
            if (!Channeling)
                return;
            if (!playerValid || !IsPlayerInDirectRangeStrict(playerPosition))
            {
                Cancel();
                return;
            }
            AdvanceChannel(delta);
        }

        /// <summary>Pumps the channel by delta; seals when progress reaches 1.0. Exposed for smokes.</summary>
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
            if (!HasRequiredItem())
            {
                Progress = 0.0;
                NotifyChanged();
                SealBlocked?.Invoke(CompartmentId, "missing_sealant");
                return;
            }
            if (!string.IsNullOrEmpty(RequiredItem))
                InventoryState.RemoveItem(RequiredItem, 1);
            if (HullState.SealCompartment(CompartmentId, SealAmount))
            {
                SetSealed(true);
                if (PlayerProgression != null)
                    PlayerProgression.GrantXp("repair", 15);
                BreachSealed?.Invoke(CompartmentId);
            }
            else
            {
                Progress = 0.0;
                NotifyChanged();
                SealBlocked?.Invoke(CompartmentId, "seal_failed");
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
