// Composition root for scenes/procgen/playable_generated_ship.tscn @ 96ecb2b0 (PlayableGeneratedShip._ready plus the
// title handoff title_main.gd made: request_load() / apply_ui_settings_summary()).
using System;
using SynapticSea.App;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Input;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace SynapticSea.Game
{
    /// <summary>
    /// Builds a playable run at runtime. The process services (CoreServices, preferences, shared input, EventSystem with
    /// <c>InputSystemUIInputModule</c>, AudioManager) come from <see cref="AppServices.Ensure"/>, so the Playable scene
    /// runs the same from Title, standalone in the editor, and in tests. The bootstrap then adds the global volume, the
    /// HUD and menu UIDocuments, the <see cref="RunSessionHost"/> (home ship, player, camera, views) and the
    /// <see cref="SessionUiBridge"/>, and honours <see cref="RunLaunchRequest.Consume"/>:
    /// <list type="bullet">
    /// <item>no request (scene opened directly, tests): a new run on the golden <c>coherent_ship_001</c>;</item>
    /// <item>NewRun: Godot's default start (<c>RunSession.DEFAULT_LAYOUT_PATH</c>, smoke seed 17) for the default
    /// seed; other seeds are not generated yet and fall back to it with a warning;</item>
    /// <item>Continue: <c>request_load()</c> on the world save; LoadSlot: the manual slot;</item>
    /// <item>SettingsSummary (when the title settings changed) is applied after any load.</item>
    /// </list>
    /// Quit to title stores <see cref="RunReturnInfo"/> and loads <see cref="RunLaunchRequest.TitleSceneName"/>.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class PlayableBootstrap : MonoBehaviour
    {
        public const string GoldenDir = "res://data/procgen/golden/coherent_ship_001/";

        /// <summary>The running bootstrap (null outside the Playable scene).</summary>
        public static PlayableBootstrap Current { get; private set; }

        /// <summary>Raised after a boot finished (tests).</summary>
        public static event Action<PlayableBootstrap> Booted;

        /// <summary>Scene-load seam for the return to title (tests capture it). Returns false when it cannot load.</summary>
        public static Func<string, bool> SceneLoader = DefaultSceneLoader;

        [SerializeField] PanelSettings hudPanelSettings;
        [SerializeField] PanelSettings menuPanelSettings;
        [SerializeField] VolumeProfile globalVolumeProfile;
        [Tooltip("Boot automatically in Start (off for tests that boot by hand).")]
        [SerializeField] bool bootOnStart = true;

        /// <summary>The consumed launch request (null when the scene started without one).</summary>
        public RunLaunchRequest Launch { get; private set; }
        public RunSessionHost Host { get; private set; }
        public RunSession Session => Host != null ? Host.Session : null;
        public SessionUiBridge Ui { get; private set; }
        public MenuCoordinator Coordinator => Ui?.Coordinator;
        public AppServices Services { get; private set; }
        public SynapticSeaInput Input => Services != null ? Services.Input : null;
        public UIDocument HudDocument { get; private set; }
        public UIDocument MenuDocument { get; private set; }
        public bool IsBooted { get; private set; }
        public string BootFailure { get; private set; } = "";
        /// <summary>The Continue/LoadSlot result (true for a new run).</summary>
        public bool LaunchApplied { get; private set; }

        public PanelSettings HudPanelSettings { get => hudPanelSettings; set => hudPanelSettings = value; }
        public PanelSettings MenuPanelSettings { get => menuPanelSettings; set => menuPanelSettings = value; }
        public VolumeProfile GlobalVolumeProfile { get => globalVolumeProfile; set => globalVolumeProfile = value; }

        void Start()
        {
            if (bootOnStart && !IsBooted) Boot();
        }

        public void Boot()
        {
            if (IsBooted) return;
            Current = this;
            Launch = RunLaunchRequest.Consume();
            Services = AppServices.Ensure();
            EnsureGlobalVolume();
            AtmosphereApplier.ApplyGodotDefaultEnvironment();

            HudDocument = MakeDocument("HUD", hudPanelSettings, 0);
            var hud = HudDocument.gameObject.AddComponent<HudRoot>();
            HudDocument.gameObject.SetActive(true);
            MenuDocument = MakeDocument("Menus", menuPanelSettings, 10);
            MenuDocument.gameObject.SetActive(true);
            Ui = new SessionUiBridge(hud, MenuDocument, Services.Input, Services.Accessibility);

            var hostGo = new GameObject("PlayableGeneratedShip");
            Host = hostGo.AddComponent<RunSessionHost>();
            RunSessionDeps deps = DepsFor(Launch);
            deps.UiState = Ui;
            RunSession session = Host.Boot(deps, Services.Audio, Services.Input, s =>
            {
                Ui.BindSessionEvents(s);
                s.ReturnToTitleRequested += OnReturnToTitle;
                s.PlayableSliceCompleted += OnSliceCompleted;
            });
            if (!session.PlayableStarted)
            {
                BootFailure = session.LastFailureReason;
                RunReturnInfo.LastFailureReason = BootFailure;
                Debug.LogError("PlayableBootstrap: run failed to start: " + BootFailure);
                return;
            }
            Ui.BuildCoordinator(session, Host, Services.Audio);
            Ui.SettingsPersist = summary => Services.ApplySettings(summary);
            LaunchApplied = ApplyLaunch(session, Launch);
            Host.ApplyViews();
            IsBooted = true;
            Booted?.Invoke(this);
        }

        /// <summary>The session dependencies for a launch (paths, starting class).</summary>
        public static RunSessionDeps DepsFor(RunLaunchRequest launch)
        {
            var deps = new RunSessionDeps
            {
                LayoutPath = GoldenDir + "layout.json",
                KitPath = RunSession.DEFAULT_KIT_PATH,
                GameplaySlicePath = GoldenDir + "gameplay_slice.json",
                BlueprintPath = GoldenDir + "blueprint.json",
            };
            if (launch == null) return deps;
            deps.StartingClassId = string.IsNullOrEmpty(launch.ClassId) ? RunLaunchRequest.DefaultClassId : launch.ClassId;
            if (launch.Seed != RunLaunchRequest.DefaultSeed)
                Debug.LogWarning($"PlayableBootstrap: seed {launch.Seed} start generation is not ported; using Godot's default start (seed {RunLaunchRequest.DefaultSeed})");
            deps.LayoutPath = RunSession.DEFAULT_LAYOUT_PATH;
            deps.GameplaySlicePath = RunSession.DEFAULT_GAMEPLAY_SLICE_PATH;
            return deps;
        }

        /// <summary>title_main.gd's handoff: load (Continue / LoadSlot), then the dirty title settings.</summary>
        static bool ApplyLaunch(RunSession session, RunLaunchRequest launch)
        {
            if (launch == null) return true;
            bool applied = true;
            switch (launch.Mode)
            {
                case RunLaunchMode.Continue:
                    applied = session.RequestLoad();
                    break;
                case RunLaunchMode.LoadSlot:
                    RunSnapshot snapshot = session.SaveLoadService.LoadFromSlot(launch.SlotId);
                    applied = snapshot != null && session.ApplyManualSlot(snapshot);
                    break;
            }
            if (!applied)
            {
                RunReturnInfo.LastFailureReason = "save could not be applied (" + launch.Mode + " " + launch.SlotId + ")";
                Debug.LogWarning("PlayableBootstrap: " + RunReturnInfo.LastFailureReason + "; continuing with a fresh run");
            }
            if (launch.SettingsSummary != null) session.ApplyUiSettingsSummary(launch.SettingsSummary);
            return applied;
        }

        void Update()
        {
            if (IsBooted) Ui.Tick();
        }

        void OnDestroy()
        {
            Ui?.Dispose();
            if (Current == this) Current = null;
        }

        void OnSliceCompleted(GdDict summary)
        {
            RunReturnInfo.LastRunOutcome = V.Str(summary.Get("reason", "complete"));
        }

        void OnReturnToTitle()
        {
            RunSession s = Session;
            if (s != null)
            {
                GdDict completion = s.GetSliceCompletionSummary();
                RunReturnInfo.LastRunProgress = "objectives " + V.I64(completion.Get("objectives_completed", 0L)) + "/" + V.I64(completion.Get("objective_count", 0L));
            }
            Ui?.Dispose();
            if (SceneLoader == null || !SceneLoader(RunLaunchRequest.TitleSceneName))
                Debug.LogWarning("PlayableBootstrap: the Title scene is not in this build");
        }

        static bool DefaultSceneLoader(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName)) return false;
            SceneManager.LoadScene(sceneName);
            return true;
        }

        // ------------------------------------------------------------------ composition helpers

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
