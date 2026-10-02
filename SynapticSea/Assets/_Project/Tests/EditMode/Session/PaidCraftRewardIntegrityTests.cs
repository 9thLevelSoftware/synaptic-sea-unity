using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    // Valid real-catalog transitions and exact raw fraction oracles; no production/catalog edits.
    public class PaidCraftRewardIntegrityTests : PaidCraftFixture
    {
        static void Multiple(Action action)
        {
#if SYNAPTIC_DOTNET_TESTS
            Assert.Multiple(action.Invoke);
#else
            action();
#endif
        }
        static GdDict Direct(RunSession s) => new GdDict {
            { "inventory", s.InventoryState.GetSummary() }, { "progression", s.PlayerProgression.GetSummary() },
            { "training", s.TrainingEventBus.ToDict() }, { "crafting", s.CraftingState.GetSummary() },
            { "field", s.FieldCraftingState.GetSummary() }, { "knowledge", s.RecipeKnowledge.GetSummary() },
            { "spoilage", s.SpoilageState.GetSummary() }
        };
        static void Positive(RunSession s, GdDict domain)
        {
            Valid(domain);
            Assert.IsTrue(s.ValidatePaidCraftingRestore(domain, out string reason), reason);
        }
        static void Exact(object expected, object actual, string message)
            => Assert.AreEqual(PaidCraftingState.Hash(expected), PaidCraftingState.Hash(actual), message);
        static void Bits(double expected, object actual, string message)
        {
            Assert.IsInstanceOf<double>(actual, message + " literal double");
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits((double)actual), message);
        }
        static void ExactProgression(GdDict expected, GdDict actual, string message)
        {
            foreach (var pair in expected.GetDictOrEmpty("skill_xp_fractional"))
                Bits((double)pair.Value, actual.GetDictOrEmpty("skill_xp_fractional").Get(pair.Key), message + ": " + pair.Key);
            Exact(expected, actual, message + ": all fields");
        }
        static void Policy(RunSession s, string policy)
        {
            s.TrainingEventBus.EventFilter = (evt, target) => policy == "filtered";
            s.TrainingEventBus.SkillGate = skill => policy != "gated";
        }
        static string RewardSkill(string eventId)
        {
            var catalog = (GdDict)CatalogRegistry.Load(TrainingEventBus.DEFAULT_TRAINING_ACTIONS_PATH);
            return catalog.GetArrayOrEmpty("training_actions").OfType<GdDict>().Single(row => row.GetString("event_id") == eventId).GetString("target_skill");
        }
        static string SeedExactCarry(RunSession s, string target, double carry)
        {
            Assert.That(carry, Is.GreaterThan(0).And.LessThan(1));
            Assert.IsTrue(s.PlayerProgression.Skills.Has(target));
            // Public runtime model fields: valid nonmaxed state, no fabricated recipe or reward rule.
            foreach (object skill in s.PlayerProgression.Skills.Keys.ToArray()) s.PlayerProgression.Skills[skill] = 4L;
            string unrelated = s.PlayerProgression.Skills.Keys.Select(V.Str).First(skill => skill != target);
            s.PlayerProgression.SkillXpFractional[target] = carry;
            s.PlayerProgression.SkillXpFractional[unrelated] = carry;
            foreach (object category in s.PlayerProgression.XpMultipliers.Keys.ToArray()) s.PlayerProgression.XpMultipliers[category] = 1.375;
            Bits(carry, s.PlayerProgression.SkillXpFractional.Get(target), "Valid rewarded starting carry");
            Bits(carry, s.PlayerProgression.SkillXpFractional.Get(unrelated), "Valid unrelated starting carry");
            return unrelated;
        }
        static PlayerProgressionState ExactClone(RunSession s, GdDict before)
        {
            var clone = new PlayerProgressionState();
            clone.Configure(ClassDefinition.LoadAll()[before.GetString("class_id")], PlayerProgressionState.LoadSkillsCatalog(), s.PlayerProgression.GetBooksCatalog());
            // Intentionally do not use ApplySummary: its legacy epsilon/clamp is the defect under test.
            clone.ClassId = before.GetString("class_id");
            clone.Skills = before.GetDictOrEmpty("skills").DeepCopy();
            clone.SkillXp = before.GetDictOrEmpty("skill_xp").DeepCopy();
            clone.SkillXpFractional = before.GetDictOrEmpty("skill_xp_fractional").DeepCopy();
            clone.CrossTraining = before.GetDictOrEmpty("cross_training").DeepCopy();
            clone.BooksRead = before.GetDictOrEmpty("books_read").DeepCopy();
            clone.XpMultipliers.Clear(); foreach (var pair in s.PlayerProgression.XpMultipliers) clone.XpMultipliers[pair.Key] = pair.Value;
            ExactProgression(before, clone.GetSummary(), "Oracle starts from the exact actual before-image");
            return clone;
        }
        static TrainingEventBus Oracle(RunSession s)
        {
            var bus = new TrainingEventBus(); Assert.IsTrue(bus.Configure()); Assert.IsTrue(bus.ApplySummary(s.TrainingEventBus.ToDict()));
            bus.EventFilter = s.TrainingEventBus.EventFilter; bus.SkillGate = s.TrainingEventBus.SkillGate;
            return bus;
        }
        static GdDict DeliveredResult(RunSession s, string command)
        {
            Provision(s); string id = Start(s, commandId: command); s.AdvanceCrafting(100);
            Assert.AreEqual("completed_delivered", Job(s, id).GetString("status"));
            GdDict result = s.RetryPaidCraft(id, "read-" + command); Assert.IsTrue(result.GetBool("committed")); return result;
        }
        static void GenuineAppend(GdDict result, int prefixCount)
        {
            GdDict effect = result.GetDictOrEmpty("result"), before = effect.GetDictOrEmpty("training_before"), after = effect.GetDictOrEmpty("training_after");
            GdArray prefix = before.GetArrayOrEmpty("log"), log = after.GetArrayOrEmpty("log");
            Assert.AreEqual(prefixCount, prefix.Count); Assert.AreEqual(prefixCount + 1, log.Count);
            Assert.AreEqual((long)prefixCount, before.GetInt("event_count")); Assert.AreEqual((long)prefixCount + 1, after.GetInt("event_count"));
            for (int i = 0; i < prefix.Count; i++) Exact(prefix[i], log[i], "Unchanged historical prefix");
            GdDict record = effect.GetDictOrEmpty("training_record");
            Exact(record, log[prefixCount], "One exact appended receipt row"); Assert.AreEqual((long)prefixCount, record.Get("sequence"));
            Assert.AreEqual(result.GetString("commit_id"), record.GetString("commit_id"));
            Assert.IsFalse(prefix.OfType<GdDict>().Any(row => row.GetString("commit_id") == result.GetString("commit_id")), "Commit is absent before its genuine reward");
        }

        [TestCase("accepted")]
        [TestCase("gated")]
        public void HistoricalBeforeAlreadyOwnsThisCompletion_RejectsWithoutMutation(string policy)
        {
            RunSession s = Boot(); Policy(s, policy);
            Assert.AreEqual(0L, s.TrainingEventBus.GetEventCount());
            GdDict original = DeliveredResult(s, "proof-first"); GenuineAppend(original, 0);
            GdDict good = Capture(s); Positive(s, good); GdDict direct = Direct(s), bad = good.DeepCopy();
            string commit = original.GetString("commit_id"), jobId = original.GetString("job_id");
            GdDict effect = bad.GetDictOrEmpty("receipts").GetDictOrEmpty(commit).GetDictOrEmpty("result");
            GdDict proof = effect.GetDictOrEmpty("reward_proof"), beforeRef = proof.GetDictOrEmpty("training_before_ref").DeepCopy(), afterRef = proof.GetDictOrEmpty("training_after_ref");
            foreach (string key in new[] { "tip_hash", "count", "event_count" }) beforeRef[key] = afterRef.Get(key);
            proof["training_before_ref"] = beforeRef;
            proof["training_before_hash"] = PaidCraftingState.Hash(new GdDict {
                { "log", GdArray.Of(effect.GetDictOrEmpty("training_record").DeepCopy()) }, { "dropped", beforeRef.Get("dropped") },
                { "xp_total", beforeRef.Get("xp_total") }, { "event_count", beforeRef.Get("event_count") }
            });
            // Exactly two historical-proof fields changed; current authority, nodes and after proof are intact.
            GdDict restoredCopy = bad.DeepCopy(), originalProof = good.GetDictOrEmpty("receipts").GetDictOrEmpty(commit).GetDictOrEmpty("result").GetDictOrEmpty("reward_proof");
            GdDict copyProof = restoredCopy.GetDictOrEmpty("receipts").GetDictOrEmpty(commit).GetDictOrEmpty("result").GetDictOrEmpty("reward_proof");
            copyProof["training_before_ref"] = originalProof.Get("training_before_ref"); copyProof["training_before_hash"] = originalProof.Get("training_before_hash");
            Exact(good, restoredCopy, "The mutant changes only its historical before reference/hash");
            bool schemaAccepted = DomainBundle.TryCreate(bad, out _, out _), sessionAccepted = s.ValidatePaidCraftingRestore(bad, out _), applied = s.RestorePaidCraftingDomain(bad);
            GdDict afterDirect = Direct(s), afterOwner = Capture(s), replay = s.RetryPaidCraft(jobId, "after-rejected-import");
            Multiple(() => {
                Assert.IsFalse(schemaAccepted, "Independent proof requires absence then one new append"); Assert.IsFalse(sessionAccepted); Assert.IsFalse(applied);
                Exact(direct, afterDirect, "Rejected proof preserves every direct participant"); Exact(good, afterOwner, "Rejected proof preserves owner");
                Exact(original, replay, "Rejected proof cannot rewrite historical public replay"); Exact(direct, Direct(s), "Replay grants nothing");
            });
        }

        [TestCase("accepted")]
        [TestCase("gated")]
        public void NonemptyOrdinaryPrefix_GenuineAppendAndRecordAppliedIdempotenceRemainValid(string policy)
        {
            RunSession s = Boot(); Assert.IsNotNull(s.TrainingEventBus.Emit("fabricate_part", "ordinary-prefix", s.PlayerProgression));
            Policy(s, policy); GdDict result = DeliveredResult(s, "normal-append"); GenuineAppend(result, 1); Positive(s, Capture(s));
            var adapter = new TrainingEventBus(); adapter.Configure(); GdDict record = result.GetDictOrEmpty("result").GetDictOrEmpty("training_record");
            adapter.RecordApplied(record, result.GetString("commit_id")); GdDict once = adapter.ToDict();
            adapter.RecordApplied(record, result.GetString("commit_id")); Exact(once, adapter.ToDict(), "RecordApplied itself remains idempotent");
            Exact(result, s.RetryPaidCraft(result.GetString("job_id"), "normal-replay"), "Historical public result remains exact");
        }
        [Test]
        public void LegitimateResetBranch_RetainsBothGenuineHistoricalAppends()
        {
            RunSession s = Boot(); GdDict first = DeliveredResult(s, "first-branch"); GenuineAppend(first, 0);
            s.TrainingEventBus.Reset(); Capture(s); GdDict second = DeliveredResult(s, "second-branch"); GenuineAppend(second, 0);
            GdDict saved = Capture(s); Positive(s, saved); Assert.IsTrue(s.RestorePaidCraftingDomain(saved));
            Exact(first, s.RetryPaidCraft(first.GetString("job_id"), "first-history"), "First branch persists independently of current log");
            Exact(second, s.RetryPaidCraft(second.GetString("job_id"), "second-history"), "Current branch remains exact");
        }

        [TestCase("weld_plating", "accepted", 0.0000005)]
        [TestCase("weld_plating", "gated", 0.0000005)]
        [TestCase("weld_plating", "filtered", 0.0000005)]
        [TestCase("field_bandage", "accepted", 0.0000005)]
        [TestCase("field_bandage", "gated", 0.0000005)]
        [TestCase("field_bandage", "filtered", 0.0000005)]
        [TestCase("weld_plating", "accepted", 0.9999995)]
        [TestCase("weld_plating", "gated", 0.9999995)]
        [TestCase("weld_plating", "filtered", 0.9999995)]
        [TestCase("field_bandage", "accepted", 0.9999995)]
        [TestCase("field_bandage", "gated", 0.9999995)]
        [TestCase("field_bandage", "filtered", 0.9999995)]
        public void PaidCompletion_PreservesExactEdgeCarryAndOnlyExistingGrant(string recipe, string policy, double carry)
        {
            RunSession s = Boot(); string skill = RewardSkill("fabricate_part"), unrelated = SeedExactCarry(s, skill, carry); Policy(s, policy);
            Provision(s, recipe); string id = Start(s, recipe); GdDict ownerBefore = Capture(s); Positive(s, ownerBefore);
            GdDict before = s.PlayerProgression.GetSummary(); Bits(carry, before.GetDictOrEmpty("skill_xp_fractional").Get(skill), "Payment retained target carry");
            PlayerProgressionState expected = ExactClone(s, before); TrainingEventBus oracle = Oracle(s);
            GdDict generated = oracle.Emit("fabricate_part", s.CraftingState.GetProduces(recipe).GetString("item_id"), expected);
            if (policy != "accepted") ExactProgression(before, expected.GetSummary(), "No-XP oracle leaves all progression unchanged");
            s.AdvanceCrafting(100); Assert.AreEqual("completed_delivered", Job(s, id).GetString("status"), "Reach actual committed reward before raw carry assertion");
            Multiple(() => {
                Bits(carry, s.PlayerProgression.SkillXpFractional.Get(unrelated), "Unrelated raw carry survives paid publication");
                ExactProgression(expected.GetSummary(), s.PlayerProgression.GetSummary(), "Exact existing grant from exact before-image");
            });
            Assert.AreEqual(oracle.GetDroppedCount(), s.TrainingEventBus.GetDroppedCount()); Assert.AreEqual(oracle.GetTotalXpDelivered(), s.TrainingEventBus.GetTotalXpDelivered());
            Assert.AreEqual(generated == null ? 0L : 1L, s.TrainingEventBus.GetEventCount());
            GdDict result = s.RetryPaidCraft(id, "exact-result"), effect = result.GetDictOrEmpty("result");
            ExactProgression(before, effect.GetDictOrEmpty("progression_before"), "Historical before proof retains raw starting fields");
            ExactProgression(expected.GetSummary(), effect.GetDictOrEmpty("progression_after"), "Historical after proof retains exact reward");
            RoundTripPaid(s); GdDict once = Direct(s); Exact(result, s.RetryPaidCraft(id, "exact-replay"), "Replay is lossless"); Exact(once, Direct(s), "Replay cannot train");
        }
        static void RoundTripPaid(RunSession s)
        {
            GdDict saved = Capture(s), direct = Direct(s); Positive(s, saved);
            Assert.IsTrue(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(saved), out GdDict decoded, out string reason), reason);
            Exact(saved, decoded, "Typed codec preserves all fields"); Assert.IsTrue(s.RestorePaidCraftingDomain(decoded));
            ExactProgression(direct.GetDictOrEmpty("progression"), s.PlayerProgression.GetSummary(), "Exact restore publication"); Exact(direct, Direct(s), "Restore preserves every direct participant");
        }
        [TestCase(0.0000005)]
        [TestCase(0.9999995)]
        public void ValidRawCarry_CodecAndSelectedOwnerRestoreAlreadyPreserveExactBits(double carry)
        {
            RunSession s = Boot(components: true); string target = RewardSkill("fabricate_part"); SeedExactCarry(s, target, carry);
            GdDict saved = Capture(s); Positive(s, saved); GdDict expected = s.PlayerProgression.GetSummary();
            Assert.IsTrue(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(saved), out GdDict decoded, out string reason), reason);
            ExactProgression(expected, decoded.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression"), "Codec already exact before a reward");
            s.PlayerProgression.SkillXpFractional[target] = 0.25; Capture(s);
            Assert.IsTrue(s.RestorePaidCraftingDomain(decoded)); ExactProgression(expected, s.PlayerProgression.GetSummary(), "Selected generation replaces exact raw carry");
        }

        static string BeginActualComponentRemoval(RunSession s)
        {
            GdDict domain = s.CaptureComponentDomain(); Valid(domain);
            GdDict mounted = domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values.OfType<GdDict>()
                .First(row => domain.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder")).GetString("kind") == "slot");
            string id = mounted.GetString("instance_id");
            GdDict target = s.ListInstallTargets(id).OfType<GdDict>().Single(row => row.GetString("holder_id") == mounted.GetString("holder"));
            s.Scene.PlayerPosition = target.Get("world_position") is Vec3 position ? position : Vec3.FromArray((GdArray)target.Get("world_position"));
            Assert.AreEqual(1L, s.InventoryState.AddItem("wrench", 1)); s.BeginWorkHold();
            GdDict request = s.RequestComponentRemoval(id); Assert.IsTrue(request.GetBool("ok"), request.GetString("reason")); return id;
        }
        static string FinishActualComponentRemoval(RunSession s)
        {
            for (int tick = 0; tick < 140 && s.GetComponentWorkState().GetString("status") != "committed"; tick++) s.StageWorkAction(0.1);
            GdDict work = s.GetComponentWorkState(); Assert.AreEqual("committed", work.GetString("status"), "Reach an actual timed component publication"); s.EndWorkHold();
            return "component_transfer:" + work.GetString("command_id");
        }
        [TestCase("accepted", 0.0000005)]
        [TestCase("gated", 0.0000005)]
        [TestCase("filtered", 0.0000005)]
        [TestCase("accepted", 0.9999995)]
        [TestCase("gated", 0.9999995)]
        [TestCase("filtered", 0.9999995)]
        public void CombinedComponentReward_PreservesExactEdgeCarry(string policy, double carry)
        {
            RunSession s = Boot(components: true); string evt = s.WorkActionDriver.Catalog.GetAction("dismount_component").GetString("xp_event"), skill = RewardSkill(evt);
            string unrelated = SeedExactCarry(s, skill, carry); Policy(s, policy); string instance = BeginActualComponentRemoval(s);
            GdDict before = s.PlayerProgression.GetSummary(); Positive(s, Capture(s)); PlayerProgressionState expected = ExactClone(s, before); TrainingEventBus oracle = Oracle(s);
            oracle.Emit(evt, instance, expected); if (policy != "accepted") ExactProgression(before, expected.GetSummary(), "No-XP component oracle is exact");
            string commit = FinishActualComponentRemoval(s);
            Multiple(() => {
                Bits(carry, s.PlayerProgression.SkillXpFractional.Get(unrelated), "Unrelated carry survives combined component publication");
                ExactProgression(expected.GetSummary(), s.PlayerProgression.GetSummary(), "Combined component uses exact starting fields");
            });
            Assert.AreEqual(oracle.GetEventCount(), s.TrainingEventBus.GetEventCount()); Assert.AreEqual(oracle.GetDroppedCount(), s.TrainingEventBus.GetDroppedCount()); Assert.AreEqual(oracle.GetTotalXpDelivered(), s.TrainingEventBus.GetTotalXpDelivered());
            RoundTripPaid(s); GdDict once = Direct(s), saved = Capture(s); var owner = new DomainTransactionCoordinator(saved);
            Assert.IsTrue(owner.Commit(commit).GetBool("committed")); Assert.IsTrue(owner.Commit(commit).GetBool("committed"));
            Exact(saved, owner.GetSummary(), "Component receipt reads do not mutate selected owner"); s.StageWorkAction(0.1); Exact(once, Direct(s), "Completed actual component cannot grant again");
        }

        [TestCase(0.0000005)]
        [TestCase(0.9999995)]
        public void LegacyApplySummary_EpsilonAndClampRemainExplicitlyUnchanged(double carry)
        {
            RunSession s = Boot(paid: false); Assert.IsFalse(s.PaidCraftingEnabled); string skill = RewardSkill("fabricate_part"); SeedExactCarry(s, skill, carry);
            var legacy = new PlayerProgressionState(); legacy.Configure(ClassDefinition.LoadAll()[s.PlayerProgression.ClassId], PlayerProgressionState.LoadSkillsCatalog());
            legacy.ApplySummary(s.PlayerProgression.GetSummary()); Bits(carry < 0.000001 ? 0.0 : 0.999999, legacy.SkillXpFractional.Get(skill), "Existing legacy load semantics");
            Bits(carry, s.PlayerProgression.SkillXpFractional.Get(skill), "Copy never changes source");
        }
        [TestCase("accepted", 0.0000005)]
        [TestCase("gated", 0.0000005)]
        [TestCase("filtered", 0.0000005)]
        [TestCase("accepted", 0.9999995)]
        [TestCase("gated", 0.9999995)]
        [TestCase("filtered", 0.9999995)]
        public void DefaultFalseSchemaTwo_ComponentCopyRetainsLegacyBehavior(string policy, double carry)
        {
            RunSession s = Boot(components: true, paid: false); Assert.IsFalse(s.PaidCraftingEnabled); Assert.AreEqual(2L, s.CaptureComponentDomain().GetInt("schema_version"));
            string evt = s.WorkActionDriver.Catalog.GetAction("dismount_component").GetString("xp_event"), skill = RewardSkill(evt), unrelated = SeedExactCarry(s, skill, carry);
            Policy(s, policy); string instance = BeginActualComponentRemoval(s); GdDict before = s.PlayerProgression.GetSummary();
            var expected = new PlayerProgressionState(); expected.Configure(ClassDefinition.LoadAll()[s.PlayerProgression.ClassId], PlayerProgressionState.LoadSkillsCatalog(), s.PlayerProgression.GetBooksCatalog());
            expected.ApplySummary(before); expected.XpMultipliers.Clear(); foreach (var pair in s.PlayerProgression.XpMultipliers) expected.XpMultipliers[pair.Key] = pair.Value;
            Bits(carry < 0.000001 ? 0.0 : 0.999999, expected.SkillXpFractional.Get(unrelated), "Deliberately legacy oracle only for schema2 control");
            Oracle(s).Emit(evt, instance, expected); FinishActualComponentRemoval(s);
            ExactProgression(expected.GetSummary(), s.PlayerProgression.GetSummary(), "Schema2 behavior is outside the paid exact-copy correction"); Valid(s.CaptureComponentDomain());
        }
    }
}
