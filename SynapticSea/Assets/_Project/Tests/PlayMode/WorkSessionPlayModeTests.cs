using System.Collections;
using NUnit.Framework;
using SynapticSea.App;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Game;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>
    /// Invariant-only scene/input seam: real Playable scene, structural anchors, player teleport and host press/release.
    /// The tool is provisioned and only the production work stage is advanced; this is not an earned walking journey.
    /// </summary>
    public class WorkSessionPlayModeTests
    {
        IStorage _previousStorage;
        IResourceReader _previousResources;
        ILog _previousLog;
        float _previousTimeScale;
        PlayableBootstrap _boot;
        RunSession _session;

        [SetUp]
        public void SetUp()
        {
            _previousStorage = CoreServices.UserStorage;
            _previousResources = CoreServices.Resources;
            _previousLog = CoreServices.Log;
            _previousTimeScale = Time.timeScale;
            AppServices.StorageOverride = new MemoryStorage();
            RunLaunchRequest.Pending = null;
            RunReturnInfo.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            Time.timeScale = _previousTimeScale;
            foreach (RunSessionHost host in Object.FindObjectsByType<RunSessionHost>()) Object.DestroyImmediate(host.gameObject);
            foreach (PlayableBootstrap boot in Object.FindObjectsByType<PlayableBootstrap>()) Object.DestroyImmediate(boot.gameObject);
            foreach (TitleScreen title in Object.FindObjectsByType<TitleScreen>()) Object.DestroyImmediate(title.gameObject);
            AppServices.Shutdown();
            AppServices.StorageOverride = null;
            RunLaunchRequest.Pending = null;
            RunReturnInfo.Clear();
            HallucinationFx.SetGlobalIntensity(0);
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
            CoreServices.UserStorage = _previousStorage;
            CoreServices.Resources = _previousResources;
            CoreServices.Log = _previousLog;
        }

        IEnumerator Boot()
        {
            RunLaunchRequest.Pending = RunLaunchRequest.GoldenShip();
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName);
            yield return null;
            float deadline = Time.realtimeSinceStartup + 60;
            while (Time.realtimeSinceStartup < deadline && (PlayableBootstrap.Current == null || !PlayableBootstrap.Current.IsBooted))
            {
                if (PlayableBootstrap.Current != null && PlayableBootstrap.Current.IsFailed) break;
                yield return null;
            }
            _boot = PlayableBootstrap.Current;
            Assert.IsNotNull(_boot);
            Assert.IsTrue(_boot.IsBooted, _boot.BootFailure);
            _session = _boot.Session;
            _session.ThreatManager.Threats.Clear();
            // Keep non-work simulation/physics out of these work invariants. The host still handles real input requests/views.
            _boot.Host.enabled = false;
            _boot.Host.SimulationPaused = () => false;
            _boot.Host.GameplayInputBlocked = () => false;
        }

        void StartCut()
        {
            _session.InventoryState.AddItem("welding_lance", 1);
            foreach (IStructuralModuleNode node in _session.Loader.StructuralModuleNodes())
            {
                if (!node.HasModuleKeyMeta || !ModuleIntegrityConsequences.IsWallKind(node.ModuleKind ?? "")) continue;
                _boot.Host.SceneState.TeleportPlayer(node.GlobalPosition);
                string handler = _boot.Host.PressInteract();
                if (!_session.WorkActionDriver.IsWorking()) { _boot.Host.ReleaseInteract(); continue; }
                Assert.AreEqual("work_action", handler);
                Assert.AreEqual("cut_wall", _session.WorkActionDriver.Work.ActionId);
                return;
            }
            Assert.Fail("A real scene wall anchor must start cut work through RunSessionHost.PressInteract.");
        }

        void Advance(int ticks = 60)
        {
            for (int i = 0; i < ticks; i++) TickOrder.Get(TickOrder.WorkAction).Run(_session, SessionLocation.Home, 0.1);
        }

        [UnityTest]
        public IEnumerator HostPressReleaseAndRealPlayerMovement_KeepWorkLocal()
        {
            yield return Boot();
            _session.SettingsState.SetHoldToTap(false);
            StartCut();
            WorkActionState work = _session.WorkActionDriver.Work;
            Advance(2);
            _boot.Host.ReleaseInteract();
            double progress = work.Progress;
            double stamina = _session.VitalsState.Stamina;
            Advance(2);
            Assert.AreEqual(progress, work.Progress);
            Assert.AreEqual(stamina, _session.VitalsState.Stamina);
            _boot.Host.PressInteract();
            _boot.Host.SceneState.TeleportPlayer(_boot.Host.SceneState.PlayerPosition + new Vec3(1000, 0, 1000));
            Advance();
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Status);
            Assert.AreEqual("left_work_site", work.BlockReason);
            Assert.AreEqual(progress, work.Progress);
            Assert.AreEqual(stamina, _session.VitalsState.Stamina);
            Assert.AreEqual(0, _session.InventoryState.GetQuantity("scrap_metal"));
        }

        [UnityTest]
        public IEnumerator HostPressAfterToggleRestore_ExplicitlyResumesOriginalSceneTarget()
        {
            yield return Boot();
            GdDict settings = _boot.Coordinator.GetSettingsSummary();
            settings["hold_to_tap"] = true;
            Assert.IsTrue(_boot.Coordinator.ApplySettingsSummary(settings));
            Assert.IsFalse(_session.HoldToWorkEnabled, "this fixture selects accessibility tap mode before starting work");
            StartCut();
            _boot.Host.ReleaseInteract();
            Advance(2);
            string target = _session.WorkActionDriver.Work.TargetId;
            var snapshot = RunSnapshotAssembler.Build(_session);
            Assert.IsTrue(_session.ApplyManualSlot(snapshot));
            _boot.Host.ApplyViews();
            Assert.IsFalse(_session.HoldToWorkEnabled, "the player's selected tap preference must still be active after restore");
            WorkActionState work = _session.WorkActionDriver.Work;
            Assert.AreEqual(target, work.TargetId);
            double progress = work.Progress;
            Advance(2);
            Assert.AreEqual(progress, work.Progress);
            Assert.AreEqual("resume_required", work.BlockReason);
            Assert.AreEqual("", _boot.Host.PressInteract(), "restored resume consumes the press before a new interaction");
            Assert.IsFalse(_session.HoldToWorkEnabled, "explicit resume uses the current tap input mode");
            _boot.Host.ReleaseInteract();
            Advance(2);
            Assert.AreEqual(WorkActionState.STATUS_ACTIVE, work.Status);
            Assert.Greater(work.Progress, progress);
            _boot.Host.SceneState.TeleportPlayer(_boot.Host.SceneState.PlayerPosition + new Vec3(1000, 0, 1000));
            Advance();
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Status);
            Assert.AreEqual("left_work_site", work.BlockReason);
        }
    }
}
