using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    // Functional witnesses for the admitted optimization; no timing thresholds or new production API.
    public class PaidCraftHistoryValidationTests : PaidCraftFixture
    {
        static void Multiple(Action action)
        {
#if SYNAPTIC_DOTNET_TESTS
            Assert.Multiple(action.Invoke);
#else
            action();
#endif
        }
        static void Exact(object a, object b, string message)
            => Assert.AreEqual(PaidCraftingState.Hash(a), PaidCraftingState.Hash(b), message);
        static DomainTransactionCoordinator Owner(RunSession s)
            => (DomainTransactionCoordinator)typeof(RunSession).GetField("_componentDomain", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(s);
        static GdDict Direct(RunSession s) => new GdDict {
            { "inventory", s.InventoryState.GetSummary() }, { "progression", s.PlayerProgression.GetSummary() },
            { "training", s.TrainingEventBus.ToDict() }, { "crafting", s.CraftingState.GetSummary() },
            { "field", s.FieldCraftingState.GetSummary() }, { "knowledge", s.RecipeKnowledge.GetSummary() },
            { "spoilage", s.SpoilageState.GetSummary() }
        };
        static GdDict History(GdDict d) => Paid(d).GetDictOrEmpty("reward_history");
        static GdDict Effect(GdDict d, string id) => d.GetDictOrEmpty("receipts").GetDictOrEmpty(id).GetDictOrEmpty("result");
        static GdDict Proof(GdDict d, string id) => Effect(d, id).GetDictOrEmpty("reward_proof");
        static GdDict Summary(GdArray rows, long dropped = 0, long xp = 0)
            => new GdDict { { "log", rows.DeepCopy() }, { "dropped", dropped }, { "xp_total", xp }, { "event_count", (long)rows.Count } };
        static GdDict Ordinary(string text) => new GdDict {
            { "opaque", text }, { "negative_legacy_xp", -7L }, { "nested", GdArray.Of("雪\n\"\\", 0.30000000000000004, true, null) }
        };
        // Independent test construction of the published schema, not the optimized internal verifier.
        static GdDict Intern(GdDict d, GdDict training)
        {
            string tip = ""; long count = 0;
            foreach (object row in training.GetArrayOrEmpty("log"))
            {
                var node = new GdDict { { "schema_version", 1L }, { "parent_hash", tip }, { "count", ++count }, { "row", V.DeepCopy(row) } };
                tip = PaidCraftingState.Hash(node); History(d).GetDictOrEmpty("training_nodes")[tip] = node;
            }
            return new GdDict { { "schema_version", 1L }, { "tip_hash", tip }, { "count", count },
                { "dropped", training.Get("dropped") }, { "xp_total", training.Get("xp_total") }, { "event_count", training.Get("event_count") } };
        }
        static GdDict Materialize(GdDict d, GdDict reference)
        {
            var rows = new System.Collections.Generic.List<object>(); string tip = reference.GetString("tip_hash");
            for (long count = reference.GetInt("count"); count > 0; count--)
            {
                GdDict node = History(d).GetDictOrEmpty("training_nodes").GetDictOrEmpty(tip);
                Assert.AreEqual(count, node.GetInt("count"), "Independent test prefix construction");
                rows.Add(V.DeepCopy(node.Get("row"))); tip = node.GetString("parent_hash");
            }
            Assert.IsEmpty(tip); rows.Reverse(); return Summary(new GdArray(rows), reference.GetInt("dropped"), reference.GetInt("xp_total"));
        }
        static void References(GdDict d, string commit, GdDict before, GdDict after)
        {
            GdDict proof = Proof(d, commit);
            proof["training_before_ref"] = Intern(d, before); proof["training_after_ref"] = Intern(d, after);
            proof["training_before_hash"] = PaidCraftingState.Hash(before); proof["training_after_hash"] = PaidCraftingState.Hash(after);
        }
        static void RewritePrefix(GdDict d, string commit, GdArray prefix)
        {
            GdDict proof = Proof(d, commit), effect = Effect(d, commit);
            GdDict before = Materialize(d, proof.GetDictOrEmpty("training_before_ref"));
            GdDict after = Materialize(d, proof.GetDictOrEmpty("training_after_ref"));
            GdDict nodes = History(d).GetDictOrEmpty("training_nodes");
            // This helper is used only after a real Reset made the completion historical. Remove its
            // obsolete sequence-bound node, then intern its new exact row at the new prefix position.
            foreach (object key in nodes.Keys.ToArray())
                if (nodes.GetDictOrEmpty(key).GetDictOrEmpty("row").GetString("commit_id") == commit) nodes.Erase(key);
            GdArray tail = prefix.DeepCopy();
            if (effect.Get("training_record") is GdDict record)
            { record["sequence"] = (long)prefix.Count; tail.Add(record.DeepCopy()); }
            References(d, commit, Summary(prefix, before.GetInt("dropped"), before.GetInt("xp_total")),
                Summary(tail, after.GetInt("dropped"), after.GetInt("xp_total")));
        }
        static void Positive(RunSession s, GdDict d)
        {
            Valid(d); Assert.IsTrue(s.ValidatePaidCraftingRestore(d, out string reason), reason);
        }
        static void Reject(RunSession s, GdDict bad, string message)
        {
            GdDict direct = Direct(s), owner = Owner(s).GetSummary();
            bool schema = DomainBundle.TryCreate(bad, out _, out _), preflight = s.ValidatePaidCraftingRestore(bad, out _), applied = s.RestorePaidCraftingDomain(bad);
            Multiple(() => {
                Assert.IsFalse(schema, message + ": independent domain"); Assert.IsFalse(preflight, message + ": session preflight"); Assert.IsFalse(applied, message + ": restore");
                Exact(direct, Direct(s), message + ": direct participants unchanged"); Exact(owner, Owner(s).GetSummary(), message + ": owner unchanged without refreshing it");
            });
        }
        static void Policy(RunSession s, string policy)
        {
            s.TrainingEventBus.EventFilter = (evt, target) => policy == "filtered";
            s.TrainingEventBus.SkillGate = skill => policy != "gated";
        }
        static GdDict Complete(RunSession s, string policy = "accepted", string command = "history-complete", string recipe = Recipe)
        {
            Policy(s, policy);
            if (policy == "none")
            {
                // Explicit test-only route: real plating recipe/output at a real medbay, which has no
                // reward mapping for non-stimulant output. No authored catalog bytes are changed.
                s.CraftingState.GetRecipe(recipe)["station_kind"] = "medbay";
            }
            Provision(s, recipe); string job = Start(s, recipe, command); s.AdvanceCrafting(100);
            Assert.AreEqual("completed_delivered", Job(s, job).GetString("status"), "Actual accepted payment and completion prerequisite");
            GdDict result = s.RetryPaidCraft(job, "read-" + command); Assert.IsTrue(result.GetBool("committed"));
            Assert.AreEqual(policy, result.GetDictOrEmpty("result").GetDictOrEmpty("reward_proof").GetString("training_outcome"));
            Positive(s, Capture(s)); return result;
        }
        static GdDict Historical(RunSession s, GdDict result)
        {
            s.TrainingEventBus.Reset(); GdDict d = Capture(s); Positive(s, d);
            Assert.AreEqual(0L, History(d).GetDictOrEmpty("current_training_ref").GetInt("count"));
            Assert.IsTrue(d.GetDictOrEmpty("receipts").Has(result.GetString("commit_id"))); return d;
        }

        [TestCase("accepted")]
        [TestCase("gated")]
        [TestCase("filtered")]
        [TestCase("none")]
        public void OpaqueOrdinaryPrefix_ExactHashesSequenceCountersAndReplay(string policy)
        {
            RunSession s = Boot(); Assert.IsTrue(s.TrainingEventBus.ApplySummary(Summary(GdArray.Of(Ordinary("first"), Ordinary("second")), 7, 11)));
            GdDict result = Complete(s, policy), effect = result.GetDictOrEmpty("result"), good = Capture(s);
            Assert.AreEqual(2L, effect.GetDictOrEmpty("training_before").GetInt("event_count"));
            if (effect.Get("training_record") is GdDict row) Assert.AreEqual(2L, row.Get("sequence"));
            foreach (string side in new[] { "before", "after" })
                Exact(effect.Get("training_" + side), Materialize(good, Proof(good, result.GetString("commit_id")).GetDictOrEmpty("training_" + side + "_ref")), "Exact full typed historical summary");
            Assert.IsTrue(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(good), out GdDict decoded, out string reason), reason);
            Assert.IsTrue(s.RestorePaidCraftingDomain(decoded)); GdDict direct = Direct(s);
            Exact(result, s.RetryPaidCraft(result.GetString("job_id"), "opaque-replay"), "Public expansion remains exact"); Exact(direct, Direct(s), "Replay grants nothing");
        }

        [TestCase("accepted", "own_id")]
        [TestCase("gated", "own_id")]
        [TestCase("accepted", "ordinary_own_id")]
        [TestCase("gated", "ordinary_own_id")]
        [TestCase("accepted", "wrong_parent")]
        [TestCase("gated", "wrong_parent")]
        [TestCase("accepted", "missing_append")]
        [TestCase("gated", "missing_append")]
        [TestCase("accepted", "sequence")]
        [TestCase("gated", "sequence")]
        [TestCase("accepted", "appended_row")]
        [TestCase("filtered", "extra_append")]
        [TestCase("none", "extra_append")]
        public void IndependentAppendObligations_RejectAfterValidHistory(string policy, string mutation)
        {
            RunSession s = Boot(); Assert.IsTrue(s.TrainingEventBus.ApplySummary(Summary(GdArray.Of(Ordinary("prefix")))));
            GdDict result = Complete(s, policy), good = Historical(s, result), bad = good.DeepCopy(); string commit = result.GetString("commit_id");
            GdDict effect = Effect(bad, commit), proof = Proof(bad, commit);
            GdDict before = Materialize(bad, proof.GetDictOrEmpty("training_before_ref")), after = Materialize(bad, proof.GetDictOrEmpty("training_after_ref"));
            GdArray prefix = before.GetArrayOrEmpty("log"), tail = after.GetArrayOrEmpty("log");
            switch (mutation)
            {
                case "own_id": prefix.Add(effect.GetDictOrEmpty("training_record").DeepCopy()); break;
                case "ordinary_own_id": prefix.Add(new GdDict { { "commit_id", commit }, { "opaque", "ordinary-looking own identity must not disappear from the index" } }); break;
                case "wrong_parent": tail[0] = Ordinary("different-but-valid-sibling"); break;
                case "missing_append": tail.RemoveAt(tail.Count - 1); break;
                case "extra_append": tail.Add(Ordinary("forbidden extra row")); break;
                case "sequence":
                    effect.GetDictOrEmpty("training_record")["sequence"] = 0L;
                    tail[tail.Count - 1] = effect.GetDictOrEmpty("training_record").DeepCopy(); break;
                case "appended_row": ((GdDict)tail[tail.Count - 1])["target_id"] = "different-output"; break;
            }
            before["event_count"] = (long)prefix.Count; after["event_count"] = (long)tail.Count;
            References(bad, commit, before, after); // Correct full hashes: refusal cannot rely solely on stale digests.
            Reject(s, bad, mutation); Exact(result, s.RetryPaidCraft(result.GetString("job_id"), "after-mutant"), "Rejected history cannot replace original replay");
        }

        [TestCase("accepted", "xp")]
        [TestCase("gated", "xp")]
        [TestCase("filtered", "xp")]
        [TestCase("none", "xp")]
        [TestCase("accepted", "dropped")]
        [TestCase("gated", "dropped")]
        [TestCase("filtered", "dropped")]
        [TestCase("none", "dropped")]
        [TestCase("accepted", "progression")]
        [TestCase("gated", "progression")]
        [TestCase("filtered", "progression")]
        [TestCase("none", "progression")]
        public void RehashedAfterState_StillRequiresIndependentArithmetic(string policy, string mutation)
        {
            RunSession s = Boot(); GdDict result = Complete(s, policy), good = Historical(s, result), bad = good.DeepCopy(); string commit = result.GetString("commit_id");
            GdDict proof = Proof(bad, commit);
            if (mutation == "progression")
            {
                GdDict node = History(bad).GetDictOrEmpty("progression_nodes").GetDictOrEmpty(proof.GetString("progression_after_hash")).DeepCopy();
                GdDict xp = node.GetDictOrEmpty("summary").GetDictOrEmpty("skill_xp"); xp["fabrication"] = xp.GetInt("fabrication") + 1;
                string hash = PaidCraftingState.Hash(node); History(bad).GetDictOrEmpty("progression_nodes")[hash] = node; proof["progression_after_hash"] = hash;
            }
            else
            {
                GdDict before = Materialize(bad, proof.GetDictOrEmpty("training_before_ref")), after = Materialize(bad, proof.GetDictOrEmpty("training_after_ref"));
                string key = mutation == "xp" ? "xp_total" : "dropped"; after[key] = after.GetInt(key) + 1; References(bad, commit, before, after);
            }
            Reject(s, bad, "Self-consistent hashes do not authorize changed " + mutation);
        }

        [TestCase("filtered", "before")]
        [TestCase("filtered", "after")]
        [TestCase("accepted", "before")]
        [TestCase("gated", "after")]
        public void FullReferenceHashCache_MustNotTrustTipOrExpectedDigest(string policy, string side)
        {
            RunSession s = Boot(); GdDict result = Complete(s, policy), good = Historical(s, result); string commit = result.GetString("commit_id");
            Positive(s, good); Assert.IsTrue(DomainBundle.TryCreate(good.DeepCopy(), out _, out _));
            GdDict bad = good.DeepCopy(), proof = Proof(bad, commit), summary = Materialize(bad, proof.GetDictOrEmpty("training_" + side + "_ref"));
            summary["dropped"] = summary.GetInt("dropped") + 1;
            proof["training_" + side + "_hash"] = PaidCraftingState.Hash(summary);
            // All arithmetic, graph and IDs are untouched. Only the expected whole digest belongs to
            // another summary with the same tip/count, challenging both cache key and candidate trust.
            Reject(s, bad, "Same tip/count is not the same typed reference"); Positive(s, good);
        }

        [TestCase("before")]
        [TestCase("after")]
        public void MultipleValidEmptyTipReferences_DifferentCountersNeedDifferentWholeHashes(string side)
        {
            RunSession s = Boot(); GdDict first = Complete(s, "filtered", "filtered-one"), second = Complete(s, "filtered", "filtered-two"), good = Capture(s);
            string a = first.GetString("commit_id"), b = second.GetString("commit_id");
            GdDict firstRef = Proof(good, a).GetDictOrEmpty("training_" + side + "_ref"), secondRef = Proof(good, b).GetDictOrEmpty("training_" + side + "_ref");
            Assert.AreEqual(firstRef.Get("tip_hash"), secondRef.Get("tip_hash")); Assert.AreEqual(firstRef.Get("count"), secondRef.Get("count"));
            Assert.AreNotEqual(firstRef.Get("dropped"), secondRef.Get("dropped"));
            Assert.AreNotEqual(Proof(good, a).Get("training_" + side + "_hash"), Proof(good, b).Get("training_" + side + "_hash")); Positive(s, good);
            GdDict bad = good.DeepCopy(); Proof(bad, b)["training_" + side + "_hash"] = Proof(bad, a).Get("training_" + side + "_hash");
            Reject(s, bad, "Another valid reference's supplied digest is not a cache authority");
            Exact(first, s.RetryPaidCraft(first.GetString("job_id"), "filtered-one-replay"), "First filtered result remains exact");
            Exact(second, s.RetryPaidCraft(second.GetString("job_id"), "filtered-two-replay"), "Second filtered result remains exact");
        }

        [TestCase("gated", "xp_total")]
        [TestCase("none", "dropped")]
        [TestCase("none", "xp_total")]
        public void CounterMaximum_WhenUnchanged_IsValid(string policy, string key)
        {
            RunSession s = Boot(); GdDict seed = Summary(new GdArray()); seed[key] = long.MaxValue;
            Assert.IsTrue(s.TrainingEventBus.ApplySummary(seed)); GdDict result = Complete(s, policy), good = Capture(s);
            Assert.AreEqual(long.MaxValue, result.GetDictOrEmpty("result").GetDictOrEmpty("training_after").Get(key));
            Assert.IsTrue(s.RestorePaidCraftingDomain(good)); Exact(result, s.RetryPaidCraft(result.GetString("job_id"), "max-replay"), "Unchanged maximum remains exact");
        }
        [TestCase("filtered", "dropped")]
        [TestCase("accepted", "xp_total")]
        public void CounterOverflow_CannotBecomeForgedSmallNonnegativeAfter(string policy, string key)
        {
            RunSession s = Boot(); GdDict result = Complete(s, policy), good = Historical(s, result), bad = good.DeepCopy(); string commit = result.GetString("commit_id");
            GdDict proof = Proof(bad, commit), before = Materialize(bad, proof.GetDictOrEmpty("training_before_ref")), after = Materialize(bad, proof.GetDictOrEmpty("training_after_ref"));
            before[key] = long.MaxValue; after[key] = 0L; References(bad, commit, before, after);
            Reject(s, bad, "Real bus arithmetic must reject a forged wrapped counter");
        }

        static GdDict ComponentReceipt(RunSession s)
        {
            GdDict d = s.CaptureComponentDomain(); Valid(d);
            GdDict mounted = d.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values.OfType<GdDict>()
                .First(row => d.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder")).GetString("kind") == "slot");
            string id = mounted.GetString("instance_id");
            GdDict target = s.ListInstallTargets(id).OfType<GdDict>().Single(row => row.GetString("holder_id") == mounted.GetString("holder"));
            s.Scene.PlayerPosition = target.Get("world_position") is Vec3 position ? position : Vec3.FromArray((GdArray)target.Get("world_position"));
            Assert.AreEqual(1L, s.InventoryState.AddItem("wrench", 1)); s.BeginWorkHold();
            GdDict request = s.RequestComponentRemoval(id); Assert.IsTrue(request.GetBool("ok"), request.GetString("reason"));
            for (int tick = 0; tick < 140 && s.GetComponentWorkState().GetString("status") != "committed"; tick++) s.StageWorkAction(0.1);
            GdDict work = s.GetComponentWorkState(); Assert.AreEqual("committed", work.GetString("status")); s.EndWorkHold();
            string commit = "component_transfer:" + work.GetString("command_id");
            GdDict row = s.TrainingEventBus.GetLog().OfType<GdDict>().Single(r => r.GetString("commit_id") == commit).DeepCopy();
            Positive(s, Capture(s)); return row;
        }
        static GdArray CompatiblePair(GdDict component)
        {
            GdDict a = component.DeepCopy(), b = component.DeepCopy();
            a["opaque"] = new GdDict { { "value", 7L }, { "text", "retained\t雪" } };
            b["opaque"] = a.GetDictOrEmpty("opaque").DeepCopy();
            a["sequence"] = 91L; b["sequence"] = "legacy sequence is excluded from payload comparison";
            b["base_xp"] = (double)a.GetInt("base_xp"); return GdArray.Of(a, b);
        }

        [TestCase("accepted")]
        [TestCase("gated")]
        [TestCase("filtered")]
        [TestCase("none")]
        public void HistoricalComponentDuplicates_KeepReceiptLocalNumericAndSequenceCompatibility(string policy)
        {
            RunSession s = Boot(components: true); GdDict component = ComponentReceipt(s); s.TrainingEventBus.Reset(); Capture(s);
            GdDict result = Complete(s, policy), candidate = Historical(s, result); string commit = result.GetString("commit_id");
            GdArray prefix = CompatiblePair(component); Assert.IsTrue(new TrainingEventBus().ApplySummary(Summary(prefix)), "Existing bus compatibility positive");
            RewritePrefix(candidate, commit, prefix); Positive(s, candidate);
            Assert.IsTrue(s.RestorePaidCraftingDomain(candidate)); GdDict expanded = s.RetryPaidCraft(result.GetString("job_id"), "compatible-history");
            Exact(prefix, expanded.GetDictOrEmpty("result").GetDictOrEmpty("training_before").Get("log"), "Typed whole-double XP and opaque sequence survive public expansion");
            GdDict direct = Direct(s); Exact(expanded, s.RetryPaidCraft(result.GetString("job_id"), "compatible-replay"), "Replay is exact"); Exact(direct, Direct(s), "History import/replay adds no reward");
        }

        [TestCase("opaque_type")]
        [TestCase("opaque_value")]
        [TestCase("fractional_xp")]
        [TestCase("out_of_range_xp")]
        [TestCase("whitespace_identity")]
        public void ReferencedComponentPrefix_RejectsOriginalBusIncompatibilities(string mutation)
        {
            RunSession s = Boot(components: true); GdDict component = ComponentReceipt(s); s.TrainingEventBus.Reset(); Capture(s);
            GdDict result = Complete(s), good = Historical(s, result); string commit = result.GetString("commit_id");
            GdArray pair = CompatiblePair(component); RewritePrefix(good, commit, pair); Positive(s, good);
            GdDict bad = good.DeepCopy(), second = (GdDict)pair[1];
            switch (mutation)
            {
                case "opaque_type": second.GetDictOrEmpty("opaque")["value"] = 7.0; break;
                case "opaque_value": second.GetDictOrEmpty("opaque")["value"] = 8L; break;
                case "fractional_xp": second["base_xp"] = 0.5; break;
                case "out_of_range_xp": second["base_xp"] = 9223372036854775808.0; break;
                case "whitespace_identity": second["commit_id"] = " "; break;
            }
            Assert.IsFalse(new TrainingEventBus().ApplySummary(Summary(pair)), "Existing bus comparator is the compatibility oracle");
            RewritePrefix(bad, commit, pair); Reject(s, bad, mutation);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnreferencedConflictAndSiblingReset_DoNotBecomeGlobalDuplicatePolicy(bool reverseInsertion)
        {
            RunSession s = Boot(components: true); GdDict component = ComponentReceipt(s); s.TrainingEventBus.Reset(); Capture(s);
            GdDict result = Complete(s), candidate = Historical(s, result); string commit = result.GetString("commit_id");
            GdArray pair = CompatiblePair(component); RewritePrefix(candidate, commit, GdArray.Of(pair[0])); Positive(s, candidate);
            GdDict conflicting = ((GdDict)pair[1]).DeepCopy(); conflicting.GetDictOrEmpty("opaque")["value"] = 8L;
            GdDict ordinaryRoot = Ordinary("unreferenced sibling root");
            GdArray unused = GdArray.Of(ordinaryRoot, pair[0], conflicting);
            Assert.IsFalse(new TrainingEventBus().ApplySummary(Summary(unused)), "This cross-row conflict is real but this prefix is not referenced");
            Intern(candidate, Summary(unused));
            // A separate valid singleton with the same component ID and different opaque metadata also
            // cannot contaminate the referenced sibling. Every node still has a valid hash/envelope.
            Intern(candidate, Summary(GdArray.Of(conflicting)));
            if (reverseInsertion)
            {
                var reversed = new GdDict(); foreach (var entry in History(candidate).GetDictOrEmpty("training_nodes").Reverse()) reversed[entry.Key] = entry.Value;
                History(candidate)["training_nodes"] = reversed;
            }
            Positive(s, candidate); Assert.IsTrue(s.RestorePaidCraftingDomain(candidate));
            GdDict expanded = s.RetryPaidCraft(result.GetString("job_id"), "sibling-replay");
            Exact(GdArray.Of(pair[0]), expanded.GetDictOrEmpty("result").GetDictOrEmpty("training_before").Get("log"), "Only the selected historical ancestry is expanded");
        }

        [Test]
        public void CurrentOwnedDuplicate_RemainsStricterThanCompatibleHistoricalPrefix()
        {
            RunSession s = Boot(components: true); GdDict component = ComponentReceipt(s); GdDict good = Capture(s); Positive(s, good);
            GdDict bad = good.DeepCopy(), training = Summary(GdArray.Of(component, component.DeepCopy()));
            Assert.IsTrue(new TrainingEventBus().ApplySummary(training), "Historical bus permits matching receipt duplicates");
            bad.GetDictOrEmpty("participating_state")["training"] = training; History(bad)["current_training_ref"] = Intern(bad, training);
            Reject(s, bad, "Current domain owned rows retain the stronger uniqueness obligation");
        }

        [Test]
        public void ResetBranches_IndependentAdmissionsAndDetachedAliasesNeverAuthorizeMutants()
        {
            RunSession s = Boot(); GdDict first = Complete(s, command: "branch-one"); s.TrainingEventBus.Reset(); Capture(s);
            GdDict second = Complete(s, command: "branch-two"), good = Capture(s); Positive(s, good);
            Assert.AreEqual(0L, first.GetDictOrEmpty("result").GetDictOrEmpty("training_before").GetInt("event_count"));
            Assert.AreEqual(0L, second.GetDictOrEmpty("result").GetDictOrEmpty("training_before").GetInt("event_count"));
            GdDict direct = Direct(s), owner = Owner(s).GetSummary(), alias = Capture(s);
            Proof(alias, first.GetString("commit_id"))["training_before_hash"] = new string('a', 64);
            Reject(s, alias, "A previously valid candidate's detached copy earns no trust"); Exact(owner, Owner(s).GetSummary(), "Public capture aliases no owner node");
            GdDict publicResult = s.RetryPaidCraft(first.GetString("job_id"), "alias-read");
            publicResult.GetDictOrEmpty("result").GetDictOrEmpty("training_after").GetArrayOrEmpty("log").Clear();
            Exact(first, s.RetryPaidCraft(first.GetString("job_id"), "alias-reread"), "Expanded public result aliases no canonical node");
            Assert.IsTrue(s.RestorePaidCraftingDomain(good));
            History(good).GetDictOrEmpty("training_nodes").Clear();
            Exact(first, s.RetryPaidCraft(first.GetString("job_id"), "restored-alias-one"), "Restore owns its selected candidate");
            Exact(second, s.RetryPaidCraft(second.GetString("job_id"), "restored-alias-two"), "Sibling replay remains exact"); Exact(direct, Direct(s), "All probes/replays grant nothing");
        }

        static void SlowStation(RunSession s, double delta)
            => typeof(RunSession).GetMethod("RecomputeExpandedShipSystems", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(s, new object[] { delta });
        static void HealthyPower(RunSession s)
        {
            foreach (ShipSystem system in s.ShipSystemsManager.Systems.Values)
                foreach (ShipSubcomponent sub in system.Subcomponents) sub.Health = 1.0;
            Assert.IsTrue(s.SetManualPowerRoute("stations", 10)); SlowStation(s, 0);
            Assert.Greater(s.PowerGridState.GetAllocationRatio("stations"), 0.0);
        }
        static string CookFood(RunSession s)
        {
            GdDict result = Complete(s, command: "food-prerequisite", recipe: "cook_basic_meal");
            Assert.IsTrue(s.SpoilageState.HasFood("cooked_meal")); return result.GetString("job_id");
        }

        [TestCase(false, "empty", "station")]
        [TestCase(false, "queue", "station")]
        [TestCase(false, "consent", "station")]
        [TestCase(false, "empty", "field")]
        [TestCase(false, "queue", "field")]
        [TestCase(false, "consent", "field")]
        [TestCase(true, "empty", "station")]
        [TestCase(true, "queue", "station")]
        [TestCase(true, "consent", "station")]
        [TestCase(true, "empty", "field")]
        [TestCase(true, "queue", "field")]
        [TestCase(true, "consent", "field")]
        public void EligibleIdlePaidChannel_DefersOwnerRefreshButExplicitCaptureIsExact(bool components, string mode, string channel)
        {
            RunSession s = Boot(components: components); CookFood(s); HealthyPower(s);
            string recipe = channel == "field" ? "field_bandage" : Recipe, kind = s.CraftingState.GetStationKind(recipe), id = "";
            if (mode == "queue")
            {
                Provision(s, recipe); GdDict queued = s.EnqueuePaidCraft(kind, recipe, "idle-queue"); Assert.IsTrue(queued.GetBool("committed"), queued.GetString("reason")); id = queued.GetString("job_id");
                Assert.AreEqual("unpaid", Job(s, id).GetString("input_state"));
            }
            if (mode == "consent")
            {
                Provision(s, recipe); id = Start(s, recipe, "idle-consent"); s.AdvanceCrafting(0.125);
                GdDict saved = Capture(s); Assert.IsTrue(s.RestorePaidCraftingDomain(saved)); Assert.IsTrue(Job(s, id).GetBool("resume_required"));
            }
            // Mapping is explicitly fixture-only; BooksRead is the real live source. No book XP emitted.
            string book = V.Str(s.PlayerProgression.GetBooksCatalog().Keys.First());
            s.CraftingState.GetRecipe("craft_thruster_nozzle")["knowledge_book_id"] = book;
            GdDict baseline = Capture(s); Positive(s, baseline);
            double age = s.SpoilageState.GetFood("cooked_meal").ElapsedSeconds;
            Assert.AreEqual(1L, s.InventoryState.AddItem("scrap_metal", 1));
            s.PlayerProgression.GrantXp("fabrication", 1L);
            Assert.IsNotNull(s.TrainingEventBus.Emit("fabricate_part", "ordinary-idle-live", s.PlayerProgression));
            s.PlayerProgression.BooksRead[book] = true;
            s.StageFood(0.125); Assert.AreEqual(age + 0.125, s.SpoilageState.GetFood("cooked_meal").ElapsedSeconds, "Actual food stage retains exact delta");
            GdDict inventory = s.InventoryState.GetSummary(), progression = s.PlayerProgression.GetSummary(), training = s.TrainingEventBus.ToDict(), food = s.SpoilageState.GetSummary();
            if (channel == "field") s.FieldCraftingState.Tick(0.125); else SlowStation(s, 0.125);
            GdDict untouchedOwner = Owner(s).GetSummary(); // Deliberately not public Capture, which must synchronize.
            GdDict synchronized = Capture(s); Positive(s, synchronized);
            Multiple(() => {
                Exact(baseline, untouchedOwner, "Eligible paid channel returns before capture; canonical revision and participants remain deferred");
                Exact(inventory, s.InventoryState.GetSummary(), "No idle or consent-paused input/output movement");
                Exact(progression, s.PlayerProgression.GetSummary(), "No idle training grant"); Exact(training, s.TrainingEventBus.ToDict(), "No idle training record");
                Exact(food, s.SpoilageState.GetSummary(), "Crafting paths do not advance or reset actual food");
                Exact(inventory, synchronized.GetDictOrEmpty("participating_state").Get("inventory"), "Explicit capture gets current inventory");
                Exact(progression, synchronized.GetDictOrEmpty("participating_state").Get("progression"), "Explicit capture gets current progression");
                Exact(training, synchronized.GetDictOrEmpty("participating_state").Get("training"), "Explicit capture gets current training");
                Exact(food, synchronized.GetDictOrEmpty("participating_state").Get("spoilage"), "Explicit capture gets exact food age");
                Exact(Jobs(baseline), Jobs(synchronized), "Jobs/payment/quality/progress remain unchanged without consent");
                Assert.IsTrue(Paid(synchronized).GetDictOrEmpty("knowledge").GetDictOrEmpty("known").GetBool("craft_thruster_nozzle"), "Explicit capture sees exact current read evidence");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ActualIdleTick_AgesFoodAndExplicitCaptureRemainsFresh(bool components)
        {
            RunSession s = Boot(components: components); CookFood(s); HealthyPower(s);
            GdDict before = Capture(s); double age = s.SpoilageState.GetFood("cooked_meal").ElapsedSeconds;
            TickContext frame = TickContext.Frame(0.125, s.Scene.PlayerPosition); s.Tick(in frame); s.Tick(in frame);
            GdDict owner = Owner(s).GetSummary(), after = Capture(s); Positive(s, after);
            Assert.AreEqual(age + 0.25, s.SpoilageState.GetFood("cooked_meal").ElapsedSeconds);
            Exact(Jobs(before), Jobs(after), "Actual whole Tick creates no paid work/payment/output");
            Exact(s.SpoilageState.GetSummary(), after.GetDictOrEmpty("participating_state").Get("spoilage"), "Public capture includes actual food time");
            if (!components) Assert.AreEqual(before.GetInt("revision"), owner.GetInt("revision"), "Ordinary idle Tick defers paid owner refresh");
            // Combined WorkAction intentionally still captures after Food. Its incidental revision is
            // outside this initial optimization; no assertion claims that capture disappeared.
        }

        [TestCase(false, "station")]
        [TestCase(false, "field")]
        [TestCase(true, "station")]
        [TestCase(true, "field")]
        public void NextActualStartAfterIdle_UsesCurrentIngredientsAndPreservesFreshBeforeImage(bool components, string channel)
        {
            RunSession s = Boot(components: components); CookFood(s); string recipe = channel == "field" ? "field_bandage" : Recipe;
            Provision(s, recipe); Capture(s);
            GdDict ingredients = s.CraftingState.GetRecipe(recipe).GetDictOrEmpty("ingredients"); string removed = V.Str(ingredients.Keys.First());
            long amount = s.InventoryState.GetQuantity(removed); Assert.Greater(amount, 0); Assert.AreEqual(amount, s.InventoryState.RemoveItem(removed, amount));
            s.StageFood(0.125); s.AdvanceCrafting(0.125); GdDict direct = Direct(s);
            GdDict refused = s.RequestPaidCraft(s.CraftingState.GetStationKind(recipe), recipe, "after-idle-missing");
            Assert.IsFalse(refused.GetBool("committed")); Assert.AreEqual("missing_ingredients", refused.GetString("reason")); Exact(direct, Direct(s), "Current gate refuses without incidental XP/output/food change");
            Assert.AreEqual(amount, s.InventoryState.AddItem(removed, amount));
            string id = Start(s, recipe, "after-idle-ready"); Assert.AreEqual("paid", Job(s, id).GetString("input_state"));
            foreach (var ingredient in ingredients)
                Assert.AreEqual(V.I64(ingredient.Value), Job(s, id).GetDictOrEmpty("consumed").GetInt(ingredient.Key), "Exactly one current ingredient set paid");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConsentedPowerPause_IsExecutableAfterActualAllocationRecovery(bool components)
        {
            RunSession s = Boot(components: components); HealthyPower(s); Provision(s); string id = Start(s);
            SlowStation(s, 0.125); double progress = Job(s, id).GetFloat("progress_seconds"); Assert.Greater(progress, 0);
            Assert.IsTrue(s.SetManualPowerRoute("stations", 0)); SlowStation(s, 1);
            GdDict paused = Job(s, id); Assert.AreEqual("paused", paused.GetString("status")); Assert.IsFalse(paused.GetBool("resume_required"));
            GdDict inventory = s.InventoryState.GetSummary(); SlowStation(s, 1); Assert.AreEqual(progress, Job(s, id).GetFloat("progress_seconds"));
            Assert.IsTrue(s.SetManualPowerRoute("stations", 10)); SlowStation(s, 0.125);
            Assert.Greater(Job(s, id).GetFloat("progress_seconds"), progress, "Scalar eligibility cannot mistake power pause for consent pause");
            Exact(inventory, s.InventoryState.GetSummary(), "Recovery never repays inputs"); Positive(s, Capture(s));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConsentedPendingDelivery_ZeroDeltaStillDeliversExactlyOnce(bool components)
        {
            RunSession s = Boot(components: components); Provision(s); string id = Start(s);
            long maximum = s.InventoryState.GetDefinition("plating").GetInt("max_stack", 99); Assert.AreEqual(maximum, s.InventoryState.AddItem("plating", maximum));
            s.AdvanceCrafting(100); Assert.AreEqual("completed_pending_delivery", Job(s, id).GetString("status")); Assert.IsFalse(Job(s, id).GetBool("resume_required"));
            Assert.AreEqual(maximum, s.InventoryState.RemoveItem("plating", maximum)); long records = s.TrainingEventBus.GetEventCount();
            s.AdvanceCrafting(0); Assert.AreEqual("completed_delivered", Job(s, id).GetString("status")); Assert.AreEqual(1L, s.InventoryState.GetQuantity("plating")); Assert.AreEqual(records + 1, s.TrainingEventBus.GetEventCount());
            GdDict once = Capture(s); s.AdvanceCrafting(0); s.RetryPaidCraft(id, "pending-once"); Exact(once, Capture(s), "No second publication/output/XP after delivery");
        }

        [TestCase("count_max")]
        [TestCase("count_double")]
        [TestCase("counter_double")]
        [TestCase("event_count")]
        [TestCase("tip_missing")]
        [TestCase("tip_uppercase")]
        [TestCase("extra_ref_key")]
        [TestCase("node_parent")]
        [TestCase("node_count_double")]
        [TestCase("node_hash")]
        [TestCase("progression_hash")]
        public void CandidateGraphAndLiteralTypes_IndependentAdmissionRejects(string mutation)
        {
            RunSession s = Boot(); GdDict result = Complete(s), good = Historical(s, result), bad = good.DeepCopy(); string commit = result.GetString("commit_id");
            GdDict proof = Proof(bad, commit), reference = proof.GetDictOrEmpty("training_after_ref"), nodes = History(bad).GetDictOrEmpty("training_nodes");
            GdDict node = nodes.GetDictOrEmpty(reference.GetString("tip_hash"));
            switch (mutation)
            {
                case "count_max": reference["count"] = long.MaxValue; reference["event_count"] = long.MaxValue; break;
                case "count_double": reference["count"] = 1.0; break;
                case "counter_double": reference["dropped"] = 0.0; break;
                case "event_count": reference["event_count"] = 0L; break;
                case "tip_missing": reference["tip_hash"] = new string('a', 64); break;
                case "tip_uppercase": reference["tip_hash"] = reference.GetString("tip_hash").ToUpperInvariant(); break;
                case "extra_ref_key": reference["trusted"] = true; break;
                case "node_parent":
                case "node_count_double":
                    string oldTip = reference.GetString("tip_hash");
                    if (mutation == "node_parent") node["parent_hash"] = oldTip; else node["count"] = 1.0;
                    string newTip = PaidCraftingState.Hash(node); nodes.Erase(oldTip); nodes[newTip] = node; reference["tip_hash"] = newTip;
                    break; // Node content hash is correct: literal count/parent validation must refuse.
                case "node_hash": node["row"] = Ordinary("wrong hash"); break;
                case "progression_hash": proof["progression_after_hash"] = new string('b', 64); break;
            }
            Reject(s, bad, mutation); Positive(s, good);
        }
    }
}
