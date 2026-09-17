using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public abstract class ProgressionDataTestBase
    {
        protected MemoryStorage Storage;
        protected ManualClock Clock;

        [SetUp]
        public void SetUpServices()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            Storage = new MemoryStorage();
            Clock = new ManualClock();
        }

        [TearDown]
        public void TearDownServices() => CatalogRegistry.Clear();

        protected static GdDict Data(string resPath)
        {
            GdDict d = CatalogRegistry.LoadDict(resPath);
            Assert.IsNotNull(d, resPath);
            return d;
        }
    }

    public class ClassDefinitionTests : ProgressionDataTestBase
    {
        [Test]
        public void LoadAll_MatchesSmoke()
        {
            var classes = ClassDefinition.LoadAll();
            Assert.AreEqual(11, classes.Count); // the Godot smoke's 8 predates the 3 unlockable Domain 6 classes
            int baseClasses = 0;
            foreach (var c in classes.Values) if (!c.Unlockable) baseClasses++;
            Assert.AreEqual(8, baseClasses);
            Assert.IsTrue(classes["salvage_captain"].Unlockable);
            var eng = classes["engineer"];
            Assert.AreEqual("Engineer", eng.DisplayName);
            Assert.AreEqual(3, eng.StartingSkills.GetInt("repair", -1));
            Assert.AreEqual(1.5, eng.XpMultiplier("technical"), 1e-4);
            Assert.AreEqual(1.0, eng.XpMultiplier("nonexistent_category"), 1e-4);
            Assert.GreaterOrEqual(classes["medic"].StartingSkills.GetInt("quarantine", 0), 1);
        }
    }

    public class HubUpgradeStateTests : ProgressionDataTestBase
    {
        [Test]
        public void Purchase_GatesOnCurrencyAndOwnership()
        {
            var hub = new HubUpgradeState();
            Assert.IsTrue(hub.Configure());
            Assert.AreEqual(50, hub.GetCost("hub_storage_basic"));
            var meta = new MetaProgressionState(Storage, Clock);
            Assert.AreEqual("no_meta_state", hub.CanPurchase("hub_storage_basic", null)["reason"]);
            Assert.AreEqual("invalid_meta_state", hub.CanPurchase("hub_storage_basic", new object())["reason"]);
            Assert.AreEqual("insufficient_currency", hub.CanPurchase("hub_storage_basic", meta)["reason"]);
            meta.AddMetaCurrency(60);
            Assert.IsTrue(hub.Purchase("hub_storage_basic", meta));
            Assert.AreEqual(10, meta.MetaCurrency);
            Assert.IsTrue(meta.IsHubUpgradeUnlocked("hub_storage_basic"));
            Assert.IsFalse(hub.Purchase("hub_storage_basic", meta));
            Assert.AreEqual("already_owned", hub.CanPurchase("hub_storage_basic", meta)["reason"]);
            var entries = hub.GetUpgradeEntries(meta);
            Assert.AreEqual(hub.GetUpgradeCount(), entries.Count);
            Assert.AreEqual(1.0, hub.ComposeXpMultipliers(null).GetFloat("technical"));
        }
    }

    public class MetaProgressionStateTests : ProgressionDataTestBase
    {
        [Test]
        public void Payout_MatchesSmoke()
        {
            var meta = new MetaProgressionState(Storage, Clock);
            meta.Configure(new GdDict());
            long payout = meta.ApplyMetaPayout(new GdDict
            {
                { "completed_objectives", 3L },
                { "skill_levels", new GdDict { { "repair", 5L }, { "welding", 4L } } },
                { "discoveries", 2L },
                { "reason", "completion" },
            });
            Assert.AreEqual(39, payout);
            Assert.AreEqual(1, meta.TotalRunsCompleted);
            Assert.AreEqual(5, meta.HighestSkillLevelSeen);
            meta.StartNewRun();
            Assert.AreEqual(0, meta.LastPayoutCurrency);
            Assert.AreEqual(39, meta.MetaCurrency);
        }

        [Test]
        public void DiskAndSummaryRoundTrip_ThroughStorage()
        {
            var meta = new MetaProgressionState(Storage, Clock);
            meta.Configure(new GdDict());
            Assert.IsTrue(meta.AddMetaCurrency(100));
            Assert.IsTrue(meta.SpendMetaCurrency(50));
            Assert.IsFalse(meta.SpendMetaCurrency(100));
            Assert.IsTrue(meta.UnlockClass("salvage_captain"));
            Assert.IsFalse(meta.UnlockClass("salvage_captain"));
            Assert.IsTrue(meta.UnlockHubUpgrade("hub_storage_basic"));
            Assert.IsTrue(meta.UnlockCodexEntry("codex_repair_intro"));
            Assert.AreEqual(3, meta.GetUnlockCount());
            Assert.IsFalse(new MetaProgressionState(Storage, Clock).LoadFromDisk("user://meta_progression_test_state.json"));
            Assert.IsTrue(meta.SaveToDisk("user://meta_progression_test_state.json"));
            var disk = new MetaProgressionState(Storage, Clock);
            Assert.IsTrue(disk.LoadFromDisk("user://meta_progression_test_state.json"));
            Assert.IsTrue(V.VariantEquals(meta.GetSummary(), disk.GetSummary()));
            Assert.IsTrue(V.VariantEquals(meta.ToDict(), disk.ToDict()));

            var partial = new MetaProgressionState(Storage, Clock);
            Assert.IsTrue(partial.ApplySummary(new GdDict { { "schema", "old" }, { "meta_currency", 7L } }));
            Assert.AreEqual(7, partial.MetaCurrency);
            Assert.IsFalse(partial.ApplySummary(new GdDict { { "schema", "old" } }));
        }
    }

    public class SkillEffectsResolverTests : ProgressionDataTestBase
    {
        [Test]
        public void ShippedCatalog_IsFullyCovered_AndSkillSpeedsWork()
        {
            var res = new SkillEffectsResolver();
            Assert.IsTrue(res.LoadDefault());
            Assert.GreaterOrEqual(res.EffectCount(), 22);
            GdDict audit = res.AuditCatalogCoverage();
            Assert.IsTrue(V.Bool(audit["ok"]), audit.ToString());
            var progression = new GdDict { { "skills", new GdDict { { "welding", 5L }, { "repair", 5L } } } };
            GdDict ctx = res.BuildWorkContext(progression, "weld", "welding", "");
            Assert.Greater(V.F64(ctx["work_speed_mult"]), 1.05);
            Assert.AreEqual(5L, ctx["skill_level"]);
            Assert.AreEqual(1.0, res.WorkSpeedMultiplier(null, "weld"));
        }
    }

    public class UnlockRegistryTests : ProgressionDataTestBase
    {
        [Test]
        public void TriggerUnlock_IsIdempotent_AndDiskRoundTrips()
        {
            GdDict catalog = Data("res://data/player/unlock_tables.json");
            var unlock = new UnlockRegistry(Storage, Clock);
            Assert.IsTrue(unlock.Configure(catalog));
            Assert.GreaterOrEqual(unlock.GetCatalogSize(), 20);
            string u1 = unlock.UnlockForTrigger("repair_full_system", "any");
            Assert.AreEqual("codex_repair_intro", u1);
            Assert.IsTrue(unlock.IsUnlocked(u1));
            Assert.AreNotEqual(u1, unlock.UnlockForTrigger("repair_full_system", "any"));
            Assert.IsFalse(unlock.Unlock("made_up"));
            Assert.IsTrue(unlock.SaveToDisk());
            var disk = new UnlockRegistry(Storage, Clock);
            disk.Configure(catalog);
            Assert.IsTrue(disk.LoadFromDisk());
            Assert.IsTrue(V.VariantEquals(unlock.GetUnlockedIds(), disk.GetUnlockedIds()));
            Assert.IsTrue(V.VariantEquals(unlock.ToDict(), disk.ToDict()));
        }
    }

    public class AchievementStateTests : ProgressionDataTestBase
    {
        [Test]
        public void SmokeFlow_UnlocksExactAndWildcard_AndRoundTrips()
        {
            GdDict catalog = Data("res://data/release/achievement_catalog.json");
            var state = new AchievementState(Storage, Clock);
            state.Configure(catalog);
            Assert.GreaterOrEqual(state.GetCatalogSize(), 5);
            string first = V.Str(state.GetCatalogIds()[0]);
            Assert.IsTrue(state.Unlock(first));
            Assert.IsFalse(state.Unlock(first));
            Assert.IsFalse(state.Unlock("totally_made_up_achievement"));
            Assert.AreEqual("junction_calibrator_used", state.UnlockForTrigger("tool_acquired", "junction_calibrator"));
            Assert.IsNotEmpty(state.UnlockForTrigger("loot_searched", "any"));
            Assert.IsEmpty(state.UnlockForTrigger("tool_acquired", "nonexistent_tool"));

            var reloaded = new AchievementState(Storage, Clock);
            reloaded.Configure(catalog);
            Assert.IsTrue(reloaded.ApplySummary(state.GetSummary()));
            Assert.IsTrue(V.VariantEquals(state.GetSummary(), reloaded.GetSummary()));
            GdDict tampered = state.GetSummary();
            tampered["schema"] = "tampered-version";
            Assert.IsFalse(new AchievementState(Storage, Clock).ApplySummary(tampered));

            Assert.IsTrue(state.SaveToDisk());
            var disk = new AchievementState(Storage, Clock);
            disk.Configure(catalog);
            Assert.IsTrue(disk.LoadFromDisk());
            Assert.AreEqual(state.GetUnlockCount(), disk.GetUnlockCount());
        }

        [Test]
        public void Unlocked_IsRaisedOncePerNewUnlock_NotOnRestore()
        {
            GdDict catalog = Data("res://data/release/achievement_catalog.json");
            var state = new AchievementState(Storage, Clock);
            state.Configure(catalog);
            var raised = new System.Collections.Generic.List<string>();
            state.Unlocked += raised.Add;
            string first = V.Str(state.GetCatalogIds()[0]);
            state.Unlock(first);
            state.Unlock(first);
            state.Unlock("totally_made_up_achievement");
            Assert.AreEqual(new[] { first }, raised.ToArray());

            var restored = new AchievementState(Storage, Clock);
            restored.Configure(catalog);
            restored.Unlocked += raised.Add;
            Assert.IsTrue(restored.ApplySummary(state.GetSummary()));
            Assert.AreEqual(1, raised.Count, "restores do not re-raise unlocks");
        }

        [Test]
        public void CatalogIds_AreValidSteamApiNames()
        {
            var state = new AchievementState(Storage, Clock);
            state.Configure(Data("res://data/release/achievement_catalog.json"));
            Assert.AreEqual(8, state.GetCatalogSize());
            Assert.IsEmpty(PlatformAchievementIds.InvalidSteamIds(state.GetCatalogIds()));
            Assert.AreEqual("first_breath", PlatformAchievementIds.SteamApiNameFor("first_breath"));
            Assert.IsFalse(PlatformAchievementIds.IsValidSteamApiName("First Breath"));
            Assert.IsFalse(PlatformAchievementIds.IsValidSteamApiName(""));
            Assert.IsFalse(PlatformAchievementIds.IsValidSteamApiName(new string('a', 129)));
        }
    }
}
