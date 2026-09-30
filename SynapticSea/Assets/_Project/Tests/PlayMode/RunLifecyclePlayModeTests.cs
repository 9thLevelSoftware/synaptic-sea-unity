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
                        var candidatePath = new NavMeshPath();
                        if (!NavMesh.CalculatePath(player.transform.position, sample.position, filter, candidatePath) || candidatePath.status != NavMeshPathStatus.PathComplete) continue;
                        chosen = portal;
                        approach = candidatePath;
                        break;
                    }
                    if (chosen != null) break;
                }
                Assert.IsNotNull(chosen, "a reachable closed door or standing interaction approach exists for " + target
                    + "; player=" + player.transform.position + "; landing=" + landing.position + "; path=" + path.status
                    + "; corners=" + string.Join(" -> ", path.corners.Select(c => c.ToString()))
                    + "; portals=" + string.Join(", ", Object.FindObjectsByType<AuthoredPortalRuntime>().Where(p => p.transform.position.y < 2f)
                        .Select(p => p.portalId + "@" + p.transform.position + " " + p.portalKind + " open=" + p.isOpen))
                    + "; obstacles=" + string.Join(", ", Object.FindObjectsByType<NavMeshObstacle>().Where(o => o.transform.position.y < 2f)
                        .Select(o => o.name + "@" + o.transform.TransformPoint(o.center) + " enabled=" + o.enabled + " size=" + o.size))
                    + "; blockers=" + string.Join(", ", Physics.OverlapSphere(blockedRouteEnd + Vector3.up, 2f, SpawnClearance.BlockingMask)
                        .Select(c => c.transform.parent.name + "/" + c.name + "@" + c.bounds.center + " size=" + c.bounds.size)));
                Debug.Log("[NaturalWalk] target " + target + "; door " + chosen.portalId + "@" + chosen.transform.position + "; route " + string.Join(" -> ", approach.corners.Select(c => c.ToString())));
                yield return WalkAlong(approach, 0.3f);
                player.RequestInteract();
                for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
                Assert.IsTrue(chosen.isOpen, "real interact opens " + chosen.portalId + "; handler " + _s.LastInteractHandlerId);
            }
            Assert.AreEqual(NavMeshPathStatus.PathComplete, path.status, "standing route to " + target);
            yield return WalkAlong(path, 0.2f);
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
                    if (!NavMesh.CalculatePath(from, sample.position, filter, candidate) || candidate.status != NavMeshPathStatus.PathComplete) continue;
                    path = candidate;
                    return true;
                }
            return false;
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
        public IEnumerator WalkRepairTravelBoardAndReturnWithoutFixtureResources()
        {
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
                var next = repairPoints.Where(r => !r.Repaired && r.MinSkill <= _s.PlayerProgression.GetSkillLevel("repair"))
                    .OrderBy(r => r.MinSkill).FirstOrDefault();
                Assert.IsNotNull(next, "normal repair XP unlocks every required remaining repair");
                Assert.GreaterOrEqual(_s.PlayerProgression.GetSkillLevel("repair"), next.MinSkill, "earned skill meets the real gate");
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
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!_s.PropulsionExpandedState.CanPropel() && Time.realtimeSinceStartup < deadline && !_s.SliceComplete) yield return null;
            Assert.IsFalse(_s.SliceComplete, "natural repairs and seals are survivable");
            Assert.IsTrue(_s.PropulsionExpandedState.CanPropel());
            var bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return WalkTo(bridge.GlobalPosition, 1.2f);
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
            yield return WalkTo(bridge.GlobalPosition, 1.2f);
            Assert.IsTrue(_s.TravelHome(), "return from the first expedition");
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            Assert.IsFalse(_s.AwayFromStart);
            Assert.IsTrue(_s.ShipSystemsManager.IsOperational("propulsion"), "repairs survive return");
            Assert.IsTrue(_s.RequestSave());
            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.IsFalse(_s.AwayFromStart);
            foreach (string id in needed) Assert.IsTrue(_s.ShipSystemsManager.IsOperational(id), id + " survives Continue");
            Assert.AreEqual(earnedRepair, _s.PlayerProgression.GetSkillLevel("repair"), "earned training survives Continue");
            Assert.AreEqual(1, _s.InventoryState.GetQuantity("portable_oxygen_pump"), "acquired pump survives Continue");
            Assert.IsTrue(_s.LootContainers.Single(l => l.ContainerId == "start_supply_a").Searched, "the finite maintenance cache cannot pay again after Continue");
            Assert.IsTrue(_s.VisitedShips[awayMarker].LootedContainerIds.Contains(awayLoot.ContainerId), "searched wreck loot remains recorded after returning and Continue");
            Assert.IsFalse(_s.VisitedShips[awayMarker].CombatSummary.GetArrayOrEmpty("threats").Cast<GdDict>().Any(t => t.GetString("instance_id") == defeatedId),
                "normal death sweep removes the defeated encounter from the saved runtime");
            Assert.AreEqual("crowbar", _s.EquipmentState.GetEquipped("primary_hand"), "acquired combat tool survives Continue");
            bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return WalkTo(bridge.GlobalPosition, 1.2f);
            Assert.IsTrue(_s.TravelToMarkerId(awayMarker).GetBool("success"), "revisit the saved wreck through guarded travel");
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            Assert.IsFalse(_s.ThreatManager.Threats.Any(t => t.InstanceId == defeatedId), "defeated generated enemy does not respawn on actual saved-wreck restoration");
            bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return WalkTo(bridge.GlobalPosition, 1.2f);
            Assert.IsTrue(_s.TravelHome());
            Assert.IsFalse(_s.SliceComplete, "hub objective extraction remains available after returning");
            var nextContact = _s.Scan().GetArrayOrEmpty("markers").Cast<GdDict>()
                .FirstOrDefault(m => m.GetInt("size_class") >= 1 && !_s.VisitedShips.ContainsKey(m.GetString("marker_id")));
            Assert.IsNotNull(nextContact, "normal scanner exposes a new larger wreck after one onboarding round trip");
            bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return WalkTo(bridge.GlobalPosition, 1.2f);
            string nextId = nextContact.GetString("marker_id");
            var nextTravel = _s.TravelToMarkerId(nextId);
            Assert.IsTrue(nextTravel.GetBool("success"), "normal subsequent scanner travel: " + GdJson.Stringify(nextTravel));
            for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            Assert.AreEqual(SynapticSea.Core.Procgen.ExpeditionLayoutEngine.Profile, _s.CurrentShip.Blueprint.GenerationProfile);
            Assert.AreEqual(SynapticSea.Core.Procgen.ExpeditionLayoutEngine.Profile, _s.CurrentShip.BuiltLayout.GetString("generation_profile"));
            Debug.Log("[NaturalExpeditionRoute] second new destination=" + nextId + " size=" + nextContact.GetInt("size_class")
                + " rooms=" + _s.CurrentShip.BuiltLayout.GetArrayOrEmpty("rooms").Count);
            Assert.IsTrue(_s.RequestSave());
            yield return BootPlayable(RunLaunchRequest.ContinueWorld());
            Assert.AreEqual(SynapticSea.Core.Procgen.ExpeditionLayoutEngine.Profile, _s.CurrentShip.Blueprint.GenerationProfile, "actual Continue keeps expanded destination");
            Assert.AreEqual(nextId, _s.CurrentShip.MarkerId);
        }

        bool NaturalChannelActive() => _s.RepairPoints.Any(r => r.IsValid && r.Channeling)
            || _s.FireSuppressionPoints.Any(r => r.IsValid && r.Channeling)
            || _s.BreachSealPoints.Any(r => r.IsValid && r.Channeling)
            || _s.DockBarriers.Any(r => r.IsValid && r.Channeling);

        IEnumerator WalkAndFinishChannel(Vec3 at)
        {
            yield return WalkTo(at, 1.6f);
            _boot.Host.SceneState.Player.RequestInteract();
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
            Assert.IsFalse(NaturalChannelActive(), "work completed without teleporting, granting resources or boosting skills");
            Assert.IsFalse(_s.SliceComplete, "survived the channel");
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
