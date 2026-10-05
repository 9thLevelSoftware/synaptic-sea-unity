using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.UI;

namespace SynapticSea.Tests.Unity
{
    public class AuxiliaryCargoPanelTests : UiTestBase
    {
        [Test]
        public void RelayBlocksBulkButPreservesManualQuantityTransferAndRechecksPower()
        {
            var player = new InventoryState(); player.AddItem("scrap_metal", 4);
            var hold = ShipInventory.Create(); hold.AddItem("wiring_spool", 4);
            var panel = new InventoryPanel(); panel.OpenTransfer(player, hold, "HOLD", EquipmentState.Create());
            bool ready = false;
            panel.BindBulkTransferGate(() => new GdDict { { "ok", ready }, { "reason", "Cargo relay has no power" } });
            Assert.AreEqual(0, panel.DepositAllToContainer());
            Assert.AreEqual(0, panel.TransferAllFrom(InventoryPanel.PaneContainer));
            Assert.AreEqual(4, player.GetQuantity("scrap_metal"));
            Assert.AreEqual(4, hold.GetQuantity("wiring_spool"));
            StringAssert.Contains("Cargo relay has no power", panel.StatusDisplay);
            Assert.AreEqual(1, panel.TransferQuantity(InventoryPanel.PaneSelf, "scrap_metal", 1));
            Assert.AreEqual(1, panel.TransferQuantity(InventoryPanel.PaneContainer, "wiring_spool", 1));
            ready = true;
            Assert.Greater(panel.DepositAllToContainer(), 0);
            Assert.AreEqual(0, player.GetQuantity("scrap_metal"));
            ready = false;
            Assert.AreEqual(0, panel.TransferAllFrom(InventoryPanel.PaneContainer));
            panel.OpenTransfer(player, hold, "MANUAL CART", EquipmentState.Create());
            Assert.Greater(panel.TransferAllFrom(InventoryPanel.PaneContainer), 0, "a new ordinary context has no stale cargo relay policy");
        }
    }
}
