using System.Collections;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>
    /// Plan Phase 8 gate, live physics: golden coherent_ship_001 loads through <see cref="ShipSceneBuilder"/> with real
    /// colliders, the player spawns on the start marker, stands on the floor, walks, and the walls keep it inside the hull.
    /// </summary>
    public class GoldenShipPlayModeTests
    {
        const string Layout = "res://data/procgen/golden/coherent_ship_001/layout.json";
        const string Kit = "res://data/kits/ship_structural_v0.json";
        const string Slice = "res://data/procgen/golden/coherent_ship_001/gameplay_slice.json";

        GameObject _root;
        ShipView _view;
        PlayerController _player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
            _root = new GameObject("GoldenShipPlayModeTests");

            var builder = ShipSceneBuilder.Create(_root.transform, "Ship");
            string failure = "";
            builder.LoadFailed += r => failure = r;
            Assert.IsTrue(builder.LoadFromPaths(Layout, Kit, Slice), failure);
            _view = builder.View;
            Physics.SyncTransforms();

            var playerGo = new GameObject("Player", typeof(CharacterController));
            playerGo.transform.SetParent(_root.transform, false);
            _player = playerGo.AddComponent<PlayerController>();
            _player.TeleportTo(_view.GetStartPoseWorld().position);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(_root);
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
            yield return null;
        }

        static IEnumerator FixedSteps(int n)
        {
            for (int i = 0; i < n; i++) yield return new WaitForFixedUpdate();
        }

        [UnityTest]
        public IEnumerator ShipHasOneWrapperPerPlacementAndColliders()
        {
            GdDict layout = _view.GetLayoutCopy();
            int placements = layout.GetDict("structural_plan")?.GetArray("placements")?.Count ?? 0;
            Assert.Greater(placements, 0);
            int wrappers = _view.Modules.Count(m => m.layer != "floor" && m.layer != "ceiling");
            // Edges a relocated vertex wrapper's wing walls build nothing of their own (VertexWrapperPlacement).
            int covered = VertexWrapperPlacement.Resolve(layout.GetDict("structural_plan")).Covered.Count;
            Assert.AreEqual(placements - covered, wrappers, "one structural wrapper per edge placement");
            Assert.Greater(_view.CountCollisionShapes(), placements, "wrappers carry collision");
            Assert.IsTrue(Physics.Raycast(_player.transform.position + Vector3.up, Vector3.down, 5f, 1 << PhysicsLayers.Structure),
                "a floor collider lies under the start marker");
            yield return null;
        }

        [UnityTest]
        public IEnumerator PlayerSettlesOnTheFloorAtTheStartMarker()
        {
            float startY = _player.transform.position.y;
            yield return FixedSteps(60);
            Assert.IsTrue(_player.Controller.isGrounded, "player grounded after one second");
            Assert.That(_player.transform.position.y, Is.InRange(startY - 0.25f, startY + 0.5f), "player neither fell through nor popped up");
        }

        [UnityTest]
        public IEnumerator PlayerWalksAndWallsContainIt()
        {
            yield return FixedSteps(30);
            Bounds hull = ShipBounds();
            Vector3 origin = _player.transform.position;
            float farthest = 0f;
            foreach (var dir in new[] { new Vec3(1f, 0f, 0f), new Vec3(-1f, 0f, 0f), new Vec3(0f, 0f, 1f), new Vec3(0f, 0f, -1f) })
            {
                _player.TeleportTo(origin);
                _player.SetScriptedMoveDirection(dir);
                yield return FixedSteps(300); // 6 s at 6 m/s: far longer than any room
                _player.ClearScriptedMoveDirection();
                Vector3 p = _player.transform.position;
                farthest = Mathf.Max(farthest, Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(origin.x, 0f, origin.z)));
                Assert.IsTrue(hull.Contains(new Vector3(p.x, hull.center.y, p.z)), $"walls stopped the player walking {dir}: {p} outside {hull}");
                Assert.Greater(p.y, origin.y - 1f, $"player did not fall out of the ship walking {dir}");
            }
            Assert.Greater(farthest, 1.5f, "the player can walk away from the start marker in at least one direction");
        }

        Bounds ShipBounds()
        {
            var colliders = _view.StructuralRoot.GetComponentsInChildren<Collider>();
            var b = colliders[0].bounds;
            foreach (var c in colliders) b.Encapsulate(c.bounds);
            b.Expand(new Vector3(0.5f, 0f, 0.5f));
            return b;
        }
    }
}
