using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.EditMode.Procgen
{
    public sealed class FirstAwaySalvageIntegrationTests
    {
        CollectingLog _log;
        [SetUp] public void Setup() { CoreServices.Resources=new FileSystemResourceReader(Fixtures.StreamingDataRoot);CoreServices.Log=_log=new CollectingLog();CatalogRegistry.Clear(); }
        [TearDown] public void Cleanup() { CoreServices.Log=NullLog.Instance;CatalogRegistry.Clear(); }
        static FirstAwayGenerationInputs Inputs(long seed,long size,long condition,long world=17,string marker="0:0:0") =>
            new FirstAwayGenerationInputs(seed,world,size,condition,marker,"ship_0:0:0","breach_field","standard");
        [TestCase(42)] [TestCase(777)]
        public void AllNineCellsPassUnchangedCompleteContractWithRealOwnedContent(long seed)
        {
            var contract=new FirstRunContract();Assert.IsTrue(contract.LoadContract());
            string before=GdJson.Stringify(contract.Contract);
            for(long size=0;size<3;size++) for(long condition=0;condition<3;condition++)
            {
                var docs=new ShipGenerator().GenerateFirstAway(Inputs(seed,size,condition));
                Assert.IsNotNull(docs,size+"/"+condition+" errors: "+string.Join(";",_log.Errors));
                Assert.AreEqual(docs.Kit.GetString("kit_id"),docs.Layout.GetString("kit_id"),"archive declares actual resolved catalog");
                Assert.AreEqual(docs.Kit.GetString("kit_id"),docs.SourceLayout.GetString("kit_id"));
                Assert.AreEqual("res://data/kits/"+docs.Layout.GetString("kit_id")+".json",docs.KitPath);
                Assert.IsTrue(FirstRunAwayGate.SatisfiesCompleteContract(contract,docs.Layout,docs.GameplaySlice,condition));
                Assert.IsTrue(FirstRunAwayGate.HasStandingStartToGoal(docs.Layout));
                AssertAllRoomStandingReturn(docs.Layout);
                var rooms=docs.Layout.GetArrayOrEmpty("rooms").Cast<GdDict>().ToArray();
                Assert.AreEqual(1,rooms.Count(r=>r.GetString("room_role")=="cargo"));
                var cargo=rooms.Single(r=>r.GetString("room_role")=="cargo");Assert.AreEqual("breached",cargo.GetString("variant"));
                Assert.AreEqual("runtime",docs.Layout.GetString("hazard_source"));
                Assert.IsTrue(docs.GameplaySlice.GetArrayOrEmpty("breach_zones").IsEmpty);
                Assert.IsTrue(EncounterInjector.Validate(docs.Layout).GetBool("valid"));
                Assert.IsTrue(docs.Layout.GetArrayOrEmpty("encounters").Cast<GdDict>().Any(e=>e.GetBool("spike")));
                foreach(GdDict e in docs.Layout.GetArrayOrEmpty("encounters"))
                { Assert.IsFalse(docs.Layout.GetArrayOrEmpty("critical_path").Contains(e.GetString("room_id"))); Assert.AreEqual("biomatter_lurker",e.GetString("encounter_kind"));Assert.GreaterOrEqual(e.GetInt("count"),1); }
                foreach(GdDict row in docs.GameplaySlice.GetArrayOrEmpty("loot_containers"))
                { var room=rooms.Single(r=>r.GetString("id")==row.GetString("room_id"));Assert.IsTrue(CellOwned(room,row.Get("approach_cell")));
                    string kind=row.GetString("slot_kind");var slots=room.GetDictOrEmpty("interior_zones").GetArrayOrEmpty(kind=="reserved"?"reserved_cells":kind+"_slots");
                    var slot=LayoutSerializer.ParseSlotCell(slots[(int)row.GetInt("slot_index")]);var approach=LayoutSerializer.ParseSlotCell(row.Get("approach_cell"));
                    Assert.AreEqual(V.I64(slot[0]),V.I64(approach[0]));Assert.AreEqual(V.I64(slot[1]),V.I64(approach[1])); }
                var cache=docs.GameplaySlice.GetArrayOrEmpty("loot_containers").Cast<GdDict>().Single(c=>c.GetString("id").EndsWith("/common_cache"));
                Assert.AreEqual("scrap_metal",((GdDict)cache.GetArrayOrEmpty("contents")[0]).GetString("item_id"));
                long units=cache.GetArrayOrEmpty("contents").Cast<GdDict>().Sum(r=>r.GetInt("qty"));Assert.That(units,Is.InRange(1L,3L));
                if(condition==0) Assert.IsTrue(docs.Layout.GetArrayOrEmpty("module_damage").IsEmpty);
                else foreach(GdDict d in docs.Layout.GetArrayOrEmpty("module_damage")) Assert.AreNotEqual("destroyed",d.GetString("state"));
                Assert.IsNotNull(docs.FirstAwayDescriptor);Assert.AreEqual(before,GdJson.Stringify(contract.Contract));
            }
        }
        [TestCase(42)] [TestCase(777)]
        public void DockPortUsesExactAuthoredEndpointInEverySizeAndCondition(long seed)
        {
            for(long size=0;size<3;size++) for(long condition=0;condition<3;condition++)
            {
                var docs=new ShipGenerator().GenerateFirstAway(Inputs(seed,size,condition));
                Assert.IsNotNull(docs);
                string before=GdJson.Stringify(docs.Layout,"  ");
                var authored=docs.Layout.GetDictOrEmpty("docking_port");
                var port=DockPorts.ForDerelict(docs.Layout,seed,condition);
                Assert.IsFalse(port.IsEmpty,size+"/"+condition);
                Assert.AreEqual(Vec3.FromArray(authored["position"],Vec3.Inf),port["position"],"exact authored cell, not dock centroid");
                Assert.AreEqual(Vec3.FromArray(authored["facing"],Vec3.Zero),port["facing"],"exact authored cardinal normal");
                Assert.AreEqual(DockPorts.ConditionFromSeed(seed,condition),port.GetString("condition"));
                Assert.AreEqual(before,GdJson.Stringify(docs.Layout,"  "),"port derivation is pure");
            }
        }
        [Test]
        public void ProfileDockPortRefusesMissingAndMalformedContractWithoutCentroidFallback()
        {
            var docs=new ShipGenerator().GenerateFirstAway(Inputs(42,0,2));Assert.IsNotNull(docs);
            var original=docs.Layout;
            var absent=original.DeepCopy();absent.Erase("docking_port");
            Assert.IsTrue(DockPorts.ForDerelict(absent).IsEmpty);
            foreach(var bad in new[] {
                new GdDict {{"contract_version",2L},{"position",GdArray.Of(0.0,0.0,0.0)},{"facing",GdArray.Of(0.0,0.0,1.0)}},
                new GdDict {{"contract_version",1L},{"position",GdArray.Of(0.0,0.0)},{"facing",GdArray.Of(0.0,0.0,1.0)}},
                new GdDict {{"contract_version",1L},{"position",GdArray.Of(double.NaN,0.0,0.0)},{"facing",GdArray.Of(0.0,0.0,1.0)}},
                new GdDict {{"contract_version",1L},{"position",GdArray.Of(0.0,0.0,0.0)},{"facing",GdArray.Of(1.0,0.0,1.0)}},
                new GdDict {{"contract_version",1L},{"position",GdArray.Of(0.0,0.0,0.0)},{"facing",GdArray.Of(0.0,1.0,0.0)}} })
            {
                var layout=original.DeepCopy();layout["docking_port"]=bad;
                Assert.IsTrue(DockPorts.ForDerelict(layout).IsEmpty,"profile must not fall back to an otherwise valid dock room");
            }
            var legacy=original.DeepCopy();legacy.Erase("generation_profile");legacy.Erase("docking_port");
            var legacyPort=DockPorts.ForDerelict(legacy);
            Assert.IsFalse(legacyPort.IsEmpty,"ordinary legacy centroid path remains available");
            Assert.AreEqual(new Vec3(1.0,0.0,0.0),legacyPort["facing"]);
            Assert.AreEqual(StartSceneBuilder.FindDockPosition(legacy),legacyPort["position"]);
        }
        static void AssertAllRoomStandingReturn(GdDict layout)
        {
            var graph=new ShipNavGraph();Assert.Greater(graph.BuildFromLayout(layout),0);
            string dock=layout.GetDictOrEmpty("prototype").GetString("start_room");
            string start=graph.Nodes.Keys.Cast<object>().Select(V.Str).First(id=>graph.GetNodeRoom(id)==dock);
            foreach(GdDict room in layout.GetArrayOrEmpty("rooms"))
            {
                string target=graph.Nodes.Keys.Cast<object>().Select(V.Str).First(id=>graph.GetNodeRoom(id)==room.GetString("id"));
                Assert.Greater(ThreatPathfinder.FindPath(graph,graph.GetNodePos(start),graph.GetNodePos(target)).Count,0,"standing outward "+room.GetString("id"));
                Assert.Greater(ThreatPathfinder.FindPath(graph,graph.GetNodePos(target),graph.GetNodePos(start)).Count,0,"standing return "+room.GetString("id"));
            }
        }
        static bool CellOwned(GdDict room,object value)
        { var cell=LayoutSerializer.ParseSlotCell(value);return room.GetArrayOrEmpty("cells").Cast<object>().Select(LayoutSerializer.ParseSlotCell).Any(c=>V.I64(c[0])==V.I64(cell[0])&&V.I64(c[1])==V.I64(cell[1])); }
        [Test]
        public void OrderedGateAcceptsFortyTwoInEveryCellWithoutMutatingOrdinaryContext()
        {
            var contract=new FirstRunContract();Assert.IsTrue(contract.LoadContract());var generator=new ShipGenerator();
            for(long size=0;size<3;size++) for(long condition=0;condition<3;condition++)
            { var result=FirstRunAwayGate.EvaluateCandidates(contract,size,condition,(seed,s,c)=>generator.GenerateFirstAway(Inputs(seed,s,c)));Assert.IsTrue(result.Success);Assert.AreEqual(42,result.Seed);CollectionAssert.AreEqual(new[]{42L},result.EvaluatedSeeds); }
            Assert.AreEqual("",generator.BiomeId);Assert.AreEqual("",generator.DifficultyId);Assert.IsFalse(generator.RichExpeditions);
        }
        [Test]
        public void DescriptorRestoresExactRawDocumentsAndRefusesWrongIdentityBytesAndSnapshot()
        {
            var input=Inputs(42,0,2);var generator=new ShipGenerator();var docs=generator.GenerateFirstAway(input);Assert.IsNotNull(docs);
            var snapshot=PaidSnapshotCodec.Parse(PaidSnapshotCodec.Stringify(docs.FirstAwayDescriptor.Snapshot()));
            Assert.IsTrue(generator.TryRestoreFirstAway(input,snapshot,docs.LayoutJson,docs.GameplaySliceJson,out var restored));
            Assert.AreEqual(docs.LayoutJson,restored.LayoutJson);Assert.AreEqual(docs.GameplaySliceJson,restored.GameplaySliceJson);
            Assert.AreEqual(docs.KitPath,restored.KitPath,"retain the actual ordinary selector fallback");
            Assert.IsTrue(V.VariantEquals(docs.Kit,restored.Kit),"restored kit must equal original wrapper-bearing kit");
            Assert.Greater(restored.Kit.GetArrayOrEmpty("modules").Count,0);
            Assert.AreEqual(restored.Kit.GetString("kit_id"),restored.Layout.GetString("kit_id"));
            Assert.IsFalse(generator.TryRestoreFirstAway(Inputs(42,0,2,29),snapshot,docs.LayoutJson,docs.GameplaySliceJson,out _));
            Assert.IsFalse(generator.TryRestoreFirstAway(input,snapshot,docs.LayoutJson+" ",docs.GameplaySliceJson,out _));
            snapshot["provider"]="untrusted";Assert.IsFalse(generator.TryRestoreFirstAway(input,snapshot,docs.LayoutJson,docs.GameplaySliceJson,out _));
            var repeat=generator.GenerateFirstAway(input);Assert.AreEqual(docs.LayoutJson,repeat.LayoutJson);Assert.AreEqual(docs.GameplaySliceJson,repeat.GameplaySliceJson);
        }
        [Test]
        public void EntireFiniteGeometryHasAuthenticContractContentAndUnchangedStockDamageOutcomes()
        {
            int count=0;var generator=new ShipLayoutGenerator();var contract=new FirstRunContract();Assert.IsTrue(contract.LoadContract());
            for(int size=0;size<3;size++) foreach(string family in FirstAwaySalvageGeometry.Families(size))
                foreach(var fp in size==0?new[]{new Vec2i(3,3)}:new[]{new Vec2i(3,3),new Vec2i(3,4),new Vec2i(4,3)})
                    for(int orientation=0;orientation<8;orientation++) for(int offset=0;offset<2;offset++)
                        foreach(long seed in new[]{42L,777L}) for(long condition=0;condition<3;condition++)
                        {
                            var inputs=Inputs(seed,size,condition);var choices=new FirstAwaySalvageGeometry.Choices(family,fp.X,fp.Y,orientation,offset);
                            var layout=generator.GenerateFirstAway(inputs,choices);Assert.IsFalse(layout.IsEmpty,family+"/"+orientation+"/"+condition+": "+string.Join(";",_log.Errors));
                            var docs=FirstAwaySalvageProfile.AsDocuments(inputs,layout,new ShipGenerator().KitPathForLayout(layout),FirstAwaySalvageProfile.LoadPinnedCatalogs());
                            Assert.IsNotNull(docs,family+"/"+orientation+"/"+condition+": "+string.Join(";",_log.Errors));
                            Assert.IsTrue(FirstRunAwayGate.SatisfiesCompleteContract(contract,docs.Layout,docs.GameplaySlice,condition));
                            AssertAllRoomStandingReturn(docs.Layout);
                            var ordinary=layout.DeepCopy();ordinary.Erase("wreck_applied");ordinary["module_damage"]=new GdArray();
                            if(condition>0)
                            {
                                LayoutMutator.ApplyWreckToCompiledPlan(ordinary,seed,null,condition==2?.55:.35);
                                Assert.IsTrue(V.VariantEquals(layout.GetArrayOrEmpty("module_damage"),ordinary.GetArrayOrEmpty("module_damage")),"exact stock damage bits/order/full pool");
                                var ids=layout.GetArrayOrEmpty("module_damage").Cast<GdDict>().Select(d=>d.GetString("module_key")).ToArray();Assert.AreEqual(ids.Length,ids.Distinct().Count());
                            }
                            count++;
                        }
            Assert.AreEqual(2112,count);
        }
        [Test]
        public void RehashedFabricatedRewardsAndGeometryAreRefusedDespiteContractPassing()
        {
            var input=Inputs(42,0,2);var generator=new ShipGenerator();var docs=generator.GenerateFirstAway(input);Assert.IsNotNull(docs);
            var gameplay=GdJson.ParseDict(docs.GameplaySliceJson);
            var cache=gameplay.GetArrayOrEmpty("loot_containers").Cast<GdDict>().Single(c=>c.GetString("id").EndsWith("/common_cache"));
            ((GdDict)cache.GetArrayOrEmpty("contents")[0])["qty"]=999L;
            string fabricated=GdJson.Stringify(gameplay,"  ");
            var forged=new FirstAwayGenerationDescriptor(input,FirstAwaySalvageProfile.Provider,FirstAwaySalvageProfile.LoadPinnedCatalogs(),
                FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(docs.LayoutJson),FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(fabricated));
            var contract=new FirstRunContract();Assert.IsTrue(contract.LoadContract());Assert.IsTrue(contract.Validate(docs.Layout,gameplay));
            Assert.IsFalse(generator.TryRestoreFirstAway(input,forged.Snapshot(),docs.LayoutJson,fabricated,out _),"hash possession is not authored content authority");
            var layout=GdJson.ParseDict(docs.LayoutJson);layout["kit_id"]="ship_structural_hazard";
            string substitutedKit=GdJson.Stringify(layout,"  ");
            forged=new FirstAwayGenerationDescriptor(input,FirstAwaySalvageProfile.Provider,FirstAwaySalvageProfile.LoadPinnedCatalogs(),
                FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(substitutedKit),FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(docs.GameplaySliceJson));
            Assert.IsFalse(generator.TryRestoreFirstAway(input,forged.Snapshot(),substitutedKit,docs.GameplaySliceJson,out _),"declared selector identity cannot replace actual catalog");
            layout=GdJson.ParseDict(docs.LayoutJson);layout.GetDictOrEmpty("first_away_reservations")["work_approach"]=GdArray.Of(999L,999L,0L);
            string invalid=GdJson.Stringify(layout,"  ");
            forged=new FirstAwayGenerationDescriptor(input,FirstAwaySalvageProfile.Provider,FirstAwaySalvageProfile.LoadPinnedCatalogs(),
                FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(invalid),FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(docs.GameplaySliceJson));
            Assert.IsFalse(generator.TryRestoreFirstAway(input,forged.Snapshot(),invalid,docs.GameplaySliceJson,out _));
        }
        [Test]
        public void MissingChangedAndTamperedParsedCatalogAuthoritiesFailClosed()
        {
            var input=Inputs(42,0,2);var generator=new ShipGenerator();var docs=generator.GenerateFirstAway(input);Assert.IsNotNull(docs);
            var original=CoreServices.Resources;
            CoreServices.Resources=new ChangedResource(original,"res://data/procgen/encounter_tables/biomatter_lurker.json",null);
            Assert.IsNull(generator.GenerateFirstAway(input));
            Assert.IsFalse(generator.TryRestoreFirstAway(input,docs.FirstAwayDescriptor.Snapshot(),docs.LayoutJson,docs.GameplaySliceJson,out _));
            CoreServices.Resources=new ChangedResource(original,"res://data/items/item_definitions.json",original.ReadText("res://data/items/item_definitions.json")+" ");
            Assert.IsNull(generator.GenerateFirstAway(input));
            CoreServices.Resources=new ChangedResource(original,docs.KitPath,original.ReadText("res://data/kits/ship_structural_hazard.json"));
            Assert.IsNull(generator.GenerateFirstAway(input),"selector kit cannot replace the actual wrapper-bearing pinned kit");
            Assert.IsFalse(generator.TryRestoreFirstAway(input,docs.FirstAwayDescriptor.Snapshot(),docs.LayoutJson,docs.GameplaySliceJson,out _));
            CoreServices.Resources=original;
            var catalog=CatalogRegistry.LoadDict("res://data/procgen/encounter_tables/biomatter_lurker.json",false);catalog["forged"]="authority";
            Assert.IsNull(generator.GenerateFirstAway(input));
        }
        sealed class ChangedResource : IResourceReader
        {
            readonly IResourceReader _reader;readonly string _path,_text;
            public ChangedResource(IResourceReader reader,string path,string text) {_reader=reader;_path=path;_text=text;}
            public bool Exists(string path)=>path==_path?_text!=null:_reader.Exists(path);
            public string ReadText(string path)=>path==_path?_text:_reader.ReadText(path);
        }
        [Test]
        public void DuplicateDamageKeysAndPreviouslyDamagedPoolsAreRefused()
        {
            var layout=new ShipLayoutGenerator().GenerateFirstAway(Inputs(42,0,0));Assert.IsTrue(FirstAwaySalvageProfile.UniqueFreshDamagePool(layout));
            var floors=layout.GetDictOrEmpty("structural_plan").GetArrayOrEmpty("floor_placements");floors.Append(((GdDict)floors[0]).DeepCopy());
            Assert.IsFalse(FirstAwaySalvageProfile.UniqueFreshDamagePool(layout));
            layout=new ShipLayoutGenerator().GenerateFirstAway(Inputs(42,0,2));Assert.IsFalse(FirstAwaySalvageProfile.UniqueFreshDamagePool(layout));
        }
    }
}
