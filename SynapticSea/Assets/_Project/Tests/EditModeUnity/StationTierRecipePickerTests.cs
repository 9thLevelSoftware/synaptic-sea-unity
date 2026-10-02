using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Game;
using SynapticSea.Tests.Session;
using SynapticSea.UI;

namespace SynapticSea.Tests.Unity
{
    /// <summary>Actual picker/session and fallback host; provisioned diagnostic inputs, skill and model tier.</summary>
    public class StationTierRecipePickerTests : UiTestBase
    {
        static RecipePickerPanel Open(RunSession session, string recipeId)
        {
            var panel = new RecipePickerPanel();
            panel.Bind(new SessionRecipeHost(session, new FakeUiAudio()));
            panel.OpenForStation("fabricator");
            GdArray rows = session.ListStationRecipeEntries("fabricator");
            for (int i = 0; i < rows.Count; i++)
                if (((GdDict)rows[i]).GetString("recipe_id") == recipeId)
                {
                    panel.MoveSelection(i - panel.GetSelectedIndex());
                    return panel;
                }
            Assert.Fail("Actual recipe must be in the picker.");
            return panel;
        }

        [TestCase(StationTierCraftingTests.TierOneRecipe, 1L)]
        [TestCase(StationTierCraftingTests.TierTwoRecipe, 2L)]
        public void ActualSessionPickerDisplaysTierReadyAndDuplicateConfirmChargesOnce(string recipeId, long tier)
        {
            RunSession session = StationTierCraftingTests.Provision(recipeId, tier, 2).Session;
            RecipePickerPanel panel = Open(session, recipeId);
            StringAssert.Contains("Status: Ready", panel.DetailText);
            Assert.IsTrue(panel.ConfirmSelection().GetBool("ok"));
            Assert.IsFalse(panel.IsOpen());
            GdDict second = panel.ConfirmSelection();
            Assert.IsFalse(second.GetBool("ok"));
            // Successful confirm closes the picker and clears its station kind; repeating that closed-panel command is bad_args.
            Assert.AreEqual("bad_args", second.GetString("reason"));
            StationTierCraftingTests.AssertQuantities(session.InventoryState, session.CraftingState.GetRecipe(recipeId).GetDictOrEmpty("ingredients"));
        }

        [TestCase("tier", "insufficient_tier", "Station tier too low")]
        [TestCase("material", "missing_ingredients", "Missing ingredients")]
        public void ActualSessionPickerShowsChangedGateAtConfirm(string change, string reason, string visibleReason)
        {
            string recipeId = StationTierCraftingTests.TierTwoRecipe;
            RunSession session = StationTierCraftingTests.Provision(recipeId, 2).Session;
            RecipePickerPanel panel = Open(session, recipeId);
            StringAssert.Contains("Status: Ready", panel.DetailText);
            if (change == "tier") session.CraftingState.GetStation("fabricator").ApplyComponentTier(1);
            else
                foreach (object id in session.CraftingState.GetRecipe(recipeId).GetDictOrEmpty("ingredients").Keys)
                {
                    session.InventoryState.RemoveItem(V.Str(id), 1);
                    break;
                }
            GdDict before = session.InventoryState.Items.ShallowCopy();
            GdDict result = panel.ConfirmSelection();
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual(reason, result.GetString("reason"));
            Assert.AreEqual(reason, panel.GetStatus());
            StringAssert.Contains(visibleReason, panel.DetailText);
            Assert.IsTrue(panel.IsOpen());
            StationTierCraftingTests.AssertQuantities(session.InventoryState, before);
            Assert.IsFalse(session.CraftingState.IsCrafting());
        }

        [Test]
        public void ActualSessionPickerPreservesVisibleTierRejection()
        {
            RunSession session = StationTierCraftingTests.Provision(StationTierCraftingTests.TierTwoRecipe, 1).Session;
            RecipePickerPanel panel = Open(session, StationTierCraftingTests.TierTwoRecipe);
            StringAssert.Contains("Station tier too low", panel.DetailText);
            Assert.AreEqual("insufficient_tier", panel.ConfirmSelection().GetString("reason"));
            Assert.IsTrue(panel.IsOpen());
        }

        [Test]
        public void FallbackPickerHostUsesActualTierAndRevalidatesAChangedTier()
        {
            string recipeId = StationTierCraftingTests.TierTwoRecipe;
            RunSession session = StationTierCraftingTests.Provision(recipeId, 2).Session;
            var host = new CraftingStationHost(session.CraftingState, session.InventoryState, () => 4, session.MaterialState);
            Assert.AreEqual("ready", StationTierCraftingTests.Entry(host.ListStationRecipeEntries("fabricator"), recipeId).GetString("status"));
            session.CraftingState.GetStation("fabricator").ApplyComponentTier(1);
            GdDict before = session.InventoryState.Items.ShallowCopy();
            GdDict result = host.BeginCraftFromPicker("fabricator", recipeId);
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual("insufficient_tier", result.GetString("reason"));
            StationTierCraftingTests.AssertQuantities(session.InventoryState, before);
        }

        [Test]
        public void ActualSessionPickerShowsBusyWithoutOfferingAnotherPayment()
        {
            string recipeId = StationTierCraftingTests.TierTwoRecipe;
            RunSession session = StationTierCraftingTests.Provision(recipeId, 2, 2).Session;
            Assert.IsTrue(session.CraftingState.BeginCraft(recipeId, session.InventoryState, session.MaterialState, 4));
            RecipePickerPanel panel = Open(session, recipeId);
            StringAssert.Contains("Status: busy", panel.DetailText);
            Assert.AreEqual("busy", panel.ConfirmSelection().GetString("reason"));
            StationTierCraftingTests.AssertQuantities(session.InventoryState, session.CraftingState.GetRecipe(recipeId).GetDictOrEmpty("ingredients"));
        }
    }
}
