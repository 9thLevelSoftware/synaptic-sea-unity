// Ported from scripts/ui/hotbar_panel.gd, scripts/ui/tooltip_panel.gd, scripts/ui/tutorial_overlay_panel.gd @ 96ecb2b0
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Quick-use slots. Spec: consumable slots live inside the lower-left cluster, not in a separate full-width bar
    /// (Godot drew a 520×72 bottom-centre bar). The coordinator composes the slots exactly as Godot did and pushes them
    /// here; <see cref="HotbarText"/> keeps the Godot composite string.
    /// </summary>
    public sealed class HotbarStrip : VisualElement
    {
        readonly Label _glyph;
        readonly VisualElement _slots;
        readonly List<string> _slotTexts = new List<string>();

        public string HotbarText { get; private set; } = "";
        public IReadOnlyList<string> SlotTexts => _slotTexts;
        public int SelectedIndex { get; private set; }

        public HotbarStrip()
        {
            name = "hud-hotbar";
            AddToClassList("hud-hotbar");
            pickingMode = PickingMode.Ignore;
            _glyph = GlyphChips.Chip("");
            _slots = UiFactory.Box("hud-hotbar__slots");
            Add(_glyph);
            Add(_slots);
        }

        bool _compact;
        string _useGlyph = "";

        /// <summary>Displayed chip texts.</summary>
        public IReadOnlyList<string> VisibleSlotTexts
        {
            get
            {
                var list = new List<string>();
                foreach (Label l in _slots.Query<Label>().ToList()) list.Add(l.text);
                return list;
            }
        }

        /// <param name="slots">Slot labels (without the [n] prefix).</param>
        /// <param name="useGlyph">Glyph text for the use/interact action.</param>
        public void SetSlots(IReadOnlyList<string> slots, int selectedIndex, string useGlyph)
        {
            SelectedIndex = selectedIndex;
            _useGlyph = useGlyph ?? "";
            _slotTexts.Clear();
            var composite = new List<string>();
            for (int i = 0; i < slots.Count; i++)
            {
                string prefix = "[" + (i + 1) + "]";
                if (i == selectedIndex) prefix = ">" + prefix;
                composite.Add(prefix + " " + slots[i]);
                _slotTexts.Add(slots[i]);
            }
            HotbarText = "HOTBAR  " + _useGlyph + "\n" + string.Join(" | ", composite);
            Render();
        }

        /// <summary>Disclosure at 1.5x/2x text: only the selected slot keeps its name; the others show their number.</summary>
        public void SetCompact(bool compact)
        {
            if (_compact == compact) return;
            _compact = compact;
            Render();
        }

        void Render()
        {
            _slots.Clear();
            for (int i = 0; i < _slotTexts.Count; i++)
            {
                // Empty slots collapse to their number (no "(empty)" text in the persistent cluster).
                bool empty = string.IsNullOrEmpty(_slotTexts[i]) || _slotTexts[i] == "(empty)";
                bool named = !empty && (!_compact || i == SelectedIndex);
                var chip = UiFactory.Text(named ? (i + 1) + " " + _slotTexts[i] : (i + 1).ToString(), "hud-hotbar__slot");
                chip.EnableInClassList("hud-hotbar__slot--selected", i == SelectedIndex);
                chip.EnableInClassList("hud-hotbar__slot--empty", empty);
                _slots.Add(chip);
            }
            _glyph.text = _useGlyph;
            UiFactory.SetShown(_glyph, _useGlyph.Length != 0);
        }
    }

    /// <summary>
    /// Context detail (Godot TooltipPanel). Spec: transient, shown in the context-prompt slot above the HUD cluster;
    /// hidden when the payload is empty.
    /// </summary>
    public sealed class TooltipCard : VisualElement
    {
        readonly Label _title;
        readonly Label _body;
        readonly Label _footer;

        public string Text { get; private set; } = "";

        public TooltipCard()
        {
            name = "hud-tooltip";
            AddToClassList(UiClasses.Panel);
            AddToClassList("hud-tooltip");
            pickingMode = PickingMode.Ignore;
            _title = UiFactory.Text("", "hud-tooltip__title");
            _body = UiFactory.Text("", UiClasses.LabelSecondary);
            _footer = UiFactory.Text("", UiClasses.LabelSecondary, UiClasses.LabelMono);
            Add(_title);
            Add(_body);
            Add(_footer);
            UiFactory.SetShown(this, false);
        }

        public bool IsShownNow => UiFactory.IsShown(this);

        public void SetPayload(string title, string body, string footer)
        {
            title = title ?? "";
            body = body ?? "";
            footer = footer ?? "";
            if (title.Length == 0 && body.Length == 0 && footer.Length == 0)
            {
                UiFactory.SetShown(this, false);
                Text = "";
                return;
            }
            UiFactory.SetShown(this, true);
            Text = title + "\n" + body + "\n" + footer;
            _title.text = title;
            _body.text = body;
            _footer.text = footer;
            UiFactory.SetShown(_footer, footer.Length != 0);
        }
    }

    /// <summary>Tutorial banner (Godot TutorialOverlayPanel), transient, above the HUD cluster; hidden when empty.</summary>
    public sealed class TutorialBanner : VisualElement
    {
        readonly Label _title;
        readonly Label _body;

        public string Text { get; private set; } = "";

        public TutorialBanner()
        {
            name = "hud-tutorial";
            AddToClassList(UiClasses.Panel);
            AddToClassList("hud-tutorial");
            pickingMode = PickingMode.Ignore;
            _title = UiFactory.Text("", "hud-tutorial__title");
            _body = UiFactory.Text("");
            Add(_title);
            Add(_body);
            UiFactory.SetShown(this, false);
        }

        public bool IsShownNow => UiFactory.IsShown(this);

        public void ShowTutorial(string title, string body)
        {
            title = title ?? "";
            body = body ?? "";
            if (title.Length == 0 && body.Length == 0)
            {
                UiFactory.SetShown(this, false);
                Text = "";
                return;
            }
            UiFactory.SetShown(this, true);
            Text = title + "\n" + body;
            _title.text = "● " + title;
            _body.text = body;
        }
    }
}
