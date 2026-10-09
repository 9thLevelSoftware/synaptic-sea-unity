using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Session;
using SynapticSea.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>
    /// Playtest 1: survivors suffocated at a generated home. The session only recomputes occupancy every tick once the lifeboat is commissioned,
    /// so on the opening deck <c>CurrentOccupancy</c> lagged a survivor walking between the home and its docked lifeboat, and the air check,
    /// which trusted the occupancy, found "no vessel underfoot" and put the survivor on suit air everywhere. The headless safety proofs idled a
    /// survivor in a session with no vessels at all, so they could not see it. This test uses the real scene.
    /// </summary>
    public partial class RunLifecyclePlayModeTests
    {
        /// <summary>The seeds from the first human playtest, plus others whose docked lifeboat starts unpowered (2024, 777, 1234).</summary>
        static readonly long[] AirSeeds = { 756700368, 816288703, 2024, 777, 1234 };

        /// <summary>
        /// Every point a survivor can actually stand on (the navigation surface, sampled on a 1 m grid across the home and the docked lifeboat) must
        /// have a vessel under it that supplies the air. Standing on floor-cell centres only proves a transform against itself; the walkable
        /// surface is what the survivor really uses.
        /// </summary>
        [UnityTest, Timeout(1200000)]
        public IEnumerator GeneratedHomeEveryWalkablePointHasAVesselUnderItThatSuppliesTheAir()
        {
            var report = new List<string>();
            foreach (long seed in AirSeeds)
            {
                yield return BootPlayable(RunLaunchRequest.GeneratedHomeRun(seed));
                var player = _boot.Host.SceneState.Player;
                yield return FixedSteps(20);
                var filter = new UnityEngine.AI.NavMeshQueryFilter { agentTypeID = ShipNavMesh.AgentTypeId, areaMask = UnityEngine.AI.NavMesh.AllAreas };
                Bounds bounds = new Bounds(player.transform.position, Vector3.zero);
                foreach (var ship in new[] { _s.HomeShip, _s.LifeboatShip })
                    foreach (Vec3 local in AssemblyMobility.Floors(ship.BuiltLayout))
                        bounds.Encapsulate(Frame.ToUnity(ship.SceneRoot.GlobalTransform * local));
                bounds.Expand(new Vector3(8f, 0f, 8f));
                int walkable = 0; var bad = new List<string>();
                var seen = new HashSet<Vector2Int>();
                for (float x = bounds.min.x; x <= bounds.max.x; x += 1f)
                    for (float z = bounds.min.z; z <= bounds.max.z; z += 1f)
                        foreach (float y in new[] { 0.1f, 4.1f })
                        {
                            if (!UnityEngine.AI.NavMesh.SamplePosition(new Vector3(x, y, z), out var hit, 0.3f, filter)) continue;
                            var key = new Vector2Int(Mathf.RoundToInt(hit.position.x * 2), Mathf.RoundToInt(hit.position.z * 2) + 100000 * Mathf.RoundToInt(hit.position.y));
                            if (!seen.Add(key)) continue;
                            walkable++;
                            player.TeleportTo(hit.position + Vector3.up * 0.05f);
                            yield return new WaitForFixedUpdate();
                            // The session only recomputes occupancy every tick once the lifeboat is commissioned, so on the opening deck the occupancy
                            // can be any vessel while the survivor stands over another: the air must not depend on it.
                            foreach (var staleOccupancy in new[] { _s.HomeShip, _s.LifeboatShip })
                            {
                                _s.CurrentOccupancy = staleOccupancy;
                                if (_s.FieldSuitPressure && bad.Count < 400) bad.Add(hit.position.ToString("0.0") + " stale=" + (staleOccupancy == _s.HomeShip ? "home" : "lifeboat") + " " + _s.AirDiagnostics());
                            }
                        }
                report.Add("seed " + seed + ": " + bad.Count + " of " + walkable + " walkable (point, occupancy) pairs put the survivor on suit air"
                    + (bad.Count == 0 ? "" : "\n    " + string.Join("\n    ", bad.Take(10))));
            }
            Debug.Log("[WalkableAir]\n" + string.Join("\n", report));
            Assert.IsTrue(report.All(r => r.Contains(": 0 of ")), string.Join("\n", report));
        }
    }
}
