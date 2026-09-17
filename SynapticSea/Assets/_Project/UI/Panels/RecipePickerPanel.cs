// Ported from scripts/ui/recipe_picker_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>The coordinator seam the recipe picker calls (Godot duck-typed PlayableGeneratedShip).</summary>
    public interface IRecipePickerHost
    {
        /// <summary>Entries from CraftingState.ListRecipeEntries (or the field-craft / salvage / hydroponics listers).</summary>
        GdArray ListStationRecipeEntries(string stationKind);

        /// <summary>{ok, reason, recipe_id}.</summary>
        GdDict BeginCraftFromPicker(string stationKind, string recipeId);

        IUiAudio Audio { get; }
    }

    /// <summary>
    /// Reference <see cref="IRecipePickerHost"/> over <see cref="CraftingState"/> for the fabrication stations, mirroring
    /// PlayableGeneratedShip.list_station_recipe_entries / begin_craft_from_picker's default (non field-craft /
    /// salvage / hydroponics) branch. The session supplies its own host for the other station kinds.
    /// </summary>
    public sealed class CraftingStationHost : IRecipePickerHost
    {
        readonly CraftingState _crafting;
        readonly InventoryState _inventory;
        readonly MaterialState _materials;  // quality source; a neutral MaterialState when the session supplies none
        readonly Func<long> _fabricationSkill;

        public IUiAudio Audio { get; set; }

        public CraftingStationHost(CraftingState crafting, InventoryState inventory, Func<long> fabricationSkill, MaterialState materials = null)
        {
            _crafting = crafting;
            _inventory = inventory;
            _fabricationSkill = fabricationSkill;
            _materials = materials ?? new MaterialState();
        }

        public GdArray ListStationRecipeEntries(string stationKind)
        {
            if (_inventory == null || _crafting == null) return new GdArray();
            return _crafting.ListRecipeEntries(stationKind, _inventory, _fabricationSkill?.Invoke() ?? 0);
        }

        public GdDict BeginCraftFromPicker(string stationKind, string recipeId)
        {
            if (recipeId.Length == 0 || stationKind.Length == 0) return Result(false, "bad_args", recipeId);
            if (_crafting == null) return Result(false, "not_ready", recipeId);
            if (_crafting.IsCrafting()) return Result(false, "busy", recipeId);
            bool ok = _crafting.BeginCraft(recipeId, _inventory, _materials, _fabricationSkill?.Invoke() ?? 0);
            return Result(ok, ok ? "started" : "begin_failed", recipeId);
        }

        static GdDict Result(bool ok, string reason, string recipeId) =>
            new GdDict { { "ok", ok }, { "reason", reason }, { "recipe_id", recipeId } };
    }

    /// <summary>
    /// REQ-CS-016 station recipe picker (LIVE inspection). Lists the station's entries with authoritative status
    /// (ready / missing_ingredients / insufficient_skill / insufficient_tier / output_full) — never fabricated — and a
    /// detail pane of required inputs and output. Confirm begins through the host; blocked entries are denied with the
    /// model's status. Cursor starts on the first ready recipe and keeps the same recipe across refreshes.
    /// </summary>
    public sealed class RecipePickerPanel : SurfacePanel
    {
        public event Action PanelClosed;
        public event Action<GdDict> CraftResolved;

        IRecipePickerHost _host;
        string _stationKind = "";
        GdArray _entries = new GdArray();
        int _selected;
        string _status = "";
        bool _open;

        readonly SelectableList _list;
        readonly Label _detail;
        readonly Button _craft;

        public RecipePickerPanel() : base("CRAFT", SurfaceTime.Live)
        {
            AddToClassList("ss-recipes");
            var columns = UiFactory.Box(UiClasses.Columns);
            var listPane = UiFactory.Box(UiClasses.Pane);
            _list = new SelectableList("recipe", "No recipes.") { Wrap = true };
            listPane.Add(_list);
            var detailPane = UiFactory.Box(UiClasses.Pane, UiClasses.Detail);
            _detail = UiFactory.Text("", UiClasses.LabelSecondary);
            detailPane.Add(_detail);
            columns.Add(listPane);
            columns.Add(detailPane);
            Body.Add(columns);
            var tools = UiFactory.Box(UiClasses.Toolbar);
            _craft = UiFactory.Button("Craft", () => ConfirmSelection(), "act:craft");
            tools.Add(_craft);
            Body.Add(tools);
            _list.SelectionRequested += i =>
            {
                _selected = i;
                Render();
            };
            _list.ActivateRequested += i =>
            {
                _selected = i;
                ConfirmSelection();
            };
            Render();
        }

        public override string SurfaceId => "recipe_picker";

        public void Bind(IRecipePickerHost host) => _host = host;

        public bool IsOpen() => _open;
        public string GetStationKind() => _stationKind;
        public int GetSelectedIndex() => _selected;
        public string GetStatus() => _status;
        public int GetEntryCount() => _entries.Count;

        public string GetSelectedId()
        {
            if (_selected < 0 || _selected >= _entries.Count) return "";
            return V.Str(((GdDict)_entries[_selected]).Get("recipe_id", ""));
        }

        public List<string> GetRowTexts()
        {
            var output = new List<string>();
            foreach (object entry in _entries) output.Add(FormatRow((GdDict)entry));
            return output;
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>
            {
                "Craft: " + (_stationKind.Length != 0 ? _stationKind : "?") + "  (" + GdString.FormatInt(_entries.Count) + " recipes)",
            };
            List<string> rows = GetRowTexts();
            for (int i = 0; i < rows.Count; i++) lines.Add((i == _selected ? "> " : "  ") + rows[i]);
            if (_status.Length != 0) lines.Add(_status);
            return lines;
        }

        public void OpenForStation(string stationKind)
        {
            _stationKind = stationKind ?? "";
            _open = true;
            SetViewVisible(true);
            _status = "";
            RefreshEntries();
            _selected = FirstReadyIndex();
            Render();
        }

        public void Close()
        {
            _open = false;
            SetViewVisible(false);
            _stationKind = "";
            PanelClosed?.Invoke();
        }

        protected override void RequestClose() => Close();

        public void Refresh()
        {
            if (!_open) return;
            string prevId = GetSelectedId();
            RefreshEntries();
            int kept = -1;
            if (prevId.Length != 0)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (V.Str(((GdDict)_entries[i]).Get("recipe_id", "")) == prevId)
                    {
                        kept = i;
                        break;
                    }
                }
            }
            _selected = kept >= 0 ? kept : FirstReadyIndex();
            Render();
        }

        public void MoveSelection(int dir)
        {
            if (_entries.IsEmpty) return;
            _selected = (int)GdMath.Wrapi(_selected + dir, 0, _entries.Count);
            Render();
        }

        public GdDict ConfirmSelection()
        {
            GdDict result;
            if (_entries.IsEmpty)
            {
                _status = "no recipes";
                Render();
                PlayDeny();
                result = new GdDict { { "ok", false }, { "reason", "no_recipes" }, { "recipe_id", "" } };
                CraftResolved?.Invoke(result);
                return result;
            }
            var entry = (GdDict)_entries[_selected];
            string rid = V.Str(entry.Get("recipe_id", ""));
            if (!entry.GetBool("craftable", false))
            {
                _status = "blocked: " + V.Str(entry.Get("status", "unknown"));
                Render();
                PlayDeny();
                result = new GdDict { { "ok", false }, { "reason", V.Str(entry.Get("status", "blocked")) }, { "recipe_id", rid } };
                CraftResolved?.Invoke(result);
                return result;
            }
            if (_host == null)
            {
                _status = "no craft handler";
                Render();
                PlayDeny();
                result = new GdDict { { "ok", false }, { "reason", "not_ready" }, { "recipe_id", rid } };
                CraftResolved?.Invoke(result);
                return result;
            }
            result = _host.BeginCraftFromPicker(_stationKind, rid);
            if (result.GetBool("ok", false))
            {
                Close();
                CraftResolved?.Invoke(result);
                return result;
            }
            _status = V.Str(result.Get("reason", "rejected"));
            Render();
            PlayDeny();
            Refresh();
            CraftResolved?.Invoke(result);
            return result;
        }

        void PlayDeny() => _host?.Audio?.PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);

        void RefreshEntries()
        {
            _entries = new GdArray();
            if (_host == null) return;
            GdArray listed = _host.ListStationRecipeEntries(_stationKind);
            if (listed != null) _entries = listed;
        }

        int FirstReadyIndex()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (((GdDict)_entries[i]).GetBool("craftable", false)) return i;
            }
            return 0;
        }

        /// <summary>The Godot row text: "[status] Name  skill=N  mat×q ... → out×q".</summary>
        public static string FormatRow(GdDict entry)
        {
            string status = V.Str(entry.Get("status", "?"));
            string name = V.Str(entry.Get("display_name", entry.Get("recipe_id", "?")));
            long skill = entry.GetInt("required_skill_level", 0);
            GdDict produces = entry.Get("produces", null) as GdDict ?? new GdDict();
            string outId = V.Str(produces.Get("item_id", ""));
            long outQty = produces.GetInt("quantity", 0);
            var ingParts = new List<string>();
            if (entry.Get("ingredients", null) is GdDict ingredients)
            {
                foreach (object matId in ingredients.Keys) ingParts.Add(V.Str(matId) + "×" + GdString.FormatInt(V.I64(ingredients[matId])));
            }
            string ingStr = ingParts.Count != 0 ? string.Join(" ", ingParts) : "-";
            return "[" + status + "] " + name + "  skill=" + GdString.FormatInt(skill) + "  " + ingStr + " → " + outId + "×" + GdString.FormatInt(outQty);
        }

        public static string StatusWording(string status)
        {
            switch (status)
            {
                case "ready": return "Ready";
                case "missing_ingredients": return "Missing ingredients";
                case "insufficient_skill": return "Skill too low";
                case "insufficient_tier": return "Station tier too low";
                case "output_full": return "No room for output";
                default: return status;
            }
        }

        public SelectableList List => _list;
        public string DetailText => _detail.text;

        void Render()
        {
            if (_stationKind == "salvage") SetTitle("SALVAGE");
            else if (_stationKind == "field_crafting") SetTitle("FIELD CRAFT");
            else if (_stationKind == "hydroponics") SetTitle("HYDROPONICS");
            else SetTitle("CRAFT — " + (_stationKind.Length != 0 ? _stationKind : "?"));

            var items = new List<SelectableList.Item>();
            foreach (object e in _entries)
            {
                var entry = (GdDict)e;
                string status = V.Str(entry.Get("status", "?"));
                bool ready = entry.GetBool("craftable", false);
                Severity sev = ready ? Severity.Success : Severity.Caution;
                items.Add(new SelectableList.Item
                {
                    Id = V.Str(entry.Get("recipe_id", "")),
                    Chip = SeverityText.Symbol(sev),
                    Text = V.Str(entry.Get("display_name", entry.Get("recipe_id", "?"))),
                    Detail = StatusWording(status),
                    Severity = ready ? Severity.None : Severity.Caution,
                    Muted = !ready,
                });
            }
            _list.SetItems(items, _selected);
            _detail.text = DetailFor(_entries.IsEmpty ? null : (GdDict)_entries[Math.Max(0, Math.Min(_selected, _entries.Count - 1))]);
            bool deny = _status.Length != 0;
            StatusText.Set(_status, deny ? Severity.Caution : Severity.None);
        }

        static string DetailFor(GdDict entry)
        {
            if (entry == null) return "No recipes for this station.";
            var lines = new List<string>
            {
                V.Str(entry.Get("display_name", entry.Get("recipe_id", "?"))),
                "Status: " + StatusWording(V.Str(entry.Get("status", "?"))),
                "Skill required " + GdString.FormatInt(entry.GetInt("required_skill_level", 0)),
            };
            if (entry.Get("ingredients", null) is GdDict ingredients && !ingredients.IsEmpty)
            {
                lines.Add("Inputs:");
                foreach (object matId in ingredients.Keys) lines.Add("  " + V.Str(matId) + " ×" + GdString.FormatInt(V.I64(ingredients[matId])));
            }
            GdDict produces = entry.Get("produces", null) as GdDict ?? new GdDict();
            if (!produces.IsEmpty)
                lines.Add("Output: " + V.Str(produces.Get("item_id", "")) + " ×" + GdString.FormatInt(produces.GetInt("quantity", 0)));
            if (entry.Has("craft_time_seconds"))
                lines.Add("Time " + GdString.FormatFixed(entry.GetFloat("craft_time_seconds", 0.0), 0) + " s");
            return string.Join("\n", lines);
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
                    ConfirmSelection();
                    return true;
            }
            return false;
        }

        protected override VisualElement InitialFocusElement() => _list.Count > 0 ? _list.RowAt(Math.Max(0, _list.SelectedIndex)) : CloseButton;
    }
}
