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
    public class PaidCraftSaveMirrorTests : InfraDataTestBase
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

        static void StartRunning(RunSession s, string commandId = "mirror-paid-start")
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
            GdDict started = s.RequestPaidCraft(kind, recipeId, commandId);
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


        [TestCase(false)]
        [TestCase(true)]
        public void HomePose_RejectsOneMirrorDoubleChangedToLong(bool diagnostic)
        {
            SessionHarness.Rig rig = Boot(diagnostic, true); StartRunning(rig.Session);
            Control control = Admit(rig);
            GdDict bad = control.Payload.DeepCopy(), run = Parse(bad, "run", true), world = Parse(bad, "world", true);
            Assert.AreEqual("", world.GetString("current_location"), "Genuine home context");
            GdArray activePose = run.GetArrayOrEmpty("player_position");
            GdArray homePose = world.GetDictOrEmpty("home_ship").GetArrayOrEmpty("player_position");
            GdArray boundPose = bad.GetDictOrEmpty("binding").GetArrayOrEmpty("player_local_pose");
            Assert.AreEqual(3, homePose.Count); Assert.AreEqual(3, activePose.Count); Assert.AreEqual(3, boundPose.Count);
            Assert.IsTrue(homePose[0] is double); Assert.AreEqual(1.0, (double)homePose[0]);
            Equal(activePose, homePose, "Original mirrors match in value and type");
            Equal(activePose, boundPose, "Actual binding pose is exact");
            homePose[0] = 1L;
            Assert.IsTrue(homePose[0] is long); Assert.IsTrue(activePose[0] is double);
            Assert.IsTrue(V.VariantEquals(activePose, homePose), "Expose the existing coercive comparison");
            Assert.AreNotEqual(Exact(activePose), Exact(homePose), "Mutant changes an exact numeric tag");
            Equal(control.Owner, DecodeOwner(run), "Paid authority and genuine payment remain untouched");
            Write(bad, run, world, true);
            GdDict reread = Parse(bad, "world", true);
            Assert.IsTrue(reread.GetDictOrEmpty("home_ship").GetArrayOrEmpty("player_position")[0] is long,
                "Production reader preserves the precise mutated tag");
            Equal(control.Payload.Get("binding"), bad.Get("binding"), "Binding untouched");
            Equal(control.Payload.Get("artifacts"), bad.Get("artifacts"), "Artifact bytes untouched");
            GdDict result = control.Coordinator.ValidateSuppliedPayload(bad, rig.Session.RunId, "world");
            Unchanged(control);
            Assert.IsFalse(result.GetBool("ok"), "One-mirror numeric-tag mismatch must be refused: " + Detail(result));
        }

        // Provisional actual-session cases: strict Domain/session/payment prerequisites must pass
        // before classifying a save failure as the string-transport regression.
        [TestCase(false)]
        [TestCase(true)]
        public void PaidCommandWithVerticalTab_SavesAndSelectsExactOwner(bool diagnostic)
        {
            SessionHarness.Rig rig = Boot(diagnostic, true);
            Control prior = Admit(rig);
            const string commandId = "mirror-command-\u000b-end";
            Assert.IsFalse(string.IsNullOrWhiteSpace(commandId));
            StartRunning(rig.Session, commandId);
            GdDict owner = Owner(rig.Session), participants = RawParticipants(rig.Session);
            Assert.IsTrue(owner.GetDictOrEmpty("receipts").Values.OfType<GdDict>()
                .Any(row => row.GetString("command_id") == commandId), "Actual receipt retains the opaque command ID");
            Assert.IsTrue(Paid(owner).GetDictOrEmpty("jobs").Values.OfType<GdDict>()
                .Any(row => row.GetString("payment_commit_id") == "craft:" + commandId), "Actual paid job retains its payment identity");
            Dictionary<string, string> before = SlotPayloadBindingTests.Bytes(rig.Storage);
            bool saved = rig.Session.RequestSaveToSlot("world", "world", "Opaque command control");
            if (!saved)
            {
                SlotPayloadBindingTests.SameBytes(before, rig.Storage);
                Equal(prior.Selection, rig.Session.SaveLoadService.SelectGeneration("world"), "Failed save preserves prior valid selection");
            }
            Equal(owner, Owner(rig.Session), "Save attempt preserves whole paid authority");
            Equal(participants, RawParticipants(rig.Session), "Save attempt preserves live participants");
            Assert.IsTrue(saved, "Valid actual paid command must survive snapshot transport: " + Detail(rig.Session.LastSaveResult));
            GdDict selected = rig.Session.SaveLoadService.SelectGeneration("world");
            Assert.IsTrue(selected.GetBool("ok"), Detail(selected));
            Assert.AreNotEqual(prior.Selection.GetString("generation_id"), selected.GetString("generation_id"));
            GdDict payload = selected.GetDictOrEmpty("payloads");
            GdDict positive = prior.Coordinator.ValidateSuppliedPayload(payload, rig.Session.RunId, "world");
            Assert.IsTrue(positive.GetBool("ok"), Detail(positive));
            Equal(owner, DecodeOwner(Parse(payload, "run", true)), "Selected run carries the exact paid owner");
            Equal(owner, DecodeOwner(Parse(payload, "world", true).GetDictOrEmpty("home_ship")),
                "Selected home carries the same exact paid owner");
        }
    }
}

