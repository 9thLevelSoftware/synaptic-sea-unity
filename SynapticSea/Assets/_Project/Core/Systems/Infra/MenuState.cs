// Ported from scripts/systems/menu_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure menu-stack state machine (REQ-UI-001 / ADR-0033). The catalog (<c>data/ui/menu_definitions.json</c>)
    /// is the only source of truth for menu and item ids; unknown ids warn and no-op.
    /// </summary>
    public class MenuState : IStatusLineProvider
    {
        /// <summary><c>signal menu_changed(new_menu_id, previous_menu_id)</c>.</summary>
        public event Action<string, string> MenuChanged;
        /// <summary><c>signal focus_changed(new_index)</c>.</summary>
        public event Action<long> FocusChanged;
        /// <summary><c>signal enabled_changed(item_id, enabled)</c>.</summary>
        public event Action<string, bool> EnabledChanged;

        public const string SchemaVersion = "menu-state-1";
        public const string SaveKey = "menu_state";

        GdDict _catalog = new GdDict();
        readonly GdArray _menuIds = new GdArray();            // ordered menu ids
        readonly GdDict _itemsByMenu = new GdDict();          // menu_id -> Array of item dicts (in order)
        string _currentMenu = "";                             // "" = in-play
        GdArray _menuHistory = new GdArray();                 // stack of previously-open menu ids
        long _focusIndex = 0;                                 // focused item index in current menu
        readonly GdDict _enabledOverrides = new GdDict();     // menu_id|item_id -> bool
        bool _closedInPlay = true;                            // whether the in-play layer is closed

        public bool Configure(GdDict catalog)
        {
            if (!MenuStateSchema.ValidateCatalog(catalog)) return false;
            _catalog = catalog.DeepCopy();
            _menuIds.Clear();
            _itemsByMenu.Clear();
            foreach (var menu in (GdArray)_catalog.Get("menus", new GdArray()))
            {
                var menuDict = (GdDict)menu;
                string menuId = V.Str(menuDict.Get("id", ""));
                _menuIds.Add(menuId);
                _itemsByMenu[menuId] = ((GdArray)menuDict.Get("items", new GdArray())).DeepCopy();
            }
            _currentMenu = "";
            _menuHistory.Clear();
            _focusIndex = 0;
            _enabledOverrides.Clear();
            _closedInPlay = true;
            return true;
        }

        /// <summary>True when no menu is open (in-play layer is the active surface).</summary>
        public bool IsInPlay() => _currentMenu.Length == 0;

        public bool IsOpen(string menuId) => _currentMenu == menuId;

        public string GetCurrentMenu() => _currentMenu;

        public GdArray GetMenuHistory() => _menuHistory.ShallowCopy();

        public long GetFocusIndex() => _focusIndex;

        public int GetItemCount()
        {
            if (_currentMenu.Length == 0) return 0;
            return ItemsOf(_currentMenu).Count;
        }

        public int GetMenuCount() => _menuIds.Count;

        public bool HasMenu(string menuId) => _menuIds.Contains(menuId);

        public bool HasItem(string menuId, string itemId)
        {
            if (!HasMenu(menuId)) return false;
            foreach (var item in ItemsOf(menuId))
            {
                if (item is GdDict d && V.Str(d.Get("id", "")) == itemId) return true;
            }
            return false;
        }

        public GdArray GetItems(string menuId)
        {
            if (!HasMenu(menuId)) return new GdArray();
            return ItemsOf(menuId).DeepCopy();
        }

        public GdDict GetFocusedItem()
        {
            if (_currentMenu.Length == 0) return new GdDict();
            GdArray items = ItemsOf(_currentMenu);
            if (_focusIndex < 0 || _focusIndex >= items.Count) return new GdDict();
            object focused = items[(int)_focusIndex];
            if (!(focused is GdDict focusedDict)) return new GdDict();
            return focusedDict.DeepCopy();
        }

        /// <summary>AND of the catalog default and the per-item override map.</summary>
        public bool IsItemEnabled(string menuId, string itemId)
        {
            GdDict item = FindItem(menuId, itemId);
            if (item.IsEmpty) return false;
            bool defaultEnabled = V.Bool(item.Get("enabled", true));
            string key = menuId + "|" + itemId;
            if (_enabledOverrides.Has(key)) return V.Bool(_enabledOverrides[key]) && defaultEnabled;
            return defaultEnabled;
        }

        public void SetItemEnabled(string menuId, string itemId, bool enabled)
        {
            if (!HasItem(menuId, itemId))
            {
                CoreServices.Log.Warning("MenuState: set_item_enabled unknown item '" + itemId + "' in menu '" + menuId + "'");
                return;
            }
            string key = menuId + "|" + itemId;
            _enabledOverrides[key] = enabled;
            EnabledChanged?.Invoke(itemId, enabled);
        }

        public bool OpenMenu(string menuId)
        {
            if (!HasMenu(menuId))
            {
                CoreServices.Log.Warning("MenuState: open_menu unknown menu '" + menuId + "'");
                return false;
            }
            if (_currentMenu == menuId) return true;
            string previous = _currentMenu;
            if (_currentMenu.Length != 0)
                _menuHistory.Add(_currentMenu);
            else
                _closedInPlay = false;
            _currentMenu = menuId;
            _focusIndex = 0;
            MenuChanged?.Invoke(_currentMenu, previous);
            FocusChanged?.Invoke(_focusIndex);
            return true;
        }

        public bool CloseTop()
        {
            if (_currentMenu.Length == 0) return false;
            string previous = _currentMenu;
            if (_menuHistory.IsEmpty)
            {
                _currentMenu = "";
                _closedInPlay = true;
            }
            else
            {
                _currentMenu = V.Str(_menuHistory.PopBack());
            }
            _focusIndex = 0;
            MenuChanged?.Invoke(_currentMenu, previous);
            FocusChanged?.Invoke(_focusIndex);
            return true;
        }

        public bool CloseAll()
        {
            if (_currentMenu.Length == 0 && _menuHistory.IsEmpty) return false;
            string previous = _currentMenu;
            _currentMenu = "";
            _menuHistory.Clear();
            _closedInPlay = true;
            _focusIndex = 0;
            MenuChanged?.Invoke(_currentMenu, previous);
            FocusChanged?.Invoke(_focusIndex);
            return true;
        }

        /// <summary>Single-column list: dy drives focus; out-of-range moves clamp.</summary>
        public long Navigate(long dx, long dy)
        {
            if (_currentMenu.Length == 0) return 0;
            long itemsCount = ItemsOf(_currentMenu).Count;
            if (itemsCount == 0) return 0;
            long newIndex = _focusIndex + dy;
            if (newIndex < 0)
                newIndex = 0;
            else if (newIndex >= itemsCount)
                newIndex = itemsCount - 1;
            if (newIndex == _focusIndex) return _focusIndex;
            _focusIndex = newIndex;
            FocusChanged?.Invoke(_focusIndex);
            return _focusIndex;
        }

        public long SetFocusIndex(long index)
        {
            if (_currentMenu.Length == 0) return 0;
            long itemsCount = ItemsOf(_currentMenu).Count;
            if (itemsCount == 0) return 0;
            long newIndex = GdMath.Clampi(index, 0, itemsCount - 1);
            if (newIndex == _focusIndex) return _focusIndex;
            _focusIndex = newIndex;
            FocusChanged?.Invoke(_focusIndex);
            return _focusIndex;
        }

        /// <summary>Focused item id, or "" when no menu is open or the item is disabled.</summary>
        public string Confirm()
        {
            GdDict focused = GetFocusedItem();
            if (focused.IsEmpty) return "";
            string itemId = V.Str(focused.Get("id", ""));
            if (!IsItemEnabled(_currentMenu, itemId)) return "";
            return itemId;
        }

        public bool Cancel() => CloseTop();

        public GdDict GetSummary()
        {
            var history = new GdArray();
            foreach (var menuId in _menuHistory) history.Add(V.Str(menuId));
            var overrides = new GdDict();
            foreach (var key in _enabledOverrides.Keys) overrides[V.Str(key)] = V.Bool(_enabledOverrides[key]);
            return new GdDict
            {
                { "schema", SchemaVersion },
                { "current_menu", _currentMenu },
                { "menu_history", history },
                { "focus_index", _focusIndex },
                { "enabled_overrides", overrides },
                { "closed_in_play", _closedInPlay },
            };
        }

        /// <summary>
        /// Restores a <see cref="GetSummary"/> dict. An unknown <c>current_menu</c> warns and resets to in-play.
        /// </summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null) return false;
            if (V.Str(summary.Get("schema", "")) != SchemaVersion) return false;
            string newMenu = V.Str(summary.Get("current_menu", ""));
            if (newMenu.Length != 0 && !HasMenu(newMenu))
            {
                CoreServices.Log.Warning("MenuState: apply_summary unknown current_menu '" + newMenu + "'; reset to in-play");
                newMenu = "";
            }
            object historyVariant = summary.Get("menu_history", new GdArray());
            var newHistory = new GdArray();
            if (historyVariant is GdArray historyArr)
            {
                foreach (var entry in historyArr)
                {
                    string entryId = V.Str(entry);
                    if (HasMenu(entryId)) newHistory.Add(entryId);
                }
            }
            _currentMenu = newMenu;
            _menuHistory = newHistory;
            _focusIndex = V.I64(summary.Get("focus_index", 0L));
            _closedInPlay = V.Bool(summary.Get("closed_in_play", _currentMenu.Length == 0));
            // Clamp focus index to current menu's item count.
            long itemsCount = ItemsOf(_currentMenu).Count;
            if (itemsCount == 0)
                _focusIndex = 0;
            else
                _focusIndex = GdMath.Clampi(_focusIndex, 0, itemsCount - 1);
            _enabledOverrides.Clear();
            object overridesVariant = summary.Get("enabled_overrides", new GdDict());
            if (overridesVariant is GdDict overridesDict)
            {
                foreach (var key in overridesDict.Keys)
                {
                    string keyStr = V.Str(key);
                    int sepIdx = keyStr.IndexOf('|');
                    if (sepIdx < 0) continue;
                    string menuId = keyStr.Substring(0, sepIdx);
                    string itemId = keyStr.Substring(sepIdx + 1);
                    if (HasItem(menuId, itemId)) _enabledOverrides[keyStr] = V.Bool(overridesDict[key]);
                }
            }
            MenuChanged?.Invoke(_currentMenu, "");
            FocusChanged?.Invoke(_focusIndex);
            return true;
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add(InfraCompat.Fmt("MenuState: current={0} history={1} focus={2} items={3}",
                _currentMenu.Length != 0 ? _currentMenu : "<in-play>",
                _menuHistory.Count,
                _focusIndex,
                GetItemCount()));
            lines.Add(InfraCompat.Fmt("  catalog_menus={0} overrides={1}", _menuIds.Count, _enabledOverrides.Count));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        GdArray ItemsOf(string menuId) => _itemsByMenu.Get(menuId, null) as GdArray ?? new GdArray();

        GdDict FindItem(string menuId, string itemId)
        {
            if (!HasMenu(menuId)) return new GdDict();
            foreach (var item in ItemsOf(menuId))
            {
                if (item is GdDict d && V.Str(d.Get("id", "")) == itemId) return d.DeepCopy();
            }
            return new GdDict();
        }
    }
}
