using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    // Provisioned headless lifecycle proof; physical acquisition and cross-runtime exchange are separate checks.
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class PaidBitExactSessionTests : InfraDataTestBase
    {
        IEngineInfo _previousEngine;
        [SetUp] public void Engine() { _previousEngine = CoreServices.Engine; CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion); }
        [TearDown] public void RestoreEngine() { CoreServices.Engine = _previousEngine; }
        static RunSession Boot(int feature, bool components, bool bits, out SessionHarness.Rig rig, out RunSessionDeps deps)
        {
            deps = SessionHarness.GoldenDeps(out rig); SessionHarness.OverlayGamePlayability(deps);
            deps.EnablePaidCrafting = true; deps.EnableComponentIntegration = components;
            deps.EnableManualStudy = feature >= 4; deps.EnableAuxiliaryServices = feature == 5;
            deps.EnableBitExactPaidCompatibility = bits;
            if (feature == 5)
            {
                const string path = "res://data/diagnostics/earned-services-home-v1/";
                deps.LayoutPath = path + "layout.json"; deps.GameplaySlicePath = path + "gameplay_slice.json"; deps.BlueprintPath = path + "blueprint.json";
            }
            rig.Session = RunSession.Create(deps); Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            rig.Session.ThreatManager.Threats.Clear(); return rig.Session;
        }
        static GdDict Valid(RunSession s, long schema, long feature)
        {
            GdDict owner = s.CapturePaidCraftingDomain(); Assert.AreEqual(schema, owner.GetInt("schema_version"));
            Assert.IsTrue(DomainBundle.TryCreate(owner, out var bundle, out string reason), reason);
            Assert.AreEqual(feature, bundle.HashContext == PaidHashContext.BitsV2 ? owner.GetInt("feature_schema") : owner.GetInt("schema_version"));
            Assert.IsTrue(s.ValidatePaidCraftingRestore(owner, out reason), reason); return owner;
        }
        [TestCase(3, false)][TestCase(4, false)][TestCase(5, false)]
        [TestCase(3, true)][TestCase(4, true)][TestCase(5, true)]
        public void FreshV2OwnerHasOnlyBitExactHistoryAndRoundTripsNormalSave(int feature, bool components)
        {
            RunSession s = Boot(feature, components, true, out var rig, out _);
            try
            {
                var owner = Valid(s, 6, feature);
                Assert.AreEqual(PaidHashContext.BitsV2.Algorithm, owner.GetString("hash_algorithm"));
                Assert.IsTrue(PaidSnapshotCodec.TryDecodeOwner(PaidSnapshotCodec.EncodeOwner(owner), out var decoded, out string reason), reason);
                Assert.IsTrue(PaidHashContext.BitsV2.Equal(owner, decoded));
                Assert.IsTrue(s.RequestSaveToSlot("slot_01", "manual", "Fresh bits owner"), GdJson.Stringify(s.LastSaveResult));
                var selected = s.SaveLoadService.SelectGeneration("slot_01"); Assert.IsTrue(selected.GetBool("ok"), GdJson.Stringify(selected));
                Assert.AreEqual(PaidHashContext.BitsV2.Algorithm, selected.GetDictOrEmpty("payloads").GetDictOrEmpty("binding").GetString("hash_algorithm"));
                var noCapability = new SaveLoadService(rig.Storage, rig.Clock, components, true);
                Assert.IsFalse(noCapability.SelectGeneration("slot_01").GetBool("ok"), "new owner requires loader capability");
                Assert.IsTrue(s.ApplySelectedGeneration(selected), GdJson.Stringify(s.LastSaveResult));
                Valid(s, 6, feature);
            }
            finally { s.Dispose(); }
        }
        [Test]
        public void LateCreationOptInAndLegacyRestoreKeepLegacyHistoryAndSaveWire()
        {
            RunSession s = Boot(3, false, false, out var rig, out var deps);
            try
            {
                var legacy = Valid(s, 3, 3); deps.EnableBitExactPaidCompatibility = true;
                s.SaveLoadService.BitExactPaidCompatibilityEnabled = true;
                Assert.IsTrue(s.RestorePaidCraftingDomain(legacy));
                var after = Valid(s, 3, 3); Assert.IsFalse(after.Has("hash_algorithm")); Assert.IsFalse(after.Has("feature_schema"));
                Assert.IsTrue(s.RequestSaveToSlot("slot_01", "manual", "Legacy stays legacy"), GdJson.Stringify(s.LastSaveResult));
                var selected = s.SaveLoadService.SelectGeneration("slot_01"); Assert.IsTrue(selected.GetBool("ok"));
                Assert.IsFalse(selected.GetDictOrEmpty("payloads").GetDictOrEmpty("binding").Has("hash_algorithm"));
                Assert.IsTrue(s.ApplySelectedGeneration(selected), GdJson.Stringify(s.LastSaveResult)); Valid(s, 3, 3);
            }
            finally { s.Dispose(); }
        }
        [TestCase(3, 4)][TestCase(4, 5)][TestCase(5, 4)][TestCase(4, 3)]
        public void V2OwnerRefusesFeatureFlagChangesWithoutChangingHistory(int created, int requested)
        {
            RunSession s = Boot(created, false, true, out _, out var deps);
            try
            {
                var before = Valid(s, 6, created);
                deps.EnableManualStudy = requested >= 4; deps.EnableAuxiliaryServices = requested == 5;
                Assert.AreEqual("paid_crafting_inactive", s.CapturePaidCraftingDomain().GetString("reason"));
                Assert.IsFalse(s.ValidatePaidCraftingRestore(before, out string reason));
                Assert.AreEqual("bit_exact_paid_feature_mismatch", reason);
                deps.EnableManualStudy = created >= 4; deps.EnableAuxiliaryServices = created == 5;
                Assert.IsTrue(PaidHashContext.BitsV2.Equal(before, Valid(s, 6, created)));
                deps.EnableBitExactPaidCompatibility = false;
                Assert.AreEqual("paid_crafting_inactive", s.CapturePaidCraftingDomain().GetString("reason"));
                Assert.IsFalse(s.ValidatePaidCraftingRestore(before, out reason));
                Assert.AreEqual("bit_exact_paid_capability_required", reason);
            }
            finally { s.Dispose(); }
        }
        [Test]
        public void RealV2CraftStudyAndAuxiliaryCompletionPreserveOnceOnlyProofs()
        {
            RunSession s = Boot(5, false, true, out var rig, out _);
            try
            {
                var supply = s.LootContainers.Single(p => p.ContainerId == "start_supply_a"); rig.Scene.PlayerPosition = supply.GlobalPosition;
                Assert.IsTrue(supply.TryInteract(supply.GlobalPosition));
                if (s.EquipmentState.GetEquipped("primary_hand") == "crowbar") s.UnequipToInventory("primary_hand");
                string utility = s.AuxiliaryServicePoints.First(p => s.GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(p.ServiceId).GetString("kind") == "utility").ServiceId;
                var materials = s.GetAuxiliaryServiceState().GetDictOrEmpty("descriptors").GetDictOrEmpty(utility).GetDictOrEmpty("materials_consumed");
                Assert.IsFalse(materials.IsEmpty);
                var quantities = new GdDict();
                foreach (var part in materials)
                {
                    string item = V.Str(part.Key); long cost = V.I64(part.Value);
                    Assert.Greater(cost, 0);
                    s.InventoryState.AddItem(item, cost);
                    quantities[item] = s.InventoryState.GetQuantity(item);
                }
                AuxiliaryRepairFeasibilityTests.FinishService(s, rig, utility);
                foreach (var part in materials)
                    Assert.AreEqual(quantities.GetInt(V.Str(part.Key)) - V.I64(part.Value), s.InventoryState.GetQuantity(V.Str(part.Key)), "Declared service payment: " + V.Str(part.Key));
                var earned = s.PlayerProgression.GetSummary().DeepCopy(); Assert.IsFalse(s.RequestAuxiliaryService(utility).GetBool("committed"));
                Assert.IsTrue(PaidHashContext.BitsV2.Equal(earned, s.PlayerProgression.GetSummary())); Valid(s, 6, 5);
                const string book = "fabrication_schematic_basic"; s.InventoryState.AddItem(book, 1);
                Assert.IsTrue(s.RequestManualStudy(book).GetBool("committed"));
                for (int i = 0; i < 80 && s.ManualStudyRunning; i++)
                { var frame = TickContext.Frame(.5, rig.Scene.PlayerPosition, false); frame.InBreachZone = false; frame.InFireZoneCompartment = ""; s.Tick(frame); }
                Assert.AreEqual("completed", s.GetManualStudyState().GetDictOrEmpty("job").GetString("status"));
                earned = s.PlayerProgression.GetSummary().DeepCopy(); Assert.AreEqual("already_studied", s.RequestManualStudy(book).GetString("reason"));
                Assert.IsTrue(PaidHashContext.BitsV2.Equal(earned, s.PlayerProgression.GetSummary())); Valid(s, 6, 5);
                s.PlayerProgression.Skills["fabrication"] = 4L;
                const string recipe = "weld_plating", kind = "workbench";
                foreach (var row in s.CraftingState.GetRecipe(recipe).GetDictOrEmpty("ingredients")) s.InventoryState.AddItem(V.Str(row.Key), V.I64(row.Value) * 2);
                s.CraftingState.GetStation(kind).SetPower(true);
                var started = s.RequestPaidCraft(kind, recipe, "v2-real-start"); Assert.IsTrue(started.GetBool("committed"), GdJson.Stringify(started));
                s.AdvanceCrafting(100); Valid(s, 6, 5);
                var inventory = s.InventoryState.GetSummary().DeepCopy(); earned = s.PlayerProgression.GetSummary().DeepCopy();
                var repeated = s.RequestPaidCraft(kind, recipe, "v2-real-start"); Assert.IsTrue(repeated.GetBool("ok"));
                Assert.IsTrue(PaidHashContext.BitsV2.Equal(inventory, s.InventoryState.GetSummary()));
                Assert.IsTrue(PaidHashContext.BitsV2.Equal(earned, s.PlayerProgression.GetSummary()));
                Assert.IsTrue(s.RequestSaveToSlot("slot_01", "manual", "V2 real work"), GdJson.Stringify(s.LastSaveResult));
                Assert.IsTrue(s.ApplySelectedGeneration(s.SaveLoadService.SelectGeneration("slot_01")), GdJson.Stringify(s.LastSaveResult)); Valid(s, 6, 5);
            }
            finally { s.Dispose(); }
        }
    }
}
