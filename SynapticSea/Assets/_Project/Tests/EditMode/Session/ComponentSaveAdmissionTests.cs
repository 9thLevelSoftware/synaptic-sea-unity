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
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class ComponentSaveAdmissionTests : InfraDataTestBase
    {
        readonly List<RunSession> _sessions = new List<RunSession>();
        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUpEngine()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
        }

        [TearDown]
        public void DisposeSessions()
        {
            try { foreach (RunSession session in _sessions) session.Dispose(); _sessions.Clear(); }
            finally { CoreServices.Engine = _previousEngine; }
        }

        SessionHarness.Rig Boot(bool diagnostic)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            // Distinct actual clock inputs make independent A/B identities unambiguous.
            rig.Clock.Advance(_sessions.Count + 1);
            deps.EnableComponentIntegration = diagnostic;
            rig.Session = RunSession.Create(deps); _sessions.Add(rig.Session);
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            Assert.AreEqual(diagnostic, s.ComponentIntegrationEnabled);
            s.ThreatManager.Threats.Clear(); s.InventoryState.Items.Clear();
            s.PlayerProgression.Skills["fabrication"] = 4L;
            s.VitalsState.Stamina = s.VitalsState.MaxStamina;
            rig.Scene.PlayerPosition = new Vec3(1, .5, 2);
            s.HomeShip.LootedContainerIds.Add("admission-full-world-witness");
            return rig;
        }

        static string Detail(GdDict result) => result.GetString("reason") + ":" + result.GetString("detail");
        static string Exact(object value) => GdJson.Stringify(ComponentDomainCodec.Encode(new GdDict { { "value", V.DeepCopy(value) } }));
        static void Equal(object expected, object actual, string label) => Assert.AreEqual(Exact(expected), Exact(actual), label);

        static GdDict Owner(RunSession s)
        {
            GdDict owner = s.CaptureComponentDomain();
            Assert.IsTrue(DomainBundle.TryCreate(owner, out _, out string reason), reason);
            Assert.IsTrue(s.ValidateComponentDomainRestore(owner, out reason), reason);
            Assert.AreEqual(2L, owner.GetInt("schema_version"));
            return owner;
        }

        static GdDict RawParticipants(RunSession s) => new GdDict
        {
            { "inventory", s.InventoryState.GetSummary() }, { "progression", s.PlayerProgression.GetSummary() },
            { "crafting", s.CraftingState.GetSummary() }, { "field_crafting", s.FieldCraftingState.GetSummary() },
            { "spoilage", s.SpoilageState.GetSummary() }, { "training", s.TrainingEventBus.ToDict() }
        };

        sealed class Control
        {
            public SessionHarness.Rig Rig;
            public SaveCommitCoordinator Coordinator;
            public GdDict Payload, Selection, Owner, Participants;
            public Dictionary<string, string> Bytes;
        }

        static Control Admit(SessionHarness.Rig rig)
        {
            RunSession s = rig.Session;
            GdDict owner = Owner(s);
            Assert.IsTrue(s.RequestSaveToSlot("world", "world", "Admission control"), Detail(s.LastSaveResult));
            GdDict selected = s.SaveLoadService.SelectGeneration("world");
            Assert.IsTrue(selected.GetBool("ok"), Detail(selected));
            Assert.AreEqual(s.RunId, selected.GetString("run_id"));
            Assert.IsNotEmpty(selected.GetString("generation_id"));
            GdDict payload = selected.GetDictOrEmpty("payloads").DeepCopy();
            Assert.Greater(payload.GetArrayOrEmpty("artifacts").Count, 0);
            // Use the actual service's coordinator and terminal/admission authority, not a permissive fixture authority.
            SaveCommitCoordinator coordinator = s.SaveLoadService.ComponentCoordinator();
            GdDict positive = coordinator.ValidateSuppliedPayload(payload, s.RunId, "world");
            Assert.IsTrue(positive.GetBool("ok"), "Actual complete same-coordinator positive: " + Detail(positive));
            Equal(owner, Owner(s), "Positive control preserves owner");
            return new Control { Rig = rig, Coordinator = coordinator, Payload = payload, Selection = selected.DeepCopy(),
                Owner = owner, Participants = RawParticipants(s), Bytes = SlotPayloadBindingTests.Bytes(rig.Storage) };
        }

        static void Unchanged(Control control)
        {
            SlotPayloadBindingTests.SameBytes(control.Bytes, control.Rig.Storage);
            Equal(control.Selection, control.Rig.Session.SaveLoadService.SelectGeneration("world"), "Prior selected generation remains exact");
            Equal(control.Owner, Owner(control.Rig.Session), "Whole canonical owner remains exact");
            Equal(control.Participants, RawParticipants(control.Rig.Session), "Live participants remain exact");
        }
        static GdDict Parse(GdDict payload, string role)
        {
            GdDict result = GdJson.ParseString(payload.GetString(role + "_text")) as GdDict;
            Assert.IsNotNull(result); return result;
        }
        static void Write(GdDict payload, GdDict run, GdDict world)
        {
            payload["run_text"] = GdJson.Stringify(run);
            payload["world_text"] = GdJson.Stringify(world);
        }

        [TestCase("active", false)]
        [TestCase("home", false)]
        [TestCase("both", false)]
        [TestCase("active", true)]
        [TestCase("home", true)]
        [TestCase("both", true)]
        public void OriginalSchema2_RejectsPresentPaidClaim(string location, bool future)
        {
            Control control = Admit(Boot(true));
            GdDict bad = control.Payload.DeepCopy(), run = Parse(bad, "run"), world = Parse(bad, "world");
            GdDict home = world.GetDictOrEmpty("home_ship");
            Assert.IsFalse(run.GetDictOrEmpty("crafting_summary").Has("paid_craft"));
            Assert.IsFalse(home.GetDictOrEmpty("crafting_summary").Has("paid_craft"));
            object claim = future ? (object)new GdDict { { "schema_version", 999L } } : false;
            if (location != "home") run.GetDictOrEmpty("crafting_summary")["paid_craft"] = V.DeepCopy(claim);
            if (location != "active") home.GetDictOrEmpty("crafting_summary")["paid_craft"] = V.DeepCopy(claim);
            Write(bad, run, world);
            GdDict result = control.Coordinator.ValidateSuppliedPayload(bad, control.Rig.Session.RunId, "world");
            Unchanged(control);
            Assert.IsFalse(result.GetBool("ok"), "Unsupported present paid claim must be refused: " + Detail(result));
        }
    }
}
