using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ShipSystemsManagerTests
    {
        static readonly string[] Dependents = { "life_support", "gravity", "navigation", "propulsion", "scanners" };

        [SetUp]
        public void SetUp()
        {
            CatalogRegistry.Clear();
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        static ShipSystemsManager Make(long condition, long seed)
        {
            var m = new ShipSystemsManager();
            m.Configure(m.LoadDefinitions(), condition, seed);
            return m;
        }

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var src = Make(1, 777);
            src.Advance(2.0);
            GdDict summary = src.GetSummary();
            var dst = Make(0, 777);
            Assert.IsTrue(dst.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, dst.GetSummary()));
            Assert.IsFalse(dst.ApplySummary(new GdDict()), "empty summary rejected");
            Assert.IsFalse(dst.ApplySummary(new GdDict { { "systems", "bad" } }), "malformed summary rejected");
        }

        [Test]
        public void ConditionDamage_IsDeterministicAndScalesWithCondition()
        {
            Assert.IsTrue(V.VariantEquals(Make(1, 4242).GetSummaryHealthList(), Make(1, 4242).GetSummaryHealthList()));
            int Damaged(ShipSystemsManager m)
            {
                int n = 0;
                foreach (object h in m.GetSummaryHealthList())
                    if (V.F64(h) < 1.0) n++;
                return n;
            }
            Assert.AreEqual(0, Damaged(Make(0, 4242)));
            Assert.Greater(Damaged(Make(1, 4242)), 0);
            Assert.GreaterOrEqual(Damaged(Make(2, 4242)), Damaged(Make(1, 4242)));
        }

        [Test]
        public void PowerLoss_CascadesAndDrainsOxygen()
        {
            var mgr = Make(0, 1);
            foreach (string sid in Dependents)
                Assert.IsTrue(mgr.IsOperational(sid), sid);
            var ls = (LifeSupportSystem)mgr.GetSystem("life_support");
            double oxyStart = ls.GetOxygenState().Oxygen;
            mgr.Advance(1.0);
            Assert.GreaterOrEqual(ls.GetOxygenState().Oxygen, oxyStart, "operational life support must not drain");

            mgr.GetSystem("power").GetSubcomponent("reactor_core").Health = 0.0;
            foreach (string sid in Dependents)
                Assert.IsFalse(mgr.IsOperational(sid), sid + " operational while power down");
            double before = ls.GetOxygenState().Oxygen;
            mgr.Advance(1.0);
            Assert.Less(ls.GetOxygenState().Oxygen, before, "offline life support drains");

            mgr.GetSystem("power").GetSubcomponent("reactor_core").Health = 1.0;
            mgr.GetSystem("navigation").GetSubcomponent("nav_computer").Health = 0.0;
            Assert.IsFalse(mgr.IsOperational("scanners"));
            Assert.IsFalse(mgr.IsOperational("propulsion"));
            Assert.IsTrue(mgr.IsOperational("gravity"), "gravity only depends on power");
        }

        [Test]
        public void Repair_ReportsReasonsAndRestoresPower()
        {
            var ship = Make(0, 1);
            ship.GetSystem("power").GetSubcomponent("reactor_core").Health = 0.0;
            Assert.AreEqual("unknown_system", ship.Repair("warp", "x", new GdArray(), new GdArray(), 9).GetString("reason"));
            Assert.AreEqual("unknown_subcomponent", ship.Repair("power", "nope", new GdArray(), new GdArray(), 9).GetString("reason"));
            Assert.IsFalse(ship.Repair("power", "reactor_core", new GdArray(), new GdArray(), 9).GetBool("success"));
            GdDict ok = ship.Repair("power", "reactor_core", GdArray.Of("reactor_core"), GdArray.Of("plasma_cutter"), 4);
            Assert.IsTrue(ok.GetBool("success"), ok.ToString());
            Assert.IsTrue(ship.IsOperational("power"));
        }
    }
}
