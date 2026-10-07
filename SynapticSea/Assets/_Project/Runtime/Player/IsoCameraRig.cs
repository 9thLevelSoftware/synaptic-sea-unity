// Ported from scripts/camera/iso_camera_rig.gd @ 96ecb2b0
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Locked-isometric follow camera. Godot: orthographic Camera3D, <c>size = 22</c> (full view height), placed at
    /// <c>target + (16, 18, 16)</c> and <c>look_at(target)</c> every frame. Unity's orthographic size is half the view
    /// height. The playable interior defaults to a closer 14 m view with bounded zoom; the offset and isometric
    /// orientation still go through <see cref="Frame"/>. Physical camera distance does not control orthographic zoom.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IsoCameraRig : MonoBehaviour
    {
        public static readonly Vec3 DefaultGodotOffset = new Vec3(16f, 18f, 16f);
        public const float GodotDefaultSize = 22f;
        public const float DefaultViewSize = 7f;
        public const float MinimumViewSize = 4f;
        public const float MaximumViewSize = 11f;

        [SerializeField] Transform followTarget;
        [SerializeField] Camera rigCamera;
        [Tooltip("Godot-frame offset from the target (converted through Frame).")]
        public Vector3 godotOffset = new Vector3(16f, 13.064f, 16f);
        [Tooltip("Orthographic half-heights in metres, close to far. These are project tuning, not PZ engine constants.")]
        public float[] zoomLevels = { 4f, 5f, 6f, 7f, 8.5f, 10f, 11f };
        [Range(0.05f, 0.5f)] public float zoomSmoothTime = 0.16f;
        float _zoomTarget = DefaultViewSize, _zoomVelocity, _wheelRemainder;
        GameObject _silhouette;
        readonly InteriorOcclusion _occlusion = new InteriorOcclusion();
        public Vector3? FocusAnchor { get; set; }
        public float TargetViewSize => _zoomTarget;
        public InteriorOcclusion Occlusion => _occlusion;
        [Tooltip("Clear with AtmosphereApplier.BackgroundColor (Godot clear colour, or the fog colour when fog is on).")]
        public bool matchAtmosphereBackground = true;
        [Tooltip("Render the Ceiling layer. Off while the player is inside a ship; exterior views (space walks) turn it on.")]
        public bool showCeilings;

        public Camera Camera => rigCamera;
        public Transform FollowTarget => followTarget;

        void Awake() => EnsureCamera();

        void LateUpdate()
        {
            AdvanceZoom(Time.unscaledDeltaTime);
            SyncToTarget();
            _occlusion.Update(rigCamera, followTarget != null ? followTarget.position : (Vector3?)null, FocusAnchor, Time.unscaledDeltaTime);
        }

        void OnDisable() { _occlusion.RestoreAll(); if (_silhouette != null) _silhouette.SetActive(false); }
        void OnEnable() { if (_silhouette != null) _silhouette.SetActive(true); }
        void OnDestroy() { _occlusion.RestoreAll(); ClearSilhouette(); }

        public void SetFollowTarget(Transform target)
        {
            ClearSilhouette();
            followTarget = target;
            EnsureCamera();
            SyncToTarget();
            // Only the controlled player's existing marker gets a depth-tested silhouette. It renders solely behind
            // opaque occluders; there is no enemy x-ray and no cloning or modification of source/shared materials.
            var marker = target != null ? target.Find("PlayerMarker") : null;
            var filter = marker != null ? marker.GetComponent<MeshFilter>() : null;
            if (filter != null)
            {
                var material = RuntimeVisualMaterialLibrary.Load().playerOcclusionSilhouette;
                if (material == null) throw new System.InvalidOperationException("Player silhouette material is missing; rebuild runtime visual materials.");
                _silhouette = RuntimeVisualCatalog.AddMesh(marker, "PlayerOcclusionSilhouette", filter.sharedMesh, material,
                    Vector3.zero, Quaternion.identity, Vector3.one * 1.01f, PhysicsLayers.Player, false);
            }
        }

        void ClearSilhouette()
        {
            if (_silhouette == null) return;
            if (Application.isPlaying) Object.Destroy(_silhouette); else Object.DestroyImmediate(_silhouette);
            _silhouette = null;
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
            rigCamera.orthographicSize = DefaultViewSize;
            rigCamera.nearClipPlane = 1f;
            rigCamera.farClipPlane = 120f;
            rigCamera.clearFlags = CameraClearFlags.SolidColor;
            rigCamera.backgroundColor = AtmosphereApplier.BackgroundColor;
            ApplyCeilingCulling();
            return rigCamera;
        }

        /// <summary>Orthographic half-height in metres. Changes only presentation, never room/interaction geometry.</summary>
        public void SetViewSize(float size)
        {
            if (float.IsNaN(size) || float.IsInfinity(size)) return;
            _zoomTarget = Mathf.Clamp(size, MinimumViewSize, MaximumViewSize);
            EnsureCamera().orthographicSize = _zoomTarget;
            _zoomVelocity = 0;
        }

        /// <summary>Positive wheel delta zooms in; one standard wheel notch is 120 Input System units.</summary>
        public void ZoomByWheel(float delta)
        {
            if (float.IsNaN(delta) || float.IsInfinity(delta)) return;
            _wheelRemainder += Mathf.Clamp(delta, -360f, 360f);
            int steps = Mathf.Clamp((int)(_wheelRemainder / 120f), -3, 3);
            if (steps == 0) return;
            _wheelRemainder -= steps * 120f;
            for (int step = 0; step < Mathf.Abs(steps); step++)
            {
                float next = _zoomTarget;
                foreach (float level in zoomLevels ?? System.Array.Empty<float>())
                {
                    if (float.IsNaN(level) || float.IsInfinity(level) || level < MinimumViewSize || level > MaximumViewSize) continue;
                    if (steps > 0 && level < _zoomTarget - 0.001f && (next == _zoomTarget || level > next)) next = level;
                    if (steps < 0 && level > _zoomTarget + 0.001f && (next == _zoomTarget || level < next)) next = level;
                }
                _zoomTarget = next;
            }
        }

        public void AdvanceZoom(float deltaTime)
        {
            if (deltaTime <= 0 || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) return;
            var camera = EnsureCamera();
            camera.orthographicSize = Mathf.SmoothDamp(camera.orthographicSize, _zoomTarget, ref _zoomVelocity,
                zoomSmoothTime, Mathf.Infinity, Mathf.Min(deltaTime, 0.1f));
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
