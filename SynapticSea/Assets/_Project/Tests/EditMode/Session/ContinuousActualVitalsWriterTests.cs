using System;
using System.Threading;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public sealed class ContinuousActualVitalsWriterTests : InfraDataTestBase
    {
        IEngineInfo _engine;
        [SetUp] public void Engine() { _engine = CoreServices.Engine; CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion); }
        [TearDown] public void Restore() { CoreServices.Engine = _engine; }
        static RunSession Boot()
        {
            var deps = SessionHarness.GoldenDeps(out var rig); SessionHarness.OverlayGamePlayability(deps);
            rig.Session = RunSession.Create(deps); Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason); return rig.Session;
        }
        [Test] public void ActualSurvivalAndDamageKeepModelIdentityAndOrderedObserverPolicy()
        {
            var session = Boot(); var actual = session.VitalsState;
            using var bridge = new ActualVitalsProjectionBridge(session); var writer = new ContinuousActualVitalsWriter(session, bridge);
            int events = 0; string source = "";
            actual.HealthDamageObserved += (s, amount) => { Assert.IsFalse(Monitor.IsEntered(CommonParticipantGate.SyncRoot)); source = s; events++; };
            Assert.IsTrue(writer.TryTick(.25, new DiagnosticVitalsTickInput(moving: false, fire: 2), out var reason), reason);
            Assert.Less(actual.Health, 100); Assert.Less(actual.Hunger, 100); Assert.Greater(events, 0);
            var target = (IDamageVitalsTarget)writer; var before = target.Health; target.Health = before - 4;
            Assert.AreEqual(before - 4, actual.Health); Assert.AreEqual("combat", source); Assert.AreSame(actual, session.VitalsState);
            Assert.GreaterOrEqual(writer.DamageEpoch, 2UL);
        }
        [Test] public void StaleCombatABARefusesWhileFreshRawDriftRevokesCaptureAndContinues()
        {
            var session = Boot(); using var bridge = new ActualVitalsProjectionBridge(session);
            var writer = new ContinuousActualVitalsWriter(session, bridge); var target = (IDamageVitalsTarget)writer;
            double initial = target.Health;
            Assert.IsTrue(writer.TryDelta(-5, 0, 0, 0, out _)); Assert.IsTrue(writer.TryDelta(5, 0, 0, 0, out _));
            Assert.Throws<InvalidOperationException>(() => target.Health = initial - 4); Assert.AreEqual(initial, session.VitalsState.Health);
            Assert.AreEqual(1UL, writer.DamageEpoch); session.VitalsState.MaxStamina += 1;
            Assert.IsTrue(writer.TryDelta(0, -1, 0, 0, out var reason), reason);
            Assert.IsFalse(writer.CaptureAvailable); Assert.AreEqual(99, session.VitalsState.Stamina);
            Assert.AreEqual(101, session.VitalsState.MaxStamina); Assert.AreEqual(initial, session.VitalsState.Health);
            Assert.IsFalse(bridge.TryPin(out _, out _));
        }
        [Test] public void ObserverFailureReportsCommittedAndForeignThreadCannotWrite()
        {
            var session = Boot(); using var bridge = new ActualVitalsProjectionBridge(session);
            var writer = new ContinuousActualVitalsWriter(session, bridge);
            session.VitalsState.HealthDamageObserved += (s, amount) => throw new InvalidOperationException("presentation");
            Assert.IsTrue(writer.TryDelta(-3, 0, 0, 0, out var reason)); StringAssert.StartsWith("committed_notification_failed:", reason);
            Assert.AreEqual(97, session.VitalsState.Health); Assert.AreEqual(1UL, writer.DamageEpoch);
            Exception failure = null;
            var thread = new Thread(() => { try { writer.TryDelta(0, -1, 0, 0, out _); } catch (Exception ex) { failure = ex; } });
            thread.Start(); thread.Join(); Assert.IsInstanceOf<InvalidOperationException>(failure); Assert.AreEqual(100, session.VitalsState.Stamina);
        }
        [Test] public void SameRunSessionModelPairedWorkDebitsOnlyStaminaAndRejectsLaterSurvivalWrite()
        {
            var session = Boot(); var actual = session.VitalsState;
            using var bridge = new ActualVitalsProjectionBridge(session); var writer = new ContinuousActualVitalsWriter(session, bridge);
            var resourceBefore = ResourceAuthorityPublication.Reader;
            try
            {
                ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(new Dictionary<string, string>()));
                Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var lease, out _));
                var inventory = InventoryState.CreateTracked(new GdDict());
                var seed = new UntrustedAuxReplaySeed("run", "actor", "service", new string('a', 64), 12, 0, 0, 0);
                var values = bridge.Read().Values;
                var context = new DiagnosticAuxiliaryPairContext(0, new AuxiliaryWorkFrame(.1, values.Stamina, values.MaxStamina, 1), inventory, lease);
                var runtime = AuxiliaryWorkRuntime.CreateRunSessionVitalsPaired("home", 0, PaidHashContext.BitsV2.Algorithm,
                    seed, 4, new AuxiliaryEvidenceLimits(8, 100, 2), bridge, context);
                Assert.IsTrue(runtime.TryPrepareRunSessionPair(out var pair, out var result, out var reason), reason);
                using (pair) using (var attempt = CommonParticipantGate.BeginAttempt()) Assert.IsTrue(pair.TryInstallUnderGate(runtime, attempt, out reason), reason);
                Assert.AreSame(actual, session.VitalsState); Assert.AreEqual(result.StaminaAfter, actual.Stamina);
                Assert.AreEqual(result.Snapshot.ProgressSeconds, runtime.Snapshot().ProgressSeconds);
                Assert.AreEqual(1, runtime.EvidenceJournal.AcceptedStepCount); Assert.AreEqual(100, actual.Health);
                Assert.AreEqual("paired_step_only", runtime.Step(new AuxiliaryWorkFrame(.1, actual.Stamina, actual.MaxStamina, 1)).Reason);
                context.Replace(1, new AuxiliaryWorkFrame(.1, actual.Stamina, actual.MaxStamina, 1), inventory, lease);
                Assert.IsTrue(runtime.TryPrepareRunSessionPair(out var stale, out _, out reason), reason);
                Assert.IsTrue(writer.TryTick(.1, new DiagnosticVitalsTickInput(moving: false, fire: 1), out reason), reason);
                var spent = actual.Stamina; var progress = runtime.Snapshot().ProgressSeconds;
                using (stale) using (var attempt = CommonParticipantGate.BeginAttempt()) Assert.IsFalse(stale.TryInstallUnderGate(runtime, attempt, out _));
                Assert.AreEqual(spent, actual.Stamina); Assert.AreEqual(progress, runtime.Snapshot().ProgressSeconds);
                Assert.AreEqual(1, runtime.EvidenceJournal.AcceptedStepCount); Assert.Less(actual.Health, 100);
            }
            finally { ResourceAuthorityPublication.ReplaceReader(resourceBefore); }
        }
    }
}
