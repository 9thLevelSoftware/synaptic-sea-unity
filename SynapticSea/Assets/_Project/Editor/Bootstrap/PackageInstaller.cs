using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace SynapticSea.EditorTools.Bootstrap
{
    /// <summary>
    /// Installs and removes the project's package set through the PackageManager Client API.
    /// Run headless WITHOUT -quit; the method calls EditorApplication.Exit itself.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Bootstrap.PackageInstaller.Install -logFile -
    /// </summary>
    public static class PackageInstaller
    {
        static readonly string[] PackagesToAdd =
        {
            "com.unity.cloud.gltfast",          // GLB import (structural kit + props)
            "com.unity.nuget.newtonsoft-json",  // JSON tokenizer for the Godot-compatible reader
        };

        static readonly string[] PackagesToRemove =
        {
            "com.unity.collab-proxy",   // Unity Version Control is off; the repo uses git
            "com.unity.ide.rider",      // no Rider on this machine; VS Code uses ide.visualstudio
            "com.unity.visualscripting",
        };

        const double TimeoutSeconds = 900;
        static AddAndRemoveRequest _request;
        static double _deadline;

        [MenuItem("Synaptic Sea/Bootstrap/Install Packages")]
        public static void Install()
        {
            Debug.Log($"[PackageInstaller] Adding: {string.Join(", ", PackagesToAdd)}; removing: {string.Join(", ", PackagesToRemove)}");
            _request = Client.AddAndRemove(packagesToAdd: PackagesToAdd, packagesToRemove: PackagesToRemove);
            _deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (_request == null) return;
            if (!_request.IsCompleted)
            {
                if (EditorApplication.timeSinceStartup > _deadline)
                {
                    EditorApplication.update -= Poll;
                    Debug.LogError("[PackageInstaller] Timed out waiting for UPM.");
                    ExitIfBatch(2);
                }
                return;
            }

            EditorApplication.update -= Poll;
            if (_request.Status == StatusCode.Success)
            {
                Debug.Log($"[PackageInstaller] Resolved: {string.Join(", ", _request.Result.Select(p => $"{p.name}@{p.version}"))}");
                ExitIfBatch(0);
            }
            else
            {
                Debug.LogError($"[PackageInstaller] Failed: {_request.Error?.message}");
                ExitIfBatch(1);
            }
        }

        static void ExitIfBatch(int code)
        {
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }
}
