// Scene half of the ship scene roots scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 parented under itself
// (the GeneratedShipLoader, generated derelict roots and the LifeBoat node tree).
using System.Collections.Generic;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// A ship scene root GameObject as the session sees it (<see cref="IShipSceneRoot"/>). The Godot-frame transform is
    /// authoritative and kept exactly (docking math reads back what it wrote); every write is mirrored onto the Unity
    /// transform through <see cref="Frame"/>. Roots are parented to the session root, which sits at the world origin,
    /// so the global transform equals the local one while attached (a detached Godot node's global transform is its
    /// local one too).
    /// </summary>
    public class SceneShipRoot : IShipSceneRoot, IShipInteriorView
    {
        public readonly GameObject GameObject;
        Xform3 _transform = Xform3.Identity;
        bool _freed;
        bool _attached;

        /// <summary>Room-node LOCAL positions (Godot frame) for <see cref="IShipInteriorView"/>.</summary>
        protected readonly List<Vec3> RoomPositions = new List<Vec3>();

        public SceneShipRoot(GameObject gameObject)
        {
            GameObject = gameObject;
            if (GameObject != null) Frame.ApplyLocal(GameObject.transform, _transform);
        }

        public bool IsValid => !_freed && GameObject != null;
        public bool IsInsideTree => IsValid && _attached;

        public Xform3 Transform
        {
            get => _transform;
            set
            {
                _transform = value;
                if (GameObject != null) Frame.ApplyLocal(GameObject.transform, value);
            }
        }

        public Xform3 GlobalTransform => _transform;

        public IReadOnlyList<Vec3> StructureRoomLocalPositions() => RoomPositions;

        /// <summary><c>add_child(root)</c>: parent under the session root and show it.</summary>
        internal void Attach(Transform sessionRoot)
        {
            if (!IsValid) return;
            GameObject.transform.SetParent(sessionRoot, false);
            Frame.ApplyLocal(GameObject.transform, _transform);
            GameObject.SetActive(true);
            _attached = true;
        }

        /// <summary><c>remove_child + queue_free</c>: gone from physics this frame, destroyed at the end of it.</summary>
        internal void Free()
        {
            if (_freed) return;
            _freed = true;
            _attached = false;
            if (GameObject == null) return;
            GameObject.SetActive(false);
            GameObject.transform.SetParent(null, false);
            if (Application.isPlaying) Object.Destroy(GameObject);
            else Object.DestroyImmediate(GameObject);
        }

        /// <summary>Unity world position of a Godot-frame position local to this root.</summary>
        public Vector3 LocalToWorld(Vec3 godotLocal) => Frame.ToUnity(_transform * godotLocal);
    }

    /// <summary>The lifeboat node tree <c>LifeBoatBuilder.build</c> described (room nodes under <c>ShipStructure</c>).</summary>
    public sealed class LifeboatSceneRoot : SceneShipRoot
    {
        public LifeboatSceneRoot(GameObject gameObject, IEnumerable<Vec3> roomPositions) : base(gameObject)
        {
            RoomPositions.AddRange(roomPositions);
        }
    }
}
