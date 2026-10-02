using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    public class LootContainerFocusGeometryTests
    {
        IResourceReader _previousResources;
        GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _previousResources = CoreServices.Resources;
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
            _root = new GameObject("LootContainerFocusGeometryTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            GameplayPropFactory.ClearCache();
            CatalogRegistry.Clear();
            CoreServices.Resources = _previousResources;
        }

        static LootContainer EmptyLoot(Vec3 root, InventoryState inventory = null, string id = "synthetic_empty")
        {
            var model = new LootContainer();
            model.Configure(id, "", "diagnostic_empty", inventory ?? new InventoryState(), new GdDict(), root,
                lootContext: new GdDict { { "contents", new GdArray() } });
            return model;
        }

        [TestCase("synthetic_empty")]
        [TestCase("corpse_synthetic_empty")]
        public void LootFocusLeavesEveryTransformRendererAndSensorInvariant(string id)
        {
            var view = InteractableView.Create(EmptyLoot(new Vec3(2, 3, 4), id: id), _root.transform, null);
            Physics.SyncTransforms();
            var transforms = view.GetComponentsInChildren<Transform>(true);
            var positions = transforms.Select(t => t.position).ToArray();
            var rotations = transforms.Select(t => t.rotation).ToArray();
            var scales = transforms.Select(t => t.lossyScale).ToArray();
            var renderers = view.GetComponentsInChildren<Renderer>(true);
            var bounds = renderers.Select(r => r.bounds).ToArray();
            var colliders = view.GetComponentsInChildren<Collider>(true);
            Assert.That(renderers.Length, Is.GreaterThan(0), "real production prop renderer");
            Assert.That(colliders.Length, Is.EqualTo(1), "existing loot geometry has only the sensor");
            var sensor = (SphereCollider)colliders[0];
            Assert.That(sensor.isTrigger, Is.True);
            Assert.That(sensor.radius, Is.EqualTo(1.8f));
            Vector3 sensorCenter = sensor.transform.TransformPoint(sensor.center);
            Bounds sensorBounds = sensor.bounds;
            foreach (bool focus in new[] { true, true, false, false, true, false })
            {
                view.SetFocused(focus);
                Physics.SyncTransforms();
                Assert.That(view.Focused, Is.EqualTo(focus));
                for (int i = 0; i < transforms.Length; i++)
                {
                    Assert.That(transforms[i].position, Is.EqualTo(positions[i]), transforms[i].name);
                    Assert.That(transforms[i].rotation, Is.EqualTo(rotations[i]), transforms[i].name);
                    Assert.That(transforms[i].lossyScale, Is.EqualTo(scales[i]), transforms[i].name);
                }
                for (int i = 0; i < renderers.Length; i++) Assert.That(renderers[i].bounds, Is.EqualTo(bounds[i]), renderers[i].name);
                Assert.That(sensor.transform.TransformPoint(sensor.center), Is.EqualTo(sensorCenter));
                Assert.That(sensor.bounds, Is.EqualTo(sensorBounds));
                Assert.That(sensor.radius, Is.EqualTo(1.8f));
                Assert.That(sensor.enabled, Is.True);
                Assert.That(view.PromptText, Is.EqualTo("Search"));
            }
        }

        [Test]
        public void GroundedCrateKeepsFloorContactThroughFocusWithoutMovingItsRoot()
        {
            const string floorPath = "Assets/Content/Prefabs/Structural/ship_structural_v0/floor_1x1.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(floorPath);
            Assert.That(prefab, Is.Not.Null, "actual structural floor prefab");
            var floor = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _root.transform);
            floor.transform.position = new Vector3(-20, 4, 4);
            var floorCollider = floor.GetComponentsInChildren<BoxCollider>(true)
                .Single(c => !c.isTrigger && c.size == new Vector3(4f, 0.25f, 4f));
            Physics.SyncTransforms();
            float top = floorCollider.bounds.max.y;
            Assert.That(top, Is.EqualTo(4.125f).Within(0.00001f));
            // Synthetic isolated test geometry, not an authored kit/source placement.
            Vec3 root = new Vec3(19.2f, top, 4f);
            var view = InteractableView.Create(EmptyLoot(root), _root.transform, null);
            var renderer = view.GetComponentsInChildren<Renderer>(true).Single();
            foreach (bool focus in new[] { false, true, false, true })
            {
                view.SetFocused(focus);
                Physics.SyncTransforms();
                Assert.That(renderer.bounds.min.y, Is.EqualTo(top).Within(0.00001f), "true floor contact in every focus state");
                Assert.That(view.transform.position, Is.EqualTo(Frame.ToUnity(root)));
                Assert.That(Vector3.Distance(renderer.bounds.size, new Vector3(0.81f, 0.9f, 0.675f)), Is.LessThan(0.00001f));
            }
        }

        [Test]
        public void RealSensorSelectionKeepsSearchPromptAndOrdinaryEmptyDispatch()
        {
            var inventory = new InventoryState();
            string inventoryBefore = GdJson.Stringify(inventory.GetSummary());
            var model = EmptyLoot(new Vec3(0.5, 0, 0), inventory);
            var session = new RunSession(new RunSessionDeps());
            session.LootContainers.Add(model);
            var sensorObject = new GameObject("ActualProximitySensor") { layer = PhysicsLayers.Player };
            sensorObject.transform.SetParent(_root.transform, false);
            sensorObject.transform.position = new Vector3(0, 0.8f, 0);
            var sphere = sensorObject.AddComponent<SphereCollider>(); sphere.isTrigger = true; sphere.radius = 0.35f;
            var sensor = sensorObject.AddComponent<ProximitySensor>();
            var view = InteractableView.Create(model, _root.transform, sensor);
            sensor.Refresh();
            Assert.That(view.PlayerOverlap, Is.True, "actual production physics overlap query");
            Assert.That(model.CandidatePlayerInRange, Is.True);
            var selected = InteractableView.PickFocus(sensor.Overlapping, Vector3.zero, session.CanFocusInteractable);
            Assert.That(selected, Is.SameAs(view));
            selected.SetFocused(true);
            Assert.That(selected.PromptText, Is.EqualTo("Search"));
            Assert.That(selected.HandlerId, Is.EqualTo("loot_container"));
            Assert.That(session.RequestInteract(), Is.EqualTo(selected.HandlerId));
            Assert.That(model.Searched, Is.True);
            Assert.That(session.CanFocusInteractable(model), Is.False);
            Assert.That(GdJson.Stringify(inventory.GetSummary()), Is.EqualTo(inventoryBefore), "empty fixture grants nothing");
            Assert.That(view.GetComponent<SphereCollider>().enabled, Is.False);
        }

        [Test]
        public void NonLootFocusRetainsExistingMarkerScaleBehavior()
        {
            var model = new DeckTransition { NodeName = "FocusControl", LocalPosition = new Vec3(2, 3, 4) };
            var view = InteractableView.Create(model, _root.transform, null);
            var renderer = view.GetComponentsInChildren<Renderer>(true).Single();
            Bounds before = renderer.bounds;
            Vector3 root = view.transform.position;
            view.SetFocused(true);
            Assert.That(Vector3.Distance(renderer.bounds.size, before.size * 1.15f), Is.LessThan(0.00001f));
            Assert.That(view.transform.position, Is.EqualTo(root));
            view.SetFocused(false);
            Assert.That(renderer.bounds, Is.EqualTo(before));
        }
    }
}
