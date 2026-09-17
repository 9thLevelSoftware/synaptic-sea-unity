// Unity port (no Godot source): Godot's NavigationRegion3D bake was debug-only and was dropped; the threats path
// on a real NavMesh instead (port-status decision 59).
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// The NavMesh of one built ship. Ships are generated at load, so the surface is built then and there (about
    /// 30 ms for a golden deck) from the ship's own <see cref="PhysicsLayers.Structure"/> colliders, under the
    /// <see cref="AgentTypeName"/> agent: the built-in Humanoid agent is too fat for a 1.2 m doorway and walls every
    /// room off. Closed doors and sealed hatches are carved out by <see cref="NavMeshBlocker"/> instead of being
    /// baked, so opening one does not need a rebuild.
    ///
    /// A ship root moves when it docks or travels, which leaves the baked data behind, so the transform is watched
    /// and the data re-placed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShipNavMesh : MonoBehaviour
    {
        /// <summary>Agent type registered by ProjectSettingsBootstrap (radius 0.35, height 1.8, climb 0.3).</summary>
        public const string AgentTypeName = "Threat";

        /// <summary>How tall a marked cell volume is: a deck, so the whole floor of the cell is covered.</summary>
        public const float CellHeight = 3f;

        NavMeshSurface _surface;
        Vector3 _placedAt;
        Quaternion _placedRotation;
        Transform _avoidedRoot;
        List<Vector3> _avoided = new List<Vector3>();
        float _avoidedCellSize;

        /// <summary>The agent type id, resolved by name so it survives a re-registration.</summary>
        public static int AgentTypeId
        {
            get
            {
                for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
                {
                    NavMeshBuildSettings settings = NavMesh.GetSettingsByIndex(i);
                    if (NavMesh.GetSettingsNameFromID(settings.agentTypeID) == AgentTypeName) return settings.agentTypeID;
                }
                return 0;
            }
        }

        /// <summary>The costly area a burning room's floor is marked with (ProjectSettingsBootstrap).</summary>
        public const string FireAreaName = "ThreatFire";

        public bool HasNavMesh => _surface != null && _surface.navMeshData != null;

        /// <summary>
        /// Marks the floor of every burning cell as <see cref="FireAreaName"/>, which costs what the nav graph
        /// charges for fire, so a threat walks around a fire rather than through it. The volumes are build-time, so
        /// a change rebuilds the surface — the burning set changes when a fire starts or dies, not every frame.
        /// </summary>
        public void SetAvoidedCells(IReadOnlyList<Vector3> centres, float cellSize)
        {
            if (Unchanged(centres, cellSize)) return;
            _avoided = new List<Vector3>(centres);
            _avoidedCellSize = cellSize;
            if (_avoidedRoot != null)
            {
                // Destroy only takes effect at the end of the frame, and the rebuild below is in this one, so the
                // old volumes are detached and switched off first or they would be collected all over again.
                _avoidedRoot.gameObject.SetActive(false);
                _avoidedRoot.SetParent(null, false);
                Destroy(_avoidedRoot.gameObject);
            }
            _avoidedRoot = null;
            if (_avoided.Count > 0)
            {
                int area = NavMesh.GetAreaFromName(FireAreaName);
                if (area < 0)
                {
                    Debug.LogWarning($"[ShipNavMesh] no '{FireAreaName}' NavMesh area; run the project bootstrap. Fires are not avoided.");
                    return;
                }
                _avoidedRoot = new GameObject("NavMeshFireVolumes") { layer = PhysicsLayers.Structure }.transform;
                _avoidedRoot.SetParent(transform, false);
                foreach (Vector3 centre in _avoided)
                {
                    // The surface filters modifier volumes by its own build layer mask, so a volume that is not on
                    // the layer the geometry comes from is silently ignored.
                    var volume = new GameObject("FireCell") { layer = PhysicsLayers.Structure }.AddComponent<NavMeshModifierVolume>();
                    volume.transform.SetParent(_avoidedRoot, false);
                    volume.transform.position = centre;
                    // Centred on the cell, not resting on it: a floor voxel sitting exactly on the box's bottom face
                    // counts as outside, and the cell's own height is where the floor is. Half a deck each way keeps
                    // the box clear of the decks above and below (4 m apart).
                    volume.size = new Vector3(cellSize, CellHeight, cellSize);
                    volume.center = Vector3.zero;
                    volume.area = area;
                }
            }
            Rebuild();
        }

        bool Unchanged(IReadOnlyList<Vector3> centres, float cellSize)
        {
            if (centres == null) centres = System.Array.Empty<Vector3>();
            if (centres.Count != _avoided.Count || !Mathf.Approximately(cellSize, _avoidedCellSize)) return false;
            for (int i = 0; i < centres.Count; i++)
                if (centres[i] != _avoided[i]) return false;
            return true;
        }

        /// <summary>Builds (or rebuilds) the surface of <paramref name="shipRoot"/>; returns null when it has no floor.</summary>
        public static ShipNavMesh Build(GameObject shipRoot)
        {
            if (shipRoot == null) return null;
            // TryGetComponent, not ?? : a missing component comes back as Unity's fake null, which ?? keeps.
            if (!shipRoot.TryGetComponent(out ShipNavMesh nav)) nav = shipRoot.AddComponent<ShipNavMesh>();
            nav.Rebuild();
            return nav.HasNavMesh ? nav : null;
        }

        public void Rebuild()
        {
            if (_surface == null)
            {
                if (!TryGetComponent(out _surface)) _surface = gameObject.AddComponent<NavMeshSurface>();
                _surface.collectObjects = CollectObjects.Children;
                _surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                _surface.layerMask = 1 << PhysicsLayers.Structure;
                _surface.agentTypeID = AgentTypeId;
                // Godot's threats never left the deck they spawned on, and neither engine has a walkable deck
                // change (port-status decision 57), so no links are generated between decks.
            }
            _surface.RemoveData();
            _surface.BuildNavMesh();
            _placedAt = transform.position;
            _placedRotation = transform.rotation;
        }

        /// <summary>
        /// A docked or travelling ship carries its NavMesh with it. The data is placed where the root stood when it
        /// was built, so moving the root re-places it (the geometry itself is unchanged, so no rebuild is needed).
        /// </summary>
        void LateUpdate()
        {
            if (!HasNavMesh) return;
            if (transform.position == _placedAt && transform.rotation == _placedRotation) return;
            _placedAt = transform.position;
            _placedRotation = transform.rotation;
            _surface.RemoveData();
            _surface.AddData();
        }

        void OnDestroy()
        {
            if (_surface != null) _surface.RemoveData();
        }
    }
}
