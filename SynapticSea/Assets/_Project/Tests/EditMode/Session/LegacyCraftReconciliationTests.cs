using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    public class LegacyCraftReconciliationTests : PaidCraftFixture
    {
        [Test]
        public void SourcePathsAndContexts_AreImmutableDistinctAndVisibleThroughPublicListing()
        {
            GdDict source = Historical("duplicate_queue"), context = source.GetDictOrEmpty("station_summaries").GetDictOrEmpty(Kind).DeepCopy();
            RunSession s = Boot(); Import(s, source, "source-one");
            GdDict first = Legacy(Capture(s)).DeepCopy();
            Assert.AreEqual(3, first.Count);
            foreach (GdDict row in first.Values.OfType<GdDict>())
                Equal(context, row.GetDictOrEmpty("original_context").Get("station_summary"), "Exact station queue/timing/quality context is retained for every original row.");
            Assert.AreEqual(2, first.Values.OfType<GdDict>().Count(row => row.Get("original_record") is string recipe && recipe == Recipe), "Duplicate queued recipe strings stay individually preserved.");
            Import(s, source, "source-two");
            Assert.AreEqual(6, Legacy(Capture(s)).Count, "Same recipe/path/hash from another source cannot collide.");
            GdDict beforeRepeat = Capture(s); Import(s, source, "source-two"); Equal(beforeRepeat, Capture(s), "Repeated import is fully idempotent including receipts/revision.");
            GdArray listing = (GdArray)Call(s, "ListLegacyCraftReconciliations", (object)null);
            Assert.AreEqual(6, listing.Count);
            source.GetDictOrEmpty("station_summaries").GetDictOrEmpty(Kind)["progress_seconds"] = 999.0;
            foreach (var row in first) Equal(row.Value, Legacy(Capture(s)).Get(row.Key), "Caller mutation cannot rewrite original context.");
        }

        [Test]
        public void LegacyPortableShape_QuarantinesWithoutReservingFieldChannel()
        {
            RunSession historical = Boot(paid: false); Provision(historical, "field_bandage");
            Assert.IsTrue(historical.BeginFieldCraftRecipe("field_bandage"));
            GdDict source = historical.FieldCraftingState.GetSummary();
            RunSession s = Boot(); GdDict before = s.InventoryState.GetSummary();
            GdDict imported = Command(s, "ImportLegacyCrafting", new GdDict(), source, "legacy-field", Hash(source));
            Assert.IsTrue(imported.GetBool("ok"));
            string id = OnlyLegacy(s); GdDict row = Legacy(Capture(s)).GetDictOrEmpty(id);
            Assert.AreEqual("paused_unverified", row.GetString("status"));
            Equal(source.GetDictOrEmpty("field_crafting").Get("active_craft"), row.Get("original_record"), "Original field record survives.");
            s.AdvanceCrafting(100); Equal(before, s.InventoryState.GetSummary(), "No field legacy output/refund.");
            Provision(s, "field_bandage"); Start(s, "field_bandage");
            Assert.AreEqual("paused_unverified", Legacy(Capture(s)).GetDictOrEmpty(id).GetString("status"));
        }

        [Test]
        public void StartFresh_UsesDifferentCurrentQuality_AndNewCommandCannotRepeatTerminalDecision()
        {
            GdDict source = Historical("finished_unpaid");
            double oldQuality = source.GetDictOrEmpty("active_craft").GetFloat("quality_score");
            RunSession s = Boot(); Import(s, source); string id = OnlyLegacy(s);
            s.PlayerProgression.Skills["fabrication"] = 0L; s.CraftingState.GetOrCreateStation(Kind).Level = 3;
            Provision(s);
            double material = s.MaterialState.AverageIngredientQuality(s.CraftingState.GetRecipe(Recipe).GetDictOrEmpty("ingredients"));
            GdDict expected = new SynapticSea.Core.Systems.QualityTierResolver().Resolve(material, 0, 3, true);
            Assert.AreNotEqual(oldQuality, expected.GetFloat("score"), "Discriminating current versus historical resolution.");
            GdDict fresh = Command(s, "ReconcileLegacyCraft", id, "start_fresh", "fresh-different-quality");
            Assert.IsTrue(fresh.GetBool("committed"));
            GdDict job = Job(s, fresh.GetString("job_id"));
            Assert.AreEqual(expected.GetFloat("score"), job.GetFloat("quality_score"));
            Assert.AreEqual(0.0, job.GetFloat("progress_seconds"));
            Assert.AreEqual(s.CraftingState.GetCraftTime(Recipe), job.GetFloat("required_seconds"));
            Assert.IsTrue(Command(s, "CancelPaidCraft", job.GetString("job_id"), "cancel-fresh").GetBool("ok"));
            Provision(s); GdDict beforeRepeat = Capture(s);
            GdDict repeat = Command(s, "ReconcileLegacyCraft", id, "start_fresh", "different-fresh-command");
            Assert.IsFalse(repeat.GetBool("ok"), "One source cannot spawn another fresh job even after the first frees its channel.");
            Equal(beforeRepeat, Capture(s), "Terminal decision cannot debit a second set.");
        }

        [Test]
        public void AbandonedLegacySource_IsTerminalForLaterDifferentCommand()
        {
            RunSession s = Boot(); Import(s, Historical("begun")); string id = OnlyLegacy(s);
            Assert.IsTrue(Command(s, "ReconcileLegacyCraft", id, "keep_paused", "keep-first").GetBool("ok"));
            Assert.IsTrue(Command(s, "ReconcileLegacyCraft", id, "abandon", "abandon-next").GetBool("ok"));
            Provision(s); GdDict before = Capture(s);
            Assert.IsFalse(Command(s, "ReconcileLegacyCraft", id, "start_fresh", "fresh-after-abandon").GetBool("ok"));
            Equal(before, Capture(s), "Abandon is terminal; Keep alone remains reversible.");
        }

        [TestCase("inventory")]
        [TestCase("job")]
        [TestCase("receipt")]
        [TestCase("publication")]
        public void StartFreshPublicationFault_RollsBackArchiveAndPaymentTogether(string stage)
        {
            RunSession s = Boot(); Import(s, Historical("begun")); string id = OnlyLegacy(s); Provision(s);
            GdDict before = Capture(s), inventory = s.InventoryState.GetSummary(), crafting = s.CraftingState.GetSummary(), xp = s.PlayerProgression.GetSummary();
            bool reached = false; s.ComponentStageHook = current => { if (current == stage) { reached = true; throw new System.InvalidOperationException("fresh-archive-payment-fault"); } };
            GdDict failed;
            try { failed = Command(s, "ReconcileLegacyCraft", id, "start_fresh", "fault-fresh"); }
            finally { s.ComponentStageHook = null; }
            Assert.IsTrue(reached); Assert.IsFalse(failed.GetBool("committed"));
            Equal(inventory, s.InventoryState.GetSummary(), "Direct inputs before capture."); Equal(crafting, s.CraftingState.GetSummary(), "Direct job before capture."); Equal(xp, s.PlayerProgression.GetSummary(), "Direct XP before capture.");
            Equal(before, Capture(s), "Decision/original evidence/payment/receipt remain one before-image.");
            Assert.IsTrue(Command(s, "ReconcileLegacyCraft", id, "start_fresh", "fault-fresh").GetBool("committed"));
            Assert.AreEqual(1, Jobs(Capture(s)).Count);
            GdDict once = Capture(s); Command(s, "ReconcileLegacyCraft", id, "start_fresh", "fault-fresh"); Equal(once, Capture(s), "Same decision retries once.");
        }

        [Test]
        public void PrepareLegacyImport_IsNonmutatingAndProducesAValidPrivateOwner()
        {
            RunSession s = Boot(); GdDict initial = Capture(s), source = Historical("begun");
            GdDict prepared = Command(s, "PrepareLegacyCrafting", initial, source, new GdDict(), "legacy-preflight", Hash(source));
            Assert.IsTrue(prepared.GetBool("ok"), prepared.GetString("reason"));
            Equal(initial, Capture(s), "Save preflight cannot apply imported state to the live session.");
            GdDict candidate = prepared.GetDictOrEmpty("domain"); Valid(candidate);
            Assert.AreEqual(1, Legacy(candidate).Count); Assert.IsTrue(Jobs(candidate).IsEmpty);
            Assert.IsTrue((bool)Call(s, "RestorePaidCraftingDomain", candidate));
            Assert.AreEqual(1, Legacy(Capture(s)).Count);
        }

        static GdDict Legacy(GdDict domain) => Paid(domain).GetDictOrEmpty("legacy");
        static string Hash(GdDict summary)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(GdJson.Stringify(summary))).Select(b => b.ToString("x2")));
        }

        GdDict Historical(string shape)
        {
            RunSession old = Boot(paid: false);
            Provision(old);
            Assert.IsTrue(old.CraftingState.BeginCraft(Recipe, old.InventoryState, old.MaterialState, 4));
            Assert.AreEqual(0L, old.InventoryState.GetQuantity("scrap_metal"), "Historical BeginCraft really consumed its only set.");
            if (shape == "unpaid_queue" || shape == "finished_unpaid")
            {
                Assert.AreEqual(1L, old.CraftingState.EnqueueCraft(Recipe));
                Assert.IsTrue(old.CraftingState.Tick(100));
                Assert.IsFalse(old.CraftingState.FinishCraft().IsEmpty);
                Assert.AreEqual(Recipe, old.CraftingState.GetActiveRecipeId(), "Historical queue auto-started without a second set.");
                Assert.AreEqual(0L, old.InventoryState.GetQuantity("scrap_metal"));
            }
            if (shape == "finished_unpaid") Assert.IsTrue(old.CraftingState.Tick(100));
            if (shape == "duplicate_queue") Assert.AreEqual(2L, old.CraftingState.EnqueueCraft(Recipe, 2));
            return old.CraftingState.GetSummary().DeepCopy();
        }

        static GdDict Import(RunSession s, GdDict summary, string source = "legacy-run/current_run.json")
        {
            GdDict result = Command(s, "ImportLegacyCrafting", summary, new GdDict(), source, Hash(summary));
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            return result;
        }

        static string OnlyLegacy(RunSession s)
        {
            GdDict records = Legacy(Capture(s));
            Assert.AreEqual(1, records.Count, "Active and matching station mirror represent one historical job.");
            return V.Str(records.Keys.Single());
        }

        [TestCase("begun")]
        [TestCase("unpaid_queue")]
        [TestCase("finished_unpaid")]
        public void HistoricalShapes_AllQuarantineWithoutInferredPaymentOutputOrRefund(string shape)
        {
            GdDict source = Historical(shape), untouched = source.DeepCopy();
            RunSession s = Boot();
            GdDict inventory = s.InventoryState.GetSummary(), xp = s.PlayerProgression.GetSummary();
            Import(s, source);
            string id = OnlyLegacy(s);
            GdDict row = Legacy(Capture(s)).GetDictOrEmpty(id);
            Assert.AreEqual("paused_unverified", row.GetString("status"));
            Assert.AreEqual("unverified", row.GetString("payment_state"));
            Assert.AreEqual(Hash(source), row.GetString("source_hash"));
            Assert.IsNotEmpty(row.GetString("source_path"));
            Equal(source.GetDictOrEmpty("active_craft"), row.GetDictOrEmpty("original_record"), "Preserve exact historical active record.");
            Equal(untouched, source, "Import never mutates the caller's original source record.");
            Assert.IsTrue(Jobs(Capture(s)).IsEmpty, "Unverified records have no executable paid job.");
            s.AdvanceCrafting(100);
            Equal(inventory, s.InventoryState.GetSummary(), "No legacy output or inferred refund.");
            Equal(xp, s.PlayerProgression.GetSummary(), "No legacy training.");
            Provision(s);
            Start(s, commandId: "unrelated-new-craft");
            Assert.AreEqual("paused_unverified", Legacy(Capture(s)).GetDictOrEmpty(id).GetString("status"), "Quarantine never reserves the station.");
        }

        [Test]
        public void DuplicateRecipeRows_HaveDistinctPersistentSourcePathIdentities()
        {
            GdDict source = Historical("duplicate_queue");
            RunSession s = Boot(); Import(s, source);
            GdDict first = Legacy(Capture(s)).DeepCopy();
            Assert.AreEqual(3, first.Count, "One active job plus two unpaid same-recipe queue rows stay distinct.");
            Assert.AreEqual(3, first.Values.OfType<GdDict>().Select(row => row.GetString("source_path")).Distinct().Count());
            Import(s, source);
            Equal(first, Legacy(Capture(s)), "Repeated source reads reuse exact IDs and original records.");
            GdDict snapshot = Capture(s); Valid(snapshot);
            Assert.IsTrue((bool)Call(s, "RestorePaidCraftingDomain", snapshot));
            Equal(first, Legacy(Capture(s)), "Owner snapshots retain quarantine identity and contents.");
        }

        [Test]
        public void KeepThenStartFresh_ArchivesDecisionAndPaysFullDuration_RepeatSurvivesRestore()
        {
            GdDict source = Historical("finished_unpaid");
            RunSession s = Boot(); Import(s, source);
            string id = OnlyLegacy(s);
            GdDict original = Legacy(Capture(s)).GetDictOrEmpty(id).GetDictOrEmpty("original_record").DeepCopy();
            Assert.IsTrue(Command(s, "ReconcileLegacyCraft", id, "keep_paused", "keep-1").GetBool("ok"));
            Provision(s);
            GdDict fresh = Command(s, "ReconcileLegacyCraft", id, "start_fresh", "fresh-1");
            Assert.IsTrue(fresh.GetBool("ok"), fresh.GetString("reason"));
            Assert.IsTrue(fresh.GetBool("committed"));
            string jobId = fresh.GetString("job_id"); Assert.IsNotEmpty(jobId);
            GdDict job = Job(s, jobId);
            Assert.AreEqual("paid", job.GetString("input_state"));
            Assert.AreEqual(0.0, job.GetFloat("progress_seconds"));
            Assert.AreEqual(s.CraftingState.GetCraftTime(Recipe), job.GetFloat("required_seconds"));
            Assert.AreEqual(0L, s.InventoryState.GetQuantity("scrap_metal"));
            Equal(original, Legacy(Capture(s)).GetDictOrEmpty(id).GetDictOrEmpty("original_record"), "Fresh start archives original immutable evidence.");
            GdDict snapshot = Capture(s); Valid(snapshot);
            Assert.IsTrue((bool)Call(s, "RestorePaidCraftingDomain", snapshot));
            GdDict beforeReplay = Capture(s);
            GdDict replay = Command(s, "ReconcileLegacyCraft", id, "start_fresh", "fresh-1");
            Assert.IsTrue(replay.GetBool("ok")); Assert.AreEqual(jobId, replay.GetString("job_id"));
            Equal(beforeReplay, Capture(s), "Restored decision receipt cannot charge or create another job.");
        }

        [Test]
        public void FailedFreshStart_PreservesQuarantineAndAllParticipants()
        {
            RunSession s = Boot(); Import(s, Historical("begun"));
            string id = OnlyLegacy(s);
            GdDict before = Capture(s); Valid(before);
            GdDict failed = Command(s, "ReconcileLegacyCraft", id, "start_fresh", "fresh-no-materials");
            Assert.IsFalse(failed.GetBool("ok")); Assert.AreEqual("missing_ingredients", failed.GetString("reason"));
            Equal(before, Capture(s), "Failed fresh start cannot archive, charge or erase old evidence.");
        }

        [Test]
        public void MissingRecipeAndStation_RemainInspectableAndAbandonableWithoutRewards()
        {
            GdDict source = Historical("begun");
            source.GetDictOrEmpty("active_craft")["recipe_id"] = "removed-recipe";
            source.GetDictOrEmpty("active_craft")["station_kind"] = "removed-station";
            GdDict station = source.GetDictOrEmpty("station_summaries").GetDictOrEmpty(Kind).DeepCopy();
            station["station_kind"] = "removed-station"; station["active_recipe_id"] = "removed-recipe";
            source["station_summaries"] = new GdDict { { "removed-station", station } };
            RunSession s = Boot(); Import(s, source);
            string id = OnlyLegacy(s);
            GdDict before = Capture(s);
            GdDict result = Command(s, "ReconcileLegacyCraft", id, "abandon", "abandon-1");
            Assert.IsTrue(result.GetBool("ok")); Assert.IsTrue(result.GetBool("committed"));
            Assert.AreEqual("abandoned", Legacy(Capture(s)).GetDictOrEmpty(id).GetString("disposition"));
            Equal(before.GetDictOrEmpty("participating_state").Get("inventory"), s.InventoryState.GetSummary(), "Abandon never refunds unknown inputs.");
            Equal(before.GetDictOrEmpty("participating_state").Get("progression"), s.PlayerProgression.GetSummary(), "Abandon grants no XP.");
            GdDict once = Capture(s);
            Assert.IsTrue(Command(s, "ReconcileLegacyCraft", id, "abandon", "abandon-1").GetBool("ok"));
            Equal(once, Capture(s), "Duplicate abandon returns existing receipt.");
            GdDict collision = Command(s, "ReconcileLegacyCraft", id, "keep_paused", "abandon-1");
            Assert.IsFalse(collision.GetBool("ok")); Assert.AreEqual("command_collision", collision.GetString("reason"));
            Equal(once, Capture(s), "Decision command payload collision cannot alter archived disposition.");
        }
    }
}
