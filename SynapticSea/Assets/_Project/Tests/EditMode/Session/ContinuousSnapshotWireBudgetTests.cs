using NUnit.Framework;
using SynapticSea.Core.Systems;
namespace SynapticSea.Tests.Session
{
    public class ContinuousSnapshotWireBudgetTests
    {
        [Test]public void AggregateBytesIncludeBothImmutableTexts(){var text="{\"value\":\""+new string('x',2100000)+"\"}";Assert.IsFalse(ContinuousSnapshotWireBudget.TryPreflight(text,text,out _,out _,out string reason));Assert.AreEqual("whole_snapshot_wire_bytes",reason);}
        [Test]public void UnicodeUtf8ChargedRatherThanCharacterLength(){var text="{\"value\":\""+new string('\u4e00',800000)+"\"}";Assert.IsFalse(ContinuousSnapshotWireBudget.TryPreflight(text,text,out _,out _,out string reason));Assert.AreEqual("whole_snapshot_wire_bytes",reason);}
        [Test]public void EscapedQuotesAndBracesInsideStringsAreNotContainers(){Assert.IsTrue(ContinuousSnapshotWireBudget.TryPreflight("{\"value\":\"[\\\"{]\"}","{}",out _,out long tokens,out _));Assert.AreEqual(4,tokens);}
        [Test]public void ExactDepthThenOneBeyond(){var allowed=new string('[',386)+"0"+new string(']',386);Assert.IsTrue(ContinuousSnapshotWireBudget.TryPreflight(allowed,"{}",out _,out _,out _));var denied="["+allowed+"]";Assert.IsFalse(ContinuousSnapshotWireBudget.TryPreflight(denied,"{}",out _,out _,out string reason));Assert.AreEqual("whole_snapshot_wire_depth",reason);}
        [Test]public void TokenLimitIsAggregate(){var array="["+string.Join(",",new string[275000]).Replace(",", "0,")+"0]";Assert.IsFalse(ContinuousSnapshotWireBudget.TryPreflight(array,array,out _,out _,out string reason));Assert.AreEqual("whole_snapshot_wire_nodes",reason);}
        [Test]public void TruncatedQuoteOrContainerRefuses(){Assert.IsFalse(ContinuousSnapshotWireBudget.TryPreflight("{\"a\":\"tail", "{}",out _,out _,out _));Assert.IsFalse(ContinuousSnapshotWireBudget.TryPreflight("[", "{}",out _,out _,out _));}
    }
}
