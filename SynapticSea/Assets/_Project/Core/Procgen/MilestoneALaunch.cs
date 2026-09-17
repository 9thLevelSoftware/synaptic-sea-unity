// Milestone A New Run launch contract (vertical_slice_v1.md + generated_seed_boarded_slice.md REQ-SLICE-001).
// Title New Run loads the golden hub. It does not use StartSceneBuilder or smoke/seed_000017.
using SynapticSea.Core.Session;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Locked Milestone A New Run start: hub is golden <c>coherent_ship_001</c>. Non-slice seed / biome /
    /// difficulty fail closed — never silently load the wrong layout.
    /// </summary>
    public static class MilestoneALaunch
    {
        public const string HubDir = "res://data/procgen/golden/coherent_ship_001/";
        public const string HubLayoutPath = HubDir + "layout.json";
        public const string HubGameplaySlicePath = HubDir + "gameplay_slice.json";
        public const string HubBlueprintPath = HubDir + "blueprint.json";

        /// <summary>Title New Run sentinel (not a generated start seed). Away seeds are the first-run preferred list.</summary>
        public const long TitleStartSeed = 17;
        public const string SliceBiomeId = "breach_field";
        public const string SliceDifficultyId = "standard";

        /// <summary>
        /// True only for the locked Milestone A New Run start. Anything else is a fail-closed non-slice launch.
        /// </summary>
        public static bool TryAccept(long seed, string biomeId, string difficultyId, out string reason)
        {
            string biome = biomeId ?? "";
            string difficulty = difficultyId ?? "";
            if (seed == TitleStartSeed && biome == SliceBiomeId && difficulty == SliceDifficultyId)
            {
                reason = "";
                return true;
            }
            reason = "non_slice_launch: Milestone A New Run requires default start seed " + TitleStartSeed
                     + ", biome " + SliceBiomeId + ", difficulty " + SliceDifficultyId
                     + " (got seed=" + seed + " biome=" + biome + " difficulty=" + difficulty + ")";
            return false;
        }

        /// <summary>Writes the golden hub layout and gameplay-slice paths onto <paramref name="deps"/>.</summary>
        public static void ApplyHubPaths(RunSessionDeps deps)
        {
            if (deps == null) return;
            deps.LayoutPath = HubLayoutPath;
            deps.GameplaySlicePath = HubGameplaySlicePath;
        }
    }
}
