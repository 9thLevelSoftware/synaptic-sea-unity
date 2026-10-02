using System;
using System.IO;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Session;

namespace SynapticSea.Tests.Systems
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class SaveCommitRecoveryTests
    {
        const string Godot = "4.7.test";
        const string RunId = "recovery-run-17";
        const string Layout = "user://runs/recovery-run-17/layout.json";
        const string Slice = "user://runs/recovery-run-17/gameplay_slice.json";
        string _root;
        IEngineInfo _engine;
        IResourceReader _resources;
        FileSystemStorage _storage;
        SaveLoadService _service;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "synaptic-save-recovery-" + Guid.NewGuid().ToString("N"));
            _storage = new FileSystemStorage(_root);
            _engine = CoreServices.Engine;
            _resources = CoreServices.Resources;
            CoreServices.Engine = new FixedEngineInfo(Godot);
            CoreServices.Resources = new FileSystemResourceReader(_root, _storage);
            _service = Restart();
            _service.SetActiveRunId(RunId);
            _storage.WriteText(Layout, GdJson.Stringify(new GdDict
            {
                { "document_kind", "ship_layout" }, { "schema_version", "1.2.0" },
                { "cell_size", 4.0 }, { "rooms", GdArray.Of(new GdDict { { "id", "home" } }) },
                { "portals", new GdArray() }
            }));
            _storage.WriteText(Slice, GdJson.Stringify(new GdDict
            {
                { "document_kind", "ship_gameplay_slice" }, { "schema_version", "1.1.0" },
                { "start_room", "home" }, { "goal_room", "home" }, { "rooms", new GdArray() }
            }));
        }

        [TearDown]
        public void TearDown()
        {
            CoreServices.Engine = _engine;
            CoreServices.Resources = _resources;
            string full = Path.GetFullPath(_root);
            Assert.IsTrue(full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFileName(full).StartsWith("synaptic-save-recovery-", StringComparison.Ordinal));
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }

        SaveLoadService Restart() => new SaveLoadService(new FileSystemStorage(_root), new ManualClock());

        static RunSnapshot Snapshot() => new RunSnapshot
        {
            LayoutPath = Layout, GameplaySlicePath = Slice, WorldSeed = 17,
            PlayerPosition = GdArray.Of(1.0, 2.0, 3.0),
            VitalsSummary = new VitalsState().GetSummary(),
            CurrentLocation = "home", PlayTimeSeconds = 42.0
        };

        void Stage(string path) => File.Move(_storage.Globalize(path), _storage.Globalize(path) + ".tmp");

        GdDict StageRun(string slot = SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID, string kind = SaveSlotState.SlotKindAuto)
        {
            Assert.IsTrue(_service.SaveToSlot(slot, Snapshot(), kind, kind == SaveSlotState.SlotKindQuick, slot));
            string path = slot == SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID ? SaveLoadService.SAVE_PATH : "user://saves/" + slot + ".json";
            var dict = (GdDict)GdJson.ParseString(_storage.ReadText(path));
            Stage(path);
            return dict;
        }

        void ReplaceStagedRun(GdDict dict)
        {
            File.WriteAllText(_storage.Globalize(SaveLoadService.SAVE_PATH) + ".tmp", GdJson.Stringify(dict));
            _storage.Delete(SaveLoadService.CLOUD_DIR + "/" + SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID + ".manifest.json");
        }

        WorldSnapshot World()
        {
            var home = Snapshot();
            // Real WorldSnapshotAssembler home slices have blank run/slot metadata; the outer world owns identity.
            home.SliceVersion = SaveLoadService.CURRENT_SLICE_VERSION; home.GodotVersion = Godot;
            return new WorldSnapshot
            {
                HomeShip = home.ToDict(), WorldSummary = new SynapticSeaWorld(17, Vec3.Zero).GetSummary(),
                SliceVersion = WorldSnapshot.WorldSliceVersion, GodotVersion = Godot
            };
        }

        [TestCase(SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID, SaveSlotState.SlotKindAuto)]
        [TestCase("slot_02", SaveSlotState.SlotKindManual)]
        [TestCase("autosave_b", SaveSlotState.SlotKindAuto)]
        [TestCase("quicksave", SaveSlotState.SlotKindQuick)]
        public void MissingDestination_ValidTemp_RestartsThroughRealLoader(string slot, string kind)
        {
            GdDict original = StageRun(slot, kind);
            SaveLoadService restarted = Restart();
            RunSnapshot loaded = restarted.LoadFromSlot(slot);
            Assert.IsNotNull(loaded);
            Assert.IsTrue(V.VariantEquals(original, loaded.ToDict()));
            string path = slot == SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID ? SaveLoadService.SAVE_PATH : "user://saves/" + slot + ".json";
            Assert.IsTrue(_storage.FileExists(path));
            Assert.IsFalse(File.Exists(_storage.Globalize(path) + ".tmp"));
        }

        [Test]
        public void MissingWorld_ValidTemp_IsAvailableThroughTitleQuery()
        {
            Assert.IsTrue(_service.SaveWorld(World()));
            string original = _storage.ReadText(SaveLoadService.WORLD_SLOT_FILE);
            Stage(SaveLoadService.WORLD_SLOT_FILE);
            var restarted = Restart();
            Assert.IsTrue(TitleSaveQuery.IsContinueAvailable(restarted, new PermadeathResolver(_storage)));
            Assert.IsNotNull(restarted.LoadWorld());
            Assert.AreEqual(original, _storage.ReadText(SaveLoadService.WORLD_SLOT_FILE));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ActualSessionAssemblerSnapshot_RecoversThroughRealFilesystemAndContinue(bool world)
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot, _storage);
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            CatalogRegistry.Clear();
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            deps.Storage = _storage;
            SessionHarness.OverlayGamePlayability(deps);
            RunSession session = RunSession.Create(deps);
            try
            {
                Assert.IsTrue(session.PlayableStarted, session.LastFailureReason);
                SaveLoadService saves = session.SaveLoadService;
                if (world)
                {
                    WorldSnapshot snapshot = WorldSnapshotAssembler.Build(session);
                    Assert.AreEqual("", snapshot.HomeShip.GetString("run_id"));
                    Assert.AreEqual("", snapshot.CurrentLocation);
                    Assert.IsTrue(saves.SaveWorld(snapshot));
                    Stage(SaveLoadService.WORLD_SLOT_FILE);
                    Assert.IsTrue(TitleSaveQuery.IsContinueAvailable(Restart(), new PermadeathResolver(_storage)));
                    Assert.IsTrue(session.RequestLoad(), "Recovered world must pass the real snapshot application path");
                    Assert.AreEqual(snapshot.RunId, session.RunId);
                }
                else
                {
                    RunSnapshot snapshot = RunSnapshotAssembler.Build(session);
                    Assert.IsTrue(saves.SaveCurrentRun(snapshot));
                    // Runtime variants inside model summaries normalize through JSON; compare loader behavior
                    // against a canonical-load control rather than the unencoded in-memory assembler dictionary.
                    RunSnapshot canonical = saves.LoadCurrentRun();
                    Assert.IsNotNull(canonical);
                    Stage(SaveLoadService.SAVE_PATH);
                    RunSnapshot loaded = Restart().LoadCurrentRun();
                    Assert.IsNotNull(loaded);
                    Assert.IsTrue(V.VariantEquals(canonical.ToDict(), loaded.ToDict()));
                    Assert.IsTrue(session.ApplyManualSlot(loaded), "Recovered run must pass the real application path");
                }
            }
            finally
            {
                session.Dispose();
                CatalogRegistry.Clear();
            }
        }

        [TestCase("wrong_propulsion_owner")]
        [TestCase("malformed_dock_edge")]
        public void ActualWorldTemp_RejectedByExistingApplicationGuards_IsNeverPromoted(string fault)
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot, _storage);
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            CatalogRegistry.Clear();
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            deps.Storage = _storage;
            SessionHarness.OverlayGamePlayability(deps);
            RunSession session = RunSession.Create(deps);
            try
            {
                Assert.IsTrue(session.PlayableStarted, session.LastFailureReason);
                WorldSnapshot snapshot = WorldSnapshotAssembler.Build(session);
                if (fault == "wrong_propulsion_owner")
                    snapshot.MobileHomeState.GetDictOrEmpty("home_mobility")["engine_id"] = "propulsion:another-ship";
                else snapshot.DockEdges = GdArray.Of("bad");
                Assert.IsFalse(WorldSnapshotAssembler.Apply(session, snapshot), "Existing application guards must reject this payload");
                Assert.IsTrue(session.SaveLoadService.SaveWorld(snapshot));
                string original = _storage.ReadText(SaveLoadService.WORLD_SLOT_FILE);
                Stage(SaveLoadService.WORLD_SLOT_FILE);
                Assert.IsNull(Restart().LoadWorld(), "Rejected application state must not be promoted or offered for Continue");
                Assert.IsFalse(TitleSaveQuery.IsContinueAvailable(Restart(), new PermadeathResolver(_storage)));
                Assert.IsFalse(_storage.FileExists(SaveLoadService.WORLD_SLOT_FILE));
                Assert.AreEqual(original, File.ReadAllText(_storage.Globalize(SaveLoadService.WORLD_SLOT_FILE) + ".tmp"));
            }
            finally
            {
                session.Dispose();
                CatalogRegistry.Clear();
            }
        }

        [Test]
        public void CanonicalSave_WinsOverStagedTemp_WithoutReadingOrChangingTemp()
        {
            Assert.IsTrue(_service.SaveCurrentRun(Snapshot()));
            string original = _storage.ReadText(SaveLoadService.SAVE_PATH);
            File.WriteAllText(_storage.Globalize(SaveLoadService.SAVE_PATH) + ".tmp", "unvalidated staging");
            Assert.IsNotNull(Restart().LoadCurrentRun());
            Assert.AreEqual(original, _storage.ReadText(SaveLoadService.SAVE_PATH));
            Assert.AreEqual("unvalidated staging", File.ReadAllText(_storage.Globalize(SaveLoadService.SAVE_PATH) + ".tmp"));
        }

        [TestCase("malformed")]
        [TestCase("future")]
        [TestCase("older")]
        [TestCase("engine")]
        [TestCase("wrong_slot")]
        [TestCase("wrong_kind")]
        [TestCase("missing_run")]
        [TestCase("wrong_run")]
        [TestCase("missing_layout")]
        [TestCase("invalid_layout")]
        [TestCase("layout_traversal")]
        [TestCase("missing_slice")]
        [TestCase("malformed_position")]
        [TestCase("malformed_summary")]
        [TestCase("terminal_health")]
        [TestCase("death")]
        [TestCase("frozen_index")]
        [TestCase("manifest_mismatch")]
        [TestCase("world_run_mismatch")]
        [TestCase("world_seed_mismatch")]
        public void InvalidOrInconsistentTemp_IsRetainedAndNeverPromoted(string fault)
        {
            GdDict dict = StageRun();
            switch (fault)
            {
                case "future": dict["slice_version"] = "gate2-current-run-99"; break;
                case "older": dict["slice_version"] = "gate2-current-run-5"; break;
                case "engine": dict["godot_version"] = "future-engine"; break;
                case "wrong_slot": dict["slot_id"] = "slot_06"; break;
                case "wrong_kind": dict["slot_kind"] = "manual"; break;
                case "missing_run": dict["run_id"] = ""; break;
                case "wrong_run": dict["run_id"] = "another-run"; break;
                case "missing_layout": _storage.Delete(Layout); break;
                case "invalid_layout": _storage.WriteText(Layout, "{}"); break;
                case "layout_traversal": dict["layout_path"] = "user://runs/recovery-run-17/../recovery-run-17/layout.json"; break;
                case "missing_slice": _storage.Delete(Slice); break;
                case "malformed_position": dict["player_position"] = GdArray.Of("bad", 0.0, 0.0); break;
                case "malformed_summary": dict["inventory_summary"] = "bad"; break;
                case "terminal_health": ((GdDict)dict["vitals_summary"])["health"] = 0.0; break;
                case "death": new PermadeathResolver(_storage).RecordDeath(SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID, "death", "", 1, 1); break;
                case "frozen_index":
                    var index = SaveIndexState.FromDict(GdJson.ParseString(_storage.ReadText(SaveLoadService.INDEX_PATH)));
                    index.Find(SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID).Frozen = true;
                    _storage.WriteText(SaveLoadService.INDEX_PATH, GdJson.Stringify(index.ToDict())); break;
                case "world_run_mismatch": _service.SetActiveRunId("another-run"); Assert.IsTrue(_service.SaveWorld(World())); break;
                case "world_seed_mismatch": var world = World(); world.WorldSummary = new SynapticSeaWorld(99, Vec3.Zero).GetSummary(); Assert.IsTrue(_service.SaveWorld(world)); break;
            }
            if (fault != "manifest_mismatch") ReplaceStagedRun(dict);
            else
            {
                dict["play_time_seconds"] = 99.0;
                File.WriteAllText(_storage.Globalize(SaveLoadService.SAVE_PATH) + ".tmp", GdJson.Stringify(dict));
            }
            if (fault == "malformed") File.WriteAllText(_storage.Globalize(SaveLoadService.SAVE_PATH) + ".tmp", "{unfinished");
            string staged = File.ReadAllText(_storage.Globalize(SaveLoadService.SAVE_PATH) + ".tmp");
            Assert.IsNull(Restart().LoadCurrentRun());
            Assert.IsFalse(_storage.FileExists(SaveLoadService.SAVE_PATH));
            Assert.AreEqual(staged, File.ReadAllText(_storage.Globalize(SaveLoadService.SAVE_PATH) + ".tmp"));
        }

        [Test]
        public void ExplicitDeletion_RemovesStagedRunAndWorld_WithoutResurrection()
        {
            StageRun(); Assert.IsTrue(_service.SaveWorld(World())); Stage(SaveLoadService.WORLD_SLOT_FILE);
            Assert.IsTrue(_service.DeleteCurrentRun());
            Assert.IsNull(Restart().LoadCurrentRun()); Assert.IsNull(Restart().LoadWorld());
            Assert.IsFalse(_storage.FileExists(SaveLoadService.SAVE_PATH + ".tmp"));
            Assert.IsFalse(_storage.FileExists(SaveLoadService.WORLD_SLOT_FILE + ".tmp"));
        }

        [Test]
        public void TitleJanitor_RetainsLayoutReferencedOnlyByTemporaryPayload()
        {
            StageRun();
            Assert.IsEmpty(new RunDirectoryJanitor(_storage, Restart()).Sweep());
            Assert.IsTrue(_storage.FileExists(Layout));
            Assert.IsNotNull(Restart().LoadCurrentRun());
        }

        [Test]
        public void DeleteNamedSlot_RemovesItsTemporaryPayload()
        {
            StageRun("slot_02", SaveSlotState.SlotKindManual);
            Assert.IsTrue(Restart().DeleteSlot("slot_02"));
            Assert.IsFalse(_storage.FileExists("user://saves/slot_02.json.tmp"));
            Assert.IsFalse(Restart().HasSlot("slot_02"));
        }

        [TestCase("slot_02", SaveSlotState.SlotKindManual)]
        [TestCase("autosave_b", SaveSlotState.SlotKindAuto)]
        [TestCase("quicksave", SaveSlotState.SlotKindQuick)]
        public void SlotMenuListsBeforeLoad_ValidTemporaryPayloadStillRecovers(string slot, string kind)
        {
            StageRun(slot, kind);
            SaveLoadService menu = Restart();
            SaveSlotState row = menu.ListSlots().Find(r => r.SlotId == slot);
            Assert.IsNotNull(row);
            Assert.IsFalse(row.Corrupt, "Opening the slot menu must not poison a valid recoverable payload");
            Assert.IsNotNull(Restart().LoadFromSlot(slot));
        }

        [TestCase("future")]
        [TestCase("wrong_home_run")]
        [TestCase("wrong_home_schema")]
        [TestCase("missing_world_summary")]
        [TestCase("missing_home")]
        [TestCase("missing_location")]
        [TestCase("terminal_health")]
        [TestCase("future_mobile_home")]
        [TestCase("death")]
        public void InvalidWorldTemp_IsRetainedAndNeverPromoted(string fault)
        {
            Assert.IsTrue(_service.SaveWorld(World()));
            GdDict dict = (GdDict)GdJson.ParseString(_storage.ReadText(SaveLoadService.WORLD_SLOT_FILE));
            Stage(SaveLoadService.WORLD_SLOT_FILE);
            _storage.Delete(SaveLoadService.CLOUD_DIR + "/world.manifest.json");
            switch (fault)
            {
                case "future": dict["slice_version"] = "world-99"; break;
                case "wrong_home_run": ((GdDict)dict["home_ship"])["run_id"] = "another-run"; break;
                case "wrong_home_schema": ((GdDict)dict["home_ship"])["slice_version"] = "gate2-current-run-99"; break;
                case "missing_world_summary": dict["world_summary"] = new GdDict(); break;
                case "missing_home": dict["home_ship"] = new GdDict(); break;
                case "missing_location": dict["current_location"] = "unknown-marker"; break;
                case "terminal_health": ((GdDict)((GdDict)dict["home_ship"])["vitals_summary"])["health"] = 0.0; break;
                case "future_mobile_home": dict["mobile_home_state"] = new GdDict { { "version", 99L } }; break;
                case "death": new PermadeathResolver(_storage).RecordDeath("world", "death", "", 1, 1); break;
            }
            string staged = GdJson.Stringify(dict);
            File.WriteAllText(_storage.Globalize(SaveLoadService.WORLD_SLOT_FILE) + ".tmp", staged);
            Assert.IsNull(Restart().LoadWorld());
            Assert.IsFalse(_storage.FileExists(SaveLoadService.WORLD_SLOT_FILE));
            Assert.AreEqual(staged, File.ReadAllText(_storage.Globalize(SaveLoadService.WORLD_SLOT_FILE) + ".tmp"));
        }

        [Test]
        public void TwoStagedPayloadsWithoutCanonicalPeer_RemainAmbiguous()
        {
            StageRun(); Assert.IsTrue(_service.SaveWorld(World())); Stage(SaveLoadService.WORLD_SLOT_FILE);
            Assert.IsNull(Restart().LoadCurrentRun()); Assert.IsNull(Restart().LoadWorld());
            Assert.IsTrue(_storage.FileExists(SaveLoadService.SAVE_PATH + ".tmp"));
            Assert.IsTrue(_storage.FileExists(SaveLoadService.WORLD_SLOT_FILE + ".tmp"));
        }

        [Test]
        public void FreezeRun_FindsStagedOwnedPayload_WhenIndexIsMissing()
        {
            StageRun(); _storage.Delete(SaveLoadService.INDEX_PATH);
            Restart().FreezeRun(RunId, "death", "", 1, 1);
            Assert.IsTrue(new PermadeathResolver(_storage).HasDiedIn(SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID));
            Assert.IsNull(Restart().LoadCurrentRun());
        }

        [Test]
        public void LockedTempBeforeWrite_KeepsOldDestination()
        {
            const string path = "user://probe.json";
            _storage.WriteText(path, "old-good");
            File.WriteAllText(_storage.Globalize(path) + ".tmp", "locked");
            using (new FileStream(_storage.Globalize(path) + ".tmp", FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Catch<IOException>(() => _storage.WriteText(path, "new-good"));
            Assert.AreEqual("old-good", _storage.ReadText(path));
        }

        [Test]
        public void LockedDestinationDuringReplacement_KeepsOldDestinationAndReportsSaveFailure()
        {
            Assert.IsTrue(_service.SaveCurrentRun(Snapshot()));
            string original = _storage.ReadText(SaveLoadService.SAVE_PATH);
            using (new FileStream(_storage.Globalize(SaveLoadService.SAVE_PATH), FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.IsFalse(_service.SaveCurrentRun(Snapshot()));
            Assert.AreEqual(original, _storage.ReadText(SaveLoadService.SAVE_PATH));
            Assert.IsNotNull(Restart().LoadCurrentRun());
        }

        [Test]
        public void LockedSourceDuringPublication_KeepsOnlyGoodDestination()
        {
            const string path = "user://probe.json";
            _storage.WriteText(path, "old-good");
            File.WriteAllText(_storage.Globalize(path) + ".tmp", "staged");
            using (new FileStream(_storage.Globalize(path) + ".tmp", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Assert.Catch<IOException>(() => _storage.WriteText(path, "new-good"));
            Assert.AreEqual("old-good", _storage.ReadText(path));
        }

        [Test]
        public void RenameLockedSource_KeepsDestinationAndSource()
        {
            _storage.WriteText("source.json", "new-good"); _storage.WriteText("destination.json", "old-good");
            using (new FileStream(_storage.Globalize("source.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Assert.Catch<IOException>(() => _storage.Rename("source.json", "destination.json"));
            Assert.AreEqual("old-good", _storage.ReadText("destination.json"));
            Assert.AreEqual("new-good", _storage.ReadText("source.json"));
        }

        [Test]
        public void SuccessfulReplacement_PreservesUtf8AndConsumesStage()
        {
            _storage.WriteText("probe.json", "old"); _storage.WriteText("probe.json", "new 海 🚀");
            Assert.AreEqual("new 海 🚀", new FileSystemStorage(_root).ReadText("probe.json"));
            Assert.IsFalse(File.Exists(_storage.Globalize("probe.json") + ".tmp"));
            Assert.AreNotEqual(0xEF, File.ReadAllBytes(_storage.Globalize("probe.json"))[0]);
        }
    }
}
