using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>
    /// Reduced-quality repair (Phase 1.4) leaves an under-skilled part below full health, and the lifeboat then has to carry
    /// itself plus its kit with that health. These tests prove a survivor of ANY repair skill who has repaired every
    /// power/navigation/propulsion part can still leave: the capacity check must never turn "operational" into "stranded".
    /// </summary>
    public class TravelCapacityTests
    {
        const double KitKg = 30.0;
        static readonly string[] TravelSystems = { "power", "navigation", "propulsion" };

        [SetUp] public void Setup() => CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        [TearDown] public void Cleanup() => CatalogRegistry.Clear();

        /// <summary>Same rule as RunSession.ApplyLifeboatOpeningDamage: propulsion healthy except nav_linkage.</summary>
        static void OpeningDamage(ShipSystemsManager manager)
        {
            ShipSystem propulsion = manager.GetSystem("propulsion");
            foreach (ShipSubcomponent sub in propulsion.Subcomponents) sub.Health = 1.0;
            propulsion.GetSubcomponent("nav_linkage").Health = ShipSystemsManager.DAMAGED_HEALTH;
        }

        static ShipSystemsManager OpeningManager(long condition, long seed)
        {
            var manager = new ShipSystemsManager();
            manager.Configure(manager.LoadDefinitions(), condition, seed);
            OpeningDamage(manager);
            return manager;
        }

        /// <summary>The home objectives force-repair these to full health (RunSession.OBJECTIVE_REPAIR_MAP).</summary>
        static void ObjectiveRepairs(ShipSystemsManager manager)
        {
            manager.GetSystem("power").GetSubcomponent("power_distribution").Health = 1.0;
            manager.GetSystem("power").GetSubcomponent("battery_cells").Health = 1.0;
            manager.GetSystem("power").GetSubcomponent("reactor_core").Health = 1.0;
            manager.GetSystem("navigation").GetSubcomponent("nav_computer").Health = 1.0;
        }

        /// <summary>Repairs every broken part of the travel systems at <paramref name="skill"/>, with all parts and tools in hand.</summary>
        static int RepairTravelSystems(ShipSystemsManager manager, long skill)
        {
            var parts = new GdArray(); var tools = new GdArray(); int repaired = 0;
            foreach (string id in TravelSystems)
                foreach (ShipSubcomponent sub in manager.GetSystem(id).Subcomponents)
                {
                    foreach (string p in sub.RequiredParts) parts.Add(p);
                    foreach (string t in sub.RequiredTools) tools.Add(t);
                }
            foreach (string id in TravelSystems)
                foreach (ShipSubcomponent sub in manager.GetSystem(id).Subcomponents)
                    if (!sub.IsFunctional() && sub.Repair(parts, tools, skill).GetBool("success")) repaired++;
            return repaired;
        }

        static ShipInstance Boat(ShipSystemsManager manager)
        {
            var ship = ShipInstance.Create("boat", "", null, manager, new FakeRoot());
            var floors = new GdArray();
            for (int i = 0; i < 3; i++) floors.Add(new GdDict { { "position", new Vec3(i * 4, 0, 0) } });
            ship.BuiltLayout = new GdDict { { "structural_plan", new GdDict { { "floor_placements", floors } } } };
            ship.Mobility = AssemblyMobility.CreateSpecification(ship, true);
            return ship;
        }

        static GdDict Evaluate(ShipSystemsManager manager) => AssemblyMobility.Evaluate(Boat(manager), KitKg);

        static string Describe(ShipSystemsManager manager, GdDict report) =>
            report.GetString("reason") + " supported=" + report.GetFloat("supported_kg") + " mass=" + report.GetFloat("total_mass_kg")
            + " propulsion=" + manager.GetSystem("propulsion").Health() + " power=" + manager.GetSystem("power").Health();

        [TestCase(0L)] [TestCase(1L)] [TestCase(2L)] [TestCase(3L)] [TestCase(4L)]
        public void GoldenHubOpeningStateCanLeaveAfterTheSurvivorRepairsWhatObjectivesDoNotCover(long skill)
        {
            var manager = OpeningManager(1, 17);
            ObjectiveRepairs(manager);
            RepairTravelSystems(manager, skill);
            Assert.IsTrue(manager.IsOperational("propulsion"), "the repairs make the flight path operational");
            var report = Evaluate(manager);
            Assert.IsTrue(report.GetBool("success"), "repair skill " + skill + ": " + Describe(manager, report));
        }

        [TestCase(0L)] [TestCase(1L)] [TestCase(2L)] [TestCase(3L)] [TestCase(4L)]
        public void GoldenHubOpeningStateCanLeaveWithoutAnyFreeObjectiveRepairs(long skill)
        {
            var manager = OpeningManager(1, 17);
            RepairTravelSystems(manager, skill);
            Assert.IsTrue(manager.IsOperational("propulsion"));
            var report = Evaluate(manager);
            Assert.IsTrue(report.GetBool("success"), "repair skill " + skill + ": " + Describe(manager, report));
        }

        [TestCase(0L)] [TestCase(1L)] [TestCase(2L)] [TestCase(3L)] [TestCase(4L)]
        public void EveryDamagedHomeSeedCanLeaveAtEveryRepairSkill(long skill)
        {
            var failures = new List<string>(); int tested = 0;
            for (long seed = 1; seed <= 200; seed++)
            {
                var manager = OpeningManager(1, seed);
                RepairTravelSystems(manager, skill);
                if (!manager.IsOperational("propulsion")) { failures.Add(seed + ":not_operational"); continue; }
                tested++;
                var report = Evaluate(manager);
                if (!report.GetBool("success")) failures.Add(seed.ToString());
            }
            TestContext.WriteLine("skill " + skill + ": " + failures.Count + "/200 seeds refuse travel capacity (" + tested + " operational)");
            Assert.AreEqual(0, failures.Count, "repair skill " + skill + " strands the survivor on " + failures.Count + "/200 seeds: "
                + string.Join(",", failures.Take(20)));
        }

        [Test]
        public void PristineCapacityIsUnchanged()
        {
            var manager = new ShipSystemsManager(); manager.Configure(manager.LoadDefinitions(), 0, 17);
            var report = Evaluate(manager);
            Assert.AreEqual(6000, report.GetFloat("supported_kg"), 1e-9, "an undamaged lifeboat keeps its full rating");
            Assert.IsTrue(report.GetBool("success"));
        }

        [Test]
        public void DamageStillCostsCapacityButOnlyBoundedly()
        {
            var manager = new ShipSystemsManager(); manager.Configure(manager.LoadDefinitions(), 0, 17);
            double full = Evaluate(manager).GetFloat("supported_kg");
            manager.GetSystem("propulsion").GetSubcomponent("thruster_array").Health = 0.5;
            double worst = Evaluate(manager).GetFloat("supported_kg");
            Assert.Less(worst, full, "a worse engine still carries less");
            Assert.Greater(worst, 4800 + KitKg, "but a barely-operational engine can still carry the boat and its kit");
        }

        [Test]
        public void ANonOperationalShipIsStillExcluded()
        {
            var manager = new ShipSystemsManager(); manager.Configure(manager.LoadDefinitions(), 0, 17);
            manager.GetSystem("propulsion").GetSubcomponent("thruster_array").Health = 0.2;
            var report = Evaluate(manager);
            Assert.IsFalse(report.GetBool("success"));
        }
    }
}
