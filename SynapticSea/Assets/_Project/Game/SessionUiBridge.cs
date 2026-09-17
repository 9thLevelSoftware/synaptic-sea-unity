// The UI half of scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _build_hud_layer, the HUD/tracker pushes, the
// panel open/close helpers (_open_inventory_self, toggle_wounds_panel_from_input, open_*_for_validation) and _input.
using System;
using System.Collections.Generic;
using SynapticSea.App;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Input;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;
using UnityEngine;
using UnityEngine.UIElements;

namespace SynapticSea.Game
{
    /// <summary>
    /// Wires a booted <see cref="RunSession"/> to the UI Toolkit HUD and menus (docs/ui-port-notes.md "Session wiring"):
    /// <list type="bullet">
    /// <item>builds the <see cref="MenuCoordinator"/> over the session's single <see cref="RunSession.TutorialState"/> and
    /// <see cref="RunSession.SettingsState"/>, adopts the persisted preferences (<see cref="AppServices.Settings"/>) before
    /// anything can emit, and re-adopts them after loads (a save's settings never clobber the player's preferences);</item>
    /// <item>mounts the coordinator and the <see cref="HudRoot"/> (accessibility applied at mount and on every settings
    /// change; world labels drawn into the HUD's label layer at the current text scale);</item>
    /// <item>routes input through <see cref="UiInputRouter"/> and gates gameplay input (attack, reload, hotbar, interact)
    /// on the modal stack through <see cref="RunSessionHost.GameplayInputBlocked"/>;</item>
    /// <item>opens the LIVE inspection panels (Wounds through the session's treatment API), forwards save/load/quit/settings
    /// and language, raises denial toasts, and pushes the session's HUD events (weapon line, damage feedback);</item>
    /// <item>opens <see cref="RunResultsPanel"/> on death or extract (<see cref="RunSession.PlayableSliceCompleted"/>),
    /// matching Godot title_main.gd <c>_show_run_results</c>. Quit-to-title does not replace that panel.</item>
    /// </list>
    /// </summary>
    public sealed class SessionUiBridge : IRunUiState
    {
        public readonly HudRoot Hud;
        public readonly UIDocument MenuDocument;
        public readonly SynapticSeaInput Input;

        public MenuCoordinator Coordinator { get; private set; }
        public UiInputRouter Router { get; private set; }
        public AccessibilitySettings Accessibility { get; }

        /// <summary>Persists changed preferences (defaults to <see cref="AppServices.ApplySettings"/>); null = session only.</summary>
        public Action<GdDict> SettingsPersist = summary => AppServices.Instance?.ApplySettings(summary);

        /// <summary>
        /// The stored preferences adopted in play: the preferences file (<see cref="UserSettingsStore"/>), else the app's
        /// in-memory settings; null = none. Read once at build; later changes update the bridge's copy.
        /// </summary>
        public Func<GdDict> PersistedSettings = () =>
            UserSettingsStore.Load(CoreServices.UserStorage) ?? (AppServices.Instance != null ? AppServices.Instance.Settings.GetSummary() : null);

        public IUiAudio Audio { get; private set; }

        /// <summary>The last-used device family (the "auto" glyph scheme).</summary>
        public LastInputDevice Devices { get; private set; }

        public readonly InventoryPanel Inventory = new InventoryPanel();
        public readonly WoundsPanel Wounds = new WoundsPanel();
        public readonly ScannerPanel Scanner = new ScannerPanel();
        public readonly ShipModificationPanel ShipMod = new ShipModificationPanel();
        public readonly ChartPanel Chart = new ChartPanel();
        public readonly RecipePickerPanel RecipePicker = new RecipePickerPanel();

        public const string NoWebChartText = "No web chart";
        public const string ShipModUnavailableText = "Ship modification unavailable";

        /// <summary>The last HUD-feedback line the bridge raised for a denied toggle (e.g. "No web chart").</summary>
        public string LastDenyLine { get; private set; } = "";

        /// <summary>The language the player picked (persisted through the settings file).</summary>
        public string ActiveLanguage => Coordinator != null ? Coordinator.GetActiveLanguage() : SettingsState.DefaultLanguage;

        /// <summary>The end-of-run results surface once death or extract ended the run (null before).</summary>
        public RunResultsPanel Results { get; private set; }

        /// <summary>The completion summary shown on <see cref="Results"/>, with the run context added.</summary>
        public GdDict ResultsSummary { get; private set; }

