// Composition root for scenes/procgen/playable_generated_ship.tscn @ 96ecb2b0 (PlayableGeneratedShip._ready plus the
// autoloads it relied on: AudioManager, the input map, the WorldEnvironment-less default environment).
using System;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Input;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace SynapticSea.Game
{
    /// <summary>How the title flow asks for a run (set <see cref="PlayableBootstrap.PendingLaunch"/> before loading the scene).</summary>
    [Serializable]
    public sealed class PlayableLaunchOptions
    {
        public const string GoldenDir = "res://data/procgen/golden/coherent_ship_001/";

        /// <summary>Continue: apply the world save (<c>request_load</c>), or the manual slot when <see cref="SlotId"/> is set.</summary>
        public bool ContinueFromSave;
        public string SlotId = "";

        /// <summary>Run context for generated ships (recorded; the home ship is the golden layout unless the paths say otherwise).</summary>
        public long Seed;
        public string BiomeId = "";
        public string DifficultyId = "";
        public string StartingClassId = "engineer";

        public string LayoutPath = GoldenDir + "layout.json";
        public string KitPath = RunSession.DEFAULT_KIT_PATH;
        public string GameplaySlicePath = GoldenDir + "gameplay_slice.json";
        public string BlueprintPath = GoldenDir + "blueprint.json";

        public static PlayableLaunchOptions NewRun() => new PlayableLaunchOptions();
        public static PlayableLaunchOptions Continue(string slotId = "") => new PlayableLaunchOptions { ContinueFromSave = true, SlotId = slotId ?? "" };
    }

    /// <summary>
    /// Builds a playable run at runtime: core services (when no Boot scene configured them), input, AudioManager,
    /// EventSystem (UI Toolkit navigation through the Input System), global volume, the HUD and menu UIDocuments, then
    /// the <see cref="RunSessionHost"/> (home ship, player, camera, views) and the <see cref="SessionUiBridge"/>. The
    /// Playable scene holds only this component; everything else is created here.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class PlayableBootstrap : MonoBehaviour
    {
        /// <summary>Set by the title flow before <c>SceneManager.LoadScene("Playable")</c>; consumed (and cleared) on boot.</summary>
        public static PlayableLaunchOptions PendingLaunch;

        /// <summary>The running bootstrap (null outside the Playable scene).</summary>
        public static PlayableBootstrap Current { get; private set; }

        /// <summary>Raised after a boot finished (tests, title flow).</summary>
        public static event Action<PlayableBootstrap> Booted;

        /// <summary>Raised on <c>return_to_title_requested</c>; without a subscriber the "Title" scene loads when it exists.</summary>
        public static event Action ReturnToTitle;

        [SerializeField] PanelSettings hudPanelSettings;
        [SerializeField] PanelSettings menuPanelSettings;
        [SerializeField] VolumeProfile globalVolumeProfile;
        [Tooltip("Boot automatically in Start (off for tests that boot by hand).")]
        [SerializeField] bool bootOnStart = true;

        public PlayableLaunchOptions ActiveLaunch { get; private set; }
        public RunSessionHost Host { get; private set; }
        public RunSession Session => Host != null ? Host.Session : null;
        public SessionUiBridge Ui { get; private set; }
        public MenuCoordinator Coordinator => Ui?.Coordinator;
        public SynapticSeaInput Input { get; private set; }
        public AudioManager Audio { get; private set; }
        public UIDocument HudDocument { get; private set; }
        public UIDocument MenuDocument { get; private set; }
        public bool IsBooted { get; private set; }
        public string BootFailure { get; private set; } = "";

        public PanelSettings HudPanelSettings { get => hudPanelSettings; set => hudPanelSettings = value; }
        public PanelSettings MenuPanelSettings { get => menuPanelSettings; set => menuPanelSettings = value; }
        public VolumeProfile GlobalVolumeProfile { get => globalVolumeProfile; set => globalVolumeProfile = value; }

        bool _ownsInput;

        void Start()
        {
            if (bootOnStart && !IsBooted) Boot(null);
        }

        /// <summary>Boots a run with <paramref name="options"/> (or <see cref="PendingLaunch"/>, or a new golden run).</summary>
        public void Boot(PlayableLaunchOptions options)
        {
            if (IsBooted) return;
            Current = this;
            // TODO(title-flow merge): consume RunLaunchRequest.Pending here once the Boot/Title branch lands.
            ActiveLaunch = options ?? PendingLaunch ?? PlayableLaunchOptions.NewRun();
            PendingLaunch = null;

            EnsureCoreServices();
            Input = new SynapticSeaInput();
            _ownsInput = true;
            Audio = FindAnyObjectByType<AudioManager>();
            if (Audio == null)
            {
                var audioGo = new GameObject("AudioManager");
                audioGo.transform.SetParent(transform, false);
                Audio = audioGo.AddComponent<AudioManager>();
            }
            EnsureEventSystem();
            EnsureGlobalVolume();
            AtmosphereApplier.ApplyGodotDefaultEnvironment();

            HudDocument = MakeDocument("HUD", hudPanelSettings, 0);
            var hud = HudDocument.gameObject.AddComponent<HudRoot>();
            HudDocument.gameObject.SetActive(true);
            MenuDocument = MakeDocument("Menus", menuPanelSettings, 10);
            MenuDocument.gameObject.SetActive(true);
            Ui = new SessionUiBridge(hud, MenuDocument, Input);

            var hostGo = new GameObject("PlayableGeneratedShip");
            Host = hostGo.AddComponent<RunSessionHost>();
            var deps = new RunSessionDeps
            {
                LayoutPath = ActiveLaunch.LayoutPath,
                KitPath = ActiveLaunch.KitPath,
                GameplaySlicePath = ActiveLaunch.GameplaySlicePath,
                BlueprintPath = ActiveLaunch.BlueprintPath,
                StartingClassId = string.IsNullOrEmpty(ActiveLaunch.StartingClassId) ? "engineer" : ActiveLaunch.StartingClassId,
                UiState = Ui,
            };
            RunSession session = Host.Boot(deps, Audio, Input, s =>
            {
                Ui.BindSessionEvents(s);
                s.ReturnToTitleRequested += OnReturnToTitle;
            });
            if (!session.PlayableStarted)
            {
                BootFailure = session.LastFailureReason;
                Debug.LogError("PlayableBootstrap: run failed to start: " + BootFailure);
                return;
            }
            Ui.BuildCoordinator(session, Host, Audio);
            Input.Player.Enable();

            if (ActiveLaunch.ContinueFromSave)
            {
                bool loaded;
                if (!string.IsNullOrEmpty(ActiveLaunch.SlotId))
                {
                    RunSnapshot snapshot = session.SaveLoadService.LoadFromSlot(ActiveLaunch.SlotId);
                    loaded = snapshot != null && session.ApplyManualSlot(snapshot);
                }
                else
                {
                    loaded = session.RequestLoad();
                }
                if (!loaded) Debug.LogWarning("PlayableBootstrap: continue requested but no save applied; starting fresh");
            }
            Host.ApplyViews();
            IsBooted = true;
            Booted?.Invoke(this);
        }

        void Update()
        {
            if (IsBooted) Ui.Tick();
        }

        void OnDestroy()
        {
            Ui?.Dispose();
            if (_ownsInput && Input != null)
            {
                Input.Disable();
                Input.Dispose();
            }
            if (Current == this) Current = null;
        }

        void OnReturnToTitle()
        {
            if (ReturnToTitle != null)
            {
                ReturnToTitle.Invoke();
                return;
            }
            if (Application.CanStreamedLevelBeLoaded("Title")) SceneManager.LoadScene("Title");
        }

        // ------------------------------------------------------------------ composition helpers

        /// <summary>Configures CoreServices only when no Boot scene did (standalone Playable, tests).</summary>
        static void EnsureCoreServices()
        {
            if (CoreServices.Resources != null) return;
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CoreServices.UserStorage = new FileSystemStorage(Application.persistentDataPath);
            CoreServices.Log = new UnityLog();
            CoreServices.Engine = new FixedEngineInfo("unity " + Application.unityVersion);
        }

        void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.transform.SetParent(transform, false);
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
        }

        void EnsureGlobalVolume()
        {
            if (globalVolumeProfile == null) return;
            var go = new GameObject("GlobalVolume");
            go.transform.SetParent(transform, false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = globalVolumeProfile;
        }

        UIDocument MakeDocument(string name, PanelSettings settings, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.SetActive(false);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.sortingOrder = sortingOrder;
            return doc;
        }
    }
}
