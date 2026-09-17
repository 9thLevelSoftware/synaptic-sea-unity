// Ported from scripts/interaction/authored_portal_runtime.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// A door / locked door / hatch / breach portal (Godot <c>Area3D</c>). The root is a trigger sphere (r 2.2) on the
    /// Sensor layer; <c>PortalBlocker</c> is a box collider on the Portal layer; <c>PortalVisual</c> is the emissive
    /// panel. LOCKED and HATCH portals also drive the structural wrapper bound with <see cref="BindStructuralBlocker"/>
    /// (its colliders and visual follow passability). The Godot meta keys are the public fields.
    ///
    /// Interaction (<see cref="TryInteract"/>) is ported for completeness; the proximity sensor / interact chain
    /// that calls it arrives with the interaction phase.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AuthoredPortalRuntime : MonoBehaviour
    {
        public const string DOOR = "DOOR";
        public const string LOCKED = "LOCKED";
        public const string LOCKED_PORTAL = "LOCKED_PORTAL";
        public const string HATCH = "HATCH";
        public const string BREACH = "BREACH";

        public const float DetectionRadius = 2.2f;
        static readonly Vector3 BlockerSize = new Vector3(2.2f, 2.4f, 0.4f);
        static readonly Vector3 VisualSize = new Vector3(2.2f, 2.4f, 0.12f);

        /// <summary><c>portal_state_changed(portal_id, is_open)</c>.</summary>
        public event Action<string, bool> PortalStateChanged;
        /// <summary><c>unsafe_triggered(portal_id)</c>.</summary>
        public event Action<string> UnsafeTriggered;
        /// <summary><c>exterior_exit_triggered(portal_id)</c>.</summary>
        public event Action<string> ExteriorExitTriggered;

        public string portalId = "";
        public string portalKind = DOOR;
        public bool isOpen;
        public bool isUnlocked;
        public bool isUnsafe;
        public bool isExterior;

        /// <summary>The configured spec (Godot meta <c>authored_portal</c>); positions inside are Godot-frame Vec3.</summary>
        public GdDict PortalSpec { get; private set; } = new GdDict();

        /// <summary>Position in the Godot frame, local to the ship root.</summary>
        public Vec3 GodotPosition { get; private set; }

        bool _playerInRange;
        BoxCollider _blocker;
        GameObject _visual;
        StructuralModule _structuralBlocker;
        GameObject _structuralBlockerVisual;

        public void Configure(GdDict spec, Vec3 godotPosition)
        {
            PortalSpec = spec.DeepCopy();
            portalId = V.Str(spec.Get("id", spec.Get("portal_id", "portal")));
            portalKind = V.Str(spec.Get("kind", spec.Get("state", spec.Get("type", spec.Get("portal_type", DOOR))))).ToUpperInvariant();
            if (portalKind == LOCKED_PORTAL) portalKind = LOCKED;
            isExterior = V.Bool(spec.Get("exterior", false));
            isUnsafe = portalKind == BREACH;
            isUnlocked = portalKind != LOCKED;
            isOpen = portalKind == BREACH || (isExterior && portalKind != DOOR && portalKind != LOCKED && portalKind != HATCH);

            GodotPosition = godotPosition;
            transform.localPosition = Frame.ToUnity(godotPosition);
            object fromPosition = spec.Get("from_position", Vec3.Inf);
            object toPosition = spec.Get("to_position", Vec3.Inf);
            if (spec.Has("yaw_degrees"))
            {
                transform.localRotation = Frame.YawRotation(V.F64(spec.Get("yaw_degrees", 0.0)));
            }
            else if (fromPosition is Vec3 from && toPosition is Vec3 to && from != Vec3.Inf && to != Vec3.Inf)
            {
                Vec3 delta = to - from;
                if (Math.Abs(delta.X) > Math.Abs(delta.Z)) transform.localRotation = Frame.YawRotation(90.0);
            }
            gameObject.layer = PhysicsLayers.Sensor;
            EnsureDetection();
            EnsureBlocker();
            EnsureVisual();
            ApplyState();
        }

        public void SetValidationPlayerInRange(bool value) => _playerInRange = value;

        public string RequiredFlag()
        {
            string lockKind = V.Str(PortalSpec.Get("lock_kind", PortalSpec.Get("lock", "mechanical"))).ToLowerInvariant();
            return lockKind == "electronic" || lockKind == "hack" || lockKind == "hack_chip" ? "hack_chip" : "lockpick";
        }

        public void RestorePersistentState(bool unlocked, bool open)
        {
            if (portalKind == LOCKED) isUnlocked = unlocked;
            isOpen = (open && (portalKind != LOCKED || isUnlocked)) || portalKind == BREACH;
            ApplyState();
        }

        /// <summary>Godot <c>get_blocker_collision_shape</c>.</summary>
        public BoxCollider GetBlockerCollider() => _blocker;

        /// <summary>Binds the LOCKED/HATCH structural wrapper (doorway_frame_blocked_1x1 / bulkhead_portal_2x1).</summary>
        public void BindStructuralBlocker(StructuralModule wrapper)
        {
            _structuralBlocker = wrapper;
            _structuralBlockerVisual = null;
            if (wrapper != null)
            {
                var visual = wrapper.transform.Find("Visual");
                _structuralBlockerVisual = visual != null ? visual.gameObject : null;
                // This wrapper opens when the portal does, so it is carved like the blocker instead of baked in.
                Session.NavMeshBlocker.IgnoreFromBuild(wrapper.gameObject);
            }
            ApplyState();
        }

        public StructuralModule StructuralBlocker => _structuralBlocker;

        public int GetStructuralBlockerCollisionEnabledCount()
        {
            if (_structuralBlocker == null) return 0;
            int count = 0;
            foreach (var c in _structuralBlocker.GetComponentsInChildren<Collider>(true))
                if (c.enabled) count++;
            return count;
        }

        public bool IsStructuralBlockerVisible() =>
            _structuralBlocker != null && (_structuralBlockerVisual == null || _structuralBlockerVisual.activeSelf);

        public bool IsVisualVisible => _visual != null && _visual.activeSelf;

        public GdDict TryInteract(GdDict activeFlags = null, Transform playerBody = null) =>
            TryInteractAt(activeFlags, playerBody != null ? playerBody.position : (Vector3?)null);

        /// <summary><see cref="TryInteract(GdDict, Transform)"/> with the player's Unity world position (the session port).</summary>
        public GdDict TryInteractAt(GdDict activeFlags, Vector3? playerWorldPosition)
        {
            activeFlags = activeFlags ?? new GdDict();
            if (!IsPlayerInRange(playerWorldPosition))
                return new GdDict { { "ok", false }, { "reason", "out_of_range" }, { "portal_id", portalId } };
            if (portalKind == BREACH)
            {
                isUnsafe = true;
                UnsafeTriggered?.Invoke(portalId);
                return new GdDict { { "ok", true }, { "unsafe", true }, { "reason", "unsafe_breach" }, { "portal_id", portalId } };
            }
            bool unlockedNow = false;
            if (portalKind == LOCKED)
            {
                if (isOpen)
                {
                    isOpen = false;
                    ApplyState();
                    PortalStateChanged?.Invoke(portalId, false);
                    return new GdDict { { "ok", true }, { "open", false }, { "portal_id", portalId } };
                }
                if (!isUnlocked)
                {
                    string flag = RequiredFlag();
                    if (!HasActiveFlag(activeFlags, flag))
                        return new GdDict { { "ok", false }, { "reason", "locked" }, { "needs", flag }, { "portal_id", portalId } };
                    isUnlocked = true;
                    unlockedNow = true;
                }
            }
            if (portalKind == DOOR || portalKind == LOCKED || portalKind == HATCH)
            {
                GdDict toggleResult = ToggleOpen();
                if (unlockedNow) toggleResult["unlocked_now"] = true;
                if (isExterior && V.Bool(toggleResult.Get("open", false)))
                {
                    toggleResult["exterior"] = true;
                    ExteriorExitTriggered?.Invoke(portalId);
                }
                return toggleResult;
            }
            if (isExterior)
            {
                ExteriorExitTriggered?.Invoke(portalId);
                return new GdDict { { "ok", true }, { "exterior", true }, { "portal_id", portalId } };
            }
            return ToggleOpen();
        }

        static bool HasActiveFlag(GdDict activeFlags, string flag)
        {
            if (!activeFlags.Has(flag)) return false;
            object value = activeFlags.Get(flag, false);
            if (value is GdDict d) return V.I64(d.Get("count", 1L)) > 0;
            return value is bool b && b;
        }

        GdDict ToggleOpen()
        {
            isOpen = !isOpen;
            ApplyState();
            PortalStateChanged?.Invoke(portalId, isOpen);
            return new GdDict { { "ok", true }, { "open", isOpen }, { "portal_id", portalId } };
        }

        bool IsPlayerInRange(Vector3? playerWorldPosition)
        {
            if (_playerInRange) return true;
            return playerWorldPosition.HasValue && Vector3.Distance(transform.position, playerWorldPosition.Value) <= DetectionRadius;
        }

        void EnsureDetection()
        {
            var sphere = GetComponent<SphereCollider>();
            if (sphere == null) sphere = gameObject.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = DetectionRadius;
        }

        void EnsureBlocker()
        {
            var go = new GameObject("PortalBlocker") { layer = PhysicsLayers.Portal };
            go.transform.SetParent(transform, false);
            _blocker = go.AddComponent<BoxCollider>();
            _blocker.size = BlockerSize;
            _blocker.center = new Vector3(0f, BlockerSize.y * 0.5f, 0f);
            // A closed portal blocks the threats' NavMesh too, by carving rather than baking: it opens mid-run.
            Session.NavMeshBlocker.Attach(_blocker);
        }

        void EnsureVisual()
        {
            Color color = KindColor();
            _visual = RuntimeVisualCatalog.AddMesh(transform, "PortalVisual", RuntimeVisualCatalog.Cube,
                RuntimeVisualCatalog.Material(color, emissionEnergy: 0.25f),
                new Vector3(0f, VisualSize.y * 0.5f, 0f), Quaternion.identity, VisualSize, PhysicsLayers.Portal);
        }

        void ApplyState()
        {
            bool passable = isOpen || isUnsafe;
            if (_blocker != null) _blocker.enabled = !passable;
            if (_structuralBlocker != null)
            {
                foreach (var c in _structuralBlocker.GetComponentsInChildren<Collider>(true)) c.enabled = !passable;
                if (_structuralBlockerVisual != null) _structuralBlockerVisual.SetActive(!passable);
            }
            if (_visual != null) _visual.SetActive(!passable);
        }

        Color KindColor()
        {
            switch (portalKind)
            {
                case LOCKED: return new Color(0.8f, 0.55f, 0.12f, 1.0f);
                case HATCH: return new Color(0.2f, 0.65f, 0.9f, 1.0f);
                // Godot's material is opaque (no TRANSPARENCY_ALPHA), so the 0.85 alpha is inert there too.
                case BREACH: return new Color(0.95f, 0.18f, 0.12f, 0.85f);
                default: return new Color(0.5f, 0.7f, 0.75f, 1.0f);
            }
        }
    }
}
