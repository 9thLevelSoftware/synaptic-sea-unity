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
    public class PaidCraftSaveCapabilityTests : InfraDataTestBase
    {
        const string Recipe = "weld_plating";
        const string Kind = "workbench";
        const string PaidRoot = "user://saves/.paid-craft-generations";
        const string OrdinaryMode = "paid_crafting_ordinary";
        const string DiagnosticMode = "component-live-diagnostic";
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

        SessionHarness.Rig Boot(bool components = false, bool paid = true)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.EnableComponentIntegration = components;
            deps.EnablePaidCrafting = paid;
            rig.Session = RunSession.Create(deps);
            _sessions.Add(rig.Session);
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, "Boot prerequisite: " + s.LastFailureReason);
            Assert.AreEqual(components, s.ComponentIntegrationEnabled);
            Assert.AreEqual(paid, s.PaidCraftingEnabled);
            s.ThreatManager.Threats.Clear();
            s.InventoryState.Items.Clear();
            s.PlayerProgression.Skills["fabrication"] = 4L;
            s.VitalsState.Stamina = s.VitalsState.MaxStamina;
            s.HomeShip.LootedContainerIds.Add(Witness);
            rig.Scene.PlayerPosition = new Vec3(1, .5, 2);
            return rig;
        }

        static void Provision(RunSession s)
        {
            GdDict recipe = s.CraftingState.GetRecipe(Recipe);
            Assert.IsFalse(recipe.IsEmpty, "Real catalog recipe prerequisite.");
            Assert.IsTrue(s.CraftingStations.Any(st => st.IsValid && st.StationKind == Kind &&
                ReferenceEquals(st.Parent, s.HomeShip.SceneRoot)), "Actual original-home wrapper prerequisite.");
            var model = s.CraftingState.GetStation(Kind);
            // Legacy-only setup uses the existing model creation path, as old BeginCraft did.
            if (!s.PaidCraftingEnabled) model = s.CraftingState.GetOrCreateStation(Kind);
            Assert.IsNotNull(model, "Actual registered station model prerequisite.");
            model.SetPower(true);
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
            {
                long count = V.I64(ingredient.Value);
                Assert.AreEqual(count, s.InventoryState.AddItem(V.Str(ingredient.Key), count));
                Assert.AreEqual(count, s.InventoryState.GetQuantity(V.Str(ingredient.Key)));
            }
        }

        static GdDict Paid(GdDict owner) => owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting");
        static GdDict Job(RunSession s, string id) => Paid(s.CapturePaidCraftingDomain()).GetDictOrEmpty("jobs").GetDictOrEmpty(id);
        static void Equal(object expected, object actual, string message)
            => Assert.IsTrue(V.VariantEquals(expected, actual), message);
        static string Detail(GdDict result) => "reason=" + result.GetString("reason") + ", detail=" + result.GetString("detail");

        static GdDict ValidOwner(RunSession s)
        {
            GdDict owner = s.CapturePaidCraftingDomain();
            Assert.AreEqual(3L, owner.GetInt("schema_version"));
            Assert.IsTrue(DomainBundle.TryCreate(owner, out _, out string domainReason), "Domain positive prerequisite: " + domainReason);
            Assert.IsTrue(s.ValidatePaidCraftingRestore(owner, out string reason), "Session positive prerequisite: " + reason);
            Assert.AreEqual(s.ComponentIntegrationEnabled ? "components_and_craft" : "craft_only", owner.GetString("domain_mode"));
            if (!s.ComponentIntegrationEnabled)
            {
                Assert.IsTrue(owner.GetDictOrEmpty("registry").GetDictOrEmpty("instances").IsEmpty);
                foreach (string key in new[] { "holders", "machinery", "physical_slots", "component_work" })
                    Assert.IsTrue(owner.GetDictOrEmpty(key).IsEmpty, key + " must remain inactive.");
            }
            return owner;
        }

        static string StartRunning(RunSession s)
        {
            Provision(s);
            GdDict started = s.RequestPaidCraft(Kind, Recipe, "save-capability-start");
            Assert.IsTrue(started.GetBool("ok"), "Paid start prerequisite: " + Detail(started));
            Assert.IsTrue(started.GetBool("committed"));
            string id = started.GetString("job_id");
            Assert.IsNotEmpty(id);
            Assert.IsNotEmpty(started.GetString("commit_id"));
            foreach (var ingredient in s.CraftingState.GetRecipe(Recipe).GetDictOrEmpty("ingredients"))
                Assert.AreEqual(0L, s.InventoryState.GetQuantity(V.Str(ingredient.Key)), "Exactly one set paid.");
            GdDict initial = Job(s, id);
            s.AdvanceCrafting(initial.GetFloat("required_seconds") / 4.0);
            GdDict running = Job(s, id);
            Assert.AreEqual("running", running.GetString("status"));
            Assert.Greater(running.GetFloat("progress_seconds"), 0.0);
            Assert.Less(running.GetFloat("progress_seconds"), running.GetFloat("required_seconds"));
            foreach (string key in new[] { "payment_commit_id", "quality_score", "quality_tier", "required_seconds", "consumed" })
                Equal(initial.Get(key), running.Get(key), "Progress preserves paid " + key);
            Assert.AreEqual(0L, s.InventoryState.GetQuantity("plating"));
            ValidOwner(s);
            return id;
        }

        [TestCase("world", "world")]
        [TestCase("autosave_active", "auto")]
        [TestCase("autosave_a", "auto")]
        [TestCase("autosave_b", "auto")]
        [TestCase("autosave_c", "auto")]
        [TestCase("quicksave", "quick")]
        [TestCase("slot_01", "manual")]
        [TestCase("slot_02", "manual")]
        [TestCase("slot_03", "manual")]
        [TestCase("slot_04", "manual")]
        [TestCase("slot_05", "manual")]
        [TestCase("slot_06", "manual")]
        public void OrdinaryExplicitSlot_CapturesCompletePaidGeneration(string slot, string kind)
        {
            SessionHarness.Rig rig = Boot();
            StartRunning(rig.Session);
            GdDict before = ValidOwner(rig.Session);
            Assert.IsTrue(rig.Session.RequestSaveToSlot(slot, kind, "Paid complete world"),
                "Ordinary capability RED: " + Detail(rig.Session.LastSaveResult));
            AssertPaidSelection(rig, slot, kind, before, false);
        }

        [TestCase("world")]
        [TestCase("quicksave")]
        [TestCase("autosave_a")]
        [TestCase("autosave_b")]
        [TestCase("autosave_c")]
        [TestCase("autosave_active")]
        public void OrdinaryGameplaySave_CapturesCompletePaidGeneration(string slot)
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            StartRunning(s);
            GdDict before = ValidOwner(s);
            if (slot == "world") Assert.IsTrue(s.RequestSave(), "Actual RequestSave may already succeed through legacy SaveWorld.");
            else if (slot == "quicksave") Assert.IsTrue(s.RequestQuicksave(), "Actual first quicksave prerequisite.");
            else if (slot == "autosave_active") Assert.IsTrue(s.SaveLoadService.SaveCurrentRun(RunSnapshotAssembler.Build(s)));
            else
            {
                bool reached = false;
                for (int i = 0; i < 3; i++)
                {
                    GdDict result = s.ForceAutosave();
                    Assert.IsTrue(result.GetBool("should_save"), "Real forced rotation prerequisite: " + Detail(result));
                    if (result.GetString("slot_id") == slot) { reached = true; break; }
                }
                Assert.IsTrue(reached, "Real rotation must visit the requested slot.");
            }
            AssertPaidSelection(rig, slot, slot == "world" ? "world" : slot == "quicksave" ? "quick" : "auto", before, false);
        }

        [TestCase("empty")]
        [TestCase("running")]
        [TestCase("unpaid_queue")]
        [TestCase("pending_delivery")]
        [TestCase("legacy_kept")]
        public void DiagnosticPaidState_CapturesCompleteGeneration(string state)
        {
            SessionHarness.Rig rig = Boot(components: true);
            RunSession s = rig.Session;
            if (state == "running" || state == "unpaid_queue" || state == "pending_delivery")
            {
                string id = StartRunning(s);
                if (state == "unpaid_queue")
                {
                    GdDict queued = s.EnqueuePaidCraft(Kind, Recipe, "save-unpaid-queue");
                    Assert.IsTrue(queued.GetBool("committed"), Detail(queued));
                    GdDict job = Job(s, queued.GetString("job_id"));
                    Assert.AreEqual("unpaid", job.GetString("input_state"));
                    Assert.IsEmpty(job.GetString("payment_commit_id"));
                    Assert.AreEqual(0L, s.InventoryState.GetQuantity("scrap_metal"));
                }
                if (state == "pending_delivery")
                {
                    long maximum = s.InventoryState.GetDefinition("plating").GetInt("max_stack", 99);
                    Assert.AreEqual(maximum, s.InventoryState.AddItem("plating", maximum));
                    s.AdvanceCrafting(Job(s, id).GetFloat("required_seconds"));
                    Assert.AreEqual("completed_pending_delivery", Job(s, id).GetString("status"));
                    Assert.AreEqual(maximum, s.InventoryState.GetQuantity("plating"), "No partial output accepted.");
                }
            }
            if (state == "legacy_kept")
            {
                RunSession old = Boot(paid: false).Session;
                Provision(old);
                Assert.IsTrue(old.CraftingState.BeginCraft(Recipe, old.InventoryState, old.MaterialState, 4));
                GdDict source = old.CraftingState.GetSummary().DeepCopy();
                string sourceHash = GenerationFixtures.Hash(GdJson.Stringify(source));
                GdDict imported = s.ImportLegacyCrafting(source, new GdDict(), "legacy-run/current_run.json", sourceHash);
                Assert.IsTrue(imported.GetBool("ok"), Detail(imported));
                GdDict legacy = Paid(s.CapturePaidCraftingDomain()).GetDictOrEmpty("legacy");
                Assert.AreEqual(1, legacy.Count);
                string id = V.Str(legacy.Keys.Single());
                Assert.IsTrue(s.ReconcileLegacyCraft(id, "keep_paused", "save-keep-legacy").GetBool("committed"));
                GdDict row = Paid(s.CapturePaidCraftingDomain()).GetDictOrEmpty("legacy").GetDictOrEmpty(id);
                Assert.AreEqual("paused_unverified", row.GetString("status"));
                Assert.AreEqual(sourceHash, row.GetString("source_hash"));
                Equal(source.Get("active_craft"), row.Get("original_record"), "Keep preserves exact anonymous source record.");
            }
            GdDict before = ValidOwner(s);
            if (state == "empty") Assert.IsTrue(Paid(before).GetDictOrEmpty("jobs").IsEmpty);
            Assert.IsTrue(s.RequestSaveToSlot("world", "world", "Paid diagnostic world"),
                "Diagnostic paid admission RED after strict positive owner: " + Detail(s.LastSaveResult));
            AssertPaidSelection(rig, "world", "world", before, true);
        }

        [Test]
        public void DefaultLegacy_RequestSaveKeepsWorld4Control()
        {
            Assert.IsFalse(new RunSessionDeps().EnablePaidCrafting);
            Assert.IsFalse(new RunSessionDeps().EnableComponentIntegration);
            SessionHarness.Rig rig = Boot(paid: false);
            Assert.IsTrue(rig.Session.RequestSave());
            GdDict world = (GdDict)GdJson.ParseString(rig.Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE));
            Assert.AreEqual("world-4", world.GetString("slice_version"));
            Assert.IsFalse(world.GetDictOrEmpty("home_ship").GetDictOrEmpty("crafting_summary").Has("paid_craft"));
            Assert.IsFalse(rig.Storage.DirExists(PaidRoot));
            Assert.IsFalse(rig.Storage.DirExists(SaveLoadService.ComponentGenerationRoot));
            Assert.IsTrue(rig.Session.RequestLoad());
            Assert.IsTrue(rig.Session.HomeShip.LootedContainerIds.Contains(Witness));
        }

        [Test]
        public void DiagnosticSchema2_FullPayloadSaveReloadControl()
        {
            SessionHarness.Rig rig = Boot(components: true, paid: false);
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

        static void AssertPaidSelection(SessionHarness.Rig rig, string slot, string kind, GdDict before, bool diagnostic)
        {
            RunSession s = rig.Session;
            GdDict selected = s.SaveLoadService.SelectGeneration(slot);
            Assert.IsTrue(selected.GetBool("ok"), "A successful gameplay save must select a complete paid generation: " + Detail(selected));
            string mode = diagnostic ? DiagnosticMode : OrdinaryMode;
            Assert.AreEqual(mode, selected.GetString("save_mode"));
            Assert.AreEqual(s.RunId, selected.GetString("run_id"));
            Assert.AreEqual(slot, selected.GetString("slot_id"));
            Assert.IsNotEmpty(selected.GetString("generation_id"));
            Assert.IsNotEmpty(selected.GetString("manifest_sha256"));
            GdDict payload = selected.GetDictOrEmpty("payloads");
            Assert.AreEqual(s.RunId, payload.GetString("run_id"));
            Assert.AreEqual(slot, payload.GetString("slot_id"));
            Assert.AreEqual(kind, payload.GetString("slot_kind"));
            Assert.AreEqual(selected.GetString("generation_id"), payload.GetString("generation_id"));
            Assert.AreEqual(diagnostic ? "component-live-diagnostic" : "paid-crafting-ordinary", payload.GetDictOrEmpty("compatibility").GetString("catalog_id"));
            Assert.AreEqual("1", payload.GetDictOrEmpty("compatibility").GetString("catalog_version"));
            // Exact-number parsing here is only an assertion oracle, not a production transport implementation.
            GdDict run = (GdDict)GdJson.Parse(payload.GetString("run_text"), true);
            GdDict world = (GdDict)GdJson.Parse(payload.GetString("world_text"), true);
            GdDict home = world.GetDictOrEmpty("home_ship");
            Assert.AreEqual(diagnostic ? "gate2-current-run-7" : "gate2-current-run-6", run.GetString("slice_version"));
            Assert.AreEqual(diagnostic ? "world-5" : "world-4", world.GetString("slice_version"));
            Assert.AreEqual(run.GetString("slice_version"), home.GetString("slice_version"));
            Assert.IsTrue(world.GetArrayOrEmpty("home_looted_containers").Contains(Witness), "Full-world witness.");
            Equal(GdArray.Of(1.0, .5, 2.0), world.Get("player_position_in_ship"), "Selected local pose.");
            GdDict canonical = null;
            foreach (GdDict mirror in new[] { run, home })
            {
                GdDict craft = mirror.GetDictOrEmpty("crafting_summary");
                GdDict envelope = craft.GetDictOrEmpty("paid_craft");
                Assert.AreEqual(3, envelope.Count, "Exact paid_craft envelope shape.");
                Assert.IsTrue(envelope.Get("schema_version") is long);
                Assert.AreEqual(1L, envelope.GetInt("schema_version"));
                Assert.AreEqual(mode, envelope.GetString("save_mode"));
                Assert.IsTrue(ComponentDomainCodec.TryDecode(envelope.GetDictOrEmpty("domain"), out GdDict decoded, out string reason), reason);
                Assert.IsTrue(DomainBundle.TryCreate(decoded, out _, out reason), reason);
                Assert.IsTrue(s.ValidatePaidCraftingRestore(decoded, out reason), reason);
                Equal(before, decoded, "Capture preserves the full sole owner, payment/progress/queue/history and receipts.");
                if (canonical != null) Equal(canonical, decoded, "Active/home canonical authority agrees.");
                canonical = decoded;
                GdDict participants = decoded.GetDictOrEmpty("participating_state");
                foreach (var pair in participants.GetDictOrEmpty("crafting")) Equal(pair.Value, craft.Get(pair.Key), "Exact crafting mirror " + pair.Key);
                Equal(participants.GetDictOrEmpty("field_crafting").Get("field_crafting"), craft.Get("field_crafting"), "Exact field mirror.");
                Equal(participants.GetDictOrEmpty("inventory").Get("items"), mirror.GetDictOrEmpty("inventory_summary").Get("items"), "Exact input/output mirror.");
                Equal(participants.Get("progression"), mirror.Get("player_progression_summary"), "Exact progression mirror.");
                Equal(participants.Get("spoilage"), mirror.Get("spoilage_summary"), "Exact spoilage mirror.");
            }
            if (diagnostic)
                foreach (GdDict mirror in new[] { run, home, world })
                {
                    Assert.IsTrue(ComponentDomainCodec.TryDecode(mirror.GetDictOrEmpty("component_domain"), out GdDict decoded, out string reason), reason);
                    Equal(canonical, decoded, "Diagnostic component_domain retains the same combined owner.");
                }
            string root = diagnostic ? SaveLoadService.ComponentGenerationRoot : PaidRoot;
            string generation = root + "/g/" + GenerationFixtures.TupleToken(s.RunId, slot, selected.GetString("generation_id"));
            Assert.AreEqual(payload.GetString("run_text"), rig.Storage.ReadText(generation + "/run.json"));
            Assert.AreEqual(payload.GetString("world_text"), rig.Storage.ReadText(generation + "/world.json"));
            Assert.Greater(payload.GetArrayOrEmpty("artifacts").Count, 0, "Complete retained document closure.");
            int ordinal = 0;
            foreach (GdDict artifact in payload.GetArrayOrEmpty("artifacts").OfType<GdDict>().OrderBy(a => a.GetString("logical_path"), StringComparer.Ordinal))
                Assert.AreEqual(artifact.GetString("text"), rig.Storage.ReadText(generation + "/" + (ordinal++).ToString("x8") + ".json"));
            Assert.AreEqual(diagnostic, s.ComponentIntegrationEnabled);
            Equal(before, ValidOwner(s), "Saving never charges, rewards or changes consent.");
        }
    }
}
