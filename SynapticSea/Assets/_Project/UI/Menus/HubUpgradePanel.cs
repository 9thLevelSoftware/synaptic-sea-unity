// Ported from scripts/ui/hub_upgrade_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// REQ-PM-007/010 hub upgrades (records screen, PAUSED). Every upgrade with cost, prerequisites, owned/affordable
    /// state from <see cref="HubUpgradeState"/> + <see cref="MetaProgressionState"/>. Purchase is the coordinator's
    /// (HubUpgradeState.Purchase + save, demo gate); a denial shows the model's can_purchase reason.
    /// </summary>
    public sealed class HubUpgradePanel : SurfacePanel
    {
        HubUpgradeState _catalog;
        MetaProgressionState _meta;
        int _selectedIndex;
        readonly Label _summary;
        readonly SelectableList _list;

        public event Action ConfirmRequested;
        public event Action BackRequested;

        public HubUpgradePanel() : base("HUB UPGRADES", SurfaceTime.Paused)
        {
            _summary = UiFactory.Text("", UiClasses.LabelSecondary);
            Body.Add(_summary);
            _list = new SelectableList("upgrade", "Hub Upgrades: (catalog uninitialized)");
            Body.Add(_list);
            var tools = UiFactory.Box(UiClasses.Toolbar);
            tools.Add(UiFactory.Button("Purchase", () => ConfirmRequested?.Invoke(), "act:purchase"));
            Body.Add(tools);
            _list.SelectionRequested += i =>
            {
                _selectedIndex = i;
                Render();
            };
            _list.ActivateRequested += i =>
            {
                _selectedIndex = i;
                ConfirmRequested?.Invoke();
            };
        }

        public override string SurfaceId => "hub_upgrades";

        public void SetCatalog(HubUpgradeState catalog) => _catalog = catalog;
        public void SetMetaState(MetaProgressionState meta) => _meta = meta;
        public HubUpgradeState GetCatalogPanel() => _catalog;
        public MetaProgressionState GetMetaStatePanel() => _meta;

        public int GetUpgradeCount() => _catalog == null ? 0 : _catalog.GetUpgradeCount();

        public int GetOwnedCount()
        {
            if (_catalog == null || _meta == null) return 0;
            int n = 0;
            foreach (object uid in _catalog.GetUpgradeIds())
            {
                if (_meta.IsHubUpgradeUnlocked(V.Str(uid))) n++;
            }
            return n;
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (_catalog == null)
            {
                lines.Add("Hub Upgrades: (catalog uninitialized)");
                return lines;
            }
            long currency = _meta != null ? _meta.GetMetaCurrency() : 0;
            lines.Add("Hub Upgrades: " + GetOwnedCount() + " / " + _catalog.GetUpgradeCount() + " owned  Currency: " + GdString.FormatInt(currency));
            int idx = 0;
            foreach (object e in _catalog.GetUpgradeEntries(_meta))
            {
                var entry = (GdDict)e;
                string uid = V.Str(entry.Get("upgrade_id", ""));
                string display = V.Str(entry.Get("display_name", uid));
                long cost = entry.GetInt("cost", 0);
                GdArray prereqs = entry.GetArrayOrEmpty("requires");
                bool owned = entry.GetBool("owned", false);
                bool affordable = entry.GetBool("affordable", false);
                string marker = owned ? "[X]" : affordable ? "[$]" : "[ ]";
                string cursor = idx == _selectedIndex ? ">" : " ";
                string prereqStr = prereqs.IsEmpty ? "" : "  req=" + string.Join(",", GdString.ToStringList(prereqs));
                lines.Add(cursor + marker + " " + display + " cost=" + GdString.FormatInt(cost) + prereqStr);
                idx++;
            }
            return lines;
        }

        public void MoveSelection(int direction)
        {
            int n = _catalog != null ? _catalog.GetUpgradeEntries(_meta).Count : 0;
            if (n <= 0)
            {
                _selectedIndex = 0;
                return;
            }
            _selectedIndex = (int)GdMath.Clampi(_selectedIndex + direction, 0, n - 1);
        }

        public string GetSelectedId()
        {
            if (_catalog == null) return "";
            GdArray entries = _catalog.GetUpgradeEntries(_meta);
            if (_selectedIndex < 0 || _selectedIndex >= entries.Count) return "";
            return V.Str(((GdDict)entries[_selectedIndex]).Get("upgrade_id", ""));
        }

        public void ShowResult(GdDict result)
        {
            string uid = V.Str(result.Get("detail", ""));
            if (result.GetBool("ok", false))
            {
                StatusText.Set("Purchased " + uid, Severity.Success);
                return;
            }
            string reason = uid == "demo_blocked" ? "not available in the demo" : "purchase failed";
            if (_catalog != null && uid.Length != 0 && uid != "demo_blocked")
                reason = V.Str(_catalog.CanPurchase(uid, _meta).Get("reason", reason));
            StatusText.Set((uid == "demo_blocked" ? "" : uid + " — ") + reason, Severity.Caution);
        }

        public SelectableList List => _list;
        public string SummaryText => _summary.text;

        public void Render()
        {
            List<string> godot = GetStatusLines();
            _summary.text = godot[0];
            var items = new List<SelectableList.Item>();
            if (_catalog != null)
            {
                foreach (object e in _catalog.GetUpgradeEntries(_meta))
                {
                    var entry = (GdDict)e;
                    string uid = V.Str(entry.Get("upgrade_id", ""));
                    bool owned = entry.GetBool("owned", false);
                    bool affordable = entry.GetBool("affordable", false);
                    GdArray prereqs = entry.GetArrayOrEmpty("requires");
                    string state = owned ? "✓ Owned" : affordable ? "Affordable" : "▲ Cannot afford";
                    items.Add(new SelectableList.Item
                    {
                        Id = uid,
                        Chip = GdString.FormatInt(entry.GetInt("cost", 0)),
                        Text = V.Str(entry.Get("display_name", uid)),
                        Detail = state + (prereqs.IsEmpty ? "" : " · requires " + string.Join(", ", GdString.ToStringList(prereqs)))
                            + (V.Str(entry.Get("description", "")).Length != 0 ? " · " + V.Str(entry.Get("description", "")) : ""),
                        Severity = owned ? Severity.Success : Severity.None,
                        Muted = !owned && !affordable,
                    });
                }
            }
            _list.SetItems(items, _selectedIndex);
        }

        protected override bool OnCommand(UiCommand command)
        {
            switch (command)
            {
                case UiCommand.Up:
                    MoveSelection(-1);
                    Render();
                    return true;
                case UiCommand.Down:
                    MoveSelection(1);
                    Render();
                    return true;
                case UiCommand.Accept:
                    ConfirmRequested?.Invoke();
                    return true;
            }
            return false;
        }

        protected override void RequestClose() => BackRequested?.Invoke();

        protected override VisualElement InitialFocusElement() => _list.Count > 0 ? _list.RowAt(Math.Max(0, _list.SelectedIndex)) : CloseButton;
    }
}
