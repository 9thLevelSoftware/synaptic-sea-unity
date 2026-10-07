using SynapticSea.Core.Procgen;
using SynapticSea.Core.Systems;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary>
        /// Generator for the run's first away wreck: the ordinary <see cref="ShipGenerator"/> plus the deterministic
        /// first-run contract patch, so the wreck the player boards is the one the gate accepted. Later wrecks and
        /// revisits use the plain generator.
        /// </summary>
        IShipGenerator FirstRunGenerator() =>
            VisitedShips.Count == 0 && FirstRunContract != null && !FirstRunContract.Contract.IsEmpty
                ? new FirstRunAwayGate.PatchedGenerator(ShipGenerator, FirstRunContract)
                : ShipGenerator;
    }
}
