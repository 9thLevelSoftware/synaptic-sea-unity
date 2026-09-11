using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// A child of <c>DressingVisuals</c> (PKG-B5.1 room dressing): the room light, its fog marker sphere, or a
    /// visual-only dressing prop. Replaces the Godot metas <c>dressing</c>, <c>prop_density</c>, <c>fog_density</c>,
    /// <c>tint</c>, <c>dressing_kind</c>, <c>slot_kind</c>, <c>slot_index</c>, <c>slot_cell</c> and
    /// <c>collision_policy</c> ("none_visual_only" for props).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DressingVisual : MonoBehaviour
    {
        public string roomId = "";
        public string dressing = "";
        /// <summary>"light", "fog" or "prop".</summary>
        public string role = "";
        public double propDensity;
        public double fogDensity;
        /// <summary>crate / pipe / growth for props.</summary>
        public string dressingKind = "";
        public string slotKind = "";
        public long slotIndex;
        public GdArray SlotCell { get; internal set; }
        public GdArray Tint { get; internal set; }
        public Vec3 GodotPosition { get; internal set; }
    }
}