        /// <summary>Results confirm (Return to Title). The host records <see cref="RunReturnInfo"/> and loads Title.</summary>
        public event Action ResultsReturnToTitleRequested;

        /// <summary>Results "New Run". The host starts the next launch; it does not skip the results panel.</summary>
        public event Action ResultsNewRunRequested;

        RunSession _session;
        RunSessionHost _host;
        string _focusPrompt = "";
        string _objectivePrompt = "";
        string _hotbarText;
        float _nextEffectRefresh;
        GdDict _preferences;

        // Events the boot raised before the coordinator existed; replayed once it does.
        bool? _pendingLoadAvailable;
        GdArray _pendingInventoryItems;
        (GdArray labels, long selected)? _pendingHotbar;
        GdDict _pendingTooltip;
        GdDict _pendingCompletion;

        public SessionUiBridge(HudRoot hud, UIDocument menuDocument, SynapticSeaInput input, AccessibilitySettings accessibility)
        {
            Hud = hud;
            MenuDocument = menuDocument;
            Input = input;
            Accessibility = accessibility ?? new AccessibilitySettings();
        }

        // ------------------------------------------------------------------ IRunUiState
        public bool RecipePickerOpen => RecipePicker.IsOpen();
        public bool ScannerOpen => Scanner.IsOpen();
        public bool InventoryOpen => Inventory.IsOpen();
        public bool MenusClosed => Coordinator == null || Coordinator.MenuState.IsInPlay();

        // ------------------------------------------------------------------ boot

        /// <summary>Subscribes the HUD to the session's events. Call from the session's before-ready hook.</summary>
        public void BindSessionEvents(RunSession session)
        {
            _session = session;
            SessionEvents e = session.Events;
            e.TrackerObjectivesSet += specs => Hud.Objective?.SetObjectives(specs);
            e.TrackerCompleted += seq => Hud.Objective?.MarkCompleted(seq);
            e.TrackerCurrentSequence += seq => Hud.Objective?.SetCurrentSequence(seq);
            e.TrackerStepProgress += (seq, p) => Hud.Objective?.SetStepProgress(seq, p);
            e.TrackerRunComplete += () => Hud.Objective?.MarkRunComplete();
            e.TrackerInteractionPrompt += text =>
            {
                _objectivePrompt = text ?? "";
                Hud.Objective?.SetInteractionPrompt(text);
                RefreshPrompt();
            };
            e.TrackerSystemStatusLines += lines => Hud.Objective?.SetSystemStatusLines(lines);
            e.WorkActionHudState += state => Hud.Work?.SetWorkState(state);
            e.HotbarText += text =>
            {
                _hotbarText = text ?? "";
                Hud.Vitals?.SetWeaponLine(_hotbarText);
            };
            e.LoadAvailable += v =>
            {
                if (Coordinator != null) Coordinator.SetLoadAvailable(v);
                else _pendingLoadAvailable = v;
            };
            e.InventoryItems += ids =>
            {
                if (Coordinator != null) Coordinator.SetInventoryItems(ToStrings(ids));
                else _pendingInventoryItems = ids;
            };
            e.HotbarSlots += (labels, selected) =>
            {
                if (Coordinator != null) Coordinator.SetHotbarSlots(ToStrings(labels), (int)selected);
                else _pendingHotbar = (labels, selected);
            };
            e.TooltipQuery += q =>
            {
                if (Coordinator != null) Coordinator.SetTooltipQuery(q);
                else _pendingTooltip = q;
            };
            // A5: the coordinator shows the session's TutorialState itself (banner + Codex); after a reload or restore it
            // re-reads it, and the player's stored preferences win over the settings a save carried.
            e.TutorialStateReset += state =>
            {
                if (Coordinator == null) return;
                Coordinator.BindTutorialState(state);
                ApplyPersistedPreferences();
            };
            e.WoundsChanged += _ =>
            {
                if (Wounds.IsOpen()) Wounds.Refresh();
            };
            e.PanelRequested += OnPanelRequested;
            session.PlayableSliceCompleted -= OnSliceCompleted;
            session.PlayableSliceCompleted += OnSliceCompleted;
        }

