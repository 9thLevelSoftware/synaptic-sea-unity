using SynapticSea.Core.Variant;

namespace SynapticSea.UI
{
    /// <summary>Diagnostic component projections and commands. Views never publish component model deltas.</summary>
    public interface IComponentInteractionHost
    {
        GdArray ListComponentInstances(string holderId);
        GdArray ListInstallTargets(string instanceId);
        GdDict GetComponentHolderIds();
        GdDict GetComponentWorkState();
        GdDict RequestComponentRemoval(string instanceId, string destinationHolderId = null);
        GdDict RequestComponentTransfer(string instanceId, string destinationHolderId);
        GdDict RequestComponentInstall(string instanceId, string shipId, string slotId);
    }
}
