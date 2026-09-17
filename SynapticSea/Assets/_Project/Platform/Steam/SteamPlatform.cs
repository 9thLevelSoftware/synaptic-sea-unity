using System;
using System.IO;
using SynapticSea.App;
using SynapticSea.Runtime.Session;
using Steamworks;
using UnityEngine;

namespace SynapticSea.Platform.Steam
{
    /// <summary>
    /// Steam integration (plan Phase 12 stretch). This assembly compiles only with the <c>SS_STEAM</c> scripting define
    /// and the Facepunch.Steamworks plugin present (see README.md); without them nothing here exists in the build.
    /// Starts the Steam client once per process, pumps callbacks on the main thread, and mirrors each booted session's
    /// achievement unlocks through <see cref="SteamAchievements"/>.
    /// </summary>
    public sealed class SteamPlatform : MonoBehaviour
    {
        /// <summary>Holds the numeric Steam app id; next to the executable (the project root in the editor).</summary>
        public const string AppIdFile = "steam_appid.txt";

        const float HostSearchInterval = 1f;

        public static SteamPlatform Instance { get; private set; }
        public bool Initialized { get; private set; }

        RunSessionHost _host;
        float _nextHostSearch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null || Application.isBatchMode) return;
            var go = new GameObject("SteamPlatform");
            DontDestroyOnLoad(go);
            go.AddComponent<SteamPlatform>();
        }

        void Awake()
        {
            Instance = this;
            AppServices services = AppServices.Ensure();
            if (!services.BuildMetadata.IsAchievementsSupported())
            {
                Debug.Log("[SteamPlatform] achievements_supported is false in build_metadata.json; Steam stays off");
                return;
            }
            uint appId = ReadAppId();
            if (appId == 0)
            {
                Debug.LogWarning("[SteamPlatform] no " + AppIdFile + " with a numeric app id; Steam stays off");
                return;
            }
            try
            {
                // Callbacks are pumped from Update so every Steam callback lands on Unity's main thread.
                SteamClient.Init(appId, false);
                Initialized = true;
                Debug.Log("[SteamPlatform] Steam client ready app_id=" + appId);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SteamPlatform] Steam client init failed (is Steam running?): " + e.Message);
            }
        }

        void Update()
        {
            if (!Initialized) return;
            SteamClient.RunCallbacks();
            if (_host != null || Time.unscaledTime < _nextHostSearch) return;
            _nextHostSearch = Time.unscaledTime + HostSearchInterval;
            _host = FindAnyObjectByType<RunSessionHost>();
            if (_host == null) return;
            _host.SessionBooted += SteamAchievements.Bind;
            if (_host.Session != null) SteamAchievements.Bind(_host.Session);
        }

        void OnDestroy()
        {
            if (_host != null) _host.SessionBooted -= SteamAchievements.Bind;
            SteamAchievements.Unbind();
            if (Initialized) SteamClient.Shutdown();
            Initialized = false;
            if (Instance == this) Instance = null;
        }

        static uint ReadAppId()
        {
            string path = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", AppIdFile);
            if (!File.Exists(path)) return 0;
            return uint.TryParse(File.ReadAllText(path).Trim(), out uint id) ? id : 0;
        }
    }
}
