using System;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    public class FiniteLootTests
    {
        [SetUp] public void Setup()
        {
            CatalogRegistry.Clear();
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }
        [TearDown] public void Cleanup() => CatalogRegistry.Clear();
        static GdDict Spec() => new GdDict { { "id", "kit" }, { "finite_source", true }, { "contents", GdArray.Of(
            new GdDict { { "item_id", "scrap_metal" }, { "qty", 4L } }) } };
        static LootContainer Container(out GdDict state, out InventoryState inventory)
        {
            state = new GdDict(); inventory = new InventoryState();
            Assert.IsTrue(FiniteLootState.TryBind(ref state, "ship_start", "kit", Spec(), out GdDict row));
            var loot = new LootContainer(); loot.Configure("kit", "", "home:kit", inventory, new GdDict(), Vec3.Zero);
            loot.FiniteSource = row; loot.FiniteAccess = () => true;
            return loot;
        }
        [Test] public void PartialAcceptanceRetainsStockAcrossSavedSnapshotAndDepletesExactlyOnce()
        {
            var loot = Container(out GdDict state, out InventoryState inventory);
            long max = ItemDefs.MaxStack(ItemDefs.LoadDefinitions(), "scrap_metal"); inventory.AddItem("scrap_metal", max - 2);
            int training = 0; loot.ContainerSearched += (_, __) => { if (loot.FiniteSearchTrainingPending) training++; };
            Assert.IsTrue(loot.TryInteract(Vec3.Zero));
            Assert.AreEqual(2, loot.FiniteSource.GetDictOrEmpty("remaining").GetInt("scrap_metal")); Assert.IsFalse(loot.Searched); Assert.AreEqual(1, training);
            var snapshot = new RunSnapshot { HomeFiniteLoot = state.DeepCopy(), SliceVersion = "finite-test", GodotVersion = "test" };
            var restored = RunSnapshot.FromDict(PaidSnapshotCodec.Parse(PaidSnapshotCodec.Stringify(snapshot.ToDict())), "finite-test", "test");
            Assert.IsNotNull(restored); state = restored.HomeFiniteLoot;
            Assert.IsTrue(FiniteLootState.TryBind(ref state, "ship_start", "kit", Spec(), out GdDict row)); loot.FiniteSource = row;
            inventory.RemoveItem("scrap_metal", 2);
            Assert.IsTrue(loot.TryInteract(Vec3.Zero)); Assert.IsTrue(loot.Searched); Assert.AreEqual(1, training);
            Assert.IsFalse(loot.TryInteract(Vec3.Zero)); Assert.AreEqual(max, inventory.GetQuantity("scrap_metal"));
        }
        [Test] public void FullStackDoesNotConsumeOrAwardSearch()
        {
            var loot = Container(out GdDict state, out InventoryState inventory); inventory.AddItem("scrap_metal", long.MaxValue);
            GdDict before = state.DeepCopy(); int calls = 0; loot.ContainerSearched += (_, __) => calls++;
            Assert.IsFalse(loot.TryInteract(Vec3.Zero)); Assert.IsTrue(V.VariantEquals(before, state)); Assert.AreEqual(0, calls);
        }
        [Test] public void StaleCandidateCannotBypassExactRangeOrSight()
        {
            var loot = Container(out _, out var inventory); loot.CandidatePlayerInRange = true;
            Assert.IsFalse(loot.TryInteract(new Vec3(1.8, 0, 0))); Assert.IsFalse(loot.TryInteract(new Vec3(50, 0, 0)));
            loot.FiniteAccess = () => false; Assert.IsFalse(loot.TryInteract(Vec3.Zero)); Assert.AreEqual(0, inventory.GetQuantity("scrap_metal"));
        }
        [Test] public void CallbackFailureRollsBackInventoryStockAndTrainingWithReentryRefused()
        {
            var loot = Container(out GdDict state, out InventoryState inventory); GdDict before = state.DeepCopy(); int xp = 0;
            loot.FiniteRollbackSnapshot = () => { int prior = xp; return () => xp = prior; };
            loot.FiniteModelCommit = (_, __) => { Assert.IsFalse(loot.TryInteract(Vec3.Zero)); xp++; throw new InvalidOperationException("modelstage"); };
            Assert.Throws<InvalidOperationException>(() => loot.TryInteract(Vec3.Zero));
            Assert.AreEqual(0, xp); Assert.AreEqual(0, inventory.GetQuantity("scrap_metal")); Assert.IsTrue(V.VariantEquals(before, state)); Assert.IsFalse(loot.Searched);
        }
        [Test] public void PostcommitObserverFailureRetainsAcceptanceAndCannotReplayReward()
        {
            var loot = Container(out GdDict state, out InventoryState inventory); int xp = 0;
            loot.FiniteModelCommit = (_, __) => { if (loot.FiniteSearchTrainingPending) xp++; };
            loot.ContainerSearched += (_, __) => { Assert.IsFalse(loot.TryInteract(Vec3.Zero)); throw new InvalidOperationException("observer"); };
            Assert.Throws<InvalidOperationException>(() => loot.TryInteract(Vec3.Zero));
            Assert.AreEqual(4, inventory.GetQuantity("scrap_metal")); Assert.IsTrue(loot.Searched); Assert.AreEqual(1, xp);
            Assert.IsFalse(loot.TryInteract(Vec3.Zero)); Assert.AreEqual(1, xp); Assert.IsTrue(FiniteLootState.Validate(state, "ship_start", out _));
        }
        [TestCase("owner")] [TestCase("hash")] [TestCase("negative")] [TestCase("overflow")] [TestCase("award")]
        public void MalformedSourceRefusesSnapshotWithoutChangingOriginal(string mutation)
        {
            Container(out GdDict state, out _); GdDict original = state.DeepCopy(); GdDict row = state.GetDictOrEmpty("sources").GetDictOrEmpty("kit");
            if (mutation == "owner") state["ship_id"] = "other";
            if (mutation == "hash") row["source_hash"] = "tampered";
            if (mutation == "negative") row.GetDictOrEmpty("remaining")["scrap_metal"] = -1L;
            if (mutation == "overflow") row.GetDictOrEmpty("remaining")["scrap_metal"] = 5L;
            if (mutation == "award") row["search_training_awarded"] = true;
            var snapshot = new RunSnapshot { HomeFiniteLoot = state, SliceVersion = "finite-test", GodotVersion = "test" };
            Assert.IsNull(RunSnapshot.FromDict(snapshot.ToDict(), "finite-test", "test")); Assert.AreEqual(4, original.GetDictOrEmpty("sources").GetDictOrEmpty("kit").GetDictOrEmpty("remaining").GetInt("scrap_metal"));
        }
        [Test] public void RestoreCannotRecreateMissingLedgerOrMissingSource()
        {
            Assert.IsFalse(FiniteLootState.ValidateSources(new GdDict(), "ship_start", GdArray.Of(Spec()), out _));
            Container(out GdDict state, out _); state.GetDictOrEmpty("sources").Clear();
            Assert.IsFalse(FiniteLootState.ValidateSources(state, "ship_start", GdArray.Of(Spec()), out _));
            Assert.IsTrue(FiniteLootState.ValidateSources(new GdDict(), "ship_start", new GdArray(), out _), "ordinary legacy has no finite rows");
        }
        [Test] public void SelectedDiagnosticKitHasExactReviewedFiniteCounts()
        {
            var gameplay = CatalogRegistry.LoadDict("res://data/diagnostics/earned-entry-home-v1/gameplay_slice.json");
            GdDict kit = null; foreach (object obj in gameplay.GetArrayOrEmpty("loot_containers"))
                if (obj is GdDict row && row.GetBool("finite_source")) { Assert.IsNull(kit); kit = row; }
            Assert.IsNotNull(kit); Assert.IsTrue(FiniteLootState.TryInitial(kit, out GdDict initial)); Assert.AreEqual(4, initial.Count);
            Assert.AreEqual(1, initial.GetInt("wrench")); Assert.AreEqual(4, initial.GetInt("scrap_metal"));
            Assert.AreEqual(4, initial.GetInt("wiring_bundle")); Assert.AreEqual(1, initial.GetInt("fabrication_schematic_basic"));
        }
        [Test] public void ChangedAuthoredContentsCannotRefillSavedSource()
        {
            Container(out GdDict state, out _); GdDict before = state.DeepCopy(); GdDict changed = Spec(); ((GdDict)changed.GetArrayOrEmpty("contents")[0])["qty"] = 5L;
            Assert.IsFalse(FiniteLootState.TryBind(ref state, "ship_start", "kit", changed, out _)); Assert.IsTrue(V.VariantEquals(before, state));
            Assert.IsFalse(FiniteLootState.ValidateSources(state, "ship_start", GdArray.Of(changed), out _));
        }
        [Test] public void LegacyAbsentStateIsOmittedAndLegacyContainerStillConsumesFullBagSearch()
        {
            var snapshot = new RunSnapshot { SliceVersion = "finite-test", GodotVersion = "test" }; Assert.IsFalse(snapshot.ToDict().Has("home_finite_loot"));
            Assert.IsNotNull(RunSnapshot.FromDict(snapshot.ToDict(), "finite-test", "test"));
            var inventory = new InventoryState(); inventory.AddItem("scrap_metal", long.MaxValue);
            var loot = new LootContainer(); loot.Configure("legacy", "", "legacy", inventory, new GdDict(), Vec3.Zero, 1.8, Spec());
            loot.CandidatePlayerInRange = true; Assert.IsTrue(loot.TryInteract(new Vec3(50, 0, 0))); Assert.IsTrue(loot.Searched);
        }
    }
}
