using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class SaveGenerationStoreTests
    {
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
        public void EveryLogicalSlotCommitsEntireExactBundleAndRestarts(string slot, string kind)
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.Request(slot, kind);
            GenerationFixtures.Commit(storage, request);
            GdDict recovered = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, slot);
            GenerationFixtures.AssertBundle(request, recovered);
            Assert.AreEqual("pointer", recovered.GetString("reason"));
            Assert.IsFalse(recovered.GetBool("recovered"));
            Assert.IsFalse(storage.FileExists("user://saves/index.json"));
            Assert.IsFalse(storage.FileExists("user://saves/" + slot + ".json"));
        }

        [TestCase("world", "world")]
        [TestCase("autosave_active", "auto")]
        [TestCase("quicksave", "quick")]
        [TestCase("slot_06", "manual")]
        public void NativeRestartPreservesWholeOriginalUtf8ByteSet(string slot, string kind)
        {
            string directory = GenerationFixtures.NativeDirectory();
            var storage = new FileSystemStorage(directory);
            GdDict request = GenerationFixtures.Request(slot, kind);
            GdDict commit = null, recovered = null;
            try
            {
                commit = GenerationFixtures.Coordinator(storage).Commit(request, GenerationFixtures.Run, slot);
                Assert.IsTrue(commit.GetBool("committed"), GdJson.Stringify(commit));
                recovered = GenerationFixtures.Coordinator(new FileSystemStorage(directory)).Recover(GenerationFixtures.Run, slot);
                GenerationFixtures.AssertBundle(request, recovered);
                foreach (var pair in GenerationFixtures.Texts(request))
                {
                    byte[] saved = File.ReadAllBytes(storage.Globalize(GenerationFixtures.PayloadPath(request, pair.Key)));
                    CollectionAssert.AreEqual(new UTF8Encoding(false, true).GetBytes(pair.Value), saved);
                }
            }
            finally { GenerationFixtures.NativeEvidence(directory, request, commit, recovered); }
        }

        [TestCase(9007199254740993L)]
        [TestCase(long.MaxValue)]
        public void InternalLongRevisionSurvivesDecimalStringWireWithoutRounding(long revision)
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.Request();
            request["domain_revision"] = revision;
            request.GetDictOrEmpty("binding").GetDictOrEmpty("owner_revisions")["ship_start"] = revision;
            GenerationFixtures.Commit(storage, request);
            GdDict manifest = (GdDict)GdJson.ParseString(storage.ReadText(GenerationFixtures.Generation(request) + "/commit.json"));
            Assert.AreEqual(revision.ToString(System.Globalization.CultureInfo.InvariantCulture), manifest.Get("domain_revision"));
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.IsInstanceOf<long>(result.GetDictOrEmpty("payloads").Get("domain_revision"));
            Assert.AreEqual(revision, result.GetDictOrEmpty("payloads").Get("domain_revision"));
        }

        [Test]
        public void AwayOwnerKeepsDifferentHomePoseAndExplicitRetainedReferenceClosure()
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.Request(away: true);
            GenerationFixtures.Commit(storage, request);
            GenerationFixtures.AssertBundle(request, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
            var world = (GdDict)GdJson.ParseString(request.GetString("world_text"));
            Assert.IsFalse(V.VariantEquals(world.GetDictOrEmpty("home_ship").Get("player_position"), world.Get("player_position_in_ship")));
            Assert.AreEqual(5, request.GetArrayOrEmpty("artifacts").Count);
        }

        [TestCase("wrong_run")]
        [TestCase("wrong_world_run")]
        [TestCase("wrong_home_run")]
        [TestCase("wrong_slot")]
        [TestCase("wrong_kind")]
        [TestCase("wrong_flag")]
        [TestCase("future_run")]
        [TestCase("older_run")]
        [TestCase("future_world")]
        [TestCase("missing_run_field")]
        [TestCase("missing_world_field")]
        [TestCase("malformed_run")]
        [TestCase("malformed_world")]
        [TestCase("wrong_engine")]
        [TestCase("dead_health")]
        [TestCase("missing_layout")]
        [TestCase("missing_slice")]
        [TestCase("missing_kit")]
        [TestCase("future_kit")]
        [TestCase("kit_missing_module")]
        [TestCase("kit_duplicate_module")]
        [TestCase("duplicate_artifact")]
        [TestCase("case_collision")]
        [TestCase("traversal")]
        [TestCase("rooted_reference")]
        [TestCase("invalid_layout")]
        [TestCase("invalid_slice")]
        [TestCase("bad_dock_edge")]
        [TestCase("seed_mismatch")]
        [TestCase("unknown_location")]
        [TestCase("wrong_binding_owner")]
        [TestCase("wrong_binding_pose")]
        [TestCase("missing_owner_revision")]
        [TestCase("double_revision")]
        [TestCase("negative_revision")]
        [TestCase("incompatible_content")]
        [TestCase("unknown_profile")]
        [TestCase("missing_away_layout")]
        [TestCase("retained_mapping_mismatch")]
        [TestCase("retained_seed_mismatch")]
        [TestCase("missing_lifeboat_mapping")]
        [TestCase("invalid_utf16")]
        public void InvalidCompletePayloadRefusesWithoutPublishingOrChangingOldSelection(string fault)
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            string pointer = storage.ReadText(GenerationFixtures.Active("slot_01"));
            GdDict request = GenerationFixtures.Child(storage, old, "invalid-child", 2L, fault == "unknown_profile" || fault == "missing_away_layout" || fault == "retained_mapping_mismatch" || fault == "retained_seed_mismatch");
            GenerationFixtures.Mutate(request, fault);
            GdDict result = GenerationFixtures.Coordinator(storage).Commit(request, GenerationFixtures.Run, "slot_01");
            Assert.IsFalse(result.GetBool("ok"));
            Assert.IsFalse(result.GetBool("committed"));
            Assert.AreNotEqual("not_implemented", result.GetString("reason"));
            Assert.AreEqual(pointer, storage.ReadText(GenerationFixtures.Active("slot_01")));
            GenerationFixtures.AssertBundle(old, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
        }

        [TestCase("user://saves/../escape")]
        [TestCase("C:/outside")]
        [TestCase("\\\\server\\share")]
        [TestCase("user://saves")]
        [TestCase("user://saves/.generations//child")]
        [TestCase("user://saves/.generations/./child")]
        public void UnsafeInjectedRootRejectsBeforeAnyStorageCall(string root)
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict result = new SaveCommitCoordinator(storage, root, new GenerationAuthority(), GenerationFixtures.Compatibility())
                .Commit(GenerationFixtures.Request(), GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("invalid_root", result.GetString("reason"));
            Assert.AreEqual(0, storage.Calls);
        }

        [TestCase("")]
        [TestCase("../slot_01")]
        [TestCase("SLOT_01")]
        [TestCase("user://saves/world")]
        public void InvalidLogicalSlotRejectsBeforeAnyStorageCall(string slot)
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict result = GenerationFixtures.Coordinator(storage).Commit(GenerationFixtures.Request(), GenerationFixtures.Run, slot);
            Assert.AreEqual("invalid_identity", result.GetString("reason"));
            Assert.AreEqual(0, storage.Calls);
        }

        [Test]
        public void OpaqueRunAndGenerationIdentifiersAreHashedAndCannotEscapeRoot()
        {
            var storage = new MemoryStorage();
            const string opaqueRun = "../Run:A\\B";
            GdDict request = GenerationFixtures.Request(run: opaqueRun, generation: "../Generation:C\\D");
            GenerationFixtures.Commit(storage, request);
            GenerationFixtures.AssertBundle(request, GenerationFixtures.Coordinator(storage).Recover(opaqueRun, "slot_01"));
            Assert.IsTrue(storage.FileExists(GenerationFixtures.Generation(request) + "/commit.json"));
            Assert.IsFalse(storage.DirExists("user://Run:A"));
        }

        [TestCase("mutable_key")]
        [TestCase("cycle")]
        public void UnsafeRequestGraphRejectsBeforeCopyOrIo(string graph)
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict request = GenerationFixtures.Request();
            if (graph == "cycle") request["opaque"] = request;
            else request["opaque"] = new GdDict { { GdArray.Of("mutable"), "value" } };
            GdDict result = GenerationFixtures.Coordinator(storage).Commit(request, GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("invalid_request", result.GetString("reason"));
            Assert.AreEqual(0, storage.Calls);
        }

        [TestCase("stale_parent", "stale_parent")]
        [TestCase("stale_hash", "stale_parent")]
        [TestCase("stale_revision", "stale_revision")]
        [TestCase("different_owner", "slot_owner_conflict")]
        [TestCase("same_id_different_bytes", "generation_conflict")]
        public void ExistingSlotGuardsKeepOriginalGeneration(string mode, string reason)
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            string pointer = storage.ReadText(GenerationFixtures.Active("slot_01"));
            GdDict child = GenerationFixtures.Child(storage, old);
            if (mode == "stale_parent") child["parent_generation_id"] = "missing-parent";
            if (mode == "stale_hash") child["expected_pointer_sha256"] = new string('0', 64);
            if (mode == "stale_revision") child["domain_revision"] = 1L;
            if (mode == "different_owner") child = GenerationFixtures.Request(run: "another-run");
            if (mode == "same_id_different_bytes") { child["generation_id"] = old.GetString("generation_id"); child["run_text"] = child.GetString("run_text") + " "; }
            GdDict result = GenerationFixtures.Coordinator(storage).Commit(child, child.GetString("run_id"), "slot_01");
            Assert.AreEqual(reason, result.GetString("reason"));
            Assert.IsFalse(result.GetBool("committed"));
            Assert.AreEqual(pointer, storage.ReadText(GenerationFixtures.Active("slot_01")));
            GenerationFixtures.AssertBundle(old, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
        }

        [Test]
        public void SameCommittedGenerationIsIdempotentAndRetainsExactPreviousParent()
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            string previous = storage.ReadText(GenerationFixtures.Active("slot_01"));
            GdDict child = GenerationFixtures.Child(storage, old);
            GenerationFixtures.Commit(storage, child);
            int writes = storage.Writes;
            GenerationFixtures.Commit(storage, child);
            Assert.AreEqual(writes, storage.Writes);
            Assert.AreEqual(previous, storage.ReadText(GenerationFixtures.Previous("slot_01")));
            Assert.AreEqual(old.GetString("run_text"), storage.ReadText(GenerationFixtures.Generation(old) + "/run.json"));
        }

        [TestCase("before_payload:run")]
        [TestCase("after_payload:run")]
        [TestCase("before_payload:world")]
        [TestCase("after_payload:world")]
        [TestCase("before_payload:user://runs/generation-fixture/layout.json")]
        [TestCase("after_payload:user://runs/generation-fixture/layout.json")]
        [TestCase("before_payload:user://runs/generation-fixture/gameplay_slice.json")]
        [TestCase("after_payload:user://runs/generation-fixture/gameplay_slice.json")]
        [TestCase("before_payload:res://data/kits/ship_structural_v0.json")]
        [TestCase("after_payload:res://data/kits/ship_structural_v0.json")]
        [TestCase("before_validation")]
        [TestCase("after_validation")]
        [TestCase("before_manifest")]
        [TestCase("after_manifest")]
        [TestCase("before_previous_pointer")]
        [TestCase("after_previous_pointer")]
        [TestCase("before_pointer")]
        public void EveryPrepublicationFaultRestartsToEntireOldBundle(string stage)
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            string pointer = storage.ReadText(GenerationFixtures.Active("slot_01"));
            bool observed = false;
            GdDict child = GenerationFixtures.Child(storage, old);
            GdDict result = GenerationFixtures.Coordinator(storage, fault: s => { if (s == stage) { observed = true; throw new IOException("injected"); } })
                .Commit(child, GenerationFixtures.Run, "slot_01");
            Assert.IsTrue(observed, "fault hook must execute at the declared boundary");
            Assert.IsFalse(result.GetBool("committed"));
            Assert.AreEqual("not_committed", result.GetString("outcome"));
            Assert.AreEqual(pointer, storage.ReadText(GenerationFixtures.Active("slot_01")));
            GenerationFixtures.AssertBundle(old, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
        }

        [TestCase("after_pointer")]
        [TestCase("before_index")]
        [TestCase("after_index")]
        public void PostPointerFaultReportsCommittedAndRestartReturnsEntireNewBundle(string stage)
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GdDict child = GenerationFixtures.Child(storage, old);
            bool observed = false;
            GdDict result = GenerationFixtures.Coordinator(storage, fault: s => { if (s == stage) { observed = true; throw new IOException("injected"); } })
                .Commit(child, GenerationFixtures.Run, "slot_01");
            Assert.IsTrue(observed);
            Assert.IsTrue(result.GetBool("committed"));
            Assert.AreEqual("committed", result.GetString("outcome"));
            Assert.IsTrue(result.GetBool("index_reconciliation_needed"));
            GenerationFixtures.AssertBundle(child, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
        }

        [Test]
        public void ThrowAfterPointerPublicationReopensBeforeReturningCommitted()
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GdDict child = GenerationFixtures.Child(storage, old);
            storage.AfterWrite = (path, text) => { if (path == GenerationFixtures.Active("slot_01")) throw new IOException("published then threw"); };
            GdDict result = GenerationFixtures.Coordinator(storage).Commit(child, GenerationFixtures.Run, "slot_01");
            Assert.IsTrue(result.GetBool("committed"));
            storage.AfterWrite = null;
            GenerationFixtures.AssertBundle(child, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
        }

        [Test]
        public void UnreadablePointerAfterPublicationIsUnknownAndDoesNotEncourageReplay()
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GdDict child = GenerationFixtures.Child(storage, old);
            storage.AfterWrite = (path, text) => { if (path == GenerationFixtures.Active("slot_01")) { storage.Unreadable.Add(path); throw new IOException("published then inaccessible"); } };
            GdDict result = GenerationFixtures.Coordinator(storage).Commit(child, GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("unknown", result.GetString("outcome"));
            Assert.AreEqual("publication_unknown", result.GetString("reason"));
            storage.AfterWrite = null; storage.Unreadable.Clear();
            GenerationFixtures.AssertBundle(child, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
        }

        [TestCase("payload_temp")]
        [TestCase("payload_source")]
        [TestCase("pointer")]
        [TestCase("truncate")]
        [TestCase("after_pointer")]
        public void NativePublicationFaultRetainsOldOrVerifiedNewCompleteByteSet(string mode)
        {
            string directory = GenerationFixtures.NativeDirectory();
            var native = new FileSystemStorage(directory);
            var storage = new GenerationStorage(native);
            GdDict old = GenerationFixtures.Request(), child = null, result = null, recovered = null;
            FileStream locked = null;
            try
            {
                GenerationFixtures.Commit(storage, old);
                child = GenerationFixtures.Child(storage, old);
                if (mode == "pointer") locked = new FileStream(native.Globalize(GenerationFixtures.Active("slot_01")), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (mode == "payload_temp")
                {
                    native.MakeDirRecursive(GenerationFixtures.Generation(child));
                    string tmp = native.Globalize(GenerationFixtures.Generation(child) + "/run.json") + ".tmp";
                    File.WriteAllText(tmp, "retained-lock", new UTF8Encoding(false));
                    locked = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                if (mode == "truncate") storage.Rewrite = (path, text) => path == GenerationFixtures.Generation(child) + "/run.json" ? text.Substring(0, text.Length / 2) : text;
                if (mode == "after_pointer") storage.AfterWrite = (path, text) => { if (path == GenerationFixtures.Active("slot_01")) throw new IOException("actual pointer published"); };
                result = GenerationFixtures.Coordinator(storage, fault: s =>
                {
                    if (mode == "payload_source" && s == "after_payload:run")
                        locked = new FileStream(native.Globalize(GenerationFixtures.Generation(child) + "/run.json"), FileMode.Open, FileAccess.Read, FileShare.None);
                }).Commit(child, GenerationFixtures.Run, "slot_01");
                if (locked != null) { locked.Dispose(); locked = null; }
                // Shared-open handles do not prevent POSIX truncation/rename. Exclusive source locks
                // and injected truncation remain real prepublication failures on both platforms.
                bool sharedTempCanPublish = Path.DirectorySeparatorChar != '\\' && Type.GetType("Mono.Runtime") == null;
                // Unity Mono denies writing the shared-open temporary file even on macOS.
                bool published = mode == "after_pointer" ||
                    Path.DirectorySeparatorChar != '\\' && mode == "pointer" ||
                    sharedTempCanPublish && mode == "payload_temp";
                Assert.AreEqual(published, result.GetBool("committed"));
                recovered = GenerationFixtures.Coordinator(new FileSystemStorage(directory)).Recover(GenerationFixtures.Run, "slot_01");
                GenerationFixtures.AssertBundle(published ? child : old, recovered);
                Assert.AreEqual(old.GetString("run_text"), native.ReadText(GenerationFixtures.Generation(old) + "/run.json"));
            }
            finally
            {
                if (locked != null) locked.Dispose();
                GenerationFixtures.NativeEvidence(directory, child ?? old, result, recovered);
            }
        }

        [Test]
        public void ValidPointerWinsOverCompleteNewerOrphan()
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GenerationFixtures.Stage(storage, GenerationFixtures.Child(storage, old));
            GenerationFixtures.AssertBundle(old, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
        }

        [Test]
        public void AbsentPointerPromotesUniqueResolvedLineageTip()
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GdDict child = GenerationFixtures.Child(storage, old);
            GenerationFixtures.Stage(storage, child);
            storage.Delete(GenerationFixtures.Active("slot_01"));
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            GenerationFixtures.AssertBundle(child, result);
            Assert.IsTrue(result.GetBool("recovered"));
            Assert.AreEqual("lineage_tip", result.GetString("reason"));
            Assert.IsTrue(storage.FileExists(GenerationFixtures.Active("slot_01")));
        }

        [TestCase("branches")]
        [TestCase("independent_roots")]
        [TestCase("unresolved_parent")]
        public void AbsentPointerAmbiguityRetainsAllCandidatesWithoutTimestampGuess(string shape)
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GenerationFixtures.Stage(storage, GenerationFixtures.Child(storage, old, "branch-a", 2L));
            if (shape == "branches") GenerationFixtures.Stage(storage, GenerationFixtures.Child(storage, old, "branch-b", 3L));
            if (shape == "independent_roots")
            {
                var other = new MemoryStorage();
                GdDict root = GenerationFixtures.Request(generation: "independent");
                GenerationFixtures.Commit(other, root);
                GenerationFixtures.CopyTree(other, storage, GenerationFixtures.Generation(root));
            }
            if (shape == "unresolved_parent") storage.Delete(GenerationFixtures.Generation(old) + "/commit.json");
            storage.Delete(GenerationFixtures.Active("slot_01"));
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("ambiguous", result.GetString("reason"));
            Assert.IsNull(result.Get("payloads"));
            Assert.Greater(result.GetArrayOrEmpty("candidates").Count, 0);
            Assert.IsFalse(storage.FileExists(GenerationFixtures.Active("slot_01")));
        }

        [Test]
        public void CorruptActiveUsesVerifiedPreviousAndPreservesCorruptBytes()
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GenerationFixtures.Commit(storage, GenerationFixtures.Child(storage, old));
            storage.WriteText(GenerationFixtures.Active("slot_01"), "{corrupt-pointer");
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            GenerationFixtures.AssertBundle(old, result);
            Assert.IsTrue(result.GetBool("recovered"));
            Assert.AreEqual("previous_pointer", result.GetString("reason"));
            Assert.AreEqual("{corrupt-pointer", storage.ReadText(GenerationFixtures.Active("slot_01")));
        }

        [Test]
        public void OtherOwnerActivePointerBlocksOldRunBeforeOrphanOrPreviousFallback()
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GenerationFixtures.Stage(storage, GenerationFixtures.Child(storage, old));
            string oldPointer = storage.ReadText(GenerationFixtures.Active("slot_01"));
            var otherStorage = new MemoryStorage();
            GdDict other = GenerationFixtures.Request(run: "current-owner-run");
            GenerationFixtures.Commit(otherStorage, other);
            GenerationFixtures.CopyTree(otherStorage, storage, GenerationFixtures.Generation(other));
            string otherPointer = otherStorage.ReadText(GenerationFixtures.Active("slot_01"));
            storage.WriteText(GenerationFixtures.Previous("slot_01"), oldPointer);
            storage.WriteText(GenerationFixtures.Active("slot_01"), otherPointer);
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("slot_owner_conflict", result.GetString("reason"));
            Assert.IsNull(result.Get("payloads"));
            Assert.AreEqual(otherPointer, storage.ReadText(GenerationFixtures.Active("slot_01")));
            Assert.AreEqual(oldPointer, storage.ReadText(GenerationFixtures.Previous("slot_01")));
        }

        [TestCase("malformed")]
        [TestCase("unknown_version")]
        [TestCase("bad_closure")]
        public void UncertainCompleteCandidateBlocksAbsentPointerOlderTipSelection(string shape)
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GdDict child = GenerationFixtures.Child(storage, old);
            GenerationFixtures.Stage(storage, child);
            string path = GenerationFixtures.Generation(child) + "/commit.json";
            if (shape == "malformed") storage.WriteText(path, "{malformed-complete-candidate");
            if (shape == "unknown_version")
            {
                var manifest = (GdDict)GdJson.ParseString(storage.ReadText(path));
                manifest["schema_version"] = "save-commit-99"; storage.WriteText(path, GdJson.Stringify(manifest));
            }
            if (shape == "bad_closure") storage.Delete(GenerationFixtures.PayloadPath(child, GenerationFixtures.Kit));
            string retained = storage.ReadText(path);
            storage.Delete(GenerationFixtures.Active("slot_01"));
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("ambiguous", result.GetString("reason"));
            Assert.IsNull(result.Get("payloads"));
            Assert.Greater(result.GetArrayOrEmpty("candidates").Count, 0);
            Assert.IsFalse(storage.FileExists(GenerationFixtures.Active("slot_01")));
            Assert.AreEqual(retained, storage.ReadText(path));
        }

        [TestCase("run")]
        [TestCase("world")]
        [TestCase("layout")]
        [TestCase("kit")]
        [TestCase("manifest")]
        [TestCase("pointer_identity")]
        [TestCase("previous")]
        public void CorruptReferencedBytesNeverReturnSplicedOrDefaultedPayloads(string role)
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, request);
            string path = GenerationFixtures.Generation(request) + "/run.json";
            if (role == "world") path = GenerationFixtures.Generation(request) + "/world.json";
            if (role == "layout") path = GenerationFixtures.PayloadPath(request, GenerationFixtures.Layout);
            if (role == "kit") path = GenerationFixtures.PayloadPath(request, GenerationFixtures.Kit);
            if (role == "manifest") path = GenerationFixtures.Generation(request) + "/commit.json";
            if (role == "pointer_identity")
            {
                var pointer = (GdDict)GdJson.ParseString(storage.ReadText(GenerationFixtures.Active("slot_01")));
                pointer["run_id"] = "another-run";
                path = GenerationFixtures.Active("slot_01"); storage.WriteText(path, GdJson.Stringify(pointer));
            }
            else if (role == "previous")
            {
                storage.WriteText(GenerationFixtures.Active("slot_01"), "bad-active");
                path = GenerationFixtures.Previous("slot_01"); storage.WriteText(path, "bad-previous");
            }
            else storage.WriteText(path, (storage.ReadText(path) ?? "") + "corrupt");
            string retained = storage.ReadText(path);
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.IsFalse(result.GetBool("ok"));
            Assert.IsNull(result.Get("payloads"));
            Assert.AreEqual(retained, storage.ReadText(path));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingOrCorruptOwnedIndexReconstructsFromVerifiedPointer(bool corrupt)
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, request);
            if (corrupt) storage.WriteText(GenerationFixtures.Root + "/index.json", "broken");
            else storage.Delete(GenerationFixtures.Root + "/index.json");
            GenerationFixtures.AssertBundle(request, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
            var index = (GdDict)GdJson.ParseString(storage.ReadText(GenerationFixtures.Root + "/index.json"));
            Assert.AreEqual(SaveCommitCoordinator.IndexVersion, index.GetString("schema_version"));
            Assert.AreEqual("slot_01", ((GdDict)index.GetArrayOrEmpty("slots")[0]).GetString("slot_id"));
            Assert.IsFalse(storage.FileExists("user://saves/index.json"));
        }

        [Test]
        public void InputAndOutputAliasesCannotChangePrivateCandidateOrRetainedBytes()
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.Request(), expected = request.DeepCopy();
            bool observed = false;
            GenerationFixtures.Commit(storage, request, s =>
            {
                if (s == "before_payload:run") { observed = true; request["run_text"] = "mutated"; request.GetDictOrEmpty("binding")["current_owner_id"] = "other"; }
            });
            Assert.IsTrue(observed);
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            GenerationFixtures.AssertBundle(expected, result);
            result.GetDictOrEmpty("payloads")["run_text"] = "mutated result";
            GenerationFixtures.AssertBundle(expected, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
        }

        [Test]
        public void HookCannotReenterMutatingCoordinator()
        {
            var storage = new MemoryStorage();
            SaveCommitCoordinator coordinator = null;
            GdDict nested = null;
            coordinator = GenerationFixtures.Coordinator(storage, fault: s =>
            {
                if (s == "before_payload:run") nested = coordinator.Commit(GenerationFixtures.Request(generation: "nested"), GenerationFixtures.Run, "slot_01");
            });
            GdDict outer = coordinator.Commit(GenerationFixtures.Request(), GenerationFixtures.Run, "slot_01");
            Assert.IsTrue(outer.GetBool("committed"));
            Assert.IsNotNull(nested);
            Assert.AreEqual("reentrant", nested.GetString("reason"));
            Assert.IsFalse(storage.FileExists(GenerationFixtures.Generation(GenerationFixtures.Request(generation: "nested")) + "/commit.json"));
        }

        [TestCase("generation_id")]
        [TestCase("run_id")]
        public void NumericRequestIdentityIsNotCoercedIntoOpaqueStringBeforeIo(string field)
        {
            var storage = new GenerationStorage(new MemoryStorage());
            string run = field == "run_id" ? "17" : GenerationFixtures.Run;
            GdDict request = GenerationFixtures.Request(run: run);
            request[field] = 17L;
            GdDict result = GenerationFixtures.Coordinator(storage).Commit(request, run, "slot_01");
            Assert.AreEqual("invalid_request", result.GetString("reason"));
            Assert.IsFalse(result.GetBool("committed"));
            Assert.AreEqual(0, storage.Writes);
        }

        [Test]
        public void NumericCompatibilityStampCannotBeAcceptedAsItsStringRepresentation()
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict compatibility = GenerationFixtures.Compatibility();
            compatibility["catalog_version"] = 1L;
            GdDict request = GenerationFixtures.Request();
            request["compatibility"] = compatibility.DeepCopy();
            GdDict result = new SaveCommitCoordinator(storage, GenerationFixtures.Root, new GenerationAuthority(), compatibility)
                .Commit(request, GenerationFixtures.Run, "slot_01");
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual("incompatible_content", result.GetString("reason"));
            Assert.AreEqual(0, storage.Writes);
        }

        [Test]
        public void PresentLifeboatUsesActualInlineBlueprintAndCompleteSuppliedClosure()
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.WithLifeboat(GenerationFixtures.Request());
            GenerationFixtures.Commit(storage, request);
            GenerationFixtures.AssertBundle(request, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
            Assert.AreEqual(5, request.GetArrayOrEmpty("artifacts").Count);
        }

        [TestCase("missing")]
        [TestCase("malformed")]
        public void PresentLifeboatCannotSkipBlueprintValidation(string shape)
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict request = GenerationFixtures.WithLifeboat(GenerationFixtures.Request());
            var world = (GdDict)GdJson.ParseString(request.GetString("world_text"));
            GdDict lifeboat = world.GetDictOrEmpty("mobile_home_state").GetDictOrEmpty("lifeboat");
            if (shape == "missing") lifeboat.Erase("blueprint");
            else lifeboat["blueprint"] = 17L;
            request["world_text"] = GdJson.Stringify(world);
            GdDict result = GenerationFixtures.Coordinator(storage).Commit(request, GenerationFixtures.Run, "slot_01");
            Assert.IsFalse(result.GetBool("ok"));
            Assert.IsFalse(result.GetBool("committed"));
            Assert.AreEqual("binding_mismatch", result.GetString("reason"));
            Assert.AreEqual(0, storage.Writes);
        }

        [Test]
        public void ActualCoherentLayoutAndSliceJoinSuppliedRealKitAndRetainExactBytes()
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.WithCoherentDocuments(GenerationFixtures.Request());
            Assert.AreEqual("84ecd46201f16add1c81a06c7d928b2409f787217bb903862e008383483052c9", GenerationFixtures.Hash(GenerationFixtures.Texts(request)[GenerationFixtures.Layout]));
            Assert.AreEqual("fa058b29d17ffd3c88306dcbdb2dab753322a43474d615497b175f08f6822bd3", GenerationFixtures.Hash(GenerationFixtures.Texts(request)[GenerationFixtures.Slice]));
            GenerationFixtures.Commit(storage, request);
            GenerationFixtures.AssertBundle(request, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
        }

        [TestCase("module")]
        [TestCase("grid")]
        public void ActualCoherentLayoutMustResolveNamedModulesAndGridInSuppliedKit(string mismatch)
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict request = GenerationFixtures.WithCoherentDocuments(GenerationFixtures.Request());
            foreach (GdDict artifact in request.GetArrayOrEmpty("artifacts"))
            {
                if (artifact.GetString("logical_path") != GenerationFixtures.Layout) continue;
                var layout = (GdDict)GdJson.ParseString(artifact.GetString("text"));
                if (mismatch == "grid") layout["cell_size"] = 8.0;
                else
                {
                    GdDict room = (GdDict)layout.GetArrayOrEmpty("rooms")[0];
                    ((GdDict)room.GetArrayOrEmpty("structural_placements")[0])["module"] = "missing_named_module";
                }
                artifact["text"] = GdJson.Stringify(layout);
            }
            GdDict result = GenerationFixtures.Coordinator(storage).Commit(request, GenerationFixtures.Run, "slot_01");
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual("invalid_reference", result.GetString("reason"));
            Assert.IsFalse(result.GetBool("committed"));
            Assert.AreEqual(0, storage.Writes);
        }

        [TestCase("commit")]
        [TestCase("recover")]
        public void OtherOwnerPreviousPointerRetainsSharedSlotOwnershipWhenActiveIsAbsent(string operation)
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.Request();
            if (operation == "recover")
            {
                GenerationFixtures.Commit(storage, request);
                storage.Delete(GenerationFixtures.Active("slot_01"));
            }
            var otherStorage = new MemoryStorage();
            GdDict other = GenerationFixtures.Request(run: "previous-owner-run");
            GenerationFixtures.Commit(otherStorage, other);
            GenerationFixtures.CopyTree(otherStorage, storage, GenerationFixtures.Generation(other));
            string previous = otherStorage.ReadText(GenerationFixtures.Active("slot_01"));
            storage.WriteText(GenerationFixtures.Previous("slot_01"), previous);
            SaveCommitCoordinator coordinator = GenerationFixtures.Coordinator(storage);
            GdDict result = operation == "commit"
                ? coordinator.Commit(request, GenerationFixtures.Run, "slot_01")
                : coordinator.Recover(GenerationFixtures.Run, "slot_01");
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual("slot_owner_conflict", result.GetString("reason"));
            Assert.IsNull(result.Get("payloads"));
            Assert.IsFalse(storage.FileExists(GenerationFixtures.Active("slot_01")));
            Assert.AreEqual(previous, storage.ReadText(GenerationFixtures.Previous("slot_01")));
        }

        [Test]
        public void CompactGenerationPathsFitOriginalNativeRootIncludingPublicationTemporaryNames()
        {
            string physicalRoot = "C:\\" + new string('p', 142);
            Assert.AreEqual(145, physicalRoot.Length);
            var storage = new GenerationStorage(new MemoryStorage())
            {
                GlobalizeOverride = path => physicalRoot + "\\" + ResPath.StripUser(path).Replace('/', '\\')
            };
            GenerationFixtures.Commit(storage, GenerationFixtures.Request());
            foreach (string path in storage.WrittenPaths.Concat(new[] { GenerationFixtures.Previous("slot_01"), GenerationFixtures.Tombstone() }).Distinct())
            {
                string full = storage.Globalize(path);
                Assert.LessOrEqual(full.Length + ".tmp".Length, 259, path);
                Assert.LessOrEqual(full.Length + ".replace.bak".Length, 259, path);
                Assert.LessOrEqual(Path.GetDirectoryName(full).Length, 247, path);
            }
            foreach (string path in storage.DirectoryPaths) Assert.LessOrEqual(storage.Globalize(path).Length, 247, path);
        }

        [TestCase("ascii")]
        [TestCase("utf8")]
        public void CompactGenerationTupleUsesUnambiguousUtf8ByteLengthFraming(string form)
        {
            string prefix = form == "utf8" ? "雪" : "a";
            GdDict first = GenerationFixtures.Request(run: prefix, generation: "slot_01bc");
            GdDict second = GenerationFixtures.Request(run: prefix + "slot_01", generation: "bc");
            Assert.AreEqual(first.GetString("run_id") + "slot_01" + first.GetString("generation_id"), second.GetString("run_id") + "slot_01" + second.GetString("generation_id"), "an unframed concatenation would alias");
            Assert.AreNotEqual(GenerationFixtures.Generation(first), GenerationFixtures.Generation(second));
            string preimage = "save-generation-path-1:" + (form == "utf8" ? "3:" : "1:") + prefix + "7:slot_019:slot_01bc";
            Assert.AreEqual(GenerationFixtures.Root + "/g/" + GenerationFixtures.Hash(preimage), GenerationFixtures.Generation(first));
            foreach (GdDict request in new[] { first, second })
            {
                var storage = new GenerationStorage(new MemoryStorage());
                GenerationFixtures.Commit(storage, request);
                string actual = ResPath.GetBaseDir(storage.WrittenPaths.Last(path => path.EndsWith("/commit.json", StringComparison.Ordinal)));
                Assert.AreEqual(GenerationFixtures.Generation(request), actual);
            }
        }

        [Test]
        public void CompactGenerationArtifactOrdinalsFollowSortedLogicalPathsAndPreserveInputOrder()
        {
            GdDict request = GenerationFixtures.Request();
            request["artifacts"] = new GdArray(request.GetArrayOrEmpty("artifacts").Reverse());
            var storage = new GenerationStorage(new MemoryStorage());
            GenerationFixtures.Commit(storage, request);
            string path = storage.WrittenPaths.Last(p => p.EndsWith("/commit.json", StringComparison.Ordinal));
            GdDict manifest = (GdDict)GdJson.ParseString(storage.ReadText(path));
            var logical = request.GetArrayOrEmpty("artifacts").Cast<GdDict>().Select(a => a.GetString("logical_path")).OrderBy(p => p, StringComparer.Ordinal).ToList();
            foreach (GdDict entry in manifest.GetArrayOrEmpty("entries"))
                if (entry.GetString("role") == "artifact")
                {
                    string expected = logical.IndexOf(entry.GetString("logical_path")).ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ".json";
                    Assert.AreEqual(expected, entry.GetString("file"));
                    Assert.AreEqual(GenerationFixtures.Texts(request)[entry.GetString("logical_path")], storage.ReadText(ResPath.GetBaseDir(path) + "/" + expected));
                }
            GenerationFixtures.AssertBundle(request, GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01"));
            var returned = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01").GetDictOrEmpty("payloads");
            Assert.IsTrue(V.VariantEquals(request.Get("artifacts"), returned.Get("artifacts")), "ordinal storage must not reorder supplied artifact records");
        }

        [TestCase("traversal")]
        [TestCase("ordinal_reassignment")]
        [TestCase("numeric")]
        public void CompactGenerationRejectsEveryArtifactFilenameBeforeAnyPayloadRead(string shape)
        {
            var inner = new MemoryStorage();
            var storage = new GenerationStorage(inner);
            GenerationFixtures.Commit(storage, GenerationFixtures.Request());
            string manifestPath = storage.WrittenPaths.Last(p => p.EndsWith("/commit.json", StringComparison.Ordinal));
            string activePath = storage.WrittenPaths.Last(p => p.EndsWith("/active.json", StringComparison.Ordinal));
            GdDict manifest = (GdDict)GdJson.ParseString(inner.ReadText(manifestPath));
            GdDict entry = manifest.GetArrayOrEmpty("entries").Cast<GdDict>().Last(e => e.GetString("role") == "artifact");
            entry["file"] = shape == "numeric" ? (object)17L : shape == "traversal" ? "../run.json" : "00000000.json";
            string retained = GdJson.Stringify(manifest);
            inner.WriteText(manifestPath, retained);
            GdDict pointer = (GdDict)GdJson.ParseString(inner.ReadText(activePath));
            pointer["manifest_sha256"] = GenerationFixtures.Hash(retained);
            string pointerText = GdJson.Stringify(pointer);
            inner.WriteText(activePath, pointerText);
            storage.ReadPaths.Clear();
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.IsFalse(result.GetBool("ok"));
            Assert.IsNull(result.Get("payloads"));
            Assert.AreEqual("corrupt_generation", result.GetString("reason"));
            Assert.AreEqual(retained, inner.ReadText(manifestPath));
            Assert.AreEqual(pointerText, inner.ReadText(activePath));
            string directory = ResPath.GetBaseDir(manifestPath) + "/";
            Assert.IsFalse(storage.ReadPaths.Any(p => p.StartsWith(directory, StringComparison.Ordinal) && p != manifestPath), "validate the complete ordinal map before reading even an earlier valid payload");
        }

        [TestCase("run")]
        [TestCase("slot")]
        public void CompactGenerationDiscoverySkipsOnlyBoundCurrentForeignOwners(string scope)
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict request = GenerationFixtures.Request();
            CompactStageObserved(storage, request);
            var foreign = new GenerationStorage(new MemoryStorage());
            GdDict other = scope == "run" ? GenerationFixtures.Request(run: "foreign-run", generation: "foreign-generation") : GenerationFixtures.Request("slot_02", generation: "foreign-generation");
            string foreignManifest = CompactStageObserved(foreign, other);
            CompactCopyTree(foreign, storage, ResPath.GetBaseDir(foreignManifest), GenerationFixtures.Generation(other));
            string retained = storage.ReadText(GenerationFixtures.Generation(other) + "/commit.json");
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            GenerationFixtures.AssertBundle(request, result);
            Assert.AreEqual("lineage_tip", result.GetString("reason"));
            Assert.AreEqual(retained, storage.ReadText(GenerationFixtures.Generation(other) + "/commit.json"));
            Assert.IsFalse(result.GetArrayOrEmpty("candidates").Cast<GdDict>().Any(c => c.GetString("generation_id") == "foreign-generation"));
        }

        [TestCase("future")]
        [TestCase("malformed")]
        [TestCase("wrong_token")]
        [TestCase("numeric_owner")]
        [TestCase("wrong_kind")]
        [TestCase("numeric_generation")]
        [TestCase("unknown_status")]
        public void CompactGenerationUnprovenForeignHeaderRemainsAnAmbiguityBlocker(string shape)
        {
            var storage = new GenerationStorage(new MemoryStorage());
            CompactStageObserved(storage, GenerationFixtures.Request());
            var foreign = new GenerationStorage(new MemoryStorage());
            GdDict other = GenerationFixtures.Request("slot_02", run: "foreign-run", generation: "foreign-generation");
            string sourceManifest = CompactStageObserved(foreign, other);
            GdDict manifest = (GdDict)GdJson.ParseString(foreign.ReadText(sourceManifest));
            if (shape == "future") manifest["schema_version"] = "save-commit-99";
            if (shape == "numeric_owner") manifest["run_id"] = 17L;
            if (shape == "wrong_kind") manifest["slot_kind"] = "auto";
            if (shape == "numeric_generation") manifest["generation_id"] = 17L;
            if (shape == "unknown_status") manifest["status"] = "unknown";
            string path = (shape == "wrong_token" ? GenerationFixtures.Root + "/g/" + GenerationFixtures.Hash("wrong-token") : GenerationFixtures.Generation(other)) + "/commit.json";
            string retained = shape == "malformed" ? "{retained-unproven-foreign-header" : GdJson.Stringify(manifest);
            storage.WriteText(path, retained);
            int writes = storage.Writes;
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("ambiguous", result.GetString("reason"));
            Assert.IsNull(result.Get("payloads"));
            Assert.Greater(result.GetArrayOrEmpty("candidates").Count, 0);
            Assert.AreEqual(writes, storage.Writes);
            Assert.IsFalse(storage.FileExists(GenerationFixtures.Active("slot_01")));
            Assert.AreEqual(retained, storage.ReadText(path));
        }

        [TestCase("recover")]
        [TestCase("query")]
        public void CompactGenerationReadOnlyOperationsRefuseAnUnsupportedPhysicalPathBudget(string operation)
        {
            var inner = new MemoryStorage();
            var storage = new GenerationStorage(inner);
            GenerationFixtures.Commit(storage, GenerationFixtures.Request());
            string activePath = storage.WrittenPaths.Last(p => p.EndsWith("/active.json", StringComparison.Ordinal));
            string pointer = inner.ReadText(activePath);
            int writes = storage.Writes;
            storage.GlobalizeOverride = path => "C:\\" + new string('p', 200) + "\\" + ResPath.StripUser(path).Replace('/', '\\');
            var coordinator = GenerationFixtures.Coordinator(storage);
            GdDict result = operation == "recover" ? coordinator.Recover(GenerationFixtures.Run, "slot_01") : coordinator.QueryTerminal(GenerationFixtures.Run);
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual("path_budget_exceeded", result.GetString("reason"));
            Assert.IsNull(result.Get("payloads"));
            Assert.AreEqual(writes, storage.Writes);
            Assert.AreEqual(pointer, inner.ReadText(activePath));
            if (operation == "query") Assert.AreEqual("unknown", result.GetString("status"));
        }

        static string CompactStageObserved(GenerationStorage storage, GdDict request)
        {
            bool observed = false;
            GdDict result = GenerationFixtures.Coordinator(storage, fault: stage => { if (stage == "before_pointer") { observed = true; throw new IOException("complete staged fixture"); } })
                .Commit(request, request.GetString("run_id"), request.GetString("slot_id"));
            Assert.IsTrue(observed);
            Assert.IsFalse(result.GetBool("committed"));
            string path = storage.WrittenPaths.Last(p => p.EndsWith("/commit.json", StringComparison.Ordinal));
            Assert.IsNotNull(storage.ReadText(path));
            return path;
        }

        static void CompactCopyTree(IStorage source, IStorage target, string from, string to)
        {
            foreach (string name in source.ListFiles(from)) target.WriteText(to + "/" + name, source.ReadText(from + "/" + name));
            foreach (string name in source.ListDirectories(from)) CompactCopyTree(source, target, from + "/" + name, to + "/" + name);
        }
    }

    internal sealed class GenerationAuthority : ISaveGenerationTerminalAuthority
    {
        public string Status = "live";
        public bool Throws, Error, WrongIdentity;
        public GdArray Witnesses = new GdArray();
        public Func<string, string, GdDict> Handler;
        public GdDict Query(string runId, string slotId)
        {
            if (Throws) throw new IOException("authority unavailable");
            if (Handler != null) return Handler(runId, slotId);
            return new GdDict { { "ok", !Error }, { "status", Status }, { "run_id", WrongIdentity ? "wrong-run" : runId }, { "slot_id", slotId }, { "legacy_witnesses", Witnesses.DeepCopy() } };
        }
    }

    internal sealed class GenerationStorage : IStorage
    {
        readonly IStorage _inner;
        public int Calls, Writes;
        public Action<string, string> BeforeWrite, AfterWrite, AfterRead;
        public Func<string, string, string> Rewrite;
        public Func<string, bool?> DeleteResult;
        public Func<string, string> GlobalizeOverride;
        public readonly HashSet<string> Unreadable = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> WrittenPaths = new List<string>(), ReadPaths = new List<string>(), DirectoryPaths = new List<string>();
        public GenerationStorage(IStorage inner) { _inner = inner; }
        public bool FileExists(string path) { Calls++; return _inner.FileExists(path); }
        public bool DirExists(string path) { Calls++; return _inner.DirExists(path); }
        public string ReadText(string path) { Calls++; ReadPaths.Add(path); if (Unreadable.Contains(path)) throw new IOException("read denied"); string text = _inner.ReadText(path); AfterRead?.Invoke(path, text); return text; }
        public void WriteText(string path, string text) { Calls++; Writes++; WrittenPaths.Add(path); BeforeWrite?.Invoke(path, text); _inner.WriteText(path, Rewrite == null ? text : Rewrite(path, text)); AfterWrite?.Invoke(path, text); }
        public bool Delete(string path) { Calls++; return DeleteResult?.Invoke(path) ?? _inner.Delete(path); }
        public bool Rename(string from, string to) { Calls++; return _inner.Rename(from, to); }
        public void MakeDirRecursive(string path) { Calls++; DirectoryPaths.Add(path); _inner.MakeDirRecursive(path); }
        public IReadOnlyList<string> ListFiles(string dir) { Calls++; return _inner.ListFiles(dir); }
        public IReadOnlyList<string> ListDirectories(string dir) { Calls++; return _inner.ListDirectories(dir); }
        public bool DeleteDirectory(string path) { Calls++; return _inner.DeleteDirectory(path); }
        public string Globalize(string path) { Calls++; return GlobalizeOverride == null ? _inner.Globalize(path) : GlobalizeOverride(path); }
    }

    internal static class GenerationFixtures
    {
        public const string Root = "user://saves/.generations";
        public const string Run = "generation-fixture";
        public const string Engine = "4.7.test";
        public const string Layout = "user://runs/generation-fixture/layout.json";
        public const string Slice = "user://runs/generation-fixture/gameplay_slice.json";
        public const string Kit = "res://data/kits/ship_structural_v0.json";
        public const string AwayLayout = "user://runs/generation-fixture/away/layout.json";
        public const string AwaySlice = "user://runs/generation-fixture/away/gameplay_slice.json";

        public static GdDict Compatibility() => new GdDict
        {
            { "engine_version", Engine }, { "catalog_id", "ship_structural_v0" }, { "catalog_version", "1.0.0" },
            { "library_id", "" }, { "library_version", "" },
            { "profiles", new GdDict { { ConstrainedExpedition.Profile, ConstrainedExpedition.Profile }, { ConstrainedExpedition.LegacyProfile, ConstrainedExpedition.LegacyProfile } } }
        };

        public static SaveCommitCoordinator Coordinator(IStorage storage, GenerationAuthority authority = null, Action<string> fault = null)
            => new SaveCommitCoordinator(storage, Root, authority ?? new GenerationAuthority(), Compatibility(), fault);

        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(new UTF8Encoding(false, true).GetBytes(text)).Select(b => b.ToString("x2")));
        }

        static string Frame(string value) => new UTF8Encoding(false, true).GetByteCount(value).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
        public static string TupleToken(string run, string slot, string generation) => Hash("save-generation-path-1:" + Frame(run) + Frame(slot) + Frame(generation));
        public static string Generation(GdDict request) => Root + "/g/" + TupleToken(request.GetString("run_id"), request.GetString("slot_id"), request.GetString("generation_id"));
        public static string Active(string slot) => Root + "/s/" + Hash(slot) + "/active.json";
        public static string Previous(string slot) => Root + "/s/" + Hash(slot) + "/previous.json";
        public static string Tombstone(string run = Run) => Root + "/r/" + Hash(run) + "/terminal.json";
        public static string PayloadPath(GdDict request, string role)
        {
            if (role == "run" || role == "world") return Generation(request) + "/" + role + ".json";
            var paths = request.GetArrayOrEmpty("artifacts").Cast<GdDict>().Select(a => a.GetString("logical_path")).OrderBy(p => p, StringComparer.Ordinal).ToList();
            int ordinal = paths.IndexOf(role);
            if (ordinal < 0) throw new ArgumentException("unknown supplied artifact", nameof(role));
            return Generation(request) + "/" + ordinal.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ".json";
        }

        public static GdDict Request(string slot = "slot_01", string kind = "manual", string run = Run, string generation = "generation-one", bool away = false)
        {
            var snapshot = new RunSnapshot
            {
                LayoutPath = away ? AwayLayout : Layout, GameplaySlicePath = away ? AwaySlice : Slice, KitPath = Kit,
                RunId = run, SlotId = slot, SlotKind = kind, IsAutosave = kind == "auto", IsQuicksave = kind == "quick",
                SliceVersion = "gate2-current-run-6", GodotVersion = Engine, WorldSeed = 17L,
                PlayerPosition = GdArray.Of(1.0, 2.0, 3.0), VitalsSummary = new GdDict { { "health", 80.0 } },
                SavedAt = "2026-10-02T12:00:00Z", SavedAtEpoch = 1790942400L
            };
            GdDict home = snapshot.ToDict();
            home["layout_path"] = Layout; home["gameplay_slice_path"] = Slice;
            home["run_id"] = ""; home["slot_id"] = ""; home["slot_kind"] = "";
            home["is_autosave"] = false; home["is_quicksave"] = false;
            home["player_position"] = away ? GdArray.Of(4.0, 5.0, 6.0) : snapshot.PlayerPosition.DeepCopy();
            var world = new WorldSnapshot
            {
                HomeShip = home, WorldSummary = new GdDict { { "world_seed", 17L }, { "player_position", GdArray.Of(0.0, 0.0, 0.0) }, { "generated_marker_ids", new GdArray() } },
                RunId = run, SliceVersion = "world-4", GodotVersion = Engine, SavedAt = "2026-10-02T12:00:00Z",
                PlayerPositionInShip = snapshot.PlayerPosition.DeepCopy(), WorldTime = 42.25
            };
            var references = new GdDict { { "ship_start", Reference(Layout, Slice, "") }, { "lifeboat", new GdDict { { "present", false } } } };
            var revisions = new GdDict { { "ship_start", 1L }, { "lifeboat", 0L } };
            var artifacts = GdArray.Of(
                Artifact(Layout, "ship_layout", "1.2.0", LayoutText("")),
                Artifact(Slice, "ship_gameplay_slice", "1.1.0", SliceText()),
                Artifact(Kit, "ship_structural_catalog", "1.0.0", File.ReadAllText(Path.Combine(Fixtures.StreamingDataRoot, "data", "kits", "ship_structural_v0.json"), new UTF8Encoding(false, true))));
            if (away)
            {
                var blueprint = new ShipBlueprint(ShipBlueprint.Size.Small, ShipBlueprint.Condition.Damaged, 23L) { GenerationProfile = ConstrainedExpedition.Profile };
                GdDict ship = ShipInstance.Create("ship-away", "marker-away", blueprint, null, null).GetSummary();
                world.VisitedShips["marker-away"] = ship;
                snapshot.VisitedShips = world.VisitedShips.DeepCopy();
                world.CurrentLocation = snapshot.CurrentLocation = "marker-away";
                references["ship-away"] = Reference(AwayLayout, AwaySlice, ConstrainedExpedition.Profile);
                revisions["ship-away"] = 1L;
                artifacts.Add(Artifact(AwayLayout, "ship_layout", "1.2.0", LayoutText(ConstrainedExpedition.Profile)));
                artifacts.Add(Artifact(AwaySlice, "ship_gameplay_slice", "1.1.0", SliceText()));
            }
            return new GdDict
            {
                { "schema_version", SaveCommitCoordinator.PayloadVersion }, { "generation_id", generation },
                { "parent_generation_id", "" }, { "expected_pointer_sha256", "" }, { "run_id", run }, { "slot_id", slot }, { "slot_kind", kind },
                { "domain_revision", 1L }, { "compatibility", Compatibility() },
                { "binding", new GdDict { { "home_ship_id", "ship_start" }, { "lifeboat_ship_id", "lifeboat" }, { "current_owner_id", away ? "ship-away" : "ship_start" },
                    { "current_location", away ? "marker-away" : "" }, { "player_local_pose", snapshot.PlayerPosition.DeepCopy() }, { "owner_revisions", revisions }, { "ship_references", references } } },
                { "run_text", "\n" + GdJson.Stringify(snapshot.ToDict(), "  ") + "\r\n" },
                { "world_text", GdJson.Stringify(world.ToDict(), "  ") + "\n" }, { "artifacts", artifacts }
            };
        }

        static GdDict Reference(string layout, string slice, string profile) => new GdDict { { "present", true }, { "layout_path", layout }, { "gameplay_slice_path", slice }, { "kit_path", Kit }, { "profile_id", profile } };
        static GdDict Artifact(string path, string kind, string version, string text) => new GdDict { { "logical_path", path }, { "document_kind", kind }, { "schema_version", version }, { "text", text } };
        static string LayoutText(string profile)
        {
            var layout = new GdDict { { "document_kind", "ship_layout" }, { "schema_version", "1.2.0" }, { "cell_size", 4.0 }, { "kit_id", "ship_structural_v0" },
                { "rooms", GdArray.Of(new GdDict { { "id", "home" }, { "role", "cargo" } }) }, { "portals", new GdArray() } };
            if (profile.Length > 0) { layout["generation_profile"] = profile; layout["generation_seed"] = 23L; }
            return GdJson.Stringify(layout, "  ") + "\n";
        }
        static string SliceText() => GdJson.Stringify(new GdDict { { "document_kind", "ship_gameplay_slice" }, { "schema_version", "1.1.0" }, { "start_room", "home" }, { "goal_room", "home" }, { "rooms", new GdArray() } }) + "\r\n";

        public static GdDict WithLifeboat(GdDict request)
        {
            const string layoutPath = "user://runs/generation-fixture/lifeboat/layout.json";
            const string slicePath = "user://runs/generation-fixture/lifeboat/gameplay_slice.json";
            var blueprint = new ShipBlueprint(ShipBlueprint.Size.LifeBoat, ShipBlueprint.Condition.Pristine, 29L);
            GdDict lifeboat = ShipInstance.Create("lifeboat", "", blueprint, null, null).GetSummary();
            lifeboat["mobility"] = Mobility("lifeboat");
            ChangeText(request, "world_text", world => world["mobile_home_state"] = new GdDict
            {
                { "version", 1L }, { "lifeboat_commissioned", true }, { "lifeboat", lifeboat }, { "home_mobility", Mobility("ship_start") }
            });
            GdDict layout = (GdDict)GdJson.ParseString(LayoutText(""));
            layout["generation_seed"] = 29L;
            request.GetArrayOrEmpty("artifacts").Add(Artifact(layoutPath, "ship_layout", "1.2.0", GdJson.Stringify(layout)));
            request.GetArrayOrEmpty("artifacts").Add(Artifact(slicePath, "ship_gameplay_slice", "1.1.0", SliceText()));
            request.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references")["lifeboat"] = Reference(layoutPath, slicePath, "");
            request.GetDictOrEmpty("binding").GetDictOrEmpty("owner_revisions")["lifeboat"] = 1L;
            return request;
        }
        static GdDict Mobility(string ship) => new GdDict
        {
            { "version", 1L }, { "area_m2", 16.0 }, { "dry_mass_kg", 1600.0 }, { "rated_supported_kg", 2000.0 }, { "engine_id", "propulsion:" + ship }
        };

        public static GdDict WithCoherentDocuments(GdDict request)
        {
            string directory = Path.Combine(Fixtures.StreamingDataRoot, "data", "procgen", "golden", "coherent_ship_001");
            foreach (GdDict artifact in request.GetArrayOrEmpty("artifacts"))
            {
                string path = artifact.GetString("logical_path");
                if (path == Layout || path == Slice)
                    artifact["text"] = File.ReadAllText(Path.Combine(directory, path == Layout ? "layout.json" : "gameplay_slice.json"), new UTF8Encoding(false, true));
            }
            return request;
        }

        public static GdDict Child(IStorage storage, GdDict parent, string generation = "generation-two", long revision = 2L, bool away = false)
        {
            GdDict child = Request(parent.GetString("slot_id"), parent.GetString("slot_kind"), parent.GetString("run_id"), generation, away);
            child["parent_generation_id"] = parent.GetString("generation_id");
            child["expected_pointer_sha256"] = Hash(storage.ReadText(Active(parent.GetString("slot_id"))));
            child["domain_revision"] = revision;
            foreach (object key in child.GetDictOrEmpty("binding").GetDictOrEmpty("owner_revisions").Keys.ToList()) child.GetDictOrEmpty("binding").GetDictOrEmpty("owner_revisions")[key] = revision;
            // Controller-authorized oracle correction after observed RED: every child has distinguishable
            // valid run/world state and exact layout/slice bytes, so whole-bundle hashes detect mixed captures.
            ChangeText(child, "run_text", d => d["play_time_seconds"] = 100.0 + revision);
            ChangeText(child, "world_text", d =>
            {
                d["world_time"] = 42.25 + revision;
                d.GetDictOrEmpty("home_ship")["play_time_seconds"] = 100.0 + revision;
            });
            foreach (GdDict artifact in child.GetArrayOrEmpty("artifacts"))
                if (artifact.GetString("document_kind") != "ship_structural_catalog")
                    artifact["text"] = artifact.GetString("text") + "\n" + new string(' ', (int)(revision % 19L) + 1) + "\n";
            return child;
        }

        public static void Commit(IStorage storage, GdDict request, Action<string> fault = null)
        {
            GdDict result = Coordinator(storage, fault: fault).Commit(request, request.GetString("run_id"), request.GetString("slot_id"));
            Assert.IsTrue(result.GetBool("ok"), GdJson.Stringify(result));
            Assert.IsTrue(result.GetBool("committed"));
        }

        public static void Stage(IStorage storage, GdDict request)
        {
            bool reached = false;
            GdDict result = Coordinator(storage, fault: s => { if (s == "before_pointer") { reached = true; throw new IOException("leave complete orphan"); } })
                .Commit(request, request.GetString("run_id"), request.GetString("slot_id"));
            Assert.IsTrue(reached); Assert.IsFalse(result.GetBool("committed"));
            Assert.IsTrue(storage.FileExists(Generation(request) + "/commit.json"));
        }

        public static SortedDictionary<string, string> Texts(GdDict request)
        {
            var texts = new SortedDictionary<string, string>(StringComparer.Ordinal) { { "run", request.GetString("run_text") }, { "world", request.GetString("world_text") } };
            foreach (GdDict artifact in request.GetArrayOrEmpty("artifacts")) texts.Add(artifact.GetString("logical_path"), artifact.GetString("text"));
            return texts;
        }
        public static GdDict Hashes(GdDict request)
        {
            var hashes = new GdDict();
            foreach (var text in Texts(request)) hashes[text.Key] = new GdDict { { "sha256", Hash(text.Value) }, { "byte_length", new UTF8Encoding(false, true).GetByteCount(text.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            return hashes;
        }
        public static void AssertBundle(GdDict expected, GdDict result)
        {
            Assert.IsTrue(result.GetBool("ok"), GdJson.Stringify(result));
            Assert.IsTrue(result.GetBool("committed"));
            GdDict actual = result.Get("payloads") as GdDict;
            Assert.IsNotNull(actual);
            Assert.AreEqual(expected.GetString("generation_id"), result.GetString("generation_id"));
            Assert.IsTrue(V.VariantEquals(Hashes(expected), Hashes(actual)), "all original text hashes and UTF8 lengths must match");
            Assert.IsTrue(V.VariantEquals(expected.Get("binding"), actual.Get("binding")));
            Assert.IsTrue(V.VariantEquals(expected.Get("compatibility"), actual.Get("compatibility")));
            foreach (var text in Texts(expected)) Assert.AreEqual(text.Value, Texts(actual)[text.Key]);
        }

        public static void CopyTree(IStorage from, IStorage to, string directory)
        {
            foreach (string file in from.ListFiles(directory)) to.WriteText(directory + "/" + file, from.ReadText(directory + "/" + file));
            foreach (string child in from.ListDirectories(directory)) CopyTree(from, to, directory + "/" + child);
        }

        public static string NativeDirectory()
        {
            string parent = Environment.GetEnvironmentVariable("SYNAPTIC_CATALOG_EVIDENCE_DIR");
            if (string.IsNullOrEmpty(parent)) parent = Path.Combine(Path.GetTempPath(), "synaptic-generation-store-evidence");
            parent = Path.GetFullPath(parent);
            Directory.CreateDirectory(parent);
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("evidence parent is a reparse point");
            string child = Path.GetFullPath(Path.Combine(parent, "generation-native-" + Guid.NewGuid().ToString("N")));
            if (!child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("invalid evidence child");
            Directory.CreateDirectory(child);
            return child;
        }
        public static void NativeEvidence(string directory, GdDict expected, GdDict result, GdDict recovered)
        {
            var evidence = new GdDict { { "test", TestContext.CurrentContext.Test.Name }, { "directory", directory }, { "expected_hashes", Hashes(expected) }, { "result", result }, { "recovered", recovered } };
            if (recovered != null && recovered.Get("payloads") is GdDict payloads) evidence["recovered_hashes"] = Hashes(payloads);
            File.WriteAllText(Path.Combine(directory, "native-evidence.json"), GdJson.Stringify(evidence, "  "), new UTF8Encoding(false, true));
        }

        static void ChangeText(GdDict request, string field, Action<GdDict> mutate)
        {
            var dict = (GdDict)GdJson.ParseString(request.GetString(field)); mutate(dict); request[field] = GdJson.Stringify(dict);
        }
        static void ChangeArtifact(GdDict request, string path, Action<GdDict> mutate)
        {
            foreach (GdDict artifact in request.GetArrayOrEmpty("artifacts")) if (artifact.GetString("logical_path") == path)
            { var dict = (GdDict)GdJson.ParseString(artifact.GetString("text")); mutate(dict); artifact["text"] = GdJson.Stringify(dict); return; }
        }
        public static void Mutate(GdDict request, string mode)
        {
            switch (mode)
            {
                case "wrong_run": ChangeText(request, "run_text", d => d["run_id"] = "wrong"); break;
                case "wrong_world_run": ChangeText(request, "world_text", d => d["run_id"] = "wrong"); break;
                case "wrong_home_run": ChangeText(request, "world_text", d => d.GetDictOrEmpty("home_ship")["run_id"] = "wrong"); break;
                case "wrong_slot": ChangeText(request, "run_text", d => d["slot_id"] = "slot_02"); break;
                case "wrong_kind": ChangeText(request, "run_text", d => d["slot_kind"] = "quick"); break;
                case "wrong_flag": ChangeText(request, "run_text", d => d["is_quicksave"] = true); break;
                case "future_run": ChangeText(request, "run_text", d => d["slice_version"] = "gate2-current-run-7"); break;
                case "older_run": ChangeText(request, "run_text", d => d["slice_version"] = "gate2-current-run-5"); break;
                case "future_world": ChangeText(request, "world_text", d => d["slice_version"] = "world-5"); break;
                case "missing_run_field": ChangeText(request, "run_text", d => d.Erase("inventory_summary")); break;
                case "missing_world_field": ChangeText(request, "world_text", d => d.Erase("home_ship_carts")); break;
                case "malformed_run": request["run_text"] = "{"; break;
                case "malformed_world": request["world_text"] = "[]"; break;
                case "wrong_engine": ChangeText(request, "run_text", d => d["godot_version"] = "4.99"); break;
                case "dead_health": ChangeText(request, "run_text", d => d.GetDictOrEmpty("vitals_summary")["health"] = 0.0); break;
                case "missing_layout": RemoveArtifact(request, Layout); break;
                case "missing_slice": RemoveArtifact(request, Slice); break;
                case "missing_kit": RemoveArtifact(request, Kit); break;
                case "future_kit": ChangeArtifact(request, Kit, d => d["schema_version"] = "99.0.0"); break;
                case "kit_missing_module": ChangeArtifact(request, Kit, d => d["default_role_module"] = "missing"); break;
                case "kit_duplicate_module": ChangeArtifact(request, Kit, d => d.GetArrayOrEmpty("modules").Add(d.GetArrayOrEmpty("modules")[0])); break;
                case "duplicate_artifact": request.GetArrayOrEmpty("artifacts").Add(((GdDict)request.GetArrayOrEmpty("artifacts")[0]).DeepCopy()); break;
                case "case_collision": var collision = ((GdDict)request.GetArrayOrEmpty("artifacts")[0]).DeepCopy(); collision["logical_path"] = Layout.ToUpperInvariant(); request.GetArrayOrEmpty("artifacts").Add(collision); break;
                case "traversal": ((GdDict)request.GetArrayOrEmpty("artifacts")[0])["logical_path"] = "user://runs/../escape.json"; break;
                case "rooted_reference": ((GdDict)request.GetArrayOrEmpty("artifacts")[0])["logical_path"] = "C:/outside.json"; break;
                case "invalid_layout": ChangeArtifact(request, Layout, d => d["rooms"] = new GdArray()); break;
                case "invalid_slice": ChangeArtifact(request, Slice, d => d["goal_room"] = "missing"); break;
                case "bad_dock_edge": ChangeText(request, "world_text", d => d["dock_edges"] = GdArray.Of("bad")); break;
                case "seed_mismatch": ChangeText(request, "world_text", d => d.GetDictOrEmpty("world_summary")["world_seed"] = 18L); break;
                case "unknown_location": ChangeText(request, "world_text", d => d["current_location"] = "unknown"); break;
                case "wrong_binding_owner": request.GetDictOrEmpty("binding")["current_owner_id"] = "unknown-owner"; break;
                case "wrong_binding_pose": request.GetDictOrEmpty("binding")["player_local_pose"] = GdArray.Of(0.0, 0.0, 0.0); break;
                case "missing_owner_revision": request.GetDictOrEmpty("binding")["owner_revisions"] = new GdDict(); break;
                case "double_revision": request["domain_revision"] = 2.0; break;
                case "negative_revision": request["domain_revision"] = -1L; break;
                case "incompatible_content": request.GetDictOrEmpty("compatibility")["catalog_version"] = "unknown"; break;
                case "unknown_profile": ChangeText(request, "world_text", d => d.GetDictOrEmpty("visited_ships").GetDictOrEmpty("marker-away").GetDictOrEmpty("blueprint")["generation_profile"] = "unknown-profile"); break;
                case "missing_away_layout": RemoveArtifact(request, AwayLayout); break;
                case "retained_mapping_mismatch": request.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty("ship-away")["profile_id"] = ConstrainedExpedition.LegacyProfile; break;
                case "retained_seed_mismatch": ChangeText(request, "world_text", d => d.GetDictOrEmpty("visited_ships").GetDictOrEmpty("marker-away").GetDictOrEmpty("blueprint")["seed_value"] = 24L); break;
                case "missing_lifeboat_mapping": request.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").Erase("lifeboat"); break;
                case "invalid_utf16": request["run_text"] = request.GetString("run_text") + "\ud800"; break;
                default: throw new ArgumentException(mode);
            }
        }
        static void RemoveArtifact(GdDict request, string path)
        {
            GdArray artifacts = request.GetArrayOrEmpty("artifacts");
            for (int i = artifacts.Count - 1; i >= 0; i--) if (((GdDict)artifacts[i]).GetString("logical_path") == path) artifacts.RemoveAt(i);
        }
    }
}
