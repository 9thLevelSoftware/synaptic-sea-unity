using System;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    /// <summary>Provisioned catalog fixtures: no earned-source or gameplay supply claim.</summary>
    public class FieldBandageSessionTests : SessionFixture
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
        static string State(RunSession s) => CanonicalHash.Of(Parts(s));

        [Test]
        public void ExistingCraftedFieldBandagePaysOneAndPreservesExactEffectAcrossContinue()
        {
            var s = Boot(); s.PlayerProgression.Skills["fabrication"] = 0L;
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
            // Ordinary legacy JSON retains its existing decimal codec; only progression's fractional XP
            // differs by floating-point rounding. Preserve exact inventory, wound effect and training assertions.
            foreach (string key in new[] { "inventory", "wounds", "training" })
                Assert.AreEqual(CanonicalHash.Of(partsBefore.Get(key)), CanonicalHash.Of(Parts(s).Get(key)), key);
            var expectedProgression = partsBefore.GetDictOrEmpty("progression").DeepCopy();
            var persistedProgression = (GdDict)GdJson.Parse(GdJson.Stringify(expectedProgression));
            expectedProgression.GetDictOrEmpty("skill_xp_fractional")["first_aid"] =
                persistedProgression.GetDictOrEmpty("skill_xp_fractional").GetFloat("first_aid");
            Assert.AreEqual(CanonicalHash.Of(expectedProgression), CanonicalHash.Of(Parts(s).Get("progression")), "legacy persisted progression matches exact existing codec");
            TestContext.WriteLine("LEGACY_FRACTION_BITS before=" + BitConverter.DoubleToInt64Bits(partsBefore.GetDictOrEmpty("progression").GetDictOrEmpty("skill_xp_fractional").GetFloat("first_aid"))
                + " after=" + BitConverter.DoubleToInt64Bits(s.PlayerProgression.GetSummary().GetDictOrEmpty("skill_xp_fractional").GetFloat("first_aid")));
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
