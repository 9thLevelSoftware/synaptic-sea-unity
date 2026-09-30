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
using UnityEngine.AI;
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
        float _previousTimeScale;

        [SetUp]
        public void SetUp()
        {
            _previousStorage = CoreServices.UserStorage;
            _previousTimeScale = Time.timeScale;
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
            Time.timeScale = _previousTimeScale;
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
        public IEnumerator ExtractShowsResultsAndConfirmReturnsToTitleWithTheLastRun()
        {
            yield return BootPlayable(RunLaunchRequest.NewRun());
            _s.EndRun("extraction");
            yield return null;
            yield return null;
            Assert.IsTrue(_s.SliceComplete, "extraction ended the run");
            RunResultsPanel results = _boot.Results;
            Assert.IsNotNull(results, "extract shows the run results");
            Assert.AreSame(results, _boot.Coordinator.Stack.Top);
            Assert.IsTrue(_boot.Coordinator.Stack.SimulationPaused, "the terminal results pause the simulation");
            Assert.AreEqual("extraction", results.NormalizedOutcome());
            StringAssert.Contains("Outcome: extraction", results.BodyText);
            StringAssert.Contains("Time survived:", results.BodyText);
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
            StringAssert.Contains("Last run: extraction — seed " + _s.RunSeed + " · breach_field · standard", title.StatusText);
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

        IEnumerator StartThroughTitle()
        {
            SceneManager.LoadScene("Boot");
            TitleScreen title = null;
            yield return WaitForTitle(t => title = t);
            var coordinator = title.Coordinator;
            coordinator.MenuState.SetFocusIndex(coordinator.MenuPanel.Rows.ToList().FindIndex(r => r.Id == "start"));
            coordinator.HandleUiInput(UiCommand.Accept);
            title.NewRunSetup.SetSeed(RunLaunchRequest.DefaultSeed);
            title.NewRunSetup.FocusRow(NewRunSetupPanel.RowStart);
            title.NewRunSetup.Consume(UiCommand.Accept);
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline && (PlayableBootstrap.Current == null || !PlayableBootstrap.Current.IsBooted)) yield return null;
            _boot = PlayableBootstrap.Current;
            Assert.IsNotNull(_boot);
            Assert.IsTrue(_boot.IsBooted, _boot.BootFailure);
            _s = _boot.Session;
            for (int i = 0; i < 30; i++) yield return new WaitForFixedUpdate();
        }

        IEnumerator WalkTo(Vec3 target, float radius = 1.2f)
        {
            // A restored/rebuilt surface receives portal carving on the next navigation update.
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            var player = _boot.Host.SceneState.Player;
            var filter = new NavMeshQueryFilter { agentTypeID = ShipNavMesh.AgentTypeId, areaMask = NavMesh.AllAreas };
            Assert.IsTrue(NavMesh.SamplePosition(Frame.ToUnity(target), out var landing, 2.5f, filter), "walkable target: " + target);
            var path = new NavMeshPath();
            for (int guard = 0; guard < 8; guard++)
            {
                if (NavMesh.CalculatePath(player.transform.position, landing.position, filter, path) && path.status == NavMeshPathStatus.PathComplete) break;
                AuthoredPortalRuntime chosen = null;
                NavMeshPath approach = null;
                Vector3 blockedRouteEnd = path.corners.Length > 0 ? path.corners[path.corners.Length - 1] : landing.position;
                foreach (var portal in _boot.Host.ShipHost.HomeLoader.View.GetAuthoredPortalNodes()
                    .Where(p => !p.isOpen && !p.isExterior && p.portalKind != "LOCKED")
                    .OrderBy(p => Vector3.Distance(p.transform.position, blockedRouteEnd)))
                {
                    foreach (var offset in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                    {
                        Vector3 candidate = portal.transform.position + offset * 1.3f;
                        candidate.y = player.transform.position.y;
                        if (!NavMesh.SamplePosition(candidate, out var sample, 0.6f, filter)) continue;
                        var candidatePath = new NavMeshPath();
                        if (!NavMesh.CalculatePath(player.transform.position, sample.position, filter, candidatePath) || candidatePath.status != NavMeshPathStatus.PathComplete) continue;
                        chosen = portal;
                        approach = candidatePath;
                        break;
                    }
                    if (chosen != null) break;
                }
                if (chosen == null)
                {
                    // Interactions require reach, not standing inside the prop/ramp collider at its exact centre.
                    for (float distance = 0.5f; distance <= radius; distance += 0.5f)
                        foreach (var offset in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                        {
                            Vector3 candidate = Frame.ToUnity(target) + offset * distance;
                            candidate.y = player.transform.position.y;
                            if (!NavMesh.SamplePosition(candidate, out var sample, 0.3f, filter)) continue;
                            var candidatePath = new NavMeshPath();
                            if (NavMesh.CalculatePath(player.transform.position, sample.position, filter, candidatePath)
                                && candidatePath.status == NavMeshPathStatus.PathComplete)
                            { yield return WalkAlong(candidatePath, 0.2f); yield break; }
                        }
                }
                Assert.IsNotNull(chosen, "a reachable closed door or standing interaction approach exists for " + target);
                Debug.Log("[NaturalWalk] target " + target + "; door " + chosen.portalId + "@" + chosen.transform.position + "; route " + string.Join(" -> ", approach.corners.Select(c => c.ToString())));
                yield return WalkAlong(approach, 0.3f);
                player.RequestInteract();
                for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
                Assert.IsTrue(chosen.isOpen, "real interact opens " + chosen.portalId + "; handler " + _s.LastInteractHandlerId);
            }
            Assert.AreEqual(NavMeshPathStatus.PathComplete, path.status, "standing route to " + target);
            yield return WalkAlong(path, radius);
        }

        IEnumerator WalkAlong(NavMeshPath path, float radius)
        {
            var player = _boot.Host.SceneState.Player;
            try
            {
                foreach (Vector3 corner in path.corners.Skip(1))
                {
                    Vector3 waypoint = corner;
                    // The threat NavMesh has a smaller clearance margin than the player's controller skin.
                    // Keep automated steering inside physically standing space, as a player would when turning.
                    SpawnClearance.TryFindClear(corner, floor => Mathf.Abs(floor.bounds.max.y - corner.y) < 0.5f, out waypoint);
                    float deadline = Time.realtimeSinceStartup + 25f;
                    // NavMesh corners sit on the baked agent margin; the CharacterController also has a skin width.
                    // Approach within the interaction radius instead of pressing the capsule into a doorway edge.
                    float reach = corner == path.corners.Last() ? Mathf.Max(radius, 0.2f) : 0.2f;
                    while (Vector2.Distance(new Vector2(player.transform.position.x, player.transform.position.z), new Vector2(waypoint.x, waypoint.z)) > reach)
                    {
                        Assert.Less(Time.realtimeSinceStartup, deadline, "player movement stalled at " + player.transform.position
                            + " en route to " + waypoint + "; nearby blockers: " + string.Join(", ", Physics.OverlapSphere(player.transform.position + Vector3.up * 0.8f, 1f, SpawnClearance.BlockingMask)
                                .Select(c => c.transform.parent.name + "/" + c.name + "@" + c.bounds.center + " size " + c.bounds.size)));
                        Assert.IsFalse(_s.SliceComplete, "the player survived exploration");
                        Vector3 direction = waypoint - player.transform.position;
                        direction.y = 0;
                        player.SetScriptedMoveDirection(Frame.ToGodot(direction.normalized));
                        yield return new WaitForFixedUpdate();
                    }
                }
            }
            finally { player.ClearScriptedMoveDirection(); }
        }

        [UnityTest]
        public IEnumerator WalkTheHubUseTheDeckConnectionLootSaveContinueAndExtract()
        {
            yield return StartThroughTitle();
            _s.RefreshDeckTransitions();
            var up = _s.DeckTransitions.First(d => d.DestinationDeck == 1);
            yield return WalkTo(up.GlobalPosition, 2.5f);
            _boot.Host.SceneState.Player.RequestInteract();
            yield return new WaitForFixedUpdate();
            Assert.AreEqual("deck_transition", _s.LastInteractHandlerId);
            Assert.Greater(_boot.Host.SceneState.Player.transform.position.y, 3.5f, "the authored connection reaches deck 1");
            Assert.IsTrue(SpawnClearance.IsClear(_boot.Host.SceneState.Player.transform.position));
            var down = _s.DeckTransitions.First(d => d.DestinationDeck == 0);
            yield return WalkTo(down.GlobalPosition, 2.5f);
            _boot.Host.SceneState.Player.RequestInteract();
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            Assert.AreEqual("deck_transition", _s.LastInteractHandlerId);
            Assert.Less(_boot.Host.SceneState.Player.transform.position.y, 1.6f, "the connection returns safely to the lower deck");
            Assert.IsTrue(SpawnClearance.IsClear(_boot.Host.SceneState.Player.transform.position));
            yield return WalkTo(up.GlobalPosition, 2.5f);
            _boot.Host.SceneState.Player.RequestInteract();
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            Assert.AreEqual("deck_transition", _s.LastInteractHandlerId, "returning from the lower landing uses the same deck target as the HUD");
            Assert.Greater(_boot.Host.SceneState.Player.transform.position.y, 3.5f);
            CaptureGameCamera("natural-hub-upper-deck.png");
            var loot = _s.LootContainers.First(l => !l.Searched && l.GlobalPosition.Y > 3f);
            yield return WalkTo(loot.GlobalPosition);
            _boot.Host.SceneState.Player.RequestInteract();
            yield return null;
            Assert.IsTrue(loot.Searched, "the player searched real authored supplies");
            string lootId = loot.ContainerId;
            var openDoors = _s.HomeShip.AuthoredOpenPortalIds.Select(id => V.Str(id)).ToArray();
            Assert.IsTrue(_s.RequestSave());
            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            foreach (var id in openDoors)
                Assert.IsTrue(_boot.Host.ShipHost.HomeLoader.View.GetAuthoredPortalNodes().First(p => p.portalId == id).isOpen, "Continue restores opened door " + id);
            Assert.IsTrue(_s.LootContainers.First(l => l.ContainerId == lootId).Searched, "loot state survives Continue");
            for (int guard = 0; guard < 12 && !_s.SliceComplete; guard++)
            {
                var objective = _s.Interactables.FirstOrDefault(o => o.Active && !o.Completed);
                Assert.IsNotNull(objective, "an active objective remains");
                yield return WalkTo(objective.GlobalPosition);
                _boot.Host.SceneState.Player.RequestInteract();
                yield return null;
            }
            Assert.IsTrue(_s.SliceComplete, "actual objective interactions end extraction");
            Assert.AreEqual("extraction", _boot.Results.NormalizedOutcome());
            using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = _boot.Results.ReturnButton; _boot.Results.ReturnButton.SendEvent(submit); }
            yield return WaitForTitle(_ => { });
            yield return StartThroughTitle();
            Assert.AreEqual(0, _s.ObjectiveCompletionCount, "New Run starts fresh after extraction");
        }

        [UnityTest]
        public IEnumerator RealThreatDamageEndsTheRunAndNewRunRestartsIt()
        {
            yield return StartThroughTitle();
            Vec3 player = _boot.Host.SceneState.Player.GodotPosition;
            Vec3? chosen = null;
            var probe = new PhysicsLineOfSightProbe();
            foreach (var offset in new[] { Vec3.Right, -Vec3.Right, Vec3.Forward, -Vec3.Forward })
                if (!probe.IntersectRay(player + Vec3.Up, player + offset + Vec3.Up, out _)) { chosen = player + offset; break; }
            Assert.IsTrue(chosen.HasValue, "a nearby unobstructed encounter position exists");
            _s.ThreatManager.InjectValidationEncounter(GdArray.Of("stalker"), chosen.Value - new Vec3(4, 0, 0));
            var threat = _s.ThreatManager.Threats[0];
            threat.RoomId = _s.ResolvePlayerRoom(chosen.Value);
            int hits = 0;
            _boot.Host.Threats.PlayerHit += (_, __, ___, ____) => hits++;
            Time.timeScale = 8f;
            float deadline = Time.realtimeSinceStartup + 40f;
            while (!_s.SliceComplete && Time.realtimeSinceStartup < deadline) yield return null;
            Time.timeScale = _previousTimeScale;
            Assert.Greater(hits, 0, "real enemy attacks reached the player");
            Assert.IsTrue(_s.SliceComplete, "damage naturally ends the run");
            Assert.AreEqual("death", _boot.Results.NormalizedOutcome());
            using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = _boot.Results.ReturnButton; _boot.Results.ReturnButton.SendEvent(submit); }
            yield return WaitForTitle(_ => { });
            yield return StartThroughTitle();
            Assert.IsFalse(_s.SliceComplete);
            Assert.Greater(_s.VitalsState.Health, 0);
        }

        void CaptureGameCamera(string filename)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            _boot.Host.SceneState.CameraRig.SyncToTarget();
            var camera = _boot.Host.SceneState.CameraRig.Camera;
            var target = new RenderTexture(1280, 720, 24);
            var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            var oldTarget = camera.targetTexture; var oldActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); pixels.Apply();
                string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../../artifacts/screenshots"));
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, filename), pixels.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = oldTarget; RenderTexture.active = oldActive;
                Object.Destroy(target); Object.Destroy(pixels);
            }
        }
    }
}
