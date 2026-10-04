using System;
using System.IO;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class SaveGenerationTerminalTests
    {
        static GdDict Terminal(string state = "terminal", long revision = 1L) => new GdDict
        {
            { "schema_version", SaveCommitCoordinator.TerminalVersion }, { "run_id", GenerationFixtures.Run },
            { "state", state }, { "reason", "death" }, { "cause", "hypoxia" }, { "epitaph", "Remember this crew" },
            { "terminal_revision", revision }, { "legacy_witnesses", new GdArray() }
        };

        [TestCase("unbound", "terminal_unbound")]
        [TestCase("error", "terminal_authority_error")]
        [TestCase("throws", "terminal_authority_error")]
        [TestCase("wrong_identity", "terminal_ambiguous")]
        [TestCase("ambiguous", "terminal_ambiguous")]
        [TestCase("frozen", "run_terminal")]
        [TestCase("terminal", "run_terminal")]
        public void AuthorityMustExplicitlyPermitBothCommitAndRecovery(string status, string reason)
        {
            var storage = new GenerationStorage(new MemoryStorage());
            var authority = new GenerationAuthority
            {
                Status = status, Error = status == "error", Throws = status == "throws", WrongIdentity = status == "wrong_identity"
            };
            SaveCommitCoordinator coordinator = status == "unbound"
                ? new SaveCommitCoordinator(storage, GenerationFixtures.Root, null, GenerationFixtures.Compatibility())
                : GenerationFixtures.Coordinator(storage, authority);
            GdDict commit = coordinator.Commit(GenerationFixtures.Request(), GenerationFixtures.Run, "slot_01");
            Assert.AreEqual(reason, commit.GetString("reason"));
            Assert.IsFalse(commit.GetBool("committed"));
            GdDict recovery = coordinator.Recover(GenerationFixtures.Run, "slot_01");
            Assert.AreEqual(reason, recovery.GetString("reason"));
            Assert.IsNull(recovery.Get("payloads"));
            Assert.AreEqual(0, storage.Writes);
        }

        [TestCase("frozen")]
        [TestCase("terminal")]
        public void DurableRunQualifiedTombstoneSurvivesRestartAndIndexLoss(string state)
        {
            string directory = GenerationFixtures.NativeDirectory();
            var storage = new FileSystemStorage(directory);
            GdDict request = GenerationFixtures.Request();
            GdDict terminalResult = null, recovered = null;
            try
            {
                GenerationFixtures.Commit(storage, request);
                terminalResult = GenerationFixtures.Coordinator(storage).RecordTerminal(Terminal(state), GenerationFixtures.Run);
                Assert.IsTrue(terminalResult.GetBool("ok"));
                Assert.IsTrue(terminalResult.GetBool("committed"));
                string retained = storage.ReadText(GenerationFixtures.Tombstone());
                storage.Delete(GenerationFixtures.Root + "/index.json");
                storage.Delete(GenerationFixtures.Active("slot_01"));
                var restarted = GenerationFixtures.Coordinator(new FileSystemStorage(directory));
                GdDict query = restarted.QueryTerminal(GenerationFixtures.Run);
                Assert.IsTrue(query.GetBool("ok"));
                Assert.AreEqual(state, query.GetString("status"));
                Assert.AreEqual("Remember this crew", query.GetDictOrEmpty("terminal").GetString("epitaph"));
                recovered = restarted.Recover(GenerationFixtures.Run, "slot_01");
                Assert.AreEqual("run_terminal", recovered.GetString("reason"));
                Assert.IsNull(recovered.Get("payloads"));
                Assert.AreEqual(retained, storage.ReadText(GenerationFixtures.Tombstone()));
                Assert.IsFalse(storage.FileExists(GenerationFixtures.Active("slot_01")));
                Assert.IsFalse(storage.FileExists(GenerationFixtures.Root + "/index.json"));
                Assert.AreEqual(request.GetString("world_text"), storage.ReadText(GenerationFixtures.Generation(request) + "/world.json"));
            }
            finally { GenerationFixtures.NativeEvidence(directory, request, terminalResult, recovered); }
        }

        [Test]
        public void TombstoneAbsentIsExplicitOwnedLiveQueryButNotExternalAuthorityDefault()
        {
            var storage = new MemoryStorage();
            var coordinator = new SaveCommitCoordinator(storage, GenerationFixtures.Root, null, GenerationFixtures.Compatibility());
            GdDict result = coordinator.QueryTerminal(GenerationFixtures.Run);
            Assert.IsTrue(result.GetBool("ok"));
            Assert.AreEqual("live", result.GetString("status"));
            Assert.AreEqual("terminal_unbound", coordinator.Recover(GenerationFixtures.Run, "slot_01").GetString("reason"));
        }

        [TestCase("malformed")]
        [TestCase("wrong_run")]
        [TestCase("future")]
        [TestCase("unreadable")]
        public void UncertainTombstoneCannotBecomeLiveOrPlayable(string shape)
        {
            var inner = new MemoryStorage();
            var storage = new GenerationStorage(inner);
            GdDict terminal = Terminal();
            terminal["terminal_revision"] = "1";
            if (shape == "wrong_run") terminal["run_id"] = "wrong";
            if (shape == "future") terminal["schema_version"] = "save-run-terminal-99";
            string original = shape == "malformed" ? "{retained-epitaph" : GdJson.Stringify(terminal);
            inner.WriteText(GenerationFixtures.Tombstone(), original);
            if (shape == "unreadable") storage.Unreadable.Add(GenerationFixtures.Tombstone());
            GdDict query = GenerationFixtures.Coordinator(storage).QueryTerminal(GenerationFixtures.Run);
            Assert.IsFalse(query.GetBool("ok"));
            Assert.AreNotEqual("live", query.GetString("status"));
            GdDict recovery = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.IsFalse(recovery.GetBool("ok"));
            Assert.IsNull(recovery.Get("payloads"));
            Assert.AreEqual(original, inner.ReadText(GenerationFixtures.Tombstone()));
            Assert.AreEqual(0, storage.Writes);
        }

        [TestCase("before_terminal", false)]
        [TestCase("after_terminal", true)]
        public void TerminalPublicationFaultReportsOnlyVerifiedDurableOutcome(string stage, bool durable)
        {
            var storage = new MemoryStorage();
            bool observed = false;
            GdDict result = GenerationFixtures.Coordinator(storage, fault: s => { if (s == stage) { observed = true; throw new IOException("terminal fault"); } })
                .RecordTerminal(Terminal(), GenerationFixtures.Run);
            Assert.IsTrue(observed);
            Assert.AreEqual(durable, result.GetBool("committed"));
            Assert.AreEqual(durable, storage.FileExists(GenerationFixtures.Tombstone()));
            GdDict query = GenerationFixtures.Coordinator(storage).QueryTerminal(GenerationFixtures.Run);
            Assert.AreEqual(durable ? "terminal" : "live", query.GetString("status"));
        }

        [Test]
        public void TombstoneCannotBeRewrittenOrClearedByLowerRevisionOrAnotherReason()
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict terminal = Terminal(revision: 2L);
            GdDict first = GenerationFixtures.Coordinator(storage).RecordTerminal(terminal, GenerationFixtures.Run);
            Assert.IsTrue(first.GetBool("committed"));
            string retained = storage.ReadText(GenerationFixtures.Tombstone());
            int writes = storage.Writes;
            Assert.IsTrue(GenerationFixtures.Coordinator(storage).RecordTerminal(terminal, GenerationFixtures.Run).GetBool("committed"));
            Assert.AreEqual(writes, storage.Writes);
            GdDict changed = Terminal(revision: 1L); changed["epitaph"] = "rewritten";
            Assert.IsFalse(GenerationFixtures.Coordinator(storage).RecordTerminal(changed, GenerationFixtures.Run).GetBool("ok"));
            Assert.AreEqual(retained, storage.ReadText(GenerationFixtures.Tombstone()));
        }

        [TestCase("malformed")]
        [TestCase("valid_epitaph")]
        public void RequestedLegacyDeathPresenceBlocksAndNeverClearsEvenWithLiveAuthority(string form)
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, request);
            string path = "user://saves/slot_01.death.json";
            string death = form == "malformed" ? "{epitaph-kept" : GdJson.Stringify(new GdDict { { "schema_version", "death-1" }, { "slot_id", "slot_01" }, { "epitaph", "Old crew" } });
            storage.WriteText(path, death);
            storage.Delete(GenerationFixtures.Active("slot_01"));
            GdDict recover = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("legacy_death", recover.GetString("reason"));
            Assert.IsNull(recover.Get("payloads"));
            GdDict commit = GenerationFixtures.Coordinator(storage).Commit(GenerationFixtures.Request(generation: "new-candidate"), GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("legacy_death", commit.GetString("reason"));
            Assert.AreEqual(death, storage.ReadText(path));
            Assert.IsFalse(storage.FileExists(GenerationFixtures.Active("slot_01")));
        }

        [TestCase("index_frozen")]
        [TestCase("canonical_death")]
        [TestCase("temporary_death")]
        [TestCase("ambiguous_index")]
        public void ExistingSameRunLegacyWitnessesBlockOtherSlotRecovery(string witness)
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, request);
            string death = "{remember-same-run";
            if (witness == "index_frozen")
            {
                var index = new SaveIndexState();
                index.Slots.Add(new SaveSlotState { SlotId = "slot_02", SlotKind = "manual", RunId = GenerationFixtures.Run, Frozen = true });
                storage.WriteText("user://saves/index.json", GdJson.Stringify(index.ToDict()));
            }
            if (witness == "canonical_death" || witness == "temporary_death")
            {
                storage.WriteText("user://saves/slot_02.json" + (witness == "temporary_death" ? ".tmp" : ""), GenerationFixtures.Request("slot_02").GetString("run_text"));
                storage.WriteText("user://saves/slot_02.death.json", death);
            }
            if (witness == "ambiguous_index") storage.WriteText("user://saves/index.json", "{unknown-associated-owner");
            string pointer = storage.ReadText(GenerationFixtures.Active("slot_01"));
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.IsFalse(result.GetBool("ok"));
            Assert.IsNull(result.Get("payloads"));
            Assert.AreNotEqual("not_implemented", result.GetString("reason"));
            Assert.AreEqual(pointer, storage.ReadText(GenerationFixtures.Active("slot_01")));
            if (witness == "canonical_death" || witness == "temporary_death") Assert.AreEqual(death, storage.ReadText("user://saves/slot_02.death.json"));
        }

        [Test]
        public void RunBecomesFrozenAfterManifestBeforePointerAndCannotPromoteStagedAncestor()
        {
            var storage = new MemoryStorage();
            var authority = new GenerationAuthority();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GdDict child = GenerationFixtures.Child(storage, old);
            bool observed = false;
            GdDict result = GenerationFixtures.Coordinator(storage, authority, s => { if (s == "after_manifest") { observed = true; authority.Status = "frozen"; } })
                .Commit(child, GenerationFixtures.Run, "slot_01");
            Assert.IsTrue(observed);
            Assert.AreEqual("run_terminal", result.GetString("reason"));
            Assert.IsFalse(result.GetBool("committed"));
            storage.Delete(GenerationFixtures.Active("slot_01"));
            GdDict recovered = GenerationFixtures.Coordinator(storage, authority).Recover(GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("run_terminal", recovered.GetString("reason"));
            Assert.IsNull(recovered.Get("payloads"));
            Assert.IsFalse(storage.FileExists(GenerationFixtures.Active("slot_01")));
        }

        [Test]
        public void IndexRebuildQueriesEachSelectedRunAndRetainsFrozenPresentation()
        {
            var storage = new MemoryStorage();
            GdDict first = GenerationFixtures.Request();
            GdDict second = GenerationFixtures.Request("slot_02", run: "second-live-run");
            GenerationFixtures.Commit(storage, first);
            GenerationFixtures.Commit(storage, second);
            storage.Delete(GenerationFixtures.Root + "/index.json");
            bool queriedFrozenRun = false;
            var authority = new GenerationAuthority();
            authority.Handler = (run, slot) =>
            {
                bool frozen = run == GenerationFixtures.Run;
                if (frozen) queriedFrozenRun = true;
                return new GdDict { { "ok", true }, { "run_id", run }, { "slot_id", slot }, { "status", frozen ? "frozen" : "live" }, { "legacy_witnesses", new GdArray() } };
            };
            GenerationFixtures.AssertBundle(second, GenerationFixtures.Coordinator(storage, authority).Recover("second-live-run", "slot_02"));
            Assert.IsTrue(queriedFrozenRun);
            var index = (GdDict)GdJson.ParseString(storage.ReadText(GenerationFixtures.Root + "/index.json"));
            GdDict frozenRow = null, liveRow = null;
            foreach (GdDict row in index.GetArrayOrEmpty("slots"))
            {
                if (row.GetString("run_id") == GenerationFixtures.Run) frozenRow = row;
                if (row.GetString("run_id") == "second-live-run") liveRow = row;
            }
            Assert.IsNotNull(frozenRow); Assert.IsTrue(frozenRow.GetBool("frozen"));
            Assert.IsNotNull(liveRow); Assert.IsFalse(liveRow.GetBool("frozen"));
            Assert.IsNull(GenerationFixtures.Coordinator(storage, authority).Recover(GenerationFixtures.Run, "slot_01").Get("payloads"));
        }

        [Test]
        public void AuthorityProvidedUnsafeWitnessCannotReadOutsideLegacyNamespace()
        {
            var storage = new GenerationStorage(new MemoryStorage());
            var authority = new GenerationAuthority { Witnesses = GdArray.Of(new GdDict { { "path", "user://../outside.death.json" }, { "run_id", GenerationFixtures.Run }, { "slot_id", "slot_01" } }) };
            GdDict result = GenerationFixtures.Coordinator(storage, authority).Recover(GenerationFixtures.Run, "slot_01");
            Assert.AreEqual("terminal_ambiguous", result.GetString("reason"));
            Assert.AreEqual(0, storage.Writes);
        }

        [TestCase("pointer", "frozen")]
        [TestCase("pointer", "terminal")]
        [TestCase("pointer", "tombstone")]
        [TestCase("previous", "frozen")]
        [TestCase("previous", "terminal")]
        [TestCase("previous", "tombstone")]
        public void FinalRecoveryTerminalGateCannotBeDowngradedToIndexMaintenance(string selection, string transition)
        {
            var storage = new MemoryStorage();
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            if (selection == "previous")
            {
                GenerationFixtures.Commit(storage, GenerationFixtures.Child(storage, old));
                storage.WriteText(GenerationFixtures.Active("slot_01"), "{retained-corrupt-active");
            }
            string pointer = storage.ReadText(GenerationFixtures.Active("slot_01"));
            string previous = storage.ReadText(GenerationFixtures.Previous("slot_01"));
            int calls = 0, finalGate = selection == "pointer" ? 2 : 3;
            var authority = new GenerationAuthority();
            authority.Handler = (run, slot) =>
            {
                calls++;
                bool reachedFinal = calls >= finalGate;
                if (reachedFinal && transition == "tombstone")
                {
                    GdDict terminal = Terminal();
                    terminal["terminal_revision"] = "1";
                    storage.WriteText(GenerationFixtures.Tombstone(), GdJson.Stringify(terminal));
                }
                return new GdDict
                {
                    { "ok", true }, { "run_id", run }, { "slot_id", slot },
                    { "status", reachedFinal && transition != "tombstone" ? transition : "live" },
                    { "legacy_witnesses", new GdArray() }
                };
            };
            GdDict result = GenerationFixtures.Coordinator(storage, authority).Recover(GenerationFixtures.Run, "slot_01");
            Assert.GreaterOrEqual(calls, finalGate, "the actual final gate must execute");
            Assert.IsFalse(result.GetBool("ok"), "terminal refusal is an unplayable recovery outcome");
            Assert.IsFalse(result.GetBool("committed"));
            Assert.IsNull(result.Get("payloads"));
            Assert.AreEqual("run_terminal", result.GetString("reason"));
            Assert.AreEqual(pointer, storage.ReadText(GenerationFixtures.Active("slot_01")));
            Assert.AreEqual(previous, storage.ReadText(GenerationFixtures.Previous("slot_01")));
            if (transition == "tombstone") Assert.IsTrue(storage.FileExists(GenerationFixtures.Tombstone()));
        }

        [Test]
        public void LoneOtherSlotDeathWithoutOwnershipBlocksPromotionAndPreservesEpitaph()
        {
            var storage = new MemoryStorage();
            GdDict request = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, request);
            storage.Delete(GenerationFixtures.Active("slot_01"));
            string path = "user://saves/slot_02.death.json";
            string death = GdJson.Stringify(new GdDict
            {
                { "schema_version", "death-1" }, { "slot_id", "slot_02" }, { "epitaph", "Remember lone crew" }
            });
            storage.WriteText(path, death);
            Assert.IsFalse(storage.FileExists("user://saves/index.json"));
            Assert.IsFalse(storage.FileExists("user://saves/slot_02.json"));
            Assert.IsFalse(storage.FileExists("user://saves/slot_02.json.tmp"));
            GdDict result = GenerationFixtures.Coordinator(storage).Recover(GenerationFixtures.Run, "slot_01");
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual("legacy_ownership_ambiguous", result.GetString("reason"));
            Assert.IsNull(result.Get("payloads"));
            Assert.IsFalse(storage.FileExists(GenerationFixtures.Active("slot_01")));
            Assert.AreEqual(death, storage.ReadText(path));
        }

        [Test]
        public void NumericAuthorityRunIdentityIsAmbiguousBeforePublication()
        {
            var storage = new GenerationStorage(new MemoryStorage());
            var authority = new GenerationAuthority();
            authority.Handler = (run, slot) => new GdDict
            {
                { "ok", true }, { "run_id", 17L }, { "slot_id", slot }, { "status", "live" }, { "legacy_witnesses", new GdArray() }
            };
            GdDict result = GenerationFixtures.Coordinator(storage, authority).Commit(GenerationFixtures.Request(run: "17"), "17", "slot_01");
            Assert.AreEqual("terminal_ambiguous", result.GetString("reason"));
            Assert.IsFalse(result.GetBool("committed"));
            Assert.AreEqual(0, storage.Writes);
        }

        [Test]
        public void NumericTerminalRunIdentityCannotBePublishedUnderItsCoercedString()
        {
            var storage = new GenerationStorage(new MemoryStorage());
            GdDict terminal = Terminal(); terminal["run_id"] = 17L;
            GdDict result = GenerationFixtures.Coordinator(storage).RecordTerminal(terminal, "17");
            Assert.AreEqual("invalid_request", result.GetString("reason"));
            Assert.IsFalse(result.GetBool("committed"));
            Assert.AreEqual(0, storage.Writes);
        }

        [TestCase("commit")]
        [TestCase("terminal")]
        public void CompactGenerationLongNativeRootUsesPlatformPublicationBudget(string operation)
        {
            string parent = GenerationFixtures.NativeDirectory();
            string directory = Path.GetFullPath(Path.Combine(parent, "budget-" + new string('p', Math.Max(36, 191 - parent.Length - 8))));
            Assert.IsTrue(directory.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(directory);
            Assert.IsFalse((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0);
            var storage = new GenerationStorage(new FileSystemStorage(directory));
            GdDict request = GenerationFixtures.Request(), result = null;
            Assert.Greater(storage.Globalize(GenerationFixtures.Generation(request) + "/commit.json").Length + 12, 259,
                "Fixture must exceed the Windows publication budget independent of the host temp root.");
            try
            {
                var coordinator = GenerationFixtures.Coordinator(storage);
                result = operation == "commit"
                    ? coordinator.Commit(request, GenerationFixtures.Run, "slot_01")
                    : coordinator.RecordTerminal(Terminal(), GenerationFixtures.Run);
                if (Path.DirectorySeparatorChar != '\\')
                {
                    Assert.IsTrue(result.GetBool("ok"), result.GetString("reason") + ":" + result.GetString("detail"));
                    Assert.IsTrue(result.GetBool("committed"));
                    Assert.Greater(storage.Writes, 0);
                    if (operation == "commit")
                        GenerationFixtures.AssertBundle(request, GenerationFixtures.Coordinator(new FileSystemStorage(directory)).Recover(GenerationFixtures.Run, "slot_01"));
                    else Assert.IsTrue(storage.FileExists(GenerationFixtures.Tombstone()));
                    return;
                }
                Assert.IsFalse(result.GetBool("ok"));
                Assert.AreEqual("path_budget_exceeded", result.GetString("reason"));
                Assert.IsFalse(result.GetBool("committed"));
                Assert.AreEqual("not_committed", result.GetString("outcome"));
                Assert.IsNull(result.Get("payloads"));
                Assert.AreEqual(0, storage.Writes, "path refusal must precede payload or tombstone publication");
                Assert.AreEqual(0, storage.DirectoryPaths.Count, "path refusal must precede owned directory creation");
                Assert.IsFalse(storage.FileExists(GenerationFixtures.Active("slot_01")));
                Assert.IsFalse(storage.FileExists(GenerationFixtures.Tombstone()));
            }
            finally { GenerationFixtures.NativeEvidence(directory, request, result, null); }
        }

        [TestCase("frozen")]
        [TestCase("terminal")]
        [TestCase("tombstone")]
        [TestCase("live")]
        public void AlreadyCommittedReplayRechecksTerminalAfterStoredPayloadRead(string transition)
        {
            var inner = new MemoryStorage();
            var storage = new GenerationStorage(inner);
            GdDict request = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, request);
            string pointer = inner.ReadText(GenerationFixtures.Active("slot_01"));
            string previous = inner.ReadText(GenerationFixtures.Previous("slot_01"));
            string manifest = inner.ReadText(GenerationFixtures.Generation(request) + "/commit.json");
            int writes = storage.Writes;
            var authority = new GenerationAuthority();
            bool observed = false;
            string retainedTombstone = null;
            storage.AfterRead = (path, text) =>
            {
                if (observed || path != GenerationFixtures.PayloadPath(request, "world")) return;
                observed = true;
                Assert.AreEqual(request.GetString("world_text"), text, "the already committed closure must be reread unchanged");
                if (transition == "tombstone")
                {
                    GdDict terminal = Terminal();
                    terminal["terminal_revision"] = "1";
                    retainedTombstone = GdJson.Stringify(terminal);
                    inner.WriteText(GenerationFixtures.Tombstone(), retainedTombstone);
                }
                else authority.Status = transition;
            };
            GdDict result = GenerationFixtures.Coordinator(storage, authority).Commit(request, GenerationFixtures.Run, "slot_01");
            storage.AfterRead = null;
            Assert.IsTrue(observed, "terminal transition must occur during actual committed payload reread");
            Assert.AreEqual(writes, storage.Writes, "exact committed replay must perform no coordinator writes");
            Assert.AreEqual(pointer, inner.ReadText(GenerationFixtures.Active("slot_01")));
            Assert.AreEqual(previous, inner.ReadText(GenerationFixtures.Previous("slot_01")));
            Assert.AreEqual(manifest, inner.ReadText(GenerationFixtures.Generation(request) + "/commit.json"));
            foreach (var text in GenerationFixtures.Texts(request))
                Assert.AreEqual(text.Value, inner.ReadText(GenerationFixtures.PayloadPath(request, text.Key)));
            Assert.IsTrue(result.GetBool("committed"), "replay retains the verified existing disk commit");
            Assert.AreEqual("committed", result.GetString("outcome"));
            Assert.AreEqual(request.GetString("generation_id"), result.GetString("generation_id"));
            if (transition == "tombstone") Assert.AreEqual(retainedTombstone, inner.ReadText(GenerationFixtures.Tombstone()));
            else Assert.IsFalse(inner.FileExists(GenerationFixtures.Tombstone()));

            if (transition == "live")
            {
                GenerationFixtures.AssertBundle(request, result);
                Assert.AreEqual("already_committed", result.GetString("reason"));
                Assert.IsFalse(result.GetBool("index_reconciliation_needed"));
            }
            else
            {
                Assert.IsFalse(result.GetBool("ok"), "idempotent return must not bypass the final terminal gate");
                Assert.IsNull(result.Get("payloads"));
                Assert.AreEqual("run_terminal", result.GetString("reason"));
            }
        }

        [TestCase("commit", "hook", "frozen")]
        [TestCase("commit", "hook", "terminal")]
        [TestCase("commit", "hook", "tombstone")]
        [TestCase("commit", "storage", "frozen")]
        [TestCase("commit", "storage", "terminal")]
        [TestCase("commit", "storage", "tombstone")]
        [TestCase("recover", "hook", "frozen")]
        [TestCase("recover", "hook", "terminal")]
        [TestCase("recover", "hook", "tombstone")]
        [TestCase("recover", "storage", "frozen")]
        [TestCase("recover", "storage", "terminal")]
        [TestCase("recover", "storage", "tombstone")]
        [TestCase("commit", "hook", "live")]
        [TestCase("commit", "storage", "live")]
        [TestCase("recover", "hook", "live")]
        [TestCase("recover", "storage", "live")]
        public void PublicationExceptionReopensCommittedPointerWithoutBypassingTerminalAuthority(string operation, string boundary, string transition)
        {
            var inner = new MemoryStorage();
            var storage = new GenerationStorage(inner);
            GdDict old = GenerationFixtures.Request();
            GenerationFixtures.Commit(storage, old);
            GdDict child = GenerationFixtures.Child(storage, old);
            if (operation == "recover")
            {
                GenerationFixtures.Stage(storage, child);
                storage.Delete(GenerationFixtures.Active("slot_01"));
            }
            var authority = new GenerationAuthority();
            bool observed = false;
            string publishedPointer = null, retainedTombstone = null;
            Action publishedThenThrow = () =>
            {
                observed = true;
                publishedPointer = inner.ReadText(GenerationFixtures.Active("slot_01"));
                if (transition == "tombstone")
                {
                    GdDict terminal = Terminal();
                    terminal["terminal_revision"] = "1";
                    retainedTombstone = GdJson.Stringify(terminal);
                    inner.WriteText(GenerationFixtures.Tombstone(), retainedTombstone);
                }
                else authority.Status = transition;
                throw new IOException("pointer published before terminal transition and ordinary exception");
            };
            if (boundary == "storage")
                storage.AfterWrite = (path, text) => { if (path == GenerationFixtures.Active("slot_01")) publishedThenThrow(); };
            Action<string> fault = boundary == "hook"
                ? (Action<string>)(stage => { if (stage == "after_pointer") publishedThenThrow(); })
                : null;
            SaveCommitCoordinator coordinator = GenerationFixtures.Coordinator(storage, authority, fault);
            GdDict result = operation == "commit"
                ? coordinator.Commit(child, GenerationFixtures.Run, "slot_01")
                : coordinator.Recover(GenerationFixtures.Run, "slot_01");
            storage.AfterWrite = null;

            Assert.IsTrue(observed, "the actual active pointer publication boundary must execute");
            Assert.IsNotNull(publishedPointer);
            Assert.AreEqual(publishedPointer, inner.ReadText(GenerationFixtures.Active("slot_01")), "exception reporting must retain the published pointer");
            GdDict pointer = (GdDict)GdJson.ParseString(publishedPointer);
            Assert.AreEqual(child.GetString("generation_id"), pointer.GetString("generation_id"));
            Assert.IsTrue(result.GetBool("committed"), "a verified published pointer remains a committed disk outcome");
            Assert.AreEqual("committed", result.GetString("outcome"));
            Assert.AreEqual(child.GetString("generation_id"), result.GetString("generation_id"));
            foreach (var text in GenerationFixtures.Texts(child))
                Assert.AreEqual(text.Value, inner.ReadText(GenerationFixtures.PayloadPath(child, text.Key)), "the entire published byte set remains intact");
            Assert.IsTrue(inner.FileExists(GenerationFixtures.Generation(child) + "/commit.json"));
            if (transition == "tombstone")
            {
                Assert.AreEqual(retainedTombstone, inner.ReadText(GenerationFixtures.Tombstone()));
                Assert.AreEqual("terminal", GenerationFixtures.Coordinator(storage).QueryTerminal(GenerationFixtures.Run).GetString("status"));
            }
            else Assert.IsFalse(inner.FileExists(GenerationFixtures.Tombstone()));

            if (transition == "live")
            {
                GenerationFixtures.AssertBundle(child, result);
                Assert.AreEqual("index_reconciliation_needed", result.GetString("reason"));
                Assert.IsTrue(result.GetBool("index_reconciliation_needed"));
                GenerationFixtures.AssertBundle(child, GenerationFixtures.Coordinator(storage, authority).Recover(GenerationFixtures.Run, "slot_01"));
            }
            else
            {
                Assert.IsFalse(result.GetBool("ok"), "ordinary publication exceptions must not bypass changed terminal authority");
                Assert.IsNull(result.Get("payloads"));
                Assert.AreEqual("run_terminal", result.GetString("reason"));
                GdDict restarted = GenerationFixtures.Coordinator(storage, authority).Recover(GenerationFixtures.Run, "slot_01");
                Assert.IsFalse(restarted.GetBool("ok"));
                Assert.IsNull(restarted.Get("payloads"));
                Assert.AreEqual("run_terminal", restarted.GetString("reason"));
                Assert.AreEqual(publishedPointer, inner.ReadText(GenerationFixtures.Active("slot_01")));
                if (transition == "tombstone") Assert.AreEqual(retainedTombstone, inner.ReadText(GenerationFixtures.Tombstone()));
            }
        }
    }
}
