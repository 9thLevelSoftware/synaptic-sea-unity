// Scene half of the interaction Area3D nodes (scripts/tools/*.gd, scripts/interaction/interactable.gd,
// scripts/interaction/sealed_hatch.gd @ 96ecb2b0): sphere collision, marker mesh, visibility and collider state.
using System.Collections.Generic;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// The thin scene object for one Core <see cref="SessionInteractable"/>: a trigger sphere (Sensor layer, radius =
    /// <c>interaction_radius</c>), the marker the Godot node built (a gameplay prop or a primitive box), the sealed
    /// hatch's blocker, and a focus highlight. State is read from the model (<see cref="Sync"/>); the model never reads
    /// the scene except for the overlap flag this view writes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractableView : MonoBehaviour
    {
        public SessionInteractable Model { get; private set; }

        /// <summary>The <c>InteractionRegistry</c> handler that owns this kind (null when no handler claims it).</summary>
        public string HandlerId { get; private set; }

        /// <summary>Index of <see cref="HandlerId"/> in the registry (dispatch priority; int.MaxValue when none).</summary>
        public int HandlerOrder { get; private set; } = int.MaxValue;

        public bool PlayerOverlap { get; private set; }
        public bool Focused { get; private set; }

        SphereCollider _sensor;
        GameObject _marker;
        BoxCollider _blocker;
        Renderer[] _markerRenderers = new Renderer[0];
        Vector3 _markerScale = Vector3.one;
        ProximitySensor _proximity;

        public static InteractableView Create(SessionInteractable model, Transform parent, ProximitySensor proximity)
        {
            var go = new GameObject(GodotNodeName.Validate(string.IsNullOrEmpty(model.NodeName) ? model.Kind : model.NodeName)) { layer = PhysicsLayers.Sensor };
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<InteractableView>();
            view.Bind(model, proximity);
            return view;
        }

        void Bind(SessionInteractable model, ProximitySensor proximity)
        {
            Model = model;
            _proximity = proximity;
            HandlerId = HandlerFor(model);
            HandlerOrder = int.MaxValue;
            for (int i = 0; i < InteractionRegistry.Handlers.Count; i++)
            {
                if (InteractionRegistry.Handlers[i].Id == HandlerId)
                {
                    HandlerOrder = i;
                    break;
                }
            }
            _sensor = gameObject.AddComponent<SphereCollider>();
            _sensor.isTrigger = true;
            _sensor.radius = (float)model.InteractionRadius;
            BuildMarker();
            model.StateChanged += OnModelChanged;
            Sync();
        }

        public void SetProximity(ProximitySensor proximity) => _proximity = proximity;

        public bool IsSensorCollider(Collider c) => c != null && c == _sensor;

        void OnModelChanged(SessionInteractable _) => Sync();

        void OnDestroy()
        {
            if (Model != null) Model.StateChanged -= OnModelChanged;
            if (_proximity != null) _proximity.Forget(this);
            if (Model != null) Model.CandidatePlayerInRange = false;
        }

        /// <summary><c>body_entered</c>/<c>body_exited</c>: the Godot <c>candidate_player</c>.</summary>
        public void SetPlayerOverlap(bool inside)
        {
            PlayerOverlap = inside;
            if (Model != null) Model.CandidatePlayerInRange = inside && _sensor != null && _sensor.enabled;
        }

        /// <summary>Applies position, visibility and collider state from the model.</summary>
        public void Sync()
        {
            if (Model == null) return;
            bool live = Model.IsValid && Model.IsInsideTree;
            transform.position = Frame.ToUnity(Model.GlobalPosition);
            bool markerShown = live && MarkerShown(Model);
            if (_marker != null && _marker.activeSelf != markerShown) _marker.SetActive(markerShown);
            bool sensorOn = live && !CollisionDisabled(Model);
            if (_sensor.enabled != sensorOn)
            {
                _sensor.enabled = sensorOn;
                if (!sensorOn && _proximity != null) _proximity.Forget(this);
            }
            if (_blocker != null) _blocker.enabled = live && !((SealedHatch)Model).BlockerDisabled;
            if (Model is ObjectiveInteractable objective) ApplyObjectiveMaterial(objective);
        }

        /// <summary>Focus highlight: the interact prompt target (first registry handler in range).</summary>
        public void SetFocused(bool focused)
        {
            if (Focused == focused) return;
            Focused = focused;
            if (_marker != null) _marker.transform.localScale = focused ? _markerScale * 1.15f : _markerScale;
        }

        /// <summary>The prompt the HUD shows while this view has focus.</summary>
        public string PromptText
        {
            get
            {
                switch (Model)
                {
                    case ObjectiveInteractable o: return o.PromptText;
                    case RepairPoint rp: return "Repair: " + rp.SubcomponentId;
                    case BreachSealPoint sp: return "Seal breach: " + sp.CompartmentId;
                    case FireSuppressionPoint fp: return "Extinguish: " + fp.CompartmentId;
                    case DockPortBarrier _: return "Breach dock seam";
                    case BridgeTerminal _: return "Log in: bridge terminal";
                    case CraftingStation cs: return "Use: " + cs.StationKind;
                    case ProductionStation ps: return "Use: " + ps.StationKind;
                    case LootContainer _: return "Search";
                    case SealedHatch h: return h.Bypassed ? "Reseal hatch" : "Bypass hatch (" + h.LockKind + ")";
                    case ToolPickup t: return "Pick up: " + t.ToolId;
                    case HangarBayControl _: return "Hangar bay";
                    case CargoHoldControl _: return "Cargo hold";
                    case CartControl _: return "Cart";
                    case WorkYieldDrop _: return "Scoop salvage";
                    default: return "Interact";
                }
            }
        }

        public static string HandlerFor(SessionInteractable model)
        {
            switch (model.Kind)
            {
                case "dock_port_barrier": return "dock_barrier";
                case "bridge_terminal": return "bridge_terminal";
                case "fire_suppression_point": return "fire_suppression_point";
                case "repair_point": return "repair_point";
                case "breach_seal_point": return "breach_seal_point";
                case "crafting_station": return "crafting_station";
                case "production_station": return "production_station";
                case "loot_container": return "loot_container";
                case "sealed_hatch": return "hatch_bypass";
                case "objective": return model.Parent != null ? "derelict_objective" : "home_objective";
                case "tool_pickup": return "tool_pickup";
                case "hangar_bay_control": return "hangar";
                case "cargo_hold_control": return "cargo_deposit";
                case "cart_control": return "cart";
                case "work_yield_drop": return "work_yield_drop";
                default: return null;
            }
        }

        static bool MarkerShown(SessionInteractable m)
        {
            switch (m)
            {
                case ObjectiveInteractable o: return o.MarkerShown;
                case RepairPoint rp: return rp.MarkerShown;
                case BreachSealPoint sp: return sp.MarkerShown;
                case FireSuppressionPoint fp: return fp.MarkerShown;
                case DockPortBarrier b: return b.MarkerShown;
                case CraftingStation cs: return cs.MarkerShown;
                case LootContainer lc: return lc.MarkerShown;
                case ToolPickup t: return t.MarkerShown;
                case WorkYieldDrop d: return d.MarkerShown;
                case SealedHatch h: return !h.Bypassed;
                default: return true;
            }
        }

        static bool CollisionDisabled(SessionInteractable m)
        {
            switch (m)
            {
                case RepairPoint rp: return rp.CollisionDisabled;
                case BreachSealPoint sp: return sp.CollisionDisabled;
                case FireSuppressionPoint fp: return fp.CollisionDisabled;
                case DockPortBarrier b: return b.CollisionDisabled;
                case LootContainer lc: return lc.CollisionDisabled;
                case ToolPickup t: return t.CollisionDisabled;
                case WorkYieldDrop d: return d.CollisionDisabled;
                default: return false;
            }
        }

        // ------------------------------------------------------------------ markers (Godot _ensure_marker)

        void BuildMarker()
        {
            float r = (float)Model.InteractionRadius;
            switch (Model)
            {
                case ObjectiveInteractable _:
                    _marker = RuntimeVisualCatalog.AddMesh(transform, "Marker", RuntimeVisualCatalog.Sphere,
                        RuntimeVisualCatalog.Material(new Color(0.25f, 0.95f, 0.45f, 0.32f), unshaded: true, transparent: true),
                        Vector3.zero, Quaternion.identity, Vector3.one * (r * 2f), PhysicsLayers.Prop, castShadows: false);
                    break;
                case LootContainer lc:
                    _marker = GameplayProp(lc.PropId);
                    break;
                case CraftingStation _:
                    _marker = GameplayProp("workbench");
                    break;
                case FireSuppressionPoint _:
                    _marker = GameplayProp("extinguisher_station");
                    break;
                case BreachSealPoint _:
                    _marker = GameplayProp("breach_patch_panel");
                    break;
                case ToolPickup _:
                    _marker = GameplayProp("tool_case");
                    break;
                case SealedHatch _:
                    _marker = GameplayProp("hatch_wheel");
                    var blockerGo = new GameObject("HatchBlocker") { layer = PhysicsLayers.ZoneBlocker };
                    blockerGo.transform.SetParent(transform, false);
                    _blocker = blockerGo.AddComponent<BoxCollider>();
                    _blocker.size = new Vector3(r, r * 2f, 0.4f);
                    _blocker.center = new Vector3(0f, r, 0f);
                    break;
                case RepairPoint _:
                    _marker = Box(new Vector3(r * 0.5f, r * 0.5f, r * 0.5f), new Color(0.95f, 0.45f, 0.15f, 0.7f));
                    break;
                case DockPortBarrier _:
                    _marker = Box(new Vector3(r * 0.5f, r, r * 0.5f), new Color(0.85f, 0.2f, 0.2f, 0.7f));
                    break;
                case BridgeTerminal _:
                    _marker = Box(new Vector3(r * 0.4f, r, r * 0.4f), new Color(0.2f, 0.7f, 0.95f, 0.7f));
                    break;
                case CargoHoldControl _:
                    _marker = Box(new Vector3(r * 0.5f, r * 0.5f, r * 0.5f), new Color(0.2f, 0.7f, 0.85f, 0.7f));
                    break;
                case CartControl _:
                    _marker = Box(new Vector3(r * 0.5f, r * 0.4f, r * 0.7f), new Color(0.85f, 0.75f, 0.2f, 0.7f));
                    break;
                case ExtinguisherRechargePort _:
                    _marker = Box(new Vector3(r * 0.5f, r * 0.5f, r * 0.5f), new Color(0.2f, 0.85f, 0.6f, 0.7f));
                    break;
                case HangarBayControl _:
                    _marker = Box(new Vector3(r * 0.5f, r, r * 0.5f), new Color(0.95f, 0.6f, 0.15f, 0.7f));
                    break;
                case ProductionStation _:
                    _marker = Box(new Vector3(r * 0.5f, r * 0.5f, r * 0.5f), new Color(0.35f, 0.85f, 0.45f, 0.7f));
                    break;
                case WorkYieldDrop _:
                    _marker = Box(new Vector3(0.35f, 0.25f, 0.35f), new Color(0.9f, 0.75f, 0.25f, 0.9f));
                    break;
            }
            if (_marker != null)
            {
                _markerScale = _marker.transform.localScale;
                _markerRenderers = _marker.GetComponentsInChildren<Renderer>(true);
            }
        }

        GameObject GameplayProp(string propId) => GameplayPropFactory.Build(propId, Vec3.Zero, transform);

        GameObject Box(Vector3 size, Color color) =>
            RuntimeVisualCatalog.AddMesh(transform, "Marker", RuntimeVisualCatalog.Cube,
                RuntimeVisualCatalog.Material(color, unshaded: true, transparent: color.a < 0.99f),
                Vector3.zero, Quaternion.identity, size, PhysicsLayers.Prop, castShadows: false);

        string _objectiveMaterialState = "";

        void ApplyObjectiveMaterial(ObjectiveInteractable o)
        {
            string state = o.MarkerMaterialState;
            if (state == _objectiveMaterialState || _markerRenderers.Length == 0) return;
            _objectiveMaterialState = state;
            Color c = state == "completed" ? new Color(0.4f, 0.4f, 0.4f, 0.16f)
                : state == "active" ? new Color(0.25f, 0.95f, 0.45f, 0.32f)
                : new Color(0.25f, 0.55f, 0.95f, 0.12f);
            Material m = RuntimeVisualCatalog.Material(c, unshaded: true, transparent: true);
            foreach (Renderer rr in _markerRenderers) rr.sharedMaterial = m;
        }

        /// <summary>Views grouped by registry order then distance (the focus rule).</summary>
        public static InteractableView PickFocus(IEnumerable<InteractableView> candidates, Vector3 playerWorld)
        {
            InteractableView best = null;
            float bestD = float.MaxValue;
            foreach (InteractableView v in candidates)
            {
                if (v == null || v.Model == null || !v.Model.IsValid || v.HandlerId == null || !v.PlayerOverlap) continue;
                float d = Vector3.Distance(v.transform.position, playerWorld);
                if (best == null || v.HandlerOrder < best.HandlerOrder || (v.HandlerOrder == best.HandlerOrder && d < bestD))
                {
                    best = v;
                    bestD = d;
                }
            }
            return best;
        }
    }
}
