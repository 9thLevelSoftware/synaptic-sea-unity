// The per-frame scene inputs scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 read inside _process.
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// Everything <c>_process(delta)</c> read from live nodes during one frame, captured by the Runtime's
    /// <c>TickContextBuilder</c> before <see cref="RunSession.Tick"/>. Positions are Godot-frame world space.
    /// </summary>
    public struct TickContext
    {
        public double Delta;

        /// <summary><c>player != null and player is Node3D</c>. False keeps player-dependent helpers on their no-player path.</summary>
        public bool HasPlayer;

        /// <summary><c>(player as Node3D).global_position</c>.</summary>
        public Vec3 PlayerPosition;

        /// <summary>Player room id as the scene knows it (Godot passed "" to ThreatManager; reserved).</summary>
        public string PlayerRoomId;

        /// <summary><c>player.is_moving()</c> (planar velocity).</summary>
        public bool Moving;

        /// <summary><c>player.is_crouching()</c>.</summary>
        public bool Crouching;

        /// <summary><c>Input.is_action_pressed("interact")</c> (hold-to-work).</summary>
        public bool InteractHeld;

        /// <summary>
        /// Optional override of <c>is_player_in_breach_zone()</c>. Godot computed it from the breach-zone node positions
        /// (horizontal radius 2.4), which Core reproduces when this is null.
        /// </summary>
        public bool? InBreachZone;

        /// <summary>
        /// Optional override of the fire-zone overlap used by <c>_player_fire_intensity()</c> (compartment id the player
        /// stands in, "" for none). Null = Core computes it from the fire-zone node positions (radius 2.0), like Godot.
        /// </summary>
        public string InFireZoneCompartment;

        public static TickContext Frame(double delta, Vec3 playerPosition, bool moving = false, bool crouching = false, bool interactHeld = false) =>
            new TickContext
            {
                Delta = delta,
                HasPlayer = true,
                PlayerPosition = playerPosition,
                PlayerRoomId = "",
                Moving = moving,
                Crouching = crouching,
                InteractHeld = interactHeld,
            };
    }
}
