using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.App;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.Game;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>
    /// Diagnostic physical gate, not an earned survival journey: reviewed frozen documents are loaded as a
    /// diagnostic home through the existing filesystem-backed explicit layout override. Spawn-room movement uses the real player.
    /// Ladder endpoint probes place the player at an authored supported landing, then use ordinary input and
    /// save/load. Those probes do not claim a walked route from entry, continuous climbing or interdeck AI.
    /// No raw export edits, free tools, opened portals, vitality overrides or ordinary source activation.
    /// </summary>
    public class FrozenProceduralPhysicalPlayModeTests
    {
        IStorage _oldStorage; IResourceReader _oldResources; ILog _oldLog;
        PlayableBootstrap _boot; RunSession _session; string _fixtureId, _diagnosticDirectory;
        [SetUp] public void Setup()
        {
            _oldStorage = CoreServices.UserStorage; _oldResources = CoreServices.Resources; _oldLog = CoreServices.Log;
            AppServices.StorageOverride = new MemoryStorage(); RunLaunchRequest.Pending = null; RunReturnInfo.Clear();
        }
        [TearDown] public void Teardown()
        {
            foreach (RunSessionHost host in Object.FindObjectsByType<RunSessionHost>()) Object.DestroyImmediate(host.gameObject);
            foreach (PlayableBootstrap boot in Object.FindObjectsByType<PlayableBootstrap>()) Object.DestroyImmediate(boot.gameObject);
            foreach (TitleScreen title in Object.FindObjectsByType<TitleScreen>()) Object.DestroyImmediate(title.gameObject);
            AppServices.Shutdown(); AppServices.StorageOverride = null; RunLaunchRequest.Pending = null; RunReturnInfo.Clear();
            HallucinationFx.SetGlobalIntensity(0); CatalogRegistry.Clear(); GameplayPropFactory.ClearCache();
            CoreServices.UserStorage = _oldStorage; CoreServices.Resources = _oldResources; CoreServices.Log = _oldLog;
            if (_diagnosticDirectory != null && Directory.Exists(_diagnosticDirectory)) Directory.Delete(_diagnosticDirectory, true);
            _diagnosticDirectory = null;
        }
        [UnityTest] public IEnumerator Seed42ShuttleIntact() => Probe(42, 0, 0);
        [UnityTest] public IEnumerator Seed42ShuttleDamaged() => Probe(42, 0, 2);
        [UnityTest] public IEnumerator Seed42FreighterIntact() => Probe(42, 2, 0);
        [UnityTest] public IEnumerator Seed42FreighterDamaged() => Probe(42, 2, 2);
        [UnityTest] public IEnumerator Seed777ShuttleIntact() => Probe(777, 0, 0);
        [UnityTest] public IEnumerator Seed777ShuttleDamaged() => Probe(777, 0, 2);
        [UnityTest] public IEnumerator Seed777FreighterIntact() => Probe(777, 2, 0);
        [UnityTest] public IEnumerator Seed777FreighterDamaged() => Probe(777, 2, 2);

        IEnumerator Probe(long seed, long size, long condition)
        {
            AppServices.Ensure();
            var reader = new FileSystemResourceReader(Application.streamingAssetsPath);
            CoreServices.Resources = reader; CatalogRegistry.Clear(); EncounterInjector.ClearTableCache();
            var source = new FrozenDerelictLayoutSource(reader);
            var generator = new ShipGenerator { DerelictSource = source, EnableReviewedFrozenVersion4 = true };
            ShipDocuments docs = generator.GenerateFromSeed(seed, size, condition); Assert.IsNotNull(docs);
            _fixtureId = docs.Layout.GetDictOrEmpty("worldgen_fixture").GetString("fixture_id");
            string rawLayout = reader.ReadText(FrozenDerelictLayoutSource.Root + _fixtureId + "/layout.json");
            string rawSlice = reader.ReadText(FrozenDerelictLayoutSource.Root + _fixtureId + "/gameplay_slice.json");
            string layoutHash = FrozenDerelictLayoutSource.Hash(rawLayout), sliceHash = FrozenDerelictLayoutSource.Hash(rawSlice);
            // The Unity scene loader intentionally resolves explicit layout paths on disk; Core catalog
            // reads still use IResourceReader. Give both readers the same test-owned diagnostic documents.
            _diagnosticDirectory = Path.Combine(Path.GetTempPath(), "synaptic-frozen-physical-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_diagnosticDirectory);
            string layoutPath = Path.Combine(_diagnosticDirectory, "layout.json").Replace('\\', '/');
            string gameplayPath = Path.Combine(_diagnosticDirectory, "gameplay_slice.json").Replace('\\', '/');
            File.WriteAllText(layoutPath, GdJson.Stringify(docs.Layout), new System.Text.UTF8Encoding(false));
            File.WriteAllText(gameplayPath, GdJson.Stringify(docs.GameplaySlice), new System.Text.UTF8Encoding(false));
            // Keep the actual filesystem reader: structural kit discovery requires its directory contract.
            // Both loader and reader resolve this explicitly computed resource alias to the same owned temp file.
            // No untrusted path input and no files are added to StreamingAssets or the reviewed export folder.
            string layoutResourcePath = "res://" + Path.GetRelativePath(Application.streamingAssetsPath, layoutPath).Replace('\\', '/');
            Assert.IsTrue(reader.Exists(layoutResourcePath));
            Assert.AreEqual(layoutPath, Path.GetFullPath(ShipSceneBuilder.ResolvePath(layoutResourcePath)));
            CoreServices.Resources = reader; CatalogRegistry.Clear();
            RunLaunchRequest.Pending = new RunLaunchRequest { LayoutOverridePath = layoutResourcePath, Seed = seed, BiomeId = RunLaunchRequest.DefaultBiomeId };
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName); yield return null;
            float deadline = Time.realtimeSinceStartup + 60;
            while (Time.realtimeSinceStartup < deadline && (PlayableBootstrap.Current == null || !PlayableBootstrap.Current.IsBooted))
            {
                if (PlayableBootstrap.Current != null && PlayableBootstrap.Current.IsFailed) break;
                yield return null;
            }
            _boot = PlayableBootstrap.Current; Assert.IsNotNull(_boot); Assert.IsTrue(_boot.IsBooted, _boot.BootFailure);
            _session = _boot.Session; yield return WaitForGrounded(0, "initial spawn");
            AssertStanding(0, "initial real prefab spawn");
            var player = _boot.Host.SceneState.Player; Vector3 start = player.transform.position;
            Vector3 destination = FindNearbyStandingPoint(start, 1f);
            yield return WalkLocal(destination, .2f);
            Assert.Greater(Vector3.Distance(start, player.transform.position), .5f, "scripted movement traverses physical floor");
            AssertStanding(0, "after physical spawn-room walk");
            Assert.Greater(Object.FindObjectsByType<StructuralModule>(FindObjectsSortMode.None).Length, 0, "real kit wrappers loaded");
            yield return Capture("spawn-room.png");
            _session.RefreshDeckTransitions();
            Assert.AreEqual(size == 2 ? 2 : 0, _session.DeckTransitions.Count);
            if (size == 2)
            {
                var up = _session.DeckTransitions.First(d => d.DestinationDeck == 1);
                RecordEntryRoute(up);
                yield return EndpointTransferProbe(up, "up");
                Vector3 upperSavedPose = player.transform.position; string connectionId = up.ConnectionId;
                Assert.IsTrue(_session.RequestSave(), "upper supported pose saved to isolated MemoryStorage");
                Assert.IsTrue(_session.RequestLoad(), "upper pose restored through production load");
                yield return WaitForGrounded(1, "upper save/load"); AssertStanding(1, "upper save/load");
                Assert.Less(Vector3.Distance(upperSavedPose, _boot.Host.SceneState.Player.transform.position), .35f, "exact supported upper pose restored, not arbitrary same-deck fallback");
                yield return Capture("upper-restored.png");
                _session.RefreshDeckTransitions();
                var down = _session.DeckTransitions.First(d => d.DestinationDeck == 0);
                Assert.AreEqual(connectionId, down.ConnectionId, "raw ladder identity persists after upper restore");
                yield return EndpointTransferProbe(down, "down");
                Vector3 lowerSavedPose = _boot.Host.SceneState.Player.transform.position;
                Assert.IsTrue(_session.RequestSave()); Assert.IsTrue(_session.RequestLoad());
                yield return WaitForGrounded(0, "lower save/load"); AssertStanding(0, "lower save/load");
                Assert.Less(Vector3.Distance(lowerSavedPose, _boot.Host.SceneState.Player.transform.position), .35f, "exact supported lower pose restored");
                _session.RefreshDeckTransitions();
                Assert.IsTrue(_session.DeckTransitions.All(d => d.ConnectionId == connectionId), "raw ladder identity persists after lower restore");
                yield return Capture("lower-restored.png");
            }
            Assert.AreEqual(layoutHash, FrozenDerelictLayoutSource.Hash(reader.ReadText(FrozenDerelictLayoutSource.Root + _fixtureId + "/layout.json")));
            Assert.AreEqual(sliceHash, FrozenDerelictLayoutSource.Hash(reader.ReadText(FrozenDerelictLayoutSource.Root + _fixtureId + "/gameplay_slice.json")));
            Debug.Log("[FrozenPhysicalGate] " + _fixtureId + " prefab_probe_complete=true walked_entry_to_ladder_acceptance=false continuous_climb=false interdeck_ai=false raw_hashes_unchanged=true");
        }
        void RecordEntryRoute(DeckTransition landing)
        {
            var player = _boot.Host.SceneState.Player; var path = new NavMeshPath();
            bool found = NavMesh.CalculatePath(player.transform.position, Frame.ToUnity(landing.GlobalPosition), new NavMeshQueryFilter { agentTypeID = ShipNavMesh.AgentTypeId, areaMask = NavMesh.AllAreas }, path);
            var locked = Object.FindObjectsByType<AuthoredPortalRuntime>(FindObjectsSortMode.None).Where(p => p.portalKind == "LOCKED" && !p.isOpen).Select(p => p.portalId).ToArray();
            Debug.Log("[FrozenEntryRouteObservation] fixture=" + _fixtureId + " target=" + landing.ConnectionId + " static_nav_path_found=" + found + " status=" + path.status + " closed_locked_portals=" + string.Join(",", locked) + " route_not_walked=true");
        }
        IEnumerator EndpointTransferProbe(DeckTransition landing, string direction)
        {
            Assert.AreEqual("ladder", landing.ConnectionType); Assert.IsNotEmpty(landing.ConnectionId);
            Vec3? clear = _session.Deps.ResolveDeckLanding(landing.GlobalPosition, landing.Parent);
            Assert.IsTrue(clear.HasValue, "raw " + landing.ConnectionId + " " + direction + " endpoint has no physically supported capsule landing; raw export must not be rewritten");
            // Explicit isolated endpoint probe; this placement is not evidence of an entry-to-ladder walked route.
            _boot.Host.SceneState.TeleportPlayer(clear.Value); yield return WaitForGrounded(landing.SourceDeck, "isolated source endpoint " + direction);
            AssertStanding(landing.SourceDeck, "isolated authored endpoint " + direction);
            Assert.IsTrue(landing.InReach(_boot.Host.SceneState.Player.GodotPosition), "supported source remains in real landing range");
            Assert.IsTrue(_boot.Host.InteractableViews.TryGetValue(landing, out InteractableView view));
            Assert.AreEqual("Ladder transfer to deck " + landing.DestinationDeck, view.PromptText);
            // Capture advances actual rendered HUD frames before focus is inspected, and preserves evidence
            // even when an ordinary higher-priority target or absent focus blocks this endpoint.
            yield return Capture(direction + "-prompt.png");
            IAuthoredPortal door = _session.FocusedAuthoredPortal(_boot.Host.SceneState.Player.GodotPosition);
            if (door != null && door.PortalKind == "DOOR" && !door.IsOpen)
            {
                // Established two-press interaction: a single currently focused ordinary closed door precedes
                // the ladder. Preserve the initial HUD capture; do not unlock, grant flags or open other portals.
                string doorId = door.PortalId;
                Assert.IsFalse(door.IsExterior, "endpoint probe does not leave the ship through an exterior door");
                _boot.Host.SceneState.Player.RequestInteract();
                for (int i = 0; i < 10; i++) yield return null;
                Assert.AreEqual("authored_portal", _session.LastInteractHandlerId, "ordinary input opens the focused door");
                Assert.IsTrue(door.IsOpen, "the same focused ordinary door opened: " + doorId);
                Assert.IsTrue(_session.CurrentShip.AuthoredOpenPortalIds.Contains(doorId), "normal portal state recorded for persistence");
                Debug.Log("[FrozenOrdinaryDoorOpened] fixture=" + _fixtureId + " connection=" + landing.ConnectionId
                    + " portal=" + doorId + " kind=" + door.PortalKind + " ordinary_input=true open=true");
                yield return Capture(direction + "-ladder-prompt.png");
            }
            var focused = _boot.Host.FocusedView?.Model;
            Debug.Log("[FrozenLadderFocus] fixture=" + _fixtureId + " connection=" + landing.ConnectionId
                + " direction=" + direction + " expected_node=" + landing.NodeName + " source=" + landing.GlobalPosition
                + " actual_player=" + _boot.Host.SceneState.Player.GodotPosition + " focused_kind=" + (focused?.Kind ?? "none")
                + " focused_node=" + (focused?.NodeName ?? "none") + " active_ship=" + _session.CurrentShip?.ShipId
                + " landing_valid=" + landing.IsValid + " landing_inside_tree=" + landing.IsInsideTree
                + " candidate_overlap=" + landing.CandidatePlayerInRange + " host_paused=" + _boot.Host.Paused);
            Assert.AreSame(landing, focused, "ordinary HUD focus must target the same landing used by interact");
            _boot.Host.SceneState.Player.RequestInteract(); yield return WaitForGrounded(landing.DestinationDeck, "ordinary transfer " + direction);
            Assert.AreEqual("deck_transition", _session.LastInteractHandlerId, "normal input invokes transfer handler");
            AssertStanding(landing.DestinationDeck, "after ordinary discrete transfer " + direction);
            Debug.Log("[FrozenLadderEndpoint] fixture=" + _fixtureId + " connection=" + landing.ConnectionId + " type=" + landing.ConnectionType + " source=" + landing.SourceDeck + " destination=" + landing.DestinationDeck + " placement_probe=true ordinary_input=true grounded=true");
            yield return Capture(direction + "-standing.png");
        }
        void AssertStanding(long deck, string context)
        {
            var player = _boot.Host.SceneState.Player; Assert.IsNotNull(player);
            Assert.IsTrue(player.Controller.isGrounded, context + " grounded");
            Assert.That(player.transform.position.y, Is.InRange(deck * 4f - .4f, deck * 4f + 1f), context + " authored deck height");
            Assert.IsTrue(SpawnClearance.IsClear(player.transform.position), context + " capsule collision clearance");
            Collider floor = SpawnClearance.FloorUnder(player.transform.position); Assert.IsNotNull(floor, context + " support collider");
            Assert.IsNotNull(_boot.Host.ShipHost.HomeLoader, context + " diagnostic fixture loader");
            Assert.IsTrue(floor.transform.IsChildOf(_boot.Host.ShipHost.HomeLoader.GameObject.transform), context + " support belongs to active diagnostic fixture, not docked boat or neighboring geometry");
            StructuralModule module = floor.GetComponentInParent<StructuralModule>(); Assert.IsNotNull(module, context + " real structural support");
            Assert.AreEqual("floor", module.layer, context + " floor rather than decorative marker or ceiling");
        }
        Vector3 FindNearbyStandingPoint(Vector3 start, float distance)
        {
            foreach (Vector3 direction in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
            {
                Vector3 at = start + direction * distance;
                if (SpawnClearance.IsClear(at) && SpawnClearance.FloorUnder(at) != null && !Physics.Linecast(start + Vector3.up, at + Vector3.up, SpawnClearance.BlockingMask, QueryTriggerInteraction.Ignore)) return at;
            }
            Assert.Fail("No one-metre physical walking space at raw fixture spawn " + _fixtureId); return start;
        }
        IEnumerator WalkLocal(Vector3 target, float radius)
        {
            var player = _boot.Host.SceneState.Player; float deadline = Time.realtimeSinceStartup + 5;
            try
            {
                while (Time.realtimeSinceStartup < deadline && Vector3.Distance(player.transform.position, target) > radius)
                {
                    Vector3 delta = target - player.transform.position; delta.y = 0;
                    player.SetScriptedMoveDirection(Frame.ToGodot(delta.normalized)); yield return new WaitForFixedUpdate();
                }
            }
            finally { player.ClearScriptedMoveDirection(); }
            Assert.LessOrEqual(Vector3.Distance(player.transform.position, target), radius + .15f, "real collision-constrained spawn-room walk");
        }
        IEnumerator WaitForGrounded(long deck, string context)
        {
            // Transfer/teleport places feet above the nav floor. Wait for real gravity/collision to settle,
            // including a physics step for queued ordinary input; never alter velocity, geometry or controller state.
            for (int i = 0; i < 60; i++)
            {
                yield return new WaitForFixedUpdate();
                var player = _boot.Host.SceneState.Player;
                if (player != null && player.Controller.isGrounded && Mathf.Abs(player.transform.position.y - deck * 4f) < 1f) yield break;
            }
            Assert.Fail(context + " did not ground on deck " + deck + " within 60 fixed steps; position=" + _boot.Host.SceneState.Player?.transform.position);
        }
        IEnumerator Capture(string name)
        {
            string folder = Environment.GetEnvironmentVariable("SYNAPTIC_PROCEDURAL_CAPTURE_DIR") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/frozen-physical"));
            folder = Path.Combine(folder, _fixtureId); Directory.CreateDirectory(folder);
            var camera = _boot.Host.SceneState.CameraRig.Camera; var panel = _boot.HudDocument.panelSettings;
            const int width = 2048, height = 1224;
            var world = new RenderTexture(width, height, 24); var hud = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(width, height, TextureFormat.RGBA32, false); var uiPixels = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var oldCamera = camera.targetTexture; var oldPanel = panel.targetTexture; var oldActive = RenderTexture.active;
            bool oldClear = panel.clearColor; Color oldColor = panel.colorClearValue;
            try
            {
                hud.Create(); panel.targetTexture = hud; panel.clearColor = true; panel.colorClearValue = Color.clear;
                for (int i = 0; i < 10; i++) yield return null;
                camera.targetTexture = world; camera.Render(); RenderTexture.active = world;
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0); pixels.Apply();
                RenderTexture.active = hud; uiPixels.ReadPixels(new Rect(0, 0, width, height), 0, 0); uiPixels.Apply();
                var scene = pixels.GetPixels32(); var ui = uiPixels.GetPixels32(); int uiCount = 0, worldCount = 0;
                for (int i = 0; i < scene.Length; i++)
                {
                    if (ui[i].a > 0) uiCount++; if (scene[i].r + scene[i].g + scene[i].b > 30) worldCount++;
                    float alpha = ui[i].a / 255f;
                    scene[i] = new Color32((byte)Mathf.Min(255, ui[i].r + scene[i].r * (1-alpha)), (byte)Mathf.Min(255, ui[i].g + scene[i].g * (1-alpha)), (byte)Mathf.Min(255, ui[i].b + scene[i].b * (1-alpha)), 255);
                }
                Assert.Greater(uiCount, 1000, "actual rendered HUD pixels"); Assert.Greater(worldCount, 10000, "actual prefab world pixels");
                pixels.SetPixels32(scene); pixels.Apply(); File.WriteAllBytes(Path.Combine(folder, name), pixels.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = oldCamera; panel.targetTexture = oldPanel; panel.clearColor = oldClear; panel.colorClearValue = oldColor; RenderTexture.active = oldActive;
                Object.Destroy(world); Object.Destroy(hud); Object.Destroy(pixels); Object.Destroy(uiPixels);
            }
        }
    }
}
