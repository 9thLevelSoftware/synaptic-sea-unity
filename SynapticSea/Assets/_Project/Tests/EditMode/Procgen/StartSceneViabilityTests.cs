using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// Unity-port C3: the generated New Run home start. Godot's StartSceneBuilder has no fallback when a derelict lacks a
    /// dock room; the port places the life boat at the boarding/airlock cell (opt-in on <see cref="StartSceneBuilder.Build"/>),
    /// gates a home start on the structural validator, walkability and a dock anchor, and reseeds deterministically.
    /// Also covers <c>user://</c> resolution through <see cref="FileSystemResourceReader"/>.
    /// </summary>
    public class StartSceneViabilityTests
    {
        CollectingLog _log;

        [SetUp]
        public void SetUp()
        {
            _log = new CollectingLog();
            CoreServices.Log = _log;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
            CoreServices.Log = NullLog.Instance;
        }

        [Test]
        public void BuildWithBoardingFallbackPlacesTheLifeBoatWhenThereIsNoDockRoom()
        {
            Assert.IsNull(StartSceneBuilder.Build(1), "Godot parity: no dock room, no start scene");
            StartSceneBuilder.StartSceneDocuments docs = StartSceneBuilder.Build(1, boardingCellFallback: true);
            Assert.IsNotNull(docs);
            Assert.AreNotEqual(Vec3.Inf, docs.LifeBoatPosition);
            Vec3 boarding = StartSceneBuilder.FindBoardingPosition(docs.Derelict.Layout, docs.Derelict.GameplaySlice.GetString("start_room", ""));
            Assert.AreEqual(boarding + new Vec3(0.0, 0.0, StartSceneBuilder.DOCK_GAP), docs.LifeBoatPosition);
        }

        [Test]
        public void HomeStartIsDeterministicAndViable()
        {
            StartSceneBuilder.HomeStart a = StartSceneBuilder.BuildHomeStart(42, "breach_field", "hardened");
            StartSceneBuilder.HomeStart b = StartSceneBuilder.BuildHomeStart(42, "breach_field", "hardened");
            Assert.IsNotNull(a);
            Assert.AreEqual(42, a.RequestedSeed);
            Assert.AreEqual(a.Seed, b.Seed);
            Assert.AreEqual(a.Documents.LayoutJson, b.Documents.LayoutJson, "same seed, same layout text");
            Assert.AreEqual("", StartSceneBuilder.ValidateHomeStart(a.Documents, out Vec3 anchor, out string source));
            Assert.AreNotEqual(Vec3.Inf, anchor);
            Assert.AreEqual(source, a.AnchorSource);
            Assert.AreEqual("breach_field", a.Documents.Layout.GetString("biome_id", ""));
            Assert.AreEqual("hardened", a.Documents.Layout.GetString("difficulty_id", ""));
            Assert.AreEqual(a.Seed, a.Blueprint.SeedValue);
            StartSceneBuilder.HomeStart other = StartSceneBuilder.BuildHomeStart(43, "breach_field", "hardened");
            Assert.AreNotEqual(a.Documents.LayoutJson, other.Documents.LayoutJson, "a different seed differs");
        }

        [Test]
        public void RejectedSeedsReseedToSeedPlusOneAndLog()
        {
            StartSceneBuilder.HomeStart start = StartSceneBuilder.BuildHomeStart(100, "dead_fleet", "standard",
                extraGate: (seed, docs) => seed < 102 ? "forced" : "");
            Assert.IsNotNull(start);
            Assert.AreEqual(102, start.Seed);
            Assert.AreEqual(3, start.Attempts);
            CollectionAssert.AreEqual(new[] { "seed 100: forced", "seed 101: forced" }, start.Rejections);
            Assert.IsTrue(_log.Warnings.Exists(w => w.Contains("reseeded the home start from 100 to 102")));
        }

        [Test]
        public void HomeStartGivesUpAfterMaxAttempts()
        {
            int calls = 0;
            StartSceneBuilder.HomeStart start = StartSceneBuilder.BuildHomeStart(5, "", "standard", extraGate: (seed, docs) =>
            {
                calls++;
                return "never";
            });
            Assert.IsNull(start);
            Assert.AreEqual(StartSceneBuilder.MAX_START_ATTEMPTS, calls);
            Assert.IsTrue(_log.Errors.Exists(e => e.Contains("no viable home start")));
        }

        [Test]
        public void ResourceReaderResolvesUserPathsThroughStorage()
        {
            var storage = new MemoryStorage();
            storage.WriteText("user://runs/r1/layout.json", "{\"a\": 1}");
            var reader = new FileSystemResourceReader(Fixtures.StreamingDataRoot, storage);
            Assert.IsTrue(reader.Exists("user://runs/r1/layout.json"));
            Assert.AreEqual("{\"a\": 1}", reader.ReadText("user://runs/r1/layout.json"));
            Assert.IsFalse(reader.Exists("user://runs/r1/missing.json"));
            Assert.IsNull(reader.ReadText("user://runs/r1/missing.json"));
            Assert.IsTrue(reader.Exists(StartSceneBuilder.DERELICT_ARCHETYPE_PATH), "res:// still reads the data root");

            IStorage previous = CoreServices.UserStorage;
            try
            {
                CoreServices.UserStorage = storage;
                CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
                Assert.IsNotNull(CatalogRegistry.LoadDict("user://runs/r1/layout.json"), "CatalogRegistry reads user:// via the process storage");
            }
            finally
            {
                CoreServices.UserStorage = previous;
            }
        }
    }
}
