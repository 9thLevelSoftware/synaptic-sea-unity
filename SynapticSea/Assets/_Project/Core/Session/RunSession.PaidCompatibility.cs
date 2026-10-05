using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        public bool BitExactPaidCompatibilityEnabled => Deps.EnableBitExactPaidCompatibility;
        PaidHashContext CurrentPaidHashContext => _componentDomain != null ? _componentDomain.HashContext
            : BitExactPaidCompatibilityEnabled && PaidCraftingEnabled && Deps.SelectedSaveGeneration == null
                ? PaidHashContext.BitsV2 : PaidHashContext.Legacy;
        long CurrentPaidFeatureSchema => _componentDomain?.FeatureSchema ?? 0;
        CheckpointProofAdmission.Result _continuousRestoreAdmission;
        CheckpointProofAdmission.Result CurrentContinuousRestoreAdmission
            => ComponentGenerationRestoreInProgress && _paidRestoreOperation != null && _paidRestoreOperation.Owner == this
                ? _paidRestoreOperation.ContinuousAdmission : _continuousRestoreAdmission;
        bool MatchesAdmittedContinuousOwner(GdDict owner, CheckpointProofAdmission.Result admitted)
            => admitted != null && admitted.Lease.IsCurrent && owner != null && owner.GetInt("schema_version") == 7 &&
                PaidHashContext.BitsV2.Equal(owner, admitted.CopyOwner());
        long PaidFeatureSchema(GdDict owner)
        {
            if (owner?.GetInt("schema_version") != 7) return PaidHashContext.FeatureSchema(owner);
            TrustedContinuousMetadata(owner, out long feature, out _); return feature;
        }
        void TrustedContinuousMetadata(GdDict owner, out long feature, out PaidHashContext context)
        {
            if (MatchesAdmittedContinuousOwner(owner, CurrentContinuousRestoreAdmission))
            {
                var admitted = CurrentContinuousRestoreAdmission.OwnedDomainForReplacement;
                feature = admitted.FeatureSchema; context = admitted.HashContext; return;
            }
            if (_componentDomain?.SchemaVersion == 7 && PaidHashContext.BitsV2.Equal(owner, _componentDomain.GetSummary()))
            { feature = _componentDomain.FeatureSchema; context = _componentDomain.HashContext; return; }
            throw new System.ArgumentException("unadmitted_continuous_owner_metadata");
        }
        PaidHashContext OwnerHashContext(GdDict owner)
        {
            if (owner?.GetInt("schema_version") != 7) return PaidHashContext.FromOwner(owner);
            TrustedContinuousMetadata(owner, out _, out PaidHashContext context); return context;
        }
        bool TryReadContinuousRestoreSelection(GdDict selection, out AdmittedContinuousSnapshots admitted, out string reason)
        {
            admitted = null; reason = "continuous_restore_capability_required";
            if (!Deps.EnableContinuousAuxiliaryDiagnostic || !ComponentIntegrationEnabled || !PaidCraftingEnabled || !BitExactPaidCompatibilityEnabled) return false;
            var service = new SaveLoadService(Storage, Clock, ComponentIntegrationEnabled, PaidCraftingEnabled)
            { FirstAwaySalvageProfileEnabled = ReviewedFirstAwayProfileEnabled, BitExactPaidCompatibilityEnabled = BitExactPaidCompatibilityEnabled };
            if (!service.TryEnableContinuousDiagnosticReader(out reason)) return false;
            return service.TryReadContinuousSelection(selection, out admitted, out reason);
        }
        static bool PaidEqual(PaidHashContext context, object left, object right)
            => context != null && context.Algorithm == PaidHashContext.BitsV2.Algorithm ? context.Equal(left, right) : V.VariantEquals(left, right);
    }
}
