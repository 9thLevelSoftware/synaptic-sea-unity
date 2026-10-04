// ARTIFACT DRAFT ONLY. ROOT releases after transport GREEN and fresh review.
// Eight cases use existing APIs. No .meta or Assets installation accompanies this file.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    public class PaidCraftSaveRestoreTests : InfraDataTestBase
    {
        const string Kind = "workbench", Recipe = "weld_plating";
        readonly List<RunSession> _sessions = new List<RunSession>();
        IEngineInfo _engine;
        [SetUp] public void SetEngine()
        { _engine = CoreServices.Engine; CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion); }
        [TearDown] public void DisposeSessions()
        {
            try { foreach (RunSession s in _sessions) s.Dispose(); _sessions.Clear(); }
            finally { CoreServices.Engine = _engine; }
        }

        static string Detail(GdDict result) => result.GetString("reason") + ":" + result.GetString("detail");
        static GdDict Paid(GdDict owner) => owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting");
        static GdDict Jobs(GdDict owner) => Paid(owner).GetDictOrEmpty("jobs");
        static GdDict Job(RunSession s, string id) => Jobs(s.CapturePaidCraftingDomain()).GetDictOrEmpty(id);
        static void Exact(object expected, object actual, string message)
        {
            // Compare exact typed values, not the legacy numeric JSON formatter.
            Assert.IsTrue(V.VariantEquals(ComponentDomainCodec.Encode(new GdDict { { "value", expected } }),
                ComponentDomainCodec.Encode(new GdDict { { "value", actual } })), message);
        }
        SessionHarness.Rig Boot(bool combined)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.EnablePaidCrafting = true; deps.EnableComponentIntegration = combined;
            rig.Session = RunSession.Create(deps); _sessions.Add(rig.Session);
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            Assert.IsTrue(s.PaidCraftingEnabled); Assert.AreEqual(combined, s.ComponentIntegrationEnabled);
            s.ThreatManager.Threats.Clear(); s.InventoryState.Items.Clear();
            s.PlayerProgression.Skills["fabrication"] = 4L;
            s.VitalsState.Stamina = s.VitalsState.MaxStamina;
            s.HomeShip.LootedContainerIds.Add("restore-full-world-witness");
            rig.Scene.PlayerPosition = new Vec3(1, .5, 2);
            Assert.IsTrue(s.CraftingStations.Any(st => st.IsValid && st.StationKind == Kind &&
                ReferenceEquals(st.Parent, s.HomeShip.SceneRoot)));
            Assert.IsNotNull(s.CraftingState.GetStation(Kind));
            s.CraftingState.GetStation(Kind).SetPower(true);
            return rig;
        }
        static void Provision(RunSession s, long batches = 1)
        {
            GdDict recipe = s.CraftingState.GetRecipe(Recipe); Assert.IsFalse(recipe.IsEmpty);
            foreach (var item in recipe.GetDictOrEmpty("ingredients"))
                Assert.AreEqual(V.I64(item.Value) * batches, s.InventoryState.AddItem(V.Str(item.Key), V.I64(item.Value) * batches));
        }
        static string Start(RunSession s, string command)
        {
            GdDict result = s.RequestPaidCraft(Kind, Recipe, command);
            Assert.IsTrue(result.GetBool("ok"), Detail(result)); Assert.IsTrue(result.GetBool("committed"));
            Assert.IsNotEmpty(result.GetString("commit_id")); Assert.IsNotEmpty(result.GetString("job_id"));
            return result.GetString("job_id");
        }
        static string Queue(RunSession s, string command)
        {
            GdDict result = s.EnqueuePaidCraft(Kind, Recipe, command);
            Assert.IsTrue(result.GetBool("ok"), Detail(result)); Assert.IsTrue(result.GetBool("committed"));
            Assert.IsNotEmpty(result.GetString("job_id")); return result.GetString("job_id");
        }
        static GdDict Valid(RunSession s)
        {
            GdDict owner = s.CapturePaidCraftingDomain();
            Assert.IsTrue(DomainBundle.TryCreate(owner, out _, out string reason), reason);
            Assert.IsTrue(s.ValidatePaidCraftingRestore(owner, out reason), reason);
            Assert.AreEqual(s.ComponentIntegrationEnabled ? "components_and_craft" : "craft_only", owner.GetString("domain_mode"));
            return owner;
        }
        static GdDict Save(SessionHarness.Rig rig, string slot, out GdDict saved)
        {
            RunSession s = rig.Session; saved = Valid(s).DeepCopy();
            string kind = slot == "world" ? "world" : "manual";
            long operationStart = Stopwatch.GetTimestamp();
            bool savedOk = s.RequestSaveToSlot(slot, kind, "Paid restore acceptance");
            long operationStop = Stopwatch.GetTimestamp();
            TestContext.WriteLine("PAID_RESTORE_TIMING; operation=RequestSaveToSlot; combined=" + s.ComponentIntegrationEnabled +
                "; slot=" + slot + "; elapsed_ms=" + ((operationStop - operationStart) * 1000.0 / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture));
            Assert.IsTrue(savedOk, Detail(s.LastSaveResult));
            operationStart = Stopwatch.GetTimestamp();
            GdDict selected = s.SaveLoadService.SelectGeneration(slot);
            operationStop = Stopwatch.GetTimestamp();
            TestContext.WriteLine("PAID_RESTORE_TIMING; operation=SelectGeneration; combined=" + s.ComponentIntegrationEnabled +
                "; slot=" + slot + "; elapsed_ms=" + ((operationStop - operationStart) * 1000.0 / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture));
            Assert.IsTrue(selected.GetBool("ok"), Detail(selected));
            Assert.AreEqual(s.ComponentIntegrationEnabled ? PaidSnapshotCodec.DiagnosticMode : PaidSnapshotCodec.OrdinaryMode, selected.GetString("save_mode"));
            GdDict payload = selected.GetDictOrEmpty("payloads");
            Assert.Greater(payload.GetArrayOrEmpty("artifacts").Count, 0, "Actual full document closure.");
            var coordinator = s.SaveLoadService.ComponentCoordinator();
            operationStart = Stopwatch.GetTimestamp();
            GdDict admitted = coordinator.ValidateSuppliedPayload(payload, s.RunId, slot);
            operationStop = Stopwatch.GetTimestamp();
            TestContext.WriteLine("PAID_RESTORE_TIMING; operation=ValidateSuppliedPayload; combined=" + s.ComponentIntegrationEnabled +
                "; slot=" + slot + "; elapsed_ms=" + ((operationStop - operationStart) * 1000.0 / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture));
            Assert.IsTrue(admitted.GetBool("ok"), Detail(admitted));
            string generationId = selected.GetString("generation_id"), manifestSha256 = selected.GetString("manifest_sha256");
            operationStart = Stopwatch.GetTimestamp();
            GdDict exact = s.SaveLoadService.ReadGeneration(s.RunId, slot, generationId, manifestSha256);
            operationStop = Stopwatch.GetTimestamp();
            TestContext.WriteLine("PAID_RESTORE_TIMING; operation=ReadGeneration; combined=" + s.ComponentIntegrationEnabled +
                "; slot=" + slot + "; elapsed_ms=" + ((operationStop - operationStart) * 1000.0 / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture));
            Assert.IsTrue(exact.GetBool("ok"), Detail(exact)); Exact(payload, exact.Get("payloads"), "Exact selected full payload.");
            GdDict run = GdJson.Parse(payload.GetString("run_text"), true) as GdDict;
            GdDict world = GdJson.Parse(payload.GetString("world_text"), true) as GdDict;
            Assert.IsNotNull(run); Assert.IsNotNull(world);
            Assert.IsTrue(world.GetArrayOrEmpty("home_looted_containers").Contains("restore-full-world-witness"));
            foreach (GdDict mirror in new[] { run, world.GetDictOrEmpty("home_ship") })
            {
                Assert.IsTrue(ComponentDomainCodec.TryDecode(mirror.GetDictOrEmpty("crafting_summary")
                    .GetDictOrEmpty("paid_craft").GetDictOrEmpty("domain"), out GdDict decoded, out string reason), reason);
                Assert.IsTrue(DomainBundle.TryCreate(decoded, out _, out reason), reason);
                Assert.IsTrue(s.ValidatePaidCraftingRestore(decoded, out reason), "Current-context positive: " + reason);
                Exact(saved, decoded, "Selected owner/payment/progress/history before restore.");
            }
            return selected.DeepCopy();
        }
        static GdDict Live(SessionHarness.Rig rig)
        {
            RunSession s = rig.Session;
            return new GdDict {
                { "owner", s.CapturePaidCraftingDomain() }, { "run", s.RunId }, { "service_run", s.SaveLoadService.GetActiveRunId() },
                { "inventory", s.InventoryState.GetSummary() }, { "progression", s.PlayerProgression.GetSummary() },
                { "crafting", s.CraftingState.GetSummary() }, { "field", s.FieldCraftingState.GetSummary() },
                { "knowledge", s.RecipeKnowledge.GetSummary() }, { "training", s.TrainingEventBus.ToDict() },
                { "spoilage", s.SpoilageState.GetSummary() }, { "looted", s.HomeShip.LootedContainerIds.DeepCopy() },
                { "position", rig.Scene.PlayerPosition }, { "world_time", s.WorldTime }, { "play_time", s.RunPlayTimeSeconds },
                { "spawns", (long)rig.Scene.SpawnCount }, { "despawns", (long)rig.Scene.DespawnCount }
            };
        }
        static void Load(SessionHarness.Rig rig, GdDict selected, bool requestLoad = false)
        {
            RunSession s = rig.Session;
            GdDict before = Live(rig), selectionBefore = selected.DeepCopy();
            var bytes = SlotPayloadBindingTests.Bytes(rig.Storage);
            var oldHome = s.HomeShip; var oldLoader = s.Loader; var oldRoot = s.HomeShip.SceneRoot;
            var wrappers = s.CraftingStations.ToArray();
            var wrapperValidity = wrappers.Select(st => st.IsValid).ToArray();
            var wrapperParents = wrappers.Select(st => st.Parent).ToArray();
            var attached = rig.Host.Attached.ToArray(); var freed = rig.Host.Freed.ToArray();
            int homeLoads = rig.Host.HomeLoads; bool valid = oldRoot.IsValid, inTree = oldRoot.IsInsideTree;
            Xform3 transform = oldRoot.GlobalTransform;
            long operationStart = Stopwatch.GetTimestamp();
            bool loaded = requestLoad ? s.RequestLoad() : s.ApplySelectedGeneration(selected);
            long operationStop = Stopwatch.GetTimestamp();
            TestContext.WriteLine("PAID_RESTORE_TIMING; operation=" + (requestLoad ? "RequestLoad" : "ApplySelectedGeneration") +
                "; combined=" + s.ComponentIntegrationEnabled + "; slot=" + selected.GetString("slot_id") +
                "; elapsed_ms=" + ((operationStop - operationStart) * 1000.0 / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture));
            SlotPayloadBindingTests.SameBytes(bytes, rig.Storage);
            Exact(selectionBefore, selected, "Caller-owned selection unchanged.");
            TestContext.WriteLine("PAID_RESTORE_ROUTE=" + (requestLoad ? "RequestLoad" : "ApplySelectedGeneration") +
                "; ok=" + loaded + "; " + Detail(s.LastSaveResult));
            if (!loaded)
            {
                Exact(before, Live(rig), "Refused actual route preserves owner and raw live before-image.");
                Assert.AreSame(oldHome, s.HomeShip); Assert.AreSame(oldLoader, s.Loader); Assert.AreSame(oldRoot, s.HomeShip.SceneRoot);
                Assert.AreEqual(valid, oldRoot.IsValid); Assert.AreEqual(inTree, oldRoot.IsInsideTree);
                Assert.AreEqual(transform, oldRoot.GlobalTransform); Assert.AreEqual(homeLoads, rig.Host.HomeLoads);
                CollectionAssert.AreEqual(wrappers, s.CraftingStations);
                CollectionAssert.AreEqual(wrapperValidity, s.CraftingStations.Select(st => st.IsValid).ToArray());
                CollectionAssert.AreEqual(wrapperParents, s.CraftingStations.Select(st => st.Parent).ToArray());
                CollectionAssert.AreEqual(attached, rig.Host.Attached); CollectionAssert.AreEqual(freed, rig.Host.Freed);
                // Current meaningful hard gate; keep the success assertion below RED.
                // A later unsupported-host/context refusal is a new gate, never a restore PASS.
            }
            Assert.IsTrue(loaded, "Actual paid restore required; later semantic assertions are UNREACHED on refusal: " + Detail(s.LastSaveResult));
            Assert.AreEqual(selected.GetString("run_id"), s.RunId);
            Assert.AreEqual(s.RunId, s.SaveLoadService.GetActiveRunId());
            Assert.IsTrue(s.HomeShip.LootedContainerIds.Contains("restore-full-world-witness"));
            Assert.IsTrue(s.CraftingStations.Any(st => st.IsValid && st.StationKind == Kind && ReferenceEquals(st.Parent, s.HomeShip.SceneRoot)));
        }
        static void InstalledWithConsentPause(RunSession s, GdDict saved)
        {
            GdDict actual = Valid(s), normalized = actual.DeepCopy();
            // Permit ONLY the specified successful-load consent/status projection changes.
            // All other owner fields, receipts, compact history, counters and participants compare exactly.
            foreach (var pair in Jobs(saved))
            {
                GdDict original = (GdDict)pair.Value, row = Jobs(actual).GetDictOrEmpty(pair.Key);
                if (PaidCraftingState.Terminal(original)) continue;
                Assert.IsTrue(row.GetBool("resume_required"));
                Assert.AreEqual(original.GetString("status") == "running" ? "paused" : original.GetString("status"), row.GetString("status"));
                Jobs(normalized).GetDictOrEmpty(pair.Key)["resume_required"] = original.Get("resume_required");
                Jobs(normalized).GetDictOrEmpty(pair.Key)["status"] = original.Get("status");
                if (original.GetString("input_state") != "paid") continue;
                GdDict prior = saved.GetDictOrEmpty("participating_state"), now = normalized.GetDictOrEmpty("participating_state");
                if (original.GetString("channel") == "field")
                { prior = prior.GetDictOrEmpty("field_crafting"); now = now.GetDictOrEmpty("field_crafting"); }
                string summaryKey = original.GetString("channel") == "field" ? "field_crafting" : "crafting";
                GdDict oldStation = prior.GetDictOrEmpty(summaryKey).GetDictOrEmpty("station_summaries").GetDictOrEmpty(original.GetString("station_kind"));
                GdDict station = now.GetDictOrEmpty(summaryKey).GetDictOrEmpty("station_summaries").GetDictOrEmpty(original.GetString("station_kind"));
                station["resume_required"] = oldStation.Get("resume_required"); station["status"] = oldStation.Get("status");
            }
            Assert.IsTrue(saved.GetDictOrEmpty("component_work").IsEmpty, "This bounded positive wave has no active component operation.");
            Exact(saved, normalized, "Exact saved owner replaces live authority; only required consent pause differs.");
        }
        static void Ingredients(RunSession s, long batches)
        {
            foreach (var ingredient in s.CraftingState.GetRecipe(Recipe).GetDictOrEmpty("ingredients"))
                Assert.AreEqual(V.I64(ingredient.Value) * batches, s.InventoryState.GetQuantity(V.Str(ingredient.Key)));
        }
        static void NoAutomaticWork(RunSession s)
        {
            GdDict before = s.CapturePaidCraftingDomain(); s.AdvanceCrafting(1000);
            Exact(before, s.CapturePaidCraftingDomain(), "Elapsed craft time does not grant restored consent/payment/delivery/XP.");
        }
        static void DeliveredOnce(RunSession s, string id, string retryId)
        {
            Assert.AreEqual("completed_delivered", Job(s, id).GetString("status"));
            GdDict once = s.CapturePaidCraftingDomain();
            Assert.IsTrue(s.RetryPaidCraft(id, retryId).GetBool("ok")); s.AdvanceCrafting(1000);
            Exact(once, s.CapturePaidCraftingDomain(), "Delivered replay/time cannot add output, training, XP or history.");
        }

        [TestCase(false)] [TestCase(true)]
        public void RunningProgress_ContinueAndRepeatedSelectedLoad_RewindWithoutRecharge(bool combined)
        {
            var rig = Boot(combined); RunSession s = rig.Session; Provision(s);
            string id = Start(s, "running-start"); Ingredients(s, 0);
            s.AdvanceCrafting(Job(s, id).GetFloat("required_seconds") / 4.0);
            Assert.Greater(Job(s, id).GetFloat("progress_seconds"), 0);
            GdDict selected = Save(rig, "world", out GdDict saved);
            s.AdvanceCrafting(Job(s, id).GetFloat("required_seconds") / 4.0);
            rig.Clock.Advance(86400); // Clock only; never a wall-clock sleep or implicit completion.
            Load(rig, selected, true); InstalledWithConsentPause(s, saved); Ingredients(s, 0); NoAutomaticWork(s);
            Assert.IsTrue(s.ResumePaidCraft(id, "running-resume").GetBool("ok")); Ingredients(s, 0);
            s.AdvanceCrafting(1000); Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating"));
            DeliveredOnce(s, id, "running-retry"); GdDict firstDone = s.CapturePaidCraftingDomain();
            Load(rig, selected); InstalledWithConsentPause(s, saved);
            Assert.AreEqual(0L, s.InventoryState.GetQuantity("plating")); Ingredients(s, 0); NoAutomaticWork(s);
            Assert.IsTrue(s.ResumePaidCraft(id, "running-resume").GetBool("ok")); s.AdvanceCrafting(1000);
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating")); DeliveredOnce(s, id, "running-retry");
            Exact(firstDone.Get("participating_state"), s.CapturePaidCraftingDomain().Get("participating_state"),
                "Loading the same selected saved point rewinds prior rewards; resuming produces one equivalent result.");
        }

        [TestCase(false)] [TestCase(true)]
        public void PendingDelivery_SelectedLoad_RetryDeliversAndRewardsOnce(bool combined)
        {
            var rig = Boot(combined); RunSession s = rig.Session; Provision(s); string id = Start(s, "pending-start");
            long maximum = s.InventoryState.GetDefinition("plating").GetInt("max_stack", 99);
            Assert.AreEqual(maximum, s.InventoryState.AddItem("plating", maximum));
            GdDict xp = s.PlayerProgression.GetSummary(); GdArray training = s.TrainingEventBus.GetLog().DeepCopy();
            s.AdvanceCrafting(1000); Assert.AreEqual("completed_pending_delivery", Job(s, id).GetString("status"));
            Exact(xp, s.PlayerProgression.GetSummary(), "No pending XP."); Exact(training, s.TrainingEventBus.GetLog(), "No pending training.");
            GdDict selected = Save(rig, "slot_01", out GdDict saved);
            Assert.AreEqual(maximum, s.InventoryState.RemoveItem("plating", maximum));
            Load(rig, selected); InstalledWithConsentPause(s, saved); Ingredients(s, 0);
            Assert.AreEqual(maximum, s.InventoryState.GetQuantity("plating"));
            Assert.AreEqual(maximum, s.InventoryState.RemoveItem("plating", maximum)); NoAutomaticWork(s);
            Assert.AreEqual(0L, s.InventoryState.GetQuantity("plating"));
            Exact(xp, s.PlayerProgression.GetSummary(), "Restore/wait does not reward pending output.");
            Assert.IsTrue(s.RetryPaidCraft(id, "pending-explicit-retry").GetBool("ok"));
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating")); Ingredients(s, 0);
            Assert.Greater(s.TrainingEventBus.GetLog().Count, training.Count);
            DeliveredOnce(s, id, "pending-explicit-retry");
        }

        [TestCase(false)] [TestCase(true)]
        public void UnpaidFifo_Continue_PreservesOrderAndRequiresEachConsentBeforeCharge(bool combined)
        {
            var rig = Boot(combined); RunSession s = rig.Session; Provision(s); string active = Start(s, "fifo-active");
            string head = Queue(s, "fifo-head"), tail = Queue(s, "fifo-tail");
            s.AdvanceCrafting(1000); Assert.AreEqual("completed_delivered", Job(s, active).GetString("status"));
            Assert.AreEqual("missing_ingredients", Job(s, head).GetString("blocked_reason"));
            Provision(s, 2); Ingredients(s, 2);
            GdDict selected = Save(rig, "world", out GdDict saved);
            Load(rig, selected, true); InstalledWithConsentPause(s, saved); NoAutomaticWork(s); Ingredients(s, 2);
            Exact(GdArray.Of(head, tail), Paid(s.CapturePaidCraftingDomain()).GetDictOrEmpty("queues").Get("station"), "FIFO preserved.");
            foreach (string id in new[] { head, tail })
            { Assert.AreEqual("unpaid", Job(s, id).GetString("input_state")); Assert.IsEmpty(Job(s, id).GetString("payment_commit_id")); }
            GdDict beforeTail = s.CapturePaidCraftingDomain();
            Assert.AreEqual("queue_not_head", s.ResumePaidCraft(tail, "fifo-tail-early").GetString("reason"));
            Exact(beforeTail, s.CapturePaidCraftingDomain(), "Tail refusal cannot charge or reorder.");
            Assert.IsTrue(s.ResumePaidCraft(head, "fifo-head-consent").GetBool("ok")); Ingredients(s, 1);
            string headPayment = Job(s, head).GetString("payment_commit_id"); Assert.IsNotEmpty(headPayment);
            GdDict paidOnce = s.CapturePaidCraftingDomain(); Assert.IsTrue(s.ResumePaidCraft(head, "fifo-head-consent").GetBool("ok"));
            Exact(paidOnce, s.CapturePaidCraftingDomain(), "Duplicate consent does not pay twice.");
            s.AdvanceCrafting(1000); Ingredients(s, 1); Assert.AreEqual("completed_delivered", Job(s, head).GetString("status"));
            Assert.AreEqual("unpaid", Job(s, tail).GetString("input_state")); Assert.IsTrue(Job(s, tail).GetBool("resume_required"));
            Assert.IsTrue(s.ResumePaidCraft(tail, "fifo-tail-consent").GetBool("ok")); Ingredients(s, 0);
            Assert.AreNotEqual(headPayment, Job(s, tail).GetString("payment_commit_id"));
            s.AdvanceCrafting(1000); Assert.AreEqual(3L, s.InventoryState.GetQuantity("plating")); DeliveredOnce(s, tail, "fifo-tail-terminal-retry");
        }

        [TestCase(false)] [TestCase(true)]
        public void EarlierEmptySelection_ReplacesNewerLiveJobsReceiptsAndHistory(bool combined)
        {
            var rig = Boot(combined); RunSession s = rig.Session;
            GdDict selected = Save(rig, "slot_01", out GdDict empty);
            GdDict selectedWorld = GdJson.Parse(selected.GetDictOrEmpty("payloads").GetString("world_text"), true) as GdDict;
            Assert.IsNotNull(selectedWorld);
            Assert.AreEqual("", selectedWorld.GetString("current_location"), "Actual saved home context.");
            GdArray savedLoot = selectedWorld.GetArrayOrEmpty("home_looted_containers").DeepCopy();
            GdArray savedPose = selectedWorld.GetArrayOrEmpty("player_position_in_ship").DeepCopy();
            Assert.AreEqual(3, savedPose.Count);
            void AssertSelectedWorld()
            {
                Assert.IsTrue(rig.Scene.HasPlayer);
                Assert.AreEqual(selectedWorld.GetString("aboard_ship_id"), s.CurrentOccupancy.ShipId);
                Exact(savedLoot, s.HomeShip.LootedContainerIds, "Full selected world restores saved loot and removes unsaved loot.");
                Vec3 local = SessionMath.AffineInverse(s.HomeShip.SceneRoot.GlobalTransform) * rig.Scene.PlayerPosition;
                Exact(savedPose, GdArray.Of((double)local.X, (double)local.Y, (double)local.Z),
                    "Full selected world restores saved local player pose.");
            }
            AssertSelectedWorld(); // Positive witness before deliberately changing live nonpaid world state.
            Assert.IsTrue(Jobs(empty).IsEmpty);
            Provision(s); string completed = Start(s, "newer-completed"); s.AdvanceCrafting(1000);
            Assert.AreEqual("completed_delivered", Job(s, completed).GetString("status"));
            Provision(s); string running = Start(s, "newer-running"); s.AdvanceCrafting(Job(s, running).GetFloat("required_seconds") / 4.0);
            Queue(s, "newer-queued"); GdDict newer = Valid(s);
            Assert.AreEqual(3, Jobs(newer).Count);
            Assert.IsFalse(V.VariantEquals(Paid(empty).Get("reward_history"), Paid(newer).Get("reward_history")), "Real completion created newer history.");
            s.HomeShip.LootedContainerIds.Remove("restore-full-world-witness");
            s.HomeShip.LootedContainerIds.Add("restore-unsaved-world-witness");
            rig.Scene.PlayerPosition = s.HomeShip.SceneRoot.GlobalTransform * new Vec3(5, 1.25, 7);
            Assert.IsFalse(s.HomeShip.LootedContainerIds.Contains("restore-full-world-witness"));
            Assert.IsTrue(s.HomeShip.LootedContainerIds.Contains("restore-unsaved-world-witness"));
            Assert.IsFalse(V.VariantEquals(savedLoot, s.HomeShip.LootedContainerIds), "Changed live loot prerequisite.");
            Assert.IsFalse(V.VariantEquals(savedPose, GdArray.Of(5.0, 1.25, 7.0)), "Changed live pose prerequisite.");
            // Load's existing refusal before-image now includes this deliberately changed live world.
            Load(rig, selected); InstalledWithConsentPause(s, empty); AssertSelectedWorld();
            Exact(empty, s.CapturePaidCraftingDomain(), "Empty owner/history replaces, never merges newer state.");
            Assert.IsTrue(Jobs(s.CapturePaidCraftingDomain()).IsEmpty);
            Assert.IsFalse(s.CraftingState.IsCrafting()); Assert.IsFalse(s.FieldCraftingState.IsCrafting());
            Assert.AreEqual(0, s.CraftingState.GetStation(Kind).Queue.Count);
            Assert.AreEqual(0L, s.InventoryState.GetQuantity("plating")); Ingredients(s, 0);
            NoAutomaticWork(s); Load(rig, selected); Exact(empty, s.CapturePaidCraftingDomain(), "Repeated empty selection remains exact.");
            AssertSelectedWorld();
        }
    }
}
