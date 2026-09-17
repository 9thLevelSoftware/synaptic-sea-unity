// Ported from scripts/ui/achievements_panel.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.UI
{
    /// <summary>
    /// REQ-RL-003/004 achievements (records screen, PAUSED). Renders the achievement catalog with unlocked/locked state
    /// from <see cref="AchievementState"/> — each row carries wording and a symbol, not colour alone.
    /// </summary>
    public sealed class AchievementsPanel : SurfacePanel
    {
        public const string CatalogPath = "res://data/release/achievement_catalog.json";

        AchievementState _state;
        GdDict _catalog = new GdDict();
        readonly SelectableList _list;
        readonly UnityEngine.UIElements.Label _summary;
        int _selected;

        public event System.Action BackRequested;

        public AchievementsPanel() : base("ACHIEVEMENTS", SurfaceTime.Paused)
        {
            _summary = UiFactory.Text("", UiClasses.LabelSecondary);
            Body.Add(_summary);
            _list = new SelectableList("achievement", "(catalog empty)");
            Body.Add(_list);
            _list.SelectionRequested += i =>
            {
                _selected = i;
                Render();
            };
        }

        public override string SurfaceId => "achievements";

        public void SetState(AchievementState state) => _state = state;
        public AchievementState GetState() => _state;

        /// <summary>Loads the catalog from JSON text, or from the catalog file when empty. Returns the entry count.</summary>
        public int LoadCatalog(string jsonText = "")
        {
            GdDict parsed = jsonText.Length == 0 ? CatalogRegistry.LoadDict(CatalogPath) : GdJson.ParseString(jsonText) as GdDict;
            if (parsed == null) return 0;
            _catalog = parsed;
            return _catalog.Get("achievements", null) is GdArray list ? list.Count : 0;
        }

        public int GetUnlockedCount() => _state == null ? 0 : _state.GetUnlockCount();

        public int GetTotalCount() => _catalog.Get("achievements", null) is GdArray list ? list.Count : 0;

        /// <summary>The Godot text ("[X] Name\n  description\n\n" per entry), kept for parity checks.</summary>
        public string RenderedText { get; private set; } = "";

        public SelectableList List => _list;
        public string SummaryText => _summary.text;

        public void Render()
        {
            if (!(_catalog.Get("achievements", null) is GdArray list))
            {
                RenderedText = "(catalog empty)";
                _list.SetItems(new List<SelectableList.Item>(), 0);
                _summary.text = "";
                return;
            }
            var unlocked = new HashSet<string>();
            if (_state != null)
            {
                foreach (object id in _state.GetUnlocked()) unlocked.Add(V.Str(id));
            }
            var bb = new System.Text.StringBuilder();
            var items = new List<SelectableList.Item>();
            foreach (object entry in list)
            {
                if (!(entry is GdDict dict)) continue;
                string id = V.Str(dict.Get("id", ""));
                string displayName = V.Str(dict.Get("display_name", ""));
                string description = V.Str(dict.Get("description", ""));
                bool isUnlocked = unlocked.Contains(id);
                bb.Append(isUnlocked ? "[X]" : "[ ]").Append(' ').Append(displayName).Append("\n  ").Append(description).Append("\n\n");
                items.Add(new SelectableList.Item
                {
                    Id = id,
                    Chip = isUnlocked ? "✓" : "□",
                    Text = displayName + (isUnlocked ? " — Unlocked" : " — Locked"),
                    Detail = description,
                    Severity = isUnlocked ? Severity.Success : Severity.None,
                    Muted = !isUnlocked,
                    Icon = UiIcons.Resolve(V.Str(dict.Get("icon_placeholder", ""))),
                });
            }
            RenderedText = bb.ToString();
            _summary.text = GetUnlockedCount() + " / " + GetTotalCount() + " unlocked";
            _list.SetItems(items, _selected);
        }

        protected override bool OnCommand(UiCommand command)
        {
            if (command != UiCommand.Up && command != UiCommand.Down) return false;
            if (_list.Count == 0) return true;
            _selected = _list.StepFrom(_selected, command == UiCommand.Up ? -1 : 1);
            Render();
            return true;
        }

        protected override void RequestClose() => BackRequested?.Invoke();

        protected override UnityEngine.UIElements.VisualElement InitialFocusElement() =>
            _list.Count > 0 ? _list.RowAt(System.Math.Max(0, _list.SelectedIndex)) : CloseButton;
    }
}
