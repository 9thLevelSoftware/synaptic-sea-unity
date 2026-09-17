// Scene half of the player + camera state in scripts/procgen/playable_generated_ship.gd @ 96ecb2b0
// (_spawn_player, _spawn_camera, _attach_ceiling_fade_controller, teleport_to, _freeze_player_for_panel).
using System;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Input;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// <see cref="IRunSceneState"/> over a live <see cref="PlayerController"/> and <see cref="IsoCameraRig"/>. The player
    /// and rig are children of <see cref="SessionRoot"/> (world origin), so Godot world positions convert with
    /// <see cref="Frame"/> alone. Spawn/teleport refresh the <see cref="ProximitySensor"/> (Godot re-evaluated Area3D
    /// overlaps on the next physics step; a teleport never produces enter/exit events in Unity).
    /// </summary>
    public sealed class UnityRunSceneState : IRunSceneState
    {
        public readonly Transform SessionRoot;
        readonly SynapticSeaInput _input;

        public PlayerController Player { get; private set; }
        public IsoCameraRig CameraRig { get; private set; }
        public ProximitySensor Sensor { get; private set; }

        /// <summary>Raised after the player and rig exist (views bind the listener, ceiling fade, HUD focus).</summary>
        public event Action<PlayerController> PlayerSpawned;
        public event Action PlayerDespawned;
        /// <summary>Raised after any teleport or direct position write (Godot-frame world position).</summary>
        public event Action<Vec3> PlayerMoved;

        bool _frozen;
        float _multiplier = 1f;

        public UnityRunSceneState(Transform sessionRoot, SynapticSeaInput input)
        {
            SessionRoot = sessionRoot != null ? sessionRoot : throw new ArgumentNullException(nameof(sessionRoot));
            _input = input;
        }

        public bool HasPlayer => Player != null;

        public Vec3 PlayerPosition
        {
            get => Player != null ? Frame.ToGodot(Player.transform.position) : Vec3.Zero;
            set => TeleportPlayer(value);
        }

        public void TeleportPlayer(Vec3 position)
        {
            if (Player == null) return;
            Player.TeleportToGodot(position);
            CameraRig?.SyncToTarget();
            Sensor?.Refresh();
            PlayerMoved?.Invoke(position);
        }

        public void SpawnPlayer(Vec3 position)
        {
            if (Player != null) DespawnPlayer();
            var go = new GameObject("PlayerController", typeof(CharacterController));
            go.transform.SetParent(SessionRoot, false);
            go.SetActive(false);
            Player = go.AddComponent<PlayerController>();
            if (_input != null) Player.BindInput(_input);
            go.SetActive(true);
            Player.SetMovementSpeedMultiplier(_multiplier);
            Sensor = ProximitySensor.AttachTo(Player);

            var rigGo = new GameObject("IsoCameraRig");
            rigGo.transform.SetParent(SessionRoot, false);
            CameraRig = rigGo.AddComponent<IsoCameraRig>();
            Camera cam = CameraRig.EnsureCamera();
            cam.tag = "MainCamera";

            Player.TeleportToGodot(position);
            CameraRig.SetFollowTarget(Player.transform);
            Sensor.Refresh();
            PlayerSpawned?.Invoke(Player);
        }

        public void DespawnPlayer()
        {
            if (Player == null && CameraRig == null) return;
            if (Player != null) Destroy(Player.gameObject);
            if (CameraRig != null) Destroy(CameraRig.gameObject);
            Player = null;
            CameraRig = null;
            Sensor = null;
            PlayerDespawned?.Invoke();
        }

        public double PlayerDefaultMoveSpeed => PlayerController.DefaultMoveSpeed;

        public double PlayerMoveSpeed
        {
            get => Player != null ? Player.moveSpeed : PlayerController.DefaultMoveSpeed;
            set
            {
                if (Player != null) Player.moveSpeed = (float)value;
            }
        }

        public void SetMovementSpeedMultiplier(double multiplier)
        {
            _multiplier = (float)multiplier;
            if (Player != null) Player.SetMovementSpeedMultiplier(_multiplier);
        }

        public void SetPlayerFrozen(bool frozen)
        {
            _frozen = frozen;
            if (Player != null) Player.enabled = !frozen;
        }

        public bool IsPlayerFrozen => _frozen;

        static void Destroy(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            go.transform.SetParent(null, false);
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }
    }
}
