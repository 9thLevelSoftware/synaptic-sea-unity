using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Parity
{
    /// <summary>
    /// Loot determinism against Godot 4.7.1 (fixtures/godot/loot): every table × 5 seed sources through LootRoller,
    /// and LootDistribution × 2 contexts. Results must match item-for-item, including RNG seeds.
    /// </summary>
    public class LootParityTests
    {
        const string FixturePath = "godot/loot/loot_rolls_fixture.json";

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        [Test]
        public void LoadedTablesMatchGodotInputs()
        {
            Fixtures.Require("godot/loot/inputs/loot_tables.json");
            var expected = Fixtures.ReadDict("godot/loot/inputs/loot_tables.json");
            var diffs = TreeDiff.Compare(expected, LootRoller.LoadTables(), new TreeDiff.Options { IntFloatEquivalent = true });
            Assert.IsEmpty(diffs, TreeDiff.Format(diffs));
        }

        [Test]
        public void LootRollerRollsMatchGodot()
        {
            Fixtures.Require(FixturePath);
            var fixture = Fixtures.ReadDict(FixturePath);
            var tables = LootRoller.LoadTables();
            int nonEmpty = 0;
            foreach (object o in fixture.GetArray("loot_roller_rolls"))
            {
                var roll = (GdDict)o;
                string table = roll.GetString("table_id"), source = roll.GetString("seed_source");
                Assert.AreEqual(roll.GetInt("rng_seed"), Math.Abs(GodotHash.StringHash(source)), $"seed for '{source}'");
                var actual = LootRoller.Roll(table, source, tables);
                var diffs = TreeDiff.Compare(roll.Get("result"), actual);
                Assert.IsEmpty(diffs, $"LootRoller.Roll({table}, {source}): {TreeDiff.Format(diffs)}");
                if (actual.Count > 0) nonEmpty++;
            }
            Assert.That(nonEmpty, Is.GreaterThan(20), "expected many non-empty rolls");
        }

        [Test]
        public void LootDistributionRollsMatchGodot()
        {
            Fixtures.Require(FixturePath);
            var fixture = Fixtures.ReadDict(FixturePath);
            var contexts = fixture.GetArray("contexts").Cast<GdDict>().ToDictionary(c => c.GetString("id"), c => c.GetDict("context"));
            var tables = LootRoller.LoadTables();
            foreach (object o in fixture.GetArray("loot_distribution_rolls"))
            {
                var r = (GdDict)o;
                string table = r.GetString("table_id"), source = r.GetString("seed_source");
                Assert.AreEqual(r.GetInt("rng_seed"), Math.Abs(GodotHash.StringHash(r.GetString("rng_seed_string"))), $"seed string {r.GetString("rng_seed_string")}");
                var context = contexts[r.GetString("context_id")].DeepCopy();
                var actual = LootDistribution.Roll(table, source, tables, context);
                var diffs = TreeDiff.Compare(r.Get("result"), actual);
                Assert.IsEmpty(diffs, $"LootDistribution.Roll({table}, {source}, {r.GetString("context_id")}): {TreeDiff.Format(diffs)}");
            }
        }
    }
}
