using SynapticSea.Core.Session;
namespace SynapticSea.Core.Systems
{
    // Explicit internal reader selection only. Cannot mint a world cut, runtime or output authority.
    internal sealed class ContinuousSaveReadPolicy
    {
        readonly ProofResourceBinding _binding;
        ContinuousSaveReadPolicy(ProofResourceBinding binding) { _binding = binding; }
        internal static bool TrySelectExplicitReader(ProofResourceBinding currentTrustedBinding,
            out ContinuousSaveReadPolicy policy, out string reason)
        {
            policy = null; reason = "current_trusted_resource_required";
            if (currentTrustedBinding == null || !currentTrustedBinding.Lease.IsCurrent) return false;
            policy = new ContinuousSaveReadPolicy(currentTrustedBinding);
            reason = "explicit_continuous_reader_selected"; return true;
        }
        internal bool MatchesCurrentBinding(ProofResourceBinding binding)
            => ReferenceEquals(binding, _binding) && _binding.Lease.IsCurrent;
    }
}
