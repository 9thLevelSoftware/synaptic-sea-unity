// Ported from scripts/ui/codex_panel.gd @ 96ecb2b0 (content composed by MenuCoordinator._refresh_codex)
using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Codex (LIVE when opened from play; beneath it the pause stack keeps simulation suspended when opened from Pause).
    /// Readable list of unlocked entries (topic · title) with the body in a detail pane, the cross-run unlocks, and an
    /// explicit empty state. Unread/new markers are not shown: there is no persistence contract for them yet.
    /// The coordinator owns the codex menu state and composes the content; Back asks the coordinator to close.
    /// </summary>
    public sealed class CodexPanel : SurfacePanel
    {
        public sealed class Entry
        {
            public string Id = "";
            public string Topic = "";
            public string Title = "";
            public string Body = "";
        }

        public const string EmptyText = "No unlocked entries yet.";

        readonly SelectableList _list;
        readonly Label _body;
        readonly VisualElement _registry;
        readonly Label _registryLines;
        readonly List<Entry> _entries = new List<Entry>();
        readonly List<string> _lines = new List<string>();
        int _selected;

        /// <summary>Back / Cancel pressed (the coordinator closes the codex menu).</summary>
        public event Action CloseRequested;

        public CodexPanel() : base("CODEX", SurfaceTime.Live)
        {
            AddToClassList("ss-codex");
            var columns = UiFactory.Box(UiClasses.Columns);
            var listPane = UiFactory.Box(UiClasses.Pane);
            _list = new SelectableList("codex", EmptyText);
            listPane.Add(_list);
            var detailPane = UiFactory.Box(UiClasses.Pane, UiClasses.Detail);
            _body = UiFactory.Text("", UiClasses.LabelSecondary);
            detailPane.Add(_body);
            columns.Add(listPane);
            columns.Add(detailPane);
            Body.Add(columns);
            _registry = UiFactory.Box(UiClasses.Section);
            _registry.Add(UiFactory.Text("Cross-run unlocks", UiClasses.SectionTitle));
            _registryLines = UiFactory.Text("", UiClasses.LabelSecondary);
            _registry.Add(_registryLines);
            Body.Add(_registry);
            _list.SelectionRequested += i =>
            {
                _selected = i;
                Render();
            };
        }

        public override string SurfaceId => "codex";

        /// <summary>The Godot panel text (the coordinator's composed lines).</summary>
        public string Text => string.Join("\n", _lines);

        public IReadOnlyList<Entry> Entries => _entries;
        public SelectableList List => _list;
        public string BodyText => _body.text;

        public void SetContent(IReadOnlyList<Entry> entries, IReadOnlyList<string> registryLines, IReadOnlyList<string> godotLines)
        {
            _entries.Clear();
            _entries.AddRange(entries);
            _lines.Clear();
            _lines.AddRange(godotLines);
            _registryLines.text = string.Join("\n", registryLines);
            UiFactory.SetShown(_registry, registryLines.Count > 0);
            Render();
        }

        public void SetShown(bool shown) => SetViewVisible(shown);

        protected override void RequestClose() => CloseRequested?.Invoke();

        void Render()
        {
            var items = new List<SelectableList.Item>();
            foreach (Entry e in _entries) items.Add(new SelectableList.Item { Id = e.Id, Chip = e.Topic, Text = e.Title });
            _list.SetItems(items, _selected);
            if (_entries.Count == 0)
            {
                _body.text = EmptyText;
                return;
            }
            Entry cur = _entries[Math.Max(0, Math.Min(_selected, _entries.Count - 1))];
            _body.text = cur.Title + "\n\n" + cur.Body;
        }

        protected override bool OnCommand(UiCommand command)
        {
            if (command == UiCommand.Up || command == UiCommand.Down)
            {
                if (_list.Count == 0) return true;
                _selected = _list.StepFrom(_selected, command == UiCommand.Up ? -1 : 1);
                Render();
                return true;
            }
            return false;
        }

        protected override VisualElement InitialFocusElement() => _list.Count > 0 ? _list.RowAt(Math.Max(0, _list.SelectedIndex)) : CloseButton;
    }
}
