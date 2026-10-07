using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// Behaviour checks for the L2-L6 procgen ports that the Godot replays do not reach: the pipeline fallback,
    /// LifeBoatBuilder's record tree, determinism and the systems interfaces.
    /// </summary>
    public class ProcgenPipelineTests
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
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
            CoreServices.Log = NullLog.Instance;
        }

        [Test]
        public void ShipGenerator_GeneratesFromPipeline()
        {
            var gen = new ShipGenerator();
            ShipDocuments docs = gen.GenerateFromSeed(17, 2, 0);
            Assert.IsNotNull(docs);
            Assert.AreEqual("GeneratedShip", docs.Name);
            Assert.AreEqual("procgen-default-seed-17", docs.Layout.GetString("program_id"));
            Assert.IsTrue(docs.Layout.GetBool("structural_plan_validated"));
            Assert.IsNotEmpty(docs.GameplaySlice.GetArray("objectives"));
            Assert.AreEqual("res://data/kits/ship_structural_v0.json", docs.KitPath);
            Assert.IsInstanceOf<ShipDocuments>(((IShipGenerator)gen).GenerateFromSeed(17, 2, 0));
        }

        [Test]
        public void LifeBoatBuilder_BuildRecordsEveryPlanRecordUnderItsRoom()
        {
            LifeBoatBuilder.BuildResult result = LifeBoatBuilder.Build("breach_field");
            Assert.IsNotNull(result, string.Join("\n", _log.Errors));
            Assert.IsEmpty(_log.Errors);
            Assert.AreEqual(3, result.Rooms.Count);
            Assert.AreEqual(new Vec3(0.0, 0.0, 0.0), result.AirlockRoom().Position);
            GdDict plan = result.Layout.GetDict("structural_plan");
            int expected = 0;
            foreach (string layer in new[] { "floor_placements", "placements", "ceiling_placements" })
                foreach (GdDict rec in plan.GetArray(layer))
                    if (rec.GetString("module_id").Length != 0) expected++;
            Assert.AreEqual(expected, result.Wrappers.Count);
            foreach (var w in result.Wrappers)
            {
                Assert.IsFalse(w.NodeName.Contains(":") || w.NodeName.Contains("|"), w.NodeName);
                StringAssert.StartsWith("res://scenes/wrappers/", w.ScenePath);
                var parent = result.Rooms.Find(r => r.RoomId == w.ParentRoomId);
                Vec3 parentPos = parent?.Position ?? Vec3.Zero;
                Assert.AreEqual(w.Position - parentPos, w.LocalPosition);
            }
            Assert.AreEqual("ship_structural_hazard", result.Layout.GetString("kit_id"));
        }

        [Test]
        public void ShipLayoutGenerator_SameInputsTwice_IdenticalText()
        {
            var archetype = CatalogRegistry.LoadDict("res://data/procgen/archetypes/derelict.json");
            string a = GdJson.Stringify(new ShipLayoutGenerator().GenerateWithOptions(new ShipBlueprint(1, 2, 999), archetype.DeepCopy(), "dead_fleet", "hardened", true));
            string b = GdJson.Stringify(new ShipLayoutGenerator().GenerateWithOptions(new ShipBlueprint(1, 2, 999), archetype.DeepCopy(), "dead_fleet", "hardened", true));
            Assert.AreEqual(a, b);
            GdDict match = SeedDeterminismContract.AssertLayoutMatch(new ShipBlueprint(2, 0, 5), new GdDict(), "breach_field", "");
            Assert.IsTrue(match.GetBool("match"));
        }

        [Test]
        public void ComponentPlacementState_ImplementsRuntimeAndMountInterfaces()
        {
            var cat = new ComponentCatalog();
            cat.LoadDefault();
            var place = new ComponentPlacementState();
            place.Populate(CatalogRegistry.LoadDict("res://data/procgen/golden/coherent_ship_002/layout.json"), cat, 7);
            Assert.That(place.Placed.Count, Is.GreaterThan(0));
            Assert.IsFalse(place.HasSlotCollisions());

            IComponentManifestModel manifest = place;
            var copy = new ComponentPlacementState();
            Assert.IsTrue(copy.ApplySummary(manifest.GetSummary()));
            Assert.IsTrue(V.VariantEquals(manifest.GetSummary(), copy.GetSummary()));
            Assert.AreEqual(place.Fingerprint(), copy.Fingerprint());

            ComponentMountResolver.IComponentPlacement mountTarget = place;
            string iid = ((GdDict)place.Placed[0]).GetString("component_instance_id");
            GdDict dismounted = mountTarget.Dismount(iid);
            Assert.IsTrue(dismounted.GetBool("ok"));
            Assert.AreEqual(1L, place.DismountedCount());
        }
    }
}
