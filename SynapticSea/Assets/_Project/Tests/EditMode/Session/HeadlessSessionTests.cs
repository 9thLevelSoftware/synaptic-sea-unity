using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// A RunSession built from the golden coherent_ship_001 documents with no scene (fake ports, in-memory storage,
    /// manual clock). Mirrors the Godot main_playable_slice_{completion,ship_systems,route_control} smoke checks.
    /// </summary>
    public class HeadlessSessionTests
    {
        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Engine = _previousEngine;
        }

        static void TickSeconds(SessionHarness.Rig rig, double seconds, double step = 0.25)
        {
            for (double t = 0; t < seconds - 1e-9; t += step)
            {
                rig.Clock.Advance(step);
                rig.Session.Tick(TickContext.Frame(step, rig.Scene.PlayerPosition));
            }
        }

        [Test]
        public void GoldenSession_BootsLikeTheGodotSlice()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, "session did not start: " + s.LastFailureReason);
            Assert.IsTrue(rig.Scene.HasPlayer, "player spawned");
            Assert.AreEqual(4, s.SequenceInteractables.Count, "four objective sequences");
            Assert.AreEqual(5, s.Interactables.Count, "four objectives, one of them a two-step junction");
            Assert.AreEqual(1, s.CurrentObjectiveSequence);
            Assert.IsNotNull(s.HomeShip);
            Assert.AreSame(s.HomeShip, s.CurrentShip);
            Assert.IsNotNull(s.LifeboatShip, "lifeboat built");
            Assert.AreSame(s.LifeboatShip, s.PilotedShip, "the lifeboat is the ride");
            Assert.AreSame(s.HomeShip, s.LifeboatShip.ParentShip, "lifeboat port-docked to home");
            Assert.AreEqual(1, s.DockBarriers.Count, "boot home seam barrier");

            // route_control smoke: one powered gate, blocking, closed.
            GdDict route = s.GetRouteControlSummary();
            Assert.GreaterOrEqual(V.I64(route.Get("route_gate_count", 0L)), 1);
            Assert.GreaterOrEqual(V.I64(route.Get("active_blocker_count", 0L)), 1);
            Assert.AreEqual(0, V.I64(route.Get("opened_gate_count", -1L)));
            Assert.IsFalse(route.GetBool("extraction_unlocked"));
            Assert.GreaterOrEqual(s.GetRouteGateCollisionEnabledCount(), 1);

            // ship_systems smoke: power restored/extraction start false.
            GdDict sys = s.GetShipSystemsSummary();
            Assert.IsFalse(sys.GetBool("main_power_restored"));
            Assert.IsFalse(sys.GetBool("extraction_unlocked"));

            // Component placement matches the Godot capture (14 placements, home seed 1).
            Assert.AreEqual(14, s.ComponentPlacementState.Placed.Count);
            Assert.AreEqual(1, s.ComponentPlacementState.SeedValue);

            // Threats fall back to the five layout archetypes around the home anchor (Godot capture).
            Assert.AreEqual(5, s.ThreatManager.Threats.Count);
            Assert.AreEqual("fallback_0_0", s.ThreatManager.Threats[0].InstanceId);
        }

        [Test]
        public void GoldenSession_TicksAndCompletesTheObjectiveChain()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            var interactions = new List<long>();
            GdDict completion = null;
            s.PlayableInteractionCompleted += (iid, oid, seq, type, room) => interactions.Add(seq);
            s.PlayableSliceCompleted += summary => completion = summary;
            // The golden ship has no encounter markers, so the five fallback threats spawn around the ship origin, next to
            // the start room; an idle player dies in ~10 s (see IdlePlayerAtSpawn_IsKilledByTheFallbackStalker). This
            // test is about the objective chain, so the threats are removed.
            s.ThreatManager.Threats.Clear();

            TickSeconds(rig, 10.0);
            Assert.AreEqual(10.0, s.WorldTime, 1e-9);
            Assert.AreEqual(10.0, s.RunPlayTimeSeconds, 1e-9);
            Assert.IsFalse(s.SliceComplete);

            Assert.IsTrue(s.CompleteObjectiveSequence(1), "objective 1");
            Assert.IsTrue(s.GetShipSystemsSummary().GetBool("emergency_supplies_recovered"));
            Assert.AreEqual(0, V.I64(s.GetRouteControlSummary().Get("opened_gate_count", -1L)), "gates stay closed after obj 1");

            TickSeconds(rig, 5.0);
            Assert.IsTrue(s.CompleteObjectiveSequence(2), "objective 2 (two-step junction)");
            GdDict s2 = s.GetShipSystemsSummary();
            Assert.IsTrue(s2.GetBool("main_power_restored"));
            Assert.IsTrue(s2.GetBool("blocked_routes_cleared"));
            Assert.AreEqual(0, V.I64(s2.Get("blocked_affordance_visible_count", -1L)));
            Assert.IsTrue(s.GetOxygenSummary().GetBool("breach_sealed"), "restore_systems seals the breach");
            Assert.GreaterOrEqual(V.I64(s.GetRouteControlSummary().Get("opened_gate_count", 0L)), 1);
            Assert.IsTrue(s.GetRouteControlSummary().GetBool("powered_gates_open"));
            Assert.AreEqual(0, V.I64(s.GetRouteControlSummary().Get("active_blocker_count", -1L)));
            Assert.IsFalse(s2.GetBool("extraction_unlocked"));
            // OBJECTIVE_REPAIR_MAP: restore_systems -> power_distribution + battery_cells.
            Assert.IsTrue(s.ShipSystemsManager.GetSystem("power").GetSubcomponent("power_distribution").IsFunctional());
            Assert.IsTrue(s.ShipSystemsManager.GetSystem("power").GetSubcomponent("battery_cells").IsFunctional());

            // Objectives 2-4 back to back, like the smoke: the damaged power grid keeps wearing while time passes, so
            // ticking after restore_systems would pull power_percent below the smoke's 100.
            Assert.IsTrue(s.CompleteObjectiveSequence(3), "objective 3");
            Assert.IsTrue(s.GetShipSystemsSummary().GetBool("navigation_logs_downloaded"));
            Assert.IsTrue(s.ShipSystemsManager.GetSystem("navigation").GetSubcomponent("nav_computer").IsFunctional());

            Assert.IsTrue(s.CompleteObjectiveSequence(4), "objective 4");
            GdDict s4 = s.GetShipSystemsSummary();
            Assert.IsTrue(s4.GetBool("reactor_stabilized"));
            Assert.IsTrue(s4.GetBool("extraction_unlocked"));
            Assert.AreEqual(100, V.I64(s4.Get("power_percent", 0L)));
            Assert.AreEqual(100, V.I64(s4.Get("reactor_stability_percent", 0L)));

            // completion smoke.
            Assert.IsTrue(s.SliceComplete);
            Assert.AreEqual(4, s.ObjectiveCompletionCount);
            Assert.AreEqual(5, s.CurrentObjectiveSequence);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, interactions);
            Assert.IsNotNull(completion, "playable_slice_completed raised");
            Assert.IsTrue(completion.GetBool("run_complete"));
            Assert.AreEqual("complete", completion.GetString("reason"));
            Assert.IsTrue(s.GetSliceCompletionSummary().GetBool("run_complete"));

            // After completion the per-frame systems stop; only world_time moves.
            double playTime = s.RunPlayTimeSeconds;
            TickSeconds(rig, 2.0);
            Assert.AreEqual(playTime, s.RunPlayTimeSeconds, 1e-12);
            Assert.IsFalse(rig.Storage.FileExists(SaveLoadService.SAVE_PATH), "completion deletes the current-run save");
        }

        [Test]
        public void GoldenSession_CheckpointSavesAndReloadsTheWorld()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.IsTrue(s.CompleteObjectiveSequence(1));
            Assert.IsTrue(rig.Storage.FileExists(SaveLoadService.WORLD_SLOT_FILE), "objective boundary writes the world checkpoint");
            Assert.IsNotNull(s.LastSavedSnapshot);
            Assert.AreEqual(2, s.LastSavedSnapshot.CurrentObjectiveSequence, "the checkpoint captures the resumed sequence");

            s.ThreatManager.Threats.Clear();
            Vec3 savedAt = rig.Scene.PlayerPosition;
            TickSeconds(rig, 2.0);
            Assert.IsTrue(s.RequestSave(), "F5 world save");
            double savedPlayTime = s.RunPlayTimeSeconds;

            // Diverge without crossing an objective boundary (that would overwrite world.json with a checkpoint).
            TickSeconds(rig, 3.0);
            rig.Scene.PlayerPosition = new Vec3(1.0f, 2.0f, 3.0f);
            Assert.AreEqual(savedPlayTime + 3.0, s.RunPlayTimeSeconds, 1e-9);

            Assert.IsTrue(s.RequestLoad(), "world load");
            Assert.AreEqual(2, s.CurrentObjectiveSequence);
            Assert.IsTrue(s.CompletedObjectiveTypes.Has("recover_supplies"));
            Assert.IsFalse(s.CompletedObjectiveTypes.Has("restore_systems"));
            Assert.AreEqual(savedPlayTime, s.RunPlayTimeSeconds, 1e-9, "play time restored");
            Assert.AreEqual(savedAt, rig.Scene.PlayerPosition, "player restored to the saved position (scene half)");
            Assert.AreEqual(2, rig.Host.HomeLoads, "the reload re-drives the home loader");
            Assert.IsTrue(s.CompleteObjectiveSequence(2), "the chain continues after load");
            Assert.AreEqual(3, s.CurrentObjectiveSequence);
        }

        [Test]
        public void IdlePlayerAtSpawn_IsKilledByTheFallbackStalker()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            GdDict completion = null;
            s.PlayableSliceCompleted += summary => completion = summary;
            TickSeconds(rig, 15.0);
            Assert.IsTrue(s.SliceComplete, "the run ended");
            Assert.IsNotNull(completion);
            Assert.AreEqual("death", completion.GetString("reason"));
            Assert.IsTrue(s.VitalsState.IsIncapacitated());
            Assert.AreEqual("fallback_2_0", V.Str(s.ThreatManager.GetSummary().GetDictOrEmpty("last_attack_result").Get("source_id", "")));
            Assert.Less(s.RunPlayTimeSeconds, 15.0, "play time stops at death");
        }

        [Test]
        public void GoldenSession_InteractDispatch_MissesWhenNothingIsInRange()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            rig.Scene.PlayerPosition = new Vec3(500.0f, 0.0f, 500.0f);
            Assert.AreEqual(InteractionRegistry.MissHandlerId, s.RequestInteract());
            ObjectiveInteractable first = s.GetInteractableBySequence(1);
            rig.Scene.PlayerPosition = first.GlobalPosition;
            string handler = s.RequestInteract();
            Assert.AreNotEqual(InteractionRegistry.MissHandlerId, handler);
        }

        [Test]
        public void RouteGateOpening_DisablesItsBlockedRouteNodeCollider()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            var loader = (FakeLoaderView)s.Loader;
            Assert.AreEqual((0, true), loader.BlockedRouteCollisionCalls[loader.BlockedRouteCollisionCalls.Count - 1], "a closed gate keeps its route node collidable");
            Assert.AreEqual(0L, V.I64(s.RouteGateNodes[0].Meta["blocked_route_index"]));
            Assert.IsTrue(s.CompleteObjectiveSequence(1));
            Assert.IsTrue(s.CompleteObjectiveSequence(2), "restore_systems opens the powered gates");
            Assert.AreEqual((0, false), loader.BlockedRouteCollisionCalls[loader.BlockedRouteCollisionCalls.Count - 1], "the open gate's route node stops colliding");
        }
    }
}
