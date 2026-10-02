using System;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>Detached receipt-log diagnostics; no live job, earned ingredients or progression publication.</summary>
    public class TrainingReceiptTests
    {
        static GdDict Event() => new GdDict
        {
            { "event_id", "resolved_external" }, { "target_id", "fixture:part_a" }, { "skill_id", "repair" },
            { "base_xp", 12L }, { "category", "technical" }, { "is_cross_training", false }, { "gated", false },
            { "evidence", new GdDict { { "history", GdArray.Of(new GdDict { { "before", "fixture" } }) } } },
        };

        static GdDict Receipt(string commitId = "commit_a")
        {
            GdDict row = Event();
            row["receipt_owned"] = true;
            row["commit_id"] = commitId;
            row["sequence"] = 0L;
            return row;
        }

        static GdDict Summary(GdArray log, long delivered = 0, long dropped = 0) => new GdDict
        {
            { "log", log }, { "xp_total", delivered }, { "dropped", dropped }, { "event_count", (long)log.Count },
        };

        static TrainingEventBus Bus()
        {
            var bus = new TrainingEventBus();
            Assert.IsTrue(bus.Configure(new GdDict
            {
                { "training_actions", GdArray.Of(new GdDict
                {
                    { "event_id", "ordinary" }, { "target_skill", "repair" }, { "base_xp", 7L }, { "category", "technical" },
                }) },
            }));
            return bus;
        }

        static PlayerProgressionState Progression()
        {
            var progression = new PlayerProgressionState();
            progression.Configure(null, new GdDict { { "repair", new GdDict { { "category", "technical" } } } });
            return progression;
        }

        static TrainingEventBus OrdinaryBus()
        {
            TrainingEventBus bus = Bus();
            Assert.IsNotNull(bus.Emit("ordinary", "fixture:old", Progression()));
            return bus;
        }

        static string Dump(object value) => GdJson.Stringify(value);

        [TestCase(0L)]
        [TestCase(12L)]
        [TestCase(12.0)]
        [TestCase(long.MaxValue)]
        public void RecordAppliedAppendsResolvedEventWithoutChangingDeliveryCounters(object amount)
        {
            TrainingEventBus bus = OrdinaryBus();
            GdDict row = Event();
            row["base_xp"] = amount;
            row["sequence"] = 901L;
            string inputBefore = Dump(row);
            bus.RecordApplied(row, "commit_a");

            Assert.AreEqual(2, bus.GetEventCount());
            Assert.AreEqual(7, bus.GetTotalXpDelivered());
            Assert.AreEqual(0, bus.GetDroppedCount());
            Assert.AreEqual(inputBefore, Dump(row), "The caller is not stamped or rewritten.");
            GdDict stored = (GdDict)bus.GetLog()[1];
            Assert.AreEqual("commit_a", stored.GetString("commit_id"));
            Assert.IsTrue(stored.GetBool("receipt_owned"));
            Assert.AreEqual(1L, stored.Get("sequence"));
            Assert.AreEqual(amount.GetType(), stored.Get("base_xp").GetType(), "Retain numeric representation.");
            Assert.AreEqual(amount, stored.Get("base_xp"));
            Assert.IsFalse(bus.IsKnown("resolved_external"), "Already-resolved recording does not rebind the catalog.");
        }

        [Test]
        public void RecordAppliedDoesNotInvokeEmitFiltersCallbacksOrGrantXp()
        {
            TrainingEventBus bus = Bus();
            PlayerProgressionState restored = Progression();
            restored.GrantXp("repair", 19);
            string progressionBefore = Dump(restored.GetSummary());
            bus.EventFilter = (eventId, targetId) => throw new Exception("EventFilter is an Emit gate.");
            bus.SkillGate = skill => throw new Exception("SkillGate is an Emit gate.");
            bus.OnEventResolved = row => throw new Exception("OnEventResolved may publish ordinary effects.");

            Assert.DoesNotThrow(() => bus.RecordApplied(Event(), "commit_a"));
            Assert.AreEqual(1, bus.GetEventCount());
            Assert.AreEqual(0, bus.GetTotalXpDelivered());
            Assert.AreEqual(0, bus.GetDroppedCount());
            Assert.AreEqual(0, bus.ReplayInto(restored));
            Assert.AreEqual(progressionBefore, Dump(restored.GetSummary()));
        }

        [Test]
        public void ExactRepeatIgnoresOnlyDerivedSequenceAndDoesNotAppend()
        {
            TrainingEventBus bus = Bus();
            GdDict row = Event();
            row["sequence"] = 999L;
            bus.RecordApplied(row, "commit_a");
            Assert.AreEqual(1, bus.GetEventCount());
            string before = Dump(bus.ToDict());
            GdDict repeat = row.DeepCopy();
            repeat["sequence"] = -77L;
            repeat["commit_id"] = "commit_a";
            repeat["receipt_owned"] = true;
            Assert.DoesNotThrow(() => bus.RecordApplied(repeat, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }

        [TestCase("event_id")]
        [TestCase("target_id")]
        [TestCase("skill_id")]
        [TestCase("base_xp")]
        [TestCase("category")]
        [TestCase("is_cross_training")]
        [TestCase("gated")]
        [TestCase("evidence")]
        [TestCase("opaque_numeric_type")]
        public void ChangedCanonicalPayloadWithSameCommitRejectsWithoutMutation(string field)
        {
            TrainingEventBus bus = Bus();
            GdDict row = Event();
            row["opaque_number"] = 1L;
            bus.RecordApplied(row, "commit_a");
            Assert.AreEqual(1, bus.GetEventCount());
            string before = Dump(bus.ToDict());
            GdDict changed = row.DeepCopy();
            switch (field)
            {
                case "base_xp": changed[field] = 13L; break;
                case "is_cross_training": case "gated": changed[field] = true; break;
                case "evidence": changed.GetDictOrEmpty(field).GetArrayOrEmpty("history").Add("changed"); break;
                case "opaque_numeric_type": changed["opaque_number"] = 1.0; break;
                default: changed[field] = "different"; break;
            }
            string changedBefore = Dump(changed);
            Assert.Throws<InvalidOperationException>(() => bus.RecordApplied(changed, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreEqual(changedBefore, Dump(changed));
        }

        [Test]
        public void ValidatedBaseXpIntegerAndJsonDoubleAreTheSameReceiptAmount()
        {
            TrainingEventBus bus = Bus();
            bus.RecordApplied(Event(), "commit_a");
            Assert.AreEqual(1, bus.GetEventCount());
            string before = Dump(bus.ToDict());
            GdDict repeated = Event();
            repeated["base_xp"] = 12.0;
            Assert.DoesNotThrow(() => bus.RecordApplied(repeated, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }

        [Test]
        public void LargeDistinctXpAmountsDoNotCompareThroughLossyDoubleConversion()
        {
            TrainingEventBus bus = Bus();
            GdDict row = Event();
            row["base_xp"] = 9007199254740993L;
            bus.RecordApplied(row, "commit_a");
            Assert.AreEqual(1, bus.GetEventCount());
            string before = Dump(bus.ToDict());
            GdDict changed = Event();
            changed["base_xp"] = 9007199254740992.0;
            Assert.Throws<InvalidOperationException>(() => bus.RecordApplied(changed, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }

        [Test]
        public void AdjacentLargeLongXpAmountsRemainConflictingAboveDoublePrecision()
        {
            TrainingEventBus bus = Bus();
            GdDict row = Event();
            row["base_xp"] = 9007199254740993L;
            bus.RecordApplied(row, "commit_a");
            Assert.AreEqual(1, bus.GetEventCount());
            string before = Dump(bus.ToDict());
            GdDict changed = Event();
            changed["base_xp"] = 9007199254740992L;
            Assert.Throws<InvalidOperationException>(() => bus.RecordApplied(changed, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }

        [TestCase("event_id")]
        [TestCase("target_id")]
        [TestCase("skill_id")]
        [TestCase("base_xp")]
        [TestCase("category")]
        [TestCase("is_cross_training")]
        [TestCase("gated")]
        public void EveryResolvedFieldIsRequiredWithoutDefaultGrantMetadata(string field)
        {
            TrainingEventBus bus = OrdinaryBus();
            string before = Dump(bus.ToDict());
            GdDict row = Event();
            row.Erase(field);
            string inputBefore = Dump(row);
            Assert.Throws<ArgumentException>(() => bus.RecordApplied(row, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreEqual(inputBefore, Dump(row));
        }

        [TestCase("event_id", "null")]
        [TestCase("event_id", "empty")]
        [TestCase("event_id", "integer")]
        [TestCase("target_id", "empty")]
        [TestCase("skill_id", "empty")]
        [TestCase("category", "null")]
        [TestCase("category", "integer")]
        [TestCase("base_xp", "negative")]
        [TestCase("base_xp", "fractional")]
        [TestCase("base_xp", "string")]
        [TestCase("base_xp", "boolean")]
        [TestCase("base_xp", "nan")]
        [TestCase("base_xp", "infinity")]
        [TestCase("base_xp", "overflow")]
        [TestCase("base_xp", "int64_upper")]
        [TestCase("is_cross_training", "string")]
        [TestCase("gated", "integer")]
        [TestCase("receipt_owned", "false")]
        [TestCase("commit_id", "mismatch")]
        [TestCase("commit_id", "null")]
        public void MalformedResolvedFieldsOrSuppliedReceiptIdentityReject(string field, string kind)
        {
            TrainingEventBus bus = OrdinaryBus();
            string before = Dump(bus.ToDict());
            GdDict row = Event();
            row[field] = InvalidValue(kind);
            object supplied = row.Get(field);
            Assert.Throws<ArgumentException>(() => bus.RecordApplied(row, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreSame(supplied, row.Get(field), "Invalid input is never rewritten.");
        }

        static object InvalidValue(string kind)
        {
            switch (kind)
            {
                case "null": return null;
                case "empty": return "";
                case "integer": return 42L;
                case "negative": return -1L;
                case "fractional": return 2.5;
                case "boolean": return true;
                case "false": return false;
                case "nan": return double.NaN;
                case "infinity": return double.PositiveInfinity;
                case "overflow": return 1E30;
                case "int64_upper": return 9223372036854775808.0;
                case "mismatch": return "another_commit";
                default: return "12";
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void MissingCommitArgumentRejectsWithoutTouchingPriorLog(string commitId)
        {
            TrainingEventBus bus = OrdinaryBus();
            string before = Dump(bus.ToDict());
            Assert.Throws<ArgumentException>(() => bus.RecordApplied(Event(), commitId));
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }

        [Test]
        public void NullEventRejectsWithoutTouchingPriorLog()
        {
            TrainingEventBus bus = OrdinaryBus();
            string before = Dump(bus.ToDict());
            Assert.Throws<ArgumentException>(() => bus.RecordApplied(null, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }

        [Test]
        public void CallerGetLogAndSummaryMutationsCannotRewriteReceiptOrDedupeIdentity()
        {
            TrainingEventBus bus = Bus();
            GdDict row = Event();
            GdDict original = row.DeepCopy();
            bus.RecordApplied(row, "commit_a");
            Assert.AreEqual(1, bus.GetEventCount());
            string before = Dump(bus.ToDict());
            row.GetDictOrEmpty("evidence").GetArrayOrEmpty("history").Add("caller_mutation");
            GdArray log = bus.GetLog();
            ((GdDict)log[0])["commit_id"] = "forged";
            ((GdDict)log[0]).GetDictOrEmpty("evidence").Clear();
            log.Clear();
            bus.ToDict().GetArrayOrEmpty("log").Clear();
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.DoesNotThrow(() => bus.RecordApplied(original, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NestedMutableDictionaryKeysRejectWithoutRetainingAliases(bool dictionaryKey)
        {
            TrainingEventBus bus = OrdinaryBus();
            string before = Dump(bus.ToDict());
            object key = dictionaryKey ? (object)new GdDict { { "key", "original" } } : GdArray.Of("original");
            GdDict row = Event();
            row["opaque"] = GdArray.Of(new GdDict { { "nested", new GdDict { { key, "value" } } } });
            string inputBefore = Dump(row);
            Assert.Throws<ArgumentException>(() => bus.RecordApplied(row, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreEqual(inputBefore, Dump(row));
            if (key is GdDict dict) dict["key"] = "changed";
            else ((GdArray)key)[0] = "changed";
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }

        [TestCase("dictionary")]
        [TestCase("array")]
        [TestCase("mixed")]
        public void CyclicReceiptExtensionsRejectBeforeAnyCopy(string kind)
        {
            // The empty RED stub does not copy. These inputs are never sent to old ApplySummary's recursive copier.
            TrainingEventBus bus = OrdinaryBus();
            string before = Dump(bus.ToDict());
            var dictionary = new GdDict();
            var array = new GdArray();
            if (kind == "dictionary") dictionary["self"] = dictionary;
            else { dictionary["values"] = array; array.Add(kind == "array" ? (object)array : dictionary); }
            GdDict row = Event();
            row["opaque"] = dictionary;
            Assert.Throws<ArgumentException>(() => bus.RecordApplied(row, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreSame(dictionary, row.Get("opaque"));
            if (kind == "dictionary") Assert.AreSame(dictionary, dictionary.Get("self"));
            else Assert.AreSame(kind == "array" ? (object)array : dictionary, array[0]);
        }

        [Test]
        public void ImmutableKeysAndSharedAcyclicMetadataRemainSupportedAndDefensive()
        {
            TrainingEventBus bus = Bus();
            var shared = new GdDict { { "values", GdArray.Of("original") } };
            GdDict row = Event();
            row["opaque"] = new GdDict { { 1L, shared }, { new Vec2i(2, 3), shared } };
            bus.RecordApplied(row, "commit_a");
            Assert.AreEqual(1, bus.GetEventCount());
            string before = Dump(bus.ToDict());
            shared.GetArrayOrEmpty("values").Clear();
            ((GdDict)bus.GetLog()[0]).GetDictOrEmpty("opaque").Clear();
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreEqual(2, ((GdDict)bus.GetLog()[0]).GetDictOrEmpty("opaque").Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RestoredReceiptRowsNeverReplayIntoAlreadyRestoredProgression(bool jsonRepresentation)
        {
            GdDict summary = Summary(GdArray.Of(Receipt()), 17, 2);
            if (jsonRepresentation) summary = (GdDict)GdJson.ParseString(Dump(summary));
            var bus = Bus();
            PlayerProgressionState progression = Progression();
            progression.GrantXp("repair", 19);
            string before = Dump(progression.GetSummary());
            Assert.IsTrue(bus.ApplySummary(summary));
            Assert.AreEqual(1, bus.GetEventCount());
            Assert.AreEqual(17, bus.GetTotalXpDelivered());
            Assert.AreEqual(2, bus.GetDroppedCount());
            Assert.AreEqual(0, bus.ReplayInto(progression));
            Assert.AreEqual(0, bus.ReplayInto(progression), "Replay is still record-only on subsequent requests.");
            Assert.AreEqual(before, Dump(progression.GetSummary()));
        }

        [Test]
        public void ImportedIdenticalRepeatedReceiptsAreRetainedButNeverDeliverXp()
        {
            GdDict first = Receipt();
            GdDict repeated = first.DeepCopy();
            repeated["sequence"] = 99L;
            repeated["base_xp"] = 12.0;
            TrainingEventBus bus = Bus();
            Assert.IsTrue(bus.ApplySummary(Summary(GdArray.Of(first, repeated))));
            Assert.AreEqual(2, bus.GetEventCount());
            Assert.AreEqual(0, bus.ReplayInto(Progression()));
            string before = Dump(bus.ToDict());
            bus.RecordApplied(Event(), "commit_a");
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }

        [Test]
        public void RestoredLogIsTheAuthorityForDuplicateAndConflictingCommitIdentity()
        {
            TrainingEventBus bus = Bus();
            Assert.IsTrue(bus.ApplySummary(Summary(GdArray.Of(Receipt()))));
            string before = Dump(bus.ToDict());
            Assert.DoesNotThrow(() => bus.RecordApplied(Event(), "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            GdDict conflict = Event();
            conflict["target_id"] = "another_target";
            Assert.Throws<InvalidOperationException>(() => bus.RecordApplied(conflict, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }

        [TestCase("missing_event")]
        [TestCase("missing_commit")]
        [TestCase("empty_commit")]
        [TestCase("marker_type")]
        [TestCase("marker_false")]
        [TestCase("missing_marker")]
        [TestCase("fractional_xp")]
        [TestCase("wrong_gate_type")]
        [TestCase("duplicate_conflict")]
        [TestCase("opaque_type_conflict")]
        [TestCase("mutable_key")]
        public void MalformedOrConflictingReceiptImportRejectsAllRowsWithoutChangingPriorLog(string kind)
        {
            TrainingEventBus bus = OrdinaryBus();
            string before = Dump(bus.ToDict());
            GdDict invalid = Receipt("commit_b");
            switch (kind)
            {
                case "missing_event": invalid.Erase("event_id"); break;
                case "missing_commit": invalid.Erase("commit_id"); break;
                case "empty_commit": invalid["commit_id"] = ""; break;
                case "marker_type": invalid["receipt_owned"] = "true"; break;
                case "marker_false": invalid["receipt_owned"] = false; break;
                case "missing_marker": invalid.Erase("receipt_owned"); break;
                case "fractional_xp": invalid["base_xp"] = 1.5; break;
                case "wrong_gate_type": invalid["gated"] = 1L; break;
                case "duplicate_conflict": invalid["commit_id"] = "commit_a"; invalid["target_id"] = "different"; break;
                case "opaque_type_conflict": invalid["commit_id"] = "commit_a"; invalid["opaque_number"] = 1.0; break;
                case "mutable_key": invalid["opaque"] = new GdDict { { GdArray.Of("key"), "value" } }; break;
            }
            GdDict valid = Receipt();
            if (kind == "opaque_type_conflict") valid["opaque_number"] = 1L;
            GdDict candidate = Summary(GdArray.Of(valid, invalid), 200, 7);
            string inputBefore = Dump(candidate);
            Assert.IsFalse(bus.ApplySummary(candidate));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreEqual(inputBefore, Dump(candidate));
            Assert.AreEqual(7, bus.ReplayInto(Progression()), "Failed import keeps the ordinary old replay behavior.");
        }

        [Test]
        public void ImportedReceiptMetadataIsDefensiveAcrossInputAndReadBoundaries()
        {
            GdDict row = Receipt();
            GdDict input = Summary(GdArray.Of(row));
            TrainingEventBus bus = Bus();
            Assert.IsTrue(bus.ApplySummary(input));
            string before = Dump(bus.ToDict());
            row.GetDictOrEmpty("evidence").GetArrayOrEmpty("history").Clear();
            input.GetArrayOrEmpty("log").Clear();
            ((GdDict)bus.GetLog()[0]).GetDictOrEmpty("evidence").Clear();
            bus.ToDict().GetArrayOrEmpty("log").Clear();
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreEqual(0, bus.ReplayInto(Progression()));
        }

        [Test]
        public void MixedOrdinaryAndReceiptLogReplaysOnlyTheOrdinaryEvent()
        {
            TrainingEventBus ordinary = OrdinaryBus();
            GdDict summary = ordinary.ToDict();
            summary.GetArrayOrEmpty("log").Add(Receipt());
            summary["event_count"] = 2L;
            TrainingEventBus restored = Bus();
            Assert.IsTrue(restored.ApplySummary(summary));
            PlayerProgressionState progression = Progression();
            Assert.AreEqual(7, restored.ReplayInto(progression));
            Assert.AreEqual(7, progression.GetSkillXp("repair"));
            Assert.AreEqual(7, restored.GetTotalXpDelivered());
        }

        [Test]
        public void ResetClearsReceiptIdentityAndCountersWithoutChangingOrdinaryCatalog()
        {
            TrainingEventBus bus = OrdinaryBus();
            bus.RecordApplied(Event(), "commit_a");
            Assert.AreEqual(2, bus.GetEventCount());
            bus.Reset();
            Assert.AreEqual(0, bus.GetEventCount());
            Assert.AreEqual(0, bus.GetTotalXpDelivered());
            Assert.AreEqual(0, bus.GetDroppedCount());
            Assert.IsTrue(bus.IsKnown("ordinary"));
            GdDict changed = Event();
            changed["target_id"] = "new_target_after_reset";
            Assert.DoesNotThrow(() => bus.RecordApplied(changed, "commit_a"));
            Assert.AreEqual(1, bus.GetEventCount());
            Assert.AreEqual("new_target_after_reset", ((GdDict)bus.GetLog()[0]).GetString("target_id"));
        }

        [Test]
        public void UnmarkedLegacyLogRetainsOrdinaryEmitGateAndReplayCompatibility()
        {
            TrainingEventBus bus = Bus();
            PlayerProgressionState progression = Progression();
            int callbacks = 0;
            bus.OnEventResolved = row => callbacks++;
            bus.EventFilter = (eventId, targetId) => targetId == "blocked";
            Assert.IsNull(bus.Emit("ordinary", "blocked", progression));
            Assert.IsNotNull(bus.Emit("ordinary", "accepted", progression));
            bus.SkillGate = skill => false;
            Assert.IsTrue(bus.Emit("ordinary", "gated", progression).GetBool("gated"));
            Assert.AreEqual(2, callbacks);
            Assert.AreEqual(1, bus.GetDroppedCount());
            Assert.AreEqual(7, bus.GetTotalXpDelivered());
            TrainingEventBus restored = Bus();
            Assert.IsTrue(restored.ApplySummary(bus.ToDict()));
            Assert.AreEqual(Dump(bus.ToDict()), Dump(restored.ToDict()));
            PlayerProgressionState fresh = Progression();
            Assert.AreEqual(7, restored.ReplayInto(fresh));
            Assert.AreEqual(7, fresh.GetSkillXp("repair"));
        }

        [TestCase("empty")]
        [TestCase("missing")]
        [TestCase("null")]
        [TestCase("non_array")]
        [TestCase("malformed_rows")]
        [TestCase("missing_one_receipt")]
        public void ExistingReceiptCannotBeForgottenByIncompleteImport(string kind)
        {
            TrainingEventBus bus = OrdinaryBus();
            bus.RecordApplied(Event(), "commit_a");
            GdDict second = Event();
            second["target_id"] = "fixture:part_b";
            if (kind == "missing_one_receipt") bus.RecordApplied(second, "commit_b");
            string before = Dump(bus.ToDict());
            GdDict candidate = Summary(new GdArray(), 0, 3);
            switch (kind)
            {
                case "missing": candidate.Erase("log"); break;
                case "null": candidate["log"] = null; break;
                case "non_array": candidate["log"] = "not_an_array"; break;
                case "malformed_rows": candidate["log"] = GdArray.Of("not_a_row", 42L); break;
                case "missing_one_receipt": candidate["log"] = GdArray.Of(Receipt()); break;
            }
            string inputBefore = Dump(candidate);

            Assert.IsFalse(bus.ApplySummary(candidate), "Import cannot forget any confirmed receipt identity, even when counters are reset.");
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreEqual(inputBefore, Dump(candidate));
            Assert.DoesNotThrow(() => bus.RecordApplied(Event(), "commit_a"));
            if (kind == "missing_one_receipt") Assert.DoesNotThrow(() => bus.RecordApplied(second, "commit_b"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            GdDict changed = Event();
            changed["target_id"] = "fixture:rewritten_part";
            Assert.Throws<InvalidOperationException>(() => bus.RecordApplied(changed, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreEqual(7, bus.ReplayInto(Progression()), "Rejected import retains ordinary replay and excludes receipt XP.");
        }

        [TestCase("target_id")]
        [TestCase("base_xp")]
        [TestCase("evidence")]
        public void ExistingReceiptCannotBeRewrittenBySelfConsistentImport(string field)
        {
            TrainingEventBus bus = OrdinaryBus();
            bus.RecordApplied(Event(), "commit_a");
            string before = Dump(bus.ToDict());
            GdDict replacement = Receipt();
            switch (field)
            {
                case "target_id": replacement["target_id"] = "fixture:rewritten_part"; break;
                case "base_xp": replacement["base_xp"] = 13L; break;
                case "evidence": replacement.GetDictOrEmpty("evidence").GetArrayOrEmpty("history").Add("rewritten"); break;
            }
            GdDict candidate = Summary(GdArray.Of(replacement), 200, 7);
            string inputBefore = Dump(candidate);

            Assert.IsFalse(bus.ApplySummary(candidate), "A self-consistent incoming log cannot reinterpret an already-confirmed receipt.");
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreEqual(inputBefore, Dump(candidate));
            Assert.DoesNotThrow(() => bus.RecordApplied(Event(), "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            GdDict changed = Event();
            changed["target_id"] = "fixture:rewritten_part";
            Assert.Throws<InvalidOperationException>(() => bus.RecordApplied(changed, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
            Assert.AreEqual(7, bus.ReplayInto(Progression()), "Rejected rewriting keeps the ordinary historical event available.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnchangedReceiptHistoryAndExtensionsImportWithoutXpReplay(bool extended)
        {
            TrainingEventBus bus = OrdinaryBus();
            bus.RecordApplied(Event(), "commit_a");
            GdDict retained = Receipt();
            retained["sequence"] = 901L;
            retained["base_xp"] = 12.0;
            GdArray incoming = GdArray.Of(retained);
            GdDict second = Receipt("commit_b");
            second["target_id"] = "fixture:part_b";
            if (extended) incoming.Add(second);
            GdDict candidate = Summary(incoming, 0, 3);
            string inputBefore = Dump(candidate);

            Assert.IsTrue(bus.ApplySummary(candidate), "Retained canonical receipts permit ordinary legacy rows and counters to be replaced.");
            Assert.AreEqual(extended ? 2 : 1, bus.GetEventCount());
            Assert.AreEqual(0, bus.GetTotalXpDelivered());
            Assert.AreEqual(3, bus.GetDroppedCount());
            Assert.AreEqual(inputBefore, Dump(candidate));
            GdDict stored = (GdDict)bus.GetLog()[0];
            Assert.AreEqual(901L, stored.Get("sequence"), "Sequence remains excluded from canonical receipt equality.");
            Assert.IsInstanceOf<double>(stored.Get("base_xp"), "Equivalent receipt-local numeric value retains the incoming representation.");
            Assert.AreEqual(0, bus.ReplayInto(Progression()));
            string before = Dump(bus.ToDict());
            Assert.DoesNotThrow(() => bus.RecordApplied(Event(), "commit_a"));
            if (extended)
            {
                GdDict secondEvent = Event();
                secondEvent["target_id"] = "fixture:part_b";
                Assert.DoesNotThrow(() => bus.RecordApplied(secondEvent, "commit_b"));
            }
            Assert.AreEqual(before, Dump(bus.ToDict()));
            GdDict changed = Event();
            changed["target_id"] = "fixture:rewritten_part";
            Assert.Throws<InvalidOperationException>(() => bus.RecordApplied(changed, "commit_a"));
            Assert.AreEqual(before, Dump(bus.ToDict()));
        }
    }
}
