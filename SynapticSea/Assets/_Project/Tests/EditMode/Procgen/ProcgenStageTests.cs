using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>Behaviour checks for the Wave 2 procgen stages (the Godot parity replay lives in ProcgenStageParityTests).</summary>
    public class ProcgenStageTests
    {
        CollectingLog _log;

        [SetUp]
        public void SetUp()
        {
            _log = new CollectingLog();
            CoreServices.Log = _log;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
            StructuralPlacer.ResetSharedKitCatalog();
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
            StructuralPlacer.ResetSharedKitCatalog();
            CoreServices.Log = NullLog.Instance;
        }

        static GdDict Derelict() => CatalogRegistry.LoadDict("res://data/procgen/archetypes/derelict.json");

        static GdDict CaptureLayout(string tag) =>
            GdJson.ParseDict(Fixtures.ReadText($"godot/procgen/layout_{tag}.fullprec.json"));

        [Test]
        public void TemplateSelector_ExplicitTemplateLoads_MissingTemplateReturnsNull()
        {
            var sel = new TemplateSelector();
            Assert.AreEqual("vault", sel.Select(new ShipBlueprint(), new GdDict { { "template", "vault" } }).Id);
            Assert.IsNull(sel.Select(new ShipBlueprint(), new GdDict { { "template", "no_such_template" } }));
            Assert.That(_log.Errors, Has.Some.Contains("template file not found"));
            Assert.AreEqual(14, sel.CatalogSizeOnDisk());
            Assert.AreEqual(sel.AvailableTemplates(true, true), new List<string>(TemplateSelector.EXTENDED_TEMPLATES));
        }

        [Test]
        public void RoomAssigner_DerelictGuaranteesDock_UniqueIds_SameSeedSamePlan()
        {
            var template = new TemplateSelector().Select(new ShipBlueprint(), new GdDict { { "template", "derelict_a" } });
            var bp = new ShipBlueprint(ShipBlueprint.Size.Small, ShipBlueprint.Condition.Wrecked, 42);
            List<GdDict> plan = new RoomAssigner().AssignWithSelector(template, bp, Derelict(), new RoomVariantSelector(), "breach_field");

            var ids = new HashSet<string>();
            bool hasDock = false;
            foreach (var room in plan)
            {
                Assert.IsTrue(ids.Add(room.GetString("id")), "duplicate id " + room.GetString("id"));
                Assert.IsInstanceOf<Vec2i>(room["footprint"]);
                var fp = (Vec2i)room["footprint"];
                Assert.AreEqual((long)fp.X * fp.Y, room.GetInt("target_cells"));
                if (room.GetString("role") == "dock") hasDock = true;
            }
            Assert.IsTrue(hasDock, "derelict archetype guarantees a dock");

            List<GdDict> again = new RoomAssigner().AssignWithSelector(template, bp, Derelict(), new RoomVariantSelector(), "breach_field");
            Assert.AreEqual(GdJson.Stringify(new GdArray(plan)), GdJson.Stringify(new GdArray(again)));
        }

        [Test]
        public void RoomAssigner_NormalizesAliases()
        {
            Assert.AreEqual("cargo", RoomAssigner.NormalizeRole("compartment"));
            Assert.AreEqual("bridge", RoomAssigner.NormalizeRole("cockpit"));
            Assert.AreEqual("hub", RoomAssigner.NormalizeRole("hub"));
            var n = RoomAssigner.NormalizeArchetype(new GdDict
            {
                { "role_weights", new GdDict { { "compartment", 2.0 }, { "bay", 3.0 }, { "cargo", 1.0 } } },
                { "guaranteed_roles", GdArray.Of("quarters", "crew_quarters", "dock") },
            });
            Assert.AreEqual(6L, n.GetDict("role_weights")["cargo"]);
            Assert.IsTrue(V.VariantEquals(GdArray.Of("crew_quarters", "dock"), n["guaranteed_roles"]));
        }

        [Test]
        public void RoomGraphGenerator_ShipGraphIsConnectedWithSystemRooms()
        {
            RoomGraph graph = new RoomGraphGenerator().Generate(new ShipBlueprint(ShipBlueprint.Size.Medium, ShipBlueprint.Condition.Pristine, 7));
            Assert.IsTrue(graph.IsFullyConnected());
            Assert.That(graph.Rooms.Count, Is.InRange(8, 12));
            Assert.AreEqual("airlock_01", graph.Rooms[0].GetString("id"));
            Assert.AreEqual(1, graph.GetRoomsByRole("engineering").Count);
            Assert.AreEqual(1, graph.GetRoomsByRole("bridge").Count);

            RoomGraph derelict = new RoomGraphGenerator().Generate(new ShipBlueprint(1, 2, 7), new GdDict { { "type", "derelict" } });
            Assert.AreEqual("dock_01", derelict.Rooms[0].GetString("id"));
            Assert.AreEqual(0, derelict.GetRoomsByRole("bridge").Count);
        }

        [Test]
        public void StructuralPlacer_EmitsRecordsPerRoomModule()
        {
            RoomGraph graph = new RoomGraphGenerator().Generate(new ShipBlueprint(ShipBlueprint.Size.Medium, ShipBlueprint.Condition.Pristine, 42));
            var placer = new StructuralPlacer();
            StructuralPlacer.Placement placement = placer.PlaceStructure(graph, 42);
            Assert.IsNotNull(placement);
            Assert.That(_log.Warnings, Has.Some.EqualTo(StructuralPlacer.DEPRECATION_DIAGNOSTIC));
            Assert.AreEqual(graph.Rooms.Count, placement.Rooms.Count);
            foreach (var room in placement.Rooms)
            {
                Assert.AreEqual(new Vec3(room.GridPosition.X * 6.0, 0.0, room.GridPosition.Y * 6.0), room.Position);
                for (int i = 0; i < room.Modules.Count; i++)
                {
                    StructuralPlacer.ModulePlacement m = room.Modules[i];
                    Assert.AreEqual(room.RoomId, m.RoomId);
                    Assert.AreEqual(m.ModuleId + "_" + i, m.NodeName);
                    Assert.AreEqual(new Vec3(0.0, 0.0, i * 4.0), m.LocalPosition);
                    Assert.AreEqual(room.Position + m.LocalPosition, m.Position);
                    Assert.AreEqual(StructuralPlacer.MODULE_BASE_PATH + m.ModuleId + ".tscn", m.ScenePath);
                }
            }
            Assert.That(placement.AllModules().Count, Is.GreaterThan(graph.Rooms.Count));
            Assert.IsNull(placer.PlaceStructure(new RoomGraph(), 1));
        }

        [Test]
        public void EncounterInjector_NeverSpawnsOnCriticalPath_AndValidates()
        {
            GdDict layout = CaptureLayout("s999_small_wrecked_ext");
            var biome = BiomeProfile.FromFile("res://data/procgen/biomes/breach_field.json");
            GdDict result = new EncounterInjector().Inject(layout, biome, DifficultyProfile.ForId("deep_dive"), 42);
            GdArray markers = result.GetArray("encounters");
            Assert.That(markers.Count, Is.GreaterThan(0));
            var critical = GdString.ToStringList(result.GetArray("critical_path"));
            foreach (GdDict m in markers)
            {
                Assert.IsFalse(critical.Contains(m.GetString("room_id")), "marker on critical path: " + m.GetString("id"));
                Assert.That(m.GetInt("count"), Is.GreaterThanOrEqualTo(1));
            }
            GdDict verdict = EncounterInjector.Validate(result);
            Assert.IsTrue(verdict.GetBool("valid"), GdJson.Stringify(verdict));
            Assert.AreEqual((long)markers.Count, verdict.GetInt("marker_count"));

            // Tampering is caught.
            ((GdDict)markers[0])["room_id"] = critical[0];
            Assert.AreEqual(critical[0], EncounterInjector.Validate(result).GetString("critical_path_violation"));
        }

        [Test]
        public void StructuralEdgeCompiler_OneFloorPerCell_NoErrorsOnCapture()
        {
            GdDict layout = CaptureLayout("s999_small_wrecked_ext");
            GdDict plan = new StructuralEdgeCompiler().Compile(layout);
            Assert.IsEmpty(plan.GetArray("errors"));
            long cells = 0;
            foreach (GdDict room in layout.GetArray("rooms")) cells += room.GetArray("cells").Count;
            Assert.AreEqual(cells, plan.GetDict("occupancy").Count);
            Assert.AreEqual(cells, plan.GetArray("floor_placements").Count);
            foreach (GdDict p in plan.GetArray("placements"))
            {
                Assert.AreNotEqual("OPEN", p.GetString("kind"));
                Assert.IsInstanceOf<Vec3>(p["position"]);
            }
            Assert.That(plan.GetArray("socket_bindings").Count, Is.GreaterThan(0));
            Assert.AreEqual("0|h|-1|2", StructuralEdgeCompiler.EdgeKey(0, new Vec2i(2, 0), "north"));
            Assert.AreEqual("0|v|0|2", StructuralEdgeCompiler.EdgeKey(0, new Vec2i(3, 0), "west"));
            Assert.AreEqual("", StructuralEdgeCompiler.EdgeKey(0, new Vec2i(3, 0), "up"));
        }

        [Test]
        public void FirstRunContract_LoadsAndFallsBackToFirstPreferredSeed()
        {
            var frc = new FirstRunContract();
            Assert.IsTrue(frc.LoadContract());
            Assert.AreEqual("breach_field", frc.Contract.GetString("biome_id"));
            Assert.AreEqual(42L, frc.PickSeed());
            Assert.IsFalse(frc.LoadContract("res://data/procgen/slice/missing.json"));
            Assert.IsTrue(frc.Contract.IsEmpty);

            // A provider returning a valid payload for 777 only picks 777.
            var good = CaptureLayout("s42_breach_field_standard");
            good["encounters"] = GdArray.Of(new GdDict { { "id", "x" } });
            var slice = GdJson.ParseDict(Fixtures.ReadText("godot/procgen/gameplay_slice_s42_breach_field_standard.json"));
            System.Func<long, object> provider = seed => seed == 777
                ? new GdDict { { "layout", good }, { "gameplay_slice", slice } }
                : null;
            Assert.AreEqual(777L, new FirstRunContract().PickSeed(provider));
        }
    }
}
