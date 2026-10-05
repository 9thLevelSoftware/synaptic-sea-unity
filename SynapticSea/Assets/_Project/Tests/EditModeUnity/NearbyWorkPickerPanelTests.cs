using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.UI;
using UnityEngine.UIElements;

namespace SynapticSea.Tests.Unity
{
    public class NearbyWorkPickerPanelTests : UiTestBase
    {
        static GdDict Describe(SessionInteractable target) => new GdDict {
            { "id", target.NodeName }, { "label", target.NodeName }, { "action", "repair" },
            { "reason", "insufficient_skill" }, { "status", "blocked" },
            { "requirements", new GdDict { { "skill_id", "repair" }, { "min_skill", 3L } } } };

        [Test]
        public void NavigationSelectsBlockedIdentityWithoutFallbackThenCancelReopenDoesNotStart()
        {
            var repair = new RepairPoint { NodeName = "gravity.field_emitter" };
            var seal = new BreachSealPoint { NodeName = "cargo" };
            int requests = 0; SessionInteractable requested = null;
            var panel = new NearbyWorkPickerPanel {
                ListTargets = () => new SessionInteractable[] { repair, seal }, DescribeTarget = Describe,
                RequestTarget = target => { requests++; requested = target; return new GdDict { { "started", false }, { "reason", "insufficient_skill" } }; } };
            using (var harness = new UiHarness())
            {
                harness.Mount(panel); panel.Open(); harness.Layout(); panel.RestoreFocus("");
                Assert.AreSame(panel.List.RowAt(0), harness.Focused, "actual modal entry focuses the selected work row");
                Assert.AreEqual(0, requests, "opening cannot confirm");
                UiHarness.Submit(panel.List.RowAt(0));
                Assert.AreEqual(1, requests); Assert.AreSame(repair, requested);
                StringAssert.Contains("insufficient_skill", panel.StatusDisplay);
                Assert.IsTrue(panel.IsOpen());
                UiHarness.Cancel(panel);
                Assert.IsFalse(panel.IsOpen()); panel.Open(); Assert.AreEqual(1, requests);
                panel.Consume(UiCommand.Down); Assert.AreSame(seal, panel.SelectedTarget);
                Assert.AreEqual(1, requests, "navigation never starts work");
            }
        }

        [Test]
        public void SuccessfulSubmitClosesAndRepeatedSubmitCannotRequestAgain()
        {
            var seal = new BreachSealPoint { NodeName = "cargo" }; int requests = 0;
            var panel = new NearbyWorkPickerPanel {
                ListTargets = () => new SessionInteractable[] { seal }, DescribeTarget = Describe,
                RequestTarget = target => { Assert.AreSame(seal, target); requests++; return new GdDict { { "started", true } }; } };
            using (var harness = new UiHarness())
            {
                harness.Mount(panel); panel.Open(); harness.Layout(); panel.RestoreFocus("");
                var row = panel.List.RowAt(0);
                Assert.AreSame(row, harness.Focused);
                UiHarness.Submit(row);
                Assert.IsFalse(panel.IsOpen()); Assert.AreEqual(1, requests);
                UiHarness.Submit(row);
                panel.ConfirmSelection(); Assert.AreEqual(1, requests, "closed picker cannot duplicate a start");
            }
        }

