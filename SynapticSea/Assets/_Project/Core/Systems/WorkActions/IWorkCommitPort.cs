using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Detached work publication seam; the supplied owner alone prepares and commits effects.</summary>
    public interface IWorkCommitPort
    {
        GdDict Prepare(GdDict command);
        GdDict Commit(string transactionId);
    }
}
