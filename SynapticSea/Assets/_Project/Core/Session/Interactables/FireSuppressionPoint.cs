// Ported from scripts/tools/fire_suppression_point.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// Spatial, tool-gated, timed extinguish node bound to one burning compartment of a FireSuppressionState.
    /// Modeled on BreachSealPoint: interacting starts a channel ticked by <see cref="Process"/>; leaving range
    /// cancels with no cost; completing consumes one extinguisher use and extinguishes the compartment.
    /// Without a charged extinguisher, interacting deliberately vents the compartment instead (Fire B2).
    /// PKG-B2.5: progress/interrupt rides WorkActionChannel (action suppress_fire).
    /// </summary>
    public sealed class FireSuppressionPoint : SessionInteractable
    {
        public override string Kind => "fire_suppression_point";

        public const string WORK_ACTION_ID = "suppress_fire";

        /// <summary>signal fire_extinguished(compartment_id)</summary>
        public event Action<string> FireExtinguished;
        /// <summary>signal extinguish_blocked(compartment_id, reason)</summary>
        public event Action<string, string> ExtinguishBlocked;
        /// <summary>signal compartment_vented(compartment_id) — Fire B2 deliberate vacuum vent.</summary>
        public event Action<string> CompartmentVented;

        public string CompartmentId = "";
        public FireSuppressionState FireState;
        public ExtinguisherState ExtinguisherState;
        public InventoryState InventoryState;
        public PlayerProgressionState PlayerProgression;
        public double ExtinguishSeconds = 4.0;
        public string RequiredTool = "fire_extinguisher";

        public bool Channeling = false;
        public double Progress = 0.0;
        public bool Extinguished = false;
        public bool MarkerVisible = true;

        WorkActionChannel _workChannel = null; // WorkActionChannel while channeling

        /// <summary>RUNTIME: <c>marker.visible</c> (marker_visible and not extinguished; hidden once extinguished/vented).</summary>
        public bool MarkerShown => MarkerVisible && !Extinguished;

        /// <summary>RUNTIME: <c>collision_shape.disabled</c> (true once extinguished/vented).</summary>
        public bool CollisionDisabled => Extinguished;

        public void Configure(string compartmentId, FireSuppressionState fireState, ExtinguisherState extinguisherState, InventoryState inventoryState, PlayerProgressionState playerProgression, Vec3 worldPosition, double extinguishSeconds, string requiredTool, double radius = 1.8)
        {
            CompartmentId = compartmentId;
            FireState = fireState;
            ExtinguisherState = extinguisherState;
            InventoryState = inventoryState;
            PlayerProgression = playerProgression;
            ExtinguishSeconds = Math.Max(0.01, extinguishSeconds);
            RequiredTool = requiredTool;
            InteractionRadius = radius;
            Channeling = false;
            Progress = 0.0;
            Extinguished = false;
            CandidatePlayerInRange = false;
            LocalPosition = worldPosition;
            NodeName = "FireSuppressionPoint_" + compartmentId;
            // RUNTIME: set_meta("fire_suppression_point", true); sphere collision (radius, disabled = extinguished);
            // GameplayPropFactory.build("extinguisher_station") visual, marker visible = MarkerShown.
        }

        public bool TryStart(Vec3 playerPosition)
        {
            if (Channeling)
                // Already extinguishing — consume interact so lower-priority handlers do not fire.
                return true;
            if (Extinguished || FireState == null)
                return false;
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            if (!FireState.IsBurning(CompartmentId))
            {
                ExtinguishBlocked?.Invoke(CompartmentId, "not_burning");
                return true; // consume; extinguish not started
            }
            // Fire B2: no extinguisher / empty charge -> deliberate vent (instant vacuum).
            if (!HasRequiredTool() || ExtinguisherState == null || !ExtinguisherState.HasChargeForUse())
                return TryVent(playerPosition);
            var channel = new WorkActionChannel();
            if (!channel.Begin(WORK_ACTION_ID, CompartmentId, ExtinguishSeconds, new GdDict()))
            {
                ExtinguishBlocked?.Invoke(CompartmentId, "work_action");
                return true;
            }
            _workChannel = channel;
            Channeling = true;
            Progress = 0.0;
            NotifyChanged();
            return true;
        }

        /// <summary>Fire B2 deliberate vent: open vacuum in this compartment (kills fire, costs air).</summary>
        public bool TryVent(Vec3 playerPosition)
        {
            if (FireState == null)
                return false;
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            if (FireState.IsVented(CompartmentId))
            {
                ExtinguishBlocked?.Invoke(CompartmentId, "already_vented");
                return true; // consume
            }
            if (!FireState.DeliberateVent(CompartmentId))
            {
                ExtinguishBlocked?.Invoke(CompartmentId, "vent_failed");
                return true;
            }
            Extinguished = true;
            SetExtinguishedVisual();
            CompartmentVented?.Invoke(CompartmentId);
            return true;
        }

        bool HasRequiredTool()
        {
            if (string.IsNullOrEmpty(RequiredTool))
                return true;
            if (InventoryState == null)
                return false;
            return InventoryState.GetQuantity(RequiredTool) > 0;
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
            if (ExtinguisherState == null || !ExtinguisherState.HasChargeForUse())
            {
                Progress = 0.0;
                NotifyChanged();
                ExtinguishBlocked?.Invoke(CompartmentId, "no_charge");
                return;
            }
            if (!HasRequiredTool())
            {
                Progress = 0.0;
                NotifyChanged();
                ExtinguishBlocked?.Invoke(CompartmentId, "missing_extinguisher");
                return;
            }
            ExtinguisherState.ConsumeUse();
            if (FireState.Extinguish(CompartmentId))
            {
                Extinguished = true;
                SetExtinguishedVisual();
                if (PlayerProgression != null)
                    PlayerProgression.GrantXp("repair", 10);
                FireExtinguished?.Invoke(CompartmentId);
            }
            else
            {
                Progress = 0.0;
                NotifyChanged();
                ExtinguishBlocked?.Invoke(CompartmentId, "extinguish_failed");
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

        void SetExtinguishedVisual()
        {
            // RUNTIME: collision_shape.disabled = true; marker.visible = false.
            NotifyChanged();
        }
    }
}
