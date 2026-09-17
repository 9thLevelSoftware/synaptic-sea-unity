// Scene half of _rebuild_component_markers / _clear_component_markers in scripts/procgen/playable_generated_ship.gd
// @ 96ecb2b0 (3700-3766).
using System.Collections.Generic;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// Mounted-component placeholders from <see cref="SessionEvents.ComponentMarkersRebuilt"/>: one
    /// <c>ComponentMarker_&lt;instance&gt;</c> per record, carrying Godot's primitive fallback (a 0.45×0.9×0.35 translucent
    /// unshaded box, lifted by half its height). The records hold Godot-frame world positions; the marker root sits at the
    /// session origin, so they convert through <see cref="Frame"/> alone. Imported prop visuals are not bound yet.
    /// </summary>
    public sealed class ComponentMarkerView
    {
        public const string NamePrefix = "ComponentMarker_";
        static readonly Vec3 BoxSize = new Vec3(0.45f, 0.9f, 0.35f);
        static readonly Color BoxColor = new Color(0.55f, 0.75f, 0.95f, 0.85f);

        readonly Transform _root;
        readonly List<GameObject> _markers = new List<GameObject>();
        readonly Dictionary<GameObject, GdDict> _records = new Dictionary<GameObject, GdDict>();

        public ComponentMarkerView(Transform root) => _root = root;

        public IReadOnlyList<GameObject> Markers => _markers;

        /// <summary>The session record a marker was built from (Godot's marker metas: instance id, component id, room).</summary>
        public GdDict RecordFor(GameObject marker) => marker != null && _records.TryGetValue(marker, out GdDict r) ? r : null;

        public void Rebuild(IReadOnlyList<GdDict> records)
        {
            Clear();
            if (records == null || _root == null) return;
            for (int i = 0; i < records.Count; i++)
            {
                GdDict e = records[i];
                if (e == null || !(e.Get("world_position", null) is Vec3 pos)) continue;
                var marker = new GameObject(GodotNodeName.Validate(NamePrefix + V.Str(e.Get("component_instance_id", (long)i)))) { layer = PhysicsLayers.Prop };
                marker.transform.SetParent(_root, false);
                marker.transform.localPosition = Frame.ToUnity(pos);
                _records[marker] = e.DeepCopy();
                RuntimeVisualCatalog.AddMesh(marker.transform, "ComponentMarkerFallback", RuntimeVisualCatalog.Cube,
                    RuntimeVisualCatalog.Material(BoxColor, unshaded: true, transparent: true), new Vector3(0f, BoxSize.Y * 0.5f, 0f),
                    Quaternion.identity, Frame.SizeToUnity(BoxSize), PhysicsLayers.Prop, castShadows: false);
                _markers.Add(marker);
            }
        }

        public void Clear()
        {
            foreach (GameObject m in _markers)
            {
                if (m == null) continue;
                m.SetActive(false);
                if (Application.isPlaying) Object.Destroy(m);
                else Object.DestroyImmediate(m);
            }
            _markers.Clear();
            _records.Clear();
        }
    }
}
