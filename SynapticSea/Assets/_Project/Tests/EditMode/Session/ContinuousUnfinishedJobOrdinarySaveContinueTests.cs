using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
    #if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
    #endif
    public class ContinuousUnfinishedJobOrdinarySaveContinueTests : InfraDataTestBase
    {
        // Genuine production-entry witness. SceneHarness is headless; this is not Unity walking evidence.
        // Save/ordinary Continue assertions are added to THIS witness once activation crosses.
        [Test]
        public void EarnedUnfinishedJobContinuousTickOrdinarySaveContinue()
        {
            var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);
            const string path="res://data/diagnostics/earned-services-home-v1/";
            deps.LayoutPath=path+"layout.json";deps.GameplaySlicePath=path+"gameplay_slice.json";deps.BlueprintPath=path+"blueprint.json";
            deps.EnablePaidCrafting=true;deps.EnableManualStudy=true;deps.EnableAuxiliaryServices=true;
            deps.EnableComponentIntegration=true;deps.EnableBitExactPaidCompatibility=true;deps.EnableContinuousAuxiliaryDiagnostic=true;
            var session=RunSession.Create(deps);
            try
            {
                Assert.IsTrue(session.PlayableStarted,session.LastFailureReason);
                var inventory=session.InventoryState;var progression=session.PlayerProgression;var training=session.TrainingEventBus;
                var supply=session.LootContainers.Single(p=>p.ContainerId=="start_supply_a");
                rig.Scene.PlayerPosition=supply.GlobalPosition;
                Assert.IsTrue(supply.TryInteract(supply.GlobalPosition),"ordinary authored supply interaction");
                if(session.EquipmentState.GetEquipped("primary_hand")=="crowbar")
                    Assert.AreEqual("crowbar",session.UnequipToInventory("primary_hand"));
                Assert.GreaterOrEqual(inventory.GetQuantity("crowbar"),1,"authored earned supply, no inventory grants");
                var activate=typeof(RunSession).GetMethod("TryActivateContinuousAuxiliaryDiagnostic",BindingFlags.Instance|BindingFlags.Public);
                Assert.IsNotNull(activate,"production continuous activation entrypoint is absent after authentic ordinary boot");
                object[] arguments={null};
                Assert.IsTrue((bool)activate.Invoke(session,arguments),arguments[0] as string);
                var active=typeof(RunSession).GetProperty("ContinuousAuxiliaryRuntimeActive",BindingFlags.Instance|BindingFlags.Public);
                Assert.IsNotNull(active,"production active-cohort contract is absent");Assert.IsTrue((bool)active.GetValue(session));
                Assert.AreSame(inventory,session.InventoryState);Assert.AreSame(progression,session.PlayerProgression);Assert.AreSame(training,session.TrainingEventBus);
                const string service="home_spare_harness_rack_02";
                var point=session.AuxiliaryServicePoints.Single(p=>p.ServiceId==service);
                rig.Scene.PlayerPosition=point.GlobalPosition;
                var start=session.RequestAuxiliaryService(service);
                Assert.IsTrue(start.GetBool("ok"),start.GetString("reason"));
                Assert.IsTrue(start.GetBool("queued"));Assert.IsFalse(start.GetBool("committed"));
                // Pump through real RunSession.Tick; no reflection/work-only helper, no threat clears.
                double time=session.WorldTime;
                for(int frame=0;frame<240&&session.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds")<=0;frame++)
                    session.Tick(TickContext.Frame(1.0/60.0,rig.Scene.PlayerPosition,interactHeld:true));
                Assert.Greater(session.WorldTime,time);
                var job=session.GetAuxiliaryServiceState().GetDictOrEmpty("job");
                Assert.Greater(job.GetFloat("progress_seconds"),0);Assert.Less(job.GetFloat("progress_seconds"),8);
                Assert.IsTrue(session.RequestSaveToSlot("slot_01","manual","unfinished witness"),session.LastSaveResult.GetString("reason"));
                double saveStarted=session.WorldTime;
                var timeout=Stopwatch.StartNew();double previousElapsed=0;
                while(session.LastSaveResult.GetBool("pending")&&timeout.Elapsed.TotalSeconds<30)
                {
                    // Consent is released, not simulation: ordinary oxygen/survival/threat Tick continues.
                    double elapsed=timeout.Elapsed.TotalSeconds;
                    double delta=System.Math.Min(1.0/60.0,elapsed-previousElapsed);previousElapsed=elapsed;
                    session.Tick(TickContext.Frame(delta,rig.Scene.PlayerPosition,interactHeld:false));
                    Thread.Yield();
                }
                Assert.IsTrue(session.LastSaveResult.GetBool("ok"),session.LastSaveResult.GetString("reason")+":"+session.LastSaveResult.GetString("detail"));
                Assert.Greater(session.WorldTime,saveStarted,"actual world advances during ordinary save workers");
                var selected=session.SaveLoadService.SelectGeneration("slot_01");
                Assert.IsTrue(selected.GetBool("ok"),selected.GetString("reason"));
                Assert.IsTrue(session.SaveLoadService.TryReadContinuousSelection(selected,out var admitted,out string admissionReason),admissionReason);
                var savedRun=admitted.CopyRun();
                var savedJob=AuxiliaryServiceState.State(admitted.CopyOwner()).GetDictOrEmpty("job");
                // The cut is historical R; compare Continue against R, never against later live stamina.
                double savedStamina=savedRun.GetDictOrEmpty("vitals_summary").GetFloat("stamina");
                var resumeDeps=SessionHarness.GoldenDeps(out var resumeRig);SessionHarness.OverlayGamePlayability(resumeDeps);
                resumeDeps.LayoutPath=deps.LayoutPath;resumeDeps.GameplaySlicePath=deps.GameplaySlicePath;resumeDeps.BlueprintPath=deps.BlueprintPath;
                resumeDeps.EnablePaidCrafting=true;resumeDeps.EnableManualStudy=true;resumeDeps.EnableAuxiliaryServices=true;
                resumeDeps.EnableComponentIntegration=true;resumeDeps.EnableBitExactPaidCompatibility=true;resumeDeps.EnableContinuousAuxiliaryDiagnostic=true;
                resumeDeps.Storage=deps.Storage;resumeDeps.SelectedSaveGeneration=selected;
                var resumed=RunSession.Create(resumeDeps);
                try
                {
                    Assert.IsTrue(resumed.PlayableStarted,resumed.LastFailureReason);
                    object[] resumeArguments={null};
                    Assert.IsTrue((bool)activate.Invoke(resumed,resumeArguments),resumeArguments[0] as string);
                    Assert.AreEqual(System.BitConverter.DoubleToInt64Bits(savedStamina),System.BitConverter.DoubleToInt64Bits(resumed.VitalsState.Stamina));
                    var restoredJob=resumed.GetAuxiliaryServiceState().GetDictOrEmpty("job");
                    Assert.Greater(restoredJob.GetFloat("progress_seconds"),0);
                    Assert.AreEqual(savedJob.GetFloat("progress_seconds"),restoredJob.GetFloat("progress_seconds"));
                    Assert.AreEqual(savedJob.GetFloat("eligible_seconds"),restoredJob.GetFloat("eligible_seconds"));
                    Assert.AreEqual("paused",restoredJob.GetString("status"),"Continue never restores consent");
                    double restoredProgress=restoredJob.GetFloat("progress_seconds"),restoredTime=resumed.WorldTime;
                    resumed.Tick(TickContext.Frame(1.0/60.0,resumeRig.Scene.PlayerPosition,interactHeld:false));
                    Assert.Greater(resumed.WorldTime,restoredTime);
                    Assert.AreEqual(restoredProgress,resumed.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds"),"no prefix replay or implicit resume");
                }
                finally{resumed.Dispose();}

            }
            finally{session.Dispose();}
        }
    }
}
