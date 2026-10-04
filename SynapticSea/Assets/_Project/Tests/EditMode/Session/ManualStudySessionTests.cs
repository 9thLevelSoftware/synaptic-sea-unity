using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    // Provisioned engine-free integration tests: no claim of earned acquisition or physical source reachability.
    public class ManualStudySessionTests : PaidCraftFixture
    {
        const string Book = "fabrication_schematic_basic";
        RunSession StudyBoot(string classId = "cook")
        {
            RunSession s = Boot(manualStudy: true);
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
        static void Complete(RunSession s)
        {
            for (int i = 0; i < 60 && s.ManualStudyRunning; i++) Step(s, .5);
            Assert.AreEqual("completed", s.GetManualStudyState().GetDictOrEmpty("job").GetString("status"));
        }
        [TestCase("engineer", 300L)] [TestCase("mechanic", 300L)] [TestCase("medic", 140L)]
        [TestCase("pilot", 200L)] [TestCase("scientist", 240L)] [TestCase("cook", 160L)]
        [TestCase("security", 180L)] [TestCase("communications", 180L)] [TestCase("salvage_captain", 220L)]
        [TestCase("field_medic", 160L)] [TestCase("signal_specialist", 200L)]
        public void RetainedStudyGrantsExactAuthoredClassRewardOnce(string classId, long xp)
        {
            RunSession s = StudyBoot(classId); long beforeLevel = s.PlayerProgression.GetSkillLevel("fabrication");
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed"));
            Complete(s);
            long expectedLevel = beforeLevel, remaining = xp;
            while (remaining >= PlayerProgressionState.XpForNextLevel(expectedLevel)) remaining -= PlayerProgressionState.XpForNextLevel(expectedLevel++);
            Assert.AreEqual(expectedLevel, s.PlayerProgression.GetSkillLevel("fabrication"));
            Assert.AreEqual(remaining, s.PlayerProgression.GetSkillXp("fabrication"));
            Assert.AreEqual(1, s.InventoryState.GetQuantity(Book));
            Assert.IsTrue(s.PlayerProgression.HasReadBook(Book));
            Assert.IsTrue(s.RecipeKnowledge.IsKnown("craft_thruster_nozzle"));
            GdDict domain = Capture(s); Valid(domain);
            Assert.AreEqual(4L, domain.GetInt("schema_version"));
            Assert.AreEqual(1, s.TrainingEventBus.GetLog().OfType<GdDict>().Count(row => row.GetString("event_id") == "study_manual"));
            long total = s.TrainingEventBus.GetTotalXpDelivered();
            GdDict before = s.PlayerProgression.GetSummary(); Assert.AreEqual(1, s.InventoryState.RemoveItem(Book, 1)); Assert.AreEqual(1, s.InventoryState.AddItem(Book, 1));
            Assert.AreEqual("already_studied", s.RequestManualStudy(Book).GetString("reason"));
            Equal(before, s.PlayerProgression.GetSummary(), "Duplicate retained copies do not award XP.");
            Assert.AreEqual(total, s.TrainingEventBus.GetTotalXpDelivered(), "RecordApplied does not deliver XP again.");
            Assert.LessOrEqual(domain.GetDictOrEmpty("receipts").Count, 3, "Only start/latest progress/completion receipts survive per-frame work.");
        }
        [Test]
        public void LearnedNozzleRetainsAdvancedSkillAndTierGates()
        {
            RunSession s = StudyBoot(); Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Complete(s);
            Provision(s, "craft_thruster_nozzle");
            Assert.AreEqual(1, s.PlayerProgression.GetSkillLevel("fabrication"));
            Assert.AreEqual("insufficient_skill", s.RequestPaidCraft("fabricator", "craft_thruster_nozzle", "nozzle-low-skill").GetString("reason"));
            s.PlayerProgression.Skills["fabrication"] = 4L;
            s.CraftingState.GetStation("fabricator").Level = 0; s.CraftingState.GetStation("fabricator").Tier = 0;
            Assert.AreEqual("insufficient_tier", s.RequestPaidCraft("fabricator", "craft_thruster_nozzle", "nozzle-low-tier").GetString("reason"));
        }
        [Test]
        public void CraftStudyCraftPreservesIndependentRewardProofsAndRefusesTampering()
        {
            RunSession s = StudyBoot("scientist"); s.PlayerProgression.Skills["fabrication"] = 4L; Provision(s);
            Start(s); s.AdvanceCrafting(100); GdDict prior = Capture(s), receipts = prior.GetDictOrEmpty("receipts").DeepCopy();
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Complete(s);
            foreach (var row in receipts) Equal(row.Value, Capture(s).GetDictOrEmpty("receipts").Get(row.Key), "Study does not rewrite existing craft proofs.");
            Provision(s); Start(s, commandId: "after-study"); s.AdvanceCrafting(100); GdDict combined = Capture(s); Valid(combined);
            Assert.IsTrue(s.RestorePaidCraftingDomain(combined)); Valid(Capture(s));
            GdDict forged = combined.DeepCopy();
            GdDict craft = forged.GetDictOrEmpty("receipts").Values.OfType<GdDict>().First(row => row.GetDictOrEmpty("result").GetString("operation") == "craft_complete");
            craft.GetDictOrEmpty("result").GetDictOrEmpty("training_record")["base_xp"] = 10000L;
            Assert.IsFalse(DomainBundle.TryCreate(forged, out _, out _), "Schema four retains the paid craft tamper gate.");
        }
        [Test]
        public void DamageAndPublicationFailureKeepProgressAndRewardsSafe()
        {
            RunSession s = StudyBoot(); Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Step(s, 2);
            s.VitalsState.Health -= 1; Step(s, .5);
            Assert.AreEqual("damage", s.GetManualStudyState().GetDictOrEmpty("job").GetString("reason"));
            Assert.AreEqual(2, s.GetManualStudyState().GetDictOrEmpty("job").GetFloat("progress_seconds"));
            GdDict before = Capture(s), raw = s.PlayerProgression.GetSummary(), inventory = s.InventoryState.GetSummary();
            s.ComponentStageHook = stage => { if (stage == "live_placement") throw new InvalidOperationException("study-publication-fault"); };
            Assert.IsFalse(s.RequestManualStudy(Book).GetBool("committed"));
            s.ComponentStageHook = null; Equal(before, Capture(s), "Failed study publication retains exact owner.");
            Equal(raw, s.PlayerProgression.GetSummary(), "Progression remains original."); Equal(inventory, s.InventoryState.GetSummary(), "Manual quantity remains original.");
        }
        [Test]
        public void MovementCopyLossReleaseAndRestorePauseExactProgressUntilExplicitResume()
        {
            RunSession s = StudyBoot(); Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Step(s, 2);
            Step(s, 1, true); Assert.AreEqual("moving", s.GetManualStudyState().GetDictOrEmpty("job").GetString("reason"));
            Assert.AreEqual(2, s.GetManualStudyState().GetDictOrEmpty("job").GetFloat("progress_seconds"));
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); s.EndWorkHold();
            Assert.AreEqual("released", s.GetManualStudyState().GetDictOrEmpty("job").GetString("reason"));
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); s.InventoryState.RemoveItem(Book, 1); Step(s, 1);
            Assert.AreEqual("manual_not_carried", s.GetManualStudyState().GetDictOrEmpty("job").GetString("reason"));
            s.InventoryState.AddItem(Book, 1); Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed"));
            GdDict saved = Capture(s); Assert.IsTrue(s.RestorePaidCraftingDomain(saved));
            Assert.IsTrue(s.GetManualStudyState().GetDictOrEmpty("job").GetBool("resume_required"));
            Assert.IsFalse(s.IsWorkInteractHeld); Step(s, 2);
            Assert.AreEqual(2, s.GetManualStudyState().GetDictOrEmpty("job").GetFloat("progress_seconds"));
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Complete(s);
        }
        [Test]
        public void StudyHasNoIndustrialStaminaDrainAndRejectsCraftOverlap()
        {
            RunSession s = StudyBoot(); s.VitalsState.Stamina = 50;
            Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed"));
            Assert.AreEqual("study_busy", s.CraftingState.RecipeBlockedReason(Recipe));
            Step(s, 2); Assert.GreaterOrEqual(s.VitalsState.Stamina, 50);
            Assert.AreEqual("study_busy", s.RequestInteract());
            s.PauseManualStudy(); Provision(s); s.PlayerProgression.Skills["fabrication"] = 4;
            Start(s); Assert.AreEqual("work_busy", s.RequestManualStudy(Book).GetString("reason"));
        }
        [TestCase("early_job")] [TestCase("copy")] [TestCase("book")] [TestCase("actor")]
        [TestCase("commit")] [TestCase("progress")] [TestCase("reward")]
        public void ForgedStudyStateOrCompletionIsRejectedWithoutMutation(string mutation)
        {
            RunSession s = StudyBoot(); Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Complete(s);
            GdDict original = Capture(s), bad = original.DeepCopy(), state = ManualStudyState.State(bad);
            string id = state.GetDictOrEmpty("completed").GetString(Book);
            GdDict effect = bad.GetDictOrEmpty("receipts").GetDictOrEmpty(id).GetDictOrEmpty("result");
            switch (mutation)
            {
                case "early_job": effect.GetDictOrEmpty("job_before")["progress_seconds"] = 0.0; break;
                case "copy": effect.GetDictOrEmpty("inventory_before").GetDictOrEmpty("items").Erase(Book); effect["inventory_after"] = effect.GetDictOrEmpty("inventory_before").DeepCopy(); break;
                case "book": effect["book_id"] = "welding_manual_basic"; break;
                case "actor": state["actor_id"] = "another_character"; break;
                case "commit": state.GetDictOrEmpty("completed")[Book] = "forged"; break;
                case "progress": state.GetDictOrEmpty("job")["progress_seconds"] = double.PositiveInfinity; break;
                case "reward": effect.GetDictOrEmpty("book_definition")["book_xp"] = 10000L; break;
            }
            Assert.IsFalse(DomainBundle.TryCreate(bad, out _, out _));
            Assert.IsFalse(s.RestorePaidCraftingDomain(bad)); Equal(original, Capture(s), "Refusal is nonmutating.");
        }
        [TestCase("progress")] [TestCase("missing_proof")] [TestCase("mismatched_proof")] [TestCase("empty_job")]
        public void SavedCurrentWorkCannotChangeOrLoseItsCumulativeWitness(string mutation)
        {
            RunSession s = StudyBoot(); Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Step(s, 1);
            GdDict original = Capture(s), bad = original.DeepCopy();
            GdDict progress = bad.GetDictOrEmpty("receipts").Values.OfType<GdDict>().Single(row => row.GetDictOrEmpty("result").GetString("operation") == "study_progress");
            switch (mutation)
            {
                case "progress": ManualStudyState.State(bad).GetDictOrEmpty("job")["progress_seconds"] = 29.0; break;
                case "missing_proof": bad.GetDictOrEmpty("receipts").Erase(progress.GetString("commit_id")); break;
                case "mismatched_proof": progress.GetDictOrEmpty("result")["origin_receipt_id"] = "missing"; break;
                case "empty_job": ManualStudyState.State(bad)["job"] = new GdDict(); break;
            }
            Assert.IsFalse(DomainBundle.TryCreate(bad, out _, out _)); Assert.IsFalse(s.RestorePaidCraftingDomain(bad));
            Equal(original, Capture(s), "Isolated saved work tampering is nonmutating.");
        }
        [Test]
        public void OrdinaryPaidSnapshotCodecRetainsTypedStudyProgressAndCompletionProof()
        {
            RunSession s = StudyBoot(); Assert.IsTrue(s.RequestManualStudy(Book).GetBool("committed")); Step(s, 2);
            foreach (bool complete in new[] { false, true })
            {
                if (complete) Complete(s);
                GdDict owner = Capture(s);
                var snapshot = new RunSnapshot { SliceVersion = SaveLoadService.CURRENT_SLICE_VERSION, GodotVersion = "test" };
                snapshot.CraftingSummary["paid_craft"] = PaidSnapshotCodec.Envelope(owner, false);
                GdDict parsed = PaidSnapshotCodec.Parse(PaidSnapshotCodec.Stringify(snapshot.ToDict(), PaidSnapshotCodec.Policy.OrdinaryRun), PaidSnapshotCodec.Policy.OrdinaryRun);
                Assert.IsNotNull(parsed);
                Assert.IsTrue(ComponentDomainCodec.TryDecode(parsed.GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft").GetDictOrEmpty("domain"), out GdDict decoded, out _));
                Equal(owner, decoded, "Existing typed envelope preserves exact study fields and receipts."); Valid(decoded);
            }
        }
        [Test]
        public void DefaultCompositionRemainsThreeAndRefusesFourWithoutOptIn()
        {
            RunSession old = Boot(); Assert.AreEqual(3, Capture(old).GetInt("schema_version"));
            Assert.AreEqual("manual_study_inactive", old.RequestManualStudy(Book).GetString("reason"));
            RunSession study = StudyBoot(); Assert.IsFalse(old.RestorePaidCraftingDomain(Capture(study)));
        }
    }
}
