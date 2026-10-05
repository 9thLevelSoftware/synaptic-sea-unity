using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    public class AuxiliaryConsumerTests : InfraDataTestBase
    {
        [Test]
        public void ObjectivePowerCannotRepairFeedOrRelayAndWorkbenchRemainsIndependent()
        {
            var deps = SessionHarness.GoldenDeps(out var rig);
            SessionHarness.OverlayGamePlayability(deps);
            const string path = "res://data/diagnostics/earned-services-home-v1/";
            deps.LayoutPath = path + "layout.json"; deps.GameplaySlicePath = path + "gameplay_slice.json";
            deps.BlueprintPath = path + "blueprint.json";
            deps.EnablePaidCrafting = true; deps.EnableManualStudy = true; deps.EnableAuxiliaryServices = true;
            var session = RunSession.Create(deps);
            try
            {
                Assert.IsTrue(session.PlayableStarted, session.LastFailureReason);
                Assert.IsFalse(session.CraftingState.GetStation("fabricator").Powered, "initial projections must apply the feed gate before the first tick");
                Assert.IsTrue(session.CompleteAllObjectives());
                session.Tick(TickContext.Frame(ShipRuntime.SLOW_INTERVAL_SECONDS, session.Scene.PlayerPosition, false));
                Assert.IsTrue(session.ShipSystemsManager.IsOperational("power"));
                Assert.IsTrue(session.CraftingState.GetStation("workbench").Powered);
                Assert.IsFalse(session.CraftingState.GetStation("fabricator").Powered, "ordinary objectives repair ship power, not auxiliary hardware");
                Assert.IsFalse(session.GetCargoBulkEligibility(session.HomeShip.ShipId).GetBool("ok"));
                session.InventoryState.AddItem("scrap_metal", 1);
                long before = session.InventoryState.GetQuantity("scrap_metal");
                Assert.AreEqual(0, session.CargoDeposit(session.HomeShip.ShipId));
                Assert.AreEqual(before, session.InventoryState.GetQuantity("scrap_metal"));
                foreach (string id in new[] { "medbay_task_light_01", "airlock_dock_beacon_01" })
                {
                    var effect = session.GetAuxiliaryUtilityEffects(session.HomeShip.ShipId, id);
                    Assert.IsFalse(effect.GetBool("hardware_ready"));
                    Assert.IsFalse(effect.GetBool("powered"));
                }
                Assert.AreEqual(6, session.AuxiliaryServicePoints.Count);
                // Provisioned consumer proof, not a physical acquisition or survival route.
                session.ThreatManager.Threats.Clear(); session.InventoryState.Items.Clear();
                session.InventoryState.AddItem("crowbar", 1);
                session.InventoryState.AddItem("scrap_metal", 4);
                session.InventoryState.AddItem("wiring_bundle", 4);
                var feed = session.AuxiliaryServicePoints.Single(point => point.ServiceId == "maintenance_fabricator_feed_01");
                rig.Scene.PlayerPosition = feed.GlobalPosition;
                var started = session.RequestAuxiliaryService(feed.ServiceId);
                Assert.IsTrue(started.GetBool("committed"), started.GetString("reason"));
                Assert.IsFalse(session.CraftingState.GetStation("fabricator").Powered, "aux_start projection cannot reset gated power");
                session.BeginWorkHold();
                typeof(RunSession).GetMethod("TickWorkAction", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(session, new object[] { .5 });
                Assert.Greater(session.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds"), 0);
                Assert.IsFalse(session.CraftingState.GetStation("fabricator").Powered, "aux_progress projection cannot reset gated power");
                session.EndWorkHold();
                Assert.IsFalse(session.AuxiliaryWorkRunning);
                Assert.IsFalse(session.CraftingState.GetStation("fabricator").Powered, "aux_pause projection cannot reset gated power");
                foreach (string id in new[] { "maintenance_fabricator_feed_01", "maintenance_cargo_relay_01", "medbay_task_light_01", "airlock_dock_beacon_01" })
                    AuxiliaryRepairFeasibilityTests.FinishService(session, rig, id);
                Assert.IsTrue(session.CraftingState.GetStation("fabricator").Powered, "committed feed repair refreshes the real station");
                Assert.IsTrue(session.CraftingState.GetStation("workbench").Powered);
                Assert.IsTrue(session.GetCargoBulkEligibility(session.HomeShip.ShipId).GetBool("ok"));
                session.InventoryState.AddItem("scrap_metal", 1);
                Assert.Greater(session.CargoDeposit(session.HomeShip.ShipId), 0, "committed relay enables actual bulk deposit");
                Assert.AreEqual(0, session.InventoryState.GetQuantity("scrap_metal"));
                foreach (string id in new[] { "medbay_task_light_01", "airlock_dock_beacon_01" })
                {
                    var effect = session.GetAuxiliaryUtilityEffects(session.HomeShip.ShipId, id);
                    Assert.IsTrue(effect.GetBool("hardware_ready"));
                    Assert.IsTrue(effect.GetBool("powered"));
                }
                var beacon = session.GetAuxiliaryUtilityEffects(session.HomeShip.ShipId, "airlock_dock_beacon_01");
                Assert.AreEqual(session.HomeShip.DockedShips.Count + (session.HomeShip.ParentShip == null ? 0 : 1), beacon.GetInt("dock_connection_count"));
                StringAssert.Contains("airlock " + beacon.GetString("airlock_state"), beacon.GetString("status"));
                session.PowerGridState.Rebalance(0);
                Assert.IsFalse(session.GetCargoBulkEligibility(session.HomeShip.ShipId).GetBool("ok"), "repaired relay still requires current power");
                foreach (string id in new[] { "medbay_task_light_01", "airlock_dock_beacon_01" })
                {
                    var effect = session.GetAuxiliaryUtilityEffects(session.HomeShip.ShipId, id);
                    Assert.IsTrue(effect.GetBool("hardware_ready"));
                    Assert.IsFalse(effect.GetBool("powered"));
                }
            }
            finally { session.Dispose(); }
        }
    }
}
