using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>Shared setup for the group-C infra tests that read StreamingAssets data.</summary>
    public abstract class InfraDataTestBase
    {
        protected MemoryStorage Storage;
        protected ManualClock Clock;

        [SetUp]
        public void SetUpServices()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            Storage = new MemoryStorage();
            CoreServices.UserStorage = Storage;
            Clock = new ManualClock();
            CoreServices.Clock = Clock;
        }

        [TearDown]
        public void TearDownServices()
        {
            CatalogRegistry.Clear();
            CoreServices.UserStorage = new MemoryStorage();
            CoreServices.Clock = new SystemClock();
        }

        protected static GdDict Data(string resPath)
        {
            GdDict d = CatalogRegistry.LoadDict(resPath);
            Assert.IsNotNull(d, resPath);
            return d;
        }
    }

    public class SimKeysTests
    {
        [Test]
        public void HotPathKeys_AreTwelveStableWireNames()
        {
            var hot = SimKeys.VitalsHotPathKeys();
            Assert.AreEqual(12, hot.Count);
            Assert.AreEqual("fire_health_drain", SimKeys.FireHealthDrain);
            Assert.AreEqual("moving", SimKeys.Moving);
            Assert.Contains("sanity_stamina_recovery_mult", hot);
            Assert.AreEqual(55, SimKeys.AllKeys().Count);
        }
    }

    public class TuningCatalogTests : InfraDataTestBase
    {
        [Test]
        public void MissingKeys_ReturnDefaults_AndShellFlattens()
        {
            var cat = new TuningCatalog();
            Assert.AreEqual(3.25, cat.GetFloat("definitely.missing", 3.25));
            Assert.AreEqual(7, cat.GetInt("definitely.missing", 7));
            Assert.IsTrue(cat.LoadFile("res://data/balance/shell.json"));
            Assert.AreEqual(1.5, cat.GetFloat("tuning.example_float", 0.0), 1e-9);
            Assert.AreEqual(1, cat.GetInt("tuning.catalog_shell_version", 0));
            Assert.GreaterOrEqual(new TuningCatalog().LoadDefaults(), 1);
        }

        [Test]
        public void DirectoryLoad_ThenUserOverrideWins()
        {
            var cat = new TuningCatalog(Storage);
            Assert.GreaterOrEqual(cat.LoadDirectory("res://data/balance/"), 1);
            Storage.WriteText("user://tuning_catalog_smoke_override.json", "{\"tuning\":{\"example_float\":9.25}}");
            Assert.IsTrue(cat.LoadFile("user://tuning_catalog_smoke_override.json"));
            Assert.AreEqual(9.25, cat.GetFloat("tuning.example_float", 0.0), 1e-9);
        }
    }

    public class BuildMetadataStateTests : InfraDataTestBase
    {
        [Test]
        public void ManifestConfigures_AndUnknownKindIsFlagged()
        {
            var meta = new BuildMetadataState();
            meta.Configure(Data("res://data/release/build_metadata.json"));
            Assert.AreEqual("dev", meta.GetBuildKind());
            Assert.IsTrue(meta.IsBuildKindValidated());
            Assert.AreEqual("en", meta.GetDefaultLanguage());
            Assert.AreEqual(3, ((GdArray)meta.GetSummary()["demo_hub_unlocked_features"]).Count);

            var bogus = new BuildMetadataState();
            bogus.Configure(new GdDict { { "build_kind", "nightly" }, { "language_defaults", new GdArray() } });
            Assert.IsFalse(V.Bool(bogus.GetSummary()["build_kind_validated"]));
            Assert.AreEqual("Build: WARN unknown build_kind=nightly", bogus.GetStatusLines()[1]);
            Assert.AreEqual("en", bogus.GetDefaultLanguage());
        }
    }

    public class ControllerGlyphStateTests : InfraDataTestBase
    {
        static GdDict Glyphs() => new GdDict
        {
            { "version", "controller-glyphs-1" }, { "default_scheme", "auto" }, { "fallback_scheme", "keyboard" },
            { "actions", GdArray.Of(new GdDict
                {
                    { "action", "interact" },
                    { "schemes", new GdDict { { "keyboard", "[E]" }, { "gamepad_xbox", "[A]" }, { "gamepad_ps", "[Cross]" } } },
                }) },
        };

        [Test]
        public void SmokeConfig_ResolvesGlyphs_AndRoundTrips()
        {
            var state = new ControllerGlyphState();
            Assert.IsTrue(state.Configure(Glyphs(), new GdDict { { "interact", GdArray.Of(69L) } }));
            Assert.AreEqual("[E]", state.GlyphFor("interact", "keyboard"));
            Assert.AreEqual("[E]", state.GlyphFor("interact", "auto"));
            Assert.AreEqual("", state.GlyphFor("jump", "keyboard"));
            Assert.AreEqual("keyboard", state.ResolveScheme("keyboard"));
            Assert.AreEqual("keyboard", state.ResolveScheme("auto"));
            Assert.AreEqual("gamepad_xbox", new ControllerGlyphState(() => 1).ResolveScheme("auto"));

            var restored = new ControllerGlyphState();
            Assert.IsTrue(restored.ApplySummary(state.GetSummary()));
            Assert.AreEqual(state.GetDefaultScheme(), restored.GetDefaultScheme());
            Assert.AreEqual(state.GetFallbackScheme(), restored.GetFallbackScheme());
        }

        [Test]
        public void ShippedGlyphTable_Validates()
        {
            var state = new ControllerGlyphState();
            Assert.IsTrue(state.Configure(Data("res://data/ui/input_glyphs.json")));
            Assert.AreEqual("[A]", state.GlyphFor("interact", "gamepad_xbox"));
            var bad = Glyphs();
            bad["version"] = "controller-glyphs-0";
            Assert.IsFalse(new ControllerGlyphState().Configure(bad));
        }
    }

    public class DemoScopeGateTests : InfraDataTestBase
    {
        [Test]
        public void DemoBlocksManifest_ReleaseAllowsAll_ParamsExposed()
        {
            GdDict manifest = Data("res://data/release/demo_scope_manifest.json");
            var demoMeta = new BuildMetadataState();
            demoMeta.Configure(new GdDict { { "build_kind", "demo" }, { "version", "v0.1.0" }, { "store", "itch" } });
            var gate = new DemoScopeGate();
            gate.Configure(manifest, demoMeta);
            foreach (var fid in gate.ListBlocked()) Assert.IsFalse(gate.IsAllowed(V.Str(fid)));
            Assert.IsTrue(gate.IsAllowed("definitely.not.in.manifest"));
            Assert.IsFalse(gate.IsAllowed(""));
            Assert.AreEqual(6.0, gate.GetParams("cargo_hold.full_inventory").GetFloat("max_weight_kg"), 0.001);
            Assert.AreEqual(1200, gate.GetParams("long_run.persistence").GetInt("max_play_seconds"));
            Assert.IsTrue(gate.GetParams("hub.meta_progression").IsEmpty);

            var fullMeta = new BuildMetadataState();
            fullMeta.Configure(new GdDict { { "build_kind", "release" } });
            var full = new DemoScopeGate();
            full.Configure(manifest, fullMeta);
            foreach (var fid in full.ListBlocked()) Assert.IsTrue(full.IsAllowed(V.Str(fid)));
        }
    }

    public class LocalizationCatalogTests : InfraDataTestBase
    {
        [Test]
        public void FallbackRules_MatchSmoke()
        {
            var catalog = new LocalizationCatalog();
            catalog.Configure(Data("res://data/release/localization_catalog.json"));
            Assert.IsTrue(catalog.GetKnownLanguages().Contains("en"));
            Assert.AreEqual("Oxygen:", catalog.Translate("oxygen.label", "en"));
            Assert.AreEqual("Oxygen:", catalog.Translate("oxygen.label", "zz"));
            Assert.AreEqual("", catalog.Translate("definitely.not.in.catalog", "en"));
            Assert.AreEqual("DEFAULT TEXT", catalog.TranslateFallback("definitely.not.in.catalog", "DEFAULT TEXT", "en"));
            Assert.AreEqual(catalog.GetTranslationCount(), catalog.GetSummary()["translation_count"]);
        }
    }

    public class MenuStateTests
    {
        static GdDict Catalog() => new GdDict
        {
            { "menus", GdArray.Of(
                new GdDict { { "id", "main_menu" }, { "title", "Main" }, { "items", GdArray.Of(
                    new GdDict { { "id", "start" }, { "label", "Start" }, { "enabled", true }, { "kind", "command" } },
                    new GdDict { { "id", "continue" }, { "label", "Continue" }, { "enabled", false }, { "kind", "command" } }) } },
                new GdDict { { "id", "settings_menu" }, { "title", "Settings" }, { "items", GdArray.Of(
                    new GdDict { { "id", "text_scale" }, { "label", "Text Scale" }, { "enabled", true }, { "kind", "slider" } },
                    new GdDict { { "id", "back" }, { "label", "Back" }, { "enabled", true }, { "kind", "command" } }) } }) },
        };

        [Test]
        public void SmokeFlow_NavigatesConfirmsAndCancels()
        {
            var state = new MenuState();
            Assert.IsTrue(state.Configure(Catalog()));
            string lastMenu = null;
            state.MenuChanged += (now, prev) => lastMenu = now;
            Assert.IsTrue(state.OpenMenu("main_menu"));
            state.SetItemEnabled("main_menu", "continue", true);
            Assert.IsFalse(state.IsItemEnabled("main_menu", "continue")); // catalog default false ANDs the override
            Assert.IsTrue(state.OpenMenu("settings_menu"));
            Assert.AreEqual(1, state.Navigate(0, 1));
            Assert.AreEqual(1, state.Navigate(0, 5));
            Assert.AreEqual("back", state.Confirm());
            Assert.IsTrue(state.Cancel());
            Assert.AreEqual("main_menu", lastMenu);
        }

        [Test]
        public void Summary_RoundTrips()
        {
            var state = new MenuState();
            state.Configure(Catalog());
            state.OpenMenu("main_menu");
            state.OpenMenu("settings_menu");
            state.Navigate(0, 1);
            state.SetItemEnabled("main_menu", "start", false);
            var restored = new MenuState();
            restored.Configure(Catalog());
            Assert.IsTrue(restored.ApplySummary(state.GetSummary()));
            Assert.IsTrue(V.VariantEquals(state.GetSummary(), restored.GetSummary()));
            Assert.AreEqual("settings_menu", restored.GetCurrentMenu());
        }
    }

    public class SettingsStateTests
    {
        sealed class FakeA11y : IAccessibilitySettingsSink
        {
            public double Scale;
            public string Scheme;
            public void SetTextScale(double newScale) => Scale = newScale;
            public void SetColorblindMode(string mode) { }
            public void SetMotionReduce(bool value) { }
            public void SetCaptionsEnabled(bool value) { }
            public void SetHoldToTap(bool value) { }
            public void SetDifficulty(string difficulty) { }
            public void SetGlyphScheme(string scheme) => Scheme = scheme;
            public void SetPresetId(string id) { }
        }

        [Test]
        public void SmokeFlow_RoundTripsAndWritesAccessibility()
        {
            var state = new SettingsState();
            Assert.IsTrue(state.SetTextScale(1.5));
            Assert.IsTrue(state.SetColorblindMode("deuteranopia"));
            Assert.IsTrue(state.SetHoldToTap(true));
            Assert.IsTrue(state.SetGlyphScheme("keyboard"));
            Assert.IsFalse(state.SetTextScale(2.5));
            Assert.IsFalse(state.SetDifficulty("nightmare"));
            var a11y = new FakeA11y();
            Assert.IsTrue(state.ApplyToAccessibility(a11y));
            Assert.AreEqual(1.5, a11y.Scale);
            Assert.AreEqual("keyboard", a11y.Scheme);
            Assert.IsFalse(state.ApplyToAccessibility(new object()));

            var restored = new SettingsState();
            Assert.IsTrue(restored.ApplySummary(state.GetSummary()));
            Assert.AreEqual("1.5", V.Str(restored.GetTextScale()));
            Assert.IsTrue(V.VariantEquals(state.GetSummary(), restored.GetSummary()));
            Assert.AreEqual("SettingsState: text_scale=1.50 colorblind=deuteranopia motion_reduce=false captions=true hold_to_tap=true",
                restored.GetStatusLines()[0]);
        }

        [Test]
        public void Sanitize_ClampsAndFillsDefaults()
        {
            var s = SettingsStateSchema.Sanitize(new GdDict { { "text_scale", 9L }, { "difficulty", "bogus" }, { "captions", "yes" } });
            Assert.AreEqual(2.0, s["text_scale"]);
            Assert.AreEqual("standard", s["difficulty"]);
            Assert.AreEqual(true, s["captions"]);
            Assert.IsTrue(SettingsStateSchema.Validate(s));
        }
    }

    public class TooltipPayloadTests
    {
        [Test]
        public void FooterSplits_AndDictRoundTrips()
        {
            var p = new TooltipPayload("Crate", "Loot", "  [E] Pick up ", "prop", "crate_1");
            Assert.AreEqual("[E]", p.FooterGlyph);
            Assert.AreEqual("Pick up", p.FooterActionLabel);
            Assert.AreEqual("Inspect", new TooltipPayload("", "", "Inspect").FooterActionLabel);
            var back = TooltipPayload.FromDict(p.ToDict());
            Assert.IsTrue(V.VariantEquals(p.ToDict(), back.ToDict()));
        }
    }

    public class TutorialStateTests
    {
        static GdDict Catalog() => new GdDict
        {
            { "version", "tutorial-triggers-1" },
            { "tutorials", GdArray.Of(new GdDict
                {
                    { "id", "first_move" }, { "trigger_event", "player_moved" }, { "trigger_target", "any" },
                    { "title", "Movement" }, { "body", "Move." }, { "codex_topic", "Survival" }, { "codex_entry_id", "first_move" },
                }) },
        };

        [Test]
        public void TriggersOnce_DismissUnlocksCodex_RoundTrips()
        {
            var state = new TutorialState();
            Assert.IsTrue(state.Configure(Catalog()));
            int fired = 0;
            state.Triggered += (id, title, body) => fired++;
            Assert.AreEqual("first_move", state.Trigger("player_moved", "any"));
            Assert.IsTrue(state.HasPendingBanner());
            Assert.IsTrue(state.Dismiss("first_move"));
            Assert.AreEqual(1, state.GetUnlockedCodexIds().Count);
            Assert.AreEqual("", state.Trigger("player_moved", "any"));
            Assert.AreEqual(1, fired);

            var restored = new TutorialState();
            restored.Configure(Catalog());
            Assert.IsTrue(restored.ApplySummary(state.GetSummary()));
            Assert.IsTrue(V.VariantEquals(state.GetSummary(), restored.GetSummary()));
        }
    }
}
