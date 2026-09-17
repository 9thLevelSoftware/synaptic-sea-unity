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
    /// quicksave and current_run).
    ///
    /// (1) Played run: replaying that flow on a headless RunSession and writing through the real SaveLoadService must
    ///     reproduce every captured file exactly (type-aware; only wall-clock stamps ignored).
    /// (2) Apply -> Build: loading each captured file into a fresh session through the Apply assemblers and building it
    ///     back must reproduce the file, except for a fixed, documented set of paths that Godot's own
    ///     <c>_apply_run_snapshot</c> changes on load (see <see cref="LoadDerivedPaths"/>).
    /// </summary>
    public class SessionSaveParityTests
    {
        const string Root = "godot/save/capture_main_playable/";
        const string CapturedRunId = "1647935-c8a8";

        /// <summary>The only keys ignored: wall-clock stamps (top level, embedded home slice, meta_progression_summary).</summary>
        public static readonly string[] VolatileKeys = { "saved_at", "saved_at_epoch" };

        /// <summary>
        /// Paths (indices normalised to []) that differ after Apply -> Build, each for a reason Godot shares:
        /// <list type="bullet">
        /// <item>int -> float: Godot's JSON.parse returns every number as a float and these nested arrays/dicts are stored
        ///   by apply_summary without coercion, so the next save writes them as floats.</item>
        /// <item>power_grid / propulsion / sustenance summaries: <c>_apply_run_snapshot</c> calls
        ///   <c>_recompute_expanded_ship_systems(0.0)</c> (gd 10582) after the manager apply, so these are re-derived from
        ///   the restored (lifeboat-damaged) manager. The captured run never hit a SLOW-band recompute before saving.</item>
        /// <item>crafting_summary.station_summaries.*: the restore build path re-runs <c>_build_crafting_stations</c>,
        ///   which registers the six stations.</item>
        /// <item>sfx_router.caption_queue_size: SfxEventRouter.apply_summary does not restore the caption queue.</item>
        /// </list>
        /// </summary>
        public static readonly string[] LoadDerivedPaths =
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

        // ------------------------------------------------------------------ (1) played run

        [Test]
        public void PlayedRun_WritesTheGodotCapture()
        {
            GdDict currentRun = Fixtures.ReadDict(Root + "mid_run/saves/current_run.json");
            SessionHarness.Rig rig = BootWithCapturedEngine(currentRun);
            RunSession s = rig.Session;
            s.SaveLoadService.SetActiveRunId(CapturedRunId);

            Assert.IsTrue(s.CompleteObjectiveSequence(1));
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
                List<string> diffs = TreeDiff.Compare(Fixtures.ReadDict(Root + "mid_run/saves/" + fixture), ReadWritten(rig.Storage, path), Strict());
                Assert.IsEmpty(diffs, fixture + ":\n" + TreeDiff.Format(diffs));
            }
        }

        // ------------------------------------------------------------------ (2) Apply -> Build

        [TestCaseSource(nameof(RunSaves))]
        public void RunSave_ApplyThenBuild_ReproducesTheGodotFile(string rel, string slotId, string slotKind, bool quick, string display)
        {
            GdDict original = Fixtures.ReadDict(rel);
            SessionHarness.Rig rig = BootWithCapturedEngine(original);
            RunSession s = rig.Session;

            // Load through the real service read path (Godot parse: every number a double, then from_dict coercion).
            rig.Storage.WriteText(SlotFile(slotId), Fixtures.ReadText(rel).Replace("\r\n", "\n"));
            RunSnapshot loaded = s.SaveLoadService.LoadFromSlot(slotId);
            Assert.IsNotNull(loaded, "SaveLoadService refused the Godot save");
            Assert.IsTrue(RunSnapshotAssembler.Apply(s, loaded), "RunSnapshotAssembler.Apply");
            Assert.AreEqual(2, s.CurrentObjectiveSequence);

            s.SaveLoadService.SetActiveRunId(original.GetString("run_id"));
            Assert.IsTrue(s.SaveLoadService.SaveToSlot(slotId, RunSnapshotAssembler.Build(s), slotKind, quick, display));
            List<string> diffs = TreeDiff.Compare(original, ReadWritten(rig.Storage, SlotFile(slotId)), Strict());

            string[] paths = NormalisedPaths(diffs, "");
            TestContext.WriteLine($"{rel}: {diffs.Count} load-derived differences over {paths.Length} paths");
            CollectionAssert.AreEqual(LoadDerivedPaths, paths, TreeDiff.Format(diffs));
            AssertOnlyIntFloatWhereExpected(diffs);
        }

        [Test]
        public void WorldSave_ApplyThenBuild_ReproducesTheGodotFile()
        {
            const string rel = Root + "mid_run/saves/world.json";
            GdDict original = Fixtures.ReadDict(rel);
            SessionHarness.Rig rig = BootWithCapturedEngine(original);
            RunSession s = rig.Session;

            rig.Storage.WriteText(SaveLoadService.WORLD_SLOT_FILE, Fixtures.ReadText(rel).Replace("\r\n", "\n"));
            WorldSnapshot loaded = s.SaveLoadService.LoadWorld();
            Assert.IsNotNull(loaded, "SaveLoadService refused the Godot world save");
            Assert.IsTrue(WorldSnapshotAssembler.Apply(s, loaded), "WorldSnapshotAssembler.Apply");

            s.SaveLoadService.SetActiveRunId(original.GetString("run_id"));
            Assert.IsTrue(s.SaveLoadService.SaveWorld(WorldSnapshotAssembler.Build(s)));
            List<string> diffs = TreeDiff.Compare(original, ReadWritten(rig.Storage, SaveLoadService.WORLD_SLOT_FILE), Strict());

            string[] paths = NormalisedPaths(diffs, "$.home_ship.");
            TestContext.WriteLine($"{rel}: {diffs.Count} load-derived differences over {paths.Length} paths");
            Assert.IsTrue(diffs.All(d => d.StartsWith("$.home_ship.")), "only the embedded home slice may differ:\n" + TreeDiff.Format(diffs));
            CollectionAssert.AreEqual(LoadDerivedPaths, paths, TreeDiff.Format(diffs));
            AssertOnlyIntFloatWhereExpected(diffs);
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
