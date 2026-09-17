// Scene half of scripts/tools/threat_placeholder_renderer.gd and the placeholder nodes of
// scripts/systems/threat_manager.gd / hallucination_manager.gd @ 96ecb2b0.
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// Builds the catalog-backed primitive placeholder (<c>res://data/combat/threat_visual_catalog.json</c>: primitive,
    /// scale, albedo, y_offset) for threats (Threat layer, with a trigger capsule so line-of-sight rays can be debugged)
    /// and hallucination phantoms (Hallucination layer). Godot primitive meshes map 1:1 onto Unity's unit primitives.
    /// </summary>
    public static class ThreatPlaceholderFactory
    {
        public const string CatalogPath = "res://data/combat/threat_visual_catalog.json";

        public static GameObject Build(string archetypeId, GdArray tags, Transform parent, int layer)
        {
            GdDict catalog = CatalogRegistry.Exists(CatalogPath) ? CatalogRegistry.LoadDict(CatalogPath) ?? new GdDict() : new GdDict();
            GdDict archetype = catalog.GetDictOrEmpty("archetypes").GetDictOrEmpty(archetypeId ?? "");
            var node = new GameObject(GodotNodeName.Validate("Threat_" + archetypeId)) { layer = layer };
            node.transform.SetParent(parent, false);
            string primitive = V.Str(archetype.Get("primitive", ""));
            if (primitive.Length == 0)
                primitive = tags != null && tags.Contains("swarm") ? "sphere" : tags != null && tags.Contains("anchored") ? "cylinder" : "capsule";
            Mesh mesh;
            Vector3 scale;
            switch (primitive)
            {
                case "sphere":
                    mesh = RuntimeVisualCatalog.Sphere;
                    scale = Vector3.one;
                    break;
                case "cylinder":
                    mesh = RuntimeVisualCatalog.Cylinder(0.5f, 0.5f, 2f);
                    scale = Vector3.one;
                    break;
                case "box":
                    mesh = RuntimeVisualCatalog.Cube;
                    scale = Vector3.one;
                    break;
                default:
                    mesh = RuntimeVisualCatalog.Capsule;
                    scale = Vector3.one;
                    break;
            }
            float s = (float)V.F64(archetype.Get("scale", 1.0));
            Color color = AtmosphereApplier.ColorValue(archetype.Get("albedo", ""), FallbackColor(archetypeId));
            var meshGo = RuntimeVisualCatalog.AddMesh(node.transform, "Mesh", mesh, RuntimeVisualCatalog.Material(color),
                new Vector3(0f, (float)V.F64(archetype.Get("y_offset", 0.0)), 0f), Quaternion.identity, scale * s, layer, castShadows: true);
            if (layer == PhysicsLayers.Threat)
            {
                var capsule = meshGo.AddComponent<CapsuleCollider>();
                capsule.isTrigger = true;
            }
            return node;
        }

        static Color FallbackColor(string archetypeId)
        {
            switch (archetypeId)
            {
                case "biomatter_swarm": return new Color(0.55f, 1.0f, 0.45f);
                case "puppet_corpse": return new Color(0.85f, 0.82f, 0.7f);
                case "stalker": return new Color(0.7f, 0.7f, 1.0f);
                case "mimic": return new Color(1.0f, 0.55f, 0.25f);
                case "hull_tendril": return new Color(0.55f, 0.9f, 1.0f);
                default: return new Color(1.0f, 0.35f, 0.35f);
            }
        }
    }

    /// <summary>Applies <see cref="ThreatRuntime"/>'s placeholder events (spawn, move, remove, clear).</summary>
    public sealed class ThreatPlaceholderView
    {
        readonly Transform _root;
        readonly Dictionary<string, GameObject> _nodes = new Dictionary<string, GameObject>();
        ThreatRuntime _bound;

        public ThreatPlaceholderView(Transform root) => _root = root;

        public int Count => _nodes.Count;
        public IReadOnlyDictionary<string, GameObject> Nodes => _nodes;

        public void Bind(ThreatRuntime runtime)
        {
            if (_bound == runtime) return;
            Unbind();
            _bound = runtime;
            if (runtime == null) return;
            runtime.PlaceholderSpawned += OnSpawned;
            runtime.PlaceholderMoved += OnMoved;
            runtime.PlaceholderRemoved += OnRemoved;
            runtime.PlaceholdersCleared += Clear;
            // Threats configured before the view bound (the session boot) get their nodes now.
            foreach (ThreatAIState threat in runtime.Threats)
                if (threat != null && !_nodes.ContainsKey(threat.InstanceId)) OnSpawned(threat, 0);
        }

        public void Unbind()
        {
            if (_bound == null) return;
            _bound.PlaceholderSpawned -= OnSpawned;
            _bound.PlaceholderMoved -= OnMoved;
            _bound.PlaceholderRemoved -= OnRemoved;
            _bound.PlaceholdersCleared -= Clear;
            _bound = null;
            Clear();
        }

        /// <summary>Drops nodes whose threat no longer exists (a direct list edit bypasses the events).</summary>
        public void Reconcile()
        {
            if (_bound == null) return;
            var live = new HashSet<string>();
            foreach (ThreatAIState t in _bound.Threats)
                if (t != null) live.Add(t.InstanceId);
            foreach (string id in new List<string>(_nodes.Keys))
                if (!live.Contains(id)) OnRemoved(id);
        }

        void OnSpawned(ThreatAIState threat, long index)
        {
            if (threat == null) return;
            OnRemoved(threat.InstanceId);
            GameObject node = ThreatPlaceholderFactory.Build(threat.ArchetypeId, threat.Tags, _root, PhysicsLayers.Threat);
            node.name = GodotNodeName.Validate("ThreatPlaceholder_" + threat.InstanceId);
            if (threat.WorldPosition.Count >= 3)
                node.transform.position = Frame.ToUnity(V.F64(threat.WorldPosition[0]), V.F64(threat.WorldPosition[1]), V.F64(threat.WorldPosition[2]));
            _nodes[threat.InstanceId] = node;
        }

        void OnMoved(string instanceId, Vec3 world)
        {
            if (_nodes.TryGetValue(instanceId, out GameObject node) && node != null) node.transform.position = Frame.ToUnity(world);
        }

        void OnRemoved(string instanceId)
        {
            if (instanceId == null || !_nodes.TryGetValue(instanceId, out GameObject node)) return;
            _nodes.Remove(instanceId);
            if (node != null) Object.Destroy(node);
        }

        public void Clear()
        {
            foreach (GameObject node in _nodes.Values)
                if (node != null) Object.Destroy(node);
            _nodes.Clear();
        }
    }

    /// <summary>
    /// The scene half of <c>HallucinationManager</c>: phantom placeholders under the active ship root and the screen FX
    /// intensity (<see cref="HallucinationFx"/>), with <c>motion_reduce</c> from the accessibility settings.
    /// </summary>
    public sealed class HallucinationView
    {
        readonly Transform _root;
        readonly Dictionary<long, GameObject> _phantoms = new Dictionary<long, GameObject>();
        readonly Dictionary<long, Vec3> _phantomLocals = new Dictionary<long, Vec3>();
        HallucinationRuntime _bound;

        public HallucinationView(Transform root) => _root = root;

        public int PhantomCount => _phantoms.Count;

        public void Bind(HallucinationRuntime runtime)
        {
            if (_bound == runtime) return;
            Unbind();
            _bound = runtime;
            if (runtime == null) return;
            runtime.PhantomSpawned += OnPhantomSpawned;
            runtime.PhantomFreed += OnPhantomFreed;
            runtime.FxIntensityChanged += OnFx;
            OnFx(runtime.FxIntensity);
        }

        public void Unbind()
        {
            if (_bound != null)
            {
                _bound.PhantomSpawned -= OnPhantomSpawned;
                _bound.PhantomFreed -= OnPhantomFreed;
                _bound.FxIntensityChanged -= OnFx;
                _bound = null;
            }
            foreach (GameObject node in _phantoms.Values)
                if (node != null) Object.Destroy(node);
            _phantoms.Clear();
            _phantomLocals.Clear();
            HallucinationFx.SetGlobalIntensity(0.0);
        }

        public static void SetMotionReduce(bool motionReduce) => HallucinationFx.SetGlobalMotionReduce(motionReduce);

        /// <summary>Phantoms follow their ship root (docking moves it).</summary>
        public void Sync()
        {
            if (_bound == null) return;
            IShipSceneRoot parent = _bound.Parent;
            foreach (var pair in _phantoms)
            {
                if (pair.Value == null || !_phantomLocals.TryGetValue(pair.Key, out Vec3 local)) continue;
                Vec3 world = parent != null && parent.IsValid && parent.IsInsideTree ? parent.GlobalTransform * local : local;
                pair.Value.transform.position = Frame.ToUnity(world);
            }
        }

        void OnPhantomSpawned(long id, Vec3 local)
        {
            OnPhantomFreed(id);
            GameObject node = ThreatPlaceholderFactory.Build(HallucinationRuntime.PHANTOM_ARCHETYPE, null, _root, PhysicsLayers.Hallucination);
            node.name = "Phantom_" + id;
            _phantomLocals[id] = local;
            _phantoms[id] = node;
            Sync();
        }

        void OnPhantomFreed(long id)
        {
            if (!_phantoms.TryGetValue(id, out GameObject node)) return;
            _phantoms.Remove(id);
            _phantomLocals.Remove(id);
            if (node != null) Object.Destroy(node);
        }

        static void OnFx(double intensity) => HallucinationFx.SetGlobalIntensity(intensity);
    }
}
