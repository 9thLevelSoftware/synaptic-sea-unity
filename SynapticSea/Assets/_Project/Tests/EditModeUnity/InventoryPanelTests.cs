using System.Collections;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.UI;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace SynapticSea.Tests.Unity
{
    public class InventoryPanelTests : UiTestBase
    {
        InventoryState _player;
        ShipInventory _hold;
        EquipmentState _equip;

        [SetUp]
        public void SetUpModels()
        {
            _player = new InventoryState();
            _player.AddItem("scrap_metal", 4);
            _player.AddItem("ration_pack", 3);
            _hold = ShipInventory.Create();
            _hold.AddItem("wiring_spool", 2);
            _hold.AddItem("field_pack", 1);
            _equip = EquipmentState.Create();
        }

        [Test]
        public void TransferMovesTheSelectedStackAndEmitsTransferCompleted()
        {
            var panel = new InventoryPanel();
            int completed = 0;
            panel.TransferCompleted += () => completed++;
            panel.OpenTransfer(_player, _hold, "HOLD", _equip);
            Assert.AreEqual("TRANSFER  |  HOLD", panel.Title);
            Assert.AreEqual(SurfacePanel.BadgeLiveText, panel.BadgeText, "inspection surfaces carry the LIVE badge");
            CollectionAssert.AreEqual(new[] { "ration_pack", "scrap_metal" }, panel.GetPaneIds(InventoryPanel.PaneSelf));
            StringAssert.Contains("Scrap Metal", VisibleText(panel));

            panel.SelectRow(InventoryPanel.PaneSelf, 1, false, false);
            Assert.AreEqual(4, panel.TransferSelected(InventoryPanel.PaneSelf));
            Assert.AreEqual(0, _player.GetQuantity("scrap_metal"));
            Assert.AreEqual(4, _hold.GetQuantity("scrap_metal"));
            Assert.AreEqual(1, completed);
            Assert.IsTrue(panel.ContainerList.Items.Any(i => i.Id == "scrap_metal" && i.Chip == "×4"), "container pane shows the moved stack");
            Assert.AreEqual(Severity.Success, panel.StatusSeverity());
        }

        [Test]
        public void EmptySelectionTransferIsDeniedWithWordingAndSfx()
        {
            var audio = new FakeUiAudio();
            var panel = new InventoryPanel();
            panel.SetAudioManager(audio);
            panel.OpenTransfer(_player, _hold, "HOLD", _equip);
            Assert.AreEqual(0, panel.TransferSelected(InventoryPanel.PaneSelf));
            StringAssert.StartsWith("▲ Caution: Nothing selected", panel.StatusDisplay);
            CollectionAssert.Contains(audio.Sfx, AudioEventSeam.UI_PANEL_CLOSE);
            Assert.AreEqual(0, panel.TransferQuantity(InventoryPanel.PaneSelf, "power_cell", 1), "nothing to move");
        }

        [Test]
        public void EquipFromContainerAndUnequipRoundTrip()
        {
            var panel = new InventoryPanel();
            panel.OpenTransfer(_player, _hold, "HOLD", _equip);
            Assert.IsTrue(panel.EquipFromContainer("field_pack"));
            Assert.AreEqual("field_pack", _equip.GetEquipped("back"));
            Assert.AreEqual(0, _hold.GetQuantity("field_pack"));
            StringAssert.Contains("back  [Field Pack]", VisibleText(panel));
            Assert.IsTrue(panel.UnequipSlot("back"));
            Assert.AreEqual(1, _player.GetQuantity("field_pack"));
            Assert.IsFalse(panel.EquipFromContainer("wiring_spool"), "not equippable");
        }

        [Test]
        public void SplitDragAndContextActionsHaveKeyboardEquivalents()
        {
            var panel = new InventoryPanel();
            panel.OpenTransfer(_player, _hold, "HOLD", _equip);
            int scrap = panel.GetPaneIds(InventoryPanel.PaneSelf).IndexOf("scrap_metal");
            CollectionAssert.AreEquivalent(new[] { "transfer", "transfer_all", "split" }, panel.ContextActionsFor(InventoryPanel.PaneSelf, scrap));
            panel.SelectRow(InventoryPanel.PaneSelf, scrap, false, false);
            CollectionAssert.IsSupersetOf(panel.ActionButtons.Select(b => b.text).ToList(), new[] { "Transfer", "Transfer all", "Split…" });

            panel.InvokeContextAction("split", InventoryPanel.PaneSelf, scrap);
            Assert.IsTrue(panel.IsSplitOpen);
            Assert.AreEqual(2, panel.SplitQuantity, "defaults to half the stack");
            panel.StepSplit(1);
            Assert.AreEqual(3, panel.ConfirmSplit());
            Assert.AreEqual(1, _player.GetQuantity("scrap_metal"));

            int ration = panel.GetPaneIds(InventoryPanel.PaneSelf).IndexOf("ration_pack");
            GdDict payload = panel.RowDragPayload(InventoryPanel.PaneSelf, ration);
            Assert.IsTrue(panel.ZoneCanAccept(InventoryPanel.PaneContainer, payload));
            Assert.IsFalse(panel.ZoneCanAccept(InventoryPanel.PaneSelf, payload), "no drop onto its own pane");
            Assert.IsFalse(panel.ZoneCanAccept("slot:back", payload));
            panel.ZoneDrop(InventoryPanel.PaneContainer, payload);
            Assert.AreEqual(3, _hold.GetQuantity("ration_pack"));

            Assert.Greater(panel.DepositAllToContainer(), 0);
            Assert.AreEqual(0, _player.GetQuantity("scrap_metal"));
        }

        [Test]
        public void SelfModeShowsWeightBadgeAndTooltipPush()
        {
            var pushed = new System.Collections.Generic.List<string>();
            var panel = new InventoryPanel();
            panel.SetTooltipQueryPush(q => pushed.Add(q.GetString("subject_id")));
            panel.OpenSelf(_player, _equip);
            Assert.AreEqual("INVENTORY + GEAR", panel.Title);
            StringAssert.StartsWith("Wt 21.5/50.0 [OK] x1.00", panel.WeightLine());
            panel.SelectRow(InventoryPanel.PaneSelf, 0, false, false);
            Assert.AreEqual("ration_pack", pushed.Last());
            panel.Close();
            Assert.AreEqual("", pushed.Last(), "close clears the tooltip");
            Assert.IsFalse(panel.IsOpen());
        }

        [UnityTest]
        public IEnumerator KeyboardNavigationMovesTheCursorAndSwitchesPanes()
        {
            using (var harness = new UiHarness())
            {
                yield return null;
                var panel = new InventoryPanel();
                harness.Mount(panel);
                panel.OpenTransfer(_player, _hold, "HOLD", _equip);
                panel.RestoreFocus("");
                harness.Layout();
                Assert.AreEqual(panel.SelfList.RowAt(0), harness.Focused, "initial focus is the first carried row");

                UiHarness.Navigate(harness.Focused, NavigationMoveEvent.Direction.Down);
                Assert.AreEqual(panel.SelfList.RowAt(1), harness.Focused);
                CollectionAssert.AreEqual(new object[] { "scrap_metal" }, panel.GetSelectedIds(InventoryPanel.PaneSelf));

                UiHarness.Navigate(harness.Focused, NavigationMoveEvent.Direction.Right);
                Assert.AreEqual(InventoryPanel.PaneContainer, panel.ActivePane);
                Assert.AreEqual(panel.ContainerList.RowAt(0), harness.Focused);

                UiHarness.Submit(panel.SelfList.RowAt(1));
                Assert.AreEqual(4, _hold.GetQuantity("scrap_metal"), "Submit runs the primary (transfer) action");
                Assert.IsNotNull(harness.Focused, "focus survives the refresh");
            }
        }
    }

    static class SurfacePanelTestExtensions
    {
        public static Severity StatusSeverity(this SurfacePanel panel) =>
            panel.Q<StatusLine>() is StatusLine s ? s.Severity : Severity.None;
    }
}
