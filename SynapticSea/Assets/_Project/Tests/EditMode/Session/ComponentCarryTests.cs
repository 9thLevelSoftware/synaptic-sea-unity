using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    // Exact raw-fraction oracles for the component-only (schema 2) path; no production/catalog edits.
    public class ComponentCarryTests : SessionFixture
    {
        static void Exact(object expected, object actual, string message)
            => Assert.AreEqual(CanonicalHash.Of(expected), CanonicalHash.Of(actual), message);
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
        static TrainingEventBus Oracle(RunSession s)
        {
            var bus = new TrainingEventBus(); Assert.IsTrue(bus.Configure()); Assert.IsTrue(bus.ApplySummary(s.TrainingEventBus.ToDict()));
            bus.EventFilter = s.TrainingEventBus.EventFilter; bus.SkillGate = s.TrainingEventBus.SkillGate;
            return bus;
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

        [TestCase(0.0000005)]
        [TestCase(0.9999995)]
        public void LegacyApplySummary_EpsilonAndClampRemainExplicitlyUnchanged(double carry)
        {
            RunSession s = Boot(); string skill = RewardSkill("fabricate_part"); SeedExactCarry(s, skill, carry);
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
            RunSession s = Boot(components: true); Assert.AreEqual(2L, s.CaptureComponentDomain().GetInt("schema_version"));
            string evt = s.WorkActionDriver.Catalog.GetAction("dismount_component").GetString("xp_event"), skill = RewardSkill(evt), unrelated = SeedExactCarry(s, skill, carry);
            Policy(s, policy); string instance = BeginActualComponentRemoval(s); GdDict before = s.PlayerProgression.GetSummary();
            var expected = new PlayerProgressionState(); expected.Configure(ClassDefinition.LoadAll()[s.PlayerProgression.ClassId], PlayerProgressionState.LoadSkillsCatalog(), s.PlayerProgression.GetBooksCatalog());
            expected.ApplySummary(before); expected.XpMultipliers.Clear(); foreach (var pair in s.PlayerProgression.XpMultipliers) expected.XpMultipliers[pair.Key] = pair.Value;
            Bits(carry < 0.000001 ? 0.0 : 0.999999, expected.SkillXpFractional.Get(unrelated), "Deliberately legacy oracle only for schema2 control");
            Oracle(s).Emit(evt, instance, expected); FinishActualComponentRemoval(s);
            ExactProgression(expected.GetSummary(), s.PlayerProgression.GetSummary(), "Schema2 behavior is unchanged legacy behavior"); Valid(s.CaptureComponentDomain());
        }

        static void Raw(double expected, object actual, string message) => Bits(expected, actual, message);
        [Test]
        public void DefaultFalseLegacyApplySummary_RetainsExistingFractionTolerance()
        {
            RunSession s = Boot();
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
            RunSession s = Boot(components: true);
            s.PlayerProgression.SkillXpFractional["fabrication"] = 0.30000000000000004;
            GdDict saved = s.CaptureComponentDomain(); Assert.AreEqual(2L, saved.Get("schema_version"));
            Assert.Greater(saved.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Count, 0); Valid(saved);
            Assert.IsTrue(s.ValidateComponentDomainRestore(saved, out string reason), reason);
            s.PlayerProgression.SkillXpFractional["fabrication"] = 0.25;
            Assert.IsTrue(s.RestoreComponentDomain(saved)); Raw(0.30000000000000004, s.PlayerProgression.SkillXpFractional.Get("fabrication"), "Schema2 exact publication remains unchanged");
        }
    }
}
