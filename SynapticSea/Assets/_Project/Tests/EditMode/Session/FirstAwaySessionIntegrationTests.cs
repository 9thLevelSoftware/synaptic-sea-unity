using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    public class FirstAwaySessionIntegrationTests : InfraDataTestBase
    {
        static object Invoke(RunSession session, string method, params object[] args) => typeof(RunSession)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(session, args);
        static RunSession Boot(out SessionHarness.Rig rig)
        {
            var deps = SessionHarness.GoldenDeps(out rig); SessionHarness.OverlayGamePlayability(deps);
            deps.EnablePaidCrafting = true; rig.Session = RunSession.Create(deps);
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason); return rig.Session;
        }
        [Test]
        public void HomeNavigationRefusesRemoteWrongOwnerAndOfflineBoatWithoutTravelOrRewardMutation()
        {
            var s = Boot(out var rig);
            try
            {
                s.EnableReviewedFirstAwayProfile = true; s.AwayFromStart = true;
                var destination = ShipInstance.Create("ship_navigation_test", "navigation_test",
                    new ShipBlueprint(0, 2, 42) { GenerationProfile = FirstAwayGenerationInputs.Profile },
                    new ShipSystemsManager(), new FakeShipRoot { IsInsideTree = true });
                destination.BuiltLayout = s.HomeShip.BuiltLayout;
                s.VisitedShips[destination.MarkerId] = destination; s.CurrentShip = destination;
                var current = s.CurrentShip; var sea = s.SynapticSeaWorld.PlayerPosition;
                string inventory = GdJson.Stringify(s.InventoryState.Items), progression = GdJson.Stringify(s.PlayerProgression.GetSummary());
                rig.Scene.PlayerPosition = new Vec3(800, .5, 800);
                Assert.AreEqual("return_to_lifeboat", s.ReturnHomeFromNavigation().GetString("reason"));
                var bridge = s.BridgeTerminals.Single(t => t.ShipId == s.LifeboatShip.ShipId);
                rig.Scene.PlayerPosition = bridge.GlobalPosition;
                s.LifeboatShip.GetAccess().OwnerId = "other_player"; s.LifeboatShip.GetAccess().AccessIds.Clear();
                Assert.AreEqual("ship_access_denied", s.ReturnHomeFromNavigation().GetString("reason"));
                s.LifeboatShip.GetAccess().Claim("player_local");
                s.LifeboatShip.SystemsManager.DamageSubcomponent("propulsion", "thruster_array", 1);
                Assert.IsFalse(s.ReturnHomeFromNavigation().GetBool("success"), "unavailable normal travel capability cannot return");
                s.EnableReviewedFirstAwayProfile = false;
                Assert.AreEqual("home_navigation_unavailable", s.ReturnHomeFromNavigation().GetString("reason"));
                Assert.AreSame(current, s.CurrentShip); Assert.IsTrue(s.AwayFromStart); Assert.AreEqual(sea, s.SynapticSeaWorld.PlayerPosition);
                Assert.AreEqual(inventory, GdJson.Stringify(s.InventoryState.Items));
                Assert.AreEqual(progression, GdJson.Stringify(s.PlayerProgression.GetSummary()));
            }
            finally { s.Dispose(); }
        }

        [Test]
        public void ExplicitPostReadyFlagUpdatesSaveAdmissionAndAcceptedGateReusesExactDocuments()
        {
            var session = Boot(out _);
            try
            {
                Assert.IsFalse(session.EnableReviewedFirstAwayProfile);
                Assert.IsFalse(session.SaveLoadService.FirstAwaySalvageProfileEnabled);
                session.EnableReviewedFirstAwayProfile = true;
                Assert.IsTrue(session.SaveLoadService.FirstAwaySalvageProfileEnabled);
                session.SynapticSeaWorld.WorldSeed = long.MaxValue - 17;
                var marker = new MarkerGenerator().MarkersForCell(session.SynapticSeaWorld.WorldSeed, new Vec2i(0, 0))[0];
                var accepted = (GdDict)Invoke(session, "ApplyFirstRunContractToMarker", marker);
                Assert.IsTrue(accepted.GetBool("success"), accepted.GetString("reason"));
                var docs = (ShipDocuments)typeof(RunSession).GetField("_acceptedFirstAwayDocuments", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(session);
                Assert.AreEqual(session.SynapticSeaWorld.WorldSeed, docs.FirstAwayInputs.WorldSeed);
                Assert.AreEqual("ship_0:0:0", docs.FirstAwayInputs.OwnerId);
                var generator = (IShipGenerator)Invoke(session, "TravelGenerationSource", marker);
                Assert.AreSame(docs, generator.GenerateFromSeed(marker.SeedValue, marker.SizeClass, marker.Condition), "ordinary travel uses the contract-accepted package itself");
                Assert.IsNull(generator.GenerateFromSeed(marker.SeedValue + 1, marker.SizeClass, marker.Condition));
                Assert.IsFalse(session.SynapticSeaWorld.IsGenerated(marker.MarkerId), "gate alone never admits a visited marker");
                session.EnableReviewedFirstAwayProfile = false;
                Assert.IsFalse(session.SaveLoadService.FirstAwaySalvageProfileEnabled);
            }
            finally { session.Dispose(); }
        }
        [Test]
        public void InvalidFullIdentityFailsGateWithoutMarkerOrWorldMutation()
        {
            var session = Boot(out _);
            try
            {
                session.EnableReviewedFirstAwayProfile = true;
                var marker = new ShipMarker { MarkerId = "not-a-sector", SeedValue = 123, SizeClass = 2, Condition = 1 };
                GdDict before = session.SynapticSeaWorld.GetSummary().DeepCopy();
                var result = (GdDict)Invoke(session, "ApplyFirstRunContractToMarker", marker);
                Assert.IsFalse(result.GetBool("success")); Assert.AreEqual(123, marker.SeedValue);
                Assert.IsTrue(V.VariantEquals(before, session.SynapticSeaWorld.GetSummary()));
            }
            finally { session.Dispose(); }
        }
        [Test]
        public void RetainedArchiveKitMustMatchPinnedRawBytesAndPathBeforeMaterialization()
        {
            var session = Boot(out _);
            try
            {
                session.EnableReviewedFirstAwayProfile = true;
                var source = new MarkerGenerator().MarkersForCell(session.SynapticSeaWorld.WorldSeed, new Vec2i(0, 0))[0];
                var input = new FirstAwayGenerationInputs(42, session.SynapticSeaWorld.WorldSeed, source.SizeClass, source.Condition,
                    source.MarkerId, "ship_" + source.MarkerId, "breach_field", "standard");
                var docs = session.ShipGenerator.GenerateFirstAway(input); Assert.IsNotNull(docs);
                var bp = new ShipBlueprint(input.Size, input.Condition, 42) { GenerationProfile = FirstAwayGenerationInputs.Profile,
                    FirstAwayDescriptorText = PaidSnapshotCodec.Stringify(docs.FirstAwayDescriptor.Snapshot()) };
                string rawKit = SynapticSea.Core.Services.CoreServices.Resources.ReadText(docs.KitPath);
                object[] valid = { docs, bp, input.MarkerId, input.OwnerId, input.WorldSeed, null, rawKit };
                Assert.IsTrue((bool)Invoke(session, "ValidateFirstAwayDocuments", valid), "exact archived kit accepted");
                object[] changedWhitespace = { docs, bp, input.MarkerId, input.OwnerId, input.WorldSeed, null, rawKit + "\n" };
                Assert.IsFalse((bool)Invoke(session, "ValidateFirstAwayDocuments", changedWhitespace), "semantic equality cannot replace exact catalog bytes");
                string wrongPath = docs.KitPath.EndsWith("ship_structural_v0.json")
                    ? "res://data/kits/ship_structural_hazard.json" : "res://data/kits/ship_structural_v0.json";
                var swapped = new ShipDocuments { Layout = docs.Layout, LayoutJson = docs.LayoutJson, GameplaySliceJson = docs.GameplaySliceJson,
                    KitPath = wrongPath, Kit = GdJson.ParseString(SynapticSea.Core.Services.CoreServices.Resources.ReadText(wrongPath)) as GdDict };
                object[] wrong = { swapped, bp, input.MarkerId, input.OwnerId, input.WorldSeed, null,
                    SynapticSea.Core.Services.CoreServices.Resources.ReadText(wrongPath) };
                Assert.IsFalse((bool)Invoke(session, "ValidateFirstAwayDocuments", wrong), "different pinned kit is still wrong for this layout");
                Assert.IsFalse(session.SynapticSeaWorld.IsGenerated(input.MarkerId));
            }
            finally { session.Dispose(); }
        }

        [Test]
        public void CargoWorkUsesProjectedOwnedReservedObjectiveAndRefusesMissingAnchor()
        {
            var session = Boot(out _);
            try
            {
                session.EnableReviewedFirstAwayProfile = true;
                var source = new MarkerGenerator().MarkersForCell(session.SynapticSeaWorld.WorldSeed, new Vec2i(0, 0))[0];
                var input = new FirstAwayGenerationInputs(42, session.SynapticSeaWorld.WorldSeed, source.SizeClass, source.Condition, source.MarkerId, "ship_" + source.MarkerId, "breach_field", "standard");
                var docs = session.ShipGenerator.GenerateFirstAway(input); Assert.IsNotNull(docs);
                var bp = new ShipBlueprint(input.Size, input.Condition, 42) { GenerationProfile = FirstAwayGenerationInputs.Profile,
                    FirstAwayDescriptorText = PaidSnapshotCodec.Stringify(docs.FirstAwayDescriptor.Snapshot()) };
                // Persist exact descriptor string separately from mutable ship state and raw layout hashes.
                Assert.AreEqual(bp.FirstAwayDescriptorText, ShipBlueprint.FromDict(bp.ToDict()).FirstAwayDescriptorText);
                var loader = new FakeLoaderView(docs.Layout, docs.GameplaySlice, "");
                session.CurrentShip = ShipInstance.Create(input.OwnerId, input.MarkerId, bp, new ShipSystemsManager(), loader);
                object[] args = { loader, Vec3.Zero };
                Assert.IsTrue((bool)Invoke(session, "TryFirstAwayCargoWorkPosition", args));
                var spec = loader.GetObjectiveSpecsCopy().OfType<GdDict>().Single(row => row.GetString("room_id") == docs.Layout.GetString("first_away_reservation_owner"));
                Assert.AreEqual(spec.Get("position"), args[1], "work uses loader floor projection rather than guessed height");
                loader.Model.ObjectiveSpecs = new GdArray();
                Assert.IsFalse((bool)Invoke(session, "TryFirstAwayCargoWorkPosition", new object[] { loader, Vec3.Zero }));
            }
            finally { session.Dispose(); }
        }
    }
}
