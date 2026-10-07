using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    // Provisioned real-catalog fixtures, not an earned acquisition or filesystem Continue claim.
    public abstract class SessionFixture : InfraDataTestBase
    {
        protected const string Recipe = "weld_plating";
        protected const string Kind = "workbench";
        readonly List<RunSession> _sessions = new List<RunSession>();

        [TearDown]
        public void DisposeSessions()
        {
            foreach (RunSession session in _sessions) session.Dispose();
            _sessions.Clear();
        }

        protected RunSession Boot(bool components = false)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.EnableComponentIntegration = components;
            rig.Session = RunSession.Create(deps);
            _sessions.Add(rig.Session);
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            rig.Session.ThreatManager.Threats.Clear();
            rig.Session.InventoryState.Items.Clear();
            rig.Session.PlayerProgression.Skills["fabrication"] = 4L;
            rig.Session.VitalsState.Stamina = rig.Session.VitalsState.MaxStamina;
            return rig.Session;
        }

        protected static void Equal(object expected, object actual, string message)
            => Assert.IsTrue(V.VariantEquals(expected, actual), message);

        protected static void Provision(RunSession s, string recipeId = Recipe, long batches = 1)
        {
            GdDict recipe = s.CraftingState.GetRecipe(recipeId);
            Assert.IsFalse(recipe.IsEmpty, "Use a real catalog recipe.");
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
            {
                long count = V.I64(ingredient.Value) * batches;
                Assert.AreEqual(count, s.InventoryState.AddItem(V.Str(ingredient.Key), count));
            }
            string kind = recipe.GetString("station_kind");
            if (kind != "field_crafting")
            {
                Assert.IsTrue(s.CraftingStations.Any(st => st.IsValid && st.StationKind == kind), "Actual registered home station.");
                s.CraftingState.GetOrCreateStation(kind).SetPower(true);
            }
        }

        protected static void Valid(GdDict domain)
            => Assert.IsTrue(DomainBundle.TryCreate(domain, out _, out string reason), "Positive valid owner prerequisite: " + reason);
    }
}
