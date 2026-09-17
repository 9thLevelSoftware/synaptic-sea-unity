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
    /// Interact presses go to <see cref="RunSession.RequestInteract"/>, which resolves the claim through
    /// <see cref="InteractionRegistry"/>; the scene only feeds overlap flags.
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
            Hallucinations = new HallucinationView(_threatRoot);

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
                beforeReady?.Invoke(s);
            });
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
            UpdateFocus();
        }

        /// <summary>Player interact (Godot <c>interact_requested</c>): overlap flags are current, the registry decides.</summary>
        public string RequestInteract()
        {
            if (Session == null) return "";
            if (Paused) return "";
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
            if (_boundPlayer != null) _boundPlayer.InteractRequested -= OnPlayerInteract;
            _boundPlayer = player;
            player.InteractRequested += OnPlayerInteract;
            player.FieldCraftRequested += OnPlayerFieldCraft;
            foreach (InteractableView v in _interactables.Values) v.SetProximity(SceneState.Sensor);
            _environmentDirty = true;
        }

        void OnPlayerDespawned()
        {
            _boundPlayer = null;
            FocusedView = null;
            _environmentDirty = true;
        }

        void OnPlayerInteract(PlayerController _) => RequestInteract();

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
            if (_boundPlayer != null)
            {
                _boundPlayer.InteractRequested -= OnPlayerInteract;
                _boundPlayer.FieldCraftRequested -= OnPlayerFieldCraft;
            }
            Threats?.Unbind();
            Hallucinations?.Unbind();
            HallucinationFx.SetGlobalIntensity(0.0);
            Session?.Dispose();
        }
    }
}
