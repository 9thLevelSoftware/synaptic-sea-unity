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
        static string _playerPath, _logPath;
        public BuildPlayerOptions ModifyOptions(BuildPlayerOptions options)
        {
            if (!Environment.GetCommandLineArgs().Contains("-quietWindowsTestPlayer") || options.target != BuildTarget.StandaloneWindows64) return options;
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            _playerPath = Path.Combine(root, "builds/StandaloneWindows64/camera-test-player", Path.GetFileName(options.locationPathName));
            _logPath = Path.Combine(root, "builds/logs/camera-windows-test-player.log");
            Directory.CreateDirectory(Path.GetDirectoryName(_playerPath));
            options.options &= ~BuildOptions.AutoRunPlayer;
            options.locationPathName = _playerPath;
            return options;
        }

        public void Cleanup()
        {
            if (string.IsNullOrEmpty(_playerPath)) return;
            var path = _playerPath; _playerPath = null;
            Process.Start(new ProcessStartInfo(path, "-batchmode -screen-fullscreen 0 -screen-width 2048 -screen-height 1224 -quietTestResults \"" + Path.Combine(Path.GetDirectoryName(_logPath), "camera-final-windows-local.xml") + "\" -logFile \"" + _logPath + "\"")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }
}
#endif
