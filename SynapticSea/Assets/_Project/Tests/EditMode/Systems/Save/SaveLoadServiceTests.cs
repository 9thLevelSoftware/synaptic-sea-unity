using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class SaveLoadServiceTests
    {
        const string Godot = "4.7.test";

        IEngineInfo _previousEngine;
        MemoryStorage _storage;
        ManualClock _clock;
        SaveLoadService _service;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(Godot);
            _storage = new MemoryStorage();
            _clock = new ManualClock();
            _service = new SaveLoadService(_storage, _clock);
        }

        [TearDown]
        public void TearDown() => CoreServices.Engine = _previousEngine;

        static RunSnapshot MakeSnapshot()
        {
            var snap = new RunSnapshot();
            snap.LayoutPath = "res://data/procgen/smoke/seed_000017/layout.json";
            snap.PlayerPosition = GdArray.Of(1.25, 2.5, 3.75);
            snap.CurrentObjectiveSequence = 2;
            snap.PlayerProgressionSummary = new GdDict { { "class_id", "engineer" }, { "level", 1L } };
            snap.TemperatureSummary = new GdDict { { "temperature", 22.5 }, { "is_safe", true } };
            snap.PlayTimeSeconds = 42.5;
            snap.CurrentLocation = "home";
            snap.WorldSeed = 17;
            return snap;
        }

        [Test]
        public void SaveToSlot_LoadFromSlot_RoundTrips_AndIndexes()
        {
            _service.SetActiveRunId("run-1");
            RunSnapshot snap = MakeSnapshot();
            Assert.IsTrue(_service.SaveToSlot("slot_02", snap, SaveSlotState.SlotKindManual, false, "Manual 2"));
            Assert.AreEqual(Godot, snap.GodotVersion);
            Assert.AreEqual(SaveLoadService.CURRENT_SLICE_VERSION, snap.SliceVersion);
            Assert.AreEqual(1_788_000_000L, snap.SavedAtEpoch);
            Assert.IsTrue(_storage.FileExists("user://saves/slot_02.json"));
            Assert.IsTrue(_storage.FileExists("user://saves/.cloud/slot_02.manifest.json"));
            Assert.IsTrue(_service.HasSlot("slot_02"));

            RunSnapshot loaded = _service.LoadFromSlot("slot_02");
            Assert.IsNotNull(loaded);
            Assert.IsTrue(V.VariantEquals(snap.ToDict(), loaded.ToDict()));
            Assert.AreEqual("run-1", loaded.RunId);

            var rows = _service.ListSlots();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("Manual 2", rows[0].DisplayName);
            Assert.AreEqual(17, rows[0].SynapticSeaSeed);
            Assert.AreEqual("engineer", rows[0].PlayerClass);
            Assert.IsTrue(V.VariantEquals(GdArray.Of("slot_02"), _service.SlotIdsForRun("run-1")));
            Assert.IsEmpty(_service.SlotIdsForRun(""));

            // Legacy alias: the active autosave lands at current_run.json.
            Assert.IsTrue(_service.SaveCurrentRun(MakeSnapshot()));
            Assert.IsTrue(_storage.FileExists(SaveLoadService.SAVE_PATH));
            Assert.IsTrue(_service.HasSave());
            Assert.IsNotNull(_service.LoadCurrentRun());
            Assert.IsTrue(_service.DeleteCurrentRun());
            Assert.IsFalse(_service.HasSave());
        }

        [Test]
        public void CorruptSlot_IsBackedUp_AndTamperedSlotFailsManifestGate()
        {
            Assert.IsTrue(_service.SaveToSlot("slot_01", MakeSnapshot(), SaveSlotState.SlotKindManual, false, "Manual 1"));
            string text = _storage.ReadText("user://saves/slot_01.json");
            _storage.WriteText("user://saves/slot_01.json", text.Replace("\"home\"", "\"away\""));
            _clock.Unix = 1_789_104_069.75;
            Assert.IsNull(_service.LoadFromSlot("slot_01"), "sha mismatch must refuse the load");
            Assert.IsTrue(_storage.FileExists("user://saves/.corrupt/slot_01.1789104069.slot_01.json.bak"));
            Assert.IsFalse(_storage.FileExists("user://saves/slot_01.json"));

            _storage.WriteText("user://saves/slot_03.json", "not json at all");
            Assert.IsNull(_service.LoadFromSlot("slot_03"));
            Assert.IsTrue(_storage.FileExists("user://saves/.corrupt/slot_03.1789104069.slot_03.json.bak"));
            Assert.IsTrue(_service.ListSlots().Find(r => r.SlotId == "slot_01").Corrupt);
        }

        [Test]
        public void World_SaveLoad_FreezeRun_AndContinueQuery()
        {
            _service.SetActiveRunId("run-7");
            var ws = new WorldSnapshot();
            ws.WorldSummary = new SynapticSeaWorld(99, new Vec3(1f, 0f, 2f)).GetSummary();
            ws.HomeShip = MakeSnapshot().ToDict();
            ((GdDict)ws.HomeShip)["slice_version"] = SaveLoadService.CURRENT_SLICE_VERSION;
            ((GdDict)ws.HomeShip)["godot_version"] = Godot;
            ws.CurrentLocation = "3:1:0";
            ws.SliceVersion = WorldSnapshot.WorldSliceVersion;
            ws.GodotVersion = Godot;
            Assert.IsTrue(_service.SaveWorld(ws));
            Assert.AreEqual("run-7", ws.RunId);
            var resolver = new PermadeathResolver(_storage, _clock);
            Assert.IsTrue(TitleSaveQuery.IsContinueAvailable(_service, resolver));

            WorldSnapshot loaded = _service.LoadWorld();
            Assert.IsNotNull(loaded);
            Assert.IsTrue(V.VariantEquals(ws.ToDict(), loaded.ToDict()));
            var world = new SynapticSeaWorld();
            Assert.IsTrue(world.ApplySummary(loaded.WorldSummary));
            Assert.AreEqual(99, world.WorldSeed);

            _service.FreezeRun("run-7", "oxygen_depleted", "Lost.", 12.0, 2);
            Assert.IsTrue(resolver.HasDiedIn("world"));
            Assert.IsNull(_service.LoadWorld());
            Assert.IsFalse(TitleSaveQuery.IsContinueAvailable(_service, resolver));

            // A newer engine version is rejected (and quarantined) by from_dict.
            resolver.ClearDeath("world");
            CoreServices.Engine = new FixedEngineInfo("9.9.9");
            Assert.IsNull(_service.LoadWorld());
            Assert.IsFalse(_storage.FileExists(SaveLoadService.WORLD_SLOT_FILE));
        }

        [Test]
        public void ResolveGameplaySlicePath_UsesSiblingWhenEmpty()
        {
            Assert.AreEqual("res://data/procgen/smoke/seed_000017/gameplay_slice.json",
                RunSnapshot.ResolveGameplaySlicePath("res://data/procgen/smoke/seed_000017/layout.json", ""));
            Assert.AreEqual("res://explicit/slice.json",
                RunSnapshot.ResolveGameplaySlicePath("res://data/procgen/smoke/seed_000017/layout.json", "res://explicit/slice.json"));
            Assert.AreEqual("", RunSnapshot.ResolveGameplaySlicePath("", ""));
        }
    }
}
