using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class RunSnapshotTests
    {
        const string Godot = "4.7.test";

        static RunSnapshot Make()
        {
            var snap = new RunSnapshot();
            snap.LayoutPath = "layout_a";
            snap.KitPath = "res://data/kits/ship_structural_v0.json";
            snap.PlayerPosition = GdArray.Of(1.0, 0.0, 2.0);
            snap.CurrentObjectiveSequence = 3;
            snap.ShipSystemsSummary = new GdDict { { "systems", new GdDict { { "power", new GdDict { { "health", 1.0 } } } } } };
            snap.PlayerProgressionSummary = new GdDict { { "class_id", "engineer" }, { "level", 1L } };
            snap.PlayTimeSeconds = 12.5;
            snap.WorldSeed = 99;
            snap.SlotId = "slot_02";
            snap.SlotKind = SaveSlotState.SlotKindManual;
            snap.SliceVersion = SaveMigrationService.TargetVersion;
            snap.GodotVersion = Godot;
            snap.SavedAtEpoch = 1788000000;
            return snap;
        }

        [Test]
        public void ToDict_FromDict_RoundTrips_ThroughJson()
        {
            RunSnapshot snap = Make();
            GdDict dict = snap.ToDict();
            Assert.AreEqual(32, snap.GetSummaryCount());
            Assert.AreEqual("layout_path", dict.Keys[0]);
            Assert.AreEqual("saved_at_epoch", dict.Keys[dict.Count - 1]);
            object parsed = GdJson.ParseString(GdJson.Stringify(dict, "\t"));
            RunSnapshot back = RunSnapshot.FromDict(parsed, SaveMigrationService.TargetVersion, Godot);
            Assert.IsNotNull(back);
            Assert.IsTrue(V.VariantEquals(dict, back.ToDict()));
            Assert.IsInstanceOf<long>(back.ToDict()["world_seed"]);
        }

        [Test]
        public void VersionMismatch_IsRejected()
        {
            GdDict dict = Make().ToDict();
            Assert.IsNull(RunSnapshot.FromDict(dict, "gate2-current-run-99", Godot));
            Assert.IsNull(RunSnapshot.FromDict(dict, SaveMigrationService.TargetVersion, "0.0.0"));
            Assert.IsNull(RunSnapshot.FromDict(new GdDict(), SaveMigrationService.TargetVersion, Godot));
            Assert.IsNull(RunSnapshot.FromDict(null, SaveMigrationService.TargetVersion, Godot));
        }
    }

    public class WorldSnapshotTests
    {
        [Test]
        public void SmokeSnapshot_RoundTrips_AndVersionGates()
        {
            string godot = CoreServices.Engine.VersionString;
            var ws = new WorldSnapshot();
            ws.WorldSummary = new GdDict { { "world_seed", 99L }, { "player_position", GdArray.Of(1.0, 0.0, 2.0) }, { "generated_marker_ids", GdArray.Of("3:1:0") } };
            ws.HomeShip = new GdDict { { "slice_version", "gate2-current-run-1" }, { "player_position", GdArray.Of(5.0, 1.0, 5.0) } };
            ws.VisitedShips = new GdDict { { "3:1:0", new GdDict { { "ship_id", "ship_3:1:0" }, { "marker_id", "3:1:0" } } } };
            ws.CurrentLocation = "3:1:0";
            ws.PlayerPositionInShip = GdArray.Of(10.0, 2.0, 3.0);
            ws.HomeShipCarts = GdArray.Of(new GdDict { { "cart_id", "cart_home" } });
            ws.OpenedPorts = GdArray.Of("3:1:0");
            ws.WorldTime = 42.0;
            ws.SliceVersion = WorldSnapshot.WorldSliceVersion;
            ws.GodotVersion = godot;
            ws.SavedAt = "2026-06-21T00:00:00";
            GdDict dict = ws.ToDict();
            var rebuilt = WorldSnapshot.FromDict(dict, WorldSnapshot.WorldSliceVersion, godot);
            Assert.IsNotNull(rebuilt);
            Assert.IsTrue(V.VariantEquals(dict, rebuilt.ToDict()));
            Assert.AreEqual(99, rebuilt.WorldSummary.GetInt("world_seed", -1));
            Assert.IsNull(WorldSnapshot.FromDict(dict, "world-999", godot));
            Assert.IsNull(WorldSnapshot.FromDict(dict, WorldSnapshot.WorldSliceVersion, "0.0.0"));
            Assert.IsNull(WorldSnapshot.FromDict(new GdDict(), WorldSnapshot.WorldSliceVersion, godot));
        }
    }

    public class SaveMigrationServiceTests
    {
        static GdDict V1() => new GdDict
        {
            { "layout_path", "res://data/procgen/smoke/seed_000017/layout.json" },
            { "player_position", GdArray.Of(1.0, 0.0, 2.0) },
            { "current_objective_sequence", 4L },
            { "oxygen_summary", new GdDict { { "oxygen", 75.0 } } },
            { "slice_version", "gate2-current-run-1" },
            { "godot_version", "4.7.test" },
            { "saved_at", "2026-06-20T00:00:00" },
        };

        [Test]
        public void V1_WalksChainToV4_WithExactDefaults()
        {
            var migrator = new SaveMigrationService();
            GdDict source = V1();
            GdDict result = migrator.MigrateRun(source);
            Assert.IsTrue(V.Bool(result["migrated"]));
            Assert.AreEqual("gate2-current-run-1", result["from_version"]);
            var d = (GdDict)result["dict"];
            Assert.AreEqual(SaveMigrationService.TargetVersion, d["slice_version"]);
            Assert.IsTrue(V.VariantEquals(new GdDict { { "class_id", "" }, { "xp", new GdDict() }, { "level", 1L } }, d["player_progression_summary"]));
            Assert.AreEqual("", d["slot_id"]);
            Assert.AreEqual(false, d["is_autosave"]);
            Assert.AreEqual(0.0, d["play_time_seconds"]);
            Assert.AreEqual(0L, d["world_seed"]);
            Assert.AreEqual("gate2-current-run-1", source["slice_version"], "source dict must not be mutated");
            Assert.IsNotNull(RunSnapshot.FromDict(d, SaveMigrationService.TargetVersion, "4.7.test"));
        }

        [Test]
        public void NewerRun_IsRejected_CurrentPassesThrough()
        {
            var migrator = new SaveMigrationService();
            GdDict newer = V1();
            newer["slice_version"] = "gate2-current-run-99";
            Assert.IsNull(migrator.MigrateRun(newer)["dict"]);
            GdDict current = V1();
            current["slice_version"] = SaveMigrationService.TargetVersion;
            GdDict same = migrator.MigrateRun(current);
            Assert.AreSame(current, same["dict"]);
            Assert.IsFalse(V.Bool(same["migrated"]));
        }

        [Test]
        public void World_MigratesEmbeddedHomeShip_AndFlagsNewer()
        {
            var migrator = new SaveMigrationService();
            var legacy = new GdDict { { "slice_version", "world-2" }, { "home_ship", V1() } };
            GdDict result = migrator.MigrateWorld(legacy);
            Assert.IsTrue(V.Bool(result["migrated"]));
            var d = (GdDict)result["dict"];
            Assert.AreEqual("world-4", d["slice_version"]);
            Assert.AreEqual(SaveMigrationService.TargetVersion, ((GdDict)d["home_ship"])["slice_version"]);

            var current = new GdDict { { "slice_version", "world-4" }, { "home_ship", V1() } };
            Assert.IsTrue(V.Bool(migrator.MigrateWorld(current)["migrated"]));

            GdDict newer = migrator.MigrateWorld(new GdDict { { "slice_version", "world-9" } });
            Assert.IsFalse(V.Bool(newer["migrated"]));
            Assert.IsTrue(V.Bool(newer["newer_than_current"]));
            Assert.IsNull(migrator.MigrateWorld("not a dict")["dict"]);
        }
    }

    public class SaveSlotStateTests
    {
        [Test]
        public void Row_RoundTrips_AndValidates()
        {
            var row = new SaveSlotState
            {
                SlotId = "slot_01", SlotKind = SaveSlotState.SlotKindManual, DisplayName = "Slot 1", SynapticSeaSeed = 17,
                ObjectiveSequence = 2, PlayTimeSeconds = 30.5, SavedAtEpoch = 1788000000, EmbeddedWorldSlotId = "world", RunId = "run-1",
            };
            GdDict dict = row.ToDict();
            SaveSlotState back = SaveSlotState.FromDict(GdJson.ParseString(GdJson.Stringify(dict)));
            Assert.IsTrue(V.VariantEquals(dict, back.ToDict()));
            Assert.IsTrue(SaveSlotState.Validate(back));
            Assert.IsTrue(back.IsManual());
            Assert.IsFalse(SaveSlotState.Validate(new SaveSlotState { SlotId = "x", SlotKind = "bogus" }));
            Assert.IsNull(SaveSlotState.FromDict("nope"));
        }
    }

    public class PermadeathResolverTests
    {
        [Test]
        public void RecordLoadClear_ThroughStorage()
        {
            var storage = new MemoryStorage();
            var clock = new ManualClock();
            var resolver = new PermadeathResolver(storage, clock);
            Assert.IsFalse(resolver.HasDiedIn("world"));
            GdDict record = resolver.RecordDeath("world", "death", "test epitaph", 30.0, 2);
            Assert.AreEqual((long)clock.Unix, record["died_at_epoch"]);
            Assert.IsTrue(storage.FileExists("user://saves/world.death.json"));
            Assert.IsTrue(resolver.HasDiedIn("world"));
            GdDict loaded = resolver.LoadEpitaph("world");
            Assert.AreEqual("test epitaph", loaded["epitaph"]);
            Assert.AreEqual(2.0, loaded["final_objective_sequence"]);
            Assert.IsTrue(resolver.ClearDeath("world"));
            Assert.IsFalse(resolver.HasDiedIn("world"));
            Assert.IsTrue(resolver.LoadEpitaph("world").IsEmpty);
        }
    }

    public class CloudManifestStateTests
    {
        [Test]
        public void BuildForSlot_HashesSlotText_AndRoundTrips()
        {
            var storage = new MemoryStorage();
            storage.WriteText("user://saves/slot_02.json", "{\"a\":1}");
            var m = CloudManifestState.BuildForSlot("slot_02", "user://saves/slot_02.json", "gate2-current-run-4", storage, new ManualClock());
            // sha256("{\"a\":1}")
            Assert.AreEqual("015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862", m.PayloadSha256);
            Assert.AreEqual(7L, m.PayloadSizeBytes);
            Assert.AreEqual(16, m.DeviceId.Length);
            Assert.AreEqual("", m.BuildId, "Godot 4 application/config/version defaults to \"\"");
            Assert.AreEqual(m.PayloadSha256, CloudManifestState.RecomputeSha256("user://saves/slot_02.json", storage));
            Assert.AreEqual("", CloudManifestState.RecomputeSha256("user://saves/missing.json", storage));
            var back = CloudManifestState.FromDict(m.ToDict());
            Assert.IsTrue(V.VariantEquals(m.ToDict(), back.ToDict()));
        }
    }

    public class CrashReportBundleTests
    {
        [Test]
        public void CapIsFifo_AndFlushLoadRoundTrips()
        {
            var storage = new MemoryStorage();
            var bundle = new CrashReportBundle(storage, new ManualClock());
            for (int i = 0; i < 300; i++) bundle.Capture("msg " + i, new GdDict { { "i", (long)i } }, GdArray.Of("frame"));
            Assert.AreEqual(256, bundle.Size());
            Assert.AreEqual("msg 44", ((GdDict)bundle.GetEntries()[0])["message"]);
            Assert.IsTrue(bundle.Flush("user://crash/bundle.json"));
            var loaded = new CrashReportBundle(storage);
            Assert.IsTrue(loaded.LoadFromDisk("user://crash/bundle.json"));
            Assert.AreEqual(256, loaded.Size());
            Assert.IsTrue(V.VariantEquals(bundle.GetSummary(), loaded.GetSummary()));
            Assert.IsFalse(bundle.Flush(""));
        }
    }

    public class TitleSaveQueryTests
    {
        sealed class FakeService : ITitleSaveService
        {
            public bool Has;
            public WorldSnapshot World;
            public bool HasSlot(string slotId) => Has && slotId == "world";
            public WorldSnapshot LoadWorld() => World;
        }

        [Test]
        public void ContinueNeedsSave_NotFrozen_AndLoadable()
        {
            var resolver = new PermadeathResolver(new MemoryStorage(), new ManualClock());
            var service = new FakeService();
            Assert.IsFalse(TitleSaveQuery.IsContinueAvailable(service, resolver));
            service.Has = true;
            service.World = new WorldSnapshot();
            Assert.IsTrue(TitleSaveQuery.IsContinueAvailable(service, resolver));
            resolver.RecordDeath("world", "death", "test epitaph", 30.0, 2);
            Assert.IsFalse(TitleSaveQuery.IsContinueAvailable(service, resolver));
            resolver.ClearDeath("world");
            service.World = null; // corrupt / incompatible world.json
            Assert.IsFalse(TitleSaveQuery.IsContinueAvailable(service, resolver));
            Assert.IsFalse(TitleSaveQuery.IsContinueAvailable(null, resolver));
        }
    }
}
