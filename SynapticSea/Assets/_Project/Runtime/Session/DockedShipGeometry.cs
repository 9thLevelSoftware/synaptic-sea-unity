using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>Instance-only union of overlapping docked interiors. Sources, placement and portal locks remain intact.</summary>
    public sealed class DockedShipGeometry
    {
        readonly List<Collider> _suppressedColliders = new List<Collider>();
        readonly List<Renderer> _suppressedRenderers = new List<Renderer>();
        readonly Dictionary<SessionInteractable, Vec3> _interactionOrigins = new Dictionary<SessionInteractable, Vec3>();
        SceneShipRoot _host, _mobile;
        Matrix4x4 _hostPose, _mobilePose;
        public int SuppressedColliderCount => _suppressedColliders.Count;

        public void NormalizeInteractions(IEnumerable<SessionInteractable> items)
        {
            if (_mobile == null || !_mobile.IsValid) return;
            foreach (var item in items)
            {
                if (!item.IsValid || !ReferenceEquals(item.Parent, _mobile) || _interactionOrigins.ContainsKey(item)) continue;
                if (!(item is RepairPoint || item is FireSuppressionPoint || item is BreachSealPoint)) continue;
                Vector3 point = Frame.ToUnity(item.GlobalPosition);
                if (SpawnClearance.IsClear(point)) continue;
                bool OnDeck(Collider floor) => Mathf.Abs(floor.bounds.max.y - point.y) < 1f
                    && (floor.transform.IsChildOf(_host.GameObject.transform) || floor.transform.IsChildOf(_mobile.GameObject.transform));
                if (!SpawnClearance.TryFindClear(point, OnDeck, out var clear)
                    || Vector3.Distance(point, clear) >= item.InteractionRadius) continue;
                _interactionOrigins[item] = item.LocalPosition;
                item.LocalPosition = Frame.ToGodot(_mobile.GameObject.transform.InverseTransformPoint(clear));
            }
        }

        public void Reconcile(SceneShipRoot host, SceneShipRoot mobile)
        {
            if (host == null || !host.IsValid || mobile == null || !mobile.IsValid) { host = null; mobile = null; }
            if (ReferenceEquals(host, _host) && ReferenceEquals(mobile, _mobile)
                && (host == null || (_hostPose == host.GameObject.transform.localToWorldMatrix
                    && _mobilePose == mobile.GameObject.transform.localToWorldMatrix))) return;
            Restore(); _host = host; _mobile = mobile;
            if (host == null) return;
            _hostPose = host.GameObject.transform.localToWorldMatrix;
            _mobilePose = mobile.GameObject.transform.localToWorldMatrix;
            Physics.SyncTransforms();
            var hostFloors = Floors(host.GameObject);
            var mobileFloors = Floors(mobile.GameObject);
            SuppressCoveredEdges(mobile.GameObject, hostFloors, includeFrames: true);
            SuppressCoveredEdges(host.GameObject, mobileFloors, includeFrames: false);
            Physics.SyncTransforms();
            ShipNavMesh.BuildComposite(host.GameObject, mobile.GameObject);
        }

        static List<Bounds> Floors(GameObject root) => root.GetComponentsInChildren<StructuralModule>()
            .Where(m => m.layer == "floor").SelectMany(m => m.GetComponentsInChildren<BoxCollider>())
            .Where(c => c.enabled && !c.isTrigger).Select(c => c.bounds).ToList();

        static bool Covered(Bounds box, List<Bounds> floors)
        {
            // Sample the entire footprint: never remove a wall across unsupported void.
            int nx = Mathf.Max(1, Mathf.CeilToInt(box.size.x / 0.5f));
            int nz = Mathf.Max(1, Mathf.CeilToInt(box.size.z / 0.5f));
            for (int x = 0; x <= nx; x++) for (int z = 0; z <= nz; z++)
            {
                float px = Mathf.Lerp(box.min.x, box.max.x, (float)x / nx);
                float pz = Mathf.Lerp(box.min.z, box.max.z, (float)z / nz);
                if (!floors.Any(f => Mathf.Abs(f.max.y - box.min.y) < 0.7f
                    && px >= f.min.x - 0.06f && px <= f.max.x + 0.06f
                    && pz >= f.min.z - 0.06f && pz <= f.max.z + 0.06f)) return false;
            }
            return true;
        }

        void SuppressCoveredEdges(GameObject root, List<Bounds> floors, bool includeFrames)
        {
            foreach (var module in root.GetComponentsInChildren<StructuralModule>())
            {
                if (module.layer != "edge" || !(module.moduleId.StartsWith("wall_")
                    || (includeFrames && module.moduleId == "doorway_frame_open_1x1"))) continue;
                // The host admits the mobile only at its airlock, not through corridor/room boundaries or locks.
                if (!includeFrames && (module.roomIds == null || !module.roomIds.Any(id => id.StartsWith("airlock"))
                    || module.roomIds.Length > 1)) continue;
                if (module.GetComponentInChildren<NavMeshBlocker>() != null) continue;
                foreach (var collider in module.GetComponentsInChildren<BoxCollider>())
                    if (collider.enabled && !collider.isTrigger && Covered(collider.bounds, floors))
                    { collider.enabled = false; _suppressedColliders.Add(collider); }
                foreach (var renderer in module.GetComponentsInChildren<Renderer>())
                    if (renderer.enabled && Covered(renderer.bounds, floors))
                    { renderer.enabled = false; _suppressedRenderers.Add(renderer); }
            }
        }

        void Restore()
        {
            foreach (var origin in _interactionOrigins) if (origin.Key.IsValid) origin.Key.LocalPosition = origin.Value;
            _interactionOrigins.Clear();
            foreach (var collider in _suppressedColliders) if (collider != null)
            {
                var module = collider.GetComponentInParent<StructuralModule>();
                if (module == null || module.integrityState != StructuralModule.IntegrityDestroyed) collider.enabled = true;
            }
            foreach (var renderer in _suppressedRenderers) if (renderer != null) renderer.enabled = true;
            _suppressedColliders.Clear(); _suppressedRenderers.Clear();
            if (_host != null && _host.IsValid) ShipNavMesh.ResetComposite(_host.GameObject);
            if (_mobile != null && _mobile.IsValid) ShipNavMesh.Build(_mobile.GameObject);
        }
    }
}
