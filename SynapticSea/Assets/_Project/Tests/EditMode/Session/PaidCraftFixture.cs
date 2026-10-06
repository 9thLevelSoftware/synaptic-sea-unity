using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    // Provisioned real-catalog fixtures, not an earned acquisition or filesystem Continue claim.
    public abstract class PaidCraftFixture : InfraDataTestBase
    {
        protected const string Recipe = "weld_plating";
        protected const string Kind = "workbench";
        readonly List<RunSession> _sessions = new List<RunSession>();

        [TearDown]
        public void DisposePaidSessions()
        {
            foreach (RunSession session in _sessions) session.Dispose();
            _sessions.Clear();
        }

        protected RunSession Boot(bool components = false, bool paid = true, bool manualStudy = false)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.EnableComponentIntegration = components;
            deps.EnableManualStudy = manualStudy;
            // Deliberately optional in RED: reach the actual existing start before asserting missing authority.
            typeof(RunSessionDeps).GetField("EnablePaidCrafting")?.SetValue(deps, paid);
            rig.Session = RunSession.Create(deps);
            _sessions.Add(rig.Session);
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            rig.Session.ThreatManager.Threats.Clear();
            rig.Session.InventoryState.Items.Clear();
            rig.Session.PlayerProgression.Skills["fabrication"] = 4L;
            rig.Session.VitalsState.Stamina = rig.Session.VitalsState.MaxStamina;
            return rig.Session;
        }

        protected static object Call(RunSession session, string name, params object[] arguments)
        {
            MethodInfo method = typeof(RunSession).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .SingleOrDefault(m => m.Name == name && m.GetParameters().Length == arguments.Length);
            Assert.IsNotNull(method, "Paid crafting requires RunSession." + name + " (" + arguments.Length +
                " arguments) through the existing sole DomainTransactionCoordinator; legacy summaries cannot prove payment.");
            try { return method.Invoke(session, arguments); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        protected static GdDict Command(RunSession s, string name, params object[] arguments)
            => (GdDict)Call(s, name, arguments);
        protected static GdDict Capture(RunSession s) => Command(s, "CapturePaidCraftingDomain");
        protected static GdDict Paid(GdDict domain) => domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting");
        protected static GdDict Jobs(GdDict domain) => Paid(domain).GetDictOrEmpty("jobs");
        protected static GdDict Job(RunSession s, string id) => Jobs(Capture(s)).GetDictOrEmpty(id);
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

        protected static string Start(RunSession s, string recipeId = Recipe, string commandId = "start-1")
        {
            GdDict result = Command(s, "RequestPaidCraft", s.CraftingState.GetStationKind(recipeId), recipeId, commandId);
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            Assert.IsTrue(result.GetBool("committed"), "Payment and job must publish together.");
            Assert.IsNotEmpty(result.GetString("job_id"));
            Assert.IsNotEmpty(result.GetString("commit_id"));
            return result.GetString("job_id");
        }

        protected static void Valid(GdDict domain)
            => Assert.IsTrue(DomainBundle.TryCreate(domain, out _, out string reason), "Positive valid owner prerequisite: " + reason);

        protected static string Enqueue(RunSession s, string commandId)
        {
            GdDict result = Command(s, "EnqueuePaidCraft", Kind, Recipe, commandId);
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            Assert.IsTrue(result.GetBool("committed"));
            Assert.IsNotEmpty(result.GetString("job_id"));
            return result.GetString("job_id");
        }
    }
}
