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
    // Review regressions: real API defects are separate from proposed compact-proof shape assertions.
    public class PaidCraftAdmissionTests : PaidCraftFixture
    {
        static GdDict Direct(RunSession s) => new GdDict {
            { "inventory", s.InventoryState.GetSummary() }, { "progression", s.PlayerProgression.GetSummary() },
            { "training", s.TrainingEventBus.ToDict() }, { "crafting", s.CraftingState.GetSummary() },
            { "field", s.FieldCraftingState.GetSummary() }, { "knowledge", s.RecipeKnowledge.GetSummary() },
            { "spoilage", s.SpoilageState.GetSummary() }
        };
        static GdDict Receipt(GdDict domain, string id) => domain.GetDictOrEmpty("receipts").GetDictOrEmpty(id);
        static GdDict Effect(GdDict domain, string id) => Receipt(domain, id).GetDictOrEmpty("result");
        static void Positive(RunSession s, GdDict domain)
        {
            Valid(domain);
            Assert.IsTrue(s.ValidatePaidCraftingRestore(domain, out string reason), reason);
        }
        static void RejectImported(RunSession s, GdDict bad)
        {
            GdDict owner = Capture(s), direct = Direct(s);
            bool schema = DomainBundle.TryCreate(bad, out _, out _);
            bool validated = s.ValidatePaidCraftingRestore(bad, out _);
            bool restored = s.RestorePaidCraftingDomain(bad);
            Assert.IsFalse(schema, "Malformed claimed-new proof must fail DomainBundle admission.");
            Assert.IsFalse(validated, "Session validation must reject independently.");
            Assert.IsFalse(restored, "Malformed restore must refuse before apply.");
            Equal(direct, Direct(s), "All direct participants retain exact before-images.");
            Equal(owner, Capture(s), "Rejected import preserves complete owner.");
        }
        static string Pending(RunSession s, string recipe = Recipe, string command = "pending-start")
        {
            Provision(s, recipe); string id = Start(s, recipe, command);
            string output = s.CraftingState.GetProduces(recipe).GetString("item_id");
            long maximum = s.InventoryState.GetDefinition(output).GetInt("max_stack", 99);
            Assert.AreEqual(maximum, s.InventoryState.AddItem(output, maximum)); s.AdvanceCrafting(100);
            Assert.AreEqual("completed_pending_delivery", Job(s, id).GetString("status"));
            Assert.AreEqual(maximum, s.InventoryState.RemoveItem(output, maximum));
            Positive(s, Capture(s)); return id;
        }
        static string Delivered(RunSession s, string command)
        {
            Provision(s); string id = Start(s, commandId: command); s.AdvanceCrafting(100);
            Assert.AreEqual("completed_delivered", Job(s, id).GetString("status")); return id;
        }

        [TestCase("payment_operation")]
        [TestCase("payment_target")]
        [TestCase("payment_command_action")]
        [TestCase("payment_command_recipe")]
        [TestCase("cross_payment")]
        [TestCase("cross_cancel")]
        [TestCase("cross_completion")]
        [TestCase("missing_completed_proof")]
        [TestCase("completion_event")]
        [TestCase("completion_multipliers")]
        [TestCase("completion_counter")]
        [TestCase("missing_accepted_log")]
        [TestCase("orphan_receipt")]
        [TestCase("start_known_string")]
        [TestCase("paid_schema_fraction")]
        [TestCase("receipt_schema_fraction")]
        [TestCase("completion_operation")]
        [TestCase("completion_target")]
        public void ImportedReceiptMutants_RejectAfterValidBaseWithoutDirectMutation(string mutation)
        {
            RunSession s = Boot(); string first, second = "";
            if (mutation == "cross_cancel")
            {
                Provision(s); first = Start(s); Assert.IsTrue(s.CancelPaidCraft(first, "cancel-first").GetBool("committed"));
                Provision(s); second = Start(s, commandId: "start-second"); Assert.IsTrue(s.CancelPaidCraft(second, "cancel-second").GetBool("committed"));
            }
            else if (mutation.StartsWith("completion", StringComparison.Ordinal) || mutation == "missing_completed_proof" || mutation == "missing_accepted_log" || mutation == "cross_completion")
            { first = Delivered(s, "first-delivery"); if (mutation == "cross_completion") second = Delivered(s, "second-delivery"); }
            else
            {
                Provision(s); first = Start(s);
                if (mutation == "cross_payment")
                { Assert.IsTrue(s.CancelPaidCraft(first, "cancel-first").GetBool("committed")); Provision(s); second = Start(s, commandId: "second-payment"); }
            }
            GdDict good = Capture(s); Positive(s, good); GdDict bad = good.DeepCopy(), job = Jobs(bad).GetDictOrEmpty(first);
            GdDict payment = Receipt(bad, job.GetString("payment_commit_id")), effect = payment.GetDictOrEmpty("result");
            GdDict completion = Effect(bad, job.GetString("completion_commit_id"));
            if (mutation == "payment_operation") effect["operation"] = "craft_resume"; // Command/hash unchanged: the concrete review trigger.
            else if (mutation == "payment_target") effect["job_id"] = "orphan-job";
            else if (mutation == "payment_command_action" || mutation == "payment_command_recipe")
            {
                GdDict command = payment.GetDictOrEmpty("command"); command[mutation == "payment_command_action" ? "action" : "recipe_id"] = mutation == "payment_command_action" ? "cancel" : "craft_sealant";
                payment["command_hash"] = PaidCraftingState.Hash(command); // Consistent hash still cannot authorize a contradictory operation/target.
            }
            else if (mutation == "cross_payment") job["payment_commit_id"] = Jobs(bad).GetDictOrEmpty(second).Get("payment_commit_id");
            else if (mutation == "cross_cancel") job["terminal_commit_id"] = Jobs(bad).GetDictOrEmpty(second).Get("terminal_commit_id");
            else if (mutation == "cross_completion") job["completion_commit_id"] = Jobs(bad).GetDictOrEmpty(second).Get("completion_commit_id");
            else if (mutation == "missing_completed_proof")
            { foreach (string key in new[] { "reward_proof", "progression_before", "progression_after", "training_before", "training_after" }) completion.Erase(key); }
            else if (mutation == "completion_event") completion["training_event"] = "cook_meal";
            else if (mutation == "completion_multipliers") completion.GetDictOrEmpty("xp_multipliers")["fabrication"] = 91.0;
            else if (mutation == "completion_counter")
            {
                GdDict counters = completion.Has("reward_proof") ? completion.GetDictOrEmpty("reward_proof").GetDictOrEmpty("training_after_ref") : completion.GetDictOrEmpty("training_after");
                counters["xp_total"] = counters.GetInt("xp_total") + 1;
            }
            else if (mutation == "missing_accepted_log")
            {
                GdDict training = bad.GetDictOrEmpty("participating_state").GetDictOrEmpty("training");
                Assert.Greater(training.GetArrayOrEmpty("log").Count, 0); training["log"] = new GdArray(); training["event_count"] = 0L;
            }
            else if (mutation == "orphan_receipt")
            {
                GdDict orphan = payment.DeepCopy(), command = orphan.GetDictOrEmpty("command");
                command["command_id"] = "orphan-resume"; command["action"] = "resume"; command["job_id"] = "missing-job";
                orphan["command_id"] = "orphan-resume"; orphan["transaction_id"] = "craft:orphan-resume"; orphan["commit_id"] = "craft:orphan-resume";
                orphan["command_hash"] = PaidCraftingState.Hash(command);
                orphan["result"] = new GdDict { { "operation", "craft_resume" }, { "job_id", "missing-job" }, { "reason", "resumed" } };
                bad.GetDictOrEmpty("receipts")["craft:orphan-resume"] = orphan;
            }
            else if (mutation == "start_known_string") { job["start_known"] = "false"; effect.GetDictOrEmpty("payment")["start_known"] = "false"; }
            else if (mutation == "paid_schema_fraction") Paid(bad)["schema_version"] = 1.5;
            else if (mutation == "receipt_schema_fraction") payment["schema_version"] = 1.5;
            else if (mutation == "completion_operation") completion["operation"] = "craft_resume";
            else if (mutation == "completion_target") completion["job_id"] = "another-job";
            RejectImported(s, bad);
        }

        [TestCase("queued_resume")]
        [TestCase("queued_retry")]
        [TestCase("consent_resume")]
        [TestCase("consent_retry")]
        [TestCase("pending_retry")]
        [TestCase("legacy_fresh")]
        public void AlternateCommandMappings_RemainValidAndReplayable(string mapping)
        {
            RunSession s = Boot(); GdDict result; string id;
            if (mapping.StartsWith("queued", StringComparison.Ordinal))
            { Provision(s); id = Enqueue(s, "enqueue-map"); result = mapping == "queued_resume" ? s.ResumePaidCraft(id, "mapped") : s.RetryPaidCraft(id, "mapped"); Assert.AreEqual("craft_start", result.GetDictOrEmpty("result").GetString("operation")); }
            else if (mapping.StartsWith("consent", StringComparison.Ordinal))
            { Provision(s); id = Start(s); Assert.IsTrue(s.RestorePaidCraftingDomain(Capture(s))); result = mapping == "consent_resume" ? s.ResumePaidCraft(id, "mapped") : s.RetryPaidCraft(id, "mapped"); Assert.AreEqual("craft_resume", result.GetDictOrEmpty("result").GetString("operation")); }
            else if (mapping == "pending_retry")
            { id = Pending(s); result = s.RetryPaidCraft(id, "mapped"); Assert.AreEqual("craft_complete", result.GetDictOrEmpty("result").GetString("operation")); }
            else
            {
                var source = new GdDict { { "active_craft", new GdDict { { "recipe_id", Recipe }, { "station_kind", Kind } } }, { "station_summaries", new GdDict() } };
                Assert.IsTrue(s.ImportLegacyCrafting(source, new GdDict(), "mapping-source", PaidCraftingState.Hash(source)).GetBool("committed"));
                Provision(s); id = V.Str(Paid(Capture(s)).GetDictOrEmpty("legacy").Keys.Single());
                result = s.ReconcileLegacyCraft(id, "start_fresh", "mapped"); Assert.AreEqual("craft_legacy_decision", result.GetDictOrEmpty("result").GetString("operation"));
            }
            Assert.IsTrue(result.GetBool("committed"), result.GetString("reason")); GdDict owner = Capture(s); Positive(s, owner);
            GdDict repeated = mapping == "legacy_fresh" ? s.ReconcileLegacyCraft(id, "start_fresh", "mapped") : mapping.EndsWith("resume", StringComparison.Ordinal) ? s.ResumePaidCraft(id, "mapped") : s.RetryPaidCraft(id, "mapped");
            Equal(result, repeated, "Alternate admitted mapping replays its exact public result."); Equal(owner, Capture(s), "Replay has no effects.");
        }

        static CraftingStation HomeStation(RunSession s)
        {
            CraftingStation station = s.CraftingStations.Single(st => st.IsValid && st.StationKind == Kind);
            Assert.AreSame(s.HomeShip.SceneRoot, station.Parent); return station;
        }
        static void RemoveEvidence(RunSession s, CraftingStation station, string change, RunSession foreign)
        {
            if (change == "remove") Assert.IsTrue(s.CraftingStations.Remove(station));
            else if (change == "invalidate") station.Free();
            else station.Parent = foreign.HomeShip.SceneRoot;
        }
        [TestCase("remove", "progress")]
        [TestCase("invalidate", "progress")]
        [TestCase("reparent", "progress")]
        [TestCase("remove", "delivery")]
        [TestCase("invalidate", "delivery")]
        [TestCase("reparent", "delivery")]
        [TestCase("remove", "restore")]
        [TestCase("invalidate", "restore")]
        [TestCase("reparent", "restore")]
        public void OriginalHomeStationEvidence_IsRequiredForEveryPhase(string change, string phase)
        {
            RunSession s = Boot(), foreign = Boot(); string id;
            if (phase == "delivery") id = Pending(s);
            else { Provision(s); id = Start(s); s.AdvanceCrafting(0.25); Assert.Greater(Job(s, id).GetFloat("progress_seconds"), 0); }
            GdDict saved = Capture(s); Positive(s, saved); CraftingStation station = HomeStation(s);
            GdDict direct = Direct(s); double progress = Job(s, id).GetFloat("progress_seconds");
            RemoveEvidence(s, station, change, foreign);
            if (phase == "restore")
            {
                Assert.IsFalse(s.ValidatePaidCraftingRestore(saved, out _), "Kind whitelist cannot replace vanished original-home registration.");
                Assert.IsFalse(s.RestorePaidCraftingDomain(saved)); Equal(direct, Direct(s), "Restore refusal precedes effects.");
            }
            else if (phase == "delivery")
            { Assert.IsFalse(s.RetryPaidCraft(id, "missing-service").GetBool("committed")); Equal(direct, Direct(s), "Pending refusal preserves output, XP, food and projections."); }
            else
            { s.AdvanceCrafting(1); Assert.AreEqual(progress, Job(s, id).GetFloat("progress_seconds"), "Recognized kind without original registered service cannot advance."); Equal(direct.Get("inventory"), s.InventoryState.GetSummary(), "No output or debit."); Equal(direct.Get("training"), s.TrainingEventBus.ToDict(), "No reward."); }
        }
        [TestCase("remove", false)]
        [TestCase("invalidate", false)]
        [TestCase("reparent", false)]
        [TestCase("remove", true)]
        [TestCase("invalidate", true)]
        [TestCase("reparent", true)]
        public void FinalHookStationEvidenceChange_RefusesStagedPaymentOrDelivery(string change, bool delivery)
        {
            RunSession s = Boot(), foreign = Boot(); string id = "";
            if (delivery) id = Pending(s); else Provision(s);
            GdDict owner = Capture(s); Positive(s, owner); GdDict direct = Direct(s); CraftingStation station = HomeStation(s); bool reached = false;
            s.ComponentStageHook = stage => { if (stage == "live_placement") { reached = true; RemoveEvidence(s, station, change, foreign); } };
            GdDict result;
            try { result = delivery ? s.RetryPaidCraft(id, "station-race") : s.RequestPaidCraft(Kind, Recipe, "station-race"); }
            finally { s.ComponentStageHook = null; }
            Assert.IsTrue(reached); Assert.IsFalse(result.GetBool("committed"), "Actual original service must still exist at final publication.");
            Equal(direct, Direct(s), "Refusal preserves all direct participants."); Equal(owner, Capture(s), "No receipt/payment/output publication.");
            if (change == "remove") Assert.IsFalse(s.CraftingStations.Contains(station));
            else if (change == "invalidate") Assert.IsFalse(station.IsValid);
            else Assert.AreSame(foreign.HomeShip.SceneRoot, station.Parent);
        }
        [Test]
        public void ForeignCurrentShip_DoesNotSubstituteForValidOriginalHomeOrPortableOwner()
        {
            RunSession s = Boot(), foreign = Boot(); Provision(s); string stationId = Start(s); Provision(s, "field_bandage"); string fieldId = Start(s, "field_bandage", "portable");
            string home = s.HomeShip.ShipId; HomeStation(s); foreign.HomeShip.ShipId = "foreign-home-admission";
            Assert.AreNotEqual(home, foreign.HomeShip.ShipId); s.CurrentShip = foreign.HomeShip;
            s.AdvanceCrafting(100); Assert.AreEqual("completed_delivered", Job(s, stationId).GetString("status")); Assert.AreEqual("completed_delivered", Job(s, fieldId).GetString("status"));
            Assert.AreEqual(home, Job(s, stationId).GetString("station_owner_id")); Assert.AreEqual("player_local", Job(s, fieldId).GetString("station_owner_id"));
            Assert.AreEqual("portable:" + s.RunId + ":player_local", Job(s, fieldId).GetString("station_id"));
        }
        [Test]
        public void PortableWork_RemainsQualifiedWhenHomeStationRegistrationDisappears()
        {
            RunSession s = Boot(); Provision(s, "field_bandage"); string id = Start(s, "field_bandage");
            foreach (CraftingStation station in s.CraftingStations) station.Free();
            Positive(s, Capture(s)); s.AdvanceCrafting(100); Assert.AreEqual("completed_delivered", Job(s, id).GetString("status"));
            Assert.AreEqual(s.RunId, Job(s, id).GetString("run_id")); Assert.AreEqual("player_local", Job(s, id).GetString("actor_id"));
        }

        [TestCase(Recipe, "filter", false)]
        [TestCase(Recipe, "filter", true)]
        [TestCase(Recipe, "gate", false)]
        [TestCase(Recipe, "gate", true)]
        [TestCase("field_bandage", "filter", false)]
        [TestCase("field_bandage", "filter", true)]
        [TestCase("field_bandage", "gate", false)]
        [TestCase("field_bandage", "gate", true)]
        public void FinalHookTrainingPolicyReplacement_RefusesStaleRewardAndRetryUsesNewPolicy(string recipe, string policy, bool initiallyBlocked)
        {
            RunSession s = Boot(); s.TrainingEventBus.EventFilter = (evt, target) => policy == "filter" && initiallyBlocked;
            s.TrainingEventBus.SkillGate = skill => policy != "gate" || !initiallyBlocked;
            string id = Pending(s, recipe); GdDict owner = Capture(s), direct = Direct(s); bool reached = false;
            Func<string, string, bool> nextFilter = (evt, target) => !initiallyBlocked;
            Func<string, bool> nextGate = skill => initiallyBlocked;
            s.ComponentStageHook = stage => { if (stage == "live_placement") { reached = true; if (policy == "filter") s.TrainingEventBus.EventFilter = nextFilter; else s.TrainingEventBus.SkillGate = nextGate; } };
            GdDict failed;
            try { failed = s.RetryPaidCraft(id, "policy-race"); } finally { s.ComponentStageHook = null; }
            Assert.IsTrue(reached); Assert.IsFalse(failed.GetBool("committed"), "Do not publish a reward staged under replaced live configuration.");
            Equal(direct, Direct(s), "Refusal preserves exact direct participants and pending job."); Equal(owner, Capture(s), "No stale completion receipt.");
            if (policy == "filter") Assert.AreSame(nextFilter, s.TrainingEventBus.EventFilter); else Assert.AreSame(nextGate, s.TrainingEventBus.SkillGate);
            var expected = new PlayerProgressionState(); var classes = ClassDefinition.LoadAll();
            expected.Configure(classes[s.PlayerProgression.ClassId], PlayerProgressionState.LoadSkillsCatalog(), s.PlayerProgression.GetBooksCatalog()); expected.ApplySummary(s.PlayerProgression.GetSummary());
            expected.XpMultipliers.Clear(); foreach (var pair in s.PlayerProgression.XpMultipliers) expected.XpMultipliers[pair.Key] = pair.Value;
            var oracle = new TrainingEventBus(); oracle.Configure(); oracle.ApplySummary(s.TrainingEventBus.ToDict()); oracle.EventFilter = s.TrainingEventBus.EventFilter; oracle.SkillGate = s.TrainingEventBus.SkillGate;
            string output = s.CraftingState.GetProduces(recipe).GetString("item_id"); GdDict record = oracle.Emit("fabricate_part", output, expected);
            Assert.IsTrue(s.RetryPaidCraft(id, "policy-race").GetBool("committed")); Assert.AreEqual(1L, s.InventoryState.GetQuantity(output));
            Equal(expected.GetSummary(), s.PlayerProgression.GetSummary(), "Exact new policy XP/fractions."); Assert.AreEqual(oracle.GetDroppedCount(), s.TrainingEventBus.GetDroppedCount()); Assert.AreEqual(oracle.GetEventCount(), s.TrainingEventBus.GetEventCount()); Assert.AreEqual(oracle.GetTotalXpDelivered(), s.TrainingEventBus.GetTotalXpDelivered());
            if (record != null) foreach (var pair in record) Equal(pair.Value, s.TrainingEventBus.GetLog().OfType<GdDict>().Last().Get(pair.Key), "New gated/accepted record.");
            GdDict once = Capture(s); s.RetryPaidCraft(id, "policy-race"); Equal(once, Capture(s), "New policy commits once.");
        }

        static void ComponentTrainingPrefix(RunSession s)
        {
            GdDict domain = Capture(s);
            GdDict mounted = domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values.OfType<GdDict>()
                .First(row => domain.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder")).GetString("kind") == "slot");
            string id = mounted.GetString("instance_id");
            GdDict target = s.ListInstallTargets(id).OfType<GdDict>().Single(row => row.GetString("holder_id") == mounted.GetString("holder"));
            s.Scene.PlayerPosition = target.Get("world_position") is Vec3 position ? position : Vec3.FromArray((GdArray)target.Get("world_position"));
            Assert.AreEqual(1L, s.InventoryState.AddItem("wrench", 1)); s.BeginWorkHold();
            GdDict request = s.RequestComponentRemoval(id); Assert.IsTrue(request.GetBool("ok"), request.GetString("reason"));
            for (int tick = 0; tick < 140 && s.GetComponentWorkState().GetString("status") != "committed"; tick++) s.StageWorkAction(0.1);
            Assert.AreEqual("committed", s.GetComponentWorkState().GetString("status"), "Actual timed component operation provides the prefix.");
            Assert.IsTrue(s.TrainingEventBus.GetLog().OfType<GdDict>().Any(row => row.GetString("commit_id").StartsWith("component_transfer:", StringComparison.Ordinal)));
            s.EndWorkHold();
        }
        static GdDict History(GdDict domain) => Paid(domain).GetDictOrEmpty("reward_history");
        static GdDict RequireHistory(GdDict domain)
        {
            GdDict history = History(domain);
            Assert.IsFalse(history.IsEmpty, "Compact reward proof v1 must intern exact training prefixes; missing representation is an explicit scaffold RED.");
            Assert.AreEqual(1L, history.Get("schema_version"));
            Assert.IsInstanceOf<GdDict>(history.Get("training_nodes")); Assert.IsInstanceOf<GdDict>(history.Get("progression_nodes"));
            Assert.IsInstanceOf<GdDict>(history.Get("current_training_ref")); return history;
        }
        [TestCase(false)]
        [TestCase(true)]
        public void EightyActualCompletions_RoundTripAndReplayWithoutQuadraticHistory(bool components)
        {
            RunSession s = Boot(components: components);
            foreach (object skill in s.PlayerProgression.Skills.Keys.ToArray()) s.PlayerProgression.Skills[skill] = 4L;
            foreach (object skill in s.PlayerProgression.XpMultipliers.Keys.ToArray()) s.PlayerProgression.XpMultipliers[skill] = 1.375;
            Assert.IsNotNull(s.TrainingEventBus.Emit("fabricate_part", "ordinary-prefix", s.PlayerProgression));
            if (components) ComponentTrainingPrefix(s);
            GdArray prefix = s.TrainingEventBus.GetLog(); int prefixCount = prefix.Count;
            var replay = new Dictionary<string, GdDict>(); var nodeCounts = new List<int>();
            long rewarded = 0; GdDict foodBefore;
            for (int i = 0; i < 80; i++)
            {
                string recipe = i % 4 == 0 ? "cook_basic_meal" : i % 4 == 1 ? "field_bandage" : Recipe;
                s.TrainingEventBus.EventFilter = (evt, targetId) => i % 16 == 15;
                s.TrainingEventBus.SkillGate = skill => i % 16 != 14;
                string output = s.CraftingState.GetProduces(recipe).GetString("item_id");
                foodBefore = s.SpoilageState.GetSummary(); Provision(s, recipe); string id = Start(s, recipe, "workload-start-" + i);
                s.AdvanceCrafting(100); Assert.AreEqual("completed_delivered", Job(s, id).GetString("status"), "Actual accepted completion " + i);
                Assert.AreEqual(1L, s.InventoryState.RemoveItem(output, 1), "Free actual stack capacity through existing inventory behavior.");
                if (i % 16 != 15) rewarded++;
                if (foodBefore.GetDictOrEmpty("foods").Has("cooked_meal"))
                    Equal(foodBefore, s.SpoilageState.GetSummary(), "Later accepted cooked output preserves existing food age/config.");
                s.SpoilageState.Tick(0.125);
                if (i == 0 || i == 39 || i == 79) replay[id] = s.RetryPaidCraft(id, "workload-read-" + i).DeepCopy();
                if ((i + 1) % 20 == 0) nodeCounts.Add(History(Capture(s)).GetDictOrEmpty("training_nodes").Count);
            }
            Assert.GreaterOrEqual(rewarded, 68, "Reach beyond the old source-derived bound with actual recorded rewards.");
            Assert.AreEqual(prefixCount + rewarded, s.TrainingEventBus.GetEventCount());
            GdDict saved = Capture(s); Positive(s, saved); GdDict direct = Direct(s);
            // Keep codec before representation assertions: v1 must reproduce the actual bound failure, not merely missing keys.
            GdDict encoded = null; Assert.DoesNotThrow(() => encoded = ComponentDomainCodec.Encode(saved), "80 real paid completions must remain encodable within the unchanged codec bound.");
            Assert.IsTrue(ComponentDomainCodec.TryDecode(encoded, out GdDict decoded, out string reason), reason); Equal(saved, decoded, "Exact typed owner roundtrip.");
            GdDict history = RequireHistory(saved);
            Assert.AreEqual(s.TrainingEventBus.GetEventCount(), history.GetDictOrEmpty("training_nodes").Count, "One shared prefix node per distinct row for this append-only history.");
            Assert.LessOrEqual(nodeCounts[3] - nodeCounts[2], 20); Assert.LessOrEqual(nodeCounts[2] - nodeCounts[1], 20); Assert.Greater(nodeCounts[0], prefixCount);
            GdArray log = s.TrainingEventBus.GetLog(); for (int i = 0; i < prefixCount; i++) Equal(prefix[i], log[i], "Ordinary/component prefix remains exact.");
            Assert.IsTrue(s.RestorePaidCraftingDomain(decoded)); Equal(direct, Direct(s), "All terminal participants including raw food time roundtrip.");
            foreach (var pair in replay) Equal(pair.Value, s.RetryPaidCraft(pair.Key, "after-roundtrip"), "Early/middle/late public historical result expands exactly.");
            Equal(direct, Direct(s), "Replay cannot output, train or freshen again."); Equal(saved, Capture(s), "Terminal replay has no owner mutation.");
        }

        [TestCase("missing_tip")]
        [TestCase("node_hash")]
        [TestCase("parent")]
        [TestCase("count")]
        [TestCase("row")]
        [TestCase("counter")]
        [TestCase("schema_type")]
        [TestCase("progression_ref")]
        [TestCase("missing_history")]
        public void CompactProofCorruption_RejectsAfterValidRepresentationPositive(string mutation)
        {
            RunSession s = Boot(); string id = Delivered(s, "proof-completion"); GdDict good = Capture(s); Positive(s, good);
            GdDict history = RequireHistory(good); Assert.AreEqual(1, history.GetDictOrEmpty("training_nodes").Count);
            GdDict effect = Effect(good, Job(s, id).GetString("completion_commit_id"));
            Assert.IsInstanceOf<GdDict>(effect.Get("reward_proof"), "Versioned independent reward proof is required.");
            GdDict bad = good.DeepCopy(), pool = History(bad), proof = Effect(bad, Job(s, id).GetString("completion_commit_id")).GetDictOrEmpty("reward_proof");
            string nodeId = V.Str(pool.GetDictOrEmpty("training_nodes").Keys.Single()); GdDict node = pool.GetDictOrEmpty("training_nodes").GetDictOrEmpty(nodeId);
            if (mutation == "missing_tip") proof.GetDictOrEmpty("training_after_ref")["tip_hash"] = new string('0', 64);
            else if (mutation == "node_hash") { pool.GetDictOrEmpty("training_nodes").Erase(nodeId); pool.GetDictOrEmpty("training_nodes")[new string('0', 64)] = node; }
            else if (mutation == "parent") node["parent_hash"] = nodeId;
            else if (mutation == "count") node["count"] = 17L;
            else if (mutation == "row") node.GetDictOrEmpty("row")["base_xp"] = 999L;
            else if (mutation == "counter") proof.GetDictOrEmpty("training_after_ref")["dropped"] = 7L;
            else if (mutation == "schema_type") pool["schema_version"] = 1.0;
            else if (mutation == "progression_ref") proof["progression_after_hash"] = new string('0', 64);
            else Paid(bad).Erase("reward_history");
            RejectImported(s, bad);
        }
        [Test]
        public void ResetLiveTraining_RetainsReferencedHistoricalBranchAndPublicReplay()
        {
            RunSession s = Boot(); string first = Delivered(s, "branch-one"); GdDict firstResult = s.RetryPaidCraft(first, "first-read");
            s.TrainingEventBus.Reset(); Capture(s); string second = Delivered(s, "branch-two"); GdDict saved = Capture(s); Positive(s, saved);
            GdDict history = RequireHistory(saved); Assert.AreEqual(2, history.GetDictOrEmpty("training_nodes").Count, "Retain both referenced branches; reset is not identity expiry.");
            Assert.AreEqual(1L, history.GetDictOrEmpty("current_training_ref").GetInt("count"));
            Assert.IsTrue(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(saved), out GdDict decoded, out string reason), reason);
            Assert.IsTrue(s.RestorePaidCraftingDomain(decoded)); Equal(firstResult, s.RetryPaidCraft(first, "old-branch-read"), "Historical branch remains fully materializable.");
            Assert.AreEqual(second, s.TrainingEventBus.GetLog().OfType<GdDict>().Single().GetString("commit_id").Substring("craft:complete:".Length));
        }
        [Test]
        public void PaidHistory_ComponentReward_LaterPaidReward_UsesOneLosslessCurrentBranch()
        {
            RunSession s = Boot(components: true); s.PlayerProgression.Skills["salvage"] = 4L;
            string first = Delivered(s, "before-component"); GdDict firstResult = s.RetryPaidCraft(first, "first-read"), before = Capture(s);
            ComponentTrainingPrefix(s); GdDict middle = Capture(s); Positive(s, middle);
            foreach (var pair in Jobs(before)) Equal(pair.Value, Jobs(middle).Get(pair.Key), "Component event preserves existing craft identity/resolution.");
            foreach (var pair in before.GetDictOrEmpty("receipts")) Equal(pair.Value, middle.GetDictOrEmpty("receipts").Get(pair.Key), "Component event preserves historical craft receipts/proofs.");
            string second = Delivered(s, "after-component"); GdDict saved = Capture(s); Positive(s, saved);
            GdDict history = RequireHistory(saved); Assert.AreEqual(3, history.GetDictOrEmpty("training_nodes").Count, "Paid, actual component, paid form one interned prefix.");
            Assert.AreEqual(3L, history.GetDictOrEmpty("current_training_ref").GetInt("count"));
            Equal(firstResult, s.RetryPaidCraft(first, "second-read"), "Later component event cannot rewrite historical public result.");
            Assert.IsTrue(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(saved), out GdDict decoded, out string reason), reason);
            Assert.IsTrue(s.RestorePaidCraftingDomain(decoded)); Equal(saved, Capture(s), "Combined current branch and all proof identities roundtrip exactly.");
            Assert.AreEqual("completed_delivered", Job(s, second).GetString("status"));
        }
        [TestCase("paid_identity")]
        [TestCase("historical_proof")]
        public void ComponentPreparation_CannotChangeForeignPaidIdentityOrInternedProof(string mutation)
        {
            RunSession s = Boot(components: true); string paidId = Delivered(s, "protected-paid"); GdDict domain = Capture(s); Positive(s, domain);
            if (mutation == "historical_proof") RequireHistory(domain);
            GdDict holders = domain.GetDictOrEmpty("holders");
            GdDict instance = domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values.OfType<GdDict>().First(row => holders.GetDictOrEmpty(row.GetString("holder")).GetString("kind") == "slot");
            string source = instance.GetString("holder"), destination = "player:player_local";
            var command = new GdDict { { "schema_version", 1L }, { "command_id", "foreign-conservation" }, { "operation", "transfer" }, { "instance_id", instance.GetString("instance_id") },
                { "source_holder_id", source }, { "destination_holder_id", destination }, { "expected_domain_revision", domain.Get("revision") }, { "expected_instance_revision", instance.Get("revision") },
                { "expected_source_revision", holders.GetDictOrEmpty(source).Get("revision") }, { "expected_destination_revision", holders.GetDictOrEmpty(destination).Get("revision") } };
            MethodInfo prepare = typeof(DomainTransactionCoordinator).GetMethod("PrepareLive", BindingFlags.Instance | BindingFlags.NonPublic); Assert.IsNotNull(prepare);
            var control = new DomainTransactionCoordinator(domain);
            Assert.IsTrue(((GdDict)prepare.Invoke(control, new object[] { command, new Func<GdDict, GdDict>(candidate => candidate) })).GetBool("ok"), "Actual transfer positive precedes foreign mutation.");
            var attacked = new DomainTransactionCoordinator(domain);
            Func<GdDict, GdDict> mutate = candidate => {
                if (mutation == "paid_identity") Jobs(candidate).GetDictOrEmpty(paidId)["actor_id"] = "foreign-actor";
                else History(candidate).GetDictOrEmpty("training_nodes").Values.OfType<GdDict>().First().GetDictOrEmpty("row")["base_xp"] = 999L;
                return candidate;
            };
            bool accepted;
            try { accepted = ((GdDict)prepare.Invoke(attacked, new object[] { command, mutate })).GetBool("ok"); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { accepted = false; }
            Assert.IsFalse(accepted); Equal(domain, attacked.GetSummary(), "Only exact component training may intern deterministic history; foreign proof edits refuse.");
        }

        [TestCase("skill")]
        [TestCase("tier")]
        public void FundedFifoHead_CurrentGateBlocksEligibleTailWithoutPayment(string gate)
        {
            RunSession s = Boot(); Provision(s); string active = Start(s);
            const string headRecipe = "craft_sealant"; GdDict head = s.CraftingState.GetRecipe(headRecipe);
            // Test-only admission thresholds; catalog files and paid active definition remain untouched.
            head[gate == "skill" ? "required_skill_level" : "station_tier_min"] = 5L;
            Provision(s, headRecipe); Provision(s, Recipe);
            string headId = s.EnqueuePaidCraft(Kind, headRecipe, "blocked-head").GetString("job_id"), tailId = Enqueue(s, "eligible-tail");
            Assert.IsNotEmpty(headId); GdDict funded = s.InventoryState.GetSummary(); s.AdvanceCrafting(100);
            Assert.AreEqual("completed_delivered", Job(s, active).GetString("status")); Assert.AreEqual("unpaid", Job(s, headId).GetString("input_state")); Assert.AreEqual("unpaid", Job(s, tailId).GetString("input_state"));
            Assert.AreEqual(gate == "skill" ? "insufficient_skill" : "insufficient_tier", Job(s, headId).GetString("blocked_reason"));
            Equal(GdArray.Of(headId, tailId), Paid(Capture(s)).GetDictOrEmpty("queues").Get("station"), "FIFO identities remain in order.");
            foreach (var input in head.GetDictOrEmpty("ingredients")) Assert.AreEqual(funded.GetDictOrEmpty("items").GetInt(input.Key), s.InventoryState.GetQuantity(V.Str(input.Key)), "Funded blocked head consumes nothing.");
            Assert.IsFalse(s.RetryPaidCraft(tailId, "tail-bypass").GetBool("ok"));
            if (gate == "skill") s.PlayerProgression.Skills["fabrication"] = 5L; else s.CraftingState.GetOrCreateStation(Kind).Tier = 5;
            Assert.IsTrue(s.RetryPaidCraft(headId, "head-retry").GetBool("committed")); Assert.AreEqual("paid", Job(s, headId).GetString("input_state")); Assert.AreEqual("unpaid", Job(s, tailId).GetString("input_state"));
        }
        [Test]
        public void LiveFifoAutoStart_UsesCurrentQualityAndExactlyOneNewInputSet()
        {
            RunSession s = Boot(); Provision(s, batches: 2); string first = Start(s), next = Enqueue(s, "quality-queue");
            double firstQuality = Job(s, first).GetFloat("quality_score"); s.PlayerProgression.Skills["fabrication"] = 0L; s.CraftingState.GetOrCreateStation(Kind).Level = 3;
            GdDict expected = new QualityTierResolver().Resolve(s.MaterialState.AverageIngredientQuality(s.CraftingState.GetRecipe(Recipe).GetDictOrEmpty("ingredients")), 0L, 3L, true);
            Assert.AreNotEqual(firstQuality, expected.GetFloat("score"), "Current quality fixture must distinguish queued resolution.");
            s.AdvanceCrafting(100); GdDict job = Job(s, next); Assert.AreEqual("paid", job.GetString("input_state")); Assert.AreEqual(0.0, job.GetFloat("progress_seconds"));
            Assert.AreEqual(expected.GetFloat("score"), job.GetFloat("quality_score")); Assert.AreEqual(firstQuality, Job(s, first).GetFloat("quality_score"));
            foreach (var ingredient in s.CraftingState.GetRecipe(Recipe).GetDictOrEmpty("ingredients")) Assert.AreEqual(0L, s.InventoryState.GetQuantity(V.Str(ingredient.Key)));
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating"));
        }
        [TestCase(Recipe)]
        [TestCase("field_bandage")]
        public void SecondFundedContender_IsBusyWhileIndependentChannelMayStart(string recipe)
        {
            RunSession s = Boot(); Provision(s, recipe, 2); string first = Start(s, recipe); GdDict owner = Capture(s), direct = Direct(s);
            GdDict refused = s.RequestPaidCraft(s.CraftingState.GetStationKind(recipe), recipe, "second-contender"); Assert.IsFalse(refused.GetBool("committed")); Assert.AreEqual("busy", refused.GetString("reason"));
            Equal(owner, Capture(s), "Busy cannot debit or issue a receipt."); Equal(direct, Direct(s), "Busy preserves all views.");
            string other = recipe == Recipe ? "field_bandage" : Recipe; Provision(s, other); string independent = Start(s, other, "other-channel");
            Assert.AreNotEqual(Job(s, first).GetString("channel"), Job(s, independent).GetString("channel"));
        }
        [TestCase("progress")]
        [TestCase("delivery")]
        public void DefinitionChangedAfterPayment_CannotAdvanceOrDeliverOldResolution(string phase)
        {
            RunSession s = Boot(); string id;
            if (phase == "delivery") id = Pending(s); else { Provision(s); id = Start(s); }
            GdDict before = Job(s, id), direct = Direct(s); s.CraftingState.GetRecipe(Recipe)["craft_time_seconds"] = s.CraftingState.GetRecipe(Recipe).GetFloat("craft_time_seconds") + 1.0;
            if (phase == "delivery") Assert.IsFalse(s.RetryPaidCraft(id, "changed-definition").GetBool("committed")); else s.AdvanceCrafting(100);
            GdDict after = Job(s, id); Equal(PaidCraftingState.Payment(before), PaidCraftingState.Payment(after), "Changing live catalog does not rewrite paid immutable resolution.");
            Assert.AreEqual(before.GetFloat("progress_seconds"), after.GetFloat("progress_seconds")); Equal(direct.Get("inventory"), s.InventoryState.GetSummary(), "No output or input change."); Equal(direct.Get("training"), s.TrainingEventBus.ToDict(), "No reward.");
        }
        static GdDict ExpectedStartQuality(RunSession s, string recipe)
        {
            string kind = s.CraftingState.GetStationKind(recipe); StationState station = kind == "field_crafting" ? null : s.CraftingState.GetOrCreateStation(kind);
            return new QualityTierResolver().Resolve(s.MaterialState.AverageIngredientQuality(s.CraftingState.GetRecipe(recipe).GetDictOrEmpty("ingredients")),
                s.PlayerProgression.GetSkillLevel("fabrication"), station?.Level ?? 0L, station?.Powered ?? false);
        }
        [TestCase(Recipe)]
        [TestCase("field_bandage")]
        public void UnchangedEffectiveMaterials_StartWithExactIndependentQuality(string recipe)
        {
            RunSession s = Boot(); Provision(s, recipe); GdDict expected = ExpectedStartQuality(s, recipe); string id = Start(s, recipe);
            Assert.AreEqual(expected.GetFloat("score"), Job(s, id).GetFloat("quality_score")); Assert.AreEqual(expected.GetString("tier"), Job(s, id).GetString("quality_tier"));
            Assert.AreEqual(expected.GetFloat("multiplier"), Job(s, id).GetFloat("quality_multiplier")); Positive(s, Capture(s));
        }
        [TestCase("station")]
        [TestCase("portable")]
        [TestCase("queued")]
        [TestCase("legacy_fresh")]
        [TestCase("base_definition")]
        public void FinalHookEffectiveMaterialChange_RefusesNewPaymentAndRetryResolvesCurrentQuality(string path)
        {
            RunSession s = Boot(); string recipe = path == "portable" ? "field_bandage" : Recipe; Provision(s, recipe);
            GdDict ingredients = s.CraftingState.GetRecipe(recipe).GetDictOrEmpty("ingredients"); string material = V.Str(ingredients.Keys.First());
            Assert.IsTrue(s.MaterialState.HasDefinition(material));
            if (path != "base_definition") s.MaterialState.SetQuality(material, 0.95);
            else { s.MaterialState.RemoveQuality(material); s.MaterialState.GetDefinition(material)["base_quality"] = 0.95; } // Test-only mutable definition, no authored asset edit.
            double oldMaterial = s.MaterialState.AverageIngredientQuality(ingredients); GdDict oldQuality = ExpectedStartQuality(s, recipe);
            string existing = ""; Func<GdDict> request;
            if (path == "queued")
            { existing = s.EnqueuePaidCraft(Kind, recipe, "material-queue").GetString("job_id"); Assert.IsNotEmpty(existing); request = () => s.RetryPaidCraft(existing, "material-race"); }
            else if (path == "legacy_fresh")
            {
                var source = new GdDict { { "active_craft", new GdDict { { "recipe_id", recipe }, { "station_kind", Kind } } }, { "station_summaries", new GdDict() } };
                Assert.IsTrue(s.ImportLegacyCrafting(source, new GdDict(), "material-source", PaidCraftingState.Hash(source)).GetBool("committed"));
                existing = V.Str(Paid(Capture(s)).GetDictOrEmpty("legacy").Keys.Single()); request = () => s.ReconcileLegacyCraft(existing, "start_fresh", "material-race");
            }
            else request = () => s.RequestPaidCraft(s.CraftingState.GetStationKind(recipe), recipe, "material-race");
            GdDict owner = Capture(s); Positive(s, owner); GdDict direct = Direct(s); bool reached = false;
            s.ComponentStageHook = stage => {
                if (stage != "live_placement") return; reached = true;
                if (path == "base_definition") s.MaterialState.GetDefinition(material)["base_quality"] = 0.05; else s.MaterialState.SetQuality(material, 0.05);
            };
            GdDict refused; try { refused = request(); } finally { s.ComponentStageHook = null; }
            Assert.IsTrue(reached); Assert.AreEqual(0.05, s.MaterialState.GetQuality(material));
            Assert.AreNotEqual(oldMaterial, s.MaterialState.AverageIngredientQuality(ingredients)); Assert.AreNotEqual(oldQuality.GetFloat("score"), ExpectedStartQuality(s, recipe).GetFloat("score"));
            Assert.IsFalse(refused.GetBool("committed"), "Effective quality changed after staged payment resolution.");
            Equal(direct, Direct(s), "Refusal preserves inventory/progression/food/crafting/knowledge."); Equal(owner, Capture(s), "No payment, fresh archive or queue transition under stale material input.");
            GdDict expected = ExpectedStartQuality(s, recipe), accepted = request(); Assert.IsTrue(accepted.GetBool("committed"), accepted.GetString("reason"));
            GdDict job = Job(s, accepted.GetString("job_id")); Assert.AreEqual(expected.GetFloat("score"), job.GetFloat("quality_score")); Assert.AreEqual(0.0, job.GetFloat("progress_seconds"));
            Assert.AreEqual(s.CraftingState.GetCraftTime(recipe), job.GetFloat("required_seconds"));
            foreach (var input in ingredients) Assert.AreEqual(0L, s.InventoryState.GetQuantity(V.Str(input.Key)), "Exactly one paid input set after retry.");
            GdDict once = Capture(s); Equal(accepted, request(), "Same retry identity replays exact accepted result."); Equal(once, Capture(s), "No second payment.");
        }
        [TestCase(Recipe)]
        [TestCase("field_bandage")]
        public void AlreadyPaidMaterialChange_DoesNotRerollOrBlockResumeAndDelivery(string recipe)
        {
            RunSession s = Boot(); Provision(s, recipe); string id = Start(s, recipe); GdDict immutable = PaidCraftingState.Payment(Job(s, id));
            Assert.IsTrue(s.RestorePaidCraftingDomain(Capture(s)));
            foreach (var input in s.CraftingState.GetRecipe(recipe).GetDictOrEmpty("ingredients")) s.MaterialState.SetQuality(V.Str(input.Key), 0.0);
            Assert.IsTrue(s.ResumePaidCraft(id, "material-after-payment").GetBool("committed")); s.AdvanceCrafting(100);
            Assert.AreEqual("completed_delivered", Job(s, id).GetString("status")); Equal(immutable, PaidCraftingState.Payment(Job(s, id)), "Only new payment resolves current materials.");
            Assert.AreEqual(1L, s.InventoryState.GetQuantity(s.CraftingState.GetProduces(recipe).GetString("item_id")));
        }
    }
}
