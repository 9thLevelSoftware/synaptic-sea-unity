using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    [NonParallelizable]
    public sealed class TrackedParticipantStateTests
    {
        static TrackedParticipantOwner Owner() => new TrackedParticipantOwner();
        static void SetStamp(TrackedParticipantOwner owner, ulong value)
            => typeof(TrackedParticipantOwner).GetField("_stamp",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,value);
        static ClassDefinition Cook() => new ClassDefinition { ClassId="cook", StartingSkills=new GdDict{{"cooking",1L}}, XpMultipliers=new GdDict{{"technical",1.25}} };
        static GdDict Skills() => new GdDict{{"cooking",new GdDict{{"category","technical"}}}};
        static GdDict Definitions() => new GdDict{{"part",new GdDict{{"category","part"},{"weight",2.0},{"max_stack",99L},{"effect",new GdDict{{"value",1.0}}}}}};
        [Test]
        public void DictionaryMutatorsStampAcceptedWritesAndPreserveFailedOperations()
        {
            var owner=Owner();var root=owner.NewDict(true);ulong before=owner.Stamp;
            root["a"]=1L;Assert.Greater(owner.Stamp,before);before=owner.Stamp;
            root["a"]=1L;Assert.Greater(owner.Stamp,before);before=owner.Stamp;
            Assert.Throws<ArgumentException>(()=>root.Add("a",2L));Assert.AreEqual(before,owner.Stamp);
            Assert.False(root.Erase("missing"));Assert.AreEqual(before,owner.Stamp);
            root.Merge(new GdDict{{"b",2L}});Assert.Greater(owner.Stamp,before);before=owner.Stamp;
            root.Erase("a");root["a"]=1L;Assert.AreEqual("b",root.Keys[0]);Assert.Greater(owner.Stamp,before);
            root.Clear();before=owner.Stamp;root.Clear();Assert.Greater(owner.Stamp,before);
        }
        [Test]
        public void EveryArrayMutatorParticipatesAndFailedRemovalDoesNot()
        {
            var owner=Owner();var root=owner.NewArray(true);
            Action[] edits={ ()=>root.Add(1L),()=>root.Append(2L),()=>root.Insert(1,3L),()=>root[0]=1L,()=>root.AppendArray(new GdArray(new object[]{4L})),()=>root.RemoveAt(1),()=>root.Remove(2L),()=>root.PopBack(),()=>root.PopFront(),()=>root.Clear(),()=>root.Clear() };
            foreach(var edit in edits){ulong before=owner.Stamp;edit();Assert.Greater(owner.Stamp,before);}
            ulong last=owner.Stamp;Assert.False(root.Remove(99L));Assert.IsNull(root.PopBack());Assert.IsNull(root.PopFront());Assert.AreEqual(last,owner.Stamp);
        }
        [Test]
        public void SameOwnerNestedAliasesRemainTrackedUntilLastPathRemoved()
        {
            var owner=Owner();var root=owner.NewDict(true);var child=owner.NewDict();var array=owner.NewArray();array.Add(child);
            root["one"]=array;root["two"]=child;root.Erase("one");ulong before=owner.Stamp;
            child["x"]=1L;Assert.Greater(owner.Stamp,before);root.Erase("two");before=owner.Stamp;
            child["x"]=2L;Assert.AreEqual(before,owner.Stamp);
            root["again"]=array;before=owner.Stamp;child["x"]=3L;Assert.Greater(owner.Stamp,before);
        }
        [Test]
        public void UnsafeChildrenKeysCyclesAndCrossOwnerSharingRefuseWithoutMutation()
        {
            var owner=Owner();var root=owner.NewDict(true);var other=Owner().NewDict(true);ulong before=owner.Stamp;
            Assert.Throws<InvalidOperationException>(()=>root["foreign"]=other);
            Assert.Throws<InvalidOperationException>(()=>root["raw"]=new GdDict());
            Assert.Throws<InvalidOperationException>(()=>root["self"]=root);
            Assert.Throws<ArgumentException>(()=>root[new GdDict()]=1L);
            Assert.AreEqual(before,owner.Stamp);Assert.AreEqual(0,root.Count);
            var child=owner.NewArray();root["child"]=child;before=owner.Stamp;
            Assert.Throws<InvalidOperationException>(()=>child.Add(root));Assert.AreEqual(before,owner.Stamp);Assert.AreEqual(0,child.Count);
        }
        sealed class HostileEnumerable : IEnumerable
        {
            internal bool Called;
            public IEnumerator GetEnumerator(){Called=true;throw new Exception("must_not_enumerate");}
        }
        [Test]
        public void ProtectedCallerNormalizationNeverExecutesEnumerableCode()
        {
            var owner=Owner();var root=owner.NewDict(true);var array=owner.NewArray(true);var hostile=new HostileEnumerable();
            Assert.Throws<ArgumentException>(()=>root.Get(hostile));Assert.Throws<ArgumentException>(()=>root.Has(hostile));
            Assert.Throws<ArgumentException>(()=>root.Erase(hostile));Assert.Throws<ArgumentException>(()=>root["x"]=hostile);
            Assert.Throws<ArgumentException>(()=>array.IndexOf(hostile));Assert.Throws<ArgumentException>(()=>array.Remove(hostile));
            Assert.False(hostile.Called);
        }
        [Test]
        public void StableReadViewsAndDetachedCopiesCannotBypassTracking()
        {
            var owner=Owner();var root=owner.NewDict(true);var child=owner.NewDict();root["child"]=child;
            IReadOnlyList<object> keys=root.Keys,values=root.Values;
            Assert.False(keys is List<object>);Assert.False(values is IList<object>);
            var deep=root.DeepCopy();ulong before=owner.Stamp;((GdDict)deep["child"])["x"]=1L;Assert.AreEqual(before,owner.Stamp);
            var shallow=root.ShallowCopy();((GdDict)shallow["child"])["x"]=2L;Assert.Greater(owner.Stamp,before);
            before=owner.Stamp;var prepared=owner.PrepareReplacements(new object[]{root},new object[]{new GdDict{{"new",3L}}},before);
            using(var attempt=CommonParticipantGate.BeginAttempt())prepared.InstallUnderGate(attempt);
            Assert.AreSame(keys,root.Keys);Assert.AreSame(values,root.Values);Assert.AreEqual("new",keys[0]);Assert.AreEqual(3L,values[0]);
        }
        [Test]
        public void PreparedPublicationIsSingleUseFreshPrivateAndRollbackOnlyInSameAttempt()
        {
            var owner=Owner();var root=owner.NewDict(true);var old=owner.NewDict();root["old"]=old;
            var source=new GdDict{{"fresh",new GdDict{{"n",1L}}}};var prepared=owner.PrepareReplacements(new object[]{root},new object[]{source},owner.Stamp);
            ((GdDict)source["fresh"])["n"]=9L;
            using(var attempt=CommonParticipantGate.BeginAttempt())
            {
                prepared.InstallUnderGate(attempt);Assert.AreEqual(1L,((GdDict)root["fresh"])["n"]);
                prepared.RollbackUnderGate(attempt);Assert.AreSame(old,root["old"]);
                Assert.Throws<InvalidOperationException>(()=>prepared.InstallUnderGate(attempt));
            }
            var late=owner.PrepareReplacements(new object[]{root},new object[]{source},owner.Stamp);
            ParticipantPublicationAttempt original;
            using(original=CommonParticipantGate.BeginAttempt())late.InstallUnderGate(original);
            using(var different=CommonParticipantGate.BeginAttempt())
            {Assert.Throws<InvalidOperationException>(()=>late.RollbackUnderGate(original));Assert.Throws<InvalidOperationException>(()=>late.RollbackUnderGate(different));}
        }
        [Test]
        public void PrepareThenMutationRefusesInstallAndRollbackCannotOverwriteLaterEvolution()
        {
            var owner=Owner();var root=owner.NewDict(true);root["x"]=1L;
            var prepared=owner.PrepareReplacements(new object[]{root},new object[]{new GdDict{{"x",2L}}},owner.Stamp);root["x"]=3L;
            using(var attempt=CommonParticipantGate.BeginAttempt())Assert.Throws<InvalidOperationException>(()=>prepared.InstallUnderGate(attempt));
            Assert.AreEqual(3L,root["x"]);
            var next=owner.PrepareReplacements(new object[]{root},new object[]{new GdDict{{"x",4L}}},owner.Stamp);
            using(var attempt=CommonParticipantGate.BeginAttempt())
            {next.InstallUnderGate(attempt);root["x"]=5L;Assert.Throws<InvalidOperationException>(()=>next.RollbackUnderGate(attempt));}
            Assert.AreEqual(5L,root["x"]);
        }
        [Test]
        public void OverflowRefusesBeforeBackingMutationAndPreparedPairReservesRollback()
        {
            var owner=Owner();var root=owner.NewDict(true);root["x"]=1L;SetStamp(owner,ulong.MaxValue);
            Assert.Throws<InvalidOperationException>(()=>root["x"]=2L);Assert.AreEqual(1L,root["x"]);Assert.True(owner.IsPoisoned);
            var second=Owner();var other=second.NewDict(true);SetStamp(second,ulong.MaxValue-1);
            Assert.Throws<InvalidOperationException>(()=>second.PrepareReplacements(new object[]{other},new object[]{new GdDict()},second.Stamp));Assert.AreEqual(0,other.Count);
        }
        [Test]
        public void SortComparatorRunsOutsideGateAndReentrantMutationInvalidatesCandidate()
        {
            var owner=Owner();var root=owner.NewArray(true);root.Add(2L);root.Add(1L);bool ran=false;
            root.SortCustom((a,b)=>{Assert.False(Monitor.IsEntered(CommonParticipantGate.SyncRoot));ran=true;return (long)a<(long)b;});Assert.True(ran);Assert.AreEqual(1L,root[0]);
            bool changed=false;Assert.Throws<InvalidOperationException>(()=>root.SortCustom((a,b)=>{if(!changed){changed=true;root.Add(3L);}return (long)a<(long)b;}));Assert.AreEqual(3,root.Count);
        }
        [Test]
        public void RootedDepthBoundsAreCheckedFromEveryRootAndImportsRejectCycles()
        {
            var owner=Owner();var root=owner.NewDict(true);var at=root;
            for(int i=0;i<TrackedParticipantOwner.MaximumDepth;i++){var next=owner.NewDict();at["n"]=next;at=next;}
            ulong before=owner.Stamp;var last=owner.NewDict();Assert.Throws<InvalidOperationException>(()=>at["tooDeep"]=last);Assert.AreEqual(before,owner.Stamp);
            var legacy=new GdDict();legacy["self"]=legacy;Assert.Throws<InvalidOperationException>(()=>owner.ImportDict(legacy));
        }
        [Test]
        public void SharedDiamondSubtreeDepthIsCheckedOnLongerAliasedPathAndImport()
        {
            var owner=Owner();var root=owner.NewDict(true);var subtree=owner.NewDict();var at=subtree;
            for(int i=0;i<80;i++){var next=owner.NewDict();at["n"]=next;at=next;}
            var prefix=owner.NewDict();at=prefix;
            for(int i=0;i<60;i++){var next=owner.NewDict();at["n"]=next;at=next;}
            at["shared"]=subtree;root["short"]=subtree;ulong before=owner.Stamp;
            Assert.Throws<InvalidOperationException>(()=>root["long"]=prefix);Assert.AreEqual(before,owner.Stamp);Assert.False(root.Has("long"));
            var rawRoot=new GdDict();var rawSubtree=new GdDict();var rawAt=rawSubtree;
            for(int i=0;i<80;i++){var next=new GdDict();rawAt["n"]=next;rawAt=next;}
            var rawPrefix=new GdDict();rawAt=rawPrefix;
            for(int i=0;i<60;i++){var next=new GdDict();rawAt["n"]=next;rawAt=next;}
            rawAt["shared"]=rawSubtree;rawRoot["short"]=rawSubtree;rawRoot["long"]=rawPrefix;
            Assert.Throws<InvalidOperationException>(()=>owner.ImportDict(rawRoot));Assert.AreEqual(before,owner.Stamp);
        }
        [Test]
        public void InventoryFactoryClosesLegacyAliasesAndTracksDefinitionPolicyAndRootReplacement()
        {
            var source=Definitions();var inventory=InventoryState.CreateTracked(source);ulong before=inventory.CaptureTrackedStamp();
            source.GetDictOrEmpty("part")["weight"]=99.0;Assert.AreEqual(before,inventory.CaptureTrackedStamp());
            inventory.GetDefinition("part").GetDictOrEmpty("effect")["value"]=2.0;Assert.Greater(inventory.CaptureTrackedStamp(),before);
            var old=inventory.Items;var replacement=inventory.ImportTrackedDictionary(new GdDict{{"part",2L}});before=inventory.CaptureTrackedStamp();inventory.Items=replacement;Assert.AreSame(replacement,inventory.Items);Assert.Greater(inventory.CaptureTrackedStamp(),before);
            before=inventory.CaptureTrackedStamp();old["part"]=5L;Assert.AreEqual(before,inventory.CaptureTrackedStamp());
            Assert.Throws<InvalidOperationException>(()=>inventory.Items=new GdDict());Assert.AreSame(replacement,inventory.Items);
            inventory.BonusCapacity=3;Assert.Greater(inventory.CaptureTrackedStamp(),before);
        }
        [Test]
        public void InventoryPreparedSwapRetainsRootAndNotifiesOnlyAfterPublication()
        {
            var inventory=InventoryState.CreateTracked(Definitions());inventory.Items["part"]=3L;int events=0;
            inventory.ItemsRemoved+=(id,qty)=>{Assert.False(Monitor.IsEntered(CommonParticipantGate.SyncRoot));Assert.AreEqual("part",id);Assert.AreEqual(2,qty);events++;};
            var root=inventory.Items;var summary=inventory.CaptureTrackedSummary(out ulong stamp);summary.GetDictOrEmpty("items")["part"]=1L;
            var prepared=inventory.PrepareTrackedReplacement(summary,stamp);
            using(var attempt=CommonParticipantGate.BeginAttempt()){prepared.InstallUnderGate(attempt);Assert.AreEqual(0,events);}
            Assert.AreSame(root,inventory.Items);prepared.NotifyAfterPublication();Assert.AreEqual(1,events);
            Assert.Throws<InvalidOperationException>(()=>prepared.NotifyAfterPublication());
            inventory.ComponentMass=()=>throw new Exception("must_not_invoke");Assert.Throws<InvalidOperationException>(()=>inventory.CaptureTrackedSummary(out _));
        }
        [Test]
        public void ProgressionExactReplayTracksAllMapsAndPreservesLiveMultiplierSemantics()
        {
            var model=PlayerProgressionState.CreateTracked();var books=new GdDict{{"book",new GdDict{{"target_skill","cooking"},{"book_xp",1L}}}};model.Configure(Cook(),Skills(),books);
            ulong before=model.CaptureTrackedStamp();books.GetDictOrEmpty("book")["book_xp"]=99L;Assert.AreEqual(before,model.CaptureTrackedStamp());
            model.XpMultipliers["technical"]=1.75;var replay=model.CaptureTrackedReplay(out ulong stamp);Assert.AreEqual(1.75,replay.XpMultipliers["technical"]);
            replay.GrantXp("cooking",3);model.GrantXp("cooking",3);Assert.AreEqual(replay.SkillXp["cooking"],model.SkillXp["cooking"]);Assert.AreEqual(BitConverter.DoubleToInt64Bits((double)replay.SkillXpFractional["cooking"]),BitConverter.DoubleToInt64Bits((double)model.SkillXpFractional["cooking"]));
            foreach(var map in new[]{model.Skills,model.SkillXp,model.CrossTraining,model.BooksRead,model.SkillXpFractional}){before=model.CaptureTrackedStamp();map.Clear();Assert.Greater(model.CaptureTrackedStamp(),before);}
            before=model.CaptureTrackedStamp();model.ClassId=model.ClassId;Assert.Greater(model.CaptureTrackedStamp(),before);
        }
        [Test]
        public void LegacyConstructorsPreserveExposedViewsAndInputBookAliases()
        {
            var dict=new GdDict();Assert.True(dict.Keys is List<object>);Assert.True(dict.Values is List<object>);
            var model=new PlayerProgressionState();var books=new GdDict{{"book",new GdDict{{"target_skill","cooking"},{"book_xp",1L}}}};model.Configure(Cook(),Skills(),books);
            books.GetDictOrEmpty("book")["book_xp"]=7L;Assert.AreEqual(7L,model.GetBooksCatalog().GetDictOrEmpty("book")["book_xp"]);
            var inventory=new InventoryState(new GdDict());var raw=new GdDict();inventory.Items=raw;Assert.AreSame(raw,inventory.Items);
        }
    }
}
