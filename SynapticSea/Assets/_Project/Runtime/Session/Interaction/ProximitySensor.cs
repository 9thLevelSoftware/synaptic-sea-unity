using System.Collections.Generic;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// The player's side of every interaction <c>Area3D</c> overlap (Godot <c>body_entered</c>/<c>body_exited</c> →
    /// <c>candidate_player</c>). One kinematic trigger sphere on the player (Player layer; the Sensor×Player pair
    /// collides) tracks which <see cref="InteractableView"/> triggers (Sensor layer) it overlaps. Trigger events only
    /// report movement, so <see cref="Refresh"/> re-queries with <c>OverlapSphere</c> after a spawn, a teleport, a ship
    /// re-peg, or when views appear or change their collider. It only feeds overlap flags; which handler claims an
    /// interact comes from <c>InteractionRegistry</c>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ProximitySensor : MonoBehaviour
    {
        public const string ObjectName = "ProximitySensor";

        SphereCollider _sphere;
        readonly HashSet<InteractableView> _inside = new HashSet<InteractableView>();
        static readonly Collider[] Hits = new Collider[128];

        public IReadOnlyCollection<InteractableView> Overlapping => _inside;

        public static ProximitySensor AttachTo(PlayerController player)
        {
            Transform existing = player.transform.Find(ObjectName);
            if (existing != null && existing.TryGetComponent(out ProximitySensor found)) return found;
            var go = new GameObject(ObjectName) { layer = PhysicsLayers.Player };
            go.transform.SetParent(player.transform, false);
            go.transform.localPosition = new Vector3(0f, PlayerController.DefaultCollisionHeight * 0.5f, 0f);
            var body = go.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            var sphere = go.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = PlayerController.DefaultCollisionRadius;
            var sensor = go.AddComponent<ProximitySensor>();
            sensor._sphere = sphere;
            return sensor;
        }

        void Awake()
        {
            if (_sphere == null) _sphere = GetComponent<SphereCollider>();
        }

        void OnTriggerEnter(Collider other)
        {
            InteractableView view = other.GetComponentInParent<InteractableView>();
            if (view != null && view.IsSensorCollider(other) && _inside.Add(view)) view.SetPlayerOverlap(true);
        }

        void OnTriggerExit(Collider other)
        {
            InteractableView view = other.GetComponentInParent<InteractableView>();
            if (view != null && view.IsSensorCollider(other) && _inside.Remove(view)) view.SetPlayerOverlap(false);
        }

        void OnDisable() => ClearAll();

        /// <summary>Re-evaluates every overlap now (OverlapSphere against the Sensor layer, triggers included).</summary>
        public void Refresh()
        {
            if (_sphere == null) _sphere = GetComponent<SphereCollider>();
            Physics.SyncTransforms();
            var now = new HashSet<InteractableView>();
            if (isActiveAndEnabled && _sphere != null)
            {
                Vector3 center = transform.TransformPoint(_sphere.center);
                float radius = _sphere.radius * Mathf.Max(transform.lossyScale.x, Mathf.Max(transform.lossyScale.y, transform.lossyScale.z));
                int count = Physics.OverlapSphereNonAlloc(center, radius, Hits, 1 << PhysicsLayers.Sensor, QueryTriggerInteraction.Collide);
                for (int i = 0; i < count; i++)
                {
                    InteractableView view = Hits[i].GetComponentInParent<InteractableView>();
                    if (view != null && view.IsSensorCollider(Hits[i])) now.Add(view);
                    Hits[i] = null;
                }
            }
            foreach (InteractableView gone in new List<InteractableView>(_inside))
            {
                if (gone == null || !now.Contains(gone))
                {
                    _inside.Remove(gone);
                    if (gone != null) gone.SetPlayerOverlap(false);
                }
            }
            foreach (InteractableView view in now)
                if (_inside.Add(view)) view.SetPlayerOverlap(true);
        }

        /// <summary>A view whose sensor collider was disabled or destroyed leaves the overlap set (Unity sends no exit).</summary>
        public void Forget(InteractableView view)
        {
            if (view != null && _inside.Remove(view)) view.SetPlayerOverlap(false);
        }

        void ClearAll()
        {
            foreach (InteractableView view in _inside)
                if (view != null) view.SetPlayerOverlap(false);
            _inside.Clear();
        }
    }
}
