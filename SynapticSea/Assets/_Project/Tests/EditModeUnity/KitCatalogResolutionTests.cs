using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    /// <summary>
    /// A4: the prefab catalog comes from the kit document Godot loads (<c>kit_path_for_layout</c>), keyed by its
    /// wrapper-scene folder, so derelicts from every structural kit id build.
    /// </summary>
    public class KitCatalogResolutionTests
    {
        GameObject _parent;

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
            _parent = new GameObject("KitCatalogResolutionTests");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_parent);
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
        }

        [TestCase("ship_structural_v0", "res://data/kits/ship_structural_v0.json")]
        [TestCase("ship_structural_biomatter", "res://data/kits/ship_structural_biomatter.json")]
        [TestCase("ship_structural_hazard", "res://data/kits/ship_structural_v0.json")]
        [TestCase("ship_structural_industrial", "res://data/kits/ship_structural_v0.json")]
        public void KitPathAndCatalogFollowGodotRule(string kitId, string expectedPath)
        {
            var layout = new GdDict { { "kit_id", kitId } };
            Assert.AreEqual(expectedPath, KitCatalogResolver.KitPathForLayout(layout));
            KitPrefabCatalog catalog = KitCatalogResolver.ForLayout(layout);
            Assert.IsNotNull(catalog, kitId);
            Assert.AreEqual("ship_structural_v0", catalog.kitId);
            // The raw document (even one without a wrapper map) resolves to the v0 prefabs too.
            Assert.AreSame(catalog, KitCatalogResolver.ForKitDocument(CatalogRegistry.LoadDict("res://data/kits/" + kitId + ".json")));
        }

        [TestCase("")]
        [TestCase("breach_field")]
        [TestCase("dead_fleet")]
        public void LifeboatBuildsFromItsKitPath(string biome)
        {
            LifeBoatBuilder.BuildResult lifeboat = LifeBoatBuilder.Build(biome);
            Assert.IsNotNull(lifeboat, biome);
            KitPrefabCatalog catalog = KitCatalogResolver.ForKitPath(lifeboat.KitPath);
            Assert.IsNotNull(catalog, lifeboat.KitPath);
            Assert.IsNotNull(new StructuralLayoutBuilder().Build(lifeboat.Layout, catalog, _parent.transform), biome);
        }

        [Test]
        public void WrapperFolderParsesSceneDirectory()
        {
            Assert.AreEqual("ship_structural_v0", KitCatalogResolver.WrapperFolder("res://scenes/wrappers/structural/ship_structural_v0/floor_1x1.tscn"));
            Assert.AreEqual("ithappy", KitCatalogResolver.WrapperFolder("res://scenes/wrappers/structural/ithappy/floor_1x1.tscn"));
            Assert.AreEqual("", KitCatalogResolver.WrapperFolder("floor.tscn"));
            Assert.AreEqual("ship_structural_v0", KitCatalogResolver.CatalogIdForKitDocument(new GdDict()));
        }

        [Test]
        public void IthappyKitLoadsItsOwnPrefabCatalog()
        {
            var layout = new GdDict { { "kit_id", KitCatalog.ITHAPPY_KIT_ID } };
            Assert.AreEqual("res://data/kits/ithappy_scifi_v0.json", KitCatalogResolver.KitPathForLayout(layout));
            GdDict kit = CatalogRegistry.LoadDict("res://data/kits/ithappy_scifi_v0.json");
            Assert.AreEqual("ithappy", KitCatalogResolver.CatalogIdForKitDocument(kit));
            KitPrefabCatalog catalog = KitCatalogResolver.ForKitDocument(kit);
            Assert.IsNotNull(catalog);
            Assert.AreEqual(KitCatalog.ITHAPPY_KIT_ID, catalog.kitId);
            Assert.AreSame(catalog, KitCatalogResolver.ForLayout(layout));
        }

        static IEnumerable<TestCaseData> Kits()
        {
            yield return new TestCaseData("ship_structural_v0", "");
            yield return new TestCaseData("ship_structural_biomatter", "breach_field");
            yield return new TestCaseData("ship_structural_hazard", "breach_field");
            yield return new TestCaseData("ship_structural_industrial", "dead_fleet");
        }

        [TestCaseSource(nameof(Kits))]
        public void GeneratedDerelictBuildsForEveryKitId(string kitId, string biome)
        {
            ShipDocuments docs = null;
            for (long seed = 1; seed <= 400 && docs == null; seed++)
            {
                var gen = new ShipGenerator();
                gen.ConfigureRunContext(biome, biome.Length == 0 ? "" : "standard");
                ShipDocuments candidate = gen.GenerateFromSeed(seed, 1, 1);
                if (candidate != null && candidate.Layout.GetString("kit_id") == kitId) docs = candidate;
            }
            Assert.IsNotNull(docs, "no seed in 1..400 generated a layout with kit_id " + kitId);

            Assert.AreSame(KitCatalogResolver.ForKitDocument(docs.Kit), KitCatalogResolver.ForKitPath(docs.KitPath), docs.KitPath);
            var builder = ShipSceneBuilder.Create(_parent.transform);
            builder.KitPath = docs.KitPath;
            string failure = "";
            builder.LoadFailed += r => failure = r;
            Assert.IsTrue(builder.LoadFromDocuments(docs.Layout, docs.Kit, docs.GameplaySlice, docs.IsAway), kitId + ": " + failure);
            Assert.That(builder.View.StructuralRoot.childCount, Is.GreaterThan(0));
        }
    }
}
