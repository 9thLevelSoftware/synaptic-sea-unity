using System;
using System.Collections.Generic;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>Deliberate LIVE work selection. Model references, never display IDs, authorize confirmation.</summary>
    public sealed class NearbyWorkPickerPanel : SurfacePanel
    {
        public event Action PanelClosed;
        public event Action<GdDict> WorkResolved;
        public Func<IReadOnlyList<SessionInteractable>> ListTargets;
        public Func<SessionInteractable, GdDict> DescribeTarget;
        public Func<SessionInteractable, GdDict> RequestTarget;
        readonly SelectableList _list;
        readonly Label _detail;
        IReadOnlyList<SessionInteractable> _targets = Array.Empty<SessionInteractable>();
        int _selected;
        bool _open, _confirming, _covered;
        readonly Dictionary<SessionInteractable, string> _tokens = new Dictionary<SessionInteractable, string>();
        long _nextToken;
        string _status = "";
        public NearbyWorkPickerPanel() : base("CHOOSE NEARBY WORK", SurfaceTime.Live)
        {
            _list = new SelectableList("nearby_work", "No reachable repair, seal or workbench.") { Wrap = true };
            _detail = UiFactory.Text("", UiClasses.LabelSecondary);
            Body.Add(_list); Body.Add(_detail);
            Body.Add(UiFactory.Button("Select work", () => ConfirmSelection(), "action:select_work"));
            _list.SelectionRequested += i => { _selected = i; Render(); };
            _list.ActivateRequested += i => { _selected = i; ConfirmSelection(); };
        }
        public override string SurfaceId => "nearby_work_picker";
        public bool IsOpen() => _open;
        public SelectableList List => _list;
        public IReadOnlyList<SessionInteractable> Targets => _targets;
        public string GetStatus() => _status;
        public SessionInteractable SelectedTarget => _selected >= 0 && _selected < _targets.Count ? _targets[_selected] : null;
        public string DetailText => _detail.text;
        public void Open()
        {
            _open = true; _covered = false; _status = ""; _selected = 0;
            _tokens.Clear();
            _targets = ListTargets?.Invoke() ?? Array.Empty<SessionInteractable>();
            SetViewVisible(true); Render();
        }
        public void Close()
        {
            if (!_open) return;
            _open = false; _targets = Array.Empty<SessionInteractable>();
            _tokens.Clear();
            SetViewVisible(false); PanelClosed?.Invoke();
        }
        protected override void RequestClose() => Close();
        public void Refresh()
        {
            if (!_open || _confirming) return;
            SessionInteractable selected = SelectedTarget;
            var next = ListTargets?.Invoke() ?? Array.Empty<SessionInteractable>();
            int kept = -1;
            for (int i = 0; i < next.Count; i++) if (ReferenceEquals(next[i], selected)) { kept = i; break; }
            // Never replace a selection after movement, rebuilding, completion or deck travel.
            if (selected != null && kept < 0) { Close(); return; }
            _targets = next; _selected = kept >= 0 ? kept : 0; Render();
        }
        public GdDict ConfirmSelection()
        {
            if (!_open || _covered || !enabledInHierarchy || _confirming || SelectedTarget == null)
                return new GdDict { { "ok", false }, { "started", false }, { "reason", "no_selection" } };
            _confirming = true;
            try
            {
                // Blocked rows deliberately reach the authoritative denial path; no UI fallback.
                GdDict result = RequestTarget?.Invoke(SelectedTarget) ?? new GdDict { { "ok", false }, { "reason", "not_ready" } };
                if (result.GetBool("started") || result.GetBool("opened")) Close();
                else { _status = "Work denied: " + result.GetString("reason", "rejected"); Render(); }
                WorkResolved?.Invoke(result);
                return result;
            }
            finally { _confirming = false; }
        }
        public void MoveSelection(int direction)
        {
            if (_targets.Count == 0) return;
            _selected = (_selected + direction + _targets.Count) % _targets.Count; Render();
        }
        void Render()
        {
            var items = new List<SelectableList.Item>();
            for (int i = 0; i < _targets.Count; i++)
            {
                GdDict d = DescribeTarget?.Invoke(_targets[i]) ?? new GdDict();
                if (!_tokens.TryGetValue(_targets[i], out string token))
                    _tokens[_targets[i]] = token = "target:" + (++_nextToken).ToString();
                items.Add(new SelectableList.Item { Id = token, Text = d.GetString("label", d.GetString("id")),
                    Detail = d.GetString("reason"), Muted = d.GetString("status") != "ready" });
            }
            _list.SetItems(items, _selected);
            GdDict row = SelectedTarget != null ? DescribeTarget?.Invoke(SelectedTarget) : null;
            if (row == null) _detail.text = "No reachable repair, seal or workbench. Close and reopen after moving.";
            else
            {
                GdDict req = row.GetDictOrEmpty("requirements");
                var lines = new List<string> { row.GetString("label"),
                    "Owner: " + row.GetString("owner_id"), "Target: " + row.GetString("id"),
                    "Status: " + row.GetString("reason"), "Skill: " + req.GetString("skill_id") + " " + req.GetInt("min_skill") };
                if (req.GetString("required_item").Length > 0) lines.Add("Required item: " + req.GetString("required_item"));
                foreach (object part in req.GetArrayOrEmpty("parts")) lines.Add("Part: " + V.Str(part));
                foreach (object tool in req.GetArrayOrEmpty("tools")) lines.Add("Tool: " + V.Str(tool));
                if (row.GetString("station_kind").Length > 0)
                {
                    lines.Add("Station tier: " + req.GetInt("effective_tier"));
                    lines.Add("Select opens this workbench’s recipes. Crafting requires a separate confirmation.");
                }
                else lines.Add("Select starts this task only. Existing work controls remain available.");
                _detail.text = string.Join("\n", lines);
            }
            StatusText.Set(_status, _status.Length > 0 ? Severity.Caution : Severity.None);
        }
        public override void SetCovered(bool covered)
        {
            _covered = covered;
            base.SetCovered(covered);
        }
        protected override bool OnCommand(UiCommand command)
        {
            if (_covered || !enabledInHierarchy) return false;
            if (command == UiCommand.Up) { MoveSelection(-1); return true; }
            if (command == UiCommand.Down) { MoveSelection(1); return true; }
            if (command == UiCommand.Accept) { ConfirmSelection(); return true; }
            return false;
        }
        protected override VisualElement InitialFocusElement() => _list.RowAt(_selected) ?? CloseButton;
    }
}
