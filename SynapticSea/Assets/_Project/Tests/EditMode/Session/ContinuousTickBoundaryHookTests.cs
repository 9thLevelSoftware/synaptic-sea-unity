using System;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public class ContinuousTickBoundaryHookTests
    {
        static RunSession Fresh(bool requested=true) => new RunSession(new RunSessionDeps {
            EnableContinuousAuxiliaryDiagnostic=requested, EnableComponentIntegration=true,
            EnablePaidCrafting=true, EnableBitExactPaidCompatibility=true, EnableAuxiliaryServices=true });
        [Test] public void ActualTickClosesPhaseAfterClocksAndNeverGrantsWholeWorldAuthority()
        {
            var s=Fresh();s.Tick(new TickContext{Delta=1,HasPlayer=false});
            Assert.AreEqual(1,s.WorldTime);Assert.IsTrue(s.TryReadContinuousSafeEndTick(out var first));
            Assert.IsFalse(first.IsCompleteWorldAuthority);
            s.Tick(new TickContext{Delta=2,HasPlayer=false});
            Assert.AreEqual(3,s.WorldTime);Assert.IsTrue(s.TryReadContinuousSafeEndTick(out var second));
            Assert.AreNotSame(first,second);
            lock(CommonParticipantGate.SyncRoot) Assert.IsFalse(first.MatchesUnderGate(s));
        }
        [Test] public void DefaultOffTickDoesNotIssueDiagnosticPhaseTicket()
        {var s=Fresh(false);s.Tick(new TickContext{Delta=1,HasPlayer=false});Assert.AreEqual(1,s.WorldTime);Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _));}
        [Test] public void ExistingRestoreEarlyReturnInvalidatesPreviousPhaseTicket()
        {
            var s=Fresh();s.Tick(new TickContext{Delta=1,HasPlayer=false});Assert.IsTrue(s.TryReadContinuousSafeEndTick(out _));
            typeof(RunSession).GetProperty("ComponentGenerationRestoreInProgress",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(s,true);s.Tick(new TickContext{Delta=1,HasPlayer=false});
            Assert.AreEqual(1,s.WorldTime);Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _));
        }
        [Test] public void ActualStageObserverFaultCannotIssueEndTicketOrRewindClock()
        {
            var s=Fresh();s.AwayFromStart=true;s.PlayableStarted=true;
            s.StageRan+=(id,location)=> {Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _));throw new InvalidOperationException("observer_fault");};
            Assert.Throws<InvalidOperationException>(()=>s.Tick(new TickContext{Delta=1,HasPlayer=false}));
            Assert.AreEqual(1,s.WorldTime);Assert.IsFalse(s.TryReadContinuousSafeEndTick(out _));
        }
    }
}
