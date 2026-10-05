using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Systems
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public sealed class MaintainedLifetimeCursorTests
    {
        IResourceReader _previous; ResourceAuthorityLease _lease;
        [SetUp] public void Setup()
        {
            _previous = CoreServices.Resources;
            var files = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            var texts = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (string path in ParticipantProjectionFactory.RequiredResourcePaths) texts.Add(path, files.ReadText(path));
            CoreServices.Resources = new ImmutableResourceAuthority(texts, new Dictionary<string,IReadOnlyList<string>> {
                { "res://data/player", new[] { "classes.json", "skills.json", "skill_books.json", "training_actions.json" } } });
            CatalogRegistry.Clear(); Assert.True(ResourceAuthorityPublication.TryAcquire(out _lease, out var reason), reason);
        }
        [TearDown] public void Cleanup() { CoreServices.Resources = _previous; CatalogRegistry.Clear(); }
        ParticipantProjectionCohort Cohort()
        {
            using var prepared = ParticipantProjectionFactory.Prepare(_lease, "cook", null,
                new[] { new ParticipantInitialQuantity("scrap_metal",4) });
            Assert.True(prepared.TryPublish(out var cohort, out var reason), reason); return cohort;
        }
        [Test] public void MoreThan32CandidateLifetimeAdmissionsArePreparedWithoutLiveClocksOrItemsChanging()
        {
            using var cohort = Cohort(); var root = cohort.Inventory.Items; var data = new GdDict();
            for (int i = 0; i < 40; i++) data["child" + i] = new GdDict { { "value", (long)i } };
            var backing = root.Owner.PrepareReplacements(new object[] { root }, new object[] { data }, root.Owner.Stamp);
            using var candidate = cohort.PrepareMaintainedCandidate(new[] { backing.CaptureProjectionOrigin() });
            using var admission = cohort.PrepareCandidateAdmission(candidate);
            while (admission.Status == ProjectionCursorStatus.Pending) admission.Advance(3);
            Assert.AreEqual(ProjectionCursorStatus.Complete, admission.Status, admission.Reason);
            using var lifetimes = cohort.PrepareMaintainedLifetimes(candidate, admission);
            while (lifetimes.Status == ProjectionCursorStatus.Pending) lifetimes.Advance(1);
            Assert.AreEqual(ProjectionCursorStatus.Complete, lifetimes.Status, lifetimes.Reason);
            Assert.GreaterOrEqual(lifetimes.PreparedCount, 41);
            lock (CommonParticipantGate.SyncRoot) Assert.True(lifetimes.MatchesUnderGate());
            Assert.True(cohort.Ready); Assert.AreEqual(4L, cohort.Inventory.GetQuantity("scrap_metal"));
            Assert.Throws<ArgumentOutOfRangeException>(() => lifetimes.Advance(65));
            lifetimes.Dispose(); lifetimes.Dispose();
            Assert.AreEqual(ProjectionCursorStatus.Cancelled, lifetimes.Status);
        }
        [Test] public void IndividuallyCurrentDisjointCandidatesCannotCrossPairTableAndLifetimeProofs()
        {
            using var cohort = Cohort(); var items = cohort.Inventory.Items; var skills = cohort.Progression.Skills;
            var inventoryBacking = items.Owner.PrepareReplacements(new object[] { items },
                new object[] { new GdDict { { "scrap_metal",3L } } }, items.Owner.Stamp);
            var progressionBacking = skills.Owner.PrepareReplacements(new object[] { skills },
                new object[] { skills.DeepCopy() }, skills.Owner.Stamp);
            using var inventoryCandidate = cohort.PrepareMaintainedCandidate(new[] { inventoryBacking.CaptureProjectionOrigin() });
            using var progressionCandidate = cohort.PrepareMaintainedCandidate(new[] { progressionBacking.CaptureProjectionOrigin() });
            // Capture both admission sources AFTER both disjoint issuances: neither is stale.
            using var inventoryAdmission = cohort.PrepareCandidateAdmission(inventoryCandidate);
            using var progressionAdmission = cohort.PrepareCandidateAdmission(progressionCandidate);
            while (inventoryAdmission.Status == ProjectionCursorStatus.Pending) inventoryAdmission.Advance(3);
            while (progressionAdmission.Status == ProjectionCursorStatus.Pending) progressionAdmission.Advance(3);
            Assert.AreEqual(ProjectionCursorStatus.Complete, inventoryAdmission.Status, inventoryAdmission.Reason);
            Assert.AreEqual(ProjectionCursorStatus.Complete, progressionAdmission.Status, progressionAdmission.Reason);
            lock (CommonParticipantGate.SyncRoot)
            { Assert.True(inventoryAdmission.MatchesUnderGate()); Assert.True(progressionAdmission.MatchesUnderGate()); }
            Assert.Throws<InvalidOperationException>(() => cohort.PrepareMaintainedLifetimes(progressionCandidate, inventoryAdmission));
            Assert.Throws<InvalidOperationException>(() => ParticipantProjectionRegistry.MaintainedLifetimeCursor.Create(
                (ParticipantProjectionRegistry)typeof(ParticipantProjectionCohort).GetField("_registry",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(cohort),
                cohort, inventoryCandidate, progressionAdmission));
            using var valid = cohort.PrepareMaintainedLifetimes(inventoryCandidate, inventoryAdmission);
            while (valid.Status == ProjectionCursorStatus.Pending) valid.Advance(2);
            Assert.AreEqual(ProjectionCursorStatus.Complete, valid.Status, valid.Reason);
            Assert.True(cohort.Ready); Assert.AreEqual(4L, cohort.Inventory.GetQuantity("scrap_metal"));
        }
        [Test] public void SupersedingIssuedObjectRefusesPartlyCopiedLifetimeCandidate()
        {
            using var cohort = Cohort(); var root = cohort.Inventory.Items;
            var backing = root.Owner.PrepareReplacements(new object[] { root },
                new object[] { new GdDict { { "scrap_metal",3L } } }, root.Owner.Stamp);
            var origin = backing.CaptureProjectionOrigin();
            using var candidate = cohort.PrepareMaintainedCandidate(new[] { origin });
            using var admission = cohort.PrepareCandidateAdmission(candidate);
            while (admission.Status == ProjectionCursorStatus.Pending) admission.Advance(2);
            using var lifetimes = cohort.PrepareMaintainedLifetimes(candidate, admission);
            Assert.AreEqual(ProjectionCursorStatus.Pending, lifetimes.Advance(1));
            using var superseding = cohort.PrepareMaintainedCandidate(new[] { origin });
            Assert.AreEqual(ProjectionCursorStatus.Refused, lifetimes.Advance(1));
            Assert.AreEqual("stale_maintenance_lifetime_source", lifetimes.Reason);
            Assert.True(cohort.Ready); Assert.AreEqual(4L, cohort.Inventory.GetQuantity("scrap_metal"));
        }
    }
}
