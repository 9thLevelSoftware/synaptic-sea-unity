using System;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public class ContinuousOxygenDrainCacheTests
    {
        [Test]public void PreparedCacheBackingsMatchNativeSummaryFeedsWithoutNormalizingBits()
        {
            var actual=new OxygenState();actual.Configure(new GdDict{{"zone_ids",GdArray.Of("alpha")}});var oracle=new OxygenState();oracle.Configure(new GdDict{{"zone_ids",GdArray.Of("alpha")}});
            var prepared=OxygenState.PrepareContinuousDrainCaches(.5,.8);actual.InstallContinuousDrainCaches(prepared);
            oracle.ApplyInventorySummary(new GdDict{{"drain_multiplier",.5}});oracle.ApplyEquipmentSummary(new GdDict{{"drain_multiplier",.8}});
            actual.Tick(.25,true);oracle.Tick(.25,true);Assert.IsTrue(ContinuousOxygenValues.CaptureExact(oracle).MatchesRaw(actual));
        }
        [Test]public void SealedMaskDoesNotLoseHiddenCacheWhenPreparedBackingInstalled()
        {
            var actual=new OxygenState();actual.Configure(new GdDict{{"zone_ids",GdArray.Of("alpha")}});actual.SealBreach("alpha");actual.InstallContinuousDrainCaches(OxygenState.PrepareContinuousDrainCaches(.5,.8));
            Assert.AreEqual(1,actual.GetSummary().GetFloat("drain_multiplier"));actual.ReadContinuousDrainCacheMultipliers(out double inventory,out double equipment);Assert.AreEqual(.5,inventory);Assert.AreEqual(.8,equipment);
        }
        [Test]public void SignedZeroPreservedAndNonfiniteRejectedWithoutNativeWrite()
        {
            var actual=new OxygenState();var zero=BitConverter.Int64BitsToDouble(long.MinValue);actual.InstallContinuousDrainCaches(OxygenState.PrepareContinuousDrainCaches(zero,1));
            actual.ReadContinuousDrainCacheMultipliers(out double inventory,out _);Assert.AreEqual(long.MinValue,BitConverter.DoubleToInt64Bits(inventory));
            Assert.Throws<ArgumentException>(()=>OxygenState.PrepareContinuousDrainCaches(double.NaN,1));Assert.Throws<ArgumentException>(()=>OxygenState.PrepareContinuousDrainCaches(1,double.PositiveInfinity));
            actual.ReadContinuousDrainCacheMultipliers(out inventory,out _);Assert.AreEqual(long.MinValue,BitConverter.DoubleToInt64Bits(inventory));
        }
    }
}
