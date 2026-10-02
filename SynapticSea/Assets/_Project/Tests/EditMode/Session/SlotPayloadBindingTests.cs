using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
#if !UNITY_5_3_OR_NEWER
    [NonParallelizable]
#endif
    public class SlotPayloadBindingTests
    {
        IEngineInfo _engine;
        IResourceReader _resources;
        [SetUp] public void Setup() { _engine = CoreServices.Engine; _resources = CoreServices.Resources; CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion); CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot); CatalogRegistry.Clear(); }
        [TearDown] public void Cleanup() { CatalogRegistry.Clear(); CoreServices.Engine = _engine; CoreServices.Resources = _resources; }

        internal static object Call(object target, string name, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).SingleOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length);
            Assert.IsNotNull(method, "Complete live save boundary must implement " + target.GetType().Name + "." + name);
            return method.Invoke(target, args);
        }
        static GdDict Select(RunSession s, string slot) => (GdDict)Call(s.SaveLoadService, "SelectGeneration", slot);
        static bool Save(RunSession s, string slot, string kind) => (bool)Call(s, "RequestSaveToSlot", slot, kind, "Live bundle witness");
        static SessionHarness.Rig IntegrationRig()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out var rig);
            FieldInfo enabled = typeof(RunSessionDeps).GetField("EnableComponentIntegration");
            Assert.IsNotNull(enabled, "Incomplete component/save integration must be an explicit diagnostic opt-in");
            enabled.SetValue(deps, true); rig.Session = RunSession.Create(deps); return rig;
        }
        static GdDict Parse(GdDict selection, string role) => (GdDict)GdJson.ParseString(selection.GetDictOrEmpty("payloads").GetString(role + "_text"));
        internal static Dictionary<string, string> Bytes(IStorage storage, string dir = "user://saves")
        {
            var result = new Dictionary<string, string>();
            foreach (string name in storage.ListFiles(dir)) result[dir + "/" + name] = storage.ReadText(dir + "/" + name);
            foreach (string name in storage.ListDirectories(dir)) foreach (var pair in Bytes(storage, dir + "/" + name)) result[pair.Key] = pair.Value;
            return result;
        }
        internal static void SameBytes(Dictionary<string, string> before, IStorage storage)
        {
            var after = Bytes(storage); Assert.AreEqual(before.Count, after.Count, "A refused save/read cannot create, remove or rename owned bytes");
            foreach (var pair in before) Assert.IsTrue(after.ContainsKey(pair.Key) && pair.Value == after[pair.Key], "Original bytes changed at " + pair.Key);
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
        public void EveryGameplayFamilyCapturesCompleteActualWorldAndOwner(string slot, string kind)
        {
            var rig = IntegrationRig(); var s = rig.Session;
            rig.Scene.PlayerPosition = new Vec3(1.25, 0.55, -2.5);
            s.HomeShip.GetInventory().AddItem("scrap_metal", 3); s.HomeShip.LootedContainerIds.Add("live-capture-container");
            Assert.IsTrue(Save(s, slot, kind), "Actual session family must publish a complete bundle");
            GdDict selected = Select(s, slot); Assert.IsTrue(selected.GetBool("ok"), GdJson.Stringify(selected));
            GdDict run = Parse(selected, "run"), world = Parse(selected, "world"), payloads = selected.GetDictOrEmpty("payloads");
            Assert.AreEqual("gate2-current-run-7", run.GetString("slice_version")); Assert.AreEqual("world-5", world.GetString("slice_version"));
            Assert.AreEqual(slot, run.GetString("slot_id")); Assert.AreEqual(kind, run.GetString("slot_kind"));
            Assert.AreEqual(run.GetString("run_id"), world.GetString("run_id"));
            Assert.IsTrue(V.VariantEquals(run.Get("component_domain"), world.Get("component_domain")), "One capture owns both duplicate summaries");
            Assert.IsFalse(world.GetDictOrEmpty("component_domain").IsEmpty, "The live owner must be persisted");
            Assert.IsTrue(world.GetArrayOrEmpty("home_looted_containers").Contains("live-capture-container"));
            Assert.IsTrue(V.VariantEquals(run.Get("player_position"), world.Get("player_position_in_ship")));
            var refs = payloads.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references"); Assert.IsTrue(refs.Has("ship_start")); Assert.IsTrue(refs.Has("lifeboat"));
            Assert.IsTrue(refs.GetDictOrEmpty("lifeboat").GetBool("present"), "The actual fixed lifeboat is a complete generation member");
            Assert.GreaterOrEqual(payloads.GetArrayOrEmpty("artifacts").Count, 4, "Home and actual boat artifact bytes must both be bound");
        }

        [Test]
        public void ManualSelectionSurvivesNewPointerAndRestoresCapturedWorld()
        {
            var rig = IntegrationRig(); var s = rig.Session;
            rig.Scene.PlayerPosition = new Vec3(1, .55, 2); s.HomeShip.LootedContainerIds.Add("before");
            Assert.IsTrue(Save(s, "slot_01", "manual")); GdDict old = Select(s, "slot_01");
            s.HomeShip.LootedContainerIds.Add("after"); rig.Scene.PlayerPosition = new Vec3(4, .55, 8);
            Assert.IsTrue(Save(s, "slot_01", "manual")); GdDict latest = Select(s, "slot_01");
            Assert.AreNotEqual(old.GetString("generation_id"), latest.GetString("generation_id"));
            rig.Storage.Delete(SaveLoadService.INDEX_PATH); rig.Storage.Delete("user://saves/.generations/index.json");
            Assert.IsTrue((bool)Call(s, "ApplySelectedGeneration", old), "Retained exact handle must not reread latest slot");
            Assert.IsTrue(s.HomeShip.LootedContainerIds.Contains("before")); Assert.IsFalse(s.HomeShip.LootedContainerIds.Contains("after"));
            Assert.AreEqual(new Vec3(1, .55, 2), rig.Scene.PlayerPosition);
            Assert.IsTrue(Save(s, "slot_01", "manual"), "Capture lineage advances after explicit rewind");
        }

        [TestCase("active")][TestCase("complete")][TestCase("queue")][TestCase("field")]
        public void UnverifiedCurrentCraftRefusesBeforeAnyPublication(string shape)
        {
            var rig = IntegrationRig(); var s = rig.Session; Assert.IsTrue(Save(s, "world", "world"), GdJson.Stringify(s.LastSaveResult));
            var before = Bytes(rig.Storage); GdDict craft = s.CraftingState.GetSummary();
            if (shape == "queue") craft["station_summaries"] = new GdDict { { "workbench", new GdDict { { "queue", GdArray.Of("unpaid") } } } };
            else if (shape == "field") craft["field_crafting"] = new GdDict { { "active_craft", new GdDict { { "recipe_id", "unverified" } } } };
            else craft["active_craft"] = new GdDict { { "recipe_id", "unverified" }, { "status", shape }, { "progress", 30.0 } };
            if (shape == "field") s.FieldCraftingState.ApplySummary(new GdDict { { "field_crafting", craft.GetDictOrEmpty("field_crafting") } });
            else s.CraftingState.ApplySummary(craft);
            Assert.IsFalse(Save(s, "world", "world"), "Shape cannot prove paid inputs");
            PropertyInfo result = typeof(RunSession).GetProperty("LastSaveResult"); Assert.IsNotNull(result, "Save refusal must expose its reason");
            Assert.AreEqual("craft_payment_unverified", ((GdDict)result.GetValue(s)).GetString("reason")); SameBytes(before, rig.Storage);
        }

        [Test]
        public void MutatedSelectionRefusesBeforeLiveReload()
        {
            var rig = IntegrationRig(); var s = rig.Session; Assert.IsTrue(Save(s, "world", "world"), GdJson.Stringify(s.LastSaveResult));
            GdDict selection = Select(s, "world"); selection.GetDictOrEmpty("payloads")["world_text"] = "{}";
            int spawns = rig.Scene.SpawnCount, despawns = rig.Scene.DespawnCount; Vec3 pose = rig.Scene.PlayerPosition;
            Assert.IsFalse((bool)Call(s, "ApplySelectedGeneration", selection));
            Assert.AreEqual(spawns, rig.Scene.SpawnCount); Assert.AreEqual(despawns, rig.Scene.DespawnCount); Assert.AreEqual(pose, rig.Scene.PlayerPosition);
        }

        [Test]
        public void ExactGenerationReadIsReadOnlyAfterPointerChanges()
        {
            var storage = new MemoryStorage(); GdDict first = GenerationFixtures.Request(); GenerationFixtures.Commit(storage, first);
            GdDict selected = (GdDict)Call(GenerationFixtures.Coordinator(storage), "ReadSelected", "slot_01");
            GdDict child = GenerationFixtures.Child(storage, first); GenerationFixtures.Commit(storage, child); var before = Bytes(storage);
            GdDict exact = (GdDict)Call(GenerationFixtures.Coordinator(storage), "ReadGeneration", GenerationFixtures.Run, "slot_01", selected.GetString("generation_id"), selected.GetString("manifest_sha256"));
            Assert.IsTrue(exact.GetBool("ok"), GdJson.Stringify(exact)); Assert.AreEqual(first.GetString("world_text"), exact.GetDictOrEmpty("payloads").GetString("world_text")); SameBytes(before, storage);
        }

        [Test]
        public void OrdinaryActiveCraftSaveAndContinueRemainLegacyAndUsable()
        {
            var rig = SessionHarness.CreateGolden(); var s = rig.Session;
            GdDict craft = s.CraftingState.GetSummary(); craft["active_craft"] = new GdDict { { "recipe_id", "ordinary_active" }, { "progress", 3.0 } }; s.CraftingState.ApplySummary(craft);
            Assert.IsTrue(s.RequestSave(), "Incomplete diagnostic integration must not strand ordinary active crafting");
            GdDict world = (GdDict)GdJson.ParseString(rig.Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE));
            Assert.AreEqual("world-4", world.GetString("slice_version")); Assert.AreEqual("gate2-current-run-6", world.GetDictOrEmpty("home_ship").GetString("slice_version"));
            Assert.IsFalse(world.Has("component_domain")); Assert.IsTrue(s.RequestLoad(), "Ordinary Continue remains available");
            Assert.AreEqual("ordinary_active", s.CraftingState.GetSummary().GetDictOrEmpty("active_craft").GetString("recipe_id"));
        }
    }
}
