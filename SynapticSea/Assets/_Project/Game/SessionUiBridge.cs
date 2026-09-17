// The UI half of scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _build_hud_layer, the HUD/tracker pushes, the
// panel open/close helpers (_open_inventory_self, toggle_wounds_panel_from_input, open_*_for_validation) and _input.
using System;
using System.Collections.Generic;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Input;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace SynapticSea.Game
{
    /// <summary>
    /// Wires a booted <see cref="RunSession"/> to the UI Toolkit HUD and menus (docs/ui-port-notes.md "Session wiring"):
    /// builds the <see cref="MenuCoordinator"/> from the session's models, mounts it and the <see cref="HudRoot"/>, routes
    /// input through <see cref="UiInputRouter"/>, opens the LIVE inspection panels on the Panels-map toggles and on the
    /// session's panel requests, forwards save/load/quit/settings, and pushes the session's HUD events.
    /// </summary>
    public sealed class SessionUiBridge : IRunUiState
    {
        public readonly HudRoot Hud;
        public readonly UIDocument MenuDocument;
        public readonly SynapticSeaInput Input;

        public MenuCoordinator Coordinator { get; private set; }
        public UiInputRouter Router { get; private set; }
        public AccessibilitySettings Accessibility { get; } = new AccessibilitySettings();
        public IUiAudio Audio { get; private set; }

        public readonly InventoryPanel Inventory = new InventoryPanel();
        public readonly WoundsPanel Wounds = new WoundsPanel();
        public readonly ScannerPanel Scanner = new ScannerPanel();
        public readonly ShipModificationPanel ShipMod = new ShipModificationPanel();
        public readonly ChartPanel Chart = new ChartPanel();
        public readonly RecipePickerPanel RecipePicker = new RecipePickerPanel();

        /// <summary>The last HUD-feedback line the bridge raised for a denied toggle (e.g. "No web chart").</summary>
        public string LastDenyLine { get; private set; } = "";

        RunSession _session;
        RunSessionHost _host;
        string _focusPrompt = "";
        string _objectivePrompt = "";

        // Events the boot raised before the coordinator existed; replayed once it does.
        bool? _pendingLoadAvailable;
        GdArray _pendingInventoryItems;
        (GdArray labels, long selected)? _pendingHotbar;
        GdDict _pendingTooltip;

        public SessionUiBridge(HudRoot hud, UIDocument menuDocument, SynapticSeaInput input)
        {
            Hud = hud;
            MenuDocument = menuDocument;
            Input = input;
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
            e.TutorialShown += (id, title, body) => Coordinator?.TutorialBanner.ShowTutorial(title, body);
            e.PanelRequested += OnPanelRequested;
        }

        /// <summary>Builds the coordinator from the booted session (Godot bind_meta_screens order) and mounts the UI.</summary>
        public void BuildCoordinator(RunSession session, RunSessionHost host, AudioManager manager)
        {
            _session = session;
            _host = host;
            Audio = new SessionUiAudio(session, manager);
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
                () => Gamepad.all.Count);
            Coordinator.ConfigureFromData(Accessibility);
            MenuDocument.rootVisualElement.Add(Coordinator.Root);
            Hud.Mount(Coordinator);

            if (_pendingLoadAvailable.HasValue) Coordinator.SetLoadAvailable(_pendingLoadAvailable.Value);
            else Coordinator.SetLoadAvailable(session.IsLoadAvailable());
            if (_pendingInventoryItems != null) Coordinator.SetInventoryItems(ToStrings(_pendingInventoryItems));
            if (_pendingHotbar.HasValue) Coordinator.SetHotbarSlots(ToStrings(_pendingHotbar.Value.labels), (int)_pendingHotbar.Value.selected);
            if (_pendingTooltip != null) Coordinator.SetTooltipQuery(_pendingTooltip);

            Coordinator.SaveRequested += () => session.RequestSave();
            Coordinator.LoadRequested += () => session.RequestLoad();
            Coordinator.WorldLoadRequested += () => session.RequestLoad();
            Coordinator.QuitRequested += session.QuitToTitle;
            Coordinator.SaveAndExitRequested += session.SaveAndExit;
            Coordinator.SettingsChanged += summary => session.ApplyUiSettingsSummary(summary);
            Coordinator.SlotSnapshotLoaded += (slotId, snapshot) => session.ApplyManualSlot(snapshot);

            Inventory.SetAudioManager(Audio);
            Inventory.SetTooltipQueryPush(Coordinator.SetTooltipQuery);
            Inventory.PanelClosed += () => OnInspectionClosed(Inventory);
            Inventory.TransferCompleted += session.OnInventoryTransferCompleted;
            Inventory.UseRequested += (itemId, useAll) => session.UseConsumableItem(itemId, useAll);
            Wounds.SetAudioManager(Audio);
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
            Router.DevShortcutRequested += OnDevShortcut;
            Router.Enable();

            host.FocusPromptChanged += prompt =>
            {
                _focusPrompt = prompt ?? "";
                RefreshPrompt();
            };
            host.SimulationPaused = () => Coordinator.Stack.SimulationPaused;
            host.MotionReduce = Accessibility.IsMotionReduce;
        }

        /// <summary>Per-frame UI work (before the session tick): input routing and the vitals cluster.</summary>
        public void Tick()
        {
            Router?.Tick();
            if (_session?.VitalsModel != null && Hud.Vitals != null) Hud.Vitals.Refresh(_session.VitalsModel);
        }

        void RefreshPrompt() => Hud.SetContextPrompt(_focusPrompt.Length > 0 ? _focusPrompt : _objectivePrompt);

        static List<string> ToStrings(GdArray values)
        {
            var output = new List<string>();
            if (values != null)
                foreach (object v in values) output.Add(V.Str(v));
            return output;
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
                        Wounds.Bind(_session.WoundState);
                        Wounds.Open();
                        if (Wounds.IsOpen()) Show(Wounds);
                    }
                    break;
                case "toggle_ship_mod":
                    if (ShipMod.IsOpen())
                    {
                        ShipMod.Close();
                    }
                    else if (_session.ShipModificationState != null)
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
                        Audio?.PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                        LastDenyLine = "No web chart";
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

        /// <summary>F5 save / F6 quicksave / F9 load, only in play (the router already checked the stack).</summary>
        public void OnDevShortcut(string action)
        {
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
                    _session.RequestLoad();
                    break;
            }
        }

        public void Dispose()
        {
            Router?.Disable();
        }
    }
}
