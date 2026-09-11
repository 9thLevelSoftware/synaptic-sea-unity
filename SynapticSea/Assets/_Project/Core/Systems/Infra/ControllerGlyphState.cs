// Ported from scripts/systems/controller_glyph_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure controller glyph resolver (REQ-UI-007 / ADR-0033). Owns per-scheme glyph maps loaded from
    /// <c>data/ui/input_glyphs.json</c>; <see cref="GlyphFor"/> falls back to the fallback scheme.
    /// </summary>
    public class ControllerGlyphState : IStatusLineProvider
    {
        public const string SchemaVersion = "controller-glyph-state-1";
        public const string SaveKey = "controller_glyph_state";
        public const string FallbackScheme = "keyboard";
        public static readonly GdArray ValidSchemes = GdArray.Of("auto", "keyboard", "gamepad_xbox", "gamepad_ps");

        string _defaultScheme = "auto";
        string _fallbackScheme = FallbackScheme;
        readonly GdDict _glyphs = new GdDict();       // action_name -> {scheme -> glyph}
        readonly GdArray _actionNames = new GdArray();
        readonly GdDict _bindings = new GdDict();     // action_name -> Array of keycodes (int)

        /// <summary>
        /// RUNTIME: replaces <c>Input.get_connected_joypads().size()</c>. The Runtime layer supplies the
        /// connected-gamepad count (e.g. from the Input System); null means "no gamepads" (headless / tests).
        /// </summary>
        public Func<int> ConnectedJoypadCount;

        public ControllerGlyphState(Func<int> connectedJoypadCount = null)
        {
            ConnectedJoypadCount = connectedJoypadCount;
        }

        public bool Configure(GdDict glyphTable, GdDict bindingsTable = null)
        {
            if (!ControllerGlyphSchema.Validate(glyphTable)) return false;
            GdDict dict = glyphTable;
            _defaultScheme = V.Str(dict.Get("default_scheme", "auto"));
            _fallbackScheme = V.Str(dict.Get("fallback_scheme", FallbackScheme));
            _glyphs.Clear();
            _actionNames.Clear();
            foreach (var action in (GdArray)dict.Get("actions", new GdArray()))
            {
                var actionDict = (GdDict)action;
                string actionName = V.Str(actionDict.Get("action", ""));
                var schemesDict = (GdDict)actionDict.Get("schemes", new GdDict());
                _actionNames.Add(actionName);
                var schemeGlyphs = new GdDict();
                _glyphs[actionName] = schemeGlyphs;
                foreach (var schemeName in schemesDict.Keys)
                    schemeGlyphs[V.Str(schemeName)] = V.Str(schemesDict[schemeName]);
            }
            _bindings.Clear();
            if (bindingsTable != null)
            {
                foreach (var actionName in bindingsTable.Keys)
                {
                    object bindingsVariant = bindingsTable[actionName];
                    var bindingsList = new GdArray();
                    if (bindingsVariant is GdArray keycodes)
                    {
                        foreach (var keycode in keycodes) bindingsList.Add(V.I64(keycode));
                    }
                    _bindings[V.Str(actionName)] = bindingsList;
                }
            }
            return true;
        }

        public void SetBindings(GdDict bindingsTable)
        {
            _bindings.Clear();
            if (bindingsTable == null) return;
            foreach (var actionName in bindingsTable.Keys)
            {
                object bindingsVariant = bindingsTable[actionName];
                var bindingsList = new GdArray();
                if (bindingsVariant is GdArray keycodes)
                {
                    foreach (var keycode in keycodes) bindingsList.Add(V.I64(keycode));
                }
                _bindings[V.Str(actionName)] = bindingsList;
            }
        }

        public GdArray GetActionNames() => _actionNames.ShallowCopy();

        public string GetDefaultScheme() => _defaultScheme;

        public string GetFallbackScheme() => _fallbackScheme;

        public bool IsKnownAction(string actionName) => _actionNames.Contains(actionName);

        public GdArray GetBindingsFor(string actionName)
        {
            if (!_bindings.Has(actionName)) return new GdArray();
            return ((GdArray)_bindings[actionName]).ShallowCopy();
        }

        /// <summary>
        /// Glyph text for an action under the requested scheme, falling back to the fallback scheme.
        /// Returns "" for unknown actions.
        /// </summary>
        public string GlyphFor(string actionName, string scheme = "auto")
        {
            if (!IsKnownAction(actionName)) return "";
            GdDict schemesDict = _glyphs.Get(actionName, new GdDict()) as GdDict ?? new GdDict();
            if (schemesDict.Has(scheme)) return V.Str(schemesDict[scheme]);
            if (schemesDict.Has(_fallbackScheme)) return V.Str(schemesDict[_fallbackScheme]);
            return "";
        }

        /// <summary>
        /// <c>auto</c> becomes <c>gamepad_xbox</c> when any gamepad is connected, else the fallback scheme.
        /// </summary>
        public string ResolveScheme(string scheme)
        {
            if (scheme != "auto") return scheme;
            // RUNTIME: Input.get_connected_joypads().size() > 0
            int joypads = ConnectedJoypadCount != null ? ConnectedJoypadCount() : 0;
            if (joypads > 0) return "gamepad_xbox";
            return _fallbackScheme;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "schema", SchemaVersion },
                { "default_scheme", _defaultScheme },
                { "fallback_scheme", _fallbackScheme },
                { "action_count", _actionNames.Count },
                { "bindings_count", _bindings.Count },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null) return false;
            if (V.Str(summary.Get("schema", "")) != SchemaVersion) return false;
            _defaultScheme = V.Str(summary.Get("default_scheme", "auto"));
            _fallbackScheme = V.Str(summary.Get("fallback_scheme", FallbackScheme));
            if (!ValidSchemes.Contains(_defaultScheme)) _defaultScheme = "auto";
            if (!ValidSchemes.Contains(_fallbackScheme)) _fallbackScheme = FallbackScheme;
            return true;
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add(InfraCompat.Fmt("ControllerGlyphState: actions={0} default={1} fallback={2} bindings={3}",
                _actionNames.Count, _defaultScheme, _fallbackScheme, _bindings.Count));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();
    }
}
