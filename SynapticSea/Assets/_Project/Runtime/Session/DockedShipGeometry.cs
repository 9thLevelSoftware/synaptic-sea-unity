using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using UnityEngine;
using UnityEngine.AI;

namespace SynapticSea.Runtime.Session
{
    /// <summary>Instance-only union of overlapping docked interiors. Sources, placement and portal locks remain intact.</summary>
    public sealed class DockedShipGeometry
    {
        readonly List<Collider> _suppressedColliders = new List<Collider>();
        readonly List<Renderer> _suppressedRenderers = new List<Renderer>();
        readonly List<StructuralModule> _openedModules = new List<StructuralModule>();
        readonly Dictionary<SessionInteractable, Vec3> _interactionOrigins = new Dictionary<SessionInteractable, Vec3>();
        readonly Dictionary<SessionInteractable, float> _nextInteractionAttempt = new Dictionary<SessionInteractable, float>();
        SceneShipRoot _host, _mobile;
        Matrix4x4 _hostPose, _mobilePose;
        public int SuppressedColliderCount => _suppressedColliders.Count;

        public void NormalizeInteractions(IEnumerable<SessionInteractable> items, Vector3? reachableFrom = null)
        {
            if (_mobile == null || !_mobile.IsValid) return;
            foreach (var item in items)
            {
                if (!item.IsValid || !ReferenceEquals(item.Parent, _mobile)) continue;
                if (!(item is RepairPoint || item is FireSuppressionPoint || item is BreachSealPoint || item is BridgeTerminal)) continue;
                Vector3 point = Frame.ToUnity(item.GlobalPosition);
                if (_nextInteractionAttempt.TryGetValue(item, out float next) && Time.unscaledTime < next) continue;
                _nextInteractionAttempt[item] = Time.unscaledTime + 0.5f;
                // Portal carving settles after the composite is built and can invalidate an initially
                // accepted anchor. Recheck periodically rather than cache that first navigation result.
                if (StandingAndConnected(point, reachableFrom)) continue;
                bool OnDeck(Collider floor) => Mathf.Abs(floor.bounds.max.y - point.y) < 1f
                    && (floor.transform.IsChildOf(_host.GameObject.transform) || floor.transform.IsChildOf(_mobile.GameObject.transform));
                if (!TryConnectedPoint(point, (float)item.InteractionRadius, OnDeck, reachableFrom, out var clear)
                    || Vector3.Distance(point, clear) >= item.InteractionRadius) continue;
                if (!_interactionOrigins.ContainsKey(item)) _interactionOrigins[item] = item.LocalPosition;
                item.LocalPosition = Frame.ToGodot(_mobile.GameObject.transform.InverseTransformPoint(clear));
            }
        }

        bool StandingAndConnected(Vector3 point, Vector3? reachableFrom)
        {
            if (!SpawnClearance.IsClear(point) || SpawnClearance.FloorUnder(point) == null) return false;
            // Unit-only roots without a loader retain the physical-clearance contract.
            if (!(_host is IShipLoaderView loader)) return true;
            var filter = new NavMeshQueryFilter { agentTypeID = ShipNavMesh.AgentTypeId, areaMask = NavMesh.AllAreas };
            // A restored player may be on the boat side of an overlapping host boundary. An anchor
            // reachable only from the host's authored spawn is not a usable boat control for them.
            Vector3 entry = reachableFrom ?? Frame.ToUnity(_host.GlobalTransform * loader.GetStartTransform().Origin);
            if (!NavMesh.SamplePosition(entry, out var start, 2.5f, filter)
                || !NavMesh.SamplePosition(point, out var target, 0.75f, filter)) return false;
            if (Vector2.Distance(new Vector2(point.x, point.z), new Vector2(target.position.x, target.position.z)) > 0.2f) return false;
            var path = new NavMeshPath();
            return NavMesh.CalculatePath(start.position, target.position, filter, path) && path.status == NavMeshPathStatus.PathComplete;
        }

