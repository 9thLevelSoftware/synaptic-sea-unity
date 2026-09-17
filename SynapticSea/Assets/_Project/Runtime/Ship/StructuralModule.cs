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

        public const string IntegrityDestroyed = "destroyed";

        /// <summary>
        /// True for Godot "legacy" wrappers with a single <c>VisualInstance</c> (corners, T-junction, end cap, ceiling,
        /// bulkhead): the prefab builder points all three variant slots at the same object.
        /// </summary>
        public bool HasSingleVisual => intactVisual != null && damagedVisual == intactVisual && breachedVisual == intactVisual;

        /// <summary>
        /// Port of IntegrityVisualResolver.apply_visual_state. Variant wrappers show exactly the requested variant
        /// (none for <c>destroyed</c> or a state without a variant). Single-visual wrappers stay visible for every state
        /// except <c>destroyed</c> (Godot's legacy branch; its per-state albedo tint is not ported).
        /// </summary>
        public void SetIntegrity(string state)
        {
            integrityState = string.IsNullOrEmpty(state) ? IntegrityIntact : state;
            if (HasSingleVisual)
            {
                intactVisual.SetActive(!string.Equals(integrityState, IntegrityDestroyed, StringComparison.Ordinal));
                return;
            }
            GameObject shown = null;
            if (string.Equals(integrityState, IntegrityIntact, StringComparison.Ordinal)) shown = intactVisual;
            else if (string.Equals(integrityState, IntegrityDamaged, StringComparison.Ordinal)) shown = damagedVisual;
            else if (string.Equals(integrityState, IntegrityBreached, StringComparison.Ordinal)) shown = breachedVisual;
            // Deactivate the others first, so a slot shared by two variants cannot end up hidden.
            if (intactVisual != null && intactVisual != shown) intactVisual.SetActive(false);
            if (damagedVisual != null && damagedVisual != shown) damagedVisual.SetActive(false);
            if (breachedVisual != null && breachedVisual != shown) breachedVisual.SetActive(false);
            if (shown != null) shown.SetActive(true);
        }
    }
}
