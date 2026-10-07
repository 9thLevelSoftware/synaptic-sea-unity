using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using SynapticSea.App;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace SynapticSea.Tests.PlayMode
{
    public class ComponentInteractionPlayModeTests : ComponentRuntimeFixture
    {
        [UnityTest]
        public IEnumerator OrdinaryBootDoesNotInventToolOrActivateEquipmentAndDiagnosticNoGrantRemovalRefuses()
        {
            yield return Launch(RunLaunchRequest.NewRun());
            Assert.IsFalse(Session.ComponentIntegrationEnabled);
            Assert.AreEqual(0, Session.InventoryState.GetQuantity("wrench"));
            Assert.AreEqual(0, Session.InventoryState.GetQuantity("tool_wrench"));
            GdDict refusal = Session.RequestComponentRemoval("unearned-equipment");
            Assert.IsFalse(refusal.GetBool("ok"));
            Assert.AreEqual("component_integration_inactive", refusal.GetString("reason"));
            yield return Launch(RunLaunchRequest.DiagnosticNewRun());
            GdDict original = Instances(Session.CaptureComponentDomain()).Values.OfType<GdDict>()
                .First(row => row.GetString("holder").StartsWith("slot:" + Session.CurrentShip.ShipId + ":", StringComparison.Ordinal));
            string id = original.GetString("instance_id");
            StandAt(TargetFor(id));
            refusal = Session.RequestComponentRemoval(id);
            Assert.IsFalse(refusal.GetBool("ok"));
            Assert.AreEqual("missing_tool", refusal.GetString("reason"));
            Assert.AreEqual(original.GetString("holder"), Instance(id).GetString("holder"));
            Assert.AreEqual(0, Session.InventoryState.GetQuantity("wrench"));
            TitleScreen title = null;
            yield return ReturnToTitle(value => title = value);
            title.ConfigureComponentDiagnostic(true);
            NewRunSetupPanel setup = title.OpenNewRunSetup();
            setup.FocusRow(NewRunSetupPanel.RowStart);
            setup.Consume(UiCommand.Accept);
            yield return WaitForBoot();
            Assert.AreEqual(RunLaunchMode.NewRun, Boot.Launch.Mode);
            Assert.IsTrue(Boot.Launch.EnableComponentIntegration, "Only the explicitly opted-in Title setup propagates diagnostic New Run.");
            Assert.IsTrue(Session.ComponentIntegrationEnabled);
        }

        [UnityTest]
        public IEnumerator PhysicalActualMountFocusOpensSamePickerAndDiagnosticMarkersNeverOccludeTheirOwnLos()
        {
            Keyboard keyboard = InputSystem.AddDevice<Keyboard>();
            yield return Launch(RunLaunchRequest.DiagnosticNewRun());
            GdDict target = null;
            foreach (GdDict candidate in Session.ListInstallTargets("").OfType<GdDict>())
            {
                StandAt(candidate);
                if (!Boot.Host.FocusedComponentTarget.IsEmpty) { target = Boot.Host.FocusedComponentTarget; break; }
            }
            Assert.IsNotNull(target, "At least one actual physical mount is reachable outside ordinary objective focus.");
            Assert.AreEqual("selection_required", target.GetString("reason"));
            foreach (GameObject marker in Boot.Host.ComponentMarkers.Markers)
            {
                GdDict record = Boot.Host.ComponentMarkers.RecordFor(marker);
                Assert.IsTrue(record.Has("ship_id") && record.Has("slot_id"));
                Assert.IsTrue(marker.GetComponentsInChildren<Collider>(true).All(collider => !collider.enabled), "Visual markers do not obstruct the real structural LOS query.");
            }
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.E));
            yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null;
            Assert.IsTrue(Boot.Ui.ShipMod.IsOpen());
            Assert.AreEqual(target.GetString("slot_id"), Boot.Ui.ShipMod.GetSelectedSlotId());
            Assert.IsFalse(Boot.Ui.ShipMod.List.Items.Any(item => item.Id.Contains("hub_slot")));
            Assert.IsEmpty(Session.GetComponentWorkState().GetString("job_id"), "Opening the physical picker cannot start an unnamed action.");
        }

        [UnityTest]
        public IEnumerator VirtualGamepadKeyboardAndPointerUiDispatchTransferTheExactSameFormInstance()
        {
            var gamepad = InputSystem.AddDevice<Gamepad>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return Launch(RunLaunchRequest.DiagnosticNewRun());
            GdDict domain = Session.CaptureComponentDomain();
            var pair = Instances(domain).Values.OfType<GdDict>().Where(row => row.GetString("holder").StartsWith("slot:" + Session.CurrentShip.ShipId + ":", StringComparison.Ordinal))
                .GroupBy(row => row.GetString("item_form")).First(group => group.Count() >= 2).Take(2).ToArray();
            string low = pair[0].GetString("instance_id"), high = pair[1].GetString("instance_id"), player = Session.GetComponentHolderIds().GetString("player");
            // Explicit carried-equipment fixture using existing generated IDs/holders; timed work is covered by the journey.
            foreach (GdDict row in pair) Instances(domain).GetDictOrEmpty(row.GetString("instance_id"))["holder"] = player;
            Instances(domain).GetDictOrEmpty(low)["condition"] = 0.23;
            Instances(domain).GetDictOrEmpty(high)["condition"] = 0.81;
            Assert.IsTrue(Session.RestoreComponentDomain(domain));
            yield return OpenRealCargo();
            string cargo = Session.GetComponentHolderIds().GetString("ship_cargo");
            SelectInventory(InventoryPanel.PaneSelf, high);
            Boot.Ui.Inventory.SelfList.FocusRow(Boot.Ui.Inventory.SelfList.SelectedIndex);
            yield return null;
            Press(gamepad.buttonSouth);
            yield return null;
            Release(gamepad.buttonSouth);
            yield return null;
            Assert.AreEqual(cargo, Instance(high).GetString("holder"), "Virtual gamepad Submit traverses the mounted UI/owner command.");
            Assert.AreEqual(player, Instance(low).GetString("holder"));
            SelectInventory(InventoryPanel.PaneSelf, low);
            Boot.Ui.Inventory.SelfList.FocusRow(Boot.Ui.Inventory.SelfList.SelectedIndex);
            yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
            yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null;
            Assert.AreEqual(cargo, Instance(low).GetString("holder"), "Virtual keyboard Submit shares the exact instance command.");
            int highIndex = Boot.Ui.Inventory.GetPaneIds(InventoryPanel.PaneContainer).IndexOf("instance:" + high);
            Click(Boot.Ui.Inventory.ContainerList.RowAt(highIndex));
            Assert.AreEqual("instance:" + high, Boot.Ui.Inventory.GetSelectedIds(InventoryPanel.PaneContainer)[0]);
            yield return null; // Give the newly selected row's action button real layout before pointer hit/capture.
            var transfer = Boot.Ui.Inventory.ActionButtons.Single(button => button.text == "Transfer");
            ResolvePanelLayout(transfer);
            Vector2 pointerPosition = transfer.worldBound.center;
            using (PointerDownEvent evt = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = pointerPosition }))
            { evt.target = transfer; transfer.SendEvent(evt); }
            yield return null; // Real presses/releases span frames; retain the actual pressed button, never replace it with a new action.
            using (PointerUpEvent evt = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0, mousePosition = pointerPosition }))
            { evt.target = transfer; transfer.SendEvent(evt); }
            yield return null;
            Assert.AreEqual(player, Instance(high).GetString("holder"), "Synthetic pointer events dispatch the mounted Transfer button; no direct transfer API is used.");
            Assert.AreEqual(cargo, Instance(low).GetString("holder"));
            Assert.AreEqual(0.23, Instance(low).GetFloat("condition"), 1e-9);
            Assert.AreEqual(0.81, Instance(high).GetFloat("condition"), 1e-9);
        }
    }
}
