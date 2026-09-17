// Scene half of scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: the node that owned the run (_ready, _process,
// the interaction/zone/threat/hallucination children, _attach_ceiling_fade_controller and the travel scene surgery).
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
    /// focus, and — when the active ship root changes (travel, reload) — the environment, ceiling fade and sensors.</item>
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

        /// <summary>Raised after the session booted (views bound, first sync done).</summary>
        public event Action<RunSession> SessionBooted;

        /// <summary>Raised after the active ship root changed and the scene was re-pegged (travel, reload).</summary>
        public event Action ActiveShipChanged;

        public bool Paused { get; private set; }
        public InteractableView FocusedView { get; private set; }
        public IReadOnlyDictionary<SessionInteractable, InteractableView> InteractableViews => _interactables;
        public IReadOnlyDictionary<SessionZone, ZoneView> ZoneViews => _zones;
        public ThreatPlaceholderView Threats { get; private set; }
        public HallucinationView Hallucinations { get; private set; }
        public AffordanceView Affordances { get; private set; }
        public ComponentMarkerView ComponentMarkers { get; private set; }
        public WorldLabelLayer WorldLabels { get; private set; }

        Transform _interactionRoot;
        Transform _zoneRoot;
        Transform _threatRoot;
        readonly Dictionary<SessionInteractable, InteractableView> _interactables = new Dictionary<SessionInteractable, InteractableView>();
        readonly Dictionary<SessionZone, ZoneView> _zones = new Dictionary<SessionZone, ZoneView>();
        readonly HashSet<SessionInteractable> _liveInteractables = new HashSet<SessionInteractable>();
        readonly HashSet<SessionZone> _liveZones = new HashSet<SessionZone>();
        IShipSceneRoot _appliedActiveRoot;
        bool _environmentDirty = true;
        string _focusPrompt = "";
        PlayerController _boundPlayer;
        bool _affordancesDirty;
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
            Threats.PlayerPosition = () => SceneState?.Player != null ? SceneState.Player.transform.position : (Vector3?)null;
            Threats.PlayerHit += (damage, id, archetype, at) => PlayerDamaged?.Invoke(damage, archetype, at);
            Hallucinations = new HallucinationView(_threatRoot);
            WorldLabels = new WorldLabelLayer();
            Affordances = new AffordanceView(MakeChild("AffordanceRoot"), WorldLabels);
            ComponentMarkers = new ComponentMarkerView(MakeChild("ComponentMarkerRoot"));

            ShipHost = new UnityShipSceneHost(transform);
            ShipHost.RootAttached += _ => _environmentDirty = true;
            ShipHost.RootFreed += _ => _environmentDirty = true;
            SceneState = new UnityRunSceneState(transform, input);
            SceneState.PlayerSpawned += OnPlayerSpawned;
            SceneState.PlayerDespawned += OnPlayerDespawned;

            deps = deps ?? new RunSessionDeps();
            deps.Scene = SceneState;
            deps.ShipHost = ShipHost;
            deps.LosProbe = new PhysicsLineOfSightProbe();
            if (audio != null) deps.AudioSink = new AudioManagerSink(audio, () => SceneState.Player != null ? SceneState.Player.transform : null);

            Session = RunSession.Create(deps, s =>
            {
                s.Events.InteractableSpawned += OnInteractableSpawned;
                s.Events.InteractableDespawned += OnInteractableDespawned;
                s.Events.ZoneSpawned += OnZoneSpawned;
                s.Events.ZoneDespawned += OnZoneDespawned;
                s.Events.ZoneStateChanged += OnZoneStateChanged;
                s.Events.AffordancesRebuilt += OnAffordancesRebuilt;
                s.Events.BlockedAffordancesCleared += OnBlockedAffordancesCleared;
                s.Events.BreachUnsafeMarkerVisible += OnBreachUnsafeMarkerVisible;
                s.Events.ComponentMarkersRebuilt += OnComponentMarkersRebuilt;
                beforeReady?.Invoke(s);
            });
            // The music stems / ambient beds follow the session's audio models (explicit, instead of a host lookup).
            if (audio != null && Session.AudioManager != null)
                audio.BindSessionModels(Session.AudioManager.MusicState, Session.AudioManager.AmbientZoneState);
            Reconcile();
            ApplyActiveShipIfChanged(force: true);
            SessionBooted?.Invoke(Session);
            return Session;
        }

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
                PlayerRoomId = "",
                Moving = p.IsMoving(),
                Crouching = p.IsCrouching(),
                InteractHeld = interactHeld,
            };
        }

        /// <summary>One view pass (also called by tests after driving the session directly).</summary>
        public void ApplyViews()
        {
            if (Session == null) return;
            Reconcile();
            ApplyActiveShipIfChanged(force: false);
            foreach (InteractableView v in _interactables.Values) v.Sync();
            foreach (ZoneView z in _zones.Values) z.Sync();
            Threats.Bind(Session.ThreatManager);
            Threats.Reconcile();
            Hallucinations.Bind(Session.HallucinationManager);
            Hallucinations.Sync();
            HallucinationView.SetMotionReduce(MotionReduce != null && MotionReduce());
            if (_affordancesDirty)
            {
                _affordancesDirty = false;
                Affordances.Rebuild(Session);
                _breachMarkerDirty = true;
            }
            if (_breachMarkerDirty)
            {
                _breachMarkerDirty = false;
                Affordances.SetBreachMarkerVisible(Session, _breachMarkerVisible);
            }
            Affordances.SyncArcLabels(Session.ArcZoneNodes);
            UpdateFocus();
        }

        void LateUpdate()
        {
            if (Session == null || WorldLabels == null) return;
            PlayerController p = SceneState?.Player;
            Camera cam = SceneState?.CameraRig != null ? SceneState.CameraRig.Camera : null;
            WorldLabels.Update(cam, p != null ? p.transform.position : (Vector3?)null);
        }

        // ------------------------------------------------------------------ scene event views (D1/D2)

        void OnAffordancesRebuilt() => _affordancesDirty = true;

        void OnBlockedAffordancesCleared() => Affordances?.ClearBlocked();

        void OnBreachUnsafeMarkerVisible(bool visible)
        {
            _breachMarkerVisible = visible;
            _breachMarkerDirty = true;
        }

        void OnComponentMarkersRebuilt(IReadOnlyList<GdDict> records) => ComponentMarkers?.Rebuild(records);

        // ------------------------------------------------------------------ gameplay input (A1, B3, A5)

        /// <summary>Gameplay keys may act: booted, not paused, the run not over, and no surface consuming input.</summary>
        public bool GameplayInputAllowed =>
            Session != null && !Paused && !(SimulationPaused != null && SimulationPaused()) && !Session.SliceComplete
            && !(GameplayInputBlocked != null && GameplayInputBlocked());

        /// <summary><c>attack_primary</c>: <see cref="RunSession.AttackWithEquippedWeapon"/> (null when refused by the gate).</summary>
        public GdDict RequestAttack()
        {
            if (!GameplayInputAllowed) return null;
            GdDict result = Session.AttackWithEquippedWeapon();
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
            AddAll(s.Interactables);
            AddAll(s.DerelictInteractables);
            AddAll(s.LootContainers);
            AddAll(s.WorkYieldDrops);
            AddAll(s.SealedHatches);
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
                next = InteractableView.PickFocus(SceneState.Sensor.Overlapping, p.transform.position);
            if (next != FocusedView)
            {
                if (FocusedView != null) FocusedView.SetFocused(false);
                FocusedView = next;
                if (FocusedView != null) FocusedView.SetFocused(true);
            }
            string prompt = FocusedView != null ? FocusedView.PromptText : "";
            if (prompt != _focusPrompt)
            {
                _focusPrompt = prompt;
                FocusPromptChanged?.Invoke(prompt);
            }
        }

        // ------------------------------------------------------------------ player

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
        /// new active root: environment (biome atmosphere or Godot's default), ceiling fade over every attached root's
        /// ceilings, sensor overlaps, and the audio listener.
        /// </summary>
        void ApplyActiveShipIfChanged(bool force)
        {
            IShipSceneRoot active = Session.CurrentShip != null ? Session.CurrentShip.SceneRoot : Session.Loader;
            if (!force && !_environmentDirty && ReferenceEquals(active, _appliedActiveRoot)) return;
            _environmentDirty = false;
            _appliedActiveRoot = active;
            ApplyEnvironment(active as ShipLoaderNode, Session.AwayFromStart);
            Physics.SyncTransforms();
            SceneState.Sensor?.Refresh();
            ActiveShipChanged?.Invoke();
        }

        static void ApplyEnvironment(ShipLoaderNode loader, bool away)
        {
            string biomeId = loader != null ? V.Str(loader.LayoutDoc.Get("biome_id", "")) : "";
            string biomePath = "res://data/procgen/biomes/" + biomeId + ".json";
            if (biomeId.Length == 0 || !CatalogRegistry.Exists(biomePath))
            {
                AtmosphereApplier.ApplyGodotDefaultEnvironment();
                return;
            }
            GdDict atmosphere = (CatalogRegistry.LoadDict(biomePath) ?? new GdDict()).GetDictOrEmpty("atmosphere");
            loader.View.AtmosphereSummary = AtmosphereApplier.Apply(loader.View.transform, atmosphere, away);
        }

        // ------------------------------------------------------------------ teardown

        void OnDestroy()
        {
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
