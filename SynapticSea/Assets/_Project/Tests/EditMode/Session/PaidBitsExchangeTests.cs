using System;
using System.Collections.Generic;
using System.IO;
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
    // Provisioned headless normal-command/save fixtures; no physical acquisition claim.
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public sealed class PaidBitsExchangeTests : InfraDataTestBase
    {
        const string Recipe="weld_plating", Book="fabrication_schematic_basic", Utility="maintenance_fabricator_feed_01", OtherUtility="maintenance_cargo_relay_01";
        static readonly double Real=BitConverter.Int64BitsToDouble(0x3fa81f8b6a300d00L);
        readonly List<RunSession> _sessions=new List<RunSession>();
        IEngineInfo _engine;
        static string Runtime => Type.GetType("Mono.Runtime")==null?".NET":"Mono";
        [SetUp] public void Engine() {_engine=CoreServices.Engine;CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);}
        [TearDown] public void Cleanup(){foreach(var s in _sessions)s.Dispose();_sessions.Clear();CoreServices.Engine=_engine;}
        RunSession Boot(out SessionHarness.Rig rig)
        {
            var d=SessionHarness.GoldenDeps(out rig);SessionHarness.OverlayGamePlayability(d);
            const string p="res://data/diagnostics/earned-services-home-v1/";
            d.LayoutPath=p+"layout.json";d.GameplaySlicePath=p+"gameplay_slice.json";d.BlueprintPath=p+"blueprint.json";
            d.EnablePaidCrafting=true;d.EnableManualStudy=true;d.EnableAuxiliaryServices=true;d.EnableBitExactPaidCompatibility=true;
            var s=rig.Session=RunSession.Create(d);_sessions.Add(s);Assert.IsTrue(s.PlayableStarted,s.LastFailureReason);
            s.ThreatManager.Threats.Clear();s.InventoryState.Items.Clear();s.PlayerProgression.Skills["fabrication"]=4L;
            s.CraftingState.GetStation("workbench").SetPower(true);s.VitalsState.Stamina=s.VitalsState.MaxStamina;
            Assert.AreEqual(6,s.CapturePaidCraftingDomain().GetInt("schema_version"));return s;
        }
        static void Equal(object a,object b,string why)=>Assert.IsTrue(PaidHashContext.BitsV2.Equal(a,b),why);
        static void Ok(GdDict r){Assert.IsTrue(r.GetBool("ok"),r.GetString("reason")+":"+r.GetString("detail"));Assert.IsTrue(r.GetBool("committed"));}
        static GdDict Jobs(GdDict o)=>PaidCraftingState.State(o).GetDictOrEmpty("jobs");
        static string Craft(RunSession s,string id)
        {
            foreach(var x in s.CraftingState.GetRecipe(Recipe).GetDictOrEmpty("ingredients"))Assert.AreEqual(V.I64(x.Value),s.InventoryState.AddItem(V.Str(x.Key),V.I64(x.Value)));
            var r=s.RequestPaidCraft("workbench",Recipe,id);Ok(r);return r.GetString("job_id");
        }
        static void Step(RunSession s,double delta)=>typeof(RunSession).GetMethod("TickWorkAction",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(s,new object[]{delta});
        static void StudyStep(RunSession s,double delta)
        {
            var frame=TickContext.Frame(delta,s.Scene.PlayerPosition,false);frame.InBreachZone=false;frame.InFireZoneCompartment="";s.Tick(frame);
        }
        static void AuxProvision(RunSession s,string id)
        {
            if(s.InventoryState.GetQuantity("crowbar")==0)Assert.AreEqual(1,s.InventoryState.AddItem("crowbar",1));
            foreach(var x in s.GetAuxiliaryServiceState().GetDictOrEmpty("descriptors").GetDictOrEmpty(id).GetDictOrEmpty("materials_consumed"))Assert.AreEqual(V.I64(x.Value),s.InventoryState.AddItem(V.Str(x.Key),V.I64(x.Value)));
        }
        GdDict Produce(string scenario)
        {
            var s=Boot(out var rig);string done=Craft(s,"exchange-completed");s.AdvanceCrafting(100);Assert.IsTrue(PaidCraftingState.Terminal(Jobs(s.CapturePaidCraftingDomain()).GetDictOrEmpty(done)));
            string cancel=Craft(s,"exchange-cancelled");Ok(s.CancelPaidCraft(cancel,"exchange-cancel"));Assert.AreEqual("cancelled",Jobs(s.CapturePaidCraftingDomain()).GetDictOrEmpty(cancel).GetString("status"));
            Assert.AreEqual(1,s.InventoryState.AddItem(Book,1));
            if(scenario!="manual")
            {
                Ok(s.RequestManualStudy(Book));Assert.IsTrue(s.BeginWorkHold());
                for(int i=0;i<100&&s.ManualStudyRunning;i++)StudyStep(s,.5);
                Assert.AreEqual("completed",s.GetManualStudyState().GetDictOrEmpty("job").GetString("status"));s.EndWorkHold();
            }
            AuxProvision(s,Utility);var costs=s.GetAuxiliaryServiceState().GetDictOrEmpty("descriptors").GetDictOrEmpty(Utility).GetDictOrEmpty("materials_consumed");
            var before=new Dictionary<string,long>();foreach(var x in costs)before[V.Str(x.Key)]=s.InventoryState.GetQuantity(V.Str(x.Key));
            AuxiliaryRepairFeasibilityTests.FinishService(s,rig,Utility);
            foreach(var x in costs)Assert.AreEqual(before[V.Str(x.Key)]-V.I64(x.Value),s.InventoryState.GetQuantity(V.Str(x.Key)),"authored auxiliary materials paid once");
            if(scenario=="craft") {string pending=Craft(s,"exchange-pending");s.AdvanceCrafting(Real);Assert.Greater(Jobs(s.CapturePaidCraftingDomain()).GetDictOrEmpty(pending).GetFloat("progress_seconds"),0);}
            else if(scenario=="manual") {Ok(s.RequestManualStudy(Book));Assert.IsTrue(s.BeginWorkHold());StudyStep(s,Real);Assert.Greater(s.GetManualStudyState().GetDictOrEmpty("job").GetFloat("progress_seconds"),0);}
            else {AuxProvision(s,OtherUtility);rig.Scene.PlayerPosition=s.AuxiliaryServicePoints.Single(p=>p.ServiceId==OtherUtility).GlobalPosition;Ok(s.RequestAuxiliaryService(OtherUtility));Assert.IsTrue(s.BeginWorkHold());Step(s,Real);Assert.Greater(s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds"),0);}
            Assert.IsNotEmpty(s.WoundState.ApplyWound(new GdDict{{"wound_id","exchange-wound"},{"kind",WoundState.KIND_BURN},{"body_part",WoundState.BODY_TORSO},{"severity",.06542359136835758}}));s.WoundState.Tick(Real);
            var owner=s.CapturePaidCraftingDomain();Assert.IsTrue(DomainBundle.TryCreate(owner,out _,out var reason),reason);
            Assert.IsTrue(s.RequestSaveToSlot("world","world","Paid bits exchange"),s.LastSaveResult.GetString("reason")+":"+s.LastSaveResult.GetString("detail"));
            var selected=s.SaveLoadService.SelectGeneration("world");Assert.IsTrue(selected.GetBool("ok"),selected.GetString("reason"));
            var files=new GdDict();foreach(var x in SlotPayloadBindingTests.Bytes(rig.Storage))files[x.Key]=x.Value;
            return new GdDict{{"fixture_version",1L},{"producer_runtime",Runtime},{"scenario",scenario},{"generation_id",selected.Get("generation_id")},{"manifest_sha256",selected.Get("manifest_sha256")},{"owner",owner},{"wounds",s.WoundState.GetSummary()},{"files",files}};
        }
        void Consume(GdDict package,bool foreign)
        {
            Assert.AreEqual(1,package.GetInt("fixture_version"));if(foreign)Assert.AreNotEqual(Runtime,package.GetString("producer_runtime"),"actual different runtime required");
            var s=Boot(out var rig);foreach(var x in package.GetDictOrEmpty("files"))rig.Storage.WriteText(V.Str(x.Key),(string)x.Value);
            var bytes=SlotPayloadBindingTests.Bytes(rig.Storage);var selected=s.SaveLoadService.SelectGeneration("world");Assert.IsTrue(selected.GetBool("ok"),selected.GetString("reason")+":"+selected.GetString("detail"));
            Assert.AreEqual(package.Get("generation_id"),selected.Get("generation_id"));Assert.AreEqual(package.Get("manifest_sha256"),selected.Get("manifest_sha256"));
            Assert.IsTrue(s.ApplySelectedGeneration(selected),s.LastSaveResult.GetString("reason")+":"+s.LastSaveResult.GetString("detail"));SlotPayloadBindingTests.SameBytes(bytes,rig.Storage);
            var saved=package.GetDictOrEmpty("owner");var actual=s.CapturePaidCraftingDomain();var normalized=actual.DeepCopy();
            Assert.AreEqual(6,actual.GetInt("schema_version"));Assert.AreEqual(PaidHashContext.BitsV2.Algorithm,actual.GetString("hash_algorithm"));
            foreach(var x in Jobs(saved))
            {
                var prior=(GdDict)x.Value;if(PaidCraftingState.Terminal(prior))continue;
                var now=Jobs(actual).GetDictOrEmpty(x.Key);Assert.IsTrue(now.GetBool("resume_required"));Assert.AreEqual("paused",now.GetString("status"));
                var n=Jobs(normalized).GetDictOrEmpty(x.Key);n["status"]=prior.Get("status");n["resume_required"]=prior.Get("resume_required");
                var ps=saved.GetDictOrEmpty("participating_state").GetDictOrEmpty("crafting").GetDictOrEmpty("station_summaries").GetDictOrEmpty(prior.GetString("station_kind"));
                var ns=normalized.GetDictOrEmpty("participating_state").GetDictOrEmpty("crafting").GetDictOrEmpty("station_summaries").GetDictOrEmpty(prior.GetString("station_kind"));ns["status"]=ps.Get("status");ns["resume_required"]=ps.Get("resume_required");
            }
            foreach(string state in new[]{"manual_study","auxiliary_services"})
            {
                var prior=saved.GetDictOrEmpty("participating_state").GetDictOrEmpty(state).GetDictOrEmpty("job");if(prior.IsEmpty||prior.GetString("status")=="completed")continue;
                var now=actual.GetDictOrEmpty("participating_state").GetDictOrEmpty(state).GetDictOrEmpty("job");Assert.AreEqual("paused",now.GetString("status"));Assert.IsTrue(now.GetBool("resume_required"));Assert.AreEqual("explicit_resume_required",now.GetString("reason"));
                var n=normalized.GetDictOrEmpty("participating_state").GetDictOrEmpty(state).GetDictOrEmpty("job");foreach(string key in new[]{"status","resume_required","reason"})n[key]=prior.Get(key);
            }
            Equal(saved,normalized,"all owner leaves and receipt references except consent projection");Equal(package.Get("wounds"),s.WoundState.GetSummary(),"exact wound bits");Assert.IsFalse(s.IsWorkInteractHeld);
            var inventory=s.InventoryState.GetSummary();var progression=s.PlayerProgression.GetSummary();var training=s.TrainingEventBus.ToDict();
            if(saved.GetDictOrEmpty("participating_state").GetDictOrEmpty("manual_study").GetDictOrEmpty("completed").Has(Book))
                Assert.AreEqual("already_studied",s.RequestManualStudy(Book).GetString("reason"));
            else Assert.IsFalse(s.PlayerProgression.HasReadBook(Book),"Pending manual is not rewarded during admission.");
            Ok(s.RequestPaidCraft("workbench",Recipe,"exchange-completed"));
            Equal(inventory,s.InventoryState.GetSummary(),"no replay grant/consumption");Equal(progression,s.PlayerProgression.GetSummary(),"no replay XP");Equal(training,s.TrainingEventBus.ToDict(),"no replay history");
            var forged=saved.DeepCopy();var receipt=forged.GetDictOrEmpty("receipts").Values.OfType<GdDict>().First(r=>r.Has("command_hash"));receipt["command_hash"]=PaidHashContext.Legacy.Hash(receipt.Get("command"));Assert.IsFalse(DomainBundle.TryCreate(forged,out _,out _),"mixed algorithm receipt refuses");
        }
        [TestCase("craft")][TestCase("manual")][TestCase("auxiliary")]
        public void NormalFullGenerationPreservesPendingAndTerminalProofs(string scenario)
        {
            var package=Produce(scenario);Consume(package,false);string d=Environment.GetEnvironmentVariable("SYNAPTIC_PAID_BITS_EXPORT_DIR");
            if(!string.IsNullOrEmpty(d)){Directory.CreateDirectory(d);string p=Path.Combine(d,scenario+".json");Assert.IsFalse(File.Exists(p),"preserve original exchange archive");File.WriteAllText(p,PaidSnapshotCodec.Stringify(ComponentDomainCodec.Encode(package,ComponentDomainCodec.BitExactSchema),PaidSnapshotCodec.Policy.TypedOwner),new System.Text.UTF8Encoding(false,true));TestContext.WriteLine("PAID_BITS_EXPORT="+p+";runtime="+Runtime);}
        }
        [TestCase("craft")][TestCase("manual")][TestCase("auxiliary")]
        public void ForeignFullGenerationUsesNormalAdmissionAndApply(string scenario)
        {
            string d=Environment.GetEnvironmentVariable("SYNAPTIC_PAID_BITS_IMPORT_DIR");if(string.IsNullOrEmpty(d))Assert.Ignore("Foreign archive absent; local success is not cross-runtime proof.");
            var e=GdJson.ParseDict(File.ReadAllText(Path.Combine(d,scenario+".json"),new System.Text.UTF8Encoding(false,true)));Assert.IsTrue(ComponentDomainCodec.TryDecode(e,out var package,out var reason),reason);Consume(package,true);TestContext.WriteLine("PAID_BITS_IMPORT="+scenario+";producer="+package.GetString("producer_runtime")+";consumer="+Runtime);
        }
    }
}
