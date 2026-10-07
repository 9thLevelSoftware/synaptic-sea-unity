using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    // First SAVE wave: provisioned real catalog, actual session saves, MemoryStorage only.
    // ROOT executes RED. Later numeric/context/authority waves append separate fixtures.
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class ComponentSaveCapabilityTests : InfraDataTestBase
    {
        const string Witness = "paid-save-complete-world-witness";
        readonly List<RunSession> _sessions = new List<RunSession>();
        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUpSaveEngine()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
        }

        [TearDown]
        public void DisposeSessions()
        {
            try
            {
                foreach (RunSession session in _sessions) session.Dispose();
                _sessions.Clear();
            }
            finally { CoreServices.Engine = _previousEngine; }
        }

        SessionHarness.Rig Boot(bool components = false)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.EnableComponentIntegration = components;
            rig.Session = RunSession.Create(deps);
            _sessions.Add(rig.Session);
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, "Boot prerequisite: " + s.LastFailureReason);
            Assert.AreEqual(components, s.ComponentIntegrationEnabled);
            s.ThreatManager.Threats.Clear();
            s.InventoryState.Items.Clear();
            s.PlayerProgression.Skills["fabrication"] = 4L;
            s.VitalsState.Stamina = s.VitalsState.MaxStamina;
            s.HomeShip.LootedContainerIds.Add(Witness);
            rig.Scene.PlayerPosition = new Vec3(1, .5, 2);
            return rig;
        }

        static void Equal(object expected, object actual, string message)
            => Assert.IsTrue(V.VariantEquals(expected, actual), message);
        static string Detail(GdDict result) => "reason=" + result.GetString("reason") + ", detail=" + result.GetString("detail");

        [Test]
        public void DefaultLegacy_RequestSaveKeepsWorld4Control()
        {
            Assert.IsFalse(new RunSessionDeps().EnableComponentIntegration);
            SessionHarness.Rig rig = Boot();
            Assert.IsTrue(rig.Session.RequestSave());
            GdDict world = (GdDict)GdJson.ParseString(rig.Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE));
            Assert.AreEqual("world-4", world.GetString("slice_version"));
            Assert.IsFalse(world.GetDictOrEmpty("home_ship").GetDictOrEmpty("crafting_summary").Has("paid_craft"));
            Assert.IsFalse(rig.Storage.DirExists(SaveLoadService.ComponentGenerationRoot));
            Assert.IsTrue(rig.Session.RequestLoad());
            Assert.IsTrue(rig.Session.HomeShip.LootedContainerIds.Contains(Witness));
        }

        [Test]
        public void DiagnosticSchema2_FullPayloadSaveReloadControl()
        {
            SessionHarness.Rig rig = Boot(components: true);
            RunSession s = rig.Session;
            GdDict owner = s.CaptureComponentDomain();
            Assert.AreEqual(2L, owner.GetInt("schema_version"));
            Assert.IsTrue(DomainBundle.TryCreate(owner, out _, out string reason), reason);
            Assert.IsTrue(s.ValidateComponentDomainRestore(owner, out reason), reason);
            GdDict capture = SavePayloadAssembler.Build(s, "world", "world");
            Assert.IsTrue(capture.GetBool("ok"), Detail(capture));
            Assert.IsTrue(s.RequestSaveToSlot("world", "world", "Original schema2"), Detail(s.LastSaveResult));
            GdDict selected = s.SaveLoadService.SelectGeneration("world");
            Assert.IsTrue(selected.GetBool("ok"), Detail(selected));
            GdDict payload = selected.GetDictOrEmpty("payloads");
            GdDict run = (GdDict)GdJson.ParseString(payload.GetString("run_text"));
            GdDict world = (GdDict)GdJson.ParseString(payload.GetString("world_text"));
            Assert.AreEqual("gate2-current-run-7", run.GetString("slice_version"));
            Assert.AreEqual("world-5", world.GetString("slice_version"));
            Assert.IsFalse(run.GetDictOrEmpty("crafting_summary").Has("paid_craft"));
            Assert.IsTrue(ComponentDomainCodec.TryDecode(world.GetDictOrEmpty("component_domain"), out GdDict decoded, out reason), reason);
            Equal(owner, decoded, "Original empty-craft schema2 owner remains readable.");
            s.HomeShip.LootedContainerIds.Clear();
            Assert.IsTrue(s.ApplySelectedGeneration(selected), Detail(s.LastSaveResult));
            Assert.IsTrue(s.HomeShip.LootedContainerIds.Contains(Witness));
            Assert.AreEqual(new Vec3(1, .5, 2), rig.Scene.PlayerPosition);
        }

        [TestCase("run", "gate2-current-run-7")]
        [TestCase("world", "world-5")]
        public void DefaultCoordinator_RejectsFutureSnapshotVersion(string role, string version)
        {
            var storage = new MemoryStorage();
            SaveCommitCoordinator coordinator = GenerationFixtures.Coordinator(storage);
            GdDict good = GenerationFixtures.Request();
            GdDict accepted = coordinator.Commit(good, good.GetString("run_id"), good.GetString("slot_id"));
            Assert.IsTrue(accepted.GetBool("ok"), "Valid full payload before future mutation: " + Detail(accepted));
            var bytes = SlotPayloadBindingTests.Bytes(storage);
            GdDict bad = good.DeepCopy();
            GdDict document = (GdDict)GdJson.ParseString(bad.GetString(role + "_text"));
            document["slice_version"] = version;
            bad[role + "_text"] = GdJson.Stringify(document);
            GdDict refused = coordinator.ValidateSuppliedPayload(bad, bad.GetString("run_id"), bad.GetString("slot_id"));
            Assert.IsFalse(refused.GetBool("ok"));
            Assert.AreEqual("unsupported_schema", refused.GetString("reason"));
            SlotPayloadBindingTests.SameBytes(bytes, storage);
        }
    }
}
