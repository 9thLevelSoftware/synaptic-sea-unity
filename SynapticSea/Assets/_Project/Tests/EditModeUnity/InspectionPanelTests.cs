using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.UI;
using UnityEngine.UIElements;

namespace SynapticSea.Tests.Unity
{
    public class InspectionPanelTests : UiTestBase
    {
        sealed class FakeWorld : IMarkerWorld
        {
            public readonly List<ShipMarker> Markers = new List<ShipMarker>();
            public IReadOnlyList<ShipMarker> MarkersInRange(double radius) => Markers;
            public Vec3 PlayerPosition => Vec3.Zero;
            public void SetPlayerPosition(Vec3 pos) { }
            public void MarkGenerated(string markerId) { }
        }

        // --- wounds ---

        [Test]
        public void WoundsRankCriticalFirstAndTreatThroughTheModel()
        {
            var wounds = new WoundState();
            wounds.ApplyWound(new GdDict { { "kind", "laceration" }, { "body_part", "arm" }, { "severity", 0.4 } });
            string burn = wounds.ApplyWound(new GdDict { { "kind", "burn" }, { "body_part", "torso" }, { "severity", 0.9 } });
            var panel = new WoundsPanel();
            var applied = new List<string>();
            panel.TreatmentApplied += (id, kind) => applied.Add(id + ":" + kind);
            panel.Bind(wounds);
            panel.Open();
            Assert.AreEqual(SurfacePanel.BadgeLiveText, panel.BadgeText);
            Assert.AreEqual(burn, panel.GetSelectedWoundId(), "critical condition first");
            Assert.AreEqual(Severity.Danger, panel.List.Items[0].Severity);
            StringAssert.Contains("Burn · torso", VisibleText(panel));
            StringAssert.Contains("⚠ Danger", panel.List.Items[0].Detail);

            Assert.IsTrue(panel.TreatSelected());
            CollectionAssert.AreEqual(new[] { burn + ":treat" }, applied);
            Assert.IsTrue(wounds.GetWound(burn).GetBool("treated"));
            Assert.AreEqual(burn, panel.GetSelectedWoundId(), "selection keeps the same wound after re-ranking");
            StringAssert.Contains("treated " + burn, panel.StatusDisplay);

            panel.MoveSelection(1);
            Assert.IsTrue(panel.BandageSelected());
            StringAssert.Contains("bandaged", panel.List.Items[panel.GetSelectedIndex()].Detail);
        }

        [Test]
        public void WoundsDenyWithoutAWound()
        {
            var audio = new FakeUiAudio();
            var panel = new WoundsPanel();
            panel.SetAudioManager(audio);
            panel.Bind(new WoundState());
            panel.Open();
            Assert.AreEqual("No active wounds.", panel.List.EmptyText);
            StringAssert.Contains("No active wounds.", VisibleText(panel));
            Assert.IsFalse(panel.TreatSelected());
            Assert.AreEqual("no wound", panel.GetStatus());
            StringAssert.StartsWith("▲ Caution: no wound", panel.StatusDisplay);
            Assert.AreEqual(1, audio.Sfx.Count);
        }

        // --- ship modification ---

        [Test]
        public void ShipModInstallUninstallAndDenyPaths()
        {
            var mod = new ShipModificationState();
            var panel = new ShipModificationPanel();
            var installs = new List<string>();
            var uninstalls = new List<string>();
            panel.InstallRequested += (slot, comp, form) => installs.Add(slot + "|" + comp + "|" + form);
            panel.UninstallRequested += (slot, comp, form) => uninstalls.Add(slot + "|" + comp + "|" + form);

            Assert.IsFalse(panel.InstallIntoSelected("console_unit", "console_unit"));
            Assert.AreEqual("no mod state", panel.GetStatus());

            panel.Bind(mod, new GdDict { { "console_unit", 1L } });
            panel.Open();
            Assert.IsFalse(panel.UninstallSelected());
            Assert.AreEqual("empty slot", panel.GetStatus());
            StringAssert.StartsWith("▲ Caution: empty slot", panel.StatusDisplay);

            Assert.IsTrue(panel.InstallFromInventory());
            CollectionAssert.AreEqual(new[] { "hub_slot_0|console_unit|console_unit" }, installs);
            Assert.AreEqual("installed console_unit -> hub_slot_0", panel.GetStatus());
            Assert.IsFalse(panel.GetInventoryBag().Has("console_unit"));
            StringAssert.Contains("console_unit", VisibleText(panel));

            Assert.IsFalse(panel.InstallFromInventory());
            Assert.AreEqual("empty inventory", panel.GetStatus());

            panel.SetInventory(new GdDict { { "plating_plate", 1L } });
            mod.PowerSupply = 6.0;
            panel.MoveSelection(1);
            Assert.IsFalse(panel.InstallIntoSelected("plating", "plating_plate", 5.0));
            Assert.AreEqual("install failed: power_budget", panel.GetStatus());
            Assert.IsFalse(panel.InstallIntoSelected("sensor", "sensor_rack", 0.5));
            Assert.AreEqual("install failed: missing_item", panel.GetStatus());

            panel.MoveSelection(-1);
            Assert.IsTrue(panel.UninstallSelected());
            CollectionAssert.AreEqual(new[] { "hub_slot_0|console_unit|console_unit" }, uninstalls);
            Assert.AreEqual(1, panel.GetInventoryBag().GetInt("console_unit"));

            panel.CandidateSlots = new List<string>();
            panel.Refresh();
            Assert.IsFalse(panel.InstallIntoSelected("console_unit", "console_unit"));
            Assert.AreEqual("no empty slot", panel.GetStatus());
        }

