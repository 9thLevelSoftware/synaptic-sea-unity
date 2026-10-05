using System;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    // Actual-cut source authority, distinct from reader policy. SCC still admits all structural/artifact data.
    internal sealed class OwnedContinuousSaveOutput
    {
        readonly GdDict _payload,_beforeOwner;
        readonly RunSession.ContinuousOutputLifetime _lifetime;
        internal ProofResourceBinding CurrentBinding { get; }
        internal ContinuousSaveReadPolicy ReaderPolicy { get; }
        internal string RunId { get; }
        internal string SlotId { get; }
        OwnedContinuousSaveOutput(OwnedContinuousCapture cut,GdDict payload)
        {
            _payload=payload.DeepCopy();_beforeOwner=cut.ProofCut.CanonicalBefore.GetSummary();
            _lifetime=cut.ProofCut.Lifetime;CurrentBinding=cut.Parent.Binding;ReaderPolicy=cut.Parent.Policy;RunId=cut.RunId;SlotId=cut.SlotId;
        }
        // Runs against owned data after actual synchronous source capture; worker-safe, no scene/live getters.
        internal static bool TryPrepare(OwnedContinuousCapture cut,out OwnedContinuousSaveOutput output,out string reason)
        {
            output=null;reason="actual_continuous_cut_required";if(cut==null||!cut.IsCurrent)return false;
            try
            {
                var proofCut=cut.ProofCut;
                if(!AuxiliaryCheckpointExport.TryExport(proofCut.Evidence,proofCut.History,proofCut.CanonicalBefore,proofCut.RetainedResult,
                    cut.Parent.Binding,"save:"+cut.Generation,out var proof,out reason))return false;
                GdDict payload=SavePayloadAssembler.BuildContinuous(cut,proof);
                if(!ContinuousSnapshotAdmission.TryRead(payload.GetString("run_text"),payload.GetString("world_text"),cut.Parent.Binding,cut.Parent.Policy,out var admitted,out reason))return false;
                var metadata=payload.DeepCopy();metadata.Erase("run_text");metadata.Erase("world_text");
                if(!ContinuousWholeSnapshotBudget.TryPreflightAdditional(metadata,admitted.SemanticNodes,admitted.JsonUpperBytes,out _,out _,out reason))return false;
                if(!cut.IsCurrent){reason="continuous_output_retired";return false;}
                var prepared=new OwnedContinuousSaveOutput(cut,payload);
                if(!prepared.IsCurrentForOutput(out reason))return false;
                output=prepared;reason="owned_continuous_output_prepared";return true;
            }
            catch(Exception failure){reason="continuous_output_failed:"+failure.Message;return false;}
        }
        internal bool IsCurrentForOutput(out string reason)
        {
            reason="continuous_output_retired";
            if(!_lifetime.IsCurrent||!ReaderPolicy.MatchesCurrentBinding(CurrentBinding))return false;
            reason="";return true;
        }
        internal bool TryCopyPayload(out GdDict payloads,out string reason)
        {
            payloads=null;if(!IsCurrentForOutput(out reason))return false;
            GdDict owned=_payload.DeepCopy();if(!IsCurrentForOutput(out reason))return false;
            payloads=owned;return true;
        }
        internal bool MatchesBeforeOwner(GdDict actualBeforeOwner)
            => actualBeforeOwner!=null&&PaidHashContext.BitsV2.Equal(_beforeOwner,actualBeforeOwner);
    }
}
