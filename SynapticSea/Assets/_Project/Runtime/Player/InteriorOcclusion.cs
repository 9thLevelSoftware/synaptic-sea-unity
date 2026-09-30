using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SynapticSea.Runtime
{
    /// <summary>Camera-only foreground wall reveal. Physics, AI and source/shared materials are never changed.</summary>
    public sealed class InteriorOcclusion
    {
        const int Mask = (1 << PhysicsLayers.Structure) | (1 << PhysicsLayers.Portal) | (1 << PhysicsLayers.Ceiling) | (1 << PhysicsLayers.Walkable);
        const float ReleaseDelay = 0.12f;
        readonly Dictionary<Renderer, State> _states = new Dictionary<Renderer, State>();
        readonly Dictionary<Renderer, bool> _hiddenThreats = new Dictionary<Renderer, bool>();
        readonly HashSet<Renderer> _wanted = new HashSet<Renderer>();
        Vector3? _lastPlayer;
        float _clock;
        float _nextRefresh;
        readonly List<(StructuralModule module, Renderer renderer)> _visuals = new List<(StructuralModule, Renderer)>();
        public int ActiveRendererCount => _states.Count;

        sealed class State
        {
            public Renderer renderer;
            public Material[] originals, fades;
            public MaterialPropertyBlock[] blocks, working;
            public Color[] colors;
            public bool originalOff, canFade;
            public float alpha = 1, lastWanted;
            public StructuralModule module;
            public string integrity;
            public GameObject outline;
        }

        public void Update(Camera camera, Vector3? player, Vector3? focus, float dt)
        {
            if (camera == null || !player.HasValue) { RestoreAll(); return; }
            if (_lastPlayer.HasValue && Vector3.Distance(_lastPlayer.Value, player.Value) > 3f) RestoreAll();
            _lastPlayer = player;
            _clock += Mathf.Clamp(dt, 0, 0.1f);
            if (_clock >= _nextRefresh || _visuals.Count == 0) RefreshModules();
            _wanted.Clear();
            Collect(camera, player.Value + Vector3.up * 0.2f);
            Collect(camera, player.Value + Vector3.up * 0.8f);
            Collect(camera, player.Value + Vector3.up * 1.4f);
            // Shoulder rays protect the character's width, rather than only its centre pixel.
            Collect(camera, player.Value + Vector3.up * 0.8f + camera.transform.right * 0.3f);
            Collect(camera, player.Value + Vector3.up * 0.8f - camera.transform.right * 0.3f);
            if (focus.HasValue) Collect(camera, focus.Value + Vector3.up * 1.2f);

            foreach (var renderer in _wanted)
            {
                if (!_states.TryGetValue(renderer, out var state)) _states.Add(renderer, state = Begin(renderer));
                state.lastWanted = _clock;
            }
            foreach (var pair in new List<KeyValuePair<Renderer, State>>(_states))
            {
                var state = pair.Value;
                if (pair.Key == null || !pair.Key.gameObject.activeInHierarchy || (state.module != null && state.integrity != state.module.integrityState))
                { Restore(state, state.module != null && state.module.HasSingleVisual && state.integrity != state.module.integrityState); _states.Remove(pair.Key); continue; }
                bool wanted = _wanted.Contains(pair.Key) || _clock - state.lastWanted < ReleaseDelay;
                state.alpha = Mathf.MoveTowards(state.alpha, wanted ? 0.08f : 1f, Mathf.Max(0, dt) / 0.16f);
                Apply(state);
                if (!wanted && state.alpha >= 1f) { Restore(state); _states.Remove(pair.Key); }
            }
        }

        void Collect(Camera camera, Vector3 anchor)
        {
            var viewport = camera.WorldToViewportPoint(anchor);
            if (viewport.z <= 0 || viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1) return;
            // Orthographic rays are parallel: a camera.position->anchor ray would select the wrong walls.
            var ray = camera.ViewportPointToRay(viewport);
            float distance = Vector3.Dot(anchor - ray.origin, ray.direction) - 0.08f;
            if (distance <= 0) return;
            // DockedShipGeometry replaces overlapping wrapper colliders with compound union boxes. Those boxes
            // deliberately have no single module owner, so camera reveal also queries the visual bounds directly.
            foreach (var visual in _visuals)
            {
                var module = visual.module; var renderer = visual.renderer;
                if (module == null || renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy
                    || (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
                bool upperDeck = (module.layer == "floor" || module.layer == "ceiling") && renderer.bounds.min.y > anchor.y + 0.6f;
                if (module.layer != "edge" && !upperDeck) continue;
                if (renderer.bounds.IntersectRay(ray, out float hitDistance) && hitDistance < distance) _wanted.Add(renderer);
            }
            foreach (var hit in Physics.RaycastAll(ray, distance, Mask, QueryTriggerInteraction.Ignore))
            {
                var module = hit.collider.GetComponentInParent<StructuralModule>();
                if (module != null)
                {
                    bool upperDeck = (module.layer == "floor" || module.layer == "ceiling") && hit.point.y > anchor.y + 0.6f;
                    if (module.layer != "edge" && !upperDeck) continue; // The player's own support floor always renders.
                    foreach (var renderer in module.GetComponentsInChildren<Renderer>())
                        if (renderer.enabled && (camera.cullingMask & (1 << renderer.gameObject.layer)) != 0) _wanted.Add(renderer);
                }
                else
                {
                    var portal = hit.collider.GetComponentInParent<AuthoredPortalRuntime>();
                    if (portal != null)
                        foreach (var renderer in portal.GetComponentsInChildren<Renderer>()) if (renderer.enabled) _wanted.Add(renderer);
                }
            }
        }

        public void RefreshModules()
        {
            _visuals.Clear();
            foreach (var module in Object.FindObjectsByType<StructuralModule>(FindObjectsSortMode.None))
                foreach (var renderer in module.GetComponentsInChildren<Renderer>(true)) _visuals.Add((module, renderer));
            _nextRefresh = _clock + 0.5f;
        }

        static bool SupportsRetainedFade(Material material)
        {
            if (material == null || material.shader == null) return false;
            if (material.shader.name != RuntimeVisualCatalog.LitShaderName && material.shader.name != RuntimeVisualCatalog.UnlitShaderName) return false;
            // Only these feature combinations are retained by the serialized library. More complex/purchased
            // shaders take the material-independent hide/footprint fallback instead of risking a stripped variant.
            foreach (string keyword in material.shaderKeywords)
                if (keyword != "_EMISSION" && keyword != "_SURFACE_TYPE_TRANSPARENT") return false;
            return true;
        }

        State Begin(Renderer renderer)
        {
            var state = new State { renderer = renderer, originals = renderer.sharedMaterials, originalOff = renderer.forceRenderingOff,
                module = renderer.GetComponentInParent<StructuralModule>(), canFade = true, lastWanted = _clock };
            state.integrity = state.module != null ? state.module.integrityState : "";
            foreach (var material in state.originals) state.canFade &= SupportsRetainedFade(material);
            if (state.canFade)
            {
                state.fades = new Material[state.originals.Length];
                state.blocks = new MaterialPropertyBlock[state.originals.Length];
                state.working = new MaterialPropertyBlock[state.originals.Length];
                state.colors = new Color[state.originals.Length];
                for (int i = 0; i < state.originals.Length; i++)
                {
                    var source = state.originals[i];
                    bool unlit = source.shader.name == RuntimeVisualCatalog.UnlitShaderName;
                    var fade = new Material(RuntimeVisualMaterialLibrary.Load().Select(unlit, true, source.IsKeywordEnabled("_EMISSION")))
                        { name = source.name + "_CameraFade", hideFlags = HideFlags.DontSave };
                    fade.CopyPropertiesFromMaterial(source);
                    fade.SetFloat("_Surface", 1); fade.SetFloat("_Blend", 0);
                    fade.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); fade.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    fade.SetFloat("_SrcBlendAlpha", (float)BlendMode.One); fade.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                    fade.SetFloat("_ZWrite", 0); fade.SetOverrideTag("RenderType", "Transparent");
                    fade.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); fade.renderQueue = (int)RenderQueue.Transparent;
                    fade.SetShaderPassEnabled("ShadowCaster", false); fade.SetShaderPassEnabled("DepthOnly", false);
                    state.fades[i] = fade;
                    state.blocks[i] = new MaterialPropertyBlock(); state.working[i] = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(state.blocks[i], i); renderer.GetPropertyBlock(state.working[i], i);
                    state.colors[i] = state.blocks[i].HasColor("_BaseColor") ? state.blocks[i].GetColor("_BaseColor") : source.GetColor("_BaseColor");
                }
                renderer.sharedMaterials = state.fades;
            }
            // A thin floor-level footprint keeps a removed foreground wall/door boundary legible. It has no collider.
            var bounds = renderer.bounds;
            state.outline = RuntimeVisualCatalog.AddMesh(null, "CameraWallFootprint", RuntimeVisualCatalog.Cube,
                RuntimeVisualCatalog.Material(new Color(0.65f, 0.72f, 0.8f, 0.22f), true, true),
                new Vector3(bounds.center.x, bounds.min.y + 0.025f, bounds.center.z), Quaternion.identity,
                new Vector3(Mathf.Max(0.05f, bounds.size.x), 0.05f, Mathf.Max(0.05f, bounds.size.z)), PhysicsLayers.Prop, false);
            return state;
        }

        static void Apply(State state)
        {
            if (state.canFade)
            {
                for (int i = 0; i < state.fades.Length; i++)
                {
                    Color color = state.colors[i]; color.a *= state.alpha;
                    state.working[i].SetColor("_BaseColor", color);
                    state.renderer.SetPropertyBlock(state.working[i], i);
                }
            }
            else state.renderer.forceRenderingOff = true; // Unsupported shader: never mutate it or guess its blending.
        }

        static void Restore(State state, bool preserveCurrentTint = false)
        {
            if (state.renderer != null)
            {
                state.renderer.forceRenderingOff = state.originalOff;
                if (state.canFade)
                {
                    state.renderer.sharedMaterials = state.originals;
                    if (!preserveCurrentTint)
                        for (int i = 0; i < state.blocks.Length; i++) state.renderer.SetPropertyBlock(state.blocks[i].isEmpty ? null : state.blocks[i], i);
                }
            }
            if (state.fades != null) foreach (var material in state.fades) Destroy(material);
            Destroy(state.outline);
        }

        public void UpdateThreatVisibility(IEnumerable<GameObject> threats, Vector3 player)
        {
            var live = new HashSet<Renderer>();
            foreach (var node in threats)
            {
                if (node == null) continue;
                Vector3 from = player + Vector3.up, to = node.transform.position + Vector3.up;
                // Apply before the first fade frame too; no one-frame enemy reveal as a wall starts transitioning.
                bool hidden = Physics.Linecast(from, to, Mask | (1 << PhysicsLayers.ZoneBlocker), QueryTriggerInteraction.Ignore);
                foreach (var renderer in node.GetComponentsInChildren<Renderer>(true))
                {
                    live.Add(renderer);
                    if (hidden)
                    {
                        if (!_hiddenThreats.ContainsKey(renderer)) _hiddenThreats.Add(renderer, renderer.forceRenderingOff);
                        renderer.forceRenderingOff = true;
                    }
                    else if (_hiddenThreats.TryGetValue(renderer, out bool original)) { renderer.forceRenderingOff = original; _hiddenThreats.Remove(renderer); }
                }
            }
            foreach (var pair in new List<KeyValuePair<Renderer, bool>>(_hiddenThreats))
                if (pair.Key == null || !live.Contains(pair.Key)) { if (pair.Key != null) pair.Key.forceRenderingOff = pair.Value; _hiddenThreats.Remove(pair.Key); }
        }

        public void RestoreAll()
        {
            foreach (var state in _states.Values) Restore(state);
            _states.Clear(); _wanted.Clear();
            foreach (var pair in _hiddenThreats) if (pair.Key != null) pair.Key.forceRenderingOff = pair.Value;
            _hiddenThreats.Clear(); _lastPlayer = null;
        }

        static void Destroy(Object obj) { if (obj == null) return; if (Application.isPlaying) Object.Destroy(obj); else Object.DestroyImmediate(obj); }
    }
}
