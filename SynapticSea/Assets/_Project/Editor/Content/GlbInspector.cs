using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>
    /// Dumps the imported hierarchy of selected GLBs (node names, local TRS, mesh bounds, materials/shaders)
    /// to builds/logs/glb-inspect.txt. Used to verify glTFast's axis conversion against the Godot contracts.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.GlbInspector.Run -quit
    /// </summary>
    public static class GlbInspector
    {
        static readonly string[] Targets =
        {
            "Assets/Content/Structural/ship_structural_v0/wall_straight_1x1/wall_straight_1x1.glb",
            "Assets/Content/Structural/ship_structural_v0/wall_outer_corner/wall_outer_corner.glb",
            "Assets/Content/Structural/ship_structural_v0/floor_1x1/floor_1x1.glb",
            "Assets/Content/Structural/ship_structural_v0/doorway_frame_open_1x1/doorway_frame_open_1x1.glb",
            "Assets/Content/Structural/ship_structural_v0/wall_straight_1x1/wall_straight_1x1_textured.glb",
            "Assets/Content/Props/components/console_generic.glb",
        };

        [MenuItem("Synaptic Sea/Content/Inspect GLB Imports")]
        public static void Run()
        {
            var sb = new StringBuilder();
            foreach (string path in Targets)
            {
                var importer = AssetImporter.GetAtPath(path);
                sb.AppendLine($"=== {path}  importer={(importer == null ? "NONE" : importer.GetType().FullName)}");
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null)
                {
                    sb.AppendLine("   (no GameObject main asset)");
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path)) sb.AppendLine($"   sub: {o.GetType().Name} {o.name}");
                    continue;
                }
                Dump(go.transform, 1, sb);
                var renderers = go.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length > 0)
                {
                    var b = renderers[0].bounds;
                    foreach (var r in renderers.Skip(1)) b.Encapsulate(r.bounds);
                    sb.AppendLine($"   total bounds min={F(b.min)} max={F(b.max)}");
                }
            }
            Directory.CreateDirectory("../builds/logs");
            File.WriteAllText("../builds/logs/glb-inspect.txt", sb.ToString());
            Debug.Log("[GlbInspector] wrote builds/logs/glb-inspect.txt");
        }

        static void Dump(Transform t, int depth, StringBuilder sb)
        {
            string pad = new string(' ', depth * 3);
            sb.Append($"{pad}{t.name}  pos={F(t.localPosition)} rotEuler={F(t.localEulerAngles)} scale={F(t.localScale)}");
            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                sb.Append($"  mesh={mf.sharedMesh.name} verts={mf.sharedMesh.vertexCount} localBounds[min={F(mf.sharedMesh.bounds.min)} max={F(mf.sharedMesh.bounds.max)}]");
            var mr = t.GetComponent<Renderer>();
            if (mr != null)
                sb.Append("  mats=[" + string.Join(", ", mr.sharedMaterials.Select(m => m == null ? "null" : $"{m.name}<{m.shader.name}>")) + "]");
            var col = t.GetComponent<Collider>();
            if (col != null) sb.Append($"  collider={col.GetType().Name}");
            sb.AppendLine();
            foreach (Transform c in t) Dump(c, depth + 1, sb);
        }

        static string F(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", v.x, v.y, v.z);
    }
}
