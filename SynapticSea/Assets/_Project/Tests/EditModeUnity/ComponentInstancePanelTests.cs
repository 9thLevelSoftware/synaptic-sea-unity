using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace SynapticSea.Tests.Unity
{
    /// <summary>Live component admission regressions. No tool/source acquisition is claimed by these UI fixtures.</summary>
    public sealed class ComponentInstancePanelTests : UiTestBase
    {
        [Test]
        public void SameFormSelectionRetainsChosenInstanceWhenRowsReorder()
        {
            var selection = new InventorySelectionModel();
            selection.SetIds(GdArray.Of("instance:home:reactor_low", "instance:home:reactor_high"));
            selection.SelectSingle(1);

            selection.SetIds(GdArray.Of("instance:home:reactor_high", "instance:home:reactor_low"));

            CollectionAssert.AreEqual(new object[] { "instance:home:reactor_high" }, selection.GetSelectedIds(),
                "the chosen 0.81 console remains chosen when its 0.23 same-form sibling moves");
            Assert.IsTrue(selection.IsSelected(0), "the selected ID is remapped to its new row index");
        }

        [Test]
        public void RangeAnchorFollowsInstanceIdentityThroughReorder()
        {
            var selection = new InventorySelectionModel();
            selection.SetIds(GdArray.Of("instance:home:a", "instance:home:b", "instance:home:c"));
            selection.SelectSingle(1);

            selection.SetIds(GdArray.Of("instance:home:b", "instance:home:c", "instance:home:a"));
            selection.SelectRangeTo(1);

            CollectionAssert.AreEqual(new object[] { "instance:home:b", "instance:home:c" }, selection.GetSelectedIds(),
                "Shift range uses the retained anchor instance's new position");
        }

        [Test]
        public void RemovedSelectedInstanceDoesNotSelectTheReplacementAtItsOldIndex()
        {
            var selection = new InventorySelectionModel();
            selection.SetIds(GdArray.Of("instance:home:reactor_low", "instance:home:reactor_high"));
            selection.SelectSingle(1);

            selection.SetIds(GdArray.Of("instance:home:reactor_low", "stack:scrap_metal"));

            Assert.IsTrue(selection.GetSelectedIds().IsEmpty, "a vanished instance cannot silently become a fungible stack");
        }

        [Test]
        public void ComponentIntegrationRequiresAnExplicitLaunchOptIn()
        {
            var field = typeof(RunLaunchRequest).GetField("EnableComponentIntegration");
            Assert.IsNotNull(field, "the diagnostic launch must carry an explicit opt-in flag through scene boot");
            Assert.AreEqual(typeof(bool), field.FieldType);
            Assert.IsFalse((bool)field.GetValue(RunLaunchRequest.NewRun()), "ordinary New Run retains legacy behavior");
            Assert.IsFalse((bool)field.GetValue(RunLaunchRequest.ContinueWorld()), "ordinary Continue retains legacy behavior");
        }

        [Test]
        public void DiagnosticPanelsExposeAnExplicitComponentHostBinding()
        {
            Assert.IsNotNull(typeof(InventoryPanel).GetMethod("BindComponents"),
                "diagnostic inventory commands need an explicit read/command host rather than aggregate model mutations");
            Assert.IsNotNull(typeof(ShipModificationPanel).GetMethod("BindComponents"),
                "diagnostic ship modification must bind actual targets and chosen instances separately from legacy fixtures");
        }

        [Test]
        public void DiagnosticSaveScreenExposesFullSaveAndExactSelectionBindings()
        {
            var fullSave = typeof(SaveSlotScreenModel).GetField("FullSave");
            Assert.IsNotNull(fullSave, "diagnostic manual saves must request one full generation capture");
            Assert.AreEqual(typeof(Func<string, string, string, bool>), fullSave.FieldType);
            var selection = typeof(SaveSlotScreenModel).GetProperty("LastLoadedSelection");
            Assert.IsNotNull(selection, "the diagnostic load result must retain its validated exact generation handle");
            Assert.AreEqual(typeof(GdDict), selection.PropertyType);
        }

        [Test]
        public void DiagnosticInventoryDisplaysSeparateConditionMassAndHolderRows()
        {
            var host = new ComponentHost();
            var player = new InventoryState();
            player.Items["reactor_console"] = 2L; // Deliberately invalid aggregate evidence; UI may not offer it as equipment.
            player.AddItem("scrap_metal", 4);
            var panel = new InventoryPanel();
            panel.BindComponents(host);
            panel.OpenSelf(player, EquipmentState.Create());

            CollectionAssert.AreEquivalent(new[] { "instance:home:low", "instance:home:high", "instance:home:unknown", "stack:scrap_metal" }, panel.GetPaneIds(InventoryPanel.PaneSelf));
            Assert.IsFalse(panel.SelfList.Items.Any(row => row.Id == "stack:reactor_console"));
            StringAssert.Contains("23%", VisibleText(panel));
            StringAssert.Contains("81%", VisibleText(panel));
            StringAssert.Contains("Unknown", VisibleText(panel));
            StringAssert.Contains("13.0 kg", VisibleText(panel));
            StringAssert.Contains(ComponentHost.Player, VisibleText(panel));
            StringAssert.StartsWith("DIAGNOSTIC", panel.Title);
        }

        [Test]
        public void DiagnosticUniqueRowsCannotSplitOrEquipAndDoNotMutatePassedModels()
        {
            var host = new ComponentHost();
            var player = new InventoryState();
            var panel = new InventoryPanel();
            panel.BindComponents(host, ComponentHost.Cargo);
            panel.OpenTransfer(player, ShipInventory.Create(), "HOLD", EquipmentState.Create());
            int high = panel.GetPaneIds(InventoryPanel.PaneSelf).IndexOf("instance:home:high");
            CollectionAssert.DoesNotContain(panel.ContextActionsFor(InventoryPanel.PaneSelf, high), "split");
            CollectionAssert.DoesNotContain(panel.ContextActionsFor(InventoryPanel.PaneSelf, high), "equip");
            GdDict before = player.GetSummary();
            panel.OpenSplitPicker(InventoryPanel.PaneSelf, "instance:home:high");
            Assert.IsFalse(panel.IsSplitOpen);
            Assert.AreEqual(0, panel.TransferQuantity(InventoryPanel.PaneSelf, "instance:home:high", 1));
            Assert.IsFalse(panel.EquipFromContainer("instance:home:high"));
            Assert.IsTrue(V.VariantEquals(before, player.GetSummary()));
            Assert.IsEmpty(host.Commands);
        }

        [Test]
        public void DiagnosticDragUsesCapturedInstanceAfterSelectionChangesAndRowsInsert()
        {
            var host = new ComponentHost();
            var panel = new InventoryPanel();
            var player = new InventoryState();
            var hold = ShipInventory.Create();
            panel.BindComponents(host, ComponentHost.Cargo);
            panel.OpenTransfer(player, hold, "HOLD", EquipmentState.Create());
            GdDict playerBefore = player.GetSummary(), holdBefore = hold.GetSummary();
            int high = panel.GetPaneIds(InventoryPanel.PaneSelf).IndexOf("instance:home:high");
            GdDict drag = panel.RowDragPayload(InventoryPanel.PaneSelf, high);
            host.Add("home:aaa", 0.5);
            panel.RefreshComponents();
            panel.SelectRow(InventoryPanel.PaneSelf, panel.GetPaneIds(InventoryPanel.PaneSelf).IndexOf("instance:home:low"), false, false);

            panel.ZoneDrop(InventoryPanel.PaneContainer, drag);

            CollectionAssert.AreEqual(new[] { "transfer:home:high:" + ComponentHost.Cargo }, host.Commands);
            Assert.AreEqual(ComponentHost.Player, host.Row("home:low").GetString("holder"));
            Assert.AreEqual(ComponentHost.Cargo, host.Row("home:high").GetString("holder"));
            Assert.IsTrue(V.VariantEquals(playerBefore, player.GetSummary()));
            Assert.IsTrue(V.VariantEquals(holdBefore, hold.GetSummary()), "only the command host publishes component state");
        }

        [UnityTest]
        public IEnumerator DiagnosticSubmitTransfersTheChosenInstanceAndPreservesFocusThroughReorder()
        {
            using (var harness = new UiHarness())
            {
                yield return null;
                var host = new ComponentHost();
                var panel = new InventoryPanel();
                panel.BindComponents(host, ComponentHost.Cargo);
                harness.Mount(panel);
                panel.OpenTransfer(new InventoryState(), ShipInventory.Create(), "HOLD", EquipmentState.Create());
                int high = panel.GetPaneIds(InventoryPanel.PaneSelf).IndexOf("instance:home:high");
                panel.SelectRow(InventoryPanel.PaneSelf, high, false, false);
                panel.SelfList.FocusRow(high);
                host.Add("home:aaa", 0.5);
                panel.RefreshComponents();
                harness.Layout();
                int retained = panel.GetPaneIds(InventoryPanel.PaneSelf).IndexOf("instance:home:high");
                Assert.AreEqual(panel.SelfList.RowAt(retained), harness.Focused);
                CollectionAssert.AreEqual(new object[] { "instance:home:high" }, panel.GetSelectedIds(InventoryPanel.PaneSelf));
                UiHarness.Submit(harness.Focused);
                CollectionAssert.AreEqual(new[] { "transfer:home:high:" + ComponentHost.Cargo }, host.Commands);
            }
        }

        [Test]
        public void DiagnosticShipModUsesChosenInstanceAndActualTargetAndReportsPendingWork()
        {
            var host = new ComponentHost();
            var legacy = new ShipModificationState();
            var panel = new ShipModificationPanel();
            panel.Bind(legacy, new GdDict { { "reactor_console", 2L } });
            panel.BindComponents(host);
            panel.OpenForComponent("home:high");
            Assert.IsTrue(panel.SelectTarget("home", ComponentHost.GoodSlot));
            GdDict before = legacy.GetSummary(), beforeBag = panel.GetInventoryBag();

            Assert.IsTrue(panel.InstallSelectedComponent());

            CollectionAssert.AreEqual(new[] { "install:home:high:home:" + ComponentHost.GoodSlot }, host.Commands);
            Assert.IsFalse(panel.LastCommandResult.GetBool("committed"));
            Assert.AreEqual("started", panel.LastCommandResult.GetString("reason"));
            StringAssert.Contains("Work started", panel.GetStatus());
            Assert.IsTrue(V.VariantEquals(before, legacy.GetSummary()));
            Assert.IsTrue(V.VariantEquals(beforeBag, panel.GetInventoryBag()));
            Assert.IsFalse(panel.List.Items.Any(row => row.Id.Contains("hub_slot")));
        }

        [Test]
        public void DiagnosticUnknownAndIncompatibleTargetsRefuseWithoutChoosingAnotherSlot()
        {
            var host = new ComponentHost();
            var panel = new ShipModificationPanel();
            panel.BindComponents(host);
            panel.OpenForComponent("home:unknown");
            panel.SelectTarget("home", ComponentHost.GoodSlot);
            Assert.IsFalse(panel.InstallSelectedComponent());
            Assert.AreEqual("condition_unknown", panel.LastCommandResult.GetString("reason"));
            Assert.IsNull(host.Row("home:unknown").Get("condition"));

            panel.SelectComponent("home:high");
            panel.SelectTarget("home", ComponentHost.BadSlot);
            Assert.IsFalse(panel.InstallSelectedComponent());
            Assert.AreEqual("form_incompatible", panel.LastCommandResult.GetString("reason"));
            Assert.AreEqual(ComponentHost.BadSlot, panel.GetSelectedSlotId());
            StringAssert.Contains("form_incompatible", VisibleText(panel));
            Assert.AreEqual(ComponentHost.Player, host.Row("home:high").GetString("holder"));
        }

        [Test]
        public void DiagnosticOpenMissingPhysicalTargetCannotActivateOrRemoveTheFirstVisibleMount()
        {
            var host = new ComponentHost { OccupiedGoodSlot = true };
            var panel = new ShipModificationPanel();
            panel.BindComponents(host);
            panel.OpenForTarget("home", "missing-physical-target");

            panel.Consume(UiCommand.Accept);

            Assert.IsEmpty(host.Commands, "Opening a vanished named physical target must not activate/uninstall the first unrelated displayed mount.");
            Assert.IsFalse(panel.UninstallSelected(), "The explicit missing target remains a refusal until the player selects a real target.");
            Assert.IsEmpty(host.Commands);
            Assert.AreEqual("", panel.GetSelectedSlotId());
        }

        [Test]
        public void DiagnosticOpenMissingComponentCannotInstallThePreviouslyChosenSiblingInstance()
        {
            var host = new ComponentHost();
            var panel = new ShipModificationPanel();
            panel.BindComponents(host);
            panel.OpenForComponent("home:high");
            panel.SelectTarget("home", ComponentHost.GoodSlot);
            panel.Close();
            panel.OpenForComponent("home:missing-instance");
            panel.SelectTarget("home", ComponentHost.GoodSlot);

            Assert.IsFalse(panel.InstallSelectedComponent(), "Opening a vanished named instance must not reuse the prior high-condition sibling.");
            Assert.IsEmpty(host.Commands);
            Assert.AreNotEqual("home:high", panel.GetSelectedInstanceId());
            Assert.AreEqual(ComponentHost.Player, host.Row("home:high").GetString("holder"));
        }

        [UnityTest]
        public IEnumerator DiagnosticTargetNavigationSubmitAndPointerSelectionShareExactCommand()
        {
            using (var harness = new UiHarness())
            {
                yield return null;
                var host = new ComponentHost();
                var panel = new ShipModificationPanel();
                panel.BindComponents(host);
                harness.Mount(panel);
                panel.OpenForComponent("home:low");
                panel.SelectTarget("home", ComponentHost.GoodSlot);
                harness.Layout();
                UiHarness.Submit(panel.List.RowAt(panel.GetSelectedIndex()));
                CollectionAssert.AreEqual(new[] { "install:home:low:home:" + ComponentHost.GoodSlot }, host.Commands);
                host.Commands.Clear();
                panel.SelectComponent("home:high");
                panel.SelectTarget("home", ComponentHost.GoodSlot);
                Assert.IsTrue(panel.InstallFromInventory());
                CollectionAssert.AreEqual(new[] { "install:home:high:home:" + ComponentHost.GoodSlot }, host.Commands,
                    "button-compatible entry uses the same exact-instance host command");
            }
        }

        [Test]
        public void DiagnosticFullSaveRefusalNeverFallsBackToPartialSnapshotAndExplainsQueuedCrafting()
        {
            var model = new SaveSlotScreenModel(new SaveLoadMenu());
            int fullSaves = 0;
            model.FullSave = (_, __, ___) => { fullSaves++; return false; };
            model.SaveFailureReason = () => "unverified_craft_job";
            model.SnapshotBuilder = () => { Assert.Fail("diagnostic refusal cannot fall back to legacy snapshot save"); return null; };
            var screen = new SaveLoadScreen(model);

            Assert.IsFalse(screen.ConfirmVerb(SaveSlotScreenModel.VerbSave).GetBool("ok"));

            Assert.AreEqual(1, fullSaves);
            StringAssert.Contains("active and queued crafting", screen.StatusDisplay);
            StringAssert.Contains("earlier save is preserved", screen.StatusDisplay);
            model.SaveFailureReason = () => "slot_deletion_reconciliation_needed";
            Assert.IsFalse(screen.ConfirmVerb(SaveSlotScreenModel.VerbSave).GetBool("ok"));
            StringAssert.Contains("save was written", screen.StatusDisplay);
            StringAssert.Contains("still hidden", screen.StatusDisplay);
            StringAssert.Contains("Retry saving", screen.StatusDisplay);
            StringAssert.DoesNotContain("earlier save is preserved", screen.StatusDisplay);
        }

        [Test]
        public void DiagnosticLoadRetainsOwnedExactHandleAfterCallerMutation()
        {
            var menu = new SaveLoadMenu();
            menu.Bind(new SavedSlotService());
            var selection = new GdDict { { "ok", true }, { "slot_id", "slot_01" }, { "generation_id", "older" }, { "payloads", new GdDict { { "world_text", "older world" } } } };
            var model = new SaveSlotScreenModel(menu) { SelectGeneration = _ => selection };
            Assert.IsTrue(model.ConfirmVerb(SaveSlotScreenModel.VerbLoad).GetBool("ok"));
            selection["generation_id"] = "newer";
            selection.GetDictOrEmpty("payloads")["world_text"] = "newer world";
            Assert.AreEqual("older", model.LastLoadedSelection.GetString("generation_id"));
            Assert.AreEqual("older world", model.LastLoadedSelection.GetDictOrEmpty("payloads").GetString("world_text"));
            Assert.IsNull(model.LastLoadedSnapshot, "diagnostic load never emits a partial snapshot");
        }

        [UnityTest]
        public IEnumerator DiagnosticLegacyRefusalOffersAnActualOriginalSaveAction()
        {
            using (var harness = new UiHarness())
            {
                yield return null;
                var menu = new SaveLoadMenu();
                menu.Bind(new SavedSlotService());
                var model = new SaveSlotScreenModel(menu) { SelectGeneration = _ => new GdDict { { "ok", false }, { "reason", "legacy_component_conversion_unavailable" } } };
                var screen = new SaveLoadScreen(model);
                harness.Mount(screen);
                string requested = "";
                screen.OpenOriginalSaveRequested += slot => requested = slot;
                screen.ConfirmVerb(SaveSlotScreenModel.VerbLoad);
                harness.Layout();
                Button original = screen.VerbButtons.Single(button => button.text == "Open original save");
                UiHarness.Submit(original);
                Assert.AreEqual("slot_01", requested);
                Assert.IsNull(model.LastLoadedSelection);
                StringAssert.Contains("original save", screen.StatusDisplay);
            }
        }

        sealed class SavedSlotService : ISaveSlotService
        {
            public List<SaveSlotState> ListSlots() => new List<SaveSlotState> { new SaveSlotState { SlotId = "slot_01", SlotKind = "manual", SavedAtEpoch = 1, SchemaVersion = "legacy" } };
            public RunSnapshot LoadFromSlot(string slotId) => null;
            public bool SaveToSlot(string slotId, RunSnapshot snapshot, string slotKind, bool isQuicksave, string displayName) => false;
            public bool DeleteSlot(string slotId) => false;
        }

        sealed class ComponentHost : IComponentInteractionHost
        {
            public const string Player = "player:player_local", Cargo = "ship_cargo:home", GoodSlot = "maintenance|wall|1", BadSlot = "maintenance|center|2";
            public readonly List<string> Commands = new List<string>();
            public bool OccupiedGoodSlot;
            readonly GdArray _rows = new GdArray();
            public ComponentHost() { Add("home:low", 0.23); Add("home:high", 0.81); Add("home:unknown", null); }
            public void Add(string id, double? condition) => _rows.Add(new GdDict { { "instance_id", id }, { "definition_id", "reactor_console" }, { "item_form", "reactor_console" },
                { "condition_state", condition.HasValue ? "known" : "unknown" }, { "condition", condition.HasValue ? (object)condition.Value : null }, { "mass", 13.0 }, { "holder", Player }, { "revision", 0L } });
            public GdDict Row(string id) => _rows.OfType<GdDict>().Single(row => row.GetString("instance_id") == id);
            public GdArray ListComponentInstances(string holderId) => new GdArray(_rows.OfType<GdDict>().Where(row => row.GetString("holder") == holderId).Select(row => row.DeepCopy()));
            public GdArray ListInstallTargets(string instanceId) => GdArray.Of(Target(GoodSlot, instanceId, false), Target(BadSlot, instanceId, true));
            GdDict Target(string slot, string instanceId, bool incompatible) => new GdDict { { "ship_id", "home" }, { "slot_id", slot }, { "holder_id", "slot:home:" + slot },
                { "occupied", OccupiedGoodSlot && slot == GoodSlot }, { "instance_id", OccupiedGoodSlot && slot == GoodSlot ? "home:low" : "" }, { "ok", instanceId.Length > 0 && !incompatible && Row(instanceId).GetString("condition_state") == "known" },
                { "reason", instanceId.Length == 0 ? "selection_required" : incompatible ? "form_incompatible" : Row(instanceId).GetString("condition_state") == "unknown" ? "condition_unknown" : "ok" } };
            public GdDict GetComponentHolderIds() => new GdDict { { "player", Player }, { "ship_cargo", Cargo }, { "cart", "" } };
            public GdDict GetComponentWorkState() => new GdDict { { "status", "idle" } };
            public GdDict RequestComponentRemoval(string instanceId, string destinationHolderId = null)
            {
                Commands.Add("remove:" + instanceId + ":" + (destinationHolderId ?? Player));
                return Pending();
            }
            public GdDict RequestComponentTransfer(string instanceId, string destinationHolderId)
            {
                Commands.Add("transfer:" + instanceId + ":" + destinationHolderId);
                Row(instanceId)["holder"] = destinationHolderId;
                return new GdDict { { "ok", true }, { "committed", true }, { "reason", "committed" } };
            }
            public GdDict RequestComponentInstall(string instanceId, string shipId, string slotId)
            {
                Commands.Add("install:" + instanceId + ":" + shipId + ":" + slotId);
                if (Row(instanceId).GetString("condition_state") == "unknown") return Refused("condition_unknown");
                if (slotId == BadSlot) return Refused("form_incompatible");
                return Pending();
            }
            static GdDict Pending() => new GdDict { { "ok", true }, { "committed", false }, { "reason", "started" }, { "job_id", "test-job" } };
            static GdDict Refused(string reason) => new GdDict { { "ok", false }, { "committed", false }, { "reason", reason } };
        }
    }
}
