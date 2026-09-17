using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.App;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Game;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>
    /// Decision 59, on the real Playable scene: the active ship builds a NavMesh for the Threat agent when it loads,
    /// every floor of the ship is reachable across it, a hunting threat is walked by its own
    /// <see cref="NavMeshAgent"/> (with Core still storing the position), a closed door is not walkable, and a
    /// burning room is marked as the costly fire area.
    /// </summary>
    public class ThreatNavMeshPlayModeTests
    {
        IStorage _previousStorage;
        IResourceReader _previousResources;
        ILog _previousLog;
        PlayableBootstrap _boot;
        RunSession _session;

        [SetUp]
        public void SetUp()
        {
            _previousStorage = CoreServices.UserStorage;
            _previousResources = CoreServices.Resources;
            _previousLog = CoreServices.Log;
            AppServices.StorageOverride = new MemoryStorage();
            RunLaunchRequest.Pending = null;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (RunSessionHost host in Object.FindObjectsByType<RunSessionHost>()) Object.DestroyImmediate(host.gameObject);
            foreach (PlayableBootstrap boot in Object.FindObjectsByType<PlayableBootstrap>()) Object.DestroyImmediate(boot.gameObject);
            AppServices.Shutdown();
            AppServices.StorageOverride = null;
            RunLaunchRequest.Pending = null;
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
            CoreServices.UserStorage = _previousStorage;
            CoreServices.Resources = _previousResources;
            CoreServices.Log = _previousLog;
        }

        IEnumerator BootGoldenShip()
        {
            RunLaunchRequest.Pending = RunLaunchRequest.GoldenShip();
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName);
            float deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline && (PlayableBootstrap.Current == null || !PlayableBootstrap.Current.IsBooted))
                yield return null;
            _boot = PlayableBootstrap.Current;
            Assert.IsNotNull(_boot, "the Playable scene has a bootstrap");
            Assert.IsTrue(_boot.IsBooted, "the run booted: " + _boot.BootFailure);
            _session = _boot.Session;
            yield return null;
        }

        RunSessionHost Host => _boot.Host;

        static NavMeshQueryFilter ThreatFilter => new NavMeshQueryFilter
        {
            agentTypeID = ShipNavMesh.AgentTypeId,
            areaMask = NavMesh.AllAreas,
        };

        [UnityTest]
        public IEnumerator TheActiveShipBuildsANavMeshForTheThreatAgent()
        {
            yield return BootGoldenShip();

            Assert.IsNotNull(Host.ThreatNavigation, "the host owns the threats' navigation");
            Assert.IsTrue(Host.ThreatNavigation.HasNavMesh, "the home ship built a NavMesh");
            Assert.AreSame(Host.ShipHost.HomeLoader.GameObject.GetComponent<ShipNavMesh>(), Host.ThreatNavigation.Ship);
            Assert.AreEqual(ShipNavMesh.AgentTypeId, Host.Session.ThreatManager.Navigation != null ? ShipNavMesh.AgentTypeId : -1);
            Assert.AreNotEqual(0, ShipNavMesh.AgentTypeId, "the Threat agent type is registered (run the project bootstrap)");
            Assert.Greater(NavMesh.CalculateTriangulation().indices.Length / 3, 0, "the surface has triangles");
        }

        /// <summary>
        /// Every floor cell is walkable, and a room's own cells are reachable from one another — a room is never cut
        /// in half. This is what the vertex-wrapper fix (decision 58) bought: stray walls used to run through rooms,
        /// which a NavMesh reads as a wall and a threat can never get past. (Rooms are not checked against each
        /// other: doors and route gates close, and the golden ship's second deck has no walkable connection at all,
        /// which is decision 57.)
        /// </summary>
        [UnityTest]
        public IEnumerator EveryRoomOfTheShipIsWalkableAndWholeOnTheNavMesh()
        {
            yield return BootGoldenShip();

            Transform root = Host.ShipHost.HomeLoader.GameObject.transform;
            List<StructuralModule> floors = root.GetComponentsInChildren<StructuralModule>(true)
                .Where(m => m.layer == "floor")
                .ToList();
            Assert.Greater(floors.Count, 10, "the golden ship has floors");

            NavMeshQueryFilter filter = ThreatFilter;
            var offMesh = new List<string>();
            var byRoom = new Dictionary<string, List<Vector3>>();
            foreach (StructuralModule floor in floors)
            {
                if (!NavMesh.SamplePosition(floor.transform.position, out NavMeshHit hit, 1.5f, filter))
                {
                    offMesh.Add($"{floor.roomId} {floor.transform.position}");
                    continue;
                }
                if (!byRoom.TryGetValue(floor.roomId, out List<Vector3> cells)) byRoom[floor.roomId] = cells = new List<Vector3>();
                cells.Add(hit.position);
            }
            Assert.IsEmpty(offMesh, $"{offMesh.Count}/{floors.Count} floor cells are not walkable: " + string.Join(", ", offMesh));

            var split = new List<string>();
            foreach (var room in byRoom)
            {
                for (int i = 1; i < room.Value.Count; i++)
                {
                    var path = new NavMeshPath();
                    if (!NavMesh.CalculatePath(room.Value[0], room.Value[i], filter, path) || path.status != NavMeshPathStatus.PathComplete)
                        split.Add($"{room.Key}: {room.Value[0]} -> {room.Value[i]}");
                }
            }
            Assert.IsEmpty(split, "a room is cut in two by geometry standing inside it: " + string.Join(", ", split));
        }

        [UnityTest]
        public IEnumerator AHuntingThreatIsWalkedByItsAgentTowardsThePlayer()
        {
            yield return BootGoldenShip();

            // The golden ship's fallback encounter spawns beside the start room and comes for an idle player, so the
            // threats' own AI drives this: nothing is teleported or forced.
            ThreatAIState threat = _session.ThreatManager.Threats.FirstOrDefault();
            Assert.IsNotNull(threat, "the golden ship spawns threats");
            Transform player = Host.SceneState.Player.transform;
            float startDistance = Vector3.Distance(Frame.ToUnity(ThreatPosition(threat)), player.position);

            NavMeshAgent agent = null;
            float closest = startDistance;
            float deadline = Time.realtimeSinceStartup + 25f;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                if (agent == null && Host.Threats.Nodes.TryGetValue(threat.InstanceId, out GameObject node) && node != null)
                    node.TryGetComponent(out agent);
                if (agent == null) continue;
                closest = Mathf.Min(closest, Vector3.Distance(agent.transform.position, player.position));
                if (closest <= Mathf.Max((float)threat.AttackRange, 1.5f)) break;
            }

            Assert.IsNotNull(agent, "the threat's placeholder got a NavMeshAgent");
            Assert.AreEqual(ShipNavMesh.AgentTypeId, agent.agentTypeID);
            Assert.IsTrue(agent.isOnNavMesh, "the agent stands on the ship's NavMesh");
            Assert.LessOrEqual(closest, Mathf.Max((float)threat.AttackRange, 1.5f),
                $"the agent walked its threat into range of the player (from {startDistance:0.0} m, state {threat.State})");
            Assert.AreEqual(agent.transform.position.x, Frame.ToUnity(ThreatPosition(threat)).x, 0.5f,
                "Core stores the position its agent reached");
        }

        [UnityTest]
        public IEnumerator AClosedDoorIsCarvedOutOfTheNavMesh()
        {
            yield return BootGoldenShip();

            AuthoredPortalRuntime closed = Host.ShipHost.HomeLoader.View.GetAuthoredPortalNodes()
                .FirstOrDefault(p => p.GetBlockerCollider() != null && p.GetBlockerCollider().enabled);
            Assert.IsNotNull(closed, "the golden ship has a closed portal");
            Assert.IsTrue(closed.GetBlockerCollider().TryGetComponent(out NavMeshObstacle obstacle), "its blocker carves the NavMesh");
            Assert.IsTrue(obstacle.carving);
            Assert.IsTrue(obstacle.enabled, "a closed blocker carves");
            yield return null;

            Assert.IsFalse(NavMesh.SamplePosition(closed.transform.position + Vector3.up * 0.2f, out NavMeshHit _, 0.35f, ThreatFilter),
                "the doorway of a closed portal is not walkable");
        }

        [UnityTest]
        public IEnumerator ABurningRoomIsMarkedAsTheCostlyFireArea()
        {
            yield return BootGoldenShip();

            ThreatAIState threat = _session.ThreatManager.Threats.FirstOrDefault();
            Assert.IsNotNull(threat);
            Vec3 cell = ThreatPosition(threat);
            Vector3 cellUnity = Frame.ToUnity(cell);
            Assert.IsTrue(NavMesh.SamplePosition(cellUnity, out NavMeshHit before, 2f, ThreatFilter));
            Assert.AreEqual(1, before.mask, "the cell starts on the ordinary walkable area");

            var navigation = (IThreatNavigation)Host.ThreatNavigation;
            navigation.SetAvoidedCells(GdArray.Of(cell), _session.ThreatManager.NavGraph.CellSize);
            Assert.IsTrue(NavMesh.SamplePosition(cellUnity, out NavMeshHit burning, 2f, ThreatFilter));
            Assert.AreEqual(1 << NavMesh.GetAreaFromName(ShipNavMesh.FireAreaName), burning.mask, "the burning cell costs fire");
            Assert.Greater(NavMesh.GetAreaCost(NavMesh.GetAreaFromName(ShipNavMesh.FireAreaName)), 1f, "and fire costs more than a floor");

            navigation.SetAvoidedCells(new GdArray(), _session.ThreatManager.NavGraph.CellSize);
            Assert.IsTrue(NavMesh.SamplePosition(cellUnity, out NavMeshHit after, 2f, ThreatFilter));
            Assert.AreEqual(1, after.mask, "putting the fire out clears the area again");
            yield return null;
        }

        static Vec3 ThreatPosition(ThreatAIState threat) =>
            new Vec3(V.F64(threat.WorldPosition[0]), V.F64(threat.WorldPosition[1]), V.F64(threat.WorldPosition[2]));
    }
}
