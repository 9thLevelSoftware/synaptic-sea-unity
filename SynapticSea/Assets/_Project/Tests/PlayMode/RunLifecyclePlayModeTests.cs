using System.Collections;
using System.Collections.Generic;
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
    public partial class RunLifecyclePlayModeTests
    {
        IStorage _previousStorage;
        IResourceReader _previousResources;
        ILog _previousLog;
        MemoryStorage _storage;
        PlayableBootstrap _boot;
        RunSession _s;
        float _previousTimeScale;
        bool _defendWhileExploring;
        bool _flyJoinedAssembly, _recordJourneyTelemetry;
        bool _installedAssemblyFixture;
        readonly HashSet<RunSession> _observedSessions=new HashSet<RunSession>();
        readonly GdDict _journeyDamage=new GdDict(), _journeyDebits=new GdDict();

        [SetUp]
        public void SetUp()
        {
            _defendWhileExploring = false;
            _installedAssemblyFixture=false;
            _flyJoinedAssembly=false;_recordJourneyTelemetry=false;_observedSessions.Clear();_journeyDamage.Clear();_journeyDebits.Clear();
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
            if(_recordJourneyTelemetry)Debug.Log("[NaturalJourneyLedger] actual_health_loss_by_source="+GdJson.Stringify(_journeyDamage)
                +" player_item_debits_including_cargo_transfers="+GdJson.Stringify(_journeyDebits));
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
            ObserveNaturalJourney();
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
        public IEnumerator NewRunWithARandomSeedBootsTheGoldenHubOnASeededWorld()
        {
            const long seed = 4711;
            yield return BootPlayable(RunLaunchRequest.NewRun(seed, RunLaunchRequest.DefaultBiomeId, RunLaunchRequest.DefaultDifficultyId));
            Assert.AreEqual(RunLaunchMode.NewRun, _boot.Launch.Mode);
            Assert.IsTrue(_s.PlayableStarted, "the run reaches the playable state");
            Assert.AreEqual(MilestoneALaunch.HubLayoutPath, _s.LayoutPath, "the home is the golden hub whatever the seed");
            Assert.AreEqual(seed, _s.RunSeed);
            Assert.AreEqual(seed, V.I64(_s.GetRunContextSummary()["seed"]));
            Assert.AreEqual(seed, _s.SynapticSeaWorld.WorldSeed, "the seed drives the world");
            Assert.IsNotNull(_boot.Host.SceneState.Player, "player spawned");
            Assert.IsNotNull(_s.LifeboatShip, "life boat built");
            Assert.Greater(_s.SynapticSeaWorld.MarkersInRange(250.0).Count, 0, "the scanner has contacts in the seeded world");
        }

        [UnityTest]
        public IEnumerator ResultsNewRunRollsAFreshSeedAndKeepsTheClassAndPacing()
        {
            yield return BootPlayable(RunLaunchRequest.NewRun());
            var previousLoader = PlayableBootstrap.SceneLoader;
            var previousRandom = PlayableBootstrap.RandomSeed;
            var loaded = new List<string>();
            PlayableBootstrap.SceneLoader = name => { loaded.Add(name); return true; };
            PlayableBootstrap.RandomSeed = () => 31337;
            try
            {
                typeof(PlayableBootstrap).GetMethod("StartNextRun", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(_boot, null);
                CollectionAssert.AreEqual(new[] { RunLaunchRequest.PlayableSceneName }, loaded);
                RunLaunchRequest next = RunLaunchRequest.Pending;
                Assert.IsNotNull(next);
                Assert.AreEqual(31337L, next.Seed, "the next run rolls a fresh seed");
                Assert.AreEqual(RunLaunchRequest.DefaultBiomeId, next.BiomeId);
                Assert.AreEqual(RunLaunchRequest.DefaultDifficultyId, next.DifficultyId);
                Assert.AreEqual(_boot.Launch.ClassId, next.ClassId);
                Assert.AreEqual(_s.GameClock.Scale, next.TimeScale, "the next run keeps this run's pacing");
                Assert.IsTrue(MilestoneALaunch.TryAccept(next.Seed, next.BiomeId, next.DifficultyId, out string reason), reason);
            }
            finally
            {
                PlayableBootstrap.SceneLoader = previousLoader;
                PlayableBootstrap.RandomSeed = previousRandom;
                RunLaunchRequest.Pending = null;
            }
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
            ObserveNaturalJourney();
            for (int i = 0; i < 30; i++) yield return new WaitForFixedUpdate();
        }
        void ObserveNaturalJourney()
        {
            if(!_recordJourneyTelemetry || !_observedSessions.Add(_s))return;
            _s.VitalsState.HealthDamageObserved+=(source,amount)=>_journeyDamage[source]=_journeyDamage.GetFloat(source)+amount;
            _s.InventoryState.ItemsRemoved+=(item,quantity)=>_journeyDebits[item]=_journeyDebits.GetInt(item)+quantity;
        }

        IEnumerator WalkTo(SessionInteractable target, float radius = 1.2f)
        {
            // Docked controls are relocated onto connected standing space after portal carving.
            // Resolve the live anchor after that bounded reconciliation, rather than walking to
            // a copied pre-LateUpdate position after travel/Continue. All reach/LOS/path checks remain.
            float deadline = Time.unscaledTime + 3f;
            float stableSince = Time.unscaledTime;
            Vec3 previous = target.GlobalPosition;
            while (Time.unscaledTime - stableSince < 0.55f)
            {
                Assert.IsTrue(target.IsValid, "the live interaction survives dock reconciliation");
                Assert.Less(Time.unscaledTime, deadline, "docked interaction anchor never settled: " + target.NodeName);
                yield return new WaitForFixedUpdate();
                Vec3 current = target.GlobalPosition;
                if (current.DistanceSquaredTo(previous) > 0.0001)
                { previous = current; stableSince = Time.unscaledTime; }
            }
            yield return WalkTo(target.GlobalPosition, radius);
        }

        IEnumerator WalkTo(Vec3 target, float radius = 1.2f)
        {
            // A restored/rebuilt surface receives portal carving on the next navigation update.
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            var player = _boot.Host.SceneState.Player;
            var filter = new NavMeshQueryFilter { agentTypeID = ShipNavMesh.AgentTypeId, areaMask = NavMesh.AllAreas };
            Assert.IsTrue(NavMesh.SamplePosition(Frame.ToUnity(target), out var landing, 2.5f, filter), "walkable target: " + target);
            var path = new NavMeshPath();
            bool rebuiltNavigation = false;
            for (int guard = 0; guard < 8; guard++)
            {
                if (TryStandingApproach(target, radius, player.transform.position, filter, out var standing))
                { path = standing; break; }
                NavMesh.CalculatePath(player.transform.position, landing.position, filter, path);
                AuthoredPortalRuntime chosen = null;
                NavMeshPath approach = null;
                Vector3 blockedRouteEnd = path.corners.Length > 0 ? path.corners[path.corners.Length - 1] : landing.position;
                foreach (var portal in Object.FindObjectsByType<AuthoredPortalRuntime>()
                    .Where(p => !p.isOpen && p.portalKind != "LOCKED")
                    .OrderBy(p => Vector3.Distance(p.transform.position, blockedRouteEnd)))
                {
                    foreach (var offset in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                    {
                        Vector3 candidate = portal.transform.position + offset * 1.3f;
                        candidate.y = player.transform.position.y;
                        if (!NavMesh.SamplePosition(candidate, out var sample, 0.6f, filter)) continue;
                        if (!SpawnClearance.IsClear(sample.position, PlayerController.DefaultCollisionRadius + 0.05f)) continue;
                        var candidatePath = new NavMeshPath();
                        if (!NavMesh.CalculatePath(player.transform.position, sample.position, filter, candidatePath) || candidatePath.status != NavMeshPathStatus.PathComplete
                            || CrossesClosedPortal(candidatePath)) continue;
                        chosen = portal;
                        approach = candidatePath;
                        break;
                    }
                    if (chosen != null) break;
                }
                if (chosen == null && !rebuiltNavigation)
                {
                    // Diagnostic and safety net: if the route only exists after the navigation surface is rebuilt, the surface was stale.
                    rebuiltNavigation = true;
                    foreach (var nav in Object.FindObjectsByType<ShipNavMesh>()) ShipNavMesh.StructureCollisionChanged(nav.gameObject);
                    for (int i = 0; i < 12; i++) yield return new WaitForFixedUpdate();
                    Debug.Log("[NaturalWalk] no route to " + target + " from " + player.transform.position + " (" + path.status + "); rebuilt navigation and retrying");
                    continue;
                }
                if (rebuiltNavigation && chosen == null) Debug.Log("[NaturalWalk] route to " + target + " is still missing after a navigation rebuild");
                Assert.IsNotNull(chosen, "a reachable closed door or standing interaction approach exists for " + target
                    + "; current=" + _s.CurrentShip?.ShipId + " piloted=" + _s.PilotedShip?.ShipId + " home=" + _s.HomeShip?.ShipId + " boat=" + _s.LifeboatShip?.ShipId
                    + "; consoles=" + string.Join(", ", _s.BridgeTerminals.Select(t => t.ShipId + "@" + Frame.ToUnity(t.GlobalPosition)))
                    + "; dock barriers=" + string.Join(", ", _s.DockBarriers.Select(b => b.GlobalPosition + " opened=" + b.Opened + " valid=" + b.IsValid))
                    + "; join controls=" + string.Join(", ", _s.HomeJoinControls.Select(c => c.ActionId + "@" + Frame.ToUnity(c.GlobalPosition)))
                    + "; reach=" + string.Join(" | ", _s.BridgeTerminals.Select(t => ("console " + t.ShipId, Frame.ToUnity(t.GlobalPosition)))
                        .Concat(_s.DockBarriers.Select(b => ("dock barrier", Frame.ToUnity(b.GlobalPosition))))
                        .Concat(_s.HomeJoinControls.Select(c => ("join " + c.ActionId, Frame.ToUnity(c.GlobalPosition))))
                        .Select(probe =>
                        {
                            var pr = new NavMeshPath();
                            bool onMesh = NavMesh.SamplePosition(probe.Item2, out var hit, 2.5f, filter);
                            bool ok = onMesh && NavMesh.CalculatePath(player.transform.position, hit.position, filter, pr);
                            return probe.Item1 + "@" + probe.Item2 + " -> " + (!onMesh ? "off mesh" : ok ? pr.status + " ends@" + pr.corners.LastOrDefault() : "no path");
                        }))
                    + "; player=" + player.transform.position + "; landing=" + landing.position + "; path=" + path.status
                    + "; corners=" + string.Join(" -> ", path.corners.Select(c => c.ToString()))
                    + "; portals=" + string.Join(", ", Object.FindObjectsByType<AuthoredPortalRuntime>().Where(p => p.transform.position.y < 2f)
                        .Select(p => p.portalId + "@" + p.transform.position + " " + p.portalKind + " open=" + p.isOpen))
                    + "; obstacles=" + string.Join(", ", Object.FindObjectsByType<NavMeshObstacle>().Where(o => o.transform.position.y < 2f)
                        .Select(o => o.name + "@" + o.transform.TransformPoint(o.center) + " enabled=" + o.enabled + " size=" + o.size))
                    + "; blockers=" + string.Join(", ", Physics.OverlapSphere(blockedRouteEnd + Vector3.up, 2f, SpawnClearance.BlockingMask)
                        .Select(c => c.transform.parent.name + "/" + c.name + "@" + c.bounds.center + " size=" + c.bounds.size)));
                Debug.Log("[NaturalWalk] target " + target + "; door " + chosen.portalId + "@" + chosen.transform.position + "; route " + string.Join(" -> ", approach.corners.Select(c => c.ToString())));
                yield return WalkAlong(approach, 0.05f);
                player.RequestInteract();
                for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
                Assert.IsTrue(chosen.isOpen, "real interact opens " + chosen.portalId + "; handler " + _s.LastInteractHandlerId
                    + "; player="+player.transform.position+" distance="+Vector3.Distance(player.transform.position,chosen.transform.position)
                    + "; current="+_s.CurrentShip.ShipId+" focused="+(_s.FocusedAuthoredPortal(player.GodotPosition)?.PortalId ?? "none")
                    + "; portal-kind="+chosen.portalKind);
            }
            Assert.AreEqual(NavMeshPathStatus.PathComplete, path.status, "standing route to " + target);
            yield return WalkAlong(path, 0.2f);
        }

        /// <summary>Logs whether the survivor can walk to each console, dock barrier and join control from here; used to see when a route disappears.</summary>
        void LogReach(string label)
        {
            var player = _boot.Host.SceneState.Player;
            var filter = new NavMeshQueryFilter { agentTypeID = ShipNavMesh.AgentTypeId, areaMask = NavMesh.AllAreas };
            var probes = _s.BridgeTerminals.Select(t => ("console " + t.ShipId, Frame.ToUnity(t.GlobalPosition)))
                .Concat(_s.DockBarriers.Select(b => ("dock barrier", Frame.ToUnity(b.GlobalPosition))))
                .Concat(_s.HomeJoinControls.Select(c => ("join " + c.ActionId, Frame.ToUnity(c.GlobalPosition))));
            Debug.Log("[Reach] " + label + " player=" + player.transform.position + " current=" + _s.CurrentShip?.ShipId + " piloted=" + _s.PilotedShip?.ShipId
                + " home@" + _s.HomeShip?.SceneRoot?.GlobalTransform.Origin + " boat@" + _s.LifeboatShip?.SceneRoot?.GlobalTransform.Origin
                + " navmeshes=" + Object.FindObjectsByType<ShipNavMesh>().Length + " :: " + string.Join(" | ", probes.Select(probe =>
                {
                    var pr = new NavMeshPath();
                    bool onMesh = NavMesh.SamplePosition(probe.Item2, out var hit, 2.5f, filter);
                    bool ok = onMesh && NavMesh.CalculatePath(player.transform.position, hit.position, filter, pr);
                    return probe.Item1 + "@" + probe.Item2 + " -> " + (!onMesh ? "off mesh" : ok ? pr.status + " ends@" + pr.corners.LastOrDefault() : "no path");
                })));
        }

        static readonly Vec3[] HatchFaceOffsets = { new Vec3(0, 0, 1.35), new Vec3(0, 0, -1.35), new Vec3(1.35, 0, 0), new Vec3(-1.35, 0, 0) };

        /// <summary>The hatch face on the survivor's side of any closed doors: standable, clear, and whose route ends nearest to it.</summary>
        Vec3? NearestHatchFaceBehindClosedDoors(SealedHatch hatch, NavMeshQueryFilter filter)
        {
            var from = _boot.Host.SceneState.Player.transform.position;
            Vec3? best = null; float bestMiss = float.MaxValue;
            foreach (var direction in HatchFaceOffsets)
            {
                Vec3 candidate = hatch.GlobalPosition + direction;
                Vector3 at = Frame.ToUnity(candidate); at.y = from.y;
                if (!NavMesh.SamplePosition(at, out var sample, 0.5f, filter) || !SpawnClearance.IsClear(sample.position)) continue;
                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(from, sample.position, filter, path) || path.corners.Length == 0) continue;
                float miss = Vector3.Distance(path.corners[path.corners.Length - 1], sample.position);
                if (miss < bestMiss) { bestMiss = miss; best = candidate; }
            }
            return best;
        }

        /// <summary>Per-direction reason a hatch face is unusable: no navmesh, no clearance, a blocker in line, or no complete route.</summary>
        string DescribeHatchFaces(SealedHatch hatch, NavMeshQueryFilter filter)
        {
            var from = _boot.Host.SceneState.Player.transform.position;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var direction in HatchFaceOffsets)
            {
                Vec3 candidate = hatch.GlobalPosition + direction;
                Vector3 at = Frame.ToUnity(candidate); at.y = from.y;
                string reason;
                if (!NavMesh.SamplePosition(at, out var sample, 0.5f, filter)) reason = "no navmesh within 0.5";
                else if (!SpawnClearance.IsClear(sample.position)) reason = "no clearance at " + sample.position;
                else if (Physics.Linecast(sample.position + Vector3.up, Frame.ToUnity(candidate) + Vector3.up, out var hit, SpawnClearance.BlockingMask, QueryTriggerInteraction.Ignore))
                    reason = "blocked by " + hit.collider.transform.parent?.name + "/" + hit.collider.name + "@" + hit.collider.bounds.center;
                else
                {
                    var path = new NavMeshPath();
                    bool ok = NavMesh.CalculatePath(from, sample.position, filter, path);
                    reason = "path " + (ok ? path.status.ToString() : "failed") + (ok && CrossesClosedPortal(path) ? " crosses closed portal" : "")
                        + (ok && path.corners.Length > 0 ? " ends@" + path.corners[path.corners.Length - 1] : "");
                }
                parts.Add(direction + " -> " + reason);
            }
            return "current=" + _s.CurrentShip.ShipId + "; faces: " + string.Join(" | ", parts)
                + "; closed doors near the hatch=" + string.Join(", ", Object.FindObjectsByType<AuthoredPortalRuntime>()
                    .Where(p => !p.isOpen && Vector3.Distance(p.transform.position, Frame.ToUnity(hatch.GlobalPosition)) < 8f)
                    .Select(p => p.portalId + "@" + p.transform.position + " " + p.portalKind))
                + "; enabled obstacles near the hatch=" + string.Join(", ", Object.FindObjectsByType<NavMeshObstacle>()
                    .Where(o => o.enabled && Vector3.Distance(o.transform.TransformPoint(o.center), Frame.ToUnity(hatch.GlobalPosition)) < 8f)
                    .Select(o => o.name + "@" + o.transform.TransformPoint(o.center)));
        }

        bool TryStandingApproach(Vec3 target, float radius, Vector3 from, NavMeshQueryFilter filter, out NavMeshPath path)
        {
            path = null;
            Vector3 at = Frame.ToUnity(target);
            bool deckCue = _s.DeckTransitions.Any(d => d.GlobalPosition == target);
            float searchRadius = deckCue ? Mathf.Min(radius, 1.2f) : radius;
            var directions = new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back,
                new Vector3(1, 0, 1).normalized, new Vector3(-1, 0, 1).normalized,
                new Vector3(1, 0, -1).normalized, new Vector3(-1, 0, -1).normalized };
            for (float distance = 0; distance <= searchRadius + 0.01f; distance += 0.25f)
                foreach (Vector3 direction in directions)
                {
                    Vector3 feet = at + direction * distance;
                    feet.y = from.y;
                    if (!NavMesh.SamplePosition(feet, out var sample, 0.3f, filter)
                        || Mathf.Abs(sample.position.y - at.y) > 1f || !SpawnClearance.IsClear(sample.position)) continue;
                    if (!deckCue && Physics.Linecast(sample.position + Vector3.up, at + Vector3.up,
                        SpawnClearance.BlockingMask, QueryTriggerInteraction.Ignore)) continue;
                    var candidate = new NavMeshPath();
                    if (!NavMesh.CalculatePath(from, sample.position, filter, candidate) || candidate.status != NavMeshPathStatus.PathComplete
                        || CrossesClosedPortal(candidate)) continue;
                    path = candidate;
                    return true;
                }
            return false;
        }

        static bool CrossesClosedPortal(NavMeshPath path)
        {
            // A restored door's physical blocker can precede its asynchronous navigation carve.
            // Do not treat that transient stale path as permission to walk through the door.
            for(int i=1;i<path.corners.Length;i++)
                if(Physics.Linecast(path.corners[i-1]+Vector3.up*.8f,path.corners[i]+Vector3.up*.8f,
                    1<<PhysicsLayers.Portal,QueryTriggerInteraction.Ignore)) return true;
            return false;
        }

        IEnumerator WalkAlong(NavMeshPath path, float radius, int replansLeft = 4)
        {
            var player = _boot.Host.SceneState.Player;
            // A threat the survivor cannot reach or hurt (behind a door jamb, out of melee range) is walked away from after a few
            // seconds, as a person would, instead of standing still against the wall until the stall limit.
            var disengaged = new HashSet<string>(); string fightId = null; float fightSince = 0f; double fightHealth = 0;
            // After a knock-back or a sidestep the survivor can stand with a door jamb between them and the next corner. A person
            // looks around and takes a new route from where they are; so does this walker when it makes no progress.
            Vector3 lastPosition = player.transform.position; float lastProgress = Time.realtimeSinceStartup;
            try
            {
                foreach (Vector3 corner in path.corners.Skip(1))
                {
                    Vector3 waypoint = corner;
                    // The threat NavMesh has a smaller clearance margin than the player's controller skin.
                    // Keep automated steering inside physically standing space, as a player would when turning.
                    SpawnClearance.TryFindClear(corner, floor => Mathf.Abs(floor.bounds.max.y - corner.y) < 0.5f, out waypoint,
                        radius: PlayerController.DefaultCollisionRadius + 0.05f);
                    float deadline = Time.realtimeSinceStartup + 25f;
                    // NavMesh corners sit on the baked agent margin; the CharacterController also has a skin width.
                    // Approach within the interaction radius instead of pressing the capsule into a doorway edge.
                    float reach = corner == path.corners.Last() ? Mathf.Max(radius, 0.2f) : 0.05f;
                    var trail = new List<string>(); float nextSample = 0f;
                    while (Vector2.Distance(new Vector2(player.transform.position.x, player.transform.position.z), new Vector2(waypoint.x, waypoint.z)) > reach)
                    {
                        if (Time.realtimeSinceStartup >= nextSample)
                        {
                            nextSample = Time.realtimeSinceStartup + 1f;
                            var door = Object.FindObjectsByType<AuthoredPortalRuntime>().OrderBy(d => Vector3.Distance(d.transform.position, player.transform.position)).FirstOrDefault();
                            trail.Add(player.transform.position.ToString("F2") + (door != null ? " door " + door.portalId + (door.isOpen ? " open" : " closed") + " d=" + Vector3.Distance(door.transform.position, player.transform.position).ToString("F1") : ""));
                            if (trail.Count > 30) trail.RemoveAt(0);
                        }
                        Assert.Less(Time.realtimeSinceStartup, deadline, "player movement stalled at " + player.transform.position + "; trail(1 s)=" + string.Join(" | ", trail)
                            + " en route to " + waypoint + "; nearby blockers: " + string.Join(", ", Physics.OverlapSphere(player.transform.position + Vector3.up * 0.8f, 1f, SpawnClearance.BlockingMask)
                                .Select(c => c.transform.parent.name + "/" + c.name + "@" + c.bounds.center + " size " + c.bounds.size))
                            + "; route=" + string.Join(" -> ", path.corners.Select(c => c.ToString()))
                            + "; threats=" + string.Join(", ", _s.ThreatManager.Threats.Where(t => t.Health > 0 && t.WorldPosition.Count >= 3)
                                .Select(t => t.InstanceId + "@" + Frame.ToUnity(new Vec3(V.F64(t.WorldPosition[0]), V.F64(t.WorldPosition[1]), V.F64(t.WorldPosition[2])))
                                    + " " + t.State + " hp=" + t.Health))
                            + "; " + SurvivalReport());
                        Assert.IsFalse(_s.SliceComplete, "the player survived exploration; vitals="+GdJson.Stringify(_s.VitalsState.GetSummary())
                            +" oxygen="+_s.OxygenState.Oxygen+" wounds="+GdJson.Stringify(_s.WoundState.GetSummary()));
                        bool fighting=false;
                        Vector3 combatMovement=Vector3.zero;
                        if (_defendWhileExploring)
                        {
                            foreach (var wound in _s.GetTreatableWounds().Cast<GdDict>())
                                if (_s.EvaluateWoundTreatment(RunSession.WOUND_ACTION_BANDAGE,wound.GetString("wound_id")).GetBool("ok")) Assert.IsTrue(_s.BandageWound(wound.GetString("wound_id")).GetBool("ok"));
                                else if (_s.EvaluateWoundTreatment(RunSession.WOUND_ACTION_TREAT,wound.GetString("wound_id")).GetBool("ok")) Assert.IsTrue(_s.TreatWound(wound.GetString("wound_id")).GetBool("ok"));
                            if (_s.VitalsState.Health < 65 && _s.InventoryState.GetQuantity("field_medkit") > 0)
                                _s.UseConsumableItem("field_medkit"); // Medicine cooldowns and actual inventory still gate use.
                            var nearby = _s.ThreatManager.Threats.Where(t => t.Health > 0 && t.WorldPosition.Count >= 3 && !disengaged.Contains(t.InstanceId))
                                .Select(t => new {Threat=t,Position=new Vec3(V.F64(t.WorldPosition[0]),V.F64(t.WorldPosition[1]),V.F64(t.WorldPosition[2]))})
                                .Where(t=>t.Position.DistanceSquaredTo(player.GodotPosition)<System.Math.Pow(_flyJoinedAssembly?System.Math.Max(2.4,t.Threat.AttackRange+.75):2.4,2)
                                    // A threat that has not noticed the survivor and is out of melee reach is walked past, as a person would;
                                    // standing still to "fight" it from behind a door jamb never ends.
                                    && (t.Threat.State!=SynapticSea.Core.Systems.ThreatAIState.STATE_IDLE||t.Position.DistanceSquaredTo(player.GodotPosition)<2.4*2.4)
                                    && !Physics.Linecast(player.transform.position+Vector3.up*1.2f,Frame.ToUnity(t.Position)+Vector3.up,
                                        SpawnClearance.BlockingMask,QueryTriggerInteraction.Ignore))
                                .OrderBy(t=>t.Position.DistanceSquaredTo(player.GodotPosition)).FirstOrDefault();
                            if (nearby != null)
                            {
                                if (fightId != nearby.Threat.InstanceId || nearby.Threat.Health < fightHealth)
                                { fightId = nearby.Threat.InstanceId; fightSince = Time.realtimeSinceStartup; fightHealth = nearby.Threat.Health; }
                                else if (Time.realtimeSinceStartup - fightSince > 6f)
                                { disengaged.Add(nearby.Threat.InstanceId); fightId = null; nearby = null; }
                            }
                            else fightId = null;
                            if (nearby != null)
                            {
                                player.FaceAttackDirection(nearby.Position-player.GodotPosition);
                                _boot.Host.RequestAttack(); // Ordinary reach/LOS/cooldown still decide whether this hits.
                                fighting=true;
                                float distance=(float)nearby.Position.DistanceTo(player.GodotPosition);
                                double escapeSeconds=System.Math.Max(0,nearby.Threat.AttackRange+.35-distance)/System.Math.Max(.1,player.GetEffectiveMoveSpeed())+.1;
                                bool escape=(nearby.Threat.State==SynapticSea.Core.Systems.ThreatAIState.STATE_TELEGRAPH&&nearby.Threat.TelegraphRemaining<=escapeSeconds)
                                    ||(nearby.Threat.State==SynapticSea.Core.Systems.ThreatAIState.STATE_ATTACK&&nearby.Threat.AttackCooldown<=escapeSeconds);
                                Vector3 travel=Frame.ToUnity((escape?player.GodotPosition-nearby.Position:nearby.Position-player.GodotPosition));
                                travel.y=0;travel.Normalize();
                                if(_flyJoinedAssembly&&(escape||distance>2.1f))
                                {
                                    var candidate=player.transform.position+travel*.8f;
                                    if(NavMesh.SamplePosition(candidate,out var safe,.25f,NavMesh.AllAreas)
                                        &&Mathf.Abs(safe.position.y-player.transform.position.y)<.25f&&SpawnClearance.IsClear(safe.position)
                                        &&!Physics.Linecast(player.transform.position+Vector3.up*.8f,safe.position+Vector3.up*.8f,SpawnClearance.BlockingMask,QueryTriggerInteraction.Ignore))
                                        combatMovement=travel;
                                }
                            }
                        }
                        if (Vector3.Distance(player.transform.position, lastPosition) > 0.05f) { lastPosition = player.transform.position; lastProgress = Time.realtimeSinceStartup; }
                        else if (!fighting && replansLeft > 0 && Time.realtimeSinceStartup - lastProgress > 1.5f)
                        {
                            var again = new NavMeshPath();
                            var replanFilter = new NavMeshQueryFilter { agentTypeID = ShipNavMesh.AgentTypeId, areaMask = NavMesh.AllAreas };
                            if (NavMesh.CalculatePath(player.transform.position, path.corners.Last(), replanFilter, again)
                                && again.status == NavMeshPathStatus.PathComplete && again.corners.Length > 1 && !CrossesClosedPortal(again))
                            {
                                Debug.Log("[NaturalWalk] no progress at " + player.transform.position + " toward " + waypoint + "; re-planning from here");
                                player.ClearScriptedMoveDirection();
                                yield return WalkAlong(again, radius, replansLeft - 1);
                                yield break;
                            }
                            lastProgress = Time.realtimeSinceStartup;
                        }
                        Vector3 direction = waypoint - player.transform.position;
                        direction.y = 0;
                        // Slow the final physics step instead of oscillating across a tight corner.
                        // CharacterController movement and collision remain authoritative.
                        float stride = player.GetEffectiveMoveSpeed() * Time.fixedDeltaTime;
                        player.SetScriptedMoveDirection(fighting ? Frame.ToGodot(combatMovement) : Frame.ToGodot(direction.normalized * Mathf.Min(1f,direction.magnitude / Mathf.Max(stride,0.001f))));
                        yield return new WaitForFixedUpdate();
                    }
                }
            }
            finally { player.ClearScriptedMoveDirection(); }
        }

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator WalkRepairTravelBoardAndReturnWithoutFixtureResources() => NaturalExpeditionJourney(false);

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator WalkRepairTravelExploreOddSeedCompositionAndReturnWithoutFixtureResources() => NaturalExpeditionJourney(true);

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator WalkRepairTravelFightRetreatToTheLifeboatForAirThenSearchAndReturnWithoutFixtureResources() => NaturalExpeditionJourney(false, false, true);

        [UnityTest, Timeout(600000)]
        public IEnumerator ReclaimWeldWalkSaveAndLeaveByIndependentCraftWithoutFixtureResources()
        {
            string earned = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../../builds/artifacts/reclamation-earned-world.json"));
            string moored=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../builds/artifacts/reclamation-moored-world.json"));
            string transport=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../builds/artifacts/reclamation-transport-world.json"));
            if(System.Environment.GetCommandLineArgs().Contains("-resumeEarnedTransport"))
            {
                Assert.IsTrue(System.IO.File.Exists(transport),"diagnostic transport replay requires an earned repair/salvage checkpoint");
                _storage.WriteText(SynapticSea.Core.Systems.SaveLoadService.WORLD_SLOT_FILE,System.IO.File.ReadAllText(transport));
                yield return BootPlayable(RunLaunchRequest.ContinueWorld());
                string excursion=_s.Scan().GetArrayOrEmpty("markers").Cast<GdDict>().First(m=>m.GetString("marker_id")!=_s.CurrentShip.MarkerId).GetString("marker_id");
                _defendWhileExploring=true;yield return ReclaimTransportAndJoin(_s.CurrentShip,_s.CurrentShip.MarkerId,excursion);
            }
            else if(System.Environment.GetCommandLineArgs().Contains("-resumeEarnedHomeJoin"))
            {
                Assert.IsTrue(System.IO.File.Exists(moored),"debug join replay requires an earned mooring checkpoint");
                _storage.WriteText(SynapticSea.Core.Systems.SaveLoadService.WORLD_SLOT_FILE,System.IO.File.ReadAllText(moored));
                yield return BootPlayable(RunLaunchRequest.ContinueWorld());
                string excursion=_s.Scan().GetArrayOrEmpty("markers").Cast<GdDict>().First(m=>m.GetString("marker_id")!=_s.CurrentShip.MarkerId).GetString("marker_id");
                _defendWhileExploring=true;yield return CompleteReclaimedJoin(_s.CurrentShip,_s.CurrentShip.MarkerId,excursion);
            }
            else if(System.Environment.GetCommandLineArgs().Contains("-resumeEarnedReclamation"))
            {
                Assert.IsTrue(System.IO.File.Exists(earned), "debug resume requires a checkpoint produced by the complete ordinary journey");
                _storage.WriteText(SynapticSea.Core.Systems.SaveLoadService.WORLD_SLOT_FILE, System.IO.File.ReadAllText(earned));
                yield return BootPlayable(RunLaunchRequest.ContinueWorld());
                string excursion=_s.Scan().GetArrayOrEmpty("markers").Cast<GdDict>().First(m=>m.GetString("marker_id")!=_s.CurrentShip.MarkerId).GetString("marker_id");
                yield return ReclaimCurrentWreck(_s.CurrentShip.MarkerId,excursion);
            }
            else yield return NaturalExpeditionJourney(false, true);
        }

        [UnityTest, Timeout(1800000)]
        public IEnumerator JoinedHomeFlightRequiresEarnedPropulsionAndPreservesAssemblyAndShuttle()
        {
            _flyJoinedAssembly=true;
            _recordJourneyTelemetry=true;
            if(System.Environment.GetCommandLineArgs().Contains("-resumeEarnedWelding"))
            {
                string moored=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../builds/artifacts/reclamation-moored-world.json"));
                Assert.IsTrue(System.IO.File.Exists(moored),"debug resume requires the checkpoint earned by the complete ordinary journey");
                _storage.WriteText(SynapticSea.Core.Systems.SaveLoadService.WORLD_SLOT_FILE,System.IO.File.ReadAllText(moored));
                yield return BootPlayable(RunLaunchRequest.ContinueWorld());
                string excursion=_s.Scan().GetArrayOrEmpty("markers").Cast<GdDict>().First(m=>m.GetString("marker_id")!=_s.CurrentShip.MarkerId).GetString("marker_id");
                _defendWhileExploring=true;yield return CompleteReclaimedJoin(_s.CurrentShip,_s.CurrentShip.MarkerId,excursion);
            }
            else yield return NaturalExpeditionJourney(false,true);
        }

        [UnityTest,Timeout(300000)]
        public IEnumerator InstalledAssemblyFixtureTravelsContinuesAndPreservesIndependentShuttle()
        {
            string path=System.Environment.GetEnvironmentVariable("SYNAPTIC_TEST_EARNED_WORLD");
            if(string.IsNullOrEmpty(path)&&Application.isEditor)
                path=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../builds/artifacts/reclamation-moored-world.json"));
            if(string.IsNullOrEmpty(path)||!System.IO.File.Exists(path))
                Assert.Ignore("Requires a locally earned world snapshot via SYNAPTIC_TEST_EARNED_WORLD. This fixture is not shipped in the game.");
            _storage.WriteText(SynapticSea.Core.Systems.SaveLoadService.WORLD_SLOT_FILE,System.IO.File.ReadAllText(path));
            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            var wreck=_s.CurrentShip;string marker=wreck.MarkerId;
            Assert.AreEqual("moored",((GdDict)wreck.DockingPorts[0]).GetString("connection_kind"));
            // Explicit subsystem fixture: recovery and all physical work/travel remain real,
            // but replenished vitals and supplied installation parts are not natural acquisition evidence.
            _s.VitalsState.Health=_s.VitalsState.MaxHealth;_s.VitalsState.Stamina=_s.VitalsState.MaxStamina;
            _s.VitalsState.Hunger=_s.VitalsState.MaxHunger;_s.VitalsState.Thirst=_s.VitalsState.MaxThirst;
            _installedAssemblyFixture=true;
            yield return CompleteReclaimedJoin(wreck,marker,"");
            _installedAssemblyFixture=false;wreck=_s.VisitedShips[marker];
            var homeDoor=_s.HomeJoinControls.Single(c=>c.ShipId==wreck.ShipId&&c.ActionId=="connection_door"&&ReferenceEquals(c.Parent,_s.HomeShip.SceneRoot));
            yield return WalkTo(homeDoor,.6f);Assert.AreSame(_s.HomeShip,_s.CurrentShip);
            var required=_s.WorkActionDriver.Catalog.GetAction("commission_home_propulsion").GetDictOrEmpty("materials_consumed");
            var before=new Dictionary<string,long>();
            foreach(var part in required)
            {
                string id=V.Str(part.Key);long amount=V.I64(part.Value);
                _s.InventoryState.AddItem(id,System.Math.Max(0,amount-_s.InventoryState.GetQuantity(id)));
                before[id]=_s.InventoryState.GetQuantity(id);
            }
            var install=_s.HomeJoinControls.Single(c=>c.ActionId=="commission_home_propulsion");
            if(install.GlobalPosition.Y>3)
            {
                var transition=_s.DeckTransitions.First(d=>d.DestinationDeck==1);
                yield return WalkTo(transition,2.4f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
            }
            yield return WalkTo(install,1.2f);_s.BeginWorkHold();_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
            Assert.IsTrue(_s.WorkActionDriver.IsWorking(),"supplied fixture parts still require ordinary installation work");
            float deadline=Time.realtimeSinceStartup+45;
            while(_s.WorkActionDriver.IsWorking()&&!_s.SliceComplete&&Time.realtimeSinceStartup<deadline)yield return null;
            _s.EndWorkHold();Assert.IsFalse(_s.SliceComplete);Assert.IsFalse(_s.WorkActionDriver.IsWorking());
            Assert.AreEqual("propulsion:"+_s.HomeShip.ShipId,_s.HomeShip.Mobility.GetString("engine_id"));
            foreach(var part in required)Assert.AreEqual(before[V.Str(part.Key)]-V.I64(part.Value),_s.InventoryState.GetQuantity(V.Str(part.Key)));
            if(_boot.Host.SceneState.Player.transform.position.y>3)
            {
                var transition=_s.DeckTransitions.First(d=>d.DestinationDeck==0);
                yield return WalkTo(transition,2.4f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
            }
            var bridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.HomeShip.ShipId);
            yield return WalkTo(bridge,1.2f);
            if(!ReferenceEquals(_s.HomeShip,_s.PilotedShip)){_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);}
            Assert.AreSame(_s.HomeShip,_s.PilotedShip);
            var destination=_s.SynapticSeaWorld.MarkersInRange(_s.ScannerState.RangeRadius).First(m=>m.MarkerId!=marker);
            var homePose=_s.HomeShip.SceneRoot.GlobalTransform;var wreckPose=wreck.SceneRoot.GlobalTransform;
            string edge=GdJson.Stringify(wreck.DockingPorts);string inventory=GdJson.Stringify(_s.InventoryState.Items);
            var travel=_s.TravelToMarkerId(destination.MarkerId);Assert.IsTrue(travel.GetBool("success"),GdJson.Stringify(travel));yield return FixedSteps(8);
            Assert.AreEqual(destination.Position,_s.HomeSeaPosition);Assert.AreEqual(homePose,_s.HomeShip.SceneRoot.GlobalTransform);
            Assert.AreEqual(wreckPose,wreck.SceneRoot.GlobalTransform);Assert.AreEqual(edge,GdJson.Stringify(wreck.DockingPorts));
            Assert.AreEqual(inventory,GdJson.Stringify(_s.InventoryState.Items));Assert.IsTrue(_s.RequestSave());
            yield return BootPlayable(RunLaunchRequest.ContinueWorld());wreck=_s.VisitedShips[marker];
            Assert.AreEqual(destination.MarkerId,_s.HomeSeaMarkerId);Assert.AreEqual(destination.Position,_s.SynapticSeaWorld.PlayerPosition);
            Assert.Less(wreckPose.Origin.DistanceTo(wreck.SceneRoot.GlobalTransform.Origin),.001);Assert.IsTrue(_s.IsHomeMember(wreck));
            Assert.AreNotSame(_s.HomeShip.SystemsManager,wreck.SystemsManager);
            yield return CaptureHud("installed-home-transit-fixture.png");
            Assert.IsTrue(((GdDict)wreck.DockingPorts[0]).GetBool("connection_open"),"the saved open connection remains open after assembly travel and Continue");
            var joinedDoor=_s.HomeJoinControls.Single(c=>c.ShipId==wreck.ShipId&&c.ActionId=="connection_door"&&ReferenceEquals(c.Parent,wreck.SceneRoot));
            yield return WalkTo(joinedDoor,.6f);Assert.AreSame(wreck,_s.CurrentShip);
            bridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.LifeboatShip.ShipId);
            yield return WalkTo(bridge,1.2f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
            Assert.AreSame(_s.LifeboatShip,_s.PilotedShip);
            var excursion=_s.SynapticSeaWorld.MarkersInRange(_s.ScannerState.RangeRadius).First(m=>m.MarkerId!=marker&&m.MarkerId!=destination.MarkerId);
            Assert.IsTrue(_s.TravelToMarkerId(excursion.MarkerId).GetBool("success"));yield return FixedSteps(8);
            bridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.LifeboatShip.ShipId);yield return WalkTo(bridge,1.2f);
            Assert.IsTrue(_s.TravelHome());yield return FixedSteps(8);Assert.AreEqual(destination.Position,_s.SynapticSeaWorld.PlayerPosition);
            Assert.IsTrue(_s.IsHomeMember(_s.VisitedShips[marker]));Assert.IsTrue(_s.RequestSave());
            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.AreEqual(destination.Position,_s.HomeSeaPosition);Assert.IsTrue(_s.IsHomeMember(_s.VisitedShips[marker]));
            Assert.AreNotSame(_s.HomeShip,_s.VisitedShips[marker]);Assert.IsFalse(_s.SliceComplete);
            Debug.Log("[InstalledAssemblyFixture] passed real weld, install, transit, Continue and independent shuttle return; vitals and parts were fixture-supplied.");
        }

        [UnityTest, Timeout(180000)]
        public IEnumerator WalkToDiagnosticFiniteKitCandidateWithoutPositioningPlayer()
        {
            // Placement qualification only: an empty source, no materials or skill grants.
            AppServices.Ensure();
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "synaptic-entry-anchor-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            try
            {
                var reader = new FileSystemResourceReader(Application.streamingAssetsPath);
                string golden = "res://data/procgen/golden/coherent_ship_001/";
                GdDict slice = GdJson.Parse(reader.ReadText(golden + "gameplay_slice.json"), true) as GdDict;
                slice.GetArrayOrEmpty("loot_containers").Add(new GdDict {
                    { "id", "home_service_kit_01" }, { "kind", "generic_crate" }, { "room_id", "maintenance_01" },
                    { "approach_cell", GdArray.Of(5L, 1L, 1L) }, { "position_offset", GdArray.Of(0.8, 0.05, -0.8) },
                    { "loot_table", "generic_crate" }, { "contents", new GdArray() } });
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "layout.json"), reader.ReadText(golden + "layout.json"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "gameplay_slice.json"), GdJson.Stringify(slice));
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "blueprint.json"), reader.ReadText(golden + "blueprint.json"));
                string resource = "res://" + System.IO.Path.GetRelativePath(Application.streamingAssetsPath, System.IO.Path.Combine(directory, "layout.json")).Replace('\\', '/');
                yield return BootPlayable(new RunLaunchRequest { LayoutOverridePath = resource });
                _s.RefreshDeckTransitions();
                var up = _s.DeckTransitions.First(d => d.DestinationDeck == 1);
                yield return WalkTo(up.GlobalPosition, 2.5f);
                _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8);
                Assert.Greater(_boot.Host.SceneState.Player.GodotPosition.Y, 3.5f);
                var kit = _s.LootContainers.Single(l => l.ContainerId == "home_service_kit_01");
                yield return WalkTo(kit.GlobalPosition, 1.1f);
                Assert.Less(_boot.Host.SceneState.Player.GodotPosition.DistanceTo(kit.GlobalPosition), 1.8);
                _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
                Assert.IsTrue(kit.Searched, "actual normal input searches the source-qualified empty candidate");
                Assert.IsFalse(_s.SliceComplete, "physical acquisition candidate is reached alive");
                Debug.Log("[EarnedEntryAnchor] source=" + kit.ContainerId + " target=" + kit.GlobalPosition
                    + " player=" + _boot.Host.SceneState.Player.GodotPosition + " vitals=" + GdJson.Stringify(_s.VitalsState.GetSummary()));
            }
            finally { System.IO.Directory.Delete(directory, true); }
        }

        [UnityTest, Timeout(240000)]
        public IEnumerator CookWalksFiniteKitRetainedStudyAndLockpick() => EarnedEntryHomeBranch("cook", 60);

        [UnityTest, Timeout(240000)]
        public IEnumerator MedicWalksFiniteKitRetainedStudyAndLockpick() => EarnedEntryHomeBranch("medic", 40);

        static bool _quietReceiverFailureExpected;
        IEnumerator EarnedEntryHomeBranch(string classId, long expectedFabricationXp)
        {
            if (!Application.isEditor && System.Environment.GetCommandLineArgs().Contains("-quietTestResults") && !_quietReceiverFailureExpected)
            {
                // Independently launched evidence players have no Editor receiver; admit only this exact transport error.
                LogAssert.Expect(LogType.Error, "Direct connection to host failed after retrying for 10 seconds. Switching to listen mode.");
                _quietReceiverFailureExpected = true;
            }
            // Fresh diagnostic opt-in; same authored home geometry and real player, no grants/positioning.
            // This branch uses legacy objective power and does not certify F08 paid solo-home or away entry.
            _recordJourneyTelemetry = true;
            yield return BootPlayable(new RunLaunchRequest { ClassId = classId,
                LayoutOverridePath = "res://data/diagnostics/earned-entry-home-v1/layout.json",
                BiomeId = RunLaunchRequest.DefaultBiomeId });
            Assert.AreEqual(classId, _s.PlayerProgression.ClassId);
            Assert.AreEqual(0, _s.PlayerProgression.GetSkillLevel("fabrication"));
            Assert.AreEqual(0, _s.InventoryState.GetQuantity("fabrication_schematic_basic"));
            _s.RefreshDeckTransitions();
            yield return WalkTo(_s.DeckTransitions.First(d => d.DestinationDeck == 1), 2.4f);
            _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8);
            var kit = _s.LootContainers.Single(l => l.ContainerId == "home_service_kit_01");
            yield return WalkTo(kit, 1.1f);
            _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
            Assert.IsTrue(kit.Searched, "actual normal input acquires the authored finite kit");
            Assert.AreEqual(4, _s.InventoryState.GetQuantity("scrap_metal"));
            Assert.AreEqual(4, _s.InventoryState.GetQuantity("wiring_bundle"));
            Assert.AreEqual(1, _s.InventoryState.GetQuantity("wrench"));
            Assert.AreEqual(1, _s.InventoryState.GetQuantity("fabrication_schematic_basic"));
            yield return CaptureHud(classId + "-finite-kit-acquired.png");
            StudyThroughInventory();
            Assert.IsTrue(_s.ManualStudyRunning, "real inventory action starts retained study");
            Assert.IsFalse(_boot.Ui.Inventory.IsOpen(), "inspection closes and live simulation continues");
            float deadline = Time.realtimeSinceStartup + 15;
            while (_s.GetManualStudyState().GetDictOrEmpty("job").GetFloat("progress_seconds") < 2 && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.GreaterOrEqual(_s.GetManualStudyState().GetDictOrEmpty("job").GetFloat("progress_seconds"), 2);
            Assert.IsFalse(_s.PlayerProgression.HasReadBook("fabrication_schematic_basic"));
            _boot.Host.SceneState.Player.SetScriptedMoveDirection(new Vec3(0.3f, 0, 0));
            yield return FixedSteps(12); _boot.Host.SceneState.Player.ClearScriptedMoveDirection();
            Assert.IsFalse(_s.ManualStudyRunning, "ordinary movement pauses study");
            double paused = _s.GetManualStudyState().GetDictOrEmpty("job").GetFloat("progress_seconds");
            Assert.IsTrue(_s.RequestSave(), "partial study and depleted finite source: " + GdJson.Stringify(_s.LastSaveResult));
            Assert.IsTrue(_s.RequestLoad(), "partial study and exact source restore through production admission");
            yield return FixedSteps(8);
            Assert.IsFalse(_s.ManualStudyRunning, "Continue never restores held study input");
            Assert.Greater(paused, 0, "study had progressed before the save");
            Assert.IsTrue(_s.GetManualStudyState().GetDictOrEmpty("job").IsEmpty, "the in-progress study job is session-held and not saved");
            Assert.IsFalse(_s.PlayerProgression.HasReadBook("fabrication_schematic_basic"));
            Assert.IsTrue(_s.LootContainers.Single(l => l.ContainerId == "home_service_kit_01").Searched);
            StudyThroughInventory(); deadline = Time.realtimeSinceStartup + 50;
            while (!_s.PlayerProgression.HasReadBook("fabrication_schematic_basic") && !_s.SliceComplete && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsFalse(_s.SliceComplete, "actual survival continues during study");
            Assert.IsTrue(_s.PlayerProgression.HasReadBook("fabrication_schematic_basic"), GdJson.Stringify(_s.GetManualStudyState()));
            Assert.AreEqual(1, _s.PlayerProgression.GetSkillLevel("fabrication"));
            Assert.AreEqual(expectedFabricationXp, _s.PlayerProgression.GetSkillXp("fabrication"));
            Assert.AreEqual(1, _s.InventoryState.GetQuantity("fabrication_schematic_basic"), "the manual is retained");
            StudyThroughInventory(); yield return FixedSteps(2);
            Assert.AreEqual(expectedFabricationXp, _s.PlayerProgression.GetSkillXp("fabrication"), "repeat UI study awards nothing");
            yield return CaptureHud(classId + "-retained-study-complete.png");
            for (int guard = 0; guard < 16 && !_s.HomeObjectivesComplete; guard++)
            {
                var objective = _s.Interactables.First(o => o.Active && !o.Completed);
                int deck = objective.GlobalPosition.Y > 3 ? 1 : 0;
                if ((_boot.Host.SceneState.Player.GodotPosition.Y > 3 ? 1 : 0) != deck)
                {
                    _s.RefreshDeckTransitions(); yield return WalkTo(_s.DeckTransitions.First(d => d.DestinationDeck == deck), 2.4f);
                    _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8);
                }
                yield return WalkTo(objective); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
            }
            Assert.IsTrue(_s.HomeObjectivesComplete, "current legacy onboarding earns power, not F08 utility completion");
            yield return FixedSteps(8);
            Assert.AreEqual(1.0, _s.ShipSystemsManager.GetSystem("power").Health());
            Assert.AreEqual(1.0, _s.PowerGridState.GetAllocationRatio("stations"));
            var station = _s.CraftingStations.Single(c => c.StationKind == "workbench");
            int stationDeck = station.GlobalPosition.Y > 3 ? 1 : 0;
            if ((_boot.Host.SceneState.Player.GodotPosition.Y > 3 ? 1 : 0) != stationDeck)
            {
                _s.RefreshDeckTransitions(); yield return WalkTo(_s.DeckTransitions.First(d => d.DestinationDeck == stationDeck), 2.4f);
                _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8);
            }
            yield return WalkTo(station, 1.1f); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
            Assert.IsTrue(_boot.Ui.RecipePicker.IsOpen());
            for (int i = 0; i < 100 && _boot.Ui.RecipePicker.GetSelectedId() != "craft_lockpick_set"; i++) _boot.Ui.RecipePicker.MoveSelection(1);
            Assert.AreEqual("craft_lockpick_set", _boot.Ui.RecipePicker.GetSelectedId());
            GdDict result = _boot.Ui.RecipePicker.ConfirmSelection(); Assert.IsTrue(result.GetBool("ok"), GdJson.Stringify(result));
            Assert.AreEqual(2, _s.InventoryState.GetQuantity("scrap_metal"), "exact finite inputs paid once");
            deadline = Time.realtimeSinceStartup + 45;
            while (_s.InventoryState.GetQuantity("lockpick_set") == 0 && !_s.SliceComplete && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.AreEqual(1, _s.InventoryState.GetQuantity("lockpick_set")); Assert.IsFalse(_s.SliceComplete);
            Assert.AreEqual(4, _s.InventoryState.GetQuantity("wiring_bundle"));
            Assert.AreEqual(1, _s.InventoryState.GetQuantity("fabrication_schematic_basic"));
            Assert.IsTrue(_s.RequestSave()); Assert.IsTrue(_s.RequestLoad()); yield return FixedSteps(8);
            Assert.AreEqual(1, _s.InventoryState.GetQuantity("lockpick_set")); Assert.AreEqual(2, _s.InventoryState.GetQuantity("scrap_metal"));
            Assert.AreEqual(expectedFabricationXp, _s.PlayerProgression.GetSkillXp("fabrication"));
            yield return CaptureHud(classId + "-paid-lockpick-restored.png");
            Debug.Log("[EarnedEntryHome] class=" + classId + " source=home_service_kit_01 progression=" + GdJson.Stringify(_s.PlayerProgression.GetSummary())
                + " inventory=" + GdJson.Stringify(_s.InventoryState.GetSummary()) + " vitals=" + GdJson.Stringify(_s.VitalsState.GetSummary())
                + " time=" + _s.WorldTime + " debits=" + GdJson.Stringify(_journeyDebits) + " travel=" + GdJson.Stringify(_s.TravelCapability()));
        }

        void StudyThroughInventory()
        {
            _boot.Ui.Inventory.OpenSelf(_s.InventoryState, _s.EquipmentState);
            var rows = _boot.Ui.Inventory.GetPaneIds(InventoryPanel.PaneSelf);
            int index = rows.FindIndex(id => id == "fabrication_schematic_basic" || id == "stack:fabrication_schematic_basic");
            Assert.GreaterOrEqual(index, 0);
            _boot.Ui.Inventory.SelectRow(InventoryPanel.PaneSelf, index, false, false);
            Assert.Contains("study", _boot.Ui.Inventory.ContextActionsFor(InventoryPanel.PaneSelf, index));
            _boot.Ui.Inventory.InvokeContextAction("study", InventoryPanel.PaneSelf, index);
        }

        IEnumerator NaturalExpeditionJourney(bool cargoFamily, bool reclaim = false, bool retreatForAir = false)
        {
            _recordJourneyTelemetry=true;
            yield return StartThroughTitle();
            _s.RefreshDeckTransitions();
            var up = _s.DeckTransitions.First(d => d.DestinationDeck == 1);
            yield return WalkTo(up.GlobalPosition, 2.5f);
            _boot.Host.SceneState.Player.RequestInteract();
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            Assert.Greater(_boot.Host.SceneState.Player.transform.position.y, 3.5f);
            foreach (var loot in _s.LootContainers.Where(l => !l.Searched).ToList())
            {
                yield return WalkTo(loot.GlobalPosition);
                _boot.Host.SceneState.Player.RequestInteract();
                yield return null;
                Assert.IsTrue(loot.Searched, "normal exploration searches " + loot.ContainerId);
            }
            if (!_s.ToolPickup.Acquired)
            {
                yield return WalkTo(_s.ToolPickup.GlobalPosition, (float)_s.ToolPickup.InteractionRadius - 0.1f);
                _boot.Host.SceneState.Player.RequestInteract();
                yield return null;
                Assert.IsTrue(_s.ToolPickup.Acquired, "the maintenance pump is physically reachable");
            }
            var down = _s.DeckTransitions.First(d => d.DestinationDeck == 0);
            yield return WalkTo(down.GlobalPosition, 2.5f);
            _boot.Host.SceneState.Player.RequestInteract();
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            Assert.Less(_boot.Host.SceneState.Player.transform.position.y, 1.6f, "normal interact returns to the lower deck; handler=" + _s.LastInteractHandlerId);
            var needed = new HashSet<string> { "power", "navigation", "scanners", "propulsion" };
            var repairPoints = _s.RepairPoints.Where(r => r.IsValid && needed.Contains(r.SystemId)).ToList();
            Debug.Log("[NaturalAway] inventory " + GdJson.Stringify(_s.InventoryState.Items));
            foreach (var rp in repairPoints.Where(r => !r.Repaired))
            {
                var sub = rp.TargetManager.GetSystem(rp.SystemId).GetSubcomponent(rp.SubcomponentId);
                Debug.Log("[NaturalAway] " + rp.NodeName + "@" + rp.GlobalPosition + " parts=" + string.Join(",", sub.RequiredParts)
                    + " tools=" + string.Join(",", sub.RequiredTools) + " skill=" + rp.MinSkill);
                foreach (string part in sub.RequiredParts) Assert.Greater(_s.InventoryState.GetQuantity(part), 0, "normal hub supplies " + part);
                foreach (string tool in sub.RequiredTools) Assert.Greater(_s.InventoryState.GetQuantity(tool), 0, "normal hub supplies " + tool);
            }
            long coresBefore = _s.InventoryState.GetQuantity("reactor_core");
            long startingRepair = _s.PlayerProgression.GetSkillLevel("repair");
            for (int guard = 0; guard < 24 && needed.Any(id => !_s.ShipSystemsManager.IsOperational(id)); guard++)
            {
                // Repair is universal (D6): skill sets speed and quality, so any broken required part is a valid next job.
                var next = repairPoints.Where(r => !r.TargetManager.GetSystem(r.SystemId).GetSubcomponent(r.SubcomponentId).IsFunctional())
                    .OrderBy(r => r.MinSkill).FirstOrDefault();
                Assert.IsNotNull(next, "every required remaining repair is available at any skill");
                yield return WalkAndFinishChannel(next.GlobalPosition);
            }
            foreach (string id in needed) Assert.IsTrue(_s.ShipSystemsManager.IsOperational(id), id + " repaired through normal channels");
            Assert.Greater(_s.PlayerProgression.GetSkillLevel("repair"), startingRepair, "completed work earns repair training");
            long earnedRepair = _s.PlayerProgression.GetSkillLevel("repair");
            Assert.Less(_s.InventoryState.GetQuantity("reactor_core"), coresBefore, "normal repairs consume the looted core");
            for (int guard = 0; guard < 16; guard++)
            {
                var seal = _s.BreachSealPoints.FirstOrDefault(p => p.IsValid && !p.Sealed &&
                    V.Bool(_s.HullIntegrityState.Compartments.GetDictOrEmpty(p.CompartmentId).Get("breach_open", false)));
                if (seal == null) break;
                yield return WalkAndFinishChannel(seal.GlobalPosition);
            }
            for (int guard=0;guard<16&&!_s.HomeObjectivesComplete;guard++)
            {
                var objective=_s.Interactables.FirstOrDefault(o=>o.Active&&!o.Completed);
                Assert.IsNotNull(objective,"an onboarding task remains");
                int wantedDeck=objective.GlobalPosition.Y>3?1:0;
                if((_boot.Host.SceneState.Player.GodotPosition.Y>3?1:0)!=wantedDeck)
                {
                    _s.RefreshDeckTransitions();var transfer=_s.DeckTransitions.First(d=>d.DestinationDeck==wantedDeck);
                    yield return WalkTo(transfer.GlobalPosition,2.5f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
                    Assert.AreEqual(wantedDeck,_boot.Host.SceneState.Player.GodotPosition.Y>3?1:0,"real deck transfer reaches the onboarding task");
                }
                yield return WalkTo(objective.GlobalPosition);_boot.Host.SceneState.Player.RequestInteract();yield return null;
            }
            Assert.IsTrue(_s.HomeObjectivesComplete,"finish onboarding before departing");
            Assert.IsFalse(_s.SliceComplete,"onboarding does not terminate this life");Assert.IsNull(_boot.Results);
            earnedRepair=_s.PlayerProgression.GetSkillLevel("repair");
            if(_boot.Host.SceneState.Player.GodotPosition.Y>3)
            {
                _s.RefreshDeckTransitions();var transfer=_s.DeckTransitions.First(d=>d.DestinationDeck==0);
                yield return WalkTo(transfer.GlobalPosition,2.5f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
                Assert.Less(_boot.Host.SceneState.Player.GodotPosition.Y,1.6,"real deck transfer returns to the boat");
            }
            yield return CutBiomatterMooring(_s.HomeShip);
            yield return CutBiomatterMooring(_s.LifeboatShip);
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!_s.PropulsionExpandedState.CanPropel() && Time.realtimeSinceStartup < deadline && !_s.SliceComplete) yield return null;
            Assert.IsFalse(_s.SliceComplete, "natural repairs and seals are survivable");
            Assert.IsTrue(_s.PropulsionExpandedState.CanPropel());
            var bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return WalkTo(bridge, 1.2f);
            _boot.Ui.Scanner.Open();
            var contacts = _s.Scan().GetArrayOrEmpty("markers");
            Assert.Greater(contacts.Count, 0, "repaired navigation sees contacts");
            GdDict travel = null;
            for (int i = 0; i < contacts.Count; i++)
            {
                travel = _boot.Ui.Scanner.ConfirmSelection();
                if (travel.GetBool("success")) break;
                _boot.Ui.Scanner.MoveSelection(1);
            }
            Assert.IsTrue(travel != null && travel.GetBool("success"), "first-away travel: " + GdJson.Stringify(travel));
            yield return null;
            Assert.IsTrue(_s.AwayFromStart);
            Assert.AreEqual("breach_field", _s.CurrentShip.BuiltLayout.GetString("biome_id"));
            Assert.IsFalse(_s.SliceComplete, "first-away travel is not extraction");
            string awayMarker = _s.CurrentShip.MarkerId;
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            foreach (var barrier in _s.DockBarriers.Where(b => b.IsValid && !b.Opened).ToList())
                yield return WalkAndFinishChannel(barrier.GlobalPosition);
            Assert.AreEqual("crowbar", _s.EquipmentState.GetEquipped("primary_hand"), "the physically searched hub cache supplies and auto-equips the existing melee tool");
            var hostile = _s.ThreatManager.Threats.Where(t => t.Health > 0)
                .OrderBy(t => new Vec3(V.F64(t.WorldPosition[0]), V.F64(t.WorldPosition[1]), V.F64(t.WorldPosition[2]))
                    .DistanceSquaredTo(Frame.ToGodot(_boot.Host.SceneState.Player.transform.position))).FirstOrDefault();
            Assert.IsNotNull(hostile, "the generated first-away encounter supplies a real hostile");
            string defeatedId = hostile.InstanceId;
            int combatHits = 0;
            float combatDeadline = Time.realtimeSinceStartup + 45f;
            while (hostile.Health > 0 && !_s.SliceComplete && Time.realtimeSinceStartup < combatDeadline)
            {
                var at = new Vec3(V.F64(hostile.WorldPosition[0]), V.F64(hostile.WorldPosition[1]), V.F64(hostile.WorldPosition[2]));
                yield return WalkTo(at, 2.1f);
                var player = _boot.Host.SceneState.Player;
                at = new Vec3(V.F64(hostile.WorldPosition[0]), V.F64(hostile.WorldPosition[1]), V.F64(hostile.WorldPosition[2]));
                player.FaceAttackDirection(at - player.GodotPosition);
                double before = hostile.Health;
                var hit = _boot.Host.RequestAttack();
                Assert.IsNotNull(hit, "ordinary gameplay attack is enabled");
                if (hostile.Health < before) combatHits++;
                yield return new WaitForSeconds(0.6f);
            }
            Assert.Greater(combatHits, 0, "physical reach/facing/LOS attacks damage the generated encounter");
            Assert.LessOrEqual(hostile.Health, 0, "the real encounter is defeated without fixture damage or spawns");
            Assert.IsFalse(_s.SliceComplete, "the player survives the expedition fight");
            if (retreatForAir)
            {
                // Sensible-survivor route: after the fight, go back to the docked lifeboat's bridge to breathe and recover before looting.
                float healthBeforeRetreat = (float)_s.VitalsState.Health;
                yield return RecoverInOwnedShuttle();
                Assert.GreaterOrEqual(_s.OxygenState.Oxygen, 99, "the docked lifeboat refills suit air: " + SurvivalReport());
                Assert.GreaterOrEqual(_s.VitalsState.Health, healthBeforeRetreat - 1, "retreating does not cost health: " + SurvivalReport());
                Assert.IsTrue(_s.AwayFromStart, "the retreat stays aboard the docked lifeboat at the wreck");
            }
            var awayLoot = _s.LootContainers.Where(l => l.IsValid && !l.Searched)
                .OrderBy(l => l.GlobalPosition.DistanceSquaredTo(Frame.ToGodot(_boot.Host.SceneState.Player.transform.position))).FirstOrDefault();
            Assert.IsNotNull(awayLoot, "the first wreck has an interior loot target");
            yield return WalkTo(awayLoot.GlobalPosition, (float)awayLoot.InteractionRadius - 0.2f);
            for (int attempt = 0; attempt < 6 && !awayLoot.Searched; attempt++)
            {
                var door = _s.FocusedAuthoredPortal(Frame.ToGodot(_boot.Host.SceneState.Player.transform.position));
                bool closedDoor = door != null && !door.IsOpen;
                _boot.Host.SceneState.Player.RequestInteract();
                for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
                Assert.IsTrue(_s.AwayFromStart, "looting does not accidentally exit the wreck");
                if (_s.LastInteractHandlerId == "authored_portal")
                    Assert.IsTrue(closedDoor && door.IsOpen, "an adjacent door opened instead of stealing the loot interaction: " + door?.PortalId);
                else break;
            }
            Assert.IsTrue(awayLoot.Searched, "physically boarded and searched the generated wreck; handler=" + _s.LastInteractHandlerId
                + "; player=" + _boot.Host.SceneState.Player.transform.position + "; target=" + awayLoot.GlobalPosition);
            CaptureGameCamera("natural-first-away.png");
            bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return WalkTo(bridge, 1.2f);
            Assert.IsTrue(_s.TravelHome(), "return from the first expedition");
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            Assert.IsFalse(_s.AwayFromStart);
            Assert.IsTrue(_s.ShipSystemsManager.IsOperational("propulsion"), "repairs survive return");
            Assert.IsTrue(_s.RequestSave());
            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.IsFalse(_s.AwayFromStart);
            Assert.IsTrue(_s.HomeObjectivesComplete,"onboarding state survives the first-away round trip and Continue");
            foreach (string id in needed) Assert.IsTrue(_s.ShipSystemsManager.IsOperational(id), id + " survives Continue");
            Assert.AreEqual(earnedRepair, _s.PlayerProgression.GetSkillLevel("repair"), "earned training survives Continue");
            Assert.AreEqual(1, _s.InventoryState.GetQuantity("portable_oxygen_pump"), "acquired pump survives Continue");
            Assert.IsTrue(_s.LootContainers.Single(l => l.ContainerId == "start_supply_a").Searched, "the finite maintenance cache cannot pay again after Continue");
            Assert.IsTrue(_s.VisitedShips[awayMarker].LootedContainerIds.Contains(awayLoot.ContainerId), "searched wreck loot remains recorded after returning and Continue");
            Assert.IsFalse(_s.VisitedShips[awayMarker].CombatSummary.GetArrayOrEmpty("threats").Cast<GdDict>().Any(t => t.GetString("instance_id") == defeatedId),
                "normal death sweep removes the defeated encounter from the saved runtime");
            Assert.AreEqual("crowbar", _s.EquipmentState.GetEquipped("primary_hand"), "acquired combat tool survives Continue");
            bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return WalkTo(bridge, 1.2f);
            Assert.IsTrue(_s.TravelToMarkerId(awayMarker).GetBool("success"), "revisit the saved wreck through guarded travel");
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            Assert.IsFalse(_s.ThreatManager.Threats.Any(t => t.InstanceId == defeatedId), "defeated generated enemy does not respawn on actual saved-wreck restoration");
            bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return WalkTo(bridge, 1.2f);
            Assert.IsTrue(_s.TravelHome());
            Assert.IsFalse(_s.SliceComplete, "returning continues the existing life");
            if (retreatForAir) yield break;
            // Scanner rows expose IDs and size, not the full saved marker seed. Resolve family
            // from the same in-range world markers, while requiring a selectable scanner row.
            var availableMarkers = _s.SynapticSeaWorld.MarkersInRange(_s.ScannerState.RangeRadius).ToDictionary(m=>m.MarkerId);
            var nextContact = _s.Scan().GetArrayOrEmpty("markers").Cast<GdDict>()
                .FirstOrDefault(m => m.GetInt("size_class") >= 1 && m.GetInt("size_class") <= 2
                    && !_s.VisitedShips.ContainsKey(m.GetString("marker_id"))
                    && availableMarkers.TryGetValue(m.GetString("marker_id"),out var candidate)
                    && PurposefulExpedition.CrossHull(candidate.SeedValue) == cargoFamily
                    && (!reclaim || candidate.Condition == 0));
            Assert.IsNotNull(nextContact, "normal scanner exposes a new larger wreck after one onboarding round trip");
            bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return WalkTo(bridge, 1.2f);
            string nextId = nextContact.GetString("marker_id");
            var nextTravel = _s.TravelToMarkerId(nextId);
            Assert.IsTrue(nextTravel.GetBool("success"), "normal subsequent scanner travel: " + GdJson.Stringify(nextTravel));
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            Assert.AreEqual(SynapticSea.Core.Procgen.ConstrainedExpedition.Profile, _s.CurrentShip.Blueprint.GenerationProfile);
            Assert.AreEqual(SynapticSea.Core.Procgen.ConstrainedExpedition.Profile, _s.CurrentShip.BuiltLayout.GetString("generation_profile"));
            Debug.Log("[NaturalExpeditionRoute] second new destination=" + nextId + " size=" + nextContact.GetInt("size_class")
                + " rooms=" + _s.CurrentShip.BuiltLayout.GetArrayOrEmpty("rooms").Count);
            Assert.IsTrue(_s.RequestSave());
            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.AreEqual(SynapticSea.Core.Procgen.ConstrainedExpedition.Profile, _s.CurrentShip.Blueprint.GenerationProfile, "actual Continue keeps expanded destination");
            Assert.AreEqual(nextId, _s.CurrentShip.MarkerId);
            if (reclaim)
            {
                string earned=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../builds/artifacts/reclamation-earned-world.json"));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(earned));
                System.IO.File.WriteAllText(earned,_storage.ReadText(SynapticSea.Core.Systems.SaveLoadService.WORLD_SLOT_FILE));
                yield return ReclaimCurrentWreck(nextId, awayMarker);
                yield break;
            }
            if (System.Environment.GetCommandLineArgs().Contains("-profileExpeditionFrames"))
            {
                Assert.AreEqual("constrained_composition",_s.CurrentShip.BuiltLayout.GetString("topology_family"));
                _defendWhileExploring = true;
                yield return CaptureHud(cargoFamily ? "constrained-odd-arrival.png" : "constrained-even-arrival.png");
                yield return ProfileLiveExpedition();
                yield return ReviewPurposefulRooms();
            }
            bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return WalkTo(bridge, 1.2f);
            Assert.IsTrue(_s.TravelHome(), "expanded expedition returns through existing travel checks");
            Debug.Log("[DepartureBeforeSave] "+GdJson.Stringify(_s.GetShipSystemsExpandedSummary()));
            Assert.IsTrue(_s.RequestSave()); yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.IsFalse(_s.AwayFromStart);
            Assert.AreEqual(ConstrainedExpedition.Profile, _s.VisitedShips[nextId].Blueprint.GenerationProfile);
            Assert.AreEqual(cargoFamily,PurposefulExpedition.CrossHull(_s.VisitedShips[nextId].Blueprint.SeedValue),"normal return/Continue retains the saved seed that selects the hull family");
            _defendWhileExploring = false;
        }

        IEnumerator UseEarnedProvisions()
        {
            for(int i=0;i<8&&_s.VitalsState.Hunger<90&&_s.InventoryState.GetQuantity("ration_pack")>0;i++)
                Assert.IsTrue(_s.UseConsumableItem("ration_pack").GetBool("ok"),"eat physically searched finite crew rations");
            for(int i=0;i<8&&_s.VitalsState.Thirst<90&&_s.InventoryState.GetQuantity("purified_water")>0;i++)
                Assert.IsTrue(_s.UseConsumableItem("purified_water").GetBool("ok"),"drink physically searched finite crew water");
            yield return null;
        }

        IEnumerator CutBiomatterMooring(SynapticSea.Core.Systems.ShipInstance ship)
        {
            _s.RebuildHomeJoinControls();
            var control=_s.HomeJoinControls.Single(c=>c.ShipId==ship.ShipId && c.ActionId=="cut_web_attachment");
            Debug.Log("[NaturalReclamation] cut mooring " + ship.ShipId + " web="+control.Web.Coverage+" hull="+control.Hull.AverageIntegrity());
            yield return WalkTo(control, 1.2f);
            string inventory=GdJson.Stringify(_s.InventoryState.Items);
            _s.BeginWorkHold(); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(12);
            Assert.IsTrue(_s.WorkActionDriver.IsWorking(), "ordinary held interaction starts cut-free work; handler="+_s.LastInteractHandlerId);
            _s.EndWorkHold(); Assert.IsTrue(_s.CancelWorkAction());
            Assert.IsTrue(control.Web.AttachedToWeb, "interrupted cutting does not detach the vessel");
            Assert.AreEqual(inventory,GdJson.Stringify(_s.InventoryState.Items), "cutting interruption consumes no inventory");
            float restDeadline=Time.realtimeSinceStartup+30;
            while(_s.VitalsState.Stamina<_s.VitalsState.MaxStamina*.95 && !_s.SliceComplete && Time.realtimeSinceStartup<restDeadline)yield return null;
            double integrity=control.Hull.AverageIntegrity();
            _s.BeginWorkHold();_boot.Host.SceneState.Player.RequestInteract();
            float deadline=Time.realtimeSinceStartup+20;
            while(_s.WorkActionDriver.IsWorking()&&!_s.SliceComplete&&Time.realtimeSinceStartup<deadline)yield return null;
            _s.EndWorkHold();
            Assert.IsFalse(_s.WorkActionDriver.IsWorking(),"cut-free channel finishes within its bounded duration");
            Assert.IsFalse(control.Web.AttachedToWeb,"successful work detaches only this vessel's web mooring");
            Assert.LessOrEqual(control.Hull.AverageIntegrity(),integrity,"cutting does not magically restore hull integrity");
            Assert.AreEqual(inventory,GdJson.Stringify(_s.InventoryState.Items),"existing plasma cutter action does not grant or consume salvage");
            Assert.IsFalse(_s.SliceComplete,"survive the cut-free channel");
        }

        IEnumerator ReclaimCurrentWreck(string marker, string excursion)
        {
            _defendWhileExploring = true;
            var wreck = _s.CurrentShip;
            Debug.Log("[NaturalReclamation] arrived " + marker + " systems=" + GdJson.Stringify(wreck.SystemsManager.GetSummary()));
            foreach (var barrier in _s.DockBarriers.Where(b => b.IsValid && !b.Opened).ToList())
                yield return WalkAndFinishChannel(barrier.GlobalPosition);
            yield return CutBiomatterMooring(wreck);
            // Stop the existing hull leak before touring the interior and opening its room doors.
            // This uses the ordinary finite sealant and channel, not a pressure or health fixture.
            foreach(var seal in _s.BreachSealPoints.Where(p=>_flyJoinedAssembly&&p.IsValid&&!p.Sealed
                &&wreck.GetHull().Compartments.GetDictOrEmpty(p.CompartmentId).GetBool("breach_open")).ToList())
                yield return WalkAndFinishChannel(seal.GlobalPosition);
            var medical = _s.LootContainers.FirstOrDefault(l => l.IsValid && !l.Searched && l.ContainerId.Contains("medical"));
            Assert.IsNotNull(medical, "the purposeful expedition exposes its finite medical cache");
            yield return WalkTo(medical); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8);
            Assert.IsTrue(medical.Searched); Assert.Greater(_s.InventoryState.GetQuantity("field_medkit"), 0, "ordinary medical-room exploration acquires usable care");
            foreach(var wound in _s.GetTreatableWounds().Cast<GdDict>().Where(w=>w.GetBool("can_bandage")))
                Assert.IsTrue(_s.BandageWound(wound.GetString("wound_id")).GetBool("ok"));
            if(_s.VitalsState.Health<80)Assert.IsTrue(_s.UseConsumableItem("field_medkit").GetBool("ok"));
            yield return RecoverInOwnedShuttle();
            var crew=_s.LootContainers.Single(l=>l.IsValid&&l.ContainerId=="loot_crew_quarters_01");
            // The home and first-wreck emergency stores (Phase 1.3) already put food and water in the bag; the crew store must add exactly its 8 + 8.
            long rationsBeforeCrew=_s.InventoryState.GetQuantity("ration_pack"),waterBeforeCrew=_s.InventoryState.GetQuantity("purified_water");
            yield return WalkTo(crew);
            for(int attempt=0;attempt<6&&!crew.Searched;attempt++)
            { _boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);if(_s.LastInteractHandlerId!="authored_portal")break; }
            Assert.IsTrue(crew.Searched,"explore the real crew stores before prolonged repair work");
            Assert.AreEqual(rationsBeforeCrew+8,_s.InventoryState.GetQuantity("ration_pack"));Assert.AreEqual(waterBeforeCrew+8,_s.InventoryState.GetQuantity("purified_water"));
            yield return UseEarnedProvisions();
            if (_s.OxygenState.Oxygen < 70) yield return RecoverInOwnedShuttle();
            for(int guard=0;guard<16;guard++)
            {
                yield return UseEarnedProvisions();
                if (_s.OxygenState.Oxygen < 70) yield return RecoverInOwnedShuttle();
                var fire=_s.FireSuppressionPoints.FirstOrDefault(p=>p.IsValid&&!p.Extinguished);
                if(fire==null)break;
                if(!_s.ExtinguisherState.HasChargeForUse())
                {
                    yield return UseEarnedProvisions();
                    if (_s.OxygenState.Oxygen < 70 || _s.SanityState.Sanity < 50) yield return RecoverInOwnedShuttle();
                    var recharge=_s.ExtinguisherRechargePort;
                    Assert.IsNotNull(recharge,"a real powered recharge station supports repeated suppression");
                    yield return WalkTo(recharge,1.2f);
                    float chargeDeadline=Time.realtimeSinceStartup+25;
                    while(_s.ExtinguisherState.Charge<_s.ExtinguisherState.MaxCharge && !_s.SliceComplete && Time.realtimeSinceStartup<chargeDeadline)yield return null;
                    Assert.IsFalse(_s.SliceComplete,"survive physical extinguisher recharging; vitals="+GdJson.Stringify(_s.VitalsState.GetSummary())+" oxygen="+_s.OxygenState.Oxygen+" sanity="+_s.SanityState.Sanity+" load="+_s.InventoryState.GetLoadRatio()+" powered="+recharge.Powered+" charge="+_s.ExtinguisherState.Charge);
                    Assert.GreaterOrEqual(_s.ExtinguisherState.Charge,_s.ExtinguisherState.MaxCharge-.01,"normal powered station refills the real tool");
                    fire=_s.FireSuppressionPoints.FirstOrDefault(p=>p.IsValid&&!p.Extinguished);
                    if(fire==null)break;
                }
                Debug.Log("[NaturalReclamation] suppress " + fire.CompartmentId + " oxygen=" + _s.OxygenState.Oxygen+" vitals="+GdJson.Stringify(_s.VitalsState.GetSummary())+" load="+_s.InventoryState.GetLoadRatio());
                yield return WalkAndFinishChannel(fire.GlobalPosition);
                // A salvage trip uses the already repaired, physically reachable shuttle
                // as shelter; no fixture refill or suit-budget bypass.
                if (_s.OxygenState.Oxygen < 70)
                {
                    var shelter = _s.BridgeTerminals.Single(t => t.ShipId == _s.LifeboatShip.ShipId);
                    yield return WalkTo(shelter, 1.2f);
                    float refillDeadline = Time.realtimeSinceStartup + 40;
                    while (_s.OxygenState.Oxygen < 99 && !_s.SliceComplete && Time.realtimeSinceStartup < refillDeadline) yield return null;
                    Assert.IsFalse(_s.SliceComplete, "ordinary shuttle shelter permits recovery");
                    Assert.GreaterOrEqual(_s.OxygenState.Oxygen, 99, "suit refills through actual boat occupancy; occupied="+_s.CurrentOccupancy?.ShipId+" player="+_boot.Host.SceneState.Player.GodotPosition+" boat="+_s.LifeboatShip.SceneRoot.GlobalTransform);
                }
            }
            Assert.IsFalse(_s.FireSuppressionPoints.Any(p=>p.IsValid&&!p.Extinguished),"normal salvage first extinguishes fires that would destroy the vessel's systems");
            Debug.Log("[NaturalReclamation] resources=" + GdJson.Stringify(_s.InventoryState.Items) + " health=" + _s.VitalsState.Health);
            var maintenance = _s.LootContainers.Single(l=>l.IsValid && l.ContainerId=="loot_maintenance_01");
            yield return WalkTo(maintenance); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8);
            Assert.IsTrue(maintenance.Searched, "finite ordinary maintenance supplies support recovery");
            foreach (var repair in _s.RepairPoints.Where(r=>r.IsValid && (r.SystemId=="power" || r.SystemId=="life_support")).OrderBy(r=>_flyJoinedAssembly&&r.SystemId=="life_support"?0:1).ToList())
                if (!repair.TargetManager.GetSystem(repair.SystemId).GetSubcomponent(repair.SubcomponentId).IsFunctional())
                {
                    yield return UseEarnedProvisions();
                    if (_s.SanityState.Sanity < 50) yield return RecoverInOwnedShuttle();
                    Debug.Log("[NaturalReclamation] repair " + repair.SystemId + "." + repair.SubcomponentId + " vitals=" + GdJson.Stringify(_s.VitalsState.GetSummary()) + " sanity="+_s.SanityState.Sanity+" oxygen="+_s.OxygenState.Oxygen+" load="+_s.InventoryState.GetLoadRatio());
                    yield return WalkAndFinishChannel(repair.GlobalPosition);
                }
            Assert.IsTrue(wreck.SystemsManager.IsOperational("power"), "repair earned power before claiming the bridge");
            for (int guard=0;guard<8;guard++)
            {
                var seal=_s.BreachSealPoints.FirstOrDefault(p=>p.IsValid&&!p.Sealed && wreck.GetHull().Compartments.GetDictOrEmpty(p.CompartmentId).GetBool("breach_open"));
                if(seal==null)break;
                Debug.Log("[NaturalReclamation] seal hull " + seal.CompartmentId + " integrity=" + wreck.GetHull().AverageIntegrity());
                yield return WalkAndFinishChannel(seal.GlobalPosition);
            }
            Assert.IsFalse(wreck.GetWeb().AttachedToWeb, "departure follows real cut-free work");
            Assert.AreEqual(0, wreck.GetHull().GetBreachCount(), "actual sealant repairs the breached vessel before claiming departure");
            if(_flyJoinedAssembly)yield return SearchExistingCrewCare();
            var workingBridge=_s.BridgeTerminals.Single(t=>t.ShipId==wreck.ShipId);
            yield return WalkTo(workingBridge,1.2f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
            Assert.AreSame(wreck,_s.PilotedShip,"earned repair permits bridge claim before hauling bulk salvage");
            if(_s.InventoryState.GetLoadRatio()>.85)yield return StowUnneededHaul(wreck);
            foreach (var loot in _s.LootContainers.Where(l => l.IsValid && !l.Searched)
                .OrderBy(l => l.ContainerId.Contains("cargo") ? 0 : 1).ToList())
            {
                if (_s.InventoryState.GetQuantity("plating") >= 2) break;
                yield return WalkTo(loot);
                for(int attempt=0;attempt<6&&!loot.Searched;attempt++)
                {
                    _boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
                    if(_s.LastInteractHandlerId!="authored_portal")break;
                }
                Assert.IsTrue(loot.Searched, "ordinary wreck exploration acquires reclamation materials from " + loot.ContainerId + "; handler="+_s.LastInteractHandlerId);
                Debug.Log("[NaturalReclamation] searched=" + loot.ContainerId + " resources=" + GdJson.Stringify(_s.InventoryState.Items) + " health=" + _s.VitalsState.Health + " radiation="+_s.RadiationState.Radiation+" wounds="+GdJson.Stringify(_s.WoundState.GetSummary()));
                if(_s.InventoryState.GetLoadRatio()>.85)yield return StowUnneededHaul(wreck);
            }
            Assert.GreaterOrEqual(_s.InventoryState.GetQuantity("plating"), 2, "finite salvage or existing crafting must supply the two real hull plates; inventory=" + GdJson.Stringify(_s.InventoryState.Items));
            Assert.IsTrue(_s.RequestSave());
            if(Application.isEditor)
            {
                string checkpoint=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../builds/artifacts/reclamation-transport-world.json"));
                System.IO.File.WriteAllText(checkpoint,_storage.ReadText(SynapticSea.Core.Systems.SaveLoadService.WORLD_SLOT_FILE));
            }
            yield return ReclaimTransportAndJoin(wreck,marker,excursion);
        }

        IEnumerator ReclaimTransportAndJoin(SynapticSea.Core.Systems.ShipInstance wreck,string marker,string excursion)
        {
            Debug.Log("[NaturalReclamation] transport health="+_s.VitalsState.Health+" oxygen="+_s.OxygenState.Oxygen+" hostiles="+_s.ThreatManager.Threats.Count);
            var bridge = _s.BridgeTerminals.Single(t => t.ShipId == wreck.ShipId);
            yield return WalkTo(bridge, 1.2f);
            for(int attempt=0;attempt<6&&_s.PilotedShip!=wreck;attempt++)
            {
                _boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
                if(_s.LastInteractHandlerId!="authored_portal")break;
            }
            Assert.AreSame(wreck, _s.PilotedShip, "normal bridge login claims the working vessel; handler="+_s.LastInteractHandlerId+" target="+bridge.GlobalPosition+" player="+_boot.Host.SceneState.Player.GodotPosition);
            Debug.Log("[NaturalReclamation] claimed bridge health="+_s.VitalsState.Health+" oxygen="+_s.OxygenState.Oxygen);
            var hold=_s.CargoHoldControls.Single(c=>c.IsValid&&c.CarrierId==wreck.ShipId);
            yield return WalkTo(hold,.5f);
            var boatPose=_s.LifeboatShip.SceneRoot.GlobalTransform;
            var boatParent=_s.LifeboatShip.ParentShip;
            for(int attempt=0;attempt<6&&!_boot.Ui.Inventory.IsOpen();attempt++)
            { _boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8); }
            Assert.IsTrue(_boot.Ui.Inventory.IsOpen(),"physical hold interaction opens the normal transfer panel");
            Assert.AreEqual("cargo_deposit",_s.LastInteractHandlerId,"cargo focus and dispatch agree");
            Assert.AreSame(boatParent,_s.LifeboatShip.ParentShip,"opening cargo must not bay or launch the shuttle");
            Assert.AreEqual(boatPose,_s.LifeboatShip.SceneRoot.GlobalTransform,"opening cargo must not move the shuttle");
            long scrap=_s.InventoryState.GetQuantity("scrap_metal");
            Assert.AreEqual(scrap,_boot.Ui.Inventory.TransferQuantity(SynapticSea.UI.InventoryPanel.PaneSelf,"scrap_metal",scrap));
            _boot.Ui.Inventory.Close();yield return FixedSteps(8);
            Assert.LessOrEqual(_s.InventoryState.GetLoadRatio(),1,"stow unnecessary haul without consuming welding plates");
            yield return RecoverInOwnedShuttle();yield return UseEarnedProvisions();
            yield return WalkTo(bridge,1.2f);
            Assert.IsTrue(_s.TravelCapability().GetBool("success"), GdJson.Stringify(_s.TravelCapability()));
            Assert.IsTrue(_s.TravelHome(), "owned repaired wreck returns with its carried boat"); yield return FixedSteps(10);
            Assert.IsTrue(_s.RequestSave());
            if(Application.isEditor)
            {
                string checkpoint=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../builds/artifacts/reclamation-moored-world.json"));
                System.IO.File.WriteAllText(checkpoint,_storage.ReadText(SynapticSea.Core.Systems.SaveLoadService.WORLD_SLOT_FILE));
            }
            yield return CompleteReclaimedJoin(wreck,marker,excursion);
        }

        IEnumerator CompleteReclaimedJoin(SynapticSea.Core.Systems.ShipInstance wreck,string marker,string excursion)
        {
            yield return RecoverInOwnedShuttle();yield return UseEarnedProvisions();
            var weld = _s.HomeJoinControls.Single(c => c.ShipId == wreck.ShipId && c.ActionId == "secure_connection");
            yield return WalkTo(weld, 1.2f);
            long plates = _s.InventoryState.GetQuantity("plating");
            Debug.Log("[AssemblyWelding] arrival vitals="+GdJson.Stringify(_s.VitalsState.GetSummary()));
            float initialRestDeadline=Time.realtimeSinceStartup+120;
            while(_flyJoinedAssembly&&_s.VitalsState.Stamina<_s.VitalsState.MaxStamina*.95&&!_s.SliceComplete&&Time.realtimeSinceStartup<initialRestDeadline)yield return null;
            Assert.IsFalse(_s.SliceComplete,"ordinary rest before welding remains survivable");
            if(_flyJoinedAssembly)Assert.GreaterOrEqual(_s.VitalsState.Stamina,_s.VitalsState.MaxStamina*.95,"recover actual work stamina without a fixture refill");
            _s.BeginWorkHold();
            for(int attempt=0;attempt<4&&!_s.WorkActionDriver.IsWorking();attempt++)
            {
                _boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(12);
                Debug.Log("[AssemblyWelding] handler="+_s.LastInteractHandlerId+" focus="+_s.CanFocusInteractable(weld)+" player="+_boot.Host.SceneState.Player.GodotPosition+" target="+weld.GlobalPosition+" status="+_s.WorkActionDriver.GetStatus()+" vitals="+GdJson.Stringify(_s.VitalsState.GetSummary())+" repair="+_s.PlayerProgression.GetSkillLevel("repair")+" inventory="+GdJson.Stringify(_s.InventoryState.Items));
                if(_s.LastInteractHandlerId!="authored_portal")break;
            }
            Assert.IsTrue(_s.WorkActionDriver.IsWorking(), "ordinary held interaction begins timed welding; handler="+_s.LastInteractHandlerId+" focus="+_s.CanFocusInteractable(weld));
            _s.EndWorkHold(); Assert.IsTrue(_s.CancelWorkAction());
            Assert.AreEqual(plates, _s.InventoryState.GetQuantity("plating"), "interruption does not consume plates");
            Assert.AreEqual("moored", ((GdDict)wreck.DockingPorts[0]).GetString("connection_kind"));
            yield return RecoverInOwnedShuttle();yield return UseEarnedProvisions();
            float restDeadline = Time.realtimeSinceStartup + (_flyJoinedAssembly?120:30);
            while (_s.VitalsState.Stamina < _s.VitalsState.MaxStamina * .95 && !_s.SliceComplete && Time.realtimeSinceStartup < restDeadline) yield return null;
            Assert.GreaterOrEqual(_s.VitalsState.Stamina, _s.VitalsState.MaxStamina * .95, "ordinary rest restores enough stamina for welding");
            yield return WalkTo(weld,1.2f);
            if(_s.VitalsState.Health<80&&_s.InventoryState.GetQuantity("field_medkit")>0)Assert.IsTrue(_s.UseConsumableItem("field_medkit").GetBool("ok"));
            _s.BeginWorkHold(); _boot.Host.SceneState.Player.RequestInteract();
            float deadline = Time.realtimeSinceStartup + 30;
            float reportAt=0;
            while (_s.WorkActionDriver.IsWorking() && Time.realtimeSinceStartup < deadline && !_s.SliceComplete)
            {
                if(Time.realtimeSinceStartup>=reportAt)
                {
                    Debug.Log("[NaturalReclamation] weld progress="+_s.WorkActionDriver.ProgressRatio()+" held="+_s.IsWorkInteractHeld+" paused="+_boot.Host.Paused
                        +" vitals="+GdJson.Stringify(_s.VitalsState.GetSummary())+" oxygen="+_s.OxygenState.Oxygen+" sanity="+_s.SanityState.Sanity+" load="+_s.InventoryState.GetLoadRatio());
                    reportAt=Time.realtimeSinceStartup+5;
                }
                yield return null;
            }
            _s.EndWorkHold();Assert.IsFalse(_s.SliceComplete,"survive welding: vitals="+GdJson.Stringify(_s.VitalsState.GetSummary())+" oxygen="+_s.OxygenState.Oxygen+" sanity="+_s.SanityState.Sanity);
            Assert.IsFalse(_s.WorkActionDriver.IsWorking(),"bounded weld progress="+_s.WorkActionDriver.ProgressRatio()+" paused="+_boot.Host.Paused);
            Assert.AreEqual("secured", ((GdDict)wreck.DockingPorts[0]).GetString("connection_kind"));
            Assert.AreEqual(plates - 2, _s.InventoryState.GetQuantity("plating"), "successful work consumes exactly two plates once");
            yield return FixedSteps(10);
            var doors = _s.HomeJoinControls.Where(c => c.ShipId == wreck.ShipId && c.ActionId == "connection_door").ToList();
            var wreckDoor = doors.Single(c => ReferenceEquals(c.Parent, wreck.SceneRoot));
            var homeSide = doors.Single(c => ReferenceEquals(c.Parent, _s.HomeShip.SceneRoot));
            Assert.IsTrue(Physics.Linecast(Frame.ToUnity(wreckDoor.GlobalPosition) + Vector3.up * .5f,
                Frame.ToUnity(homeSide.GlobalPosition) + Vector3.up * .5f, SpawnClearance.BlockingMask, QueryTriggerInteraction.Ignore),
                "the secured connection starts physically closed; welding must not bypass the door");
            yield return WalkTo(wreckDoor, 1.2f); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(10);
            Assert.IsTrue(((GdDict)wreck.DockingPorts[0]).GetBool("connection_open"));
            var homeDoor = _s.HomeJoinControls.Single(c => c.ShipId == wreck.ShipId && c.ActionId == "connection_door" && ReferenceEquals(c.Parent, _s.HomeShip.SceneRoot));
            yield return WalkTo(homeDoor, .6f); Assert.AreSame(_s.HomeShip, _s.CurrentShip, "actual passage switches to home services");
            float refillDeadline=Time.realtimeSinceStartup+40;
            while(_s.OxygenState.Oxygen<99&&!_s.SliceComplete&&Time.realtimeSinceStartup<refillDeadline)yield return null;
            Assert.IsFalse(_s.SliceComplete,"home's own repaired services permit ordinary recovery");Assert.GreaterOrEqual(_s.OxygenState.Oxygen,99);
            wreckDoor = _s.HomeJoinControls.Single(c => c.ShipId == wreck.ShipId && c.ActionId == "connection_door" && ReferenceEquals(c.Parent, wreck.SceneRoot));
            yield return WalkTo(wreckDoor, .6f); Assert.AreSame(wreck, _s.CurrentShip, "reverse walking returns to recovered local services");
            yield return CaptureHud(_installedAssemblyFixture?"installed-connection-fixture.png":"natural-reclaimed-passage.png");
            Assert.IsTrue(_s.RequestSave()); yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            wreck = _s.CurrentShip;
            Assert.AreEqual(marker, wreck.MarkerId); Assert.IsTrue(_s.IsHomeMember(wreck));
            Assert.AreEqual(plates - 2, _s.InventoryState.GetQuantity("plating"));
            Assert.AreNotSame(wreck.SystemsManager, _s.LifeboatShip.SystemsManager);
            if(_installedAssemblyFixture)yield break;
            if(_flyJoinedAssembly)
            {
                LogReach("after the weld and Continue, before the engineering salvage");
                yield return AcquireEngineeringSalvageNaturally(wreck);
                yield return FlyJoinedHomeNaturally(wreck,marker,excursion);
            }
            if(System.Environment.GetCommandLineArgs().Contains("-profileJoinedHomeFrames"))
            {
                Assert.IsTrue(SynapticSea.Core.Systems.DockingManager.TryConnectedMembers(_s.HomeShip,out var members,out _));
                Debug.Log("[JoinedHomeProfile] connected_members="+members.Count+" active_context="+wreck.ShipId);
                yield return ProfileLiveExpedition();
            }
            var bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.LifeboatShip.ShipId);
            yield return WalkTo(bridge, 1.2f); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8);
            Assert.AreSame(_s.LifeboatShip, _s.PilotedShip);
            Assert.IsTrue(_s.TravelToMarkerId(excursion).GetBool("success")); yield return FixedSteps(8);
            bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.LifeboatShip.ShipId);
            yield return WalkTo(bridge, 1.2f); Assert.IsTrue(_s.TravelHome()); yield return FixedSteps(8);
            Assert.IsTrue(_s.IsHomeMember(_s.VisitedShips[marker]), "independent departure and return preserve the home extension");
            Assert.IsTrue(_s.RequestSave()); yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.IsTrue(_s.IsHomeMember(_s.VisitedShips[marker])); Assert.IsFalse(_s.SliceComplete);
            _defendWhileExploring = false;
        }

        IEnumerator SearchExistingCrewCare()
        {
            // The full repair/flight route needs wound treatment, not just a short-term health restore.
            // Search normal survivor lockers; their existing deterministic rolls remain authoritative.
            foreach (var locker in _s.LootContainers.Where(l=>l.IsValid&&!l.Searched&&l.LootTable=="generic_locker"&&(l.ContainerId=="loot_crew_quarters_02"||l.ContainerId=="loot_crew_quarters_04"))
                .OrderBy(l=>l.GlobalPosition.DistanceSquaredTo(_boot.Host.SceneState.Player.GodotPosition)).ToList())
            {
                if(_s.OxygenState.Oxygen<80)yield return RecoverInOwnedShuttle();
                yield return WalkTo(locker);
                for(int attempt=0;attempt<6&&!locker.Searched;attempt++)
                {_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);if(_s.LastInteractHandlerId!="authored_portal")break;}
                Assert.IsTrue(locker.Searched,"ordinary crew exploration supplies long-term care");
                foreach(var wound in _s.GetTreatableWounds().Cast<GdDict>().OrderByDescending(w=>w.GetFloat("severity"))
                    .Where(w=>_s.EvaluateWoundTreatment(RunSession.WOUND_ACTION_TREAT,w.GetString("wound_id")).GetBool("ok")))
                    Assert.IsTrue(_s.TreatWound(wound.GetString("wound_id")).GetBool("ok"));
                yield return UseEarnedProvisions();
                Debug.Log("[AssemblySupplies] searched="+locker.ContainerId+" inventory="+GdJson.Stringify(_s.InventoryState.Items)+" wounds="+GdJson.Stringify(_s.WoundState.GetSummary()));
                if(!_s.WoundState.GetSummary().GetArrayOrEmpty("wounds").Cast<GdDict>().Any(w=>w.GetFloat("severity")>.001&&!w.GetBool("treated")))break;
            }
        }

        IEnumerator StowUnneededHaul(SynapticSea.Core.Systems.ShipInstance vessel)
        {
            var hold=_s.CargoHoldControls.Single(c=>c.IsValid&&c.CarrierId==vessel.ShipId);
            yield return WalkTo(hold,.5f);
            for(int attempt=0;attempt<6&&!_boot.Ui.Inventory.IsOpen();attempt++)
            {_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);}
            Assert.IsTrue(_boot.Ui.Inventory.IsOpen(),"real owned cargo interaction supports safe salvage hauling");
            Assert.AreEqual("cargo_deposit",_s.LastInteractHandlerId);
            foreach(string item in new[]{"scrap_metal","biomatter_tangle","wiring_spool","frayed_cable_coil","cracked_pressure_valve","contaminated_water","fuel_canister","capacitor_cell"})
            {
                long quantity=_s.InventoryState.GetQuantity(item);
                long keep=item=="scrap_metal"&&_s.InventoryState.GetQuantity("plating")<2?4:0;
                long transfer=System.Math.Max(0,quantity-keep);
                if(transfer>0)Assert.AreEqual(transfer,_boot.Ui.Inventory.TransferQuantity(InventoryPanel.PaneSelf,item,transfer));
            }
            _boot.Ui.Inventory.Close();yield return FixedSteps(8);
            Debug.Log("[AssemblyFlight] physically stowed bulk haul; player_load="+_s.InventoryState.GetLoadRatio()+" cargo="+GdJson.Stringify(vessel.Inventory.GetSummary()));
            Assert.LessOrEqual(_s.InventoryState.GetLoadRatio(),1,"ordinary cargo handling removes overload without destroying supplies");
        }

        IEnumerator CraftUtilityFromEarnedHaul(string tool, SynapticSea.Core.Systems.ShipInstance securedWreck)
        {
            string recipe=tool=="lockpick_set"?"craft_lockpick_set":"craft_hack_chip";
            var inputs=_s.CraftingState.GetRecipe(recipe).GetDictOrEmpty("ingredients");
            if(_s.CurrentShip==_s.HomeShip&&inputs.Any(e=>_s.InventoryState.GetQuantity(V.Str(e.Key))<V.I64(e.Value)))
            {
                var wreckDoor=_s.HomeJoinControls.Single(c=>c.ShipId==securedWreck.ShipId&&c.ActionId=="connection_door"&&ReferenceEquals(c.Parent,securedWreck.SceneRoot));
                yield return WalkTo(wreckDoor,.6f);
            }
            if(_s.IsHomeMember(_s.CurrentShip)&&_s.CurrentShip!=_s.HomeShip)
            {
                var hold=_s.CargoHoldControls.Single(c=>c.IsValid&&c.CarrierId==securedWreck.ShipId);
                yield return WalkTo(hold,.5f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
                Assert.IsTrue(_boot.Ui.Inventory.IsOpen());
                foreach(var entry in inputs)
                {
                    string item=V.Str(entry.Key);long need=System.Math.Max(0,V.I64(entry.Value)-_s.InventoryState.GetQuantity(item));
                    long available=System.Math.Min(need,securedWreck.Inventory.GetQuantity(item));
                    if(available>0)Assert.AreEqual(available,_boot.Ui.Inventory.TransferQuantity(InventoryPanel.PaneContainer,item,available));
                }
                _boot.Ui.Inventory.Close();
                foreach(var loot in _s.LootContainers.Where(l=>l.IsValid&&!l.Searched).ToList())
                {
                    if(inputs.All(e=>_s.InventoryState.GetQuantity(V.Str(e.Key))>=V.I64(e.Value)))break;
                    yield return WalkTo(loot);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
                    Assert.IsTrue(loot.Searched,"utility crafting uses physically searched supplies");
                }
                var homeDoor=_s.HomeJoinControls.Single(c=>c.ShipId==securedWreck.ShipId&&c.ActionId=="connection_door"&&ReferenceEquals(c.Parent,_s.HomeShip.SceneRoot));
                yield return WalkTo(homeDoor,.6f);
            }
            Assert.AreSame(_s.HomeShip,_s.CurrentShip,"crafting takes place at the actual home workbench");
            foreach(var entry in inputs)Assert.GreaterOrEqual(_s.InventoryState.GetQuantity(V.Str(entry.Key)),V.I64(entry.Value),"earned utility ingredient "+entry.Key);
            _s.RefreshDeckTransitions();
            var station=_s.CraftingStations.Single(c=>c.StationKind=="workbench");
            if(station.GlobalPosition.Y>3 && _boot.Host.SceneState.Player.GodotPosition.Y<3)
            {var up=_s.DeckTransitions.First(d=>d.DestinationDeck==1);yield return WalkTo(up,2.4f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);}
            yield return WalkTo(station,1.2f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
            Assert.IsTrue(_boot.Ui.RecipePicker.IsOpen(),"normal workbench interaction exposes crafting");
            for(int guard=0;guard<_boot.Ui.RecipePicker.GetEntryCount()&&_boot.Ui.RecipePicker.GetSelectedId()!=recipe;guard++)_boot.Ui.RecipePicker.MoveSelection(1);
            Assert.AreEqual(recipe,_boot.Ui.RecipePicker.GetSelectedId());long before=_s.InventoryState.GetQuantity(tool);
            var result=_boot.Ui.RecipePicker.ConfirmSelection();Assert.IsTrue(result.GetBool("ok"),GdJson.Stringify(result));
            float deadline=Time.realtimeSinceStartup+40;
            while(_s.CraftingState.IsCrafting()&&!_s.SliceComplete&&Time.realtimeSinceStartup<deadline)yield return null;
            Assert.IsFalse(_s.SliceComplete);Assert.IsFalse(_s.CraftingState.IsCrafting());Assert.AreEqual(before+1,_s.InventoryState.GetQuantity(tool));
            Debug.Log("[AssemblyFlight] earned utility craft="+recipe+" inventory="+GdJson.Stringify(_s.InventoryState.Items));
            if(_boot.Host.SceneState.Player.GodotPosition.Y>3)
            {var down=_s.DeckTransitions.First(d=>d.DestinationDeck==0);yield return WalkTo(down,2.4f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);}
        }

        IEnumerator AcquireEngineeringSalvageNaturally(SynapticSea.Core.Systems.ShipInstance wreck)
        {
            var engineering=_s.DerelictInteractables.First(i=>i.IsValid&&i.ObjectiveId.Contains("engineering"));
            string objectiveId=engineering.ObjectiveId;
            var hatch=_s.SealedHatches.FirstOrDefault(h=>h.IsValid&&!h.Bypassed&&h.GlobalPosition.DistanceSquaredTo(engineering.GlobalPosition)<1);
            if(hatch!=null)
            {
                string hatchId=hatch.HatchId,tool=hatch.LockKind==SealedHatch.MECHANICAL?"lockpick_set":"hack_chip";
                if(_s.InventoryState.GetQuantity(tool)==0)
                {
                    if(!_s.IsHomeMember(wreck))
                    {
                        var boatBridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.LifeboatShip.ShipId);
                        yield return WalkTo(boatBridge,1.2f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
                        Assert.IsTrue(_s.TravelHome());yield return FixedSteps(8);
                        yield return CraftUtilityFromEarnedHaul(tool,_s.VisitedShips.Values.First(v=>_s.IsHomeMember(v)));
                        boatBridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.LifeboatShip.ShipId);yield return WalkTo(boatBridge,1.2f);
                        Assert.IsTrue(_s.TravelToMarkerId(wreck.MarkerId).GetBool("success"));yield return FixedSteps(8);
                    }
                    else
                    {
                        yield return CraftUtilityFromEarnedHaul(tool,wreck);
                        var wreckDoor=_s.HomeJoinControls.Single(c=>c.ShipId==wreck.ShipId&&c.ActionId=="connection_door"&&ReferenceEquals(c.Parent,wreck.SceneRoot));
                        yield return WalkTo(wreckDoor,.6f);
                    }
                }
                hatch=_s.SealedHatches.Single(h=>h.HatchId==hatchId);
                var filter=new NavMeshQueryFilter{agentTypeID=ShipNavMesh.AgentTypeId,areaMask=NavMesh.AllAreas};Vec3? face=null;
                foreach(var direction in HatchFaceOffsets)
                {
                    var candidate=hatch.GlobalPosition+direction;
                    if(TryStandingApproach(candidate,.2f,_boot.Host.SceneState.Player.transform.position,filter,out _)){face=candidate;break;}
                }
                // A closed door between the survivor and the hatch makes every face's route partial. A person opens the door and goes on,
                // so take the face whose partial route ends closest to it and let WalkTo open the doors on the way.
                if(!face.HasValue)face=NearestHatchFaceBehindClosedDoors(hatch,filter);
                Assert.IsTrue(face.HasValue,"a reachable hatch face must exist without penetrating the blocker; hatch="+hatchId+"@"+hatch.GlobalPosition
                    +"; player="+_boot.Host.SceneState.Player.transform.position+"; "+DescribeHatchFaces(hatch,filter));
                yield return WalkTo(face.Value,.2f);long toolsBefore=_s.InventoryState.GetQuantity(tool);
                Assert.IsTrue(_s.UseConsumableItem(tool).GetBool("ok"));
                for(int guard=0;guard<4&&!hatch.Bypassed;guard++){_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);}
                Assert.IsTrue(hatch.Bypassed,"normal interaction consumes the crafted bypass flag");
                Assert.AreEqual(toolsBefore-1,_s.InventoryState.GetQuantity(tool));Assert.IsTrue(wreck.BypassedHatchIds.Contains(hatchId));
            }
            engineering=_s.DerelictInteractables.Single(i=>i.IsValid&&i.ObjectiveId==objectiveId);
            yield return WalkTo(engineering,1.2f);
            for(int guard=0;guard<8&&!engineering.Completed;guard++)
            {
                _boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
                if(_s.LastInteractHandlerId!="authored_portal")break; // a door beside the objective takes the key first; anything else is the objective's own answer
            }
            Assert.IsTrue(engineering.Completed,"physically salvage the existing engineering objective; handler="+_s.LastInteractHandlerId
                +"; player="+_boot.Host.SceneState.Player.transform.position+"; objective="+engineering.GlobalPosition+" radius="+engineering.InteractionRadius
                +"; hatchBypassed="+(hatch==null?"n/a":hatch.Bypassed.ToString())+"; "+SurvivalReport());
            Assert.IsTrue(wreck.GetObjectiveController().IsObjectiveComplete(engineering.Sequence),"engineering interaction must complete the authoritative objective, not only its view");
            Debug.Log("[AssemblyFlight] engineering salvage marker="+wreck.MarkerId+" inventory="+GdJson.Stringify(_s.InventoryState.Items));
        }

        IEnumerator GatherMissingPropulsionSalvage(SynapticSea.Core.Systems.ShipInstance securedWreck)
        {
            foreach(var route in new[]{("thruster_nozzle","-2:-2:2"),("fuel_line","0:2:1")})
            {
                if(_s.InventoryState.GetQuantity(route.Item1)>0)continue;
                var boatBridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.LifeboatShip.ShipId);
                yield return WalkTo(boatBridge,1.2f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
                Assert.AreSame(_s.LifeboatShip,_s.PilotedShip);var travel=_s.TravelToMarkerId(route.Item2);
                Assert.IsTrue(travel.GetBool("success"),"normal surveyed salvage excursion: "+GdJson.Stringify(travel));yield return FixedSteps(8);
                var destination=_s.CurrentShip;
                foreach(var barrier in _s.DockBarriers.Where(b=>b.IsValid&&!b.Opened).ToList())yield return WalkAndFinishChannel(barrier.GlobalPosition);
                yield return CutBiomatterMooring(destination);
                var medical=_s.LootContainers.First(l=>l.IsValid&&!l.Searched&&l.ContainerId.Contains("medical"));
                yield return WalkTo(medical);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);Assert.IsTrue(medical.Searched);
                while(_s.VitalsState.Health<80&&_s.InventoryState.GetQuantity("field_medkit")>0)Assert.IsTrue(_s.UseConsumableItem("field_medkit").GetBool("ok"));
                foreach(var wound in _s.GetTreatableWounds().Cast<GdDict>().Where(w=>w.GetBool("can_bandage")))Assert.IsTrue(_s.BandageWound(wound.GetString("wound_id")).GetBool("ok"));
                var crew=_s.LootContainers.First(l=>l.IsValid&&!l.Searched&&l.ContainerId=="loot_crew_quarters_01");
                yield return WalkTo(crew);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);Assert.IsTrue(crew.Searched);yield return UseEarnedProvisions();
                yield return AcquireEngineeringSalvageNaturally(destination);
                Assert.Greater(_s.InventoryState.GetQuantity(route.Item1),0,"the existing deterministic engineering roll supplies "+route.Item1+" without a fixture grant");
                boatBridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.LifeboatShip.ShipId);yield return WalkTo(boatBridge,1.2f);
                _boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);Assert.IsTrue(_s.TravelHome());yield return FixedSteps(8);
                Assert.IsTrue(_s.IsHomeMember(_s.VisitedShips[securedWreck.MarkerId]));
                Assert.IsTrue(_s.RequestSave());yield return BootPlayable(RunLaunchRequest.ContinueWorld());
                Assert.Greater(_s.InventoryState.GetQuantity(route.Item1),0,"earned parts survive return and Continue");
            }
        }

        IEnumerator FlyJoinedHomeNaturally(SynapticSea.Core.Systems.ShipInstance wreck,string marker,string excursion)
        {
            var homeBridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.HomeShip.ShipId);
            LogReach("before walking to the home bridge");
            yield return WalkTo(homeBridge,1.2f);LogReach("at the home bridge, before claiming it");_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
            LogReach("after claiming the home bridge");
            Assert.AreSame(_s.HomeShip,_s.PilotedShip,"the joined home's real bridge claims assembly controls");
            Vec3 seaBefore=_s.SynapticSeaWorld.PlayerPosition;
            var destination=_s.SynapticSeaWorld.MarkersInRange(_s.ScannerState.RangeRadius).First(m=>m.MarkerId!=marker);
            var denied=_s.TravelToMarkerId(destination.MarkerId);
            Assert.IsFalse(denied.GetBool("success"),"the uninstalled home cannot lift the repaired joined mass");
            Assert.AreEqual("insufficient_propulsion_capacity",denied.GetString("reason"));
            Assert.AreEqual(seaBefore,_s.SynapticSeaWorld.PlayerPosition);
            Debug.Log("[AssemblyFlight] initial denial="+GdJson.Stringify(denied));
            yield return GatherMissingPropulsionSalvage(wreck);
            wreck=_s.VisitedShips[marker];
            var required=_s.WorkActionDriver.Catalog.GetAction("commission_home_propulsion").GetDictOrEmpty("materials_consumed");
            var missing=new GdDict();
            foreach(var part in required)
            {long shortage=V.I64(part.Value)-_s.InventoryState.GetQuantity(V.Str(part.Key));if(shortage>0)missing[V.Str(part.Key)]=shortage;}
            Assert.IsTrue(missing.IsEmpty,"Natural assembly installation is blocked by existing finite supply: "+GdJson.Stringify(missing)
                +". No parts are granted; nozzle fabrication needs a known recipe, tier-2 fabricator and its existing ingredients.");
            var installation=_s.HomeJoinControls.Single(c=>c.ActionId=="commission_home_propulsion");
            if(installation.GlobalPosition.Y>3)
            {
                var transition=_s.DeckTransitions.First(d=>d.DestinationDeck==1);
                yield return WalkTo(transition,2.4f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
            }
            yield return WalkTo(installation,1.2f);_s.BeginWorkHold();_boot.Host.SceneState.Player.RequestInteract();
            Assert.IsTrue(_s.WorkActionDriver.IsWorking(),"earned tools, skill and real materials begin home installation");
            float deadline=Time.realtimeSinceStartup+40;
            while(_s.WorkActionDriver.IsWorking()&&!_s.SliceComplete&&Time.realtimeSinceStartup<deadline)yield return null;
            _s.EndWorkHold();Assert.IsFalse(_s.SliceComplete);Assert.IsFalse(_s.WorkActionDriver.IsWorking());
            Assert.AreEqual("propulsion:"+_s.HomeShip.ShipId,_s.HomeShip.Mobility.GetString("engine_id"));
            if(_boot.Host.SceneState.Player.transform.position.y>3)
            {
                var transition=_s.DeckTransitions.First(d=>d.DestinationDeck==0);
                yield return WalkTo(transition,2.4f);_boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);
            }
            homeBridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.HomeShip.ShipId);yield return WalkTo(homeBridge,1.2f);
            _boot.Host.SceneState.Player.RequestInteract();yield return FixedSteps(8);Assert.AreSame(_s.HomeShip,_s.PilotedShip);
            var homePose=_s.HomeShip.SceneRoot.GlobalTransform;var wreckPose=wreck.SceneRoot.GlobalTransform;
            string edge=GdJson.Stringify(wreck.DockingPorts);string resources=GdJson.Stringify(_s.InventoryState.Items);
            var moved=_s.TravelToMarkerId(destination.MarkerId);
            Assert.IsTrue(moved.GetBool("success"),"legitimately installed and locally powered assembly: "+GdJson.Stringify(moved));
            Assert.AreEqual(destination.Position,_s.HomeSeaPosition);Assert.AreEqual(destination.Position,_s.SynapticSeaWorld.PlayerPosition);
            Assert.AreEqual(homePose,_s.HomeShip.SceneRoot.GlobalTransform);Assert.AreEqual(wreckPose,wreck.SceneRoot.GlobalTransform,
                "sea travel keeps the retained local walking frame and member relative poses");
            Assert.AreEqual(edge,GdJson.Stringify(wreck.DockingPorts));Assert.AreEqual(resources,GdJson.Stringify(_s.InventoryState.Items));
            Assert.IsTrue(_s.RequestSave());yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.AreEqual(destination.Position,_s.HomeSeaPosition);Assert.AreEqual(destination.Position,_s.SynapticSeaWorld.PlayerPosition);
            Assert.IsTrue(_s.IsHomeMember(_s.VisitedShips[marker]));
            Assert.AreNotSame(_s.HomeShip.SystemsManager,_s.VisitedShips[marker].SystemsManager);
            Assert.AreNotSame(_s.HomeShip.SystemsManager,_s.LifeboatShip.SystemsManager);
            yield return CaptureHud("natural-mobile-home-arrival.png");
            // The caller completes independent-shuttle departure/return and another Continue.
        }

        IEnumerator RecoverInOwnedShuttle()
        {
            var shelter=_s.BridgeTerminals.Single(t=>t.ShipId==_s.LifeboatShip.ShipId);
            yield return WalkTo(shelter,1.2f);
            float deadline=Time.realtimeSinceStartup+45;
            while((_s.SanityState.Sanity<90 || _s.OxygenState.Oxygen<99) && !_s.SliceComplete && Time.realtimeSinceStartup<deadline)yield return null;
            Assert.IsFalse(_s.SliceComplete,"survived ordinary local shelter recovery: "+SurvivalReport());
            Assert.GreaterOrEqual(_s.SanityState.Sanity,90,"owned operational boat permits actual sanity recovery: "+SurvivalReport());
            Assert.GreaterOrEqual(_s.OxygenState.Oxygen,99,SurvivalReport());
        }

        IEnumerator ReviewPurposefulRooms()
        {
            string family=_s.CurrentShip.BuiltLayout.GetString("topology_family");
            foreach(string role in new[]{"cargo","medical","crew_quarters","engineering"})
            {
                var room=_s.CurrentShip.BuiltLayout.GetArrayOrEmpty("rooms").Cast<GdDict>().FirstOrDefault(r=>r.GetString("room_role")==role);
                if(room==null) continue;
                var cell=LayoutSerializer.ParseSlotCell(room.GetArrayOrEmpty("cells")[room.GetArrayOrEmpty("cells").Count/2]);
                var local=new Vec3(V.F64(cell[0])*4,.55,V.F64(cell[1])*4);
                yield return WalkTo(_s.CurrentShip.SceneRoot.GlobalTransform*local,1.2f);
                yield return FixedSteps(10); yield return CaptureHud("constrained-"+family+"-"+role+".png");
            }
        }

        IEnumerator ProfileLiveExpedition()
        {
            var camera = _boot.Host.SceneState.CameraRig.Camera; var oldTarget = camera.targetTexture;
            var target = new RenderTexture(2048,1224,24); var frames = new List<double>();
            var frameIntervals=new List<double>();
            var panel=_boot.HudDocument.panelSettings; var oldHudTarget=panel.targetTexture;
            var hudTarget=new RenderTexture(2048,1224,0,RenderTextureFormat.ARGB32);
            int oldRate = Application.targetFrameRate, oldVsync = QualitySettings.vSyncCount; float oldScale = Time.timeScale;
            double worldBefore = _s.WorldTime; var clock = new System.Diagnostics.Stopwatch();
            try
            {
                camera.targetTexture = target; panel.targetTexture=hudTarget; Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0; Time.timeScale = 1;
                for(int i=0;i<150;i++)
                {
                    clock.Restart(); yield return null;
                    if(i>=30) frameIntervals.Add(Time.unscaledDeltaTime*1000);
                    if(SystemInfo.supportsAsyncGPUReadback)
                    {
                        var completion = UnityEngine.Rendering.AsyncGPUReadback.Request(target,0,0,1,0,1,0,1);
                        while(!completion.done && clock.Elapsed.TotalSeconds < 5)
                        { yield return null; if(i>=30) frameIntervals.Add(Time.unscaledDeltaTime*1000); }
                        Assert.IsTrue(completion.done); Assert.IsFalse(completion.hasError);
                    }
                    if(i>=30) frames.Add(clock.Elapsed.TotalMilliseconds);
                }
                frames.Sort(); frameIntervals.Sort(); Assert.Greater(_s.WorldTime,worldBefore,"live survival/combat session continues during profile");
                Debug.Log($"[LiveExpeditionProfile] family={_s.CurrentShip.BuiltLayout.GetString("topology_family")} rooms={_s.CurrentShip.BuiltLayout.GetArrayOrEmpty("rooms").Count} "
                    + $"threats={_s.ThreatManager.Threats.Count} renderers={Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length} samples={frames.Count} completion_median_ms={frames[60]:F2} completion_p95_ms={frames[114]:F2} "
                    + $"frame_samples={frameIntervals.Count} frame_interval_median_ms={frameIntervals[frameIntervals.Count/2]:F2} frame_interval_p95_ms={frameIntervals[(int)(frameIntervals.Count*.95)]:F2} "
                    + $"gpu_completion_fenced={SystemInfo.supportsAsyncGPUReadback} device={SystemInfo.graphicsDeviceName}; live host/player/camera/AI/survival/UI active, stationary gameplay view");
            }
            finally { camera.targetTexture=oldTarget; panel.targetTexture=oldHudTarget; Application.targetFrameRate=oldRate; QualitySettings.vSyncCount=oldVsync; Time.timeScale=oldScale; Object.Destroy(target); Object.Destroy(hudTarget); }
        }

        bool NaturalChannelActive() => _s.RepairPoints.Any(r => r.IsValid && r.Channeling)
            || _s.FireSuppressionPoints.Any(r => r.IsValid && r.Channeling)
            || _s.BreachSealPoints.Any(r => r.IsValid && r.Channeling)
            || _s.DockBarriers.Any(r => r.IsValid && r.Channeling);

        [UnityTest]
        public IEnumerator RampLandingPresentationAndBothDeckTransfersPreservePhysicalStanding()
        {
            yield return StartThroughTitle(); _s.RefreshDeckTransitions();
            var up = _s.DeckTransitions.First(d => d.DestinationDeck == 1);
            yield return WalkTo(up.GlobalPosition,1.2f); yield return FixedSteps(10);
            var player = _boot.Host.SceneState.Player; var rig = _boot.Host.SceneState.CameraRig;
            Debug.Log("[RampStanding] before up player="+player.transform.position+" controller="+player.GetComponent<CharacterController>().bounds+" landing="+up.GlobalPosition);
            var ray = rig.Camera.ScreenPointToRay(rig.Camera.WorldToScreenPoint(player.transform.position+Vector3.up*.8f));
            foreach(var renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                if(renderer.enabled && renderer.bounds.IntersectRay(ray,out float distance) && distance < Vector3.Distance(ray.origin,player.transform.position))
                    Debug.Log("[RampRenderer] "+renderer.name+" parent="+renderer.transform.parent?.name+" bounds="+renderer.bounds+" structural="+renderer.GetComponentInParent<StructuralModule>()?.layer+" marker="+renderer.GetComponentInParent<RuntimeMarker>()?.kind);
            Assert.Less(player.transform.position.y,1.5f,"player is legitimately on lower deck before transfer");
            foreach(float size in new[]{7f,4f,11f}) { rig.SetViewSize(size); yield return FixedSteps(10); yield return CaptureHud("ramp-lower-"+size+".png"); }
            rig.SetViewSize(7); player.RequestInteract(); yield return FixedSteps(10);
            Assert.Greater(player.transform.position.y,3.5f); Assert.Less(player.transform.position.y,5.5f);
            Assert.IsTrue(_s.RequestSave()); yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            player = _boot.Host.SceneState.Player; yield return FixedSteps(10); Assert.Greater(player.transform.position.y,3.5f,"save at upper landing retains physical deck");
            yield return CaptureHud("ramp-upper-loaded.png");
            _s.RefreshDeckTransitions(); var down = _s.DeckTransitions.First(d=>d.DestinationDeck==0);
            yield return WalkTo(down.GlobalPosition,1.2f); player.RequestInteract(); yield return FixedSteps(10);
            Assert.Less(player.transform.position.y,1.5f); Assert.IsTrue(_s.RequestSave());
            yield return BootPlayable(RunLaunchRequest.ContinueWorld()); yield return FixedSteps(10);
            Assert.Less(_boot.Host.SceneState.Player.transform.position.y,1.5f,"save at lower landing retains physical deck");
            yield return CaptureHud("ramp-lower-loaded.png");
        }

        IEnumerator CaptureHud(string name)
        {
            if(Application.isEditor) yield break; // The Windows GPU player owns the visual evidence.
            string folder = System.Environment.GetEnvironmentVariable("SYNAPTIC_ENTRY_CAPTURE_DIR");
            if (string.IsNullOrEmpty(folder)) folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../artifacts/ramp-review"));
            System.IO.Directory.CreateDirectory(folder); string path = System.IO.Path.Combine(folder,name);
            // Batch players have no screen framebuffer. Render the actual camera and HUD panel offscreen,
            // then composite their pixels; no UI is redrawn or invented by the capture helper.
            var camera = _boot.Host.SceneState.CameraRig.Camera;
            var panel = _boot.HudDocument.panelSettings;
            var world = new RenderTexture(2048,1224,24);
            var hud = new RenderTexture(2048,1224,0,RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(2048,1224,TextureFormat.RGBA32,false);
            var hudPixels = new Texture2D(2048,1224,TextureFormat.RGBA32,false);
            var oldCamera = camera.targetTexture; var oldPanel = panel.targetTexture;
            bool oldClear = panel.clearColor; Color oldClearValue = panel.colorClearValue;
            var oldActive = RenderTexture.active;
            try
            {
                hud.Create(); panel.targetTexture = hud; panel.clearColor = true; panel.colorClearValue = Color.clear;
                for(int i=0;i<10;i++) yield return null;
                camera.targetTexture = world; camera.Render(); RenderTexture.active = world;
                pixels.ReadPixels(new Rect(0,0,2048,1224),0,0); pixels.Apply();
                RenderTexture.active = hud; hudPixels.ReadPixels(new Rect(0,0,2048,1224),0,0); hudPixels.Apply();
                var sceneColors = pixels.GetPixels32(); var uiColors = hudPixels.GetPixels32(); int uiCount=0, sceneCount=0;
                for(int i=0;i<sceneColors.Length;i++)
                {
                    if(uiColors[i].a>0) uiCount++;
                    if(sceneColors[i].r+sceneColors[i].g+sceneColors[i].b>30) sceneCount++;
                    float alpha=uiColors[i].a/255f;
                    sceneColors[i] = new Color32((byte)Mathf.Min(255,uiColors[i].r+sceneColors[i].r*(1-alpha)),
                        (byte)Mathf.Min(255,uiColors[i].g+sceneColors[i].g*(1-alpha)),
                        (byte)Mathf.Min(255,uiColors[i].b+sceneColors[i].b*(1-alpha)),255);
                }
                Assert.Greater(uiCount,1000,"capture must contain the actual rendered HUD");
                Assert.Greater(sceneCount,10000,"capture must contain the rendered world");
                pixels.SetPixels32(sceneColors); pixels.Apply(); System.IO.File.WriteAllBytes(path,pixels.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture=oldCamera; panel.targetTexture=oldPanel; panel.clearColor=oldClear; panel.colorClearValue=oldClearValue;
                RenderTexture.active=oldActive; Object.Destroy(world); Object.Destroy(hud); Object.Destroy(pixels); Object.Destroy(hudPixels);
            }
        }

        /// <summary>Health, vitals and the damage taken by source so far, appended to route failures so a death is reported as one.</summary>
        string SurvivalReport()
            => "health=" + _s.VitalsState.Health + " oxygen=" + _s.OxygenState.Oxygen + " sanity=" + _s.SanityState.Sanity
                + " radiation=" + _s.RadiationState.Radiation + " ship=" + _s.CurrentShip.ShipId + " position=" + _boot.Host.SceneState.Player.transform.position
                + " health_lost_by_source=" + GdJson.Stringify(_journeyDamage);

        static IEnumerator FixedSteps(int count)
        {
            for(int i=0;i<count;i++) yield return new WaitForFixedUpdate();
        }

        IEnumerator WalkAndFinishChannel(Vec3 at)
        {
            yield return WalkTo(at, 1.6f);
            for(int attempt=0;attempt<6;attempt++)
            {
                _boot.Host.SceneState.Player.RequestInteract();
                if(_s.LastInteractHandlerId!="authored_portal")break;
                yield return FixedSteps(8);
            }
            Assert.That(_s.LastInteractHandlerId, Is.EqualTo("repair_point").Or.EqualTo("fire_suppression_point").Or.EqualTo("breach_seal_point").Or.EqualTo("dock_barrier"),
                "work at " + at + "; standing=" + _boot.Host.SceneState.Player.transform.position);
            if (_s.LastInteractHandlerId == "dock_barrier" && !NaturalChannelActive())
            {
                Assert.IsTrue(_s.DockBarriers.Any(b => b.Opened && b.IsPlayerInDirectRangeStrict(Frame.ToGodot(_boot.Host.SceneState.Player.transform.position))), "intact docking seam opened through normal interaction");
                yield break;
            }
            Assert.IsTrue(NaturalChannelActive(), "normal interact begins a timed work channel");
            float deadline = Time.realtimeSinceStartup + 60f;
            while (NaturalChannelActive() && !_s.SliceComplete && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsFalse(_s.SliceComplete || _s.VitalsState.IsIncapacitated(), "the survivor died during the work channel at " + at + ": " + SurvivalReport());
            Assert.IsFalse(NaturalChannelActive(), "the work channel did not finish within 60 s at " + at + ": " + SurvivalReport());
        }

        [UnityTest]
        public IEnumerator WalkTheHubUseDeckLootSaveContinueAndKeepSurvivingAfterOnboarding()
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
            for (int guard = 0; guard < 12 && !_s.HomeObjectivesComplete && !_s.SliceComplete; guard++)
            {
                var objective = _s.Interactables.FirstOrDefault(o => o.Active && !o.Completed);
                Assert.IsNotNull(objective, "an active objective remains");
                yield return WalkTo(objective.GlobalPosition);
                _boot.Host.SceneState.Player.RequestInteract();
                yield return null;
            }
            Assert.IsTrue(_s.HomeObjectivesComplete, "real walking/interactions complete onboarding");
            Assert.IsFalse(_s.SliceComplete, "onboarding keeps the same life active");
            Assert.IsNull(_boot.Results, "no terminal results or simulation pause");
            double before = _s.WorldTime;
            yield return FixedSteps(30);
            Assert.Greater(_s.WorldTime,before,"survival continues after all home tasks");
            Assert.IsTrue(_s.RequestSave());
            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.IsTrue(_s.HomeObjectivesComplete);
            Assert.IsFalse(_s.SliceComplete);
            Assert.IsNull(_boot.Results);
            Assert.IsTrue(_s.LootContainers.First(l=>l.ContainerId==lootId).Searched,"persistent loot is retained after onboarding and Continue");
            var restoredChip = _boot.HudDocument.GetComponent<HudRoot>().Objective;
            StringAssert.Contains("survive", restoredChip.ChipText);
            Assert.AreEqual("4/4", restoredChip.ProgressText, "Continue restores completed onboarding HUD history");
            Assert.IsTrue(_s.Interactables.All(o => o.Completed && !o.Active), "completed markers stay completed after Continue");
            yield return CaptureHud("persistent-home-onboarding-continue.png");

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
