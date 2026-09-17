using System;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Input;
using SynapticSea.UI.Presenters;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace SynapticSea.App
{
    /// <summary>
    /// Process-wide composition root (Boot scene; survives scene loads). Owns what Godot's autoload-free project got
    /// from the engine or rebuilt per scene: the <see cref="CoreServices"/> seams (StreamingAssets resources,
    /// persistent-data storage, clock, logging), the user preferences (<see cref="UserSettingsStore"/>) mirrored into
    /// <see cref="AccessibilitySettings"/>, build metadata + <c>build_stamp.json</c> + the demo scope gate, the
    /// <see cref="AudioManager"/> (skipped in batch mode), the shared <see cref="SynapticSeaInput"/>, and an EventSystem
    /// with <see cref="InputSystemUIInputModule"/> driving UI Toolkit navigation from the input asset's UI map.
    ///
    /// Scenes call <see cref="Ensure"/>, so Title (or Playable) opened directly in the editor composes the same services.
    /// </summary>
    public sealed class AppServices : MonoBehaviour
    {
        public const string BuildMetadataPath = "res://data/release/build_metadata.json";
        public const string DemoScopeManifestPath = "res://data/release/demo_scope_manifest.json";
        public const string BuildStampPath = "res://build_stamp.json";

        /// <summary>Test seam: user storage to use instead of <c>Application.persistentDataPath</c>.</summary>
        public static IStorage StorageOverride;

        public static AppServices Instance { get; private set; }

        public bool Headless { get; private set; }
        public SynapticSeaInput Input { get; private set; }
        /// <summary>Null when running in batch mode (no audio).</summary>
        public AudioManager Audio { get; private set; }
        public SettingsState Settings { get; } = new SettingsState();
        public AccessibilitySettings Accessibility { get; private set; }
        public BuildMetadataState BuildMetadata { get; } = new BuildMetadataState();
        public DemoScopeGate DemoScopeGate { get; } = new DemoScopeGate();
        /// <summary>The Builder's <c>build_stamp.json</c>; empty when absent (editor).</summary>
        public GdDict BuildStamp { get; private set; } = new GdDict();
        public EventSystem EventSystem { get; private set; }
        public InputSystemUIInputModule UiInputModule { get; private set; }

        /// <summary>Raised after <see cref="ApplySettings"/> stores new preferences.</summary>
        public event Action<GdDict> SettingsApplied;

        /// <summary>Returns the live services, composing them on first use.</summary>
        public static AppServices Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("AppServices");
            DontDestroyOnLoad(go);
            var services = go.AddComponent<AppServices>();
            services.Compose();
            return services;
        }

        /// <summary>Tears the services down (tests; the player never calls this).</summary>
        public static void Shutdown()
        {
            if (Instance == null) return;
            AppServices services = Instance;
            Instance = null;
            services.Release();
            if (Application.isPlaying) Destroy(services.gameObject);
            else DestroyImmediate(services.gameObject);
        }

        void Compose()
        {
            Instance = this;
            Headless = Application.isBatchMode;

            CoreServices.Clock = new SystemClock();
            CoreServices.Log = new UnityLog();
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CoreServices.UserStorage = StorageOverride ?? new FileSystemStorage(Application.persistentDataPath);
            CatalogRegistry.Clear();

            ComposeBuildInfo();
            ComposeSettings();

            Input = new SynapticSeaInput();
            ComposeEventSystem();

            if (!Headless)
            {
                var audioGo = new GameObject("AudioManager");
                audioGo.transform.SetParent(transform, false);
                Audio = audioGo.AddComponent<AudioManager>();
            }
            CoreServices.Log.Info($"[AppServices] composed headless={Headless} build={BuildMetadata.GetBuildKind()} version={BuildMetadata.Version} " +
                                  $"text_scale={Accessibility.GetTextScale():0.0#} stamp={(BuildStamp.IsEmpty ? "none" : "present")}");
        }

        void ComposeBuildInfo()
        {
            GdDict manifest = CatalogRegistry.LoadDict(BuildMetadataPath) ?? new GdDict();
            GdDict stamp = null;
            if (CoreServices.Resources.Exists(BuildStampPath)) stamp = GdJson.ParseString(CoreServices.Resources.ReadText(BuildStampPath)) as GdDict;
            BuildStamp = stamp ?? new GdDict();
            BuildMetadata.Configure(ApplyStamp(manifest, BuildStamp));
            CoreServices.ProjectVersion = ProjectVersionFor(BuildStamp, Application.version);
            DemoScopeGate.Configure(CatalogRegistry.LoadDict(DemoScopeManifestPath) ?? new GdDict(), BuildMetadata);
        }

        /// <summary>
        /// The build stamp wins over the synced manifest for kind and version: the Builder picks the kind per build,
        /// while <c>data/release/build_metadata.json</c> always says <c>dev</c>.
        /// </summary>
        public static GdDict ApplyStamp(GdDict manifest, GdDict stamp)
        {
            GdDict result = (manifest ?? new GdDict()).DeepCopy();
            if (stamp == null || stamp.IsEmpty) return result;
            string kind = stamp.GetString("build_kind", "");
            if (kind.Length != 0) result["build_kind"] = kind;
            string version = stamp.GetString("version", "");
            string sha = stamp.GetString("git_sha", "");
            if (version.Length != 0) result["version"] = "v" + version + (sha.Length != 0 ? "+" + sha : "");
            return result;
        }

        /// <summary>
        /// Godot <c>ProjectSettings application/config/version</c> (<c>CloudManifestState.BuildId</c>): the build stamp's
        /// version, else <c>Application.version</c> (the player settings bundle version, set in the editor too).
        /// </summary>
        public static string ProjectVersionFor(GdDict stamp, string applicationVersion)
        {
            string version = stamp != null ? stamp.GetString("version", "") : "";
            return version.Length != 0 ? version : applicationVersion ?? "";
        }

                void ComposeSettings()
        {
            // The environment variable seeds the text scale on first run; stored preferences win afterwards.
            Accessibility = new AccessibilitySettings();
            GdDict stored = UserSettingsStore.Load(CoreServices.UserStorage);
            if (stored == null || !Settings.ApplySummary(stored)) Settings.SetTextScale(Accessibility.GetTextScale());
            Settings.ApplyToAccessibility(Accessibility);
        }

        void ComposeEventSystem()
        {
            var esGo = new GameObject("EventSystem");
            esGo.transform.SetParent(transform, false);
            EventSystem = esGo.AddComponent<EventSystem>();
            UiInputModule = esGo.AddComponent<InputSystemUIInputModule>();
            UiInputModule.actionsAsset = Input.asset;
            UiInputModule.move = InputActionReference.Create(Input.UI.Navigate);
            UiInputModule.submit = InputActionReference.Create(Input.UI.Submit);
            UiInputModule.cancel = InputActionReference.Create(Input.UI.Cancel);
            UiInputModule.point = InputActionReference.Create(Input.UI.Point);
            UiInputModule.leftClick = InputActionReference.Create(Input.UI.Click);
            UiInputModule.scrollWheel = InputActionReference.Create(Input.UI.ScrollWheel);
            UiInputModule.middleClick = InputActionReference.Create(Input.UI.MiddleClick);
            UiInputModule.rightClick = InputActionReference.Create(Input.UI.RightClick);
            Input.UI.Enable();
        }

        /// <summary>Stores new preferences (shared by title and in-run UI), mirrors them into accessibility, persists.</summary>
        public bool ApplySettings(GdDict summary)
        {
            if (!Settings.ApplySummary(summary)) return false;
            Settings.ApplyToAccessibility(Accessibility);
            bool saved = UserSettingsStore.Save(CoreServices.UserStorage, Settings.GetSummary());
            SettingsApplied?.Invoke(Settings.GetSummary());
            return saved;
        }

        void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                Release();
            }
        }

        void Release()
        {
            if (Input == null) return;
            if (UiInputModule != null) UiInputModule.enabled = false;
            Input.Disable();
            Input.Dispose();
            Input = null;
        }
    }

    /// <summary>
    /// The standalone preferences file (ui_presentation_program.md "Settings ownership": one user preference source
    /// shared by title and in-run UI). Holds a <see cref="SettingsState"/> summary.
    /// </summary>
    public static class UserSettingsStore
    {
        public const string SettingsPath = "user://settings.json";

        public static GdDict Load(IStorage storage)
        {
            if (storage == null || !storage.FileExists(SettingsPath)) return null;
            return GdJson.ParseString(storage.ReadText(SettingsPath) ?? "") as GdDict;
        }

        public static bool Save(IStorage storage, GdDict summary)
        {
            if (storage == null || summary == null) return false;
            try
            {
                storage.WriteText(SettingsPath, GdJson.Stringify(summary, "\t"));
                return true;
            }
            catch (Exception e)
            {
                CoreServices.Log.Warning("UserSettingsStore: could not write " + SettingsPath + ": " + e.Message);
                return false;
            }
        }
    }

    /// <summary><see cref="ILog"/> onto the Unity console / player log.</summary>
    public sealed class UnityLog : ILog
    {
        public void Info(string message) => Debug.Log(message);
        public void Warning(string message) => Debug.LogWarning(message);
        public void Error(string message) => Debug.LogError(message);
    }
}
