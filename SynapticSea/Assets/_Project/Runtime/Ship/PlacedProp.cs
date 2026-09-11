using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// An authored placed prop (<c>PlacedProp_*</c>), replacing the Godot metas <c>placed_prop</c>,
    /// <c>placed_prop_id</c> and <c>authored_position</c>. Visual only: a GameplayPropFactory primitive (child
    /// <c>Mesh</c>) or an imported dressing prefab (child <c>ImportedVisual</c>).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlacedProp : MonoBehaviour
    {
        public string placedPropId = "";
        /// <summary>GameplayPropFactory id (Godot meta <c>gameplay_prop_id</c>); empty for imported dressing visuals.</summary>
        public string gameplayPropId = "";
        public string visualId = "";
        public long quarterTurns;
        public GdDict Spec { get; internal set; } = new GdDict();
        /// <summary>The authored floor-cell position in the Godot frame (before any catalog y_offset).</summary>
        public Vec3 GodotAuthoredPosition { get; internal set; }
    }
}
