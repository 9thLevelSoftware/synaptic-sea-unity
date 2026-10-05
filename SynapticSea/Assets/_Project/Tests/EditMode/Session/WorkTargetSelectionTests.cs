using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    // Provisioned Core admission regressions. Physical acquisition/input proof belongs to the pinned checkpoint.
    public class WorkTargetSelectionTests : PaidCraftFixture
    {
        sealed class Sight : ILineOfSightProbe
        {
            public bool Blocked;
            public bool HasSpace => true;
            public bool IntersectRay(Vec3 from, Vec3 to, out Vec3 hit) { hit = (from + to) * .5f; return Blocked; }
        }
        RunSession WorkBoot(bool paid = false, bool study = false)
        {
            var session = Boot(paid: paid, manualStudy: study);
            session.PlayerProgression.Configure(ClassDefinition.LoadAll()["cook"], PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            Assert.AreEqual(3, session.RepairPoints.Single(p => p.SystemId == "gravity" && p.SubcomponentId == "field_emitter").MinSkill);
            return session;
        }
        static BreachSealPoint Cargo(RunSession s) => s.BreachSealPoints.Single(p => p.CompartmentId == "cargo");
        static void Stand(RunSession s, SessionInteractable p) => ((FakeSceneState)s.Scene).PlayerPosition = p.GlobalPosition;
        static void StockRepair(RunSession s, RepairPoint p)
        {
            var sub = p.TargetManager.GetSystem(p.SystemId).GetSubcomponent(p.SubcomponentId);
            foreach (string item in sub.RequiredParts.Concat(sub.RequiredTools).Distinct())
                if (s.InventoryState.GetQuantity(item) < 1) Assert.AreEqual(1, s.InventoryState.AddItem(item, 1));
        }
        static void NoSeal(RunSession s, BreachSealPoint p, string reason)
        {
            long items = s.InventoryState.GetQuantity(p.RequiredItem), xp = s.PlayerProgression.GetSkillXp("repair");
            var result = s.RequestWorkTarget(p);
            Assert.IsFalse(result.GetBool("ok")); Assert.IsFalse(result.GetBool("started"));
            Assert.AreEqual(reason, result.GetString("reason")); Assert.IsFalse(p.Channeling);
            Assert.AreEqual(items, s.InventoryState.GetQuantity(p.RequiredItem)); Assert.AreEqual(xp, s.PlayerProgression.GetSkillXp("repair"));
        }

        [Test]
        public void ExactOverlappingSealStartsCompletesPaysAndAwardsOnce()
        {
            var s = WorkBoot(); var seal = Cargo(s);
            var repair = s.RepairPoints.Single(p => p.SystemId == "gravity" && p.SubcomponentId == "field_emitter");
            repair.Parent = seal.Parent; repair.LocalPosition = seal.LocalPosition;
            StockRepair(s, repair); s.InventoryState.AddItem(seal.RequiredItem, 2); Stand(s, seal);
            Assert.AreEqual("insufficient_skill", repair.DescribeReason());
            CollectionAssert.Contains(s.ListNearbyWorkTargets(), repair);
            CollectionAssert.Contains(s.ListNearbyWorkTargets(), seal);
            long qty = s.InventoryState.GetQuantity(seal.RequiredItem), xp = s.PlayerProgression.GetSkillXp("repair");
            var started = s.RequestWorkTarget(seal);
            Assert.IsTrue(started.GetBool("ok")); Assert.IsTrue(started.GetBool("handled"));
            Assert.AreEqual("breach_seal_point", started.GetString("handler")); Assert.IsFalse(repair.Channeling);
            NoSealWhileActive(s, seal);
            seal.Process(seal.SealSeconds + 1, s.Scene.PlayerPosition);
            Assert.IsTrue(seal.Sealed); Assert.AreEqual(qty - 1, s.InventoryState.GetQuantity(seal.RequiredItem));
            Assert.AreEqual(xp + 12, s.PlayerProgression.GetSkillXp("repair"));
            NoSeal(s, seal, "completed");
            CollectionAssert.DoesNotContain(s.ListNearbyWorkTargets(), seal);
        }
        static void NoSealWhileActive(RunSession s, BreachSealPoint seal)
        {
            double progress = seal.Progress;
            var result = s.RequestWorkTarget(seal);
            Assert.IsFalse(result.GetBool("ok")); Assert.AreEqual("work_busy", result.GetString("reason"));
            Assert.IsTrue(seal.Channeling); Assert.AreEqual(progress, seal.Progress);
        }
        [Test]
        public void DeliberateUnavailableRepairEmitsOwnDenialAndDoesNotFallBack()
        {
            var s = WorkBoot(); var seal = Cargo(s);
            var repair = s.RepairPoints.Single(p => p.SystemId == "gravity" && p.SubcomponentId == "field_emitter");
            repair.Parent = seal.Parent; repair.LocalPosition = seal.LocalPosition; StockRepair(s, repair);
            s.InventoryState.AddItem(seal.RequiredItem, 1); Stand(s, repair);
            string denial = ""; int count = 0;
            repair.RepairBlocked += (sys, sub, reason) => { denial = reason; count++; };
            long qty = s.InventoryState.GetQuantity(seal.RequiredItem), xp = s.PlayerProgression.GetSkillXp("repair");
            Assert.AreEqual("insufficient_skill", s.DescribeWorkTarget(repair).GetString("reason"));
            var result = s.RequestWorkTarget(repair);
            Assert.IsTrue(result.GetBool("handled")); Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual("insufficient_skill", result.GetString("reason")); Assert.AreEqual("insufficient_skill", denial); Assert.AreEqual(1, count);
            Assert.IsFalse(seal.Channeling); Assert.IsFalse(repair.Channeling);
            Assert.AreEqual(qty, s.InventoryState.GetQuantity(seal.RequiredItem)); Assert.AreEqual(xp, s.PlayerProgression.GetSkillXp("repair"));
        }
        [TestCase("inventory")][TestCase("progression")][TestCase("hull")][TestCase("owner")]
        public void ChangedSealBindingsOrOwnerRefuse(string binding)
        {
            var s = WorkBoot(); var seal = Cargo(s); s.InventoryState.AddItem(seal.RequiredItem, 1); Stand(s, seal);
            if (binding == "inventory") seal.InventoryState = null;
            if (binding == "progression") seal.PlayerProgression = null;
            if (binding == "hull") seal.HullState = null;
            if (binding == "owner") seal.Parent = new FakeShipRoot { IsInsideTree = true };
            NoSeal(s, seal, binding == "owner" ? "stale_owner" : "invalid_binding");
        }
        [Test]
        public void FabricatedSameIdAndReplacementAfterContinueNeverAuthorizeOldReference()
        {
            var s = WorkBoot(); var old = Cargo(s); Stand(s, old);
            var fake = new BreachSealPoint();
            fake.Configure(old.CompartmentId, old.HullState, old.InventoryState, old.PlayerProgression, old.LocalPosition, old.SealSeconds, old.RequiredItem, old.SealAmount);
            fake.Parent = old.Parent;
            NoSeal(s, fake, "stale_target");
            Assert.IsTrue(s.ApplyManualSlot(RunSnapshotAssembler.Build(s)));
            Assert.AreNotSame(old, Cargo(s)); Stand(s, Cargo(s)); NoSeal(s, old, "stale_target");
        }
        [Test]
        public void MovementAndSightRevalidateAtConfirmationDespiteCandidateFlag()
        {
            var s = WorkBoot(); var seal = Cargo(s); s.InventoryState.AddItem(seal.RequiredItem, 1); Stand(s, seal);
            var sight = new Sight(); s.Deps.LosProbe = sight;
            CollectionAssert.Contains(s.ListNearbyWorkTargets(), seal);
            ((FakeSceneState)s.Scene).PlayerPosition += new Vec3(20, 0, 0); seal.CandidatePlayerInRange = true;
            NoSeal(s, seal, "out_of_range"); CollectionAssert.DoesNotContain(s.ListNearbyWorkTargets(), seal);
            Stand(s, seal); sight.Blocked = true; NoSeal(s, seal, "no_line_of_sight");
            CollectionAssert.DoesNotContain(s.ListNearbyWorkTargets(), seal);
        }
        [Test]
        public void DescriptionAndListingDoNotStartOrLatchWorkAndMissingSealantKeepsFeedback()
        {
            var s = WorkBoot(); var seal = Cargo(s); Stand(s, seal);
            int denied = 0; seal.SealBlocked += (id, why) => { Assert.AreEqual("missing_sealant", why); denied++; };
            for (int i = 0; i < 3; i++) { s.ListNearbyWorkTargets(); Assert.AreEqual("missing_sealant", s.DescribeWorkTarget(seal).GetString("reason")); }
            Assert.AreEqual(0, denied); Assert.IsFalse(s.IsWorkInteractHeld); Assert.IsFalse(seal.Channeling);
            var result = s.RequestWorkTarget(seal); Assert.IsTrue(result.GetBool("handled")); Assert.IsFalse(result.GetBool("ok")); Assert.AreEqual(1, denied);
        }
        [Test]
        public void ActiveAndRestoredHoldWorkCannotBeRedirected()
        {
            var s = WorkBoot(); var seal = Cargo(s); Stand(s, seal); s.InventoryState.AddItem(seal.RequiredItem, 1);
            s.WorkActionDriver.Work = new WorkActionState { Status = WorkActionState.STATUS_ACTIVE };
            NoSeal(s, seal, "work_busy");
            s.WorkActionDriver.Work.Status = WorkActionState.STATUS_INTERRUPTED;
            typeof(RunSession).GetField("_workAwaitingResume", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(s, true);
            NoSeal(s, seal, "work_busy");
        }
        [Test]
        public void PausedPaidOrdinaryJobCannotBeRedirected()
        {
            var s = WorkBoot(paid: true); Provision(s); string id = Start(s);
            Assert.IsTrue(s.RestorePaidCraftingDomain(s.CapturePaidCraftingDomain()));
            Assert.AreEqual("paused", Job(s, id).GetString("status"));
            var seal = Cargo(s); Stand(s, seal); NoSeal(s, seal, "work_busy");
        }
        [Test]
        public void PausedStudyCannotBeRedirected()
        {
            var s = WorkBoot(paid: true, study: true); s.InventoryState.AddItem("fabrication_schematic_basic", 1);
            Assert.IsTrue(s.RequestManualStudy("fabrication_schematic_basic").GetBool("committed"));
            Assert.IsTrue(s.PauseManualStudy("released").GetBool("committed"));
            var seal = Cargo(s); Stand(s, seal); NoSeal(s, seal, "work_busy");
        }
        [Test]
        public void UnsupportedTargetDoesNotDispatchEarlierDoorOrLootHandlers()
        {
            var s = WorkBoot(); var loot = s.LootContainers.First(); Stand(s, loot);
            var result = s.RequestWorkTarget(loot);
            Assert.AreEqual("unsupported_target", result.GetString("reason")); Assert.IsFalse(result.GetBool("handled")); Assert.IsFalse(loot.Searched);
        }
    }
}
