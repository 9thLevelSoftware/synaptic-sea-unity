// ARTIFACT ONLY: copy to a NEW test path only after ROOT admits the explicit API seam.
// No product implementation or baseline test execution is implied by this draft.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
using Policy = SynapticSea.Core.Systems.PaidSnapshotCodec.Policy;

namespace SynapticSea.Tests.Session
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class PaidCraftSnapshotCapacityTests : PaidCraftFixture
    {
        // No System.Text.Json, file paths, frozen owner bundles, reflection to private APIs,
        // test caches, native storage, or large retained-history loops.
        static readonly Policy[] SnapshotPolicies = {
            Policy.OrdinaryRun, Policy.OrdinaryWorld, Policy.DiagnosticRun, Policy.DiagnosticWorld
        };
        IEngineInfo _previousEngine;
        [SetUp] public void CapacityEngine()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
        }
        [TearDown] public void RestoreCapacityEngine() { CoreServices.Engine = _previousEngine; }

        public static IEnumerable<object[]> RawNodeCases()
        { foreach (int n in new[] { 99999, 100000, 100001 }) foreach (bool read in Both()) yield return new object[] { n, read }; }
        public static IEnumerable<object[]> DepthCases()
        { foreach (int d in new[] { 127, 128, 129 }) foreach (bool read in Both()) yield return new object[] { d, read }; }
        public static IEnumerable<object[]> ExpansionCases()
        {
            foreach (int count in new[] { 33329, 33330 })
                foreach (Policy p in new[] { Policy.Raw, Policy.TypedOwner })
                    foreach (bool read in Both()) yield return new object[] { count, p, read };
        }
        public static IEnumerable<object[]> SemanticNodeCases()
        { foreach (int n in new[] { 100000, 100001 }) foreach (bool read in Both()) yield return new object[] { n, read }; }
        public static IEnumerable<object[]> SemanticDepthCases()
        { foreach (int d in new[] { 42, 128, 129 }) foreach (bool read in Both()) yield return new object[] { d, read }; }
        public static IEnumerable<object[]> SnapshotCases()
        { foreach (Policy p in SnapshotPolicies) foreach (bool read in Both()) yield return new object[] { p, read }; }
        public static IEnumerable<object[]> SnapshotNodeCases()
        {
            foreach (Policy p in SnapshotPolicies) foreach (int n in new[] { 99999, 100000, 100001 })
                foreach (bool read in Both()) yield return new object[] { p, n, read };
        }
        public static IEnumerable<object[]> SnapshotDepthCases()
        {
            foreach (Policy p in SnapshotPolicies) foreach (int d in new[] { 127, 128, 129 })
                foreach (bool read in Both()) yield return new object[] { p, d, read };
        }
        public static IEnumerable<object[]> SharedCases()
        {
            foreach (Policy p in new[] { Policy.Raw, Policy.TypedOwner })
                foreach (bool over in Both()) foreach (bool read in Both()) yield return new object[] { p, over, read };
        }
        public static IEnumerable<object[]> TypedMalformedCases()
        {
            foreach (string kind in new[] { "tag", "arity", "integer_overflow", "integer_noncanonical",
                "real_nan", "real_infinity", "real_overflow", "vector_overflow", "duplicate_key", "mutable_key", "extra_envelope" })
                foreach (bool read in Both()) yield return new object[] { kind, read };
        }
        public static IEnumerable<object[]> SnapshotMutantCases()
        {
            foreach (Policy p in SnapshotPolicies)
            {
                foreach (string kind in new[] { "missing_paid", "missing_domain", "paid_version", "forged_codec", "wrong_mode", "wrong_owner_mode",
                    "extra_envelope_field", "outer_version", "home_version", "misplaced_budget", "array_budget",
                    "nested_budget", "slot_huge", "proof_binding" })
                {
                    if (kind == "home_version" && !World(p)) continue;
                    foreach (bool read in Both()) yield return new object[] { p, kind, read };
                }
                foreach (string kind in Diagnostic(p) ? new[] { "missing_copy", "unequal_copy" } : new[] { "forbidden_copy" })
                    foreach (bool read in Both()) yield return new object[] { p, kind, read };
            }
        }
        static IEnumerable<bool> Both() { yield return false; yield return true; }

        [TestCaseSource(nameof(RawNodeCases))]
        public void RawValues_KeepOriginalNodeBudget(int nodes, bool read)
        {
            var graph = new GdDict { { "x", Repeat(null, nodes - 2) } };
            Assert.AreEqual(nodes, WireCount(graph));
            Check(new GdDict { { "x", 1L } }, Policy.Raw, read, true);
            Check(graph, Policy.Raw, read, nodes <= 100000);
        }

        [TestCaseSource(nameof(DepthCases))]
        public void RawDepth_KeepOriginalBudget(int depth, bool read)
        {
            GdDict graph = Chain(depth);
            Assert.AreEqual(depth, WireDepth(graph));
            Check(Chain(2), Policy.Raw, read, true);
            Check(graph, Policy.Raw, read, depth <= 128);
        }

        [TestCaseSource(nameof(ExpansionCases))]
        public void TypedRepresentation_DoesNotSpendRawSemanticBudget(int count, Policy policy, bool read)
        {
            GdDict semantic = new GdDict { { "x", Repeat(1L, count) } };
            GdDict wire = CoreControl(semantic);
            Assert.AreEqual(count + 3, SemanticCount(semantic));
            Assert.AreEqual(count == 33329 ? 99999 : 100002, WireCount(wire));
            Check(wire, policy, read, policy == Policy.TypedOwner || count == 33329);
        }

        [TestCaseSource(nameof(SemanticNodeCases))]
        public void TypedSemanticNodes_RespectOriginalCoreLimit(int nodes, bool read)
        {
            CoreControl(new GdDict { { "x", GdArray.Of(1L) } });
            GdDict semantic = new GdDict { { "x", Repeat(1L, nodes - 3) } };
            Assert.AreEqual(nodes, SemanticCount(semantic));
            GdDict wire = TypedWire(semantic); // Does not ask the failing Core encoder for invalid input.
            if (nodes <= 100000) Exact(wire, CoreControl(semantic));
            else
            {
                CoreReject(semantic);
                Assert.IsFalse(ComponentDomainCodec.TryDecode(wire, out _, out _));
            }
            Check(wire, Policy.TypedOwner, read, nodes <= 100000);
        }

        [TestCaseSource(nameof(SemanticDepthCases))]
        public void TypedSemanticDepth_IsDecodedDepth(int depth, bool read)
        {
            CoreControl(Chain(2));
            GdDict semantic = Chain(depth), wire = TypedWire(semantic);
            Assert.AreEqual(depth, WireDepth(semantic));
            Assert.AreEqual(3 * depth + 2, WireDepth(wire));
            if (depth <= 128) Exact(wire, CoreControl(semantic));
            else { CoreReject(semantic); Assert.IsFalse(ComponentDomainCodec.TryDecode(wire, out _, out _)); }
            Check(wire, Policy.TypedOwner, read, depth <= 128);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TypedMixedContainersAndLeaves_RetainExactTypes(bool read)
        {
            var semantic = new GdDict {
                { 1L, GdArray.Of(null, true, "", long.MinValue, long.MaxValue, 9007199254740993L) },
                { 1.0, new GdDict { { false, 0.30000000000000004 }, { "v", new Vec3(0.1, -2, 3) } } },
                { "1", GdArray.Of(new GdDict(), new GdArray(), double.Epsilon, double.MaxValue) }
            };
            Check(CoreControl(semantic), Policy.TypedOwner, read, true);
        }

        [TestCaseSource(nameof(TypedMalformedCases))]
        public void TypedMalformedShapeAndNumbers_RefuseAfterValidControl(string kind, bool read)
        {
            GdDict good = CoreControl(new GdDict { { "x", 1L } });
            Check(good, Policy.TypedOwner, read, true);
            GdDict bad = good.DeepCopy();
            var entries = ((GdArray)bad.Get("value"))[1] as GdArray;
            var pair = entries[0] as GdArray;
            switch (kind)
            {
                case "tag": pair[1] = GdArray.Of("unknown", 1L); break;
                case "arity": pair[1] = GdArray.Of("integer", "1", "extra"); break;
                case "integer_overflow": pair[1] = GdArray.Of("integer", "9223372036854775808"); break;
                case "integer_noncanonical": pair[1] = GdArray.Of("integer", "01"); break;
                case "real_nan": pair[1] = GdArray.Of("real", "NaN"); break;
                case "real_infinity": pair[1] = GdArray.Of("real", "Infinity"); break;
                case "real_overflow": pair[1] = GdArray.Of("real", "1e999"); break;
                case "vector_overflow": pair[1] = GdArray.Of("vector3", "1E+100", "0", "0"); break;
                case "duplicate_key": entries.Add(((GdArray)entries[0]).DeepCopy()); break;
                case "mutable_key": pair[0] = GdArray.Of("array", new GdArray()); break;
                case "extra_envelope": bad["extra"] = 0L; break;
                default: Assert.Fail(kind); break;
            }
            Assert.IsFalse(ComponentDomainCodec.TryDecode(bad, out _, out _), "Core remains the semantic oracle.");
            Check(bad, Policy.TypedOwner, read, false);
        }

        [TestCase("{\"x\":1,\"x\":2}")]
        [TestCase("{\"x\":1,\"\\u0078\":2}")]
        [TestCase("{\"x\":9223372036854775808}")]
        [TestCase("{\"x\":-9223372036854775809}")]
        [TestCase("{\"x\":1e999}")]
        [TestCase("{\"x\":-1e999}")]
        [TestCase("{\"x\":NaN}")]
        [TestCase("[]")]
        public void RawStrictReader_RejectsDuplicatesOverflowAndNonObjects(string bad)
        {
            Assert.IsNotNull(PaidSnapshotCodec.Parse("{\"x\":9223372036854775807}", Policy.Raw));
            Assert.IsNull(PaidSnapshotCodec.Parse(bad, Policy.Raw));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void RawWriter_RejectsNonfinite(int kind)
        {
            Check(new GdDict { { "x", double.MaxValue } }, Policy.Raw, false, true);
            double bad = kind == 0 ? double.NaN : kind == 1 ? double.PositiveInfinity : double.NegativeInfinity;
            WriterReject(new GdDict { { "x", bad } }, Policy.Raw);
        }

        [Test]
        public void RawWriter_RejectsNonstringKeys()
        {
            Check(new GdDict { { "1", 1L } }, Policy.Raw, false, true);
            WriterReject(new GdDict { { 1L, 1L } }, Policy.Raw);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TypedOwner_RequiresDictionarySemanticRoot(bool read)
        {
            Check(CoreControl(new GdDict()), Policy.TypedOwner, read, true);
            var bad = new GdDict { { "schema", "component_domain_codec_v1" }, { "value", GdArray.Of("array", new GdArray()) } };
            Assert.IsFalse(ComponentDomainCodec.TryDecode(bad, out _, out _));
            Check(bad, Policy.TypedOwner, read, false);
        }

        [TestCaseSource(nameof(SharedCases))]
        public void SharedAcyclicSubtrees_CountEveryOccurrence(Policy policy, bool over, bool read)
        {
            int size = policy == Policy.Raw ? (over ? 49999 : 49998) : (over ? 49998 : 49997);
            var child = Repeat(null, size);
            var semantic = new GdDict { { "a", child }, { "b", child } };
            Assert.IsTrue(ReferenceEquals(semantic.Get("a"), semantic.Get("b")));
            int count = policy == Policy.Raw ? WireCount(semantic) : SemanticCount(semantic);
            Assert.AreEqual(over ? 100001 : 99999, count);
            GdDict small = new GdDict { { "a", GdArray.Of(null) }, { "b", GdArray.Of(null) } };
            Check(policy == Policy.Raw ? small : CoreControl(small), policy, read, true);
            GdDict wire = policy == Policy.Raw ? semantic : TypedWire(semantic);
            if (policy == Policy.TypedOwner)
            {
                var entries = (GdArray)((GdArray)wire.Get("value"))[1];
                ((GdArray)entries[1])[1] = ((GdArray)entries[0])[1];
                Assert.IsTrue(ReferenceEquals(((GdArray)entries[0])[1], ((GdArray)entries[1])[1]),
                    "The writer receives actual aliased wire nodes and must charge both occurrences.");
                if (over) CoreReject(semantic); else CoreControl(semantic);
            }
            Check(wire, policy, read, !over);
            Assert.AreEqual(size, child.Count);
            Assert.IsTrue(ReferenceEquals(semantic.Get("a"), semantic.Get("b")));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RawWriter_RejectsAncestorCycles(bool array)
        {
            Check(new GdDict { { "x", GdArray.Of(1L) } }, Policy.Raw, false, true);
            var bad = new GdDict();
            if (array) { var a = new GdArray(); bad["x"] = a; a.Add(bad); }
            else bad["x"] = bad;
            WriterReject(bad, Policy.Raw);
            // A cycle has no finite independent JSON input. No recursive serializer is called on it.
        }

        [Test]
        public void TypedWriter_RejectsWireCycleBeforeUnboundedCoreTraversal()
        {
            GdDict good = CoreControl(new GdDict { { "x", new GdArray() } });
            Check(good, Policy.TypedOwner, false, true);
            var node = (GdArray)good.Get("value");
            var pair = (GdArray)((GdArray)node[1])[0];
            ((GdArray)((GdArray)pair[1])[1]).Add(node);
            WriterReject(good, Policy.TypedOwner); // Never call Core TryDecode or test JSON writer on this cycle.
        }

        [TestCase(-1, false)]
        [TestCase(-1, true)]
        [TestCase(6, false)]
        [TestCase(6, true)]
        public void UnknownPolicy_Refuses(int unknown, bool read)
        {
            var graph = new GdDict { { "x", 1L } };
            Check(graph, Policy.Raw, read, true);
            Check(graph, (Policy)unknown, read, false);
        }

        [Test]
        public void OneArgumentRaw_StillUsesRawLimitsAndExactValidBytes()
        {
            var graph = new GdDict { { "i", 9007199254740993L }, { "r", 1.0 }, { "empty", "" } };
            const string expected = "{\"i\":9007199254740993,\"r\":1.0,\"empty\":\"\"}";
            Assert.AreEqual(expected, PaidSnapshotCodec.Stringify(graph));
            Assert.AreEqual(expected, PaidSnapshotCodec.Stringify(graph, Policy.Raw));
            Exact(graph, PaidSnapshotCodec.Parse(expected));
            Exact(graph, PaidSnapshotCodec.Parse(expected, Policy.Raw));
            GdDict expanded = CoreControl(new GdDict { { "x", Repeat(1L, 33330) } });
            bool refused = false;
            try { PaidSnapshotCodec.Stringify(expanded); } catch (ArgumentException) { refused = true; }
            Assert.IsTrue(refused);
            Assert.IsNull(PaidSnapshotCodec.Parse(Json(expanded)));
        }

        [TestCaseSource(nameof(SnapshotCases))]
        public void GenuineSmallSession_PreservesAllRequiredCopiesAndProofs(Policy policy, bool read)
        {
            Control c = Small(policy);
            GdDict graph = c.Snapshot.DeepCopy();
            var slots = OwnerSlots(graph, policy);
            Assert.AreEqual(policy == Policy.DiagnosticWorld ? 3 : Diagnostic(policy) ? 2 : 1, slots.Count);
            foreach (GdDict slot in slots)
            {
                Assert.IsTrue(ComponentDomainCodec.TryDecode(slot, out GdDict owner, out string reason), reason);
                Valid(owner); Exact(c.Owner, owner);
                Assert.AreEqual(PaidCraftingState.Hash(c.Owner), PaidCraftingState.Hash(owner));
            }
            Check(graph, policy, read, true);
            Unchanged(c);
        }

        [TestCaseSource(nameof(SnapshotNodeCases))]
        public void CodecOnlyRawRemainder_CountsOwnerRootsOnce(Policy policy, int nodes, bool read)
        {
            Control c = Small(policy);
            GdDict graph = c.Snapshot.DeepCopy();
            var padding = new GdArray(); graph["capacity_test_padding"] = padding;
            int remainder = RawRemainder(graph, policy);
            Assert.Less(remainder, 99999, "Small fixture prerequisite; no guessed payload remainder.");
            for (int i = remainder; i < nodes; i++) padding.Add(null);
            Assert.AreEqual(nodes, RawRemainder(graph, policy));
            Assert.Greater(WireCount(graph), nodes);
            // Intentional codec-only padding; no claim that the padded snapshot passes coordinator schema.
            Check(graph, policy, read, nodes <= 100000);
            Unchanged(c);
        }

        [TestCaseSource(nameof(SnapshotDepthCases))]
        public void CodecOnlyRawDepth_DoesNotInheritTypedDepthAllowance(Policy policy, int depth, bool read)
        {
            Control c = Small(policy);
            GdDict graph = c.Snapshot.DeepCopy();
            graph["capacity_test_padding"] = Chain(depth - 1);
            Assert.AreEqual(depth, WireDepth(graph));
            Check(graph, policy, read, depth <= 128);
            Unchanged(c);
        }

        [TestCaseSource(nameof(SnapshotCases))]
        public void DecodedPathAndDictionaryOrder_SelectSameSlots(Policy policy, bool read)
        {
            Control c = Small(policy);
            var reversed = new GdDict();
            foreach (object key in c.Snapshot.Keys.Reverse()) reversed[key] = c.Snapshot[key];
            var padding = new GdArray(); reversed["capacity_test_padding"] = padding;
            int remainder = RawRemainder(reversed, policy);
            for (int i = remainder; i < 100000; i++) padding.Add(null);
            Assert.AreEqual(100000, RawRemainder(reversed, policy));
            string independent = Json(reversed)
                .Replace("\"crafting_summary\":", "\"\\u0063rafting_summary\":")
                .Replace("\"component_domain\":", "\"\\u0063omponent_domain\":");
            if (read) Exact(reversed, PaidSnapshotCodec.Parse(independent, policy));
            else Check(reversed, policy, false, true);
            Unchanged(c);
        }

        [TestCaseSource(nameof(SnapshotMutantCases))]
        public void SnapshotPolicy_RejectsMalformedAndBudgetMutants(Policy policy, string kind, bool read)
        {
            Control c = Small(policy); // Current session, Core owner, complete payload, proof, and codec positive.
            Check(c.Snapshot, policy, read, true);
            GdDict bad = c.Snapshot.DeepCopy(), home = Home(bad, policy), paid = PaidEnvelope(bad, policy);
            switch (kind)
            {
                case "missing_paid": home.GetDictOrEmpty("crafting_summary").Erase("paid_craft"); break;
                case "missing_domain": paid.Erase("domain"); break;
                case "paid_version": paid["schema_version"] = 2L; break;
                case "forged_codec": paid.GetDictOrEmpty("domain")["schema"] = "forged"; break;
                case "wrong_mode": paid["save_mode"] = Diagnostic(policy) ? PaidSnapshotCodec.OrdinaryMode : PaidSnapshotCodec.DiagnosticMode; break;
                case "wrong_owner_mode":
                {
                    Control opposite = Small(Diagnostic(policy) ? Policy.OrdinaryRun : Policy.DiagnosticRun);
                    Valid(opposite.Owner);
                    Assert.AreNotEqual(c.Owner.GetString("domain_mode"), opposite.Owner.GetString("domain_mode"));
                    StampOwners(bad, policy, opposite.Owner);
                    Unchanged(opposite);
                    break;
                }
                case "extra_envelope_field": paid["extra"] = 0L; break;
                case "outer_version": bad["slice_version"] = 999L; break;
                case "home_version": home["slice_version"] = 999L; break;
                case "missing_copy": bad.Erase("component_domain"); break;
                case "unequal_copy":
                {
                    Control other = Small(policy);
                    Provision(other.Session);
                    Start(other.Session, commandId: "capacity-alternate-running");
                    GdDict different = Capture(other.Session); Valid(different);
                    Assert.AreNotEqual(PaidCraftingState.Hash(c.Owner), PaidCraftingState.Hash(different));
                    bad["component_domain"] = ComponentDomainCodec.Encode(different);
                    break;
                }
                case "forbidden_copy": bad["component_domain"] = ComponentDomainCodec.Encode(c.Owner); break;
                case "slot_huge": paid["domain"] = Repeat(null, 550000); break;
                case "proof_binding":
                {
                    GdDict changed = c.Owner.DeepCopy();
                    Completion(changed).GetDictOrEmpty("reward_proof")["training_before_hash"] = new string('0', 64);
                    Assert.IsFalse(DomainBundle.TryCreate(changed, out _, out _), "Core proof binding must independently reject.");
                    StampOwners(bad, policy, changed);
                    break;
                }
                case "misplaced_budget":
                case "array_budget":
                case "nested_budget":
                {
                    GdDict wire = ComponentDomainCodec.Encode(c.Owner);
                    int copies = 100001 / WireCount(wire) + 1;
                    GdArray extras = Repeat(wire, 1);
                    if (kind == "misplaced_budget") bad["domain"] = extras;
                    else if (kind == "array_budget") bad["other"] = GdArray.Of(new GdDict {
                        { "crafting_summary", new GdDict { { "paid_craft", new GdDict { { "domain", extras } } } } } });
                    else
                    {
                        var imitation = new GdDict { { "paid_craft", new GdDict { { "domain", extras } } } };
                        var nestedHome = new GdDict { { "crafting_summary", imitation } };
                        bad["other"] = new GdDict { { "home_ship", nestedHome } };
                    }
                    Assert.LessOrEqual(RawRemainder(bad, policy), 100000);
                    Check(bad, policy, read, true); // A small misplaced copy is ordinary raw data, not a reserved-key protocol.
                    extras.Clear();
                    for (int i = 0; i < copies; i++) extras.Add(wire);
                    Assert.Greater(RawRemainder(bad, policy), 100000, "Misplaced copies have no representation exemption.");
                    break;
                }
                default: Assert.Fail(kind); break;
            }
            Check(bad, policy, read, false);
            Unchanged(c);
        }

        [TestCase(Policy.OrdinaryRun)]
        [TestCase(Policy.OrdinaryWorld)]
        [TestCase(Policy.DiagnosticRun)]
        [TestCase(Policy.DiagnosticWorld)]
        public void SnapshotReader_RejectsEscapedDuplicateSlotNames(Policy policy)
        {
            Control c = Small(policy);
            Check(c.Snapshot, policy, true, true);
            string text = Json(c.Snapshot);
            const string token = "\"crafting_summary\":";
            Assert.IsTrue(text.Contains(token));
            text = text.Replace(token, "\"\\u0063rafting_summary\":null," + token);
            Assert.IsNull(PaidSnapshotCodec.Parse(text, policy));
            Unchanged(c);
        }

        sealed class Control
        {
            public RunSession Session;
            public GdDict Owner, Snapshot, Payload, Selection;
        }
        Control Small(Policy policy)
        {
            RunSession s = Boot(components: Diagnostic(policy), paid: true);
            Assert.AreEqual(Diagnostic(policy), s.ComponentIntegrationEnabled);
            Provision(s); string job = Start(s, commandId: "capacity-small-completion");
            s.AdvanceCrafting(100);
            Assert.AreEqual("completed_delivered", Job(s, job).GetString("status"));
            GdDict owner = Capture(s); Valid(owner);
            Assert.AreEqual(3L, owner.GetInt("schema_version"));
            Assert.AreEqual(Diagnostic(policy) ? "components_and_craft" : "craft_only", owner.GetString("domain_mode"));
            Assert.AreEqual(1, Jobs(owner).Count);
            GdDict proof = Completion(owner).GetDictOrEmpty("reward_proof");
            Assert.IsNotEmpty(proof.GetString("training_before_hash"));
            Assert.IsNotEmpty(proof.GetString("training_after_hash"));
            Assert.IsTrue(s.ValidatePaidCraftingRestore(owner, out string reason), reason);
            Assert.IsTrue(s.RequestSaveToSlot("world", "world", "Capacity small control"),
                "Actual small current API save prerequisite: " + s.LastSaveResult.GetString("reason") + ":" + s.LastSaveResult.GetString("detail"));
            GdDict selected = s.SaveLoadService.SelectGeneration("world");
            Assert.IsTrue(selected.GetBool("ok"), selected.GetString("reason"));
            GdDict payload = selected.GetDictOrEmpty("payloads").DeepCopy();
            GdDict admitted = s.SaveLoadService.ComponentCoordinator().ValidateSuppliedPayload(payload, s.RunId, "world");
            Assert.IsTrue(admitted.GetBool("ok"), "Unmodified complete payload prerequisite: " + admitted.GetString("reason"));
            GdDict snapshot = PaidSnapshotCodec.Parse(payload.GetString(World(policy) ? "world_text" : "run_text"));
            Assert.IsNotNull(snapshot, "Small control must fit the existing one-argument Raw API.");
            Exact(owner, Capture(s));
            foreach (GdDict slot in OwnerSlots(snapshot, policy))
            {
                Assert.IsTrue(ComponentDomainCodec.TryDecode(slot, out GdDict decoded, out reason), reason);
                Valid(decoded); Exact(owner, decoded);
            }
            return new Control { Session = s, Owner = owner, Snapshot = snapshot, Payload = payload, Selection = selected.DeepCopy() };
        }
        static void Unchanged(Control c)
        {
            Exact(c.Owner, c.Session.CapturePaidCraftingDomain());
            Exact(c.Selection, c.Session.SaveLoadService.SelectGeneration("world"));
            // Codec-only mutants were never sent to Save/commit. No storage or native-byte claim.
        }
        static GdDict Completion(GdDict owner)
        {
            return owner.GetDictOrEmpty("receipts").Values.OfType<GdDict>()
                .Select(r => r.GetDictOrEmpty("result")).Single(r => r.GetString("operation") == "craft_complete");
        }
        static bool Diagnostic(Policy p) => p == Policy.DiagnosticRun || p == Policy.DiagnosticWorld;
        static bool World(Policy p) => p == Policy.OrdinaryWorld || p == Policy.DiagnosticWorld;
        static GdDict Home(GdDict graph, Policy p) => World(p) ? graph.GetDictOrEmpty("home_ship") : graph;
        static GdDict PaidEnvelope(GdDict graph, Policy p) => Home(graph, p).GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft");
        static List<GdDict> OwnerSlots(GdDict graph, Policy p)
        {
            var result = new List<GdDict> { PaidEnvelope(graph, p).GetDictOrEmpty("domain") };
            if (Diagnostic(p))
            {
                result.Add(graph.GetDictOrEmpty("component_domain"));
                if (World(p)) result.Add(Home(graph, p).GetDictOrEmpty("component_domain"));
            }
            Assert.IsTrue(result.All(x => !x.IsEmpty), "All fixed slots are present.");
            return result;
        }
        static void StampOwners(GdDict graph, Policy p, GdDict owner)
        {
            GdDict wire = ComponentDomainCodec.Encode(owner);
            PaidEnvelope(graph, p)["domain"] = wire.DeepCopy();
            if (Diagnostic(p))
            {
                graph["component_domain"] = wire.DeepCopy();
                if (World(p)) Home(graph, p)["component_domain"] = wire.DeepCopy();
            }
        }
        static int RawRemainder(GdDict graph, Policy p)
        {
            // Test oracle uses exact structural slots, never identity deduplication or schema discovery.
            int total = WireCount(graph);
            foreach (GdDict slot in OwnerSlots(graph, p)) total -= WireCount(slot) - 1;
            return total;
        }

        static GdArray Repeat(object value, int count)
        { var a = new GdArray(count); for (int i = 0; i < count; i++) a.Add(value); return a; }
        static GdDict Chain(int depth)
        {
            object v = 1L;
            for (int i = 0; i < depth; i++) v = new GdDict { { "x", v } };
            return (GdDict)v;
        }
        static int WireCount(object value)
        {
            int n = 1;
            if (value is GdDict d) foreach (var pair in d) n = checked(n + WireCount(pair.Value));
            else if (value is GdArray a) foreach (object child in a) n = checked(n + WireCount(child));
            return n;
        }
        static int SemanticCount(object value)
        {
            int n = 1;
            if (value is GdDict d) foreach (var pair in d) n = checked(n + SemanticCount(pair.Key) + SemanticCount(pair.Value));
            else if (value is GdArray a) foreach (object child in a) n = checked(n + SemanticCount(child));
            return n;
        }
        static int WireDepth(object value)
        {
            int depth = 0;
            if (value is GdDict d) foreach (var pair in d) depth = Math.Max(depth, 1 + WireDepth(pair.Value));
            else if (value is GdArray a) foreach (object child in a) depth = Math.Max(depth, 1 + WireDepth(child));
            return depth;
        }
        static GdDict CoreControl(GdDict semantic)
        {
            GdDict wire = ComponentDomainCodec.Encode(semantic);
            Assert.IsTrue(ComponentDomainCodec.TryDecode(wire, out GdDict decoded, out string reason), reason);
            Assert.IsTrue(V.VariantEquals(semantic, decoded));
            Exact(TypedWire(semantic), wire);
            return wire;
        }
        static void CoreReject(GdDict semantic)
        {
            bool rejected = false;
            try { ComponentDomainCodec.Encode(semantic); } catch (ArgumentException) { rejected = true; }
            Assert.IsTrue(rejected, "Existing semantic bound is unchanged.");
        }
        static GdDict TypedWire(GdDict semantic) => new GdDict {
            { "schema", "component_domain_codec_v1" }, { "value", Node(semantic) }
        };
        static GdArray Node(object value)
        {
            if (value == null) return GdArray.Of("null");
            if (value is string s) return GdArray.Of("text", s);
            if (value is bool b) return GdArray.Of("bool", b);
            if (value is long n) return GdArray.Of("integer", n.ToString(CultureInfo.InvariantCulture));
            if (value is double r) return GdArray.Of("real", r.ToString("R", CultureInfo.InvariantCulture));
            if (value is Vec3 v) return GdArray.Of("vector3", ((double)v.X).ToString("R", CultureInfo.InvariantCulture),
                ((double)v.Y).ToString("R", CultureInfo.InvariantCulture), ((double)v.Z).ToString("R", CultureInfo.InvariantCulture));
            var children = new GdArray();
            if (value is GdArray a)
            { foreach (object item in a) children.Add(Node(item)); return GdArray.Of("array", children); }
            foreach (var pair in (GdDict)value) children.Add(GdArray.Of(Node(pair.Key), Node(pair.Value)));
            return GdArray.Of("dictionary", children);
        }
        static void Check(GdDict graph, Policy policy, bool read, bool accepted)
        {
            // Read inputs are always built independently, even when the production writer refuses.
            string text = Json(graph);
            if (read)
            {
                GdDict result = PaidSnapshotCodec.Parse(text, policy);
                if (accepted) Exact(graph, result); else Assert.IsNull(result);
            }
            else if (accepted) Assert.AreEqual(text, PaidSnapshotCodec.Stringify(graph, policy));
            else WriterReject(graph, policy);
            Assert.AreEqual(text, Json(graph), "Codec must not mutate its input.");
        }
        static void WriterReject(GdDict graph, Policy policy)
        {
            bool rejected = false;
            try { PaidSnapshotCodec.Stringify(graph, policy); }
            catch (ArgumentException) { rejected = true; }
            Assert.IsTrue(rejected, "Writer must refuse without returning usable text.");
        }
        static void Exact(object expected, object actual)
        {
            Assert.IsTrue(ExactValue(expected, actual), "Exact graph types, values, key order and all occurrences.");
        }
        static bool ExactValue(object expected, object actual)
        {
            if (expected == null || actual == null) return expected == null && actual == null;
            if (expected.GetType() != actual.GetType()) return false;
            if (expected is GdDict d)
            {
                var other = (GdDict)actual;
                if (d.Count != other.Count) return false;
                for (int i = 0; i < d.Count; i++)
                    if (!ExactValue(d.Keys[i], other.Keys[i]) || !ExactValue(d.Values[i], other.Values[i])) return false;
                return true;
            }
            if (expected is GdArray a)
            {
                var other = (GdArray)actual;
                if (a.Count != other.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!ExactValue(a[i], other[i])) return false;
                return true;
            }
            // Vec3 remains float32 semantic metadata; it is never fed to the raw JSON text builder.
            return expected.Equals(actual);
        }
        static string Json(object value)
        { var output = new StringBuilder(); JsonValue(output, value); return output.ToString(); }
        static void JsonValue(StringBuilder output, object value)
        {
            if (value == null) { output.Append("null"); return; }
            if (value is string s) { Quote(output, s); return; }
            if (value is bool b) { output.Append(b ? "true" : "false"); return; }
            if (value is long n) { output.Append(n.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException("Test JSON must be finite.");
                string token = d == 0 ? "0.0" : d.ToString("R", CultureInfo.InvariantCulture);
                if (token.IndexOfAny(new[] { '.', 'e', 'E' }) < 0) token += ".0";
                output.Append(token); return;
            }
            bool first = true;
            if (value is GdDict dict)
            {
                output.Append('{');
                foreach (var pair in dict)
                {
                    if (!first) output.Append(','); first = false;
                    Quote(output, (string)pair.Key); output.Append(':'); JsonValue(output, pair.Value);
                }
                output.Append('}'); return;
            }
            output.Append('[');
            foreach (object child in (GdArray)value)
            { if (!first) output.Append(','); first = false; JsonValue(output, child); }
            output.Append(']');
        }
        static void Quote(StringBuilder output, string value)
        {
            new UTF8Encoding(false, true).GetByteCount(value);
            output.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (c < 32) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else output.Append(c);
                        break;
                }
            }
            output.Append('"');
        }
    }
}
