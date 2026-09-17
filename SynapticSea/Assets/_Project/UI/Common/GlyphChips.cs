using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Input glyphs as text chips, resolved from <c>data/ui/input_glyphs.json</c> through <see cref="ControllerGlyphState"/>.
    /// The glyph data uses UI-facing names for the two forward/back movement actions; the Input System asset uses the
    /// Godot InputMap ids, so <c>move_forward</c> reads the <c>move_up</c> entry and <c>move_back</c> reads <c>move_down</c>.
    /// </summary>
    public static class GlyphChips
    {
        public const string GlyphTablePath = "res://data/ui/input_glyphs.json";

        /// <summary>Input action id → glyph-table action name.</summary>
        public static readonly IReadOnlyDictionary<string, string> ActionToGlyphKey = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "move_forward", "move_up" },
            { "move_back", "move_down" },
        };

        /// <summary>
        /// Unity-side glyph rows for Player/Panels actions the synced Godot table (<c>input_glyphs.json</c>) never listed.
        /// They mirror the bindings in <c>SynapticSea.inputactions</c>; an empty gamepad glyph means "no gamepad binding"
        /// (the chip hides). Rows already present in the data table win.
        /// </summary>
        public static readonly IReadOnlyList<(string action, string keyboard, string xbox, string ps)> UnitySupplement = new[]
        {
            ("attack_primary", "[F]", "[RT]", "[R2]"),
            ("reload_weapon", "[R]", "[X]", "[Square]"),
            ("crouch", "[Ctrl]", "[B]", "[Circle]"),
            ("field_craft", "[C]", "[D-Down]", "[D-Down]"),
            ("toggle_ship_mod", "[U]", "", ""),
            ("toggle_wounds", "[O]", "", ""),
            ("hotbar_1", "[1]", "[D-Left]", "[D-Left]"),
            ("hotbar_2", "[2]", "[D-Up]", "[D-Up]"),
            ("hotbar_3", "[3]", "[D-Right]", "[D-Right]"),
            ("quicksave_run", "[F6]", "", ""),
        };

        /// <summary>A copy of <paramref name="table"/> with the <see cref="UnitySupplement"/> rows appended (null → null).</summary>
        public static GdDict WithUnitySupplement(GdDict table)
        {
            if (table == null) return new GdDict();
            GdDict copy = table.DeepCopy();
            var actions = copy.Get("actions", null) as GdArray;
            if (actions == null) return copy;
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (object entry in actions)
                if (entry is GdDict d) known.Add(V.Str(d.Get("action", "")));
            foreach (var row in UnitySupplement)
            {
                if (known.Contains(row.action)) continue;
                actions.Add(new GdDict
                {
                    { "action", row.action },
                    { "schemes", new GdDict { { "keyboard", row.keyboard }, { "gamepad_xbox", row.xbox }, { "gamepad_ps", row.ps } } },
                });
            }
            return copy;
        }

        public static string GlyphKeyFor(string inputActionId) =>
            inputActionId != null && ActionToGlyphKey.TryGetValue(inputActionId, out string key) ? key : inputActionId ?? "";

        /// <summary>Glyph text (e.g. "[E]") for an input action id under <paramref name="scheme"/> ("auto" resolves by device).</summary>
        public static string GlyphText(ControllerGlyphState glyphs, string inputActionId, string scheme = "auto")
        {
            if (glyphs == null) return "";
            return glyphs.GlyphFor(GlyphKeyFor(inputActionId), glyphs.ResolveScheme(scheme));
        }

        /// <summary>A mono text chip (never an image: remapped bindings supply the text).</summary>
        public static Label Chip(string glyph)
        {
            var chip = UiFactory.Text(glyph ?? "", UiClasses.Glyph, UiClasses.LabelMono);
            UiFactory.SetShown(chip, !string.IsNullOrEmpty(glyph));
            return chip;
        }

        /// <summary>Loads the glyph table through CatalogRegistry; null when the data is missing.</summary>
        public static ControllerGlyphState LoadDefault(Func<int> connectedJoypadCount = null)
        {
            var table = CatalogRegistry.LoadDict(GlyphTablePath);
            if (table == null) return null;
            table = WithUnitySupplement(table);
            var state = new ControllerGlyphState(connectedJoypadCount);
            return state.Configure(table) ? state : null;
        }
    }
}
