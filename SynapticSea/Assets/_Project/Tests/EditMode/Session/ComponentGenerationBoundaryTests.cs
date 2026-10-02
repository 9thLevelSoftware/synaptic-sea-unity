using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
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
    public class ComponentGenerationBoundaryTests
    {
        IEngineInfo _engine;
        IResourceReader _resources;
        [SetUp] public void Setup() { _engine = CoreServices.Engine; _resources = CoreServices.Resources; CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion); CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot); CatalogRegistry.Clear(); }
        [TearDown] public void Cleanup() { CatalogRegistry.Clear(); CoreServices.Engine = _engine; CoreServices.Resources = _resources; }
        static SessionHarness.Rig Create(IStorage storage = null, GdDict selected = null, bool diagnostic = true)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            deps.EnableComponentIntegration = diagnostic; deps.SelectedSaveGeneration = selected?.DeepCopy();
            if (storage != null) deps.Storage = storage;
            rig.Session = RunSession.Create(deps); return rig;
        }
        static GdDict Payload(RunSession s, string slot = "world", string kind = "world")
        {
            GdDict assembled = SavePayloadAssembler.Build(s, slot, kind);
            Assert.IsTrue(assembled.GetBool("ok"), ResultSummary(assembled)); return assembled.GetDictOrEmpty("payloads");
        }
        static string ResultSummary(GdDict result) => "reason=" + result.GetString("reason") + ", committed=" + result.GetBool("committed") + ", outcome=" + result.GetString("outcome") + ", detail=" + result.GetString("detail");
        static SaveCommitCoordinator Coordinator(IStorage storage, GdDict payload, Action<string> fault = null)
            => new SaveCommitCoordinator(storage, SaveLoadService.ComponentGenerationRoot, new GenerationAuthority(), payload.GetDictOrEmpty("compatibility"), fault, true);
        static string NativeDirectory()
        {
            string parent = Environment.GetEnvironmentVariable("SYNAPTIC_CATALOG_EVIDENCE_DIR");
            if (string.IsNullOrEmpty(parent)) parent = Path.Combine(Path.GetTempPath(), "synaptic-component-evidence");
            parent = Path.GetFullPath(parent); Directory.CreateDirectory(parent);
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("evidence parent is a reparse point");
            string child = Path.GetFullPath(Path.Combine(parent, "c" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            if (!child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("invalid evidence child");
            Directory.CreateDirectory(child); return child;
        }
        static string GenerationPath(GdDict payload) => SaveLoadService.ComponentGenerationRoot + "/g/" + GenerationFixtures.TupleToken(payload.GetString("run_id"), payload.GetString("slot_id"), payload.GetString("generation_id"));
        static void Evidence(string directory, FileSystemStorage storage, GdDict payload, GdDict result, string fault = "")
        {
            GdDict hashes = new GdDict(); int longest = 0;
            foreach (var pair in SlotPayloadBindingTests.Bytes(storage))
            {
                hashes[pair.Key] = GenerationFixtures.Hash(pair.Value); longest = Math.Max(longest, storage.Globalize(pair.Key).Length);
            }
            var record = new GdDict { { "test", TestContext.CurrentContext.Test.Name }, { "directory", directory }, { "diagnostic_root", storage.Globalize(SaveLoadService.ComponentGenerationRoot) },
                { "longest_owned_physical_path", longest }, { "fault", fault }, { "payload_hash", payload == null ? "" : GenerationFixtures.Hash(GdJson.Stringify(payload)) }, { "result", result }, { "owned_hashes", hashes } };
            File.WriteAllText(Path.Combine(directory, "component-evidence.json"), GdJson.Stringify(record, "  "), new UTF8Encoding(false, true));
        }
        [TestCase("world", "world")][TestCase("autosave_active", "auto")]
        [TestCase("autosave_a", "auto")][TestCase("autosave_b", "auto")][TestCase("autosave_c", "auto")]
        [TestCase("quicksave", "quick")][TestCase("slot_01", "manual")][TestCase("slot_02", "manual")]
        [TestCase("slot_03", "manual")][TestCase("slot_04", "manual")][TestCase("slot_05", "manual")][TestCase("slot_06", "manual")]
        public void NativeActualSessionBundleRestartsEveryFamilyWithExactUtf8(string slot, string kind)
        {
            string directory = NativeDirectory(); var storage = new FileSystemStorage(directory); GdDict payload = null, selected = null;
            try
            {
                var rig = Create(storage); rig.Scene.PlayerPosition = new Vec3(1, .55, 2); rig.Session.HomeShip.LootedContainerIds.Add("native-witness");
                Assert.IsTrue(rig.Session.RequestSaveToSlot(slot, kind, "Native actual world"), ResultSummary(rig.Session.LastSaveResult));
                selected = new SaveLoadService(new FileSystemStorage(directory), new ManualClock(), true).SelectGeneration(slot);
                Assert.IsTrue(selected.GetBool("ok"), ResultSummary(selected)); payload = selected.GetDictOrEmpty("payloads");
                Assert.AreEqual("world-5", ((GdDict)GdJson.ParseString(payload.GetString("world_text"))).GetString("slice_version"));
                string generation = GenerationPath(payload);
                CollectionAssert.AreEqual(new UTF8Encoding(false, true).GetBytes(payload.GetString("run_text")), File.ReadAllBytes(storage.Globalize(generation + "/run.json")));
                CollectionAssert.AreEqual(new UTF8Encoding(false, true).GetBytes(payload.GetString("world_text")), File.ReadAllBytes(storage.Globalize(generation + "/world.json")));
                int ordinal = 0;
                foreach (GdDict artifact in payload.GetArrayOrEmpty("artifacts").OfType<GdDict>().OrderBy(a => a.GetString("logical_path"), StringComparer.Ordinal))
                    CollectionAssert.AreEqual(new UTF8Encoding(false, true).GetBytes(artifact.GetString("text")), File.ReadAllBytes(storage.Globalize(generation + "/" + (ordinal++).ToString("x8") + ".json")));
                var restarted = Create(new FileSystemStorage(directory), selected);
                Assert.IsTrue(restarted.Session.PlayableStarted, restarted.Session.LastFailureReason);
                Assert.IsTrue(restarted.Session.ApplySelectedGeneration(selected), ResultSummary(restarted.Session.LastSaveResult));
                Assert.IsTrue(restarted.Session.HomeShip.LootedContainerIds.Contains("native-witness")); Assert.AreEqual(new Vec3(1, .55, 2), restarted.Scene.PlayerPosition);
                Assert.IsFalse(storage.FileExists(SaveLoadService.WORLD_SLOT_FILE)); Assert.IsFalse(storage.FileExists(SaveLoadService.SAVE_PATH));
            }
            finally { Evidence(directory, storage, payload, selected); }
        }
        [TestCase("after_payload:run")][TestCase("after_manifest")][TestCase("before_pointer")][TestCase("after_pointer")]
        public void NativeInterruptedActualBundleRetainsRecoverableWholeSelection(string stage)
        {
            string directory = NativeDirectory(); var storage = new FileSystemStorage(directory); GdDict child = null, result = null;
            try
            {
                var rig = Create(storage); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Before interruption"));
                GdDict old = rig.Session.SaveLoadService.SelectGeneration("world"); rig.Session.HomeShip.LootedContainerIds.Add("after-fault"); child = Payload(rig.Session);
                result = Coordinator(storage, child, hit => { if (hit == stage) throw new IOException("injected diagnostic publication interruption"); }).Commit(child, child.GetString("run_id"), "world");
                GdDict restarted = new SaveLoadService(new FileSystemStorage(directory), new ManualClock(), true).SelectGeneration("world"); Assert.IsTrue(restarted.GetBool("ok"), ResultSummary(restarted));
                GdDict expected = stage == "after_pointer" ? child : old.GetDictOrEmpty("payloads");
                Assert.AreEqual(expected.GetString("generation_id"), restarted.GetString("generation_id")); Assert.IsTrue(V.VariantEquals(expected, restarted.Get("payloads")));
                GdDict recovered = Coordinator(new FileSystemStorage(directory), child).Recover(child.GetString("run_id"), "world");
                Assert.IsTrue(recovered.GetBool("ok"), ResultSummary(recovered)); Assert.IsTrue(V.VariantEquals(expected, recovered.Get("payloads")));
            }
            finally { Evidence(directory, storage, child, result, stage); }
        }
        [Test]
        public void DiagnosticOverlongPhysicalRootRefusesBeforeAnyWrite()
        {
            var rig = Create(); GdDict payload = Payload(rig.Session); var storage = new GenerationStorage(new MemoryStorage()) { GlobalizeOverride = p => "C:/" + new string('x', 160) + "/" + p.Substring("user://".Length) };
            GdDict result = Coordinator(storage, payload).Commit(payload, payload.GetString("run_id"), "world");
            Assert.AreEqual("path_budget_exceeded", result.GetString("reason")); Assert.AreEqual(0, storage.Writes); Assert.AreEqual(0, storage.DirectoryPaths.Count);
        }
        [TestCase("run", "gate2-current-run-8")][TestCase("world", "world-6")]
        public void DiagnosticFutureSchemasRefuseWithoutTouchingSelectedBytes(string role, string version)
        {
            var rig = Create(); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "old")); var before = SlotPayloadBindingTests.Bytes(rig.Storage); GdDict child = Payload(rig.Session);
            var document = (GdDict)GdJson.ParseString(child.GetString(role + "_text")); document["slice_version"] = version; child[role + "_text"] = GdJson.Stringify(document);
            Assert.IsFalse(Coordinator(rig.Storage, child).Commit(child, child.GetString("run_id"), "world").GetBool("ok")); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [Test]
        public void OrdinaryQueuedCraftSaveAndContinueRemainLegacy()
        {
            var rig = Create(diagnostic: false); GdDict craft = rig.Session.CraftingState.GetSummary();
            craft["station_summaries"] = new GdDict { { "workbench", new GdDict { { "queue", GdArray.Of("ordinary-queued") } } } }; rig.Session.CraftingState.ApplySummary(craft);
            Assert.IsTrue(rig.Session.RequestSave()); Assert.IsTrue(rig.Session.RequestLoad());
            Assert.IsTrue(rig.Session.CraftingState.GetSummary().GetDictOrEmpty("station_summaries").GetDictOrEmpty("workbench").GetArrayOrEmpty("queue").Contains("ordinary-queued"));
            Assert.IsFalse(rig.Storage.DirExists(SaveLoadService.ComponentGenerationRoot));
        }
        [Test]
        public void DiagnosticNamespaceCannotHideOrReplaceOrdinarySaveAndIndex()
        {
            var ordinary = Create(diagnostic: false); Assert.IsTrue(ordinary.Session.RequestSave()); var original = SlotPayloadBindingTests.Bytes(ordinary.Storage);
            var diagnostic = Create(ordinary.Storage); Assert.IsTrue(diagnostic.Session.RequestSaveToSlot("world", "world", "Diagnostic"));
            foreach (var pair in original) Assert.AreEqual(pair.Value, ordinary.Storage.ReadText(pair.Key), pair.Key);
            var service = new SaveLoadService(ordinary.Storage, ordinary.Clock); Assert.IsNotNull(service.LoadWorld()); Assert.IsTrue(TitleSaveQuery.IsContinueAvailable(service, new PermadeathResolver(ordinary.Storage, ordinary.Clock)));
            Assert.IsTrue(diagnostic.Session.SaveLoadService.DeleteSlot("world")); Assert.IsNotNull(service.LoadWorld());
            foreach (var pair in original) Assert.AreEqual(pair.Value, ordinary.Storage.ReadText(pair.Key), pair.Key);
        }
        [TestCase("world-1")][TestCase("world-2")][TestCase("world-3")][TestCase("world-4")]
        public void LegacyWorldDiagnosticRefusalKeepsOriginalBytes(string version)
        {
            var rig = Create(diagnostic: false); Assert.IsTrue(rig.Session.RequestSave()); GdDict world = (GdDict)GdJson.ParseString(rig.Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE)); world["slice_version"] = version;
            rig.Storage.WriteText(SaveLoadService.WORLD_SLOT_FILE, GdJson.Stringify(world)); var before = SlotPayloadBindingTests.Bytes(rig.Storage);
            Assert.AreEqual("legacy_component_conversion_unavailable", new SaveLoadService(rig.Storage, rig.Clock, true).SelectGeneration("world").GetString("reason")); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [Test]
        public void LegacyUnverifiedQueueRefusalKeepsOriginalContinueAvailable()
        {
            var rig = Create(diagnostic: false); GdDict craft = rig.Session.CraftingState.GetSummary(); craft["station_summaries"] = new GdDict { { "workbench", new GdDict { { "queue", GdArray.Of("legacy-queue") } } } }; rig.Session.CraftingState.ApplySummary(craft);
            Assert.IsTrue(rig.Session.RequestSave()); var before = SlotPayloadBindingTests.Bytes(rig.Storage);
            Assert.AreEqual("craft_payment_unverified", new SaveLoadService(rig.Storage, rig.Clock, true).SelectGeneration("world").GetString("reason")); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
            Assert.IsTrue(rig.Session.RequestLoad());
        }
        [Test]
        public void DeathAfterSelectionDeniesExactApplyAndFreshBootBeforeHostEffects()
        {
            var rig = Create(); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Live")); GdDict selected = rig.Session.SaveLoadService.SelectGeneration("world");
            rig.Session.SaveLoadService.FreezeRun(selected.GetString("run_id"), "death", "selected death", 10, 1); var before = SlotPayloadBindingTests.Bytes(rig.Storage);
            int loads = rig.Host.HomeLoads, spawns = rig.Scene.SpawnCount;
            Assert.IsFalse(rig.Session.ApplySelectedGeneration(selected)); Assert.AreEqual(loads, rig.Host.HomeLoads); Assert.AreEqual(spawns, rig.Scene.SpawnCount);
            var boot = Create(rig.Storage, selected); Assert.IsFalse(boot.Session.PlayableStarted); Assert.AreEqual(0, boot.Host.HomeLoads); Assert.AreEqual(0, boot.Scene.SpawnCount); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [Test]
        public void MutatedRetainedBootHandleRefusesBeforeHostEffects()
        {
            var rig = Create(); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Live")); GdDict selected = rig.Session.SaveLoadService.SelectGeneration("world"); selected.GetDictOrEmpty("payloads")["world_text"] = "{}";
            selected["payloads_sha256"] = GenerationFixtures.Hash(GdJson.Stringify(selected.GetDictOrEmpty("payloads"))); var before = SlotPayloadBindingTests.Bytes(rig.Storage);
            var boot = Create(rig.Storage, selected); Assert.IsFalse(boot.Session.PlayableStarted); Assert.AreEqual("selection_mismatch", boot.Session.LastFailureReason); Assert.AreEqual(0, boot.Host.HomeLoads); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [Test]
        public void ExplicitNewDiagnosticRunReclaimsTerminalSlotWithoutRevivingOldHandle()
        {
            var old = Create(); Assert.IsTrue(old.Session.RequestSaveToSlot("slot_01", "manual", "Old")); GdDict selected = old.Session.SaveLoadService.SelectGeneration("slot_01");
            old.Session.SaveLoadService.FreezeRun(selected.GetString("run_id"), "death", "old epitaph", 10, 1); string tombstone = SaveLoadService.ComponentGenerationRoot + "/r/" + GenerationFixtures.Hash(selected.GetString("run_id")) + "/terminal.json"; string terminal = old.Storage.ReadText(tombstone);
            var newer = Create(old.Storage); Assert.IsTrue(newer.Session.RequestSaveToSlot("slot_01", "manual", "New"), ResultSummary(newer.Session.LastSaveResult));
            GdDict current = newer.Session.SaveLoadService.SelectGeneration("slot_01"); Assert.IsTrue(current.GetBool("ok")); Assert.AreNotEqual(selected.GetString("run_id"), current.GetString("run_id"));
            Assert.AreEqual(terminal, old.Storage.ReadText(tombstone)); Assert.IsTrue(old.Storage.FileExists(GenerationPath(selected.GetDictOrEmpty("payloads")) + "/commit.json"));
            Assert.IsFalse(newer.Session.SaveLoadService.ReadGeneration(selected.GetString("run_id"), "slot_01", selected.GetString("generation_id"), selected.GetString("manifest_sha256")).GetBool("ok"));
        }
        [Test]
        public void MatchingOriginalRunDeathDeniesEncodedDiagnosticSelection()
        {
            var rig = Create(); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Live")); GdDict selected = rig.Session.SaveLoadService.SelectGeneration("world");
            rig.Storage.WriteText(SaveLoadService.WORLD_SLOT_FILE, GdJson.Stringify(new GdDict { { "run_id", selected.GetString("run_id") } }));
            new PermadeathResolver(rig.Storage, rig.Clock).RecordDeath("world", "death", "Original same run", 10, 1); var before = SlotPayloadBindingTests.Bytes(rig.Storage);
            Assert.IsFalse(new SaveLoadService(rig.Storage, rig.Clock, true).ReadGeneration(selected.GetString("run_id"), "world", selected.GetString("generation_id"), selected.GetString("manifest_sha256")).GetBool("ok")); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [Test]
        public void DeathDuringImmutableRereadRefusesWithoutReturningPayloads()
        {
            var memory = new MemoryStorage(); var storage = new GenerationStorage(memory); var rig = Create(storage); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Live")); GdDict selected = rig.Session.SaveLoadService.SelectGeneration("world");
            bool fired = false; storage.AfterRead = (path, text) => { if (!fired && path.EndsWith("/world.json", StringComparison.Ordinal)) { fired = true; rig.Session.SaveLoadService.FreezeRun(selected.GetString("run_id"), "death", "read race", 10, 1); } };
            GdDict result = rig.Session.SaveLoadService.ReadGeneration(selected.GetString("run_id"), "world", selected.GetString("generation_id"), selected.GetString("manifest_sha256")); Assert.IsTrue(fired); Assert.IsFalse(result.GetBool("ok")); Assert.IsNull(result.Get("payloads"));
        }
        [Test]
        public void SameRunManualDeleteThenSavePublishesAUsableNewSelection()
        {
            var rig = Create(); Assert.IsTrue(rig.Session.RequestSaveToSlot("slot_01", "manual", "Old"), ResultSummary(rig.Session.LastSaveResult)); GdDict old = rig.Session.SaveLoadService.SelectGeneration("slot_01");
            Assert.IsTrue(rig.Session.SaveLoadService.DeleteSlot("slot_01")); Assert.IsFalse(rig.Session.SaveLoadService.SelectGeneration("slot_01").GetBool("ok"));
            Assert.IsTrue(rig.Session.RequestSaveToSlot("slot_01", "manual", "New"), ResultSummary(rig.Session.LastSaveResult));
            GdDict selected = rig.Session.SaveLoadService.SelectGeneration("slot_01"); Assert.IsTrue(selected.GetBool("ok"), ResultSummary(selected)); Assert.AreNotEqual(old.GetString("generation_id"), selected.GetString("generation_id"));
        }
        [TestCase(false)][TestCase(true)]
        public void LockedDeletionMarkerNeverReportsAPlayableCommitIncludingExactRepeat(bool repeat)
        {
            var memory = new MemoryStorage(); var old = Create(memory); Assert.IsTrue(old.Session.RequestSaveToSlot("slot_01", "manual", "Old"), ResultSummary(old.Session.LastSaveResult)); Assert.IsTrue(old.Session.SaveLoadService.DeleteSlot("slot_01"));
            var fresh = Create(memory); GdDict request = Payload(fresh.Session, "slot_01", "manual");
            var fault = new GenerationStorage(memory) { DeleteResult = path => path.EndsWith("/deleted.json", StringComparison.Ordinal) ? (bool?)false : null };
            GdDict result = Coordinator(fault, request).CommitNewRun(request, request.GetString("run_id"), "slot_01");
            if (repeat) result = Coordinator(fault, request).CommitNewRun(request, request.GetString("run_id"), "slot_01");
            Assert.IsFalse(result.GetBool("ok"), "A locked visibility marker cannot report a playable commit"); Assert.IsTrue(result.GetBool("committed"), "Verified pointer bytes remain committed independently of visibility reconciliation");
            Assert.AreEqual("slot_deletion_reconciliation_needed", result.GetString("reason")); Assert.IsFalse(Coordinator(fault, request).ReadSelected("slot_01").GetBool("ok"));
            fault.DeleteResult = null; GdDict repaired = Coordinator(fault, request).CommitNewRun(request, request.GetString("run_id"), "slot_01");
            Assert.IsTrue(repaired.GetBool("ok"), ResultSummary(repaired)); Assert.IsTrue(Coordinator(fault, request).ReadSelected("slot_01").GetBool("ok"));
        }
        [Test]
        public void FailedDiagnosticDeathFinalWriteBlocksFreshContinueWithoutCompletionOrPayout()
        {
            var memory = new MemoryStorage(); var storage = new GenerationStorage(memory); var rig = Create(storage); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Live"), ResultSummary(rig.Session.LastSaveResult));
            int completions = 0; rig.Session.PlayableSliceCompleted += ignored => completions++;
            storage.BeforeWrite = (path, text) => { if (path.EndsWith("/terminal.json", StringComparison.Ordinal)) throw new IOException("terminal final write locked"); };
            Assert.AreEqual(0, rig.Session.EndRun("death")); Assert.IsFalse(rig.Session.LastSaveResult.GetBool("ok"));
            Assert.IsFalse(rig.Session.SliceComplete, "Failed durable terminal publication cannot complete or pay out the run"); Assert.AreEqual(0, completions);
            Assert.IsFalse(new SaveLoadService(memory, rig.Clock, true).SelectGeneration("world").GetBool("ok"), "Owned durable terminal intent denies restart when final publication fails");
            storage.BeforeWrite = null; rig.Session.EndRun("death"); Assert.IsTrue(rig.Session.SliceComplete); Assert.AreEqual(1, completions);
        }
        [Test]
        public void PendingDiagnosticDeathStopsTimeSaveLoadAndDirectComponentAdmission()
        {
            var memory = new MemoryStorage(); var storage = new GenerationStorage(memory); var rig = Create(storage);
            Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Live"), ResultSummary(rig.Session.LastSaveResult));
            GdDict item = rig.Session.CaptureComponentDomain().GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values.OfType<GdDict>().First();
            storage.BeforeWrite = (path, text) => { if (path.EndsWith("/terminal.json", StringComparison.Ordinal)) throw new IOException("terminal locked"); };
            rig.Session.EndRun("death"); double time = rig.Session.WorldTime; int writes = storage.Writes;
            rig.Session.Tick(new TickContext { Delta = 3, HasPlayer = true, PlayerPosition = rig.Scene.PlayerPosition });
            Assert.AreEqual(time, rig.Session.WorldTime); Assert.AreEqual(writes, storage.Writes, "Pending terminal does not retry every frame"); Assert.IsFalse(rig.Session.RequestSaveToSlot("world", "world", "No")); Assert.IsFalse(rig.Session.RequestLoad());
            Assert.AreEqual("terminal_pending", rig.Session.RequestComponentRemoval(item.GetString("instance_id")).GetString("reason"));
        }
        static void ChangeDomain(GdDict request, Action<GdDict> mutate)
        {
            var world = (GdDict)GdJson.ParseString(request.GetString("world_text")); Assert.IsTrue(ComponentDomainCodec.TryDecode(world.GetDictOrEmpty("component_domain"), out GdDict domain, out string reason), reason);
            mutate(domain); GdDict encoded = ComponentDomainCodec.Encode(domain); var run = (GdDict)GdJson.ParseString(request.GetString("run_text"));
            run["component_domain"] = encoded.DeepCopy(); world["component_domain"] = encoded.DeepCopy(); world.GetDictOrEmpty("home_ship")["component_domain"] = encoded.DeepCopy();
            request["run_text"] = GdJson.Stringify(run); request["world_text"] = GdJson.Stringify(world);
        }
        [TestCase("inventory")][TestCase("progression")][TestCase("home_cargo")][TestCase("lifeboat_cargo")][TestCase("home_machinery")][TestCase("lifeboat_system_health_range")]
        public void ConflictingDomainAndOrdinaryMirrorsRefuseBeforePublication(string mirror)
        {
            var rig = Create(); GdDict request = Payload(rig.Session); var before = SlotPayloadBindingTests.Bytes(rig.Storage);
            if (mirror.EndsWith("machinery", StringComparison.Ordinal))
                ChangeDomain(request, domain => domain.GetDictOrEmpty("machinery").Values.OfType<GdDict>().First(m => m.GetString("owner_id") == (mirror.StartsWith("home", StringComparison.Ordinal) ? "ship_start" : "lifeboat"))["health"] = .19);
            else
            {
                var run = (GdDict)GdJson.ParseString(request.GetString("run_text")); var world = (GdDict)GdJson.ParseString(request.GetString("world_text"));
                if (mirror == "inventory") run.GetDictOrEmpty("inventory_summary").GetDictOrEmpty("items")["scrap_metal"] = 17L;
                if (mirror == "progression") run.GetDictOrEmpty("player_progression_summary").GetDictOrEmpty("skill_xp")["salvage"] = 13.0;
                if (mirror == "home_cargo") world.GetDictOrEmpty("home_ship_inventory").GetDictOrEmpty("items")["scrap_metal"] = 17L;
                if (mirror == "lifeboat_cargo") world.GetDictOrEmpty("mobile_home_state").GetDictOrEmpty("lifeboat")["inventory"] = new GdDict { { "items", new GdDict { { "scrap_metal", 17L } } }, { "max_weight", 200.0 } };
                if (mirror == "lifeboat_system_health_range") world.GetDictOrEmpty("mobile_home_state").GetDictOrEmpty("lifeboat").GetDictOrEmpty("systems").GetDictOrEmpty("systems").Values.OfType<GdDict>().First(system => !system.GetArrayOrEmpty("subcomponents").IsEmpty).GetArrayOrEmpty("subcomponents").OfType<GdDict>().First()["health"] = 2.0;
                request["run_text"] = GdJson.Stringify(run); request["world_text"] = GdJson.Stringify(world);
            }
            GdDict result = Coordinator(rig.Storage, request).Commit(request, request.GetString("run_id"), "world"); Assert.IsFalse(result.GetBool("ok"), "Conflicting " + mirror + " cannot represent one synchronous world: " + ResultSummary(result)); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [TestCase("index")][TestCase("cell")][TestCase("anchor")]
        public void PhysicalMetadataMustMatchExactArchivedSlotAndWitness(string mutation)
        {
            var rig = Create(); GdDict request = Payload(rig.Session); var before = SlotPayloadBindingTests.Bytes(rig.Storage);
            ChangeDomain(request, domain =>
            {
                GdDict slot = domain.GetDictOrEmpty("physical_slots").Values.OfType<GdDict>().First();
                if (mutation == "index") slot["slot_index"] = slot.GetInt("slot_index") + 77;
                if (mutation == "cell") slot["cell"] = GdArray.Of(999L, 999L);
                if (mutation == "anchor") slot["local_position"] = (Vec3)slot.Get("local_position") + new Vec3(80, 0, 80);
            });
            GdDict result = Coordinator(rig.Storage, request).Commit(request, request.GetString("run_id"), "world"); Assert.IsFalse(result.GetBool("ok"), "Archived physical metadata " + mutation + " must be proved before publication"); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [Test]
        public void PureArchivedPhysicalValidationApiIsAvailable()
        {
            var method = typeof(RunSession).GetMethod("ValidateComponentPhysicalLayout", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            Assert.IsNotNull(method, "Exact archived physical slot validation must be available before save commit/load");
        }
        [TestCase("layout", "schema_version")][TestCase("layout", "document_kind")][TestCase("kit", "schema_version")][TestCase("kit", "document_kind")]
        public void FixedLifeboatArtifactBodyMustAgreeWithOwnedEnvelope(string role, string field)
        {
            var rig = Create(); GdDict request = Payload(rig.Session); var before = SlotPayloadBindingTests.Bytes(rig.Storage); GdDict reference = request.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty("lifeboat");
            string path = reference.GetString(role + "_path"); GdDict original = request.GetArrayOrEmpty("artifacts").OfType<GdDict>().Single(a => a.GetString("logical_path") == path);
            if (role == "kit")
            {
                original = original.DeepCopy(); path = "user://component-artifacts/fixed-boat-only-kit.json"; original["logical_path"] = path; reference["kit_path"] = path; request.GetArrayOrEmpty("artifacts").Add(original);
            }
            GdDict body = (GdDict)GdJson.ParseString(original.GetString("text")); body[field] = field == "schema_version" ? "99.0.0" : "unsupported_future_body"; original["text"] = GdJson.Stringify(body);
            GdDict result = Coordinator(rig.Storage, request).Commit(request, request.GetString("run_id"), "world"); Assert.IsFalse(result.GetBool("ok"), "Fixed " + role + " body cannot contradict its supported envelope"); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        sealed class RefusingAwayHost : IShipSceneHost
        {
            readonly FakeShipHost _inner; public int Refused;
            public RefusingAwayHost(FakeShipHost inner) { _inner = inner; }
            public IShipLoaderView LoadHomeShip(string layout, string kit, string slice, out string reason) => _inner.LoadHomeShip(layout, kit, slice, out reason);
            public IShipLoaderView BuildShipScene(ShipDocuments docs) { if (docs.IsAway) { Refused++; return null; } return _inner.BuildShipScene(docs); }
            public IShipSceneRoot BuildLifeboatScene(LifeBoatBuilder.BuildResult boat) => _inner.BuildLifeboatScene(boat);
            public void AttachShipRoot(IShipSceneRoot root) => _inner.AttachShipRoot(root);
            public void FreeShipRoot(IShipSceneRoot root) => _inner.FreeShipRoot(root);
            public void SetShipRootPosition(IShipSceneRoot root, Vec3 position) => _inner.SetShipRootPosition(root, position);
            public void SetShipRootGlobalTransform(IShipSceneRoot root, Xform3 value) => _inner.SetShipRootGlobalTransform(root, value);
            public bool IsParentedToSession(IShipSceneRoot root) => _inner.IsParentedToSession(root);
        }
        [Test]
        public void RequiredInactiveDockedOwnerHostFailureRefusesBeforeDespawn()
        {
            var rig = Create(); RunSession s = rig.Session; s.ForceRepairAll(); s.ThreatManager.Threats.Clear(); GdDict travelled = null;
            foreach (string id in s.ScannableMarkerIds()) { travelled = s.TravelToMarkerId(id); if (travelled.GetBool("success")) break; }
            Assert.IsNotNull(travelled); Assert.IsTrue(travelled.GetBool("success"), ResultSummary(travelled)); ShipInstance away = s.CurrentShip;
            Assert.IsTrue(s.TravelHome()); away.ParentShip = s.HomeShip; s.HomeShip.DockedShips.Add(away); away.DockingPorts = new GdArray();
            Assert.IsTrue(s.RequestSaveToSlot("world", "world", "Docked retained fixture"), ResultSummary(s.LastSaveResult)); GdDict selected = s.SaveLoadService.SelectGeneration("world");
            var host = new RefusingAwayHost(rig.Host); s.Deps.ShipHost = host; int despawns = rig.Scene.DespawnCount; Vec3 position = rig.Scene.PlayerPosition; GdDict before = s.CaptureComponentDomain();
            Assert.IsFalse(s.ApplySelectedGeneration(selected), "A required inactive saved endpoint cannot disappear from an exact successful restore"); Assert.Greater(host.Refused, 0); Assert.AreEqual(despawns, rig.Scene.DespawnCount, "Actual required detached roots must be staged before replacing the live world"); Assert.AreEqual(position, rig.Scene.PlayerPosition); Assert.IsTrue(V.VariantEquals(before, s.CaptureComponentDomain()));
        }
        [TestCase(true)][TestCase(false)]
        public void PairedLegacyManualWorldRefusalNamesActualBindingAvailability(bool paired)
        {
            var rig = Create(diagnostic: false); Assert.IsTrue(rig.Session.RequestSave());
            Assert.IsTrue(rig.Session.SaveLoadService.SaveToSlot("slot_01", RunSnapshotAssembler.Build(rig.Session), "manual", false, "Original manual"));
            if (!paired) rig.Storage.Delete(SaveLoadService.WORLD_SLOT_FILE);
            var before = SlotPayloadBindingTests.Bytes(rig.Storage); GdDict refused = new SaveLoadService(rig.Storage, rig.Clock, true).SelectGeneration("slot_01");
            Assert.AreEqual(paired ? "legacy_component_conversion_unavailable" : "legacy_world_binding_missing", refused.GetString("reason")); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        static SessionHarness.Rig RuntimeAway()
        {
            var rig = Create(); rig.Session.ForceRepairAll(); rig.Session.ThreatManager.Threats.Clear(); GdDict travelled = null;
            foreach (string id in rig.Session.ScannableMarkerIds()) { travelled = rig.Session.TravelToMarkerId(id); if (travelled.GetBool("success")) break; }
            Assert.IsNotNull(travelled); Assert.IsTrue(travelled.GetBool("success"), ResultSummary(travelled)); return rig;
        }
        static GdDict RuntimeArtifact(GdDict request, string role)
        {
            string owner = request.GetDictOrEmpty("binding").GetString("current_owner_id");
            string path = request.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty(owner).GetString(role + "_path");
            return request.GetArrayOrEmpty("artifacts").OfType<GdDict>().Single(a => a.GetString("logical_path") == path);
        }
        [Test]
        public void ActualRuntimeAwaySelectedContinuePreservesRawGameplayAndEnvelope()
        {
            var rig = RuntimeAway(); RunSession s = rig.Session; string owner = s.CurrentShip.ShipId;
            Assert.IsTrue(s.RequestSaveToSlot("world", "world", "Actual runtime away"), ResultSummary(s.LastSaveResult));
            GdDict selected = s.SaveLoadService.SelectGeneration("world"), payload = selected.GetDictOrEmpty("payloads"), archived = RuntimeArtifact(payload, "gameplay_slice");
            Assert.AreEqual("runtime_generated_gameplay_slice", archived.GetString("document_kind")); Assert.AreEqual("component-runtime-gameplay-1", archived.GetString("schema_version"));
            var body = (GdDict)GdJson.ParseString(archived.GetString("text")); Assert.AreEqual(7, body.Count); Assert.IsFalse(body.Has("document_kind")); Assert.IsFalse(body.Has("schema_version"));
            Assert.IsTrue(s.TravelHome()); Assert.IsTrue(s.ApplySelectedGeneration(selected), ResultSummary(s.LastSaveResult)); Assert.AreEqual(owner, s.CurrentShip.ShipId);
            GdDict next = RuntimeArtifact(Payload(s), "gameplay_slice"); Assert.AreEqual(archived.GetString("text"), next.GetString("text")); Assert.AreEqual(archived.GetString("document_kind"), next.GetString("document_kind")); Assert.AreEqual(archived.GetString("schema_version"), next.GetString("schema_version"));
        }
        [TestCase("seed")][TestCase("program")][TestCase("profile")][TestCase("owner")][TestCase("version")][TestCase("missing_field")][TestCase("body_metadata")][TestCase("room")][TestCase("cell")][TestCase("slot")][TestCase("published_label")]
        public void RuntimeArchiveRefusesChangedContractBeforePublication(string mutation)
        {
            var rig = RuntimeAway(); GdDict request = Payload(rig.Session); var before = SlotPayloadBindingTests.Bytes(rig.Storage);
            GdDict slice = RuntimeArtifact(request, "gameplay_slice"), layoutArtifact = RuntimeArtifact(request, "layout");
            GdDict body = (GdDict)GdJson.ParseString(slice.GetString("text")), layout = (GdDict)GdJson.ParseString(layoutArtifact.GetString("text"));
            if (mutation == "seed") layout["generation_seed"] = 123456789L;
            if (mutation == "program") layout["program_id"] = "prefix-procgen-cargo-seed-123-suffix";
            if (mutation == "profile") layout["generation_profile"] = "unknown_runtime_profile";
            if (mutation == "owner") request.GetDictOrEmpty("binding")["current_owner_id"] = "lifeboat";
            if (mutation == "version") slice["schema_version"] = "component-runtime-gameplay-99";
            if (mutation == "missing_field") body.Erase("breach_zones");
            if (mutation == "body_metadata") body["document_kind"] = "ship_gameplay_slice";
            GdDict objective = body.GetArrayOrEmpty("objectives").OfType<GdDict>().First();
            if (mutation == "room") objective["room_id"] = "missing_archived_room";
            if (mutation == "cell") objective["approach_cell"] = GdArray.Of(999L, 999L, 0L);
            if (mutation == "slot") objective["slot_index"] = 999L;
            if (mutation == "published_label") { slice["document_kind"] = "ship_gameplay_slice"; slice["schema_version"] = "1.1.0"; }
            slice["text"] = GdJson.Stringify(body); layoutArtifact["text"] = GdJson.Stringify(layout);
            GdDict result = Coordinator(rig.Storage, request).Commit(request, request.GetString("run_id"), "world"); Assert.IsFalse(result.GetBool("ok"), mutation + ": " + ResultSummary(result)); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [TestCase(true)][TestCase(false)]
        public void OriginalDeathPayloadAndIndexOwnershipMustAgree(bool contradictory)
        {
            var rig = Create(); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Live diagnostic"), ResultSummary(rig.Session.LastSaveResult)); GdDict selected = rig.Session.SaveLoadService.SelectGeneration("world");
            rig.Storage.WriteText(SaveLoadService.SAVES_DIR + "/slot_01.json", GdJson.Stringify(new GdDict { { "run_id", "legacy-unrelated" } }));
            new PermadeathResolver(rig.Storage, rig.Clock).RecordDeath("slot_01", "death", "Original witnessed run", 10, 1);
            rig.Storage.WriteText(SaveLoadService.INDEX_PATH, GdJson.Stringify(new GdDict { { "slots", GdArray.Of(new GdDict { { "slot_id", "slot_01" }, { "run_id", contradictory ? selected.GetString("run_id") : "legacy-unrelated" }, { "frozen", false } }) } }));
            var before = SlotPayloadBindingTests.Bytes(rig.Storage); GdDict result = new SaveLoadService(rig.Storage, rig.Clock, true).ReadGeneration(selected.GetString("run_id"), "world", selected.GetString("generation_id"), selected.GetString("manifest_sha256"));
            Assert.AreEqual(!contradictory, result.GetBool("ok"), "Conflicting original terminal ownership must fail closed; consistent unrelated ownership remains separate: " + ResultSummary(result)); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [TestCase("missing")][TestCase("empty")][TestCase("corrupt")][TestCase("corrupt_payload_index_S")][TestCase("unrelated")][TestCase("unrelated_no_index")]
        public void ExplicitNewAuthorizationNeedsProvedUnrelatedOriginalDeath(string evidence)
        {
            var rig = Create(); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Live"), ResultSummary(rig.Session.LastSaveResult)); GdDict selected = rig.Session.SaveLoadService.SelectGeneration("world");
            string original = SaveLoadService.SAVES_DIR + "/slot_01.json"; bool unrelated = evidence.StartsWith("unrelated", StringComparison.Ordinal);
            if (evidence == "empty") rig.Storage.WriteText(original, "{}");
            if (evidence.StartsWith("corrupt", StringComparison.Ordinal)) rig.Storage.WriteText(original, "{");
            if (unrelated) rig.Storage.WriteText(original, GdJson.Stringify(new GdDict { { "run_id", "legacy-unrelated" } }));
            new PermadeathResolver(rig.Storage, rig.Clock).RecordDeath("slot_01", "death", "Original terminal evidence", 10, 1);
            if (evidence == "unrelated" || evidence == "corrupt_payload_index_S") rig.Storage.WriteText(SaveLoadService.INDEX_PATH, GdJson.Stringify(new GdDict { { "slots", GdArray.Of(new GdDict { { "slot_id", "slot_01" }, { "run_id", "legacy-unrelated" }, { "frozen", false } }) } }));
            var before = SlotPayloadBindingTests.Bytes(rig.Storage);
            bool saved = rig.Session.RequestSaveToSlot("slot_02", "manual", "Explicit authorization still retained");
            GdDict fresh = new SaveLoadService(rig.Storage, rig.Clock, true).ReadGeneration(selected.GetString("run_id"), "world", selected.GetString("generation_id"), selected.GetString("manifest_sha256"));
            Assert.AreEqual(unrelated, saved, "Explicit creation authority cannot guess missing/corrupt terminal ownership: " + evidence + ", " + ResultSummary(rig.Session.LastSaveResult));
            Assert.AreEqual(unrelated, fresh.GetBool("ok"), "Fresh service requires the same proved association: " + evidence + ", " + ResultSummary(fresh));
            if (!unrelated) SlotPayloadBindingTests.SameBytes(before, rig.Storage);
            else foreach (var pair in before.Where(p => !p.Key.StartsWith(SaveLoadService.ComponentGenerationRoot, StringComparison.Ordinal))) Assert.AreEqual(pair.Value, rig.Storage.ReadText(pair.Key), "Original evidence must remain unchanged");
        }
        [Test]
        public void DuplicateOriginalIndexCannotOverwriteMatchingFrozenWitness()
        {
            var rig = Create(); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Live"), ResultSummary(rig.Session.LastSaveResult)); GdDict selected = rig.Session.SaveLoadService.SelectGeneration("world");
            rig.Storage.WriteText(SaveLoadService.SAVES_DIR + "/slot_01.json", GdJson.Stringify(new GdDict { { "run_id", "legacy-unrelated" } })); new PermadeathResolver(rig.Storage, rig.Clock).RecordDeath("slot_01", "death", "Original terminal", 10, 1);
            rig.Storage.WriteText(SaveLoadService.INDEX_PATH, GdJson.Stringify(new GdDict { { "slots", GdArray.Of(new GdDict { { "slot_id", "slot_01" }, { "run_id", selected.GetString("run_id") }, { "frozen", true } }, new GdDict { { "slot_id", "slot_01" }, { "run_id", "legacy-unrelated" }, { "frozen", false } }) } }));
            var before = SlotPayloadBindingTests.Bytes(rig.Storage); GdDict result = new SaveLoadService(rig.Storage, rig.Clock, true).ReadGeneration(selected.GetString("run_id"), "world", selected.GetString("generation_id"), selected.GetString("manifest_sha256")); Assert.IsFalse(result.GetBool("ok"), "A later duplicate cannot erase an existing same-run frozen witness"); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [Test]
        public void CorruptOriginalIndexCannotEraseMatchingReadableDeathPayload()
        {
            var rig = Create(); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Live"), ResultSummary(rig.Session.LastSaveResult)); GdDict selected = rig.Session.SaveLoadService.SelectGeneration("world");
            rig.Storage.WriteText(SaveLoadService.SAVES_DIR + "/slot_01.json", GdJson.Stringify(new GdDict { { "run_id", selected.GetString("run_id") } })); new PermadeathResolver(rig.Storage, rig.Clock).RecordDeath("slot_01", "death", "Matching original", 10, 1); rig.Storage.WriteText(SaveLoadService.INDEX_PATH, "{");
            var before = SlotPayloadBindingTests.Bytes(rig.Storage); Assert.IsFalse(rig.Session.RequestSaveToSlot("slot_02", "manual", "Still same run")); GdDict result = new SaveLoadService(rig.Storage, rig.Clock, true).ReadGeneration(selected.GetString("run_id"), "world", selected.GetString("generation_id"), selected.GetString("manifest_sha256")); Assert.IsFalse(result.GetBool("ok")); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [TestCase("actual")][TestCase("manager_string")][TestCase("manager_null")][TestCase("systems_string")][TestCase("systems_null")][TestCase("system_string")][TestCase("system_null")][TestCase("subcomponents_string")][TestCase("subcomponents_null")][TestCase("subcomponent_string")][TestCase("subcomponent_null")][TestCase("order_string")][TestCase("order_null")][TestCase("dependencies_string")][TestCase("dependencies_null")]
        public void RawFixedBoatManagerCannotSilentlyOmitMalformedContainers(string shape)
        {
            var rig = Create(); GdDict request = Payload(rig.Session); var world = (GdDict)GdJson.ParseString(request.GetString("world_text")); GdDict boat = world.GetDictOrEmpty("mobile_home_state").GetDictOrEmpty("lifeboat"), manager = boat.GetDictOrEmpty("systems"), systems = manager.GetDictOrEmpty("systems"), power = systems.GetDictOrEmpty("power");
            object malformed = shape.EndsWith("null", StringComparison.Ordinal) ? null : "malformed_container";
            if (shape.StartsWith("manager_", StringComparison.Ordinal)) boat["systems"] = malformed;
            if (shape.StartsWith("systems_", StringComparison.Ordinal)) manager["systems"] = malformed;
            if (shape.StartsWith("system_", StringComparison.Ordinal)) systems["power"] = malformed;
            if (shape.StartsWith("subcomponents_", StringComparison.Ordinal)) power["subcomponents"] = malformed;
            if (shape.StartsWith("subcomponent_", StringComparison.Ordinal)) power.GetArrayOrEmpty("subcomponents")[0] = malformed;
            if (shape.StartsWith("order_", StringComparison.Ordinal)) manager["system_order"] = malformed;
            if (shape.StartsWith("dependencies_", StringComparison.Ordinal)) power["dependency_ids"] = malformed;
            request["world_text"] = GdJson.Stringify(world); var before = SlotPayloadBindingTests.Bytes(rig.Storage); GdDict result = Coordinator(rig.Storage, request).Commit(request, request.GetString("run_id"), "world");
            Assert.AreEqual(shape == "actual", result.GetBool("ok"), "Original raw manager containers must retain typed membership: " + shape + ", " + ResultSummary(result));
            if (shape != "actual") SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }
        [Test]
        public void UnlinkedFixedBoatTinyHealthDeltaSurvivesExactSaveApplyAndFreshContinue()
        {
            var rig = Create(); RunSession s = rig.Session; s.ForceRepairAll();
            Assert.IsFalse(s.CaptureComponentDomain().GetDictOrEmpty("machinery").Values.OfType<GdDict>().Any(m => m.GetString("owner_id") == "lifeboat"), "This exercises actual unlinked fixed-boat raw systems");
            s.LifeboatShip.SystemsManager.GetSystem("life_support").GetSubcomponent("air_recycler").Health = .99995;
            Assert.IsTrue(s.RequestSaveToSlot("world", "world", "Exact raw boat health"), ResultSummary(s.LastSaveResult)); GdDict selected = s.SaveLoadService.SelectGeneration("world"); var before = SlotPayloadBindingTests.Bytes(rig.Storage);
            s.LifeboatShip.SystemsManager.GetSystem("life_support").GetSubcomponent("air_recycler").Health = 1;
            Assert.IsTrue(s.ApplySelectedGeneration(selected), ResultSummary(s.LastSaveResult)); double loaded = s.LifeboatShip.SystemsManager.GetSystem("life_support").GetSubcomponent("air_recycler").Health;
            var fresh = Create(rig.Storage, selected); Assert.IsTrue(fresh.Session.PlayableStarted); Assert.IsTrue(fresh.Session.ApplySelectedGeneration(selected), ResultSummary(fresh.Session.LastSaveResult)); double continued = fresh.Session.LifeboatShip.SystemsManager.GetSystem("life_support").GetSubcomponent("air_recycler").Health;
            Assert.AreEqual(.99995, loaded, "Diagnostic restore cannot suppress a finite exact saved delta using ordinary notification epsilon"); Assert.AreEqual(.99995, continued); SlotPayloadBindingTests.SameBytes(before, rig.Storage);
        }

        [TestCase("replacement_temp_lock")][TestCase("truncate_manifest")]
        public void NativeActualAssembledFaultRereadsAndRecoversWholeOldSelection(string fault)
        {
            string directory = NativeDirectory(); var native = new FileSystemStorage(directory); var storage = new GenerationStorage(native);
            GdDict child = null, result = null; FileStream locked = null;
            try
            {
                var rig = Create(storage); Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Before native failure"), ResultSummary(rig.Session.LastSaveResult));
                GdDict old = rig.Session.SaveLoadService.SelectGeneration("world"); Assert.IsTrue(old.GetBool("ok"), ResultSummary(old));
                var oldBytes = SlotPayloadBindingTests.Bytes(native); rig.Session.HomeShip.LootedContainerIds.Add("must-not-publish"); child = Payload(rig.Session);
                string pointer = SaveLoadService.ComponentGenerationRoot + "/s/" + GenerationFixtures.Hash("world") + "/active.json";
                if (fault == "replacement_temp_lock")
                {
                    string temporary = native.Globalize(pointer) + ".tmp";
                    File.WriteAllText(temporary, "task-owned locked replacement witness", new UTF8Encoding(false, true));
                    locked = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                else storage.Rewrite = (path, text) => path == GenerationPath(child) + "/commit.json" ? text.Substring(0, text.Length / 2) : text;
                result = Coordinator(storage, child).Commit(child, child.GetString("run_id"), "world");
                if (locked != null) { locked.Dispose(); locked = null; }
                Assert.IsFalse(result.GetBool("ok"), ResultSummary(result)); Assert.IsFalse(result.GetBool("committed"), ResultSummary(result));
                foreach (var pair in oldBytes) CollectionAssert.AreEqual(new UTF8Encoding(false, true).GetBytes(pair.Value), File.ReadAllBytes(native.Globalize(pair.Key)), pair.Key);
                var freshStorage = new FileSystemStorage(directory); var service = new SaveLoadService(freshStorage, new ManualClock(), true);
                GdDict reread = service.SelectGeneration("world"), recovered = Coordinator(freshStorage, child).Recover(child.GetString("run_id"), "world");
                Assert.IsTrue(reread.GetBool("ok"), ResultSummary(reread)); Assert.IsTrue(recovered.GetBool("ok"), ResultSummary(recovered));
                Assert.IsTrue(V.VariantEquals(old.Get("payloads"), reread.Get("payloads")), "Fresh read must retain the complete old bundle.");
                Assert.IsTrue(V.VariantEquals(old.Get("payloads"), recovered.Get("payloads")), "Recovery must not combine an interrupted child with old artifacts.");
                var restarted = Create(freshStorage, reread); Assert.IsTrue(restarted.Session.PlayableStarted, restarted.Session.LastFailureReason);
                Assert.IsTrue(restarted.Session.ApplySelectedGeneration(reread), ResultSummary(restarted.Session.LastSaveResult));
                Assert.IsFalse(restarted.Session.HomeShip.LootedContainerIds.Contains("must-not-publish"));
            }
            finally { if (locked != null) locked.Dispose(); Evidence(directory, native, child, result, fault); }
        }

        static SessionHarness.Rig CreateHeldWork(IStorage storage = null, GdDict selected = null)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig); SessionHarness.OverlayGamePlayability(deps);
            deps.EnableComponentIntegration = true; deps.SelectedSaveGeneration = selected?.DeepCopy(); deps.SettingsState = new SettingsState();
            if (storage != null) deps.Storage = storage;
            rig.Session = RunSession.Create(deps); Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            rig.Session.ThreatManager.Threats.Clear(); return rig;
        }
        [Test]
        public void FullGenerationPendingHeldWorkRestoresPausedAndFreshContinueResumesExactlyOnce()
        {
            var rig = CreateHeldWork(); RunSession s = rig.Session; GdDict initial = s.CaptureComponentDomain();
            GdDict row = initial.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values.OfType<GdDict>().First(i => initial.GetDictOrEmpty("holders").GetDictOrEmpty(i.GetString("holder")).GetString("kind") == "slot");
            string id = row.GetString("instance_id"); GdDict target = s.ListInstallTargets(id).OfType<GdDict>().Single(t => t.GetString("holder_id") == row.GetString("holder"));
            rig.Scene.PlayerPosition = (Vec3)target.Get("world_position"); s.InventoryState.Items["wrench"] = 1L;
            s.BeginWorkHold(); GdDict requested = s.RequestComponentRemoval(id); Assert.IsTrue(requested.GetBool("ok"), requested.GetString("reason")); Assert.IsFalse(requested.GetBool("committed")); s.StageWorkAction(1.0);
            GdDict saved = s.CaptureComponentDomain(); double progress = saved.GetDictOrEmpty("component_work").GetFloat("progress"); Assert.Greater(progress, 0);
            Assert.AreEqual(row.GetString("holder"), saved.GetDictOrEmpty("registry").GetDictOrEmpty("instances").GetDictOrEmpty(id).GetString("holder"), "Fixture must be unfinished work.");
            Assert.IsTrue(s.RequestSaveToSlot("world", "world", "Partial held component work"), ResultSummary(s.LastSaveResult)); GdDict selected = s.SaveLoadService.SelectGeneration("world");
            Assert.IsTrue(s.ApplySelectedGeneration(selected), ResultSummary(s.LastSaveResult));
            for (int tick = 0; tick < 30; tick++) s.StageWorkAction(.1);
            Assert.AreEqual(progress, s.GetComponentWorkState().GetFloat("progress")); Assert.IsTrue(s.GetComponentWorkState().GetBool("resume_required"));
            Assert.IsTrue(V.VariantEquals(saved.Get("registry"), s.CaptureComponentDomain().Get("registry")), "Full Apply must not finish saved held work.");
            var fresh = CreateHeldWork(rig.Storage, selected); RunSession continued = fresh.Session;
            Assert.IsTrue(continued.ApplySelectedGeneration(selected), ResultSummary(continued.LastSaveResult));
            for (int tick = 0; tick < 30; tick++) continued.StageWorkAction(.1);
            Assert.AreEqual(progress, continued.GetComponentWorkState().GetFloat("progress")); Assert.IsTrue(continued.GetComponentWorkState().GetBool("resume_required"));
            Assert.AreEqual(saved.GetDictOrEmpty("component_work").GetString("command_id"), continued.GetComponentWorkState().GetString("command_id"));
            continued.VitalsState.Stamina = continued.VitalsState.MaxStamina; Assert.IsTrue(continued.BeginWorkHold());
            for (int tick = 0; tick < 140; tick++) continued.StageWorkAction(.1);
            GdDict completed = continued.CaptureComponentDomain();
            Assert.AreEqual("player:player_local", completed.GetDictOrEmpty("registry").GetDictOrEmpty("instances").GetDictOrEmpty(id).GetString("holder"));
            Assert.AreEqual(saved.GetDictOrEmpty("receipts").Count + 1, completed.GetDictOrEmpty("receipts").Count);
            for (int tick = 0; tick < 140; tick++) continued.StageWorkAction(.1);
            Assert.AreEqual(completed.GetDictOrEmpty("receipts").Count, continued.CaptureComponentDomain().GetDictOrEmpty("receipts").Count, "Resumed chosen work publishes exactly once.");
            Assert.AreEqual(row.Get("condition"), completed.GetDictOrEmpty("registry").GetDictOrEmpty("instances").GetDictOrEmpty(id).Get("condition"));
        }
    }
}
