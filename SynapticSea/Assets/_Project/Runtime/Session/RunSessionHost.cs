// Scene half of scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: the node that owned the run (_ready, _process,
// the interaction/zone/threat/hallucination children and the travel scene surgery; no ceiling fade, see port-status decision 17).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Input;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// Runs a <see cref="RunSession"/> inside a Unity scene. It composes the scene ports (ship host, player/camera
    /// state, audio sink, line-of-sight probe), boots the session, and every frame:
    /// <list type="number">
    /// <item>builds the <see cref="TickContext"/> from the live player (Godot <c>_process</c>, variable delta; the player
    /// itself moves in <c>FixedUpdate</c> like Godot's <c>_physics_process</c>),</item>
    /// <item>calls <see cref="RunSession.Tick"/> unless <see cref="SimulationPaused"/> (the modal stack) says otherwise,</item>
    /// <item>applies the views: interaction nodes, zones, threat and phantom placeholders, hallucination FX, interact
    /// focus, and — when the active ship root changes (travel, reload) — the environment and sensors.</item>
    /// </list>
    /// Interact presses go through <see cref="RunSession.BeginWorkHold"/> (an in-progress work action resumes or, in tap
    /// mode, cancels) and otherwise to <see cref="RunSession.RequestInteract"/>, which resolves the claim through
    /// <see cref="InteractionRegistry"/>; releases call <see cref="RunSession.EndWorkHold"/>. Attack, reload and the three
    /// hotbar keys call the session's combat entry points (Godot <c>_input</c> gameplay tail). All gameplay input is
    /// refused while the simulation is paused, the run has ended, or <see cref="GameplayInputBlocked"/> (the modal stack)
    /// says a surface consumes input; the Player map is also disabled then.
    /// The host also owns the presentation of the session's scene events: readability props, landmark VFX and world
    /// labels (<see cref="Affordances"/>, <see cref="WorldLabels"/>), component markers, and threat combat feedback.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RunSessionHost : MonoBehaviour
    {
        public RunSession Session { get; private set; }
        [SerializeField] CritterCrafter.CritterLibrary creatureLibrary;
        public UnityShipSceneHost ShipHost { get; private set; }
        public UnityRunSceneState SceneState { get; private set; }
        public AudioManager Audio { get; private set; }
        public SynapticSeaInput Input { get; private set; }

        /// <summary>True while the simulation must not advance (pause stack / run results). Null = never paused.</summary>
        public Func<bool> SimulationPaused;

        /// <summary>Accessibility motion-reduce (hallucination FX); polled each frame. Null = off.</summary>
        public Func<bool> MotionReduce;

        /// <summary>True while a surface consumes gameplay input (the modal stack); null = never blocked.</summary>
        public Func<bool> GameplayInputBlocked;

        /// <summary>A threat hit the player: (damage, archetype id, Godot-frame threat position).</summary>
        public event Action<double, string, Vec3> PlayerDamaged;

        /// <summary>Raised when the interact focus changes (prompt text, or "" when nothing is in reach).</summary>
        public event Action<string> FocusPromptChanged;
        public event Action<string, string> ComponentPickerRequested;
        GdDict _focusedComponentTarget = new GdDict();
        bool _componentMarkersDirty = true;
        public GdDict FocusedComponentTarget => _focusedComponentTarget.DeepCopy();

        /// <summary>Raised after the session booted (views bound, first sync done).</summary>
        public event Action<RunSession> SessionBooted;

        /// <summary>Raised after the active ship root changed and the scene was re-pegged (travel, reload).</summary>
        public event Action ActiveShipChanged;

        public bool Paused { get; private set; }
        public InteractableView FocusedView { get; private set; }
        public IReadOnlyDictionary<SessionInteractable, InteractableView> InteractableViews => _interactables;
        public IReadOnlyDictionary<SessionZone, ZoneView> ZoneViews => _zones;
        public ThreatPlaceholderView Threats { get; private set; }

        /// <summary>The NavMesh agents that move the threats (decision 59); null before the boot.</summary>
        public NavMeshThreatNavigation ThreatNavigation { get; private set; }
        public HallucinationView Hallucinations { get; private set; }
        public AffordanceView Affordances { get; private set; }
        public ComponentMarkerView ComponentMarkers { get; private set; }
        public WorldLabelLayer WorldLabels { get; private set; }

        Transform _interactionRoot;
        Transform _zoneRoot;
        Transform _threatRoot;
        readonly Dictionary<SessionInteractable, InteractableView> _interactables = new Dictionary<SessionInteractable, InteractableView>();
        readonly Dictionary<SessionZone, ZoneView> _zones = new Dictionary<SessionZone, ZoneView>();
        readonly HomeAssemblyGeometry _homeAssembly = new HomeAssemblyGeometry();
        readonly HashSet<SessionInteractable> _liveInteractables = new HashSet<SessionInteractable>();
        readonly HashSet<SessionZone> _liveZones = new HashSet<SessionZone>();
        IShipSceneRoot _appliedActiveRoot;
        bool _environmentDirty = true;
        readonly DockedShipGeometry _dockedGeometry = new DockedShipGeometry();
        string _focusPrompt = "";
        PlayerController _boundPlayer;
        readonly List<IShipLoaderView> _affordanceDirtyRoots = new List<IShipLoaderView>();
        bool _breachMarkerDirty;
        bool _breachMarkerVisible;

        /// <summary>
        /// Composes the ports and boots the session (Godot <c>_ready</c>). <paramref name="configure"/> may adjust the
        /// dependencies (paths, class, settings, storage) before the boot. Safe to call once.
        /// </summary>
        public RunSession Boot(RunSessionDeps deps, AudioManager audio, SynapticSeaInput input, Action<RunSession> beforeReady = null)
        {
            if (Session != null) throw new InvalidOperationException("RunSessionHost: already booted");
            transform.position = Vector3.zero;
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            Audio = audio;
            Input = input;
            _interactionRoot = MakeChild("InteractionRoot");
            _zoneRoot = MakeChild("ZoneRoot");
            _threatRoot = MakeChild("ThreatRoot");
            Threats = new ThreatPlaceholderView(_threatRoot);
            Threats.CreatureFactory = new ThreatCreatureFactory(creatureLibrary != null ? creatureLibrary :
                Resources.Load<CritterCrafter.CritterLibrary>("CritterProductionLibrary"));
            Threats.PlayerPosition = () => SceneState?.Player != null ? SceneState.Player.transform.position : (Vector3?)null;
            Threats.PlayerHit += (damage, id, archetype, at) => PlayerDamaged?.Invoke(damage, archetype, at);
            Hallucinations = new HallucinationView(_threatRoot);
            WorldLabels = new WorldLabelLayer();
            Affordances = new AffordanceView(MakeChild("AffordanceRoot"), WorldLabels);
            ComponentMarkers = new ComponentMarkerView(MakeChild("ComponentMarkerRoot"));

            ShipHost = new UnityShipSceneHost(transform);
            ShipHost.RootAttached += _ => { _environmentDirty = true; SceneState?.CameraRig?.Occlusion.RefreshModules(); };
            ShipHost.RootFreed += r =>
            {
                _environmentDirty = true;
                // A freed derelict loses its affordances even when no AffordancesCleared preceded the free (reload paths).
                if (r is IShipLoaderView loader) OnAffordancesCleared(loader);
            };
            SceneState = new UnityRunSceneState(transform, input);
            SceneState.PlayerSpawned += OnPlayerSpawned;
            SceneState.PlayerDespawned += OnPlayerDespawned;

            deps = deps ?? new RunSessionDeps();
            deps.Scene = SceneState;
            deps.ShipHost = ShipHost;
            deps.LosProbe = new PhysicsLineOfSightProbe();
            deps.ResolveDeckLanding = (desired, root) =>
            {
                Vector3 feet = Frame.ToUnity(desired);
                Transform ship = (root as ShipLoaderNode)?.GameObject.transform;
                bool Accept(Collider floor) => ship != null && floor.transform.IsChildOf(ship)
                    && Mathf.Abs(floor.bounds.max.y - feet.y) < 1f;
                return SpawnClearance.TryFindClear(feet, Accept, out Vector3 clear) ? Frame.ToGodot(clear) : (Vec3?)null;
            };
            ThreatNavigation = new NavMeshThreatNavigation(Threats);
            deps.ThreatNavigation = ThreatNavigation;
            if (audio != null) deps.AudioSink = new AudioManagerSink(audio, () => SceneState.Player != null ? SceneState.Player.transform : null);

            Session = RunSession.Create(deps, s =>
            {
                s.Events.InteractableSpawned += OnInteractableSpawned;
                s.Events.InteractableDespawned += OnInteractableDespawned;
                s.Events.ZoneSpawned += OnZoneSpawned;
                s.Events.ZoneDespawned += OnZoneDespawned;
                s.Events.ZoneStateChanged += OnZoneStateChanged;
                s.Events.AffordancesRebuilt += OnAffordancesRebuilt;
                s.Events.AffordancesCleared += OnAffordancesCleared;
                s.Events.BlockedAffordancesCleared += OnBlockedAffordancesCleared;
                s.Events.BreachUnsafeMarkerVisible += OnBreachUnsafeMarkerVisible;
                s.Events.ComponentMarkersRebuilt += OnComponentMarkersRebuilt;
                if (s.ComponentIntegrationEnabled) s.ComponentDomainChanged += _ => _componentMarkersDirty = true;
                beforeReady?.Invoke(s);
            });
            // The music stems / ambient beds follow the session's audio models (explicit, instead of a host lookup).
            if (audio != null && Session.AudioManager != null)
                audio.BindSessionModels(Session.AudioManager.MusicState, Session.AudioManager.AmbientZoneState);
            _homeAssembly.Reconcile(Session.HomeShip);
            Reconcile();
            ApplyActiveShipIfChanged(force: true);
            ResolveSpawnClearance();
            SessionBooted?.Invoke(Session);
            return Session;
        }

        /// <summary>
        /// Where the boot spawned the player inside wall geometry (golden 001: a docked life boat wall runs through the
        /// start marker), move them to the nearest clear spot standing on the home ship's floor (decision 55).
        /// </summary>
        void ResolveSpawnClearance()
        {
            PlayerController player = SceneState.Player;
            if (player == null)
                return;
            Vector3 feet = player.transform.position;
            Transform home = ShipHost.HomeLoader != null ? ShipHost.HomeLoader.GameObject.transform : null;
            bool found = SpawnClearance.TryFindClear(feet, floor => home == null || floor.transform.IsChildOf(home), out Vector3 clear);
            if (!found)
            {
                Debug.LogWarning($"[RunSessionHost] no clear spawn within {SpawnClearance.MaxSearchRadius} m of {feet}; the player keeps the start pose");
                return;
            }
            if ((clear - feet).sqrMagnitude < 1e-6f)
                return;
            SpawnResolvedFrom = feet;
            SceneState.TeleportPlayer(Frame.ToGodot(clear));
            Debug.Log($"[RunSessionHost] spawn moved {Vector3.Distance(clear, feet):0.00} m clear of wall geometry");
        }

        /// <summary>The start pose the boot moved away from, when the spawn overlapped geometry (null otherwise).</summary>
        public Vector3? SpawnResolvedFrom { get; private set; }

        Transform MakeChild(string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(transform, false);
            return t;
        }

        // ------------------------------------------------------------------ frame

        void Update()
        {
            if (Session == null) return;
            Paused = SimulationPaused != null && SimulationPaused();
            // Agents walk in the engine's update, which a paused simulation does not stop.
            ThreatNavigation?.SetPaused(Paused);
            if (!Paused) Session.Tick(BuildTickContext(Time.deltaTime));
            ApplyViews();
        }

        /// <summary>The per-frame scene inputs <c>_process</c> read from live nodes (Godot frame).</summary>
        public TickContext BuildTickContext(double delta)
        {
            PlayerController p = SceneState != null ? SceneState.Player : null;
            if (p == null) return new TickContext { Delta = delta, HasPlayer = false, PlayerPosition = Vec3.Zero, PlayerRoomId = "" };
            bool interactHeld = Input != null && Input.Player.enabled && Input.Player.interact.IsPressed();
            return new TickContext
            {
                Delta = delta,
                HasPlayer = true,
                PlayerPosition = Frame.ToGodot(p.transform.position),
                PlayerRoomId = Session.ResolvePlayerRoom(Frame.ToGodot(p.transform.position)),
                Moving = p.IsMoving(),
                Crouching = p.IsCrouching(),
                InteractHeld = interactHeld,
            };
        }

        /// <summary>One view pass (also called by tests after driving the session directly).</summary>
        public void ApplyViews()
        {
            if (Session == null) return;
            var mobile = Session.LifeboatShip;
            _dockedGeometry.Reconcile(mobile?.ParentShip?.SceneRoot as SceneShipRoot, mobile?.SceneRoot as SceneShipRoot);
            _homeAssembly.Reconcile(Session.HomeShip);
            Reconcile();
            ApplyActiveShipIfChanged(force: false);
            _dockedGeometry.NormalizeInteractions(_liveInteractables, SceneState?.Player != null ? SceneState.Player.transform.position : (Vector3?)null);
            foreach (InteractableView v in _interactables.Values) v.Sync();
            foreach (ZoneView z in _zones.Values) z.Sync();
            Threats.Bind(Session.ThreatManager);
            Threats.Reconcile();
            Hallucinations.Bind(Session.HallucinationManager);
            Hallucinations.Sync();
            HallucinationView.SetMotionReduce(MotionReduce != null && MotionReduce());
            if (_affordanceDirtyRoots.Count > 0)
            {
                var roots = new List<IShipLoaderView>(_affordanceDirtyRoots);
                _affordanceDirtyRoots.Clear();
                foreach (IShipLoaderView root in roots)
                    if (root != null && root.IsValid) Affordances.Rebuild(Session, root);
                _breachMarkerDirty = true;
            }
            if (_breachMarkerDirty)
            {
                _breachMarkerDirty = false;
                Affordances.SetBreachMarkerVisible(Session, _breachMarkerVisible);
            }
            Affordances.SyncArcLabels(Session.ArcZoneNodes);
            RefreshDiagnosticComponentMarkers();
            UpdateFocus();
        }

        void LateUpdate()
        {
            if (Session == null || WorldLabels == null) return;
            if (Input != null && !PointerOverScrollView()) RequestCameraZoom(Input.UI.ScrollWheel.ReadValue<Vector2>().y);
            PlayerController p = SceneState?.Player;
            Camera cam = SceneState?.CameraRig != null ? SceneState.CameraRig.Camera : null;
            WorldLabels.Update(cam, p != null ? p.transform.position : (Vector3?)null);
            if (p != null && SceneState?.CameraRig != null)
                SceneState.CameraRig.Occlusion.UpdateThreatVisibility(Threats.Nodes.Values, p.transform.position);
        }

        // ------------------------------------------------------------------ scene event views (D1/D2)

        void OnAffordancesRebuilt(IShipLoaderView root)
        {
            if (root != null && !_affordanceDirtyRoots.Contains(root)) _affordanceDirtyRoots.Add(root);
        }

        void OnAffordancesCleared(IShipLoaderView root)
        {
            _affordanceDirtyRoots.Remove(root);
            Affordances?.Clear(root);
        }

        void OnBlockedAffordancesCleared() => Affordances?.ClearBlocked();

        void OnBreachUnsafeMarkerVisible(bool visible)
        {
            _breachMarkerVisible = visible;
            _breachMarkerDirty = true;
        }

        void OnComponentMarkersRebuilt(IReadOnlyList<GdDict> records)
        {
            _componentMarkersDirty = true;
            if (Session?.ComponentIntegrationEnabled != true) ComponentMarkers?.Rebuild(records);
        }

        void RefreshDiagnosticComponentMarkers()
        {
            if (Session?.ComponentIntegrationEnabled != true || !_componentMarkersDirty) return;
            _componentMarkersDirty = false;
            var records = new List<GdDict>();
            foreach (object item in Session.ListInstallTargets(""))
            {
                if (!(item is GdDict target)) continue;
                GdDict row = target.DeepCopy();
                bool occupied = target.GetBool("occupied");
                row["empty_anchor"] = !occupied;
                row["component_instance_id"] = occupied ? target.GetString("instance_id")
                    : "anchor:" + target.GetString("ship_id") + ":" + target.GetString("slot_id");
                if (occupied)
                    foreach (object value in Session.ListComponentInstances(target.GetString("holder_id")))
                        if (value is GdDict instance && instance.GetString("instance_id") == target.GetString("instance_id"))
                        { row["component_id"] = instance.GetString("definition_id"); row["condition_state"] = instance.GetString("condition_state"); row["condition"] = instance.Get("condition"); break; }
                records.Add(row);
            }
            ComponentMarkers?.Rebuild(records);
        }

        // ------------------------------------------------------------------ gameplay input (A1, B3, A5)

        /// <summary>Gameplay keys may act: booted, not paused, the run not over, and no surface consuming input.</summary>
        public bool GameplayInputAllowed =>
            Session != null && !Paused && !(SimulationPaused != null && SimulationPaused()) && !Session.SliceComplete
            && !(GameplayInputBlocked != null && GameplayInputBlocked());

        /// <summary>Wheel zoom shares the gameplay gate: inventory/menu scrolling cannot change the camera.</summary>
        public bool RequestCameraZoom(float delta)
        {
            if (!GameplayInputAllowed || SceneState?.CameraRig == null || delta == 0f) return false;
            SceneState.CameraRig.ZoomByWheel(delta);
            return true;
        }

        bool PointerOverScrollView()
        {
            var panel = WorldLabels?.Container?.panel;
            if (panel == null || Input == null) return false;
            Vector2 point = Input.UI.Point.ReadValue<Vector2>();
            var picked = panel.Pick(UnityEngine.UIElements.RuntimePanelUtils.ScreenToPanel(panel, new Vector2(point.x, Screen.height - point.y)));
            for (var element = picked; element != null; element = element.parent)
                if (element is UnityEngine.UIElements.ScrollView || element is UnityEngine.UIElements.Scroller) return true;
            return false;
        }

        /// <summary><c>attack_primary</c>: <see cref="RunSession.AttackWithEquippedWeapon"/> (null when refused by the gate).</summary>
        public GdDict RequestAttack()
        {
            if (!GameplayInputAllowed) return null;
            GdDict result = Session.AttackWithEquippedWeapon(SceneState.Player.AttackDirection);
            ApplyViews();
            return result;
        }

        /// <summary><c>reload_weapon</c>: <see cref="RunSession.BeginWeaponReload"/>. False when refused by the gate.</summary>
        public bool RequestReload()
        {
            if (!GameplayInputAllowed) return false;
            Session.BeginWeaponReload();
            return true;
        }

        /// <summary><c>hotbar_1..3</c>: <see cref="RunSession.UseConsumableHotbarSlot"/> (null when refused by the gate).</summary>
        public GdDict RequestHotbar(int slotIndex)
        {
            if (!GameplayInputAllowed) return null;
            return Session.UseConsumableHotbarSlot(slotIndex);
        }

        /// <summary>
        /// Interact pressed: an in-progress work action consumes the press (<see cref="RunSession.BeginWorkHold"/>: hold
        /// resumes, tap cancels); otherwise the registry interact runs. Returns the claiming handler id ("" = none / work).
        /// </summary>
        public string PressInteract()
        {
            if (!GameplayInputAllowed) return "";
            if (Session.BeginWorkHold())
            {
                ApplyViews();
                return "";
            }
            return RequestInteract();
        }

        /// <summary>Interact released: work progress pauses in hold mode (<see cref="RunSession.EndWorkHold"/>).</summary>
        public void ReleaseInteract() => Session?.EndWorkHold();

        /// <summary>A movement key press: the <c>player_moved</c> tutorial (fires once per run).</summary>
        public void NotifyMoveInput()
        {
            if (GameplayInputAllowed) Session.OnPlayerMoved();
        }

        /// <summary>Player interact (Godot <c>interact_requested</c>): overlap flags are current, the registry decides.</summary>
        public string RequestInteract()
        {
            if (Session == null) return "";
            if (Paused || (GameplayInputBlocked != null && GameplayInputBlocked())) return "";
            SceneState.Sensor?.Refresh();
            foreach (InteractableView v in _interactables.Values) v.Sync();
            UpdateFocus();
            if (!_focusedComponentTarget.IsEmpty && ComponentPickerRequested != null)
            {
                ComponentPickerRequested.Invoke(_focusedComponentTarget.GetString("ship_id"), _focusedComponentTarget.GetString("slot_id"));
                return "component_picker";
            }
            string handler = Session.RequestInteract();
            ApplyViews();
            return handler;
        }

        // ------------------------------------------------------------------ interaction nodes

        void OnInteractableSpawned(SessionInteractable model)
        {
            if (model == null || _interactables.ContainsKey(model) || !model.IsValid || (model.Parent != null && !model.Parent.IsValid)) return;
            _interactables[model] = InteractableView.Create(model, _interactionRoot, SceneState?.Sensor);
            SceneState?.Sensor?.Refresh();
        }

        void OnInteractableDespawned(SessionInteractable model)
        {
            if (model == null || !_interactables.TryGetValue(model, out InteractableView view)) return;
            _interactables.Remove(model);
            if (view != null)
            {
                if (FocusedView == view) FocusedView = null;
                view.gameObject.SetActive(false);
                Destroy(view.gameObject);
            }
        }

        void OnZoneSpawned(SessionZone zone)
        {
            if (zone == null || _zones.ContainsKey(zone)) return;
            _zones[zone] = ZoneView.Create(zone, _zoneRoot);
            Physics.SyncTransforms();
        }

        void OnZoneDespawned(SessionZone zone)
        {
            if (zone == null || !_zones.TryGetValue(zone, out ZoneView view)) return;
            _zones.Remove(zone);
            if (view != null)
            {
                view.gameObject.SetActive(false);
                Destroy(view.gameObject);
            }
        }

        void OnZoneStateChanged(SessionZone zone)
        {
            if (zone != null && _zones.TryGetValue(zone, out ZoneView view) && view != null) view.Sync();
        }

        /// <summary>
        /// Brings the view set in line with the session's node lists (events cover the normal path; this catches nodes
        /// created or freed without one, e.g. a scooped yield pile or a list reset).
        /// </summary>
        void Reconcile()
        {
            _liveInteractables.Clear();
            RunSession s = Session;
            s.RefreshDeckTransitions();
            AddAll(s.DeckTransitions);
            AddAll(s.Interactables);
            AddAll(s.DerelictInteractables);
            AddAll(s.LootContainers);
            AddAll(s.WorkYieldDrops);
            AddAll(s.SealedHatches);
            AddAll(s.HomeJoinControls);
            AddAll(s.RepairPoints);
            AddAll(s.BreachSealPoints);
            AddAll(s.FireSuppressionPoints);
            AddAll(s.CraftingStations);
            AddAll(s.ProductionStations);
            AddAll(s.DockBarriers);
            AddAll(s.BridgeTerminals);
            AddAll(s.HangarControls);
            AddAll(s.CargoHoldControls);
            AddAll(s.CartControls);
            AddOne(s.ExtinguisherRechargePort);
            AddOne(s.ToolPickup);
            AddOne(s.JunctionCalibratorPickup);
            foreach (SessionInteractable model in _liveInteractables)
                if (!_interactables.ContainsKey(model)) OnInteractableSpawned(model);
            foreach (SessionInteractable model in new List<SessionInteractable>(_interactables.Keys))
                if (!_liveInteractables.Contains(model)) OnInteractableDespawned(model);

            _liveZones.Clear();
            foreach (SessionZone z in s.RouteGateNodes) _liveZones.Add(z);
            foreach (SessionZone z in s.BreachZoneNodes) _liveZones.Add(z);
            foreach (SessionZone z in s.ArcZoneNodes) _liveZones.Add(z);
            foreach (SessionZone z in s.FireZoneNodes.Values) _liveZones.Add(z);
            foreach (SessionZone z in _liveZones)
                if (z != null && !_zones.ContainsKey(z)) OnZoneSpawned(z);
            foreach (SessionZone z in new List<SessionZone>(_zones.Keys))
                if (!_liveZones.Contains(z)) OnZoneDespawned(z);
        }

        void AddAll<T>(List<T> list) where T : SessionInteractable
        {
            foreach (T item in list) AddOne(item);
        }

        /// <summary>A node parented to a freed ship root went with it (Godot freed the children with the root).</summary>
        void AddOne(SessionInteractable item)
        {
            if (item != null && item.IsValid && (item.Parent == null || item.Parent.IsValid)) _liveInteractables.Add(item);
        }

        void UpdateFocus()
        {
            PlayerController p = SceneState.Player;
            InteractableView next = null;
            if (p != null && SceneState.Sensor != null && !Paused)
                next = InteractableView.PickFocus(SceneState.Sensor.Overlapping, p.transform.position, Session.CanFocusInteractable);
            IAuthoredPortal portal = p != null && !Paused ? Session.FocusedAuthoredPortal(Frame.ToGodot(p.transform.position)) : null;
            int portalOrder = InteractionRegistry.OrderFor(Session.AwayFromStart ? SessionLocation.Away : SessionLocation.Home).IndexOf("authored_portal");
            if (portal != null && (next == null || next.HandlerOrder > portalOrder)) next = null;
            else portal = null;
            if (next != FocusedView)
            {
                if (FocusedView != null) FocusedView.SetFocused(false);
                FocusedView = next;
                if (FocusedView != null) FocusedView.SetFocused(true);
            }
            _focusedComponentTarget = next == null && portal == null && p != null && !Paused
                ? FindComponentFocus(Frame.ToGodot(p.transform.position)) : new GdDict();
            string prompt = portal != null ? (portal.IsOpen ? "Close door" : portal.PortalKind == "LOCKED" ? "Unlock door" : "Open door")
                : FocusedView != null ? FocusedView.PromptText : !_focusedComponentTarget.IsEmpty
                    ? (_focusedComponentTarget.GetBool("occupied") ? "Remove component" : "Install component") : "";
            // A screen-space label identifies the same authoritative focus as dispatch, even when a foreground wall
            // covers the small marker. Capture this frame's selected anchor rather than another nearest target.
            Vector3? focusAnchor = portal != null ? Frame.ToUnity(portal.GlobalPosition) :
                FocusedView != null ? FocusedView.transform.position : _focusedComponentTarget.Get("world_position") is Vec3 componentAnchor
                    ? Frame.ToUnity(componentAnchor) : (Vector3?)null;
            if (SceneState?.CameraRig != null) SceneState.CameraRig.FocusAnchor = focusAnchor;
            WorldLabels.Set("focused_interaction", string.IsNullOrEmpty(prompt) ? "" : "E · " + prompt,
                () => focusAnchor.HasValue ? focusAnchor.Value + Vector3.up * 1.2f : (Vector3?)null,
                new Color(1f, 0.95f, 0.65f), false, !string.IsNullOrEmpty(prompt));
            if (prompt != _focusPrompt)
            {
                _focusPrompt = prompt;
                FocusPromptChanged?.Invoke(prompt);
            }
        }

        // ------------------------------------------------------------------ player

        GdDict FindComponentFocus(Vec3 playerPosition)
        {
            if (Session?.ComponentIntegrationEnabled != true || Session.WorkActionDriver?.IsWorking() == true) return new GdDict();
            GdDict work = Session.GetComponentWorkState();
            if (work.GetBool("resume_required") || (work.GetString("job_id").Length != 0 && work.GetString("status") != "idle" && work.GetString("status") != "committed")) return new GdDict();
            GdDict best = new GdDict();
            double distance = 3.5;
            foreach (object item in Session.ListInstallTargets(""))
            {
                if (!(item is GdDict target) || !(target.Get("world_position") is Vec3 anchor)) continue;
                GdDict requirements = target.GetDictOrEmpty("requirements");
                double d = anchor.DistanceTo(playerPosition);
                if (d > distance || !requirements.GetBool("in_range", d <= 3.5) || !requirements.GetBool("has_los", true) || !requirements.GetBool("access", true)) continue;
                distance = d; best = target.DeepCopy();
            }
            return best;
        }

        void OnPlayerSpawned(PlayerController player)
        {
            UnbindPlayer();
            _boundPlayer = player;
            player.InteractRequested += OnPlayerInteract;
            player.InteractPressed += OnPlayerInteractPressed;
            player.InteractReleased += OnPlayerInteractReleased;
            player.FieldCraftRequested += OnPlayerFieldCraft;
            player.AttackRequested += OnPlayerAttack;
            player.ReloadRequested += OnPlayerReload;
            player.HotbarRequested += OnPlayerHotbar;
            player.MoveInputPressed += OnPlayerMoveInput;
            foreach (InteractableView v in _interactables.Values) v.SetProximity(SceneState.Sensor);
            _environmentDirty = true;
        }

        void UnbindPlayer()
        {
            if (_boundPlayer == null) return;
            _boundPlayer.InteractRequested -= OnPlayerInteract;
            _boundPlayer.InteractPressed -= OnPlayerInteractPressed;
            _boundPlayer.InteractReleased -= OnPlayerInteractReleased;
            _boundPlayer.FieldCraftRequested -= OnPlayerFieldCraft;
            _boundPlayer.AttackRequested -= OnPlayerAttack;
            _boundPlayer.ReloadRequested -= OnPlayerReload;
            _boundPlayer.HotbarRequested -= OnPlayerHotbar;
            _boundPlayer.MoveInputPressed -= OnPlayerMoveInput;
            _boundPlayer = null;
        }

        void OnPlayerDespawned()
        {
            UnbindPlayer();
            Session?.EndWorkHold();
            FocusedView = null;
            _environmentDirty = true;
        }

        void OnPlayerInteract(PlayerController _) => RequestInteract();
        void OnPlayerInteractPressed(PlayerController _) => PressInteract();
        void OnPlayerInteractReleased(PlayerController _) => ReleaseInteract();
        void OnPlayerAttack(PlayerController _) => RequestAttack();
        void OnPlayerReload(PlayerController _) => RequestReload();
        void OnPlayerHotbar(PlayerController _, int slot) => RequestHotbar(slot);
        void OnPlayerMoveInput(PlayerController _) => NotifyMoveInput();

        void OnPlayerFieldCraft(PlayerController _)
        {
            if (Session != null && !Paused) Session.RequestFieldCraft();
        }

        // ------------------------------------------------------------------ active ship (travel / reload scene surgery)

        /// <summary>
        /// After travel (<c>_attach_derelict_active</c> / <c>travel_home</c>) or a reload, the session has already
        /// re-parented roots and re-pegged the player and docked ships through the ports. The scene then follows the
        /// new active root: environment (biome atmosphere or Godot's default), sensor overlaps, and the audio listener.
        /// </summary>
        void ApplyActiveShipIfChanged(bool force)
        {
            IShipSceneRoot active = Session.CurrentShip != null ? Session.CurrentShip.SceneRoot : Session.Loader;
            if (!force && !_environmentDirty && ReferenceEquals(active, _appliedActiveRoot)) return;
            _environmentDirty = false;
            _appliedActiveRoot = active;
            ApplyEnvironment(active as ShipLoaderNode, Session.AwayFromStart);
            ApplyThreatNavMesh(active);
            Physics.SyncTransforms();
            SceneState.Sensor?.Refresh();
            ActiveShipChanged?.Invoke();
        }

        /// <summary>
        /// Builds the NavMesh of the ship the threats are now on (decision 59). A ship is generated at load, so its
        /// surface is built here rather than baked into an asset; a build failure leaves
        /// <see cref="Core.Session.IThreatNavigation.HasNavMesh"/> false and the threats keep to the nav graph.
        /// </summary>
        void ApplyThreatNavMesh(IShipSceneRoot active)
        {
            if (ThreatNavigation == null) return;
            GameObject root = active is SceneShipRoot scene && scene.IsValid ? scene.GameObject : null;
            ShipNavMesh nav = ShipNavMesh.ForActiveShip(root);
            if (root != null && nav == null)
                Debug.LogWarning($"[RunSessionHost] no NavMesh built for {root.name}; threats fall back to the nav graph");
            ThreatNavigation.SetShip(nav, rebind: true);
        }

        static void ApplyEnvironment(ShipLoaderNode loader, bool away)
        {
            string biomeId = loader != null ? V.Str(loader.LayoutDoc.Get("biome_id", "")) : "";
            string biomePath = "res://data/procgen/biomes/" + biomeId + ".json";
            if (biomeId.Length == 0 || !CatalogRegistry.Exists(biomePath))
            {
                AtmosphereApplier.ApplyPlayableDefaultEnvironment();
                return;
            }
            GdDict atmosphere = (CatalogRegistry.LoadDict(biomePath) ?? new GdDict()).GetDictOrEmpty("atmosphere");
            loader.View.AtmosphereSummary = AtmosphereApplier.Apply(loader.View.transform, atmosphere, away);
        }

        // ------------------------------------------------------------------ teardown

        void OnDestroy()
        {
            _homeAssembly.Restore();
            UnbindPlayer();
            Threats?.Unbind();
            Affordances?.Clear();
            ComponentMarkers?.Clear();
            WorldLabels?.Clear();
            Hallucinations?.Unbind();
            HallucinationFx.SetGlobalIntensity(0.0);
            Session?.Dispose();
        }
    }
}
