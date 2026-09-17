using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Input;
using SynapticSea.UI;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    public class UiInputRouterTests : UiTestBase
    {
        [Test]
        public void GameplayMapIsDisabledWhileAnySurfaceIsOpen()
        {
            var input = new SynapticSeaInput();
            try
            {
                var stack = new ModalStack();
                var router = new UiInputRouter(input, stack, cmd => false);
                router.Enable();
                Assert.IsTrue(input.Player.enabled);
                Assert.IsTrue(input.Menu.enabled);
                var wounds = new WoundsPanel();
                stack.Push(wounds);
                router.ApplyGameplayGate();
                Assert.IsFalse(input.Player.enabled, "every gameplay action is blocked while a surface is open");
                stack.Pop(wounds);
                router.ApplyGameplayGate();
                Assert.IsTrue(input.Player.enabled);
                router.Disable();
            }
            finally
            {
                Object.DestroyImmediate(input.asset);
            }
        }

        [Test]
        public void ToggleSurfaceIdsMatchThePanels()
        {
            Assert.AreEqual(new InventoryPanel().SurfaceId, UiInputRouter.ToggleSurfaceIds["toggle_inventory"]);
            Assert.AreEqual(new ScannerPanel().SurfaceId, UiInputRouter.ToggleSurfaceIds["toggle_scanner"]);
            Assert.AreEqual(new ShipModificationPanel().SurfaceId, UiInputRouter.ToggleSurfaceIds["toggle_ship_mod"]);
            Assert.AreEqual(new WoundsPanel().SurfaceId, UiInputRouter.ToggleSurfaceIds["toggle_wounds"]);
            Assert.AreEqual(new ChartPanel().SurfaceId, UiInputRouter.ToggleSurfaceIds["ui_open_map"]);
        }

        [Test]
        public void CompactStatusKeepsTheMostSevereChipAndCountsTheRest()
        {
            var cluster = new HudVitalsCluster();
            cluster.Refresh(new GdDict
            {
                { "health", 18.0 }, { "oxygen", 14.0 }, { "stamina", 12.0 }, { "breach_state", "breach" }, { "heavy", true },
                { "radiation", 62L }, { "sanity", 20L },
            });
            CollectionAssert.AreEqual(new[] { "BREACH", "O2 LOW", "RADIATION 62", "OVERLOADED", "SANITY" }, cluster.StatusChipTexts);
            CollectionAssert.AreEqual(new[] { "⚠ BREACH", "⚠ O2 LOW", "⚠ RADIATION 62", "▲ OVERLOADED", "▲ SANITY" }, cluster.VisibleChipTexts);
            cluster.SetCompact(true, 2);
            CollectionAssert.AreEqual(new[] { "⚠ BREACH", "⚠ O2 LOW", "⚠ +3 danger" }, cluster.VisibleChipTexts);
            cluster.SetCompact(true, 1);
            CollectionAssert.AreEqual(new[] { "⚠ BREACH", "⚠ +4 danger" }, cluster.VisibleChipTexts);
            Assert.AreEqual("⚠ Suit O2", cluster.Oxygen.NameText, "meters carry the severity symbol");
        }
    }
}
