using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// Locks the interaction dispatcher order against docs/InteractionOrder.md and the two Godot chains of
    /// _on_player_interact_requested (playable_generated_ship.gd 7953-8077).
    /// </summary>
    public class InteractionOrderTests
    {
        static readonly string[] GodotHomeChain =
        {
            "dock_barrier", "bridge_terminal", "fire_suppression_point", "repair_point", "breach_seal_point",
            "crafting_station", "production_station", "loot_container", "tool_pickup", "junction_calibrator_pickup",
            "home_objective", "hangar", "cargo_deposit", "cart", "work_yield_drop", "work_action",
        };

        static readonly string[] GodotAwayChain =
        {
            "dock_barrier", "bridge_terminal", "fire_suppression_point", "repair_point", "breach_seal_point",
            "loot_container", "authored_portal", "hatch_bypass", "hatch_reseal", "derelict_objective", "hangar",
            "cargo_deposit", "cart", "work_yield_drop", "work_action",
        };

        static List<(string id, string scope)> DocTable()
        {
            string doc = File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "docs", "InteractionOrder.md"));
            int begin = doc.IndexOf("<!-- interaction-order:begin -->");
            int end = doc.IndexOf("<!-- interaction-order:end -->");
            Assert.That(begin >= 0 && end > begin, "InteractionOrder.md is missing its table markers");
            var rows = new List<(string, string)>();
            foreach (string raw in doc.Substring(begin, end - begin).Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("|") || line.StartsWith("|---") || line.StartsWith("| #"))
                    continue;
                string[] cells = line.Split('|').Select(c => c.Trim()).ToArray();
                rows.Add((cells[2], cells[3]));
            }
            return rows;
        }

        [Test]
        public void Registry_EqualsTheCheckedInTable()
        {
            List<(string id, string scope)> table = DocTable();
            Assert.AreEqual(InteractionRegistry.Handlers.Count, table.Count, "row count");
            for (int i = 0; i < table.Count; i++)
            {
                Assert.AreEqual(table[i].id, InteractionRegistry.Handlers[i].Id, $"row {i + 1} handler");
                Assert.AreEqual(table[i].scope, InteractionRegistry.Handlers[i].Scope.ToString(), $"row {i + 1} scope");
            }
        }

        [Test]
        public void HomeAndAwayOrders_EqualTheGodotChains()
        {
            CollectionAssert.AreEqual(GodotHomeChain, InteractionRegistry.OrderFor(SessionLocation.Home));
            CollectionAssert.AreEqual(GodotAwayChain, InteractionRegistry.OrderFor(SessionLocation.Away));
        }

        [Test]
        public void DocTableFilteredByScope_EqualsBothChains()
        {
            List<(string id, string scope)> table = DocTable();
            string[] home = table.Where(r => r.scope == "Both" || r.scope == "Home").Select(r => r.id).ToArray();
            string[] away = table.Where(r => r.scope == "Both" || r.scope == "Away").Select(r => r.id).ToArray();
            CollectionAssert.AreEqual(GodotHomeChain, home);
            CollectionAssert.AreEqual(GodotAwayChain, away);
        }
    }
}
