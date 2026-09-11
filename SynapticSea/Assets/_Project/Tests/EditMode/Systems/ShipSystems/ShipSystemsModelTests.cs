using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ShipSubcomponentTests
    {
        static ShipSubcomponent Damaged()
        {
            var sub = new ShipSubcomponent("reactor_core", new[] { "power_cell" }, new[] { "welder" }, 2, 10.0, 0.5);
            sub.Health = 0.2;
            return sub;
        }

        [Test]
        public void RoundTrip()
        {
            var sub = Damaged();
            GdDict summary = sub.GetSummary();
            var fresh = new ShipSubcomponent("reactor_core", new[] { "power_cell" }, new[] { "welder" }, 2, 10.0, 0.5);
            Assert.IsTrue(fresh.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, fresh.GetSummary()));
        }

        [Test]
        public void RepairGates_ThenSucceedsFasterWithSkill()
        {
            var sub = Damaged();
            Assert.IsFalse(sub.IsFunctional());
            Assert.AreEqual("missing_parts", sub.Repair(new GdArray(), GdArray.Of("welder"), 5).GetString("reason"));
            Assert.AreEqual("missing_tools", sub.Repair(GdArray.Of("power_cell"), new GdArray(), 5).GetString("reason"));
            Assert.AreEqual("insufficient_skill", sub.Repair(GdArray.Of("power_cell"), GdArray.Of("welder"), 1).GetString("reason"));
            GdDict ok = sub.Repair(GdArray.Of("power_cell"), GdArray.Of("welder"), 4);
            Assert.IsTrue(ok.GetBool("success"));
            Assert.AreEqual(10.0 / 1.2, ok.GetFloat("seconds"), 1e-12);
            Assert.IsTrue(sub.IsFunctional());
            Assert.AreEqual("already_functional", sub.Repair(new GdArray(), new GdArray(), 0).GetString("reason"));
        }
    }

    public class ShipSystemTests
    {
        static ShipSystem Build()
        {
            var system = new ShipSystem("life_support", new[] { "power" });
            system.AddSubcomponent(new ShipSubcomponent("air_recycler", null, null, 0, 5.0, 0.5));
            system.AddSubcomponent(new ShipSubcomponent("co2_scrubber", null, null, 0, 5.0, 0.5));
            return system;
        }

        [Test]
        public void RoundTrip()
        {
            var system = Build();
            system.GetSubcomponent("co2_scrubber").Health = 0.3;
            GdDict summary = system.GetSummary();
            var fresh = Build();
            Assert.IsTrue(fresh.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, fresh.GetSummary()));
        }

        [Test]
        public void HealthIsWeakestLink()
        {
            var system = Build();
            Assert.IsTrue(system.IsSelfFunctional());
            Assert.AreEqual(1.0, system.Health());
            system.GetSubcomponent("co2_scrubber").Health = 0.1;
            Assert.IsFalse(system.IsSelfFunctional());
            Assert.AreEqual(0.1, system.Health(), 1e-12);
            Assert.IsNull(system.GetSubcomponent("nope"));
        }
    }

    public class ModuleIntegrityStateTests
    {
        static GdDict Config() => new GdDict
        {
            { "module_id", "wall_a" },
            { "kind", "wall_straight_1x1" },
            { "base_integrity", 1.0 },
            { "material_composition", new GdDict { { "scrap_metal", 4L } } },
        };

        [Test]
        public void RoundTrip()
        {
            var m = new ModuleIntegrityState();
            m.Configure(Config());
            m.ApplyDamage(0.3);
            GdDict summary = m.GetSummary();
            var fresh = new ModuleIntegrityState();
            Assert.IsTrue(fresh.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, fresh.GetSummary()));
        }

        [Test]
        public void DamageWalksTheStateMachine()
        {
            var m = new ModuleIntegrityState();
            m.Configure(Config());
            Assert.AreEqual(ModuleIntegrityState.STATE_INTACT, m.State);
            Assert.IsTrue(m.IsPristine());
            Assert.AreEqual(ModuleIntegrityState.STATE_DAMAGED, m.ApplyDamage(0.3));
            Assert.AreEqual(ModuleIntegrityState.STATE_BREACHED, m.ApplyDamage(0.4));
            Assert.AreEqual(ModuleIntegrityState.STATE_DESTROYED, m.ApplyDamage(0.5));
            Assert.AreEqual(ModuleIntegrityState.STATE_DESTROYED, m.Repair(1.0));
        }
    }

    public class ModuleDamageRouterTests
    {
        /// <summary>Minimal stand-in for ModuleIntegrityMap (its own port is outside this batch).</summary>
        sealed class FakeMap : IModuleIntegrityMap
        {
            readonly SortedDictionary<string, ModuleIntegrityState> _modules = new SortedDictionary<string, ModuleIntegrityState>(System.StringComparer.Ordinal);

            public ModuleIntegrityState Ensure(string id, string kind = "", string roomId = "")
            {
                if (_modules.TryGetValue(id, out var existing)) return existing;
                var m = new ModuleIntegrityState();
                m.Configure(new GdDict { { "module_id", id }, { "kind", kind }, { "material_composition", new GdDict() }, { "room_id", roomId } });
                _modules[id] = m;
                return m;
            }

            public string GetState(string moduleId) =>
                _modules.TryGetValue(moduleId, out var m) ? m.State : ModuleIntegrityState.STATE_INTACT;

            public string ApplyDamage(string moduleId, double amount, string kind = "") => Ensure(moduleId, kind).ApplyDamage(amount);

            public IEnumerable<string> ModuleIds() => new List<string>(_modules.Keys);

            public ModuleIntegrityState GetModule(string moduleId) => _modules.TryGetValue(moduleId, out var m) ? m : null;
        }

        [Test]
        public void SourcesRouteDamage_AndUnknownSourceRejected()
        {
            Assert.AreEqual(4, ModuleDamageRouter.KnownSources().Count);
            var map = new FakeMap();
            map.Ensure("eng/wall_a", "wall_straight_1x1", "eng");
            GdDict fire = ModuleDamageRouter.Apply(map, "eng/wall_a", ModuleDamageRouter.SOURCE_FIRE, 0.3);
            Assert.IsTrue(fire.GetBool("ok"));
            Assert.AreEqual("intact", fire.GetString("state_before"));
            Assert.AreNotEqual(ModuleIntegrityState.STATE_INTACT, map.GetState("eng/wall_a"));

            GdDict threat = ModuleDamageRouter.ApplyThreatStructureHit(map, "cor/wall_t", 0.4);
            Assert.AreEqual(ModuleDamageRouter.SOURCE_THREAT, threat.GetString("source"));

            GdDict resisted = ModuleDamageRouter.Apply(map, "x/y", ModuleDamageRouter.SOURCE_TOOL, -1.0, "", 0.5);
            Assert.AreEqual(0.5, resisted.GetFloat("amount"), 1e-12);

            GdDict bad = ModuleDamageRouter.Apply(map, "eng/wall_a", "laser_beams", 1.0);
            Assert.IsFalse(bad.GetBool("ok"));
            Assert.AreEqual("unknown_source", bad.GetString("reason"));
        }

        [Test]
        public void DecompressionDamagesRegisteredRoomModules()
        {
            var map = new FakeMap();
            map.Ensure("eng/wall_b", "wall_straight_1x1", "eng");
            var layout = new GdDict
            {
                {
                    "rooms", GdArray.Of(new GdDict
                    {
                        { "id", "eng" },
                        { "room_role", "engineering" },
                        { "structural_placements", GdArray.Of(new GdDict { { "name", "wall_b" }, { "module_id", "wall_straight_1x1" }, { "world_position", GdArray.Of(0L, 0L, 0L) } }) },
                    })
                },
            };
            GdArray changed = ModuleDamageRouter.ApplyDecompressionToCompartment(
                map, layout, "engineering", new GdDict { { "engineering", "engineering" } }, 0.5);
            Assert.IsTrue(V.VariantEquals(GdArray.Of("eng/wall_b"), changed));
            Assert.AreEqual(ModuleIntegrityState.STATE_DAMAGED, map.GetState("eng/wall_b"));
        }
    }

    public class PowerGridStateTests
    {
        static GdDict Config() => new GdDict
        {
            { "total_supply_units", 100.0 },
            { "min_operational_ratio", 0.5 },
            { "subsystem_order", GdArray.Of("life_support", "propulsion", "stations", "lights", "sustenance") },
            {
                "baseline_demand_units", new GdDict
                {
                    { "life_support", 22.0 }, { "propulsion", 30.0 }, { "stations", 12.0 }, { "lights", 8.0 }, { "sustenance", 10.0 },
                }
            },
        };

        [Test]
        public void BlackoutOverload_AndRoundTrip()
        {
            var grid = new PowerGridState();
            grid.Configure(Config());
            grid.SetManualRoute("propulsion", 0.0);
            grid.Rebalance(1.0);
            Assert.IsFalse(grid.IsSystemPowered("propulsion"));
            grid.SetManualRoute("propulsion", 30.0);
            grid.SetManualRoute("sustenance", 20.0);
            grid.Rebalance(0.7);
            Assert.IsTrue(grid.Overloaded);
            Assert.Greater(grid.GetAllocationRatio("life_support"), 0.0);

            GdDict snap = grid.GetSummary();
            var restored = new PowerGridState();
            restored.Configure(Config());
            restored.ApplySummary(snap);
            Assert.AreEqual(GdJson.Stringify(snap), GdJson.Stringify(restored.GetSummary()));
            Assert.IsTrue(V.VariantEquals(snap, restored.GetSummary()));
        }

        [Test]
        public void StatusLines()
        {
            var grid = new PowerGridState();
            grid.Configure(Config());
            grid.Rebalance(0.7);
            var lines = grid.GetStatusLines();
            Assert.AreEqual("Grid 70/100 units OVERLOAD", lines[0]);
            Assert.AreEqual("Grid life_support 100% ON", lines[1]);
        }
    }

    public class ShipModificationStateTests
    {
        [SetUp]
        public void SetUp() => CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        [Test]
        public void InstallBudgetPlatingUninstall_AndRoundTrip()
        {
            var mod = new ShipModificationState();
            mod.Configure(new GdDict());
            Assert.AreEqual(100.0, mod.PowerSupply);
            Assert.AreEqual(82.0, mod.PowerDemandBaseline, 1e-9);
            var inv = new GdDict { { "reactor_console", 2L }, { "machinery_block", 1L }, { "hull_plate_kit", 1L } };
            Assert.IsTrue(mod.Install("hub_wall_0", "reactor_console", "reactor_console", inv, 8.0, 15.0, "derelict_a").GetBool("ok"));
            Assert.AreEqual(1L, inv["reactor_console"]);
            GdDict huge = mod.Install("hub_wall_1", "machinery_block", "machinery_block", inv, 9999.0, 25.0);
            Assert.AreEqual("power_budget", huge.GetString("reason"));
            Assert.IsTrue(mod.Install("hub_center_0", "machinery_block", "machinery_block", inv, 10.0, 25.0, "captured").GetBool("ok"));
            Assert.IsFalse(inv.Has("machinery_block"));
            Assert.IsTrue(mod.Install("hull_plate_0", "hull_plating", "hull_plate_kit", inv, 0.0, 5.0, "salvage", true).GetBool("ok"));
            Assert.AreEqual(0.05, mod.HullPlatingBonus, 1e-12);
            Assert.AreEqual(0.1, mod.StructureDamageResist(), 1e-12);
            Assert.IsTrue(mod.Uninstall("hub_wall_0", inv).GetBool("ok"));
            Assert.AreEqual(2L, inv["reactor_console"]);
            Assert.AreEqual(2, mod.InstalledCount());

            GdDict snap = mod.GetSummary();
            var mod2 = new ShipModificationState();
            Assert.IsTrue(mod2.ApplySummary(snap));
            Assert.IsTrue(V.VariantEquals(snap, mod2.GetSummary()));
        }
    }
}
