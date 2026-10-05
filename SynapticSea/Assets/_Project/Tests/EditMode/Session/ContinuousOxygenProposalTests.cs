using System;
using NUnit.Framework;
using SynapticSea.Core.Systems;
namespace SynapticSea.Tests.Session
{
    public class ContinuousOxygenProposalTests
    {
        [Test] public void ProposalBindsExactBeforeAndStockTickAfterAndRefusesForeignIssuer()
        {
            var model=new OxygenState();var before=ContinuousOxygenValues.CaptureExact(model);
            var input=new ContinuousOxygenTickInput(false,true,1.4,.2);
            var proposal=ContinuousOxygenEvaluator.EvaluateTick(before,.3,input);
            Assert.AreSame(before,proposal.Before);
            var expected=ContinuousOxygenEvaluator.Tick(before,.3,input);
            Assert.AreEqual(expected.Oxygen,proposal.After.Oxygen);Assert.AreEqual(expected.EffectiveDrainRate,proposal.After.EffectiveDrainRate);
            Assert.Throws<InvalidOperationException>(()=>new ContinuousOxygenProposal(new object(),before,expected));
            Assert.AreEqual(before.Oxygen,model.Oxygen);
        }
    }
}
