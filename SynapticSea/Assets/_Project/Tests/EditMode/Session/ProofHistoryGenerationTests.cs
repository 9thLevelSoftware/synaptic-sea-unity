using System;
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
    public sealed class ProofHistoryGenerationTests : InfraDataTestBase
    {
        static GdDict Fixture(bool ready,out ProofResourceBinding binding,out GdDict origin)
        {
            return new CheckpointProofAdmissionTests().Fixture(ready,out binding,out origin);
        }
        static GdDict Complete(GdDict package,ProofResourceBinding binding,GdDict before)=>CheckpointProofAdmissionTests.Complete(package,binding,before);
        static CheckpointProofAdmission.Result Admit(GdDict package,ProofResourceBinding binding)
        {
            var outer=new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",ComponentDomainCodec.Encode(package,ComponentDomainCodec.BitExactSchema)}};
            Assert.IsTrue(ProofPackageCodec.TryOwnEnvelope(outer,out var input,out string reason),reason);Assert.IsTrue(CheckpointProofAdmission.TryAdmit(input,binding,out var result,out reason),reason);return result;
        }
        static GdDict NextCheckpoint(GdDict parent,bool adopted)
        {
            var package=parent.DeepCopy();var oldOwner=package.GetDictOrEmpty("current_owner");var prior=package.GetDictOrEmpty("proofs").Values.OfType<GdDict>().Single();
            var origin=package.GetDictOrEmpty("origins").GetDictOrEmpty((string)prior.Get("start_origin_capsule_digest")).GetDictOrEmpty("owner");var before=adopted?oldOwner:origin;
            string beforeDigest=(string)prior.Get("start_origin_capsule_digest");
            if(adopted)
            {
                var refs=GdArray.Of(beforeDigest);var capsule=new GdDict{{"capsule_version",1L},{"owner_profile",AuxiliaryProofOwnerProfile.Profile},{"run_id",package.Get("run_id")},{"actor_id",package.Get("actor_id")},{"owner_revision",before.Get("revision")},{"command_sequence",before.Get("command_sequence")},{"resource_digest",package.Get("current_resource_digest")},{"owner",before.DeepCopy()},{"origin_refs",refs}};
                beforeDigest=PaidHashContext.BitsV2.Hash(capsule);package.GetDictOrEmpty("origins")[beforeDigest]=capsule;
            }
            var proof=prior.DeepCopy();var job=prior.GetDictOrEmpty("job_after");double duration=AuxiliaryServiceState.State(origin).GetDictOrEmpty("descriptors").GetDictOrEmpty((string)prior.Get("service_id")).GetFloat("required_seconds");long count=prior.GetInt("accepted_steps");
            var runtime=new AuxiliaryWorkRuntime("owner",(string)package.Get("run_id"),(string)package.Get("actor_id"),(string)prior.Get("service_id"),0,PaidHashContext.BitsV2.Algorithm,duration,job.GetFloat("progress_seconds"),job.GetFloat("eligible_seconds"),100,count,256);
            var result=runtime.Step(new AuxiliaryWorkFrame(.125,100,100,1));Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted,result.Status);var s=result.AcceptedStep;
            var row=new GdDict{{"sequence",s.Sequence},{"delta_seconds",s.RequestedDelta},{"stamina_before",s.StaminaBefore},{"max_stamina",s.MaxStamina},{"wound_work_multiplier",s.WoundSpeed},{"ratio",Math.Max(0,Math.Min(1,s.StaminaBefore/Math.Max(1,s.MaxStamina)))},{"speed",s.Speed},{"remaining_before",duration-s.ProgressBefore},{"elapsed_seconds",s.ElapsedSeconds},{"delta_progress",s.DeltaSeconds},{"stamina_after",s.StaminaAfter},{"progress_after",s.ProgressAfter},{"eligible_after",s.EligibleAfter}};
            var chunks=(GdArray)proof.Get("chunks");var chunk=new GdDict{{"chunk_version",1L},{"ordinal",(long)chunks.Count},{"previous_chunk_digest",proof.Get("latest_chunk_digest")},{"initial_step_sequence",s.Sequence},{"progress_before",s.ProgressBefore},{"eligible_before",s.EligibleBefore},{"accepted_steps_before",count},{"steps",GdArray.Of(row)},{"progress_after",s.ProgressAfter},{"eligible_after",s.EligibleAfter},{"accepted_steps_after",count+1}};string digest=PaidHashContext.BitsV2.Hash(chunk);chunk["digest"]=digest;chunks.Add(chunk);
            var nextJob=job.DeepCopy();nextJob["progress_seconds"]=s.ProgressAfter;nextJob["eligible_seconds"]=s.EligibleAfter;proof["job_after"]=nextJob;proof["latest_chunk_digest"]=digest;proof["accepted_steps"]=count+1;proof["cut_work_version"]=count+1;proof["checkpoint_before_capsule_digest"]=beforeDigest;proof["checkpoint_before_owner_digest"]=PaidHashContext.BitsV2.Hash(before);
            const string id="auxiliary-proof-checkpoint:next";proof["checkpoint_receipt_id"]=id;string hash=AuxiliaryProofOwnerProfile.ProofDigest(proof);
            var command=oldOwner.GetDictOrEmpty("receipts").GetDictOrEmpty("auxiliary-proof-checkpoint:test").GetDictOrEmpty("command").DeepCopy();command["command_id"]="next";command["proof_digest"]=hash;
            foreach(string key in new[]{"checkpoint_before_capsule_digest","checkpoint_before_owner_digest","accepted_steps","cut_work_version"})command[key]=proof.Get(key);
            var effect=oldOwner.GetDictOrEmpty("receipts").GetDictOrEmpty("auxiliary-proof-checkpoint:test").GetDictOrEmpty("result").DeepCopy();foreach(string key in new[]{"checkpoint_before_capsule_digest","checkpoint_before_owner_digest","accepted_steps","cut_work_version"})effect[key]=proof.Get(key);effect["proof_digest"]=hash;effect["before_owner_revision"]=before.Get("revision");effect["before_command_sequence"]=before.Get("command_sequence");effect["job_before"]=AuxiliaryServiceState.State(before).Get("job");effect["job_after"]=nextJob;
            var receipt=new GdDict{{"schema_version",1L},{"transaction_id",id},{"commit_id",id},{"command_id","next"},{"command",command},{"command_hash",PaidHashContext.BitsV2.Hash(command)},{"revision",before.GetInt("revision")+1},{"result",effect}};proof["checkpoint_receipt_digest"]=PaidHashContext.BitsV2.Hash(receipt);
            var owner=before.DeepCopy();owner["schema_version"]=7L;owner["feature_schema"]=5L;owner["hash_algorithm"]=PaidHashContext.BitsV2.Algorithm;owner["auxiliary_proof_format"]=1L;if(!adopted)owner["auxiliary_proofs"]=new GdDict();owner["revision"]=before.GetInt("revision")+1;owner["command_sequence"]=before.GetInt("command_sequence")+1;AuxiliaryServiceState.State(owner)["job"]=nextJob;owner.GetDictOrEmpty("receipts")[id]=receipt;owner.GetDictOrEmpty("auxiliary_proofs")[id]=hash;
            if(!adopted)package["proofs"]=new GdDict();package.GetDictOrEmpty("proofs")[hash]=proof;package["current_owner"]=owner;package["current_owner_digest"]=PaidHashContext.BitsV2.Hash(owner);return package;
        }
        [Test] public void RepeatedDetachedBranchRoundtripsAndOfflineOmissionNeedsOutputSourceBinding()
        {
            var package=Fixture(false,out var binding,out var origin);var parent=Admit(package,binding);var childPackage=NextCheckpoint(package,false);var child=Admit(childPackage,binding);
            Assert.IsTrue(ProofGenerationComparison.TryCompare(parent,child,out var compatible,out string reason),reason);Assert.IsTrue(compatible.RequiresOutputSourceBinding);Assert.AreEqual(PaidHashContext.BitsV2.Hash(origin),compatible.RequiredBeforeOwnerDigest);Assert.AreEqual("auxiliary-proof-checkpoint:test",compatible.OmittedCheckpointAt(0));
            Assert.IsTrue(PaidSnapshotCodec.TryCreateProofEnvelope(child,false,out var envelope,out reason),reason);Assert.IsTrue(PaidSnapshotCodec.TryDecodeProofEnvelope(envelope,binding,false,out var loaded,out reason),reason);Assert.IsTrue(ProofGenerationComparison.TryCompare(parent,loaded,out _,out reason),reason);
            Assert.IsFalse(ProofGenerationComparison.TryCompare(child,parent,out _,out _),"Exact retained prefix cannot regress even at equal paid revision");
            Assert.IsTrue(AdmittedAuxiliaryHistory.TryIssueResume(loaded,out var history,out reason),reason);Assert.AreEqual(0,history.OriginalSeed.Progress);Assert.AreEqual(0,history.OriginalSeed.AcceptedSteps);Assert.AreEqual(2,history.AcceptedSteps);Assert.AreEqual(2,history.ChunkCount);Assert.AreEqual(2,history.ChunkAt(1).At(0).Sequence);Assert.AreEqual(.25,history.Progress);Assert.AreEqual(2,history.CutWorkVersion);
            var escaped=history.CopyOriginalOwner();escaped["revision"]=999L;Assert.AreNotEqual(999,history.CopyOriginalOwner().GetInt("revision"));
        }
        [Test] public void AdoptedCheckpointCannotDisappearFromFutureCanonicalTransition()
        {
            var package=Fixture(false,out var binding,out _);var parent=Admit(package,binding);var childPackage=NextCheckpoint(package,true);var child=Admit(childPackage,binding);Assert.IsTrue(ProofGenerationComparison.TryCompare(parent,child,out var compatible,out _));Assert.IsFalse(compatible.RequiresOutputSourceBinding);
            var bad=childPackage.DeepCopy();var owner=bad.GetDictOrEmpty("current_owner");owner.GetDictOrEmpty("receipts").Erase("auxiliary-proof-checkpoint:test");owner.GetDictOrEmpty("auxiliary_proofs").Erase("auxiliary-proof-checkpoint:test");bad["current_owner_digest"]=PaidHashContext.BitsV2.Hash(owner);var wire=new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",ComponentDomainCodec.Encode(bad,ComponentDomainCodec.BitExactSchema)}};Assert.IsTrue(ProofPackageCodec.TryOwnEnvelope(wire,out var input,out _));Assert.IsFalse(CheckpointProofAdmission.TryAdmit(input,binding,out _,out _));
        }
        [Test] public void IndividuallyAdmittedTerminalForkCannotReplaceStableParentPaidReceipt()
        {
            var package=Fixture(true,out var binding,out var origin);var completedPackage=Complete(package,binding,origin);var parent=Admit(completedPackage,binding);var fork=completedPackage.DeepCopy();var owner=fork.GetDictOrEmpty("current_owner");const string oldId="auxiliary-proof-complete:complete",newId="auxiliary-proof-complete:fork";
            var receipt=owner.GetDictOrEmpty("receipts").GetDictOrEmpty(oldId);receipt["command_id"]="fork";receipt["transaction_id"]=newId;receipt["commit_id"]=newId;receipt.GetDictOrEmpty("command")["command_id"]="fork";receipt["command_hash"]=PaidHashContext.BitsV2.Hash(receipt.Get("command"));
            string hash=(string)owner.GetDictOrEmpty("auxiliary_proofs").Get(oldId);owner.GetDictOrEmpty("receipts").Erase(oldId);owner.GetDictOrEmpty("receipts")[newId]=receipt;owner.GetDictOrEmpty("auxiliary_proofs").Erase(oldId);owner.GetDictOrEmpty("auxiliary_proofs")[newId]=hash;var proof=fork.GetDictOrEmpty("proofs").GetDictOrEmpty(hash);proof["completion_receipt_id"]=newId;proof["completion_receipt_digest"]=PaidHashContext.BitsV2.Hash(receipt);fork["current_owner_digest"]=PaidHashContext.BitsV2.Hash(owner);
            var child=Admit(fork,binding);Assert.AreEqual(parent.CopyOwner().GetInt("revision"),child.CopyOwner().GetInt("revision"));Assert.IsFalse(ProofGenerationComparison.TryCompare(parent,child,out _,out string reason));Assert.AreEqual("stable_paid_receipt_missing",reason);
        }
        [Test] public void FreshStartRequiresGrantedFreshOrdinaryOwnerAndEpochDoesNotSurviveRevoke()
        {
            Fixture(false,out var binding,out var origin);Assert.IsTrue(DomainBundle.TryCreateWorkerInput(origin,out var input,out string reason),reason);
            var paid=PaidCraftingState.State(origin);var epoch=new WorkerAdmissionEpoch("history-test",(string)paid.Get("run_id"),(string)paid.Get("actor_id"));var basis=new WorkerAdmissionState(epoch,1,2,3,4);var slot=new WorkerAdmissionSlot(basis);var queue=new WorkerOriginAdmissionQueue(slot);
            Assert.IsTrue(queue.BeginOrigin(input,binding.Lease,basis,out var job,out reason),reason);Assert.IsTrue(job.WaitForDiagnosticTest(30000));Assert.IsTrue(queue.TryTake(job,out var outcome));Assert.AreEqual(WorkerAdmissionStatus.Accepted,outcome.Status,outcome.Reason);
            Assert.IsTrue(AdmittedAuxiliaryHistory.TryIssueFresh(outcome.Origin,out var history,out reason),reason);Assert.AreEqual(0,history.AcceptedSteps);Assert.AreEqual(0,history.ChunkCount);Assert.AreEqual("history-test",history.SourceSessionId);Assert.IsTrue(history.ResourcesCurrent);epoch.Revoke();Assert.IsFalse(history.ResourcesCurrent);Assert.IsFalse(AdmittedAuxiliaryHistory.TryIssueFresh(outcome.Origin,out _,out _));
            Assert.IsFalse(AdmittedAuxiliaryHistory.TryIssueFresh(null,out _,out _));
        }
        [Test] public void ResourceRevocationRefusesComparisonHistoryAndTerminalInstallation()
        {
            var package=Fixture(true,out var binding,out var origin);var checkpoint=Admit(package,binding);Assert.IsTrue(AdmittedAuxiliaryHistory.TryIssueResume(checkpoint,out var history,out _));Assert.IsTrue(DomainBundle.TryCreate(origin,out var baseline,out _));var complete=Admit(Complete(package,binding,origin),binding);Assert.IsTrue(PreparedProofOwnerReplacement.TryPrepare(complete,baseline,out var candidate,out _));ResourceAuthorityPublication.Invalidate();
            Assert.IsFalse(history.ResourcesCurrent);Assert.IsFalse(AdmittedAuxiliaryHistory.TryIssueResume(checkpoint,out _,out _));Assert.IsFalse(ProofGenerationComparison.TryCompare(checkpoint,checkpoint,out _,out _));
            using(var attempt=CommonParticipantGate.BeginAttempt()){Assert.IsFalse(candidate.TryAuthorizeUnderGate(baseline,attempt));Assert.Throws<InvalidOperationException>(()=>candidate.ConsumeUnderGate(baseline,attempt));}
        }
        [Test] public void TerminalOwnerReplacementIsExactOneUseAndRefusesDetachedCheckpointAdoption()
        {
            var package=Fixture(true,out var binding,out var origin);var checkpoint=Admit(package,binding);Assert.IsTrue(DomainBundle.TryCreate(origin,out var baseline,out string reason),reason);Assert.IsFalse(PreparedProofOwnerReplacement.TryPrepare(checkpoint,baseline,out _,out _));
            var completed=Admit(Complete(package,binding,origin),binding);Assert.IsFalse(AdmittedAuxiliaryHistory.TryIssueResume(completed,out _,out _));Assert.IsTrue(PreparedProofOwnerReplacement.TryPrepare(completed,baseline,out var prepared,out reason),reason);
            using(var attempt=CommonParticipantGate.BeginAttempt())
            {
                Assert.IsTrue(prepared.TryAuthorizeUnderGate(baseline,attempt));var replacement=prepared.ConsumeUnderGate(baseline,attempt);Assert.AreEqual(5,replacement.FeatureSchema);Assert.IsFalse(prepared.TryAuthorizeUnderGate(baseline,attempt));Assert.Throws<InvalidOperationException>(()=>prepared.ConsumeUnderGate(baseline,attempt));Assert.AreSame(completed,prepared.SourcePackage);
            }
            Assert.IsFalse(DomainBundle.TryCreate(completed.CopyOwner(),out _,out _));
        }
    }
}
