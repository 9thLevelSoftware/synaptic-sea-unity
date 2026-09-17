using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.Tests.Unity
{
    /// <summary>
    /// Godot's legacy per-state albedo tint on single-visual structural wrappers (integrity_visual_resolver.gd
    /// <c>_apply_legacy_tint</c>, colours from module_integrity_consequences.gd), applied through material property blocks.
    /// </summary>
    public class StructuralModuleTintTests
    {
        const string Kit = "ship_structural_v0";
        readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
                if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
        }

        static KitPrefabCatalog Catalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>($"Assets/Resources/Catalogs/KitCatalog_{Kit}.asset");
            Assert.IsNotNull(catalog);
            return catalog;
        }

        StructuralModule Instantiate(StructuralModule prefab)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            _objects.Add(go);
            return go.GetComponent<StructuralModule>();
        }

        static (Renderer renderer, int index, Material material, int property) FirstTintable(StructuralModule module)
        {
            foreach (Renderer r in module.intactVisual.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = r.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    int id = StructuralModule.TintPropertyFor(materials[i]);
                    if (id != 0) return (r, i, materials[i], id);
                }
            }
            return (null, -1, null, 0);
        }

        [Test]
        public void SingleVisualWrapperTakesTheLegacyTintAndRepairClearsIt()
        {
            StructuralModule prefab = Catalog().modules.Select(e => e.prefab).FirstOrDefault(p => p != null && p.HasSingleVisual);
            Assert.IsNotNull(prefab, "the kit has single-visual wrappers (corners, T-junction, end cap, ceiling, bulkhead)");
            StructuralModule module = Instantiate(prefab);
            var (renderer, index, material, property) = FirstTintable(module);
            Assert.IsNotNull(renderer, module.moduleId + " has a material with a base colour property");
            Color original = material.GetColor(property);
            var block = new MaterialPropertyBlock();

            module.SetIntegrity(StructuralModule.IntegrityDamaged);
            Assert.IsTrue(module.intactVisual.activeSelf);
            renderer.GetPropertyBlock(block, index);
            Color damaged = new Color(0.85f, 0.70f, 0.55f, 1f);
            AssertColor(original * damaged, block.GetColor(property), "damaged tint");
            AssertColor(damaged, module.LegacyTint, "reported tint");
            Assert.AreEqual(original, material.GetColor(property), "the shared material is untouched");

            module.SetIntegrity(StructuralModule.IntegrityBreached);
            renderer.GetPropertyBlock(block, index);
            AssertColor(original * new Color(0.55f, 0.60f, 0.75f, 1f), block.GetColor(property), "breached tint");

            module.SetIntegrity(StructuralModule.IntegrityIntact);
            renderer.GetPropertyBlock(block, index);
            Assert.IsTrue(block.isEmpty, "a repair back to intact clears the tint");
            Assert.AreEqual(Color.white, module.LegacyTint);

            module.SetIntegrity(StructuralModule.IntegrityDestroyed);
            Assert.IsFalse(module.intactVisual.activeSelf, "destroyed hides the single visual");
        }

        [Test]
        public void VariantWrappersAreNeverTinted()
        {
            StructuralModule prefab = Catalog().modules.Select(e => e.prefab).FirstOrDefault(p => p != null && !p.HasSingleVisual && p.damagedVisual != null && p.damagedVisual != p.intactVisual);
            Assert.IsNotNull(prefab, "the kit has variant wrappers");
            StructuralModule module = Instantiate(prefab);
            module.SetIntegrity(StructuralModule.IntegrityDamaged);
            module.ApplyLegacyTint(new Color(0.85f, 0.70f, 0.55f, 1f));
            Assert.AreEqual(Color.white, module.LegacyTint);
            foreach (Renderer r in module.GetComponentsInChildren<Renderer>(true))
                Assert.IsFalse(r.HasPropertyBlock(), module.moduleId + "/" + r.name + " carries no tint block");
        }

        static void AssertColor(Color expected, Color actual, string what)
        {
            Assert.AreEqual(expected.r, actual.r, 1e-3f, what + " r");
            Assert.AreEqual(expected.g, actual.g, 1e-3f, what + " g");
            Assert.AreEqual(expected.b, actual.b, 1e-3f, what + " b");
            Assert.AreEqual(expected.a, actual.a, 1e-3f, what + " a");
        }
    }
}
