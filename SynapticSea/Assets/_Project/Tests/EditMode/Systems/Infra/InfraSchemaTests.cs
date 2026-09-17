using System;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>
    /// The five Infra schema validators (scripts/schemas/*.gd). Each rejection rule from the Godot header comments is
    /// checked for its verdict and its exact <c>push_error</c> text (the Godot message, with <c>typeof()</c> integers),
    /// and the shipped <c>data/ui</c> catalogs must validate without errors.
    /// </summary>
    public class InfraSchemaTests
    {
        CollectingLog _log;
        ILog _previousLog;
        IResourceReader _previousReader;

        [SetUp]
        public void SetUp()
        {
            _previousLog = CoreServices.Log;
            _previousReader = CoreServices.Resources;
            CoreServices.Log = _log = new CollectingLog();
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Log = _previousLog;
            CoreServices.Resources = _previousReader;
        }

        void Rejects(Func<bool> validate, string error, string what)
        {
            _log.Errors.Clear();
            Assert.IsFalse(validate(), $"{what}: expected rejection");
            CollectionAssert.AreEqual(new[] { error }, _log.Errors, what);
        }

        void Accepts(Func<bool> validate, string what)
        {
            _log.Errors.Clear();
            Assert.IsTrue(validate(), $"{what}: expected acceptance; errors: {string.Join(" | ", _log.Errors)}");
            CollectionAssert.IsEmpty(_log.Errors, what);
        }

        [Test]
        public void ShippedUiCatalogsValidate()
        {
            Accepts(() => ControllerGlyphSchema.Validate(CatalogRegistry.LoadDict("res://data/ui/input_glyphs.json")), "input_glyphs.json");
            Accepts(() => MenuStateSchema.ValidateCatalog(CatalogRegistry.LoadDict("res://data/ui/menu_definitions.json")), "menu_definitions.json");
            Accepts(() => TooltipSchema.Validate(CatalogRegistry.LoadDict("res://data/ui/tooltip_catalog.json")), "tooltip_catalog.json");
            Accepts(() => TutorialStateSchema.Validate(CatalogRegistry.LoadDict("res://data/ui/tutorial_triggers.json")), "tutorial_triggers.json");
            Accepts(() => SettingsStateSchema.Validate(SettingsStateSchema.DefaultPayload()), "default settings payload");
        }

        // ------------------------------------------------------------------ SettingsStateSchema

        static GdDict Settings(params (string key, object value)[] overrides)
        {
            GdDict d = SettingsStateSchema.DefaultPayload();
            foreach (var (key, value) in overrides) d[key] = value;
            return d;
        }

        [Test]
        public void Settings_RejectionRules()
        {
            const string P = "SettingsStateSchema: ";
            Rejects(() => SettingsStateSchema.Validate("x"), P + "payload must be a Dictionary; got 4", "string payload");
            Rejects(() => SettingsStateSchema.Validate(Settings(("schema", "settings-state-0"))), P + "schema version mismatch (expected settings-state-1)", "schema");
            Rejects(() => SettingsStateSchema.Validate(new GdDict()), P + "schema version mismatch (expected settings-state-1)", "no schema key");
            Rejects(() => SettingsStateSchema.Validate(Settings(("text_scale", "1.5"))), P + "text_scale must be a number", "string scale");
            Rejects(() => SettingsStateSchema.Validate(Settings(("text_scale", 2.5))), P + "text_scale 2.500 out of range [1.0, 2.0]", "scale high");
            Rejects(() => SettingsStateSchema.Validate(Settings(("text_scale", 0L))), P + "text_scale 0.000 out of range [1.0, 2.0]", "int scale low");
            Rejects(() => SettingsStateSchema.Validate(Settings(("colorblind_mode", "mono"))), P + "colorblind_mode 'mono' is not in allowlist", "colorblind");
            Rejects(() => SettingsStateSchema.Validate(Settings(("difficulty", "easy"))), P + "difficulty 'easy' is not in allowlist", "difficulty");
            Rejects(() => SettingsStateSchema.Validate(Settings(("glyph_scheme", "gamepad_switch"))), P + "glyph_scheme 'gamepad_switch' is not in allowlist", "glyph");
            Rejects(() => SettingsStateSchema.Validate(Settings(("preset_id", ""))), P + "preset_id must not be empty", "preset");
            Rejects(() => SettingsStateSchema.Validate(Settings(("captions", 1L))), P + "captions must be a bool (got 2)", "int bool");
            Rejects(() => SettingsStateSchema.Validate(Settings(("hold_to_tap", "true"))), P + "hold_to_tap must be a bool (got 4)", "string bool");
        }

        [Test]
        public void Settings_AcceptsBoundsIntsMissingAndExtraFields()
        {
            Accepts(() => SettingsStateSchema.Validate(Settings(("text_scale", 2L))), "int text_scale at the bound");
            Accepts(() => SettingsStateSchema.Validate(Settings(("text_scale", 1.0))), "lower bound");
            Accepts(() => SettingsStateSchema.Validate(new GdDict { { "schema", "settings-state-1" } }), "missing fields take defaults");
            Accepts(() => SettingsStateSchema.Validate(Settings(("future_field", new GdArray()))), "unknown fields are ignored");
        }

        [Test]
        public void Settings_SanitizeFillsClampsAndKeepsValidFields()
        {
            Assert.IsTrue(V.VariantEquals(SettingsStateSchema.DefaultPayload(), SettingsStateSchema.Sanitize(null)), "null -> defaults");
            Assert.IsTrue(V.VariantEquals(SettingsStateSchema.DefaultPayload(), SettingsStateSchema.Sanitize(GdArray.Of(1L))), "non-dict -> defaults");

            GdDict s = SettingsStateSchema.Sanitize(new GdDict
            {
                { "text_scale", 0L }, { "colorblind_mode", "tritanopia" }, { "glyph_scheme", "nope" },
                { "preset_id", "" }, { "motion_reduce", true }, { "captions", 0L }, { "extra", 1L },
            });
            Assert.IsInstanceOf<double>(s["text_scale"], "clampf returns a float even for an int input");
            Assert.AreEqual(1.0, s["text_scale"]);
            Assert.AreEqual("tritanopia", s["colorblind_mode"]);
            Assert.AreEqual("auto", s["glyph_scheme"], "invalid scheme falls back to the default");
            Assert.AreEqual("default", s["preset_id"], "empty preset keeps the default");
            Assert.AreEqual(true, s["motion_reduce"]);
            Assert.AreEqual(true, s["captions"], "non-bool keeps the default");
            Assert.IsFalse(s.Has("extra"), "sanitize only emits schema fields");
            Accepts(() => SettingsStateSchema.Validate(s), "sanitized payload");
        }

        // ------------------------------------------------------------------ ControllerGlyphSchema

        static GdDict Glyphs(object actions = null) => new GdDict
        {
            { "version", "controller-glyphs-1" },
            { "default_scheme", "auto" },
            { "fallback_scheme", "keyboard" },
            { "actions", actions ?? new GdArray { Action("interact", "keyboard", "gamepad_xbox") } },
        };

        static GdDict Action(string name, params string[] schemes)
        {
            var d = new GdDict();
            foreach (string s in schemes) d[s] = "[" + s + "]";
            return new GdDict { { "action", name }, { "schemes", d } };
        }

        [Test]
        public void ControllerGlyphs_RejectionRules()
        {
            const string P = "ControllerGlyphSchema: ";
            Accepts(() => ControllerGlyphSchema.Validate(Glyphs(new GdArray { Action("interact", "auto", "gamepad_ps") })), "auto is a valid per-action scheme");
            Rejects(() => ControllerGlyphSchema.Validate(null), P + "table must be a Dictionary; got 0", "null");
            Rejects(() => ControllerGlyphSchema.Validate(new GdDict { { "version", "controller-glyphs-2" } }), P + "version mismatch (expected controller-glyphs-1)", "version");

            GdDict badDefault = Glyphs();
            badDefault["default_scheme"] = "touch";
            Rejects(() => ControllerGlyphSchema.Validate(badDefault), P + "default_scheme 'touch' is invalid", "default_scheme");
            GdDict badFallback = Glyphs();
            badFallback["fallback_scheme"] = "";
            Rejects(() => ControllerGlyphSchema.Validate(badFallback), P + "fallback_scheme '' is invalid", "fallback_scheme");

            Rejects(() => ControllerGlyphSchema.Validate(Glyphs(new GdDict())), P + "'actions' must be an Array", "actions dict");
            Rejects(() => ControllerGlyphSchema.Validate(Glyphs(GdArray.Of("interact"))), P + "action entry must be a Dictionary", "action string");
            Rejects(() => ControllerGlyphSchema.Validate(Glyphs(new GdArray { new GdDict { { "schemes", new GdDict() } } })), P + "action missing 'action'", "no name");
            Rejects(() => ControllerGlyphSchema.Validate(Glyphs(new GdArray { Action("a", "keyboard"), Action("a", "gamepad_ps") })), P + "duplicate action 'a'", "duplicate");
            Rejects(() => ControllerGlyphSchema.Validate(Glyphs(new GdArray { new GdDict { { "action", "a" }, { "schemes", GdArray.Of("keyboard") } } })), P + "action 'a' missing 'schemes' Dictionary", "schemes array");
            Rejects(() => ControllerGlyphSchema.Validate(Glyphs(new GdArray { Action("a", "keyboard", "steam_deck") })), P + "action 'a' has invalid scheme 'steam_deck'", "bad scheme");
        }

        // ------------------------------------------------------------------ MenuStateSchema

        static GdDict Menu(string id, params GdDict[] items) => new GdDict { { "id", id }, { "title", "T" }, { "items", new GdArray(items) } };

        static GdDict Item(string id, string kind = "command") => new GdDict { { "id", id }, { "label", "L" }, { "kind", kind } };

        static GdDict Menus(params GdDict[] menus) => new GdDict { { "menus", new GdArray(menus) } };

        [Test]
        public void MenuState_RejectionRules()
        {
            const string P = "MenuStateSchema: ";
            Accepts(() => MenuStateSchema.ValidateCatalog(Menus(Menu("a", Item("x"), Item("y", "slider")), Menu("b", Item("x", "toggle")))), "item ids are per menu");
            Assert.IsTrue(MenuStateSchema.IsValidKind("submenu"));
            Assert.IsFalse(MenuStateSchema.IsValidKind("Command"));

            Rejects(() => MenuStateSchema.ValidateCatalog(GdArray.Of()), P + "catalog must be a Dictionary; got 28", "array root");
            Rejects(() => MenuStateSchema.ValidateCatalog(new GdDict()), P + "'menus' must be an Array", "no menus");
            Rejects(() => MenuStateSchema.ValidateCatalog(new GdDict { { "menus", GdArray.Of(1L) } }), P + "menu entry must be a Dictionary", "menu int");
            Rejects(() => MenuStateSchema.ValidateCatalog(Menus(Menu("", Item("x")))), P + "menu missing 'id'", "menu id");
            Rejects(() => MenuStateSchema.ValidateCatalog(Menus(Menu("a", Item("x")), Menu("a", Item("y")))), P + "duplicate menu id 'a'", "dup menu");
            Rejects(() => MenuStateSchema.ValidateCatalog(Menus(new GdDict { { "id", "a" }, { "items", GdArray.Of(Item("x")) } })), P + "menu 'a' missing 'title'", "title");
            Rejects(() => MenuStateSchema.ValidateCatalog(Menus(new GdDict { { "id", "a" }, { "title", "T" } })), P + "menu 'a' 'items' must be an Array", "items");
            Rejects(() => MenuStateSchema.ValidateCatalog(Menus(Menu("a"))), P + "menu 'a' has empty 'items'", "empty items");
            Rejects(() => MenuStateSchema.ValidateCatalog(Menus(new GdDict { { "id", "a" }, { "title", "T" }, { "items", GdArray.Of("x") } })), P + "item in menu 'a' must be a Dictionary", "item string");
            Rejects(() => MenuStateSchema.ValidateCatalog(Menus(Menu("a", Item("")))), P + "item in menu 'a' missing 'id'", "item id");
            Rejects(() => MenuStateSchema.ValidateCatalog(Menus(Menu("a", Item("x"), Item("x")))), P + "duplicate item id 'x' in menu 'a'", "dup item");
            Rejects(() => MenuStateSchema.ValidateCatalog(Menus(Menu("a", new GdDict { { "id", "x" }, { "kind", "command" } }))), P + "item 'x' in menu 'a' missing 'label'", "label");
            Rejects(() => MenuStateSchema.ValidateCatalog(Menus(Menu("a", Item("x", "button")))), P + "item 'x' in menu 'a' has invalid kind 'button'", "kind");
        }

        // ------------------------------------------------------------------ TooltipSchema

        static GdDict Tip(string id, string kind = "item", string subject = "circuit_board", string title = "Circuit Board") =>
            new GdDict { { "id", id }, { "subject_kind", kind }, { "subject_id", subject }, { "title", title } };

        static GdDict Tips(params object[] entries) => new GdDict { { "version", "tooltip-catalog-1" }, { "entries", new GdArray(entries) } };

        [Test]
        public void Tooltip_RejectionRules()
        {
            const string P = "TooltipSchema: ";
            Accepts(() => TooltipSchema.Validate(Tips(Tip("a"), Tip("b"))), "body/footer optional; the same subject may repeat");
            Rejects(() => TooltipSchema.Validate(42L), P + "catalog must be a Dictionary; got 2", "int root");
            Rejects(() => TooltipSchema.Validate(new GdDict { { "version", "tooltip-catalog-0" }, { "entries", new GdArray() } }), P + "version mismatch (expected tooltip-catalog-1)", "version");
            Rejects(() => TooltipSchema.Validate(new GdDict { { "version", "tooltip-catalog-1" } }), P + "'entries' must be an Array", "entries");
            Rejects(() => TooltipSchema.Validate(Tips("a")), P + "entry must be a Dictionary", "entry string");
            Rejects(() => TooltipSchema.Validate(Tips(Tip(""))), P + "entry missing 'id'", "id");
            Rejects(() => TooltipSchema.Validate(Tips(Tip("a"), Tip("a", subject: "door"))), P + "duplicate entry id 'a'", "dup");
            Rejects(() => TooltipSchema.Validate(Tips(Tip("a", kind: ""))), P + "entry 'a' missing 'subject_kind'", "kind");
            Rejects(() => TooltipSchema.Validate(Tips(Tip("a", subject: ""))), P + "entry 'a' missing 'subject_id'", "subject");
            Rejects(() => TooltipSchema.Validate(Tips(Tip("a", title: ""))), P + "entry 'a' missing 'title'", "title");
        }

        // ------------------------------------------------------------------ TutorialStateSchema

        static GdDict Tut(string id, string evt = "player_moved", string target = "any", string title = "T", string body = "B") =>
            new GdDict { { "id", id }, { "trigger_event", evt }, { "trigger_target", target }, { "title", title }, { "body", body } };

        static GdDict Tuts(params object[] tutorials) => new GdDict { { "version", "tutorial-triggers-1" }, { "tutorials", new GdArray(tutorials) } };

        [Test]
        public void Tutorial_RejectionRules()
        {
            const string P = "TutorialStateSchema: ";
            Accepts(() => TutorialStateSchema.Validate(Tuts(Tut("a"), Tut("b", target: "door"), Tut("c", evt: "item_picked"))), "same event with another target is distinct");
            Rejects(() => TutorialStateSchema.Validate("catalog"), P + "catalog must be a Dictionary; got 4", "string root");
            Rejects(() => TutorialStateSchema.Validate(new GdDict { { "tutorials", new GdArray() } }), P + "version mismatch (expected tutorial-triggers-1)", "version");
            Rejects(() => TutorialStateSchema.Validate(new GdDict { { "version", "tutorial-triggers-1" }, { "tutorials", new GdDict() } }), P + "'tutorials' must be an Array", "tutorials");
            Rejects(() => TutorialStateSchema.Validate(Tuts(1L)), P + "tutorial must be a Dictionary", "int entry");
            Rejects(() => TutorialStateSchema.Validate(Tuts(Tut(""))), P + "tutorial missing 'id'", "id");
            Rejects(() => TutorialStateSchema.Validate(Tuts(Tut("a"), Tut("a", target: "door"))), P + "duplicate tutorial id 'a'", "dup id");
            Rejects(() => TutorialStateSchema.Validate(Tuts(Tut("a", evt: ""))), P + "tutorial 'a' missing 'trigger_event'", "event");
            Rejects(() => TutorialStateSchema.Validate(Tuts(Tut("a", target: ""))), P + "tutorial 'a' missing 'trigger_target'", "target");
            Rejects(() => TutorialStateSchema.Validate(Tuts(Tut("a"), Tut("b"))), P + "duplicate trigger (event, target)=(player_moved, any)", "dup trigger");
            Rejects(() => TutorialStateSchema.Validate(Tuts(Tut("a", title: ""))), P + "tutorial 'a' missing 'title'", "title");
            Rejects(() => TutorialStateSchema.Validate(Tuts(Tut("a", body: ""))), P + "tutorial 'a' missing 'body'", "body");
        }
    }
}
