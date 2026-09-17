// Scene half of the coordinator-built zone nodes in scripts/procgen/playable_generated_ship.gd @ 96ecb2b0:
// route gates (1093-1160, 8340-8370), breach zones (8863-9180), arc zones (9318-9580) and fire zones (4965-5065).
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// One <see cref="SessionZone"/>: a solid box collider on the ZoneBlocker layer (route gate / breach / arc) whose
    /// <c>enabled</c> follows <see cref="SessionZone.CollisionEnabled"/>, and a translucent unshaded box whose colour
    /// follows <see cref="SessionZone.VisualState"/>. Fire zones are a sensor sphere (radius 2, never blocking) with the
    /// emissive cube and the <c>timed_fire</c> VFX; closed route gates carry <c>biomatter_blockage</c> (the hooks
    /// <c>VfxCatalog</c> records).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ZoneView : MonoBehaviour
    {
        public static readonly Vector3 RouteGateSize = new Vector3(2.6f, 2.2f, 0.7f);
        public static readonly Vector3 BreachZoneSize = new Vector3(2.6f, 2.2f, 1.6f);
        public static readonly Vector3 ArcZoneSize = new Vector3(2.6f, 2.2f, 1.6f);

        static readonly Color RouteGateClosed = new Color(1.0f, 0.22f, 0.18f, 0.82f);
        static readonly Color RouteGateOpen = new Color(0.18f, 0.75f, 1.0f, 0.18f);
        static readonly Color BreachOpen = new Color(0.95f, 0.32f, 0.22f, 0.65f);
        static readonly Color BreachBlocked = new Color(0.65f, 0.05f, 0.05f, 0.92f);
        static readonly Color BreachSealed = new Color(0.18f, 0.55f, 1.0f, 0.55f);
        static readonly Color ArcDischarged = new Color(0.35f, 0.85f, 1.0f, 0.35f);
        static readonly Color ArcArcing = new Color(0.95f, 0.32f, 1.0f, 0.82f);
        static readonly Color FireColor = new Color(0.95f, 0.3f, 0.05f, 0.65f);

        public SessionZone Zone { get; private set; }
        public BoxCollider Blocker { get; private set; }
        public GameObject Visual { get; private set; }
        public GameObject Vfx { get; private set; }

        MeshRenderer _renderer;
        string _appliedVisualState;

        public static ZoneView Create(SessionZone zone, Transform parent)
        {
            var go = new GameObject(GodotNodeName.Validate(string.IsNullOrEmpty(zone.NodeName) ? zone.Kind + "_" + zone.ZoneId : zone.NodeName));
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<ZoneView>();
            view.Build(zone);
            return view;
        }

        void Build(SessionZone zone)
        {
            Zone = zone;
            if (zone.Kind == "fire")
            {
                gameObject.layer = PhysicsLayers.Sensor;
                var sphere = gameObject.AddComponent<SphereCollider>();
                sphere.isTrigger = true;
                sphere.radius = 2f;
                Visual = RuntimeVisualCatalog.AddMesh(transform, "FireZoneVisual", RuntimeVisualCatalog.Cube,
                    RuntimeVisualCatalog.Material(FireColor, unshaded: true, transparent: true),
                    Vector3.zero, Quaternion.identity, new Vector3(1.2f, 1.2f, 1.2f), PhysicsLayers.Prop, castShadows: false);
                Vfx = VfxCatalog.Spawn(VfxCatalog.TimedFire, transform, Vec3.Zero);
            }
            else
            {
                Vector3 size = zone.Kind == "route_gate" ? RouteGateSize : zone.Kind == "breach" ? BreachZoneSize : ArcZoneSize;
                string prefix = zone.Kind == "route_gate" ? "RouteGate" : zone.Kind == "breach" ? "BreachZone" : "ArcZone";
                var blockerGo = new GameObject(prefix + "CollisionShape3D") { layer = PhysicsLayers.ZoneBlocker };
                blockerGo.transform.SetParent(transform, false);
                Blocker = blockerGo.AddComponent<BoxCollider>();
                Blocker.size = Frame.SizeToUnity(new Vec3(size.x, size.y, size.z));
                Blocker.center = new Vector3(0f, size.y * 0.5f, 0f);
                Visual = RuntimeVisualCatalog.AddMesh(transform, prefix + "Visual", RuntimeVisualCatalog.Cube,
                    RuntimeVisualCatalog.Material(ColorFor(zone), unshaded: true, transparent: true, doubleSided: true),
                    new Vector3(0f, size.y * 0.5f, 0f), Quaternion.identity, size, PhysicsLayers.Prop, castShadows: false);
                // VfxCatalog hook: a powered route blocker reads as the biomatter blockage while it is closed.
                if (zone.Kind == "route_gate") Vfx = VfxCatalog.Spawn(VfxCatalog.BiomatterBlockage, transform, Vec3.Zero);
            }
            _renderer = Visual != null ? Visual.GetComponent<MeshRenderer>() : null;
            Sync();
        }

        /// <summary>Position (follows the parent ship root), collider enable and visual state from the model.</summary>
        public void Sync()
        {
            if (Zone == null) return;
            IShipSceneRoot parent = Zone.Parent;
            bool live = parent == null || (parent.IsValid && parent.IsInsideTree);
            Vec3 world = parent != null && live ? parent.GlobalTransform * Zone.LocalPosition : Zone.LocalPosition;
            transform.position = Frame.ToUnity(world);
            if (Blocker != null && Blocker.enabled != (live && Zone.CollisionEnabled)) Blocker.enabled = live && Zone.CollisionEnabled;
            if (Visual != null && Visual.activeSelf != (live && Zone.VisualVisible)) Visual.SetActive(live && Zone.VisualVisible);
            bool vfxOn = live && (Zone.Kind == "fire" || Zone.VisualVisible);
            if (Vfx != null && Vfx.activeSelf != vfxOn) Vfx.SetActive(vfxOn);
            if (_renderer != null && Zone.Kind != "fire" && _appliedVisualState != Zone.VisualState)
            {
                _appliedVisualState = Zone.VisualState;
                _renderer.sharedMaterial = RuntimeVisualCatalog.Material(ColorFor(Zone), unshaded: true, transparent: true, doubleSided: true);
            }
        }

        static Color ColorFor(SessionZone zone)
        {
            switch (zone.Kind)
            {
                case "route_gate": return zone.VisualState == "open" ? RouteGateOpen : RouteGateClosed;
                case "breach": return zone.VisualState == "blocked" ? BreachBlocked : zone.VisualState == "sealed" ? BreachSealed : BreachOpen;
                case "arc": return zone.VisualState == "arcing" ? ArcArcing : ArcDischarged;
                default: return FireColor;
            }
        }
    }
}
