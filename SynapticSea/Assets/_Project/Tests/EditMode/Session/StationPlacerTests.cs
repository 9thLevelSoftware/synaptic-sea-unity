using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>Home crafting and production stations by room role, clear of the start and of every other interaction area.</summary>
    public class StationPlacerTests
    {
        const double Radius = RunSession.STATION_INTERACTION_RADIUS;
        const double SpawnHeight = RunSession.PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR;
        static readonly string[] AllKinds = { "fabricator", "medbay", "kitchen", "synthesizer", "workbench", "salvage", "hydroponics", "water_recycler" };

        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Engine = _previousEngine;
        }

        static GdDict GoldenLayout() => CatalogRegistry.LoadDict(SessionHarness.GoldenDir + "layout.json");

        /// <summary>The golden airlock's first floor cell centre at standing height (the start marker sits in the airlock).</summary>
        static Vec3 AirlockStart() => new Vec3(0.0f, (float)SpawnHeight, 0.0f);

        static GdDict RoomById(GdDict layout, string id) =>
            layout.GetArrayOrEmpty("rooms").Cast<GdDict>().Single(r => r.GetString("id") == id);

        static bool InsideRoom(GdDict room, Vec3 p)
        {
            foreach (GdDict placement in room.GetArrayOrEmpty("structural_placements").Cast<GdDict>())
            {
                GdArray wp = placement.GetArrayOrEmpty("world_position");
                if (wp.Count < 3)
                    continue;
                if (System.Math.Abs(p.X - V.F64(wp[0])) <= 2.0 && System.Math.Abs(p.Z - V.F64(wp[2])) <= 2.0 && System.Math.Abs(p.Y - SpawnHeight - V.F64(wp[1])) < 1e-4)
                    return true;
            }
            return false;
        }

        [Test]
        public void GoldenLayout_PlacesStationsInTheirRooms_ClearOfTheStartAndOfEachOther()
        {
            GdDict layout = GoldenLayout();
            var occupied = new List<StationPlacer.Occupied> { new StationPlacer.Occupied(AirlockStart(), Radius) };
            List<StationPlacer.Placement> placements = StationPlacer.Place(layout, AllKinds, occupied, new List<Vec3> { AirlockStart() }, Radius, SpawnHeight);

            Assert.AreEqual(AllKinds.Length, placements.Count);
            CollectionAssert.AreEqual(AllKinds, placements.Select(p => p.Kind).ToArray(), "kinds keep their order");
            foreach (StationPlacer.Placement p in placements)
            {
                Assert.IsNotEmpty(p.RoomId, p.Kind + " found a room spot");
                GdDict room = RoomById(layout, p.RoomId);
                Assert.IsFalse(StationPlacer.PASSAGE_ROLES.Contains(room.GetString("room_role")), p.Kind + " is not in a passage");
                Assert.IsTrue(InsideRoom(room, p.LocalPosition), p.Kind + " stands inside " + p.RoomId);
                Assert.GreaterOrEqual(p.LocalPosition.DistanceTo(AirlockStart()), 2 * Radius - 1e-4, p.Kind + " keeps its area off the start");
                foreach (StationPlacer.Placement other in placements.Where(o => o != p))
                    Assert.GreaterOrEqual(p.LocalPosition.DistanceTo(other.LocalPosition), 2 * Radius - 1e-4, p.Kind + " vs " + other.Kind);
            }
            Assert.AreEqual("maintenance_01", placements.Single(p => p.Kind == "fabricator").RoomId);
            Assert.AreEqual("medbay_01", placements.Single(p => p.Kind == "medbay").RoomId);
            Assert.AreEqual("cargo_01", placements.Single(p => p.Kind == "salvage").RoomId);
        }

        [Test]
        public void GoldenLayout_NeverUsesADoorwayCell()
        {
            GdDict layout = GoldenLayout();
            var doorways = new HashSet<(long, long)>();
            foreach (GdDict portal in layout.GetArrayOrEmpty("portals").Cast<GdDict>())
            {
                foreach (string key in new[] { "from_cell", "to_cell" })
                {
                    GdArray c = portal.GetArrayOrEmpty(key);
                    doorways.Add((V.I64(c[0]), V.I64(c[1])));
                }
            }
            List<StationPlacer.Placement> placements = StationPlacer.Place(layout, AllKinds, new List<StationPlacer.Occupied>(), new List<Vec3>(), Radius, SpawnHeight);
            foreach (StationPlacer.Placement p in placements)
            {
                var cell = ((long)System.Math.Floor((p.LocalPosition.X + 2.0) / 4.0), (long)System.Math.Floor((p.LocalPosition.Z + 2.0) / 4.0));
                CollectionAssert.DoesNotContain(doorways, cell, p.Kind + " avoids doorway cells");
            }
        }

        [Test]
        public void Placement_IsDeterministic_AndFallsBackWhenNoRoomIsFree()
        {
            GdDict layout = GoldenLayout();
            List<StationPlacer.Placement> a = StationPlacer.Place(layout, AllKinds, new List<StationPlacer.Occupied>(), new List<Vec3>(), Radius, SpawnHeight);
            List<StationPlacer.Placement> b = StationPlacer.Place(layout, AllKinds, new List<StationPlacer.Occupied>(), new List<Vec3>(), Radius, SpawnHeight);
            CollectionAssert.AreEqual(a.Select(p => p.LocalPosition).ToArray(), b.Select(p => p.LocalPosition).ToArray());

            var legacy = new List<Vec3> { new Vec3(100f, 0f, 0f), new Vec3(200f, 0f, 0f) };
            List<StationPlacer.Placement> fallback = StationPlacer.Place(new GdDict(), new[] { "fabricator", "medbay", "kitchen" },
                new List<StationPlacer.Occupied>(), legacy, Radius, SpawnHeight);
            Assert.AreEqual(3, fallback.Count);
            Assert.AreEqual(legacy[0], fallback[0].LocalPosition);
            Assert.AreEqual(legacy[1], fallback[1].LocalPosition);
            Assert.AreEqual(legacy[0], fallback[2].LocalPosition, "no free legacy spot left: the legacy index choice");
            Assert.IsTrue(fallback.All(p => p.RoomId == ""));
        }

        [Test]
        public void GoldenBoot_StationsNeverOverlapAnotherInteractionAreaOrTheStart()
        {
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            Assert.AreEqual(RunSession.CRAFTING_STATION_KINDS.Count, s.CraftingStations.Count);
            Assert.AreEqual(2, s.ProductionStations.Count);
            Vec3 start = s.Loader.GetStartTransform().Origin;
            var stations = new List<SessionInteractable>();
            stations.AddRange(s.CraftingStations);
            stations.AddRange(s.ProductionStations);
            // Golden's rooms are small and already hold objectives, loot and the hub controls: five stations find a room spot,
            // the rest take free legacy positions (still clear of every interaction area).
            CollectionAssert.IsSubsetOf(new[] { "STATION PLACED kind=fabricator room=maintenance_01", "STATION PLACED kind=medbay room=medbay_01" },
                rig.Log.Infos.Where(m => m.StartsWith("STATION PLACED")).ToList());
            foreach (SessionInteractable st in stations)
            {
                Assert.AreSame(s.HomeShip.SceneRoot, st.Parent);
                Assert.GreaterOrEqual(st.LocalPosition.DistanceTo(start), Radius, st.NodeName + " is out of reach from the start");
                foreach (SessionInteractable other in s.LiveNodes.Where(n => n.IsValid && n != st))
                {
                    Assert.GreaterOrEqual(st.LocalPosition.DistanceTo(other.LocalPosition), st.InteractionRadius + other.InteractionRadius - 1e-4,
                        st.Kind + " at " + st.LocalPosition + " overlaps " + other.Kind + " at " + other.LocalPosition);
                }
            }
        }
    }
}
