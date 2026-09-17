// Scene-affecting outcomes of scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 that Core raises instead of touching nodes.
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>A coordinator-built zone node (route gate, breach zone, arc zone, fire zone) as a plain record.</summary>
    public sealed class SessionZone
    {
        /// <summary>"route_gate" | "breach" | "arc" | "fire".</summary>
        public string Kind = "";
        public string ZoneId = "";
        public string NodeName = "";

        /// <summary>The ship root the node was parented under (null = coordinator origin root).</summary>
        public Systems.IShipSceneRoot Parent;

        /// <summary>Local to <see cref="Parent"/> (world when null).</summary>
        public Vec3 LocalPosition;

        /// <summary>Fire zones: the compartment id; arc zones: the resolved room id.</summary>
        public string CompartmentOrRoomId = "";

        /// <summary>Extra per-kind metadata (fire: layout zone id/kind + marker index).</summary>
        public GdDict Meta = new GdDict();

        /// <summary>Collision enabled (route gate closed, breach blocked, arc arcing). Fire zones never collide.</summary>
        public bool CollisionEnabled;

        /// <summary>Visual state tag the view maps to a color ("open"/"closed", "open"/"blocked"/"sealed", "arcing"/"discharged").</summary>
        public string VisualState = "";

        /// <summary>Whether the visual mesh is shown.</summary>
        public bool VisualVisible = true;
    }

    /// <summary>
    /// Every scene/HUD/menu side effect the coordinator performed directly on nodes, raised at the same point in the
    /// order. The Runtime's view appliers subscribe. Nothing in Core reads these back.
    /// </summary>
    public sealed class SessionEvents
    {
        // ---- interaction nodes
        /// <summary>A tool/interactable Area3D would have been instanced and parented (<c>add_child</c>).</summary>
        public event Action<SessionInteractable> InteractableSpawned;

        /// <summary>A tool/interactable node was freed (<c>queue_free</c>).</summary>
        public event Action<SessionInteractable> InteractableDespawned;

        // ---- zones
        /// <summary>A route gate / breach zone / arc zone / fire zone node was built.</summary>
        public event Action<SessionZone> ZoneSpawned;

        /// <summary>A zone node was freed.</summary>
        public event Action<SessionZone> ZoneDespawned;

        /// <summary>A zone's collider/visual state changed (<c>_apply_*_scene_state</c>).</summary>
        public event Action<SessionZone> ZoneStateChanged;

        /// <summary><c>unsafe_room_marker.visible</c> / arc labels: the breach "OXYGEN LOW" label visibility.</summary>
        public event Action<bool> BreachUnsafeMarkerVisible;

        // ---- affordances
        /// <summary><c>_clear_blocked_affordances()</c> (restore_systems cleared the blocked-biomatter props).</summary>
        public event Action BlockedAffordancesCleared;

        /// <summary><c>_build_slice_affordance_labels()</c> + <c>_build_route_control_gates()</c> ran for the home loader.</summary>
        public event Action AffordancesRebuilt;

        // ---- component markers
        /// <summary><c>_rebuild_component_markers()</c>: the mounted-component marker list (world positions + ids).</summary>
        public event Action<IReadOnlyList<GdDict>> ComponentMarkersRebuilt;

        // ---- HUD: objective tracker
        public event Action<GdArray> TrackerObjectivesSet;
        public event Action<long> TrackerCompleted;
        public event Action<long> TrackerCurrentSequence;
        public event Action<long, GdDict> TrackerStepProgress;
        public event Action TrackerRunComplete;
        public event Action<string> TrackerInteractionPrompt;
        public event Action<IReadOnlyList<string>> TrackerSystemStatusLines;

        // ---- HUD: vitals / hotbar / work / tooltip
        public event Action<IReadOnlyList<string>> VitalsPanelLines;
        public event Action<string> HotbarText;
        public event Action<GdDict> WorkActionHudState;
        public event Action<GdDict> TooltipQuery;

        // ---- menu coordinator
        /// <summary><c>menu_coordinator.trigger_tutorial(trigger, target)</c>.</summary>
        public event Action<string, string> TutorialTriggered;
        /// <summary>TutorialState.triggered (id, title, body): the overlay panel shows it.</summary>
        public event Action<string, string, string> TutorialShown;
        public event Action<bool> LoadAvailable;
        public event Action<GdArray> InventoryItems;
        public event Action<GdArray, long> HotbarSlots;

        /// <summary>
        /// A panel open/close request the coordinator performed on a HUD panel: <c>(panel_id, args)</c>, e.g.
        /// ("recipe_picker", {station_kind}), ("transfer", {ship_id|cart_id, label}), ("inventory_self", {}),
        /// ("wounds", {}), ("ship_mod", {}), ("chart", {}), ("scanner", {}).
        /// </summary>
        public event Action<string, GdDict> PanelRequested;

        /// <summary><c>_hallucination_fx_overlay</c> intensity meta.</summary>
        public event Action<double> HallucinationFxIntensity;

        // ---- Unity-port additions
        /// <summary>
        /// The session's <see cref="RunSession.TutorialState"/> was reset (HUD rebuild at boot / reload) or restored from a
        /// save. Same instance every time; the Codex / banner re-read it.
        /// </summary>
        public event Action<Systems.TutorialState> TutorialStateReset;

        /// <summary>The session's <see cref="RunSession.WoundState"/> changed (new wound, treatment, healing to zero, reset or restore).</summary>
        public event Action<Systems.WoundState> WoundsChanged;

        /// <summary>
        /// A wound treatment request finished: <c>(result)</c> with <c>ok</c>, <c>action</c> ("bandage"/"treat"),
        /// <c>wound_id</c>, <c>item_id</c> and, when refused, <c>reason</c> (see <see cref="RunSession.BandageWound"/>).
        /// </summary>
        public event Action<GdDict> WoundTreatmentResult;

        internal void RaiseInteractableSpawned(SessionInteractable i) => InteractableSpawned?.Invoke(i);
        internal void RaiseInteractableDespawned(SessionInteractable i) => InteractableDespawned?.Invoke(i);
        internal void RaiseZoneSpawned(SessionZone z) => ZoneSpawned?.Invoke(z);
        internal void RaiseZoneDespawned(SessionZone z) => ZoneDespawned?.Invoke(z);
        internal void RaiseZoneStateChanged(SessionZone z) => ZoneStateChanged?.Invoke(z);
        internal void RaiseBreachUnsafeMarkerVisible(bool v) => BreachUnsafeMarkerVisible?.Invoke(v);
        internal void RaiseBlockedAffordancesCleared() => BlockedAffordancesCleared?.Invoke();
        internal void RaiseAffordancesRebuilt() => AffordancesRebuilt?.Invoke();
        internal void RaiseComponentMarkersRebuilt(IReadOnlyList<GdDict> m) => ComponentMarkersRebuilt?.Invoke(m);
        internal void RaiseTrackerObjectivesSet(GdArray specs) => TrackerObjectivesSet?.Invoke(specs);
        internal void RaiseTrackerCompleted(long seq) => TrackerCompleted?.Invoke(seq);
        internal void RaiseTrackerCurrentSequence(long seq) => TrackerCurrentSequence?.Invoke(seq);
        internal void RaiseTrackerStepProgress(long seq, GdDict p) => TrackerStepProgress?.Invoke(seq, p);
        internal void RaiseTrackerRunComplete() => TrackerRunComplete?.Invoke();
        internal void RaiseTrackerInteractionPrompt(string t) => TrackerInteractionPrompt?.Invoke(t);
        internal void RaiseTrackerSystemStatusLines(IReadOnlyList<string> l) => TrackerSystemStatusLines?.Invoke(l);
        internal void RaiseVitalsPanelLines(IReadOnlyList<string> l) => VitalsPanelLines?.Invoke(l);
        internal void RaiseHotbarText(string t) => HotbarText?.Invoke(t);
        internal void RaiseWorkActionHudState(GdDict s) => WorkActionHudState?.Invoke(s);
        internal void RaiseTooltipQuery(GdDict q) => TooltipQuery?.Invoke(q);
        internal void RaiseTutorialTriggered(string trigger, string target) => TutorialTriggered?.Invoke(trigger, target);
        internal void RaiseTutorialShown(string id, string title, string body) => TutorialShown?.Invoke(id, title, body);
        internal void RaiseLoadAvailable(bool v) => LoadAvailable?.Invoke(v);
        internal void RaiseInventoryItems(GdArray ids) => InventoryItems?.Invoke(ids);
        internal void RaiseHotbarSlots(GdArray labels, long selected) => HotbarSlots?.Invoke(labels, selected);
        internal void RaisePanelRequested(string panelId, GdDict args) => PanelRequested?.Invoke(panelId, args ?? new GdDict());
        internal void RaiseHallucinationFxIntensity(double v) => HallucinationFxIntensity?.Invoke(v);
        internal void RaiseTutorialStateReset(Systems.TutorialState t) => TutorialStateReset?.Invoke(t);
        internal void RaiseWoundsChanged(Systems.WoundState w) => WoundsChanged?.Invoke(w);
        internal void RaiseWoundTreatmentResult(GdDict r) => WoundTreatmentResult?.Invoke(r);
    }
}
