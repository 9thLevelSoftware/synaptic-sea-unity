using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.Tests.Unity
{
    /// <summary>
    /// Gate for the single Godot→Unity coordinate conversion (<see cref="Frame"/>).
    /// </summary>
    public class FrameConventionTests
    {
        const string Kit = "ship_structural_v0";
        const float Eps = 1e-3f;

        static GdDict Contract(string moduleId)
        {
            string path = Path.Combine(Application.streamingAssetsPath, "data", "placement", "contracts", "structural", Kit, moduleId + "_contract.json");
            return File.Exists(path) ? GdJson.ParseDict(File.ReadAllText(path)) : null;
        }

        /// <summary>
        /// Blender-authored SOCK_* nodes inside the textured GLBs are ground truth for glTFast's axis conversion:
        /// each must land exactly where Frame puts the contract's Godot-frame socket position.
        /// </summary>
        /// <summary>
        /// Contract sockets whose Godot data disagrees with the authored GLB for reasons unrelated to the frame
        /// (Blender Z-up leftovers in the contract). Godot's runtime used the contract value, so the port keeps it.
        /// </summary>
        static readonly HashSet<string> KnownContractDefects = new HashSet<string>(StringComparer.Ordinal)
        {
            "pillar_support_1x1/prop_anchor_down_01",
            "pillar_support_1x1/prop_anchor_up_01",
        };

        [Test]
        public void GltfImportedSockets_MatchContractUnderFrame()
        {
            int compared = 0;
            var mismatches = new List<string>();
            foreach (string glb in AssetDatabase.FindAssets("t:GameObject", new[] { $"Assets/Content/Structural/{Kit}" })
                         .Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith("_textured.glb", StringComparison.Ordinal)))
            {
                string moduleId = Path.GetFileNameWithoutExtension(glb).Replace("_textured", "");
                var contract = Contract(moduleId);
                if (contract == null) continue;
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(glb);
                var byName = model.GetComponentsInChildren<Transform>(true).ToDictionary(t => t.name, t => t, StringComparer.Ordinal);
                foreach (object s in contract.GetArrayOrEmpty("sockets"))
                {
                    var socket = (GdDict)s;
                    if (!byName.TryGetValue("SOCK_" + socket.GetString("id"), out Transform node)) continue;
                    Vector3 expected = Frame.ToUnity(socket.Get("position_m"));
                    Vector3 actual = model.transform.InverseTransformPoint(node.position);
                    string key = moduleId + "/" + socket.GetString("id");
                    compared++;
                    if (Vector3.Distance(expected, actual) >= Eps && !KnownContractDefects.Contains(key))
                        mismatches.Add($"{key}: Frame={expected:F3} glTFast={actual:F3}");
                }
            }
            Assert.That(compared, Is.GreaterThan(10), "expected textured GLBs with authored SOCK_ nodes");
            Assert.IsEmpty(mismatches, $"{mismatches.Count}/{compared} sockets disagree:\n" + string.Join("\n", mismatches));
            TestContext.WriteLine($"compared {compared} authored sockets");
        }

        /// <summary>
        /// Placing a prefab with Frame at every kit yaw must put each socket where Godot's
        /// <c>placement + local.rotated(Vector3.UP, deg_to_rad(yaw))</c> puts it (then converted).
        /// </summary>
        [Test]
        public void PrefabSockets_MatchGodotPlacementMath_AtAllYaws([Values(0, 90, 180, 270)] int yaw)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>($"Assets/Resources/Catalogs/KitCatalog_{Kit}.asset");
            Assert.IsNotNull(catalog, "run the StructuralPrefabBuilder first");
            var godotPlacement = new Vec3(12f, 0f, -8f);
            int compared = 0;
            foreach (var entry in catalog.modules)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(entry.prefab.gameObject);
                try
                {
                    instance.transform.SetPositionAndRotation(Frame.ToUnity(godotPlacement), Frame.YawRotation(yaw));
                    foreach (var socket in instance.GetComponentsInChildren<SocketMarker>(true))
                    {
                        var local = new Vec3(socket.godotLocalPosition.x, socket.godotLocalPosition.y, socket.godotLocalPosition.z);
                        Vec3 godotWorld = godotPlacement + GodotRotatedAboutUp(local, yaw);
                        Vector3 expected = Frame.ToUnity(godotWorld);
                        Assert.That(Vector3.Distance(expected, socket.transform.position), Is.LessThan(Eps),
                            $"{entry.moduleId} yaw {yaw} socket {socket.socketId}");
                        compared++;
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
            Assert.That(compared, Is.GreaterThan(20));
        }

        [Test]
        public void EveryKitModuleHasAPrefabWithCollision()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>($"Assets/Resources/Catalogs/KitCatalog_{Kit}.asset");
            Assert.IsNotNull(catalog);
            Assert.AreEqual(15, catalog.modules.Count);
            foreach (var entry in catalog.modules)
            {
                Assert.IsNotNull(entry.prefab, entry.moduleId);
                Assert.IsNotEmpty(entry.prefab.GetComponentsInChildren<BoxCollider>(true), entry.moduleId);
                Assert.IsNotNull(entry.prefab.intactVisual, entry.moduleId);
            }
        }

        /// <summary>Godot <c>v.rotated(Vector3.UP, angle)</c> for a unit Y axis (right-handed).</summary>
        static Vec3 GodotRotatedAboutUp(Vec3 v, int yawDegrees)
        {
            double a = yawDegrees * Math.PI / 180.0;
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Vec3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
        }
    }
}
