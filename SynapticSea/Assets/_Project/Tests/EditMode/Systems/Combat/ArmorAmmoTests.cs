using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ArmorResolverTests
    {
        static ArmorResolver SmokeArmor()
        {
            var armor = new ArmorResolver();
            armor.Configure(new GdDict
            {
                { "flat_reduction", new GdDict { { "physical", 2.0 } } },
                { "resistance", new GdDict { { "physical", 0.25 }, { "fire", -0.10 } } },
                { "durability", 20.0 },
                { "max_durability", 20.0 },
                { "wear_factor", 0.5 },
            });
            return armor;
        }

        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var armor = SmokeArmor();
            armor.ResolveDamage(new GdDict { { "damage_type", "physical" }, { "amount", 10.0 } });
            GdDict summary = armor.GetSummary();

            var restored = new ArmorResolver();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsFalse(restored.ApplySummary(summary));
        }

        [Test]
        public void FlatThenResistance_WearsDurability_WeaknessAmplifies()
        {
            var armor = SmokeArmor();
            GdDict result = armor.ResolveDamage(new GdDict { { "damage_type", "physical" }, { "amount", 10.0 } });
            Assert.AreEqual(6.0, V.F64(result["final_damage"]), 0.01);
            Assert.AreEqual(18.0, V.F64(result["durability"]), 0.01);
            GdDict fire = armor.ResolveDamage(new GdDict { { "damage_type", "fire" }, { "amount", 10.0 } });
            Assert.That(V.F64(fire["final_damage"]), Is.GreaterThan(10.0));
            CollectionAssert.AreEqual(new[] { "Armor durability: 18.0", "Last hit: fire 10.0 -> 11.0" }, armor.GetStatusLines());
        }
    }

    public class AmmoStateTests
    {
        [Test]
        public void Summary_RoundTripsThroughFreshInstance()
        {
            var b = new AmmoState();
            b.Configure(new GdDict { { "magazines", new GdDict { { "shock_probe", 3L } } } });
            b.BeginReload("shock_probe", 5, 4);
            GdDict summary = b.GetSummary();

            var c = new AmmoState();
            Assert.IsTrue(c.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, c.GetSummary()));
            Assert.AreEqual(3, c.Loaded("shock_probe"));
            Assert.IsTrue(c.IsReloading());
            Assert.AreEqual(2, c.ReloadTarget);
        }

        [Test]
        public void SpendReloadAndComplete()
        {
            var a = new AmmoState();
            a.Configure(new GdDict { { "magazines", new GdDict { { "flare_pistol", 1L } } } });
            Assert.IsTrue(a.Spend("flare_pistol"));
            Assert.AreEqual(0, a.Loaded("flare_pistol"));
            Assert.IsFalse(a.Spend("flare_pistol"));
            Assert.IsTrue(a.BeginReload("flare_pistol", 2, 5));
            Assert.AreEqual(2, a.ReloadTarget);
            Assert.IsTrue(a.Tick(0.5).IsEmpty);
            CollectionAssert.AreEqual(new[] { "Mag flare_pistol=0", "Reloading flare_pistol (1.0s)" }, a.GetStatusLines());
            GdDict done = a.Tick(2.0);
            Assert.AreEqual("flare_pistol", done["weapon_id"]);
            Assert.AreEqual(2L, done["loaded"]);
            Assert.AreEqual(2, a.Loaded("flare_pistol"));
            Assert.IsFalse(a.IsReloading());
        }
    }
}
