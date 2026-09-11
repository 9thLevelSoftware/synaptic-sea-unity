using System;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Root component of a structural kit prefab (port of the Godot wrapper scene root + the metadata the loader
    /// stamped with <c>set_meta</c>). Built by the Editor StructuralPrefabBuilder from the kit JSON, the placement
    /// contract, and the Godot wrapper collision boxes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StructuralModule : MonoBehaviour
    {
        public const string IntegrityIntact = "intact";
        public const string IntegrityDamaged = "damaged";
        public const string IntegrityBreached = "breached";

        [Header("Kit identity")]
        public string kitId;
        public string moduleId;
        public string moduleFamily;
        public Vector2Int footprintCells;
        public bool navBlocker;
        public string pivotPolicy;

        [Header("Contract bounds (Godot frame, metres)")]
        public Vector3 godotBoundsMin;
        public Vector3 godotBoundsMax;

        [Header("Visual variants")]
        public GameObject intactVisual;
        public GameObject damagedVisual;
        public GameObject breachedVisual;

        [Header("Placement metadata (set by the ship scene builder; replaces Godot set_meta keys)")]
        public string moduleKey;
        public string structuralKind;
        public string roomId;
        public string[] roomIds;
        public string placementId;
        /// <summary>Edge key (walls/portals) or cell key (floors/ceilings) from the structural plan.</summary>
        public string placementKey;
        /// <summary>"edge", "floor" or "ceiling".</summary>
        public string layer;
        /// <summary>Placement in Godot's frame (metres) and Godot yaw degrees, as authored in layout.json.</summary>
        public Vector3 godotPosition;
        public float godotYawDegrees;
        public string integrityState = IntegrityIntact;

        /// <summary>Port of IntegrityVisualResolver.apply_visual_state: exactly one variant is visible.</summary>
        public void SetIntegrity(string state)
        {
            integrityState = string.IsNullOrEmpty(state) ? IntegrityIntact : state;
            bool damaged = string.Equals(integrityState, IntegrityDamaged, StringComparison.Ordinal);
            bool breached = string.Equals(integrityState, IntegrityBreached, StringComparison.Ordinal);
            if (intactVisual != null) intactVisual.SetActive(!damaged && !breached);
            if (damagedVisual != null) damagedVisual.SetActive(damaged);
            if (breachedVisual != null) breachedVisual.SetActive(breached);
        }
    }
}
