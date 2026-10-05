using System;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    public sealed class ContinuousOxygenEvaluatorTests
    {
        static OxygenState Model()
        {
            var m = new OxygenState { Oxygen = 41.123456789, MaxOxygen = 100.0001, DrainRate = 6.0001,
                RegenRate = 3.5001, RecoveryThreshold = 30.0001, SafeThreshold = 35.0001 };
            m.BreachZoneIds.Add("cargo");
            m.ApplyInventorySummary(new GdDict { { "drain_multiplier", .5 } });
            m.ApplyEquipmentSummary(new GdDict { { "drain_multiplier", .8 } }); return m;
        }
        static void Exact(ContinuousOxygenValues actual, OxygenState expected)
        {
            var a = actual.ExactScratch().GetSummary(); var e = expected.GetSummary();
            Assert.AreEqual(e.Count, a.Count);
            foreach (var row in e)
            {
                if (row.Value is double d) Assert.AreEqual(BitConverter.DoubleToInt64Bits(d), BitConverter.DoubleToInt64Bits((double)a[row.Key]), row.Key.ToString());
                else if (row.Value is GdArray zones) CollectionAssert.AreEqual(zones, (GdArray)a[row.Key]);
                else Assert.AreEqual(row.Value, a[row.Key]);
            }
        }
        [TestCase(true, false)][TestCase(false, true)][TestCase(false, false)]
        public void ExactNativeTickPreservesCachedInputsAndSubToleranceConfiguration(bool breach, bool field)
        {
            var actual = Model(); var before = ContinuousOxygenValues.CaptureExact(actual); var native = Model();
            native.Tick(.125, new GdDict { { "player_in_breach_zone", breach }, { "field_atmosphere", field },
                { "field_atmosphere_multiplier", .73 }, { "fire_oxygen_drain", .4 } });
            var next = ContinuousOxygenEvaluator.Tick(before, .125, new ContinuousOxygenTickInput(breach, field, .73, .4));
            Exact(next, native); Assert.AreEqual(41.123456789, actual.Oxygen); Assert.AreEqual(100.0001, next.MaxOxygen);
        }
        [Test] public void SealedHomeRetainsFieldMultipliersAndSuitReserveExactOperationOrder()
        {
            var actual = Model(); actual.SealBreach("cargo"); var before = ContinuousOxygenValues.CaptureExact(actual);
            Assert.AreEqual(1.0, (double)actual.GetSummary()["drain_multiplier"]);
            var native = before.ExactScratch(); double suitBefore = native.Oxygen;
            native.Tick(.25, new GdDict { { "player_in_breach_zone", false }, { "field_atmosphere", false }, { "field_atmosphere_multiplier", 1.0 }, { "fire_oxygen_drain", .1 } });
            double perSecond = .4 * native.MaxOxygen / 90 * Math.Max(.1, .9);
            native.Oxygen = Math.Max(0, Math.Min(native.Oxygen, suitBefore) - perSecond * .25);
            Exact(ContinuousOxygenEvaluator.Tick(before, .25, new ContinuousOxygenTickInput(false, false, 1, .1, true, .4, 90, .9)), native);
            var field = before.ExactScratch(); field.Tick(.25, new GdDict { { "field_atmosphere", true }, { "field_atmosphere_multiplier", .7 }, { "fire_oxygen_drain", .1 } });
            Exact(ContinuousOxygenEvaluator.Tick(before, .25, new ContinuousOxygenTickInput(false, true, .7, .1, true, .4, 90, .9)), field);
            Assert.AreEqual(.5, before.InventoryMultiplier); Assert.AreEqual(.8, before.EquipmentMultiplier);
        }
        [Test] public void InvalidInputsRefuseBeforeAnyActualOxygenChange()
        {
            var actual = Model(); var before = ContinuousOxygenValues.CaptureExact(actual);
            Assert.Throws<ArgumentException>(() => new ContinuousOxygenTickInput(false, false, 1, 0, true, .2, 0));
            Assert.Throws<ArgumentException>(() => ContinuousOxygenEvaluator.Tick(before, double.NaN, new ContinuousOxygenTickInput(false, false, 1, 0)));
            Assert.AreEqual(before.Oxygen, actual.Oxygen);
        }
    }
}
