// Composition root for scenes/procgen/playable_generated_ship.tscn @ 96ecb2b0 (PlayableGeneratedShip._ready plus the
// title handoff title_main.gd made: request_load() / apply_ui_settings_summary()), with the Unity-port run lifecycle:
// Milestone A New Run hub (golden coherent_ship_001), the end-of-run results (A2) and failures back to the title (A3).
using System;
using SynapticSea.App;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
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
    /// <item>no request (scene opened directly) and Title New Run: Milestone A hub golden <c>coherent_ship_001</c>.
    /// Non-slice seed/biome/difficulty fail closed and return to Title;</item>
    /// <item>NewRun with <see cref="RunLaunchRequest.LayoutOverridePath"/> (tests): that pre-authored layout;</item>
    /// <item>Continue / LoadSlot: boots the saved home layout, then <c>request_load()</c> / the manual slot;</item>
    /// <item>SettingsSummary (when the title settings changed) is applied after any load.</item>
    /// </list>
    /// A failed boot, generation or load stores <see cref="RunReturnInfo.LastFailureReason"/> and returns to the title (no
    /// silent fresh run). Death or completion pauses the run and shows <see cref="RunResultsPanel"/>; its confirm stores
    /// the outcome in <see cref="RunReturnInfo"/> and loads <see cref="RunLaunchRequest.TitleSceneName"/>.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class PlayableBootstrap : MonoBehaviour
    {
        public const string GoldenDir = MilestoneALaunch.HubDir;
        /// <summary>Where generated run documents live (Continue / janitor). Milestone A New Run does not write here.</summary>
        public const string RunsDir = RunDirectoryJanitor.RunsDir + "/";

        /// <summary>The running bootstrap (null outside the Playable scene).</summary>
        public static PlayableBootstrap Current { get; private set; }

        /// <summary>Raised after a boot finished (tests).</summary>
        public static event Action<PlayableBootstrap> Booted;

        /// <summary>Raised when a boot failed and the bootstrap is returning to the title (tests).</summary>
        public static event Action<PlayableBootstrap> BootFailed;

        /// <summary>Scene-load seam for the return to title (tests capture it). Returns false when it cannot load.</summary>
        public static Func<string, bool> SceneLoader = DefaultSceneLoader;

        /// <summary>Seed source for the results screen's New Run (tests pin it).</summary>
        public static Func<long> RandomSeed = DefaultRandomSeed;

        [SerializeField] PanelSettings hudPanelSettings;
        [SerializeField] PanelSettings menuPanelSettings;
        [SerializeField] VolumeProfile globalVolumeProfile;
        [Tooltip("Boot automatically in Start (off for tests that boot by hand).")]
        [SerializeField] bool bootOnStart = true;

        /// <summary>The launch this boot honoured: the consumed request, or the defaults when the scene started without one.</summary>
        public RunLaunchRequest Launch { get; private set; }
        /// <summary>True when the scene started without a pending request (opened directly).</summary>
        public bool DirectOpen { get; private set; }
        public RunSessionHost Host { get; private set; }
        public RunSession Session => Host != null ? Host.Session : null;
        public SessionUiBridge Ui { get; private set; }
        public MenuCoordinator Coordinator => Ui?.Coordinator;
        public AppServices Services { get; private set; }
        public SynapticSeaInput Input => Services != null ? Services.Input : null;
        public UIDocument HudDocument { get; private set; }
        public UIDocument MenuDocument { get; private set; }
        public bool IsBooted { get; private set; }
        /// <summary>True once a boot failed (the title has been requested).</summary>
        public bool IsFailed { get; private set; }
        public string BootFailure { get; private set; } = "";
        /// <summary>The Continue/LoadSlot result (true for a new run).</summary>
        public bool LaunchApplied { get; private set; }
        /// <summary>The generated home start of a new run (null for Milestone A hub, loads and layout overrides).</summary>
        public StartSceneBuilder.HomeStart GeneratedStart { get; private set; }
        /// <summary>The <c>user://runs/&lt;run_id&gt;/</c> directory of a generated run ("" otherwise).</summary>
        public string RunDirectory { get; private set; } = "";
        /// <summary>The end-of-run results surface once the run ended (null before).</summary>
        public RunResultsPanel Results { get; private set; }
        /// <summary>The completion summary shown on <see cref="Results"/>, with the run context added.</summary>
        public GdDict ResultsSummary { get; private set; }

        public PanelSettings HudPanelSettings { get => hudPanelSettings; set => hudPanelSettings = value; }
        public PanelSettings MenuPanelSettings { get => menuPanelSettings; set => menuPanelSettings = value; }
        public VolumeProfile GlobalVolumeProfile { get => globalVolumeProfile; set => globalVolumeProfile = value; }

        GdDict _pendingCompletion;
        bool _leaving;

        void Start()
        {
            if (bootOnStart && !IsBooted && !IsFailed) Boot();
        }

        public void Boot()
        {
            if (IsBooted || IsFailed) return;
            Current = this;
            RunLaunchRequest pending = RunLaunchRequest.Consume();
            DirectOpen = pending == null;
            Launch = pending ?? RunLaunchRequest.NewRun();
            if (Launch.Mode == RunLaunchMode.NewRun
                && string.IsNullOrEmpty(Launch.LayoutOverridePath)
                && !MilestoneALaunch.TryAccept(Launch.Seed, Launch.BiomeId, Launch.DifficultyId, out string closedReason))
            {
                FailToTitle(closedReason);
                return;
            }
            Services = AppServices.Ensure();
            EnsureGlobalVolume();
            AtmosphereApplier.ApplyGodotDefaultEnvironment();

            HudDocument = MakeDocument("HUD", hudPanelSettings, 0);
            var hud = HudDocument.gameObject.AddComponent<HudRoot>();
            HudDocument.gameObject.SetActive(true);
            MenuDocument = MakeDocument("Menus", menuPanelSettings, 10);
            MenuDocument.gameObject.SetActive(true);
            Ui = new SessionUiBridge(hud, MenuDocument, Services.Input, Services.Accessibility);

            RunSessionDeps deps = PrepareDeps(Launch, out string failure);
            if (deps == null)
            {
                FailToTitle(failure);
                return;
            }
            deps.UiState = Ui;
            deps.SettingsState = Services.Settings;

            var hostGo = new GameObject("PlayableGeneratedShip");
            Host = hostGo.AddComponent<RunSessionHost>();
            RunSession session = Host.Boot(deps, Services.Audio, Services.Input, s =>
            {
                Ui.BindSessionEvents(s);
                s.ReturnToTitleRequested += OnReturnToTitle;
                s.PlayableSliceCompleted += OnSliceCompleted;
            });
            if (!session.PlayableStarted)
            {
                FailToTitle("the run failed to start: " + session.LastFailureReason);
                return;
            }
            Ui.BuildCoordinator(session, Host, Services.Audio);
            Ui.SettingsPersist = summary => Services.ApplySettings(summary);
            LaunchApplied = ApplyLaunch(session, Launch, out failure);
            if (!LaunchApplied)
            {
                FailToTitle(failure);
                return;
            }
            Host.ApplyViews();
            IsBooted = true;
            CoreServices.Log.Info($"[PlayableBootstrap] booted {Launch} layout={session.LayoutPath} seed={session.RunSeed} biome={session.BiomeId} difficulty={session.DifficultyId}");
            Booted?.Invoke(this);
            if (_pendingCompletion != null) ShowResults(_pendingCompletion);
        }

        // ------------------------------------------------------------------ launch → session dependencies

        /// <summary>
        /// The session dependencies for <paramref name="launch"/> (paths, run context, starting class); null with
        /// <paramref name="failure"/> when the run cannot start. Title New Run and a direct-open scene use the Milestone A
        /// golden hub. Tests may override the layout path. Continue / LoadSlot boot the saved home.
        /// </summary>
        public RunSessionDeps PrepareDeps(RunLaunchRequest launch, out string failure)
        {
            failure = "";
            launch = launch ?? RunLaunchRequest.NewRun();
            var deps = new RunSessionDeps
            {
                StartingClassId = string.IsNullOrEmpty(launch.ClassId) ? RunLaunchRequest.DefaultClassId : launch.ClassId,
                DifficultyId = string.IsNullOrEmpty(launch.DifficultyId) ? RunLaunchRequest.DefaultDifficultyId : launch.DifficultyId,
                BiomeId = launch.BiomeId ?? "",
            };
            switch (launch.Mode)
            {
                case RunLaunchMode.Continue:
                {
                    var service = new SaveLoadService(CoreServices.UserStorage, CoreServices.Clock);
                    WorldSnapshot world = service.LoadWorld();
                    if (world == null)
                    {
                        failure = "no compatible world save to continue";
                        return null;
                    }
                    return ApplySavedHome(deps, world.HomeShip, "world save", out failure) ? deps : null;
                }
                case RunLaunchMode.LoadSlot:
                {
                    var service = new SaveLoadService(CoreServices.UserStorage, CoreServices.Clock);
                    RunSnapshot slot = service.LoadFromSlot(launch.SlotId);
                    if (slot == null)
                    {
                        failure = "save slot '" + launch.SlotId + "' could not be loaded";
                        return null;
                    }
                    return ApplySavedHome(deps, slot.ToDict(), "slot " + launch.SlotId, out failure) ? deps : null;
                }
                case RunLaunchMode.NewRun:
                    break;
                default:
                    throw new InvalidOperationException("unhandled launch mode: " + launch.Mode);
            }

            if (!string.IsNullOrEmpty(launch.LayoutOverridePath))
            {
                string dir = ResPath.GetBaseDir(launch.LayoutOverridePath) + "/";
                deps.LayoutPath = launch.LayoutOverridePath;
                deps.GameplaySlicePath = dir + "gameplay_slice.json";
                deps.KitPath = RunSession.DEFAULT_KIT_PATH;
                if (CatalogRegistry.Exists(dir + "blueprint.json")) deps.BlueprintPath = dir + "blueprint.json";
                if (!CatalogRegistry.Exists(deps.LayoutPath))
                {
                    failure = "layout override not found: " + deps.LayoutPath;
                    return null;
                }
                return deps;
            }

            MilestoneALaunch.ApplyHubPaths(deps);
            deps.RunSeed = launch.Seed;
            return deps;
        }

        /// <summary>A saved home ship's paths and run context (Continue / LoadSlot boot the layout the save names).</summary>
        static bool ApplySavedHome(RunSessionDeps deps, GdDict home, string label, out string failure)
        {
            failure = "";
            home = home ?? new GdDict();
            string layout = home.GetString("layout_path", "");
            string slice = home.GetString("gameplay_slice_path", "");
            if (layout.Length == 0 || !CatalogRegistry.Exists(layout))
            {
                failure = "the " + label + " ship layout is missing (" + (layout.Length == 0 ? "no path" : layout) + ")";
                return false;
            }
            if (slice.Length == 0 || !CatalogRegistry.Exists(slice))
            {
                failure = "the " + label + " gameplay slice is missing (" + (slice.Length == 0 ? "no path" : slice) + ")";
                return false;
            }
            deps.LayoutPath = layout;
            deps.GameplaySlicePath = slice;
            string kit = home.GetString("kit_path", "");
            deps.KitPath = kit.Length != 0 ? kit : RunSession.DEFAULT_KIT_PATH;
            string blueprint = ResPath.GetBaseDir(layout) + "/blueprint.json";
            if (CatalogRegistry.Exists(blueprint)) deps.BlueprintPath = blueprint;
            if (home.Get("run_context", null) is GdDict ctx && !ctx.IsEmpty)
            {
                deps.DifficultyId = ctx.GetString("difficulty_id", deps.DifficultyId);
                deps.BiomeId = ctx.GetString("biome_id", deps.BiomeId);
                if (ctx.Has("seed")) deps.RunSeed = V.I64(ctx["seed"]);
            }
            return true;
        }

        /// <summary>title_main.gd's handoff: load (Continue / LoadSlot), then the dirty title settings.</summary>
        static bool ApplyLaunch(RunSession session, RunLaunchRequest launch, out string failure)
        {
            failure = "";
            bool applied = true;
            switch (launch.Mode)
            {
                case RunLaunchMode.Continue:
                    applied = session.RequestLoad();
                    if (!applied) failure = "the world save could not be applied";
                    break;
                case RunLaunchMode.LoadSlot:
                    RunSnapshot snapshot = session.SaveLoadService.LoadFromSlot(launch.SlotId);
                    applied = snapshot != null && session.ApplyManualSlot(snapshot);
                    if (!applied) failure = "save slot '" + launch.SlotId + "' could not be applied";
                    break;
                case RunLaunchMode.NewRun:
                    break;
                default:
                    throw new InvalidOperationException("unhandled launch mode: " + launch.Mode);
            }
            if (applied && launch.SettingsSummary != null) session.ApplyUiSettingsSummary(launch.SettingsSummary);
            return applied;
        }

        /// <summary>A3: record the failure and go back to the title (which shows "Load failed: …").</summary>
        void FailToTitle(string reason)
        {
            IsFailed = true;
            BootFailure = string.IsNullOrEmpty(reason) ? "unknown failure" : reason;
            RunReturnInfo.LastFailureReason = BootFailure;
            Debug.LogWarning("PlayableBootstrap: " + BootFailure + "; returning to the title");
            BootFailed?.Invoke(this);
            LeaveToTitle();
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

        // ------------------------------------------------------------------ run end (A2)

        void OnSliceCompleted(GdDict summary)
        {
            GdDict completion = (summary ?? new GdDict()).DeepCopy();
            if (!IsBooted || Coordinator == null)
            {
                _pendingCompletion = completion;
                return;
            }
            ShowResults(completion);
        }

        /// <summary>Pauses the run (TERMINAL surface on the modal stack) and shows the results with the run context.</summary>
        void ShowResults(GdDict completion)
        {
            _pendingCompletion = null;
            if (Results != null || _leaving) return;
            RunSession s = Session;
            GdDict summary = completion.DeepCopy();
            if (s != null)
            {
                summary["seed"] = s.RunSeed;
                summary["biome_id"] = s.BiomeId;
                summary["difficulty_id"] = s.DifficultyId;
            }
            ResultsSummary = summary;
            Results = new RunResultsPanel();
            Results.SetRunSummary(summary);
            Results.SetContextLine(ContextLine(summary));
            Results.ReturnToTitleRequested += ConfirmResults;
            Results.NewRunRequested += StartNextRun;
            Coordinator.MenuState.CloseAll();
            Coordinator.OpenInspection(Results);
            RecordReturnInfo(summary, Results.NormalizedOutcome());
        }

        /// <summary>The results / title context line, e.g. "seed 17 · breach_field · standard".</summary>
        public static string ContextLine(GdDict summary)
        {
            string biome = summary.GetString("biome_id", "");
            return "seed " + V.I64(summary.Get("seed", 0L)) + " · " + (biome.Length != 0 ? biome : "no biome") + " · " + summary.GetString("difficulty_id", "standard");
        }

        static void RecordReturnInfo(GdDict summary, string outcome)
        {
            RunReturnInfo.LastRunOutcome = outcome;
            RunReturnInfo.LastRunProgress = "objectives " + V.I64(summary.Get("objectives_completed", 0L)) + "/" + V.I64(summary.Get("objective_count", 0L));
            RunReturnInfo.LastRunContext = ContextLine(summary);
            long seconds = Math.Max(0L, (long)Math.Floor(V.F64(summary.Get("play_time_seconds", 0.0))));
            RunReturnInfo.LastRunTime = (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
        }

        /// <summary>Results confirm (Return to Title): record the outcome and load the title.</summary>
        public void ConfirmResults()
        {
            if (ResultsSummary != null) RecordReturnInfo(ResultsSummary, Results.NormalizedOutcome());
            LeaveToTitle();
        }

        /// <summary>Results "New Run": another Milestone A hub boot (slice seed / biome / difficulty) with the same class.</summary>
        void StartNextRun()
        {
            if (_leaving) return;
            RunLaunchRequest next = RunLaunchRequest.NewRun();
            next.ClassId = Launch.ClassId;
            if (ResultsSummary != null) RecordReturnInfo(ResultsSummary, Results.NormalizedOutcome());
            _leaving = true;
            Ui?.Dispose();
            RunLaunchRequest.Pending = next;
            if (SceneLoader == null || !SceneLoader(RunLaunchRequest.PlayableSceneName))
            {
                RunLaunchRequest.Pending = null;
                Debug.LogWarning("PlayableBootstrap: the Playable scene is not in this build");
            }
        }

        void OnReturnToTitle()
        {
            RunSession s = Session;
            if (s != null)
            {
                GdDict completion = s.GetSliceCompletionSummary();
                RunReturnInfo.LastRunProgress = "objectives " + V.I64(completion.Get("objectives_completed", 0L)) + "/" + V.I64(completion.Get("objective_count", 0L));
            }
            LeaveToTitle();
        }

        void LeaveToTitle()
        {
            if (_leaving) return;
            _leaving = true;
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

        static long DefaultRandomSeed() => UnityEngine.Random.Range(1, int.MaxValue);

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
