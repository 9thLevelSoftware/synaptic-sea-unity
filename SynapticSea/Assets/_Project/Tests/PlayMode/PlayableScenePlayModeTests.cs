using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.App;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Game;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>
    /// Plan Phase 8 gate on the real Playable scene: the bootstrap boots golden coherent_ship_001 through the session's
    /// own load path; the player walks; interact resolves through <see cref="InteractionRegistry"/>; route-gate colliders
    /// follow the model; F5/F9 round-trips (with wounds and the web chart); propulsion is repaired through the repair points
    /// and the run travels to a breach_field, a dead_fleet and a hive derelict and back; real threats fight the player
    /// through the input path and the HUD; the gamepad inventory → pause → resume journey keeps focus; Title → New Run
    /// boots Playable, settings survive Title → play → pause → Title, and quit returns to Title.
    /// The golden ship's threats are left alive (its fallback encounter spawns beside the start room); a test that needs
    /// a quiet ship opts in with <see cref="QuietShip"/> and a reason.
    /// </summary>
    public class PlayableScenePlayModeTests : InputTestFixture
    {
        IStorage _previousStorage;
        IResourceReader _previousResources;
        ILog _previousLog;
        MemoryStorage _storage;
        PlayableBootstrap _boot;
        RunSession _s;
        /// <summary>The player's Unity position when the boot finished (before any physics step).</summary>
        Vector3? _spawnedAt;
        float _previousTimeScale;

        public override void Setup()
        {
            base.Setup();
            _previousStorage = CoreServices.UserStorage;
            _previousResources = CoreServices.Resources;
            _previousLog = CoreServices.Log;
            _storage = new MemoryStorage();
            AppServices.StorageOverride = _storage;
            RunLaunchRequest.Pending = null;
            RunReturnInfo.Clear();
            _spawnedAt = null;
            _previousTimeScale = Time.timeScale;
            PlayableBootstrap.Booted += OnBooted;
        }

        void OnBooted(PlayableBootstrap boot)
        {
            PlayerController p = boot.Host != null && boot.Host.SceneState != null ? boot.Host.SceneState.Player : null;
            _spawnedAt = p != null ? p.transform.position : (Vector3?)null;
        }

        public override void TearDown()
        {
            PlayableBootstrap.Booted -= OnBooted;
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
            base.TearDown();
        }

        /// <summary>Boots the Playable scene; these scene-layer tests run on the golden ship unless a request says otherwise
        /// (a request-less scene generates a New Run, see RunLifecyclePlayModeTests).</summary>
        IEnumerator BootPlayable(RunLaunchRequest request = null)
        {
            RunLaunchRequest.Pending = request ?? RunLaunchRequest.GoldenShip();
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName);
            yield return WaitForBoot();
        }

        IEnumerator WaitForBoot()
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline && (PlayableBootstrap.Current == null || !PlayableBootstrap.Current.IsBooted))
                yield return null;
            _boot = PlayableBootstrap.Current;
            Assert.IsNotNull(_boot, "the Playable scene has a bootstrap");
            Assert.IsTrue(_boot.IsBooted, "the run booted: " + _boot.BootFailure);
            _s = _boot.Session;
            yield return null;
        }

        /// <summary>
        /// Opt-in quiet ship for a test that needs one: removes the threats (golden 001 has no encounter markers, so the
        /// fallback encounter spawns beside the start room and kills an idle player in ~10 s).
        /// </summary>
        void QuietShip(string reason)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason), "a quiet ship needs a stated reason");
            _s.ThreatManager.InjectValidationEncounter(new GdArray(), Vec3.Zero);
            Assert.IsEmpty(_s.ThreatManager.Threats, "quiet ship: " + reason);
        }

        static IEnumerator FixedSteps(int n)
        {
            for (int i = 0; i < n; i++) yield return new WaitForFixedUpdate();
        }

        /// <summary>A key tap through queued keyboard state events (the fixture's Press needs a state pointer the editor's
        /// batch-mode keyboard buffers do not expose).</summary>
        static IEnumerator TapKey(Keyboard keyboard, Key key)
        {
            InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(key));
            yield return null;
            InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        PlayerController Player => _boot.Host.SceneState.Player;

        [UnityTest]
        public IEnumerator BootBuildsTheGoldenShipPlayerCameraAndHud()
        {
            yield return BootPlayable();
            Assert.IsTrue(_s.PlayableStarted);
            StringAssert.Contains("coherent_ship_001", _s.LayoutPath, "the explicit golden test request");
            Assert.AreEqual(5, _s.Interactables.Count);
            Assert.IsNotNull(Player, "player spawned");
            Assert.IsNotNull(_boot.Host.SceneState.CameraRig, "camera rig spawned");
            Camera cam = _boot.Host.SceneState.CameraRig.Camera;
            Assert.AreEqual(0, cam.cullingMask & (1 << PhysicsLayers.Ceiling), "no ceilings are drawn inside the ship");
            Assert.Greater(_boot.Host.ShipHost.HomeLoader.GameObject.GetComponentsInChildren<StructuralModule>(true).Count(m => m.layer == "ceiling"), 0,
                "ceiling modules stay in the scene for layout, save and integrity parity");
            Assert.IsTrue(_boot.Host.ShipHost.HomeLoader.IsInsideTree);
            Assert.AreEqual(_s.Interactables.Count, _boot.Host.InteractableViews.Keys.OfType<ObjectiveInteractable>().Count(), "one view per objective node");
            Assert.GreaterOrEqual(_boot.Host.ZoneViews.Count, 1, "route gate views");
            Assert.IsNotNull(_boot.Coordinator, "menu coordinator mounted");
            Assert.IsNotNull(_boot.Ui.Hud.Root, "HUD built");

            // The spawn pose is exact when the boot finishes. The first physics steps may then move the player: on golden 001
            // the life boat docks with its airlock edge on the home airlock's centre (DockPorts.ForDerelict/ForLifeboat), so
            // one of its walls runs through the start marker and the CharacterController depenetrates by radius + half a
            // wall + skin (~0.47 m). Assert the spawn at spawn time, then that the player settles on the floor near it.
            Vec3 start = _s.Loader.GetStartTransform().Origin;
            Assert.IsTrue(_spawnedAt.HasValue, "the boot spawned the player");
            Vec3 spawned = Frame.ToGodot(_spawnedAt.Value);
            Assert.AreEqual(start.X, spawned.X, 1e-3, "player spawned at the start pose (x)");
            Assert.AreEqual(start.Z, spawned.Z, 1e-3, "player spawned at the start pose (z)");
            Assert.AreEqual(start.Y + RunSession.PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, spawned.Y, 1e-3, "spawned at the nav floor + spawn height");
            yield return FixedSteps(40);
            Vec3 settled = Frame.ToGodot(Player.transform.position);
            Assert.IsTrue(Player.Controller.isGrounded, "the player settles on the floor");
            float drift = new Vector2(settled.X - start.X, settled.Z - start.Z).magnitude;
            Assert.LessOrEqual(drift, PlayerController.DefaultCollisionRadius + 0.15f, "at most a depenetration away from the start pose: " + settled);
        }

        [UnityTest]
        public IEnumerator PlayerWalks()
        {
            yield return BootPlayable();
            yield return FixedSteps(20);
            Vector3 origin = Player.transform.position;
            float farthest = 0f;
            foreach (var dir in new[] { new Vec3(1f, 0f, 0f), new Vec3(-1f, 0f, 0f), new Vec3(0f, 0f, 1f), new Vec3(0f, 0f, -1f) })
            {
                Player.TeleportTo(origin);
                Player.SetScriptedMoveDirection(dir);
                yield return FixedSteps(50);
                Player.ClearScriptedMoveDirection();
                Vector3 p = Player.transform.position;
                farthest = Mathf.Max(farthest, new Vector2(p.x - origin.x, p.z - origin.z).magnitude);
                Assert.Greater(p.y, origin.y - 1f, "the floor holds the player");
            }
            Assert.Greater(farthest, 1.5f, "the player walks away from the start marker");
            yield return null;
            Assert.IsTrue(_s.WorldTime > 0.0, "the session ticks every frame");
        }

        [UnityTest]
        public IEnumerator InteractOnObjectiveOneCompletesItThroughTheRegistry()
        {
            yield return BootPlayable();
            ObjectiveInteractable first = _s.GetInteractableBySequence(1);
            Assert.IsNotNull(first);
            InteractableView view = _boot.Host.InteractableViews[first];
            _boot.Host.SceneState.TeleportPlayer(first.GlobalPosition);
            yield return FixedSteps(2);
            _boot.Host.SceneState.Sensor.Refresh();
            Assert.IsTrue(view.PlayerOverlap, "the proximity sensor overlaps the objective trigger");
            Assert.IsTrue(first.CandidatePlayerInRange, "overlap reaches the model (candidate_player)");

            var claimed = new List<string>();
            _s.PlayableInteractionCompleted += (iid, oid, seq, type, room) => claimed.Add(iid);
            Player.RequestInteract();
            List<string> home = InteractionRegistry.OrderFor(SessionLocation.Home);
            Assert.AreEqual("home_objective", _s.LastInteractHandlerId, "handlers before home_objective declined; order " + string.Join(",", home));
            Assert.IsTrue(first.Completed);
            Assert.AreEqual(2, _s.CurrentObjectiveSequence);
            Assert.AreEqual(1, claimed.Count);
            yield return null;
            Assert.IsFalse(first.Active);
        }

        [UnityTest]
        public IEnumerator RouteGateColliderFollowsTheModel()
        {
            yield return BootPlayable();
            ZoneView gate = _boot.Host.ZoneViews.Values.First(z => z.Zone.Kind == "route_gate");
            Assert.IsTrue(gate.Blocker.enabled, "powered gate blocks while main power is down");
            Assert.AreEqual(PhysicsLayers.ZoneBlocker, gate.Blocker.gameObject.layer);
            var down = new Ray(gate.Blocker.bounds.center + Vector3.up * 5f, Vector3.down);
            Assert.IsTrue(gate.Blocker.Raycast(down, out _, 10f), "the gate collider is in the physics scene");
            Assert.AreEqual(0L, V.I64(gate.Zone.Meta["blocked_route_index"]), "gate 1 maps to the first blocked-route node");
            BoxCollider marker = _boot.Host.ShipHost.HomeLoader.View.GetBlockedRouteNodes()[0].GetComponentInChildren<BoxCollider>(true);
            Assert.IsNotNull(marker, "the blocked-route node carries a collider");
            Assert.IsTrue(marker.enabled, "the blocked-route node blocks while the gate is closed");
            var markerDown = new Ray(marker.bounds.center + Vector3.up * 5f, Vector3.down);
            Assert.IsTrue(marker.Raycast(markerDown, out _, 10f));

            Assert.IsTrue(_s.CompleteObjectiveSequence(1));
            Assert.IsTrue(_s.CompleteObjectiveSequence(2), "restore_systems opens the powered gates");
            yield return null;
            Assert.IsFalse(gate.Zone.CollisionEnabled);
            Assert.IsFalse(gate.Blocker.enabled, "collider follows passability");
            Assert.IsFalse(gate.Visual.activeSelf);
            Physics.SyncTransforms();
            Assert.IsFalse(gate.Blocker.Raycast(down, out _, 10f), "the open gate no longer blocks");
            Assert.IsFalse(marker.enabled, "the blocked-route node's collider follows its gate");
            Assert.IsFalse(marker.Raycast(markerDown, out _, 10f), "the route is physically open");
            foreach (ZoneView breach in _boot.Host.ZoneViews.Values.Where(z => z.Zone.Kind == "breach"))
                Assert.AreEqual(breach.Zone.CollisionEnabled, breach.Blocker.enabled, "breach collider follows the model");
        }

        [UnityTest]
        public IEnumerator F5SaveThenF9LoadRestoresTheRun()
        {
            // A batch-mode editor has no focused Game view; keyboard input would otherwise be routed to the editor.
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return BootPlayable();
            yield return FixedSteps(10);
            Vector3 savedAt = Player.transform.position;
            long scrapBefore = _s.InventoryState.GetQuantity("scrap_metal");

            yield return TapKey(keyboard, Key.F5);
            Assert.IsTrue(_storage.FileExists(SaveLoadService.WORLD_SLOT_FILE), "F5 wrote the world save");

            _s.InventoryState.AddItem("scrap_metal", 3);
            _boot.Host.SceneState.TeleportPlayer(Frame.ToGodot(savedAt + new Vector3(2f, 0f, 0f)));
            ShipLoaderNode loaderBefore = _boot.Host.ShipHost.HomeLoader;
            int viewsBefore = _boot.Host.InteractableViews.Count;

            yield return TapKey(keyboard, Key.F9);
            Assert.AreEqual(scrapBefore, _s.InventoryState.GetQuantity("scrap_metal"), "inventory restored");
            Assert.AreNotSame(loaderBefore, _boot.Host.ShipHost.HomeLoader, "the reload re-drove the home loader");
            Assert.IsFalse(loaderBefore.IsValid, "the old loader was freed");
            Assert.IsNotNull(Player, "player respawned");
            Assert.Less(Vector2.Distance(new Vector2(Player.transform.position.x, Player.transform.position.z), new Vector2(savedAt.x, savedAt.z)), 0.25f,
                "player back at the saved position");
            yield return null;
            Assert.AreEqual(viewsBefore, _boot.Host.InteractableViews.Count, "interaction views rebuilt");
            Assert.IsTrue(_boot.Host.InteractableViews.Values.All(v => v != null && v.Model.IsValid));
        }

        /// <summary>
        /// Makes the piloted life boat flyable through the real interaction path: the player stands at each repair point
        /// for propulsion and its power / navigation dependencies, then at each open-breach seal point, presses interact
        /// (claimed through <see cref="InteractionRegistry"/>), and the session tick runs the channel to completion,
        /// consuming parts and sealant. Golden 001's life boat bridge is also burning, and that suppression point shares a
        /// spot with repair points ahead of them in the registry, so the extinguisher channel runs there first. The drive
        /// then spools up on the session tick until it can propel (thrust is capped by hull integrity, hence the seals).
        /// </summary>
        IEnumerator MakeTheLifeboatFlyable()
        {
            ShipSystemsManager mgr = _s.ShipSystemsManager;
            Assert.IsFalse(mgr.IsOperational("propulsion"), "golden 001 starts with propulsion down");
            // Setup, not a shortcut: the points still gate on these parts, tools and skill and consume what they use.
            foreach (string part in new[] { "reactor_core", "power_cell", "power_cell", "circuit_board", "circuit_board", "circuit_board", "data_core", "sensor_module" })
                _s.InventoryState.AddItem(part, 1);
            _s.InventoryState.AddItem("plasma_cutter", 1);
            _s.InventoryState.AddItem("welder", 1);
            _s.InventoryState.AddItem("fire_extinguisher", 1);
            _s.InventoryState.AddItem("hull_sealant", 6);
            // High repair skill shortens the channels (1 + 0.1 per level above the minimum): golden 001's home life support
            // is starved while power is down and an idle player dies at ~29 s, so power must come back well before that.
            _s.PlayerProgression.Skills["repair"] = 20L;
            var needed = new HashSet<string> { "power", "navigation", "propulsion" };
            var log = new List<string>();
            Time.timeScale = 20f; // ~30 s of simulated channels in a few real seconds; restored here and in TearDown

            for (int guard = 0; guard < 24 && !mgr.IsOperational("propulsion"); guard++)
            {
                RepairPoint next = _s.RepairPoints.FirstOrDefault(rp => rp.IsValid && !rp.Repaired && needed.Contains(rp.SystemId));
                Assert.IsNotNull(next, "a repair point remains for a propulsion dependency: " + string.Join(" | ", log));
                yield return InteractAndFinishChannels(next.GlobalPosition, next.NodeName, log);
            }
            Assert.IsTrue(mgr.IsOperational("propulsion"), "propulsion restored through the repair points: " + string.Join(" | ", log));
            Assert.AreEqual(0, _s.InventoryState.GetQuantity("reactor_core"), "the repairs consumed their parts");

            for (int guard = 0; guard < 16; guard++)
            {
                BreachSealPoint seal = _s.BreachSealPoints.FirstOrDefault(sp => sp.IsValid && !sp.Sealed
                    && V.Bool(_s.HullIntegrityState.Compartments.GetDictOrEmpty(sp.CompartmentId).Get("breach_open", false)));
                if (seal == null) break;
                yield return InteractAndFinishChannels(seal.GlobalPosition, seal.NodeName, log);
            }

            float spool = Time.realtimeSinceStartup + 20f;
            while (!_s.PropulsionExpandedState.CanPropel() && !_s.SliceComplete && Time.realtimeSinceStartup < spool) yield return null;
            Time.timeScale = _previousTimeScale;
            Assert.IsFalse(_s.SliceComplete, "the player survived the repairs: " + string.Join(" | ", log));
            Assert.IsTrue(_s.PropulsionExpandedState.CanPropel(), "the drive spooled up: " + GdJson.Stringify(_s.PropulsionExpandedState.GetSummary())
                                                                + " hull " + _s.HullIntegrityState.AverageIntegrity() + " | " + string.Join(" | ", log));
        }

        bool AnyChannel() =>
            _s.RepairPoints.Any(rp => rp.IsValid && rp.Channeling)
            || _s.FireSuppressionPoints.Any(fp => fp.IsValid && fp.Channeling)
            || _s.BreachSealPoints.Any(sp => sp.IsValid && sp.Channeling);

        /// <summary>Stands at <paramref name="at"/>, presses interact once and waits until no repair / fire / seal channel runs.</summary>
        IEnumerator InteractAndFinishChannels(Vec3 at, string what, List<string> log)
        {
            Assert.IsFalse(_s.SliceComplete, "the player is alive: " + string.Join(" | ", log));
            _boot.Host.SceneState.TeleportPlayer(at);
            yield return new WaitForFixedUpdate();
            Player.RequestInteract();
            string handler = _s.LastInteractHandlerId;
            log.Add("t=" + _s.WorldTime.ToString("F1") + " " + what + " -> " + handler);
            Assert.That(handler, Is.EqualTo("repair_point").Or.EqualTo("fire_suppression_point").Or.EqualTo("breach_seal_point"),
                "interact at " + what + " is claimed by a repair, fire suppression or seal point: " + string.Join(" | ", log));
            float deadline = Time.realtimeSinceStartup + 30f;
            while (AnyChannel() && !_s.SliceComplete && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsFalse(AnyChannel(), "the channel at " + what + " finished: " + string.Join(" | ", log));
        }

        /// <summary>Travels to an in-range marker and back, checking the built derelict view, the kit and the re-peg.</summary>
        IEnumerator TravelAndReturn(string markerId, string expectedKitId)
        {
            GdDict result = _s.TravelToMarkerId(markerId);
            Assert.IsTrue(result.GetBool("success"), "travel to " + markerId + ": " + GdJson.Stringify(result));
            yield return null;
            Assert.IsTrue(_s.AwayFromStart);
            Assert.AreEqual(expectedKitId, V.Str(_s.CurrentShip.BuiltLayout.Get("kit_id", "")), "marker " + markerId + " kit");
            var derelict = _s.CurrentShip.SceneRoot as ShipLoaderNode;
            Assert.IsNotNull(derelict, "the derelict is a built loader view");
            Assert.IsTrue(derelict.IsInsideTree && derelict.GameObject.activeInHierarchy, "attached and shown");
            Assert.AreEqual(Frame.ToUnity(RunSession.DERELICT_DOCK_OFFSET).x, derelict.GameObject.transform.position.x, 1e-3, "placed at DERELICT_DOCK_OFFSET");
            Assert.Greater(derelict.CountCollisionShapes(), 0, expectedKitId + " built its colliders");
            Assert.Less(Vector3.Distance(Player.transform.position, derelict.GameObject.transform.position), 40f, "the player was re-pegged with the ride");
            var lifeboat = (SceneShipRoot)_s.LifeboatShip.SceneRoot;
            Assert.Less(Vector3.Distance(lifeboat.GameObject.transform.position, derelict.GameObject.transform.position), 60f, "the lifeboat docked to the derelict");
            Assert.IsTrue(_boot.Host.InteractableViews.Keys.Any(m => m is DockPortBarrier b && b.Parent == derelict), "derelict seam barrier view");

            Assert.IsTrue(_s.TravelHome());
            yield return null;
            Assert.IsFalse(_s.AwayFromStart);
            Assert.IsFalse(derelict.IsValid, "the derelict root was freed");
            Assert.IsTrue(derelict.GameObject == null, "and destroyed");
            Assert.Less(Vector3.Distance(Player.transform.position, _boot.Host.ShipHost.HomeLoader.GameObject.transform.position), 40f, "player carried home");
            Assert.IsFalse(_boot.Host.InteractableViews.Keys.Any(m => m.Parent == (IShipSceneRoot)derelict), "derelict views removed");
        }

        static List<string> WoundIds(WoundState wounds) =>
            wounds.Wounds.OfType<GdDict>().Select(w => V.Str(w.Get("wound_id", ""))).ToList();

        [UnityTest]
        public IEnumerator WoundsAndTheWebChartSurviveF5AndF9()
        {
            KeyboardToGameView();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return BootPlayable();
            // The scanner needs navigation (and its power) online before a scan can chart anything.
            QuietShip("charting needs the power and navigation repairs first (~40 s of simulated channels beside the start room, where golden 001's fallback encounter kills an idle player in ~10 s)");
            yield return MakeTheLifeboatFlyable();

            string wound = _s.WoundState.ApplyWound(new GdDict { { "kind", WoundState.KIND_LACERATION }, { "body_part", WoundState.BODY_ARM }, { "severity", 0.6 } });
            Assert.IsNotEmpty(wound);
            _s.InventoryState.AddItem(RunSession.BANDAGE_ITEM_IDS[0], 1);
            Assert.IsTrue(_s.TryBandageWound(wound), "bandaged through the session");
            _s.InventoryState.AddItem("web_chart", 1);
            _s.Scan();
            long charted = _s.WebChartState.GetKnownCount();
            Assert.Greater(charted, 0, "a scan with a web chart records the markers it sees");
            List<string> savedWounds = WoundIds(_s.WoundState);
            GdDict savedWound = _s.WoundState.GetWound(wound).DeepCopy();

            yield return TapKey(keyboard, Key.F5);
            Assert.IsTrue(_storage.FileExists(SaveLoadService.WORLD_SLOT_FILE), "F5 wrote the world save");
            string later = _s.WoundState.ApplyWound(new GdDict { { "kind", WoundState.KIND_BURN }, { "wound_id", "after_save" } });
            _s.WebChartState.ApplySummary(new GdDict());
            Assert.AreEqual(0, _s.WebChartState.GetKnownCount());

            yield return TapKey(keyboard, Key.F9);
            List<string> loaded = WoundIds(_s.WoundState);
            CollectionAssert.IsSubsetOf(savedWounds, loaded, "the saved wounds came back");
            CollectionAssert.DoesNotContain(loaded, later, "a wound taken after the save is gone");
            GdDict restored = _s.WoundState.GetWound(wound);
            Assert.AreEqual(savedWound.Get("kind"), restored.Get("kind"));
            Assert.AreEqual(savedWound.Get("body_part"), restored.Get("body_part"), "the body part round-trips");
            Assert.AreEqual(V.Bool(savedWound.Get("bandaged", false)), V.Bool(restored.Get("bandaged", false)));
            Assert.AreEqual(charted, _s.WebChartState.GetKnownCount(), "the web chart came back");
        }

        const string HazardKit = "ship_structural_hazard";
        const string IndustrialKit = "ship_structural_industrial";
        const string BiomatterKit = "ship_structural_biomatter";

        [UnityTest]
        public IEnumerator RepairPropulsionThenTravelToBreachFieldDeadFleetAndHiveDerelictsAndBack()
        {
            yield return BootPlayable();
            QuietShip("the propulsion repairs run ~45 s of simulated channel time beside the start room, where golden 001's fallback encounter kills an idle player in ~10 s");
            yield return MakeTheLifeboatFlyable();

            // The run's first travel follows the first-run contract (breach_field), whichever marker was picked.
            Assert.AreEqual("breach_field", V.Str(_s.FirstRunContract.Contract.Get("biome_id", "")));
            List<string> markers = _s.ScannableMarkerIds();
            Assert.IsNotEmpty(markers, "markers in scanner range");
            yield return TravelAndReturn(markers[0], HazardKit);

            // Later travels generate from the marker: a dead_fleet (industrial kit) and a hive-template (biomatter kit) derelict.
            var kitByMarker = new Dictionary<string, string>();
            string KitOf(string id)
            {
                if (!kitByMarker.TryGetValue(id, out string kit)) kitByMarker[id] = kit = _s.MarkerKitId(id);
                return kit;
            }
            string deadFleet = _s.ScannableMarkerIds().FirstOrDefault(id => KitOf(id) == IndustrialKit);
            Assert.IsNotNull(deadFleet, "a dead_fleet derelict in range: " + string.Join(", ", kitByMarker.Select(kv => kv.Key + "=" + kv.Value)));
            yield return TravelAndReturn(deadFleet, IndustrialKit);
            kitByMarker.Clear(); // the world position moved with the travel; ids stay stable but re-check
            string hive = _s.ScannableMarkerIds().FirstOrDefault(id => KitOf(id) == BiomatterKit);
            Assert.IsNotNull(hive, "a hive derelict in range: " + string.Join(", ", kitByMarker.Select(kv => kv.Key + "=" + kv.Value)));
            yield return TravelAndReturn(hive, BiomatterKit);
            Assert.GreaterOrEqual(_s.GetVisitedShipIds().Count, 3, "three derelicts visited");
        }

        [UnityTest]
        public IEnumerator GamepadInventoryPauseResumeRestoresFocus()
        {
            var gamepad = InputSystem.AddDevice<Gamepad>();
            yield return BootPlayable();
            _s.InventoryState.AddItem("scrap_metal", 2);
            _s.InventoryState.AddItem("ration_pack", 1);
            MenuCoordinator c = _boot.Coordinator;

            yield return Tap(gamepad.buttonNorth);
            Assert.IsTrue(_boot.Ui.Inventory.IsOpen(), "Triangle/Y opens the inventory");
            Assert.AreSame(_boot.Ui.Inventory, c.Stack.Top);
            Assert.IsFalse(c.Stack.SimulationPaused, "inspection is LIVE");
            Assert.IsFalse(_boot.Input.Player.enabled, "gameplay input blocked under the inspection");
            double liveTime = _s.WorldTime;
            yield return null;
            Assert.Greater(_s.WorldTime, liveTime, "the simulation keeps running under a LIVE panel");
            string token = c.Stack.Top.CaptureFocusToken();

            yield return Tap(gamepad.startButton);
            Assert.AreSame(c.MenuPanel, c.Stack.Top, "Start pauses over the inventory");
            Assert.IsTrue(c.Stack.SimulationPaused);
            double pausedTime = _s.WorldTime;
            yield return null;
            yield return null;
            Assert.AreEqual(pausedTime, _s.WorldTime, "the session does not tick while paused");

            c.MenuState.SetFocusIndex(0);
            yield return Tap(gamepad.buttonSouth);
            Assert.AreSame(_boot.Ui.Inventory, c.Stack.Top, "Resume returns to the inventory");
            Assert.IsFalse(c.Stack.SimulationPaused);
            Assert.AreEqual(token, c.Stack.Top.CaptureFocusToken(), "focus restored to the same row");
            Assert.AreEqual(2, _s.InventoryState.GetQuantity("scrap_metal"), "the held confirm did not act on the inventory");

            yield return Tap(gamepad.buttonEast);
            Assert.IsFalse(_boot.Ui.Inventory.IsOpen(), "Back closes the inventory");
            Assert.IsTrue(c.Stack.IsEmpty);
            yield return null;
            Assert.IsTrue(_boot.Input.Player.enabled, "gameplay input restored");
        }

        [UnityTest]
        public IEnumerator TitleNewRunBootsPlayableAndQuitReturnsToTitle()
        {
            SceneManager.LoadScene("Boot");
            TitleScreen title = null;
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                title = SceneManager.GetActiveScene().name == RunLaunchRequest.TitleSceneName ? Object.FindAnyObjectByType<TitleScreen>() : null;
                if (title != null && title.IsBuilt) break;
                yield return null;
            }
            Assert.IsNotNull(title, "Boot reached the title");
            MenuCoordinator tc = title.Coordinator;
            tc.MenuState.SetFocusIndex(tc.MenuPanel.Rows.ToList().FindIndex(r => r.Id == "start"));
            tc.HandleUiInput(UiCommand.Accept);
            Assert.IsNotNull(title.NewRunSetup, "New Run opens the setup");
            title.NewRunSetup.SetSeed(RunLaunchRequest.DefaultSeed);
            title.NewRunSetup.FocusRow(NewRunSetupPanel.RowStart);
            title.NewRunSetup.Consume(UiCommand.Accept);

            yield return WaitForBoot();
            Assert.AreEqual(RunLaunchRequest.PlayableSceneName, SceneManager.GetActiveScene().name);
            Assert.IsNotNull(_boot.Launch, "the title's request was consumed");
            Assert.AreEqual(RunLaunchMode.NewRun, _boot.Launch.Mode);
            Assert.IsNull(RunLaunchRequest.Pending);
            StringAssert.StartsWith(PlayableBootstrap.RunsDir, _s.LayoutPath, "New Run generates the home ship under user://runs/");
            Assert.IsNotNull(Player, "the player spawned");

            _s.QuitToTitle();
            title = null;
            deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                title = SceneManager.GetActiveScene().name == RunLaunchRequest.TitleSceneName ? Object.FindAnyObjectByType<TitleScreen>() : null;
                if (title != null && title.IsBuilt) break;
                yield return null;
            }
            Assert.IsNotNull(title, "quit to title returned to the Title scene");
            Assert.IsTrue(PlayableBootstrap.Current == null || !PlayableBootstrap.Current, "the playable scene unloaded");
        }

        static IEnumerator WaitForTitle(System.Action<TitleScreen> found)
        {
            TitleScreen title = null;
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                title = SceneManager.GetActiveScene().name == RunLaunchRequest.TitleSceneName ? Object.FindAnyObjectByType<TitleScreen>() : null;
                if (title != null && title.IsBuilt) break;
                yield return null;
            }
            Assert.IsNotNull(title, "reached the Title scene");
            found(title);
        }

        static void Focus(MenuCoordinator c, string itemId)
        {
            int index = c.MenuPanel.Rows.ToList().FindIndex(r => r.Id == itemId);
            Assert.GreaterOrEqual(index, 0, "menu row " + itemId + " in " + c.GetCurrentMenu());
            c.MenuState.SetFocusIndex(index);
        }

        GdDict StoredSettings()
        {
            Assert.IsTrue(_storage.FileExists(UserSettingsStore.SettingsPath), "user://settings.json exists");
            return GdJson.ParseString(_storage.ReadText(UserSettingsStore.SettingsPath)) as GdDict;
        }

        [UnityTest]
        public IEnumerator SettingsPersistFromTitleThroughPlayAndPauseBackToTitle()
        {
            SceneManager.LoadScene("Boot");
            TitleScreen title = null;
            yield return WaitForTitle(t => title = t);
            MenuCoordinator tc = title.Coordinator;
            Assert.AreEqual(1.0, AppServices.Instance.Settings.GetTextScale(), 1e-9, "fresh preferences");
            Assert.IsTrue(AppServices.Instance.Settings.IsCaptionsEnabled());

            Focus(tc, "settings");
            tc.HandleUiInput(UiCommand.Accept);
            Assert.AreEqual("settings_menu", tc.GetCurrentMenu());
            Focus(tc, "text_scale");
            tc.HandleUiInput(UiCommand.Right);
            Assert.AreEqual(1.5, tc.SettingsState.GetTextScale(), 1e-9, "the title changed the text scale");
            Assert.AreEqual(1.5, StoredSettings().GetFloat("text_scale"), 1e-9, "the title change is in user://settings.json");
            tc.HandleUiInput(UiCommand.Cancel);

            Focus(tc, "start");
            tc.HandleUiInput(UiCommand.Accept);
            title.NewRunSetup.SetSeed(RunLaunchRequest.DefaultSeed);
            title.NewRunSetup.FocusRow(NewRunSetupPanel.RowStart);
            title.NewRunSetup.Consume(UiCommand.Accept);
            yield return WaitForBoot();

            MenuCoordinator c = _boot.Coordinator;
            Assert.AreEqual(1.5, c.SettingsState.GetTextScale(), 1e-9, "the title's text scale applies in play");
            Assert.IsTrue(_boot.Ui.Hud.Root.ClassListContains(AccessibilitySettings.ClassScale150), "the HUD reflowed at 1.5x");

            c.HandleUiInput(UiCommand.Pause);
            Assert.AreEqual("pause_menu", c.GetCurrentMenu());
            Assert.IsTrue(c.Stack.SimulationPaused);
            Focus(c, "settings");
            c.HandleUiInput(UiCommand.Accept);
            Assert.AreEqual("settings_menu", c.GetCurrentMenu());
            Focus(c, "captions");
            c.HandleUiInput(UiCommand.Accept);
            Assert.IsFalse(_s.SettingsState.IsCaptionsEnabled(), "the pause menu turned captions off");
            GdDict stored = StoredSettings();
            Assert.IsFalse(stored.GetBool("captions", true), "the pause change is in user://settings.json");
            Assert.AreEqual(1.5, stored.GetFloat("text_scale"), 1e-9, "the pause change kept the title's text scale");
            c.HandleUiInput(UiCommand.Cancel);

            Focus(c, "quit_main");
            c.HandleUiInput(UiCommand.Accept);
            yield return WaitForTitle(t => title = t);
            Assert.AreEqual(1.5, AppServices.Instance.Settings.GetTextScale(), 1e-9, "the title keeps the text scale");
            Assert.IsFalse(AppServices.Instance.Settings.IsCaptionsEnabled(), "the title sees the pause-menu change");
            Assert.AreEqual(1.5, title.Coordinator.SettingsState.GetTextScale(), 1e-9);
            Assert.IsTrue(title.ScreenRoot.ClassListContains("scale-150"), "the title reflows at 1.5x");
            stored = StoredSettings();
            Assert.AreEqual(1.5, stored.GetFloat("text_scale"), 1e-9);
            Assert.IsFalse(stored.GetBool("captions", true));
        }

        // ================================================================== W2a in-play integration

        static void KeyboardToGameView() =>
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

        /// <summary>
        /// Replaces the ship's threats with one real archetype 1.2 m from the player, on a side the session's own line-of-sight
        /// probe sees clearly. <see cref="ThreatRuntime.InjectValidationEncounter"/> places a marker without a local position
        /// on a 4 m ring around the anchor (index 0: +4 m on x), so the anchor is offset to land the threat on that spot.
        /// (Anchoring at the player put the stalker 4 m further out, behind the airlock wall: no line of sight, awareness
        /// 0.12 against the 0.85 detection threshold, and it idled forever.)
        /// </summary>
        ThreatAIState InjectThreatBesidePlayer(string archetype = "stalker")
        {
            Vec3 player = Frame.ToGodot(Player.transform.position);
            Vec3 eye = player + new Vec3(0f, 1.2f, 0f);
            var probe = new PhysicsLineOfSightProbe();
            Vec3? spot = null;
            foreach (Vec3 offset in new[] { new Vec3(1.2f, 0f, 0f), new Vec3(-1.2f, 0f, 0f), new Vec3(0f, 0f, 1.2f), new Vec3(0f, 0f, -1.2f) })
            {
                Vec3 candidate = player + offset;
                if (!probe.IntersectRay(eye, candidate + new Vec3(0f, 1f, 0f), out _)) { spot = candidate; break; }
            }
            Assert.IsTrue(spot.HasValue, "a clear spot beside the player at " + player);
            _s.ThreatManager.InjectValidationEncounter(GdArray.Of(archetype), spot.Value - new Vec3(4f, 0f, 0f));
            Assert.AreEqual(1, _s.ThreatManager.Threats.Count, "validation encounter spawned");
            ThreatAIState threat = _s.ThreatManager.Threats[0];
            Vec3 at = new Vec3(V.F64(threat.WorldPosition[0]), V.F64(threat.WorldPosition[1]), V.F64(threat.WorldPosition[2]));
            Assert.Less(at.DistanceTo(spot.Value), 1e-3, "the threat stands on the chosen spot");
            return threat;
        }

        [UnityTest]
        public IEnumerator ARealThreatDetectsAndAttacksThePlayerAndTheHudShowsTheHit()
        {
            yield return BootPlayable();
            yield return FixedSteps(40); // settle on the floor (see BootBuildsTheGoldenShipPlayerCameraAndHud)
            ThreatAIState threat = InjectThreatBesidePlayer();
            var hits = new List<double>();
            _boot.Host.PlayerDamaged += (damage, archetype, from) => hits.Add(damage);
            double health = _s.VitalsState.Health;
            float deadline = Time.realtimeSinceStartup + 20f;
            while (hits.Count == 0 && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsNotEmpty(hits, "the stalker hit the player through ThreatRuntime → ThreatAttacked → RunSessionHost.PlayerDamaged (state "
                                    + threat.State + ", awareness " + threat.AwarenessScore + ", los " + _s.ThreatManager.EngagedLos.Get(threat.InstanceId, "?") + ")");
            Assert.AreEqual(ThreatAIState.STATE_ATTACK, threat.State, "detection resolved into the attack pipeline");
            Assert.Less(_s.VitalsState.Health, health, "the hit went through the damage pipeline to the vitals");
            yield return null;
            StringAssert.Contains("Hit", _boot.Ui.Hud.Vitals.DamageIndicatorText, "the HUD damage indicator shows the hit");
        }

        [UnityTest]
        public IEnumerator CombatAndHotbarKeysDriveTheSessionAndRespectTheModalStack()
        {
            KeyboardToGameView();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return BootPlayable();
            yield return FixedSteps(5);

            ThreatAIState threat = InjectThreatBesidePlayer();
            double health = threat.Health;
            var killed = new List<string>();
            _s.ThreatManager.ThreatKilled += record => killed.Add(V.Str(record.Get("instance_id", "")));
            _s.InventoryState.AddItem("crowbar", 1);
            Assert.IsTrue(_s.EquipmentState.Equip("crowbar").GetBool("ok"));
            yield return TapKey(keyboard, Key.F);
            Assert.Less(threat.Health, health, "attack_primary hit the threat through the session");
            StringAssert.Contains("Crowbar", _boot.Ui.Hud.Vitals.WeaponLine, "the HUD shows the session's hotbar text");
            for (int i = 0; i < 40 && killed.Count == 0; i++)
            {
                Assert.IsTrue(_boot.Host.GameplayInputAllowed || _s.SliceComplete, "input stays live while fighting");
                yield return TapKey(keyboard, Key.F);
            }
            CollectionAssert.Contains(killed, threat.InstanceId, "repeated attack_primary killed the stalker (health " + threat.Health + ")");
            Assert.IsFalse(_s.ThreatManager.Threats.Contains(threat), "the dead threat left the runtime");

            _s.InventoryState.AddItem("flare_pistol", 1);
            _s.InventoryState.AddItem("flare_round", 4);
            Assert.IsTrue(_s.EquipmentState.Equip("flare_pistol").GetBool("ok"));
            long loaded = _s.AmmoState.Loaded("flare_pistol");
            yield return TapKey(keyboard, Key.R);
            Assert.IsTrue(_s.AmmoState.IsReloading(), "reload_weapon began a reload");
            float reloadDeadline = Time.realtimeSinceStartup + 15f;
            while (_s.AmmoState.IsReloading() && Time.realtimeSinceStartup < reloadDeadline) yield return null;
            Assert.IsFalse(_s.AmmoState.IsReloading(), "the reload finished on the session tick");
            Assert.Greater(_s.AmmoState.Loaded("flare_pistol"), loaded, "the magazine was filled from the reserve");

            _s.InventoryState.AddItem("nutrient_paste", 3);
            Assert.IsTrue(_s.AssignHotbarSlot(0, "nutrient_paste"));
            long paste = _s.InventoryState.GetQuantity("nutrient_paste");
            yield return TapKey(keyboard, Key.Digit1);
            Assert.AreEqual(paste - 1, _s.InventoryState.GetQuantity("nutrient_paste"), "hotbar_1 used the slot");

            _boot.Ui.OpenInventorySelf();
            yield return null;
            Assert.IsFalse(_boot.Host.GameplayInputAllowed, "a LIVE inspection consumes gameplay input");
            yield return TapKey(keyboard, Key.Digit1);
            Assert.AreEqual(paste - 1, _s.InventoryState.GetQuantity("nutrient_paste"), "no hotbar use under the inventory");
            Assert.IsNull(_boot.Host.RequestHotbar(0), "the host gate refuses as well");
            _boot.Ui.Inventory.Close();
        }

        [UnityTest]
        public IEnumerator InteractPressAndReleaseDriveHoldToWork()
        {
            KeyboardToGameView();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return BootPlayable();
            yield return FixedSteps(2);
            Assert.IsFalse(_s.IsWorkInteractHeld);
            InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(Key.E));
            yield return null;
            Assert.IsTrue(_s.IsWorkInteractHeld, "interact press -> BeginWorkHold");
            InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null;
            Assert.IsFalse(_s.IsWorkInteractHeld, "interact release -> EndWorkHold");
        }

        [UnityTest]
        public IEnumerator StoredSettingsApplyInPlayAndAChangeSavesTheMergedState()
        {
            var stored = SettingsStateSchema.DefaultPayload();
            stored["text_scale"] = 1.5;
            stored["captions"] = false;
            stored[SettingsState.AudioBusVolumesKey] = new GdDict { { AudioEventSeam.BUS_MUSIC, -20.0 } };
            _storage.WriteText(UserSettingsStore.SettingsPath, GdJson.Stringify(stored, "\t"));
            yield return BootPlayable();

            MenuCoordinator c = _boot.Coordinator;
            Assert.AreSame(_s.SettingsState, c.SettingsState, "the pause menu edits the session's settings");
            Assert.AreSame(_s.TutorialState, c.TutorialState, "the banner and Codex read the session's tutorials");
            Assert.AreEqual(1.5, c.SettingsState.GetTextScale(), "stored preferences applied in play");
            Assert.IsFalse(_s.SettingsState.IsCaptionsEnabled());
            Assert.AreEqual(-20.0, _s.AudioManager.GetBusVolume(AudioEventSeam.BUS_MUSIC), 1e-6, "stored bus volume applied to the session audio");
            Assert.IsTrue(_boot.Ui.Hud.Root.ClassListContains(AccessibilitySettings.ClassScale150), "HUD accessibility applied at mount");

            c.AudioSettingsPanel.OnCaptionToggled(true);
            GdDict saved = GdJson.ParseString(_storage.ReadText(UserSettingsStore.SettingsPath)) as GdDict;
            Assert.IsNotNull(saved);
            Assert.AreEqual(1.5, saved.GetFloat("text_scale"), "a pause-menu change does not clobber the stored scale");
            Assert.IsTrue(saved.GetBool("captions"));
            Assert.AreEqual(-20.0, saved.GetDict(SettingsState.AudioBusVolumesKey).GetFloat(AudioEventSeam.BUS_MUSIC));

            Assert.IsTrue(_s.RequestSave());
            _boot.Ui.OnDevShortcut("load_run");
            Assert.AreEqual(1.5, c.SettingsState.GetTextScale(), "a load keeps the player's preferences");
            Assert.AreEqual(-20.0, _s.AudioManager.GetBusVolume(AudioEventSeam.BUS_MUSIC), 1e-6);
        }

        [UnityTest]
        public IEnumerator HomeShipAffordancesWorldLabelsAndComponentMarkersAreBuilt()
        {
            yield return BootPlayable();
            yield return null;
            AffordanceView affordances = _boot.Host.Affordances;
            Assert.IsTrue(affordances.Props.Keys.Any(k => k.StartsWith(ReadabilityPropFactory.OBJECTIVE_PREFIX)), "objective props");
            Assert.IsTrue(affordances.Props.ContainsKey(ReadabilityPropFactory.ENTRY_NAME), "entry beacon");
            Assert.IsTrue(affordances.Props.ContainsKey(ReadabilityPropFactory.DESTINATION_NAME), "destination core");
            Assert.IsTrue(affordances.Vfx.Any(v => v != null && v.name.Contains("beacon_blue")), "beacon_blue glow hook");
            Assert.IsTrue(affordances.Vfx.Any(v => v != null && v.name.Contains("reactor_green")), "reactor_green glow hook");
            Assert.IsTrue(_boot.Host.WorldLabels.Has(AffordanceView.AffordanceLabelPrefix + "objective_01"), "objective world label");
            Assert.Greater(_boot.Ui.Hud.WorldLabelLayer.childCount, 0, "labels are drawn into the HUD label layer");
            Assert.AreEqual(_s.ComponentMarkers.Count, _boot.Host.ComponentMarkers.Markers.Count, "one placeholder per mounted component");

            int blocked = affordances.BlockedVisibleCount;
            Assert.IsTrue(_s.CompleteObjectiveSequence(1));
            Assert.IsTrue(_s.CompleteObjectiveSequence(2), "restore_systems clears the blocked affordances");
            yield return null;
            Assert.AreEqual(0, affordances.BlockedVisibleCount, "blocked props hidden (was " + blocked + ")");
        }

        [UnityTest]
        public IEnumerator ThreatAttackFeedbackReachesTheViewsAndDenialsToast()
        {
            yield return BootPlayable();
            _boot.Ui.OnPanelToggle("ui_open_map");
            StringAssert.Contains(SessionUiBridge.NoWebChartText, _boot.Ui.Hud.ToastText, "chart denial toast");

            ThreatAIState threat = InjectThreatBesidePlayer();
            yield return null;
            Assert.IsTrue(_boot.Host.Threats.Nodes.ContainsKey(threat.InstanceId), "the injected threat has a placeholder");
            var handled = new List<string>();
            var deaths = new List<string>();
            _boot.Host.Threats.AttackHandled += (id, kind) => handled.Add(kind);
            _boot.Host.Threats.DeathPlayed += id => deaths.Add(id);
            _s.InventoryState.AddItem("crowbar", 1);
            Assert.IsTrue(_s.EquipmentState.Equip("crowbar").GetBool("ok"));
            for (int i = 0; i < 30 && deaths.Count == 0; i++)
            {
                _boot.Host.RequestAttack();
                yield return null;
            }
            Assert.Contains(ThreatRuntime.ATTACK_TARGET_THREAT, handled, "weapon hits reach the threat view");
            Assert.Contains(threat.InstanceId, deaths, "the kill played the death effect");
            Assert.IsFalse(_boot.Host.Threats.Nodes.ContainsKey(threat.InstanceId), "the dead threat left the view");

            _boot.Ui.Hud.ShowDamage(7, threat.ArchetypeId);
            StringAssert.Contains("Hit", _boot.Ui.Hud.Vitals.DamageIndicatorText, "HUD damage indicator");
        }
    }
}
