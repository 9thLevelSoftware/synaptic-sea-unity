using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Root of a ReadabilityPropFactory prop; replaces the Godot metas <c>readability_kind</c>,
    /// <c>normal_mode_visual</c>, <c>objective_type</c>, <c>sequence</c>, <c>route_from</c>, <c>route_to</c> and
    /// <c>route_index</c>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ReadabilityProp : MonoBehaviour
    {
        public string readabilityKind = "";
        public bool normalModeVisual = true;
        public string objectiveType = "";
        public long sequence;
        public long routeIndex;
        /// <summary>Route endpoints in the Godot frame (route cues only).</summary>
        public Vec3 GodotRouteFrom { get; internal set; }
        public Vec3 GodotRouteTo { get; internal set; }
    }
}
