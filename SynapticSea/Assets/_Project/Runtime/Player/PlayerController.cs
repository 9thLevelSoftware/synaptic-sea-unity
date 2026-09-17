// Ported from scripts/player/player_controller.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Player movement (Godot <c>CharacterBody3D</c> + <c>move_and_slide</c> → Unity <see cref="CharacterController"/>).
    /// Movement is computed in Godot's frame exactly as the GDScript does (move_right = +X, move_back = +Z) and converted
    /// once through <see cref="Frame"/>; because the camera is converted the same way, on-screen movement is identical.
    /// Interact / field-craft are raised as events; the modal input router decides whether the Player map is enabled
    /// (Godot delivered these through <c>_unhandled_input</c>, after UI consumed what it wanted).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public sealed class PlayerController : MonoBehaviour
    {
        public const float DefaultMoveSpeed = 6.0f;
        public const float CrouchSpeedFactor = 0.5f;
        public const float DefaultCollisionRadius = 0.35f;
        public const float DefaultCollisionHeight = 1.6f;
        public const float DefaultFloorSnapLength = 0.5f;
        public const float DefaultFloorMaxAngleDegrees = 60.0f;
        public const int PlayerLayer = 6;

        /// <summary>A programmatic interact (<see cref="RequestInteract"/>): no hold is involved.</summary>
        public event Action<PlayerController> InteractRequested;
        /// <summary>Interact button went down (hold-to-work begins; the host decides between resume and a new interact).</summary>
        public event Action<PlayerController> InteractPressed;
        /// <summary>Interact button released (or the Player map was disabled while it was held).</summary>
        public event Action<PlayerController> InteractReleased;
        public event Action<PlayerController> FieldCraftRequested;
        /// <summary><c>attack_primary</c> pressed.</summary>
        public event Action<PlayerController> AttackRequested;
        /// <summary><c>reload_weapon</c> pressed.</summary>
        public event Action<PlayerController> ReloadRequested;
        /// <summary><c>hotbar_1..3</c> pressed: the zero-based slot index.</summary>
        public event Action<PlayerController, int> HotbarRequested;
        /// <summary>A movement action was pressed (Godot <c>_input</c> raised the <c>player_moved</c> tutorial on these).</summary>
        public event Action<PlayerController> MoveInputPressed;

        public float moveSpeed = DefaultMoveSpeed;
        public float gravity = 9.8f;

        float _speedMultiplier = 1f;
        bool _crouching;
        bool _useScriptedMovement;
        Vec3 _scriptedMoveDirection = Vec3.Zero;

        /// <summary>Velocity in Godot's frame (what the GDScript's <c>velocity</c> held).</summary>
        public Vec3 GodotVelocity { get; private set; } = Vec3.Zero;

        CharacterController _controller;
        SynapticSeaInput _input;
        bool _ownsInput;

        public CharacterController Controller => _controller;

        /// <summary>Player position in Godot's frame, for Core systems (rooms, zones, saves).</summary>
        public Vec3 GodotPosition => Frame.ToGodot(transform.position);

        void Awake()
        {
            EnsureSupportComponents();
        }

        /// <summary>Uses a shared input instance (the modal router owns enable/disable of maps).</summary>
        public void BindInput(SynapticSeaInput input)
        {
            UnbindInput();
            _input = input;
            _ownsInput = false;
            HookInput();
        }

        void OnEnable()
        {
            if (_input == null)
            {
                _input = new SynapticSeaInput();
                _ownsInput = true;
                _input.Player.Enable();
            }
            HookInput();
        }

        void OnDisable() => UnbindInput();

        void OnDestroy()
        {
            if (_ownsInput && _input != null)
            {
                _input.Dispose();
                _input = null;
            }
        }

        void HookInput()
        {
            if (_input == null) return;
            UnbindInput();
            var p = _input.Player;
            p.interact.performed += OnInteract;
            p.interact.canceled += OnInteractReleased;
            p.field_craft.performed += OnFieldCraft;
            p.attack_primary.performed += OnAttack;
            p.reload_weapon.performed += OnReload;
            p.hotbar_1.performed += OnHotbar1;
            p.hotbar_2.performed += OnHotbar2;
            p.hotbar_3.performed += OnHotbar3;
            p.move_forward.performed += OnMove;
            p.move_back.performed += OnMove;
            p.move_left.performed += OnMove;
            p.move_right.performed += OnMove;
        }

        void UnbindInput()
        {
            if (_input == null) return;
            var p = _input.Player;
            p.interact.performed -= OnInteract;
            p.interact.canceled -= OnInteractReleased;
            p.field_craft.performed -= OnFieldCraft;
            p.attack_primary.performed -= OnAttack;
            p.reload_weapon.performed -= OnReload;
            p.hotbar_1.performed -= OnHotbar1;
            p.hotbar_2.performed -= OnHotbar2;
            p.hotbar_3.performed -= OnHotbar3;
            p.move_forward.performed -= OnMove;
            p.move_back.performed -= OnMove;
            p.move_left.performed -= OnMove;
            p.move_right.performed -= OnMove;
        }

        void OnInteract(InputAction.CallbackContext _)
        {
            // Without a hold-aware listener (standalone controller), a press is a plain interact.
            if (InteractPressed != null) InteractPressed.Invoke(this);
            else RequestInteract();
        }

        void OnInteractReleased(InputAction.CallbackContext _) => InteractReleased?.Invoke(this);
        void OnFieldCraft(InputAction.CallbackContext _) => FieldCraftRequested?.Invoke(this);
        void OnAttack(InputAction.CallbackContext _) => AttackRequested?.Invoke(this);
        void OnReload(InputAction.CallbackContext _) => ReloadRequested?.Invoke(this);
        void OnHotbar1(InputAction.CallbackContext _) => HotbarRequested?.Invoke(this, 0);
        void OnHotbar2(InputAction.CallbackContext _) => HotbarRequested?.Invoke(this, 1);
        void OnHotbar3(InputAction.CallbackContext _) => HotbarRequested?.Invoke(this, 2);
        void OnMove(InputAction.CallbackContext _) => MoveInputPressed?.Invoke(this);

        /// <summary>Domain 1 vitals gate: the coordinator pushes VitalsState's movement multiplier each frame.</summary>
        public void SetMovementSpeedMultiplier(float m) => _speedMultiplier = Mathf.Clamp01(m);

        public float GetEffectiveMoveSpeed() => moveSpeed * _speedMultiplier * (_crouching ? CrouchSpeedFactor : 1f);

        public void SetCrouching(bool crouching) => _crouching = crouching;
        public bool IsCrouching() => _crouching;

        /// <summary>True with meaningful planar velocity (drives stamina drain and emitted noise).</summary>
        public bool IsMoving() => (GodotVelocity.X * GodotVelocity.X + GodotVelocity.Z * GodotVelocity.Z) > 0.01f;

        public void RequestInteract() => InteractRequested?.Invoke(this);

        /// <summary>Teleport to a Unity world position (disables the controller for the move, then syncs physics).</summary>
        public void TeleportTo(Vector3 unityWorldPosition)
        {
            bool wasEnabled = _controller.enabled;
            _controller.enabled = false;
            transform.position = unityWorldPosition;
            _controller.enabled = wasEnabled;
            Physics.SyncTransforms();
            GodotVelocity = Vec3.Zero;
        }

        /// <summary>Teleport using a Godot-frame position (what Core systems and saves carry).</summary>
        public void TeleportToGodot(Vec3 godotPosition) => TeleportTo(Frame.ToUnity(godotPosition));

        public void SetScriptedMoveDirection(Vec3 godotDirection)
        {
            _useScriptedMovement = true;
            _scriptedMoveDirection = godotDirection;
        }

        public void ClearScriptedMoveDirection()
        {
            _useScriptedMovement = false;
            _scriptedMoveDirection = Vec3.Zero;
        }

        void FixedUpdate() => Step(Time.fixedDeltaTime);

        /// <summary>One <c>_physics_process</c> step (public for tests and deterministic drivers).</summary>
        public void Step(float delta)
        {
            Vec3 moveDirection = ReadMoveDirection();
            if (moveDirection.LengthSquared() > 1f) moveDirection = moveDirection.Normalized();
            if (_input != null && _input.Player.enabled) SetCrouching(_input.Player.crouch.IsPressed());

            GodotVelocity = ComputeVelocity(GodotVelocity, moveDirection, GetEffectiveMoveSpeed(), gravity, _controller.isGrounded, delta);

            Vector3 motion = Frame.ToUnity(GodotVelocity) * delta;
            if (_controller.isGrounded && GodotVelocity.Y <= 0f) motion.y -= DefaultFloorSnapLength * delta; // floor snap
            _controller.Move(motion);
            if (_controller.isGrounded && GodotVelocity.Y < 0f) GodotVelocity = new Vec3(GodotVelocity.X, 0f, GodotVelocity.Z);
        }

        /// <summary>The GDScript velocity update, in Godot's frame.</summary>
        public static Vec3 ComputeVelocity(Vec3 velocity, Vec3 moveDirection, float speed, float gravity, bool onFloor, float delta)
        {
            float vy = velocity.Y;
            if (onFloor && vy < 0f) vy = 0f;
            else vy -= gravity * delta;
            return new Vec3(moveDirection.X * speed, vy, moveDirection.Z * speed);
        }

        Vec3 ReadMoveDirection()
        {
            if (_useScriptedMovement) return _scriptedMoveDirection;
            if (_input == null || !_input.Player.enabled) return Vec3.Zero;
            float x = _input.Player.move_right.ReadValue<float>() - _input.Player.move_left.ReadValue<float>();
            float z = _input.Player.move_back.ReadValue<float>() - _input.Player.move_forward.ReadValue<float>();
            return new Vec3(x, 0f, z);
        }

        /// <summary>Godot `_ready` support-node setup (collision capsule, layer, marker). Runs in Awake; public for edit-mode use.</summary>
        public void EnsureSupportComponents()
        {
            gameObject.layer = PlayerLayer;
            _controller = GetComponent<CharacterController>();
            _controller.radius = DefaultCollisionRadius;
            _controller.height = DefaultCollisionHeight;
            _controller.center = new Vector3(0f, DefaultCollisionHeight * 0.5f, 0f);
            _controller.slopeLimit = DefaultFloorMaxAngleDegrees;
            _controller.stepOffset = 0.3f;
            _controller.skinWidth = 0.02f;

            if (transform.Find("PlayerMarker") == null)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                marker.name = "PlayerMarker";
                DestroyImmediateOrLater(marker.GetComponent<Collider>());
                marker.layer = PlayerLayer;
                marker.transform.SetParent(transform, false);
                marker.transform.localPosition = new Vector3(0f, DefaultCollisionHeight * 0.5f, 0f);
                // Unity's capsule primitive is 2 m tall and 1 m wide.
                marker.transform.localScale = new Vector3(DefaultCollisionRadius * 2f, DefaultCollisionHeight * 0.5f, DefaultCollisionRadius * 2f);
                var r = marker.GetComponent<MeshRenderer>();
                r.sharedMaterial = RuntimeMaterials.Unlit(new Color(0.15f, 0.72f, 1f, 1f));
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        static void DestroyImmediateOrLater(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
