using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace SynapticSea.Tests.Unity
{
    public class MenuCoordinatorTests : UiTestBase
    {
        FakeUiAudio _audio;
        AccessibilitySettings _a11y;
        MetaProgressionState _meta;
        SaveLoadService _service;
        SkillTreeState _tree;
        PlayerProgressionState _progression;

        MenuCoordinator NewCoordinator(Func<RunSnapshot> snapshotBuilder = null, DemoScopeGate gate = null)
        {
            _audio = new FakeUiAudio();
            _a11y = new AccessibilitySettings(_ => null);
            var achievements = new AchievementState(Storage, new ManualClock());
            achievements.Configure(CatalogRegistry.LoadDict(AchievementsPanel.CatalogPath));
            _tree = new SkillTreeState();
            _tree.Configure(SkillTreeState.LoadSkillsCatalog(), SkillTreeState.LoadBooksCatalog());
            _tree.LoadPrerequisites();
            _progression = new PlayerProgressionState();
            _progression.Configure(ClassDefinition.LoadAll()["engineer"], PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            var hub = new HubUpgradeState();
            hub.Configure();
            _meta = new MetaProgressionState(Storage, new ManualClock());
            _meta.Configure();
            var localization = new LocalizationCatalog();
            localization.Configure(CatalogRegistry.LoadDict("res://data/release/localization_catalog.json"));
            var build = new BuildMetadataState();
            build.Configure(CatalogRegistry.LoadDict("res://data/release/build_metadata.json"));
            _service = new SaveLoadService(Storage, new ManualClock());
            var menu = new SaveLoadMenu();
            menu.Bind(_service);
            var c = new MenuCoordinator(achievements, _audio, _tree, _progression, hub, _meta, localization, build, menu, _a11y,
                null, snapshotBuilder, gate);
            Assert.IsTrue(c.ConfigureFromData(_a11y));
            return c;
        }

        [Test]
        public void RequiredDependenciesAreEnforced()
        {
            Assert.Throws<ArgumentNullException>(() => new MenuCoordinator(null, new FakeUiAudio(), null, null, null, null, null, null, null));
        }

        [Test]
        public void PauseOpensThePausedMenuAndEmitsModalEvents()
        {
            MenuCoordinator c = NewCoordinator();
            var opened = new List<string>();
            var closed = new List<string>();
            c.ModalOpened += opened.Add;
            c.ModalClosed += closed.Add;
            Assert.IsFalse(c.HandleUiInput(UiCommand.Down), "menu commands are ignored in play");
            Assert.IsTrue(c.HandleUiInput(UiCommand.Pause));
            CollectionAssert.AreEqual(new[] { "pause_menu" }, opened);
            Assert.AreSame(c.MenuPanel, c.Stack.Top);
            Assert.IsTrue(c.Stack.SimulationPaused);
            Assert.AreEqual("Paused", c.MenuPanel.Title);
            Assert.AreEqual(SurfacePanel.BadgePausedText, c.MenuPanel.BadgeText);
            StringAssert.StartsWith("> Resume", c.GetMenuText());
            CollectionAssert.Contains(_audio.Sfx, AudioEventSeam.UI_PANEL_OPEN);
            Assert.IsTrue(c.HandleUiInput(UiCommand.Pause), "Start again resumes (close_all)");
            CollectionAssert.AreEqual(new[] { "pause_menu" }, closed);
            Assert.IsTrue(c.Stack.IsEmpty);
        }

        [Test]
        public void PauseCommandsEmitTheSessionEvents()
        {
            MenuCoordinator c = NewCoordinator();
            int save = 0, saveExit = 0, quit = 0, load = 0;
            c.SaveRequested += () => save++;
            c.SaveAndExitRequested += () => saveExit++;
            c.QuitRequested += () => quit++;
            c.LoadRequested += () => load++;
            c.HandleUiInput(UiCommand.Pause);
            Select(c, "save");
            Select(c, "save_and_exit");
            Select(c, "quit_main");
            Assert.AreEqual((1, 1, 1), (save, saveExit, quit));

            c.MenuState.CloseAll();
            c.SetLoadAvailable(false);
            c.OpenMainMenu();
            StringAssert.Contains("Continue (disabled)", c.GetMenuText());
            Assert.IsTrue(c.MenuPanel.Rows.First(r => r.Id == "continue").Enabled == false);
            Select(c, "continue");
            Assert.AreEqual(0, load, "a disabled Continue never emits load_requested");
            c.SetLoadAvailable(true);
            Select(c, "continue");
            Assert.AreEqual(1, load);
        }

        static void Select(MenuCoordinator c, string itemId)
        {
            int index = c.MenuPanel.Rows.ToList().FindIndex(r => r.Id == itemId);
            Assert.GreaterOrEqual(index, 0, itemId);
            c.MenuState.SetFocusIndex(index);
            c.HandleUiInput(UiCommand.Accept);
        }

        [UnityTest]
        public IEnumerator PauseSettingsBackRestoresFocusThroughRealNavigation()
        {
            using (var harness = new UiHarness())
            {
                yield return null;
                MenuCoordinator c = NewCoordinator();
                harness.Mount(c.Root);
                var changes = new List<GdDict>();
                c.SettingsChanged += changes.Add;
                c.HandleUiInput(UiCommand.Pause);
                harness.Layout();
                Assert.AreEqual(MenuPanel.TokenFor("pause_menu", "resume"), UiFocus.TokenOf(harness.Focused), "initial focus on Resume");

                UiHarness.Navigate(harness.Focused, NavigationMoveEvent.Direction.Down);
                Assert.AreEqual(1, c.GetFocusIndex());
                Assert.AreEqual(MenuPanel.TokenFor("pause_menu", "settings"), UiFocus.TokenOf(harness.Focused));
                UiHarness.Submit(harness.Focused);
                Assert.AreEqual("settings_menu", c.GetCurrentMenu());
                Assert.AreEqual("Settings", c.MenuPanel.Title);
                harness.Layout();
                Assert.AreEqual(MenuPanel.TokenFor("settings_menu", "preset"), UiFocus.TokenOf(harness.Focused));

                UiHarness.Navigate(harness.Focused, NavigationMoveEvent.Direction.Down);
                Assert.AreEqual(MenuPanel.TokenFor("settings_menu", "text_scale"), UiFocus.TokenOf(harness.Focused));
                UiHarness.Navigate(harness.Focused, NavigationMoveEvent.Direction.Right);
                Assert.AreEqual(1.5, c.SettingsState.GetTextScale());
                Assert.AreEqual(1.5, changes.Last().GetFloat("text_scale"));
                Assert.IsTrue(c.Root.ClassListContains("scale-150"), "text scale applies by reflow class");
                Assert.AreEqual("1.5x", c.MenuPanel.Rows.First(r => r.Id == "text_scale").Value);
                StringAssert.Contains("Text Scale: 1.5x", c.GetMenuText());

                UiHarness.Cancel(harness.Focused);
                Assert.AreEqual("pause_menu", c.GetCurrentMenu(), "Back returns exactly one level");
                Assert.AreEqual(1, c.GetFocusIndex(), "focus restored to the item that opened Settings");
                harness.Layout();
                Assert.AreEqual(MenuPanel.TokenFor("pause_menu", "settings"), UiFocus.TokenOf(harness.Focused));

                UiHarness.Cancel(harness.Focused);
                Assert.AreEqual("", c.GetCurrentMenu());
                Assert.IsTrue(c.Stack.IsEmpty);
            }
        }

        [UnityTest]
        public IEnumerator PauseAboveInspectionGuardsInputAndRestoresFocus()
        {
            using (var harness = new UiHarness())
            {
                yield return null;
                MenuCoordinator c = NewCoordinator();
                harness.Mount(c.Root);
                var player = new InventoryState();
                player.AddItem("scrap_metal", 2);
                player.AddItem("ration_pack", 1);
                var inventory = new InventoryPanel();
                inventory.PanelClosed += () => c.NotifyInspectionClosed(inventory);
                inventory.OpenSelf(player, EquipmentState.Create());
                c.OpenInspection(inventory);
                harness.Layout();
                Assert.AreSame(inventory, c.Stack.Top);
                Assert.IsTrue(c.Stack.BlocksGameplay);
                Assert.IsFalse(c.Stack.SimulationPaused, "inspection is LIVE");
                UiHarness.Navigate(harness.Focused, NavigationMoveEvent.Direction.Down);
                string inventoryToken = UiFocus.TokenOf(harness.Focused);
                Assert.AreEqual("inv-self:scrap_metal", inventoryToken);

                var opened = new List<string>();
                c.ModalOpened += opened.Add;
                c.HandleUiInput(UiCommand.Pause);
                CollectionAssert.AreEqual(new[] { "pause_menu" }, opened);
                Assert.AreSame(c.MenuPanel, c.Stack.Top);
                Assert.IsTrue(inventory.ClassListContains(UiClasses.SurfaceCovered));
                Assert.IsFalse(inventory.enabledSelf, "the covered inspection cannot take input or focus");
                Assert.IsTrue(c.Stack.SimulationPaused);

                c.Stack.Dispatch(UiCommand.Down);
                Assert.AreEqual(1, c.GetFocusIndex(), "the pause menu consumed Down");
                CollectionAssert.AreEqual(new object[] { "scrap_metal" }, inventory.GetSelectedIds(InventoryPanel.PaneSelf), "inventory selection untouched");

                c.MenuState.SetFocusIndex(0);
                c.Stack.Dispatch(UiCommand.Accept);
                Assert.AreSame(inventory, c.Stack.Top, "Resume returns to the inspection");
                Assert.IsFalse(c.Stack.SimulationPaused);
                Assert.IsTrue(inventory.enabledSelf);
                harness.Layout();
                Assert.AreEqual(inventoryToken, UiFocus.TokenOf(harness.Focused), "focus restored by stable token");
                Assert.IsTrue(c.Stack.Dispatch(UiCommand.Accept), "the held confirm is swallowed");
                Assert.AreEqual(2, player.GetQuantity("scrap_metal"));

                c.Stack.Dispatch(UiCommand.Cancel);
                Assert.IsTrue(c.Stack.IsEmpty, "Back closes the topmost inspection");
                Assert.IsFalse(inventory.IsOpen());
            }
        }

        [Test]
        public void CodexFromPlayIsLiveAndListsUnlockedEntries()
        {
            MenuCoordinator c = NewCoordinator();
            c.HandleUiInput(UiCommand.OpenCodex);
            Assert.AreSame(c.CodexPanel, c.Stack.Top);
            Assert.IsFalse(c.Stack.SimulationPaused);
            StringAssert.Contains(CodexPanel.EmptyText, c.CodexPanel.Text);
            c.HandleUiInput(UiCommand.Cancel);
            Assert.IsTrue(c.Stack.IsEmpty);

            Assert.AreEqual("first_move", c.TriggerTutorial("player_moved"));
            Assert.AreEqual("Movement\nUse WASD or the left stick to move. Hold SHIFT to walk quietly.", c.GetTutorialText());
            Assert.IsTrue(c.DismissLatestTutorial());
            Assert.AreEqual("", c.GetTutorialText());
            CollectionAssert.Contains(c.GetCodexUnlockedIds(), "first_move");
            StringAssert.Contains("- Survival | Movement", c.CodexPanel.Text);
            Assert.AreEqual(1, c.CodexPanel.Entries.Count);
        }

        [Test]
        public void HotbarTooltipAndGlyphChips()
        {
            MenuCoordinator c = NewCoordinator();
            c.SettingsState.SetGlyphScheme("keyboard");
            c.SetInventoryItems(new[] { "scrap_metal", "medkit" }, 1);
            Assert.AreEqual("HOTBAR  [E]\n[1] scrap_metal | >[2] medkit | [3] (empty) | [4] (empty) | [5] (empty)", c.GetHotbarText());
            c.SetTooltipQuery(new GdDict { { "subject_kind", "interactable" }, { "subject_id", "circuit_board" } });
            StringAssert.StartsWith("Circuit Board\n", c.GetTooltipPanelText());
            Assert.IsTrue(c.TooltipCard.IsShownNow);
            c.SetTooltipQuery(new GdDict { { "subject_kind", "item" }, { "subject_id", "" } });
            Assert.IsFalse(c.TooltipCard.IsShownNow);

            Assert.AreEqual("[W]", GlyphChips.GlyphText(c.ControllerGlyphState, "move_forward", "keyboard"), "move_forward reads the move_up glyph");
            Assert.AreEqual("[S]", GlyphChips.GlyphText(c.ControllerGlyphState, "move_back", "keyboard"));
            Assert.AreEqual("[A]", GlyphChips.GlyphText(c.ControllerGlyphState, "interact", "gamepad_xbox"));
        }

        [Test]
        public void SurfacesShowBackAndPauseGlyphChipsForTheActiveScheme()
        {
            MenuCoordinator c = NewCoordinator();
            c.SettingsState.SetGlyphScheme("gamepad_xbox");
            var wounds = new WoundsPanel();
            wounds.Bind(new WoundState());
            wounds.Open();
            c.OpenInspection(wounds);
            Assert.AreEqual("[B]", wounds.BackGlyphText);
            Assert.AreEqual("[Start] Pause", wounds.PauseHintText, "LIVE surfaces advertise the pause path");
            c.HandleUiInput(UiCommand.Pause);
            Assert.AreEqual("", c.AchievementsPanel.PauseHintText, "PAUSED surfaces do not");
            c.SettingsState.SetGlyphScheme("keyboard");
            c.ApplySettingsSummary(c.GetSettingsSummary());
            Assert.AreEqual("[Esc]", wounds.BackGlyphText);
            Assert.AreEqual("[Esc]", c.AchievementsPanel.BackGlyphText);
            Assert.AreEqual("[W]", c.GlyphFor("move_forward"));
        }

        [Test]
        public void RecordsSaveLoadScreenThroughTheCoordinator()
        {
            MenuCoordinator c = NewCoordinator(() => new RunSnapshot
            {
                CurrentLocation = "cargo_bay",
                PlayerProgressionSummary = new GdDict { { "class_id", "engineer" } },
                CurrentObjectiveSequence = 2,
                PlayTimeSeconds = 125,
                WorldSeed = 42,
            });
            var results = new List<GdDict>();
            c.MetaScreenConfirmed += results.Add;
            c.HandleUiInput(UiCommand.Pause);
            Select(c, "records");
            Assert.AreEqual("records_menu", c.GetCurrentMenu());
            Select(c, "save_load");
            Assert.AreEqual("save_load", c.GetActiveMetaScreen());
            Assert.AreSame(c.SaveLoadScreen, c.Stack.Top);
            Assert.IsTrue(c.MenuPanel.ClassListContains(UiClasses.SurfaceCovered));

            c.HandleUiInput(UiCommand.Accept); // arm Save on slot_01 (empty)
            Assert.AreEqual("arm", results.Last().GetString("action"));
            c.HandleUiInput(UiCommand.Accept);
            Assert.AreEqual("save", results.Last().GetString("action"));
            Assert.IsTrue(results.Last().GetBool("ok"));
            Assert.AreSame(results.Last(), c.GetLastMetaScreenConfirmResult());
            SelectableList.Item row = c.SaveLoadScreen.List.Items[c.SaveSlots.RowIndex];
            Assert.AreEqual("Location cargo_bay · Class engineer · Objective 2 · Played 2m05s · Seed 42", row.Detail);
            string loadedSlot = null;
            RunSnapshot loaded = null;
            c.SlotSnapshotLoaded += (slot, snap) =>
            {
                loadedSlot = slot;
                loaded = snap;
            };
            c.HandleUiInput(UiCommand.Accept); // arm Load, the first verb of a filled manual row
            c.HandleUiInput(UiCommand.Accept);
            Assert.AreEqual("load", results.Last().GetString("action"));
            Assert.AreEqual("slot_01", loadedSlot);
            Assert.AreEqual("cargo_bay", loaded.CurrentLocation);

            c.HandleUiInput(UiCommand.Cancel);
            Assert.AreEqual("", c.GetActiveMetaScreen());
            Assert.AreSame(c.MenuPanel, c.Stack.Top);
            Assert.AreEqual(MenuPanel.TokenFor("records_menu", "save_load"), c.MenuPanel.LastFocusToken);
        }

        [Test]
        public void MetaScreensConfirmThroughModelGates()
        {
            MenuCoordinator c = NewCoordinator();
            foreach (string id in MenuCoordinator.MetaScreenIds)
            {
                if (id == "audio_log") continue;
                Assert.IsTrue(c.MetaScreenIsPopulated(id), id);
            }
            Assert.IsTrue(c.MetaScreenIsPopulated("audio_log"), "the voice-log registry lists entries");

            c.OpenMetaScreen("hub_upgrades");
            int storage = c.HubUpgradePanel.GetCatalogPanel().GetUpgradeEntries(_meta).Cast<GdDict>().ToList()
                .FindIndex(e => e.GetString("upgrade_id") == "hub_storage_basic");
            c.HubUpgradePanel.MoveSelection(storage);
            GdDict denied = c.MetaScreenConfirm();
            Assert.IsFalse(denied.GetBool("ok"));
            StringAssert.Contains("insufficient_currency", c.HubUpgradePanel.StatusDisplay);
            _meta.AddMetaCurrency(500);
            c.HubUpgradePanel.Render();
            GdDict bought = c.MetaScreenConfirm();
            Assert.IsTrue(bought.GetBool("ok"));
            Assert.IsTrue(_meta.IsHubUpgradeUnlocked(bought.GetString("detail")));
            StringAssert.StartsWith("✓ Purchased", c.HubUpgradePanel.StatusDisplay);

            c.OpenMetaScreen("skill_tree");
            int surgery = _tree.GetSkillEntries().Cast<GdDict>().ToList().FindIndex(e => e.GetString("skill_id") == "surgery");
            c.SkillTreePanel.MoveSelection(surgery);
            Assert.IsFalse(c.MetaScreenConfirm().GetBool("ok"));
            StringAssert.Contains("first_aid ≥ 4", c.SkillTreePanel.StatusDisplay);

            c.OpenMetaScreen("class");
            c.ClassPanel.MoveSelection(1);
            string pick = c.ClassPanel.GetSelectedId();
            Assert.IsTrue(c.MetaScreenConfirm().GetBool("ok"));
            Assert.AreEqual(pick, _meta.GetSelectedClass());
            StringAssert.Contains("✓ Selected", c.ClassPanel.List.Items[1].Detail);

            c.OpenMetaScreen("credits");
            List<GdDict> credits = c.CreditsScreen.GetEntries();
            Assert.IsTrue(credits.Any(e => e.GetString("role") == "Typography" && e.GetString("name").StartsWith("Inter")));
            Assert.IsTrue(credits.Any(e => e.GetString("role") == "Typography" && e.GetString("name").StartsWith("JetBrains Mono")));
            Assert.IsTrue(c.CreditsScreen.VisibleTexts().Any(t => t.StartsWith("Typography — Inter")));
            c.HandleUiInput(UiCommand.Cancel);
            Assert.AreEqual("", c.GetActiveMetaScreen(), "credits Back dismisses");

            c.OpenMetaScreen("release_badge");
            Assert.AreEqual("DEV", c.ReleaseBadgeOverlay.BadgeLabelText);
            StringAssert.Contains("Version v0.1.0", c.ReleaseBadgeOverlay.InfoText);

            string changedTo = null;
            c.LanguageChanged += l => changedTo = l;
            c.OpenMetaScreen("language");
            List<string> langs = GdString.ToStringList(c.LanguageSelector.GetKnownLanguages());
            int other = langs.FindIndex(l => l != "en");
            if (other >= 0)
            {
                c.LanguageSelector.SelectIndex(other);
                Assert.AreEqual(langs[other], changedTo);
                Assert.AreEqual(langs[other], c.GetActiveLanguage());
            }

            GdDict captured = null;
            c.SettingsChanged += s => captured = s;
            c.OpenMetaScreen("audio_settings");
            c.AudioSettingsPanel.OnCaptionToggled(false);
            Assert.IsNotNull(captured, "ADR-0044: captions go through settings_changed");
            Assert.IsFalse(captured.GetBool("captions", true));
            c.AudioSettingsPanel.OnVolumeChanged(AudioEventSeam.BUS_MUSIC, -20);
            Assert.AreEqual(-20, _audio.Volumes[AudioEventSeam.BUS_MUSIC]);

            c.OpenMetaScreen("audio_log");
            c.AudioLogPanel.SelectEntry(0);
            c.AudioLogPanel.PlaySelected();
            StringAssert.StartsWith("Playing: ", c.AudioLogPanel.StatusLabelText);
            c.AudioLogPanel.Stop();
            Assert.AreEqual("(no entry playing)", c.AudioLogPanel.StatusLabelText);
        }

        [Test]
        public void DemoGateBlocksMetaProgressionPersistence()
        {
            var build = new BuildMetadataState();
            build.Configure(new GdDict { { "version", "v1" }, { "build_kind", "demo" } });
            var gate = new DemoScopeGate();
            gate.Configure(CatalogRegistry.LoadDict("res://data/release/demo_scope_manifest.json"), build);
            MenuCoordinator c = NewCoordinator(null, gate);
            c.OpenMetaScreen("class");
            GdDict result = c.MetaScreenConfirm();
            if (gate.IsBlocked("hub.meta_progression"))
            {
                Assert.AreEqual("demo_blocked", result.GetString("detail"));
                StringAssert.Contains("not available in the demo", c.ClassPanel.StatusDisplay);
            }
        }
    }
}
