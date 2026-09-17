// Scene half of the Label3D world labels in scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 (_make_affordance_label,
// _create_unsafe_room_marker, _create_arc_zone_label, _apply_world_label_scale).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// World labels drawn as UI Toolkit labels projected from world anchors onto the HUD document each frame. Godot used
    /// billboarded, fixed-size, no-depth-test <c>Label3D</c>s; projecting onto the HUD panel gives the same fixed on-screen
    /// size, stays crisp under the orthographic iso camera, and uses the project font (no TextMeshPro resources needed).
    /// <list type="bullet">
    /// <item>Size: <see cref="Entry.BasePixels"/> × text scale (Godot divided <c>pixel_size</c> by the text scale).</item>
    /// <item>Culling: affordance labels hide beyond <see cref="CullDistance"/> metres from the player; hazard warnings are
    /// never distance-culled (only hidden off-screen / behind the camera).</item>
    /// </list>
    /// The anchor set is model-driven and headless-safe: without a container or camera the entries still track their
    /// visibility and cull state (tests).
    /// </summary>
    public sealed class WorldLabelLayer
    {
        public const float CullDistance = 24f;
        public const float AffordancePixels = 18f;
        public const float HazardPixels = 20f;

        public sealed class Entry
        {
            public string Id = "";
            public string Text = "";
            public Color Color = Color.white;
            public bool Hazard;
            public float BasePixels = AffordancePixels;
            /// <summary>Model visibility (e.g. the breach marker only while the breach is open).</summary>
            public bool Visible = true;
            /// <summary>Unity world position of the anchor (null = no anchor this frame).</summary>
            public Func<Vector3?> Anchor;
            /// <summary>Result of the last <see cref="WorldLabelLayer.Update"/>: model-visible and not culled.</summary>
            public bool Shown;
            public Label Label;
        }

        readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        VisualElement _container;
        float _textScale = 1f;

        public IReadOnlyDictionary<string, Entry> Entries => _entries;

        /// <summary>The HUD element labels are drawn into (null = headless).</summary>
        public VisualElement Container
        {
            get => _container;
            set
            {
                if (_container == value) return;
                _container = value;
                foreach (Entry e in _entries.Values)
                {
                    e.Label?.RemoveFromHierarchy();
                    if (_container != null) _container.Add(e.Label);
                }
            }
        }

        /// <summary>Accessibility text scale (1, 1.5, 2); world labels grow with it.</summary>
        public float TextScale
        {
            get => _textScale;
            set
            {
                _textScale = Mathf.Clamp(value, 1f, 2f);
                foreach (Entry e in _entries.Values) ApplyStyle(e);
            }
        }

        public Entry Set(string id, string text, Func<Vector3?> anchor, Color color, bool hazard, bool visible = true)
        {
            if (!_entries.TryGetValue(id, out Entry entry))
            {
                entry = new Entry { Id = id, Label = new Label { name = "world-label:" + id, pickingMode = PickingMode.Ignore } };
                entry.Label.AddToClassList("hud-world-label");
                entry.Label.style.position = Position.Absolute;
                entry.Label.style.unityTextAlign = TextAnchor.LowerCenter;
                entry.Label.style.unityFontStyleAndWeight = FontStyle.Bold;
                entry.Label.style.translate = new Translate(Length.Percent(-50), Length.Percent(-100));
                entry.Label.style.unityTextOutlineWidth = 1f;
                entry.Label.style.unityTextOutlineColor = Color.black;
                entry.Label.style.whiteSpace = WhiteSpace.NoWrap;
                entry.Label.style.display = DisplayStyle.None;
                _entries[id] = entry;
                _container?.Add(entry.Label);
            }
            entry.Text = text ?? "";
            entry.Anchor = anchor;
            entry.Color = color;
            entry.Hazard = hazard;
            entry.BasePixels = hazard ? HazardPixels : AffordancePixels;
            entry.Visible = visible;
            entry.Label.text = entry.Text;
            ApplyStyle(entry);
            return entry;
        }

        public bool Has(string id) => _entries.ContainsKey(id);

        public Entry Get(string id) => _entries.TryGetValue(id, out Entry e) ? e : null;

        public void SetVisible(string id, bool visible)
        {
            if (_entries.TryGetValue(id, out Entry e)) e.Visible = visible;
        }

        public void SetText(string id, string text, Color color)
        {
            if (!_entries.TryGetValue(id, out Entry e)) return;
            if (e.Text != text)
            {
                e.Text = text ?? "";
                e.Label.text = e.Text;
            }
            if (e.Color != color)
            {
                e.Color = color;
                ApplyStyle(e);
            }
        }

        public void Remove(string id)
        {
            if (!_entries.TryGetValue(id, out Entry e)) return;
            e.Label?.RemoveFromHierarchy();
            _entries.Remove(id);
        }

        public void RemoveWhere(Func<Entry, bool> predicate)
        {
            var doomed = new List<string>();
            foreach (Entry e in _entries.Values)
                if (predicate(e)) doomed.Add(e.Id);
            foreach (string id in doomed) Remove(id);
        }

        public void Clear() => RemoveWhere(_ => true);

        void ApplyStyle(Entry e)
        {
            if (e.Label == null) return;
            e.Label.style.fontSize = Mathf.Round(e.BasePixels * _textScale);
            e.Label.style.color = new Color(e.Color.r, e.Color.g, e.Color.b, 1f);
        }

        /// <summary>
        /// Projects every anchor through <paramref name="camera"/> onto the container's panel and applies culling.
        /// <paramref name="player"/> is the Unity-world player position (null = no player: affordance labels hide).
        /// </summary>
        public void Update(Camera camera, Vector3? player)
        {
            IPanel panel = _container?.panel;
            foreach (Entry e in _entries.Values)
            {
                Vector3? anchor = e.Anchor?.Invoke();
                bool show = e.Visible && anchor.HasValue && (e.Hazard || WithinCull(anchor.Value, player));
                if (show && camera != null)
                {
                    Vector3 viewport = camera.WorldToViewportPoint(anchor.Value);
                    if (viewport.z < 0f || viewport.x < -0.1f || viewport.x > 1.1f || viewport.y < -0.1f || viewport.y > 1.1f) show = false;
                }
                e.Shown = show;
                if (e.Label == null) continue;
                if (show && panel != null && camera != null)
                {
                    Vector2 p = RuntimePanelUtils.CameraTransformWorldToPanel(panel, anchor.Value, camera);
                    e.Label.style.left = p.x;
                    e.Label.style.top = p.y;
                    e.Label.style.display = DisplayStyle.Flex;
                }
                else
                {
                    e.Label.style.display = DisplayStyle.None;
                }
            }
        }

        static bool WithinCull(Vector3 anchor, Vector3? player)
        {
            if (!player.HasValue) return false;
            float dx = anchor.x - player.Value.x;
            float dz = anchor.z - player.Value.z;
            return dx * dx + dz * dz <= CullDistance * CullDistance;
        }
    }
}
