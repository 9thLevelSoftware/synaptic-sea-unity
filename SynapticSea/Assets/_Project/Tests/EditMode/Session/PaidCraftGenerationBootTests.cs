// ARTIFACT DRAFT ONLY. ROOT promotes after runtime Restore8 GREEN and source freeze.
// Two existing-public-Create cases. No .meta or Assets installation.
using System;
using System.Diagnostics;
using System.Globalization;
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
    public class PaidCraftGenerationBootTests : InfraDataTestBase
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
            s.HomeShip.LootedContainerIds.Add("boot-full-world-witness");
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
            long start = Stopwatch.GetTimestamp();
            bool success;
            try { success = s.RequestSaveToSlot(slot, kind, "Paid boot acceptance"); }
            finally { Timing("RequestSaveToSlot", s.ComponentIntegrationEnabled, start); }
            Assert.IsTrue(success, Detail(s.LastSaveResult));
            GdDict selected = s.SaveLoadService.SelectGeneration(slot);
            Assert.IsTrue(selected.GetBool("ok"), Detail(selected));
            Assert.AreEqual(s.ComponentIntegrationEnabled ? PaidSnapshotCodec.DiagnosticMode : PaidSnapshotCodec.OrdinaryMode, selected.GetString("save_mode"));
            GdDict payload = selected.GetDictOrEmpty("payloads");
            Assert.Greater(payload.GetArrayOrEmpty("artifacts").Count, 0, "Actual full document closure.");
            GdDict admitted = s.SaveLoadService.ComponentCoordinator().ValidateSuppliedPayload(payload, s.RunId, slot);
            Assert.IsTrue(admitted.GetBool("ok"), Detail(admitted));
            GdDict exact = s.SaveLoadService.ReadGeneration(s.RunId, slot, selected.GetString("generation_id"), selected.GetString("manifest_sha256"));
            Assert.IsTrue(exact.GetBool("ok"), Detail(exact)); Exact(payload, exact.Get("payloads"), "Exact selected full payload.");
            GdDict run = GdJson.Parse(payload.GetString("run_text"), true) as GdDict;
            GdDict world = GdJson.Parse(payload.GetString("world_text"), true) as GdDict;
            Assert.IsNotNull(run); Assert.IsNotNull(world);
            Assert.IsTrue(world.GetArrayOrEmpty("home_looted_containers").Contains("boot-full-world-witness"));
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

        static void Timing(string operation, bool combined, long start)
        {
            long stop = Stopwatch.GetTimestamp();
            TestContext.WriteLine("PAID_BOOT_TIMING; operation=" + operation +
                "; mode=" + (combined ? "combined" : "ordinary") +
                "; elapsed_ms=" + ((stop - start) * 1000.0 / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture));
        }

        SessionHarness.Rig SelectedBoot(SessionHarness.Rig previous, GdDict selected, bool combined, string label)
        {
            // Fresh scene, host, models and service; only fixture-owned storage is shared.
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig next);
            SessionHarness.OverlayGamePlayability(deps);
            deps.EnablePaidCrafting = true; deps.EnableComponentIntegration = combined;
            next.Storage = previous.Storage; deps.Storage = previous.Storage;
            next.Clock.Advance(86400); // No wall-clock sleep; saved work must not catch up.
            deps.SelectedSaveGeneration = selected.DeepCopy();
            GdDict before = Live(previous), selectionBefore = selected.DeepCopy();
            var bytes = SlotPayloadBindingTests.Bytes(previous.Storage);
            var allOwnedBytes = SlotPayloadBindingTests.Bytes(previous.Storage, "user://");
            RunSession old = previous.Session;
            var oldHome = old.HomeShip; var oldLoader = old.Loader; var oldRoot = oldHome.SceneRoot;
            var wrappers = old.CraftingStations.ToArray();
            var validWrappers = wrappers.Select(st => st.IsValid).ToArray();
            var parents = wrappers.Select(st => st.Parent).ToArray();
            var attached = previous.Host.Attached.ToArray(); var freed = previous.Host.Freed.ToArray();
            int homeLoads = previous.Host.HomeLoads;
            bool valid = oldRoot.IsValid, inTree = oldRoot.IsInsideTree;
            Xform3 transform = oldRoot.GlobalTransform;
            int ready = 0, failed = 0;
            Action<RunSession> observe = session => {
                session.PlayableReady += _ => ready++;
                session.PlayableFailed += _ => failed++;
            };
            long start = Stopwatch.GetTimestamp();
            try { next.Session = RunSession.Create(deps, observe); }
            finally { Timing("Create.Selected." + label, combined, start); }
            _sessions.Add(next.Session);
            // All assertions/log formatting are outside the measured public call.
            SlotPayloadBindingTests.SameBytes(bytes, previous.Storage);
            Exact(selectionBefore, selected, "Caller-owned exact selection is unchanged.");
            Exact(before, Live(previous), "Fresh selected boot preserves the previous live session.");
            Assert.AreSame(oldHome, old.HomeShip); Assert.AreSame(oldLoader, old.Loader);
            Assert.AreSame(oldRoot, old.HomeShip.SceneRoot);
            Assert.AreEqual(valid, oldRoot.IsValid); Assert.AreEqual(inTree, oldRoot.IsInsideTree);
            Assert.AreEqual(transform, oldRoot.GlobalTransform);
            Assert.AreEqual(homeLoads, previous.Host.HomeLoads);
            CollectionAssert.AreEqual(wrappers, old.CraftingStations);
            CollectionAssert.AreEqual(validWrappers, old.CraftingStations.Select(st => st.IsValid).ToArray());
            CollectionAssert.AreEqual(parents, old.CraftingStations.Select(st => st.Parent).ToArray());
            CollectionAssert.AreEqual(attached, previous.Host.Attached);
            CollectionAssert.AreEqual(freed, previous.Host.Freed);
            RunSession loaded = next.Session;
            TestContext.WriteLine("PAID_BOOT_ROUTE=" + label + "; playable=" + loaded.PlayableStarted +
                "; failure=" + loaded.LastFailureReason + "; " + Detail(loaded.LastSaveResult));
            if (!loaded.PlayableStarted)
            {
                var afterOwnedBytes = SlotPayloadBindingTests.Bytes(previous.Storage, "user://");
                Assert.AreEqual(allOwnedBytes.Count, afterOwnedBytes.Count, "Refused boot preserves all owned memory files.");
                foreach (var pair in allOwnedBytes)
                    Assert.IsTrue(afterOwnedBytes.TryGetValue(pair.Key, out string text) && text == pair.Value,
                        "Refused boot changed owned memory bytes at " + pair.Key);
                Assert.AreEqual(0, ready, "Refused Create never publishes ready.");
                Assert.Greater(failed, 0, "Refused Create reports failure.");
                Assert.IsFalse(next.Scene.HasPlayer, "The refused fresh boot is not a playable scene.");
            }
            Assert.IsTrue(loaded.PlayableStarted,
                "Actual selected Create required; subsequent restore assertions are UNREACHED on refusal: " + loaded.LastFailureReason);
            Assert.AreEqual(1, ready); Assert.AreEqual(0, failed);
            Assert.IsTrue(loaded.PaidCraftingEnabled);
            Assert.AreEqual(combined, loaded.ComponentIntegrationEnabled);
            Assert.AreEqual(selected.GetString("run_id"), loaded.RunId);
            Assert.AreEqual(loaded.RunId, loaded.SaveLoadService.GetActiveRunId());
            Assert.AreNotSame(oldHome, loaded.HomeShip);
            Assert.AreNotSame(oldRoot, loaded.HomeShip.SceneRoot);
            return next;
        }

        static void SelectedWorld(SessionHarness.Rig rig, GdDict world)
        {
            Assert.IsTrue(rig.Scene.HasPlayer);
            Assert.AreEqual("", world.GetString("current_location"), "This bounded control saves at home.");
            Assert.AreEqual(world.GetString("aboard_ship_id"), rig.Session.CurrentOccupancy.ShipId);
            Exact(world.Get("home_looted_containers"), rig.Session.HomeShip.LootedContainerIds, "Selected loot restored exactly.");
            Vec3 local = SessionMath.AffineInverse(rig.Session.HomeShip.SceneRoot.GlobalTransform) * rig.Scene.PlayerPosition;
            Exact(world.Get("player_position_in_ship"), GdArray.Of((double)local.X, (double)local.Y, (double)local.Z),
                "Selected local pose restored exactly.");
            Assert.IsTrue(rig.Session.CraftingStations.Any(st => st.IsValid && st.StationKind == Kind &&
                ReferenceEquals(st.Parent, rig.Session.HomeShip.SceneRoot)));
            Assert.IsNotNull(rig.Session.CraftingState.GetStation(Kind));
        }

        static void CompleteOnce(RunSession session, string id)
        {
            Ingredients(session, 0);
            Assert.IsTrue(session.ResumePaidCraft(id, "boot-explicit-consent").GetBool("ok"));
            Ingredients(session, 0); // No second debit after selected boot.
            session.AdvanceCrafting(1000);
            Assert.AreEqual(1L, session.InventoryState.GetQuantity("plating"));
            DeliveredOnce(session, id, "boot-delivered-retry");
        }

        [TestCase(false)] [TestCase(true)]
        public void SelectedCreate_RestoresRunningPaidWorld_AndRepeatedFreshCreateRewinds(bool combined)
        {
            SessionHarness.Rig source = Boot(combined);
            RunSession original = source.Session;
            Provision(original);
            string id = Start(original, "boot-paid-start");
            Ingredients(original, 0);
            original.AdvanceCrafting(Job(original, id).GetFloat("required_seconds") / 4.0);
            Assert.AreEqual("running", Job(original, id).GetString("status"));
            Assert.Greater(Job(original, id).GetFloat("progress_seconds"), 0);
            GdDict selected = Save(source, "world", out GdDict saved);
            GdDict world = GdJson.Parse(selected.GetDictOrEmpty("payloads").GetString("world_text"), true) as GdDict;
            Assert.IsNotNull(world); SelectedWorld(source, world);
            Assert.AreEqual(0L, original.InventoryState.GetQuantity("plating"));

            // Distinguish the selected world/owner from the still-live source.
            original.AdvanceCrafting(Job(original, id).GetFloat("required_seconds") / 4.0);
            original.HomeShip.LootedContainerIds.Remove("boot-full-world-witness");
            original.HomeShip.LootedContainerIds.Add("boot-unsaved-world-witness");
            source.Scene.PlayerPosition = original.HomeShip.SceneRoot.GlobalTransform * new Vec3(5, 1.25, 7);
            Assert.Greater(Job(original, id).GetFloat("progress_seconds"), Jobs(saved).GetDictOrEmpty(id).GetFloat("progress_seconds"));
            GdDict sourceBefore = Live(source);

            SessionHarness.Rig first = SelectedBoot(source, selected, combined, "first");
            InstalledWithConsentPause(first.Session, saved); SelectedWorld(first, world);
            Assert.AreEqual(0L, first.Session.InventoryState.GetQuantity("plating"));
            NoAutomaticWork(first.Session); CompleteOnce(first.Session, id);
            GdDict completed = Valid(first.Session).DeepCopy();

            // Same immutable selection, fresh models again; prior completion is not merged.
            first.Session.HomeShip.LootedContainerIds.Add("boot-post-completion-world-witness");
            first.Scene.PlayerPosition = first.Session.HomeShip.SceneRoot.GlobalTransform * new Vec3(9, 2, 3);
            SessionHarness.Rig repeated = SelectedBoot(first, selected, combined, "repeat");
            InstalledWithConsentPause(repeated.Session, saved); SelectedWorld(repeated, world);
            Assert.AreEqual(0L, repeated.Session.InventoryState.GetQuantity("plating"));
            NoAutomaticWork(repeated.Session); CompleteOnce(repeated.Session, id);
            Exact(completed.Get("participating_state"), Valid(repeated.Session).Get("participating_state"),
                "Repeated fresh Create rewinds saved rewards/history; consent produces one equivalent result.");
            Exact(sourceBefore, Live(source), "Both independent boots preserve the original source session.");
        }
    }
}
