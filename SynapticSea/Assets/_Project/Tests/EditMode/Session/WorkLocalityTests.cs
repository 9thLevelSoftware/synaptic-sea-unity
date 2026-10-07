using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// Invariant-only fixtures: tools/targets are provisioned, but start, input and timed completion use the real session.
    /// These do not certify earned acquisition, walking, physics, or native input.
    /// </summary>
    public abstract class WorkSessionTestBase
    {
        readonly List<RunSession> _sessions = new List<RunSession>();
        IEngineInfo _previousEngine;
        protected static readonly Vec3 WorkSite = new Vec3(2000, 0, 2000);

        [SetUp]
        public void SetUpWork()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDownWork()
        {
            foreach (RunSession session in _sessions) session.Dispose();
            _sessions.Clear();
            CatalogRegistry.Clear();
            CoreServices.Engine = _previousEngine;
        }

        protected SessionHarness.Rig Boot(bool tap = false)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.SettingsState = new SettingsState();
            deps.SettingsState.SetHoldToTap(tap);
            rig.Session = RunSession.Create(deps);
            _sessions.Add(rig.Session);
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            rig.Session.ThreatManager.Threats.Clear();
            return rig;
        }

        protected static GdDict LocalLayout() => new GdDict
        {
            { "rooms", GdArray.Of(new GdDict
                {
                    { "id", "work-room" },
                    { "structural_placements", GdArray.Of(new GdDict
                        {
                            { "module_id", "wall_straight_1x1" }, { "name", "work-wall" },
                            { "world_position", GdArray.Of((double)WorkSite.X, (double)WorkSite.Y, (double)WorkSite.Z) },
                        }) },
                }) },
        };

        protected static void StartLocalCut(SessionHarness.Rig rig, double offset = 0)
        {
            RunSession session = rig.Session;
            session.CurrentShip.BuiltLayout = LocalLayout();
            session.ModuleIntegrityMap = new ModuleIntegrityMap();
            session.InventoryState.AddItem("welding_lance", 1);
            rig.Scene.PlayerPosition = WorkSite + new Vec3(offset, 0, 0);
            Assert.IsFalse(session.BeginWorkHold());
            Assert.AreEqual("work_action", session.RequestInteract());
            Assert.IsTrue(session.WorkActionDriver.IsWorking());
            Assert.AreEqual("cut_wall", session.WorkActionDriver.Work.ActionId);
            Assert.AreEqual("work-room/work-wall", session.WorkActionDriver.Work.TargetId);
        }

        protected static void StartGoldenCut(SessionHarness.Rig rig)
        {
            RunSession session = rig.Session;
            session.InventoryState.AddItem("welding_lance", 1);
            Assert.IsFalse(session.BeginWorkHold());
            foreach (GdDict room in session.Loader.LayoutDoc.GetArrayOrEmpty("rooms").OfType<GdDict>())
            {
                foreach (GdDict placement in room.GetArrayOrEmpty("structural_placements").OfType<GdDict>())
                {
                    if (!(placement.Get("world_position", null) is GdArray pos) || pos.Count < 3) continue;
                    rig.Scene.PlayerPosition = new Vec3(V.F64(pos[0]), V.F64(pos[1]), V.F64(pos[2]));
                    string handler = session.RequestInteract();
                    if (!session.WorkActionDriver.IsWorking()) continue;
                    Assert.AreEqual("work_action", handler);
                    Assert.AreEqual("cut_wall", session.WorkActionDriver.Work.ActionId);
                    return;
                }
            }
            Assert.Fail("Golden fixture must start a cut through ordinary interaction dispatch.");
        }

        protected static void Advance(RunSession session, int ticks = 60)
        {
            for (int i = 0; i < ticks; i++) session.StageWorkAction(0.1);
        }

        protected static int TrainingCount(RunSession session) => session.TrainingEventBus.GetLog().Count;
    }

    public class WorkLocalityTests : WorkSessionTestBase
    {
        [TestCase(false)]
        [TestCase(true)]
        public void RemoteCut_InterruptsBeforeProgressStaminaOrEffects(bool tap)
        {
            SessionHarness.Rig rig = Boot(tap);
            StartLocalCut(rig);
            RunSession session = rig.Session;
            WorkActionState work = session.WorkActionDriver.Work;
            double stamina = session.VitalsState.Stamina;
            int events = TrainingCount(session);
            rig.Scene.PlayerPosition += new Vec3(1000, 0, 1000);
            Advance(session);
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Status);
            Assert.AreEqual("left_work_site", work.BlockReason);
            Assert.AreEqual(0, work.Progress);
            Assert.AreEqual(stamina, session.VitalsState.Stamina);
            Assert.AreEqual("intact", session.ModuleIntegrityMap.GetState(work.TargetId));
            Assert.AreEqual(0, session.InventoryState.GetQuantity("scrap_metal"));
            Assert.AreEqual(events, TrainingCount(session));
        }

        [Test]
        public void ReleasedHold_PreservesProgressWithoutStaminaNoiseOrEffects()
        {
            SessionHarness.Rig rig = Boot();
            StartLocalCut(rig);
            RunSession session = rig.Session;
            Advance(session, 2);
            WorkActionState work = session.WorkActionDriver.Work;
            double progress = work.Progress;
            double stamina = session.VitalsState.Stamina;
            int events = TrainingCount(session);
            int sounds = session.AudioManager.PlayedSfx.Count;
            session.EndWorkHold();
            Advance(session);
            Assert.IsTrue(session.WorkActionDriver.IsWorking());
            Assert.AreEqual(progress, work.Progress);
            Assert.AreEqual(stamina, session.VitalsState.Stamina);
            Assert.AreEqual(sounds, session.AudioManager.PlayedSfx.Count);
            Assert.AreEqual(events, TrainingCount(session));
            Assert.AreEqual(0, session.InventoryState.GetQuantity("scrap_metal"));
        }

        [Test]
        public void ChangedOwner_WithEqualTargetId_DoesNotCompleteAgainstAnotherShip()
        {
            SessionHarness.Rig rig = Boot();
            StartLocalCut(rig);
            RunSession session = rig.Session;
            WorkActionState work = session.WorkActionDriver.Work;
            session.CurrentShip = new ShipInstance { ShipId = "different-owner", BuiltLayout = LocalLayout(), SceneRoot = rig.Session.Loader };
            Advance(session);
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Status);
            Assert.AreEqual(0, work.Progress);
            Assert.AreEqual(0, session.InventoryState.GetQuantity("scrap_metal"));
        }

        [Test]
        public void ChangedModuleContext_WithEqualTargetId_DoesNotMutateReplacementMap()
        {
            SessionHarness.Rig rig = Boot();
            StartLocalCut(rig);
            RunSession session = rig.Session;
            WorkActionState work = session.WorkActionDriver.Work;
            session.ModuleIntegrityMap = new ModuleIntegrityMap();
            session.ModuleIntegrityMap.EnsureModule(work.TargetId, "wall_straight_1x1");
            Advance(session);
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Status);
            Assert.AreEqual("intact", session.ModuleIntegrityMap.GetState(work.TargetId));
            Assert.AreEqual(0, session.InventoryState.GetQuantity("scrap_metal"));
        }

        [Test]
        public void MissingOriginalTarget_DoesNotRetargetToNearbyStructure()
        {
            SessionHarness.Rig rig = Boot();
            StartLocalCut(rig);
            RunSession session = rig.Session;
            WorkActionState work = session.WorkActionDriver.Work;
            GdDict placement = (GdDict)((GdDict)session.CurrentShip.BuiltLayout.GetArrayOrEmpty("rooms")[0]).GetArrayOrEmpty("structural_placements")[0];
            placement["name"] = "replacement-wall";
            Advance(session);
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Status);
            Assert.AreEqual(0, work.Progress);
            Assert.AreEqual(0, session.InventoryState.GetQuantity("scrap_metal"));
        }

        [Test]
        public void OriginalGenericRangeBoundary_RemainsInclusive()
        {
            SessionHarness.Rig rig = Boot();
            StartLocalCut(rig, RunSession.WORK_ACTION_INTERACT_RANGE);
            Advance(rig.Session, 1);
            Assert.Greater(rig.Session.WorkActionDriver.Work.Progress, 0);
            Assert.IsTrue(rig.Session.WorkActionDriver.IsWorking());
            rig.Scene.PlayerPosition += new Vec3(0.01, 0, 0);
            double progress = rig.Session.WorkActionDriver.Work.Progress;
            Advance(rig.Session, 1);
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, rig.Session.WorkActionDriver.Work.Status);
            Assert.AreEqual(progress, rig.Session.WorkActionDriver.Work.Progress);
        }

        [Test]
        public void LocalCompletion_AwardsExistingEffectsOnceAcrossRepeatedTicksAndRestore()
        {
            SessionHarness.Rig rig = Boot();
            StartGoldenCut(rig);
            RunSession session = rig.Session;
            int events = TrainingCount(session);
            Advance(session);
            Assert.AreEqual(WorkActionState.STATUS_IDLE, session.WorkActionDriver.GetStatus());
            long scrap = session.InventoryState.GetQuantity("scrap_metal");
            Assert.Greater(scrap, 0);
            Assert.AreEqual(events + 1, TrainingCount(session));
            int sounds = session.AudioManager.PlayedSfx.Count;
            Advance(session);
            Assert.AreEqual(scrap, session.InventoryState.GetQuantity("scrap_metal"));
            Assert.AreEqual(events + 1, TrainingCount(session));
            Assert.AreEqual(sounds, session.AudioManager.PlayedSfx.Count);
            var snapshot = RunSnapshotAssembler.Build(session);
            SessionHarness.Rig restored = Boot();
            Assert.IsTrue(restored.Session.ApplyManualSlot(snapshot));
            Advance(restored.Session);
            Assert.AreEqual(scrap, restored.Session.InventoryState.GetQuantity("scrap_metal"));
            Assert.AreEqual(0, TrainingCount(restored.Session));
        }

        [Test]
        public void Exhaustion_StillInterruptsBeforeProgress()
        {
            SessionHarness.Rig rig = Boot();
            StartLocalCut(rig);
            rig.Session.VitalsState.Stamina = 0;
            Advance(rig.Session, 1);
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, rig.Session.WorkActionDriver.Work.Status);
            Assert.AreEqual("exhausted", rig.Session.WorkActionDriver.Work.BlockReason);
            Assert.AreEqual(0, rig.Session.WorkActionDriver.Work.Progress);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ComponentWork_RemoteSiteCannotDismountOrConsumeRemountItem(bool mounted)
        {
            SessionHarness.Rig rig = Boot();
            RunSession session = rig.Session;
            session.CurrentShip.BuiltLayout = LocalLayout();
            session.ComponentPlacementState.Placed.Clear();
            var entry = new GdDict
            {
                { "component_instance_id", "local-component" }, { "component_id", "power_coupling" },
                { "item_form", "power_coupling" }, { "room_id", "work-room" },
                { "slot_kind", "wall" }, { "slot_index", 0L }, { "mounted", mounted },
            };
            session.ComponentPlacementState.Placed.Add(entry);
            // The audit found wrench acquisition unresolved. Provision quantity only to isolate the existing work invariant.
            session.InventoryState.Items["wrench"] = 1L;
            if (!mounted) session.InventoryState.Items["power_coupling"] = 1L;
            rig.Scene.PlayerPosition = WorkSite;
            Assert.IsFalse(session.BeginWorkHold());
            Assert.AreEqual("work_action", session.RequestInteract());
            WorkActionState work = session.WorkActionDriver.Work;
            Assert.IsTrue(session.WorkActionDriver.IsWorking());
            Assert.AreEqual(mounted ? "dismount_component" : "mount_component", work.ActionId);
            double stamina = session.VitalsState.Stamina;
            rig.Scene.PlayerPosition += new Vec3(1000, 0, 1000);
            Advance(session, 80);
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Status);
            Assert.AreEqual("left_work_site", work.BlockReason);
            Assert.AreEqual(0, work.Progress);
            Assert.AreEqual(stamina, session.VitalsState.Stamina);
            Assert.AreEqual(mounted, entry.GetBool("mounted"));
            Assert.AreEqual(mounted ? 0 : 1, session.InventoryState.GetQuantity("power_coupling"));
            Assert.AreEqual(0, TrainingCount(session));
        }

        [Test]
        public void LegacyWeldFallback_RetainsSelectedRoomCenterAnchorDuringWork()
        {
            SessionHarness.Rig rig = Boot();
            RunSession session = rig.Session;
            // Existing selector: the nearest intact wall is skipped for weld; the damaged map target uses its room
            // center when no scene wrapper exists. Its per-room placement lies elsewhere; preserve this legacy policy.
            session.CurrentShip.BuiltLayout = new GdDict
            {
                { "rooms", GdArray.Of(
                    new GdDict
                    {
                        { "id", "damaged-room" },
                        { "structural_placements", GdArray.Of(
                            new GdDict { { "module_id", "wall_straight_1x1" }, { "name", "wall" },
                                { "world_position", GdArray.Of((double)WorkSite.X + 100, 0.0, (double)WorkSite.Z) } },
                            new GdDict { { "module_id", "floor_1x1" }, { "name", "floor" },
                                { "world_position", GdArray.Of((double)WorkSite.X - 100, 0.0, (double)WorkSite.Z) } }) },
                    },
                    new GdDict
                    {
                        { "id", "nearby-room" },
                        { "structural_placements", GdArray.Of(new GdDict
                            { { "module_id", "wall_straight_1x1" }, { "name", "wall" },
                                { "world_position", GdArray.Of((double)WorkSite.X, 0.0, (double)WorkSite.Z) } }) },
                    }) },
            };
            session.ModuleIntegrityMap = new ModuleIntegrityMap();
            session.ModuleIntegrityMap.ApplyDamage("damaged-room/wall", 0.4, "wall_straight_1x1");
            session.InventoryState.AddItem("welding_lance", 1);
            session.InventoryState.AddItem("hull_plate", 1);
            rig.Scene.PlayerPosition = WorkSite;
            Assert.IsFalse(session.BeginWorkHold());
            Assert.AreEqual("work_action", session.RequestInteract());
            WorkActionState work = session.WorkActionDriver.Work;
            Assert.IsTrue(session.WorkActionDriver.IsWorking());
            Assert.AreEqual("weld_patch", work.ActionId);
            Assert.AreEqual("damaged-room/wall", work.TargetId);
            Advance(session, 1);
            Assert.Greater(work.Progress, 0, "the anchor chosen by the current selector remains valid on the next work tick");
            double progress = work.Progress;
            double stamina = session.VitalsState.Stamina;
            rig.Scene.PlayerPosition += new Vec3(1000, 0, 1000);
            Advance(session);
            Assert.AreEqual(WorkActionState.STATUS_INTERRUPTED, work.Status);
            Assert.AreEqual(progress, work.Progress);
            Assert.AreEqual(stamina, session.VitalsState.Stamina);
            Assert.AreEqual(1, session.InventoryState.GetQuantity("hull_plate"));
        }
    }
}