        /// <summary>Builds the coordinator from the booted session (Godot bind_meta_screens order) and mounts the UI.</summary>
        public void BuildCoordinator(RunSession session, RunSessionHost host, AudioManager manager)
        {
            _session = session;
            _host = host;
            Audio = new SessionUiAudio(session, manager);
            Devices = new LastInputDevice();
            var saveLoadMenu = new SaveLoadMenu();
            saveLoadMenu.Bind(session.SaveLoadService);
            Coordinator = new MenuCoordinator(
                session.AchievementState,
                Audio,
                session.SkillTreeState,
                session.PlayerProgression,
                session.HubUpgradeState,
                session.MetaProgressionState,
                session.LocalizationCatalog,
                session.BuildMetadataState,
                saveLoadMenu,
                Accessibility,
                session.UnlockRegistry,
                () => RunSnapshotAssembler.Build(session),
                session.DemoScopeGate,
                null,
                Devices.JoypadCountForGlyphs,
                session.TutorialState,
                session.SettingsState);
            Coordinator.ConfigureFromData(Accessibility);
            // B1: adopt the stored preferences before any handler is subscribed, so nothing re-saves defaults. The copy is
            // kept (and follows later changes): the session's SettingsState may be the app's own instance, which a save
            // restore overwrites in memory.
            _preferences = PersistedSettings?.Invoke()?.DeepCopy();
            ApplyPersistedPreferences();
            Devices.Changed += _ => Coordinator?.RefreshGlyphs();

            MenuDocument.rootVisualElement.Add(Coordinator.Root);
            Hud.Mount(Coordinator);
            ApplyAccessibilityToScene();
            if (host != null && host.WorldLabels != null) host.WorldLabels.Container = Hud.WorldLabelLayer;

            if (_pendingLoadAvailable.HasValue) Coordinator.SetLoadAvailable(_pendingLoadAvailable.Value);
            else Coordinator.SetLoadAvailable(session.IsLoadAvailable());
            if (_pendingInventoryItems != null) Coordinator.SetInventoryItems(ToStrings(_pendingInventoryItems));
            if (_pendingHotbar.HasValue) Coordinator.SetHotbarSlots(ToStrings(_pendingHotbar.Value.labels), (int)_pendingHotbar.Value.selected);
            if (_pendingTooltip != null) Coordinator.SetTooltipQuery(_pendingTooltip);
            if (_hotbarText != null) Hud.Vitals?.SetWeaponLine(_hotbarText);

            Coordinator.SaveRequested += () => session.RequestSave();
            Coordinator.LoadRequested += () => AfterLoad(session.RequestLoad());
            Coordinator.WorldLoadRequested += () => AfterLoad(session.RequestLoad());
            Coordinator.QuitRequested += session.QuitToTitle;
            Coordinator.SaveAndExitRequested += session.SaveAndExit;
            Coordinator.SettingsChanged += OnSettingsChanged;
            Coordinator.SlotSnapshotLoaded += (slotId, snapshot) => AfterLoad(session.ApplyManualSlot(snapshot));
            Coordinator.LanguageChanged += OnLanguageChanged;

            Inventory.SetAudioManager(Audio);
            Inventory.SetTooltipQueryPush(Coordinator.SetTooltipQuery);
            Inventory.PanelClosed += () => OnInspectionClosed(Inventory);
            Inventory.TransferCompleted += session.OnInventoryTransferCompleted;
            Inventory.UseRequested += (itemId, useAll) => session.UseConsumableItem(itemId, useAll);
            Wounds.SetAudioManager(Audio);
            Wounds.Bind(new SessionWoundHost(session));
            Wounds.PanelClosed += () => OnInspectionClosed(Wounds);
            Scanner.Bind(new SessionScannerHost(session, Audio));
            Scanner.PanelClosed += () => OnInspectionClosed(Scanner);
            ShipMod.PanelClosed += () => OnInspectionClosed(ShipMod);
            ShipMod.InstallRequested += (slot, component, form) => session.OnShipModInstalled(component, form);
            ShipMod.UninstallRequested += (slot, component, form) => session.OnShipModUninstalled(component, ShipMod.GetInventoryBag());
            Chart.PanelClosed += () => OnInspectionClosed(Chart);
            RecipePicker.Bind(new SessionRecipeHost(session, Audio));
            RecipePicker.PanelClosed += () => OnInspectionClosed(RecipePicker);

            Router = new UiInputRouter(Input, Coordinator.Stack, Coordinator.HandleUiInput);
            Router.PanelToggleRequested += OnPanelToggle;
#if !SS_BUILD_RELEASE
            Router.DevShortcutRequested += OnDevShortcut;
#endif
            Router.Enable();

            if (host != null)
            {
                host.FocusPromptChanged += prompt =>
                {
                    _focusPrompt = prompt ?? "";
                    RefreshPrompt();
                };
                host.SimulationPaused = () => Coordinator.Stack.SimulationPaused;
                host.GameplayInputBlocked = () => Coordinator.Stack.BlocksGameplay;
                host.MotionReduce = Accessibility.IsMotionReduce;
                host.PlayerDamaged += OnPlayerDamaged;
            }
            if (_pendingCompletion != null) ShowRunResults(_pendingCompletion);
        }

