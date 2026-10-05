using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    // Engine-free engineering fixture, never a physical route or survival acceptance claim.
    [NonParallelizable]
    public class DiagnosticAuxiliaryPublisherTests : InfraDataTestBase
    {
        sealed class RecordingResources : IResourceReader, IResourceDirectoryReader
        {
            readonly IResourceReader _reader;
            readonly IResourceDirectoryReader _directories;
            readonly Dictionary<string,string> _texts = new Dictionary<string,string>(StringComparer.Ordinal);
            readonly Dictionary<string,IReadOnlyList<string>> _dirs = new Dictionary<string,IReadOnlyList<string>>(StringComparer.Ordinal);
            internal RecordingResources(IResourceReader reader) { _reader = reader; _directories = (IResourceDirectoryReader)reader; }
            public bool Exists(string path) { bool exists = _reader.Exists(path); _texts[path] = exists ? _reader.ReadText(path) : null; return exists; }
            public string ReadText(string path) { string text = _reader.ReadText(path); _texts[path] = text; return text; }
            public bool DirExists(string path) { bool exists = _directories.DirExists(path); if (!exists) _dirs[path] = null; else ListFiles(path); return exists; }
            public IReadOnlyList<string> ListFiles(string path)
            {
                var names = _directories.ListFiles(path).ToArray(); _dirs[path] = names;
                foreach (string name in names) Exists(path.TrimEnd('/') + "/" + name);
                return names;
            }
            internal ImmutableResourceAuthority Freeze() => new ImmutableResourceAuthority(_texts, _dirs);
        }
        sealed class Rig : IDisposable
        {
            internal RunSession Session;
            internal GdDict Before, Candidate, Effect;
            internal InventoryState Inventory;
            internal PlayerProgressionState Progression;
            internal TrainingEventBus Training;
            internal DiagnosticAuxiliaryPublisher Publisher;
            internal ResourceAuthorityLease Lease;
            internal string ServiceId;
            public void Dispose() => Session?.Dispose();
        }
        Rig Fixture(bool utility = true)
        {
            var resources = new RecordingResources(CoreServices.Resources); CoreServices.Resources = resources; CatalogRegistry.Clear();
            var deps = SessionHarness.GoldenDeps(out var scene); SessionHarness.OverlayGamePlayability(deps);
            const string path = "res://data/diagnostics/earned-services-home-v1/";
            deps.LayoutPath = path + "layout.json"; deps.GameplaySlicePath = path + "gameplay_slice.json"; deps.BlueprintPath = path + "blueprint.json";
            deps.EnablePaidCrafting = true; deps.EnableManualStudy = true; deps.EnableAuxiliaryServices = true;
            var s = RunSession.Create(deps); var result = new Rig { Session = s };
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            var supply = s.LootContainers.Single(p => p.ContainerId == "start_supply_a"); scene.Scene.PlayerPosition = supply.GlobalPosition;
            Assert.IsTrue(supply.TryInteract(supply.GlobalPosition));
            if (s.EquipmentState.GetEquipped("primary_hand") == "crowbar") s.UnequipToInventory("primary_hand");
            string id = utility ? "maintenance_fabricator_feed_01" : "home_spare_harness_rack_02";
            result.ServiceId = id;
            if (utility)
            {
                var kit = s.LootContainers.Single(p => p.ContainerId == "home_service_kit_01");
                scene.Scene.PlayerPosition = kit.GlobalPosition; Assert.IsTrue(kit.TryInteract(kit.GlobalPosition));
                Assert.GreaterOrEqual(s.InventoryState.GetQuantity("scrap_metal"), 1);
                Assert.GreaterOrEqual(s.InventoryState.GetQuantity("wiring_bundle"), 1);
            }
            var descriptor = s.GetAuxiliaryServiceState().GetDictOrEmpty("descriptors").GetDictOrEmpty(id);
            double duration = descriptor.GetFloat("required_seconds");
            Assert.AreEqual(utility ? 12.0 : 8.0, duration);
            Assert.AreEqual(utility ? "utility" : "recovery_rack", descriptor.GetString("kind"));
            var costs = descriptor.GetDictOrEmpty("materials_consumed");
            Assert.AreEqual(utility ? 2 : 0, costs.Count);
            if (utility)
            {
                Assert.AreEqual(1L, costs.GetInt("scrap_metal")); Assert.AreEqual(1L, costs.GetInt("wiring_bundle"));
                Assert.AreEqual(60L, descriptor.GetDictOrEmpty("reward").GetInt("repair_xp"));
            }
            scene.Scene.PlayerPosition = s.AuxiliaryServicePoints.Single(p => p.ServiceId == id).GlobalPosition;
            Assert.IsTrue(s.RequestAuxiliaryService(id).GetBool("committed")); Assert.IsTrue(s.BeginWorkHold());
            s.ComponentStageHook = stage => { if (stage == "live_inventory" && s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds") == duration) throw new InvalidOperationException("fixture retains unpaid ready boundary"); };
            var tick = typeof(RunSession).GetMethod("TickWorkAction", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < 100 && s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds") < duration; i++)
            {
                if (s.VitalsState.Stamina < 25)
                {
                    s.PauseAuxiliaryService("rest"); s.EndWorkHold();
                    for (int rest = 0; rest < 2000 && s.VitalsState.Stamina < 75; rest++) s.VitalsState.Tick(.1, new GdDict { { "moving", false } });
                    Assert.IsTrue(s.RequestAuxiliaryService(id).GetBool("committed")); Assert.IsTrue(s.BeginWorkHold());
                }
                tick.Invoke(s, new object[] { .5 });
            }
            s.ComponentStageHook = null;
            Assert.AreEqual(duration, s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds"));
            result.Before = s.CapturePaidCraftingDomain();
            var state = AuxiliaryServiceState.State(result.Before);
            var command = new GdDict { { "command_id", "aux:" + state.GetString("run_id") + ":" + (result.Before.GetInt("command_sequence") + 1) },
                { "operation", "aux_complete" }, { "run_id", state.GetString("run_id") }, { "actor_id", state.GetString("actor_id") },
                { "service_id", id }, { "delta_seconds", 0.0 }, { "elapsed_seconds", 0.0 }, { "speed", 1.0 },
                { "stamina_before", 0.0 }, { "stamina_after", 0.0 }, { "reason", "" } };
            var coordinator = new DomainTransactionCoordinator(result.Before);
            var prepared = coordinator.PrepareAuxiliary(command, candidate =>
            {
                var effect = AuxiliaryServiceState.Apply(candidate, "aux_complete", id);
                candidate["command_sequence"] = candidate.GetInt("command_sequence") + 1;
                typeof(RunSession).GetMethod("SetPaidProjections", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(s, new object[] { candidate });
                result.Effect = effect.DeepCopy(); return effect;
            });
            Assert.IsTrue(prepared.GetBool("ok"), PaidSnapshotCodec.Stringify(prepared)); result.Candidate = prepared.GetDictOrEmpty("candidate");
            // Explicitly owned immutable publication after recording the complete fixture's admission reads.
            ResourceAuthorityPublication.Publish(resources.Freeze()); Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out result.Lease, out _));
            var p = result.Before.GetDictOrEmpty("participating_state");
            result.Inventory = InventoryState.CreateTracked(ItemDefs.LoadDefinitions()); Assert.IsTrue(result.Inventory.ApplySummary(p.GetDictOrEmpty("inventory")));
            result.Progression = PlayerProgressionState.CreateTracked();
            var classId = p.GetDictOrEmpty("progression").GetString("class_id");
            result.Progression.Configure(ClassDefinition.LoadAll()[classId], PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            Assert.IsTrue(result.Progression.ApplySummary(p.GetDictOrEmpty("progression")));
            result.Training = TrainingEventBus.CreateTracked(); Assert.IsTrue(result.Training.ApplySummary(p.GetDictOrEmpty("training")));
            result.Publisher = new DiagnosticAuxiliaryPublisher(result.Before, result.Inventory, result.Progression, result.Training);
            return result;
        }
        static void AssertCatalogUtilityReward(Rig f)
        {
            var before = f.Before.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression");
            var after = f.Candidate.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression");
            var expected = new PlayerProgressionState();
            expected.Configure(ClassDefinition.LoadAll()[before.GetString("class_id")],
                PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            Assert.IsTrue(PaidCraftRewardProof.CopyProgressionExact(expected, before));
            // GrantXp reports a level change; the exact summary and positive banked reward below prove the grant.
            expected.GrantXp("repair", 60, false);
            Assert.IsTrue(V.VariantEquals(expected.GetSummary(), after), "Actual completion must use exact catalog class policy.");
            Assert.IsTrue(after.GetDictOrEmpty("skills").GetInt("repair") > before.GetDictOrEmpty("skills").GetInt("repair") ||
                after.GetDictOrEmpty("skill_xp").GetInt("repair") > before.GetDictOrEmpty("skill_xp").GetInt("repair") ||
                after.GetDictOrEmpty("skill_xp_fractional").GetFloat("repair") > before.GetDictOrEmpty("skill_xp_fractional").GetFloat("repair"),
                "Fixture must witness a positive earned repair reward.");
            Assert.IsTrue(V.VariantEquals(before.Get("cross_training"), after.Get("cross_training")));
            var trainingBefore = f.Before.GetDictOrEmpty("participating_state").GetDictOrEmpty("training");
            var trainingAfter = f.Candidate.GetDictOrEmpty("participating_state").GetDictOrEmpty("training");
            Assert.AreEqual(trainingBefore.GetInt("event_count") + 1, trainingAfter.GetInt("event_count"));
            var row = (GdDict)trainingAfter.GetArrayOrEmpty("log")[trainingAfter.GetArrayOrEmpty("log").Count - 1];
            Assert.AreEqual("auxiliary_utility_repair", row.GetString("event_id")); Assert.AreEqual(f.ServiceId, row.GetString("target_id"));
            Assert.AreEqual(60L, row.GetInt("base_xp")); Assert.IsTrue(row.GetBool("receipt_owned"));
            Assert.IsFalse(row.GetBool("is_cross_training")); Assert.IsFalse(row.GetBool("gated"));
        }
        [Test] public void ExactCompletionOnceAndNotificationFailureStayCommittedWithoutSurvivalWrite()
        {
            using (var f = Fixture())
            {
                AssertCatalogUtilityReward(f);
                var items = f.Inventory.Items; var skills = f.Progression.Skills; var bus = f.Training;
                double health = f.Session.VitalsState.Health, stamina = f.Session.VitalsState.Stamina;
                var cert = f.Publisher.Prepare(f.Candidate, f.Effect, f.Lease); int notifications = 0;
                Assert.AreEqual(DiagnosticAuxiliaryPublisher.PublishResult.CommittedPresentationFailed,
                    f.Publisher.PublishAndNotify(cert, true, () => { notifications++; throw new Exception("presentation"); }));
                Assert.AreSame(items, f.Inventory.Items); Assert.AreSame(skills, f.Progression.Skills); Assert.AreSame(bus, f.Training);
                var after = f.Candidate.GetDictOrEmpty("participating_state");
                Assert.IsTrue(V.VariantEquals(after.Get("inventory"), f.Inventory.GetSummary()));
                Assert.IsTrue(V.VariantEquals(after.Get("progression"), f.Progression.GetSummary()));
                Assert.IsTrue(V.VariantEquals(after.Get("training"), f.Training.ToDict()));
                var beforeParticipants = f.Before.GetDictOrEmpty("participating_state");
                var beforeItems = beforeParticipants.GetDictOrEmpty("inventory").GetDictOrEmpty("items");
                Assert.AreEqual(beforeItems.GetInt("scrap_metal") - 1, f.Inventory.GetQuantity("scrap_metal"));
                Assert.AreEqual(beforeItems.GetInt("wiring_bundle") - 1, f.Inventory.GetQuantity("wiring_bundle"));
                var paidRow = (GdDict)f.Training.GetLog()[f.Training.GetLog().Count - 1];
                Assert.AreEqual(60L, paidRow.GetInt("base_xp")); Assert.IsTrue(paidRow.GetBool("receipt_owned"));
                Assert.AreEqual(beforeParticipants.GetDictOrEmpty("training").GetInt("xp_total"), f.Training.GetTotalXpDelivered());
                Assert.IsTrue(AuxiliaryServiceState.State(f.Publisher.CaptureOwnerSnapshot()).GetDictOrEmpty("services")
                    .GetDictOrEmpty(f.ServiceId).GetBool("hardware_ready"));
                Assert.AreEqual(health, f.Session.VitalsState.Health); Assert.AreEqual(stamina, f.Session.VitalsState.Stamina);
                Assert.AreEqual(DiagnosticAuxiliaryPublisher.PublishResult.AlreadyConsumed, f.Publisher.PublishAndNotify(cert, true, () => notifications++));
                Assert.AreEqual(1, notifications);
            }
        }
        [Test] public void RackReleaseReservesFourPlusFourWithoutInventoryXpOrTrainingDrain()
        {
            using (var f = Fixture(utility: false))
            {
                var before = f.Before.GetDictOrEmpty("participating_state");
                var cert = f.Publisher.Prepare(f.Candidate, f.Effect, f.Lease);
                Assert.AreEqual(DiagnosticAuxiliaryPublisher.PublishResult.Committed, f.Publisher.Publish(cert, true));
                Assert.IsTrue(V.VariantEquals(before.Get("inventory"), f.Inventory.GetSummary()));
                Assert.IsTrue(V.VariantEquals(before.Get("progression"), f.Progression.GetSummary()));
                Assert.IsTrue(V.VariantEquals(before.Get("training"), f.Training.ToDict()));
                Assert.IsNull(f.Effect.Get("training_record"));
                var service = AuxiliaryServiceState.State(f.Publisher.CaptureOwnerSnapshot()).GetDictOrEmpty("services").GetDictOrEmpty(f.ServiceId);
                Assert.IsTrue(service.GetBool("released"));
                Assert.IsTrue(service.Has("hardware_ready")); Assert.AreEqual(false, service.Get("hardware_ready"));
                Assert.IsNotEmpty(service.GetString("completion_commit_id"));
                Assert.AreEqual(4L, service.GetDictOrEmpty("remaining").GetInt("scrap_metal"));
                Assert.AreEqual(4L, service.GetDictOrEmpty("remaining").GetInt("wiring_bundle"));
                Assert.AreEqual(0L, service.GetDictOrEmpty("accepted").GetInt("scrap_metal"));
                Assert.AreEqual(0L, service.GetDictOrEmpty("accepted").GetInt("wiring_bundle"));
            }
        }
        [Test] public void CatalogProvedCompletionDoesNotRegrantThroughLiveHubMultiplier()
        {
            using (var f = Fixture())
            {
                AssertCatalogUtilityReward(f);
                f.Progression.XpMultipliers["technical"] = 10.0;
                var livePolicy = f.Progression.CaptureTrackedReplay(out _); livePolicy.GrantXp("repair", 60);
                var proved = f.Candidate.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression");
                Assert.IsFalse(V.VariantEquals(proved, livePolicy.GetSummary()), "Fixture must distinguish live policy from catalog reward.");
                var cert = f.Publisher.Prepare(f.Candidate, f.Effect, f.Lease);
                Assert.AreEqual(DiagnosticAuxiliaryPublisher.PublishResult.Committed, f.Publisher.Publish(cert, true));
                Assert.IsTrue(V.VariantEquals(proved, f.Progression.GetSummary()));
                Assert.AreEqual(10.0, f.Progression.XpMultipliers.GetFloat("technical"));
                var committed = f.Progression.GetSummary();
                Assert.AreEqual(DiagnosticAuxiliaryPublisher.PublishResult.AlreadyConsumed, f.Publisher.Publish(cert, true));
                Assert.IsTrue(V.VariantEquals(committed, f.Progression.GetSummary()));
            }
        }
        [TestCase("inventory")][TestCase("progression")][TestCase("training")][TestCase("resource")][TestCase("eligibility")]
        public void ChangedReadSetRefusesWithoutOwnerOrOtherParticipantWrite(string change)
        {
            using (var f = Fixture())
            {
                var cert = f.Publisher.Prepare(f.Candidate, f.Effect, f.Lease);
                if (change == "inventory") f.Inventory.Items["scrap_metal"] = f.Inventory.GetQuantity("scrap_metal") + 1;
                if (change == "progression") f.Progression.SkillXp["repair"] = 1L;
                if (change == "training") f.Training.Reset();
                if (change == "resource") CatalogRegistry.Clear();
                var before = f.Publisher.CaptureSnapshot();
                Assert.AreEqual(DiagnosticAuxiliaryPublisher.PublishResult.Refused, f.Publisher.Publish(cert, change != "eligibility"));
                Assert.IsTrue(V.VariantEquals(before, f.Publisher.CaptureSnapshot()));
                Assert.AreEqual(DiagnosticAuxiliaryPublisher.PublishResult.AlreadyConsumed, f.Publisher.Publish(cert, true));
            }
        }
        [TestCase("AfterInventory")]
        [TestCase("AfterProgression")]
        [TestCase("AfterTraining")]
        [TestCase("AfterOwner")]
        public void ImmediateAssignmentFaultRestoresOnlyAttemptedContent(string faultName)
        {
            using (var f = Fixture())
            {
                var cert = f.Publisher.Prepare(f.Candidate, f.Effect, f.Lease);
                var before = f.Publisher.CaptureSnapshot();
                var fault = (DiagnosticAuxiliaryPublisher.DiagnosticFault)Enum.Parse(typeof(DiagnosticAuxiliaryPublisher.DiagnosticFault), faultName);
                double stamina = f.Session.VitalsState.Stamina;
                Assert.Throws<InvalidOperationException>(() => f.Publisher.Publish(cert, true, fault));
                Assert.IsTrue(V.VariantEquals(before, f.Publisher.CaptureSnapshot()));
                Assert.AreEqual(stamina, f.Session.VitalsState.Stamina);
                Assert.AreEqual(DiagnosticAuxiliaryPublisher.PublishResult.AlreadyConsumed, f.Publisher.Publish(cert, true));
            }
        }
        [Test] public void WrongThreadAndUnsupportedCallbackPolicyRefuse()
        {
            using (var f = Fixture())
            {
                var cert = f.Publisher.Prepare(f.Candidate, f.Effect, f.Lease);
                var foreign = new DiagnosticAuxiliaryPublisher(f.Before, f.Inventory, f.Progression, f.Training);
                Assert.AreEqual(DiagnosticAuxiliaryPublisher.PublishResult.Refused, foreign.Publish(cert, true));
                var result = DiagnosticAuxiliaryPublisher.PublishResult.Committed;
                var thread = new Thread(() => result = f.Publisher.Publish(cert, true)); thread.Start(); thread.Join();
                Assert.AreEqual(DiagnosticAuxiliaryPublisher.PublishResult.Refused, result);
                f.Inventory.ComponentMass = () => throw new Exception("must not invoke");
                Assert.Throws<InvalidOperationException>(() => f.Publisher.Prepare(f.Candidate, f.Effect, f.Lease));
            }
        }
    }
}
