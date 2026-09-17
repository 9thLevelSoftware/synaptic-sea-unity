// Scene half of scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _build_slice_affordance_labels (971-1092, 1161-1225),
// _clear_blocked_affordances (8315), the breach unsafe marker (9020, 9163) and the arc zone labels (9511, 9551).
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// Builds the readability props of the home ship and, in the port, of a boarded derelict through
    /// <see cref="ReadabilityPropFactory"/> on <see cref="SessionEvents.AffordancesRebuilt"/> (objective props, blocked
    /// biomatter, ramp cues, entry beacon, destination reactor core, route cues). Objective props mount the bound imported
    /// visual first (<see cref="PropVisualBindingCatalog.GetObjectiveBinding"/>, as Godot did) and fall back to the
    /// procedural prop. It attaches the <see cref="VfxCatalog"/> landmark glows (D4: <c>beacon_blue</c> on the entry beacon
    /// and blue landmarks, <c>reactor_green</c> on the destination core and the green reactor landmark), hides the home
    /// ship's blocked props on <see cref="SessionEvents.BlockedAffordancesCleared"/>, drops a derelict's set on
    /// <see cref="SessionEvents.AffordancesCleared"/>, and keeps the world labels: affordance labels (Godot's
    /// <c>debug_affordance_labels_enabled</c> set, shown by default in the port — see <see cref="ShowAffordanceLabels"/>),
    /// the breach "OXYGEN LOW" marker (<see cref="SessionEvents.BreachUnsafeMarkerVisible"/>) and the arc zone
    /// "ARC LIVE / GROUNDED" labels.
    /// Godot built the affordances for the home loader only (<c>_build_slice_affordance_labels</c> ran once, on the start
    /// ship); each root now has its own set, keyed by its loader view, with its own label prefix.
    /// </summary>
    public sealed class AffordanceView
    {
        public const string BreachLabelId = "hazard:breach_unsafe";
        public const string BreachUnsafeText = "OXYGEN LOW";
        public const string ArcLiveText = "ARC LIVE — WAIT";
        public const string ArcGroundedText = "ARC GROUNDED — CROSS";
        /// <summary>The home ship's affordance label prefix.</summary>
        public const string AffordanceLabelPrefix = "affordance:";
        /// <summary>A derelict's affordance labels are <c>affordance@&lt;root&gt;:</c> (see <see cref="LabelPrefixFor"/>).</summary>
        public const string DerelictLabelPrefix = "affordance@";
        public const string ArcLabelPrefix = "hazard:arc:";

        static readonly Color ObjectiveLabelColor = new Color(0.35f, 1.0f, 0.45f);
        static readonly Color BlockedLabelColor = new Color(1.0f, 0.28f, 0.22f);
        static readonly Color RampLabelColor = new Color(1.0f, 0.78f, 0.25f);
        static readonly Color LandmarkLabelColor = new Color(0.28f, 0.75f, 1.0f);
        static readonly Color BreachLabelColor = new Color(1.0f, 0.32f, 0.22f);
        static readonly Color ArcArcingColor = new Color(0.95f, 0.32f, 1.0f);
        static readonly Color ArcDischargedColor = new Color(0.35f, 0.85f, 1.0f);

        static readonly Dictionary<string, GameObject> EmptyProps = new Dictionary<string, GameObject>();
        static readonly List<GameObject> EmptyVfx = new List<GameObject>();

        /// <summary>One loader's readability props, glows and label prefix.</summary>
        sealed class RootSet
        {
            public readonly Dictionary<string, GameObject> Props = new Dictionary<string, GameObject>();
            public readonly List<GameObject> Vfx = new List<GameObject>();
            public string LabelPrefix = AffordanceLabelPrefix;
        }

        sealed class ReferenceComparer : IEqualityComparer<IShipLoaderView>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(IShipLoaderView a, IShipLoaderView b) => ReferenceEquals(a, b);
            public int GetHashCode(IShipLoaderView o) => RuntimeHelpers.GetHashCode(o);
        }

        readonly Transform _root;
        readonly Dictionary<IShipLoaderView, RootSet> _sets = new Dictionary<IShipLoaderView, RootSet>(ReferenceComparer.Instance);
        IShipLoaderView _home;
        PropVisualBindingCatalog _bindings;
        bool _bindingsLoaded;

        /// <summary>
        /// Show the objective / blocked / ramp / landmark labels. Godot built them only with the debug export on; the port
        /// shows them by default (no ceilings, iso camera) and keeps the flag for parity captures.
        /// </summary>
        public bool ShowAffordanceLabels = true;

        /// <summary>The prop prefabs imported objective visuals come from (null: <see cref="RuntimePropVisualBinder.DefaultCatalog"/>).</summary>
        public PropCatalog PropPrefabs;

        public WorldLabelLayer Labels { get; }

        /// <summary>The home ship's props (Godot's <c>affordance_root</c> children).</summary>
        public IReadOnlyDictionary<string, GameObject> Props => HomeSet != null ? HomeSet.Props : EmptyProps;

        /// <summary>The home ship's landmark glows.</summary>
        public IReadOnlyList<GameObject> Vfx => HomeSet != null ? HomeSet.Vfx : EmptyVfx;

        /// <summary>The home ship's blocked props were cleared by restore_systems.</summary>
        public bool BlockedCleared { get; private set; }

        public AffordanceView(Transform root, WorldLabelLayer labels)
        {
            _root = root;
            Labels = labels ?? new WorldLabelLayer();
        }

        /// <summary>
        /// The prop-visual bindings objective props mount from (Godot <c>PropVisualBindingCatalog.load_from_path()</c>),
        /// loaded on first use unless set. Null when the index does not load: the procedural props are used.
        /// </summary>
        public PropVisualBindingCatalog Bindings
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
            set
            {
                _bindings = value;
                _bindingsLoaded = value != null;
            }
        }

        RootSet HomeSet => _home != null && _sets.TryGetValue(_home, out RootSet set) ? set : null;

        /// <summary>Whether <paramref name="root"/> currently has built affordances.</summary>
        public bool HasSetFor(IShipLoaderView root) => root != null && _sets.ContainsKey(root);

        /// <summary>The props built for <paramref name="root"/> (empty when it has none).</summary>
        public IReadOnlyDictionary<string, GameObject> PropsFor(IShipLoaderView root) =>
            root != null && _sets.TryGetValue(root, out RootSet set) ? set.Props : EmptyProps;

        /// <summary>
        /// The world-label id prefix of <paramref name="root"/>'s affordance labels: <see cref="AffordanceLabelPrefix"/> for the
        /// home ship, <c>affordance@&lt;root&gt;:</c> for a derelict, so clearing one root never touches another's labels.
        /// </summary>
        public string LabelPrefixFor(IShipLoaderView root)
        {
            if (root == null || ReferenceEquals(root, _home)) return AffordanceLabelPrefix;
            if (_sets.TryGetValue(root, out RootSet set)) return set.LabelPrefix;
            return DerelictLabelPrefix + RuntimeHelpers.GetHashCode(root).ToString(CultureInfo.InvariantCulture) + ":";
        }

        /// <summary>Blocked props still visible on the home ship (Godot <c>get_blocked_affordance_visible_count</c>).</summary>
        public int BlockedVisibleCount
        {
            get
            {
                int n = 0;
                foreach (var pair in Props)
                    if (pair.Key.StartsWith("BlockedAffordance_") && pair.Value != null && pair.Value.activeSelf) n++;
                return n;
            }
        }

        // ------------------------------------------------------------------ rebuild

        /// <summary>Rebuilds the home loader's affordances.</summary>
        public void Rebuild(RunSession session) => Rebuild(session, session?.Loader);

        /// <summary>
        /// Rebuilds <paramref name="root"/>'s affordances: the home loader (Godot <c>_build_slice_affordance_labels</c>) or a
        /// boarded derelict, whose objectives are the session's <see cref="RunSession.DerelictInteractables"/> parented to it.
        /// </summary>
        public void Rebuild(RunSession session, IShipLoaderView root)
        {
            if (session == null || root == null) return;
            bool home = ReferenceEquals(root, session.Loader);
            if (home) _home = root;
            Clear(root);
            if (home) BlockedCleared = session.BlockedAffordancesCleared;
            if (!(root is ShipLoaderNode loader) || loader.View == null || !loader.IsValid) return;
            var set = new RootSet { LabelPrefix = LabelPrefixFor(root) };
            _sets[root] = set;
            bool blockedHidden = home && BlockedCleared;
            Xform3 xf = loader.GlobalTransform;
            var objectives = new List<ObjectiveInteractable>();
            foreach (ObjectiveInteractable it in home ? session.Interactables : session.DerelictInteractables)
                if (it != null && it.IsValid && (home || ReferenceEquals(it.Parent, root))) objectives.Add(it);

            // _build_objective_affordance_props: one prop per placement (repair-junction steps share a placement).
            Dictionary<long, string> slicePlacements = SlicePlacementIds(loader.GameplayDoc);
            var renderedPlacements = new HashSet<string>();
            foreach (ObjectiveInteractable it in objectives)
            {
                if (it.PlacementId.Length != 0 && !renderedPlacements.Add(it.PlacementId)) continue;
                string placementId = it.PlacementId.Length != 0 ? it.PlacementId
                    : slicePlacements.TryGetValue(it.Sequence, out string sliceId) ? sliceId : "";
                Register(set, CreateObjectiveProp(it, placementId), it.GlobalPosition);
            }

            // _build_blocked_affordance_props
            int index = 0;
            foreach (RuntimeMarker node in loader.View.GetBlockedRouteNodes())
            {
                if (node == null) continue;
                index++;
                GameObject prop = ReadabilityPropFactory.CreateBlockedBiomatter();
                prop.name = "BlockedAffordance_" + GdString.FormatIntPadded(index, 2) + "_BlockedBiomatter";
                Register(set, prop, xf * node.GodotPosition);
                if (blockedHidden) prop.SetActive(false);
            }

            // _build_vertical_affordance_props
            index = 0;
            foreach (RuntimeMarker node in loader.View.GetVisibleVerticalTransitionNodes())
            {
                if (node == null) continue;
                index++;
                GameObject prop = ReadabilityPropFactory.CreateRampCue();
                prop.name = "VerticalAffordance_" + GdString.FormatIntPadded(index, 2) + "_RampCue";
                Register(set, prop, xf * node.GodotPosition);
            }

            // _build_entry_destination_props (+ D4 glows)
            Vec3 entry = loader.GetStartTransform().Origin;
            if (!IsInf(entry))
            {
                GameObject beacon = ReadabilityPropFactory.CreateEntryBeacon();
                Register(set, beacon, xf * entry);
                AddVfx(set, VfxCatalog.BeaconBlue, beacon.transform, new Vec3(0f, 2.7f, 0f));
            }
            Vec3 destination = loader.GetGoalPosition();
            Vec3 destinationWorld = IsInf(destination) ? Vec3.Inf : xf * destination;
            if (IsInf(destination) && objectives.Count > 0)
                destinationWorld = objectives[objectives.Count - 1].GlobalPosition;
            if (!IsInf(destinationWorld))
            {
                GameObject core = ReadabilityPropFactory.CreateDestinationReactorCore();
                Register(set, core, destinationWorld);
                AddVfx(set, VfxCatalog.ReactorGreen, core.transform, new Vec3(0f, 3.0f, 0f));
            }

            // _build_route_readability_props
            var points = new List<Vec3>();
            if (!IsInf(entry)) points.Add(xf * entry);
            foreach (string roomId in loader.View.GetCriticalPath())
            {
                Vec3 center = loader.GetRoomCenter(roomId);
                if (!IsInf(center)) points.Add(xf * center);
            }
            if (!IsInf(destination)) points.Add(xf * destination);
            int cueIndex = 0;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                if (points[i].DistanceTo(points[i + 1]) < 0.25f) continue;
                cueIndex++;
                GameObject cue = ReadabilityPropFactory.CreateRouteCue(cueIndex, points[i], points[i + 1]);
                cue.transform.SetParent(_root, false); // the factory placed it at the midpoint (world, session root at origin)
                set.Props[cue.name] = cue;
            }

            // Landmark glows (VfxCatalog hooks: blue landmarks, the green reactor core landmark).
            List<RuntimeMarker> landmarks = loader.View.GetLandmarkNodes();
            GdArray landmarkSpecs = loader.View.GetLandmarkSpecs();
            for (int i = 0; i < landmarks.Count; i++)
            {
                if (landmarks[i] == null) continue;
                GdDict spec = i < landmarkSpecs.Count ? landmarkSpecs[i] as GdDict : null;
                string color = spec != null ? V.Str(spec.Get("color", "")) : "";
                string id = spec != null ? V.Str(spec.Get("id", "")) : "";
                string vfxId = color == "blue" ? VfxCatalog.BeaconBlue : (color == "green" || id == "reactor_green_core") ? VfxCatalog.ReactorGreen : "";
                if (vfxId.Length != 0) AddVfx(set, vfxId, _root, xf * landmarks[i].GodotPosition);
            }

            if (ShowAffordanceLabels) BuildAffordanceLabels(set.LabelPrefix, objectives, loader, xf, blockedHidden);
        }

        /// <summary>
        /// The gameplay slice's <c>placement_id</c> per objective sequence. Godot's loader objective specs (and so the
        /// interactables' <c>placement_id</c> meta) never carried it, so its objective binding lookup never matched; the
        /// port reads the id from the slice here instead of changing the parity-captured specs.
        /// </summary>
        static Dictionary<long, string> SlicePlacementIds(GdDict gameplayDoc)
        {
            var output = new Dictionary<long, string>();
            if (gameplayDoc == null || !(gameplayDoc.Get("objectives", null) is GdArray objectives)) return output;
            foreach (object o in objectives)
            {
                if (!(o is GdDict objective)) continue;
                string id = V.Str(objective.Get("placement_id", ""));
                if (id.Length != 0) output[V.I64(objective.Get("sequence", 0L))] = id;
            }
            return output;
        }

        /// <summary>
        /// Godot <c>_build_objective_affordance_props</c>: <c>get_objective_binding(placement_id)</c> →
        /// <c>create_objective_visual</c>, else <see cref="ReadabilityPropFactory.CreateObjectiveProp"/>. The imported visual
        /// is wrapped in a root named like the procedural prop, so prop keys stay <c>ObjectiveAffordance_NN_kind</c>.
        /// </summary>
        GameObject CreateObjectiveProp(ObjectiveInteractable it, string placementId)
        {
            PropVisualBindingCatalog bindings = placementId.Length != 0 ? Bindings : null;
            GameObject imported = bindings != null
                ? RuntimePropVisualBinder.CreateObjectiveVisual(bindings.GetObjectiveBinding(placementId), PropPrefabs)
                : null;
            if (imported == null) return ReadabilityPropFactory.CreateObjectiveProp(it.Sequence, it.ObjectiveType);
            var root = new GameObject(ReadabilityPropFactory.OBJECTIVE_PREFIX + GdString.FormatIntPadded(it.Sequence, 2) + "_"
                                      + ReadabilityPropFactory.ObjectiveKind(it.ObjectiveType)) { layer = PhysicsLayers.Prop };
            imported.transform.SetParent(root.transform, false);
            return root;
        }

        /// <summary>Whether a prop mounts an imported visual (Godot meta <c>visual_source == "imported"</c>).</summary>
        public static bool IsImported(GameObject prop) =>
            prop != null && prop.transform.Find(RuntimePropVisualBinder.IMPORTED_VISUAL_NAME) != null;

        void BuildAffordanceLabels(string prefix, List<ObjectiveInteractable> objectives, ShipLoaderNode loader, Xform3 xf, bool blockedHidden)
        {
            foreach (ObjectiveInteractable it in objectives)
            {
                ObjectiveInteractable captured = it;
                string text = GdString.FormatIntPadded(it.Sequence, 2) + " " + ShortObjectiveLabel(it.ObjectiveType);
                Labels.Set(prefix + "objective_" + GdString.FormatIntPadded(it.Sequence, 2), text,
                    () => captured.IsValid ? Frame.ToUnity(captured.GlobalPosition + new Vec3(0f, 2.4f, 0f)) : (Vector3?)null,
                    ObjectiveLabelColor, hazard: false);
            }
            int index = 0;
            foreach (RuntimeMarker node in loader.View.GetBlockedRouteNodes())
            {
                if (node == null) continue;
                index++;
                Vector3 at = Frame.ToUnity(xf * node.GodotPosition + new Vec3(0f, 2.8f, 0f));
                Labels.Set(prefix + "blocked_" + GdString.FormatIntPadded(index, 2), "Blocked\nBio", () => at, BlockedLabelColor,
                    hazard: false, visible: !blockedHidden);
            }
            index = 0;
            foreach (RuntimeMarker node in loader.View.GetVisibleVerticalTransitionNodes())
            {
                if (node == null) continue;
                index++;
                Vector3 at = Frame.ToUnity(xf * node.GodotPosition + new Vec3(0f, 2.2f, 0f));
                Labels.Set(prefix + "vertical_" + GdString.FormatIntPadded(index, 2), "Ramp\nUp", () => at, RampLabelColor, hazard: false);
            }
            index = 0;
            foreach (RuntimeMarker node in loader.View.GetLandmarkNodes())
            {
                if (node == null) continue;
                index++;
                Vector3 at = Frame.ToUnity(xf * node.GodotPosition + new Vec3(0f, 2.8f, 0f));
                Labels.Set(prefix + "landmark_" + GdString.FormatIntPadded(index, 2), index == 1 ? "Beacon" : "Core", () => at,
                    LandmarkLabelColor, hazard: false);
            }
        }

        /// <summary>Godot <c>_short_objective_label</c>.</summary>
        public static string ShortObjectiveLabel(string raw)
        {
            switch (raw)
            {
                case "recover_supplies": return "Supplies";
                case "restore_systems": return "Systems";
                case "download_logs": return "Logs";
                case "stabilize_reactor": return "Reactor";
                default: return GdString.Capitalize(raw ?? "");
            }
        }

        void Register(RootSet set, GameObject prop, Vec3 godotWorld)
        {
            if (prop == null) return;
            prop.transform.SetParent(_root, false);
            prop.transform.localPosition = Frame.ToUnity(godotWorld);
            set.Props[prop.name] = prop;
        }

        static void AddVfx(RootSet set, string id, Transform parent, Vec3 godotLocal)
        {
            GameObject vfx = VfxCatalog.Spawn(id, parent, godotLocal);
            if (vfx != null) set.Vfx.Add(vfx);
        }

        static bool IsInf(Vec3 v) => float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z);

        // ------------------------------------------------------------------ state changes

        /// <summary><c>_clear_blocked_affordances</c>: restore_systems hides the home ship's blocked biomatter props (and labels).</summary>
        public void ClearBlocked()
        {
            BlockedCleared = true;
            foreach (var pair in Props)
                if (pair.Key.StartsWith("BlockedAffordance_") && pair.Value != null) pair.Value.SetActive(false);
            foreach (WorldLabelLayer.Entry e in Labels.Entries.Values)
                if (e.Id.StartsWith(AffordanceLabelPrefix + "blocked_")) e.Visible = false;
        }

        /// <summary>The breach "OXYGEN LOW" marker above the first breach zone (a hazard label: never distance-culled).</summary>
        public void SetBreachMarkerVisible(RunSession session, bool visible)
        {
            SessionZone zone = null;
            if (session != null)
                foreach (SessionZone z in session.BreachZoneNodes)
                    if (z != null) { zone = z; break; }
            if (zone == null)
            {
                Labels.Remove(BreachLabelId);
                return;
            }
            SessionZone captured = zone;
            Labels.Set(BreachLabelId, BreachUnsafeText, () => ZoneAnchor(captured, 2.6f), BreachLabelColor, hazard: true, visible: visible);
        }

        /// <summary>One label per arc zone, text and colour following its arcing state (Godot updated them in _refresh_arc_state).</summary>
        public void SyncArcLabels(IEnumerable<SessionZone> arcZones)
        {
            var live = new HashSet<string>();
            if (arcZones != null)
            {
                foreach (SessionZone z in arcZones)
                {
                    if (z == null) continue;
                    string id = ArcLabelPrefix + (z.ZoneId.Length != 0 ? z.ZoneId : z.NodeName);
                    live.Add(id);
                    bool arcing = z.VisualState == "arcing";
                    if (!Labels.Has(id))
                    {
                        SessionZone captured = z;
                        Labels.Set(id, arcing ? ArcLiveText : ArcGroundedText, () => ZoneAnchor(captured, 2.6f),
                            arcing ? ArcArcingColor : ArcDischargedColor, hazard: true);
                    }
                    else
                    {
                        Labels.SetText(id, arcing ? ArcLiveText : ArcGroundedText, arcing ? ArcArcingColor : ArcDischargedColor);
                    }
                }
            }
            Labels.RemoveWhere(e => e.Id.StartsWith(ArcLabelPrefix) && !live.Contains(e.Id));
        }

        static Vector3? ZoneAnchor(SessionZone zone, float height)
        {
            IShipSceneRoot parent = zone.Parent;
            if (parent != null && !(parent.IsValid && parent.IsInsideTree)) return null;
            Vec3 world = parent != null ? parent.GlobalTransform * zone.LocalPosition : zone.LocalPosition;
            return Frame.ToUnity(world + new Vec3(0f, height, 0f));
        }

        /// <summary>Drops every root's affordances.</summary>
        public void Clear()
        {
            foreach (IShipLoaderView root in new List<IShipLoaderView>(_sets.Keys)) Clear(root);
            Labels.RemoveWhere(e => e.Id.StartsWith(AffordanceLabelPrefix) || e.Id.StartsWith(DerelictLabelPrefix));
        }

        /// <summary>Drops <paramref name="root"/>'s props, glows and labels; other roots keep theirs.</summary>
        public void Clear(IShipLoaderView root)
        {
            if (root == null) return;
            string prefix = LabelPrefixFor(root);
            if (_sets.TryGetValue(root, out RootSet set))
            {
                foreach (GameObject prop in set.Props.Values) Destroy(prop);
                foreach (GameObject vfx in set.Vfx) Destroy(vfx);
                _sets.Remove(root);
            }
            Labels.RemoveWhere(e => e.Id.StartsWith(prefix));
        }

        static void Destroy(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }
    }
}
