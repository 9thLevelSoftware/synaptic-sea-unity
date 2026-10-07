using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Runtime;
using UnityEngine;

namespace SynapticSea.Tests.Unity
{
    public class InteriorOcclusionTests
    {
        readonly List<GameObject> _objects = new List<GameObject>();
        InteriorOcclusion _occlusion;
        Camera _camera;

        [SetUp] public void Setup()
        {
            _occlusion = new InteriorOcclusion();
            var go = new GameObject("OcclusionCamera"); _objects.Add(go);
            _camera = go.AddComponent<Camera>(); _camera.orthographic = true; _camera.orthographicSize = 7;
            _camera.transform.position = new Vector3(0, 3, -8); _camera.transform.LookAt(Vector3.zero);
        }
        [TearDown] public void Cleanup()
        {
            _occlusion.RestoreAll();
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
        }
        Renderer Wall(float z, float x = 0)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); _objects.Add(go);
            go.name = "Wall"; go.layer = PhysicsLayers.Structure;
            go.transform.position = new Vector3(x, 1.5f, z); go.transform.localScale = new Vector3(4, 3, 0.2f);
            go.AddComponent<StructuralModule>().layer = "edge";
            var renderer = go.GetComponent<Renderer>(); renderer.sharedMaterial = RuntimeVisualCatalog.Material(Color.gray);
            Physics.SyncTransforms(); return renderer;
        }
        void Reveal(Vector3? player = null, Vector3? focus = null)
        {
            Physics.SyncTransforms();
            _occlusion.Update(_camera, player ?? Vector3.zero, focus, 0.1f);
        }

        [Test] public void EveryBlockerFadesWithoutChangingPhysicsOrSharedMaterialsAndRestoresExactly()
        {
            var first = Wall(-2); var second = Wall(-4);
            var original = first.sharedMaterial;
            var block = new MaterialPropertyBlock(); block.SetFloat("_Metallic", 0.23f); first.SetPropertyBlock(block);
            Reveal(); Reveal();
            Assert.AreEqual(2, _occlusion.ActiveRendererCount);
            Assert.AreNotSame(original, first.sharedMaterial);
            Assert.IsTrue(first.sharedMaterial.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"));
            Assert.IsTrue(first.GetComponent<Collider>().enabled);
            Assert.IsFalse(original.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"));
            var clone = first.sharedMaterial;
            _occlusion.RestoreAll();
            Assert.AreSame(original, first.sharedMaterial); Assert.AreSame(original, second.sharedMaterial);
            Assert.IsFalse(first.forceRenderingOff); Assert.IsTrue(clone == null, "temporary material destroyed");
            first.GetPropertyBlock(block); Assert.AreEqual(0.23f, block.GetFloat("_Metallic"));
        }

        [Test] public void FocusRayUsesOrthographicProjectionAndRevealReleasesAfterMovement()
        {
            var wall = Wall(-2, 5);
            Reveal(focus: new Vector3(5, 0, 0));
            Assert.Greater(_occlusion.ActiveRendererCount, 0, "off-axis target ray must be parallel to camera direction");
            for (int i = 0; i < 8; i++) Reveal();
            Assert.AreEqual(0, _occlusion.ActiveRendererCount);
            Assert.IsFalse(wall.forceRenderingOff);
        }

        [Test] public void CompoundDockingCollisionDoesNotNeedVisualModuleOwnership()
        {
            var wall = Wall(-2); wall.GetComponent<Collider>().enabled = false;
            var union = GameObject.CreatePrimitive(PrimitiveType.Cube); _objects.Add(union);
            union.name = "DockedCollisionUnion"; union.layer = PhysicsLayers.Structure;
            union.transform.position = wall.transform.position; union.transform.localScale = wall.transform.localScale;
            Object.DestroyImmediate(union.GetComponent<Renderer>());
            Reveal(); Assert.Greater(_occlusion.ActiveRendererCount, 0, "visual bounds work after docking suppresses wrapper colliders");
            Assert.IsFalse(wall.GetComponent<Collider>().enabled); Assert.IsTrue(union.GetComponent<Collider>().enabled);
        }

        [Test] public void DisableAndPoolReuseRestoreOriginalStateAndZoomRecomputesBlockers()
        {
            var wall = Wall(-2); var original = wall.sharedMaterial;
            Reveal(); wall.gameObject.SetActive(false); Reveal();
            Assert.AreEqual(0, _occlusion.ActiveRendererCount); Assert.AreSame(original, wall.sharedMaterial);
            wall.gameObject.SetActive(true); _camera.orthographicSize = 4; Reveal();
            Assert.Greater(_occlusion.ActiveRendererCount, 0);
            _camera.orthographicSize = 11; Reveal(); Assert.Greater(_occlusion.ActiveRendererCount, 0);
            _occlusion.Update(_camera, null, null, 0.1f); Assert.AreSame(original, wall.sharedMaterial);
        }

        [Test] public void UnsupportedMaterialUsesHideAndFootprintWithoutMutatingItsShader()
        {
            var wall = Wall(-2);
            var source = new Material(wall.sharedMaterial); source.EnableKeyword("_NORMALMAP"); wall.sharedMaterial = source;
            try
            {
                Reveal(); Assert.IsTrue(wall.forceRenderingOff); Assert.AreSame(source, wall.sharedMaterial);
                Assert.IsTrue(source.IsKeywordEnabled("_NORMALMAP")); Assert.IsTrue(wall.GetComponent<Collider>().enabled);
                _occlusion.RestoreAll(); Assert.IsFalse(wall.forceRenderingOff); Assert.AreSame(source, wall.sharedMaterial);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test] public void HiddenForegroundWallDoesNotRevealEnemyBehindIt()
        {
            Wall(-2); Reveal();
            var enemy = GameObject.CreatePrimitive(PrimitiveType.Capsule); _objects.Add(enemy);
            enemy.transform.position = new Vector3(0, 0, -4); Physics.SyncTransforms();
            _occlusion.UpdateThreatVisibility(new[] { enemy }, Vector3.zero);
            Assert.IsTrue(enemy.GetComponent<Renderer>().forceRenderingOff);
            enemy.transform.position = new Vector3(0, 0, 1); Physics.SyncTransforms();
            _occlusion.UpdateThreatVisibility(new[] { enemy }, Vector3.zero);
            Assert.IsFalse(enemy.GetComponent<Renderer>().forceRenderingOff);
            _occlusion.RestoreAll(); Assert.IsFalse(enemy.GetComponent<Renderer>().forceRenderingOff);
        }

        [Test] public void VisualBoundsRevealAllSiblingPanelsAndTrimsEvenWhenOnlyOneIntersects()
        {
            var wall = Wall(-2); wall.GetComponent<Collider>().enabled = false;
            var trim = GameObject.CreatePrimitive(PrimitiveType.Cube); trim.transform.SetParent(wall.transform, false);
            trim.transform.localPosition = new Vector3(0.45f, 0.5f, 0); trim.transform.localScale = Vector3.one * 0.05f;
            trim.layer = PhysicsLayers.Structure; var renderer = trim.GetComponent<Renderer>();
            renderer.sharedMaterial = RuntimeVisualCatalog.Material(Color.white); var original = renderer.sharedMaterial;
            Reveal(); Assert.GreaterOrEqual(_occlusion.ActiveRendererCount, 2);
            Assert.AreNotSame(original, renderer.sharedMaterial, "sibling trim follows the logical wall even outside sample rays");
            _occlusion.RestoreAll(); Assert.AreSame(original, renderer.sharedMaterial);
        }

        [Test] public void RendererDisabledDuringCutawayRestoresImmediatelyWithoutBeingEnabled()
        {
            var wall = Wall(-2); var original = wall.sharedMaterial;
            Reveal(); Assert.AreNotSame(original, wall.sharedMaterial);
            wall.enabled = false; Reveal();
            Assert.AreEqual(0, _occlusion.ActiveRendererCount);
            Assert.AreSame(original, wall.sharedMaterial);
            Assert.IsFalse(wall.enabled);
            Assert.IsFalse(wall.forceRenderingOff);
            Assert.IsTrue(wall.GetComponent<Collider>().enabled);
        }

        [Test] public void FullyRevealedWallHidesChildTrimAndRestoresEveryOriginalVisibilityState()
        {
            var wall = Wall(-2);
            var trim = GameObject.CreatePrimitive(PrimitiveType.Cube);
            trim.transform.SetParent(wall.transform, false);
            trim.transform.localPosition = new Vector3(0.45f, 0.5f, 0);
            trim.transform.localScale = Vector3.one * 0.05f;
            trim.layer = PhysicsLayers.Structure;
            var child = trim.GetComponent<Renderer>(); child.sharedMaterial = RuntimeVisualCatalog.Material(Color.white);
            var originalWall = wall.sharedMaterial; var originalChild = child.sharedMaterial;
            var neighbor = Wall(-2, 5); var originalNeighbor = neighbor.sharedMaterial;
            var block = new MaterialPropertyBlock(); block.SetFloat("_Metallic", 0.37f); child.SetPropertyBlock(block, 0);
            var hidden = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hidden.transform.SetParent(wall.transform, false); hidden.layer = PhysicsLayers.Structure;
            var hiddenRenderer = hidden.GetComponent<Renderer>(); hiddenRenderer.sharedMaterial = originalChild;
            hiddenRenderer.forceRenderingOff = true;
            var disabled = GameObject.CreatePrimitive(PrimitiveType.Cube);
            disabled.transform.SetParent(wall.transform, false); disabled.layer = PhysicsLayers.Structure;
            var disabledRenderer = disabled.GetComponent<Renderer>(); disabledRenderer.sharedMaterial = originalChild;
            disabledRenderer.enabled = false;
            Reveal(); Reveal();
            Assert.IsTrue(wall.forceRenderingOff, "fully faded panel is removed independent of shader alpha support");
            Assert.IsTrue(child.forceRenderingOff, "off-ray child trim follows its wall owner");
            Assert.IsTrue(hiddenRenderer.forceRenderingOff); Assert.IsFalse(disabledRenderer.enabled);
            Assert.IsFalse(neighbor.forceRenderingOff); Assert.AreSame(originalNeighbor, neighbor.sharedMaterial,
                "neighboring module outside reveal rays must remain unchanged");
            Assert.IsTrue(wall.GetComponent<Collider>().enabled);
            Assert.IsTrue(trim.GetComponent<Collider>().enabled);
            _occlusion.RestoreAll();
            Assert.IsFalse(wall.forceRenderingOff); Assert.IsFalse(child.forceRenderingOff);
            Assert.IsTrue(hiddenRenderer.forceRenderingOff, "original hidden owner state must remain hidden");
            Assert.IsFalse(disabledRenderer.enabled, "original disabled renderer must remain disabled");
            Assert.AreSame(originalChild, hiddenRenderer.sharedMaterial);
            Assert.AreSame(originalChild, disabledRenderer.sharedMaterial);
            Assert.AreSame(originalWall, wall.sharedMaterial); Assert.AreSame(originalChild, child.sharedMaterial);
            child.GetPropertyBlock(block, 0); Assert.AreEqual(0.37f, block.GetFloat("_Metallic"));
            Assert.AreEqual(0, _occlusion.ActiveRendererCount);
        }

        [Test] public void UpperFloorRevealDoesNotRecreateTheObstructionAsAFootprint()
        {
            var floor = Wall(-3); floor.GetComponent<StructuralModule>().layer = "floor";
            floor.gameObject.layer = PhysicsLayers.Walkable;
            floor.transform.position = new Vector3(0, 2, -3); floor.transform.localScale = new Vector3(8, 0.2f, 8);
            Reveal(); Reveal(); Assert.Greater(_occlusion.ActiveRendererCount, 0);
            foreach (var node in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                Assert.AreNotEqual("CameraWallFootprint", node.name, "upper floor must disappear completely");
            Assert.IsTrue(floor.GetComponent<Collider>().enabled);
        }

        [Test] public void WallAssemblyHasOneFootprintAtItsBaseRatherThanAtEveryTrimHeight()
        {
            var root = new GameObject("LogicalWall"); _objects.Add(root);
            root.transform.position = new Vector3(0, 0, -2); root.AddComponent<StructuralModule>().layer = "edge";
            for (int i = 0; i < 3; i++)
            {
                var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
                panel.transform.SetParent(root.transform, false); panel.transform.localPosition = new Vector3(0, 0.7f * (i + 1), 0);
                panel.transform.localScale = new Vector3(4, 0.3f, 0.2f); panel.layer = PhysicsLayers.Structure;
                panel.GetComponent<Renderer>().sharedMaterial = RuntimeVisualCatalog.Material(Color.gray);
            }
            Reveal(); Reveal(); int count = 0;
            foreach (var node in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                if (node.name == "CameraWallFootprint") { count++; Assert.Less(node.position.y, 0.1f); }
            Assert.AreEqual(1, count);
        }

        [Test] public void CollisionOnlyImportedMeshIsHiddenWithoutRemovingCollision()
        {
            var helper = GameObject.CreatePrimitive(PrimitiveType.Cube); _objects.Add(helper);
            helper.name = "Platform_Simple_1_convcolonly";
            StructuralLayoutBuilder.HideCollisionOnlyVisuals(helper);
            Assert.IsFalse(helper.GetComponent<Renderer>().enabled); Assert.IsTrue(helper.GetComponent<Collider>().enabled);
        }

        [Test] public void UpperWallDoesNotLeaveAnOverheadBoundaryForLowerDeckPlayer()
        {
            var wall=Wall(-4); wall.transform.position=new Vector3(0,3,-4);
            Reveal(); Reveal(); Assert.Greater(_occlusion.ActiveRendererCount,0);
            foreach(var node in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                Assert.AreNotEqual("CameraWallFootprint",node.name);
            _occlusion.RestoreAll(); Assert.IsTrue(wall.GetComponent<Collider>().enabled);
        }

        [Test] public void SupportFloorIsKeptAndUpperDeckRestoresOnTeleport()
        {
            var floor = Wall(-2); floor.GetComponent<StructuralModule>().layer = "floor";
            floor.gameObject.layer = PhysicsLayers.Walkable; floor.transform.position = new Vector3(0, -0.15f, 0);
            floor.transform.localScale = new Vector3(8, 0.2f, 8);
            Reveal(); Assert.AreEqual(0, _occlusion.ActiveRendererCount);
            floor.transform.position = new Vector3(0, 2, -3); Reveal(); Assert.Greater(_occlusion.ActiveRendererCount, 0);
            Reveal(new Vector3(0, 4, 0)); Assert.AreEqual(0, _occlusion.ActiveRendererCount);
        }
    }
}
