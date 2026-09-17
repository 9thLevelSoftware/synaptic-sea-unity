using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// The W1a integration contracts the Runtime / UI bind to: kit paths per loaded ship (A4), the single TutorialState and
    /// player_moved (A5), wound treatment through the session (A6), hold vs tap work (B3), run difficulty / biome on the
    /// home ship (C4), the threat attack event (D3), wounds ticking (E1), wounds / chart / tutorial persistence (E2) and
    /// manual-slot restores (E3).
    /// </summary>
    public class SessionIntegrationTests
    {
        const string V0 = "res://data/kits/ship_structural_v0.json";
        const double Step = 0.25;

        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Engine = _previousEngine;
        }

        static SessionHarness.Rig Boot(System.Action<RunSessionDeps> configure = null)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            configure?.Invoke(deps);
            rig.Session = RunSession.Create(deps);
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            return rig;
        }

        static void TickSeconds(SessionHarness.Rig rig, double seconds, bool interactHeld = false)
        {
            for (double t = 0; t < seconds - 1e-9; t += Step)
            {
                rig.Clock.Advance(Step);
                rig.Session.Tick(TickContext.Frame(Step, rig.Scene.PlayerPosition, false, false, interactHeld));
            }
        }

        // ================================================================== A4 kit paths

        [Test]
        public void A4_EveryLoadedShip_ReportsTheKitDocumentItWasBuiltFrom()
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            Assert.AreEqual(V0, s.KitPath);
            Assert.AreEqual(V0, rig.Host.LastHomeKitPath, "the home loader receives the resolved kit");
            Assert.AreEqual(V0, s.KitPathForRoot(s.Loader));
            Assert.AreEqual(V0, s.KitPathForShip(s.HomeShip));

            string lifeboatKitId = s.LifeboatShip.BuiltLayout.GetString("kit_id");
            Assert.AreEqual(LifeBoatBuilder.ResolveKitPath(lifeboatKitId), rig.Host.LastLifeboatKitPath);
            Assert.AreEqual(rig.Host.LastLifeboatKitPath, s.KitPathForShip(s.LifeboatShip));

            s.ForceRepairAll();
            s.ThreatManager.Threats.Clear();
            GdDict result = null;
            foreach (string id in s.ScannableMarkerIds())
            {
                result = s.TravelToMarkerId(id);
                if (result.GetBool("success"))
                    break;
            }
            Assert.IsNotNull(result);
            Assert.IsTrue(result.GetBool("success"), GdJson.Stringify(result));
            string derelictKit = s.KitPathForShip(s.CurrentShip);
            Assert.AreEqual(rig.Host.DerelictKitPaths.Last(), derelictKit, "derelict kit = ShipDocuments.KitPath");
            Assert.IsTrue(CatalogRegistry.Exists(derelictKit), derelictKit);
        }

        [Test]
        public void A4_KitsWithoutAWrapperMap_FallBackToV0_AndAnEmptyPathResolvesFromTheLayout()
        {
            Assert.AreEqual(V0, LifeBoatBuilder.ResolveKitPath("ship_structural_hazard"));
            Assert.AreEqual(V0, LifeBoatBuilder.ResolveKitPath("ship_structural_industrial"));
            Assert.AreEqual("res://data/kits/ship_structural_biomatter.json", LifeBoatBuilder.ResolveKitPath("ship_structural_biomatter"));
            Assert.AreEqual(V0, LifeBoatBuilder.ResolveKitPath("no_such_kit"));

            SessionHarness.Rig hazard = Boot(d => d.KitPath = "res://data/kits/ship_structural_hazard.json");
            Assert.AreEqual(V0, hazard.Session.KitPath);
            Assert.AreEqual(V0, hazard.Host.LastHomeKitPath);

            SessionHarness.Rig biomatter = Boot(d => d.KitPath = "res://data/kits/ship_structural_biomatter.json");
            Assert.AreEqual("res://data/kits/ship_structural_biomatter.json", biomatter.Host.LastHomeKitPath);

            SessionHarness.Rig fromLayout = Boot(d => d.KitPath = "");
            Assert.AreEqual(V0, fromLayout.Host.LastHomeKitPath, "golden layout kit_id is ship_structural_v0");
        }

        // ================================================================== A5 tutorial

        [Test]
        public void A5_TutorialState_IsOneInstanceForTheSession_AndPlayerMovedFiresOnce()
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            TutorialState tutorial = s.TutorialState;
            Assert.IsNotNull(tutorial);
            var shown = new List<string>();
            tutorial.Triggered += (id, title, body) => shown.Add(id);
            int resets = 0;
            s.Events.TutorialStateReset += t =>
            {
                Assert.AreSame(tutorial, t);
                resets++;
            };

            s.OnPlayerMoved();
            s.OnPlayerMoved();
            CollectionAssert.AreEqual(new[] { "first_move" }, shown);

            Assert.IsTrue(s.RequestSave());
            Assert.IsTrue(s.RequestLoad());
            Assert.AreSame(tutorial, s.TutorialState, "reloads keep the instance the UI bound to");
            Assert.GreaterOrEqual(resets, 1);
            Assert.IsTrue(tutorial.GetSummary().GetArrayOrEmpty("fired_keys").Contains("player_moved|any"), "restored from the save");
        }

        // ================================================================== A6 wound treatment

        [Test]
        public void A6_WoundTreatment_ConsumesItems_PlaysSfx_Trains_AndExplainsRefusals()
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            string wound = s.WoundState.ApplyWound(new GdDict { { "kind", "laceration" }, { "body_part", "arm" }, { "severity", 0.6 } });
            var results = new List<GdDict>();
            s.Events.WoundTreatmentResult += r => results.Add(r);

            GdDict row = s.GetTreatableWounds().Cast<GdDict>().Single();
            Assert.AreEqual(wound, row.GetString("wound_id"));
            Assert.IsFalse(row.GetBool("can_bandage"));
            Assert.AreEqual("no_bandage_item", row.GetString("bandage_reason"));
            Assert.AreEqual("no_treatment_item", row.GetString("treat_reason"));

            GdDict denied = s.BandageWound(wound);
            Assert.IsFalse(denied.GetBool("ok"));
            Assert.AreEqual("no_bandage_item", denied.GetString("reason"));
            Assert.AreEqual(AudioEventSeam.UI_PANEL_CLOSE, s.AudioManager.PlayedSfx.Last());

            s.InventoryState.AddItem("bandage_kit", 2);
            row = s.GetTreatableWounds().Cast<GdDict>().Single();
            Assert.IsTrue(row.GetBool("can_bandage"));
            Assert.AreEqual("bandage_kit", row.GetString("bandage_item_id"));
            GdDict ok = s.BandageWound(wound);
            Assert.IsTrue(ok.GetBool("ok"), GdJson.Stringify(ok));
            Assert.AreEqual("bandage_kit", ok.GetString("item_id"));
            Assert.AreEqual(1, s.InventoryState.GetQuantity("bandage_kit"));
            Assert.IsTrue(s.AudioManager.PlayedSfx.Contains(AudioEventSeam.SFX_WOUND_BANDAGE));
            Assert.IsTrue(s.WoundState.GetWound(wound).GetBool("bandaged"));
            Assert.AreEqual("already_bandaged", s.BandageWound(wound).GetString("reason"));
            Assert.AreEqual(1, s.InventoryState.GetQuantity("bandage_kit"), "a refusal consumes nothing");

            s.InventoryState.AddItem("medkit", 1);
            double severityBefore = s.WoundState.GetWound(wound).GetFloat("severity");
            Assert.IsTrue(s.TryTreatWound(wound));
            Assert.AreEqual(0, s.InventoryState.GetQuantity("medkit"));
            Assert.IsTrue(s.AudioManager.PlayedSfx.Contains(AudioEventSeam.SFX_WOUND_TREAT));
            Assert.Less(s.WoundState.GetWound(wound).GetFloat("severity"), severityBefore);
            Assert.AreEqual("already_treated", s.TreatWound(wound).GetString("reason"));
            Assert.AreEqual("unknown_wound", s.TreatWound("w999").GetString("reason"));

            Assert.AreEqual(6, results.Count, "every request reports its result");
            var events = s.TrainingEventBus.GetLog().Cast<GdDict>().Select(e => e.GetString("event_id")).ToList();
            Assert.AreEqual(1, events.Count(e => e == "bandage_wound"), string.Join(",", events));
            Assert.AreEqual(1, events.Count(e => e == "treat_wound"), string.Join(",", events));
        }

        // ================================================================== B3 hold vs tap

        /// <summary>Starts a welding-lance cut on a golden-ship structural module through the real interact dispatch.</summary>
        static bool StartPryWork(SessionHarness.Rig rig)
        {
            RunSession s = rig.Session;
            Assert.AreEqual(1, s.InventoryState.AddItem("welding_lance", 1));
            foreach (object roomV in s.Loader.LayoutDoc.GetArrayOrEmpty("rooms"))
            {
                foreach (object pV in ((GdDict)roomV).GetArrayOrEmpty("structural_placements"))
                {
                    var p = (GdDict)pV;
                    if (!(p.Get("world_position", null) is GdArray a) || a.Count < 3)
                        continue;
                    rig.Scene.PlayerPosition = new Vec3(V.F64(a[0]), V.F64(a[1]), V.F64(a[2]));
                    s.RequestInteract();
                    if (s.WorkActionDriver.IsWorking())
                        return true;
                }
            }
            return false;
        }

        [Test]
        public void B3_HoldMode_ProgressesOnlyWhileHeld()
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            Assert.IsTrue(s.HoldToWorkEnabled, "hold_to_tap defaults off");
            Assert.IsFalse(s.BeginWorkHold(), "nothing in progress: dispatch the interact");
            Assert.IsTrue(StartPryWork(rig), "a pry started");
            Assert.IsTrue(s.WorkRequiresHold);

            TickSeconds(rig, 0.5);
            double held = s.WorkActionDriver.ProgressRatio();
            Assert.Greater(held, 0.0, "progress while the hold latch is on");

            s.EndWorkHold();
            TickSeconds(rig, 0.5);
            Assert.AreEqual(held, s.WorkActionDriver.ProgressRatio(), "released: progress pauses");
            Assert.IsTrue(s.WorkActionDriver.IsWorking());

            Assert.IsTrue(s.BeginWorkHold(), "a press on an active action resumes it");
            Assert.IsTrue(s.WorkActionDriver.IsWorking(), "hold mode never cancels on press");
            TickSeconds(rig, 0.5);
            Assert.Greater(s.WorkActionDriver.ProgressRatio(), held);
        }

        [Test]
        public void B3_TapMode_RunsWithoutHolding_AndATapCancels()
        {
            SessionHarness.Rig rig = Boot(d => d.SettingsState = new SettingsState());
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            Assert.IsTrue(s.SettingsState.SetHoldToTap(true));
            Assert.IsFalse(s.HoldToWorkEnabled);
            Assert.IsTrue(StartPryWork(rig));
            Assert.IsFalse(s.WorkRequiresHold, "tap mode");

            TickSeconds(rig, 0.5);
            Assert.Greater(s.WorkActionDriver.ProgressRatio(), 0.0, "progress with nothing held");
            Assert.IsTrue(s.BeginWorkHold(), "the tap was consumed");
            s.EndWorkHold();
            Assert.IsFalse(s.WorkActionDriver.IsWorking(), "a tap cancels");
        }

        [Test]
        public void B3_SwitchingToTapMidAction_ReleasesTheHold()
        {
            SessionHarness.Rig rig = Boot(d => d.SettingsState = new SettingsState());
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            Assert.IsTrue(StartPryWork(rig));
            s.EndWorkHold();
            TickSeconds(rig, 0.5);
            Assert.AreEqual(0.0, s.WorkActionDriver.ProgressRatio(), "hold mode, not held");
            s.SettingsState.SetHoldToTap(true);
            TickSeconds(rig, 0.5);
            Assert.Greater(s.WorkActionDriver.ProgressRatio(), 0.0);
        }

        // ================================================================== C4 run difficulty / biome

        [Test]
        public void C4_Standard_IsIdentity()
        {
            RunSession s = Boot().Session;
            Assert.AreEqual("standard", s.DifficultyId);
            Assert.AreEqual("", s.BiomeId);
            foreach (string dial in DifficultyProfile.ALL_DIALS)
                Assert.AreEqual(1.0, s.HomeDial(dial), dial);
            Assert.AreEqual(1.0, s.ThreatManager.EncounterDensityModifier);
            Assert.AreEqual(1.0, s.ThreatManager.AggressionModifier);
            Assert.AreEqual(5, s.ThreatManager.Threats.Count);
            Assert.AreEqual(OxygenState.DEFAULT_DRAIN_RATE, V.F64(s.GetOxygenSummary().Get("drain_rate", 0.0)));
            Assert.AreEqual(17L, s.RunSeed);
        }

        [Test]
        public void C4_HardenedDeadFleet_ScalesTheHomeShip_AndIsSavedAndRestored()
        {
            RunSession standard = Boot().Session;
            var standardDamage = standard.ThreatManager.Threats.Select(t => t.AttackDamage).ToList();

            SessionHarness.Rig rig = Boot(d =>
            {
                d.DifficultyId = "hardened";
                d.BiomeId = "dead_fleet";
                d.RunSeed = 4242;
            });
            RunSession s = rig.Session;
            DifficultyProfile hardened = DifficultyProfile.ForId("hardened");
            BiomeProfile deadFleet = BiomeProfile.FromDict(new ShipLayoutGenerator().ResolveBiome("dead_fleet"));
            double encounter = DifficultyProfile.CombinedModifier(deadFleet, hardened, DifficultyProfile.DIAL_ENCOUNTER);
            double hazard = DifficultyProfile.CombinedModifier(deadFleet, hardened, DifficultyProfile.DIAL_HAZARD);
            Assert.AreEqual(encounter, s.ThreatManager.EncounterDensityModifier, 1e-12);
            Assert.AreEqual(hazard, s.ThreatManager.AggressionModifier, 1e-12);

            // Five fallback markers of count 1 with fractional carry: floor(5 * density) threats.
            Assert.AreEqual((int)System.Math.Floor(5 * encounter + 1e-9), s.ThreatManager.Threats.Count);
            ThreatAIState first = s.ThreatManager.Threats.First(t => t.ArchetypeId == standard.ThreatManager.Threats[0].ArchetypeId);
            Assert.AreEqual(standardDamage[0] * hazard, first.AttackDamage, 1e-9, "aggression scales attack damage");
            Assert.AreEqual(OxygenState.DEFAULT_DRAIN_RATE * hazard, V.F64(s.GetOxygenSummary().Get("drain_rate", 0.0)), 1e-12, "hazard scales the home breach drain");
            Assert.AreEqual("ship_structural_industrial", rig.Host.LastLifeboatLayoutKitId, "the run biome skins the lifeboat");
            Assert.AreEqual(hazard > 0 ? DifficultyProfile.CombinedModifier(deadFleet, hardened, DifficultyProfile.DIAL_AMBIENT) : 0, s.HomeAmbientIntensity, 1e-12);

            RunSnapshot snap = RunSnapshotAssembler.Build(s);
            Assert.IsTrue(V.VariantEquals(new GdDict { { "seed", 4242L }, { "biome_id", "dead_fleet" }, { "difficulty_id", "hardened" } }, snap.RunContext));
            Assert.IsTrue(s.RequestSave());

            // A default-context session sharing the storage adopts the saved context on load.
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig other);
            deps.Storage = rig.Storage;
            RunSession loaded = RunSession.Create(deps);
            Assert.IsTrue(loaded.RequestLoad());
            Assert.AreEqual("hardened", loaded.DifficultyId);
            Assert.AreEqual("dead_fleet", loaded.BiomeId);
            Assert.AreEqual(4242L, loaded.RunSeed);
        }

        // ================================================================== D3 threat attack event

        [Test]
        public void D3_ThreatAttacked_IsRaisedForPlayerAndStructureHits_AndWeaponHits()
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            var events = new List<(string id, string kind, double damage, GdDict result)>();
            s.ThreatManager.ThreatAttacked += (id, kind, damage, result) => events.Add((id, kind, damage, result));
            foreach (ThreatAIState t in s.ThreatManager.Threats)
                t.StructureDamage = 1.5;

            TickSeconds(rig, 15.0);
            var playerHits = events.Where(e => e.kind == ThreatRuntime.ATTACK_TARGET_PLAYER).ToList();
            Assert.IsNotEmpty(playerHits, "the idle player is attacked");
            var last = playerHits.Last();
            Assert.Greater(last.damage, 0.0);
            Assert.IsInstanceOf<Vec3>(last.result.Get("position"));
            GdDict lastAttack = s.ThreatManager.LastAttackResult.DeepCopy();
            GdDict withoutPosition = last.result.DeepCopy();
            withoutPosition.Erase("position");
            Assert.IsTrue(V.VariantEquals(lastAttack, withoutPosition), "the event carries LastAttackResult");
            Assert.AreEqual(lastAttack.GetString("source_id"), last.id);
            Assert.IsTrue(events.Any(e => e.kind == ThreatRuntime.ATTACK_TARGET_STRUCTURE && e.damage == 1.5));

            // Weapon hits: a fresh session, crowbar equipped.
            SessionHarness.Rig armed = Boot();
            RunSession a = armed.Session;
            var weaponHits = new List<string>();
            var kills = new List<GdDict>();
            a.ThreatManager.ThreatAttacked += (id, kind, damage, result) =>
            {
                if (kind == ThreatRuntime.ATTACK_TARGET_THREAT)
                    weaponHits.Add(id);
            };
            a.ThreatManager.ThreatKilled += r => kills.Add(r);
            a.InventoryState.AddItem("crowbar", 1);
            Assert.IsTrue(a.EquipmentState.Equip("crowbar").GetBool("ok"));
            for (int i = 0; i < 40 && kills.Count == 0; i++)
            {
                a.AttackWithEquippedWeapon();
                armed.Clock.Advance(Step);
                a.Tick(TickContext.Frame(Step, armed.Scene.PlayerPosition));
            }
            Assert.IsNotEmpty(weaponHits);
            Assert.IsNotEmpty(kills, "a threat died");
            Assert.IsInstanceOf<Vec3>(kills[0].Get("position"), "ThreatKilled carries the position");
        }

        // ================================================================== E1 wounds tick

        [Test]
        public void E1_Wounds_Bleed_Heal_AndOpenFromCombatDamage()
        {
            SessionHarness.Rig control = Boot();
            SessionHarness.Rig rig = Boot();
            control.Session.ThreatManager.Threats.Clear();
            rig.Session.ThreatManager.Threats.Clear();
            RunSession s = rig.Session;
            string wound = s.WoundState.ApplyWound(new GdDict { { "kind", "puncture" }, { "body_part", "leg" }, { "severity", 0.4 } });

            TickSeconds(control, 2.0);
            TickSeconds(rig, 2.0);
            Assert.Less(s.VitalsState.Health, control.Session.VitalsState.Health, "bleeding costs health");
            Assert.Greater(s.WoundState.GetWound(wound).GetFloat("age_seconds"), 1.9, "the wounds stage ticks");
            Assert.Less(s.VitalsState.Thirst, control.Session.VitalsState.Thirst, "bleeding raises thirst");

            s.InventoryState.AddItem("medkit", 1);
            Assert.IsTrue(s.TryTreatWound(wound));
            double treated = s.WoundState.GetWound(wound).GetFloat("severity");
            int changed = 0;
            s.Events.WoundsChanged += w => changed++;
            TickSeconds(rig, 10.0);
            Assert.AreEqual(treated - 10.0 * RunSession.WOUND_TREATED_HEAL_PER_SECOND, s.WoundState.GetWound(wound).GetFloat("severity"), 1e-9, "treated wounds heal");
            TickSeconds(rig, 5.0);
            Assert.IsFalse(s.SliceComplete);
            Assert.AreEqual(0, s.WoundState.ActiveCount(), "healed shut");
            Assert.GreaterOrEqual(changed, 1);

            // Combat damage opens wounds.
            SessionHarness.Rig fight = Boot();
            TickSeconds(fight, 8.0);
            Assert.Greater(fight.Session.WoundState.ActiveCount(), 0, "threat hits opened a wound");
        }

        // ================================================================== E2 persistence

        [Test]
        public void E2_WoundsChartAndTutorial_SurviveWorldSaveAndLoad()
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            s.WoundState.ApplyWound(new GdDict { { "kind", "burn" }, { "body_part", "arm" }, { "severity", 0.4 } });
            s.WebChartState.RecordViews(GdArray.Of(new GdDict
            {
                { "marker_id", "3:1:0" }, { "position", GdArray.Of(1.0, 0.0, 2.0) }, { "size_class", 1L }, { "ship_type", "freighter" },
            }), 3);
            s.OnPlayerMoved();
            Assert.IsTrue(s.DismissLatestTutorial());
            GdDict wounds = s.WoundState.GetSummary();
            GdDict chart = s.WebChartState.GetSummary();
            GdDict tutorial = s.TutorialState.GetSummary();
            Assert.AreEqual(1L, tutorial.GetInt("codex_unlock_count"));

            Assert.IsTrue(s.RequestSave());
            s.WoundState.Clear();
            s.WebChartState.ApplySummary(null);
            TickSeconds(rig, 1.0);
            Assert.IsTrue(s.RequestLoad());

            Assert.IsTrue(V.VariantEquals(JsonRoundTrip(wounds), JsonRoundTrip(s.WoundState.GetSummary())), GdJson.Stringify(s.WoundState.GetSummary()));
            Assert.IsTrue(V.VariantEquals(JsonRoundTrip(chart), JsonRoundTrip(s.WebChartState.GetSummary())), GdJson.Stringify(s.WebChartState.GetSummary()));
            Assert.IsTrue(V.VariantEquals(JsonRoundTrip(tutorial), JsonRoundTrip(s.TutorialState.GetSummary())), GdJson.Stringify(s.TutorialState.GetSummary()));
            Assert.IsTrue(s.TutorialState.IsCodexUnlocked("first_move"));
        }

        static object JsonRoundTrip(object v) => GdJson.Parse(GdJson.Stringify(v, "\t"));

        [Test]
        public void E2_MigrationGivesRunFourSavesTheEmptyDefaults()
        {
            var run4 = new GdDict { { "slice_version", "gate2-current-run-4" }, { "godot_version", "x" }, { "wound_summary", new GdDict { { "schema", "kept" } } } };
            GdDict result = new SaveMigrationService().MigrateRun(run4);
            Assert.IsTrue(result.GetBool("migrated"));
            var d = (GdDict)result["dict"];
            Assert.AreEqual(SaveMigrationService.TargetVersion, d.GetString("slice_version"));
            foreach (object key in RunSnapshot.PortExtensionFields)
                Assert.IsTrue(d.Has(key), V.Str(key));
            Assert.AreEqual("kept", d.GetDictOrEmpty("wound_summary").GetString("schema"), "present keys are never overwritten");
            Assert.IsTrue(d.GetDictOrEmpty("tutorial_summary").IsEmpty);
            Assert.IsFalse(run4.Has("tutorial_summary"), "input not mutated");
        }

        [Test]
        public void E2_MigrationGivesRunFiveSavesTheEmptyDefaults()
        {
            var run5 = new GdDict { { "slice_version", "gate2-current-run-5" }, { "godot_version", "x" }, { "run_context", new GdDict { { "seed", 5L } } } };
            GdDict result = new SaveMigrationService().MigrateRun(run5);
            Assert.IsTrue(result.GetBool("migrated"));
            var d = (GdDict)result["dict"];
            Assert.AreEqual("gate2-current-run-6", d.GetString("slice_version"));
            foreach (object key in SaveMigrationService.V6Defaults.Keys)
                Assert.IsTrue(V.VariantEquals(SaveMigrationService.V6Defaults[key], d[key]), V.Str(key));
            Assert.AreEqual(5L, d.GetDictOrEmpty("run_context").GetInt("seed"), "present keys are never overwritten");
            Assert.IsFalse(d.Has("wound_summary"), "a run-5 save only gains the run-6 keys");
            Assert.IsFalse(run5.Has("visited_ships"), "input not mutated");
        }

        // ================================================================== E3 manual slots

        [Test]
        public void E3_ManualSlot_RestoresEquipment_HomeLoot_AndCargo()
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            s.ThreatManager.Threats.Clear();
            Assert.IsTrue(s.EquipmentState.Equip("crowbar").GetBool("ok"));
            s.HomeShip.LootedContainerIds.Add("start_supply_a");
            s.HomeShip.GetInventory().AddItem("scrap_metal", 3);
            CartState savedCart = CartState.Create("slot_test_cart", 50.0);
            savedCart.GetHold().AddItem("scrap_metal", 2);
            s.HomeShip.GetCarts().Add(savedCart);
            s.HomeShip.BreachEnvironmentSummary = new GdDict { { "hazard_kind", "oxygen" }, { "breach_open", false } };
            Assert.IsTrue(s.MetaProgressionState.UnlockCodexEntry("slot_test_entry"));
            Assert.IsTrue(s.UniqueItemState.Claim("slot_test_unique", "slot_seed"));
            ShipInstance visited = ShipInstance.Create("slot_test_ship", "9:9:9", new ShipBlueprint(), new ShipSystemsManager(), null);
            s.VisitedShips["9:9:9"] = visited;
            RunSnapshot slot = RunSnapshotAssembler.Build(s);
            Assert.IsNotNull(slot);
            Assert.IsFalse(slot.HomeShipCarts.IsEmpty);
            Assert.IsFalse(slot.HomeBreachEnvironment.IsEmpty);
            Assert.IsTrue(slot.VisitedShips.Has("9:9:9"));

            s.EquipmentState.Unequip("primary_hand");
            Assert.IsTrue(s.EquipmentState.Equip("welding_lance").GetBool("ok"));
            s.HomeShip.GetCarts().Clear();
            s.MetaProgressionState.UnlockedCodexEntryIds.Clear();
            s.UniqueItemState.Reset();
            TickSeconds(rig, 1.0);

            Assert.IsTrue(s.ApplyManualSlot(slot));
            Assert.AreEqual("crowbar", s.EquipmentState.GetEquipped("primary_hand"), "equipment comes from the slot, not the live run");
            Assert.IsTrue(s.HomeShip.LootedContainerIds.Contains("start_supply_a"), "searched containers stay searched");
            Assert.AreEqual(3, s.HomeShip.GetInventory().GetQuantity("scrap_metal"), "home cargo restored");
            LootContainer searched = s.LootContainers.FirstOrDefault(c => c.ContainerId == "start_supply_a");
            if (searched != null)
                Assert.IsTrue(searched.Searched, "the rebuilt container reads as searched");

            // gate2-current-run-6: carts, breach environment, meta progression, unique items and visited ships.
            CartState restoredCart = s.HomeShip.GetCarts().FirstOrDefault(c => c.CartId == "slot_test_cart");
            Assert.IsNotNull(restoredCart, "home carts restored");
            Assert.AreEqual(2, restoredCart.GetHold().GetQuantity("scrap_metal"), "cart hold restored");
            foreach (CartState cart in s.HomeShip.GetCarts())
                Assert.AreEqual(1, s.CartControls.Count(c => c.CartId == cart.CartId), "one control per cart: " + cart.CartId);
            Assert.IsFalse(s.HomeShip.BreachEnvironmentSummary.IsEmpty, "home breach environment restored");
            Assert.IsTrue(s.MetaProgressionState.IsCodexEntryUnlocked("slot_test_entry"), "meta progression restored");
            Assert.IsTrue(s.UniqueItemState.IsClaimed("slot_test_unique"), "unique items restored");
            Assert.IsTrue(s.VisitedShips.ContainsKey("9:9:9"), "visited ships restored");
            Assert.AreEqual("slot_test_ship", s.VisitedShips["9:9:9"].ShipId);
        }
    }
}
