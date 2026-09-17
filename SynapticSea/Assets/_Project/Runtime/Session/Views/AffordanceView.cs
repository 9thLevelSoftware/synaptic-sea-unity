// Scene half of scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _build_slice_affordance_labels (971-1092, 1161-1225),
// _clear_blocked_affordances (8315), the breach unsafe marker (9020, 9163) and the arc zone labels (9511, 9551).
using System.Collections.Generic;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// Builds the home ship's readability props through <see cref="ReadabilityPropFactory"/> on
    /// <see cref="SessionEvents.AffordancesRebuilt"/> (objective props, blocked biomatter, ramp cues, entry beacon,
    /// destination reactor core, route cues), attaches the <see cref="VfxCatalog"/> landmark glows (D4:
    /// <c>beacon_blue</c> on the entry beacon and blue landmarks, <c>reactor_green</c> on the destination core and the
    /// green reactor landmark), hides the blocked props on <see cref="SessionEvents.BlockedAffordancesCleared"/>, and
    /// keeps the world labels: affordance labels (Godot's <c>debug_affordance_labels_enabled</c> set, shown by default in
    /// the port — see <see cref="ShowAffordanceLabels"/>), the breach "OXYGEN LOW" marker
    /// (<see cref="SessionEvents.BreachUnsafeMarkerVisible"/>) and the arc zone "ARC LIVE / GROUNDED" labels.
    /// </summary>
    public sealed class AffordanceView
    {
        public const string BreachLabelId = "hazard:breach_unsafe";
        public const string BreachUnsafeText = "OXYGEN LOW";
        public const string ArcLiveText = "ARC LIVE — WAIT";
        public const string ArcGroundedText = "ARC GROUNDED — CROSS";
        public const string AffordanceLabelPrefix = "affordance:";
        public const string ArcLabelPrefix = "hazard:arc:";

        static readonly Color ObjectiveLabelColor = new Color(0.35f, 1.0f, 0.45f);
        static readonly Color BlockedLabelColor = new Color(1.0f, 0.28f, 0.22f);
        static readonly Color RampLabelColor = new Color(1.0f, 0.78f, 0.25f);
        static readonly Color LandmarkLabelColor = new Color(0.28f, 0.75f, 1.0f);
        static readonly Color BreachLabelColor = new Color(1.0f, 0.32f, 0.22f);
        static readonly Color ArcArcingColor = new Color(0.95f, 0.32f, 1.0f);
        static readonly Color ArcDischargedColor = new Color(0.35f, 0.85f, 1.0f);

        readonly Transform _root;
        readonly Dictionary<string, GameObject> _props = new Dictionary<string, GameObject>();
        readonly List<GameObject> _vfx = new List<GameObject>();

        /// <summary>
        /// Show the objective / blocked / ramp / landmark labels. Godot built them only with the debug export on; the port
        /// shows them by default (no ceilings, iso camera) and keeps the flag for parity captures.
        /// </summary>
        public bool ShowAffordanceLabels = true;

        public WorldLabelLayer Labels { get; }
        public IReadOnlyDictionary<string, GameObject> Props => _props;
        public IReadOnlyList<GameObject> Vfx => _vfx;
        public bool BlockedCleared { get; private set; }

        public AffordanceView(Transform root, WorldLabelLayer labels)
        {
            _root = root;
            Labels = labels ?? new WorldLabelLayer();
        }

        /// <summary>Blocked props still visible (Godot <c>get_blocked_affordance_visible_count</c>).</summary>
        public int BlockedVisibleCount
        {
            get
            {
                int n = 0;
                foreach (var pair in _props)
                    if (pair.Key.StartsWith("BlockedAffordance_") && pair.Value != null && pair.Value.activeSelf) n++;
                return n;
            }
        }

        // ------------------------------------------------------------------ rebuild

        public void Rebuild(RunSession session)
        {
            Clear();
            BlockedCleared = session != null && session.BlockedAffordancesCleared;
            if (session == null || !(session.Loader is ShipLoaderNode loader) || loader.View == null) return;
            Xform3 xf = loader.GlobalTransform;

            // _build_objective_affordance_props: one prop per placement (repair-junction steps share a placement).
            var renderedPlacements = new HashSet<string>();
            foreach (ObjectiveInteractable it in session.Interactables)
            {
                if (it == null || !it.IsValid) continue;
                if (it.PlacementId.Length != 0 && !renderedPlacements.Add(it.PlacementId)) continue;
                GameObject prop = ReadabilityPropFactory.CreateObjectiveProp(it.Sequence, it.ObjectiveType);
                Register(prop, it.GlobalPosition);
            }

            // _build_blocked_affordance_props
            int index = 0;
            foreach (RuntimeMarker node in loader.View.GetBlockedRouteNodes())
            {
                if (node == null) continue;
                index++;
                GameObject prop = ReadabilityPropFactory.CreateBlockedBiomatter();
                prop.name = "BlockedAffordance_" + GdString.FormatIntPadded(index, 2) + "_BlockedBiomatter";
                Register(prop, xf * node.GodotPosition);
                if (BlockedCleared) prop.SetActive(false);
            }

            // _build_vertical_affordance_props
            index = 0;
            foreach (RuntimeMarker node in loader.View.GetVisibleVerticalTransitionNodes())
            {
                if (node == null) continue;
                index++;
                GameObject prop = ReadabilityPropFactory.CreateRampCue();
                prop.name = "VerticalAffordance_" + GdString.FormatIntPadded(index, 2) + "_RampCue";
                Register(prop, xf * node.GodotPosition);
            }

            // _build_entry_destination_props (+ D4 glows)
            Vec3 entry = loader.GetStartTransform().Origin;
            if (!IsInf(entry))
            {
                GameObject beacon = ReadabilityPropFactory.CreateEntryBeacon();
                Register(beacon, xf * entry);
                AddVfx(VfxCatalog.BeaconBlue, beacon.transform, new Vec3(0f, 2.7f, 0f));
            }
            Vec3 destination = loader.GetGoalPosition();
            Vec3 destinationWorld = IsInf(destination) ? Vec3.Inf : xf * destination;
            if (IsInf(destination) && session.Interactables.Count > 0 && session.Interactables[session.Interactables.Count - 1] != null)
                destinationWorld = session.Interactables[session.Interactables.Count - 1].GlobalPosition;
            if (!IsInf(destinationWorld))
            {
                GameObject core = ReadabilityPropFactory.CreateDestinationReactorCore();
                Register(core, destinationWorld);
                AddVfx(VfxCatalog.ReactorGreen, core.transform, new Vec3(0f, 3.0f, 0f));
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
                _props[cue.name] = cue;
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
                if (vfxId.Length != 0) AddVfx(vfxId, _root, xf * landmarks[i].GodotPosition);
            }

            if (ShowAffordanceLabels) BuildAffordanceLabels(session, loader, xf);
        }

        void BuildAffordanceLabels(RunSession session, ShipLoaderNode loader, Xform3 xf)
        {
            foreach (ObjectiveInteractable it in session.Interactables)
            {
                if (it == null || !it.IsValid) continue;
                ObjectiveInteractable captured = it;
                string text = GdString.FormatIntPadded(it.Sequence, 2) + " " + ShortObjectiveLabel(it.ObjectiveType);
                Labels.Set(AffordanceLabelPrefix + "objective_" + GdString.FormatIntPadded(it.Sequence, 2), text,
                    () => captured.IsValid ? Frame.ToUnity(captured.GlobalPosition + new Vec3(0f, 2.4f, 0f)) : (Vector3?)null,
                    ObjectiveLabelColor, hazard: false);
            }
            int index = 0;
            foreach (RuntimeMarker node in loader.View.GetBlockedRouteNodes())
            {
                if (node == null) continue;
                index++;
                Vector3 at = Frame.ToUnity(xf * node.GodotPosition + new Vec3(0f, 2.8f, 0f));
                Labels.Set(AffordanceLabelPrefix + "blocked_" + GdString.FormatIntPadded(index, 2), "Blocked\nBio", () => at, BlockedLabelColor,
                    hazard: false, visible: !BlockedCleared);
            }
            index = 0;
            foreach (RuntimeMarker node in loader.View.GetVisibleVerticalTransitionNodes())
            {
                if (node == null) continue;
                index++;
                Vector3 at = Frame.ToUnity(xf * node.GodotPosition + new Vec3(0f, 2.2f, 0f));
                Labels.Set(AffordanceLabelPrefix + "vertical_" + GdString.FormatIntPadded(index, 2), "Ramp\nUp", () => at, RampLabelColor, hazard: false);
            }
            index = 0;
            foreach (RuntimeMarker node in loader.View.GetLandmarkNodes())
            {
                if (node == null) continue;
                index++;
                Vector3 at = Frame.ToUnity(xf * node.GodotPosition + new Vec3(0f, 2.8f, 0f));
                Labels.Set(AffordanceLabelPrefix + "landmark_" + GdString.FormatIntPadded(index, 2), index == 1 ? "Beacon" : "Core", () => at,
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

        void Register(GameObject prop, Vec3 godotWorld)
        {
            if (prop == null) return;
            prop.transform.SetParent(_root, false);
            prop.transform.localPosition = Frame.ToUnity(godotWorld);
            _props[prop.name] = prop;
        }

        void AddVfx(string id, Transform parent, Vec3 godotLocal)
        {
            GameObject vfx = VfxCatalog.Spawn(id, parent, godotLocal);
            if (vfx != null) _vfx.Add(vfx);
        }

        static bool IsInf(Vec3 v) => float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z);

        // ------------------------------------------------------------------ state changes

        /// <summary><c>_clear_blocked_affordances</c>: restore_systems hides the blocked biomatter props (and their labels).</summary>
        public void ClearBlocked()
        {
            BlockedCleared = true;
            foreach (var pair in _props)
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

        public void Clear()
        {
            foreach (GameObject prop in _props.Values) Destroy(prop);
            _props.Clear();
            foreach (GameObject vfx in _vfx) Destroy(vfx);
            _vfx.Clear();
            Labels.RemoveWhere(e => e.Id.StartsWith(AffordanceLabelPrefix));
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
