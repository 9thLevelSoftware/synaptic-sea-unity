using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    /// <summary>Provisioned diagnostic ingredients/skills/tier; actual golden session, catalogs and spatial stations.</summary>
    public class StationTierCraftingTests : InfraDataTestBase
    {
        public const string TierOneRecipe = "craft_sensor_module";
        public const string TierTwoRecipe = "craft_thruster_nozzle";

        public static SessionHarness.Rig Provision(string recipeId, long tier, long batches = 1)
        {
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            RunSession session = rig.Session;
            session.InventoryState.Items.Clear();
            session.PlayerProgression.Skills["fabrication"] = 4L;
            session.CraftingState.GetOrCreateStation("fabricator").ApplyComponentTier(tier);
            foreach (var ingredient in session.CraftingState.GetRecipe(recipeId).GetDictOrEmpty("ingredients"))
            {
                long count = V.I64(ingredient.Value) * batches;
                Assert.AreEqual(count, session.InventoryState.AddItem(V.Str(ingredient.Key), count), "Diagnostic provisioning: " + ingredient.Key);
            }
            return rig;
        }

        public static CraftingStation Fabricator(RunSession session)
        {
            foreach (CraftingStation station in session.CraftingStations)
                if (station.IsValid && station.StationKind == "fabricator") return station;
            Assert.Fail("Golden session must contain the actual registered fabricator.");
            return null;
        }

        public static GdDict Entry(GdArray entries, string recipeId)
        {
            foreach (object entry in entries)
                if (entry is GdDict row && row.GetString("recipe_id") == recipeId) return row;
            Assert.Fail("Missing actual recipe " + recipeId);
            return null;
        }

        public static void AssertQuantities(InventoryState inventory, GdDict expected)
        {
            foreach (var item in expected)
                Assert.AreEqual(V.I64(item.Value), inventory.GetQuantity(V.Str(item.Key)), "Quantity for " + item.Key);
        }

        static string FirstIngredient(RunSession session, string recipeId)
        {
            foreach (object id in session.CraftingState.GetRecipe(recipeId).GetDictOrEmpty("ingredients").Keys) return V.Str(id);
            Assert.Fail("Actual recipe must have inputs.");
            return "";
        }

        static void FillOutput(RunSession session, string recipeId)
        {
            string output = session.CraftingState.GetProduces(recipeId).GetString("item_id");
            long max = session.InventoryState.GetDefinition(output).GetInt("max_stack", 99);
            Assert.AreEqual(max, session.InventoryState.AddItem(output, max), "Diagnostic output stack provisioning");
        }

        static void AssertBlocked(RunSession session, string recipeId, string reason)
        {
            GdDict before = session.InventoryState.Items.ShallowCopy();
            string stationReason = "";
            Fabricator(session).CraftBlocked += (_, why) => stationReason = why;
            Assert.IsFalse(Fabricator(session).TryCraftRecipe(recipeId));
            Assert.AreEqual(reason, stationReason, "Spatial precheck feedback");
            GdDict result = session.BeginCraftFromPicker("fabricator", recipeId);
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual(reason, result.GetString("reason"), "Session picker feedback");
            AssertQuantities(session.InventoryState, before);
        }

        [TestCase(TierOneRecipe, 1L)]
        [TestCase(TierTwoRecipe, 2L)]
        public void ActualTierListsAndStartsMatchingRecipe(string recipeId, long tier)
        {
            RunSession session = Provision(recipeId, tier).Session;
            GdDict row = Entry(session.ListStationRecipeEntries("fabricator"), recipeId);
            Assert.AreEqual("ready", row.GetString("status"));
            Assert.IsTrue(row.GetBool("craftable"));
            GdDict result = session.BeginCraftFromPicker("fabricator", recipeId);
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            Assert.AreEqual("started", result.GetString("reason"));
            Assert.AreEqual(recipeId, session.CraftingState.GetActiveRecipeId());
            foreach (var ingredient in session.CraftingState.GetRecipe(recipeId).GetDictOrEmpty("ingredients"))
                Assert.AreEqual(0L, session.InventoryState.GetQuantity(V.Str(ingredient.Key)));
        }

        [TestCase(TierOneRecipe, 0L)]
        [TestCase(TierTwoRecipe, 1L)]
        public void BelowActualTierPreservesExactGateFeedbackWithoutPayment(string recipeId, long tier)
        {
            RunSession session = Provision(recipeId, tier).Session;
            Assert.AreEqual("insufficient_tier", Entry(session.ListStationRecipeEntries("fabricator"), recipeId).GetString("status"));
            AssertBlocked(session, recipeId, "insufficient_tier");
            Assert.IsFalse(session.CraftingState.IsCrafting());
        }

        [Test]
        public void ListingAndPrecheckUseEffectiveTierIncludingExistingLevel()
        {
            RunSession session = Provision(TierTwoRecipe, 0).Session;
            session.CraftingState.GetStation("fabricator").Level = 2;
            Assert.AreEqual("ready", Entry(session.ListStationRecipeEntries("fabricator"), TierTwoRecipe).GetString("status"));
            Assert.IsTrue(Fabricator(session).TryCraftRecipe(TierTwoRecipe));
        }

        [TestCase("missing_ingredients")]
        [TestCase("insufficient_skill")]
        [TestCase("output_full")]
        public void SufficientTierKeepsOtherExistingGatesVisible(string gate)
        {
            RunSession session = Provision(TierTwoRecipe, 2).Session;
            if (gate == "missing_ingredients") session.InventoryState.RemoveItem(FirstIngredient(session, TierTwoRecipe), 1);
            if (gate == "insufficient_skill") session.PlayerProgression.Skills["fabrication"] = 3L;
            if (gate == "output_full") FillOutput(session, TierTwoRecipe);
            GdDict row = Entry(session.ListStationRecipeEntries("fabricator"), TierTwoRecipe);
            Assert.AreEqual(gate, row.GetString("status"));
            Assert.IsFalse(row.GetBool("craftable"));
            AssertBlocked(session, TierTwoRecipe, gate);
        }

        [Test]
        public void ListingAndPrecheckShareExistingSkillBeforeTierBeforeInputsPriority()
        {
            RunSession session = Provision(TierTwoRecipe, 0).Session;
            session.PlayerProgression.Skills["fabrication"] = 0L;
            session.InventoryState.Items.Clear();
            Assert.AreEqual("insufficient_skill", Entry(session.ListStationRecipeEntries("fabricator"), TierTwoRecipe).GetString("status"));
            AssertBlocked(session, TierTwoRecipe, "insufficient_skill");
        }

        [Test]
        public void ActualStationFirstReadySelectionUsesItsTier()
        {
            RunSession session = Provision(TierOneRecipe, 1).Session;
            Assert.AreEqual(TierOneRecipe, Fabricator(session).FirstReadyRecipeId());
            Assert.IsTrue(Fabricator(session).TryCraftRecipe(Fabricator(session).FirstReadyRecipeId()));
        }

        [Test]
        public void BusyListingAndBothStartPathsDoNotChargeAgain()
        {
            RunSession session = Provision(TierTwoRecipe, 2, 2).Session;
            Assert.IsTrue(session.CraftingState.BeginCraft(TierTwoRecipe, session.InventoryState, session.MaterialState, 4));
            GdDict row = Entry(session.ListStationRecipeEntries("fabricator"), TierTwoRecipe);
            Assert.AreEqual("busy", row.GetString("status"));
            Assert.IsFalse(row.GetBool("craftable"));
            Assert.AreEqual("", Fabricator(session).FirstReadyRecipeId());
            AssertBlocked(session, TierTwoRecipe, "busy");
        }

        [TestCase("tier", "insufficient_tier")]
        [TestCase("material", "missing_ingredients")]
        [TestCase("skill", "insufficient_skill")]
        [TestCase("output", "output_full")]
        public void PreviewThenChangedGateRevalidatesBeforeAnyPayment(string change, string reason)
        {
            RunSession session = Provision(TierTwoRecipe, 2).Session;
            Assert.AreEqual("ready", Entry(session.ListStationRecipeEntries("fabricator"), TierTwoRecipe).GetString("status"));
            if (change == "tier") session.CraftingState.GetStation("fabricator").ApplyComponentTier(1);
            if (change == "material") session.InventoryState.RemoveItem(FirstIngredient(session, TierTwoRecipe), 1);
            if (change == "skill") session.PlayerProgression.Skills["fabrication"] = 3L;
            if (change == "output") FillOutput(session, TierTwoRecipe);
            AssertBlocked(session, TierTwoRecipe, reason);
            Assert.AreEqual(reason, Entry(session.ListStationRecipeEntries("fabricator"), TierTwoRecipe).GetString("status"));
            Assert.IsFalse(session.CraftingState.IsCrafting());
        }

        [Test]
        public void ActualTierUpgradeAfterBlockedPreviewCanStartWithoutStaleRejection()
        {
            RunSession session = Provision(TierTwoRecipe, 1).Session;
            Assert.AreEqual("insufficient_tier", Entry(session.ListStationRecipeEntries("fabricator"), TierTwoRecipe).GetString("status"));
            session.CraftingState.GetStation("fabricator").ApplyComponentTier(2);
            GdDict result = session.BeginCraftFromPicker("fabricator", TierTwoRecipe);
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
        }

        [Test]
        public void DuplicatePickerConfirmConsumesExactlyOneActualRecipe()
        {
            RunSession session = Provision(TierTwoRecipe, 2, 2).Session;
            GdDict ingredients = session.CraftingState.GetRecipe(TierTwoRecipe).GetDictOrEmpty("ingredients");
            long removed = 0;
            session.InventoryState.ItemsRemoved += (_, count) => removed += count;
            Assert.IsTrue(session.BeginCraftFromPicker("fabricator", TierTwoRecipe).GetBool("ok"));
            GdDict second = session.BeginCraftFromPicker("fabricator", TierTwoRecipe);
            Assert.IsFalse(second.GetBool("ok"));
            Assert.AreEqual("busy", second.GetString("reason"));
            AssertQuantities(session.InventoryState, ingredients);
            long expected = 0;
            foreach (var input in ingredients) expected += V.I64(input.Value);
            Assert.AreEqual(expected, removed, "Only the model start consumes inputs, once.");
        }

        [Test]
        public void ActualRegistrationReportsTheTierForwardedToPrecheck()
        {
            RunSession session = Provision(TierTwoRecipe, 2).Session;
            GdDict station = session.GetCatalogSourceRegistrations().GetDictOrEmpty("stations").GetDictOrEmpty("fabricator");
            Assert.AreEqual(session.CraftingState.GetStation("fabricator").EffectiveTier(), station.GetInt("precheck_tier"));
        }
    }
}
