using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// Locks the two _process branch orders (playable_generated_ship.gd 8525-8576) and kills the "system wired into only one
    /// branch" regression class: every stage must be in both tables unless it declares a location scope with a reason.
    /// </summary>
    public class TickOrderTests
    {
        static readonly string[] GodotAwayBranch =
        {
            "oxygen", "threat", "sanity_hallucination", "active_fire", "survival_attrition", "player_vitals", "tracker_status",
            "field_craft", "autosave", "audio", "present_ships", "recharge_port_power", "food", "ammo_consumable_decay",
            "electrical_arc", "work_action", "work_action_hud", "tooltip_focus",
        };

        static readonly string[] GodotHomeBranch =
        {
            "autosave", "threat", "present_ships", "active_fire", "field_craft", "oxygen", "electrical_arc",
            "ammo_consumable_decay", "survival_attrition", "sanity_hallucination", "food", "audio", "work_action",
            "work_action_hud", "tooltip_focus",
        };

        [Test]
        public void EveryStage_IsInBothOrders_UnlessDeclaredLocationSpecificWithAReason()
        {
            var home = new HashSet<string>(TickOrder.HomeOrder);
            var away = new HashSet<string>(TickOrder.AwayOrder);
            foreach (ITickStage stage in TickOrder.Stages)
            {
                switch (stage.Scope)
                {
                    case StageScope.Both:
                        Assert.IsTrue(home.Contains(stage.Id), $"stage '{stage.Id}' (Both) is missing from HomeOrder");
                        Assert.IsTrue(away.Contains(stage.Id), $"stage '{stage.Id}' (Both) is missing from AwayOrder");
                        break;
                    case StageScope.HomeOnly:
                        Assert.IsFalse(string.IsNullOrWhiteSpace(stage.ScopeReason), $"home-only stage '{stage.Id}' needs a reason");
                        Assert.IsTrue(home.Contains(stage.Id) && !away.Contains(stage.Id), $"home-only stage '{stage.Id}' placement");
                        break;
                    case StageScope.AwayOnly:
                        Assert.IsFalse(string.IsNullOrWhiteSpace(stage.ScopeReason), $"away-only stage '{stage.Id}' needs a reason");
                        Assert.IsTrue(away.Contains(stage.Id) && !home.Contains(stage.Id), $"away-only stage '{stage.Id}' placement");
                        break;
                }
            }
            var known = new HashSet<string>(TickOrder.Stages.Select(s => s.Id));
            foreach (string id in TickOrder.HomeOrder.Concat(TickOrder.AwayOrder))
                Assert.IsTrue(known.Contains(id), $"order table references unknown stage '{id}'");
            Assert.AreEqual(TickOrder.HomeOrder.Count, home.Count, "HomeOrder has duplicates");
            Assert.AreEqual(TickOrder.AwayOrder.Count, away.Count, "AwayOrder has duplicates");
        }

        /// <summary>Godot's branch with the port-added stages inserted (docs/TickOrder.md "Port-added stages").</summary>
        static string[] WithPortStages(string[] godotBranch)
        {
            var list = godotBranch.ToList();
            // E1: wounds tick right before survival_attrition, whose vitals context carries the bleed.
            list.Insert(list.IndexOf("survival_attrition"), "wounds");
            return list.ToArray();
        }

        [Test]
        public void OrderTables_EqualTheGodotBranches()
        {
            CollectionAssert.AreEqual(WithPortStages(GodotAwayBranch), TickOrder.AwayOrder.ToArray());
            CollectionAssert.AreEqual(WithPortStages(GodotHomeBranch), TickOrder.HomeOrder.ToArray());
            // Removing the port stages gives back Godot's branches exactly.
            CollectionAssert.AreEqual(GodotAwayBranch, TickOrder.AwayOrder.Where(id => !TickOrder.PortAddedStages.Contains(id)).ToArray());
            CollectionAssert.AreEqual(GodotHomeBranch, TickOrder.HomeOrder.Where(id => !TickOrder.PortAddedStages.Contains(id)).ToArray());
            CollectionAssert.AreEqual(new[] { "wounds" }, TickOrder.PortAddedStages.ToArray());
        }

        [Test]
        public void OnlyThePlannedStagesAreLocationSpecific()
        {
            string[] specific = TickOrder.Stages.Where(s => s.Scope != StageScope.Both).Select(s => s.Id).OrderBy(s => s).ToArray();
            CollectionAssert.AreEqual(new[] { "player_vitals", "recharge_port_power", "tracker_status" }, specific);
        }

        [Test]
        public void TickDocMatchesTheTables()
        {
            string doc = System.IO.File.ReadAllText(System.IO.Path.Combine(Fixtures.RepoRoot, "docs", "TickOrder.md"));
            int awayAt = doc.IndexOf("## Away branch");
            int homeAt = doc.IndexOf("## Home branch");
            int specificAt = doc.IndexOf("## Location-specific stages");
            Assert.That(awayAt >= 0 && homeAt > awayAt && specificAt > homeAt, "TickOrder.md sections");
            CollectionAssert.AreEqual(TickOrder.AwayOrder.ToArray(), StageColumn(doc.Substring(awayAt, homeAt - awayAt)));
            CollectionAssert.AreEqual(TickOrder.HomeOrder.ToArray(), StageColumn(doc.Substring(homeAt, specificAt - homeAt)));
        }

        static string[] StageColumn(string section)
        {
            var ids = new List<string>();
            foreach (string raw in section.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("|") || line.StartsWith("|---") || line.StartsWith("| #"))
                    continue;
                string[] cells = line.Split('|');
                if (cells.Length > 2)
                    ids.Add(cells[2].Trim());
            }
            return ids.ToArray();
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        [Test]
        public void StartedSession_RunsTheHomeOrder_ThenTheAwayOrder()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, "golden session did not start: " + s.LastFailureReason);
            var ran = new List<(string, SessionLocation)>();
            s.StageRan += (id, loc) => ran.Add((id, loc));
            s.Tick(TickContext.Frame(0.1, rig.Scene.PlayerPosition));
            CollectionAssert.AreEqual(TickOrder.HomeOrder.ToArray(), ran.Select(r => r.Item1).ToArray());
            Assert.IsTrue(ran.All(r => r.Item2 == SessionLocation.Home));

            // Force the away branch (the probe only checks dispatch order; travel is covered by the headless tests).
            ran.Clear();
            s.AwayFromStart = true;
            s.Tick(TickContext.Frame(0.1, rig.Scene.PlayerPosition));
            CollectionAssert.AreEqual(TickOrder.AwayOrder.ToArray(), ran.Select(r => r.Item1).ToArray());
            Assert.IsTrue(ran.All(r => r.Item2 == SessionLocation.Away));
        }

        [Test]
        public void EarlyReturns_KeepTheClocksCorrect()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            int stages = 0;
            s.StageRan += (id, loc) => stages++;
            s.Tick(TickContext.Frame(0.5, rig.Scene.PlayerPosition));
            Assert.AreEqual(0.5, s.WorldTime, 1e-12);
            Assert.AreEqual(0.5, s.RunPlayTimeSeconds, 1e-12);
            s.SliceComplete = true;
            stages = 0;
            s.Tick(TickContext.Frame(0.5, rig.Scene.PlayerPosition));
            Assert.AreEqual(0, stages, "no stage runs once the slice is complete");
            Assert.AreEqual(1.0, s.WorldTime, 1e-12, "world_time advances before any branch/return");
            Assert.AreEqual(0.5, s.RunPlayTimeSeconds, 1e-12, "play time only counts while playable");
        }
    }
}
