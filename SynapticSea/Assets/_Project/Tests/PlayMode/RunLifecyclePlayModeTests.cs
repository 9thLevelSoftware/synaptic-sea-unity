using System.Collections;
using System.Linq;
using NUnit.Framework;
using SynapticSea.App;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.Game;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>
    /// The run lifecycle on the real scenes: Title New Run and a direct-open scene boot the Milestone A hub
    /// (golden <c>coherent_ship_001</c>), a non-slice New Run fails closed back to Title, Continue reloads that hub
    /// by path, death pauses into the results and returns to the title with the last-run line, and boot/load failures
    /// return to the title with the error.
    /// </summary>
    public class RunLifecyclePlayModeTests
    {
        IStorage _previousStorage;
        IResourceReader _previousResources;
        ILog _previousLog;
        MemoryStorage _storage;
        PlayableBootstrap _boot;
        RunSession _s;

        [SetUp]
        public void SetUp()
        {
            _previousStorage = CoreServices.UserStorage;
            _previousResources = CoreServices.Resources;
            _previousLog = CoreServices.Log;
            _storage = new MemoryStorage();
            AppServices.StorageOverride = _storage;
            RunLaunchRequest.Pending = null;
            RunReturnInfo.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (RunSessionHost host in Object.FindObjectsByType<RunSessionHost>()) Object.DestroyImmediate(host.gameObject);
            foreach (PlayableBootstrap boot in Object.FindObjectsByType<PlayableBootstrap>()) Object.DestroyImmediate(boot.gameObject);
            foreach (TitleScreen title in Object.FindObjectsByType<TitleScreen>()) Object.DestroyImmediate(title.gameObject);
            AppServices.Shutdown();
            AppServices.StorageOverride = null;
            RunLaunchRequest.Pending = null;
            RunReturnInfo.Clear();
            HallucinationFx.SetGlobalIntensity(0.0);
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
            CoreServices.UserStorage = _previousStorage;
            CoreServices.Resources = _previousResources;
            CoreServices.Log = _previousLog;
        }

        IEnumerator BootPlayable(RunLaunchRequest request)
        {
            RunLaunchRequest.Pending = request;
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName);
            yield return null;
            float deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline && (PlayableBootstrap.Current == null || !PlayableBootstrap.Current.IsBooted))
            {
                if (PlayableBootstrap.Current != null && PlayableBootstrap.Current.IsFailed) break;
                yield return null;
            }
            _boot = PlayableBootstrap.Current;
            Assert.IsNotNull(_boot, "the Playable scene has a bootstrap");
            Assert.IsTrue(_boot.IsBooted, "the run booted: " + _boot.BootFailure);
            _s = _boot.Session;
            yield return null;
        }

        static IEnumerator WaitForTitle(System.Action<TitleScreen> found)
        {
            TitleScreen title = null;
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                title = SceneManager.GetActiveScene().name == RunLaunchRequest.TitleSceneName ? Object.FindAnyObjectByType<TitleScreen>() : null;
                if (title != null && title.IsBuilt) break;
                yield return null;
            }
            Assert.IsNotNull(title, "returned to the Title scene");
            Assert.IsTrue(title.IsBuilt);
            found(title);
        }

        [UnityTest]
        public IEnumerator NewRunBootsTheMilestoneAHubAndDocksTheLifeboat()
        {
            yield return BootPlayable(RunLaunchRequest.NewRun());
            Assert.AreEqual(RunLaunchMode.NewRun, _boot.Launch.Mode);
            Assert.IsNull(_boot.GeneratedStart, "Milestone A hub is authored, not generated");
            StringAssert.Contains("coherent_ship_001", _s.LayoutPath);
            Assert.AreEqual(MilestoneALaunch.HubLayoutPath, _s.LayoutPath);
            Assert.AreEqual(RunLaunchRequest.DefaultSeed, _s.RunSeed);
            Assert.AreEqual("breach_field", _s.BiomeId);
            Assert.AreEqual("standard", _s.DifficultyId);
            GdDict ctx = _s.GetRunContextSummary();
            Assert.AreEqual(RunLaunchRequest.DefaultSeed, V.I64(ctx["seed"]));

            Assert.IsTrue(_s.PlayableStarted);
            Assert.Greater(_s.Interactables.Count, 0, "objectives from the hub gameplay slice");
            Assert.IsNotNull(_boot.Host.SceneState.Player, "player spawned");
            Assert.IsNotNull(_s.LifeboatShip, "life boat built");
            Assert.AreSame(_s.HomeShip, _s.LifeboatShip.ParentShip, "the life boat docked to the hub");
            var lifeboat = (SceneShipRoot)_s.LifeboatShip.SceneRoot;
            Assert.IsTrue(lifeboat.IsInsideTree && lifeboat.GameObject.activeInHierarchy, "the life boat is placed in the scene");
            Assert.Less(Vector3.Distance(lifeboat.GameObject.transform.position, _boot.Host.ShipHost.HomeLoader.GameObject.transform.position), 80f);
        }

        [UnityTest]
        public IEnumerator NonSliceNewRunFailsClosedAndReturnsToTitle()
        {
            RunLaunchRequest.Pending = RunLaunchRequest.NewRun(99, "dead_fleet", "hardened");
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName);
            TitleScreen title = null;
            yield return WaitForTitle(t => title = t);
            StringAssert.Contains("Load failed: non_slice_launch", title.StatusText);
            StringAssert.Contains("seed=99", title.StatusText);
            Assert.IsTrue(PlayableBootstrap.Current == null || !PlayableBootstrap.Current, "the failed playable scene unloaded");
            Assert.IsFalse(Object.FindObjectsByType<RunSessionHost>().Any(h => h.Session != null && h.Session.PlayableStarted), "the wrong layout is never loaded");
        }

        [UnityTest]
        public IEnumerator DirectOpenBootsTheMilestoneAHub()
        {
            yield return BootPlayable(null);
            Assert.IsTrue(_boot.DirectOpen);
            Assert.AreEqual(RunLaunchMode.NewRun, _boot.Launch.Mode);
            Assert.IsNull(_boot.GeneratedStart, "no request = the same hub path");
            StringAssert.Contains("coherent_ship_001", _s.LayoutPath);
            Assert.AreEqual(RunLaunchRequest.DefaultSeed, _s.RunSeed);
            Assert.AreEqual(RunLaunchRequest.DefaultBiomeId, _s.BiomeId);
            Assert.AreEqual(RunLaunchRequest.DefaultDifficultyId, _s.DifficultyId);
        }

        [UnityTest]
        public IEnumerator ContinueReloadsTheHubRun()
        {
            yield return BootPlayable(RunLaunchRequest.NewRun());
            string layoutPath = _s.LayoutPath;
            long seed = _s.RunSeed;
            Assert.IsTrue(_s.RequestSave(), "world save written");

            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.AreEqual(RunLaunchMode.Continue, _boot.Launch.Mode);
            Assert.IsNull(_boot.GeneratedStart, "Continue does not generate");
            Assert.IsTrue(_boot.LaunchApplied, "the world save applied");
            Assert.AreEqual(layoutPath, _s.LayoutPath, "the hub layout reloads by path");
            Assert.AreEqual(seed, _s.RunSeed);
            Assert.AreEqual("breach_field", _s.BiomeId);
            Assert.AreEqual("standard", _s.DifficultyId);
            Assert.IsNotNull(_boot.Host.ShipHost.HomeLoader);
            Assert.IsTrue(_boot.Host.ShipHost.HomeLoader.IsInsideTree);
        }

        [UnityTest]
        public IEnumerator ContinueReloadsTheSavedHomeLayoutNotTheMilestoneAHub()
        {
            const string savedLayout = "res://data/procgen/golden/coherent_ship_003/layout.json";
            yield return BootPlayable(new RunLaunchRequest
            {
                Mode = RunLaunchMode.NewRun,
                LayoutOverridePath = savedLayout,
                Seed = RunLaunchRequest.DefaultSeed,
                BiomeId = RunLaunchRequest.DefaultBiomeId,
                DifficultyId = RunLaunchRequest.DefaultDifficultyId,
            });
            Assert.AreEqual(savedLayout, _s.LayoutPath, "precondition: Continue starts from a non-hub save");
            Assert.IsTrue(_s.RequestSave(), "world save written");

            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.AreEqual(RunLaunchMode.Continue, _boot.Launch.Mode);
            Assert.AreEqual(savedLayout, _s.LayoutPath, "Continue boots the saved home, not the Milestone A hub");
            StringAssert.DoesNotContain("coherent_ship_001", _s.LayoutPath);
            Assert.IsTrue(_boot.LaunchApplied, "the world save applied");
        }

        [UnityTest]
        public IEnumerator DeathShowsResultsAndConfirmReturnsToTitleWithTheLastRun()
        {
            yield return BootPlayable(RunLaunchRequest.NewRun());
            _s.VitalsState.Health = 0.0;
            yield return null;
            yield return null;
            Assert.IsTrue(_s.SliceComplete, "incapacitation ended the run");
            RunResultsPanel results = _boot.Results;
            Assert.IsNotNull(results, "death shows the run results");
            Assert.AreSame(results, _boot.Coordinator.Stack.Top);
            Assert.IsTrue(_boot.Coordinator.Stack.SimulationPaused, "the terminal results pause the simulation");
            Assert.AreEqual("death", results.NormalizedOutcome());
            StringAssert.Contains("Outcome: death", results.BodyText);
            StringAssert.Contains("Time survived:", results.BodyText);
            StringAssert.Contains("Objectives completed:", results.BodyText);
            var context = results.Q<Label>("run-results-context");
            Assert.IsNotNull(context);
            StringAssert.Contains("seed " + _s.RunSeed + " · breach_field · standard", context.text);
            double pausedTime = _s.WorldTime;
            yield return null;
            yield return null;
            Assert.AreEqual(pausedTime, _s.WorldTime, "no ticks under the results");

            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = results.ReturnButton;
                results.ReturnButton.SendEvent(submit);
            }
            TitleScreen title = null;
            yield return WaitForTitle(t => title = t);
            StringAssert.Contains("Last run: death — seed " + _s.RunSeed + " · breach_field · standard", title.StatusText);
            StringAssert.Contains("Progress: objectives", title.StatusText);
        }

        [UnityTest]
        public IEnumerator BootFailureReturnsToTitleWithTheError()
        {
            RunLaunchRequest.Pending = new RunLaunchRequest { LayoutOverridePath = "res://data/procgen/golden/does_not_exist/layout.json" };
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName);
            TitleScreen title = null;
            yield return WaitForTitle(t => title = t);
            StringAssert.Contains("Load failed: layout override not found", title.StatusText);
            Assert.IsTrue(PlayableBootstrap.Current == null || !PlayableBootstrap.Current, "the failed playable scene unloaded");
        }

        [UnityTest]
        public IEnumerator FailedContinueReturnsToTitleInsteadOfAFreshRun()
        {
            RunLaunchRequest.Pending = RunLaunchRequest.ContinueWorld();
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName);
            TitleScreen title = null;
            yield return WaitForTitle(t => title = t);
            StringAssert.Contains("Load failed: no compatible world save", title.StatusText);
            Assert.IsFalse(Object.FindObjectsByType<RunSessionHost>().Any(h => h.Session != null && h.Session.PlayableStarted), "no silent fresh run");
        }

        [UnityTest]
        public IEnumerator FailedSlotLoadReturnsToTitleWithTheError()
        {
            RunLaunchRequest.Pending = RunLaunchRequest.LoadSlot("manual_3");
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName);
            TitleScreen title = null;
            yield return WaitForTitle(t => title = t);
            StringAssert.Contains("Load failed: save slot 'manual_3' could not be loaded", title.StatusText);
        }
    }
}
