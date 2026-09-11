using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Shared URP materials and primitive meshes for the runtime-generated ship visuals (markers, trigger volumes,
    /// portal panels, dressing props, readability and gameplay props). Replaces Godot's per-node
    /// <c>StandardMaterial3D.new()</c> / <c>BoxMesh.new()</c>: every distinct (color, shading, transparency, emission,
    /// cull) combination is created once and shared, so the SRP Batcher can batch them.
    ///
    /// StandardMaterial3D mapping: default shading → URP Lit (Godot roughness 1 → smoothness 0, metallic 0);
    /// <c>SHADING_MODE_UNSHADED</c> → URP Unlit; <c>TRANSPARENCY_ALPHA</c> → URP transparent surface (alpha blend,
    /// no depth write, no shadow casting); <c>emission</c> × <c>emission_energy_multiplier</c> → <c>_EmissionColor</c>
    /// (linear); <c>CULL_DISABLED</c> → <c>_Cull</c> Off. Colors are passed as Godot's sRGB values; Unity linearizes
    /// them in a linear-space project exactly like Godot does.
    ///
    /// The shaders are looked up by name, so builds must include URP Lit and Unlit (they are, as long as any project
    /// material references them; add them to Always Included Shaders otherwise).
    /// </summary>
    public static class RuntimeVisualCatalog
    {
        public const string LitShaderName = "Universal Render Pipeline/Lit";
        public const string UnlitShaderName = "Universal Render Pipeline/Unlit";

        static readonly Dictionary<(float r, float g, float b, float a, bool unshaded, bool transparent, float emission, bool doubleSided), Material> Materials =
            new Dictionary<(float, float, float, float, bool, bool, float, bool), Material>();

        static readonly Dictionary<(float top, float bottom, float height), Mesh> Cylinders = new Dictionary<(float, float, float), Mesh>();
        static Mesh _cube, _sphere, _capsule;
        static Shader _lit, _unlit;

        /// <summary>A shared material for a Godot <c>StandardMaterial3D</c> configuration.</summary>
        public static Material Material(Color albedo, bool unshaded = false, bool transparent = false, float emissionEnergy = 0f, bool doubleSided = false)
        {
            var key = (albedo.r, albedo.g, albedo.b, albedo.a, unshaded, transparent, emissionEnergy, doubleSided);
            if (Materials.TryGetValue(key, out Material cached) && cached != null) return cached;

            Shader shader = unshaded ? (_unlit != null ? _unlit : _unlit = Shader.Find(UnlitShaderName))
                                     : (_lit != null ? _lit : _lit = Shader.Find(LitShaderName));
            if (shader == null) throw new InvalidOperationException($"RuntimeVisualCatalog: shader '{(unshaded ? UnlitShaderName : LitShaderName)}' not found");
            var m = new Material(shader)
            {
                name = $"Runtime_{(unshaded ? "Unlit" : "Lit")}{(transparent ? "_Alpha" : "")}{(emissionEnergy > 0f ? "_Emissive" : "")}_{ColorUtility.ToHtmlStringRGBA(albedo)}",
                hideFlags = HideFlags.DontSave,
            };
            m.SetColor("_BaseColor", albedo);
            if (!unshaded)
            {
                m.SetFloat("_Smoothness", 0f);
                m.SetFloat("_Metallic", 0f);
            }
            if (transparent)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetShaderPassEnabled("ShadowCaster", false);
                m.SetShaderPassEnabled("DepthOnly", false);
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            if (doubleSided) m.SetFloat("_Cull", (float)CullMode.Off);
            if (emissionEnergy > 0f && !unshaded)
            {
                // Godot: linear(emission) × energy, added to the lit result. SetColor linearizes, so hand it the
                // gamma-encoded form of the linear product.
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                m.SetColor("_EmissionColor", (new Color(albedo.r, albedo.g, albedo.b, 1f).linear * emissionEnergy).gamma);
            }
            Materials[key] = m;
            return m;
        }

        /// <summary>Unit cube (Godot <c>BoxMesh</c> of size 1; scale by the box size).</summary>
        public static Mesh Cube => _cube != null ? _cube : _cube = PrimitiveMesh(PrimitiveType.Cube);

        /// <summary>Unit-diameter sphere (Godot <c>SphereMesh(radius r, height h)</c> → scale (2r, h, 2r)).</summary>
        public static Mesh Sphere => _sphere != null ? _sphere : _sphere = PrimitiveMesh(PrimitiveType.Sphere);

        /// <summary>Unity capsule (height 2, radius 0.5).</summary>
        public static Mesh Capsule => _capsule != null ? _capsule : _capsule = PrimitiveMesh(PrimitiveType.Capsule);

        /// <summary>Godot <c>CylinderMesh</c> (top/bottom radius, height; centered on its origin), built once per shape.</summary>
        public static Mesh Cylinder(float topRadius, float bottomRadius, float height)
        {
            var key = (topRadius, bottomRadius, height);
            if (Cylinders.TryGetValue(key, out Mesh cached) && cached != null) return cached;
            Mesh mesh = BuildFrustum(topRadius, bottomRadius, height, 32);
            Cylinders[key] = mesh;
            return mesh;
        }

        /// <summary>Adds a child renderer (Godot MeshInstance3D) with a shared mesh + material.</summary>
        public static GameObject AddMesh(Transform parent, string name, Mesh mesh, Material material, Vector3 localPosition,
            Quaternion localRotation, Vector3 localScale, int layer, bool castShadows = true)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = localRotation;
            go.transform.localScale = localScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = castShadows && material.GetTag("RenderType", false) != "Transparent" ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return go;
        }

        /// <summary>
        /// Adds a point light for a Godot <c>OmniLight3D</c>: same range, intensity = energy ×
        /// <see cref="AtmosphereApplier.OmniEnergyScale"/>, no shadows (Godot's default <c>shadow_enabled = false</c>).
        /// </summary>
        public static Light AddOmniLight(Transform parent, string name, Vector3 localPosition, Color color, float energy, float range, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = energy * AtmosphereApplier.OmniEnergyScale;
            light.shadows = LightShadows.None;
            return light;
        }

        static Mesh PrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
            return mesh;
        }

        static Mesh BuildFrustum(float topRadius, float bottomRadius, float height, int segments)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            float half = height * 0.5f;
            float slope = (bottomRadius - topRadius) / Mathf.Max(height, 1e-6f);

            // Side.
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float a = t * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Vector3 n = new Vector3(dir.x, slope, dir.z).normalized;
                vertices.Add(dir * topRadius + Vector3.up * half);
                normals.Add(n);
                uvs.Add(new Vector2(t, 1f));
                vertices.Add(dir * bottomRadius - Vector3.up * half);
                normals.Add(n);
                uvs.Add(new Vector2(t, 0f));
            }
            for (int i = 0; i < segments; i++)
            {
                int top0 = i * 2, bottom0 = top0 + 1, top1 = top0 + 2, bottom1 = top0 + 3;
                triangles.Add(top0); triangles.Add(top1); triangles.Add(bottom0);
                triangles.Add(bottom0); triangles.Add(top1); triangles.Add(bottom1);
            }
            // Caps.
            AddCap(vertices, normals, uvs, triangles, topRadius, half, Vector3.up, segments);
            AddCap(vertices, normals, uvs, triangles, bottomRadius, -half, Vector3.down, segments);

            var mesh = new Mesh { name = $"Cylinder_{topRadius:0.###}_{bottomRadius:0.###}_{height:0.###}", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        static void AddCap(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> tris, float radius, float y, Vector3 normal, int segments)
        {
            if (radius <= 0f) return;
            int center = v.Count;
            v.Add(new Vector3(0f, y, 0f));
            n.Add(normal);
            uv.Add(new Vector2(0.5f, 0.5f));
            for (int i = 0; i <= segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                v.Add(new Vector3(Mathf.Cos(a) * radius, y, Mathf.Sin(a) * radius));
                n.Add(normal);
                uv.Add(new Vector2(Mathf.Cos(a) * 0.5f + 0.5f, Mathf.Sin(a) * 0.5f + 0.5f));
            }
            for (int i = 0; i < segments; i++)
            {
                int a = center + 1 + i, b = center + 2 + i;
                // Unity is clockwise-front: the top cap (normal +Y) winds center → b → a when viewed from above.
                if (normal.y > 0f) { tris.Add(center); tris.Add(b); tris.Add(a); }
                else { tris.Add(center); tris.Add(a); tris.Add(b); }
            }
        }
    }
}
