using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// Behaviour checks for the L2-L6 procgen ports that the Godot replays do not reach: the DerelictGenerator seam
    /// (no native implementation exists), LifeBoatBuilder's record tree, determinism and the systems interfaces.
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

        /// <summary>Stands in for a future native DerelictGenerator: serves a golden layout + a worldgen-style slice.</summary>
        sealed class FakeDerelictSource : IDerelictLayoutSource
        {
            public long Version = ShipGenerator.WORLDGEN_VERSION;
            public GdDict LastParams;

            public long GeneratorVersion() => Version;

            public string ExportLayoutJson(long seedValue, GdDict parameters, string kitId)
            {
                LastParams = parameters;
                GdDict layout = CatalogRegistry.LoadDict("res://data/procgen/golden/coherent_ship_001/layout.json");
                return GdJson.Stringify(layout);
            }

            public string ExportGameplaySliceJson(long seedValue, GdDict parameters) => GdJson.Stringify(new GdDict
            {
                {
                    "loot_containers", GdArray.Of(
                        new GdDict { { "id", "container_1" }, { "kind", "suit_locker" }, { "room_id", "cargo_a" }, { "approach_cell", GdArray.Of(9L, 9L, 0L) }, { "loot_table", "worldgen_seeded" } },
                        new GdDict { { "id", "container_2" }, { "kind", "cargo_crate" }, { "room_id", "cargo_a" }, { "approach_cell", GdArray.Of(8L, 9L, 0L) }, { "loot_table", "worldgen_seeded" } })
                },
            });
        }

        [Test]
        public void ShipGenerator_WithoutDerelictSource_FallsBackToPipeline()
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
        public void ShipGenerator_WorldgenSeam_MergesLootAndInjectsEncounters()
        {
            var source = new FakeDerelictSource();
            var gen = new ShipGenerator { DerelictSource = source };
            gen.ConfigureRunContext("breach_field", "standard");
            ShipDocuments docs = gen.GenerateFromSeed(42, 1, 2);
            Assert.IsNotNull(docs, string.Join("\n", _log.Errors));
            Assert.AreEqual("corvette", source.LastParams.GetString("archetype_id"));
            Assert.AreEqual(2000L, source.LastParams.GetInt("intactness_override"));
            Assert.AreEqual("ship_structural_v0", docs.Layout.GetString("kit_id"));
            Assert.AreEqual("breach_field", docs.Layout.GetString("biome_id"));
            Assert.IsTrue(docs.Layout.Has("encounter_pacing"));
            Assert.IsNull(docs.LayoutJson);

            GdArray containers = docs.GameplaySlice.GetArray("loot_containers");
            GdDict lootTables = Systems_LootTables();
            var mapped = new System.Collections.Generic.List<string>();
            foreach (GdDict c in containers)
            {
                Assert.IsTrue(lootTables.Has(c.GetString("loot_table")), c.GetString("id"));
                mapped.Add(c.GetString("id") + ":" + c.GetString("loot_table"));
            }
            Assert.Contains("container_1:generic_locker", mapped);
            Assert.Contains("container_2:salvage_cargo", mapped);

            source.Version = 3;
            Assert.IsNull(gen.GenerateFromSeed(42, 1, 2));
            Assert.That(_log.Errors, Has.Some.Contains("unsupported DerelictGenerator version"));
            source.Version = ShipGenerator.WORLDGEN_VERSION;
            Assert.IsNull(gen.GenerateFromSeed(42, 5, 2));
        }

        static GdDict Systems_LootTables() => LootRoller.LoadTables();

        [Test]
        public void ShipGenerator_WorldgenLootMapping()
        {
            var tables = new GdDict { { "salvage_cargo", new GdDict() }, { "salvage_engineering", new GdDict() }, { "generic_locker", new GdDict() }, { "generic_crate", new GdDict() } };
            Assert.AreEqual("salvage_engineering", ShipGenerator.MapWorldgenLootTable("tool_rack", tables));
            Assert.AreEqual("generic_crate", ShipGenerator.MapWorldgenLootTable("odd_crate", tables));
            Assert.AreEqual("generic_locker", ShipGenerator.MapWorldgenLootTable("big_locker", tables));
            Assert.AreEqual("", ShipGenerator.MapWorldgenLootTable("barrel", tables));
            var gameplay = new GdDict { { "loot_containers", GdArray.Of(new GdDict { { "room_id", "r" }, { "approach_cell", GdArray.Of(1L, 2L, 0L) }, { "loot_table", "generic_crate" } }) } };
            var exported = new GdDict
            {
                {
                    "loot_containers", GdArray.Of(
                        new GdDict { { "room_id", "r" }, { "approach_cell", GdArray.Of(1L, 2L, 0L) }, { "kind", "cargo_crate" }, { "loot_table", "worldgen_seeded" } },
                        new GdDict { { "room_id", "r" }, { "approach_cell", GdArray.Of(3L, 2L, 0L) }, { "kind", "footlocker" }, { "loot_table", "worldgen_seeded" } })
                },
            };
            Assert.IsTrue(ShipGenerator.ResolveWorldgenLootContainers(gameplay, exported, tables));
            Assert.AreEqual(2, gameplay.GetArray("loot_containers").Count, "duplicate cell is not merged twice");
            exported["loot_containers"] = GdArray.Of(new GdDict { { "kind", "barrel" }, { "loot_table", "worldgen_seeded" } });
            Assert.IsFalse(ShipGenerator.ResolveWorldgenLootContainers(gameplay, exported, tables));
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
