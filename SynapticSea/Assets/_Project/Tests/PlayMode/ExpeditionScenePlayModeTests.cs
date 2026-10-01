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
        public IEnumerator CargoDockHasPhysicalRouteFromBoatToHost()
        {
            var previous=CoreServices.Resources; CoreServices.Resources=new FileSystemResourceReader(Application.streamingAssetsPath);
            var root=new GameObject("CargoDockFixture"); var host=ShipSceneBuilder.Create(root.transform,"CargoHost");
            var scenes=new UnityShipSceneHost(root.transform); var geometry=new DockedShipGeometry();
            try
            {
                var generator=new ShipGenerator {RichExpeditions=true, ExpeditionProfile=PurposefulExpedition.Profile}; generator.ConfigureRunContext("dead_fleet","standard");
                var docs=generator.GenerateFromSeed(367936917,1,0);
                Assert.IsTrue(host.LoadFromDocuments(docs.Layout,docs.Kit,docs.GameplaySlice,true));
                var boat=scenes.BuildLifeboatScene(LifeBoatBuilder.Build("dead_fleet")); scenes.AttachShipRoot(boat);
                boat.Transform=SynapticSea.Core.Systems.DockingManager.ComputeMobileTransform(
                    SynapticSea.Core.Systems.DockPorts.ForDerelict(docs.Layout),SynapticSea.Core.Systems.DockPorts.ForLifeboat(LifeBoatBuilder.BuildLayout("dead_fleet")));
                foreach(var portal in root.GetComponentsInChildren<AuthoredPortalRuntime>()) portal.RestorePersistentState(true,true);
                geometry.Reconcile(new ShipLoaderNode(host.View),(SceneShipRoot)boat);
                yield return new WaitForSecondsRealtime(.5f);
                Assert.Greater(geometry.SuppressedColliderCount,0,"the supported overlapping entrance has an instance-local opening");
                var filter=new NavMeshQueryFilter {agentTypeID=ShipNavMesh.AgentTypeId,areaMask=NavMesh.AllAreas};
                var start=Frame.ToUnity(boat.GlobalTransform*new Vec3(4,.2,0));
                Assert.IsTrue(NavMesh.SamplePosition(start,out var a,2f,filter));
                Assert.IsTrue(NavMesh.SamplePosition(host.View.GetStartPoseWorld().position,out var b,2f,filter));
                var path=new NavMeshPath(); Assert.IsTrue(NavMesh.CalculatePath(a.position,b.position,filter,path));
                Assert.AreEqual(NavMeshPathStatus.PathComplete,path.status,"cargo dock must physically connect the boat to the host");
            }
            finally {geometry.Reconcile(null,null); Object.DestroyImmediate(root); CoreServices.Resources=previous; CatalogRegistry.Clear();}
        }

        [UnityTest]
        public IEnumerator DockedInteractionRechecksAcceptedAnchorWhenGeometrySettles()
        {
            var host = new GameObject("AnchorHost"); var mobile = new GameObject("AnchorMobile");
            var geometry = new DockedShipGeometry();
            try
            {
                foreach (var root in new[] { host, mobile })
                {
                    var floor = new GameObject("Floor") { layer = PhysicsLayers.Structure };
                    floor.transform.SetParent(root.transform, false); floor.transform.localPosition = new Vector3(0,-.125f,0);
                    var module = floor.AddComponent<StructuralModule>(); module.layer = "floor"; module.moduleId = "floor_1x1";
                    floor.AddComponent<BoxCollider>().size = new Vector3(4,.25f,4);
                }
                var mobileRoot = new SceneShipRoot(mobile);
                geometry.Reconcile(new SceneShipRoot(host), mobileRoot);
                var point = new SynapticSea.Core.Session.RepairPoint { Parent = mobileRoot, LocalPosition = new Vec3(0,.175,0) };
                var original = point.LocalPosition;
                geometry.NormalizeInteractions(new[] { point });
                Assert.AreEqual(original, point.LocalPosition, "initial clear anchor stays authored");
                var blocker = new GameObject("SettledBoundary") { layer = PhysicsLayers.Structure };
                blocker.transform.SetParent(host.transform, false); blocker.transform.localPosition = new Vector3(0,1.5f,0);
                blocker.AddComponent<BoxCollider>().size = new Vector3(.2f,3,4);
                Physics.SyncTransforms(); yield return new WaitForSecondsRealtime(.6f);
                geometry.NormalizeInteractions(new[] { point });
                Assert.AreNotEqual(original, point.LocalPosition, "a previously accepted anchor is reconsidered");
                Assert.IsTrue(SpawnClearance.IsClear(Frame.ToUnity(point.GlobalPosition)));
                Assert.Less((point.LocalPosition-original).Length(), point.InteractionRadius, "repositioning respects real reach");
                geometry.Reconcile(null,null); Assert.AreEqual(original, point.LocalPosition, "undocking restores the original anchor");
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(mobile); }
        }

        [UnityTest]
        public IEnumerator PurposefulFamiliesBuildImportedInteriorsAndReachableWorkAnchors() => ReviewComposedScenes(PurposefulExpedition.Profile, "purposeful");

        [UnityTest]
        public IEnumerator ConstrainedCompositionsBuildImportedInteriorsAndReachableWorkAnchors() => ReviewComposedScenes(ConstrainedExpedition.Profile, "constrained");

        IEnumerator ReviewComposedScenes(string profile, string capturePrefix)
        {
            var previous = CoreServices.Resources; var previousLog = CoreServices.Log;
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath); CoreServices.Log = NullLog.Instance;
            GameObject cameraRoot = null; ShipSceneBuilder builder = null;
            try
            {
                foreach (int seed in new[] { 17, 42, 777 })
                {
                    CatalogRegistry.Clear(); builder = ShipSceneBuilder.Create(name: "PurposefulAcceptance");
                    var generator = new ShipGenerator { RichExpeditions = true, ExpeditionProfile=profile }; generator.ConfigureRunContext("dead_fleet", "standard");
                    var docs = generator.GenerateFromSeed(seed, seed == 17 ? 1 : 2, 0);
                    Assert.IsTrue(builder.LoadFromDocuments(docs.Layout, docs.Kit, docs.GameplaySlice, true));
                    foreach (var portal in builder.View.GetComponentsInChildren<AuthoredPortalRuntime>()) portal.RestorePersistentState(true,true);
                    Physics.SyncTransforms(); Assert.IsTrue(ShipNavMesh.Build(builder.View.gameObject).HasNavMesh); yield return null;
                    var filter = new NavMeshQueryFilter { agentTypeID = ShipNavMesh.AgentTypeId, areaMask = NavMesh.AllAreas };
                    Assert.IsTrue(NavMesh.SamplePosition(builder.View.GetStartPoseWorld().position,out var start,2f,filter));
                    foreach (var floor in builder.View.Modules.Where(m => m.layer == "floor")) CheckPath(start.position,floor.transform.position,filter,floor.moduleKey);
                    foreach (string category in new[] { "objectives", "loot_containers" }) foreach (GdDict anchor in docs.GameplaySlice.GetArrayOrEmpty(category))
                    {
                        var cell = anchor.GetArrayOrEmpty("approach_cell"); if (cell.Count < 2) continue;
                        CheckPath(start.position,Frame.ToUnity(new Vec3(V.F64(cell[0])*4,.12,V.F64(cell[1])*4)),filter,"work/loot " + anchor.GetString("id"));
                    }
                    var interiors = builder.View.transform.Find("StructuralRoot/PurposefulInteriors");
                    if (interiors == null) interiors = builder.View.GetComponentsInChildren<Transform>().First(t => t.name == "PurposefulInteriors");
                    Assert.AreEqual(docs.Layout.GetArrayOrEmpty("purposeful_interiors").Count, interiors.childCount, "every planned local prop is available");
                    Assert.IsEmpty(interiors.GetComponentsInChildren<Collider>(), "preserve visual-only source prop contracts");
                    foreach (var renderer in interiors.GetComponentsInChildren<Renderer>()) Assert.IsTrue(renderer.sharedMaterials.All(m => m != null),renderer.name);
                    foreach(GdDict item in docs.Layout.GetArrayOrEmpty("purposeful_interiors"))
                    {
                        var visual=interiors.Find(item.GetString("id")); var renderers=visual.GetComponentsInChildren<Renderer>();
                        Assert.IsNotEmpty(renderers); var visualBounds=renderers[0].bounds;
                        foreach(var renderer in renderers) visualBounds.Encapsulate(renderer.bounds);
                        var cell=item.GetArrayOrEmpty("cell"); var center=Frame.ToUnity(new Vec3(V.F64(cell[0])*4,0,V.F64(cell[1])*4));
                        Assert.Less(Mathf.Abs(visualBounds.center.x-center.x)+visualBounds.extents.x,1.95f,item.GetString("id")+" must clear its cell's wall line");
                        Assert.Less(Mathf.Abs(visualBounds.center.z-center.z)+visualBounds.extents.z,1.95f,item.GetString("id")+" must clear its cell's wall line");
                    }
                    Debug.Log($"[PurposefulAcceptance] seed={seed} family={docs.Layout.GetString("topology_family")} rooms={docs.Layout.GetArrayOrEmpty("rooms").Count} interiors={interiors.childCount}");
                    cameraRoot = new GameObject("PurposefulReview"); var camera = cameraRoot.AddComponent<Camera>(); camera.orthographic = true;
                    camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = AtmosphereApplier.BackgroundColor; camera.cullingMask &= ~(1 << PhysicsLayers.Ceiling);
                    var floors = builder.View.Modules.Where(m => m.layer == "floor").ToArray(); var bounds = new Bounds(floors[0].transform.position,Vector3.zero);
                    foreach(var floor in floors) bounds.Encapsulate(floor.transform.position);
                    camera.transform.position = bounds.center + new Vector3(40,65,-40); camera.transform.LookAt(bounds.center); camera.orthographicSize = Mathf.Max(bounds.size.x,bounds.size.z)*.65f;
                    Capture(camera,capturePrefix+"-overview-"+seed+".png");
                    foreach(string role in new[] { "cargo", "medical", "crew_quarters", "engineering" })
                    {
                        var room = docs.Layout.GetArrayOrEmpty("rooms").Cast<GdDict>().FirstOrDefault(r => r.GetString("room_role") == role); if(room == null) continue;
                        var cell = LayoutSerializer.ParseSlotCell(room.GetArrayOrEmpty("cells")[0]);
                        Vector3 anchor = Frame.ToUnity(new Vec3(V.F64(cell[0])*4,.12,V.F64(cell[1])*4));
                        camera.transform.position = anchor + new Vector3(16,13.064f,-16); camera.transform.LookAt(anchor); camera.orthographicSize = 7;
                        var reveal = new InteriorOcclusion(); reveal.Update(camera,anchor,null,.2f); Capture(camera,capturePrefix+"-"+role+"-"+seed+".png"); reveal.RestoreAll();
                    }
                    Object.DestroyImmediate(builder.View.gameObject); builder = null; Object.DestroyImmediate(cameraRoot); cameraRoot = null;
                }
            }
            finally { if(builder != null) Object.DestroyImmediate(builder.View.gameObject); if(cameraRoot != null) Object.DestroyImmediate(cameraRoot); CatalogRegistry.Clear(); CoreServices.Resources=previous; CoreServices.Log=previousLog; }
        }

        static void CheckPath(Vector3 start,Vector3 destination,NavMeshQueryFilter filter,string label)
        {
            Assert.IsTrue(NavMesh.SamplePosition(destination,out var hit,1.5f,filter),label); var path = new NavMeshPath();
            Assert.IsTrue(NavMesh.CalculatePath(start,hit.position,filter,path),label); Assert.AreEqual(NavMeshPathStatus.PathComplete,path.status,label);
        }

        [UnityTest]
        public IEnumerator LargerExpeditionBuildsActualRoomsAndAllDoorwaysConnectOnTheNavMesh()
        {
            var previous = CoreServices.Resources; var previousLog = CoreServices.Log;
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath); CoreServices.Log = NullLog.Instance;
            CatalogRegistry.Clear(); var builder = ShipSceneBuilder.Create(name: "ExpeditionAcceptance");
            GameObject cameraRoot = null;
            try
            {
                var generator = new ShipGenerator { RichExpeditions = true, ExpeditionProfile=ExpeditionLayoutEngine.Profile }; generator.ConfigureRunContext("dead_fleet", "standard");
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
