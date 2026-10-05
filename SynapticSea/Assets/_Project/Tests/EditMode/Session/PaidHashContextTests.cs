using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    public sealed class PaidHashContextTests : PaidCraftFixture
    {
        // Fresh zero-receipt diagnostic owner assembly, not a historical owner migration.
        GdDict Fresh(long feature, PaidHashContext context)
        {
            var s=Boot(manualStudy: feature>=4);
            var owner=Capture(s);
            Assert.IsTrue(owner.GetDictOrEmpty("receipts").IsEmpty);
            var paid=PaidCraftingState.State(owner);
            Assert.IsTrue(paid.GetDictOrEmpty("jobs").IsEmpty);
            paid["reward_history"]=PaidCraftRewardProof.NewHistory();
            PaidCraftRewardProof.RefreshCurrent(paid,owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("training"),context);
            if(ReferenceEquals(context,PaidHashContext.BitsV2))
            { owner["schema_version"]=6L;owner["feature_schema"]=feature;owner["hash_algorithm"]=context.Algorithm; }
            return owner;
        }
        [TestCase(3)] [TestCase(4)]
        public void FreshV2OwnerPublishesImmutableContextAndRefusesBindingChanges(long feature)
        {
            var owner=Fresh(feature,PaidHashContext.BitsV2);
            Assert.IsTrue(DomainBundle.TryCreate(owner,out var bundle,out var reason),reason);
            Assert.AreSame(PaidHashContext.BitsV2,bundle.HashContext);Assert.AreEqual(feature,bundle.FeatureSchema);
            var detached=bundle.GetSummary();detached["hash_algorithm"]=PaidHashContext.Legacy.Algorithm;
            Assert.AreSame(PaidHashContext.BitsV2,bundle.HashContext);
            Assert.IsFalse(DomainBundle.TryCreate(detached,out _,out _));
            var coordinator=new DomainTransactionCoordinator(owner);
            var legacy=Fresh(feature,PaidHashContext.Legacy);legacy["revision"]=1L;
            Assert.IsFalse(coordinator.ApplySummary(legacy),"an existing owner cannot reinterpret its receipt authority");
            Assert.AreSame(PaidHashContext.BitsV2,coordinator.HashContext);
            Assert.IsFalse(DomainBundle.TryCreate(With(owner,"feature_schema",2L),out _,out _));
            Assert.IsFalse(DomainBundle.TryCreate(With(owner,"feature_schema",3.0),out _,out _));
            Assert.IsFalse(DomainBundle.TryCreate(With(owner,"hash_algorithm","unknown"),out _,out _));
            var missing=owner.DeepCopy();missing.Erase("hash_algorithm");Assert.IsFalse(DomainBundle.TryCreate(missing,out _,out _));
            var extra=owner.DeepCopy();extra["unexpected"]=true;Assert.IsFalse(DomainBundle.TryCreate(extra,out _,out _));
        }
        static GdDict With(GdDict owner,string key,object value) { var copy=owner.DeepCopy();copy[key]=value;return copy; }
        [Test]
        public void LegacyOwnerRetainsExactFieldSetAndLegacyHashAuthority()
        {
            var legacy=Fresh(3,PaidHashContext.Legacy);
            Assert.IsTrue(DomainBundle.TryCreate(legacy,out var bundle,out var reason),reason);
            Assert.AreSame(PaidHashContext.Legacy,bundle.HashContext);Assert.AreEqual(3,bundle.FeatureSchema);
            Assert.AreEqual(PaidCraftingState.Hash(legacy),bundle.HashContext.Hash(legacy));
            Assert.IsFalse(DomainBundle.TryCreate(With(legacy,"hash_algorithm",PaidHashContext.Legacy.Algorithm),out _,out _));
            Assert.IsFalse(DomainBundle.TryCreate(With(legacy,"feature_schema",3L),out _,out _));
            for(long schema=1;schema<=5;schema++)
            {
                var metadata=new GdDict {{"schema_version",schema}};
                Assert.IsTrue(PaidHashContext.TryFromOwner(metadata,out var context,out _));Assert.AreSame(PaidHashContext.Legacy,context);
                metadata["hash_algorithm"]=PaidHashContext.BitsV2.Algorithm;
                Assert.IsFalse(PaidHashContext.TryFromOwner(metadata,out _,out _));
            }
        }
        [Test]
        public void V2EqualityAndHistoryIdsPreserveTypedRealBitsAndUseOnlyDeclaredAlgorithm()
        {
            double negativeZero=BitConverter.Int64BitsToDouble(long.MinValue);
            Assert.IsTrue(PaidHashContext.Legacy.Equal(0.0,negativeZero));
            Assert.IsFalse(PaidHashContext.BitsV2.Equal(0.0,negativeZero));
            Assert.IsFalse(PaidHashContext.BitsV2.Equal(1L,1.0));
            var training=new GdDict {{"log",GdArray.Of(new GdDict {{"seconds",BitConverter.Int64BitsToDouble(0x3fa81f8b6a300d00L)}})},
                {"dropped",0L},{"xp_total",0L},{"event_count",1L}};
            var v1=new GdDict {{"reward_history",PaidCraftRewardProof.NewHistory()}};
            var v2=new GdDict {{"reward_history",PaidCraftRewardProof.NewHistory()}};
            PaidCraftRewardProof.RefreshCurrent(v1,training);
            PaidCraftRewardProof.RefreshCurrent(v2,training,PaidHashContext.BitsV2);
            var h1=v1.GetDictOrEmpty("reward_history");var h2=v2.GetDictOrEmpty("reward_history");
            string k1=(string)h1.GetDictOrEmpty("training_nodes").Keys.Single();string k2=(string)h2.GetDictOrEmpty("training_nodes").Keys.Single();
            Assert.AreNotEqual(k1,k2);
            Assert.AreEqual(PaidHashContext.Legacy.Hash(h1.GetDictOrEmpty("training_nodes")[k1]),k1);
            Assert.AreEqual(PaidHashContext.BitsV2.Hash(h2.GetDictOrEmpty("training_nodes")[k2]),k2);
            Assert.AreEqual(k2,h2.GetDictOrEmpty("current_training_ref").GetString("tip_hash"));
            var owner=Fresh(4,PaidHashContext.BitsV2);var progression=owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression");
            string hash=ManualStudyState.Intern(owner,progression);
            Assert.AreEqual(PaidHashContext.BitsV2.Hash(new GdDict {{"schema_version",1L},{"summary",progression.DeepCopy()}}),hash);
            var state=new GdDict {{"run_id","run"},{"actor_id","actor"}};
            Assert.AreNotEqual(ManualStudyState.CompletionId(state,"book"),ManualStudyState.CompletionId(state,"book",PaidHashContext.BitsV2));
            Assert.AreNotEqual(AuxiliaryServiceState.CompletionId(state,"service"),AuxiliaryServiceState.CompletionId(state,"service",PaidHashContext.BitsV2));
        }
    }
}
