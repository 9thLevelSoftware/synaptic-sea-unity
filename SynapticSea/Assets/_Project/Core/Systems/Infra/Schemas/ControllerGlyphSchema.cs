// Ported from scripts/schemas/controller_glyph_schema.gd @ 96ecb2b0
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Static validation for <see cref="ControllerGlyphState"/> catalogs (REQ-UI-007 / ADR-0033).</summary>
    public static class ControllerGlyphSchema
    {
        public const string SchemaVersion = "controller-glyphs-1";
        public static readonly GdArray ValidSchemes = GdArray.Of("auto", "keyboard", "gamepad_xbox", "gamepad_ps");

        public static bool Validate(object table)
        {
            if (table == null || !(table is GdDict dict))
            {
                CoreServices.Log.Error("ControllerGlyphSchema: table must be a Dictionary; got " + InfraCompat.TypeOf(table));
                return false;
            }
            if (V.Str(dict.Get("version", "")) != SchemaVersion)
            {
                CoreServices.Log.Error("ControllerGlyphSchema: version mismatch (expected " + SchemaVersion + ")");
                return false;
            }
            string defaultScheme = V.Str(dict.Get("default_scheme", "auto"));
            if (!ValidSchemes.Contains(defaultScheme))
            {
                CoreServices.Log.Error("ControllerGlyphSchema: default_scheme '" + defaultScheme + "' is invalid");
                return false;
            }
            string fallbackScheme = V.Str(dict.Get("fallback_scheme", "keyboard"));
            if (!ValidSchemes.Contains(fallbackScheme))
            {
                CoreServices.Log.Error("ControllerGlyphSchema: fallback_scheme '" + fallbackScheme + "' is invalid");
                return false;
            }
            object actionsVariant = dict.Get("actions", null);
            if (!(actionsVariant is GdArray actions))
            {
                CoreServices.Log.Error("ControllerGlyphSchema: 'actions' must be an Array");
                return false;
            }
            var seenActions = new GdDict();
            foreach (var action in actions)
            {
                if (!(action is GdDict actionDict))
                {
                    CoreServices.Log.Error("ControllerGlyphSchema: action entry must be a Dictionary");
                    return false;
                }
                string actionName = V.Str(actionDict.Get("action", ""));
                if (actionName.Length == 0)
                {
                    CoreServices.Log.Error("ControllerGlyphSchema: action missing 'action'");
                    return false;
                }
                if (seenActions.Has(actionName))
                {
                    CoreServices.Log.Error("ControllerGlyphSchema: duplicate action '" + actionName + "'");
                    return false;
                }
                seenActions[actionName] = true;
                object schemes = actionDict.Get("schemes", null);
                if (!(schemes is GdDict schemesDict))
                {
                    CoreServices.Log.Error("ControllerGlyphSchema: action '" + actionName + "' missing 'schemes' Dictionary");
                    return false;
                }
                foreach (var schemeName in schemesDict.Keys)
                {
                    string schemeStr = V.Str(schemeName);
                    if (!ValidSchemes.Contains(schemeStr) && schemeStr != "auto")
                    {
                        CoreServices.Log.Error("ControllerGlyphSchema: action '" + actionName + "' has invalid scheme '" + schemeStr + "'");
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
