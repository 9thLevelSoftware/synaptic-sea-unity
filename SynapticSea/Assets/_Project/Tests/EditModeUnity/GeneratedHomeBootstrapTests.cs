using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Game;
using SynapticSea.Runtime.Session;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    /// <summary>
    /// Phase 1.8: the generated-home boot seam in <see cref="PlayableBootstrap.PrepareDeps"/>. A <see cref="RunLaunchRequest.GeneratedHome"/>
    /// New Run generates the home from the seed and points the deps at <c>user://runs/&lt;id&gt;/</c>; every other launch (the title's New
    /// Run, direct open, tests) keeps the golden hub. The generated home is NOT yet guaranteed or safe (PRs D/E/F).
    /// </summary>
    public class GeneratedHomeBootstrapTests : UiTestBase
    {
        GameObject _go;
        PlayableBootstrap _boot;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("GeneratedHomeBootstrapTests");
            _boot = _go.AddComponent<PlayableBootstrap>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [Test]
        public void TheFlagIsOffForEveryExistingLaunchFactory()
        {
            Assert.IsFalse(new RunLaunchRequest().GeneratedHome);
            Assert.IsFalse(RunLaunchRequest.NewRun().GeneratedHome);
            Assert.IsFalse(RunLaunchRequest.NewRun(4711, "breach_field", "standard").GeneratedHome);
            Assert.IsFalse(RunLaunchRequest.GoldenShip().GeneratedHome);
            Assert.IsFalse(RunLaunchRequest.ContinueWorld().GeneratedHome);
            RunLaunchRequest generated = RunLaunchRequest.GeneratedHomeRun(4711);
            Assert.IsTrue(generated.GeneratedHome);
            Assert.AreEqual(RunLaunchMode.NewRun, generated.Mode);
            Assert.AreEqual(4711, generated.Seed);
            StringAssert.Contains("generatedHome", generated.ToString());
        }

        [Test]
        public void TheDefaultNewRunStaysOnTheGoldenHub()
        {
            RunSessionDeps deps = _boot.PrepareDeps(RunLaunchRequest.NewRun(4711, "breach_field", "standard"), out string failure);
            Assert.IsNotNull(deps, failure);
            Assert.AreEqual(MilestoneALaunch.HubLayoutPath, deps.LayoutPath);
            Assert.AreEqual(4711, deps.RunSeed);
            Assert.IsNull(_boot.GeneratedStart);
            Assert.AreEqual("", _boot.RunDirectory);
        }

        [Test]
        public void AGeneratedHomeIsWrittenToUserRunsAndTheDepsPointAtIt()
        {
            RunSessionDeps deps = _boot.PrepareDeps(RunLaunchRequest.GeneratedHomeRun(1234), out string failure);
            Assert.IsNotNull(deps, failure);
            Assert.IsNotNull(_boot.GeneratedStart, "the home ship was generated");
            StringAssert.StartsWith(PlayableBootstrap.RunsDir, _boot.RunDirectory);
            Assert.AreEqual(_boot.RunDirectory + "layout.json", deps.LayoutPath);
            Assert.AreEqual(_boot.RunDirectory + "gameplay_slice.json", deps.GameplaySlicePath);
            Assert.AreEqual(_boot.RunDirectory + "blueprint.json", deps.BlueprintPath);
            Assert.IsTrue(Storage.FileExists(deps.LayoutPath));
            Assert.IsTrue(Storage.FileExists(deps.GameplaySlicePath));
            Assert.IsTrue(Storage.FileExists(deps.BlueprintPath));
            Assert.AreEqual(_boot.GeneratedStart.Documents.LayoutJson, Storage.ReadText(deps.LayoutPath), "the written layout is the generated text");
            Assert.AreNotEqual(MilestoneALaunch.HubLayoutPath, deps.LayoutPath);
        }

        [Test]
        public void TheRunSeedStaysTheRequestedSeedAndTheHomeSeedLivesInTheBlueprint()
        {
            RunSessionDeps deps = _boot.PrepareDeps(RunLaunchRequest.GeneratedHomeRun(1234), out string failure);
            Assert.IsNotNull(deps, failure);
            Assert.AreEqual(1234, deps.RunSeed, "the Synaptic Sea world follows the requested seed, never a reseeded home seed");
            GdDict blueprint = GdJson.Parse(Storage.ReadText(deps.BlueprintPath)) as GdDict;
            Assert.IsNotNull(blueprint);
            Assert.AreEqual(_boot.GeneratedStart.Seed, V.I64(blueprint["seed_value"]));
            Assert.AreEqual((long)ShipBlueprint.Condition.Pristine, V.I64(blueprint["condition"]), "the layout is generated Pristine");
            Assert.AreEqual(RunLaunchRequest.GeneratedHomeRun(1234).TimeScale, deps.TimeScale);
        }

        [Test]
        public void TheGeneratedLayoutCarriesTheExteriorDockingPortAndDockPortsHonoursIt()
        {
            RunSessionDeps deps = _boot.PrepareDeps(RunLaunchRequest.GeneratedHomeRun(1234), out string failure);
            Assert.IsNotNull(deps, failure);
            GdDict layout = GdJson.Parse(Storage.ReadText(deps.LayoutPath)) as GdDict;
            Assert.IsNotNull(layout);
            Assert.IsTrue(layout.Has("docking_port"), "HomeDockPlanner stamped the contract");
            GdArray position = layout.GetDictOrEmpty("docking_port").GetArrayOrEmpty("position");
            GdDict port = DockPorts.ForDerelict(layout, _boot.GeneratedStart.Seed, 0);
            Assert.IsFalse(port.IsEmpty, "the contract parses");
            var p = (Vec3)port["position"];
            Assert.AreEqual(V.F64(position[0]), p.X, 1e-9);
            Assert.AreEqual(V.F64(position[2]), p.Z, 1e-9);
        }

        [Test]
        public void TheSameSeedGivesTheSameLayoutInItsOwnRunDirectoryAndADifferentSeedDiffers()
        {
            RunSessionDeps first = _boot.PrepareDeps(RunLaunchRequest.GeneratedHomeRun(501), out string failure);
            Assert.IsNotNull(first, failure);
            string firstDir = _boot.RunDirectory, firstLayout = Storage.ReadText(first.LayoutPath);
            RunSessionDeps again = _boot.PrepareDeps(RunLaunchRequest.GeneratedHomeRun(501), out failure);
            Assert.IsNotNull(again, failure);
            Assert.AreNotEqual(firstDir, _boot.RunDirectory, "each run gets its own directory");
            Assert.AreEqual(firstLayout, Storage.ReadText(again.LayoutPath), "same seed, same layout");
            RunSessionDeps other = _boot.PrepareDeps(RunLaunchRequest.GeneratedHomeRun(502), out failure);
            Assert.IsNotNull(other, failure);
            Assert.AreNotEqual(firstLayout, Storage.ReadText(other.LayoutPath), "a different seed gives a different layout");
        }
    }
}
