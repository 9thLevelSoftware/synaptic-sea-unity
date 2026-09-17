using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
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
        /// Material colour properties the legacy tint multiplies, in lookup order: glTFast's URP shader graphs
        /// (<c>baseColorFactor</c>), URP Lit (<c>_BaseColor</c>), legacy shaders (<c>_Color</c>).
        /// </summary>
        static readonly int[] TintColorIds =
        {
            Shader.PropertyToID("baseColorFactor"),
            Shader.PropertyToID("_BaseColor"),
            Shader.PropertyToID("_Color"),
        };

        [NonSerialized] bool _tinted;
        [NonSerialized] MaterialPropertyBlock _tintBlock;

        /// <summary>The legacy tint currently applied (white when none).</summary>
        public Color LegacyTint { get; private set; } = Color.white;

        /// <summary>
        /// Port of IntegrityVisualResolver.apply_visual_state. Variant wrappers show exactly the requested variant
        /// (none for <c>destroyed</c> or a state without a variant). Single-visual wrappers stay visible for every state
        /// except <c>destroyed</c> and take Godot's legacy per-state albedo tint
        /// (<see cref="ModuleIntegrityConsequences.ConsequenceForState"/> <c>modulate</c>). Unlike Godot, whose loader only
        /// ran the resolver for non-intact states, a repair back to <c>intact</c> clears the tint.
        /// </summary>
        public void SetIntegrity(string state)
        {
            integrityState = string.IsNullOrEmpty(state) ? IntegrityIntact : state;
            if (HasSingleVisual)
            {
                intactVisual.SetActive(!string.Equals(integrityState, IntegrityDestroyed, StringComparison.Ordinal));
                ApplyLegacyTint(ModulateFor(integrityState));
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

        /// <summary>The <c>modulate</c> colour Godot's legacy branch applied for <paramref name="state"/>.</summary>
        public static Color ModulateFor(string state)
        {
            if (!(ModuleIntegrityConsequences.ConsequenceForState(state).Get("modulate", null) is GdArray c) || c.Count < 4) return Color.white;
            return new Color((float)V.F64(c[0]), (float)V.F64(c[1]), (float)V.F64(c[2]), (float)V.F64(c[3]));
        }

        /// <summary>
        /// Godot <c>_apply_legacy_tint</c>: multiplies every intact-visual material's base colour by
        /// <paramref name="modulate"/> through a per-material <see cref="MaterialPropertyBlock"/> (shared materials are never
        /// modified). White removes the blocks, so untinted modules keep SRP batching. Godot also switched to alpha blending
        /// below 0.99 alpha; only <c>destroyed</c> uses that, and the visual is hidden then, so blending is not switched.
        /// Variant wrappers are never tinted (Godot: the albedo tint fights the variant visuals).
        /// </summary>
        public void ApplyLegacyTint(Color modulate)
        {
            if (!HasSingleVisual) return;
            bool white = Mathf.Approximately(modulate.r, 1f) && Mathf.Approximately(modulate.g, 1f) &&
                         Mathf.Approximately(modulate.b, 1f) && Mathf.Approximately(modulate.a, 1f);
            if (white && !_tinted) return;
            LegacyTint = white ? Color.white : modulate;
            if (_tintBlock == null) _tintBlock = new MaterialPropertyBlock();
            foreach (Renderer r in intactVisual.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = r.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (white)
                    {
                        r.SetPropertyBlock(null, i);
                        continue;
                    }
                    Material m = materials[i];
                    int id = TintPropertyFor(m);
                    if (id == 0) continue;
                    _tintBlock.Clear();
                    _tintBlock.SetColor(id, m.GetColor(id) * modulate);
                    r.SetPropertyBlock(_tintBlock, i);
                }
            }
            _tinted = !white;
        }

        /// <summary>The colour property the legacy tint drives on <paramref name="material"/> (0 when it has none).</summary>
        public static int TintPropertyFor(Material material)
        {
            if (material == null) return 0;
            foreach (int id in TintColorIds)
                if (material.HasProperty(id)) return id;
            return 0;
        }
    }
}
