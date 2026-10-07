using System;
using System.IO;
using NUnit.Framework.Interfaces;
using UnityEngine;
using UnityEngine.TestRunner;

[assembly: TestRunCallback(typeof(SynapticSea.Tests.PlayMode.QuietPlayerResultCallback))]

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>Supported NUnit callback for an independently launched GPU test player; never included in game builds.</summary>
    public sealed class QuietPlayerResultCallback : ITestRunCallback
    {
        static string ResultPath
        {
            get
            {
                if (Application.isEditor) return null;
                var args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-quietTestResults");
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
        }
        public void RunStarted(ITest test) { if (ResultPath != null) Debug.Log("[QuietPlayerTests] started count=" + test.TestCaseCount); }
        public void TestStarted(ITest test) { if (ResultPath != null) Debug.Log("[QuietPlayerTests] " + test.FullName); }
        public void TestFinished(ITestResult result) { }
        public void RunFinished(ITestResult result)
        {
            string path = ResultPath;
            if (path == null) return;
            File.WriteAllText(path, result.ToXml(true).OuterXml);
            Debug.Log($"[QuietPlayerTests] finished passed={result.PassCount} failed={result.FailCount} skipped={result.SkipCount}");
            Application.Quit(result.FailCount == 0 ? 0 : 1);
        }
    }
}
