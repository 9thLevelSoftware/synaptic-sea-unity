// Ported from scripts/title_main.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace SynapticSea.App
{
    /// <summary>
    /// ADR-0043 title screen on the UI Toolkit presenters. Godot's title built its own MenuState + text MenuPanel and a
    /// title-local SettingsState mirror of the coordinator's settings handling; Unity runs the real
    /// <see cref="MenuCoordinator"/> in <see cref="MenuCoordinator.TitleMode"/> instead, so the title gets the same rows,
    /// settings selectors, focus restore and records screens as the pause stack.
    ///
    /// Items: New Run, Continue (enabled by <see cref="TitleSaveQuery"/> over the save index), Settings, Records
    /// (Unity addition: Save / Load slots, Achievements, Skill Tree, Hub Upgrades, Class Roster, audio, language, build
    /// info, credits; demo builds are gated by <see cref="DemoScopeGate"/>), Quit.
    ///
    /// Starting a run no longer instantiates main.tscn in-process: it fills <see cref="RunLaunchRequest.Pending"/> and
    /// loads the Playable scene. The playable reports failures/outcomes back through <see cref="RunReturnInfo"/>,
    /// shown under the menu as Godot did.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(UIDocument))]
    public sealed class TitleScreen : MonoBehaviour
    {
        public const string ScreenClass = "title-screen";

        /// <summary>Scene-load seam (tests capture the request instead of loading Playable). Returns false when the
        /// scene cannot be loaded.</summary>
        public static Func<string, bool> SceneLoader = DefaultSceneLoader;

        /// <summary>Quit seam (Godot <c>get_tree().quit()</c>).</summary>
        public static Action QuitHandler = DefaultQuit;

        /// <summary>Raised with the request just stored in <see cref="RunLaunchRequest.Pending"/>.</summary>
        public event Action<RunLaunchRequest> LaunchRequested;

        public MenuCoordinator Coordinator { get; private set; }
        public VisualElement ScreenRoot { get; private set; }
        public Label ReleaseBadge { get; private set; }
        public VisualElement StatusBox { get; private set; }
        public UiInputRouter Router { get; private set; }
        public SaveLoadService SaveService { get; private set; }
        public PermadeathResolver DeathRecords { get; private set; }
        public MetaProgressionState MetaProgression { get; private set; }
        public bool IsBuilt => Coordinator != null;

        /// <summary>Godot <c>_settings_dirty</c>: the player touched settings at the title.</summary>
        public bool SettingsDirty { get; private set; }

        public RunLaunchRequest LastRequest { get; private set; }

        /// <summary>The open New Run setup submenu (null when closed).</summary>
        public NewRunSetupPanel NewRunSetup { get; private set; }

        string _lastBootError = "";
        string _lastRunOutcome = "";
        string _lastRunProgress = "";
        string _lastRunContext = "";
        string _lastRunTime = "";
        bool _launching;
        int _lastSelectResync = -1000;

        void Start() => Build();

        void Update()
        {
            if (Router == null || _launching) return;
            Router.Tick();
            // The coordinator re-shows its menu panel on any refresh; the setup submenu replaces it while open.
            if (NewRunSetup != null && Coordinator.MenuPanel.IsViewVisible) Coordinator.MenuPanel.SetShown(false);
            EnsureMenuFocus();
        }

        /// <summary>
        /// Keeps UI Toolkit focus on the top surface (a gamepad has no pointer to recover focus after a background click,
        /// and the first rows are built before the panel's first layout). Restores the surface's last focus token.
        /// </summary>
        public void EnsureMenuFocus()
        {
            FocusController focus = ScreenRoot?.panel?.focusController;
            if (focus == null || !(Coordinator.Stack.Top is IInputConsumer top)) return;
            if (focus.focusedElement is VisualElement current && ScreenRoot.Contains(current))
            {
                // Focus taken before the EventSystem created this panel's selectable handler never selected it, so
                // gamepad/keyboard navigation would not reach UI Toolkit. Re-focus once so the handler selects the panel.
                EventSystem events = EventSystem.current;
                if (events == null || events.currentSelectedGameObject != null || Time.frameCount - _lastSelectResync < 10) return;
                _lastSelectResync = Time.frameCount;
                string token = top.CaptureFocusToken();
                current.Blur();
                top.RestoreFocus(token);
                return;
            }
            top.RestoreFocus(top.CaptureFocusToken());
        }

        void OnDisable() => Router?.Disable();

        void OnEnable()
        {
            if (Router != null && !_launching) Router.Enable();
        }

        /// <summary>Godot <c>_ready</c> + <c>_build_title_ui</c>. Idempotent.</summary>
        public void Build()
        {
            if (IsBuilt) return;
            AppServices services = AppServices.Ensure();
            IStorage storage = CoreServices.UserStorage;
            IClock clock = CoreServices.Clock;

            SaveService = new SaveLoadService(storage, clock);
            DeathRecords = new PermadeathResolver(storage, clock);

            var achievements = new AchievementState(storage, clock);
            achievements.Configure(CatalogRegistry.LoadDict(AchievementsPanel.CatalogPath));
            achievements.LoadFromDisk();
            var tree = new SkillTreeState();
            tree.Configure(SkillTreeState.LoadSkillsCatalog(), SkillTreeState.LoadBooksCatalog());
            tree.LoadPrerequisites();
            MetaProgression = new MetaProgressionState(storage, clock);
            MetaProgression.Configure();
            MetaProgression.LoadFromDisk();
            var progression = new PlayerProgressionState();
            Dictionary<string, ClassDefinition> classes = ClassDefinition.LoadAll();
            if (classes.TryGetValue(ResolveClassId(), out ClassDefinition classDef) || classes.TryGetValue(RunLaunchRequest.DefaultClassId, out classDef))
                progression.Configure(classDef, PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            var hub = new HubUpgradeState();
            hub.Configure();
            var localization = new LocalizationCatalog();
            localization.Configure(CatalogRegistry.LoadDict("res://data/release/localization_catalog.json"));
            var unlocks = new UnlockRegistry(storage, clock);
            unlocks.Configure();
            unlocks.LoadFromDisk();
            var saveMenu = new SaveLoadMenu();
            saveMenu.Bind(SaveService);

            Coordinator = new MenuCoordinator(achievements, new AudioManagerAdapter(services.Audio), tree, progression, hub,
                MetaProgression, localization, services.BuildMetadata, saveMenu, services.Accessibility, unlocks,
                snapshotBuilder: null, demoScopeGate: services.DemoScopeGate, demoSaveRefused: null,
                connectedJoypadCount: () => Gamepad.all.Count)
            {
                TitleMode = true,
            };

            // Mount before any menu opens so the first row can take UI Toolkit focus.
            ScreenRoot = BuildScreen();
            GetComponent<UIDocument>().rootVisualElement.Add(ScreenRoot);
            ScreenRoot.RegisterCallback<GeometryChangedEvent>(_ => EnsureMenuFocus());

            if (!Coordinator.Configure(TitleMenuCatalog(),
                    CatalogRegistry.LoadDict(MenuCoordinator.TutorialCatalogPath) ?? new GdDict(),
                    CatalogRegistry.LoadDict(MenuCoordinator.CodexCatalogPath) ?? new GdDict(),
                    CatalogRegistry.LoadDict(GlyphChips.GlyphTablePath) ?? new GdDict(),
                    CatalogRegistry.LoadDict(MenuCoordinator.TooltipCatalogPath) ?? new GdDict(),
                    CatalogRegistry.LoadDict(MenuCoordinator.PresetsCatalogPath) ?? new GdDict(),
                    null, services.Accessibility))
                CoreServices.Log.Error("TitleScreen: failed to configure the menu catalogs");
            Coordinator.RegisterScaleRoot(ScreenRoot);
            Coordinator.ApplySettingsSummary(services.Settings.GetSummary());

            Coordinator.StartRequested += OnTitleStart;
            Coordinator.LoadRequested += OnTitleContinue;
            Coordinator.WorldLoadRequested += OnTitleContinue;
            Coordinator.SlotSnapshotLoaded += OnSlotLoaded;
            Coordinator.QuitRequested += OnTitleQuit;
            Coordinator.SettingsChanged += OnSettingsChanged;
            Coordinator.MetaScreenConfirmed += OnMetaScreenConfirmed;

            _lastBootError = RunReturnInfo.LastFailureReason ?? "";
            _lastRunOutcome = RunReturnInfo.LastRunOutcome ?? "";
            _lastRunProgress = RunReturnInfo.LastRunProgress ?? "";
            _lastRunContext = RunReturnInfo.LastRunContext ?? "";
            _lastRunTime = RunReturnInfo.LastRunTime ?? "";
            RunReturnInfo.Clear();

            RefreshContinueEnabled();
            Coordinator.OpenMainMenu();
            RefreshStatus();

            Router = new UiInputRouter(services.Input, Coordinator.Stack, Coordinator.HandleUiInput);
            if (isActiveAndEnabled) Router.Enable();
            CoreServices.Log.Info($"[TitleScreen] ready menu={Coordinator.GetCurrentMenu()} continue={Coordinator.MenuState.IsItemEnabled("main_menu", "continue")}");
        }

        VisualElement BuildScreen()
        {
            var root = new VisualElement { name = "title-screen" };
            root.AddToClassList(UiClasses.Root);
            root.AddToClassList(ScreenClass);
            root.Add(Coordinator.Root);

            var footer = UiFactory.Box("title-footer");
            footer.pickingMode = PickingMode.Ignore;
            StatusBox = UiFactory.Box("title-status");
            ReleaseBadge = UiFactory.Text("", "title-release-badge", UiClasses.LabelMono, UiClasses.LabelSecondary);
            footer.Add(StatusBox);
            footer.Add(ReleaseBadge);
            root.Add(footer);
            return root;
        }

        /// <summary>The synced menu catalog with a Records row added to the title's main menu (before Quit).</summary>
        public static GdDict TitleMenuCatalog()
        {
            GdDict catalog = CatalogRegistry.LoadDict(MenuCoordinator.MenuCatalogPath) ?? new GdDict();
            foreach (object menuV in catalog.GetArrayOrEmpty("menus"))
            {
                if (!(menuV is GdDict menu) || menu.GetString("id", "") != "main_menu") continue;
                GdArray items = menu.GetArrayOrEmpty("items");
                int quit = -1;
                bool hasRecords = false;
                for (int i = 0; i < items.Count; i++)
                {
                    string id = (items[i] as GdDict)?.GetString("id", "") ?? "";
                    if (id == "records") hasRecords = true;
                    if (id == "quit") quit = i;
                }
                if (hasRecords) break;
                var records = new GdDict { { "id", "records" }, { "label", "Records" }, { "enabled", true }, { "kind", "command" } };
                if (quit < 0) items.Add(records);
                else items.Insert(quit, records);
                break;
            }
            return catalog;
        }

        /// <summary>Godot <c>_refresh_continue_enabled</c>.</summary>
        public void RefreshContinueEnabled()
        {
            Coordinator.SetLoadAvailable(TitleSaveQuery.IsContinueAvailable(SaveService, DeathRecords));
        }

        string ResolveClassId()
        {
            string selected = MetaProgression?.GetSelectedClass() ?? "";
            return selected.Length != 0 ? selected : RunLaunchRequest.DefaultClassId;
        }

        void OnTitleStart() => OpenNewRunSetup();

        /// <summary>C1: New Run opens the setup submenu (biome, difficulty, seed) above the title menu.</summary>
        public NewRunSetupPanel OpenNewRunSetup()
        {
            if (NewRunSetup != null || _launching) return NewRunSetup;
            List<string> biomes = NewRunSetupPanel.LoadBiomeIds();
            string biome = biomes.Contains(RunLaunchRequest.DefaultBiomeId) ? RunLaunchRequest.DefaultBiomeId : (biomes.Count > 0 ? biomes[0] : "");
            var panel = new NewRunSetupPanel(biomes, NewRunSetupPanel.LoadDifficultyIds(), biome, Coordinator.SettingsState.GetDifficulty(),
                NewRunSetupPanel.RandomSeed());
            panel.SetGlyphResolver(Coordinator.GlyphFor);
            panel.StartRequested += request => Launch(request);
            panel.BackRequested += CloseNewRunSetup;
            NewRunSetup = panel;
            Coordinator.MenuPanel.SetShown(false);
            Coordinator.MenuLayer.Add(panel);
            Coordinator.Stack.Push(panel);
            return panel;
        }

        /// <summary>Back from the setup: the title menu returns with focus on New Run.</summary>
        public void CloseNewRunSetup()
        {
            NewRunSetupPanel panel = NewRunSetup;
            if (panel == null) return;
            NewRunSetup = null;
            Coordinator.MenuPanel.SetShown(true);
            Coordinator.Stack.Pop(panel);
            panel.RemoveFromHierarchy();
        }

        void OnTitleContinue() => Launch(RunLaunchRequest.ContinueWorld());

        void OnSlotLoaded(string slotId, RunSnapshot snapshot) => Launch(RunLaunchRequest.LoadSlot(slotId));

        void OnTitleQuit() => QuitHandler?.Invoke();

        void OnSettingsChanged(GdDict summary)
        {
            SettingsDirty = true;
            AppServices.Instance?.ApplySettings(summary);
        }

        void OnMetaScreenConfirmed(GdDict result)
        {
            // A slot delete can remove the world save; Continue must never be offered falsely.
            if (result.GetString("screen", "") == "save_load") RefreshContinueEnabled();
        }

        /// <summary>Godot <c>_instantiate_gameplay</c>: hand the run to the Playable scene.</summary>
        public bool Launch(RunLaunchRequest request)
        {
            if (_launching || request == null) return false;
            // A New Run carries the setup's difficulty; loads restore the saved run's own context.
            if (request.Mode != RunLaunchMode.NewRun || string.IsNullOrEmpty(request.DifficultyId))
                request.DifficultyId = Coordinator.SettingsState.GetDifficulty();
            request.ClassId = ResolveClassId();
            request.SettingsSummary = SettingsDirty ? Coordinator.GetSettingsSummary() : null;
            _lastBootError = "";
            _lastRunOutcome = "";
            _lastRunProgress = "";
            _lastRunContext = "";
            _lastRunTime = "";
            RunLaunchRequest.Pending = request;
            LastRequest = request;
            LaunchRequested?.Invoke(request);
            if (SceneLoader == null || !SceneLoader(RunLaunchRequest.PlayableSceneName))
            {
                RunLaunchRequest.Pending = null;
                _lastBootError = "the Playable scene is not in this build";
                CoreServices.Log.Warning("TitleScreen: gameplay boot failed (" + _lastBootError + ") — staying on title");
                RefreshStatus();
                return false;
            }
            _launching = true;
            SettingsDirty = false;
            Router?.Disable();
            return true;
        }

        /// <summary>Godot <c>_refresh_panel</c>'s trailing lines plus the release badge.</summary>
        void RefreshStatus()
        {
            StatusBox.Clear();
            if (_lastBootError.Length != 0) AddStatus("Load failed: " + _lastBootError, Severity.Danger);
            if (_lastRunOutcome.Length != 0) AddStatus(LastRunLine(_lastRunOutcome, _lastRunContext, _lastRunTime), _lastRunOutcome == "death" ? Severity.Caution : Severity.Info);
            if (_lastRunProgress.Length != 0) AddStatus("Progress: " + _lastRunProgress, Severity.Info);

            ReleaseBadgeOverlay badge = Coordinator.ReleaseBadgeOverlay;
            BuildMetadataState meta = AppServices.Instance != null ? AppServices.Instance.BuildMetadata : null;
            ReleaseBadge.text = badge.GetBadgeText() + (meta != null ? "  " + meta.Version : "");
            Color color = badge.GetBadgeColor();
            ReleaseBadge.style.borderLeftColor = color;
        }

        /// <summary>"Last run: death — seed 17 · breach_field · standard — 03:12".</summary>
        public static string LastRunLine(string outcome, string context, string time)
        {
            string line = "Last run: " + outcome;
            if (!string.IsNullOrEmpty(context)) line += " — " + context;
            if (!string.IsNullOrEmpty(time)) line += " — " + time;
            return line;
        }

        void AddStatus(string text, Severity severity)
        {
            var line = new StatusLine();
            line.Set(text, severity);
            StatusBox.Add(line);
        }

        public string StatusText
        {
            get
            {
                var lines = new List<string>();
                foreach (VisualElement child in StatusBox.Children())
                {
                    if (child is StatusLine s) lines.Add(s.Raw);
                }
                return string.Join("\n", lines);
            }
        }

        static bool DefaultSceneLoader(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName)) return false;
            SceneManager.LoadScene(sceneName);
            return true;
        }

        static void DefaultQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