        /// <summary>Per-frame UI work (before the session tick): input routing, the vitals cluster and status icons.</summary>
        public void Tick()
        {
            Router?.Tick();
            if (_session?.VitalsModel != null && Hud.Vitals != null) Hud.Vitals.Refresh(_session.VitalsModel);
            if (Hud.Vitals != null && Time.unscaledTime >= _nextEffectRefresh)
            {
                _nextEffectRefresh = Time.unscaledTime + 0.25f;
                RefreshStatusEffectIcons();
            }
        }

        void RefreshPrompt() => Hud.SetContextPrompt(_focusPrompt.Length > 0 ? _focusPrompt : _objectivePrompt);

        static List<string> ToStrings(GdArray values)
        {
            var output = new List<string>();
            if (values != null)
                foreach (object v in values) output.Add(V.Str(v));
            return output;
        }

        /// <summary>D6: active status effects as icon chips in the cluster.</summary>
        public void RefreshStatusEffectIcons()
        {
            if (Hud.Vitals == null) return;
            var ids = new List<string>();
            if (_session?.StatusEffectsState != null)
                foreach (object effect in _session.StatusEffectsState.Effects)
                    if (effect is GdDict d) ids.Add(V.Str(d.Get("id", "")));
            Hud.Vitals.SetStatusEffects(ids, UiIcons.ForStatusEffect);
        }

        // ------------------------------------------------------------------ settings (B1, B2, B4, B5)

        /// <summary>
        /// Adopts the stored preferences into the in-run coordinator (and so the session's SettingsState) without emitting,
        /// then applies the stored bus volumes / mutes to the session's <c>SessionAudio.BusConfig</c> and the scene.
        /// </summary>
        public void ApplyPersistedPreferences()
        {
            if (Coordinator == null) return;
            if (_preferences != null) Coordinator.LoadSettingsSummary(_preferences);
            _session?.ApplyUiSettingsSummary(Coordinator.GetSettingsSummary());
            ApplyBusPreferences(Coordinator.SettingsState);
            ApplyAccessibilityToScene();
        }

        void ApplyBusPreferences(SettingsState settings)
        {
            SessionAudio audio = _session?.AudioManager;
            if (audio == null || settings == null) return;
            GdDict volumes = settings.GetAudioBusVolumes();
            foreach (object bus in volumes.Keys) audio.SetBusVolume(V.Str(bus), V.F64(volumes[bus]));
            GdDict mutes = settings.GetAudioBusMutes();
            foreach (object bus in mutes.Keys) audio.SetBusMuted(V.Str(bus), V.Bool(mutes[bus]));
        }

        void OnSettingsChanged(GdDict summary)
        {
            if (summary != null) _preferences = summary.DeepCopy();
            _session?.ApplyUiSettingsSummary(summary);
            ApplyAccessibilityToScene();
            SettingsPersist?.Invoke(summary);
        }

        /// <summary>B4: the HUD reflow / motion / colour-blind classes and the world-label text scale.</summary>
        void ApplyAccessibilityToScene()
        {
            if (Hud != null && Hud.Root != null) Hud.ApplyAccessibility(Accessibility);
            if (_host != null && _host.WorldLabels != null) _host.WorldLabels.TextScale = (float)Accessibility.GetTextScale();
        }

        /// <summary>
        /// B5: the language choice is persisted through the settings file (the coordinator raised SettingsChanged). Godot's
        /// LocalizationCatalog only carries "en" and no UI string read it; the choice is kept and logged.
        /// </summary>
        void OnLanguageChanged(string languageId)
        {
            CoreServices.Log.Info("[SessionUiBridge] language set to '" + languageId + "'");
        }

        void AfterLoad(bool loaded)
        {
            if (loaded) ApplyPersistedPreferences();
        }

        // ------------------------------------------------------------------ run end (Godot title_main._show_run_results)

