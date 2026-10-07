using NUnit.Framework;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>Explicit diagnostic snapshots, without live stores, catalogs, generated IDs or default condition.</summary>
    public static class ComponentDomainTestFixtures
    {
        public static GdDict Row(string id, string holder, double? condition)
            => new GdDict
            {
                { "schema_version", 1L }, { "instance_id", id }, { "definition_id", "reactor_console" },
                { "item_form", "reactor_console" }, { "condition_state", condition.HasValue ? "known" : "unknown" },
                { "condition", condition.HasValue ? (object)condition.Value : null }, { "mass", 2.5 },
                { "holder", holder }, { "revision", 0L },
                { "origin", new GdDict { { "ship", "source-ship" }, { "slot", "source-console" } } },
                { "provenance", new GdDict { { "kind", "exact_saved_witness" }, { "notes", GdArray.Of("preserve") } } }
            };

        public static GdDict Holder(string id, string kind, string owner)
            => new GdDict { { "holder_id", id }, { "kind", kind }, { "owner_id", owner }, { "revision", 0L } };

        public static GdDict Bundle()
        {
            GdDict slotA = Holder("slot-a", "slot", "ship-a"), slotB = Holder("slot-b", "slot", "ship-b");
            slotA["slot_id"] = "console"; slotB["slot_id"] = "console";
            slotA["accepted_forms"] = GdArray.Of("reactor_console"); slotB["accepted_forms"] = GdArray.Of("reactor_console");
            slotA["machinery_id"] = "machine-a"; slotB["machinery_id"] = "machine-b";
            return new GdDict
            {
                { "schema_version", 1L }, { "revision", 0L },
                { "registry", new GdDict { { "schema_version", 1L }, { "instances", new GdDict
                    { { "a", Row("a", "slot-a", 0.23) }, { "b", Row("b", "bag", 0.81) } } } } },
                { "holders", new GdDict { { "slot-a", slotA }, { "slot-b", slotB },
                    { "bag", Holder("bag", "player", "player") }, { "cargo-b", Holder("cargo-b", "ship_cargo", "ship-b") } } },
                { "machinery", new GdDict
                    { { "machine-a", new GdDict { { "machinery_id", "machine-a" }, { "owner_id", "ship-a" }, { "health", 0.8 } } },
                      { "machine-b", new GdDict { { "machinery_id", "machine-b" }, { "owner_id", "ship-b" }, { "health", 0.2 } } } } },
                { "receipts", new GdDict() }
            };
        }

        public static GdDict Move(string commandId = "move-a", string instanceId = "a", string source = "slot-a", string destination = "bag",
            long domainRevision = 0, long instanceRevision = 0, long sourceRevision = 0, long destinationRevision = 0)
            => new GdDict
            {
                { "schema_version", 1L }, { "command_id", commandId }, { "operation", "transfer" },
                { "instance_id", instanceId }, { "source_holder_id", source }, { "destination_holder_id", destination },
                { "expected_domain_revision", domainRevision }, { "expected_instance_revision", instanceRevision },
                { "expected_source_revision", sourceRevision }, { "expected_destination_revision", destinationRevision }
            };

        public static GdDict Receipt(string commandId = "old-command", long revision = 1)
            => new GdDict
            {
                { "schema_version", 1L }, { "transaction_id", "component_transfer:" + commandId }, { "command_id", commandId },
                { "revision", revision }, { "result", new GdDict
                    { { "operation", "transfer" }, { "instance_id", "a" }, { "source_holder_id", "slot-a" }, { "destination_holder_id", "bag" } } }
            };

        public static GdDict Instances(GdDict summary) => summary.GetDictOrEmpty("registry").GetDictOrEmpty("instances");
        public static void Same(object expected, object actual) => Assert.IsTrue(V.VariantEquals(expected, actual),
            "Expected complete snapshot equality. Expected=" + GdJson.Stringify(expected) + " actual=" + GdJson.Stringify(actual));
    }
}
