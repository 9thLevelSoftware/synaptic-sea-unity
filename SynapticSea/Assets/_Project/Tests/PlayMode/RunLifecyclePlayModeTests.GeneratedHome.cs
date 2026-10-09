using System.Collections;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Game;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using UnityEngine;
using UnityEngine.TestTools;

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>
    /// Phase 1.8: the generated-home boot behind <see cref="RunLaunchRequest.GeneratedHome"/> on the real scenes. These restore the
    /// d88eaec lifecycle tests (generate and dock, same seed same layout, Continue reloads from <c>user://runs</c>) against the flag.
    /// They prove the boot, not safety: the generated home has no repair-kit/food/water guarantee, hazard strip or typed objectives yet.
    /// </summary>
    public partial class RunLifecyclePlayModeTests
    {
        string LayoutText(RunSession s) => _storage.ReadText(s.LayoutPath);

        [UnityTest]
        public IEnumerator GeneratedHomeNewRunGeneratesTheHomeShipAndDocksTheLifeboatOnTheExteriorEdge()
        {
            yield return BootPlayable(RunLaunchRequest.GeneratedHomeRun(1234));
            Assert.AreEqual(RunLaunchMode.NewRun, _boot.Launch.Mode);
            Assert.IsNotNull(_boot.GeneratedStart, "the home ship was generated");
            StringAssert.StartsWith(PlayableBootstrap.RunsDir, _s.LayoutPath);
            StringAssert.StartsWith(_boot.RunDirectory, _s.GameplaySlicePath);
            Assert.IsTrue(_s.GeneratedHome);
            Assert.IsTrue(_storage.FileExists(_boot.RunDirectory + "layout.json"));
            Assert.IsTrue(_storage.FileExists(_boot.RunDirectory + "gameplay_slice.json"));
            Assert.IsTrue(_storage.FileExists(_boot.RunDirectory + "blueprint.json"));
            Assert.AreEqual(_boot.GeneratedStart.Documents.LayoutJson, LayoutText(_s), "the written layout is the generated text");
            Assert.AreEqual(1234, _s.RunSeed, "the run seed is the requested seed; the reseeded home seed lives in the blueprint");
            Assert.AreEqual("breach_field", _s.BiomeId);
            Assert.AreEqual("standard", _s.DifficultyId);
            Assert.AreEqual(_boot.GeneratedStart.Documents.KitPath, _s.KitPath);

            Assert.IsTrue(_s.PlayableStarted, _s.LastFailureReason);
            Assert.IsNotNull(_boot.Host.SceneState.Player, "player spawned");
            Assert.Greater(_s.Interactables.Count, 0, "objectives from the generated gameplay slice");
            Assert.IsNotNull(_s.LifeboatShip, "life boat built");
            Assert.AreSame(_s.HomeShip, _s.LifeboatShip.ParentShip, "the life boat docked to the generated home ship");

            // The boat docks at the planner's exterior port, not the room-centroid +X default.
            GdDict contract = _s.HomeShip.BuiltLayout.GetDictOrEmpty("docking_port");
            Assert.IsFalse(contract.IsEmpty, "HomeDockPlanner stamped the docking_port contract on the booted home layout");
            GdDict port = DockPorts.ForDerelict(_s.HomeShip.BuiltLayout, _s.HomeShip.Blueprint.SeedValue, 0);
            Assert.IsFalse(port.IsEmpty, "the contract parses");
            GdArray planned = contract.GetArrayOrEmpty("position");
            var p = (Vec3)port["position"];
            Assert.AreEqual(V.F64(planned[0]), p.X, 1e-6);
            Assert.AreEqual(V.F64(planned[2]), p.Z, 1e-6);

            var lifeboat = (SceneShipRoot)_s.LifeboatShip.SceneRoot;
            Assert.IsTrue(lifeboat.IsInsideTree && lifeboat.GameObject.activeInHierarchy, "the life boat is placed in the scene");
            Assert.Less(Vector3.Distance(lifeboat.GameObject.transform.position, _boot.Host.ShipHost.HomeLoader.GameObject.transform.position), 80f);

            // The loot roll key folds the run seed in; the golden hub's empty-marker key is untouched.
            Assert.AreEqual("home:1234:probe", _s.LootSeedSource("probe"));
        }

        [UnityTest]
        public IEnumerator GeneratedHomeSameSeedGivesTheSameLayoutAndADifferentSeedDiffers()
        {
            yield return BootPlayable(RunLaunchRequest.GeneratedHomeRun(501));
            string first = LayoutText(_s);
            string firstDir = _boot.RunDirectory;
            long firstHash = SeedDeterminismContract.Fnv1a64(first);

            yield return BootPlayable(RunLaunchRequest.GeneratedHomeRun(501));
            Assert.AreNotEqual(firstDir, _boot.RunDirectory, "each run gets its own directory");
            Assert.AreEqual(firstHash, SeedDeterminismContract.Fnv1a64(LayoutText(_s)), "same seed, same layout hash");

            yield return BootPlayable(RunLaunchRequest.GeneratedHomeRun(502));
            Assert.AreNotEqual(firstHash, SeedDeterminismContract.Fnv1a64(LayoutText(_s)), "a different seed gives a different layout");
        }

        [UnityTest]
        public IEnumerator DirectOpenAndTheDefaultNewRunStayOnTheGoldenHubWhenTheFlagIsOff()
        {
            yield return BootPlayable(null);
            Assert.IsTrue(_boot.DirectOpen);
            Assert.IsNull(_boot.GeneratedStart, "no request = the golden hub, not generation");
            Assert.IsFalse(_s.GeneratedHome);
            StringAssert.Contains("coherent_ship_001", _s.LayoutPath);
            Assert.AreEqual(":start_supply_a", _s.LootSeedSource("start_supply_a"), "the golden hub keeps the original loot key");
        }

        [UnityTest]
        public IEnumerator ContinueReloadsAGeneratedRunFromUserRuns()
        {
            yield return BootPlayable(RunLaunchRequest.GeneratedHomeRun(777));
            _s.ThreatManager.Threats.Clear();
            string layoutPath = _s.LayoutPath;
            string layout = LayoutText(_s);
            long seed = _s.RunSeed;
            string runDirectory = _boot.RunDirectory;
            Assert.IsTrue(_s.RequestSave(), "world save written");

            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.AreEqual(RunLaunchMode.Continue, _boot.Launch.Mode);
            Assert.IsNull(_boot.GeneratedStart, "Continue does not generate");
            Assert.IsTrue(_boot.LaunchApplied, "the world save applied");
            Assert.AreEqual(layoutPath, _s.LayoutPath, "the generated layout reloads by its user://runs path");
            Assert.AreEqual(layout, LayoutText(_s));
            Assert.AreEqual(seed, _s.RunSeed);
            Assert.AreEqual("breach_field", _s.BiomeId);
            Assert.IsTrue(_s.GeneratedHome, "a reloaded generated home is still recognised, so its loot key keeps the run seed");
            Assert.AreEqual("home:777:probe", _s.LootSeedSource("probe"));
            Assert.IsTrue(_storage.FileExists(runDirectory + "layout.json"), "the janitor keeps the referenced run directory");
            Assert.IsNotNull(_boot.Host.ShipHost.HomeLoader);
            Assert.IsTrue(_boot.Host.ShipHost.HomeLoader.IsInsideTree);
        }
    }
}
