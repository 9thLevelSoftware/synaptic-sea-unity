using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    public class PurposefulExpeditionTests
    {
        static string CellKey(object value)
        {
            var cell=LayoutSerializer.ParseSlotCell(value);
            Assert.GreaterOrEqual(cell.Count,2);
            return V.I64(cell[0])+":"+V.I64(cell[1]);
        }
        [SetUp] public void Setup() { CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot); CoreServices.Log = NullLog.Instance; CatalogRegistry.Clear(); }
        [Test]
        public void CargoExchangeVariesItsFootprintAndKeepsEveryPortalAndWorkCellClear()
        {
            var areas=new HashSet<int>(); var generator=new ShipGenerator {RichExpeditions=true}; generator.ConfigureRunContext("dead_fleet","standard");
            foreach(int seed in new[]{17,19,21,23,25,27,29,31})
            {
                var docs=generator.GenerateFromSeed(seed,1,0); Assert.NotNull(docs);
                var rooms=docs.Layout.GetArrayOrEmpty("rooms").Cast<GdDict>().ToArray();
                areas.Add(rooms.Single(r=>r.GetString("room_role")=="cargo").GetArrayOrEmpty("cells").Count);
                var interiors=docs.Layout.GetArrayOrEmpty("purposeful_interiors").Cast<GdDict>().ToArray();
                var occupied=new HashSet<string>();
                foreach(var item in interiors)
                {
                    string cell=CellKey(item["cell"]); Assert.IsTrue(occupied.Add(cell),"one furniture bay per cell");
                    foreach(GdDict portal in docs.Layout.GetArrayOrEmpty("portals"))
                        foreach(string side in new[]{"from_cell","to_cell"}) Assert.AreNotEqual(CellKey(portal.Get(side)),cell,"door approach reserved independent of Variant encoding and optional deck coordinate");
                }
            }
            Assert.Greater(areas.Count,1,"seed variation must change actual geometry rather than only rotate it");
        }

        [TestCase(17)] [TestCase(42)] [TestCase(777)] [TestCase(998)]
        public void VersionedDockUsesARealExteriorCellAndRejectsMissingContract(int seed)
        {
            var generator=new ShipGenerator {RichExpeditions=true}; generator.ConfigureRunContext("dead_fleet","standard");
            var layout=generator.GenerateFromSeed(seed,1,0).Layout;
            var contract=layout.GetDictOrEmpty("docking_port"); var cell=contract.GetArrayOrEmpty("cell");
            var port=SynapticSea.Core.Systems.DockPorts.ForDerelict(layout,seed,0); Assert.IsFalse(port.IsEmpty);
            var position=(Vec3)port["position"]; var facing=(Vec3)port["facing"];
            Assert.AreEqual(V.I64(cell[0])*4,position.X); Assert.AreEqual(V.I64(cell[1])*4,position.Z);
            foreach(GdDict room in layout.GetArrayOrEmpty("rooms")) foreach(object value in room.GetArrayOrEmpty("cells"))
            {
                var floor=LayoutSerializer.ParseSlotCell(value);
                Assert.IsFalse(V.I64(floor[0])==V.I64(cell[0])+facing.X&&V.I64(floor[1])==V.I64(cell[1])+facing.Z,"dock points out of the actual hull");
            }
            contract["facing"]=GdArray.Of(0.0,0.0,0.0);
            Assert.IsTrue(SynapticSea.Core.Systems.DockPorts.ForDerelict(layout).IsEmpty,"v2 malformed direction fails closed");
            contract["facing"]=GdArray.Of(.5,0.0,.5);
            Assert.IsTrue(SynapticSea.Core.Systems.DockPorts.ForDerelict(layout).IsEmpty,"a diagonal cannot align a cardinal dock wall");
            layout.Erase("docking_port"); Assert.IsTrue(SynapticSea.Core.Systems.DockPorts.ForDerelict(layout).IsEmpty,"v2 missing geometry contract fails closed");
            Assert.IsTrue(SynapticSea.Core.Systems.DockPorts.ForDerelict(null).IsEmpty,"legacy null layout remains safe");
        }
        [TestCase(17,1)] [TestCase(42,1)] [TestCase(777,2)] [TestCase(998,2)]
        public void BothFamiliesKeepPhysicalCyclesPurposeAndExactSavedIdentity(int seed, int size)
        {
            var generator = new ShipGenerator { RichExpeditions = true }; generator.ConfigureRunContext("dead_fleet", "standard");
            var docs = generator.GenerateFromSeed(seed, size, 0); Assert.NotNull(docs);
            Assert.AreEqual(PurposefulExpedition.Profile, docs.Layout.GetString("generation_profile"));
            Assert.AreEqual(seed % 2 == 0 ? "service_loop" : "cargo_exchange", docs.Layout.GetString("topology_family"));
            var rooms = docs.Layout.GetArrayOrEmpty("rooms").Cast<GdDict>().ToArray();
            var owners = new HashSet<string>(); var graph = rooms.ToDictionary(r => r.GetString("id"), r => new HashSet<string>());
            foreach (var room in rooms) foreach (var cell in room.GetArrayOrEmpty("cells")) Assert.IsTrue(owners.Add(GdJson.Stringify(cell)));
            foreach (GdDict p in docs.Layout.GetArrayOrEmpty("portals")) { graph[p.GetString("from_room")].Add(p.GetString("to_room")); graph[p.GetString("to_room")].Add(p.GetString("from_room")); }
            var reached = new HashSet<string>(); var pending = new Queue<string>(); pending.Enqueue("dock_01");
            while (pending.Count > 0) { string id = pending.Dequeue(); if (reached.Add(id)) foreach (string next in graph[id]) pending.Enqueue(next); }
            Assert.AreEqual(rooms.Length, reached.Count); Assert.Greater(docs.Layout.GetArrayOrEmpty("portals").Count, rooms.Length);
            Assert.IsTrue(docs.Layout.GetBool("structural_plan_validated"));
            var interiors = docs.Layout.GetArrayOrEmpty("purposeful_interiors").Cast<GdDict>().ToArray(); Assert.Greater(interiors.Length, 10);
            foreach (var item in interiors)
            {
                var room = rooms.Single(r => r.GetString("id") == item.GetString("room_id"));
                var placedCell = (GdArray)item["cell"];
                Assert.IsTrue(room.GetArrayOrEmpty("cells").Select(LayoutSerializer.ParseSlotCell).Any(c =>
                    c.Count >= 2 && V.I64(c[0]) == V.I64(placedCell[0]) && V.I64(c[1]) == V.I64(placedCell[1])), "interior must be on its room's actual floor");
                foreach (string category in new[] { "objectives", "loot_containers" }) foreach (GdDict anchor in docs.GameplaySlice.GetArrayOrEmpty(category))
                    if (anchor.GetString("room_id") == item.GetString("room_id"))
                    {
                        var at = anchor.GetArrayOrEmpty("approach_cell"); var placed = (GdArray)item["cell"];
                        if (at.Count >= 2) Assert.IsFalse(V.I64(at[0]) == V.I64(placed[0]) && V.I64(at[1]) == V.I64(placed[1]), "furniture must clear gameplay approach cells");
                    }
            }
            if (seed % 2 != 0)
                CollectionAssert.IsSubsetOf(new[] { "cargo_pallet", "medical_cabinet", "maintenance_bench", "sleep_berth" }, interiors.Select(i => i.GetString("asset_id")).ToArray());
            var bp = ShipBlueprint.FromDict(GdJson.ParseDict(GdJson.Stringify(new ShipBlueprint(size,0,seed) { GenerationProfile = PurposefulExpedition.Profile }.ToDict())));
            Assert.AreEqual(docs.LayoutJson, generator.Generate(bp).LayoutJson);
            var old = new ShipBlueprint(size,0,seed) { GenerationProfile = ExpeditionLayoutEngine.Profile };
            var legacy = generator.Generate(old); Assert.IsFalse(legacy.Layout.Has("purposeful_interiors")); Assert.IsFalse(legacy.Layout.Has("topology_family"));
            generator.ExpeditionProfile = ExpeditionLayoutEngine.Profile;
            Assert.AreEqual(legacy.LayoutJson, generator.GenerateFromSeed(seed,size,0).LayoutJson, "saved v1 revisit does not upgrade");
        }
    }
}
