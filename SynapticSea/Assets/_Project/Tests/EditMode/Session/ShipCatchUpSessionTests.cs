using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>Phase 1.2: an absent derelict catches up on revisit in real-equivalent seconds, including its own fire.</summary>
    public class ShipCatchUpSessionTests
    {
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

        static ShipInstance BurningDerelict(string id, bool breachEngineering = false)
        {
            var systems = new ShipSystemsManager();
            systems.Configure(systems.LoadDefinitions(), 0, 17);
            ShipInstance ship = ShipInstance.Create(id, id, null, systems, null);
            ship.GetHull().Configure(new GdDict
            {
                {
                    "compartments", GdArray.Of(
                        new GdDict { { "compartment_id", "bridge" }, { "health", 1.0 }, { "breach_open", false } },
                        new GdDict { { "compartment_id", "engineering" }, { "health", 1.0 }, { "breach_open", breachEngineering } })
                },
            });
            // A derelict's web would breach the hull during catch-up and put the fire out; keep this ship web-free.
            ship.GetWeb().Configure(new GdDict { { "attached_to_web", false }, { "seed_coverage", 0.0 } });
            ship.GetFire().Configure(new GdDict
            {
                { "compartments", GdArray.Of("bridge", "engineering") },
                { "adjacency", new GdDict { { "bridge", GdArray.Of("engineering") }, { "engineering", GdArray.Of("bridge") } } },
            });
            ship.GetFire().Ignite("bridge", 1.0);
            ship.LastSimTime = 0.0;
            return ship;
        }

        [Test]
        public void AbsentShipFire_SpreadsAndDamagesItsSystems()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            ShipInstance ship = BurningDerelict("fire_spreads");
            double navBefore = ship.SystemsManager.GetSystem("navigation").Health();

            s.WorldTime = 600.0;
            s.CatchUpShip(ship);

            Assert.IsTrue(ship.GetFire().IsBurning("engineering"), "the fire spread to the oxygenated neighbour while away");
            Assert.Less(ship.SystemsManager.GetSystem("navigation").Health(), navBefore, "burning bridge damages navigation");
            Assert.AreEqual(600.0, ship.LastSimTime);
        }

        [Test]
        public void AbsentShipFire_GoesOutWhereTheHullIsBreached()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            ShipInstance ship = BurningDerelict("fire_breached", breachEngineering: true);

            s.WorldTime = 600.0;
            s.CatchUpShip(ship);

            Assert.IsFalse(ship.GetFire().IsBurning("engineering"), "a breached compartment cannot hold a fire");
            Assert.IsTrue(ship.GetFire().IsBurning("bridge"), "the unbreached source keeps burning (no suppression power on a derelict)");
        }

        [Test]
        public void AbsentShipFire_AtScale60_MatchesTheSameRealEquivalentAtScaleOne()
        {
            var rigA = SessionHarness.CreateGolden();
            ShipInstance a = BurningDerelict("fire_a");
            rigA.Session.WorldTime = 600.0;
            rigA.Session.CatchUpShip(a);

            var rigB = SessionHarness.CreateGolden();
            rigB.Session.GameClock.SetScale(60.0);
            ShipInstance b = BurningDerelict("fire_b");
            rigB.Session.WorldTime = 600.0 * 60.0;
            rigB.Session.CatchUpShip(b);

            Assert.IsTrue(V.VariantEquals(a.GetFire().GetSummary(), b.GetFire().GetSummary()),
                "36000 game seconds at 60x is 600 real-equivalent seconds: identical fire state");
            Assert.AreEqual(
                a.SystemsManager.GetSystem("navigation").Health(),
                b.SystemsManager.GetSystem("navigation").Health(), 1e-12);
        }

        [Test]
        public void ShipWithoutFire_CatchUpDoesNotCreateOrIgniteOne()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            var systems = new ShipSystemsManager();
            systems.Configure(systems.LoadDefinitions(), 0, 17);
            ShipInstance ship = ShipInstance.Create("no_fire", "no_fire", null, systems, null);
            ship.LastSimTime = 0.0;

            s.WorldTime = 600.0;
            s.CatchUpShip(ship);

            Assert.IsNull(ship.Fire, "catch-up never allocates a fire model for a ship that never had one");
        }

        [Test]
        public void Spoilage_FollowsGameTimeWhereverTheFoodIs()
        {
            // Spoilage is one run-level model keyed by item id, ticked on game time in every location, so cargo left
            // aboard a derelict ages with the run clock and needs no per-ship catch-up.
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.GameClock.SetScale(60.0);
            s.SpoilageState.AddFood("ration_pack", new GdDict { { "spoilage_seconds", 3600.0 } });
            FoodState food = s.SpoilageState.GetFood("ration_pack");
            Assert.AreEqual(0.0, food.ElapsedSeconds);

            s.StageFood(3600.0);

            Assert.Greater(food.ElapsedSeconds, 0.0, "game seconds age the food");
            Assert.LessOrEqual(food.ElapsedSeconds, 3600.0);
        }
    }
}
