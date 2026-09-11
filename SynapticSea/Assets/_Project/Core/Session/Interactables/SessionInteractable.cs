// Shared engine-free base for the coordinator-spawned Area3D interaction nodes
// (scripts/tools/*.gd, scripts/interaction/interactable.gd, scripts/interaction/sealed_hatch.gd @ 96ecb2b0).
using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// The model half of one Godot interaction <c>Area3D</c>. Godot kept gameplay state on the node (<c>searched</c>,
    /// <c>repaired</c>, <c>channeling</c>, ...); here it lives on this object, and the Runtime spawns one scene object per
    /// instance (<see cref="SessionEvents.InteractableSpawned"/>) and re-reads its state on
    /// <see cref="StateChanged"/>.
    ///
    /// Position: Godot parented each node either under a ship scene root (<see cref="Parent"/>, ship-local
    /// <see cref="LocalPosition"/>) or under one of the coordinator's origin roots (<see cref="Parent"/> null, the
    /// local position is world). <see cref="GlobalPosition"/> reproduces <c>global_position</c>.
    ///
    /// Range: Godot tools used two gates — the Area3D overlap (<c>candidate_player</c>, set by body_entered and by the
    /// validation seam) and a direct distance check against the sphere radius. The Runtime feeds overlap through
    /// <see cref="CandidatePlayerInRange"/>; the distance check is computed here from the player position.
    /// </summary>
    public abstract class SessionInteractable
    {
        /// <summary>A stable kind tag for the Runtime spawner (e.g. "repair_point").</summary>
        public abstract string Kind { get; }

        /// <summary>The Godot node name (<c>RepairPoint_power_reactor_core</c>, ...).</summary>
        public string NodeName = "";

        /// <summary>RUNTIME: the ship scene root the Godot node was parented under; null for the coordinator origin roots.</summary>
        public IShipSceneRoot Parent;

        /// <summary><c>position</c> (local to <see cref="Parent"/>; world when there is no parent).</summary>
        public Vec3 LocalPosition = Vec3.Zero;

        /// <summary><c>interaction_radius</c> (also the collision sphere radius).</summary>
        public double InteractionRadius = 1.8;

        /// <summary><c>candidate_player != null</c>: the Area3D overlap reported by the Runtime (or forced by validation).</summary>
        public bool CandidatePlayerInRange;

        /// <summary>False once the session freed the node (<c>queue_free</c>); <c>is_instance_valid</c> is false after that.</summary>
        public bool IsValid { get; private set; } = true;

        /// <summary>Raised whenever a state field the scene renders changes (marker visibility, collider, progress, ...).</summary>
        public event Action<SessionInteractable> StateChanged;

        protected void NotifyChanged() => StateChanged?.Invoke(this);

        /// <summary>Marks the node freed (the session raises <see cref="SessionEvents.InteractableDespawned"/>).</summary>
        public void Free() => IsValid = false;

        /// <summary><c>is_inside_tree()</c>: coordinator-root children always are; ship children follow their root.</summary>
        public bool IsInsideTree => Parent == null || (Parent.IsValid && Parent.IsInsideTree);

        /// <summary><c>global_position</c>.</summary>
        public Vec3 GlobalPosition
        {
            get
            {
                if (Parent != null && Parent.IsValid && Parent.IsInsideTree)
                    return Parent.GlobalTransform * LocalPosition;
                return LocalPosition;
            }
        }

        /// <summary><c>set_validation_player_in_range(player)</c>.</summary>
        public void SetValidationPlayerInRange(bool inRange = true) => CandidatePlayerInRange = inRange;

        /// <summary>
        /// The strict tool gate (<c>RepairPoint._is_player_in_direct_range</c> style): both nodes in the tree, global
        /// distance &lt;= radius.
        /// </summary>
        public bool IsPlayerInDirectRangeStrict(Vec3 playerPosition, bool playerInTree = true)
        {
            if (!IsValid || !IsInsideTree || !playerInTree)
                return false;
            return GlobalPosition.DistanceTo(playerPosition) <= InteractionRadius;
        }

        /// <summary>
        /// The lenient gate (<c>Interactable._is_player_in_direct_range</c> style): global position when in the tree,
        /// otherwise the local position, compared with the player position.
        /// </summary>
        public bool IsPlayerInDirectRangeLenient(Vec3 playerPosition)
        {
            if (!IsValid)
                return false;
            Vec3 here = IsInsideTree ? GlobalPosition : LocalPosition;
            return here.DistanceTo(playerPosition) <= InteractionRadius;
        }
    }
}
