using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class AutosavePolicyTests
    {
        [Test]
        public void Tick_ForceEventsAndRotation_MatchSmoke()
        {
            var clock = new ManualClock();
            var policy = new AutosavePolicy(clock);
            policy.MinRealIntervalSeconds = 0.0;
            Assert.IsFalse(policy.Tick(0.0, 0).GetBool("should_save")); // zero tick seeds
            policy.Force = true;
            GdDict r1 = policy.Tick(0.0, 0);
            Assert.IsTrue(r1.GetBool("should_save"));
            Assert.AreEqual("autosave_b", r1.GetString("slot_id"));
            Assert.AreEqual("forced", r1.GetString("reason"));

            policy.Reset();
            Assert.IsFalse(policy.Tick(0.0, 0).GetBool("should_save"));
            GdDict ev1 = policy.Tick(0.0, 8);
            Assert.AreEqual("events", ev1.GetString("reason"));
            Assert.AreEqual("autosave_b", ev1.GetString("slot_id"));
            GdDict ev2 = policy.Tick(0.0, 16);
            Assert.AreEqual("autosave_c", ev2.GetString("slot_id"));
            GdDict cadence = policy.Tick(91.0, 16);
            Assert.AreEqual("cadence", cadence.GetString("reason"));
            Assert.AreEqual("autosave_a", cadence.GetString("slot_id"));
        }

        [Test]
        public void BudgetGuardAndQuicksaveCooldown_UseInjectedClock()
        {
            var clock = new ManualClock();
            var policy = new AutosavePolicy(clock);
            policy.Tick(0.0, 0);
            Assert.AreEqual("no_trigger", policy.Tick(0.0, 8).GetString("reason")); // inside the 5 s budget
            clock.Advance(5.0);
            Assert.IsTrue(policy.Tick(0.0, 8).GetBool("should_save"));

            GdDict q1 = policy.TryQuicksave();
            Assert.IsTrue(q1.GetBool("should_save"));
            Assert.AreEqual(SaveSlotState.QuicksaveSlotId, q1.GetString("slot_id"));
            Assert.AreEqual("cooldown", policy.TryQuicksave().GetString("reason"));
            clock.Advance(10.0);
            Assert.AreEqual("manual", policy.TryQuicksave().GetString("reason"));
            policy.SetLastQuicksaveRealTime(-1.0);
            Assert.IsTrue(policy.TryQuicksave().GetBool("should_save"));
        }
    }

    public class SaveIndexStateTests
    {
        static SaveSlotState Row(string id, string kind, long epoch) =>
            new SaveSlotState { SlotId = id, SlotKind = kind, SavedAtEpoch = epoch, SavedAt = "t" + epoch };

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var idx = new SaveIndexState { GodotVersion = "4.7", UpdatedAt = "2026-09-11T00:00:00" };
            idx.AddOrReplace(Row("slot_01", SaveSlotState.SlotKindManual, 100));
            idx.AddOrReplace(Row("autosave_a", SaveSlotState.SlotKindAuto, 300));
            GdDict d = idx.ToDict();
            SaveIndexState restored = SaveIndexState.FromDict(GdJson.ParseString(GdJson.Stringify(d)));
            Assert.IsTrue(V.VariantEquals(d, restored.ToDict()));
        }

        [Test]
        public void ReplaceSortAndReclassify()
        {
            var idx = new SaveIndexState();
            idx.AddOrReplace(Row("slot_01", SaveSlotState.SlotKindManual, 100));
            idx.AddOrReplace(Row("slot_02", SaveSlotState.SlotKindManual, 300));
            idx.AddOrReplace(Row("slot_01", SaveSlotState.SlotKindManual, 200));
            Assert.AreEqual(2, idx.Slots.Count);
            Assert.AreEqual(200, idx.Find("slot_01").SavedAtEpoch);
            List<SaveSlotState> sorted = idx.SortedBySavedAtDesc();
            Assert.AreEqual("slot_02", sorted[0].SlotId);
            Assert.AreEqual(1, idx.ReclassifyCorrupt(GdArray.Of("slot_02")));
            Assert.IsTrue(idx.Find("slot_01").Corrupt);
            Assert.IsTrue(idx.Remove("slot_01"));
            Assert.IsNull(idx.Find("slot_01"));
            // Invalid rows are dropped on load.
            var raw = new GdDict { { "slots", GdArray.Of(new GdDict { { "slot_id", "x" }, { "slot_kind", "bogus" } }) } };
            Assert.AreEqual(0, SaveIndexState.FromDict(raw).Slots.Count);
        }
    }
}
