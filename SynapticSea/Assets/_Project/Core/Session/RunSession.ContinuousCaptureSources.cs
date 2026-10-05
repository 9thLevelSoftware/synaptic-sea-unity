using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using System.Collections.Generic;
namespace SynapticSea.Core.Session
{
    public partial class RunSession
    {
        internal bool BuildContinuousGenerationDocumentSet(WorldSnapshot world, ContinuousSafeEndTick ticket,
            out GdDict references, out GdArray artifacts, out string reason)
        {
            RequireContinuousClosedCut(ticket);
            var documents = new Dictionary<string, GdDict>();
            foreach (var entry in _generationShipDocuments) documents.Add(entry.Key, entry.Value.DeepCopy());
            // Read archived authored documents; never Remember/regenerate as a capture side effect.
            return BuildGenerationDocumentSet(world, documents, out references, out artifacts, out reason);
        }

        internal GdDict DetachedCurrentShipSummaryForContinuousCut(ContinuousSafeEndTick ticket)
        {
            RequireContinuousClosedCut(ticket);
            GdDict summary = CurrentShip?.GetSummary().DeepCopy() ?? new GdDict();
            if (CurrentShip == null) return summary;
            if (ThreatManager != null) summary["combat"] = ThreatManager.GetSummary().DeepCopy();
            if (ElectricalArcState != null) summary["arc"] = ElectricalArcState.GetSummary().DeepCopy();
            if (OxygenState != null) summary["breach_environment"] = BreachEnvironmentFrom(OxygenState.GetSummary());
            if (ModuleIntegrityMap != null) summary["module_integrity"] = ModuleIntegrityMap.GetSummary().DeepCopy();
            if (ComponentPlacementState != null)
            {
                GdDict placement = ComponentPlacementState.GetSummary();
                summary["component_placement"] = placement.GetArrayOrEmpty("placed").IsEmpty ? new GdDict() : placement.DeepCopy();
            }
            return summary;
        }
    }
}
