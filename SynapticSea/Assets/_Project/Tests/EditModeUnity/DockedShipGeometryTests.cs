using NUnit.Framework;
using System.Linq;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
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
                var overlapModule=overlap.GetComponent<StructuralModule>();
                overlapModule.SetIntegrity(StructuralModule.IntegrityDamaged);
                new SceneModuleNode(overlapModule,mobile).SetCollisionEnabled(true);
                Assert.IsFalse(overlap.enabled,"a later integrity refresh cannot restore a wall through the baked dock passage");
                Assert.IsTrue(overlapModule.DockOverlapOpening);
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
                Assert.IsFalse(overlapModule.DockOverlapOpening);Assert.IsEmpty(overlapModule.DockOverlapColliders);
            }
            finally { Object.DestroyImmediate(hostGo); Object.DestroyImmediate(mobileGo); }
        }

        [TestCase("valid")] [TestCase("wrong_profile")] [TestCase("fake_owner")] [TestCase("fake_semantic")] [TestCase("missing_port")]
        public void ActualProfileDockMetadataAdmitsOnlyOwnedSupportedExterior(string metadataCase)
        {
            var previous = CoreServices.Resources;
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath); CatalogRegistry.Clear();
            var hostParent = new GameObject("ActualProfileHost"); var boatGo = new GameObject("ActualBoatFloorPlan");
            var geometry = new DockedShipGeometry();
            try
            {
                var docs = new ShipGenerator().GenerateFirstAway(new FirstAwayGenerationInputs(42, 17, 0, 2, "0:0:0", "ship_0:0:0", "breach_field", "standard"));
                Assert.IsNotNull(docs);
                var builder = ShipSceneBuilder.Create(hostParent.transform);
                Assert.IsTrue(builder.LoadFromDocuments(docs.Layout, docs.Kit, docs.GameplaySlice, true));
                var host = new ShipLoaderNode(builder.View);
                var boatLayout = LifeBoatBuilder.BuildLayout();
                // Engineering fixture: real compiled lifeboat floor ownership and ordinary port alignment.
                // This does not claim player traversal or scene/portal acceptance.
                foreach (GdDict floor in boatLayout.GetDictOrEmpty("structural_plan").GetArrayOrEmpty("floor_placements"))
                {
                    Assert.IsTrue(floor.Has("position"), "StructuralEdgeCompiler floor placement declares position");
                    Vec3 position = Vec3.FromArray(floor["position"], Vec3.Inf);
                    Assert.IsFalse(double.IsNaN(position.X) || double.IsInfinity(position.X)
                        || double.IsNaN(position.Y) || double.IsInfinity(position.Y)
                        || double.IsNaN(position.Z) || double.IsInfinity(position.Z), "compiled floor position must be finite before creating geometry");
                    Module(boatGo, "floor_1x1", "floor", Frame.ToUnity(position) - Vector3.up * .125f, new Vector3(4, .25f, 4));
                }
                Assert.AreEqual(3, boatGo.GetComponentsInChildren<BoxCollider>().Length, "actual compiled lifeboat has three floor cells");
                var mobile = new SceneShipRoot(boatGo);
                var sceneHost = new UnityShipSceneHost(hostParent.transform);
                sceneHost.AttachShipRoot(host); sceneHost.AttachShipRoot(mobile);
                Assert.IsTrue(host.IsInsideTree); Assert.IsTrue(mobile.IsInsideTree);
                var hostShip = ShipInstance.Create("ship_0:0:0", "0:0:0", null, null, host);
                var boatShip = ShipInstance.Create("lifeboat", "", null, null, mobile);
                var port = DockPorts.ForDerelict(docs.Layout, 42, 2);
                Assert.AreEqual(Vec3.FromArray(docs.Layout.GetDictOrEmpty("docking_port").GetArrayOrEmpty("position")), port["position"], "strict authored profile port patch is prerequisite");
                var dockResult = DockingManager.Dock(hostShip, boatShip, DockingManager.HostPortToWorld(hostShip, port), DockPorts.ForLifeboat(boatLayout));
                Assert.IsTrue(dockResult.GetBool("success"), GdJson.Stringify(dockResult));
                string dock = docs.Layout.GetDictOrEmpty("docking_port").GetString("room_id");
                var authored = host.LayoutDoc.GetArrayOrEmpty("rooms").Cast<GdDict>().Single(room => room.GetString("id") == dock);
                var initial = host.GameObject.GetComponentsInChildren<StructuralModule>().Where(m => m.layer == "edge" && m.moduleId.StartsWith("wall_")
                    && m.roomIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().SequenceEqual(new[] { dock }))
                    .SelectMany(m => m.GetComponentsInChildren<BoxCollider>()).Where(c => c.enabled && !c.isTrigger).ToArray();
                Assert.IsNotEmpty(initial, "actual generated dock owns live exterior wall colliders");
                Physics.SyncTransforms();
                Vector3 boatFloor = boatGo.GetComponentsInChildren<BoxCollider>().First().bounds.center; boatFloor.y = 1.5f;
                Vector3 hostLocal = host.GameObject.transform.InverseTransformPoint(boatFloor);
                var guard = Module(host.GameObject, "wall_straight_1x1", "edge", hostLocal, new Vector3(1, 3, .2f)); guard.GetComponent<StructuralModule>().roomIds = new[] { dock, "" };
                var fakePrefix = Module(host.GameObject, "wall_straight_1x1", "edge", hostLocal, new Vector3(1, 3, .2f)); fakePrefix.GetComponent<StructuralModule>().roomIds = new[] { dock + "/fake" };
                var interior = Module(host.GameObject, "wall_straight_1x1", "edge", hostLocal, new Vector3(1, 3, .2f)); interior.GetComponent<StructuralModule>().roomIds = new[] { dock, dock + "/interior" };
                var locked = Module(host.GameObject, "wall_straight_1x1", "edge", hostLocal, new Vector3(1, 3, .2f)); locked.GetComponent<StructuralModule>().roomIds = new[] { dock }; NavMeshBlocker.Attach(locked);
                var voidWall = Module(host.GameObject, "wall_straight_1x1", "edge", hostLocal + Vector3.right * 100, new Vector3(1, 3, .2f)); voidWall.GetComponent<StructuralModule>().roomIds = new[] { dock };
                if (metadataCase == "wrong_profile") host.LayoutDoc["generation_profile"] = "other_profile";
                if (metadataCase == "fake_owner") authored["owner_id"] = "unrelated_ship";
                if (metadataCase == "fake_semantic") authored["semantic_id"] = "interior";
                if (metadataCase == "missing_port") host.LayoutDoc["docking_port"] = new GdDict();
                Physics.SyncTransforms(); geometry.Reconcile(host, mobile);
                Assert.AreEqual(metadataCase != "valid", guard.enabled);
                Assert.IsTrue(fakePrefix.enabled); Assert.IsTrue(interior.enabled); Assert.IsTrue(locked.enabled); Assert.IsTrue(voidWall.enabled);
                if (metadataCase == "valid") Assert.IsTrue(initial.Any(c => !c.enabled), "actual aligned generated dock exterior opens through supported boat floor");
                else Assert.IsTrue(initial.All(c => c.enabled), "profile identity mismatch preserves actual authored boundaries");
                geometry.Reconcile(null, null);
                Assert.IsTrue(guard.enabled); Assert.IsTrue(initial.All(c => c.enabled), "release restores actual generated dock collision");
            }
            finally
            {
                geometry.Reconcile(null, null); Object.DestroyImmediate(hostParent); Object.DestroyImmediate(boatGo);
                CoreServices.Resources = previous; CatalogRegistry.Clear();
            }
        }

        [Test]
        public void DedicatedDockAdmitsSupportedBoatOverlapButKeepsInternalBoundaries()
        {
            var hostGo=new GameObject("DockHost"); var mobileGo=new GameObject("DockBoat"); var geometry=new DockedShipGeometry();
            try
            {
                Module(hostGo,"floor_1x1","floor",new Vector3(0,-.125f,0),new Vector3(4,.25f,4));
                Module(mobileGo,"floor_1x1","floor",new Vector3(0,-.125f,0),new Vector3(4,.25f,4));
                var admission=Module(hostGo,"wall_straight_1x1","edge",new Vector3(0,1.5f,0),new Vector3(4,3,.2f));
                admission.GetComponent<StructuralModule>().roomIds=new[]{"dock_01", ""};
                var trim=GameObject.CreatePrimitive(PrimitiveType.Cube);
                trim.transform.SetParent(admission.transform,false); trim.transform.localPosition=new Vector3(0,1.35f,0);
                trim.transform.localScale=new Vector3(4,.1f,.2f); Object.DestroyImmediate(trim.GetComponent<Collider>());
                var trimRenderer=trim.GetComponent<Renderer>();
                var internalWall=Module(hostGo,"wall_straight_1x1","edge",new Vector3(0,1.5f,1),new Vector3(4,3,.2f));
                internalWall.GetComponent<StructuralModule>().roomIds=new[]{"dock_01","corridor_01"};
                geometry.Reconcile(new SceneShipRoot(hostGo),new SceneShipRoot(mobileGo));
                Assert.IsFalse(admission.enabled); Assert.IsFalse(trimRenderer.enabled,"a removed dock boundary cannot leave floating upper trim"); Assert.IsTrue(internalWall.enabled);
                geometry.Reconcile(null,null); Assert.IsTrue(admission.enabled); Assert.IsTrue(trimRenderer.enabled,"undocking restores the complete assembly");
            }
            finally {Object.DestroyImmediate(hostGo);Object.DestroyImmediate(mobileGo);}
        }
    }
}
