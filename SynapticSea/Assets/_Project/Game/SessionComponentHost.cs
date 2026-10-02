using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.UI;

namespace SynapticSea.Game
{
    public sealed class SessionComponentHost : IComponentInteractionHost
    {
        readonly RunSession _session;
        public SessionComponentHost(RunSession session) { _session = session; }
        public GdArray ListComponentInstances(string holderId) => _session.ListComponentInstances(holderId);
        public GdArray ListInstallTargets(string instanceId) => _session.ListInstallTargets(instanceId);
        public GdDict GetComponentHolderIds() => _session.GetComponentHolderIds();
        public GdDict GetComponentWorkState() => _session.GetComponentWorkState();
        public GdDict RequestComponentRemoval(string instanceId, string destinationHolderId = null) => _session.RequestComponentRemoval(instanceId, destinationHolderId);
        public GdDict RequestComponentTransfer(string instanceId, string destinationHolderId) => _session.RequestComponentTransfer(instanceId, destinationHolderId);
        public GdDict RequestComponentInstall(string instanceId, string shipId, string slotId) => _session.RequestComponentInstall(instanceId, shipId, slotId);
    }
}
