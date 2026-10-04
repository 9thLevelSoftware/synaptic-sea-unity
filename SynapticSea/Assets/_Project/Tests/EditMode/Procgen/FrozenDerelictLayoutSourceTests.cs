using System;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    public class FrozenDerelictLayoutSourceTests
    {
        IResourceReader _reader;
        [SetUp] public void Setup()
        {
            _reader = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CoreServices.Resources = _reader; CoreServices.Log = NullLog.Instance;
            CatalogRegistry.Clear(); EncounterInjector.ClearTableCache();
        }
        [TearDown] public void Teardown() { CatalogRegistry.Clear(); EncounterInjector.ClearTableCache(); }
        sealed class Overlay : IResourceReader
        {
            public IResourceReader Inner; public string Path, Text;
            public bool Exists(string path) => path == Path || Inner.Exists(path);
            public string ReadText(string path) => path == Path ? Text : Inner.ReadText(path);
        }
        GdDict Manifest() => (GdDict)GdJson.ParseString(_reader.ReadText(FrozenDerelictLayoutSource.Root + "manifest.json"));
        [Test] public void Version4RequiresExplicitAdmissionAndHasNoDefaultSource()
        {
            var gen = new ShipGenerator(); Assert.IsNull(gen.DerelictSource); Assert.IsFalse(gen.EnableReviewedFrozenVersion4);
            gen.DerelictSource = new FrozenDerelictLayoutSource(_reader);
            Assert.IsNull(gen.GenerateFromSeed(42, 0, 0));
            gen.EnableReviewedFrozenVersion4 = true; Assert.IsNotNull(gen.GenerateFromSeed(42, 0, 0));
            Assert.IsNull(gen.GenerateFromSeed(43, 0, 0), "Missing request must not reroll");
        }
        [TestCase(42, 0, 0)] [TestCase(42, 0, 2)] [TestCase(42, 2, 0)] [TestCase(42, 2, 2)]
        [TestCase(777, 0, 0)] [TestCase(777, 0, 2)] [TestCase(777, 2, 0)] [TestCase(777, 2, 2)]
        public void ActualReviewedPairsPassSharedGenerationBoundary(int seed, int size, int condition)
        {
            var source = new FrozenDerelictLayoutSource(_reader);
            var args = new GdDict { { "archetype_id", ShipGenerator.WORLDGEN_ARCHETYPE_BY_SIZE[(long)size] }, { "intactness_override", ShipGenerator.WORLDGEN_INTACTNESS_BY_CONDITION[(long)condition] } };
            string rawText = source.ExportLayoutJson(seed, args, ShipGenerator.WORLDGEN_KIT_ID);
            var raw = (GdDict)GdJson.ParseString(rawText);
            var exported = (GdDict)GdJson.ParseString(source.ExportGameplaySliceJson(seed, args));
            var gen = new ShipGenerator { DerelictSource = source, EnableReviewedFrozenVersion4 = true };
            ShipDocuments docs = gen.GenerateFromSeed(seed, size, condition); Assert.IsNotNull(docs);
            foreach (string key in new[] { "rooms", "portals", "structural_plan", "vertical_connections", "fire_zones", "arc_zones", "breach_zones", "radiation_zones" })
                Assert.AreEqual(GdJson.Stringify(raw[key]), GdJson.Stringify(docs.Layout[key]), key);
            foreach (string key in FrozenDerelictLayoutSource.HazardKeys) Assert.AreEqual(GdJson.Stringify(raw[key]), GdJson.Stringify(docs.GameplaySlice[key]), key);
            var actual = docs.GameplaySlice.GetArrayOrEmpty("loot_containers"); var loot = exported.GetArrayOrEmpty("loot_containers");
            Assert.AreEqual(loot.Count, actual.Count);
            for (int i = 0; i < loot.Count; i++)
            {
                var expected = ((GdDict)loot[i]).DeepCopy(); expected["loot_table"] = ShipGenerator.MapWorldgenLootTable(expected.GetString("kind"), LootRoller.LoadTables());
                Assert.AreEqual(GdJson.Stringify(expected), GdJson.Stringify(actual[i]));
            }
            Assert.IsFalse(docs.GameplaySlice.GetArrayOrEmpty("objectives").IsEmpty);
            Assert.AreEqual("unity_gameplay_slice_builder", docs.Layout.GetDictOrEmpty("worldgen_fixture").GetString("objectives_authority"));
            Assert.AreEqual(rawText, source.ExportLayoutJson(seed, args, ShipGenerator.WORLDGEN_KIT_ID), "Immutable raw source");
        }
        [TestCase("source_commit", "bad")] [TestCase("generator_version", 2)] [TestCase("layout_schema", "1.1.0")] [TestCase("kit_id", "wrong")]
        public void RejectsManifestIdentity(string key, object value)
        {
            var m = Manifest(); m[key] = value;
            Assert.Throws<ArgumentException>(() => new FrozenDerelictLayoutSource(new Overlay { Inner = _reader, Path = FrozenDerelictLayoutSource.Root + "manifest.json", Text = GdJson.Stringify(m) }));
        }
        [Test] public void RejectsMissingHashCorruptionAndDuplicateRequests()
        {
            var m = Manifest(); var rows = (GdArray)m["fixtures"]; var row = (GdDict)rows[0];
            Assert.Throws<ArgumentException>(() => new FrozenDerelictLayoutSource(new Overlay { Inner = _reader, Path = FrozenDerelictLayoutSource.Root + row.GetString("layout_path"), Text = null }));
            Assert.Throws<ArgumentException>(() => new FrozenDerelictLayoutSource(new Overlay { Inner = _reader, Path = FrozenDerelictLayoutSource.Root + row.GetString("layout_path"), Text = "{}" }));
            rows.Append(row.DeepCopy());
            Assert.Throws<ArgumentException>(() => new FrozenDerelictLayoutSource(new Overlay { Inner = _reader, Path = FrozenDerelictLayoutSource.Root + "manifest.json", Text = GdJson.Stringify(m) }));
        }
        sealed class Impostor : IDerelictLayoutSource
        {
            public long GeneratorVersion() => 4;
            public string ExportLayoutJson(long seed, GdDict parameters, string kit) => throw new Exception("Must not export");
            public string ExportGameplaySliceJson(long seed, GdDict parameters) => throw new Exception("Must not export");
        }
        [Test] public void ArbitraryVersion4SourceIsNotAdmittedByOptIn()
        {
            Assert.IsNull(new ShipGenerator { EnableReviewedFrozenVersion4 = true, DerelictSource = new Impostor() }.GenerateFromSeed(42, 0, 0));
        }
        [Test] public void RehashedManifestDoesNotAuthenticateAlteredPair()
        {
            var m = Manifest(); var row = (GdDict)((GdArray)m["fixtures"])[0];
            row["layout_sha256"] = FrozenDerelictLayoutSource.Hash("{}");
            Assert.Throws<ArgumentException>(() => new FrozenDerelictLayoutSource(new Overlay { Inner = _reader, Path = FrozenDerelictLayoutSource.Root + "manifest.json", Text = GdJson.Stringify(m) }));
        }
        [TestCase("portal")] [TestCase("module")] [TestCase("placement")] [TestCase("cell")] [TestCase("position")]
        public void SharedGeometryValidationRejectsSyntheticMalformedRecords(string damage)
        {
            var source = new FrozenDerelictLayoutSource(_reader); var args = new GdDict { { "archetype_id", "shuttle" }, { "intactness_override", 9500L } };
            var layout = (GdDict)GdJson.ParseString(source.ExportLayoutJson(42, args, ShipGenerator.WORLDGEN_KIT_ID));
            var modules = new System.Collections.Generic.HashSet<string>();
            foreach (object raw in CatalogRegistry.LoadDict(ShipGenerator.WORLDGEN_KIT_PATH).GetArrayOrEmpty("modules")) modules.Add(((GdDict)raw).GetString("module_id"));
            Assert.IsTrue(FrozenDerelictLayoutSource.ValidateGeometry(layout, modules));
            var plan = layout.GetDictOrEmpty("structural_plan"); var placements = plan.GetArrayOrEmpty("placements"); var placement = (GdDict)placements[0];
            switch (damage)
            {
                case "portal": ((GdDict)layout.GetArrayOrEmpty("portals")[0])["to_room"] = "missing"; break;
                case "module": placement["module_id"] = "missing"; break;
                case "placement": placements.Append(placement.DeepCopy()); break;
                case "cell": ((GdDict)layout.GetArrayOrEmpty("rooms")[0])["cells"] = GdArray.Of(GdArray.Of("invalid", 0L)); break;
                case "position": placement["position"] = GdArray.Of(double.PositiveInfinity, 0L, 0L); break;
            }
            Assert.IsFalse(FrozenDerelictLayoutSource.ValidateGeometry(layout, modules));
        }
        [Test] public void SyntheticNonzeroHazardsSurviveAndMalformedAuthorityFailsClosed()
        {
            var source = new FrozenDerelictLayoutSource(_reader); var args = new GdDict { { "archetype_id", "shuttle" }, { "intactness_override", 9500L } };
            var layout = (GdDict)GdJson.ParseString(source.ExportLayoutJson(42, args, ShipGenerator.WORLDGEN_KIT_ID));
            var exported = (GdDict)GdJson.ParseString(source.ExportGameplaySliceJson(42, args));
            var roomDoc = (GdDict)layout.GetArrayOrEmpty("rooms")[0];
            string room = roomDoc.GetString("id"); var xy = (GdArray)roomDoc.GetArrayOrEmpty("cells")[0];
            var endpoint = GdArray.Of(xy[0], xy[1], roomDoc.GetInt("deck"));
            foreach (string key in FrozenDerelictLayoutSource.HazardKeys)
                layout[key] = GdArray.Of(new GdDict { { "id", "synthetic-" + key }, { "from_room", room }, { "to_room", room }, { "from_cell", endpoint.DeepCopy() }, { "to_cell", endpoint.DeepCopy() } });
            exported["fire_zones"] = ((GdArray)layout["fire_zones"]).DeepCopy();
            var built = new GameplaySliceBuilder().Build(layout); var objectives = GdJson.Stringify(built["objectives"]);
            Assert.IsTrue(FrozenDerelictLayoutSource.ApplyAuthority(layout, built, exported, LootRoller.LoadTables()));
            Assert.AreEqual(objectives, GdJson.Stringify(built["objectives"]));
            foreach (string key in FrozenDerelictLayoutSource.HazardKeys) Assert.AreEqual(GdJson.Stringify(layout[key]), GdJson.Stringify(built[key]));
            exported["fire_zones"] = new GdArray(); Assert.IsFalse(FrozenDerelictLayoutSource.ApplyAuthority(layout, built, exported, LootRoller.LoadTables()), "Lost fire authority");
            exported["fire_zones"] = ((GdArray)layout["fire_zones"]).DeepCopy();
            var loot = exported.GetArrayOrEmpty("loot_containers");
            var first = (GdDict)loot[0]; var saved = first.DeepCopy();
            first["kind"] = "unmapped_crate";
            Assert.IsFalse(FrozenDerelictLayoutSource.ApplyAuthority(layout, built, exported, LootRoller.LoadTables()), "No fallback table mapping");
            loot[0] = saved.DeepCopy(); ((GdDict)loot[0])["approach_cell"] = GdArray.Of(double.PositiveInfinity, 0L, 0L);
            Assert.IsFalse(FrozenDerelictLayoutSource.ApplyAuthority(layout, built, exported, LootRoller.LoadTables()), "Nonfinite loot cell");
            loot[0] = saved.DeepCopy();
            Assert.IsTrue(FrozenDerelictLayoutSource.ValidateAuthority(layout, exported), "Valid synthetic preimage before duplicate injection");
            loot.Append(((GdDict)loot[0]).DeepCopy());
            Assert.IsFalse(FrozenDerelictLayoutSource.ApplyAuthority(layout, built, exported, LootRoller.LoadTables()), "Duplicate exported loot");
        }
    }
}
