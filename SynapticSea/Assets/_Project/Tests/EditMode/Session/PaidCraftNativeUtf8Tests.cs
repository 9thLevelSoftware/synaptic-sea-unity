// DRAFT ONLY: not compiled, not installed under Assets, no .meta allocated.
// ROOT must explicitly release/promote after paid Unicode/capacity GREEN.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    public class PaidCraftNativeUtf8Tests : InfraDataTestBase
    {
        // Explicit evidence parent permits the same byte-preservation fixture on another host.
        // Keep the original Windows parent when not configured; never fall back to a user save/profile.
        static string NativeParent => Environment.GetEnvironmentVariable("SYNAPTIC_NATIVE_UTF8_EVIDENCE_DIR") ?? @"F:\tmp";
        const string WitnessPrefix = "native-utf8-opaque-before-";
        const string Witness = WitnessPrefix + "\uFFFD-after";
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        readonly List<RunSession> _sessions = new List<RunSession>();
        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUpEngine()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
        }

        [TearDown]
        public void DisposeSessionsWithoutDeletingEvidence()
        {
            try { foreach (RunSession session in _sessions) session.Dispose(); _sessions.Clear(); }
            finally { CoreServices.Engine = _previousEngine; }
            // Deliberately retain every new native root, on success and failure.
        }

        static string Detail(GdDict result) => result.GetString("reason") + ":" + result.GetString("detail");
        static string Sha(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        static string Exact(object value)
            => GdJson.Stringify(ComponentDomainCodec.Encode(new GdDict { { "value", V.DeepCopy(value) } }));

        static void AssertOwned(string root, string path)
        {
            string absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string absolute = Path.GetFullPath(path);
            Assert.IsTrue(absolute.StartsWith(absoluteRoot, StringComparison.OrdinalIgnoreCase), "Owned path required: " + absolute);
            for (string current = absolute; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (File.Exists(current) || Directory.Exists(current))
                    Assert.AreEqual(0, (int)(File.GetAttributes(current) & FileAttributes.ReparsePoint), "No reparse ancestors: " + current);
        }

        static FileSystemStorage FreshNative(out string root)
        {
            Assert.IsTrue(Directory.Exists(NativeParent), "Provision SYNAPTIC_NATIVE_UTF8_EVIDENCE_DIR (or the original Windows parent); no profile fallback.");
            root = Path.GetFullPath(Path.Combine(NativeParent, "ss-native-" + Guid.NewGuid().ToString("N")));
            Assert.IsFalse(File.Exists(root)); Assert.IsFalse(Directory.Exists(root));
            AssertOwned(NativeParent, root);
            // The longest generation payload/manifest names plus .replace.bak stay inside the existing guard.
            string longest = Path.Combine(root, "saves", ".component-live-generations", "g", new string('a', 64), "commit.json");
            Assert.LessOrEqual(longest.Length + 12, 259, "Do not relax coordinator physical-path budgets for the fixture.");
            TestContext.WriteLine("PRESERVED_NATIVE_UTF8_ROOT=" + root);
            return new FileSystemStorage(root);
        }

        SessionHarness.Rig Boot(FileSystemStorage native, bool diagnostic)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.Storage = native; // Rig.Storage intentionally remains MemoryStorage; NEVER use it as the save oracle.
            deps.EnablePaidCrafting = true; deps.EnableComponentIntegration = diagnostic;
            rig.Session = RunSession.Create(deps); _sessions.Add(rig.Session);
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, s.LastFailureReason);
            Assert.AreSame(native, s.SaveLoadService.Storage);
            Assert.AreEqual(diagnostic, s.ComponentIntegrationEnabled);
            Assert.IsTrue(s.PaidCraftingEnabled);
            s.ThreatManager.Threats.Clear(); s.InventoryState.Items.Clear();
            s.PlayerProgression.Skills["fabrication"] = 4L;
            s.VitalsState.Stamina = s.VitalsState.MaxStamina;
            s.HomeShip.LootedContainerIds.Add(Witness);
            rig.Scene.PlayerPosition = new Vec3(1, .5, 2);

            GdDict recipe = s.CraftingState.GetRecipe("weld_plating");
            Assert.IsFalse(recipe.IsEmpty);
            Assert.IsTrue(s.CraftingStations.Any(st => st.IsValid && st.StationKind == "workbench" &&
                ReferenceEquals(st.Parent, s.HomeShip.SceneRoot)));
            var station = s.CraftingState.GetStation("workbench");
            Assert.IsNotNull(station); station.SetPower(true);
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
                Assert.AreEqual(V.I64(ingredient.Value), s.InventoryState.AddItem(V.Str(ingredient.Key), V.I64(ingredient.Value)));
            GdDict started = s.RequestPaidCraft("workbench", "weld_plating", "native-utf8-paid-start");
            Assert.IsTrue(started.GetBool("ok"), Detail(started));
            Assert.IsTrue(started.GetBool("committed")); Assert.IsNotEmpty(started.GetString("commit_id"));
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
                Assert.AreEqual(0L, s.InventoryState.GetQuantity(V.Str(ingredient.Key)));
            GdDict initial = s.CapturePaidCraftingDomain().GetDictOrEmpty("participating_state")
                .GetDictOrEmpty("paid_crafting").GetDictOrEmpty("jobs").GetDictOrEmpty(started.GetString("job_id"));
            s.AdvanceCrafting(initial.GetFloat("required_seconds") / 4.0);
            GdDict owner = s.CapturePaidCraftingDomain();
            Assert.IsTrue(DomainBundle.TryCreate(owner, out _, out string reason), reason);
            Assert.IsTrue(s.ValidatePaidCraftingRestore(owner, out reason), reason);
            GdDict running = owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting")
                .GetDictOrEmpty("jobs").GetDictOrEmpty(started.GetString("job_id"));
            Assert.AreEqual("running", running.GetString("status"));
            Assert.Greater(running.GetFloat("progress_seconds"), 0.0);
            Assert.Less(running.GetFloat("progress_seconds"), running.GetFloat("required_seconds"));
            return rig;
        }

        static GdDict Live(SessionHarness.Rig rig)
        {
            RunSession s = rig.Session;
            return new GdDict {
                { "owner", s.CapturePaidCraftingDomain() }, { "run_id", s.RunId },
                { "inventory", s.InventoryState.GetSummary() }, { "progression", s.PlayerProgression.GetSummary() },
                { "crafting", s.CraftingState.GetSummary() }, { "field", s.FieldCraftingState.GetSummary() },
                { "spoilage", s.SpoilageState.GetSummary() }, { "training", s.TrainingEventBus.ToDict() },
                { "knowledge", s.RecipeKnowledge.GetSummary() }, { "looted", s.HomeShip.LootedContainerIds.DeepCopy() },
                { "position", rig.Scene.PlayerPosition }, { "spawns", (long)rig.Scene.SpawnCount },
                { "despawns", (long)rig.Scene.DespawnCount }
            };
        }

        static Dictionary<string, byte[]> Files(string root)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            void Visit(string directory)
            {
                foreach (string path in Directory.GetFiles(directory).OrderBy(p => p, StringComparer.Ordinal))
                { AssertOwned(root, path); result.Add(path, File.ReadAllBytes(path)); }
                foreach (string child in Directory.GetDirectories(directory).OrderBy(p => p, StringComparer.Ordinal))
                { AssertOwned(root, child); Visit(child); }
            }
            Visit(root); return result;
        }

        static void SameFiles(Dictionary<string, byte[]> expected, string root, string onlyChanged = null)
        {
            Dictionary<string, byte[]> actual = Files(root);
            Assert.AreEqual(expected.Count, actual.Count, "No native file creation/deletion during operation.");
            foreach (var pair in expected)
            {
                Assert.IsTrue(actual.ContainsKey(pair.Key), pair.Key);
                if (pair.Key != onlyChanged)
                    Assert.IsTrue(pair.Value.SequenceEqual(actual[pair.Key]), "Raw native bytes changed: " + pair.Key);
            }
        }

        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { found = false; break; }
                if (found) return i;
            }
            return -1;
        }

        static void CreateEvidence(string root, string name, byte[] bytes)
        {
            string directory = Path.Combine(root, "_utf8_evidence"); AssertOwned(root, directory);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name); AssertOwned(root, path);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }

        static void RecordInventory(string root, string name)
        {
            string text = string.Join("\n", Files(root).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Sha(pair.Value) + " " + pair.Value.Length.ToString(CultureInfo.InvariantCulture) + " " + pair.Key)) + "\n";
            // The new inventory lists all existing files; it deliberately does not claim a self-hash.
            CreateEvidence(root, name, StrictUtf8.GetBytes(text));
        }

        [TestCase(false, "run")]
        [TestCase(false, "world")]
        [TestCase(true, "run")]
        [TestCase(true, "world")]
        public void PaidSelection_RejectsMalformedOriginalUtf8EvenWhenReplacementTextMatches(bool diagnostic, string role)
        {
            FileSystemStorage native = FreshNative(out string root);
            SessionHarness.Rig rig = Boot(native, diagnostic); RunSession s = rig.Session;
            Assert.IsTrue(s.RequestSaveToSlot("world", "world", "Native UTF8 control"), Detail(s.LastSaveResult));
            GdDict selected = s.SaveLoadService.SelectGeneration("world");
            Assert.IsTrue(selected.GetBool("ok"), "Actual native full-save control: " + Detail(selected));
            Assert.AreEqual(diagnostic ? PaidSnapshotCodec.DiagnosticMode : PaidSnapshotCodec.OrdinaryMode, selected.GetString("save_mode"));
            GdDict payload = selected.GetDictOrEmpty("payloads");
            GdDict positive = s.SaveLoadService.ComponentCoordinator().ValidateSuppliedPayload(payload, s.RunId, "world");
            Assert.IsTrue(positive.GetBool("ok"), "Actual supplied-payload positive before native mutation: " + Detail(positive));
            GdDict exactPositive = s.SaveLoadService.ReadGeneration(s.RunId, "world", selected.GetString("generation_id"), selected.GetString("manifest_sha256"));
            Assert.IsTrue(exactPositive.GetBool("ok"), Detail(exactPositive));

            // Exact test reader observes fields; do not infer paid codec policy or weaken admission.
            GdDict run = GdJson.Parse(payload.GetString("run_text"), true) as GdDict;
            GdDict world = GdJson.Parse(payload.GetString("world_text"), true) as GdDict;
            Assert.IsNotNull(run); Assert.IsNotNull(world);
            Assert.IsTrue(run.GetArrayOrEmpty("home_looted_containers").Contains(Witness));
            Assert.IsTrue(world.GetArrayOrEmpty("home_looted_containers").Contains(Witness));

            string generationRoot = diagnostic ? SaveLoadService.ComponentGenerationRoot : SaveLoadService.PaidGenerationRoot;
            string directory = generationRoot + "/g/" + GenerationFixtures.TupleToken(s.RunId, "world", selected.GetString("generation_id"));
            string logical = directory + "/" + role + ".json", physical = native.Globalize(logical);
            AssertOwned(root, physical); Assert.LessOrEqual(physical.Length + 12, 259);
            byte[] original = File.ReadAllBytes(physical); string originalText = payload.GetString(role + "_text");
            Assert.AreEqual(originalText, StrictUtf8.GetString(original));
            Assert.IsTrue(original.SequenceEqual(StrictUtf8.GetBytes(originalText)), "Original save is literal BOM-free UTF8.");
            int witnessStart = IndexOf(original, StrictUtf8.GetBytes(Witness));
            Assert.GreaterOrEqual(witnessStart, 0, "Prerequisite: writer emits legitimate U+FFFD literally, not a Unicode escape.");
            int offset = witnessStart + StrictUtf8.GetByteCount(WitnessPrefix);
            Assert.AreEqual(0xEF, original[offset]); Assert.AreEqual(0xBF, original[offset + 1]); Assert.AreEqual(0xBD, original[offset + 2]);
            byte[] malformed = new byte[original.Length - 2];
            Buffer.BlockCopy(original, 0, malformed, 0, offset); malformed[offset] = 0xFF;
            Buffer.BlockCopy(original, offset + 3, malformed, offset + 1, original.Length - offset - 3);
            bool strictRefused = false;
            try { StrictUtf8.GetString(malformed); } catch (DecoderFallbackException) { strictRefused = true; }
            Assert.IsTrue(strictRefused, "Independent byte-level malformed UTF8 prerequisite.");

            GdDict manifest = GdJson.Parse(native.ReadText(directory + "/commit.json"), true) as GdDict;
            Assert.IsNotNull(manifest);
            GdDict entry = manifest.GetArrayOrEmpty("entries").OfType<GdDict>().Single(e => e.GetString("role") == role);
            Assert.AreEqual(Sha(original), entry.GetString("sha256"));
            Assert.AreEqual(original.Length.ToString(CultureInfo.InvariantCulture), entry.GetString("byte_length"));
            CreateEvidence(root, "original.bin", original); CreateEvidence(root, "mutated.bin", malformed);
            string evidence = "role=" + role + "\nmode=" + selected.GetString("save_mode") + "\nlogical=" + logical +
                "\nphysical=" + physical + "\noffset=" + offset.ToString(CultureInfo.InvariantCulture) +
                "\noriginal_length=" + original.Length.ToString(CultureInfo.InvariantCulture) + "\noriginal_sha256=" + Sha(original) +
                "\nmutated_length=" + malformed.Length.ToString(CultureInfo.InvariantCulture) + "\nmutated_sha256=" + Sha(malformed) + "\n";
            CreateEvidence(root, "mutation.txt", StrictUtf8.GetBytes(evidence)); TestContext.WriteLine(evidence);

            RecordInventory(root, "before-mutation-files.txt");
            Dictionary<string, byte[]> beforeMutation = Files(root);
            AssertOwned(root, physical); File.WriteAllBytes(physical, malformed); // The sole deliberate native corruption.
            SameFiles(beforeMutation, root, physical);
            Assert.IsTrue(malformed.SequenceEqual(File.ReadAllBytes(physical)));
            string replacementText = native.ReadText(logical);
            Assert.AreEqual(originalText, replacementText, "Legacy FileSystemStorage decoding masks exactly this byte mutation.");
            Assert.AreEqual(entry.GetString("sha256"), Sha(StrictUtf8.GetBytes(replacementText)), "Decoded-text SHA still matches.");
            Assert.AreEqual(entry.GetString("byte_length"), StrictUtf8.GetByteCount(replacementText).ToString(CultureInfo.InvariantCulture), "Re-encoded length still matches.");
            Assert.AreNotEqual(Sha(original), Sha(malformed)); Assert.AreEqual(original.Length - 2, malformed.Length);

            RecordInventory(root, "before-selection-files.txt");
            Dictionary<string, byte[]> beforeSelection = Files(root); string liveBefore = Exact(Live(rig));
            GdDict refused = s.SaveLoadService.SelectGeneration("world");
            GdDict exactRefused = s.SaveLoadService.ReadGeneration(s.RunId, "world", selected.GetString("generation_id"), selected.GetString("manifest_sha256"));
            SameFiles(beforeSelection, root);
            Assert.AreEqual(liveBefore, Exact(Live(rig)), "Selection never changes canonical or raw live state.");
            TestContext.WriteLine("SELECT_RESULT=" + Detail(refused) + "; ok=" + refused.GetBool("ok") +
                "; EXACT_RESULT=" + Detail(exactRefused) + "; ok=" + exactRefused.GetBool("ok"));
            // Both calls and preservation assertions run before the expected RED assertions.
            Assert.IsFalse(refused.GetBool("ok"), "Malformed ORIGINAL bytes must refuse despite identical decoded text: " + Detail(refused));
            Assert.IsFalse(exactRefused.GetBool("ok"), "Exact generation read must apply the same native byte boundary: " + Detail(exactRefused));
            Assert.IsTrue(refused.GetDictOrEmpty("payloads").IsEmpty);
            Assert.IsTrue(exactRefused.GetDictOrEmpty("payloads").IsEmpty);
        }
    }
}
