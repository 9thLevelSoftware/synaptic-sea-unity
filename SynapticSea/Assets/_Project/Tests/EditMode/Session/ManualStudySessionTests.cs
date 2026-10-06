using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    // Manual Study on the base game (no paid crafting, no component integration): a session-held 30 s job whose
    // reward is the book's XP, a training-log row and the books_read mark that persists through the progression summary.
    public class ManualStudySessionTests : InfraDataTestBase
    {
        const string Book = "fabrication_schematic_basic", OtherBook = "welding_manual_basic";
        readonly List<RunSession> _sessions = new List<RunSession>();

        [TearDown]
        public void DisposeSessions() { foreach (RunSession s in _sessions) s.Dispose(); _sessions.Clear(); }

        RunSession StudyBoot(string classId = "cook")
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            rig.Session = RunSession.Create(deps);
            _sessions.Add(rig.Session);
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            s.ThreatManager.Threats.Clear();
            s.InventoryState.Items.Clear();
            s.VitalsState.Stamina = s.VitalsState.MaxStamina;
            s.PlayerProgression.Configure(ClassDefinition.LoadAll()[classId], PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            s.InventoryState.AddItem(Book, 1);
            return s;
        }
        static void Step(RunSession s, double seconds, bool moving = false)
        {
            var frame = TickContext.Frame(seconds, s.Scene.PlayerPosition, moving);
            frame.InBreachZone = false; frame.InFireZoneCompartment = "";
            s.Tick(frame);
        }
        static GdDict Job(RunSession s) => s.GetManualStudyState().GetDictOrEmpty("job");
        static void Complete(RunSession s)
        {
            for (int i = 0; i < 60 && s.ManualStudyRunning; i++) Step(s, .5);
            Assert.AreEqual("completed", Job(s).GetString("status"));
        }

        [TestCase("engineer", 300L)] [TestCase("mechanic", 300L)] [TestCase("medic", 140L)]
        [TestCase("pilot", 200L)] [TestCase("scientist", 240L)] [TestCase("cook", 160L)]
        [TestCase("security", 180L)] [TestCase("communications", 180L)] [TestCase("salvage_captain", 220L)]
        [TestCase("field_medic", 160L)] [TestCase("signal_specialist", 200L)]
        public void StudyGrantsExactAuthoredClassRewardOnce(string classId, long xp)
        {
            RunSession s = StudyBoot(classId); long beforeLevel = s.PlayerProgression.GetSkillLevel("fabrication");
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed"));
            Complete(s);
            long expectedLevel = beforeLevel, remaining = xp;
            while (remaining >= PlayerProgressionState.XpForNextLevel(expectedLevel)) remaining -= PlayerProgressionState.XpForNextLevel(expectedLevel++);
            Assert.AreEqual(expectedLevel, s.PlayerProgression.GetSkillLevel("fabrication"));
            Assert.AreEqual(remaining, s.PlayerProgression.GetSkillXp("fabrication"));
            Assert.AreEqual(1, s.InventoryState.GetQuantity(Book), "The manual is retained.");
            Assert.IsTrue(s.PlayerProgression.HasReadBook(Book));
            Assert.AreEqual(1, s.TrainingEventBus.GetLog().OfType<GdDict>().Count(row => row.GetString("event_id") == "study_manual"));
            GdDict before = s.PlayerProgression.GetSummary(); long total = s.TrainingEventBus.GetTotalXpDelivered();
            Assert.AreEqual(1, s.InventoryState.RemoveItem(Book, 1)); Assert.AreEqual(1, s.InventoryState.AddItem(Book, 1));
            Assert.AreEqual("already_studied", s.RequestManualStudy(Book).GetString("reason"));
            Assert.IsTrue(V.VariantEquals(before, s.PlayerProgression.GetSummary()), "A second copy awards nothing.");
            Assert.AreEqual(total, s.TrainingEventBus.GetTotalXpDelivered());
        }
        [Test]
        public void StudyTakesExactlyThirtySecondsAndShowsItInTheManualText()
        {
            RunSession s = StudyBoot(); Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed"));
            StringAssert.Contains("/ 30 s", s.ViewManual(Book));
            Step(s, 29.5); Assert.IsFalse(s.PlayerProgression.HasReadBook(Book));
            Step(s, .5); Assert.IsTrue(s.PlayerProgression.HasReadBook(Book));
            StringAssert.Contains("Already studied", s.ViewManual(Book));
        }
        [Test]
        public void BooksReadPersistsThroughTheProgressionSummary()
        {
            RunSession s = StudyBoot(); Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Complete(s);
            var restored = new PlayerProgressionState();
            restored.Configure(ClassDefinition.LoadAll()["cook"], PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            restored.ApplySummary(s.PlayerProgression.GetSummary());
            Assert.IsTrue(restored.HasReadBook(Book));
            Assert.AreEqual(s.PlayerProgression.GetSkillXp("fabrication"), restored.GetSkillXp("fabrication"));
        }
        [Test]
        public void DamageMovementReleaseAndCopyLossPauseExactProgressUntilExplicitResume()
        {
            RunSession s = StudyBoot(); Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Step(s, 2);
            s.VitalsState.Health -= 1; Step(s, .5);
            Assert.AreEqual("damage", Job(s).GetString("reason")); Assert.AreEqual(2, Job(s).GetFloat("progress_seconds"));
            Step(s, 3); Assert.AreEqual(2, Job(s).GetFloat("progress_seconds"), "A paused job does not advance by itself.");
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Step(s, 1, true);
            Assert.AreEqual("moving", Job(s).GetString("reason")); Assert.AreEqual(2, Job(s).GetFloat("progress_seconds"));
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); s.EndWorkHold();
            Assert.AreEqual("released", Job(s).GetString("reason"));
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); s.InventoryState.RemoveItem(Book, 1); Step(s, 1);
            Assert.AreEqual("manual_not_carried", Job(s).GetString("reason"));
            s.InventoryState.AddItem(Book, 1); Assert.IsTrue(Job(s).GetBool("resume_required"));
            Assert.IsTrue(s.BeginWorkHold(), "Holding interact resumes a paused study.");
            Assert.AreEqual(2, Job(s).GetFloat("progress_seconds")); Complete(s);
        }
        [Test]
        public void StudyHasNoStaminaDrainBlocksInteractAndRefusesASecondBook()
        {
            RunSession s = StudyBoot(); s.InventoryState.AddItem(OtherBook, 1); s.VitalsState.Stamina = 50;
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed"));
            Assert.AreEqual("study_busy", s.RequestManualStudy(Book).GetString("reason"));
            Step(s, 2); Assert.GreaterOrEqual(s.VitalsState.Stamina, 50);
            Assert.AreEqual("study_busy", s.RequestInteract());
            s.PauseManualStudy();
            Assert.AreEqual("other_manual_pending", s.RequestManualStudy(OtherBook).GetString("reason"));
        }
        [Test]
        public void NonBooksAndUncarriedBooksAreRefused()
        {
            RunSession s = StudyBoot(); s.InventoryState.AddItem("scrap_metal", 1);
            Assert.AreEqual("manual_missing", s.RequestManualStudy("scrap_metal").GetString("reason"));
            Assert.AreEqual("manual_not_carried", s.RequestManualStudy(OtherBook).GetString("reason"));
        }
        [Test]
        public void BookFoundInAnOrdinaryContainerCanBeStudiedForXp()
        {
            RunSession s = StudyBoot(); s.InventoryState.Items.Clear();
            GdDict tables = LootRoller.LoadTablesWithOverlays(), defs = ItemDefs.LoadDefinitions();
            string found = "";
            for (int seed = 0; seed < 2000 && found.Length == 0; seed++)
            {
                var crate = new LootContainer();
                crate.Configure("study_crate_" + seed, "salvage_engineering", "study-test|" + seed, s.InventoryState, tables, s.Scene.PlayerPosition);
                crate.SetValidationPlayerInRange(true);
                Assert.IsTrue(crate.TryInteract(s.Scene.PlayerPosition));
                found = s.InventoryState.Items.Keys.Select(V.Str).FirstOrDefault(id => ItemDefs.Category(defs, id) == "book") ?? "";
            }
            Assert.IsNotEmpty(found, "an ordinary engineering cache yields a book within 2000 searches");
            string skill = PlayerProgressionState.LoadBooksCatalog().GetDictOrEmpty(found).GetString("target_skill");
            long before = s.PlayerProgression.GetSkillXp(skill) + s.PlayerProgression.GetSkillLevel(skill) * 1000;
            Assert.IsTrue(s.RequestManualStudy(found).GetBool("committed"));
            for (int i = 0; i < 60 && s.ManualStudyRunning; i++) Step(s, .5);
            Assert.IsTrue(s.PlayerProgression.HasReadBook(found));
            Assert.Greater(s.PlayerProgression.GetSkillXp(skill) + s.PlayerProgression.GetSkillLevel(skill) * 1000, before);
        }
        [Test]
        public void EveryAuthoredBookIsAnItemAndDropsFromAnOrdinaryLootTable()
        {
            string root = SynapticSea.Tests.Fixtures.StreamingDataRoot;
            var books = PlayerProgressionState.LoadBooksCatalog();
            var items = (GdDict)GdJson.Parse(File.ReadAllText(Path.Combine(root, "data/items/item_definitions.json")));
            GdDict tables = LootRoller.LoadTablesWithOverlays();
            var dropped = new HashSet<string>(tables.Values.OfType<GdDict>().SelectMany(t => t.GetArrayOrEmpty("entries").OfType<GdDict>()).Select(e => e.GetString("item_id")));
            Assert.AreEqual(12, books.Count);
            foreach (var book in books)
            {
                string id = V.Str(book.Key);
                Assert.AreEqual("book", items.GetDictOrEmpty(id).GetString("category"), id + " must be a book item");
                Assert.IsTrue(dropped.Contains(id), id + " must appear in a loot table");
            }
        }
    }
}
