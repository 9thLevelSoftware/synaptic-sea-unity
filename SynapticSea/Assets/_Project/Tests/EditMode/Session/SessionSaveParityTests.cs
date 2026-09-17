using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// Coordinator save parity against fixtures/godot/save/capture_main_playable (parity_capture_saves.gd --mode main:
    /// boot the golden ship, complete objective 1, request_save, then write get_last_saved_snapshot() to slot_01,
    /// quicksave and current_run) and fixtures/godot/save_roundtrip (parity_capture_roundtrip_wear.gd --mode roundtrip:
    /// Godot's own load_from_slot / load_world -> _apply_*_snapshot -> _build_*_snapshot -> save of each captured file on a
    /// freshly booted golden ship; see its README.json).
    ///
    /// (1) Played run: replaying the capture flow on a headless RunSession and writing through the real SaveLoadService
    ///     must reproduce every captured file exactly on Godot's keys (type-aware; only wall-clock stamps ignored).
    /// (2) Apply -> Build: loading each captured file into a fresh session and building it back must equal what Godot
    ///     itself wrote after the same load-then-save, exactly on Godot's keys.
    /// The port's gate2-current-run-5 keys (<see cref="RunSnapshot.PortExtensionFields"/>) are compared through
    /// <see cref="SynapticSea.Tests.Parity.SavePortSchema.GodotView"/> and asserted separately against the live models.
    /// </summary>
    public class SessionSaveParityTests
    {
        const string Root = "godot/save/capture_main_playable/";
        const string RoundTripRoot = "godot/save_roundtrip/";
        const string CapturedRunId = "1647935-c8a8";

        /// <summary>The only keys ignored: wall-clock stamps (top level, embedded home slice, meta_progression_summary).</summary>
        public static readonly string[] VolatileKeys = { "saved_at", "saved_at_epoch" };

        /// <summary>
        /// The paths (indices normalised to []) where Godot's own load-then-save differs from the file it loaded, as
        /// captured in fixtures/godot/save_roundtrip. Only documents the capture (asserted against Godot's two files, not
        /// against the port):
        /// <list type="bullet">
        /// <item>int -> float: Godot's JSON.parse returns every number as a float and these nested arrays/dicts are stored
        ///   by apply_summary without coercion, so the next save writes them as floats.</item>
        /// <item>power_grid / propulsion / sustenance summaries: <c>_apply_run_snapshot</c> calls
        ///   <c>_recompute_expanded_ship_systems(0.0)</c> (gd 10582) after the manager apply.</item>
        /// <item>crafting_summary.station_summaries.*: the restore build path re-runs <c>_build_crafting_stations</c>.</item>
        /// <item>sfx_router.caption_queue_size: SfxEventRouter.apply_summary does not restore the caption queue.</item>
        /// </list>
        /// </summary>
        public static readonly string[] GodotLoadDerivedPaths =
        {
            "$.audio_summary.sfx_router.caption_queue_size",
            "$.component_placement_summary.placed[].cell[]",
            "$.component_placement_summary.placed[].slot_index",
            "$.crafting_summary.station_summaries.fabricator",
            "$.crafting_summary.station_summaries.kitchen",
            "$.crafting_summary.station_summaries.medbay",
            "$.crafting_summary.station_summaries.salvage",
            "$.crafting_summary.station_summaries.synthesizer",
            "$.crafting_summary.station_summaries.workbench",
            "$.inventory_summary.threat_summary.encounter_markers[].cell[]",
            "$.inventory_summary.threat_summary.encounter_markers[].count",
            "$.inventory_summary.threat_summary.threats[].cell[]",
            "$.ship_systems_summary.power_grid_summary.available_supply_units",
            "$.ship_systems_summary.power_grid_summary.blackout_subsystems",
            "$.ship_systems_summary.power_grid_summary.effective_routes_units.life_support",
            "$.ship_systems_summary.power_grid_summary.effective_routes_units.propulsion",
            "$.ship_systems_summary.power_grid_summary.effective_routes_units.sustenance",
            "$.ship_systems_summary.power_grid_summary.manager_broken_systems",
            "$.ship_systems_summary.power_grid_summary.overloaded",
            "$.ship_systems_summary.propulsion_state_summary.operational",
            "$.ship_systems_summary.propulsion_state_summary.thrust_percent",
            "$.ship_systems_summary.sustenance_state_summary.total_materials_consumed",
        };

        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Engine = _previousEngine;
        }

        public static IEnumerable<TestCaseData> RunSaves()
        {
            yield return new TestCaseData(Root + "mid_run/saves/current_run.json", SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID, SaveSlotState.SlotKindAuto, false, "current_run_alias");
            yield return new TestCaseData(Root + "mid_run/saves/quicksave.json", SaveSlotState.QuicksaveSlotId, SaveSlotState.SlotKindQuick, true, "Quicksave");
            yield return new TestCaseData(Root + "mid_run/saves/slot_01.json", "slot_01", SaveSlotState.SlotKindManual, false, "Manual 1");
            yield return new TestCaseData(Root + "after_completion/saves/quicksave.json", SaveSlotState.QuicksaveSlotId, SaveSlotState.SlotKindQuick, true, "Quicksave");
            yield return new TestCaseData(Root + "after_completion/saves/slot_01.json", "slot_01", SaveSlotState.SlotKindManual, false, "Manual 1");
            yield return new TestCaseData(Root + "residual/saves/quicksave.json", SaveSlotState.QuicksaveSlotId, SaveSlotState.SlotKindQuick, true, "Quicksave");
            yield return new TestCaseData(Root + "residual/saves/slot_01.json", "slot_01", SaveSlotState.SlotKindManual, false, "Manual 1");
        }

        static string SlotFile(string slotId) =>
            slotId == SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID ? SaveLoadService.SAVE_PATH : "user://saves/" + slotId + ".json";

        static TreeDiff.Options Strict()
        {
            var o = new TreeDiff.Options { MaxDifferences = 500 };
            foreach (string k in VolatileKeys)
                o.IgnoreKeys.Add(k);
            return o;
        }

        static object ReadWritten(MemoryStorage storage, string path) => GdJson.Parse(storage.ReadText(path), exactNumbers: true);

        static SessionHarness.Rig BootWithCapturedEngine(GdDict original)
        {
            CoreServices.Engine = new FixedEngineInfo(original.GetString("godot_version"));
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            return rig;
        }

        static string[] NormalisedPaths(IEnumerable<string> diffs, string prefix) => diffs
            .Select(d => d.Substring(0, d.IndexOf(':')))
            .Select(p => Regex.Replace(p, @"\[\d+\]", "[]"))
            .Select(p => prefix.Length > 0 && p.StartsWith(prefix) ? "$." + p.Substring(prefix.Length) : p)
            .Distinct().OrderBy(p => p, System.StringComparer.Ordinal).ToArray();

        // ------------------------------------------------------------------ port keys

        /// <summary>The gate2-current-run-5 keys of a run dict the session just built equal its live models.</summary>
        static void AssertPortKeysMatchSession(GdDict runDict, RunSession s, string where, GdDict tutorialAtSave = null)
        {
            SameAsModel(runDict, "wound_summary", s.WoundState.GetSummary(), where);
            SameAsModel(runDict, "web_chart_summary", s.WebChartState.GetSummary(), where);
            SameAsModel(runDict, "tutorial_summary", tutorialAtSave ?? s.TutorialState.GetSummary(), where);
            SameAsModel(runDict, "equipment_summary", s.EquipmentState.GetSummary(), where);
            SameAsModel(runDict, "home_looted_containers", s.HomeShip.LootedContainerIds, where);
            SameAsModel(runDict, "home_ship_inventory", s.HomeShip.GetInventory().GetSummary(), where);
            SameAsModel(runDict, "run_context", s.GetRunContextSummary(), where);
            GdDict ctx = runDict.GetDictOrEmpty("run_context");
            Assert.AreEqual("standard", ctx.GetString("difficulty_id"), where);
            Assert.AreEqual("", ctx.GetString("biome_id"), where);
            Assert.AreEqual(17L, ctx.GetInt("seed", -1), where + ": golden blueprint seed");
        }

        static void SameAsModel(GdDict runDict, string key, object model, string where)
        {
            Assert.IsTrue(runDict.Has(key), where + ": missing " + key);
            object expected = GdJson.Parse(GdJson.Stringify(model, "\t"), exactNumbers: true);
            List<string> d = TreeDiff.Compare(expected, runDict.Get(key), Strict());
            Assert.IsEmpty(d, where + " " + key + ":\n" + TreeDiff.Format(d));
        }

        static GdDict GodotView(GdDict tree) => SynapticSea.Tests.Parity.SavePortSchema.GodotView(tree);

        static string RoundTripPath(string rel) => RoundTripRoot + rel.Substring(Root.Length);

        // ------------------------------------------------------------------ (1) played run

        [Test]
        public void PlayedRun_WritesTheGodotCapture()
        {
            GdDict currentRun = Fixtures.ReadDict(Root + "mid_run/saves/current_run.json");
            SessionHarness.Rig rig = BootWithCapturedEngine(currentRun);
            RunSession s = rig.Session;
            s.SaveLoadService.SetActiveRunId(CapturedRunId);

            Assert.IsTrue(s.CompleteObjectiveSequence(1));
            GdDict worldTutorial = s.TutorialState.GetSummary();
            Assert.IsTrue(s.RequestSave());
            RunSnapshot snapshot = s.LastSavedSnapshot;
            Assert.IsNotNull(snapshot);
            Assert.IsTrue(s.SaveLoadService.SaveToSlot("slot_01", snapshot, SaveSlotState.SlotKindManual, false, "Manual 1"));
            Assert.IsTrue(s.SaveLoadService.SaveToSlot("quicksave", snapshot, SaveSlotState.SlotKindQuick, true, "Quicksave"));
            Assert.IsTrue(s.SaveLoadService.SaveCurrentRun(snapshot));

            var files = new (string fixture, string path)[]
            {
                ("current_run.json", SaveLoadService.SAVE_PATH),
                ("quicksave.json", "user://saves/quicksave.json"),
                ("slot_01.json", "user://saves/slot_01.json"),
                ("world.json", SaveLoadService.WORLD_SLOT_FILE),
            };
            foreach ((string fixture, string path) in files)
            {
                var written = (GdDict)ReadWritten(rig.Storage, path);
                List<string> diffs = TreeDiff.Compare(Fixtures.ReadDict(Root + "mid_run/saves/" + fixture), GodotView(written), Strict());
                Assert.IsEmpty(diffs, fixture + ":\n" + TreeDiff.Format(diffs));
                // The run files hold the objective-1 checkpoint snapshot; world.json was built by request_save before its
                // run_saved tutorial fired. Everything else is unchanged since.
                GdDict tutorialAtSave = fixture == "world.json" ? worldTutorial : snapshot.TutorialSummary;
                AssertPortKeysMatchSession(fixture == "world.json" ? written.GetDictOrEmpty("home_ship") : written, s, fixture, tutorialAtSave);
            }
        }

        // ------------------------------------------------------------------ (2) Apply -> Build vs Godot's own load-then-save

        [TestCaseSource(nameof(RunSaves))]
        public void RunSave_ApplyThenBuild_MatchesGodotsLoadThenSave(string rel, string slotId, string slotKind, bool quick, string display)
        {
            GdDict original = Fixtures.ReadDict(rel);
            GdDict godotRoundTrip = Fixtures.ReadDict(RoundTripPath(rel));
            SessionHarness.Rig rig = BootWithCapturedEngine(original);
            RunSession s = rig.Session;

            // Load through the real service read path (Godot parse: every number a double, run-4 -> run-5 migration,
            // then from_dict coercion).
            rig.Storage.WriteText(SlotFile(slotId), Fixtures.ReadText(rel).Replace("\r\n", "\n"));
            RunSnapshot loaded = s.SaveLoadService.LoadFromSlot(slotId);
            Assert.IsNotNull(loaded, "SaveLoadService refused the Godot save");
            Assert.IsTrue(RunSnapshotAssembler.Apply(s, loaded), "RunSnapshotAssembler.Apply");
            Assert.AreEqual(2, s.CurrentObjectiveSequence);

            s.SaveLoadService.SetActiveRunId(original.GetString("run_id"));
            Assert.IsTrue(s.SaveLoadService.SaveToSlot(slotId, RunSnapshotAssembler.Build(s), slotKind, quick, display));
            var written = (GdDict)ReadWritten(rig.Storage, SlotFile(slotId));
            List<string> diffs = TreeDiff.Compare(godotRoundTrip, GodotView(written), Strict());
            Assert.IsEmpty(diffs, rel + " vs Godot's load-then-save:\n" + TreeDiff.Format(diffs));
            AssertPortKeysMatchSession(written, s, rel);
        }

        [Test]
        public void WorldSave_ApplyThenBuild_MatchesGodotsLoadThenSave()
        {
            const string rel = Root + "mid_run/saves/world.json";
            GdDict original = Fixtures.ReadDict(rel);
            GdDict godotRoundTrip = Fixtures.ReadDict(RoundTripPath(rel));
            SessionHarness.Rig rig = BootWithCapturedEngine(original);
            RunSession s = rig.Session;

            rig.Storage.WriteText(SaveLoadService.WORLD_SLOT_FILE, Fixtures.ReadText(rel).Replace("\r\n", "\n"));
            WorldSnapshot loaded = s.SaveLoadService.LoadWorld();
            Assert.IsNotNull(loaded, "SaveLoadService refused the Godot world save");
            Assert.IsTrue(WorldSnapshotAssembler.Apply(s, loaded), "WorldSnapshotAssembler.Apply");

            s.SaveLoadService.SetActiveRunId(original.GetString("run_id"));
            Assert.IsTrue(s.SaveLoadService.SaveWorld(WorldSnapshotAssembler.Build(s)));
            var written = (GdDict)ReadWritten(rig.Storage, SaveLoadService.WORLD_SLOT_FILE);
            List<string> diffs = TreeDiff.Compare(godotRoundTrip, GodotView(written), Strict());
            Assert.IsEmpty(diffs, "world.json vs Godot's load-then-save:\n" + TreeDiff.Format(diffs));
            AssertPortKeysMatchSession(written.GetDictOrEmpty("home_ship"), s, "world.json home_ship");
        }

        /// <summary>The capture itself: Godot's load-then-save changed exactly <see cref="GodotLoadDerivedPaths"/>.</summary>
        [Test]
        public void GodotLoadThenSave_ChangedOnlyTheDocumentedPaths()
        {
            var rels = RunSaves().Select(c => (string)c.Arguments[0]).ToList();
            rels.Add(Root + "mid_run/saves/world.json");
            foreach (string rel in rels)
            {
                bool world = rel.EndsWith("world.json");
                List<string> diffs = TreeDiff.Compare(Fixtures.ReadDict(rel), Fixtures.ReadDict(RoundTripPath(rel)), Strict());
                if (world)
                    Assert.IsTrue(diffs.All(d => d.StartsWith("$.home_ship.")), rel + ": only the embedded home slice may differ:\n" + TreeDiff.Format(diffs));
                CollectionAssert.AreEqual(GodotLoadDerivedPaths, NormalisedPaths(diffs, world ? "$.home_ship." : ""), rel + ":\n" + TreeDiff.Format(diffs));
                AssertOnlyIntFloatWhereExpected(diffs);
            }
        }

        [Test]
        public void SyntheticWorldSnapshotCapture_IsRejectedLikeGodot()
        {
            // capture_world_snapshot is a hand-built WorldSnapshot (world_snapshot smoke), not a coordinator save: its
            // home_ship slice has no godot_version, so _apply_world_snapshot rejects it at RunSnapshot.from_dict.
            const string rel = "godot/save/capture_world_snapshot/user_data/saves/world.json";
            GdDict original = Fixtures.ReadDict(rel);
            SessionHarness.Rig rig = BootWithCapturedEngine(original);
            RunSession s = rig.Session;
            rig.Storage.WriteText(SaveLoadService.WORLD_SLOT_FILE, Fixtures.ReadText(rel).Replace("\r\n", "\n"));
            Assert.IsFalse(s.RequestLoad());
            Assert.IsTrue(s.PlayableStarted);
            Assert.AreEqual(1, s.CurrentObjectiveSequence);
        }

        static void AssertOnlyIntFloatWhereExpected(List<string> diffs)
        {
            foreach (string d in diffs)
            {
                bool typeOnly = Regex.IsMatch(d, @"expected int (-?\d+), got float \1$");
                bool typePath = d.Contains(".cell[") || d.Contains(".slot_index:") || d.Contains(".count:");
                Assert.AreEqual(typePath, typeOnly, "unexpected difference kind: " + d);
            }
        }
    }
}
