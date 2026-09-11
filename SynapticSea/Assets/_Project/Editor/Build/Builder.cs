using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SynapticSea.Core.Variant;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SynapticSea.EditorTools.Build
{
    /// <summary>
    /// Player build entry point (plan Phase 12). Validates the synced data, stamps the version from
    /// data/release/build_metadata.json + git, sets the build-kind define, picks the scripting backend
    /// (Mono for dev/demo, IL2CPP for release, falling back to Mono when no C++ toolchain is present), and builds.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Build.Builder.PerformBuild
    ///             -buildTarget StandaloneWindows64 -buildKind dev|demo|release -outputPath &lt;dir&gt;
    /// </summary>
    public static class Builder
    {
        public static void PerformBuild()
        {
            string[] args = Environment.GetCommandLineArgs();
            string targetName = Arg(args, "-buildTarget") ?? "StandaloneWindows64";
            string kind = (Arg(args, "-buildKind") ?? "dev").ToLowerInvariant();
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string output = Arg(args, "-outputPath") ?? Path.Combine(repoRoot, "builds", targetName, kind);
            int code = Build((BuildTarget)Enum.Parse(typeof(BuildTarget), targetName), kind, output) ? 0 : 1;
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public static bool Build(BuildTarget target, string kind, string outputDir)
        {
            if (!DataIsValid(out string dataError))
            {
                Debug.LogError("[Builder] BUILD FAIL data: " + dataError);
                return false;
            }

            var named = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
            string version = ReadVersion();
            string sha = GitShortSha();

            // Restore the project's defines and backend afterwards so a build never leaves the editor in release mode.
            string previousDefines = PlayerSettings.GetScriptingDefineSymbols(named);
            ScriptingImplementation previousBackend = PlayerSettings.GetScriptingBackend(named);
            string previousVersion = PlayerSettings.bundleVersion;
            try
            {
                return BuildWithSettings(target, kind, outputDir, named, version, sha, previousDefines);
            }
            finally
            {
                PlayerSettings.SetScriptingDefineSymbols(named, previousDefines);
                PlayerSettings.SetScriptingBackend(named, previousBackend);
                PlayerSettings.bundleVersion = previousVersion;
                AssetDatabase.SaveAssets();
            }
        }

        static bool BuildWithSettings(BuildTarget target, string kind, string outputDir, NamedBuildTarget named, string version, string sha, string previousDefines)
        {
            PlayerSettings.bundleVersion = string.IsNullOrEmpty(sha) ? version : $"{version}+{sha}";
            string define = kind == "release" ? "SS_BUILD_RELEASE" : kind == "demo" ? "SS_BUILD_DEMO" : "SS_BUILD_DEV";
            var defines = previousDefines.Split(';')
                .Where(d => d.Length > 0 && !d.StartsWith("SS_BUILD_", StringComparison.Ordinal)).ToList();
            defines.Add(define);
            PlayerSettings.SetScriptingDefineSymbols(named, string.Join(";", defines));

            bool il2cpp = kind == "release" && Il2CppToolchainAvailable(target);
            if (kind == "release" && !il2cpp) Debug.LogWarning("[Builder] IL2CPP toolchain not found; release falls back to Mono.");
            PlayerSettings.SetScriptingBackend(named, il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);

            string exeName = target == BuildTarget.StandaloneOSX ? "TheSynapticSea.app"
                : target == BuildTarget.StandaloneLinux64 ? "TheSynapticSea.x86_64" : "TheSynapticSea.exe";
            Directory.CreateDirectory(outputDir);
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[Builder] BUILD FAIL no enabled scenes in Build Settings");
                return false;
            }

            WriteStamp(kind, target, version, sha);
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(outputDir, exeName),
                target = target,
                options = kind == "dev" ? BuildOptions.Development : BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            Debug.Log($"[Builder] BUILD {(s.result == BuildResult.Succeeded ? "PASS" : "FAIL")} target={target} kind={kind} backend={(il2cpp ? "IL2CPP" : "Mono")} " +
                      $"version={PlayerSettings.bundleVersion} size_mb={s.totalSize / (1024.0 * 1024.0):0.0} errors={s.totalErrors} seconds={s.totalTime.TotalSeconds:0} out={options.locationPathName}");
            return s.result == BuildResult.Succeeded;
        }

        /// <summary>Every StreamingAssets JSON must parse with the Godot-faithful reader.</summary>
        static bool DataIsValid(out string error)
        {
            error = "";
            string root = Path.Combine(Application.streamingAssetsPath, "data");
            if (!Directory.Exists(root))
            {
                error = "StreamingAssets/data missing (run tools/sync-godot-data.ps1)";
                return false;
            }
            foreach (string f in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
            {
                try { GdJson.Parse(File.ReadAllText(f)); }
                catch (GdJson.ParseError e)
                {
                    error = f + ": " + e.Message;
                    return false;
                }
            }
            return true;
        }

        static string ReadVersion()
        {
            string path = Path.Combine(Application.streamingAssetsPath, "data", "release", "build_metadata.json");
            var doc = File.Exists(path) ? GdJson.ParseDict(File.ReadAllText(path)) : null;
            string v = doc?.GetString("version", "") ?? "";
            if (v.StartsWith("v", StringComparison.Ordinal)) v = v.Substring(1);
            return string.IsNullOrEmpty(v) ? "0.1.0" : v;
        }

        static void WriteStamp(string kind, BuildTarget target, string version, string sha)
        {
            var stamp = new GdDict
            {
                { "version", version },
                { "git_sha", sha },
                { "build_kind", kind },
                { "target", target.ToString() },
                { "built_utc", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") },
                { "unity_version", Application.unityVersion },
            };
            string path = Path.Combine(Application.streamingAssetsPath, "build_stamp.json");
            File.WriteAllText(path, GdJson.Stringify(stamp, "\t"));
            AssetDatabase.ImportAsset("Assets/StreamingAssets/build_stamp.json");
        }

        static string GitShortSha()
        {
            try
            {
                var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                };
                using (var p = Process.Start(psi))
                {
                    string outText = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(5000);
                    return p.ExitCode == 0 ? outText : "";
                }
            }
            catch
            {
                return "";
            }
        }

        static bool Il2CppToolchainAvailable(BuildTarget target)
        {
            if (target != BuildTarget.StandaloneWindows64) return true; // other targets validate themselves
            string[] roots = { @"F:\Tools\VSBuildTools", @"C:\Program Files\Microsoft Visual Studio", @"C:\Program Files (x86)\Microsoft Visual Studio" };
            return roots.Any(r => Directory.Exists(r) && Directory.GetDirectories(r, "MSVC", SearchOption.AllDirectories).Length > 0);
        }
    }
}