        void OnSliceCompleted(GdDict summary)
        {
            GdDict completion = (summary ?? new GdDict()).DeepCopy();
            if (Coordinator == null)
            {
                _pendingCompletion = completion;
                return;
            }
            ShowRunResults(completion);
        }

        /// <summary>
        /// Pauses the run (TERMINAL surface on the modal stack) and shows the results with the run context.
        /// Idempotent. Quit-to-title must not call this — that path is abandon, not death/extract.
        /// </summary>
        public void ShowRunResults(GdDict completion)
        {
            _pendingCompletion = null;
            if (Results != null || Coordinator == null) return;
            RunSession s = _session;
            GdDict summary = (completion ?? new GdDict()).DeepCopy();
            if (s != null)
            {
                summary["seed"] = s.RunSeed;
                summary["biome_id"] = s.BiomeId;
                summary["difficulty_id"] = s.DifficultyId;
            }
            ResultsSummary = summary;
            Results = new RunResultsPanel();
            Results.SetRunSummary(summary);
            Results.SetContextLine(ContextLine(summary));
            Results.ReturnToTitleRequested += () => ResultsReturnToTitleRequested?.Invoke();
            Results.NewRunRequested += () => ResultsNewRunRequested?.Invoke();
            Coordinator.MenuState.CloseAll();
            Coordinator.OpenInspection(Results);
        }

        /// <summary>The results / title context line, e.g. "seed 17 · breach_field · standard".</summary>
        public static string ContextLine(GdDict summary)
        {
            summary = summary ?? new GdDict();
            string biome = summary.GetString("biome_id", "");
            return "seed " + V.I64(summary.Get("seed", 0L)) + " · " + (biome.Length != 0 ? biome : "no biome") + " · " + summary.GetString("difficulty_id", "standard");
        }

        // ------------------------------------------------------------------ HUD feedback

        void OnPlayerDamaged(double damage, string archetypeId, Vec3 from)
        {
            Hud.ShowDamage(damage, archetypeId);
        }

        void Deny(string line)
        {
            LastDenyLine = line;
            Audio?.PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            Hud.ShowToast(line, Severity.Caution);
        }

        // ------------------------------------------------------------------ panels

        void OnInspectionClosed(SurfacePanel panel)
        {
            Coordinator?.NotifyInspectionClosed(panel);
            if (panel == Inventory) Audio?.PlaySfx(AudioEventSeam.UI_INVENTORY_CLOSE);
            // _on_inventory_panel_closed / _unfreeze_player_after_panel.
            _session?.Scene?.SetPlayerFrozen(false);
        }

        void Show(SurfacePanel panel)
        {
            Coordinator.OpenInspection(panel);
        }

        /// <summary>A Panels-map toggle the router allowed (Godot <c>_input</c>'s per-panel branches).</summary>
        public void OnPanelToggle(string action)
        {
            if (_session == null || Coordinator == null) return;
            switch (action)
            {
                case "toggle_inventory":
                    if (Inventory.IsOpen()) Inventory.Close();
                    else OpenInventorySelf();
                    break;
                case "toggle_scanner":
                    if (Scanner.IsOpen())
                    {
                        Scanner.Close();
                    }
                    else
                    {
                        Scanner.Open();
                        if (Scanner.IsOpen())
                        {
                            Show(Scanner);
                            _session.TriggerTutorial("scanner_opened", "any");
                        }
                    }
                    break;
                case "toggle_wounds":
                    if (Wounds.IsOpen())
                    {
                        Wounds.Close();
                    }
                    else
                    {
                        Wounds.Bind(new SessionWoundHost(_session));
                        Wounds.Open();
                        if (Wounds.IsOpen()) Show(Wounds);
                    }
                    break;
                case "toggle_ship_mod":
                    if (ShipMod.IsOpen())
                    {
                        ShipMod.Close();
                    }
                    else if (_session.ShipModificationState == null)
                    {
                        // B7: Godot returned silently; the player now hears and reads why nothing opened.
                        Deny(ShipModUnavailableText);
                    }
                    else
                    {
                        ShipMod.SetCatalog(_session.ComponentCatalog);
                        ShipMod.Bind(_session.ShipModificationState, InventoryBag());
                        ShipMod.Open();
                        if (ShipMod.IsOpen())
                        {
                            Show(ShipMod);
                            Audio?.PlaySfx(AudioEventSeam.UI_SHIP_MOD_OPEN);
                        }
                    }
                    break;
                case "ui_open_map":
                    if (Chart.IsOpen())
                    {
                        Chart.Close();
                    }
                    else if (_session.InventoryState == null || _session.InventoryState.GetQuantity("web_chart") <= 0)
                    {
                        // Domain 10 (ADR-0045): Godot's HUD feedback line; shown as a toast (B7).
                        Deny(NoWebChartText);
                    }
                    else
                    {
                        Chart.Bind(_session.WebChartState);
                        Chart.BindSeaGraph(_session.SeaGraph);
                        Chart.Open();
                        Chart.RefreshExtractionRoute();
                        if (Chart.IsOpen())
                        {
                            Show(Chart);
                            Audio?.PlaySfx(AudioEventSeam.UI_CHART_ROUTE);
                        }
                    }
                    break;
            }
        }

