// Ported from scripts/ui/ship_modification_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// PKG-D9b hub ship modification (LIVE inspection): slot manifest, power budget and install/uninstall through
    /// <see cref="ShipModificationState"/>. Presentation only; the model decides (slot_occupied, power_budget,
    /// missing_item, not_found) and the panel shows the reason with caution wording + symbol. Install/Uninstall are
    /// focusable buttons; Submit on a slot uninstalls an occupied slot or installs into an empty one.
    /// </summary>
    public sealed class ShipModificationPanel : SurfacePanel
    {
        public static readonly IReadOnlyList<string> DefaultPreferredForms = new[]
        {
            "console_unit", "reactor_console", "nav_console", "pump_assembly",
            "conduit_segment", "plating_plate", "air_recycler_unit", "thruster_control", "sensor_rack",
        };

        public event Action PanelClosed;
        public event Action<string, string, string> InstallRequested;
        public event Action<string, string, string> UninstallRequested;
        public event Action<GdDict> ComponentWorkStarted;

        ShipModificationState _modState;
        GdDict _inventory = new GdDict();
        ComponentCatalog _catalog;
        bool _open;
        int _selected;
        string _status = "";
        IComponentInteractionHost _componentHost;
        string _selectedInstanceId = "";
        string _selectedTargetKey = "";
        GdDict _lastCommandResult = new GdDict();
        bool _renderingComponents;
        readonly SelectableList _componentList;

        public void BindComponents(IComponentInteractionHost host)
        {
            _componentHost = host;
            Render();
        }

        public string GetSelectedInstanceId() => _selectedInstanceId;
        public GdDict LastCommandResult => _lastCommandResult.DeepCopy();
        public SelectableList ComponentList => _componentList;

        public bool SelectComponent(string instanceId)
        {
            foreach (object item in ComponentRows())
                if (item is GdDict row && row.GetString("instance_id") == instanceId)
                { _selectedInstanceId = instanceId; Render(); return true; }
            _selectedInstanceId = "";
            _status = "Selected component is no longer carried";
            Render();
            return false;
        }

        public bool SelectTarget(string shipId, string slotId)
        {
            List<GdDict> rows = SlotRows();
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].GetString("ship_id") == shipId && rows[i].GetString("slot_id") == slotId)
                { _selected = i; _selectedTargetKey = TargetKey(rows[i]); Render(); return true; }
            // Preserve the missing named target as a refusal; rendering must never substitute its first row.
            _selectedTargetKey = (shipId ?? "") + ":" + (slotId ?? "");
            _status = "Installation target is no longer available";
            Render();
            return false;
        }

        public void OpenForComponent(string instanceId)
        {
            Open();
            SelectComponent(instanceId);
        }

        public void OpenForTarget(string shipId, string slotId)
        {
            Open();
            SelectTarget(shipId, slotId);
        }

        static string TargetKey(GdDict row) => row.GetString("ship_id") + ":" + row.GetString("slot_id");

        GdArray ComponentRows()
        {
            if (_componentHost == null) return new GdArray();
            return _componentHost.ListComponentInstances(_componentHost.GetComponentHolderIds().GetString("player"));
        }

        /// <summary>Candidate empty slots the panel can install into (the coordinator may set).</summary>
        public List<string> CandidateSlots = new List<string> { "hub_slot_0", "hub_slot_1", "hub_slot_2" };

        readonly Meter _power = new Meter("Power draw");
        readonly Label _summary;
        readonly SelectableList _list;
        readonly Label _bag;

        public ShipModificationPanel() : base("SHIP MODIFICATION", SurfaceTime.Live)
        {
            AddToClassList("ss-shipmod");
            Body.Add(_power);
            _summary = UiFactory.Text("", UiClasses.LabelSecondary, UiClasses.LabelMono);
            Body.Add(_summary);
            _componentList = new SelectableList("component-instance", "No component equipment carried.");
            Body.Add(_componentList);
            UiFactory.SetShown(_componentList, false);
            _componentList.SelectionRequested += SelectComponentAt;
            _componentList.ActivateRequested += SelectComponentAt;
            _list = new SelectableList("slot", "No slots.");
            Body.Add(_list);
            _bag = UiFactory.Text("", UiClasses.LabelSecondary);
            Body.Add(_bag);
            var tools = UiFactory.Box(UiClasses.Toolbar);
            tools.Add(UiFactory.Button("Install", () => InstallFromInventory(_catalog), "act:install"));
            tools.Add(UiFactory.Button("Uninstall", () => UninstallSelected(), "act:uninstall"));
            Body.Add(tools);
            _list.SelectionRequested += i =>
            {
                if (_renderingComponents) return;
                _selected = i;
                if (_componentHost != null && i >= 0 && i < SlotRows().Count) _selectedTargetKey = TargetKey(SlotRows()[i]);
                Render();
            };
            _list.ActivateRequested += i =>
            {
                if (_renderingComponents) return;
                _selected = i;
                if (_componentHost != null && i >= 0 && i < SlotRows().Count) _selectedTargetKey = TargetKey(SlotRows()[i]);
                ActivateSelected();
            };
            Render();
        }

        public override string SurfaceId => "ship_mod";

        public void Bind(ShipModificationState modState, GdDict inventory = null)
        {
            _modState = modState;
            _inventory = inventory?.DeepCopy() ?? new GdDict();
            Render();
        }

        public void SetInventory(GdDict inventory)
        {
            _inventory = inventory?.DeepCopy() ?? new GdDict();
            Render();
        }

        /// <summary>Optional component catalog for the Install button (Godot passed it to install_from_inventory).</summary>
        public void SetCatalog(ComponentCatalog catalog) => _catalog = catalog;

        public bool IsOpen() => _open;

        public void Open()
        {
            _open = true;
            SetViewVisible(true);
            _selected = 0;
            if (_componentHost != null) _selectedTargetKey = "";
            _status = "";
            Render();
        }

        public void Close()
        {
            _open = false;
            SetViewVisible(false);
            PanelClosed?.Invoke();
        }

        public void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        protected override void RequestClose() => Close();

        public void Refresh() => Render();

        public int GetSelectedIndex() => _selected;

        public string GetSelectedSlotId()
        {
            if (_componentHost != null) return SelectedTarget().GetString("slot_id");
            List<GdDict> rows = SlotRows();
            if (_selected < 0 || _selected >= rows.Count) return "";
            return V.Str(rows[_selected].Get("slot_id", ""));
        }

        public void MoveSelection(int delta)
        {
            int n = SlotRows().Count;
            if (n <= 0)
            {
                _selected = 0;
                Render();
                return;
            }
            _selected = (int)GdMath.Clampi(_selected + delta, 0, n - 1);
            if (_componentHost != null) _selectedTargetKey = TargetKey(SlotRows()[_selected]);
            Render();
        }

        /// <summary>Uninstalls the selected occupied slot into the panel inventory bag.</summary>
        public bool UninstallSelected()
        {
            if (_componentHost != null)
            {
                GdDict selected = SelectedTarget();
                string instanceId = selected.GetString("instance_id");
                if (!selected.GetBool("occupied") || instanceId.Length == 0) return ComponentRefused("No installed component selected");
                return ShowComponentResult(_componentHost.RequestComponentRemoval(instanceId));
            }
            string slotId = GetSelectedSlotId();
            if (slotId.Length == 0 || _modState == null)
            {
                _status = "no slot";
                Render();
                return false;
            }
            GdDict row = RowForSlot(slotId);
            if (!row.GetBool("occupied", false))
            {
                _status = "empty slot";
                Render();
                return false;
            }
            string componentId = V.Str(row.Get("component_id", ""));
            string itemForm = V.Str(row.Get("item_form", ""));
            GdDict res = _modState.Uninstall(slotId, _inventory);
            if (!res.GetBool("ok", false))
            {
                _status = "uninstall failed: " + V.Str(res.Get("reason", ""));
                Render();
                return false;
            }
            if (itemForm.Length == 0) itemForm = V.Str(res.Get("item_form", ""));
            _status = "uninstalled " + slotId;
            UninstallRequested?.Invoke(slotId, componentId, itemForm);
            Render();
            return true;
        }

        /// <summary>Installs into the selected empty slot (or the first empty candidate when it is occupied).</summary>
        public bool InstallIntoSelected(string componentId, string itemForm, double powerDraw = 5.0, double mass = 10.0, bool plating = false)
        {
            if (_componentHost != null) return ComponentRefused("Choose an exact component instance and installation target");
            if (_modState == null)
            {
                _status = "no mod state";
                Render();
                return false;
            }
            string slotId = GetSelectedSlotId();
            GdDict row = RowForSlot(slotId);
            if (row.GetBool("occupied", false)) slotId = FirstEmptySlot();
            if (slotId.Length == 0)
            {
                _status = "no empty slot";
                Render();
                return false;
            }
            GdDict res = _modState.Install(slotId, componentId, itemForm, _inventory, powerDraw, mass, "hub", plating);
            if (!res.GetBool("ok", false))
            {
                _status = "install failed: " + V.Str(res.Get("reason", ""));
                Render();
                return false;
            }
            _status = "installed " + componentId + " -> " + slotId;
            InstallRequested?.Invoke(slotId, componentId, itemForm);
            Render();
            return true;
        }

        /// <summary>Installs the first bag item matching a known component form (then any stack as a last resort).</summary>
        public bool InstallFromInventory(ComponentCatalog catalog = null, IReadOnlyList<string> preferredForms = null)
        {
            if (_componentHost != null) return InstallSelectedComponent();
            preferredForms = preferredForms ?? DefaultPreferredForms;
            if (_inventory.IsEmpty)
            {
                _status = "empty inventory";
                Render();
                return false;
            }
            string form = "";
            foreach (string f in preferredForms)
            {
                if (_inventory.GetInt(f, 0) > 0)
                {
                    form = f;
                    break;
                }
            }
            if (form.Length == 0)
            {
                foreach (object k in _inventory.Keys)
                {
                    if (V.I64(_inventory[k]) > 0)
                    {
                        form = V.Str(k);
                        break;
                    }
                }
            }
            if (form.Length == 0)
            {
                _status = "no installable item";
                Render();
                return false;
            }
            string componentId = form;
            double powerDraw = 5.0;
            double mass = 10.0;
            bool plating = GdString.Find(form, "plating") >= 0 || GdString.Find(form, "plate") >= 0;
            if (catalog != null)
            {
                string cid = catalog.ComponentIdForItemForm(form);
                if (!string.IsNullOrEmpty(cid)) componentId = cid;
                GdDict def = catalog.GetComponent(componentId);
                if (def != null && !def.IsEmpty)
                {
                    powerDraw = def.GetFloat("power_draw", powerDraw);
                    mass = def.GetFloat("mass", mass);
                    if (def.Has("plating")) plating = V.Bool(def.Get("plating", plating));
                }
            }
            return InstallIntoSelected(componentId, form, powerDraw, mass, plating);
        }

        void ActivateSelected()
        {
            if (_componentHost != null)
            {
                if (SelectedTarget().GetBool("occupied")) UninstallSelected(); else InstallSelectedComponent();
                return;
            }
            GdDict row = RowForSlot(GetSelectedSlotId());
            if (row.GetBool("occupied", false)) UninstallSelected();
            else InstallFromInventory(_catalog);
        }

        public List<string> GetStatusLines()
        {
            if (_componentHost != null)
            {
                var diagnostic = new List<string> { "DIAGNOSTIC COMPONENT INTEGRATION", "Selected instance: " + _selectedInstanceId };
                foreach (GdDict target in SlotRows()) diagnostic.Add(TargetKey(target) + " | " + target.GetString("reason"));
                if (_status.Length != 0) diagnostic.Add("Status: " + _status);
                return diagnostic;
            }
            var lines = new List<string>();
            if (_modState == null)
            {
                lines.Add("Ship Mod: (unbound)");
                return lines;
            }
            double supply = _modState.PowerSupply;
            double draw = _modState.TotalPowerDraw();
            bool ok = _modState.IsPowerBudgetOk();
            lines.Add("Ship Mod: power " + GdString.FormatFixed(draw, 0) + "/" + GdString.FormatFixed(supply, 0) + " " + (ok ? "OK" : "OVER")
                + "  plating=" + GdString.FormatFixed(_modState.HullPlatingBonus, 2) + "  installed=" + GdString.FormatInt(_modState.InstalledCount()));
            int idx = 0;
            foreach (GdDict row in SlotRows())
            {
                string cursor = idx == _selected ? ">" : " ";
                if (row.GetBool("occupied", false))
                {
                    lines.Add(cursor + "[" + V.Str(row.Get("slot_id", "")) + "] " + V.Str(row.Get("component_id", ""))
                        + "  draw=" + GdString.FormatFixed(row.GetFloat("power_draw", 0.0), 1) + "  item=" + V.Str(row.Get("item_form", "")));
                }
                else
                {
                    lines.Add(cursor + "[" + V.Str(row.Get("slot_id", "")) + "] (empty)");
                }
                idx++;
            }
            if (_status.Length != 0) lines.Add("Status: " + _status);
            return lines;
        }

        public GdDict GetInventoryBag() => _inventory.DeepCopy();

        public string GetStatus() => _status;

        List<GdDict> SlotRows()
        {
            if (_componentHost != null)
            {
                var targets = new List<GdDict>();
                foreach (object item in _componentHost.ListInstallTargets(_selectedInstanceId))
                    if (item is GdDict row) targets.Add(row.DeepCopy());
                return targets;
            }
            var rows = new List<GdDict>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (_modState != null)
            {
                foreach (object e in _modState.Installed)
                {
                    if (!(e is GdDict entry)) continue;
                    GdDict row = entry.DeepCopy();
                    row["occupied"] = true;
                    string sid = V.Str(row.Get("slot_id", ""));
                    if (sid.Length == 0) continue;
                    seen.Add(sid);
                    rows.Add(row);
                }
            }
            foreach (string s in CandidateSlots)
            {
                if (seen.Contains(s)) continue;
                rows.Add(new GdDict { { "slot_id", s }, { "occupied", false } });
            }
            return rows;
        }

        GdDict RowForSlot(string slotId)
        {
            foreach (GdDict r in SlotRows())
            {
                if (V.Str(r.Get("slot_id", "")) == slotId) return r;
            }
            return new GdDict();
        }

        string FirstEmptySlot()
        {
            foreach (GdDict row in SlotRows())
            {
                if (!row.GetBool("occupied", true)) return V.Str(row.Get("slot_id", ""));
            }
            return "";
        }

        public SelectableList List => _list;
        public Meter PowerMeter => _power;
        public string SummaryText => _summary.text;

        void Render()
        {
            if (_componentHost != null) { RenderComponents(); return; }
            UiFactory.SetShown(_componentList, false);
            SetTitle("SHIP MODIFICATION");
            if (_modState == null)
            {
                _power.Set(0, 1, "", Meter.Severity.Normal);
                _summary.text = "Ship Mod: (unbound)";
            }
            else
            {
                bool ok = _modState.IsPowerBudgetOk();
                _power.Set(_modState.TotalPowerDraw(), Math.Max(0.0001, _modState.PowerSupply), " / " + GdString.FormatFixed(_modState.PowerSupply, 0),
                    ok ? Meter.Severity.Normal : Meter.Severity.Danger);
                _summary.text = (ok ? "✓ Power budget OK" : "⚠ Danger: power budget OVER")
                    + " · plating " + GdString.FormatFixed(_modState.HullPlatingBonus, 2)
                    + " · installed " + GdString.FormatInt(_modState.InstalledCount());
                SeverityText.Apply(_summary, ok ? Severity.None : Severity.Danger);
            }
            var items = new List<SelectableList.Item>();
            foreach (GdDict row in SlotRows())
            {
                string sid = V.Str(row.Get("slot_id", ""));
                bool occupied = row.GetBool("occupied", false);
                items.Add(new SelectableList.Item
                {
                    Id = sid,
                    Chip = sid,
                    Text = occupied ? V.Str(row.Get("component_id", "")) : "(empty)",
                    Detail = occupied
                        ? "draw " + GdString.FormatFixed(row.GetFloat("power_draw", 0.0), 1) + " · item " + V.Str(row.Get("item_form", ""))
                        : "Submit: install from bag",
                    Muted = !occupied,
                });
            }
            _list.SetItems(items, _selected);
            var bag = new List<string>();
            foreach (object k in _inventory.Keys) bag.Add(V.Str(k) + " ×" + GdString.FormatInt(V.I64(_inventory[k])));
            _bag.text = bag.Count == 0 ? "Parts bag: empty" : "Parts bag: " + string.Join(", ", bag);
            bool success = GdString.BeginsWith(_status, "installed ") || GdString.BeginsWith(_status, "uninstalled ");
            StatusText.Set(_status, _status.Length == 0 ? Severity.None : success ? Severity.Success : Severity.Caution);
        }

        protected override bool OnCommand(UiCommand command)
        {
            switch (command)
            {
                case UiCommand.Up:
                    MoveSelection(-1);
                    return true;
                case UiCommand.Down:
                    MoveSelection(1);
                    return true;
                case UiCommand.Accept:
                    ActivateSelected();
                    return true;
            }
            return false;
        }

        protected override VisualElement InitialFocusElement() => _list.Count > 0 ? _list.RowAt(Math.Max(0, _list.SelectedIndex)) : CloseButton;

        void SelectComponentAt(int index)
        {
            if (_renderingComponents) return;
            GdArray rows = ComponentRows();
            if (index >= 0 && index < rows.Count && rows[index] is GdDict row) SelectComponent(row.GetString("instance_id"));
        }

        GdDict SelectedTarget()
        {
            List<GdDict> rows = SlotRows();
            foreach (GdDict row in rows) if (TargetKey(row) == _selectedTargetKey) return row;
            if (_selectedTargetKey.Length != 0) return new GdDict();
            return _selected >= 0 && _selected < rows.Count ? rows[_selected] : new GdDict();
        }

        public bool InstallSelectedComponent()
        {
            if (_componentHost == null) return ComponentRefused("Component command host is unavailable");
            if (_selectedInstanceId.Length == 0) return ComponentRefused("Choose a component instance first");
            GdDict target = SelectedTarget();
            if (target.IsEmpty) return ComponentRefused("Choose an actual installation target");
            // The owner rechecks every gate. A displayed refusal never redirects to another slot.
            return ShowComponentResult(_componentHost.RequestComponentInstall(_selectedInstanceId, target.GetString("ship_id"), target.GetString("slot_id")));
        }

        bool ComponentRefused(string reason)
        {
            _lastCommandResult = new GdDict { { "ok", false }, { "committed", false }, { "reason", reason } };
            _status = reason; Render(); return false;
        }

        bool ShowComponentResult(GdDict result)
        {
            _lastCommandResult = result?.DeepCopy() ?? new GdDict { { "ok", false }, { "reason", "Component command returned no result" } };
            bool ok = _lastCommandResult.GetBool("ok");
            bool committed = _lastCommandResult.GetBool("committed");
            _status = ok && !committed && _lastCommandResult.GetString("reason") == "started"
                ? "Work started. Hold Interact to continue; release pauses." : _lastCommandResult.GetString("reason", "Command refused");
            Render();
            if (ok && !committed && _lastCommandResult.GetString("reason") == "started") ComponentWorkStarted?.Invoke(_lastCommandResult.DeepCopy());
            return ok;
        }

        void RenderComponents()
        {
            _renderingComponents = true;
            try
            {
            SetTitle("DIAGNOSTIC  |  COMPONENT EQUIPMENT");
            UiFactory.SetShown(_componentList, true);
            var components = new List<SelectableList.Item>();
            int selectedComponent = -1;
            foreach (object item in ComponentRows())
            {
                if (!(item is GdDict row)) continue;
                if (row.GetString("instance_id") == _selectedInstanceId) selectedComponent = components.Count;
                components.Add(new SelectableList.Item
                {
                    Id = "instance:" + row.GetString("instance_id"), Text = row.GetString("name", row.GetString("item_form")),
                    Chip = InventoryPanel.ComponentConditionText(row),
                    Detail = GdString.FormatFixed(row.GetFloat("mass"), 1) + " kg | " + row.GetString("holder"),
                });
            }
            _componentList.SetItems(components, selectedComponent < 0 ? 0 : selectedComponent);
            // Retain explicit identity even if its row vanishes; the owner will refuse instead of substituting a sibling.
            List<GdDict> targets = SlotRows();
            var slots = new List<SelectableList.Item>();
            int targetIndex = -1;
            foreach (GdDict row in targets)
            {
                if (TargetKey(row) == _selectedTargetKey) targetIndex = slots.Count;
                slots.Add(new SelectableList.Item
                {
                    Id = TargetKey(row), Text = row.GetString("ship_id") + " / " + row.GetString("slot_id"),
                    Chip = row.GetBool("occupied") ? "Installed" : "Empty",
                    Detail = row.GetBool("occupied") ? row.GetString("instance_id") + " | " + row.GetString("reason") : row.GetString("reason"),
                    Muted = !row.GetBool("ok"), Severity = row.GetBool("ok") ? Severity.None : Severity.Caution,
                });
            }
            _selected = targetIndex >= 0 ? targetIndex : Math.Max(0, Math.Min(_selected, Math.Max(0, slots.Count - 1)));
            if (_selectedTargetKey.Length == 0 && targets.Count > 0) _selectedTargetKey = TargetKey(targets[_selected]);
            _list.SetItems(slots, _selected);
            _power.Set(0, 1, "", Meter.Severity.Normal);
            _summary.text = "Choose an instance and a physical target. Installation/removal uses timed work.";
            GdDict work = _componentHost.GetComponentWorkState();
            if (work.GetString("status").Length != 0 && work.GetString("status") != "idle")
                _summary.text += "\nWork " + work.GetString("status") + " | " + work.GetString("instance_id") + " | " + work.GetString("reason");
            _bag.text = "Selected instance: " + (_selectedInstanceId.Length == 0 ? "none" : _selectedInstanceId);
            StatusText.Set(_status, _status.Length == 0 ? Severity.None : _lastCommandResult.GetBool("committed") ? Severity.Success
                : _lastCommandResult.GetBool("ok") ? Severity.Info : Severity.Caution);
            }
            finally { _renderingComponents = false; }
        }
    }
}
