using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public class ContinuousWholeSnapshotBudgetTests
    {
        [Test]public void ExactStringBytesMatchActualStrictPaidWriter()
        {
            foreach(string text in new[]{"plain ASCII", "\"\\\b\t\n\f\r\0\u001f", "é漢\ud83d\ude80", "\u2028\u2029"})
            {
                Assert.IsTrue(ContinuousWholeSnapshotBudget.TryStringBytes(text,out long count));
                string wire=PaidSnapshotCodec.Stringify(new GdDict{{"s",text}});
                Assert.AreEqual(new System.Text.UTF8Encoding(false,true).GetByteCount(wire)-6,count);
            }
            Assert.IsFalse(ContinuousWholeSnapshotBudget.TryStringBytes("\ud800",out _));
            Assert.IsFalse(ContinuousWholeSnapshotBudget.TryStringBytes("\udc00",out _));
        }
        [Test]public void TypedDecodedVectorLeafChargedPerOccurrenceButRawVectorRefuses()
        {
            var vector=new Vec3(-0.0,2.5,-3);var decoded=new GdDict{{"local_position",vector}};
            var transport=new GdDict{{"wire","owned"}};var root=new GdDict{{"a",transport},{"b",transport}};
            var map=new System.Collections.Generic.Dictionary<GdDict,GdDict>{{transport,decoded}};
            Assert.IsTrue(ContinuousWholeSnapshotBudget.TryPreflightOwnedProjection(root,map,out long nodes,out long bytes,out _));
            Assert.AreEqual(9,nodes);Assert.GreaterOrEqual(bytes,256);
            Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflightEnvelope(decoded,out _,out _,out _));
            Assert.AreEqual(vector,decoded.Get("local_position"));
            decoded["local_position"]=Vec3.Inf;
            Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflightOwnedProjection(root,map,out _,out _,out string reason));
            Assert.AreEqual("whole_snapshot_nonfinite",reason);
        }
        [Test]public void NativeInt64SequenceKeysChargeExactWireSpellingWithoutMutation()
        {
            var native=new GdDict{{1L,new GdDict{{"complete",false}}},{long.MaxValue,1L}};
            var wire=new GdDict{{"1",new GdDict{{"complete",false}}},{"9223372036854775807",1L}};
            Assert.IsTrue(ContinuousWholeSnapshotBudget.TryPreflightEnvelope(native,out long n,out long b,out _));
            Assert.IsTrue(ContinuousWholeSnapshotBudget.TryPreflightEnvelope(wire,out long wn,out long wb,out _));
            Assert.AreEqual(wn,n);Assert.AreEqual(wb,b);Assert.IsTrue(native.Has(1L));Assert.IsFalse(native.Has("1"));
            Assert.AreEqual(GdJson.Stringify(wire),GdJson.Stringify(native));
        }
        [Test]public void NativeAndTextKeyCollisionRefusesBeforeNormalization()
        {
            var source=new GdDict{{1L,1L},{"1",2L}};
            Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflightEnvelope(source,out _,out _,out string reason));
            Assert.AreEqual("whole_snapshot_key_collision",reason);Assert.AreEqual(2,source.Count);
        }
        [Test]public void MirrorOccurrencesChargedWithoutMutatingInput(){var shared=new GdDict{{"state",1L}};var run=new GdDict{{"a",shared}};var world=new GdDict{{"home_ship",shared}};Assert.IsTrue(ContinuousWholeSnapshotBudget.TryPreflight(run,world,out long nodes,out _,out _));Assert.AreEqual(13,nodes);Assert.AreSame(shared,world.Get("home_ship"));}
        [Test]public void AggregateNestedHomesRefuseBeforeCloning(){var text=new string('x',2200000);var run=new GdDict{{"text",text}};var world=new GdDict{{"home_ship",new GdDict{{"text",text}}}};Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflight(run,world,out _,out _,out string reason));StringAssert.StartsWith("whole_snapshot_bytes",reason);}
        [Test]public void NonfiniteAndNonStringKeyRefuse(){Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflight(new GdDict{{"value",double.NaN}},new GdDict(),out _,out _,out _));Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflight(new GdDict{{true,true}},new GdDict(),out _,out _,out _));}
        [Test]public void CyclicCallerGraphRefusesAtDepthBound(){var run=new GdDict();run["self"]=run;Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflight(run,new GdDict(),out _,out _,out string reason));Assert.AreEqual("whole_snapshot_depth",reason);}
        [Test]public void SignedZeroAndInt64RemainOriginalTypedValues(){var run=new GdDict{{"zero",System.BitConverter.Int64BitsToDouble(long.MinValue)},{"counter",long.MaxValue}};Assert.IsTrue(ContinuousWholeSnapshotBudget.TryPreflight(run,new GdDict(),out _,out _,out _));Assert.AreEqual(long.MinValue,System.BitConverter.DoubleToInt64Bits((double)run.Get("zero")));Assert.AreEqual(long.MaxValue,run.Get("counter"));}
        [Test]public void ExactNodeLimitAndOneBeyondAreIndependentOfBytes()
        {
            var array=new GdArray();for(int i=0;i<99997;i++)array.Add(null);var envelope=new GdDict{{"array",array}};
            Assert.IsTrue(ContinuousWholeSnapshotBudget.TryPreflightEnvelope(envelope,out long nodes,out _,out _));Assert.AreEqual(100000,nodes);
            array.Add(null);Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflightEnvelope(envelope,out _,out _,out string reason));Assert.AreEqual("whole_snapshot_nodes",reason);
        }
        [Test]public void ExactDepth128And129Refusal()
        {
            var envelope=new GdDict();var current=envelope;for(int i=0;i<127;i++){var child=new GdDict();current["child"]=child;current=child;}current["value"]=1L;
            Assert.IsTrue(ContinuousWholeSnapshotBudget.TryPreflightEnvelope(envelope,out _,out _,out _));
            current["value"]=new GdDict{{"value",1L}};Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflightEnvelope(envelope,out _,out _,out string reason));Assert.AreEqual("whole_snapshot_depth",reason);
        }
        [Test]public void SemanticProjectionPreservesEveryRepeatedEnvelopeMetadataOccurrence()
        {
            var package=new GdDict{{"value",1L}};var paid=new GdDict{{"schema_version",3L},{"save_mode",PaidSnapshotCodec.DiagnosticMode}};
            var semantic=ContinuousSnapshotAdmission.SemanticPaidEnvelope(paid,package);
            var opaque=new GdDict{{"wire","opaque"}};var original=new GdDict{{"a",opaque},{"b",opaque}};
            // Independent literal tree: do not reuse the production projection helper.
            var expectedPaid=new GdDict{{"schema_version",3L},{"save_mode",PaidSnapshotCodec.DiagnosticMode},
                {"domain",new GdDict{{"schema","component_paid_package_v1"},
                    {"codec",new GdDict{{"schema","component_domain_codec_v2"},{"value",package}}}}}};
            var expected=new GdDict{{"a",expectedPaid},{"b",expectedPaid}};
            Assert.IsTrue(ContinuousWholeSnapshotBudget.TryPreflightEnvelope(expected,out long expectedNodes,out long expectedBytes,out _));
            Assert.IsTrue(ContinuousWholeSnapshotBudget.TryPreflightOwnedProjection(original,new System.Collections.Generic.Dictionary<GdDict,GdDict>{{opaque,semantic}},out long actualNodes,out long actualBytes,out _));
            Assert.AreEqual(37,expectedNodes);Assert.AreEqual(expectedNodes,actualNodes);Assert.AreEqual(expectedBytes,actualBytes);
        }
        [Test]public void AdditionalMetadataUsesAlreadyChargedBudgetWithoutReset()
        {
            var additional=new GdDict{{"artifact",1L}};
            Assert.IsTrue(ContinuousWholeSnapshotBudget.TryPreflightAdditional(additional,99997,100,out long nodes,out _,out _));Assert.AreEqual(100000,nodes);
            Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflightAdditional(additional,99998,100,out _,out _,out string reason));Assert.AreEqual("whole_snapshot_nodes",reason);
            Assert.IsFalse(ContinuousWholeSnapshotBudget.TryPreflightAdditional(additional,0,ContinuousWholeSnapshotBudget.MaximumJsonUpperBytes,out _,out _,out reason));StringAssert.StartsWith("whole_snapshot_bytes",reason);
        }
    }
}
