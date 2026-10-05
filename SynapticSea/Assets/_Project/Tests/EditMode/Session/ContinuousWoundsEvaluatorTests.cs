using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public class ContinuousWoundsEvaluatorTests
    {
        [Test] public void TickThenHealMatchesOrdinaryOrderedArithmeticWithoutMutatingSource()
        {
            var actual=new WoundState();actual.Configure(new GdDict());
            string id=actual.ApplyWound(new GdDict{{"kind",WoundState.KIND_PUNCTURE},{"body_part",WoundState.BODY_ARM},{"severity",.37}});
            actual.Bandage(id);var before=ContinuousWoundsValues.CaptureExact(actual);
            var ordinary=before.ExactScratch();ordinary.Tick(.123);long closed=ordinary.Heal(.123,.09,.02);
            var proposal=ContinuousWoundsEvaluator.TickHeal(before,.123,.09,.02);
            Assert.IsTrue(before.MatchesRaw(actual));Assert.IsTrue(proposal.After.MatchesRaw(ordinary));Assert.AreEqual(closed,proposal.Healed);
        }
        [Test] public void DamageCreateAndMergeMatchOrdinaryRowsAndPrivateNextId()
        {
            var actual=new WoundState();actual.Configure(new GdDict());var before=ContinuousWoundsValues.CaptureExact(actual);
            var suggestion=WoundState.SuggestFromDamage(25,"heat",WoundState.BODY_LEG);suggestion["source_id"]="threat";
            string id=actual.ApplyOrWorsenWound(suggestion);
            var first=ContinuousWoundsEvaluator.Damage(before,25,"heat",WoundState.BODY_LEG,"threat");
            Assert.AreEqual(id,first.WoundId);Assert.IsTrue(first.After.MatchesRaw(actual));Assert.AreEqual(0,before.Count);
            actual.ApplyOrWorsenWound(suggestion);
            var second=ContinuousWoundsEvaluator.Damage(first.After,25,"heat",WoundState.BODY_LEG,"threat");
            Assert.IsTrue(second.After.MatchesRaw(actual));Assert.AreEqual(first.After.NextId,second.After.NextId);Assert.AreEqual(1,second.After.Count);
        }
    }
}
