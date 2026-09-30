using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.EditorTools.Content;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    public class PurchasedMegaKitTests
    {
        [Test]
        public void ExistingCustomCorridorBindingCoversItsEightMetreCollisionDeck()
        {
            var original = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>("Assets/Resources/Catalogs/KitCatalog_ship_structural_v0.asset");
            var prefab = original.modules.First(e => e.moduleId == "corridor_floor_1x2").prefab;
            var instance = Object.Instantiate(prefab);
            try
            {
                var renderers = instance.intactVisual.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToArray();
                Bounds bounds = renderers[0].bounds;
                foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                Assert.AreEqual(4f, bounds.size.x, 0.2f);
                Assert.AreEqual(8f, bounds.size.z, 0.2f);
                Assert.AreEqual(instance.GetComponentsInChildren<BoxCollider>(true).Max(c => c.bounds.max.y), bounds.max.y, 0.01f);
            }
            finally { Object.DestroyImmediate(instance.gameObject); }
        }

        static KitPrefabCatalog Local()
        {
            var local = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>(PurchasedMegaKitBuilder.CatalogPath);
            if (local == null) Assert.Ignore("Local licensed floor overlay is absent; import and bake it to exercise this suite.");
            return local;
        }

        [TestCase("floor_1x1")]
        [TestCase("floor_2x1")]
        [TestCase("corridor_floor_1x1")]
        [TestCase("corridor_floor_1x2")]
        public void FloorAdapterPreservesSocketsAndCollisionAndMatchesVisibleDeck(string id)
        {
            var local = Local();
            var original = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>("Assets/Resources/Catalogs/KitCatalog_ship_structural_v0.asset");
            var source = original.modules.First(e => e.moduleId == id).prefab;
            var target = local.modules.First(e => e.moduleId == id).prefab;
            Assert.AreEqual(source.footprintCells, target.footprintCells);
            var originalBoxes = source.GetComponentsInChildren<BoxCollider>(true);
            var adaptedBoxes = target.GetComponentsInChildren<BoxCollider>(true);
            Assert.AreEqual(originalBoxes.Length, adaptedBoxes.Length);
            for (int i = 0; i < originalBoxes.Length; i++)
            {
                Assert.AreEqual(originalBoxes[i].center, adaptedBoxes[i].center);
                Assert.AreEqual(originalBoxes[i].size, adaptedBoxes[i].size);
                Assert.AreEqual(originalBoxes[i].transform.localPosition, adaptedBoxes[i].transform.localPosition);
            }
            Assert.AreEqual(source.transform.Find("Sockets").childCount, target.transform.Find("Sockets").childCount);
            var instance = Object.Instantiate(target);
            try
            {
                var renderers = instance.intactVisual.GetComponentsInChildren<Renderer>(true);
                Bounds bounds = renderers[0].bounds;
                foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                Assert.AreEqual(target.footprintCells.x * 4f, bounds.size.x, 0.2f);
                Assert.AreEqual(target.footprintCells.y * 4f, bounds.size.z, 0.2f);
                Assert.AreEqual(instance.GetComponentsInChildren<BoxCollider>(true).Max(c => c.bounds.max.y), bounds.max.y, 0.01f);
                Assert.IsTrue(renderers.All(r => r.sharedMaterials.All(m => m != null)), "floor materials resolve");
            }
            finally { Object.DestroyImmediate(instance.gameObject); }
        }

        [TestCase(17)] [TestCase(42)] [TestCase(73)]
        public void GeneratedSeedUsesPurchasedFloorsWithoutChangingLayoutOrConnectivity(int seed)
        {
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CatalogRegistry.Clear();
            var local = Local();
            var generator = new ShipGenerator();
            var first = generator.GenerateFromSeed(seed, 1, 1);
            var second = generator.GenerateFromSeed(seed, 1, 1);
            Assert.IsNotNull(first);
            Assert.IsTrue(V.VariantEquals(first.Layout, second.Layout), "seed determinism");
            var graph = new ShipNavGraph();
            Assert.Greater(graph.BuildFromLayout(first.Layout), 0);
            var root = new GameObject("PurchasedSeedTest");
            try
            {
                var builder = ShipSceneBuilder.Create(root.transform);
                builder.KitCatalog = local;
                Assert.IsTrue(builder.LoadFromDocuments(first.Layout, first.Kit, first.GameplaySlice, first.IsAway));
                var modules = root.GetComponentsInChildren<StructuralModule>(true);
                Assert.IsTrue(modules.Any(m => m.transform.Find("Visual/PurchasedTile_0_0") != null));
                var again = new ShipNavGraph();
                again.BuildFromLayout(first.Layout);
                Assert.IsTrue(V.VariantEquals(graph.Nodes, again.Nodes));
                Assert.IsTrue(V.VariantEquals(graph.Edges, again.Edges), "visual overlay never writes the walkable graph");
            }
            finally { Object.DestroyImmediate(root); CatalogRegistry.Clear(); }
        }
    }
}
