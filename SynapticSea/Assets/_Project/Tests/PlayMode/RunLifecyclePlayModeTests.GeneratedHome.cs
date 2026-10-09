using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// Phase 1.11 adds the real-scene proofs behind making it the Title default: the Title start, Results New Run, the docked lifeboat's
    /// colliders against the home, and a physically walked loot, repair and depart route.
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

        // ---------------------------------------------------------------- Phase 1.11: the default

        [UnityTest]
        public IEnumerator TitleNewRunStartsInAGeneratedHomeAtTheDefaultPacing()
        {
            yield return StartThroughTitle(generatedHome: true, seed: 2024);
            Assert.IsTrue(_boot.Launch.GeneratedHome, "Title New Run at scaled pacing generates the home");
            Assert.IsNotNull(_boot.GeneratedStart);
            Assert.IsTrue(_s.GeneratedHome);
            Assert.AreEqual(2024, _s.RunSeed);
            Assert.AreEqual(WorldClock.DefaultNewRunScale, _s.GameClock.Scale, "the default pacing");
            Assert.AreEqual(0, _s.ThreatManager.Threats.Count, "the generated home starts calm");
            Assert.IsFalse(_s.AwayFromStart);
        }

        [UnityTest]
        public IEnumerator ResultsNewRunAfterAGeneratedHomeStartsAnotherGeneratedHomeOnAFreshSeed()
        {
            yield return BootPlayable(RunLaunchRequest.GeneratedHomeRun(88));
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
                Assert.IsTrue(next.GeneratedHome, "a generated home is followed by another generated home");
                Assert.AreEqual(31337L, next.Seed, "a fresh seed");
                Assert.AreEqual(_s.GameClock.Scale, next.TimeScale, "the next run keeps this run's pacing");
                Assert.AreEqual(_boot.Launch.ClassId, next.ClassId);
            }
            finally
            {
                PlayableBootstrap.SceneLoader = previousLoader;
                PlayableBootstrap.RandomSeed = previousRandom;
                RunLaunchRequest.Pending = null;
            }
        }

        /// <summary>A collider that can hold a survivor: enabled, solid and active, and not a ceiling proxy (ceilings are 1 m placeholder boxes at the cell centre that the camera culls).</summary>
        static bool Solid(Collider c) => c != null && c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy && !(c is MeshCollider m && !m.convex)
            && !(c.transform.parent != null && c.transform.parent.name.StartsWith("Ceiling_"));

        /// <summary>The deepest overlap, in metres, between the docked lifeboat's solid colliders and the home's, with the pair that produced it.</summary>
        float DeepestDockOverlap(out string pair, out int overlapping)
        {
            pair = "";
            overlapping = 0;
            var lifeboat = (SceneShipRoot)_s.LifeboatShip.SceneRoot;
            Transform boatRoot = lifeboat.GameObject.transform;
            Collider[] boat = lifeboat.GameObject.GetComponentsInChildren<Collider>().Where(Solid).ToArray();
            Collider[] home = _boot.Host.ShipHost.HomeLoader.GameObject.GetComponentsInChildren<Collider>().Where(c => Solid(c) && !c.transform.IsChildOf(boatRoot)).ToArray();
            float worst = 0f;
            foreach (Collider a in boat)
                foreach (Collider b in home)
                {
                    if (!a.bounds.Intersects(b.bounds)) continue;
                    if (Physics.ComputePenetration(a, a.transform.position, a.transform.rotation, b, b.transform.position, b.transform.rotation, out _, out float depth) && depth > 0.02f)
                    {
                        overlapping++;
                        if (depth > worst)
                        {
                            worst = depth;
                            pair = a.transform.parent?.name + "/" + a.name + " b=" + a.bounds.center.ToString("0.00") + " s=" + a.bounds.size.ToString("0.00")
                                + " x " + b.transform.parent?.name + "/" + b.name + " b=" + b.bounds.center.ToString("0.00") + " s=" + b.bounds.size.ToString("0.00");
                        }
                    }
                }
            return worst;
        }

        [UnityTest]
        public IEnumerator GeneratedHomeLifeboatDocksNoDeeperIntoTheHomeThanTheAuthoredHubDoes()
        {
            // The docked lifeboat overlaps the home's dock seam by design (the seam's covered walls are suppressed). The authored hub is the
            // reference: a generated home must not sink the boat any deeper than that.
            yield return BootPlayable(RunLaunchRequest.GoldenShip());
            yield return FixedSteps(10);
            float golden = DeepestDockOverlap(out string goldenPair, out int goldenPairs);

            var lines = new List<string> { "golden hub: deepest overlap " + golden.ToString("0.000") + " m over " + goldenPairs + " collider pair(s) (" + goldenPair + ")" };
            float worstOverall = 0f;
            foreach (long seed in new long[] { 1234, 501, 502, 777, 4711, 31337 })
            {
                yield return BootPlayable(RunLaunchRequest.GeneratedHomeRun(seed));
                yield return FixedSteps(10); // the docked geometry reconciles after the first physics steps
                float worst = DeepestDockOverlap(out string pair, out int overlapping);
                worstOverall = Mathf.Max(worstOverall, worst);
                lines.Add("seed " + seed + ": deepest overlap " + worst.ToString("0.000") + " m over " + overlapping + " collider pair(s)" + (pair.Length != 0 ? " (" + pair + ")" : ""));
            }
            Debug.Log("[DockFit] " + string.Join(" | ", lines));
            Assert.LessOrEqual(worstOverall, golden + 0.05f, "a generated home must not take the docked lifeboat deeper than the authored hub does: " + string.Join("; ", lines));
        }

        /// <summary>Walks to the deck the target is on, using the home's ramp like a survivor does (generated homes can have two decks).</summary>
        IEnumerator GoToDeckOf(Vec3 target)
        {
            var player = _boot.Host.SceneState.Player;
            int wanted = target.Y > 3 ? 1 : 0;
            for (int guard = 0; guard < 3 && (player.GodotPosition.Y > 3 ? 1 : 0) != wanted; guard++)
            {
                _s.RefreshDeckTransitions();
                var transfer = _s.DeckTransitions.FirstOrDefault(d => d.DestinationDeck == wanted);
                Assert.IsNotNull(transfer, "a deck transition leads to deck " + wanted + " from " + player.GodotPosition);
                yield return WalkTo(transfer.GlobalPosition, 2.5f);
                player.RequestInteract();
                yield return FixedSteps(8);
            }
            Assert.AreEqual(wanted, player.GodotPosition.Y > 3 ? 1 : 0, "the real deck transfer reaches deck " + wanted);
        }

        /// <summary>Presses interact until <paramref name="done"/>: a closed door or another control in reach may take the first press, as it would for a player.</summary>
        IEnumerator PressUntil(System.Func<bool> done, int attempts = 6)
        {
            var player = _boot.Host.SceneState.Player;
            for (int i = 0; i < attempts && !done(); i++)
            {
                player.RequestInteract();
                yield return FixedSteps(8);
            }
        }

        [UnityTest]
        public IEnumerator GeneratedHomeWalkLootRepairAndDepartInTheRealScene_Seed2024() => GeneratedHomeRoute(2024);

        [UnityTest]
        public IEnumerator GeneratedHomeWalkLootRepairAndDepartInTheRealScene_Seed777() => GeneratedHomeRoute(777);

        /// <summary>
        /// The survivor's first hour at a generated home, physically: walk to and search the three guarantee containers, take the pickups, repair
        /// the flight path with the parts found, finish the onboarding tasks, walk to the lifeboat bridge and depart for the first wreck.
        /// </summary>
        IEnumerator GeneratedHomeRoute(long seed)
        {
            yield return BootPlayable(RunLaunchRequest.GeneratedHomeRun(seed));
            var player = _boot.Host.SceneState.Player;
            Assert.AreEqual(0, _s.ThreatManager.Threats.Count, "the home starts calm");
            foreach (string id in new[] { StartingHomeGuarantee.RepairCacheAId, StartingHomeGuarantee.RepairCacheBId, StartingHomeGuarantee.SurvivalStoresId })
            {
                var loot = _s.LootContainers.Single(l => l.ContainerId == id);
                yield return GoToDeckOf(loot.GlobalPosition);
                yield return WalkTo(loot.GlobalPosition);
                yield return PressUntil(() => loot.Searched);
                Assert.IsTrue(loot.Searched, "the survivor can reach and search " + id + "; handler=" + _s.LastInteractHandlerId);
            }
            foreach (var pickup in new[] { _s.ToolPickup, _s.JunctionCalibratorPickup })
            {
                if (pickup == null || pickup.Acquired) continue;
                yield return GoToDeckOf(pickup.GlobalPosition);
                yield return WalkTo(pickup.GlobalPosition, (float)pickup.InteractionRadius - 0.1f);
                yield return PressUntil(() => pickup.Acquired);
                Assert.IsTrue(pickup.Acquired, "the onboarding pickup is physically reachable; handler=" + _s.LastInteractHandlerId);
            }
            var needed = new HashSet<string> { "power", "navigation", "propulsion" };
            var repairPoints = _s.RepairPoints.Where(r => r.IsValid && needed.Contains(r.SystemId)).ToList();
            for (int guard = 0; guard < 24 && needed.Any(id => !_s.ShipSystemsManager.IsOperational(id)); guard++)
            {
                var next = repairPoints.Where(r => !r.TargetManager.GetSystem(r.SystemId).GetSubcomponent(r.SubcomponentId).IsFunctional())
                    .OrderBy(r => r.MinSkill).FirstOrDefault();
                Assert.IsNotNull(next, "a required repair remains available");
                yield return GoToDeckOf(next.GlobalPosition);
                yield return WalkAndFinishChannel(next.GlobalPosition);
            }
            foreach (string id in needed) Assert.IsTrue(_s.ShipSystemsManager.IsOperational(id), id + " repaired with the parts found at home");
            for (int guard = 0; guard < 40 && !_s.HomeObjectivesComplete; guard++)
            {
                var objective = _s.Interactables.FirstOrDefault(o => o.Active && !o.Completed);
                Assert.IsNotNull(objective, "an onboarding task remains");
                yield return GoToDeckOf(objective.GlobalPosition);
                yield return WalkTo(objective.GlobalPosition);
                player.RequestInteract();
                yield return FixedSteps(8);
            }
            Assert.IsTrue(_s.HomeObjectivesComplete, "the onboarding chain finishes on foot");
            Assert.IsFalse(_s.SliceComplete, "onboarding does not end this life: " + SurvivalReport());
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!_s.PropulsionExpandedState.CanPropel() && Time.realtimeSinceStartup < deadline && !_s.SliceComplete) yield return null;
            Assert.IsTrue(_s.PropulsionExpandedState.CanPropel(), "the repaired lifeboat can propel: " + GdJson.Stringify(_s.TravelCapability()));
            var bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return GoToDeckOf(bridge.GlobalPosition);
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
            Assert.IsFalse(_s.SliceComplete);
            Assert.IsTrue(_s.LootContainers.Any(l => l.ContainerId == "first_wreck_stores"), "the first wreck carries its emergency stores");
        }
    }
}
