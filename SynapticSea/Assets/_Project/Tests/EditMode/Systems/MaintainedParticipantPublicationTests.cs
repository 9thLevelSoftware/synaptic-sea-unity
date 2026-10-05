using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Systems
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public sealed class MaintainedParticipantPublicationTests
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
        static ProjectionCaptureResult Capture(ParticipantProjectionCohort cohort)
        {
            Assert.True(cohort.TryPin(out var pin, out var reason), reason);
            using (pin) { Assert.True(pin.TryCreateCursor(out var cursor, out reason), reason); using (cursor)
                { while (cursor.Status == ProjectionCursorStatus.Pending) cursor.Advance(3);
                  Assert.AreEqual(ProjectionCursorStatus.Complete,cursor.Status,cursor.Reason); return cursor.Result; } }
        }
        static long Header(ProjectionCaptureResult result, string name)
        { for (int i=0;i<5;i++) if(result.Scalar(i).Name==name) return (long)result.Scalar(i).Value.ToNormalized(); throw new ArgumentException(name); }
        static ProjectionNodeId Root(ProjectionCaptureResult result,string name)
        { for(int i=0;i<12;i++) if(result.Root(i).Name==name)return result.Root(i).Node;throw new ArgumentException(name); }
        static ProjectionEntry Entry(ProjectionCaptureResult result,ProjectionNodeId id,int index)
        { for(int i=0;i<result.NodeCount;i++)if(result.Node(i).Node.Equals(id))return result.Entry(i,index);throw new ArgumentException("missing_node"); }
        static InventoryState.PreparedReplacement Quantity(ParticipantProjectionCohort cohort,long quantity)
        { var summary=cohort.Inventory.CaptureTrackedSummary(out var stamp);summary.GetDictOrEmpty("items")["scrap_metal"]=quantity;return cohort.Inventory.PrepareTrackedReplacement(summary,stamp); }
        static TrainingEventBus DetachedTraining(ParticipantProjectionCohort cohort)
        { var detached=new TrainingEventBus();Assert.True(detached.Configure(CatalogRegistry.LoadDict("res://data/player/training_actions.json")));Assert.True(detached.ApplySummary(cohort.Training.ToDict()));return detached; }
        [Test] public void AtomicPublishPreservesActualModelsRootsDefinitionsAndHistoricalCut()
        {
            using var cohort=Cohort();var inventory=cohort.Inventory;var items=inventory.Items;var definition=inventory.GetDefinition("scrap_metal");
            using var before=Capture(cohort);Assert.True(cohort.TryPin(out var pin,out var reason),reason);
            using(pin)
            using(var plan=cohort.PrepareMaintainedPublication(Quantity(cohort,3),null,null))
            {
                using(var attempt=CommonParticipantGate.BeginAttempt())
                {
                    Assert.True(plan.MatchesUnderGate(attempt));plan.InstallUnderGate(attempt);
                    Assert.False(cohort.TryPin(out _,out _)); // no intermediate paired publication observable
                    plan.CompleteUnderGate(attempt);
                }
                Assert.True(cohort.Ready);Assert.AreSame(inventory,cohort.Inventory);Assert.AreSame(items,inventory.Items);Assert.AreSame(definition,inventory.GetDefinition("scrap_metal"));
                Assert.AreEqual(3L,inventory.GetQuantity("scrap_metal"));
                Assert.True(pin.TryCreateCursor(out var cursor,out reason),reason);using(cursor)
                {while(cursor.Status==ProjectionCursorStatus.Pending)cursor.Advance(1);Assert.AreEqual(ProjectionCursorStatus.Complete,cursor.Status,cursor.Reason);using var historical=cursor.Result;
                 Assert.AreEqual(before.Revision,historical.Revision);Assert.AreEqual(4L,Entry(historical,Root(historical,"inventory.items"),0).Value.Scalar.ToNormalized());}
                using var after=Capture(cohort);Assert.AreEqual(before.Revision+1,after.Revision);Assert.AreEqual(3L,Entry(after,Root(after,"inventory.items"),0).Value.Scalar.ToNormalized());
            }
        }
        [Test] public void RollbackRestoresPayloadAndPolicyAliasesWithAdvancedClocksAndNoLifetimeRewind()
        {
            using var cohort=Cohort();var definition=cohort.Inventory.GetDefinition("scrap_metal");var items=cohort.Inventory.Items;
            ulong stamp=cohort.Inventory.CaptureTrackedStamp();using var before=Capture(cohort);
            using(var plan=cohort.PrepareMaintainedPublication(Quantity(cohort,2),null,null))
            using(var attempt=CommonParticipantGate.BeginAttempt()){plan.InstallUnderGate(attempt);plan.RollbackUnderGate(attempt);}
            Assert.True(cohort.Ready);Assert.AreEqual(stamp+2,cohort.Inventory.CaptureTrackedStamp());Assert.AreSame(items,cohort.Inventory.Items);Assert.AreSame(definition,cohort.Inventory.GetDefinition("scrap_metal"));Assert.AreEqual(4L,cohort.Inventory.GetQuantity("scrap_metal"));
            using var restored=Capture(cohort);Assert.AreEqual(before.Revision+2,restored.Revision);
            // A new generation can mint beyond the already-admitted abandoned version.
            using(var next=cohort.PrepareMaintainedPublication(Quantity(cohort,3),null,null))using(var attempt=CommonParticipantGate.BeginAttempt()){next.InstallUnderGate(attempt);next.CompleteUnderGate(attempt);}
            Assert.True(cohort.Ready);Assert.AreEqual(3L,cohort.Inventory.GetQuantity("scrap_metal"));
        }
        [Test] public void TrainingCounterHeadersAndMoreThan32DescendantsPublishTogether()
        {
            using var cohort=Cohort();using var before=Capture(cohort);var scratch=cohort.Progression.CaptureTrackedReplay(out var progressionStamp);var training=DetachedTraining(cohort);
            for(int i=0;i<40;i++)Assert.NotNull(training.Emit("cook_meal","synthetic-fixture-"+i,scratch));training.Emit("unknown_fixture_event","",scratch);
            var progression=cohort.Progression.PrepareTrackedReplacement(scratch.GetSummary(),progressionStamp);
            var history=cohort.Training.PrepareTrackedReplacement(training.ToDict(),cohort.Training.CaptureTrackedStamp());
            using(var plan=cohort.PrepareMaintainedPublication(null,progression,history))using(var attempt=CommonParticipantGate.BeginAttempt()){plan.InstallUnderGate(attempt);plan.CompleteUnderGate(attempt);}
            using var after=Capture(cohort);Assert.True(cohort.Ready);Assert.AreEqual(0L,Header(before,"training.dropped"));Assert.AreEqual(0L,Header(before,"training.xp_total"));Assert.AreEqual(1L,Header(after,"training.dropped"));Assert.AreEqual(1600L,Header(after,"training.xp_total"));Assert.AreEqual(1600L,cohort.Training.GetTotalXpDelivered());Assert.Greater(after.NodeCount,before.NodeCount+32);
        }
        [Test] public void RepeatedTrainingAppendKeepsExistingNodeIdentityAndOldPinsWithoutRemintingHistory()
        {
            using var cohort=Cohort();ProjectionNodeId first=default;
            for(int iteration=0;iteration<50;iteration++)
            {
                var scratch=cohort.Progression.CaptureTrackedReplay(out var ps);var detached=DetachedTraining(cohort);detached.Emit("cook_meal","fixture-"+iteration,scratch);
                var p=cohort.Progression.PrepareTrackedReplacement(scratch.GetSummary(),ps);var t=cohort.Training.PrepareTrackedReplacement(detached.ToDict(),cohort.Training.CaptureTrackedStamp());
                using(var plan=cohort.PrepareMaintainedPublication(null,p,t))using(var attempt=CommonParticipantGate.BeginAttempt()){plan.InstallUnderGate(attempt);plan.CompleteUnderGate(attempt);}
                using var cut=Capture(cohort);var child=Entry(cut,Root(cut,"training.log"),0).Value.Child;if(iteration==0)first=child;else Assert.AreEqual(first,child);
                Assert.True(cohort.Ready);Assert.AreEqual((long)(iteration+1)*40,Header(cut,"training.xp_total"));
            }
        }
        [Test] public void CancelledPreparationDoesNotConsumeWorldOrBlockSubsequentPublication()
        {
            using var cohort=Cohort();using var before=Capture(cohort);using(var cancelled=cohort.PrepareMaintainedPublication(Quantity(cohort,1),null,null)){}
            Assert.True(cohort.Ready);Assert.AreEqual(4L,cohort.Inventory.GetQuantity("scrap_metal"));using var cut=Capture(cohort);Assert.AreEqual(before.Revision,cut.Revision);
            using(var next=cohort.PrepareMaintainedPublication(Quantity(cohort,3),null,null))using(var attempt=CommonParticipantGate.BeginAttempt()){next.InstallUnderGate(attempt);next.CompleteUnderGate(attempt);}
            Assert.True(cohort.Ready);
        }
        [Test] public void RepeatedCaptureReleaseBalancesRetentionAfterAuxiliaryMapCharging()
        {
            using var cohort=Cohort();
            using(var plan=cohort.PrepareMaintainedPublication(Quantity(cohort,3),null,null))using(var attempt=CommonParticipantGate.BeginAttempt()){plan.InstallUnderGate(attempt);plan.CompleteUnderGate(attempt);}
            var registry=(ParticipantProjectionRegistry)typeof(ParticipantProjectionCohort).GetField("_registry",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(cohort);
            long baseline=registry.RetainedUnits;
            for(int i=0;i<100;i++){using(Capture(cohort)){}Assert.AreEqual(baseline,registry.RetainedUnits,"capture "+i);}
        }
        [Test] public void ReservationPressureRefusesBeforeWorldWriteAndKeepsCurrentCutReady()
        {
            using var cohort=Cohort();using var before=Capture(cohort);
            var registry=(ParticipantProjectionRegistry)typeof(ParticipantProjectionCohort).GetField("_registry",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(cohort);
            var field=typeof(ParticipantProjectionRegistry).GetField("_retainedUnits",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            long actual=registry.RetainedUnits;field.SetValue(registry,registry.Limits.MaximumRetainedUnits);
            try { var ex=Assert.Throws<InvalidOperationException>(()=>cohort.PrepareMaintainedPublication(Quantity(cohort,1),null,null));Assert.AreEqual("maintenance_retention_pressure",ex.Message); }
            finally { field.SetValue(registry,actual); }
            Assert.True(cohort.Ready);Assert.AreEqual(4L,cohort.Inventory.GetQuantity("scrap_metal"));
            using var unchanged=Capture(cohort);Assert.AreEqual(before.Revision,unchanged.Revision);
            using(var next=cohort.PrepareMaintainedPublication(Quantity(cohort,3),null,null))using(var attempt=CommonParticipantGate.BeginAttempt()){next.InstallUnderGate(attempt);next.CompleteUnderGate(attempt);}
            Assert.True(cohort.Ready);
        }
        [Test] public void InventoryInputReceiptBindsActualModelAndExactOwnerStampIncludingSameValueABA()
        {
            using var cohort=Cohort();Assert.True(cohort.TryCaptureInventoryInput(cohort.Inventory,out var receipt,out var reason),reason);
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(1),BitConverter.DoubleToInt64Bits(receipt.DrainMultiplier));
            Assert.False(cohort.TryCaptureInventoryInput(new InventoryState(),out _,out reason));Assert.AreEqual("foreign_inventory_input",reason);
            using(var plan=cohort.PrepareMaintainedPublication(Quantity(cohort,4),null,null))using(var attempt=CommonParticipantGate.BeginAttempt()){plan.InstallUnderGate(attempt);plan.CompleteUnderGate(attempt);}
            lock(CommonParticipantGate.SyncRoot)Assert.False(receipt.MatchesUnderGate(cohort.Inventory));
            Assert.True(cohort.TryCaptureInventoryInput(cohort.Inventory,out var fresh,out reason),reason);lock(CommonParticipantGate.SyncRoot)Assert.True(fresh.MatchesUnderGate(cohort.Inventory));
        }
        [Test] public void InventoryInputReceiptReadsActualPumpBitsAndSurvivesUnrelatedProgressionPublication()
        {
            using var cohort=Cohort();var summary=cohort.Inventory.CaptureTrackedSummary(out var stamp);summary.GetDictOrEmpty("items")["portable_oxygen_pump"]=1L;
            var pump=cohort.Inventory.PrepareTrackedReplacement(summary,stamp);
            using(var plan=cohort.PrepareMaintainedPublication(pump,null,null))using(var attempt=CommonParticipantGate.BeginAttempt()){plan.InstallUnderGate(attempt);plan.CompleteUnderGate(attempt);}
            Assert.True(cohort.TryCaptureInventoryInput(cohort.Inventory,out var receipt,out var reason),reason);Assert.AreEqual(BitConverter.DoubleToInt64Bits(.5),BitConverter.DoubleToInt64Bits(receipt.DrainMultiplier));
            var scratch=cohort.Progression.CaptureTrackedReplay(out var ps);scratch.GrantXp("cooking",1);var progression=cohort.Progression.PrepareTrackedReplacement(scratch.GetSummary(),ps);
            using(var plan=cohort.PrepareMaintainedPublication(null,progression,null))using(var attempt=CommonParticipantGate.BeginAttempt()){plan.InstallUnderGate(attempt);plan.CompleteUnderGate(attempt);}
            lock(CommonParticipantGate.SyncRoot)Assert.True(receipt.MatchesUnderGate(cohort.Inventory));
        }
        [Test] public void ClosedAttemptCannotRollbackPublishedWorldLater()
        {
            using var cohort=Cohort();using var plan=cohort.PrepareMaintainedPublication(Quantity(cohort,3),null,null);
            var attempt=CommonParticipantGate.BeginAttempt();plan.InstallUnderGate(attempt);plan.CompleteUnderGate(attempt);attempt.Dispose();
            lock(CommonParticipantGate.SyncRoot)Assert.Throws<InvalidOperationException>(()=>plan.RollbackUnderGate(attempt));
            Assert.AreEqual(3L,cohort.Inventory.GetQuantity("scrap_metal"));Assert.True(cohort.Ready);
        }
    }
}
