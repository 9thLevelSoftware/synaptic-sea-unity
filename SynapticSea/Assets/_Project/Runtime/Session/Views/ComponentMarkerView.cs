// Scene half of _rebuild_component_markers / _clear_component_markers in scripts/procgen/playable_generated_ship.gd
// @ 96ecb2b0 (3700-3766).
using System.Collections.Generic;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// Mounted-component markers from <see cref="SessionEvents.ComponentMarkersRebuilt"/>: one
    /// <c>ComponentMarker_&lt;instance&gt;</c> per record. As in Godot, the marker mounts the component's bound imported prop
    /// visual (<see cref="PropVisualBindingCatalog.GetComponentBinding"/> →
    /// <see cref="RuntimePropVisualBinder.MountComponentVisual"/>, <c>visual_source = "imported"</c>) and otherwise carries
    /// Godot's primitive fallback (a 0.45×0.9×0.35 translucent unshaded box, lifted by half its height,
    /// <c>visual_source = "fallback"</c>). The records hold Godot-frame world positions; the marker root sits at the
    /// session origin, so they convert through <see cref="Frame"/> alone.
    /// </summary>
    public sealed class ComponentMarkerView
    {
        public const string NamePrefix = "ComponentMarker_";
        public const string FallbackName = "ComponentMarkerFallback";
        public const string VisualSourceImported = "imported";
        public const string VisualSourceFallback = "fallback";
        static readonly Vec3 BoxSize = new Vec3(0.45f, 0.9f, 0.35f);
        static readonly Color BoxColor = new Color(0.55f, 0.75f, 0.95f, 0.85f);

        readonly Transform _root;
        readonly List<GameObject> _markers = new List<GameObject>();
        readonly Dictionary<GameObject, GdDict> _records = new Dictionary<GameObject, GdDict>();
        readonly PropCatalog _props;
        PropVisualBindingCatalog _bindings;
        bool _bindingsLoaded;

        /// <param name="root">The marker parent (at the session origin).</param>
        /// <param name="bindings">The prop-visual bindings (null: <c>PropVisualBindingCatalog.LoadFromPath()</c> on first rebuild).</param>
        /// <param name="props">The prop prefabs (null: <see cref="RuntimePropVisualBinder.DefaultCatalog"/>).</param>
        public ComponentMarkerView(Transform root, PropVisualBindingCatalog bindings = null, PropCatalog props = null)
        {
            _root = root;
            _props = props;
            if (bindings != null)
            {
                _bindings = bindings;
                _bindingsLoaded = true;
            }
        }

        public IReadOnlyList<GameObject> Markers => _markers;

        /// <summary>
        /// The session record a marker was built from (Godot's marker metas: instance id, component id, room), plus
        /// <c>visual_source</c>.
        /// </summary>
        public GdDict RecordFor(GameObject marker) => marker != null && _records.TryGetValue(marker, out GdDict r) ? r : null;

        /// <summary>Whether a marker mounts the imported prop visual rather than the primitive fallback.</summary>
        public static bool IsImported(GameObject marker) =>
            marker != null && marker.transform.Find(RuntimePropVisualBinder.IMPORTED_VISUAL_NAME) != null;

        PropVisualBindingCatalog Bindings
        {
            get
            {
                if (_bindings == null)
                {
                    _bindings = new PropVisualBindingCatalog();
                    _bindingsLoaded = _bindings.LoadFromPath();
                }
                return _bindingsLoaded ? _bindings : null;
            }
        }

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
                GdDict record = e.DeepCopy();
                PropVisualBindingCatalog bindings = Bindings;
                GdDict binding = bindings != null ? bindings.GetComponentBinding(V.Str(e.Get("component_id", ""))) : new GdDict();
                if (RuntimePropVisualBinder.MountComponentVisual(marker.transform, binding, _props))
                {
                    record["visual_source"] = VisualSourceImported;
                }
                else
                {
                    record["visual_source"] = VisualSourceFallback;
                    RuntimeVisualCatalog.AddMesh(marker.transform, FallbackName, RuntimeVisualCatalog.Cube,
                        RuntimeVisualCatalog.Material(BoxColor, unshaded: true, transparent: true), new Vector3(0f, BoxSize.Y * 0.5f, 0f),
                        Quaternion.identity, Frame.SizeToUnity(BoxSize), PhysicsLayers.Prop, castShadows: false);
                }
                _records[marker] = record;
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
