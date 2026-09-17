// Unity port addition (no Godot source): keep a spawned player out of wall geometry (port-status decision 55).
using System;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// Finds a standing spot for the player capsule that overlaps no blocking collider and has floor under it.
    /// The session spawns at the loader's start marker. On the home ship the docked life boat's airlock edge sits on
    /// that marker (decision 34, open items), so one of its walls runs through the spawn and the CharacterController
    /// used to depenetrate on the first physics step. The search walks rings around the requested point in a fixed
    /// order, so the same geometry always gives the same spot.
    /// </summary>
    public static class SpawnClearance
    {
        /// <summary>Distance between ring samples and between rings (m).</summary>
        public const float SearchStep = 0.5f;
        /// <summary>The farthest ring searched (m); one cell is 4 m.</summary>
        public const float MaxSearchRadius = 4f;
        /// <summary>Gap kept between the capsule and the floor or walls (m).</summary>
        public const float Skin = 0.05f;
        /// <summary>How far below the feet a floor must be (m).</summary>
        public const float FloorProbeDepth = 1.5f;

        public static int BlockingMask => (1 << PhysicsLayers.Structure) | (1 << PhysicsLayers.ZoneBlocker) | (1 << PhysicsLayers.Portal);

        /// <summary>True when a player capsule standing with its feet at <paramref name="feet"/> overlaps no blocking collider.</summary>
        public static bool IsClear(Vector3 feet, float radius = PlayerController.DefaultCollisionRadius, float height = PlayerController.DefaultCollisionHeight)
        {
            Vector3 bottom = feet + Vector3.up * (radius + Skin);
            Vector3 top = feet + Vector3.up * Mathf.Max(radius + Skin, height - radius);
            return !Physics.CheckCapsule(bottom, top, radius, BlockingMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>The floor collider under <paramref name="feet"/>, or null.</summary>
        public static Collider FloorUnder(Vector3 feet)
        {
            Vector3 from = feet + Vector3.up * 0.5f;
            RaycastHit[] hits = Physics.RaycastAll(from, Vector3.down, 0.5f + FloorProbeDepth, 1 << PhysicsLayers.Structure, QueryTriggerInteraction.Ignore);
            Collider best = null;
            float bestDistance = float.MaxValue;
            foreach (RaycastHit hit in hits)
            {
                if (hit.distance < bestDistance)
                {
                    bestDistance = hit.distance;
                    best = hit.collider;
                }
            }
            return best;
        }

        /// <summary>
        /// The requested point when it is already clear, else the nearest clear sample whose floor satisfies
        /// <paramref name="acceptFloor"/> (null accepts any floor). Rings grow by <see cref="SearchStep"/>; each ring starts
        /// at +X and turns counter-clockwise. False when nothing within <see cref="MaxSearchRadius"/> qualifies.
        /// </summary>
        public static bool TryFindClear(Vector3 feet, Func<Collider, bool> acceptFloor, out Vector3 clear,
            float radius = PlayerController.DefaultCollisionRadius, float height = PlayerController.DefaultCollisionHeight)
        {
            Physics.SyncTransforms();
            clear = feet;
            if (IsClear(feet, radius, height))
                return true;
            for (float ring = SearchStep; ring <= MaxSearchRadius + 1e-4f; ring += SearchStep)
            {
                int samples = Mathf.Max(8, Mathf.CeilToInt(2f * Mathf.PI * ring / SearchStep));
                for (int i = 0; i < samples; i++)
                {
                    float angle = i * 2f * Mathf.PI / samples;
                    Vector3 candidate = feet + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * ring;
                    if (!IsClear(candidate, radius, height))
                        continue;
                    Collider floor = FloorUnder(candidate);
                    if (floor == null || (acceptFloor != null && !acceptFloor(floor)))
                        continue;
                    clear = candidate;
                    return true;
                }
            }
            return false;
        }
    }
}
