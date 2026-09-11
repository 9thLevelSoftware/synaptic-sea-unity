using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class PlayerProgressionStateTests : ProgressionDataTestBase
    {
        static PlayerProgressionState MakeEngineer(out GdDict catalog)
        {
            catalog = PlayerProgressionState.LoadSkillsCatalog();
            var prog = new PlayerProgressionState();
            prog.Configure(ClassDefinition.LoadAll()["engineer"], catalog, PlayerProgressionState.LoadBooksCatalog());
            return prog;
        }

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var prog = MakeEngineer(out GdDict catalog);
            prog.GrantXp("repair", 100);
            prog.GrantXp("repair", 250);
            prog.GrantXp("surgery", 33, true);
            GdDict summary = prog.GetSummary();
            var restored = new PlayerProgressionState();
            restored.Configure(ClassDefinition.LoadAll()["engineer"], catalog);
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.AreEqual(4, restored.GetSkillLevel("repair"));
        }

        [Test]
        public void GrantXp_MatchesSmoke()
        {
            var prog = MakeEngineer(out GdDict catalog);
            Assert.AreEqual("technical", catalog.GetDictOrEmpty("repair").GetString("category"));
            Assert.AreEqual(3, prog.GetSkillLevel("repair"));
            Assert.AreEqual(0, prog.GetSkillLevel("surgery"));
            Assert.AreEqual(400, PlayerProgressionState.XpForNextLevel(3));
            Assert.IsFalse(prog.GrantXp("repair", 100)); // 150 effective < 400
            Assert.IsTrue(prog.GrantXp("repair", 250));  // 525 >= 400 -> level 4
            Assert.AreEqual(4, prog.GetSkillLevel("repair"));
            Assert.IsFalse(prog.GrantXp("not_a_skill", 100));
            Assert.IsFalse(prog.GrantXp("repair", 0));
            Assert.IsFalse(prog.GrantXp("repair", -50));

            var cap = MakeEngineer(out _);
            cap.GrantXp("repair", 1000000);
            Assert.AreEqual(10, cap.GetSkillLevel("repair"));
            Assert.IsFalse(cap.GrantXp("repair", 1000));
        }

        [Test]
        public void ApplySummary_ReloadsClassMultipliers()
        {
            GdDict catalog = PlayerProgressionState.LoadSkillsCatalog();
            var medic = new PlayerProgressionState();
            medic.Configure(ClassDefinition.LoadAll()["medic"], catalog);
            var prog = new PlayerProgressionState();
            prog.Configure(ClassDefinition.LoadAll()["engineer"], catalog);
            prog.ApplySummary(medic.GetSummary());
            prog.GrantXp("repair", 100); // medic technical 0.7 -> 70
            Assert.AreEqual(70, prog.GetSkillXp("repair"));
            ISkillLevelSource source = prog;
            Assert.AreEqual(prog.GetSkillLevel("repair"), source.GetSkillLevel("repair"));
        }
    }

    public class TrainingEventBusTests : ProgressionDataTestBase
    {
        static PlayerProgressionState MakeEngineer()
        {
            var prog = new PlayerProgressionState();
            prog.Configure(ClassDefinition.LoadAll()["engineer"], PlayerProgressionState.LoadSkillsCatalog(), PlayerProgressionState.LoadBooksCatalog());
            return prog;
        }

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var bus = new TrainingEventBus();
            Assert.IsTrue(bus.Configure());
            var prog = MakeEngineer();
            Assert.IsNotNull(bus.Emit("scavenge_container", "crate_01", prog));
            Assert.IsNull(bus.Emit("not_an_event", "", prog));
            GdDict summary = bus.ToDict();
            var restored = new TrainingEventBus();
            Assert.IsTrue(restored.Configure());
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.ToDict()));
            Assert.AreEqual(1, restored.GetDroppedCount());

            var fresh = MakeEngineer();
            Assert.AreEqual(bus.GetTotalXpDelivered(), restored.ReplayInto(fresh));
            Assert.AreEqual(prog.GetSkillXp("scavenging"), fresh.GetSkillXp("scavenging"));
        }

        [Test]
        public void SkillGate_LogsButSuppressesXp()
        {
            var bus = new TrainingEventBus();
            Assert.IsTrue(bus.Configure());
            var prog = MakeEngineer();
            bool surgeryUnlocked = false;
            bus.SkillGate = skillId => skillId != "surgery" || surgeryUnlocked;
            long before = prog.GetSkillXp("surgery");
            GdDict r1 = bus.Emit("perform_surgery", "", prog);
            Assert.IsNotNull(r1);
            Assert.IsTrue(r1.GetBool("gated"));
            Assert.AreEqual(before, prog.GetSkillXp("surgery"));
            Assert.AreEqual(0, bus.GetDroppedCount());
            Assert.AreEqual(1, bus.GetEventCount());

            GdDict r2 = bus.Emit("scavenge_container", "", prog);
            Assert.IsFalse(r2.GetBool("gated"));
            Assert.Greater(prog.GetSkillXp("scavenging"), 0);

            surgeryUnlocked = true;
            GdDict r3 = bus.Emit("perform_surgery", "", prog);
            Assert.IsFalse(r3.GetBool("gated"));
            Assert.Greater(prog.GetSkillXp("surgery"), before);
            Assert.IsTrue(r3.GetBool("is_cross_training")); // engineer primary is technical
        }

        [Test]
        public void EventFilter_DropsAndCallbackFires()
        {
            var bus = new TrainingEventBus();
            Assert.IsTrue(bus.Configure());
            var prog = MakeEngineer();
            int resolved = 0;
            bus.OnEventResolved = _ => resolved++;
            bus.EventFilter = (eventId, targetId) => targetId == "blocked";
            Assert.IsNull(bus.Emit("scavenge_container", "blocked", prog));
            Assert.IsNotNull(bus.Emit("scavenge_container", "ok", prog));
            Assert.AreEqual(1, bus.GetDroppedCount());
            Assert.AreEqual(1, resolved);
        }
    }
}
