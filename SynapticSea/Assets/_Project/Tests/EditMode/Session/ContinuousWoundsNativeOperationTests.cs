using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public class ContinuousWoundsNativeOperationTests
    {
        [Test] public void NativeOperationContinuesBeyondProjectionRowCapacityWithoutCloneOrReset()
        {
            var actual=new WoundState();actual.Configure(new GdDict());
            for(int i=0;i<=ContinuousWoundsValues.MaximumRows;i++)actual.ApplyWound(new GdDict{{"severity",.2},{"kind",WoundState.KIND_LACERATION},{"body_part",WoundState.BODY_TORSO}});
            var root=actual.Wounds;var first=root[0];long next=actual.ReadContinuousNextId();
            Assert.Throws<System.ArgumentException>(()=>ContinuousWoundsValues.CaptureExact(actual));
            var operation=ContinuousWoundsNativeOperation.PrepareTickHeal(1,.09,.02);Assert.IsTrue(operation.IsIssued);
            operation.ApplyToActual(actual,out var healed,out var id);
            Assert.AreSame(root,actual.Wounds);Assert.AreSame(first,root[0]);Assert.AreEqual(ContinuousWoundsValues.MaximumRows+1,root.Count);
            Assert.AreEqual(1,((GdDict)root[0]).GetFloat("age_seconds"));Assert.AreEqual(next,actual.ReadContinuousNextId());Assert.AreEqual(0,healed);Assert.AreEqual("",id);
        }
    }
}
