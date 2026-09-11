using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Parity
{
    /// <summary>
    /// Replays fixtures/godot/procgen_validation/validator_verdicts.json (Godot 4.7.1): StructuralPlanValidator
    /// verdicts and WalkabilityContract adjacency / flood / standing path / void / capsule results for the golden
    /// coherent ships, the layouts regenerated from every fixtures/godot/procgen recipe and the walkability smoke
    /// layout, plus fail-closed verdicts for tampered plans.
    /// </summary>
    public class ProcgenValidatorParityTests
    {
        const string VerdictsPath = "godot/procgen_validation/validator_verdicts.json";

        [SetUp]
        public void SetUp()
        {
            Fixtures.Require(VerdictsPath);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
        }

        static GdDict GoldenLayout(string name) => CatalogRegistry.LoadDict("res://data/procgen/golden/" + name + "/layout.json");

        static GdDict Verdicts(GdDict layout, GdDict plan)
        {
            var output = new GdDict { { "plan_verdict", new StructuralPlanValidator().Validate(plan, layout) } };
            GdDict occupancy = plan.GetDictOrEmpty("occupancy");
            GdDict edges = plan.GetDictOrEmpty("edges");
            GdDict enclosure = WalkabilityContract.BuildAdjacency(occupancy, edges, layout, false);
            string startKey = occupancy.IsEmpty ? "" : V.Str(occupancy.Keys[0]);
            output["enclosure_adjacency"] = enclosure;
            output["enclosure_flood"] = new GdArray(WalkabilityContract.FloodVisited(enclosure, startKey).Keys);
            GdDict gameplay = new GameplaySliceBuilder().Build(layout);
            string startRoom = gameplay.GetString("start_room");
            string goalRoom = gameplay.GetString("goal_room");
            GdDict standing = WalkabilityContract.BuildAdjacency(occupancy, edges, layout, true);
            output["standing_adjacency"] = standing;
            output["start_room"] = startRoom;
            output["goal_room"] = goalRoom;
            output["rooms_reachable"] = WalkabilityContract.RoomsReachable(standing, occupancy, startRoom, goalRoom);
            List<string> pathKeys = WalkabilityContract.StandingPathKeys(standing, occupancy, startRoom, goalRoom);
            output["standing_path_keys"] = GdString.ToGdArray(pathKeys);
            output["standing_void_reason"] = WalkabilityContract.StandingVoidReason(plan, occupancy, pathKeys, layout);
            var capsules = new GdDict();
            foreach (var kv in edges)
            {
                var edge = (GdDict)kv.Value;
                capsules[V.Str(kv.Key)] = GdArray.Of(
                    WalkabilityContract.EdgeKind(edge),
                    WalkabilityContract.CapsuleHitsSolidSlab(edge, occupancy),
                    WalkabilityContract.CapsulePassesDoorOpening(edge, occupancy),
                    WalkabilityContract.CapsuleHitsZeroThicknessFixture(edge, occupancy),
                    WalkabilityContract.CapsuleHitsCellCenterAabbFixture(edge, occupancy));
            }
            output["capsules"] = capsules;
            return output;
        }

        static void AssertSame(object expected, object actual, string where)
        {
            var diffs = TreeDiff.Compare(expected, ProcgenPipelineParityTests.Canon(V.Normalize(actual)));
            Assert.IsEmpty(diffs, where + ": " + TreeDiff.Format(diffs));
        }

        [Test]
        public void ValidatorAndWalkabilityVerdictsMatchGodot()
        {
            GdArray cases = Fixtures.ReadDict(VerdictsPath).GetArray("cases");
            Assert.That(cases.Count, Is.GreaterThan(0));
            foreach (GdDict expected in cases)
            {
                string name = expected.GetString("name");
                string kind = expected.GetString("kind");
                GdDict actual;
                if (kind == "golden")
                {
                    GdDict layout = GoldenLayout(name);
                    GdDict embedded = layout.GetDictOrEmpty("structural_plan");
                    actual = Verdicts(layout, new StructuralEdgeCompiler().Compile(layout));
                    if (!embedded.IsEmpty) actual["embedded_plan_verdict"] = new StructuralPlanValidator().Validate(embedded, layout);
                }
                else if (kind == "recipe")
                {
                    GdDict recipe = Fixtures.ReadDict($"godot/procgen/recipe_{name}.json");
                    GdDict layout = ProcgenPipelineParityTests.Regenerate(recipe).Layout;
                    actual = Verdicts(layout, layout.GetDictOrEmpty("structural_plan"));
                    actual["recompiled_plan_verdict"] = new StructuralPlanValidator().Validate(new StructuralEdgeCompiler().Compile(layout), layout);
                }
                else
                {
                    var bp = new ShipBlueprint(ShipBlueprint.Size.Medium, ShipBlueprint.Condition.Pristine, 42);
                    GdDict layout = new ShipLayoutGenerator().Generate(bp, new GdDict { { "template", "spine" } });
                    actual = Verdicts(layout, new StructuralEdgeCompiler().Compile(layout));
                }
                actual["name"] = name;
                actual["kind"] = kind;
                AssertSame(expected, actual, name);
            }
        }

        [Test]
        public void TamperedPlanVerdictsMatchGodot()
        {
            GdArray tampered = Fixtures.ReadDict(VerdictsPath).GetArray("tampered");
            Assert.That(tampered.Count, Is.GreaterThan(0));
            GdDict baseLayout = GoldenLayout("coherent_ship_001");
            GdDict basePlan = new StructuralEdgeCompiler().Compile(baseLayout);
            foreach (GdDict expected in tampered)
            {
                string mode = expected.GetString("mode");
                GdDict plan = basePlan.DeepCopy();
                GdDict topo = baseLayout.DeepCopy();
                switch (mode)
                {
                    case "empty_plan":
                        plan = new GdDict();
                        break;
                    case "drop_floor":
                        plan.GetArray("floor_placements").RemoveAt(0);
                        break;
                    case "open_placement":
                        {
                            var p = (GdDict)plan.GetArray("placements")[0];
                            p["kind"] = "OPEN";
                            p["module_id"] = "floor_1x1";
                            break;
                        }
                    case "bad_occupancy":
                        {
                            GdDict occupancy = plan.GetDict("occupancy");
                            var record = (GdDict)occupancy[occupancy.Keys[0]];
                            record["deck"] = "x";
                            record["room_id"] = "";
                            break;
                        }
                    case "no_bindings":
                        plan["socket_bindings"] = new GdArray();
                        foreach (string key in new[] { "placements", "floor_placements", "ceiling_placements" })
                            foreach (GdDict rec in plan.GetArray(key)) rec.Erase("socket_bindings");
                        break;
                    case "bad_portal":
                        {
                            var portal = (GdDict)topo.GetArray("portals")[0];
                            portal["to_cell"] = GdArray.Of(99L, 99L);
                            portal["from_direction"] = "north";
                            portal["to_direction"] = "east";
                            break;
                        }
                    case "drop_ceiling":
                        plan.GetArray("ceiling_placements").RemoveAt(1);
                        ((GdDict)plan.GetArray("ceiling_placements")[0])["position"] = GdArray.Of(1.0, 2.0, 3.0);
                        break;
                    case "bad_yaw":
                        ((GdDict)plan.GetArray("placements")[0])["yaw_degrees"] = 45.0;
                        ((GdDict)plan.GetArray("floor_placements")[0])["yaw_degrees"] = 1.0;
                        break;
                    default:
                        Assert.Fail("unknown tamper mode " + mode);
                        break;
                }
                AssertSame(expected["verdict"], new StructuralPlanValidator().Validate(plan, topo), mode);
            }
        }
    }
}
