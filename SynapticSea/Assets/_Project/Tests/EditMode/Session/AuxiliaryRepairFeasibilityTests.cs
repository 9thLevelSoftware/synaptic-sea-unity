using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    // Engine-free actual service-work feasibility. Reachability and full survival are separate PlayMode gates.
    public class AuxiliaryRepairFeasibilityTests : InfraDataTestBase
    {
        internal static void FinishService(RunSession session, SessionHarness.Rig rig, string id)
        {
            var point = session.AuxiliaryServicePoints.Single(p => p.ServiceId == id);
            rig.Scene.PlayerPosition = point.GlobalPosition;
            var start = session.RequestAuxiliaryService(id); Assert.IsTrue(start.GetBool("committed"), start.GetString("detail") + start.GetString("reason"));
            Assert.IsTrue(session.BeginWorkHold());
            var tick = typeof(RunSession).GetMethod("TickWorkAction", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int step = 0; step < 2000 && session.GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetString("completion_commit_id") == ""; step++)
            {
                if (session.VitalsState.Stamina < 25)
                {
                    if (session.AuxiliaryWorkRunning) session.PauseAuxiliaryService("rest");
                    session.EndWorkHold();
                    for (int rest = 0; rest < 2000 && session.VitalsState.Stamina < 75; rest++) session.VitalsState.Tick(.1, new GdDict { { "moving", false } });
                    Assert.GreaterOrEqual(session.VitalsState.Stamina, 75, "Normal stationary recovery must support the work route.");
                    var resumed = session.RequestAuxiliaryService(id); Assert.IsTrue(resumed.GetBool("committed"), resumed.GetString("detail") + resumed.GetString("reason"));
                    Assert.IsTrue(session.BeginWorkHold());
                }
                double beforeProgress = session.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds");
                tick.Invoke(session, new object[] { .5 });
                var afterJob = session.GetAuxiliaryServiceState().GetDictOrEmpty("job");
                Assert.IsTrue(afterJob.GetString("status") == "completed" || afterJob.GetFloat("progress_seconds") > beforeProgress, "Eligible held work must publish progress: " + PaidSnapshotCodec.Stringify(afterJob));
            }
            Assert.IsNotEmpty(session.GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetString("completion_commit_id"), "Timed actual service must finish: " + PaidSnapshotCodec.Stringify(session.GetAuxiliaryServiceState().GetDictOrEmpty("job")));
            session.EndWorkHold();
        }
        [TestCase("engineer")][TestCase("mechanic")][TestCase("medic")]
        [TestCase("pilot")][TestCase("scientist")][TestCase("cook")]
        [TestCase("security")][TestCase("communications")][TestCase("salvage_captain")]
        [TestCase("field_medic")][TestCase("signal_specialist")]
        public void ActualFourUtilitiesPermitPaidDepartureRepairs(string classId)
        {
            var deps = SessionHarness.GoldenDeps(out var rig);
            SessionHarness.OverlayGamePlayability(deps);
            const string source = "res://data/diagnostics/earned-services-home-v1/";
            deps.LayoutPath = source + "layout.json"; deps.GameplaySlicePath = source + "gameplay_slice.json";
            deps.BlueprintPath = source + "blueprint.json"; deps.StartingClassId = classId;
            deps.EnablePaidCrafting = true; deps.EnableManualStudy = true; deps.EnableAuxiliaryServices = true;
            var session = RunSession.Create(deps);
            try
            {
                Assert.IsTrue(session.PlayableStarted, session.LastFailureReason);
                Assert.AreEqual(classId, session.PlayerProgression.GetClassId());
                var supply = session.LootContainers.Single(row => row.ContainerId == "start_supply_a");
                rig.Scene.PlayerPosition = supply.GlobalPosition;
                Assert.IsTrue(supply.TryInteract(supply.GlobalPosition));
                if (session.EquipmentState.GetEquipped("primary_hand") == "crowbar") Assert.AreEqual("crowbar", session.UnequipToInventory("primary_hand"));
                Assert.AreEqual(1, session.InventoryState.GetQuantity("crowbar"));
                Assert.AreEqual(1, session.InventoryState.GetQuantity("data_core"));
                Assert.AreEqual(3, session.InventoryState.GetQuantity("circuit_board"));
                Assert.IsTrue(session.CompleteAllObjectives(), "Real callbacks grant objective rewards and force their mapped repairs.");
                Assert.IsTrue(session.ShipSystemsManager.IsOperational("power"));
                // No battery or fire repair reward in this conservative contract frontier.
                var cargo = session.BreachSealPoints.Single(row => row.CompartmentId == "cargo" && !row.Sealed);
                long sealant = session.InventoryState.GetQuantity("hull_sealant");
                Assert.IsTrue(cargo.TryStart(cargo.GlobalPosition)); Assert.IsTrue(cargo.Channeling);
                cargo.AdvanceChannel(cargo.SealSeconds + 1);
                Assert.IsTrue(cargo.Sealed); Assert.AreEqual(sealant - 1, session.InventoryState.GetQuantity("hull_sealant"));
                var kit = session.LootContainers.Single(row => row.ContainerId == "home_service_kit_01");
                rig.Scene.PlayerPosition = kit.GlobalPosition; Assert.IsTrue(kit.TryInteract(kit.GlobalPosition));
                foreach (var point in session.AuxiliaryServicePoints.Where(p => p.ServiceKind == "utility"))
                {
                    FinishService(session, rig, point.ServiceId);
                    Assert.IsTrue(session.IsAuxiliaryHardwareReady(session.HomeShip.ShipId, point.ServiceId));
                }
                Assert.AreEqual(0, session.InventoryState.GetQuantity("scrap_metal"));
                Assert.AreEqual(0, session.InventoryState.GetQuantity("wiring_bundle"));
                Assert.IsTrue(DomainBundle.TryCreate(session.CapturePaidCraftingDomain(), out _, out string reason), reason);
                var charts = session.RepairPoints.Single(row => row.SystemId == "navigation" && row.SubcomponentId == "star_charts");
                Assert.IsTrue(charts.CanBeginRepair()); Assert.IsTrue(charts.TryStart(charts.GlobalPosition));
                Assert.IsTrue(charts.Channeling); charts.AdvanceChannel(charts.RepairSeconds + 1);
                Assert.IsTrue(charts.Repaired); Assert.AreEqual(0, session.InventoryState.GetQuantity("data_core"));
                Assert.GreaterOrEqual(session.PlayerProgression.GetSkillLevel("repair"), 2, "Actual earned utility budget meets actual opening gate.");
                var linkage = session.RepairPoints.Single(row => row.SystemId == "propulsion" && row.SubcomponentId == "nav_linkage");
                Assert.AreEqual(2, linkage.MinSkill); Assert.IsTrue(linkage.CanBeginRepair());
                long boards = session.InventoryState.GetQuantity("circuit_board");
                Assert.IsTrue(linkage.TryStart(linkage.GlobalPosition)); Assert.IsTrue(linkage.Channeling);
                linkage.AdvanceChannel(linkage.RepairSeconds + 1); Assert.IsTrue(linkage.Repaired);
                Assert.AreEqual(boards - 1, session.InventoryState.GetQuantity("circuit_board"));
                Assert.IsTrue(session.ShipSystemsManager.IsOperational("navigation"));
                Assert.IsTrue(session.ShipSystemsManager.IsOperational("propulsion"));
                Assert.IsFalse(session.ShipSystemsManager.IsOperational("scanners"), "Departure feasibility does not claim rank-three scanner repairs.");
            }
            finally { session.Dispose(); }
        }
    }
}
