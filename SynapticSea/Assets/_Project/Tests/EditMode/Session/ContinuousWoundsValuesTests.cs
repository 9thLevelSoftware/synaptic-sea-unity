using System;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.EditMode
{
    public sealed class ContinuousWoundsValuesTests
    {
        static WoundState Actual()
        {
            var actual = new WoundState();
            actual.ApplyWound(new GdDict { { "kind", "laceration" }, { "body_part", "arm" }, { "severity", 0.7 } });
            return actual;
        }
        [Test] public void ExactScratchPreservesRowsAndNextIdentifierWithoutSharedRows()
        {
            var actual = Actual(); var values = ContinuousWoundsValues.CaptureExact(actual); var scratch = values.ExactScratch();
            Assert.IsTrue(values.MatchesRaw(scratch));
            Assert.AreNotSame(actual.Wounds, scratch.Wounds); Assert.AreNotSame(actual.Wounds[0], scratch.Wounds[0]);
            var damage = new GdDict { { "kind", "burn" }, { "body_part", "leg" }, { "severity", 0.3 } };
            Assert.AreEqual(actual.ApplyWound(damage), scratch.ApplyWound(damage));
        }
        [Test] public void TickThenHealMatchesNativeOracleAndLeavesCapturedDataImmutable()
        {
            var actual = Actual(); ((GdDict)actual.Wounds[0])["treated"] = true;
            var values = ContinuousWoundsValues.CaptureExact(actual); var scratch = values.ExactScratch();
            actual.Tick(0.25); scratch.Tick(0.25); actual.Heal(0.25, 0.03, 0.01); scratch.Heal(0.25, 0.03, 0.01);
            Assert.IsTrue(ContinuousWoundsValues.CaptureExact(actual).MatchesRaw(scratch));
            Assert.IsFalse(values.MatchesRaw(actual)); Assert.IsTrue(values.MatchesRaw(values.ExactScratch()));
        }
        [Test] public void SignedZeroPreservedAndBitChangeRefused()
        {
            var actual = Actual(); ((GdDict)actual.Wounds[0])["age_seconds"] = BitConverter.Int64BitsToDouble(long.MinValue);
            var values = ContinuousWoundsValues.CaptureExact(actual); Assert.IsTrue(values.MatchesRaw(values.ExactScratch()));
            ((GdDict)actual.Wounds[0])["age_seconds"] = 0.0; Assert.IsFalse(values.MatchesRaw(actual));
        }
        [Test] public void UnknownFieldAndNonfiniteRefused()
        {
            var actual = Actual(); var row = (GdDict)actual.Wounds[0]; row["unknown"] = true;
            Assert.Throws<ArgumentException>(() => ContinuousWoundsValues.CaptureExact(actual));
            actual = Actual(); ((GdDict)actual.Wounds[0])["severity"] = double.NaN;
            Assert.Throws<ArgumentException>(() => ContinuousWoundsValues.CaptureExact(actual));
        }
        [Test] public void CapacityIsCaptureRefusalWithoutNativeWorldMutation()
        {
            var actual = Actual(); var row = (GdDict)actual.Wounds[0];
            for (int i = 1; i <= ContinuousWoundsValues.MaximumRows; i++) actual.Wounds.Add(row.DeepCopy());
            Assert.Throws<ArgumentException>(() => ContinuousWoundsValues.CaptureExact(actual));
            actual.Tick(0.1); Assert.AreEqual(0.1, ((GdDict)actual.Wounds[0]).Get("age_seconds"));
        }
    }
}
