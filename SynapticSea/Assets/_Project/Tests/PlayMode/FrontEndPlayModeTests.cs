using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.App;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
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
    /// Front-end flow: Boot composes the services and loads Title; the title menu is driven by a virtual gamepad through
    /// the real input path (Input System → EventSystem/InputSystemUIInputModule → UI Toolkit navigation → MenuPanel);
    /// New Run / Continue hand off through <see cref="RunLaunchRequest"/> (scene load captured, Playable not required).
    /// </summary>
    public class FrontEndPlayModeTests : InputTestFixture
    {
        MemoryStorage _storage;
        Func<string, bool> _previousLoader;
        Func<long> _previousRandomSeed;
        IStorage _previousStorage;
        IResourceReader _previousResources;
        ILog _previousLog;
        readonly List<string> _loadedScenes = new List<string>();

        public override void Setup()
        {
            base.Setup();
            _previousStorage = CoreServices.UserStorage;
            _previousResources = CoreServices.Resources;
            _previousLog = CoreServices.Log;
            _storage = new MemoryStorage();
            AppServices.StorageOverride = _storage;
            _previousLoader = TitleScreen.SceneLoader;
            _loadedScenes.Clear();
            TitleScreen.SceneLoader = name =>
            {
                _loadedScenes.Add(name);
                return true;
            };
            RunLaunchRequest.Pending = null;
            RunReturnInfo.Clear();
            _previousRandomSeed = NewRunSetupPanel.RandomSeed;
            NewRunSetupPanel.RandomSeed = () => 4242;
        }

        public override void TearDown()
        {
            foreach (TitleScreen title in Object.FindObjectsByType<TitleScreen>()) Object.DestroyImmediate(title.gameObject);
            AppServices.Shutdown();
            AppServices.StorageOverride = null;
            TitleScreen.SceneLoader = _previousLoader;
            NewRunSetupPanel.RandomSeed = _previousRandomSeed;
            RunReturnInfo.Clear();
            RunLaunchRequest.Pending = null;
            CatalogRegistry.Clear();
            CoreServices.UserStorage = _previousStorage;
            CoreServices.Resources = _previousResources;
            CoreServices.Log = _previousLog;
            base.TearDown();
        }

        TitleScreen _title;

        IEnumerator BootToTitle()
        {
            SceneManager.LoadScene("Boot");
            float deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline)
            {
                _title = SceneManager.GetActiveScene().name == RunLaunchRequest.TitleSceneName ? Object.FindAnyObjectByType<TitleScreen>() : null;
                if (_title != null && _title.IsBuilt) break;
                yield return null;
            }
            Assert.IsNotNull(_title, "Boot loaded the Title scene and built the title screen");
            Assert.IsTrue(_title.IsBuilt);
            // Styles/layout resolve, the first row takes focus and the EventSystem selects the title panel.
            for (int i = 0; i < 120 && UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject == null; i++) yield return null;
            yield return null;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        static int RowIndex(MenuCoordinator c, string itemId) => c.MenuPanel.Rows.ToList().FindIndex(r => r.Id == itemId);

        static string FocusedToken(TitleScreen title) =>
            UiFocus.TokenOf(title.ScreenRoot.panel?.focusController?.focusedElement as VisualElement);

        static string FocusDiagnostics(TitleScreen title)
        {
            VisualElement row = title.Coordinator.MenuPanel.RowAt((int)title.Coordinator.GetFocusIndex());
            var doc = title.GetComponent<UIDocument>();
            return $"docRootPanel={(doc.rootVisualElement?.panel != null)} parentIsDocRoot={(title.ScreenRoot.parent == doc.rootVisualElement)} docEnabled={doc.isActiveAndEnabled} ps={doc.panelSettings} runtimePanel={doc.runtimePanel} " +
                   $"panel={(title.ScreenRoot.panel != null)} focused={title.ScreenRoot.panel?.focusController?.focusedElement} " +
                   $"row={(row != null)} canGrabFocus={row?.canGrabFocus} display={row?.resolvedStyle.display} visible={row?.visible} " +
                   $"enabled={row?.enabledInHierarchy} layout={row?.layout}";
        }

        [UnityTest]
        public IEnumerator BootLoadsTitleWithoutErrors()
        {
            yield return BootToTitle();
            Assert.IsNotNull(AppServices.Instance, "services survive the scene load");
            Assert.AreEqual(AppServices.Instance.gameObject.scene.name, "DontDestroyOnLoad");
            Assert.IsNotNull(AppServices.Instance.Input);
            Assert.IsNotNull(AppServices.Instance.EventSystem);
            Assert.AreEqual("main_menu", _title.Coordinator.GetCurrentMenu());
            Assert.AreSame(_title.Coordinator.MenuPanel, _title.Coordinator.Stack.Top);
            CollectionAssert.AreEqual(new[] { "start", "continue", "settings", "records", "quit" }, _title.Coordinator.MenuPanel.Rows.Select(r => r.Id).ToArray());
            Assert.AreEqual("The Synaptic Sea", _title.Coordinator.MenuPanel.Title);
            StringAssert.StartsWith(_title.Coordinator.ReleaseBadgeOverlay.GetBadgeText(), _title.ReleaseBadge.text);
            Assert.AreEqual(MenuPanel.TokenFor("main_menu", "start"), FocusedToken(_title), "New Run takes initial focus: " + FocusDiagnostics(_title));
        }

        [UnityTest]
        public IEnumerator GamepadTitleSettingsTextScaleBackRestoresFocus()
        {
            var gamepad = InputSystem.AddDevice<Gamepad>();
            yield return BootToTitle();
            MenuCoordinator c = _title.Coordinator;
            int settings = RowIndex(c, "settings");
            Assert.IsNotNull(UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject, "the EventSystem routes navigation to the title panel");

            for (int i = 0; i < settings; i++) yield return Tap(gamepad.dpad.down);
            Assert.AreEqual(settings, c.GetFocusIndex(), "one d-pad press moves one row");
            Assert.AreEqual(MenuPanel.TokenFor("main_menu", "settings"), FocusedToken(_title));

            yield return Tap(gamepad.buttonSouth);
            Assert.AreEqual("settings_menu", c.GetCurrentMenu());
            int textScale = RowIndex(c, "text_scale");
            for (int i = 0; i < textScale; i++) yield return Tap(gamepad.dpad.down);
            Assert.AreEqual(MenuPanel.TokenFor("settings_menu", "text_scale"), FocusedToken(_title));

            yield return Tap(gamepad.dpad.right);
            Assert.AreEqual(1.5, AppServices.Instance.Settings.GetTextScale(), 1e-9, "the shared preferences took the new scale");
            Assert.AreEqual(1.5, AppServices.Instance.Accessibility.GetTextScale(), 1e-9);
            Assert.IsTrue(_title.ScreenRoot.ClassListContains("scale-150"), "the title reflows at 1.5x");
            Assert.IsTrue(_title.SettingsDirty);
            GdDict stored = UserSettingsStore.Load(_storage);
            Assert.IsNotNull(stored, "preferences persisted");
            Assert.AreEqual(1.5, stored.GetFloat("text_scale", 0.0), 1e-9);

            yield return Tap(gamepad.buttonEast);
            Assert.AreEqual("main_menu", c.GetCurrentMenu(), "Back returns to the title menu");
            Assert.AreEqual(settings, c.GetFocusIndex(), "Back restores the Settings item");
            yield return null;
            Assert.AreEqual(MenuPanel.TokenFor("main_menu", "settings"), FocusedToken(_title), "UI Toolkit focus is back on Settings");

            yield return Tap(gamepad.buttonEast);
            Assert.AreEqual("main_menu", c.GetCurrentMenu(), "Back on the root title menu does nothing");
        }

        [UnityTest]
        public IEnumerator NewRunOpensSetupAndStartStoresTheLaunchRequest()
        {
            yield return BootToTitle();
            MenuCoordinator c = _title.Coordinator;
            c.MenuState.SetFocusIndex(RowIndex(c, "start"));
            c.HandleUiInput(UiCommand.Accept);
            CollectionAssert.IsEmpty(_loadedScenes, "New Run opens the setup instead of launching");
            NewRunSetupPanel setup = _title.NewRunSetup;
            Assert.IsNotNull(setup);
            Assert.AreSame(setup, c.Stack.Top);
            CollectionAssert.AreEqual(new[] { "abyssal_synaptic_sea", "breach_field", "dead_fleet" }, setup.BiomeIds.ToArray(), "biomes from data/procgen/biomes");
            CollectionAssert.AreEqual(new[] { "standard", "hardened", "deep_dive" }, setup.DifficultyIds.ToArray(), "difficulties from data/procgen/difficulty");
            Assert.AreEqual("breach_field", setup.BiomeId);
            Assert.AreEqual("standard", setup.DifficultyId, "the title difficulty setting");
            Assert.AreEqual(4242, setup.Seed, "a random seed by default");

            setup.FocusRow(NewRunSetupPanel.RowStart);
            setup.Consume(UiCommand.Accept);

            CollectionAssert.AreEqual(new[] { RunLaunchRequest.PlayableSceneName }, _loadedScenes);
            RunLaunchRequest request = RunLaunchRequest.Pending;
            Assert.IsNotNull(request);
            Assert.AreEqual(RunLaunchMode.NewRun, request.Mode);
            Assert.AreEqual("", request.SlotId);
            Assert.AreEqual(4242, request.Seed);
            Assert.AreEqual("breach_field", request.BiomeId);
            Assert.AreEqual("standard", request.DifficultyId);
            Assert.AreEqual("engineer", request.ClassId);
            Assert.AreEqual("", request.LayoutOverridePath, "a title run is generated");
            Assert.IsNull(request.SettingsSummary, "untouched title settings are not handed off");
            Assert.AreSame(request, RunLaunchRequest.Consume());
            Assert.IsNull(RunLaunchRequest.Pending);
        }

        [UnityTest]
        public IEnumerator GamepadNewRunSetupFillsTheRequestAndBackRestoresFocus()
        {
            var gamepad = InputSystem.AddDevice<Gamepad>();
            yield return BootToTitle();
            MenuCoordinator c = _title.Coordinator;
            Assert.AreEqual(MenuPanel.TokenFor("main_menu", "start"), FocusedToken(_title));

            yield return Tap(gamepad.buttonSouth);
            NewRunSetupPanel setup = _title.NewRunSetup;
            Assert.IsNotNull(setup, "South on New Run opens the setup");
            yield return null;
            Assert.AreEqual(NewRunSetupPanel.TokenFor(NewRunSetupPanel.RowBiome), FocusedToken(_title), "the biome row takes focus");
            Assert.IsFalse(c.MenuPanel.IsViewVisible, "the title menu is hidden under the setup");

            yield return Tap(gamepad.dpad.right);
            Assert.AreEqual("dead_fleet", setup.BiomeId, "Right cycles the biome");
            yield return Tap(gamepad.dpad.down);
            Assert.AreEqual(NewRunSetupPanel.TokenFor(NewRunSetupPanel.RowDifficulty), FocusedToken(_title));
            yield return Tap(gamepad.dpad.right);
            Assert.AreEqual("hardened", setup.DifficultyId);
            yield return Tap(gamepad.dpad.down);
            yield return Tap(gamepad.dpad.right);
            Assert.AreEqual(4243, setup.Seed, "Right steps the seed");
            Assert.AreEqual("4243", setup.RowValue(NewRunSetupPanel.RowSeed));
            yield return Tap(gamepad.dpad.down);
            NewRunSetupPanel.RandomSeed = () => 777;
            yield return Tap(gamepad.buttonSouth);
            Assert.AreEqual(777, setup.Seed, "Randomize seed rolls a new seed");

            yield return Tap(gamepad.buttonEast);
            Assert.IsNull(_title.NewRunSetup, "Back closes the setup");
            Assert.AreSame(c.MenuPanel, c.Stack.Top);
            Assert.IsTrue(c.MenuPanel.IsViewVisible);
            yield return null;
            Assert.AreEqual(MenuPanel.TokenFor("main_menu", "start"), FocusedToken(_title), "Back restores focus to New Run");
            CollectionAssert.IsEmpty(_loadedScenes);

            NewRunSetupPanel.RandomSeed = () => 99;
            yield return Tap(gamepad.buttonSouth);
            setup = _title.NewRunSetup;
            Assert.IsNotNull(setup);
            yield return null;
            yield return Tap(gamepad.dpad.right);
            yield return Tap(gamepad.dpad.down);
            yield return Tap(gamepad.dpad.right);
            yield return Tap(gamepad.dpad.right);
            Assert.AreEqual("deep_dive", setup.DifficultyId);
            yield return Tap(gamepad.dpad.up);
            yield return Tap(gamepad.dpad.up);
            Assert.AreEqual(NewRunSetupPanel.TokenFor(NewRunSetupPanel.RowStart), FocusedToken(_title), "Up wraps to Start");
            yield return Tap(gamepad.buttonSouth);

            CollectionAssert.AreEqual(new[] { RunLaunchRequest.PlayableSceneName }, _loadedScenes);
            RunLaunchRequest request = RunLaunchRequest.Pending;
            Assert.IsNotNull(request);
            Assert.AreEqual(RunLaunchMode.NewRun, request.Mode);
            Assert.AreEqual(99, request.Seed);
            Assert.AreEqual("dead_fleet", request.BiomeId);
            Assert.AreEqual("deep_dive", request.DifficultyId);
            Assert.AreEqual("engineer", request.ClassId);
        }

        [UnityTest]
        public IEnumerator SeedFieldAcceptsTypedDigits()
        {
            yield return BootToTitle();
            NewRunSetupPanel setup = _title.OpenNewRunSetup();
            setup.FocusRow(NewRunSetupPanel.RowSeed);
            setup.Consume(UiCommand.Accept);
            Assert.IsTrue(setup.IsEditingSeed, "Accept on the seed row opens the numeric field");
            setup.SeedField.value = "90x21";
            setup.Consume(UiCommand.Accept);
            Assert.IsFalse(setup.IsEditingSeed);
            Assert.AreEqual(9021, setup.Seed, "non-digits are dropped");
            setup.Consume(UiCommand.Accept);
            setup.SeedField.value = "5";
            setup.Consume(UiCommand.Cancel);
            Assert.AreEqual(9021, setup.Seed, "Back cancels the edit");
            Assert.IsNotNull(_title.NewRunSetup, "and keeps the setup open");
            setup.Consume(UiCommand.Cancel);
            Assert.IsNull(_title.NewRunSetup);
        }

        [UnityTest]
        public IEnumerator TitleShowsTheLastRunAndFailureLines()
        {
            RunReturnInfo.LastRunOutcome = "death";
            RunReturnInfo.LastRunContext = "seed 17 · breach_field · hardened";
            RunReturnInfo.LastRunTime = "03:12";
            RunReturnInfo.LastRunProgress = "objectives 1/5";
            RunReturnInfo.LastFailureReason = "boom";
            yield return BootToTitle();
            StringAssert.Contains("Load failed: boom", _title.StatusText);
            StringAssert.Contains("Last run: death — seed 17 · breach_field · hardened — 03:12", _title.StatusText);
            StringAssert.Contains("Progress: objectives 1/5", _title.StatusText);
            Assert.AreEqual("", RunReturnInfo.LastRunOutcome, "the title consumed the return info");
        }

        [Test]
        public void ProjectVersionComesFromTheStampElseTheApplicationVersion()
        {
            Assert.AreEqual("1.2.3", AppServices.ProjectVersionFor(new GdDict { { "version", "1.2.3" } }, "0.9"));
            Assert.AreEqual("0.9", AppServices.ProjectVersionFor(new GdDict(), "0.9"));
        }

        [UnityTest]
        public IEnumerator BootSetsTheCloudManifestBuildId()
        {
            yield return BootToTitle();
            Assert.AreEqual(Application.version, CoreServices.ProjectVersion, "no build stamp in the editor: Application.version");
            Assert.IsNotEmpty(CoreServices.ProjectVersion);
        }

        [UnityTest]
        public IEnumerator ContinueIsDisabledWithoutASave()
        {
            yield return BootToTitle();
            MenuCoordinator c = _title.Coordinator;
            Assert.IsFalse(c.MenuPanel.Rows.First(r => r.Id == "continue").Enabled);
            c.MenuState.SetFocusIndex(RowIndex(c, "continue"));
            c.HandleUiInput(UiCommand.Accept);
            Assert.IsNull(RunLaunchRequest.Pending, "a disabled Continue launches nothing");
            CollectionAssert.IsEmpty(_loadedScenes);
        }

        [UnityTest]
        public IEnumerator ContinueIsEnabledWithAWorldSaveAndLaunchesContinue()
        {
            WriteWorldSave(_storage);
            yield return BootToTitle();
            MenuCoordinator c = _title.Coordinator;
            Assert.IsTrue(c.MenuPanel.Rows.First(r => r.Id == "continue").Enabled);
            c.MenuState.SetFocusIndex(RowIndex(c, "continue"));
            c.HandleUiInput(UiCommand.Accept);
            CollectionAssert.AreEqual(new[] { RunLaunchRequest.PlayableSceneName }, _loadedScenes);
            Assert.IsNotNull(RunLaunchRequest.Pending);
            Assert.AreEqual(RunLaunchMode.Continue, RunLaunchRequest.Pending.Mode);
            Assert.AreEqual("world", RunLaunchRequest.Pending.SlotId);
        }

        static void WriteWorldSave(IStorage storage)
        {
            var service = new SaveLoadService(storage, new ManualClock());
            service.SetActiveRunId("run-title");
            var home = new RunSnapshot
            {
                LayoutPath = "res://data/procgen/smoke/seed_000017/layout.json",
                PlayerPosition = GdArray.Of(1.0, 0.0, 1.0),
                CurrentLocation = "home",
                WorldSeed = 17,
            }.ToDict();
            home["slice_version"] = SaveLoadService.CURRENT_SLICE_VERSION;
            home["godot_version"] = CoreServices.Engine.VersionString;
            var world = new WorldSnapshot
            {
                WorldSummary = new SynapticSeaWorld(17, new Vec3(0f, 0f, 0f)).GetSummary(),
                HomeShip = home,
                CurrentLocation = "home",
                SliceVersion = WorldSnapshot.WorldSliceVersion,
                GodotVersion = CoreServices.Engine.VersionString,
            };
            Assert.IsTrue(service.SaveWorld(world), "test world save written");
        }
    }
}
