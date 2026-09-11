// Scene boundary for scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 (player + camera half).
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// RUNTIME: the player-side scene state the coordinator read and wrote directly on its <c>player</c>
    /// (<c>PlayerController</c>) and <c>camera_rig</c> nodes. Every position is Godot-frame world space; the Runtime converts
    /// through <c>Frame</c>. Lifetime calls (<see cref="SpawnPlayer"/>, <see cref="DespawnPlayer"/>) mirror
    /// <c>_spawn_player</c>/<c>_spawn_camera</c> and the player teardown in <c>_reset_runtime_for_reload</c>.
    /// </summary>
    public interface IRunSceneState
    {
        /// <summary><c>player != null and is_instance_valid(player)</c>.</summary>
        bool HasPlayer { get; }

        /// <summary><c>player.global_position</c> (read) / direct write (<c>(player as Node3D).global_position = p</c>).</summary>
        Vec3 PlayerPosition { get; set; }

        /// <summary><c>player.teleport_to(p)</c> (disables physics for one step, then re-peg; CharacterController in Unity).</summary>
        void TeleportPlayer(Vec3 position);

        /// <summary><c>_spawn_player</c> + <c>_spawn_camera</c>: instantiate the player at <paramref name="position"/> and bind the rig.</summary>
        void SpawnPlayer(Vec3 position);

        /// <summary>Reload teardown: free the player and camera rig.</summary>
        void DespawnPlayer();

        /// <summary><c>PlayerController.DEFAULT_MOVE_SPEED</c>.</summary>
        double PlayerDefaultMoveSpeed { get; }

        /// <summary><c>player.move_speed</c> (the encumbrance x cart-push speed).</summary>
        double PlayerMoveSpeed { get; set; }

        /// <summary><c>player.set_movement_speed_multiplier(m)</c> (vitals gating).</summary>
        void SetMovementSpeedMultiplier(double multiplier);

        /// <summary>
        /// <c>_freeze_player_for_panel</c> (<paramref name="frozen"/> true) and <c>_unfreeze_player_after_panel</c>: toggles
        /// the player's physics and input processing.
        /// </summary>
        void SetPlayerFrozen(bool frozen);

        /// <summary><c>not player.is_physics_processing()</c>.</summary>
        bool IsPlayerFrozen { get; }
    }
}
