using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public class ContinuousSnapshotFieldPlanTests
    {
        [Test] public void RealRunAndWorldSerializerFieldsAreExplicitlyClassifiedWithoutAdmission()
        {
            foreach(bool world in new[]{false,true})
            {
                GdDict snapshot=world?new WorldSnapshot().ToDict():new RunSnapshot().ToDict();
                Assert.IsTrue(ContinuousSnapshotFieldPlan.InspectBootstrapLayout(snapshot,world,out var plan,out string reason),reason);
                Assert.AreEqual(snapshot.Count,plan.Count);Assert.IsFalse(plan.TryBuildWholeWorldSave(out reason));Assert.AreEqual("continuous_world_producers_not_enrolled",reason);
                int i=0;foreach(var row in snapshot)Assert.AreEqual(row.Key,plan.Name(i++));
            }
        }
        [Test] public void UnknownFieldRefusesInsteadOfImplicitlySealingMutableGenesis()
        {
            var snapshot=new RunSnapshot().ToDict();snapshot["future_mutable_field"]=1L;
            Assert.IsFalse(ContinuousSnapshotFieldPlan.InspectBootstrapLayout(snapshot,false,out _,out string reason));Assert.AreEqual("unenrolled_snapshot_field",reason);
        }
        [Test] public void PlanDoesNotRetainCallerMutableLayoutDictionary()
        {
            var snapshot=new RunSnapshot().ToDict();Assert.IsTrue(ContinuousSnapshotFieldPlan.InspectBootstrapLayout(snapshot,false,out var plan,out _));int count=plan.Count;
            snapshot.Clear();Assert.AreEqual(count,plan.Count);Assert.IsFalse(plan.TryBuildWholeWorldSave(out _));
        }
    }
}
