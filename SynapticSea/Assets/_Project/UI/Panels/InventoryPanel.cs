// Ported from scripts/ui/inventory_panel.gd, scripts/ui/inventory_row.gd, scripts/ui/inventory_drop_zone.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Inventory / equipment / transfer inspection surface (LIVE). A thin view over the System 6 models: every decision
    /// delegates to <see cref="InventorySelectionModel"/> + <see cref="CargoTransfer"/> exactly as the Godot panel did,
    /// and the Godot logical API (select_row, transfer_selected, equip_from_container, zone_drop, ...) is kept so the
    /// same code paths are driven by mouse, keyboard, gamepad and tests.
    ///
    /// Layout (spec "Inventory/equipment/loot"): list-based two-pane transfer (YOU | container) plus a detail area and
    /// the equipment slots; the row's context actions are an always-visible action row, so every drag/right-click action
    /// has a keyboard/gamepad alternative (Transfer, Transfer all, Split with a quantity stepper, Equip, Unequip, Use).
    /// Mouse: click / Ctrl-click / Shift-click select, drag rows onto the other pane or a slot, right-click focuses the
    /// row's actions. Keyboard/gamepad: Up/Down move, Left/Right switch pane, Submit runs the primary action.
    /// </summary>
    public sealed class InventoryPanel : SurfacePanel
    {
        public const string PaneSelf = "self";
        public const string PaneContainer = "container";

        /// <summary>Emitted on every close so the coordinator restores control.</summary>
        public event Action PanelClosed;
        /// <summary>Emitted after any state mutation so the coordinator recomputes (before the re-render).</summary>
        public event Action TransferCompleted;
        public event Action<string, bool> UseRequested;

        string _mode = "closed";
        InventoryState _playerInv;
        EquipmentState _equip;
        CargoTransfer.ICargoHold _container;
        string _containerLabel = "";
        Action<GdDict> _tooltipQueryPush;
        IUiAudio _audio;

        readonly InventorySelectionModel _selSelf = new InventorySelectionModel();
        readonly InventorySelectionModel _selContainer = new InventorySelectionModel();
        readonly Dictionary<string, int> _cursor = new Dictionary<string, int> { { PaneSelf, 0 }, { PaneContainer, 0 } };
        string _activePane = PaneSelf;
        int _slotCursor;
        readonly GdDict _defs;

        // view
        readonly Label _weightLine;
        readonly VisualElement _columns;
        readonly VisualElement _selfPane;
        readonly VisualElement _containerPane;
        readonly Label _selfTitle;
        readonly Label _containerTitle;
        readonly SelectableList _selfList;
        readonly SelectableList _containerList;
        readonly VisualElement _detail;
        readonly Label _detailName;
        readonly Label _detailLines;
        readonly SelectableList _slotList;
        readonly VisualElement _actions;
        readonly VisualElement _splitRow;
        readonly Label _splitLabel;
        readonly Button _depositAll;
        readonly Dictionary<VisualElement, string> _zones = new Dictionary<VisualElement, string>();
        readonly Label _dragGhost;

        // split picker (inline replacement for the Godot AcceptDialog + SpinBox)
        string _splitPane = "";
        string _splitItem = "";
        long _splitQty;
        long _splitMax;

        // pointer drag state
        string _dragPane = "";
        int _dragIndex = -1;
        Vector2 _dragStart;
        GdDict _dragPayload;

        public InventoryPanel() : base("INVENTORY + GEAR", SurfaceTime.Live)
        {
            AddToClassList("ss-inventory");
            _defs = ItemDefs.LoadDefinitions() ?? new GdDict();

            _weightLine = UiFactory.Text("", UiClasses.LabelMono, "ss-inventory__weight");
            Body.Add(_weightLine);

            _columns = UiFactory.Box(UiClasses.Columns);
            _selfPane = UiFactory.Box(UiClasses.Pane);
            _selfTitle = UiFactory.Text("Carrying", UiClasses.SectionTitle);
            _selfList = new SelectableList("inv-self", "Nothing carried.");
            _selfPane.Add(_selfTitle);
            _selfPane.Add(_selfList);
            _containerPane = UiFactory.Box(UiClasses.Pane);
            _containerTitle = UiFactory.Text("", UiClasses.SectionTitle);
            _containerList = new SelectableList("inv-container", "Container is empty.");
            _containerPane.Add(_containerTitle);
            _containerPane.Add(_containerList);
            _detail = UiFactory.Box(UiClasses.Detail, UiClasses.Pane);
            _detailName = UiFactory.Text("", UiClasses.SectionTitle);
            _detailLines = UiFactory.Text("", UiClasses.LabelSecondary);
            _detail.Add(_detailName);
            _detail.Add(_detailLines);
            _columns.Add(_selfPane);
            _columns.Add(_containerPane);
            _columns.Add(_detail);
            Body.Add(_columns);

            _splitRow = UiFactory.Box(UiClasses.Toolbar, "ss-inventory__split");
            _splitLabel = UiFactory.Text("", UiClasses.LabelMono);
            _splitRow.Add(UiFactory.Button("−", () => StepSplit(-1), "split:minus"));
            _splitRow.Add(_splitLabel);
            _splitRow.Add(UiFactory.Button("+", () => StepSplit(1), "split:plus"));
            _splitRow.Add(UiFactory.Button("Move", () => ConfirmSplit(), "split:confirm"));
            _splitRow.Add(UiFactory.Button("Cancel", () => CancelSplit(), "split:cancel"));
            UiFactory.SetShown(_splitRow, false);
            Body.Add(_splitRow);

            _actions = UiFactory.Box(UiClasses.Toolbar, "ss-inventory__actions");
            Body.Add(_actions);

            var slotSection = UiFactory.Box(UiClasses.Section);
            slotSection.Add(UiFactory.Text("Equipment", UiClasses.SectionTitle));
            _slotList = new SelectableList("inv-slot", "No equipment slots.");
            slotSection.Add(_slotList);
            Body.Add(slotSection);

            _depositAll = UiFactory.Button("Deposit All", () => DepositAllToContainer(), "action:deposit_all");
            ActionBar.Insert(0, _depositAll);

            _dragGhost = UiFactory.Text("", "ss-drag-ghost");
            _dragGhost.pickingMode = PickingMode.Ignore;
            UiFactory.SetShown(_dragGhost, false);
            Add(_dragGhost);

            WireList(_selfList, PaneSelf);
            WireList(_containerList, PaneContainer);
            _slotList.SelectionRequested += i =>
            {
                _slotCursor = i;
                Render();
            };
            _slotList.ActivateRequested += i => ActivateSlot(i);
            _slotList.RowPointerDown += (i, e) =>
            {
                if (e.button == 1) ActivateSlot(i);
            };
            _slotList.MoveOut = dir => false;

            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            Render();
        }

        public override string SurfaceId => "inventory";

        // --- lifecycle ---

        public void OpenSelf(InventoryState inv, EquipmentState equip)
        {
            _playerInv = inv ?? throw new ArgumentNullException(nameof(inv), "InventoryState dependency must not be null");
            _equip = equip ?? throw new ArgumentNullException(nameof(equip), "EquipmentState dependency must not be null");
            _container = null;
            _containerLabel = "";
            _mode = "self";
            _activePane = PaneSelf;
            SetViewVisible(true);
            RebuildModels();
            Render();
        }

        public void OpenTransfer(InventoryState playerInv, CargoTransfer.ICargoHold containerHold, string containerLabel, EquipmentState equip)
        {
            _playerInv = playerInv ?? throw new ArgumentNullException(nameof(playerInv), "Player inventory dependency must not be null");
            _container = containerHold ?? throw new ArgumentNullException(nameof(containerHold), "Container hold dependency must not be null");
            _containerLabel = containerLabel ?? "";
            _equip = equip;
            _mode = "transfer";
            _activePane = PaneSelf;
            SetViewVisible(true);
            RebuildModels();
            Render();
        }

        public void Close()
        {
            _mode = "closed";
            CancelSplit();
            SetViewVisible(false);
            PanelClosed?.Invoke();
            PushTooltipClear();
        }

        protected override void RequestClose() => Close();

        public bool IsOpen() => _mode != "closed";
        public string GetMode() => _mode;

        /// <summary>Domain 10 (ADR-0045) tooltip trigger 2 seam, injected at bind time.</summary>
        public void SetTooltipQueryPush(Action<GdDict> push) => _tooltipQueryPush = push;

        public void SetAudioManager(IUiAudio audio) => _audio = audio;

        // --- list/pane access ---

        List<string> IdsForPane(string pane) => pane == PaneContainer ? SortedIds(_container) : SortedIds(_playerInv);

        static GdDict ItemsOf(object inv)
        {
            if (inv is CargoTransfer.ICargoPlayer player) return player.Items;
            if (inv is ShipInventory hold) return hold.Items;
            return null;
        }

        static List<string> SortedIds(object inv)
        {
            var output = new List<string>();
            GdDict items = ItemsOf(inv);
            if (items == null) return output;
            var ids = new GdArray(items.Keys);
            GdSort.Sort(ids);
            foreach (object v in ids) output.Add(V.Str(v));
            return output;
        }

        public List<string> GetPaneIds(string pane) => IdsForPane(pane);

        InventorySelectionModel ModelForPane(string pane) => pane == PaneContainer ? _selContainer : _selSelf;

        void RebuildModels()
        {
            _selSelf.SetIds(GdString.ToGdArray(IdsForPane(PaneSelf)));
            _selContainer.SetIds(GdString.ToGdArray(IdsForPane(PaneContainer)));
        }

        public void SelectRow(string pane, int index, bool additive, bool rangeSel)
        {
            InventorySelectionModel m = ModelForPane(pane);
            if (rangeSel) m.SelectRangeTo(index);
            else if (additive) m.Toggle(index);
            else m.SelectSingle(index);
            _cursor[NormPane(pane)] = index;
            _activePane = NormPane(pane);
            Render();
            PushTooltipForSelection(pane);
        }

        static string NormPane(string pane) => pane == PaneContainer ? PaneContainer : PaneSelf;

        void PushTooltipForSelection(string pane)
        {
            if (_tooltipQueryPush == null) return;
            GdArray selected = ModelForPane(pane).GetSelectedIds();
            if (selected.Count == 1)
                _tooltipQueryPush(new GdDict { { "subject_kind", "item" }, { "subject_id", V.Str(selected[0]) } });
            else
                PushTooltipClear();
        }

        void PushTooltipClear() => _tooltipQueryPush?.Invoke(new GdDict { { "subject_kind", "item" }, { "subject_id", "" } });

        public GdArray GetSelectedIds(string pane) => ModelForPane(pane).GetSelectedIds();

        // --- equip / unequip ---

        public bool EquipSelected()
        {
            if (_playerInv == null || _equip == null) return false;
            GdArray sel = _selSelf.GetSelectedIds();
            if (sel.Count != 1) return false;
            if (EquipInInventory(V.Str(sel[0])))
            {
                AfterMutation();
                return true;
            }
            Deny("Cannot equip " + Name(V.Str(sel[0])));
            return false;
        }

        /// <summary>Atomic equip of an item already carried; on failure inventory and equipment are unchanged.</summary>
        bool EquipInInventory(string itemId)
        {
            if (_playerInv == null || _equip == null) return false;
            if (_playerInv.GetQuantity(itemId) <= 0 || !_equip.CanEquip(itemId)) return false;
            GdDict res = _equip.Equip(itemId);
            if (!res.GetBool("ok", false)) return false;
            string displaced = V.Str(res.Get("displaced", ""));
            if (displaced.Length != 0)
            {
                if (_playerInv.AddItem(displaced, 1) < 1)
                {
                    _equip.Equip(displaced);
                    return false;
                }
            }
            _playerInv.RemoveItem(itemId, 1);
            return true;
        }

        /// <summary>Equip-from-container (ADR-0026): one unit container → player, then equip; rolled back on failure.</summary>
        public bool EquipFromContainer(string itemId)
        {
            if (_container == null || _playerInv == null || _equip == null) return false;
            if (ItemDefs.EquipSlot(_defs, itemId).Length == 0) return false;
            if (_container.GetQuantity(itemId) <= 0) return false;
            if (CargoTransfer.MoveItem(_container, _playerInv, itemId, 1) < 1)
            {
                Deny("No room to take " + Name(itemId));
                return false;
            }
            if (EquipInInventory(itemId))
            {
                AfterMutation();
                return true;
            }
            long rolledBack = CargoTransfer.MoveItem(_playerInv, _container, itemId, 1);
            if (rolledBack < 1) CoreServices_Error("equip_from_container: rollback move failed — unit stranded in carry");
            Deny("Cannot equip " + Name(itemId));
            return false;
        }

        static void CoreServices_Error(string message) => SynapticSea.Core.Services.CoreServices.Log.Error(message);

        public bool UnequipSlot(string slotId)
        {
            if (_playerInv == null || _equip == null) return false;
            string itemId = _equip.Unequip(slotId);
            if (itemId.Length == 0) return false;
            if (_playerInv.AddItem(itemId, 1) < 1)
            {
                _equip.Equip(itemId);
                Deny("No carry room to unequip " + Name(itemId));
                return false;
            }
            AfterMutation();
            return true;
        }

        // --- encumbrance badge ---

        public string GetLoadBadge()
        {
            if (_playerInv == null) return "OK";
            double r = _playerInv.GetLoadRatio();
            if (r <= 1.0) return "OK";
            if (r <= 1.25) return "HEAVY";
            return "OVERLOADED";
        }

        double MoveSpeedMult() => _playerInv == null ? 1.0 : Encumbrance.MoveSpeedMultiplier(_playerInv.GetLoadRatio());

        // --- shared post-mutation hook ---

        void AfterMutation()
        {
            // Emit BEFORE rendering: the coordinator recomputes bonus capacity from worn gear in this handler.
            RebuildModels();
            TransferCompleted?.Invoke();
            Render();
            if (_tooltipQueryPush != null)
            {
                GdArray selfSel = _selSelf.GetSelectedIds();
                GdArray containerSel = _selContainer.GetSelectedIds();
                if (selfSel.Count == 1 && containerSel.IsEmpty) PushTooltipForSelection(PaneSelf);
                else if (containerSel.Count == 1 && selfSel.IsEmpty) PushTooltipForSelection(PaneContainer);
                else PushTooltipClear();
            }
        }

        // --- widget callbacks (rows/zones forward here, as in Godot) ---

        public void RowClicked(string pane, int index, bool additive, bool rangeSel) => SelectRow(pane, index, additive, rangeSel);

        /// <summary>The drag payload {from_pane, ids} (selecting the row first when it is not selected); null when empty.</summary>
        public GdDict RowDragPayload(string pane, int index)
        {
            InventorySelectionModel m = ModelForPane(pane);
            if (!m.IsSelected(index))
            {
                m.SelectSingle(index);
                PushTooltipForSelection(pane);
            }
            GdDict data = BuildDragPayload(pane);
            return data.GetArrayOrEmpty("ids").IsEmpty ? null : data;
        }

        /// <summary>Godot's right-click: select the row when needed, then offer its context actions (the action row).</summary>
        public List<string> RowContext(string pane, int index)
        {
            InventorySelectionModel m = ModelForPane(pane);
            if (!m.IsSelected(index))
            {
                m.SelectSingle(index);
                PushTooltipForSelection(pane);
            }
            _cursor[NormPane(pane)] = index;
            _activePane = NormPane(pane);
            Render();
            VisualElement first = _actions.Query<Button>().First();
            if (first != null) UiFocus.Focus(first);
            return ContextActionsFor(pane, index);
        }

        /// <summary>True iff <paramref name="data"/> can drop on <paramref name="target"/> (pane or "slot:&lt;id&gt;").</summary>
        public bool ZoneCanAccept(string target, GdDict data)
        {
            if (data == null) return false;
            string fromPane = V.Str(data.Get("from_pane", ""));
            if (GdString.BeginsWith(target, "slot:"))
            {
                if (fromPane != PaneSelf && fromPane != PaneContainer) return false;
                string slot = target.Substring(5);
                foreach (object id in data.GetArrayOrEmpty("ids"))
                {
                    if (_equip != null && ItemDefs.EquipSlot(_defs, V.Str(id)) == slot) return true;
                }
                return false;
            }
            return _mode == "transfer" && target != fromPane && (fromPane == PaneSelf || fromPane == PaneContainer);
        }

        public void ZoneDrop(string target, GdDict data)
        {
            if (data == null) return;
            string fromPane = V.Str(data.Get("from_pane", ""));
            if (GdString.BeginsWith(target, "slot:"))
            {
                if (fromPane != PaneSelf && fromPane != PaneContainer) return;
                string slot = target.Substring(5);
                foreach (object idV in data.GetArrayOrEmpty("ids"))
                {
                    string id = V.Str(idV);
                    if (_equip != null && ItemDefs.EquipSlot(_defs, id) == slot)
                    {
                        if (fromPane == PaneContainer)
                        {
                            EquipFromContainer(id);
                        }
                        else
                        {
                            _selSelf.SelectSingle(IdsForPane(PaneSelf).IndexOf(id));
                            PushTooltipForSelection(PaneSelf);
                            EquipSelected();
                        }
                        return;
                    }
                }
                return;
            }
            if (_mode == "transfer" && target != fromPane && (fromPane == PaneSelf || fromPane == PaneContainer))
                TransferSelected(fromPane);
        }

        /// <summary>Moves every id in <paramref name="pane"/> to the other pane (manual; includes tools).</summary>
        public long TransferAllFrom(string pane)
        {
            CargoTransfer.ICargoStore src = InvForPane(pane);
            CargoTransfer.ICargoStore dst = InvForPane(OtherPane(pane));
            if (src == null || dst == null) return 0;
            var idToQty = new GdDict();
            foreach (string id in IdsForPane(pane)) idToQty[id] = src.GetQuantity(id);
            long moved = CargoTransfer.MoveItems(src, dst, idToQty);
            if (moved > 0) AfterMutation();
            else Deny("Nothing moved");
            return moved;
        }

        public void SlotContext(string slotId)
        {
            if (_equip == null || !_equip.IsSlotOccupied(slotId)) return;
            UnequipSlot(slotId);
        }

        public long PaneQuantity(string pane, string id)
        {
            CargoTransfer.ICargoStore inv = InvForPane(pane);
            return inv != null ? inv.GetQuantity(id) : 0;
        }

        /// <summary>The Godot context-menu action ids for a row (transfer, transfer_all, split, equip, unequip, use, use_all).</summary>
        public List<string> ContextActionsFor(string pane, int index)
        {
            List<string> ids = IdsForPane(pane);
            if (index < 0 || index >= ids.Count) return new List<string>();
            return InventorySelectionModel.ContextActions(ids[index], _defs, _mode == "transfer", pane == PaneContainer, false);
        }

        /// <summary>Godot <c>_on_context_id</c>: runs one context action for a row.</summary>
        public void InvokeContextAction(string action, string pane, int index)
        {
            List<string> ids = IdsForPane(pane);
            string itemId = index >= 0 && index < ids.Count ? ids[index] : "";
            switch (action)
            {
                case "transfer":
                    TransferSelected(pane);
                    break;
                case "transfer_all":
                    TransferAllFrom(pane);
                    break;
                case "split":
                    OpenSplitPicker(pane, itemId);
                    break;
                case "equip":
                    if (pane == PaneContainer)
                    {
                        if (itemId.Length != 0) EquipFromContainer(itemId);
                    }
                    else
                    {
                        ModelForPane(pane).SelectSingle(index);
                        PushTooltipForSelection(pane);
                        EquipSelected();
                    }
                    break;
                case "use":
                case "use_all":
                    if (itemId.Length != 0) UseRequested?.Invoke(itemId, action == "use_all");
                    break;
            }
        }

        /// <summary>Split picker: quantity 1..stack, default max(1, stack/2); confirm moves that quantity.</summary>
        public void OpenSplitPicker(string pane, string itemId)
        {
            CargoTransfer.ICargoStore src = InvForPane(pane);
            if (src == null || itemId.Length == 0) return;
            long maxq = src.GetQuantity(itemId);
            if (maxq <= 0) return;
            _splitPane = pane;
            _splitItem = itemId;
            _splitMax = maxq;
            _splitQty = Math.Max(1, (long)(maxq / 2.0));
            RenderSplit();
            VisualElement confirm = UiFocus.FindByToken(_splitRow, "split:confirm");
            UiFocus.Focus(confirm);
        }

        public bool IsSplitOpen => _splitItem.Length != 0;
        public long SplitQuantity => _splitQty;

        public void StepSplit(int delta)
        {
            if (!IsSplitOpen) return;
            _splitQty = GdMath.Clampi(_splitQty + delta, 1, _splitMax);
            RenderSplit();
        }

        public long ConfirmSplit()
        {
            if (!IsSplitOpen) return 0;
            string pane = _splitPane, item = _splitItem;
            long qty = _splitQty;
            CancelSplit();
            return TransferQuantity(pane, item, qty);
        }

        public void CancelSplit()
        {
            _splitPane = "";
            _splitItem = "";
            RenderSplit();
        }

        void RenderSplit()
        {
            UiFactory.SetShown(_splitRow, IsSplitOpen);
            if (IsSplitOpen) _splitLabel.text = "Split " + Name(_splitItem) + ": " + _splitQty + " / " + _splitMax;
        }

        // --- transfer ---

        static string OtherPane(string pane) => pane == PaneSelf || pane == "you" ? PaneContainer : PaneSelf;

        CargoTransfer.ICargoStore InvForPane(string pane) => pane == PaneContainer ? (CargoTransfer.ICargoStore)_container : _playerInv;

        /// <summary>Moves every selected whole stack to the other pane. Returns the total moved.</summary>
        public long TransferSelected(string fromPane)
        {
            if (_mode != "transfer") return 0;
            CargoTransfer.ICargoStore src = InvForPane(fromPane);
            CargoTransfer.ICargoStore dst = InvForPane(OtherPane(fromPane));
            if (src == null || dst == null) return 0;
            var idToQty = new GdDict();
            foreach (object id in ModelForPane(fromPane).GetSelectedIds()) idToQty[V.Str(id)] = src.GetQuantity(V.Str(id));
            long moved = CargoTransfer.MoveItems(src, dst, idToQty);
            if (moved > 0)
            {
                AfterMutation();
                SetStatus("Moved " + moved, Severity.Success);
            }
            else
            {
                Deny(idToQty.Count == 0 ? "Nothing selected to transfer" : "Nothing moved — destination full");
            }
            return moved;
        }

        /// <summary>Split: moves exactly <paramref name="qty"/> of one id to the other pane.</summary>
        public long TransferQuantity(string fromPane, string itemId, long qty)
        {
            if (_mode != "transfer") return 0;
            CargoTransfer.ICargoStore src = InvForPane(fromPane);
            CargoTransfer.ICargoStore dst = InvForPane(OtherPane(fromPane));
            if (src == null || dst == null) return 0;
            long moved = CargoTransfer.MoveItem(src, dst, itemId, qty);
            if (moved > 0)
            {
                AfterMutation();
                SetStatus("Moved " + moved + " " + Name(itemId), Severity.Success);
            }
            else
            {
                Deny("Nothing moved — destination full");
            }
            return moved;
        }

        /// <summary>"Deposit All": bulk part+supply (tools excluded) into the container.</summary>
        public long DepositAllToContainer()
        {
            if (_mode != "transfer" || _playerInv == null || _container == null) return 0;
            long moved = CargoTransfer.DepositAll(_playerInv, _container).GetInt("total_moved", 0);
            if (moved > 0)
            {
                AfterMutation();
                SetStatus("Deposited " + moved, Severity.Success);
            }
            else
            {
                Deny("Nothing haulable to deposit");
            }
            return moved;
        }

        public GdDict BuildDragPayload(string pane) =>
            new GdDict { { "from_pane", pane }, { "ids", ModelForPane(pane).GetSelectedIds() } };

        void Deny(string reason)
        {
            SetStatus(reason, Severity.Caution);
            _audio?.PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
        }

        void SetStatus(string text, Severity severity) => StatusText.Set(text, severity);

        public string LastStatus => StatusText.Raw;

        // --- rendering ---

        public string WeightLine()
        {
            if (_playerInv == null) return "";
            return "Wt " + GdString.FormatFixed(_playerInv.GetTotalWeight(), 1) + "/" + GdString.FormatFixed(_playerInv.GetCapacity(), 1)
                + " [" + GetLoadBadge() + "] x" + GdString.FormatFixed(MoveSpeedMult(), 2);
        }

        public string HeaderText => Title + "\n" + WeightLine();
        public SelectableList SelfList => _selfList;
        public SelectableList ContainerList => _containerList;
        public SelectableList SlotList => _slotList;
        public string ActivePane => _activePane;
        public string DetailText => _detailName.text + "\n" + _detailLines.text;
        public IEnumerable<Button> ActionButtons => _actions.Query<Button>().ToList();

        void Render()
        {
            bool open = _mode != "closed";
            SetTitle(_mode == "transfer" ? "TRANSFER  |  " + _containerLabel : "INVENTORY + GEAR");
            _weightLine.text = WeightLine();
            string badge = GetLoadBadge();
            SeverityText.Apply(_weightLine, badge == "OVERLOADED" ? Severity.Danger : badge == "HEAVY" ? Severity.Caution : Severity.None);
            if (badge != "OK") _weightLine.text = SeverityText.Symbol(badge == "OVERLOADED" ? Severity.Danger : Severity.Caution) + " " + _weightLine.text;

            bool transfer = _mode == "transfer";
            _selfTitle.text = transfer ? "YOU" : "Carrying";
            UiFactory.SetShown(_containerPane, transfer);
            UiFactory.SetShown(_depositAll, transfer);
            if (transfer && _container != null)
                _containerTitle.text = _containerLabel + "  " + GdString.FormatInt((long)ContainerWeight()) + "/" + GdString.FormatInt((long)ContainerMaxWeight());

            _zones.Clear();
            _zones[_selfPane] = PaneSelf;
            _zones[_containerPane] = PaneContainer;
            RenderPane(_selfList, PaneSelf);
            RenderPane(_containerList, PaneContainer);
            _selfPane.EnableInClassList(UiClasses.PaneActive, _activePane == PaneSelf);
            _containerPane.EnableInClassList(UiClasses.PaneActive, _activePane == PaneContainer);
            RenderSlots();
            RenderDetail();
            RenderActions();
            if (!open) UiFactory.SetShown(_splitRow, false);
        }

        double ContainerWeight() => _container is ShipInventory s ? s.GetTotalWeight() : _container is InventoryState i ? i.GetTotalWeight() : 0.0;
        double ContainerMaxWeight() => _container is ShipInventory s ? s.GetMaxWeight() : _container is InventoryState i ? i.GetCapacity() : 0.0;

        void RenderPane(SelectableList list, string pane)
        {
            List<string> ids = IdsForPane(pane);
            InventorySelectionModel model = ModelForPane(pane);
            var items = new List<SelectableList.Item>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                string category = ItemDefs.Category(_defs, id);
                string rarity = ItemDefs.Rarity(_defs, id);
                string detail = category;
                if (rarity.Length != 0 && RarityTier.Normalize(rarity) != RarityTier.DEFAULT_RARITY) detail += " · " + RarityTier.Label(rarity);
                detail += " · " + GdString.FormatFixed(ItemDefs.WeightEach(_defs, id), 1) + " each";
                items.Add(new SelectableList.Item
                {
                    Id = id,
                    Text = Name(id),
                    Chip = "×" + PaneQuantity(pane, id),
                    Detail = detail,
                    Marked = model.IsSelected(i),
                });
            }
            int cursor = _cursor[pane];
            if (ids.Count > 0) cursor = Math.Max(0, Math.Min(cursor, ids.Count - 1));
            _cursor[pane] = cursor;
            list.SetItems(items, cursor);
            for (int i = 0; i < list.RowElements.Count; i++)
            {
                RarityTier.RarityColor c = RarityTier.Color(ItemDefs.Rarity(_defs, ids[i]));
                list.RowElements[i].style.borderLeftColor = new Color(c.R, c.G, c.B, c.A);
                _zones[list.RowElements[i]] = pane;
            }
        }

        void RenderSlots()
        {
            var items = new List<SelectableList.Item>();
            foreach (object slotV in EquipmentState.SLOTS)
            {
                string slot = V.Str(slotV);
                string worn = _equip != null ? _equip.GetEquipped(slot) : "";
                items.Add(new SelectableList.Item
                {
                    Id = slot,
                    Text = slot + "  [" + (worn.Length == 0 ? "(empty)" : Name(worn)) + "]",
                    Detail = worn.Length == 0 ? "" : "Submit / right-click: Unequip",
                    Muted = worn.Length == 0,
                });
            }
            _slotList.SetItems(items, _slotCursor);
            for (int i = 0; i < _slotList.RowElements.Count; i++) _zones[_slotList.RowElements[i]] = "slot:" + items[i].Id;
        }

        void ActivateSlot(int index)
        {
            if (index < 0 || index >= EquipmentState.SLOTS.Count) return;
            string slot = V.Str(EquipmentState.SLOTS[index]);
            if (_equip == null || !_equip.IsSlotOccupied(slot))
            {
                Deny(slot + " slot is empty");
                return;
            }
            UnequipSlot(slot);
        }

        void RenderDetail()
        {
            List<string> ids = IdsForPane(_activePane);
            int cursor = _cursor[_activePane];
            if (ids.Count == 0 || cursor < 0 || cursor >= ids.Count)
            {
                _detailName.text = "No item selected";
                _detailLines.text = "";
                return;
            }
            string id = ids[cursor];
            _detailName.text = Name(id);
            var lines = new List<string>
            {
                "Quantity " + PaneQuantity(_activePane, id),
                "Category " + ItemDefs.Category(_defs, id),
                "Weight " + GdString.FormatFixed(ItemDefs.WeightEach(_defs, id), 1) + " each",
            };
            string slot = ItemDefs.EquipSlot(_defs, id);
            if (slot.Length != 0) lines.Add("Equips to " + slot);
            string rarity = ItemDefs.Rarity(_defs, id);
            if (rarity.Length != 0) lines.Add("Rarity " + RarityTier.Label(rarity));
            _detailLines.text = string.Join("\n", lines);
        }

        void RenderActions()
        {
            _actions.Clear();
            List<string> ids = IdsForPane(_activePane);
            int cursor = _cursor[_activePane];
            if (_mode == "closed" || ids.Count == 0) return;
            string pane = _activePane;
            foreach (string action in ContextActionsFor(pane, cursor))
            {
                string a = action;
                _actions.Add(UiFactory.Button(ActionLabel(a), () => InvokeContextAction(a, pane, _cursor[pane]), "act:" + a));
            }
        }

        static string ActionLabel(string action)
        {
            switch (action)
            {
                case "transfer": return "Transfer";
                case "transfer_all": return "Transfer all";
                case "split": return "Split…";
                case "equip": return "Equip";
                case "unequip": return "Unequip";
                case "use": return "Use";
                case "use_all": return "Use All";
                default: return action;
            }
        }

        string Name(string id) => ItemDefs.DisplayName(_defs, id);

        // --- input ---

        void WireList(SelectableList list, string pane)
        {
            list.SelectionRequested += i => SelectRow(pane, i, false, false);
            list.ActivateRequested += i => PrimaryAction(pane, i);
            list.RowPointerDown += (i, e) =>
            {
                if (e.button == 1)
                {
                    RowContext(pane, i);
                    return;
                }
                if (e.button != 0) return;
                RowClicked(pane, i, e.actionKey || e.ctrlKey, e.shiftKey);
                _dragPane = pane;
                _dragIndex = i;
                _dragStart = e.position;
                _dragPayload = null;
            };
            list.MoveOut = dir =>
            {
                if (_mode != "transfer") return false;
                string target = dir == NavigationMoveEvent.Direction.Right ? PaneContainer : PaneSelf;
                if (target == pane) return false;
                SwitchPane(target);
                return true;
            };
        }

        void SwitchPane(string pane)
        {
            if (_mode != "transfer" && pane == PaneContainer) return;
            _activePane = pane;
            Render();
            SelectableList list = pane == PaneContainer ? _containerList : _selfList;
            if (list.Count > 0) list.FocusRow(list.SelectedIndex);
        }

        /// <summary>Submit on a row: transfer (transfer mode), else equip, else use.</summary>
        public void PrimaryAction(string pane, int index)
        {
            List<string> ids = IdsForPane(pane);
            if (index < 0 || index >= ids.Count) return;
            if (!ModelForPane(pane).IsSelected(index)) SelectRow(pane, index, false, false);
            List<string> actions = ContextActionsFor(pane, index);
            if (_mode == "transfer" && actions.Contains("transfer")) InvokeContextAction("transfer", pane, index);
            else if (actions.Contains("equip")) InvokeContextAction("equip", pane, index);
            else if (actions.Contains("use")) InvokeContextAction("use", pane, index);
            else Deny("No action for " + Name(ids[index]));
        }

        protected override bool OnCommand(UiCommand command)
        {
            if (_mode == "closed") return false;
            SelectableList list = _activePane == PaneContainer ? _containerList : _selfList;
            switch (command)
            {
                case UiCommand.Up:
                case UiCommand.Down:
                    if (list.Count == 0) return true;
                    SelectRow(_activePane, list.StepFrom(_cursor[_activePane], command == UiCommand.Up ? -1 : 1), false, false);
                    return true;
                case UiCommand.Left:
                    SwitchPane(PaneSelf);
                    return true;
                case UiCommand.Right:
                    SwitchPane(PaneContainer);
                    return true;
                case UiCommand.Accept:
                    PrimaryAction(_activePane, _cursor[_activePane]);
                    return true;
            }
            return false;
        }

        protected override VisualElement InitialFocusElement()
        {
            if (_selfList.Count > 0) return _selfList.RowAt(Math.Max(0, _selfList.SelectedIndex));
            if (_mode == "transfer" && _containerList.Count > 0) return _containerList.RowAt(Math.Max(0, _containerList.SelectedIndex));
            return CloseButton;
        }

        // --- pointer drag & drop ---

        void OnPointerMove(PointerMoveEvent e)
        {
            if (_dragIndex < 0) return;
            if (_dragPayload == null)
            {
                if (((Vector2)e.position - _dragStart).sqrMagnitude < 36f) return;
                _dragPayload = RowDragPayload(_dragPane, _dragIndex);
                if (_dragPayload == null)
                {
                    _dragIndex = -1;
                    return;
                }
                this.CapturePointer(e.pointerId);
                _dragGhost.text = _dragPayload.GetArrayOrEmpty("ids").Count + " item(s)";
                UiFactory.SetShown(_dragGhost, true);
            }
            Vector2 local = this.WorldToLocal(e.position);
            _dragGhost.style.left = local.x + 12;
            _dragGhost.style.top = local.y + 12;
            string target = ZoneAt(e.position);
            foreach (var kv in _zones) kv.Key.EnableInClassList(UiClasses.DropTargetHot, kv.Value == target && ZoneCanAccept(target, _dragPayload));
        }

        void OnPointerUp(PointerUpEvent e)
        {
            if (_dragIndex < 0) return;
            GdDict payload = _dragPayload;
            _dragIndex = -1;
            _dragPayload = null;
            UiFactory.SetShown(_dragGhost, false);
            if (this.HasPointerCapture(e.pointerId)) this.ReleasePointer(e.pointerId);
            foreach (var kv in _zones) kv.Key.EnableInClassList(UiClasses.DropTargetHot, false);
            if (payload == null) return;
            string target = ZoneAt(e.position);
            if (target != null && ZoneCanAccept(target, payload)) ZoneDrop(target, payload);
        }

        string ZoneAt(Vector2 panelPosition)
        {
            VisualElement picked = panel?.Pick(panelPosition);
            for (VisualElement el = picked; el != null && el != this; el = el.parent)
            {
                if (_zones.TryGetValue(el, out string target)) return target;
            }
            return null;
        }
    }
}
