using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// Phase 1.8: a generated home folds the run seed into its loot roll key so its containers vary by seed. The golden hub and every
    /// test layout keep the original <c>MarkerId:id</c> key (the home's marker id is empty), so the Godot parity rolls are unchanged.
    /// </summary>
    public class GeneratedHomeLootKeyTests
    {
        [Test]
        public void TheGoldenHubKeepsTheOriginalLootKey()
        {
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.IsFalse(s.GeneratedHome);
            Assert.AreEqual(":start_supply_a", s.LootSeedSource("start_supply_a"));
        }

        [Test]
        public void AGeneratedHomeFoldsTheRunSeedIntoTheKey()
        {
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.Deps.LayoutPath = RunDirectoryJanitor.RunsDir + "/20261008T000000-s1234-abc123/layout.json";
            s.Deps.RunSeed = 1234;
            Assert.IsTrue(s.GeneratedHome);
            Assert.AreEqual("home:1234:start_supply_a", s.LootSeedSource("start_supply_a"));
            s.Deps.RunSeed = 1235;
            Assert.AreEqual("home:1235:start_supply_a", s.LootSeedSource("start_supply_a"), "a different run seed changes the key");
        }

        [Test]
        public void OnlyPathsUnderUserRunsCountAsGenerated()
        {
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.Deps.LayoutPath = "res://data/procgen/golden/coherent_ship_002/layout.json";
            Assert.IsFalse(s.GeneratedHome);
            s.Deps.LayoutPath = "user://runs-archive/layout.json";
            Assert.IsFalse(s.GeneratedHome, "the directory must be exactly user://runs/");
        }
    }
}
