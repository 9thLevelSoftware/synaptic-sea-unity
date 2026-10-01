using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class AssemblyMobilityTests
    {
        [SetUp] public void Setup() => CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        [TearDown] public void Cleanup() => CatalogRegistry.Clear();
        static ShipInstance Ship(string id, int cells)
        {
            var manager = new ShipSystemsManager(); manager.Configure(manager.LoadDefinitions(), 0, 17);
            var ship = ShipInstance.Create(id, "", null, manager, new FakeRoot());
            var floors = new GdArray();
            for (int i = 0; i < cells; i++) floors.Add(new GdDict { { "position", new Vec3(i * 4, 0, 0) } });
            ship.BuiltLayout = new GdDict { { "structural_plan", new GdDict { { "floor_placements", floors } } } };
            ship.Mobility = AssemblyMobility.CreateSpecification(ship, true);
            return ship;
        }
        static void Join(ShipInstance host, ShipInstance child, string kind)
        {
            child.ParentShip = host; host.DockedShips.Add(child);
            child.DockingPorts.Add(new GdDict { { "connection_kind", kind } });
        }
        [Test] public void CarriedCraftAddsItsMassButOnlySecuredEnginesSupplyAssemblyCapacity()
        {
            var home = Ship("home", 10); var boat = Ship("boat", 3); Join(home, boat, "moored");
            var carried = AssemblyMobility.Evaluate(home, 0);
            Assert.AreEqual(20800, carried.GetFloat("total_mass_kg"));
            Assert.AreEqual(20000, carried.GetFloat("supported_kg"));
            Assert.IsFalse(carried.GetBool("success"));
            Assert.AreEqual("carried_craft_payload", ((GdDict)carried.GetArrayOrEmpty("engines")[1]).GetString("excluded_reason"));
            ((GdDict)boat.DockingPorts[0])["connection_kind"] = "secured";
            var secured = AssemblyMobility.Evaluate(home, 200);
            Assert.AreEqual(21000, secured.GetFloat("total_mass_kg"));
            Assert.AreEqual(26000, secured.GetFloat("supported_kg")); Assert.IsTrue(secured.GetBool("success"));
            var independent = AssemblyMobility.Evaluate(boat, 200);
            Assert.AreEqual(5000, independent.GetFloat("total_mass_kg"), "ancestors are not carried by an independent craft");
        }
        [Test] public void LocalDamageCannotBePaidForByUnconnectedOrSharedSystems()
        {
            var root = Ship("root", 4); var child = Ship("child", 4); Join(root, child, "secured");
            root.SystemsManager.DamageSubcomponent("power", "reactor_core", 1);
            var damaged = AssemblyMobility.Evaluate(root, 0);
            Assert.IsFalse(damaged.GetBool("success"));
            child.SystemsManager = root.SystemsManager;
            Assert.AreEqual("shared_systems_owner:child", AssemblyMobility.Evaluate(root, 0).GetString("reason"));
        }
        [Test] public void DuplicateInstallationAndInvalidMassFailClosed()
        {
            var root = Ship("root", 4); var child = Ship("child", 4); Join(root, child, "secured");
            child.Mobility["engine_id"] = root.Mobility.GetString("engine_id");
            Assert.AreEqual("duplicate_engine_owner", AssemblyMobility.Evaluate(root, 0).GetString("reason"));
            child.Mobility["engine_id"] = "propulsion:child"; child.Mobility["dry_mass_kg"] = double.NaN;
            Assert.IsFalse(AssemblyMobility.Evaluate(root, 0).GetBool("success"));
            Assert.IsFalse(AssemblyMobility.Evaluate(root, double.PositiveInfinity).GetBool("success"));
        }
        [Test] public void CompiledCellsCountOnceAcrossDecksRegardlessOfVisualTiles()
        {
            var ship = Ship("root", 1);
            var floors = ship.BuiltLayout.GetDictOrEmpty("structural_plan").GetArrayOrEmpty("floor_placements");
            floors.Add(new GdDict { { "position", Vec3.Zero } }); floors.Add(new GdDict { { "position", new Vec3(0, 4, 0) } });
            Assert.AreEqual(32, AssemblyMobility.CreateSpecification(ship, false).GetFloat("area_m2"));
        }
        [Test] public void SerializedGeneratedVectorsRetainAuthoritativeAreaAfterDocumentRoundTrip()
        {
            var ship = Ship("generated", 5);
            ship.BuiltLayout = (GdDict)GdJson.Parse(GdJson.Stringify(ship.BuiltLayout));
            Assert.AreEqual(80, AssemblyMobility.CreateSpecification(ship, true).GetFloat("area_m2"));
            ship.BuiltLayout.GetDictOrEmpty("structural_plan").GetArrayOrEmpty("floor_placements").Add(
                new GdDict { { "world_position", "(0.0, 4.0, 0.0)" } });
            Assert.AreEqual(96, AssemblyMobility.CreateSpecification(ship, true).GetFloat("area_m2"));
        }
        [Test] public void ExteriorPlanIsDeterministicAndCannotBeForgedUsingOnlyItsSiteName()
        {
            var home = Ship("home", 6); var wreck = Ship("wreck", 5);
            Assert.IsTrue(HomeJoinPlanner.TryPlan(home, wreck, out var h, out var m, out var reason), reason);
            Assert.IsTrue(HomeJoinPlanner.TryPlan(home, wreck, out var again, out _, out _));
            Assert.AreEqual(h.GetString("site_id"), again.GetString("site_id"));
            Assert.IsTrue(DockingManager.Dock(home, wreck, DockingManager.HostPortToWorld(home, h), m).GetBool("success"));
            Assert.IsTrue(HomeJoinPlanner.Validate(home, wreck, h, m, out reason), reason);
            var forged = h.DeepCopy(); forged["position"] = new Vec3(100, 0, 100);
            Assert.IsFalse(HomeJoinPlanner.Validate(home, wreck, forged, m, out reason));
            Assert.AreEqual("invalid_exterior_site", reason);
        }
    }
}
