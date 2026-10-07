using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    public class ComponentPreparationTests : SessionFixture
    {
        [Test]
        public void ComponentPreparation_RejectsUnrelatedStackMutation()
        {
            RunSession s = Boot(components: true);
            GdDict domain = s.CaptureComponentDomain(); Valid(domain);
            GdDict holders = domain.GetDictOrEmpty("holders");
            GdDict instance = domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values.OfType<GdDict>()
                .First(row => holders.GetDictOrEmpty(row.GetString("holder")).GetString("kind") == "slot");
            string source = instance.GetString("holder"), destination = "player:player_local";
            var command = new GdDict {
                { "schema_version", 1L }, { "command_id", "conservation-control" }, { "operation", "transfer" },
                { "instance_id", instance.GetString("instance_id") }, { "source_holder_id", source }, { "destination_holder_id", destination },
                { "expected_domain_revision", domain.Get("revision") }, { "expected_instance_revision", instance.Get("revision") },
                { "expected_source_revision", holders.GetDictOrEmpty(source).Get("revision") }, { "expected_destination_revision", holders.GetDictOrEmpty(destination).Get("revision") }
            };
            MethodInfo prepare = typeof(DomainTransactionCoordinator).GetMethod("PrepareLive", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(prepare, "Existing private session staging seam; no new arbitrary public candidate API.");
            var control = new DomainTransactionCoordinator(domain);
            GdDict positive = (GdDict)prepare.Invoke(control, new object[] { command, new Func<GdDict, GdDict>(candidate => candidate) });
            Assert.IsTrue(positive.GetBool("ok"), "Actual populated equipment transfer must validate before unrelated mutation.");
            var attacked = new DomainTransactionCoordinator(domain);
            Func<GdDict, GdDict> mutate = candidate => {
                candidate.GetDictOrEmpty("participating_state").GetDictOrEmpty("inventory").GetDictOrEmpty("items")["scrap_metal"] = 999L;
                return candidate;
            };
            bool accepted;
            try { accepted = ((GdDict)prepare.Invoke(attacked, new object[] { command, mutate })).GetBool("ok"); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { accepted = false; }
            Assert.IsFalse(accepted, "Component-only preparation cannot mint stacks.");
            Equal(domain, attacked.GetSummary(), "Rejected preparation leaves committed owner intact.");
        }
    }
}
