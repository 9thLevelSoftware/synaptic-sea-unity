using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Root of a visual-only prop prefab (port of a Godot prop sidecar binding, mounted by RuntimePropVisualBinder).
    /// Built by the Editor PropPrefabBuilder from <c>*.sidecar.json</c>; carries no collider (<c>none_visual_only</c>).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PropVisual : MonoBehaviour
    {
        public string assetId;
        public string propKind;
        public string bindingNamespace;
        public string[] bindingIds;
        public float[] allowedYawDegrees;
        public string surface;
        public string collisionPolicy;
        public Vector3 godotBoundsMin;
        public Vector3 godotBoundsMax;
        public string sourceSha256;
    }
}
