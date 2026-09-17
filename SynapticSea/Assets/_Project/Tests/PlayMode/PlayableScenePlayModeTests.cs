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
    /// follow the model; F5/F9 round-trips; travel to a generated derelict and back re-pegs the scene; the gamepad
    /// inventory → pause → resume journey keeps focus; Title → New Run boots Playable and quit returns to Title.
    /// </summary>
    public class PlayableScenePlayModeTests : InputTestFixture
    {
        IStorage _previousStorage;
        IResourceReader _previousResources;
        ILog _previousLog;
        MemoryStorage _storage;
        PlayableBootstrap _boot;
        RunSession _s;

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
        }

        public override void TearDown()
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
            // The golden ship has no encounter markers, so fallback threats spawn beside the start room and kill an idle
            // player in ~10 s (HeadlessSessionTests); these scene tests are about the scene layer.
            _s.ThreatManager.Threats.Clear();
            yield return null;
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
            Assert.AreEqual(_s.Loader.GetStartTransform().Origin.X, Frame.ToGodot(Player.transform.position).X, 1e-3, "player at the start pose");
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

            Assert.IsTrue(_s.CompleteObjectiveSequence(1));
            Assert.IsTrue(_s.CompleteObjectiveSequence(2), "restore_systems opens the powered gates");
            yield return null;
            Assert.IsFalse(gate.Zone.CollisionEnabled);
            Assert.IsFalse(gate.Blocker.enabled, "collider follows passability");
            Assert.IsFalse(gate.Visual.activeSelf);
            Physics.SyncTransforms();
            Assert.IsFalse(gate.Blocker.Raycast(down, out _, 10f), "the open gate no longer blocks");
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

        [UnityTest]
        public IEnumerator TravelToAGeneratedDerelictAndBack()
        {
            yield return BootPlayable();
            _s.ForceRepairAll();
            GdDict result = null;
            foreach (string id in _s.ScannableMarkerIds())
            {
                result = _s.TravelToMarkerId(id);
                if (result.GetBool("success")) break;
            }
            Assert.IsNotNull(result);
            Assert.IsTrue(result.GetBool("success"), "travel: " + GdJson.Stringify(result));
            yield return null;
            Assert.IsTrue(_s.AwayFromStart);
            var derelict = _s.CurrentShip.SceneRoot as ShipLoaderNode;
            Assert.IsNotNull(derelict, "the derelict is a built loader view");
            Assert.IsTrue(derelict.IsInsideTree && derelict.GameObject.activeInHierarchy, "attached and shown");
            Assert.AreEqual(Frame.ToUnity(RunSession.DERELICT_DOCK_OFFSET).x, derelict.GameObject.transform.position.x, 1e-3, "placed at DERELICT_DOCK_OFFSET");
            Assert.Greater(derelict.CountCollisionShapes(), 0);
            float playerToDerelict = Vector3.Distance(Player.transform.position, derelict.GameObject.transform.position);
            Assert.Less(playerToDerelict, 40f, "the player was re-pegged with the ride");
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

        // ================================================================== W2a in-play integration

        static void KeyboardToGameView() =>
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

        ThreatAIState InjectThreatBesidePlayer(string archetype = "stalker")
        {
            Vec3 at = Frame.ToGodot(Player.transform.position) + new Vec3(2.5f, 0f, 0f);
            _s.ThreatManager.InjectValidationEncounter(GdArray.Of(archetype), at);
            Assert.IsNotEmpty(_s.ThreatManager.Threats, "validation encounter spawned");
            return _s.ThreatManager.Threats[_s.ThreatManager.Threats.Count - 1];
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
            _s.InventoryState.AddItem("crowbar", 1);
            Assert.IsTrue(_s.EquipmentState.Equip("crowbar").GetBool("ok"));
            yield return TapKey(keyboard, Key.F);
            Assert.IsTrue(threat.Health < health || !_s.ThreatManager.Threats.Contains(threat), "attack_primary hit the threat through the session");
            StringAssert.Contains("Crowbar", _boot.Ui.Hud.Vitals.WeaponLine, "the HUD shows the session's hotbar text");

            _s.InventoryState.AddItem("flare_pistol", 1);
            _s.InventoryState.AddItem("flare_round", 4);
            Assert.IsTrue(_s.EquipmentState.Equip("flare_pistol").GetBool("ok"));
            yield return TapKey(keyboard, Key.R);
            Assert.IsTrue(_s.AmmoState.IsReloading(), "reload_weapon began a reload");

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
        public IEnumerator ThreatAttackFeedbackReachesTheHudAndDenialsToast()
        {
            yield return BootPlayable();
            _boot.Ui.OnPanelToggle("ui_open_map");
            StringAssert.Contains(SessionUiBridge.NoWebChartText, _boot.Ui.Hud.ToastText, "chart denial toast");

            InjectThreatBesidePlayer();
            var hits = new List<double>();
            _boot.Host.PlayerDamaged += (damage, archetype, at) => hits.Add(damage);
            float previousScale = Time.timeScale;
            Time.timeScale = 4f;
            try
            {
                float deadline = Time.realtimeSinceStartup + 20f;
                while (hits.Count == 0 && Time.realtimeSinceStartup < deadline && !_s.SliceComplete)
                    yield return null;
            }
            finally
            {
                Time.timeScale = previousScale;
            }
            Assert.IsNotEmpty(hits, "the threat attacked the player");
            StringAssert.Contains("Hit", _boot.Ui.Hud.Vitals.DamageIndicatorText, "HUD damage indicator");
        }
    }
}
