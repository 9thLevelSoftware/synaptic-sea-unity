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
    public class PaidCraftSaveAdmissionTests : InfraDataTestBase
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

        SessionHarness.Rig Boot(bool diagnostic, bool paid)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            // Distinct actual clock inputs make independent A/B identities unambiguous.
            rig.Clock.Advance(_sessions.Count + 1);
            deps.EnableComponentIntegration = diagnostic; deps.EnablePaidCrafting = paid;
            rig.Session = RunSession.Create(deps); _sessions.Add(rig.Session);
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            Assert.AreEqual(diagnostic, s.ComponentIntegrationEnabled);
            Assert.AreEqual(paid, s.PaidCraftingEnabled);
            s.ThreatManager.Threats.Clear(); s.InventoryState.Items.Clear();
            s.PlayerProgression.Skills["fabrication"] = 4L;
            s.VitalsState.Stamina = s.VitalsState.MaxStamina;
            rig.Scene.PlayerPosition = new Vec3(1, .5, 2);
            s.HomeShip.LootedContainerIds.Add("admission-full-world-witness");
            return rig;
        }

        static string Detail(GdDict result) => result.GetString("reason") + ":" + result.GetString("detail");
        static GdDict Paid(GdDict owner) => owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting");
        static string Exact(object value) => GdJson.Stringify(ComponentDomainCodec.Encode(new GdDict { { "value", V.DeepCopy(value) } }));
        static void Equal(object expected, object actual, string label) => Assert.AreEqual(Exact(expected), Exact(actual), label);

        static GdDict Owner(RunSession s)
        {
            GdDict owner = s.PaidCraftingEnabled ? s.CapturePaidCraftingDomain() : s.CaptureComponentDomain();
            Assert.IsTrue(DomainBundle.TryCreate(owner, out _, out string reason), reason);
            bool accepted = s.PaidCraftingEnabled ? s.ValidatePaidCraftingRestore(owner, out reason) : s.ValidateComponentDomainRestore(owner, out reason);
            Assert.IsTrue(accepted, reason);
            Assert.AreEqual(s.PaidCraftingEnabled ? 3L : 2L, owner.GetInt("schema_version"));
            return owner;
        }

        static void StartRunning(RunSession s)
        {
            const string recipeId = "weld_plating", kind = "workbench";
            GdDict recipe = s.CraftingState.GetRecipe(recipeId);
            Assert.IsFalse(recipe.IsEmpty);
            Assert.IsTrue(s.CraftingStations.Any(st => st.IsValid && st.StationKind == kind && ReferenceEquals(st.Parent, s.HomeShip.SceneRoot)));
            var station = s.CraftingState.GetStation(kind);
            Assert.IsNotNull(station); station.SetPower(true);
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
            {
                long count = V.I64(ingredient.Value);
                Assert.AreEqual(count, s.InventoryState.AddItem(V.Str(ingredient.Key), count));
            }
            GdDict started = s.RequestPaidCraft(kind, recipeId, "admission-paid-start");
            Assert.IsTrue(started.GetBool("ok"), Detail(started));
            Assert.IsTrue(started.GetBool("committed"));
            Assert.IsNotEmpty(started.GetString("commit_id"));
            GdDict initial = Paid(Owner(s)).GetDictOrEmpty("jobs").GetDictOrEmpty(started.GetString("job_id"));
            Assert.AreEqual("paid", initial.GetString("input_state"));
            Assert.IsNotEmpty(initial.GetString("payment_commit_id"));
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
                Assert.AreEqual(0L, s.InventoryState.GetQuantity(V.Str(ingredient.Key)));
            s.AdvanceCrafting(initial.GetFloat("required_seconds") / 4.0);
            GdDict running = Paid(Owner(s)).GetDictOrEmpty("jobs").GetDictOrEmpty(started.GetString("job_id"));
            Assert.AreEqual("running", running.GetString("status"));
            Assert.Greater(running.GetFloat("progress_seconds"), 0.0);
            Assert.Less(running.GetFloat("progress_seconds"), running.GetFloat("required_seconds"));
            foreach (string key in new[] { "payment_commit_id", "consumed", "required_seconds", "quality_score" })
                Equal(initial.Get(key), running.Get(key), "Actual paid work retains " + key);
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

        static GdDict Parse(GdDict payload, string role, bool paid)
        {
            GdDict result = paid ? PaidSnapshotCodec.Parse(payload.GetString(role + "_text")) : GdJson.ParseString(payload.GetString(role + "_text")) as GdDict;
            Assert.IsNotNull(result); return result;
        }
        static void Write(GdDict payload, GdDict run, GdDict world, bool paid)
        {
            payload["run_text"] = paid ? PaidSnapshotCodec.Stringify(run) : GdJson.Stringify(run);
            payload["world_text"] = paid ? PaidSnapshotCodec.Stringify(world) : GdJson.Stringify(world);
        }
        static GdDict DecodeOwner(GdDict run)
        {
            Assert.IsTrue(ComponentDomainCodec.TryDecode(run.GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft").GetDictOrEmpty("domain"),
                out GdDict owner, out string reason), reason);
            return owner;
        }
        static void StampOwner(GdDict run, GdDict world, GdDict owner, bool diagnostic)
        {
            GdDict encoded = ComponentDomainCodec.Encode(owner), home = world.GetDictOrEmpty("home_ship");
            run.GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft")["domain"] = encoded.DeepCopy();
            home.GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft")["domain"] = encoded.DeepCopy();
            if (diagnostic)
                foreach (GdDict snapshot in new[] { run, home, world }) snapshot["component_domain"] = encoded.DeepCopy();
        }

        [TestCase("active", false)]
        [TestCase("home", false)]
        [TestCase("both", false)]
        [TestCase("active", true)]
        [TestCase("home", true)]
        [TestCase("both", true)]
        public void OriginalSchema2_RejectsPresentPaidClaim(string location, bool future)
        {
            Control control = Admit(Boot(true, false));
            GdDict bad = control.Payload.DeepCopy(), run = Parse(bad, "run", false), world = Parse(bad, "world", false);
            GdDict home = world.GetDictOrEmpty("home_ship");
            Assert.IsFalse(run.GetDictOrEmpty("crafting_summary").Has("paid_craft"));
            Assert.IsFalse(home.GetDictOrEmpty("crafting_summary").Has("paid_craft"));
            object claim = future ? (object)new GdDict { { "schema_version", 999L } } : false;
            if (location != "home") run.GetDictOrEmpty("crafting_summary")["paid_craft"] = V.DeepCopy(claim);
            if (location != "active") home.GetDictOrEmpty("crafting_summary")["paid_craft"] = V.DeepCopy(claim);
            Write(bad, run, world, false);
            GdDict result = control.Coordinator.ValidateSuppliedPayload(bad, control.Rig.Session.RunId, "world");
            Unchanged(control);
            Assert.IsFalse(result.GetBool("ok"), "Unsupported present paid claim must be refused: " + Detail(result));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PaidOwner_RejectsDifferentAdmittedOuterRun(bool diagnostic)
        {
            SessionHarness.Rig rigA = Boot(diagnostic, true); StartRunning(rigA.Session);
            Control source = Admit(rigA);
            Control target = Admit(Boot(diagnostic, true));
            Assert.AreNotEqual(source.Rig.Session.RunId, target.Rig.Session.RunId);
            GdDict bad = source.Payload.DeepCopy(), run = Parse(bad, "run", true), world = Parse(bad, "world", true);
            string targetRun = target.Rig.Session.RunId;
            bad["run_id"] = targetRun; run["run_id"] = targetRun; world["run_id"] = targetRun; world.GetDictOrEmpty("home_ship")["run_id"] = targetRun;
            Write(bad, run, world, true);
            GdDict keptOwner = DecodeOwner(run);
            Equal(source.Owner, keptOwner, "Run A's complete paid work/payment/history remains untouched");
            Assert.AreEqual(source.Rig.Session.RunId, Paid(keptOwner).GetString("run_id"));
            Assert.IsTrue(DomainBundle.TryCreate(keptOwner, out _, out string reason), reason);
            Equal(source.Payload.Get("artifacts"), bad.Get("artifacts"), "Artifacts are not rewritten");
            GdDict result = target.Coordinator.ValidateSuppliedPayload(bad, targetRun, "world");
            Unchanged(source); Unchanged(target);
            Assert.IsFalse(result.GetBool("ok"), "Canonical run A cannot be admitted under live outer run B: " + Detail(result));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PaidOwner_RejectsDifferentActorWithValidEmptyDomain(bool diagnostic)
        {
            Control control = Admit(Boot(diagnostic, true));
            GdDict changedOwner = control.Owner.DeepCopy();
            Assert.IsTrue(Paid(changedOwner).GetDictOrEmpty("jobs").IsEmpty);
            Assert.AreEqual(RunSession.PLAYER_LOCAL_ID, Paid(changedOwner).GetString("actor_id"));
            Paid(changedOwner)["actor_id"] = "another_actor";
            Assert.IsTrue(DomainBundle.TryCreate(changedOwner, out _, out string reason), "Mutant remains a valid empty domain: " + reason);
            GdDict bad = control.Payload.DeepCopy(), run = Parse(bad, "run", true), world = Parse(bad, "world", true);
            StampOwner(run, world, changedOwner, diagnostic); Write(bad, run, world, true);
            Equal(changedOwner, DecodeOwner(run), "Exact changed actor survives the typed transport");
            GdDict result = control.Coordinator.ValidateSuppliedPayload(bad, control.Rig.Session.RunId, "world");
            Unchanged(control);
            Assert.IsFalse(result.GetBool("ok"), "Non-player actor cannot own this generation: " + Detail(result));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PaidOwner_WrongSaveModeRemainsRefusedControl(bool diagnostic)
        {
            Control control = Admit(Boot(diagnostic, true));
            GdDict bad = control.Payload.DeepCopy(), run = Parse(bad, "run", true), world = Parse(bad, "world", true);
            string wrongMode = diagnostic ? PaidSnapshotCodec.OrdinaryMode : PaidSnapshotCodec.DiagnosticMode;
            bad.GetDictOrEmpty("binding")["save_mode"] = wrongMode;
            foreach (GdDict snapshot in new[] { run, world.GetDictOrEmpty("home_ship") })
                snapshot.GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft")["save_mode"] = wrongMode;
            Write(bad, run, world, true);
            Equal(control.Owner, DecodeOwner(run), "Wrong mode control does not corrupt owner structure");
            GdDict result = control.Coordinator.ValidateSuppliedPayload(bad, control.Rig.Session.RunId, "world");
            Unchanged(control);
            Assert.IsFalse(result.GetBool("ok"), "Existing exact mode guard: " + Detail(result));
            Assert.AreEqual("binding_mismatch", result.GetString("reason"));
        }
    }
}

