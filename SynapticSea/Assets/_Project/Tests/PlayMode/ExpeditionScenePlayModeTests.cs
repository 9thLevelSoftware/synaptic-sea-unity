using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.PlayMode
{
    public class ExpeditionScenePlayModeTests
    {
        [UnityTest]
        public IEnumerator LargerExpeditionBuildsActualRoomsAndAllDoorwaysConnectOnTheNavMesh()
        {
            var previous = CoreServices.Resources; var previousLog = CoreServices.Log;
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath); CoreServices.Log = NullLog.Instance;
            CatalogRegistry.Clear(); var builder = ShipSceneBuilder.Create(name: "ExpeditionAcceptance");
            GameObject cameraRoot = null;
            try
            {
                var generator = new ShipGenerator { RichExpeditions = true }; generator.ConfigureRunContext("dead_fleet", "standard");
                var started = System.Diagnostics.Stopwatch.StartNew(); var docs = generator.GenerateFromSeed(42, 2, 0);
                Assert.NotNull(docs); Assert.IsTrue(builder.LoadFromDocuments(docs.Layout, docs.Kit, docs.GameplaySlice, true));
                foreach (var portal in builder.View.GetComponentsInChildren<AuthoredPortalRuntime>()) portal.RestorePersistentState(true, true);
                Physics.SyncTransforms(); var nav = ShipNavMesh.Build(builder.View.gameObject); Assert.IsTrue(nav.HasNavMesh);
                yield return null; yield return null;
                var filter = new NavMeshQueryFilter { agentTypeID = ShipNavMesh.AgentTypeId, areaMask = NavMesh.AllAreas };
                Assert.IsTrue(NavMesh.SamplePosition(builder.View.GetStartPoseWorld().position, out var start, 2f, filter));
                int checkedCells = 0;
                foreach (var floor in builder.View.Modules.Where(m => m.layer == "floor"))
                {
                    Assert.IsTrue(NavMesh.SamplePosition(floor.transform.position, out var hit, 1.5f, filter), floor.moduleKey);
                    var path = new NavMeshPath(); Assert.IsTrue(NavMesh.CalculatePath(start.position, hit.position, filter, path));
                    Assert.AreEqual(NavMeshPathStatus.PathComplete, path.status, "physical route to " + floor.moduleKey); checkedCells++;
                }
                Assert.Greater(checkedCells, 65, "meaningfully larger expedition footprint");
                Debug.Log($"[ExpeditionAcceptance] rooms={docs.Layout.GetArrayOrEmpty("rooms").Count} floors={checkedCells} renderers={builder.View.GetComponentsInChildren<Renderer>().Length} generation_scene_nav_ms={started.ElapsedMilliseconds}");
                cameraRoot = new GameObject("ExpeditionReviewCamera"); var camera = cameraRoot.AddComponent<Camera>();
                camera.orthographic = true; camera.clearFlags = CameraClearFlags.SolidColor; camera.cullingMask &= ~(1 << PhysicsLayers.Ceiling); camera.backgroundColor = AtmosphereApplier.BackgroundColor;
                var floors = builder.View.Modules.Where(m => m.layer == "floor").ToArray(); var bounds = new Bounds(floors[0].transform.position, Vector3.zero);
                foreach (var floor in floors) bounds.Encapsulate(floor.transform.position);
                camera.transform.position = bounds.center + new Vector3(40, 65, -40); camera.transform.LookAt(bounds.center); camera.orthographicSize = Mathf.Max(bounds.size.x, bounds.size.z) * 0.65f;
                Capture(camera, "expedition-overview-seed42.png");
                camera.orthographicSize = 7; camera.transform.position = start.position + new Vector3(16, 13.064f, -16); camera.transform.LookAt(start.position);
                var reveal = new InteriorOcclusion(); reveal.Update(camera, start.position, null, 0.2f);
                Capture(camera, "expedition-dock-seed42.png"); reveal.RestoreAll();
                if (System.Environment.GetCommandLineArgs().Contains("-profileExpeditionFrames"))
                {
                    yield return ProfileFrames(camera, "indoor", builder.View.GetComponentsInChildren<Renderer>().Length, start.position);
                    camera.transform.position = bounds.center + new Vector3(40, 65, -40); camera.transform.LookAt(bounds.center);
                    camera.orthographicSize = Mathf.Max(bounds.size.x, bounds.size.z) * 0.65f;
                    yield return ProfileFrames(camera, "overview", builder.View.GetComponentsInChildren<Renderer>().Length, bounds.center);
                }
            }
            finally
            {
                Object.DestroyImmediate(builder.View.gameObject); if (cameraRoot != null) Object.DestroyImmediate(cameraRoot);
                CatalogRegistry.Clear(); CoreServices.Resources = previous; CoreServices.Log = previousLog;
            }
        }

        static IEnumerator ProfileFrames(Camera camera, string view, int rendererCount, Vector3 anchor)
        {
            int oldVsync = QualitySettings.vSyncCount, oldRate = Application.targetFrameRate;
            var target = new RenderTexture(2048, 1224, 24); var elapsed = new System.Collections.Generic.List<double>();
            var renderCpu = new System.Collections.Generic.List<double>(); var gpu = new System.Collections.Generic.List<double>();
            var occlusionCpu = new System.Collections.Generic.List<double>(); var reveal = new InteriorOcclusion(); reveal.RefreshModules();
            var timings = new FrameTiming[1]; var clock = System.Diagnostics.Stopwatch.StartNew();
            bool oldEnabled = camera.enabled; camera.enabled = false; camera.targetTexture = target;
            try
            {
                QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
                for (int i = 0; i < 150; i++)
                {
                    clock.Restart(); reveal.Update(camera, anchor, null, 1f / 60f); double occlusion = clock.Elapsed.TotalMilliseconds;
                    camera.Render(); double submission = clock.Elapsed.TotalMilliseconds - occlusion;
                    FrameTimingManager.CaptureFrameTimings();
                    // A one-pixel readback fences the preceding render without copying the entire screenshot.
                    // CPU submission alone can substantially understate an asynchronous GPU's cost.
                    if (SystemInfo.supportsAsyncGPUReadback)
                    {
                        var completion = UnityEngine.Rendering.AsyncGPUReadback.Request(target, 0, 0, 1, 0, 1, 0, 1);
                        while (!completion.done && clock.Elapsed.TotalSeconds < 5) yield return null;
                        Assert.IsTrue(completion.done, "bounded GPU completion wait"); Assert.IsFalse(completion.hasError);
                    }
                    else yield return null;
                    if (i < 30) continue;
                    elapsed.Add(clock.Elapsed.TotalMilliseconds); renderCpu.Add(submission); occlusionCpu.Add(occlusion);
                    if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0) gpu.Add(timings[0].gpuFrameTime);
                }
                elapsed.Sort(); renderCpu.Sort(); gpu.Sort(); occlusionCpu.Sort();
                string metrics = $"[ExpeditionFrameProfile] view={view} samples={elapsed.Count} resolution=2048x1224 renderers={rendererCount} "
                    + $"frame_median_ms={elapsed[elapsed.Count / 2]:F2} frame_p95_ms={elapsed[(int)(elapsed.Count * .95)]:F2} "
                    + $"render_submission_median_ms={renderCpu[renderCpu.Count / 2]:F2} gpu_median_ms={(gpu.Count == 0 ? "unavailable" : gpu[gpu.Count / 2].ToString("F2"))} "
                    + $"occlusion_median_ms={occlusionCpu[occlusionCpu.Count / 2]:F2} gpu_completion_fenced={SystemInfo.supportsAsyncGPUReadback} device={SystemInfo.graphicsDeviceName} api={SystemInfo.graphicsDeviceType}; camera/occlusion fixture, no live HUD/AI";
                Debug.Log(metrics);
            }
            finally { reveal.RestoreAll(); QualitySettings.vSyncCount = oldVsync; Application.targetFrameRate = oldRate; camera.targetTexture = null; camera.enabled = oldEnabled; Object.Destroy(target); }
        }

        static void Capture(Camera camera, string name)
        {
            Assert.AreNotEqual(UnityEngine.Rendering.GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType);
            var target = new RenderTexture(2048, 1224, 24); var pixels = new Texture2D(2048, 1224, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 2048, 1224), 0, 0); pixels.Apply();
                var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/screenshots")); Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, name), pixels.EncodeToPNG());
            }
            finally { camera.targetTexture = null; RenderTexture.active = previous; Object.Destroy(target); Object.Destroy(pixels); }
        }
    }
}
