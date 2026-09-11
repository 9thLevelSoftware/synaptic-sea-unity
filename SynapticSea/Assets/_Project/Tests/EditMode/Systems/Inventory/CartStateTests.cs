using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class CartStateTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var cart = CartState.Create("cart_1", 200.0);
            cart.ParkedShipId = "home";
            cart.ParkedPosition = new Vec3(2f, 0f, 3f);
            cart.PushSpeedMultiplier = 0.55;
            cart.GetHold().AddItem("scrap_metal", 4);
            GdDict summary = cart.GetSummary();
            var clone = CartState.Create("x", 1.0);
            Assert.IsTrue(clone.ApplySummary(summary));
            Assert.AreEqual("cart_1", clone.CartId);
            Assert.AreEqual(new Vec3(2f, 0f, 3f), clone.ParkedPosition);
            Assert.AreEqual(0.55, clone.PushSpeedMultiplier);
            Assert.AreEqual(4L, clone.GetHold().GetQuantity("scrap_metal"));
            Assert.IsTrue(V.VariantEquals(summary, clone.GetSummary()));
            Assert.IsFalse(CartState.Create("y").ApplySummary(new GdDict()), "empty summary rejected");
        }

        [Test]
        public void CargoTransfer_LoadsAndUnloadsCart()
        {
            var cart = CartState.Create("cart_1", 200.0);
            Assert.AreEqual(0.7, cart.PushSpeedMultiplier);
            Assert.AreEqual(200.0, cart.GetHold().GetMaxWeight());
            var player = new InventoryState();
            player.AddItem("scrap_metal", 6);
            Assert.AreEqual(6L, CargoTransfer.DepositAll(player, cart.GetHold()).GetInt("total_moved"));
            Assert.AreEqual(0L, player.GetQuantity("scrap_metal"));
            Assert.AreEqual(6L, cart.GetHold().GetQuantity("scrap_metal"));
            Assert.AreEqual(6L, CargoTransfer.WithdrawCategory(cart.GetHold(), player, "part").GetInt("total_moved"));
        }
    }
}
