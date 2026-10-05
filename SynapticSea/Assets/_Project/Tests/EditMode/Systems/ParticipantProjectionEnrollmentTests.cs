using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    [NonParallelizable]
    public sealed class ParticipantProjectionEnrollmentTests
    {
        IResourceReader _previous;
        Dictionary<string,string> _texts;
        ImmutableResourceAuthority _authority;
        ResourceAuthorityLease _lease;
        [SetUp]
        public void Setup()
        {
            _previous = CoreServices.Resources;
            var files = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            _texts = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (string path in ParticipantProjectionFactory.RequiredResourcePaths) _texts.Add(path, files.ReadText(path));
            _authority = new ImmutableResourceAuthority(_texts, new Dictionary<string,IReadOnlyList<string>> {
                { "res://data/player", new[] { "classes.json", "skills.json", "skill_books.json", "training_actions.json" } } });
            CoreServices.Resources = _authority; CatalogRegistry.Clear();
            Assert.True(ResourceAuthorityPublication.TryAcquire(out _lease, out var reason), reason);
        }
        [TearDown]
        public void Cleanup() { CoreServices.Resources = _previous; CatalogRegistry.Clear(); }
        ParticipantProjectionCohort Cohort(ProjectionLimits limits = null)
        {
            using var prepared = ParticipantProjectionFactory.Prepare(_lease, "cook", limits,
                new[] { new ParticipantInitialQuantity("scrap_metal", 4), new ParticipantInitialQuantity("power_cell", 2) });
            Assert.True(prepared.TryPublish(out var result, out var reason), reason); return result;
        }
        static ProjectionCaptureResult Capture(ParticipantProjectionCohort cohort)
        {
            Assert.True(cohort.TryPin(out var pin, out var reason), reason);
            using (pin)
            {
                Assert.True(pin.TryCreateCursor(out var cursor, out reason), reason);
                using (cursor)
                {
                    while (cursor.Status == ProjectionCursorStatus.Pending) cursor.Advance(3);
                    Assert.AreEqual(ProjectionCursorStatus.Complete, cursor.Status, cursor.Reason); return cursor.Result;
                }
            }
        }
        static ProjectionScalar RootValue(ProjectionCaptureResult result, string rootName, string key)
        {
            ProjectionNodeId id = default;
            for (int i = 0; i < 12; i++) if (result.Root(i).Name == rootName) id = result.Root(i).Node;
            for (int i = 0; i < result.NodeCount; i++)
            {
                var node = result.Node(i); if (!node.Node.Equals(id)) continue;
                for (int j = 0; j < node.EntryCount; j++) { var entry = result.Entry(i, j); if (entry.Key.Kind == ProjectionScalarKind.String && entry.Key.Text == key) return entry.Value.Scalar; }
            }
            throw new InvalidOperationException("missing_test_root_value:" + rootName + ":" + key);
        }
        [Test]
        public void ActualCatalogFactoryRunsInsidePortRefusalAndExposesOnlyPublishedModels()
        {
            PreparedParticipantProjectionCohort prepared;
            using (new PinnedAdmissionResourceScope(_lease)) prepared = ParticipantProjectionFactory.Prepare(_lease, "cook");
            using (prepared)
            {
                Assert.True(prepared.TryPublish(out var cohort, out var reason), reason);
                using (cohort)
                {
                    Assert.IsInstanceOf<InventoryState>(cohort.Inventory); Assert.IsInstanceOf<PlayerProgressionState>(cohort.Progression);
                    Assert.IsInstanceOf<TrainingEventBus>(cohort.Training); Assert.True(cohort.Ready); Assert.False(cohort.WholeWorldSave);
                    Assert.False(prepared.TryPublish(out _, out _));
                }
            }
        }
        [Test]
        public void FactoryUsesExactLegacyCatalogNormalizationAndActualInitialState()
        {
            var definitions = ItemDefs.LoadDefinitions();
            var expected = new PlayerProgressionState(); expected.Configure(ClassDefinition.LoadAll()["cook"], PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            using var cohort = Cohort();
            Assert.AreEqual(GdJson.Stringify(expected.GetSummary()), GdJson.Stringify(cohort.Progression.GetSummary()));
            foreach (var row in definitions)
                Assert.AreEqual(GdJson.Stringify(row.Value), GdJson.Stringify(cohort.Inventory.GetDefinition(V.Str(row.Key))), V.Str(row.Key));
            Assert.AreEqual(4L, cohort.Inventory.GetQuantity("scrap_metal")); Assert.AreEqual(2L, cohort.Inventory.GetQuantity("power_cell"));
            using var cut = Capture(cohort);
            Assert.AreEqual(4L, RootValue(cut, "inventory.items", "scrap_metal").ToNormalized());
            Assert.AreEqual(expected.GetSkillLevel("cooking"), RootValue(cut, "progression.skills", "cooking").ToNormalized());
        }
        [Test]
        public void SupportedPublicScalarWritesKeepHistoricalCutWhileLiveWorldAdvances()
        {
            using var cohort = Cohort(); Assert.True(cohort.TryPin(out var pin, out var reason), reason);
            using (pin)
            {
                Assert.True(pin.TryCreateCursor(out var cursor, out reason), reason);
                using (cursor)
                {
                    int edits = 0;
                    while (cursor.Status == ProjectionCursorStatus.Pending)
                    {
                        cohort.Inventory.Items["scrap_metal"] = (long)(3 + edits % 2);
                        cohort.Progression.SkillXpFractional["cooking"] = 0.125 + edits;
                        cursor.Advance(2); Assert.LessOrEqual(cursor.LastWorkUnits, 2); edits++;
                    }
                    Assert.Greater(edits, 1); Assert.True(cohort.Ready);
                    using var old = cursor.Result;
                    Assert.AreEqual(4L, RootValue(old, "inventory.items", "scrap_metal").ToNormalized());
                    Assert.AreEqual(0.0, RootValue(old, "progression.fractional", "cooking").ToNormalized());
                    using var next = Capture(cohort);
                    Assert.AreEqual(cohort.Inventory.GetQuantity("scrap_metal"), RootValue(next, "inventory.items", "scrap_metal").ToNormalized());
                    Assert.True(ProjectionScalar.FromNormalized(cohort.Progression.SkillXpFractional["cooking"]).Equals(RootValue(next, "progression.fractional", "cooking")));
                }
            }
        }
        [Test]
        public void StructuralAndPolicyWritesRevokeBeforeAcceptanceAndStillChangeTheModel()
        {
            using var cohort = Cohort(); using var old = Capture(cohort);
            cohort.Inventory.Items["new_diagnostic_state"] = 1L;
            Assert.AreEqual(1L, cohort.Inventory.GetQuantity("new_diagnostic_state")); Assert.False(cohort.Ready);
            Assert.AreEqual(ProjectionOutputStatus.Revoked, old.Status);
            using var second = Cohort(); using var secondCut = Capture(second);
            var definition = second.Inventory.GetDefinition("scrap_metal"); definition["weight"] = 123.0;
            Assert.AreEqual(123.0, second.Inventory.GetWeightEach("scrap_metal")); Assert.False(second.Ready);
            Assert.AreEqual(ProjectionOutputStatus.Revoked, secondCut.Status);
        }
        [Test]
        public void FailedMutationDoesNotDestroyReadyButAcceptedSameValueAdvances()
        {
            using var cohort = Cohort(); using var old = Capture(cohort);
            Assert.Throws<ArgumentException>(() => cohort.Inventory.Items.Add("scrap_metal", 9L));
            Assert.False(cohort.Inventory.Items.Erase("not-present")); Assert.True(cohort.Ready);
            cohort.Inventory.Items["scrap_metal"] = 4L;
            using var next = Capture(cohort); Assert.Greater(next.Revision, old.Revision); Assert.AreEqual(old.ReadinessEpoch, next.ReadinessEpoch);
        }
        [Test]
        public void ActualModelCommandsContinueAfterDiagnosticInvalidation()
        {
            using var cohort = Cohort(); using var old = Capture(cohort);
            long accepted = cohort.Inventory.AddItem("scrap_metal", 1); Assert.AreEqual(1L, accepted);
            cohort.Progression.GrantXp("cooking", 17);
            Assert.AreEqual(5L, cohort.Inventory.GetQuantity("scrap_metal")); Assert.Greater(V.I64(cohort.Progression.SkillXp["cooking"]), 0L);
            Assert.False(cohort.Ready); Assert.AreEqual(ProjectionOutputStatus.Revoked, old.Status);
            Assert.False(cohort.TryPin(out _, out _)); // no stale Ready; accepted commands were not paused/refunded
        }
        [Test]
        public void ScalarPolicyAndEligibilityClosureSettersInvalidateTheCohort()
        {
            using var first = Cohort(); first.Inventory.BonusCapacity = 2.0; Assert.False(first.Ready); Assert.AreEqual(2.0, first.Inventory.BonusCapacity);
            using var second = Cohort(); second.Progression.XpMultipliers["technical"] = 2.0; Assert.False(second.Ready);
            using var third = Cohort(); third.Training.SkillGate = _ => true; Assert.False(third.Ready);
            using var fourth = Cohort(); fourth.Progression.ClassId = "engineer"; Assert.False(fourth.Ready); Assert.AreEqual("engineer", fourth.Progression.ClassId);
        }
        [Test]
        public void LeaseReplacementRefusesPreparedFactoryAndRevokesExistingOutputsWithoutAnotherPin()
        {
            using var prepared = ParticipantProjectionFactory.Prepare(_lease, "cook");
            using var cohort = Cohort(); using var output = Capture(cohort);
            var replacement = new ImmutableResourceAuthority(_texts);
            Assert.AreNotEqual(_authority.ContentSha256, replacement.ContentSha256); // directory declaration participates in identity
            CoreServices.Resources = replacement;
            Assert.False(prepared.TryPublish(out _, out _));
            Assert.Throws<InvalidOperationException>(() => output.Root(0)); Assert.AreEqual(ProjectionOutputStatus.Revoked, output.Status);
            Assert.False(cohort.Ready);
        }
        [Test]
        public void FactoryRejectsEarlyLimitsUnknownClassAndInvalidQuantityRows()
        {
            Assert.Throws<ArgumentException>(() => ParticipantProjectionFactory.Prepare(_lease, "not-a-catalog-class"));
            Assert.Throws<ArgumentException>(() => ParticipantProjectionFactory.Prepare(_lease, "cook", new ProjectionLimits(nodes: 2)));
            Assert.Throws<ArgumentException>(() => ParticipantProjectionFactory.Prepare(_lease, "cook", initialQuantities: new ParticipantInitialQuantity[129]));
            Assert.Throws<ArgumentException>(() => ParticipantProjectionFactory.Prepare(_lease, "cook", initialQuantities: new ParticipantInitialQuantity[1]));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)ParticipantProjectionFactory.RequiredResourcePaths)[0] = "res://fake.json");
        }
        [Test]
        public void OwnerScalarRegistryRejectsForeignCohortsAndForgedDescriptors()
        {
            using var cohort = Cohort(); using var foreign = Cohort();
            var registry = (ParticipantProjectionRegistry)typeof(ParticipantProjectionCohort)
                .GetField("_registry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(cohort);
            var binding = cohort.Inventory.Items.ProjectionNode;
            var before = binding.Current;
            int slot = -1;
            for (int i = 0; i < before.EntryCount; i++)
                if (before.GetEntry(i).Key.Text == "scrap_metal") slot = i;
            Assert.GreaterOrEqual(slot, 0);
            Assert.True(binding.Handle.TryPrepareScalarVersion(before, slot,
                new ProjectionValue(ProjectionScalar.FromNormalized(3L)), out var issued, out var reason), reason);
            using var original = Capture(cohort);
            var revision = (ulong)typeof(ParticipantProjectionCohort).GetField("_cohortRevision", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(cohort);
            var next = cohort.Descriptor(revision + 1);
            var undo = cohort.Descriptor(revision + 2);
            Assert.False(registry.TryPrepareOwnerScalar(null, before, issued, next, undo, out _));
            Assert.False(registry.TryPrepareOwnerScalar(foreign, before, issued, next, undo, out _));
            var roots = new ProjectionRootBinding[next.RootCount];
            var scalars = new ProjectionScalarBinding[next.ScalarCount];
            for (int i = 0; i < roots.Length; i++) roots[i] = next.Root(i);
            for (int i = 0; i < scalars.Length; i++) scalars[i] = next.Scalar(i);
            var swapped = (ProjectionRootBinding[])roots.Clone();
            swapped[0] = new ProjectionRootBinding(roots[0].Name, roots[1].Node);
            Assert.False(registry.TryPrepareOwnerScalar(cohort, before, issued,
                new ProjectionRootDescriptor(next.CohortIdentity, next.PolicyIdentity, next.OwnerStamp, swapped, scalars), undo, out _));
            Assert.False(registry.TryPrepareOwnerScalar(cohort, before, issued,
                new ProjectionRootDescriptor(next.CohortIdentity, "forged-policy", next.OwnerStamp, roots, scalars), undo, out _));
            var changed = (ProjectionScalarBinding[])scalars.Clone();
            changed[0] = new ProjectionScalarBinding(scalars[0].Name, ProjectionScalar.FromNormalized("forged-class"));
            Assert.False(registry.TryPrepareOwnerScalar(cohort, before, issued,
                new ProjectionRootDescriptor(next.CohortIdentity, next.PolicyIdentity, next.OwnerStamp, roots, changed), undo, out _));
            Assert.False(registry.TryPrepareOwnerScalar(cohort, before, issued, cohort.Descriptor(next.OwnerStamp + 1), undo, out _));
            Assert.False(registry.TryPrepareOwnerScalar(cohort, before, issued, next, next, out _));
            Assert.True(registry.TryPrepareOwnerScalar(cohort, before, issued, next, undo, out var valid));
            lock (CommonParticipantGate.SyncRoot) valid.CancelUnderGate();
            Assert.True(cohort.Ready); Assert.AreEqual(4L, cohort.Inventory.GetQuantity("scrap_metal"));
            using var unchanged = Capture(cohort);
            Assert.AreEqual(original.Revision, unchanged.Revision);
            Assert.AreEqual(4L, RootValue(unchanged, "inventory.items", "scrap_metal").ToNormalized());
        }
        [Test]
        public void ActualCohortPreparesMoreThan32PrivateDescendantsWithoutPublishingIntermediateState()
        {
            using var cohort = Cohort(); using var before = Capture(cohort);
            var root = cohort.Inventory.Items; var incoming = new GdDict();
            // Deliberately generic graph preparation only: not an inventory reward/admission claim.
            for (int i = 0; i < 40; i++) incoming["branch" + i] = new GdDict { { "value", (long)i } };
            var backing = root.Owner.PrepareReplacements(new object[] { root }, new object[] { incoming }, root.Owner.Stamp);
            var origin = backing.CaptureProjectionOrigin();
            using (var candidate = cohort.PrepareMaintainedCandidate(new[] { origin }))
            {
                Assert.AreEqual(41, candidate.VersionCount);
                using (var admission = cohort.PrepareCandidateAdmission(candidate))
                {
                    while (admission.Status == ProjectionCursorStatus.Pending)
                    { admission.Advance(3); Assert.LessOrEqual(admission.LastWorkUnits, 3); }
                    Assert.AreEqual(ProjectionCursorStatus.Complete, admission.Status, admission.Reason);
                    lock (CommonParticipantGate.SyncRoot) Assert.True(admission.MatchesUnderGate());
                    Assert.GreaterOrEqual(admission.PreparedPlan.NodeCount, 41);
                    Assert.AreEqual(40, admission.PreparedTable.Get(root.ProjectionNode.Handle.Id).EntryCount);
                    var definitions = before.Root(1).Node;
                    Assert.AreEqual(definitions, admission.PreparedTable.Get(definitions).Id);
                }
                Assert.AreEqual(root.ProjectionNode.Handle.Id, candidate.Version(0).Id);
                lock (CommonParticipantGate.SyncRoot) Assert.True(candidate.MatchesUnderGate());
                Assert.True(cohort.Ready); Assert.AreEqual(4L, cohort.Inventory.GetQuantity("scrap_metal"));
                using var unchanged = Capture(cohort); Assert.AreEqual(before.Revision, unchanged.Revision);
                Assert.AreEqual(4L, RootValue(unchanged, "inventory.items", "scrap_metal").ToNormalized());
            }
            Assert.True(cohort.Ready); Assert.AreEqual(4L, cohort.Inventory.GetQuantity("scrap_metal"));
        }
        [Test]
        public void MaintainedAdmissionRefusesSupersededIssuanceWithoutWorldRollback()
        {
            using var cohort = Cohort(); var root = cohort.Inventory.Items;
            var backing = root.Owner.PrepareReplacements(new object[] { root }, new object[] { new GdDict { { "scrap_metal", 3L } } }, root.Owner.Stamp);
            var origin = backing.CaptureProjectionOrigin();
            using var first = cohort.PrepareMaintainedCandidate(new[] { origin });
            using var cursor = cohort.PrepareCandidateAdmission(first);
            using var superseding = cohort.PrepareMaintainedCandidate(new[] { origin });
            Assert.AreEqual(ProjectionCursorStatus.Refused, cursor.Advance(1));
            Assert.AreEqual("stale_maintenance_admission", cursor.Reason);
            Assert.True(cohort.Ready); Assert.AreEqual(4L, cohort.Inventory.GetQuantity("scrap_metal"));
        }
        [Test]
        public void MaintenanceGraphPreparationRejectsForeignAndPolicyOrigins()
        {
            using var cohort = Cohort(); using var foreign = Cohort();
            var root = foreign.Inventory.Items;
            var backing = root.Owner.PrepareReplacements(new object[] { root }, new object[] { new GdDict { { "scrap_metal", 3L } } }, root.Owner.Stamp);
            Assert.Throws<ArgumentException>(() => cohort.PrepareMaintainedCandidate(new[] { backing.CaptureProjectionOrigin() }));
            var definitions = (GdDict)typeof(InventoryState).GetField("_definitions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(cohort.Inventory);
            var policy = definitions.Owner.PrepareReplacements(new object[] { definitions }, new object[] { definitions.DeepCopy() }, definitions.Owner.Stamp);
            Assert.Throws<ArgumentException>(() => cohort.PrepareMaintainedCandidate(new[] { policy.CaptureProjectionOrigin() }));
            Assert.True(cohort.Ready);
        }
        [Test]
        public void ExplicitNonProjectedTrackingAndLegacyRemainOutsideNewEnrollment()
        {
            var legacy = new InventoryState(); legacy.Items["scrap_metal"] = 1L; legacy.Items["scrap_metal"] = 2L; Assert.AreEqual(2L, legacy.GetQuantity("scrap_metal"));
            var tracked = InventoryState.CreateTracked(ItemDefs.LoadDefinitions()); tracked.Items["scrap_metal"] = 1L; tracked.BonusCapacity = 3.0;
            Assert.AreEqual(1L, tracked.GetQuantity("scrap_metal")); Assert.AreEqual(3.0, tracked.BonusCapacity);
        }
    }
}
