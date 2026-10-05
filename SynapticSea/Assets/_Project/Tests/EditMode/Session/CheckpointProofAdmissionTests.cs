using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
    #if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
    #endif
    public sealed class CheckpointProofAdmissionTests : InfraDataTestBase
    {
        sealed class RecordingReader : IResourceReader,IResourceDirectoryReader
        {
            readonly IResourceReader _reader;
            internal readonly Dictionary<string,string> Texts=new Dictionary<string,string>();
            internal readonly Dictionary<string,IReadOnlyList<string>> Dirs=new Dictionary<string,IReadOnlyList<string>>();
            internal RecordingReader(IResourceReader reader){_reader=reader;}
            public bool Exists(string path){bool exists=_reader.Exists(path);Texts[path]=exists?_reader.ReadText(path):null;return exists;}
            public string ReadText(string path){string text=_reader.ReadText(path);Texts[path]=text;return text;}
            public bool DirExists(string path){var r=_reader as IResourceDirectoryReader;bool exists=r!=null&&r.DirExists(path);Dirs[path]=exists?r.ListFiles(path).ToArray():null;if(exists)foreach(string n in Dirs[path])Texts[path+"/"+n]=_reader.ReadText(path+"/"+n);return exists;}
            public IReadOnlyList<string> ListFiles(string path){DirExists(path);return Dirs[path]??Array.Empty<string>();}
        }
        static void Upgrade(GdDict owner){owner["schema_version"]=7L;owner["feature_schema"]=5L;owner["auxiliary_proof_format"]=1L;owner["hash_algorithm"]=PaidHashContext.BitsV2.Algorithm;owner["auxiliary_proofs"]=new GdDict();}
        static GdDict Capsule(GdDict owner,ProofResourceBinding binding)=>new GdDict{{"capsule_version",1L},{"owner_profile",AuxiliaryProofOwnerProfile.Profile},{"run_id",PaidCraftingState.State(owner).Get("run_id")},{"actor_id",PaidCraftingState.State(owner).Get("actor_id")},{"owner_revision",owner.Get("revision")},{"command_sequence",owner.Get("command_sequence")},{"resource_digest",binding.ResourceCapsuleDigest},{"owner",owner.DeepCopy()},{"origin_refs",new GdArray()}};
        static GdDict Step(AuxiliaryAcceptedStep a,double duration)=>new GdDict{{"sequence",a.Sequence},{"delta_seconds",a.RequestedDelta},{"stamina_before",a.StaminaBefore},{"max_stamina",a.MaxStamina},{"wound_work_multiplier",a.WoundSpeed},{"ratio",Math.Max(0,Math.Min(1,a.StaminaBefore/Math.Max(1,a.MaxStamina)))},{"speed",a.Speed},{"remaining_before",duration-a.ProgressBefore},{"elapsed_seconds",a.ElapsedSeconds},{"delta_progress",a.DeltaSeconds},{"stamina_after",a.StaminaAfter},{"progress_after",a.ProgressAfter},{"eligible_after",a.EligibleAfter}};
        static OwnedProofPackageInput Own(GdDict package)
        {
            var wire=new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",ComponentDomainCodec.Encode(package,ComponentDomainCodec.BitExactSchema)}};
            Assert.IsTrue(ProofPackageCodec.TryOwnEnvelope(wire,out var input,out string reason),reason);return input;
        }
        IResourceReader _bootReader;
        GdDict _capturedRun,_capturedWorld;
        internal GdDict CopyCapturedRun()=>_capturedRun.DeepCopy();
        internal GdDict CopyCapturedWorld()=>_capturedWorld.DeepCopy();
        internal GdDict Fixture(bool ready,out ProofResourceBinding binding,out GdDict origin,bool utility=false,bool components=false)
        {
            if(_bootReader==null)_bootReader=CoreServices.Resources;
            else CoreServices.Resources=_bootReader;
            CatalogRegistry.Clear();
            var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);const string path="res://data/diagnostics/earned-services-home-v1/";
            deps.LayoutPath=path+"layout.json";deps.GameplaySlicePath=path+"gameplay_slice.json";deps.BlueprintPath=path+"blueprint.json";deps.EnablePaidCrafting=true;deps.EnableManualStudy=true;deps.EnableAuxiliaryServices=true;deps.EnableBitExactPaidCompatibility=true;deps.EnableComponentIntegration=components;
            var session=RunSession.Create(deps);try
            {
                Assert.IsTrue(session.PlayableStarted,session.LastFailureReason);var supply=session.LootContainers.Single(p=>p.ContainerId=="start_supply_a");rig.Scene.PlayerPosition=supply.GlobalPosition;Assert.IsTrue(supply.TryInteract(supply.GlobalPosition));
                if(session.EquipmentState.GetEquipped("primary_hand")=="crowbar")session.UnequipToInventory("primary_hand");
                string service=utility?"maintenance_fabricator_feed_01":"home_spare_harness_rack_02";
                rig.Scene.PlayerPosition=session.AuxiliaryServicePoints.Single(p=>p.ServiceId==service).GlobalPosition;
                var requested=session.RequestAuxiliaryService(service);
                if(utility)
                {
                    Assert.IsFalse(requested.GetBool("committed"),"Authored initial stock must first exercise the missing-material gate");
                    Assert.AreEqual("missing_materials",requested.GetString("reason"),requested.GetString("detail"));
                    // Isolated admission-test setup, not an earned physical acquisition route.
                    foreach(var part in session.GetAuxiliaryServiceState().GetDictOrEmpty("descriptors").GetDictOrEmpty(service).GetDictOrEmpty("materials_consumed"))
                    {
                        string item=(string)part.Key;long missing=Math.Max(0,(long)part.Value-session.InventoryState.GetQuantity(item));
                        if(missing>0)Assert.AreEqual(missing,session.InventoryState.AddItem(item,missing),"Diagnostic fixture uses the unchanged stock inventory allocator");
                    }
                    requested=session.RequestAuxiliaryService(service);
                }
                Assert.IsTrue(requested.GetBool("committed"),requested.GetString("reason")+":"+requested.GetString("detail"));
                origin=session.CapturePaidCraftingDomain();Assert.AreEqual(6,origin.GetInt("schema_version"));
                _capturedRun=RunSnapshotAssembler.Build(session).ToDict();
                _capturedWorld=WorldSnapshotAssembler.Build(session).ToDict();
            }
            finally {session.Dispose();}
            var originalReader=CoreServices.Resources;var reader=new RecordingReader(originalReader);CoreServices.Resources=reader;CatalogRegistry.Clear();
            Assert.IsTrue(DomainBundle.TryCreate(origin,out _,out string valid),valid);
            // Exercise unchanged private completion catalog reads too, before sealing caller authority.
            var completionProbe=origin.DeepCopy();var probeState=AuxiliaryServiceState.State(completionProbe);string id=(string)probeState.GetDictOrEmpty("job").Get("service_id");double duration=probeState.GetDictOrEmpty("descriptors").GetDictOrEmpty(id).GetFloat("required_seconds");probeState.GetDictOrEmpty("job")["progress_seconds"]=duration;probeState.GetDictOrEmpty("job")["eligible_seconds"]=duration;AuxiliaryServiceState.Apply(completionProbe,"aux_complete",id);
            ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(reader.Texts,reader.Dirs));Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var lease,out _));Assert.IsTrue(ProofResourceBinding.TryCapture(lease,out binding,out valid),valid);
            var capsule=Capsule(origin,binding);string capsuleDigest=PaidHashContext.BitsV2.Hash(capsule),originId=AuxiliaryServiceState.Origin(origin,id);var start=origin.GetDictOrEmpty("receipts").GetDictOrEmpty(originId);string startHash=PaidHashContext.BitsV2.Hash(start);
            var paid=PaidCraftingState.State(origin);var runtime=new AuxiliaryWorkRuntime("owner",(string)paid.Get("run_id"),(string)paid.Get("actor_id"),id,0,PaidHashContext.BitsV2.Algorithm,duration,0,0,100,0,256);var steps=new GdArray();
            do {var accepted=runtime.Step(new AuxiliaryWorkFrame(.125,100,100,1));Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted,accepted.Status);steps.Add(Step(accepted.AcceptedStep,duration));}while(ready&&runtime.Snapshot().ProgressSeconds<duration);
            var snapshot=runtime.Snapshot();var chunk=new GdDict{{"chunk_version",1L},{"ordinal",0L},{"previous_chunk_digest",startHash},{"initial_step_sequence",1L},{"progress_before",0.0},{"eligible_before",0.0},{"accepted_steps_before",0L},{"steps",steps},{"progress_after",snapshot.ProgressSeconds},{"eligible_after",snapshot.EligibleSeconds},{"accepted_steps_after",snapshot.EligibleSteps}};string chunkDigest=PaidHashContext.BitsV2.Hash(chunk);chunk["digest"]=chunkDigest;
            var job=AuxiliaryServiceState.State(origin).GetDictOrEmpty("job").DeepCopy();job["progress_seconds"]=snapshot.ProgressSeconds;job["eligible_seconds"]=snapshot.EligibleSeconds;
            var proof=new GdDict{{"proof_version",1L},{"state",ready?"ready_on_cut":"ongoing"},{"run_id",paid.Get("run_id")},{"actor_id",paid.Get("actor_id")},{"service_id",id},{"owner_hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"resource_capsule_digest",binding.ResourceCapsuleDigest},{"snapshot_content_sha256",binding.SnapshotContentSha256},{"descriptor_digest",PaidHashContext.BitsV2.Hash(AuxiliaryServiceState.State(origin).GetDictOrEmpty("descriptors").Get(id))},{"origin_receipt_id",originId},{"origin_receipt_digest",startHash},{"start_origin_capsule_digest",capsuleDigest},{"start_origin_owner_digest",PaidHashContext.BitsV2.Hash(origin)},{"chunks",GdArray.Of(chunk)},{"latest_chunk_digest",chunkDigest},{"job_after",job},{"accepted_steps",snapshot.EligibleSteps},{"work_lineage_id","auxiliary-work:"+startHash},{"cut_work_version",snapshot.EligibleSteps},{"checkpoint_before_capsule_digest",capsuleDigest},{"checkpoint_before_owner_digest",PaidHashContext.BitsV2.Hash(origin)},{"checkpoint_receipt_id","auxiliary-proof-checkpoint:test"},{"checkpoint_receipt_digest",new string('0',64)}};
            string proofHash=AuxiliaryProofOwnerProfile.ProofDigest(proof);var command=new GdDict{{"command_id","test"},{"operation","aux_proof_checkpoint_v1"},{"run_id",paid.Get("run_id")},{"actor_id",paid.Get("actor_id")},{"proof_digest",proofHash}};
            foreach(string key in new[]{"service_id","work_lineage_id","checkpoint_before_capsule_digest","checkpoint_before_owner_digest","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest","accepted_steps","cut_work_version"})command[key]=proof.Get(key);
            var effect=new GdDict{{"operation","aux_proof_checkpoint_v1"},{"service_id",id},{"work_lineage_id",proof.Get("work_lineage_id")},{"proof_digest",proofHash},{"accepted_steps",snapshot.EligibleSteps},{"cut_work_version",snapshot.EligibleSteps},{"before_owner_revision",origin.Get("revision")},{"before_command_sequence",origin.Get("command_sequence")},{"job_before",AuxiliaryServiceState.State(origin).Get("job")},{"job_after",job}};
            foreach(string key in new[]{"checkpoint_before_capsule_digest","checkpoint_before_owner_digest","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest"})effect[key]=proof.Get(key);
            const string receiptId="auxiliary-proof-checkpoint:test";var receipt=new GdDict{{"schema_version",1L},{"transaction_id",receiptId},{"commit_id",receiptId},{"command_id","test"},{"command",command},{"command_hash",PaidHashContext.BitsV2.Hash(command)},{"revision",origin.GetInt("revision")+1},{"result",effect}};proof["checkpoint_receipt_digest"]=PaidHashContext.BitsV2.Hash(receipt);
            var current=origin.DeepCopy();Upgrade(current);current["revision"]=origin.GetInt("revision")+1;current["command_sequence"]=origin.GetInt("command_sequence")+1;AuxiliaryServiceState.State(current)["job"]=job.DeepCopy();current.GetDictOrEmpty("receipts")[receiptId]=receipt;current.GetDictOrEmpty("auxiliary_proofs")[receiptId]=proofHash;
            return new GdDict{{"package_version",1L},{"owner_profile",AuxiliaryProofOwnerProfile.Profile},{"hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"run_id",paid.Get("run_id")},{"actor_id",paid.Get("actor_id")},{"current_owner",current},{"current_owner_digest",PaidHashContext.BitsV2.Hash(current)},{"current_resource_digest",binding.ResourceCapsuleDigest},{"snapshot_content_sha256",binding.SnapshotContentSha256},{"origins",new GdDict{{capsuleDigest,capsule}}},{"resources",new GdDict{{binding.ResourceCapsuleDigest,binding.CopyCapsule()}}},{"proofs",new GdDict{{proofHash,proof}}}};
        }
        [TestCase(false)] [TestCase(true)] public void AuthenticCheckpointFullyAdmitsAndOrdinaryEnvelopeRoundtrips(bool ready)
        {
            var package=Fixture(ready,out var binding,out _);var input=Own(package);Assert.IsTrue(CheckpointProofAdmission.TryAdmit(input,binding,out var admitted,out string reason),reason);
            Assert.IsFalse(DomainBundle.TryCreate(admitted.CopyOwner(),out _,out _),"No ambient admission authority leaks after context disposal");
            Assert.IsTrue(PaidSnapshotCodec.TryCreateProofEnvelope(admitted,false,out var envelope,out reason),reason);Assert.IsTrue(PaidSnapshotCodec.TryDecodeProofEnvelope(envelope,binding,false,out var restored,out reason),reason);
            Assert.IsTrue(PaidHashContext.BitsV2.Equal(admitted.CopyOwner(),restored.CopyOwner()));Assert.IsFalse(PaidSnapshotCodec.TryDecodeOwner((GdDict)envelope.Get("domain"),out _,out _));
            var escaped=admitted.CopyOwner();AuxiliaryServiceState.State(escaped).GetDictOrEmpty("job")["progress_seconds"]=0.0;Assert.IsFalse(PaidHashContext.BitsV2.Equal(escaped,admitted.CopyOwner()));
        }
        internal static GdDict Complete(GdDict package,ProofResourceBinding binding,GdDict directBefore=null)
        {
            var before=directBefore??(GdDict)package.Get("current_owner");var capsule=Capsule(before,binding);var refs=new GdArray();if(before.GetInt("schema_version")==7)foreach(string key in ((GdDict)package.Get("origins")).Keys.Cast<string>().OrderBy(k=>k,StringComparer.Ordinal))refs.Add(key);capsule["origin_refs"]=refs;
            string beforeDigest=PaidHashContext.BitsV2.Hash(capsule);((GdDict)package.Get("origins"))[beforeDigest]=capsule;
            var proof=((GdDict)((GdDict)package.Get("proofs")).Values.First()).DeepCopy();foreach(string k in new[]{"checkpoint_before_capsule_digest","checkpoint_before_owner_digest","checkpoint_receipt_id","checkpoint_receipt_digest"})proof.Erase(k);
            const string receiptId="auxiliary-proof-complete:complete";proof["state"]="completed";proof["terminal_before_capsule_digest"]=beforeDigest;proof["terminal_before_owner_digest"]=PaidHashContext.BitsV2.Hash(before);proof["completion_receipt_id"]=receiptId;proof["completion_receipt_digest"]=new string('0',64);
            var after=before.DeepCopy();after["schema_version"]=6L;after.Erase("auxiliary_proof_format");after.Erase("auxiliary_proofs");
            if(directBefore!=null)AuxiliaryServiceState.State(after)["job"]=((GdDict)proof.Get("job_after")).DeepCopy();
            var computed=AuxiliaryServiceState.Apply(after,"aux_complete",(string)proof.Get("service_id"),proofTrainingReceipt:receiptId);proof["job_after"]=computed.Get("job_after");
            string proofHash=AuxiliaryProofOwnerProfile.ProofDigest(proof);var command=new GdDict{{"command_id","complete"},{"operation","aux_proof_complete_v1"},{"proof_digest",proofHash}};
            foreach(string key in new[]{"run_id","actor_id","service_id","work_lineage_id","cut_work_version","accepted_steps","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest","terminal_before_capsule_digest","terminal_before_owner_digest"})command[key]=proof.Get(key);
            var effect=new GdDict{{"operation","aux_proof_complete_v1"}};
            foreach(var entry in command)if((string)entry.Key!="command_id"&&(string)entry.Key!="run_id"&&(string)entry.Key!="actor_id"&&(string)entry.Key!="operation")effect[entry.Key]=entry.Value;
            foreach(string key in new[]{"descriptor_sha256","job_before","service_before","inventory_before","progression_before","training_before","accepted","training_record","job_after","service_after","inventory_after","progression_after","training_after"})effect[key]=computed.Get(key);
            effect["job_before"]=AuxiliaryServiceState.State(before).GetDictOrEmpty("job").DeepCopy();
            var receipt=new GdDict{{"schema_version",1L},{"transaction_id",receiptId},{"commit_id",receiptId},{"command_id","complete"},{"command",command},{"command_hash",PaidHashContext.BitsV2.Hash(command)},{"revision",before.GetInt("revision")+1},{"result",effect}};proof["completion_receipt_digest"]=PaidHashContext.BitsV2.Hash(receipt);
            Upgrade(after);after["auxiliary_proofs"]=before.GetDictOrEmpty("auxiliary_proofs").DeepCopy();after.GetDictOrEmpty("auxiliary_proofs")[receiptId]=proofHash;after.GetDictOrEmpty("receipts")[receiptId]=receipt;after["revision"]=before.GetInt("revision")+1;after["command_sequence"]=before.GetInt("command_sequence")+1;
            if(directBefore!=null)package["proofs"]=new GdDict();
            ((GdDict)package.Get("proofs"))[proofHash]=proof;package["current_owner"]=after;package["current_owner_digest"]=PaidHashContext.BitsV2.Hash(after);return package;
        }
        [TestCase(false)] [TestCase(true)] public void CompletedRackAndUtilityAdmitExactBenefitsAndRejectChangedEffect(bool utility)
        {
            var package=Fixture(true,out var binding,out _,utility);package=Complete(package,binding);Assert.IsTrue(CheckpointProofAdmission.TryAdmit(Own(package),binding,out var result,out string reason),reason);
            var owner=result.CopyOwner();var receipt=owner.GetDictOrEmpty("receipts").GetDictOrEmpty("auxiliary-proof-complete:complete");var effect=receipt.GetDictOrEmpty("result");
            Assert.AreEqual("completed",AuxiliaryServiceState.State(owner).GetDictOrEmpty("job").Get("status"));
            if(utility)Assert.AreEqual("auxiliary-proof-complete:complete",effect.GetDictOrEmpty("training_record").Get("commit_id"));else Assert.IsTrue(effect.GetDictOrEmpty("service_after").GetBool("released"));
            var changed=package.DeepCopy();var items=changed.GetDictOrEmpty("current_owner").GetDictOrEmpty("participating_state").GetDictOrEmpty("inventory").GetDictOrEmpty("items");string item=items.Keys.Cast<string>().First();items[item]=items.GetInt(item)+1;changed["current_owner_digest"]=PaidHashContext.BitsV2.Hash(changed.Get("current_owner"));Assert.IsFalse(CheckpointProofAdmission.TryAdmit(Own(changed),binding,out _,out _));
        }
        static GdDict Take(GdDict package,ProofResourceBinding binding)
        {
            var before=(GdDict)package.Get("current_owner");var capsule=Capsule(before,binding);var refs=new GdArray();foreach(string key in ((GdDict)package.Get("origins")).Keys.Cast<string>().OrderBy(k=>k,StringComparer.Ordinal))refs.Add(key);capsule["origin_refs"]=refs;
            string beforeDigest=PaidHashContext.BitsV2.Hash(capsule);((GdDict)package.Get("origins"))[beforeDigest]=capsule;
            string service=(string)AuxiliaryServiceState.State(before).GetDictOrEmpty("job").Get("service_id");const string receiptId="auxiliary-proof-take:take";
            var after=before.DeepCopy();after["schema_version"]=6L;after.Erase("auxiliary_proof_format");after.Erase("auxiliary_proofs");var computed=AuxiliaryServiceState.Apply(after,"aux_take",service);
            var command=new GdDict{{"command_id","take"},{"operation","aux_proof_take_v1"},{"run_id",package.Get("run_id")},{"actor_id",package.Get("actor_id")},{"service_id",service},{"completion_receipt_id","auxiliary-proof-complete:complete"},{"take_before_capsule_digest",beforeDigest},{"take_before_owner_digest",PaidHashContext.BitsV2.Hash(before)}};
            var effect=new GdDict{{"operation","aux_proof_take_v1"},{"service_id",service},{"completion_receipt_id","auxiliary-proof-complete:complete"},{"take_before_capsule_digest",beforeDigest},{"take_before_owner_digest",PaidHashContext.BitsV2.Hash(before)}};
            foreach(string key in new[]{"descriptor_sha256","service_before","inventory_before","progression_before","training_before","accepted","training_record","service_after","inventory_after","progression_after","training_after","job_before","job_after"})effect[key]=computed.Get(key);
            var receipt=new GdDict{{"schema_version",1L},{"transaction_id",receiptId},{"commit_id",receiptId},{"command_id","take"},{"command",command},{"command_hash",PaidHashContext.BitsV2.Hash(command)},{"revision",before.GetInt("revision")+1},{"result",effect}};
            Upgrade(after);after["auxiliary_proofs"]=before.GetDictOrEmpty("auxiliary_proofs").DeepCopy();after.GetDictOrEmpty("receipts")[receiptId]=receipt;after["revision"]=before.GetInt("revision")+1;after["command_sequence"]=before.GetInt("command_sequence")+1;package["current_owner"]=after;package["current_owner_digest"]=PaidHashContext.BitsV2.Hash(after);return package;
        }
        [Test] public void RackTakeAdmitsAllocatedRemainingOnceAndRetainsTerminalHistory()
        {
            var package=Fixture(true,out var binding,out _);package=Take(Complete(package,binding),binding);Assert.IsTrue(CheckpointProofAdmission.TryAdmit(Own(package),binding,out var result,out string reason),reason);
            var owner=result.CopyOwner();var take=owner.GetDictOrEmpty("receipts").GetDictOrEmpty("auxiliary-proof-take:take").GetDictOrEmpty("result");Assert.IsFalse(take.GetDictOrEmpty("accepted").IsEmpty);Assert.IsNull(take.Get("training_record"));Assert.IsTrue(PaidHashContext.BitsV2.Equal(take.Get("progression_before"),take.Get("progression_after")));
            var probe=owner.DeepCopy();probe["schema_version"]=6L;probe.Erase("auxiliary_proof_format");probe.Erase("auxiliary_proofs");Assert.Throws<ArgumentException>(()=>AuxiliaryServiceState.Apply(probe,"aux_take",(string)take.Get("service_id")));
            var bad=package.DeepCopy();bad.GetDictOrEmpty("current_owner").GetDictOrEmpty("receipts").Erase("auxiliary-proof-complete:complete");bad["current_owner_digest"]=PaidHashContext.BitsV2.Hash(bad.Get("current_owner"));Assert.IsFalse(CheckpointProofAdmission.TryAdmit(Own(bad),binding,out _,out _));
        }
        [Test] public void DerivedTerminalIdOverBoundAndMissingHistoricalProofRefuse()
        {
            var package=Complete(Fixture(true,out var binding,out _),binding);var owner=package.GetDictOrEmpty("current_owner");const string old="auxiliary-proof-complete:complete";string commandId=new string('x',256),id="auxiliary-proof-complete:"+commandId;
            var receipt=owner.GetDictOrEmpty("receipts").GetDictOrEmpty(old);receipt["command_id"]=commandId;receipt["transaction_id"]=id;receipt["commit_id"]=id;receipt.GetDictOrEmpty("command")["command_id"]=commandId;receipt["command_hash"]=PaidHashContext.BitsV2.Hash(receipt.Get("command"));
            string hash=(string)owner.GetDictOrEmpty("auxiliary_proofs").Get(old);owner.GetDictOrEmpty("receipts").Erase(old);owner.GetDictOrEmpty("receipts")[id]=receipt;owner.GetDictOrEmpty("auxiliary_proofs").Erase(old);owner.GetDictOrEmpty("auxiliary_proofs")[id]=hash;
            var proof=package.GetDictOrEmpty("proofs").GetDictOrEmpty(hash);proof["completion_receipt_id"]=id;proof["completion_receipt_digest"]=PaidHashContext.BitsV2.Hash(receipt);package["current_owner_digest"]=PaidHashContext.BitsV2.Hash(owner);Assert.IsFalse(CheckpointProofAdmission.TryAdmit(Own(package),binding,out _,out _));
            package=Complete(Fixture(true,out binding,out _),binding);var checkpointHash=package.GetDictOrEmpty("current_owner").GetDictOrEmpty("auxiliary_proofs").Get("auxiliary-proof-checkpoint:test");package.GetDictOrEmpty("proofs").Erase(checkpointHash);Assert.IsFalse(CheckpointProofAdmission.TryAdmit(Own(package),binding,out _,out _));
        }
        [TestCase("checkpoint")] [TestCase("complete")] [TestCase("take")] public void ShapeValidEconomicDriftCannotEscapeReconstructedOwner(string stage)
        {
            var package=Fixture(stage!="checkpoint",out var binding,out _);if(stage!="checkpoint")package=Complete(package,binding);if(stage=="take")package=Take(package,binding);
            Assert.IsTrue(CheckpointProofAdmission.TryAdmit(Own(package),binding,out _,out string reason),reason);
            var bad=package.DeepCopy();var p=bad.GetDictOrEmpty("current_owner").GetDictOrEmpty("participating_state");var inventory=p.GetDictOrEmpty("inventory");var items=inventory.GetDictOrEmpty("items");string item=items.Keys.Cast<string>().First();items[item]=items.GetInt(item)+1;
            var model=new InventoryState();Assert.IsTrue(model.ApplySummary(inventory));p["inventory"]=model.GetSummary();bad["current_owner_digest"]=PaidHashContext.BitsV2.Hash(bad.Get("current_owner"));Assert.IsFalse(CheckpointProofAdmission.TryAdmit(Own(bad),binding,out _,out reason));Assert.AreEqual("proof_owner_transition_not_conserved",reason);
            bad=package.DeepCopy();var xp=bad.GetDictOrEmpty("current_owner").GetDictOrEmpty("participating_state").GetDictOrEmpty("progression").GetDictOrEmpty("skill_xp");string skill=xp.Keys.Cast<string>().First();xp[skill]=xp.GetInt(skill)+1;bad["current_owner_digest"]=PaidHashContext.BitsV2.Hash(bad.Get("current_owner"));Assert.IsFalse(CheckpointProofAdmission.TryAdmit(Own(bad),binding,out _,out reason));Assert.AreEqual("proof_owner_transition_not_conserved",reason);
        }
        [Test] public void IndividuallyValidSiblingTakesFromSameBeforeCannotBecomeOneOwnerHistory()
        {
            var package=Take(Complete(Fixture(true,out var binding,out _),binding),binding);Assert.IsTrue(CheckpointProofAdmission.TryAdmit(Own(package),binding,out _,out string reason),reason);
            var owner=package.GetDictOrEmpty("current_owner");var sibling=owner.GetDictOrEmpty("receipts").GetDictOrEmpty("auxiliary-proof-take:take").DeepCopy();const string id="auxiliary-proof-take:sibling";sibling["command_id"]="sibling";sibling["transaction_id"]=id;sibling["commit_id"]=id;sibling.GetDictOrEmpty("command")["command_id"]="sibling";sibling["command_hash"]=PaidHashContext.BitsV2.Hash(sibling.Get("command"));owner.GetDictOrEmpty("receipts")[id]=sibling;package["current_owner_digest"]=PaidHashContext.BitsV2.Hash(owner);Assert.IsFalse(CheckpointProofAdmission.TryAdmit(Own(package),binding,out _,out reason));Assert.AreEqual("proof_owner_transition_not_conserved",reason);
        }
        [Test] public void IndependentlyValidLegacyPaidProgressCannotBeConvertedAtProofCompletion()
        {
            var package=Fixture(true,out var binding,out var origin);var legacy=origin.DeepCopy();string service=(string)AuxiliaryServiceState.State(legacy).GetDictOrEmpty("job").Get("service_id");
            var effect=AuxiliaryServiceState.Apply(legacy,"aux_progress",service,.125,.125,1,100,99);
            var command=new GdDict{{"command_id","oldwork"},{"operation","aux_progress"},{"run_id",package.Get("run_id")},{"actor_id",package.Get("actor_id")},{"service_id",service},{"delta_seconds",.125},{"elapsed_seconds",.125},{"speed",1.0},{"stamina_before",100.0},{"stamina_after",99.0},{"reason",""}};
            const string id="auxiliary:oldwork";legacy["revision"]=origin.GetInt("revision")+1;legacy["command_sequence"]=origin.GetInt("command_sequence")+1;legacy.GetDictOrEmpty("receipts")[id]=new GdDict{{"schema_version",1L},{"transaction_id",id},{"commit_id",id},{"command_id","oldwork"},{"command",command},{"command_hash",PaidHashContext.BitsV2.Hash(command)},{"revision",legacy.Get("revision")},{"result",effect}};
            Assert.IsTrue(DomainBundle.TryCreate(legacy,out _,out string reason),reason);package=Complete(package,binding,legacy);Assert.IsFalse(CheckpointProofAdmission.TryAdmit(Own(package),binding,out _,out reason));Assert.AreEqual("legacy_auxiliary_progress_not_convertible",reason);
        }
        [Test] public void ResourceReplacementAndReciprocalReceiptTamperingRefuse()
        {
            var package=Fixture(false,out var binding,out _);var proof=(GdDict)((GdDict)package.Get("proofs")).Values.First();proof["checkpoint_receipt_digest"]=new string('a',64);Assert.IsFalse(CheckpointProofAdmission.TryAdmit(Own(package),binding,out _,out _));
            ResourceAuthorityPublication.Invalidate();Assert.IsFalse(CheckpointProofAdmission.TryAdmit(Own(package),binding,out _,out _));
        }
    }
}
