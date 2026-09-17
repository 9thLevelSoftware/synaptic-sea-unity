// Unity port (no Godot source): the NavMesh half of a blocker collider (port-status decision 59).
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// Keeps a blocker collider — a closed door or hatch, a closed route gate — out of the threats' NavMesh while it
    /// is on. The collider itself cannot simply be baked: these open and close during a run, and a baked surface
    /// would keep blocking a door the player just unlocked. A carving <see cref="NavMeshObstacle"/> cuts the hole
    /// instead, which the engine updates as the obstacle switches on and off.
    ///
    /// A blocker that would otherwise be baked (a structural wrapper, on the Structure layer) is also marked
    /// <see cref="NavMeshModifier.ignoreFromBuild"/>, so the carve is the only thing standing in the doorway.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NavMeshBlocker : MonoBehaviour
    {
        Collider _collider;
        NavMeshObstacle _obstacle;

        /// <summary>Carves <paramref name="blocker"/> out of the NavMesh whenever that collider is enabled.</summary>
        public static NavMeshBlocker Attach(Collider blocker)
        {
            if (blocker == null) return null;
            // TryGetComponent, not ?? : a missing component comes back as Unity's fake null, which ?? keeps.
            if (!blocker.TryGetComponent(out NavMeshBlocker component)) component = blocker.gameObject.AddComponent<NavMeshBlocker>();
            component.Bind(blocker);
            return component;
        }

        /// <summary>Keeps every collider under <paramref name="root"/> out of the bake: a carve blocks it instead.</summary>
        public static void IgnoreFromBuild(GameObject root)
        {
            if (root == null) return;
            if (!root.TryGetComponent(out NavMeshModifier modifier)) modifier = root.AddComponent<NavMeshModifier>();
            modifier.ignoreFromBuild = true;
        }

        void Bind(Collider blocker)
        {
            _collider = blocker;
            if (_obstacle == null)
            {
                if (!TryGetComponent(out _obstacle)) _obstacle = gameObject.AddComponent<NavMeshObstacle>();
                _obstacle.shape = NavMeshObstacleShape.Box;
                _obstacle.carving = true;
                // The blockers do not move once placed, so a carve only has to be re-cut when one opens or closes.
                _obstacle.carveOnlyStationary = true;
            }
            if (blocker is BoxCollider box)
            {
                _obstacle.center = box.center;
                _obstacle.size = box.size;
            }
            else
            {
                Bounds bounds = blocker.bounds;
                _obstacle.center = transform.InverseTransformPoint(bounds.center);
                _obstacle.size = bounds.size;
            }
            Apply();
        }

        void LateUpdate() => Apply();

        void Apply()
        {
            if (_obstacle == null) return;
            bool blocking = _collider != null && _collider.enabled && _collider.gameObject.activeInHierarchy;
            if (_obstacle.enabled != blocking) _obstacle.enabled = blocking;
        }
    }
}
