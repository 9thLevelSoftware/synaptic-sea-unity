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
    public class PaidCraftSaveNumericTests : InfraDataTestBase
    {
        const string PaidRoot = "user://saves/.paid-craft-generations";
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

        SessionHarness.Rig Boot()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.EnablePaidCrafting = true;
            deps.EnableComponentIntegration = false;
            rig.Session = RunSession.Create(deps);
            _sessions.Add(rig.Session);
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, "Boot prerequisite: " + s.LastFailureReason);
            Assert.IsTrue(s.PaidCraftingEnabled);
            Assert.IsFalse(s.ComponentIntegrationEnabled);
            s.ThreatManager.Threats.Clear();
            s.InventoryState.Items.Clear();
            s.PlayerProgression.Skills["fabrication"] = 4L;
            s.VitalsState.Stamina = s.VitalsState.MaxStamina;
            return rig;
        }

        static GdDict Paid(GdDict owner) => owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting");
        static GdDict Job(RunSession s, string id) => Paid(s.CapturePaidCraftingDomain()).GetDictOrEmpty("jobs").GetDictOrEmpty(id);
        static string Detail(GdDict result) => "reason=" + result.GetString("reason") + ", detail=" + result.GetString("detail");

        static void Exact(object expected, object actual, string message)
        {
            GdDict a = ComponentDomainCodec.Encode(new GdDict { { "value", expected } });
            GdDict b = ComponentDomainCodec.Encode(new GdDict { { "value", actual } });
            Assert.IsTrue(V.VariantEquals(a, b), message);
        }

        static void Real(double expected, object actual, string message)
        {
            Assert.IsInstanceOf<double>(actual, message + " must retain the real tag.");
            double observed = (double)actual;
            Assert.IsFalse(double.IsNaN(observed) || double.IsInfinity(observed), message + " must remain finite.");
            if (expected == 0.0) Assert.AreEqual(0.0, observed, message + " uses canonical codec zero.");
            else Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(observed), message);
        }

        static string Start(RunSession s, string recipeId)
        {
            GdDict recipe = s.CraftingState.GetRecipe(recipeId);
            Assert.IsFalse(recipe.IsEmpty, "Actual recipe prerequisite.");
            string kind = recipe.GetString("station_kind");
            Assert.IsTrue(s.CraftingStations.Any(st => st.IsValid && st.StationKind == kind &&
                ReferenceEquals(st.Parent, s.HomeShip.SceneRoot)), "Actual original-home station prerequisite.");
            var model = s.CraftingState.GetStation(kind);
            Assert.IsNotNull(model, "Existing registered station model prerequisite.");
            model.SetPower(true);
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
            {
                Assert.AreEqual(0L, s.InventoryState.GetQuantity(V.Str(ingredient.Key)), "Exactly one new ingredient set.");
                long count = V.I64(ingredient.Value);
                Assert.AreEqual(count, s.InventoryState.AddItem(V.Str(ingredient.Key), count));
            }
            GdDict start = s.RequestPaidCraft(kind, recipeId, "numeric-start-" + recipeId);
            Assert.IsTrue(start.GetBool("ok"), "Real paid start prerequisite: " + Detail(start));
            Assert.IsTrue(start.GetBool("committed"));
            Assert.IsNotEmpty(start.GetString("job_id"));
            Assert.IsNotEmpty(start.GetString("commit_id"));
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
                Assert.AreEqual(0L, s.InventoryState.GetQuantity(V.Str(ingredient.Key)), "One committed debit.");
            GdDict job = Job(s, start.GetString("job_id"));
            Assert.AreEqual("paid", job.GetString("input_state"));
            Assert.AreEqual(start.GetString("commit_id"), job.GetString("payment_commit_id"));
            return start.GetString("job_id");
        }

        static void StartRunning(RunSession s)
        {
            string id = Start(s, "weld_plating");
            GdDict paid = Job(s, id);
            s.AdvanceCrafting(paid.GetFloat("required_seconds") / 4.0);
            GdDict running = Job(s, id);
            Assert.AreEqual("running", running.GetString("status"));
            Assert.Greater(running.GetFloat("progress_seconds"), 0.0);
            Assert.Less(running.GetFloat("progress_seconds"), running.GetFloat("required_seconds"));
            foreach (string field in new[] { "payment_commit_id", "consumed", "quality_score", "required_seconds" })
                Exact(paid.Get(field), running.Get(field), "Real progress retains paid " + field);
            Assert.AreEqual(0L, s.InventoryState.GetQuantity("plating"));
        }

        static GdDict ValidOwnerAndRawMirrors(RunSession s, string participant, string mirror)
        {
            GdDict owner = s.CapturePaidCraftingDomain();
            Assert.AreEqual(3L, owner.GetInt("schema_version"));
            Assert.AreEqual("craft_only", owner.GetString("domain_mode"));
            Assert.IsTrue(DomainBundle.TryCreate(owner, out _, out string reason), "Canonical positive prerequisite: " + reason);
            Assert.IsTrue(s.ValidatePaidCraftingRestore(owner, out reason), "Session positive prerequisite: " + reason);
            Assert.IsTrue(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(owner), out GdDict decoded, out reason), reason);
            Exact(owner, decoded, "Whole typed owner is an independent exact positive control.");
            Assert.IsTrue(s.ValidatePaidCraftingRestore(decoded, out reason), "Decoded positive prerequisite: " + reason);
            RunSnapshot run = RunSnapshotAssembler.Build(s);
            WorldSnapshot world = WorldSnapshotAssembler.Build(s);
            Assert.IsNotNull(run);
            Assert.IsNotNull(world);
            GdDict raw = owner.GetDictOrEmpty("participating_state").GetDictOrEmpty(participant);
            Exact(raw, run.ToDict().Get(mirror), "Actual raw run mirror must be exact before serialization.");
            Exact(raw, world.HomeShip.Get(mirror), "Actual raw world/home mirror must be exact before serialization.");
            return owner;
        }

        // Test observation only. This is not a runtime legacy-fallback policy.
        static string SaveAndObserveActualWorldText(SessionHarness.Rig rig)
        {
            RunSession s = rig.Session;
            Assert.IsTrue(s.RequestSave(), "Actual ordinary paid RequestSave prerequisite: " + Detail(s.LastSaveResult));
            GdDict selected = s.SaveLoadService.SelectGeneration("world");
            if (selected.GetBool("ok"))
            {
                Assert.AreEqual(s.RunId, selected.GetString("run_id"));
                Assert.AreEqual("world", selected.GetString("slot_id"));
                Assert.IsNotEmpty(selected.GetString("generation_id"));
                string text = selected.GetDictOrEmpty("payloads").GetString("world_text");
                Assert.IsNotEmpty(text, "Observe the actual selected full-generation world text.");
                return text;
            }
            Assert.IsFalse(rig.Storage.DirExists(PaidRoot), "Never observe legacy bytes after a refused existing paid root: " + Detail(selected));
            Assert.IsFalse(rig.Storage.FileExists(PaidRoot), "Malformed paid root also forbids baseline observation.");
            Assert.IsEmpty(selected.GetString("generation_id"), "A claimed generation cannot fall through to baseline observation.");
            Assert.IsEmpty(selected.GetString("save_mode"), "A claimed paid mode cannot fall through to baseline observation.");
            Assert.IsTrue(selected.GetDictOrEmpty("payloads").IsEmpty, "A claimed payload cannot fall through to baseline observation.");
            Assert.IsTrue(rig.Storage.FileExists(SaveLoadService.WORLD_SLOT_FILE), "Current baseline must really have written the world file.");
            return rig.Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE);
        }

        static GdDict EmittedHome(string text)
        {
            // Existing test-only exact reader isolates the production writer's emitted numeric tokens.
            GdDict world = GdJson.Parse(text, true) as GdDict;
            Assert.IsNotNull(world, "Actual emitted JSON must parse.");
            Assert.AreEqual("world-4", world.GetString("slice_version"));
            return world.GetDictOrEmpty("home_ship");
        }

        static GdDict LoadedHome(RunSession s)
        {
            WorldSnapshot world = s.SaveLoadService.LoadWorld();
            Assert.IsNotNull(world, "Actual production reader must accept the written world in the aligned engine context.");
            return world.HomeShip;
        }

        static object Fraction(GdDict progression) => progression.GetDictOrEmpty("skill_xp_fractional").Get("fabrication");
        static object Age(GdDict spoilage) => spoilage.GetDictOrEmpty("foods").GetDictOrEmpty("cooked_meal").Get("elapsed_seconds");
        static object Counter(GdDict progression) => progression.GetDictOrEmpty("cross_training").Get("fabrication");

        [TestCase(0.0)]
        [TestCase(0.25)]
        [TestCase(0.0000005)]
        [TestCase(0.9999995)]
        [TestCase(0.30000000000000004)]
        [TestCase(1e-33)]
        [TestCase(double.Epsilon)]
        public void OrdinaryPaidRequestSave_FractionMirrorRetainsExactValue(double value)
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            StartRunning(s);
            s.PlayerProgression.SkillXpFractional["fabrication"] = value;
            GdDict owner = ValidOwnerAndRawMirrors(s, "progression", "player_progression_summary");
            Real(value, Fraction(owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression")), "Canonical raw fraction prerequisite");
            GdDict emitted = EmittedHome(SaveAndObserveActualWorldText(rig));
            Real(value, Fraction(emitted.GetDictOrEmpty("player_progression_summary")), "WRITER exact fractional mirror");
            Real(value, Fraction(LoadedHome(s).GetDictOrEmpty("player_progression_summary")), "READER exact fractional mirror after writer control");
            Exact(owner, s.CapturePaidCraftingDomain(), "Save/read cannot change canonical work or progression.");
        }

        [TestCase(0.5)]
        [TestCase(0.30000000000000004)]
        [TestCase(1.0000000000000002)]
        public void OrdinaryPaidRequestSave_FoodAgeMirrorRetainsExactValue(double value)
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            string cooked = Start(s, "cook_basic_meal");
            s.AdvanceCrafting(Job(s, cooked).GetFloat("required_seconds"));
            Assert.AreEqual("completed_delivered", Job(s, cooked).GetString("status"));
            Assert.AreEqual(1L, s.InventoryState.GetQuantity("cooked_meal"));
            Assert.IsTrue(s.SpoilageState.HasFood("cooked_meal"), "Actual accepted paid food output registers its catalog food config.");
            StartRunning(s);
            FoodState food = s.SpoilageState.GetFood("cooked_meal");
            Assert.IsNotNull(food);
            Real(0.0, food.GetSummary().Get("elapsed_seconds"), "Genuine delivered food starts at zero age");
            GdDict configBefore = food.GetSummary();
            s.SpoilageState.Tick(value);
            Real(value, food.GetSummary().Get("elapsed_seconds"), "Actual food clock retains the raw age");
            foreach (var field in configBefore)
                if (V.Str(field.Key) != "elapsed_seconds") Exact(field.Value, food.GetSummary().Get(field.Key), "A small age tick preserves food config/stage " + field.Key);
            GdDict owner = ValidOwnerAndRawMirrors(s, "spoilage", "spoilage_summary");
            Real(value, Age(owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("spoilage")), "Canonical raw food-age prerequisite");
            GdDict emitted = EmittedHome(SaveAndObserveActualWorldText(rig));
            Real(value, Age(emitted.GetDictOrEmpty("spoilage_summary")), "WRITER exact food-age mirror");
            Real(value, Age(LoadedHome(s).GetDictOrEmpty("spoilage_summary")), "READER exact food-age mirror after writer control");
            Exact(owner, s.CapturePaidCraftingDomain(), "Save/read cannot freshen food or change paid receipts.");
        }

        [TestCase(9007199254740991L)]
        [TestCase(9007199254740992L)]
        [TestCase(9007199254740993L)]
        public void OrdinaryPaidRequestSave_CrossTrainingCounterRetainsExactInt64(long value)
        {
            SessionHarness.Rig rig = Boot();
            RunSession s = rig.Session;
            StartRunning(s);
            Assert.IsTrue(s.PlayerProgression.CrossTraining.Has("fabrication"), "Use an actual configured catalog skill counter.");
            s.PlayerProgression.CrossTraining["fabrication"] = value;
            GdDict owner = ValidOwnerAndRawMirrors(s, "progression", "player_progression_summary");
            object canonical = Counter(owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression"));
            Assert.IsInstanceOf<long>(canonical);
            Assert.AreEqual(value, (long)canonical);
            GdDict emitted = EmittedHome(SaveAndObserveActualWorldText(rig));
            object written = Counter(emitted.GetDictOrEmpty("player_progression_summary"));
            Assert.IsInstanceOf<long>(written, "WRITER control: invariant integer digits retain the literal long tag under exact test parsing.");
            Assert.AreEqual(value, (long)written, "WRITER control: all three Int64 token values are exact before production reading.");
            object read = Counter(LoadedHome(s).GetDictOrEmpty("player_progression_summary"));
            long observed;
            if (read is long exact) observed = exact;
            else
            {
                Assert.IsInstanceOf<double>(read, "READER must return a numeric counter.");
                double real = (double)read;
                Assert.IsFalse(double.IsNaN(real) || double.IsInfinity(real));
                Assert.AreEqual(Math.Truncate(real), real, "READER counter must remain integral.");
                Assert.IsTrue(real >= 0.0 && real < 9223372036854775808.0, "READER counter must fit a checked Int64 conversion.");
                observed = checked((long)real);
            }
            Assert.AreEqual(value, observed, "READER value loss is distinct from the passing writer control (2^53+1 must not collapse).");
            Assert.IsInstanceOf<long>(read, "READER paid counter must retain its canonical literal long tag even when a double could represent the value.");
            Exact(owner, s.CapturePaidCraftingDomain(), "Save/read cannot award XP, advance work or change canonical counters.");
        }
    }
}
