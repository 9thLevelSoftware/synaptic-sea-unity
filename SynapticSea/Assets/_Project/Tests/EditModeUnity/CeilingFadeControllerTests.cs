using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Runtime;
using UnityEngine;

namespace SynapticSea.Tests.Unity
{
    /// <summary>Port of ceiling_fade_smoke.gd's rule: ceilings within 12 m of the player at 1.0, the rest at 0.15.</summary>
    public class CeilingFadeControllerTests
    {
        readonly List<GameObject> _objects = new List<GameObject>();
        Material _source;

        [SetUp]
        public void SetUp()
        {
            _source = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _source.SetColor("_BaseColor", new Color(0.2f, 0.4f, 0.6f, 1f));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) Object.DestroyImmediate(go);
            _objects.Clear();
            Object.DestroyImmediate(_source);
        }

        StructuralModule Ceiling(string name, Vector3 position)
        {
            var root = new GameObject(name);
            _objects.Add(root);
            root.transform.position = position;
            var module = root.AddComponent<StructuralModule>();
            module.layer = "ceiling";
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.SetParent(root.transform, false);
            visual.GetComponent<MeshRenderer>().sharedMaterial = _source;
            return module;
        }

        [Test]
        public void NearCeilingsStayOpaqueAndFarOnesDither()
        {
            var player = new GameObject("Player").transform;
            _objects.Add(player.gameObject);
            var near = Ceiling("Ceiling_near", new Vector3(5f, 3f, 0f));
            var edge = Ceiling("Ceiling_edge", new Vector3(0f, 0f, 12f)); // exactly the radius: near (d <= fade_radius_m)
            var far = Ceiling("Ceiling_far", new Vector3(-20f, 3f, 4f));
            far.gameObject.SetActive(false); // Godot sets visible = true every frame

            var controller = new GameObject("CeilingFadeController").AddComponent<CeilingFadeController>();
            _objects.Add(controller.gameObject);
            controller.Configure(new[] { near, edge, far }, player);

            Assert.AreEqual(3, controller.CeilingCount);
            Assert.AreEqual(1f, controller.GetAppliedAlpha(near.transform));
            Assert.AreEqual(1f, controller.GetAppliedAlpha(edge.transform));
            Assert.AreEqual(CeilingFadeController.DefaultFadeAlpha, controller.GetAppliedAlpha(far.transform));
            Assert.IsTrue(far.gameObject.activeSelf);

            var nearMat = near.GetComponentInChildren<MeshRenderer>().sharedMaterial;
            var farMat = far.GetComponentInChildren<MeshRenderer>().sharedMaterial;
            Assert.AreEqual(CeilingFadeController.ShaderName, nearMat.shader.name);
            Assert.AreEqual(1f, nearMat.GetFloat("_Fade"));
            Assert.AreEqual(0.15f, farMat.GetFloat("_Fade"), 1e-6f);
            Assert.That(Vector4.Distance(_source.GetColor("_BaseColor"), farMat.GetColor("_BaseColor")), Is.LessThan(1e-5f), "base colour preserved");
            Assert.AreSame(farMat, CeilingFadeController.GetFadeMaterial(_source, 0.15f), "fade materials are shared");

            // Walk to the far ceiling: the rule flips.
            player.position = far.transform.position;
            controller.Apply();
            Assert.AreEqual(1f, controller.GetAppliedAlpha(far.transform));
            Assert.AreEqual(CeilingFadeController.DefaultFadeAlpha, controller.GetAppliedAlpha(near.transform));

            controller.RestoreOriginalMaterials();
            Assert.AreSame(_source, near.GetComponentInChildren<MeshRenderer>().sharedMaterial);
        }

        [Test]
        public void SingleVisualWrappersStayVisibleForEveryIntegrityState()
        {
            var root = new GameObject("Wall_T_Junction");
            _objects.Add(root);
            var visual = new GameObject("Intact");
            visual.transform.SetParent(root.transform, false);
            var module = root.AddComponent<StructuralModule>();
            module.intactVisual = module.damagedVisual = module.breachedVisual = visual;

            Assert.IsTrue(module.HasSingleVisual);
            foreach (var state in new[] { "intact", "damaged", "breached" })
            {
                module.SetIntegrity(state);
                Assert.IsTrue(visual.activeSelf, state);
            }
            module.SetIntegrity(StructuralModule.IntegrityDestroyed);
            Assert.IsFalse(visual.activeSelf);
        }
    }
}
