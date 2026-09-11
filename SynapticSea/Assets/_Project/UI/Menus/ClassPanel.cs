// Ported from scripts/ui/class_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// REQ-PM-001/010 class roster (records screen, PAUSED). Every class in data/player/classes.json with description,
    /// starting skills, selected / available / LOCKED state. Selection persists through the coordinator (meta
    /// progression save, demo gate); the roster stays outside the Milestone A title flow.
    /// </summary>
    public sealed class ClassPanel : SurfacePanel
    {
        public const string CatalogPath = "res://data/player/classes.json";

        readonly GdDict _classes = new GdDict();
        string _selectedClassId = "";
        MetaProgressionState _meta;
        int _selectedIndex;
        readonly Label _summary;
        readonly SelectableList _list;

        public event Action ConfirmRequested;
        public event Action BackRequested;

        public ClassPanel() : base("CLASS ROSTER", SurfaceTime.Paused)
        {
            _summary = UiFactory.Text("", UiClasses.LabelSecondary);
            Body.Add(_summary);
            _list = new SelectableList("class", "No classes.");
            Body.Add(_list);
            var tools = UiFactory.Box(UiClasses.Toolbar);
            tools.Add(UiFactory.Button("Select", () => ConfirmRequested?.Invoke(), "act:select"));
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

        public override string SurfaceId => "class";

        public void SetMetaState(MetaProgressionState meta) => _meta = meta;
        public MetaProgressionState GetMetaStatePanel() => _meta;

        /// <summary>Base classes are always selectable; unlockable ones need the meta unlock.</summary>
        public bool IsAvailable(string classId)
        {
            if (!_classes.Has(classId)) return false;
            bool unlockable = ((GdDict)_classes[classId]).GetBool("unlockable", false);
            if (!unlockable) return true;
            return _meta != null && _meta.IsClassUnlocked(classId);
        }

        public void MoveSelection(int direction)
        {
            int n = GetClassEntries().Count;
            if (n <= 0)
            {
                _selectedIndex = 0;
                return;
            }
            _selectedIndex = (int)GdMath.Clampi(_selectedIndex + direction, 0, n - 1);
        }

        public string GetSelectedId()
        {
            List<GdDict> entries = GetClassEntries();
            if (_selectedIndex < 0 || _selectedIndex >= entries.Count) return "";
            return V.Str(entries[_selectedIndex].Get("class_id", ""));
        }

        public int LoadCatalog(string jsonText = "")
        {
            GdDict parsed = jsonText.Length == 0 ? CatalogRegistry.LoadDict(CatalogPath) : GdJson.ParseString(jsonText) as GdDict;
            if (parsed == null) return 0;
            if (!(parsed.Get("classes", null) is GdArray list)) return 0;
            _classes.Clear();
            foreach (object entry in list)
            {
                if (!(entry is GdDict dict)) continue;
                string cid = V.Str(dict.Get("class_id", ""));
                if (cid.Length == 0) continue;
                _classes[cid] = dict.DeepCopy();
            }
            return _classes.Count;
        }

        public void SetSelectedClass(string classId) => _selectedClassId = classId ?? "";
        public string SelectedClassId => _selectedClassId;
        public int GetClassCount() => _classes.Count;

        public GdArray GetClassIds()
        {
            var keys = new GdArray(_classes.Keys);
            GdSort.Sort(keys);
            return keys;
        }

        public List<GdDict> GetClassEntries()
        {
            var output = new GdArray();
            foreach (object cid in _classes.Keys)
            {
                var entry = (GdDict)_classes[cid];
                string id = V.Str(cid);
                output.Add(new GdDict
                {
                    { "class_id", id },
                    { "display_name", V.Str(entry.Get("name", id)) },
                    { "description", V.Str(entry.Get("description", "")) },
                    { "starting_skills", (entry.Get("starting_skills", null) as GdDict ?? new GdDict()).ShallowCopy() },
                    { "selected", id == _selectedClassId },
                    { "unlockable", entry.GetBool("unlockable", false) },
                    { "available", IsAvailable(id) },
                });
            }
            output.SortCustom((a, b) => GdString.Less(V.Str(((GdDict)a).Get("class_id", "")), V.Str(((GdDict)b).Get("class_id", ""))));
            var result = new List<GdDict>();
            foreach (object o in output) result.Add((GdDict)o);
            return result;
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>
            {
                "Class Roster: " + _classes.Count + "  Selected: " + (_selectedClassId.Length != 0 ? _selectedClassId : "(none)"),
            };
            int idx = 0;
            foreach (GdDict entry in GetClassEntries())
            {
                string cid = V.Str(entry.Get("class_id", ""));
                string display = V.Str(entry.Get("display_name", cid));
                bool selected = entry.GetBool("selected", false);
                bool available = entry.GetBool("available", false);
                string cursor = idx == _selectedIndex ? ">" : " ";
                string marker = selected ? ">>" : available ? "[ ]" : "[LOCKED]";
                lines.Add(cursor + marker + " " + display + StartingSkills(entry, "  [", "]", "="));
                idx++;
            }
            return lines;
        }

        static string StartingSkills(GdDict entry, string open, string close, string sep)
        {
            GdDict starting = entry.Get("starting_skills", null) as GdDict ?? new GdDict();
            if (starting.IsEmpty) return "";
            var parts = new List<string>();
            foreach (object sid in starting.Keys) parts.Add(V.Str(sid) + sep + GdString.FormatInt(V.I64(starting[sid])));
            return open + string.Join(", ", parts) + close;
        }

        public void ShowResult(GdDict result)
        {
            string cid = V.Str(result.Get("detail", ""));
            if (result.GetBool("ok", false)) StatusText.Set("Selected " + cid + " for the next run", Severity.Success);
            else if (cid == "demo_blocked") StatusText.Set("Class selection is not available in the demo", Severity.Caution);
            else StatusText.Set(cid + " — " + (IsAvailable(cid) ? "could not save the selection" : "locked"), Severity.Caution);
        }

        public SelectableList List => _list;
        public string SummaryText => _summary.text;

        public void Render()
        {
            _summary.text = GetStatusLines()[0];
            var items = new List<SelectableList.Item>();
            foreach (GdDict entry in GetClassEntries())
            {
                string cid = V.Str(entry.Get("class_id", ""));
                bool selected = entry.GetBool("selected", false);
                bool available = entry.GetBool("available", false);
                string state = selected ? "✓ Selected" : available ? "Available" : "Locked";
                items.Add(new SelectableList.Item
                {
                    Id = cid,
                    Chip = selected ? "✓" : available ? "" : "LOCKED",
                    Text = V.Str(entry.Get("display_name", cid)),
                    Detail = state + " · " + V.Str(entry.Get("description", "")) + StartingSkills(entry, " · starts with ", "", " "),
                    Severity = selected ? Severity.Success : Severity.None,
                    Muted = !available,
                });
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
