// Ported from scripts/procgen/ceiling_fade_controller.gd @ 96ecb2b0
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Zomboid-style ceiling fade: ceilings whose origin is within <see cref="fadeRadiusM"/> of the player stay fully
    /// visible (they block sight); every other ceiling fades to <see cref="fadeAlpha"/> for isometric readability.
    ///
    /// <para>Godot duplicated each ceiling StandardMaterial3D and set <c>TRANSPARENCY_ALPHA</c> with albedo alpha = fade.
    /// Unity swaps the ceiling renderers to the opaque dithered <c>SynapticSea/LitDitherFade</c> shader instead: depth,
    /// SSAO and shadows stay valid and nothing needs sorting. Materials are shared, one per (source material, fade), and
    /// assigned as <c>sharedMaterials</c> only when a ceiling crosses the radius. Material property blocks are not used
    /// because the GPU Resident Drawer does not support them, and the near/far rule has only two fade values, so two
    /// shared materials per source cover every case.</para>
    ///
    /// <para>No smoothing. Godot had none, and a crossfade would need per-renderer materials, which would break batching.
    /// Godot defect not kept: its <c>_apply_alpha</c> only visited direct <c>MeshInstance3D</c> children of the
    /// <c>Ceiling_*</c> wrapper, which has none (meshes sit under <c>Visual/VisualInstance</c>), so Godot ceilings never
    /// faded. The port applies the documented rule.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CeilingFadeController : MonoBehaviour
    {
        public const float DefaultFadeRadiusM = 12f;
        public const float DefaultFadeAlpha = 0.15f;
        public const string ShaderName = "SynapticSea/LitDitherFade";

        static readonly int FadeId = Shader.PropertyToID("_Fade");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        static readonly int CullId = Shader.PropertyToID("_Cull");

        [Tooltip("Ceilings within this distance (m) of the player stay opaque.")]
        public float fadeRadiusM = DefaultFadeRadiusM;
        [Tooltip("Fraction of pixels kept on ceilings beyond the radius.")]
        public float fadeAlpha = DefaultFadeAlpha;

        sealed class Ceiling
        {
            public Transform Root;
            public Renderer[] Renderers;
            public Material[][] Originals;
            public int State; // -1 unset, 0 far, 1 near
        }

        readonly List<Ceiling> _ceilings = new List<Ceiling>();
        Transform _player;

        static readonly Dictionary<(Material source, float fade), Material> FadeMaterials = new Dictionary<(Material, float), Material>();
        static Shader _shader;

        public int CeilingCount => _ceilings.Count;
        public Transform Player => _player;

        /// <summary>Port of <c>configure(loader_root, player)</c>: collects the loaded ship's ceiling modules.</summary>
        public void Configure(ShipView ship, Transform player)
        {
            var modules = new List<StructuralModule>();
            if (ship != null)
                foreach (var m in ship.Modules)
                    if (m != null && m.layer == "ceiling") modules.Add(m);
            Configure(modules, player);
        }

        /// <summary>Configures an explicit ceiling set (tests and non-loader scenes).</summary>
        public void Configure(IEnumerable<StructuralModule> ceilings, Transform player)
        {
            RestoreOriginalMaterials();
            _ceilings.Clear();
            foreach (var module in ceilings)
            {
                if (module == null) continue;
                var renderers = new List<Renderer>();
                foreach (var r in module.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer)) renderers.Add(r);
                var originals = new Material[renderers.Count][];
                for (int i = 0; i < renderers.Count; i++) originals[i] = renderers[i].sharedMaterials;
                _ceilings.Add(new Ceiling { Root = module.transform, Renderers = renderers.ToArray(), Originals = originals, State = -1 });
            }
            _player = player;
            Apply();
        }

        void LateUpdate() => Apply();

        void OnDestroy() => RestoreOriginalMaterials();

        /// <summary>Port of <c>_process</c>: near ceilings get alpha 1.0, far ones <see cref="fadeAlpha"/>.</summary>
        public void Apply()
        {
            if (_player == null) return;
            Vector3 pp = _player.position;
            foreach (var c in _ceilings)
            {
                if (c.Root == null) continue;
                bool near = Vector3.Distance(c.Root.position, pp) <= fadeRadiusM;
                if (!c.Root.gameObject.activeSelf) c.Root.gameObject.SetActive(true); // Godot: c.visible = true
                int state = near ? 1 : 0;
                if (state == c.State) continue;
                c.State = state;
                float alpha = near ? 1f : fadeAlpha;
                for (int i = 0; i < c.Renderers.Length; i++)
                {
                    var renderer = c.Renderers[i];
                    if (renderer == null) continue;
                    var src = c.Originals[i];
                    var mats = new Material[src.Length];
                    for (int m = 0; m < src.Length; m++) mats[m] = GetFadeMaterial(src[m], alpha);
                    renderer.sharedMaterials = mats;
                }
            }
        }

        /// <summary>The fade value currently applied to a ceiling (1 near, <see cref="fadeAlpha"/> far, -1 unknown).</summary>
        public float GetAppliedAlpha(Transform ceilingRoot)
        {
            foreach (var c in _ceilings)
                if (c.Root == ceilingRoot) return c.State < 0 ? -1f : c.State == 1 ? 1f : fadeAlpha;
            return -1f;
        }

        /// <summary>Puts the ceilings' original materials back.</summary>
        public void RestoreOriginalMaterials()
        {
            foreach (var c in _ceilings)
            {
                for (int i = 0; i < c.Renderers.Length; i++)
                    if (c.Renderers[i] != null) c.Renderers[i].sharedMaterials = c.Originals[i];
                c.State = -1;
            }
        }

        /// <summary>
        /// The shared dither material for <paramref name="source"/> at <paramref name="fade"/>: base colour and texture,
        /// metallic, smoothness, emission and culling are copied from URP Lit (<c>_BaseColor</c>/<c>_BaseMap</c>) or
        /// glTFast (<c>baseColorFactor</c>/<c>baseColorTexture</c>, roughness → smoothness) materials.
        /// </summary>
        public static Material GetFadeMaterial(Material source, float fade)
        {
            if (FadeMaterials.TryGetValue((source, fade), out Material cached) && cached != null) return cached;
            if (_shader == null) _shader = Shader.Find(ShaderName);
            if (_shader == null) throw new System.InvalidOperationException($"CeilingFadeController: shader '{ShaderName}' not found");

            var m = new Material(_shader) { name = $"{(source != null ? source.name : "Default")}_DitherFade_{fade:0.##}", hideFlags = HideFlags.DontSave };
            if (source != null)
            {
                if (TryColor(source, out Color color, "_BaseColor", "baseColorFactor", "_Color")) m.SetColor(BaseColorId, color);
                if (TryTexture(source, out Texture tex, out Vector2 scale, out Vector2 offset, "_BaseMap", "baseColorTexture", "_MainTex"))
                {
                    m.SetTexture(BaseMapId, tex);
                    m.SetTextureScale(BaseMapId, scale);
                    m.SetTextureOffset(BaseMapId, offset);
                }
                if (TryFloat(source, out float metallic, "_Metallic", "metallicFactor")) m.SetFloat(MetallicId, metallic);
                if (source.HasProperty("_Smoothness")) m.SetFloat(SmoothnessId, source.GetFloat("_Smoothness"));
                else if (source.HasProperty("roughnessFactor")) m.SetFloat(SmoothnessId, 1f - source.GetFloat("roughnessFactor"));
                if (TryColor(source, out Color emission, "_EmissionColor", "emissiveFactor") &&
                    (source.IsKeywordEnabled("_EMISSION") || source.IsKeywordEnabled("_EMISSIVE") || source.HasProperty("emissiveFactor")))
                    m.SetColor(EmissionColorId, emission);
                bool doubleSided = source.doubleSidedGI ||
                                   (source.HasProperty("_Cull") && Mathf.Approximately(source.GetFloat("_Cull"), (float)CullMode.Off)) ||
                                   (source.HasProperty("_CullMode") && Mathf.Approximately(source.GetFloat("_CullMode"), (float)CullMode.Off));
                m.SetFloat(CullId, (float)(doubleSided ? CullMode.Off : CullMode.Back));
            }
            m.SetFloat(FadeId, fade);
            FadeMaterials[(source, fade)] = m;
            return m;
        }

        static bool TryColor(Material m, out Color c, params string[] names)
        {
            foreach (var n in names)
                if (m.HasProperty(n)) { c = m.GetColor(n); return true; }
            c = default;
            return false;
        }

        static bool TryFloat(Material m, out float f, params string[] names)
        {
            foreach (var n in names)
                if (m.HasProperty(n)) { f = m.GetFloat(n); return true; }
            f = 0f;
            return false;
        }

        static bool TryTexture(Material m, out Texture t, out Vector2 scale, out Vector2 offset, params string[] names)
        {
            foreach (var n in names)
            {
                if (!m.HasProperty(n)) continue;
                t = m.GetTexture(n);
                if (t == null) continue;
                scale = m.GetTextureScale(n);
                offset = m.GetTextureOffset(n);
                return true;
            }
            t = null;
            scale = Vector2.one;
            offset = Vector2.zero;
            return false;
        }
    }
}