        bool TryConnectedPoint(Vector3 original, float limit, System.Func<Collider, bool> onDeck, Vector3? reachableFrom, out Vector3 clear)
        {
            clear = original;
            for (float ring = SpawnClearance.SearchStep; ring < limit; ring += SpawnClearance.SearchStep)
            {
                int samples = Mathf.Max(8, Mathf.CeilToInt(2f * Mathf.PI * ring / SpawnClearance.SearchStep));
                for (int i = 0; i < samples; i++)
                {
                    float angle = i * 2f * Mathf.PI / samples;
                    Vector3 candidate = original + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * ring;
                    Collider floor = SpawnClearance.FloorUnder(candidate);
                    if (floor == null || !onDeck(floor)) continue;
                    candidate.y = Mathf.Max(candidate.y, floor.bounds.max.y + SpawnClearance.Skin);
                    if (Vector3.Distance(original, candidate) >= limit || !StandingAndConnected(candidate, reachableFrom)) continue;
                    clear = candidate;
                    return true;
                }
            }
            return false;
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
            SuppressCoveredEdges(host.GameObject, mobileFloors, includeFrames: false, hostLoader: host as IShipLoaderView);
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

        void SuppressCoveredEdges(GameObject root, List<Bounds> floors, bool includeFrames, IShipLoaderView hostLoader = null)
        {
            foreach (var module in root.GetComponentsInChildren<StructuralModule>())
            {
                if (module.layer != "edge" || !(module.moduleId.StartsWith("wall_")
                    || (includeFrames && module.moduleId == "doorway_frame_open_1x1"))) continue;
                // Dedicated generated docks admit the same supported overlap as the hub airlock.
                // Interior boundaries, locks and unsupported void remain authoritative.
                // Compiled hull edges include an empty exterior owner. That is not a second
                // interior room; only distinct, nonempty owners identify a protected boundary.
                var owners = module.roomIds?.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();
                if (!includeFrames && (owners == null || !owners.Any(id => id.StartsWith("airlock") || id.StartsWith("dock"))
                    || owners.Length > 1)) continue;
                if (module.GetComponentInChildren<NavMeshBlocker>() != null) continue;
                var activeColliders = module.GetComponentsInChildren<BoxCollider>().Where(c => c.enabled && !c.isTrigger).ToArray();
                bool entireBoundaryCovered = activeColliders.Length > 0 && activeColliders.All(c => Covered(c.bounds, floors));
                if(entireBoundaryCovered) {module.DockOverlapOpening=true;_openedModules.Add(module);}
                foreach (var collider in activeColliders)
                    if (collider.enabled && !collider.isTrigger && Covered(collider.bounds, floors))
                    { collider.enabled = false; module.DockOverlapColliders.Add(collider); _suppressedColliders.Add(collider); }
                foreach (var renderer in module.GetComponentsInChildren<Renderer>())
                    // Upper trim bounds are not at floor height. When the entire physical boundary
                    // yields to a supported dock overlap, hide its whole visual assembly too.
                    if (renderer.enabled && (entireBoundaryCovered || Covered(renderer.bounds, floors)))
                    { renderer.enabled = false; _suppressedRenderers.Add(renderer); }
            }
        }

        void Restore()
        {
            foreach (var origin in _interactionOrigins) if (origin.Key.IsValid) origin.Key.LocalPosition = origin.Value;
            _interactionOrigins.Clear();
            _nextInteractionAttempt.Clear();
            foreach (var collider in _suppressedColliders) if (collider != null)
            {
                var module = collider.GetComponentInParent<StructuralModule>();
                if(module!=null) module.DockOverlapColliders.Remove(collider);
                if (module == null || (module.integrityState != StructuralModule.IntegrityDestroyed && !module.ConnectionOpening)) collider.enabled = true;
            }
            foreach(var module in _openedModules)if(module!=null){module.DockOverlapOpening=false;module.SetIntegrity(module.integrityState);}
            _openedModules.Clear();
            foreach (var renderer in _suppressedRenderers) if (renderer != null) renderer.enabled = true;
            _suppressedColliders.Clear(); _suppressedRenderers.Clear();
            if (_host != null && _host.IsValid) ShipNavMesh.ResetComposite(_host.GameObject);
            if (_mobile != null && _mobile.IsValid) ShipNavMesh.Build(_mobile.GameObject);
        }
    }
}
