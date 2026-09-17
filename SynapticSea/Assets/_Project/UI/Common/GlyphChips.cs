using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
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
            var state = new ControllerGlyphState(connectedJoypadCount);
            return state.Configure(table) ? state : null;
        }
    }
}