        [Test]
        public void ShipModShowsPowerOverBudgetWithWordingAndSymbol()
        {
            var mod = new ShipModificationState();
            mod.Install("hub_slot_0", "reactor", "reactor_console", new GdDict { { "reactor_console", 1L } }, 50.0);
            mod.PowerSupply = 10.0;
            var panel = new ShipModificationPanel();
            panel.Bind(mod);
            panel.Open();
            Assert.AreEqual(Meter.Severity.Danger, panel.PowerMeter.CurrentSeverity);
            StringAssert.StartsWith("⚠ Danger: power budget OVER", panel.SummaryText);
            StringAssert.Contains("OVER", panel.GetStatusLines()[0]);
        }

        // --- scanner / chart ---

        static FakeWorld TwoContacts()
        {
            var world = new FakeWorld();
            world.Markers.Add(new ShipMarker { MarkerId = "m_alpha", Position = new Vec3(120, 0, 0), SizeClass = 2, Condition = 1, ShipType = "freighter" });
            world.Markers.Add(new ShipMarker { MarkerId = "m_beta", Position = new Vec3(0, 0, 200), SizeClass = 1, Condition = 2, ShipType = "tug" });
            return world;
        }

        [Test]
        public void ScannerConfirmTravelsAndClosesKnownVsUnknownDetail()
        {
            FakeWorld world = TwoContacts();
            var chart = new WebChartState();
            string traveled = "";
            var host = new ScannerHost(new ScannerState(), world,
                () => new GdDict { { "navigation", true }, { "scanners", true } }, () => 4,
                m =>
                {
                    traveled = m.MarkerId;
                    return new GdDict { { "success", true }, { "reason", "" }, { "ship", null } };
                },
                chart, () => true);
            var events = new List<string>();
            host.TrainingEvent = (e, t) => events.Add(e);
            var panel = new ScannerPanel();
            bool closed = false;
            panel.PanelClosed += () => closed = true;
            panel.Bind(host);
            panel.Open();
            Assert.AreEqual("2 contact(s)", panel.GetStatus());
            CollectionAssert.AreEqual(new[] { "scan_derelict" }, events);
            Assert.AreEqual("m_alpha · d=120 · sz=2 · freighter · cond=1", panel.GetRowTexts()[0]);
            StringAssert.Contains("Ship type freighter", panel.DetailText);
            StringAssert.Contains("Predicted unknown (needs a stronger scan)", panel.DetailText, "detail 3 does not reveal predicted status");
            Assert.AreEqual(2, chart.GetKnownCount(), "a possessed chart records the scan");

            panel.MoveSelection(1);
            GdDict result = panel.ConfirmSelection();
            Assert.IsTrue(result.GetBool("success"));
            Assert.AreEqual("m_beta", traveled);
            Assert.IsTrue(closed);
            Assert.IsFalse(panel.IsOpen());
        }

        [Test]
        public void ScannerDenyPaths()
        {
            var audio = new FakeUiAudio();
            var offline = new ScannerHost(new ScannerState(), TwoContacts(),
                () => new GdDict { { "navigation", false } }, () => 0, m => null) { Audio = audio };
            var panel = new ScannerPanel();
            panel.Bind(offline);
            panel.Open();
            Assert.AreEqual("no signal", panel.GetStatus());
            StringAssert.StartsWith("▲ Caution: no signal", panel.StatusDisplay);
            GdDict denied = panel.ConfirmSelection();
            Assert.AreEqual("no_target", denied.GetString("reason"));
            Assert.AreEqual("no target", panel.GetStatus());
            CollectionAssert.Contains(audio.Sfx, AudioEventSeam.UI_PANEL_CLOSE);

            var rejecting = new ScannerHost(new ScannerState(), TwoContacts(),
                () => new GdDict { { "navigation", true } }, () => 0,
                m => new GdDict { { "success", false }, { "reason", "fuel_shortage" }, { "ship", null } });
            panel.Bind(rejecting);
            panel.Refresh();
            GdDict rejected = panel.ConfirmSelection();
            Assert.IsFalse(rejected.GetBool("success"));
            Assert.AreEqual("fuel_shortage", panel.GetStatus());
            Assert.IsTrue(panel.IsOpen(), "a rejected travel keeps the panel open");

            var unbound = new ScannerPanel();
            unbound.Open();
            Assert.AreEqual("no scanner", unbound.GetStatus());
        }

