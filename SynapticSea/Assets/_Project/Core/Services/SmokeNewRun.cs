using System;
using System.Globalization;
using SynapticSea.Core.Procgen;

namespace SynapticSea.Core.Services
{
    /// <summary>
    /// Test tooling for <c>tools/mac/smoke.sh --new-run</c>: lets the smoke test press New Run in a built development player
    /// (<c>-synaptic-smoke-new-run &lt;seed&gt;</c>) so a broken New Run is no longer invisible. It is not a game option and not a
    /// persistent flag: it exists only as a command-line argument, and it is inert unless the build kind is "dev" and the
    /// argument carries a valid seed. Ordinary launches never pass it, so default behaviour is unchanged.
    /// </summary>
    public static class SmokeNewRun
    {
        public const string Flag = "-synaptic-smoke-new-run";

        /// <summary>True only for a development build whose command line holds <see cref="Flag"/> followed by a seed the launch contract accepts.</summary>
        public static bool TryGetSeed(string[] args, string buildKind, out long seed)
        {
            seed = 0;
            if (args == null || buildKind != "dev") return false;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != Flag) continue;
                if (!long.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) return false;
                if (parsed < MilestoneALaunch.MinSeed || parsed > MilestoneALaunch.MaxSeed) return false;
                seed = parsed;
                return true;
            }
            return false;
        }
    }
}
