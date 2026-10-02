using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    public class PaidCraftCurrentParticipantTests : PaidCraftFixture
    {
        static void Multiple(Action action)
        {
#if SYNAPTIC_DOTNET_TESTS
            Assert.Multiple(action.Invoke);
#else
            action();
#endif
        }
        static void Exact(object expected, object actual, string message)
            => Assert.AreEqual(PaidCraftingState.Hash(expected), PaidCraftingState.Hash(actual), message);
        static void Raw(double expected, object actual, string message)
        {
            Assert.IsInstanceOf<double>(actual, message);
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits((double)actual), message);
        }
        static GdDict Direct(RunSession s) => new GdDict {
            { "inventory", s.InventoryState.GetSummary() }, { "progression", s.PlayerProgression.GetSummary() },
            { "training", s.TrainingEventBus.ToDict() }, { "crafting", s.CraftingState.GetSummary() },
            { "field", s.FieldCraftingState.GetSummary() }, { "knowledge", s.RecipeKnowledge.GetSummary() },
            { "spoilage", s.SpoilageState.GetSummary() }, { "multipliers", s.PlayerProgression.XpMultipliers.DeepCopy() }
        };
        static void Positive(RunSession s, GdDict domain, bool components)
        {
            Assert.AreEqual(components ? "components_and_craft" : "craft_only", domain.GetString("domain_mode"));
            Assert.AreEqual(3L, domain.Get("schema_version"));
            if (components) Assert.Greater(domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Count, 0, "Actual populated equipment prerequisite");
            Valid(domain); Assert.IsTrue(s.ValidatePaidCraftingRestore(domain, out string reason), reason);
        }

        [TestCase(false, false, "negative_fraction")]
        [TestCase(false, false, "unit_fraction")]
        [TestCase(false, false, "long_fraction")]
        [TestCase(false, false, "bool_fraction")]
        [TestCase(false, false, "string_fraction")]
        [TestCase(false, false, "wrong_schema")]
        [TestCase(false, false, "missing_dictionary")]
        [TestCase(false, false, "negative_xp")]
        [TestCase(false, false, "floating_level")]
        [TestCase(false, false, "false_book")]
        [TestCase(false, false, "empty_class")]
        [TestCase(false, false, "extra_field")]
        [TestCase(false, true, "negative_fraction")]
        [TestCase(false, true, "unit_fraction")]
        [TestCase(false, true, "long_fraction")]
        [TestCase(false, true, "bool_fraction")]
        [TestCase(false, true, "string_fraction")]
        [TestCase(false, true, "wrong_schema")]
        [TestCase(false, true, "missing_dictionary")]
        [TestCase(false, true, "negative_xp")]
        [TestCase(false, true, "floating_level")]
        [TestCase(false, true, "false_book")]
        [TestCase(false, true, "empty_class")]
        [TestCase(false, true, "extra_field")]
        [TestCase(true, false, "negative_fraction")]
        [TestCase(true, false, "unit_fraction")]
        [TestCase(true, false, "long_fraction")]
        [TestCase(true, false, "bool_fraction")]
        [TestCase(true, false, "string_fraction")]
        [TestCase(true, false, "wrong_schema")]
        [TestCase(true, false, "missing_dictionary")]
        [TestCase(true, false, "negative_xp")]
        [TestCase(true, false, "floating_level")]
        [TestCase(true, false, "false_book")]
        [TestCase(true, false, "empty_class")]
        [TestCase(true, false, "extra_field")]
        [TestCase(true, true, "negative_fraction")]
        [TestCase(true, true, "unit_fraction")]
        [TestCase(true, true, "long_fraction")]
        [TestCase(true, true, "bool_fraction")]
        [TestCase(true, true, "string_fraction")]
        [TestCase(true, true, "wrong_schema")]
        [TestCase(true, true, "missing_dictionary")]
        [TestCase(true, true, "negative_xp")]
        [TestCase(true, true, "floating_level")]
        [TestCase(true, true, "false_book")]
        [TestCase(true, true, "empty_class")]
        [TestCase(true, true, "extra_field")]
        public void MalformedCurrentProgression_RejectsBeforeAnyParticipantApply(bool components, bool active, string mutation)
        {
            RunSession s = Boot(components: components); GdDict accepted = null;
            if (active)
            {
                Provision(s); accepted = s.RequestPaidCraft(Kind, Recipe, "current-active");
                Assert.IsTrue(accepted.GetBool("committed"), accepted.GetString("reason"));
                Assert.AreEqual("paid", Job(s, accepted.GetString("job_id")).GetString("input_state"));
            }
            GdDict good = Capture(s); Positive(s, good, components);
            Assert.AreEqual(active ? 1 : 0, Jobs(good).Count);
            GdDict direct = Direct(s), bad = good.DeepCopy();
            GdDict progression = bad.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression");
            const string skill = "fabrication";
            Assert.IsTrue(progression.GetDictOrEmpty("skills").Has(skill));
            Assert.Less(progression.GetDictOrEmpty("skills").GetInt(skill), PlayerProgressionState.MAX_SKILL_LEVEL);
            GdDict fractions = progression.GetDictOrEmpty("skill_xp_fractional");
            switch (mutation)
            {
                case "negative_fraction": fractions[skill] = -0.5; break;
                case "unit_fraction": fractions[skill] = 1.0; break;
                case "long_fraction": fractions[skill] = 0L; break;
                case "bool_fraction": fractions[skill] = false; break;
                case "string_fraction": fractions[skill] = "0.5"; break;
                case "wrong_schema": progression["schema"] = "progression-invalid"; break;
                case "missing_dictionary": progression.Erase("cross_training"); break;
                case "negative_xp": progression.GetDictOrEmpty("skill_xp")[skill] = -1L; break;
                case "floating_level": progression.GetDictOrEmpty("skills")[skill] = 4.0; break;
                case "false_book": progression.GetDictOrEmpty("books_read")["malformed-value-only"] = false; break;
                case "empty_class": progression["class_id"] = ""; break;
                case "extra_field": progression["unrecognized_field"] = true; break;
                default: Assert.Fail("Unknown mutation"); break;
            }
            GdDict onlyCurrent = bad.DeepCopy();
            onlyCurrent.GetDictOrEmpty("participating_state")["progression"] = good.GetDictOrEmpty("participating_state").Get("progression");
            Exact(good, onlyCurrent, "Only CURRENT progression changed: every proof/node/job/receipt/other participant is untouched");
            bool domainAccepted = DomainBundle.TryCreate(bad, out _, out _);
            bool sessionAccepted = s.ValidatePaidCraftingRestore(bad, out _);
            bool restored = s.RestorePaidCraftingDomain(bad);
            GdDict afterDirect = Direct(s), afterOwner = Capture(s);
            GdDict replay = active ? s.RequestPaidCraft(Kind, Recipe, "current-active") : null;
            Multiple(() => {
                Assert.IsFalse(domainAccepted, "Schema3 current progression needs the existing strict progression validator");
                Assert.IsFalse(sessionAccepted, "Session preflight must reject malformed CURRENT state");
                Assert.IsFalse(restored, "Malformed current state cannot reach direct participant apply");
                Exact(direct, afterDirect, "Every direct participant retains its exact before-image");
                Exact(good, afterOwner, "Owner and revision remain unchanged");
                if (active) Exact(accepted, replay, "Existing start receipt replays exactly without recharge");
                Exact(direct, Direct(s), "Replay never modifies inventory, XP, food or knowledge");
                Exact(good, Capture(s), "Replay preserves complete owner");
            });
        }

        [TestCase(false, 0.0000005)]
        [TestCase(false, 0.9999995)]
        [TestCase(false, 0.30000000000000004)]
        [TestCase(true, 0.0000005)]
        [TestCase(true, 0.9999995)]
        [TestCase(true, 0.30000000000000004)]
        public void ValidCurrentCarry_ExactSelectedRestoreWithoutHistoricalNodeBinding(bool components, double carry)
        {
            RunSession s = Boot(components: components);
            s.PlayerProgression.SkillXpFractional["fabrication"] = carry;
            GdDict saved = Capture(s); Positive(s, saved, components);
            Assert.AreEqual(0, Paid(saved).GetDictOrEmpty("reward_history").GetDictOrEmpty("progression_nodes").Count, "No historical reward node is needed to authorize valid current progression");
            Raw(carry, saved.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression").GetDictOrEmpty("skill_xp_fractional").Get("fabrication"), "Exact current starting value");
            GdDict direct = Direct(s);
            Assert.IsTrue(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(saved), out GdDict decoded, out string reason), reason);
            Exact(saved, decoded, "Typed codec preserves raw current participant");
            s.PlayerProgression.SkillXpFractional["fabrication"] = 0.25; Capture(s);
            Assert.IsTrue(s.RestorePaidCraftingDomain(decoded));
            Raw(carry, s.PlayerProgression.SkillXpFractional.Get("fabrication"), "Selected restore keeps exact raw double");
            Exact(direct, Direct(s), "Valid selected owner replaces exact direct state");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OrdinaryLiveProgressionAndBookMetadata_AfterPaidReceiptRemainIndependent(bool components)
        {
            RunSession s = Boot(components: components); Provision(s); string id = Start(s); s.AdvanceCrafting(100);
            Assert.AreEqual("completed_delivered", Job(s, id).GetString("status"));
            GdDict originalResult = s.RetryPaidCraft(id, "historical-read"), before = Capture(s); Positive(s, before, components);
            // Existing Core helpers, not authored data edits or an earned book/acquisition claim.
            s.PlayerProgression.GrantXp("fabrication", 1L);
            Assert.IsNotNull(s.TrainingEventBus.Emit("fabricate_part", "ordinary-live-after-paid", s.PlayerProgression));
            GdDict books = s.PlayerProgression.GetBooksCatalog();
            string book = books.Keys.Select(V.Str).First(key => !s.PlayerProgression.BooksRead.GetBool(key) &&
                s.PlayerProgression.Skills.Has(books.GetDictOrEmpty(key).GetString("target_skill")));
            Assert.IsTrue(s.PlayerProgression.GrantXpFromBook(book)); Assert.IsTrue(s.PlayerProgression.BooksRead.GetBool(book));
            GdDict saved = Capture(s); Positive(s, saved, components);
            Assert.AreNotEqual(PaidCraftingState.Hash(before.GetDictOrEmpty("participating_state").Get("progression")),
                PaidCraftingState.Hash(saved.GetDictOrEmpty("participating_state").Get("progression")), "Current live progression legitimately differs from the paid historical after-state");
            Exact(before.Get("receipts"), saved.Get("receipts"), "Ordinary changes cannot rewrite prior paid receipts");
            Exact(Paid(before).GetDictOrEmpty("reward_history").Get("progression_nodes"), Paid(saved).GetDictOrEmpty("reward_history").Get("progression_nodes"), "Historical progression proofs remain immutable");
            GdDict direct = Direct(s);
            Assert.IsTrue(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(saved), out GdDict decoded, out string reason), reason);
            Assert.IsTrue(s.RestorePaidCraftingDomain(decoded)); Exact(direct, Direct(s), "Ordinary live changes and books restore exactly");
            Exact(originalResult, s.RetryPaidCraft(id, "after-current-change"), "Historical replay is independent of current progression");
            Exact(direct, Direct(s), "Historical replay grants nothing");
        }

        [Test]
        public void DefaultFalseLegacyApplySummary_RetainsExistingFractionTolerance()
        {
            RunSession s = Boot(paid: false); Assert.IsFalse(s.PaidCraftingEnabled);
            s.PlayerProgression.SkillXpFractional["fabrication"] = 0.0000005;
            var copy = new PlayerProgressionState();
            copy.Configure(ClassDefinition.LoadAll()[s.PlayerProgression.ClassId], PlayerProgressionState.LoadSkillsCatalog());
            copy.ApplySummary(s.PlayerProgression.GetSummary());
            Raw(0.0, copy.SkillXpFractional.Get("fabrication"), "Legacy loading epsilon is unchanged");
            Raw(0.0000005, s.PlayerProgression.SkillXpFractional.Get("fabrication"), "Legacy copy leaves its source alone");
        }
        [Test]
        public void DefaultFalsePopulatedSchemaTwo_ValidCurrentRestoreRemainsExact()
        {
            RunSession s = Boot(components: true, paid: false); Assert.IsFalse(s.PaidCraftingEnabled);
            s.PlayerProgression.SkillXpFractional["fabrication"] = 0.30000000000000004;
            GdDict saved = s.CaptureComponentDomain(); Assert.AreEqual(2L, saved.Get("schema_version"));
            Assert.Greater(saved.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Count, 0); Valid(saved);
            Assert.IsTrue(s.ValidateComponentDomainRestore(saved, out string reason), reason);
            s.PlayerProgression.SkillXpFractional["fabrication"] = 0.25;
            Assert.IsTrue(s.RestoreComponentDomain(saved)); Raw(0.30000000000000004, s.PlayerProgression.SkillXpFractional.Get("fabrication"), "Schema2 exact publication remains unchanged");
        }
    }
}
