// Ported from scripts/camera/iso_camera_rig.gd @ 96ecb2b0
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Locked-isometric follow camera. Godot: orthographic Camera3D, <c>size = 22</c> (full view height), placed at
    /// <c>target + (16, 18, 16)</c> and <c>look_at(target)</c> every frame. Unity's orthographic size is half the view
    /// height, so the default is 11; the offset goes through <see cref="Frame"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IsoCameraRig : MonoBehaviour
    {
        public static readonly Vec3 DefaultGodotOffset = new Vec3(16f, 18f, 16f);
        public const float GodotDefaultSize = 22f;

        [SerializeField] Transform followTarget;
        [SerializeField] Camera rigCamera;
        [Tooltip("Godot-frame offset from the target (converted through Frame).")]
        public Vector3 godotOffset = new Vector3(16f, 18f, 16f);
        [Tooltip("Clear with AtmosphereApplier.BackgroundColor (Godot clear colour, or the fog colour when fog is on).")]
        public bool matchAtmosphereBackground = true;
        [Tooltip("Render the Ceiling layer. Off while the player is inside a ship; exterior views (space walks) turn it on.")]
        public bool showCeilings;

        public Camera Camera => rigCamera;
        public Transform FollowTarget => followTarget;

        void Awake() => EnsureCamera();

        void LateUpdate() => SyncToTarget();

        public void SetFollowTarget(Transform target)
        {
            followTarget = target;
            EnsureCamera();
            SyncToTarget();
        }

        public void SyncToTarget()
        {
            if (rigCamera != null && matchAtmosphereBackground) rigCamera.backgroundColor = AtmosphereApplier.BackgroundColor;
            ApplyCeilingCulling();
            if (followTarget == null || rigCamera == null) return;
            Vector3 offset = Frame.ToUnity(new Vec3(godotOffset.x, godotOffset.y, godotOffset.z));
            transform.position = followTarget.position + offset;
            rigCamera.transform.position = transform.position;
            rigCamera.transform.LookAt(followTarget.position, Vector3.up);
        }

        public Camera EnsureCamera()
        {
            if (rigCamera != null) return rigCamera;
            var go = new GameObject("PlayableIsoCamera");
            go.transform.SetParent(transform, false);
            rigCamera = go.AddComponent<Camera>();
            rigCamera.orthographic = true;
            rigCamera.orthographicSize = GodotDefaultSize * 0.5f;
            rigCamera.nearClipPlane = 1f;
            rigCamera.farClipPlane = 120f;
            rigCamera.clearFlags = CameraClearFlags.SolidColor;
            rigCamera.backgroundColor = AtmosphereApplier.BackgroundColor;
            ApplyCeilingCulling();
            return rigCamera;
        }

        /// <summary>Shows or hides ceilings for this camera (see <see cref="showCeilings"/>).</summary>
        public void SetShowCeilings(bool show)
        {
            showCeilings = show;
            ApplyCeilingCulling();
        }

        /// <summary>
        /// Design rule (2026-09-17): ceilings are not drawn while the player is inside a ship; they exist for exterior
        /// views only. The ceiling modules stay in the scene (layouts, saves and integrity are unchanged); only this
        /// camera's culling mask drops <see cref="PhysicsLayers.Ceiling"/>, which also drops their shadows. This replaces
        /// Godot's 12 m ceiling fade, which never ran in Godot and would have covered the player's own room.
        /// </summary>
        void ApplyCeilingCulling()
        {
            if (rigCamera == null) return;
            int bit = 1 << PhysicsLayers.Ceiling;
            rigCamera.cullingMask = showCeilings ? rigCamera.cullingMask | bit : rigCamera.cullingMask & ~bit;
        }
    }
}
