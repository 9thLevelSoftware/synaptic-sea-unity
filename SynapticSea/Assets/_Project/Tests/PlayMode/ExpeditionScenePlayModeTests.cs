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
            }
            finally
            {
                Object.DestroyImmediate(builder.View.gameObject); if (cameraRoot != null) Object.DestroyImmediate(cameraRoot);
                CatalogRegistry.Clear(); CoreServices.Resources = previous; CoreServices.Log = previousLog;
            }
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
