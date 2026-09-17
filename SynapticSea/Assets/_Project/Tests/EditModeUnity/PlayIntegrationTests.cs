using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Game;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using SynapticSea.Tests.Session;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    /// <summary>
    /// W2a in-play wiring against a headless golden session: the coordinator over the session's TutorialState and
    /// SettingsState (A5, B1), wound treatment through the panel's session host (A6), audio / language preferences in the
    /// settings payload (B2, B5), the Unity glyph rows and last-device "auto" scheme (B4), the HUD toast / damage feedback
    /// (B7, D3), world-label culling (D1), component markers (D2) and the threat view's attack / death feedback (D3).
    /// </summary>
    public class PlayIntegrationTests : UiTestBase
    {
        IEngineInfo _previousEngine;
        readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
                if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            CoreServices.Engine = _previousEngine;
        }

        static SessionHarness.Rig Boot()
        {
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            return rig;
        }

        static MenuCoordinator CoordinatorFor(RunSession s, IUiAudio audio, AccessibilitySettings a11y, System.Func<int> joypads = null)
        {
            var menu = new SaveLoadMenu();
            menu.Bind(s.SaveLoadService);
            var c = new MenuCoordinator(s.AchievementState, audio, s.SkillTreeState, s.PlayerProgression, s.HubUpgradeState, s.MetaProgressionState,
                s.LocalizationCatalog, s.BuildMetadataState, menu, a11y, s.UnlockRegistry, () => RunSnapshotAssembler.Build(s), s.DemoScopeGate, null,
                joypads, s.TutorialState, s.SettingsState);
            Assert.IsTrue(c.ConfigureFromData(a11y));
            return c;
        }

        // ------------------------------------------------------------------ A5 tutorial owner

        [Test]
        public void CoordinatorInPlayShowsTheSessionTutorialStateAndSurvivesConfigureAndReload()
        {
            RunSession s = Boot().Session;
            s.OnPlayerMoved();
            Assert.IsTrue(s.TutorialState.HasPendingBanner(), "player_moved fired on the session state");

            var audio = new FakeUiAudio();
            MenuCoordinator c = CoordinatorFor(s, audio, new AccessibilitySettings(_ => null));
            Assert.AreSame(s.TutorialState, c.TutorialState, "one TutorialState");
            Assert.AreSame(s.SettingsState, c.SettingsState, "one SettingsState");
            Assert.IsTrue(c.TutorialStateInjected);
            Assert.IsTrue(s.TutorialState.GetSummary().GetArrayOrEmpty("fired_keys").Contains("player_moved|any"),
                "configuring the coordinator did not reset the session's fired tutorials");
            c.BindTutorialState(s.TutorialState);
            StringAssert.Contains(s.TutorialState.GetTitle(s.TutorialState.GetLatestTutorialId()), c.GetTutorialText(), "the banner shows the session tutorial");

            int sfxBefore = audio.Sfx.Count;
            s.TriggerTutorial("inventory_opened", "any");
            Assert.AreEqual(sfxBefore, audio.Sfx.Count, "the session plays the tutorial cue, not the coordinator a second time");
            CollectionAssert.AreEqual(s.TutorialState.GetUnlockedCodexIds().Cast<object>().ToList(), c.GetCodexUnlockedIds().Cast<object>().ToList());

            var reset = 0;
            s.Events.TutorialStateReset += t =>
            {
                reset++;
                c.BindTutorialState(t);
            };
            Assert.IsTrue(s.RequestSave());
            Assert.IsTrue(s.RequestLoad());
            Assert.GreaterOrEqual(reset, 1);
            Assert.AreSame(s.TutorialState, c.TutorialState);
        }

        // ------------------------------------------------------------------ A6 wounds through the session

        [Test]
        public void WoundsPanelTreatsThroughTheSessionShowingTheItemAndDenyReasons()
        {
            RunSession s = Boot().Session;
            string wound = s.WoundState.ApplyWound(new GdDict { { "kind", "laceration" }, { "body_part", "arm" }, { "severity", 0.6 } });
            var audio = new FakeUiAudio();
            var panel = new WoundsPanel();
            panel.SetAudioManager(audio);
            panel.Bind(new SessionWoundHost(s));
            panel.Open();

            StringAssert.Contains("Bandage: no bandage item", panel.List.Items[0].Detail);
            StringAssert.Contains("Treat: no medical item", panel.List.Items[0].Detail);
            Assert.IsFalse(panel.BandageSelected());
            Assert.AreEqual("cannot bandage: no bandage item", panel.GetStatus());
            Assert.AreEqual(0, audio.Sfx.Count, "the session played the refusal cue");
            Assert.AreEqual(AudioEventSeam.UI_PANEL_CLOSE, s.AudioManager.PlayedSfx.Last());

            s.InventoryState.AddItem("bandage_kit", 2);
            panel.Refresh();
            StringAssert.Contains("Bandage: bandage_kit", panel.List.Items[0].Detail);
            Assert.IsTrue(panel.BandageSelected());
            Assert.AreEqual(1, s.InventoryState.GetQuantity("bandage_kit"), "the session consumed the item");
            Assert.IsTrue(s.WoundState.GetWound(wound).GetBool("bandaged"));
            Assert.AreEqual("bandaged " + wound + " with bandage_kit", panel.GetStatus());
            Assert.AreEqual("bandage_kit", panel.LastResult.GetString("item_id"));
            StringAssert.Contains("Bandage: already bandaged", panel.List.Items[0].Detail);
        }

        // ------------------------------------------------------------------ B1 / B2 / B5 settings

        [Test]
        public void StoredPreferencesLoadWithoutEmittingAndAudioAndLanguagePersistInTheSummary()
        {
            RunSession s = Boot().Session;
            var audio = new FakeUiAudio();
            var a11y = new AccessibilitySettings(_ => null);
            MenuCoordinator c = CoordinatorFor(s, audio, a11y);
            var emitted = new List<GdDict>();
            c.SettingsChanged += summary => emitted.Add(summary);

            var stored = SettingsStateSchema.DefaultPayload();
            stored["text_scale"] = 1.5;
            stored["hold_to_tap"] = true;
            stored[SettingsState.AudioBusVolumesKey] = new GdDict { { AudioEventSeam.BUS_MUSIC, -20.0 } };
            Assert.IsTrue(c.LoadSettingsSummary(stored));
            Assert.AreEqual(0, emitted.Count, "adopting the stored file does not re-save");
            Assert.AreEqual(1.5, s.SettingsState.GetTextScale());
            Assert.AreEqual(1.5, a11y.GetTextScale());
            Assert.IsFalse(s.HoldToWorkEnabled, "the session reads the adopted hold_to_tap live");
            Assert.AreEqual(-20.0, s.SettingsState.GetAudioBusVolumes().GetFloat(AudioEventSeam.BUS_MUSIC));

            c.OpenMetaScreen("audio_settings");
            c.AudioSettingsPanel.OnVolumeChanged(AudioEventSeam.BUS_SFX, -12);
            c.AudioSettingsPanel.OnMuteChanged(AudioEventSeam.BUS_VOICE, true);
            Assert.AreEqual(2, emitted.Count);
            GdDict last = emitted.Last();
            Assert.AreEqual(1.5, last.GetFloat("text_scale"), "the merged state is saved, not defaults plus one change");
            Assert.AreEqual(-12.0, last.GetDict(SettingsState.AudioBusVolumesKey).GetFloat(AudioEventSeam.BUS_SFX));
            Assert.AreEqual(-20.0, last.GetDict(SettingsState.AudioBusVolumesKey).GetFloat(AudioEventSeam.BUS_MUSIC));
            Assert.IsTrue(last.GetDict(SettingsState.AudioBusMutedKey).GetBool(AudioEventSeam.BUS_VOICE));

            // An old preferences file (no audio keys) keeps the current volumes; a Godot-shaped summary stays Godot-shaped.
            var restored = new SettingsState();
            Assert.IsTrue(restored.ApplySummary(last));
            Assert.IsTrue(restored.ApplySummary(SettingsStateSchema.DefaultPayload()));
            Assert.AreEqual(-12.0, restored.GetAudioBusVolumes().GetFloat(AudioEventSeam.BUS_SFX));
            CollectionAssert.AreEquivalent(SettingsStateSchema.DefaultPayload().Keys.Select(V.Str), new SettingsState().GetSummary().Keys.Select(V.Str));

            List<string> langs = GdString.ToStringList(c.LanguageSelector.GetKnownLanguages());
            c.LanguageSelector.SetActiveLanguage("xx");
            c.LanguageSelector.SelectIndex(langs.IndexOf("en"));
            Assert.AreEqual("en", s.SettingsState.GetLanguage());
            Assert.AreEqual("en", emitted.Last().GetString(SettingsState.LanguageKey), "the language choice is persisted");
        }

        // ------------------------------------------------------------------ B4 glyphs

        [Test]
        public void UnityGlyphRowsCoverCombatAndPanelActionsAndAutoFollowsTheLastDevice()
        {
            RunSession s = Boot().Session;
            var devices = new LastInputDevice(listen: false);
            MenuCoordinator c = CoordinatorFor(s, new FakeUiAudio(), new AccessibilitySettings(_ => null), devices.JoypadCountForGlyphs);
            Assert.AreEqual("[F]", c.GlyphFor("attack_primary"));
            Assert.AreEqual("[R]", c.GlyphFor("reload_weapon"));
            Assert.AreEqual("[Ctrl]", c.GlyphFor("crouch"));
            Assert.AreEqual("[C]", c.GlyphFor("field_craft"));
            Assert.AreEqual("[U]", c.GlyphFor("toggle_ship_mod"));
            Assert.AreEqual("[O]", c.GlyphFor("toggle_wounds"));
            Assert.AreEqual("[1]", c.GlyphFor("hotbar_1"));
            Assert.AreEqual("[E]", c.GlyphFor("interact"), "data rows are untouched");

            var gamepad = InputSystem.AddDevice<Gamepad>();
            try
            {
                int changes = 0;
                devices.Changed += _ => changes++;
                devices.Report(gamepad);
                Assert.IsTrue(devices.IsGamepad);
                Assert.AreEqual("[RT]", c.GlyphFor("attack_primary"), "auto follows the last-used device");
                Assert.AreEqual("", c.GlyphFor("toggle_wounds"), "no gamepad binding: the chip hides");
                devices.Report(Keyboard.current ?? InputSystem.AddDevice<Keyboard>());
                Assert.AreEqual("[F]", c.GlyphFor("attack_primary"));
                Assert.AreEqual(2, changes);
            }
            finally
            {
                InputSystem.RemoveDevice(gamepad);
            }
        }

        // ------------------------------------------------------------------ B7 / D3 HUD feedback

        [Test]
        public void HudToastsAndDamageFeedbackExpireAndHonourReducedMotion()
        {
            var go = new GameObject("hud");
            _objects.Add(go);
            go.SetActive(false);
            go.AddComponent<UnityEngine.UIElements.UIDocument>();
            var hud = go.AddComponent<HudRoot>();
            hud.Build(new UnityEngine.UIElements.VisualElement());

            hud.ShowToast(SessionUiBridge.NoWebChartText);
            StringAssert.Contains(SessionUiBridge.NoWebChartText, hud.ToastText);
            hud.TickFeedback(HudRoot.ToastSeconds + 0.1f);
            Assert.AreEqual("", hud.ToastText);

            hud.ShowDamage(12, "stalker");
            StringAssert.Contains("Hit −12 · stalker", hud.Vitals.DamageIndicatorText);
            Assert.IsTrue(hud.DamageFlashActive);
            hud.TickFeedback(HudRoot.DamageIndicatorSeconds + 0.1f);
            Assert.AreEqual("", hud.Vitals.DamageIndicatorText);
            Assert.IsFalse(hud.DamageFlashActive);

            var reduced = new AccessibilitySettings(_ => null);
            reduced.SetMotionReduce(true);
            hud.ApplyAccessibility(reduced);
            hud.ShowDamage(5, "");
            Assert.IsFalse(hud.DamageFlashActive, "reduced motion: no screen flash");
            StringAssert.Contains("Hit −5", hud.Vitals.DamageIndicatorText, "the indicator still shows");

            hud.Vitals.SetWeaponLine("Crowbar | melee | Threat 0.00 | stealth");
            Assert.AreEqual("Crowbar | melee | Threat 0.00 | stealth", hud.Vitals.WeaponLine);
        }

        // ------------------------------------------------------------------ D1 world labels

        [Test]
        public void WorldLabelsCullAffordancesByDistanceButNeverHazards()
        {
            var layer = new WorldLabelLayer();
            var container = new UnityEngine.UIElements.VisualElement();
            layer.Container = container;
            Vector3 far = new Vector3(100f, 0f, 0f);
            layer.Set("affordance:a", "01 Supplies", () => far, Color.green, hazard: false);
            layer.Set("hazard:breach", "OXYGEN LOW", () => far, Color.red, hazard: true);
            layer.Set("affordance:near", "Ramp", () => Vector3.one, Color.yellow, hazard: false);
            layer.Set("hazard:hidden", "ARC", () => Vector3.one, Color.red, hazard: true, visible: false);

            layer.Update(null, Vector3.zero);
            Assert.IsFalse(layer.Get("affordance:a").Shown, "affordances beyond the cull distance hide");
            Assert.IsTrue(layer.Get("hazard:breach").Shown, "hazard warnings are never distance-culled");
            Assert.IsTrue(layer.Get("affordance:near").Shown);
            Assert.IsFalse(layer.Get("hazard:hidden").Shown, "model visibility wins");
            Assert.AreEqual(4, container.childCount);

            layer.TextScale = 2f;
            Assert.AreEqual(WorldLabelLayer.HazardPixels * 2f, layer.Get("hazard:breach").Label.style.fontSize.value.value, 0.01f);
            Assert.AreEqual(WorldLabelLayer.AffordancePixels * 2f, layer.Get("affordance:near").Label.style.fontSize.value.value, 0.01f);

            layer.RemoveWhere(e => e.Id.StartsWith("affordance:"));
            Assert.AreEqual(2, container.childCount);
        }

        // ------------------------------------------------------------------ D2 component markers

        [Test]
        public void ComponentMarkersFollowTheSessionRecords()
        {
            var root = new GameObject("markers");
            _objects.Add(root);
            var view = new ComponentMarkerView(root.transform);
            view.Rebuild(new List<GdDict>
            {
                new GdDict { { "component_instance_id", "ci_1" }, { "component_id", "console_unit" }, { "room_id", "r1" }, { "world_position", new Vec3(2f, 0f, 3f) } },
                new GdDict { { "component_instance_id", "ci_2" }, { "component_id", "pump" }, { "room_id", "r2" } },
            });
            Assert.AreEqual(1, view.Markers.Count, "records without a position are skipped");
            GameObject marker = view.Markers[0];
            Assert.AreEqual("ComponentMarker_ci_1", marker.name);
            Assert.AreEqual(Frame.ToUnity(new Vec3(2f, 0f, 3f)), marker.transform.localPosition);
            Assert.AreEqual("console_unit", view.RecordFor(marker).GetString("component_id"));
            Assert.IsFalse(ComponentMarkerView.IsImported(marker), "an unbound component keeps the primitive fallback");
            Assert.AreEqual(ComponentMarkerView.VisualSourceFallback, view.RecordFor(marker).GetString("visual_source"));
            Assert.IsNotNull(marker.transform.Find(ComponentMarkerView.FallbackName));
            view.Rebuild(new List<GdDict>());
            Assert.AreEqual(0, view.Markers.Count);
            Assert.AreEqual(0, root.transform.childCount);
        }

        [Test]
        public void ComponentMarkersMountTheBoundPropVisual()
        {
            var bindings = new PropVisualBindingCatalog();
            Assert.IsTrue(bindings.LoadFromPath(), string.Join("; ", bindings.GetErrors()));
            Assert.IsNotNull(RuntimePropVisualBinder.DefaultCatalog, "Resources/Catalogs/PropCatalog");
            var root = new GameObject("markers");
            _objects.Add(root);
            var view = new ComponentMarkerView(root.transform, bindings);
            view.Rebuild(new List<GdDict>
            {
                new GdDict { { "component_instance_id", "ci_1" }, { "component_id", "console_generic" }, { "world_position", new Vec3(2f, 0f, 3f) } },
                new GdDict { { "component_instance_id", "ci_2" }, { "component_id", "pump" }, { "world_position", new Vec3(6f, 0f, 3f) } },
            });
            Assert.AreEqual(2, view.Markers.Count);
            GameObject bound = view.Markers[0];
            Assert.IsTrue(ComponentMarkerView.IsImported(bound), "console_generic mounts its prop prefab");
            Assert.AreEqual(ComponentMarkerView.VisualSourceImported, view.RecordFor(bound).GetString("visual_source"));
            Assert.IsNull(bound.transform.Find(ComponentMarkerView.FallbackName), "no placeholder box under an imported visual");
            Assert.Greater(bound.GetComponentsInChildren<Renderer>().Length, 0);
            Assert.IsEmpty(bound.GetComponentsInChildren<Collider>(true), "imported visuals stay collider-free");
            Assert.IsFalse(ComponentMarkerView.IsImported(view.Markers[1]), "an unbound component id falls back");
        }

        // ------------------------------------------------------------------ D3 threat feedback

        [Test]
        public void ThreatViewLungesOnPlayerHitsPunchesOnWeaponHitsAndPlaysADeath()
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            var root = new GameObject("threats");
            _objects.Add(root);
            var view = new ThreatPlaceholderView(root.transform);
            view.PlayerPosition = () => Vector3.zero;
            var hits = new List<double>();
            var handled = new List<string>();
            var deaths = new List<string>();
            view.PlayerHit += (damage, id, archetype, at) => hits.Add(damage);
            view.AttackHandled += (id, kind) => handled.Add(kind);
            view.DeathPlayed += id => deaths.Add(id);
            view.Bind(s.ThreatManager);
            Assert.Greater(view.Count, 0, "the golden session's fallback threats have placeholders");

            for (int i = 0; i < 80 && hits.Count == 0; i++)
            {
                rig.Clock.Advance(0.25);
                s.Tick(TickContext.Frame(0.25, rig.Scene.PlayerPosition));
            }
            Assert.IsNotEmpty(hits, "a threat hit the idle player");
            Assert.Contains(ThreatRuntime.ATTACK_TARGET_PLAYER, handled);
            Assert.Greater(view.Animator.ActiveCount, 0, "the attacker lunges");
            view.Animator.Step(1f);
            Assert.AreEqual(0, view.Animator.ActiveCount);

            s.InventoryState.AddItem("crowbar", 1);
            Assert.IsTrue(s.EquipmentState.Equip("crowbar").GetBool("ok"));
            int before = view.Count;
            for (int i = 0; i < 40 && deaths.Count == 0; i++)
            {
                s.AttackWithEquippedWeapon();
                rig.Clock.Advance(0.25);
                s.Tick(TickContext.Frame(0.25, rig.Scene.PlayerPosition));
            }
            Assert.Contains(ThreatRuntime.ATTACK_TARGET_THREAT, handled);
            Assert.IsNotEmpty(deaths, "a kill plays the death effect");
            Assert.Less(view.Count, before, "the dead threat left the view");
            Assert.IsNotNull(root.transform.Find("ThreatDying_" + deaths[0]), "the dying node plays out before removal");
            view.Animator.Step(1f);
            Assert.IsNull(root.transform.Find("ThreatDying_" + deaths[0]));
            view.Unbind();
        }
    }
}
