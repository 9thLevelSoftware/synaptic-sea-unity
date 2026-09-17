// Ported from scripts/ui/language_selector.gd, scripts/ui/release_badge_overlay.gd, scripts/ui/credits_screen.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// REQ-RL-005 language selector (records screen, PAUSED). Owns a <see cref="LocalizationCatalog"/>; choosing a row
    /// emits <see cref="LanguageChanged"/> (Godot's OptionButton.item_selected). The active language carries a ✓.
    /// </summary>
    public sealed class LanguageSelector : SurfacePanel
    {
        LocalizationCatalog _catalog;
        string _active = "en";
        int _cursor;
        readonly SelectableList _list;

        public event Action<string> LanguageChanged;
        public event Action BackRequested;

        public LanguageSelector() : base("LANGUAGE", SurfaceTime.Paused)
        {
            _list = new SelectableList("language", "No languages.");
            Body.Add(_list);
            _list.SelectionRequested += i =>
            {
                _cursor = i;
                Render();
            };
            _list.ActivateRequested += i => SelectIndex(i);
        }

        public override string SurfaceId => "language";

        public void SetCatalog(LocalizationCatalog catalog)
        {
            _catalog = catalog;
            List<string> langs = GetKnownLanguagesList();
            _cursor = Math.Max(0, langs.IndexOf(_active));
            Render();
        }

        public LocalizationCatalog GetCatalog() => _catalog;
        public string GetActiveLanguage() => _active;

        public void SetActiveLanguage(string languageId)
        {
            _active = languageId ?? "";
            Render();
        }

        public string Translate(string stringId) => _catalog == null ? "" : _catalog.Translate(stringId, _active);

        public string TranslateFallback(string stringId, string defaultText) =>
            _catalog == null ? defaultText : _catalog.TranslateFallback(stringId, defaultText, _active);

        public GdArray GetKnownLanguages() => _catalog == null ? new GdArray() : _catalog.GetKnownLanguages();

        List<string> GetKnownLanguagesList() => GdString.ToStringList(GetKnownLanguages());

        /// <summary>Godot <c>_on_item_selected</c>: activates the language at <paramref name="index"/>.</summary>
        public void SelectIndex(int index)
        {
            List<string> langs = GetKnownLanguagesList();
            if (index < 0 || index >= langs.Count) return;
            _cursor = index;
            string selected = langs[index];
            if (selected == _active)
            {
                Render();
                return;
            }
            _active = selected;
            Render();
            LanguageChanged?.Invoke(_active);
        }

        public SelectableList List => _list;

        void Render()
        {
            var items = new List<SelectableList.Item>();
            foreach (string lang in GetKnownLanguagesList())
            {
                bool active = lang == _active;
                items.Add(new SelectableList.Item { Id = lang, Chip = active ? "✓" : "", Text = lang, Detail = active ? "Active" : "", Severity = active ? Severity.Success : Severity.None });
            }
            _list.SetItems(items, _cursor);
        }

        protected override bool OnCommand(UiCommand command)
        {
            switch (command)
            {
                case UiCommand.Up:
                case UiCommand.Down:
                    if (_list.Count == 0) return true;
                    _cursor = _list.StepFrom(_cursor, command == UiCommand.Up ? -1 : 1);
                    Render();
                    return true;
                case UiCommand.Accept:
                    SelectIndex(_cursor);
                    return true;
            }
            return false;
        }

        protected override void RequestClose() => BackRequested?.Invoke();

        protected override VisualElement InitialFocusElement() => _list.Count > 0 ? _list.RowAt(Math.Max(0, _list.SelectedIndex)) : CloseButton;
    }

    /// <summary>
    /// REQ-RL-002/006 build info (records screen "Build Info", PAUSED). DEV / DEMO / RELEASE badge from
    /// <see cref="BuildMetadataState.GetBuildKind"/> — a quiet secondary record, not diagnostic HUD noise — plus the
    /// readable version and store.
    /// </summary>
    public sealed class ReleaseBadgeOverlay : SurfacePanel
    {
        public static readonly Color DevColor = new Color(0.6f, 0.6f, 0.6f, 1f);
        public static readonly Color DemoColor = new Color(1f, 0.55f, 0f, 1f);
        public static readonly Color ReleaseColor = new Color(0.2f, 0.85f, 0.3f, 1f);

        BuildMetadataState _metadata;
        readonly Label _badge;
        readonly Label _info;

        public event Action MetadataChanged;
        public event Action BackRequested;

        public ReleaseBadgeOverlay() : base("BUILD INFO", SurfaceTime.Paused)
        {
            _badge = UiFactory.Text("...", "ss-release-badge", UiClasses.LabelMono);
            _info = UiFactory.Text("", UiClasses.LabelSecondary, UiClasses.LabelMono);
            Body.Add(_badge);
            Body.Add(_info);
        }

        public override string SurfaceId => "release_badge";

        public void SetMetadata(BuildMetadataState metadata)
        {
            _metadata = metadata;
            RefreshBadge();
            MetadataChanged?.Invoke();
        }

        public BuildMetadataState GetMetadata() => _metadata;

        public string GetBadgeText()
        {
            if (_metadata == null) return "DEV";
            string kind = _metadata.GetBuildKind();
            if (kind == "demo") return "DEMO";
            if (kind == "release") return "RELEASE";
            return "DEV";
        }

        public Color GetBadgeColor()
        {
            if (_metadata == null) return DevColor;
            string kind = _metadata.GetBuildKind();
            if (kind == "demo") return DemoColor;
            if (kind == "release") return ReleaseColor;
            return DevColor;
        }

        public string BadgeLabelText => _badge.text;
        public string InfoText => _info.text;

        void RefreshBadge()
        {
            _badge.text = GetBadgeText();
            _badge.style.color = GetBadgeColor();
            _badge.style.borderLeftColor = GetBadgeColor();
            if (_metadata == null)
            {
                _info.text = "";
                return;
            }
            var lines = new List<string> { "Version " + _metadata.Version, "Store " + _metadata.Store };
            if (_metadata.ReleaseDate.Length != 0) lines.Add("Released " + _metadata.ReleaseDate);
            _info.text = string.Join("\n", lines);
        }

        public void ApplyToScene() => RefreshBadge();

        protected override void RequestClose() => BackRequested?.Invoke();
    }

    /// <summary>
    /// Unity-side credits additions, appended after the synced data/release/credits.json (which is never edited here).
    /// Fonts/LICENSES.md requires the in-game credits to name the bundled fonts.
    /// </summary>
    public static class CreditsOverlay
    {
        public static readonly IReadOnlyList<GdDict> Entries = new[]
        {
            new GdDict
            {
                { "role", "Typography" },
                { "name", "Inter by Rasmus Andersson" },
                { "license", "SIL Open Font License 1.1" },
                { "note", "Interface text. Inter 4.1, https://github.com/rsms/inter" },
            },
            new GdDict
            {
                { "role", "Typography" },
                { "name", "JetBrains Mono by JetBrains" },
                { "license", "SIL Open Font License 1.1" },
                { "note", "Quantities and readings. JetBrains Mono 2.304, https://github.com/JetBrains/JetBrainsMono" },
            },
        };
    }

    /// <summary>
    /// REQ-RL-009 credits (records screen, PAUSED): data/release/credits.json as a scrolling list, plus the Unity-side
    /// <see cref="CreditsOverlay"/> (Typography). Back / dismiss emits <see cref="CreditsDismissed"/>.
    /// </summary>
    public sealed class CreditsScreen : SurfacePanel
    {
        public const string CreditsPath = "res://data/release/credits.json";

        readonly List<GdDict> _entries = new List<GdDict>();
        readonly ScrollView _scroll;

        public event Action CreditsDismissed;

        public CreditsScreen() : base("CREDITS & ATTRIBUTION", SurfaceTime.Paused)
        {
            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.AddToClassList("ss-credits");
            _scroll.focusable = true;
            _scroll.tabIndex = 0;
            UiFocus.Tag(_scroll, "credits:list");
            Body.Add(_scroll);
        }

        public override string SurfaceId => "credits";

        /// <summary>Loads credits.json (or <paramref name="jsonText"/>) then appends the overlay. Returns the entry count.</summary>
        public int LoadCatalog(string jsonText = "")
        {
            GdDict parsed = jsonText.Length == 0 ? CatalogRegistry.LoadDict(CreditsPath) : GdJson.ParseString(jsonText) as GdDict;
            if (parsed == null) return 0;
            if (!(parsed.Get("credits", null) is GdArray list)) return 0;
            _entries.Clear();
            foreach (object entry in list) AddEntry(entry as GdDict);
            foreach (GdDict overlay in CreditsOverlay.Entries) AddEntry(overlay);
            Render();
            return _entries.Count;
        }

        void AddEntry(GdDict dict)
        {
            if (dict == null) return;
            string role = V.Str(dict.Get("role", ""));
            string name = V.Str(dict.Get("name", ""));
            if (role.Length == 0 || name.Length == 0) return;
            _entries.Add(new GdDict
            {
                { "role", role },
                { "name", name },
                { "license", V.Str(dict.Get("license", "")) },
                { "note", V.Str(dict.Get("note", "")) },
            });
        }

        public List<GdDict> GetEntries()
        {
            var copy = new List<GdDict>();
            foreach (GdDict e in _entries) copy.Add(e.DeepCopy());
            return copy;
        }

        public int GetEntryCount() => _entries.Count;

        public void Dismiss() => CreditsDismissed?.Invoke();

        public IEnumerable<string> VisibleTexts()
        {
            foreach (Label l in _scroll.Query<Label>().ToList()) yield return l.text;
        }

        void Render()
        {
            _scroll.Clear();
            foreach (GdDict e in _entries)
            {
                var block = UiFactory.Box("ss-credit");
                block.Add(UiFactory.Text(V.Str(e["role"]) + " — " + V.Str(e["name"]), "ss-credit__title"));
                string license = V.Str(e["license"]);
                if (license.Length != 0) block.Add(UiFactory.Text(license, UiClasses.LabelSecondary));
                string note = V.Str(e["note"]);
                if (note.Length != 0) block.Add(UiFactory.Text(note, UiClasses.LabelSecondary));
                _scroll.Add(block);
            }
        }

        protected override bool OnCommand(UiCommand command)
        {
            if (command == UiCommand.Up || command == UiCommand.Down)
            {
                Vector2 offset = _scroll.scrollOffset;
                offset.y += command == UiCommand.Up ? -48f : 48f;
                _scroll.scrollOffset = offset;
                return true;
            }
            return false;
        }

        protected override void RequestClose() => Dismiss();

        protected override VisualElement InitialFocusElement() => _scroll;
    }
}
