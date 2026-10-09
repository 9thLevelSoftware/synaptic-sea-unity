using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;

namespace SynapticSea.Tests.Systems.Infra
{
    /// <summary>
    /// The New Run smoke automation (<c>tools/mac/smoke.sh --new-run</c>) is test tooling that must be inert for ordinary launches:
    /// it activates only in a "dev" build whose command line carries the flag and a seed the launch contract accepts.
    /// </summary>
    public class SmokeNewRunTests
    {
        static readonly string[] Plain = { "TheSynapticSea", "-batchmode", "-nographics" };

        [Test]
        public void OrdinaryLaunchesNeverTriggerIt()
        {
            Assert.IsFalse(SmokeNewRun.TryGetSeed(Plain, "dev", out _), "no flag");
            Assert.IsFalse(SmokeNewRun.TryGetSeed(null, "dev", out _), "no arguments at all");
            Assert.IsFalse(SmokeNewRun.TryGetSeed(new string[0], "dev", out _), "empty arguments");
        }

        [Test]
        public void ADevBuildWithAValidSeedTriggersIt()
        {
            Assert.IsTrue(SmokeNewRun.TryGetSeed(new[] { "app", SmokeNewRun.Flag, "4242" }, "dev", out long seed));
            Assert.AreEqual(4242L, seed);
            Assert.IsTrue(SmokeNewRun.TryGetSeed(new[] { "app", "-batchmode", SmokeNewRun.Flag, "17", "-nographics" }, "dev", out seed));
            Assert.AreEqual(17L, seed);
        }

        [TestCase("demo")]
        [TestCase("release")]
        [TestCase("")]
        [TestCase("Dev")]
        public void ItIsInertOutsideADevBuild(string buildKind)
        {
            Assert.IsFalse(SmokeNewRun.TryGetSeed(new[] { "app", SmokeNewRun.Flag, "4242" }, buildKind, out long seed));
            Assert.AreEqual(0L, seed, "no seed is reported when inert");
        }

        [TestCase("0")]
        [TestCase("-5")]
        [TestCase("abc")]
        [TestCase("")]
        [TestCase("2147483648")]
        public void ASeedTheLaunchContractRejectsDoesNotTriggerIt(string value)
        {
            Assert.IsFalse(SmokeNewRun.TryGetSeed(new[] { "app", SmokeNewRun.Flag, value }, "dev", out _));
        }

        [Test]
        public void TheFlagWithoutAValueDoesNotTriggerIt()
        {
            Assert.IsFalse(SmokeNewRun.TryGetSeed(new[] { "app", SmokeNewRun.Flag }, "dev", out _));
        }

        [Test]
        public void TheAcceptedRangeMatchesTheLaunchContract()
        {
            Assert.IsTrue(SmokeNewRun.TryGetSeed(new[] { SmokeNewRun.Flag, MilestoneALaunch.MinSeed.ToString() }, "dev", out _));
            Assert.IsTrue(SmokeNewRun.TryGetSeed(new[] { SmokeNewRun.Flag, MilestoneALaunch.MaxSeed.ToString() }, "dev", out _));
        }
    }
}
