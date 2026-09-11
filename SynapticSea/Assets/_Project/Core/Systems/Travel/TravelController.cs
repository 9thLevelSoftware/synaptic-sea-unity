// Ported from scripts/systems/travel_controller.gd @ 96ecb2b0

using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// RUNTIME: ShipGenerator.generate_from_seed(seed, size, condition), which builds and returns the ship's Node3D.
    /// The Runtime layer implements it; the returned object is an opaque ship handle (null on failure).
    /// </summary>
    public interface IShipGenerator
    {
        object GenerateFromSeed(long seedValue, long size = 0, long condition = 1);
    }

    /// <summary>
    /// The GDScript result <c>{ success: bool, reason: String, ship: Node3D|null }</c>. The ship is a scene node, which a
    /// GdDict cannot hold, so the result is typed; <see cref="ToDict"/> gives the success/reason part.
    /// </summary>
    public sealed class TravelAttemptResult
    {
        public bool Success;
        public string Reason = "";

        /// <summary>RUNTIME: the generated ship root (Node3D in Godot); null unless Success.</summary>
        public object Ship;

        public TravelAttemptResult(bool success, string reason, object ship)
        {
            Success = success;
            Reason = reason;
            Ship = ship;
        }

        public GdDict ToDict() => new GdDict { { "success", Success }, { "reason", Reason } };
    }

    /// <summary>
    /// Validates and executes a jump to a marker, materializing the ship via the procgen pipeline. Pure
    /// coordinator: takes the world, a ShipGenerator, and operational status as inputs; mutates the world only on
    /// success.
    /// </summary>
    public class TravelController
    {
        /// <summary>systems_ops: { "propulsion": bool }. radius: current scanner reach.</summary>
        public TravelAttemptResult AttemptTravel(ShipMarker marker, GdDict systemsOps, IMarkerWorld world, IShipGenerator generator, double radius)
        {
            // GDScript assert()s (debug builds halt).
            if (world == null)
                throw new ArgumentNullException(nameof(world), "TravelController.attempt_travel: world must not be null");
            if (generator == null)
                throw new ArgumentNullException(nameof(generator), "TravelController.attempt_travel: generator must not be null");
            if (marker == null)
                return new TravelAttemptResult(false, "null_marker", null);
            bool inRange = false;
            foreach (ShipMarker m in world.MarkersInRange(radius))
            {
                if (m.MarkerId == marker.MarkerId)
                {
                    inRange = true;
                    break;
                }
            }
            if (!inRange)
                return new TravelAttemptResult(false, "out_of_range", null);
            if (!V.Bool((systemsOps ?? new GdDict()).Get("propulsion", false)))
                return new TravelAttemptResult(false, "propulsion_offline", null);
            // RUNTIME: generation builds the ship scene; the handle is passed through untouched.
            object ship = generator.GenerateFromSeed(marker.SeedValue, marker.SizeClass, marker.Condition);
            if (ship == null)
                return new TravelAttemptResult(false, "generation_failed", null);
            world.SetPlayerPosition(marker.Position);
            world.MarkGenerated(marker.MarkerId);
            return new TravelAttemptResult(true, "ok", ship);
        }
    }
}
