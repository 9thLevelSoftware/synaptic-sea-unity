using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    public class HomeAssemblyGeometryTests
    {
        static ShipInstance Ship(string id, GameObject root)
        {
            var records = new GdArray();
            for (int i = 0; i < 4; i++)
            {
                records.Add(new GdDict { { "position", new Vec3(i * 4, 0, 0) } });
                var floor = new GameObject("Floor") { layer = PhysicsLayers.Structure };
                floor.transform.SetParent(root.transform, false); floor.transform.localPosition = Frame.ToUnity(new Vec3(i * 4, -.125f, 0));
                floor.AddComponent<BoxCollider>().size = new Vector3(4, .25f, 4);
            }
            var scene = new SceneShipRoot(root);
            typeof(SceneShipRoot).GetMethod("Attach", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(scene, new object[] { null });
            var ship = ShipInstance.Create(id, "", null, null, scene);
            ship.BuiltLayout = new GdDict { { "structural_plan", new GdDict { { "floor_placements", records } } } };
            return ship;
        }
        static BoxCollider Wall(GameObject root, Vec3 position)
        {
            var go = new GameObject("QualifiedWall") { layer = PhysicsLayers.Structure };
            go.transform.SetParent(root.transform, false); go.transform.localPosition = Frame.ToUnity(position);
            var module = go.AddComponent<StructuralModule>(); module.layer = "edge"; module.moduleId = "wall_straight_1x1";
            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube); panel.transform.SetParent(go.transform, false);
            panel.transform.localPosition = new Vector3(0, 1.5f, 0); panel.transform.localScale = new Vector3(.2f, 3, 4);
            var trim = GameObject.CreatePrimitive(PrimitiveType.Cube); trim.transform.SetParent(go.transform, false);
            trim.transform.localPosition = new Vector3(0, 2.9f, 0); trim.transform.localScale = new Vector3(.3f, .1f, 4);
            return panel.GetComponent<BoxCollider>();
        }
        [Test] public void SecuredConnectionReplacesBothWholeBoundariesAndRestoresOnRelease()
        {
            var homeGo = new GameObject("Home"); var wreckGo = new GameObject("Wreck"); var geometry = new HomeAssemblyGeometry();
            try
            {
                var home = Ship("home", homeGo); var wreck = Ship("wreck", wreckGo);
                Assert.IsTrue(HomeJoinPlanner.TryPlan(home, wreck, out var h, out var m, out var reason), reason);
                var homeWall = Wall(homeGo, (Vec3)h["position"]); var wreckWall = Wall(wreckGo, (Vec3)m["position"]);
                var untouched = Wall(homeGo, new Vec3(14, 0, 0));
                Assert.IsTrue(DockingManager.Dock(home, wreck, DockingManager.HostPortToWorld(home, h), m).GetBool("success"));
                var edge = (GdDict)wreck.DockingPorts[0]; edge["connection_kind"] = "secured";
                geometry.Reconcile(home);
                Assert.IsFalse(homeWall.enabled); Assert.IsFalse(wreckWall.enabled); Assert.IsTrue(untouched.enabled);
                var wallModule=homeWall.GetComponentInParent<StructuralModule>();
                wallModule.SetIntegrity(StructuralModule.IntegrityDamaged);
                new SceneModuleNode(wallModule, new SceneShipRoot(homeGo)).SetCollisionEnabled(true);
                Assert.IsFalse(homeWall.enabled,"changing active ship/integrity cannot reseal an established passage");
                foreach (var renderer in homeWall.GetComponentInParent<StructuralModule>().GetComponentsInChildren<Renderer>()) Assert.IsFalse(renderer.enabled);
                Assert.IsTrue(homeGo.transform.Find("HomeConnection_wreck/Door").GetComponent<BoxCollider>().enabled);
                edge["connection_open"] = true; geometry.Reconcile(home);
                Assert.IsFalse(homeGo.transform.Find("HomeConnection_wreck/Door").GetComponent<BoxCollider>().enabled);
                Assert.IsTrue(homeGo.transform.Find("HomeConnection_wreck/LeftFrame").GetComponent<BoxCollider>().enabled);
                geometry.Restore(); Assert.IsTrue(homeWall.enabled); Assert.IsTrue(wreckWall.enabled);
                Assert.IsNull(homeGo.transform.Find("HomeConnection_wreck"));
            }
            finally { geometry.Restore(); Object.DestroyImmediate(homeGo); Object.DestroyImmediate(wreckGo); }
        }
    }
}
