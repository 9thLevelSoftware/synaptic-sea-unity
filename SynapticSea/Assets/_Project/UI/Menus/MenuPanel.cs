// Ported from scripts/ui/menu_panel.gd @ 96ecb2b0 (content composed by MenuCoordinator._refresh_menu_panel)
using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Pause-stack menu surface (PAUSED) rendering the current <c>MenuState</c> menu as real focusable 44 px rows
    /// instead of Godot's text lines. Settings rows are selectors (label + value, ◀ ▶ to cycle; Left/Right with keyboard
    /// or gamepad). Disabled items stay visible with "unavailable" wording. All decisions stay in the coordinator: rows
    /// forward activate / focus / cycle / navigate and the coordinator re-renders.
    /// </summary>
    public sealed class MenuPanel : SurfacePanel
    {
        public sealed class Row
        {
            public string Id = "";
            public string Label = "";
            public string Value = "";
            public bool Enabled = true;
            public bool Cyclable;
        }

        readonly VisualElement _rowsBox;
        readonly List<VisualElement> _rows = new List<VisualElement>();
        readonly List<Row> _data = new List<Row>();
        string _menuId = "";
        int _focusIndex;

        /// <summary>Menu command (navigate / accept / cancel / cycle) — normally MenuCoordinator.HandleUiInput.</summary>
        public Func<UiCommand, bool> CommandHandler;
        /// <summary>Pointer/submit activation of a row index.</summary>
        public Action<int> RowActivated;
        /// <summary>Focus moved onto a row index (keeps MenuState's focus in sync with UI Toolkit focus).</summary>
        public Action<int> RowFocused;
        /// <summary>Pointer ◀ / ▶ on a selector row (index, direction).</summary>
        public Action<int, int> RowCycled;

        public MenuPanel() : base("", SurfaceTime.Paused)
        {
            AddToClassList("ss-menu");
            _rowsBox = UiFactory.Box("ss-menu__rows");
            Body.Add(_rowsBox);
            UiFactory.SetShown(ActionBar, false);
        }

        public override string SurfaceId => "menu";

        public string MenuId => _menuId;
        public int FocusIndex => _focusIndex;
        public IReadOnlyList<VisualElement> RowElements => _rows;
        public IReadOnlyList<Row> Rows => _data;

        /// <summary>The Godot body text ("> label" / "  label (disabled)").</summary>
        public string BodyText { get; private set; } = "";

        public static string TokenFor(string menuId, string itemId) => "menu:" + menuId + ":" + itemId;

        public void SetContent(string menuId, string title, IList<Row> rows, int focusIndex, string godotBodyText)
        {
            SetTitle(title);
            BodyText = godotBodyText ?? "";
            bool rebuild = menuId != _menuId || rows.Count != _data.Count;
            for (int i = 0; !rebuild && i < rows.Count; i++) rebuild = rows[i].Id != _data[i].Id;
            _menuId = menuId ?? "";
            _data.Clear();
            _data.AddRange(rows);
            if (rebuild)
            {
                _rowsBox.Clear();
                _rows.Clear();
                for (int i = 0; i < _data.Count; i++)
                {
                    VisualElement row = BuildRow(i);
                    _rows.Add(row);
                    _rowsBox.Add(row);
                }
            }
            for (int i = 0; i < _data.Count; i++) Paint(i);
            _focusIndex = Math.Max(0, Math.Min(focusIndex, _data.Count - 1));
            for (int i = 0; i < _rows.Count; i++) _rows[i].EnableInClassList(UiClasses.RowSelected, i == _focusIndex);
            RememberFocus(_data.Count > 0 ? TokenFor(_menuId, _data[_focusIndex].Id) : "");
        }

        public VisualElement RowAt(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;

        public void FocusRow(int index) => UiFocus.Focus(RowAt(index));

        VisualElement BuildRow(int index)
        {
            var row = new VisualElement();
            row.AddToClassList(UiClasses.Row);
            row.AddToClassList(UiClasses.Focusable);
            row.AddToClassList("ss-menu-row");
            row.focusable = true;
            row.tabIndex = 0;
            var prev = UiFactory.Text("◀", "ss-menu-row__cycle");
            var label = UiFactory.Text("", "ss-menu-row__label");
            var value = UiFactory.Text("", "ss-menu-row__value", UiClasses.LabelMono);
            var next = UiFactory.Text("▶", "ss-menu-row__cycle");
            var note = UiFactory.Text("", "ss-menu-row__note", UiClasses.LabelSecondary);
            row.Add(label);
            row.Add(prev);
            row.Add(value);
            row.Add(next);
            row.Add(note);
            prev.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                RowCycled?.Invoke(_rows.IndexOf(row), -1);
            });
            next.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                RowCycled?.Invoke(_rows.IndexOf(row), 1);
            });
            row.RegisterCallback<ClickEvent>(e =>
            {
                int i = _rows.IndexOf(row);
                if (i >= 0 && _data[i].Enabled) RowActivated?.Invoke(i);
            });
            row.RegisterCallback<NavigationSubmitEvent>(e =>
            {
                int i = _rows.IndexOf(row);
                if (i < 0) return;
                UiFocus.Consume(e, this);
                if (_data[i].Enabled) RowActivated?.Invoke(i);
            });
            row.RegisterCallback<NavigationMoveEvent>(e =>
            {
                UiCommand? cmd = null;
                switch (e.direction)
                {
                    case NavigationMoveEvent.Direction.Up: cmd = UiCommand.Up; break;
                    case NavigationMoveEvent.Direction.Down: cmd = UiCommand.Down; break;
                    case NavigationMoveEvent.Direction.Left: cmd = UiCommand.Left; break;
                    case NavigationMoveEvent.Direction.Right: cmd = UiCommand.Right; break;
                }
                if (cmd == null) return;
                UiFocus.Consume(e, this);
                CommandHandler?.Invoke(cmd.Value);
                FocusRow(_focusIndex);
            });
            row.RegisterCallback<FocusInEvent>(e =>
            {
                int i = _rows.IndexOf(row);
                if (i >= 0 && i != _focusIndex) RowFocused?.Invoke(i);
            });
            return row;
        }

        void Paint(int index)
        {
            VisualElement row = _rows[index];
            Row data = _data[index];
            row.Q<Label>(className: "ss-menu-row__label").text = data.Label;
            var value = row.Q<Label>(className: "ss-menu-row__value");
            value.text = data.Value;
            UiFactory.SetShown(value, data.Value.Length != 0);
            foreach (Label cycle in row.Query<Label>(className: "ss-menu-row__cycle").ToList()) UiFactory.SetShown(cycle, data.Cyclable);
            var note = row.Q<Label>(className: "ss-menu-row__note");
            note.text = data.Enabled ? "" : "unavailable";
            UiFactory.SetShown(note, !data.Enabled);
            row.EnableInClassList(UiClasses.RowMuted, !data.Enabled);
            UiFocus.Tag(row, TokenFor(_menuId, data.Id));
        }

        public void SetShown(bool shown) => SetViewVisible(shown);

        public override bool Consume(UiCommand command) => CommandHandler != null && CommandHandler(command);

        protected override void RequestClose() => CommandHandler?.Invoke(UiCommand.Cancel);

        protected override VisualElement InitialFocusElement() => RowAt(_focusIndex) ?? CloseButton;
    }
}
