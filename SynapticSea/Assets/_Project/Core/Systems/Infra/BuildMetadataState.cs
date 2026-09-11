// Ported from scripts/systems/build_metadata_state.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The duck-typed <c>get_build_kind()</c> seam <see cref="DemoScopeGate"/> calls on its build metadata.
    /// </summary>
    public interface IBuildKindProvider
    {
        string GetBuildKind();
    }

    /// <summary>
    /// REQ-RL-002 build metadata state. Single source of truth for "what kind of build is running"
    /// (<c>dev</c>, <c>demo</c>, <c>release</c>), read from <c>data/release/build_metadata.json</c>.
    /// </summary>
    public class BuildMetadataState : IBuildKindProvider, IStatusLineProvider
    {
        public static readonly GdArray ValidBuildKinds = GdArray.Of("dev", "demo", "release");
        public const string DefaultVersion = "v0.0.0";
        public const string DefaultStore = "direct";

        public string Version = DefaultVersion;
        public string BuildKind = "";
        public string Store = DefaultStore;
        public GdArray LanguageDefaults = GdArray.Of("en");
        public bool AchievementsSupported = false;
        public GdArray DemoHubUnlockedFeatures = new GdArray();
        public string ReleaseDate = "";
        // ADR-0029 deferred crash-upload endpoint placeholder; no consumer exists until crash upload is wired.
        public string TelemetryEndpointPlaceholder = "";

        // true after Configure() ran with a known build_kind.
        bool _validated = false;

        public void Configure(GdDict manifest)
        {
            if (manifest == null) manifest = new GdDict();
            Version = V.Str(manifest.Get("version", DefaultVersion));
            BuildKind = V.Str(manifest.Get("build_kind", "dev"));
            Store = V.Str(manifest.Get("store", DefaultStore));
            LanguageDefaults.Clear();
            object langVariant = manifest.Get("language_defaults", GdArray.Of("en"));
            if (langVariant is GdArray langs)
            {
                foreach (var lang in langs)
                {
                    string langStr = V.Str(lang);
                    if (langStr.Length == 0) continue;
                    LanguageDefaults.Add(langStr);
                }
            }
            if (LanguageDefaults.IsEmpty) LanguageDefaults.Add("en");
            AchievementsSupported = V.Bool(manifest.Get("achievements_supported", false));
            DemoHubUnlockedFeatures.Clear();
            object hubVariant = manifest.Get("demo_hub_unlocked_features", new GdArray());
            if (hubVariant is GdArray hubs)
            {
                foreach (var hubId in hubs)
                {
                    string hubIdStr = V.Str(hubId);
                    if (hubIdStr.Length == 0) continue;
                    DemoHubUnlockedFeatures.Add(hubIdStr);
                }
            }
            ReleaseDate = V.Str(manifest.Get("release_date", ""));
            TelemetryEndpointPlaceholder = V.Str(manifest.Get("telemetry_endpoint_placeholder", ""));
            _validated = ValidBuildKinds.Contains(BuildKind);
        }

        public string GetBuildKind() => BuildKind;

        public bool IsAchievementsSupported() => AchievementsSupported;

        public bool IsBuildKindValidated() => _validated;

        public string GetDefaultLanguage()
        {
            if (LanguageDefaults.IsEmpty) return "en";
            return V.Str(LanguageDefaults[0]);
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "version", Version },
                { "build_kind", BuildKind },
                { "store", Store },
                { "language_defaults", LanguageDefaults.ShallowCopy() },
                { "achievements_supported", AchievementsSupported },
                { "demo_hub_unlocked_features", DemoHubUnlockedFeatures.ShallowCopy() },
                { "release_date", ReleaseDate },
                { "telemetry_endpoint_placeholder", TelemetryEndpointPlaceholder },
                { "build_kind_validated", _validated },
                { "valid_build_kinds", ValidBuildKinds.ShallowCopy() },
            };
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Build: " + Version + " (" + BuildKind + ")");
            if (!_validated) lines.Add("Build: WARN unknown build_kind=" + BuildKind);
            lines.Add("Store: " + Store);
            var langs = new List<string>();
            foreach (var lang in LanguageDefaults) langs.Add(V.Str(lang));
            lines.Add("Languages: " + string.Join(",", langs));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();
    }
}
