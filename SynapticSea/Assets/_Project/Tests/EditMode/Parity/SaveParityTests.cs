using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Parity
{
    /// <summary>
    /// Save-format parity against the real Godot 4.7.1 save files under <c>fixtures/godot/save/</c>
    /// (see its README.json):
    /// (a) every captured run/world save round-trips through <see cref="RunSnapshot"/> / <see cref="WorldSnapshot"/>
    ///     FromDict → ToDict type-aware (plus the index, cloud manifests, and the embedded model summaries of the
    ///     Wave 3 models);
    /// (b) every <c>legacy/migration_cases.json</c> pair migrates to the captured output;
    /// (c) <see cref="SaveLoadService"/> over a <see cref="MemoryStorage"/> seeded with the captured
    ///     <c>user://saves</c> trees loads every slot and rewrites it to the same JSON tree (and index rows);
    ///     the migrate-on-load and corruption-backup paths reproduce Godot's files;
    /// (d) <c>GdJson.Stringify(parsed, "\t")</c> reproduces every captured JSON file byte for byte.
    /// No volatile keys are ignored in (a), (b) or (d).
    /// The port writes <c>gate2-current-run-5</c> (Godot's run-4 plus <see cref="RunSnapshot.PortExtensionFields"/>): Godot's
    /// keys are compared exactly through <see cref="SavePortSchema.GodotView"/> and the added keys are asserted separately. (c) ignores only fields that depend on the rewritten byte
    /// count (<c>payload_size_bytes</c>, <c>payload_sha256</c>), the host (<c>device_id</c>) and wall-clock stamps
    /// the service writes itself (<c>updated_at</c>, manifest <c>created_at</c>), plus <c>build_id</c> (see
    /// <see cref="ServiceWrites_CloudManifests_MatchGodotShape"/>).
    /// </summary>
    public class SaveParityTests
    {
        const string SaveRoot = "godot/save";

        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUp() => _previousEngine = CoreServices.Engine;

        [TearDown]
        public void TearDown() => CoreServices.Engine = _previousEngine;

        // ------------------------------------------------------------------ fixture enumeration

        static IEnumerable<string> JsonFiles(Func<string, bool> filter = null)
        {
            string root = Fixtures.PathOf(SaveRoot);
            if (!Directory.Exists(root)) yield break;
            foreach (string f in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                string rel = SaveRoot + "/" + f.Substring(root.Length + 1).Replace('\\', '/');
                if (filter == null || filter(rel)) yield return rel;
            }
        }

        static bool IsRunSave(string rel)
        {
            string name = Path.GetFileName(rel);
            return rel.Contains("/saves/") && (name == "current_run.json" || name == "quicksave.json" || Regex.IsMatch(name, @"^slot_\d\d\.json$"));
        }

        static bool IsWorldSave(string rel) => Path.GetFileName(rel) == "world.json" || Path.GetFileName(rel) == "world_snapshot_to_dict.json";

        public static IEnumerable<string> AllJsonFiles() => JsonFiles();
        public static IEnumerable<string> RunSaveFiles() => JsonFiles(IsRunSave);
        public static IEnumerable<string> WorldSaveFiles() => JsonFiles(IsWorldSave);
        public static IEnumerable<string> IndexFiles() => JsonFiles(r => r.EndsWith("/saves/index.json", StringComparison.Ordinal));
        public static IEnumerable<string> ManifestFiles() => JsonFiles(r => r.EndsWith(".manifest.json", StringComparison.Ordinal));

        /// <summary>Captured <c>user://saves</c> trees that SaveLoadService can load.</summary>
        public static IEnumerable<string> SaveTrees() => new[]
        {
            "capture_main_playable/mid_run/saves",
            "capture_main_playable/after_completion/saves",
            "capture_main_playable/residual/saves",
            "capture_world_snapshot/user_data/saves",
            "capture_world_snapshot/residual/saves",
        };

        /// <summary>Godot writes LF; a Windows checkout may not, so compare on LF.</summary>
        static string ReadLf(string rel) => Fixtures.ReadText(rel).Replace("\r\n", "\n");

        static void AssertTree(object expected, object actual, string where, TreeDiff.Options options = null)
        {
            var diffs = TreeDiff.Compare(expected, actual, options);
            if (diffs.Count > 0) Assert.Fail($"{where}: {TreeDiff.Format(diffs)}");
        }

        // ================================================================== (a) snapshot round trips

        [TestCaseSource(nameof(RunSaveFiles))]
        public void A_RunSave_RoundTripsThroughRunSnapshot(string rel)
        {
            // Exact-number read: ints stay long and floats stay double, i.e. the Godot in-memory types that wrote the
            // file. ToDict(FromDict(x)) must reproduce them exactly (int fields stay int, float fields stay float).
            GdDict exact = Fixtures.ReadDict(rel);
            RunSnapshot snap = RunSnapshot.FromDict(exact, exact.GetString("slice_version"), exact.GetString("godot_version"));
            Assert.IsNotNull(snap, "FromDict rejected the Godot save");
            Assert.AreEqual(SaveMigrationService.GodotTargetVersion, snap.SliceVersion);
            AssertTree(exact, SavePortSchema.GodotView(snap.ToDict()), "exact round trip");
            SavePortSchema.AssertExtensionDefaults(snap.ToDict(), "port keys default for a Godot save");
            SavePortSchema.AssertExtensionOrder(snap.ToDict(), "port key order");

            // Game-facing Godot-faithful parse (every number a double): FromDict coerces the typed fields back.
            var parsed = (GdDict)GdJson.Parse(ReadLf(rel));
            RunSnapshot snap2 = RunSnapshot.FromDict(parsed, parsed.GetString("slice_version"), parsed.GetString("godot_version"));
            Assert.IsNotNull(snap2);
            AssertTree(parsed, SavePortSchema.GodotView(snap2.ToDict()), "Godot-parse round trip", new TreeDiff.Options { IntFloatEquivalent = true });
            AssertTree(snap.ToDict(), snap2.ToDict(), "exact vs Godot-parse snapshot", new TreeDiff.Options { IntFloatEquivalent = true });

            // Embedded Wave 3 model summaries reproduce through the ported models.
            var temp = new BodyTemperatureState();
            temp.Configure(new GdDict());
            temp.ApplySummary(snap.TemperatureSummary);
            AssertTree(snap.TemperatureSummary, temp.GetSummary(), "temperature_summary via BodyTemperatureState");
            var tree = new SkillTreeState();
            Assert.IsTrue(tree.ApplySummary(snap.SkillTreeSummary));
            AssertTree(snap.SkillTreeSummary, tree.ToDict(), "skill_tree_summary via SkillTreeState");
        }

        [Test]
        public void A_MigratedRunSave_RoundTrips_WithGodotTypeCoercion()
        {
            // slot_legacy.migrated.json is written by load_from_slot straight from the parsed+migrated Dictionary, so
            // Godot's parse turned every int into a float ("4.0"). RunSnapshot.from_dict coerces the typed top-level
            // fields back to int exactly as the GDScript does; those three fields are the only strict differences.
            const string rel = SaveRoot + "/legacy/save_migration_service_smoke/residual/saves/slot_legacy.migrated.json";
            GdDict exact = Fixtures.ReadDict(rel);
            RunSnapshot snap = RunSnapshot.FromDict(exact, SaveMigrationService.GodotTargetVersion, exact.GetString("godot_version"));
            Assert.IsNotNull(snap);
            var diffs = TreeDiff.Compare(exact, SavePortSchema.GodotView(snap.ToDict()));
            CollectionAssert.AreEquivalent(
                new[] { "$.current_objective_sequence", "$.saved_at_epoch", "$.world_seed" },
                diffs.Select(d => d.Substring(0, d.IndexOf(':'))).ToArray(),
                TreeDiff.Format(diffs));
            Assert.IsTrue(diffs.All(d => d.Contains("expected float") && d.Contains("got int")), TreeDiff.Format(diffs));
            AssertTree(exact, SavePortSchema.GodotView(snap.ToDict()), "value round trip", new TreeDiff.Options { IntFloatEquivalent = true });
            SavePortSchema.AssertExtensionDefaults(snap.ToDict(), "port keys");
        }

        [TestCaseSource(nameof(WorldSaveFiles))]
        public void A_WorldSave_RoundTripsThroughWorldSnapshot(string rel)
        {
            GdDict exact = Fixtures.ReadDict(rel);
            WorldSnapshot ws = WorldSnapshot.FromDict(exact, exact.GetString("slice_version"), exact.GetString("godot_version"));
            Assert.IsNotNull(ws, "FromDict rejected the Godot world save");
            Assert.AreEqual(WorldSnapshot.WorldSliceVersion, ws.SliceVersion);
            AssertTree(exact, ws.ToDict(), "exact round trip");

            var parsed = (GdDict)GdJson.Parse(ReadLf(rel));
            WorldSnapshot ws2 = WorldSnapshot.FromDict(parsed, parsed.GetString("slice_version"), parsed.GetString("godot_version"));
            Assert.IsNotNull(ws2);
            AssertTree(parsed, ws2.ToDict(), "Godot-parse round trip", new TreeDiff.Options { IntFloatEquivalent = true });

            // world_summary is SynapticSeaWorld.get_summary(): apply + get reproduces it with exact types.
            var world = new SynapticSeaWorld();
            Assert.IsTrue(world.ApplySummary(ws.WorldSummary));
            AssertTree(ws.WorldSummary, world.GetSummary(), "world_summary via SynapticSeaWorld");
        }

        [TestCaseSource(nameof(IndexFiles))]
        public void A_Index_RoundTripsThroughSaveIndexState(string rel)
        {
            GdDict exact = Fixtures.ReadDict(rel);
            AssertTree(exact, SaveIndexState.FromDict(exact).ToDict(), "index round trip");
        }

        [TestCaseSource(nameof(ManifestFiles))]
        public void A_Manifest_RoundTripsThroughCloudManifestState(string rel)
        {
            GdDict exact = Fixtures.ReadDict(rel);
            AssertTree(exact, CloudManifestState.FromDict(exact).ToDict(), "manifest round trip");
            // The manifest's sha is the sha256 of the captured slot file's text (the load gate relies on it).
            string slotId = exact.GetString("slot_id");
            string dir = rel.Substring(0, rel.LastIndexOf("/.cloud/", StringComparison.Ordinal));
            string payload = dir + "/" + (slotId == SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID ? "current_run" : slotId) + ".json";
            if (!Fixtures.Exists(payload)) Assert.Pass($"payload {payload} not captured (deleted before capture)");
            string text = ReadLf(payload);
            if (Encoding.UTF8.GetByteCount(text) != exact.GetInt("payload_size_bytes"))
                Assert.Pass("payload was rewritten after this manifest was written");
            Assert.AreEqual(exact.GetString("payload_sha256"), GdString.Sha256Text(text));
        }

        // ================================================================== (b) migration pairs

        public static IEnumerable<TestCaseData> MigrationCases()
        {
            const string rel = SaveRoot + "/legacy/migration_cases.json";
            if (!Fixtures.Exists(rel)) yield break;
            GdDict all = Fixtures.ReadDict(rel);
            foreach (string kind in new[] { "migrate_run", "migrate_world" })
                foreach (object c in all.GetArrayOrEmpty(kind))
                    yield return new TestCaseData(kind, ((GdDict)c).GetString("case")).SetName($"B_Migration({kind}:{((GdDict)c).GetString("case")})");
        }

        [TestCaseSource(nameof(MigrationCases))]
        public void B_MigrationCase_MatchesGodot(string kind, string caseName)
        {
            GdDict all = Fixtures.ReadDict(SaveRoot + "/legacy/migration_cases.json");
            var c = all.GetArrayOrEmpty(kind).Cast<GdDict>().Single(x => x.GetString("case") == caseName);
            var service = new SaveMigrationService();
            GdDict input = c.GetDict("input").DeepCopy();
            GdDict result = kind == "migrate_run" ? service.MigrateRun(input) : service.MigrateWorld(input);
            var expected = (GdDict)((GdDict)c.Get("result")).DeepCopy();
            if (caseName == "run_from_gate2-current-run-4")
            {
                // Godot's current version is the port's legacy one: the port runs its run-4 -> run-5 step.
                Assert.IsFalse(expected.GetBool("migrated"));
                expected["migrated"] = true;
            }
            AssertTree(expected, SavePortSchema.GodotView(result), $"{kind}:{caseName}");
            // Every migrated run dict carries the port keys with their defaults (a newer-than-us dict is left alone).
            if (result.Get("dict") is GdDict migrated)
            {
                GdDict run = kind == "migrate_run" ? migrated : migrated.GetDictOrEmpty("home_ship");
                if (run.GetString("slice_version") == SaveMigrationService.TargetVersion)
                    SavePortSchema.AssertExtensionDefaults(run, $"{kind}:{caseName} port keys");
            }
            // The service must not mutate its input (Godot deep-copies before stepping).
            AssertTree(c.GetDict("input"), input, "input left untouched");
        }

        [Test]
        public void B_KnownVersionsMatchGodot()
        {
            GdDict all = Fixtures.ReadDict(SaveRoot + "/legacy/migration_cases.json");
            // The port's chain is Godot's chain plus its own gate2-current-run-5 and gate2-current-run-6.
            GdArray known = SaveMigrationService.KnownVersions;
            var godotChain = new GdArray();
            for (int i = 0; i < known.Count - 2; i++)
                godotChain.Add(known[i]);
            AssertTree(all.Get("known_versions"), godotChain, "known_versions");
            Assert.AreEqual("gate2-current-run-5", known[known.Count - 2]);
            Assert.AreEqual("gate2-current-run-6", known.Back());
            Assert.AreEqual(all.GetString("target_version"), SaveMigrationService.GodotTargetVersion);
            Assert.AreEqual("gate2-current-run-6", SaveMigrationService.TargetVersion);
            Assert.AreEqual(SaveMigrationService.TargetVersion, SaveLoadService.CURRENT_SLICE_VERSION);
            Assert.AreEqual(all.GetString("world_target_version"), SaveMigrationService.WorldTargetVersion);
        }

        // ================================================================== (c) SaveLoadService over captured saves

        sealed class Seeded
        {
            public MemoryStorage Storage;
            public ManualClock Clock;
            public SaveLoadService Service;
            public GdDict Index; // Godot's index.json (Godot parse)
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Seeded Seed(string tree)
        {
            string root = Fixtures.PathOf(SaveRoot + "/" + tree);
            var s = new Seeded { Storage = new MemoryStorage(), Clock = new ManualClock() };
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string relInTree = f.Substring(root.Length + 1).Replace('\\', '/');
                string text = File.ReadAllText(f, new UTF8Encoding(false)).Replace("\r\n", "\n");
                s.Files["user://saves/" + relInTree] = text;
                s.Storage.WriteText("user://saves/" + relInTree, text);
            }
            s.Service = new SaveLoadService(s.Storage, s.Clock);
            s.Index = s.Files.TryGetValue(SaveLoadService.INDEX_PATH, out string idx) ? (GdDict)GdJson.Parse(idx) : new GdDict();
            // The version gate compares against the running engine's version string; the capture ran 4.7.1.
            string anySave = s.Files.Where(kv => !kv.Key.Contains("/.cloud/") && kv.Key != SaveLoadService.INDEX_PATH).Select(kv => kv.Value).FirstOrDefault()
                             ?? s.Files[SaveLoadService.INDEX_PATH];
            CoreServices.Engine = new FixedEngineInfo(((GdDict)GdJson.Parse(anySave)).GetString("godot_version"));
            return s;
        }

        static GdDict IndexRow(GdDict index, string slotId) =>
            index.GetArrayOrEmpty("slots").Cast<GdDict>().FirstOrDefault(r => r.GetString("slot_id") == slotId);

        /// <summary>
        /// Godot parses every JSON number as a float, so a load → save cycle rewrites nested int literals as
        /// <c>N.0</c> (typed RunSnapshot/WorldSnapshot fields are coerced back to int). Asserts the rewritten text
        /// differs from the original only by that, and returns how many lines changed.
        /// </summary>
        static int AssertOnlyIntLiteralsBecameFloats(string original, string rewritten, string where)
        {
            string[] a = original.Split('\n');
            string[] b = rewritten.Split('\n');
            Assert.AreEqual(a.Length, b.Length, $"{where}: line count differs");
            var intLine = new Regex(@"^(\s*(?:""(?:[^""\\]|\\.)*"":\s)?)(-?\d+)(,?)$");
            int changed = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;
                Match m = intLine.Match(a[i]);
                Assert.IsTrue(m.Success && b[i] == m.Groups[1].Value + m.Groups[2].Value + ".0" + m.Groups[3].Value,
                    $"{where}: line {i + 1} differs beyond int→float rendering:\n  godot: {a[i]}\n  port:  {b[i]}");
                changed++;
            }
            return changed;
        }

        static void AssertRewrittenTree(object expectedTree, string written, string where)
        {
            // Compare what a reader sees: both sides through Godot's parse.
            object expected = GdJson.Parse(GdJson.Stringify(expectedTree, "\t"));
            AssertTree(expected, GdJson.Parse(written), where);
        }

        [TestCaseSource(nameof(SaveTrees))]
        public void C_SaveLoadService_LoadsAndRewritesCapturedSaves(string tree)
        {
            Seeded s = Seed(tree);
            var slots = new List<string>();
            if (s.Files.ContainsKey(SaveLoadService.SAVE_PATH)) slots.Add(SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID);
            foreach (string id in new[] { "slot_01", "slot_02", "slot_03", "slot_04", "slot_05", "slot_06", "quicksave", "autosave_a", "autosave_b", "autosave_c" })
                if (s.Files.ContainsKey("user://saves/" + id + ".json")) slots.Add(id);
            bool hasWorld = s.Files.ContainsKey(SaveLoadService.WORLD_SLOT_FILE);
            Assert.IsTrue(slots.Count > 0 || hasWorld, "tree has no loadable save");
            Assert.AreEqual(s.Files.ContainsKey(SaveLoadService.SAVE_PATH) || hasWorld, s.Service.HasSave());

            foreach (string slotId in slots)
            {
                string path = slotId == SaveLoadService.ACTIVE_AUTOSAVE_SLOT_ID ? SaveLoadService.SAVE_PATH : "user://saves/" + slotId + ".json";
                string original = s.Files[path];
                Assert.IsTrue(s.Service.HasSlot(slotId), slotId);
                RunSnapshot snap = s.Service.LoadFromSlot(slotId);
                Assert.IsNotNull(snap, $"{tree}: load_from_slot({slotId}) failed (version gate / manifest sha gate)");
                Assert.IsTrue(s.Storage.FileExists(path), "a good load must not quarantine the file");
                // Godot's current run-4 is legacy for the port: the load runs the run-4 -> run-5 step and persists the
                // migrated form, which is Godot's file (as Godot parses it) plus the port keys at their defaults.
                string migratedPath = GdString.TrimSuffix(path, ".json") + ".migrated.json";
                Assert.IsTrue(s.Storage.FileExists(migratedPath), "Godot run-4 saves migrate to run-5 on load");
                var migratedTree = (GdDict)GdJson.Parse(s.Storage.ReadText(migratedPath));
                AssertTree(GdJson.Parse(original), SavePortSchema.GodotView(migratedTree), $"{tree}/{slotId} migrated form");
                SavePortSchema.AssertExtensionDefaults(migratedTree, $"{tree}/{slotId} migrated port keys");
                Assert.AreEqual(SaveMigrationService.GodotTargetVersion, ((GdDict)GdJson.Parse(original)).GetString("slice_version"));

                // Write it back as the same run, at the same instant Godot indexed it.
                GdDict row = IndexRow(s.Index, slotId);
                Assert.IsNotNull(row, $"{slotId} has no Godot index row");
                s.Clock.Unix = row.GetFloat("saved_at_epoch");
                s.Service.SetActiveRunId(snap.RunId);
                Assert.IsTrue(s.Service.SaveToSlot(slotId, snap, row.GetString("slot_kind"), snap.IsQuicksave, row.GetString("display_name")));
                string written = s.Storage.ReadText(path);
                var writtenTree = (GdDict)GdJson.Parse(written, exactNumbers: true);
                Assert.AreEqual(SaveLoadService.CURRENT_SLICE_VERSION, writtenTree.GetString("slice_version"));
                SavePortSchema.AssertExtensionDefaults(writtenTree, $"{tree}/{slotId} rewritten port keys");
                string writtenGodotView = GdJson.Stringify(SavePortSchema.GodotView(writtenTree), "	");
                AssertRewrittenTree(GdJson.Parse(original), writtenGodotView, $"{tree}/{slotId} rewrite");
                AssertOnlyIntLiteralsBecameFloats(original, writtenGodotView, $"{tree}/{slotId}");

                GdDict newRow = IndexRow((GdDict)GdJson.Parse(s.Storage.ReadText(SaveLoadService.INDEX_PATH)), slotId);
                AssertTree(row, SavePortSchema.GodotView(newRow), $"{slotId} index row", new TreeDiff.Options { IgnoreKeys = { "payload_size_bytes" } });
                Assert.AreEqual((double)Encoding.UTF8.GetByteCount(written), newRow.GetFloat("payload_size_bytes"));
            }

            if (hasWorld)
            {
                string original = s.Files[SaveLoadService.WORLD_SLOT_FILE];
                WorldSnapshot ws = s.Service.LoadWorld();
                Assert.IsNotNull(ws, $"{tree}: load_world failed");
                var expected = (GdDict)GdJson.Parse(original);
                GdDict homeShip = expected.GetDictOrEmpty("home_ship");
                bool homeShipMigrated = homeShip.GetString("slice_version") != SaveLoadService.CURRENT_SLICE_VERSION;
                if (homeShipMigrated)
                    expected["home_ship"] = new SaveMigrationService().MigrateRun(homeShip)["dict"]; // migrate_world upgrades the embedded run

                GdDict row = IndexRow(s.Index, "world");
                Assert.IsNotNull(row, "world has no Godot index row");
                s.Clock.Unix = row.GetFloat("saved_at_epoch");
                s.Service.SetActiveRunId(ws.RunId);
                Assert.IsTrue(s.Service.SaveWorld(ws));
                string written = s.Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE);
                AssertRewrittenTree(expected, written, $"{tree}/world rewrite");
                if (!homeShipMigrated) AssertOnlyIntLiteralsBecameFloats(original, written, $"{tree}/world");
                GdDict writtenHome = ((GdDict)GdJson.Parse(written)).GetDictOrEmpty("home_ship");
                if (!writtenHome.IsEmpty && writtenHome.GetString("slice_version") == SaveLoadService.CURRENT_SLICE_VERSION)
                {
                    // Godot's keys through the Godot view; the port keys are the migration defaults.
                    GdDict godotHome = ((GdDict)GdJson.Parse(original)).GetDictOrEmpty("home_ship");
                    if (godotHome.GetString("slice_version") == SaveMigrationService.GodotTargetVersion)
                        AssertTree(godotHome, SavePortSchema.GodotView(writtenHome), $"{tree}/world home_ship Godot keys");
                    SavePortSchema.AssertExtensionDefaults(writtenHome, $"{tree}/world home_ship port keys");
                }

                GdDict newRow = IndexRow((GdDict)GdJson.Parse(s.Storage.ReadText(SaveLoadService.INDEX_PATH)), "world");
                AssertTree(row, newRow, "world index row", new TreeDiff.Options { IgnoreKeys = { "payload_size_bytes" } });
            }

            // After rewriting every slot, the whole index matches Godot's apart from the rewrite-dependent sizes and
            // the index's own write stamp.
            AssertTree(s.Index, SavePortSchema.GodotView(GdJson.Parse(s.Storage.ReadText(SaveLoadService.INDEX_PATH))), "index.json",
                new TreeDiff.Options { IgnoreKeys = { "payload_size_bytes", "updated_at" } });
        }

        [Test]
        public void C_WrongEngineVersion_IsRejectedAndQuarantined()
        {
            Seeded s = Seed("capture_main_playable/mid_run/saves");
            CoreServices.Engine = new FixedEngineInfo("4.7.2-stable (official)");
            s.Clock.Unix = 1_789_200_000.5;
            Assert.IsNull(s.Service.LoadFromSlot("slot_01"));
            Assert.IsTrue(s.Storage.FileExists("user://saves/.corrupt/slot_01.1789200000.slot_01.json.bak"));
            Assert.IsTrue(IndexRow((GdDict)GdJson.Parse(s.Storage.ReadText(SaveLoadService.INDEX_PATH)), "slot_01").GetBool("corrupt"));
        }

        [Test]
        public void ServiceWrites_CloudManifests_MatchGodotShape()
        {
            Seeded s = Seed("capture_main_playable/mid_run/saves");
            var godot = (GdDict)GdJson.Parse(s.Files["user://saves/.cloud/slot_01.manifest.json"]);
            RunSnapshot snap = s.Service.LoadFromSlot("slot_01");
            s.Service.SetActiveRunId(snap.RunId);
            Assert.IsTrue(s.Service.SaveToSlot("slot_01", snap, SaveSlotState.SlotKindManual, false, "Manual 1"));
            var port = (GdDict)GdJson.Parse(s.Storage.ReadText("user://saves/.cloud/slot_01.manifest.json"));
            // device_id hashes the host's user:// path; created_at is the service's own clock stamp. build_id is
            // ProjectSettings "application/config/version": Godot returns "" (the setting is registered with an ""
            // default, so the "0.0.0" fallback in cloud_manifest_state.gd never applies), while the Wave 1 port
            // (InfraCompat.ProjectVersion) uses "0.0.0". Reported, not changed here.
            Assert.AreEqual(SaveLoadService.CURRENT_SLICE_VERSION, port.GetString("schema_version"));
            AssertTree(godot, SavePortSchema.GodotView(port), "manifest", new TreeDiff.Options { IgnoreKeys = { "device_id", "created_at", "payload_sha256", "payload_size_bytes" } });
            string written = s.Storage.ReadText("user://saves/slot_01.json");
            Assert.AreEqual(GdString.Sha256Text(written), port.GetString("payload_sha256"));
            Assert.AreEqual((double)Encoding.UTF8.GetByteCount(written), port.GetFloat("payload_size_bytes"));
            Assert.AreEqual("", godot.GetString("build_id"), "Godot's captured build_id");
        }

        [Test]
        public void C_LegacySlot_MigrateOnLoad_WritesGodotsMigratedFileByteForByte()
        {
            // Replays save_migration_service_smoke.gd step 3 and compares the migrated sidecar with Godot's.
            const string migratedRel = SaveRoot + "/legacy/save_migration_service_smoke/residual/saves/slot_legacy.migrated.json";
            string godotMigrated = ReadLf(migratedRel);
            var godotTree = (GdDict)GdJson.Parse(godotMigrated);
            CoreServices.Engine = new FixedEngineInfo(godotTree.GetString("godot_version"));
            var storage = new MemoryStorage();
            var clock = new ManualClock { Unix = godotTree.GetFloat("saved_at_epoch") + 0.25 };
            var service = new SaveLoadService(storage, clock);

            var legacy = new RunSnapshot();
            legacy.SliceVersion = "gate2-current-run-1";
            legacy.GodotVersion = CoreServices.Engine.VersionString;
            legacy.LayoutPath = "res://data/procgen/smoke/seed_000017/layout.json";
            legacy.CurrentObjectiveSequence = 4;
            legacy.PlayerPosition = GdArray.Of(1.0, 0.0, 2.0);
            legacy.ShipSystemsSummary = new GdDict { { "systems", new GdDict { { "power", new GdDict { { "health", 0.5 } } } } } };
            legacy.RouteControlSummary = new GdDict { { "active_blockers", 0L } };
            legacy.OxygenSummary = new GdDict { { "oxygen", 75.0 } };
            legacy.InventorySummary = new GdDict { { "tools", new GdArray() } };
            legacy.FireSummary = new GdDict { { "state", "CLEARED" } };
            legacy.ElectricalArcSummary = new GdDict { { "state", "DISCHARGED" } };
            legacy.ObjectiveProgressSummary = new GdDict { { "current", 4L } };
            legacy.SavedAt = "2026-06-20T00:00:00";
            Assert.IsTrue(service.SaveToSlot("slot_legacy", legacy, SaveSlotState.SlotKindManual, false, "Legacy"));

            RunSnapshot migrated = service.LoadFromSlot("slot_legacy");
            Assert.IsNotNull(migrated);
            Assert.AreEqual(SaveMigrationService.TargetVersion, migrated.SliceVersion);
            Assert.IsTrue(migrated.PlayerProgressionSummary.Has("class_id"));
            string portMigrated = storage.ReadText("user://saves/slot_legacy.migrated.json");
            Assert.IsNotNull(portMigrated, "migrated form not persisted");
            // Byte for byte on Godot's keys (the port additionally walks run-4 -> run-5 and adds its keys).
            var portTree = (GdDict)GdJson.Parse(portMigrated, exactNumbers: true);
            SavePortSchema.AssertExtensionDefaults(portTree, "migrated port keys");
            Assert.AreEqual(godotMigrated, GdJson.Stringify(SavePortSchema.GodotView(portTree), "	"));
        }

        [Test]
        public void C_CorruptSlot_BackupMatchesGodotsQuarantineFile()
        {
            const string bakRel = SaveRoot + "/main_playable_slice_multislot_save_smoke/residual/saves/.corrupt/slot_01.1789104069.slot_01.json.bak";
            if (!Fixtures.Exists(bakRel)) Assert.Ignore("corrupt backup not captured");
            string garbage = Fixtures.ReadText(bakRel);
            var storage = new MemoryStorage();
            var clock = new ManualClock { Unix = 1_789_104_069.4 };
            var service = new SaveLoadService(storage, clock);
            storage.WriteText("user://saves/slot_01.json", garbage);
            Assert.IsNull(service.LoadFromSlot("slot_01"));
            Assert.IsFalse(storage.FileExists("user://saves/slot_01.json"));
            string backupPath = "user://saves/.corrupt/" + Path.GetFileName(Fixtures.PathOf(bakRel));
            Assert.AreEqual(garbage, storage.ReadText(backupPath));
        }

        // ================================================================== (d) byte-level writer parity

        [TestCaseSource(nameof(AllJsonFiles))]
        public void D_Stringify_ReproducesGodotFileBytes(string rel)
        {
            string text = ReadLf(rel);
            // The file as Godot's writer produced it from in-memory Variants: exact numbers restore those types
            // (int vs float), so re-stringifying must give back the identical bytes.
            string fromExact = GdJson.Stringify(GdJson.Parse(text, exactNumbers: true), "\t");
            Assert.AreEqual(text, fromExact, "exact-typed re-stringify");
        }

        [TestCaseSource(nameof(AllJsonFiles))]
        public void D_GodotParse_ThenStringify_OnlyTurnsIntLiteralsIntoFloats(string rel)
        {
            // Godot quirk: JSON.parse_string yields floats only, so parse → stringify (what Godot itself does on
            // load → save) rewrites "17" as "17.0" and changes nothing else.
            string text = ReadLf(rel);
            string fromGodotParse = GdJson.Stringify(GdJson.Parse(text), "\t");
            int changed = AssertOnlyIntLiteralsBecameFloats(text, fromGodotParse, rel);
            TestContext.WriteLine($"{rel}: {changed} int literal line(s) re-rendered as floats");
        }
    }
}
