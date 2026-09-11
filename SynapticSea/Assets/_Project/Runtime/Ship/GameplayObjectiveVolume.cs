// Ported from scripts/procgen/gameplay_objective_volume.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// An objective trigger (Godot <c>Area3D</c>): a trigger <see cref="SphereCollider"/> on the Sensor layer plus the
    /// translucent marker sphere. The Godot meta keys (<c>objective_id</c>, <c>objective_sequence</c>,
    /// <c>objective_type</c>, <c>room_id</c>) are the public fields.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameplayObjectiveVolume : MonoBehaviour
    {
        /// <summary><c>objective_completed(objective_id, sequence, objective_type, room_id)</c>.</summary>
        public event Action<string, long, string, string> ObjectiveCompleted;

        public string objectiveId = "";
        public long sequence;
        public string objectiveType = "";
        public string roomId = "";
        public bool completed;
        public float radius = 1.5f;

        /// <summary>Position in the Godot frame, local to the ship root.</summary>
        public Vec3 GodotPosition { get; private set; }

        static readonly Color MarkerColor = new Color(0.2f, 0.95f, 0.45f, 0.25f);

        public void Configure(GdDict objective, Vec3 godotPosition, double triggerRadius = 1.5)
        {
            objectiveId = V.Str(objective.Get("id", ""));
            sequence = V.I64(objective.Get("sequence", 0L));
            objectiveType = V.Str(objective.Get("type", "objective"));
            roomId = V.Str(objective.Get("room_id", ""));
            completed = false;
            radius = (float)triggerRadius;

            name = GodotNodeName.Validate($"ObjectiveVolume_seq{sequence}_{objectiveType}_{objectiveId}");
            GodotPosition = godotPosition;
            transform.localPosition = Frame.ToUnity(godotPosition);
            gameObject.layer = PhysicsLayers.Sensor;

            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }

            var sphere = GetComponent<SphereCollider>();
            if (sphere == null) sphere = gameObject.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = radius;

            // Godot sets SphereMesh.radius only; its height keeps the 1.0 default, so the marker is a flattened
            // ellipsoid (horizontal radius r, vertical half-height 0.5).
            RuntimeVisualCatalog.AddMesh(transform, "MarkerMesh", RuntimeVisualCatalog.Sphere,
                RuntimeVisualCatalog.Material(MarkerColor, unshaded: true, transparent: true, doubleSided: true),
                Vector3.zero, Quaternion.identity, new Vector3(radius * 2f, 1f, radius * 2f), PhysicsLayers.Sensor, castShadows: false);
        }

        public void Complete()
        {
            if (completed) return;
            completed = true;
            ObjectiveCompleted?.Invoke(objectiveId, sequence, objectiveType, roomId);
        }
    }
}
