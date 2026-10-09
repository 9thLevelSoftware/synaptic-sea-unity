using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>Phase 1.10: the onboarding composer on generated documents (the session-level behaviour is covered by <c>GeneratedHomeSessionTests</c>).</summary>
    public class HomeObjectiveComposerTests
    {
        [SetUp]
        public void SetUp()
        {
            CoreServices.Log = new CollectingLog();
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

        /// <summary>A generated Pristine home with no guarantee and no composer, as the generator hands it over.</summary>
        static ShipDocuments RawHome(long seed)
        {
            StartSceneBuilder.HomeStart start = StartSceneBuilder.BuildHomeStart(seed, "breach_field", "standard", condition: (long)ShipBlueprint.Condition.Pristine);
            Assert.IsNotNull(start, "a raw home for seed " + seed);
            return start.Documents;
        }

        /// <summary>First seed from 1 whose raw home has at least <see cref="HomeObjectiveComposer.MinUsableRooms"/> usable rooms.</summary>
        static ShipDocuments RoomyHome(out long seed)
        {
            for (seed = 1; seed < 200; seed++)
            {
                ShipDocuments docs = RawHome(seed);
                if (StartingHomeGuarantee.EligibleRooms(docs.Layout, docs.GameplaySlice).Count >= HomeObjectiveComposer.MinUsableRooms) return docs;
            }
            Assert.Fail("no roomy home in 200 seeds");
            return null;
        }

        [Test]
        public void ReplacesTheGeneratedObjectivesWithTheFourTypedOnes_AndAddsBothPickups()
        {
            ShipDocuments docs = RoomyHome(out long seed);
            Assert.AreEqual("", HomeObjectiveComposer.Apply(docs, seed));
            Assert.AreEqual("", HomeObjectiveComposer.Validate(docs));
            CollectionAssert.AreEqual(HomeObjectiveComposer.ChainTypes, docs.GameplaySlice.GetArrayOrEmpty("objectives").OfType<GdDict>().Select(o => o.GetString("type")).ToList());
            GdDict junction = (GdDict)docs.GameplaySlice.GetArrayOrEmpty("objectives")[1];
            Assert.AreEqual("repair_junction", junction.GetString("kind"));
            Assert.AreEqual(2, junction.GetArrayOrEmpty("steps").Count);
            CollectionAssert.AreEquivalent(new[] { HomeObjectiveComposer.ToolPickupId, HomeObjectiveComposer.CalibratorPickupId },
                docs.GameplaySlice.GetArrayOrEmpty(HomeObjectiveComposer.PickupsKey).OfType<GdDict>().Select(p => p.GetString("tool_id")).ToList());
        }

        [Test]
        public void TheTextMirrorAgreesWithTheDocument()
        {
            ShipDocuments docs = RoomyHome(out long seed);
            Assert.AreEqual("", HomeObjectiveComposer.Apply(docs, seed));
            Assert.AreEqual(GdJson.Stringify(GdJson.ParseDict(docs.GameplaySliceJson)), GdJson.Stringify(docs.GameplaySlice));
        }

        [Test]
        public void TheSameSeedGivesTheSameChain()
        {
            ShipDocuments a = RoomyHome(out long seed);
            ShipDocuments b = RawHome(seed);
            Assert.AreEqual("", HomeObjectiveComposer.Apply(a, seed));
            Assert.AreEqual("", HomeObjectiveComposer.Apply(b, seed));
            Assert.AreEqual(GdJson.Stringify(a.GameplaySlice), GdJson.Stringify(b.GameplaySlice));
        }

        [Test]
        public void AHomeWithTooFewUsableRoomsFailsClosed_AndLeavesTheDocumentsUntouched()
        {
            ShipDocuments docs = RoomyHome(out long seed);
            var usable = StartingHomeGuarantee.EligibleRooms(docs.Layout, docs.GameplaySlice);
            GdArray rooms = docs.Layout.GetArrayOrEmpty("rooms");
            for (int i = rooms.Count - 1; i >= 0; i--)
                if (rooms[i] is GdDict room && usable.Skip(HomeObjectiveComposer.MinUsableRooms - 1).Contains(room.GetString("id"))) rooms.RemoveAt(i);
            string before = GdJson.Stringify(docs.GameplaySlice);
            string reason = HomeObjectiveComposer.Apply(docs, seed);
            StringAssert.StartsWith(HomeObjectiveComposer.ReasonTooFewRooms, reason);
            Assert.AreEqual(before, GdJson.Stringify(docs.GameplaySlice));
        }

        [Test]
        public void ValidateRejectsAChainOutOfOrder_APickupOffTheSlots_AndAStaleTextMirror()
        {
            ShipDocuments docs = RoomyHome(out long seed);
            Assert.AreEqual("", HomeObjectiveComposer.Apply(docs, seed));

            ShipDocuments swapped = Copy(docs);
            GdArray objectives = swapped.GameplaySlice.GetArrayOrEmpty("objectives");
            object first = objectives[0];
            objectives[0] = objectives[1];
            objectives[1] = first;
            StringAssert.Contains("not the onboarding chain", HomeObjectiveComposer.Validate(swapped));

            ShipDocuments offSlot = Copy(docs);
            GdDict pickup = (GdDict)offSlot.GameplaySlice.GetArrayOrEmpty(HomeObjectiveComposer.PickupsKey)[0];
            pickup["approach_cell"] = GdArray.Of(9999L, 9999L, 0L);
            StringAssert.Contains("not on a loot slot", HomeObjectiveComposer.Validate(offSlot));

            ShipDocuments stale = Copy(docs);
            stale.GameplaySliceJson = GdJson.Stringify(RawHome(seed).GameplaySlice, "  ");
            StringAssert.Contains("mirror disagrees", HomeObjectiveComposer.Validate(stale));
        }

        static ShipDocuments Copy(ShipDocuments docs) => new ShipDocuments
        {
            Layout = docs.Layout.DeepCopy(),
            SourceLayout = docs.SourceLayout?.DeepCopy(),
            GameplaySlice = docs.GameplaySlice.DeepCopy(),
            LayoutJson = docs.LayoutJson,
            GameplaySliceJson = docs.GameplaySliceJson,
            Kit = docs.Kit,
        };
    }
}
