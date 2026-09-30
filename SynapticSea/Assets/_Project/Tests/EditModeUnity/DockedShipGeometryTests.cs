using NUnit.Framework;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using UnityEngine;

namespace SynapticSea.Tests.Unity
{
    public class DockedShipGeometryTests
    {
        static BoxCollider Module(GameObject root, string id, string layer, Vector3 position, Vector3 size)
        {
            var go = new GameObject(id) { layer = PhysicsLayers.Structure };
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;
            var module = go.AddComponent<StructuralModule>();
            module.moduleId = id; module.layer = layer; module.roomIds = new[] { "airlock_01" };
            var box = go.AddComponent<BoxCollider>(); box.size = size;
            return box;
        }

        [Test]
        public void OverlappingStaticWallsYieldToSupportedFloorButLocksAndUnsupportedEdgesStay()
        {
            var hostGo = new GameObject("Host"); var mobileGo = new GameObject("Mobile");
            var host = new SceneShipRoot(hostGo); var mobile = new SceneShipRoot(mobileGo);
            var geometry = new DockedShipGeometry();
            try
            {
                Module(hostGo, "floor_1x1", "floor", new Vector3(0, -0.125f, 0), new Vector3(4, 0.25f, 4));
                Module(mobileGo, "floor_1x1", "floor", new Vector3(0, -0.125f, 0), new Vector3(4, 0.25f, 4));
                var overlap = Module(mobileGo, "wall_straight_1x1", "edge", new Vector3(0, 1.5f, 0), new Vector3(4, 3, 0.2f));
                var outside = Module(mobileGo, "wall_straight_1x1", "edge", new Vector3(6, 1.5f, 0), new Vector3(4, 3, 0.2f));
                var otherDeck = Module(mobileGo, "wall_straight_1x1", "edge", new Vector3(0, 5.5f, 0), new Vector3(4, 3, 0.2f));
                var locked = Module(mobileGo, "doorway_frame_blocked_1x1", "edge", new Vector3(0, 1.5f, 0), new Vector3(1, 3, 0.2f));
                NavMeshBlocker.Attach(locked);
                geometry.Reconcile(host, mobile);
                Assert.IsFalse(overlap.enabled);
                Assert.IsTrue(outside.enabled); Assert.IsTrue(otherDeck.enabled); Assert.IsTrue(locked.enabled);
                Assert.AreEqual(1, geometry.SuppressedColliderCount);
                geometry.Reconcile(host, mobile);
                Assert.AreEqual(1, geometry.SuppressedColliderCount, "repeat view passes do not accumulate changes");
                var retainedWall = Module(hostGo, "wall_straight_1x1", "edge", new Vector3(0, 1.5f, 0), new Vector3(4, 3, 0.2f));
                retainedWall.GetComponent<StructuralModule>().roomIds = new[] { "corridor_01" };
                var point = new SynapticSea.Core.Session.RepairPoint { Parent = mobile,
                    LocalPosition = new SynapticSea.Core.Variant.Vec3(0, 0.55, 0) };
                var original = point.LocalPosition;
                geometry.NormalizeInteractions(new[] { point });
                Assert.AreNotEqual(original, point.LocalPosition, "wall-embedded repair markers receive a nearby standing anchor");
                Assert.IsTrue(SpawnClearance.IsClear(Frame.ToUnity(point.GlobalPosition)));
                geometry.Reconcile(null, null);
                Assert.AreEqual(original, point.LocalPosition, "undocking restores the authored marker anchor");
                Assert.IsTrue(overlap.enabled, "undocking restores the source instance collision");
            }
            finally { Object.DestroyImmediate(hostGo); Object.DestroyImmediate(mobileGo); }
        }
    }
}
