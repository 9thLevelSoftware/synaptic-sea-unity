using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
    public class AuxiliaryServiceSessionTests : InfraDataTestBase
    {
        static RunSession Boot(out SessionHarness.Rig rig, out RunSessionDeps deps)
        {
            deps = SessionHarness.GoldenDeps(out rig); SessionHarness.OverlayGamePlayability(deps);
            const string path = "res://data/diagnostics/earned-services-home-v1/";
            deps.LayoutPath = path + "layout.json"; deps.GameplaySlicePath = path + "gameplay_slice.json"; deps.BlueprintPath = path + "blueprint.json";
            deps.EnablePaidCrafting = true; deps.EnableManualStudy = true; deps.EnableAuxiliaryServices = true;
            var s = RunSession.Create(deps); Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            var supply = s.LootContainers.Single(p => p.ContainerId == "start_supply_a"); rig.Scene.PlayerPosition = supply.GlobalPosition;
            Assert.IsTrue(supply.TryInteract(supply.GlobalPosition));
            if (s.EquipmentState.GetEquipped("primary_hand") == "crowbar") Assert.AreEqual("crowbar", s.UnequipToInventory("primary_hand"));
            return s;
        }
        static void TickWork(RunSession s, double elapsed) => typeof(RunSession).GetMethod("TickWorkAction", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(s, new object[] { elapsed });
        static void Equal(object a, object b) => Assert.IsTrue(V.VariantEquals(a,b));
        [Test]
        public void RecoveryReleasesStockWithFullBagAndPartialTakeConservesRemainderWithoutXp()
        {
            var s = Boot(out var rig, out _);
            try
            {
                const string id = "home_spare_harness_rack_01";
                long cap = ItemDefs.MaxStack(ItemDefs.LoadDefinitions(), "scrap_metal");
                s.InventoryState.AddItem("scrap_metal", cap); s.InventoryState.AddItem("wiring_bundle", ItemDefs.MaxStack(ItemDefs.LoadDefinitions(),"wiring_bundle"));
                var inventory = s.InventoryState.GetSummary(); var progression = s.PlayerProgression.GetSummary(); var training = s.TrainingEventBus.ToDict();
                AuxiliaryRepairFeasibilityTests.FinishService(s,rig,id);
                Equal(inventory,s.InventoryState.GetSummary()); Equal(progression,s.PlayerProgression.GetSummary()); Equal(training,s.TrainingEventBus.ToDict());
                Assert.AreEqual(4,s.GetAuxRackRemaining(id).GetInt("scrap_metal"));
                Assert.IsFalse(s.RequestTakeAuxRack(id).GetBool("committed")); Assert.AreEqual(4,s.GetAuxRackRemaining(id).GetInt("scrap_metal"));
                Assert.AreEqual(2,s.InventoryState.RemoveItem("scrap_metal",2));
                var take = s.RequestTakeAuxRack(id); Assert.IsTrue(take.GetBool("committed"),take.GetString("detail")+take.GetString("reason"));
                Assert.AreEqual(2,s.GetAuxRackRemaining(id).GetInt("scrap_metal")); Assert.AreEqual(4,s.GetAuxRackRemaining(id).GetInt("wiring_bundle"));
                Equal(progression,s.PlayerProgression.GetSummary()); Equal(training,s.TrainingEventBus.ToDict());
                var saved = s.CapturePaidCraftingDomain(); Assert.IsTrue(DomainBundle.TryCreate(saved,out _,out string reason),reason);
                Assert.IsTrue(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(saved),out var decoded,out reason),reason);
                Assert.IsTrue(s.RestorePaidCraftingDomain(decoded)); Assert.AreEqual(2,s.GetAuxRackRemaining(id).GetInt("scrap_metal"));
                Assert.IsFalse(s.RequestAuxiliaryService(id).GetBool("committed"),"Recovery is once-only; remaining stock does not repay XP or refill.");
                var forged=saved.DeepCopy(); forged.GetDictOrEmpty("participating_state").GetDictOrEmpty("auxiliary_services").GetDictOrEmpty("services").GetDictOrEmpty(id).GetDictOrEmpty("remaining")["scrap_metal"]=4L;
                Assert.IsFalse(DomainBundle.TryCreate(forged,out _,out _));
            }
            finally{s.Dispose();}
        }
        [Test]
        public void PartialWorkRestoreRequiresFreshHoldAndRejectsProgressSourceAndFlagTampering()
        {
            var s=Boot(out var rig,out var deps);
            try
            {
                const string id="home_spare_harness_rack_02"; var p=s.AuxiliaryServicePoints.Single(v=>v.ServiceId==id); rig.Scene.PlayerPosition=p.GlobalPosition;
                Assert.IsTrue(s.RequestAuxiliaryService(id).GetBool("committed")); Assert.IsTrue(s.BeginWorkHold());
                double stamina=s.VitalsState.Stamina; TickWork(s,.5); Assert.AreEqual(stamina-4,s.VitalsState.Stamina,1e-8);
                var saved=s.CapturePaidCraftingDomain(); Assert.IsTrue(DomainBundle.TryCreate(saved,out _,out string reason),reason);
                Assert.IsTrue(s.RestorePaidCraftingDomain(saved)); Assert.IsFalse(s.IsWorkInteractHeld);
                var job=s.GetAuxiliaryServiceState().GetDictOrEmpty("job"); Assert.AreEqual("paused",job.GetString("status")); Assert.IsTrue(job.GetBool("resume_required"));
                double progress=job.GetFloat("progress_seconds"); TickWork(s,10); Assert.AreEqual(progress,s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds"));
                foreach(string field in new[]{"progress_seconds","eligible_seconds"})
                { var bad=saved.DeepCopy(); bad.GetDictOrEmpty("participating_state").GetDictOrEmpty("auxiliary_services").GetDictOrEmpty("job")[field]=7.9; Assert.IsFalse(DomainBundle.TryCreate(bad,out _,out _),field); }
                var source=saved.DeepCopy(); source.GetDictOrEmpty("participating_state").GetDictOrEmpty("auxiliary_services")["source_sha256"]=new string('0',64); Assert.IsFalse(DomainBundle.TryCreate(source,out _,out _));
                var before=s.InventoryState.GetSummary(); deps.EnableAuxiliaryServices=false; Assert.IsFalse(s.RestorePaidCraftingDomain(saved)); Equal(before,s.InventoryState.GetSummary()); deps.EnableAuxiliaryServices=true;
                Assert.IsTrue(s.RequestAuxiliaryService(id).GetBool("committed")); Assert.IsTrue(s.BeginWorkHold());
                s.InventoryState.RemoveItem("crowbar",1); TickWork(s,.5); Assert.AreEqual("paused",s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetString("status")); Assert.AreEqual(progress,s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds"));
            }
            finally{s.Dispose();}
        }
        [Test]
        public void FullProgressSavedWithoutFinalPaymentCanResumeCompletionWithoutMoreEffort()
        {
            var engine=CoreServices.Engine; CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);
            var s=Boot(out var rig,out var deps);
            try
            {
                const string id="home_spare_harness_rack_02";
                rig.Scene.PlayerPosition=s.AuxiliaryServicePoints.Single(p=>p.ServiceId==id).GlobalPosition;
                Assert.IsTrue(s.RequestAuxiliaryService(id).GetBool("committed")); Assert.IsTrue(s.BeginWorkHold());
                s.ComponentStageHook=stage=>{if(stage=="live_inventory" && s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds")==8)throw new System.InvalidOperationException("completion boundary fault");};
                for(int step=0;step<100 && s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds")<8;step++)
                {
                    if(s.VitalsState.Stamina<25){s.PauseAuxiliaryService("rest");s.EndWorkHold();while(s.VitalsState.Stamina<75)s.VitalsState.Tick(.1,new GdDict{{"moving",false}});Assert.IsTrue(s.RequestAuxiliaryService(id).GetBool("committed"));Assert.IsTrue(s.BeginWorkHold());}
                    TickWork(s,.5);
                }
                s.ComponentStageHook=null;
                Assert.AreEqual(8,s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds"));
                Assert.IsFalse(s.GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetBool("released"));
                Assert.IsTrue(s.RequestSaveToSlot("world",SaveSlotState.SlotKindWorld,"Aux full progress"),PaidSnapshotCodec.Stringify(s.LastSaveResult));
                var selected=s.SaveLoadService.SelectGeneration("world"); Assert.IsTrue(selected.GetBool("ok"));
                var nextDeps=SessionHarness.GoldenDeps(out var nextRig); SessionHarness.OverlayGamePlayability(nextDeps);
                nextDeps.LayoutPath=deps.LayoutPath; nextDeps.GameplaySlicePath=deps.GameplaySlicePath; nextDeps.BlueprintPath=deps.BlueprintPath;
                nextDeps.EnablePaidCrafting=true; nextDeps.EnableManualStudy=true; nextDeps.EnableAuxiliaryServices=true;
                nextDeps.Storage=rig.Storage; nextDeps.SelectedSaveGeneration=selected;
                var fresh=RunSession.Create(nextDeps);
                try
                {
                    Assert.IsTrue(fresh.PlayableStarted,fresh.LastFailureReason);
                    var freshPoint=fresh.AuxiliaryServicePoints.Single(p=>p.ServiceId==id);
                    Assert.AreSame(fresh.HomeShip.SceneRoot,freshPoint.Parent); Assert.IsTrue(freshPoint.IsValid&&freshPoint.IsInsideTree);
                    Assert.IsFalse(fresh.IsWorkInteractHeld); Assert.IsFalse(fresh.AuxiliaryWorkRunning);
                    Assert.AreEqual(8,fresh.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds"));
                }
                finally { fresh.Dispose(); }
                var oldPoint=s.AuxiliaryServicePoints.Single(p=>p.ServiceId==id);
                Assert.IsTrue(s.RequestLoad(),PaidSnapshotCodec.Stringify(s.LastSaveResult));
                var restoredPoint=s.AuxiliaryServicePoints.Single(p=>p.ServiceId==id);
                Assert.AreNotSame(oldPoint,restoredPoint); Assert.IsFalse(oldPoint.IsValid);
                Assert.AreSame(s.HomeShip.SceneRoot,restoredPoint.Parent); Assert.IsTrue(restoredPoint.IsValid&&restoredPoint.IsInsideTree);
                rig.Scene.PlayerPosition=restoredPoint.GlobalPosition;
                Assert.IsTrue(restoredPoint.IsPlayerInDirectRangeStrict(rig.Scene.PlayerPosition));
                Assert.IsFalse(s.IsWorkInteractHeld); Assert.IsFalse(s.AuxiliaryWorkRunning);
                while(s.VitalsState.Stamina<75)s.VitalsState.Tick(.1,new GdDict{{"moving",false}});
                Assert.IsTrue(s.RequestAuxiliaryService(id).GetBool("committed"));Assert.IsTrue(s.BeginWorkHold());double stamina=s.VitalsState.Stamina;
                TickWork(s,.5); Assert.IsTrue(s.GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetBool("released")); Assert.AreEqual(stamina,s.VitalsState.Stamina,"Completing already-earned work charges no additional elapsed effort.");
            }
            finally{s.Dispose();CoreServices.Engine=engine;}
        }
        [Test]
        public void PublicationFailureRollsBackWorkAndEffortBeforeObservers()
        {
            var s=Boot(out var rig,out _);
            try
            {
                const string id="home_spare_harness_rack_02"; rig.Scene.PlayerPosition=s.AuxiliaryServicePoints.Single(p=>p.ServiceId==id).GlobalPosition;
                Assert.IsTrue(s.RequestAuxiliaryService(id).GetBool("committed")); Assert.IsTrue(s.BeginWorkHold()); var before=s.GetAuxiliaryServiceState().GetDictOrEmpty("job").DeepCopy(); double stamina=s.VitalsState.Stamina;
                s.ComponentStageHook=stage=>{if(stage=="live_inventory")throw new System.InvalidOperationException("owned auxiliary publication fault");}; TickWork(s,.5); s.ComponentStageHook=null;
                Assert.AreEqual(before.GetFloat("progress_seconds"),s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds")); Assert.AreEqual(stamina,s.VitalsState.Stamina);
            }
            finally{s.Dispose();}
        }
    }
}
