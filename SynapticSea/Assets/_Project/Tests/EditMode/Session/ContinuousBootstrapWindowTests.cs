using NUnit.Framework;
using SynapticSea.Core.Session;
namespace SynapticSea.Tests.Session
{
    public class ContinuousBootstrapWindowTests
    {
        static RunSessionDeps Requested() => new RunSessionDeps {
            EnableContinuousAuxiliaryDiagnostic=true, EnableComponentIntegration=true,
            EnablePaidCrafting=true, EnableBitExactPaidCompatibility=true, EnableAuxiliaryServices=true };
        [Test] public void DefaultAndLaterMutableRequestCannotOpenConstructionWindow()
        {
            var deps=new RunSessionDeps();var session=new RunSession(deps);
            Assert.IsFalse(session.ContinuousDiagnosticBootstrapOpen);
            deps.EnableContinuousAuxiliaryDiagnostic=true;deps.EnableComponentIntegration=true;
            deps.EnablePaidCrafting=true;deps.EnableBitExactPaidCompatibility=true;deps.EnableAuxiliaryServices=true;
            Assert.IsFalse(session.ContinuousDiagnosticBootstrapOpen);
        }
        [Test] public void MissingRequiredProfileCannotOpenWindowLater()
        {
            var deps=Requested();deps.EnableBitExactPaidCompatibility=false;var session=new RunSession(deps);
            Assert.IsFalse(session.ContinuousDiagnosticBootstrapOpen);
            deps.EnableBitExactPaidCompatibility=true;Assert.IsFalse(session.ContinuousDiagnosticBootstrapOpen);
        }
        [Test] public void RestoredWorldTimeDoesNotPretendFirstTickAlreadyHappened()
        {
            var session=new RunSession(Requested());session.WorldTime=1234;
            Assert.IsTrue(session.ContinuousDiagnosticBootstrapOpen);
            session.CloseContinuousDiagnosticBootstrapForTick();Assert.IsFalse(session.ContinuousDiagnosticBootstrapOpen);
            session.WorldTime=0;Assert.IsFalse(session.ContinuousDiagnosticBootstrapOpen);
        }
        [Test] public void ActualTickClosesWindowPermanentlyEvenWithZeroDelta()
        {
            var session=new RunSession(Requested());Assert.IsTrue(session.ContinuousDiagnosticBootstrapOpen);
            session.Tick(new TickContext{Delta=0,HasPlayer=false});
            Assert.IsFalse(session.ContinuousDiagnosticBootstrapOpen);
        }
    }
}
