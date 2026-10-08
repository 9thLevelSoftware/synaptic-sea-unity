// Milestone A New Run launch contract (vertical_slice_v1.md + generated_seed_boarded_slice.md REQ-SLICE-001).
// Title New Run loads the golden hub (any run seed: the seed drives the world, markers and wrecks, not the home).
// It does not use StartSceneBuilder or smoke/seed_000017.
using SynapticSea.Core.Session;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Milestone A New Run start: hub is golden <c>coherent_ship_001</c> for every run seed (Phase 1.5). The seed drives the Synaptic
    /// Sea world (markers, sea graph) and the first away wreck; the home ship, its loot and its tutorial stay golden. Biome and
    /// difficulty stay locked to the slice values (hardened home safety is untested); a non-slice biome / difficulty or an
    /// out-of-range seed fails closed — never silently load the wrong layout.
    /// </summary>
    public static class MilestoneALaunch
    {
        public const string HubDir = "res://data/procgen/golden/coherent_ship_001/";
        public const string HubLayoutPath = HubDir + "layout.json";
        public const string HubGameplaySlicePath = HubDir + "gameplay_slice.json";
        public const string HubBlueprintPath = HubDir + "blueprint.json";

        /// <summary>Seed of a direct-open / test run and of the golden hub's own blueprint. A Title New Run rolls a random seed instead.</summary>
        public const long TitleStartSeed = 17;
        /// <summary>Smallest accepted run seed.</summary>
        public const long MinSeed = 1;
        /// <summary>Largest accepted run seed (the range the generators and <c>Random.Range</c> rolls use).</summary>
        public const long MaxSeed = int.MaxValue;
        public const string SliceBiomeId = "breach_field";
        public const string SliceDifficultyId = "standard";

        /// <summary>
        /// True for a Milestone A New Run: any seed in [<see cref="MinSeed"/>, <see cref="MaxSeed"/>] with the slice biome and
        /// difficulty. Anything else is a fail-closed non-slice launch.
        /// </summary>
        public static bool TryAccept(long seed, string biomeId, string difficultyId, out string reason)
        {
            string biome = biomeId ?? "";
            string difficulty = difficultyId ?? "";
            if (seed >= MinSeed && seed <= MaxSeed && biome == SliceBiomeId && difficulty == SliceDifficultyId)
            {
                reason = "";
                return true;
            }
            reason = "non_slice_launch: Milestone A New Run requires a seed in " + MinSeed + ".." + MaxSeed
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
