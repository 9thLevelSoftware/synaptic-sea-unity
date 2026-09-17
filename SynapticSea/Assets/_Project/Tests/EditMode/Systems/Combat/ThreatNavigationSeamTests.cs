using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>
    /// The seam the Unity port hangs its NavMesh agents on (decision 59): with an
    /// <see cref="IThreatNavigation"/> attached the scene walks the threats and Core stores where they got to;
    /// without one — every headless session and every other EditMode suite — the ADR-0049 nav graph still does.
    /// </summary>
    public class ThreatNavigationSeamTests
    {
        [SetUp]
        public void SetUp()
        {
            // ThreatRuntime loads the archetype, weapon and ammo catalogs when it is constructed.
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        /// <summary>Stands in for the scene's agents: it reports the calls and teleports the threat where told.</summary>
        sealed class StubNavigation : IThreatNavigation
        {
            public bool HasNavMesh { get; set; } = true;
            public bool Refuse;
            public readonly List<string> Held = new List<string>();
            public readonly List<string> Released = new List<string>();
            public readonly List<Vec3> Targets = new List<Vec3>();
            public GdArray AvoidedCells;
            public double AvoidedCellSize;
            public Vec3 Reached = new Vec3(3.0, 0.0, 0.0);

            public bool TryAdvance(string instanceId, Vec3 current, Vec3 target, double speed, double delta, out Vec3 position)
            {
                position = current;
                Targets.Add(target);
                if (Refuse) return false;
                position = Reached;
                return true;
            }

            public void Hold(string instanceId) => Held.Add(instanceId);

            public void Release(string instanceId) => Released.Add(instanceId);

            public void SetAvoidedCells(GdArray worldPositions, double cellSize)
            {
                AvoidedCells = worldPositions;
                AvoidedCellSize = cellSize;
            }
        }

        /// <summary>A four-cell corridor: (0,0,0) - (4,0,0) - (8,0,0) in room "a", then (8,0,4) in room "b".</summary>
        static GdDict CorridorLayout() => new GdDict
        {
            { "cell_size", 4.0 },
            { "deck_height", 4.0 },
            {
                "rooms", GdArray.Of(
                    new GdDict
                    {
                        { "id", "a" },
                        {
                            "structural_placements", GdArray.Of(
                                new GdDict { { "module", "floor_1x1" }, { "world_position", GdArray.Of(0.0, 0.0, 0.0) } },
                                new GdDict { { "module", "floor_1x1" }, { "world_position", GdArray.Of(4.0, 0.0, 0.0) } },
                                new GdDict { { "module", "floor_1x1" }, { "world_position", GdArray.Of(8.0, 0.0, 0.0) } })
                        },
                    },
                    new GdDict
                    {
                        { "id", "b" },
                        { "structural_placements", GdArray.Of(new GdDict { { "module", "floor_1x1" }, { "world_position", GdArray.Of(8.0, 0.0, 4.0) } }) },
                    })
            },
        };

        static ThreatRuntime Runtime(StubNavigation navigation, out ThreatAIState threat)
        {
            var runtime = new ThreatRuntime { Navigation = navigation };
            runtime.ConfigureNavGraph(CorridorLayout());
            runtime.InjectValidationEncounter(GdArray.Of("stalker"), Vec3.Zero);
            threat = runtime.Threats[0];
            threat.WorldPosition = GdArray.Of(0.0, 0.0, 0.0);
            return runtime;
        }

        /// <summary>Drives one motion tick with the threat hunting a player standing at the far end.</summary>
        static void TickHunting(ThreatRuntime runtime, ThreatAIState threat, Vec3 playerPosition, double delta = 0.1)
        {
            threat.State = ThreatAIState.STATE_HUNT;
            runtime.TickThreats(delta, null, null, new GdDict(), playerPosition);
        }

        [Test]
        public void AttachedNavigationMovesTheThreatAndCoreStoresWhereItGot()
        {
            var navigation = new StubNavigation { Reached = new Vec3(4.0, 0.0, 0.0) };
            ThreatRuntime runtime = Runtime(navigation, out ThreatAIState threat);
            var player = new Vec3(8.0, 0.0, 0.0);

            TickHunting(runtime, threat, player);

            CollectionAssert.Contains(navigation.Targets, player, "the agent is sent after the player");
            Assert.AreEqual(new Vec3(4.0, 0.0, 0.0), new Vec3(V.F64(threat.WorldPosition[0]), V.F64(threat.WorldPosition[1]), V.F64(threat.WorldPosition[2])),
                "Core stores the position the agent reached");
            Assert.AreEqual("a", threat.RoomId, "the nav graph still resolves the room");
        }

        [Test]
        public void WithoutANavMeshTheNavGraphStillWalksTheThreat()
        {
            var navigation = new StubNavigation { HasNavMesh = false };
            ThreatRuntime runtime = Runtime(navigation, out ThreatAIState threat);

            TickHunting(runtime, threat, new Vec3(8.0, 0.0, 0.0), 0.5);

            Assert.IsEmpty(navigation.Targets, "a navigation without a NavMesh is never asked to move anything");
            double x = V.F64(threat.WorldPosition[0]);
            Assert.Greater(x, 0.0, "the nav graph stepped the threat along the corridor");
            Assert.Less(x, 8.0);
        }

        [Test]
        public void AnAgentThatCannotMoveFallsBackToTheNavGraph()
        {
            var navigation = new StubNavigation { Refuse = true };
            ThreatRuntime runtime = Runtime(navigation, out ThreatAIState threat);

            TickHunting(runtime, threat, new Vec3(8.0, 0.0, 0.0), 0.5);

            Assert.IsNotEmpty(navigation.Targets, "the agent was asked first");
            Assert.Greater(V.F64(threat.WorldPosition[0]), 0.0, "and the nav graph moved the threat when it refused");
        }

        [Test]
        public void AThreatThatIsNotWalkingHoldsItsAgentAndAKilledOneReleasesIt()
        {
            var navigation = new StubNavigation();
            ThreatRuntime runtime = Runtime(navigation, out ThreatAIState threat);

            // The player is standing on the threat, so it is in range: it attacks instead of walking.
            runtime.TickThreats(0.1, null, null, new GdDict(), new Vec3(0.0, 0.0, 0.0));
            CollectionAssert.Contains(navigation.Held, threat.InstanceId);
            Assert.IsEmpty(navigation.Targets, "a threat in range is never sent anywhere");

            threat.ApplyDamage(new GdDict { { "final_damage", 1000.0 } });
            runtime.TickThreats(0.1, null, null, new GdDict(), new Vec3(8.0, 0.0, 0.0));
            CollectionAssert.Contains(navigation.Released, threat.InstanceId, "a dead threat's agent is dropped");
        }

        [Test]
        public void BurningRoomsAreHandedOverAsCellsToAvoid()
        {
            var navigation = new StubNavigation();
            ThreatRuntime runtime = Runtime(navigation, out ThreatAIState _);

            runtime.UpdateNavDynamicCosts(new GdDict { { "b", 0.8 } }, new GdArray());

            Assert.IsNotNull(navigation.AvoidedCells);
            Assert.AreEqual(1, navigation.AvoidedCells.Count, "room b is one cell");
            Assert.AreEqual(new Vec3(8.0, 0.0, 4.0), navigation.AvoidedCells[0]);
            Assert.AreEqual(runtime.NavGraph.CellSize, navigation.AvoidedCellSize);

            runtime.UpdateNavDynamicCosts(new GdDict(), new GdArray());
            Assert.AreEqual(0, navigation.AvoidedCells.Count, "the avoidance clears with the fire");
        }
    }
}
