// Ported from scripts/ui/skill_tree_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// REQ-PM-010 skill tree (records screen, PAUSED). Reads <see cref="SkillTreeState"/> + <see cref="PlayerProgressionState"/>;
    /// every skill shows level, XP to next, category, prerequisites and locked/unlocked/maxed. Confirm asks the
    /// coordinator to unlock (its can_unlock gate is authoritative); the outcome — including the model's missing
    /// prerequisites on a denial — is shown with wording + symbol.
    /// </summary>
    public sealed class SkillTreePanel : SurfacePanel
    {
        SkillTreeState _tree;
        PlayerProgressionState _progression;
        int _selectedIndex;
        readonly Label _summary;
        readonly SelectableList _list;
        readonly Label _detail;

        public event Action ConfirmRequested;
        public event Action BackRequested;

        public SkillTreePanel() : base("SKILL TREE", SurfaceTime.Paused)
        {
            _summary = UiFactory.Text("", UiClasses.LabelSecondary);
            Body.Add(_summary);
            var columns = UiFactory.Box(UiClasses.Columns);
            var listPane = UiFactory.Box(UiClasses.Pane);
            _list = new SelectableList("skill", "Skill Tree: (uninitialized)");
            listPane.Add(_list);
            var detailPane = UiFactory.Box(UiClasses.Pane, UiClasses.Detail);
            _detail = UiFactory.Text("", UiClasses.LabelSecondary);
            detailPane.Add(_detail);
            columns.Add(listPane);
            columns.Add(detailPane);
            Body.Add(columns);
            var tools = UiFactory.Box(UiClasses.Toolbar);
            tools.Add(UiFactory.Button("Unlock", () => ConfirmRequested?.Invoke(), "act:unlock"));
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

        public override string SurfaceId => "skill_tree";

        public void SetTree(SkillTreeState tree) => _tree = tree;
        public void SetProgression(PlayerProgressionState progression) => _progression = progression;
        public SkillTreeState GetTreePanel() => _tree;
        public PlayerProgressionState GetProgressionPanel() => _progression;

        public void MoveSelection(int direction)
        {
            int n = _tree != null ? _tree.GetSkillEntries().Count : 0;
            if (n <= 0)
            {
                _selectedIndex = 0;
                return;
            }
            _selectedIndex = (int)GdMath.Clampi(_selectedIndex + direction, 0, n - 1);
        }

        public int SelectedIndex => _selectedIndex;

        public string GetSelectedId()
        {
            if (_tree == null) return "";
            GdArray entries = _tree.GetSkillEntries();
            if (_selectedIndex < 0 || _selectedIndex >= entries.Count) return "";
            return V.Str(((GdDict)entries[_selectedIndex]).Get("skill_id", ""));
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (_tree == null)
            {
                lines.Add("Skill Tree: (uninitialized)");
                return lines;
            }
            GdArray entries = _tree.GetSkillEntries();
            lines.Add("Skill Tree: " + GdString.FormatInt(_tree.GetUnlocked().Count) + " / " + GdString.FormatInt(entries.Count) + " unlocked");
            int idx = 0;
            foreach (object e in entries)
            {
                var entry = (GdDict)e;
                string sid = V.Str(entry.Get("skill_id", ""));
                string display = V.Str(entry.Get("display_name", sid));
                string cat = V.Str(entry.Get("category", ""));
                string book = V.Str(entry.Get("book_prerequisite", ""));
                bool isUnlocked = entry.GetBool("unlocked", false);
                long lvl = 0;
                string xpToNext = "";
                if (_progression != null)
                {
                    lvl = _progression.GetSkillLevel(sid);
                    if (lvl < 10)
                    {
                        long xp = _progression.GetSkillXp(sid);
                        long needed = (lvl + 1) * 100;
                        xpToNext = "  xp=" + GdString.FormatInt(xp) + "/" + GdString.FormatInt(needed);
                    }
                    else
                    {
                        xpToNext = "  (max)";
                    }
                }
                lines.Add((idx == _selectedIndex ? ">" : " ") + (isUnlocked ? "[X]" : "[ ]") + " " + display + " [" + cat + "] L" + GdString.FormatInt(lvl) + xpToNext);
                GdArray prereqs = _tree.GetPrerequisites(sid);
                if (prereqs.IsEmpty && book.Length == 0)
                {
                    lines.Add("    prereq: none");
                }
                else
                {
                    foreach (object p in prereqs)
                    {
                        var prereq = (GdDict)p;
                        lines.Add("    prereq: " + V.Str(prereq.Get("skill_id", "")) + " >= " + GdString.FormatInt(prereq.GetInt("min_level", 1)));
                    }
                    if (book.Length != 0) lines.Add("    prereq: read " + book);
                }
                idx++;
            }
            return lines;
        }

        /// <summary>Shows the coordinator's confirm result; a denial carries the model's missing prerequisites.</summary>
        public void ShowResult(GdDict result, object metaState)
        {
            string sid = V.Str(result.Get("detail", ""));
            if (result.GetBool("ok", false))
            {
                StatusText.Set("Unlocked " + sid, Severity.Success);
                return;
            }
            string reason = "cannot unlock";
            if (_tree != null && sid.Length != 0)
            {
                GdDict chk = _tree.CanUnlock(sid, _progression, metaState);
                reason = V.Str(chk.Get("reason", reason));
                var missing = new List<string>();
                foreach (object m in chk.GetArrayOrEmpty("missing"))
                {
                    var md = (GdDict)m;
                    missing.Add(V.Str(md.Get("type", "")) == "book"
                        ? "read " + V.Str(md.Get("book_id", ""))
                        : V.Str(md.Get("skill_id", "")) + " ≥ " + GdString.FormatInt(md.GetInt("min_level", 1)));
                }
                if (missing.Count > 0) reason += ": " + string.Join(", ", missing);
            }
            StatusText.Set(sid + " — " + reason, Severity.Caution);
        }

        public SelectableList List => _list;
        public string SummaryText => _summary.text;

        public void Render()
        {
            List<string> godot = GetStatusLines();
            _summary.text = godot[0];
            var items = new List<SelectableList.Item>();
            string detail = "";
            if (_tree != null)
            {
                int idx = 0;
                foreach (object e in _tree.GetSkillEntries())
                {
                    var entry = (GdDict)e;
                    string sid = V.Str(entry.Get("skill_id", ""));
                    bool isUnlocked = entry.GetBool("unlocked", false);
                    long lvl = _progression != null ? _progression.GetSkillLevel(sid) : 0;
                    string state = isUnlocked ? "Unlocked" : "Locked";
                    if (lvl >= 10) state = "Maxed";
                    items.Add(new SelectableList.Item
                    {
                        Id = sid,
                        Chip = "L" + GdString.FormatInt(lvl),
                        Text = V.Str(entry.Get("display_name", sid)),
                        Detail = V.Str(entry.Get("category", "")) + " · " + state,
                        Severity = isUnlocked ? Severity.Success : Severity.None,
                        Muted = !isUnlocked,
                    });
                    if (idx == _selectedIndex) detail = DetailLines(godot, idx);
                    idx++;
                }
            }
            _list.SetItems(items, _selectedIndex);
            _detail.text = detail;
        }

        /// <summary>The Godot block (header + prereq lines) for one entry.</summary>
        static string DetailLines(List<string> godot, int entryIndex)
        {
            int seen = -1;
            var block = new List<string>();
            for (int i = 1; i < godot.Count; i++)
            {
                bool header = !GdString.BeginsWith(godot[i], "    ");
                if (header) seen++;
                if (seen == entryIndex) block.Add(GdString.StripEdges(godot[i]));
                else if (seen > entryIndex) break;
            }
            return string.Join("\n", block);
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
