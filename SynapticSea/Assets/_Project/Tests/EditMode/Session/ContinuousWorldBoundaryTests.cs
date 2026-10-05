using System;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using SynapticSea.Core.Session;
namespace SynapticSea.Tests.Session
{
    public class ContinuousWorldBoundaryTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static RunSession Fresh() => new RunSession(new RunSessionDeps {
            EnableContinuousAuxiliaryDiagnostic=true, EnableComponentIntegration=true,
            EnablePaidCrafting=true, EnableBitExactPaidCompatibility=true, EnableAuxiliaryServices=true });
        static IDisposable Begin(RunSession s) => (IDisposable)typeof(RunSession).GetMethod("BeginContinuousWorldMutationBatch",Private).Invoke(s,null);
        static void Complete(IDisposable scope) => scope.GetType().GetMethod("Complete",Private).Invoke(scope,null);
        [Test] public void ObserverIntentWaitsForOutermostNotificationsAndExactCompletion()
        {
            var s=Fresh(); bool queued=false;
            using(var outer=Begin(s)) {
                s.WorldTime+=1;
                Action observer=()=> { queued=true; using(var nested=Begin(s)) {
                    Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _)); Complete(nested);
                } Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _)); };
                observer(); Assert.IsTrue(queued); Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _));
                Complete(outer);
            }
            Assert.IsTrue(s.TryReadContinuousSafeEndTick(out var ticket));
            Assert.IsFalse(ticket.IsCompleteWorldAuthority);
        }
        [Test] public void FaultedNestedNotificationPreventsTicketButNextTickCanRecover()
        {
            var s=Fresh(); using(var outer=Begin(s)) {
                using(var nested=Begin(s)) { s.WorldTime+=1; }
                Complete(outer);
            }
            Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _));
            using(var next=Begin(s)) { s.WorldTime+=1; Complete(next); }
            Assert.IsTrue(s.TryReadContinuousSafeEndTick(out _));
        }
        [Test] public void NextBatchAndClockDriftInvalidateTicketAndForeignThreadCannotObserve()
        {
            var s=Fresh(); using(var first=Begin(s)) Complete(first);
            Assert.IsTrue(s.TryReadContinuousSafeEndTick(out var old));
            bool foreign=true;var thread=new Thread(()=>foreign=s.TryReadContinuousSafeEndTick(out _));thread.Start();thread.Join();
            Assert.IsFalse(foreign);
            s.WorldTime+=1;Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _));
            using(var next=Begin(s)) { Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _)); Complete(next); }
            Assert.IsTrue(s.TryReadContinuousSafeEndTick(out var current));Assert.AreNotSame(old,current);
        }
        [Test] public void EpochExhaustionRevokesCaptureWithoutFreezingActualTick()
        {
            var s=Fresh(); typeof(RunSession).GetField("_continuousBoundaryEpoch",Private).SetValue(s,ulong.MaxValue);
            using(var scope=Begin(s)) { s.Tick(new TickContext{Delta=1,HasPlayer=false});Complete(scope); }
            Assert.AreEqual(1,s.WorldTime);Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _));
            using(var scope=Begin(s)) { s.Tick(new TickContext{Delta=1,HasPlayer=false});Complete(scope); }
            Assert.AreEqual(2,s.WorldTime);Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _));
        }
        [Test] public void OutOfOrderCompleteRefusesBeforeClosingEitherScope()
        {
            var s=Fresh();using(var outer=Begin(s))using(var inner=Begin(s)) {
                var error=Assert.Throws<TargetInvocationException>(()=>Complete(outer));
                Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
                Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _));Complete(inner);Complete(outer);
            }
            Assert.IsTrue(s.TryReadContinuousSafeEndTick(out _));
        }
    }
}
