// Ported from scripts/ui/menu_coordinator.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.UI.Presenters;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Presenter-side menu state machine (Godot MenuCoordinator) plus the <see cref="ModalStack"/> every surface lives
    /// on. It owns MenuState / SettingsState / TutorialState / TooltipPresenter / ControllerGlyphState, the menu and
    /// codex surfaces, the ten records (meta) screens, and the coordinator-owned HUD pieces (hotbar, tooltip, tutorial).
    ///
    /// Same events as Godot: <see cref="ModalOpened"/>, <see cref="ModalClosed"/>, <see cref="SaveRequested"/>,
    /// <see cref="LoadRequested"/>, <see cref="QuitRequested"/>, <see cref="SaveAndExitRequested"/>,
    /// <see cref="SettingsChanged"/>. Godot's bind_meta_screens(14 dependencies) becomes the constructor.
    ///
    /// Stack contract: whenever MenuState leaves play, the menu surface is pushed (PAUSED); the codex, and an open
    /// records screen, push above it; inspection panels opened through <see cref="OpenInspection"/> sit below. Nested
    /// Back returns one level and restores focus from the stable item token (the Godot MenuState resets focus to the
    /// first item; the coordinator remembers which item opened a submenu and restores it).
    /// </summary>
    public sealed class MenuCoordinator
    {
        public static readonly IReadOnlyList<string> MetaScreenIds = new[]
        {
            "achievements", "skill_tree", "hub_upgrades", "class", "audio_log",
            "audio_settings", "language", "save_load", "release_badge", "credits",
        };

        public const string MenuCatalogPath = "res://data/ui/menu_definitions.json";
        public const string TutorialCatalogPath = "res://data/ui/tutorial_triggers.json";
        public const string CodexCatalogPath = "res://data/ui/codex_entries.json";
        public const string TooltipCatalogPath = "res://data/ui/tooltip_catalog.json";
        public const string PresetsCatalogPath = "res://data/ui/accessibility_presets.json";

        public event Action<string> ModalOpened;
        public event Action<string> ModalClosed;
        public event Action SaveRequested;
        public event Action LoadRequested;
        public event Action QuitRequested;
        public event Action SaveAndExitRequested;
        public event Action<GdDict> SettingsChanged;
        /// <summary>Every records-screen confirm result (replaces polling get_last_meta_screen_confirm_result).</summary>
        public event Action<GdDict> MetaScreenConfirmed;
        /// <summary>A slot-screen Load succeeded: the session applies the snapshot (Godot: apply_manual_slot).</summary>
        public event Action<string, RunSnapshot> SlotSnapshotLoaded;
        /// <summary>A slot-screen Load on the world row: the session runs its world load (Godot: request_load()).</summary>
        public event Action WorldLoadRequested;
        public event Action<string> LanguageChanged;
        /// <summary>Title mode only: the main menu's New Run (Godot title_main.gd <c>_on_title_start</c>).</summary>
        public event Action StartRequested;

        /// <summary>
        /// Title screen host (Godot title_main.gd): New Run raises <see cref="StartRequested"/> instead of closing the
        /// menu into play, the root main menu cannot be cancelled away, and Pause / Codex are ignored.
        /// </summary>
        public bool TitleMode { get; set; }

        public readonly MenuState MenuState = new MenuState();

        /// <summary>
        /// The settings the menus edit. The title owns its own; in play it is the session's <c>RunSession.SettingsState</c>
        /// (constructor injection), so hold-to-work, captions and saves read the same instance the pause menu edits.
        /// </summary>
        public SettingsState SettingsState { get; }

        /// <summary>
        /// The tutorial state the banner and Codex show. The title builds its own; in play it is the session's single
        /// <c>RunSession.TutorialState</c> (injected, re-read on <see cref="BindTutorialState"/>). An injected state is
        /// never reconfigured here, and its cue SFX stay with the session that owns it.
        /// </summary>
        public TutorialState TutorialState { get; private set; }

        /// <summary>True when <see cref="TutorialState"/> was injected (the session owns its catalog and cue SFX).</summary>
        public bool TutorialStateInjected { get; private set; }
        public readonly TooltipPresenter TooltipPresenter = new TooltipPresenter();
        public readonly ControllerGlyphState ControllerGlyphState;
        public readonly ModalStack Stack = new ModalStack();

        public AccessibilitySettings AccessibilitySettings { get; private set; }

        // views
        public VisualElement Root { get; }
        public VisualElement InspectionLayer { get; }
        public VisualElement MenuLayer { get; }
        public MenuPanel MenuPanel { get; }
        public CodexPanel CodexPanel { get; }
        public HotbarStrip HotbarStrip { get; }
        public TooltipCard TooltipCard { get; }
        public TutorialBanner TutorialBanner { get; }
        public AchievementsPanel AchievementsPanel { get; }
        public SkillTreePanel SkillTreePanel { get; }
        public HubUpgradePanel HubUpgradePanel { get; }
        public ClassPanel ClassPanel { get; }
        public AudioLogPanel AudioLogPanel { get; }
        public AudioSettingsPanel AudioSettingsPanel { get; }
        public LanguageSelector LanguageSelector { get; }
        public ReleaseBadgeOverlay ReleaseBadgeOverlay { get; }
        public CreditsScreen CreditsScreen { get; }
        public SaveLoadScreen SaveLoadScreen { get; }
        public SaveSlotScreenModel SaveSlots { get; }

        readonly Dictionary<string, SurfacePanel> _metaPanels = new Dictionary<string, SurfacePanel>();
        readonly List<VisualElement> _scaleRoots = new List<VisualElement>();
        readonly Dictionary<string, string> _focusMemory = new Dictionary<string, string>();

        // the 14 injected dependencies (Godot bind_meta_screens)
        readonly AchievementState _achievementState;
        readonly IUiAudio _audio;
        readonly SkillTreeState _skillTreeState;
        readonly PlayerProgressionState _playerProgression;
        readonly HubUpgradeState _hubUpgradeState;
        readonly MetaProgressionState _metaProgressionState;
        readonly LocalizationCatalog _localizationCatalog;
        readonly BuildMetadataState _buildMetadataState;
        readonly SaveLoadMenu _saveLoadMenu;
        readonly UnlockRegistry _unlockRegistry;
        readonly Func<RunSnapshot> _snapshotBuilder;
        readonly DemoScopeGate _demoScopeGate;
        readonly Func<bool> _demoSaveRefused;

        GdDict _menuCatalog = new GdDict();
        readonly GdDict _codexEntries = new GdDict();
        GdArray _presets = new GdArray();
        bool _loadAvailable;
        List<string> _inventoryItemIds = new List<string>();
        List<string> _hotbarSlotLabels = new List<string>();
        int _selectedHotbarIndex;
        string _lastClosedMenu = "";
        string _activeMetaScreen = "";
        string _activeLanguage = "en";
        GdDict _lastMetaScreenConfirmResult = new GdDict();

        /// <param name="achievementState">Required.</param>
        /// <param name="audioManager">Required (UI open/close SFX, audio screens).</param>
        /// <param name="skillTreeState">Required.</param>
        /// <param name="playerProgression">Required.</param>
        /// <param name="hubUpgradeState">Required.</param>
        /// <param name="metaProgressionState">Required.</param>
        /// <param name="localizationCatalog">Required.</param>
        /// <param name="buildMetadataState">Required.</param>
        /// <param name="saveLoadMenu">Required.</param>
        /// <param name="a11y">Optional accessibility sink (null-tolerant, as in Godot).</param>
        /// <param name="unlockRegistry">Optional cross-run unlock registry (codex lines).</param>
        /// <param name="snapshotBuilder">Builds the RunSnapshot for a slot-screen Save.</param>
        /// <param name="demoScopeGate">Blocks hub/meta progression persistence in demo builds.</param>
        /// <param name="demoSaveRefused">The playable's demo play-time save refusal predicate.</param>
        /// <param name="connectedJoypadCount">Resolves the "auto" glyph scheme (Godot read Input.get_connected_joypads).</param>
        public MenuCoordinator(
            AchievementState achievementState,
            IUiAudio audioManager,
            SkillTreeState skillTreeState,
            PlayerProgressionState playerProgression,
            HubUpgradeState hubUpgradeState,
            MetaProgressionState metaProgressionState,
            LocalizationCatalog localizationCatalog,
            BuildMetadataState buildMetadataState,
            SaveLoadMenu saveLoadMenu,
            AccessibilitySettings a11y = null,
            UnlockRegistry unlockRegistry = null,
            Func<RunSnapshot> snapshotBuilder = null,
            DemoScopeGate demoScopeGate = null,
            Func<bool> demoSaveRefused = null,
            Func<int> connectedJoypadCount = null,
            TutorialState tutorialState = null,
            SettingsState settingsState = null)
        {
            SettingsState = settingsState ?? new SettingsState();
            TutorialStateInjected = tutorialState != null;
            TutorialState = tutorialState ?? new TutorialState();
            _achievementState = achievementState ?? throw new ArgumentNullException(nameof(achievementState), "p_achievement_state dependency is missing");
            _audio = audioManager ?? throw new ArgumentNullException(nameof(audioManager), "p_audio_manager dependency is missing");
            _skillTreeState = skillTreeState ?? throw new ArgumentNullException(nameof(skillTreeState), "p_skill_tree_state dependency is missing");
            _playerProgression = playerProgression ?? throw new ArgumentNullException(nameof(playerProgression), "p_player_progression dependency is missing");
            _hubUpgradeState = hubUpgradeState ?? throw new ArgumentNullException(nameof(hubUpgradeState), "p_hub_upgrade_state dependency is missing");
            _metaProgressionState = metaProgressionState ?? throw new ArgumentNullException(nameof(metaProgressionState), "p_meta_progression_state dependency is missing");
            _localizationCatalog = localizationCatalog ?? throw new ArgumentNullException(nameof(localizationCatalog), "p_localization_catalog dependency is missing");
            _buildMetadataState = buildMetadataState ?? throw new ArgumentNullException(nameof(buildMetadataState), "p_build_metadata_state dependency is missing");
            _saveLoadMenu = saveLoadMenu ?? throw new ArgumentNullException(nameof(saveLoadMenu), "p_save_load_menu dependency is missing");
            AccessibilitySettings = a11y;
            _unlockRegistry = unlockRegistry;
            _snapshotBuilder = snapshotBuilder;
            _demoScopeGate = demoScopeGate;
            _demoSaveRefused = demoSaveRefused;
            ControllerGlyphState = new ControllerGlyphState(connectedJoypadCount);

            // --- views (Godot _ready + _build_meta_screens) ---
            Root = new VisualElement { name = "menu-root" };
            Root.AddToClassList(UiClasses.Root);
            Root.AddToClassList("menu-root");
            Root.pickingMode = PickingMode.Ignore;
            InspectionLayer = UiFactory.Box("ss-layer", "ss-layer--inspection");
            InspectionLayer.pickingMode = PickingMode.Ignore;
            MenuLayer = UiFactory.Box("ss-layer", "ss-layer--menu");
            MenuLayer.pickingMode = PickingMode.Ignore;
            Root.Add(InspectionLayer);
            Root.Add(MenuLayer);

            MenuPanel = new MenuPanel
            {
                CommandHandler = HandleUiInput,
                RowActivated = ActivateMenuIndex,
                RowFocused = i => MenuState.SetFocusIndex(i),
                RowCycled = (i, dir) =>
                {
                    MenuState.SetFocusIndex(i);
                    CycleSetting(dir);
                },
            };
            MenuLayer.Add(MenuPanel);
            CodexPanel = new CodexPanel();
            CodexPanel.CloseRequested += () =>
            {
                MenuState.CloseTop();
                EmitMenuCloseSfx();
                RefreshAll();
            };
            MenuLayer.Add(CodexPanel);
            HotbarStrip = new HotbarStrip();
            TooltipCard = new TooltipCard();
            TutorialBanner = new TutorialBanner();

            AchievementsPanel = new AchievementsPanel();
            AchievementsPanel.BackRequested += CloseMetaScreen;
            SkillTreePanel = new SkillTreePanel();
            SkillTreePanel.BackRequested += CloseMetaScreen;
            SkillTreePanel.ConfirmRequested += () => RecordConfirm(MetaScreenConfirm());
            HubUpgradePanel = new HubUpgradePanel();
            HubUpgradePanel.BackRequested += CloseMetaScreen;
            HubUpgradePanel.ConfirmRequested += () => RecordConfirm(MetaScreenConfirm());
            ClassPanel = new ClassPanel();
            ClassPanel.BackRequested += CloseMetaScreen;
            ClassPanel.ConfirmRequested += () => RecordConfirm(MetaScreenConfirm());
            AudioLogPanel = new AudioLogPanel();
            AudioLogPanel.BackRequested += CloseMetaScreen;
            AudioSettingsPanel = new AudioSettingsPanel();
            AudioSettingsPanel.BackRequested += CloseMetaScreen;
            LanguageSelector = new LanguageSelector();
            LanguageSelector.BackRequested += CloseMetaScreen;
            LanguageSelector.LanguageChanged += OnLanguageChanged;
            ReleaseBadgeOverlay = new ReleaseBadgeOverlay();
            ReleaseBadgeOverlay.BackRequested += CloseMetaScreen;
            ReleaseBadgeOverlay.MetadataChanged += RefreshAll;
            CreditsScreen = new CreditsScreen();
            CreditsScreen.CreditsDismissed += CloseMetaScreen;
            SaveSlots = new SaveSlotScreenModel(_saveLoadMenu) { SnapshotBuilder = _snapshotBuilder, DemoSaveRefused = _demoSaveRefused };
            SaveLoadScreen = new SaveLoadScreen(SaveSlots);
            SaveLoadScreen.BackRequested += CloseMetaScreen;
            SaveLoadScreen.Confirmed += RecordConfirm;

            _metaPanels["achievements"] = AchievementsPanel;
            _metaPanels["skill_tree"] = SkillTreePanel;
            _metaPanels["hub_upgrades"] = HubUpgradePanel;
            _metaPanels["class"] = ClassPanel;
            _metaPanels["audio_log"] = AudioLogPanel;
            _metaPanels["audio_settings"] = AudioSettingsPanel;
            _metaPanels["language"] = LanguageSelector;
            _metaPanels["save_load"] = SaveLoadScreen;
            _metaPanels["release_badge"] = ReleaseBadgeOverlay;
            _metaPanels["credits"] = CreditsScreen;
            foreach (string id in MetaScreenIds)
            {
                SurfacePanel panel = _metaPanels[id];
                panel.style.display = DisplayStyle.None;
                MenuLayer.Add(panel);
            }

            MenuState.MenuChanged += OnMenuChanged;
            MenuState.EnabledChanged += (item, enabled) => RefreshAll();
            SubscribeTutorialState(TutorialState);
            TooltipPresenter.PayloadChanged += OnPayloadChanged;

            BindMetaScreens(a11y);
            ApplyAccessibilityToChildren();
            RefreshAll();
        }

        void SubscribeTutorialState(TutorialState state)
        {
            if (state == null) return;
            state.Triggered += OnTutorialTriggered;
            state.Dismissed += OnTutorialDismissed;
            state.CodexUnlocked += OnCodexUnlocked;
        }

        void UnsubscribeTutorialState(TutorialState state)
        {
            if (state == null) return;
            state.Triggered -= OnTutorialTriggered;
            state.Dismissed -= OnTutorialDismissed;
            state.CodexUnlocked -= OnCodexUnlocked;
        }

        /// <summary>
        /// (Re)binds the banner and Codex to <paramref name="state"/> (the session raises <c>TutorialStateReset</c> after
        /// boot, reload and save restores; the instance is normally the same, so this re-reads it). Null is ignored.
        /// </summary>
        public void BindTutorialState(TutorialState state)
        {
            if (state == null) return;
            if (!ReferenceEquals(state, TutorialState))
            {
                UnsubscribeTutorialState(TutorialState);
                TutorialState = state;
                TutorialStateInjected = true;
                SubscribeTutorialState(state);
            }
            RefreshTutorial();
            RefreshCodex();
        }

        /// <summary>Re-resolves glyph chips (the "auto" scheme follows the last-used device).</summary>
        public void RefreshGlyphs()
        {
            RefreshHotbar();
            ApplyGlyphs();
        }

        /// <summary>Godot bind_meta_screens' per-panel wiring (constructor-injected dependencies).</summary>
        void BindMetaScreens(AccessibilitySettings a11y)
        {
            AchievementsPanel.LoadCatalog();
            AchievementsPanel.SetState(_achievementState);
            AchievementsPanel.Render();
            SkillTreePanel.SetTree(_skillTreeState);
            SkillTreePanel.SetProgression(_playerProgression);
            SkillTreePanel.Render();
            HubUpgradePanel.SetCatalog(_hubUpgradeState);
            HubUpgradePanel.SetMetaState(_metaProgressionState);
            HubUpgradePanel.Render();
            ClassPanel.LoadCatalog();
            ClassPanel.SetMetaState(_metaProgressionState);
            ClassPanel.SetSelectedClass(_playerProgression.GetClassId());
            ClassPanel.Render();
            AudioLogPanel.SetAudioManager(_audio);
            AudioSettingsPanel.SetAudioManager(_audio);
            AudioSettingsPanel.SetSettingsState(SettingsState);
            // ADR-0044: the panel mutates SettingsState then asks THIS coordinator to emit settings_changed.
            AudioSettingsPanel.SetSettingsPush(EmitSettingsChanged);
            LanguageSelector.SetCatalog(_localizationCatalog);
            ReleaseBadgeOverlay.SetMetadata(_buildMetadataState);
            CreditsScreen.LoadCatalog();
            if (a11y != null) AccessibilitySettings = a11y;
        }

        /// <summary>Godot configure(): catalogs for menus, tutorials, codex, glyphs, tooltips, presets and bindings.</summary>
        public bool Configure(GdDict menuCatalog, GdDict tutorialCatalog, GdDict codexCatalog, GdDict glyphTable, GdDict tooltipCatalog,
            GdDict presetsCatalog, GdDict bindingsTable, AccessibilitySettings a11y)
        {
            AccessibilitySettings = a11y;
            _menuCatalog = menuCatalog?.DeepCopy() ?? new GdDict();
            _codexEntries.Clear();
            foreach (object entry in (codexCatalog ?? new GdDict()).GetArrayOrEmpty("entries"))
            {
                if (entry is GdDict dict) _codexEntries[V.Str(dict.Get("id", ""))] = dict.DeepCopy();
            }
            _presets = (presetsCatalog ?? new GdDict()).GetArrayOrEmpty("presets").DeepCopy();
            bool ok = true;
            ok = MenuState.Configure(menuCatalog) && ok;
            // An injected (session-owned) TutorialState is already configured; configuring it again would reset the run's
            // fired tutorials and Codex unlocks.
            if (!TutorialStateInjected) ok = TutorialState.Configure(tutorialCatalog) && ok;
            ok = ControllerGlyphState.Configure(glyphTable, bindingsTable) && ok;
            ok = TooltipPresenter.Configure(tooltipCatalog) && ok;
            ApplyAccessibilityToChildren();
            SetLoadAvailable(_loadAvailable);
            RefreshAll();
            return ok;
        }

        /// <summary>Configure from the synced data catalogs (CatalogRegistry). No bindings table (glyph text only).</summary>
        public bool ConfigureFromData(AccessibilitySettings a11y = null)
        {
            return Configure(
                CatalogRegistry.LoadDict(MenuCatalogPath) ?? new GdDict(),
                CatalogRegistry.LoadDict(TutorialCatalogPath) ?? new GdDict(),
                CatalogRegistry.LoadDict(CodexCatalogPath) ?? new GdDict(),
                GlyphChips.WithUnitySupplement(CatalogRegistry.LoadDict(GlyphChips.GlyphTablePath)),
                CatalogRegistry.LoadDict(TooltipCatalogPath) ?? new GdDict(),
                CatalogRegistry.LoadDict(PresetsCatalogPath) ?? new GdDict(),
                null,
                a11y ?? AccessibilitySettings);
        }

        public void ApplyAccessibilitySettings(AccessibilitySettings settings)
        {
            if (settings == null) return;
            AccessibilitySettings = settings;
            ApplyAccessibilityToChildren();
            RefreshAll();
        }

        // --- input -----------------------------------------------------------------------------

        /// <summary>
        /// Godot handle_ui_input. Pause and OpenCodex are global; everything else only while a menu is open. While a
        /// records screen is displayed it owns the input (Cancel returns to the records list).
        /// </summary>
        public bool HandleUiInput(UiCommand command)
        {
            if (TitleMode && (command == UiCommand.Pause || command == UiCommand.OpenCodex)) return false;
            if (TitleMode && command == UiCommand.Cancel && _activeMetaScreen.Length == 0
                && MenuState.GetCurrentMenu() == "main_menu" && MenuState.GetMenuHistory().IsEmpty)
                return true;
            if (command == UiCommand.Pause)
            {
                if (Stack.Top != null && Stack.Top.Time == SurfaceTime.Terminal)
                    return true;
                if (MenuState.IsInPlay())
                {
                    MenuState.OpenMenu("pause_menu");
                    EmitMenuOpenSfx();
                }
                else
                {
                    MenuState.CloseAll();
                    EmitMenuCloseSfx();
                }
                return true;
            }
            if (command == UiCommand.OpenCodex)
            {
                MenuState.OpenMenu("codex");
                EmitMenuOpenSfx();
                return true;
            }
            if (MenuState.IsInPlay()) return false;
            if (_activeMetaScreen.Length != 0)
            {
                SurfacePanel meta = _metaPanels[_activeMetaScreen];
                meta.Consume(command);
                return command != UiCommand.Pause && command != UiCommand.OpenCodex;
            }
            if (MenuState.GetCurrentMenu() == "codex" && command != UiCommand.Cancel && command != UiCommand.Accept)
            {
                CodexPanel.Consume(command);
                return true;
            }
            switch (command)
            {
                case UiCommand.Down:
                    MenuState.Navigate(0, 1);
                    RefreshMenuPanel();
                    return true;
                case UiCommand.Up:
                    MenuState.Navigate(0, -1);
                    RefreshMenuPanel();
                    return true;
                case UiCommand.Left:
                    CycleSetting(-1);
                    return true;
                case UiCommand.Right:
                    CycleSetting(1);
                    return true;
                case UiCommand.Accept:
                    ConfirmCurrentItem();
                    return true;
                case UiCommand.Cancel:
                    if (MenuState.GetCurrentMenu() == "codex") MenuState.CloseTop();
                    else MenuState.Cancel();
                    RefreshAll();
                    return true;
            }
            return false;
        }

        void ActivateMenuIndex(int index)
        {
            MenuState.SetFocusIndex(index);
            ConfirmCurrentItem();
        }

        // --- session seams ---------------------------------------------------------------------

        public void SetLoadAvailable(bool value)
        {
            _loadAvailable = value;
            if (MenuState.HasItem("main_menu", "continue")) MenuState.SetItemEnabled("main_menu", "continue", value);
            RefreshMenuPanel();
        }

        public void SetInventoryItems(IList<string> itemIds, int selectedIndex = 0)
        {
            _inventoryItemIds = new List<string>(itemIds ?? Array.Empty<string>());
            _selectedHotbarIndex = (int)GdMath.Clampi(selectedIndex, 0, Math.Max(0, _inventoryItemIds.Count - 1));
            RefreshHotbar();
        }

        public void SetHotbarSlots(IList<string> slotLabels, int selectedIndex = 0)
        {
            _hotbarSlotLabels = new List<string>(slotLabels ?? Array.Empty<string>());
            _selectedHotbarIndex = (int)GdMath.Clampi(selectedIndex, 0, Math.Max(0, _hotbarSlotLabels.Count - 1));
            RefreshHotbar();
        }

        /// <summary>ADR-0045: proximity focus and the inventory selection push both call this; the last call wins.</summary>
        public void SetTooltipQuery(GdDict query) => TooltipPresenter.Resolve(query);

        public string TriggerTutorial(string eventId, string targetId = "any")
        {
            string tutorialId = TutorialState.Trigger(eventId, targetId);
            RefreshCodex();
            return tutorialId;
        }

        public bool DismissLatestTutorial()
        {
            string latest = TutorialState.GetLatestTutorialId();
            return latest.Length != 0 && TutorialState.Dismiss(latest);
        }

        public void OpenMainMenu()
        {
            MenuState.OpenMenu("main_menu");
            RefreshAll();
        }

        /// <summary>ADR-0043 title handoff: dismisses the boot-time main menu. Idempotent.</summary>
        public bool DismissBootMenu() => MenuState.CloseAll();

        public string GetCurrentMenu() => MenuState.GetCurrentMenu();
        public long GetFocusIndex() => MenuState.GetFocusIndex();
        public GdDict GetSettingsSummary() => SettingsState.GetSummary();

        /// <summary>
        /// Applies stored preferences WITHOUT raising <see cref="SettingsChanged"/> (the in-run coordinator adopting the
        /// persisted file before anything can emit; nothing is re-saved).
        /// </summary>
        public bool LoadSettingsSummary(GdDict summary)
        {
            bool ok = SettingsState.ApplySummary(summary);
            if (ok)
            {
                if (AccessibilitySettings != null) SettingsState.ApplyToAccessibility(AccessibilitySettings);
                ApplyAccessibilityToChildren();
                SyncLanguageFromSettings();
            }
            RefreshAll();
            return ok;
        }

        void SyncLanguageFromSettings()
        {
            string language = SettingsState.GetLanguage();
            if (language == _activeLanguage) return;
            _activeLanguage = language;
            LanguageSelector.SetActiveLanguage(language);
        }

        public bool ApplySettingsSummary(GdDict summary)
        {
            bool ok = SettingsState.ApplySummary(summary);
            if (ok) SyncLanguageFromSettings();
            if (ok && AccessibilitySettings != null)
            {
                SettingsState.ApplyToAccessibility(AccessibilitySettings);
                ApplyAccessibilityToChildren();
                SettingsChanged?.Invoke(SettingsState.GetSummary());
            }
            RefreshAll();
            return ok;
        }

        public GdArray GetCodexUnlockedIds() => TutorialState.GetUnlockedCodexIds();
        public string GetHotbarText() => HotbarStrip.HotbarText;
        public string GetMenuText() => MenuPanel.BodyText;
        public string GetTutorialText() => TutorialBanner.Text;
        public string GetTooltipPanelText() => TooltipCard.Text;
        public string GetActiveLanguage() => _activeLanguage;
        public string LastClosedMenu => _lastClosedMenu;

        // --- inspection panels on the shared stack ----------------------------------------------

        /// <summary>Mounts <paramref name="panel"/> in the inspection layer (once) and pushes it on the stack. The
        /// session calls this after opening a LIVE panel and <see cref="NotifyInspectionClosed"/> from its PanelClosed.</summary>
        public void OpenInspection(SurfacePanel panel)
        {
            if (panel == null) return;
            if (panel.parent == null) InspectionLayer.Add(panel);
            if (!_inspections.Contains(panel)) _inspections.Add(panel);
            panel.SetGlyphResolver(GlyphFor);
            Stack.Push(panel);
        }

        readonly List<SurfacePanel> _inspections = new List<SurfacePanel>();

        /// <summary>Glyph chip text for an input action id under the current glyph scheme (move_forward/back aliased).</summary>
        public string GlyphFor(string inputActionId) =>
            GlyphChips.GlyphText(ControllerGlyphState, inputActionId, SettingsState.GetGlyphScheme());

        void ApplyGlyphs()
        {
            MenuPanel.SetGlyphResolver(GlyphFor);
            CodexPanel.SetGlyphResolver(GlyphFor);
            foreach (SurfacePanel meta in _metaPanels.Values) meta.SetGlyphResolver(GlyphFor);
            foreach (SurfacePanel panel in _inspections) panel.SetGlyphResolver(GlyphFor);
        }

        public void NotifyInspectionClosed(SurfacePanel panel) => Stack.Pop(panel);

        // --- settings ----------------------------------------------------------------------------

        void ConfirmCurrentItem()
        {
            string currentMenu = MenuState.GetCurrentMenu();
            string itemId = MenuState.Confirm();
            if (itemId.Length == 0) return;
            switch (currentMenu)
            {
                case "main_menu":
                    switch (itemId)
                    {
                        case "start":
                            if (TitleMode)
                            {
                                StartRequested?.Invoke();
                                break;
                            }
                            MenuState.CloseAll();
                            EmitMenuCloseSfx();
                            break;
                        case "continue":
                            LoadRequested?.Invoke();
                            break;
                        case "settings":
                            OpenSubmenu(currentMenu, itemId, "settings_menu");
                            break;
                        case "records":
                            OpenSubmenu(currentMenu, itemId, "records_menu");
                            break;
                        case "quit":
                            QuitRequested?.Invoke();
                            break;
                    }
                    break;
                case "pause_menu":
                    switch (itemId)
                    {
                        case "resume":
                            MenuState.CloseAll();
                            EmitMenuCloseSfx();
                            break;
                        case "settings":
                            OpenSubmenu(currentMenu, itemId, "settings_menu");
                            break;
                        case "codex":
                            OpenSubmenu(currentMenu, itemId, "codex");
                            break;
                        case "records":
                            OpenSubmenu(currentMenu, itemId, "records_menu");
                            break;
                        case "save":
                            SaveRequested?.Invoke();
                            break;
                        case "save_and_exit":
                            SaveAndExitRequested?.Invoke();
                            break;
                        case "quit_main":
                            QuitRequested?.Invoke();
                            break;
                    }
                    break;
                case "records_menu":
                    if (itemId == "back")
                    {
                        MenuState.CloseTop();
                        EmitMenuCloseSfx();
                    }
                    else
                    {
                        OpenMetaScreenInternal(itemId);
                    }
                    break;
                case "settings_menu":
                    if (itemId == "back")
                    {
                        MenuState.CloseTop();
                        EmitMenuCloseSfx();
                    }
                    else
                    {
                        CycleSetting(1);
                    }
                    break;
                case "codex":
                    if (itemId == "back") MenuState.CloseTop();
                    break;
            }
            RefreshAll();
        }

        /// <summary>Opens a submenu, remembering which item opened it so Back restores focus there.</summary>
        void OpenSubmenu(string fromMenu, string itemId, string submenu)
        {
            _focusMemory[fromMenu] = itemId;
            MenuState.OpenMenu(submenu);
            EmitMenuOpenSfx();
        }

        void CycleSetting(int direction)
        {
            if (MenuState.GetCurrentMenu() != "settings_menu") return;
            string itemId = V.Str(MenuState.GetFocusedItem().Get("id", ""));
            if (itemId.Length == 0 || itemId == "back") return;
            switch (itemId)
            {
                case "preset":
                    CyclePreset(direction);
                    break;
                case "text_scale":
                    CycleTextScale(direction);
                    break;
                case "colorblind":
                    CycleArraySetting("colorblind_mode", UiClasses.ColorblindModes, SettingsState.GetColorblindMode(), direction);
                    break;
                case "motion_reduce":
                    SettingsState.SetMotionReduce(!SettingsState.IsMotionReduce());
                    break;
                case "captions":
                    SettingsState.SetCaptionsEnabled(!SettingsState.IsCaptionsEnabled());
                    break;
                case "hold_to_tap":
                    SettingsState.SetHoldToTap(!SettingsState.IsHoldToTap());
                    break;
                case "difficulty":
                    CycleArraySetting("difficulty", new[] { "standard", "hardened", "deep_dive" }, SettingsState.GetDifficulty(), direction);
                    break;
                case "glyph_scheme":
                    CycleArraySetting("glyph_scheme", new[] { "auto", "keyboard", "gamepad_xbox", "gamepad_ps" }, SettingsState.GetGlyphScheme(), direction);
                    break;
            }
            if (AccessibilitySettings != null) SettingsState.ApplyToAccessibility(AccessibilitySettings);
            ApplyAccessibilityToChildren();
            SettingsChanged?.Invoke(SettingsState.GetSummary());
            RefreshAll();
        }

        /// <summary>ADR-0044: the single settings_changed re-emit handed to the audio settings screen.</summary>
        void EmitSettingsChanged() => SettingsChanged?.Invoke(SettingsState.GetSummary());

        void CyclePreset(int direction)
        {
            if (_presets.IsEmpty) return;
            var ids = new List<string>();
            foreach (object preset in _presets) ids.Add(V.Str(((GdDict)preset).Get("id", "default")));
            int index = ids.IndexOf(SettingsState.GetPresetId());
            if (index < 0) index = 0;
            index = (int)GdMath.Wrapi(index + direction, 0, ids.Count);
            SettingsState.ApplyPresetDict((GdDict)_presets[index]);
        }

        void CycleTextScale(int direction)
        {
            double[] scales = { 1.0, 1.5, 2.0 };
            double current = SettingsState.GetTextScale();
            int index = 0;
            for (int i = 0; i < scales.Length; i++)
            {
                if (GdMath.IsEqualApprox(scales[i], current))
                {
                    index = i;
                    break;
                }
            }
            index = (int)GdMath.Wrapi(index + direction, 0, scales.Length);
            SettingsState.SetTextScale(scales[index]);
        }

        void CycleArraySetting(string field, IReadOnlyList<string> values, string current, int direction)
        {
            int index = -1;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == current)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) index = 0;
            index = (int)GdMath.Wrapi(index + direction, 0, values.Count);
            switch (field)
            {
                case "colorblind_mode": SettingsState.SetColorblindMode(values[index]); break;
                case "difficulty": SettingsState.SetDifficulty(values[index]); break;
                case "glyph_scheme": SettingsState.SetGlyphScheme(values[index]); break;
            }
        }

        // --- records (meta) screens ----------------------------------------------------------

        /// <summary>Domain 6 (WI-3): cross-run unlocked entries for the codex.</summary>
        public List<string> GetRegistryUnlockLines()
        {
            var output = new List<string>();
            if (_unlockRegistry == null) return output;
            foreach (object uidV in _unlockRegistry.GetUnlockedIds())
            {
                string uid = V.Str(uidV);
                output.Add("- [" + _unlockRegistry.GetCategory(uid) + "] " + _unlockRegistry.GetDisplayName(uid));
            }
            return output;
        }

        void OpenMetaScreenInternal(string screenId)
        {
            if (!_metaPanels.ContainsKey(screenId)) return;
            if (screenId == "save_load") SaveLoadScreen.OnOpened();
            else if (screenId == "hub_upgrades") HubUpgradePanel.Render();
            else if (screenId == "skill_tree") SkillTreePanel.Render();
            else if (screenId == "class") ClassPanel.Render();
            else if (screenId == "achievements") AchievementsPanel.Render();
            else if (screenId == "audio_settings") AudioSettingsPanel.RefreshFromManager();
            _activeMetaScreen = screenId;
            EmitMenuOpenSfx();
            RefreshAll();
        }

        void CloseMetaScreen()
        {
            if (_activeMetaScreen == "save_load") SaveLoadScreen.OnClosed();
            _activeMetaScreen = "";
            EmitMenuCloseSfx();
            RefreshAll();
        }

        /// <summary>Validation/host seam: open the records list directly.</summary>
        public void OpenRecordsMenu()
        {
            MenuState.OpenMenu("records_menu");
            RefreshAll();
        }

        /// <summary>Validation/host seam: open one records screen (opening the records list first if needed).</summary>
        public void OpenMetaScreen(string screenId)
        {
            if (MenuState.GetCurrentMenu() != "records_menu") MenuState.OpenMenu("records_menu");
            OpenMetaScreenInternal(screenId);
        }

        public string GetActiveMetaScreen() => _activeMetaScreen;

        public GdDict GetLastMetaScreenConfirmResult() => _lastMetaScreenConfirmResult;

        public void ClearLastMetaScreenConfirmResult() => _lastMetaScreenConfirmResult = new GdDict();

        void RecordConfirm(GdDict result)
        {
            _lastMetaScreenConfirmResult = result;
            MetaScreenConfirmed?.Invoke(result);
            if (V.Str(result.Get("screen", "")) != "save_load" || !result.GetBool("ok", false)) return;
            string action = V.Str(result.Get("action", ""));
            if (action == "load" && SaveSlots.LastLoadedSnapshot != null)
                SlotSnapshotLoaded?.Invoke(V.Str(result.Get("detail", "")), SaveSlots.LastLoadedSnapshot);
            else if (action == "load_world")
                WorldLoadRequested?.Invoke();
        }

        /// <summary>Domain 6 host/input seam: moves the active interactive records screen's cursor.</summary>
        public void MetaScreenMoveSelection(int direction)
        {
            switch (_activeMetaScreen)
            {
                case "hub_upgrades":
                    HubUpgradePanel.MoveSelection(direction);
                    HubUpgradePanel.Render();
                    break;
                case "skill_tree":
                    SkillTreePanel.MoveSelection(direction);
                    SkillTreePanel.Render();
                    break;
                case "class":
                    ClassPanel.MoveSelection(direction);
                    ClassPanel.Render();
                    break;
                case "save_load":
                    SaveSlots.MoveSelection(direction);
                    break;
            }
        }

        /// <summary>Domain 6 host/input seam: confirm on the active records screen. Returns {screen, action, ok, detail}.</summary>
        public GdDict MetaScreenConfirm()
        {
            switch (_activeMetaScreen)
            {
                case "hub_upgrades":
                {
                    if (_demoScopeGate != null && _demoScopeGate.IsBlocked("hub.meta_progression"))
                    {
                        GdDict blocked = Result("hub_upgrades", "purchase", false, "demo_blocked");
                        HubUpgradePanel.ShowResult(blocked);
                        return blocked;
                    }
                    string sel = HubUpgradePanel.GetSelectedId();
                    bool ok = false;
                    if (sel.Length != 0 && _hubUpgradeState.Purchase(sel, _metaProgressionState))
                        ok = _metaProgressionState.SaveToDisk();
                    HubUpgradePanel.Render();
                    GdDict result = Result("hub_upgrades", "purchase", ok, sel);
                    HubUpgradePanel.ShowResult(result);
                    return result;
                }
                case "skill_tree":
                {
                    string sel = SkillTreePanel.GetSelectedId();
                    bool ok = false;
                    if (sel.Length != 0)
                    {
                        GdDict chk = _skillTreeState.CanUnlock(sel, _playerProgression, _metaProgressionState);
                        if (chk.GetBool("can", false)) ok = _skillTreeState.Unlock(sel);
                    }
                    SkillTreePanel.Render();
                    GdDict result = Result("skill_tree", "unlock", ok, sel);
                    SkillTreePanel.ShowResult(result, _metaProgressionState);
                    return result;
                }
                case "class":
                {
                    if (_demoScopeGate != null && _demoScopeGate.IsBlocked("hub.meta_progression"))
                    {
                        GdDict blocked = Result("class", "select", false, "demo_blocked");
                        ClassPanel.ShowResult(blocked);
                        return blocked;
                    }
                    string sel = ClassPanel.GetSelectedId();
                    bool ok = false;
                    if (sel.Length != 0 && ClassPanel.IsAvailable(sel))
                    {
                        _metaProgressionState.SetSelectedClass(sel);
                        ok = _metaProgressionState.SaveToDisk();
                        ClassPanel.SetSelectedClass(sel);
                    }
                    ClassPanel.Render();
                    GdDict result = Result("class", "select", ok, sel);
                    ClassPanel.ShowResult(result);
                    return result;
                }
                case "save_load":
                    return SaveLoadScreen.Confirm();
            }
            return Result(_activeMetaScreen, "none", false, "");
        }

        static GdDict Result(string screen, string action, bool ok, string detail) =>
            new GdDict { { "screen", screen }, { "action", action }, { "ok", ok }, { "detail", detail } };

        public List<string> GetMetaScreenIds() => new List<string>(MetaScreenIds);

        public SurfacePanel GetMetaScreenPanel(string screenId) => _metaPanels.TryGetValue(screenId, out SurfacePanel p) ? p : null;

        public SaveLoadMenu GetSaveLoadMenu() => _saveLoadMenu;

        /// <summary>Per-screen "mounted + populated" check (the records reachability gate).</summary>
        public bool MetaScreenIsPopulated(string screenId)
        {
            switch (screenId)
            {
                case "achievements": return AchievementsPanel.GetTotalCount() > 0;
                case "skill_tree": return SkillTreePanel.GetStatusLines().Count >= 1;
                case "hub_upgrades": return HubUpgradePanel.GetUpgradeCount() > 0;
                case "class": return ClassPanel.GetClassCount() > 0;
                case "audio_log": return AudioLogPanel.GetEntryCount() > 0;
                case "audio_settings": return AudioSettingsPanel.AudioManager != null;
                case "language": return LanguageSelector.GetKnownLanguages().Count > 0;
                case "save_load": return _saveLoadMenu.IsBound;
                case "release_badge": return ReleaseBadgeOverlay.GetBadgeText().Length != 0;
                case "credits": return CreditsScreen.GetEntryCount() > 0;
            }
            return false;
        }

        // --- accessibility -------------------------------------------------------------------

        /// <summary>Registers a document root (e.g. the HUD) that follows the text scale / motion / colour-blind classes.</summary>
        public void RegisterScaleRoot(VisualElement root)
        {
            if (root == null || _scaleRoots.Contains(root)) return;
            _scaleRoots.Add(root);
            if (AccessibilitySettings != null) ApplyAccessibilityClasses(root, AccessibilitySettings);
        }

        /// <summary>Reflow step (scale-150 / scale-200), reduce-motion, and cb-&lt;mode&gt; classes on a root.</summary>
        public static void ApplyAccessibilityClasses(VisualElement root, AccessibilitySettings settings)
        {
            if (root == null || settings == null) return;
            string reflow = settings.ReflowClass();
            root.EnableInClassList(AccessibilitySettings.ClassScale150, reflow == AccessibilitySettings.ClassScale150);
            root.EnableInClassList(AccessibilitySettings.ClassScale200, reflow == AccessibilitySettings.ClassScale200);
            root.EnableInClassList(UiClasses.ReduceMotion, settings.IsMotionReduce());
            foreach (string mode in UiClasses.ColorblindModes)
                root.EnableInClassList(UiClasses.ColorblindPrefix + mode, mode != "none" && settings.GetColorblindMode() == mode);
        }

        void ApplyAccessibilityToChildren()
        {
            if (AccessibilitySettings == null) return;
            ApplyAccessibilityClasses(Root, AccessibilitySettings);
            foreach (VisualElement root in _scaleRoots) ApplyAccessibilityClasses(root, AccessibilitySettings);
        }

        // --- rendering -----------------------------------------------------------------------

        void RefreshAll()
        {
            RefreshMenuPanel();
            RefreshCodex();
            RefreshHotbar();
            RefreshTutorial();
            RefreshMetaScreens();
            ApplyGlyphs();
            SyncStack();
        }

        void RefreshMenuPanel()
        {
            string currentMenu = MenuState.GetCurrentMenu();
            bool shown = currentMenu.Length != 0 && currentMenu != "codex" && _activeMetaScreen.Length == 0;
            MenuPanel.SetShown(shown);
            if (currentMenu.Length == 0 || currentMenu == "codex") return;
            string title = GdString.Capitalize(currentMenu);
            foreach (object menuEntry in _menuCatalog.GetArrayOrEmpty("menus"))
            {
                var menuDict = (GdDict)menuEntry;
                if (V.Str(menuDict.Get("id", "")) == currentMenu)
                {
                    title = V.Str(menuDict.Get("title", title));
                    break;
                }
            }
            var lines = new List<string>();
            var rows = new List<MenuPanel.Row>();
            GdArray items = MenuState.GetItems(currentMenu);
            long focus = MenuState.GetFocusIndex();
            for (int index = 0; index < items.Count; index++)
            {
                var item = (GdDict)items[index];
                string itemId = V.Str(item.Get("id", ""));
                string labelText = V.Str(item.Get("label", itemId));
                string value = currentMenu == "settings_menu" ? SettingsValue(itemId) : "";
                string lineText = value.Length != 0 ? labelText + ": " + value : labelText;
                bool enabled = MenuState.IsItemEnabled(currentMenu, itemId);
                lines.Add((index == focus ? "> " : "  ") + lineText + (enabled ? "" : " (disabled)"));
                rows.Add(new MenuPanel.Row
                {
                    Id = itemId,
                    Label = labelText,
                    Value = value,
                    Enabled = enabled,
                    Cyclable = currentMenu == "settings_menu" && itemId != "back",
                });
            }
            MenuPanel.SetContent(currentMenu, title, rows, (int)focus, string.Join("\n", lines));
            if (Stack.IsTop(MenuPanel) && (UiFocus.FocusedWithin(MenuPanel) != null || MenuPanel.panel?.focusController?.focusedElement == null))
                MenuPanel.FocusRow((int)focus);
        }

        /// <summary>The value half of the Godot settings line ("Text Scale: 1.5x" → "1.5x").</summary>
        string SettingsValue(string itemId)
        {
            switch (itemId)
            {
                case "preset": return SettingsState.GetPresetId();
                case "text_scale": return GdString.FormatFixed(SettingsState.GetTextScale(), 1) + "x";
                case "colorblind": return SettingsState.GetColorblindMode();
                case "motion_reduce": return SettingsState.IsMotionReduce() ? "On" : "Off";
                case "captions": return SettingsState.IsCaptionsEnabled() ? "On" : "Off";
                case "hold_to_tap": return SettingsState.IsHoldToTap() ? "On" : "Off";
                case "difficulty":
                {
                    string difficultyId = SettingsState.GetDifficulty();
                    return difficultyId + " (hazard x" + GdString.FormatFixed(DifficultyProfile.ForId(difficultyId).HazardModifier, 1) + ")";
                }
                case "glyph_scheme": return SettingsState.GetGlyphScheme();
            }
            return "";
        }

        void RefreshCodex()
        {
            CodexPanel.SetShown(MenuState.GetCurrentMenu() == "codex");
            var lines = new List<string> { "CODEX" };
            var entries = new List<CodexPanel.Entry>();
            foreach (object idV in TutorialState.GetUnlockedCodexIds())
            {
                string entryId = V.Str(idV);
                if (!_codexEntries.Has(entryId)) continue;
                var entry = (GdDict)_codexEntries[entryId];
                string topic = V.Str(entry.Get("topic", "Misc"));
                string title = V.Str(entry.Get("title", entryId));
                string body = V.Str(entry.Get("body", ""));
                lines.Add("- " + topic + " | " + title);
                lines.Add("  " + body);
                entries.Add(new CodexPanel.Entry { Id = entryId, Topic = topic, Title = title, Body = body });
            }
            if (lines.Count == 1) lines.Add(CodexPanel.EmptyText);
            List<string> registryLines = GetRegistryUnlockLines();
            if (registryLines.Count > 0)
            {
                lines.Add("— CROSS-RUN UNLOCKS —");
                lines.AddRange(registryLines);
            }
            CodexPanel.SetContent(entries, registryLines, lines);
        }

        void RefreshHotbar()
        {
            var slots = new List<string>();
            if (_hotbarSlotLabels.Count > 0)
            {
                slots.AddRange(_hotbarSlotLabels);
            }
            else
            {
                for (int index = 0; index < 5; index++)
                    slots.Add(index < _inventoryItemIds.Count ? _inventoryItemIds[index] : "(empty)");
            }
            string scheme = ControllerGlyphState.ResolveScheme(SettingsState.GetGlyphScheme());
            string useGlyph = ControllerGlyphState.GlyphFor("interact", scheme);
            HotbarStrip.SetSlots(slots, _selectedHotbarIndex, useGlyph);
            UiFactory.SetShown(HotbarStrip, MenuState.IsInPlay());
        }

        void RefreshTutorial()
        {
            if (TutorialState.HasPendingBanner())
            {
                string tutorialId = TutorialState.GetLatestTutorialId();
                TutorialBanner.ShowTutorial(TutorialState.GetTitle(tutorialId), TutorialState.GetBody(tutorialId));
            }
            else
            {
                TutorialBanner.ShowTutorial("", "");
            }
        }

        void RefreshMetaScreens()
        {
            foreach (var pair in _metaPanels) UiFactory.SetShown(pair.Value, pair.Key == _activeMetaScreen);
        }

        /// <summary>Reconciles the coordinator-owned surfaces on the modal stack with the menu state.</summary>
        void SyncStack()
        {
            string current = MenuState.GetCurrentMenu();
            bool anyMenu = current.Length != 0 && current != "codex";
            foreach (object h in MenuState.GetMenuHistory())
            {
                if (V.Str(h) != "codex") anyMenu = true;
            }
            var want = new List<IInputConsumer>();
            if (anyMenu) want.Add(MenuPanel);
            if (current == "codex") want.Add(CodexPanel);
            if (_activeMetaScreen.Length != 0) want.Add(_metaPanels[_activeMetaScreen]);

            var owned = new List<IInputConsumer> { MenuPanel, CodexPanel };
            owned.AddRange(_metaPanels.Values);
            for (int i = Stack.Surfaces.Count - 1; i >= 0; i--)
            {
                IInputConsumer c = Stack.Surfaces[i];
                if (owned.Contains(c) && !want.Contains(c)) Stack.Pop(c);
            }
            foreach (IInputConsumer c in want)
            {
                if (!Stack.Contains(c)) Stack.Push(c);
            }
        }

        // --- model signal handlers -----------------------------------------------------------

        void OnLanguageChanged(string languageId)
        {
            _activeLanguage = languageId;
            // B5: the choice is a persisted preference (Godot kept it in memory only).
            if (SettingsState.SetLanguage(languageId))
                SettingsChanged?.Invoke(SettingsState.GetSummary());
            LanguageChanged?.Invoke(languageId);
            RefreshAll();
        }

        void OnMenuChanged(string newMenuId, string previousMenuId)
        {
            _lastClosedMenu = previousMenuId;
            if (newMenuId != "records_menu" && _activeMetaScreen.Length != 0)
            {
                if (_activeMetaScreen == "save_load") SaveLoadScreen.OnClosed();
                _activeMetaScreen = "";
            }
            if (newMenuId.Length == 0)
            {
                _focusMemory.Clear();
            }
            else if (_focusMemory.TryGetValue(newMenuId, out string itemId) && previousMenuId.Length != 0)
            {
                // Back from a submenu: restore focus to the item that opened it (stable token, not index 0).
                GdArray items = MenuState.GetItems(newMenuId);
                for (int i = 0; i < items.Count; i++)
                {
                    if (V.Str(((GdDict)items[i]).Get("id", "")) == itemId)
                    {
                        MenuState.SetFocusIndex(i);
                        break;
                    }
                }
                _focusMemory.Remove(newMenuId);
            }
            if (previousMenuId.Length == 0 && newMenuId.Length != 0) ModalOpened?.Invoke(newMenuId);
            else if (previousMenuId.Length != 0 && newMenuId.Length == 0) ModalClosed?.Invoke(previousMenuId);
            RefreshAll();
            if (newMenuId.Length != 0 && newMenuId != "codex" && Stack.IsTop(MenuPanel)) MenuPanel.FocusRow((int)MenuState.GetFocusIndex());
        }

        void OnTutorialTriggered(string tutorialId, string title, string body)
        {
            TutorialBanner.ShowTutorial(title, body);
            if (!TutorialStateInjected) _audio.PlaySfx(AudioEventSeam.UI_OBJECTIVE_ADVANCE);
            RefreshCodex();
        }

        void OnTutorialDismissed(string tutorialId)
        {
            if (!TutorialStateInjected) _audio.PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            RefreshTutorial();
            RefreshCodex();
        }

        void OnCodexUnlocked(string codexEntryId)
        {
            if (!TutorialStateInjected) _audio.PlaySfx(AudioEventSeam.UI_OBJECTIVE_ADVANCE);
            RefreshCodex();
        }

        void OnPayloadChanged(TooltipPayload payload)
        {
            if (payload == null)
            {
                TooltipCard.SetPayload("", "", "");
                return;
            }
            TooltipCard.SetPayload(payload.Title, payload.Body, payload.Footer);
        }

        void EmitMenuOpenSfx() => _audio.PlaySfx(AudioEventSeam.UI_PANEL_OPEN);

        void EmitMenuCloseSfx() => _audio.PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
    }
}
