using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class DomainTransactionTests
    {
        static GdDict Bundle() => ComponentDomainTestFixtures.Bundle();
        static GdDict Move(string id = "move-a") => ComponentDomainTestFixtures.Move(id);
        static void Same(object expected, object actual) => ComponentDomainTestFixtures.Same(expected, actual);
        static GdDict Instances(GdDict summary) => ComponentDomainTestFixtures.Instances(summary);
        static string Prepare(DomainTransactionCoordinator coordinator, GdDict command = null)
        {
            GdDict prepared = coordinator.Prepare(command ?? Move());
            Assert.IsTrue(prepared.GetBool("ok"), prepared.GetString("reason"));
            Assert.AreEqual("component_transfer:" + (command ?? Move()).GetString("command_id"), prepared.GetString("transaction_id"));
            return prepared.GetString("transaction_id");
        }

        [Test]
        public void Bundle_UsesSoleLocationAuthorityAndIndependentHealthProjections()
        {
            Assert.IsTrue(DomainBundle.TryCreate(Bundle(), out DomainBundle bundle, out string reason), reason);
            GdDict projections = bundle.GetProjections();
            Assert.AreEqual(1, projections.GetDictOrEmpty("holders").GetDictOrEmpty("slot-a").GetArrayOrEmpty("instance_ids").Count);
            Assert.AreEqual("a", projections.GetDictOrEmpty("holders").GetDictOrEmpty("slot-a").GetArrayOrEmpty("instance_ids")[0]);
            Assert.AreEqual(2.5, projections.GetDictOrEmpty("holders").GetDictOrEmpty("bag").GetFloat("mass"));
            Assert.IsTrue(projections.GetDictOrEmpty("machinery").GetDictOrEmpty("machine-a").GetBool("component_available"));
            Assert.AreEqual(0.23, projections.GetDictOrEmpty("machinery").GetDictOrEmpty("machine-a").GetFloat("effective_health"));
            Assert.IsFalse(projections.GetDictOrEmpty("machinery").GetDictOrEmpty("machine-b").GetBool("component_available"));
            Assert.AreEqual(0.0, projections.GetDictOrEmpty("machinery").GetDictOrEmpty("machine-b").GetFloat("effective_health"));
            Assert.AreEqual(0.8, bundle.GetSummary().GetDictOrEmpty("machinery").GetDictOrEmpty("machine-a").GetFloat("health"));
        }

        [Test]
        public void KnownComponentConditionCannotRaiseIndependentlyDamagedMachineryHealth()
        {
            GdDict initial = Bundle(); Instances(initial).GetDictOrEmpty("b")["holder"] = "slot-b";
            GdDict before = initial.DeepCopy();
            Assert.IsTrue(DomainBundle.TryCreate(initial, out DomainBundle bundle, out string reason), reason);
            GdDict projected = bundle.GetProjections().GetDictOrEmpty("machinery").GetDictOrEmpty("machine-b");
            Assert.IsTrue(projected.GetBool("component_available")); Assert.AreEqual(0.2, projected.GetFloat("effective_health"));
            Assert.AreEqual(0.2, bundle.GetSummary().GetDictOrEmpty("machinery").GetDictOrEmpty("machine-b").GetFloat("health"));
            Assert.AreEqual(0.81, Instances(bundle.GetSummary()).GetDictOrEmpty("b").GetFloat("condition"));
            Same(before, bundle.GetSummary()); Same(before, initial);
        }

        [Test]
        public void Bundle_DefensiveInputSummaryAndProjectionCopies()
        {
            GdDict initial = Bundle(), expected = initial.DeepCopy();
            Assert.IsTrue(DomainBundle.TryCreate(initial, out DomainBundle bundle, out _));
            Instances(initial).GetDictOrEmpty("a")["condition"] = 0.99;
            GdDict returned = bundle.GetSummary();
            returned.GetDictOrEmpty("machinery").GetDictOrEmpty("machine-a")["health"] = 1.0;
            Instances(returned).GetDictOrEmpty("a").GetDictOrEmpty("provenance").GetArrayOrEmpty("notes").Add("forged");
            GdDict projection = bundle.GetProjections(); projection["holders"] = new GdDict();
            Same(expected, bundle.GetSummary());
            Assert.IsNotEmpty(bundle.GetProjections().GetDictOrEmpty("holders"));
        }

        [TestCase("schema_version")]
        [TestCase("revision")]
        [TestCase("holder_revision")]
        [TestCase("capacity_count")]
        [TestCase("receipt_schema")]
        [TestCase("receipt_revision")]
        public void IntegralDoubleDomainIntegerFieldsAreRejected(string field)
        {
            GdDict initial = Bundle(); initial["revision"] = 1L;
            GdDict receipt = ComponentDomainTestFixtures.Receipt();
            initial.GetDictOrEmpty("receipts")[receipt.GetString("transaction_id")] = receipt;
            GdDict malformed = initial.DeepCopy();
            switch (field)
            {
                case "schema_version": malformed["schema_version"] = 1.0; break;
                case "revision": malformed["revision"] = 1.0; break;
                case "holder_revision": malformed.GetDictOrEmpty("holders").GetDictOrEmpty("bag")["revision"] = 0.0; break;
                case "capacity_count": malformed.GetDictOrEmpty("holders").GetDictOrEmpty("bag")["capacity_count"] = 4.0; break;
                case "receipt_schema": malformed.GetDictOrEmpty("receipts").GetDictOrEmpty(receipt.GetString("transaction_id"))["schema_version"] = 1.0; break;
                case "receipt_revision": malformed.GetDictOrEmpty("receipts").GetDictOrEmpty(receipt.GetString("transaction_id"))["revision"] = 1.0; break;
            }
            GdDict before = malformed.DeepCopy();
            Assert.IsFalse(DomainBundle.TryCreate(malformed, out DomainBundle rejected, out string reason));
            Assert.IsNull(rejected); Assert.IsNotEmpty(reason); Same(before, malformed);
            Assert.Throws<ArgumentException>(() => new DomainTransactionCoordinator(malformed));
            var coordinator = new DomainTransactionCoordinator(initial);
            string transaction = Prepare(coordinator, ComponentDomainTestFixtures.Move(domainRevision: 1));
            Assert.IsFalse(coordinator.ApplySummary(malformed)); Same(initial, coordinator.GetSummary());
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed"));
        }

        [TestCase("registry", "array")]
        [TestCase("registry", "dictionary")]
        [TestCase("origin", "array")]
        [TestCase("origin", "dictionary")]
        [TestCase("provenance", "array")]
        [TestCase("provenance", "dictionary")]
        [TestCase("holder", "array")]
        [TestCase("holder", "dictionary")]
        [TestCase("machinery", "array")]
        [TestCase("machinery", "dictionary")]
        [TestCase("receipt", "array")]
        [TestCase("receipt", "dictionary")]
        public void MutableDictionaryKeysRejectWholeDomainImportAndKeepPendingCandidate(string location, string keyKind)
        {
            GdDict initial = Bundle(); initial["revision"] = 1L;
            GdDict oldReceipt = ComponentDomainTestFixtures.Receipt();
            initial.GetDictOrEmpty("receipts")[oldReceipt.GetString("transaction_id")] = oldReceipt;
            var coordinator = new DomainTransactionCoordinator(initial);
            string transaction = Prepare(coordinator, ComponentDomainTestFixtures.Move(domainRevision: 1));
            GdDict unsafeSummary = initial.DeepCopy(); unsafeSummary["revision"] = 2L;
            object mutableKey = keyKind == "array" ? (object)GdArray.Of("caller-owned") : new GdDict { { "caller", "owned" } };
            GdDict metadata = new GdDict { { "nested", GdArray.Of(new GdDict { { mutableKey, "opaque" } }) } };
            switch (location)
            {
                case "registry": unsafeSummary.GetDictOrEmpty("registry")["metadata"] = metadata; break;
                case "origin": Instances(unsafeSummary).GetDictOrEmpty("a")["origin"] = metadata; break;
                case "provenance": Instances(unsafeSummary).GetDictOrEmpty("a")["provenance"] = metadata; break;
                case "holder": unsafeSummary.GetDictOrEmpty("holders").GetDictOrEmpty("bag")["metadata"] = metadata; break;
                case "machinery": unsafeSummary.GetDictOrEmpty("machinery").GetDictOrEmpty("machine-a")["metadata"] = metadata; break;
                case "receipt": unsafeSummary.GetDictOrEmpty("receipts").GetDictOrEmpty(oldReceipt.GetString("transaction_id"))["metadata"] = metadata; break;
            }
            Assert.IsFalse(DomainBundle.TryCreate(unsafeSummary, out DomainBundle rejected, out string reason));
            Assert.IsNull(rejected); Assert.IsNotEmpty(reason);
            Assert.Throws<ArgumentException>(() => new DomainTransactionCoordinator(unsafeSummary));
            Assert.IsFalse(coordinator.ApplySummary(unsafeSummary)); Same(initial, coordinator.GetSummary());
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed"), "Rejected import must preserve the pending candidate");
            Assert.AreEqual(2L, coordinator.GetSummary().GetInt("revision"));
            Assert.AreEqual("bag", Instances(coordinator.GetSummary()).GetDictOrEmpty("a").GetString("holder"));
            Assert.AreEqual(2, coordinator.GetSummary().GetDictOrEmpty("receipts").Count);
        }

        [TestCase("array")]
        [TestCase("dictionary")]
        public void MutableCommandDictionaryKeysRejectBeforeCoordinatorCopyWithoutReservingIdentity(string keyKind)
        {
            GdDict initial = Bundle(); var coordinator = new DomainTransactionCoordinator(initial);
            GdDict command = Move();
            object mutableKey = keyKind == "array" ? (object)GdArray.Of("caller-owned") : new GdDict { { "caller", "owned" } };
            command["metadata"] = GdArray.Of(new GdDict { { mutableKey, "opaque" } });
            GdDict rejected = coordinator.Prepare(command);
            Assert.IsFalse(rejected.GetBool("ok"));
            Assert.AreEqual("invalid_command", rejected.GetString("reason")); Same(initial, coordinator.GetSummary());
            Assert.IsTrue(coordinator.Commit(Prepare(coordinator)).GetBool("committed"), "Rejected command must not reserve its identity");
        }

        static object CyclicValue(string kind)
        {
            if (kind == "array") { var array = new GdArray(); array.Add(array); return array; }
            var dictionary = new GdDict(); dictionary["self"] = dictionary; return dictionary;
        }

        [TestCase("root", "dictionary")]
        [TestCase("root", "array")]
        [TestCase("holder", "dictionary")]
        [TestCase("holder", "array")]
        [TestCase("machinery", "dictionary")]
        [TestCase("machinery", "array")]
        [TestCase("receipt", "dictionary")]
        [TestCase("receipt", "array")]
        public void CyclicValueGraphRejectsBeforeWholeDomainCopyAndPreservesPendingCandidate(string location, string kind)
        {
            GdDict initial = Bundle(); initial["revision"] = 1L;
            GdDict oldReceipt = ComponentDomainTestFixtures.Receipt();
            initial.GetDictOrEmpty("receipts")[oldReceipt.GetString("transaction_id")] = oldReceipt;
            var coordinator = new DomainTransactionCoordinator(initial);
            string transaction = Prepare(coordinator, ComponentDomainTestFixtures.Move(domainRevision: 1));
            GdDict unsafeSummary = initial.DeepCopy(); object cycle = CyclicValue(kind);
            switch (location)
            {
                case "root": unsafeSummary["metadata"] = cycle; break;
                case "holder": unsafeSummary.GetDictOrEmpty("holders").GetDictOrEmpty("bag")["metadata"] = cycle; break;
                case "machinery": unsafeSummary.GetDictOrEmpty("machinery").GetDictOrEmpty("machine-a")["metadata"] = cycle; break;
                case "receipt": unsafeSummary.GetDictOrEmpty("receipts").GetDictOrEmpty(oldReceipt.GetString("transaction_id"))["metadata"] = cycle; break;
            }
            Assert.IsFalse(DomainBundle.TryCreate(unsafeSummary, out DomainBundle rejected, out string reason));
            Assert.IsNull(rejected); Assert.IsNotEmpty(reason);
            Assert.Throws<ArgumentException>(() => new DomainTransactionCoordinator(unsafeSummary));
            Assert.IsFalse(coordinator.ApplySummary(unsafeSummary)); Same(initial, coordinator.GetSummary());
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed"));
            Assert.AreEqual(2, coordinator.GetSummary().GetDictOrEmpty("receipts").Count);
        }

        [TestCase("dictionary")]
        [TestCase("array")]
        public void CyclicCommandRejectsBeforeCoordinatorCopyWithoutReservingIdentity(string kind)
        {
            GdDict initial = Bundle(); var coordinator = new DomainTransactionCoordinator(initial);
            GdDict command = Move(); command["metadata"] = CyclicValue(kind);
            Assert.IsFalse(coordinator.Prepare(command).GetBool("ok")); Same(initial, coordinator.GetSummary());
            Assert.IsTrue(coordinator.Commit(Prepare(coordinator)).GetBool("committed"));
        }

        [Test]
        public void SharedAcyclicMetadataAndImmutableVectorKeysRemainValidAndCopyIndependently()
        {
            GdDict initial = Bundle();
            var shared = new GdDict { { "notes", GdArray.Of("preserve") } };
            var firstKey = new Vec2i(3, 4); var secondKey = new Vec3(1f, 2f, 3f);
            Instances(initial).GetDictOrEmpty("a")["origin"] = new GdDict { { firstKey, shared }, { secondKey, shared }, { true, "scalar-key" } };
            GdDict before = initial.DeepCopy();
            Assert.IsTrue(DomainBundle.TryCreate(initial, out DomainBundle bundle, out string reason), reason);
            shared.GetArrayOrEmpty("notes").Add("caller-change");
            GdDict returnedOrigin = Instances(bundle.GetSummary()).GetDictOrEmpty("a").GetDictOrEmpty("origin");
            returnedOrigin.GetDictOrEmpty(firstKey).GetArrayOrEmpty("notes").Add("returned-change");
            Assert.AreEqual(1, returnedOrigin.GetDictOrEmpty(secondKey).GetArrayOrEmpty("notes").Count);
            Same(before, bundle.GetSummary());
        }

        [TestCase("schema_missing")]
        [TestCase("schema_future")]
        [TestCase("revision_negative")]
        [TestCase("revision_fractional")]
        [TestCase("registry_missing")]
        [TestCase("bad_instance")]
        [TestCase("missing_holder")]
        [TestCase("holder_key_mismatch")]
        [TestCase("holder_kind")]
        [TestCase("holder_owner")]
        [TestCase("holder_revision")]
        [TestCase("parallel_membership")]
        [TestCase("capacity_count")]
        [TestCase("capacity_mass")]
        [TestCase("capacity_overflow")]
        [TestCase("duplicate_slot")]
        [TestCase("slot_forms")]
        [TestCase("slot_duplicate_forms")]
        [TestCase("slot_overfull")]
        [TestCase("slot_wrong_form")]
        [TestCase("machinery_missing")]
        [TestCase("machinery_key_mismatch")]
        [TestCase("machinery_health")]
        [TestCase("machinery_owner")]
        [TestCase("machinery_duplicate_link")]
        [TestCase("receipt_identity")]
        [TestCase("receipt_revision")]
        [TestCase("receipt_result")]
        [TestCase("receipt_duplicate_command")]
        public void Bundle_RejectsCompleteMalformedSnapshotBeforeOwnership(string fault)
        {
            GdDict summary = Bundle(), holders = summary.GetDictOrEmpty("holders"), machinery = summary.GetDictOrEmpty("machinery");
            GdDict a = Instances(summary).GetDictOrEmpty("a"), slot = holders.GetDictOrEmpty("slot-a");
            switch (fault)
            {
                case "schema_missing": summary.Erase("schema_version"); break;
                case "schema_future": summary["schema_version"] = 2L; break;
                case "revision_negative": summary["revision"] = -1L; break;
                case "revision_fractional": summary["revision"] = 0.5; break;
                case "registry_missing": summary.Erase("registry"); break;
                case "bad_instance": a["condition"] = 1.01; break;
                case "missing_holder": a["holder"] = "missing"; break;
                case "holder_key_mismatch": slot["holder_id"] = "other"; break;
                case "holder_kind": slot["kind"] = "another_kind"; break;
                case "holder_owner": slot["owner_id"] = ""; break;
                case "holder_revision": slot["revision"] = -1L; break;
                case "parallel_membership": slot["instance_ids"] = GdArray.Of("b"); break;
                case "capacity_count": holders.GetDictOrEmpty("bag")["capacity_count"] = 0.25; break;
                case "capacity_mass": holders.GetDictOrEmpty("bag")["capacity_mass"] = double.NaN; break;
                case "capacity_overflow": holders.GetDictOrEmpty("bag")["capacity_mass"] = 1.0; break;
                case "duplicate_slot": holders.GetDictOrEmpty("slot-b")["owner_id"] = "ship-a"; break;
                case "slot_forms": slot["accepted_forms"] = new GdArray(); break;
                case "slot_duplicate_forms": slot["accepted_forms"] = GdArray.Of("reactor_console", "reactor_console"); break;
                case "slot_overfull": Instances(summary).GetDictOrEmpty("b")["holder"] = "slot-a"; break;
                case "slot_wrong_form": slot["accepted_forms"] = GdArray.Of("other_form"); break;
                case "machinery_missing": machinery.Erase("machine-a"); break;
                case "machinery_key_mismatch": machinery.GetDictOrEmpty("machine-a")["machinery_id"] = "other"; break;
                case "machinery_health": machinery.GetDictOrEmpty("machine-a")["health"] = double.PositiveInfinity; break;
                case "machinery_owner": machinery.GetDictOrEmpty("machine-a")["owner_id"] = "ship-b"; break;
                case "machinery_duplicate_link": holders.GetDictOrEmpty("slot-b")["machinery_id"] = "machine-a"; break;
                default:
                    summary["revision"] = 1L;
                    GdDict receipt = ComponentDomainTestFixtures.Receipt();
                    summary.GetDictOrEmpty("receipts")[receipt.GetString("transaction_id")] = receipt;
                    if (fault == "receipt_identity") receipt["transaction_id"] = "another";
                    if (fault == "receipt_revision") receipt["revision"] = 2L;
                    if (fault == "receipt_result") receipt["result"] = "not a result";
                    if (fault == "receipt_duplicate_command")
                    {
                        GdDict second = receipt.DeepCopy(); second["transaction_id"] = "duplicate";
                        summary.GetDictOrEmpty("receipts")["duplicate"] = second;
                    }
                    break;
            }
            GdDict before = summary.DeepCopy();
            Assert.IsFalse(DomainBundle.TryCreate(summary, out DomainBundle bundle, out string reason));
            Assert.IsNull(bundle); Assert.IsNotEmpty(reason); Same(before, summary);
            Assert.Throws<ArgumentException>(() => new DomainTransactionCoordinator(summary));
        }

        [Test]
        public void UnknownComponentRemainsNullAndCannotSupplyMachineHealth()
        {
            GdDict summary = Bundle();
            Instances(summary).GetDictOrEmpty("a")["condition_state"] = "unknown";
            Instances(summary).GetDictOrEmpty("a")["condition"] = null;
            Assert.IsTrue(DomainBundle.TryCreate(summary, out DomainBundle bundle, out string reason), reason);
            Assert.IsNull(Instances(bundle.GetSummary()).GetDictOrEmpty("a").Get("condition"));
            Assert.IsTrue(bundle.GetProjections().GetDictOrEmpty("machinery").Has("machine-a"));
            GdDict machine = bundle.GetProjections().GetDictOrEmpty("machinery").GetDictOrEmpty("machine-a");
            Assert.IsFalse(machine.GetBool("component_available")); Assert.AreEqual(0.0, machine.GetFloat("effective_health"));
        }

        [Test]
        public void PrepareAndExternalCandidateAliasesCannotPublishOrChangeInternalCandidate()
        {
            GdDict initial = Bundle(), before = initial.DeepCopy(), command = Move();
            var coordinator = new DomainTransactionCoordinator(initial);
            GdDict prepared = coordinator.Prepare(command);
            Assert.IsTrue(prepared.GetBool("ok"), prepared.GetString("reason"));
            string transaction = prepared.GetString("transaction_id");
            Same(before, coordinator.GetSummary());
            command["destination_holder_id"] = "slot-b"; initial["revision"] = 99L;
            prepared.GetDictOrEmpty("candidate")["revision"] = 99L;
            Instances(prepared.GetDictOrEmpty("candidate")).GetDictOrEmpty("a")["condition"] = 1.0;
            prepared.GetDictOrEmpty("result")["instance_id"] = "b";
            GdDict committed = coordinator.Commit(transaction);
            Assert.IsTrue(committed.GetBool("committed"), committed.GetString("reason"));
            GdDict final = coordinator.GetSummary();
            Assert.AreEqual(1L, final.GetInt("revision"));
            Assert.AreEqual("bag", Instances(final).GetDictOrEmpty("a").GetString("holder"));
            Assert.AreEqual(0.23, Instances(final).GetDictOrEmpty("a").GetFloat("condition"));
            Same(before.Get("machinery"), final.Get("machinery"));
            Assert.AreEqual("a", committed.GetDictOrEmpty("result").GetString("instance_id"));
        }

        [TestCase("registry")]
        [TestCase("holders")]
        [TestCase("machinery")]
        [TestCase("receipt")]
        [TestCase("validation")]
        [TestCase("publication")]
        public void FaultAtEveryPrepublicationBoundaryDiscardsCandidateAndKeepsCompleteOldBundle(string fault)
        {
            GdDict initial = Bundle(); bool fail = true; int notified = 0;
            DomainTransactionCoordinator coordinator = null;
            coordinator = new DomainTransactionCoordinator(initial, stage =>
            {
                Same(initial, coordinator.GetSummary());
                if (fail && stage == fault) throw new InvalidOperationException("injected:" + fault);
            }, result => notified++);
            string transaction = Prepare(coordinator);
            Assert.IsFalse(coordinator.Commit(transaction).GetBool("committed"));
            Same(initial, coordinator.GetSummary()); Assert.AreEqual(0, notified);
            Assert.IsFalse(coordinator.Commit(transaction).GetBool("committed"), "Discarded preparation must not be reusable");
            fail = false;
            string retry = Prepare(coordinator);
            Assert.AreEqual(transaction, retry, "A discarded command without receipt may be prepared afresh");
            Assert.IsTrue(coordinator.Commit(retry).GetBool("committed")); Assert.AreEqual(1, notified);
        }

        [Test]
        public void ReadersSeeCompleteOldUntilPublicationAndCompleteNewAtNotification()
        {
            GdDict initial = Bundle(), published = null; var stages = new List<string>();
            DomainTransactionCoordinator coordinator = null;
            coordinator = new DomainTransactionCoordinator(initial, stage =>
            {
                stages.Add(stage); Same(initial, coordinator.GetSummary());
            }, result =>
            {
                published = coordinator.GetSummary();
                Assert.AreEqual(1L, published.GetInt("revision"));
                Assert.AreEqual("bag", Instances(published).GetDictOrEmpty("a").GetString("holder"));
                Assert.AreEqual(1, published.GetDictOrEmpty("receipts").Count);
                Same(initial.Get("machinery"), published.Get("machinery"));
            });
            Assert.IsTrue(coordinator.Commit(Prepare(coordinator)).GetBool("committed"));
            CollectionAssert.AreEqual(new[] { "registry", "holders", "machinery", "receipt", "validation", "publication" }, stages);
            Assert.IsNotNull(published); Same(published, coordinator.GetSummary());
            Assert.AreEqual(1L, published.GetInt("revision"));
            Assert.AreEqual("bag", Instances(published).GetDictOrEmpty("a").GetString("holder"));
            Assert.AreEqual(1, published.GetDictOrEmpty("receipts").Count);
            Same(initial.Get("machinery"), published.Get("machinery"));
        }

        [TestCase("registry")]
        [TestCase("holders")]
        [TestCase("machinery")]
        [TestCase("receipt")]
        [TestCase("validation")]
        [TestCase("publication")]
        public void ReentrantMutationAtEveryStageRejectsWithoutDisturbingOuterCommit(string boundary)
        {
            GdDict initial = Bundle(); int checkedGuards = 0; string transaction = null;
            DomainTransactionCoordinator coordinator = null;
            coordinator = new DomainTransactionCoordinator(initial, stage =>
            {
                if (stage != boundary) return;
                Assert.IsFalse(coordinator.Prepare(Move("nested")).GetBool("ok"));
                Assert.IsFalse(coordinator.Commit(transaction).GetBool("committed"));
                GdDict import = initial.DeepCopy(); import["revision"] = 10L;
                Assert.IsFalse(coordinator.ApplySummary(import));
                Same(initial, coordinator.GetSummary()); checkedGuards++;
            });
            transaction = Prepare(coordinator);
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed"));
            Assert.AreEqual(1, checkedGuards); Assert.AreEqual(1L, coordinator.GetSummary().GetInt("revision"));
        }

        [Test]
        public void ReceiptReplayReturnsOneEffectAndCannotReinterpretConflictingCommand()
        {
            int notifications = 0; var coordinator = new DomainTransactionCoordinator(Bundle(), null, result => notifications++);
            string transaction = Prepare(coordinator);
            GdDict committed = coordinator.Commit(transaction), final = coordinator.GetSummary();
            Assert.IsTrue(committed.GetBool("committed"));
            Same(committed, coordinator.Commit(transaction)); Same(final, coordinator.GetSummary());
            Assert.AreEqual(1, notifications); Assert.AreEqual(1, final.GetDictOrEmpty("receipts").Count);
            GdDict conflicting = Move(); conflicting["destination_holder_id"] = "slot-b";
            Assert.IsFalse(coordinator.Prepare(conflicting).GetBool("ok")); Same(final, coordinator.GetSummary());
            var restarted = new DomainTransactionCoordinator(final.DeepCopy(), null, result => notifications++);
            Same(committed, restarted.Commit(transaction)); Same(final, restarted.GetSummary()); Assert.AreEqual(1, notifications);
        }

        [Test]
        public void ParsedJsonNumericRepresentationIsRejectedWithoutChangingTypedDomainOrPendingCandidate()
        {
            GdDict initial = Bundle(); var coordinator = new DomainTransactionCoordinator(initial);
            string transaction = Prepare(coordinator);
            GdDict parsed = (GdDict)GdJson.ParseString(GdJson.Stringify(initial));
            Assert.IsInstanceOf<double>(parsed.Get("schema_version"));
            Assert.IsFalse(DomainBundle.TryCreate(parsed, out DomainBundle rejected, out string reason));
            Assert.IsNull(rejected); Assert.IsNotEmpty(reason);
            Assert.Throws<ArgumentException>(() => new DomainTransactionCoordinator(parsed));
            Assert.IsFalse(coordinator.ApplySummary(parsed)); Same(initial, coordinator.GetSummary());
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed"));
        }

        [Test]
        public void PendingCommandIdentityRejectsDuplicateAndCompetingCandidateBecomesStale()
        {
            var coordinator = new DomainTransactionCoordinator(Bundle());
            string first = Prepare(coordinator);
            GdDict conflict = Move(); conflict["destination_holder_id"] = "slot-b";
            Assert.IsFalse(coordinator.Prepare(conflict).GetBool("ok"));
            string competing = Prepare(coordinator, ComponentDomainTestFixtures.Move("move-b", "b", "bag", "cargo-b"));
            Assert.IsTrue(coordinator.Commit(first).GetBool("committed")); GdDict final = coordinator.GetSummary();
            Assert.IsFalse(coordinator.Commit(competing).GetBool("committed")); Same(final, coordinator.GetSummary());
            Assert.AreEqual("bag", Instances(final).GetDictOrEmpty("b").GetString("holder"));
        }

        [Test]
        public void NotificationFailureCannotUndoOrReplayPublishedDomainEffects()
        {
            int notifications = 0; var coordinator = new DomainTransactionCoordinator(Bundle(), null, result =>
            {
                notifications++; result["revision"] = 99L; throw new InvalidOperationException("presentation-down");
            });
            string transaction = Prepare(coordinator);
            GdDict first = coordinator.Commit(transaction), final = coordinator.GetSummary();
            Assert.IsTrue(first.GetBool("committed")); Assert.IsTrue(first.GetBool("presentation_failed"));
            Assert.IsNotEmpty(first.GetString("presentation_error"));
            Assert.AreEqual(1L, final.GetInt("revision")); Assert.AreEqual(1, final.GetDictOrEmpty("receipts").Count);
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed")); Same(final, coordinator.GetSummary()); Assert.AreEqual(1, notifications);
        }

        sealed class UnreadableMessageException : Exception
        {
            public override string Message => throw new InvalidOperationException("exception-message-unavailable");
        }

        [Test]
        public void NotificationFailureWithThrowingMessageStillReturnsReceiptOwnedCommittedOutcome()
        {
            GdDict initial = Bundle(); int notifications = 0;
            var coordinator = new DomainTransactionCoordinator(initial, null, result =>
            {
                notifications++; throw new UnreadableMessageException();
            });
            string transaction = Prepare(coordinator);
            GdDict first = coordinator.Commit(transaction), final = coordinator.GetSummary();
            Assert.IsTrue(first.GetBool("committed"), "Notification diagnostic failure cannot reinterpret a published receipt");
            Assert.IsTrue(first.GetBool("presentation_failed")); Assert.IsNotEmpty(first.GetString("presentation_error"));
            Assert.AreEqual(1L, final.GetInt("revision"));
            Assert.AreEqual("bag", Instances(final).GetDictOrEmpty("a").GetString("holder"));
            Assert.AreEqual(1, final.GetDictOrEmpty("receipts").Count);
            Assert.AreEqual(transaction, final.GetDictOrEmpty("receipts").GetDictOrEmpty(transaction).GetString("transaction_id"));
            Same(initial.Get("machinery"), final.Get("machinery"));
            Same(first, coordinator.Commit(transaction)); Same(final, coordinator.GetSummary()); Assert.AreEqual(1, notifications);
        }

        [Test]
        public void PrepublicationExceptionWithThrowingMessageReturnsFailureAndKeepsCompleteOldDomain()
        {
            GdDict initial = Bundle(); bool fail = true; int notifications = 0;
            var coordinator = new DomainTransactionCoordinator(initial, stage =>
            {
                if (fail && stage == "publication") throw new UnreadableMessageException();
            }, result => notifications++);
            string transaction = Prepare(coordinator);
            GdDict failed = coordinator.Commit(transaction);
            Assert.IsFalse(failed.GetBool("committed")); Assert.IsNotEmpty(failed.GetString("detail"));
            Same(initial, coordinator.GetSummary()); Assert.AreEqual(0, notifications);
            Assert.IsFalse(coordinator.Commit(transaction).GetBool("committed"));
            fail = false;
            Assert.AreEqual(transaction, Prepare(coordinator));
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed")); Assert.AreEqual(1, notifications);
        }

        [Test]
        public void ImportValidatesBeforeReplacementAndFailedImportKeepsPendingCandidate()
        {
            GdDict initial = Bundle(); var coordinator = new DomainTransactionCoordinator(initial);
            string transaction = Prepare(coordinator);
            GdDict invalid = initial.DeepCopy(); invalid["revision"] = 4L; Instances(invalid).GetDictOrEmpty("a")["holder"] = "missing";
            Assert.IsFalse(coordinator.ApplySummary(invalid)); Same(initial, coordinator.GetSummary());
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed"));
        }

        [Test]
        public void ImportRejectsDecreaseAndDifferentSameRevisionButAcceptsExactReplayAndHigherCompleteState()
        {
            GdDict initial = Bundle(); var coordinator = new DomainTransactionCoordinator(initial);
            Assert.IsTrue(coordinator.ApplySummary(initial.DeepCopy()));
            GdDict differing = initial.DeepCopy(); differing.GetDictOrEmpty("machinery").GetDictOrEmpty("machine-a")["health"] = 0.4;
            Assert.IsFalse(coordinator.ApplySummary(differing)); Same(initial, coordinator.GetSummary());
            string transaction = Prepare(coordinator);
            GdDict newer = initial.DeepCopy(); newer["revision"] = 5L;
            Assert.IsTrue(coordinator.ApplySummary(newer)); Same(newer, coordinator.GetSummary());
            Assert.IsFalse(coordinator.ApplySummary(initial)); Same(newer, coordinator.GetSummary());
            Assert.IsFalse(coordinator.Commit(transaction).GetBool("committed")); Same(newer, coordinator.GetSummary());
            newer["revision"] = 999L; Assert.AreEqual(5L, coordinator.GetSummary().GetInt("revision"));
        }

        [Test]
        public void HigherRevisionImportCannotForgetCommittedCommand()
        {
            var coordinator = new DomainTransactionCoordinator(Bundle());
            string transaction = Prepare(coordinator);
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed"));
            GdDict final = coordinator.GetSummary(), forgotten = final.DeepCopy();
            forgotten["revision"] = 2L; forgotten["receipts"] = new GdDict();
            Assert.IsFalse(coordinator.ApplySummary(forgotten)); Same(final, coordinator.GetSummary());
            Assert.IsFalse(coordinator.Prepare(Move()).GetBool("ok"));
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed")); Same(final, coordinator.GetSummary());
        }

        [Test]
        public void HigherRevisionImportCannotRewriteReceipt()
        {
            var coordinator = new DomainTransactionCoordinator(Bundle());
            string transaction = Prepare(coordinator);
            Assert.IsTrue(coordinator.Commit(transaction).GetBool("committed"));
            GdDict final = coordinator.GetSummary(), rewritten = final.DeepCopy();
            rewritten["revision"] = 2L;
            rewritten.GetDictOrEmpty("receipts").GetDictOrEmpty(transaction).GetDictOrEmpty("result")["destination_holder_id"] = "cargo-b";
            Assert.IsFalse(coordinator.ApplySummary(rewritten)); Same(final, coordinator.GetSummary());
            Assert.IsFalse(coordinator.Prepare(Move()).GetBool("ok"));
        }
    }
}
