using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>Diagnostic unbound registry records; no catalog acquisition, live store or save migration.</summary>
    public class ItemInstanceStateTests
    {
        static GdDict Row(string id = "part_a", double condition = 0.23) => new GdDict
        {
            { "schema_version", 1L }, { "instance_id", id }, { "definition_id", "reactor_console" },
            { "item_form", "reactor_console" }, { "condition_state", "known" }, { "condition", condition },
            { "mass", 8.0 }, { "holder", "ship_a:slot:console" }, { "revision", 3L },
            { "origin", new GdDict { { "ship_id", "ship_a" }, { "witness", GdArray.Of("placed", "saved") } } },
            { "provenance", new GdDict { { "kind", "saved_exact" }, { "history", GdArray.Of(new GdDict { { "condition", condition } }) } } },
        };

        static GdDict Summary(params GdDict[] rows)
        {
            var instances = new GdDict();
            foreach (GdDict row in rows) instances[row.GetString("instance_id")] = row;
            return new GdDict { { "schema_version", 1L }, { "instances", instances } };
        }

        static ItemInstanceState Loaded()
        {
            var state = new ItemInstanceState();
            Assert.IsTrue(state.ApplySummary(Summary(Row())), "Valid initial diagnostic record must load.");
            return state;
        }

        static void RejectWithoutMutation(ItemInstanceState state, GdDict invalid)
        {
            string before = GdJson.Stringify(state.GetSummary());
            Assert.IsFalse(state.ApplySummary(invalid), "Malformed import must reject.");
            Assert.AreEqual(before, GdJson.Stringify(state.GetSummary()), "Entire previous registry must remain intact.");
            Assert.AreEqual(0.23, state.Get("part_a").GetFloat("condition"));
        }

        [Test]
        public void ConstructorReturnsCanonicalEmptyRegistryAndEmptyImportSucceeds()
        {
            var state = new ItemInstanceState();
            GdDict summary = state.GetSummary();
            Assert.AreEqual(1L, summary.GetInt("schema_version"));
            Assert.IsTrue(summary.Get("instances") is GdDict);
            Assert.IsTrue(summary.GetDictOrEmpty("instances").IsEmpty);
            Assert.IsTrue(state.ApplySummary(Summary()));
        }

        [Test]
        public void SameFormInstancesRetainSeparateIdentityConditionMassAndMetadata()
        {
            GdDict low = Row(), high = Row("part_b", 0.81);
            high["holder"] = "ship_b:cargo";
            low["extension"] = new GdDict { { "opaque", GdArray.Of(1L, "evidence") } };
            GdDict summary = Summary(low, high);
            summary["extension"] = "preserve_without_interpreting";
            var state = new ItemInstanceState();
            Assert.IsTrue(state.ApplySummary(summary));
            Assert.AreEqual(GdJson.Stringify(summary), GdJson.Stringify(state.GetSummary()));
            Assert.AreEqual(2, state.GetSummary().GetDictOrEmpty("instances").Count);
            Assert.AreEqual(0.23, state.Get("part_a").GetFloat("condition"));
            Assert.AreEqual(0.81, state.Get("part_b").GetFloat("condition"));
            Assert.AreEqual("ship_b:cargo", state.Get("part_b").GetString("holder"));
        }

        [TestCase(0.0)]
        [TestCase(1.0)]
        public void KnownConditionIncludesBothExactBoundaries(double condition)
        {
            var state = new ItemInstanceState();
            Assert.IsTrue(state.ApplySummary(Summary(Row(condition: condition))));
            Assert.AreEqual("known", state.Get("part_a").GetString("condition_state"));
            Assert.AreEqual(condition, state.Get("part_a").GetFloat("condition"));
        }

        [Test]
        public void UnknownExplicitNullIsDistinctFromKnownZero()
        {
            GdDict unknown = Row("unknown"), zero = Row("zero", 0);
            unknown["condition_state"] = "unknown";
            unknown["condition"] = null;
            var state = new ItemInstanceState();
            Assert.IsTrue(state.ApplySummary(Summary(unknown, zero)));
            GdDict restored = state.Get("unknown");
            Assert.IsTrue(restored.Has("condition"));
            Assert.IsNull(restored.Get("condition"));
            Assert.AreEqual("unknown", restored.GetString("condition_state"));
            Assert.AreEqual("known", state.Get("zero").GetString("condition_state"));
            Assert.AreEqual(0L, state.Get("zero").GetInt("condition"));
        }

        [Test]
        public void ApplySummaryReplacesWithoutMergingOrGeneratingIds()
        {
            ItemInstanceState state = Loaded();
            Assert.IsTrue(state.ApplySummary(Summary(Row("replacement", 0.81))));
            Assert.IsTrue(state.Get("part_a").IsEmpty);
            Assert.AreEqual("replacement", state.Get("replacement").GetString("instance_id"));
            Assert.AreEqual(1, state.GetSummary().GetDictOrEmpty("instances").Count);
            Assert.IsTrue(state.ApplySummary(Summary()));
            Assert.IsTrue(state.GetSummary().GetDictOrEmpty("instances").IsEmpty);
        }

        [Test]
        public void ValidRowThenInvalidRowCannotPartiallyReplaceExistingRegistry()
        {
            ItemInstanceState state = Loaded();
            GdDict first = Row("new_valid", 0.81), second = Row("new_invalid");
            second["mass"] = 0.0;
            RejectWithoutMutation(state, Summary(first, second));
            Assert.IsTrue(state.Get("new_valid").IsEmpty);
        }

        [Test]
        public void ImportCopiesCallerSummaryAndNestedOriginProvenance()
        {
            GdDict input = Summary(Row());
            var state = new ItemInstanceState();
            Assert.IsTrue(state.ApplySummary(input));
            string before = GdJson.Stringify(state.GetSummary());
            GdDict row = input.GetDictOrEmpty("instances").GetDictOrEmpty("part_a");
            row["holder"] = "attacker";
            row.GetDictOrEmpty("origin").GetArrayOrEmpty("witness").Add("changed");
            ((GdDict)row.GetDictOrEmpty("provenance").GetArrayOrEmpty("history")[0])["condition"] = 1.0;
            input.GetDictOrEmpty("instances").Clear();
            Assert.AreEqual(before, GdJson.Stringify(state.GetSummary()));
        }

        [Test]
        public void GetReturnsDeepDefensiveRecord()
        {
            ItemInstanceState state = Loaded();
            string before = GdJson.Stringify(state.GetSummary());
            GdDict exposed = state.Get("part_a");
            exposed["condition"] = 1.0;
            exposed.GetDictOrEmpty("origin").GetArrayOrEmpty("witness").Clear();
            ((GdDict)exposed.GetDictOrEmpty("provenance").GetArrayOrEmpty("history")[0])["condition"] = 1.0;
            Assert.AreEqual(before, GdJson.Stringify(state.GetSummary()));
        }

        [Test]
        public void GetSummaryReturnsDeepDefensiveRegistry()
        {
            ItemInstanceState state = Loaded();
            string before = GdJson.Stringify(state.GetSummary());
            GdDict exposed = state.GetSummary();
            GdDict row = exposed.GetDictOrEmpty("instances").GetDictOrEmpty("part_a");
            row["mass"] = 1.0;
            row.GetDictOrEmpty("provenance").GetArrayOrEmpty("history").Clear();
            exposed.GetDictOrEmpty("instances").Clear();
            exposed["schema_version"] = 2L;
            Assert.AreEqual(before, GdJson.Stringify(state.GetSummary()));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OriginAndProvenancePreserveTheirExplicitStringOrDictionaryShape(bool dictionary)
        {
            GdDict row = Row();
            row["origin"] = dictionary ? (object)new GdDict { { "opaque", "source" } } : "saved:source";
            row["provenance"] = dictionary ? (object)new GdDict { { "opaque", "exact" } } : "saved_exact";
            var state = new ItemInstanceState();
            Assert.IsTrue(state.ApplySummary(Summary(row)));
            Assert.AreEqual(GdJson.Stringify(row), GdJson.Stringify(state.Get("part_a")));
        }

        [Test]
        public void MissingLookupIsEmptyAndCannotIntroduceIdentity()
        {
            var state = new ItemInstanceState();
            Assert.IsTrue(state.Get("absent").IsEmpty);
            Assert.IsTrue(state.Get(null).IsEmpty);
            state.Get("absent")["instance_id"] = "minted";
            Assert.IsTrue(state.Get("minted").IsEmpty);
        }

        [TestCase("schema_version")]
        [TestCase("instance_id")]
        [TestCase("definition_id")]
        [TestCase("item_form")]
        [TestCase("condition_state")]
        [TestCase("condition")]
        [TestCase("mass")]
        [TestCase("holder")]
        [TestCase("revision")]
        [TestCase("origin")]
        [TestCase("provenance")]
        public void MissingRequiredRowFieldRejectsWithoutDefaults(string field)
        {
            ItemInstanceState state = Loaded();
            GdDict candidate = Summary(Row());
            candidate.GetDictOrEmpty("instances").GetDictOrEmpty("part_a").Erase(field);
            RejectWithoutMutation(state, candidate);
        }

        [TestCase("instance_id")]
        [TestCase("definition_id")]
        [TestCase("item_form")]
        [TestCase("holder")]
        public void BlankIdentifiersCannotReplaceRegistry(string field)
        {
            ItemInstanceState state = Loaded();
            GdDict candidate = Summary(Row());
            candidate.GetDictOrEmpty("instances").GetDictOrEmpty("part_a")[field] = " \t\r\n";
            RejectWithoutMutation(state, candidate);
        }

        [TestCase("instance_id")]
        [TestCase("definition_id")]
        [TestCase("item_form")]
        [TestCase("holder")]
        public void NumericIdentifiersCannotReplaceRegistry(string field)
        {
            ItemInstanceState state = Loaded();
            GdDict candidate = Summary(Row());
            candidate.GetDictOrEmpty("instances").GetDictOrEmpty("part_a")[field] = 42L;
            RejectWithoutMutation(state, candidate);
        }

        [TestCase("known_negative")]
        [TestCase("known_above_one")]
        [TestCase("known_nan")]
        [TestCase("known_positive_infinity")]
        [TestCase("known_negative_infinity")]
        [TestCase("known_string")]
        [TestCase("known_boolean")]
        [TestCase("known_null")]
        [TestCase("unknown_numeric")]
        [TestCase("unknown_string_null")]
        [TestCase("mass_zero")]
        [TestCase("mass_negative")]
        [TestCase("mass_nan")]
        [TestCase("mass_positive_infinity")]
        [TestCase("mass_string")]
        [TestCase("mass_boolean")]
        [TestCase("mass_null")]
        [TestCase("revision_negative")]
        [TestCase("revision_fraction")]
        [TestCase("revision_string")]
        [TestCase("revision_boolean")]
        [TestCase("revision_null")]
        [TestCase("schema_zero")]
        [TestCase("schema_future")]
        [TestCase("schema_string")]
        [TestCase("schema_float")]
        [TestCase("state_blank")]
        [TestCase("state_future")]
        [TestCase("state_boolean")]
        [TestCase("origin_empty_string")]
        [TestCase("origin_empty_dictionary")]
        [TestCase("origin_array")]
        [TestCase("provenance_empty_string")]
        [TestCase("provenance_empty_dictionary")]
        [TestCase("provenance_boolean")]
        public void MalformedRowCannotReplaceLoadedRegistry(string invalid)
        {
            ItemInstanceState state = Loaded();
            GdDict row = Row();
            switch (invalid)
            {
                case "known_negative": row["condition"] = -0.01; break;
                case "known_above_one": row["condition"] = 1.01; break;
                case "known_nan": row["condition"] = double.NaN; break;
                case "known_positive_infinity": row["condition"] = double.PositiveInfinity; break;
                case "known_negative_infinity": row["condition"] = double.NegativeInfinity; break;
                case "known_string": row["condition"] = "0.23"; break;
                case "known_boolean": row["condition"] = true; break;
                case "known_null": row["condition"] = null; break;
                case "unknown_numeric": row["condition_state"] = "unknown"; row["condition"] = 0L; break;
                case "unknown_string_null": row["condition_state"] = "unknown"; row["condition"] = "null"; break;
                case "mass_zero": row["mass"] = 0L; break;
                case "mass_negative": row["mass"] = -1.0; break;
                case "mass_nan": row["mass"] = double.NaN; break;
                case "mass_positive_infinity": row["mass"] = double.PositiveInfinity; break;
                case "mass_string": row["mass"] = "8"; break;
                case "mass_boolean": row["mass"] = true; break;
                case "mass_null": row["mass"] = null; break;
                case "revision_negative": row["revision"] = -1L; break;
                case "revision_fraction": row["revision"] = 3.5; break;
                case "revision_string": row["revision"] = "3"; break;
                case "revision_boolean": row["revision"] = true; break;
                case "revision_null": row["revision"] = null; break;
                case "schema_zero": row["schema_version"] = 0L; break;
                case "schema_future": row["schema_version"] = 2L; break;
                case "schema_string": row["schema_version"] = "1"; break;
                case "schema_float": row["schema_version"] = 1.0; break;
                case "state_blank": row["condition_state"] = ""; break;
                case "state_future": row["condition_state"] = "estimated"; break;
                case "state_boolean": row["condition_state"] = true; break;
                case "origin_empty_string": row["origin"] = " \t"; break;
                case "origin_empty_dictionary": row["origin"] = new GdDict(); break;
                case "origin_array": row["origin"] = GdArray.Of("source"); break;
                case "provenance_empty_string": row["provenance"] = ""; break;
                case "provenance_empty_dictionary": row["provenance"] = new GdDict(); break;
                case "provenance_boolean": row["provenance"] = true; break;
                default: Assert.Fail("Unknown diagnostic case " + invalid); break;
            }
            RejectWithoutMutation(state, Summary(row));
        }

        [TestCase("null_summary")]
        [TestCase("missing_schema")]
        [TestCase("future_schema")]
        [TestCase("string_schema")]
        [TestCase("missing_instances")]
        [TestCase("array_instances")]
        [TestCase("non_dictionary_row")]
        [TestCase("numeric_key")]
        [TestCase("mismatched_key")]
        [TestCase("duplicate_record_identity")]
        public void MalformedSummaryCannotReplaceLoadedRegistry(string invalid)
        {
            ItemInstanceState state = Loaded();
            GdDict candidate = Summary(Row());
            switch (invalid)
            {
                case "null_summary": candidate = null; break;
                case "missing_schema": candidate.Erase("schema_version"); break;
                case "future_schema": candidate["schema_version"] = 2L; break;
                case "string_schema": candidate["schema_version"] = "1"; break;
                case "missing_instances": candidate.Erase("instances"); break;
                case "array_instances": candidate["instances"] = GdArray.Of(Row()); break;
                case "non_dictionary_row": candidate.GetDictOrEmpty("instances")["part_a"] = "row"; break;
                case "numeric_key": candidate["instances"] = new GdDict { { 42L, Row() } }; break;
                case "mismatched_key": candidate["instances"] = new GdDict { { "other", Row() } }; break;
                case "duplicate_record_identity": candidate.GetDictOrEmpty("instances")["second_key"] = Row(); break;
                default: Assert.Fail("Unknown diagnostic case " + invalid); break;
            }
            RejectWithoutMutation(state, candidate);
        }

        [TestCase("origin", false)]
        [TestCase("origin", true)]
        [TestCase("provenance", false)]
        [TestCase("provenance", true)]
        [TestCase("row_extension", false)]
        [TestCase("row_extension", true)]
        [TestCase("summary_extension", false)]
        [TestCase("summary_extension", true)]
        public void MutableKeysNestedThroughMetadataArraysRejectWithoutInputOrRegistryMutation(string location, bool dictionaryKey)
        {
            ItemInstanceState state = Loaded();
            string before = GdJson.Stringify(state.GetSummary());
            object key = dictionaryKey ? (object)new GdDict { { "key", "original" } } : GdArray.Of("original");
            var keyed = new GdDict { { key, "evidence" } };
            var metadata = new GdDict { { "nested", GdArray.Of(new GdDict { { "deeper", keyed } }) } };
            GdDict row = Row("candidate", 0.81);
            GdDict candidate = Summary(row);
            if (location == "summary_extension") candidate["opaque_extension"] = metadata;
            else if (location == "row_extension") row["opaque_extension"] = metadata;
            else row[location] = metadata;
            string inputBefore = GdJson.Stringify(candidate);

            Assert.IsFalse(state.ApplySummary(candidate), "Mutable dictionary keys cannot enter a defensive snapshot.");
            Assert.AreEqual(inputBefore, GdJson.Stringify(candidate), "Rejected import cannot rewrite caller-owned input.");
            Assert.AreEqual(before, GdJson.Stringify(state.GetSummary()), "Previously valid registry must remain complete.");
            if (key is GdDict dictionary) dictionary["key"] = "changed";
            else ((GdArray)key)[0] = "changed";
            Assert.AreEqual(before, GdJson.Stringify(state.GetSummary()), "Mutating rejected input cannot reach retained state.");
            Assert.IsTrue(state.Get("candidate").IsEmpty);
            GdDict returned = state.Get("part_a");
            returned.GetDictOrEmpty("origin").GetArrayOrEmpty("witness").Clear();
            state.GetSummary().GetDictOrEmpty("instances").Clear();
            Assert.AreEqual(before, GdJson.Stringify(state.GetSummary()), "Both read boundaries remain defensive after rejection.");
        }

        [Test]
        public void ImmutableScalarAndVectorMetadataKeysRemainSupported()
        {
            GdDict row = Row();
            row["origin"] = new GdDict
            {
                { "source", "string" }, { 42L, "integer" }, { 3.5, "floating" }, { true, "boolean" },
                { new Vec2i(1, 2), "vector2" }, { new Vec3(1, 2, 3), "vector3" },
            };
            var state = new ItemInstanceState();
            Assert.IsTrue(state.ApplySummary(Summary(row)), "Immutable metadata keys do not expose mutable owner aliases.");
            Assert.AreEqual(GdJson.Stringify(row), GdJson.Stringify(state.Get("part_a")));
            Assert.AreEqual(6, state.Get("part_a").GetDictOrEmpty("origin").Count);
        }

        [Test]
        public void SharedAcyclicMetadataReferencesImportAsIndependentDefensiveCopies()
        {
            var shared = new GdDict { { "events", GdArray.Of("original") } };
            GdDict row = Row();
            row["origin"] = new GdDict { { "shared", shared } };
            row["provenance"] = new GdDict { { "shared", shared } };
            var state = new ItemInstanceState();
            Assert.IsTrue(state.ApplySummary(Summary(row)));
            string before = GdJson.Stringify(state.GetSummary());
            shared.GetArrayOrEmpty("events").Add("caller_mutation");
            state.Get("part_a").GetDictOrEmpty("origin").GetDictOrEmpty("shared").GetArrayOrEmpty("events").Clear();
            Assert.AreEqual(before, GdJson.Stringify(state.GetSummary()));
            Assert.AreEqual("original", state.Get("part_a").GetDictOrEmpty("provenance").GetDictOrEmpty("shared").GetArrayOrEmpty("events")[0]);
        }

        // Added after the guard: the pre-guard recursive copier cannot safely execute cyclic inputs.
        [TestCase("dictionary")]
        [TestCase("array")]
        [TestCase("mixed")]
        public void CyclicOpaqueValuesRejectWithoutChangingInputOrRetainedRegistry(string cycleKind)
        {
            ItemInstanceState state = Loaded();
            string before = GdJson.Stringify(state.GetSummary());
            GdDict row = Row("candidate");
            GdDict candidate = Summary(row);
            var metadata = new GdDict();
            var array = new GdArray();
            if (cycleKind == "dictionary") metadata["self"] = metadata;
            else
            {
                metadata["events"] = array;
                array.Add(cycleKind == "array" ? (object)array : metadata);
            }
            row["opaque_extension"] = metadata;

            Assert.IsFalse(state.ApplySummary(candidate));
            Assert.AreEqual(before, GdJson.Stringify(state.GetSummary()));
            Assert.IsTrue(state.Get("candidate").IsEmpty);
            Assert.AreSame(row, candidate.GetDictOrEmpty("instances").Get("candidate"));
            Assert.AreSame(metadata, row.Get("opaque_extension"));
            if (cycleKind == "dictionary") Assert.AreSame(metadata, metadata.Get("self"));
            else
            {
                Assert.AreSame(array, metadata.Get("events"));
                Assert.AreSame(cycleKind == "array" ? (object)array : metadata, array[0]);
            }
        }
    }
}
