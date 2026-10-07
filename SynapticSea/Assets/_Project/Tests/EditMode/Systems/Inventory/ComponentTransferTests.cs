using System;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>Diagnostic supplied snapshots only; no production acquisition, live models, save or UI binding.</summary>
    public class ComponentTransferTests
    {
        const string A = "component-A", B = "component-B";
        const string SlotA = "ship-A/slot/engine", SlotB = "ship-B/slot/engine";
        const string Player = "player/bag", CargoA = "ship-A/cargo", CargoB = "ship-B/cargo";

        static GdDict Instance(string id, string holder, double condition, long revision) => new GdDict
        {
            { "schema_version", 1L }, { "instance_id", id }, { "definition_id", "reactor-console-definition" },
            { "item_form", "reactor_console" }, { "condition_state", "known" }, { "condition", condition },
            { "mass", 12.5 }, { "holder", holder }, { "revision", revision },
            { "origin", new GdDict { { "ship", id == A ? "ship-A" : "ship-B" } } },
            { "provenance", new GdDict { { "evidence", GdArray.Of(new GdDict { { "tag", "diagnostic" } }) } } },
        };

        static GdDict Holder(string id, string kind, string owner, long revision) => new GdDict
        {
            { "holder_id", id }, { "kind", kind }, { "owner_id", owner }, { "revision", revision },
        };

        static GdDict Slot(string id, string owner, long revision, string machinery)
        {
            GdDict slot = Holder(id, "slot", owner, revision);
            slot["slot_id"] = "engine";
            slot["accepted_forms"] = GdArray.Of("reactor_console");
            slot["machinery_id"] = machinery;
            return slot;
        }

        static GdDict Snapshot()
        {
            GdDict cargo = Holder(CargoA, "ship_cargo", "ship-A", 5);
            cargo["capacity_count"] = 1L;
            cargo["capacity_mass"] = 20.0;
            return new GdDict
            {
                { "schema_version", 1L }, { "revision", 7L },
                { "registry", new GdDict { { "schema_version", 1L }, { "instances", new GdDict
                    { { A, Instance(A, SlotA, 0.23, 11) }, { B, Instance(B, SlotB, 0.81, 17) } } } } },
                { "holders", new GdDict
                    {
                        { SlotA, Slot(SlotA, "ship-A", 2, "machinery-A") },
                        { SlotB, Slot(SlotB, "ship-B", 4, "machinery-B") },
                        { Player, Holder(Player, "player", "player-local", 3) }, { CargoA, cargo },
                        { CargoB, Holder(CargoB, "ship_cargo", "ship-B", 6) },
                    } },
                { "machinery", new GdDict
                    {
                        { "machinery-A", new GdDict { { "machinery_id", "machinery-A" }, { "owner_id", "ship-A" }, { "health", 0.8 } } },
                        { "machinery-B", new GdDict { { "machinery_id", "machinery-B" }, { "owner_id", "ship-B" }, { "health", 0.2 } } },
                    } },
                { "receipts", new GdDict { { "component_transfer:prior", new GdDict
                    {
                        { "schema_version", 1L }, { "transaction_id", "component_transfer:prior" }, { "command_id", "prior" },
                        { "revision", 5L }, { "result", new GdDict { { "operation", "transfer" }, { "instance_id", A },
                            { "source_holder_id", Player }, { "destination_holder_id", SlotA } } },
                    } } } },
            };
        }

        static GdDict Row(GdDict snapshot, string id) => snapshot.GetDictOrEmpty("registry").GetDictOrEmpty("instances").GetDictOrEmpty(id);
        static GdDict Descriptor(GdDict snapshot, string id) => snapshot.GetDictOrEmpty("holders").GetDictOrEmpty(id);

        static GdDict Command(GdDict snapshot, string destination = Player, string instance = A, string commandId = "move", string swap = "")
        {
            string source = Row(snapshot, instance).GetString("holder");
            var command = new GdDict
            {
                { "schema_version", 1L }, { "command_id", commandId }, { "operation", swap.Length == 0 ? "transfer" : "swap" },
                { "instance_id", instance }, { "source_holder_id", source }, { "destination_holder_id", destination },
                { "expected_domain_revision", snapshot.GetInt("revision") }, { "expected_instance_revision", Row(snapshot, instance).GetInt("revision") },
                { "expected_source_revision", Descriptor(snapshot, source).GetInt("revision") },
                { "expected_destination_revision", Descriptor(snapshot, destination).GetInt("revision") },
            };
            if (swap.Length > 0)
            {
                command["swap_instance_id"] = swap;
                command["expected_swap_instance_revision"] = Row(snapshot, swap).GetInt("revision");
            }
            return command;
        }

        static void Conserved(GdDict original, GdDict candidate)
        {
            Assert.AreEqual(original.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Count,
                candidate.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Count);
            foreach (string id in new[] { A, B })
            {
                GdDict before = Row(original, id), after = Row(candidate, id);
                foreach (object key in before.Keys)
                {
                    if (V.Str(key) == "holder" || V.Str(key) == "revision") continue;
                    Assert.IsTrue(V.VariantEquals(before[key], after.Get(key)), id + "/" + key);
                }
            }
            Assert.IsTrue(V.VariantEquals(original.Get("machinery"), candidate.Get("machinery")), "saved independent health is unchanged");
            Assert.IsTrue(V.VariantEquals(original.Get("receipts"), candidate.Get("receipts")), "pure preparation creates no receipt");
        }

        static GdDict Success(GdDict command, GdDict snapshot)
        {
            GdDict before = snapshot.DeepCopy(), commandBefore = command.DeepCopy();
            GdDict result = new ComponentTransferService().Prepare(command, snapshot);
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            Assert.AreEqual("component_transfer:" + command.GetString("command_id"), result.GetString("transaction_id"));
            Assert.AreEqual(snapshot.GetInt("revision"), result.GetInt("expected_revision"));
            Assert.IsTrue(V.VariantEquals(before, snapshot));
            Assert.IsTrue(V.VariantEquals(commandBefore, command));
            GdDict candidate = result.GetDictOrEmpty("candidate");
            Assert.AreEqual(before.GetInt("revision") + 1, candidate.GetInt("revision"));
            Assert.AreEqual(command.GetString("operation"), result.GetDictOrEmpty("result").GetString("operation"));
            Conserved(before, candidate);
            return candidate;
        }

        static void Denied(GdDict snapshot, GdDict command, string reason)
        {
            GdDict before = snapshot.DeepCopy(), commandBefore = command.DeepCopy();
            GdDict result = new ComponentTransferService().Prepare(command, snapshot);
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual(reason, result.GetString("reason"));
            Assert.IsTrue(result.GetDictOrEmpty("candidate").IsEmpty);
            Assert.IsTrue(V.VariantEquals(before, snapshot));
            Assert.IsTrue(V.VariantEquals(commandBefore, command));
        }

        [Test]
        public void SameFormDifferentConditions_KeepExactIdentityAcrossShipsAndReturn()
        {
            GdDict snapshot = Snapshot();
            GdDict original = snapshot.DeepCopy();
            int step = 0;
            foreach (string destination in new[] { Player, CargoA, CargoB, SlotA })
                snapshot = Success(Command(snapshot, destination, commandId: "route-" + step++), snapshot);
            snapshot = Success(Command(snapshot, SlotB, commandId: "swap-out", swap: B), snapshot);
            Assert.AreEqual(SlotB, Row(snapshot, A).GetString("holder"));
            Assert.AreEqual(SlotA, Row(snapshot, B).GetString("holder"));
            Assert.AreEqual(0.23, Row(snapshot, A).GetFloat("condition"));
            Assert.AreEqual(0.81, Row(snapshot, B).GetFloat("condition"));
            snapshot = Success(Command(snapshot, SlotA, commandId: "swap-back", swap: B), snapshot);
            Assert.AreEqual(SlotA, Row(snapshot, A).GetString("holder"));
            Assert.AreEqual(SlotB, Row(snapshot, B).GetString("holder"));
            Conserved(original, snapshot);
        }

        [Test]
        public void Swap_ChangesOnlyBothHoldersAndMovedRevisionsOnce()
        {
            GdDict snapshot = Snapshot();
            GdDict candidate = Success(Command(snapshot, SlotB, swap: B), snapshot);
            Assert.AreEqual(12, Row(candidate, A).GetInt("revision"));
            Assert.AreEqual(18, Row(candidate, B).GetInt("revision"));
            Assert.AreEqual(3, Descriptor(candidate, SlotA).GetInt("revision"));
            Assert.AreEqual(5, Descriptor(candidate, SlotB).GetInt("revision"));
            Assert.IsTrue(V.VariantEquals(Descriptor(snapshot, Player), Descriptor(candidate, Player)));
            Assert.AreEqual("engine", Descriptor(candidate, SlotA).GetString("slot_id"));
            Assert.AreEqual("engine", Descriptor(candidate, SlotB).GetString("slot_id"), "equal local IDs on different ships do not cross-write");
        }

        [Test]
        public void Swap_EvaluatesCompleteCandidateInsteadOfTransientDoubleOccupancy()
        {
            GdDict snapshot = Snapshot();
            Row(snapshot, A)["holder"] = CargoA;
            Row(snapshot, B)["holder"] = CargoB;
            Row(snapshot, B)["mass"] = 20.0;
            Descriptor(snapshot, CargoB)["capacity_count"] = 1L;
            Descriptor(snapshot, CargoB)["capacity_mass"] = 20.0;
            GdDict candidate = Success(Command(snapshot, CargoB, swap: B), snapshot);
            Assert.AreEqual(CargoB, Row(candidate, A).GetString("holder"));
            Assert.AreEqual(CargoA, Row(candidate, B).GetString("holder"));
        }

        [TestCase("capacity_count")]
        [TestCase("capacity_mass")]
        public void FullDestination_LeavesSourceMountedAndUnchanged(string limit)
        {
            GdDict snapshot = Snapshot();
            Descriptor(snapshot, CargoA)[limit] = limit == "capacity_count" ? (object)0L : 12.0;
            Denied(snapshot, Command(snapshot, CargoA), limit);
            Assert.AreEqual(SlotA, Row(snapshot, A).GetString("holder"));
        }

        [Test]
        public void OccupiedSlot_RequiresExplicitNamedSwap()
        {
            GdDict snapshot = Snapshot();
            Denied(snapshot, Command(snapshot, SlotB), "slot_occupied");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void IncompatibleSlotForm_RejectsTransferAndSwap(bool swap)
        {
            GdDict snapshot = Snapshot();
            Descriptor(snapshot, SlotB)["accepted_forms"] = GdArray.Of("other_form");
            Row(snapshot, B)["item_form"] = "other_form";
            if (!swap) Row(snapshot, B)["holder"] = CargoA;
            Denied(snapshot, Command(snapshot, SlotB, swap: swap ? B : ""), "form_incompatible");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnknownNullCondition_CannotInstallInTransferOrSwap(bool swap)
        {
            GdDict snapshot = Snapshot();
            Row(snapshot, A)["condition_state"] = "unknown";
            Row(snapshot, A)["condition"] = null;
            if (!swap) Row(snapshot, B)["holder"] = CargoA;
            Denied(snapshot, Command(snapshot, SlotB, swap: swap ? B : ""), "condition_unknown");
        }

        [Test]
        public void UnknownNullCondition_CanTransferToNonSlotWithoutInventingCondition()
        {
            GdDict snapshot = Snapshot();
            Row(snapshot, A)["condition_state"] = "unknown";
            Row(snapshot, A)["condition"] = null;
            GdDict candidate = Success(Command(snapshot, Player), snapshot);
            Assert.AreEqual("unknown", Row(candidate, A).GetString("condition_state"));
            Assert.IsTrue(Row(candidate, A).Has("condition"));
            Assert.IsNull(Row(candidate, A)["condition"]);
        }

        [Test]
        public void KnownZeroCondition_RemainsKnownAndMayInstall()
        {
            GdDict snapshot = Snapshot();
            Row(snapshot, A)["condition"] = 0.0;
            Row(snapshot, B)["holder"] = CargoA;
            GdDict candidate = Success(Command(snapshot, SlotB), snapshot);
            Assert.AreEqual("known", Row(candidate, A).GetString("condition_state"));
            Assert.AreEqual(0.0, Row(candidate, A).GetFloat("condition"));
        }

        [TestCase("missing")]
        [TestCase("known_null")]
        [TestCase("known_nan")]
        [TestCase("unknown_numeric")]
        public void MalformedCondition_IsRejectedBeforePreparingAnyMove(string variant)
        {
            GdDict snapshot = Snapshot();
            GdDict row = Row(snapshot, A);
            if (variant == "missing") row.Erase("condition");
            if (variant == "known_null") row["condition"] = null;
            if (variant == "known_nan") row["condition"] = double.NaN;
            if (variant == "unknown_numeric") { row["condition_state"] = "unknown"; row["condition"] = 0.0; }
            Denied(snapshot, Command(snapshot), "invalid_domain");
        }

        [TestCase("expected_domain_revision", "stale_domain")]
        [TestCase("expected_instance_revision", "stale_instance")]
        [TestCase("expected_source_revision", "stale_source")]
        [TestCase("expected_destination_revision", "stale_destination")]
        [TestCase("expected_swap_instance_revision", "stale_swap")]
        public void StaleExpectedRevision_IsNonmutating(string field, string reason)
        {
            GdDict snapshot = Snapshot();
            GdDict command = Command(snapshot, SlotB, swap: B);
            command[field] = command.GetInt(field) - 1;
            Denied(snapshot, command, reason);
        }

        [Test]
        public void SourceHolderMustMatchCanonicalInstanceHolder()
        {
            GdDict snapshot = Snapshot();
            GdDict command = Command(snapshot);
            command["source_holder_id"] = CargoA;
            command["expected_source_revision"] = Descriptor(snapshot, CargoA).GetInt("revision");
            Denied(snapshot, command, "source_mismatch");
        }

        [Test]
        public void SwapCounterpartMustActuallyOccupyDestination()
        {
            GdDict snapshot = Snapshot();
            Row(snapshot, B)["holder"] = CargoA;
            Denied(snapshot, Command(snapshot, SlotB, swap: B), "swap_source_mismatch");
        }

        [Test]
        public void SwapCannotNameTheSameInstanceTwice()
        {
            GdDict snapshot = Snapshot();
            Denied(snapshot, Command(snapshot, SlotB, swap: A), "invalid_command");
        }

        [TestCase("schema")]
        [TestCase("blank_id")]
        [TestCase("operation")]
        [TestCase("same_holder")]
        [TestCase("missing_revision")]
        [TestCase("float_revision")]
        [TestCase("bool_revision")]
        [TestCase("negative_revision")]
        public void MalformedCommand_IsNonmutating(string variant)
        {
            GdDict snapshot = Snapshot();
            GdDict command = Command(snapshot);
            switch (variant)
            {
                case "schema": command["schema_version"] = 0L; break;
                case "blank_id": command["command_id"] = "  "; break;
                case "operation": command["operation"] = "by_form"; break;
                case "same_holder": command["destination_holder_id"] = SlotA; break;
                case "missing_revision": command.Erase("expected_domain_revision"); break;
                case "float_revision": command["expected_domain_revision"] = 7.0; break;
                case "bool_revision": command["expected_domain_revision"] = true; break;
                case "negative_revision": command["expected_domain_revision"] = -1L; break;
            }
            Denied(snapshot, command, "invalid_command");
        }

        [Test]
        public void SuppliedAndReturnedNestedObjects_HaveNoMutationAliases()
        {
            GdDict snapshot = Snapshot(), clean = snapshot.DeepCopy();
            GdDict command = Command(snapshot);
            var service = new ComponentTransferService();
            GdDict result = service.Prepare(command, snapshot);
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            GdDict candidate = result.GetDictOrEmpty("candidate");
            command["destination_holder_id"] = CargoB;
            Row(snapshot, A)["mass"] = 999.0;
            ((GdDict)Row(snapshot, A).GetDictOrEmpty("provenance").GetArrayOrEmpty("evidence")[0])["tag"] = "changed input";
            Assert.AreEqual(12.5, Row(candidate, A).GetFloat("mass"));
            Assert.AreEqual("diagnostic", ((GdDict)Row(candidate, A).GetDictOrEmpty("provenance").GetArrayOrEmpty("evidence")[0]).GetString("tag"));
            Row(candidate, A)["condition"] = 0.99;
            Descriptor(candidate, SlotA).GetArrayOrEmpty("accepted_forms").Add("changed output");
            candidate.GetDictOrEmpty("machinery").GetDictOrEmpty("machinery-A")["health"] = 0.0;
            Assert.IsTrue(V.VariantEquals(Snapshot(), clean));
            GdDict repeated = service.Prepare(Command(clean), clean).GetDictOrEmpty("candidate");
            Assert.AreEqual(0.23, Row(repeated, A).GetFloat("condition"));
            Assert.AreEqual(1, Descriptor(repeated, SlotA).GetArrayOrEmpty("accepted_forms").Count);
            Assert.AreEqual(0.8, repeated.GetDictOrEmpty("machinery").GetDictOrEmpty("machinery-A").GetFloat("health"));
        }

        [Test]
        public void CommittedCommandIdentity_CannotBeReinterpreted()
        {
            GdDict snapshot = Snapshot();
            Denied(snapshot, Command(snapshot, commandId: "prior"), "duplicate_command");
        }

        [Test]
        public void MissingOptionalCapacity_DoesNotInventHardPlayerLimits()
        {
            GdDict snapshot = Snapshot();
            Row(snapshot, A)["mass"] = 1000.0;
            GdDict candidate = Success(Command(snapshot), snapshot);
            Assert.AreEqual(Player, Row(candidate, A).GetString("holder"));
            Assert.IsFalse(Descriptor(candidate, Player).Has("capacity_mass"));
        }

        [TestCase("schema")]
        [TestCase("missing_holder")]
        [TestCase("holder_identity")]
        [TestCase("duplicate_owner_slot")]
        [TestCase("machinery_owner")]
        public void InvalidDomainRecords_CannotProduceCandidate(string variant)
        {
            GdDict snapshot = Snapshot();
            GdDict command = Command(snapshot);
            switch (variant)
            {
                case "schema": snapshot["schema_version"] = 2L; break;
                case "missing_holder": Row(snapshot, B)["holder"] = "missing"; break;
                case "holder_identity": Descriptor(snapshot, CargoB)["holder_id"] = "different"; break;
                case "duplicate_owner_slot": Descriptor(snapshot, SlotB)["owner_id"] = "ship-A"; break;
                case "machinery_owner": snapshot.GetDictOrEmpty("machinery").GetDictOrEmpty("machinery-A")["owner_id"] = "ship-B"; break;
            }
            Denied(snapshot, command, "invalid_domain");
        }

        [Test]
        public void MutableDictionaryKeyInNestedDomainMetadata_IsRejectedBeforeCopy()
        {
            GdDict snapshot = Snapshot();
            var key = new GdDict { { "tag", "mutable input key" } };
            Row(snapshot, A).GetDictOrEmpty("provenance").GetArrayOrEmpty("evidence").Add(
                new GdDict { { "mapping", new GdDict { { key, "retained key alias" } } } });
            Denied(snapshot, Command(snapshot), "invalid_domain");
            Assert.AreEqual("mutable input key", key.GetString("tag"));
        }

        [Test]
        public void MutableArrayKeyInCommandExtension_IsRejectedBeforeCopy()
        {
            GdDict snapshot = Snapshot();
            GdDict command = Command(snapshot);
            var key = GdArray.Of("mutable input key");
            command["extension"] = new GdDict { { "mapping", new GdDict { { key, "retained key alias" } } } };
            Denied(snapshot, command, "invalid_command");
            Assert.AreEqual("mutable input key", key[0]);
        }

        [TestCase("domain")]
        [TestCase("instance")]
        [TestCase("holder")]
        public void RevisionOverflow_CannotWrapCandidateRevisions(string boundary)
        {
            GdDict snapshot = Snapshot();
            if (boundary == "domain") snapshot["revision"] = long.MaxValue;
            if (boundary == "instance") Row(snapshot, A)["revision"] = long.MaxValue;
            if (boundary == "holder") Descriptor(snapshot, SlotA)["revision"] = long.MaxValue;
            Denied(snapshot, Command(snapshot), "revision_overflow");
        }
    }
}
