// Ported from scripts/tools/dock_port_barrier.gd @ 96ecb2b0
using System;
using System.Diagnostics;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// A closed dock-seam barrier at a derelict's dock port. An intact port opens in one interact; a broken port
    /// requires a timed, welding-speeded breach channel (PZ-style — leaving range cancels with no loss). No parts
    /// consumed; the breach always eventually succeeds.
    /// </summary>
    public sealed class DockPortBarrier : SessionInteractable
    {
        public override string Kind => "dock_port_barrier";

        /// <summary>signal breach_opened(marker_id)</summary>
        public event Action<string> BreachOpened;

        public string MarkerId = "";
        /// <summary>"intact" | "broken"</summary>
        public string Condition = "intact";
        public PlayerProgressionState PlayerProgression;
        public double BreachSeconds = 6.0;

        public bool Opened = false;
        public bool Channeling = false;
        /// <summary>0..1</summary>
        public double Progress = 0.0;

        double _scaledSeconds = 1.0;

        /// <summary>RUNTIME: <c>collision_shape.disabled = opened</c> (opening removes the blocking collider).</summary>
        public bool CollisionDisabled => Opened;

        /// <summary>RUNTIME: <c>marker.visible = not opened</c>.</summary>
        public bool MarkerShown => !Opened;

        public void Configure(string markerId, string condition, PlayerProgressionState playerProgression, Vec3 worldPosition, double breachSeconds, double radius = 1.8)
        {
            MarkerId = markerId;
            Condition = condition;
            PlayerProgression = playerProgression;
            BreachSeconds = breachSeconds;
            Debug.Assert(radius >= 0.0, "DockPortBarrier.configure: radius must be non-negative");
            InteractionRadius = radius;
            Opened = false;
            Channeling = false;
            Progress = 0.0;
            CandidatePlayerInRange = false;
            LocalPosition = worldPosition;
            NodeName = "DockPortBarrier_" + markerId;
            // RUNTIME: set_meta("dock_port_barrier", true); sphere collision (radius, disabled = opened); box marker
            // (radius*0.5 x radius x radius*0.5, red 0.85,0.2,0.2,0.7 unshaded, no shadow), visible = not opened.
        }

        long PlayerSkill()
        {
            if (PlayerProgression != null)
                return PlayerProgression.GetSkillLevel("welding");
            return 0;
        }

        /// <summary>
        /// Intact: open immediately (one interact). Broken: start the welding-speeded channel. Returns true if the
        /// interaction was consumed (opened or channel started).
        /// </summary>
        public bool TryStart(Vec3 playerPosition)
        {
            if (Opened)
                return false;
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            if (Condition != "broken")
            {
                SetOpened(true);
                BreachOpened?.Invoke(MarkerId);
                return true;
            }
            if (Channeling)
                // Already breaching — consume interact so lower-priority handlers do not fire.
                return true;
            Channeling = true;
            Progress = 0.0;
            double factor = 1.0 + 0.1 * (double)Math.Max(0L, PlayerSkill());
            _scaledSeconds = Math.Max(0.01, BreachSeconds / factor);
            NotifyChanged();
            return true;
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
            if (!Channeling)
                return;
            Progress = GdMath.Clampf(Progress + delta / _scaledSeconds, 0.0, 1.0);
            NotifyChanged();
            if (Progress >= 1.0)
                Complete();
        }

        void Complete()
        {
            Channeling = false;
            SetOpened(true);
            if (PlayerProgression != null)
                PlayerProgression.GrantXp("welding", 25);
            BreachOpened?.Invoke(MarkerId);
        }

        void Cancel()
        {
            Channeling = false;
            Progress = 0.0;
            NotifyChanged();
        }

        public void SetOpened(bool value)
        {
            Opened = value;
            Channeling = false;
            Progress = value ? 1.0 : 0.0;
            // RUNTIME: collision_shape.disabled = opened; marker.visible = not opened.
            NotifyChanged();
        }
    }
}
