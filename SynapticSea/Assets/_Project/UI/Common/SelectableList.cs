using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// A keyboard/mouse/gamepad list of 44 px focusable rows. The owning presenter keeps the model cursor; the list
    /// raises requests (move, activate, pointer) and re-renders from <see cref="SetItems"/>. Rows are reused when the ids
    /// are unchanged, so refresh never drops focus; when rows change, focus is restored to the row carrying the
    /// selected id (or the deterministic adjacent row).
    /// Navigation: Up/Down move (clamped or wrapped per <see cref="Wrap"/>), Submit activates, Left/Right bubble to
    /// <see cref="MoveOut"/> (pane switch / verb cycling), pointer click selects, double click activates.
    /// </summary>
    public sealed class SelectableList : VisualElement
    {
        public sealed class Item
        {
            public string Id = "";
            public string Text = "";
            public string Detail = "";
            public string Chip = "";
            public Severity Severity;
            public bool Muted;
            public bool Marked;
            /// <summary>Optional row icon (achievement art, item category placeholder); null hides the icon.</summary>
            public UnityEngine.Texture2D Icon;
        }

        public const string RowIconClass = "ss-row__icon";

        readonly ScrollView _scroll;
        readonly Label _empty;
        readonly List<VisualElement> _rows = new List<VisualElement>();
        readonly List<Item> _items = new List<Item>();
        readonly string _listId;

        /// <summary>Wrap at the ends (scanner / recipe picker use wrapi) instead of clamping.</summary>
        public bool Wrap;

        public int SelectedIndex { get; private set; } = -1;
        public int Count => _items.Count;
        public IReadOnlyList<Item> Items => _items;
        public IReadOnlyList<VisualElement> RowElements => _rows;
        public string EmptyText => _empty.text;

        /// <summary>The user moved the cursor to <c>index</c> (keyboard/gamepad/pointer/focus).</summary>
        public event Action<int> SelectionRequested;
        /// <summary>Submit / double click on <c>index</c>.</summary>
        public event Action<int> ActivateRequested;
        /// <summary>Pointer down on a row (index, event) — for modifiers and secondary clicks. When subscribed, the list
        /// does not raise <see cref="SelectionRequested"/> for pointer presses itself.</summary>
        public event Action<int, PointerDownEvent> RowPointerDown;
        /// <summary>Left/Right navigation that the list does not handle. Return true to consume it.</summary>
        public Func<NavigationMoveEvent.Direction, bool> MoveOut;

        public SelectableList(string listId, string emptyText)
        {
            _listId = listId ?? "list";
            AddToClassList(UiClasses.List);
            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.AddToClassList("ss-list__scroll");
            Add(_scroll);
            _empty = UiFactory.Text(emptyText, UiClasses.Empty, UiClasses.LabelSecondary);
            Add(_empty);
            UiFactory.SetShown(_scroll, false);
        }

        public void SetEmptyText(string text) => _empty.text = text ?? "";

        public string TokenFor(string id) => _listId + ":" + id;

        public VisualElement RowAt(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;

        /// <summary>Renders <paramref name="items"/> with the cursor on <paramref name="selectedIndex"/>.</summary>
        public void SetItems(IList<Item> items, int selectedIndex)
        {
            VisualElement focused = UiFocus.FocusedWithin(this);
            bool hadFocus = focused != null;
            bool sameIds = items.Count == _items.Count;
            for (int i = 0; sameIds && i < items.Count; i++) sameIds = items[i].Id == _items[i].Id;

            _items.Clear();
            _items.AddRange(items);
            if (!sameIds)
            {
                _scroll.Clear();
                _rows.Clear();
                for (int i = 0; i < _items.Count; i++)
                {
                    VisualElement row = BuildRow(i);
                    _rows.Add(row);
                    _scroll.Add(row);
                }
            }
            for (int i = 0; i < _items.Count; i++)
            {
                Paint(_rows[i], _items[i]);
                UiFocus.Tag(_rows[i], TokenFor(_items[i].Id));
            }

            SelectedIndex = _items.Count == 0 ? -1 : Math.Max(0, Math.Min(selectedIndex, _items.Count - 1));
            for (int i = 0; i < _rows.Count; i++) _rows[i].EnableInClassList(UiClasses.RowSelected, i == SelectedIndex);
            UiFactory.SetShown(_scroll, _items.Count > 0);
            UiFactory.SetShown(_empty, _items.Count == 0);

            // Focus restore on refresh: a rebuilt list gives focus back to the selected row.
            if (hadFocus && SelectedIndex >= 0 && (!sameIds || UiFocus.FocusedWithin(this) == null))
                FocusRow(SelectedIndex);
        }

        public void FocusRow(int index)
        {
            VisualElement row = RowAt(index);
            if (row == null) return;
            UiFocus.Focus(row);
            if (row.panel != null) _scroll.ScrollTo(row);
        }

        /// <summary>The next cursor index for a one-step move (clamped, or wrapped when <see cref="Wrap"/>).</summary>
        public int StepFrom(int index, int delta)
        {
            if (_items.Count == 0) return -1;
            int next = index + delta;
            if (Wrap) return (int)SynapticSea.Core.Variant.GdMath.Wrapi(next, 0, _items.Count);
            return Math.Max(0, Math.Min(next, _items.Count - 1));
        }

        VisualElement BuildRow(int index)
        {
            var row = new VisualElement();
            row.AddToClassList(UiClasses.Row);
            row.AddToClassList(UiClasses.Focusable);
            row.focusable = true;
            row.tabIndex = 0;
            var chip = UiFactory.Text("", UiClasses.RowChip, UiClasses.LabelMono);
            var text = UiFactory.Text("", UiClasses.RowText);
            var detail = UiFactory.Text("", UiClasses.RowDetail, UiClasses.LabelSecondary);
            var icon = new Image { scaleMode = UnityEngine.ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            icon.AddToClassList(RowIconClass);
            icon.style.width = 28;
            icon.style.height = 28;
            icon.style.marginRight = 8;
            icon.style.flexShrink = 0;
            icon.style.alignSelf = Align.Center;
            UiFactory.SetShown(icon, false);
            row.Add(icon);
            var col = UiFactory.Box("ss-row__col");
            col.Add(text);
            col.Add(detail);
            row.Add(chip);
            row.Add(col);

            row.RegisterCallback<NavigationMoveEvent>(e => OnNavigate(row, e));
            row.RegisterCallback<NavigationSubmitEvent>(e =>
            {
                int i = _rows.IndexOf(row);
                if (i < 0) return;
                UiFocus.Consume(e, this);
                ActivateRequested?.Invoke(i);
            });
            row.RegisterCallback<PointerDownEvent>(e =>
            {
                int i = _rows.IndexOf(row);
                if (i < 0) return;
                if (RowPointerDown != null) RowPointerDown(i, e);
                else if (e.button == 0) SelectionRequested?.Invoke(i);
            });
            row.RegisterCallback<ClickEvent>(e =>
            {
                int i = _rows.IndexOf(row);
                if (i >= 0 && e.clickCount == 2) ActivateRequested?.Invoke(i);
            });
            row.RegisterCallback<FocusInEvent>(e =>
            {
                int i = _rows.IndexOf(row);
                if (i >= 0 && i != SelectedIndex) SelectionRequested?.Invoke(i);
            });
            return row;
        }

        void OnNavigate(VisualElement row, NavigationMoveEvent e)
        {
            int index = _rows.IndexOf(row);
            if (index < 0) return;
            switch (e.direction)
            {
                case NavigationMoveEvent.Direction.Up:
                case NavigationMoveEvent.Direction.Down:
                {
                    int delta = e.direction == NavigationMoveEvent.Direction.Up ? -1 : 1;
                    int next = StepFrom(index, delta);
                    // At a clamped end, let the event bubble so focus can leave the list (e.g. to the action bar).
                    if (next == index && !Wrap)
                    {
                        if (delta > 0 && index == _items.Count - 1) return;
                        if (delta < 0 && index == 0) return;
                    }
                    UiFocus.Consume(e, this);
                    SelectionRequested?.Invoke(next);
                    FocusRow(next);
                    break;
                }
                case NavigationMoveEvent.Direction.Left:
                case NavigationMoveEvent.Direction.Right:
                    if (MoveOut != null && MoveOut(e.direction)) UiFocus.Consume(e, this);
                    break;
            }
        }

        static void Paint(VisualElement row, Item item)
        {
            var chip = row.Q<Label>(className: UiClasses.RowChip);
            var text = row.Q<Label>(className: UiClasses.RowText);
            var detail = row.Q<Label>(className: UiClasses.RowDetail);
            var icon = row.Q<Image>(className: RowIconClass);
            if (icon != null)
            {
                icon.image = item.Icon;
                UiFactory.SetShown(icon, item.Icon != null);
            }
            chip.text = item.Chip ?? "";
            UiFactory.SetShown(chip, !string.IsNullOrEmpty(item.Chip));
            text.text = item.Text ?? "";
            detail.text = item.Detail ?? "";
            UiFactory.SetShown(detail, !string.IsNullOrEmpty(item.Detail));
            SeverityText.Apply(row, item.Severity);
            row.EnableInClassList(UiClasses.RowMuted, item.Muted);
            row.EnableInClassList(UiClasses.RowMarked, item.Marked);
        }

        /// <summary>Row index for a focus token, or -1.</summary>
        public int IndexOfId(string id)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == id) return i;
            }
            return -1;
        }
    }
}
