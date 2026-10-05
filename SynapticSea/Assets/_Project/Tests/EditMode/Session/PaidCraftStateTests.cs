using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    // Provisioned real-catalog fixtures, not an earned acquisition or filesystem Continue claim.
    public abstract class PaidCraftFixture : InfraDataTestBase
    {
        protected const string Recipe = "weld_plating";
        protected const string Kind = "workbench";
        readonly List<RunSession> _sessions = new List<RunSession>();

        [TearDown]
        public void DisposePaidSessions()
        {
            foreach (RunSession session in _sessions) session.Dispose();
            _sessions.Clear();
        }

        protected RunSession Boot(bool components = false, bool paid = true, bool manualStudy = false)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.EnableComponentIntegration = components;
            deps.EnableManualStudy = manualStudy;
            // Deliberately optional in RED: reach the actual existing start before asserting missing authority.
            typeof(RunSessionDeps).GetField("EnablePaidCrafting")?.SetValue(deps, paid);
            rig.Session = RunSession.Create(deps);
            _sessions.Add(rig.Session);
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            rig.Session.ThreatManager.Threats.Clear();
            rig.Session.InventoryState.Items.Clear();
            rig.Session.PlayerProgression.Skills["fabrication"] = 4L;
            rig.Session.VitalsState.Stamina = rig.Session.VitalsState.MaxStamina;
            return rig.Session;
        }

        protected static object Call(RunSession session, string name, params object[] arguments)
        {
            MethodInfo method = typeof(RunSession).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .SingleOrDefault(m => m.Name == name && m.GetParameters().Length == arguments.Length);
            Assert.IsNotNull(method, "Paid crafting requires RunSession." + name + " (" + arguments.Length +
                " arguments) through the existing sole DomainTransactionCoordinator; legacy summaries cannot prove payment.");
            try { return method.Invoke(session, arguments); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        protected static GdDict Command(RunSession s, string name, params object[] arguments)
            => (GdDict)Call(s, name, arguments);
        protected static GdDict Capture(RunSession s) => Command(s, "CapturePaidCraftingDomain");
        protected static GdDict Paid(GdDict domain) => domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting");
        protected static GdDict Jobs(GdDict domain) => Paid(domain).GetDictOrEmpty("jobs");
        protected static GdDict Job(RunSession s, string id) => Jobs(Capture(s)).GetDictOrEmpty(id);
        protected static void Equal(object expected, object actual, string message)
            => Assert.IsTrue(V.VariantEquals(expected, actual), message);

        protected static void Provision(RunSession s, string recipeId = Recipe, long batches = 1)
        {
            GdDict recipe = s.CraftingState.GetRecipe(recipeId);
            Assert.IsFalse(recipe.IsEmpty, "Use a real catalog recipe.");
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
            {
                long count = V.I64(ingredient.Value) * batches;
                Assert.AreEqual(count, s.InventoryState.AddItem(V.Str(ingredient.Key), count));
            }
            string kind = recipe.GetString("station_kind");
            if (kind != "field_crafting")
            {
                Assert.IsTrue(s.CraftingStations.Any(st => st.IsValid && st.StationKind == kind), "Actual registered home station.");
                s.CraftingState.GetOrCreateStation(kind).SetPower(true);
            }
        }

        protected static string Start(RunSession s, string recipeId = Recipe, string commandId = "start-1")
        {
            GdDict result = Command(s, "RequestPaidCraft", s.CraftingState.GetStationKind(recipeId), recipeId, commandId);
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            Assert.IsTrue(result.GetBool("committed"), "Payment and job must publish together.");
            Assert.IsNotEmpty(result.GetString("job_id"));
            Assert.IsNotEmpty(result.GetString("commit_id"));
            return result.GetString("job_id");
        }

        protected static void Valid(GdDict domain)
            => Assert.IsTrue(DomainBundle.TryCreate(domain, out _, out string reason), "Positive valid owner prerequisite: " + reason);

        protected static string Enqueue(RunSession s, string commandId)
        {
            GdDict result = Command(s, "EnqueuePaidCraft", Kind, Recipe, commandId);
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            Assert.IsTrue(result.GetBool("committed"));
            Assert.IsNotEmpty(result.GetString("job_id"));
            return result.GetString("job_id");
        }
    }

    public class PaidCraftParityTests : PaidCraftFixture
    {
        [Test]
        public void ManyProgressSteps_RetainOneInternalProofAndKeepUserReplayAcrossRoundTrip()
        {
            RunSession s = Boot(); Provision(s); string id = Start(s, commandId: "retained-start");
            double step = Job(s, id).GetFloat("required_seconds") / 1024.0;
            s.AdvanceCrafting(step);
            string retiredCommand = Capture(s).GetDictOrEmpty("receipts").Values.OfType<GdDict>()
                .Single(row => row.GetDictOrEmpty("result").GetString("operation") == "craft_progress").GetString("command_id");
            for (int i = 1; i < 256; i++) s.AdvanceCrafting(step);
            GdDict saved = Capture(s); Valid(saved);
            Assert.AreEqual(2, saved.GetDictOrEmpty("receipts").Count, "Keep payment plus latest internal progress proof, not one receipt per frame.");
            Assert.AreEqual(Job(s, id).GetFloat("required_seconds") / 4.0, Job(s, id).GetFloat("progress_seconds"), 0.0000001);
            Assert.IsTrue(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(saved), out GdDict decoded, out string reason), reason);
            Equal(saved, decoded, "Typed codec retains bounded proof and command sequence exactly.");
            Assert.IsTrue((bool)Call(s, "RestorePaidCraftingDomain", decoded));
            GdDict repeat = Command(s, "RequestPaidCraft", Kind, Recipe, "retained-start");
            Assert.AreEqual(id, repeat.GetString("job_id")); Assert.IsTrue(repeat.GetBool("committed"));
            Assert.IsFalse(Command(s, "ResumePaidCraft", id, retiredCommand).GetBool("ok"), "External calls cannot reuse a pruned internal identity.");
            Assert.IsTrue(Command(s, "ResumePaidCraft", id, "retained-resume").GetBool("ok"));
            long sequence = Capture(s).GetInt("command_sequence"); s.AdvanceCrafting(step);
            GdDict continued = Capture(s); Valid(continued);
            Assert.Greater(continued.GetInt("command_sequence"), sequence);
            Assert.AreEqual(3, continued.GetDictOrEmpty("receipts").Count, "Payment and user resume retained alongside one internal proof.");
            Assert.AreEqual(0L, s.InventoryState.GetQuantity("plating"));
        }

        [Test]
        public void PaidStartAndDelivery_PreserveActualPopulatedEquipmentAuthority()
        {
            RunSession s = Boot(components: true); Provision(s);
            GdDict before = s.CaptureComponentDomain();
            Assert.Greater(before.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Count, 0, "Actual populated golden equipment positive.");
            Assert.IsTrue(s.BeginCraftFromPicker(Kind, Recipe).GetBool("ok"));
            s.AdvanceCrafting(100);
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating"));
            GdDict after = Capture(s);
            foreach (string key in new[] { "registry", "holders", "machinery", "physical_slots", "component_work", "registered_owners" })
                Equal(before.Get(key), after.Get(key), "Craft operation preserves exact equipment field " + key);
            Equal(after, s.CaptureComponentDomain(), "Equipment and paid consumers read one committed owner.");
        }

        [TestCase("inventory", false)]
        [TestCase("inventory", true)]
        [TestCase("paid_quality", true)]
        [TestCase("paid_identity", true)]
        public void ComponentPreparation_RejectsUnrelatedStackOrPaidStateMutation(string mutation, bool paid)
        {
            RunSession s = Boot(components: true, paid: paid);
            if (paid) { Provision(s); Start(s); }
            GdDict domain = paid ? Capture(s) : s.CaptureComponentDomain(); Valid(domain);
            GdDict holders = domain.GetDictOrEmpty("holders");
            GdDict instance = domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values.OfType<GdDict>()
                .First(row => holders.GetDictOrEmpty(row.GetString("holder")).GetString("kind") == "slot");
            string source = instance.GetString("holder"), destination = "player:player_local";
            var command = new GdDict {
                { "schema_version", 1L }, { "command_id", "conservation-control" }, { "operation", "transfer" },
                { "instance_id", instance.GetString("instance_id") }, { "source_holder_id", source }, { "destination_holder_id", destination },
                { "expected_domain_revision", domain.Get("revision") }, { "expected_instance_revision", instance.Get("revision") },
                { "expected_source_revision", holders.GetDictOrEmpty(source).Get("revision") }, { "expected_destination_revision", holders.GetDictOrEmpty(destination).Get("revision") }
            };
            MethodInfo prepare = typeof(DomainTransactionCoordinator).GetMethod("PrepareLive", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(prepare, "Existing private session staging seam; no new arbitrary public candidate API.");
            var control = new DomainTransactionCoordinator(domain);
            GdDict positive = (GdDict)prepare.Invoke(control, new object[] { command, new Func<GdDict, GdDict>(candidate => candidate) });
            Assert.IsTrue(positive.GetBool("ok"), "Actual populated equipment transfer must validate before unrelated mutation.");
            var attacked = new DomainTransactionCoordinator(domain);
            Func<GdDict, GdDict> mutate = candidate => {
                if (mutation == "inventory") candidate.GetDictOrEmpty("participating_state").GetDictOrEmpty("inventory").GetDictOrEmpty("items")["scrap_metal"] = 999L;
                else
                {
                    GdDict job = Jobs(candidate).Values.OfType<GdDict>().Single();
                    if (mutation == "paid_quality") job["quality_score"] = job.GetFloat("quality_score") + 0.01;
                    else job["actor_id"] = "another-actor";
                }
                return candidate;
            };
            bool accepted;
            try { accepted = ((GdDict)prepare.Invoke(attacked, new object[] { command, mutate })).GetBool("ok"); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { accepted = false; }
            Assert.IsFalse(accepted, "Component-only preparation cannot mint stacks or alter paid identity/payment.");
            Equal(domain, attacked.GetSummary(), "Rejected preparation leaves committed owner intact.");
        }

        [Test]
        public void DeliveredFoodAge_SurvivesRetryAndExactOwnerRestoreWithoutFreshening()
        {
            RunSession s = Boot(); Provision(s, "cook_basic_meal"); string id = Start(s, "cook_basic_meal");
            s.AdvanceCrafting(100); Assert.IsTrue(s.SpoilageState.HasFood("cooked_meal"));
            s.SpoilageState.Tick(37);
            GdDict aged = s.SpoilageState.GetSummary();
            GdDict saved = Capture(s);
            Equal(aged, saved.GetDictOrEmpty("participating_state").Get("spoilage"), "Spoilage is an exact committed generation participant.");
            Command(s, "RetryPaidCraft", id, "aged-retry"); Equal(aged, s.SpoilageState.GetSummary(), "Replay cannot re-register/freshen food.");
            s.SpoilageState.Clear(); Assert.IsTrue((bool)Call(s, "RestorePaidCraftingDomain", saved));
            Equal(aged, s.SpoilageState.GetSummary(), "Owner restore replaces exact food ages.");
            Command(s, "RetryPaidCraft", id, "aged-after-restore"); Equal(aged, s.SpoilageState.GetSummary(), "Restored terminal replay cannot freshen food.");
        }

        [Test]
        public void NewAcceptedFoodJob_PreservesAlreadyTrackedAgeAndConfiguration()
        {
            RunSession s = Boot(); Provision(s, "cook_basic_meal");
            Assert.IsTrue(s.BeginCraftFromPicker("kitchen", "cook_basic_meal").GetBool("ok")); s.AdvanceCrafting(100);
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("cooked_meal"));
            s.SpoilageState.Tick(37); GdDict aged = s.SpoilageState.GetSummary();
            Provision(s, "cook_basic_meal");
            Assert.IsTrue(s.BeginCraftFromPicker("kitchen", "cook_basic_meal").GetBool("ok")); s.AdvanceCrafting(100);
            Assert.AreEqual(2L, s.InventoryState.GetQuantity("cooked_meal"));
            Equal(aged, s.SpoilageState.GetSummary(), "Existing HasFood guard applies even to a genuinely new accepted job; no freshness blending/reset.");
        }

        [TestCase("run_id")]
        [TestCase("actor_id")]
        [TestCase("inventory_owner_id")]
        [TestCase("job_id")]
        [TestCase("recipe_hash")]
        [TestCase("consumed")]
        [TestCase("required_seconds")]
        [TestCase("start_skill")]
        [TestCase("start_tier")]
        [TestCase("future_version")]
        [TestCase("negative_progress")]
        [TestCase("over_progress")]
        [TestCase("delivered_without_receipt")]
        [TestCase("unpaid_with_payment")]
        public void ClaimedPaidIdentityAndResolutionMutation_RejectsValidationAndRestore(string field)
        {
            RunSession s = Boot(); Provision(s); string id = Start(s);
            GdDict good = Capture(s); Valid(good);
            Assert.IsTrue((bool)Call(s, "ValidatePaidCraftingRestore", good, null));
            GdDict bad = good.DeepCopy(), job = Jobs(bad).GetDictOrEmpty(id);
            if (field == "consumed") job.GetDictOrEmpty("consumed")["scrap_metal"] = 1L;
            else if (field == "required_seconds") job[field] = job.GetFloat(field) + 1;
            else if (field == "start_skill" || field == "start_tier") job[field] = job.GetInt(field) + 1;
            else if (field == "future_version") Paid(bad)["schema_version"] = 999L;
            else if (field == "negative_progress") job["progress_seconds"] = -1.0;
            else if (field == "over_progress") job["progress_seconds"] = job.GetFloat("required_seconds") + 1;
            else if (field == "delivered_without_receipt") { job["status"] = "completed_delivered"; job["progress_seconds"] = job.Get("required_seconds"); }
            else if (field == "unpaid_with_payment") { job["input_state"] = "unpaid"; job["status"] = "unpaid"; }
            else job[field] = "different-identity";
            GdDict inventory = s.InventoryState.GetSummary(), craft = s.CraftingState.GetSummary(), xp = s.PlayerProgression.GetSummary();
            Assert.IsFalse((bool)Call(s, "ValidatePaidCraftingRestore", bad, null));
            Assert.IsFalse((bool)Call(s, "RestorePaidCraftingDomain", bad));
            Equal(inventory, s.InventoryState.GetSummary(), "Direct inventory preserved."); Equal(craft, s.CraftingState.GetSummary(), "Direct craft preserved.");
            Equal(xp, s.PlayerProgression.GetSummary(), "Direct progression preserved."); Equal(good, Capture(s), "Authority preserved.");
        }

        [Test]
        public void PendingDelivery_InterveningProgressionAtPublicationPreservesExternalAward()
        {
            RunSession s = Boot(); Provision(s); string id = Start(s);
            long maximum = s.InventoryState.GetDefinition("plating").GetInt("max_stack", 99);
            s.InventoryState.AddItem("plating", maximum); s.AdvanceCrafting(100);
            Assert.AreEqual("completed_pending_delivery", Job(s, id).GetString("status"));
            s.InventoryState.RemoveItem("plating", maximum); GdDict before = Capture(s);
            GdDict external = null; bool reached = false;
            s.ComponentStageHook = stage => { if (stage == "publication" && !reached) { reached = true; s.PlayerProgression.GrantXp("fabrication", 7, false); external = s.PlayerProgression.GetSummary(); } };
            GdDict rejected;
            try { rejected = Command(s, "RetryPaidCraft", id, "stale-completion"); }
            finally { s.ComponentStageHook = null; }
            Assert.IsTrue(reached); Assert.IsFalse(rejected.GetBool("committed"));
            Equal(external, s.PlayerProgression.GetSummary(), "Stale candidate cannot erase independently awarded XP.");
            Assert.AreEqual(0L, s.InventoryState.GetQuantity("plating"));
            Equal(Jobs(before), Jobs(Capture(s)), "Pending job remains payable for delivery.");
            Equal(before.Get("receipts"), Capture(s).Get("receipts"), "No stale completion receipt.");
        }

        [Test]
        public void PortableRestoredWork_RequiresConsentWithoutSecondIngredients()
        {
            RunSession s = Boot(); Provision(s, "field_bandage"); string id = Start(s, "field_bandage"); s.AdvanceCrafting(1);
            GdDict saved = Capture(s); double progress = Job(s, id).GetFloat("progress_seconds");
            Assert.IsTrue((bool)Call(s, "RestorePaidCraftingDomain", saved)); s.AdvanceCrafting(100);
            Assert.AreEqual(progress, Job(s, id).GetFloat("progress_seconds")); Assert.AreEqual(0L, s.InventoryState.GetQuantity("field_bandage"));
            Assert.IsTrue(Command(s, "ResumePaidCraft", id, "field-resume").GetBool("ok")); s.AdvanceCrafting(100);
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("field_bandage")); Assert.AreEqual(0L, s.InventoryState.GetQuantity("medical_gauze"));
        }

        [Test]
        public void CompletionNotificationFailure_RetainsCommittedOutputRewardAndSpoilage()
        {
            RunSession s = Boot(); Provision(s, "cook_basic_meal");
            foreach (object key in s.PlayerProgression.Skills.Keys.ToArray()) s.PlayerProgression.Skills[key] = 4L;
            string id = Start(s, "cook_basic_meal");
            bool reached = false;
            s.ComponentDomainChanged += result => {
                if (result.GetDictOrEmpty("result").GetString("operation") == "craft_complete")
                { reached = true; throw new InvalidOperationException("paid-presentation-fault"); }
            };
            s.AdvanceCrafting(100);
            Assert.IsTrue(reached, "The shared publisher emits committed completion notification.");
            Assert.AreEqual("completed_delivered", Job(s, id).GetString("status"));
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("cooked_meal"));
            Assert.IsTrue(s.SpoilageState.HasFood("cooked_meal"), "Core committed spoilage registration precedes fallible presentation.");
            GdDict once = Capture(s); Command(s, "RetryPaidCraft", id, "retry-presented");
            Equal(once.Get("participating_state"), Capture(s).Get("participating_state"), "Notification retry cannot grant reward twice.");
        }

        [Test]
        public void ActualPowerAllocation_OutagePausesLivePaidWork_RecoveryDoesNotRecharge()
        {
            RunSession s = Boot(); Provision(s);
            foreach (ShipSystem system in s.ShipSystemsManager.Systems.Values)
                foreach (ShipSubcomponent sub in system.Subcomponents) sub.Health = 1.0;
            Assert.IsTrue(s.SetManualPowerRoute("stations", 10));
            Assert.Greater(s.PowerGridState.GetAllocationRatio("stations"), 0.0, "Actual allocation positive prerequisite.");
            string id = Start(s);
            MethodInfo tick = typeof(RunSession).GetMethod("RecomputeExpandedShipSystems", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(tick);
            tick.Invoke(s, new object[] { 1.0 });
            Assert.Greater(Job(s, id).GetFloat("progress_seconds"), 0.0);
            Assert.IsTrue(s.SetManualPowerRoute("stations", 0));
            Assert.AreEqual(0.0, s.PowerGridState.GetAllocationRatio("stations"));
            double progress = Job(s, id).GetFloat("progress_seconds");
            tick.Invoke(s, new object[] { 100.0 });
            Assert.AreEqual(progress, Job(s, id).GetFloat("progress_seconds"), "Actual power allocation outage, not convenience AdvanceCrafting.");
            Assert.AreEqual(0L, s.InventoryState.GetQuantity("plating"));
            Assert.IsTrue(s.SetManualPowerRoute("stations", 10));
            tick.Invoke(s, new object[] { 100.0 });
            Assert.AreEqual("completed_delivered", Job(s, id).GetString("status"));
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating")); Assert.AreEqual(0L, s.InventoryState.GetQuantity("scrap_metal"));
        }

        [Test]
        public void ExactBooksReadEvidence_SeedsOnlyExplicitMappedKnowledgeWithoutXp()
        {
            RunSession s = Boot();
            GdDict recipe = s.CraftingState.GetRecipe("craft_thruster_nozzle");
            Assert.AreEqual("book", recipe.GetString("knowledge_source"));
            Assert.AreEqual("fabrication_schematic_basic", recipe.GetString("knowledge_book_id"), "Selected F02 manual maps only authored nozzle knowledge.");
            string book = recipe.GetString("knowledge_book_id");
            Assert.AreEqual(1L, s.InventoryState.AddItem(book, 1));
            GdDict xp = s.PlayerProgression.GetSummary(); GdArray log = s.TrainingEventBus.GetLog();
            Assert.IsFalse(Paid(Capture(s)).GetDictOrEmpty("knowledge").GetDictOrEmpty("known").GetBool("craft_thruster_nozzle"));
            // Authored mapping still distinguishes exact read evidence from possession/skill.
            Assert.IsFalse(Paid(Capture(s)).GetDictOrEmpty("knowledge").GetDictOrEmpty("known").GetBool("craft_thruster_nozzle"));
            s.PlayerProgression.BooksRead[book] = true;
            GdDict beforeReadCapture = s.PlayerProgression.GetSummary();
            Assert.IsTrue(Paid(Capture(s)).GetDictOrEmpty("knowledge").GetDictOrEmpty("known").GetBool("craft_thruster_nozzle"));
            Equal(beforeReadCapture, s.PlayerProgression.GetSummary(), "Knowledge seeding never replays book XP.");
            Equal(log, s.TrainingEventBus.GetLog(), "Knowledge seeding emits no study/training event.");
            Equal(xp.Get("skill_xp"), s.PlayerProgression.GetSummary().Get("skill_xp"), "No book XP issued.");
        }

        [Test]
        public void PortablePolicy_RemainsSkillUngatedWithExplicitAboveCurrentThresholdFixture()
        {
            RunSession s = Boot(); Provision(s, "field_bandage");
            s.PlayerProgression.Skills["fabrication"] = 0L;
            // FieldCraftingState owns its own catalog. Both loaded recipe rows share the resource recipe references.
            GdDict fieldRecipe = s.FieldCraftingState.GetFieldRecipes().OfType<GdDict>().Single(r => r.GetString("recipe_id") == "field_bandage");
            Assert.AreEqual(0L, fieldRecipe.GetInt("required_skill_level"));
            s.CraftingState.GetRecipe("field_bandage")["required_skill_level"] = 5L;
            Assert.AreEqual(5L, s.CraftingState.GetRecipe("field_bandage").GetInt("required_skill_level"));
            Assert.IsTrue(s.BeginFieldCraftRecipe("field_bandage"), "Paid phase gate must preserve portable emergency skill policy.");
            GdDict job = Jobs(Capture(s)).Values.OfType<GdDict>().Single();
            Assert.AreEqual(0L, job.GetInt("start_skill"));
            Assert.AreEqual(5L, job.GetDictOrEmpty("recipe_definition").GetInt("required_skill_level"));
        }

        static PlayerProgressionState CloneProgression(RunSession s, GdDict summary)
        {
            var clone = new PlayerProgressionState();
            var classes = ClassDefinition.LoadAll(); classes.TryGetValue(s.PlayerProgression.ClassId, out ClassDefinition definition);
            clone.Configure(definition, PlayerProgressionState.LoadSkillsCatalog(), s.PlayerProgression.GetBooksCatalog());
            clone.ApplySummary(summary);
            clone.XpMultipliers.Clear(); foreach (var pair in s.PlayerProgression.XpMultipliers) clone.XpMultipliers[pair.Key] = pair.Value;
            return clone;
        }

        [Test]
        public void TwoOutputRecipe_WithOnlyOneSlotFree_DeliversNothingUntilEntireOutputFits()
        {
            RunSession s = Boot(); Provision(s, "craft_sealant");
            Assert.IsTrue(s.BeginCraftFromPicker(Kind, "craft_sealant").GetBool("ok"));
            long maximum = s.InventoryState.GetDefinition("sealant").GetInt("max_stack", 99);
            Assert.AreEqual(maximum - 1, s.InventoryState.AddItem("sealant", maximum - 1));
            GdDict progression = s.PlayerProgression.GetSummary(); GdArray log = s.TrainingEventBus.GetLog();
            s.AdvanceCrafting(100);
            Assert.AreEqual(maximum - 1, s.InventoryState.GetQuantity("sealant"), "Atomic two-unit output cannot partially fill one available slot.");
            Equal(progression, s.PlayerProgression.GetSummary(), "Partial capacity grants zero XP.");
            Equal(log, s.TrainingEventBus.GetLog(), "Partial capacity grants no training event.");
            GdDict domain = Capture(s), job = Jobs(domain).Values.OfType<GdDict>().Single();
            Assert.AreEqual("completed_pending_delivery", job.GetString("status"));
            Assert.IsFalse(domain.GetDictOrEmpty("receipts").Has(job.GetString("completion_commit_id")));
            Assert.AreEqual(1L, s.InventoryState.RemoveItem("sealant", 1));
            Assert.IsTrue(Command(s, "RetryPaidCraft", job.GetString("job_id"), "full-two-output").GetBool("committed"));
            Assert.AreEqual(maximum, s.InventoryState.GetQuantity("sealant"));
            Assert.AreEqual(log.Count + 1, s.TrainingEventBus.GetLog().Count);
        }

        [TestCase("weld_plating", "fabricate_part", "normal")]
        [TestCase("craft_power_cell", "fabricate_part", "normal")]
        [TestCase("cook_basic_meal", "cook_meal", "normal")]
        [TestCase("synthesize_nutrient_paste", "cook_meal", "normal")]
        [TestCase("craft_stimulant", "compound_stimulant", "normal")]
        [TestCase("field_bandage", "fabricate_part", "normal")]
        [TestCase("weld_plating", "fabricate_part", "filter")]
        [TestCase("weld_plating", "fabricate_part", "gate")]
        public void PaidReward_ExactlyMatchesExistingTrainingPolicy_AndReceiptReplayCannotTrain(string recipe, string eventId, string policy)
        {
            RunSession s = Boot(); Provision(s, recipe);
            foreach (object key in s.PlayerProgression.Skills.Keys.ToArray()) s.PlayerProgression.Skills[key] = 4L;
            foreach (object key in s.PlayerProgression.XpMultipliers.Keys.ToArray()) s.PlayerProgression.XpMultipliers[key] = 1.375;
            s.TrainingEventBus.EventFilter = (evt, target) => policy == "filter";
            s.TrainingEventBus.SkillGate = skill => policy != "gate";
            string output = s.CraftingState.GetProduces(recipe).GetString("item_id");
            Assert.IsTrue(s.BeginCraftFromPicker(s.CraftingState.GetStationKind(recipe), recipe).GetBool("ok"));
            GdDict before = s.PlayerProgression.GetSummary();
            PlayerProgressionState expected = CloneProgression(s, before);
            var oracle = new TrainingEventBus(); oracle.Configure(); oracle.ApplySummary(s.TrainingEventBus.ToDict());
            oracle.EventFilter = s.TrainingEventBus.EventFilter; oracle.SkillGate = s.TrainingEventBus.SkillGate;
            GdDict record = oracle.Emit(eventId, output, expected);
            s.AdvanceCrafting(100);
            Equal(expected.GetSummary(), s.PlayerProgression.GetSummary(), "Exact existing class/runtime multiplier, fractions, cross-training and gated progression.");
            Assert.AreEqual(oracle.GetTotalXpDelivered(), s.TrainingEventBus.GetTotalXpDelivered());
            Assert.AreEqual(oracle.GetDroppedCount(), s.TrainingEventBus.GetDroppedCount());
            Assert.AreEqual(oracle.GetEventCount(), s.TrainingEventBus.GetEventCount());
            if (record != null)
            {
                GdDict actual = s.TrainingEventBus.GetLog().OfType<GdDict>().Last();
                Assert.IsTrue(actual.GetBool("receipt_owned"), "Paid reward must be an already-applied receipt event, not a replayable legacy grant.");
                foreach (var field in record) Equal(field.Value, actual.Get(field.Key), "Training field " + field.Key);
                GdDict job = Jobs(Capture(s)).Values.OfType<GdDict>().Single();
                Assert.AreEqual(job.GetString("completion_commit_id"), actual.GetString("commit_id"));
                var onlyPaid = new TrainingEventBus(); onlyPaid.Configure(); onlyPaid.RecordApplied(record, actual.GetString("commit_id"));
                PlayerProgressionState replay = CloneProgression(s, before);
                Equal(before, replay.GetSummary(), "Replay positive before image.");
                Assert.AreEqual(0L, onlyPaid.ReplayInto(replay)); Equal(before, replay.GetSummary(), "Receipt-owned replay grants no XP.");
            }
            else Assert.AreEqual("completed_delivered", Jobs(Capture(s)).Values.OfType<GdDict>().Single().GetString("status"));
            if (s.InventoryState.GetCategory(output) == "food" || s.InventoryState.GetCategory(output) == "drink")
                Assert.IsTrue(s.SpoilageState.HasFood(output), "Committed food output retains spoilage registration.");
        }

        [TestCase("inventory")]
        [TestCase("job")]
        [TestCase("receipt")]
        [TestCase("publication")]
        [TestCase("live_inventory")]
        public void PendingDeliveryFault_PreservesDirectViewsAndReceipt_RetryGrantsOnce(string fault)
        {
            RunSession s = Boot(); Provision(s); string id = Start(s);
            long maximum = s.InventoryState.GetDefinition("plating").GetInt("max_stack", 99);
            s.InventoryState.AddItem("plating", maximum); s.AdvanceCrafting(100);
            Assert.AreEqual("completed_pending_delivery", Job(s, id).GetString("status"));
            s.InventoryState.RemoveItem("plating", maximum);
            GdDict owner = Capture(s), inventory = s.InventoryState.GetSummary(), craft = s.CraftingState.GetSummary(), field = s.FieldCraftingState.GetSummary(), xp = s.PlayerProgression.GetSummary();
            GdArray training = s.TrainingEventBus.GetLog();
            bool reached = false; s.ComponentStageHook = stage => { if (stage == fault) { reached = true; throw new InvalidOperationException("paid-delivery-fault"); } };
            GdDict failure;
            try { failure = Command(s, "RetryPaidCraft", id, "fault-delivery"); }
            finally { s.ComponentStageHook = null; }
            Assert.IsTrue(reached); Assert.IsFalse(failure.GetBool("committed"));
            Equal(inventory, s.InventoryState.GetSummary(), "Direct inventory before capture.");
            Equal(craft, s.CraftingState.GetSummary(), "Direct station projection before capture.");
            Equal(field, s.FieldCraftingState.GetSummary(), "Direct field projection before capture.");
            Equal(xp, s.PlayerProgression.GetSummary(), "Direct progression before capture."); Equal(training, s.TrainingEventBus.GetLog(), "Direct training before capture.");
            Equal(owner, Capture(s), "Failed completion owns no receipt.");
            Assert.IsTrue(Command(s, "RetryPaidCraft", id, "fault-delivery").GetBool("committed"));
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating"));
            GdDict once = Capture(s); Command(s, "RetryPaidCraft", id, "fault-delivery"); Equal(once, Capture(s), "Retry once.");
        }

        [TestCase("pending")]
        [TestCase("queue")]
        public void RestoredPendingOrFundedQueue_WaitsForExplicitConsent(string shape)
        {
            RunSession s = Boot(); Provision(s); string id;
            if (shape == "queue") id = Enqueue(s, "funded-queue");
            else
            {
                id = Start(s); long maximum = s.InventoryState.GetDefinition("plating").GetInt("max_stack", 99);
                s.InventoryState.AddItem("plating", maximum); s.AdvanceCrafting(100);
                Assert.AreEqual("completed_pending_delivery", Job(s, id).GetString("status"));
                s.InventoryState.RemoveItem("plating", maximum);
            }
            GdDict saved = Capture(s); Valid(saved);
            Assert.IsTrue((bool)Call(s, "RestorePaidCraftingDomain", saved));
            GdDict inventory = s.InventoryState.GetSummary(), xp = s.PlayerProgression.GetSummary();
            s.AdvanceCrafting(100);
            Equal(inventory, s.InventoryState.GetSummary(), "Restore/time cannot deliver or charge."); Equal(xp, s.PlayerProgression.GetSummary(), "Restore cannot train.");
            Assert.IsTrue(Job(s, id).GetBool("resume_required"));
            Assert.IsTrue(Command(s, "ResumePaidCraft", id, "explicit-consent").GetBool("ok"));
            if (shape == "pending") Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating"));
            else Assert.AreEqual("paid", Job(s, id).GetString("input_state"));
        }
    }

    public class PaidCraftStateTests : PaidCraftFixture
    {
        [TestCase("picker")]
        [TestCase("station")]
        [TestCase("field")]
        [TestCase("model")]
        public void ActualStart_PublishesPaidJobAndIndependentPaymentReceipt(string path)
        {
            RunSession s = Boot();
            string recipe = path == "field" ? "field_bandage" : Recipe;
            Provision(s, recipe);
            if (path == "station") Assert.IsTrue(s.CraftingStations.Single(st => st.IsValid && st.StationKind == Kind).TryCraftRecipe(recipe));
            else if (path == "field") Assert.IsTrue(s.BeginFieldCraftRecipe(recipe));
            else if (path == "model") Assert.IsTrue(s.CraftingState.BeginCraft(recipe, s.InventoryState, s.MaterialState, 4));
            else
            {
                GdDict result = s.BeginCraftFromPicker(Kind, recipe);
                Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
                Assert.IsTrue(result.GetBool("committed"), "Actual picker start debited inputs but exposed no committed payment/job identity.");
                Assert.IsNotEmpty(result.GetString("job_id"));
            }
            foreach (var input in s.CraftingState.GetRecipe(recipe).GetDictOrEmpty("ingredients"))
                Assert.AreEqual(0L, s.InventoryState.GetQuantity(V.Str(input.Key)), "Exactly one recipe set was paid.");
            GdDict domain = Capture(s);
            Valid(domain);
            Assert.AreEqual(3L, domain.GetInt("schema_version"));
            Assert.AreEqual("craft_only", domain.GetString("domain_mode"));
            Assert.IsFalse(s.ComponentIntegrationEnabled);
            Assert.IsTrue(domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").IsEmpty);
            foreach (string key in new[] { "holders", "machinery", "physical_slots", "component_work" })
                Assert.IsTrue(domain.GetDictOrEmpty(key).IsEmpty, key + " stays inactive in ordinary paid mode.");
            Assert.AreEqual(1, Jobs(domain).Count);
            GdDict job = Jobs(domain).Values.OfType<GdDict>().Single();
            Assert.AreEqual(recipe, job.GetString("recipe_id"));
            Assert.AreEqual("paid", job.GetString("input_state"));
            Assert.IsTrue(domain.GetDictOrEmpty("receipts").Has(job.GetString("payment_commit_id")), "Independent payment receipt is required.");
            Assert.IsFalse(domain.GetDictOrEmpty("receipts").Has(job.GetString("completion_commit_id")), "Reserved completion ID is not a fabricated receipt.");
            Equal(s.CraftingState.GetRecipe(recipe).GetDictOrEmpty("ingredients"), job.GetDictOrEmpty("consumed"), "Bind exact paid multiset.");
        }

        [Test]
        public void ComponentAndPaidModes_ShareTheSameCoordinatorAndSnapshot()
        {
            RunSession s = Boot(components: true);
            Provision(s);
            Assert.IsTrue(s.BeginCraftFromPicker(Kind, Recipe).GetBool("ok"));
            GdDict paid = Capture(s);
            Assert.AreEqual("components_and_craft", paid.GetString("domain_mode"));
            Equal(paid, s.CaptureComponentDomain(), "Two mode APIs must read one authority.");
            var owners = typeof(RunSession).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(DomainTransactionCoordinator)).Select(f => f.GetValue(s)).Where(o => o != null).Distinct().ToArray();
            Assert.AreEqual(1, owners.Length, "Never compose two live publishers of the same inventory/progression.");
        }

        [Test]
        public void SameCommandReplaysReceipt_DifferentPayloadRejectsWithoutSecondDebit()
        {
            RunSession s = Boot(); Provision(s, batches: 2);
            string id = Start(s);
            GdDict before = Capture(s);
            GdDict replay = Command(s, "RequestPaidCraft", Kind, Recipe, "start-1");
            Assert.IsTrue(replay.GetBool("ok")); Assert.AreEqual(id, replay.GetString("job_id"));
            Equal(before, Capture(s), "Matching command replay cannot publish another revision or debit.");
            GdDict collision = Command(s, "RequestPaidCraft", Kind, "craft_lockpick_set", "start-1");
            Assert.IsFalse(collision.GetBool("ok")); Assert.AreEqual("command_collision", collision.GetString("reason"));
            Equal(before, Capture(s), "Payload collision preserves authority.");
        }

        [Test]
        public void PaidRestore_RequiresResumeAndNeverRechargesOrRerollsQuality()
        {
            RunSession s = Boot(); Provision(s);
            string id = Start(s); s.AdvanceCrafting(2);
            GdDict snapshot = Capture(s); Valid(snapshot);
            GdDict original = Jobs(snapshot).GetDictOrEmpty(id);
            Assert.IsTrue((bool)Call(s, "RestorePaidCraftingDomain", snapshot));
            Assert.IsTrue(Job(s, id).GetBool("resume_required"));
            s.CraftingState.GetStation(Kind).SetPower(false);
            s.CraftingState.GetStation(Kind).SetPower(true);
            s.AdvanceCrafting(100);
            Equal(original.Get("progress_seconds"), Job(s, id).Get("progress_seconds"), "Power recovery and time do not grant restored consent.");
            s.PlayerProgression.Skills["fabrication"] = 0L;
            s.CraftingState.GetStation(Kind).Level = 0;
            Assert.IsTrue(Command(s, "ResumePaidCraft", id, "resume-1").GetBool("ok"));
            s.AdvanceCrafting(100);
            GdDict done = Job(s, id);
            Assert.AreEqual("completed_delivered", done.GetString("status"));
            Equal(original.Get("quality_score"), done.Get("quality_score"), "Completion retains resolved start quality.");
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating"));
            GdDict once = Capture(s);
            s.AdvanceCrafting(100);
            Command(s, "RetryPaidCraft", id, "retry-done");
            Equal(once.Get("participating_state"), Capture(s).Get("participating_state"), "Delivered retry/time cannot duplicate output or XP.");
        }

        [Test]
        public void OutputFull_WaitsAndRetriesWithoutPositiveTick_WithRewardOnce()
        {
            RunSession s = Boot(); Provision(s);
            string id = Start(s);
            long maximum = s.InventoryState.GetDefinition("plating").GetInt("max_stack", 99);
            Assert.AreEqual(maximum, s.InventoryState.AddItem("plating", maximum));
            GdDict progression = s.PlayerProgression.GetSummary();
            GdArray training = s.TrainingEventBus.GetLog().DeepCopy();
            s.AdvanceCrafting(100);
            Assert.AreEqual("completed_pending_delivery", Job(s, id).GetString("status"));
            Equal(progression, s.PlayerProgression.GetSummary(), "Full output capacity cannot grant XP.");
            Equal(training, s.TrainingEventBus.GetLog(), "Pending delivery cannot emit training.");
            Assert.AreEqual(maximum, s.InventoryState.RemoveItem("plating", maximum));
            GdDict delivered = Command(s, "RetryPaidCraft", id, "deliver-1");
            Assert.IsTrue(delivered.GetBool("ok")); Assert.IsTrue(delivered.GetBool("committed"));
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating"));
            Assert.AreEqual("completed_delivered", Job(s, id).GetString("status"));
            Assert.Greater(s.TrainingEventBus.GetLog().Count, training.Count, "Accepted delivery retains existing craft training.");
            GdDict once = Capture(s);
            Command(s, "RetryPaidCraft", id, "deliver-1");
            Equal(once, Capture(s), "Duplicate delivery returns receipt without replaying effects.");
        }

        [Test]
        public void QueueHead_RemainsUnpaidWhenBlocked_ExplicitRetryPaysExactlyOnce()
        {
            RunSession s = Boot(); Provision(s);
            string active = Start(s);
            string head = Enqueue(s, "queue-1"), tail = Enqueue(s, "queue-2");
            Assert.AreNotEqual(head, tail);
            Assert.AreEqual("unpaid", Job(s, head).GetString("input_state"));
            Assert.IsEmpty(Job(s, head).GetString("payment_commit_id"));
            s.AdvanceCrafting(100);
            Assert.AreEqual("completed_delivered", Job(s, active).GetString("status"));
            Assert.AreEqual("unpaid", Job(s, head).GetString("input_state"));
            Assert.AreEqual("missing_ingredients", Job(s, head).GetString("blocked_reason"));
            Assert.AreEqual("unpaid", Job(s, tail).GetString("input_state"));
            Provision(s);
            Assert.IsTrue(Command(s, "RetryPaidCraft", head, "retry-head").GetBool("ok"));
            Assert.AreEqual("paid", Job(s, head).GetString("input_state"));
            Assert.AreEqual("unpaid", Job(s, tail).GetString("input_state"));
            Assert.AreEqual(0L, s.InventoryState.GetQuantity("scrap_metal"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancelRunningOrPending_ArchivesNoRefundOutputXp_LeavesQueuePaused(bool pending)
        {
            RunSession s = Boot(); Provision(s);
            string id = Start(s), queued = Enqueue(s, "queue-1");
            if (pending)
            {
                long maximum = s.InventoryState.GetDefinition("plating").GetInt("max_stack", 99);
                Assert.AreEqual(maximum, s.InventoryState.AddItem("plating", maximum));
                s.AdvanceCrafting(100);
                Assert.AreEqual("completed_pending_delivery", Job(s, id).GetString("status"));
            }
            GdDict inventory = s.InventoryState.GetSummary(), xp = s.PlayerProgression.GetSummary();
            GdDict cancelled = Command(s, "CancelPaidCraft", id, "cancel-1");
            Assert.IsTrue(cancelled.GetBool("ok")); Assert.IsTrue(cancelled.GetBool("committed"));
            Assert.AreEqual("cancelled", Job(s, id).GetString("status"));
            Equal(inventory, s.InventoryState.GetSummary(), "Cancellation neither refunds nor delivers.");
            Equal(xp, s.PlayerProgression.GetSummary(), "Cancellation grants no progression.");
            Assert.AreEqual("unpaid", Job(s, queued).GetString("input_state"));
            Assert.IsTrue(Job(s, queued).GetBool("resume_required"));
            Provision(s); s.AdvanceCrafting(100);
            Assert.AreEqual("unpaid", Job(s, queued).GetString("input_state"), "Cancellation cannot silently process remaining queue.");
        }

        [Test]
        public void RestoreEarlierEmptyOwner_ClearsLiveQueueAndBothActiveProjections()
        {
            RunSession s = Boot();
            GdDict empty = Capture(s); Valid(empty);
            Provision(s); Start(s); Enqueue(s, "queue-1");
            Provision(s, "field_bandage"); Start(s, "field_bandage", "field-1");
            Assert.AreEqual(3, Jobs(Capture(s)).Count);
            Assert.IsTrue((bool)Call(s, "RestorePaidCraftingDomain", empty));
            Assert.IsTrue(Jobs(Capture(s)).IsEmpty);
            Assert.IsFalse(s.CraftingState.IsCrafting()); Assert.IsFalse(s.FieldCraftingState.IsCrafting());
            Assert.AreEqual(0, s.CraftingState.GetOrCreateStation(Kind).Queue.Count, "Exact restore cannot merge old queues.");
        }

        [TestCase("payment")]
        [TestCase("quality")]
        [TestCase("owner")]
        [TestCase("progress")]
        public void ContradictoryPaidRecord_RejectsWithoutChangingAnyLiveParticipant(string mutation)
        {
            RunSession s = Boot(); Provision(s); string id = Start(s);
            GdDict valid = Capture(s); Valid(valid);
            Assert.IsTrue((bool)Call(s, "ValidatePaidCraftingRestore", valid, null), "Unmodified paid owner must validate before adversarial mutation.");
            GdDict bad = valid.DeepCopy(), row = Jobs(bad).GetDictOrEmpty(id);
            if (mutation == "payment") bad.GetDictOrEmpty("receipts").Erase(row.GetString("payment_commit_id"));
            if (mutation == "quality") row["quality_score"] = row.GetFloat("quality_score") + 0.125;
            if (mutation == "owner") row["station_owner_id"] = "invented-other-ship";
            if (mutation == "progress") row["progress_seconds"] = double.NaN;
            Assert.IsFalse((bool)Call(s, "RestorePaidCraftingDomain", bad));
            Equal(valid, Capture(s), "Rejected restore preserves exact authoritative inventory/jobs/progression/receipts.");
        }

        [Test]
        public void MutableLegacyProjection_CannotRewriteCanonicalPaymentOrQuality()
        {
            RunSession s = Boot(); Provision(s); string id = Start(s);
            GdDict before = Job(s, id);
            GdDict legacy = s.CraftingState.GetSummary();
            legacy.GetDictOrEmpty("active_craft")["quality_score"] = 999.0;
            s.CraftingState.ApplySummary(legacy);
            Equal(before, Job(s, id), "Capture must never rebuild canonical paid rows from legacy mutable summaries.");
        }

        [TestCase("input")]
        [TestCase("output")]
        public void OrdinaryPaidComponentForms_RejectBeforeAnyPayment(string side)
        {
            RunSession s = Boot(); Provision(s);
            // Prove actual unmodified recipe starts, then isolate the unsupported identified-item edge.
            string control = Start(s);
            Assert.IsTrue(Command(s, "CancelPaidCraft", control, "cancel-control").GetBool("ok"));
            Provision(s);
            const string form = "console_unit";
            Assert.IsNotEmpty(s.ComponentCatalog.ComponentIdForItemForm(form));
            GdDict recipe = s.CraftingState.GetRecipe(Recipe);
            if (side == "input")
            {
                recipe.GetDictOrEmpty("ingredients")[form] = 1L;
                Assert.AreEqual(1L, s.InventoryState.AddItem(form, 1));
            }
            else recipe.GetDictOrEmpty("produces")["item_id"] = form;
            GdDict before = Capture(s);
            GdDict rejected = s.BeginCraftFromPicker(Kind, Recipe);
            Assert.IsFalse(rejected.GetBool("ok"), "Anonymous equipment " + side + " cannot enter paid crafting.");
            Assert.IsNotEmpty(rejected.GetString("reason"));
            Equal(before, Capture(s), "Unavailable equipment recipe cannot debit/start/grant XP.");
            Assert.IsFalse(s.ComponentIntegrationEnabled);
        }

        [Test]
        public void Knowledge_IsExplicitStarterAuthority_HighSkillDoesNotUnlockBookRecipe()
        {
            RunSession s = Boot(); Provision(s);
            Start(s); // Actual starter positive before checking the locked authored recipe.
            GdDict known = Paid(Capture(s)).GetDictOrEmpty("knowledge").GetDictOrEmpty("known");
            Assert.IsTrue(known.GetBool(Recipe));
            Assert.IsFalse(known.GetBool("craft_thruster_nozzle"), "Skill and ingredients are not proof of reading a book.");
            Assert.IsTrue(Command(s, "CancelPaidCraft", Jobs(Capture(s)).Keys.Select(V.Str).Single(), "knowledge-control-cancel").GetBool("ok"));
            Provision(s, "craft_thruster_nozzle");
            s.CraftingState.GetOrCreateStation("fabricator").ApplyComponentTier(2);
            GdDict before = Capture(s);
            GdDict rejected = s.BeginCraftFromPicker("fabricator", "craft_thruster_nozzle");
            Assert.IsFalse(rejected.GetBool("ok"));
            Assert.AreEqual("unknown_recipe", rejected.GetString("reason"));
            Equal(before, Capture(s), "Unknown knowledge rejects before charging.");
        }

        [Test]
        public void PortableQuality_UsesActualSkillWithLevelZeroAndUnpoweredResolution()
        {
            RunSession s = Boot(); Provision(s, "field_bandage");
            GdDict inputs = s.CraftingState.GetRecipe("field_bandage").GetDictOrEmpty("ingredients");
            double quality = s.MaterialState.AverageIngredientQuality(inputs);
            GdDict expected = new QualityTierResolver().Resolve(quality, 4L, 0L, false);
            string id = Start(s, "field_bandage");
            Assert.AreEqual(expected.GetFloat("score"), Job(s, id).GetFloat("quality_score"));
            Assert.AreEqual(expected.GetString("tier"), Job(s, id).GetString("quality_tier"));
            Assert.AreEqual("player_local", Job(s, id).GetString("station_owner_id"));
        }

        [Test]
        public void InterveningInventoryAtPublication_RejectsStalePaymentAndRetainsExternalChange()
        {
            RunSession s = Boot(); Provision(s);
            string control = Start(s);
            Assert.IsTrue(Command(s, "CancelPaidCraft", control, "control-cancel").GetBool("ok"));
            Provision(s);
            GdDict before = Capture(s);
            bool reached = false;
            s.ComponentStageHook = stage => {
                if (stage == "publication" && !reached)
                {
                    reached = true;
                    Assert.AreEqual(1L, s.InventoryState.AddItem("scrap_metal", 1));
                }
            };
            GdDict result;
            try { result = Command(s, "RequestPaidCraft", Kind, Recipe, "stale-start"); }
            finally { s.ComponentStageHook = null; }
            Assert.IsTrue(reached);
            Assert.IsFalse(result.GetBool("committed"));
            Assert.AreEqual(3L, s.InventoryState.GetQuantity("scrap_metal"), "Stale publication cannot debit or erase intervening loot.");
            Equal(Jobs(before), Jobs(Capture(s)), "Stale start cannot publish another job.");
            Equal(before.Get("receipts"), Capture(s).Get("receipts"), "Stale start cannot publish a payment receipt.");
        }

        [TestCase("inventory")]
        [TestCase("job")]
        [TestCase("receipt")]
        [TestCase("publication")]
        public void StartPublicationFault_PreservesAllBeforeImages(string stage)
        {
            RunSession s = Boot(); Provision(s);
            string control = Start(s);
            Assert.IsTrue(Command(s, "CancelPaidCraft", control, "control-cancel").GetBool("ok"));
            Provision(s);
            GdDict before = Capture(s); Valid(before);
            bool reached = false;
            s.ComponentStageHook = current => { if (current == stage) { reached = true; throw new InvalidOperationException("paid-test-fault"); } };
            GdDict result;
            try { result = Command(s, "RequestPaidCraft", Kind, Recipe, "fault-start"); }
            finally { s.ComponentStageHook = null; }
            Assert.IsTrue(reached, "Positive start control must reach the existing coordinator stage " + stage);
            Assert.IsFalse(result.GetBool("committed"));
            Equal(before, Capture(s), "Prepublication failure rolls back inventory, crafting, knowledge, progression and receipts together.");
        }
    }
    /// <summary>Provisioned catalog fixtures: no earned-source or gameplay supply claim.</summary>
    public class FieldBandageSessionTests : PaidCraftFixture
    {
        SynapticSea.Core.Services.IEngineInfo _previousEngine;
        [SetUp] public void UseSnapshotEngine()
        {
            _previousEngine = SynapticSea.Core.Services.CoreServices.Engine;
            SynapticSea.Core.Services.CoreServices.Engine = new SynapticSea.Core.Services.FixedEngineInfo(SessionHarness.GodotVersion);
        }
        [TearDown] public void RestoreSnapshotEngine() => SynapticSea.Core.Services.CoreServices.Engine = _previousEngine;

        static string Wound(RunSession s) => s.WoundState.ApplyWound(new GdDict
            { { "kind", "laceration" }, { "body_part", "arm" }, { "severity", .6 } });
        static GdDict Parts(RunSession s) => new GdDict {
            { "inventory", s.InventoryState.GetSummary() }, { "wounds", s.WoundState.GetSummary() },
            { "progression", s.PlayerProgression.GetSummary() }, { "training", s.TrainingEventBus.ToDict() } };
        static string State(RunSession s) => PaidCraftingState.Hash(Parts(s));

        [TestCase(false)]
        [TestCase(true)]
        public void ExistingCraftedFieldBandagePaysOneAndPreservesExactEffectAcrossContinue(bool paid)
        {
            var s = Boot(paid: paid); s.PlayerProgression.Skills["fabrication"] = 0L;
            string wound = Wound(s); double raw = s.WoundState.GetWound(wound).GetFloat("bleed_rate");
            double severity = s.WoundState.GetWound(wound).GetFloat("severity"), health = s.VitalsState.Health;
            Provision(s, "field_bandage");
            Assert.AreEqual(2, s.InventoryState.GetQuantity("synth_fiber"));
            Assert.AreEqual(1, s.InventoryState.GetQuantity("medical_gauze"));
            var recipe = s.CraftingState.GetRecipe("field_bandage");
            Assert.AreEqual(8.0, recipe.GetFloat("craft_time_seconds"));
            Assert.AreEqual(0, recipe.GetInt("required_skill_level")); Assert.AreEqual(0, recipe.GetFloat("power_cost"));
            var started = s.BeginCraftFromPicker("field_crafting", "field_bandage");
            Assert.IsTrue(started.GetBool("ok"), GdJson.Stringify(started));
            Assert.AreEqual(0, s.InventoryState.GetQuantity("synth_fiber")); Assert.AreEqual(0, s.InventoryState.GetQuantity("medical_gauze"));
            s.AdvanceCrafting(7.9); Assert.AreEqual(0, s.InventoryState.GetQuantity("field_bandage"));
            s.AdvanceCrafting(.11); Assert.AreEqual(1, s.InventoryState.GetQuantity("field_bandage"));
            Assert.AreEqual("field_bandage", s.EvaluateWoundTreatment(RunSession.WOUND_ACTION_BANDAGE, wound).GetString("item_id"));
            var result = s.BandageWound(wound); Assert.IsTrue(result.GetBool("ok"), GdJson.Stringify(result));
            Assert.AreEqual("field_bandage", result.GetString("item_id")); Assert.AreEqual(0, s.InventoryState.GetQuantity("field_bandage"));
            Assert.AreEqual(raw * .4, s.WoundState.GetWound(wound).GetFloat("bleed_rate"), 1e-12);
            Assert.AreEqual(raw * .4 * .25, s.WoundState.TotalBleedRate(), 1e-12);
            Assert.AreEqual(severity, s.WoundState.GetWound(wound).GetFloat("severity")); Assert.AreEqual(health, s.VitalsState.Health);
            Assert.IsFalse(s.WoundState.GetWound(wound).GetBool("treated"));
            s.InventoryState.AddItem("field_bandage", 1);
            string before = State(s);
            Assert.AreEqual("already_bandaged", s.BandageWound(wound).GetString("reason"));
            Assert.AreEqual(before, State(s), "repeat refuses without payment, effect or XP");
            var partsBefore = Parts(s);
            Assert.IsTrue(s.RequestSave(), GdJson.Stringify(s.LastSaveResult)); Assert.IsTrue(s.RequestLoad());
            if (paid) Assert.AreEqual(before, State(s), "paid Continue preserves exact inventory/wound/progression/training bits");
            else
            {
                // Ordinary legacy JSON retains its existing decimal codec; only progression's fractional XP
                // differs by floating-point rounding. Preserve exact inventory, wound effect and training assertions.
                foreach (string key in new[] { "inventory", "wounds", "training" })
                    Assert.AreEqual(PaidCraftingState.Hash(partsBefore.Get(key)), PaidCraftingState.Hash(Parts(s).Get(key)), key);
                var expectedProgression = partsBefore.GetDictOrEmpty("progression").DeepCopy();
                var persistedProgression = (GdDict)GdJson.Parse(GdJson.Stringify(expectedProgression));
                expectedProgression.GetDictOrEmpty("skill_xp_fractional")["first_aid"] =
                    persistedProgression.GetDictOrEmpty("skill_xp_fractional").GetFloat("first_aid");
                Assert.AreEqual(PaidCraftingState.Hash(expectedProgression), PaidCraftingState.Hash(Parts(s).Get("progression")), "legacy persisted progression matches exact existing codec");
                TestContext.WriteLine("LEGACY_FRACTION_BITS before=" + BitConverter.DoubleToInt64Bits(partsBefore.GetDictOrEmpty("progression").GetDictOrEmpty("skill_xp_fractional").GetFloat("first_aid"))
                    + " after=" + BitConverter.DoubleToInt64Bits(s.PlayerProgression.GetSummary().GetDictOrEmpty("skill_xp_fractional").GetFloat("first_aid")));
            }
            Assert.AreEqual(raw * .4, s.WoundState.GetWound(wound).GetFloat("bleed_rate"), 1e-12);
            Assert.AreEqual(raw * .4 * .25, s.WoundState.TotalBleedRate(), 1e-12);
            Assert.IsTrue(s.WoundState.GetWound(wound).GetBool("bandaged")); Assert.IsFalse(s.WoundState.GetWound(wound).GetBool("treated"));
            string restored = State(s);
            Assert.AreEqual("already_bandaged", s.BandageWound(wound).GetString("reason"));
            Assert.AreEqual(restored, State(s));
        }

        [TestCase("bandage_kit")]
        [TestCase("bandage")]
        [TestCase("field_dressing")]
        public void LegacyBandagePriorityIsUnchanged(string legacy)
        {
            var s = Boot(); string wound = Wound(s);
            s.InventoryState.AddItem(legacy, 1); s.InventoryState.AddItem("field_bandage", 1);
            var result = s.BandageWound(wound); Assert.IsTrue(result.GetBool("ok"));
            Assert.AreEqual(legacy, result.GetString("item_id")); Assert.AreEqual(0, s.InventoryState.GetQuantity(legacy));
            Assert.AreEqual(1, s.InventoryState.GetQuantity("field_bandage"));
        }

        [TestCase("unknown_wound")]
        [TestCase("wound_healed")]
        [TestCase("already_bandaged")]
        public void FieldBandageRefusalsPreserveInventoryEffectsAndXp(string refusal)
        {
            var s = Boot(); string wound = Wound(s); s.InventoryState.AddItem("field_bandage", 2);
            if (refusal == "unknown_wound") wound = "absent";
            if (refusal == "wound_healed") ((GdDict)s.WoundState.Wounds[0])["severity"] = 0.0;
            if (refusal == "already_bandaged")
            { s.InventoryState.AddItem("medkit", 1); Assert.IsTrue(s.TreatWound(wound).GetBool("ok")); }
            string before = State(s);
            Assert.AreEqual(refusal, s.BandageWound(wound).GetString("reason"));
            Assert.AreEqual(before, State(s));
        }

        [Test]
        public void MedicalIngredientsAndFieldBandageDoNotBecomeTreatmentItems()
        {
            var s = Boot(); string wound = Wound(s);
            s.InventoryState.AddItem("medical_gauze", 1); string before = State(s);
            Assert.AreEqual("no_bandage_item", s.BandageWound(wound).GetString("reason")); Assert.AreEqual(before, State(s));
            s.InventoryState.AddItem("field_bandage", 1); before = State(s);
            Assert.AreEqual("no_treatment_item", s.TreatWound(wound).GetString("reason")); Assert.AreEqual(before, State(s));
        }
    }

}
