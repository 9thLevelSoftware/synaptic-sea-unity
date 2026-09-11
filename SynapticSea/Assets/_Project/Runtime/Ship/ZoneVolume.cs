using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// A hazard / atmosphere trigger box built by the ship loader (Godot <c>_make_trigger_volume</c> Area3D):
    /// <c>RadiationZone_*</c> (oriented along its segment) or <c>AuthoredAtmosphere_*</c>. A trigger BoxCollider on
    /// the Sensor layer (bottom at the node origin, like Godot's shape offset) plus the translucent box visual.
    /// <see cref="Spec"/> replaces the Godot meta <c>atmosphere</c> (atmosphere volumes) and carries the zone spec
    /// for radiation volumes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ZoneVolume : MonoBehaviour
    {
        public const string KindRadiation = "radiation";
        public const string KindAtmosphere = "atmosphere";

        public string kind;
        public Vector3 size;
        public GdDict Spec { get; internal set; } = new GdDict();
        /// <summary>Volume origin in the Godot frame, local to the ship root.</summary>
        public Vec3 GodotPosition { get; internal set; }
    }
}
