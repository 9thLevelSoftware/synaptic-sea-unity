using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class MedicineStateTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void UsesMedicineAndRoundTrips()
        {
            var dispatcher = new EffectDispatcher();
            dispatcher.Configure();
            var medicine = new MedicineState();
            medicine.Configure();
            var vitals = new FakeVitals { Health = 52.0 };
            var sanity = new FakeSanity { Sanity = 40.0 };
            var radiation = new FakeRadiation { Radiation = 62.0 };
            var statuses = new FakeStatusEffects();
            statuses.AddEffect("radiation_sickness", 18.0, 1);
            statuses.AddEffect("food_poisoning", 18.0, 1);
            var context = new Dictionary<string, object>
            {
                { "vitals_state", vitals },
                { "sanity_state", sanity },
                { "radiation_state", radiation },
                { "status_effects_state", statuses },
            };

            GdDict rad = medicine.UseMedicine("rad_patch",
                new GdDict { { "effects", GdArray.Of("reduce_radiation_minor", "cure_radiation_sickness") } }, dispatcher, context);
            Assert.IsTrue(V.Bool(rad["ok"]));
            Assert.AreEqual(42.0, radiation.Radiation, 0.001);
            Assert.IsFalse(statuses.HasEffect("radiation_sickness"));
            Assert.IsTrue(V.VariantEquals(GdArray.Of("radiation_sickness"), rad["cured_statuses"]));

            medicine.UseMedicine("field_medkit",
                new GdDict { { "effects", GdArray.Of("heal_medium", "restore_stamina_small") } }, dispatcher, context);
            Assert.AreEqual(87.0, vitals.Health, 0.001);

            GdDict summary = medicine.GetSummary();
            var restored = new MedicineState();
            restored.Configure();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.AreEqual("field_medkit", restored.LastItemId);
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }
    }
}