        [Test]
        public void WorkbenchSubmitClosesBeforeRecipeHandoffWithoutStartingCraftOrDuplicating()
        {
            var station = new CraftingStation { NodeName = "workbench", StationKind = "workbench" };
            int requests = 0, recipes = 0;
            var panel = new NearbyWorkPickerPanel {
                ListTargets = () => new SessionInteractable[] { station }, DescribeTarget = target => new GdDict {
                    { "label", "Use workbench" }, { "station_kind", "workbench" },
                    { "requirements", new GdDict { { "effective_tier", 3L } } },
                    { "status", "ready" }, { "reason", "ready" } },
                RequestTarget = target => { Assert.AreSame(station, target); requests++;
                    return new GdDict { { "opened", true }, { "started", false }, { "station_kind", "workbench" } }; } };
            panel.WorkResolved += result => {
                Assert.IsFalse(panel.IsOpen(), "chooser closes before normal recipe modal handoff");
                Assert.IsTrue(result.GetBool("opened")); Assert.IsFalse(result.GetBool("started")); recipes++;
            };
            using (var harness = new UiHarness())
            {
                harness.Mount(panel); panel.Open(); harness.Layout();
                Assert.AreEqual(0, requests, "opening does not use a station");
                StringAssert.Contains("separate confirmation", panel.DetailText);
                StringAssert.Contains("Station tier: 3", panel.DetailText);
                var row = panel.List.RowAt(0);
                using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = row; row.SendEvent(submit); }
                Assert.AreEqual(1, requests); Assert.AreEqual(1, recipes); Assert.IsFalse(panel.IsOpen());
                using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = row; row.SendEvent(submit); }
                panel.ConfirmSelection(); Assert.AreEqual(1, requests); Assert.AreEqual(1, recipes);
            }
        }

        [Test]
        public void WorkbenchCoveredCancelAndReplacementNeverOpenRecipes()
        {
            var station = new CraftingStation { NodeName = "workbench", StationKind = "workbench" };
            IReadOnlyList<SessionInteractable> live = new SessionInteractable[] { station }; int requests = 0;
            var panel = new NearbyWorkPickerPanel { ListTargets = () => live, DescribeTarget = Describe,
                RequestTarget = target => { requests++; return new GdDict { { "opened", true } }; } };
            using (var harness = new UiHarness())
            {
                harness.Mount(panel); panel.Open(); harness.Layout(); panel.SetCovered(true);
                panel.Consume(UiCommand.Accept); Assert.AreEqual(0, requests);
                panel.SetCovered(false); panel.Consume(UiCommand.Cancel); Assert.IsFalse(panel.IsOpen());
                panel.Open(); Assert.AreEqual(0, requests);
                live = new SessionInteractable[] { new CraftingStation { NodeName = "workbench", StationKind = "workbench" } };
                panel.Refresh(); Assert.IsFalse(panel.IsOpen()); panel.ConfirmSelection(); Assert.AreEqual(0, requests);
            }
        }

        [Test]
        public void LiveReorderPreservesSelectedReferenceThroughRealNavigationSubmit()
        {
            var repair = new RepairPoint { NodeName = "gravity" };
            var seal = new BreachSealPoint { NodeName = "cargo" };
            IReadOnlyList<SessionInteractable> live = new SessionInteractable[] { repair, seal };
            SessionInteractable requested = null;
            var panel = new NearbyWorkPickerPanel { ListTargets = () => live, DescribeTarget = Describe,
                RequestTarget = target => { requested = target; return new GdDict { { "started", false } }; } };
            using (var harness = new UiHarness())
            {
                harness.Mount(panel); panel.Open(); harness.Layout(); panel.MoveSelection(1);
                panel.List.FocusRow(1); Assert.AreSame(seal, panel.SelectedTarget);
                live = new SessionInteractable[] { seal, repair }; panel.Refresh(); harness.Layout();
                Assert.AreSame(seal, panel.SelectedTarget);
                Assert.AreEqual(0, panel.List.SelectedIndex);
                UiHarness.Submit(panel.List.RowAt(0));
                Assert.AreSame(seal, requested, "LIVE refresh never retargets the deliberate choice");
            }
        }

        [Test]
        public void CoveredPickerRefusesDirectAndToolkitConfirmation()
        {
            var seal = new BreachSealPoint { NodeName = "cargo" }; int requests = 0;
            var panel = new NearbyWorkPickerPanel { ListTargets = () => new SessionInteractable[] { seal }, DescribeTarget = Describe,
                RequestTarget = target => { requests++; return new GdDict { { "started", true } }; } };
            using (var harness = new UiHarness())
            {
                harness.Mount(panel); panel.Open(); harness.Layout(); panel.SetCovered(true);
                panel.ConfirmSelection();
                UiHarness.Submit(panel.List.RowAt(0));
                Assert.AreEqual(0, requests); Assert.IsTrue(panel.IsOpen());
                panel.SetCovered(false); panel.Consume(UiCommand.Accept);
                Assert.AreEqual(1, requests); Assert.IsFalse(panel.IsOpen());
            }
        }

        [Test]
        public void RebuiltOrMovedSelectionClosesRatherThanReplacingWithSameNamedTarget()
        {
            var old = new BreachSealPoint { NodeName = "cargo" };
            IReadOnlyList<SessionInteractable> live = new SessionInteractable[] { old }; int requests = 0;
            var panel = new NearbyWorkPickerPanel { ListTargets = () => live, DescribeTarget = Describe,
                RequestTarget = target => { requests++; return new GdDict(); } };
            panel.Open(); live = new SessionInteractable[] { new BreachSealPoint { NodeName = "cargo" } };
            panel.Refresh(); Assert.IsFalse(panel.IsOpen()); panel.ConfirmSelection(); Assert.AreEqual(0, requests);
            panel.Open(); live = System.Array.Empty<SessionInteractable>();
            panel.Refresh(); Assert.IsFalse(panel.IsOpen()); Assert.AreEqual(0, requests);
        }
    }
}
