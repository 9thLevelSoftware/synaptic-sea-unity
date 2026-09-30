#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.TestTools;
using UnityEngine;
using UnityEngine.TestTools;

[assembly: TestPlayerBuildModifier(typeof(SynapticSea.Tests.PlayMode.QuietWindowsTestPlayer))]
[assembly: PostBuildCleanup(typeof(SynapticSea.Tests.PlayMode.QuietWindowsTestPlayer))]

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>Opt-in supported test-runner launch override: GPU Windows tests without taking the user's foreground.</summary>
    public sealed class QuietWindowsTestPlayer : ITestPlayerBuildModifier, IPostBuildCleanup
    {
        static string _playerPath, _logPath, _resultPath;
        public BuildPlayerOptions ModifyOptions(BuildPlayerOptions options)
        {
            if (!Environment.GetCommandLineArgs().Contains("-quietWindowsTestPlayer") || options.target != BuildTarget.StandaloneWindows64) return options;
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-quietTestRunId");
            string id = index >= 0 && index + 1 < args.Length ? args[index + 1] : "camera";
            if (id.Length == 0 || id.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) throw new ArgumentException("Invalid quiet test run id");
            _playerPath = Path.Combine(root, "builds/StandaloneWindows64/" + id + "-test-player", Path.GetFileName(options.locationPathName));
            _logPath = Path.Combine(root, "builds/logs/" + id + "-windows-test-player.log");
            _resultPath = Path.Combine(root, "builds/logs/" + id + "-windows-local.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(_playerPath));
            options.options &= ~BuildOptions.AutoRunPlayer;
            options.locationPathName = _playerPath;
            return options;
        }

        public void Cleanup()
        {
            if (string.IsNullOrEmpty(_playerPath)) return;
            var path = _playerPath; _playerPath = null;
            string profiling = Environment.GetCommandLineArgs().Contains("-profileExpeditionFrames") ? " -profileExpeditionFrames" : "";
            Process.Start(new ProcessStartInfo(path, "-batchmode -screen-fullscreen 0 -screen-width 2048 -screen-height 1224 -quietTestResults \"" + _resultPath + "\" -logFile \"" + _logPath + "\"" + profiling)
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }
}
#endif
