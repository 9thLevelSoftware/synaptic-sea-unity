using System.Collections.Generic;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Cached URP materials for code-built visuals (the Godot code created a StandardMaterial3D per node; here each
    /// distinct look is one shared material so the SRP Batcher keeps batching). Keyed by colour and style.
    /// </summary>
    public static class RuntimeMaterials
    {
        static readonly Dictionary<string, Material> Cache = new Dictionary<string, Material>();

        const string UnlitShader = "Universal Render Pipeline/Unlit";
        const string LitShader = "Universal Render Pipeline/Lit";

        /// <summary>Godot <c>SHADING_MODE_UNSHADED</c> with an albedo colour (opaque unless alpha &lt; 1).</summary>
        public static Material Unlit(Color color) => Get("unlit", color, 0f, () => Make(UnlitShader, color, transparent: color.a < 0.999f, emission: 0f));

        /// <summary>Lit material with optional emission (Godot albedo + <c>emission_energy</c>).</summary>
        public static Material Lit(Color color, float emission = 0f) =>
            Get("lit", color, emission, () => Make(LitShader, color, transparent: color.a < 0.999f, emission: emission));

        static Material Get(string style, Color c, float emission, System.Func<Material> factory)
        {
            string key = $"{style}:{ColorUtility.ToHtmlStringRGBA(c)}:{emission:0.###}";
            if (!Cache.TryGetValue(key, out Material m) || m == null)
            {
                m = factory();
                m.name = "Runtime_" + key.Replace(':', '_');
                Cache[key] = m;
            }
            return m;
        }

        static Material Make(string shaderName, Color color, bool transparent, float emission)
        {
            var shader = Shader.Find(shaderName) ?? Shader.Find("Hidden/InternalErrorShader");
            var m = new Material(shader) { hideFlags = HideFlags.DontSave };
            m.SetColor("_BaseColor", color);
            if (transparent)
            {
                m.SetFloat("_Surface", 1f); // Transparent
                m.SetFloat("_Blend", 0f);   // Alpha
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", color * emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            return m;
        }

        /// <summary>Drops cached materials (domain reload / tests).</summary>
        public static void Clear()
        {
            foreach (var m in Cache.Values)
                if (m != null) Object.DestroyImmediate(m);
            Cache.Clear();
        }
    }
}
