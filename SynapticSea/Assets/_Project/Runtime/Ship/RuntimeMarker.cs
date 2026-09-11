using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// A coherence marker built by the ship loader (<c>_make_marker_node</c>): <c>Landmark_*</c>,
    /// <c>BlockedRoute_*</c> or <c>VisibleVerticalTransition_*</c>. A box visual plus, when collidable, a
    /// <c>CollisionRoot</c> box collider (Structure layer; blocked routes use ZoneBlocker).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RuntimeMarker : MonoBehaviour
    {
        public const string KindLandmark = "landmark";
        public const string KindBlockedRoute = "blocked_route";
        public const string KindVerticalTransition = "vertical_transition";

        public string kind;
        /// <summary>Marker position in the Godot frame, local to the ship root.</summary>
        public Vec3 GodotPosition { get; internal set; }
        /// <summary>Godot basis columns of the marker (identity unless it was oriented with look_at).</summary>
        public Vec3 GodotBasisX { get; internal set; } = Vec3.Right;
        public Vec3 GodotBasisY { get; internal set; } = Vec3.Up;
        public Vec3 GodotBasisZ { get; internal set; } = Vec3.Back;
    }
}