        /// <summary><c>_open_inventory_self</c>.</summary>
        public void OpenInventorySelf()
        {
            if (_session?.InventoryState == null || Coordinator == null) return;
            Inventory.OpenSelf(_session.InventoryState, _session.EquipmentState);
            Show(Inventory);
            Audio?.PlaySfx(AudioEventSeam.UI_INVENTORY_OPEN);
            _session.TriggerTutorial("inventory_opened", "any");
        }

        /// <summary>The session's own panel requests (transfer holds/carts, recipe pickers).</summary>
        void OnPanelRequested(string panelId, GdDict args)
        {
            if (Coordinator == null || _session == null) return;
            switch (panelId)
            {
                case "transfer":
                    CargoTransfer.ICargoHold hold = null;
                    string shipId = V.Str(args.Get("ship_id", ""));
                    string cartId = V.Str(args.Get("cart_id", ""));
                    if (shipId.Length > 0) hold = _session.GetShipById(shipId)?.GetInventory();
                    else if (cartId.Length > 0) hold = FindCart(cartId)?.GetHold();
                    if (hold == null) return;
                    if (Inventory.IsOpen()) Inventory.Close();
                    Inventory.OpenTransfer(_session.InventoryState, hold, V.Str(args.Get("label", "HOLD")), _session.EquipmentState);
                    Show(Inventory);
                    break;
                case "recipe_picker":
                    if (RecipePicker.IsOpen()) RecipePicker.Close();
                    RecipePicker.OpenForStation(V.Str(args.Get("station_kind", "")));
                    if (RecipePicker.IsOpen()) Show(RecipePicker);
                    break;
                case "inventory_self":
                    OpenInventorySelf();
                    break;
                default:
                    OnPanelToggle(panelId == "wounds" ? "toggle_wounds" : panelId == "ship_mod" ? "toggle_ship_mod" : panelId == "chart" ? "ui_open_map" : panelId == "scanner" ? "toggle_scanner" : panelId);
                    break;
            }
        }

        CartState FindCart(string cartId)
        {
            var ships = new List<ShipInstance> { _session.HomeShip, _session.LifeboatShip, _session.CurrentShip };
            ships.AddRange(_session.VisitedShips.Values);
            foreach (ShipInstance ship in ships)
            {
                if (ship == null) continue;
                foreach (CartState cart in ship.GetCarts())
                    if (cart != null && cart.CartId == cartId) return cart;
            }
            return null;
        }

        GdDict InventoryBag() => _session.InventoryState != null ? _session.InventoryState.Items.DeepCopy() : new GdDict();

        /// <summary>
        /// F5 save / F6 quicksave / F9 load, only in play (the router already checked the stack). Development builds only:
        /// the router subscription and this body compile out under <c>SS_BUILD_RELEASE</c> (B6).
        /// </summary>
        public void OnDevShortcut(string action)
        {
#if !SS_BUILD_RELEASE
            if (_session == null || _session.SliceComplete) return;
            switch (action)
            {
                case "save_run":
                    _session.RequestSave();
                    break;
                case "quicksave_run":
                    _session.RequestQuicksave();
                    break;
                case "load_run":
                    AfterLoad(_session.RequestLoad());
                    break;
            }
#endif
        }

        public void Dispose()
        {
            Devices?.Dispose();
            Devices = null;
            if (_session != null) _session.PlayableSliceCompleted -= OnSliceCompleted;
            if (_host != null) _host.PlayerDamaged -= OnPlayerDamaged;
            if (Router == null) return;
            Router.PanelToggleRequested -= OnPanelToggle;
#if !SS_BUILD_RELEASE
            Router.DevShortcutRequested -= OnDevShortcut;
#endif
            Router.Disable();
            Router = null;
        }
    }
}