        [Test]
        public void ChartIsReadOnlyAndShowsRecordedMarkersAndRoute()
        {
            var chart = new WebChartState();
            var scan = new ScannerState().Scan(TwoContacts(), new GdDict { { "navigation", true }, { "scanners", true } }, 10);
            chart.RecordViews(scan.GetArrayOrEmpty("markers"), scan.GetInt("detail_level"));
            var panel = new ChartPanel();
            panel.Bind(chart);
            panel.Open();
            Assert.AreEqual("2 marker(s) recorded", panel.GetStatus());
            Assert.AreEqual(2, panel.List.Count);
            Assert.IsFalse(panel.Query<UnityEngine.UIElements.Button>().ToList().Any(b => b.text.Contains("Travel")), "no travel action on the chart");
            panel.SetRouteSummary(new GdDict { { "ok", false }, { "reason", "no_path" } });
            StringAssert.Contains("Route: unavailable (no_path)", panel.RouteText);
            panel.SetRouteSummary(new GdDict { { "ok", true }, { "path", GdArray.Of("hub", "m_alpha", "extraction") }, { "fuel", 2.0 }, { "food", 1.0 }, { "distance", 300.0 } });
            Assert.AreEqual("Route to extraction (2 hops)", panel.GetRouteLines()[0]);
            StringAssert.EndsWith("route ready", panel.GetStatus());
        }

        // --- recipes ---

        [Test]
        public void RecipePickerStartsOnReadyRecipeDeniesBlockedAndCrafts()
        {
            var inventory = new InventoryState();
            inventory.AddItem("scrap_metal", 2);
            inventory.AddItem("adhesive_paste", 1);
            var crafting = new CraftingState();
            var audio = new FakeUiAudio();
            var host = new CraftingStationHost(crafting, inventory, () => 0) { Audio = audio };
            var panel = new RecipePickerPanel();
            panel.Bind(host);
            panel.OpenForStation("workbench");
            Assert.AreEqual("CRAFT — workbench", panel.Title);
            Assert.AreEqual("weld_plating", panel.GetSelectedId(), "cursor starts on the first ready recipe");
            StringAssert.Contains("Status: Ready", panel.DetailText);
            StringAssert.Contains("scrap_metal ×2", panel.DetailText);

            int splice = panel.GetRowTexts().FindIndex(r => r.Contains("Splice") || r.Contains("splice"));
            Assert.GreaterOrEqual(splice, 0);
            panel.MoveSelection(splice - panel.GetSelectedIndex());
            GdDict blocked = panel.ConfirmSelection();
            Assert.IsFalse(blocked.GetBool("ok"));
            Assert.AreEqual("insufficient_skill", blocked.GetString("reason"));
            Assert.AreEqual("blocked: insufficient_skill", panel.GetStatus());
            StringAssert.StartsWith("▲ Caution: blocked", panel.StatusDisplay);
            CollectionAssert.Contains(audio.Sfx, AudioEventSeam.UI_PANEL_CLOSE);

            panel.Refresh();
            Assert.AreEqual("splice_circuit", panel.GetSelectedId(), "refresh keeps the same recipe under the cursor");
            panel.MoveSelection(panel.GetRowTexts().FindIndex(r => r.Contains("[ready]")) - panel.GetSelectedIndex());
            GdDict started = panel.ConfirmSelection();
            Assert.IsTrue(started.GetBool("ok"), started.GetString("reason"));
            Assert.IsFalse(panel.IsOpen());
            Assert.AreEqual(0, inventory.GetQuantity("scrap_metal"), "ingredients were consumed by the model");
            Assert.IsTrue(crafting.IsCrafting());
        }

        [Test]
        public void RecipePickerEmptyStation()
        {
            var panel = new RecipePickerPanel();
            panel.Bind(new CraftingStationHost(new CraftingState(), new InventoryState(), () => 0));
            panel.OpenForStation("no_such_station");
            Assert.AreEqual(0, panel.GetEntryCount());
            Assert.AreEqual("no_recipes", panel.ConfirmSelection().GetString("reason"));
            Assert.AreEqual("no recipes", panel.GetStatus());
        }
    }
}
