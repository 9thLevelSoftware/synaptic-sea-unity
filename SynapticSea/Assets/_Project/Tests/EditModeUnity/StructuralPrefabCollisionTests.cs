using System.Linq;
using NUnit.Framework;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.Tests.Unity
{
    /// <summary>
    /// The ramp's collision is derived from its imported treads instead of Godot's 1 m placeholder cube
    /// (the Editor StructuralPrefabBuilder collision override; port-status decision 9 amendment).
    /// </summary>
    public class StructuralPrefabCollisionTests
    {
        const string Kit = "ship_structural_v0";

        [Test]
        public void RampPrefabHasASlopedColliderMatchingItsTreads()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>($"Assets/Resources/Catalogs/KitCatalog_{Kit}.asset");
            Assert.IsNotNull(catalog);
            StructuralModule prefab = catalog.modules.Where(e => e.moduleId == "ramp_up_1x2").Select(e => e.prefab).FirstOrDefault();
            Assert.IsNotNull(prefab, "ramp_up_1x2 prefab");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            try
            {
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                BoxCollider[] boxes = go.GetComponentsInChildren<BoxCollider>(true);
                Assert.IsFalse(boxes.Any(b => b.size == Vector3.one && b.center == Vector3.zero && b.transform.localRotation == Quaternion.identity),
                    "the Godot 1 m placeholder cube is gone");
                Transform slopeT = go.transform.Find("CollisionRoot/RampSlope");
                Assert.IsNotNull(slopeT, "CollisionRoot/RampSlope");
                var slope = slopeT.GetComponent<BoxCollider>();
                Assert.IsNotNull(slope);
                Assert.AreEqual(7, slope.gameObject.layer, "Structure layer");

                var liveTreads = go.GetComponent<StructuralModule>().intactVisual.GetComponentsInChildren<Renderer>(true)
                    .Where(r => r.enabled && r.name.Contains("_tread_")).Select(r => r.bounds).OrderBy(b => b.max.y).ToList();
                Assert.GreaterOrEqual(liveTreads.Count, 2, "tread meshes");
                Bounds low = liveTreads.First(), high = liveTreads.Last();

                float angle = Vector3.Angle(slopeT.up, Vector3.up);
                Assert.That(angle, Is.InRange(3f, 12f), "a gentle slope");
                Assert.AreEqual(low.max.y, TopHeightAt(slope, low.center), 0.05f, "the slope top meets the lowest tread");
                Assert.AreEqual(high.max.y, TopHeightAt(slope, high.center), 0.05f, "the slope top meets the highest tread");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Height of the box's top face above (x, z) of <paramref name="at"/>.</summary>
        static float TopHeightAt(BoxCollider box, Vector3 at)
        {
            Transform t = box.transform;
            Vector3 top = t.TransformPoint(box.center + Vector3.up * (box.size.y * 0.5f));
            Vector3 n = t.up;
            return top.y - (n.x * (at.x - top.x) + n.z * (at.z - top.z)) / n.y;
        }
    }
}
