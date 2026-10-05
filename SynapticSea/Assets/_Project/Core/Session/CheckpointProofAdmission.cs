using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Closed fresh global admission for checkpoint, completion and take proof families.
    internal sealed class CheckpointProofAdmission
    {
        [ThreadStatic] static CheckpointProofAdmission _current;
        readonly GdDict _package,_proofs;
        readonly ProofResourceBinding _binding;
        readonly Dictionary<string,DomainBundle> _origins=new Dictionary<string,DomainBundle>(StringComparer.Ordinal);
        readonly Dictionary<string,Witness> _witnesses=new Dictionary<string,Witness>(StringComparer.Ordinal);
        readonly object _seal=new object();
        string _activeOwnerHash;
        Witness _self;
        sealed class Witness
        {
            internal readonly string ReceiptId,ReceiptHash,ProofHash,OwnerHash;
            internal readonly GdDict Owner;
            internal DomainBundle FullyAdmitted;
            internal Witness(string id,string receiptHash,string proofHash,GdDict owner)
            {ReceiptId=id;ReceiptHash=receiptHash;ProofHash=proofHash;Owner=owner.DeepCopy();OwnerHash=PaidHashContext.BitsV2.Hash(owner);}
        }
        internal sealed class Result
        {
            readonly DomainBundle _owner;readonly GdDict _package;
            internal readonly ResourceAuthorityLease Lease;
            internal Result(CheckpointProofAdmission issuer,object seal,DomainBundle owner)
            {
                if(issuer==null||!ReferenceEquals(seal,issuer._seal)||!ReferenceEquals(_current,issuer)||owner==null||issuer._activeOwnerHash!=PaidHashContext.BitsV2.Hash(owner.GetSummary()))throw new InvalidOperationException("checkpoint_issuer_mismatch");
                _owner=owner;_package=issuer._package.DeepCopy();Lease=issuer._binding.Lease;
            }
            // Immutable handle is consumed only by the closed replacement factory; never a mutable summary escape.
            internal DomainBundle OwnedDomainForReplacement=>_owner;
            internal GdDict CopyPackage()=>_package.DeepCopy();
            internal GdDict CopyOwner()=>_owner.GetSummary();
        }
        CheckpointProofAdmission(GdDict package,ProofResourceBinding binding){_package=package.DeepCopy();_proofs=(GdDict)_package.Get("proofs");_binding=binding;}
        internal static bool HasClosedContext=>_current!=null;
        internal static bool AcceptsExactOwner(GdDict summary)=>_current!=null&&_current._activeOwnerHash!=null&&AuxiliaryProofOwnerProfile.IsBinding(summary)&&PaidHashContext.BitsV2.Hash(summary)==_current._activeOwnerHash;
        internal static bool TryAdmit(OwnedProofPackageInput input,ProofResourceBinding binding,out Result result,out string reason)
        {
            result=null;reason="nested_proof_package_context";if(_current!=null)return false;
            if(!ProofPackageGraphPlan.TryPrepare(input,binding,out var plan,out reason))return false;
            var context=new CheckpointProofAdmission(plan.CopyPackage(),binding);_current=context;
            try
            {
                using(new PinnedAdmissionResourceScope(binding.Lease))
                {
                    for(int i=0;i<plan.OriginCount;i++)
                    {
                        if(!binding.Lease.IsCurrent){reason="resource_epoch_changed";return false;}
                        var capsule=plan.CopyCapsuleAt(i);if(!context.AdmitOwner((GdDict)capsule.Get("owner"),out var admitted,out reason))return false;
                        context._origins.Add(plan.OriginDigestAt(i),admitted);
                    }
                    if(!context.AdmitOwner((GdDict)context._package.Get("current_owner"),out var owner,out reason))return false;
                    if(!binding.Lease.IsCurrent){reason="resource_epoch_changed";return false;}
                    context._activeOwnerHash=PaidHashContext.BitsV2.Hash(owner.GetSummary());result=new Result(context,context._seal,owner);reason="checkpoint_package_fully_admitted";return true;
                }
            }
            catch(UnsupportedWorkerPortException e){reason="unsupported_worker_port:"+e.Port;return false;}
            catch(InvalidOperationException e){reason=e.Message.StartsWith("undeclared_resource",StringComparison.Ordinal)?e.Message:"proof_package_admission_failed";return false;}
            catch(ArgumentException){reason="invalid_checkpoint_package";return false;}catch(OverflowException){reason="checkpoint_counter_overflow";return false;}
            finally{_current=null;}
        }
        bool AdmitOwner(GdDict owner,out DomainBundle admitted,out string reason)
        {
            admitted=null;reason="invalid_checkpoint_history";
            if(owner.Get("receipts") is GdDict receipts)
                foreach(var entry in receipts.OrderBy(p=>(p.Value as GdDict)?.GetInt("revision")??0))
                {
                    if(!(entry.Value is GdDict receipt))return false;string operation=(receipt.GetDictOrEmpty("result").Get("operation") as string);
                    if(AuxiliaryProofOwnerProfile.IsNewReceipt(operation))
                    {
                        if(operation=="aux_proof_checkpoint_v1") { if(!PrepareCheckpoint(owner,receipt,(string)entry.Key,out reason))return false; }
                        else if(!PrepareTerminal(owner,receipt,(string)entry.Key,out reason))return false;
                    }
                }
            if(owner.GetInt("schema_version")==7)
            {
                if(!(owner.Get("auxiliary_proofs") is GdDict map))return false;
                int count=owner.GetDictOrEmpty("receipts").Values.OfType<GdDict>().Count(r=>AuxiliaryProofOwnerProfile.IsNewReceipt((r.GetDictOrEmpty("result").Get("operation") as string)) && r.GetDictOrEmpty("result").Get("operation") as string!="aux_proof_take_v1");
                if(map.Count!=count)return false;
                foreach(var entry in map)if(!(entry.Key is string id)||!(entry.Value is string digest)||!_witnesses.TryGetValue(id,out var witness)||witness.FullyAdmitted==null||witness.ProofHash!=digest)return false;
            }
            _activeOwnerHash=PaidHashContext.BitsV2.Hash(owner);
            if(owner.GetInt("schema_version")==7 && _witnesses.Count>0)
            {
                var ordered=owner.GetDictOrEmpty("receipts").Where(p=>p.Value is GdDict).OrderByDescending(p=>((GdDict)p.Value).GetInt("revision")).ToArray();
                if(ordered.Length==0||!_witnesses.TryGetValue((string)ordered[0].Key,out var terminal)||terminal.FullyAdmitted==null||terminal.OwnerHash!=_activeOwnerHash)
                {reason="proof_owner_transition_not_conserved";return false;}
            }
            if(!DomainBundle.TryCreate(owner,out admitted,out reason))return false;
            var paid=PaidCraftingState.State(owner);
            if(paid.Get("run_id") as string!=_package.Get("run_id") as string||paid.Get("actor_id") as string!=_package.Get("actor_id") as string){admitted=null;reason="proof_owner_identity_mismatch";return false;}
            return true;
        }
        static bool Text(object value)=>value is string s&&s.Length>0&&s.Length<=256&&!string.IsNullOrWhiteSpace(s);
        static readonly string[] ReceiptKeys={"schema_version","transaction_id","commit_id","command_id","command","command_hash","revision","result"};
        static readonly string[] CommandKeys={"command_id","operation","run_id","actor_id","service_id","work_lineage_id","checkpoint_before_capsule_digest","checkpoint_before_owner_digest","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest","proof_digest","accepted_steps","cut_work_version"};
        static readonly string[] EffectKeys={"operation","service_id","work_lineage_id","proof_digest","accepted_steps","cut_work_version","before_owner_revision","before_command_sequence","job_before","job_after","checkpoint_before_capsule_digest","checkpoint_before_owner_digest","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest"};
        bool PrepareCheckpoint(GdDict containingOwner,GdDict receipt,string id,out string reason)
        {
            reason="invalid_proof_checkpoint_receipt";
            if(_witnesses.TryGetValue(id,out var prior))return prior.FullyAdmitted!=null&&prior.ReceiptHash==PaidHashContext.BitsV2.Hash(receipt);
            if(!AuxiliaryProofOwnerProfile.Exact(receipt,ReceiptKeys)||!(receipt.Get("schema_version") is long version)||version!=1||!Text(receipt.Get("command_id"))||!Text(id)||
                id!="auxiliary-proof-checkpoint:"+(string)receipt.Get("command_id")||receipt.Get("transaction_id") as string!=id||receipt.Get("commit_id") as string!=id||
                !(receipt.Get("command") is GdDict command)||!AuxiliaryProofOwnerProfile.Exact(command,CommandKeys)||command.Get("command_id") as string!=receipt.Get("command_id") as string||
                command.Get("operation") as string!="aux_proof_checkpoint_v1"||receipt.Get("command_hash") as string!=PaidHashContext.BitsV2.Hash(command)||
                !(receipt.Get("result") is GdDict effect)||!AuxiliaryProofOwnerProfile.Exact(effect,EffectKeys))return false;
            if(!(containingOwner.Get("auxiliary_proofs") is GdDict map)||!(map.Get(id) is string proofHash)||!(_proofs.Get(proofHash) is GdDict proof)||
                !AuxiliaryProofOwnerProfile.ProofKeys(proof)||proof.Get("state") as string=="completed"||AuxiliaryProofOwnerProfile.ProofDigest(proof)!=proofHash||
                proof.Get("checkpoint_receipt_id") as string!=id||proof.Get("checkpoint_receipt_digest") as string!=PaidHashContext.BitsV2.Hash(receipt))return false;
            if(!_origins.TryGetValue(proof.Get("start_origin_capsule_digest") as string??"",out var start)||!_origins.TryGetValue(proof.Get("checkpoint_before_capsule_digest") as string??"",out var beforeBundle))return false;
            var origin=start.GetSummary();var before=beforeBundle.GetSummary();
            if(proof.Get("start_origin_owner_digest") as string!=PaidHashContext.BitsV2.Hash(origin)||proof.Get("checkpoint_before_owner_digest") as string!=PaidHashContext.BitsV2.Hash(before)||
                !ProofPackageArithmetic.TryReplay(origin,proof,out _,out reason))return false;
            foreach(string key in new[]{"service_id","work_lineage_id","checkpoint_before_capsule_digest","checkpoint_before_owner_digest","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest","accepted_steps","cut_work_version"})
                if(!PaidHashContext.BitsV2.Equal(command.Get(key),proof.Get(key)))return false;
            if(command.Get("proof_digest") as string!=proofHash||command.Get("run_id") as string!=_package.Get("run_id") as string||command.Get("actor_id") as string!=_package.Get("actor_id") as string)return false;
            string service=proof.Get("service_id") as string;var beforeJob=AuxiliaryServiceState.State(before).GetDictOrEmpty("job");
            if(beforeJob.Get("service_id") as string!=service||(beforeJob.Get("status") as string)=="completed")return false;
            if(before.GetInt("schema_version")==6&&(!AuxiliaryServiceState.Latest(before,service,"aux_progress").IsEmpty||beforeJob.GetFloat("progress_seconds")!=0))return false;
            if(!PrefixPreserved(before,proof,service))return false;
            long revision=checked(before.GetInt("revision")+1),sequence=checked(before.GetInt("command_sequence")+1);
            if(!(receipt.Get("revision") is long declaredRevision)||declaredRevision!=revision)return false;
            var expectedEffect=new GdDict{{"operation","aux_proof_checkpoint_v1"},{"service_id",service},{"work_lineage_id",proof.Get("work_lineage_id")},{"proof_digest",proofHash},{"accepted_steps",proof.Get("accepted_steps")},{"cut_work_version",proof.Get("cut_work_version")},
                {"before_owner_revision",before.Get("revision")},{"before_command_sequence",before.Get("command_sequence")},{"job_before",beforeJob.DeepCopy()},{"job_after",((GdDict)proof.Get("job_after")).DeepCopy()}};
            foreach(string key in new[]{"checkpoint_before_capsule_digest","checkpoint_before_owner_digest","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest"})expectedEffect[key]=proof.Get(key);
            if(!PaidHashContext.BitsV2.Equal(expectedEffect,effect))return false;
            var expected=before.DeepCopy();expected["schema_version"]=7L;expected["feature_schema"]=5L;expected["hash_algorithm"]=PaidHashContext.BitsV2.Algorithm;expected["auxiliary_proof_format"]=1L;
            if(!(expected.Get("auxiliary_proofs") is GdDict))expected["auxiliary_proofs"]=new GdDict();
            expected["revision"]=revision;expected["command_sequence"]=sequence;AuxiliaryServiceState.State(expected)["job"]=((GdDict)proof.Get("job_after")).DeepCopy();
            expected.GetDictOrEmpty("receipts")[id]=receipt.DeepCopy();expected.GetDictOrEmpty("auxiliary_proofs")[id]=proofHash;
            if(!ConservedReceiptSuffix(before,expected,receipt,id,proofHash)){reason="proof_transition_not_conserved";return false;}
            var witness=new Witness(id,PaidHashContext.BitsV2.Hash(receipt),proofHash,expected);
            string oldHash=_activeOwnerHash;var oldSelf=_self;_self=witness;_activeOwnerHash=witness.OwnerHash;
            try
            {
                if(!DomainBundle.TryCreate(expected,out var fully,out reason))return false;
                witness.FullyAdmitted=fully;_witnesses.Add(id,witness);return true;
            }
            finally{_self=oldSelf;_activeOwnerHash=oldHash;}
        }
        static readonly string[] CompleteCommandKeys={"command_id","operation","run_id","actor_id","service_id","work_lineage_id","cut_work_version","accepted_steps","proof_digest","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest","terminal_before_capsule_digest","terminal_before_owner_digest"};
        static readonly string[] CompleteEffectKeys={"operation","service_id","work_lineage_id","cut_work_version","accepted_steps","proof_digest","start_origin_capsule_digest","start_origin_owner_digest","origin_receipt_id","origin_receipt_digest","terminal_before_capsule_digest","terminal_before_owner_digest","descriptor_sha256","job_before","service_before","inventory_before","progression_before","training_before","accepted","training_record","job_after","service_after","inventory_after","progression_after","training_after"};
        static readonly string[] TakeCommandKeys={"command_id","operation","run_id","actor_id","service_id","completion_receipt_id","take_before_capsule_digest","take_before_owner_digest"};
        static readonly string[] TakeEffectKeys={"operation","service_id","completion_receipt_id","take_before_capsule_digest","take_before_owner_digest","descriptor_sha256","service_before","inventory_before","progression_before","training_before","accepted","training_record","service_after","inventory_after","progression_after","training_after","job_before","job_after"};
        bool PrepareTerminal(GdDict containingOwner,GdDict receipt,string id,out string reason)
        {
            reason="invalid_proof_terminal_receipt";
            if(_witnesses.TryGetValue(id,out var prior))return prior.FullyAdmitted!=null&&prior.ReceiptHash==PaidHashContext.BitsV2.Hash(receipt);
            if(!AuxiliaryProofOwnerProfile.Exact(receipt,ReceiptKeys)||!(receipt.Get("schema_version") is long version)||version!=1||!Text(receipt.Get("command_id"))||!Text(id)||
                !(receipt.Get("command") is GdDict command)||!(receipt.Get("result") is GdDict effect))return false;
            string operation=command.Get("operation") as string;bool complete=operation=="aux_proof_complete_v1";
            if(!complete&&operation!="aux_proof_take_v1"||!AuxiliaryProofOwnerProfile.Exact(command,complete?CompleteCommandKeys:TakeCommandKeys)||!AuxiliaryProofOwnerProfile.Exact(effect,complete?CompleteEffectKeys:TakeEffectKeys))return false;
            if(id!=(complete?"auxiliary-proof-complete:":"auxiliary-proof-take:")+receipt.Get("command_id")||receipt.Get("transaction_id") as string!=id||receipt.Get("commit_id") as string!=id||command.Get("command_id") as string!=receipt.Get("command_id") as string||receipt.Get("command_hash") as string!=PaidHashContext.BitsV2.Hash(command)||
                command.Get("run_id") as string!=_package.Get("run_id") as string||command.Get("actor_id") as string!=_package.Get("actor_id") as string)return false;
            string service=command.Get("service_id") as string;if(!Text(service))return false;
            string beforeKey=complete?"terminal_before_capsule_digest":"take_before_capsule_digest",beforeOwnerKey=complete?"terminal_before_owner_digest":"take_before_owner_digest";
            if(!_origins.TryGetValue(command.Get(beforeKey) as string??"",out var beforeBundle))return false;
            var before=beforeBundle.GetSummary();if(command.Get(beforeOwnerKey) as string!=PaidHashContext.BitsV2.Hash(before))return false;
            GdDict proof=null;string proofHash="";
            if(complete)
            {
                if(before.GetInt("schema_version")==6 && (!AuxiliaryServiceState.Latest(before,service,"aux_progress").IsEmpty || AuxiliaryServiceState.State(before).GetDictOrEmpty("job").GetFloat("progress_seconds")!=0))
                {reason="legacy_auxiliary_progress_not_convertible";return false;}
                proofHash=command.Get("proof_digest") as string;
                if(!(containingOwner.GetDictOrEmpty("auxiliary_proofs").Get(id) is string declared)||declared!=proofHash||!(_proofs.Get(proofHash??"") is GdDict found)||!AuxiliaryProofOwnerProfile.ProofKeys(found)||found.Get("state") as string!="completed"||AuxiliaryProofOwnerProfile.ProofDigest(found)!=proofHash)return false;
                proof=found;
                if(proof.Get("completion_receipt_id") as string!=id||proof.Get("completion_receipt_digest") as string!=PaidHashContext.BitsV2.Hash(receipt)||!_origins.TryGetValue(proof.Get("start_origin_capsule_digest") as string??"",out var start)||proof.Get("start_origin_owner_digest") as string!=PaidHashContext.BitsV2.Hash(start.GetSummary())||!ProofPackageArithmetic.TryReplay(start.GetSummary(),proof,out _,out reason)||!PrefixPreserved(before,proof,service))return false;
                foreach(string key in CompleteCommandKeys.Where(k=>k!="command_id"&&k!="operation"&&k!="proof_digest"))if(!PaidHashContext.BitsV2.Equal(command.Get(key),proof.Get(key)))return false;
            }
            else
            {
                string completion=command.Get("completion_receipt_id") as string;
                if(!_witnesses.TryGetValue(completion??"",out var completed)||completed.FullyAdmitted==null||!(before.GetDictOrEmpty("receipts").Get(completion??"") is GdDict completionReceipt)||completionReceipt.GetDictOrEmpty("result").Get("operation") as string!="aux_proof_complete_v1"||completionReceipt.GetDictOrEmpty("result").Get("service_id") as string!=service||!ValidateReceipt(before,completionReceipt,completion))return false;
            }
            var expected=before.DeepCopy();Upgrade(expected);
            if(complete)
            {
                var ready=((GdDict)proof.Get("job_after")).DeepCopy();ready["status"]="running";ready["resume_required"]=false;ready["reason"]="";AuxiliaryServiceState.State(expected)["job"]=ready;
            }
            // Execute unchanged catalog/material/inventory/class rules only on private models.
            var computed=AuxiliaryServiceState.Apply(expected,complete?"aux_complete":"aux_take",service,proofTrainingReceipt:complete?id:null);
            var projected=new GdDict();foreach(string key in complete?CompleteEffectKeys:TakeEffectKeys)
                if(key=="operation")projected[key]=operation;
                else if(command.Has(key))projected[key]=command.Get(key);
                else projected[key]=computed.Get(key);
            projected["job_before"]=AuxiliaryServiceState.State(before).GetDictOrEmpty("job").DeepCopy();
            if(complete&&!PaidHashContext.BitsV2.Equal(computed.Get("job_after"),proof.Get("job_after")))return false;
            if(!PaidHashContext.BitsV2.Equal(projected,effect))return false;
            long revision=checked(before.GetInt("revision")+1),sequence=checked(before.GetInt("command_sequence")+1);
            if(!(receipt.Get("revision") is long declaredRevision)||declaredRevision!=revision)return false;
            expected["revision"]=revision;expected["command_sequence"]=sequence;expected.GetDictOrEmpty("receipts")[id]=receipt.DeepCopy();
            if(complete)expected.GetDictOrEmpty("auxiliary_proofs")[id]=proofHash;
            if(!ConservedReceiptSuffix(before,expected,receipt,id,proofHash)){reason="proof_transition_not_conserved";return false;}
            var witness=new Witness(id,PaidHashContext.BitsV2.Hash(receipt),proofHash,expected);string oldHash=_activeOwnerHash;var oldSelf=_self;_self=witness;_activeOwnerHash=witness.OwnerHash;
            try { if(!DomainBundle.TryCreate(expected,out var fully,out reason))return false;witness.FullyAdmitted=fully;_witnesses.Add(id,witness);return true; }
            finally{_self=oldSelf;_activeOwnerHash=oldHash;}
        }
        // The effect branch above is built only by fixed mirror assignments or stock Apply on
        // a private admitted-before clone. This suffix separately conserves every other owner
        // and exact prior economic receipt, rather than granting a mutable after-owner authority.
        static bool ConservedReceiptSuffix(GdDict before,GdDict after,GdDict receipt,string id,string proofHash)
        {
            if(!AuxiliaryProofOwnerProfile.IsBinding(after)||after.GetInt("revision")!=checked(before.GetInt("revision")+1)||after.GetInt("command_sequence")!=checked(before.GetInt("command_sequence")+1))return false;
            foreach(string key in new[]{"registry","holders","machinery","physical_slots","component_work","registered_owners","domain_mode"})if(!PaidHashContext.BitsV2.Equal(before.Get(key),after.Get(key)))return false;
            var oldReceipts=before.GetDictOrEmpty("receipts");var nextReceipts=after.GetDictOrEmpty("receipts");if(oldReceipts.Has(id)||nextReceipts.Count!=oldReceipts.Count+1||!PaidHashContext.BitsV2.Equal(nextReceipts.Get(id),receipt))return false;
            foreach(var entry in oldReceipts)if(!nextReceipts.Has(entry.Key)||!PaidHashContext.BitsV2.Equal(entry.Value,nextReceipts.Get(entry.Key)))return false;
            var oldMap=before.GetDictOrEmpty("auxiliary_proofs");var nextMap=after.GetDictOrEmpty("auxiliary_proofs");if(nextMap.Count!=oldMap.Count+(proofHash==""?0:1))return false;
            foreach(var entry in oldMap)if(!nextMap.Has(entry.Key)||!PaidHashContext.BitsV2.Equal(entry.Value,nextMap.Get(entry.Key)))return false;
            if(proofHash!=""&&nextMap.Get(id) as string!=proofHash)return false;
            var effect=receipt.GetDictOrEmpty("result");string operation=effect.Get("operation") as string;string service=effect.Get("service_id") as string;
            var expectedAux=AuxiliaryServiceState.State(before).DeepCopy();expectedAux["job"]=effect.Get("job_after");
            if(operation!="aux_proof_checkpoint_v1")expectedAux.GetDictOrEmpty("services")[service]=effect.Get("service_after");
            if(!PaidHashContext.BitsV2.Equal(expectedAux,AuxiliaryServiceState.State(after)))return false;
            var oldParticipants=before.GetDictOrEmpty("participating_state");var nextParticipants=after.GetDictOrEmpty("participating_state");if(oldParticipants.Count!=nextParticipants.Count)return false;
            foreach(var entry in oldParticipants)
            {
                string key=(string)entry.Key;if(key=="auxiliary_services")continue;
                if(operation=="aux_proof_complete_v1"&&(key=="manual_study"||key=="paid_crafting"))continue; // exact changes arise only from stock Apply, then full global predicates
                if(operation!="aux_proof_checkpoint_v1"&&(key=="inventory"||key=="progression"||key=="training"))
                {if(!PaidHashContext.BitsV2.Equal(effect.Get(key+"_before"),entry.Value)||!PaidHashContext.BitsV2.Equal(effect.Get(key+"_after"),nextParticipants.Get(key)))return false;}
                else if(!PaidHashContext.BitsV2.Equal(entry.Value,nextParticipants.Get(key)))return false;
            }
            return true;
        }
        static void Upgrade(GdDict owner)
        {
            owner["schema_version"]=7L;owner["feature_schema"]=5L;owner["hash_algorithm"]=PaidHashContext.BitsV2.Algorithm;owner["auxiliary_proof_format"]=1L;
            if(!(owner.Get("auxiliary_proofs") is GdDict))owner["auxiliary_proofs"]=new GdDict();
        }
        internal static GdDict CompletionReceipt(GdDict owner,string service,string stable)
        {
            if(_current==null||owner.GetInt("schema_version")!=7)return new GdDict();
            var matches=owner.GetDictOrEmpty("receipts").Where(p=>p.Value is GdDict r&&r.GetDictOrEmpty("result").Get("operation") as string=="aux_proof_complete_v1"&&r.GetDictOrEmpty("result").Get("service_id") as string==service&&r.GetDictOrEmpty("result").GetDictOrEmpty("service_after").Get("completion_commit_id") as string==stable).ToArray();
            return matches.Length==1&&ValidateReceipt(owner,(GdDict)matches[0].Value,(string)matches[0].Key)?(GdDict)matches[0].Value:new GdDict();
        }
        bool PrefixPreserved(GdDict before,GdDict proof,string service)
        {
            if(!(before.Get("auxiliary_proofs") is GdDict map))return true;
            GdDict latest=null;long revision=-1;
            foreach(var entry in map)if(_proofs.Get(entry.Value) is GdDict prior&&prior.Get("service_id") as string==service&&before.GetDictOrEmpty("receipts").Get(entry.Key) is GdDict receipt&&receipt.GetInt("revision")>revision){latest=prior;revision=receipt.GetInt("revision");}
            if(latest==null)return true;
            if(!(latest.Get("accepted_steps") is long old)||!(proof.Get("accepted_steps") is long next)||next<old||latest.Get("work_lineage_id") as string!=proof.Get("work_lineage_id") as string||
                !(latest.Get("chunks") is GdArray prefix)||!(proof.Get("chunks") is GdArray full)||prefix.Count>full.Count)return false;
            for(int i=0;i<prefix.Count;i++)if(!PaidHashContext.BitsV2.Equal(prefix[i],full[i]))return false;return true;
        }
        internal static bool ValidateReceipt(GdDict owner,GdDict receipt,string id)
        {
            var context=_current;if(context==null||receipt==null||!AuxiliaryProofOwnerProfile.IsNewReceipt(receipt.GetDictOrEmpty("result").Get("operation") as string))return false;
            string hash=PaidHashContext.BitsV2.Hash(receipt);
            if(context._self!=null&&context._self.ReceiptId==id&&context._self.ReceiptHash==hash&&context._self.OwnerHash==PaidHashContext.BitsV2.Hash(owner))return true;
            return context._witnesses.TryGetValue(id,out var witness)&&witness.FullyAdmitted!=null&&witness.ReceiptHash==hash&&(witness.ProofHash==""||owner.GetDictOrEmpty("auxiliary_proofs").Get(id) as string==witness.ProofHash);
        }
    }
}
