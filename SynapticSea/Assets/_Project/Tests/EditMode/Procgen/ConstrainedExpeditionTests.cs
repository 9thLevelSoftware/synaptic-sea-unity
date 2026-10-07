using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    public class ConstrainedExpeditionTests
    {
        [SetUp] public void Setup(){CoreServices.Resources=new FileSystemResourceReader(Fixtures.StreamingDataRoot);CoreServices.Log=NullLog.Instance;CatalogRegistry.Clear();}
        [Test]
        public void SavedV3ProfileKeepsOriginalLootAndExactRegeneration()
        {
            var generator=new ShipGenerator {RichExpeditions=true};generator.ConfigureRunContext("dead_fleet","standard");
            var bp=new ShipBlueprint(2,0,17){GenerationProfile=ConstrainedExpedition.LegacyProfile};
            var before=generator.Generate(bp);var restored=ShipBlueprint.FromDict(GdJson.ParseDict(GdJson.Stringify(bp.ToDict())));
            var after=generator.Generate(restored);Assert.AreEqual(ConstrainedExpedition.LegacyProfile,after.Layout.GetString("generation_profile"));
            Assert.AreEqual(before.LayoutJson,after.LayoutJson);Assert.AreEqual(before.GameplaySliceJson,after.GameplaySliceJson);
            var port=SynapticSea.Core.Systems.DockPorts.ForDerelict(after.Layout);
            Assert.IsFalse(port.IsEmpty);var expectedPort=after.Layout.GetDictOrEmpty("docking_port");
            Assert.AreEqual(Vec3.FromArray(expectedPort["position"]),port["position"],"saved v3 uses the published boundary port, not the old room center fallback");
            Assert.IsFalse(after.GameplaySlice.GetArrayOrEmpty("loot_containers").Cast<GdDict>().Any(c=>c.Has("contents")),"existing v3 worlds retain original rolled loot rather than receiving new supplies");
        }

        [TestCase(1)] [TestCase(2)]
        public void SeedMatrixComposesPhysicalRoutesVariedDimensionsBranchesAndRoles(int size)
        {
            var graphs=new HashSet<string>();var dimensions=new HashSet<string>();var generator=new ShipGenerator{RichExpeditions=true};generator.ConfigureRunContext("dead_fleet","standard");
            for(int seed=17;seed<41;seed++)
            {
                var docs=generator.GenerateFromSeed(seed,size,0);Assert.NotNull(docs,"seed "+seed);
                var layout=docs.Layout;Assert.AreEqual(ConstrainedExpedition.Profile,layout.GetString("generation_profile"));
                Assert.AreEqual("composed",layout.GetDictOrEmpty("composition_diagnostics").GetString("status"),"ordinary matrix must not silently use fallback seed="+seed);
                Assert.IsTrue(layout.GetBool("structural_plan_validated"));
                var rooms=layout.GetArrayOrEmpty("rooms").Cast<GdDict>().ToDictionary(r=>r.GetString("id"));
                var graph=rooms.Keys.ToDictionary(id=>id,id=>new HashSet<string>());var floors=new HashSet<string>();
                foreach(var r in rooms.Values)foreach(var raw in r.GetArrayOrEmpty("cells")){var c=LayoutSerializer.ParseSlotCell(raw);Assert.IsTrue(floors.Add(V.I64(c[0])+":"+V.I64(c[1])));}
                foreach(GdDict p in layout.GetArrayOrEmpty("portals"))
                {
                    string a=p.GetString("from_room"),b=p.GetString("to_room");graph[a].Add(b);graph[b].Add(a);
                    var ca=LayoutSerializer.ParseSlotCell(p["from_cell"]);var cb=LayoutSerializer.ParseSlotCell(p["to_cell"]);
                    Assert.AreEqual(1,System.Math.Abs(V.I64(ca[0])-V.I64(cb[0]))+System.Math.Abs(V.I64(ca[1])-V.I64(cb[1])));
                }
                Assert.AreEqual("dock_01",layout.GetDictOrEmpty("prototype").GetString("start_room"));
                Assert.AreNotEqual("dock_01",layout.GetDictOrEmpty("prototype").GetString("goal_room"));
                Assert.AreEqual("dock_01",docs.GameplaySlice.GetString("start_room"));
                Assert.AreEqual(layout.GetDictOrEmpty("prototype").GetString("goal_room"),docs.GameplaySlice.GetString("goal_room"));
                foreach(var room in rooms.Values)
                {
                    Assert.AreEqual("initial_hull_condition_v1",room.GetString("oxygen_source"));
                    bool vacuum=room.GetString("variant")=="breached"||room.GetString("variant")=="collapsed";
                    Assert.AreEqual(vacuum?0:10000,room.GetInt("oxygen_bp"),"initial air follows actual room condition");
                }
                var medicalId=rooms.Values.Single(r=>r.GetString("room_role")=="medical").GetString("id");
                var medical=docs.GameplaySlice.GetArrayOrEmpty("loot_containers").Cast<GdDict>().Single(c=>c.GetString("room_id")==medicalId);
                Assert.AreEqual("loot_"+medicalId,medical.GetString("id"),"care preserves the existing finite container identity");
                var contents=medical.GetArrayOrEmpty("contents");Assert.AreEqual(2,contents.Count);
                Assert.AreEqual("field_medkit",((GdDict)contents[0]).GetString("item_id"));Assert.AreEqual(2,((GdDict)contents[0]).GetInt("qty"));
                Assert.AreEqual("bandage_kit",((GdDict)contents[1]).GetString("item_id"));Assert.AreEqual(2,((GdDict)contents[1]).GetInt("qty"));
                Assert.AreEqual(2,((GdDict)contents[0]).Count);Assert.AreEqual(2,((GdDict)contents[1]).Count,"no extra grants hidden in the medical payload");
                var crewId=rooms.Values.Single(r=>r.GetString("id")=="crew_quarters_01").GetString("id");
                var provisions=docs.GameplaySlice.GetArrayOrEmpty("loot_containers").Cast<GdDict>().Single(c=>c.GetString("room_id")==crewId).GetArrayOrEmpty("contents").Cast<GdDict>().ToList();
                Assert.AreEqual(2,provisions.Count);Assert.AreEqual("ration_pack",provisions[0].GetString("item_id"));Assert.AreEqual(8,provisions[0].GetInt("qty"));
                Assert.AreEqual("purified_water",provisions[1].GetString("item_id"));Assert.AreEqual(8,provisions[1].GetInt("qty"));
                Assert.IsTrue(provisions.All(p=>p.Count==2),"finite crew stores carry exact existing consumable payloads");
                var maintenanceId=rooms.Values.Single(r=>r.GetString("id")=="maintenance_01").GetString("id");
                var maintenance=docs.GameplaySlice.GetArrayOrEmpty("loot_containers").Cast<GdDict>().Single(c=>c.GetString("room_id")==maintenanceId);
                Assert.AreEqual("loot_"+maintenanceId,maintenance.GetString("id"));
                var supplies=maintenance.GetArrayOrEmpty("contents").Cast<GdDict>().ToList();Assert.AreEqual(4,supplies.Count);
                CollectionAssert.AreEqual(new[]{"reactor_core","power_cell","oxygen_filter","sealant"},supplies.Select(c=>c.GetString("item_id")));
                CollectionAssert.AreEqual(new long[]{1,2,2,1},supplies.Select(c=>c.GetInt("qty")));
                Assert.IsTrue(supplies.All(c=>c.Count==2),"finite repair parts, no hidden tools or skill grants");
                Assert.AreEqual(1,graph[layout.GetDictOrEmpty("composition_diagnostics").GetString("branch_room")].Count,"published protected branch survives dock insertion");
                Assert.IsTrue(ShipLayoutGenerator.LayoutIsConnected(layout));Assert.GreaterOrEqual(layout.GetArrayOrEmpty("portals").Count-rooms.Count+1,2);
                Assert.IsTrue(graph.Any(g=>g.Key!="dock_01"&&g.Value.Count==1),"deliberate side branch");
                foreach(var roles in new[]{new[]{"crew_quarters","medical"},new[]{"engineering","maintenance"}})
                    Assert.IsTrue(rooms.Values.Where(r=>r.GetString("room_role")==roles[0]).Any(r=>graph[r.GetString("id")].Any(id=>rooms[id].GetString("room_role")==roles[1])));
                Assert.IsFalse(SynapticSea.Core.Systems.DockPorts.ForDerelict(layout).IsEmpty);
                graphs.Add(string.Join(";",graph.OrderBy(g=>g.Key).Select(g=>g.Key+"="+string.Join(",",g.Value.OrderBy(v=>v)))));
                dimensions.Add(string.Join(",",rooms.Values.Select(r=>r.GetArrayOrEmpty("cells").Count).OrderBy(n=>n)));
                var bp=ShipBlueprint.FromDict(GdJson.ParseDict(GdJson.Stringify(new ShipBlueprint(size,0,seed){GenerationProfile=ConstrainedExpedition.Profile}.ToDict())));
                Assert.AreEqual(docs.LayoutJson,generator.Generate(bp).LayoutJson,"exact save regeneration");
                foreach(GdDict item in layout.GetArrayOrEmpty("purposeful_interiors"))foreach(GdDict p in layout.GetArrayOrEmpty("portals"))foreach(string side in new[]{"from_cell","to_cell"})
                    Assert.AreNotEqual(GdJson.Stringify(LayoutSerializer.ParseSlotCell(item["cell"])),GdJson.Stringify(LayoutSerializer.ParseSlotCell(p[side])),"door bay clear");
            }
            Assert.Greater(graphs.Count,12,"normalized role-labelled graph variety, excluding orientation");Assert.Greater(dimensions.Count,12,"real room-area variety");
        }
        [TestCase(1)] [TestCase(2)]
        public void DamagedAndWreckedOverlaysPreserveTheDockToGoalCriticalRoute(int condition)
        {
            var generator=new ShipGenerator{RichExpeditions=true};generator.ConfigureRunContext("breach_field","standard");
            foreach(int seed in new[]{17,42,777})
            {
                var docs=generator.GenerateFromSeed(seed,1,condition);Assert.NotNull(docs);var layout=docs.Layout;
                Assert.IsTrue(layout.GetBool("structural_plan_validated"));Assert.AreEqual("dock_01",docs.GameplaySlice.GetString("start_room"));
                var path=layout.GetArrayOrEmpty("critical_path");Assert.Greater(path.Count,1);
                Assert.AreEqual("dock_01",V.Str(path[0]));Assert.AreEqual(docs.GameplaySlice.GetString("goal_room"),V.Str(path[path.Count-1]));
                for(int i=1;i<path.Count;i++)
                {
                    string a=V.Str(path[i-1]),b=V.Str(path[i]);
                    var portal=layout.GetArrayOrEmpty("portals").Cast<GdDict>().Single(p=>p.GetString("from_room")==a&&p.GetString("to_room")==b||p.GetString("from_room")==b&&p.GetString("to_room")==a);
                    Assert.AreNotEqual("LOCKED",portal.GetString("state"),"required return/goal route remains usable");
                }
            }
        }
        [Test] public void ExhaustedBudgetUsesExactDeterministicRecipeAndMalformedRoutesFailValidation()
        {
            var bp=new ShipBlueprint(1L,0L,17L){GenerationProfile=ConstrainedExpedition.Profile};
            var fallback=ConstrainedExpedition.LayoutWithAttemptBudget(bp,0,out var plan);
            Assert.AreEqual("fallback",fallback.GetDictOrEmpty("composition_diagnostics").GetString("status"));
            Assert.AreEqual(PurposefulExpedition.Profile,fallback.GetDictOrEmpty("composition_diagnostics").GetString("fallback_recipe"));
            Assert.AreEqual(GdJson.Stringify(PurposefulExpedition.Layout(bp,out var original).GetDictOrEmpty("rooms")),GdJson.Stringify(fallback.GetDictOrEmpty("rooms")));
            var composed=ConstrainedExpedition.Layout(bp,out plan);Assert.AreEqual("",ConstrainedExpedition.ValidateComposition(composed));
            ((GdDict)composed.GetArrayOrEmpty("adjacencies")[0])["to_cell"]=new Vec2i(999,999);
            StringAssert.Contains("physical portal",ConstrainedExpedition.ValidateComposition(composed));
        }
        [Test] public void UnsupportedCompositionSizeFailsClosed()
        {var grid=ConstrainedExpedition.Layout(new ShipBlueprint(0L,0L,17L),out var plan);Assert.IsTrue(grid.IsEmpty);Assert.IsEmpty(plan);}
    }
}
