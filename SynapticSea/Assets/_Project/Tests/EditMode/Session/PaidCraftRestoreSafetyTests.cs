// ARTIFACT ONLY: ROOT reviews/promotes after runtime Restore8 GREEN. Not compiled or executed.
using System;
using System.Collections.Generic;
using System.Linq;
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
    public class PaidCraftRestoreSafetyTests : InfraDataTestBase
    {
        const string Recipe = "weld_plating", Kind = "workbench", StartCommand = "safety-completed-start";
        readonly List<RunSession> _sessions = new List<RunSession>();
        IEngineInfo _engine;
        [SetUp] public void SetEngine()
        { _engine = CoreServices.Engine; CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion); }
        [TearDown] public void Cleanup()
        {
            try { foreach (RunSession s in _sessions) { s.ComponentStageHook = null; s.Dispose(); } _sessions.Clear(); }
            finally { CoreServices.Engine = _engine; }
        }
        static string Detail(GdDict result) => result.GetString("reason") + ":" + result.GetString("detail");
        static GdDict Paid(GdDict owner) => owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting");
        static GdDict Job(GdDict owner, string id) => Paid(owner).GetDictOrEmpty("jobs").GetDictOrEmpty(id);
        static void Exact(object expected, object actual, string label)
        {
            GdDict expectedTyped = ComponentDomainCodec.Encode(new GdDict { { "value", expected } });
            GdDict actualTyped = ComponentDomainCodec.Encode(new GdDict { { "value", actual } });
            if (!V.VariantEquals(expectedTyped, actualTyped))
            {
                TestContext.WriteLine("PAID_SAFETY_LABEL=" + label);
                TestContext.WriteLine("PAID_SAFETY_EXPECTED_TYPED=" + GdJson.Stringify(expectedTyped));
                TestContext.WriteLine("PAID_SAFETY_ACTUAL_TYPED=" + GdJson.Stringify(actualTyped));
            }
            Assert.IsTrue(V.VariantEquals(
                ComponentDomainCodec.Encode(new GdDict { { "value", expected } }),
                ComponentDomainCodec.Encode(new GdDict { { "value", actual } })), label);
        }

        // The ordinary wrapper deliberately does NOT implement the optional capability. Both wrappers
        // delegate existing host behavior; neither fabricates documents, loaders or generation authority.
        class ObservedHost : IShipSceneHost
        {
            protected readonly FakeShipHost Inner;
            public int Loads, Builds, Boats;
            public ObservedHost(FakeShipHost inner) { Inner = inner; }
            public IShipLoaderView LoadHomeShip(string layout, string kit, string slice, out string reason)
            { Loads++; return Inner.LoadHomeShip(layout, kit, slice, out reason); }
            public IShipLoaderView BuildShipScene(ShipDocuments documents)
            { Builds++; return Inner.BuildShipScene(documents); }
            public IShipSceneRoot BuildLifeboatScene(LifeBoatBuilder.BuildResult boat)
            { Boats++; return Inner.BuildLifeboatScene(boat); }
            public void AttachShipRoot(IShipSceneRoot root) => Inner.AttachShipRoot(root);
            public void FreeShipRoot(IShipSceneRoot root) => Inner.FreeShipRoot(root);
            public void SetShipRootPosition(IShipSceneRoot root, Vec3 position) => Inner.SetShipRootPosition(root, position);
            public void SetShipRootGlobalTransform(IShipSceneRoot root, Xform3 transform) => Inner.SetShipRootGlobalTransform(root, transform);
            public bool IsParentedToSession(IShipSceneRoot root) => Inner.IsParentedToSession(root);
        }
        sealed class PreparedObservedHost : ObservedHost, IPreparedHomeSceneHost
        {
            public int Prepares;
            public IShipLoaderView SelectedHome;
            public PreparedObservedHost(FakeShipHost inner) : base(inner) { }
            public IPreparedHome PrepareHome(ShipDocuments documents, IShipLoaderView expectedCurrentHome, out string reason)
            {
                Prepares++;
                IPreparedHome prepared = Inner.PrepareHome(documents, expectedCurrentHome, out reason);
                SelectedHome = prepared?.PreparedLoader;
                return prepared;
            }
        }
        sealed class Case
        {
            public SessionHarness.Rig Rig;
            public ObservedHost Host;
            public string Completed, Running;
        }
        Case Boot(bool prepared, MemoryStorage storage = null, double clockOffset = 0)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.EnablePaidCrafting = true; deps.EnableComponentIntegration = false;
            if (storage != null) { rig.Storage = storage; deps.Storage = storage; }
            rig.Clock.Advance(clockOffset); // Actual normal generated run-ID prefix, never an assigned identity.
            ObservedHost host = prepared ? new PreparedObservedHost(rig.Host) : new ObservedHost(rig.Host);
            deps.ShipHost = host;
            rig.Session = RunSession.Create(deps); _sessions.Add(rig.Session);
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, "BOOT_DEPENDENCY: " + s.LastFailureReason);
            Assert.IsTrue(s.PaidCraftingEnabled); Assert.IsFalse(s.ComponentIntegrationEnabled);
            Assert.AreSame(s.HomeShip, s.CurrentShip); Assert.IsFalse(s.AwayFromStart);
            s.ThreatManager.Threats.Clear(); s.InventoryState.Items.Clear();
            s.PlayerProgression.Skills["fabrication"] = 4L;
            s.PlayerProgression.SkillXpFractional["fabrication"] = 0.0000005;
            s.VitalsState.Stamina = s.VitalsState.MaxStamina;
            s.HomeShip.LootedContainerIds.Add("safety-saved-world");
            rig.Scene.PlayerPosition = new Vec3(1, .5, 2);
            Assert.IsTrue(s.CraftingStations.Any(st => st.IsValid && st.StationKind == Kind && ReferenceEquals(st.Parent, s.HomeShip.SceneRoot)));
            Assert.IsNotNull(s.CraftingState.GetStation(Kind)); s.CraftingState.GetStation(Kind).SetPower(true);
            var c = new Case { Rig = rig, Host = host };
            c.Completed = Start(s, StartCommand); s.AdvanceCrafting(1000);
            Assert.AreEqual("completed_delivered", Job(Valid(s), c.Completed).GetString("status"));
            Assert.AreEqual(1L, s.InventoryState.RemoveItem("plating", 1L));
            // Exact legal live fractional participant witness; the normal next command captures it.
            s.PlayerProgression.SkillXpFractional["fabrication"] = 0.9999995;
            c.Running = Start(s, "safety-running-start");
            s.AdvanceCrafting(Job(Valid(s), c.Running).GetFloat("required_seconds") / 4.0);
            GdDict running = Job(Valid(s), c.Running);
            Assert.AreEqual("running", running.GetString("status")); Assert.IsFalse(running.GetBool("resume_required"));
            Assert.Greater(running.GetFloat("progress_seconds"), 0);
            return c;
        }
        static string Start(RunSession s, string command)
        {
            GdDict recipe = s.CraftingState.GetRecipe(Recipe); Assert.IsFalse(recipe.IsEmpty);
            foreach (var pair in recipe.GetDictOrEmpty("ingredients"))
                Assert.AreEqual(V.I64(pair.Value), s.InventoryState.AddItem(V.Str(pair.Key), V.I64(pair.Value)));
            GdDict result = s.RequestPaidCraft(Kind, Recipe, command);
            Assert.IsTrue(result.GetBool("ok"), Detail(result)); Assert.IsTrue(result.GetBool("committed"));
            foreach (var pair in recipe.GetDictOrEmpty("ingredients")) Assert.AreEqual(0L, s.InventoryState.GetQuantity(V.Str(pair.Key)));
            return result.GetString("job_id");
        }
        static GdDict Valid(RunSession s)
        {
            GdDict owner = s.CapturePaidCraftingDomain();
            Assert.IsTrue(DomainBundle.TryCreate(owner, out _, out string reason), "CORE_DEPENDENCY: " + reason);
            Assert.IsTrue(s.ValidatePaidCraftingRestore(owner, out reason), "CURRENT_CONTEXT_DEPENDENCY: " + reason);
            Assert.AreEqual("craft_only", owner.GetString("domain_mode"));
            Assert.AreEqual(s.RunId, Paid(owner).GetString("run_id"));
            return owner;
        }
        static GdDict Read(SaveLoadService service, GdDict selected) => service.ReadGeneration(selected.GetString("run_id"),
            selected.GetString("slot_id"), selected.GetString("generation_id"), selected.GetString("manifest_sha256"));
        static GdDict SaveControl(Case c, string slot)
        {
            RunSession s = c.Rig.Session; GdDict owner = Valid(s);
            Assert.IsTrue(s.RequestPaidCraft(Kind, Recipe, StartCommand).GetBool("ok"), "Existing start receipt has a successful unguarded replay control");
            Assert.IsTrue(s.RetryPaidCraft(c.Completed, "safety-positive-replay").GetBool("ok"), "Terminal retry has a successful unguarded control");
            Exact(owner, Valid(s), "Positive replay controls do not change the owner");
            Assert.IsTrue(s.RequestSaveToSlot(slot, slot == "world" ? "world" : "manual", "Restore safety actual control"),
                "FULL_SAVE_DEPENDENCY: " + Detail(s.LastSaveResult));
            GdDict selected = s.SaveLoadService.SelectGeneration(slot);
            Assert.IsTrue(selected.GetBool("ok"), "SELECT_DEPENDENCY: " + Detail(selected));
            Assert.AreEqual(PaidSnapshotCodec.OrdinaryMode, selected.GetString("save_mode"));
            GdDict payload = selected.GetDictOrEmpty("payloads"), exact = Read(s.SaveLoadService, selected);
            Assert.IsTrue(exact.GetBool("ok"), Detail(exact)); Exact(payload, exact.Get("payloads"), "Exact actual selected payload");
            GdDict admitted = s.SaveLoadService.ComponentCoordinator().ValidateSuppliedPayload(payload, s.RunId, slot);
            Assert.IsTrue(admitted.GetBool("ok"), "PAYLOAD_ADMISSION_DEPENDENCY: " + Detail(admitted));
            Assert.Greater(payload.GetArrayOrEmpty("artifacts").Count, 0);
            var run = GdJson.Parse(payload.GetString("run_text"), true) as GdDict;
            var world = GdJson.Parse(payload.GetString("world_text"), true) as GdDict;
            Assert.IsNotNull(run); Assert.IsNotNull(world);
            Assert.IsTrue(world.GetArrayOrEmpty("home_looted_containers").Contains("safety-saved-world"));
            foreach (GdDict mirror in new[] { run, world.GetDictOrEmpty("home_ship") })
            {
                Assert.IsTrue(ComponentDomainCodec.TryDecode(mirror.GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft")
                    .GetDictOrEmpty("domain"), out GdDict decoded, out string reason), reason);
                Assert.IsTrue(DomainBundle.TryCreate(decoded, out _, out reason), reason);
                Assert.IsTrue(s.ValidatePaidCraftingRestore(decoded, out reason), "Saved source-session positive: " + reason);
                Exact(owner, decoded, "Exact owner/payment/history in actual full payload");
            }
            return selected.DeepCopy();
        }
        static GdDict Raw(Case c)
        {
            RunSession s = c.Rig.Session;
            return new GdDict {
                { "run", s.RunId }, { "service_run", s.SaveLoadService.GetActiveRunId() },
                { "inventory", s.InventoryState.GetSummary() }, { "progression", s.PlayerProgression.GetSummary() },
                { "multipliers", s.PlayerProgression.XpMultipliers.DeepCopy() }, { "training", s.TrainingEventBus.ToDict() },
                { "craft", s.CraftingState.GetSummary() }, { "field", s.FieldCraftingState.GetSummary() },
                { "knowledge", s.RecipeKnowledge.GetSummary() }, { "spoilage", s.SpoilageState.GetSummary() },
                { "vitals", s.VitalsState.GetSummary() }, { "equipment", s.EquipmentState.GetSummary() },
                { "home_cache", s.HomeShip.GetSummary() }, { "boat_cache", s.LifeboatShip.GetSummary() },
                { "looted", s.HomeShip.LootedContainerIds.DeepCopy() }, { "position", c.Rig.Scene.PlayerPosition },
                { "occupancy", s.CurrentOccupancy.ShipId }, { "world_time", s.WorldTime }, { "play_time", s.RunPlayTimeSeconds },
                { "held", s.IsWorkInteractHeld }
            };
        }
        sealed class Before
        {
            public GdDict Owner, Raw, World;
            public object Home, Crafting, Inventory, Progression, LastSaved;
            public IShipLoaderView Loader;
            public IShipSceneRoot Root;
            public CraftingStation[] Wrappers;
            public object[] Models;
            public Xform3 Transform;
            public bool Inside;
            public int Loads, Builds, Boats, Spawns, Despawns;
            public IShipSceneRoot[] Attached, Freed;
        }
        static Before Capture(Case c)
        {
            RunSession s = c.Rig.Session;
            var world = WorldSnapshotAssembler.Build(s).ToDict();
            var owner = Valid(s);
            return new Before { Owner = owner.DeepCopy(), Raw = Raw(c), World = world, Home = s.HomeShip,
                Loader = s.Loader, Root = s.HomeShip.SceneRoot, Crafting = s.CraftingState, Inventory = s.InventoryState,
                Progression = s.PlayerProgression, LastSaved = s.LastSavedSnapshot, Wrappers = s.CraftingStations.ToArray(),
                Models = s.CraftingStations.Select(st => (object)s.CraftingState.GetStation(st.StationKind)).ToArray(),
                Transform = s.HomeShip.SceneRoot.GlobalTransform, Inside = s.HomeShip.SceneRoot.IsInsideTree,
                Loads = c.Host.Loads, Builds = c.Host.Builds, Boats = c.Host.Boats,
                Spawns = c.Rig.Scene.SpawnCount, Despawns = c.Rig.Scene.DespawnCount,
                Attached = c.Rig.Host.Attached.ToArray(), Freed = c.Rig.Host.Freed.ToArray() };
        }
        static void ChangedLiveWorld(Case c)
        {
            RunSession s = c.Rig.Session;
            s.HomeShip.LootedContainerIds.Remove("safety-saved-world"); s.HomeShip.LootedContainerIds.Add("safety-unsaved-world");
            c.Rig.Scene.PlayerPosition = s.HomeShip.SceneRoot.GlobalTransform * new Vec3(5, 1.25, 7);
            s.AdvanceCrafting(.125); s.BeginWorkHold(); Assert.IsTrue(s.IsWorkInteractHeld);
            Assert.IsFalse(Job(Valid(s), c.Running).GetBool("resume_required"));
        }
        static void AssertRollback(Case c, Before before, bool early)
        {
            RunSession s = c.Rig.Session;
            // Observe raw before any public owner/world capture can synchronize its projections.
            Exact(before.Raw, Raw(c), "Exact raw/world/consent/held before-image after refusal");
            Exact(before.Owner, Valid(s), "Exact observable canonical owner/revision/receipts/history restored");
            Exact(before.World, WorldSnapshotAssembler.Build(s).ToDict(), "Complete original world restored");
            Assert.AreSame(before.Loader, s.Loader); Assert.AreSame(before.Root, s.HomeShip.SceneRoot);
            Assert.IsTrue(before.Root.IsValid); Assert.AreEqual(before.Inside, before.Root.IsInsideTree);
            Assert.AreEqual(before.Transform, before.Root.GlobalTransform); Assert.IsTrue(c.Host.IsParentedToSession(before.Root));
            Assert.IsFalse(c.Rig.Host.Freed.Contains(before.Root)); Assert.AreEqual(before.Loads, c.Host.Loads, "No second home load");
            Assert.AreSame(before.LastSaved, s.LastSavedSnapshot);
            Assert.IsTrue(s.CraftingStations.Any(st => st.IsValid && st.StationKind == Kind && ReferenceEquals(st.Parent, before.Root) &&
                ReferenceEquals(st.CraftingState, s.CraftingState)), "Rebuilt real original-home evidence");
            if (!early) return; // Approved late lifecycle rebuilds models; only old home is retained.
            Assert.AreSame(before.Home, s.HomeShip); Assert.AreSame(before.Crafting, s.CraftingState);
            Assert.AreSame(before.Inventory, s.InventoryState); Assert.AreSame(before.Progression, s.PlayerProgression);
            CollectionAssert.AreEqual(before.Wrappers, s.CraftingStations);
            for (int i = 0; i < before.Wrappers.Length; i++)
            { Assert.IsTrue(before.Wrappers[i].IsValid); Assert.AreSame(before.Root, before.Wrappers[i].Parent);
                Assert.AreSame(before.Models[i], s.CraftingState.GetStation(before.Wrappers[i].StationKind)); }
            Assert.AreEqual(before.Builds, c.Host.Builds); Assert.AreEqual(before.Boats, c.Host.Boats);
            Assert.AreEqual(before.Spawns, c.Rig.Scene.SpawnCount); Assert.AreEqual(before.Despawns, c.Rig.Scene.DespawnCount);
            CollectionAssert.AreEqual(before.Attached, c.Rig.Host.Attached); CollectionAssert.AreEqual(before.Freed, c.Rig.Host.Freed);
        }
        static void Guards(Case c, GdDict selected)
        {
            RunSession s = c.Rig.Session; GdDict raw = Raw(c);
            var bytes = SlotPayloadBindingTests.Bytes(c.Rig.Storage);
            Assert.IsTrue(s.DomainPublicationInProgress);
            Assert.IsFalse(s.RequestPaidCraft(Kind, Recipe, StartCommand).GetBool("ok"), "Existing command replay cannot escape guard");
            Assert.IsFalse(s.RetryPaidCraft(c.Completed, "safety-terminal-replay").GetBool("ok"));
            Assert.IsFalse(s.RequestPaidCraft(Kind, Recipe, "safety-reentrant-new").GetBool("ok"));
            Assert.AreEqual("restore_in_progress", s.CapturePaidCraftingDomain().GetString("reason"));
            Assert.AreEqual("restore_in_progress", s.CaptureComponentDomain().GetString("reason"));
            Assert.IsTrue(s.ListPaidCraftJobs().IsEmpty);
            Assert.IsFalse(s.RequestSaveToSlot("slot_06", "manual", "Must remain excluded"));
            Assert.IsFalse(s.RequestLoad()); Assert.IsFalse(s.ApplySelectedGeneration(selected));
            int stages = 0; Action<string, SessionLocation> observe = (id, location) => stages++;
            s.StageRan += observe;
            try { TickContext frame = TickContext.Frame(10, c.Rig.Scene.PlayerPosition, interactHeld: false); s.Tick(in frame); }
            finally { s.StageRan -= observe; }
            Assert.AreEqual(0, stages); s.AdvanceCrafting(1000);
            Assert.IsFalse(s.BeginWorkHold()); s.EndWorkHold();
            Assert.IsNull(s.EmitTrainingEvent("craft_complete", "safety-reentry"));
            Exact(raw, Raw(c), "Reentry cannot mutate installed participants, clocks, raw world or held input");
            SlotPayloadBindingTests.SameBytes(bytes, c.Rig.Storage);
        }
        static void AssertLateCleanup(Case c, Before before, int hookBuilds, int hookBoats)
        {
            AssertRollback(c, before, false);
            Assert.AreEqual(hookBuilds, c.Host.Builds, "Rollback consumes prestaged roots, no new derelict build");
            Assert.AreEqual(hookBoats, c.Host.Boats, "Rollback consumes prestaged roots, no new boat build");
            var host = (PreparedObservedHost)c.Host;
            Assert.AreEqual(1, host.Prepares); Assert.IsNotNull(host.SelectedHome);
            Assert.IsFalse(host.SelectedHome.IsValid); Assert.IsFalse(c.Rig.Host.Attached.Contains(host.SelectedHome));
            Assert.AreEqual(1, c.Rig.Host.Freed.Count(root => ReferenceEquals(root, host.SelectedHome)), "Selected home freed exactly once");
        }

        [Test]
        public void UnsupportedPreparedHome_RefusesBeforeAnyOldWorldMutation()
        {
            Case c = Boot(false); RunSession s = c.Rig.Session;
            GdDict selected = SaveControl(c, "world"); ChangedLiveWorld(c); Before before = Capture(c);
            var bytes = SlotPayloadBindingTests.Bytes(c.Rig.Storage); GdDict selection = selected.DeepCopy();
            Assert.IsFalse(s.ApplySelectedGeneration(selected));
            Assert.AreEqual("prepared_home_unsupported", s.LastSaveResult.GetString("reason"));
            AssertRollback(c, before, true); Exact(selection, selected, "Caller selection retained");
            Exact(selected, s.SaveLoadService.SelectGeneration("world"), "Prior save selection conserved");
            SlotPayloadBindingTests.SameBytes(bytes, c.Rig.Storage);
            Assert.IsTrue(s.PlayableStarted); Assert.IsFalse(s.SliceComplete); Assert.IsFalse(s.DomainPublicationInProgress);
        }

        [Test]
        public void FinalParticipantReentryAndRemovedSelectedWorkbench_RollsBackExactRunningState()
        {
            Case c = Boot(true); RunSession s = c.Rig.Session;
            GdDict selected = SaveControl(c, "world"); ChangedLiveWorld(c); Before before = Capture(c);
            var bytes = SlotPayloadBindingTests.Bytes(c.Rig.Storage); GdDict selection = selected.DeepCopy();
            int reached = 0, builds = -1, boats = -1; bool guardsPassed = false; Exception hookError = null;
            s.ComponentStageHook = stage => {
                if (stage != "live_placement" || reached != 0) return;
                reached++; builds = c.Host.Builds; boats = c.Host.Boats;
                try {
                    Assert.AreNotSame(before.Root, s.HomeShip.SceneRoot, "Actual selected home installed before final hook");
                    Guards(c, selected); guardsPassed = true;
                    CraftingStation station = s.CraftingStations.Single(st => st.IsValid && st.StationKind == Kind && ReferenceEquals(st.Parent, s.HomeShip.SceneRoot));
                    station.Free(); Assert.IsFalse(station.IsValid); // Real exposed selected evidence removed, not a forged owner.
                } catch (Exception error) { hookError = error; throw; }
            };
            bool loaded; try { loaded = s.ApplySelectedGeneration(selected); } finally { s.ComponentStageHook = null; }
            Assert.AreEqual(1, reached, "FINAL_HOOK_DEPENDENCY: actual final participant hook must be reached");
            Assert.IsNull(hookError, hookError?.ToString()); Assert.IsTrue(guardsPassed);
            Assert.IsFalse(loaded); Assert.AreEqual("stale_restore_context", s.LastSaveResult.GetString("reason"), Detail(s.LastSaveResult));
            AssertLateCleanup(c, before, builds, boats); Exact(selection, selected, "Caller selection retained");
            Exact(selected, s.SaveLoadService.SelectGeneration("world"), "Prior selection survives late refusal");
            SlotPayloadBindingTests.SameBytes(bytes, c.Rig.Storage);
            Assert.IsTrue(s.PlayableStarted); Assert.IsFalse(s.SliceComplete); Assert.IsFalse(s.DomainPublicationInProgress);
        }

        [Test]
        public void SameRunTerminalAtFinalParticipant_RollsBackWithoutRevivingAuthority()
        {
            Case c = Boot(true); RunSession s = c.Rig.Session; GdDict selected = SaveControl(c, "world");
            ChangedLiveWorld(c); Before before = Capture(c); GdDict selection = selected.DeepCopy();
            int reached = 0, builds = -1, boats = -1; Exception hookError = null;
            Dictionary<string, string> terminalBytes = null;
            s.ComponentStageHook = stage => {
                if (stage != "live_placement" || reached != 0) return;
                reached++; builds = c.Host.Builds; boats = c.Host.Boats;
                try {
                    Assert.AreEqual(selected.GetString("run_id"), s.RunId);
                    Assert.AreNotSame(before.Root, s.HomeShip.SceneRoot);
                    s.SaveLoadService.FreezeRun(s.RunId, "death", "Actual same-run hook terminal", 10, 1);
                    Assert.IsTrue(s.SaveLoadService.LastGenerationResult.GetBool("ok"), "Actual terminal publication prerequisite");
                    Assert.AreEqual("run_terminal", Read(s.SaveLoadService, selected).GetString("reason"));
                    terminalBytes = SlotPayloadBindingTests.Bytes(c.Rig.Storage);
                } catch (Exception error) { hookError = error; throw; }
            };
            bool loaded; try { loaded = s.ApplySelectedGeneration(selected); } finally { s.ComponentStageHook = null; }
            Assert.AreEqual(1, reached, "FINAL_HOOK_DEPENDENCY"); Assert.IsNull(hookError, hookError?.ToString());
            Assert.IsFalse(loaded); Assert.AreEqual("run_terminal", s.LastSaveResult.GetString("reason"), Detail(s.LastSaveResult));
            AssertLateCleanup(c, before, builds, boats); Exact(selection, selected, "Caller terminal selection is not rewritten");
            Assert.IsTrue(s.ComponentTerminalPending); Assert.IsTrue(s.SliceComplete || !s.PlayableStarted, "Terminal old run cannot become playable");
            Assert.IsTrue(s.LastSaveResult.GetArrayOrEmpty("observed_terminal_runs").Contains(before.Raw.GetString("run")));
            Assert.IsFalse(Read(s.SaveLoadService, selected).GetBool("ok"));
            var fresh = new SaveLoadService(c.Rig.Storage, c.Rig.Clock, false, true);
            Assert.AreEqual("run_terminal", Read(fresh, selected).GetString("reason"), "Durable terminal authority survives a fresh service");
            GdDict stopped = Raw(c); TickContext frame = TickContext.Frame(10, c.Rig.Scene.PlayerPosition); s.Tick(in frame); s.AdvanceCrafting(1000);
            Assert.IsFalse(s.RequestPaidCraft(Kind, Recipe, "terminal-new-command").GetBool("ok"));
            Assert.IsFalse(s.RequestSaveToSlot("slot_06", "manual", "Terminal cannot save")); Assert.IsFalse(s.RequestLoad());
            Exact(stopped, Raw(c), "Terminal exclusion after rollback");
            Assert.IsNotNull(terminalBytes); SlotPayloadBindingTests.SameBytes(terminalBytes, c.Rig.Storage);
        }

        [Test]
        public void SelectedForeignRunTerminal_RestoresIndependentlyLiveOriginalIdentity()
        {
            Case c = Boot(true); RunSession s = c.Rig.Session; GdDict original = SaveControl(c, "world");
            Case foreign = Boot(true, c.Rig.Storage, 10); GdDict selected = SaveControl(foreign, "slot_01");
            Assert.AreNotEqual(s.RunId, foreign.Rig.Session.RunId, "UNIQUE_NORMAL_BOOT_DEPENDENCY");
            Assert.IsTrue(Read(s.SaveLoadService, selected).GetBool("ok"), "Real foreign admission readable by receiving service");
            Assert.IsTrue(Read(s.SaveLoadService, original).GetBool("ok"));
            ChangedLiveWorld(c); Before before = Capture(c); GdDict selection = selected.DeepCopy();
            int reached = 0, builds = -1, boats = -1; Exception hookError = null;
            Dictionary<string, string> terminalBytes = null;
            s.ComponentStageHook = stage => {
                if (stage != "live_placement" || reached != 0) return;
                reached++; builds = c.Host.Builds; boats = c.Host.Boats;
                try {
                    Assert.AreEqual(selected.GetString("run_id"), s.RunId, "Actual foreign selected context reached");
                    Assert.AreNotEqual(before.Raw.GetString("run"), s.RunId); Assert.AreNotSame(before.Root, s.HomeShip.SceneRoot);
                    s.SaveLoadService.FreezeRun(s.RunId, "death", "Actual selected foreign-run hook terminal", 10, 1);
                    Assert.IsTrue(s.SaveLoadService.LastGenerationResult.GetBool("ok"), "Actual terminal publication prerequisite");
                    Assert.AreEqual("run_terminal", Read(s.SaveLoadService, selected).GetString("reason"));
                    Assert.IsTrue(Read(s.SaveLoadService, original).GetBool("ok"), "Original run independently remains live");
                    terminalBytes = SlotPayloadBindingTests.Bytes(c.Rig.Storage);
                } catch (Exception error) { hookError = error; throw; }
            };
            bool loaded; try { loaded = s.ApplySelectedGeneration(selected); } finally { s.ComponentStageHook = null; }
            Assert.AreEqual(1, reached, "FOREIGN_CONTEXT_HOOK_DEPENDENCY: no fabricated selected context is permitted");
            Assert.IsNull(hookError, hookError?.ToString()); Assert.IsFalse(loaded);
            Assert.AreEqual("run_terminal", s.LastSaveResult.GetString("reason"), Detail(s.LastSaveResult));
            GdDict observed = s.LastSaveResult.DeepCopy(); AssertLateCleanup(c, before, builds, boats);
            Exact(selection, selected, "Foreign selection remains unchanged");
            Assert.AreEqual(before.Raw.GetString("run"), s.RunId); Assert.AreEqual(s.RunId, s.SaveLoadService.GetActiveRunId());
            Assert.IsTrue(observed.GetArrayOrEmpty("observed_terminal_runs").Contains(selected.GetString("run_id")));
            Assert.IsFalse(observed.GetArrayOrEmpty("observed_terminal_runs").Contains(s.RunId), "Selected death cannot relabel original authority");
            var fresh = new SaveLoadService(c.Rig.Storage, c.Rig.Clock, false, true);
            Assert.AreEqual("run_terminal", Read(fresh, selected).GetString("reason"));
            GdDict oldRead = Read(fresh, original); Assert.IsTrue(oldRead.GetBool("ok"), "Original independent durable authority must be live");
            Exact(original.Get("payloads"), oldRead.Get("payloads"), "Original full generation remains intact");
            Assert.IsTrue(s.PlayableStarted); Assert.IsFalse(s.SliceComplete); Assert.IsFalse(s.ComponentTerminalPending); Assert.IsFalse(s.DomainPublicationInProgress);
            Assert.IsNotNull(terminalBytes); SlotPayloadBindingTests.SameBytes(terminalBytes, c.Rig.Storage);
        }
    }
}
